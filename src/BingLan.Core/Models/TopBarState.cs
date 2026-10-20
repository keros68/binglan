using BingLan.Core.TopBar;

namespace BingLan.Core.Models;

public enum TopBarVisibilityMode
{
    ReserveTopEdge,
    SmartHide
}

public enum SearchPresentation
{
    TopBar,
    Floating,
    Icon
}

/// <summary>The top bar's read-only modules, in display order. The search box sits in
/// the bar's centre regardless of this order; its kind is here so it shares the module
/// switch machinery with the rest.</summary>
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
    Clock,
    Search,
    TrayOverflow
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
    public bool Search { get; set; } = true;
    public bool TrayOverflow { get; set; } = true;
}

/// <summary>
/// The strip at the top of one monitor. It only shows the app's own data and read-only
/// system status; it never hosts app menus, tray icons, notifications or any control.
/// </summary>
public sealed class TopBarState
{
    public const double DefaultHeightDip = 32d;
    public const double MinimumHeightDip = 24d;
    public const double MaximumHeightDip = 48d;
    public const int MaximumAttentionApps = 3;
    public const string DefaultSurfaceColor = "#1E1F24";
    public const double DefaultSurfaceOpacity = 0.82d;
    // Win-key combos are owned by the system and Edge on stock Windows 11 (Win+Shift+F
    // is Edge's own search bar), so the default lives in the Ctrl+Alt space; the
    // recorder can retarget it to anything free.
    public const string DefaultSearchHotkey = "Ctrl+Alt+F";

    public bool IsEnabled { get; set; }

    /// <summary>Bar height in DIPs; clamped by <see cref="TopBarRules.Normalize"/>.</summary>
    public double Height { get; set; } = DefaultHeightDip;

    /// <summary>Device name of the monitor the bar sits on; null keeps the primary.</summary>
    public string? MonitorDeviceName { get; set; }

    public TopBarVisibilityMode VisibilityMode { get; set; } = TopBarVisibilityMode.ReserveTopEdge;

    public TopBarModuleSwitches Modules { get; set; } = new();

    /// <summary>Whether the bar takes its colours from the card material. The default
    /// dark menu-bar look reads white-on-charcoal; the card material is too light for it.</summary>
    public bool FollowCardLook { get; set; }

    /// <summary>Whether a dark surface blurs the wallpaper behind it (WCA acrylic).
    /// Light surfaces ignore this and always use the plain translucent colour.</summary>
    public bool FrostedGlass { get; set; } = true;

    public string SurfaceColor { get; set; } = DefaultSurfaceColor;

    public double SurfaceOpacity { get; set; } = DefaultSurfaceOpacity;

    /// <summary>The global focus-search hotkey text ("Win+Shift+F"); machine-local and
    /// never exported with a theme. Invalid or missing text falls back to the default.</summary>
    public string SearchHotkey { get; set; } = DefaultSearchHotkey;

    /// <summary>Optional local Everything query source; no index is maintained here.</summary>
    public bool UseEverything { get; set; }

    public SearchPresentation SearchPresentation { get; set; }

    public double SearchWindowWidth { get; set; } = 420d;

    public WindowPlacement SearchWindowPlacement { get; set; } = new();

    public bool SearchFollowCardLook { get; set; }
    public string SearchSurfaceColor { get; set; } = DefaultSurfaceColor;
    public double SearchSurfaceOpacity { get; set; } = 0.94d;
    public double SearchCornerRadius { get; set; } = 10d;
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
        TopBarModuleKind.Search => switches.Search,
        TopBarModuleKind.TrayOverflow => switches.TrayOverflow,
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
            case TopBarModuleKind.Search: switches.Search = on; break;
            case TopBarModuleKind.TrayOverflow: switches.TrayOverflow = on; break;
        }
    }

    public static void Normalize(TopBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Modules ??= new TopBarModuleSwitches();
        state.SearchSurfaceColor = WidgetAppearanceRules.CoerceColorOrDefault(state.SearchSurfaceColor, TopBarState.DefaultSurfaceColor);
        state.SearchSurfaceOpacity = double.IsFinite(state.SearchSurfaceOpacity) ? Math.Clamp(state.SearchSurfaceOpacity, 0.05d, 1d) : 0.94d;
        state.SearchCornerRadius = double.IsFinite(state.SearchCornerRadius) ? Math.Clamp(state.SearchCornerRadius, 0d, 24d) : 10d;
        state.SearchWindowPlacement ??= new WindowPlacement();
        if (!double.IsFinite(state.SearchWindowPlacement.Left))
        {
            state.SearchWindowPlacement.Left = 92;
        }
        if (!double.IsFinite(state.SearchWindowPlacement.Top))
        {
            state.SearchWindowPlacement.Top = 92;
        }
        if (!Enum.IsDefined(state.SearchPresentation))
        {
            state.SearchPresentation = SearchPresentation.TopBar;
        }
        state.SearchWindowWidth = double.IsFinite(state.SearchWindowWidth)
            ? Math.Clamp(state.SearchWindowWidth, 240d, 800d)
            : 420d;
        if (double.IsNaN(state.Height) || double.IsInfinity(state.Height))
        {
            state.Height = TopBarState.DefaultHeightDip;
        }
        else
        {
            state.Height = Math.Clamp(state.Height, TopBarState.MinimumHeightDip, TopBarState.MaximumHeightDip);
        }
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
        // A hand-edited or foreign hotkey string must not take over the registration.
        if (!TopBarSearchRules.IsValidHotkey(state.SearchHotkey))
        {
            state.SearchHotkey = TopBarState.DefaultSearchHotkey;
        }
    }
}
