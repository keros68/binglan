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
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.TopBar;
using Button = System.Windows.Controls.Button;
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
    void OpenQuickSettings();

    /// <summary>Opens Task Manager and lands on its performance page.</summary>
    void OpenTaskManagerPerformance();

    void OpenComponentSettings(DesktopComponentKind kind);

    /// <summary>Brings one window of an app that asked for attention to the front.</summary>
    bool ActivateWindow(nint handle);

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
        // A network address change is the one system fact with an event source; the
        // others ride the sampling tick.
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
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

        BuildModules();
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
        // While the flyout is open the fast interval keeps its outside-click watch
        // prompt even in reserve mode.
        _foregroundTimer.Interval = reserve && _todoFlyout is null
            ? FullScreenCheckInterval
            : SmartHideCheckInterval;
        _foregroundTimer.Start();
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
        _modules.Clear();
        CloseTodoFlyout();
        CloseClockFlyout();

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
            _environment.OpenQuickSettings, GlyphBatteryUnknown);
        AddModule(TopBarModuleKind.Network, RightModules, "网络",
            _environment.OpenQuickSettings, GlyphWifiOff);
        AddModule(TopBarModuleKind.Volume, RightModules, "音量",
            _environment.OpenQuickSettings, GlyphVolume);
        AddModule(TopBarModuleKind.InputMethod, RightModules, "输入法", null);
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
            || module is not Border { Child: TextBlock badgeText } block)
        {
            return;
        }

        block.Padding = new Thickness(2, 0, 2, 0);
        // Re-parenting demands detaching first: the initializer would otherwise adopt a
        // TextBlock that still belongs to the block's child slot.
        block.Child = null;
        var nameText = new TextBlock
        {
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 120,
            Margin = new Thickness(4, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        block.Child = new StackPanel
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

    private static System.Windows.Media.SolidColorBrush FrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
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
        var darkSurface = IsDarkSurface(glass.Surface);
        var acrylic = _acrylicAvailable && darkSurface;
        // Written unconditionally: the startup probe may have left the accent applied,
        // so a change-tracking comparison would skip the disable a light surface needs.
        // The call is idempotent and only runs on material changes.
        _acrylicBackdrop = acrylic;
        TaskbarNativeMethods.TrySetAccent(
            _handle,
            acrylic
                ? TaskbarNativeMethods.AccentEnableAcrylicBlurBehind
                : TaskbarNativeMethods.AccentDisabled,
            acrylic ? AcrylicScrimAbgr : 0);
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

    private static bool IsDarkSurface(string surfaceWithAlpha)
    {
        // "#AARRGGBB" as produced by the style rules' WithAlpha.
        if (surfaceWithAlpha.Length < 9)
        {
            return false;
        }
        var red = Convert.ToInt32(surfaceWithAlpha.Substring(3, 2), 16);
        var green = Convert.ToInt32(surfaceWithAlpha.Substring(5, 2), 16);
        var blue = Convert.ToInt32(surfaceWithAlpha.Substring(7, 2), 16);
        return (0.2126d * red + 0.7152d * green + 0.0722d * blue) / 255d <= 0.6d;
    }

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
            InteractionActive: _menuOpen || _todoFlyout?.IsOpen == true || _clockFlyout?.IsOpen == true);
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
