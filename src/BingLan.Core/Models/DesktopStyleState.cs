using System.Text.Json;
using System.Text.Json.Serialization;

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
/// <summary>The tint of rows, buttons and calendar cells inside a light-glass card.</summary>
public enum SurfaceTone
{
    Light,
    Dark
}

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

/// <summary>The body and text colours one built-in card gets from the shared material.</summary>
public sealed record ComponentGlass(string BackgroundColor, double BackgroundOpacity, string TextColor);

/// <summary>The colours one card gets from the shared material.</summary>
public sealed record GlassAppearance(
    string BackgroundColor,
    double BackgroundOpacity,
    string HeaderColor,
    double HeaderOpacity,
    string TextColor,
    string ItemSurfaceColor);

/// <summary>
/// Resolved whole-card rendering look derived from a saved card appearance and global style
/// without mutating persisted user settings.
/// </summary>
public sealed record RenderedCardLook(
    string BackgroundColor,
    double BackgroundOpacity,
    string ItemSurfaceColor,
    string SubtleControlColor,
    string ControlColor,
    CardShadow EffectiveShadow,
    string NoteBodySurfaceColor);

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

    /// <summary>
    /// Whether dragging aligns cards to the desktop grid instead of to nearby cards. Off
    /// by default, so cards keep moving freely until the user asks for the grid.
    /// </summary>
    public bool GridSnap { get; set; }

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

    /// <summary>Whether surfaces inside a light-glass card are tinted white or dark.</summary>
    public SurfaceTone ItemSurfaceTone { get; set; } = SurfaceTone.Light;

    private double _itemSurfaceStrength = DesktopStyleRules.DefaultItemSurfaceStrength;

    /// <summary>How strong the surfaces inside a light-glass card are, as the row opacity.</summary>
    public double ItemSurfaceStrength
    {
        get => _itemSurfaceStrength;
        set => _itemSurfaceStrength = DesktopStyleRules.CoerceItemSurfaceStrength(value);
    }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public DesktopStyleState Clone() => new()
    {
        CardShadow = CardShadow,
        CardSpacing = CardSpacing,
        GridSnap = GridSnap,
        Motion = Motion,
        Glass = Glass,
        Palette = Palette,
        TextInk = TextInk,
        CustomTextColor = CustomTextColor,
        AccentColor = AccentColor,
        GlassDensity = GlassDensity,
        ItemSurfaceTone = ItemSurfaceTone,
        ItemSurfaceStrength = ItemSurfaceStrength
    };
}

public static class DesktopStyleRules
{
    public const string DefaultAccentColor = "#1EA9FF";

    public const double DefaultCardSpacing = 16d;
    public const double MinimumCardSpacing = 0d;
    public const double MaximumCardSpacing = 40d;

    /// <summary>The cell of the optional snap grid, in DIP; part of the desktop style.</summary>
    public const double GridCellDip = 16d;
    public const double MinimumGlassDensity = 0.05d;
    public const double MaximumGlassDensity = 0.96d;
    public const double DefaultGlassDensity = WidgetAppearanceRules.DefaultBackgroundOpacity;
    public const string DefaultPalette = "ice";
    public const double DefaultItemSurfaceStrength = 0.32d;
    public const double MaximumItemSurfaceStrength = 0.8d;

    /// <summary>Light-glass palettes, the first being the original ice blue.</summary>
    public static IReadOnlyList<CardPalette> Palettes { get; } =
    [
        new("ice", "冰蓝", WidgetAppearanceRules.DefaultBackgroundColor, WidgetAppearanceRules.DefaultHeaderColor, WidgetAppearanceRules.DefaultTextColor),
        new("milk-tea", "奶茶", "#F6EBDD", "#D9B99B", "#2E2118"),
        new("matcha", "抹茶", "#EEF5E6", "#A9C79A", "#1F331B"),
        new("nord", "北欧", "#ECEFF4", "#88A0BF", "#232933"),
        new("rosy", "玫瑰", "#FBEDEF", "#E3A9B4", "#331B22"),
        new("mint", "薄荷", "#E8F7F2", "#8FD1BE", "#16352D")
    ];

    public static CardPalette GetPalette(string? key) =>
        Palettes.FirstOrDefault(palette => palette.Key == key) ?? Palettes[0];

    public static double CoerceGlassDensity(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, MinimumGlassDensity, MaximumGlassDensity)
            : DefaultGlassDensity;

    private const string RenderedDarkBackgroundColor = "#16202C";
    private const string RenderedDarkItemSurfaceColor = "#14FFFFFF";
    private const string RenderedDarkSubtleControlColor = "#35FFFFFF";
    private const string RenderedDarkControlColor = "#1CFFFFFF";
    private const string DefaultLightControlColor = "#66FFFFFF";
    private const string SurfaceToneDarkColor = "#1B2635";

    /// <summary>
    /// The colours the shared material gives a card. Only one layer is translucent: the
    /// card body. The title bar of the light and dark glass stays nearly solid, as before,
    /// and rows inside a card get a faint surface that the clear mode removes, so stacked
    /// translucent layers never turn the card milky. Text follows the material (dark on
    /// light glass, white on dark glass and on a clear card) unless white or dark was chosen.
    /// On light glass the row surface follows the chosen tone and strength.
    /// </summary>
    private static GlassAppearance ResolveBaseLook(DesktopStyleState style)
    {
        var material = Material(style);
        return style.TextInk switch
        {
            TextInk.White => material with { TextColor = "#FFFFFF" },
            TextInk.Dark => material with { TextColor = WidgetAppearanceRules.DefaultTextColor },
            TextInk.Custom => material with { TextColor = style.CustomTextColor },
            _ => material
        };
    }

    public static GlassAppearance Appearance(DesktopStyleState style)
    {
        ArgumentNullException.ThrowIfNull(style);
        var look = ResolveBaseLook(style);
        if (style.Glass != GlassMode.Light)
        {
            return look;
        }

        return look with { ItemSurfaceColor = WithAlpha(SurfaceToneColor(style), style.ItemSurfaceStrength) };
    }

    /// <summary>
    /// Resolves the rendered whole-card look for the shared style without altering saved user settings.
    /// </summary>
    public static RenderedCardLook ResolveRenderedCard(DesktopStyleState style)
    {
        ArgumentNullException.ThrowIfNull(style);
        var look = ResolveBaseLook(style);
        return ResolveRenderedCardCore(look.BackgroundColor, look.BackgroundOpacity, look.TextColor, style);
    }

    /// <summary>
    /// Resolves the rendered whole-card look for a single card's saved appearance without mutating
    /// its persisted material, tone, or strength. When light glass is paired with light text
    /// (relative luminance &gt; 0.5), cards below 20% opacity keep their light backing with soft text
    /// shadow, capped dark item surfaces (&lt;= 0.16), and a transparent note body surface, while cards
    /// at or above 20% render as a unified dark-glass card using the user's saved opacity and add soft
    /// text shadow whenever contrast against bright wallpaper falls below 4.5:1.
    /// </summary>
    public static RenderedCardLook ResolveRenderedCard(
        WidgetAppearanceState cardAppearance,
        DesktopStyleState style)
    {
        ArgumentNullException.ThrowIfNull(cardAppearance);
        ArgumentNullException.ThrowIfNull(style);
        return ResolveRenderedCardCore(
            cardAppearance.BackgroundColor,
            cardAppearance.BackgroundOpacity,
            cardAppearance.TextColor,
            style);
    }

    /// <summary>
    /// Resolves the rendered whole-card look for a desktop component without mutating its
    /// persisted material, tone, or strength. Unbacked components whose XAML text elements
    /// already carry <c>NakedTextContrastShadow</c> (<c>TimeDate</c> and <c>Greeting</c>)
    /// keep <see cref="CardShadow.None"/> on their container so only a single shadow layer
    /// is drawn.
    /// </summary>
    public static RenderedCardLook ResolveRenderedCard(
        DesktopComponentState component,
        DesktopStyleState style)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(style);
        var hasOwnNakedContrastShadow = component.Kind is
            DesktopComponentKind.TimeDate or DesktopComponentKind.Greeting;
        return ResolveRenderedCardCore(
            component.Appearance.BackgroundColor,
            component.Appearance.BackgroundOpacity,
            component.Appearance.TextColor,
            style,
            hasOwnNakedContrastShadow);
    }

    private static RenderedCardLook ResolveRenderedCardCore(
        string backgroundColor,
        double backgroundOpacity,
        string textColor,
        DesktopStyleState style,
        bool hasOwnNakedContrastShadow = false)
    {
        if (backgroundOpacity <= WidgetAppearanceRules.MinimumBackgroundOpacity + 0.005d)
        {
            return new RenderedCardLook(
                backgroundColor,
                backgroundOpacity,
                "#00FFFFFF",
                "#00FFFFFF",
                "#00FFFFFF",
                hasOwnNakedContrastShadow
                    ? CardShadow.None
                    : ResolveContrastShadow(backgroundColor, backgroundOpacity, textColor, style.CardShadow),
                "#00FFFFFF");
        }

        var isLightGlass = ColorContrastRules.RelativeLuminance(backgroundColor) >= 0.5d;
        var isLightText = ColorContrastRules.RelativeLuminance(textColor) >
            ColorContrastRules.LightTextLuminanceThreshold;
        if (isLightGlass && isLightText)
        {
            if (backgroundOpacity < ColorContrastRules.NearClearBackgroundOpacityThreshold)
            {
                var nearClearShadow = ResolveContrastShadow(
                    backgroundColor,
                    backgroundOpacity,
                    textColor,
                    style.CardShadow);
                var effectiveStrength = Math.Min(
                    style.ItemSurfaceStrength,
                    ColorContrastRules.NearClearMaxItemSurfaceStrength);
                var darkSurface = WithCappedAlpha(
                    SurfaceToneDarkColor,
                    effectiveStrength,
                    ColorContrastRules.NearClearMaxItemSurfaceStrength);
                return new RenderedCardLook(
                    backgroundColor,
                    backgroundOpacity,
                    darkSurface,
                    WithCappedAlpha(
                        SurfaceToneDarkColor,
                        effectiveStrength * SubtleControlRatio,
                        ColorContrastRules.NearClearMaxItemSurfaceStrength * SubtleControlRatio),
                    darkSurface,
                    nearClearShadow,
                    "#00FFFFFF");
            }

            var shadow = ResolveContrastShadow(
                RenderedDarkBackgroundColor,
                backgroundOpacity,
                textColor,
                style.CardShadow);

            return new RenderedCardLook(
                RenderedDarkBackgroundColor,
                backgroundOpacity,
                RenderedDarkItemSurfaceColor,
                RenderedDarkSubtleControlColor,
                RenderedDarkControlColor,
                shadow,
                RenderedDarkItemSurfaceColor);
        }

        if (ColorContrastRules.RelativeLuminance(backgroundColor) < 0.25d)
        {
            return new RenderedCardLook(
                backgroundColor,
                backgroundOpacity,
                RenderedDarkItemSurfaceColor,
                RenderedDarkSubtleControlColor,
                RenderedDarkControlColor,
                ResolveContrastShadow(backgroundColor, backgroundOpacity, textColor, style.CardShadow),
                RenderedDarkItemSurfaceColor);
        }

        var tone = SurfaceToneColor(style);
        var controlColor = style.ItemSurfaceTone == SurfaceTone.Dark
            ? WithAlpha(tone, style.ItemSurfaceStrength)
            : DefaultLightControlColor;
        var itemSurface = WithAlpha(tone, style.ItemSurfaceStrength);
        return new RenderedCardLook(
            backgroundColor,
            backgroundOpacity,
            itemSurface,
            WithAlpha(tone, style.ItemSurfaceStrength * SubtleControlRatio),
            controlColor,
            style.CardShadow,
            itemSurface);
    }

    private static CardShadow ResolveContrastShadow(
        string backgroundColor,
        double backgroundOpacity,
        string textColor,
        CardShadow configuredShadow)
    {
        if (configuredShadow != CardShadow.None
            || ColorContrastRules.RelativeLuminance(textColor) <= ColorContrastRules.LightTextLuminanceThreshold)
        {
            return configuredShadow;
        }

        var renderedBackdrop = ColorContrastRules.CompositeLayers(
            ColorContrastRules.BrightWallpaper,
            ColorContrastRules.WithAlpha(backgroundColor, backgroundOpacity));
        var contrast = ColorContrastRules.ContrastRatio(
            ColorContrastRules.ParseHex(textColor),
            renderedBackdrop);
        return contrast < ColorContrastRules.MinimumNormalTextContrast
            ? CardShadow.Soft
            : configuredShadow;
    }

    /// <summary>
    /// The fill of small surfaces inside a card (buttons, calendar day cells). On light
    /// glass it follows the chosen tone at a little under half the row strength, which at
    /// the default strength is the original faint white.
    /// </summary>
    public static string SubtleControlColor(DesktopStyleState style)
    {
        ArgumentNullException.ThrowIfNull(style);
        return style.Glass switch
        {
            GlassMode.Clear => "#00FFFFFF",
            GlassMode.Dark => RenderedDarkSubtleControlColor,
            _ => WithAlpha(SurfaceToneColor(style), style.ItemSurfaceStrength * SubtleControlRatio)
        };
    }

    // 0x35 / 0x7A: the original button fill against the original row surface.
    private const double SubtleControlRatio = 53d / 122d;

    private static string SurfaceToneColor(DesktopStyleState style) =>
        style.ItemSurfaceTone == SurfaceTone.Dark ? SurfaceToneDarkColor : "#FFFFFF";

    public static double CoerceItemSurfaceStrength(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, 0d, MaximumItemSurfaceStrength)
            : DefaultItemSurfaceStrength;

    private static GlassAppearance Material(DesktopStyleState style)
    {
        var density = style.GlassDensity;
        return style.Glass switch
        {
            GlassMode.Dark => new GlassAppearance(
                RenderedDarkBackgroundColor, density, "#2C3E57", Math.Min(0.97d, density + 0.5d), "#FFFFFF", RenderedDarkItemSurfaceColor),
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
        var alpha = Math.Clamp(0.82d + (style.GlassDensity - DefaultGlassDensity) * 0.45d, MinimumGlassDensity, 0.96d);
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
    /// Whether the dark frosted scrim (blurred wallpaper) may sit under the top bar:
    /// the user has not turned the frosted look off and the material is dark enough
    /// for the scrim to belong — a light or nearly clear surface shows the scrim
    /// through as mud, so it yields to the plain brush.
    /// </summary>
    public static bool TopBarFrostedScrim(TopBarState? topBar, string surfaceWithAlpha) =>
        topBar is { FrostedGlass: true } && IsDarkSurface(surfaceWithAlpha);

    private static bool IsDarkSurface(string surfaceWithAlpha)
    {
        // "#AARRGGBB" as produced by WithAlpha.
        if (surfaceWithAlpha.Length < 9)
        {
            return false;
        }
        var red = Convert.ToInt32(surfaceWithAlpha.Substring(3, 2), 16);
        var green = Convert.ToInt32(surfaceWithAlpha.Substring(5, 2), 16);
        var blue = Convert.ToInt32(surfaceWithAlpha.Substring(7, 2), 16);
        return (0.2126d * red + 0.7152d * green + 0.0722d * blue) / 255d <= 0.6d;
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

    // Floors the upper bound so 8-bit quantization never rounds a capped surface strength past maxAlpha.
    private static string WithCappedAlpha(string rgb, double alpha, double maxAlpha)
    {
        var byteAlpha = Math.Min(
            (int)Math.Round(Math.Clamp(alpha, 0d, 1d) * 255),
            (int)Math.Floor(Math.Clamp(maxAlpha, 0d, 1d) * 255));
        return $"#{Math.Clamp(byteAlpha, 0, 255):X2}{rgb.TrimStart('#')}";
    }

    /// <summary>The light-glass colours of one palette at a body opacity.</summary>
    public static GlassAppearance FromPalette(CardPalette palette, double density) => new(
        palette.Body,
        density,
        palette.Header,
        Math.Min(WidgetAppearanceRules.DefaultHeaderOpacity, density + 0.65d),
        palette.Text,
        "#52FFFFFF");

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

    /// <summary>
    /// The colours the shared material gives one built-in card. Time, date and greeting
    /// are text straight on the wallpaper; on a clear desktop the quick-launch strip keeps
    /// a light frosted bar at <paramref name="clearBarOpacity"/> so its line icons stay visible.
    /// </summary>
    public static ComponentGlass ComponentLook(
        DesktopComponentKind kind,
        DesktopStyleState style,
        double clearBarOpacity)
    {
        ArgumentNullException.ThrowIfNull(style);
        var glass = Appearance(style);
        var nakedText = NakedTextColor(style);
        if (kind == DesktopComponentKind.QuickLaunch && style.Glass == GlassMode.Clear)
        {
            return new ComponentGlass("#FFFFFF", clearBarOpacity, nakedText);
        }
        return kind is DesktopComponentKind.TimeDate or DesktopComponentKind.Greeting
            ? new ComponentGlass(glass.BackgroundColor, WidgetAppearanceRules.MinimumBackgroundOpacity, nakedText)
            : new ComponentGlass(glass.BackgroundColor, glass.BackgroundOpacity, glass.TextColor);
    }

    /// <summary>
    /// Whether a built-in card still wears the colours the shared material gives it, as
    /// opposed to colours set for this card alone.
    /// </summary>
    public static bool FollowsSharedLook(
        DesktopComponentState component,
        DesktopStyleState style,
        double clearBarOpacity)
    {
        ArgumentNullException.ThrowIfNull(component);
        var look = ComponentLook(component.Kind, style, clearBarOpacity);
        var appearance = component.Appearance;
        var bodyMatches = string.Equals(appearance.BackgroundColor, look.BackgroundColor, StringComparison.OrdinalIgnoreCase)
            && string.Equals(appearance.TextColor, look.TextColor, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(appearance.BackgroundOpacity - look.BackgroundOpacity) < 0.005d;
        if (!bodyMatches || component.Kind != DesktopComponentKind.Todo)
        {
            return bodyMatches;
        }

        var glass = Appearance(style);
        return string.Equals(appearance.HeaderColor, glass.HeaderColor, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(appearance.HeaderOpacity - glass.HeaderOpacity) < 0.005d;
    }

    public static bool FollowsSharedCardLook(
        WidgetAppearanceState appearance,
        DesktopStyleState style)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        var glass = Appearance(style);
        return string.Equals(appearance.BackgroundColor, glass.BackgroundColor, StringComparison.OrdinalIgnoreCase)
            && string.Equals(appearance.HeaderColor, glass.HeaderColor, StringComparison.OrdinalIgnoreCase)
            && string.Equals(appearance.TextColor, glass.TextColor, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(appearance.BackgroundOpacity - glass.BackgroundOpacity) < 0.005d
            && Math.Abs(appearance.HeaderOpacity - glass.HeaderOpacity) < 0.005d;
    }

    /// <summary>
    /// Preserves a card's customized text color when the shared material changes,
    /// while updating cards that still follow the previous shared ink.
    /// </summary>
    public static string ResolveTransitionTextColor(
        string currentTextColor,
        DesktopStyleState previousStyle,
        DesktopStyleState nextStyle)
    {
        ArgumentNullException.ThrowIfNull(previousStyle);
        ArgumentNullException.ThrowIfNull(nextStyle);
        var previousInk = Appearance(previousStyle).TextColor;
        var nextInk = Appearance(nextStyle).TextColor;
        return string.Equals(currentTextColor, previousInk, StringComparison.OrdinalIgnoreCase)
            ? nextInk
            : currentTextColor;
    }

    public static string ResolveComponentTransitionTextColor(
        DesktopComponentKind kind,
        string currentTextColor,
        DesktopStyleState previousStyle,
        DesktopStyleState nextStyle,
        double clearBarOpacity)
    {
        ArgumentNullException.ThrowIfNull(previousStyle);
        ArgumentNullException.ThrowIfNull(nextStyle);
        var previousInk = ComponentLook(kind, previousStyle, clearBarOpacity).TextColor;
        var nextInk = ComponentLook(kind, nextStyle, clearBarOpacity).TextColor;
        return string.Equals(currentTextColor, previousInk, StringComparison.OrdinalIgnoreCase)
            ? nextInk
            : currentTextColor;
    }

    /// <summary>
    /// Applies the recommended frameless card look: Clear material with soft text shadow,
    /// setting the shared text ink to white while keeping any user-customized text colour.
    /// </summary>
    public static void ApplyRecommendedClearLook(DesktopStyleState style)
    {
        ArgumentNullException.ThrowIfNull(style);
        style.Glass = GlassMode.Clear;
        style.CardShadow = CardShadow.Soft;
        if (style.TextInk != TextInk.Custom)
        {
            style.TextInk = TextInk.White;
        }
    }

    /// <summary>
    /// Applies the shared look from <paramref name="nextStyle"/> to a card's appearance,
    /// keeping a text colour the user customized relative to <paramref name="previousStyle"/>.
    /// </summary>
    public static void ApplySharedCardLook(
        WidgetAppearanceState appearance,
        DesktopStyleState previousStyle,
        DesktopStyleState nextStyle)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(previousStyle);
        ArgumentNullException.ThrowIfNull(nextStyle);
        var glass = Appearance(nextStyle);
        appearance.BackgroundColor = glass.BackgroundColor;
        appearance.BackgroundOpacity = glass.BackgroundOpacity;
        appearance.HeaderColor = glass.HeaderColor;
        appearance.HeaderOpacity = glass.HeaderOpacity;
        appearance.TextColor = ResolveTransitionTextColor(appearance.TextColor, previousStyle, nextStyle);
    }

    /// <summary>
    /// Applies the shared look from <paramref name="nextStyle"/> to a built-in component,
    /// keeping a text colour the user customized relative to <paramref name="previousStyle"/>.
    /// </summary>
    public static void ApplySharedComponentLook(
        DesktopComponentState component,
        DesktopStyleState previousStyle,
        DesktopStyleState nextStyle,
        double clearBarOpacity)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(previousStyle);
        ArgumentNullException.ThrowIfNull(nextStyle);
        var look = ComponentLook(component.Kind, nextStyle, clearBarOpacity);
        component.Appearance.BackgroundColor = look.BackgroundColor;
        component.Appearance.BackgroundOpacity = look.BackgroundOpacity;
        component.Appearance.TextColor = ResolveComponentTransitionTextColor(
            component.Kind,
            component.Appearance.TextColor,
            previousStyle,
            nextStyle,
            clearBarOpacity);
        if (component.Kind == DesktopComponentKind.Todo)
        {
            var glass = Appearance(nextStyle);
            component.Appearance.HeaderColor = glass.HeaderColor;
            component.Appearance.HeaderOpacity = glass.HeaderOpacity;
        }
    }

    public static string ResolveCardItemSurfaceColor(
        WidgetAppearanceState cardAppearance,
        DesktopStyleState globalStyle) =>
        ResolveRenderedCard(cardAppearance, globalStyle).ItemSurfaceColor;

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
        if (!Enum.IsDefined(style.ItemSurfaceTone))
        {
            style.ItemSurfaceTone = SurfaceTone.Light;
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
