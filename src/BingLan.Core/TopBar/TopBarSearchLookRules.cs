using BingLan.Core.Models;

namespace BingLan.Core.TopBar;

/// <summary>
/// Resolved colours and glass behaviour for the top bar's inline search popup,
/// aligned with the top bar's own material, adaptive wallpaper ink, and readability rules.
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
/// Pure colour and contrast rules for the top bar's inline search popup. Keeps the popup
/// visually unified with the top bar and its system flyouts across dark, light, custom,
/// palette, and transparent wallpaper-adaptive configurations.
/// </summary>
public static class TopBarSearchLookRules
{
    public const double TransparentTopBarOpacityThreshold = 0.12d;
    public const string DefaultLightSurfaceRgb = "#ECF6FF";
    public const string DefaultDarkSurfaceRgb = "#162230";
    public const string ClearGlassDarkTintRgb = "#1E2D3D";

    public const string ClearGlassWhiteForeground = "#FFFFFF";
    public const string ClearGlassMutedForeground = "#E4F1FC";
    public const string ClearGlassPlaceholderForeground = "#D8EAFA";
    public const string ClearGlassBorder = "#5CFFFFFF";
    public const string ClearGlassDivider = "#30FFFFFF";
    public const string ClearGlassBadgeBackground = "#26FFFFFF";
    public const string ClearGlassBadgeBorder = "#48FFFFFF";
    public const string ClearGlassHoverRow = "#2CFFFFFF";
    public const string ClearGlassDefaultActiveRow = "#583BA7F0";

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

    public static TopBarSearchPalette Resolve(
        DesktopStyleState? style,
        LauncherAppearanceState? launcherAppearance = null,
        TopBarState? topBar = null)
    {
        var effectiveStyle = style ?? new DesktopStyleState();
        var effectiveLauncher = launcherAppearance ?? new LauncherAppearanceState();
        var effectiveTopBar = topBar ?? new TopBarState();
        var accentRgb = WidgetAppearanceRules.CoerceColorOrDefault(
            effectiveStyle.AccentColor,
            DesktopStyleRules.DefaultAccentColor);
        var allowBackdropBlur = effectiveTopBar.FrostedGlass && !effectiveLauncher.UseClearGlass;

        bool darkSurface;
        bool sheerClearGlass = false;
        string baseRgb;
        double density;
        double? guaranteedFallbackOpacity = null;
        string? customForeground = null;

        // Follow the user's global style (material, palette, and TextInk) or explicit
        // launcher appearance override so inline search matches cards & the top bar pill.
        if (!effectiveLauncher.FollowCardLook)
        {
            var customRgb = WidgetAppearanceRules.CoerceColorOrDefault(
                effectiveLauncher.SurfaceColor,
                LauncherAppearanceState.DefaultSurfaceColor);
            var readability = LauncherReadabilityRules.Resolve(customRgb, effectiveLauncher.SurfaceOpacity);
            baseRgb = customRgb;
            darkSurface = !readability.DarkInk;
            density = readability.SurfaceOpacity;
            guaranteedFallbackOpacity = readability.PlateOpacity;
        }
        else
        {
            var wantsLightInk = ResolveWantsLightInk(effectiveStyle, out customForeground);
            if (wantsLightInk)
            {
                darkSurface = true;
                sheerClearGlass = effectiveStyle.Glass == GlassMode.Clear;
                baseRgb = sheerClearGlass ? ClearGlassDarkTintRgb : DefaultDarkSurfaceRgb;
                density = sheerClearGlass ? 0.26d : effectiveStyle.GlassDensity;
            }
            else
            {
                darkSurface = false;
                var palette = DesktopStyleRules.GetPalette(effectiveStyle.Palette);
                baseRgb = palette.Key == DesktopStyleRules.DefaultPalette
                    ? DefaultLightSurfaceRgb
                    : WidgetAppearanceRules.CoerceColorOrDefault(palette.Body, DefaultLightSurfaceRgb);
                density = effectiveStyle.Glass == GlassMode.Clear
                    ? 0.48d
                    : effectiveStyle.GlassDensity;
            }
        }

        if (darkSurface)
        {
            var blurAlpha = sheerClearGlass
                ? (byte)0x42
                : guaranteedFallbackOpacity is not null
                    ? (byte)Math.Clamp((int)Math.Ceiling(Math.Max(density, 0.72d) * 255d - 1e-9), 0xB8, 0xFF)
                    : (byte)Math.Clamp((int)Math.Round((0.64d + (density - 0.25d) * 0.24d) * 255d), 0x9C, 0xD6);
            var fallbackAlpha = guaranteedFallbackOpacity is { } minPlate
                ? (byte)Math.Clamp((int)Math.Ceiling(Math.Max(minPlate, 0.88d) * 255d - 1e-9), 0xE0, 0xFF)
                : sheerClearGlass
                    ? (byte)0xDC
                    : (byte)Math.Clamp((int)Math.Round((0.88d + (density - 0.25d) * 0.10d) * 255d), 0xE0, 0xF2);

            var glassTint = WithAlphaByte(baseRgb, blurAlpha);
            var fallbackSurface = WithAlphaByte(baseRgb, fallbackAlpha);
            var foreground = customForeground
                ?? (sheerClearGlass ? ClearGlassWhiteForeground : DarkSurfaceForeground);
            var muted = EnsureReadableSecondaryInk(
                sheerClearGlass ? ClearGlassMutedForeground : DarkSurfaceMutedForeground,
                foreground,
                fallbackSurface);
            var placeholder = EnsureReadableSecondaryInk(
                sheerClearGlass ? ClearGlassPlaceholderForeground : DarkSurfacePlaceholderForeground,
                muted,
                fallbackSurface);
            var activeRow = ResolveActiveRowBackground(
                accentRgb,
                darkSurface: true,
                sheerClearGlass,
                fallbackSurface,
                foreground);
            var selection = IsDefaultAccent(accentRgb)
                ? DarkSurfaceDefaultSelection
                : WithAlphaByte(accentRgb, 0x66);

            return new TopBarSearchPalette(
                UseDarkInk: false,
                AllowBackdropBlur: allowBackdropBlur,
                UseTextShadow: sheerClearGlass,
                GlassTint: glassTint,
                FallbackSurface: fallbackSurface,
                Border: sheerClearGlass ? ClearGlassBorder : DarkSurfaceBorder,
                Foreground: foreground,
                MutedForeground: muted,
                PlaceholderForeground: placeholder,
                Divider: sheerClearGlass ? ClearGlassDivider : DarkSurfaceDivider,
                BadgeBackground: sheerClearGlass ? ClearGlassBadgeBackground : DarkSurfaceBadgeBackground,
                BadgeBorder: sheerClearGlass ? ClearGlassBadgeBorder : DarkSurfaceBadgeBorder,
                ActiveRowBackground: activeRow,
                HoverRowBackground: sheerClearGlass ? ClearGlassHoverRow : DarkSurfaceHoverRow,
                SelectionBrush: selection);
        }
        else
        {
            var blurAlpha = guaranteedFallbackOpacity is not null
                ? (byte)Math.Clamp((int)Math.Ceiling(Math.Max(density, 0.74d) * 255d - 1e-9), 0xBD, 0xFF)
                : (byte)Math.Clamp((int)Math.Round((0.64d + (density - 0.25d) * 0.22d) * 255d), 0x9E, 0xD4);
            var fallbackAlpha = guaranteedFallbackOpacity is { } minPlate
                ? (byte)Math.Clamp((int)Math.Ceiling(Math.Max(minPlate, 0.90d) * 255d - 1e-9), 0xE6, 0xFF)
                : (byte)Math.Clamp((int)Math.Round((0.90d + (density - 0.25d) * 0.08d) * 255d), 0xE6, 0xF4);

            var glassTint = WithAlphaByte(baseRgb, blurAlpha);
            var fallbackSurface = WithAlphaByte(baseRgb, fallbackAlpha);
            var foreground = customForeground ?? LightSurfaceForeground;
            var muted = EnsureReadableSecondaryInk(LightSurfaceMutedForeground, foreground, fallbackSurface);
            var placeholder = EnsureReadableSecondaryInk(LightSurfacePlaceholderForeground, muted, fallbackSurface);
            var activeRow = ResolveActiveRowBackground(
                accentRgb,
                darkSurface: false,
                sheerClearGlass: false,
                fallbackSurface,
                foreground);
            var selection = IsDefaultAccent(accentRgb)
                ? LightSurfaceDefaultSelection
                : WithAlphaByte(accentRgb, 0x52);

            return new TopBarSearchPalette(
                UseDarkInk: true,
                AllowBackdropBlur: allowBackdropBlur,
                UseTextShadow: false,
                GlassTint: glassTint,
                FallbackSurface: fallbackSurface,
                Border: LightSurfaceBorder,
                Foreground: foreground,
                MutedForeground: muted,
                PlaceholderForeground: placeholder,
                Divider: LightSurfaceDivider,
                BadgeBackground: LightSurfaceBadgeBackground,
                BadgeBorder: LightSurfaceBadgeBorder,
                ActiveRowBackground: activeRow,
                HoverRowBackground: LightSurfaceHoverRow,
                SelectionBrush: selection);
        }
    }

    private static bool ResolveWantsLightInk(DesktopStyleState style, out string? customForeground)
    {
        customForeground = null;
        switch (style.TextInk)
        {
            case TextInk.White:
                return true;
            case TextInk.Dark:
                return false;
            case TextInk.Custom:
                var custom = WidgetAppearanceRules.CoerceTextColor(style.CustomTextColor);
                customForeground = custom;
                return ColorContrastRules.RelativeLuminance(ColorContrastRules.ParseHex(custom)) > 0.45d;
            default:
                // TextInk.Follow matches DesktopStyleRules: white on Dark and Clear glass, dark on Light glass.
                return style.Glass is GlassMode.Dark or GlassMode.Clear;
        }
    }

    private static string EnsureReadableSecondaryInk(
        string candidateHex,
        string fallbackHex,
        string fallbackSurface)
    {
        var candidate = ColorContrastRules.ParseHex(candidateHex);
        var surfaceLayer = ColorContrastRules.ParseHex(fallbackSurface);
        var overDark = ColorContrastRules.CompositeLayers(ColorContrastRules.DarkWallpaper, surfaceLayer);
        var overBright = ColorContrastRules.CompositeLayers(ColorContrastRules.BrightWallpaper, surfaceLayer);
        if (ColorContrastRules.ContrastRatio(candidate, overDark) >= ColorContrastRules.MinimumNormalTextContrast
            && ColorContrastRules.ContrastRatio(candidate, overBright) >= ColorContrastRules.MinimumNormalTextContrast)
        {
            return candidateHex;
        }

        return fallbackHex;
    }

    private static string ResolveActiveRowBackground(
        string accentRgb,
        bool darkSurface,
        bool sheerClearGlass,
        string fallbackSurface,
        string foreground)
    {
        var preferred = IsDefaultAccent(accentRgb)
            ? (darkSurface
                ? (sheerClearGlass ? ClearGlassDefaultActiveRow : DarkSurfaceDefaultActiveRow)
                : LightSurfaceDefaultActiveRow)
            : WithAlphaByte(accentRgb, darkSurface ? (byte)0x48 : (byte)0x34);

        string[] candidates = darkSurface
            ? [preferred, DarkSurfaceFallbackActiveRow, DarkSurfaceDarkenActiveRow, DarkSurfaceStrongDarkenActiveRow]
            : [preferred, LightSurfaceFallbackActiveRow, LightSurfaceLightenActiveRow, LightSurfaceStrongLightenActiveRow];

        var ink = ColorContrastRules.ParseHex(foreground);
        var surfaceLayer = ColorContrastRules.ParseHex(fallbackSurface);

        foreach (var candidate in candidates)
        {
            var rowLayer = ColorContrastRules.ParseHex(candidate);
            var overDark = ColorContrastRules.CompositeLayers(ColorContrastRules.DarkWallpaper, surfaceLayer, rowLayer);
            var overBright = ColorContrastRules.CompositeLayers(ColorContrastRules.BrightWallpaper, surfaceLayer, rowLayer);
            if (ColorContrastRules.ContrastRatio(ink, overDark) >= ColorContrastRules.MinimumNormalTextContrast
                && ColorContrastRules.ContrastRatio(ink, overBright) >= ColorContrastRules.MinimumNormalTextContrast)
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
