using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.App.Taskbar;
using BingLan.App.Themes;
using BingLan.App.TopBar;
using BingLan.Core.Dock;
using Microsoft.Win32;
using BingLan.Core.Themes;
using BingLan.Core.TopBar;
using BingLan.App.Windows;
using BingLan.Core.Models;
using BingLan.Core.Services;

namespace BingLan.App;

public sealed partial class WidgetCoordinator : IDisposable
{
    private static readonly DesktopComponentKind[] InformationComponentKinds =
    [
        DesktopComponentKind.TimeDate,
        DesktopComponentKind.Greeting,
        DesktopComponentKind.Weather,
        DesktopComponentKind.Performance,
        DesktopComponentKind.AudioVisualizer
    ];

    private readonly LocalStateStore _store;
    private readonly List<WidgetWindowBase> _windows = [];
    private readonly DispatcherTimer _saveTimer;
    private readonly TrayIconHost _trayIcon;
    private readonly WindowsPerformanceSamplingService _performanceSampler;
    private readonly WeatherService _weatherService;
    private readonly TopBarEnvironment _topBarEnvironment;
    private readonly CityLookupService _citySearchService;
    private readonly DockHost _dock;
    private readonly TopBarHost _topBar;
    private TrayOverflowController? _trayOverflow;
    private Task<string>? _closingTrayOverflow;
    private readonly TaskbarAdapter _taskbar;
    private readonly CleanDesktopAdapter _cleanDesktop;
    private readonly TaskbarCornerReveal _cornerReveal = new();
    private readonly TaskbarMinimizeFocusGuard _taskbarMinimizeFocusGuard;
    private readonly DockIconStore _iconStore;
    private readonly AppUpdater _updater;
    private TrayMenuItem? _updateMenuItem;
    private TrayMenuItem? _launcherMenuItem;
    private TrayMenuItem? _peekMenuItem;
    private Action? _balloonClicked;
    private IReadOnlyDictionary<string, byte[]> _pendingThemeIcons = new Dictionary<string, byte[]>();
    private readonly SemaphoreSlim _cleanDesktopGate = new(1, 1);
    private bool _cleanDesktopRecovered;
    private bool _cleanDesktopRestoreFailed;
    private readonly StartupRegistration _startup;
    private readonly FullScreenWatcher _fullScreenWatcher = new();
    private readonly DesktopSurfaceWatcher _desktopSurfaceWatcher;
    private readonly DesktopBlankHitTester _desktopBlankHitTester = new();
    private DesktopDoubleClickWatcher? _doubleClickWatcher;
    private bool _isHandlingDesktopDoubleClick;
    private readonly GlobalHotkeyHost _hotkeys;
    private readonly DesktopPeekController _peek;
    private readonly FileBoxEdgeDockController _edgeDock;
    private readonly FileMappingWatchService _mappingWatcher;
    private readonly List<WidgetWindowBase> _windowsHiddenFromTray = [];
    private TrayMenuItem? _toggleDesktopMenuItem;
    private TrayMenuItem? _gameModeMenuItem;
    private string _trayBaseText = "冰蓝桌面";
    private bool _gameModeActive;
    private bool _desktopHidden;
    private bool _temporaryNativeDesktopActive;
    private bool _widgetsHiddenByNativeToggle;
    private int _desktopToggleVersion;
    private int _nativeToggleVersion;
    private bool _saveFailureShown;
    private Task<Exception?>? _deferredSave;
    private readonly string[] _launchArguments;
    private readonly bool _showOnboarding;
    private AppState _state;
    private DesktopStyleState _lastAppliedStyle;
    private SettingsWindow? _settingsWindow;
    private bool _isExiting;
    private bool _exitRequested;
    private bool _sessionEndingPending;
    private bool _sessionEndedHandled;
    private bool _isOrganizingDesktop;
    private bool _isApplyingDesktopExperience;
    private bool _isApplyingTaskbar;
    private bool _isSweepingGoneMappings;
    private bool _sweepGoneAgain;
    private bool _isImportingDesktopEntries;
    private bool _importDesktopAgain;

    public WidgetCoordinator(
        bool interactiveQa = false,
        string? dataDirectory = null,
        bool showOnboarding = false)
    {
        _showOnboarding = showOnboarding;
        _launchArguments = interactiveQa ? ["--interactive-qa"] : [];
        _startup = new StartupRegistration(
            Environment.ProcessPath ?? string.Empty,
            canModify: dataDirectory is null && Environment.ProcessPath is not null);
        _store = new LocalStateStore(dataDirectory);
        _state = _store.Load();
        _lastAppliedStyle = _state.Style.Clone();
        _performanceSampler = new WindowsPerformanceSamplingService();
        _weatherService = new WeatherService();
        _citySearchService = new CityLookupService();
        _mappingWatcher = new FileMappingWatchService(SweepGoneFileMappings, ImportNewDesktopEntries);
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _saveTimer.Tick += (_, _) => SaveDeferred();
        _trayIcon = BuildTrayIcon(interactiveQa);
        _desktopSurfaceWatcher = new DesktopSurfaceWatcher(GetDesktopProtectedHandles);
        _hotkeys = new GlobalHotkeyHost();
        _hotkeys.HotkeyPressed += OnGlobalHotkeyPressed;
        _peek = new DesktopPeekController(ReturnPeekedFileBoxes, ReturnSingleFileBoxToDesktop);
        _edgeDock = new FileBoxEdgeDockController(_peek, _windows, ScheduleSave, GetFileBoxOwnerDeviceName);
        // Only the installed app checks on its own; test and interactive runs keep quiet.
        _updater = new AppUpdater(_state.Updates, AppVersion, ScheduleSave, ExitAsync, canAutoCheck: dataDirectory is null);
        _updater.ReleaseFound += OnReleaseFound;
        _iconStore = new DockIconStore(Path.Combine(_store.DataDirectory, "icons"));
        DockAppResolver.IconStore = _iconStore;
        ApplyStyleSettings();
        _dock = new DockHost(
            _state.Dock,
            _state.Style,
            () =>
            {
                ScheduleSave();
                _settingsWindow?.RefreshDockSettings();
                NotifyMultiScreenUiChanged();
            },
            () => OpenSettings("Dock"),
            message => _trayIcon.ShowBalloonTip(
                3000,
                "冰蓝 Dock",
                message,
                TrayTipKind.Info));
        _dock.LauncherRequested += ToggleLauncherPanel;
        _dock.IsLauncherEnabled = () => _state.LauncherEnabled;
        _dock.ReadFileBoxes = ReadLauncherFileBoxes;
        _dock.SendToFileBox = SendLauncherPathToFileBoxAsync;
        _dock.ReadScreenPreferences = ReadScreenSurfacePreferences;
        _dock.GetOrCreateScreenPreferences = GetOrCreateScreenSurfacePreferences;
        _dock.ShellRecovered += _desktopSurfaceWatcher.ScheduleCheck;
        _dock.ExplorerNotified += _cornerReveal.ScheduleZOrderCheck;
        _dock.ShowDesktopTriggered += _desktopSurfaceWatcher.ScheduleToggleDesktopCheck;
        _taskbar = new TaskbarAdapter(TaskbarCheckpointPath(_store.DataDirectory));
        _taskbarMinimizeFocusGuard = new TaskbarMinimizeFocusGuard(
            Dispatcher.CurrentDispatcher,
            () => _dock.HasAttention);
        _taskbar.StatusChanged += OnTaskbarStatusChanged;
        _taskbar.AutoHideTakenOver += () =>
        {
            _state.Taskbar.Mode = TaskbarMode.SystemDefault;
            ScheduleSave();
            _settingsWindow?.RefreshTaskbarSettings();
        };
        _topBarEnvironment = new TopBarEnvironment(this);
        _topBar = new TopBarHost(
            _state.TopBar,
            _state.Style,
            _topBarEnvironment,
            _performanceSampler,
            message => _trayIcon.ShowBalloonTip(
                3000,
                "顶端信息条",
                message,
                TrayTipKind.Info));
        _topBar.ShellRecovered += _desktopSurfaceWatcher.ScheduleCheck;
        _dock.ResolvePanelForeground = current => ResolveLauncherForeground(_topBar.ResolveTrayPanelForeground(current));
        _topBar.ExplorerNotified += _cornerReveal.ScheduleZOrderCheck;
        _topBar.ReadScreenPreferences = ReadScreenSurfacePreferences;
        _dock.ResolveTopBarReservedBottomPx = _topBar.GetReservedBottomForMonitor;
        _topBar.ResolveBottomDockTop = _dock.GetBottomDockTopForMonitor;
        _dock.TryOpenTrayForApplication = TryOpenTrayForApplication;
        StartupLog.Open(_store.DataDirectory);
        UiDiagnostics.Start(
            Dispatcher.CurrentDispatcher,
            _store.DataDirectory,
            trackOperations: Environment.GetEnvironmentVariable("BINGLAN_DIAG_HOOKS") == "1");
        _cleanDesktop = new CleanDesktopAdapter(
            CleanDesktopCheckpointPath(_store.DataDirectory),
            disableShellMutations: interactiveQa);
        _cleanDesktop.TakenOver += () =>
        {
            _state.CleanDesktopEnabled = false;
            ScheduleSave();
            _settingsWindow?.RefreshCleanDesktopSettings();
        };
    }

    public static string TaskbarCheckpointPath(string dataDirectory) =>
        Path.Combine(dataDirectory, "taskbar-recovery.json");

    public static string CleanDesktopCheckpointPath(string dataDirectory) =>
        Path.Combine(dataDirectory, "desktop-icons-recovery.json");

    public void Start()
    {
        _trayIcon.Visible = true;
        _cardRuntimeStarted = true;
        SyncMultiScreenRuntime();
        ApplyDisplayRecovery();
        WindowScreenRecovery.SettleLayout();
        // Catches deletions that happened while the app was off; later ones arrive through
        // the mapping watcher, as do new desktop items through the import watcher. All of
        // it only when the user opted in to file-box automation.
        ApplyFileBoxAutomation();
        StartShellModulesAsync().ContinueWith(
            task => StartupLog.Write($"启动未完成：{task.Exception?.GetBaseException().Message}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        // Leaving high contrast puts the default row surface back; the material's one returns.
        AccessibilityThemeManager.HighContrastChanged += OnHighContrastChanged;
        _fullScreenWatcher.Changed += UpdateSamplingMode;
        _fullScreenWatcher.Changed += () => _cornerReveal.SetSuspended(_fullScreenWatcher.IsGameLikeFullScreenInFront);
        _cornerReveal.Raised += () => _dock.EnsureAboveDesktop();
        _taskbar.ExplorerRestarted += () =>
        {
            ClearProcessIconCachesOnExplorerRestart();
            _cornerReveal.Update();
            _edgeDock.ApplyAll();
        };
        _fullScreenWatcher.Start();
        _edgeDock.ApplyAll();
        _desktopSurfaceWatcher.Changed += OnDesktopSurfaceChanged;
        _desktopSurfaceWatcher.Start();
        _taskbarMinimizeFocusGuard.Start();
        SyncGlobalHotkeys();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        WindowScreenRecovery.LayoutChanged += OnDisplayLayoutChanged;
        SaveNow();
        if (_showOnboarding && !_state.OnboardingCompleted)
        {
            ShowOnboarding();
        }
    }

    private void RestoreRootWindows()
    {
        using (WindowScreenRecovery.DeferShowRecovery())
        {
            var informationVisible = HasVisibleInformationComponent();
            foreach (var information in _state.InformationWidgets.ToList())
            {
                if (informationVisible)
                {
                    foreach (var kind in InformationComponentKinds.Where(
                                 kind => _state.DesktopExperience.GetComponent(kind).IsVisible
                                     && !IsInformationCardSuppressed(null, kind)))
                    {
                        ShowInformation(information, kind);
                    }
                }
            }
            ApplyQuickPlaces();
            var todoComponent = _state.DesktopExperience.GetComponent(DesktopComponentKind.Todo);
            var primaryTodos = ScreenProfileAccessor.GetTodosForScreen(_state, screenKey: null);
            // Reconcile legacy/inconsistent state where Todo remained marked visible with no cards,
            // so later unrelated settings changes cannot resurrect a default to-do card.
            if (todoComponent.IsVisible && primaryTodos.Count == 0)
            {
                todoComponent.IsVisible = false;
            }
            var todoVisible = todoComponent.IsVisible;
            var primaryTodoId = primaryTodos.FirstOrDefault()?.Id;
            foreach (var todo in primaryTodos)
            {
                if (todoVisible)
                {
                    if (todo.Id == primaryTodoId)
                    {
                        todo.Placement = todoComponent.Placement;
                        todo.IsLocked = todoComponent.IsLocked;
                    }
                    ShowTodo(todo);
                }
            }
            foreach (var note in ScreenProfileAccessor.GetNotesForScreen(_state, screenKey: null))
            {
                ShowNote(note);
            }
            foreach (var group in FileBoxGroupRules.PartitionIntoHosts(
                         ScreenProfileAccessor.FilterPrimaryFileBoxes(_state.FileBoxes)))
            {
                ShowFileBoxGroup(group);
            }
        }
    }

    /// <summary>
    /// Starts the parts that talk to Explorer: desktop icon recovery, the dock's reserved
    /// space, the taskbar look and clean desktop. Right after logon Explorer is still
    /// building the desktop and taskbar, so they wait until it is ready and settled
    /// instead of competing with it; none of it runs on the UI thread's critical path.
    /// </summary>
    private async Task StartShellModulesAsync()
    {
        StartupLog.Write("启动");
        if (Environment.TickCount64 < LogonWindow.TotalMilliseconds)
        {
            await WaitForExplorerAsync();
            if (Environment.TickCount64 < EarlyLogonWindow.TotalMilliseconds)
            {
                StartupLog.Write("资源管理器就绪，短暂等待桌面稳定");
                await Task.Delay(LogonSettleDelay);
            }
        }
        if (_isExiting)
        {
            return;
        }

        var path = CleanDesktopCheckpointPath(_store.DataDirectory);
        await _cleanDesktopGate.WaitAsync();
        try
        {
            if (_state.CleanDesktopEnabled)
            {
                var (succeeded, restoredWhenFailed, originalKnown, _) = await _cleanDesktop.ResumeOrEnableAsync();
                if (succeeded)
                {
                    _cleanDesktopRestoreFailed = false;
                    _cleanDesktopRecovered = true;
                    StartupLog.Write("清爽桌面已应用");
                }
                else
                {
                    _cleanDesktopRestoreFailed = !restoredWhenFailed;
                    _cleanDesktopRecovered = restoredWhenFailed && originalKnown;
                    _state.CleanDesktopEnabled = false;
                    _settingsWindow?.RefreshCleanDesktopSettings();
                }
            }
            else
            {
                var restored = _cleanDesktop.IsShellMutationDisabled
                    || await Task.Run(() => CleanDesktopAdapter.RecoverNow(path));
                _cleanDesktopRestoreFailed = !restored;
                _cleanDesktopRecovered = restored
                    && (_cleanDesktop.IsShellMutationDisabled || CleanDesktopAdapter.IsOriginalStateKnown(path));
            }
        }
        finally
        {
            _cleanDesktopGate.Release();
        }
        StartupLog.Write("桌面图标状态检查完成");
        if (_isExiting)
        {
            return;
        }

        RunShellStep("桌面空白双击监听", () => _ = SyncDesktopDoubleClickWatcher());
        _settingsWindow?.RefreshCleanDesktopSettings();

        RunShellStep("顶端信息条", _topBar.Apply);
        RunShellStep("Dock ", _dock.Apply);
        RefreshInformationCardVisibility();
        // A bar-only desktop has no information card whose load would start the shared
        // sampler, so the bar brings it up itself here.
        RunShellStep("共享采样", UpdateSamplingMode);
        await ApplyTaskbarModeAsync();
        StartupLog.Write("任务栏模式已应用");
        RunShellStep("任务栏边缘唤出", () => _cornerReveal.SetMode(_state.Taskbar.EdgeRevealMode));
        if (!_isExiting) TopBarWindow.EnableTrayPanelWarmup();
        if (!_cleanDesktopRecovered)
        {
            // The original icon state is unknown or could not be put back; stay off
            // instead of recording a hidden desktop as the new original.
            _state.CleanDesktopEnabled = false;
            _settingsWindow?.RefreshCleanDesktopSettings();
            if (_cleanDesktopRestoreFailed)
            {
                _trayIcon.ShowBalloonTip(
                    5000,
                    "清爽桌面",
                    "上次隐藏的桌面图标未能自动恢复，可在设置的“桌面”页点“重新恢复”再试。",
                    TrayTipKind.Warning);
            }
        }
        ScheduleSave();
        _updater.Start();
        ScheduleIdleMemoryTrim(StartupMemoryTrimDelay);
    }

    private void OnReleaseFound(UpdateRelease release)
    {
        var version = release.Version.ToString(3);
        if (_updateMenuItem is not null)
        {
            _updateMenuItem.Text = $"新版本 {version} 可用…";
            _updateMenuItem.Visible = true;
        }
        _balloonClicked = () => OpenSettings("About");
        _trayIcon.ShowBalloonTip(
            8000,
            "冰蓝桌面有新版本",
            $"{version} 已发布，点击查看更新内容并安装。",
            TrayTipKind.Info);
    }

    private void OnHighContrastChanged(object? sender, EventArgs e) => ApplyItemSurface();

    /// <summary>
    /// Gives every card the look read from a Rainmeter skin: text colour, backing colour
    /// and opacity, accent, corner and, when it is installed, the font. The current
    /// settings are backed up first, so the change can be undone from the backup list.
    /// </summary>
    private string ApplyRainmeterLook(RainmeterLook look, string skinName)
    {
        if (look.IsEmpty)
        {
            return $"“{skinName}”中没有找到字体或颜色设置";
        }

        CaptureAll();
        try
        {
            _store.CreateBackup(_state, allowWhenReloadRequired: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return $"备份未完成：{exception.Message}";
        }
        var style = _state.Style;
        if (look.TextColor is { } text)
        {
            style.TextInk = TextInk.Custom;
            style.CustomTextColor = text;
        }
        if (look.AccentColor is { } accent)
        {
            style.AccentColor = accent;
        }
        var color = look.BackgroundColor;
        if (look.BackgroundOpacity is { } opacity)
        {
            // A backing the eye barely sees reads as no backing at all.
            style.Glass = opacity < 0.08d
                ? GlassMode.Clear
                : IsDarkColor(color) ? GlassMode.Dark : GlassMode.Light;
            style.GlassDensity = opacity;
        }
        ApplyGlass();

        if (color is not null && style.Glass != GlassMode.Clear)
        {
            foreach (var component in _state.DesktopExperience.Components.Where(component =>
                         component.Kind != DesktopComponentKind.Greeting))
            {
                component.Appearance.BackgroundColor = color;
            }
            foreach (var window in _windows.Where(window => window is not InformationWidgetWindow))
            {
                window.SetWidgetBackgroundColor(color);
                window.SetWidgetHeaderColor(color);
            }
        }
        if (look.CornerRadius is { } radius)
        {
            foreach (var component in _state.DesktopExperience.Components)
            {
                component.CornerRadius = radius;
            }
            foreach (var window in _windows.Where(window => window is not InformationWidgetWindow))
            {
                window.WidgetCornerRadius = radius;
            }
        }
        var fontNote = "";
        if (look.FontFace is { } font)
        {
            var installed = InstalledFontCatalog.Names.FirstOrDefault(
                name => string.Equals(name, font, StringComparison.CurrentCultureIgnoreCase));
            if (installed is null)
            {
                fontNote = $"；字体“{font}”本机未安装，沿用原字体";
            }
            else
            {
                foreach (var kind in InformationComponentKinds)
                {
                    _state.DesktopExperience.GetComponent(kind).FontFamily = installed;
                }
            }
        }
        ApplyDesktopExperience(_state.DesktopExperience);
        CaptureAll();
        SaveNow();
        return $"已按“{skinName}”调整卡片外观，原设置已备份{fontNote}";
    }

    private static bool IsDarkColor(string? color)
    {
        if (color is null || System.Windows.Media.ColorConverter.ConvertFromString(color) is not System.Windows.Media.Color value)
        {
            return false;
        }
        return (0.299 * value.R) + (0.587 * value.G) + (0.114 * value.B) < 128;
    }

    private DateTime _lastFailureNotice;

    /// <summary>Tells the user an action did not finish, at most once a minute.</summary>
    internal void ReportFailedAction()
    {
        if (DateTime.UtcNow - _lastFailureNotice < TimeSpan.FromMinutes(1))
        {
            return;
        }
        _lastFailureNotice = DateTime.UtcNow;
        _trayIcon.ShowBalloonTip(
            4000,
            "冰蓝桌面",
            "刚才的操作没有完成。",
            TrayTipKind.Warning);
    }

    // A step that fails is logged and the other modules still start.
    private static void RunShellStep(string name, Action step)
    {
        try
        {
            step();
            StartupLog.Write($"{name}已应用");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StartupLog.Write($"{name}未能应用：{exception.GetType().Name} {exception.Message}");
        }
    }

    // Started within this long after Windows started counts as starting at logon.
    private static readonly TimeSpan LogonWindow = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan EarlyLogonWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LogonSettleDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ExplorerWaitLimit = TimeSpan.FromSeconds(30);

    // Explorer is ready once the taskbar, desktop window and Shell desktop view are reachable.
    private static async Task WaitForExplorerAsync()
    {
        var deadline = DateTime.UtcNow + ExplorerWaitLimit;
        while (DateTime.UtcNow < deadline)
        {
            if (Interop.TaskbarNativeMethods.FindWindowW("Shell_TrayWnd", null) != 0
                && Interop.TaskbarNativeMethods.FindWindowW("Progman", null) != 0
                && await Task.Run(IsDesktopShellViewReady))
            {
                return;
            }
            await Task.Delay(250);
        }
    }

    private static bool IsDesktopShellViewReady()
    {
        try
        {
            using var view = DesktopShellView.Open();
            _ = view.ReadFolderFlags();
            return true;
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException
            or InvalidCastException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private InformationWidgetState InformationState()
    {
        if (_state.InformationWidgets.FirstOrDefault() is { } existing)
        {
            return existing;
        }

        var created = new InformationWidgetState();
        _state.InformationWidgets.Add(created);
        return created;
    }

    private void ApplyGreeting(string greetingName, bool use24HourClock)
    {
        var state = InformationState();
        state.GreetingName = greetingName;
        state.Use24HourClock = use24HourClock;
        foreach (var information in _windows.OfType<InformationWidgetWindow>())
        {
            information.ApplyDesktopExperience(GetExperienceForWindow(information));
            information.RefreshConfiguredValues(false);
        }
        _topBar?.RefreshClock();
        SaveNow();
    }

    private void ApplyCity(CitySearchResult city)
    {
        var state = InformationState();
        state.WeatherCity = city.DisplayName;
        state.WeatherLatitude = city.Latitude;
        state.WeatherLongitude = city.Longitude;
        foreach (var information in _windows.OfType<InformationWidgetWindow>())
        {
            information.RefreshConfiguredValues(true);
        }
        SaveNow();
    }

    private void ShowOnboarding()
    {
        var guide = new OnboardingWindow(new OnboardingActions
        {
            CitySearch = _citySearchService,
            Monitors = MonitorIdentityResolver.ResolveConnected()
                .Where(monitor => monitor.IsPrimary || monitor.IsPersistentIdentity)
                .Select(monitor => new OnboardingMonitor(monitor.IsPrimary ? null : monitor.MonitorKey,
                    monitor.IsPrimary ? "跟随 Windows 主显示器" : monitor.DisplayLabel, monitor.IsPrimary))
                .ToList(),
            ApplyMonitor = monitorKey =>
            {
                _state.Dock.MonitorKey = monitorKey;
                _state.Dock.MonitorDeviceName = null;
                _dock.Apply();
                ScheduleSave();
            },
            DownloadsFolder = Interop.KnownFolders.GetDownloadsPath() is { } downloads && Directory.Exists(downloads) ? downloads : null,
            GreetingName = InformationState().GreetingName,
            CanChangeStartup = _startup.CanModify,
            ApplyPreset = preset =>
            {
                _state.DesktopExperience.ActivePreset = preset;
                ApplyLayoutPreset(_state.DesktopExperience);
            },
            ReadTheme = ReadTheme,
            ImportTheme = ImportThemeAsync,
            ApplyGreetingName = name => ApplyGreeting(name, InformationState().Use24HourClock),
            ApplyCity = ApplyCity,
            DetectEntries = DetectStarterEntriesAsync,
            ApplyEntries = ApplyStarterEntries,
            ApplyDesktopMode = mode =>
            {
                DesktopModeRules.Apply(
                    mode,
                    _state.Dock,
                    _state.Taskbar,
                    _state.TopBar,
                    _state.DesktopExperience,
                    updateInformationCards: !ScreenProfileAccessor.IsMultiScreenEnabled(_state));
                _topBar.Apply();
                _dock.Apply();
                ApplyDesktopExperience(_state.DesktopExperience);
                RefreshInformationCardVisibility();
                UpdateSamplingMode();
                _ = ApplyTaskbarModeAsync();
                SaveNow();
            },
            SetStartupEnabled = _startup.SetEnabled,
            Complete = () =>
            {
                _state.OnboardingCompleted = true;
                SaveNow();
            },
            OpenSettings = () => OpenSettings()
        });
        guide.Show();
        guide.Activate();
    }

    private static async Task<IReadOnlyList<OnboardingEntry>> DetectStarterEntriesAsync()
    {
        var resolver = await Task.Run(ThemeAppResolver.Create);
        return AppSlotCatalog.StarterSlots
            .Select(slot =>
            {
                var binding = new ThemeAppBinding { Slot = slot };
                var app = AppSlotCatalog.CandidatesFor(slot)
                    .Select(candidate => resolver.Resolve(binding, candidate))
                    .FirstOrDefault(candidate => candidate is not null);
                return new OnboardingEntry(slot, app);
            })
            .ToArray();
    }

    private void ApplyStarterEntries(IReadOnlyList<DockPinnedApp> apps, string? downloadsFolder)
    {
        foreach (var app in apps)
        {
            DockPinRules.Pin(_state.Dock, app);
        }
        _dock.Apply();

        if (downloadsFolder is { } downloads && Directory.Exists(downloads))
        {
            var box = _state.FileBoxes.FirstOrDefault();
            if (box is null)
            {
                AddRootFileBox();
                box = _state.FileBoxes[^1];
            }
            var window = _windows.OfType<FileBoxWindow>().FirstOrDefault(candidate => candidate.State.Id == box.Id);
            if (window is not null)
            {
                _ = window.AddNamedEntryAsync(downloads, "下载");
            }
            else
            {
                FileMappingService.AddSystemEntry(box, downloads, "下载");
            }
        }
        SaveNow();
    }

    private void UpdateSamplingMode()
    {
        foreach (var window in _windows.OfType<InformationWidgetWindow>())
        {
            window.SetAudioSpectrumSuspended(_fullScreenWatcher.IsFullScreenInFront || _gameModeActive);
        }

        var hasVisiblePerformanceCard = !_desktopHidden
            && _windows.OfType<InformationWidgetWindow>().Any(window =>
                window.IsVisible
                && window.ComponentKind is null or DesktopComponentKind.Performance);
        var hasTopBarPerformance = _topBar.HasWindow
            && !_gameModeActive
            && TopBarRules.IsModuleOn(_state.TopBar.Modules, TopBarModuleKind.Performance);
        if (hasVisiblePerformanceCard || hasTopBarPerformance)
        {
            if (!_performanceSampler.IsRunning)
            {
                _performanceSampler.Start();
            }
            _performanceSampler.SetMode(PerformanceSamplingRules.ResolveMode(
                _fullScreenWatcher.IsFullScreenInFront || _gameModeActive,
                false));
            return;
        }
        _performanceSampler.Stop();
    }

    // Runs after monitors are added, removed or rearranged, or scaling changes.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => OnDisplayLayoutChanged();

    // Cards go back to where they last sat on the arrangement now in use; an arrangement
    // seen for the first time gathers its cards fully into view.
    private void OnDisplayLayoutChanged()
    {
        if (_isExiting)
        {
            return;
        }

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            () =>
            {
                if (_isExiting)
                {
                    return;
                }

                HandleMultiScreenDisplayLayoutChanged();
                _topBar.Apply();
                _dock.Apply();
                RefreshInformationCardVisibility();
                UpdateSamplingMode();
                NotifyMultiScreenUiChanged();
                if (!ScreenProfileAccessor.IsMultiScreenEnabled(_state))
                {
                    ApplyDisplayRecovery();
                }
                // A restored spot can sit under the taskbar or dock of this arrangement.
                foreach (var window in _windows)
                {
                    window.ApplyReservedEdges(WindowScreenRecovery.ReservedEdgeTarget);
                }
                // Saving captures the cards again, now remembered for this arrangement.
                WindowScreenRecovery.SettleLayout();
                if (_state.Taskbar.EdgeRevealMode != TaskbarEdgeRevealMode.AnyEdge)
                {
                    _cornerReveal.Update();
                }
                _edgeDock.InvalidateMonitors();
                _edgeDock.ApplyAll();
                _settingsWindow?.RefreshTaskbarSettings();
                ScheduleSave();
            });
    }

    // Cards meet the arrangement of monitors now in use: ones that sat on it before go
    // back to their remembered spot, first-time arrivals are gathered into view clear
    // of the cards that already settled.
    private void ApplyDisplayRecovery()
    {
        if (ScreenProfileAccessor.IsMultiScreenEnabled(_state))
        {
            foreach (var window in _windows.Where(window => !IsSecondaryWindow(window) && window.IsVisible))
            {
                PlaceRootWindow(window);
            }
            return;
        }

        var arrivals = new List<WidgetWindowBase>();
        var settled = new List<WidgetWindowBase>();
        foreach (var window in _windows)
        {
            if (IsSecondaryWindow(window))
            {
                continue;
            }

            if (window.HasDisplayLayoutMemory())
            {
                window.RestoreDisplayLayout();
                WindowScreenRecovery.EnsureOnScreen(window);
                settled.Add(window);
            }
            else
            {
                arrivals.Add(window);
            }
        }
        WindowScreenRecovery.GatherOnArrival(arrivals, settled);
    }

    private void ToggleDesktop()
    {
        _desktopToggleVersion++;
        if (_desktopHidden)
        {
            _desktopHidden = false;
            foreach (var window in _windowsHiddenFromTray.Where(_windows.Contains))
            {
                if (IsSecondaryWindow(window))
                {
                    window.Show();
                }
                else
                {
                    ShowRootWindow(window);
                }
            }
            _windowsHiddenFromTray.Clear();
            SyncMultiScreenRuntime();
        }
        else
        {
            _windowsHiddenFromTray.Clear();
            foreach (var window in _windows.Where(window => window.IsVisible).ToList())
            {
                _windowsHiddenFromTray.Add(window);
                window.HideFromApp();
            }
            _desktopHidden = true;
        }

        if (_toggleDesktopMenuItem is not null)
        {
            _toggleDesktopMenuItem.Text = _desktopHidden ? "显示桌面组件" : "隐藏桌面组件";
        }
        UpdateSamplingMode();
    }

    internal void ToggleGameMode() => SetGameMode(!_gameModeActive);

    internal void SetGameMode(bool active)
    {
        if (_isExiting || _exitRequested || _gameModeActive == active)
        {
            return;
        }

        _gameModeActive = active;
        _topBarEnvironment.UpdatePeripheralBatterySource();
        if (active)
        {
            _launcherPanel?.Dismiss(restoreForeground: false);
            if (_peek.IsActive)
            {
                _peek.End(restoreForeground: false);
            }
        }

        _topBar.SetSuppressed(active);
        _dock.SetSuppressed(active);
        RefreshInformationCardVisibility();
        SyncGlobalHotkeys();
        if (_gameModeMenuItem is not null)
        {
            _gameModeMenuItem.Text = active ? "退出免打扰（游戏模式）" : "免打扰（游戏模式）";
        }
        if (_launcherMenuItem is not null)
        {
            _launcherMenuItem.Visible = !active && _state.LauncherEnabled;
        }
        if (_peekMenuItem is not null)
        {
            _peekMenuItem.Visible = !active;
        }
        _trayIcon.SetText(active
            ? $"{_trayBaseText}（免打扰模式）"
            : _trayBaseText);
        UpdateSamplingMode();
        if (active)
        {
            _trayIcon.ShowBalloonTip(
                3000,
                "免打扰（游戏模式）已开启",
                "已暂时隐藏 Dock 与顶端信息条，避免全屏游戏或演示误触；右键托盘图标可随时退出。",
                TrayTipKind.Info);
        }
    }

    internal void OnQueryEndSession()
    {
        if (_isExiting)
        {
            return;
        }

        // WM_QUERYENDSESSION can still be cancelled by another application; save now without
        // disposing Shell adapters or locking in reboot behaviour until WM_ENDSESSION confirms.
        _sessionEndingPending = true;
        _saveTimer.Stop();
        SaveNow();
    }

    private void OnSessionEndingResolved(bool ending)
    {
        if (!ending)
        {
            _sessionEndingPending = false;
            _cleanDesktop.CancelKeepHiddenForReboot();
            return;
        }

        FinalizeSessionEnd();
    }

    private void FinalizeSessionEnd()
    {
        if (_isExiting || _sessionEndedHandled)
        {
            return;
        }

        _sessionEndedHandled = true;
        _sessionEndingPending = false;
        Dock.DockCompositionBackdrop.MarkSessionEnding();
        _saveTimer.Stop();
        StopIdleMemoryTrimTimer();
        SaveNow();
        if (_cleanDesktop.IsActive && _state.CleanDesktopEnabled && _startup.IsEnabled())
        {
            _cleanDesktop.KeepHiddenForReboot();
        }
        else
        {
            _cleanDesktop.Dispose();
        }
        _launcherPanel?.Close();
        _dock.Dispose();
        _topBar.Dispose();
        _taskbar.Dispose();
    }

    public void Dispose()
    {
        if (_sessionEndingPending && !_sessionEndedHandled)
        {
            FinalizeSessionEnd();
        }
        var canCaptureSecondaryPlacement = !WindowScreenRecovery.IsLayoutTransitioning()
            && MonitorIdentityResolver.HasMultipleMonitors();
        DisposeMultiScreenHost(capturePlacement: canCaptureSecondaryPlacement);
        if (!_isExiting)
        {
            SaveNow();
        }
        _isExiting = true;
        StopIdleMemoryTrimTimer();
        _saveTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _launcherPanel?.Close();
        _settingsWindow?.Close();
        _settingsWindow = null;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        WindowScreenRecovery.LayoutChanged -= OnDisplayLayoutChanged;
        AccessibilityThemeManager.HighContrastChanged -= OnHighContrastChanged;
        _saveTimer.Stop();
        _peek.Dispose();
        _fullScreenWatcher.Dispose();
        _desktopSurfaceWatcher.Dispose();
        StopDesktopDoubleClickWatcher();
        _hotkeys.Dispose();
        _mappingWatcher.Dispose();
        _edgeDock.Dispose();
        _dock.Dispose();
        _taskbarMinimizeFocusGuard.Dispose();
        _cornerReveal.Dispose();
        _taskbar.Dispose();
        _cleanDesktop.Dispose();
        _updater.Dispose();
        _topBar.Dispose();
        _trayOverflow?.Dispose();
        _performanceSampler.Dispose();
    }

    private bool IsPrimaryTodo(TodoWidgetState state) =>
        ScreenProfileAccessor.GetPrimaryTodoId(_state, screenKey: null) == state.Id;

    private TodoWidgetWindow CreateTodoWindow(TodoWidgetState state)
    {
        SanitizeRootPlacement(state.Placement);
        var window = new TodoWidgetWindow(state)
        {
            ShowInTaskbar = false
        };
        window.SameKindOtherTitles = () => _state.TodoWidgets
            .Where(other => other.Id != state.Id)
            .Select(other => other.Title);
        if (IsPrimaryTodo(state))
        {
            var component = _state.DesktopExperience.GetComponent(DesktopComponentKind.Todo);
            CopyTodoLookFromComponent(state, component);
            ApplyTodoComponent(window, component);
        }
        Wire(window);
        _windows.Add(window);
        return window;
    }

    private TodoWidgetWindow ShowTodo(TodoWidgetState state)
    {
        var window = CreateTodoWindow(state);
        ShowRootWindow(window);
        return window;
    }

    // The primary todo card follows the built-in Todo component; extra todo cards start
    // with the component's look and keep their own settings afterwards.
    private static void ApplyTodoComponent(TodoWidgetWindow window, DesktopComponentState component)
    {
        window.SetWidgetBackgroundColor(component.Appearance.BackgroundColor);
        window.SetWidgetBackgroundOpacity(component.Appearance.BackgroundOpacity);
        window.SetWidgetHeaderColor(component.Appearance.HeaderColor);
        window.SetWidgetHeaderOpacity(component.Appearance.HeaderOpacity);
        window.SetWidgetTextColor(component.Appearance.TextColor);
        window.SetContentFontScale(component.FontScale);
        window.SetContentFontFamily(component.FontFamily);
        window.WidgetCornerRadius = component.CornerRadius;
    }

    private static void CopyTodoLookFromComponent(TodoWidgetState target, DesktopComponentState component)
    {
        target.Appearance.BackgroundColor = component.Appearance.BackgroundColor;
        target.Appearance.BackgroundOpacity = component.Appearance.BackgroundOpacity;
        target.Appearance.HeaderColor = component.Appearance.HeaderColor;
        target.Appearance.HeaderOpacity = component.Appearance.HeaderOpacity;
        target.Appearance.TextColor = component.Appearance.TextColor;
        target.Appearance.TitleFontFamily = component.Appearance.TitleFontFamily;
        target.Appearance.TitleFontBold = component.Appearance.TitleFontBold;
        target.Appearance.TitleFontItalic = component.Appearance.TitleFontItalic;
        target.CornerRadius = component.CornerRadius;
        target.FontScale = component.FontScale;
        target.FontFamily = component.FontFamily;
    }

    private static void SyncComponentFromTodo(DesktopComponentState component, TodoWidgetState source)
    {
        component.Placement = new WindowPlacement
        {
            Left = source.Placement.Left,
            Top = source.Placement.Top,
            Width = source.Placement.Width,
            Height = source.Placement.Height,
            DisplayLayouts = (source.Placement.DisplayLayouts ?? [])
                .Select(entry => new DisplayLayoutPlacement
                {
                    Layout = entry.Layout,
                    Left = entry.Left,
                    Top = entry.Top,
                    Right = entry.Right,
                    Bottom = entry.Bottom
                })
                .ToList()
        };
        component.IsLocked = source.IsLocked;
        component.Appearance.BackgroundColor = source.Appearance.BackgroundColor;
        component.Appearance.BackgroundOpacity = source.Appearance.BackgroundOpacity;
        component.Appearance.HeaderColor = source.Appearance.HeaderColor;
        component.Appearance.HeaderOpacity = source.Appearance.HeaderOpacity;
        component.Appearance.TextColor = source.Appearance.TextColor;
        component.Appearance.TitleFontFamily = source.Appearance.TitleFontFamily;
        component.Appearance.TitleFontBold = source.Appearance.TitleFontBold;
        component.Appearance.TitleFontItalic = source.Appearance.TitleFontItalic;
        component.CornerRadius = source.CornerRadius;
        component.FontScale = source.FontScale;
        component.FontFamily = source.FontFamily;
    }

    private FileBoxWindow ShowFileBox(FileBoxState state) => ShowFileBoxGroup([state]);

    private void ShowNote(NoteWidgetState state)
    {
        SanitizeRootPlacement(state.Placement);
        var window = new NoteWidgetWindow(state)
        {
            ShowInTaskbar = false
        };
        window.SameKindOtherTitles = () => _state.NoteWidgets
            .Where(other => other.Id != state.Id)
            .Select(other => other.Title);
        Wire(window);
        _windows.Add(window);
        if (!state.IsHidden)
        {
            ShowRootWindow(window);
        }
    }

    private InformationWidgetWindow CreateInformationWindow(
        InformationWidgetState state,
        DesktopComponentKind componentKind)
    {
        var component = _state.DesktopExperience.GetComponent(componentKind);
        SanitizeRootPlacement(component.Placement);
        var window = new InformationWidgetWindow(
            state,
            _performanceSampler,
            _weatherService,
            _state.DesktopExperience,
            componentKind)
        {
            ShowInTaskbar = false
        };
        window.SetAudioSpectrumSuspended(_fullScreenWatcher.IsFullScreenInFront || _gameModeActive);
        Wire(window);
        _windows.Add(window);
        return window;
    }

    private InformationWidgetWindow ShowInformation(
        InformationWidgetState state,
        DesktopComponentKind componentKind)
    {
        var component = _state.DesktopExperience.GetComponent(componentKind);
        var window = CreateInformationWindow(state, componentKind);
        window.ApplyPlacement(component.Placement, component.IsLocked);
        ShowRootWindow(window);
        return window;
    }

    private void Wire(WidgetWindowBase window)
    {
        window.ApplyStyle(_state.Style);
        window.HeaderToolsOnHover = _state.HeaderToolsOnHover;
        // A card that appears while the desktop is shown would be covered by the
        // raised desktop surface; lift it with the others.
        window.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                if (_desktopSurfaceWatcher.IsDesktopShown)
                {
                    PlaceCardAboveShellDesktop(window);
                }
                else
                {
                    window.ReturnBelowOrdinaryWindows();
                }
            }
        };
        window.ContentRendered += (_, _) =>
        {
            if (_desktopSurfaceWatcher.IsDesktopShown && window.IsVisible)
            {
                PlaceCardAboveShellDesktop(window);
            }
        };
        window.ShellRecovered += recovered =>
        {
            if (_desktopSurfaceWatcher.IsDesktopShown && recovered.IsVisible)
            {
                PlaceCardAboveShellDesktop(recovered);
            }
            _desktopSurfaceWatcher.ScheduleCheck();
        };
        window.WidgetChanged += OnWindowStateChanged;
        window.DragCompleted += OnWindowDragCompleted;
        window.Selected += selected =>
        {
            foreach (var candidate in _windows)
            {
                candidate.ApplySelection(ReferenceEquals(candidate, selected));
            }
        };
        window.OrganizeDesktopRequested += () => _ = OrganizeDesktopAsync();
        window.OpenAppSettingsRequested += OpenSettings;
        window.DeleteRequested += DeleteWidget;
        window.ExitRequested += Exit;
    }

    private IReadOnlyList<nint> GetDesktopProtectedHandles()
    {
        var handles = new List<nint>(_windows.Count + 4);
        foreach (var window in _windows)
        {
            if (window.IsVisible && window.WindowHandle is var handle && handle != 0)
            {
                handles.Add(handle);
            }
        }
        if (_topBar is not null)
        {
            handles.AddRange(_topBar.ActiveDesktopHandles());
        }
        if (_dock is not null)
        {
            handles.AddRange(_dock.ActiveDesktopHandles());
        }
        return handles;
    }

    /// <summary>
    /// The shell just showed or stopped showing the desktop. While it is shown, the
    /// raised desktop surface (<c>WorkerW</c> or <c>Progman</c>) would cover desktop
    /// cards and bars behind the wallpaper. Cards are lifted within the desktop
    /// surface's band, and TopBar/Dock re-assert their own layers.
    /// </summary>
    private void OnDesktopSurfaceChanged()
    {
        if (_peek.IsActive)
        {
            _peek.End(restoreForeground: false);
        }
        _edgeDock.CollapseAllExpanded();
        if (_desktopSurfaceWatcher.IsDesktopShown)
        {
            var slot = RaiseCardsAboveDesktop();
            _topBar.SetDesktopShown(true, slot, _desktopSurfaceWatcher.IsTopmostLayerRequired);
            _cornerReveal.Raise();
            _dock.EnsureAboveDesktop();
            return;
        }
        WidgetWindowBase.ReturnBelowOrdinaryWindows(_windows);
        _topBar.SetDesktopShown(false, 0, topmost: false);
    }

    private nint RaiseCardsAboveDesktop()
    {
        // Start directly above the wallpaper, then stack the cards in their layout order.
        var slot = FindCardRaiseSlot();
        foreach (var window in _windows.Where(window => window.IsVisible))
        {
            window.RaiseAboveDesktop(slot, _desktopSurfaceWatcher.IsTopmostLayerRequired, keepDesktopOrder: true);
            var handle = window.WindowHandle;
            if (slot != DockNativeMethods.HwndBottom && handle != 0)
            {
                slot = handle;
            }
        }
        return slot;
    }

    private void PlaceCardAboveShellDesktop(WidgetWindowBase window) =>
        window.RaiseAboveDesktop(FindCardRaiseSlot(), _desktopSurfaceWatcher.IsTopmostLayerRequired, keepDesktopOrder: true);

    private nint FindCardRaiseSlot()
    {
        var desktopSurface = DesktopSurfaceWatcher.FindDesktopSurfaceWindow(out _);
        return NativeMethods.GetWindowAboveInBand(desktopSurface, _desktopSurfaceWatcher.IsTopmostLayerRequired);
    }

    private void OnWindowDragCompleted(WidgetWindowBase window)
    {
        var pendingEdgeDock = _pendingEdgeDockRelease;
        if (pendingEdgeDock is { } pending && ReferenceEquals(pending.Box, window))
        {
            _pendingEdgeDockRelease = null;
        }
        else
        {
            pendingEdgeDock = null;
        }

        if (_isApplyingDesktopExperience || window.IsClosingOrClosed || !_windows.Contains(window))
        {
            return;
        }

        if (_multiScreen?.ReconcileWindowScreenAfterMove(window) == true)
        {
            Capture(window);
            ScheduleSave();
        }

        if (pendingEdgeDock is { } edgeDockCandidate
            && !edgeDockCandidate.Box.IsClosingOrClosed
            && _windows.Contains(edgeDockCandidate.Box))
        {
            _edgeDock.TryDockOnDragRelease(
                edgeDockCandidate.Box,
                edgeDockCandidate.CursorX,
                edgeDockCandidate.CursorY);
        }
    }

    private void OnWindowStateChanged(WidgetWindowBase window)
    {
        if (_isApplyingDesktopExperience || window.IsClosingOrClosed || !_windows.Contains(window))
        {
            return;
        }

        Capture(window);
        ScheduleSave();
        if (window is TodoWidgetWindow)
        {
            _topBar.RefreshTodoSummary();
        }
        if (window is FileBoxWindow && _state.FileBoxAutomationEnabled)
        {
            // Box membership changed; keep the watched folders and the gone sweep current.
            _mappingWatcher.RequestSweep();
        }
    }

    private void Capture(WidgetWindowBase window)
    {
        if (window.IsClosingOrClosed || !_windows.Contains(window))
        {
            return;
        }

        if (_multiScreen?.TryCaptureSecondaryWindow(window) == true)
        {
            return;
        }

        if (IsModelOwnedBySecondaryScreen(window))
        {
            return;
        }

        using var suppressPlacement = ScreenProfileAccessor.IsMultiScreenEnabled(_state)
            && (_rootMonitor is null || WindowScreenRecovery.IsLayoutTransitioning()
                || !MonitorIdentityResolver.IsDeviceConnected(_rootMonitor.DeviceName))
            ? window.SuppressPlacementCapture()
            : null;
        switch (window)
        {
            case TodoWidgetWindow todo:
                todo.CaptureState();
                if (IsPrimaryTodo(todo.State))
                {
                    var component = _state.DesktopExperience
                        .GetComponent(DesktopComponentKind.Todo);
                    component.Placement = todo.State.Placement;
                    component.IsLocked = todo.State.IsLocked;
                }
                break;
            case NoteWidgetWindow note:
                note.CaptureState();
                break;
            case InformationWidgetWindow information:
                information.CaptureState();
                if (information.ComponentKind is { } kind)
                {
                    var component = _state.DesktopExperience.GetComponent(kind);
                    information.CopyPlacementTo(component.Placement);
                    component.IsLocked = information.IsWidgetLocked;
                }
                break;
            case FileBoxWindow box:
                box.CaptureState();
                break;
            case QuickPlacesWindow places:
                var quick = _state.DesktopExperience.GetComponent(DesktopComponentKind.QuickLaunch);
                places.CopyPlacementTo(quick.Placement);
                quick.IsLocked = places.IsWidgetLocked;
                break;
        }
        if (suppressPlacement is null)
        {
            CaptureRootLocalPosition(window);
        }
    }

    private static bool IsModelOwnedBySecondaryScreen(WidgetWindowBase window) =>
        window switch
        {
            TodoWidgetWindow todo => !ScreenProfileAccessor.IsPrimary(todo.State.ScreenKey),
            NoteWidgetWindow note => !ScreenProfileAccessor.IsPrimary(note.State.ScreenKey),
            FileBoxWindow box => !ScreenProfileAccessor.IsPrimary(box.State.ScreenKey),
            _ => false
        };

    private void AddTodo()
    {
        if (!TryAddWidgetOnCurrentMainScreen(NewWidgetKind.Todo))
        {
            AddRootTodo();
        }
    }

    private void AddRootTodo()
    {
        var todoComponent = _state.DesktopExperience.GetComponent(DesktopComponentKind.Todo);
        todoComponent.IsVisible = true;
        var primaryTodoCount = ScreenProfileAccessor.GetTodosForScreen(_state, screenKey: null).Count;
        var offset = primaryTodoCount * 24;
        var state = TodoService.CreateDefaultWidget();
        state.Title = WidgetTitleRules.NextTitle(state.Title, _state.TodoWidgets.Select(todo => todo.Title));
        CopyTodoLookFromComponent(state, todoComponent);
        if (primaryTodoCount == 0)
        {
            state.IsLocked = todoComponent.IsLocked;
        }
        state.Placement = primaryTodoCount == 0
            ? todoComponent.Placement
            : new WindowPlacement
            {
                Left = 92 + offset,
                Top = 92 + offset,
                Width = 286,
                Height = 336
            };
        _state.TodoWidgets.Add(state);
        ShowTodo(state);
        ScheduleSave();
    }

    private void AddFileBox()
    {
        if (!TryAddWidgetOnCurrentMainScreen(NewWidgetKind.FileBox))
        {
            AddRootFileBox();
        }
    }

    private FileBoxWindow AddRootFileBox(WindowPlacement? placement = null)
    {
        var offset = ScreenProfileAccessor.GetFileBoxesForScreen(_state, screenKey: null).Count * 24;
        var state = new FileBoxState
        {
            Placement = placement ?? new WindowPlacement
            {
                Left = 432 + offset,
                Top = 92 + offset,
                Width = 366,
                Height = 306
            }
        };
        InitializeFileBox(state);
        var window = ShowFileBox(state);
        ScheduleSave();
        return window;
    }

    private void InitializeFileBox(FileBoxState state)
    {
        state.Title = WidgetTitleRules.NextTitle(state.Title, _state.FileBoxes.Select(box => box.Title));
        UseSharedLook(state.Appearance);
        _state.FileBoxes.Add(state);
    }

    private bool TryAddWidgetOnCurrentMainScreen(NewWidgetKind kind)
    {
        if (_multiScreen is not { } host)
        {
            return false;
        }

        var monitor = host.ConnectedMonitors.FirstOrDefault(candidate => candidate.IsPrimary);
        if (monitor is { IsPersistentIdentity: true })
        {
            var key = host.GetScreenKeyForMonitor(monitor);
            if (key is null)
            {
                return false;
            }

            host.AddWidgetOnScreen(key, kind);
        }
        return true;
    }

    // 框选新建：托盘发起的瞬态全屏遮罩。用户拖出的矩形就是新分组盒的位置与尺寸；
    // 遮罩自己负责取消路径（Esc、右键、失焦），这里只接收最终矩形。
    private void StartMarqueeFileBox()
    {
        var overlay = new BoxMarqueeOverlay();
        overlay.BoxSelected += rect =>
        {
            var placement = new WindowPlacement
            {
                Left = rect.Left,
                Top = rect.Top,
                // 小于分组盒最小可读尺寸的框选按最小值补齐，锚点保持在框选左上角。
                Width = Math.Max(rect.Width, 226),
                Height = Math.Max(rect.Height, 166)
            };
            if (WidgetWindowBase.GridSnapEnabled)
            {
                var cell = DesktopStyleRules.GridCellDip;
                placement.Left = Math.Round(placement.Left / cell) * cell;
                placement.Top = Math.Round(placement.Top / cell) * cell;
            }
            if (_multiScreen is null)
            {
                SanitizePlacement(placement);
                AddRootFileBox(placement);
            }
        };
        overlay.PhysicalBoxSelected += physical => AddMarqueeFileBox(physical);
        overlay.Show();
    }

    internal FileBoxWindow? AddMarqueeFileBox(PixelRect physical)
    {
        if (!physical.HasArea)
        {
            return null;
        }

        var monitors = _multiScreen?.ConnectedMonitors.Select(screen => new MonitorSnapshot(
            screen.MonitorHandle, screen.DeviceName, screen.Bounds, screen.WorkingArea, screen.IsPrimary, screen.Dpi)).ToArray()
            ?? MonitorCatalog.GetAll();
        var centerX = physical.Left + physical.Width / 2;
        var centerY = physical.Top + physical.Height / 2;
        var monitor = monitors.FirstOrDefault(screen =>
            centerX >= screen.Bounds.Left && centerX < screen.Bounds.Right
            && centerY >= screen.Bounds.Top && centerY < screen.Bounds.Bottom);
        if (monitor is null)
        {
            return null;
        }

        var scale = ScreenProfileAccessor.ResolveScale(monitor.Dpi);
        var minWidth = (int)Math.Ceiling(226d * scale);
        var minHeight = (int)Math.Ceiling(166d * scale);
        var work = monitor.WorkingArea;
        var left = physical.Left;
        var top = physical.Top;
        if (WidgetWindowBase.GridSnapEnabled)
        {
            var cell = DesktopStyleRules.GridCellDip * scale;
            left = work.Left + (int)Math.Round(Math.Round((left - work.Left) / cell) * cell);
            top = work.Top + (int)Math.Round(Math.Round((top - work.Top) / cell) * cell);
        }

        var width = Math.Clamp(physical.Width, minWidth, Math.Max(minWidth, work.Width));
        var height = Math.Clamp(physical.Height, minHeight, Math.Max(minHeight, work.Height));
        left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width));
        top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height));
        var bounds = new PixelRect(left, top, left + width, top + height);
        var placement = new WindowPlacement
        {
            Left = left / scale,
            Top = top / scale,
            Width = width / scale,
            Height = height / scale
        };

        if (_multiScreen is { } host)
        {
            var target = host.ConnectedMonitors.FirstOrDefault(candidate => candidate.IsPersistentIdentity
                && candidate.MonitorHandle == monitor.MonitorHandle
                && string.Equals(candidate.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                return null;
            }

            if (!MonitorIdentityResolver.IsDeviceConnected(target.DeviceName)
                || WindowScreenRecovery.IsLayoutTransitioning())
            {
                return null;
            }

            ScreenProfileAccessor.RecordMonitorLocalPlacement(placement, bounds, work, target.Dpi);
            DisplayLayoutRules.Remember(placement, MultiScreenHost.CurrentLayoutKey(host.ConnectedMonitors), bounds);
            if (host.GetScreenKeyForMonitor(target) is { } key)
            {
                ScreenProfileAccessor.EnsureScreenProfile(
                    _state, key, target.DeviceName, target.DisplayLabel, target.HardwareId, target.FriendlyName);
                var state = new FileBoxState { ScreenKey = key, Placement = placement };
                InitializeFileBox(state);
                var window = host.ShowDetachedFileBoxOnScreen(key, state);
                NotifyMultiScreenUiChanged(refreshComponentList: true);
                ScheduleSave();
                return window;
            }

            return AddRootFileBox(placement);
        }

        var rootWindow = AddRootFileBox(placement);
        MultiScreenHost.ApplyPhysicalBounds(rootWindow, bounds, monitor.Dpi, placement,
            WindowScreenRecovery.CurrentLayout());
        return rootWindow;
    }

    // A new card starts in the shared card look, like the cards already on the desktop.
    private void UseSharedLook(WidgetAppearanceState appearance)
    {
        var glass = DesktopStyleRules.Appearance(_state.Style);
        appearance.BackgroundColor = glass.BackgroundColor;
        appearance.BackgroundOpacity = glass.BackgroundOpacity;
        appearance.HeaderColor = glass.HeaderColor;
        appearance.HeaderOpacity = glass.HeaderOpacity;
        appearance.TextColor = glass.TextColor;
    }

    private void AddNote()
    {
        if (!TryAddWidgetOnCurrentMainScreen(NewWidgetKind.Note))
        {
            AddRootNote();
        }
    }

    private void AddRootNote()
    {
        var offset = ScreenProfileAccessor.GetNotesForScreen(_state, screenKey: null).Count * 24;
        var state = NoteService.CreateDefaultWidget();
        state.Title = WidgetTitleRules.NextTitle(state.Title, _state.NoteWidgets.Select(note => note.Title));
        UseSharedLook(state.Appearance);
        state.BodyTypography.Color = state.Appearance.TextColor;
        state.Placement = new WindowPlacement
        {
            Left = 232 + offset,
            Top = 148 + offset,
            Width = 306,
            Height = 266
        };
        _state.NoteWidgets.Add(state);
        ShowNote(state);
        ScheduleSave();
    }

    private void AddInformation()
    {
        foreach (var kind in InformationComponentKinds.Where(kind => kind != DesktopComponentKind.AudioVisualizer))
        {
            _state.DesktopExperience.GetComponent(kind).IsVisible = true;
        }

        var state = _state.InformationWidgets.FirstOrDefault();
        if (state is null)
        {
            state = new InformationWidgetState();
            _state.InformationWidgets.Add(state);
        }

        InformationWidgetWindow? first = null;
        foreach (var kind in InformationComponentKinds.Where(kind => kind != DesktopComponentKind.AudioVisualizer))
        {
            var existing = _windows.OfType<InformationWidgetWindow>()
                .FirstOrDefault(window =>
                    window.State.Id == state.Id && window.ComponentKind == kind);
            if (existing is null)
            {
                existing = ShowInformation(state, kind);
            }
            existing.Show();
            first ??= existing;
        }

        if (first is not null)
        {
            if (first.WindowState == WindowState.Minimized)
            {
                first.WindowState = WindowState.Normal;
            }
            first.Activate();
        }
        UpdateSamplingMode();
        ScheduleSave();
    }

    private void OpenSettings() => OpenSettings((WidgetWindowBase?)null);

    private void OpenSettings(string page)
    {
        OpenSettings((WidgetWindowBase?)null);
        _settingsWindow?.ShowSettingsPage(page);
    }

    private void OpenTopBarSettings(TopBarModuleKind? focusModule)
    {
        OpenSettings("TopBar");
        if (focusModule is { } kind)
        {
            _settingsWindow?.SelectTopBarModule(kind);
        }
    }

    private void OpenSettings(DesktopComponentKind componentKind)
    {
        OpenSettings((WidgetWindowBase?)null);
        _settingsWindow?.ShowComponentSettings(componentKind);
    }

    private void OpenSettings(WidgetWindowBase? target)
    {
        if (_peek.IsActive)
        {
            _peek.End(restoreForeground: false);
        }
        if (_settingsWindow is not null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }
            _settingsWindow.Show();
            if (target is not null)
            {
                _settingsWindow.ShowWidgetSettings(target);
            }
            _settingsWindow.Activate();
            return;
        }

        var informationState = _state.InformationWidgets.FirstOrDefault();
        if (informationState is null)
        {
            informationState = new InformationWidgetState();
            _state.InformationWidgets.Add(informationState);
        }

        var state = informationState;
        // A test run with its own data folder never offers to uninstall the real copy.
        var uninstaller = _startup.CanModify ? AppUninstaller.Find(Environment.ProcessPath) : null;
        var settings = new SettingsWindow(
            _citySearchService,
            state,
            _state.DesktopExperience,
            ApplyGreeting,
            ApplyCity,
            ApplyDesktopExperience,
            ApplyLayoutPreset,
            () => _windows.ToList(),
            ApplyWidgetLock,
            _state.Dock,
            ApplyDock,
            _state.Taskbar,
            ApplyTaskbar,
            () => (_taskbar.ActiveMode, _taskbar.Problem),
            new SettingsMaintenance
            {
                CanChangeStartup = _startup.CanModify,
                IsStartupEnabled = _startup.IsEnabled,
                SetStartupEnabled = _startup.SetEnabled,
                PrimaryTodoId = () => ScreenProfileAccessor.GetPrimaryTodoId(_state, screenKey: null),
                DataDirectory = _store.DataDirectory,
                OpenDataDirectory = OpenDataDirectory,
                CreateBackup = () =>
                {
                    CaptureAll();
                    return _store.CreateBackup(_state);
                },
                ListBackups = _store.ListBackups,
                DeleteBackup = _store.DeleteBackup,
                RestoreBackup = RestoreBackupAndRestart,
                ResetWidgetPlacement = ResetWidgetPlacement,
                ImportRainmeterLook = ApplyRainmeterLook,
                QuickPlaces = () => _state.QuickPlaces,
                SetQuickPlaces = items =>
                {
                    _state.QuickPlaces = QuickPlaceRules.Normalize(items);
                    ApplyQuickPlaces();
                    ScheduleSave();
                },
                SetWidgetVisibility = SetWidgetVisibility,
                ToggleFileBoxEdgeDock = (box, enabled) => _edgeDock.ToggleEdgeDock(box, enabled),
                MergeFileBoxes = MergeFileBoxes,
                DetachFileBoxTab = DetachFileBoxTab,
                SplitFileBoxGroup = SplitFileBoxGroup,
                DeleteWidget = window =>
                {
                    var wasVisible = window.IsVisible;
                    DeleteWidget(window);
                    return !_windows.Contains(window) || (wasVisible && !window.IsVisible);
                },
                ExportTheme = ExportTheme,
                ReadTheme = ReadTheme,
                ImportTheme = ImportThemeAsync,
                PinBoundApp = PinBoundApp,
                IsCleanDesktopEnabled = () => _state.CleanDesktopEnabled,
                IsCleanDesktopAvailable = IsCleanDesktopAvailable,
                SetCleanDesktop = SetCleanDesktopAsync,
                IsDoubleClickDesktopToToggleIconsEnabled = () => _state.DoubleClickDesktopToToggleIcons == true,
                SetDoubleClickDesktopToToggleIcons = SetDoubleClickDesktopToToggleIcons,
                IsFileBoxAutomationEnabled = () => _state.FileBoxAutomationEnabled,
                SetFileBoxAutomation = SetFileBoxAutomation,
                IsFileBoxDeleteOriginalsEnabled = () => _state.FileBoxDeleteOriginalsEnabled,
                IsHeaderToolsOnHover = () => _state.HeaderToolsOnHover,
                SetHeaderToolsOnHover = enabled =>
                {
                    _state.HeaderToolsOnHover = enabled;
                    foreach (var card in _windows)
                    {
                        card.HeaderToolsOnHover = enabled;
                    }
                    ScheduleSave();
                },
                Hotkeys = _state.Hotkeys,
                ProbeGlobalHotkey = (kind, text) => _hotkeys.Probe(kind, text),
                ApplyGlobalHotkeys = () =>
                {
                    GlobalHotkeyRules.Normalize(_state.Hotkeys);
                    _state.TopBar.SearchHotkey = _state.Hotkeys.Launcher;
                    SyncGlobalHotkeys();
                    _topBar.Apply();
                    ScheduleSave();
                },
                GetLastSettingsPage = () => _state.LastSettingsPage,
                SetLastSettingsPage = page =>
                {
                    var normalized = SettingsNavigationRules.NormalizePageKey(page);
                    if (!string.Equals(_state.LastSettingsPage, normalized, StringComparison.Ordinal))
                    {
                        _state.LastSettingsPage = normalized;
                        ScheduleSave();
                    }
                },
                SetFileBoxDeleteOriginals = enabled =>
                {
                    _state.FileBoxDeleteOriginalsEnabled = enabled;
                    ScheduleSave();
                },
                CleanDesktopNotice = CleanDesktopNotice,
                CleanDesktopIconsOrphaned = CleanDesktopIconsOrphanedAsync,
                RestoreCleanDesktopIcons = RestoreCleanDesktopIconsAsync,
                Style = _state.Style,
                ApplyStyle = ApplyStyle,
                LauncherAppearance = _state.LauncherAppearance,
                IsLauncherEnabled = () => _state.LauncherEnabled,
                SetLauncherEnabled = SetLauncherEnabled,
                IsLauncherShowRecentFiles = () => _state.LauncherPreferences.ShowRecentFiles,
                SetLauncherShowRecentFiles = enabled =>
                {
                    _state.LauncherPreferences.ShowRecentFiles = enabled;
                    ScheduleSave();
                    _launcherPanel?.RefreshRecentPreference();
                },
                IsLauncherUseEverything = () => _state.LauncherPreferences.UseEverything,
                SetLauncherUseEverything = enabled =>
                {
                    _state.LauncherPreferences.UseEverything = enabled;
                    ScheduleSave();
                },
                GetLauncherFavoriteMode = () => _state.LauncherPreferences.FavoriteMode,
                SetLauncherFavoriteMode = mode =>
                {
                    _state.LauncherPreferences.FavoriteMode = mode;
                    ScheduleSave();
                },
                GetLauncherBackdropMode = () => _state.LauncherPreferences.BackdropMode,
                SetLauncherBackdropMode = mode =>
                {
                    _state.LauncherPreferences.BackdropMode = mode;
                    ScheduleSave();
                },
                ApplyLauncherAppearance = () =>
                {
                    _launcherPanel?.ApplyAppearance();
                    ScheduleSave();
                },
                AddWidget = kind =>
                {
                    switch (kind)
                    {
                        case NewWidgetKind.Note:
                            AddNote();
                            return _windows.OfType<NoteWidgetWindow>().LastOrDefault();
                        case NewWidgetKind.FileBox:
                            AddFileBox();
                            return _windows.OfType<FileBoxWindow>().LastOrDefault();
                        default:
                            AddTodo();
                            return _windows.OfType<TodoWidgetWindow>().LastOrDefault();
                    }
                },
                ExitApp = Exit,
                CanUninstall = uninstaller is not null,
                Uninstall = () =>
                {
                    if (uninstaller is null)
                    {
                        return "没有找到卸载程序";
                    }
                    // The app then exits normally, saving its state and putting the taskbar
                    // and desktop icons back, instead of being stopped by the uninstaller.
                    if (AppUninstaller.Start(uninstaller) is { } error)
                    {
                        return error;
                    }
                    StartupLog.Write("启动卸载程序");
                    Exit();
                    return null;
                },
                Updater = _updater,
                ApplyGlass = ApplyGlass,
                ImportTaskbarPins = () =>
                {
                    var added = _dock.ImportTaskbarPins();
                    _dock.Apply();
                    SaveNow();
                    return added;
                },
                MultiScreen = BuildMultiScreenSettingsBridge()
            },
            topBarState: _state.TopBar,
            applyTopBar: ApplyTopBar,
            probeTopBarSearchHotkey: text => _hotkeys.Probe(GlobalHotkeyKind.Launcher, text));
        settings.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsWindow, settings))
            {
                _settingsWindow = null;
            }
            ScheduleIdleMemoryTrim(WindowClosedMemoryTrimDelay);
        };
        _settingsWindow = settings;
        if (target is not null)
        {
            settings.ShowWidgetSettings(target);
        }
        settings.Show();
        settings.Activate();
    }

    private void ApplyDesktopExperience(DesktopExperienceState experience) =>
        ApplyDesktopExperienceWithSave(experience, saveImmediate: true);

    private void ApplyDesktopExperienceWithSave(DesktopExperienceState experience, bool saveImmediate)
    {
        // Changing which cards show brings back cards hidden from the tray first, so the
        // tray menu and the desktop agree.
        if (_desktopHidden)
        {
            ToggleDesktop();
        }
        _isApplyingDesktopExperience = true;
        try
        {
            ApplyDesktopExperienceCore(experience, saveImmediate);
        }
        finally
        {
            _isApplyingDesktopExperience = false;
        }
        _topBar?.RefreshClock();
    }

    private void ApplyDesktopExperienceCore(DesktopExperienceState experience, bool saveImmediate)
    {
        DesktopExperienceRules.Normalize(experience);
        _state.DesktopExperience = experience;
        if (!CanPresentRootWidgets)
        {
            ScheduleSave();
            return;
        }
        if (HasVisibleInformationComponent() && !_state.InformationWidgets.Any())
        {
            _state.InformationWidgets.Add(new InformationWidgetState());
        }
        var todoComponent = experience.GetComponent(DesktopComponentKind.Todo);
        var primaryTodos = ScreenProfileAccessor.GetTodosForScreen(_state, screenKey: null);
        if (todoComponent.IsVisible && primaryTodos.Count == 0)
        {
            var created = TodoService.CreateDefaultWidget();
            CopyTodoLookFromComponent(created, todoComponent);
            _state.TodoWidgets.Add(created);
            primaryTodos = [created];
        }
        ApplyRootInformationCards(experience);

        if (todoComponent.IsVisible)
        {
            foreach (var state in primaryTodos)
            {
                var window = _windows
                    .OfType<TodoWidgetWindow>()
                    .FirstOrDefault(candidate => !IsSecondaryWindow(candidate) && candidate.State.Id == state.Id);
                if (window is null)
                {
                    window = CreateTodoWindow(state);
                }
                if (IsPrimaryTodo(state))
                {
                    SanitizeRootPlacement(todoComponent.Placement);
                    CopyTodoLookFromComponent(state, todoComponent);
                    ApplyTodoComponent(window, todoComponent);
                    state.IsLocked = todoComponent.IsLocked;
                    state.Placement = todoComponent.Placement;
                }
                window.ApplyPlacement(state.Placement, state.IsLocked);
                ShowRootWindow(window);
            }
        }
        else
        {
            foreach (var window in _windows.OfType<TodoWidgetWindow>().Where(win => !IsSecondaryWindow(win)))
            {
                window.HideFromApp();
            }
        }
        ApplyQuickPlaces();
        UpdateSamplingMode();

        if (saveImmediate)
        {
            SaveNow();
        }
        else
        {
            ScheduleSave();
        }
    }

    private void ApplyRootInformationCards(DesktopExperienceState experience)
    {
        if (HasVisibleInformationComponent())
        {
            foreach (var state in _state.InformationWidgets)
            {
                foreach (var kind in InformationComponentKinds)
                {
                    var component = experience.GetComponent(kind);
                    var window = _windows
                        .OfType<InformationWidgetWindow>()
                        .FirstOrDefault(candidate =>
                            !IsSecondaryWindow(candidate) &&
                            candidate.State.Id == state.Id &&
                            candidate.ComponentKind == kind);
                    if (!component.IsVisible || IsInformationCardSuppressed(null, kind))
                    {
                        if (window is not null)
                        {
                            _windowsHiddenFromTray.Remove(window);
                            window.HideFromApp();
                        }
                        continue;
                    }
                    if (window is null)
                    {
                        window = CreateInformationWindow(state, kind);
                    }
                    window.ApplyDesktopExperience(experience);
                    window.ApplyPlacement(component.Placement, component.IsLocked);
                    ShowRootWindow(window);
                }
            }
        }
        else
        {
            foreach (var window in _windows.OfType<InformationWidgetWindow>().Where(win => !IsSecondaryWindow(win)))
            {
                window.HideFromApp();
            }
        }

    }

    // The row of system places follows its component: shown, placed and coloured by it.
    private void ApplyQuickPlaces()
    {
        var component = _state.DesktopExperience.GetComponent(DesktopComponentKind.QuickLaunch);
        var window = _windows.OfType<QuickPlacesWindow>().FirstOrDefault(win => !IsSecondaryWindow(win));
        if (!CanPresentRootWidgets || !component.IsVisible)
        {
            window?.HideFromApp();
            return;
        }
        if (window is null)
        {
            SanitizeRootPlacement(component.Placement);
            window = new QuickPlacesWindow();
            Wire(window);
            _windows.Add(window);
        }
        window.SetItems(_state.QuickPlaces);
        window.ApplyComponent(component);
        window.ApplyPlacement(component.Placement, component.IsLocked);
        ShowRootWindow(window);
    }

    private void ApplyLayoutPreset(DesktopExperienceState experience)
    {
        DesktopExperienceRules.Normalize(experience);
        _state.DesktopExperience = experience;
        if (_rootMonitor is { } monitor && _multiScreen is { } host)
        {
            ScreenProfileAccessor.ApplyPresetToScreen(_state, null, experience.ActivePreset,
                monitor.WorkingArea, monitor.Dpi, MultiScreenHost.CurrentLayoutKey(host.ConnectedMonitors));
        }
        else if (!ScreenProfileAccessor.IsMultiScreenEnabled(_state))
        {
            ApplyPresetPlacements(experience.ActivePreset);
        }
        ApplyDesktopExperience(experience);
    }

    private void ApplyDock()
    {
        ApplyItemSurface();
        _dock.Apply();
        RefreshInformationCardVisibility();
        NotifyMultiScreenUiChanged();
        _cornerReveal.RaiseIfBehindTaskbar();
        SaveNow();
    }

    private void ApplyTopBar()
    {
        _topBar.Apply();
        _topBarEnvironment.UpdatePeripheralBatterySource();
        _dock.Apply();
        RefreshInformationCardVisibility();
        NotifyMultiScreenUiChanged();
        _cornerReveal.RaiseIfBehindTaskbar();
        SyncGlobalHotkeys();
        UpdateSamplingMode();
        ScheduleSave();
    }

    private void SyncGlobalHotkeys()
    {
        GlobalHotkeyRules.Normalize(_state.Hotkeys);
        _hotkeys.Sync(_state.Hotkeys, _state.LauncherEnabled, _state.CleanDesktopEnabled, suspendAll: _gameModeActive);
    }

    private bool IsTrueFullScreenInFront()
    {
        var foreground = DockNativeMethods.GetForegroundWindow();
        var monitors = MonitorCatalog.GetAll();
        var monitor = DockNativeMethods.GetCursorPos(out var cursor)
            ? monitors.FirstOrDefault(screen => cursor.X >= screen.Bounds.Left && cursor.X < screen.Bounds.Right
                && cursor.Y >= screen.Bounds.Top && cursor.Y < screen.Bounds.Bottom)
            : null;
        monitor ??= monitors.FirstOrDefault();
        return monitor is not null && FullScreenWatcher.IsFullScreen(foreground, monitor.MonitorHandle, excludeMaximized: true);
    }

    private void OnGlobalHotkeyPressed(GlobalHotkeyKind kind)
    {
        if (_isExiting || _exitRequested)
        {
            return;
        }

        // Both panels can change foreground focus across monitors, including when closing each other.
        if ((kind is GlobalHotkeyKind.Launcher or GlobalHotkeyKind.LauncherFiles or GlobalHotkeyKind.PeekFileBoxes)
            && FullScreenWatcher.IsFullScreen(DockNativeMethods.GetForegroundWindow(), excludeMaximized: true))
        {
            return;
        }

        switch (kind)
        {
            case GlobalHotkeyKind.Launcher:
                if (_peek.IsActive)
                {
                    _peek.End(restoreForeground: false);
                }
                ToggleLauncherPanel();
                break;
            case GlobalHotkeyKind.LauncherFiles:
                if (_peek.IsActive)
                {
                    _peek.End(restoreForeground: false);
                }
                ToggleLauncherPanelForTab(LauncherMainTab.Files);
                break;
            case GlobalHotkeyKind.PeekFileBoxes:
                if (_launcherPanel is { } openPanel)
                {
                    openPanel.Dismiss(restoreForeground: false);
                }
                TogglePeekFileBoxes();
                break;
            case GlobalHotkeyKind.ToggleWidgets:
                if (!IsTrueFullScreenInFront())
                {
                    if (_peek.IsActive)
                    {
                        _peek.End(restoreForeground: false);
                    }
                    ToggleDesktop();
                }
                break;
            case GlobalHotkeyKind.QuickTodo:
                if (!IsTrueFullScreenInFront())
                {
                    if (_peek.IsActive)
                    {
                        _peek.End(restoreForeground: false);
                    }
                    TriggerQuickTodo();
                }
                break;
            case GlobalHotkeyKind.ToggleDesktopIcons:
                if (!IsTrueFullScreenInFront() && _state.CleanDesktopEnabled)
                {
                    _ = ToggleTemporaryNativeDesktopAsync();
                }
                break;
        }
    }

    internal void TogglePeekFileBoxes()
    {
        if (_isExiting || _exitRequested || _gameModeActive)
        {
            return;
        }
        if (_desktopHidden)
        {
            ToggleDesktop();
        }
        var boxes = _windows.OfType<FileBoxWindow>().ToList();
        if (boxes.Count == 0)
        {
            _trayIcon.ShowBalloonTip(2500, "文件收纳盒", "当前还没有文件收纳盒，可在托盘菜单新建。", TrayTipKind.Info);
            return;
        }
        _edgeDock.CollapseAllExpanded();
        _peek.Toggle(boxes, CardMotionAnimator.IsAnimationEnabled);
    }

    private void ReturnPeekedFileBoxes(IReadOnlyList<FileBoxWindow> boxes)
    {
        if (_desktopSurfaceWatcher.IsDesktopShown)
        {
            RaiseCardsAboveDesktop();
            return;
        }
        WidgetWindowBase.ReturnBelowOrdinaryWindows(boxes);
    }

    private void ReturnSingleFileBoxToDesktop(FileBoxWindow box)
    {
        if (_desktopSurfaceWatcher.IsDesktopShown)
        {
            PlaceCardAboveShellDesktop(box);
            return;
        }
        WidgetWindowBase.ReturnBelowOrdinaryWindows([box]);
    }

    private void TriggerQuickTodo()
    {
        if (_desktopHidden)
        {
            ToggleDesktop();
        }
        var todo = _windows.OfType<TodoWidgetWindow>().FirstOrDefault(window => window.IsVisible)
            ?? _windows.OfType<TodoWidgetWindow>().FirstOrDefault();
        if (todo is null)
        {
            AddTodo();
            todo = _windows.OfType<TodoWidgetWindow>().LastOrDefault();
        }
        else if (!todo.IsVisible)
        {
            todo.Show();
        }
        todo?.QuickAddAndFocus();
    }

    private async Task ToggleTemporaryNativeDesktopAsync()
    {
        if (_isExiting || _exitRequested || !_state.CleanDesktopEnabled)
        {
            return;
        }
        await _cleanDesktopGate.WaitAsync();
        try
        {
            if (!_state.CleanDesktopEnabled || _isExiting)
            {
                return;
            }
            if (!_temporaryNativeDesktopActive)
            {
                if (_peek.IsActive)
                {
                    _peek.End(restoreForeground: false);
                }
                // Only undo what this toggle did: widgets the user hid from the tray stay hidden.
                var hidWidgets = !_desktopHidden;
                if (hidWidgets)
                {
                    ToggleDesktop();
                }
                if (!await _cleanDesktop.DisableAsync())
                {
                    if (hidWidgets && _desktopHidden)
                    {
                        ToggleDesktop();
                    }
                    return;
                }
                _temporaryNativeDesktopActive = true;
                _widgetsHiddenByNativeToggle = hidWidgets;
                _nativeToggleVersion = _desktopToggleVersion;
            }
            else
            {
                if ((await _cleanDesktop.EnableAsync()).Length > 0)
                {
                    // Icons are still visible, so the temporary native state is still true.
                    return;
                }
                _temporaryNativeDesktopActive = false;
                RestoreWidgetsHiddenByNativeToggle();
            }
        }
        finally
        {
            _cleanDesktopGate.Release();
        }
    }

    // Shows the widgets only if this toggle hid them and the user has not toggled them since.
    private void RestoreWidgetsHiddenByNativeToggle()
    {
        if (_widgetsHiddenByNativeToggle && _desktopHidden && _desktopToggleVersion == _nativeToggleVersion)
        {
            ToggleDesktop();
        }
        _widgetsHiddenByNativeToggle = false;
    }

    private bool IsCleanDesktopAvailable() => _cleanDesktopRecovered && !_cleanDesktopRestoreFailed;

    private string SyncDesktopDoubleClickWatcher()
    {
        var shouldRun = !_isExiting
            && !_exitRequested
            && _state.DoubleClickDesktopToToggleIcons == true
            && IsCleanDesktopAvailable();
        if (!shouldRun)
        {
            StopDesktopDoubleClickWatcher();
            return string.Empty;
        }

        if (_doubleClickWatcher is { IsRegistered: true })
        {
            return string.Empty;
        }

        StopDesktopDoubleClickWatcher();
        var watcher = new DesktopDoubleClickWatcher(Dispatcher.CurrentDispatcher);
        watcher.DoubleClickDetected += OnDesktopDoubleClickDetected;
        if (!watcher.Start())
        {
            watcher.DoubleClickDetected -= OnDesktopDoubleClickDetected;
            watcher.Dispose();
            return "无法启用桌面空白处双击监听，请稍后再试";
        }

        _doubleClickWatcher = watcher;
        return string.Empty;
    }

    private void StopDesktopDoubleClickWatcher()
    {
        if (_doubleClickWatcher is null)
        {
            return;
        }

        _doubleClickWatcher.DoubleClickDetected -= OnDesktopDoubleClickDetected;
        _doubleClickWatcher.Dispose();
        _doubleClickWatcher = null;
    }

    private string SetDoubleClickDesktopToToggleIcons(bool enabled)
    {
        if (!enabled)
        {
            _state.DoubleClickDesktopToToggleIcons = false;
            StopDesktopDoubleClickWatcher();
            ScheduleSave();
            return string.Empty;
        }

        if (!IsCleanDesktopAvailable())
        {
            return "隐藏桌面图标当前不可用，请先完成上方恢复";
        }

        _state.DoubleClickDesktopToToggleIcons = true;
        var problem = SyncDesktopDoubleClickWatcher();
        if (problem.Length > 0)
        {
            _state.DoubleClickDesktopToToggleIcons = false;
            ScheduleSave();
            return problem;
        }

        ScheduleSave();
        return string.Empty;
    }

    private async void OnDesktopDoubleClickDetected(int x, int y)
    {
        if (_isExiting
            || _exitRequested
            || _isHandlingDesktopDoubleClick
            || _state.DoubleClickDesktopToToggleIcons != true
            || !IsCleanDesktopAvailable()
            || _cleanDesktopGate.CurrentCount == 0)
        {
            return;
        }

        _isHandlingDesktopDoubleClick = true;
        try
        {
            if (!await _desktopBlankHitTester.IsDesktopBlankAtAsync(x, y))
            {
                return;
            }
            if (_isExiting
                || _exitRequested
                || _state.DoubleClickDesktopToToggleIcons != true
                || !IsCleanDesktopAvailable())
            {
                return;
            }

            if (_state.CleanDesktopEnabled)
            {
                // Showing the native desktop also tucks the widgets away so they do not cover the icons.
                await ToggleTemporaryNativeDesktopAsync();
                return;
            }

            var status = await SetCleanDesktopAsync(true);
            _settingsWindow?.RefreshCleanDesktopSettings(status);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"双击桌面空白处切换桌面图标失败：{exception.Message}");
        }
        finally
        {
            _isHandlingDesktopDoubleClick = false;
        }
    }

    private string CleanDesktopNotice() =>
        _cleanDesktopRestoreFailed
            ? "上次隐藏的桌面图标未能自动恢复。可点“重新恢复”再试。"
            : !_cleanDesktopRecovered
                ? "无法确认隐藏桌面图标前的原状态。可点“重新恢复”显示桌面图标，再开启“隐藏 Windows 桌面图标”。"
                : string.Empty;

    /// <summary>
    /// Whether the desktop icons are hidden while clean desktop is off — the orphaned
    /// leftover of a lost checkpoint, which the settings page offers to restore.
    /// </summary>
    private async Task<bool> CleanDesktopIconsOrphanedAsync() =>
        !_cleanDesktop.IsActive && await _cleanDesktop.AreIconsHiddenNowAsync();

    /// <summary>
    /// Shows the desktop icons again from the settings page: a leftover checkpoint is
    /// honoured first and the icons are shown even without one, so a stuck hidden
    /// desktop is fixed from inside the app instead of the desktop context menu.
    /// </summary>
    private async Task<string> RestoreCleanDesktopIconsAsync()
    {
        await _cleanDesktopGate.WaitAsync();
        try
        {
            if (_isExiting)
            {
                return string.Empty;
            }

            var restored = await _cleanDesktop.RestoreIconsFromSettingsAsync();
            _cleanDesktopRestoreFailed = !restored;
            // The icons are verified visible again, so the state before the mode is
            // known once more; the restore clears an unreadable record's marker.
            _cleanDesktopRecovered = restored
                && CleanDesktopAdapter.IsOriginalStateKnown(CleanDesktopCheckpointPath(_store.DataDirectory));
            _ = SyncDesktopDoubleClickWatcher();
            return restored ? "桌面图标已恢复" : "仍未能恢复，可稍后再试";
        }
        finally
        {
            _cleanDesktopGate.Release();
        }
    }

    /// <summary>
    /// Turns clean desktop on or off and returns a status line. Before hiding the icons,
    /// the row of place icons (this PC, the Recycle Bin, Downloads and others) is shown so
    /// they stay reachable.
    /// </summary>
    private async Task<string> SetCleanDesktopAsync(bool enabled)
    {
        // Requests run in order, so a later turn-off always wins over an earlier turn-on.
        await _cleanDesktopGate.WaitAsync();
        try
        {
            if (_isExiting)
            {
                return string.Empty;
            }

            if (!enabled)
            {
                _temporaryNativeDesktopActive = false;
                RestoreWidgetsHiddenByNativeToggle();
                _state.CleanDesktopEnabled = false;
                SyncGlobalHotkeys();
                ScheduleSave();
                return await _cleanDesktop.DisableAsync()
                    ? "桌面图标已恢复"
                    : "桌面图标未能恢复，可点“重新恢复”再试";
            }

            // This PC, the Recycle Bin and the other places stay one click away in the row
            // of place icons while the desktop icons are hidden.
            var places = _state.DesktopExperience.GetComponent(DesktopComponentKind.QuickLaunch);
            if (!places.IsVisible)
            {
                places.IsVisible = true;
                ApplyQuickPlaces();
                _settingsWindow?.RefreshDesktopExperience();
            }

            var problem = await _cleanDesktop.EnableAsync();
            _temporaryNativeDesktopActive = false;
            RestoreWidgetsHiddenByNativeToggle();
            _state.CleanDesktopEnabled = problem.Length == 0;
            SyncGlobalHotkeys();
            // A successful turn-on either recorded visible icons or found them hidden with
            // a known history, so the original state is known again from here on.
            _cleanDesktopRecovered |= problem.Length == 0;
            _ = SyncDesktopDoubleClickWatcher();
            ScheduleSave();
            return problem.Length == 0
                ? "桌面图标已隐藏，可从快捷入口打开此电脑、回收站、下载和桌面"
                : problem;
        }
        finally
        {
            _cleanDesktopGate.Release();
        }
    }

    // Card spacing, grid alignment and dock motion are read when a card is dragged or an
    // icon hovered, so they take effect at once; the card shadow is redrawn on every open card.
    private void ApplyStyleSettings()
    {
        WidgetWindowBase.CardSpacingDip = _state.Style.CardSpacing;
        WidgetWindowBase.GridSnapEnabled = _state.Style.GridSnap;
        ApplyItemSurface();
        IceBlueDockWindow.Motion = _state.Style.Motion;
        CardMotionAnimator.Motion = _state.Style.Motion;
        // The constructor runs this before the dock exists; the dock reads the settings itself
        // when it first applies, so there is nothing to refresh yet.
        if (_dock is not null && _state.Dock.IsEnabled && _state.Dock.HoverMagnification)
        {
            _dock.Apply();
        }
        foreach (var window in _windows)
        {
            window.ApplyStyle(_state.Style);
        }
    }

    /// <summary>
    /// Gives every card the shared material chosen in the settings. It overwrites each
    /// card's own colours once; a card can still be adjusted on its own afterwards.
    /// </summary>
    private void ApplyGlass()
    {
        var style = _state.Style;
        if (style.Glass == GlassMode.Clear && style.CardShadow == CardShadow.None)
        {
            // Text straight on the wallpaper needs a shadow to stay readable.
            style.CardShadow = CardShadow.Soft;
        }

        ApplyStyleSettings();
        var glass = DesktopStyleRules.Appearance(style);
        ApplyItemSurface();
        foreach (var component in _state.DesktopExperience.Components)
        {
            DesktopStyleRules.ApplySharedComponentLook(
                component,
                _lastAppliedStyle,
                style,
                QuickPlacesWindow.ClearBarOpacity);
        }

        var openWindowAppearances = new HashSet<WidgetAppearanceState>(ReferenceEqualityComparer.Instance);
        var openNotes = new HashSet<NoteWidgetState>(ReferenceEqualityComparer.Instance);
        foreach (var window in _windows.Where(window => window is TodoWidgetWindow or FileBoxWindow or NoteWidgetWindow))
        {
            openWindowAppearances.Add(window.WidgetAppearance);
            if (window is FileBoxWindow openFileBox)
            {
                foreach (var tab in openFileBox.Tabs)
                {
                    openWindowAppearances.Add(tab.Appearance);
                }
            }
            var nextTextColor = DesktopStyleRules.ResolveTransitionTextColor(
                window.WidgetAppearance.TextColor,
                _lastAppliedStyle,
                style);
            window.SetWidgetBackgroundColor(glass.BackgroundColor);
            window.SetWidgetBackgroundOpacity(glass.BackgroundOpacity);
            window.SetWidgetHeaderColor(glass.HeaderColor);
            window.SetWidgetHeaderOpacity(glass.HeaderOpacity);
            window.SetWidgetTextColor(nextTextColor);
            window.ApplyStyle(style);
            if (window is NoteWidgetWindow note)
            {
                openNotes.Add(note.State);
                note.SetBodyColor(DesktopStyleRules.ResolveTransitionTextColor(
                    note.State.BodyTypography.Color,
                    _lastAppliedStyle,
                    style));
            }
        }
        foreach (var todoState in _state.TodoWidgets)
        {
            if (!openWindowAppearances.Contains(todoState.Appearance))
            {
                DesktopStyleRules.ApplySharedCardLook(todoState.Appearance, _lastAppliedStyle, style);
            }
        }
        foreach (var boxState in _state.FileBoxes)
        {
            if (!openWindowAppearances.Contains(boxState.Appearance))
            {
                DesktopStyleRules.ApplySharedCardLook(boxState.Appearance, _lastAppliedStyle, style);
            }
        }
        foreach (var noteState in _state.NoteWidgets)
        {
            if (!openWindowAppearances.Contains(noteState.Appearance))
            {
                DesktopStyleRules.ApplySharedCardLook(noteState.Appearance, _lastAppliedStyle, style);
            }
            if (!openNotes.Contains(noteState))
            {
                noteState.BodyTypography.Color = DesktopStyleRules.ResolveTransitionTextColor(
                    noteState.BodyTypography.Color,
                    _lastAppliedStyle,
                    style);
            }
        }
        ApplyGlassToSecondaryProfiles(_lastAppliedStyle, style);
        ApplyDesktopExperienceWithSave(_state.DesktopExperience, saveImmediate: false);
        _lastAppliedStyle = style.Clone();
    }

    // Rows inside cards (to-dos, the weather and performance panels) and the dock take the
    // shared material; a clear desktop has no row surface and a dock of icons only.
    private void ApplyItemSurface()
    {
        if (AccessibilityThemeManager.IsHighContrastEnabled)
        {
            return;
        }
        var resources = System.Windows.Application.Current.Resources;
        var rendered = DesktopStyleRules.ResolveRenderedCard(_state.Style);
        resources["WidgetItemSurfaceBrush"] = Brush(rendered.ItemSurfaceColor);
        resources["WidgetNoteBodySurfaceBrush"] = Brush(rendered.NoteBodySurfaceColor);
        resources["WidgetSubtleControlBrush"] = Brush(rendered.SubtleControlColor);
        resources["WidgetControlBrush"] = Brush(rendered.ControlColor);
        var dock = DesktopStyleRules.Dock(_state.Style, _state.Dock);
        var dockAdaptive = BarAdaptiveInkRules.ResolveDockAdaptiveColors(dock, sampledBackdrop: null, previousUseDarkInk: null);
        resources["DockSurfaceBrush"] = Brush(dock.Surface);
        resources["DockSurfaceBorderBrush"] = Brush(dock.Border);
        resources["DockDividerBrush"] = Brush(dockAdaptive.Divider);
        resources["DockRunningDotBrush"] = Brush(dock.RunningDot);
        FloatingGlassPalette.Apply(resources, dock);
        _dock?.RefreshAdaptiveAppearance();
        _topBar?.RefreshAppearance();
        _launcherPanel?.ApplyAppearance();
        resources["PerformanceAccentBrush"] = Brush("#F5" + _state.Style.AccentColor.TrimStart('#'));
        // A whisper of the accent colour behind an entry whose deadline is due soon.
        resources["TodoDueSoonBrush"] = Brush("#26" + _state.Style.AccentColor.TrimStart('#'));

        static System.Windows.Media.SolidColorBrush Brush(string value)
        {
            var brush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value)!);
            brush.Freeze();
            return brush;
        }
    }

    private void ApplyStyle()
    {
        ApplyStyleSettings();
        ScheduleSave();
    }

    private void ApplyTaskbar()
    {
        if (Core.Taskbar.TaskbarCornerRules.NeedsModeReapply(
                _taskbar.ActiveMode,
                _state.Taskbar.Mode,
                _taskbar.Problem))
        {
            _ = ApplyTaskbarModeAsync();
        }
        _cornerReveal.SetMode(_state.Taskbar.EdgeRevealMode);
        RefreshInformationCardVisibility();
        UpdateSamplingMode();
        SaveNow();
    }

    // The taskbar adapter is the riskiest module; any failure leaves the system
    // default taskbar in place and never reaches the desktop widgets or the dock.
    // While a mode the user just chose is being applied, a problem shows in settings
    // rather than as a tray notice. The adapter restores the default on any failure.
    private async Task ApplyTaskbarModeAsync()
    {
        _isApplyingTaskbar = true;
        try
        {
            await _taskbar.ApplyAsync(_state.Taskbar.Mode);
        }
        finally
        {
            _isApplyingTaskbar = false;
        }
        if (!_isExiting)
        {
            _cornerReveal.SetMode(_state.Taskbar.EdgeRevealMode);
        }
    }

    private void OnTaskbarStatusChanged()
    {
        _settingsWindow?.RefreshTaskbarSettings();
        if (!_isApplyingTaskbar && _taskbar.Problem.Length > 0
            && _taskbar.ActiveMode == TaskbarMode.SystemDefault)
        {
            _trayIcon.ShowBalloonTip(
                3500,
                "Windows 任务栏",
                _taskbar.Problem,
                TrayTipKind.Warning);
        }
    }

    private void ApplyWidgetLock(WidgetWindowBase window, bool isLocked)
    {
        if (_multiScreen?.TryApplyWidgetLock(window, isLocked) == true)
        {
            SaveNow();
            return;
        }

        window.ApplyWidgetLock(isLocked);
        switch (window)
        {
            case InformationWidgetWindow { ComponentKind: { } kind }:
                _state.DesktopExperience.GetComponent(kind).IsLocked = isLocked;
                break;
            case TodoWidgetWindow todo
                when IsPrimaryTodo(todo.State):
                _state.DesktopExperience
                    .GetComponent(DesktopComponentKind.Todo)
                    .IsLocked = isLocked;
                break;
        }
        SaveNow();
    }

    private bool HasVisibleInformationComponent() =>
        InformationComponentKinds.Any(
            kind => _state.DesktopExperience.GetComponent(kind).IsVisible);

    private void ApplyPresetPlacements(DesktopLayoutPreset preset)
    {
        _state.DesktopExperience.GetComponent(DesktopComponentKind.Greeting).Appearance.TextColor = "#FFFFFF";
        foreach (var (kind, placement) in PresetPlacements(preset))
        {
            _state.DesktopExperience.GetComponent(kind).Placement = placement;
        }
        if (_state.TodoWidgets.FirstOrDefault(item => ScreenProfileAccessor.IsPrimary(item.ScreenKey)) is { } todo)
        {
            todo.Placement = _state.DesktopExperience.GetComponent(DesktopComponentKind.Todo).Placement;
        }
    }

    private IReadOnlyDictionary<DesktopComponentKind, WindowPlacement> PresetPlacements(DesktopLayoutPreset preset)
    {
        var area = SystemParameters.WorkArea;
        return DesktopExperienceRules.PresetPlacements(preset, area.Left, area.Top, area.Right, area.Bottom,
            _state.DesktopExperience.GetComponent(DesktopComponentKind.AudioVisualizer).IsVisible);
    }

    /// <summary>Moves one card back to where the current layout preset puts it.</summary>
    private bool ResetWidgetPlacement(WidgetWindowBase? window, DesktopComponentKind? kind)
    {
        var area = SystemParameters.WorkArea;
        var activePreset = _state.DesktopExperience.ActivePreset;
        var rootMonitor = _rootMonitor;
        var layoutKey = _multiScreen is { } host ? MultiScreenHost.CurrentLayoutKey(host.ConnectedMonitors) : null;
        var presets = rootMonitor is { } monitor
            ? ScreenProfileAccessor.ComputePresetPlacementsForMonitor(activePreset, monitor.WorkingArea, monitor.Dpi, layoutKey,
                    _state.DesktopExperience.GetComponent(DesktopComponentKind.AudioVisualizer).IsVisible)
                .ToDictionary(entry => entry.Key, entry => entry.Value.Placement)
            : PresetPlacements(activePreset);
        if (kind is { } componentKind && presets.TryGetValue(componentKind, out var preset))
        {
            _state.DesktopExperience.GetComponent(componentKind).Placement = preset;
            if (componentKind == DesktopComponentKind.Todo
                && _state.TodoWidgets.FirstOrDefault(item => ScreenProfileAccessor.IsPrimary(item.ScreenKey)) is { } todo)
            {
                todo.Placement = preset;
            }
            ApplyDesktopExperience(_state.DesktopExperience);
            return true;
        }

        var (placement, defaultLeft, defaultTop) = window switch
        {
            NoteWidgetWindow note => (note.State.Placement, 232d, 148d),
            FileBoxWindow box => (box.State.Placement, 432d, 92d),
            TodoWidgetWindow todoWindow => (todoWindow.State.Placement, 92d, 92d),
            _ => (null, 0d, 0d)
        };
        if (window is null || placement is null)
        {
            return false;
        }

        Capture(window);
        if (rootMonitor is { } target)
        {
            var bounds = ScreenProfileAccessor.LocalDipToPhysicalBounds(defaultLeft, defaultTop,
                placement.Width, placement.Height, target.WorkingArea, target.Dpi);
            using (window.SuppressPlacementCapture())
            {
                MultiScreenHost.ApplyPhysicalBounds(window, bounds, target.Dpi, placement, layoutKey, target.WorkingArea);
            }
        }
        else
        {
            placement.Left = area.Left + defaultLeft;
            placement.Top = area.Top + defaultTop;
            window.ApplyPlacement(placement, window.IsWidgetLocked);
        }
        SaveNow();
        return true;
    }


    private void SetWidgetVisibility(WidgetWindowBase window, bool visible)
    {
        if (_isExiting || window.IsClosingOrClosed || !_windows.Contains(window))
        {
            return;
        }

        switch (window)
        {
            case FileBoxWindow box:
                box.State.IsHidden = !visible;
                box.SyncGroupSharedProperties();
                if (visible)
                {
                    _windowsHiddenFromTray.Remove(box);
                    if (IsSecondaryWindow(box))
                    {
                        if (_multiScreen?.TryShowSecondaryWindow(box) != true)
                        {
                            box.State.IsHidden = true;
                            box.SyncGroupSharedProperties();
                            return;
                        }
                    }
                    else
                    {
                        ShowRootWindow(box);
                    }
                    if (box.IsEdgeDocked)
                    {
                        _edgeDock.ApplyAll();
                    }
                }
                else
                {
                    _windowsHiddenFromTray.Remove(box);
                    box.HideFromApp();
                    if (box.IsEdgeDocked)
                    {
                        _edgeDock.ApplyAll();
                    }
                }
                break;
            case NoteWidgetWindow note:
                note.State.IsHidden = !visible;
                _windowsHiddenFromTray.Remove(note);
                if (visible)
                {
                    if (IsSecondaryWindow(note))
                    {
                        if (_multiScreen?.TryShowSecondaryWindow(note) != true)
                        {
                            note.State.IsHidden = true;
                            return;
                        }
                    }
                    else
                    {
                        ShowRootWindow(note);
                    }
                }
                else
                {
                    note.HideFromApp();
                }
                break;
            default:
                return;
        }

        UpdateSamplingMode();
        ScheduleSave();
    }

    private void DeleteWidget(WidgetWindowBase window)
    {
        var message = window switch
        {
            NoteWidgetWindow => "将删除此便签及其中的文字。继续吗？",
            InformationWidgetWindow { ComponentKind: { } kind } =>
                $"将从桌面隐藏“{DesktopExperienceRules.GetComponentName(kind)}”。继续吗？",
            QuickPlacesWindow => "将从桌面隐藏“快捷入口”。继续吗？",
            InformationWidgetWindow => "将删除此桌面信息组件及其本机设置。继续吗？",
            TodoWidgetWindow => "将删除此待办及其中的项目。继续吗？",
            FileBoxWindow box when box.IsGrouped =>
                $"将删除此组合收纳盒中的全部 {box.Tabs.Count} 个标签页及其映射（不删除原文件）。继续吗？",
            FileBoxWindow => "将删除此文件收纳盒及其中的映射。继续吗？",
            _ => "将删除此组件。继续吗？"
        };
        var answer = System.Windows.MessageBox.Show(
            message,
            "删除组件",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        RemoveWidget(window);
    }

    private void RemoveWidget(WidgetWindowBase window)
    {
        if (_multiScreen?.TryRemoveSecondaryWindow(window) == true)
        {
            return;
        }

        switch (window)
        {
            case TodoWidgetWindow todo:
                var wasPrimary = IsPrimaryTodo(todo.State);
                _state.TodoWidgets.RemoveAll(x => x.Id == todo.State.Id);
                var remainingPrimary = ScreenProfileAccessor.GetTodosForScreen(_state, screenKey: null);
                // With the last one gone the to-do card is off, so a later settings change
                // does not bring back an empty one.
                if (remainingPrimary.Count == 0)
                {
                    _state.DesktopExperience.GetComponent(DesktopComponentKind.Todo).IsVisible = false;
                    _settingsWindow?.RefreshDesktopExperience();
                }
                else if (wasPrimary)
                {
                    SyncComponentFromTodo(
                        _state.DesktopExperience.GetComponent(DesktopComponentKind.Todo),
                        remainingPrimary[0]);
                    _settingsWindow?.RefreshDesktopExperience();
                }
                break;
            case NoteWidgetWindow note:
                _state.NoteWidgets.RemoveAll(x => x.Id == note.State.Id);
                break;
            case QuickPlacesWindow:
                _state.DesktopExperience.GetComponent(DesktopComponentKind.QuickLaunch).IsVisible = false;
                _settingsWindow?.RefreshDesktopExperience();
                break;
            case InformationWidgetWindow information:
                if (information.ComponentKind is { } kind)
                {
                    _state.DesktopExperience.GetComponent(kind).IsVisible = false;
                    _settingsWindow?.RefreshDesktopExperience();
                }
                else
                {
                    _state.InformationWidgets.RemoveAll(x => x.Id == information.State.Id);
                    _settingsWindow?.Close();
                }
                break;
            case FileBoxWindow box:
                var tabIds = box.Tabs.Select(tab => tab.Id).ToHashSet();
                _state.FileBoxes.RemoveAll(x => tabIds.Contains(x.Id));
                // Category boxes the user deleted are not created again by organising.
                foreach (var tab in box.Tabs)
                {
                    if (tab.DesktopCategory != DesktopGroupCategory.None
                        && !_state.DismissedDesktopCategories.Contains(tab.DesktopCategory))
                    {
                        _state.DismissedDesktopCategories.Add(tab.DesktopCategory);
                    }
                }
                break;
        }
        _windows.Remove(window);
        window.CanClose = true;
        window.Close();
        UpdateSamplingMode();
        SaveNow();
    }

    private void Backup()
    {
        CaptureAll();
        try
        {
            var path = _store.CreateBackup(_state);
            _trayIcon.ShowBalloonTip(
                2500,
                "备份完成",
                $"已保存到 {path}",
                TrayTipKind.Info);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ReportSaveFailure(exception);
        }
    }

    private static Version AppVersion =>
        typeof(WidgetCoordinator).Assembly.GetName().Version ?? new Version(0, 1, 0);

    private static ThemeArea WorkArea()
    {
        var area = SystemParameters.WorkArea;
        return new ThemeArea(area.Left, area.Top, area.Width, area.Height);
    }

    private string ExportTheme(string path, string themeName)
    {
        CaptureAll();
        var package = ThemeRules.Export(_state, themeName, WorkArea(), AppVersion);
        var preview = ThemePreviewRenderer.Render(package);
        var icons = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (_, entry, iconFile) in ThemeRules.IconSources(_state))
        {
            if (_iconStore.ReadPng(iconFile) is { } bytes)
            {
                icons[entry] = bytes;
            }
        }

        bool previewWritten;
        using (var stream = File.Create(path))
        {
            previewWritten = ThemeArchive.Write(stream, package, preview, icons);
        }
        var iconCount = package.Bindings.Apps.Count(binding => binding.IconEntry is not null);
        var message = $"已导出主题“{package.Manifest.Name}”：{package.Tokens.Components.Count} 个组件，{package.Bindings.Apps.Count} 个 Dock 应用槽位";
        if (iconCount > 0)
        {
            message += $"，{iconCount} 个自定义图标";
        }
        return previewWritten ? message : message + "；预览图未能生成，主题包不含预览图";
    }

    private static ThemeReadResult ReadTheme(string path)
    {
        using var stream = File.OpenRead(path);
        return ThemeArchive.Read(stream, AppVersion);
    }

    private async Task<ThemeImportOutcome> ImportThemeAsync(string path)
    {
        var read = ReadTheme(path);
        if (read.Package is not { } package)
        {
            return new ThemeImportOutcome(false, read.Error, []);
        }

        var resolver = await Task.Run(ThemeAppResolver.Create);
        var bindings = ThemeRules.ResolveBindings(package.Bindings, resolver.Resolve);

        CaptureAll();
        _store.CreateBackup(_state, allowWhenReloadRequired: false);
        ThemeRules.ApplyVisuals(package, _state.DesktopExperience, WorkArea());
        ApplyDesktopExperience(_state.DesktopExperience);
        var taskbarBefore = _state.Taskbar.Mode;
        ThemeRules.ApplyStyle(package, _state.Style, _state.Taskbar);
        ApplyStyleSettings();
        _lastAppliedStyle = _state.Style.Clone();
        if (_state.Taskbar.Mode != taskbarBefore)
        {
            _ = ApplyTaskbarModeAsync();
            _settingsWindow?.RefreshTaskbarSettings();
        }

        _pendingThemeIcons = read.Icons ?? new Dictionary<string, byte[]>();
        // The replaced pins' icons stay on disk: the backup just made still refers to them.
        // Each import adds at most MaximumIcons small files; prune unreferenced ones if
        // repeated imports ever make the folder matter.
        for (var index = 0; index < bindings.Resolved.Count; index++)
        {
            bindings.Resolved[index].IconFile = SaveThemeIcon(bindings.ResolvedIconEntries[index]);
        }
        _state.Dock.IsEnabled = package.Bindings.DockEnabled;
        _state.Dock.VisibilityMode = package.Bindings.DockVisibility;
        // Pinned folders and files are personal and not part of a theme; they stay.
        _state.Dock.PinnedApps = bindings.Resolved
            .Concat(_state.Dock.PinnedApps.Where(DockPinRules.IsPathPin))
            .ToList();
        ThemeRules.ApplyTopBar(package, _state.TopBar);
        _topBar.Apply();
        _dock.Apply();
        RefreshInformationCardVisibility();
        UpdateSamplingMode();
        SaveNow();
        _settingsWindow?.RefreshDockSettings();
        _settingsWindow?.RefreshTopBarSettings();

        var message = $"已应用主题“{package.Manifest.Name}”，原设置已备份";
        if (bindings.Missing.Count == 0)
        {
            return new ThemeImportOutcome(true, message, []);
        }

        message += "；本机未找到："
            + string.Join("、", bindings.Missing.Select(binding =>
                binding.DisplayName.Length > 0 ? binding.DisplayName : AppSlotCatalog.GetName(binding.Slot)))
            + "，可在绑定向导或 Dock 页手动添加";
        var missingSlots = bindings.Missing
            .Select(binding => new ThemeMissingSlot(binding, package.Bindings.Apps.IndexOf(binding)))
            .ToList();
        return new ThemeImportOutcome(true, message, missingSlots);
    }

    private string? SaveThemeIcon(string? entry) =>
        entry is not null && _pendingThemeIcons.TryGetValue(entry, out var bytes)
            ? _iconStore.SaveFromPng(bytes)
            : null;

    /// <summary>Pins the app the binding wizard picked for one missing slot, at that
    /// slot's original position among the theme's dock bindings.</summary>
    private BindOutcome PinBoundApp(ThemeAppBinding binding, string executablePath, int originalIndex)
    {
        if (!string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return new BindOutcome(false, "只能绑定应用程序（.exe）或指向应用程序的快捷方式");
        }

        var app = DockPinRules.CreateFromExecutable(executablePath);
        if (app is null)
        {
            return new BindOutcome(false, "无法识别所选应用");
        }
        app.DisplayName = binding.DisplayName.Length > 0
            ? binding.DisplayName
            : DockAppResolver.GetFileDescription(executablePath) ?? app.DisplayName;

        var index = originalIndex >= 0
            ? Math.Min(originalIndex, _state.Dock.PinnedApps.Count)
            : _state.Dock.PinnedApps.Count;
        if (!DockPinRules.Pin(_state.Dock, app, index))
        {
            return new BindOutcome(false, $"{app.DisplayName} 已在 Dock 中");
        }

        app.IconFile = SaveThemeIcon(binding.IconEntry);
        _dock.Apply();
        SaveNow();
        _settingsWindow?.RefreshDockSettings();
        return new BindOutcome(true, $"已将 {app.DisplayName} 绑定到“{AppSlotCatalog.GetName(binding.Slot)}”槽位");
    }

    // The backup is queued and applied by the next instance before it loads any state.
    private void RestoreBackupAndRestart(string backupPath)
    {
        CaptureAll();
        _store.ScheduleRestore(backupPath, _state);
        var executable = Environment.ProcessPath;
        if (executable is not null)
        {
            var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
            foreach (var argument in _launchArguments.Append(App.WaitForPreviousInstanceSwitch))
            {
                startInfo.ArgumentList.Add(argument);
            }
            Process.Start(startInfo)?.Dispose();
        }
        Exit();
    }

    private void OpenDataDirectory()
    {
        Directory.CreateDirectory(_store.DataDirectory);
        Process.Start(new ProcessStartInfo(_store.DataDirectory) { UseShellExecute = true });
    }

    /// <summary>
    /// Removes mappings whose originals are confirmed missing and refreshes the watched folders.
    /// Runs once at startup and after watcher notices or box changes; the filesystem checks run off the UI thread.
    /// </summary>
    private async Task SweepGoneFileMappings()
    {
        if (_isExiting || !_state.FileBoxAutomationEnabled)
        {
            return;
        }
        if (_isSweepingGoneMappings)
        {
            _sweepGoneAgain = true;
            return;
        }
        _isSweepingGoneMappings = true;
        try
        {
            do
            {
                _sweepGoneAgain = false;
                var items = _state.FileBoxes.SelectMany(box => box.Items).Select(item => (item.Id, item.Path)).ToArray();
                var (gonePaths, parents) = await Task.Run(() => (
                    FileMappingService.CollectGonePaths(items),
                    FileMappingService.CollectExistingParentDirectories(items.Select(item => item.Path))));
                if (_isExiting || !_state.FileBoxAutomationEnabled)
                {
                    return;
                }

                foreach (var box in _windows.OfType<FileBoxWindow>())
                {
                    var clearedHere = box.RemoveGoneMappings(gonePaths);
                    if (clearedHere > 0)
                    {
                        box.SetOperationStatus($"已自动移除 {clearedHere} 项已失效映射");
                    }
                }
                _mappingWatcher.UpdateWatchedDirectories(parents);
            }
            while (_sweepGoneAgain && !_isExiting);
        }
        finally
        {
            _isSweepingGoneMappings = false;
        }
    }

    private static string[] DesktopDirectories() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
    ];

    // 手动整理与自动收纳共用：桌面直接子项里跳过下载中的临时文件，改名完成后才可入盒。
    private static List<string> CollectImportableDesktopEntries() =>
        FileMappingService.EnumerateDirectChildren(DesktopDirectories())
            .Where(path => !FileMappingService.IsPartialDownload(path))
            .ToList();

    /// <summary>
    /// Turns file-box automation on or off and applies it: on starts watching the mapped
    /// folders and the desktops, off tears every watcher down so the feature costs nothing.
    /// </summary>
    private void SetFileBoxAutomation(bool enabled)
    {
        _state.FileBoxAutomationEnabled = enabled;
        ApplyFileBoxAutomation();
        ScheduleSave();
    }

    private void ApplyFileBoxAutomation()
    {
        if (_state.FileBoxAutomationEnabled)
        {
            _mappingWatcher.UpdateImportDirectories(DesktopDirectories().Where(Directory.Exists));
            // The sweep establishes the watched folders and clears anything already gone.
            _mappingWatcher.RequestSweep();
        }
        else
        {
            _mappingWatcher.UpdateImportDirectories([]);
            _mappingWatcher.UpdateWatchedDirectories([]);
        }
    }

    /// <summary>
    /// Imports items that appeared on the desktop while the app was running into the boxes
    /// that already exist (custom collect rules and the category boxes). No new boxes are
    /// created and partial downloads are skipped; they arrive when renamed to the final
    /// name. Runs off the watcher; the scan stays off the UI thread.
    /// </summary>
    private async Task ImportNewDesktopEntries()
    {
        if (_isExiting || !_state.FileBoxAutomationEnabled)
        {
            return;
        }
        if (_isImportingDesktopEntries)
        {
            _importDesktopAgain = true;
            return;
        }
        _isImportingDesktopEntries = true;
        try
        {
            do
            {
                _importDesktopAgain = false;
                var boxes = ScreenProfileAccessor.FilterPrimaryFileBoxes(_state.FileBoxes)
                    .Select(box => new FileBoxState
                    {
                        Id = box.Id,
                        DesktopCategory = box.DesktopCategory,
                        CollectFolders = box.CollectFolders,
                        CollectExtensions = box.CollectExtensions.ToList(),
                        AutoCollectExcludedPaths = box.AutoCollectExcludedPaths.ToList(),
                        Items = box.Items.Select(item => new FileMappingState { Path = item.Path }).ToList()
                    }).ToArray();
                var distribution = await Task.Run(() =>
                    FileMappingService.DistributeAutomatically(CollectImportableDesktopEntries(), boxes));
                if (_isExiting || !_state.FileBoxAutomationEnabled)
                {
                    return;
                }

                foreach (var (boxId, paths) in distribution.ByBox)
                {
                    if (FindPrimaryFileBoxWindow(boxId) is { } boxWindow)
                    {
                        var added = await boxWindow.AddAutomaticMappingsToBoxAsync(boxId, paths,
                            () => _isExiting || !_state.FileBoxAutomationEnabled
                                ? null
                                : ScreenProfileAccessor.FilterPrimaryFileBoxes(_state.FileBoxes));
                        if (added > 0)
                        {
                            boxWindow.SetOperationStatus($"已自动收纳 {added} 项新桌面项目");
                        }
                    }
                }
            }
            while (_importDesktopAgain && !_isExiting);
        }
        finally
        {
            _isImportingDesktopEntries = false;
        }
    }

    private async Task OrganizeDesktopAsync()
    {
        if (_isOrganizingDesktop)
        {
            return;
        }

        _isOrganizingDesktop = true;
        try
        {
            CaptureAll();
            var boxes = ScreenProfileAccessor.FilterPrimaryFileBoxes(_state.FileBoxes);
            var items = boxes.SelectMany(box => box.Items).Select(item => (item.Id, item.Path)).ToArray();
            var gonePaths = await Task.Run(() => FileMappingService.CollectGonePaths(items));
            if (_isExiting)
            {
                return;
            }
            var cleared = 0;
            if (gonePaths.Count > 0)
            {
                foreach (var box in _windows.OfType<FileBoxWindow>().Where(box => !IsSecondaryWindow(box)))
                {
                    cleared += box.RemoveGoneMappings(gonePaths);
                }
            }

            var distribution = await Task.Run(() =>
                FileMappingService.Distribute(CollectImportableDesktopEntries(), boxes));

            var added = 0;
            var created = 0;
            var left = 0;
            foreach (var (boxId, paths) in distribution.ByBox)
            {
                if (FindPrimaryFileBoxWindow(boxId) is { } boxWindow)
                {
                    added += await boxWindow.AddMappingsToBoxAsync(boxId, paths);
                }
            }

            // Items no box collects go to the original category boxes, created as needed.
            foreach (var category in new[]
                     {
                         DesktopGroupCategory.Applications,
                         DesktopGroupCategory.Folders,
                         DesktopGroupCategory.Files
                     })
            {
                if (!distribution.Unclaimed.TryGetValue(category, out var paths) || paths.Count == 0)
                {
                    continue;
                }
                if (_state.DismissedDesktopCategories.Contains(category))
                {
                    left += paths.Count;
                    continue;
                }

                FileBoxWindow window;
                var state = _state.FileBoxes.FirstOrDefault(
                    box => ScreenProfileAccessor.IsPrimary(box.ScreenKey) &&
                           box.DesktopCategory == DesktopGroupCategory.None &&
                           box.Items.Count == 0 &&
                           box.CollectExtensions.Count == 0 &&
                           !box.CollectFolders &&
                           WidgetTitleRules.IsDefaultFileBoxTitle(box.Title));
                if (state is null)
                {
                    state = CreateCategorizedFileBox(category);
                    _state.FileBoxes.Add(state);
                    window = ShowFileBox(state);
                    created++;
                }
                else
                {
                    window = FindPrimaryFileBoxWindow(state.Id)!;
                    if (window.State.Id == state.Id)
                    {
                        window.AssignDesktopCategory(category, GetCategoryTitle(category));
                    }
                    else
                    {
                        state.DesktopCategory = category;
                        state.Title = GetCategoryTitle(category);
                        window.RefreshTabStripItems();
                    }
                }

                added += await window.AddMappingsToBoxAsync(state.Id, paths);
            }

            MoveSystemPlacesToFolderBox();
            SaveNow();
            var status = $"整理完成：新增 {added} 项，创建 {created} 个分类盒";
            if (cleared > 0)
            {
                status += $"；清除 {cleared} 项已失效映射";
            }
            if (left > 0)
            {
                // Deleted category boxes stay deleted; a box with a collect rule takes them.
                status += $"；{left} 项的分类盒已删除，留在桌面（可给文件收纳盒设置收集规则）";
            }
            foreach (var box in _windows.OfType<FileBoxWindow>().Where(box => !IsSecondaryWindow(box)))
            {
                box.SetOperationStatus(status);
            }
            // Hiding the original icons is a separate setting, so the result says where it is
            // while that setting is off.
            var hint = _state.CleanDesktopEnabled
                ? string.Empty
                : "桌面上的原图标仍会显示；在设置的“桌面方案”页开启“隐藏 Windows 桌面图标”后可只显示文件收纳盒。";
            _trayIcon.ShowBalloonTip(
                6000,
                "桌面归类完成",
                $"新增 {added} 项，创建 {created} 个分类盒"
                + (cleared > 0 ? $"，清除 {cleared} 项已失效映射" : string.Empty)
                + $"。{hint}",
                TrayTipKind.Info);
        }
        catch (Exception ex)
        {
            foreach (var box in _windows.OfType<FileBoxWindow>().Where(box => !IsSecondaryWindow(box)))
            {
                box.SetOperationStatus("整理未完成");
            }
            _trayIcon.ShowBalloonTip(
                3500,
                "桌面归类未完成",
                ex.Message,
                TrayTipKind.Warning);
        }
        finally
        {
            _isOrganizingDesktop = false;
        }
    }

    // This PC, the Recycle Bin, Downloads and the desktop folder are places, so they live in
    // the folder box: the desktop-folder category box, or a box that collects folders.
    private (FileBoxWindow Window, FileBoxState Tab)? FolderBoxTarget()
    {
        foreach (var box in _windows.OfType<FileBoxWindow>().Where(box => !IsSecondaryWindow(box)))
        {
            if (box.Tabs.FirstOrDefault(tab => tab.DesktopCategory == DesktopGroupCategory.Folders) is { } catTab)
            {
                return (box, catTab);
            }
        }
        foreach (var box in _windows.OfType<FileBoxWindow>().Where(box => !IsSecondaryWindow(box)))
        {
            if (box.Tabs.FirstOrDefault(tab => tab.CollectFolders) is { } collectTab)
            {
                return (box, collectTab);
            }
        }
        return null;
    }

    private void MoveSystemPlacesToFolderBox()
    {
        if (FolderBoxTarget() is not { } target)
        {
            return;
        }
        foreach (var box in _windows.OfType<FileBoxWindow>().Where(box => !IsSecondaryWindow(box)).ToList())
        {
            box.MoveSystemPlacesToTab(target.Window, target.Tab);
        }
    }

    private FileBoxState CreateCategorizedFileBox(DesktopGroupCategory category)
    {
        var index = category switch
        {
            DesktopGroupCategory.Applications => 0,
            DesktopGroupCategory.Folders => 1,
            DesktopGroupCategory.Files => 2,
            _ => 0
        };
        var state = new FileBoxState
        {
            Title = GetCategoryTitle(category),
            DesktopCategory = category,
            Placement = new WindowPlacement
            {
                Left = 432 + (index * 390),
                Top = 92,
                Width = 366,
                Height = 306
            }
        };
        UseSharedLook(state.Appearance);
        return state;
    }

    private static string GetCategoryTitle(DesktopGroupCategory category) => category switch
    {
        DesktopGroupCategory.Applications => "软件与快捷入口",
        DesktopGroupCategory.Folders => "桌面文件夹",
        DesktopGroupCategory.Files => "桌面文件",
        _ => WidgetTitleRules.DefaultFileBoxTitle
    };

    private void ScheduleSave()
    {
        if (_isExiting)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async void SaveDeferred()
    {
        _saveTimer.Stop();
        CaptureAll();
        Task<Exception?> save;
        try
        {
            save = _store.SaveDeferred(_state);
        }
        catch (InvalidDataException exception)
        {
            _deferredSave = null;
            ReportSaveFailure(exception);
            return;
        }
        if (ReferenceEquals(_deferredSave, save))
        {
            return;
        }
        _deferredSave = save;
        var error = await save;
        if (!ReferenceEquals(_deferredSave, save))
        {
            return;
        }
        _deferredSave = null;
        if (error is null)
        {
            _saveFailureShown = false;
        }
        else
        {
            ReportSaveFailure(error);
        }
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        _deferredSave = null;
        CaptureAll();
        try
        {
            _store.Save(_state);
            _saveFailureShown = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ReportSaveFailure(exception);
        }
    }

    private void ReportSaveFailure(Exception exception)
    {
        StartupLog.Write($"保存失败：{exception.Message}");
        if (_saveFailureShown)
        {
            return;
        }
        _saveFailureShown = true;
        var message = _store.RequiresReloadBeforeSaving
            ? "原设置暂时无法读取。可用“立即备份”单独保存本次修改。文件可访问后可重启。"
            : "设置暂时无法保存，稍后修改时会再次尝试。";
        _trayIcon.ShowBalloonTip(5000, "冰蓝桌面", message, TrayTipKind.Warning);
    }

    private void CaptureAll()
    {
        foreach (var window in _windows)
        {
            Capture(window);
        }
    }

    private bool TryOpenTrayForApplication(nint monitorHandle)
    {
        if (_exitRequested || _isExiting)
        {
            return false;
        }

        try
        {
            return _topBar.TryOpenTrayForApplication(monitorHandle);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Debug.WriteLine($"冰蓝托盘入口自动唤起失败：{exception}");
            return false;
        }
    }

    internal Task<string> CloseTrayOverflowAsync()
    {
        if (_closingTrayOverflow is { } pending) return pending;
        if (_trayOverflow is not { } controller) return Task.FromResult("closed");
        return _closingTrayOverflow = CloseControllerAsync(controller);
    }

    private async Task<string> CloseControllerAsync(TrayOverflowController controller)
    {
        // Install the shared task before cleanup can finish synchronously.
        await Task.Yield();
        try
        {
            var result = await controller.CloseAsync();
            if (result == "closed")
            {
                controller.Dispose();
                if (ReferenceEquals(_trayOverflow, controller)) _trayOverflow = null;
            }
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Debug.WriteLine($"系统托盘清理失败：{exception}");
            return "failed";
        }
        finally
        {
            _closingTrayOverflow = null;
        }
    }

    /// <summary>A newer start asked this instance to make way; same path as the tray's Exit.</summary>
    internal void RequestExit() => Exit();

    private async void Exit() => await ExitAsync();

    private async Task ExitAsync()
    {
        if (_isExiting || _exitRequested)
        {
            return;
        }
        _exitRequested = true;
        _sessionEndingPending = false;
        _cleanDesktop.CancelKeepHiddenForReboot();
        var trayClosed = await CloseTrayOverflowAsync();
        if (trayClosed != "closed")
        {
            StartupLog.Write($"退出时托盘面板状态：{trayClosed}");
        }
        _isExiting = true;
        _saveTimer.Stop();
        var canCaptureSecondaryPlacement = !WindowScreenRecovery.IsLayoutTransitioning()
            && MonitorIdentityResolver.HasMultipleMonitors();
        DisposeMultiScreenHost(capturePlacement: canCaptureSecondaryPlacement);
        SaveNow();
        _settingsWindow?.Close();
        _settingsWindow = null;
        _launcherPanel?.Close();
        _peek.Dispose();
        StopDesktopDoubleClickWatcher();
        _hotkeys.Dispose();
        _mappingWatcher.Dispose();
        _dock.Dispose();
        _taskbarMinimizeFocusGuard.Dispose();
        _cornerReveal.Dispose();
        _taskbar.Dispose();
        _cleanDesktop.Dispose();
        _updater.Dispose();
        _topBar.Dispose();
        foreach (var window in _windows.ToList())
        {
            window.CanClose = true;
            window.Close();
        }
        _windows.Clear();
        _trayIcon.Visible = false;
        System.Windows.Application.Current.Shutdown();
    }

    private TrayIconHost BuildTrayIcon(bool interactiveQa)
    {
        _trayBaseText = interactiveQa ? "冰蓝桌面（测试预览）" : "冰蓝桌面";
        var trayIcon = new TrayIconHost(_trayBaseText);
        _launcherMenuItem = trayIcon.AddItem("打开冰蓝总览卡", ToggleLauncherPanel);
        _launcherMenuItem.Visible = _state.LauncherEnabled && !_gameModeActive;
        _peekMenuItem = trayIcon.AddItem("临时浮起文件收纳盒", TogglePeekFileBoxes);
        _peekMenuItem.Visible = !_gameModeActive;
        trayIcon.AddItem("一键整理桌面", async () => await OrganizeDesktopAsync());
        trayIcon.AddSeparator();
        _toggleDesktopMenuItem = trayIcon.AddItem("隐藏桌面组件", ToggleDesktop);
        _gameModeMenuItem = trayIcon.AddItem("免打扰（游戏模式）", ToggleGameMode);
        trayIcon.AddSeparator();
        trayIcon.AddItem("显示桌面信息", AddInformation);
        trayIcon.AddItem("新建待办便签", AddTodo);
        trayIcon.AddItem("新建普通便签", AddNote);
        trayIcon.AddItem("新建文件收纳盒", () => AddFileBox());
        trayIcon.AddItem("框选新建文件收纳盒…", StartMarqueeFileBox);
        trayIcon.AddSeparator();
        _updateMenuItem = trayIcon.AddItem(string.Empty, () => OpenSettings("About"));
        _updateMenuItem.Visible = false;
        trayIcon.AddItem("冰蓝桌面设置…", () => OpenSettings());
        trayIcon.AddItem("立即备份", Backup);
        trayIcon.AddItem("打开数据目录", OpenDataDirectory);
        trayIcon.AddSeparator();
        trayIcon.AddItem("退出", Exit);

        trayIcon.DoubleClick += (_, _) => OpenSettings();
        trayIcon.SessionEndingResolved += OnSessionEndingResolved;
        // Only the update notice acts on a click; other notices are plain information.
        trayIcon.BalloonTipClicked += (_, _) => _balloonClicked?.Invoke();
        trayIcon.BalloonTipClosed += (_, _) => _balloonClicked = null;
        return trayIcon;
    }

    // A rough bound over all monitors; Start then moves any card that lands between or off
    // monitors onto the nearest work area.
    private static void SanitizePlacement(WindowPlacement placement) =>
        SanitizePlacement(placement, MonitorCatalog.GetAll());

    internal static void SanitizePlacement(
        WindowPlacement placement,
        IReadOnlyList<MonitorSnapshot> monitors)
    {
        if (monitors.Count == 0)
        {
            var area = new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);
            if (!double.IsFinite(placement.Width)) placement.Width = 320d;
            if (!double.IsFinite(placement.Height)) placement.Height = 240d;
            if (!double.IsFinite(placement.Left)) placement.Left = area.Left;
            if (!double.IsFinite(placement.Top)) placement.Top = area.Top;
            placement.Width = Math.Clamp(placement.Width, 226, Math.Max(226, area.Width));
            placement.Height = Math.Clamp(placement.Height, 72, Math.Max(72, area.Height));
            placement.Left = Math.Clamp(placement.Left, area.Left - placement.Width + 80, area.Right - 80);
            placement.Top = Math.Clamp(placement.Top, area.Top, area.Bottom - 60);
            return;
        }

        var primary = monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors[0];
        var primaryScale = primary.Dpi > 0 ? primary.Dpi / 96d : 1d;
        var minLeft = double.PositiveInfinity;
        var minTop = double.PositiveInfinity;
        var maxRight = double.NegativeInfinity;
        var maxBottom = double.NegativeInfinity;
        var maxMonitorWidth = 226d;
        var maxMonitorHeight = 72d;

        foreach (var monitor in monitors)
        {
            var scale = monitor.Dpi > 0 ? monitor.Dpi / 96d : primaryScale;
            var work = monitor.WorkingArea.HasArea ? monitor.WorkingArea : monitor.Bounds;
            var bounds = monitor.Bounds.HasArea ? monitor.Bounds : work;
            // Encompass both the monitor's own DPI coordinate space and the primary-DPI
            // projection so mixed-DPI secondary monitors and negative offsets stay uncropped.
            minLeft = Math.Min(minLeft, Math.Min(bounds.Left / scale, bounds.Left / primaryScale));
            minTop = Math.Min(minTop, Math.Min(bounds.Top / scale, bounds.Top / primaryScale));
            maxRight = Math.Max(maxRight, Math.Max(bounds.Right / scale, bounds.Right / primaryScale));
            maxBottom = Math.Max(maxBottom, Math.Max(bounds.Bottom / scale, bounds.Bottom / primaryScale));
            maxMonitorWidth = Math.Max(maxMonitorWidth, work.Width / scale);
            maxMonitorHeight = Math.Max(maxMonitorHeight, work.Height / scale);
        }

        if (!double.IsFinite(placement.Width)) placement.Width = 320d;
        if (!double.IsFinite(placement.Height)) placement.Height = 240d;
        if (!double.IsFinite(placement.Left)) placement.Left = primary.WorkingArea.Left / primaryScale;
        if (!double.IsFinite(placement.Top)) placement.Top = primary.WorkingArea.Top / primaryScale;

        var totalWidth = Math.Max(maxMonitorWidth, maxRight - minLeft);
        var totalHeight = Math.Max(maxMonitorHeight, maxBottom - minTop);
        placement.Width = Math.Clamp(placement.Width, 226d, Math.Max(226d, totalWidth));
        placement.Height = Math.Clamp(placement.Height, 72d, Math.Max(72d, totalHeight));
        placement.Left = Math.Clamp(placement.Left, minLeft - placement.Width + 80d, maxRight - 80d);
        placement.Top = Math.Clamp(placement.Top, minTop, maxBottom - 60d);
    }

    /// <summary>
    /// The top bar's view of the app: shared facts and actions only, so the bar itself
    /// stays a dumb surface and tests can fake every input.
    /// </summary>
    private sealed class TopBarEnvironment(WidgetCoordinator owner) : ITopBarEnvironment
    {
        private volatile bool _weatherInFlight;
        private string? _batteryHubPath;
        private PeripheralBatteryService? _peripheralBattery;
        private Task? _peripheralRefresh;
        private CancellationTokenSource? _peripheralCancellation;

        public bool Use24HourClock => owner.InformationState().Use24HourClock;

        public bool ShowDate => owner.InformationState().ShowDate;

        public bool ShowLunarDate => owner.InformationState().ShowLunarDate;

        public WeatherSnapshot? ReadWeather()
        {
            var information = owner.InformationState();
            return information.HasWeatherLocation
                ? owner._weatherService.GetLastSnapshot(
                    information.WeatherCity,
                    information.WeatherLatitude!.Value,
                    information.WeatherLongitude!.Value)
                : null;
        }

        public void RefreshWeatherIfDue()
        {
            var information = owner.InformationState();
            if (!information.HasWeatherLocation || _weatherInFlight)
            {
                return;
            }

            var city = information.WeatherCity;
            var latitude = information.WeatherLatitude!.Value;
            var longitude = information.WeatherLongitude!.Value;
            var now = DateTimeOffset.Now;
            if (!owner._weatherService.IsAutomaticRefreshDue(
                    now,
                    city,
                    latitude,
                    longitude))
            {
                return;
            }

            _weatherInFlight = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    await owner._weatherService.RefreshAsync(city, latitude, longitude);
                }
                finally
                {
                    _weatherInFlight = false;
                }
            });
        }

        public PeripheralBatterySnapshot ReadPeripheralBattery()
        {
            UpdatePeripheralBatterySource();
            return _peripheralBattery?.GetSnapshot() ?? PeripheralBatterySnapshot.NotConfigured;
        }

        public void RefreshPeripheralBatteryIfDue() => RefreshPeripheralBattery(force: false);

        public void ForceRefreshPeripheralBattery() => RefreshPeripheralBattery(force: true);

        public void UpdatePeripheralBatterySource()
        {
            var settings = owner._state.TopBar;
            var path = !owner._isExiting && !owner._sessionEndedHandled && !owner._gameModeActive
                && owner._topBar.HasWindow && TopBarRules.IsModuleOn(settings.Modules, TopBarModuleKind.PeripheralBattery)
                ? BatteryHubCli.GetConfiguredPath(settings.BatteryHubExecutablePath)
                : null;
            if (string.Equals(path, _batteryHubPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            StopPeripheralBattery();
            if (path is not null)
            {
                _batteryHubPath = path;
                _peripheralBattery = new PeripheralBatteryService(token => BatteryHubCli.ReadAsync(path, token));
            }
        }

        public void StopPeripheralBattery()
        {
            _batteryHubPath = null;
            _peripheralBattery = null;
            _peripheralCancellation?.Cancel();
        }

        private void RefreshPeripheralBattery(bool force)
        {
            UpdatePeripheralBatterySource();
            if (_peripheralBattery is not { } service || _peripheralRefresh is { IsCompleted: false }
                || (!force && !service.IsAutomaticRefreshDue()))
            {
                return;
            }

            var cancellation = new CancellationTokenSource();
            _peripheralCancellation = cancellation;
            _peripheralRefresh = RunPeripheralRefreshAsync(service, force, cancellation);
        }

        private async Task RunPeripheralRefreshAsync(
            PeripheralBatteryService service, bool force, CancellationTokenSource cancellation)
        {
            using (cancellation)
            {
                try
                {
                    await service.RefreshAsync(force, cancellation.Token);
                    if (!cancellation.IsCancellationRequested)
                    {
                        owner._topBar.RefreshPeripheralBattery();
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    // The selected source changed or the top bar shut down.
                }
                finally
                {
                    _peripheralCancellation = null;
                }
            }
        }

        public int CountIncompleteTodos() =>
            TopBarModuleRules.CountIncompleteTodosToday(owner._state.TodoWidgets);

        public TopBarTodoList? ReadTodoList() =>
            owner._state.TodoWidgets.FirstOrDefault() is { } todo
                ? new TopBarTodoList(
                    todo.Title,
                    todo.Items
                        .Where(item => !string.IsNullOrWhiteSpace(item.Text))
                        .Where(item => TodoService.BelongsToTodayList(item, DateTime.Today, todo.CarryOverIncomplete))
                        .ToList(),
                    todo.CarryOverIncomplete)
                : null;

        public void TodoItemToggled()
        {
            foreach (var todo in owner._windows.OfType<TodoWidgetWindow>())
            {
                todo.RefreshCollapsedSummary();
            }
            owner.ScheduleSave();
        }

        public bool IsLauncherEnabled => owner._state.LauncherEnabled;

        public bool IsEverythingSearchEnabled => owner._state.LauncherPreferences.UseEverything;

        public async Task<LauncherPanelSnapshot?> ReadSearchSnapshotAsync() =>
            await owner.ReadLauncherSnapshotAsync();

        public void OpenSearchEntry(LauncherPanelEntry entry) =>
            owner.OpenLauncherTarget(entry, locate: false);

        public void ToggleLauncherPanel() => owner.ToggleLauncherPanel();

        public void OpenTopBarSettings() => owner.OpenSettings("TopBar");

        public void OpenTopBarSettings(TopBarModuleKind? focusModule) => owner.OpenTopBarSettings(focusModule);

        public void TopBarStateChanged()
        {
            owner.ApplyTopBar();
            owner._settingsWindow?.RefreshTopBarSettings();
            owner.ScheduleSave();
        }

        public async void OpenTrayOverflow()
        {
            if (owner._exitRequested || owner._isExiting || owner._closingTrayOverflow is not null) return;
            try
            {
                if (owner._trayOverflow is { IsStopping: true }
                    && await owner.CloseTrayOverflowAsync() != "closed")
                {
                    ReportShellFailure();
                    return;
                }
                if (owner._exitRequested || owner._isExiting) return;
                var controller = owner._trayOverflow ??= new TrayOverflowController();
                // A second click closes the panel this button opened, like the native chevron.
                if (controller.OwnsPanel && TrayOverflowController.IsVisible)
                {
                    if (!await controller.CloseOwnedPanelAsync() && TrayOverflowController.IsVisible
                        && !owner._exitRequested && !owner._isExiting) ReportShellFailure();
                    return;
                }
                var result = await controller.OpenAsync();
                if (owner._exitRequested || owner._isExiting) return;
                if (result == "elevated-foreground") ReportElevatedForeground();
                else if (result is not ("opened" or "already-open" or "busy")) ReportShellFailure();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Debug.WriteLine($"系统托盘入口不可用：{exception}");
                if (!owner._isExiting) ReportShellFailure();
            }
        }

        public async void CloseTrayOverflow()
        {
            if (owner._isExiting) return;
            var result = await owner.CloseTrayOverflowAsync();
            if (result != "closed" && !owner._isExiting && !owner._exitRequested)
                owner._trayIcon.ShowBalloonTip(4000, "系统托盘尚未关闭",
                    "可点击其他窗口收起面板，或稍后重试退出冰蓝桌面。", TrayTipKind.Info);
        }

        public async void OpenQuickSettings(TopBarModuleKind kind)
        {
            var uri = kind switch
            {
                TopBarModuleKind.Network => "ms-settings:network-status",
                TopBarModuleKind.Battery => "ms-settings:batterysaver",
                _ => "ms-settings:sound"
            };
            if (!await ShellSurfaceService.OpenQuickSettingsAsync(uri))
            {
                ReportShellFailure();
            }
        }

        public void OpenSystemSettings(string settingsUri)
        {
            if (!ShellSurfaceService.OpenSystemSettings(settingsUri))
            {
                ReportShellFailure();
            }
        }

        public async void OpenShutdownDialog()
        {
            if (!await ShellSurfaceService.OpenShutdownDialogAsync() && !owner._isExiting && !owner._exitRequested)
            {
                ReportShellFailure();
            }
        }

        private readonly SemaphoreSlim _audioWriteGate = new(1, 1);
        private readonly SemaphoreSlim _powerWriteGate = new(1, 1);
        private readonly SemaphoreSlim _brightnessWriteGate = new(1, 1);
        private long _audioWriteTicket;
        private long _brightnessWriteTicket;

        private async Task<bool> RunSerializedAsync(SemaphoreSlim gate, Func<bool> work)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(work).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        public Task<TopBarAudioSnapshot> ReadAudioSnapshotAsync() =>
            Task.Run(TopBarSystemInfo.ReadAudioSnapshot);

        public async Task<bool> SetMasterVolumeAsync(int percent)
        {
            var ticket = Interlocked.Increment(ref _audioWriteTicket);
            await _audioWriteGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Skip superseded slider drags so rapid motion never leaves Windows at an older value.
                if (ticket != Interlocked.Read(ref _audioWriteTicket))
                {
                    return true;
                }
                return await Task.Run(() => TopBarSystemInfo.SetMasterVolume(percent)).ConfigureAwait(false);
            }
            finally
            {
                _audioWriteGate.Release();
            }
        }

        public async Task<bool> SetMasterMuteAsync(bool muted)
        {
            // Advance the ticket so any queued earlier volume drag is skipped and cannot unmute after this mute.
            Interlocked.Increment(ref _audioWriteTicket);
            return await RunSerializedAsync(_audioWriteGate, () => TopBarSystemInfo.SetMasterMute(muted)).ConfigureAwait(false);
        }

        public async Task<bool> SetDefaultOutputDeviceAsync(string deviceId)
        {
            Interlocked.Increment(ref _audioWriteTicket);
            return await RunSerializedAsync(_audioWriteGate, () => TopBarSystemInfo.SetDefaultOutputDevice(deviceId)).ConfigureAwait(false);
        }

        public Task<bool> SetAudioSessionVolumeAsync(string? deviceId, uint processId, int percent) =>
            RunSerializedAsync(_audioWriteGate, () => TopBarSystemInfo.SetAudioSessionVolume(deviceId, processId, percent));

        public Task<bool> SetAudioSessionMuteAsync(string? deviceId, uint processId, bool muted) =>
            RunSerializedAsync(_audioWriteGate, () => TopBarSystemInfo.SetAudioSessionMute(deviceId, processId, muted));

        public Task<TopBarBatterySnapshot> ReadBatterySnapshotAsync() =>
            Task.Run(TopBarSystemInfo.ReadBatterySnapshot);

        public Task<bool> SetPowerModeAsync(TopBarPowerMode mode) =>
            RunSerializedAsync(_powerWriteGate, () => TopBarSystemInfo.SetPowerMode(mode));

        public async Task<bool> SetBuiltInBrightnessAsync(int percent)
        {
            var ticket = Interlocked.Increment(ref _brightnessWriteTicket);
            await _brightnessWriteGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ticket != Interlocked.Read(ref _brightnessWriteTicket))
                {
                    return true;
                }
                return await Task.Run(() => TopBarSystemInfo.SetBuiltInBrightness(percent)).ConfigureAwait(false);
            }
            finally
            {
                _brightnessWriteGate.Release();
            }
        }

        public Task<TopBarNetworkSnapshot> ReadNetworkSnapshotAsync() =>
            Task.Run(TopBarSystemInfo.ReadNetworkSnapshot);

        public Task<bool> ConnectSavedWifiAsync(Guid interfaceGuid, string profileName) =>
            Task.Run(() => TopBarSystemInfo.ConnectSavedWifi(interfaceGuid, profileName));

        public Task<bool> DisconnectWifiAsync(Guid interfaceGuid) =>
            Task.Run(() => TopBarSystemInfo.DisconnectWifi(interfaceGuid));

        public Task<bool> TriggerWifiScanAsync(Guid interfaceGuid) =>
            Task.Run(() => TopBarSystemInfo.TriggerWifiScan(interfaceGuid));

        public void OpenInputMethod()
        {
            if (!ShellSurfaceService.OpenInputMethodSettings())
            {
                ReportShellFailure();
            }
        }

        public IReadOnlyList<TopBarInputMethodOption> ReadInputMethodOptions() =>
            TopBarSystemInfo.ReadInputMethodOptions();

        public bool ActivateInputMethod(TopBarInputMethodOption option, nint foregroundWindow) =>
            TopBarSystemInfo.ActivateInputMethod(option, foregroundWindow);

        private void ReportShellFailure() => owner._trayIcon.ShowBalloonTip(
            4000, "系统入口暂不可用", "可将鼠标移到屏幕底边或角落唤出 Windows 任务栏。", TrayTipKind.Info);

        private void ReportElevatedForeground() => owner._trayIcon.ShowBalloonTip(
            4000, "当前前台是管理员程序", "Windows 不允许普通程序向它发送快捷键，请先切换到其他窗口再试。", TrayTipKind.Info);

        public void OpenTaskManagerPerformance()
        {
            int? launchedPid = null;
            try
            {
                using var started = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr.exe")
                {
                    UseShellExecute = true
                });
                launchedPid = started?.Id;
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                or InvalidOperationException)
            {
                return;
            }

            // Task Manager rebuilds its window while starting and drops in-flight UIA
            // reads, so each attempt re-reads and the loop gives up quietly.
            _ = Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    await Task.Delay(300);
                    try
                    {
                        var manager = FindTaskManagerWindow(launchedPid);
                        var page = FindTaskManagerPerformanceTab(manager);
                        if (page is null)
                        {
                            continue;
                        }
                        // The side navigation is a list: selection first, invoke second.
                        if (page.GetCurrentPattern(
                                System.Windows.Automation.SelectionItemPattern.Pattern)
                            is System.Windows.Automation.SelectionItemPattern selection)
                        {
                            selection.Select();
                            return;
                        }
                        if (page.GetCurrentPattern(
                                System.Windows.Automation.InvokePattern.Pattern)
                            is System.Windows.Automation.InvokePattern invoke)
                        {
                            invoke.Invoke();
                            return;
                        }
                    }
                    catch (Exception exception)
                        when (exception is System.Windows.Automation.ElementNotAvailableException
                            or System.Windows.Automation.ElementNotEnabledException
                            or InvalidOperationException
                            or System.Runtime.InteropServices.COMException)
                    {
                        // Window rebuilt mid-read or elevated UIA denied; try again quietly.
                    }
                }
            });
        }

        private static System.Windows.Automation.AutomationElement? FindTaskManagerWindow(int? launchedPid)
        {
            var root = System.Windows.Automation.AutomationElement.RootElement;
            if (launchedPid is { } pid)
            {
                var byPid = root.FindFirst(
                    System.Windows.Automation.TreeScope.Children,
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.ProcessIdProperty,
                        pid));
                if (byPid is not null)
                {
                    return byPid;
                }
            }
            var byClass = root.FindFirst(
                System.Windows.Automation.TreeScope.Children,
                new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ClassNameProperty,
                    "TaskManagerWindow"));
            if (byClass is not null)
            {
                return byClass;
            }
            return root.FindFirst(
                System.Windows.Automation.TreeScope.Children,
                new System.Windows.Automation.OrCondition(
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.NameProperty,
                        "任务管理器"),
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.NameProperty,
                        "Task Manager")));
        }

        private static System.Windows.Automation.AutomationElement? FindTaskManagerPerformanceTab(
            System.Windows.Automation.AutomationElement? manager)
        {
            if (manager is null)
            {
                return null;
            }
            // Prefer locale-neutral AutomationIds on Windows 11 Task Manager, falling back to
            // localized labels when older builds expose only the visible name.
            return manager.FindFirst(
                System.Windows.Automation.TreeScope.Descendants,
                new System.Windows.Automation.OrCondition(
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.AutomationIdProperty,
                        "Performance"),
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.AutomationIdProperty,
                        "PerformancePage"),
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.AutomationIdProperty,
                        "NavigationViewItem_Performance"),
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.NameProperty,
                        "性能"),
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.NameProperty,
                        "Performance")));
        }

        public void OpenComponentSettings(DesktopComponentKind kind) => owner.OpenSettings(kind);

        public bool ActivateWindow(nint handle, uint processId, nint monitorHandle)
        {
            if (handle == 0 && processId == 0)
            {
                return false;
            }

            if (processId == 0 && handle != 0 && DockNativeMethods.IsWindow(handle))
            {
                DockNativeMethods.GetWindowThreadProcessId(handle, out processId);
            }

            // A visible top-level window can be raised directly (with async restore retries);
            // hidden message windows or non-top-level ghosts delegate to the owning instance.
            if (handle != 0
                && DockNativeMethods.IsWindow(handle)
                && DockNativeMethods.IsWindowVisible(handle)
                && !NativeMethods.IsWindowCloaked(handle)
                && WindowCatalog.ShouldInclude(handle, DockNativeMethods.GetShellWindow()))
            {
                _ = ActivateHandleOrProcessAsync(handle, processId, monitorHandle);
                return true;
            }

            if (processId == 0)
            {
                return false;
            }

            _ = ActivateProcessAttentionAsync(processId, monitorHandle);
            return true;
        }

        private async Task ActivateHandleOrProcessAsync(nint handle, uint processId, nint monitorHandle)
        {
            var restore = DockNativeMethods.IsIconic(handle);
            var result = await WindowCommandService.FocusAsync(
                new TrackedWindow(handle, processId, "窗口", null, null, false, restore),
                restore);
            if (result.Succeeded)
            {
                return;
            }

            if (result.NoLiveWindows && processId != 0)
            {
                await ActivateProcessAttentionAsync(processId, monitorHandle);
                return;
            }

            if (!owner._isExiting && !string.IsNullOrWhiteSpace(result.Message))
            {
                owner._trayIcon.ShowBalloonTip(3000, "顶端信息条", result.Message, TrayTipKind.Info);
            }
        }

        private async Task ActivateProcessAttentionAsync(uint processId, nint monitorHandle)
        {
            var (running, instancePids, rootExecutablePath) = await Task.Run(() =>
            {
                var captured = new WindowCatalog().Capture();
                var (pids, rootExe) = WindowCommandService.ResolveInstanceInfo(processId);
                return (captured, pids, rootExe);
            });

            var instanceWindows = running
                .Where(window => window.ProcessId == processId || instancePids.Contains(window.ProcessId))
                .ToArray();
            if (instanceWindows.Length > 0)
            {
                var group = WindowGrouping.Group(instanceWindows)[0];
                var target = WindowCommandService.SelectBestWindow(instanceWindows);
                var focusResult = await WindowCommandService.FocusAsync(
                    target,
                    DockNativeMethods.IsIconic(target.Handle),
                    group.DisplayName);
                if (!focusResult.Succeeded && !owner._isExiting && !string.IsNullOrWhiteSpace(focusResult.Message))
                {
                    owner._trayIcon.ShowBalloonTip(3000, group.DisplayName, focusResult.Message, TrayTipKind.Info);
                }
                return;
            }

            var executablePath = rootExecutablePath;
            if (string.IsNullOrWhiteSpace(executablePath)
                || DockAppResolver.IsFrameHost(executablePath)
                || DockAppResolver.IsPackagedPath(executablePath))
            {
                return;
            }

            var pinned = owner._state.Dock.PinnedApps.FirstOrDefault(candidate =>
                    !string.IsNullOrWhiteSpace(candidate.ExecutablePath)
                    && (DockPinRules.MatchesExecutablePath(candidate, executablePath)
                        || DockQuitRules.MatchesExecutableOrVersionSibling(candidate.ExecutablePath, executablePath)))
                ?? new DockPinnedApp
                {
                    DisplayName = DockAppResolver.GetFileDescription(executablePath)
                        ?? Path.GetFileNameWithoutExtension(executablePath),
                    ExecutablePath = executablePath
                };
            var result = await WindowCommandService.RevealOrLaunchAsync(pinned, preferredProcessId: processId);
            if (!result.Succeeded && !owner._isExiting)
            {
                if (result.OpenTrayRecommended && owner.TryOpenTrayForApplication(monitorHandle))
                {
                    return;
                }
                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    owner._trayIcon.ShowBalloonTip(3000, pinned.DisplayName, result.Message, TrayTipKind.Info);
                }
            }
        }

        public void ExitApp() => owner.Exit();
    }
}
