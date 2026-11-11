using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using SystemColors = System.Windows.SystemColors;

namespace BingLan.App;

/// <summary>
/// The "glacier mica" colours of the settings centre, in a light and a dark set. Over the
/// Mica backdrop the surfaces are translucent layers; without it each translucent colour
/// is blended onto the page colour, so Windows 10 and "transparency effects off" get the
/// same colours as solid, readable fills. High contrast replaces everything with opaque
/// system colours.
/// </summary>
internal static class SettingsPalette
{
    private const string LightPage = "#F3F6FA";
    private const string DarkPage = "#10151E";

    private enum HighContrast
    {
        Window,
        WindowText,
        Control,
        ControlText,
        Highlight,
        HighlightText
    }

    // Keys the settings window shares with the dialogs and cards that use the app-level
    // ice palette; they are only overridden inside the settings window.
    private static readonly (string Key, string Light, string Dark, HighContrast System)[] SharedTokens =
    [
        ("IcePageBrush", LightPage, DarkPage, HighContrast.Window),
        ("IceInkBrush", "#0F1B2D", "#EDF2F8", HighContrast.WindowText),
        ("IceMutedInkBrush", "#5A667A", "#97A3B6", HighContrast.WindowText),
        ("IceActionBrush", "#1985D3", "#55BDF6", HighContrast.Highlight),
        ("IceActionTextBrush", "#FFFFFF", "#06131F", HighContrast.HighlightText),
        ("SettingsNavigationBrush", "#00FFFFFF", "#00000000", HighContrast.Control),
        ("SettingsSurfaceBrush", "#CCFFFFFF", "#0EFFFFFF", HighContrast.Window),
        ("SettingsSurfaceHoverBrush", "#B8ECF2F8", "#09FFFFFF", HighContrast.Control),
        ("SettingsSurfaceBorderBrush", "#14102440", "#12FFFFFF", HighContrast.WindowText),
        ("SettingsControlBrush", "#CCFFFFFF", "#0EFFFFFF", HighContrast.Control),
        ("SettingsControlHoverBrush", "#B8ECF2F8", "#17FFFFFF", HighContrast.Control),
        ("SettingsControlBorderBrush", "#26102440", "#21FFFFFF", HighContrast.ControlText),
        ("SettingsToggleOffBrush", "#64748B", "#97A3B6", HighContrast.WindowText),
        ("SettingsTrackBrush", "#26102440", "#21FFFFFF", HighContrast.ControlText),
        ("SettingsSegmentTrackBrush", "#B8ECF2F8", "#09FFFFFF", HighContrast.Control),
        ("SettingsNavSelectedBrush", "#DBFFFFFF", "#17FFFFFF", HighContrast.Highlight),
        ("SettingsNavSelectedTextBrush", "#0F1B2D", "#EDF2F8", HighContrast.HighlightText),
        ("SettingsChoiceSelectedBrush", "#CCFFFFFF", "#0EFFFFFF", HighContrast.Window),
        ("SettingsBadgeBrush", "#1F1985D3", "#2955BDF6", HighContrast.Control),
        ("SettingsBadgeTextBrush", "#5A667A", "#97A3B6", HighContrast.ControlText),
        ("SettingsCoordinateTextBrush", "#5A667A", "#97A3B6", HighContrast.WindowText),
        ("SettingsPreviewBrush", "#B8ECF2F8", "#09FFFFFF", HighContrast.Window)
    ];

    // Keys only the redesigned settings styles use; the app-level palette carries their
    // light values so dialogs that share the settings theme resolve them too.
    private static readonly (string Key, string Light, string Dark, HighContrast System)[] OwnTokens =
    [
        ("SettingsFaintInkBrush", "#98A3B3", "#5E6A7C", HighContrast.WindowText),
        ("SettingsAccentSoftBrush", "#1F1985D3", "#2955BDF6", HighContrast.Highlight),
        ("SettingsSelectedTextBrush", "#0F1B2D", "#EDF2F8", HighContrast.HighlightText),
        ("SettingsKnobBrush", "#FFFFFF", "#FFFFFF", HighContrast.HighlightText),
        ("SettingsKnobOffBrush", "#FFFFFF", "#E6EDF5", HighContrast.WindowText),
        ("SettingsToggleTrackOffBrush", "#30102440", "#26FFFFFF", HighContrast.Control),
        ("SettingsToggleBorderBrush", "#00FFFFFF", "#00FFFFFF", HighContrast.ControlText),
        ("SettingsWarnBrush", "#29FFB74D", "#1FFFB74D", HighContrast.Window),
        ("SettingsWarnTextBrush", "#8A5300", "#F0C063", HighContrast.WindowText),
        ("SettingsOkBrush", "#1F9D57", "#4CCB86", HighContrast.Highlight),
        ("SettingsHighlightBrush", "#59FFD666", "#2EFFD666", HighContrast.Highlight),
        ("SettingsDangerTextBrush", "#C2413B", "#FF8A80", HighContrast.WindowText),
        ("SettingsMapBarBrush", "#C7121A26", "#C7121A26", HighContrast.Control),
        ("SettingsMapTaskbarBrush", "#99121A26", "#99121A26", HighContrast.Control),
        ("SettingsMapDockBrush", "#47FFFFFF", "#33FFFFFF", HighContrast.Control),
        ("SettingsMapTextBrush", "#FFFFFF", "#FFFFFF", HighContrast.ControlText),
        ("SettingsMapDeskTextBrush", "#8CFFFFFF", "#8CFFFFFF", HighContrast.WindowText)
    ];

    // Separate windows (pop-ups) cannot show the backdrop, so these stay opaque.
    private static readonly (string Key, string Light, string Dark, HighContrast System)[] OpaqueTokens =
    [
        ("SettingsPopupBrush", "#FFFFFF", "#1B212B", HighContrast.Window),
        ("SettingsPopupBorderBrush", "#D3D9E1", "#333B47", HighContrast.WindowText)
    ];

    /// <summary>
    /// Writes the palette into <paramref name="target"/>. <paramref name="translucent"/>
    /// keeps the layered alpha for the Mica backdrop; otherwise colours are blended onto
    /// the page. <paramref name="includeShared"/> also overrides the keys shared with the
    /// app-level ice palette, which only the settings window itself should do.
    /// </summary>
    internal static void Apply(
        ResourceDictionary target,
        bool highContrast,
        bool dark,
        bool translucent,
        bool includeShared)
    {
        var page = Parse(dark ? DarkPage : LightPage);
        var tokens = includeShared ? SharedTokens.Concat(OwnTokens) : OwnTokens.AsEnumerable();
        foreach (var (key, light, darkValue, system) in tokens)
        {
            target[key] = highContrast
                ? SystemBrush(system)
                : Solid(translucent ? Parse(dark ? darkValue : light) : Blend(Parse(dark ? darkValue : light), page));
        }
        foreach (var (key, light, darkValue, system) in OpaqueTokens)
        {
            target[key] = highContrast ? SystemBrush(system) : Solid(Parse(dark ? darkValue : light));
        }

        // A faint cool layer over Mica gives the window its glacier tint; the solid page
        // colour already carries it when there is no backdrop.
        target["SettingsWindowTintBrush"] = highContrast
            ? SystemColors.WindowBrush
            : Solid(translucent ? Parse(dark ? "#990E131C" : "#80F4F7FB") : Colors.Transparent);
        target["SettingsAccentFillBrush"] = highContrast
            ? SystemColors.HighlightBrush
            : Gradient(dark ? "#8AD8FF" : "#4FC3F7", dark ? "#55BDF6" : "#1985D3");
        target["SettingsMapWallpaperBrush"] = highContrast
            ? SystemColors.WindowBrush
            : Gradient(dark ? "#2D628F" : "#8EC5EA", dark ? "#0A1729" : "#27486E");
        // A soft offset band stands in for a drop shadow: the process renders in software,
        // where blur effects are re-rasterised on every scroll and hover.
        target["SettingsLiftShadowBrush"] = highContrast
            ? SystemColors.WindowBrush
            : Solid(Parse(dark ? "#59000000" : "#14102440"));
    }

    private static Brush SystemBrush(HighContrast system) => system switch
    {
        HighContrast.WindowText => SystemColors.WindowTextBrush,
        HighContrast.Control => SystemColors.ControlBrush,
        HighContrast.ControlText => SystemColors.ControlTextBrush,
        HighContrast.Highlight => SystemColors.HighlightBrush,
        HighContrast.HighlightText => SystemColors.HighlightTextBrush,
        _ => SystemColors.WindowBrush
    };

    private static Color Blend(Color color, Color page)
    {
        var alpha = color.A / 255d;
        return Color.FromRgb(
            (byte)Math.Round(color.R * alpha + page.R * (1 - alpha)),
            (byte)Math.Round(color.G * alpha + page.G * (1 - alpha)),
            (byte)Math.Round(color.B * alpha + page.B * (1 - alpha)));
    }

    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value)!;

    private static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Gradient(string from, string to)
    {
        var brush = new LinearGradientBrush(Parse(from), Parse(to), new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
        brush.Freeze();
        return brush;
    }
}
