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

public enum TopBarDensityPreset
{
    MacMinimal,
    Balanced,
    Detailed
}

public enum TopBarVolumeStyle
{
    IconAndPercent,
    IconOnly
}

public enum TopBarBatteryStyle
{
    IconAndPercent,
    IconOnly
}

public enum TopBarInputMethodStyle
{
    BadgeAndName,
    BadgeOnly
}

public enum TopBarWeatherStyle
{
    Full,
    Compact,
    TempAndCondition
}

public enum TopBarPerformanceStyle
{
    Full,
    Compact,
    CpuOnly
}

public enum TopBarClockStyle
{
    Full,
    DateAndTime,
    Compact
}

public enum TopBarAttentionStyle
{
    IconAndName,
    IconOnly
}

public enum TopBarSearchPlacement
{
    CenterBar,
    SideIcon
}

public enum TopBarNetworkDisplayMode
{
    IconOnly,
    IconAndWlan,
    IconAndWifiName
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

    /// <summary>Persistent physical display identity; null follows Windows primary.</summary>
    public string? MonitorKey { get; set; }

    /// <summary>Display name retained for compatibility with older configurations.</summary>
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

    /// <summary>Search engine key for the inline search popup ("bing", "google", "baidu", "custom").
    /// Machine-local and null/unknown defaults to Bing; never exported with a theme.</summary>
    public string? SearchEngine { get; set; }

    /// <summary>Custom search URL template with a <c>{0}</c> query placeholder used when
    /// <see cref="SearchEngine"/> is <c>"custom"</c>. Machine-local; never exported with a theme.</summary>
    public string? SearchCustomUrl { get; set; }

    /// <summary>Volume readout style; null preserves legacy IconAndPercent (mute still stays icon-only).</summary>
    public TopBarVolumeStyle? VolumeStyle { get; set; }

    /// <summary>Battery readout style; null preserves legacy IconAndPercent.</summary>
    public TopBarBatteryStyle? BatteryStyle { get; set; }

    /// <summary>Input method readout style; null preserves legacy BadgeAndName.</summary>
    public TopBarInputMethodStyle? InputMethodStyle { get; set; }

    /// <summary>Weather readout style; null preserves legacy Full (city + temp + condition).</summary>
    public TopBarWeatherStyle? WeatherStyle { get; set; }

    /// <summary>Performance readout style; null preserves legacy Full (CPU + RAM + up/down).</summary>
    public TopBarPerformanceStyle? PerformanceStyle { get; set; }

    /// <summary>Clock readout style; null preserves legacy Full (follows desktop time/date/lunar settings).</summary>
    public TopBarClockStyle? ClockStyle { get; set; }

    /// <summary>Attention readout style; null preserves legacy IconAndName.</summary>
    public TopBarAttentionStyle? AttentionStyle { get; set; }

    /// <summary>Whether the tray overflow entry shows only the chevron icon without text; null preserves false.</summary>
    public bool? TrayIconOnly { get; set; }

    /// <summary>Whether the search entry sits centered as a pill bar or joins left/right modules as a compact icon.</summary>
    public TopBarSearchPlacement? SearchPlacement { get; set; }

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
        IsOrderable(kind, includeSearch: false);

    public static bool IsOrderable(TopBarModuleKind kind, bool includeSearch) =>
        Enum.IsDefined(kind) && (includeSearch || kind != TopBarModuleKind.Search);

    public static TopBarVolumeStyle EffectiveVolumeStyle(TopBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.VolumeStyle is { } style && Enum.IsDefined(style)
            ? style
            : TopBarVolumeStyle.IconAndPercent;
    }

    public static TopBarBatteryStyle EffectiveBatteryStyle(TopBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.BatteryStyle is { } style && Enum.IsDefined(style)
            ? style
            : TopBarBatteryStyle.IconAndPercent;
    }

    public static TopBarInputMethodStyle EffectiveInputMethodStyle(TopBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.InputMethodStyle is { } style && Enum.IsDefined(style)
            ? style
            : TopBarInputMethodStyle.BadgeAndName;
    }

    public static TopBarWeatherStyle EffectiveWeatherStyle(TopBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.WeatherStyle is { } style && Enum.IsDefined(style)
            ? style
            : TopBarWeatherStyle.Full;
    }

    public static TopBarPerformanceStyle EffectivePerformanceStyle(TopBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.PerformanceStyle is { } style && Enum.IsDefined(style)
            ? style
            : TopBarPerformanceStyle.Full;
    }

    public static TopBarClockStyle EffectiveClockStyle(TopBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.ClockStyle is { } style && Enum.IsDefined(style)
            ? style
            : TopBarClockStyle.Full;
    }

    public static TopBarAttentionStyle EffectiveAttentionStyle(TopBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.AttentionStyle is { } style && Enum.IsDefined(style)
            ? style
            : TopBarAttentionStyle.IconAndName;
    }

    public static bool EffectiveTrayIconOnly(TopBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.TrayIconOnly == true;
    }

    public static TopBarSearchPlacement EffectiveSearchPlacement(TopBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.SearchPlacement is { } placement && Enum.IsDefined(placement)
            ? placement
            : TopBarSearchPlacement.CenterBar;
    }

    public static TopBarNetworkDisplayMode EffectiveNetworkDisplayMode(TopBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.NetworkIconOnly == true)
        {
            return TopBarNetworkDisplayMode.IconOnly;
        }
        return state.ShowWifiName == true
            ? TopBarNetworkDisplayMode.IconAndWifiName
            : TopBarNetworkDisplayMode.IconAndWlan;
    }

    public static void SetNetworkDisplayMode(TopBarState state, TopBarNetworkDisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(state);
        switch (mode)
        {
            case TopBarNetworkDisplayMode.IconOnly:
                state.NetworkIconOnly = true;
                state.ShowWifiName = false;
                break;
            case TopBarNetworkDisplayMode.IconAndWifiName:
                state.NetworkIconOnly = false;
                state.ShowWifiName = true;
                break;
            default:
                state.NetworkIconOnly = false;
                state.ShowWifiName = false;
                break;
        }
    }

    public static bool HasExplicitDisplayStyles(TopBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.VolumeStyle is not null
            || state.BatteryStyle is not null
            || state.InputMethodStyle is not null
            || state.WeatherStyle is not null
            || state.PerformanceStyle is not null
            || state.ClockStyle is not null
            || state.AttentionStyle is not null
            || state.TrayIconOnly is not null
            || state.SearchPlacement is not null
            || state.NetworkIconOnly == true
            || state.ShowWifiName == true;
    }

    public static void ApplyDensityPreset(TopBarState state, TopBarDensityPreset preset)
    {
        ArgumentNullException.ThrowIfNull(state);
        switch (preset)
        {
            case TopBarDensityPreset.MacMinimal:
                state.VolumeStyle = TopBarVolumeStyle.IconOnly;
                SetNetworkDisplayMode(state, TopBarNetworkDisplayMode.IconOnly);
                state.BatteryStyle = TopBarBatteryStyle.IconOnly;
                state.InputMethodStyle = TopBarInputMethodStyle.BadgeOnly;
                state.TrayIconOnly = true;
                state.WeatherStyle = TopBarWeatherStyle.Compact;
                state.PerformanceStyle = TopBarPerformanceStyle.CpuOnly;
                state.ClockStyle = TopBarClockStyle.Compact;
                state.AttentionStyle = TopBarAttentionStyle.IconOnly;
                state.SearchPlacement = TopBarSearchPlacement.SideIcon;
                break;
            case TopBarDensityPreset.Balanced:
                state.VolumeStyle = TopBarVolumeStyle.IconOnly;
                SetNetworkDisplayMode(state, TopBarNetworkDisplayMode.IconOnly);
                state.BatteryStyle = TopBarBatteryStyle.IconAndPercent;
                state.InputMethodStyle = TopBarInputMethodStyle.BadgeOnly;
                state.TrayIconOnly = true;
                state.WeatherStyle = TopBarWeatherStyle.TempAndCondition;
                state.PerformanceStyle = TopBarPerformanceStyle.Compact;
                state.ClockStyle = TopBarClockStyle.DateAndTime;
                state.AttentionStyle = TopBarAttentionStyle.IconAndName;
                state.SearchPlacement = TopBarSearchPlacement.CenterBar;
                break;
            default:
                state.VolumeStyle = TopBarVolumeStyle.IconAndPercent;
                state.NetworkIconOnly = false;
                state.BatteryStyle = TopBarBatteryStyle.IconAndPercent;
                state.InputMethodStyle = TopBarInputMethodStyle.BadgeAndName;
                state.TrayIconOnly = false;
                state.WeatherStyle = TopBarWeatherStyle.Full;
                state.PerformanceStyle = TopBarPerformanceStyle.Full;
                state.ClockStyle = TopBarClockStyle.Full;
                state.AttentionStyle = TopBarAttentionStyle.IconAndName;
                state.SearchPlacement = TopBarSearchPlacement.CenterBar;
                break;
        }
    }

    public static TopBarDensityPreset? DetectDensityPreset(TopBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var vol = EffectiveVolumeStyle(state);
        var net = EffectiveNetworkDisplayMode(state);
        var bat = EffectiveBatteryStyle(state);
        var ime = EffectiveInputMethodStyle(state);
        var tray = EffectiveTrayIconOnly(state);
        var weather = EffectiveWeatherStyle(state);
        var perf = EffectivePerformanceStyle(state);
        var clock = EffectiveClockStyle(state);
        var attention = EffectiveAttentionStyle(state);
        var search = EffectiveSearchPlacement(state);

        if (vol == TopBarVolumeStyle.IconOnly
            && net == TopBarNetworkDisplayMode.IconOnly
            && bat == TopBarBatteryStyle.IconOnly
            && ime == TopBarInputMethodStyle.BadgeOnly
            && tray
            && weather == TopBarWeatherStyle.Compact
            && perf == TopBarPerformanceStyle.CpuOnly
            && clock == TopBarClockStyle.Compact
            && attention == TopBarAttentionStyle.IconOnly
            && search == TopBarSearchPlacement.SideIcon)
        {
            return TopBarDensityPreset.MacMinimal;
        }

        if (vol == TopBarVolumeStyle.IconOnly
            && net == TopBarNetworkDisplayMode.IconOnly
            && bat == TopBarBatteryStyle.IconAndPercent
            && ime == TopBarInputMethodStyle.BadgeOnly
            && tray
            && weather == TopBarWeatherStyle.TempAndCondition
            && perf == TopBarPerformanceStyle.Compact
            && clock == TopBarClockStyle.DateAndTime
            && attention == TopBarAttentionStyle.IconAndName
            && search == TopBarSearchPlacement.CenterBar)
        {
            return TopBarDensityPreset.Balanced;
        }

        if (vol == TopBarVolumeStyle.IconAndPercent
            && net != TopBarNetworkDisplayMode.IconOnly
            && bat == TopBarBatteryStyle.IconAndPercent
            && ime == TopBarInputMethodStyle.BadgeAndName
            && !tray
            && weather == TopBarWeatherStyle.Full
            && perf == TopBarPerformanceStyle.Full
            && clock == TopBarClockStyle.Full
            && attention == TopBarAttentionStyle.IconAndName
            && search == TopBarSearchPlacement.CenterBar)
        {
            return TopBarDensityPreset.Detailed;
        }

        return null;
    }

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
        IEnumerable<TopBarModuleKind>? right,
        bool includeSearch = false)
    {
        if (left is null && right is null)
        {
            var defaultLeft = new List<TopBarModuleKind>(DefaultLeftOrder);
            var defaultRight = new List<TopBarModuleKind>(DefaultRightOrder);
            if (includeSearch)
            {
                InsertDefaultSearch(defaultRight);
            }
            return (defaultLeft, defaultRight);
        }

        var normalizedLeft = new List<TopBarModuleKind>();
        var normalizedRight = new List<TopBarModuleKind>();
        var seen = new HashSet<TopBarModuleKind>();

        if (left is not null)
        {
            foreach (var kind in left)
            {
                if (IsOrderable(kind, includeSearch) && seen.Add(kind))
                {
                    normalizedLeft.Add(kind);
                }
            }
        }

        if (right is not null)
        {
            foreach (var kind in right)
            {
                if (IsOrderable(kind, includeSearch) && seen.Add(kind))
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

        if (includeSearch && seen.Add(TopBarModuleKind.Search))
        {
            InsertDefaultSearch(normalizedRight);
        }

        return (normalizedLeft, normalizedRight);
    }

    private static void InsertDefaultSearch(List<TopBarModuleKind> right)
    {
        var clockIndex = right.IndexOf(TopBarModuleKind.Clock);
        if (clockIndex >= 0)
        {
            right.Insert(clockIndex, TopBarModuleKind.Search);
        }
        else
        {
            right.Add(TopBarModuleKind.Search);
        }
    }

    public static (IReadOnlyList<TopBarModuleKind> Left, IReadOnlyList<TopBarModuleKind> Right) ResolveOrder(
        TopBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var includeSearch = EffectiveSearchPlacement(state) == TopBarSearchPlacement.SideIcon;
        if (state.LeftOrder is null && state.RightOrder is null)
        {
            return includeSearch
                ? NormalizeOrder(null, null, includeSearch: true)
                : (DefaultLeftOrder, DefaultRightOrder);
        }

        return NormalizeOrder(state.LeftOrder, state.RightOrder, includeSearch);
    }

    public static bool IsDefaultOrder(TopBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var includeSearch = EffectiveSearchPlacement(state) == TopBarSearchPlacement.SideIcon;
        if (state.LeftOrder is null && state.RightOrder is null)
        {
            return true;
        }

        var (left, right) = NormalizeOrder(state.LeftOrder, state.RightOrder, includeSearch);
        var (defaultLeft, defaultRight) = NormalizeOrder(null, null, includeSearch);
        return left.SequenceEqual(defaultLeft) && right.SequenceEqual(defaultRight);
    }

    public static string OrderSignature(TopBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var (left, right) = ResolveOrder(state);
        return $"{string.Join(',', left)}|{string.Join(',', right)}";
    }

    public static string LayoutSignature(TopBarState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return $"{OrderSignature(state)}|{EffectiveSearchPlacement(state)}|{EffectivePerformanceStyle(state)}|{EffectiveClockStyle(state)}|{EffectiveAttentionStyle(state)}|{EffectiveTrayIconOnly(state)}";
    }

    private static bool ShouldPreserveSearchInStoredOrder(TopBarState state) =>
        EffectiveSearchPlacement(state) == TopBarSearchPlacement.SideIcon
        || state.LeftOrder?.Contains(TopBarModuleKind.Search) == true
        || state.RightOrder?.Contains(TopBarModuleKind.Search) == true;

    public static bool MoveModule(
        TopBarState state,
        TopBarModuleKind kind,
        int delta,
        Func<TopBarModuleKind, bool>? isAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var searchVisibleOnSides = EffectiveSearchPlacement(state) == TopBarSearchPlacement.SideIcon;
        if (!IsOrderable(kind, searchVisibleOnSides) || delta == 0)
        {
            return false;
        }

        var preserveSearch = ShouldPreserveSearchInStoredOrder(state);
        var (left, right) = NormalizeOrder(state.LeftOrder, state.RightOrder, preserveSearch);
        var list = left.Contains(kind) ? left : right;
        var index = list.IndexOf(kind);
        if (index < 0)
        {
            return false;
        }

        Func<TopBarModuleKind, bool>? effectiveAvailable = searchVisibleOnSides
            ? isAvailable
            : k => k != TopBarModuleKind.Search && (isAvailable is null || isAvailable(k));

        var step = delta < 0 ? -1 : 1;
        var target = index + step;
        while (target >= 0 && target < list.Count
               && effectiveAvailable is not null && !effectiveAvailable(list[target]))
        {
            target += step;
        }

        if (target < 0 || target >= list.Count)
        {
            return false;
        }

        list.RemoveAt(index);
        list.Insert(target, kind);
        StoreOrder(state, left, right, preserveSearch);
        return true;
    }

    public static bool MoveModuleToSide(TopBarState state, TopBarModuleKind kind, TopBarModuleSide targetSide)
    {
        ArgumentNullException.ThrowIfNull(state);
        var searchVisibleOnSides = EffectiveSearchPlacement(state) == TopBarSearchPlacement.SideIcon;
        if (!IsOrderable(kind, searchVisibleOnSides))
        {
            return false;
        }

        var preserveSearch = ShouldPreserveSearchInStoredOrder(state);
        var (left, right) = NormalizeOrder(state.LeftOrder, state.RightOrder, preserveSearch);
        var source = targetSide == TopBarModuleSide.Left ? right : left;
        var destination = targetSide == TopBarModuleSide.Left ? left : right;
        if (!source.Remove(kind))
        {
            return false;
        }

        destination.Add(kind);
        StoreOrder(state, left, right, preserveSearch);
        return true;
    }

    /// <summary>
    /// Places <paramref name="kind"/> at <paramref name="targetIndex"/> on <paramref name="targetSide"/>.
    /// Dropping <see cref="TopBarModuleKind.Search"/> onto a side switches its placement to
    /// <see cref="TopBarSearchPlacement.SideIcon"/> automatically.
    /// </summary>
    public static bool PlaceModule(
        TopBarState state,
        TopBarModuleKind kind,
        TopBarModuleSide targetSide,
        int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!Enum.IsDefined(kind))
        {
            return false;
        }

        var placementChanged = false;
        if (kind == TopBarModuleKind.Search && EffectiveSearchPlacement(state) != TopBarSearchPlacement.SideIcon)
        {
            state.SearchPlacement = TopBarSearchPlacement.SideIcon;
            placementChanged = true;
        }

        var searchVisibleOnSides = EffectiveSearchPlacement(state) == TopBarSearchPlacement.SideIcon;
        var preserveSearch = ShouldPreserveSearchInStoredOrder(state);
        var (left, right) = NormalizeOrder(state.LeftOrder, state.RightOrder, preserveSearch);
        var destination = targetSide == TopBarModuleSide.Left ? left : right;
        var currentSide = left.Contains(kind) ? TopBarModuleSide.Left : TopBarModuleSide.Right;
        var currentIndex = destination.IndexOf(kind);

        left.Remove(kind);
        right.Remove(kind);

        var resolvedIndex = targetIndex;
        if (!searchVisibleOnSides && preserveSearch)
        {
            var searchIndex = destination.IndexOf(TopBarModuleKind.Search);
            if (searchIndex >= 0 && resolvedIndex < int.MaxValue && searchIndex <= resolvedIndex)
            {
                resolvedIndex++;
            }
        }

        var clampedIndex = Math.Clamp(resolvedIndex, 0, destination.Count);
        destination.Insert(clampedIndex, kind);
        StoreOrder(state, left, right, preserveSearch);
        return placementChanged || currentSide != targetSide || currentIndex != clampedIndex;
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
        List<TopBarModuleKind> right,
        bool includeSearch)
    {
        var (defaultLeft, defaultRight) = NormalizeOrder(null, null, includeSearch);
        if (left.SequenceEqual(defaultLeft) && right.SequenceEqual(defaultRight))
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
        if (state.VolumeStyle is { } volumeStyle && !Enum.IsDefined(volumeStyle))
        {
            state.VolumeStyle = null;
        }
        if (state.BatteryStyle is { } batteryStyle && !Enum.IsDefined(batteryStyle))
        {
            state.BatteryStyle = null;
        }
        if (state.InputMethodStyle is { } imeStyle && !Enum.IsDefined(imeStyle))
        {
            state.InputMethodStyle = null;
        }
        if (state.WeatherStyle is { } weatherStyle && !Enum.IsDefined(weatherStyle))
        {
            state.WeatherStyle = null;
        }
        if (state.PerformanceStyle is { } perfStyle && !Enum.IsDefined(perfStyle))
        {
            state.PerformanceStyle = null;
        }
        if (state.ClockStyle is { } clockStyle && !Enum.IsDefined(clockStyle))
        {
            state.ClockStyle = null;
        }
        if (state.AttentionStyle is { } attentionStyle && !Enum.IsDefined(attentionStyle))
        {
            state.AttentionStyle = null;
        }
        if (state.SearchPlacement is { } searchPlacement && !Enum.IsDefined(searchPlacement))
        {
            state.SearchPlacement = null;
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
            var preserveSearch = ShouldPreserveSearchInStoredOrder(state);
            var (left, right) = NormalizeOrder(state.LeftOrder, state.RightOrder, preserveSearch);
            StoreOrder(state, left, right, preserveSearch);
        }
    }
}
