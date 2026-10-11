namespace BingLan.Core.Models;

public enum TopBarVisibilityMode
{
    ReserveTopEdge,
    SmartHide
}

/// <summary>The nine read-only modules the top bar can show, in display order.</summary>
public enum TopBarModuleKind
{
    TodoSummary,
    Weather,
    Performance,
    Attention,
    InputMethod,
    Volume,
    Network,
    Battery,
    Clock
}

public sealed class TopBarModuleSwitches
{
    public bool TodoSummary { get; set; } = true;
    public bool Weather { get; set; } = true;
    public bool Performance { get; set; } = true;
    public bool Attention { get; set; } = true;
    public bool InputMethod { get; set; } = true;
    public bool Volume { get; set; } = true;
    public bool Network { get; set; } = true;
    public bool Battery { get; set; } = true;
    public bool Clock { get; set; } = true;
}

/// <summary>
/// The strip at the top of one monitor. It only shows the app's own data and read-only
/// system status; it never hosts app menus, tray icons, notifications or any control.
/// </summary>
public sealed class TopBarState
{
    public const double HeightDip = 32d;
    public const int MaximumAttentionApps = 3;
    public const string DefaultSurfaceColor = "#1E1F24";
    public const double DefaultSurfaceOpacity = 0.82d;

    public bool IsEnabled { get; set; }

    /// <summary>Device name of the monitor the bar sits on; null keeps the primary.</summary>
    public string? MonitorDeviceName { get; set; }

    public TopBarVisibilityMode VisibilityMode { get; set; } = TopBarVisibilityMode.ReserveTopEdge;

    public TopBarModuleSwitches Modules { get; set; } = new();

    /// <summary>Whether the bar takes its colours from the card material. The default
    /// dark menu-bar look reads white-on-charcoal; the card material is too light for it.</summary>
    public bool FollowCardLook { get; set; }

    public string SurfaceColor { get; set; } = DefaultSurfaceColor;

    public double SurfaceOpacity { get; set; } = DefaultSurfaceOpacity;
}

public static class TopBarRules
{
    public static IReadOnlyList<TopBarModuleKind> ModuleOrder { get; } =
        Enum.GetValues<TopBarModuleKind>();

    public static bool IsModuleOn(TopBarModuleSwitches switches, TopBarModuleKind kind) => kind switch
    {
        TopBarModuleKind.TodoSummary => switches.TodoSummary,
        TopBarModuleKind.Weather => switches.Weather,
        TopBarModuleKind.Performance => switches.Performance,
        TopBarModuleKind.Attention => switches.Attention,
        TopBarModuleKind.InputMethod => switches.InputMethod,
        TopBarModuleKind.Volume => switches.Volume,
        TopBarModuleKind.Network => switches.Network,
        TopBarModuleKind.Battery => switches.Battery,
        TopBarModuleKind.Clock => switches.Clock,
        _ => false
    };

    public static void SetModule(TopBarModuleSwitches switches, TopBarModuleKind kind, bool on)
    {
        switch (kind)
        {
            case TopBarModuleKind.TodoSummary: switches.TodoSummary = on; break;
            case TopBarModuleKind.Weather: switches.Weather = on; break;
            case TopBarModuleKind.Performance: switches.Performance = on; break;
            case TopBarModuleKind.Attention: switches.Attention = on; break;
            case TopBarModuleKind.InputMethod: switches.InputMethod = on; break;
            case TopBarModuleKind.Volume: switches.Volume = on; break;
            case TopBarModuleKind.Network: switches.Network = on; break;
            case TopBarModuleKind.Battery: switches.Battery = on; break;
            case TopBarModuleKind.Clock: switches.Clock = on; break;
        }
    }

    public static void Normalize(TopBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Modules ??= new TopBarModuleSwitches();
        if (!Enum.IsDefined(state.VisibilityMode))
        {
            state.VisibilityMode = TopBarVisibilityMode.ReserveTopEdge;
        }
        state.SurfaceColor = WidgetAppearanceRules.CoerceBackgroundColor(state.SurfaceColor);
        if (double.IsNaN(state.SurfaceOpacity) || double.IsInfinity(state.SurfaceOpacity))
        {
            state.SurfaceOpacity = TopBarState.DefaultSurfaceOpacity;
        }
        else
        {
            state.SurfaceOpacity = Math.Clamp(state.SurfaceOpacity, 0d, 1d);
        }
    }
}
