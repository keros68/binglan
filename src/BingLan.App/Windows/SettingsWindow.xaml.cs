using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.App.Themes;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Themes;
using BingLan.Core.Services;
using BingLan.Core.TopBar;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Control = System.Windows.Controls.Control;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;

namespace BingLan.App.Windows;

public partial class SettingsWindow : Window
{
    private static readonly DesktopComponentKind[] CurrentComponentKinds =
    [
        DesktopComponentKind.TimeDate,
        DesktopComponentKind.Greeting,
        DesktopComponentKind.Weather,
        DesktopComponentKind.Performance,
        DesktopComponentKind.Todo,
        DesktopComponentKind.QuickLaunch
    ];

    private readonly CityLookupService _citySearchService;
    private readonly Action<string, bool> _applyDisplaySettings;
    private readonly Action<CitySearchResult> _applyCity;
    private readonly Action<DesktopExperienceState> _applyDesktopExperience;
    private readonly Action<DesktopExperienceState> _applyLayoutPreset;
    private readonly Func<IReadOnlyList<WidgetWindowBase>> _widgetProvider;
    private readonly Action<WidgetWindowBase, bool> _applyWidgetLock;
    private readonly DesktopExperienceState _desktopExperience;
    private readonly InformationWidgetState _informationState;
    private readonly DockState _dockState;
    private readonly Action _applyDock;
    private readonly TaskbarState _taskbarState;
    private readonly Action _applyTaskbar;
    private readonly Func<(TaskbarMode ActiveMode, string Problem)> _taskbarStatus;
    private readonly SettingsMaintenance _maintenance;
    private readonly TopBarState? _topBarState;
    private readonly Action? _applyTopBar;
    private readonly Func<string, GlobalHotkeyProbeResult>? _probeTopBarSearchHotkey;
    private DesktopComponentSettingsEntry? _selectedComponentEntry;
    private CancellationTokenSource? _searchCancellation;
    private bool _loadingExperience;
    private bool _loadingComponentEditor;
    private bool _loadingDisplaySettings;
    // Settings apply as soon as they change; typed values wait for a short pause first.
    private readonly DispatcherTimer _componentApplyTimer =
        new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _displaySettingsTimer =
        new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _loadingDock;
    private bool _loadingTaskbar;
    private bool _loadingDesktopMode;
    private bool _loadingStartup;
    private bool _loadingCleanDesktop;
    private int _cleanDesktopRefreshVersion;
    private bool _loadingFileBoxAutomation;
    private bool _loadingStyle;
    private bool _loadingTopBar;
    private bool _loadingTopBarLook;
    private bool _topBarApplyQueued;
    private bool _presetPreviewQueued;
    private bool _initializingNavigation;
    private bool _isClosed;
    private DesktopLayoutPreset _previewPreset;

    public SettingsWindow(
        CityLookupService citySearchService,
        InformationWidgetState state,
        Action<string, bool> applyDisplaySettings,
        Action<CitySearchResult> applyCity)
        : this(
            citySearchService,
            state,
            DesktopExperienceRules.CreateDefault(),
            applyDisplaySettings,
            applyCity,
            _ => { })
    {
    }

    public SettingsWindow(
        CityLookupService citySearchService,
        InformationWidgetState state,
        DesktopExperienceState desktopExperience,
        Action<string, bool> applyDisplaySettings,
        Action<CitySearchResult> applyCity,
        Action<DesktopExperienceState> applyDesktopExperience,
        Action<DesktopExperienceState>? applyLayoutPreset = null,
        Func<IReadOnlyList<WidgetWindowBase>>? widgetProvider = null,
        Action<WidgetWindowBase, bool>? applyWidgetLock = null,
        DockState? dockState = null,
        Action? applyDock = null,
        TaskbarState? taskbarState = null,
        Action? applyTaskbar = null,
        Func<(TaskbarMode ActiveMode, string Problem)>? taskbarStatus = null,
        SettingsMaintenance? maintenance = null,
        TopBarState? topBarState = null,
        Action? applyTopBar = null,
        Func<string, GlobalHotkeyProbeResult>? probeTopBarSearchHotkey = null)
    {
        AccessibilityThemeManager.EnsureInitialized();
        _citySearchService = citySearchService;
        _applyDisplaySettings = applyDisplaySettings;
        _applyCity = applyCity;
        _applyDesktopExperience = applyDesktopExperience;
        _applyLayoutPreset = applyLayoutPreset ?? applyDesktopExperience;
        _widgetProvider = widgetProvider ?? (() => []);
        _applyWidgetLock = applyWidgetLock ?? ((_, _) => { });
        _desktopExperience = desktopExperience;
        _informationState = state;
        DesktopExperienceRules.Normalize(_desktopExperience);
        _dockState = dockState ?? new DockState();
        _applyDock = applyDock ?? (() => { });
        _taskbarState = taskbarState ?? new TaskbarState();
        _applyTaskbar = applyTaskbar ?? (() => { });
        _taskbarStatus = taskbarStatus ?? (() => (TaskbarMode.SystemDefault, string.Empty));
        _maintenance = maintenance ?? new SettingsMaintenance();
        _topBarState = topBarState;
        _applyTopBar = applyTopBar;
        _probeTopBarSearchHotkey = probeTopBarSearchHotkey;

        InitializeComponent();
        ApplyTheme();
        SourceInitialized += (_, _) =>
        {
            ApplyTheme();
            ApplyHostWorkAreaConstraint(recenter: true);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(ThemeMessageHook);
        };
        AccessibilityThemeManager.HighContrastChanged += ReapplyTheme;
        AccessibilityThemeManager.DarkModeOverrideChanged += ReapplyTheme;
        Closed += (_, _) =>
        {
            AccessibilityThemeManager.HighContrastChanged -= ReapplyTheme;
            AccessibilityThemeManager.DarkModeOverrideChanged -= ReapplyTheme;
        };
        InitializeQuickActions();
        PaletteSwatches.Fill(GlassPaletteSwatches, palette =>
        {
            _maintenance.Style.Palette = palette.Key;
            _maintenance.Style.Glass = GlassMode.Light;
            _maintenance.ApplyGlass();
            LoadStyle();
        });
        PaletteSwatches.Fill(DockPaletteSwatches, palette =>
        {
            _dockState.FollowCardLook = false;
            _dockState.SurfaceColor = palette.Body;
            ApplyDockChange($"Dock 已改为{palette.Name}配色");
            LoadDockLook();
        });
        PaletteSwatches.Fill(ComponentPaletteSwatches, palette =>
        {
            SelectedComponentBackgroundColorEditor.Text = palette.Body;
            SelectedComponentTextColorEditor.Text = palette.Text;
            ScheduleComponentApply();
        });
        SelectedComponentFontPicker.ItemsSource = new[] { DefaultFontLabel }.Concat(InstalledFontCatalog.Names).ToList();
        _componentApplyTimer.Tick += (_, _) =>
        {
            _componentApplyTimer.Stop();
            ApplySelectedComponent(refreshList: false);
        };
        _displaySettingsTimer.Tick += (_, _) =>
        {
            _displaySettingsTimer.Stop();
            ApplyDisplaySettings();
        };

        _loadingDisplaySettings = true;
        GreetingNameEditor.Text = state.GreetingName;
        Use24HourClockCheckBox.IsChecked = state.Use24HourClock;
        ShowClockDateCheckBox.IsChecked = state.ShowDate;
        SelectByTag(ClockLayoutSelector, state.ClockLayout.ToString());
        _loadingDisplaySettings = false;
        UpdateCurrentCity(state.WeatherCity);
        LoadDesktopExperience();
        RefreshComponentList();
        LoadDockSettings();
        InitializeLauncherLook();
        if (_topBarState is null)
        {
            // Tests and older call sites construct the window without a top bar; every
            // place that shows or changes it stays out of sight.
            TopBarNavItem.Visibility = Visibility.Collapsed;
            TopBarPage.Visibility = Visibility.Collapsed;
            TopBarSection.Visibility = Visibility.Collapsed;
            LauncherSearchHotkeyRow.Visibility = Visibility.Collapsed;
            SchemeTopBarRow.Visibility = Visibility.Collapsed;
            AppearanceTopBarPartRow.Visibility = Visibility.Collapsed;
            AppearanceTopBarPartEditor.Visibility = Visibility.Collapsed;
        }
        else
        {
            LoadTopBarSettings();
        }
        LoadTaskbarSettings();
        LoadDesktopMode();
        LoadPrivacySettings();
        LoadUpdateSettings();
        RefreshCleanDesktopSettings();
        LoadFileBoxAutomation();
        LoadGlobalHotkeys();
        LoadStyle();
        RefreshBackupList();
        InitializeComponentsPageEnhancements();
        _initializingNavigation = true;
        try
        {
            var initialPage = SettingsNavigationRules.NormalizePageKey(
                _maintenance.GetLastSettingsPage?.Invoke(),
                topBarAvailable: _topBarState is not null);
            SelectNavigationPage(initialPage);
        }
        finally
        {
            _initializingNavigation = false;
        }
        Closing += (_, _) => CommitPendingChanges();
        Closed += (_, _) => { _isClosed = true; InvalidatePendingReveal(); StopQuickActionTimers(); CancelSearch(); UnsubscribeObservedWidgetTitles(); };
        // The index reads the finished pages once, after the window has shown.
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _ = SearchIndex);
        Deactivated += (_, _) => SettingsSearchPopup.IsOpen = false;
        // Back from Windows' own taskbar settings: show what is there now.
        Activated += (_, _) => LoadTaskbarSettings();
        LocationChanged += (_, _) => SettingsSearchPopup.IsOpen = false;
        SystemParameters.StaticPropertyChanged += SystemParameters_Changed;
        DpiChanged += SettingsWindow_DpiChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        Closed += (_, _) =>
        {
            SystemParameters.StaticPropertyChanged -= SystemParameters_Changed;
            DpiChanged -= SettingsWindow_DpiChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        };
        if (_maintenance.Updater is { } updater)
        {
            updater.Changed += RefreshUpdateView;
            Closed += (_, _) => updater.Changed -= RefreshUpdateView;
        }
        ApplyWorkAreaConstraint(ResolveHostWorkAreaDip(), recenter: true);
    }

    /// <summary>Whether the window shows the Mica material instead of the solid page colour.</summary>
    internal bool IsBackdropActive { get; private set; }

    /// <summary>Whether the window uses its dark palette, following the Windows app mode.</summary>
    internal bool IsDarkTheme { get; private set; }

    private const int WmSettingChange = 0x001A;

    private void ReapplyTheme(object? sender, EventArgs e) => ApplyTheme();

    /// <summary>
    /// Light or dark with the Windows app mode, translucent over Mica and solid without
    /// it, and system colours in high contrast. The palette lives in this window's own
    /// resources, so the cards and other dialogs keep their colours.
    /// </summary>
    private void ApplyTheme()
    {
        IsDarkTheme = AccessibilityThemeManager.IsDarkAppMode && !AccessibilityThemeManager.IsHighContrastEnabled;
        IsBackdropActive = SettingsBackdrop.Apply(this, IsDarkTheme);
        SettingsPalette.Apply(
            Resources,
            AccessibilityThemeManager.IsHighContrastEnabled,
            IsDarkTheme,
            translucent: IsBackdropActive,
            includeShared: true);
    }

    // Switching the app mode or the transparency effects arrives as ImmersiveColorSet.
    private nint ThemeMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmSettingChange
            && lParam != 0
            && System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            Dispatcher.BeginInvoke(ApplyTheme);
        }
        return 0;
    }

    private void SystemParameters_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.WorkArea))
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!_isClosed)
                {
                    ApplyHostWorkAreaConstraint(recenter: false);
                }
                UpdatePresetPreview(GetSelectedPreset());
            });
        }
    }

    private void SettingsWindow_DpiChanged(object sender, DpiChangedEventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (!_isClosed)
            {
                ApplyHostWorkAreaConstraint(recenter: false);
            }
        });

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (!_isClosed)
            {
                ApplyHostWorkAreaConstraint(recenter: false);
            }
        });

    internal void ApplyWorkAreaConstraint(Rect workArea, bool recenter)
    {
        var bounds = SettingsWindowSizingRules.Resolve(
            workArea.Left,
            workArea.Top,
            workArea.Width,
            workArea.Height);

        MinWidth = bounds.MinWidth;
        MinHeight = bounds.MinHeight;

        if (recenter)
        {
            Width = bounds.Width;
            Height = bounds.Height;
            Left = bounds.Left;
            Top = bounds.Top;
            return;
        }

        if (WindowState != WindowState.Normal)
        {
            return;
        }

        var safeLeft = double.IsFinite(workArea.Left) ? workArea.Left : bounds.Left;
        var safeTop = double.IsFinite(workArea.Top) ? workArea.Top : bounds.Top;
        var safeWidth = double.IsFinite(workArea.Width) && workArea.Width > 0d
            ? workArea.Width
            : bounds.Width;
        var safeHeight = double.IsFinite(workArea.Height) && workArea.Height > 0d
            ? workArea.Height
            : bounds.Height;

        if (!double.IsFinite(Width) || Width > safeWidth)
        {
            Width = Math.Max(bounds.MinWidth, Math.Min(SettingsWindowSizingRules.DefaultWidth, safeWidth));
        }
        if (!double.IsFinite(Height) || Height > safeHeight)
        {
            Height = Math.Max(bounds.MinHeight, Math.Min(SettingsWindowSizingRules.DefaultHeight, safeHeight));
        }

        var currentWidth = double.IsFinite(Width) ? Width : bounds.Width;
        var currentHeight = double.IsFinite(Height) ? Height : bounds.Height;
        var maxLeft = safeLeft + Math.Max(0d, safeWidth - currentWidth);
        var maxTop = safeTop + Math.Max(0d, safeHeight - currentHeight);
        Left = double.IsFinite(Left) ? Math.Clamp(Left, safeLeft, maxLeft) : bounds.Left;
        Top = double.IsFinite(Top) ? Math.Clamp(Top, safeTop, maxTop) : bounds.Top;
    }

    private void ApplyHostWorkAreaConstraint(bool recenter)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var monitor = ResolveHostMonitor(handle);
        if (handle == 0 || monitor is null || !monitor.WorkingArea.HasArea)
        {
            ApplyWorkAreaConstraint(ResolveHostWorkAreaDip(), recenter);
            return;
        }

        var scale = monitor.Dpi > 0 ? monitor.Dpi / 96d : 1d;
        var areaWidthDip = monitor.WorkingArea.Width / scale;
        var areaHeightDip = monitor.WorkingArea.Height / scale;
        var bounds = SettingsWindowSizingRules.Resolve(0d, 0d, areaWidthDip, areaHeightDip);

        MinWidth = bounds.MinWidth;
        MinHeight = bounds.MinHeight;

        if (!recenter && WindowState != WindowState.Normal)
        {
            return;
        }

        var workArea = monitor.WorkingArea;
        var targetWidthPx = Math.Clamp((int)Math.Round(bounds.Width * scale), 1, workArea.Width);
        var targetHeightPx = Math.Clamp((int)Math.Round(bounds.Height * scale), 1, workArea.Height);
        int targetLeftPx;
        int targetTopPx;

        if (recenter || !DockNativeMethods.GetWindowRect(handle, out var current))
        {
            targetLeftPx = workArea.Left + Math.Max(0, (workArea.Width - targetWidthPx) / 2);
            targetTopPx = workArea.Top + Math.Max(0, (workArea.Height - targetHeightPx) / 2);
        }
        else
        {
            var currentWidthPx = Math.Max(1, current.Right - current.Left);
            var currentHeightPx = Math.Max(1, current.Bottom - current.Top);
            var minWidthPx = Math.Max(1, (int)Math.Ceiling(bounds.MinWidth * scale));
            var minHeightPx = Math.Max(1, (int)Math.Ceiling(bounds.MinHeight * scale));
            targetWidthPx = currentWidthPx > workArea.Width
                ? Math.Clamp(Math.Max(minWidthPx, targetWidthPx), 1, workArea.Width)
                : currentWidthPx;
            targetHeightPx = currentHeightPx > workArea.Height
                ? Math.Clamp(Math.Max(minHeightPx, targetHeightPx), 1, workArea.Height)
                : currentHeightPx;
            var maxLeftPx = workArea.Left + Math.Max(0, workArea.Width - targetWidthPx);
            var maxTopPx = workArea.Top + Math.Max(0, workArea.Height - targetHeightPx);
            targetLeftPx = Math.Clamp(current.Left, workArea.Left, maxLeftPx);
            targetTopPx = Math.Clamp(current.Top, workArea.Top, maxTopPx);
        }

        var targetRect = new BingLan.Core.Dock.PixelRect(
            targetLeftPx,
            targetTopPx,
            targetLeftPx + targetWidthPx,
            targetTopPx + targetHeightPx);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (DockNativeMethods.GetWindowRect(handle, out var actual)
                && new BingLan.Core.Dock.PixelRect(actual.Left, actual.Top, actual.Right, actual.Bottom) == targetRect)
            {
                break;
            }

            NativeMethods.SetWindowPos(
                handle,
                0,
                targetRect.Left,
                targetRect.Top,
                targetRect.Width,
                targetRect.Height,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        }
    }

    private MonitorSnapshot? ResolveHostMonitor(nint handle)
    {
        var monitors = MonitorCatalog.GetAll();
        if (monitors.Count == 0)
        {
            return null;
        }

        MonitorSnapshot? monitor = null;
        if (handle != 0)
        {
            var monitorHandle = DockNativeMethods.MonitorFromWindow(
                handle,
                DockNativeMethods.MonitorDefaultToNearest);
            monitor = monitors.FirstOrDefault(candidate => candidate.MonitorHandle == monitorHandle);
        }
        else if (DockNativeMethods.GetCursorPos(out var cursor))
        {
            monitor = monitors.FirstOrDefault(candidate =>
                cursor.X >= candidate.Bounds.Left
                && cursor.X < candidate.Bounds.Right
                && cursor.Y >= candidate.Bounds.Top
                && cursor.Y < candidate.Bounds.Bottom);
        }

        return monitor ?? monitors.FirstOrDefault(candidate => candidate.IsPrimary) ?? monitors[0];
    }

    private Rect ResolveHostWorkAreaDip()
    {
        var monitor = ResolveHostMonitor(new WindowInteropHelper(this).Handle);
        if (monitor is null)
        {
            return SystemParameters.WorkArea;
        }

        var scale = monitor.Dpi > 0 ? monitor.Dpi / 96d : 1d;
        return new Rect(
            monitor.WorkingArea.Left / scale,
            monitor.WorkingArea.Top / scale,
            monitor.WorkingArea.Width / scale,
            monitor.WorkingArea.Height / scale);
    }

    private void LoadDesktopExperience()
    {
        _loadingExperience = true;
        try
        {
            QuietInformationPresetRadio.IsChecked =
                _desktopExperience.ActivePreset == DesktopLayoutPreset.QuietInformation;
            GlacierWorkbenchPresetRadio.IsChecked =
                _desktopExperience.ActivePreset == DesktopLayoutPreset.GlacierWorkbench;
            TransparentApplePresetRadio.IsChecked =
                _desktopExperience.ActivePreset == DesktopLayoutPreset.TransparentApple;
            CenterClockPresetRadio.IsChecked =
                _desktopExperience.ActivePreset == DesktopLayoutPreset.CenterClock;
            UpdatePresetPreview(_desktopExperience.ActivePreset);
        }
        finally
        {
            _loadingExperience = false;
        }
    }

    private void DesktopComponentList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_suppressComponentSelectionReload
            || DesktopComponentList?.SelectedItem is not DesktopComponentSettingsEntry entry
            || SelectedComponentVisibleCheckBox is null)
        {
            return;
        }

        // A change typed for the previous card is saved before its editor is replaced.
        CommitPendingTitleEdits();
        if (_componentApplyTimer.IsEnabled)
        {
            _componentApplyTimer.Stop();
            ApplySelectedComponent(refreshList: false);
        }
        _selectedComponentEntry = entry;
        ScrollSelectedComponentIntoView(entry);
        if (entry.Kind is { } kind)
        {
            SelectedWidgetAppearanceHost.Content = null;
            FileBoxCollectPanel.Visibility = Visibility.Collapsed;
            FileBoxDesktopHintPanel.Visibility = Visibility.Collapsed;
            SelectedWidgetHeaderBar.Visibility = Visibility.Collapsed;
            SelectedWidgetEditorPanel.Visibility = Visibility.Collapsed;
            BuiltInComponentHeaderBar.Visibility = Visibility.Visible;
            BuiltInComponentEditorPanel.Visibility = Visibility.Visible;
            LoadSelectedComponentEditor(kind, entry.Window);
            UpdateBuiltInHeaderBarState(kind, entry);
            // A card with its own colours opens its fold, so what was set is in view.
            ResetComponentMoreFolds();
            ComponentOwnLookFold.IsExpanded = UpdateOwnLookSummary(kind);
            // 待办卡片自身走内置组件分支（DesktopExperience 有 Todo 组件项），
            // 周历设置面板必须在这里点亮；其余内置组件保持隐藏。
            if (kind == DesktopComponentKind.Todo && entry.Window is TodoWidgetWindow todoWindow)
            {
                LoadTodoSettings(todoWindow);
            }
            else
            {
                TodoSettingsPanel.Visibility = Visibility.Collapsed;
            }
            DisplayContentSettingsCard.Visibility = kind is DesktopComponentKind.TimeDate or
                DesktopComponentKind.Greeting
                ? Visibility.Visible
                : Visibility.Collapsed;
            var isTimeDate = kind == DesktopComponentKind.TimeDate;
            ShowClockDateRow.Visibility = isTimeDate ? Visibility.Visible : Visibility.Collapsed;
            ClockLayoutRow.Visibility = isTimeDate ? Visibility.Visible : Visibility.Collapsed;
            ClockDividerRow.Visibility = isTimeDate ? Visibility.Visible : Visibility.Collapsed;
            ComponentMoreFold.Visibility = isTimeDate ? Visibility.Visible : Visibility.Collapsed;
            _loadingDisplaySettings = true;
            ShowClockDateCheckBox.IsChecked = _informationState.ShowDate;
            SelectByTag(ClockLayoutSelector, ClockLayoutRules.CoercePreset(_informationState.ClockLayout).ToString());
            _loadingDisplaySettings = false;
            _loadingDivider = true;
            var clock = _desktopExperience.GetComponent(DesktopComponentKind.TimeDate);
            ClockDividerCheckBox.IsChecked = clock.ShowDivider;
            ClockDividerThicknessSlider.Value = clock.DividerThickness;
            ClockDividerThicknessText.Text = $"{clock.DividerThickness:0.#} px";
            _loadingDivider = false;
            WeatherContentSettingsCard.Visibility = kind == DesktopComponentKind.Weather
                ? Visibility.Visible
                : Visibility.Collapsed;
            QuickPlacesSettingsCard.Visibility = kind == DesktopComponentKind.QuickLaunch
                ? Visibility.Visible
                : Visibility.Collapsed;
            // Quick launch shows only icon glyphs, and only the weather and performance
            // cards lay out by density, so the other components hide options that do nothing.
            SelectedComponentFontRow.Visibility = kind == DesktopComponentKind.QuickLaunch
                ? Visibility.Collapsed
                : Visibility.Visible;
            SelectedComponentDensityRow.Visibility = kind is DesktopComponentKind.Weather or
                DesktopComponentKind.Performance
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (kind == DesktopComponentKind.QuickLaunch)
            {
                _quickPlaces = _maintenance.QuickPlaces()
                    .Select(item => new QuickPlaceState { Icon = item.Icon, Name = item.Name, Target = item.Target })
                    .ToList();
                ShowQuickPlaces(0);
            }
            ComponentDetailsScrollViewer.ScrollToTop();
            return;
        }

        if (entry.Window is null)
        {
            return;
        }

        var panel = new WidgetAppearancePanel { SharedStyle = _maintenance.Style };
        panel.Attach(entry.Window);
        panel.AppearanceEdited += () =>
        {
            if (ReferenceEquals(_selectedComponentEntry?.Window, entry.Window))
            {
                UpdateSelectedWidgetOwnLookSummary(entry.Window);
                RefreshAppearanceOverrides();
            }
        };
        var fileBox = entry.Window as FileBoxWindow;
        LoadFileBoxViewMode(fileBox);
        LoadFileBoxCollect(fileBox);
        LoadTodoSettings(entry.Window as TodoWidgetWindow);
        SelectedWidgetAppearanceHost.Content = panel;
        UpdateSelectedWidgetHeaderBarState(entry);
        ResetComponentMoreFolds();
        SelectedWidgetMoreFold.Visibility = fileBox is not null ? Visibility.Visible : Visibility.Collapsed;
        SelectedWidgetOwnLookFold.IsExpanded = UpdateSelectedWidgetOwnLookSummary(entry.Window);
        BuiltInComponentHeaderBar.Visibility = Visibility.Collapsed;
        BuiltInComponentEditorPanel.Visibility = Visibility.Collapsed;
        SelectedWidgetHeaderBar.Visibility = Visibility.Visible;
        SelectedWidgetEditorPanel.Visibility = Visibility.Visible;
        DisplayContentSettingsCard.Visibility = Visibility.Collapsed;
        WeatherContentSettingsCard.Visibility = Visibility.Collapsed;
        QuickPlacesSettingsCard.Visibility = Visibility.Collapsed;
        ComponentDetailsScrollViewer.ScrollToTop();
    }

    private bool IsSecondaryTodoWindow(
        WidgetWindowBase? window,
        [NotNullWhen(true)] out TodoWidgetWindow? todoWindow)
    {
        var primaryId = _maintenance.PrimaryTodoId()
            ?? _widgetProvider().OfType<TodoWidgetWindow>().FirstOrDefault()?.State.Id;
        if (window is TodoWidgetWindow candidate
            && primaryId is { } id
            && id != candidate.State.Id)
        {
            todoWindow = candidate;
            return true;
        }

        todoWindow = null;
        return false;
    }

    private void LoadSelectedComponentEditor(
        DesktopComponentKind kind,
        WidgetWindowBase? targetWindow)
    {
        _loadingComponentEditor = true;
        try
        {
            var component = _desktopExperience.GetComponent(kind);
            TodoWidgetWindow? secondaryTodo = null;
            var isSecondaryTodo = kind == DesktopComponentKind.Todo
                && IsSecondaryTodoWindow(targetWindow, out secondaryTodo);
            var appearance = isSecondaryTodo ? secondaryTodo!.State.Appearance : component.Appearance;
            var fontScale = isSecondaryTodo ? secondaryTodo!.State.FontScale : component.FontScale;
            var fontFamily = isSecondaryTodo ? secondaryTodo!.State.FontFamily : component.FontFamily;
            var cornerRadius = isSecondaryTodo ? secondaryTodo!.State.CornerRadius : component.CornerRadius;
            var placement = isSecondaryTodo ? secondaryTodo!.State.Placement : component.Placement;

            SelectedComponentVisibleCheckBox.IsChecked = component.IsVisible;
            SelectedComponentLockedCheckBox.IsChecked =
                targetWindow?.IsWidgetLocked ?? component.IsLocked;
            SelectedComponentFontScaleSlider.Value = fontScale;
            SelectedComponentFontPicker.SelectedItem = fontFamily is { } font
                ? InstalledFontCatalog.Resolve(font)
                : DefaultFontLabel;
            SelectedComponentTextColorEditor.Text = appearance.TextColor;
            SelectedComponentBackgroundColorEditor.Text =
                appearance.BackgroundColor;
            SelectedComponentOpacitySlider.Value = appearance.BackgroundOpacity;
            // 标题栏设置只在有标题栏的组件（待办卡片）上显示；裸文字信息卡没有
            // 这一层，隐藏不等于不可调。
            SelectedComponentHeaderRows.Visibility = kind == DesktopComponentKind.Todo
                ? Visibility.Visible
                : Visibility.Collapsed;
            SelectedComponentHeaderColorEditor.Text = appearance.HeaderColor;
            SelectedComponentHeaderOpacitySlider.Value = appearance.HeaderOpacity;
            SelectedComponentCornerRadiusSlider.Value = cornerRadius;
            SelectedComponentWidthEditor.Text = $"{placement.Width:0.#}";
            SelectedComponentHeightEditor.Text = $"{placement.Height:0.#}";
            SelectedComponentDensitySelector.SelectedIndex = component.Density switch
            {
                DesktopInformationDensity.Comfortable => 0,
                DesktopInformationDensity.Compact => 2,
                _ => 1
            };
            // The segment shows which material this card wears; custom colours read as
            // the nearer of light and dark glass.
            var inferredLook = InferLook(appearance);
            ComponentLookLightRadio.IsChecked = inferredLook == GlassMode.Light;
            ComponentLookDarkRadio.IsChecked = inferredLook == GlassMode.Dark;
            ComponentLookClearRadio.IsChecked = inferredLook == GlassMode.Clear;
            RefreshComponentColorChips();
            UpdateComponentStyleValueText();
            SelectedComponentHeadingText.Text =
                _selectedComponentEntry?.Name ?? DesktopExperienceRules.GetComponentName(kind);
        }
        finally
        {
            _loadingComponentEditor = false;
        }
    }

    private void ScheduleComponentApply()
    {
        if (_loadingComponentEditor || _selectedComponentEntry?.Kind is null)
        {
            return;
        }
        _componentApplyTimer.Stop();
        _componentApplyTimer.Start();
    }

    /// <summary>Applies any change still waiting for its typing pause.</summary>
    public void CommitPendingChanges()
    {
        CommitPendingTitleEdits();
        if (_componentApplyTimer.IsEnabled)
        {
            _componentApplyTimer.Stop();
            ApplySelectedComponent(refreshList: false);
        }
        if (_displaySettingsTimer.IsEnabled)
        {
            _displaySettingsTimer.Stop();
            ApplyDisplaySettings();
        }
    }

    private void ComponentEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        // The swatches follow the boxes so a typed colour shows before it applies.
        RefreshComponentColorChips();
        ScheduleComponentApply();
    }

    private const string DefaultFontLabel = "默认（界面字体）";

    private void ComponentFont_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingComponentEditor && _selectedComponentEntry?.Kind is not null)
        {
            _componentApplyTimer.Stop();
            ApplySelectedComponent(refreshList: false);
        }
    }

    // One card takes light glass, dark glass or no backing without changing the others.
    // The segmented radios also show the current material while the editor loads.
    private void ComponentLook_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingComponentEditor
            || sender is not FrameworkElement { Tag: string tag }
            || !Enum.TryParse<GlassMode>(tag, out var mode))
        {
            return;
        }

        var look = CardLook(mode, _maintenance.Style, SelectedComponentOpacitySlider.Value);
        SelectedComponentBackgroundColorEditor.Text = look.BackgroundColor;
        SelectedComponentTextColorEditor.Text = look.TextColor;
        SelectedComponentOpacitySlider.Value = look.BackgroundOpacity;
        SelectedComponentHeaderColorEditor.Text = look.HeaderColor;
        SelectedComponentHeaderOpacitySlider.Value = look.HeaderOpacity;
        _componentApplyTimer.Stop();
        ApplySelectedComponent(refreshList: false);
    }

    /// <summary>
    /// The colours of one card in a material, keeping the card's opacity when it has a
    /// visible body and falling back to the shared glass density when it had none.
    /// </summary>
    internal static GlassAppearance CardLook(GlassMode mode, DesktopStyleState shared, double currentOpacity)
    {
        var density = mode == GlassMode.Clear || currentOpacity >= 0.05d ? currentOpacity : shared.GlassDensity;
        return DesktopStyleRules.Appearance(new DesktopStyleState
        {
            Glass = mode,
            Palette = shared.Palette,
            GlassDensity = density,
            TextInk = TextInk.Auto
        });
    }

    /// <summary>
    /// Which material a card currently wears, for the segment control's checked state;
    /// a custom colour reads as whichever of light or dark glass it is closest to.
    /// </summary>
    internal static GlassMode InferLook(WidgetAppearanceState look)
    {
        if (string.IsNullOrWhiteSpace(look.BackgroundColor) || look.BackgroundOpacity < 0.05d)
        {
            return GlassMode.Clear;
        }

        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                .ConvertFromString(look.BackgroundColor)!;
            var luminance = (0.2126d * color.R + 0.7152d * color.G + 0.0722d * color.B) / 255d;
            return luminance > 0.5d ? GlassMode.Light : GlassMode.Dark;
        }
        catch (FormatException)
        {
            return GlassMode.Light;
        }
    }

    // A colour dialog for one card's own colour field; cancel keeps the typed value.
    private void ComponentColorPicker_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string target })
        {
            return;
        }

        var editor = target switch
        {
            "Background" => SelectedComponentBackgroundColorEditor,
            "Header" => SelectedComponentHeaderColorEditor,
            _ => SelectedComponentTextColorEditor
        };
        var current = System.Windows.Media.Colors.White;
        try
        {
            if (!string.IsNullOrWhiteSpace(editor.Text))
            {
                current = (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                    .ConvertFromString(editor.Text)!;
            }
        }
        catch (FormatException)
        {
        }

        if (!NativeColorDialog.TryPickColor(this, current, out var chosen))
        {
            return;
        }

        editor.Text = chosen;
    }

    private void RefreshComponentColorChips()
    {
        if (SelectedComponentTextColorChip is null)
        {
            return;
        }

        SetColorChip(SelectedComponentTextColorChip, SelectedComponentTextColorEditor.Text);
        SetColorChip(SelectedComponentBackgroundChip, SelectedComponentBackgroundColorEditor.Text);
        SetColorChip(SelectedComponentHeaderChip, SelectedComponentHeaderColorEditor.Text);
    }

    private static void SetColorChip(Border chip, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            chip.Background = null;
            return;
        }

        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value)!;
            var brush = new System.Windows.Media.SolidColorBrush(color);
            brush.Freeze();
            chip.Background = brush;
        }
        catch (FormatException)
        {
            // Keep the last valid swatch while a colour is still being typed.
        }
    }

    private void ComponentDensity_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingComponentEditor && _selectedComponentEntry?.Kind is not null)
        {
            _componentApplyTimer.Stop();
            ApplySelectedComponent(refreshList: false);
        }
    }

    // Showing or locking a card changes its label in the list, so the list is refreshed.
    private void ComponentToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loadingComponentEditor && _selectedComponentEntry?.Kind is not null)
        {
            _componentApplyTimer.Stop();
            ApplySelectedComponent(refreshList: true);
        }
    }

    private void DisplaySettings_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _loadingDisplaySettings)
        {
            return;
        }
        _displaySettingsTimer.Stop();
        if (sender is CheckBox)
        {
            ApplyDisplaySettings();
        }
        else
        {
            _displaySettingsTimer.Start();
        }
    }

    private void ClockLayout_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _loadingDisplaySettings)
        {
            return;
        }
        _displaySettingsTimer.Stop();
        ApplyDisplaySettings();
    }

    // The quick-launch icons being edited. An added icon stays here until it has a
    // location; the saved list only holds icons that open something.
    private List<QuickPlaceState> _quickPlaces = [];
    private bool _loadingQuickPlace;

    private sealed record QuickPlaceRow(string Glyph, string Name, string Target);

    private void ShowQuickPlaces(int select)
    {
        QuickPlaceList.ItemsSource = _quickPlaces
            .Select(item => new QuickPlaceRow(QuickPlaceRules.GetIcon(item.Icon).Glyph, item.Name, DescribeTarget(item.Target)))
            .ToList();
        QuickPlaceIconPicker.ItemsSource ??= QuickPlaceRules.Icons;
        QuickPlaceList.SelectedIndex = _quickPlaces.Count == 0 ? -1 : Math.Clamp(select, 0, _quickPlaces.Count - 1);
        LoadQuickPlaceEditor();
    }

    private static string DescribeTarget(string target) => target switch
    {
        "" => "（还没有选择位置）",
        _ when target.StartsWith(QuickPlaceRules.KnownPrefix, StringComparison.OrdinalIgnoreCase) =>
            $"系统{QuickPlaceRules.GetIcon(target[QuickPlaceRules.KnownPrefix.Length..]).Name}文件夹",
        _ when target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) => "系统位置",
        _ => target
    };

    private QuickPlaceState? SelectedQuickPlace =>
        QuickPlaceList.SelectedIndex is var index && index >= 0 && index < _quickPlaces.Count ? _quickPlaces[index] : null;

    private void LoadQuickPlaceEditor()
    {
        _loadingQuickPlace = true;
        var item = SelectedQuickPlace;
        QuickPlaceEditor.IsEnabled = item is not null;
        QuickPlaceIconPicker.SelectedItem = item is null ? null : QuickPlaceRules.GetIcon(item.Icon);
        QuickPlaceNameEditor.Text = item?.Name ?? string.Empty;
        QuickPlaceTargetEditor.Text = item is null || item.Target.StartsWith(QuickPlaceRules.KnownPrefix, StringComparison.OrdinalIgnoreCase)
            || item.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
            ? DescribeTarget(item?.Target ?? string.Empty)
            : item.Target;
        _loadingQuickPlace = false;
    }

    private void SaveQuickPlaces()
    {
        var index = QuickPlaceList.SelectedIndex;
        _maintenance.SetQuickPlaces(_quickPlaces
            .Select(item => new QuickPlaceState { Icon = item.Icon, Name = item.Name, Target = item.Target })
            .ToList());
        ShowQuickPlaces(index);
        ComponentSettingsStatusText.Text = "快捷入口已更新";
    }

    private void QuickPlaceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingQuickPlace)
        {
            LoadQuickPlaceEditor();
        }
    }

    private void QuickPlaceIcon_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingQuickPlace || SelectedQuickPlace is not { } item || QuickPlaceIconPicker.SelectedItem is not QuickPlaceIcon icon)
        {
            return;
        }
        item.Icon = icon.Key;
        SaveQuickPlaces();
    }

    private void QuickPlaceName_Commit(object sender, RoutedEventArgs e)
    {
        if (_loadingQuickPlace || SelectedQuickPlace is not { } item || item.Name == QuickPlaceNameEditor.Text.Trim())
        {
            return;
        }
        item.Name = QuickPlaceNameEditor.Text.Trim();
        SaveQuickPlaces();
    }

    // A typed path replaces the location; the friendly text shown for a system place is
    // left alone.
    private void QuickPlaceTarget_Commit(object sender, RoutedEventArgs e)
    {
        var text = QuickPlaceTargetEditor.Text.Trim().Trim('"');
        if (_loadingQuickPlace || SelectedQuickPlace is not { } item || text.Length == 0
            || text == item.Target || text == DescribeTarget(item.Target))
        {
            return;
        }
        item.Target = text;
        SaveQuickPlaces();
    }

    private void QuickPlaceEditor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }
        if (ReferenceEquals(sender, QuickPlaceNameEditor))
        {
            QuickPlaceName_Commit(sender, e);
        }
        else
        {
            QuickPlaceTarget_Commit(sender, e);
        }
        e.Handled = true;
    }

    private void QuickPlaceChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedQuickPlace is not { } item)
        {
            return;
        }
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择快捷入口打开的文件夹" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        SetQuickPlaceTarget(item, dialog.FolderName, Path.GetFileName(dialog.FolderName.TrimEnd(Path.DirectorySeparatorChar)));
    }

    private void QuickPlaceChooseFile_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedQuickPlace is not { } item)
        {
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择快捷入口打开的程序或文件",
            Filter = "程序 (*.exe;*.lnk)|*.exe;*.lnk|所有文件 (*.*)|*.*",
            DereferenceLinks = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        var name = Path.GetExtension(dialog.FileName).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            ? BingLan.App.Dock.DockAppResolver.GetFileDescription(dialog.FileName) ?? Path.GetFileNameWithoutExtension(dialog.FileName)
            : Path.GetFileNameWithoutExtension(dialog.FileName);
        SetQuickPlaceTarget(item, dialog.FileName, name);
    }

    // A new icon takes the chosen item's name; a named one keeps the name it was given.
    private void SetQuickPlaceTarget(QuickPlaceState item, string target, string name)
    {
        if (item.Target.Length == 0 && name.Length > 0)
        {
            item.Name = name[..Math.Min(name.Length, QuickPlaceRules.MaximumNameLength)];
        }
        item.Target = target;
        SaveQuickPlaces();
    }

    private void QuickPlaceAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_quickPlaces.Count >= QuickPlaceRules.MaximumItems)
        {
            ComponentSettingsStatusText.Text = $"快捷入口最多 {QuickPlaceRules.MaximumItems} 个";
            return;
        }
        _quickPlaces.Add(new QuickPlaceState { Icon = QuickPlaceRules.FolderIcon, Name = "新入口", Target = string.Empty });
        ShowQuickPlaces(_quickPlaces.Count - 1);
        ComponentSettingsStatusText.Text = "选择图标，再用“选文件夹…”或“选程序或文件…”指定位置";
    }

    private void QuickPlaceRemove_Click(object sender, RoutedEventArgs e)
    {
        if (QuickPlaceList.SelectedIndex is var index and >= 0 && index < _quickPlaces.Count)
        {
            _quickPlaces.RemoveAt(index);
            SaveQuickPlaces();
            ShowQuickPlaces(index);
        }
    }

    private void QuickPlaceUp_Click(object sender, RoutedEventArgs e) => MoveQuickPlace(-1);

    private void QuickPlaceDown_Click(object sender, RoutedEventArgs e) => MoveQuickPlace(1);

    private void MoveQuickPlace(int offset)
    {
        var index = QuickPlaceList.SelectedIndex;
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _quickPlaces.Count)
        {
            return;
        }
        (_quickPlaces[index], _quickPlaces[target]) = (_quickPlaces[target], _quickPlaces[index]);
        SaveQuickPlaces();
        ShowQuickPlaces(target);
    }

    private void QuickPlaceReset_Click(object sender, RoutedEventArgs e)
    {
        _quickPlaces = QuickPlaceRules.CreateDefaults();
        SaveQuickPlaces();
        ShowQuickPlaces(0);
    }

    private bool _loadingDivider;

    private void ClockDivider_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingDivider || ClockDividerThicknessText is null)
        {
            return;
        }
        var clock = _desktopExperience.GetComponent(DesktopComponentKind.TimeDate);
        clock.ShowDivider = ClockDividerCheckBox.IsChecked == true;
        clock.DividerThickness = ClockDividerThicknessSlider.Value;
        ClockDividerThicknessText.Text = $"{clock.DividerThickness:0.#} px";
        _applyDesktopExperience(_desktopExperience);
        DisplaySettingsStatusText.Text = "已保存";
    }

    private void ApplyDisplaySettings()
    {
        var greetingName = GreetingNameEditor.Text.Trim();
        var use24HourClock = Use24HourClockCheckBox.IsChecked == true;
        _informationState.GreetingName = greetingName;
        _informationState.Use24HourClock = use24HourClock;
        _informationState.ShowDate = ShowClockDateCheckBox.IsChecked == true;
        if (ClockLayoutSelector.SelectedItem is ComboBoxItem { Tag: string layoutTag }
            && Enum.TryParse<ClockLayoutPreset>(layoutTag, out var clockLayout))
        {
            _informationState.ClockLayout = ClockLayoutRules.CoercePreset(clockLayout);
        }

        _applyDisplaySettings(greetingName, use24HourClock);
        foreach (var information in _widgetProvider().OfType<InformationWidgetWindow>())
        {
            information.ApplyDesktopExperience(_desktopExperience);
            information.RefreshConfiguredValues(false);
        }
        DisplaySettingsStatusText.Text = "已保存";
    }

    private void ComponentStyleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loadingComponentEditor &&
            SelectedComponentFontScaleSlider is not null &&
            SelectedComponentFontScaleValueText is not null &&
            SelectedComponentOpacitySlider is not null &&
            SelectedComponentOpacityValueText is not null &&
            SelectedComponentHeaderOpacitySlider is not null &&
            SelectedComponentHeaderOpacityValueText is not null &&
            SelectedComponentCornerRadiusSlider is not null &&
            SelectedComponentCornerRadiusValueText is not null)
        {
            UpdateComponentStyleValueText();
            ScheduleComponentApply();
        }
    }

    private void UpdateComponentStyleValueText()
    {
        SelectedComponentFontScaleValueText.Text =
            $"{SelectedComponentFontScaleSlider.Value * 100:0}%";
        SelectedComponentOpacityValueText.Text =
            $"{SelectedComponentOpacitySlider.Value * 100:0}%";
        SelectedComponentHeaderOpacityValueText.Text =
            $"{SelectedComponentHeaderOpacitySlider.Value * 100:0}%";
        SelectedComponentCornerRadiusValueText.Text =
            $"{SelectedComponentCornerRadiusSlider.Value:0}px";
    }

    private void QuickTextColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            SelectedComponentTextColorEditor.Text = color;
        }
    }

    private void QuickBackgroundColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            SelectedComponentBackgroundColorEditor.Text = color;
        }
    }

    private void QuickHeaderColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            SelectedComponentHeaderColorEditor.Text = color;
        }
    }

    private void ApplySelectedComponent(bool refreshList)
    {
        if (_selectedComponentEntry?.Kind is not { } kind)
        {
            return;
        }

        var component = _desktopExperience.GetComponent(kind);
        var density = SelectedComponentDensitySelector.SelectedIndex switch
        {
            0 => DesktopInformationDensity.Comfortable,
            2 => DesktopInformationDensity.Compact,
            _ => DesktopInformationDensity.Standard
        };
        var (minimumWidth, minimumHeight) = GetMinimumComponentSize(
            kind,
            SelectedComponentFontScaleSlider.Value,
            density);
        var fontFamily = SelectedComponentFontPicker.SelectedItem is string font && font != DefaultFontLabel
            ? font
            : null;

        if (kind == DesktopComponentKind.Todo
            && IsSecondaryTodoWindow(_selectedComponentEntry.Window, out var secondaryTodo))
        {
            var visibilityChanged = component.IsVisible != (SelectedComponentVisibleCheckBox.IsChecked == true);
            component.IsVisible = SelectedComponentVisibleCheckBox.IsChecked == true;
            var todoState = secondaryTodo.State;
            todoState.FontScale = SelectedComponentFontScaleSlider.Value;
            todoState.FontFamily = fontFamily;
            todoState.Appearance.TextColor = WidgetAppearanceRules.CoerceTextColor(
                SelectedComponentTextColorEditor.Text);
            todoState.Appearance.BackgroundColor = WidgetAppearanceRules.CoerceBackgroundColor(
                SelectedComponentBackgroundColorEditor.Text);
            todoState.Appearance.BackgroundOpacity =
                WidgetAppearanceRules.CoerceBackgroundOpacity(
                    SelectedComponentOpacitySlider.Value);
            todoState.Appearance.HeaderColor = WidgetAppearanceRules.CoerceHeaderColor(
                SelectedComponentHeaderColorEditor.Text);
            todoState.Appearance.HeaderOpacity =
                WidgetAppearanceRules.CoerceHeaderOpacity(
                    SelectedComponentHeaderOpacitySlider.Value);
            todoState.CornerRadius = SelectedComponentCornerRadiusSlider.Value;
            todoState.Placement.Width = ParseSize(
                SelectedComponentWidthEditor.Text,
                todoState.Placement.Width,
                minimumWidth,
                1600d);
            todoState.Placement.Height = ParseSize(
                SelectedComponentHeightEditor.Text,
                todoState.Placement.Height,
                minimumHeight,
                1200d);

            secondaryTodo.SetWidgetBackgroundColor(todoState.Appearance.BackgroundColor);
            secondaryTodo.SetWidgetBackgroundOpacity(todoState.Appearance.BackgroundOpacity);
            secondaryTodo.SetWidgetHeaderColor(todoState.Appearance.HeaderColor);
            secondaryTodo.SetWidgetHeaderOpacity(todoState.Appearance.HeaderOpacity);
            secondaryTodo.SetWidgetTextColor(todoState.Appearance.TextColor);
            secondaryTodo.SetContentFontScale(todoState.FontScale);
            secondaryTodo.SetContentFontFamily(todoState.FontFamily);
            secondaryTodo.WidgetCornerRadius = todoState.CornerRadius;
            secondaryTodo.ApplyPlacement(todoState.Placement, SelectedComponentLockedCheckBox.IsChecked == true);
            if (visibilityChanged)
            {
                _applyDesktopExperience(_desktopExperience);
            }
            _applyWidgetLock(secondaryTodo, SelectedComponentLockedCheckBox.IsChecked == true);
        }
        else
        {
            component.IsVisible = SelectedComponentVisibleCheckBox.IsChecked == true;
            component.FontScale = SelectedComponentFontScaleSlider.Value;
            component.FontFamily = fontFamily;
            component.Appearance.TextColor = WidgetAppearanceRules.CoerceTextColor(
                SelectedComponentTextColorEditor.Text);
            component.Appearance.BackgroundColor = WidgetAppearanceRules.CoerceBackgroundColor(
                SelectedComponentBackgroundColorEditor.Text);
            component.Appearance.BackgroundOpacity =
                WidgetAppearanceRules.CoerceBackgroundOpacity(
                    SelectedComponentOpacitySlider.Value);
            component.Appearance.HeaderColor = WidgetAppearanceRules.CoerceHeaderColor(
                SelectedComponentHeaderColorEditor.Text);
            component.Appearance.HeaderOpacity =
                WidgetAppearanceRules.CoerceHeaderOpacity(
                    SelectedComponentHeaderOpacitySlider.Value);
            component.CornerRadius = SelectedComponentCornerRadiusSlider.Value;
            component.Placement.Width = ParseSize(
                SelectedComponentWidthEditor.Text,
                component.Placement.Width,
                minimumWidth,
                1600d);
            if (ShouldApplyEditedHeight(SelectedComponentHeightEditor.Text, component.Placement.Height))
            {
                component.Placement.Height = ParseSize(
                    SelectedComponentHeightEditor.Text,
                    component.Placement.Height,
                    minimumHeight,
                    1200d);
            }
            component.Density = density;
            if (_selectedComponentEntry.Window is { } targetWindow)
            {
                // 先应用组件状态、再应用锁定：ApplyWidgetLock 会触发窗口状态回写，
                // 把窗口当前尺寸写回组件 Placement。放在后面时窗口已拿到上面写入的
                // 新尺寸，回写与设置一致；放在前面则会用旧尺寸覆盖，卡片尺寸设置
                // 永远不生效。
                _applyDesktopExperience(_desktopExperience);
                _applyWidgetLock(
                    targetWindow,
                    SelectedComponentLockedCheckBox.IsChecked == true);
            }
            else
            {
                component.IsLocked = SelectedComponentLockedCheckBox.IsChecked == true;
                _applyDesktopExperience(_desktopExperience);
            }
        }
        ComponentSettingsStatusText.Text = $"已保存“{_selectedComponentEntry.Name}”设置";
        UpdateOwnLookSummary(kind);
        RefreshAppearanceOverrides();
        if (refreshList)
        {
            RefreshComponentList(_selectedComponentEntry.Window, kind);
        }
    }

    /// <summary>
    /// Says in the fold header whether the card follows the shared look, and offers the
    /// way back when it does not. Returns whether the card has its own colours.
    /// </summary>
    private bool UpdateOwnLookSummary(DesktopComponentKind kind)
    {
        var own = kind == DesktopComponentKind.Todo
            && IsSecondaryTodoWindow(_selectedComponentEntry?.Window, out var secondaryTodo)
            ? !DesktopStyleRules.FollowsSharedCardLook(secondaryTodo.State.Appearance, _maintenance.Style)
            : HasOwnLook(kind);
        ComponentOwnLookSummaryText.Text = own
            ? "已独立设置颜色，不跟随全局外观"
            : "跟随全局外观，展开后可为这张卡片独立设置";
        ComponentFollowSharedLookRow.Visibility = own ? Visibility.Visible : Visibility.Collapsed;
        ComponentQuickFollowGlobalButton.Visibility = own ? Visibility.Visible : Visibility.Collapsed;
        ComponentUnifiedLookGuideRow.Description = FormatUnifiedLookGuideDescription(
            own,
            "时间、问候、天气等桌面卡片");
        return own;
    }

    private bool UpdateSelectedWidgetOwnLookSummary(WidgetWindowBase window)
    {
        var own = !DesktopStyleRules.FollowsSharedCardLook(window.WidgetAppearance, _maintenance.Style);
        SelectedWidgetOwnLookSummaryText.Text = own
            ? "已独立设置颜色，不跟随全局外观"
            : "跟随全局外观，展开后可为这张卡片独立设置";
        SelectedWidgetFollowSharedLookRow.Visibility = own ? Visibility.Visible : Visibility.Collapsed;
        SelectedWidgetQuickFollowGlobalButton.Visibility = own ? Visibility.Visible : Visibility.Collapsed;
        SelectedWidgetUnifiedLookGuideRow.Description = FormatUnifiedLookGuideDescription(
            own,
            "所有卡片");
        return own;
    }

    private string FormatUnifiedLookGuideDescription(bool own, string scopeNoun)
    {
        var summary = DesktopStyleRules.DescribeSharedLookSummary(_maintenance.Style);
        return own
            ? $"已单独设置颜色，不随全局变化（当前全局：{summary}）。可点右侧恢复跟随，或前往调整全局统一配色"
            : $"当前跟随全局（{summary}）。{scopeNoun}可在“外观样式”页统一改色；单独定制此卡片请展开下方“独立外观”";
    }

    private bool HasOwnLook(DesktopComponentKind kind) => !DesktopStyleRules.FollowsSharedLook(
        _desktopExperience.GetComponent(kind),
        _maintenance.Style,
        QuickPlacesWindow.ClearBarOpacity);

    // Gives one card the colours of the shared material again, as applying the global
    // material would.
    private void FollowSharedLook(DesktopComponentKind kind)
    {
        var component = _desktopExperience.GetComponent(kind);
        var look = DesktopStyleRules.ComponentLook(kind, _maintenance.Style, QuickPlacesWindow.ClearBarOpacity);
        component.Appearance.BackgroundColor = look.BackgroundColor;
        component.Appearance.BackgroundOpacity = look.BackgroundOpacity;
        component.Appearance.TextColor = look.TextColor;
        if (kind == DesktopComponentKind.Todo)
        {
            var glass = DesktopStyleRules.Appearance(_maintenance.Style);
            component.Appearance.HeaderColor = glass.HeaderColor;
            component.Appearance.HeaderOpacity = glass.HeaderOpacity;
        }
        _applyDesktopExperience(_desktopExperience);
        if (_selectedComponentEntry?.Kind == kind)
        {
            _componentApplyTimer.Stop();
            LoadSelectedComponentEditor(kind, _selectedComponentEntry.Window);
            UpdateOwnLookSummary(kind);
        }
        ComponentSettingsStatusText.Text = $"“{DesktopExperienceRules.GetComponentName(kind)}”已恢复跟随全局外观";
        RefreshAppearanceOverrides();
    }

    private void ApplySharedCardAppearanceToWindow(WidgetWindowBase window)
    {
        var glass = DesktopStyleRules.Appearance(_maintenance.Style);
        window.SetWidgetBackgroundColor(glass.BackgroundColor);
        window.SetWidgetBackgroundOpacity(glass.BackgroundOpacity);
        window.SetWidgetHeaderColor(glass.HeaderColor);
        window.SetWidgetHeaderOpacity(glass.HeaderOpacity);
        window.SetWidgetTextColor(glass.TextColor);
        if (window is NoteWidgetWindow note)
        {
            note.SetBodyColor(glass.TextColor);
        }
    }

    private void FollowSecondaryTodoSharedLook(TodoWidgetWindow todoWindow)
    {
        ApplySharedCardAppearanceToWindow(todoWindow);
        if (ReferenceEquals(_selectedComponentEntry?.Window, todoWindow))
        {
            _componentApplyTimer.Stop();
            LoadSelectedComponentEditor(DesktopComponentKind.Todo, todoWindow);
            UpdateOwnLookSummary(DesktopComponentKind.Todo);
        }
        ComponentSettingsStatusText.Text = $"“{todoWindow.State.Title}”已恢复跟随全局外观";
        RefreshAppearanceOverrides();
    }

    private void FollowWidgetSharedLook(WidgetWindowBase window)
    {
        ApplySharedCardAppearanceToWindow(window);
        if (ReferenceEquals(_selectedComponentEntry?.Window, window))
        {
            if (SelectedWidgetAppearanceHost.Content is WidgetAppearancePanel panel)
            {
                panel.RefreshValues();
            }
            UpdateSelectedWidgetOwnLookSummary(window);
        }
        ComponentSettingsStatusText.Text = $"“{GetWidgetName(window)}”已恢复跟随全局外观";
        RefreshAppearanceOverrides();
    }

    private void ComponentFollowSharedLook_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedComponentEntry?.Kind == DesktopComponentKind.Todo
            && IsSecondaryTodoWindow(_selectedComponentEntry.Window, out var secondaryTodo))
        {
            FollowSecondaryTodoSharedLook(secondaryTodo);
            return;
        }
        if (_selectedComponentEntry?.Kind is { } kind)
        {
            FollowSharedLook(kind);
        }
    }

    private void SelectedWidgetFollowSharedLook_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedComponentEntry?.Window is { } window && _selectedComponentEntry.Kind is null)
        {
            FollowWidgetSharedLook(window);
        }
    }

    private void GoToUnifiedAppearance_Click(object sender, RoutedEventArgs e)
    {
        SelectNavigationPage("Appearance");
        AppearancePage.ScrollToTop();
    }

    public void ShowWidgetSettings(WidgetWindowBase window)
    {
        SelectNavigationPage("Components");
        ShowPage("Components");
        RefreshComponentList(window);
    }

    /// <summary>
    /// Opens the components page with one kind selected, for deep links from surfaces
    /// that show a card's data without the card itself, like the top bar.
    /// </summary>
    public void ShowComponentSettings(DesktopComponentKind kind)
    {
        SelectNavigationPage("Components");
        ShowPage("Components");
        RefreshComponentList(null, kind);
    }

    private bool _loadingCollect;

    private bool _loadingViewMode;

    private bool _loadingTodoWeekCount;

    private void LoadTodoSettings(TodoWidgetWindow? todo)
    {
        TodoSettingsPanel.Visibility = todo is null ? Visibility.Collapsed : Visibility.Visible;
        if (todo is null)
        {
            return;
        }

        _loadingTodoWeekCount = true;
        try
        {
            var weeks = Math.Clamp(todo.State.WeeklyWeekCount, 1, 5);
            TodoWeekCountPicker.SelectedIndex = weeks - 1;
        }
        finally
        {
            _loadingTodoWeekCount = false;
        }
    }

    private void TodoWeekCount_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTodoWeekCount
            || TodoWeekCountPicker.SelectedIndex < 0
            || _selectedComponentEntry?.Window is not TodoWidgetWindow todo)
        {
            return;
        }

        todo.SetWeeklyWeekCount(TodoWeekCountPicker.SelectedIndex + 1);
        ComponentSettingsStatusText.Text = $"已应用“{todo.State.Title}”的周历周数";
    }

    private void LoadFileBoxViewMode(FileBoxWindow? box)
    {
        FileBoxViewModePanel.Visibility = box is null ? Visibility.Collapsed : Visibility.Visible;
        if (box is null)
        {
            return;
        }

        _loadingViewMode = true;
        try
        {
            var list = box.State.ViewMode == FileBoxViewMode.List;
            FileBoxTilesRadio.IsChecked = !list;
            FileBoxListRadio.IsChecked = list;
            FileBoxAutoCollapseCheckBox.IsChecked = box.State.AutoCollapse;
        }
        finally
        {
            _loadingViewMode = false;
        }
    }

    private void FileBoxAutoCollapse_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingViewMode || _selectedComponentEntry?.Window is not FileBoxWindow box)
        {
            return;
        }

        var enabled = FileBoxAutoCollapseCheckBox.IsChecked == true;
        box.SetAutoCollapse(enabled);
        ComponentSettingsStatusText.Text = enabled
            ? $"“{box.State.Title}”已开启鼠标离开 5 秒后自动收起"
            : $"“{box.State.Title}”已关闭自动收起";
    }

    private void FileBoxViewMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingViewMode || _selectedComponentEntry?.Window is not FileBoxWindow box)
        {
            return;
        }

        box.SetViewMode(FileBoxListRadio.IsChecked == true ? FileBoxViewMode.List : FileBoxViewMode.Tiles);
        ComponentSettingsStatusText.Text = $"已保存“{box.State.Title}”的显示方式";
    }

    private void LoadFileBoxCollect(FileBoxWindow? box)
    {
        FileBoxCollectPanel.Visibility = box is null ? Visibility.Collapsed : Visibility.Visible;
        FileBoxDesktopHintPanel.Visibility = box is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshFileBoxCleanDesktopHint();
        if (box is null)
        {
            return;
        }

        _loadingCollect = true;
        try
        {
            FileBoxCollectTypes.Children.Clear();
            var folders = new CheckBox
            {
                Content = "文件夹",
                Tag = "folders",
                Margin = new Thickness(0, 4, 18, 4),
                IsChecked = box.State.CollectFolders
            };
            folders.Click += FileBoxCollect_Changed;
            FileBoxCollectTypes.Children.Add(folders);
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in FileMappingService.CollectTypes)
            {
                var check = new CheckBox
                {
                    Content = type.Name,
                    Tag = type.Key,
                    Margin = new Thickness(0, 4, 18, 4),
                    ToolTip = string.Join(" ", type.Extensions),
                    IsChecked = type.Extensions.All(box.State.CollectExtensions.Contains)
                };
                check.Click += FileBoxCollect_Changed;
                FileBoxCollectTypes.Children.Add(check);
                if (check.IsChecked == true)
                {
                    known.UnionWith(type.Extensions);
                }
            }
            FileBoxExtraExtensionsEditor.Text = string.Join(
                " ",
                box.State.CollectExtensions.Where(extension => !known.Contains(extension)));
        }
        finally
        {
            _loadingCollect = false;
        }
    }

    private void FileBoxCollect_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingCollect || _selectedComponentEntry?.Window is not FileBoxWindow box)
        {
            return;
        }

        var checks = FileBoxCollectTypes.Children.OfType<CheckBox>().ToList();
        var extensions = FileMappingService.CollectTypes
            .Where(type => checks.Any(check => Equals(check.Tag, type.Key) && check.IsChecked == true))
            .SelectMany(type => type.Extensions)
            .Concat(FileMappingService.NormalizeExtensions([FileBoxExtraExtensionsEditor.Text]));
        var folders = checks.Any(check => Equals(check.Tag, "folders") && check.IsChecked == true);
        box.SetCollectRule(extensions, folders);
        ComponentSettingsStatusText.Text = $"已保存“{box.State.Title}”的收纳类型";
    }

    private void SelectedWidgetLockedCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedComponentEntry?.Window is not { } window)
        {
            return;
        }

        _applyWidgetLock(window, SelectedWidgetLockedCheckBox.IsChecked == true);
        RefreshComponentList(window);
    }


    private void AddTodo_Click(object sender, RoutedEventArgs e) => AddWidget(NewWidgetKind.Todo);

    private void AddNote_Click(object sender, RoutedEventArgs e) => AddWidget(NewWidgetKind.Note);

    private void AddFileBox_Click(object sender, RoutedEventArgs e) => AddWidget(NewWidgetKind.FileBox);

    // The new card is selected so it can be adjusted right away.
    private void AddWidget(NewWidgetKind kind)
    {
        if (_maintenance.AddWidget(kind) is { } window)
        {
            if (kind == NewWidgetKind.FileBox)
            {
                ClearQuickActionFallbackStatus();
            }
            RefreshComponentList(window);
            ComponentSettingsStatusText.Text = $"已新建“{GetWidgetName(window)}”";
        }
    }

    private void ExitApp_Click(object sender, RoutedEventArgs e) => _maintenance.ExitApp();

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_maintenance.Uninstall() is { } error)
        {
            UninstallStatusText.Text = $"无法打开卸载程序：{error}。也可以在 Windows 设置 > 应用 > 已安装的应用中卸载。";
            UninstallStatusText.Visibility = Visibility.Visible;
        }
    }

    private void SettingsNavigationList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (SettingsNavigationList.SelectedItem is ListBoxItem { IsEnabled: true, Visibility: Visibility.Visible, Tag: string page })
        {
            ShowPage(page);
        }
    }

    private void SettingsNavigationList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down or Key.Home or Key.End))
        {
            return;
        }

        var items = SettingsNavigationList.Items;
        var selectable = new bool[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            selectable[i] = items[i] is ListBoxItem { IsEnabled: true, Visibility: Visibility.Visible, Tag: string };
        }

        var nextIndex = e.Key switch
        {
            Key.Up => SettingsNavigationRules.FindNextSelectableIndex(selectable, SettingsNavigationList.SelectedIndex, -1),
            Key.Down => SettingsNavigationRules.FindNextSelectableIndex(selectable, SettingsNavigationList.SelectedIndex, 1),
            Key.Home => SettingsNavigationRules.FindEdgeSelectableIndex(selectable, fromStart: true),
            Key.End => SettingsNavigationRules.FindEdgeSelectableIndex(selectable, fromStart: false),
            _ => -1
        };

        if (nextIndex >= 0 && items[nextIndex] is ListBoxItem target)
        {
            SettingsNavigationList.SelectedIndex = nextIndex;
            SettingsNavigationList.ScrollIntoView(target);
            target.Focus();
            e.Handled = true;
        }
    }

    /// <summary>
    /// The page and section a settings link opens. Page names from earlier versions
    /// ("Bars", "Layout", "Theme", "About" …) still lead to the settings they used to show.
    /// </summary>
    internal static (string Page, string? Section) ResolveSettingsLink(string link) =>
        SettingsNavigationRules.ResolveLink(link);

    private void ShowPage(string page)
    {
        if (SchemePage is null || ComponentsPage is null || AppearancePage is null
            || DockPage is null || TopBarPage is null || TaskbarPage is null
            || LauncherPage is null || GeneralPage is null)
        {
            return;
        }

        page = SettingsNavigationRules.NormalizePageKey(page, topBarAvailable: _topBarState is not null);
        InvalidatePendingReveal();
        SettingsSearchPopup.IsOpen = false;
        SchemePage.Visibility = page == SettingsNavigationRules.SchemePageKey ? Visibility.Visible : Visibility.Collapsed;
        ComponentsPage.Visibility = page == SettingsNavigationRules.ComponentsPageKey ? Visibility.Visible : Visibility.Collapsed;
        AppearancePage.Visibility = page == SettingsNavigationRules.AppearancePageKey ? Visibility.Visible : Visibility.Collapsed;
        DockPage.Visibility = page == SettingsNavigationRules.DockPageKey ? Visibility.Visible : Visibility.Collapsed;
        TopBarPage.Visibility = page == SettingsNavigationRules.TopBarPageKey && _topBarState is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        TaskbarPage.Visibility = page == SettingsNavigationRules.TaskbarPageKey ? Visibility.Visible : Visibility.Collapsed;
        LauncherPage.Visibility = page == SettingsNavigationRules.LauncherPageKey ? Visibility.Visible : Visibility.Collapsed;
        GeneralPage.Visibility = page == SettingsNavigationRules.GeneralPageKey ? Visibility.Visible : Visibility.Collapsed;
        if (!_initializingNavigation)
        {
            _maintenance.SetLastSettingsPage?.Invoke(page);
        }
        if (page == SettingsNavigationRules.AppearancePageKey)
        {
            RefreshAppearanceOverrides();
            UpdatePresetPreview(GetSelectedPreset());
        }
        if (page == SettingsNavigationRules.GeneralPageKey)
        {
            RefreshBackupList();
        }

        if (page == SettingsNavigationRules.ComponentsPageKey)
        {
            RefreshComponentList(
                _selectedComponentEntry?.Window,
                _selectedComponentEntry?.Kind);
            ScheduleComponentListOverflowUpdate();
        }
    }

    internal void ShowSettingsPage(string page) => SelectNavigationPage(page);

    // ---------------------------------------------------------------- 设置搜索

    private List<SettingsSearchEntry>? _searchIndex;

    private (FrameworkElement Page, string Tag, string Title)[] SearchPages =>
    [
        (SchemePage, SettingsNavigationRules.SchemePageKey, "桌面方案"),
        (ComponentsPage, SettingsNavigationRules.ComponentsPageKey, "桌面卡片"),
        (AppearancePage, SettingsNavigationRules.AppearancePageKey, "外观样式"),
        (DockPage, SettingsNavigationRules.DockPageKey, "冰蓝 Dock"),
        (TopBarPage, SettingsNavigationRules.TopBarPageKey, "顶部信息条"),
        (TaskbarPage, SettingsNavigationRules.TaskbarPageKey, "Windows 任务栏"),
        (LauncherPage, SettingsNavigationRules.LauncherPageKey, "冰蓝总览卡"),
        (GeneralPage, SettingsNavigationRules.GeneralPageKey, "通用设置")
    ];

    /// <summary>Built once from the pages; see <see cref="SettingsSearch"/>.</summary>
    internal IReadOnlyList<SettingsSearchEntry> SearchIndex => _searchIndex ??= SettingsSearch.Build(
        SearchPages.Select(page => (page.Page, page.Title)),
        (Style)FindResource("SectionHeaderStyle"),
        (Style)FindResource("ChoiceCardStyle"));

    /// <summary>The results the search box shows for a query, best match first.</summary>
    internal IReadOnlyList<SettingsSearchEntry> SearchSettings(string query) =>
        SettingsSearch.Find(SearchIndex, query.Trim(), IsSearchEntryAvailable);

    // A row that is hidden right now (a feature this computer lacks, an option of
    // another mode) is left out; component rows need a matching card to select.
    private bool IsSearchEntryAvailable(SettingsSearchEntry entry)
    {
        if (_topBarState is null
            && (ReferenceEquals(entry.Page, TopBarPage)
                || string.Equals(entry.AccordionPart, AppearancePartLookRules.TopBarPartKey, StringComparison.Ordinal)
                || ReferenceEquals(entry.Target, AppearanceTopBarPartRow)))
        {
            return false;
        }
        if (entry.Scope is { } scope)
        {
            return DesktopComponentList.Items.OfType<DesktopComponentSettingsEntry>().Any(item => MatchesSearchScope(item, scope));
        }
        return SettingsSearch.IsShown(entry.Target, entry.Page);
    }

    private static bool MatchesSearchScope(DesktopComponentSettingsEntry item, string scope) => scope switch
    {
        "Builtin" => item.Kind is not null,
        "Widget" => item.Kind is null,
        "FileBox" => item.Window is FileBoxWindow,
        "Todo" => item.Window is TodoWidgetWindow,
        _ => scope.StartsWith("Kind:", StringComparison.Ordinal) && item.Kind?.ToString() == scope["Kind:".Length..]
    };

    /// <summary>
    /// Goes to a setting: switches page, selects a card the setting belongs to,
    /// opens the folds around it, then scrolls it into the middle, tints it and focuses it.
    /// </summary>
    internal void OpenSearchResult(SettingsSearchEntry entry, bool focus = true)
    {
        SettingsSearchPopup.IsOpen = false;
        var tag = SearchPages.First(page => ReferenceEquals(page.Page, entry.Page)).Tag;
        SelectNavigationPage(tag);
        if (entry.Scope is { } scope)
        {
            SelectSearchScope(scope, entry.Target);
        }
        if (entry.AccordionPart is { Length: > 0 } partKey
            && AppearancePartLookRules.TryParsePartKey(partKey, out var part))
        {
            ExpandAppearancePart(AppearancePartLookRules.ExpandAccordion(_expandedAppearancePart, part));
        }
        foreach (var fold in entry.Folds)
        {
            fold.IsExpanded = true;
        }
        ExpandFoldsForElement(entry.Target);
        RevealElement(entry.Target, focus);
    }

    // The current card is kept when it shows the setting; otherwise the first matching
    // card that shows it is selected.
    private void SelectSearchScope(string scope, FrameworkElement target)
    {
        var candidates = DesktopComponentList.Items.OfType<DesktopComponentSettingsEntry>()
            .Where(item => MatchesSearchScope(item, scope))
            .ToList();
        if (DesktopComponentList.SelectedItem is DesktopComponentSettingsEntry current && candidates.Remove(current))
        {
            candidates.Insert(0, current);
        }
        foreach (var candidate in candidates)
        {
            if (!ReferenceEquals(DesktopComponentList.SelectedItem, candidate))
            {
                DesktopComponentList.SelectedItem = candidate;
            }
            if (SettingsSearch.IsShown(target, ComponentsPage))
            {
                return;
            }
        }
    }

    private void UpdateSearchResults()
    {
        var query = SettingsSearchBox.Text.Trim();
        if (query.Length == 0)
        {
            SettingsSearchPopup.IsOpen = false;
            return;
        }

        var hits = SearchSettings(query);
        SettingsSearchResults.ItemsSource = hits;
        SettingsSearchResults.SelectedIndex = hits.Count > 0 ? 0 : -1;
        SettingsSearchResults.Visibility = hits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingsSearchEmptyText.Text = hits.Count > 0 ? string.Empty : $"没有找到“{query}”。试试 透明、圆角、热键。";
        SettingsSearchEmptyText.Visibility = hits.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        SettingsSearchPopup.IsOpen = SettingsSearchBox.IsKeyboardFocusWithin;
    }

    private void SettingsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SettingsSearchPlaceholder.Visibility = SettingsSearchBox.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateSearchResults();
    }

    private void SettingsSearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        UpdateSearchResults();

    private void SettingsSearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        SettingsSearchPopup.IsOpen = false;

    private void SettingsSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var count = SettingsSearchResults.Items.Count;
        switch (e.Key)
        {
            case Key.Down or Key.Up when count > 0 && SettingsSearchBox.Text.Trim().Length > 0:
                var step = e.Key == Key.Down ? 1 : -1;
                SettingsSearchResults.SelectedIndex = (SettingsSearchResults.SelectedIndex + step + count) % count;
                SettingsSearchResults.ScrollIntoView(SettingsSearchResults.SelectedItem);
                SettingsSearchPopup.IsOpen = SettingsSearchBox.IsKeyboardFocusWithin;
                e.Handled = true;
                break;
            case Key.Enter when SettingsSearchResults.SelectedItem is SettingsSearchEntry entry:
                OpenSearchResult(entry);
                e.Handled = true;
                break;
            case Key.Escape when SettingsSearchPopup.IsOpen:
                SettingsSearchPopup.IsOpen = false;
                e.Handled = true;
                break;
            case Key.Escape when SettingsSearchBox.Text.Length > 0:
                SettingsSearchBox.Clear();
                e.Handled = true;
                break;
        }
    }

    private void SettingsSearchResults_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(SettingsSearchResults, source) is ListBoxItem { DataContext: SettingsSearchEntry entry })
        {
            OpenSearchResult(entry);
            e.Handled = true;
        }
    }

    // Ctrl+F reaches the search from anywhere in the window, except while a hotkey is
    // being recorded, where the keys belong to the recording.
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && !_recordingSearchHotkey && _recordingGlobalHotkey is null)
        {
            SettingsSearchBox.Focus();
            SettingsSearchBox.SelectAll();
            e.Handled = true;
        }
    }

    internal void RefreshDesktopExperience()
    {
        LoadDesktopExperience();
        RefreshAppearanceOverrides();
    }

    internal void RefreshDockSettings()
    {
        LoadDockSettings();
        LoadDesktopMode();
    }

    internal void RefreshTopBarSettings()
    {
        if (_topBarState is null)
        {
            return;
        }

        LoadTopBarSettings();
        LoadDesktopMode();
    }

    internal void RefreshTaskbarSettings()
    {
        LoadTaskbarSettings();
        LoadDesktopMode();
    }

    private void LoadPrivacySettings()
    {
        _loadingStartup = true;
        try
        {
            StartupCheckBox.IsChecked = _maintenance.IsStartupEnabled();
            StartupCheckBox.IsEnabled = _maintenance.CanChangeStartup;
            if (!_maintenance.CanChangeStartup)
            {
                StartupNoteText.Text = "测试模式不修改开机启动。";
                StartupNoteText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _loadingStartup = false;
        }
        DataDirectoryText.Text = _maintenance.DataDirectory;
        UninstallRow.Visibility = _maintenance.CanUninstall ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingStartup)
        {
            return;
        }

        _maintenance.SetStartupEnabled(StartupCheckBox.IsChecked == true);
        LoadPrivacySettings();
    }

    internal void RefreshCleanDesktopSettings(string? statusText = null)
    {
        var notice = _maintenance.CleanDesktopNotice();
        var doubleClickAvailable = _maintenance.IsCleanDesktopAvailable()
            && notice.Length == 0
            && CleanDesktopCheckBox.IsEnabled;

        _loadingCleanDesktop = true;
        try
        {
            CleanDesktopCheckBox.IsChecked = _maintenance.IsCleanDesktopEnabled();
            DoubleClickDesktopCheckBox.IsChecked = _maintenance.IsDoubleClickDesktopToToggleIconsEnabled();
            DoubleClickDesktopCheckBox.IsEnabled = doubleClickAvailable;
        }
        finally
        {
            _loadingCleanDesktop = false;
        }

        if (!doubleClickAvailable)
        {
            DoubleClickDesktopStatusText.Text = notice.Length > 0 || !_maintenance.IsCleanDesktopAvailable()
                ? "隐藏桌面图标当前不可用，请先完成上方恢复"
                : "正在切换桌面图标…";
        }
        else
        {
            DoubleClickDesktopStatusText.Text = string.Empty;
        }

        RefreshFileBoxCleanDesktopHint();

        // A restore that did not complete stays visible here until it is resolved.
        if (notice.Length > 0)
        {
            CleanDesktopStatusText.Text = notice;
            CleanDesktopRetryButton.Content = "重新恢复";
            CleanDesktopRetryButton.Visibility = Visibility.Visible;
            return;
        }

        if (statusText is not null)
        {
            CleanDesktopStatusText.Text = statusText;
        }

        // With nothing to retry, the icons may still be stuck hidden while the mode is
        // off (a lost record from an earlier version). The Shell query runs off the UI
        // thread; a newer refresh or a closed window cancels applying what it found.
        CleanDesktopRetryButton.Visibility = Visibility.Collapsed;
        var version = ++_cleanDesktopRefreshVersion;
        _ = RefreshCleanDesktopOrphanStateAsync(version);
    }

    private async Task RefreshCleanDesktopOrphanStateAsync(int version)
    {
        var orphaned = await _maintenance.CleanDesktopIconsOrphaned();
        if (!orphaned || version != _cleanDesktopRefreshVersion || _isClosed)
        {
            return;
        }

        CleanDesktopStatusText.Text = "桌面图标当前处于隐藏状态。";
        CleanDesktopRetryButton.Content = "恢复显示";
        CleanDesktopRetryButton.Visibility = Visibility.Visible;
    }

    private async void CleanDesktopRetry_Click(object sender, RoutedEventArgs e)
    {
        CleanDesktopRetryButton.IsEnabled = false;
        CleanDesktopStatusText.Text = "正在恢复桌面图标…";
        try
        {
            CleanDesktopStatusText.Text = await _maintenance.RestoreCleanDesktopIcons();
        }
        finally
        {
            CleanDesktopRetryButton.IsEnabled = true;
            RefreshCleanDesktopSettings();
        }
    }

    internal void LoadStyle()
    {
        _loadingStyle = true;
        try
        {
            SelectByTag(CardShadowSelector, _maintenance.Style.CardShadow.ToString());
            SelectByTag(MotionSelector, _maintenance.Style.Motion.ToString());
            var spacing = CardSpacingSelector.Items.OfType<ComboBoxItem>()
                .OrderBy(item => Math.Abs(double.Parse((string)item.Tag, System.Globalization.CultureInfo.InvariantCulture)
                    - _maintenance.Style.CardSpacing))
                .First();
            CardSpacingSelector.SelectedItem = spacing;
            var style = _maintenance.Style;
            GridSnapCheckBox.IsChecked = style.GridSnap;
            GlassLightRadio.IsChecked = style.Glass == GlassMode.Light;
            GlassDarkRadio.IsChecked = style.Glass == GlassMode.Dark;
            GlassClearRadio.IsChecked = style.Glass == GlassMode.Clear;
            TextInkAutoRadio.IsChecked = style.TextInk == TextInk.Auto;
            TextInkWhiteRadio.IsChecked = style.TextInk == TextInk.White;
            TextInkDarkRadio.IsChecked = style.TextInk == TextInk.Dark;
            TextInkCustomRadio.IsChecked = style.TextInk == TextInk.Custom;
            CustomTextColorChip.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(style.CustomTextColor)!);
            GlassDensitySlider.Value = style.GlassDensity;
            GlassDensityText.Text = $"{style.GlassDensity * 100:0}%";
            GlassDensityPanel.IsEnabled = style.Glass != GlassMode.Clear;
            ItemSurfaceLightRadio.IsChecked = style.ItemSurfaceTone == SurfaceTone.Light;
            ItemSurfaceDarkRadio.IsChecked = style.ItemSurfaceTone == SurfaceTone.Dark;
            ItemSurfaceStrengthSlider.Value = style.ItemSurfaceStrength;
            ItemSurfaceStrengthText.Text = $"{style.ItemSurfaceStrength * 100:0}%";
            ItemSurfacePanel.IsEnabled = style.Glass == GlassMode.Light;
            PaletteSwatches.Select(GlassPaletteSwatches, style.Glass == GlassMode.Light ? style.Palette : null);
        }
        finally
        {
            _loadingStyle = false;
        }
        UpdatePresetPreview(GetSelectedPreset());
        RefreshAppearanceOverrides();
        if (_selectedComponentEntry?.Kind is { } kind)
        {
            UpdateOwnLookSummary(kind);
        }
        else if (_selectedComponentEntry?.Window is { } selectedWidget)
        {
            if (SelectedWidgetAppearanceHost.Content is WidgetAppearancePanel panel)
            {
                panel.RefreshValues();
            }
            UpdateSelectedWidgetOwnLookSummary(selectedWidget);
        }
    }

    private void AppearancePreview_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AppearancePreviewWindow(_maintenance.Style) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.AppliedAppearance is { } selection)
        {
            selection.ApplyTo(_maintenance.Style);
            _maintenance.ApplyGlass();
            LoadStyle();
        }
    }

    private void Glass_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingStyle || GlassClearRadio is null || TextInkCustomRadio is null)
        {
            return;
        }

        var style = _maintenance.Style;
        style.Glass = GlassDarkRadio.IsChecked == true
            ? GlassMode.Dark
            : GlassClearRadio.IsChecked == true ? GlassMode.Clear : GlassMode.Light;
        style.TextInk = TextInkCustomRadio.IsChecked == true
            ? TextInk.Custom
            : TextInkDarkRadio.IsChecked == true
                ? TextInk.Dark
                : TextInkWhiteRadio.IsChecked == true ? TextInk.White : TextInk.Auto;
        _maintenance.ApplyGlass();
        LoadStyle();
    }

    private void RecommendedClearLook_Click(object sender, RoutedEventArgs e)
    {
        DesktopStyleRules.ApplyRecommendedClearLook(_maintenance.Style);
        _maintenance.ApplyGlass();
        _maintenance.ApplyStyle();
        LoadStyle();
    }

    // Reads a skin's look, shows what will change and applies it after a yes.
    private async void ImportRainmeterLook_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择一个 Rainmeter 皮肤文件夹",
            InitialDirectory = BingLan.App.Themes.RainmeterSkinFiles.SkinsFolder() ?? string.Empty
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var folder = dialog.FolderName;
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        RainmeterImportStatusText.Text = "正在读取…";
        var look = await Task.Run(() => RainmeterSkinReader.Read(BingLan.App.Themes.RainmeterSkinFiles.Collect(folder)));
        if (look.IsEmpty)
        {
            RainmeterImportStatusText.Text = $"“{name}”中没有找到字体或颜色设置";
            return;
        }

        var lines = new List<string>();
        if (look.FontFace is { } font) lines.Add($"字体：{font}");
        if (look.TextColor is { } text) lines.Add($"文字颜色：{text}");
        if (look.BackgroundColor is { } backing) lines.Add($"卡片底色：{backing}，不透明度 {look.BackgroundOpacity:P0}");
        if (look.AccentColor is { } accent) lines.Add($"强调色：{accent}");
        if (look.CornerRadius is { } radius) lines.Add($"圆角：{radius:0}");
        var answer = System.Windows.MessageBox.Show(
            this,
            $"将按“{name}”调整所有卡片：\n\n{string.Join("\n", lines)}\n\n当前设置会先备份，可在“通用设置”页的备份中恢复。继续吗？",
            "从 Rainmeter 皮肤导入外观",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            RainmeterImportStatusText.Text = "已取消";
            return;
        }

        RainmeterImportStatusText.Text = _maintenance.ImportRainmeterLook(look, name);
        LoadStyle();
    }

    // Picking a colour also switches the cards to it.
    private void CustomTextColor_Click(object sender, RoutedEventArgs e)
    {
        var style = _maintenance.Style;
        var current = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(style.CustomTextColor)!;
        if (!NativeColorDialog.TryPickColor(this, current, out var chosen))
        {
            return;
        }

        style.CustomTextColor = chosen;
        style.TextInk = TextInk.Custom;
        _maintenance.ApplyGlass();
        LoadStyle();
    }

    private void GlassDensity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingStyle || GlassDensityText is null)
        {
            return;
        }

        _maintenance.Style.GlassDensity = e.NewValue;
        GlassDensityText.Text = $"{_maintenance.Style.GlassDensity * 100:0}%";
        _maintenance.ApplyGlass();
        UpdatePresetPreview(GetSelectedPreset());
    }

    // Inner surfaces only change the shared brushes, so the cards keep their own colours.
    private void ItemSurfaceTone_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingStyle || ItemSurfaceDarkRadio is null)
        {
            return;
        }

        _maintenance.Style.ItemSurfaceTone = ItemSurfaceDarkRadio.IsChecked == true
            ? SurfaceTone.Dark
            : SurfaceTone.Light;
        _maintenance.ApplyStyle();
    }

    private void ItemSurfaceStrength_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingStyle || ItemSurfaceStrengthText is null)
        {
            return;
        }

        _maintenance.Style.ItemSurfaceStrength = e.NewValue;
        ItemSurfaceStrengthText.Text = $"{_maintenance.Style.ItemSurfaceStrength * 100:0}%";
        _maintenance.ApplyStyle();
    }

    private static void SelectByTag(System.Windows.Controls.ComboBox selector, string tag) =>
        selector.SelectedItem = selector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals((string)item.Tag, tag, StringComparison.Ordinal));

    private void StyleSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingStyle
            || CardShadowSelector.SelectedItem is not ComboBoxItem { Tag: string shadowTag }
            || CardSpacingSelector.SelectedItem is not ComboBoxItem { Tag: string spacingTag }
            || MotionSelector.SelectedItem is not ComboBoxItem { Tag: string motionTag })
        {
            return;
        }

        _maintenance.Style.CardShadow = Enum.Parse<CardShadow>(shadowTag);
        _maintenance.Style.CardSpacing = double.Parse(spacingTag, System.Globalization.CultureInfo.InvariantCulture);
        _maintenance.Style.Motion = Enum.Parse<MotionLevel>(motionTag);
        _maintenance.ApplyStyle();
    }

    private void GridSnapCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingStyle || GridSnapCheckBox is null)
        {
            return;
        }

        _maintenance.Style.GridSnap = GridSnapCheckBox.IsChecked == true;
        _maintenance.ApplyStyle();
    }

    private void LoadFileBoxAutomation()
    {
        _loadingFileBoxAutomation = true;
        try
        {
            FileBoxAutomationCheckBox.IsChecked = _maintenance.IsFileBoxAutomationEnabled();
            FileBoxDeleteOriginalsCheckBox.IsChecked = _maintenance.IsFileBoxDeleteOriginalsEnabled();
            HeaderToolsOnHoverCheckBox.IsChecked = _maintenance.IsHeaderToolsOnHover();
        }
        finally
        {
            _loadingFileBoxAutomation = false;
        }
    }

    private void FileBoxAutomationCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingFileBoxAutomation)
        {
            return;
        }

        _maintenance.SetFileBoxAutomation(FileBoxAutomationCheckBox.IsChecked == true);
    }

    private void FileBoxDeleteOriginalsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingFileBoxAutomation)
        {
            return;
        }

        _maintenance.SetFileBoxDeleteOriginals(FileBoxDeleteOriginalsCheckBox.IsChecked == true);
    }

    private void HeaderToolsOnHoverCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingFileBoxAutomation)
        {
            return;
        }

        _maintenance.SetHeaderToolsOnHover(HeaderToolsOnHoverCheckBox.IsChecked == true);
    }

    private async void CleanDesktopCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingCleanDesktop)
        {
            return;
        }

        var enable = CleanDesktopCheckBox.IsChecked == true;
        CleanDesktopCheckBox.IsEnabled = false;
        DoubleClickDesktopCheckBox.IsEnabled = false;
        CleanDesktopStatusText.Text = enable ? "正在隐藏桌面图标…" : "正在恢复桌面图标…";
        try
        {
            CleanDesktopStatusText.Text = await _maintenance.SetCleanDesktop(enable);
        }
        finally
        {
            CleanDesktopCheckBox.IsEnabled = true;
            RefreshCleanDesktopSettings();
        }
    }

    private void DoubleClickDesktopCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingCleanDesktop)
        {
            return;
        }

        var enable = DoubleClickDesktopCheckBox.IsChecked == true;
        var problem = _maintenance.SetDoubleClickDesktopToToggleIcons(enable);
        RefreshCleanDesktopSettings();
        if (problem.Length > 0)
        {
            DoubleClickDesktopStatusText.Text = problem;
        }
    }

    // 收纳只记录位置，桌面上的原文件仍可见；分组盒设置里指向清爽桌面开关，避免用户找不到隐藏入口。
    private void RefreshFileBoxCleanDesktopHint()
    {
        var enabled = _maintenance.IsCleanDesktopEnabled();
        GoToCleanDesktopButton.IsEnabled = !enabled;
        GoToCleanDesktopButton.Content = enabled ? "桌面图标已隐藏" : "前往隐藏桌面图标";
    }

    private void GoToCleanDesktop_Click(object sender, RoutedEventArgs e) => SelectNavigationPage("CleanDesktop");

    private void OpenDataDirectory_Click(object sender, RoutedEventArgs e) =>
        _maintenance.OpenDataDirectory();

    private int _backupRefreshVersion;

    private async void RefreshBackupList()
    {
        var version = ++_backupRefreshVersion;
        var paths = _maintenance.ListBackups();
        var entries = paths.Select(path => new BackupEntry(path)).ToList();
        BackupList.ItemsSource = entries;
        BackupEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (entries.Count == 0)
        {
            return;
        }

        var labels = await Task.Run(() =>
            paths.Select(BackupEntry.FormatLabel).ToArray());
        if (_isClosed || version != _backupRefreshVersion)
        {
            return;
        }

        for (var i = 0; i < entries.Count && i < labels.Length; i++)
        {
            entries[i].Label = labels[i];
        }
    }

    private void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _maintenance.CreateBackup();
            RefreshBackupList();
            BackupStatusText.Text = $"已备份到 {path}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            BackupStatusText.Text = $"备份未完成：{exception.Message}";
        }
    }

    private void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not BackupEntry entry)
        {
            BackupStatusText.Text = "请先选择一个备份";
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            this,
            $"将用 {entry.Label} 的备份替换当前桌面组件、个人内容和设置。当前状态会先另存为备份，随后冰蓝桌面会重新启动。继续吗？",
            "从备份恢复",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _maintenance.RestoreBackup(entry.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidDataException)
        {
            BackupStatusText.Text = $"无法使用这个备份：{exception.Message}";
        }
    }

    private void DeleteBackup_Click(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not BackupEntry entry)
        {
            BackupStatusText.Text = "请先选择一个备份";
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            this,
            $"将删除 {entry.Label} 的备份文件，删除后无法恢复。继续吗？",
            "删除备份",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (!_maintenance.DeleteBackup(entry.Path))
            {
                BackupStatusText.Text = "无法删除这个备份";
                RefreshBackupList();
                return;
            }
            RefreshBackupList();
            BackupStatusText.Text = "已删除备份";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            BackupStatusText.Text = $"删除未完成：{exception.Message}";
        }
    }

    private void ResetComponentPlacement_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedComponentEntry is not { } entry)
        {
            return;
        }

        var reset = _maintenance.ResetWidgetPlacement(entry.Window, entry.Kind);
        ComponentSettingsStatusText.Text = reset ? "已恢复默认位置" : "这张卡片没有默认位置";
        RefreshComponentList(entry.Window, entry.Kind);
    }

    // Same as the card's own "删除" menu: it asks first, and built-in cards are hidden
    // rather than losing their settings.
    private void DeleteComponent_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedComponentEntry is not { Window: { } window } entry)
        {
            ComponentSettingsStatusText.Text = "这张卡片当前没有显示在桌面上";
            return;
        }
        var isBuiltInInfo = entry.Kind is { } kind && kind != DesktopComponentKind.Todo;
        if (_maintenance.DeleteWidget(window))
        {
            ComponentSettingsStatusText.Text = isBuiltInInfo ? "已从桌面移除" : "已删除";
            RefreshComponentList(null, isBuiltInInfo ? entry.Kind : null);
        }
    }

    private void ExportTheme_Click(object sender, RoutedEventArgs e)
    {
        var nameDialog = new ThemeExportDialog { Owner = this };
        if (nameDialog.ShowDialog() != true)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出冰蓝桌面主题",
            Filter = $"冰蓝桌面主题 (*{ThemeArchive.FileExtension})|*{ThemeArchive.FileExtension}",
            FileName = nameDialog.ThemeName,
            DefaultExt = ThemeArchive.FileExtension,
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ThemeStatusText.Text = _maintenance.ExportTheme(dialog.FileName, nameDialog.ThemeName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ThemeStatusText.Text = $"导出未完成：{exception.Message}";
        }
    }

    private async void ImportTheme_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入冰蓝桌面主题",
            Filter = $"冰蓝桌面主题 (*{ThemeArchive.FileExtension})|*{ThemeArchive.FileExtension}"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var preview = _maintenance.ReadTheme(dialog.FileName);
            if (preview.Package is not { } package)
            {
                ThemeStatusText.Text = preview.Error;
                return;
            }

            var confirmDialog = new ThemeImportDialog(package, preview.PreviewPng) { Owner = this };
            if (confirmDialog.ShowDialog() != true)
            {
                return;
            }

            ThemeStatusText.Text = "正在匹配本机应用…";
            var outcome = await _maintenance.ImportTheme(dialog.FileName);
            ThemeStatusText.Text = outcome.Message;
            LoadStyle();
            LoadDockSettings();
            LoadDesktopMode();
            RefreshBackupList();

            if (outcome.MissingSlots.Count > 0)
            {
                new ThemeBindingWizardWindow(outcome.MissingSlots, _maintenance.PinBoundApp) { Owner = this }
                    .ShowDialog();
                LoadDockSettings();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException)
        {
            ThemeStatusText.Text = $"导入未完成：{exception.Message}";
        }
    }

    /// <summary>
    /// The desktop mode is where Dock, taskbar and top bar settings start from; the bars
    /// page fine-tunes them. The scheme page and the bars page each name the mode in use
    /// and what was changed since, with one button back to the mode's defaults.
    /// </summary>
    private void LoadDesktopMode()
    {
        var match = DesktopModeRules.Match(_dockState, _taskbarState, _topBarState);
        _loadingDesktopMode = true;
        try
        {
            WindowsNativeModeRadio.IsChecked = match.IsExact && match.Mode == DesktopMode.WindowsNative;
            IceBlueHybridModeRadio.IsChecked = match.IsExact && match.Mode == DesktopMode.IceBlueHybrid;
            AppleStyleModeRadio.IsChecked = match.IsExact && match.Mode == DesktopMode.AppleStyle;
        }
        finally
        {
            _loadingDesktopMode = false;
        }

        var name = DesktopModeRules.GetName(match.Mode);
        var changes = string.Join("，", match.Differences);
        var restore = $"恢复{name}默认";
        foreach (var button in new[] { RestoreDesktopModeButton, DockRestoreModeButton, TopBarRestoreModeButton, TaskbarRestoreModeButton })
        {
            button.Content = restore;
            button.Tag = match.Mode;
            button.Visibility = match.IsExact ? Visibility.Collapsed : Visibility.Visible;
        }

        var (activeMode, problem) = _taskbarStatus();
        var taskbarHeldBack = problem.Length > 0
            && activeMode == TaskbarMode.SystemDefault
            && _taskbarState.Mode != TaskbarMode.SystemDefault;
        DesktopModeStatusText.Text = taskbarHeldBack
            ? $"任务栏保持系统默认：{problem}"
            : match.IsExact
                ? $"当前桌面方案：{name}。"
                : $"在{name}基础上自定义：{changes}";
        DesktopModeDriftPanel.Visibility = taskbarHeldBack || !match.IsExact
            ? Visibility.Visible
            : Visibility.Collapsed;

        RefreshSchemeSummary(match, activeMode, problem);
    }

    // “当前状态”: what each bar really does now, marked where it left the mode's defaults.
    private void RefreshSchemeSummary(DesktopModeMatch match, TaskbarMode activeMode, string problem)
    {
        SetSummary(
            SchemeDockDot,
            SchemeDockStateText,
            SchemeDockCustomTag,
            _dockState.IsEnabled,
            DesktopModeRules.GetDockVisibilitySummary(_dockState),
            match.DockChanged);
        if (_topBarState is { } topBar)
        {
            SetSummary(
                SchemeTopBarDot,
                SchemeTopBarStateText,
                SchemeTopBarCustomTag,
                topBar.IsEnabled,
                !topBar.IsEnabled
                    ? "已关闭"
                    : $"{(topBar.VisibilityMode == TopBarVisibilityMode.SmartHide ? "智能隐藏" : "始终显示")} · {CountTopBarModules(topBar)} 个模块",
                match.TopBarChanged);
        }
        var activeName = DesktopModeRules.GetTaskbarName(activeMode);
        SetSummary(
            SchemeTaskbarDot,
            SchemeTaskbarStateText,
            SchemeTaskbarCustomTag,
            activeMode != TaskbarMode.SystemDefault,
            problem.Length == 0 ? activeName : $"{activeName}（{problem}）",
            match.TaskbarChanged);

        static void SetSummary(System.Windows.Shapes.Ellipse dot, TextBlock text, Border tag, bool on, string state, bool changed)
        {
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, on ? "SettingsOkBrush" : "SettingsFaintInkBrush");
            text.Text = state;
            tag.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static int CountTopBarModules(TopBarState state)
    {
        var modules = state.Modules;
        bool[] shown =
        [
            modules.Clock, modules.TodoSummary, modules.Weather, modules.Performance, modules.Attention,
            modules.InputMethod, modules.Volume, modules.Network, modules.Battery, modules.Search,
            modules.TrayOverflow && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
        ];
        return shown.Count(value => value);
    }

    private void DesktopMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingDesktopMode
            || sender is not RadioButton { Tag: string tag }
            || !Enum.TryParse<DesktopMode>(tag, out var mode))
        {
            return;
        }

        ApplyDesktopMode(mode);
    }

    private void RestoreDesktopMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DesktopMode mode })
        {
            ApplyDesktopMode(mode);
        }
    }

    private void ApplyDesktopMode(DesktopMode mode)
    {
        if (_topBarState is null)
        {
            DesktopModeRules.Apply(mode, _dockState, _taskbarState);
        }
        else
        {
            // The apple mode also turns the top bar on and hides the three information
            // cards it replaces, so the same facts do not show twice.
            DesktopModeRules.Apply(mode, _dockState, _taskbarState, _topBarState, _desktopExperience);
            _applyDesktopExperience(_desktopExperience);
            _applyTopBar?.Invoke();
            LoadTopBarSettings();
        }
        _applyDock();
        _applyTaskbar();
        LoadDockSettings();
        LoadTaskbarSettings();
        LoadDesktopMode();
    }

    private void LoadTaskbarSettings()
    {
        _loadingTaskbar = true;
        try
        {
            TaskbarDefaultRadio.IsChecked = _taskbarState.Mode == TaskbarMode.SystemDefault;
            TaskbarTransparentRadio.IsChecked = _taskbarState.Mode == TaskbarMode.Transparent;
            TaskbarBlurRadio.IsChecked = _taskbarState.Mode == TaskbarMode.Blur;
            TaskbarSmartHideRadio.IsChecked = _taskbarState.Mode == TaskbarMode.SmartHide;
            TaskbarEdgeRevealComboBox.SelectedItem = TaskbarEdgeRevealComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => (string)item.Tag == _taskbarState.EdgeRevealMode.ToString())
                ?? TaskbarEdgeRevealComboBox.Items.OfType<ComboBoxItem>().First();
            // Both pop-up options only matter while the taskbar hides itself.
            TaskbarPopupPanel.Visibility = _taskbarState.Mode == TaskbarMode.SmartHide
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _loadingTaskbar = false;
        }

        var (activeMode, problem) = _taskbarStatus();
        var activeName = activeMode switch
        {
            TaskbarMode.Transparent => "透明",
            TaskbarMode.Blur => "模糊",
            TaskbarMode.SmartHide => "自动隐藏",
            _ => "系统默认"
        };
        TaskbarStatusText.Text = problem.Length == 0
            ? $"当前：{activeName}"
            : activeMode == TaskbarMode.SystemDefault
                ? $"当前保持系统默认：{problem}"
                : $"当前：{activeName}（{problem}）";

        var mainMonitor = MonitorCatalog.GetAll().FirstOrDefault(monitor => monitor.IsPrimary);
        var edge = mainMonitor is null
            ? null
            : TaskbarEdgeGuard.GetNativeTaskbarLayout(mainMonitor.Bounds)?.Edge;
        var edgeName = edge switch
        {
            DockEdge.Left => "左侧",
            DockEdge.Top => "顶部",
            DockEdge.Right => "右侧",
            DockEdge.Bottom => "底部",
            _ => null
        };
        TaskbarPositionText.Visibility = edgeName is null ? Visibility.Collapsed : Visibility.Visible;
        TaskbarPositionText.Text = edgeName is null ? string.Empty : $"主显示器任务栏位于{edgeName}";

        // The app owns auto-hide only in the auto-hide mode; anywhere else it is Windows' own.
        WindowsAutoHidePanel.Visibility = _taskbarState.Mode != TaskbarMode.SmartHide
            && Taskbar.TaskbarAdapter.IsWindowsAutoHideOn()
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void ShowTaskbarAlways_Click(object sender, RoutedEventArgs e)
    {
        var done = Taskbar.TaskbarAdapter.TryShowTaskbarAlways();
        LoadTaskbarSettings();
        if (!done)
        {
            TaskbarStatusText.Text = "Windows 没有接受修改，请在 Windows 任务栏设置中手动关闭自动隐藏";
        }
        else if (Taskbar.TaskbarAdapter.IsAutoHideStored() == true)
        {
            // The live state is off but Windows still stores "hidden" for the next sign-in.
            TaskbarStatusText.Text = "任务栏已显示；Windows 仍记着自动隐藏，重新登录后可能恢复。请打开 Windows 任务栏设置，把“自动隐藏任务栏”关闭一次";
        }
    }

    private void OpenWindowsTaskbarSettings_Click(object sender, RoutedEventArgs e)
    {
        _ = Services.ShellLauncher.StartAsync(
            new System.Diagnostics.ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true });
    }

    private void TaskbarEdgeReveal_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTaskbar
            || TaskbarEdgeRevealComboBox.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse<TaskbarEdgeRevealMode>(tag, out var mode))
        {
            return;
        }

        _taskbarState.EdgeRevealMode = mode;
        _taskbarState.RevealOnlyAtCorners = mode == TaskbarEdgeRevealMode.CornersOnly;
        _applyTaskbar();
        TaskbarStatusText.Text = mode switch
        {
            TaskbarEdgeRevealMode.CornersOnly => "任务栏只在底边左右两端弹出",
            TaskbarEdgeRevealMode.Disabled => "已禁止鼠标触边唤出任务栏（仍可按 Win 键或点击 Dock 开始按钮打开）",
            _ => "已恢复：鼠标碰到底边任何位置都可弹出任务栏",
        };
    }

    private void TaskbarMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingTaskbar
            || sender is not RadioButton { Tag: string tag }
            || !Enum.TryParse<TaskbarMode>(tag, out var mode))
        {
            return;
        }

        _taskbarState.Mode = mode;
        _applyTaskbar();
        LoadTaskbarSettings();
        LoadDesktopMode();
    }

    private void LoadDockSettings()
    {
        _loadingDock = true;
        try
        {
            DockEnabledCheckBox.IsChecked = _dockState.IsEnabled;
            DockOptionsPanel.IsEnabled = _dockState.IsEnabled;
            DockAdvancedBehaviorPanel.IsEnabled = _dockState.IsEnabled;
            DockReserveRadio.IsChecked =
                _dockState.VisibilityMode == DockVisibilityMode.ReserveWorkArea;
            DockSmartHideRadio.IsChecked =
                _dockState.VisibilityMode == DockVisibilityMode.SmartHide;
            DockIconSizeSlider.Value = _dockState.IconSize;
            DockAutoIconSizeCheckBox.IsChecked = _dockState.AutoIconSize;
            DockHoverMagnificationCheckBox.IsChecked = _dockState.HoverMagnification;
            var magScale = DockMagnificationRules.ClampScale(_dockState.HoverMagnificationScale);
            DockHoverMagnificationScaleSlider.Value = magScale;
            DockHoverMagnificationScaleText.Text = $"{magScale:0.00}×";
            DockHoverMagnificationScaleRow.IsEnabled = _dockState.HoverMagnification;
            QuietTaskbarFlashCheckBox.IsChecked = !BingLan.App.Services.TaskbarFlashingSetting.IsEnabled();
            LoadDockLook();
            DockIconSizeText.Text = $"{_dockState.IconSize:0}";
            DockMaximizeReleaseCheckBox.IsChecked = _dockState.ReleaseWhenMaximized;
            DockMaximizeReleaseRow.IsEnabled =
                _dockState.VisibilityMode == DockVisibilityMode.ReserveWorkArea;
            DockRevealModeComboBox.SelectedItem = DockRevealModeComboBox.Items
                .OfType<ComboBoxItem>()
                .First(item => (string)item.Tag == _dockState.RevealMode.ToString());
            var normalizedEdge = DockEdgeRules.Normalize(_dockState.Edge);
            DockEdgeComboBox.SelectedItem = DockEdgeComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals((string)item.Tag, normalizedEdge.ToString(), StringComparison.Ordinal))
                ?? DockEdgeComboBox.Items.OfType<ComboBoxItem>().First();
            DockStartMenuButtonCheckBox.IsChecked = _dockState.ShowStartMenuButton;
            DockLauncherButtonCheckBox.IsChecked = _dockState.ShowLauncherButton;
            DockShowDesktopButtonCheckBox.IsChecked = _dockState.ShowDesktopButton;
            DockBottomGapSlider.Value = _dockState.BottomGapDip;
            DockBottomGapText.Text = $"{_dockState.BottomGapDip:0} DIP";

            DockMonitorComboBox.Items.Clear();
            foreach (var monitor in MonitorCatalog.GetAll())
            {
                var item = new ComboBoxItem { Content = monitor.Label, Tag = monitor.DeviceName };
                DockMonitorComboBox.Items.Add(item);
                if (string.Equals(
                        monitor.DeviceName,
                        _dockState.MonitorDeviceName,
                        StringComparison.OrdinalIgnoreCase)
                    || (DockMonitorComboBox.SelectedItem is null && monitor.IsPrimary))
                {
                    DockMonitorComboBox.SelectedItem = item;
                }
            }

            RefreshDockPinnedList();
            RefreshDockHiddenList();
        }
        finally
        {
            _loadingDock = false;
        }
    }

    private void RefreshDockPinnedList(DockPinnedApp? selected = null)
    {
        var entries = _dockState.PinnedApps
            .Select(app => new DockPinnedEntry(app))
            .ToList();
        DockPinnedList.ItemsSource = entries;
        DockPinnedList.SelectedItem = entries.FirstOrDefault(entry => ReferenceEquals(entry.App, selected));
        DockPinnedEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshDockHiddenList()
    {
        var entries = _dockState.HiddenApps
            .Select(app => new DockPinnedEntry(app))
            .ToList();
        DockHiddenList.ItemsSource = entries;
        DockHiddenEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UnhideDockApp_Click(object sender, RoutedEventArgs e)
    {
        if (DockHiddenList.SelectedItem is not DockPinnedEntry entry)
        {
            DockStatusText.Text = "请先选择一个应用";
            return;
        }

        if (DockPinRules.Unhide(_dockState, DockPinRules.IdentityKey(entry.App)))
        {
            RefreshDockHiddenList();
            ApplyDockChange($"{entry.App.DisplayName} 会重新显示在 Dock 上");
        }
    }

    private void DockSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingDock || DockEnabledCheckBox is null || DockMonitorComboBox is null || DockMaximizeReleaseRow is null)
        {
            return;
        }

        _dockState.IsEnabled = DockEnabledCheckBox.IsChecked == true;
        DockOptionsPanel.IsEnabled = _dockState.IsEnabled;
        DockAdvancedBehaviorPanel.IsEnabled = _dockState.IsEnabled;
        var previousVisibility = _dockState.VisibilityMode;
        _dockState.VisibilityMode = DockSmartHideRadio.IsChecked == true
            ? DockVisibilityMode.SmartHide
            : DockVisibilityMode.ReserveWorkArea;
        if (previousVisibility == DockVisibilityMode.SmartHide
            && _dockState.VisibilityMode == DockVisibilityMode.ReserveWorkArea
            && ReferenceEquals(sender, DockReserveRadio))
        {
            _loadingDock = true;
            try
            {
                DockMaximizeReleaseCheckBox.IsChecked = false;
            }
            finally
            {
                _loadingDock = false;
            }
        }
        _dockState.ReleaseWhenMaximized = DockMaximizeReleaseCheckBox.IsChecked == true;
        DockMaximizeReleaseRow.IsEnabled =
            _dockState.VisibilityMode == DockVisibilityMode.ReserveWorkArea;
        if (DockRevealModeComboBox?.SelectedItem is ComboBoxItem { Tag: string revealTag }
            && Enum.TryParse<DockRevealMode>(revealTag, out var revealMode))
        {
            _dockState.RevealMode = revealMode;
        }
        if (DockEdgeComboBox?.SelectedItem is ComboBoxItem { Tag: string edgeTag }
            && Enum.TryParse<DockEdge>(edgeTag, out var dockEdge))
        {
            _dockState.Edge = DockEdgeRules.Normalize(dockEdge);
        }
        _dockState.ShowStartMenuButton = DockStartMenuButtonCheckBox.IsChecked == true;
        _dockState.ShowLauncherButton = DockLauncherButtonCheckBox.IsChecked == true;
        _dockState.ShowDesktopButton = DockShowDesktopButtonCheckBox.IsChecked == true;
        if (DockMonitorComboBox.SelectedItem is ComboBoxItem { Tag: string deviceName })
        {
            _dockState.MonitorDeviceName = deviceName;
        }
        ApplyDockChange(_dockState.IsEnabled ? "已应用" : "冰蓝 Dock 已关闭");
        LoadDesktopMode();
    }

    private bool _loadingDockLook;

    private void LoadDockLook()
    {
        _loadingDockLook = true;
        try
        {
            DockFollowLookRadio.IsChecked = _dockState.FollowCardLook;
            DockCustomLookRadio.IsChecked = !_dockState.FollowCardLook;
            DockCustomLookPanel.IsEnabled = !_dockState.FollowCardLook;
            DockColorEditor.Text = _dockState.SurfaceColor;
            DockOpacitySlider.Value = _dockState.SurfaceOpacity;
            DockOpacityText.Text = $"{_dockState.SurfaceOpacity * 100:0}%";
            var preview = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dockState.SurfaceColor)!);
            preview.Freeze();
            DockColorPreview.Background = preview;
            PaletteSwatches.Select(
                DockPaletteSwatches,
                _dockState.FollowCardLook
                    ? null
                    : DesktopStyleRules.Palettes.FirstOrDefault(palette => palette.Body == _dockState.SurfaceColor)?.Key);
        }
        finally
        {
            _loadingDockLook = false;
        }
        RefreshAppearanceOverrides();
    }

    private void DockLook_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingDockLook || _loadingDock || DockCustomLookRadio is null)
        {
            return;
        }

        _dockState.FollowCardLook = DockFollowLookRadio.IsChecked == true;
        ApplyDockChange(_dockState.FollowCardLook ? "Dock 跟随卡片外观" : "Dock 使用自定义外观");
        LoadDockLook();
    }

    private void DockOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingDockLook || _loadingDock || DockOpacityText is null)
        {
            return;
        }

        _dockState.SurfaceOpacity = e.NewValue;
        DockOpacityText.Text = $"{e.NewValue * 100:0}%";
        ApplyDockChange("已调整 Dock 玻璃浓度");
    }

    private void DockColorEditor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            DockColorEditor_Commit(sender, e);
            e.Handled = true;
        }
    }

    private void DockColorEditor_Commit(object sender, RoutedEventArgs e)
    {
        if (_loadingDockLook)
        {
            return;
        }

        var color = WidgetAppearanceRules.CoerceColorOrDefault(DockColorEditor.Text, _dockState.SurfaceColor);
        if (color != _dockState.SurfaceColor)
        {
            _dockState.SurfaceColor = color;
            ApplyDockChange("已调整 Dock 颜色");
        }
        LoadDockLook();
    }

    private void DockChooseColor_Click(object sender, RoutedEventArgs e)
    {
        var current = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_dockState.SurfaceColor)!;
        if (!NativeColorDialog.TryPickColor(this, current, out var chosen))
        {
            return;
        }

        _dockState.SurfaceColor = chosen;
        ApplyDockChange("已调整 Dock 颜色");
        LoadDockLook();
    }

    // ---------------------------------------------------------------- 顶端信息条

    private void LoadTopBarSettings()
    {
        if (_topBarState is not { } state)
        {
            return;
        }

        _loadingTopBar = true;
        try
        {
            TopBarEnabledCheckBox.IsChecked = state.IsEnabled;
            TopBarOptionsPanel.IsEnabled = state.IsEnabled;
            TopBarAdvancedBehaviorPanel.IsEnabled = state.IsEnabled;
            TopBarFrostedCheckBox.IsChecked = state.FrostedGlass;
            TopBarReserveRadio.IsChecked = state.VisibilityMode == TopBarVisibilityMode.ReserveTopEdge;
            TopBarSmartHideRadio.IsChecked = state.VisibilityMode == TopBarVisibilityMode.SmartHide;
            TopBarHeightSlider.Value = Math.Clamp(
                double.IsFinite(state.Height) ? state.Height : TopBarState.DefaultHeightDip,
                TopBarState.MinimumHeightDip,
                TopBarState.MaximumHeightDip);
            TopBarClockCheckBox.IsChecked = state.Modules.Clock;
            TopBarTodoSummaryCheckBox.IsChecked = state.Modules.TodoSummary;
            TopBarWeatherCheckBox.IsChecked = state.Modules.Weather;
            TopBarPerformanceCheckBox.IsChecked = state.Modules.Performance;
            TopBarAttentionCheckBox.IsChecked = state.Modules.Attention;
            TopBarInputMethodCheckBox.IsChecked = state.Modules.InputMethod;
            TopBarVolumeCheckBox.IsChecked = state.Modules.Volume;
            TopBarNetworkCheckBox.IsChecked = state.Modules.Network;
            TopBarShowWifiNameCheckBox.IsChecked = state.ShowWifiName == true;
            TopBarShowWifiNameRow.IsEnabled = state.Modules.Network;
            TopBarBatteryCheckBox.IsChecked = state.Modules.Battery;
            TopBarSearchCheckBox.IsChecked = state.Modules.Search;
            TopBarTrayOverflowCheckBox.IsChecked = state.Modules.TrayOverflow;
            TopBarTrayOverflowCheckBox.Visibility = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
                ? Visibility.Visible : Visibility.Collapsed;
            LoadTopBarSearchHotkey();

            TopBarMonitorComboBox.Items.Clear();
            foreach (var monitor in MonitorCatalog.GetAll())
            {
                var item = new ComboBoxItem { Content = monitor.Label, Tag = monitor.DeviceName };
                TopBarMonitorComboBox.Items.Add(item);
                if (string.Equals(
                        monitor.DeviceName,
                        state.MonitorDeviceName,
                        StringComparison.OrdinalIgnoreCase)
                    || (TopBarMonitorComboBox.SelectedItem is null && monitor.IsPrimary))
                {
                    TopBarMonitorComboBox.SelectedItem = item;
                }
            }

            LoadTopBarLook();
        }
        finally
        {
            _loadingTopBar = false;
        }
    }

    private void TopBarSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingTopBar || _topBarState is not { } state
            || TopBarEnabledCheckBox is null || TopBarMonitorComboBox is null)
        {
            return;
        }

        state.IsEnabled = TopBarEnabledCheckBox.IsChecked == true;
        TopBarOptionsPanel.IsEnabled = state.IsEnabled;
        TopBarAdvancedBehaviorPanel.IsEnabled = state.IsEnabled;
        state.FrostedGlass = TopBarFrostedCheckBox.IsChecked == true;
        state.VisibilityMode = TopBarSmartHideRadio.IsChecked == true
            ? TopBarVisibilityMode.SmartHide
            : TopBarVisibilityMode.ReserveTopEdge;
        state.Modules.Clock = TopBarClockCheckBox.IsChecked == true;
        state.Modules.TodoSummary = TopBarTodoSummaryCheckBox.IsChecked == true;
        state.Modules.Weather = TopBarWeatherCheckBox.IsChecked == true;
        state.Modules.Performance = TopBarPerformanceCheckBox.IsChecked == true;
        state.Modules.Attention = TopBarAttentionCheckBox.IsChecked == true;
        state.Modules.InputMethod = TopBarInputMethodCheckBox.IsChecked == true;
        state.Modules.Volume = TopBarVolumeCheckBox.IsChecked == true;
        state.Modules.Network = TopBarNetworkCheckBox.IsChecked == true;
        state.ShowWifiName = TopBarShowWifiNameCheckBox.IsChecked == true;
        TopBarShowWifiNameRow.IsEnabled = state.Modules.Network;
        state.Modules.Battery = TopBarBatteryCheckBox.IsChecked == true;
        state.Modules.Search = TopBarSearchCheckBox.IsChecked == true;
        state.Modules.TrayOverflow = TopBarTrayOverflowCheckBox.IsChecked == true;
        if (TopBarMonitorComboBox.SelectedItem is ComboBoxItem { Tag: string deviceName })
        {
            state.MonitorDeviceName = deviceName;
        }
        ApplyTopBarChange(state.IsEnabled ? "已应用" : "顶部信息条已关闭");
        RefreshAppearanceOverrides();
    }

    // ---------------------------------------------------------------- 顶栏搜索热键

    private void TopBarModules_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.UniformGrid grid)
            grid.Columns = Math.Clamp((int)(grid.ActualWidth / 150d), 2, 5);
    }

    /// <summary>
    /// A click-to-record capture: the next modifier-plus-key press becomes the search
    /// hotkey. Bare keys are refused (they would swallow typing system-wide) and the
    /// optional probe rejects combinations another program already owns.
    /// </summary>
    private bool _recordingSearchHotkey;

    private void LoadTopBarSearchHotkey()
    {
        if (TopBarSearchHotkeyButton is null || TopBarSearchHotkeyHint is null)
        {
            return;
        }
        var configured = _maintenance.Hotkeys.Launcher;
        TopBarSearchHotkeyHint.Text = string.Empty;
        TopBarSearchHotkeyButton.Content = string.IsNullOrWhiteSpace(configured)
            ? "未设置"
            : TopBarSearchRules.ParseHotkey(configured) is { } binding
                ? TopBarSearchRules.FormatHotkey(binding)
                : configured;
    }

    private void TopBarSearchHotkeyClear_Click(object sender, RoutedEventArgs e)
    {
        _recordingSearchHotkey = false;
        GlobalHotkeyRules.Set(_maintenance.Hotkeys, GlobalHotkeyKind.Launcher, string.Empty);
        if (_topBarState is not null)
        {
            _topBarState.SearchHotkey = string.Empty;
        }
        LoadTopBarSearchHotkey();
        _maintenance.ApplyGlobalHotkeys();
        ApplyTopBarChange("已清空总览卡快捷键");
    }

    private void TopBarSearchHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_topBarState is null)
        {
            return;
        }
        _recordingSearchHotkey = !_recordingSearchHotkey;
        if (_recordingSearchHotkey)
        {
            TopBarSearchHotkeyHint.Text = string.Empty;
            TopBarSearchHotkeyButton.Content = "按下组合键…";
            TopBarSearchHotkeyButton.Focus();
        }
        else
        {
            LoadTopBarSearchHotkey();
        }
    }

    private void TopBarSearchHotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingSearchHotkey || _topBarState is not { } state)
        {
            return;
        }
        e.Handled = true;
        // With Alt held the real key arrives on SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Escape)
        {
            StopSearchHotkeyRecording(state);
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            return;
        }
        if (HotkeyToken(key) is not { } token)
        {
            TopBarSearchHotkeyHint.Text = "这个键不能作为热键主键";
            return;
        }
        var parts = new List<string>(5);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }
        if (parts.Count == 0)
        {
            TopBarSearchHotkeyHint.Text = "需要至少一个修饰键（Ctrl/Alt/Shift/Win）";
            return;
        }
        parts.Add(token);
        var text = string.Join("+", parts);
        if (TopBarSearchRules.ParseHotkey(text) is null)
        {
            TopBarSearchHotkeyHint.Text = "组合无效";
            return;
        }
        if (GlobalHotkeyRules.FindInternalConflict(_maintenance.Hotkeys, GlobalHotkeyKind.Launcher, text) is { } conflict)
        {
            TopBarSearchHotkeyHint.Text = GlobalHotkeyRules.FormatConflictHint(
                GlobalHotkeyProbeResult.OwnedByBingLan(conflict));
            return;
        }
        if (_probeTopBarSearchHotkey?.Invoke(text) is { IsAvailable: false } probe)
        {
            TopBarSearchHotkeyHint.Text = GlobalHotkeyRules.FormatConflictHint(probe);
            return;
        }
        GlobalHotkeyRules.Set(_maintenance.Hotkeys, GlobalHotkeyKind.Launcher, text);
        state.SearchHotkey = text;
        StopSearchHotkeyRecording(state);
        _maintenance.ApplyGlobalHotkeys();
        ApplyTopBarChange("已更新搜索热键");
    }

    private void TopBarSearchHotkey_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_recordingSearchHotkey && _topBarState is { } state)
        {
            StopSearchHotkeyRecording(state);
        }
    }

    private void StopSearchHotkeyRecording(TopBarState state)
    {
        _recordingSearchHotkey = false;
        LoadTopBarSearchHotkey();
    }

    private static string? HotkeyToken(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.Space => "Space",
        _ => null
    };

    private void LoadTopBarLook()
    {
        if (_topBarState is not { } state)
        {
            return;
        }

        _loadingTopBarLook = true;
        try
        {
            TopBarFollowLookRadio.IsChecked = state.FollowCardLook;
            TopBarCustomLookRadio.IsChecked = !state.FollowCardLook;
            TopBarCustomLookPanel.IsEnabled = !state.FollowCardLook;
            TopBarColorEditor.Text = state.SurfaceColor;
            TopBarOpacitySlider.Value = state.SurfaceOpacity;
            TopBarOpacityText.Text = $"{state.SurfaceOpacity * 100:0}%";
            var preview = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(state.SurfaceColor)!);
            preview.Freeze();
            TopBarColorPreview.Background = preview;
        }
        finally
        {
            _loadingTopBarLook = false;
        }
        RefreshAppearanceOverrides();
    }

    private void TopBarLook_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingTopBarLook || _loadingTopBar || _topBarState is null || TopBarCustomLookRadio is null)
        {
            return;
        }

        _topBarState.FollowCardLook = TopBarFollowLookRadio.IsChecked == true;
        ApplyTopBarChange(_topBarState.FollowCardLook ? "信息条跟随卡片外观" : "信息条使用自定义外观");
        LoadTopBarLook();
    }

    private void TopBarOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingTopBarLook || _loadingTopBar || _topBarState is null || TopBarOpacityText is null)
        {
            return;
        }

        _topBarState.SurfaceOpacity = e.NewValue;
        TopBarOpacityText.Text = $"{e.NewValue * 100:0}%";
        ApplyTopBarChange("已调整信息条不透明度");
    }

    private void TopBarHeight_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TopBarHeightText is not null)
        {
            TopBarHeightText.Text = $"{e.NewValue:0} DIP";
        }
        // IsLoaded keeps the parse-time coercion (unset Value snapping to the minimum)
        // from counting as a user change before the window even exists.
        if (!IsLoaded || _loadingTopBar || _topBarState is null)
        {
            return;
        }

        _topBarState.Height = e.NewValue;
        ApplyTopBarChange("已调整信息条高度");
    }

    private void TopBarColorEditor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            TopBarColorEditor_Commit(sender, e);
            e.Handled = true;
        }
    }

    private void TopBarColorEditor_Commit(object sender, RoutedEventArgs e)
    {
        if (_loadingTopBarLook || _topBarState is not { } state)
        {
            return;
        }

        var color = WidgetAppearanceRules.CoerceColorOrDefault(TopBarColorEditor.Text, state.SurfaceColor);
        if (color != state.SurfaceColor)
        {
            state.SurfaceColor = color;
            ApplyTopBarChange("已调整信息条颜色");
        }
        LoadTopBarLook();
    }

    private void TopBarChooseColor_Click(object sender, RoutedEventArgs e)
    {
        if (_topBarState is not { } state)
        {
            return;
        }

        var current = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(state.SurfaceColor)!;
        if (!NativeColorDialog.TryPickColor(this, current, out var chosen))
        {
            return;
        }

        state.SurfaceColor = chosen;
        ApplyTopBarChange("已调整信息条颜色");
        LoadTopBarLook();
    }

    private void ApplyTopBarChange(string status)
    {
        if (TopBarStatusText is not null)
        {
            TopBarStatusText.Text = status;
        }
        // Let the current input/selection message finish, and apply only the latest
        // slider or preset value if several events arrived in the same dispatcher turn.
        if (_topBarApplyQueued) return;
        _topBarApplyQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _topBarApplyQueued = false;
            _applyTopBar?.Invoke();
            LoadDesktopMode();
        }, DispatcherPriority.Background);
    }

    private void QuietTaskbarFlash_Click(object sender, RoutedEventArgs e)
    {
        var quiet = QuietTaskbarFlashCheckBox.IsChecked == true;
        try
        {
            BingLan.App.Services.TaskbarFlashingSetting.SetEnabled(!quiet);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or System.Security.SecurityException or IOException)
        {
            QuietTaskbarFlashCheckBox.IsChecked = !quiet;
            TaskbarStatusText.Text = $"无法更改任务栏闪烁设置：{exception.Message}";
            return;
        }
        TaskbarStatusText.Text = quiet
            ? "已关闭任务栏应用闪烁，新消息只在 Dock 上标记"
            : "已恢复 Windows 默认的任务栏应用闪烁";
    }

    private void DockIconSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingDock || DockIconSizeText is null)
        {
            return;
        }

        _dockState.IconSize = e.NewValue;
        DockIconSizeText.Text = $"{e.NewValue:0}";
        ApplyDockChange("已调整图标大小");
    }

    private void DockAutoIconSize_Changed(object sender, RoutedEventArgs e)
    {
        // DockStatusText parses after this row; a null means XAML loading is still
        // running and the checked state is the initial one, not a user action.
        if (_loadingDock || DockAutoIconSizeCheckBox is null || DockStatusText is null)
        {
            return;
        }

        _dockState.AutoIconSize = DockAutoIconSizeCheckBox.IsChecked == true;
        ApplyDockChange(
            _dockState.AutoIconSize ? "已开启自动缩小图标" : "已关闭自动缩小图标");
    }

    private void DockHoverMagnification_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingDock || DockHoverMagnificationCheckBox is null || DockHoverMagnificationScaleRow is null || DockStatusText is null)
        {
            return;
        }

        _dockState.HoverMagnification = DockHoverMagnificationCheckBox.IsChecked == true;
        DockHoverMagnificationScaleRow.IsEnabled = _dockState.HoverMagnification;
        ApplyDockChange(_dockState.HoverMagnification ? "已开启 Dock 悬停放大" : "已关闭 Dock 悬停放大");
    }

    private void DockHoverMagnificationScale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingDock || DockHoverMagnificationScaleText is null)
        {
            return;
        }

        var clamped = DockMagnificationRules.ClampScale(Math.Round(e.NewValue, 2));
        _dockState.HoverMagnificationScale = clamped;
        DockHoverMagnificationScaleText.Text = $"{clamped:0.00}×";
        ApplyDockChange("已调整悬停放大强度");
    }

    private void DockBottomGap_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingDock || DockBottomGapText is null)
        {
            return;
        }

        _dockState.BottomGapDip = e.NewValue;
        DockBottomGapText.Text = $"{e.NewValue:0} DIP";
        ApplyDockChange("已调整距底部高度");
    }

    private void ImportTaskbarPins_Click(object sender, RoutedEventArgs e)
    {
        var added = _maintenance.ImportTaskbarPins();
        RefreshDockPinnedList();
        DockStatusText.Text = added > 0 ? $"已导入 {added} 个应用" : "任务栏上的应用都已在 Dock 中";
    }

    private void AddDockApp_Click(object sender, RoutedEventArgs e)
    {
        AppPickerDialog.Open(this, _dockState.PinnedApps, chosen =>
        {
            var pinned = chosen.Where(app => DockPinRules.Pin(_dockState, app)).ToList();
            RefreshDockPinnedList(pinned.LastOrDefault());
            ApplyDockChange(pinned.Count > 0 ? $"已固定 {pinned.Count} 个应用" : "所选应用已在 Dock 中");
        });
    }

    private void MoveDockAppUp_Click(object sender, RoutedEventArgs e) => MoveSelectedDockApp(-1);

    private void MoveDockAppDown_Click(object sender, RoutedEventArgs e) => MoveSelectedDockApp(1);

    private void MoveSelectedDockApp(int offset)
    {
        if (DockPinnedList.SelectedItem is not DockPinnedEntry entry)
        {
            DockStatusText.Text = "请先选择一个应用";
            return;
        }

        var key = DockPinRules.IdentityKey(entry.App);
        if (DockPinRules.Move(_dockState, key, DockPinRules.IndexOf(_dockState, key) + offset))
        {
            RefreshDockPinnedList(entry.App);
            ApplyDockChange("已调整顺序");
        }
    }

    private void UnpinDockApp_Click(object sender, RoutedEventArgs e)
    {
        if (DockPinnedList.SelectedItem is not DockPinnedEntry entry)
        {
            DockStatusText.Text = "请先选择一个应用";
            return;
        }

        if (DockPinRules.Unpin(_dockState, DockPinRules.IdentityKey(entry.App)))
        {
            RefreshDockPinnedList();
            ApplyDockChange($"已取消固定 {entry.App.DisplayName}");
        }
    }

    private bool _loadingUpdates;

    private void LoadUpdateSettings()
    {
        _loadingUpdates = true;
        try
        {
            var updater = _maintenance.Updater;
            AppVersionText.Text = updater is null
                ? "冰蓝桌面"
                : $"冰蓝桌面 {updater.CurrentVersion.ToString(3)}";
            var version = updater?.CurrentVersion ?? typeof(SettingsWindow).Assembly.GetName().Version;
            NavVersionText.Text = version is null ? string.Empty : $"版本 {version.ToString(3)}";
            AutoCheckUpdatesCheckBox.IsChecked = updater?.AutoCheck == true;
            AutoCheckUpdatesCheckBox.IsEnabled = updater is not null;
        }
        finally
        {
            _loadingUpdates = false;
        }
        RefreshUpdateView();
    }

    private void RefreshUpdateView()
    {
        var updater = _maintenance.Updater;
        CheckUpdatesButton.IsEnabled = updater is { IsBusy: false };
        UpdateStatusText.Text = updater?.Status ?? "此运行方式不提供更新检查。";
        if (updater?.Available is { } release)
        {
            UpdateAvailableCard.Visibility = Visibility.Visible;
            UpdateAvailableTitle.Text = $"新版本 {release.Version.ToString(3)}";
            UpdateNotesText.Text = release.Notes.Length > 0 ? release.Notes : "这个版本没有附更新说明。";
            var installable = release.Installer is not null;
            InstallUpdateButton.Visibility = installable ? Visibility.Visible : Visibility.Collapsed;
            InstallUpdateButton.IsEnabled = !updater.IsBusy;
            InstallUpdateNoteText.Text = installable
                ? "安装时冰蓝桌面会暂时退出，装完自动重新打开。"
                : "这个版本没有可校验的安装包，请在发布页下载。";
        }
        else
        {
            UpdateAvailableCard.Visibility = Visibility.Collapsed;
        }
    }

    private void AutoCheckUpdates_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUpdates || _maintenance.Updater is not { } updater)
        {
            return;
        }
        updater.AutoCheck = AutoCheckUpdatesCheckBox.IsChecked == true;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_maintenance.Updater is { } updater)
        {
            await updater.CheckNowAsync();
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_maintenance.Updater is { } updater)
        {
            await updater.DownloadAndInstallAsync();
        }
    }

    private void OpenReleasePage_Click(object sender, RoutedEventArgs e) =>
        _maintenance.Updater?.OpenReleasePage();

    private void OpenRepository_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("https://github.com/keros68/binglan") { UseShellExecute = true })?.Dispose();

    private void ApplyDockChange(string status)
    {
        _applyDock();
        DockStatusText.Text = status;
    }

    private void SelectNavigationPage(string link)
    {
        var (page, section) = SettingsNavigationRules.ResolveLink(link, topBarAvailable: _topBarState is not null);
        var item = SettingsNavigationList.Items
            .OfType<ListBoxItem>()
            .FirstOrDefault(candidate => candidate.IsEnabled && candidate.Visibility == Visibility.Visible && Equals(candidate.Tag, page));
        if (item is null)
        {
            page = SettingsNavigationRules.DefaultPageKey;
            section = null;
            item = SettingsNavigationList.Items
                .OfType<ListBoxItem>()
                .FirstOrDefault(candidate => candidate.IsEnabled && candidate.Visibility == Visibility.Visible && Equals(candidate.Tag, page));
            if (item is null)
            {
                return;
            }
        }

        var alreadySelected = ReferenceEquals(SettingsNavigationList.SelectedItem, item);
        SettingsNavigationList.SelectedItem = item;
        if (alreadySelected)
        {
            ShowPage(page);
        }
        ExpandFoldForSection(section);
        switch (section)
        {
            case "Layout":
                RevealElement(AppearanceLayoutFold);
                break;
            case "CleanDesktop":
                RevealElement(CleanDesktopRow);
                break;
            case "Backup":
                RevealElement(BackupList);
                break;
            case "Privacy":
                RevealElement(DataDirectoryText);
                break;
        }
    }

    private void BarsJump_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string bar })
        {
            SelectNavigationPage(bar);
            switch (bar)
            {
                case SettingsNavigationRules.DockPageKey:
                    DockPage.ScrollToTop();
                    break;
                case SettingsNavigationRules.TopBarPageKey:
                    TopBarPage.ScrollToTop();
                    break;
                case SettingsNavigationRules.TaskbarPageKey:
                    TaskbarPage.ScrollToTop();
                    break;
            }
        }
    }

    /// <summary>
    /// Scrolls a setting to the middle of its page once layout has caught up, tints it
    /// briefly (a fold only on its header), and optionally moves keyboard focus to its
    /// first control.
    /// </summary>
    private void RevealElement(FrameworkElement target, bool focus = false)
    {
        var generation = BeginRevealGeneration();
        ExpandFoldsForElement(target);
        ScheduleRevealElement(target, focus, generation, SettingsQuickActions.MaxRevealRetries);
    }

    private void CompleteRevealElement(FrameworkElement target, bool focus)
    {
        var flash = target is Expander fold
            ? fold.Template?.FindName("HeaderSite", fold) as FrameworkElement
            : null;
        if (FindVisualAncestor<ScrollViewer>(target) is { } scroller)
        {
            var top = target.TranslatePoint(new System.Windows.Point(), scroller).Y;
            var centred = Math.Max(0, (scroller.ViewportHeight - target.ActualHeight) / 2);
            scroller.ScrollToVerticalOffset(Math.Max(0, scroller.VerticalOffset + top - centred));
            scroller.UpdateLayout();
        }
        SettingsSearch.Flash(flash ?? target);
        if (focus)
        {
            FocusFirstControl(target);
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var node = System.Windows.Media.VisualTreeHelper.GetParent(element);
             node is not null;
             node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is T match)
            {
                return match;
            }
        }
        return null;
    }

    // Depth first, so the first keyboard stop inside the setting takes focus.
    private static bool FocusFirstControl(DependencyObject node)
    {
        if (node is Control { IsVisible: true, IsEnabled: true, Focusable: true, IsTabStop: true } control)
        {
            return control.Focus();
        }
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(node, index);
            if (child is not UIElement { IsVisible: false } && FocusFirstControl(child))
            {
                return true;
            }
        }
        return false;
    }

    private void RefreshComponentList(
        WidgetWindowBase? preferredWindow = null,
        DesktopComponentKind? preferredKind = null)
    {
        if (DesktopComponentList is null)
        {
            return;
        }

        var windows = _widgetProvider();
        SyncObservedWidgetTitles(windows);
        var entries = new List<DesktopComponentSettingsEntry>();
        foreach (var kind in CurrentComponentKinds)
        {
            var matchingWindows = windows
                .Where(window =>
                    TryGetDesktopComponentKind(window, out var windowKind) &&
                    windowKind == kind)
                .ToArray();
            if (matchingWindows.Length == 0)
            {
                entries.Add(CreateComponentEntry(kind, null));
                continue;
            }

            entries.AddRange(matchingWindows.Select(window => CreateComponentEntry(kind, window)));
        }
        entries.AddRange(windows
            .Where(window => !TryGetDesktopComponentKind(window, out _))
            .Select(CreateWidgetEntry));

        DesktopComponentList.ItemsSource = entries;
        if (entries.Any(entry => entry.Window is FileBoxWindow))
        {
            ClearQuickActionFallbackStatus();
        }
        var selected = preferredWindow is not null
            ? entries.FirstOrDefault(entry => ReferenceEquals(entry.Window, preferredWindow))
            : null;
        selected ??= preferredKind is { } requestedKind
            ? entries.FirstOrDefault(entry => entry.Kind == requestedKind)
            : null;
        selected ??= entries.FirstOrDefault();
        DesktopComponentList.SelectedItem = selected;
        if (selected is not null)
        {
            ScrollSelectedComponentIntoView(selected);
        }
        ScheduleComponentListOverflowUpdate();
    }

    private DesktopComponentSettingsEntry CreateComponentEntry(
        DesktopComponentKind kind,
        WidgetWindowBase? window)
    {
        var component = _desktopExperience.GetComponent(kind);
        var locked = window?.IsWidgetLocked ?? component.IsLocked;
        return new DesktopComponentSettingsEntry(
            window is null ? DesktopExperienceRules.GetComponentName(kind) : GetWidgetName(window),
            kind == DesktopComponentKind.Todo ? "待办卡片" : "桌面信息",
            !component.IsVisible ? "未显示" : locked ? "已锁定" : "可拖动",
            kind,
            window);
    }

    private static DesktopComponentSettingsEntry CreateWidgetEntry(WidgetWindowBase window)
    {
        var typeName = window switch
        {
            NoteWidgetWindow => "普通便签",
            FileBoxWindow => "文件收纳盒",
            _ => "桌面卡片"
        };
        return new DesktopComponentSettingsEntry(
            GetWidgetName(window),
            typeName,
            !IsWidgetShown(window) ? "未显示" : window.IsWidgetLocked ? "已锁定" : "可拖动",
            null,
            window);
    }

    private static bool TryGetDesktopComponentKind(
        WidgetWindowBase window,
        out DesktopComponentKind kind)
    {
        switch (window)
        {
            case TodoWidgetWindow:
                kind = DesktopComponentKind.Todo;
                return true;
            case InformationWidgetWindow { ComponentKind: { } componentKind }:
                kind = componentKind;
                return true;
            case QuickPlacesWindow places:
                kind = places.ComponentKind;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static string GetWidgetName(WidgetWindowBase window) => window switch
    {
        TodoWidgetWindow todo => todo.State.Title,
        NoteWidgetWindow note => note.State.Title,
        FileBoxWindow box => box.State.Title,
        InformationWidgetWindow { ComponentKind: { } kind } =>
            DesktopExperienceRules.GetComponentName(kind),
        _ => window.Title
    };

    private void PresetRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingExperience || sender is not RadioButton radio || radio.Tag is not string tag)
        {
            return;
        }
        if (Enum.TryParse<DesktopLayoutPreset>(tag, out var preset))
        {
            UpdatePresetPreview(preset);
        }
    }

    // Previews use the same placements as applying a preset, with current card colours.
    private void UpdatePresetPreview(DesktopLayoutPreset preset)
    {
        _previewPreset = preset;
        if (_presetPreviewQueued || _isClosed) return;
        _presetPreviewQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _presetPreviewQueued = false;
            if (!_isClosed) RenderPresetPreviews(_previewPreset);
        }));
    }

    private void RenderPresetPreviews(DesktopLayoutPreset preset)
    {
        PresetPreviewImage.Source = ThemePreviewRenderer.RenderPreset(preset, _desktopExperience);
        QuietPresetThumb.Source = ThemePreviewRenderer.RenderPreset(DesktopLayoutPreset.QuietInformation, _desktopExperience);
        GlacierPresetThumb.Source = ThemePreviewRenderer.RenderPreset(DesktopLayoutPreset.GlacierWorkbench, _desktopExperience);
        ApplePresetThumb.Source = ThemePreviewRenderer.RenderPreset(DesktopLayoutPreset.TransparentApple, _desktopExperience);
        CenterClockPresetThumb.Source = ThemePreviewRenderer.RenderPreset(DesktopLayoutPreset.CenterClock, _desktopExperience);
    }

    private void ApplyLayout_Click(object sender, RoutedEventArgs e)
    {
        _desktopExperience.ActivePreset = GetSelectedPreset();
        DesktopExperienceRules.Normalize(_desktopExperience);
        _applyLayoutPreset(_desktopExperience);
        LayoutStatusText.Text =
            $"已应用“{DesktopExperienceRules.GetPresetName(_desktopExperience.ActivePreset)}”";
    }

    private DesktopLayoutPreset GetSelectedPreset()
    {
        if (GlacierWorkbenchPresetRadio.IsChecked == true)
        {
            return DesktopLayoutPreset.GlacierWorkbench;
        }
        if (CenterClockPresetRadio.IsChecked == true)
        {
            return DesktopLayoutPreset.CenterClock;
        }
        return TransparentApplePresetRadio.IsChecked == true
            ? DesktopLayoutPreset.TransparentApple
            : DesktopLayoutPreset.QuietInformation;
    }

    private static bool ShouldApplyEditedHeight(string editorText, double currentHeight)
    {
        var trimmed = editorText.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        return !string.Equals(trimmed, currentHeight.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            && !string.Equals(trimmed, currentHeight.ToString("0", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static (double Width, double Height) GetMinimumComponentSize(
        DesktopComponentKind kind,
        double fontScale = 1d,
        DesktopInformationDensity density = DesktopInformationDensity.Standard) =>
        kind switch
        {
            DesktopComponentKind.Todo => (226d, 166d),
            DesktopComponentKind.Weather => (180d, DesktopExperienceRules.CalculateWeatherMinHeight(fontScale, density)),
            _ => (180d, 72d)
        };

    private static double ParseSize(
        string value,
        double fallback,
        double minimum,
        double maximum) =>
        double.TryParse(value, out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;

    private async void SearchCity_Click(object sender, RoutedEventArgs e) =>
        await SearchCityAsync();

    private async Task SearchCityAsync()
    {
        CancelSearch();
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        SearchCityButton.IsEnabled = false;
        ApplyCityButton.IsEnabled = false;
        CityResultsList.ItemsSource = null;
        CityResultsList.Visibility = Visibility.Collapsed;
        CitySearchStatusText.Text = "正在搜索…";

        try
        {
            var outcome = await _citySearchService.SearchAsync(
                CityQueryEditor.Text,
                cancellation.Token);
            if (cancellation.IsCancellationRequested ||
                !ReferenceEquals(_searchCancellation, cancellation))
            {
                return;
            }

            CityResultsList.ItemsSource = outcome.Results;
            // The list only takes room while there is something to choose.
            CityResultsList.Visibility = outcome.Results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            CitySearchStatusText.Text = outcome.Message;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 关闭窗口或开始新搜索时安静取消旧请求。
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                _searchCancellation = null;
                SearchCityButton.IsEnabled = true;
            }
            cancellation.Dispose();
        }
    }

    private void CityQueryEditor_KeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        _ = SearchCityAsync();
        e.Handled = true;
    }

    private void CityResultsList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        ApplyCityButton.IsEnabled = CityResultsList.SelectedItem is CitySearchResult;

    private void ApplyCity_Click(object sender, RoutedEventArgs e)
    {
        if (CityResultsList.SelectedItem is not CitySearchResult selected)
        {
            return;
        }

        _applyCity(selected);
        UpdateCurrentCity(selected.DisplayName);
        CitySearchStatusText.Text = "城市已保存，正在刷新天气";
    }

    private void UpdateCurrentCity(string city) =>
        CurrentCityText.Text = string.IsNullOrWhiteSpace(city)
            ? "当前城市：未设置"
            : $"当前城市：{city.Trim()}";

    private void CancelSearch()
    {
        _searchCancellation?.Cancel();
        _searchCancellation = null;
    }

    public sealed record DesktopComponentSettingsEntry(
        string Name,
        string TypeName,
        string Status,
        DesktopComponentKind? Kind,
        WidgetWindowBase? Window);
}

internal sealed class DockPinnedEntry(DockPinnedApp app)
{
    public DockPinnedApp App { get; } = app;
    public string DisplayName => App.DisplayName;
    public string Detail => App.ExecutablePath ?? App.TargetPath ?? "Microsoft Store 或网页应用";
}

internal sealed class BackupEntry(string path) : INotifyPropertyChanged
{
    public string Path { get; } = path;

    public event PropertyChangedEventHandler? PropertyChanged;

    private string _label = $"{System.IO.Path.GetFileNameWithoutExtension(path)}  ·  正在读取…";

    // The time plus what the backup holds, so the right one can be picked without opening it.
    public string Label
    {
        get => _label;
        internal set
        {
            if (_label == value)
            {
                return;
            }
            _label = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
    }

    internal static string FormatLabel(string path)
    {
        var timestamp = File.Exists(path)
            ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm")
            : System.IO.Path.GetFileNameWithoutExtension(path);
        return LocalStateStore.DescribeBackup(path) is { } summary
            ? $"{timestamp}  ·  {summary.PresetName}  ·  {summary.CardCount} 张卡片  ·  Dock {summary.DockAppCount} 个应用"
            : $"{timestamp}  ·  无法读取此备份";
    }
}
