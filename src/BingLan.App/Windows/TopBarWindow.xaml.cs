using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.App.TopBar;
using BingLan.App.Taskbar;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.TopBar;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using Image = System.Windows.Controls.Image;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using ToolTip = System.Windows.Controls.ToolTip;

namespace BingLan.App.Windows;

/// <summary>
/// What the top bar needs from the rest of the app. The window stays a dumb surface:
/// every fact and action comes from here, so tests can fake all of it.
/// </summary>
internal interface ITopBarEnvironment
{
    bool Use24HourClock { get; }

    bool ShowDate => true;

    bool ShowLunarDate => true;

    /// <summary>The cached weather, or null while there is no city or no data yet.</summary>
    WeatherSnapshot? ReadWeather();

    /// <summary>Kicks off a network refresh when the service's own cadence says it is due.</summary>
    void RefreshWeatherIfDue();

    /// <summary>The latest cached peripheral battery snapshot from the host.</summary>
    PeripheralBatterySnapshot ReadPeripheralBattery() => PeripheralBatterySnapshot.Empty;

    /// <summary>Starts a refresh when the peripheral service's cadence says it is due.</summary>
    void RefreshPeripheralBatteryIfDue() { }

    /// <summary>Requests an immediate peripheral refresh.</summary>
    void ForceRefreshPeripheralBattery() { }

    /// <summary>Cancels pending reads when no information bar is running.</summary>
    void StopPeripheralBattery() { }

    int CountIncompleteTodos();

    /// <summary>The first to-do card's live items, for the to-do flyout.</summary>
    TopBarTodoList? ReadTodoList();

    /// <summary>An item was toggled in the flyout; persist at the host's pace.</summary>
    void TodoItemToggled();

    void OpenTopBarSettings();

    void OpenTopBarSettings(TopBarModuleKind? focusModule) => OpenTopBarSettings();

    void TopBarStateChanged() { }

    /// <summary>Opens the system quick-settings flyout (the taskbar tray's panel).</summary>
    void OpenQuickSettings(TopBarModuleKind kind);

    /// <summary>Opens a Windows Settings ms-settings: URI directly without popping up the taskbar.</summary>
    void OpenSystemSettings(string settingsUri) => OpenQuickSettings(TopBarModuleKind.Volume);

    /// <summary>Opens Windows' shutdown/restart selection dialog without confirming an action.</summary>
    void OpenShutdownDialog() { }

    Task<TopBarAudioSnapshot> ReadAudioSnapshotAsync() => Task.FromResult(TopBarAudioSnapshot.Unavailable);

    Task<bool> SetMasterVolumeAsync(int percent) => Task.FromResult(false);

    Task<bool> SetMasterMuteAsync(bool muted) => Task.FromResult(false);

    Task<bool> SetDefaultOutputDeviceAsync(string deviceId) => Task.FromResult(false);

    Task<bool> SetAudioSessionVolumeAsync(string? deviceId, uint processId, int percent) => Task.FromResult(false);

    Task<bool> SetAudioSessionMuteAsync(string? deviceId, uint processId, bool muted) => Task.FromResult(false);

    Task<TopBarBatterySnapshot> ReadBatterySnapshotAsync() => Task.FromResult(
        new TopBarBatterySnapshot(-1, TopBarBatteryStatus.Unknown, false, null, false, TopBarPowerMode.Unknown, false, 0));

    Task<bool> SetPowerModeAsync(TopBarPowerMode mode) => Task.FromResult(false);

    Task<bool> SetBuiltInBrightnessAsync(int percent) => Task.FromResult(false);

    Task<TopBarNetworkSnapshot> ReadNetworkSnapshotAsync() => Task.FromResult(TopBarNetworkSnapshot.Disconnected);

    Task<bool> ConnectSavedWifiAsync(Guid interfaceGuid, string profileName) => Task.FromResult(false);

    Task<bool> DisconnectWifiAsync(Guid interfaceGuid) => Task.FromResult(false);

    Task<bool> TriggerWifiScanAsync(Guid interfaceGuid) => Task.FromResult(false);

    void OpenInputMethod();

    IReadOnlyList<TopBarInputMethodOption> ReadInputMethodOptions();

    bool ActivateInputMethod(TopBarInputMethodOption option, nint foregroundWindow);

    void OpenTrayOverflow();

    void CloseTrayOverflow();

    /// <summary>Opens Task Manager and lands on its performance page.</summary>
    void OpenTaskManagerPerformance();

    void OpenComponentSettings(DesktopComponentKind kind);

    /// <summary>Brings one window of an app that asked for attention to the front.</summary>
    bool ActivateWindow(nint handle, uint processId, nint monitorHandle);

    bool IsLauncherEnabled { get; }

    /// <summary>Whether file results may come from the running Everything instance.</summary>
    bool IsEverythingSearchEnabled { get; }

    Task<LauncherPanelSnapshot?> ReadSearchSnapshotAsync() => Task.FromResult<LauncherPanelSnapshot?>(null);

    void OpenSearchEntry(LauncherPanelEntry entry) { }

    void ToggleLauncherPanel();

    void ExitApp();
}

/// <summary>
/// The list the to-do flyout shows: the first card's title and its live items. The
/// items are the card's own state objects, so toggling one in the flyout reaches the
/// card and the persistence through the same objects.
/// </summary>
internal sealed record TopBarTodoList(
    string Title,
    IReadOnlyList<TodoItemState> Items,
    bool CarryOverIncomplete = false);

/// <summary>
/// The full-width strip at the top edge of one monitor. It only shows the app's own data
/// and read-only system status; clicking a module opens the matching settings page, and
/// an attention mark focuses that app. It never takes focus itself.
/// </summary>
public sealed partial class TopBarWindow : Window
{
    private static readonly TimeSpan SmartHideCheckInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan FullScreenCheckInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BackdropSampleInterval = TimeSpan.FromMilliseconds(1500);
    private const int SystemInfoTickDivisor = 5;
    private const double CapsuleWidth = 176d;
    private double _capsuleCompactWidth = CapsuleWidth;
    private const double CapsuleHeight = 40d;
    private const double CapsuleExpandedWidth = 352d;
    private bool _capsuleExpanded;
    private double _capsuleExpandedHeight;
    private int _capsuleMorphGeneration;
    private TextBlock? _capsuleSummary;
    private TextBlock? _capsuleStatus;
    private TextBlock? _capsuleChevron;
    private TopBarDisplayMode? _appliedDisplayMode;
    private readonly bool _enableExperimentalCapsule;
    private TopBarDisplayMode DisplayMode => _enableExperimentalCapsule && !_state.ShowAtBottom
        ? _state.DisplayMode : TopBarDisplayMode.FullWidth;
    private bool IsCapsule => DisplayMode == TopBarDisplayMode.Capsule;
    private DockEdge BarEdge => _state.ShowAtBottom ? DockEdge.Bottom : DockEdge.Top;
    private PlacementMode InwardPlacement => _state.ShowAtBottom ? PlacementMode.Top : PlacementMode.Bottom;
    private double InwardFlyoutOffset => _state.ShowAtBottom ? -4 : 4;
    private long _lastCountdownMinute = -1;

    private readonly TopBarState _state;
    private readonly DesktopStyleState _style;
    private readonly ITopBarEnvironment _environment;
    private readonly IPerformanceSamplingService _sampler;
    private SurfaceMonitorSelection? _screenTarget;
    private bool _ownsGlobalEvents;
    private double BarHeight => _screenTarget?.Preferences?.TopBarHeight ?? _state.Height;
    private TopBarVisibilityMode VisibilityMode => _screenTarget?.Preferences?.TopBarVisibilityMode ?? _state.VisibilityMode;
    private readonly StackPanel _attentionPanel = new()
    {
        Orientation = System.Windows.Controls.Orientation.Horizontal
    };
    private DockAutoHideState _autoHide = new();
    private readonly TopBarAttentionQueue _attention = new();
    private readonly Dictionary<nint, (string Name, string? ExecutablePath)> _attentionMeta = [];
    private readonly Dictionary<string, ImageSource?> _attentionIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _attentionIconsLoading = new(StringComparer.OrdinalIgnoreCase);
    private readonly WindowCatalog _catalog = new();
    private readonly MaximizeStateWatcher _maximizeWatcher;
    private readonly DispatcherTimer _foregroundTimer;
    private readonly DispatcherTimer _ambientTimer;
    private readonly DispatcherTimer _minuteTimer;
    private readonly DispatcherTimer _backdropTimer;
    private int _backdropSampleInFlight;
    private ColorContrastRules.SrgbColor? _sampledBackdrop;
    private bool? _lastAdaptiveDarkInk;
    private TopBarAdaptivePalette _palette = BarAdaptiveInkRules.ResolveTopBarPalette(
        DesktopStyleRules.TopBar(new DesktopStyleState(), new TopBarState()),
        hasAcrylicBackdrop: true,
        sampledBackdrop: null,
        previousUseDarkInk: null);
    private readonly Dictionary<TopBarModuleKind, FrameworkElement> _modules = [];
    private TopBarAppBarController? _appBar;
    private PerformanceSnapshot? _lastSnapshot;
    private TopBarSystemFacts? _facts;
    private Popup? _todoFlyout;
    private Popup? _clockFlyout;
    private Popup? _inputMethodFlyout;
    private long? _flyoutPointerLeftSinceTick;
    private nint _flyoutOpenedForegroundWindow;
    private Popup? _peripheralFlyout;
    private PeripheralBatterySnapshot? _peripheralFlyoutSnapshot;
    private StackPanel? _peripheralRoot;
    private string? _peripheralSignature;
    private DateTime _clockMonth;
    private MonitorSnapshot? _monitor;
    private TaskbarEdgeGuard.NativeTaskbarLayout? _nativeTaskbar;
    private nint _handle;
    private uint _shellHookMessage;
    private bool _shellHookActive;
    private readonly HashSet<nint> _observedTopWindows = [];
    private readonly DispatcherTimer _nudgeRecheckTimer;
    private bool _observedWindowsSeeded;
    private bool _closing;
    private bool _isChangingVisibilityByApp;
    private bool _trueFullScreenForeground;
    private bool _menuOpen;
    private NonActivatingPopupMenu? _barMenu;
    private int _tick;
    private TextBlock? _inputMethodBadgeText;
    private TextBlock? _inputMethodNameText;
    private nint _lastInputLayout;
    private nint _lastInputForeground;
    private long _lastInputProfileCheckAt;
    private string? _inputMethodName;
    private string _inputMethodFullName = string.Empty;
    private string _inputMethodToolTip = string.Empty;
    private string _inputMethodLabel = string.Empty;
    private int _imeReadInFlight;
    private IDisposable? _inputMethodSubscription;
    private nint _inputMethodTargetForeground;

    // ---------------------------------------------------------------- 搜索

    private const string GlyphSearch = "\uE721";
    private const double SearchBoxWidth = TopBarSearchRules.DefaultCenteredEntryWidth;
    private const int SearchHotkeyId = 0x5E41;

    private Button? _searchButton;
    private TopBarSearchWindow? _searchPopup;
    private long _searchPopupOpenedTick;
    private long? _searchPopupPointerLeftSinceTick;
    private nint _searchPopupOpenedForegroundWindow;
    private bool _searchPopupObservedForeground;
    private long? _revealZoneEnteredTick;
    private bool _smartHideOverlappingWindow;
    private int _moduleMask = -1;
    private string _orderSignature = string.Empty;
    private DockGlassSurface? _capsuleGlass;
    private bool _searchHotkeyRegistered;

    private bool IsSearchEntryEnabled =>
        TopBarRules.IsModuleOn(_state.Modules, TopBarModuleKind.Search)
        && (_state.SearchInline == true || _environment.IsLauncherEnabled);

    private const int HshellWindowDestroyed = 2;
    private const int HshellWindowActivated = 4;
    private const int HshellRudeAppActivated = 0x8004;
    private const int HshellFlash = 0x8006;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;

    internal TopBarWindow(
        TopBarState state,
        DesktopStyleState style,
        ITopBarEnvironment environment,
        IPerformanceSamplingService sampler,
        bool enableExperimentalCapsule = false,
        SurfaceMonitorSelection? screenTarget = null,
        bool ownsGlobalEvents = true)
    {
        _state = state;
        _style = style;
        _environment = environment;
        _sampler = sampler;
        _enableExperimentalCapsule = enableExperimentalCapsule;
        _screenTarget = screenTarget;
        _ownsGlobalEvents = ownsGlobalEvents;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        _maximizeWatcher = new MaximizeStateWatcher(Dispatcher);
        _maximizeWatcher.TopLevelStateChanged += () =>
        {
            if (!_closing)
            {
                UpdateForForeground();
            }
        };
        _foregroundTimer = new DispatcherTimer(
            FullScreenCheckInterval,
            DispatcherPriority.Background,
            (_, _) => UpdateForForeground(),
            Dispatcher);
        _ambientTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(5),
            DispatcherPriority.Background,
            (_, _) => OnAmbientTick(),
            Dispatcher);
        _minuteTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _minuteTimer.Tick += (_, _) => OnMinuteTick();
        _backdropTimer = new DispatcherTimer(
            BackdropSampleInterval,
            DispatcherPriority.Background,
            (_, _) => ScheduleBackdropSample(),
            Dispatcher);
        _backdropTimer.Stop();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) CloseTrayPanel();
            UpdateBackdropSampling(immediate: IsVisible);
        };
        AccessibilityThemeManager.HighContrastChanged += OnHighContrastChanged;
        _nudgeRecheckTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _nudgeRecheckTimer.Tick += (_, _) =>
        {
            _nudgeRecheckTimer.Stop();
            if (!_closing)
            {
                UpdateForForeground();
            }
        };
        _sampler.Sampled += OnSampled;
        // A network address change is the one system fact with an event source; the
        // others ride the sampling or ambient tick.
        if (_ownsGlobalEvents)
        {
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            SystemEvents.TimeChanged += OnSystemTimeChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
        // The centred search box must not sit on top of the side modules when they
        // leave little room: every size change on the bar or either module group
        // re-clamps the box width around the true centre.
        Surface.SizeChanged += (_, _) => UpdateSearchHostWidth();
        LeftModules.SizeChanged += (_, _) => UpdateSearchHostWidth();
        RightModules.SizeChanged += (_, _) => UpdateSearchHostWidth();
        BuildContextMenu();
    }

    /// <summary>
    /// Re-reads the state: which monitor, which display mode, which modules, what look.
    /// Called whenever the settings or the desktop style change.
    /// </summary>
    internal void ApplyState()
    {
        if (_closing)
        {
            return;
        }

        _monitor = ResolveMonitor();
        if (_monitor is null)
        {
            Close();
            return;
        }

        var moduleMask = TopBarRules.ModuleOrder.Aggregate(0,
            (mask, kind) =>
            {
                var enabled = kind == TopBarModuleKind.Search
                    ? IsSearchEntryEnabled
                    : TopBarRules.IsModuleOn(_state.Modules, kind);
                return mask | (enabled ? 1 << (int)kind : 0);
            });
        var layoutSignature = TopBarRules.LayoutSignature(_state);
        if (_moduleMask != moduleMask || _appliedDisplayMode != DisplayMode || _orderSignature != layoutSignature)
        {
            BuildModules();
            _moduleMask = moduleMask;
            _orderSignature = layoutSignature;
            _appliedDisplayMode = DisplayMode;
        }
        if (_searchButton is { } searchButton)
        {
            if (TopBarRules.EffectiveSearchPlacement(_state) == TopBarSearchPlacement.CenterBar)
            {
                searchButton.Height = Math.Clamp(BarHeight - 10d, 18d, 30d);
            }
            SetModuleGlassToolTip(
                searchButton,
                string.IsNullOrWhiteSpace(_state.SearchHotkey)
                    ? "搜索应用、文件"
                    : $"搜索应用、文件（{_state.SearchHotkey}）");
        }
        var wasReserved = _appBar?.IsReserved == true;
        _nativeTaskbar = TaskbarEdgeGuard.GetNativeTaskbarLayout(_monitor.Bounds);
        // The top edge is taken (a taskbar moved to the top on Windows 10): behave as
        // smart-hide instead of fighting over the space. A bar that already holds the
        // edge keeps it — its own reservation is not a foreign occupier.
        var requestsReservation = !IsCapsule && VisibilityMode == TopBarVisibilityMode.ReserveTopEdge;
        var reserve = _state.ShowAtBottom ? requestsReservation : TopBarReserveRules.ShouldReserve(
            requestsReservation,
            _appBar is { IsReserved: true, Edge: DockEdge.Top },
            _monitor.Bounds,
            DockAppBarController.GetLiveWorkingArea(_monitor),
            nativeTopEdgeOccupied: _nativeTaskbar?.Edge == DockEdge.Top);
        if (reserve || wasReserved)
        {
            _autoHide = new DockAutoHideState();
            _smartHideOverlappingWindow = false;
            _revealZoneEnteredTick = null;
            _sampledBackdrop = null;
            _lastAdaptiveDarkInk = null;
        }
        RefreshPopupAppearance();
        ApplySurface(retryFailedAcrylic: true);
        var previousBounds = _appBar?.Bounds;
        _appBar?.Apply(_monitor, reserve,
            IsCapsule ? (_capsuleExpanded ? _capsuleExpandedHeight : CapsuleHeight) : BarHeight,
            IsCapsule ? (_capsuleExpanded ? CapsuleExpandedWidth : _capsuleCompactWidth) : null, BarEdge);
        if (previousBounds is { } before && _appBar is { } afterBar && before != afterBar.Bounds)
        {
            CloseFlyoutsBeforeOpening();
            CloseTrayPanel();
        }
        // While a flyout is open the fast interval keeps its outside-click watch
        // prompt even in reserve mode.
        UpdateForegroundTimerInterval();
        _foregroundTimer.Start();
        _ambientTimer.Start();
        ArmMinuteTimer();
        SyncSearchHotkey();
        RenderModules();
        RefreshSystemFacts();
        UpdateBackdropSampling(immediate: true);
        if (!reserve && _appBar is not null)
        {
            UpdateForForeground();
        }
    }

    internal void ApplyScreenTarget(SurfaceMonitorSelection target)
    {
        _screenTarget = target;
        ApplyState();
    }

    internal int? GetReservedBottomForMonitor(MonitorSnapshot monitor) =>
        !_closing && _appBar is { IsReserved: true, Edge: DockEdge.Top } bar
            && bar.Monitor?.MonitorHandle == monitor.MonitorHandle
            && bar.Bounds.Bottom > monitor.Bounds.Top
            && TopBarReserveRules.IsWorkAreaReserved(DockAppBarController.GetLiveWorkingArea(monitor), bar.Bounds)
            ? bar.Bounds.Bottom
            : null;

    private MonitorSnapshot? ResolveMonitor() => _screenTarget is { } target
        ? MonitorCatalog.GetAll().FirstOrDefault(monitor => monitor.MonitorHandle == target.Monitor.MonitorHandle
            && string.Equals(monitor.DeviceName, target.Monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
        : SurfaceMonitorTargetResolver.Resolve(_state.MonitorKey);

    private void OnEnvironmentChanged()
    {
        _lastRequestedAcrylicTint = null;
        if (EnvironmentChanged is { } changed) changed();
        else ApplyState();
    }

    internal void SetGlobalEventOwner(bool enabled)
    {
        if (_ownsGlobalEvents == enabled) return;
        _ownsGlobalEvents = enabled;
        if (enabled)
        {
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            SystemEvents.TimeChanged += OnSystemTimeChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            RegisterShellHook();
            SubscribeInputMethodChanges();
            RefreshSystemFacts();
        }
        else
        {
            UnregisterSearchHotkey();
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            SystemEvents.TimeChanged -= OnSystemTimeChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _inputMethodSubscription?.Dispose();
            _inputMethodSubscription = null;
            if (_shellHookActive && _handle != 0)
            {
                DockNativeMethods.DeregisterShellHookWindow(_handle);
                HwndSource.FromHwnd(_handle)?.RemoveHook(OnShellHook);
                _shellHookActive = false;
            }
        }
    }

    private void RegisterShellHook()
    {
        if (_handle == 0 || _shellHookActive || !_ownsGlobalEvents || _closing) return;
        _shellHookMessage = DockNativeMethods.RegisterWindowMessageW("SHELLHOOK");
        if (DockNativeMethods.RegisterShellHookWindow(_handle))
        {
            _shellHookActive = true;
            HwndSource.FromHwnd(_handle)?.AddHook(OnShellHook);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        var style = (long)NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle);
        NativeMethods.SetWindowLongPtr(
            _handle,
            NativeMethods.GwlExStyle,
            (nint)(style | NativeMethods.WsExToolWindow | DockNativeMethods.WsExNoActivate));
        var cornerPreference = NativeMethods.DwmWindowCornerDoNotRound;
        NativeMethods.DwmSetWindowAttribute(
            _handle,
            NativeMethods.DwmwaWindowCornerPreference,
            ref cornerPreference,
            sizeof(int));
        HwndSource.FromHwnd(_handle)?.AddHook(PreventActivation);
        HwndSource.FromHwnd(_handle)?.AddHook(OnHotkeyMessage);
        HwndSource.FromHwnd(_handle)?.AddHook(OnCompositionMessage);

        RegisterShellHook();

        _appBar = new TopBarAppBarController(this, BarHeight) { ResolveBottomDockTop = ResolveBottomDockTop };
        _appBar.EnvironmentChanged += OnEnvironmentChanged;
        _appBar.ExplorerNotified += () => ExplorerNotified?.Invoke();
        _appBar.ExplorerRestarted += OnExplorerRestarted;
        _appBar.ReservationFallbackTriggered += UpdateForForeground;
        SubscribeInputMethodChanges();
        _maximizeWatcher.Start();
        ApplyState();
    }

    internal event Action? ShellRecovered;

    internal event Action? ExplorerNotified;

    internal event Action? EnvironmentChanged;

    internal Func<MonitorSnapshot, int?>? ResolveBottomDockTop { get; init; }

    internal event Action<int, nint>? ShellEventReceived;

    internal event Action<TopBarSystemFacts>? SystemFactsChanged;

    internal event Action? InputMethodChanged;

    internal Action? SearchRequested { get; set; }

    private void SubscribeInputMethodChanges()
    {
        _inputMethodSubscription?.Dispose();
        _inputMethodSubscription = _ownsGlobalEvents ? TopBarSystemInfo.SubscribeInputMethodChanges(() =>
            Dispatcher.BeginInvoke(() =>
            {
                if (_closing) return;
                RefreshInputMethod();
                InputMethodChanged?.Invoke();
            })) : null;
    }

    internal void RefreshInputMethod()
    {
        if (_closing || !_modules.ContainsKey(TopBarModuleKind.InputMethod)) return;
        RefreshActiveInputMethodName();
        PollInputMethod(forceProfileRefresh: true);
    }

    internal IEnumerable<nint> ActiveDesktopHandles()
    {
        if (!_closing
            && IsVisible
            && !_trueFullScreenForeground
            && _appBar is not { IsFullScreenDetected: true }
            && _handle != 0)
        {
            yield return _handle;
        }
    }

    internal void SetDesktopShown(bool shown, nint cardSlot, bool topmost)
    {
        if (_closing || !shown)
        {
            return;
        }

        if (IsVisible
            && !_trueFullScreenForeground
            && _appBar is not { IsFullScreenDetected: true }
            && _handle != 0)
        {
            if (NativeMethods.IsIconic(_handle) || !NativeMethods.IsWindowVisible(_handle))
            {
                NativeMethods.ShowWindow(_handle, NativeMethods.SwShowNoActivate);
            }
            _appBar?.EnsureLayer();
        }
    }

    private void HideFromApp()
    {
        _backdropTimer.Stop();
        _isChangingVisibilityByApp = true;
        try
        {
            Hide();
            _appBar?.EnsureLayer();
        }
        finally
        {
            _isChangingVisibilityByApp = false;
        }
    }

    private nint PreventActivation(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmMouseActivate)
        {
            handled = true;
            return MaNoActivate;
        }

        if (!_closing && !_isChangingVisibilityByApp)
        {
            if (message == NativeMethods.WmSize && wParam == NativeMethods.SizeMinimized)
            {
                RecoverFromShell(minimized: true);
            }
            else if (message == NativeMethods.WmShowWindow && wParam == 0)
            {
                RecoverFromShell(minimized: false);
            }
            else if (message == NativeMethods.WmWindowPosChanged
                     && (Marshal.PtrToStructure<NativeMethods.WindowPos>(lParam).Flags
                         & NativeMethods.SwpHideWindow) != 0)
            {
                RecoverFromShell(minimized: false);
            }
        }
        return 0;
    }

    private void RecoverFromShell(bool minimized)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Send,
            new Action(() =>
            {
                if (_closing || _handle == 0 || !IsVisible)
                {
                    return;
                }
                if (minimized ? !NativeMethods.IsIconic(_handle) : NativeMethods.IsWindowVisible(_handle))
                {
                    return;
                }
                NativeMethods.ShowWindow(_handle, NativeMethods.SwShowNoActivate);
                _appBar?.EnsureLayer();
                ShellRecovered?.Invoke();
            }));
    }

    private nint OnShellHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_shellHookMessage == 0 || (uint)message != _shellHookMessage)
        {
            return 0;
        }

        var shellEvent = (int)wParam;
        HandleShellEvent(shellEvent, lParam);
        ShellEventReceived?.Invoke(shellEvent, lParam);
        return 0;
    }

    internal void HandleShellEvent(int shellEvent, nint window)
    {
        if (_closing) return;
        var changed = shellEvent switch
        {
            HshellFlash => MarkFlashed(window),
            HshellWindowActivated or HshellRudeAppActivated => ClearActivatedAttention(window),
            HshellWindowDestroyed => _attention.Closed(window),
            _ => false
        };
        if (shellEvent == HshellWindowDestroyed && window != 0)
        {
            _observedTopWindows.Remove(window);
        }
        else if (shellEvent is HshellWindowActivated or HshellRudeAppActivated)
        {
            // React immediately when a new or overlapping window activates instead of waiting
            // for the 500 ms foreground poll.
            Dispatcher.BeginInvoke(UpdateForForeground, DispatcherPriority.Input);
        }
        if (changed)
        {
            RefreshAttention();
        }
    }

    private bool MarkFlashed(nint window)
    {
        if (window == 0 || !DockNativeMethods.IsWindow(window))
        {
            return false;
        }

        // A flash often names a hidden message window; the process lets the bar show
        // the app's name, coalesce duplicate flashes, and clear once the app activates.
        DockNativeMethods.GetWindowThreadProcessId(window, out var rawProcessId);
        var executablePath = rawProcessId != 0 ? WindowCatalog.ExecutablePathOf(rawProcessId) : null;
        var className = new StringBuilder(64);
        DockNativeMethods.GetClassNameW(window, className, className.Capacity);
        var foreground = DockNativeMethods.GetForegroundWindow();
        var foregroundMinimized = foreground != 0 && DockNativeMethods.IsIconic(foreground);
        if (!AttentionFlashRules.ShouldRecordFlash(
                window,
                isLiveWindow: true,
                rawProcessId,
                (uint)Environment.ProcessId,
                executablePath,
                className.ToString(),
                foreground,
                EffectiveAttentionProcessId(foreground),
                foregroundMinimized))
        {
            return false;
        }

        return _attention.Flashed(
            window,
            AttentionFlashRules.EffectiveProcessId(rawProcessId, executablePath));
    }

    private bool ClearActivatedAttention(nint window)
    {
        var active = window != 0 ? window : DockNativeMethods.GetForegroundWindow();
        return active != 0 && _attention.Cleared(active, EffectiveAttentionProcessId(active));
    }

    private static uint EffectiveAttentionProcessId(nint window)
    {
        if (window == 0)
        {
            return 0;
        }
        DockNativeMethods.GetWindowThreadProcessId(window, out var processId);
        return processId == 0
            ? 0
            : AttentionFlashRules.EffectiveProcessId(processId, WindowCatalog.ExecutablePathOf(processId));
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        SetGlobalEventOwner(false);
        if (_barMenu is { } barMenu)
        {
            barMenu.IsOpen = false;
            _barMenu = null;
        }
        _environment.CloseTrayOverflow();
        UnregisterSearchHotkey();
        CloseAllOpenFlyouts(animateTodo: false);

        if (_searchPopup is { } searchPopup)
        {
            _searchPopup = null;
            searchPopup.RequestClose();
        }
        _foregroundTimer.Stop();
        _ambientTimer.Stop();
        _minuteTimer.Stop();
        _backdropTimer.Stop();
        _nudgeRecheckTimer.Stop();
        _maximizeWatcher.Dispose();
        AccessibilityThemeManager.HighContrastChanged -= OnHighContrastChanged;
        _sampler.Sampled -= OnSampled;
        _appBar?.Dispose();
        _appBar = null;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => RefreshSystemFacts();

    private void OnSystemTimeChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing) return;
            ArmMinuteTimer();
            if (IsLoaded && IsVisible) RenderModules();
        });

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing) return;
            ArmMinuteTimer();
            RefreshSystemFacts();
            _environment.RefreshWeatherIfDue();
            if (IsLoaded && IsVisible) RenderModules();
        });
    }

    private void ArmMinuteTimer()
    {
        var now = DateTime.Now;
        var msToNextMinute = Math.Clamp(60_000 - ((now.Second * 1000) + now.Millisecond) + 50, 250, 60_050);
        _minuteTimer.Interval = TimeSpan.FromMilliseconds(msToNextMinute);
        _minuteTimer.Start();
    }

    private void OnMinuteTick()
    {
        if (_closing) return;
        ArmMinuteTimer();
        if (!_sampler.IsRunning && IsLoaded && IsVisible)
        {
            RenderModules();
        }
    }

    private void OnAmbientTick()
    {
        if (_closing || !IsLoaded)
        {
            return;
        }

        if (IsVisible && _peripheralRoot is not null)
        {
            _environment.RefreshPeripheralBatteryIfDue();
        }
        if (_sampler.IsRunning)
        {
            return;
        }

        // The owner can be smart-hidden while a peer remains visible on another screen.
        if (_ownsGlobalEvents)
        {
            RefreshSystemFacts();
            _environment.RefreshWeatherIfDue();
        }
        if (IsVisible) RenderModules();
    }

    private void OnSampled(PerformanceSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        // System facts are read off the UI thread: network enumeration and the volume COM
        // call are not free. The tick counter keeps them on their slower cadence.
        TopBarSystemFacts? facts = null;
        if (_ownsGlobalEvents && Interlocked.Increment(ref _tick) % SystemInfoTickDivisor == 0)
        {
            facts = ReadSystemFacts();
        }
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing) return;
            if (_ownsGlobalEvents)
            {
                if (facts is not null)
                {
                    ApplySharedSystemFacts(facts);
                    SystemFactsChanged?.Invoke(facts);
                }
                _environment.RefreshWeatherIfDue();
            }
            // A smart-hidden bar is not on screen; its projection waits until it shows.
            if (!IsLoaded || !IsVisible || _closing)
            {
                return;
            }
            RenderModules();
        });
    }

    private static TopBarSystemFacts ReadSystemFacts() => new(
        TopBarSystemInfo.ReadBattery(),
        TopBarSystemInfo.ReadVolume(),
        TopBarSystemInfo.ReadNetwork());

    private void RefreshSystemFacts()
    {
        if (_closing || !_ownsGlobalEvents)
        {
            return;
        }

        // The reads leave the UI thread: enumerating interfaces and the volume COM call
        // are not free, and ApplyState can run on every display or settings change.
        Task.Run(() =>
        {
            if (_closing || !_ownsGlobalEvents)
            {
                return;
            }

            var facts = ReadSystemFacts();
            Dispatcher.BeginInvoke(() =>
            {
                if (_closing || !_ownsGlobalEvents)
                {
                    return;
                }
                ApplySharedSystemFacts(facts);
                SystemFactsChanged?.Invoke(facts);
            });
        });
    }

    // The input method leaves the background facts cadence: its TSF name read needs the
    // UI thread (STA), so the whole module rides the fast focus tick.
    internal void ApplySharedSystemFacts(TopBarSystemFacts facts)
    {
        if (_closing) return;
        _facts = facts;
        if (IsLoaded && IsVisible) RenderSystemModules();
    }

    internal sealed record TopBarSystemFacts(
        (int Percent, TopBarBatteryStatus Status) Battery,
        (int Percent, bool Muted)? Volume,
        TopBarNetworkStatus Network);

    // ---------------------------------------------------------------- modules

    // Windows' own status-bar glyph font: Segoe Fluent Icons on Windows 11, with the
    // older Segoe MDL2 Assets as the fallback for the same codepoints.
    private static readonly System.Windows.Media.FontFamily StatusIconFont =
        new("Segoe Fluent Icons, Segoe MDL2 Assets");

    // Marks the glyph TextBlock inside a module's content so the value text can be
    // told apart when reading a module's state back.
    private const string IconTag = "topbar-icon";

    private const string GlyphCheckMark = "\uE73E";
    private const string GlyphLocation = "\uE707";
    private const string GlyphDiagnostic = "\uE9D9";
    private const string GlyphWifi = "\uE701";
    private const string GlyphWifi1 = "\uE872";
    private const string GlyphWifi2 = "\uE873";
    private const string GlyphWifi3 = "\uE874";
    private const string GlyphEthernet = "\uE839";
    private const string GlyphWifiOff = "\uE871";
    private const string GlyphVolume = "\uE767";
    private const string GlyphVolume0 = "\uE992";
    private const string GlyphVolumeMuted = "\uE74F";
    private const string GlyphVolume1 = "\uE993";
    private const string GlyphVolume2 = "\uE994";
    private const string GlyphVolume3 = "\uE995";
    private const string GlyphBatteryUnknown = "\uE996";

    // Battery0..Battery10 sit at E850..E859 plus E83F; the charging variants follow the
    // same order from E85A, with BatteryCharging10 at EA93.
    private static string BatteryGlyph(int percent, bool charging)
    {
        var level = Math.Clamp(percent / 10, 0, 10);
        if (level == 10)
        {
            return charging ? "\uEA93" : "\uE83F";
        }
        return Convert.ToChar((charging ? 0xE85A : 0xE850) + level).ToString();
    }

    private void BuildModules()
    {
        if (_attentionPanel.Parent is Panel attentionParent)
        {
            attentionParent.Children.Remove(_attentionPanel);
        }
        LeftModules.Children.Clear();
        RightModules.Children.Clear();
        _peripheralRoot = null;
        _peripheralSignature = null;
        CenterHost.Children.Clear();
        _modules.Clear();
        _searchButton = null;
        CloseTodoFlyout(animate: false);
        CloseClockFlyout();
        CloseInputMethodFlyout();
        CloseSystemFlyouts();
        ClosePeripheralBatteryFlyout();

        CapsuleHeader.Children.Clear();
        CapsuleHeader.Visibility = IsCapsule ? Visibility.Visible : Visibility.Collapsed;
        ModuleHeader.Visibility = IsCapsule ? Visibility.Collapsed : Visibility.Visible;
        _capsuleSummary = null;
        _capsuleStatus = null;
        _capsuleChevron = null;

        if (TopBarRules.EffectiveSearchPlacement(_state) == TopBarSearchPlacement.CenterBar)
        {
            AddSearchModule();
        }

        CenterHost.HorizontalAlignment = HorizontalAlignment.Center;
        Surface.Padding = new Thickness(0);
        if (IsCapsule)
        {
            _environment.CloseTrayOverflow();
            CenterHost.Visibility = Visibility.Collapsed;
            CapsuleHeader.Height = CapsuleHeight - 2;
            var content = new Grid { Margin = new Thickness(6, 0, 6, 0) };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            var markGlyph = new TextBlock
            {
                Text = "\uE73E", FontFamily = StatusIconFont, FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            markGlyph.SetResourceReference(TextBlock.ForegroundProperty, "FloatingGlassInkBrush");
            var mark = new Border
            {
                Width = 23, Height = 23, CornerRadius = new CornerRadius(8),
                VerticalAlignment = VerticalAlignment.Center, Child = markGlyph
            };
            mark.SetResourceReference(Border.BackgroundProperty, "FloatingGlassSelectedBrush");
            content.Children.Add(mark);
            var text = CreateValueText();
            text.FontSize = 12;
            text.SetResourceReference(TextBlock.ForegroundProperty, "FloatingGlassInkBrush");
            _capsuleSummary = text;
            Grid.SetColumn(text, 1);
            content.Children.Add(text);
            _capsuleChevron = new TextBlock
            {
                Text = "\uE70D", FontFamily = StatusIconFont, FontSize = 9,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            _capsuleChevron.SetResourceReference(TextBlock.ForegroundProperty, "FloatingGlassMutedBrush");
            Grid.SetColumn(_capsuleChevron, 2);
            content.Children.Add(_capsuleChevron);
            var summary = new Button
            {
                Style = (Style)Resources["ModuleButton"],
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                MinHeight = CapsuleHeight - 2
            };
            SetModuleGlassToolTip(summary, "今日待办，点击查看列表");
            AutomationProperties.SetName(summary, "今日待办概要，点击查看列表");
            summary.Click += (_, _) => ToggleTodoFlyout(summary);
            CapsuleHeader.Children.Add(summary);
            _modules[TopBarModuleKind.TodoSummary] = summary;
            RenderModules();
            return;
        }
        CenterHost.Visibility = Visibility.Visible;

        if (TrayOverflowController.IsSupported && !_state.Modules.TrayOverflow)
        {
            _environment.CloseTrayOverflow();
        }

        var (leftOrder, rightOrder) = TopBarRules.ResolveOrder(_state);
        foreach (var kind in leftOrder)
        {
            AddOrderedModule(kind, LeftModules);
        }

        foreach (var kind in rightOrder)
        {
            AddOrderedModule(kind, RightModules);
        }

        RefreshAttention();
    }

    private void AddOrderedModule(TopBarModuleKind kind, StackPanel panel)
    {
        switch (kind)
        {
            case TopBarModuleKind.TodoSummary:
                // The to-do module shows its own flyout instead of opening settings.
                AddTodoModule(panel);
                break;
            case TopBarModuleKind.Weather:
                // The weather is a glance with hover details; it does nothing on click.
                AddModule(TopBarModuleKind.Weather, panel, "天气", null, GlyphLocation);
                break;
            case TopBarModuleKind.Performance:
                AddModule(TopBarModuleKind.Performance, panel, "性能",
                    _environment.OpenTaskManagerPerformance, GlyphDiagnostic);
                break;
            case TopBarModuleKind.Battery:
                AddModule(TopBarModuleKind.Battery, panel, "电量", () =>
                {
                    if (_modules.TryGetValue(TopBarModuleKind.Battery, out var batteryEl)
                        && batteryEl is Button batteryBtn)
                    {
                        ToggleBatteryFlyout(batteryBtn);
                    }
                }, GlyphBatteryUnknown);
                break;
            case TopBarModuleKind.PeripheralBattery:
                // 点击显示外设电量明细。
                AddModule(TopBarModuleKind.PeripheralBattery, panel, "外设电量", () =>
                {
                    if (_modules.TryGetValue(TopBarModuleKind.PeripheralBattery, out var element)
                        && element is Button button)
                    {
                        TogglePeripheralBatteryFlyout(button);
                    }
                }, GlyphBatteryUnknown);
                // 每台设备各一个图标（各画各的电量格），不用通用模块的单图标结构：
                // 把按钮内容换成专属容器，由 RenderPeripheralModule 按设备集填充。
                if (_modules.TryGetValue(TopBarModuleKind.PeripheralBattery, out var peripheralElement)
                    && peripheralElement is Button peripheralButton)
                {
                    _peripheralRoot = new StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Horizontal,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    peripheralButton.Content = _peripheralRoot;
                    _peripheralSignature = null;
                }
                break;
            case TopBarModuleKind.Network:
                AddModule(TopBarModuleKind.Network, panel, "网络", () =>
                {
                    if (_modules.TryGetValue(TopBarModuleKind.Network, out var networkEl)
                        && networkEl is Button networkBtn)
                    {
                        ToggleNetworkFlyout(networkBtn);
                    }
                }, GlyphWifiOff);
                break;
            case TopBarModuleKind.Volume:
                AddModule(TopBarModuleKind.Volume, panel, "音量", () =>
                {
                    if (_modules.TryGetValue(TopBarModuleKind.Volume, out var volumeEl)
                        && volumeEl is Button volumeBtn)
                    {
                        ToggleVolumeFlyout(volumeBtn);
                    }
                }, GlyphVolume);
                if (_modules.TryGetValue(TopBarModuleKind.Volume, out var volumeControl)
                    && volumeControl is Button volumeButton)
                {
                    AttachVolumeQuickWheel(volumeButton);
                }
                break;
            case TopBarModuleKind.TrayOverflow:
                if (TrayOverflowController.IsSupported)
                {
                    AddTrayPanelModule(panel);
                }
                break;
            case TopBarModuleKind.Search:
                if (IsSearchEntryEnabled && TopBarRules.EffectiveSearchPlacement(_state) == TopBarSearchPlacement.SideIcon)
                {
                    AddSideSearchModule(panel);
                }
                break;
            case TopBarModuleKind.InputMethod:
                AddModule(TopBarModuleKind.InputMethod, panel, "输入法", () =>
                {
                    if (_modules.TryGetValue(TopBarModuleKind.InputMethod, out var element)
                        && element is Button button)
                    {
                        ToggleInputMethodFlyout(button);
                    }
                });
                StyleInputMethodModule();
                break;
            case TopBarModuleKind.Attention:
                if (TopBarRules.IsModuleOn(_state.Modules, TopBarModuleKind.Attention))
                {
                    panel.Children.Add(_attentionPanel);
                }
                break;
            case TopBarModuleKind.Clock:
                AddClockModule(panel);
                break;
        }
    }

    private void AddModule(TopBarModuleKind kind, StackPanel panel, string label, Action? click, string? glyph = null)
    {
        if (!TopBarRules.IsModuleOn(_state.Modules, kind))
        {
            return;
        }

        var text = CreateValueText();
        if (kind == TopBarModuleKind.Weather)
        {
            // Online geocoding can return a long display name; the bar shows the city
            // part only (RenderWeather trims it) and the tooltip carries the rest.
            text.MaxWidth = 240;
        }
        else if (kind == TopBarModuleKind.Performance)
        {
            var perfStyle = TopBarRules.EffectivePerformanceStyle(_state);
            text.MaxWidth = perfStyle switch
            {
                TopBarPerformanceStyle.CpuOnly => 96,
                TopBarPerformanceStyle.Compact => 200,
                _ => 360
            };
            // The upload/download digits change width every second; reserving the widest
            // steady-state readout keeps the module from resizing, which would shuffle
            // the centred search box left and right once per tick.
            text.MinWidth = perfStyle switch
            {
                TopBarPerformanceStyle.CpuOnly => Math.Min(96d, MeasureTextWidth("CPU 100%")),
                TopBarPerformanceStyle.Compact => Math.Min(200d, MeasureTextWidth("CPU 100%  ↓1023.9 MB/s")),
                _ => Math.Min(360d, MeasureTextWidth("CPU 100%  内存 100%  ↑1023.9 MB/s ↓1023.9 MB/s"))
            };
        }

        FrameworkElement content = glyph is null ? text : CreateIconContent(glyph, text);
        if (click is null)
        {
            // A display-only module keeps the same padding and tooltip, but no button:
            // UIA would otherwise announce a control that does nothing.
            text.VerticalAlignment = VerticalAlignment.Center;
            var block = new Border
            {
                Padding = new Thickness(8, 0, 8, 0),
                MinHeight = BarHeight,
                Child = content
            };
            System.Windows.Automation.AutomationProperties.SetName(block, $"{label}模块");
            if (kind == TopBarModuleKind.Weather)
            {
                // ToolTips never open on their own in this no-activate window, so the
                // hover details open manually after a rest delay, like the to-do flyout.
                // The panel uses the bar's own glass and white text: the system tooltip
                // style renders dark-on-dark on a dark desktop.
                var toolTip = CreateGlassToolTip();
                AttachHoverToolTip(block, toolTip);
                block.ToolTip = toolTip;
            }
            _modules[kind] = block;
            panel.Children.Add(block);
            return;
        }

        var button = new Button
        {
            Style = (Style)Resources["ModuleButton"],
            Content = content,
            Focusable = true
        };
        System.Windows.Automation.AutomationProperties.SetName(button, $"{label}模块");
        if (kind is TopBarModuleKind.Battery or TopBarModuleKind.Volume or TopBarModuleKind.Network
            or TopBarModuleKind.Performance or TopBarModuleKind.TrayOverflow)
        {
            var toolTip = CreateGlassToolTip();
            AttachHoverToolTip(button, toolTip);
            button.ToolTip = toolTip;
        }
        button.Click += (_, _) => click();
        _modules[kind] = button;
        panel.Children.Add(button);
    }

    /// <summary>The bar's value text, bound to the adaptive ink brush so contrast flips
    /// automatically over light surfaces or bright wallpapers without losing ClearType.</summary>
    private static TextBlock CreateValueText()
    {
        var text = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TopBarInkBrush");
        return text;
    }

    /// <summary>Measures one line of text in the bar's own typeface, in DIU.</summary>
    private double MeasureTextWidth(string value)
    {
        var measured = new FormattedText(
            value,
            System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
            FontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return measured.Width;
    }

    /// <summary>A status glyph from Windows' icon font followed by the value text.</summary>
    private static StackPanel CreateIconContent(string glyph, TextBlock value)
    {
        var icon = new TextBlock
        {
            FontFamily = StatusIconFont,
            FontSize = 14,
            Text = glyph,
            Tag = IconTag,
            Opacity = 0.92,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 1)
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "TopBarInkBrush");
        return new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { icon, value }
        };
    }

    /// <summary>
    /// The input method module is a display-only element wearing the taskbar indicator's
    /// shape: the mode badge in a rounded tile, followed by the input method's own name
    /// ("微信输入法"). The name collapses for plain keyboard layouts; the full layout
    /// name lives on the tooltip.
    /// </summary>
    private void StyleInputMethodModule()
    {
        if (!_modules.TryGetValue(TopBarModuleKind.InputMethod, out var module)
            || module is not Button { Content: TextBlock badgeText } block)
        {
            return;
        }

        block.Padding = new Thickness(2, 0, 2, 0);
        // Detach the existing content before placing the badge inside its layout panel.
        block.Content = null;
        var nameText = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 120,
            Margin = new Thickness(4, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "TopBarInkBrush");
        var badgeBorder = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 1, 6, 1),
            Child = badgeText
        };
        badgeBorder.SetResourceReference(Border.BackgroundProperty, "TopBarBadgeBackgroundBrush");
        block.Content = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Children = { badgeBorder, nameText }
        };
        _inputMethodBadgeText = badgeText;
        _inputMethodNameText = nameText;
        var toolTip = CreateGlassToolTip();
        AttachHoverToolTip(block, toolTip);
        block.ToolTip = toolTip;
        ApplyInputMethodSnapshot(TopBarSystemInfo.ReadInputMethod());
    }

    private const long InputProfileRefreshIntervalMs = 1000;

    /// <summary>
    /// Re-reads the input method on the fast focus tick off the UI thread so a busy or
    /// hung foreground IME window cannot stall the bar; the TSF name also refreshes on TSF
    /// activation notifications, foreground switches and a 1 s cadence so same-HKL switches
    /// between Chinese IMEs (e.g. Microsoft Pinyin ↔ WeChat/Sogou IME) never get stuck.
    /// </summary>
    private void PollInputMethod(bool forceProfileRefresh = false)
    {
        if (Interlocked.CompareExchange(ref _imeReadInFlight, 1, 0) != 0)
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                if (_closing) return;
                var snapshot = TopBarSystemInfo.ReadInputMethod();
                if (_closing) return;
                var foreground = DockNativeMethods.GetForegroundWindow();
                var now = Environment.TickCount64;
                var shouldRefreshProfile = forceProfileRefresh
                    || snapshot.Layout != _lastInputLayout
                    || foreground != _lastInputForeground
                    || now - Interlocked.Read(ref _lastInputProfileCheckAt) >= InputProfileRefreshIntervalMs;
                if (!shouldRefreshProfile
                    && (!snapshot.ModeKnown || snapshot.Label == _inputMethodLabel))
                {
                    return;
                }
                Dispatcher.BeginInvoke(() =>
                {
                    if (!_closing) ApplyInputMethodSnapshot(snapshot, refreshProfile: shouldRefreshProfile, foreground);
                });
            }
            finally
            {
                Interlocked.Exchange(ref _imeReadInFlight, 0);
            }
        });
    }

    private void RefreshActiveInputMethodName()
    {
        Interlocked.Exchange(ref _lastInputProfileCheckAt, Environment.TickCount64);
        var active = TopBarSystemInfo.ReadActiveInputMethod();
        var displayName = TopBarModuleRules.ResolveInputMethodDisplayName(
            active.Name,
            _lastInputLayout != 0 ? _lastInputLayout : active.Layout,
            _inputMethodFullName,
            active.IsInputProcessor ? active.LanguageId : null);
        var tooltip = displayName ?? (_inputMethodFullName.Length > 0 ? _inputMethodFullName : _inputMethodToolTip);
        if (displayName == _inputMethodName && tooltip == _inputMethodToolTip)
        {
            return;
        }
        _inputMethodName = displayName;
        _inputMethodToolTip = tooltip;
        UpdateInputMethodVisuals();
    }

    private void ApplyInputMethodSnapshot(
        (nint Layout, string Label, string FullName, bool ModeKnown) snapshot,
        bool refreshProfile = false,
        nint foreground = 0)
    {
        var (layout, label, fullName, modeKnown) = snapshot;
        var layoutChanged = layout != _lastInputLayout;
        if (foreground != 0)
        {
            _lastInputForeground = foreground;
        }
        _inputMethodFullName = fullName;
        if (layoutChanged)
        {
            _lastInputLayout = layout;
            _inputMethodLabel = label;
        }
        else if (modeKnown)
        {
            // A typing user keeps the IME window busy, so the mode call can miss its
            // timeout. Within one layout that means "no news": keep the last badge
            // instead of flashing the language-code fallback.
            _inputMethodLabel = label;
        }

        if (layoutChanged || refreshProfile)
        {
            Interlocked.Exchange(ref _lastInputProfileCheckAt, Environment.TickCount64);
            var active = TopBarSystemInfo.ReadActiveInputMethod();
            _inputMethodName = TopBarModuleRules.ResolveInputMethodDisplayName(
                active.Name,
                layout,
                fullName,
                active.IsInputProcessor ? active.LanguageId : null);
            _inputMethodToolTip = _inputMethodName ?? fullName;
        }

        UpdateInputMethodVisuals();
    }

    private void UpdateInputMethodVisuals()
    {
        if (_inputMethodBadgeText is { } badge)
        {
            badge.Text = _inputMethodLabel;
        }
        if (_inputMethodNameText is { } name)
        {
            var badgeOnly = TopBarRules.EffectiveInputMethodStyle(_state) == TopBarInputMethodStyle.BadgeOnly;
            name.Text = _inputMethodName ?? string.Empty;
            name.Visibility = badgeOnly || _inputMethodName is null
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        if (_modules.TryGetValue(TopBarModuleKind.InputMethod, out var module)
            && module.ToolTip is ToolTip toolTip
            && (toolTip.Content as TextBlock)?.Text != _inputMethodToolTip)
        {
            toolTip.Content = new TextBlock
            {
                Text = _inputMethodToolTip
            };
        }
    }

    // ---------------------------------------------------------------- 输入法切换浮层

    private void ToggleInputMethodFlyout(Button button)
    {
        if (_inputMethodFlyout?.IsOpen == true)
        {
            CloseInputMethodFlyout();
            return;
        }

        CloseFlyoutsBeforeOpening();
        _inputMethodTargetForeground = DockNativeMethods.GetForegroundWindow();

        var highContrast = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast;
        var look = ResolveSystemFlyoutLook(highContrast);
        var flyoutPalette = look.Palette;
        var card = new FlyoutCard(look)
        {
            Background = highContrast ? SystemColors.WindowBrush : FrozenBrush(flyoutPalette.Background),
            BorderBrush = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(flyoutPalette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10, 10, 10, 10),
            Child = BuildInputMethodCard(highContrast, flyoutPalette)
        };
        var onRight = ReferenceEquals(button.Parent, RightModules);
        var hostPanel = button.Parent as Panel ?? RightModules;
        var flyout = new Popup
        {
            PlacementTarget = button,
            Placement = InwardPlacement,
            HorizontalOffset = onRight ? -110 : -6,
            VerticalOffset = InwardFlyoutOffset,
            StaysOpen = true,
            AllowsTransparency = true,
            Child = card
        };
        PopupGlassBackdrop? glass = look.AllowBackdropBlur ? new PopupGlassBackdrop(flyout, card, look) : null;
        flyout.Closed += (_, _) =>
        {
            glass?.Dispose();
            glass = null;
            if (ReferenceEquals(_inputMethodFlyout, flyout))
            {
                _inputMethodFlyout = null;
            }
            if (flyout.Parent is Panel parent)
            {
                parent.Children.Remove(flyout);
            }
            UpdateForegroundTimerInterval();
        };
        hostPanel.Children.Add(flyout);
        _inputMethodFlyout = flyout;
        flyout.IsOpen = true;
        OnFlyoutOpened();
    }

    private FrameworkElement BuildInputMethodCard(bool highContrast, TopBarFlyoutPalette flyoutPalette)
    {
        var inkBrush = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(flyoutPalette.Foreground);
        var mutedOpacity = highContrast ? 1.0 : 0.68;
        var panel = new StackPanel { MinWidth = 228, MaxWidth = 280 };

        var header = new DockPanel { Margin = new Thickness(6, 0, 6, 8) };
        var title = new TextBlock
        {
            Text = "切换输入法",
            Foreground = inkBrush,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        var hint = new TextBlock
        {
            Text = "Win+Space",
            Foreground = inkBrush,
            Opacity = mutedOpacity,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(hint, System.Windows.Controls.Dock.Right);
        header.Children.Add(hint);
        header.Children.Add(title);
        panel.Children.Add(header);

        var options = _environment.ReadInputMethodOptions();
        if (options.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "未检测到可切换的输入法",
                Foreground = inkBrush,
                Opacity = mutedOpacity,
                Margin = new Thickness(6, 4, 6, 8)
            });
        }
        else
        {
            foreach (var option in options)
            {
                panel.Children.Add(CreateInputMethodRow(option, highContrast, inkBrush, flyoutPalette));
            }
        }

        panel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(4, 6, 4, 6),
            Background = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(flyoutPalette.Divider)
        });

        var settingsButton = CreateInputMethodActionButton(
            highContrast,
            isActive: false,
            flyoutPalette,
            content: new TextBlock
            {
                Text = "键盘与语言设置…",
                Foreground = inkBrush,
                Opacity = highContrast ? 1.0 : 0.85,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            },
            automationName: "打开 Windows 键盘与语言设置");
        settingsButton.Click += (_, _) =>
        {
            CloseInputMethodFlyout();
            _environment.OpenInputMethod();
        };
        panel.Children.Add(settingsButton);
        return panel;
    }

    private Button CreateInputMethodRow(
        TopBarInputMethodOption option,
        bool highContrast,
        Brush inkBrush,
        TopBarFlyoutPalette flyoutPalette)
    {
        var rowInk = highContrast && option.IsActive ? SystemColors.HighlightTextBrush : inkBrush;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });

        var tagBorder = new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = highContrast
                ? SystemColors.ControlBrush
                : FrozenBrush(option.IsActive ? flyoutPalette.ActiveBadgeBackground : flyoutPalette.BadgeBackground),
            Padding = new Thickness(4, 1, 4, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = option.LanguageTag,
                Foreground = highContrast ? SystemColors.ControlTextBrush : inkBrush,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            }
        };
        Grid.SetColumn(tagBorder, 0);
        grid.Children.Add(tagBorder);

        var nameText = new TextBlock
        {
            Text = option.DisplayName,
            Foreground = rowInk,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0)
        };
        Grid.SetColumn(nameText, 1);
        grid.Children.Add(nameText);

        if (option.IsActive)
        {
            var check = new TextBlock
            {
                Text = "\uE73E",
                FontFamily = StatusIconFont,
                FontSize = 12,
                Foreground = rowInk,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(check, 2);
            grid.Children.Add(check);
        }

        var button = CreateInputMethodActionButton(
            highContrast,
            option.IsActive,
            flyoutPalette,
            grid,
            automationName: option.DisplayName);
        button.Click += (_, _) => SelectInputMethod(option);
        return button;
    }

    private static Button CreateInputMethodActionButton(
        bool highContrast,
        bool isActive,
        TopBarFlyoutPalette flyoutPalette,
        object content,
        string automationName)
    {
        var normalBg = isActive
            ? (highContrast ? SystemColors.HighlightBrush : FrozenBrush(flyoutPalette.ActiveRowBackground))
            : Brushes.Transparent;
        var hoverBg = highContrast ? SystemColors.HighlightBrush : FrozenBrush(flyoutPalette.HoverRowBackground);

        var shellFactory = new FrameworkElementFactory(typeof(Border));
        shellFactory.Name = "Shell";
        shellFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        shellFactory.SetValue(Border.PaddingProperty, new Thickness(8, 6, 8, 6));
        shellFactory.SetValue(Border.BackgroundProperty, normalBg);
        shellFactory.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        shellFactory.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = shellFactory };
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg, "Shell"));
        if (highContrast)
        {
            hoverTrigger.Setters.Add(new Setter(System.Windows.Documents.TextElement.ForegroundProperty, SystemColors.HighlightTextBrush));
        }
        template.Triggers.Add(hoverTrigger);

        var button = new Button
        {
            Template = template,
            MinHeight = 32,
            Margin = new Thickness(0, 1, 0, 1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Cursor = Cursors.Hand,
            Content = content
        };
        AutomationProperties.SetName(button, automationName);
        return button;
    }

    private void SelectInputMethod(TopBarInputMethodOption option)
    {
        var targetForeground = _inputMethodTargetForeground;
        CloseInputMethodFlyout();
        if (_environment.ActivateInputMethod(option, targetForeground))
        {
            _inputMethodName = option.IsInputProcessor || TopBarModuleRules.IsLegacyImeLayout(option.KeyboardLayout)
                ? option.DisplayName
                : null;
            _inputMethodToolTip = option.DisplayName;
            _inputMethodLabel = option.LanguageTag;
            UpdateInputMethodVisuals();
            PollInputMethod(forceProfileRefresh: true);
        }
    }

    private void CloseInputMethodFlyout()
    {
        _inputMethodFlyout?.SetCurrentValue(Popup.IsOpenProperty, false);
        _inputMethodFlyout = null;
        UpdateForegroundTimerInterval();
    }

    // ---------------------------------------------------------------- 外设电量浮层

    internal Popup? PeripheralBatteryFlyout => _peripheralFlyout;

    /// <summary>外设电量显示设备图标和百分比，数据不变时复用现有内容。</summary>
    internal void RenderPeripheralModule()
    {
        if (_peripheralRoot is null)
        {
            return;
        }

        var snapshot = _environment.ReadPeripheralBattery();
        if (_peripheralFlyout is { IsOpen: true, Child: Border flyoutCard }
            && !ReferenceEquals(_peripheralFlyoutSnapshot, snapshot))
        {
            var highContrast = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast;
            flyoutCard.Child = BuildPeripheralBatteryCard(highContrast, ResolveSystemFlyoutLook(highContrast).Palette, snapshot);
        }
        var shown = TopBarModuleRules.ShownPeripherals(snapshot);
        if (_modules.TryGetValue(TopBarModuleKind.PeripheralBattery, out var element))
        {
            SetModuleGlassToolTip(element, TopBarModuleRules.FormatPeripheralBatteryTooltip(snapshot));
        }

        var signature = shown.Count == 0
            ? "empty"
            : string.Join(";", shown.Select(d => $"{d.Key}:{d.Kind}:{d.Percent}:{d.Charging}:{d.Asleep}"));
        if (signature == _peripheralSignature)
        {
            return;
        }
        _peripheralSignature = signature;

        _peripheralRoot.Children.Clear();
        if (shown.Count == 0)
        {
            _peripheralRoot.Children.Add(new TextBlock
            {
                Text = GlyphBatteryUnknown,
                FontFamily = StatusIconFont,
                FontSize = 16,
                Opacity = 0.92,
                VerticalAlignment = VerticalAlignment.Center
            });
            return;
        }

        var first = true;
        foreach (var device in shown)
        {
            var group = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = first ? new Thickness(0) : new Thickness(10, 0, 0, 0),
                Opacity = device.Asleep ? 0.55 : 1.0
            };
            first = false;
            var kindIcon = new TextBlock
            {
                Text = PeripheralKindGlyph(device.Kind),
                FontFamily = StatusIconFont,
                FontSize = 14,
                Opacity = 0.92,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 1)
            };
            kindIcon.SetResourceReference(TextBlock.ForegroundProperty, "TopBarInkBrush");
            group.Children.Add(kindIcon);
            var value = CreateValueText();
            value.Text = TopBarModuleRules.FormatPeripheralPercent(device.Percent);
            value.Margin = new Thickness(0, 0, 0, 1);
            group.Children.Add(value);
            _peripheralRoot.Children.Add(group);
        }
    }

    private void TogglePeripheralBatteryFlyout(Button button)
    {
        if (_peripheralFlyout?.IsOpen == true)
        {
            ClosePeripheralBatteryFlyout();
            return;
        }

        CloseFlyoutsBeforeOpening();

        var highContrast = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast;
        var look = ResolveSystemFlyoutLook(highContrast);
        const double width = 286;
        var card = CreateSystemFlyoutCard(highContrast, look, width);
        card.Child = BuildPeripheralBatteryCard(highContrast, look.Palette, _environment.ReadPeripheralBattery());
        var flyout = CreateSystemFlyoutPopup(button, card, look, width, closed =>
        {
            if (ReferenceEquals(_peripheralFlyout, closed))
            {
                _peripheralFlyout = null;
                _peripheralFlyoutSnapshot = null;
            }
        });
        _peripheralFlyout = flyout;
        flyout.IsOpen = true;
        OnFlyoutOpened();
    }

    private FrameworkElement BuildPeripheralBatteryCard(
        bool highContrast, TopBarFlyoutPalette flyoutPalette, PeripheralBatterySnapshot snapshot)
    {
        var inkBrush = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(flyoutPalette.Foreground);
        var mutedOpacity = highContrast ? 1.0 : 0.68;
        var panel = new StackPanel();

        _peripheralFlyoutSnapshot = snapshot;
        var header = new DockPanel { Margin = new Thickness(6, 0, 6, 8) };
        var title = new TextBlock
        {
            Text = "外设电量",
            Foreground = inkBrush,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        var hint = new TextBlock
        {
            Text = snapshot.Status switch
            {
                PeripheralBatteryStatus.SourceUnavailable => "暂不可用",
                PeripheralBatteryStatus.NotConfigured => "尚未配置",
                PeripheralBatteryStatus.Stale => "显示上次数据",
                _ => snapshot.UpdatedAt is { } updated ? $"{updated.LocalDateTime:HH:mm} 更新" : string.Empty
            },
            Foreground = inkBrush,
            Opacity = mutedOpacity,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(hint, System.Windows.Controls.Dock.Right);
        header.Children.Add(hint);
        header.Children.Add(title);
        panel.Children.Add(header);

        if (snapshot.Status is PeripheralBatteryStatus.SourceUnavailable or PeripheralBatteryStatus.NotConfigured)
        {
            panel.Children.Add(new TextBlock
            {
                Text = snapshot.Status == PeripheralBatteryStatus.NotConfigured
                    ? "在外设电量设置中选择 BatteryHub.exe 后可读取电量。"
                    : "BatteryHub 暂不可用，可检查程序路径后重试。",
                Foreground = inkBrush,
                Opacity = mutedOpacity,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(6, 0, 6, 6)
            });
        }
        else if (snapshot.Devices.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = snapshot.Status == PeripheralBatteryStatus.NoData
                    ? "正在读取外设电量…"
                    : "暂未检测到带电量上报的外设",
                Foreground = inkBrush,
                Opacity = mutedOpacity,
                FontSize = 12,
                Margin = new Thickness(6, 0, 6, 6)
            });
        }
        else
        {
            foreach (var device in snapshot.Devices)
            {
                panel.Children.Add(BuildPeripheralBatteryRow(device, inkBrush, mutedOpacity));
            }
        }

        panel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(4, 6, 4, 6),
            Background = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(flyoutPalette.Divider)
        });

        var refreshButton = CreateInputMethodActionButton(
            highContrast,
            isActive: false,
            flyoutPalette,
            content: new TextBlock
            {
                Text = "立即刷新",
                Foreground = inkBrush,
                Opacity = highContrast ? 1.0 : 0.85,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            },
            automationName: "立即刷新外设电量");
        refreshButton.Click += (_, _) =>
        {
            _environment.ForceRefreshPeripheralBattery();
            RenderPeripheralModule();
        };
        panel.Children.Add(refreshButton);

        var settingsButton = CreateInputMethodActionButton(
            highContrast,
            isActive: false,
            flyoutPalette,
            content: new TextBlock
            {
                Text = "外设电量设置…",
                Foreground = inkBrush,
                Opacity = highContrast ? 1.0 : 0.85,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            },
            automationName: "打开外设电量设置");
        settingsButton.Click += (_, _) =>
        {
            ClosePeripheralBatteryFlyout();
            _environment.OpenTopBarSettings(TopBarModuleKind.PeripheralBattery);
        };
        panel.Children.Add(settingsButton);
        return panel;
    }

    private FrameworkElement BuildPeripheralBatteryRow(
        PeripheralDevice device, Brush inkBrush, double mutedOpacity)
    {
        var grid = new Grid { Margin = new Thickness(6, 3, 6, 3), Opacity = device.Asleep ? 0.55 : 1.0 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = PeripheralKindGlyph(device.Kind),
            FontFamily = StatusIconFont,
            Foreground = inkBrush,
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });

        var left = new StackPanel();
        left.Children.Add(new TextBlock
        {
            Text = device.Name,
            Foreground = inkBrush,
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        left.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(device.Via)
                ? TopBarModuleRules.PeripheralConnectionLabel(device)
                : $"{device.Via} · {TopBarModuleRules.PeripheralConnectionLabel(device)}",
            Foreground = inkBrush,
            Opacity = mutedOpacity,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(left, 1);
        grid.Children.Add(left);

        var right = new TextBlock
        {
            Text = TopBarModuleRules.FormatPeripheralPercent(device.Percent),
            Foreground = inkBrush,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    private static string PeripheralKindGlyph(PeripheralDeviceKind kind) => kind switch
    {
        PeripheralDeviceKind.Mouse => "\uE962",
        PeripheralDeviceKind.Headset => "\uE95B",
        PeripheralDeviceKind.Keyboard => "\uE765",
        PeripheralDeviceKind.Gamepad => "\uE7FC",
        PeripheralDeviceKind.Phone => "\uE694",
        _ => GlyphBatteryUnknown
    };

    private void ClosePeripheralBatteryFlyout()
    {
        _peripheralFlyout?.SetCurrentValue(Popup.IsOpenProperty, false);
        _peripheralFlyout = null;
        _peripheralFlyoutSnapshot = null;
        UpdateForegroundTimerInterval();
    }

    private void UpdateForegroundTimerInterval()
    {
        if (_appBar is null || _closing)
        {
            return;
        }
        _foregroundTimer.Interval = _appBar.IsReserved
            && _todoFlyout is null
            && _clockFlyout is null
            && _inputMethodFlyout is null
            && _searchPopup is null
            && _peripheralFlyout is null
            && !HasAnySystemFlyoutRef
            ? FullScreenCheckInterval
            : SmartHideCheckInterval;
    }

    /// <summary>The open input-method picker flyout, for tests; popups sit outside the visual tree.</summary>
    internal Popup? InputMethodFlyout => _inputMethodFlyout;

    private ToolTip CreateGlassToolTip()
    {
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(System.Windows.Controls.Control.Background)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        borderFactory.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(System.Windows.Controls.Control.BorderBrush)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        borderFactory.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding(nameof(System.Windows.Controls.Control.BorderThickness)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        borderFactory.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding(nameof(System.Windows.Controls.Control.Padding)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        borderFactory.SetValue(Border.SnapsToDevicePixelsProperty, true);
        borderFactory.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));

        var toolTip = new ToolTip
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 10, 7),
            StaysOpen = true,
            HasDropShadow = false,
            Template = new ControlTemplate(typeof(ToolTip)) { VisualTree = borderFactory }
        };
        ApplyToolTipStyle(toolTip);
        return toolTip;
    }

    private void ApplyToolTipStyle(ToolTip toolTip)
    {
        var highContrast = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast;
        var flyout = ResolveSystemFlyoutLook(highContrast).Palette;
        toolTip.Background = highContrast ? SystemColors.WindowBrush : FrozenBrush(flyout.Background);
        toolTip.Foreground = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(flyout.Foreground);
        toolTip.BorderBrush = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(flyout.Border);
    }

    private TextBlock? ModuleText(TopBarModuleKind kind) =>
        IsCapsule && kind == TopBarModuleKind.TodoSummary ? _capsuleSummary :
        _modules.TryGetValue(kind, out var module) ? module switch
        {
            Button button => ValueTextOf(button.Content),
            Border border => ValueTextOf(border.Child),
            _ => null
        } : null;

    /// <summary>The module's status glyph, when its content carries one.</summary>
    private TextBlock? ModuleIcon(TopBarModuleKind kind) =>
        _modules.TryGetValue(kind, out var module) ? module switch
        {
            Button button => IconOf(button.Content),
            Border border => IconOf(border.Child),
            _ => null
        } : null;

    private static TextBlock? ValueTextOf(object? content) => content switch
    {
        TextBlock text => text,
        StackPanel panel => panel.Children.OfType<TextBlock>()
            .FirstOrDefault(text => text.Tag is not IconTag),
        _ => null
    };

    private static TextBlock? IconOf(object? content) => content switch
    {
        StackPanel panel => panel.Children.OfType<TextBlock>()
            .FirstOrDefault(text => text.Tag is IconTag),
        _ => null
    };

    private void AddTodoModule(StackPanel panel)
    {
        if (!TopBarRules.IsModuleOn(_state.Modules, TopBarModuleKind.TodoSummary))
        {
            return;
        }

        var text = CreateValueText();
        var button = new Button
        {
            Style = (Style)Resources["ModuleButton"],
            Content = CreateIconContent(GlyphCheckMark, text),
            Focusable = true
        };
        System.Windows.Automation.AutomationProperties.SetName(button, "待办模块");
        button.Click += (_, _) => ToggleTodoFlyout(button);
        _modules[TopBarModuleKind.TodoSummary] = button;
        panel.Children.Add(button);
    }

    private void AddClockModule(StackPanel panel)
    {
        if (!TopBarRules.IsModuleOn(_state.Modules, TopBarModuleKind.Clock))
        {
            return;
        }

        var text = CreateValueText();
        var button = new Button
        {
            Style = (Style)Resources["ModuleButton"],
            Content = text,
            Focusable = true
        };
        System.Windows.Automation.AutomationProperties.SetName(button, "时间日期模块");
        button.Click += (_, _) => ToggleClockFlyout(button);
        _modules[TopBarModuleKind.Clock] = button;
        panel.Children.Add(button);
    }

    // ---------------------------------------------------------------- 日历信息栏

    /// <summary>
    /// The calendar flyout under the clock module: the current time, today's date and a
    /// read-only month view. The system's calendar panel only anchors to the taskbar, so
    /// the bar shows its own month the same way it shows the to-do list.
    /// </summary>
    private void ToggleClockFlyout(Button button)
    {
        if (_clockFlyout?.IsOpen == true)
        {
            CloseClockFlyout();
            return;
        }

        CloseFlyoutsBeforeOpening();

        _clockMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var highContrast = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast;
        var look = ResolveSystemFlyoutLook(highContrast);
        var flyoutPalette = look.Palette;
        var inkBrush = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(flyoutPalette.Foreground);
        var card = new FlyoutCard(look)
        {
            Background = highContrast ? SystemColors.WindowBrush : FrozenBrush(flyoutPalette.Background),
            BorderBrush = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(flyoutPalette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 12, 16, 14),
            Child = BuildClockCard(inkBrush)
        };
        var onRight = ReferenceEquals(button.Parent, RightModules);
        var flyout = new Popup
        {
            PlacementTarget = button,
            Placement = InwardPlacement,
            HorizontalOffset = onRight ? -156 : -6,
            VerticalOffset = InwardFlyoutOffset,
            StaysOpen = true,
            AllowsTransparency = true,
            Child = card
        };
        PopupGlassBackdrop? glass = look.AllowBackdropBlur ? new PopupGlassBackdrop(flyout, card, look) : null;
        flyout.Closed += (_, _) =>
        {
            glass?.Dispose();
            glass = null;
            if (ReferenceEquals(_clockFlyout, flyout))
            {
                _clockFlyout = null;
            }
            if (flyout.Parent is Panel parent)
            {
                parent.Children.Remove(flyout);
            }
            UpdateForegroundTimerInterval();
        };
        (button.Parent as Panel ?? RightModules).Children.Add(flyout);
        _clockFlyout = flyout;
        flyout.IsOpen = true;
        OnFlyoutOpened();
    }

    private FrameworkElement BuildClockCard(Brush inkBrush)
    {
        var panel = new StackPanel { Width = 268 };
        var now = DateTime.Now;
        panel.Children.Add(new TextBlock
        {
            Text = now.ToString(_environment.Use24HourClock ? "HH:mm" : "hh:mm tt"),
            Foreground = inkBrush,
            FontSize = 30
        });
        var lunarToday = LunarDayText.FullDate(now);
        var dateLine = $"{now.Year}年{now.Month}月{now.Day}日 星期{"日一二三四五六"[(int)now.DayOfWeek]}";
        if (!string.IsNullOrEmpty(lunarToday))
        {
            dateLine += $" · {lunarToday}";
        }
        panel.Children.Add(new TextBlock
        {
            Text = dateLine,
            Foreground = inkBrush,
            Opacity = 0.82,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 10)
        });
        panel.Children.Add(BuildMonthView(inkBrush));
        return panel;
    }

    private FrameworkElement BuildMonthView(Brush inkBrush)
    {
        var panel = new StackPanel();

        // Month title row with previous/next navigation.
        var title = new TextBlock
        {
            Text = MonthTitle(_clockMonth),
            Foreground = inkBrush,
            FontSize = 13,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var previous = CreateMonthNavButton("‹", "上一月", -1, inkBrush);
        var next = CreateMonthNavButton("›", "下一月", 1, inkBrush);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(previous, System.Windows.Controls.Dock.Left);
        DockPanel.SetDock(next, System.Windows.Controls.Dock.Right);
        header.Children.Add(previous);
        header.Children.Add(next);
        header.Children.Add(title);
        panel.Children.Add(header);

        var grid = new Grid();
        for (var column = 0; column < 7; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
        for (var row = 1; row < 7; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        }

        // Monday-first weeks, the calendar convention in Chinese layouts.
        var weekdayNames = WeeklyCalendarRules.WeekdayHeaders(weekStartsOnSunday: false, shortForm: true);
        for (var column = 0; column < 7; column++)
        {
            var headerCell = new TextBlock
            {
                Text = weekdayNames[column],
                Foreground = inkBrush,
                Opacity = 0.65,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(headerCell, 0);
            Grid.SetColumn(headerCell, column);
            grid.Children.Add(headerCell);
        }

        var daysInMonth = DateTime.DaysInMonth(_clockMonth.Year, _clockMonth.Month);
        var leading = WeeklyCalendarRules.LeadingDaysForMonth(_clockMonth, weekStartsOnSunday: false);
        for (var day = 1; day <= daysInMonth; day++)
        {
            var index = leading + day - 1;
            var row = index / 7 + 1;
            var column = index % 7;
            var date = new DateTime(_clockMonth.Year, _clockMonth.Month, day);
            var isToday = date == DateTime.Today;
            var cellInk = isToday ? Brushes.White : inkBrush;
            var lunar = LunarDayText.For(date);
            var dayContent = new StackPanel
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            dayContent.Children.Add(new TextBlock
            {
                Text = day.ToString(),
                Foreground = cellInk,
                Opacity = isToday ? 1.0 : 0.9,
                FontSize = 12.5,
                FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            });
            if (!string.IsNullOrEmpty(lunar))
            {
                dayContent.Children.Add(new TextBlock
                {
                    Text = lunar,
                    Foreground = cellInk,
                    Opacity = isToday ? 0.92 : (lunar.EndsWith('月') ? 0.8 : 0.62),
                    FontSize = 9.5,
                    Margin = new Thickness(0, -1, 0, 0),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                });
            }
            FrameworkElement cell = isToday
                ? new Border
                {
                    Background = TodayBrush,
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(1),
                    Child = dayContent
                }
                : dayContent;
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }
        panel.Children.Add(grid);
        return panel;
    }

    private static readonly SolidColorBrush TodayBrush = CreateTodayBrush();

    private static SolidColorBrush CreateTodayBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(216, 37, 116, 200));
        brush.Freeze();
        return brush;
    }

    private Button CreateMonthNavButton(string glyph, string automationName, int months, Brush inkBrush)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                Foreground = inkBrush,
                FontSize = 16
            },
            Style = (Style)Resources["ModuleButton"],
            MinWidth = 28,
            Padding = new Thickness(4, 0, 4, 0)
        };
        System.Windows.Automation.AutomationProperties.SetName(button, automationName);
        button.Click += (_, _) => ShiftMonth(months, inkBrush);
        return button;
    }

    private void ShiftMonth(int months, Brush inkBrush)
    {
        _clockMonth = _clockMonth.AddMonths(months);
        if (_clockFlyout?.Child is Border card)
        {
            card.Child = BuildClockCard(inkBrush);
        }
    }

    private static string MonthTitle(DateTime month) =>
        month.Year + "年" + month.Month + "月";

    private void CloseClockFlyout()
    {
        _clockFlyout?.SetCurrentValue(Popup.IsOpenProperty, false);
        _clockFlyout = null;
        UpdateForegroundTimerInterval();
    }

    /// <summary>The open calendar flyout, for tests; popups sit outside the visual tree.</summary>
    internal Popup? ClockFlyout => _clockFlyout;

    // ---------------------------------------------------------------- 待办信息栏

    /// <summary>
    /// Shows the first to-do card's items under the module. A row click toggles that
    /// item's completion, exactly like the card's checkbox, and the card follows along
    /// because both share the same state object; clicking elsewhere closes the flyout.
    /// </summary>
    private void ToggleTodoFlyout(Button button)
    {
        if (IsCapsule)
        {
            ToggleCapsuleDetails();
            return;
        }
        if (_todoFlyout?.IsOpen == true)
        {
            CloseTodoFlyout(animate: false);
            return;
        }

        CloseFlyoutsBeforeOpening();

        var highContrast = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast;
        var look = ResolveSystemFlyoutLook(highContrast);
        var flyoutPalette = look.Palette;
        var inkBrush = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(flyoutPalette.Foreground);
        var list = _environment.ReadTodoList();
        var items = list?.Items ?? [];
        var panel = new StackPanel { MaxWidth = 300 };

        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(list?.Title) ? "待办" : list!.Title,
            Foreground = inkBrush,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 6)
        });
        if (items.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "暂无待办",
                Foreground = inkBrush,
                Opacity = 0.7
            });
        }
        else
        {
            foreach (var item in items)
            {
                panel.Children.Add(CreateTodoRow(item, inkBrush));
            }
        }

        var card = new FlyoutCard(look)
        {
            Background = highContrast ? SystemColors.WindowBrush : FrozenBrush(flyoutPalette.Background),
            BorderBrush = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(flyoutPalette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 10, 14, 12),
            Child = panel
        };
        // The flyout owns its closing: a StaysOpen popup drops cross-process clicks
        // silently and swallows in-thread ones through capture, so the bar closes it
        // from its own poll instead.
        var onRight = ReferenceEquals(button.Parent, RightModules);
        var flyout = new Popup
        {
            PlacementTarget = button,
            Placement = InwardPlacement,
            HorizontalOffset = onRight ? -140 : -6,
            VerticalOffset = InwardFlyoutOffset,
            StaysOpen = true,
            AllowsTransparency = true,
            Child = card
        };
        PopupGlassBackdrop? glass = look.AllowBackdropBlur ? new PopupGlassBackdrop(flyout, card, look) : null;
        flyout.Closed += (_, _) =>
        {
            glass?.Dispose();
            glass = null;
            if (ReferenceEquals(_todoFlyout, flyout))
            {
                _todoFlyout = null;
            }
            if (flyout.Parent is Panel parent)
            {
                parent.Children.Remove(flyout);
            }
            UpdateForegroundTimerInterval();
        };
        // A popup outside the visual tree never hears outside clicks at all, so it
        // lives in the module panel; it takes no layout space while closed.
        (button.Parent as Panel ?? LeftModules).Children.Add(flyout);
        _todoFlyout = flyout;
        flyout.IsOpen = true;
        OnFlyoutOpened();
    }

    private FrameworkElement CreateTodoRow(TodoItemState item, Brush? inkBrush = null)
    {
        var foreground = inkBrush ?? FrozenBrush(_palette.Flyout.Foreground);
        var glyph = new TextBlock
        {
            Width = 18,
            Foreground = foreground,
            Opacity = 0.7
        };
        if (IsCapsule) glyph.SetResourceReference(TextBlock.ForegroundProperty, "FloatingGlassMutedBrush");
        if (item.Kind == TodoItemKind.Event)
        {
            // Events are not checkable: a dot and the optional time, and no click action.
            glyph.Text = "•";
            glyph.Width = item.Time is null ? 18 : double.NaN;
            if (item.Time is { } time)
            {
                glyph.Text = $"•  {time:HH:mm}  ";
            }
            var eventRow = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 3, 0, 3),
                DataContext = item
            };
            eventRow.Children.Add(glyph);
            var eventText = new TextBlock
            {
                Text = item.Text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 270,
                Foreground = foreground
            };
            if (IsCapsule) eventText.SetResourceReference(TextBlock.ForegroundProperty, "FloatingGlassInkBrush");
            eventRow.Children.Add(eventText);
            return eventRow;
        }

        var glyphStyle = new Style(typeof(TextBlock));
        glyphStyle.Setters.Add(new Setter(TextBlock.TextProperty, item.Kind == TodoItemKind.Countdown ? "•" : "○"));
        glyphStyle.Triggers.Add(TodoCompletedTrigger(
            new Setter(TextBlock.TextProperty, "✓"),
            new Setter(TextBlock.OpacityProperty, 0.9)));
        glyph.Style = glyphStyle;

        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 270,
            Foreground = foreground
        };
        if (IsCapsule) text.SetResourceReference(TextBlock.ForegroundProperty, "FloatingGlassInkBrush");
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TodoItemState.Text)));
        var textStyle = new Style(typeof(TextBlock));
        textStyle.Triggers.Add(TodoCompletedTrigger(
            new Setter(TextBlock.TextDecorationsProperty, TextDecorations.Strikethrough),
            new Setter(TextBlock.OpacityProperty, 0.55)));
        text.Style = textStyle;

        var content = new StackPanel();
        content.Children.Add(text);
        var deadline = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = foreground, Opacity = 0.7, FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0)
        };
        if (IsCapsule) deadline.SetResourceReference(TextBlock.ForegroundProperty, "FloatingGlassMutedBrush");
        deadline.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TodoItemState.CountdownLabel)));
        deadline.SetBinding(ToolTipProperty, new System.Windows.Data.Binding(nameof(TodoItemState.DeadlineDescription)));
        var deadlineStyle = new Style(typeof(TextBlock));
        deadlineStyle.Triggers.Add(new DataTrigger
        {
            Binding = new System.Windows.Data.Binding(nameof(TodoItemState.CountdownLabel)), Value = null,
            Setters = { new Setter(VisibilityProperty, Visibility.Collapsed) }
        });
        deadline.Style = deadlineStyle;
        content.Children.Add(deadline);
        var row = new Grid
        {
            Width = 270,
            Margin = new Thickness(0, 3, 0, 3),
            Cursor = item.Kind == TodoItemKind.Todo ? Cursors.Hand : Cursors.Arrow,
            DataContext = item
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(glyph);
        Grid.SetColumn(content, 1);
        row.Children.Add(content);
        row.MouseLeftButtonUp += (_, _) =>
        {
            if (item.Kind != TodoItemKind.Todo) return;
            // One behaviour, like the card's checkbox: the row toggles completion.
            item.IsCompleted = !item.IsCompleted;
            TodoService.StampCompletion(item);
            _environment.TodoItemToggled();
            var carryOver = _environment.ReadTodoList()?.CarryOverIncomplete == true;
            if (!TodoService.BelongsToTodayList(item, DateTime.Today, carryOver))
            {
                row.Visibility = Visibility.Collapsed;
            }
            RenderModules();
        };
        return row;
    }

    private static DataTrigger TodoCompletedTrigger(params Setter[] setters)
    {
        var trigger = new DataTrigger
        {
            Binding = new System.Windows.Data.Binding(nameof(TodoItemState.IsCompleted)),
            Value = true
        };
        foreach (var setter in setters)
        {
            trigger.Setters.Add(setter);
        }
        return trigger;
    }

    /// <summary>
    /// Opens a tool tip manually after the pointer rests on the host for a moment and
    /// closes it when the pointer leaves. WPF's own tool tips never open in this
    /// no-activate window, so the bar drives the same rest-then-show behaviour itself.
    /// </summary>
    private void AttachHoverToolTip(FrameworkElement host, ToolTip toolTip)
    {
        var openTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        openTimer.Tick += (_, _) =>
        {
            openTimer.Stop();
            if (_closing || !host.IsLoaded || !IsVisible || HasAnyOpenFlyout
                || toolTip.Content is null
                || (toolTip.Content is TextBlock tb && string.IsNullOrWhiteSpace(tb.Text)))
            {
                toolTip.IsOpen = false;
                return;
            }
            ApplyToolTipStyle(toolTip);
            toolTip.PlacementTarget = host;
            toolTip.Placement = InwardPlacement;
            toolTip.VerticalOffset = InwardFlyoutOffset;
            toolTip.IsOpen = true;
        };
        host.MouseEnter += (_, _) =>
        {
            if (!_closing && host.IsLoaded && IsVisible)
            {
                openTimer.Start();
            }
        };
        host.MouseLeave += (_, _) =>
        {
            openTimer.Stop();
            toolTip.IsOpen = false;
        };
        host.PreviewMouseDown += (_, _) =>
        {
            openTimer.Stop();
            toolTip.IsOpen = false;
        };
        host.Unloaded += (_, _) =>
        {
            openTimer.Stop();
            toolTip.IsOpen = false;
        };
    }

    private void SetModuleGlassToolTip(FrameworkElement element, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (element.ToolTip is not ToolTip toolTip)
        {
            toolTip = CreateGlassToolTip();
            AttachHoverToolTip(element, toolTip);
            element.ToolTip = toolTip;
        }

        if (toolTip.Content is TextBlock block)
        {
            block.Text = text;
        }
        else
        {
            toolTip.Content = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320
            };
        }
    }

    private void ToggleCapsuleDetails()
    {
        if (_capsuleExpanded)
        {
            CloseTodoFlyout();
            return;
        }
        CloseFlyoutsBeforeOpening();
        var now = DateTime.Now;
        var todoList = _environment.ReadTodoList();
        var carryOver = todoList?.CarryOverIncomplete == true;
        var items = (todoList?.Items ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Text) && TodoService.BelongsToTodayList(item, now, carryOver))
            .ToList();
        var panel = new StackPanel();
        var heading = new DockPanel { Margin = new Thickness(0, 4, 0, 8) };
        var date = new TextBlock
        {
            Text = now.ToString("M月d日"), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        date.SetResourceReference(TextBlock.ForegroundProperty, "FloatingGlassMutedBrush");
        DockPanel.SetDock(date, System.Windows.Controls.Dock.Right);
        heading.Children.Add(date);
        var title = new TextBlock
        {
            Text = "今日待办", FontSize = 16, FontWeight = FontWeights.SemiBold
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "FloatingGlassInkBrush");
        heading.Children.Add(title);
        panel.Children.Add(heading);
        _capsuleStatus = new TextBlock
        {
            FontSize = 11, Margin = new Thickness(0, 0, 0, 12)
        };
        _capsuleStatus.SetResourceReference(TextBlock.ForegroundProperty, "FloatingGlassMutedBrush");
        panel.Children.Add(_capsuleStatus);
        if (items.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "今天没有待办，享受片刻轻松。", FontSize = 12,
                Margin = new Thickness(0, 12, 0, 18)
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "FloatingGlassInkBrush");
            panel.Children.Add(empty);
        }
        else
        {
            var rows = new StackPanel();
            foreach (var item in items)
            {
                var row = CreateTodoRow(item);
                row.Margin = new Thickness(2, 7, 2, 7);
                rows.Children.Add(row);
            }
            var availableHeight = _monitor is { } monitor
                ? DockAppBarController.GetLiveWorkingArea(monitor).Height / (monitor.Dpi / 96d) - 140d : 300d;
            panel.Children.Add(new ScrollViewer
            {
                MaxHeight = Math.Clamp(availableHeight, 48d, 300d),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = rows
            });
        }
        CapsuleBody.Child = panel;
        CapsuleBody.Visibility = Visibility.Visible;
        _capsuleExpanded = true;
        if (_capsuleChevron is { } chevron) chevron.Text = "\uE70E";
        RenderModules();
        CapsuleBody.Measure(new Size(CapsuleExpandedWidth - 2d, double.PositiveInfinity));
        _capsuleExpandedHeight = CapsuleHeight + CapsuleBody.DesiredSize.Height + 18d;
        MorphCapsule(expanding: true, animate: true);
        TopBarNativeMethods.ConsumeMouseButtonDown(TopBarNativeMethods.LeftMouseButton);
        TopBarNativeMethods.ConsumeMouseButtonDown(TopBarNativeMethods.EscapeKey);
    }

    private void MorphCapsule(bool expanding, bool animate)
    {
        var generation = ++_capsuleMorphGeneration;
        var fromWidth = double.IsNaN(Surface.Width) ? Surface.ActualWidth : Surface.Width;
        var fromHeight = double.IsNaN(Surface.Height) ? Surface.ActualHeight : Surface.Height;
        Surface.BeginAnimation(WidthProperty, null);
        Surface.BeginAnimation(HeightProperty, null);
        var targetWidth = expanding ? CapsuleExpandedWidth : _capsuleCompactWidth;
        var targetHeight = expanding ? _capsuleExpandedHeight : CapsuleHeight;
        Surface.Width = targetWidth;
        Surface.Height = targetHeight;
        if (expanding) ApplyCapsuleBounds(targetWidth, targetHeight);

        void Finish()
        {
            if (generation != _capsuleMorphGeneration) return;
            Surface.BeginAnimation(WidthProperty, null);
            Surface.BeginAnimation(HeightProperty, null);
            if (!expanding)
            {
                CapsuleBody.Visibility = Visibility.Collapsed;
                CapsuleBody.Child = null;
                _capsuleStatus = null;
                ApplyCapsuleBounds(targetWidth, targetHeight);
            }
        }

        if (!animate || _closing || !IsVisible || !SystemParameters.ClientAreaAnimation
            || IceBlueDockWindow.Motion != MotionLevel.Standard)
        {
            Finish();
            return;
        }
        var duration = new Duration(TimeSpan.FromMilliseconds(220));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        Surface.BeginAnimation(WidthProperty, new DoubleAnimation(fromWidth, targetWidth, duration)
        {
            EasingFunction = easing, FillBehavior = FillBehavior.Stop
        });
        var height = new DoubleAnimation(fromHeight, targetHeight, duration)
        {
            EasingFunction = easing, FillBehavior = FillBehavior.Stop
        };
        height.Completed += (_, _) => Finish();
        Surface.BeginAnimation(HeightProperty, height);
    }

    private void ApplyCapsuleBounds(double width, double height)
    {
        if (IsCapsule && !_closing && _monitor is { } monitor)
            _appBar?.Apply(monitor, reserveWorkArea: false, height, width);
    }

    internal bool CapsuleDetailsExpanded => _capsuleExpanded;
    internal FrameworkElement? CapsuleDetailsElement => CapsuleBody.Child as FrameworkElement;

    private void CloseTodoFlyout(bool animate = true)
    {
        _todoFlyout?.SetCurrentValue(Popup.IsOpenProperty, false);
        _todoFlyout = null;
        if (_capsuleExpanded)
        {
            _capsuleExpanded = false;
            if (_capsuleChevron is { } chevron) chevron.Text = "\uE70D";
            MorphCapsule(expanding: false, animate);
        }
        UpdateForegroundTimerInterval();
    }

    private void OnFlyoutOpened()
    {
        _flyoutPointerLeftSinceTick = null;
        _flyoutOpenedForegroundWindow = DockNativeMethods.GetForegroundWindow();
        UpdateForegroundTimerInterval();
        TopBarNativeMethods.ConsumeMouseButtonDown(TopBarNativeMethods.LeftMouseButton);
        TopBarNativeMethods.ConsumeMouseButtonDown(TopBarNativeMethods.RightMouseButton);
        TopBarNativeMethods.ConsumeMouseButtonDown(TopBarNativeMethods.EscapeKey);
    }

    private void CloseFlyoutsBeforeOpening()
    {
        CloseAllOpenFlyouts(animateTodo: false);
        CloseSearchPopup();
        CloseAllHoverToolTips();
    }

    private void CloseAllOpenFlyouts(bool animateTodo = true)
    {
        if (_capsuleExpanded || _todoFlyout?.IsOpen == true) CloseTodoFlyout(animateTodo);
        if (_clockFlyout?.IsOpen == true) CloseClockFlyout();
        if (_inputMethodFlyout?.IsOpen == true) CloseInputMethodFlyout();
        if (HasOpenSystemFlyout) CloseSystemFlyouts();
        if (_peripheralFlyout?.IsOpen == true) ClosePeripheralBatteryFlyout();
        _flyoutPointerLeftSinceTick = null;
        _flyoutOpenedForegroundWindow = 0;
    }

    private Popup? ActiveOpenPopup() =>
        _volumeFlyout is { IsOpen: true } v ? v
        : _batteryFlyout is { IsOpen: true } b ? b
        : _networkFlyout is { IsOpen: true } n ? n
        : _inputMethodFlyout is { IsOpen: true } im ? im
        : _peripheralFlyout is { IsOpen: true } peripheral ? peripheral
        : _clockFlyout is { IsOpen: true } c ? c
        : _todoFlyout is { IsOpen: true } t ? t
        : null;

    /// <summary>
    /// A StaysOpen=false popup never hears clicks that land in other processes: the
    /// system drops the capture instead of telling the popup. While the flyout is open
    /// the bar polls the button state and closes it when a click lands outside it,
    /// when the foreground window changes, or after the pointer stays outside for a grace period.
    /// </summary>
    private void CloseFlyoutOnOutsideClick()
    {
        CheckSearchPopupAutoDismiss();
        CheckTrayPanelDismissal();
        if (HasOpenTrayPanel)
        {
            return;
        }
        if (!HasAnyOpenFlyout && !_capsuleExpanded)
        {
            _flyoutPointerLeftSinceTick = null;
            _flyoutOpenedForegroundWindow = 0;
            return;
        }

        if (TopBarNativeMethods.IsMouseButtonDown(TopBarNativeMethods.EscapeKey))
        {
            CloseAllOpenFlyouts();
            return;
        }

        var activePopup = ActiveOpenPopup();
        var sliderCaptured = activePopup?.Child is FrameworkElement activeChild
            && Mouse.Captured is DependencyObject capturedObj
            && IsDescendantOfVisual(capturedObj, activeChild);

        var fg = DockNativeMethods.GetForegroundWindow();
        if (!sliderCaptured
            && fg != 0
            && _flyoutOpenedForegroundWindow != 0
            && fg != _flyoutOpenedForegroundWindow
            && fg != _handle)
        {
            CloseAllOpenFlyouts();
            return;
        }

        DockNativeMethods.GetCursorPos(out var cursor);
        if (TopBarNativeMethods.IsMouseButtonDown(TopBarNativeMethods.LeftMouseButton)
            || TopBarNativeMethods.IsMouseButtonDown(TopBarNativeMethods.RightMouseButton))
        {
            if (_capsuleExpanded && _appBar is { } appBar
                && !(cursor.X >= appBar.Bounds.Left && cursor.X < appBar.Bounds.Right
                    && cursor.Y >= appBar.Bounds.Top && cursor.Y < appBar.Bounds.Bottom))
            {
                CloseTodoFlyout();
            }
            TryCloseOnOutsideClick(_todoFlyout, cursor);
            TryCloseOnOutsideClick(_clockFlyout, cursor);
            TryCloseOnOutsideClick(_inputMethodFlyout, cursor);
            TryCloseOnOutsideClick(_peripheralFlyout, cursor);
            TryCloseSystemFlyoutsOnOutsideClick(cursor);
            return;
        }

        // Auto-close after the pointer leaves both the open popup and its bar button
        // (with a short grace delay and vertical bridge so moving into the popup never flickers).
        if (activePopup is null)
        {
            _flyoutPointerLeftSinceTick = null;
            return;
        }

        var inside = IsPointerInsideFlyoutOrAnchor(activePopup, cursor);
        var busy = sliderCaptured || IsSystemFlyoutBusy;
        if (inside || busy)
        {
            _flyoutPointerLeftSinceTick = null;
            return;
        }

        var now = Environment.TickCount64;
        _flyoutPointerLeftSinceTick ??= now;
        if (TopBarControlRules.ShouldAutoCloseFlyoutOnLeave(_flyoutPointerLeftSinceTick, now, pointerInsideFlyoutOrAnchor: false, interactionBusy: false))
        {
            CloseAllOpenFlyouts();
        }
    }

    private bool IsPointerInsideFlyoutOrAnchor(Popup flyout, DockNativeMethods.NativePoint cursor)
    {
        if (flyout.Child is not FrameworkElement child
            || System.Windows.PresentationSource.FromVisual(child) is not HwndSource source
            || source.Handle == 0)
        {
            return true;
        }

        DockNativeMethods.GetWindowRect(source.Handle, out var rect);
        const int margin = 6;
        if (cursor.X >= rect.Left - margin && cursor.X < rect.Right + margin
            && cursor.Y >= rect.Top - margin && cursor.Y < rect.Bottom + margin)
        {
            return true;
        }

        TopBarModuleKind? owner = flyout == _todoFlyout
            ? TopBarModuleKind.TodoSummary
            : flyout == _clockFlyout ? TopBarModuleKind.Clock
            : flyout == _inputMethodFlyout ? TopBarModuleKind.InputMethod
            : flyout == _peripheralFlyout ? TopBarModuleKind.PeripheralBattery
            : ResolveSystemFlyoutOwner(flyout);
        if (owner is { } kind
            && _modules.TryGetValue(kind, out var element)
            && element is Button button
            && button.IsLoaded)
        {
            var topLeft = button.PointToScreen(new System.Windows.Point(0, 0));
            var bottomRight = button.PointToScreen(new System.Windows.Point(button.ActualWidth, button.ActualHeight));
            var bridgeTop = Math.Min(topLeft.Y, rect.Bottom);
            var bridgeBottom = Math.Max(bottomRight.Y, rect.Top);
            if (cursor.X >= topLeft.X - margin && cursor.X < bottomRight.X + margin
                && cursor.Y >= bridgeTop - margin && cursor.Y <= bridgeBottom + margin)
            {
                return true;
            }
        }
        return false;
    }

    private void TryCloseOnOutsideClick(Popup? flyout, DockNativeMethods.NativePoint cursor)
    {
        if (flyout is not { IsOpen: true } || flyout.Child is not FrameworkElement child)
        {
            return;
        }

        if (Mouse.Captured is DependencyObject captured && IsDescendantOfVisual(captured, child))
        {
            return;
        }

        if (System.Windows.PresentationSource.FromVisual(child) is not HwndSource source || source.Handle == 0)
        {
            return;
        }
        DockNativeMethods.GetWindowRect(source.Handle, out var rect);
        if (cursor.X >= rect.Left && cursor.X < rect.Right && cursor.Y >= rect.Top && cursor.Y < rect.Bottom)
        {
            return;
        }

        // A click on a module that owns an open flyout toggles it; letting the poll
        // close it here would only make the same click re-open it.
        TopBarModuleKind? owner = flyout == _todoFlyout
            ? TopBarModuleKind.TodoSummary
            : flyout == _clockFlyout ? TopBarModuleKind.Clock
            : flyout == _inputMethodFlyout ? TopBarModuleKind.InputMethod
            : ResolveSystemFlyoutOwner(flyout);
        if (owner is { } kind
            && _modules.TryGetValue(kind, out var element)
            && element is Button button)
        {
            var topLeft = button.PointToScreen(new System.Windows.Point(0, 0));
            var bottomRight = button.PointToScreen(new System.Windows.Point(button.ActualWidth, button.ActualHeight));
            if (cursor.X >= topLeft.X && cursor.X < bottomRight.X
                && cursor.Y >= topLeft.Y && cursor.Y < bottomRight.Y)
            {
                return;
            }
        }

        flyout.IsOpen = false;
    }

    private static bool IsDescendantOfVisual(DependencyObject node, DependencyObject ancestor)
    {
        var current = node;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    /// <summary>The open to-do flyout, for tests; popups sit outside the visual tree.</summary>
    internal Popup? TodoFlyout => _todoFlyout;

    // ---------------------------------------------------------------- 搜索

    /// <summary>
    /// The centre search entry button: clicking it or pressing the global search hotkey
    /// opens the Ice Blue Overview Card and focuses its search box.
    /// </summary>
    private void AddSearchModule()
    {
        if (IsCapsule || !IsSearchEntryEnabled)
        {
            return;
        }

        var searchIcon = new TextBlock
        {
            Text = GlyphSearch,
            FontFamily = StatusIconFont,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 1)
        };
        searchIcon.SetResourceReference(TextBlock.ForegroundProperty, "TopBarMutedInkBrush");
        var searchPlaceholder = new TextBlock
        {
            Text = "搜索应用、文件…",
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        searchPlaceholder.SetResourceReference(TextBlock.ForegroundProperty, "TopBarMutedInkBrush");
        var content = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { searchIcon, searchPlaceholder }
        };

        var button = new Button
        {
            Style = (Style)Resources["SearchEntryButton"],
            Width = SearchBoxWidth,
            MinWidth = TopBarSearchRules.MinCenteredEntryWidth,
            Height = Math.Clamp(BarHeight - 10d, 18d, 30d),
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = true,
            Content = content
        };
        SetModuleGlassToolTip(
            button,
            string.IsNullOrWhiteSpace(_state.SearchHotkey)
                ? "搜索应用、文件"
                : $"搜索应用、文件（{_state.SearchHotkey}）");
        AutomationProperties.SetName(button, "搜索入口");
        button.Click += (_, _) => Dispatcher.BeginInvoke(TriggerSearch, DispatcherPriority.Input);

        _searchButton = button;
        _modules[TopBarModuleKind.Search] = button;
        CenterHost.Children.Add(button);
        UpdateSearchHostWidth();
    }

    private void AddSideSearchModule(StackPanel panel)
    {
        var searchIcon = new TextBlock
        {
            Text = GlyphSearch,
            FontFamily = StatusIconFont,
            FontSize = 13.5,
            Opacity = 0.92,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 1)
        };
        searchIcon.SetResourceReference(TextBlock.ForegroundProperty, "TopBarInkBrush");
        var button = new Button
        {
            Style = (Style)Resources["ModuleButton"],
            Padding = new Thickness(6, 0, 6, 0),
            MinWidth = 32,
            Focusable = true,
            Content = searchIcon
        };
        SetModuleGlassToolTip(
            button,
            string.IsNullOrWhiteSpace(_state.SearchHotkey)
                ? "搜索应用、文件"
                : $"搜索应用、文件（{_state.SearchHotkey}）");
        AutomationProperties.SetName(button, "搜索入口");
        button.Click += (_, _) => Dispatcher.BeginInvoke(TriggerSearch, DispatcherPriority.Input);
        _searchButton = button;
        _modules[TopBarModuleKind.Search] = button;
        panel.Children.Add(button);
    }

    /// <summary>
    /// Clamps the centered entry button to the symmetric clear room left by the wider
    /// side module so it never overlaps either side; collapses the button only when even
    /// the compact minimum width would collide (the global hotkey remains active).
    /// </summary>
    private void UpdateSearchHostWidth()
    {
        if (IsCapsule
            || TopBarRules.EffectiveSearchPlacement(_state) != TopBarSearchPlacement.CenterBar
            || _searchButton is not { } button
            || Surface.ActualWidth <= 0)
        {
            return;
        }

        var width = TopBarSearchRules.ComputeCenteredEntryWidth(
            Surface.ActualWidth,
            LeftModules.ActualWidth,
            RightModules.ActualWidth);
        if (width <= 0d)
        {
            button.Visibility = Visibility.Collapsed;
            return;
        }

        button.Visibility = Visibility.Visible;
        button.Width = width;
    }

    internal void TriggerSearch()
    {
        if (SearchRequested is { } requested) requested();
        else TriggerOwnedSearch();
    }

    internal void TriggerOwnedSearch()
    {
        if (_closing || !IsSearchEntryEnabled)
        {
            return;
        }
        if (IsCapsule)
        {
            UpdateForForeground();
            if (_trueFullScreenForeground) return;
        }
        if (_state.SearchInline == true && _searchPopup is not null)
        {
            CloseSearchPopup();
            return;
        }
        CloseFlyoutsBeforeOpening();
        if (_state.SearchInline == true)
        {
            OpenSearchPopup();
            return;
        }
        _environment.ToggleLauncherPanel();
    }

    /// <summary>
    /// Opens the search popup on the work-area side of the entry.
    /// </summary>
    private void OpenSearchPopup()
    {
        System.Windows.Point anchorPixels;
        if (_searchButton is { ActualWidth: > 0d } button)
        {
            anchorPixels = button.PointToScreen(
                new System.Windows.Point(button.ActualWidth / 2d, _state.ShowAtBottom ? 0d : button.ActualHeight));
        }
        else if (ActualWidth > 0d && ActualHeight > 0d)
        {
            anchorPixels = PointToScreen(new System.Windows.Point(ActualWidth / 2d, _state.ShowAtBottom ? 0d : ActualHeight));
        }
        else
        {
            return;
        }

        var look = ResolveSearchLook();
        var engine = TopBarSearchRowRules.ResolveEngine(_state.SearchEngine, _state.SearchCustomUrl);
        var monitor = _appBar?.Monitor ?? _monitor;
        var popup = new TopBarSearchWindow(
            look,
            engine,
            (int)Math.Round(anchorPixels.X),
            (int)Math.Round(anchorPixels.Y),
            monitor,
            _environment.IsEverythingSearchEnabled,
            _environment.ReadSearchSnapshotAsync,
            _environment.OpenSearchEntry,
            opensUpward: _state.ShowAtBottom);
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_searchPopup, popup))
            {
                _searchPopup = null;
                _searchPopupPointerLeftSinceTick = null;
                _searchPopupOpenedForegroundWindow = 0;
                _searchPopupObservedForeground = false;
                UpdateForegroundTimerInterval();
            }
        };
        _searchPopup = popup;
        _searchPopupOpenedTick = Environment.TickCount64;
        _searchPopupPointerLeftSinceTick = null;
        _searchPopupOpenedForegroundWindow = DockNativeMethods.GetForegroundWindow();
        _searchPopupObservedForeground = false;
        TopBarNativeMethods.ConsumeMouseButtonDown(TopBarNativeMethods.LeftMouseButton);
        TopBarNativeMethods.ConsumeMouseButtonDown(TopBarNativeMethods.RightMouseButton);
        TopBarNativeMethods.ConsumeMouseButtonDown(TopBarNativeMethods.EscapeKey);
        UpdateForegroundTimerInterval();
        popup.Show();
    }

    internal void CloseSearchPopup()
    {
        if (_searchPopup is not { } popup) return;
        _searchPopup = null;
        _searchPopupPointerLeftSinceTick = null;
        _searchPopupOpenedForegroundWindow = 0;
        _searchPopupObservedForeground = false;
        UpdateForegroundTimerInterval();
        popup.RequestClose();
    }

    private void CheckSearchPopupAutoDismiss()
    {
        if (_searchPopup is not { IsLoaded: true } popup || popup.IsOpeningResult)
        {
            _searchPopupPointerLeftSinceTick = null;
            return;
        }

        if (TopBarNativeMethods.IsMouseButtonDown(TopBarNativeMethods.EscapeKey))
        {
            CloseSearchPopup();
            return;
        }

        var now = Environment.TickCount64;
        var fg = DockNativeMethods.GetForegroundWindow();
        if (fg != 0 && popup.WindowHandle != 0 && fg == popup.WindowHandle)
        {
            _searchPopupObservedForeground = true;
        }
        else if (now - _searchPopupOpenedTick >= 250
            && fg != 0
            && fg != _handle)
        {
            if (_searchPopupObservedForeground
                || (_searchPopupOpenedForegroundWindow != 0 && fg != _searchPopupOpenedForegroundWindow))
            {
                CloseSearchPopup();
                return;
            }
        }

        DockNativeMethods.GetCursorPos(out var cursor);
        var inside = IsPointerInsideSearchPopupOrAnchor(popup, cursor);
        if (now - _searchPopupOpenedTick >= 200
            && (TopBarNativeMethods.IsMouseButtonDown(TopBarNativeMethods.LeftMouseButton)
                || TopBarNativeMethods.IsMouseButtonDown(TopBarNativeMethods.RightMouseButton)))
        {
            if (!inside)
            {
                CloseSearchPopup();
            }
            return;
        }

        if (inside || popup.HasUserInput)
        {
            _searchPopupPointerLeftSinceTick = null;
            return;
        }

        _searchPopupPointerLeftSinceTick ??= now;
        if (TopBarControlRules.ShouldAutoCloseFlyoutOnLeave(
            _searchPopupPointerLeftSinceTick,
            now,
            pointerInsideFlyoutOrAnchor: false,
            interactionBusy: false))
        {
            CloseSearchPopup();
        }
    }

    private bool IsPointerInsideSearchPopupOrAnchor(TopBarSearchWindow popup, DockNativeMethods.NativePoint cursor)
    {
        var popupHandle = popup.WindowHandle;
        if (popupHandle == 0)
        {
            return true;
        }

        DockNativeMethods.GetWindowRect(popupHandle, out var rect);
        const int margin = 6;
        if (cursor.X >= rect.Left - margin && cursor.X < rect.Right + margin
            && cursor.Y >= rect.Top - margin && cursor.Y < rect.Bottom + margin)
        {
            return true;
        }

        if (_searchButton is { IsLoaded: true, ActualWidth: > 0d, ActualHeight: > 0d } button)
        {
            var topLeft = button.PointToScreen(new System.Windows.Point(0, 0));
            var bottomRight = button.PointToScreen(new System.Windows.Point(button.ActualWidth, button.ActualHeight));
            var bridgeBottom = Math.Max(bottomRight.Y, rect.Top);
            if (cursor.X >= topLeft.X - margin && cursor.X < bottomRight.X + margin
                && cursor.Y >= topLeft.Y && cursor.Y <= bridgeBottom + margin)
            {
                return true;
            }
        }

        return false;
    }

    private TopBarSearchLook ResolveSearchLook() =>
        TopBarSearchLook.FromPalette(ResolveTopBarPopupPalette());



    // ---------------------------------------------------------------- 搜索热键

    private nint OnHotkeyMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == TopBarNativeMethods.WmHotkey && (int)wParam == SearchHotkeyId)
        {
            handled = true;
            TriggerSearch();
        }
        return 0;
    }

    /// <summary>
    /// Matches the registration to the settings: the hotkey lives while the search
    /// module and overview card are both on, and a changed hotkey text re-registers in place.
    /// </summary>
    private void SyncSearchHotkey()
    {
        // Global hotkeys are now owned by GlobalHotkeyHost so they remain active even when
        // the top bar is disabled. Ensure any stale registration on this window is cleared.
        UnregisterSearchHotkey();
    }

    private void UnregisterSearchHotkey()
    {
        if (_searchHotkeyRegistered && _handle != 0)
        {
            TopBarNativeMethods.UnregisterHotKey(_handle, SearchHotkeyId);
        }
        _searchHotkeyRegistered = false;
    }

    /// <summary>
    /// Whether a candidate hotkey text is free to register right now. The live
    /// registration is lifted for the probe and restored afterwards, so testing the
    /// bar's own combination succeeds.
    /// </summary>
    internal bool ProbeSearchHotkey(string text)
    {
        if (_handle == 0 || TopBarSearchRules.ParseHotkey(text) is not { } binding)
        {
            return false;
        }
        UnregisterSearchHotkey();
        var free = TopBarNativeMethods.RegisterHotKey(
            _handle,
            SearchHotkeyId,
            binding.Modifiers | TopBarNativeMethods.ModNoRepeat,
            binding.VirtualKey);
        if (free)
        {
            TopBarNativeMethods.UnregisterHotKey(_handle, SearchHotkeyId);
        }
        SyncSearchHotkey();
        return free;
    }

    /// <summary>The centered search entry button, for tests.</summary>
    internal Button? SearchEntryButton => _searchButton;

    /// <summary>The inline search popup while it is open, for tests.</summary>
    internal TopBarSearchWindow? SearchPopup => _searchPopup;

    private static System.Windows.Media.SolidColorBrush FrozenBrush(string color) =>
        CreateFrozenColorBrush((Color)ColorConverter.ConvertFromString(color));

    private static SolidColorBrush CreateFrozenColorBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    internal void RefreshTodoSummary() => RenderModules();

    private void RenderModules()
    {
        if (_closing)
        {
            return;
        }

        _tick++;
        var minute = DateTime.Now.Ticks / TimeSpan.TicksPerMinute;
        if ((_todoFlyout?.IsOpen == true || _capsuleExpanded) && minute != _lastCountdownMinute)
        {
            _lastCountdownMinute = minute;
            // Reuse the shared sampling cadence; no timer is owned by individual rows.
            foreach (var item in _environment.ReadTodoList()?.Items ?? [])
            {
                if (item.DeadlineDate is not null) item.RefreshCountdownLabel();
            }
        }
        if (ModuleText(TopBarModuleKind.Clock) is { } clock)
        {
            clock.Text = FormatClock(
                _environment.Use24HourClock,
                _environment.ShowDate,
                _environment.ShowLunarDate,
                TopBarRules.EffectiveClockStyle(_state));
        }
        if (ModuleText(TopBarModuleKind.TodoSummary) is { } todo)
        {
            if (IsCapsule)
            {
                var now = DateTime.Now;
                var todoList = _environment.ReadTodoList();
                var carryOver = todoList?.CarryOverIncomplete == true;
                var items = todoList?.Items ?? [];
                var next = TopBarModuleRules.NextTimedTodoToday(items, now);
                var count = items.Count(item => item.Kind == TodoItemKind.Todo && !item.IsCompleted
                    && !string.IsNullOrWhiteSpace(item.Text) && TodoService.BelongsToTodayList(item, now, carryOver));
                todo.Text = next is not null ? $"{next.DeadlineTime:HH:mm}  {next.Text}"
                    : count > 0 ? $"今日待办  {count}" : now.ToString(_environment.Use24HourClock ? "HH:mm" : "hh:mm tt");
                if (_capsuleStatus is { } status)
                    status.Text = count > 0 ? $"还有 {count} 项待办" : "待办已完成";
                var compactWidth = next is not null ? 225d : CapsuleWidth;
                if (_capsuleCompactWidth != compactWidth)
                {
                    _capsuleCompactWidth = compactWidth;
                    if (!_capsuleExpanded)
                    {
                        Surface.Width = compactWidth;
                        ApplyCapsuleBounds(compactWidth, CapsuleHeight);
                    }
                }
            }
            else
            {
                todo.Text = _environment.CountIncompleteTodos().ToString();
            }
        }
        if (ModuleText(TopBarModuleKind.Weather) is { } weather)
        {
            RenderWeather(weather);
        }
        RenderPeripheralModule();
        if (ModuleText(TopBarModuleKind.Performance) is { } performance)
        {
            var perfStyle = TopBarRules.EffectivePerformanceStyle(_state);
            performance.Text = TopBarModuleRules.FormatPerformanceLabel(_lastSnapshot, perfStyle);
            if (_modules.TryGetValue(TopBarModuleKind.Performance, out var perfElement))
            {
                SetModuleGlassToolTip(perfElement, TopBarModuleRules.FormatPerformanceTooltip(_lastSnapshot));
            }
        }

        UpdateInputMethodVisuals();

        // System facts change slowly and some calls are not free, so they refresh on a
        // slower cadence than the clock; the input method also follows focus changes.
        if (_tick % SystemInfoTickDivisor == 0)
        {
            RenderSystemModules();
        }
    }

    private void ApplyIconTextModule(
        TopBarModuleKind kind,
        TextBlock valueText,
        string label,
        string? tooltip)
    {
        valueText.Text = label;
        var showText = !string.IsNullOrEmpty(label);
        valueText.Visibility = showText ? Visibility.Visible : Visibility.Collapsed;
        if (ModuleIcon(kind) is { } icon)
        {
            icon.Margin = showText
                ? new Thickness(0, 0, 6, 1)
                : new Thickness(0, 0, 0, 1);
        }
        if (_modules.TryGetValue(kind, out var element))
        {
            if (element is Button button)
            {
                button.Padding = showText ? new Thickness(8, 0, 8, 0) : new Thickness(6, 0, 6, 0);
                button.MinWidth = 32;
            }
            if (!string.IsNullOrEmpty(tooltip))
            {
                SetModuleGlassToolTip(element, tooltip);
            }
        }
    }

    private void RenderSystemModules()
    {
        if (_facts is not { } facts)
        {
            return;
        }

        if (ModuleText(TopBarModuleKind.Battery) is { } battery)
        {
            var (percent, status) = facts.Battery;
            var label = TopBarModuleRules.FormatBatteryLabel(percent, TopBarRules.EffectiveBatteryStyle(_state));
            var tooltip = TopBarModuleRules.FormatBatteryTooltip(percent, status);
            ApplyIconTextModule(TopBarModuleKind.Battery, battery, label, tooltip);
            if (ModuleIcon(TopBarModuleKind.Battery) is { } batteryIcon)
            {
                batteryIcon.Text = percent < 0
                    ? GlyphBatteryUnknown
                    : BatteryGlyph(percent, status == TopBarBatteryStatus.Charging);
            }
        }
        if (ModuleText(TopBarModuleKind.Volume) is { } volume)
        {
            var percent = facts.Volume?.Percent;
            var muted = facts.Volume?.Muted == true;
            var label = TopBarModuleRules.FormatVolumeLabel(percent, muted, TopBarRules.EffectiveVolumeStyle(_state));
            var tooltip = TopBarModuleRules.FormatVolumeTooltip(percent, muted);
            ApplyIconTextModule(TopBarModuleKind.Volume, volume, label, tooltip);
            if (ModuleIcon(TopBarModuleKind.Volume) is { } volumeIcon)
            {
                volumeIcon.Text = muted ? GlyphVolumeMuted : percent switch
                {
                    null => GlyphVolume,
                    0 => GlyphVolume0,
                    <= 33 => GlyphVolume1,
                    <= 66 => GlyphVolume2,
                    _ => GlyphVolume3
                };
            }
        }
        if (ModuleText(TopBarModuleKind.Network) is { } network)
        {
            var networkLabel = TopBarModuleRules.FormatNetworkLabel(
                facts.Network,
                _state.ShowWifiName == true,
                _state.NetworkIconOnly == true);
            ApplyIconTextModule(TopBarModuleKind.Network, network, networkLabel, facts.Network.Tooltip);
            if (ModuleIcon(TopBarModuleKind.Network) is { } networkIcon)
            {
                networkIcon.Text = !facts.Network.Connected
                    ? GlyphWifiOff
                    : !facts.Network.IsWireless
                        ? GlyphEthernet
                        : TopBarModuleRules.WifiSignalBars(facts.Network.SignalQuality) switch
                        {
                            1 => GlyphWifi1,
                            2 => GlyphWifi2,
                            3 => GlyphWifi3,
                            _ => GlyphWifi,
                        };
            }
        }
    }

    private void RenderWeather(TextBlock text)
    {
        var snapshot = _environment.ReadWeather();
        text.Text = TopBarModuleRules.FormatWeatherLabel(snapshot, TopBarRules.EffectiveWeatherStyle(_state));
        if (snapshot is not null
            && _modules.TryGetValue(TopBarModuleKind.Weather, out var module)
            && module.ToolTip is ToolTip toolTip)
        {
            toolTip.Content = new TextBlock
            {
                Text = DescribeWeather(snapshot),
                MaxWidth = 320,
                TextWrapping = TextWrapping.Wrap
            };
        }
    }

    private static string DescribeWeather(WeatherSnapshot snapshot)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(snapshot.City))
        {
            lines.Add(snapshot.City.Trim());
        }
        if (snapshot.DailyHighCelsius is { } high && snapshot.DailyLowCelsius is { } low)
        {
            lines.Add($"最高 {Math.Round(high):0}° / 最低 {Math.Round(low):0}°");
        }
        if (snapshot.HumidityPercent is { } humidity)
        {
            lines.Add($"湿度 {humidity}%");
        }
        lines.Add(snapshot.UpdatedAt is { } updated
            ? $"更新于 {updated:HH:mm}"
            : "尚无数据");
        if (snapshot.Status is WeatherStatus.Failed or WeatherStatus.CachedFallback
            && !string.IsNullOrEmpty(snapshot.ErrorMessage))
        {
            lines.Add($"刷新失败：{snapshot.ErrorMessage}");
        }
        return string.Join("\n", lines);
    }

    internal void RefreshClock()
    {
        if (!_closing && IsLoaded && IsVisible)
        {
            RenderModules();
        }
    }

    private static string FormatClock(
        bool use24HourClock,
        bool showDate,
        bool showLunarDate,
        TopBarClockStyle style = TopBarClockStyle.Full) =>
        TopBarModuleRules.FormatClock(DateTime.Now, use24HourClock, showDate, showLunarDate, style);

    private void RefreshAttention()
    {
        if (_closing)
        {
            return;
        }

        if (!TopBarRules.IsModuleOn(_state.Modules, TopBarModuleKind.Attention))
        {
            _attentionPanel.Children.Clear();
            return;
        }

        using (UiDiagnostics.Measure("顶栏注意刷新"))
        {
            var running = _catalog.Capture();
            // Only truly destroyed windows lose their notice: flashes often name hidden
            // message windows that an enumeration never sees.
            _attention.RemoveRange(_attention.OrderedWindows
                .Where(handle => !DockNativeMethods.IsWindow(handle))
                .ToArray());
            var activeHandles = _attention.OrderedWindows.ToHashSet();
            foreach (var stale in _attentionMeta.Keys
                .Where(handle => !activeHandles.Contains(handle))
                .ToList())
            {
                _attentionMeta.Remove(stale);
            }
            var groups = WindowGrouping.Group(running);
            var (shown, overflow) = TopBarAttentionQueue.ResolveDisplay(
                _attention.OrderedWindows,
                TopBarState.MaximumAttentionApps);

            _attentionPanel.Children.Clear();
            foreach (var window in shown)
            {
                var processId = _attention.ProcessOf(window);
                if (!_attentionMeta.TryGetValue(window, out var meta))
                {
                    meta = ResolveAttentionTarget(running, groups, window, processId);
                    if (!string.Equals(meta.Name, "消息", StringComparison.Ordinal)
                        || !string.IsNullOrWhiteSpace(meta.ExecutablePath))
                    {
                        _attentionMeta[window] = meta;
                    }
                }

                var name = meta.Name;
                var appIcon = ResolveAttentionIcon(meta.ExecutablePath);
                var iconOnly = TopBarRules.EffectiveAttentionStyle(_state) == TopBarAttentionStyle.IconOnly;
                object buttonContent;
                if (iconOnly)
                {
                    UIElement iconElement;
                    if (appIcon is not null)
                    {
                        var iconImage = new System.Windows.Controls.Image
                        {
                            Source = appIcon,
                            Width = 16,
                            Height = 16,
                            VerticalAlignment = VerticalAlignment.Center,
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                            Stretch = Stretch.Uniform
                        };
                        RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.HighQuality);
                        iconElement = iconImage;
                    }
                    else
                    {
                        var fallbackGlyph = new TextBlock
                        {
                            Text = "\uE7E7",
                            FontFamily = StatusIconFont,
                            FontSize = 13,
                            Width = 16,
                            Height = 16,
                            TextAlignment = TextAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                        };
                        fallbackGlyph.SetResourceReference(TextBlock.ForegroundProperty, "TopBarInkBrush");
                        iconElement = fallbackGlyph;
                    }
                    var badgeDot = new System.Windows.Shapes.Ellipse
                    {
                        Width = 5.5,
                        Height = 5.5,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, -1, -2, 0),
                        IsHitTestVisible = false
                    };
                    badgeDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TopBarAttentionBrush");
                    buttonContent = new Grid
                    {
                        Width = 18,
                        Height = 18,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { iconElement, badgeDot }
                    };
                }
                else
                {
                    var attentionText = new TextBlock
                    {
                        Text = name,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                        MaxWidth = 120
                    };
                    attentionText.SetResourceReference(TextBlock.ForegroundProperty, "TopBarAttentionBrush");
                    buttonContent = attentionText;
                    if (appIcon is not null)
                    {
                        var iconImage = new System.Windows.Controls.Image
                        {
                            Source = appIcon,
                            Width = 14,
                            Height = 14,
                            Margin = new Thickness(0, 0, 5, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Stretch = Stretch.Uniform
                        };
                        RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.HighQuality);
                        buttonContent = new StackPanel
                        {
                            Orientation = System.Windows.Controls.Orientation.Horizontal,
                            VerticalAlignment = VerticalAlignment.Center,
                            Children = { iconImage, attentionText }
                        };
                    }
                }
                var button = new Button
                {
                    Style = (Style)Resources["ModuleButton"],
                    Padding = iconOnly ? new Thickness(6, 0, 6, 0) : new Thickness(8, 0, 8, 0),
                    MinWidth = 32,
                    Content = buttonContent,
                    Focusable = true,
                    Tag = window
                };
                SetModuleGlassToolTip(button, $"{name} · 有新消息（点击切换）");
                System.Windows.Automation.AutomationProperties.SetName(button, $"{name}有新消息");
                var capturedWindow = window;
                var capturedProcessId = processId;
                button.Click += (_, _) =>
                {
                    if (_attention.Cleared(capturedWindow, capturedProcessId))
                    {
                        RefreshAttention();
                    }
                    _environment.ActivateWindow(capturedWindow, capturedProcessId, ActiveMonitorHandle);
                };
                _attentionPanel.Children.Add(button);
            }
            if (overflow > 0)
            {
                var overflowText = new TextBlock
                {
                    Text = $"+{overflow}",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 8, 0)
                };
                overflowText.SetResourceReference(TextBlock.ForegroundProperty, "TopBarAttentionBrush");
                _attentionPanel.Children.Add(overflowText);
            }
        }
    }

    private ImageSource? ResolveAttentionIcon(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        if (_attentionIcons.TryGetValue(executablePath, out var cached))
        {
            return cached;
        }

        if (_attentionIconsLoading.Add(executablePath))
        {
            var path = executablePath;
            _ = Task.Run(() => ShellIconProvider.GetIcon(path)).ContinueWith(
                task =>
                {
                    var loaded = task.IsCompletedSuccessfully ? task.Result : null;
                    if (_closing)
                    {
                        return;
                    }
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (_closing)
                        {
                            return;
                        }
                        _attentionIconsLoading.Remove(path);
                        if (_attentionIcons.Count >= 64)
                        {
                            _attentionIcons.Clear();
                        }
                        _attentionIcons[path] = loaded;
                        if (loaded is not null)
                        {
                            RefreshAttention();
                        }
                    });
                },
                TaskScheduler.Default);
        }

        return null;
    }

    private static (string Name, string? ExecutablePath) ResolveAttentionTarget(
        IReadOnlyList<TrackedWindow> running,
        IReadOnlyList<WindowGroup> groups,
        nint window,
        uint processId)
    {
        var group = groups.FirstOrDefault(candidate =>
                candidate.Windows.Any(item => item.Handle == window))
            // A flash often names a hidden message window; its process names the app.
            ?? groups.FirstOrDefault(candidate =>
                processId != 0 && candidate.Windows.Any(item => item.ProcessId == processId));
        var executablePath = group?.Windows
                .Select(item => item.ExecutablePath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
            ?? (processId != 0 ? WindowCatalog.ExecutablePathOf(processId) : null);
        var fileDescription = !string.IsNullOrWhiteSpace(executablePath)
            && !DockAppResolver.IsFrameHost(executablePath)
            && !DockAppResolver.IsPackagedPath(executablePath)
                ? DockAppResolver.GetFileDescription(executablePath)
                : null;
        var trackedTitle = running.FirstOrDefault(candidate => candidate.Handle == window)?.Title;
        if (string.IsNullOrWhiteSpace(trackedTitle) && window != 0)
        {
            var buffer = new StringBuilder(128);
            if (DockNativeMethods.GetWindowTextW(window, buffer, buffer.Capacity) > 0)
            {
                trackedTitle = buffer.ToString().Trim();
            }
        }

        var name = AttentionFlashRules.ChooseDisplayName(
            fileDescription,
            group?.DisplayName,
            executablePath,
            trackedTitle);
        var iconPath = !string.IsNullOrWhiteSpace(executablePath)
            && !DockAppResolver.IsFrameHost(executablePath)
            && !DockAppResolver.IsPackagedPath(executablePath)
                ? executablePath
                : null;
        return (name, iconPath);
    }

    // ---------------------------------------------------------------- appearance

    private nint OnCompositionMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmDwmCompositionChanged)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, ReapplyAcrylicAfterEnvironmentChange);
        }
        return 0;
    }

    private void OnExplorerRestarted()
    {
        CloseTrayPanel();
        ReapplyAcrylicAfterEnvironmentChange();
        RenderModules();
    }

    private void ReapplyAcrylicAfterEnvironmentChange()
    {
        _lastRequestedAcrylicTint = null;
        RefreshAppearance();
    }

    /// <summary>Whether the requested acrylic backdrop is active.</summary>
    private bool _acrylicBackdrop;
    private uint? _lastRequestedAcrylicTint = 0;
    private uint _appliedAcrylicTint;
    private bool _acrylicRequestFailed;

    private bool ApplyAcrylicBackdrop(Color? tint, bool retryFailed)
    {
        // The WCA tint is ABGR. Cache the tint as well as the accent state so changes
        // to a frosted bar's color or opacity reach the native backdrop immediately.
        var nextTint = tint is { } color ? (uint)color.A << 24 | (uint)color.B << 16 | (uint)color.G << 8 | color.R : 0u;
        if (_handle != 0 && (nextTint != _lastRequestedAcrylicTint || (retryFailed && _acrylicRequestFailed)))
        {
            // Retry failed native calls when reapplying settings, never on idle foreground checks.
            _lastRequestedAcrylicTint = nextTint;
            var accent = nextTint == 0 ? TaskbarNativeMethods.AccentDisabled : TaskbarNativeMethods.AccentEnableAcrylicBlurBehind;
            _acrylicRequestFailed = !TaskbarNativeMethods.TrySetAccent(_handle, accent, nextTint);
            if (!_acrylicRequestFailed)
            {
                _appliedAcrylicTint = nextTint;
            }
            else if (_appliedAcrylicTint != 0
                && TaskbarNativeMethods.TrySetAccent(_handle, TaskbarNativeMethods.AccentDisabled, 0))
            {
                _appliedAcrylicTint = 0;
                _acrylicRequestFailed = nextTint != 0;
            }
        }
        return nextTint != 0 && !_acrylicRequestFailed && _appliedAcrylicTint == nextTint;
    }

    internal void RefreshAppearance()
    {
        if (_closing)
        {
            return;
        }

        RefreshPopupAppearance();
        ApplySurface(retryFailedAcrylic: true);
        UpdateBackdropSampling(immediate: true);
    }

    internal bool UsesDarkInk => _palette.UseDarkInk;

    private void OnHighContrastChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing) return;
            CloseTodoFlyout(animate: false);
            CloseClockFlyout();
            CloseInputMethodFlyout();
            CloseSystemFlyouts();
            RefreshAppearance();
        });

    private void ApplySurface(bool retryFailedAcrylic = false)
    {
        var highContrast = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast;
        var glass = DesktopStyleRules.TopBar(_style, _state);
        var surfaceBrush = FrozenBrush(glass.Surface);
        var acrylic = ApplyAcrylicBackdrop(!IsCapsule && !highContrast
            && DesktopStyleRules.TopBarFrostedScrim(_state, glass.Surface) ? surfaceBrush.Color : null, retryFailedAcrylic);
        _acrylicBackdrop = acrylic;
        Surface.BorderThickness = IsCapsule ? new Thickness(1) : new Thickness(0, 0, 0, 1);
        Surface.CornerRadius = IsCapsule ? new CornerRadius(20) : new CornerRadius(0);
        Surface.HorizontalAlignment = IsCapsule ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        Surface.VerticalAlignment = IsCapsule ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        Surface.Width = IsCapsule ? (_capsuleExpanded ? CapsuleExpandedWidth : _capsuleCompactWidth) : double.NaN;
        Surface.Height = IsCapsule ? (_capsuleExpanded ? _capsuleExpandedHeight : CapsuleHeight) : double.NaN;
        if (IsCapsule)
        {
            _backdropTimer.Stop();
            ModuleHeader.Effect = CenterHost.Effect = null;
            if (!_state.FrostedGlass)
            {
                _capsuleGlass?.Dispose();
                _capsuleGlass = null;
            }
            if (_capsuleGlass is null)
            {
                Surface.SetResourceReference(Border.BackgroundProperty, "FloatingGlassSurfaceBrush");
                Surface.SetResourceReference(Border.BorderBrushProperty, "FloatingGlassBorderBrush");
                if (_state.FrostedGlass)
                    _capsuleGlass = new DockGlassSurface(this, Surface, "FloatingGlassSurfaceBrush",
                        "FloatingGlassBorderBrush", "FloatingGlassCapsuleBrush");
            }
            _searchPopup?.ApplyLook(ResolveSearchLook());
            return;
        }
        _capsuleGlass?.Dispose();
        _capsuleGlass = null;
        if (highContrast || !BarAdaptiveInkRules.NeedsBackdropSampling(glass.Surface))
        {
            _sampledBackdrop = null;
            _lastAdaptiveDarkInk = null;
            _backdropTimer.Stop();
        }
        ApplyAdaptivePalette(glass, acrylic, highContrast);
        var surfaceColor = surfaceBrush.Color;
        // Keep the whole surface hit-testable while the accent supplies its tint.
        Surface.Background = highContrast ? SystemColors.WindowBrush
            : acrylic ? CreateFrozenColorBrush(Color.FromArgb(1, surfaceColor.R, surfaceColor.G, surfaceColor.B))
            : surfaceBrush;
        Surface.BorderBrush = highContrast
            ? SystemColors.WindowTextBrush
            : FrozenBrush(_palette.BarBorderColor);
        _searchPopup?.ApplyLook(ResolveSearchLook());
    }

    private void ApplyAdaptivePalette(DockGlass glass, bool acrylic, bool highContrast)
    {
        var palette = BarAdaptiveInkRules.ResolveTopBarPalette(
            glass,
            acrylic,
            _sampledBackdrop,
            _lastAdaptiveDarkInk);
        _palette = palette;
        _lastAdaptiveDarkInk = BarAdaptiveInkRules.NeedsBackdropSampling(glass.Surface)
            ? palette.UseDarkInk
            : null;
        ModuleHeader.Effect = CenterHost.Effect = !highContrast && _lastAdaptiveDarkInk is { } usesDarkInk
            ? usesDarkInk ? DarkInkShadow : LightInkShadow
            : null;

        if (highContrast)
        {
            Resources["TopBarInkBrush"] = SystemColors.WindowTextBrush;
            Resources["TopBarMutedInkBrush"] = SystemColors.WindowTextBrush;
            Resources["TopBarHoverBrush"] = SystemColors.HighlightBrush;
            Resources["TopBarPressedBrush"] = SystemColors.HighlightBrush;
            Resources["TopBarSearchBackgroundBrush"] = SystemColors.ControlBrush;
            Resources["TopBarSearchHoverBrush"] = SystemColors.HighlightBrush;
            Resources["TopBarSearchBorderBrush"] = SystemColors.WindowTextBrush;
            Resources["TopBarBadgeBackgroundBrush"] = SystemColors.ControlBrush;
            Resources["TopBarAttentionBrush"] = SystemColors.HighlightBrush;
            return;
        }

        Resources["TopBarInkBrush"] = FrozenBrush(palette.Foreground);
        Resources["TopBarMutedInkBrush"] = FrozenBrush(palette.MutedForeground);
        Resources["TopBarHoverBrush"] = FrozenBrush(palette.HoverBackground);
        Resources["TopBarPressedBrush"] = FrozenBrush(palette.PressedBackground);
        Resources["TopBarSearchBackgroundBrush"] = FrozenBrush(palette.SearchBackground);
        Resources["TopBarSearchHoverBrush"] = FrozenBrush(palette.SearchHoverBackground);
        Resources["TopBarSearchBorderBrush"] = FrozenBrush(palette.SearchBorder);
        Resources["TopBarBadgeBackgroundBrush"] = FrozenBrush(palette.BadgeBackground);
        Resources["TopBarAttentionBrush"] = FrozenBrush(palette.AttentionForeground);
    }

    private bool CanSampleBackdrop => !_closing && !IsCapsule && IsVisible && !_trueFullScreenForeground
        && !AccessibilityThemeManager.IsHighContrastEnabled && !SystemParameters.HighContrast;

    private bool IsBackdropSampleObstructed => HasAnyOpenFlyout || _searchPopup is not null || _menuOpen;

    private void UpdateBackdropSampling(bool immediate)
    {
        if (!CanSampleBackdrop)
        {
            _backdropTimer.Stop();
            return;
        }

        var glass = DesktopStyleRules.TopBar(_style, _state);
        if (!BarAdaptiveInkRules.NeedsBackdropSampling(glass.Surface))
        {
            _backdropTimer.Stop();
            return;
        }

        if (!_backdropTimer.IsEnabled)
        {
            _backdropTimer.Start();
        }
        if (immediate)
        {
            ScheduleBackdropSample();
        }
    }

    private void ScheduleBackdropSample()
    {
        if (!CanSampleBackdrop || IsBackdropSampleObstructed)
        {
            return;
        }

        var bounds = _appBar?.Bounds;
        if (bounds is not { HasArea: true } barBounds)
        {
            return;
        }

        var glass = DesktopStyleRules.TopBar(_style, _state);
        if (!BarAdaptiveInkRules.NeedsBackdropSampling(glass.Surface))
        {
            _backdropTimer.Stop();
            return;
        }

        if (Interlocked.CompareExchange(ref _backdropSampleInFlight, 1, 0) != 0)
        {
            return;
        }

        var surface = glass.Surface;
        var sampledAcrylic = _acrylicBackdrop;
        var sampledOverlap = _smartHideOverlappingWindow;
        Task.Run(() =>
        {
            try
            {
                if (_closing) return;
                var sampled = ScreenBackdropSampler.SampleTopBarBackdrop(barBounds, surface);
                if (_closing || sampled is null) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (!CanSampleBackdrop || IsBackdropSampleObstructed || _appBar?.Bounds != barBounds
                        || sampledOverlap != _smartHideOverlappingWindow) return;
                    var currentGlass = DesktopStyleRules.TopBar(_style, _state);
                    if (surface != currentGlass.Surface || sampledAcrylic != _acrylicBackdrop) return;
                    _sampledBackdrop = sampled;
                    var previousDark = _palette.UseDarkInk;
                    ApplyAdaptivePalette(currentGlass, _acrylicBackdrop, highContrast: false);
                    if (previousDark != _palette.UseDarkInk)
                    {
                        Surface.BorderBrush = FrozenBrush(_palette.BarBorderColor);
                    }
                });
            }
            finally
            {
                Interlocked.Exchange(ref _backdropSampleInFlight, 0);
            }
        });
    }

    /// <summary>Whether the last successful native accent command installed a tint.</summary>
    internal bool IsAcrylicSurface => _appliedAcrylicTint != 0;

    // ---------------------------------------------------------------- visibility

    private void UpdateForForeground()
    {
        CloseFlyoutOnOutsideClick();
        if (_appBar?.Monitor is not { } monitor || _closing)
        {
            return;
        }

        var liveTaskbar = TaskbarEdgeGuard.GetNativeTaskbarLayout(monitor.Bounds);
        if (liveTaskbar?.Edge != _nativeTaskbar?.Edge
            || liveTaskbar?.AutoHidden != _nativeTaskbar?.AutoHidden)
        {
            var autoHide = _autoHide;
            ApplyState();
            // Applying the new shell layout can close this bar when its monitor disconnected.
            if (_closing || _appBar?.Monitor is not { } currentMonitor)
            {
                return;
            }
            monitor = currentMonitor;
            liveTaskbar = _nativeTaskbar;
            _autoHide = autoHide;
        }

        if (_attention.OrderedWindows.Count > 0)
        {
            var activeWindow = DockNativeMethods.GetForegroundWindow();
            if (activeWindow != 0
                && !DockNativeMethods.IsIconic(activeWindow)
                && _attention.Cleared(activeWindow, EffectiveAttentionProcessId(activeWindow)))
            {
                RefreshAttention();
            }
        }

        if (_appBar.RefreshBottomDockOffset())
        {
            CloseFlyoutsBeforeOpening();
            CloseTrayPanel();
        }
        var bounds = _appBar.Bounds;
        var taskbarOverlaps = _state.ShowAtBottom && liveTaskbar is { Edge: DockEdge.Bottom, AutoHidden: true }
            && TaskbarEdgeGuard.IsVisibleTaskbarOverlapping(monitor, bounds);
        var foreground = ForegroundWindowRect(out var foregroundBounds, out var foregroundWindow);
        var hasBacking = TransientForegroundResolver.TryResolveBackingWindow(foregroundWindow, _handle, out var backing);
        // The keyboard layout follows the focused window, and the IME's own
        // Chinese/English toggle changes no window at all, so the module re-reads on
        // every fast tick instead of waiting for the slower system-info cadence.
        if (foregroundWindow != 0 && IsVisible && _modules.ContainsKey(TopBarModuleKind.InputMethod))
        {
            PollInputMethod();
        }

        var fullScreen = foreground && DockVacateRules.CoversMonitor(foregroundBounds, monitor.Bounds);
        var zoomed = foreground && foregroundWindow != 0 && DockNativeMethods.IsZoomed(foregroundWindow)
            && DockNativeMethods.MonitorFromWindow(foregroundWindow, DockNativeMethods.MonitorDefaultToNearest) == monitor.MonitorHandle;
        var foregroundOwnProcess = false;
        var hasStandardChrome = false;
        var isToolOrOverlay = false;
        var isTopmost = false;
        if (foreground && foregroundWindow != 0)
        {
            DockNativeMethods.GetWindowThreadProcessId(foregroundWindow, out var foregroundPid);
            foregroundOwnProcess = foregroundPid == (uint)Environment.ProcessId;
            if (!foregroundOwnProcess)
            {
                var exStyle = NativeMethods.GetExStyleOf(foregroundWindow);
                isToolOrOverlay = (exStyle & (NativeMethods.WsExToolWindow | NativeMethods.WsExTransparent)) != 0;
                isTopmost = (exStyle & NativeMethods.WsExTopmost) != 0;
                if (fullScreen && !zoomed)
                {
                    var style = (long)NativeMethods.GetWindowLongPtr(foregroundWindow, NativeMethods.GwlStyle);
                    var className = new StringBuilder(64);
                    DockNativeMethods.GetClassNameW(foregroundWindow, className, className.Capacity);
                    var isApplicationFrame = string.Equals(
                        className.ToString(),
                        "ApplicationFrameWindow",
                        StringComparison.Ordinal);
                    hasStandardChrome = !isApplicationFrame
                        && (style & NativeMethods.WsCaption) == NativeMethods.WsCaption
                        && (style & NativeMethods.WsThickFrame) != 0;
                }
            }
        }
        var directTrueFullScreen = DockVacateRules.IsTrueFullScreen(
            fullScreen && !foregroundOwnProcess,
            zoomed,
            hasStandardChrome,
            isToolOrOverlay,
            isTopmost);
        var backingFullScreen = !directTrueFullScreen
            && !foregroundOwnProcess
            && foregroundWindow != 0
            && FullScreenWatcher.IsTrueFullScreenContext(foregroundWindow, monitor.MonitorHandle);
        var trueFullScreen = DockVacateRules.IsEffectiveFullScreen(directTrueFullScreen, backingFullScreen);
        _trueFullScreenForeground = trueFullScreen;

        if (_appBar.IsReserved)
        {
            if (!_state.ShowAtBottom && !_observedWindowsSeeded)
            {
                SeedObservedTopWindows();
            }

            var nudgedAsynchronously = false;
            if (!_state.ShowAtBottom && foreground && foregroundWindow != 0)
            {
                nudgedAsynchronously = TryNudgeNewlyOpenedWindow(
                    foregroundWindow,
                    foregroundBounds,
                    bounds,
                    monitor,
                    zoomed,
                    fullScreen);
            }

            // A reserved bar rides above normal windows and yields beneath a full-screen
            // app or any foreground window that still intrudes into the top strip
            // (such as a borderless, elevated, or user-dragged window).
            var coveredByForeground = fullScreen
                || trueFullScreen
                || taskbarOverlaps
                || (!nudgedAsynchronously
                    && ((foreground && TopBarReserveRules.IsWindowCoveringReservedBar(foregroundBounds, bounds, BarEdge))
                        || (hasBacking && TopBarReserveRules.IsWindowCoveringReservedBar(backing.Bounds, bounds, BarEdge))));
            _appBar.SetFullScreenDetected(coveredByForeground);
            // A partially covered bar can still receive clicks on its exposed modules.
            if (trueFullScreen || taskbarOverlaps)
            {
                CloseAllOpenFlyouts(animateTodo: false);
            }
            if (!IsVisible && !coveredByForeground)
            {
                Show();
                _appBar.EnsureLayer();
            }
            return;
        }

        _appBar.SetFullScreenDetected(false);
        _foregroundTimer.Interval = trueFullScreen
            ? FullScreenCheckInterval
            : SmartHideCheckInterval;
        DockNativeMethods.GetCursorPos(out var cursor);
        var revealDepth = Math.Max(2, (int)Math.Round(2 * monitor.Dpi / 96d));
        var directOverlaps = foreground && foregroundBounds.Intersects(bounds);
        var backingOverlaps = hasBacking && backing.Bounds.Intersects(bounds);
        var backingZoomed = hasBacking && backing.IsZoomed && backing.MonitorHandle == monitor.MonitorHandle;
        var (overlaps, effectiveZoomed) = DockVacateRules.ResolveEffectiveSmartHideOverlap(
            directOverlaps,
            directOverlaps && zoomed,
            backingOverlaps,
            backingOverlaps && backingZoomed);
        PixelRect monitorCoveringBounds = default;
        if (!(overlaps && effectiveZoomed)
            && TransientForegroundResolver.TryFindCoveringWindowOnMonitor(
                monitor.MonitorHandle,
                bounds,
                _handle,
                out var monitorOverlaps,
                out var monitorMaximized,
                out monitorCoveringBounds,
                requireBarIntersection: true))
        {
            overlaps |= monitorOverlaps;
            effectiveZoomed |= monitorMaximized;
        }
        var revealBounds = IsCapsule ? bounds with
        {
            Top = Math.Max(monitor.Bounds.Top, bounds.Top - (int)Math.Round(6d * monitor.Dpi / 96d))
        } : bounds;
        var captionSourceBounds = foreground && directOverlaps
            ? foregroundBounds
            : hasBacking && backingOverlaps
                ? backing.Bounds
                : monitorCoveringBounds;
        var captionExclusionPx = !_state.ShowAtBottom && !IsCapsule && captionSourceBounds.HasArea
            ? (int)Math.Round(TopBarRevealRules.CaptionButtonsExclusionDip * monitor.Dpi / 96d)
            : 0;
        var excludedCaptionSpan = captionExclusionPx > 0
            ? TopBarRevealRules.ComputeCaptionButtonExclusion(captionSourceBounds, bounds, captionExclusionPx)
            : null;
        var pointerOverBar = TopBarRevealRules.IsPointerOverBar(
            cursor.X,
            cursor.Y,
            IsVisible,
            bounds);
        var inRawRevealZone = TopBarRevealRules.IsInRevealZone(
            cursor.X,
            cursor.Y,
            revealBounds,
            IsCapsule ? bounds.Top - revealBounds.Top + revealDepth : revealDepth,
            excludedCaptionSpan,
            BarEdge);
        var nowTick = Environment.TickCount64;
        var leftButtonDown = TopBarNativeMethods.IsKeyCurrentlyDown(TopBarNativeMethods.LeftMouseButton);
        var pointerInRevealZone = TopBarRevealRules.ShouldTriggerRevealAfterDwell(
            inRawRevealZone,
            overlaps,
            IsVisible,
            ref _revealZoneEnteredTick,
            leftButtonDown,
            nowTick);
        var input = new DockAutoHideInput(
            // Keep a hidden bar out of the way if the dragged window moves off the top strip.
            WindowOverlapsDock: overlaps || (leftButtonDown && !IsVisible),
            ForegroundIsFullScreen: trueFullScreen || taskbarOverlaps,
            PointerInRevealZone: pointerInRevealZone,
            PointerOverDock: pointerOverBar,
            InteractionActive: IsMouseCaptureWithin || _menuOpen || _capsuleExpanded
                || HasAnyOpenFlyout || _searchPopup is not null,
            OverlapIsMaximizedWindow: effectiveZoomed);
        _autoHide.Update(input, DateTimeOffset.UtcNow);
        var overlappingWhileShown = overlaps && _autoHide.IsShown;
        var overlapStateChanged = overlappingWhileShown != _smartHideOverlappingWindow;
        var visibilityChanged = _autoHide.IsShown != IsVisible;
        if (overlapStateChanged)
        {
            _smartHideOverlappingWindow = overlappingWhileShown;
            if (!overlappingWhileShown)
            {
                // Returning from an overlapping/maximized window back to the desktop wallpaper:
                // drop any stale adaptive-ink state so bright app tabs never leave the bar stuck dark.
                _sampledBackdrop = null;
                _lastAdaptiveDarkInk = null;
            }
        }
        if (visibilityChanged)
        {
            if (_autoHide.IsShown)
            {
                if (!_smartHideOverlappingWindow)
                {
                    _sampledBackdrop = null;
                    _lastAdaptiveDarkInk = null;
                }
                ApplySurface();
                Show();
                _appBar.EnsureLayer();
                RenderModules();
                RenderSystemModules();
                UpdateBackdropSampling(immediate: true);
            }
            else
            {
                if (_barMenu is { } openBarMenu)
                {
                    openBarMenu.IsOpen = false;
                }
                CloseAllOpenFlyouts();
                HideFromApp();
                if (overlapStateChanged)
                {
                    ApplySurface();
                }
            }
        }
        else if (overlapStateChanged && IsVisible)
        {
            ApplySurface();
            UpdateBackdropSampling(immediate: true);
        }
    }

    private bool ForegroundWindowRect(out PixelRect bounds, out nint window)
    {
        bounds = default;
        window = DockNativeMethods.GetForegroundWindow();
        if (window == 0 || window == _handle || window == (_barMenu?.PopupHandle ?? 0)
            || IsTrayPanelWindow(window) || DockNativeMethods.IsIconic(window))
        {
            return false;
        }

        var className = new StringBuilder(64);
        DockNativeMethods.GetClassNameW(window, className, className.Capacity);
        if (ShellSurfaceWindows.IsShellSurface(className.ToString()))
        {
            return false;
        }

        if (DockNativeMethods.DwmGetWindowAttribute(
                window,
                DockNativeMethods.DwmwaExtendedFrameBounds,
                out DockNativeMethods.NativeRect rectangle,
                Marshal.SizeOf<DockNativeMethods.NativeRect>()) != 0
            && !DockNativeMethods.GetWindowRect(window, out rectangle))
        {
            return false;
        }

        bounds = new PixelRect(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
        return true;
    }

    private void SeedObservedTopWindows()
    {
        _observedWindowsSeeded = true;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (hwnd != 0 && NativeMethods.IsWindowVisible(hwnd))
            {
                _observedTopWindows.Add(hwnd);
            }
            return true;
        }, 0);
    }

    private bool TryNudgeNewlyOpenedWindow(
        nint window,
        PixelRect visibleBounds,
        PixelRect barBounds,
        MonitorSnapshot monitor,
        bool isMaximized,
        bool isFullScreen)
    {
        if (!_observedTopWindows.Add(window))
        {
            return false;
        }

        if (_observedTopWindows.Count > 512)
        {
            _observedTopWindows.RemoveWhere(candidate => !DockNativeMethods.IsWindow(candidate));
        }

        DockNativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == (uint)Environment.ProcessId)
        {
            return false;
        }

        if (!DockNativeMethods.GetWindowRect(window, out var nativeRect))
        {
            return false;
        }

        var style = (long)NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlStyle);
        var exStyle = (long)NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlExStyle);
        var hasCaption = (style & NativeMethods.WsCaption) == NativeMethods.WsCaption;
        var isToolOrPopup = (exStyle & NativeMethods.WsExToolWindow) != 0
            || ((style & NativeMethods.WsPopup) != 0 && !hasCaption);
        var isTopmost = (exStyle & NativeMethods.WsExTopmost) != 0;
        var hasOwner = NativeMethods.GetWindow(window, NativeMethods.GwOwner) != 0;
        var windowRect = new PixelRect(nativeRect.Left, nativeRect.Top, nativeRect.Right, nativeRect.Bottom);
        var workArea = BingLan.App.Dock.DockAppBarController.GetLiveWorkingArea(monitor);
        var nudged = TopBarReserveRules.ComputeNudgedWindowRect(
            visibleBounds,
            windowRect,
            barBounds,
            workArea,
            isMaximized,
            isFullScreen,
            isTopmost,
            isToolOrPopup,
            hasOwner,
            hasCaption);
        if (nudged is not { } target)
        {
            return false;
        }

        var flags = NativeMethods.SwpNoZOrder
            | NativeMethods.SwpNoActivate
            | DockNativeMethods.SwpAsyncWindowPos;
        if (target.Width == windowRect.Width && target.Height == windowRect.Height)
        {
            flags |= NativeMethods.SwpNoSize;
        }

        if (!NativeMethods.SetWindowPos(window, 0, target.Left, target.Top, target.Width, target.Height, flags))
        {
            return false;
        }

        // Re-verify once after the target thread applies the async move; if it ignored or
        // clamped the move back into the bar, Layer 2 yields the bar beneath it.
        _nudgeRecheckTimer.Stop();
        _nudgeRecheckTimer.Start();
        return true;
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();
        var search = new MenuItem { Header = "搜索应用、文件…" };
        search.Click += (_, _) => Dispatcher.BeginInvoke(TriggerSearch, DispatcherPriority.Input);
        if (IsSearchEntryEnabled) menu.Items.Add(search);
        var settings = new MenuItem { Header = "在冰蓝桌面中设置…" };
        settings.Click += (_, _) => _environment.OpenTopBarSettings();
        var exit = new MenuItem { Header = "退出冰蓝桌面" };
        exit.Click += (_, _) => _environment.ExitApp();
        menu.Items.Add(settings);
        menu.Items.Add(exit);
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        ContextMenu = menu;

        ContextMenuOpening += (_, args) => args.Handled = true;
        Surface.MouseRightButtonUp += (_, args) =>
        {
            args.Handled = true;
            var hitModule = HitTestModule(args.OriginalSource as DependencyObject);
            Dispatcher.BeginInvoke(() => OpenBarContextMenu(hitModule), DispatcherPriority.Input);
        };
    }

    private TopBarModuleKind? HitTestModule(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, _attentionPanel))
            {
                return TopBarModuleKind.Attention;
            }
            foreach (var (kind, element) in _modules)
            {
                if (ReferenceEquals(current, element))
                {
                    return kind;
                }
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void OpenBarContextMenu(TopBarModuleKind? hitModule = null)
    {
        if (_closing || !IsVisible)
        {
            return;
        }

        if (_barMenu is { IsOpen: true } existing)
        {
            existing.IsOpen = false;
        }
        CloseTrayPanel();
        var highContrast = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast;
        var darkSurface = !ResolveSystemFlyoutLook(highContrast).Palette.UseDarkInk;
        var menu = new NonActivatingPopupMenu(Surface, PlacementMode.Mouse, darkSurface);
        if (!IsCapsule && hitModule is { } kind)
        {
            AddModuleQuickActions(menu, kind);
        }
        if (IsCapsule && IsSearchEntryEnabled)
            menu.AddItem("搜索应用、文件…", () => Dispatcher.BeginInvoke(TriggerSearch, DispatcherPriority.Input));
        menu.AddItem("在冰蓝桌面中设置…", () => _environment.OpenTopBarSettings(hitModule));
        menu.AddItem("退出冰蓝桌面", _environment.ExitApp);
        menu.Opened += (_, _) =>
        {
            _menuOpen = true;
            _barMenu = menu;
        };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_barMenu, menu))
            {
                _menuOpen = false;
                _barMenu = null;
            }
        };
        menu.IsOpen = true;
    }

    private void AddModuleQuickActions(NonActivatingPopupMenu menu, TopBarModuleKind kind)
    {
        switch (kind)
        {
            case TopBarModuleKind.Volume:
                var volIconOnly = TopBarRules.EffectiveVolumeStyle(_state) == TopBarVolumeStyle.IconOnly;
                menu.AddItem(volIconOnly ? "音量：显示百分比" : "音量：仅显示图标", () =>
                {
                    _state.VolumeStyle = volIconOnly ? TopBarVolumeStyle.IconAndPercent : TopBarVolumeStyle.IconOnly;
                    ApplyTopBarQuickEdit();
                });
                menu.AddItem("打开 Windows 快速设置", () => _environment.OpenQuickSettings(TopBarModuleKind.Volume));
                break;
            case TopBarModuleKind.Network:
                var netIconOnly = TopBarRules.EffectiveNetworkDisplayMode(_state) == TopBarNetworkDisplayMode.IconOnly;
                menu.AddItem(netIconOnly ? "网络：显示文字" : "网络：仅显示图标", () =>
                {
                    _state.NetworkIconOnly = !netIconOnly;
                    ApplyTopBarQuickEdit();
                });
                menu.AddItem("打开 Windows 快速设置", () => _environment.OpenQuickSettings(TopBarModuleKind.Network));
                break;
            case TopBarModuleKind.Battery:
                var batIconOnly = TopBarRules.EffectiveBatteryStyle(_state) == TopBarBatteryStyle.IconOnly;
                menu.AddItem(batIconOnly ? "电量：显示百分比" : "电量：仅显示图标", () =>
                {
                    _state.BatteryStyle = batIconOnly ? TopBarBatteryStyle.IconAndPercent : TopBarBatteryStyle.IconOnly;
                    ApplyTopBarQuickEdit();
                });
                menu.AddItem("打开 Windows 快速设置", () => _environment.OpenQuickSettings(TopBarModuleKind.Battery));
                break;
            case TopBarModuleKind.InputMethod:
                var imeBadgeOnly = TopBarRules.EffectiveInputMethodStyle(_state) == TopBarInputMethodStyle.BadgeOnly;
                menu.AddItem(imeBadgeOnly ? "输入法：显示名称" : "输入法：仅显示徽标", () =>
                {
                    _state.InputMethodStyle = imeBadgeOnly ? TopBarInputMethodStyle.BadgeAndName : TopBarInputMethodStyle.BadgeOnly;
                    ApplyTopBarQuickEdit();
                });
                break;
            case TopBarModuleKind.Weather:
                var weatherStyle = TopBarRules.EffectiveWeatherStyle(_state);
                var (nextWeatherLabel, nextWeatherStyle) = weatherStyle switch
                {
                    TopBarWeatherStyle.Compact => ("天气：显示温度与天气", TopBarWeatherStyle.TempAndCondition),
                    TopBarWeatherStyle.TempAndCondition => ("天气：显示城市与完整信息", TopBarWeatherStyle.Full),
                    _ => ("天气：仅显示温度", TopBarWeatherStyle.Compact)
                };
                menu.AddItem(nextWeatherLabel, () =>
                {
                    _state.WeatherStyle = nextWeatherStyle;
                    ApplyTopBarQuickEdit();
                });
                break;
            case TopBarModuleKind.Performance:
                var perfStyle = TopBarRules.EffectivePerformanceStyle(_state);
                var (nextPerfLabel, nextPerfStyle) = perfStyle switch
                {
                    TopBarPerformanceStyle.CpuOnly => ("性能：显示 CPU 与下载速率", TopBarPerformanceStyle.Compact),
                    TopBarPerformanceStyle.Compact => ("性能：显示全部指标", TopBarPerformanceStyle.Full),
                    _ => ("性能：仅显示 CPU", TopBarPerformanceStyle.CpuOnly)
                };
                menu.AddItem(nextPerfLabel, () =>
                {
                    _state.PerformanceStyle = nextPerfStyle;
                    ApplyTopBarQuickEdit();
                });
                break;
            case TopBarModuleKind.Clock:
                var clockStyle = TopBarRules.EffectiveClockStyle(_state);
                var (nextClockLabel, nextClockStyle) = clockStyle switch
                {
                    TopBarClockStyle.Compact => ("时钟：显示日期与时间", TopBarClockStyle.DateAndTime),
                    TopBarClockStyle.DateAndTime => ("时钟：完整显示（含农历）", TopBarClockStyle.Full),
                    _ => ("时钟：极简显示（周几 + 时间）", TopBarClockStyle.Compact)
                };
                menu.AddItem(nextClockLabel, () =>
                {
                    _state.ClockStyle = nextClockStyle;
                    ApplyTopBarQuickEdit();
                });
                break;
            case TopBarModuleKind.Attention:
                var attentionIconOnly = TopBarRules.EffectiveAttentionStyle(_state) == TopBarAttentionStyle.IconOnly;
                menu.AddItem(attentionIconOnly ? "消息提示：显示应用名称" : "消息提示：仅显示图标", () =>
                {
                    _state.AttentionStyle = attentionIconOnly
                        ? TopBarAttentionStyle.IconAndName
                        : TopBarAttentionStyle.IconOnly;
                    ApplyTopBarQuickEdit();
                });
                break;
            case TopBarModuleKind.TrayOverflow:
                var trayIconOnly = TopBarRules.EffectiveTrayIconOnly(_state);
                menu.AddItem(trayIconOnly ? "托盘：显示文字" : "托盘：仅显示图标", () =>
                {
                    _state.TrayIconOnly = !trayIconOnly;
                    ApplyTopBarQuickEdit();
                });
                break;
            case TopBarModuleKind.Search:
                var sideIcon = TopBarRules.EffectiveSearchPlacement(_state) == TopBarSearchPlacement.SideIcon;
                menu.AddItem(sideIcon ? "搜索：切换为居中搜索条" : "搜索：切换为放大镜图标", () =>
                {
                    _state.SearchPlacement = sideIcon ? TopBarSearchPlacement.CenterBar : TopBarSearchPlacement.SideIcon;
                    ApplyTopBarQuickEdit();
                });
                break;
        }

        var label = TopBarRules.ModuleLabel(kind);
        menu.AddItem($"隐藏「{label}」", () =>
        {
            TopBarRules.SetModule(_state.Modules, kind, false);
            ApplyTopBarQuickEdit();
        });
    }

    private void ApplyTopBarQuickEdit()
    {
        ApplyState();
        _environment.TopBarStateChanged();
    }
}
