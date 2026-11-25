using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
/// The top bar's inline search: a small activatable window dropped under the bar.
/// The bar itself is <c>WS_EX_NOACTIVATE</c> and can never own a focused text box, so
/// typing needs a real window. Queries are debounced, the previous one is cancelled and
/// results come from the running Everything instance over its IPC (no index of our own).
/// Closes on Escape, on losing activation and after opening a result.
/// </summary>
internal sealed class TopBarSearchWindow : Window
{
    internal const double SurfaceWidth = 560d;
    private const double SurfaceMaxHeight = 420d;
    private const double SurfaceMinHeight = 96d;
    private const double AnchorGap = 6d;
    private const double EdgeMargin = 8d;
    private const string IdleStatus = "输入关键词搜索本机文件，无匹配时按 Enter 用浏览器搜索";
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(160);

    private readonly Brush _ink;
    private readonly Brush _muted;
    private readonly Brush _surface;
    private readonly Brush _border;
    private readonly int _anchorX;
    private readonly int _anchorY;
    private readonly MonitorSnapshot? _monitor;
    private readonly bool _everythingEnabled;
    private readonly TextBox _input;
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly DispatcherTimer _debounce;
    private readonly List<TopBarSearchRow> _rows = [];

    private EverythingSearchClient? _everything;
    private CancellationTokenSource? _cancellation;
    private int _version;
    private bool _closing;
    private bool _closed;
    private bool _activated;
    private bool _openingResult;

    internal TopBarSearchWindow(
        TopBarSearchLook look,
        int anchorX,
        int anchorY,
        MonitorSnapshot? monitor,
        bool everythingEnabled)
    {
        _ink = FrozenBrush(look.Foreground);
        _muted = FrozenBrush(look.MutedForeground);
        _surface = FrozenBrush(look.Background);
        _border = FrozenBrush(look.Border);
        _anchorX = anchorX;
        _anchorY = anchorY;
        _monitor = monitor;
        _everythingEnabled = everythingEnabled;

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
        MinHeight = SurfaceMinHeight;
        SizeToContent = SizeToContent.Height;

        var initialScale = monitor is { Dpi: > 0 } ? monitor.Dpi / 96d : 1d;
        var (initialLeftPx, initialTopPx) = ComputePositionPixels(initialScale);
        Left = initialLeftPx / initialScale;
        Top = initialTopPx / initialScale;

        var highContrast = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast;
        var ink = highContrast ? SystemColors.WindowTextBrush : _ink;
        var muted = highContrast ? SystemColors.WindowTextBrush : _muted;

        _input = new TextBox
        {
            Text = string.Empty,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = ink,
            CaretBrush = ink,
            FontSize = 15,
            Padding = new Thickness(0, 2, 0, 2),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(_input, "搜索框");
        _input.TextChanged += OnInputChanged;
        _input.PreviewKeyDown += OnInputKeyDown;

        var placeholder = new TextBlock
        {
            Text = "搜索本机文件…",
            Foreground = muted,
            Opacity = 0.55,
            FontSize = 15,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
            Visibility = Visibility.Visible
        };
        _input.TextChanged += (_, _) =>
            placeholder.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var inputLayer = new Grid();
        inputLayer.Children.Add(_input);
        inputLayer.Children.Add(placeholder);

        var glyph = new TextBlock
        {
            Text = "\uE721",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = muted,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 1)
        };

        var header = new Grid { Margin = new Thickness(2, 0, 2, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(glyph, 0);
        Grid.SetColumn(inputLayer, 1);
        header.Children.Add(glyph);
        header.Children.Add(inputLayer);

        _list = new ListBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = ink,
            MaxHeight = 300,
            Visibility = Visibility.Collapsed
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(_list, "搜索结果");
        _list.PreviewMouseLeftButtonUp += OnListMouseUp;

        _status = new TextBlock
        {
            Text = string.Empty,
            Foreground = muted,
            Opacity = 0.66,
            FontSize = 12,
            Margin = new Thickness(2, 4, 2, 0)
        };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_list);
        stack.Children.Add(_status);

        var shadow = new DropShadowEffect
        {
            BlurRadius = 22,
            ShadowDepth = 4,
            Direction = 270,
            Opacity = 0.32,
            Color = Colors.Black
        };
        shadow.Freeze();

        var card = new Border
        {
            Background = highContrast ? SystemColors.WindowBrush : _surface,
            BorderBrush = highContrast ? SystemColors.WindowTextBrush : _border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 10),
            Child = stack,
            Effect = shadow
        };
        Content = card;

        _debounce = new DispatcherTimer(DebounceInterval, DispatcherPriority.Input, (_, _) => OnDebounceElapsed(), Dispatcher);
        Deactivated += (_, _) => CloseUnlessOpeningResult();
        Loaded += (_, _) => FocusInput();
        Closed += OnClosed;
    }

    /// <summary>Focuses the box once the window is up and asks for the foreground explicitly.</summary>
    private void FocusInput()
    {
        Activate();
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
        {
            DockNativeMethods.SetForegroundWindow(handle);
        }
        if (IsActive)
        {
            _input.Focus();
            Keyboard.Focus(_input);
            return;
        }
        // A foreground change can land between Show and this callback; one more attempt
        // covers it, and giving up closes the panel instead of leaving an unfocusable
        // window on screen where Escape could not reach it.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (_closing || _closed)
            {
                return;
            }
            if (IsActive || (handle != 0 && DockNativeMethods.SetForegroundWindow(handle)))
            {
                _input.Focus();
                Keyboard.Focus(_input);
                return;
            }
            RequestClose();
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
                var dpi = VisualTreeHelper.GetDpi(this);
                var targetScale = _monitor is { Dpi: > 0 } ? _monitor.Dpi / 96d : dpi.DpiScaleX;
                var (leftPx, topPx) = ComputePositionPixels(targetScale);
                NativeMethods.SetWindowPos(
                    source.Handle,
                    DockNativeMethods.HwndTopmost,
                    leftPx,
                    topPx,
                    0,
                    0,
                    NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
            }
        }
        // No accent material here on purpose: the accented blur needs an opaque
        // redirection surface and renders as a flat grey block on this transparent
        // layered window, so the panel uses the bar's resolved flat colour instead.
        SetStatus(IdleStatus);
    }

    private (int Left, int Top) ComputePositionPixels(double scale)
    {
        var safeScale = double.IsFinite(scale) && scale > 0d ? scale : 1d;
        var widthPx = (int)Math.Round(SurfaceWidth * safeScale);
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

        var leftPx = TopBarSearchRowRules.ClampHorizontalPixels(_anchorX, widthPx, workArea.Left, workArea.Right, marginPx);
        var topPx = _anchorY + gapPx;
        return (leftPx, topPx);
    }

    private void OnInputChanged(object? sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        if (_input.Text.Trim().Length == 0)
        {
            CancelSearch();
            _rows.Clear();
            _list.Items.Clear();
            _list.Visibility = Visibility.Collapsed;
            SetStatus(IdleStatus);
            return;
        }
        _debounce.Start();
    }

    private void OnDebounceElapsed()
    {
        _debounce.Stop();
        _ = SearchAsync();
    }

    private async Task SearchAsync()
    {
        var query = _input.Text.Trim();
        if (query.Length == 0 || _closed)
        {
            return;
        }

        CancelSearch();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var token = cancellation.Token;
        var version = ++_version;
        SetStatus("正在搜索…");

        try
        {
            var localAvailable = false;
            IReadOnlyList<TopBarSearchCandidate> rawCandidates = [];

            if (_everything is not null)
            {
                var everything = await _everything.SearchAsync(query, token);
                if (_closed || token.IsCancellationRequested || version != _version)
                {
                    return;
                }
                if (everything.Available)
                {
                    localAvailable = true;
                    rawCandidates = everything.Candidates;
                }
            }

            if (!localAvailable)
            {
                var indexed = await WindowsIndexSearchClient.SearchAsync(query, token);
                if (_closed || token.IsCancellationRequested || version != _version)
                {
                    return;
                }
                if (indexed.Available)
                {
                    localAvailable = true;
                    rawCandidates = indexed.Candidates;
                }
            }

            IReadOnlyList<TopBarSearchCandidate> local = localAvailable
                ? TopBarSearchRules.Search(rawCandidates, query, TopBarSearchRules.MaxResults)
                : [];
            _rows.Clear();
            _rows.AddRange(TopBarSearchRowRules.Compose(local, query));
            RenderRows(localAvailable);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            if (!_closed && version == _version)
            {
                SetStatus("搜索暂不可用");
            }
        }
    }

    private void RenderRows(bool localAvailable)
    {
        _list.Items.Clear();
        foreach (var row in _rows)
        {
            _list.Items.Add(BuildRow(row));
        }
        _list.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _list.SelectedIndex = _rows.Count == 0 ? -1 : 0;
        if (_rows.Count == 0)
        {
            SetStatus(localAvailable ? "未找到匹配项" : "未找到匹配项，本机搜索暂不可用");
            return;
        }
        var localCount = _rows.Count(row => row.Kind == TopBarSearchRowKind.Local);
        SetStatus(localCount == 0
            ? (localAvailable
                ? "本机无匹配结果 · Enter 用浏览器搜索"
                : "本机索引暂不可用 · Enter 用浏览器搜索")
            : $"{localCount} 个本机结果 · ↑↓ 选择，Enter 打开");
    }

    private ListBoxItem BuildRow(TopBarSearchRow row)
    {
        var highContrast = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast;
        var ink = highContrast ? SystemColors.WindowTextBrush : _ink;
        var muted = highContrast ? SystemColors.WindowTextBrush : _muted;

        var name = new TextBlock
        {
            Text = row.Title,
            Foreground = ink,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var location = new TextBlock
        {
            Text = row.Subtitle,
            Foreground = muted,
            Opacity = 0.6,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var text = new StackPanel { Margin = new Thickness(2, 2, 2, 2) };
        text.Children.Add(name);
        text.Children.Add(location);

        FrameworkElement content = text;
        if (row.Kind == TopBarSearchRowKind.Browser)
        {
            var glyph = new TextBlock
            {
                Text = "\uE774",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = muted,
                Opacity = 0.75,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(glyph, 0);
            Grid.SetColumn(text, 1);
            grid.Children.Add(glyph);
            grid.Children.Add(text);
            content = grid;
        }

        return new ListBoxItem
        {
            Content = content,
            Padding = new Thickness(8, 5, 8, 5),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Focusable = false
        };
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

    private void OpenSelected() => OpenIndex(_list.SelectedIndex);

    /// <summary>
    /// Opens the row under the pointer. The container is hit-tested instead of reading
    /// <c>SelectedIndex</c> because the ListBox applies its own selection after this
    /// event, so the index would still point at the previously selected row.
    /// </summary>
    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
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

        var row = _rows[index];
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
        _openingResult = true;
        RequestClose();
        if (start is not null)
        {
            _ = ShellLauncher.StartAsync(start);
        }
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
        _debounce.Stop();
        CancelSearch();
        _everything?.Dispose();
        _everything = null;
    }

    private void SetStatus(string text) => _status.Text = text;

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// The bar's resolved surface, handed to the popup so both read as the same material.
/// The bar can ride a nearly transparent acrylic tint; a floating panel needs more body
/// to stay readable, so the alpha is lifted while the hue stays the bar's own.
/// </summary>
internal sealed record TopBarSearchLook(
    Color Background,
    Color Border,
    Color Foreground,
    Color MutedForeground)
{
    private const byte MinimumPanelAlpha = 0xA6;
    private const byte MaximumPanelAlpha = 0xB3;

    internal static TopBarSearchLook FromBar(Color surface, TopBarAdaptivePalette palette)
    {
        // The bar resolves its own colour and opacity from the appearance settings; the
        // panel keeps that hue but pins the alpha into a narrow window. Opaque enough for
        // the text to stay readable over any window, sheer enough to read as glass without
        // the accent blur a transparent layered window cannot host.
        var alpha = Math.Clamp(surface.A, MinimumPanelAlpha, MaximumPanelAlpha);
        var body = Color.FromArgb(alpha, surface.R, surface.G, surface.B);
        return new TopBarSearchLook(
            body,
            Parse(palette.BarBorderColor),
            Parse(palette.Foreground),
            Parse(palette.MutedForeground));
    }

    private static Color Parse(string text) =>
        ColorConverter.ConvertFromString(text) is Color color ? color : Colors.White;
}
