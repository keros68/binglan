namespace BingLan.Core.Models;

public enum CardShadow
{
    None,
    Soft,
    Strong
}

public enum MotionLevel
{
    Standard,
    Reduced,
    Off
}

/// <summary>The material every card shares: light or dark frosted glass, or no backing at all.</summary>
public enum GlassMode
{
    Light,
    Dark,
    Clear
}

/// <summary>
/// Card text colour: the material's own, white or dark to suit the wallpaper, or a colour
/// the user picked.
/// </summary>
public enum TextInk
{
    Auto,
    White,
    Dark,
    Custom
}

/// <summary>A named set of card colours: body, title bar and text.</summary>
public sealed record CardPalette(string Key, string Name, string Body, string Header, string Text);

/// <summary>The dock's colours from the shared material, as #AARRGGBB.</summary>
public sealed record DockGlass(string Surface, string Border, string RunningDot);

/// <summary>The colours one card gets from the shared material.</summary>
public sealed record GlassAppearance(
    string BackgroundColor,
    double BackgroundOpacity,
    string HeaderColor,
    double HeaderOpacity,
    string TextColor,
    string ItemSurfaceColor);

/// <summary>
/// Desktop-wide style shared by every card and the dock: card shadow, the gap cards keep
/// when snapped next to each other, and how much the dock animates. Part of a theme.
/// </summary>
public sealed class DesktopStyleState
{
    private double _cardSpacing = DesktopStyleRules.DefaultCardSpacing;

    public CardShadow CardShadow { get; set; } = CardShadow.None;

    public double CardSpacing
    {
        get => _cardSpacing;
        set => _cardSpacing = DesktopStyleRules.CoerceCardSpacing(value);
    }

    public MotionLevel Motion { get; set; } = MotionLevel.Standard;

    public GlassMode Glass { get; set; } = GlassMode.Light;

    /// <summary>Key of the light-glass palette, such as "ice" or "matcha".</summary>
    public string Palette { get; set; } = DesktopStyleRules.DefaultPalette;

    public TextInk TextInk { get; set; } = TextInk.Auto;

    private string _customTextColor = "#FFFFFF";

    /// <summary>The text colour used while TextInk is Custom, as #RRGGBB.</summary>
    public string CustomTextColor
    {
        get => _customTextColor;
        set => _customTextColor = WidgetAppearanceRules.CoerceColorOrDefault(value, "#FFFFFF");
    }

    private string _accentColor = DesktopStyleRules.DefaultAccentColor;

    /// <summary>The lively colour of the performance bars and download rate, as #RRGGBB.</summary>
    public string AccentColor
    {
        get => _accentColor;
        set => _accentColor = WidgetAppearanceRules.CoerceColorOrDefault(value, DesktopStyleRules.DefaultAccentColor);
    }

    private double _glassDensity = DesktopStyleRules.DefaultGlassDensity;

    /// <summary>How opaque the glass is, from barely there to nearly solid.</summary>
    public double GlassDensity
    {
        get => _glassDensity;
        set => _glassDensity = DesktopStyleRules.CoerceGlassDensity(value);
    }
}

public static class DesktopStyleRules
{
    public const string DefaultAccentColor = "#1EA9FF";

    public const double DefaultCardSpacing = 16d;
    public const double MinimumCardSpacing = 0d;
    public const double MaximumCardSpacing = 40d;
    public const double MinimumGlassDensity = 0.05d;
    public const double MaximumGlassDensity = 0.96d;
    public const double DefaultGlassDensity = WidgetAppearanceRules.DefaultBackgroundOpacity;
    public const string DefaultPalette = "ice";

    /// <summary>Light-glass palettes, the first being the original ice blue.</summary>
    public static IReadOnlyList<CardPalette> Palettes { get; } =
    [
        new("ice", "冰蓝", WidgetAppearanceRules.DefaultBackgroundColor, WidgetAppearanceRules.DefaultHeaderColor, WidgetAppearanceRules.DefaultTextColor),
        new("milk-tea", "奶茶", "#F6EBDD", "#D9B99B", "#4A3728"),
        new("matcha", "抹茶", "#EEF5E6", "#A9C79A", "#2F4A2A"),
        new("nord", "北欧", "#ECEFF4", "#88A0BF", "#2E3440"),
        new("rosy", "玫瑰", "#FBEDEF", "#E3A9B4", "#4E2A33"),
        new("mint", "薄荷", "#E8F7F2", "#8FD1BE", "#1F4A40")
    ];

    public static CardPalette GetPalette(string? key) =>
        Palettes.FirstOrDefault(palette => palette.Key == key) ?? Palettes[0];

    public static double CoerceGlassDensity(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, MinimumGlassDensity, MaximumGlassDensity)
            : DefaultGlassDensity;

    /// <summary>
    /// The colours the shared material gives a card. Only one layer is translucent: the
    /// card body. The title bar of the light and dark glass stays nearly solid, as before,
    /// and rows inside a card get a faint surface that the clear mode removes, so stacked
    /// translucent layers never turn the card milky. Text follows the material (dark on
    /// light glass, white on dark glass and on a clear card) unless white or dark was chosen.
    /// </summary>
    public static GlassAppearance Appearance(DesktopStyleState style)
    {
        ArgumentNullException.ThrowIfNull(style);
        var material = Material(style);
        return style.TextInk switch
        {
            TextInk.White => material with { TextColor = "#FFFFFF" },
            TextInk.Dark => material with { TextColor = WidgetAppearanceRules.DefaultTextColor },
            TextInk.Custom => material with { TextColor = style.CustomTextColor },
            _ => material
        };
    }

    private static GlassAppearance Material(DesktopStyleState style)
    {
        var density = style.GlassDensity;
        return style.Glass switch
        {
            GlassMode.Dark => new GlassAppearance(
                "#18222F", density, "#2C3E57", Math.Min(0.97d, density + 0.5d), "#FFFFFF", "#1FFFFFFF"),
            GlassMode.Clear => new GlassAppearance(
                "#FFFFFF",
                WidgetAppearanceRules.MinimumBackgroundOpacity,
                "#FFFFFF",
                0d,
                "#FFFFFF",
                "#00FFFFFF"),
            _ => FromPalette(GetPalette(style.Palette), density)
        };
    }

    /// <summary>
    /// The dock follows the card material: a slightly denser body of the same colour, a
    /// dark bar on dark glass, and only the icons on a clear desktop. Its surface never
    /// goes fully transparent so the gaps between icons still take clicks.
    /// </summary>
    public static DockGlass Dock(DesktopStyleState style, DockState? dock = null)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (dock is { FollowCardLook: false })
        {
            return CustomDock(dock.SurfaceColor, dock.SurfaceOpacity);
        }
        var alpha = Math.Min(0.96d, style.GlassDensity + 0.5d);
        return style.Glass switch
        {
            GlassMode.Dark => new DockGlass(WithAlpha("#18222F", alpha), "#33FFFFFF", "#E6FFFFFF"),
            GlassMode.Clear => new DockGlass("#01FFFFFF", "#00FFFFFF", "#E6FFFFFF"),
            _ => new DockGlass(WithAlpha(GetPalette(style.Palette).Body, alpha), "#BFFFFFFF", "#C21F3A55")
        };
    }

    /// <summary>
    /// The top bar's surface colours: the same edge-surface family as the dock, either
    /// following the card material or the bar's own colour and opacity.
    /// </summary>
    public static DockGlass TopBar(DesktopStyleState style, TopBarState? topBar = null)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (topBar is { FollowCardLook: false })
        {
            return CustomDock(topBar.SurfaceColor, topBar.SurfaceOpacity);
        }
        return Dock(style);
    }

    /// <summary>
    /// A dock in its own colour and opacity. A nearly clear dock drops its border; the
    /// running dot is dark on a light, mostly opaque colour and white otherwise.
    /// </summary>
    public static DockGlass CustomDock(string color, double opacity)
    {
        var rgb = WidgetAppearanceRules.CoerceColorOrDefault(color, DockState.DefaultSurfaceColor);
        var alpha = Math.Max(1d / 255d, Math.Clamp(opacity, 0d, 1d));
        var red = Convert.ToInt32(rgb.Substring(1, 2), 16);
        var green = Convert.ToInt32(rgb.Substring(3, 2), 16);
        var blue = Convert.ToInt32(rgb.Substring(5, 2), 16);
        var light = (0.2126d * red + 0.7152d * green + 0.0722d * blue) / 255d > 0.6d;
        return new DockGlass(
            WithAlpha(rgb, alpha),
            opacity < 0.05d ? "#00FFFFFF" : light ? "#BFFFFFFF" : "#33FFFFFF",
            light && opacity >= 0.5d ? "#C21F3A55" : "#E6FFFFFF");
    }

    private static string WithAlpha(string rgb, double alpha) =>
        $"#{(int)Math.Round(Math.Clamp(alpha, 0d, 1d) * 255):X2}{rgb.TrimStart('#')}";

    /// <summary>The light-glass colours of one palette at a body opacity.</summary>
    public static GlassAppearance FromPalette(CardPalette palette, double density) => new(
        palette.Body,
        density,
        palette.Header,
        Math.Min(WidgetAppearanceRules.DefaultHeaderOpacity, density + 0.65d),
        palette.Text,
        "#7AFFFFFF");

    /// <summary>
    /// Text on surfaces that are plain text over the wallpaper (time, date, greeting):
    /// white unless dark text was chosen.
    /// </summary>
    public static string NakedTextColor(DesktopStyleState style) => style.TextInk switch
    {
        TextInk.Dark => WidgetAppearanceRules.DefaultTextColor,
        TextInk.Custom => style.CustomTextColor,
        _ => "#FFFFFF"
    };

    public static double CoerceCardSpacing(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, MinimumCardSpacing, MaximumCardSpacing)
            : DefaultCardSpacing;

    public static void Normalize(DesktopStyleState style)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (!Enum.IsDefined(style.CardShadow))
        {
            style.CardShadow = CardShadow.None;
        }
        if (!Enum.IsDefined(style.Motion))
        {
            style.Motion = MotionLevel.Standard;
        }
        if (!Enum.IsDefined(style.Glass))
        {
            style.Glass = GlassMode.Light;
        }
        if (!Enum.IsDefined(style.TextInk))
        {
            style.TextInk = TextInk.Auto;
        }
        style.GlassDensity = style.GlassDensity;
        style.Palette = GetPalette(style.Palette).Key;
        style.CardSpacing = style.CardSpacing;
    }

    /// <summary>
    /// The dock hover zoom for a motion level; the system "show animations" switch turns
    /// it off regardless of the theme.
    /// </summary>
    public static double HoverZoom(MotionLevel motion, bool systemAnimationsEnabled) =>
        !systemAnimationsEnabled
            ? 1d
            : motion switch
            {
                MotionLevel.Off => 1d,
                MotionLevel.Reduced => 1.05d,
                _ => 1.12d
            };

    public static TimeSpan HoverZoomDuration(MotionLevel motion) =>
        motion == MotionLevel.Reduced ? TimeSpan.FromMilliseconds(80) : TimeSpan.FromMilliseconds(120);
}
