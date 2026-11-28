using BingLan.Core.Models;

namespace BingLan.Core.TopBar;

/// <summary>
/// Resolved colours and glass behaviour for the top bar's popups.
/// </summary>
public sealed record TopBarSearchPalette(
    bool UseDarkInk,
    bool AllowBackdropBlur,
    bool UseTextShadow,
    string GlassTint,
    string FallbackSurface,
    string Border,
    string Foreground,
    string MutedForeground,
    string PlaceholderForeground,
    string Divider,
    string BadgeBackground,
    string BadgeBorder,
    string ActiveRowBackground,
    string HoverRowBackground,
    string SelectionBrush);

/// <summary>
/// Shares the top bar's material across search and system flyouts.
/// </summary>
public static class TopBarSearchLookRules
{
    public const double MinimumFallbackOpacity = 0.94d;

    public const string DarkSurfaceForeground = "#F2F6FC";
    public const string DarkSurfaceMutedForeground = "#C4D5E6";
    public const string DarkSurfacePlaceholderForeground = "#B0C4D8";
    public const string DarkSurfaceBorder = "#4CFFFFFF";
    public const string DarkSurfaceDivider = "#28FFFFFF";
    public const string DarkSurfaceBadgeBackground = "#22FFFFFF";
    public const string DarkSurfaceBadgeBorder = "#30FFFFFF";
    public const string DarkSurfaceHoverRow = "#26FFFFFF";
    public const string DarkSurfaceDefaultActiveRow = "#4872BFEF";
    public const string DarkSurfaceFallbackActiveRow = "#30FFFFFF";
    public const string DarkSurfaceDarkenActiveRow = "#380B1522";
    public const string DarkSurfaceStrongDarkenActiveRow = "#52000000";
    public const string DarkSurfaceDefaultSelection = "#7052B4F5";

    public const string LightSurfaceForeground = "#122C44";
    public const string LightSurfaceMutedForeground = "#35516B";
    public const string LightSurfacePlaceholderForeground = "#3E5973";
    public const string LightSurfaceBorder = "#36122C44";
    public const string LightSurfaceDivider = "#22122C44";
    public const string LightSurfaceBadgeBackground = "#14122C44";
    public const string LightSurfaceBadgeBorder = "#24122C44";
    public const string LightSurfaceHoverRow = "#14122C44";
    public const string LightSurfaceDefaultActiveRow = "#82B9E1FA";
    public const string LightSurfaceFallbackActiveRow = "#22122C44";
    public const string LightSurfaceLightenActiveRow = "#48FFFFFF";
    public const string LightSurfaceStrongLightenActiveRow = "#70FFFFFF";
    public const string LightSurfaceDefaultSelection = "#5C1EA9FF";

    public static TopBarSearchPalette ResolveForTopBar(
        DesktopStyleState? style,
        TopBarState? topBar = null)
    {
        var effectiveStyle = style ?? new DesktopStyleState();
        var effectiveTopBar = topBar ?? new TopBarState();
        var glass = DesktopStyleRules.TopBar(effectiveStyle, effectiveTopBar);
        var allowBackdropBlur = DesktopStyleRules.TopBarFrostedScrim(effectiveTopBar, glass.Surface);
        var opacity = ColorContrastRules.ParseHex(glass.Surface).A;
        var baseRgb = "#" + glass.Surface[3..];
        var accentRgb = WidgetAppearanceRules.CoerceColorOrDefault(
            effectiveStyle.AccentColor,
            DesktopStyleRules.DefaultAccentColor);
        // Popups cover a different part of the desktop than the bar's ink sample.
        // Dense controls need their own readable plate over arbitrary application content.
        var minimumOpacity = allowBackdropBlur
            ? LauncherReadabilityRules.MinimumPlateOpacity
            : MinimumFallbackOpacity;
        var readability = LauncherReadabilityRules.Resolve(baseRgb,
            Math.Max(opacity, minimumOpacity));
        var useDarkInk = readability.DarkInk;
        var foreground = useDarkInk ? LightSurfaceForeground : DarkSurfaceForeground;
        if (ColorContrastRules.ContrastRatio(foreground, baseRgb) < ColorContrastRules.MinimumNormalTextContrast)
        {
            useDarkInk = ColorContrastRules.ContrastRatio("#000000", baseRgb)
                >= ColorContrastRules.ContrastRatio("#FFFFFF", baseRgb);
            foreground = useDarkInk ? "#000000" : "#FFFFFF";
        }

        var alpha = (byte)Math.Clamp(Math.Ceiling(readability.SurfaceOpacity * 255d), 1d, 255d);
        var glassTint = WithAlphaByte(baseRgb, alpha);
        // Without blur, suppress background glyphs as well as maintaining text contrast.
        // Only a successfully applied native backdrop uses the more translucent tint.
        var fallbackAlpha = (byte)Math.Max(alpha, Math.Ceiling(MinimumFallbackOpacity * 255d));
        var fallbackSurface = WithAlphaByte(baseRgb, fallbackAlpha);
        var useTextShadow = !HasContrast(foreground, glassTint);
        var preferredHover = useDarkInk ? LightSurfaceHoverRow : DarkSurfaceHoverRow;
        var hoverRow = HasContrast(foreground, glassTint, preferredHover)
            ? preferredHover
            : useDarkInk ? "#14FFFFFF" : "#26000000";
        var activeRow = ResolveActiveRowBackground(accentRgb, useDarkInk, glassTint, foreground, hoverRow, useTextShadow);
        var badge = useDarkInk ? LightSurfaceBadgeBackground : DarkSurfaceBadgeBackground;
        if (!HasContrastAcrossRows(foreground, glassTint, hoverRow, activeRow, badge))
        {
            badge = useDarkInk ? "#14FFFFFF" : "#22000000";
        }
        var mutedCandidate = useDarkInk ? LightSurfaceMutedForeground : DarkSurfaceMutedForeground;
        var placeholderCandidate = useDarkInk ? LightSurfacePlaceholderForeground : DarkSurfacePlaceholderForeground;
        var muted = HasContrastAcrossRows(mutedCandidate, glassTint, hoverRow, activeRow, badge)
            ? mutedCandidate : foreground;
        var placeholder = HasContrast(placeholderCandidate, glassTint) ? placeholderCandidate : muted;
        var selection = IsDefaultAccent(accentRgb)
            ? (useDarkInk ? LightSurfaceDefaultSelection : DarkSurfaceDefaultSelection)
            : WithAlphaByte(accentRgb, useDarkInk ? (byte)0x52 : (byte)0x66);

        return new TopBarSearchPalette(
            UseDarkInk: useDarkInk,
            AllowBackdropBlur: allowBackdropBlur,
            UseTextShadow: useTextShadow,
            GlassTint: glassTint,
            FallbackSurface: fallbackSurface,
            Border: useDarkInk ? LightSurfaceBorder : DarkSurfaceBorder,
            Foreground: foreground,
            MutedForeground: muted,
            PlaceholderForeground: placeholder,
            Divider: useDarkInk ? LightSurfaceDivider : DarkSurfaceDivider,
            BadgeBackground: badge,
            BadgeBorder: useDarkInk ? LightSurfaceBadgeBorder : DarkSurfaceBadgeBorder,
            ActiveRowBackground: activeRow,
            HoverRowBackground: hoverRow,
            SelectionBrush: selection);
    }

    private static bool HasContrast(string inkHex, string surfaceHex) =>
        HasContrast(ColorContrastRules.ParseHex(inkHex), ColorContrastRules.ParseHex(surfaceHex));

    private static bool HasContrast(string inkHex, string surfaceHex, string overlayHex, string underlayHex = "#00000000") =>
        HasContrast(ColorContrastRules.ParseHex(inkHex), ColorContrastRules.ParseHex(surfaceHex),
            ColorContrastRules.CompositeOver(ColorContrastRules.ParseHex(overlayHex), ColorContrastRules.ParseHex(underlayHex)));

    private static bool HasContrastAcrossRows(string inkHex, string surfaceHex, string hover, string active, string badge)
    {
        // Badges sit inside rows; an active input-method badge uses the active tint again.
        foreach (var row in new[] { "#00000000", hover, active })
        foreach (var overlay in new[] { "#00000000", badge, active })
        {
            if (!HasContrast(inkHex, surfaceHex, overlay, row))
            {
                return false;
            }
        }
        return true;
    }

    private static bool HasContrast(
        ColorContrastRules.SrgbColor ink,
        ColorContrastRules.SrgbColor surface,
        ColorContrastRules.SrgbColor row = default)
    {
        var black = new ColorContrastRules.SrgbColor(0d, 0d, 0d);
        var white = new ColorContrastRules.SrgbColor(1d, 1d, 1d);
        var overBlack = ColorContrastRules.CompositeOver(row, ColorContrastRules.CompositeOver(surface, black));
        var overWhite = ColorContrastRules.CompositeOver(row, ColorContrastRules.CompositeOver(surface, white));
        return ColorContrastRules.ContrastRatio(ink, overBlack) >= ColorContrastRules.MinimumNormalTextContrast
            && ColorContrastRules.ContrastRatio(ink, overWhite) >= ColorContrastRules.MinimumNormalTextContrast;
    }

    private static string ResolveActiveRowBackground(
        string accentRgb,
        bool useDarkInk,
        string surfaceHex,
        string foreground,
        string hover,
        bool useTextShadow)
    {
        var subtle = useDarkInk ? LightSurfaceFallbackActiveRow : DarkSurfaceFallbackActiveRow;
        var defaultAccent = IsDefaultAccent(accentRgb);
        var preferred = defaultAccent
            ? (useDarkInk ? LightSurfaceDefaultActiveRow : DarkSurfaceDefaultActiveRow)
            : WithAlphaByte(accentRgb, useDarkInk ? (byte)0x34 : (byte)0x48);
        if (useTextShadow)
        {
            return defaultAccent ? subtle : preferred;
        }

        string[] candidates = useDarkInk
            ? [preferred, subtle, LightSurfaceLightenActiveRow, LightSurfaceStrongLightenActiveRow]
            : [preferred, subtle, DarkSurfaceDarkenActiveRow, DarkSurfaceStrongDarkenActiveRow];

        var ink = ColorContrastRules.ParseHex(foreground);
        var surface = ColorContrastRules.ParseHex(surfaceHex);

        foreach (var candidate in candidates)
        {
            if (HasContrast(ink, surface, ColorContrastRules.ParseHex(candidate))
                && HasContrast(foreground, surfaceHex, candidate, candidate)
                && HasContrast(foreground, surfaceHex, candidate, hover))
            {
                return candidate;
            }
        }

        return candidates[^1];
    }

    private static bool IsDefaultAccent(string accentRgb) =>
        string.Equals(accentRgb, DesktopStyleRules.DefaultAccentColor, StringComparison.OrdinalIgnoreCase);

    private static string WithAlphaByte(string rgb, byte alpha) =>
        $"#{alpha:X2}{rgb.TrimStart('#')}";
}
