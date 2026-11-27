using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.App.TopBar;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.TopBar;

namespace BingLan.App.Windows;

/// <summary>
/// The top bar's inline search: a compact frosted-glass activatable window dropped under the bar.
/// The bar itself is <c>WS_EX_NOACTIVATE</c> and can never own a focused text box, so typing needs
/// a real window. Searches local apps, desktop/FileBox/recent entries, Everything IPC, and Windows
/// Search index first; only when nothing local matches (or an explicit URL is entered) does it
/// offer to open the default browser.
/// </summary>
internal sealed class TopBarSearchWindow : Window
{
    internal const double SurfaceWidth = 520d;
    private const double SurfaceMaxHeight = 440d;
    private const double ChromeHeightReserve = 104d;
    private const double AnchorGap = 8d;
    private const double EdgeMargin = 8d;
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan SnapshotWaitBudget = TimeSpan.FromMilliseconds(350);
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private TopBarSearchLook _look;
    private readonly TopBarSearchEngineInfo _engine;
    private readonly int _anchorX;
    private readonly int _anchorY;
    private readonly MonitorSnapshot? _monitor;
    private readonly bool _everythingEnabled;
    private readonly Func<Task<LauncherPanelSnapshot?>>? _readSnapshot;
    private readonly Action<LauncherPanelEntry>? _openEntry;
    private readonly Border _card;
    private readonly DockGlassSurface _glass;
    private readonly TextBox _input;
    private readonly TextBlock _placeholder;
    private readonly TextBlock _searchGlyph;
    private readonly Border _escHint;
    private readonly TextBlock _escHintText;
    private readonly Border _divider;
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly DispatcherTimer _debounce;
    private readonly List<TopBarSearchRow> _rows = [];
    private readonly Dictionary<TopBarSearchCandidate, LauncherPanelEntry> _entryLookup =
        new(ReferenceEqualityComparer.Instance);

    private Brush _ink = Brushes.Black;
    private Brush _muted = Brushes.Gray;
    private Brush _placeholderBrush = Brushes.Gray;
    private Brush _badgeBrush = Brushes.Transparent;
    private Brush _badgeBorderBrush = Brushes.Transparent;
    private Style? _rowItemStyle;
    private Task<LauncherPanelSnapshot?>? _snapshotTask;
    private EverythingSearchClient? _everything;
    private CancellationTokenSource? _cancellation;
    private string _renderedQuery = string.Empty;
    private int _renderedVersion = -1;
    private string? _inFlightQuery;
    private bool _lastLocalAvailable;
    private int _version;
    private bool _openFirstOnComplete;
    private bool _closing;
    private bool _closed;
    private bool _activated;
    private bool _openingResult;
    private nint _handle;

    internal TopBarSearchWindow(
        TopBarSearchLook look,
        TopBarSearchEngineInfo engine,
        int anchorX,
        int anchorY,
        MonitorSnapshot? monitor,
        bool everythingEnabled,
        Func<Task<LauncherPanelSnapshot?>>? readSnapshot = null,
        Action<LauncherPanelEntry>? openEntry = null)
    {
        _look = look;
        _engine = engine ?? TopBarSearchRowRules.BingEngine;
        _anchorX = anchorX;
        _anchorY = anchorY;
        _monitor = monitor;
        _everythingEnabled = everythingEnabled;
        _readSnapshot = readSnapshot;
        _openEntry = openEntry;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = SurfaceWidth;
        MaxHeight = SurfaceMaxHeight;
        MinHeight = 0d;
        SizeToContent = SizeToContent.Height;

        var initialScale = monitor is { Dpi: > 0 } ? monitor.Dpi / 96d : 1d;
        var initialPlacement = ComputePlacement(initialScale);
        Left = initialPlacement.LeftPx / initialScale;
        Top = initialPlacement.TopPx / initialScale;
        MaxHeight = initialPlacement.MaxHeightDip;

        _input = new TextBox
        {
            Text = string.Empty,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 14.5,
            Padding = new Thickness(0, 2, 0, 2),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(_input, "搜索框");
        _input.TextChanged += OnInputChanged;
        _input.PreviewKeyDown += OnInputKeyDown;

        _placeholder = new TextBlock
        {
            Text = "搜索本机应用、文件，或输入网址…",
            FontSize = 14,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(2, 0, 0, 0),
            Visibility = Visibility.Visible
        };
        _input.TextChanged += (_, _) =>
            _placeholder.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var inputLayer = new Grid();
        inputLayer.Children.Add(_input);
        inputLayer.Children.Add(_placeholder);

        _searchGlyph = new TextBlock
        {
            Text = "\uE721",
            FontFamily = IconFont,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 10, 0)
        };

        _escHintText = new TextBlock
        {
            Text = "Esc",
            FontSize = 10.5,
            FontWeight = FontWeights.Medium
        };
        _escHint = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Child = _escHintText
        };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_searchGlyph, 0);
        Grid.SetColumn(inputLayer, 1);
        Grid.SetColumn(_escHint, 2);
        header.Children.Add(_searchGlyph);
        header.Children.Add(inputLayer);
        header.Children.Add(_escHint);

        _divider = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 10, 0, 6),
            Visibility = Visibility.Collapsed
        };

        _list = new ListBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            MaxHeight = Math.Max(0d, initialPlacement.MaxHeightDip - ChromeHeightReserve),
            Padding = new Thickness(0),
            Visibility = Visibility.Collapsed
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(_list, "搜索结果");
        _list.PreviewMouseLeftButtonUp += OnListMouseUp;

        _status = new TextBlock
        {
            Text = string.Empty,
            FontSize = 11.5,
            Margin = new Thickness(4, 6, 4, 0),
            Visibility = Visibility.Collapsed
        };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_divider);
        stack.Children.Add(_list);
        stack.Children.Add(_status);

        _card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 10, 14, 10),
            Child = stack
        };
        _card.SetResourceReference(Border.BackgroundProperty, "FloatingGlassSurfaceBrush");
        _card.SetResourceReference(Border.BorderBrushProperty, "FloatingGlassBorderBrush");
        Content = _card;

        _glass = new DockGlassSurface(
            this,
            _card,
            "FloatingGlassSurfaceBrush",
            "FloatingGlassBorderBrush",
            "FloatingGlassPanelBrush",
            fullTintOpacity: true);

        if (TryFindResource("CardScrollBarStyle") is Style scrollBarStyle)
        {
            Resources[typeof(ScrollBar)] = scrollBarStyle;
        }

        ApplyTheme();
        AccessibilityThemeManager.HighContrastChanged += OnHighContrastChanged;

        _debounce = new DispatcherTimer(DebounceInterval, DispatcherPriority.Input, (_, _) => OnDebounceElapsed(), Dispatcher);
        Deactivated += (_, _) => CloseUnlessOpeningResult();
        Loaded += (_, _) =>
        {
            StartSnapshotPreload();
            FocusInput();
        };
        Closed += OnClosed;
    }

    internal TopBarSearchLook CurrentLook => _look;

    internal nint WindowHandle => _handle;

    internal bool HasUserInput => !string.IsNullOrWhiteSpace(_input.Text);

    internal bool IsOpeningResult => _openingResult;

    internal void ApplyLook(TopBarSearchLook look)
    {
        ArgumentNullException.ThrowIfNull(look);
        if (_closed || _look == look)
        {
            return;
        }

        _look = look;
        ApplyTheme();
        if (_rows.Count > 0)
        {
            RenderRows(_lastLocalAvailable, _renderedQuery, preserveSelection: true);
        }
    }

    private void OnHighContrastChanged(object? sender, EventArgs e)
    {
        if (!_closed)
        {
            ApplyTheme();
            if (_rows.Count > 0)
            {
                RenderRows(_lastLocalAvailable, _renderedQuery, preserveSelection: true);
            }
        }
    }

    private void ApplyTheme()
    {
        var highContrast = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast;
        Resources["FloatingGlassSurfaceBrush"] = highContrast ? SystemColors.WindowBrush : FrozenBrush(_look.FallbackSurface);
        Resources["FloatingGlassPanelBrush"] = highContrast ? SystemColors.WindowBrush : FrozenBrush(_look.GlassTint);
        Resources["FloatingGlassBorderBrush"] = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(_look.Border);

        _ink = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(_look.Foreground);
        _muted = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(_look.MutedForeground);
        _placeholderBrush = highContrast ? SystemColors.GrayTextBrush : FrozenBrush(_look.PlaceholderForeground);
        _badgeBrush = highContrast ? SystemColors.ControlBrush : FrozenBrush(_look.BadgeBackground);
        _badgeBorderBrush = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(_look.BadgeBorder);
        var dividerBrush = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(_look.Divider);
        var selectionBrush = highContrast ? SystemColors.HighlightBrush : FrozenBrush(_look.SelectionBrush);
        Resources["PrimaryTextBrush"] = _ink;
        Resources["WidgetCaretBrush"] = _ink;
        _rowItemStyle = highContrast
            ? null
            : BuildRowItemStyle(FrozenBrush(_look.HoverRowBackground), FrozenBrush(_look.ActiveRowBackground));

        _input.Foreground = _ink;
        _input.CaretBrush = _ink;
        _input.SelectionBrush = selectionBrush;
        _input.SelectionOpacity = highContrast ? 0.6d : 0.45d;
        _placeholder.Foreground = _placeholderBrush;
        _placeholder.Opacity = 1d;
        _searchGlyph.Foreground = _muted;
        _escHint.Background = _badgeBrush;
        _escHint.BorderBrush = _badgeBorderBrush;
        _escHintText.Foreground = highContrast ? SystemColors.ControlTextBrush : _muted;
        _divider.Background = dividerBrush;
        _list.Foreground = _ink;
        _status.Foreground = _muted;

        System.Windows.Media.Effects.DropShadowEffect? textShadow = null;
        if (_look.UseTextShadow && !highContrast)
        {
            textShadow = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                Opacity = 0.65d,
                BlurRadius = 8d,
                ShadowDepth = 1d,
                Direction = 270d
            };
            textShadow.Freeze();
        }
        _input.Effect = textShadow;
        _placeholder.Effect = textShadow;
        _searchGlyph.Effect = textShadow;
        _escHintText.Effect = textShadow;
        _list.Effect = textShadow;
        _status.Effect = textShadow;

        _glass.SetClearMode(false);
        _glass.SetFrostedGlass(_look.AllowBackdropBlur && !highContrast);
    }

    private void StartSnapshotPreload()
    {
        if (_snapshotTask is null && _readSnapshot is not null)
        {
            _snapshotTask = _readSnapshot();
        }
    }

    /// <summary>Focuses the box once the window is up and asks for the foreground explicitly.</summary>
    private void FocusInput()
    {
        var foregroundAtOpen = DockNativeMethods.GetForegroundWindow();
        var inputTickAtOpen = ShellSurfaceNativeMethods.GetLastInputTick();
        Activate();
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0 && DockNativeMethods.GetForegroundWindow() != handle)
        {
            _ = WindowCommandService.TryActivateOwnWindow(handle);
        }
        if (IsActive || (handle != 0 && DockNativeMethods.GetForegroundWindow() == handle))
        {
            _activated = true;
            _input.Focus();
            Keyboard.Focus(_input);
            return;
        }
        // A foreground change can land between Show and this callback; retry once on
        // ApplicationIdle, and keep the popup visible if Windows still denies foreground
        // (for example when an elevated window holds focus) so the user can click to focus
        // or move away / click outside to dismiss it.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (_closing || _closed)
            {
                return;
            }
            var foreground = DockNativeMethods.GetForegroundWindow();
            if (handle != 0 && !IsActive && foreground != handle
                && WindowActivationRules.CanRetryForegroundRequest(foregroundAtOpen, foreground,
                    inputTickAtOpen, ShellSurfaceNativeMethods.GetLastInputTick()))
            {
                _ = WindowCommandService.TryActivateOwnWindow(handle);
                Activate();
            }
            if (IsActive || (handle != 0 && DockNativeMethods.GetForegroundWindow() == handle))
            {
                _activated = true;
                _input.Focus();
                Keyboard.Focus(_input);
            }
        });
    }

    internal void RequestClose()
    {
        if (_closing || _closed)
        {
            return;
        }
        _closing = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _activated = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            if (_everythingEnabled)
            {
                _everything = new EverythingSearchClient(source);
            }
            if (source.Handle != 0)
            {
                _handle = source.Handle;
                var dpi = VisualTreeHelper.GetDpi(this);
                var targetScale = _monitor is { Dpi: > 0 } ? _monitor.Dpi / 96d : dpi.DpiScaleX;
                var placement = ComputePlacement(targetScale);
                MaxHeight = placement.MaxHeightDip;
                _list.MaxHeight = Math.Max(0d, placement.MaxHeightDip - ChromeHeightReserve);
                NativeMethods.SetWindowPos(
                    source.Handle,
                    DockNativeMethods.HwndTopmost,
                    placement.LeftPx,
                    placement.TopPx,
                    0,
                    0,
                    NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
            }
        }
    }

    private (int LeftPx, int TopPx, double MaxHeightDip) ComputePlacement(double scale)
    {
        var safeScale = double.IsFinite(scale) && scale > 0d ? scale : 1d;
        var cardWidthPx = (int)Math.Round(SurfaceWidth * safeScale);
        var marginPx = (int)Math.Round(EdgeMargin * safeScale);
        var gapPx = (int)Math.Round(AnchorGap * safeScale);

        PixelRect workArea;
        if (_monitor is { } monitor)
        {
            workArea = DockAppBarController.GetLiveWorkingArea(monitor);
            if (workArea.Width <= 0 || workArea.Height <= 0)
            {
                workArea = monitor.WorkingArea.Width > 0 ? monitor.WorkingArea : monitor.Bounds;
            }
        }
        else
        {
            var dipWork = SystemParameters.WorkArea;
            workArea = new PixelRect(
                (int)Math.Round(dipWork.Left * safeScale),
                (int)Math.Round(dipWork.Top * safeScale),
                (int)Math.Round(dipWork.Right * safeScale),
                (int)Math.Round(dipWork.Bottom * safeScale));
        }

        var leftPx = TopBarSearchRowRules.ClampHorizontalPixels(_anchorX, cardWidthPx, workArea.Left, workArea.Right, marginPx);
        var desiredTopPx = _anchorY + gapPx;
        var minVisiblePx = (int)Math.Round((ChromeHeightReserve + 56d) * safeScale);
        var maxTopPx = Math.Max(workArea.Top, workArea.Bottom - minVisiblePx - marginPx);
        var topPx = Math.Clamp(desiredTopPx, workArea.Top, maxTopPx);
        var availableHeightDip = (workArea.Bottom - topPx) / safeScale - EdgeMargin;
        var maxHeightDip = double.IsFinite(availableHeightDip) && availableHeightDip > 0d
            ? Math.Min(SurfaceMaxHeight, availableHeightDip)
            : Math.Min(SurfaceMaxHeight, Math.Max(1d, (workArea.Bottom - workArea.Top) / safeScale));
        return (leftPx, topPx, maxHeightDip);
    }

    private void OnInputChanged(object? sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _openFirstOnComplete = false;
        CancelSearch();
        _version++;

        var query = _input.Text.Trim();
        if (query.Length == 0)
        {
            _renderedQuery = string.Empty;
            _rows.Clear();
            _list.Items.Clear();
            _list.Visibility = Visibility.Collapsed;
            _divider.Visibility = Visibility.Collapsed;
            SetStatus(null);
            return;
        }

        _divider.Visibility = Visibility.Visible;
        if (!string.Equals(_renderedQuery, query, StringComparison.Ordinal))
        {
            _list.SelectedIndex = -1;
        }
        SetStatus(TopBarSearchRowRules.HasExplicitHttpScheme(query)
            && TopBarSearchRowRules.TryNormalizeDirectUrl(query, out _, out var host)
            ? $"按 Enter 直接打开 {host}"
            : "正在检索本机应用与文件…");
        _debounce.Start();
    }

    private void OnDebounceElapsed()
    {
        _debounce.Stop();
        _ = SearchAsync(openFirstOnComplete: false);
    }

    private async Task SearchAsync(bool openFirstOnComplete)
    {
        var query = _input.Text.Trim();
        if (query.Length == 0 || _closed)
        {
            return;
        }

        CancelSearch();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _inFlightQuery = query;
        var token = cancellation.Token;
        var version = ++_version;
        _openFirstOnComplete = openFirstOnComplete;
        _divider.Visibility = Visibility.Visible;
        if (_rows.Count == 0)
        {
            SetStatus("正在检索本机应用与文件…");
        }

        var catalogCandidates = new List<TopBarSearchCandidate>();
        var catalogAvailable = false;
        var catalogTimedOut = false;

        try
        {
            // Phase 1: Cached local catalog (Start Menu apps, Dock pins, Desktop, FileBox, QuickPlaces, Recent),
            // bounded so a slow redirected Desktop never blocks Everything or Windows Search.
            StartSnapshotPreload();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_snapshotTask is { } snapshotTask)
            {
                LauncherPanelSnapshot? snapshot = null;
                try
                {
                    snapshot = snapshotTask.IsCompleted
                        ? await snapshotTask
                        : await snapshotTask.WaitAsync(SnapshotWaitBudget, token);
                }
                catch (TimeoutException)
                {
                    catalogTimedOut = true;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                }

                if (_closed || token.IsCancellationRequested || version != _version)
                {
                    return;
                }

                if (snapshot is not null)
                {
                    catalogAvailable = true;
                    _entryLookup.Clear();
                    foreach (var entry in snapshot.Favorites
                        .Concat(snapshot.Entries)
                        .Concat(snapshot.RecentEntries ?? []))
                    {
                        var path = entry.Candidate.Path;
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            continue;
                        }
                        if (seenKeys.Add(entry.Key))
                        {
                            catalogCandidates.Add(entry.Candidate);
                            _entryLookup[entry.Candidate] = entry;
                            seenPaths.Add(path);
                            if (!string.IsNullOrWhiteSpace(entry.IconSource))
                            {
                                seenPaths.Add(entry.IconSource);
                            }
                        }
                    }
                }
            }

            if (catalogCandidates.Count > 0)
            {
                var fastLocal = TopBarSearchRules.Search(catalogCandidates, query, TopBarSearchRules.MaxResults);
                if (fastLocal.Count > 0)
                {
                    _renderedQuery = query;
                    _renderedVersion = version;
                    _rows.Clear();
                    _rows.AddRange(TopBarSearchRowRules.Compose(fastLocal, query, _engine));
                    RenderRows(localAvailable: true, query, preserveSelection: false);
                }
            }

            // Phase 2: Everything IPC and/or Windows Search index for files across disk.
            var indexAvailable = false;
            var indexedCandidates = new List<TopBarSearchCandidate>();

            if (_everything is not null)
            {
                var everything = await _everything.SearchAsync(query, token);
                if (_closed || token.IsCancellationRequested || version != _version)
                {
                    return;
                }
                if (everything.Available)
                {
                    indexAvailable = true;
                    indexedCandidates.AddRange(everything.Candidates);
                }
            }

            if (!indexAvailable)
            {
                var indexed = await WindowsIndexSearchClient.SearchAsync(query, token);
                if (_closed || token.IsCancellationRequested || version != _version)
                {
                    return;
                }
                if (indexed.Available)
                {
                    indexAvailable = true;
                    indexedCandidates.AddRange(indexed.Candidates);
                }
            }

            var merged = new List<TopBarSearchCandidate>(catalogCandidates.Count + indexedCandidates.Count);
            merged.AddRange(catalogCandidates);
            foreach (var candidate in indexedCandidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate.Path) && seenPaths.Add(candidate.Path))
                {
                    merged.Add(candidate);
                }
            }

            IReadOnlyList<TopBarSearchCandidate> local = merged.Count > 0
                ? TopBarSearchRules.Search(merged, query, TopBarSearchRules.MaxResults)
                : [];
            var sameQueryRefresh = _renderedVersion == version && string.Equals(_renderedQuery, query, StringComparison.Ordinal);
            var previouslySelected = sameQueryRefresh && _list.SelectedIndex >= 0 && _list.SelectedIndex < _rows.Count
                ? _rows[_list.SelectedIndex]
                : null;
            _renderedQuery = query;
            _renderedVersion = version;
            _rows.Clear();
            _rows.AddRange(TopBarSearchRowRules.Compose(local, query, _engine));
            RenderRows(catalogAvailable || indexAvailable, query, preserveSelection: sameQueryRefresh, previouslySelected);
            if (_openFirstOnComplete && version == _version && _rows.Count > 0)
            {
                _openFirstOnComplete = false;
                if (catalogTimedOut && local.Count == 0)
                {
                    SetStatus($"本机应用列表仍在加载 · 再按 Enter 用{_engine.DisplayName}搜索");
                }
                else
                {
                    OpenIndex(_list.SelectedIndex >= 0 ? _list.SelectedIndex : 0);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            if (!_closed && version == _version)
            {
                IReadOnlyList<TopBarSearchCandidate> fallbackLocal = catalogCandidates.Count > 0
                    ? TopBarSearchRules.Search(catalogCandidates, query, TopBarSearchRules.MaxResults)
                    : [];
                _renderedQuery = query;
                _renderedVersion = version;
                _rows.Clear();
                _rows.AddRange(TopBarSearchRowRules.Compose(fallbackLocal, query, _engine));
                RenderRows(localAvailable: catalogAvailable, query, preserveSelection: false);
                if (_openFirstOnComplete && _rows.Count > 0 && (!catalogTimedOut || fallbackLocal.Count > 0))
                {
                    _openFirstOnComplete = false;
                    OpenIndex(0);
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
                _inFlightQuery = null;
                cancellation.Dispose();
            }
        }
    }

    private void RenderRows(
        bool localAvailable,
        string query,
        bool preserveSelection,
        TopBarSearchRow? previouslySelected = null)
    {
        _lastLocalAvailable = localAvailable;
        var currentSelected = preserveSelection
            ? previouslySelected ?? (_list.SelectedIndex >= 0 && _list.SelectedIndex < _rows.Count ? _rows[_list.SelectedIndex] : null)
            : null;

        _list.Items.Clear();
        foreach (var row in _rows)
        {
            _list.Items.Add(BuildRow(row));
        }

        var hasRows = _rows.Count > 0;
        _divider.Visibility = hasRows || query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;
        var nextIndex = -1;
        if (hasRows)
        {
            nextIndex = 0;
            if (currentSelected is not null)
            {
                var foundIndex = currentSelected.Local is not null
                    ? _rows.FindIndex(row => ReferenceEquals(row.Local, currentSelected.Local))
                    : -1;
                if (foundIndex < 0)
                {
                    foundIndex = _rows.FindIndex(row =>
                        row.Kind == currentSelected.Kind
                        && string.Equals(row.Title, currentSelected.Title, StringComparison.Ordinal)
                        && string.Equals(row.Local?.Path, currentSelected.Local?.Path, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(row.BrowserUrl, currentSelected.BrowserUrl, StringComparison.Ordinal));
                }
                if (foundIndex >= 0)
                {
                    nextIndex = foundIndex;
                }
            }
        }
        _list.SelectedIndex = nextIndex;

        if (!hasRows)
        {
            SetStatus(localAvailable ? "未找到匹配项" : "未找到匹配项，本机搜索暂不可用");
            return;
        }

        var localCount = _rows.Count(row => row.Kind == TopBarSearchRowKind.Local);
        if (localCount > 0)
        {
            SetStatus($"{localCount} 个本机结果 · ↑↓ 选择 · Enter 打开");
            return;
        }

        if (TopBarSearchRowRules.TryNormalizeDirectUrl(query, out _, out _))
        {
            SetStatus("Enter 在默认浏览器中打开网址");
            return;
        }

        SetStatus(localAvailable
            ? $"本机无匹配 · Enter 用{_engine.DisplayName}搜索"
            : $"本机索引暂不可用 · Enter 用{_engine.DisplayName}搜索");
    }

    private ListBoxItem BuildRow(TopBarSearchRow row)
    {
        var highContrast = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast;

        var glyphText = row.Kind == TopBarSearchRowKind.Browser
            ? "\uE774"
            : row.Local?.Kind switch
            {
                TopBarSearchItemKind.Folder => "\uE8B7",
                TopBarSearchItemKind.App => "\uE737",
                _ => "\uE8A5"
            };

        var iconBadge = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(7),
            Background = _badgeBrush,
            BorderBrush = _badgeBorderBrush,
            BorderThickness = new Thickness(highContrast ? 1 : 0),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock
            {
                Text = glyphText,
                FontFamily = IconFont,
                FontSize = 13.5,
                Foreground = highContrast ? SystemColors.ControlTextBrush : _ink,
                Opacity = highContrast ? 1.0 : 0.88,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var subtitleText = row.Subtitle;
        if (row.Kind == TopBarSearchRowKind.Local
            && row.Local is { } candidate
            && _entryLookup.TryGetValue(candidate, out var entry)
            && !string.IsNullOrWhiteSpace(entry.Source)
            && candidate.Kind == TopBarSearchItemKind.App)
        {
            subtitleText = $"{entry.Source} · {subtitleText}";
        }

        var name = new TextBlock
        {
            Text = row.Title,
            Foreground = _ink,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var location = new TextBlock
        {
            Text = subtitleText,
            Foreground = _muted,
            FontSize = 11,
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        text.Children.Add(name);
        text.Children.Add(location);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(iconBadge, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(iconBadge);
        grid.Children.Add(text);

        var item = new ListBoxItem
        {
            Content = grid,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 1, 0, 1),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Focusable = false
        };
        if (_rowItemStyle is not null)
        {
            item.Style = _rowItemStyle;
        }
        return item;
    }

    private static Style BuildRowItemStyle(Brush hoverBrush, Brush activeBrush)
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));

        var template = new ControlTemplate(typeof(ListBoxItem));
        var borderFactory = new FrameworkElementFactory(typeof(Border), "Bd");
        borderFactory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        borderFactory.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        borderFactory.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var presenterFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        presenterFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty,
            new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        presenterFactory.SetValue(FrameworkElement.VerticalAlignmentProperty,
            new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        borderFactory.AppendChild(presenterFactory);
        template.VisualTree = borderFactory;

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hoverBrush, "Bd"));
        template.Triggers.Add(hoverTrigger);

        var selectedTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, activeBrush, "Bd"));
        template.Triggers.Add(selectedTrigger);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Seal();
        return style;
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                RequestClose();
                break;
            case Key.Down:
                e.Handled = true;
                MoveSelection(1);
                break;
            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                break;
            case Key.Enter:
                e.Handled = true;
                OpenSelected();
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_rows.Count == 0)
        {
            return;
        }
        var index = _list.SelectedIndex < 0
            ? (delta > 0 ? 0 : _rows.Count - 1)
            : Math.Clamp(_list.SelectedIndex + delta, 0, _rows.Count - 1);
        _list.SelectedIndex = index;
        _list.ScrollIntoView(_list.Items[index]);
    }

    private void OpenSelected()
    {
        var currentQuery = _input.Text.Trim();
        if (currentQuery.Length == 0)
        {
            return;
        }

        if (_rows.Count > 0
            && _renderedVersion == _version
            && string.Equals(_renderedQuery, currentQuery, StringComparison.Ordinal))
        {
            OpenIndex(_list.SelectedIndex >= 0 ? _list.SelectedIndex : 0);
            return;
        }

        // Explicit http(s):// URLs cannot match Windows filenames, so they can open immediately.
        // Bare domains (e.g. logo.ai) still wait for local lookup so Enter stays timing-consistent.
        if (TopBarSearchRowRules.HasExplicitHttpScheme(currentQuery)
            && TopBarSearchRowRules.TryNormalizeDirectUrl(currentQuery, out _, out _))
        {
            var immediate = TopBarSearchRowRules.Compose([], currentQuery, _engine);
            if (immediate.Count > 0)
            {
                OpenRow(immediate[0]);
            }
            return;
        }

        // For non-URL queries, finish the local lookup first so pressing Enter quickly
        // never leaks a local filename to a web search provider before local matches return.
        if (_cancellation is not null && string.Equals(_inFlightQuery, currentQuery, StringComparison.Ordinal))
        {
            _debounce.Stop();
            _openFirstOnComplete = true;
            return;
        }

        _debounce.Stop();
        _ = SearchAsync(openFirstOnComplete: true);
    }

    /// <summary>
    /// Opens the row under the pointer. The container is hit-tested instead of reading
    /// <c>SelectedIndex</c> because the ListBox applies its own selection after this
    /// event, so the index would still point at the previously selected row.
    /// </summary>
    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        var currentQuery = _input.Text.Trim();
        if (_renderedVersion != _version || !string.Equals(_renderedQuery, currentQuery, StringComparison.Ordinal))
        {
            return;
        }
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(_list, source) is not ListBoxItem item)
        {
            return;
        }
        var index = _list.ItemContainerGenerator.IndexFromContainer(item);
        _list.SelectedIndex = index;
        OpenIndex(index);
    }

    private void OpenIndex(int index)
    {
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        OpenRow(_rows[index]);
    }

    private void OpenRow(TopBarSearchRow row)
    {
        if (row.Kind == TopBarSearchRowKind.Local
            && row.Local is { } candidate
            && _openEntry is not null
            && _entryLookup.TryGetValue(candidate, out var entry))
        {
            _openingResult = true;
            RequestClose();
            _openEntry(entry);
            return;
        }

        System.Diagnostics.ProcessStartInfo? start = null;
        if (row.BrowserUrl is { Length: > 0 } url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                start = new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true };
            }
        }
        else if (row.Local?.Path is { Length: > 0 } path)
        {
            start = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
        }
        if (start is null)
        {
            SetStatus("无法打开该链接，请检查搜索引擎网址设置");
            return;
        }
        _openingResult = true;
        RequestClose();
        _ = ShellLauncher.StartAsync(start);
    }

    /// <summary>
    /// Losing activation closes the popup, but opening a result deactivates us on purpose
    /// and must not race the close that follows. Deferring out of WM_ACTIVATE also prevents
    /// re-entering Close() while the window is already being torn down.
    /// </summary>
    private void CloseUnlessOpeningResult()
    {
        if (_closing || _closed || _openingResult || !_activated)
        {
            return;
        }
        _closing = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (!_closed)
            {
                Close();
            }
        });
    }

    private void CancelSearch()
    {
        _inFlightQuery = null;
        var cancellation = _cancellation;
        _cancellation = null;
        if (cancellation is null)
        {
            return;
        }
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        cancellation.Dispose();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        AccessibilityThemeManager.HighContrastChanged -= OnHighContrastChanged;
        _debounce.Stop();
        CancelSearch();
        _everything?.Dispose();
        _everything = null;
        _glass.Dispose();
    }

    private void SetStatus(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            _status.Text = string.Empty;
            _status.Visibility = Visibility.Collapsed;
            return;
        }

        _status.Text = text;
        _status.Visibility = Visibility.Visible;
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Resolved frosted-glass palette for the top bar's inline search popup, aligned with the
/// top bar's own surface, adaptive wallpaper ink, and Windows 11 Composition blur.
/// </summary>
internal sealed record TopBarSearchLook(
    bool UseDarkInk,
    bool AllowBackdropBlur,
    bool UseTextShadow,
    Color GlassTint,
    Color FallbackSurface,
    Color Border,
    Color Foreground,
    Color MutedForeground,
    Color PlaceholderForeground,
    Color Divider,
    Color BadgeBackground,
    Color BadgeBorder,
    Color ActiveRowBackground,
    Color HoverRowBackground,
    Color SelectionBrush)
{
    internal static TopBarSearchLook Resolve(
        DesktopStyleState? style,
        LauncherAppearanceState? launcherAppearance = null,
        TopBarState? topBar = null,
        bool? useClearGlass = null) =>
        FromPalette(TopBarSearchLookRules.Resolve(style, launcherAppearance, topBar, useClearGlass));

    internal static TopBarSearchLook FromPalette(TopBarSearchPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        return new TopBarSearchLook(
            UseDarkInk: palette.UseDarkInk,
            AllowBackdropBlur: palette.AllowBackdropBlur,
            UseTextShadow: palette.UseTextShadow,
            GlassTint: Parse(palette.GlassTint, Color.FromArgb(0xD2, 0xEC, 0xF6, 0xFF)),
            FallbackSurface: Parse(palette.FallbackSurface, Color.FromArgb(0xEE, 0xEC, 0xF6, 0xFF)),
            Border: Parse(palette.Border, Color.FromArgb(0x36, 0x12, 0x2C, 0x44)),
            Foreground: Parse(palette.Foreground, Color.FromRgb(0x12, 0x2C, 0x44)),
            MutedForeground: Parse(palette.MutedForeground, Color.FromRgb(0x35, 0x51, 0x6B)),
            PlaceholderForeground: Parse(palette.PlaceholderForeground, Color.FromRgb(0x3E, 0x59, 0x73)),
            Divider: Parse(palette.Divider, Color.FromArgb(0x22, 0x12, 0x2C, 0x44)),
            BadgeBackground: Parse(palette.BadgeBackground, Color.FromArgb(0x14, 0x12, 0x2C, 0x44)),
            BadgeBorder: Parse(palette.BadgeBorder, Color.FromArgb(0x24, 0x12, 0x2C, 0x44)),
            ActiveRowBackground: Parse(palette.ActiveRowBackground, Color.FromArgb(0x82, 0xB9, 0xE1, 0xFA)),
            HoverRowBackground: Parse(palette.HoverRowBackground, Color.FromArgb(0x14, 0x12, 0x2C, 0x44)),
            SelectionBrush: Parse(palette.SelectionBrush, Color.FromArgb(0x5C, 0x1E, 0xA9, 0xFF)));
    }

    private static Color Parse(string text, Color fallback) =>
        ColorConverter.ConvertFromString(text) is Color color ? color : fallback;
}
