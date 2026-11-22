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

    /// <summary>The cached weather, or null while there is no city or no data yet.</summary>
    WeatherSnapshot? ReadWeather();

    /// <summary>Kicks off a network refresh when the service's own cadence says it is due.</summary>
    void RefreshWeatherIfDue();

    int CountIncompleteTodos();

    /// <summary>The first to-do card's live items, for the to-do flyout.</summary>
    TopBarTodoList? ReadTodoList();

    /// <summary>An item was toggled in the flyout; persist at the host's pace.</summary>
    void TodoItemToggled();

    void OpenTopBarSettings();

    /// <summary>Opens the system quick-settings flyout (the taskbar tray's panel).</summary>
    void OpenQuickSettings(TopBarModuleKind kind);

    void OpenInputMethod();

    void OpenTrayOverflow();

    void CloseTrayOverflow();

    /// <summary>Opens Task Manager and lands on its performance page.</summary>
    void OpenTaskManagerPerformance();

    void OpenComponentSettings(DesktopComponentKind kind);

    /// <summary>Brings one window of an app that asked for attention to the front.</summary>
    bool ActivateWindow(nint handle, uint processId = 0);

    bool IsLauncherEnabled { get; }

    void ToggleLauncherPanel();

    void ExitApp();
}

/// <summary>
/// The list the to-do flyout shows: the first card's title and its live items. The
/// items are the card's own state objects, so toggling one in the flyout reaches the
/// card and the persistence through the same objects.
/// </summary>
internal sealed record TopBarTodoList(string Title, IReadOnlyList<TodoItemState> Items);

/// <summary>
/// The full-width strip at the top edge of one monitor. It only shows the app's own data
/// and read-only system status; clicking a module opens the matching settings page, and
/// an attention mark focuses that app. It never takes focus itself.
/// </summary>
public sealed partial class TopBarWindow : Window
{
    private static readonly TimeSpan SmartHideCheckInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan FullScreenCheckInterval = TimeSpan.FromMilliseconds(500);
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
    private TopBarDisplayMode DisplayMode => _enableExperimentalCapsule ? _state.DisplayMode : TopBarDisplayMode.FullWidth;
    private bool IsCapsule => DisplayMode == TopBarDisplayMode.Capsule;
    private long _lastCountdownMinute = -1;

    private readonly TopBarState _state;
    private readonly DesktopStyleState _style;
    private readonly ITopBarEnvironment _environment;
    private readonly IPerformanceSamplingService _sampler;
    private readonly StackPanel _attentionPanel = new()
    {
        Orientation = System.Windows.Controls.Orientation.Horizontal
    };
    private DockAutoHideState _autoHide = new();
    private readonly TopBarAttentionQueue _attention = new();
    private readonly Dictionary<nint, string> _attentionNames = [];
    private readonly WindowCatalog _catalog = new();
    private readonly DispatcherTimer _foregroundTimer;
    private readonly DispatcherTimer _ambientTimer;
    private readonly DispatcherTimer _minuteTimer;
    private readonly Dictionary<TopBarModuleKind, FrameworkElement> _modules = [];
    private TopBarAppBarController? _appBar;
    private PerformanceSnapshot? _lastSnapshot;
    private TopBarSystemFacts? _facts;
    private Popup? _todoFlyout;
    private Popup? _clockFlyout;
    private DateTime _clockMonth;
    private MonitorSnapshot? _monitor;
    private TaskbarEdgeGuard.NativeTaskbarLayout? _nativeTaskbar;
    private nint _handle;
    private uint _shellHookMessage;
    private bool _shellHookActive;
    private bool _closing;
    private bool _isChangingVisibilityByApp;
    private bool _trueFullScreenForeground;
    private bool _menuOpen;
    private NonActivatingPopupMenu? _barMenu;
    private int _tick;
    private TextBlock? _inputMethodBadgeText;
    private TextBlock? _inputMethodNameText;
    private nint _lastInputLayout;
    private string? _inputMethodName;
    private string _inputMethodToolTip = string.Empty;
    private string _inputMethodLabel = string.Empty;
    private int _imeReadInFlight;

    // ---------------------------------------------------------------- 搜索

    private const string GlyphSearch = "\uE721";
    private const double SearchBoxWidth = TopBarSearchRules.DefaultCenteredEntryWidth;
    private const int SearchHotkeyId = 0x5E41;

    private Button? _searchButton;
    private int _moduleMask = -1;
    private bool? _appliedAcrylic;
    private DockGlassSurface? _capsuleGlass;
    private bool _searchHotkeyRegistered;

    private bool IsSearchEntryEnabled =>
        TopBarRules.IsModuleOn(_state.Modules, TopBarModuleKind.Search) && _environment.IsLauncherEnabled;

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
        bool enableExperimentalCapsule = false)
    {
        _state = state;
        _style = style;
        _environment = environment;
        _sampler = sampler;
        _enableExperimentalCapsule = enableExperimentalCapsule;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
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
        _sampler.Sampled += OnSampled;
        // A network address change is the one system fact with an event source; the
        // others ride the sampling or ambient tick.
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        SystemEvents.TimeChanged += OnSystemTimeChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
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
        if (_moduleMask != moduleMask || _appliedDisplayMode != DisplayMode)
        {
            BuildModules();
            _moduleMask = moduleMask;
            _appliedDisplayMode = DisplayMode;
        }
        if (_searchButton is { } searchButton)
        {
            searchButton.Height = Math.Clamp(_state.Height - 10d, 18d, 30d);
            searchButton.ToolTip = string.IsNullOrWhiteSpace(_state.SearchHotkey)
                ? "搜索应用、文件"
                : $"搜索应用、文件（{_state.SearchHotkey}）";
        }
        ApplySurface();
        _autoHide = new DockAutoHideState();
        _nativeTaskbar = TaskbarEdgeGuard.GetNativeTaskbarLayout(_monitor.Bounds);
        // The top edge is taken (a taskbar moved to the top on Windows 10): behave as
        // smart-hide instead of fighting over the space. A bar that already holds the
        // edge keeps it — its own reservation is not a foreign occupier.
        var reserve = TopBarReserveRules.ShouldReserve(
            !IsCapsule && _state.VisibilityMode == TopBarVisibilityMode.ReserveTopEdge,
            _appBar?.IsReserved == true,
            _monitor.Bounds,
            BingLan.App.Dock.DockAppBarController.GetLiveWorkingArea(_monitor),
            nativeTopEdgeOccupied: _nativeTaskbar?.Edge == DockEdge.Top);
        _appBar?.Apply(_monitor, reserve,
            IsCapsule ? (_capsuleExpanded ? _capsuleExpandedHeight : CapsuleHeight) : _state.Height,
            IsCapsule ? (_capsuleExpanded ? CapsuleExpandedWidth : _capsuleCompactWidth) : null);
        // While a flyout is open the fast interval keeps its outside-click watch
        // prompt even in reserve mode.
        _foregroundTimer.Interval = reserve && _todoFlyout is null && _clockFlyout is null
            ? FullScreenCheckInterval
            : SmartHideCheckInterval;
        _foregroundTimer.Start();
        _ambientTimer.Start();
        ArmMinuteTimer();
        SyncSearchHotkey();
        RenderModules();
        RefreshSystemFacts();
    }

    private MonitorSnapshot? ResolveMonitor()
    {
        var monitors = MonitorCatalog.GetAll();
        return monitors.FirstOrDefault(candidate => string.Equals(
                candidate.DeviceName,
                _state.MonitorDeviceName,
                StringComparison.OrdinalIgnoreCase))
            ?? monitors.FirstOrDefault(candidate => candidate.IsPrimary)
            ?? monitors.FirstOrDefault();
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

        // The bar registers its own shell hook: the dock's hook lives on its window and
        // disappears with it, while the bar needs the flash signal either way.
        _shellHookMessage = DockNativeMethods.RegisterWindowMessageW("SHELLHOOK");
        if (DockNativeMethods.RegisterShellHookWindow(_handle))
        {
            _shellHookActive = true;
            HwndSource.FromHwnd(_handle)?.AddHook(OnShellHook);
        }

        _appBar = new TopBarAppBarController(this, _state.Height);
        _appBar.EnvironmentChanged += ApplyState;
        _appBar.ExplorerRestarted += RenderModules;
        ApplyAcrylicBackdrop();
        ApplyState();
    }

    internal event Action? ShellRecovered;

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
        _isChangingVisibilityByApp = true;
        try
        {
            Hide();
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

        var changed = (int)wParam switch
        {
            HshellFlash => MarkFlashed(lParam),
            HshellWindowActivated or HshellRudeAppActivated => ClearActivatedAttention(lParam),
            HshellWindowDestroyed => _attention.Closed(lParam),
            _ => false
        };
        if (changed)
        {
            RefreshAttention();
        }
        return 0;
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
        if (_barMenu is { } barMenu)
        {
            barMenu.IsOpen = false;
            _barMenu = null;
        }
        _environment.CloseTrayOverflow();
        UnregisterSearchHotkey();
        CloseTodoFlyout(animate: false);
        CloseClockFlyout();
        _foregroundTimer.Stop();
        _ambientTimer.Stop();
        _minuteTimer.Stop();
        _sampler.Sampled -= OnSampled;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        SystemEvents.TimeChanged -= OnSystemTimeChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_shellHookActive && _handle != 0)
        {
            DockNativeMethods.DeregisterShellHookWindow(_handle);
        }
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
        if (_closing || _sampler.IsRunning || !IsLoaded || !IsVisible)
        {
            return;
        }

        RefreshSystemFacts();
        _environment.RefreshWeatherIfDue();
        RenderModules();
    }

    private void OnSampled(PerformanceSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        // System facts are read off the UI thread: network enumeration and the volume COM
        // call are not free. The tick counter keeps them on their slower cadence.
        TopBarSystemFacts? facts = null;
        if (Interlocked.Increment(ref _tick) % SystemInfoTickDivisor == 0)
        {
            facts = ReadSystemFacts();
        }
        Dispatcher.BeginInvoke(() =>
        {
            // A smart-hidden bar is not on screen; its projection waits until it shows.
            if (!IsLoaded || !IsVisible || _closing)
            {
                return;
            }
            if (facts is not null)
            {
                _facts = facts;
                RenderSystemModules();
            }
            _environment.RefreshWeatherIfDue();
            RenderModules();
        });
    }

    private static TopBarSystemFacts ReadSystemFacts() => new(
        TopBarSystemInfo.ReadBattery(),
        TopBarSystemInfo.ReadVolumePercent(),
        TopBarSystemInfo.ReadNetwork());

    private void RefreshSystemFacts()
    {
        if (_closing)
        {
            return;
        }

        // The reads leave the UI thread: enumerating interfaces and the volume COM call
        // are not free, and ApplyState can run on every display or settings change.
        Task.Run(() =>
        {
            if (_closing)
            {
                return;
            }

            var facts = ReadSystemFacts();
            Dispatcher.BeginInvoke(() =>
            {
                if (_closing)
                {
                    return;
                }
                _facts = facts;
                if (IsLoaded)
                {
                    RenderSystemModules();
                }
            });
        });
    }

    // The input method leaves the background facts cadence: its TSF name read needs the
    // UI thread (STA), so the whole module rides the fast focus tick.
    private sealed record TopBarSystemFacts(
        (int Percent, TopBarBatteryStatus Status) Battery,
        int? VolumePercent,
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
        LeftModules.Children.Clear();
        RightModules.Children.Clear();
        CenterHost.Children.Clear();
        _modules.Clear();
        _searchButton = null;
        CloseTodoFlyout(animate: false);
        CloseClockFlyout();
        CapsuleHeader.Children.Clear();
        CapsuleHeader.Visibility = IsCapsule ? Visibility.Visible : Visibility.Collapsed;
        ModuleHeader.Visibility = IsCapsule ? Visibility.Collapsed : Visibility.Visible;
        _capsuleSummary = null;
        _capsuleStatus = null;
        _capsuleChevron = null;

        AddSearchModule();

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
                MinHeight = CapsuleHeight - 2,
                ToolTip = "今日待办，点击查看列表"
            };
            AutomationProperties.SetName(summary, "今日待办概要，点击查看列表");
            summary.Click += (_, _) => ToggleTodoFlyout(summary);
            CapsuleHeader.Children.Add(summary);
            _modules[TopBarModuleKind.TodoSummary] = summary;
            RenderModules();
            return;
        }
        CenterHost.Visibility = Visibility.Visible;

        // The to-do module shows its own flyout instead of opening settings.
        AddTodoModule(LeftModules);
        // The weather is a glance with hover details; it does nothing on click.
        AddModule(TopBarModuleKind.Weather, LeftModules, "天气", null, GlyphLocation);
        AddModule(TopBarModuleKind.Performance, LeftModules, "性能",
            _environment.OpenTaskManagerPerformance, GlyphDiagnostic);

        // Right side from the outer edge inwards: the docked-right stack fills from the
        // screen edge leftwards, so the clock is added last to sit at the corner. The
        // attention marks sit between the input method and the clock.
        // The battery, network and volume modules summon the system quick settings,
        // the same panel the taskbar's tray icons open.
        AddModule(TopBarModuleKind.Battery, RightModules, "电量",
            () => _environment.OpenQuickSettings(TopBarModuleKind.Battery), GlyphBatteryUnknown);
        AddModule(TopBarModuleKind.Network, RightModules, "网络",
            () => _environment.OpenQuickSettings(TopBarModuleKind.Network), GlyphWifiOff);
        AddModule(TopBarModuleKind.Volume, RightModules, "音量",
            () => _environment.OpenQuickSettings(TopBarModuleKind.Volume), GlyphVolume);
        if (TrayOverflowController.IsSupported)
        {
            if (!_state.Modules.TrayOverflow) _environment.CloseTrayOverflow();
            var visibleOnPress = false;
            AddModule(TopBarModuleKind.TrayOverflow, RightModules, "托盘", () =>
            {
                var wasVisibleOnPress = visibleOnPress;
                visibleOnPress = false;
                // If Explorer light-dismissed the XAML overflow island on mouse-down,
                // do not immediately reopen it on mouse-up.
                if (wasVisibleOnPress && !TrayOverflowController.IsVisible)
                {
                    return;
                }
                _environment.OpenTrayOverflow();
            }, "\uE70E");
            if (ModuleText(TopBarModuleKind.TrayOverflow) is { } trayText) trayText.Text = "托盘";
            if (_modules.TryGetValue(TopBarModuleKind.TrayOverflow, out var trayButton))
            {
                trayButton.ToolTip = "打开 Windows 隐藏的托盘图标";
                trayButton.PreviewMouseLeftButtonDown += (_, _) =>
                    visibleOnPress = TrayOverflowController.IsVisible;
                trayButton.MouseLeave += (_, _) => visibleOnPress = false;
            }
        }
        AddModule(TopBarModuleKind.InputMethod, RightModules, "输入法", _environment.OpenInputMethod);
        StyleInputMethodModule();
        if (TopBarRules.IsModuleOn(_state.Modules, TopBarModuleKind.Attention))
        {
            RightModules.Children.Add(_attentionPanel);
        }
        AddClockModule(RightModules);
        RefreshAttention();
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
            text.MaxWidth = 360;
            // The upload/download digits change width every second; reserving the widest
            // steady-state readout keeps the module from resizing, which would shuffle
            // the centred search box left and right once per tick.
            text.MinWidth = Math.Min(360d, MeasureTextWidth(
                "CPU 100%  内存 100%  ↑1023.9 MB/s ↓1023.9 MB/s"));
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
                MinHeight = _state.Height,
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
        button.Click += (_, _) => click();
        _modules[kind] = button;
        panel.Children.Add(button);
    }

    /// <summary>The bar's value text: white over the dark scrim, no drop shadow — a WPF
    /// Effect would switch the text to software rendering and lose ClearType.</summary>
    private static TextBlock CreateValueText() => new()
    {
        Foreground = Brushes.White,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };

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
    private static StackPanel CreateIconContent(string glyph, TextBlock value) => new()
    {
        Orientation = System.Windows.Controls.Orientation.Horizontal,
        VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            new TextBlock
            {
                FontFamily = StatusIconFont,
                FontSize = 14,
                Text = glyph,
                Tag = IconTag,
                Foreground = Brushes.White,
                Opacity = 0.92,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 1)
            },
            value
        }
    };

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
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 120,
            Margin = new Thickness(4, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        block.Content = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Children =
            {
                new Border
                {
                    CornerRadius = new CornerRadius(5),
                    Background = FrozenBrush("#33FFFFFF"),
                    Padding = new Thickness(6, 1, 6, 1),
                    Child = badgeText
                },
                nameText
            }
        };
        _inputMethodBadgeText = badgeText;
        _inputMethodNameText = nameText;
        var toolTip = CreateGlassToolTip();
        AttachHoverToolTip(block, toolTip);
        block.ToolTip = toolTip;
        ApplyInputMethodSnapshot(TopBarSystemInfo.ReadInputMethod());
    }

    /// <summary>
    /// Re-reads the input method on the fast focus tick off the UI thread so a busy or
    /// hung foreground IME window cannot stall the bar; the TSF name is queried on the
    /// UI thread only when the keyboard layout handle actually changes.
    /// </summary>
    private void PollInputMethod()
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
                if (snapshot.Layout == _lastInputLayout
                    && (!snapshot.ModeKnown || snapshot.Label == _inputMethodLabel))
                {
                    return;
                }
                Dispatcher.BeginInvoke(() =>
                {
                    if (!_closing) ApplyInputMethodSnapshot(snapshot);
                });
            }
            finally
            {
                Interlocked.Exchange(ref _imeReadInFlight, 0);
            }
        });
    }

    private void ApplyInputMethodSnapshot((nint Layout, string Label, string FullName, bool ModeKnown) snapshot)
    {
        var (layout, label, fullName, modeKnown) = snapshot;
        if (layout != _lastInputLayout)
        {
            _lastInputLayout = layout;
            _inputMethodName = TopBarSystemInfo.ReadInputMethodName();
            _inputMethodToolTip = _inputMethodName ?? fullName;
            _inputMethodLabel = label;
        }
        else if (modeKnown)
        {
            // A typing user keeps the IME window busy, so the mode call can miss its
            // timeout. Within one layout that means "no news": keep the last badge
            // instead of flashing the language-code fallback.
            _inputMethodLabel = label;
        }
        if (_inputMethodBadgeText is { } badge)
        {
            badge.Text = _inputMethodLabel;
        }
        if (_inputMethodNameText is { } name)
        {
            name.Text = _inputMethodName ?? string.Empty;
            name.Visibility = _inputMethodName is null
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

    private ToolTip CreateGlassToolTip()
    {
        var glass = DesktopStyleRules.TopBar(_style, _state);
        return new ToolTip
        {
            Background = FrozenBrush(glass.Surface),
            Foreground = Brushes.White,
            BorderBrush = FrozenBrush(glass.Border),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 9),
            StaysOpen = true
        };
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
            _clockFlyout.IsOpen = false;
            return;
        }

        _clockMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var glass = DesktopStyleRules.TopBar(_style, _state);
        var card = new Border
        {
            Background = FrozenBrush(glass.Surface),
            BorderBrush = FrozenBrush(glass.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 16, 14),
            Child = BuildClockCard()
        };
        var flyout = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -140,
            VerticalOffset = 2,
            StaysOpen = true,
            AllowsTransparency = true,
            Child = card
        };
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_clockFlyout, flyout))
            {
                _clockFlyout = null;
            }
        };
        RightModules.Children.Add(flyout);
        _clockFlyout = flyout;
        flyout.IsOpen = true;
        // Like the to-do flyout: the opening click must not read as an outside click.
        TopBarNativeMethods.ConsumeMouseButtonDown(TopBarNativeMethods.LeftMouseButton);
    }

    private FrameworkElement BuildClockCard()
    {
        var panel = new StackPanel { Width = 252 };
        var now = DateTime.Now;
        panel.Children.Add(new TextBlock
        {
            Text = now.ToString(_environment.Use24HourClock ? "HH:mm" : "hh:mm tt"),
            Foreground = Brushes.White,
            FontSize = 30
        });
        panel.Children.Add(new TextBlock
        {
            Text = now.Year + "年" + now.Month + "月" + now.Day + "日 " + "日一二三四五六"[(int)now.DayOfWeek],
            Foreground = Brushes.White,
            Opacity = 0.8,
            Margin = new Thickness(0, 2, 0, 10)
        });
        panel.Children.Add(BuildMonthView());
        return panel;
    }

    private FrameworkElement BuildMonthView()
    {
        var panel = new StackPanel();

        // Month title row with previous/next navigation.
        var title = new TextBlock
        {
            Text = MonthTitle(_clockMonth),
            Foreground = Brushes.White,
            FontSize = 13,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        var previous = CreateMonthNavButton("‹", "上一月", -1);
        var next = CreateMonthNavButton("›", "下一月", 1);
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
        for (var row = 0; row < 7; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
        }

        // Monday-first weeks, the calendar convention in Chinese layouts.
        var weekdayNames = new[] { "一", "二", "三", "四", "五", "六", "日" };
        for (var column = 0; column < 7; column++)
        {
            var headerCell = new TextBlock
            {
                Text = weekdayNames[column],
                Foreground = Brushes.White,
                Opacity = 0.6,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            Grid.SetRow(headerCell, 0);
            Grid.SetColumn(headerCell, column);
            grid.Children.Add(headerCell);
        }

        var daysInMonth = DateTime.DaysInMonth(_clockMonth.Year, _clockMonth.Month);
        var leading = ((int)_clockMonth.DayOfWeek + 6) % 7; // Monday-based offset
        for (var day = 1; day <= daysInMonth; day++)
        {
            var index = leading + day - 1;
            var row = index / 7 + 1;
            var column = index % 7;
            var date = new DateTime(_clockMonth.Year, _clockMonth.Month, day);
            FrameworkElement cell;
            if (date == DateTime.Today)
            {
                cell = new Border
                {
                    Background = TodayBrush,
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = day.ToString(),
                        Foreground = Brushes.White,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
            }
            else
            {
                cell = new TextBlock
                {
                    Text = day.ToString(),
                    Foreground = Brushes.White,
                    Opacity = 0.85,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
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
        var brush = new SolidColorBrush(Color.FromArgb(140, 122, 180, 237));
        brush.Freeze();
        return brush;
    }

    private Button CreateMonthNavButton(string glyph, string automationName, int months)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = glyph,
                Foreground = Brushes.White,
                FontSize = 16
            },
            Style = (Style)Resources["ModuleButton"],
            MinWidth = 28,
            Padding = new Thickness(4, 0, 4, 0)
        };
        System.Windows.Automation.AutomationProperties.SetName(button, automationName);
        button.Click += (_, _) => ShiftMonth(months);
        return button;
    }

    private void ShiftMonth(int months)
    {
        _clockMonth = _clockMonth.AddMonths(months);
        if (_clockFlyout?.Child is Border card)
        {
            card.Child = BuildClockCard();
        }
    }

    private static string MonthTitle(DateTime month) =>
        month.Year + "年" + month.Month + "月";

    private void CloseClockFlyout()
    {
        _clockFlyout?.SetCurrentValue(Popup.IsOpenProperty, false);
        _clockFlyout = null;
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
            _todoFlyout.IsOpen = false;
            return;
        }

        var glass = DesktopStyleRules.TopBar(_style, _state);
        var list = _environment.ReadTodoList();
        var items = list?.Items ?? [];
        var panel = new StackPanel { MaxWidth = 300 };

        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(list?.Title) ? "待办" : list!.Title,
            Foreground = Brushes.White,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 6)
        });
        if (items.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "暂无待办",
                Foreground = Brushes.White,
                Opacity = 0.7
            });
        }
        else
        {
            foreach (var item in items)
            {
                panel.Children.Add(CreateTodoRow(item));
            }
        }

        var card = new Border
        {
            Background = FrozenBrush(glass.Surface),
            BorderBrush = FrozenBrush(glass.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 12),
            Child = panel
        };
        // The flyout owns its closing: a StaysOpen popup drops cross-process clicks
        // silently and swallows in-thread ones through capture, so the bar closes it
        // from its own poll instead.
        var flyout = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -6,
            VerticalOffset = 2,
            StaysOpen = true,
            AllowsTransparency = true,
            Child = card
        };
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_todoFlyout, flyout))
            {
                _todoFlyout = null;
            }
        };
        // A popup outside the visual tree never hears outside clicks at all, so it
        // lives in the module panel; it takes no layout space while closed.
        LeftModules.Children.Add(flyout);
        _todoFlyout = flyout;
        flyout.IsOpen = true;
        // The click that opened the flyout must not read as the next outside click.
        TopBarNativeMethods.ConsumeMouseButtonDown(TopBarNativeMethods.LeftMouseButton);
    }

    private FrameworkElement CreateTodoRow(TodoItemState item)
    {
        var glyph = new TextBlock
        {
            Width = 18,
            Foreground = Brushes.White,
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
                Foreground = Brushes.White
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
            Foreground = Brushes.White
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
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, Opacity = 0.7, FontSize = 11,
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
            toolTip.PlacementTarget = host;
            toolTip.Placement = PlacementMode.Bottom;
            toolTip.VerticalOffset = 4;
            toolTip.IsOpen = true;
        };
        host.MouseEnter += (_, _) => openTimer.Start();
        host.MouseLeave += (_, _) =>
        {
            openTimer.Stop();
            toolTip.IsOpen = false;
        };
    }

    private void ToggleCapsuleDetails()
    {
        if (_capsuleExpanded)
        {
            CloseTodoFlyout();
            return;
        }
        var now = DateTime.Now;
        var items = (_environment.ReadTodoList()?.Items ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Text) && TodoService.BelongsToTodayList(item, now))
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
    }

    /// <summary>
    /// A StaysOpen=false popup never hears clicks that land in other processes: the
    /// system drops the capture instead of telling the popup. While the flyout is open
    /// the bar polls the button state and closes it when a click lands outside it.
    /// </summary>
    private void CloseFlyoutOnOutsideClick()
    {
        if (_capsuleExpanded && TopBarNativeMethods.IsMouseButtonDown(TopBarNativeMethods.EscapeKey))
        {
            CloseTodoFlyout();
        }
        if (!TopBarNativeMethods.IsMouseButtonDown(TopBarNativeMethods.LeftMouseButton))
        {
            return;
        }

        DockNativeMethods.GetCursorPos(out var cursor);
        if (_capsuleExpanded && _appBar is { } appBar
            && !(cursor.X >= appBar.Bounds.Left && cursor.X < appBar.Bounds.Right
                && cursor.Y >= appBar.Bounds.Top && cursor.Y < appBar.Bounds.Bottom))
        {
            CloseTodoFlyout();
        }
        TryCloseOnOutsideClick(_todoFlyout, cursor);
        TryCloseOnOutsideClick(_clockFlyout, cursor);
    }

    private void TryCloseOnOutsideClick(Popup? flyout, DockNativeMethods.NativePoint cursor)
    {
        if (flyout is not { IsOpen: true } || flyout.Child is not FrameworkElement child)
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
            : flyout == _clockFlyout ? TopBarModuleKind.Clock : null;
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

        var content = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = GlyphSearch,
                    FontFamily = StatusIconFont,
                    FontSize = 12,
                    Foreground = Brushes.White,
                    Opacity = 0.72,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 1)
                },
                new TextBlock
                {
                    Text = "搜索应用、文件…",
                    FontSize = 12,
                    Foreground = Brushes.White,
                    Opacity = 0.72,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };

        var button = new Button
        {
            Style = (Style)Resources["SearchEntryButton"],
            Width = SearchBoxWidth,
            MinWidth = TopBarSearchRules.MinCenteredEntryWidth,
            Height = Math.Clamp(_state.Height - 10d, 18d, 30d),
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = true,
            ToolTip = $"搜索应用、文件（{_state.SearchHotkey}）",
            Content = content
        };
        AutomationProperties.SetName(button, "搜索入口");
        button.Click += (_, _) => Dispatcher.BeginInvoke(TriggerSearch, DispatcherPriority.Input);

        _searchButton = button;
        _modules[TopBarModuleKind.Search] = button;
        CenterHost.Children.Add(button);
        UpdateSearchHostWidth();
    }

    /// <summary>
    /// Clamps the centered entry button to the symmetric clear room left by the wider
    /// side module so it never overlaps either side; collapses the button only when even
    /// the compact minimum width would collide (the global hotkey remains active).
    /// </summary>
    private void UpdateSearchHostWidth()
    {
        if (IsCapsule || _searchButton is not { } button || Surface.ActualWidth <= 0)
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
        if (_closing || !IsSearchEntryEnabled)
        {
            return;
        }
        if (IsCapsule)
        {
            UpdateForForeground();
            if (_trueFullScreenForeground) return;
        }
        CloseTodoFlyout(animate: false);
        CloseClockFlyout();
        _environment.ToggleLauncherPanel();
    }



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
            clock.Text = FormatClock(_environment.Use24HourClock);
        }
        if (ModuleText(TopBarModuleKind.TodoSummary) is { } todo)
        {
            if (IsCapsule)
            {
                var now = DateTime.Now;
                var items = _environment.ReadTodoList()?.Items ?? [];
                var next = TopBarModuleRules.NextTimedTodoToday(items, now);
                var count = items.Count(item => item.Kind == TodoItemKind.Todo && !item.IsCompleted
                    && !string.IsNullOrWhiteSpace(item.Text) && TodoService.BelongsToTodayList(item, now));
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
        if (ModuleText(TopBarModuleKind.Performance) is { } performance)
        {
            var snapshot = _lastSnapshot;
            performance.Text = snapshot is null
                ? "性能 …"
                : $"CPU {PerformanceSamplingRules.FormatPercent(snapshot.CpuPercent)}  "
                    + $"内存 {PerformanceSamplingRules.FormatPercent(snapshot.MemoryPercent)}  "
                    + $"↑{PerformanceSamplingRules.FormatRate(snapshot.UploadBytesPerSecond)} "
                    + $"↓{PerformanceSamplingRules.FormatRate(snapshot.DownloadBytesPerSecond)}";
        }

        // System facts change slowly and some calls are not free, so they refresh on a
        // slower cadence than the clock; the input method also follows focus changes.
        if (_tick % SystemInfoTickDivisor == 0)
        {
            RenderSystemModules();
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
            battery.Text = percent < 0 ? "—" : $"{percent}%";
            if (ModuleIcon(TopBarModuleKind.Battery) is { } batteryIcon)
            {
                batteryIcon.Text = percent < 0
                    ? GlyphBatteryUnknown
                    : BatteryGlyph(percent, status == TopBarBatteryStatus.Charging);
            }
        }
        if (ModuleText(TopBarModuleKind.Volume) is { } volume)
        {
            var percent = facts.VolumePercent;
            volume.Text = percent is { } value ? $"{value}%" : "—";
            if (ModuleIcon(TopBarModuleKind.Volume) is { } volumeIcon)
            {
                volumeIcon.Text = percent switch
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
            network.Text = TopBarModuleRules.FormatNetworkLabel(
                facts.Network,
                _state.ShowWifiName == true);
            if (_modules.TryGetValue(TopBarModuleKind.Network, out var networkButton))
            {
                networkButton.ToolTip = facts.Network.Tooltip;
            }
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
        if (snapshot is null)
        {
            text.Text = "天气 —";
            return;
        }

        // Online geocoding names like "南岗区 · 黑龙江省·哈尔滨市 · 中国" or "绥化市-黑龙江省绥化市-中国"
        // read as a trail; the bar shows the compact location only, and the tooltip keeps the full text.
        var city = WeatherSnapshot.CompactLocation(snapshot.City);
        text.Text = snapshot.Status == WeatherStatus.NoCity
            ? "天气 未设置"
            : snapshot.TemperatureCelsius is { } temperature
                ? $"{city} {Math.Round(temperature):0}° {snapshot.Condition}"
                : $"{city} {snapshot.Condition}";
        if (_modules.TryGetValue(TopBarModuleKind.Weather, out var module) && module.ToolTip is ToolTip toolTip)
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

    private static string FormatClock(bool use24HourClock)
    {
        var now = DateTime.Now;
        var date = $"{now.Month}月{now.Day}日 {"日一二三四五六"[(int)now.DayOfWeek]}";
        var time = use24HourClock ? now.ToString("HH:mm") : now.ToString("hh:mm tt");
        return $"{date}  {time}";
    }

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
            foreach (var stale in _attentionNames.Keys
                .Where(handle => !activeHandles.Contains(handle))
                .ToList())
            {
                _attentionNames.Remove(stale);
            }
            var groups = WindowGrouping.Group(running);
            var (shown, overflow) = TopBarAttentionQueue.ResolveDisplay(
                _attention.OrderedWindows,
                TopBarState.MaximumAttentionApps);

            _attentionPanel.Children.Clear();
            foreach (var window in shown)
            {
                var processId = _attention.ProcessOf(window);
                if (!_attentionNames.TryGetValue(window, out var name))
                {
                    name = ResolveAttentionName(running, groups, window, processId);
                    if (!string.Equals(name, "消息", StringComparison.Ordinal))
                    {
                        _attentionNames[window] = name;
                    }
                }

                var button = new Button
                {
                    Style = (Style)Resources["ModuleButton"],
                    Content = new TextBlock
                    {
                        Text = name,
                        Foreground = Brushes.Orange,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 120
                    },
                    Focusable = true,
                    Tag = window
                };
                System.Windows.Automation.AutomationProperties.SetName(button, $"{name}有新消息");
                var capturedWindow = window;
                var capturedProcessId = processId;
                button.Click += (_, _) =>
                {
                    if (_attention.Cleared(capturedWindow, capturedProcessId))
                    {
                        RefreshAttention();
                    }
                    _environment.ActivateWindow(capturedWindow, capturedProcessId);
                };
                _attentionPanel.Children.Add(button);
            }
            if (overflow > 0)
            {
                _attentionPanel.Children.Add(new TextBlock
                {
                    Text = $"+{overflow}",
                    Foreground = Brushes.Orange,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 8, 0)
                });
            }
        }
    }

    private static string ResolveAttentionName(
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

        return AttentionFlashRules.ChooseDisplayName(
            fileDescription,
            group?.DisplayName,
            executablePath,
            trackedTitle);
    }

    // ---------------------------------------------------------------- appearance

    /// <summary>Whether the WCA acrylic accent currently darkens this window.</summary>
    private bool _acrylicBackdrop;

    /// <summary>Whether the composition attribute accepted the acrylic accent at all.</summary>
    private bool _acrylicAvailable;

    // macOS's menu bar is blurred wallpaper under a dark scrim: the WCA acrylic accent
    // supplies the blur and the tint's alpha sets the scrim strength. Where the accent
    // is unavailable (older Windows, composition off) the theme's semi-transparent
    // surface brush stands in. ABGR: #141419 at 55%.
    private const uint AcrylicScrimAbgr = 0x8C191414u;
    private const string AcrylicSurfaceTint = "#0F141419";

    private void ApplyAcrylicBackdrop()
    {
        // Probe once: an unavailable composition attribute should not be retried on
        // every material change. Whether the scrim stays is decided per material in
        // ApplySurface, which disables the accent again for light or clear surfaces —
        // a scrim left applied under a nearly transparent bar turns it muddy grey.
        _acrylicAvailable = TaskbarNativeMethods.TrySetAccent(
            _handle,
            TaskbarNativeMethods.AccentEnableAcrylicBlurBehind,
            AcrylicScrimAbgr);
        ApplySurface();
    }

    private void ApplySurface()
    {
        var glass = DesktopStyleRules.TopBar(_style, _state);
        var acrylic = !IsCapsule && _acrylicAvailable
            && DesktopStyleRules.TopBarFrostedScrim(_state, glass.Surface);
        _acrylicBackdrop = acrylic;
        // The first call must also clear the startup probe on a light surface.
        if (_appliedAcrylic != acrylic && TaskbarNativeMethods.TrySetAccent(
            _handle,
            acrylic
                ? TaskbarNativeMethods.AccentEnableAcrylicBlurBehind
                : TaskbarNativeMethods.AccentDisabled,
            acrylic ? AcrylicScrimAbgr : 0))
            _appliedAcrylic = acrylic;
        Surface.BorderThickness = IsCapsule ? new Thickness(1) : new Thickness(0, 0, 0, 1);
        Surface.CornerRadius = IsCapsule ? new CornerRadius(20) : new CornerRadius(0);
        Surface.HorizontalAlignment = IsCapsule ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        Surface.VerticalAlignment = IsCapsule ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        Surface.Width = IsCapsule ? (_capsuleExpanded ? CapsuleExpandedWidth : _capsuleCompactWidth) : double.NaN;
        Surface.Height = IsCapsule ? (_capsuleExpanded ? _capsuleExpandedHeight : CapsuleHeight) : double.NaN;
        if (IsCapsule)
        {
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
            return;
        }
        _capsuleGlass?.Dispose();
        _capsuleGlass = null;
        Surface.Background = FrozenBrush(acrylic ? AcrylicSurfaceTint : glass.Surface);
        Surface.BorderBrush = FrozenBrush(glass.Border);
    }

    /// <summary>Whether the current surface decided to sit on the acrylic scrim.</summary>
    internal bool IsAcrylicSurface => _acrylicBackdrop;

    // ---------------------------------------------------------------- visibility

    private void UpdateForForeground()
    {
        CloseFlyoutOnOutsideClick();
        if (_appBar?.Monitor is not { } monitor || _closing)
        {
            return;
        }

        if (TaskbarEdgeGuard.GetNativeTaskbarLayout(monitor.Bounds) != _nativeTaskbar)
        {
            var autoHide = _autoHide;
            ApplyState();
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

        var bounds = _appBar.Bounds;
        var foreground = ForegroundWindowRect(out var foregroundBounds, out var foregroundWindow);
        // The keyboard layout follows the focused window, and the IME's own
        // Chinese/English toggle changes no window at all, so the module re-reads on
        // every fast tick instead of waiting for the slower system-info cadence.
        if (foregroundWindow != 0 && IsVisible && _modules.ContainsKey(TopBarModuleKind.InputMethod))
        {
            PollInputMethod();
        }

        var fullScreen = foreground
            && foregroundBounds.Left <= monitor.Bounds.Left
            && foregroundBounds.Top <= monitor.Bounds.Top
            && foregroundBounds.Right >= monitor.Bounds.Right
            && foregroundBounds.Bottom >= monitor.Bounds.Bottom;
        var zoomed = foreground && foregroundWindow != 0 && DockNativeMethods.IsZoomed(foregroundWindow);
        var trueFullScreen = DockVacateRules.IsTrueFullScreen(fullScreen, zoomed);
        _trueFullScreenForeground = trueFullScreen;

        if (_appBar.IsReserved)
        {
            // A reserved bar rides above normal windows and under a full-screen app,
            // like the taskbar; nothing else moves it.
            _appBar.SetFullScreenDetected(fullScreen);
            if (!IsVisible && !fullScreen)
            {
                Show();
                _appBar.EnsureLayer();
            }
            return;
        }

        _foregroundTimer.Interval = trueFullScreen
            ? FullScreenCheckInterval
            : SmartHideCheckInterval;
        DockNativeMethods.GetCursorPos(out var cursor);
        var revealDepth = Math.Max(2, (int)Math.Round(2 * monitor.Dpi / 96d));
        var pointerOverBar = IsVisible
            && cursor.X >= bounds.Left && cursor.X < bounds.Right
            && cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom;
        var overlaps = foreground && foregroundBounds.Intersects(bounds);
        var revealBounds = IsCapsule ? bounds with
        {
            Top = Math.Max(monitor.Bounds.Top, bounds.Top - (int)Math.Round(6d * monitor.Dpi / 96d))
        } : bounds;
        var input = new DockAutoHideInput(
            WindowOverlapsDock: overlaps,
            ForegroundIsFullScreen: trueFullScreen,
            PointerInRevealZone: TopBarRevealRules.IsInRevealZone(
                cursor.X,
                cursor.Y,
                revealBounds,
                IsCapsule ? bounds.Top - revealBounds.Top + revealDepth : revealDepth),
            PointerOverDock: pointerOverBar,
            InteractionActive: _menuOpen || _capsuleExpanded
                || _todoFlyout?.IsOpen == true || _clockFlyout?.IsOpen == true,
            // The immediate-collapse shortcut is the dock's; the bar keeps its debounce.
            OverlapIsMaximizedWindow: IsCapsule && zoomed);
        _autoHide.Update(input, DateTimeOffset.UtcNow);
        if (_autoHide.IsShown != IsVisible)
        {
            if (_autoHide.IsShown)
            {
                Show();
                RenderModules();
                RenderSystemModules();
            }
            else
            {
                if (_barMenu is { } openBarMenu)
                {
                    openBarMenu.IsOpen = false;
                }
                CloseTodoFlyout();
                CloseClockFlyout();
                HideFromApp();
            }
        }
    }

    private bool ForegroundWindowRect(out PixelRect bounds, out nint window)
    {
        bounds = default;
        window = DockNativeMethods.GetForegroundWindow();
        if (window == 0 || window == _handle || window == (_barMenu?.PopupHandle ?? 0) || DockNativeMethods.IsIconic(window))
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
            Dispatcher.BeginInvoke(OpenBarContextMenu, DispatcherPriority.Input);
        };
    }

    private void OpenBarContextMenu()
    {
        if (_closing || !IsVisible)
        {
            return;
        }

        if (_barMenu is { IsOpen: true } existing)
        {
            existing.IsOpen = false;
        }
        var surfaceColor = IsCapsule && TryFindResource("FloatingGlassSurfaceBrush") is SolidColorBrush material
            ? material.Color
            : (Color)ColorConverter.ConvertFromString(DesktopStyleRules.TopBar(_style, _state).Surface);
        var darkSurface = (0.2126d * surfaceColor.R + 0.7152d * surfaceColor.G + 0.0722d * surfaceColor.B) / 255d <= 0.6d;
        var menu = new NonActivatingPopupMenu(Surface, PlacementMode.Mouse, darkSurface);
        if (IsCapsule && IsSearchEntryEnabled)
            menu.AddItem("搜索应用、文件…", () => Dispatcher.BeginInvoke(TriggerSearch, DispatcherPriority.Input));
        menu.AddItem("在冰蓝桌面中设置…", _environment.OpenTopBarSettings);
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
}
