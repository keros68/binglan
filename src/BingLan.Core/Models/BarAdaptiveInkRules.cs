using BingLan.Core.Dock;

namespace BingLan.Core.Models;

/// <summary>Resolved ink and control colours for the full-width top bar and its flyouts.</summary>
public sealed record TopBarAdaptivePalette(
    bool UseDarkInk,
    string Foreground,
    string MutedForeground,
    string HoverBackground,
    string PressedBackground,
    string SearchBackground,
    string SearchHoverBackground,
    string SearchBorder,
    string BadgeBackground,
    string DividerColor,
    string BarBorderColor,
    string AttentionForeground,
    TopBarFlyoutPalette Flyout);

/// <summary>Resolved readable colours for top bar popups (to-do, calendar, IME, tooltips).</summary>
public sealed record TopBarFlyoutPalette(
    bool UseDarkInk,
    string Background,
    string Border,
    string Foreground,
    string Divider,
    string BadgeBackground,
    string ActiveBadgeBackground,
    string ActiveRowBackground,
    string HoverRowBackground);

/// <summary>Resolved adaptive colours for the dock's command buttons, running dots, border and dividers.</summary>
public sealed record DockAdaptiveColors(
    bool UseDarkInk,
    string RunningDot,
    string Border,
    string Divider);

/// <summary>One-pixel-thick strip outside the dock where the bare wallpaper can be sampled safely.</summary>
public readonly record struct BackdropSampleStrip(int Left, int Top, int Width, int Height);

/// <summary>
/// Pure rules that keep the top bar and dock readable over both opaque light/dark surfaces and
/// low-opacity/transparent surfaces sitting over bright or dynamic wallpapers.
/// </summary>
public static class BarAdaptiveInkRules
{
    public const string DarkInkColor = "#122C44";
    public const string LightInkColor = "#FFFFFF";
    public const string DarkRunningDotColor = "#C21F3A55";
    public const string LightRunningDotColor = "#E6FFFFFF";
    public const string DarkAttentionColor = "#C45100";
    public const string LightAttentionColor = "#FFA500";
    public const string LightSurfaceDarkBorderColor = "#2E122C44";
    public const string LightSurfaceDarkDividerColor = "#38122C44";
    public const string DarkSurfaceLightDividerColor = "#66FFFFFF";

    /// <summary>
    /// Surfaces at or above this alpha dominate their own perceived brightness and do not need
    /// live wallpaper sampling.
    /// </summary>
    public const double OpaqueSurfaceAlphaThreshold = 0.55d;

    /// <summary>Hysteresis band preventing rapid ink flapping on dynamic wallpapers near mid-grey.</summary>
    public const double BrightBackdropEnterLuminance = 0.54d;
    public const double BrightBackdropExitLuminance = 0.42d;
    private const double InitialBrightBackdropLuminance = 0.48d;

    /// <summary>Minimum opacity for top bar flyouts/tooltips so transparent bars still open readable popups.</summary>
    public const double MinimumDarkFlyoutOpacity = 0.90d;
    public const double MinimumLightFlyoutOpacity = 0.92d;

    public const string ClearLightFlyoutBackground = "#EEEDF4FC";
    public const string ClearDarkFlyoutBackground = "#EB1E1F24";
    public const string LightFlyoutBorder = "#38122C44";
    public const string DarkFlyoutBorder = "#33FFFFFF";

    public static bool NeedsBackdropSampling(string surfaceWithAlpha, bool hasDarkAcrylicScrim)
    {
        if (hasDarkAcrylicScrim)
        {
            return false;
        }

        var surface = ParseSurfaceOrDefault(surfaceWithAlpha);
        return surface.A < OpaqueSurfaceAlphaThreshold;
    }

    public static bool ResolveUseDarkInk(
        string surfaceWithAlpha,
        bool hasDarkAcrylicScrim,
        ColorContrastRules.SrgbColor? sampledBackdrop,
        bool? previousUseDarkInk)
    {
        if (hasDarkAcrylicScrim)
        {
            return false;
        }

        var surface = ParseSurfaceOrDefault(surfaceWithAlpha);
        if (surface.A >= OpaqueSurfaceAlphaThreshold)
        {
            return ColorContrastRules.RelativeLuminance(surface with { A = 1d }) > 0.5d;
        }

        if (sampledBackdrop is not { } backdrop)
        {
            return false;
        }

        var effective = ColorContrastRules.CompositeOver(surface, backdrop with { A = 1d });
        var luminance = ColorContrastRules.RelativeLuminance(effective);
        return previousUseDarkInk switch
        {
            true => luminance > BrightBackdropExitLuminance,
            false => luminance >= BrightBackdropEnterLuminance,
            null => luminance >= InitialBrightBackdropLuminance
        };
    }

    /// <summary>
    /// Removes a known translucent bar surface layer from an observed screen pixel so the
    /// underlying wallpaper colour is recovered without self-sampling bias.
    /// </summary>
    public static ColorContrastRules.SrgbColor UncompositeOverSurface(
        ColorContrastRules.SrgbColor observed,
        string surfaceWithAlpha)
    {
        var surface = ParseSurfaceOrDefault(surfaceWithAlpha);
        var a = Math.Clamp(surface.A, 0d, 0.95d);
        if (a <= 0.005d)
        {
            return observed with { A = 1d };
        }

        var inv = 1d - a;
        return new ColorContrastRules.SrgbColor(
            Math.Clamp((observed.R - a * surface.R) / inv, 0d, 1d),
            Math.Clamp((observed.G - a * surface.G) / inv, 0d, 1d),
            Math.Clamp((observed.B - a * surface.B) / inv, 0d, 1d),
            1d);
    }

    /// <summary>
    /// Picks the median-luminance sample so an occasional cursor or bright highlight on one
    /// sample point does not skew the whole bar's contrast decision.
    /// </summary>
    public static ColorContrastRules.SrgbColor? PickRepresentativeBackdropColor(
        IReadOnlyList<ColorContrastRules.SrgbColor> samples)
    {
        if (samples is null || samples.Count == 0)
        {
            return null;
        }

        if (samples.Count == 1)
        {
            return samples[0] with { A = 1d };
        }

        var ordered = samples
            .Select(color => (Color: color with { A = 1d }, Luminance: ColorContrastRules.RelativeLuminance(color with { A = 1d })))
            .OrderBy(item => item.Luminance)
            .ToArray();

        return ordered[ordered.Length / 2].Color;
    }

    public static TopBarAdaptivePalette ResolveTopBarPalette(
        DockGlass baseGlass,
        bool hasDarkAcrylicScrim,
        ColorContrastRules.SrgbColor? sampledBackdrop,
        bool? previousUseDarkInk)
    {
        ArgumentNullException.ThrowIfNull(baseGlass);

        var useDarkInk = ResolveUseDarkInk(
            baseGlass.Surface,
            hasDarkAcrylicScrim,
            sampledBackdrop,
            previousUseDarkInk);

        var barBorder = ResolveTopBarBorder(baseGlass.Border, useDarkInk);
        var flyout = ResolveTopBarFlyout(baseGlass, hasDarkAcrylicScrim, useDarkInk);

        if (useDarkInk)
        {
            return new TopBarAdaptivePalette(
                UseDarkInk: true,
                Foreground: DarkInkColor,
                MutedForeground: "#BF122C44",
                HoverBackground: "#18122C44",
                PressedBackground: "#26122C44",
                SearchBackground: "#14122C44",
                SearchHoverBackground: "#22122C44",
                SearchBorder: "#28122C44",
                BadgeBackground: "#1A122C44",
                DividerColor: "#24122C44",
                BarBorderColor: barBorder,
                AttentionForeground: DarkAttentionColor,
                Flyout: flyout);
        }

        return new TopBarAdaptivePalette(
            UseDarkInk: false,
            Foreground: LightInkColor,
            MutedForeground: "#B8FFFFFF",
            HoverBackground: "#22FFFFFF",
            PressedBackground: "#46FFFFFF",
            SearchBackground: "#26FFFFFF",
            SearchHoverBackground: "#38FFFFFF",
            SearchBorder: "#22FFFFFF",
            BadgeBackground: "#33FFFFFF",
            DividerColor: "#26FFFFFF",
            BarBorderColor: barBorder,
            AttentionForeground: LightAttentionColor,
            Flyout: flyout);
    }

    public static TopBarFlyoutPalette ResolveTopBarFlyout(
        DockGlass baseGlass,
        bool hasDarkAcrylicScrim,
        bool barUseDarkInk)
    {
        ArgumentNullException.ThrowIfNull(baseGlass);
        var surface = ParseSurfaceOrDefault(baseGlass.Surface);
        string background;
        string border;
        bool flyoutDarkInk;

        if (hasDarkAcrylicScrim || (surface.A >= OpaqueSurfaceAlphaThreshold && !barUseDarkInk))
        {
            flyoutDarkInk = false;
            var alpha = Math.Max(surface.A, MinimumDarkFlyoutOpacity);
            background = FormatHex(surface with { A = alpha });
            border = DarkFlyoutBorder;
        }
        else if (surface.A >= OpaqueSurfaceAlphaThreshold && barUseDarkInk)
        {
            flyoutDarkInk = true;
            var alpha = Math.Max(surface.A, MinimumLightFlyoutOpacity);
            background = FormatHex(surface with { A = alpha });
            border = LightFlyoutBorder;
        }
        else if (barUseDarkInk)
        {
            flyoutDarkInk = true;
            background = ClearLightFlyoutBackground;
            border = LightFlyoutBorder;
        }
        else
        {
            flyoutDarkInk = false;
            background = ClearDarkFlyoutBackground;
            border = DarkFlyoutBorder;
        }

        if (flyoutDarkInk)
        {
            return new TopBarFlyoutPalette(
                UseDarkInk: true,
                Background: background,
                Border: border,
                Foreground: DarkInkColor,
                Divider: "#22122C44",
                BadgeBackground: "#1A122C44",
                ActiveBadgeBackground: "#2E122C44",
                ActiveRowBackground: "#22122C44",
                HoverRowBackground: "#16122C44");
        }

        return new TopBarFlyoutPalette(
            UseDarkInk: false,
            Background: background,
            Border: border,
            Foreground: LightInkColor,
            Divider: "#26FFFFFF",
            BadgeBackground: "#26FFFFFF",
            ActiveBadgeBackground: "#4AFFFFFF",
            ActiveRowBackground: "#30FFFFFF",
            HoverRowBackground: "#24FFFFFF");
    }

    public static DockAdaptiveColors ResolveDockAdaptiveColors(
        DockGlass baseGlass,
        ColorContrastRules.SrgbColor? sampledBackdrop,
        bool? previousUseDarkInk)
    {
        ArgumentNullException.ThrowIfNull(baseGlass);

        var useDarkInk = ResolveUseDarkInk(
            baseGlass.Surface,
            hasDarkAcrylicScrim: false,
            sampledBackdrop,
            previousUseDarkInk);

        var surface = ParseSurfaceOrDefault(baseGlass.Surface);
        var isLowOpacity = surface.A < OpaqueSurfaceAlphaThreshold;
        var runningDot = useDarkInk ? DarkRunningDotColor : LightRunningDotColor;
        var baseBorder = ParseSurfaceOrDefault(baseGlass.Border);
        var hasVisibleBorder = baseBorder.A >= 0.02d;

        string border;
        if (!hasVisibleBorder)
        {
            // Clear / no-backing mode deliberately hides the outer pill border.
            border = baseGlass.Border;
        }
        else if (isLowOpacity && useDarkInk && ColorContrastRules.RelativeLuminance(baseBorder with { A = 1d }) > 0.5d)
        {
            // A low-opacity light dock's white border vanishes over a bright wallpaper; switch to
            // a crisp subtle dark hairline while preserving the signature white highlight on opaque glass.
            border = LightSurfaceDarkBorderColor;
        }
        else
        {
            border = baseGlass.Border;
        }

        var divider = useDarkInk
            ? LightSurfaceDarkDividerColor
            : (hasVisibleBorder ? baseGlass.Border : DarkSurfaceLightDividerColor);

        return new DockAdaptiveColors(useDarkInk, runningDot, border, divider);
    }

    /// <summary>
    /// Computes a 1-pixel sampling strip completely outside the dock window (still inside the
    /// dock's screen-edge band) so sampling never reads dock icons, glass tint, or borders.
    /// </summary>
    public static BackdropSampleStrip? ComputeDockSampleStrip(
        PixelRect dockBounds,
        PixelRect monitorBounds,
        DockEdge edge)
    {
        if (!dockBounds.HasArea || !monitorBounds.HasArea)
        {
            return null;
        }

        const int Margin = 3;
        var normalized = DockEdgeRules.Normalize(edge);
        if (normalized == DockEdge.Bottom)
        {
            var belowY = dockBounds.Bottom + Margin;
            var left = Math.Max(monitorBounds.Left, dockBounds.Left);
            var right = Math.Min(monitorBounds.Right, dockBounds.Right);
            if (belowY < monitorBounds.Bottom && right - left >= 4)
            {
                return new BackdropSampleStrip(left, belowY, right - left, 1);
            }

            // Flush with screen bottom: sample vertically beside the dock inside the bottom edge band.
            var top = Math.Max(monitorBounds.Top, dockBounds.Top + 4);
            var bottom = Math.Min(monitorBounds.Bottom, dockBounds.Bottom - 4);
            if (bottom - top >= 4)
            {
                if (dockBounds.Left - Margin >= monitorBounds.Left)
                {
                    return new BackdropSampleStrip(dockBounds.Left - Margin, top, 1, bottom - top);
                }
                if (dockBounds.Right + Margin < monitorBounds.Right)
                {
                    return new BackdropSampleStrip(dockBounds.Right + Margin, top, 1, bottom - top);
                }
            }

            var aboveY = dockBounds.Top - Margin;
            if (aboveY >= monitorBounds.Top && right - left >= 4)
            {
                return new BackdropSampleStrip(left, aboveY, right - left, 1);
            }
            return null;
        }

        // Left or Right docked vertical dock.
        var vTop = Math.Max(monitorBounds.Top, dockBounds.Top);
        var vBottom = Math.Min(monitorBounds.Bottom, dockBounds.Bottom);
        if (normalized == DockEdge.Left)
        {
            var outerX = dockBounds.Left - Margin;
            if (outerX >= monitorBounds.Left && vBottom - vTop >= 4)
            {
                return new BackdropSampleStrip(outerX, vTop, 1, vBottom - vTop);
            }
        }
        else
        {
            var outerX = dockBounds.Right + Margin;
            if (outerX < monitorBounds.Right && vBottom - vTop >= 4)
            {
                return new BackdropSampleStrip(outerX, vTop, 1, vBottom - vTop);
            }
        }

        var hLeft = Math.Max(monitorBounds.Left, dockBounds.Left + 4);
        var hRight = Math.Min(monitorBounds.Right, dockBounds.Right - 4);
        if (hRight - hLeft >= 4)
        {
            if (dockBounds.Top - Margin >= monitorBounds.Top)
            {
                return new BackdropSampleStrip(hLeft, dockBounds.Top - Margin, hRight - hLeft, 1);
            }
            if (dockBounds.Bottom + Margin < monitorBounds.Bottom)
            {
                return new BackdropSampleStrip(hLeft, dockBounds.Bottom + Margin, hRight - hLeft, 1);
            }
        }

        return null;
    }

    private static string ResolveTopBarBorder(string baseBorderHex, bool useDarkInk)
    {
        if (!useDarkInk)
        {
            return baseBorderHex;
        }

        var border = ParseSurfaceOrDefault(baseBorderHex);
        if (border.A < 0.02d || ColorContrastRules.RelativeLuminance(border with { A = 1d }) > 0.5d)
        {
            return "#22122C44";
        }

        return baseBorderHex;
    }

    private static ColorContrastRules.SrgbColor ParseSurfaceOrDefault(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return new ColorContrastRules.SrgbColor(0.12d, 0.12d, 0.14d, 0.82d);
        }

        try
        {
            return ColorContrastRules.ParseHex(hex);
        }
        catch
        {
            return new ColorContrastRules.SrgbColor(0.12d, 0.12d, 0.14d, 0.82d);
        }
    }

    private static string FormatHex(ColorContrastRules.SrgbColor color)
    {
        var a = (byte)Math.Clamp((int)Math.Round(color.A * 255d), 0, 255);
        var r = (byte)Math.Clamp((int)Math.Round(color.R * 255d), 0, 255);
        var g = (byte)Math.Clamp((int)Math.Round(color.G * 255d), 0, 255);
        var b = (byte)Math.Clamp((int)Math.Round(color.B * 255d), 0, 255);
        return $"#{a:X2}{r:X2}{g:X2}{b:X2}";
    }
}
