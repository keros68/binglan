using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.App.Taskbar;
using BingLan.App.Themes;
using BingLan.Core.Dock;
using Microsoft.Win32;
using BingLan.Core.Themes;
using BingLan.App.Windows;
using BingLan.Core.Models;
using BingLan.Core.Services;
using Forms = System.Windows.Forms;

namespace BingLan.App;

public sealed class WidgetCoordinator : IDisposable
{
    private static readonly DesktopComponentKind[] InformationComponentKinds =
    [
        DesktopComponentKind.TimeDate,
        DesktopComponentKind.Greeting,
        DesktopComponentKind.Weather,
        DesktopComponentKind.Performance
    ];

    private readonly LocalStateStore _store;
    private readonly List<WidgetWindowBase> _windows = [];
    private readonly DispatcherTimer _saveTimer;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly WindowsPerformanceSamplingService _performanceSampler;
    private readonly WeatherService _weatherService;
    private readonly CityLookupService _citySearchService;
    private readonly DockHost _dock;
    private readonly TaskbarAdapter _taskbar;
    private readonly CleanDesktopAdapter _cleanDesktop;
    private readonly TaskbarCornerReveal _cornerReveal = new();
    private readonly DockIconStore _iconStore;
    private readonly AppUpdater _updater;
    private Forms.ToolStripMenuItem? _updateMenuItem;
    private Action? _balloonClicked;
    private IReadOnlyDictionary<string, byte[]> _pendingThemeIcons = new Dictionary<string, byte[]>();
    private readonly SemaphoreSlim _cleanDesktopGate = new(1, 1);
    private bool _cleanDesktopRecovered;
    private bool _cleanDesktopRestoreFailed;
    private readonly StartupRegistration _startup;
    private readonly FullScreenWatcher _fullScreenWatcher = new();
    private readonly DesktopSurfaceWatcher _desktopSurfaceWatcher;
    private readonly FileMappingWatchService _mappingWatcher;
    private readonly List<WidgetWindowBase> _windowsHiddenFromTray = [];
    private Forms.ToolStripMenuItem? _toggleDesktopMenuItem;
    private bool _desktopHidden;
    private bool _saveFailureShown;
    private readonly string[] _launchArguments;
    private readonly bool _showOnboarding;
    private AppState _state;
    private SettingsWindow? _settingsWindow;
    private bool _isExiting;
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
        _performanceSampler = new WindowsPerformanceSamplingService();
        _weatherService = new WeatherService();
        _citySearchService = new CityLookupService();
        _mappingWatcher = new FileMappingWatchService(SweepGoneFileMappings, ImportNewDesktopEntries);
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };
        _trayIcon = BuildTrayIcon();
        _desktopSurfaceWatcher = new DesktopSurfaceWatcher(
            () => _windows
                .Where(window => window.IsVisible)
                .Select(window => window.WindowHandle)
                .Where(handle => handle != 0)
                .ToList());
        // Only the installed app checks on its own; test and interactive runs keep quiet.
        _updater = new AppUpdater(_state.Updates, AppVersion, ScheduleSave, Exit, canAutoCheck: dataDirectory is null);
        _updater.ReleaseFound += OnReleaseFound;
        _iconStore = new DockIconStore(Path.Combine(_store.DataDirectory, "icons"));
        DockAppResolver.IconStore = _iconStore;
        ApplyStyleSettings();
        _dock = new DockHost(
            _state.Dock,
            () =>
            {
                ScheduleSave();
                _settingsWindow?.RefreshDockSettings();
            },
            () => OpenSettings("Dock"),
            message => _trayIcon.ShowBalloonTip(
                3000,
                "冰蓝 Dock",
                message,
                Forms.ToolTipIcon.Info));
        _taskbar = new TaskbarAdapter(TaskbarCheckpointPath(_store.DataDirectory));
        _taskbar.StatusChanged += OnTaskbarStatusChanged;
        _taskbar.AutoHideTakenOver += () =>
        {
            _state.Taskbar.Mode = TaskbarMode.SystemDefault;
            ScheduleSave();
            _settingsWindow?.RefreshTaskbarSettings();
        };
        StartupLog.Open(_store.DataDirectory);
        _cleanDesktop = new CleanDesktopAdapter(CleanDesktopCheckpointPath(_store.DataDirectory));
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
        var informationVisible = HasVisibleInformationComponent();
        foreach (var information in _state.InformationWidgets.ToList())
        {
            if (informationVisible)
            {
                foreach (var kind in InformationComponentKinds.Where(
                             kind => _state.DesktopExperience.GetComponent(kind).IsVisible))
                {
                    ShowInformation(information, kind);
                }
            }
        }
        ApplyQuickPlaces();
        var todoVisible = _state.DesktopExperience
            .GetComponent(DesktopComponentKind.Todo)
            .IsVisible;
        var todoComponent = _state.DesktopExperience.GetComponent(DesktopComponentKind.Todo);
        foreach (var todo in _state.TodoWidgets.ToList())
        {
            if (todoVisible)
            {
                if (ReferenceEquals(todo, _state.TodoWidgets.FirstOrDefault()))
                {
                    todo.Placement = todoComponent.Placement;
                    todo.IsLocked = todoComponent.IsLocked;
                }
                ShowTodo(todo);
            }
        }
        foreach (var note in _state.NoteWidgets.ToList())
        {
            ShowNote(note);
        }
        foreach (var box in _state.FileBoxes.ToList())
        {
            ShowFileBox(box);
        }

        if (_windows.Count == 0)
        {
            AddTodo();
        }
        foreach (var window in _windows)
        {
            window.RestoreDisplayLayout();
            WindowScreenRecovery.EnsureOnScreen(window);
        }
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
        _fullScreenWatcher.Changed += () => _cornerReveal.SetSuspended(_fullScreenWatcher.IsFullScreenInFront);
        _fullScreenWatcher.Start();
        _desktopSurfaceWatcher.Changed += OnDesktopSurfaceChanged;
        _desktopSurfaceWatcher.Start();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        WindowScreenRecovery.LayoutChanged += OnDisplayLayoutChanged;
        SaveNow();
        if (_showOnboarding && !_state.OnboardingCompleted)
        {
            ShowOnboarding();
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
            StartupLog.Write("资源管理器就绪，等待桌面稳定");
            await Task.Delay(LogonSettleDelay);
        }
        if (_isExiting)
        {
            return;
        }

        // Icons left hidden by a run that did not exit cleanly come back first; clean
        // desktop is then re-applied from the saved setting.
        var path = CleanDesktopCheckpointPath(_store.DataDirectory);
        await _cleanDesktopGate.WaitAsync();
        try
        {
            var restored = await Task.Run(() => CleanDesktopAdapter.RecoverNow(path));
            _cleanDesktopRestoreFailed = !restored;
            _cleanDesktopRecovered = restored && CleanDesktopAdapter.IsOriginalStateKnown(path);
        }
        finally
        {
            _cleanDesktopGate.Release();
        }
        StartupLog.Write("桌面图标恢复检查完成");
        if (_isExiting)
        {
            return;
        }

        RunShellStep("Dock ", _dock.Apply);
        await ApplyTaskbarModeAsync();
        StartupLog.Write("任务栏模式已应用");
        RunShellStep("任务栏两角唤出", () => _cornerReveal.SetEnabled(_state.Taskbar.RevealOnlyAtCorners));
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
                    "上次隐藏的桌面图标未能自动恢复，可在桌面右键菜单“查看”中重新显示。",
                    Forms.ToolTipIcon.Warning);
            }
        }
        else if (_state.CleanDesktopEnabled)
        {
            await SetCleanDesktopAsync(true);
            StartupLog.Write("清爽桌面已应用");
        }
        ScheduleSave();
        _updater.Start();
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
            Forms.ToolTipIcon.Info);
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
        _store.CreateBackup(_state);
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
                         component.Kind is not (DesktopComponentKind.TimeDate or DesktopComponentKind.Greeting)))
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
            Forms.ToolTipIcon.Warning);
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
    private static readonly TimeSpan LogonSettleDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ExplorerWaitLimit = TimeSpan.FromSeconds(90);

    // Explorer is ready once both the taskbar and the desktop window exist.
    private static async Task WaitForExplorerAsync()
    {
        var deadline = DateTime.UtcNow + ExplorerWaitLimit;
        while (DateTime.UtcNow < deadline
               && (Interop.TaskbarNativeMethods.FindWindowW("Shell_TrayWnd", null) == 0
                   || Interop.TaskbarNativeMethods.FindWindowW("Progman", null) == 0))
        {
            await Task.Delay(1000);
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
            information.RefreshConfiguredValues(false);
        }
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
            Monitors = MonitorCatalog.GetAll()
                .Select(monitor => new OnboardingMonitor(monitor.DeviceName, monitor.Label, monitor.IsPrimary))
                .ToList(),
            ApplyMonitor = deviceName =>
            {
                _state.Dock.MonitorDeviceName = deviceName;
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
                DesktopModeRules.Apply(mode, _state.Dock, _state.Taskbar);
                _dock.Apply();
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
                AddFileBox();
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

    private void UpdateSamplingMode() =>
        _performanceSampler.SetMode(PerformanceSamplingRules.ResolveMode(
            _fullScreenWatcher.IsFullScreenInFront,
            _desktopHidden));

    // Runs after monitors are added, removed or rearranged, or scaling changes.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => OnDisplayLayoutChanged();

    // Cards go back to where they last sat on the arrangement now in use; on an arrangement
    // seen for the first time they are only kept in view.
    private void OnDisplayLayoutChanged() =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () =>
            {
                foreach (var window in _windows.ToList())
                {
                    window.RestoreDisplayLayout();
                    WindowScreenRecovery.EnsureOnScreen(window);
                }
                // Saving captures the cards again, now remembered for this arrangement.
                WindowScreenRecovery.SettleLayout();
                ScheduleSave();
            });

    private void ToggleDesktop()
    {
        if (_desktopHidden)
        {
            foreach (var window in _windowsHiddenFromTray.Where(_windows.Contains))
            {
                window.Show();
            }
            _windowsHiddenFromTray.Clear();
            _desktopHidden = false;
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

    public void Dispose()
    {
        if (!_isExiting)
        {
            SaveNow();
        }
        _trayIcon.Visible = false;
        var trayIconImage = _trayIcon.Icon;
        _trayIcon.Icon = null;
        _trayIcon.Dispose();
        trayIconImage?.Dispose();
        _settingsWindow?.Close();
        _settingsWindow = null;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        WindowScreenRecovery.LayoutChanged -= OnDisplayLayoutChanged;
        AccessibilityThemeManager.HighContrastChanged -= OnHighContrastChanged;
        _fullScreenWatcher.Dispose();
        _desktopSurfaceWatcher.Dispose();
        _mappingWatcher.Dispose();
        _dock.Dispose();
        _cornerReveal.Dispose();
        _taskbar.Dispose();
        _cleanDesktop.Dispose();
        _performanceSampler.Dispose();
    }

    private TodoWidgetWindow ShowTodo(TodoWidgetState state)
    {
        SanitizePlacement(state.Placement);
        var window = new TodoWidgetWindow(state)
        {
            ShowInTaskbar = false
        };
        Wire(window);
        _windows.Add(window);
        window.Show();
        return window;
    }

    private FileBoxWindow ShowFileBox(FileBoxState state)
    {
        SanitizePlacement(state.Placement);
        var window = new FileBoxWindow(state)
        {
            ShowInTaskbar = false
        };
        window.OtherBoxes = () => _windows
            .OfType<FileBoxWindow>()
            .Where(other => !ReferenceEquals(other, window))
            .ToList();
        Wire(window);
        _windows.Add(window);
        window.Show();
        return window;
    }

    private void ShowNote(NoteWidgetState state)
    {
        SanitizePlacement(state.Placement);
        var window = new NoteWidgetWindow(state)
        {
            ShowInTaskbar = false
        };
        Wire(window);
        _windows.Add(window);
        window.Show();
    }

    private InformationWidgetWindow ShowInformation(
        InformationWidgetState state,
        DesktopComponentKind componentKind)
    {
        var component = _state.DesktopExperience.GetComponent(componentKind);
        SanitizePlacement(component.Placement);
        var window = new InformationWidgetWindow(
            state,
            _performanceSampler,
            _weatherService,
            _state.DesktopExperience,
            componentKind)
        {
            ShowInTaskbar = false
        };
        window.ApplyPlacement(component.Placement, component.IsLocked);
        Wire(window);
        _windows.Add(window);
        window.Show();
        return window;
    }

    private void Wire(WidgetWindowBase window)
    {
        window.ApplyStyle(_state.Style);
        // A card that appears while the desktop is shown would be covered by the
        // raised desktop surface; lift it with the others.
        window.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && _desktopSurfaceWatcher.IsDesktopShown)
            {
                var slot = NativeMethods.FindWindowW("Shell_TrayWnd", null);
                window.RaiseAboveDesktop(slot != 0 ? slot : DockNativeMethods.HwndTopmost);
            }
        };
        window.WidgetChanged += OnWindowStateChanged;
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

    /// <summary>
    /// The shell just showed or stopped showing the desktop. While it is shown, the
    /// raised desktop surface would cover the cards behind the wallpaper, so they are
    /// lifted into the topmost band under the taskbar; when ordinary windows return
    /// they go back below them.
    /// </summary>
    private void OnDesktopSurfaceChanged()
    {
        if (_desktopSurfaceWatcher.IsDesktopShown)
        {
            RaiseCardsAboveDesktop();
            return;
        }
        foreach (var window in _windows.Where(window => window.IsVisible))
        {
            window.SendToBack();
        }
    }

    private void RaiseCardsAboveDesktop()
    {
        // Under the taskbar so the taskbar stays reachable; each following card goes
        // below the previous one to keep the stacking order between the cards.
        var slot = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (slot == 0)
        {
            slot = DockNativeMethods.HwndTopmost;
        }
        foreach (var window in _windows.Where(window => window.IsVisible))
        {
            window.RaiseAboveDesktop(slot);
            var handle = window.WindowHandle;
            if (handle != 0)
            {
                slot = handle;
            }
        }
    }

    private void OnWindowStateChanged(WidgetWindowBase window)
    {
        if (_isApplyingDesktopExperience)
        {
            return;
        }

        Capture(window);
        ScheduleSave();
        if (window is FileBoxWindow && _state.FileBoxAutomationEnabled)
        {
            // Box membership changed; keep the watched folders and the gone sweep current.
            _mappingWatcher.RequestSweep();
        }
    }

    private void Capture(WidgetWindowBase window)
    {
        switch (window)
        {
            case TodoWidgetWindow todo:
                todo.CaptureState();
                if (_state.TodoWidgets.FirstOrDefault()?.Id == todo.State.Id)
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
    }

    private void AddTodo()
    {
        _state.DesktopExperience
            .GetComponent(DesktopComponentKind.Todo)
            .IsVisible = true;
        var offset = _state.TodoWidgets.Count * 24;
        var state = TodoService.CreateDefaultWidget();
        state.Title = WidgetTitleRules.NextTitle(state.Title, _state.TodoWidgets.Select(todo => todo.Title));
        if (_state.TodoWidgets.Count == 0)
        {
            state.IsLocked = _state.DesktopExperience
                .GetComponent(DesktopComponentKind.Todo)
                .IsLocked;
        }
        state.Placement = _state.TodoWidgets.Count == 0
            ? _state.DesktopExperience.GetComponent(DesktopComponentKind.Todo).Placement
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
        var offset = _state.FileBoxes.Count * 24;
        var state = new FileBoxState
        {
            Placement = new WindowPlacement
            {
                Left = 432 + offset,
                Top = 92 + offset,
                Width = 366,
                Height = 306
            }
        };
        state.Title = WidgetTitleRules.NextTitle(state.Title, _state.FileBoxes.Select(box => box.Title));
        UseSharedLook(state.Appearance);
        _state.FileBoxes.Add(state);
        ShowFileBox(state);
        ScheduleSave();
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
        var offset = _state.NoteWidgets.Count * 24;
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
        foreach (var kind in InformationComponentKinds)
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
        foreach (var kind in InformationComponentKinds)
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
        ScheduleSave();
    }

    private void OpenSettings() => OpenSettings((WidgetWindowBase?)null);

    private void OpenSettings(string page)
    {
        OpenSettings((WidgetWindowBase?)null);
        _settingsWindow?.ShowSettingsPage(page);
    }

    private void OpenSettings(WidgetWindowBase? target)
    {
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
                DataDirectory = _store.DataDirectory,
                OpenDataDirectory = OpenDataDirectory,
                CreateBackup = () =>
                {
                    CaptureAll();
                    return _store.CreateBackup(_state);
                },
                ListBackups = _store.ListBackups,
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
                DeleteWidget = window =>
                {
                    DeleteWidget(window);
                    return !_windows.Contains(window) || !window.IsVisible;
                },
                ExportTheme = ExportTheme,
                ReadTheme = ReadTheme,
                ImportTheme = ImportThemeAsync,
                PinBoundApp = PinBoundApp,
                IsCleanDesktopEnabled = () => _state.CleanDesktopEnabled,
                SetCleanDesktop = SetCleanDesktopAsync,
                IsFileBoxAutomationEnabled = () => _state.FileBoxAutomationEnabled,
                SetFileBoxAutomation = SetFileBoxAutomation,
                CleanDesktopNotice = CleanDesktopNotice,
                RetryCleanDesktopRestore = RetryCleanDesktopRestoreAsync,
                Style = _state.Style,
                ApplyStyle = ApplyStyle,
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
                Updater = _updater,
                ApplyGlass = ApplyGlass,
                ImportTaskbarPins = () =>
                {
                    var added = _dock.ImportTaskbarPins();
                    _dock.Apply();
                    SaveNow();
                    return added;
                }
            });
        settings.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsWindow, settings))
            {
                _settingsWindow = null;
            }
        };
        _settingsWindow = settings;
        if (target is not null)
        {
            settings.ShowWidgetSettings(target);
        }
        settings.Show();
        settings.Activate();
    }

    private void ApplyDesktopExperience(DesktopExperienceState experience)
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
            ApplyDesktopExperienceCore(experience);
        }
        finally
        {
            _isApplyingDesktopExperience = false;
        }
    }

    private void ApplyDesktopExperienceCore(DesktopExperienceState experience)
    {
        DesktopExperienceRules.Normalize(experience);
        _state.DesktopExperience = experience;
        if (HasVisibleInformationComponent() && !_state.InformationWidgets.Any())
        {
            _state.InformationWidgets.Add(new InformationWidgetState());
        }
        var todoComponent = experience.GetComponent(DesktopComponentKind.Todo);
        if (todoComponent.IsVisible && !_state.TodoWidgets.Any())
        {
            _state.TodoWidgets.Add(TodoService.CreateDefaultWidget());
        }
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
                            candidate.State.Id == state.Id &&
                            candidate.ComponentKind == kind);
                    if (!component.IsVisible)
                    {
                        window?.HideFromApp();
                        continue;
                    }
                    if (window is null)
                    {
                        window = ShowInformation(state, kind);
                    }
                    window.ApplyDesktopExperience(experience);
                    window.ApplyPlacement(component.Placement, component.IsLocked);
                    window.Show();
                }
            }
        }
        else
        {
            foreach (var window in _windows.OfType<InformationWidgetWindow>())
            {
                window.HideFromApp();
            }
        }

        if (todoComponent.IsVisible)
        {
            foreach (var state in _state.TodoWidgets)
            {
                var window = _windows
                    .OfType<TodoWidgetWindow>()
                    .FirstOrDefault(candidate => candidate.State.Id == state.Id);
                if (window is null)
                {
                    window = ShowTodo(state);
                }
                window.SetWidgetBackgroundColor(todoComponent.Appearance.BackgroundColor);
                window.SetWidgetBackgroundOpacity(todoComponent.Appearance.BackgroundOpacity);
                window.SetWidgetTextColor(todoComponent.Appearance.TextColor);
                window.WidgetCornerRadius = todoComponent.CornerRadius;
                var placement = ReferenceEquals(state, _state.TodoWidgets.FirstOrDefault())
                    ? todoComponent.Placement
                    : state.Placement;
                if (ReferenceEquals(state, _state.TodoWidgets.FirstOrDefault()))
                {
                    state.IsLocked = todoComponent.IsLocked;
                }
                state.Placement = placement;
                window.ApplyPlacement(placement, state.IsLocked);
                window.Show();
            }
        }
        else
        {
            foreach (var window in _windows.OfType<TodoWidgetWindow>())
            {
                window.HideFromApp();
            }
        }
        ApplyQuickPlaces();

        SaveNow();
    }

    // The row of system places follows its component: shown, placed and coloured by it.
    private void ApplyQuickPlaces()
    {
        var component = _state.DesktopExperience.GetComponent(DesktopComponentKind.QuickLaunch);
        var window = _windows.OfType<QuickPlacesWindow>().FirstOrDefault();
        if (!component.IsVisible)
        {
            window?.HideFromApp();
            return;
        }
        if (window is null)
        {
            SanitizePlacement(component.Placement);
            window = new QuickPlacesWindow();
            Wire(window);
            _windows.Add(window);
        }
        window.SetItems(_state.QuickPlaces);
        window.ApplyComponent(component);
        window.ApplyPlacement(component.Placement, component.IsLocked);
        window.Show();
    }

    private void ApplyLayoutPreset(DesktopExperienceState experience)
    {
        DesktopExperienceRules.Normalize(experience);
        _state.DesktopExperience = experience;
        ApplyPresetPlacements(experience.ActivePreset);
        ApplyDesktopExperience(experience);
    }

    private void ApplyDock()
    {
        ApplyItemSurface();
        _dock.Apply();
        SaveNow();
    }

    private string CleanDesktopNotice() =>
        _cleanDesktopRestoreFailed
            ? "上次隐藏的桌面图标未能自动恢复。可点“重新恢复”再试，或在桌面右键菜单“查看”中勾选“显示桌面图标”。"
            : !_cleanDesktopRecovered
                ? "无法确认开启清爽桌面前的图标状态。请先在桌面右键菜单“查看”中显示桌面图标，再开启清爽桌面。"
                : string.Empty;

    private async Task<string> RetryCleanDesktopRestoreAsync()
    {
        var path = CleanDesktopCheckpointPath(_store.DataDirectory);
        var restored = await Task.Run(() => CleanDesktopAdapter.RecoverNow(path));
        _cleanDesktopRestoreFailed = !restored;
        _cleanDesktopRecovered = restored && CleanDesktopAdapter.IsOriginalStateKnown(path);
        return restored ? "桌面图标已恢复" : "仍未能恢复，请在桌面右键菜单“查看”中勾选“显示桌面图标”";
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
                _state.CleanDesktopEnabled = false;
                ScheduleSave();
                return await _cleanDesktop.DisableAsync()
                    ? "桌面图标已恢复"
                    : "桌面图标未能恢复，下次启动时会再次尝试";
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

            var problem = await _cleanDesktop.EnableAsync(_cleanDesktopRecovered);
            _state.CleanDesktopEnabled = problem.Length == 0;
            // A successful turn-on either recorded visible icons or found them hidden with
            // a known history, so the original state is known again from here on.
            _cleanDesktopRecovered |= problem.Length == 0;
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

    // Card spacing and dock motion are read when a card is dragged or an icon hovered,
    // so they take effect at once; the card shadow is redrawn on every open card.
    private void ApplyStyleSettings()
    {
        WidgetWindowBase.CardSpacingDip = _state.Style.CardSpacing;
        ApplyItemSurface();
        IceBlueDockWindow.Motion = _state.Style.Motion;
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
            ApplyStyleSettings();
        }

        var glass = DesktopStyleRules.Appearance(style);
        var nakedText = DesktopStyleRules.NakedTextColor(style);
        ApplyItemSurface();
        foreach (var component in _state.DesktopExperience.Components)
        {
            var naked = component.Kind is DesktopComponentKind.TimeDate or DesktopComponentKind.Greeting;
            component.Appearance.BackgroundColor = glass.BackgroundColor;
            component.Appearance.BackgroundOpacity = naked
                ? WidgetAppearanceRules.MinimumBackgroundOpacity
                : glass.BackgroundOpacity;
            component.Appearance.TextColor = naked ? nakedText : glass.TextColor;
            if (component.Kind == DesktopComponentKind.QuickLaunch && style.Glass == GlassMode.Clear)
            {
                // Thin line icons on bare wallpaper are hard to see, so the strip keeps a
                // light frosted bar even without card backings.
                component.Appearance.BackgroundColor = "#FFFFFF";
                component.Appearance.BackgroundOpacity = QuickPlacesWindow.ClearBarOpacity;
                component.Appearance.TextColor = nakedText;
            }
        }
        foreach (var window in _windows.Where(window => window is not InformationWidgetWindow))
        {
            window.SetWidgetBackgroundColor(glass.BackgroundColor);
            window.SetWidgetBackgroundOpacity(glass.BackgroundOpacity);
            window.SetWidgetHeaderColor(glass.HeaderColor);
            window.SetWidgetHeaderOpacity(glass.HeaderOpacity);
            window.SetWidgetTextColor(glass.TextColor);
        }
        foreach (var note in _windows.OfType<NoteWidgetWindow>())
        {
            note.SetBodyColor(glass.TextColor);
        }
        ApplyDesktopExperience(_state.DesktopExperience);
        SaveNow();
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
        resources["WidgetItemSurfaceBrush"] = Brush(DesktopStyleRules.Appearance(_state.Style).ItemSurfaceColor);
        var dock = DesktopStyleRules.Dock(_state.Style, _state.Dock);
        resources["DockSurfaceBrush"] = Brush(dock.Surface);
        resources["DockSurfaceBorderBrush"] = Brush(dock.Border);
        resources["DockRunningDotBrush"] = Brush(dock.RunningDot);
        resources["PerformanceAccentBrush"] = Brush("#F5" + _state.Style.AccentColor.TrimStart('#'));

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
        SaveNow();
    }

    private void ApplyTaskbar()
    {
        _ = ApplyTaskbarModeAsync();
        _cornerReveal.SetEnabled(_state.Taskbar.RevealOnlyAtCorners);
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
    }

    private void OnTaskbarStatusChanged()
    {
        _settingsWindow?.RefreshTaskbarSettings();
        if (!_isApplyingTaskbar && _taskbar.Problem.Length > 0)
        {
            _trayIcon.ShowBalloonTip(
                3500,
                "Windows 任务栏",
                _taskbar.Problem,
                Forms.ToolTipIcon.Warning);
        }
    }

    private void ApplyWidgetLock(WidgetWindowBase window, bool isLocked)
    {
        window.ApplyWidgetLock(isLocked);
        switch (window)
        {
            case InformationWidgetWindow { ComponentKind: { } kind }:
                _state.DesktopExperience.GetComponent(kind).IsLocked = isLocked;
                break;
            case TodoWidgetWindow todo
                when _state.TodoWidgets.FirstOrDefault()?.Id == todo.State.Id:
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
        if (_state.TodoWidgets.FirstOrDefault() is { } todo)
        {
            todo.Placement = _state.DesktopExperience.GetComponent(DesktopComponentKind.Todo).Placement;
        }
    }

    private static IReadOnlyDictionary<DesktopComponentKind, WindowPlacement> PresetPlacements(DesktopLayoutPreset preset)
    {
        var area = SystemParameters.WorkArea;
        return DesktopExperienceRules.PresetPlacements(preset, area.Left, area.Top, area.Right, area.Bottom);
    }

    /// <summary>Moves one card back to where the current layout preset puts it.</summary>
    private bool ResetWidgetPlacement(WidgetWindowBase? window, DesktopComponentKind? kind)
    {
        var area = SystemParameters.WorkArea;
        if (kind is { } componentKind
            && PresetPlacements(_state.DesktopExperience.ActivePreset).TryGetValue(componentKind, out var preset))
        {
            _state.DesktopExperience.GetComponent(componentKind).Placement = preset;
            if (componentKind == DesktopComponentKind.Todo && _state.TodoWidgets.FirstOrDefault() is { } todo)
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
        placement.Left = area.Left + defaultLeft;
        placement.Top = area.Top + defaultTop;
        window.ApplyPlacement(placement, window.IsWidgetLocked);
        SaveNow();
        return true;
    }


    private void DeleteWidget(WidgetWindowBase window)
    {
        var message = window switch
        {
            NoteWidgetWindow => "将删除此便签及其中的文字。继续吗？",
            InformationWidgetWindow { ComponentKind: { } kind } =>
                $"将从桌面隐藏“{DesktopExperienceRules.GetComponentName(kind)}”。继续吗？",
            InformationWidgetWindow => "将删除此桌面信息组件及其本机设置。继续吗？",
            TodoWidgetWindow => "将删除此待办及其中的项目。继续吗？",
            FileBoxWindow => "将删除此分组盒及其中的映射。继续吗？",
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

        switch (window)
        {
            case TodoWidgetWindow todo:
                _state.TodoWidgets.RemoveAll(x => x.Id == todo.State.Id);
                // With the last one gone the to-do card is off, so a later settings change
                // does not bring back an empty one.
                if (_state.TodoWidgets.Count == 0)
                {
                    _state.DesktopExperience.GetComponent(DesktopComponentKind.Todo).IsVisible = false;
                    _settingsWindow?.RefreshDesktopExperience();
                }
                break;
            case NoteWidgetWindow note:
                _state.NoteWidgets.RemoveAll(x => x.Id == note.State.Id);
                break;
            case QuickPlacesWindow:
                _state.DesktopExperience.GetComponent(DesktopComponentKind.QuickLaunch).IsVisible = false;
                break;
            case InformationWidgetWindow information:
                if (information.ComponentKind is { } kind)
                {
                    _state.DesktopExperience.GetComponent(kind).IsVisible = false;
                }
                else
                {
                    _state.InformationWidgets.RemoveAll(x => x.Id == information.State.Id);
                    _settingsWindow?.Close();
                }
                break;
            case FileBoxWindow box:
                _state.FileBoxes.RemoveAll(x => x.Id == box.State.Id);
                // A category box the user deleted is not created again by organising.
                if (box.State.DesktopCategory != DesktopGroupCategory.None
                    && !_state.DismissedDesktopCategories.Contains(box.State.DesktopCategory))
                {
                    _state.DismissedDesktopCategories.Add(box.State.DesktopCategory);
                }
                break;
        }
        _windows.Remove(window);
        window.CanClose = true;
        window.Close();
        if (!_windows.OfType<InformationWidgetWindow>().Any())
        {
            _performanceSampler.Stop();
        }
        SaveNow();
    }

    private void Backup()
    {
        CaptureAll();
        var path = _store.CreateBackup(_state);
        _trayIcon.ShowBalloonTip(
            2500,
            "备份完成",
            $"已保存到 {path}",
            Forms.ToolTipIcon.Info);
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
        _store.CreateBackup(_state);
        ThemeRules.ApplyVisuals(package, _state.DesktopExperience, WorkArea());
        ApplyDesktopExperience(_state.DesktopExperience);
        var taskbarBefore = _state.Taskbar.Mode;
        ThemeRules.ApplyStyle(package, _state.Style, _state.Taskbar);
        ApplyStyleSettings();
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
        _state.Dock.PinnedApps = bindings.Resolved.ToList();
        _dock.Apply();
        SaveNow();
        _settingsWindow?.RefreshDockSettings();

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

    // The running app saves on exit, so the backup is queued and applied by the next
    // instance before it loads any state.
    private void RestoreBackupAndRestart(string backupPath)
    {
        _store.ScheduleRestore(backupPath);
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
    /// Removes mappings whose originals are confirmed deleted (the drive is reachable, so
    /// the file is really gone rather than on an unplugged drive or an offline share) and
    /// refreshes the watched folders. Runs once at startup and after watcher notices or box
    /// changes; the filesystem checks run off the UI thread.
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
                var items = _state.FileBoxes.SelectMany(box => box.Items).ToList();
                var (goneIds, parents) = await Task.Run(() => (
                    FileMappingService.CollectGoneIds(items),
                    FileMappingService.CollectExistingParentDirectories(items.Select(item => item.Path))));
                if (_isExiting)
                {
                    return;
                }

                foreach (var box in _windows.OfType<FileBoxWindow>())
                {
                    var clearedHere = box.RemoveGoneMappings(goneIds);
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
                var boxes = _state.FileBoxes.ToList();
                var distribution = await Task.Run(() =>
                    FileMappingService.Distribute(CollectImportableDesktopEntries(), boxes));
                if (_isExiting)
                {
                    return;
                }

                foreach (var (boxId, paths) in distribution.ByBox)
                {
                    if (_windows.OfType<FileBoxWindow>().FirstOrDefault(
                            candidate => candidate.State.Id == boxId) is { } boxWindow)
                    {
                        var added = await boxWindow.AddMappingsAsync(paths);
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
            var boxes = _state.FileBoxes.ToList();
            var goneIds = await Task.Run(() =>
                FileMappingService.CollectGoneIds(boxes.SelectMany(box => box.Items)));
            var cleared = 0;
            if (goneIds.Count > 0)
            {
                foreach (var box in _windows.OfType<FileBoxWindow>())
                {
                    cleared += box.RemoveGoneMappings(goneIds);
                }
            }

            var distribution = await Task.Run(() =>
                FileMappingService.Distribute(CollectImportableDesktopEntries(), boxes));

            var added = 0;
            var created = 0;
            var left = 0;
            foreach (var (boxId, paths) in distribution.ByBox)
            {
                if (_windows.OfType<FileBoxWindow>().FirstOrDefault(candidate => candidate.State.Id == boxId) is { } boxWindow)
                {
                    added += await boxWindow.AddMappingsAsync(paths);
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
                    box => box.DesktopCategory == DesktopGroupCategory.None &&
                           box.Items.Count == 0 &&
                           box.CollectExtensions.Count == 0 &&
                           !box.CollectFolders &&
                           box.Title == "桌面分组");
                if (state is null)
                {
                    state = CreateCategorizedFileBox(category);
                    _state.FileBoxes.Add(state);
                    window = ShowFileBox(state);
                    created++;
                }
                else
                {
                    window = _windows.OfType<FileBoxWindow>().Single(
                        candidate => candidate.State.Id == state.Id);
                    window.AssignDesktopCategory(category, GetCategoryTitle(category));
                }

                added += await window.AddMappingsAsync(paths);
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
                status += $"；{left} 项的分类盒已删除，留在桌面（可给分组盒设置收集规则）";
            }
            foreach (var box in _windows.OfType<FileBoxWindow>())
            {
                box.SetOperationStatus(status);
            }
            // Hiding the original icons is a separate setting, so the result says where it is
            // while that setting is off.
            var hint = _state.CleanDesktopEnabled
                ? string.Empty
                : "桌面上的原图标仍会显示；在设置的“外观”页开启“清爽桌面”后可只显示分组盒。";
            _trayIcon.ShowBalloonTip(
                6000,
                "桌面归类完成",
                $"新增 {added} 项，创建 {created} 个分类盒"
                + (cleared > 0 ? $"，清除 {cleared} 项已失效映射" : string.Empty)
                + $"。{hint}",
                Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            foreach (var box in _windows.OfType<FileBoxWindow>())
            {
                box.SetOperationStatus("整理未完成");
            }
            _trayIcon.ShowBalloonTip(
                3500,
                "桌面归类未完成",
                ex.Message,
                Forms.ToolTipIcon.Warning);
        }
        finally
        {
            _isOrganizingDesktop = false;
        }
    }

    // This PC, the Recycle Bin, Downloads and the desktop folder are places, so they live in
    // the folder box: the desktop-folder category box, or a box that collects folders.
    private FileBoxWindow? FolderBox() =>
        _windows.OfType<FileBoxWindow>().FirstOrDefault(box => box.State.DesktopCategory == DesktopGroupCategory.Folders)
        ?? _windows.OfType<FileBoxWindow>().FirstOrDefault(box => box.State.CollectFolders);

    private void MoveSystemPlacesToFolderBox()
    {
        if (FolderBox() is not { } folders)
        {
            return;
        }
        foreach (var box in _windows.OfType<FileBoxWindow>().Where(box => box != folders).ToList())
        {
            box.MoveSystemPlacesTo(folders);
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
        _ => "桌面分组"
    };

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // A failed save (file locked by a scanner, disk full) keeps the app running; the next
    // change saves again, and the user hears about it once.
    private void SaveNow()
    {
        CaptureAll();
        try
        {
            _store.Save(_state);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StartupLog.Write($"保存失败：{exception.Message}");
            if (!_saveFailureShown)
            {
                _saveFailureShown = true;
                _trayIcon.ShowBalloonTip(
                    5000,
                    "冰蓝桌面",
                    "设置暂时无法保存，稍后修改时会再次尝试。",
                    Forms.ToolTipIcon.Warning);
            }
        }
    }

    private void CaptureAll()
    {
        foreach (var window in _windows)
        {
            Capture(window);
        }
    }

    private void Exit()
    {
        if (_isExiting)
        {
            return;
        }
        _isExiting = true;
        _saveTimer.Stop();
        SaveNow();
        _settingsWindow?.Close();
        _settingsWindow = null;
        _mappingWatcher.Dispose();
        _dock.Dispose();
        _cornerReveal.Dispose();
        _taskbar.Dispose();
        _cleanDesktop.Dispose();
        _updater.Dispose();
        foreach (var window in _windows.ToList())
        {
            window.CanClose = true;
            window.Close();
        }
        _windows.Clear();
        _trayIcon.Visible = false;
        System.Windows.Application.Current.Shutdown();
    }

    private Forms.NotifyIcon BuildTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(
            "一键整理桌面",
            null,
            async (_, _) => await OrganizeDesktopAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        _toggleDesktopMenuItem = new Forms.ToolStripMenuItem("隐藏桌面组件", null, (_, _) => ToggleDesktop());
        menu.Items.Add(_toggleDesktopMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("显示桌面信息", null, (_, _) => AddInformation());
        menu.Items.Add("新建待办便签", null, (_, _) => AddTodo());
        menu.Items.Add("新建普通便签", null, (_, _) => AddNote());
        menu.Items.Add("新建桌面分组盒", null, (_, _) => AddFileBox());
        menu.Items.Add(new Forms.ToolStripSeparator());
        _updateMenuItem = new Forms.ToolStripMenuItem(string.Empty, null, (_, _) => OpenSettings("About"))
        {
            Visible = false
        };
        menu.Items.Add(_updateMenuItem);
        menu.Items.Add("冰蓝桌面设置…", null, (_, _) => OpenSettings());
        menu.Items.Add("立即备份", null, (_, _) => Backup());
        menu.Items.Add("打开数据目录", null, (_, _) => OpenDataDirectory());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Exit());

        var trayIcon = new Forms.NotifyIcon
        {
            Text = "冰蓝桌面",
            Icon = LoadApplicationIcon(),
            ContextMenuStrip = menu
        };
        trayIcon.DoubleClick += (_, _) => OpenSettings();
        // Only the update notice acts on a click; other notices are plain information.
        trayIcon.BalloonTipClicked += (_, _) => _balloonClicked?.Invoke();
        trayIcon.BalloonTipClosed += (_, _) => _balloonClicked = null;
        return trayIcon;
    }

    private static Icon LoadApplicationIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri(
                "pack://application:,,,/BingLan;component/Assets/Brand/BingLan.ico",
                UriKind.Absolute));
        if (resource is null)
        {
            return (Icon)SystemIcons.Application.Clone();
        }

        using var stream = resource.Stream;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    // A rough bound over all monitors; Start then moves any card that lands between or off
    // monitors onto the nearest work area.
    private static void SanitizePlacement(WindowPlacement placement)
    {
        var area = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        placement.Width = Math.Clamp(placement.Width, 226, Math.Max(226, area.Width));
        placement.Height = Math.Clamp(placement.Height, 72, Math.Max(72, area.Height));
        placement.Left = Math.Clamp(placement.Left, area.Left - placement.Width + 80, area.Right - 80);
        placement.Top = Math.Clamp(placement.Top, area.Top, area.Bottom - 60);
    }
}
