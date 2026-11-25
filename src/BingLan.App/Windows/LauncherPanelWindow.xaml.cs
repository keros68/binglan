using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.App.TopBar;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.TopBar;

namespace BingLan.App.Windows;

internal sealed record LauncherPanelEntry(
    TopBarSearchCandidate Candidate,
    string Source,
    DockPinnedApp? Pinned = null,
    bool IsSavedFallback = false,
    string? StartMenuFolder = null,
    DateTimeOffset? LastAccessedLocal = null,
    string? IconSource = null,
    long? FileSizeBytes = null)
{
    internal string Key
    {
        get
        {
            var target = Pinned?.AppUserModelId is { Length: > 0 } id
                ? "aumid:" + id : Pinned?.TargetPath ?? Pinned?.ExecutablePath ?? Candidate.Path;
            return DockShortcuts.LaunchVariantKey(target, Pinned?.Arguments, Pinned?.WorkingDirectory);
        }
    }
}

internal sealed record LauncherPanelSnapshot(
    IReadOnlyList<LauncherPanelEntry> Entries,
    IReadOnlyList<LauncherPanelEntry> Favorites,
    bool EverythingEnabled,
    IReadOnlyList<LauncherPlace>? Places = null,
    IReadOnlyList<LauncherPanelEntry>? RecentEntries = null);

public partial class LauncherPanelWindow : Window
{
    // Native file checks cannot be cancelled. Limit the outstanding checks across
    // repeated panel sessions; consider a dedicated provider only if this proves insufficient.
    private static readonly SemaphoreSlim PathCheckGate = new(2, 2);
    private static readonly SemaphoreSlim IconGate = new(4, 4);
    private readonly Func<Task<LauncherPanelSnapshot>> _loadSnapshot;
    private readonly Action<LauncherPanelEntry, bool> _openTarget;
    private readonly Func<IReadOnlyList<(Guid Id, string Title)>> _readFileBoxes;
    private readonly Func<Guid, string, Task<string>> _sendToFileBox;
    private readonly Func<LauncherPanelEntry, string>? _pinToDock;
    private readonly Func<LauncherPanelEntry, string>? _unpinFromDock;
    private readonly Func<IList<LauncherGroupState>> _readGroups;
    private readonly Func<IDictionary<string, LauncherAppCategory>>? _readCategoryOverrides;
    private readonly Action? _saveOrganizer;
    private readonly MonitorSnapshot _monitor;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _debounce;
    private readonly DockGlassSurface _glass;
    private readonly LauncherAppearanceState _appearance;
    private readonly LauncherPreferencesState _preferences;
    private readonly DesktopStyleState _style;
    private CancellationTokenSource? _searchCancellation;
    private Task _searchRowsReady = Task.CompletedTask;
    private LauncherPanelSnapshot? _snapshot;
    private EverythingSearchClient? _everything;
    private ContextMenu? _menu;
    private List<LauncherPanelEntry> _supplemental = [];
    private List<PanelRow> _unfilteredRows = [];
    private List<PanelRow> _rows = [];
    private LauncherMainTab _activeTab;
    private string? _category;
    private nint _handle;
    private int _searchVersion;
    private int _renderedSearchVersion = -1;
    private DispatcherOperation? _deactivateOperation;
    private bool _closed;
    private bool _dismissing;
    private bool _composing;
    private bool _openingTarget;
    private bool _pinningTarget;
    private bool _isPinned;
    private bool _modalDialogOpen;
    private bool _draggingOut;
    private bool _suppressNextClick;
    private System.Windows.Point? _dragStartPoint;
    private PanelRow? _dragRow;
    private PixelRect? _panelPixelBounds;
    private MonitorSnapshot? _activeMonitor;
    private bool _hasWallpaperBackdrop;
    private bool _queryWasEmpty = true;
    private PanelFilter _filter = PanelFilter.Apps;
    private PanelFilter _browseFilter = PanelFilter.Apps;

    internal LauncherPanelWindow(
        Func<Task<LauncherPanelSnapshot>> loadSnapshot,
        Action<LauncherPanelEntry, bool> openTarget,
        Func<IReadOnlyList<(Guid Id, string Title)>> readFileBoxes,
        Func<Guid, string, Task<string>> sendToFileBox,
        nint previousForeground,
        MonitorSnapshot monitor,
        LauncherAppearanceState? appearance = null,
        DesktopStyleState? style = null,
        Func<LauncherPanelEntry, string>? pinToDock = null,
        Func<IList<LauncherGroupState>>? readGroups = null,
        Action? saveOrganizer = null,
        Func<IDictionary<string, LauncherAppCategory>>? readCategoryOverrides = null,
        LauncherPreferencesState? preferences = null,
        Func<LauncherPanelEntry, string>? unpinFromDock = null,
        LauncherMainTab? initialTab = null)
    {
        _loadSnapshot = loadSnapshot;
        _openTarget = openTarget;
        _readFileBoxes = readFileBoxes;
        _sendToFileBox = sendToFileBox;
        _pinToDock = pinToDock;
        _unpinFromDock = unpinFromDock;
        _readGroups = readGroups ?? (() => Array.Empty<LauncherGroupState>());
        _saveOrganizer = saveOrganizer;
        _readCategoryOverrides = readCategoryOverrides;
        PreviousForeground = previousForeground;
        _monitor = monitor;
        _appearance = appearance ?? new LauncherAppearanceState();
        _preferences = preferences ?? new LauncherPreferencesState();
        _preferences.Normalize();
        // Direct-jump hotkeys (e.g. LauncherFiles) open the requested tab without overwriting
        // LastTab so the regular launcher hotkey still returns to the user's own last tab.
        _activeTab = initialTab ?? _preferences.LastTab;
        _filter = _browseFilter = FilterForTab(_activeTab);
        _style = style ?? new DesktopStyleState();
        InitializeComponent();
        _glass = new DockGlassSurface(this, PanelSurface, "FloatingGlassSurfaceBrush",
            "FloatingGlassBorderBrush", "FloatingGlassPanelBrush", fullTintOpacity: true);
        NewGroupButton.Visibility = saveOrganizer is null ? Visibility.Collapsed : Visibility.Visible;
        ApplyAppearance();
        UpdateMainTabButtons();
        InitializeStatusFadeTimer();
        AccessibilityThemeManager.HighContrastChanged += OnHighContrastChanged;
        _debounce = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = SearchAsync(); };
        InputMethod.SetPreferredImeState(SearchBox, InputMethodState.On);
        InputMethod.SetPreferredImeConversionMode(SearchBox, ImeConversionModeValues.Native);
        TextCompositionManager.AddPreviewTextInputStartHandler(SearchBox, (_, args) =>
        {
            if (args.TextComposition.CompositionText.Length > 0) SuspendForComposition();
        });
        TextCompositionManager.AddPreviewTextInputUpdateHandler(SearchBox, (_, args) =>
        {
            if (args.TextComposition.CompositionText.Length > 0) SuspendForComposition();
            else ResumeAfterComposition();
        });
        SearchBox.PreviewTextInput += (_, _) => ResumeAfterComposition();
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            var style = (long)NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle);
            NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExStyle, (nint)(style | NativeMethods.WsExToolWindow));
            if (HwndSource.FromHwnd(_handle) is { } source) _everything = new EverythingSearchClient(source);
            PositionPanel();
        };
        Loaded += OnPanelLoaded;
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(PositionPanel);
        Closed += (_, _) =>
        {
            AccessibilityThemeManager.HighContrastChanged -= OnHighContrastChanged;
            _closed = true;
            _deactivateOperation?.Abort();
            _deactivateOperation = null;
            _debounce.Stop();
            _statusFade?.Stop();
            PanelContentGrid.BeginAnimation(OpacityProperty, null);
            if (PanelContentGrid.RenderTransform is TranslateTransform rise)
            {
                rise.BeginAnimation(TranslateTransform.YProperty, null);
            }
            PanelContentGrid.RenderTransform = Transform.Identity;
            if (IsKeyboardFocusWithin)
            {
                Keyboard.ClearFocus();
            }
            _lifetime.Cancel();
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _searchCancellation = null;
            CancelDirectoryLoad();
            _menu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
            _menu = null;
            _everything?.Dispose();
            _everything = null;
            _glass.Dispose();
            _lifetime.Dispose();
            WallpaperBackdropImage.Source = null;
            ResultsList.ItemsSource = null;
            PurposeList.ItemsSource = null;
            FileTypeList.ItemsSource = null;
            FilePlacesNavList.ItemsSource = null;
            CategoriesList.ItemsSource = null;
            _snapshot = null;
            _rows = [];
            _unfilteredRows = [];
            _supplemental = [];
            _dragRow = null;
            _groupEntries.Clear();
            _folderStack.Clear();
        };
    }

    private void OnHighContrastChanged(object? sender, EventArgs e) => ApplyAppearance();

    internal void ApplyAppearance()
    {
        PanelSurface.CornerRadius = new CornerRadius(_appearance.FollowCardLook
            ? LauncherAppearanceState.DefaultCornerRadius : _appearance.CornerRadius);
        var (glass, readability) = _appearance.ResolveReadableGlass(_style);
        var highContrast = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast;
        var clearMode = _appearance.UseClearGlass && !highContrast;
        var effectiveDarkInk = clearMode ? ResolveClearModeDarkInk(readability.DarkInk) : readability.DarkInk;
        FloatingGlassPalette.Apply(Resources, glass, highContrast, launcher: true, darkInk: effectiveDarkInk,
            clearGlass: clearMode);
        Resources["LauncherLabelPlateBrush"] = Brushes.Transparent;
        if (clearMode)
        {
            var shadow = effectiveDarkInk
                ? new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.White,
                    Opacity = 0.85d,
                    BlurRadius = 7d,
                    ShadowDepth = 0d
                }
                : new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    Opacity = 0.65d,
                    BlurRadius = 8d,
                    ShadowDepth = 1d,
                    Direction = 270d
                };
            shadow.Freeze();
            Resources["LauncherTextShadowEffect"] = shadow;
        }
        else
        {
            Resources["LauncherTextShadowEffect"] = null;
        }
        _glass.SetClearMode(_appearance.UseClearGlass || _hasWallpaperBackdrop);
        ApplyWallpaperTint(glass, clearMode);
        MaterialButton.Content = _appearance.UseClearGlass ? "清透" : "磨砂";
        MaterialButton.ToolTip = _appearance.UseClearGlass ? "切换为磨砂玻璃" : "切换为清透玻璃";
    }

    private void ApplyWallpaperTint(DockGlass glass, bool clearMode)
    {
        if (WallpaperBackdropHost is null) return;
        var active = _preferences.BackdropMode == LauncherBackdropMode.WallpaperCanvas && _hasWallpaperBackdrop;
        WallpaperBackdropHost.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (!active) return;

        if (clearMode)
        {
            WallpaperBackdropImage.Effect = null;
            var baseTint = (Color)ColorConverter.ConvertFromString(glass.Surface);
            var tint = new SolidColorBrush(Color.FromArgb(40, baseTint.R, baseTint.G, baseTint.B));
            tint.Freeze();
            WallpaperTintOverlay.Background = tint;
        }
        else
        {
            var blur = new System.Windows.Media.Effects.BlurEffect
            {
                Radius = 18d,
                KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
            };
            blur.Freeze();
            WallpaperBackdropImage.Effect = blur;
            var baseTint = (Color)ColorConverter.ConvertFromString(glass.Surface);
            var tint = new SolidColorBrush(Color.FromArgb(Math.Max((byte)145, baseTint.A), baseTint.R, baseTint.G, baseTint.B));
            tint.Freeze();
            WallpaperTintOverlay.Background = tint;
        }
    }

    private async Task LoadWallpaperBackdropAsync()
    {
        if (_closed || _preferences.BackdropMode != LauncherBackdropMode.WallpaperCanvas
            || _activeMonitor is not { } monitor || _panelPixelBounds is not { } panelBounds)
        {
            return;
        }
        try
        {
            var cropped = await LauncherDesktopBackdropService.TryLoadWallpaperCropAsync(
                monitor.Bounds, panelBounds, _lifetime.Token);
            if (_closed || _dismissing || cropped is null) return;
            WallpaperBackdropImage.Source = cropped;
            _hasWallpaperBackdrop = true;
            ApplyAppearance();
        }
        catch (OperationCanceledException) { }
    }

    private bool ResolveClearModeDarkInk(bool fallbackDarkInk)
    {
        if (!_appearance.FollowCardLook)
        {
            return fallbackDarkInk;
        }
        return _style.TextInk switch
        {
            TextInk.White => false,
            TextInk.Dark => true,
            TextInk.Custom => ColorContrastRules.RelativeLuminance(
                ColorContrastRules.ParseHex(WidgetAppearanceRules.CoerceTextColor(_style.CustomTextColor))) <= 0.45d,
            _ => fallbackDarkInk
        };
    }

    internal void RefreshRecentPreference()
    {
        if (_closed || _dismissing) return;
        _ = ReloadSnapshotAfterPreferenceChangeAsync();
    }

    private async Task ReloadSnapshotAfterPreferenceChangeAsync()
    {
        try
        {
            if (_preferences.ShowRecentFiles && (_snapshot?.RecentEntries is null or { Count: 0 }))
            {
                var snapshot = await _loadSnapshot();
                if (_closed || _dismissing) return;
                _snapshot = snapshot;
                await SearchAsync(preserveView: true);
            }
            else if (_snapshot is not null)
            {
                await SearchAsync(preserveView: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    private void OnMaterialClick(object sender, RoutedEventArgs args)
    {
        _appearance.UseClearGlass = !_appearance.UseClearGlass;
        ApplyAppearance();
        _saveOrganizer?.Invoke();
    }

    private void OnPinPanelClick(object sender, RoutedEventArgs args)
    {
        _isPinned = !_isPinned;
        Topmost = _isPinned;
        UpdatePinPanelButton();
        SetStatusText(_isPinned
            ? "已固定并置顶：切到其他软件不会关闭，可拖动顶部空白处移到屏幕一侧（双击归位）"
            : "已取消固定：切到其他软件时自动收起");
    }

    private void UpdatePinPanelButton()
    {
        if (PinPanelButton is null) return;
        PinPanelButton.Content = _isPinned ? "\uE840" : "\uE718";
        PinPanelButton.Tag = _isPinned ? "Selected" : null;
        PinPanelButton.Background = _isPinned
            ? (Brush)FindResource("FloatingGlassSelectedBrush")
            : (Brush)FindResource("FloatingGlassControlBrush");
        PinPanelButton.BorderBrush = _isPinned
            ? (Brush)FindResource("FloatingGlassFocusBrush")
            : (Brush)FindResource("FloatingGlassControlBorderBrush");
        PinPanelButton.ToolTip = _isPinned
            ? "已固定置顶（点击取消；拖动空白处移动，双击归位）"
            : "固定并置顶（保持打开，可拖动空白处移到屏幕一侧）";
        System.Windows.Automation.AutomationProperties.SetName(
            PinPanelButton, _isPinned ? "取消固定冰蓝总览卡" : "固定并置顶冰蓝总览卡");
    }

    internal nint PreviousForeground { get; }
    internal bool DismissedOnDeactivation { get; private set; }
    internal bool LaunchedTarget { get; private set; }
    internal IReadOnlyList<LauncherPanelEntry> VisibleEntries => ResultsList.Items.OfType<PanelRow>().Select(row => row.Entry).ToArray();

    internal void Dismiss(bool restoreForeground = false)
    {
        // Focus and visibility changes leave the input message before ending an IME text store.
        Dispatcher.BeginInvoke(() => ClosePanel(restoreForeground), DispatcherPriority.Input);
    }

    private void ClosePanel(bool restoreForeground)
    {
        if (_closed || _dismissing) return;
        _dismissing = true;
        _isPinned = false;
        Topmost = false;
        _debounce.Stop();
        CancelSearch();
        CancelDirectoryLoad();
        IsHitTestVisible = false;
        var ownsForeground = DockNativeMethods.GetForegroundWindow() == _handle;
        Close();
        if (restoreForeground && ownsForeground && PreviousForeground != 0 && PreviousForeground != _handle
            && DockNativeMethods.IsWindow(PreviousForeground))
            _ = DockNativeMethods.SetForegroundWindow(PreviousForeground);
    }

    // Transient notices (copied, pinned, ...) clear themselves; load errors and progress stay.
    private DispatcherTimer? _statusFade;

    private void InitializeStatusFadeTimer()
    {
        _statusFade = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(4) };
        _statusFade.Tick += (_, _) =>
        {
            _statusFade.Stop();
            if (!_closed && !IsPersistentStatus(StatusText.Text)) StatusText.Text = string.Empty;
        };
    }

    internal void SetStatusText(string text)
    {
        StatusText.Text = text;
        if (_statusFade is null) return;
        _statusFade.Stop();
        if (!_closed && !string.IsNullOrEmpty(text) && !IsPersistentStatus(text))
        {
            _statusFade.Start();
        }
    }

    private static bool IsPersistentStatus(string text) =>
        text.StartsWith("正在", StringComparison.Ordinal) || text.Contains("无法", StringComparison.Ordinal)
        || text.Contains("失败", StringComparison.Ordinal) || text.Contains("没有", StringComparison.Ordinal);

    // 160 ms fade and 6 px rise of the content on open; skipped when system or app motion is off.
    // Only the content moves: the glass backdrop reads PanelSurface's opacity and bounds when it
    // updates, so animating the surface itself would leave the glass stuck at a mid-animation value.
    private void PlayOpenAnimation()
    {
        if (!CardMotionAnimator.IsAnimationEnabled) return;
        var rise = new TranslateTransform(0d, 6d);
        PanelContentGrid.RenderTransform = rise;
        var duration = TimeSpan.FromMilliseconds(160);
        var ease = CardMotionAnimator.CreateEase();
        PanelContentGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0d, 1d, duration) { EasingFunction = ease });
        var up = new DoubleAnimation(6d, 0d, duration) { EasingFunction = ease };
        up.Completed += (_, _) => PanelContentGrid.RenderTransform = Transform.Identity;
        rise.BeginAnimation(TranslateTransform.YProperty, up);
    }

    private async void OnPanelLoaded(object sender, RoutedEventArgs args)
    {
        PositionPanel();
        PlayOpenAnimation();
        Activate();
        SearchBox.Focus();
        if (DockNativeMethods.GetForegroundWindow() != _handle)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closed) return;
                _ = DockNativeMethods.SetForegroundWindow(_handle);
                Activate();
                SearchBox.Focus();
                // Keep the panel visible if Windows denies foreground activation so the user
                // can click to focus it or click the launcher trigger again to dismiss it.
            }, DispatcherPriority.Input);
        }
        if (_closed) return;
        SetStatusText("正在载入项目…");
        try
        {
            var snapshot = await _loadSnapshot();
            if (_closed) return;
            _snapshot = snapshot;
            if (_composing) SetStatusText("");
            else await SearchAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            if (!_closed) SetStatusText("暂时无法载入项目，请关闭后重试");
        }
    }

    private void PositionPanel()
    {
        if (_closed || _handle == 0) return;
        var monitors = MonitorCatalog.GetAll();
        var current = monitors.FirstOrDefault(screen => screen.DeviceName == _monitor.DeviceName)
            ?? monitors.FirstOrDefault() ?? _monitor;
        var monitorWork = MonitorCatalog.TryGetWorkingArea(current.MonitorHandle) ?? current.WorkingArea;
        var work = TaskbarEdgeGuard.AvoidNativeTaskbar(monitorWork, current.Bounds);
        var bounds = LauncherLayoutRules.ComputeBounds(work.Left, work.Top, work.Width, work.Height, current.Dpi);
        Width = bounds.DipWidth;
        Height = bounds.DipHeight;
        BrowseNavigation.Width = bounds.NavigationDipWidth;
        PanelContentGrid.Margin = bounds.DipWidth < 680
            ? new Thickness(16, 14, 16, 14)
            : new Thickness(26, 20, 26, 18);
        var radius = PanelSurface.CornerRadius.TopLeft;
        WallpaperBackdropClip.Rect = new Rect(0, 0, Math.Max(0, bounds.DipWidth - 2), Math.Max(0, bounds.DipHeight - 2));
        WallpaperBackdropClip.RadiusX = radius;
        WallpaperBackdropClip.RadiusY = radius;
        _activeMonitor = current;
        _panelPixelBounds = new PixelRect(bounds.PixelLeft, bounds.PixelTop,
            bounds.PixelLeft + bounds.PixelWidth, bounds.PixelTop + bounds.PixelHeight);
        NativeMethods.SetWindowPos(_handle, 0, bounds.PixelLeft, bounds.PixelTop, bounds.PixelWidth, bounds.PixelHeight,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        if (_preferences.BackdropMode == LauncherBackdropMode.WallpaperCanvas)
        {
            _ = LoadWallpaperBackdropAsync();
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs args)
    {
        if (SearchPlaceholder is null || _debounce is null) return;
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var empty = string.IsNullOrWhiteSpace(SearchBox.Text);
        if (empty != _queryWasEmpty)
        {
            _queryWasEmpty = empty;
            _filter = empty ? _browseFilter : PanelFilter.All;
            ResetSearchScope();
        }
        if (GroupEditor.Visibility == Visibility.Visible) EndGroupEditing(focusSearch: false);
        if (GroupDeleteConfirm.Visibility == Visibility.Visible) CancelGroupDelete(focusSearch: false);
        CancelSearch();
        _supplemental = [];
        _debounce.Stop();
        if (!_composing) _debounce.Start();
        UpdateFilters();
    }

    private void SuspendForComposition()
    {
        _composing = true;
        _debounce.Stop();
        CancelSearch();
    }

    private void ResumeAfterComposition()
    {
        if (!_composing) return;
        _composing = false;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed || _composing || _searchCancellation is not null || _renderedSearchVersion == _searchVersion) return;
            _debounce.Stop();
            if (_snapshot is not null && _renderedSearchVersion < 0) _ = SearchAsync();
            else _debounce.Start();
        }, DispatcherPriority.Input);
    }

    private void CancelSearch()
    {
        ++_searchVersion;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
    }

    private async Task SearchAsync(bool preserveView = false)
    {
        if (_closed || _dismissing || _composing || _snapshot is not { } snapshot) return;
        snapshot = IncludeGroupEntries(snapshot);
        CancelSearch();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _searchCancellation = cancellation;
        var token = cancellation.Token;
        var version = _searchVersion;
        var rowsReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _searchRowsReady = rowsReady.Task;
        var query = SearchBox.Text.Trim();
        var filter = _queryWasEmpty ? PanelFilter.All : _filter;
        var supplemental = _supplemental;
        var includeRecent = _preferences.ShowRecentFiles;
        var favoriteMode = _preferences.FavoriteMode;
        var independentFavorites = _preferences.FavoriteKeys.ToHashSet(StringComparer.Ordinal);
        var openCounts = _preferences.OpenCounts.Count > 0
            ? new Dictionary<string, int>(_preferences.OpenCounts, StringComparer.OrdinalIgnoreCase)
            : null;
        var canManageGroups = _saveOrganizer is not null;
        try
        {
            var results = await Task.Run(() => BuildRows(
                snapshot, supplemental, query, filter, includeRecent, favoriteMode, independentFavorites, canManageGroups, openCounts), token);
            if (!IsCurrent(version, token)) return;
            Render(results, preserveSelection: preserveView);
            _renderedSearchVersion = version;
            UpdateFilters();
            rowsReady.TrySetResult();
            if (query.Length == 0 || filter is PanelFilter.Apps or PanelFilter.Groups) return;
            SetStatusText("正在补充本机搜索结果…");
            var indexed = await WindowsIndexSearchClient.SearchAsync(query, token);
            if (!IsCurrent(version, token)) return;
            var raw = indexed.Candidates.Select(candidate => new LauncherPanelEntry(candidate, "Windows 索引")).ToList();
            var available = indexed.Available;
            if (snapshot.EverythingEnabled && _everything is { } everything)
            {
                var found = await everything.SearchAsync(query, token);
                if (!IsCurrent(version, token)) return;
                available |= found.Available;
                raw.AddRange(found.Candidates.Select(candidate => new LauncherPanelEntry(candidate, "Everything")));
            }
            var extra = await Task.Run(() => LauncherSupplementalNormalizer.Normalize(raw), token);
            if (!IsCurrent(version, token)) return;
            _supplemental = extra;
            results = await Task.Run(() => BuildRows(
                snapshot, extra, query, filter, includeRecent, favoriteMode, independentFavorites, canManageGroups, openCounts), token);
            if (!IsCurrent(version, token)) return;
            Render(results, preserveSelection: true);
            if (!available) SetStatusText(_rows.Count == 0
                ? "没有匹配项目，本机索引暂不可用"
                : $"{_rows.Count} 个项目 · 本机索引暂不可用");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            if (IsCurrent(version, token)) SetStatusText("本机搜索暂不可用，可继续使用已有项目");
        }
        finally { rowsReady.TrySetResult(); }
    }

    private bool IsCurrent(int version, CancellationToken token) => !_closed && !token.IsCancellationRequested && version == _searchVersion;

    private static List<PanelRow> BuildRows(
        LauncherPanelSnapshot snapshot,
        IReadOnlyList<LauncherPanelEntry> supplemental,
        string query,
        PanelFilter filter,
        bool includeRecent,
        LauncherFavoriteMode favoriteMode,
        IReadOnlySet<string> independentFavorites,
        bool canManageGroups,
        IReadOnlyDictionary<string, int>? openCounts = null)
    {
        var favoriteKeys = snapshot.Favorites.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        var dockKeys = snapshot.Favorites
            .Where(entry => entry.Pinned is not null)
            .Select(entry => DockPinRules.IdentityKey(entry.Pinned!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recent = includeRecent ? snapshot.RecentEntries ?? [] : [];
        var all = LauncherIdentityRules.Group(
                snapshot.Favorites.Concat(snapshot.Entries).Concat(recent).Concat(supplemental),
                IdentityOf)
            .Select(group =>
            {
                var primary = group.FirstOrDefault(entry => !entry.IsSavedFallback && entry.Pinned is not null)
                    ?? group.FirstOrDefault(entry => !entry.IsSavedFallback && entry.Source != "最近使用")
                    ?? group.FirstOrDefault(entry => !entry.IsSavedFallback)
                    ?? group[0];
                var sources = primary.IsSavedFallback ? group : group.Where(entry => !entry.IsSavedFallback);
                var folder = group.Select(entry => entry.StartMenuFolder).FirstOrDefault(name => !string.IsNullOrEmpty(name));
                var lastAccessed = group.Select(entry => entry.LastAccessedLocal).Where(t => t.HasValue).Max();
                var fileSize = group.Select(entry => entry.FileSizeBytes).FirstOrDefault(s => s.HasValue);
                var merged = primary;
                if (lastAccessed.HasValue && merged.LastAccessedLocal is null)
                {
                    merged = merged with { LastAccessedLocal = lastAccessed };
                }
                if (fileSize.HasValue && merged.FileSizeBytes is null)
                {
                    merged = merged with { FileSizeBytes = fileSize };
                }
                var preferredName = LauncherIdentityRules.PreferDisplayName(
                    merged.Candidate.DisplayName,
                    merged.Pinned?.ExecutablePath,
                    group.Where(entry => entry.IconSource is not null).Select(entry => entry.Candidate.DisplayName));
                if (!string.Equals(preferredName, merged.Candidate.DisplayName, StringComparison.Ordinal))
                {
                    merged = merged with { Candidate = merged.Candidate with { DisplayName = preferredName } };
                }
                if (merged.IconSource is null
                    && string.IsNullOrWhiteSpace(merged.Pinned?.IconFile)
                    && string.IsNullOrWhiteSpace(merged.Pinned?.AppUserModelId))
                {
                    var shortcutIcon = group.Select(entry => entry.IconSource)
                        .FirstOrDefault(path => !string.IsNullOrEmpty(path));
                    if (shortcutIcon is not null)
                    {
                        merged = merged with { IconSource = shortcutIcon };
                    }
                }
                var isDockPinned = group.Any(entry => favoriteKeys.Contains(entry.Key)
                    || (entry.Pinned is { } pin && dockKeys.Contains(DockPinRules.IdentityKey(pin))));
                var keys = group.Select(entry => entry.Key).Prepend(primary.Key).Distinct(StringComparer.Ordinal).ToArray();
                var isFavorite = favoriteMode switch
                {
                    LauncherFavoriteMode.FollowDock => isDockPinned,
                    LauncherFavoriteMode.Independent => keys.Any(independentFavorites.Contains),
                    _ => false
                };
                var canToggleFavorite = favoriteMode == LauncherFavoriteMode.Independent && canManageGroups;
                return new PanelRow(merged, isFavorite, isDockPinned,
                    sources.Select(entry => entry.Source).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    folder, keys, canToggleFavorite, canManageGroups);
            })
            .Where(row => MatchesFilter(row.Entry, filter)).ToArray();
        if (query.Length == 0)
        {
            return all.OrderByDescending(row => row.IsFavorite)
                .ThenBy(row => row.IsFavorite ? string.Empty : row.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        var byCandidate = all.ToLookup(row => row.Entry.Candidate);
        return TopBarSearchRules.Search(byCandidate.Select(group => group.Key), query, int.MaxValue, openCounts)
            .SelectMany(candidate => byCandidate[candidate]).ToList();
    }

    internal static LauncherIdentityInput IdentityOf(LauncherPanelEntry entry) =>
        new(entry.Pinned?.ExecutablePath, entry.Pinned?.AppUserModelId, entry.Pinned?.TargetPath,
            entry.Candidate.Path, entry.Pinned?.Arguments, entry.Candidate.DisplayName);

    private static bool MatchesFilter(LauncherPanelEntry entry, PanelFilter filter) =>
        filter is PanelFilter.All or PanelFilter.Groups
        || (entry.Candidate.Kind == TopBarSearchItemKind.App) == (filter == PanelFilter.Apps);

    private static PanelFilter FilterForTab(LauncherMainTab tab) => tab switch
    {
        LauncherMainTab.Apps => PanelFilter.Apps,
        LauncherMainTab.Files => PanelFilter.Files,
        _ => PanelFilter.All
    };

    private void Render(List<PanelRow> rows, bool preserveSelection)
    {
        var previous = preserveSelection ? (ResultsList.SelectedItem as PanelRow)?.Entry : null;
        var canManageGroups = _saveOrganizer is not null;
        foreach (var row in rows)
        {
            row.RefreshFavorite(_preferences, canManageGroups);
        }
        foreach (var row in _directoryRows)
        {
            row.RefreshFavorite(_preferences, canManageGroups);
        }
        _unfilteredRows = rows;
        ComputeEffectiveAppCategories(rows);
        var scope = ApplyOrganizerFilters(rows);
        var offset = preserveSelection ? ResultViewport?.VerticalOffset ?? 0 : 0;
        if (_queryWasEmpty && _restoreBrowseOffset)
        {
            offset = _browseOffset;
            _restoreBrowseOffset = false;
        }
        var items = BuildDisplayItems();
        _rows = items.OfType<PanelRow>().ToList();
        var missingInitial = new List<PanelRow>();
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (LauncherIconCache.TryGet(IconIdentity(row.Entry), out var icon))
            {
                row.Icon = icon;
            }
            else if (i < 36)
            {
                missingInitial.Add(row);
            }
        }

        UpdateResultsLayoutMode();
        ResultsList.ItemsSource = items;
        var nextSelection = _queryWasEmpty
            ? (previous is null ? null : _rows.FirstOrDefault(row => row.Entry.Key == previous.Key))
            : ((previous is null ? null : _rows.FirstOrDefault(row => row.Entry.Key == previous.Key))
                ?? _rows.FirstOrDefault());
        ResultsList.Tag = nextSelection is not null;
        ResultsList.SelectedItem = nextSelection;
        UpdateContextHeader(scope);
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = ResolveEmptyHint();
        SetStatusText(ResolveStatusText());
        // Kick off first-viewport icon loads immediately instead of waiting for two
        // Dispatcher hops (Loaded -> Background), eliminating the visible pop-in delay.
        if (missingInitial.Count > 0)
        {
            _ = LoadIconsAsync(missingInitial, ++_iconViewVersion);
        }
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed || !ReferenceEquals(ResultsList.ItemsSource, items)) return;
            ResultViewport?.ScrollToVerticalOffset(offset);
            if (offset > 0 || missingInitial.Count == 0)
            {
                QueueVisibleIcons();
            }
        }, DispatcherPriority.Loaded);
    }

    private void UpdateResultsLayoutMode()
    {
        var listMode = IsListViewActive();
        ResultsList.ItemTemplate = (DataTemplate)Resources[listMode ? "ListItemTemplate" : "TileItemTemplate"];
        if (FindGrid(ResultsList) is { } grid && grid.IsListMode != listMode)
        {
            grid.IsListMode = listMode;
        }
    }

    private async Task LoadIconsAsync(List<PanelRow> rows, int version)
    {
        // Fan out bounded concurrent icon extractions so a full viewport populates in one
        // quick wave instead of trickling in serially one icon at a time.
        var pending = new List<(string IconKey, LauncherPanelEntry Entry, string Name)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (_closed || _dismissing || version != _iconViewVersion) return;
            var iconKey = IconIdentity(row.Entry);
            if (LauncherIconCache.TryGet(iconKey, out var cached))
            {
                if (row.Icon is null) row.Icon = cached;
                continue;
            }
            if (seen.Add(iconKey))
            {
                pending.Add((iconKey, row.Entry, row.Name));
            }
        }
        if (pending.Count == 0) return;

        var tasks = pending.Select(item => LoadSingleIconAsync(item.IconKey, item.Entry, item.Name, version)).ToArray();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadSingleIconAsync(string iconKey, LauncherPanelEntry entry, string name, int version)
    {
        if (_closed || _dismissing || version != _iconViewVersion) return;
        try
        {
            var iconTask = LauncherIconCache.GetOrLoadAsync(iconKey, () => ExtractIconGuardedAsync(entry, iconKey, name, _lifetime.Token));
            var icon = await iconTask.WaitAsync(TimeSpan.FromMilliseconds(900), _lifetime.Token);
            if (!_closed && icon is not null)
            {
                ApplyIcon(iconKey, icon);
            }
        }
        catch (TimeoutException)
        {
            // Keep listening so a slow disk icon still populates when it completes.
            _ = LauncherIconCache.GetOrLoadAsync(iconKey, () => Task.FromResult<ImageSource?>(null))
                .ContinueWith(completed =>
                {
                    var resolved = completed.IsCompletedSuccessfully ? completed.Result : null;
                    if (resolved is not null && !_closed)
                    {
                        Dispatcher.BeginInvoke(() => ApplyIcon(iconKey, resolved));
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
        catch (OperationCanceledException) { }
    }

    internal static async Task<ImageSource?> ExtractIconGuardedAsync(
        LauncherPanelEntry entry,
        string iconKey,
        string name,
        CancellationToken token)
    {
        if (!await IconGate.WaitAsync(TimeSpan.FromMilliseconds(900), token).ConfigureAwait(false))
        {
            return null;
        }
        try
        {
            if (token.IsCancellationRequested) return null;
            return await Task.Run(() =>
            {
                try
                {
                    return ExtractIcon(entry, iconKey, name);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                    or System.Runtime.InteropServices.COMException)
                {
                    return null;
                }
            }, token).ConfigureAwait(false);
        }
        finally
        {
            IconGate.Release();
        }
    }

    internal static void PrewarmIcon(LauncherPanelEntry entry)
    {
        var iconKey = IconIdentity(entry);
        if (LauncherIconCache.TryGet(iconKey, out _)) return;
        var name = entry.Candidate.DisplayName;
        _ = LauncherIconCache.GetOrLoadAsync(iconKey, () => ExtractIconGuardedAsync(entry, iconKey, name, CancellationToken.None));
    }

    private void ApplyIcon(string iconKey, ImageSource? icon)
    {
        if (_closed || icon is null) return;
        foreach (var row in _rows)
        {
            if (string.Equals(IconIdentity(row.Entry), iconKey, StringComparison.OrdinalIgnoreCase))
            {
                row.Icon = icon;
            }
        }
    }

    private static ImageSource? ExtractIcon(LauncherPanelEntry entry, string iconKey, string name)
    {
        ImageSource? icon;
        if (entry.Pinned is { } customPin
            && (!string.IsNullOrWhiteSpace(customPin.IconFile) || !string.IsNullOrWhiteSpace(customPin.AppUserModelId)))
        {
            icon = DockAppResolver.Resolve(new DockItem(iconKey, name, customPin, null)).Icon;
        }
        else if (!string.IsNullOrWhiteSpace(entry.IconSource))
        {
            icon = ShellIconProvider.GetIcon(entry.IconSource)
                ?? (entry.Pinned is { } pin
                    ? DockAppResolver.Resolve(new DockItem(iconKey, name, pin, null)).Icon
                    : ShellIconProvider.GetIcon(entry.Candidate.Path));
        }
        else if (entry.Pinned is { } pin)
        {
            icon = DockAppResolver.Resolve(new DockItem(iconKey, name, pin, null)).Icon;
        }
        else
        {
            icon = ShellIconProvider.GetIcon(entry.Candidate.Path);
        }

        if (icon is Freezable { IsFrozen: false, CanFreeze: true } freezable)
        {
            freezable.Freeze();
        }
        return icon;
    }

    private static string IconIdentity(LauncherPanelEntry entry) =>
        (entry.IconSource ?? entry.Candidate.Path) + '\0' + entry.Pinned?.AppUserModelId + '\0' + entry.Pinned?.IconFile;

    private void UpdateFilters()
    {
        BrowseNavigation.Visibility = _queryWasEmpty ? Visibility.Visible : Visibility.Collapsed;
        ContentHeaderBar.Visibility = _queryWasEmpty ? Visibility.Visible : Visibility.Collapsed;
        UpdateMainTabButtons();
    }

    private void SelectSearchFilter(PanelFilter filter)
    {
        _filter = filter;
        UpdateFilters();
        _debounce.Stop();
        _ = SearchAsync();
    }

    private void OnCloseClick(object sender, RoutedEventArgs args) => Dismiss(restoreForeground: true);

    private void OnPanelKeyDown(object sender, KeyEventArgs args)
    {
        if (_dismissing) { args.Handled = true; return; }
        if (GroupDeleteConfirm.Visibility == Visibility.Visible)
        {
            if (args.Key == Key.Escape) { CancelGroupDelete(); args.Handled = true; }
            return;
        }
        if (GroupEditor.Visibility == Visibility.Visible)
        {
            if (args.Key == Key.Escape)
            {
                EndGroupEditing();
                args.Handled = true;
                return;
            }
            if (GroupNameEditor.IsKeyboardFocusWithin
                && args.Key == Key.Return
                && Keyboard.Modifiers == ModifierKeys.None)
            {
                SaveGroupEdit();
                args.Handled = true;
            }
            return;
        }
        if (SourceFilter.IsKeyboardFocusWithin && (args.Key != Key.Escape || SourceFilter.IsDropDownOpen)) return;
        if (SearchCurrentCategory.IsKeyboardFocusWithin && args.Key == Key.Return)
        {
            SearchCurrentCategory.IsChecked = SearchCurrentCategory.IsChecked != true;
            Render(_unfilteredRows, preserveSelection: false);
            args.Handled = true;
            return;
        }
        if (args.Key == Key.ImeProcessed) { SuspendForComposition(); return; }
        if (_composing) return;
        if (_menu?.IsOpen == true)
        {
            if (args.Key == Key.Escape) { _menu.IsOpen = false; SearchBox.Focus(); args.Handled = true; }
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Alt && args.SystemKey == Key.Left && _queryWasEmpty
            && _activeTab == LauncherMainTab.Files && _folderStack.Count > 0)
        {
            NavigateBackFolder();
            args.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (args.Key == Key.C
                && ResultsList.SelectedItem is PanelRow copyRow
                && (!SearchBox.IsKeyboardFocusWithin || SearchBox.SelectionLength == 0))
            {
                if (CopyRowFileToClipboard(copyRow))
                {
                    args.Handled = true;
                    return;
                }
            }
            if (args.Key is Key.D1 or Key.NumPad1) { SwitchMainTab(LauncherMainTab.Apps); args.Handled = true; return; }
            if (args.Key is Key.D2 or Key.NumPad2) { SwitchMainTab(LauncherMainTab.Files); args.Handled = true; return; }
            if (args.Key is Key.D3 or Key.NumPad3) { SwitchMainTab(LauncherMainTab.Groups); args.Handled = true; return; }
            if (args.Key == Key.Tab)
            {
                if (_queryWasEmpty)
                {
                    var next = _activeTab switch
                    {
                        LauncherMainTab.Apps => LauncherMainTab.Files,
                        LauncherMainTab.Files => LauncherMainTab.Groups,
                        _ => LauncherMainTab.Apps
                    };
                    SwitchMainTab(next);
                }
                else
                {
                    var next = _filter switch
                    {
                        PanelFilter.All => PanelFilter.Apps,
                        PanelFilter.Apps => PanelFilter.Files,
                        PanelFilter.Files => PanelFilter.Groups,
                        _ => PanelFilter.All
                    };
                    SelectSearchFilter(next);
                }
                args.Handled = true;
                return;
            }
        }
        if (Keyboard.FocusedElement is Button && args.Key == Key.Return) return;
        var inSearch = SearchBox.IsKeyboardFocusWithin;
        var inResults = ResultsList.IsKeyboardFocusWithin;
        if (args.Key == Key.Back && Keyboard.Modifiers == ModifierKeys.None
            && _queryWasEmpty && SearchBox.Text.Length == 0
            && _activeTab == LauncherMainTab.Files && _folderStack.Count > 0
            && (inSearch || inResults))
        {
            NavigateBackFolder();
            args.Handled = true;
            return;
        }
        switch (args.Key)
        {
            case Key.Escape:
                Dismiss(restoreForeground: true);
                args.Handled = true;
                break;
            case Key.Return when inSearch || inResults:
                _ = OpenSelectionAsync(Keyboard.Modifiers == ModifierKeys.Control);
                args.Handled = true;
                break;
            case Key.Space when Keyboard.Modifiers == ModifierKeys.None && !inSearch
                && ResultsList.SelectedItem is PanelRow previewRow && LocalPath(previewRow.Entry) is { } previewPath:
                _ = QuickPreviewLauncher.TryTogglePreviewAsync(previewPath);
                args.Handled = true;
                break;
            case Key.Left when inResults || (inSearch && SearchBox.Text.Length == 0):
            case Key.Right when inResults || (inSearch && SearchBox.Text.Length == 0):
                SelectNeighbor(args.Key);
                args.Handled = true;
                break;
            case Key.Up when inSearch || inResults:
            case Key.Down when inSearch || inResults:
                SelectNeighbor(args.Key);
                args.Handled = true;
                break;
            case Key.Home when inResults || (inSearch && SearchBox.Text.Length == 0):
                if (NavigableRows() is { Count: > 0 } homeRows) SelectAndReveal(homeRows[0]);
                args.Handled = true;
                break;
            case Key.End when inResults || (inSearch && SearchBox.Text.Length == 0):
                if (NavigableRows() is { Count: > 0 } endRows) SelectAndReveal(endRows[^1]);
                args.Handled = true;
                break;
            case Key.PageUp when inSearch || inResults:
            case Key.PageDown when inSearch || inResults:
                ScrollResultPage(args.Key == Key.PageDown);
                args.Handled = true;
                break;
            case Key.Apps:
            case Key.F10 when Keyboard.Modifiers == ModifierKeys.Shift:
                if (ResultsList.SelectedItem is PanelRow row) OpenMenu(row);
                args.Handled = true;
                break;
        }
    }

    private void OnPanelMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton == MouseButton.XButton1
            && _queryWasEmpty && _activeTab == LauncherMainTab.Files && _folderStack.Count > 0)
        {
            NavigateBackFolder();
            args.Handled = true;
        }
    }

    private void OnPanelSurfaceMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (_closed || _dismissing || _draggingOut || args.Handled
            || args.ButtonState != MouseButtonState.Pressed
            || IsInsideNonDragControl(args.OriginalSource as DependencyObject))
        {
            return;
        }

        if (args.ClickCount == 2)
        {
            PositionPanel();
            args.Handled = true;
            return;
        }

        try
        {
            DragMove();
            SyncMovedBounds();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void SyncMovedBounds()
    {
        if (_closed || _dismissing || _handle == 0 || !DockNativeMethods.GetWindowRect(_handle, out var rect))
        {
            return;
        }

        _panelPixelBounds = new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
        var centerX = (rect.Left + rect.Right) / 2;
        var centerY = (rect.Top + rect.Bottom) / 2;
        var movedMonitor = MonitorCatalog.GetAll().FirstOrDefault(candidate =>
            centerX >= candidate.Bounds.Left && centerX < candidate.Bounds.Right
            && centerY >= candidate.Bounds.Top && centerY < candidate.Bounds.Bottom);
        if (movedMonitor is not null)
        {
            _activeMonitor = movedMonitor;
        }

        if (_preferences.BackdropMode == LauncherBackdropMode.WallpaperCanvas)
        {
            _ = LoadWallpaperBackdropAsync();
        }
    }

    private static bool IsInsideNonDragControl(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase
                or TextBoxBase
                or Selector
                or ScrollBar
                or Thumb
                or System.Windows.Controls.MenuItem
                or System.Windows.Controls.ContextMenu)
            {
                return true;
            }
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private static ListBoxItem? FindItem(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ListBoxItem item) return item;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private static bool IsInsideScrollBar(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ScrollBar) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button) return true;
            if (source is ListBoxItem) return false;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void OnResultMouseDown(object sender, MouseButtonEventArgs args)
    {
        _dragStartPoint = null;
        _dragRow = null;
        _suppressNextClick = false;
        var source = args.OriginalSource as DependencyObject;
        if (IsInsideButton(source) || IsInsideScrollBar(source)) return;
        if (FindItem(source)?.DataContext is PanelRow row && LocalPath(row.Entry) is not null)
        {
            _dragStartPoint = args.GetPosition(ResultsList);
            _dragRow = row;
        }
    }

    private void OnResultMouseMove(object sender, System.Windows.Input.MouseEventArgs args)
    {
        if (args.LeftButton != MouseButtonState.Pressed || _dragRow is not { } row || _dragStartPoint is not { } start)
        {
            return;
        }
        var current = args.GetPosition(ResultsList);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }
        _dragRow = null;
        _dragStartPoint = null;
        if (LocalPath(row.Entry) is not { } path || (!File.Exists(path) && !Directory.Exists(path)))
        {
            return;
        }
        _suppressNextClick = true;
        _draggingOut = true;
        var previousExStyle = _handle != 0 ? (long)NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle) : 0L;
        var draggedEffect = System.Windows.DragDropEffects.None;
        try
        {
            var data = BuildFileCopyDataObject(path);
            // Make the panel click-through and invisible during drag so the underlying WeChat /
            // browser drop target is unobstructed while keeping the WPF drag source alive.
            Opacity = 0d;
            _glass.SetClearMode(true);
            if (_handle != 0)
            {
                NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExStyle, (nint)(previousExStyle | NativeMethods.WsExTransparent));
            }
            draggedEffect = DragDrop.DoDragDrop(ResultsList, data, System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Link);
        }
        catch (System.Runtime.InteropServices.ExternalException) { }
        finally
        {
            if (_handle != 0)
            {
                NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExStyle, (nint)previousExStyle);
            }
            Opacity = 1d;
            if (!_closed && !_dismissing)
            {
                _glass.SetClearMode(_appearance.UseClearGlass || _hasWallpaperBackdrop);
            }
            _draggingOut = false;
            if (!_isPinned && draggedEffect != System.Windows.DragDropEffects.None && !_closed && !_dismissing)
            {
                Dismiss(restoreForeground: false);
            }
        }
    }

    private void OnResultMouseUp(object sender, MouseButtonEventArgs args)
    {
        _dragRow = null;
        _dragStartPoint = null;
        if (_suppressNextClick)
        {
            _suppressNextClick = false;
            args.Handled = true;
            return;
        }
        var source = args.OriginalSource as DependencyObject;
        if (IsInsideButton(source)) return;
        if (FindItem(source)?.DataContext is not PanelRow row)
        {
            if (_queryWasEmpty && !IsInsideScrollBar(source))
            {
                ResultsList.SelectedItem = null;
                ResultsList.Tag = false;
            }
            return;
        }
        ActivateEntry(row.Entry, locate: false);
        args.Handled = true;
    }

    private void OnToggleFavoriteClick(object sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is PanelRow row)
        {
            ToggleIndependentFavorite(row);
        }
    }

    private void OnQuickLocateClick(object sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is PanelRow row)
        {
            QueueOpen(row.Entry, locate: true);
        }
    }

    private void OnQuickCopyPathClick(object sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is PanelRow row && LocalPath(row.Entry) is { } path)
        {
            try
            {
                System.Windows.Clipboard.SetText(path);
                SetStatusText($"已复制路径：{row.Name}");
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                SetStatusText("剪贴板暂忙，请稍后重试");
            }
        }
    }

    private void OnQuickGroupClick(object sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (_closed || _dismissing || _saveOrganizer is null) return;
        if ((sender as FrameworkElement)?.DataContext is not PanelRow row) return;
        _menu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        var menu = new ContextMenu();
        AddOrganizerMenu(menu, row);
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu)) _menu = null;
        };
        menu.PlacementTarget = sender as UIElement ?? ResultsList;
        menu.Placement = PlacementMode.Bottom;
        _menu = menu;
        menu.IsOpen = true;
    }

    private void ToggleIndependentFavorite(PanelRow row)
    {
        if (_saveOrganizer is null || _preferences.FavoriteMode != LauncherFavoriteMode.Independent) return;
        var added = _preferences.ToggleFavoriteKey(row.Entry.Key, row.Keys);
        _saveOrganizer();
        Render(_unfilteredRows, preserveSelection: true);
        SetStatusText(added ? $"已将「{row.Name}」加入常用" : $"已将「{row.Name}」移出常用");
    }

    private void OnResultRightClick(object sender, MouseButtonEventArgs args)
    {
        if (FindItem(args.OriginalSource as DependencyObject)?.DataContext is not PanelRow row)
        {
            if (_queryWasEmpty)
            {
                ResultsList.SelectedItem = null;
                ResultsList.Tag = false;
            }
            return;
        }
        ResultsList.Tag = true;
        ResultsList.SelectedItem = row;
        OpenMenu(row);
        args.Handled = true;
    }

    private void OpenMenu(PanelRow row)
    {
        if (_closed || _dismissing) return;
        _menu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        var menu = new ContextMenu();
        if (CanDrillDown(row.Entry))
        {
            var expand = new MenuItem { Header = "在面板内展开" };
            expand.Click += (_, _) => _ = DrillIntoDirectoryAsync(LocalPath(row.Entry)!, row.Name);
            menu.Items.Add(expand);

            var openExplorer = new MenuItem { Header = "在资源管理器中打开" };
            openExplorer.Click += (_, _) => QueueOpen(row.Entry, locate: false);
            menu.Items.Add(openExplorer);
        }
        else
        {
            var open = new MenuItem { Header = "打开" };
            open.Click += (_, _) => QueueOpen(row.Entry, locate: false);
            menu.Items.Add(open);
        }
        if (LocalPath(row.Entry) is { } localPath)
        {
            var locate = new MenuItem { Header = "打开所在位置" };
            locate.Click += (_, _) => QueueOpen(row.Entry, locate: true);
            menu.Items.Add(locate);

            if (File.Exists(localPath) || Directory.Exists(localPath))
            {
                var copyFile = new MenuItem { Header = "复制文件", InputGestureText = "Ctrl+C" };
                copyFile.Click += (_, _) => CopyRowFileToClipboard(row);
                menu.Items.Add(copyFile);
            }

            var copyPath = new MenuItem { Header = "复制路径" };
            copyPath.Click += (_, _) => CopyTextToClipboard(localPath, $"已复制路径：{row.Name}");
            menu.Items.Add(copyPath);

            if (Directory.Exists(localPath) && _saveOrganizer is not null)
            {
                var pinned = _preferences.IsFolderPinned(localPath);
                var togglePinnedFolder = new MenuItem { Header = pinned ? "从常用位置移除" : "固定到常用位置" };
                togglePinnedFolder.Click += (_, _) => _ = TogglePinnedFolderAsync(localPath);
                menu.Items.Add(togglePinnedFolder);
            }
        }
        if (_preferences.FavoriteMode == LauncherFavoriteMode.Independent && _saveOrganizer is not null)
        {
            var toggleFav = new MenuItem { Header = row.IsFavorite ? "移出常用" : "加入常用" };
            toggleFav.Click += (_, _) => ToggleIndependentFavorite(row);
            menu.Items.Add(toggleFav);
        }
        if ((row.IsDockPinned ? _unpinFromDock is not null || _pinToDock is not null : _pinToDock is not null)
            && (row.Entry.Pinned is not null || LocalPath(row.Entry) is not null))
        {
            if (row.IsDockPinned && _unpinFromDock is not null)
            {
                var unpin = new MenuItem { Header = "从 Dock 取消固定" };
                unpin.Click += async (_, _) => await ChangeDockPinAsync(row.Entry, pin: false);
                menu.Items.Add(unpin);
            }
            else
            {
                var pin = new MenuItem { Header = "固定到 Dock", IsEnabled = !row.IsDockPinned, IsChecked = row.IsDockPinned };
                pin.Click += async (_, _) => await ChangeDockPinAsync(row.Entry, pin: true);
                menu.Items.Add(pin);
            }
        }
        AddOrganizerMenu(menu, row);
        AddPurposeMenu(menu, row);
        if (SendPath(row.Entry) is { } path)
        {
            var send = new MenuItem { Header = "发送到文件收纳盒" };
            var boxes = _readFileBoxes();
            if (boxes.Count == 0) send.Items.Add(new MenuItem { Header = "尚无文件收纳盒", IsEnabled = false });
            for (var index = 0; index < boxes.Count; index++)
            {
                var box = boxes[index];
                var target = new MenuItem { Header = box.Title };
                target.Click += async (_, _) =>
                {
                    try
                    {
                        var message = await _sendToFileBox(box.Id, path);
                        if (!_closed) SetStatusText(message);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        if (!_closed) SetStatusText("暂时无法发送到文件收纳盒");
                    }
                };
                send.Items.Add(target);
            }
            menu.Items.Add(send);
        }
        menu.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            menu.IsOpen = false;
            args.Handled = true;
        };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu)) _menu = null;
            Dispatcher.BeginInvoke(() =>
            {
                if (_closed || _dismissing) return;
                if (IsActive || DockNativeMethods.GetForegroundWindow() == _handle)
                {
                    if (GroupEditor.Visibility != Visibility.Visible) SearchBox.Focus();
                    else GroupNameEditor.Focus();
                }
                else CloseOnDeactivation();
            }, DispatcherPriority.ContextIdle);
        };
        ResultsList.ScrollIntoView(row);
        menu.PlacementTarget = ResultsList.ItemContainerGenerator.ContainerFromItem(row) as UIElement ?? ResultsList;
        menu.Placement = PlacementMode.Bottom;
        _menu = menu;
        menu.IsOpen = true;
    }

    private static string? LocalPath(LauncherPanelEntry entry)
    {
        var path = entry.Pinned?.TargetPath ?? entry.Pinned?.ExecutablePath ?? entry.Candidate.Path;
        return !string.IsNullOrWhiteSpace(path) && !FileMappingService.IsShellEntry(path) && Path.IsPathFullyQualified(path) ? path : null;
    }

    private static bool CanDrillDown(LauncherPanelEntry entry) =>
        entry.Candidate.Kind == TopBarSearchItemKind.Folder
        && LocalPath(entry) is { Length: > 0 } path
        && !path.StartsWith(@"\\", StringComparison.Ordinal);

    private static string? SendPath(LauncherPanelEntry entry)
    {
        var path = LocalPath(entry);
        if (path is null || entry.Pinned is not { TargetPath: null } pin) return path;
        // A file mapping cannot carry app activation metadata or shortcut arguments.
        // Only a real shortcut or an ordinary executable can be represented faithfully.
        if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return path;
        if (!string.IsNullOrWhiteSpace(pin.AppUserModelId) || !string.IsNullOrWhiteSpace(pin.Arguments)) return null;
        var defaultDirectory = Path.GetDirectoryName(path);
        return string.IsNullOrWhiteSpace(pin.WorkingDirectory)
            || string.Equals(pin.WorkingDirectory.TrimEnd('\\', '/'), defaultDirectory?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                ? path : null;
    }

    private void ActivateEntry(LauncherPanelEntry entry, bool locate, int? searchVersion = null)
    {
        if (!locate && _queryWasEmpty && _activeTab == LauncherMainTab.Files && CanDrillDown(entry))
        {
            _ = DrillIntoDirectoryAsync(LocalPath(entry)!, entry.Candidate.DisplayName);
            return;
        }
        QueueOpen(entry, locate, searchVersion);
    }

    private async Task OpenSelectionAsync(bool locate)
    {
        if (_closed || _dismissing || _composing || _snapshot is null) return;
        _debounce.Stop();
        var version = _searchVersion;
        if (_renderedSearchVersion != version && _searchCancellation is null)
        {
            _ = SearchAsync();
            version = _searchVersion;
            if (_renderedSearchVersion != version && _searchCancellation is null) return;
        }
        await _searchRowsReady;
        if (!IsActivationCurrent(version) || _renderedSearchVersion != version) return;
        if (ResultsList.SelectedItem is PanelRow selected) ActivateEntry(selected.Entry, locate, version);
    }

    private bool IsActivationCurrent(int? version) =>
        !_closed && !_dismissing && (version is null || (!_composing && version == _searchVersion));

    private void QueueOpen(LauncherPanelEntry entry, bool locate, int? searchVersion = null)
    {
        Dispatcher.BeginInvoke(() => _ = OpenAsync(entry, locate, searchVersion), DispatcherPriority.Input);
    }

    private async Task OpenAsync(LauncherPanelEntry entry, bool locate, int? searchVersion = null)
    {
        if (!IsActivationCurrent(searchVersion) || _openingTarget) return;
        var path = LocalPath(entry);
        if (locate && path is null) return;
        _openingTarget = true;
        try
        {
            if (path is not null && (locate || string.IsNullOrWhiteSpace(entry.Pinned?.AppUserModelId))
                && !await CheckLocalTargetAsync(path)) return;
            if (!IsActivationCurrent(searchVersion)) return;
            if (!locate && _saveOrganizer is not null && !string.IsNullOrWhiteSpace(entry.Candidate.Path))
            {
                _preferences.RecordOpen(entry.Candidate.Path);
                _saveOrganizer();
            }
            LaunchedTarget = true;
            if (!_isPinned)
            {
                ClosePanel(restoreForeground: false);
            }
            _openTarget(entry, locate);
        }
        catch (OperationCanceledException) { }
        finally { _openingTarget = false; }
    }

    private async Task ChangeDockPinAsync(LauncherPanelEntry entry, bool pin)
    {
        var action = pin ? _pinToDock : _unpinFromDock;
        if (_closed || _dismissing || _pinningTarget || action is null) return;
        _pinningTarget = true;
        var updated = false;
        try
        {
            if (pin && string.IsNullOrWhiteSpace(entry.Pinned?.AppUserModelId) && LocalPath(entry) is { } path
                && !await CheckLocalTargetAsync(path)) return;
            if (_closed || _dismissing) return;
            var message = action(entry);
            updated = true;
            var snapshot = await _loadSnapshot();
            if (_closed || _dismissing) return;
            _snapshot = snapshot;
            await SearchAsync(preserveView: true);
            if (!_closed && !_dismissing) SetStatusText(message);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            if (!_closed && !_dismissing)
            {
                SetStatusText(updated
                    ? "已更新 Dock，但暂时无法刷新列表"
                    : "暂时无法更新 Dock，请稍后重试");
            }
        }
        finally { _pinningTarget = false; }
    }

    private async Task<bool> CheckLocalTargetAsync(string path)
    {
        SetStatusText("正在检查项目…");
        var token = _lifetime.Token;
        try
        {
            if (!await PathCheckGate.WaitAsync(TimeSpan.FromMilliseconds(200), token))
            {
                if (!_closed && !_dismissing) SetStatusText("项目响应较慢，请稍后重试");
                return false;
            }
            var check = Task.Run(() =>
            {
                try { return File.Exists(path) || Directory.Exists(path); }
                finally { PathCheckGate.Release(); }
            });
            var exists = await check.WaitAsync(TimeSpan.FromMilliseconds(1200), token);
            if (!exists && !_closed && !_dismissing) SetStatusText("文件或应用已不存在，可继续选择其他项目");
            return exists;
        }
        catch (TimeoutException)
        {
            if (!_closed && !_dismissing) SetStatusText("项目响应较慢，请检查连接后重试");
            return false;
        }
    }

    private void OnPanelDeactivated(object? sender, EventArgs args)
    {
        if (_closed || _dismissing) return;
        _deactivateOperation?.Abort();
        _deactivateOperation = Dispatcher.BeginInvoke(CloseOnDeactivation, DispatcherPriority.ContextIdle);
    }

    private void CloseOnDeactivation()
    {
        if (_closed || _isPinned || _modalDialogOpen || _draggingOut || IsActive || _menu?.IsOpen == true || DockNativeMethods.GetForegroundWindow() == _handle) return;
        DismissedOnDeactivation = true;
        ClosePanel(restoreForeground: false);
    }

    private static System.Windows.DataObject BuildFileCopyDataObject(string path)
    {
        var data = new System.Windows.DataObject();
        var list = new System.Collections.Specialized.StringCollection { path };
        data.SetFileDropList(list);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
        return data;
    }

    private bool CopyRowFileToClipboard(PanelRow row)
    {
        if (LocalPath(row.Entry) is not { } path || (!File.Exists(path) && !Directory.Exists(path)))
        {
            return false;
        }
        try
        {
            System.Windows.Clipboard.SetDataObject(BuildFileCopyDataObject(path), true);
            SetStatusText($"已复制文件：{row.Name}（可直接粘贴）");
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            SetStatusText("剪贴板暂忙，请稍后重试");
            return false;
        }
    }

    private void CopyTextToClipboard(string text, string successMessage)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            SetStatusText(successMessage);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            SetStatusText("剪贴板暂忙，请稍后重试");
        }
    }

    private enum PanelFilter { All, Apps, Files, Groups }

    private sealed record CategoryChip(
        string? Key,
        string Name,
        bool IsSelected,
        LauncherAppCategory? Purpose = null,
        string Glyph = "\uE71D",
        string CountText = "",
        LauncherFileCategory? FileKind = null,
        string? PlaceId = null)
    {
        public string SelectionStatus => IsSelected ? "已选择" : "";
    }

    private sealed record BreadcrumbSegment(int Depth, string Title, bool IsCurrent);

    private sealed class PanelRow : INotifyPropertyChanged
    {
        private ImageSource? _icon;
        private bool _isFavorite;
        private bool _canToggleFavorite;
        private bool _canManageGroups;
        public PanelRow(LauncherPanelEntry entry, bool isFavorite, bool isDockPinned, IReadOnlyList<string> sources,
            string? startMenuFolder, IReadOnlyList<string>? keys = null, bool canToggleFavorite = false, bool canManageGroups = false)
        {
            Entry = entry;
            _isFavorite = isFavorite;
            IsDockPinned = isDockPinned;
            _canToggleFavorite = canToggleFavorite;
            _canManageGroups = canManageGroups;
            Sources = sources;
            StartMenuFolder = startMenuFolder;
            Keys = keys is { Count: > 0 } ? keys : [entry.Key];
            AutomaticCategory = entry.Candidate.Kind == TopBarSearchItemKind.App
                ? LauncherAppCategoryRules.Classify(entry.Candidate.DisplayName, entry.Candidate.Path,
                    entry.Pinned?.Arguments, startMenuFolder)
                : LauncherAppCategory.Unclassified;
            EffectiveCategory = AutomaticCategory;
            FileCategory = entry.Candidate.Kind == TopBarSearchItemKind.App
                ? LauncherFileCategory.Other
                : LauncherFileCategoryRules.Classify(entry.Candidate.Path, entry.Candidate.Kind == TopBarSearchItemKind.Folder);
        }

        public LauncherPanelEntry Entry { get; }
        public bool IsDockPinned { get; }
        public bool IsFavorite
        {
            get => _isFavorite;
            private set
            {
                if (_isFavorite == value) return;
                _isFavorite = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FavoriteGlyph));
                OnPropertyChanged(nameof(FavoriteToolTip));
            }
        }
        public bool CanToggleFavorite
        {
            get => _canToggleFavorite;
            private set { if (_canToggleFavorite != value) { _canToggleFavorite = value; OnPropertyChanged(); } }
        }
        public bool CanManageGroups => _canManageGroups;
        public bool HasLocalPath => LocalPath(Entry) is not null;
        public string FavoriteGlyph => _isFavorite ? "\uE735" : "\uE734";
        public string FavoriteToolTip => _isFavorite ? "移出常用" : "加入常用";
        public bool IsSection => false;
        public IReadOnlyList<string> Sources { get; }
        public string? StartMenuFolder { get; }
        public IReadOnlyList<string> Keys { get; }
        public LauncherAppCategory AutomaticCategory { get; }
        public LauncherAppCategory EffectiveCategory { get; set; }
        public LauncherFileCategory FileCategory { get; }
        public string Name => Entry.Candidate.DisplayName;
        public string Glyph => Entry.Candidate.Kind == TopBarSearchItemKind.App
            ? "\uE71D" : LauncherFileCategoryRules.Glyph(FileCategory);
        public string Location => $"{Name}\n{string.Join("、", Sources)} · {TopBarSearchRules.FormatLocation(Entry.Candidate.Path, 76)}";

        public void RefreshFavorite(LauncherPreferencesState preferences, bool canManageGroups)
        {
            _canManageGroups = canManageGroups;
            CanToggleFavorite = preferences.FavoriteMode == LauncherFavoriteMode.Independent && canManageGroups;
            IsFavorite = preferences.FavoriteMode switch
            {
                LauncherFavoriteMode.FollowDock => IsDockPinned,
                LauncherFavoriteMode.Independent => preferences.IsIndependentFavorite(Keys),
                _ => false
            };
        }

        public string SecondaryText
        {
            get
            {
                var parts = new List<string>(4) { string.Join("、", Sources) };
                if (Entry.LastAccessedLocal is { } time)
                {
                    parts.Add(LauncherFileCategoryRules.FormatRecentTime(time, DateTime.Today));
                }
                if (Entry.FileSizeBytes is { } size)
                {
                    var formatted = LauncherFileCategoryRules.FormatFileSize(size);
                    if (formatted.Length > 0) parts.Add(formatted);
                }
                parts.Add(TopBarSearchRules.FormatLocation(Entry.Candidate.Path, parts.Count > 2 ? 42 : 52));
                return string.Join(" · ", parts);
            }
        }

        public string KindLabel => Entry.Candidate.Kind switch
        {
            TopBarSearchItemKind.App => LauncherAppCategoryRules.DisplayName(EffectiveCategory),
            TopBarSearchItemKind.Folder when CanDrillDown(Entry) => "展开目录 ›",
            _ => LauncherFileCategoryRules.DisplayName(FileCategory)
        };

        public ImageSource? Icon
        {
            get => _icon;
            set { _icon = value; OnPropertyChanged(); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
