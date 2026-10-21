using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;
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
    bool ActivateWindow(nint handle);

    /// <summary>
    /// The desktop files and Start-menu programs the search box offers. Potentially
    /// slow: the caller runs it off the UI thread and refreshes per search session.
    /// </summary>
    IReadOnlyList<TopBarSearchCandidate> ReadSearchCandidates();

    /// <summary>Opens one search result with its default handler; failures stay quiet.</summary>
    void OpenSearchTarget(TopBarSearchCandidate candidate);

    void OpenSearchTargetLocation(TopBarSearchCandidate candidate);

    void SearchLayoutChanged();

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
    private readonly Dictionary<TopBarModuleKind, FrameworkElement> _modules = [];
    private TopBarAppBarController? _appBar;
    private PerformanceSnapshot? _lastSnapshot;
    private TopBarSystemFacts? _facts;
    private Popup? _todoFlyout;
    private Popup? _clockFlyout;
    private DateTime _clockMonth;
    private MonitorSnapshot? _monitor;
    private nint _handle;
    private uint _shellHookMessage;
    private bool _shellHookActive;
    private bool _closing;
    private bool _menuOpen;
    private int _tick;
    private TextBlock? _inputMethodBadgeText;
    private TextBlock? _inputMethodNameText;
    private nint _lastInputLayout;
    private string? _inputMethodName;
    private string _inputMethodToolTip = string.Empty;
    private string _inputMethodLabel = string.Empty;

    // ---------------------------------------------------------------- 搜索

    private const string GlyphSearch = "\uE721";
    private const double SearchBoxWidth = 260d;
    private const double SearchFlyoutWidth = 440d;
    private const int SearchFlyoutVisibleRows = 8;
    private const int SearchHotkeyId = 0x5E41;
    private static readonly TimeSpan SearchDebounceInterval = TimeSpan.FromMilliseconds(220);
    private static readonly string SearchSelectionBrushColor = "#7AB4ED";
    private static readonly SolidColorBrush SearchRowSelectedBrush = CreateFrozenColorBrush(Color.FromArgb(70, 122, 180, 237));

    private Border? _searchHost;
    private TextBox? _searchBox;
    private TextBlock? _searchPlaceholder;
    private Popup? _searchFlyout;
    private Border? _searchCard;
    private Panel? _searchRows;
    private TextBlock? _searchScope;
    private string? _searchRenderedQuery;
    private readonly DispatcherTimer _searchDebounce;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource?> _searchIconCache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<TopBarSearchCandidate> _searchCandidates = [];
    private bool _searchSourcesLoaded;
    private List<TopBarSearchCandidate> _searchResults = [];
    private readonly List<Border> _searchRowBorders = [];
    private int _searchSelected;
    private int _pressedSearchRowIndex = -1;
    private DockNativeMethods.NativePoint _lastSearchMousePoint;
    private int _searchGeneration;
    private CancellationTokenSource? _searchCancellation;
    private EverythingSearchClient? _everythingSearch;
    private SearchWindow? _searchWindow;
    // Keep at most one empty host after first use; no new host is created at startup.
    private SearchWindow? _idleSearchWindow;
    private bool _searchWindowHiddenForFullScreen;
    private int _moduleMask = -1;
    private SearchPresentation? _appliedSearchPresentation;
    private Button? _searchToggleButton;
    private System.Windows.Media.Brush _searchForeground = Brushes.White;
    private bool _searchComposing;
    private ContextMenu? _searchContextMenu;
    private (List<TopBarSearchCandidate> Results, string Query, string? Scope)? _pendingSearchResults;
    private bool? _appliedAcrylic;

    private Window SearchInputWindow => _searchWindow is { } window ? window : this;
    private nint SearchInputHandle => new WindowInteropHelper(SearchInputWindow).Handle;

    /// <summary>Whether a search session holds the bar activatable and the keyboard.</summary>
    private bool _searchActive;
    private nint _searchPreviousForeground;
    // The bar whose search session currently holds the foreground, for other windows
    // (the dock) that must keep evaluating the window the session borrowed it from.
    private static TopBarWindow? _searchSessionOwner;

    private bool _searchHotkeyRegistered;
    private string? _searchHotkeyRegisteredText;

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
        IPerformanceSamplingService sampler)
    {
        _state = state;
        _style = style;
        _environment = environment;
        _sampler = sampler;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        _foregroundTimer = new DispatcherTimer(
            FullScreenCheckInterval,
            DispatcherPriority.Background,
            (_, _) => UpdateForForeground(),
            Dispatcher);
        _sampler.Sampled += OnSampled;
        _searchDebounce = new DispatcherTimer(
            SearchDebounceInterval,
            DispatcherPriority.Background,
            (_, _) => OnSearchDebounceTick(),
            Dispatcher);
        // A network address change is the one system fact with an event source; the
        // others ride the sampling tick.
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
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
            (mask, kind) => mask | (TopBarRules.IsModuleOn(_state.Modules, kind) ? 1 << (int)kind : 0));
        if (_moduleMask != moduleMask)
        {
            BuildModules();
            _moduleMask = moduleMask;
        }
        else if (_appliedSearchPresentation != _state.SearchPresentation && _searchHost is not null)
        {
            ApplySearchPresentation();
        }
        ApplySearchAppearance();
        ApplySurface();
        _autoHide = new DockAutoHideState();
        // The top edge is taken (a taskbar moved to the top on Windows 10): behave as
        // smart-hide instead of fighting over the space. A bar that already holds the
        // edge keeps it — its own reservation is not a foreign occupier.
        var reserve = TopBarReserveRules.ShouldReserve(
            _state.VisibilityMode == TopBarVisibilityMode.ReserveTopEdge,
            _appBar?.IsReserved == true,
            _monitor.Bounds,
            BingLan.App.Dock.DockAppBarController.GetLiveWorkingArea(_monitor));
        _appBar?.Apply(_monitor, reserve, _state.Height);
        // While a flyout is open the fast interval keeps its outside-click watch
        // prompt even in reserve mode.
        _foregroundTimer.Interval = reserve && _todoFlyout is null && _searchFlyout is null
            ? FullScreenCheckInterval
            : SmartHideCheckInterval;
        _foregroundTimer.Start();
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

    private static nint PreventActivation(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmMouseActivate)
        {
            handled = true;
            return MaNoActivate;
        }
        return 0;
    }

    private nint OnShellHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_shellHookMessage == 0 || (uint)message != _shellHookMessage)
        {
            return 0;
        }

        var changed = (int)wParam switch
        {
            HshellFlash => _attention.Flashed(lParam),
            HshellWindowActivated or HshellRudeAppActivated or HshellWindowDestroyed => _attention.Cleared(lParam),
            _ => false
        };
        if (changed)
        {
            RefreshAttention();
        }
        return 0;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        _environment.CloseTrayOverflow();
        EndSearchSession();
        _everythingSearch?.Dispose();
        _searchCancellation?.Dispose();
        _searchWindow?.Shutdown();
        _searchWindow = null;
        _idleSearchWindow?.Shutdown();
        _idleSearchWindow = null;
        UnregisterSearchHotkey();
        CloseTodoFlyout();
        CloseClockFlyout();
        _foregroundTimer.Stop();
        _sampler.Sampled -= OnSampled;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        if (_shellHookActive && _handle != 0)
        {
            DockNativeMethods.DeregisterShellHookWindow(_handle);
        }
        _appBar?.Dispose();
        _appBar = null;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => RefreshSystemFacts();

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
        (bool Connected, string Label) Network);

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
        EndSearchSession();
        _searchWindow?.Shutdown();
        _searchWindow = null;
        _idleSearchWindow?.Shutdown();
        _idleSearchWindow = null;
        _searchHost = null;
        _searchWindowHiddenForFullScreen = false;
        _searchToggleButton = null;
        _appliedSearchPresentation = null;
        _searchBox = null;
        _searchPlaceholder = null;
        _searchFlyout = null;
        CloseTodoFlyout();
        CloseClockFlyout();

        AddSearchModule();

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
            AddModule(TopBarModuleKind.TrayOverflow, RightModules, "托盘", _environment.OpenTrayOverflow, "\uE70E");
            if (ModuleText(TopBarModuleKind.TrayOverflow) is { } trayText) trayText.Text = "托盘";
            if (_modules.TryGetValue(TopBarModuleKind.TrayOverflow, out var trayButton))
                trayButton.ToolTip = "打开 Windows 隐藏的托盘图标";
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
    }

    /// <summary>
    /// Re-reads the input method on the fast focus tick: the badge every call, the TSF
    /// name only when the layout handle changed, because switching input methods changes
    /// the layout while an in-method mode toggle does not.
    /// </summary>
    private void UpdateInputMethod()
    {
        var (layout, label, fullName, modeKnown) = TopBarSystemInfo.ReadInputMethod();
        if (_searchActive && _searchBox?.IsKeyboardFocused == true)
        {
            // WPF's TSF state is authoritative for our own text store. The legacy
            // IME window can keep reporting its last native mode after Shift.
            var input = InputMethod.Current;
            var native = input.ImeState == InputMethodState.On
                && (input.ImeConversionMode & ImeConversionModeValues.Native) != 0;
            label = TopBarModuleRules.DescribeInputMethod(layout, native ? 1 : 0);
            modeKnown = true;
        }
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
                Text = _inputMethodToolTip,
                Effect = CreateTextShadow()
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

        var text = new TextBlock
        {
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Effect = CreateTextShadow()
        };
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
            FontSize = 30,
            Effect = CreateTextShadow()
        });
        panel.Children.Add(new TextBlock
        {
            Text = now.Year + "年" + now.Month + "月" + now.Day + "日 " + "日一二三四五六"[(int)now.DayOfWeek],
            Foreground = Brushes.White,
            Opacity = 0.8,
            Margin = new Thickness(0, 2, 0, 10),
            Effect = CreateTextShadow()
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
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Effect = CreateTextShadow()
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
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Effect = CreateTextShadow()
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
                        VerticalAlignment = VerticalAlignment.Center,
                        Effect = CreateTextShadow()
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
                    VerticalAlignment = VerticalAlignment.Center,
                    Effect = CreateTextShadow()
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
                FontSize = 16,
                Effect = CreateTextShadow()
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
            Margin = new Thickness(0, 0, 0, 6),
            Effect = CreateTextShadow()
        });
        if (items.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "暂无待办",
                Foreground = Brushes.White,
                Opacity = 0.7,
                Effect = CreateTextShadow()
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
            Opacity = 0.7,
            Effect = CreateTextShadow()
        };
        var glyphStyle = new Style(typeof(TextBlock));
        glyphStyle.Setters.Add(new Setter(TextBlock.TextProperty, "○"));
        glyphStyle.Triggers.Add(TodoCompletedTrigger(
            new Setter(TextBlock.TextProperty, "✓"),
            new Setter(TextBlock.OpacityProperty, 0.9)));
        glyph.Style = glyphStyle;

        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 270,
            Foreground = Brushes.White,
            Effect = CreateTextShadow()
        };
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TodoItemState.Text)));
        var textStyle = new Style(typeof(TextBlock));
        textStyle.Triggers.Add(TodoCompletedTrigger(
            new Setter(TextBlock.TextDecorationsProperty, TextDecorations.Strikethrough),
            new Setter(TextBlock.OpacityProperty, 0.55)));
        text.Style = textStyle;

        var row = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 3, 0, 3),
            Cursor = System.Windows.Input.Cursors.Hand,
            DataContext = item
        };
        row.Children.Add(glyph);
        row.Children.Add(text);
        row.MouseLeftButtonUp += (_, _) =>
        {
            // One behaviour, like the card's checkbox: the row toggles completion.
            item.IsCompleted = !item.IsCompleted;
            _environment.TodoItemToggled();
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

    private void CloseTodoFlyout()
    {
        _todoFlyout?.SetCurrentValue(Popup.IsOpenProperty, false);
        _todoFlyout = null;
    }

    /// <summary>
    /// A StaysOpen=false popup never hears clicks that land in other processes: the
    /// system drops the capture instead of telling the popup. While the flyout is open
    /// the bar polls the button state and closes it when a click lands outside it.
    /// </summary>
    private void CloseFlyoutOnOutsideClick()
    {
        if (!TopBarNativeMethods.IsMouseButtonDown(TopBarNativeMethods.LeftMouseButton))
        {
            return;
        }

        DockNativeMethods.GetCursorPos(out var cursor);
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
    /// The centre search box. A click or the global hotkey starts a search session:
    /// the bar — normally never-activating, so it never steals focus from other apps —
    /// briefly becomes activatable and foreground so the box can receive keyboard
    /// input, and gives the foreground back when the session ends (second hotkey press,
    /// Escape, a click elsewhere or opening a result). The session also clears the box
    /// and closes the result flyout, so every session starts from a clean state.
    /// </summary>
    private void AddSearchModule()
    {
        if (!TopBarRules.IsModuleOn(_state.Modules, TopBarModuleKind.Search))
        {
            return;
        }

        var box = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            SelectionBrush = FrozenBrush(SearchSelectionBrushColor),
            FontSize = 12,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.IBeam
        };
        // This non-activating surface borrows focus for each search session. Open the
        // selected IME on focus instead of inheriting its closed state; Shift can
        // still select Latin input while the box stays focused.
        InputMethod.SetPreferredImeState(box, InputMethodState.On);
        InputMethod.SetPreferredImeConversionMode(box, ImeConversionModeValues.Native);
        AutomationProperties.SetName(box, "顶栏搜索框");
        box.TextChanged += (_, _) =>
        {
            UpdateSearchPlaceholder();
            _searchDebounce.Stop();
            if (!_searchComposing) _searchDebounce.Start();
        };
        TextCompositionManager.AddPreviewTextInputStartHandler(box, (_, args) =>
        {
            if (args.TextComposition.CompositionText.Length > 0) SuspendSearchForComposition();
        });
        TextCompositionManager.AddPreviewTextInputUpdateHandler(box, (_, args) =>
        {
            if (args.TextComposition.CompositionText.Length > 0) SuspendSearchForComposition();
            else ResumeSearchAfterComposition(box);
        });
        box.PreviewTextInput += (_, _) => ResumeSearchAfterComposition(box);
        box.PreviewKeyDown += OnSearchBoxKeyDown;
        box.ContextMenuOpening += OnSearchBoxContextMenuOpening;
        box.LostKeyboardFocus += (_, _) =>
        {
            // Focusing any other module (or another app) ends the session; the popup's
            // rows are not focusable, so clicking a result does not land here.
            if (_searchActive)
            {
                EndSearchSessionAfterInput();
            }
        };
        box.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (!_searchActive)
            {
                // Out of the mouse message itself: switching activation from inside
                // WM_LBUTTONDOWN processing is the classic input-deadlock trap.
                Dispatcher.BeginInvoke(BeginSearchSession, DispatcherPriority.Input);
            }
        };

        var placeholder = new TextBlock
        {
            Text = "搜索文件与应用",
            Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
            FontSize = 12,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        placeholder.Foreground.Freeze();

        var host = new Border
        {
            Width = SearchBoxWidth,
            MinWidth = 140,
            Height = Math.Clamp(_state.Height - 10d, 18d, 30d),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
            Padding = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        host.Background.Freeze();
        host.Child = new DockPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = GlyphSearch,
                    FontFamily = StatusIconFont,
                    FontSize = 12,
                    Foreground = Brushes.White,
                    Opacity = 0.7,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 1)
                },
                new Grid
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        placeholder,
                        box
                    }
                }
            }
        };
        DockPanel.SetDock(
            ((DockPanel)host.Child).Children[0],
            System.Windows.Controls.Dock.Left);

        _searchHost = host;
        _searchBox = box;
        _searchPlaceholder = placeholder;
        ApplySearchPresentation();
    }

    private void ApplySearchPresentation()
    {
        if (_searchHost is not { } host) return;
        EndSearchSession();
        CenterHost.Children.Remove(host);
        if (_searchToggleButton is { } previousButton) CenterHost.Children.Remove(previousButton);
        _searchToggleButton = null;
        _searchWindowHiddenForFullScreen = false;
        if (_state.SearchPresentation == SearchPresentation.TopBar)
        {
            var previousWindow = _searchWindow;
            _searchWindow = null;
            previousWindow?.DetachSearchContent();
            previousWindow?.Hide();
            if (previousWindow is not null) _idleSearchWindow = previousWindow;
            host.Width = SearchBoxWidth;
            host.Height = Math.Clamp(_state.Height - 10d, 18d, 30d);
            CenterHost.Children.Add(host);
        }
        else
        {
            if (_searchWindow is null)
            {
                if (_idleSearchWindow is { } idle)
                {
                    _idleSearchWindow = null;
                    _searchWindow = idle;
                    idle.AttachSearchContent(host);
                }
                else
                {
                    var created = new SearchWindow(_state, host, _environment.SearchLayoutChanged);
                    _searchWindow = created;
                    created.Deactivated += (_, _) =>
                    {
                        if (ReferenceEquals(_searchWindow, created) && _searchActive) EndSearchSessionAfterInput();
                    };
                    created.Dismissed += EndSearchSession;
                }
            }
            var window = _searchWindow;
            if (_state.SearchPresentation == SearchPresentation.Floating)
            {
                window.Show();
            }
            else
            {
                window.Hide();
                var button = new Button
                {
                    Content = new TextBlock { Text = GlyphSearch, FontFamily = StatusIconFont },
                    Style = (Style)FindResource("ModuleButton"),
                    Foreground = Brushes.White, ToolTip = "搜索文件与应用", MinWidth = 32, MinHeight = 24
                };
                AutomationProperties.SetName(button, "展开搜索");
                button.Click += (_, _) => Dispatcher.BeginInvoke(BeginSearchSession, DispatcherPriority.Input);
                _searchToggleButton = button;
                CenterHost.Children.Add(button);
            }
        }
        _appliedSearchPresentation = _state.SearchPresentation;
        _modules[TopBarModuleKind.Search] = host;
        UpdateSearchHostWidth();
        UpdateSearchPlaceholder();
    }

    /// <summary>
    /// Clamps the box to the room the side modules leave: full design width on a
    /// normal bar, shrinking around the bar's centre on a narrow one. The 140 floor
    /// keeps the box usable even when the modules meet in the middle.
    /// </summary>
    private void UpdateSearchHostWidth()
    {
        if (_state.SearchPresentation != SearchPresentation.TopBar || _searchHost is not { } host)
        {
            return;
        }

        var available = Surface.ActualWidth - LeftModules.ActualWidth - RightModules.ActualWidth;
        host.Width = Math.Clamp(available - 16d, 140d, SearchBoxWidth);
    }

    private void UpdateSearchPlaceholder()
    {
        if (_searchPlaceholder is { } placeholder)
        {
            placeholder.Visibility = string.IsNullOrEmpty(_searchBox?.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void SuspendSearchForComposition()
    {
        if (_searchComposing) return;
        _searchComposing = true;
        // Phonetic text belongs to the IME. Its candidate UI must not be covered
        // by our results, nor should every composition update query file indexes.
        CloseSearchFlyout();
    }

    private void ResumeSearchAfterComposition(TextBox box)
    {
        if (!_searchComposing) return;
        _searchComposing = false;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_searchActive || _searchComposing || !ReferenceEquals(_searchBox, box)) return;
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }, DispatcherPriority.Input);
    }

    private DockGlass SearchGlass => _searchWindow is null ? DesktopStyleRules.TopBar(_style, _state)
        : _state.SearchFollowCardLook ? DesktopStyleRules.Dock(_style)
        : DesktopStyleRules.CustomDock(_state.SearchSurfaceColor, _state.SearchSurfaceOpacity);

    // Reading and command surfaces need their own opaque paint: the input can be
    // almost transparent, so its text colour cannot determine flyout contrast.
    private DockGlass SearchFlyoutGlass
    {
        get
        {
            var color = (Color)ColorConverter.ConvertFromString(SearchGlass.Surface);
            var light = (0.2126d * color.R + 0.7152d * color.G + 0.0722d * color.B) / 255d > 0.6d;
            return light
                ? new DockGlass("#F3F7FC", "#C6D5E5", "#20364D")
                : new DockGlass("#202C3B", "#536477", "#F0F5FC");
        }
    }

    private void ApplySearchAppearance()
    {
        if (_searchHost is not { } host || _searchBox is not { } box) return;
        if (_searchWindow is null) host.Height = Math.Clamp(_state.Height - 10d, 18d, 30d);
        var glass = SearchGlass;
        _searchForeground = _searchWindow is null ? Brushes.White : FrozenBrush(glass.RunningDot);
        box.Foreground = box.CaretBrush = _searchForeground;
        if (_searchPlaceholder is { } placeholder)
        {
            placeholder.Foreground = _searchForeground;
            placeholder.Opacity = 0.6d;
        }
        if (host.Child is DockPanel panel && panel.Children[0] is TextBlock glyph) glyph.Foreground = _searchForeground;
        _searchWindow?.ApplyAppearance(glass);
        if (_searchCard is { } card)
        {
            var flyoutGlass = SearchFlyoutGlass;
            card.Background = FrozenBrush(flyoutGlass.Surface);
            card.BorderBrush = FrozenBrush(flyoutGlass.Border);
            card.CornerRadius = new CornerRadius(_searchWindow is null ? 10d : _state.SearchCornerRadius);
            if (_searchRows is { } rows)
            {
                foreach (var text in rows.Children.OfType<TextBlock>())
                    text.Foreground = FrozenBrush(flyoutGlass.RunningDot);
                foreach (var row in _searchRowBorders)
                {
                    if (row.Child is DockPanel { Children: var children }
                        && children.OfType<DockPanel>().FirstOrDefault() is { } texts)
                        foreach (var text in texts.Children.OfType<TextBlock>())
                            text.Foreground = FrozenBrush(flyoutGlass.RunningDot);
                }
            }
        }
    }

    // One shot per edit: the timer itself repeats until stopped.
    private void OnSearchDebounceTick()
    {
        _searchDebounce.Stop();
        RunSearchNow();
    }

    /// <summary>Runs the pending search immediately instead of after the debounce.</summary>
    internal void PumpSearch()
    {
        _searchDebounce.Stop();
        RunSearchNow();
    }

    private async void RunSearchNow()
    {
        if (_closing || _searchComposing || _searchBox is not { } box)
        {
            return;
        }

        var query = box.Text;
        if (query.Trim().Length == 0)
        {
            // An empty query never opens the flyout; there is nothing to rank.
            CloseSearchFlyout();
            _searchResults = [];
            return;
        }

        var candidates = _searchCandidates;
        var generation = ++_searchGeneration;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;
        var useEverything = _state.UseEverything;
        try
        {
            var basicResults = TopBarSearchRules.Search(candidates, query);
            if (_closing || token.IsCancellationRequested || generation != _searchGeneration || _searchBox?.Text != query) return;
            RenderSearchResults([.. basicResults], query, "正在查询文件索引…");
            var files = EverythingSearchResult.Unavailable;
            if (useEverything && HwndSource.FromHwnd(_handle) is { } source)
            {
                _everythingSearch ??= new EverythingSearchClient(source);
                files = await _everythingSearch.SearchAsync(query, token);
            }
            var windows = files.Available ? WindowsIndexSearchResult.Unavailable
                : await WindowsIndexSearchClient.SearchAsync(query, token);
            // Keep stage-1 rows in their exact order and append deduplicated index matches below them.
            var results = await Task.Run(
                () => TopBarSearchRules.AppendSupplementalResults(
                    basicResults,
                    files.Candidates.Concat(windows.Candidates),
                    query),
                token);
            // Keep the pressed result alive through MouseUp; opening it cancels this query.
            while (Mouse.LeftButton == MouseButtonState.Pressed) await Task.Delay(30, token);
            if (_closing || token.IsCancellationRequested || generation != _searchGeneration || _searchBox?.Text != query)
            {
                return;
            }
            RenderSearchResults([.. results], query, files.Available ? "桌面与应用 · Everything 文件搜索"
                : windows.Available ? useEverything ? "Everything 不可用，已使用 Windows 索引" : "桌面与应用 · Windows 已索引文件"
                : "文件索引不可用，已搜索桌面与应用");
        }
        catch (OperationCanceledException)
        {
            // A newer edit or a closed session owns the surface now.
        }
    }

    private void RenderSearchResults(List<TopBarSearchCandidate> results, string query, string? scope = null)
    {
        if (_searchContextMenu?.IsOpen == true)
        {
            // Keep the clicked row alive; only the latest bounded reply is retained.
            _pendingSearchResults = (results, query, scope);
            return;
        }
        if (_searchHost is null)
        {
            return;
        }

        DockNativeMethods.GetCursorPos(out _lastSearchMousePoint);
        if (_searchFlyout?.IsOpen == true && _searchRows is { } existingRows && _searchRenderedQuery == query)
        {
            if (_searchResults.SequenceEqual(results))
            {
                if (_searchScope is { } footer) footer.Text = scope ?? string.Empty;
                return;
            }

            // Stage-2 supplemental results preserve the leading stage-1 rows in place:
            // append only the new rows without rebuilding existing rows or resetting selection.
            if (_searchResults.Count > 0
                && results.Count >= _searchResults.Count
                && results.Take(_searchResults.Count).SequenceEqual(_searchResults))
            {
                var previousCount = _searchResults.Count;
                _searchResults = results;
                if (_searchScope is { } oldFooter)
                {
                    existingRows.Children.Remove(oldFooter);
                }
                for (var index = previousCount; index < results.Count; index++)
                {
                    var row = CreateSearchRow(results[index], index);
                    _searchRowBorders.Add(row);
                    existingRows.Children.Add(row);
                }
                _searchScope ??= new TextBlock
                {
                    Foreground = FrozenBrush(SearchFlyoutGlass.RunningDot), Opacity = 0.72, FontSize = 11,
                    Margin = new Thickness(12, 6, 12, 2), IsHitTestVisible = false
                };
                _searchScope.Text = scope ?? string.Empty;
                existingRows.Children.Add(_searchScope);
                UpdateSearchSelection();
                LoadSearchIcons(results);
                return;
            }
        }

        var preservedIndex = 0;
        if (_searchRenderedQuery == query && (uint)_searchSelected < (uint)_searchResults.Count)
        {
            var selectedPath = _searchResults[_searchSelected].Path;
            var matchIndex = results.FindIndex(item => string.Equals(item.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (matchIndex >= 0)
            {
                preservedIndex = matchIndex;
            }
        }

        _searchResults = results;
        _searchRenderedQuery = query;
        _searchSelected = preservedIndex;
        _pressedSearchRowIndex = -1;
        EnsureSearchFlyout();
        // The popup's IsOpen can read false for a moment right after being set: the
        // actual open completes on the next layout pass. The rows build regardless;
        // the flyout shows them once it lands.
        if (_searchRows is not { } rows)
        {
            return;
        }

        rows.Children.Clear();
        _searchRowBorders.Clear();
        if (results.Count == 0)
        {
            rows.Children.Add(new TextBlock
            {
                Text = $"没有与“{query}”匹配的结果",
                Foreground = FrozenBrush(SearchFlyoutGlass.RunningDot),
                Opacity = 0.72,
                FontSize = 12,
                Margin = new Thickness(12, 6, 12, 6)
            });
        }
        else
        {
            for (var index = 0; index < results.Count; index++)
            {
                var row = CreateSearchRow(results[index], index);
                _searchRowBorders.Add(row);
                rows.Children.Add(row);
            }
            UpdateSearchSelection();
            LoadSearchIcons(results);
        }
        _searchScope = new TextBlock
            {
                Text = scope ?? string.Empty, Foreground = FrozenBrush(SearchFlyoutGlass.RunningDot), Opacity = 0.72, FontSize = 11,
                Margin = new Thickness(12, 6, 12, 2), IsHitTestVisible = false
            };
        rows.Children.Add(_searchScope);
    }

    private Border CreateSearchRow(TopBarSearchCandidate candidate, int index)
    {
        var icon = new Image
        {
            Width = 22,
            Height = 22,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Source = _searchIconCache.GetValueOrDefault(candidate.Path)
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var name = new TextBlock
        {
            Text = candidate.DisplayName,
            Foreground = FrozenBrush(SearchFlyoutGlass.RunningDot),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var detail = new TextBlock
        {
            Text = $"{KindLabel(candidate.Kind)} · {TopBarSearchRules.FormatLocation(candidate.Path)}",
            Foreground = FrozenBrush(SearchFlyoutGlass.RunningDot),
            Opacity = 0.72d,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var texts = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(name, System.Windows.Controls.Dock.Top);
        texts.Children.Add(name);
        texts.Children.Add(detail);

        var row = new Border
        {
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(4, 1, 4, 1),
            CornerRadius = new CornerRadius(6),
            Cursor = Cursors.Hand,
            Tag = index,
            Child = new DockPanel
            {
                Children =
                {
                    icon,
                    texts
                }
            }
        };
        DockPanel.SetDock(icon, System.Windows.Controls.Dock.Left);
        // Rows are not focusable: keyboard focus stays in the box, which drives the
        // selection, and one click opens exactly one result. Hover only changes
        // selection when the pointer has actually moved.
        void OnRowHover()
        {
            if (_searchContextMenu?.IsOpen == true)
            {
                return;
            }
            DockNativeMethods.GetCursorPos(out var cursor);
            if (cursor.X == _lastSearchMousePoint.X && cursor.Y == _lastSearchMousePoint.Y)
            {
                return;
            }
            _lastSearchMousePoint = cursor;
            SelectSearchRow(index);
        }
        row.MouseEnter += (_, _) => OnRowHover();
        row.MouseMove += (_, _) => OnRowHover();
        row.MouseLeftButtonDown += (_, _) =>
        {
            _pressedSearchRowIndex = index;
            SelectSearchRow(index);
        };
        row.MouseLeftButtonUp += (_, _) =>
        {
            var pressed = _pressedSearchRowIndex;
            _pressedSearchRowIndex = -1;
            if (pressed == index || pressed == -1)
            {
                OpenSearchResultAfterInput(candidate);
            }
        };
        row.ContextMenuOpening += (_, args) => args.Handled = true;
        row.MouseRightButtonDown += (_, args) =>
        {
            args.Handled = true;
            SelectSearchRow(index);
        };
        row.MouseRightButtonUp += (_, args) =>
        {
            args.Handled = true;
            SelectSearchRow(index);
            var offsetX = Math.Clamp(args.GetPosition(row).X, 8d, Math.Max(8d, row.ActualWidth - 180d));
            OpenSearchContextMenu(row, candidate, offsetX);
        };
        return row;
    }

    private void OpenSearchContextMenu(Border row, TopBarSearchCandidate candidate, double horizontalOffset = 8d)
    {
        _searchContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        var menu = CreateStyledSearchContextMenu(row, PlacementMode.Bottom, horizontalOffset, 2d);
        // Allocate only for the invoked result, not for every row on every keystroke.
        void Add(string title, string gestureText, Action action)
        {
            var item = CreateStyledSearchMenuItem(title, gestureText, () =>
            {
                EndSearchSession();
                action();
            });
            menu.Items.Add(item);
        }
        Add("打开", "Enter", () => _environment.OpenSearchTarget(candidate));
        Add("打开文件位置", "Ctrl+Enter", () => _environment.OpenSearchTargetLocation(candidate));
        Add("复制路径", "Ctrl+Shift+C", () => TryCopyTextToClipboard(candidate.Path));
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_searchContextMenu, menu)) return;
            _searchContextMenu = null;
            row.ContextMenu = null;
            if (!_searchActive) return;
            if (DockNativeMethods.GetForegroundWindow() != SearchInputHandle)
            {
                EndSearchSession();
                return;
            }
            _searchBox?.Focus();
            var pending = _pendingSearchResults;
            _pendingSearchResults = null;
            if (pending is { } reply && _searchBox?.Text == reply.Query)
                RenderSearchResults(reply.Results, reply.Query, reply.Scope);
        }, DispatcherPriority.Input);
        row.ContextMenu = menu;
        _searchContextMenu = menu;
        menu.IsOpen = true;
    }

    private void OnSearchBoxContextMenuOpening(object sender, ContextMenuEventArgs args)
    {
        if (sender is not TextBox box)
        {
            return;
        }
        args.Handled = true;
        _searchContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        var offsetX = args.CursorLeft >= 0 ? Math.Max(0d, args.CursorLeft) : 4d;
        var menu = CreateStyledSearchContextMenu(box, PlacementMode.Bottom, offsetX, 4d);
        var hasSelection = box.SelectionLength > 0;
        var hasText = !string.IsNullOrEmpty(box.Text);
        var canPaste = false;
        try { canPaste = System.Windows.Clipboard.ContainsText(); }
        catch (ExternalException) { }

        menu.Items.Add(CreateStyledSearchMenuItem("剪切", "Ctrl+X", () => { box.Cut(); box.Focus(); }, hasSelection));
        menu.Items.Add(CreateStyledSearchMenuItem("复制", "Ctrl+C", () => { box.Copy(); box.Focus(); }, hasSelection));
        menu.Items.Add(CreateStyledSearchMenuItem("粘贴", "Ctrl+V", () => { box.Paste(); box.Focus(); }, canPaste));
        menu.Items.Add(CreateStyledSearchMenuItem("全选", "Ctrl+A", () => { box.SelectAll(); box.Focus(); }, hasText));
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_searchContextMenu, menu)) return;
            _searchContextMenu = null;
            box.ContextMenu = null;
            if (!_searchActive) return;
            if (DockNativeMethods.GetForegroundWindow() != SearchInputHandle)
            {
                EndSearchSession();
                return;
            }
            box.Focus();
        }, DispatcherPriority.Input);
        box.ContextMenu = menu;
        _searchContextMenu = menu;
        menu.IsOpen = true;
    }

    private ContextMenu CreateStyledSearchContextMenu(
        UIElement placementTarget,
        PlacementMode placement,
        double horizontalOffset,
        double verticalOffset)
    {
        var glass = SearchFlyoutGlass;
        var surfaceBrush = FrozenBrush(glass.Surface);
        var borderBrush = FrozenBrush(glass.Border);

        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
        borderFactory.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BorderBrushProperty));
        borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        borderFactory.SetValue(Border.PaddingProperty, new Thickness(4));
        borderFactory.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var presenterFactory = new FrameworkElementFactory(typeof(ItemsPresenter));
        presenterFactory.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        borderFactory.AppendChild(presenterFactory);

        return new ContextMenu
        {
            PlacementTarget = placementTarget,
            Placement = placement,
            HorizontalOffset = horizontalOffset,
            VerticalOffset = verticalOffset,
            Background = surfaceBrush,
            BorderBrush = borderBrush,
            Foreground = FrozenBrush(glass.RunningDot),
            MinWidth = 244,
            HasDropShadow = true,
            Template = new ControlTemplate(typeof(ContextMenu))
            {
                VisualTree = borderFactory
            }
        };
    }

    private MenuItem CreateStyledSearchMenuItem(
        string title,
        string? gestureText,
        Action action,
        bool isEnabled = true)
    {
        var borderFactory = new FrameworkElementFactory(typeof(Border), "ItemBorder");
        borderFactory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        borderFactory.SetValue(Border.PaddingProperty, new Thickness(12, 7, 12, 7));
        borderFactory.SetValue(FrameworkElement.MinHeightProperty, 34d);
        borderFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1));

        var dockFactory = new FrameworkElementFactory(typeof(DockPanel));
        if (!string.IsNullOrEmpty(gestureText))
        {
            var gestureFactory = new FrameworkElementFactory(typeof(TextBlock), "GestureText");
            gestureFactory.SetValue(DockPanel.DockProperty, System.Windows.Controls.Dock.Right);
            gestureFactory.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(MenuItem.InputGestureTextProperty));
            gestureFactory.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.ForegroundProperty));
            gestureFactory.SetValue(UIElement.OpacityProperty, 0.72d);
            gestureFactory.SetValue(TextBlock.FontSizeProperty, 11d);
            gestureFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(18, 0, 0, 0));
            gestureFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            dockFactory.AppendChild(gestureFactory);
        }

        var headerFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        headerFactory.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        headerFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        headerFactory.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        dockFactory.AppendChild(headerFactory);
        borderFactory.AppendChild(dockFactory);

        var template = new ControlTemplate(typeof(MenuItem))
        {
            VisualTree = borderFactory
        };
        var highlightTrigger = new Trigger
        {
            Property = MenuItem.IsHighlightedProperty,
            Value = true
        };
        highlightTrigger.Setters.Add(new Setter(Border.BackgroundProperty, SearchRowSelectedBrush, "ItemBorder"));
        template.Triggers.Add(highlightTrigger);

        var disabledTrigger = new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false
        };
        disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4d, "ItemBorder"));
        template.Triggers.Add(disabledTrigger);

        var item = new MenuItem
        {
            Header = title,
            InputGestureText = gestureText ?? string.Empty,
            Foreground = FrozenBrush(SearchFlyoutGlass.RunningDot),
            FontSize = 13,
            IsEnabled = isEnabled,
            Cursor = isEnabled ? Cursors.Hand : Cursors.Arrow,
            Template = template
        };
        item.Click += (_, _) => Dispatcher.BeginInvoke(action, DispatcherPriority.Input);
        return item;
    }

    private static void TryCopyTextToClipboard(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            // Another application may hold the clipboard.
        }
    }

    private static string KindLabel(TopBarSearchItemKind kind) => kind switch
    {
        TopBarSearchItemKind.App => "应用",
        TopBarSearchItemKind.Folder => "文件夹",
        _ => "文件"
    };

    private void SelectSearchRow(int index)
    {
        if (index < 0 || index >= _searchResults.Count || index == _searchSelected)
        {
            return;
        }
        _searchSelected = index;
        UpdateSearchSelection();
    }

    private void UpdateSearchSelection()
    {
        for (var index = 0; index < _searchRowBorders.Count; index++)
        {
            _searchRowBorders[index].Background = index == _searchSelected
                ? SearchRowSelectedBrush
                : Brushes.Transparent;
        }
        if ((uint)_searchSelected < (uint)_searchRowBorders.Count)
        {
            _searchRowBorders[_searchSelected].BringIntoView();
        }
    }

    private void MoveSearchSelection(int delta)
    {
        if (_searchResults.Count == 0)
        {
            return;
        }
        var next = Math.Clamp(_searchSelected + delta, 0, _searchResults.Count - 1);
        if (next == _searchSelected)
        {
            return;
        }
        _searchSelected = next;
        DockNativeMethods.GetCursorPos(out _lastSearchMousePoint);
        UpdateSearchSelection();
    }

    private void OnSearchBoxKeyDown(object sender, KeyEventArgs args)
    {
        // A Chinese IME reports every key it processes mid-composition — Return
        // commits the raw letters, Escape cancels, the arrows page candidates — as
        // Key.ImeProcessed, and the same physical keys arrive plain once no
        // composition is running (verified against WeChat IME). Those keys belong
        // to the IME: ending the session from inside one makes the foreground
        // switch tear the composition down synchronously while re-entering WPF's
        // text store, which fail-fasts the whole process.
        if (args.Key == Key.ImeProcessed)
        {
            SuspendSearchForComposition();
            return;
        }

        // Physical Enter reports Key.Return (Key.Enter is another value).
        switch (args.Key)
        {
            case Key.Apps:
            case Key.F10 when Keyboard.Modifiers == ModifierKeys.Shift:
                if ((uint)_searchSelected < (uint)_searchRowBorders.Count)
                    OpenSearchContextMenu(_searchRowBorders[_searchSelected], _searchResults[_searchSelected]);
                args.Handled = true;
                break;
            case Key.C when Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                if ((uint)_searchSelected < (uint)_searchResults.Count)
                {
                    CopySearchPathAfterInput(_searchResults[_searchSelected]);
                    args.Handled = true;
                }
                break;
            case Key.Escape:
                EndSearchSessionAfterInput();
                args.Handled = true;
                break;
            case Key.Return:
                // Snapshot the target now: like the row click, the deferred open must
                // survive a teardown racing in before the dispatcher turn.
                if ((uint)_searchSelected < (uint)_searchResults.Count)
                {
                    var selected = _searchResults[_searchSelected];
                    if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                    {
                        OpenSearchLocationAfterInput(selected);
                    }
                    else
                    {
                        OpenSearchResultAfterInput(selected);
                    }
                }
                args.Handled = true;
                break;
            case Key.Down:
                MoveSearchSelection(1);
                args.Handled = true;
                break;
            case Key.Up:
                MoveSearchSelection(-1);
                args.Handled = true;
                break;
        }
    }

    /// <summary>Extracts shell icons off the UI thread for the rows just rendered.</summary>
    private void LoadSearchIcons(IReadOnlyList<TopBarSearchCandidate> results)
    {
        var missing = results
            .Select(candidate => candidate.Path)
            .Where(path => !_searchIconCache.ContainsKey(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count == 0)
        {
            return;
        }
        if (_searchIconCache.Count > 512)
        {
            var activePaths = new HashSet<string>(results.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var key in _searchIconCache.Keys)
            {
                if (!activePaths.Contains(key))
                {
                    _searchIconCache.TryRemove(key, out _);
                }
            }
        }
        Task.Run(() =>
        {
            foreach (var path in missing)
            {
                if (_closing)
                {
                    return;
                }
                var icon = ShellIconProvider.GetIcon(path);
                _searchIconCache[path] = icon;
                Dispatcher.BeginInvoke(() =>
                {
                    if (_closing || _searchFlyout?.IsOpen != true)
                    {
                        return;
                    }
                    foreach (var row in _searchRowBorders)
                    {
                        if (row.Child is DockPanel panel
                            && panel.Children.OfType<Image>().FirstOrDefault() is { } image
                            && row.Tag is int index
                            && index < _searchResults.Count
                            && string.Equals(_searchResults[index].Path, path, StringComparison.OrdinalIgnoreCase))
                        {
                            image.Source = icon;
                        }
                    }
                }, DispatcherPriority.Background);
            }
        });
    }

    private void EnsureSearchFlyout()
    {
        if (_searchFlyout is { } || _searchHost is null)
        {
            return;
        }

        var rows = new StackPanel();
        var flyoutWidth = _searchWindow?.ActualWidth > 0 ? _searchWindow.ActualWidth : SearchFlyoutWidth;
        var glass = SearchFlyoutGlass;
        var card = new Border
        {
            VerticalAlignment = VerticalAlignment.Top,
            Width = flyoutWidth,
            Background = FrozenBrush(glass.Surface),
            BorderBrush = FrozenBrush(glass.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(_searchWindow is null ? 10d : _state.SearchCornerRadius),
            Padding = new Thickness(0, 6, 0, 6),
            Child = new ScrollViewer
            {
                MaxHeight = 44d * SearchFlyoutVisibleRows,
                // Keep keyboard focus in the search box throughout a result click.
                Focusable = false,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = rows
            }
        };
        card.PreviewMouseLeftButtonDown += (_, _) => _pressedSearchRowIndex = -2;
        card.ContextMenuOpening += (_, args) => args.Handled = true;
        card.MouseRightButtonDown += (_, args) => args.Handled = true;
        card.MouseRightButtonUp += (_, args) => args.Handled = true;
        var host = _searchHost;
        var flyout = new Popup
        {
            PlacementTarget = host,
            Placement = PlacementMode.Bottom,
            // Centred on the box whatever width it clamped to, not on the design width.
            HorizontalOffset = -(flyoutWidth - host.ActualWidth) / 2d,
            VerticalOffset = 2,
            StaysOpen = true,
            AllowsTransparency = true,
            // The visible card still fits its rows. A fixed, transparent backing
            // avoids WPF's synchronous render flush on every result-count resize.
            // This bound follows the eight-row viewport plus padding and border.
            Child = new Grid
            {
                Width = flyoutWidth,
                Height = 44d * SearchFlyoutVisibleRows + card.Padding.Top + card.Padding.Bottom
                    + card.BorderThickness.Top + card.BorderThickness.Bottom,
                Children = { card }
            }
        };
        flyout.Opened += (_, _) => KeepFlyoutNonActivating(flyout);
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_searchFlyout, flyout))
            {
                _searchFlyout = null;
            }
        };
        CenterHost.Children.Add(flyout);
        _searchRows = rows;
        _searchCard = card;
        _searchFlyout = flyout;
        flyout.IsOpen = true;
    }

    private void CloseSearchFlyout()
    {
        _pendingSearchResults = null;
        _pressedSearchRowIndex = -1;
        _searchContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        _searchContextMenu = null;
        _searchCancellation?.Cancel();
        _searchGeneration++;
        _searchDebounce.Stop();
        var flyout = _searchFlyout;
        flyout?.SetCurrentValue(Popup.IsOpenProperty, false);
        if (flyout is not null)
        {
            CenterHost.Children.Remove(flyout);
        }
        _searchFlyout = null;
        _searchRows = null;
        _searchCard = null;
        _searchScope = null;
        _searchRenderedQuery = null;
        _searchResults = [];
        _searchRowBorders.Clear();
    }

    /// <summary>
    /// A WPF popup window activates on click: the click that picks a result would
    /// deactivate the bar, which ends the session between the mouse's down and up and
    /// closes the flyout under the cursor before the row ever sees the release — the
    /// click lands nowhere. Making the popup's own window never-activating keeps the
    /// bar active and the keyboard in the box for the whole click.
    /// </summary>
    private void KeepFlyoutNonActivating(Popup flyout)
    {
        // The popup's window comes to life during the open pass; style it after that.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (PresentationSource.FromVisual(flyout.Child) is not HwndSource source
                || source.Handle == 0)
            {
                return;
            }
            var style = (long)NativeMethods.GetWindowLongPtr(source.Handle, NativeMethods.GwlExStyle);
            NativeMethods.SetWindowLongPtr(
                source.Handle,
                NativeMethods.GwlExStyle,
                (nint)(style | DockNativeMethods.WsExNoActivate));
        });
    }

    // ---------------------------------------------------------------- 搜索会话

    /// <summary>
    /// Makes the bar activatable and takes the foreground so the box can receive text.
    /// Called from a box click or the global hotkey; the previous foreground window is
    /// remembered and gets the focus back when the session ends.
    /// </summary>
    internal void BeginSearchSession()
    {
        if (_closing || _searchHost is null)
        {
            return;
        }

        if (!IsVisible)
        {
            // A smart-hidden bar shows itself for the session; the interaction flag
            // keeps the hide state machine from pulling it away mid-typing.
            Show();
            RenderModules();
            RenderSystemModules();
        }
        if (_searchActive)
        {
            _searchBox?.Focus();
            return;
        }

        _searchActive = true;
        _pressedSearchRowIndex = -1;
        DockNativeMethods.GetCursorPos(out _lastSearchMousePoint);
        _searchSessionOwner = this;
        _searchPreviousForeground = DockNativeMethods.GetForegroundWindow();
        if (_searchWindow is { } searchWindow)
        {
            searchWindow.Show();
            searchWindow.RecoverPlacement();
        }
        SetSearchActivatable(true);
        try
        {
            DockNativeMethods.SetForegroundWindow(SearchInputHandle);
            SearchInputWindow.Activate();
        }
        catch (Exception exception) when (exception is InvalidOperationException)
        {
            // A failed foreground grab leaves a session without keyboard input; the
            // next Escape or outside click still ends it cleanly.
        }
        if (_searchBox is { } box)
        {
            box.Focus();
            // WPF can retain logical focus when this HWND becomes non-activating;
            // reopening then raises no focus event to apply PreferredImeState.
            if (box.IsKeyboardFocused)
            {
                InputMethod.Current.ImeState = InputMethodState.On;
                InputMethod.Current.ImeConversionMode = ImeConversionModeValues.Native;
            }
            box.CaretIndex = box.Text.Length;
        }
        // The fast tick keeps the outside-watch prompt while the flyout can be open.
        _foregroundTimer.Interval = SmartHideCheckInterval;
        LoadSearchCandidates();
    }

    /// <summary>
    /// Ends the search session one dispatcher turn later. Teardown returns the
    /// borrowed foreground to the previous window; doing that synchronously inside
    /// a key, focus or activation message makes a composing IME finish its
    /// composition while re-entering the WPF text store, which fail-fasts the
    /// process (observed with WeChat IME). Deferring puts the switch after the
    /// message that caused it.
    /// </summary>
    private void EndSearchSessionAfterInput() =>
        Dispatcher.BeginInvoke(() =>
        {
            if (_searchContextMenu?.IsOpen != true) EndSearchSession();
        }, DispatcherPriority.Input);

    /// <summary>
    /// Opens one result one dispatcher turn later, for the same reason as
    /// <see cref="EndSearchSessionAfterInput"/>. The candidate was captured at the
    /// event, so a session teardown that runs first cannot lose it.
    /// </summary>
    private void OpenSearchResultAfterInput(TopBarSearchCandidate candidate) =>
        Dispatcher.BeginInvoke(() =>
        {
            EndSearchSession();
            _environment.OpenSearchTarget(candidate);
        }, DispatcherPriority.Input);

    private void OpenSearchLocationAfterInput(TopBarSearchCandidate candidate) =>
        Dispatcher.BeginInvoke(() =>
        {
            EndSearchSession();
            _environment.OpenSearchTargetLocation(candidate);
        }, DispatcherPriority.Input);

    private void CopySearchPathAfterInput(TopBarSearchCandidate candidate) =>
        Dispatcher.BeginInvoke(() =>
        {
            EndSearchSession();
            TryCopyTextToClipboard(candidate.Path);
        }, DispatcherPriority.Input);

    /// <summary>Clears the box, closes the flyout and returns the bar to never-activating.</summary>
    internal void EndSearchSession()
    {
        _searchComposing = false;
        if (!_searchActive)
        {
            if (_searchBox is { } box)
            {
                box.Text = string.Empty;
            }
            CloseSearchFlyout();
            return;
        }

        _searchActive = false;
        if (ReferenceEquals(_searchSessionOwner, this))
        {
            _searchSessionOwner = null;
        }
        CloseSearchFlyout();
        if (_searchBox is { } searchBox)
        {
            searchBox.Text = string.Empty;
        }
        UpdateSearchPlaceholder();
        SetSearchActivatable(false);
        if (!_closing)
        {
            var previous = _searchPreviousForeground;
            if (previous != 0 && previous != SearchInputHandle
                && DockNativeMethods.GetForegroundWindow() == SearchInputHandle)
            {
                _ = DockNativeMethods.SetForegroundWindow(previous);
            }
        }
        if (_state.SearchPresentation == SearchPresentation.Icon)
        {
            _searchWindow?.Hide();
        }
        // Reserved bars return to the idle cadence after borrowing the fast input tick.
        _foregroundTimer.Interval = _appBar?.IsReserved == true && _todoFlyout is null && _clockFlyout is null
            ? FullScreenCheckInterval
            : SmartHideCheckInterval;
    }

    /// <summary>The bar takes no activation except while a search session holds it.</summary>
    protected override void OnDeactivated(EventArgs args)
    {
        base.OnDeactivated(args);
        if (_searchActive && _searchWindow is null)
        {
            EndSearchSessionAfterInput();
        }
    }

    private void SetSearchActivatable(bool activatable)
    {
        var inputHandle = SearchInputHandle;
        if (inputHandle == 0)
        {
            return;
        }
        var style = (long)NativeMethods.GetWindowLongPtr(inputHandle, NativeMethods.GwlExStyle);
        var wanted = activatable
            ? style & ~DockNativeMethods.WsExNoActivate
            : style | DockNativeMethods.WsExNoActivate;
        if (wanted != style)
        {
            NativeMethods.SetWindowLongPtr(inputHandle, NativeMethods.GwlExStyle, (nint)wanted);
        }
    }

    private void LoadSearchCandidates()
    {
        Task.Run(() =>
        {
            if (_closing)
            {
                return;
            }
            var candidates = _environment.ReadSearchCandidates();
            Dispatcher.BeginInvoke(() =>
            {
                if (_closing)
                {
                    return;
                }
                _searchCandidates = candidates;
                _searchSourcesLoaded = true;
                // The first keystrokes may have run against an empty list; re-rank once
                // the sources arrive.
                if (_searchActive && !string.IsNullOrWhiteSpace(_searchBox?.Text))
                {
                    RunSearchNow();
                }
            });
        });
    }

    // ---------------------------------------------------------------- 搜索热键

    private nint OnHotkeyMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == TopBarNativeMethods.WmHotkey && (int)wParam == SearchHotkeyId)
        {
            handled = true;
            if (_searchActive)
            {
                EndSearchSessionAfterInput();
            }
            else
            {
                BeginSearchSession();
            }
        }
        return 0;
    }

    /// <summary>
    /// Matches the registration to the settings: the hotkey lives while the search
    /// module is on, and a changed hotkey text re-registers in place.
    /// </summary>
    private void SyncSearchHotkey()
    {
        if (!TopBarRules.IsModuleOn(_state.Modules, TopBarModuleKind.Search))
        {
            UnregisterSearchHotkey();
            return;
        }
        var wanted = _state.SearchHotkey;
        if (_searchHotkeyRegistered && wanted == _searchHotkeyRegisteredText)
        {
            return;
        }
        UnregisterSearchHotkey();
        if (_handle == 0 || TopBarSearchRules.ParseHotkey(wanted) is not { } binding)
        {
            return;
        }
        if (TopBarNativeMethods.RegisterHotKey(
                _handle,
                SearchHotkeyId,
                binding.Modifiers | TopBarNativeMethods.ModNoRepeat,
                binding.VirtualKey))
        {
            _searchHotkeyRegistered = true;
            _searchHotkeyRegisteredText = wanted;
        }
        // A registration the system refuses (another app owns the combination) stays
        // off; the box itself keeps working and the next settings change retries.
    }

    private void UnregisterSearchHotkey()
    {
        if (_searchHotkeyRegistered && _handle != 0)
        {
            TopBarNativeMethods.UnregisterHotKey(_handle, SearchHotkeyId);
        }
        _searchHotkeyRegistered = false;
        _searchHotkeyRegisteredText = null;
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

    /// <summary>The open search flyout, for tests; popups sit outside the visual tree.</summary>
    internal Popup? SearchFlyout => _searchFlyout;

    internal TextBox? SearchBox => _searchBox;

    /// <summary>The box's host border, for tests reading its on-screen position.</summary>
    internal Border? SearchHost => _searchHost;

    /// <summary>Results of the last rendered search, in display order, for tests.</summary>
    internal IReadOnlyList<TopBarSearchCandidate> SearchResults => _searchResults;

    internal int SearchSelectedIndex => _searchSelected;

    internal bool SearchSessionActive => _searchActive;

    /// <summary>Test seam: the background candidate load has been published to the bar.
    /// <see cref="ITopBarEnvironment.ReadSearchCandidates"/> returning does not yet mean
    /// the bar can search against it.</summary>
    internal bool SearchSourcesLoaded => _searchSourcesLoaded;

    /// <summary>Test seam: an IME composition is suspending search updates.</summary>
    internal bool SearchSuspendedForComposition => _searchComposing;
    internal SearchWindow? FloatingSearchWindow => _searchWindow;

    /// <summary>
    /// While a search session holds the foreground, the bar holding it and the window
    /// it was borrowed from; both zero when no session is active. Lets the dock keep
    /// evaluating the foreground as it was before the box took it.
    /// </summary>
    internal static (nint Holder, nint Borrowed) SearchForeground
    {
        get
        {
            var owner = _searchSessionOwner;
            return owner is null || !owner._searchActive || owner._closing
                ? (0, 0)
                : (owner.SearchInputHandle, owner._searchPreviousForeground);
        }
    }

    private static System.Windows.Media.SolidColorBrush FrozenBrush(string color) =>
        CreateFrozenColorBrush((Color)ColorConverter.ConvertFromString(color));

    private static SolidColorBrush CreateFrozenColorBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static DropShadowEffect CreateTextShadow() => new()
    {
        Color = Colors.Black,
        Opacity = 0.55d,
        BlurRadius = 2d,
        ShadowDepth = 1d,
        Direction = 270
    };

    private void RenderModules()
    {
        if (_closing)
        {
            return;
        }

        _tick++;
        if (ModuleText(TopBarModuleKind.Clock) is { } clock)
        {
            clock.Text = FormatClock(_environment.Use24HourClock);
        }
        if (ModuleText(TopBarModuleKind.TodoSummary) is { } todo)
        {
            todo.Text = _environment.CountIncompleteTodos().ToString();
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
            network.Text = facts.Network.Label;
            if (ModuleIcon(TopBarModuleKind.Network) is { } networkIcon)
            {
                networkIcon.Text = facts.Network.Connected ? GlyphWifi : GlyphWifiOff;
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

        // Online geocoding names like "绥化市-黑龙江省绥化市-中国…" read as a trail;
        // the bar shows the city part, the tooltip keeps the full snapshot text.
        var city = snapshot.City.Split('-')[0];
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
                TextWrapping = TextWrapping.Wrap,
                Effect = CreateTextShadow()
            };
        }
    }

    private static string DescribeWeather(WeatherSnapshot snapshot)
    {
        var lines = new List<string>();
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
        if (snapshot.Status == WeatherStatus.Failed && !string.IsNullOrEmpty(snapshot.ErrorMessage))
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

        var running = _catalog.Capture();
        _attention.Retain(running.Select(window => window.Handle));
        // Names of windows that have since closed would survive handle reuse, so they go.
        var alive = running.Select(window => window.Handle).ToHashSet();
        foreach (var stale in _attentionNames.Keys.Where(handle => !alive.Contains(handle)).ToList())
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
            if (!_attentionNames.TryGetValue(window, out var name))
            {
                name = ResolveAttentionName(running, groups, window);
                _attentionNames[window] = name;
            }

            var button = new Button
            {
                Style = (Style)Resources["ModuleButton"],
                Content = new TextBlock
                {
                    Text = name,
                    Foreground = Brushes.Orange,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 120,
                    Effect = CreateTextShadow()
                },
                Focusable = true,
                Tag = window
            };
            System.Windows.Automation.AutomationProperties.SetName(button, $"{name}有新消息");
            button.Click += (_, _) =>
            {
                if (button.Tag is nint handle)
                {
                    _environment.ActivateWindow(handle);
                }
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
                Margin = new Thickness(4, 0, 8, 0),
                Effect = CreateTextShadow()
            });
        }
    }

    private static string ResolveAttentionName(
        IReadOnlyList<TrackedWindow> running,
        IReadOnlyList<WindowGroup> groups,
        nint window)
    {
        var group = groups.FirstOrDefault(candidate =>
            candidate.Windows.Any(item => item.Handle == window));
        if (group is not null && !string.IsNullOrWhiteSpace(group.DisplayName))
        {
            return group.DisplayName;
        }
        var tracked = running.FirstOrDefault(candidate => candidate.Handle == window);
        return !string.IsNullOrWhiteSpace(tracked?.Title) ? tracked!.Title : "消息";
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
        var acrylic = _acrylicAvailable
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
        var surfaceHex = acrylic ? AcrylicSurfaceTint : glass.Surface;
        var background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(surfaceHex));
        background.Freeze();
        Surface.Background = background;
        var border = new SolidColorBrush((Color)ColorConverter.ConvertFromString(glass.Border));
        border.Freeze();
        Surface.BorderBrush = border;
        Surface.BorderThickness = new Thickness(0, 0, 0, 1);
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

        var bounds = _appBar.Bounds;
        var foreground = ForegroundWindowRect(out var foregroundBounds, out var foregroundWindow);
        // The keyboard layout follows the focused window, and the IME's own
        // Chinese/English toggle changes no window at all, so the module re-reads on
        // every fast tick instead of waiting for the slower system-info cadence.
        if (foregroundWindow != 0 && IsVisible)
        {
            UpdateInputMethod();
        }

        var fullScreen = foreground
            && foregroundBounds.Left <= monitor.Bounds.Left
            && foregroundBounds.Top <= monitor.Bounds.Top
            && foregroundBounds.Right >= monitor.Bounds.Right
            && foregroundBounds.Bottom >= monitor.Bounds.Bottom;
        var zoomed = foreground && foregroundWindow != 0 && DockNativeMethods.IsZoomed(foregroundWindow);
        var trueFullScreen = DockVacateRules.IsTrueFullScreen(fullScreen, zoomed);
        if (_searchWindow is { } floating && _state.SearchPresentation == SearchPresentation.Floating)
        {
            if (trueFullScreen && !_searchActive && floating.IsVisible)
            {
                floating.Hide();
                _searchWindowHiddenForFullScreen = true;
            }
            else if (!trueFullScreen && _searchWindowHiddenForFullScreen)
            {
                floating.Show();
                _searchWindowHiddenForFullScreen = false;
            }
        }

        if (_appBar.IsReserved)
        {
            // A reserved bar rides above normal windows and under a full-screen app,
            // like the taskbar; nothing else moves it.
            _appBar.SetFullScreenDetected(fullScreen);
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
        var input = new DockAutoHideInput(
            WindowOverlapsDock: overlaps,
            ForegroundIsFullScreen: trueFullScreen,
            PointerInRevealZone: TopBarRevealRules.IsInRevealZone(
                cursor.X,
                cursor.Y,
                monitor.Bounds,
                revealDepth),
            PointerOverDock: pointerOverBar,
            InteractionActive: _menuOpen || _searchActive
                || _todoFlyout?.IsOpen == true || _clockFlyout?.IsOpen == true,
            // The immediate-collapse shortcut is the dock's; the bar keeps its debounce.
            OverlapIsMaximizedWindow: false);
        if (_autoHide.Update(input, DateTimeOffset.UtcNow))
        {
            if (_autoHide.IsShown)
            {
                Show();
                RenderModules();
                RenderSystemModules();
            }
            else
            {
                CloseTodoFlyout();
                CloseClockFlyout();
                Hide();
            }
        }
    }

    private bool ForegroundWindowRect(out PixelRect bounds, out nint window)
    {
        bounds = default;
        window = DockNativeMethods.GetForegroundWindow();
        if (window == 0 || window == _handle || DockNativeMethods.IsIconic(window))
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
        var settings = new MenuItem { Header = "在冰蓝桌面中设置…" };
        settings.Click += (_, _) => _environment.OpenTopBarSettings();
        var exit = new MenuItem { Header = "退出冰蓝桌面" };
        exit.Click += (_, _) => _environment.ExitApp();
        menu.Items.Add(settings);
        menu.Items.Add(exit);
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        ContextMenu = menu;
    }
}
