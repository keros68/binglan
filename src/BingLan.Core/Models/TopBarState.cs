using System.Text.Json;
using System.Text.Json.Serialization;
using BingLan.Core.TopBar;

namespace BingLan.Core.Models;

public enum TopBarVisibilityMode
{
    ReserveTopEdge,
    SmartHide
}

public enum TopBarDisplayMode
{
    FullWidth,
    Capsule
}

public enum TopBarModuleSide
{
    Left,
    Right
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

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
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

    /// <summary>Machine-local layout; full width remains the default for existing profiles.</summary>
    public TopBarDisplayMode DisplayMode { get; set; }

    public TopBarModuleSwitches Modules { get; set; } = new();

    /// <summary>
    /// Custom left-to-right module order on the left side of the bar. Null together with
    /// <see cref="RightOrder"/> means the built-in default order; an empty list intentionally
    /// keeps the left side empty. The search entry always stays centred and is excluded.
    /// </summary>
    public List<TopBarModuleKind>? LeftOrder { get; set; }

    /// <summary>
    /// Custom left-to-right module order on the right side of the bar. Null together with
    /// <see cref="LeftOrder"/> means the built-in default order; an empty list intentionally
    /// keeps the right side empty.
    /// </summary>
    public List<TopBarModuleKind>? RightOrder { get; set; }

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

    /// <summary>Whether the network module shows the connected Wi-Fi SSID in the bar itself.
    /// Machine-local and off by default (null or false) to avoid leaking network names during
    /// screen recording or streaming; never exported with a theme.</summary>
    public bool? ShowWifiName { get; set; }

    /// <summary>Whether the network module shows only the signal/ethernet icon without any
    /// "WLAN" or SSID text label; tooltip still carries full connection details. Machine-local
    /// and off by default (null or false).</summary>
    public bool? NetworkIconOnly { get; set; }

    /// <summary>Whether the centred search entry opens the inline search popup instead of the
    /// overview card. Machine-local and off by default (null or false) so existing profiles keep
    /// the behaviour they had before the popup existed; never exported with a theme.</summary>
    public bool? SearchInline { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public static class TopBarRules
{
    public static IReadOnlyList<TopBarModuleKind> ModuleOrder { get; } =
        Enum.GetValues<TopBarModuleKind>();

    public static IReadOnlyList<TopBarModuleKind> DefaultLeftOrder { get; } =
    [
        TopBarModuleKind.TodoSummary,
        TopBarModuleKind.Weather,
        TopBarModuleKind.Performance
    ];

    public static IReadOnlyList<TopBarModuleKind> DefaultRightOrder { get; } =
    [
        TopBarModuleKind.Battery,
        TopBarModuleKind.Network,
        TopBarModuleKind.Volume,
        TopBarModuleKind.TrayOverflow,
        TopBarModuleKind.InputMethod,
        TopBarModuleKind.Attention,
        TopBarModuleKind.Clock
    ];

    public static bool IsOrderable(TopBarModuleKind kind) =>
        Enum.IsDefined(kind) && kind != TopBarModuleKind.Search;

    public static string ModuleLabel(TopBarModuleKind kind) => kind switch
    {
        TopBarModuleKind.TodoSummary => "待办概要",
        TopBarModuleKind.Weather => "天气",
        TopBarModuleKind.Performance => "性能",
        TopBarModuleKind.Attention => "消息提示",
        TopBarModuleKind.InputMethod => "输入法",
        TopBarModuleKind.Volume => "音量",
        TopBarModuleKind.Network => "网络",
        TopBarModuleKind.Battery => "电量",
        TopBarModuleKind.Clock => "时钟与日期",
        TopBarModuleKind.Search => "搜索",
        TopBarModuleKind.TrayOverflow => "系统托盘",
        _ => kind.ToString()
    };

    public static (List<TopBarModuleKind> Left, List<TopBarModuleKind> Right) NormalizeOrder(
        IEnumerable<TopBarModuleKind>? left,
        IEnumerable<TopBarModuleKind>? right)
    {
        if (left is null && right is null)
        {
            return ([.. DefaultLeftOrder], [.. DefaultRightOrder]);
        }

        var normalizedLeft = new List<TopBarModuleKind>();
        var normalizedRight = new List<TopBarModuleKind>();
        var seen = new HashSet<TopBarModuleKind>();

        if (left is not null)
        {
            foreach (var kind in left)
            {
                if (IsOrderable(kind) && seen.Add(kind))
                {
                    normalizedLeft.Add(kind);
                }
            }
        }

        if (right is not null)
        {
            foreach (var kind in right)
            {
                if (IsOrderable(kind) && seen.Add(kind))
                {
                    normalizedRight.Add(kind);
                }
            }
        }

        foreach (var kind in DefaultLeftOrder)
        {
            if (seen.Add(kind))
            {
                normalizedLeft.Add(kind);
            }
        }

        foreach (var kind in DefaultRightOrder)
        {
            if (seen.Add(kind))
            {
                normalizedRight.Add(kind);
            }
        }

        return (normalizedLeft, normalizedRight);
    }

    public static (IReadOnlyList<TopBarModuleKind> Left, IReadOnlyList<TopBarModuleKind> Right) ResolveOrder(
        TopBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.LeftOrder is null && state.RightOrder is null)
        {
            return (DefaultLeftOrder, DefaultRightOrder);
        }

        return NormalizeOrder(state.LeftOrder, state.RightOrder);
    }

    public static bool IsDefaultOrder(TopBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.LeftOrder is null && state.RightOrder is null)
        {
            return true;
        }

        var (left, right) = NormalizeOrder(state.LeftOrder, state.RightOrder);
        return left.SequenceEqual(DefaultLeftOrder) && right.SequenceEqual(DefaultRightOrder);
    }

    public static string OrderSignature(TopBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var (left, right) = ResolveOrder(state);
        return $"{string.Join(',', left)}|{string.Join(',', right)}";
    }

    public static bool MoveModule(
        TopBarState state,
        TopBarModuleKind kind,
        int delta,
        Func<TopBarModuleKind, bool>? isAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!IsOrderable(kind) || delta == 0)
        {
            return false;
        }

        var (left, right) = NormalizeOrder(state.LeftOrder, state.RightOrder);
        var list = left.Contains(kind) ? left : right;
        var index = list.IndexOf(kind);
        if (index < 0)
        {
            return false;
        }

        var step = delta < 0 ? -1 : 1;
        var target = index + step;
        while (target >= 0 && target < list.Count
               && isAvailable is not null && !isAvailable(list[target]))
        {
            target += step;
        }

        if (target < 0 || target >= list.Count)
        {
            return false;
        }

        list.RemoveAt(index);
        list.Insert(target, kind);
        StoreOrder(state, left, right);
        return true;
    }

    public static bool MoveModuleToSide(TopBarState state, TopBarModuleKind kind, TopBarModuleSide targetSide)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!IsOrderable(kind))
        {
            return false;
        }

        var (left, right) = NormalizeOrder(state.LeftOrder, state.RightOrder);
        var source = targetSide == TopBarModuleSide.Left ? right : left;
        var destination = targetSide == TopBarModuleSide.Left ? left : right;
        if (!source.Remove(kind))
        {
            return false;
        }

        destination.Add(kind);
        StoreOrder(state, left, right);
        return true;
    }

    public static bool ResetOrder(TopBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (IsDefaultOrder(state))
        {
            state.LeftOrder = null;
            state.RightOrder = null;
            return false;
        }

        state.LeftOrder = null;
        state.RightOrder = null;
        return true;
    }

    private static void StoreOrder(
        TopBarState state,
        List<TopBarModuleKind> left,
        List<TopBarModuleKind> right)
    {
        if (left.SequenceEqual(DefaultLeftOrder) && right.SequenceEqual(DefaultRightOrder))
        {
            state.LeftOrder = null;
            state.RightOrder = null;
            return;
        }

        state.LeftOrder = left;
        state.RightOrder = right;
    }

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
        if (!Enum.IsDefined(state.DisplayMode))
        {
            state.DisplayMode = TopBarDisplayMode.FullWidth;
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

        if (state.LeftOrder is not null || state.RightOrder is not null)
        {
            var (left, right) = NormalizeOrder(state.LeftOrder, state.RightOrder);
            StoreOrder(state, left, right);
        }
    }
}
