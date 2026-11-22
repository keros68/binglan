namespace BingLan.Core.Models;

public readonly record struct LauncherReadability(double SurfaceOpacity, bool DarkInk, double PlateOpacity);

public static class LauncherReadabilityRules
{
    public const string DarkInkColor = "#122C44";
    public const string LightInkColor = "#F2F6FC";
    public const double MinimumContrast = 4.5d;
    public const double MinimumPlateOpacity = 0.78d;

    public static LauncherReadability Resolve(string surfaceRgb, double requestedOpacity)
    {
        var rgb = WidgetAppearanceRules.CoerceColorOrDefault(surfaceRgb, LauncherAppearanceState.DefaultSurfaceColor);
        var surface = ColorContrastRules.ParseHex(rgb);
        var clampedRequest = double.IsFinite(requestedOpacity)
            ? Math.Clamp(requestedOpacity, 0d, 1d)
            : LauncherAppearanceState.DefaultSurfaceOpacity;

        var darkInk = ColorContrastRules.ParseHex(DarkInkColor);
        var lightInk = ColorContrastRules.ParseHex(LightInkColor);

        var startAlpha = Math.Max(clampedRequest, 0.01d);
        var darkAlpha = FindMinimumReadableOpacity(surface, darkInk, startAlpha);
        var lightAlpha = FindMinimumReadableOpacity(surface, lightInk, startAlpha);

        bool useDarkInk;
        double resolvedOpacity;
        if (darkAlpha is not null && lightAlpha is not null)
        {
            if (Math.Abs(darkAlpha.Value - lightAlpha.Value) < 1e-6)
            {
                useDarkInk = ColorContrastRules.RelativeLuminance(surface) > 0.5d;
                resolvedOpacity = darkAlpha.Value;
            }
            else if (darkAlpha.Value < lightAlpha.Value)
            {
                useDarkInk = true;
                resolvedOpacity = darkAlpha.Value;
            }
            else
            {
                useDarkInk = false;
                resolvedOpacity = lightAlpha.Value;
            }
        }
        else if (darkAlpha is not null)
        {
            useDarkInk = true;
            resolvedOpacity = darkAlpha.Value;
        }
        else if (lightAlpha is not null)
        {
            useDarkInk = false;
            resolvedOpacity = lightAlpha.Value;
        }
        else
        {
            resolvedOpacity = 1d;
            useDarkInk = ColorContrastRules.ContrastRatio(darkInk, surface) >= ColorContrastRules.ContrastRatio(lightInk, surface);
        }

        resolvedOpacity = Math.Max(clampedRequest, resolvedOpacity);
        var chosenInk = useDarkInk ? darkInk : lightInk;
        var plateBase = FindMinimumReadableOpacity(surface, chosenInk, 0.01d) ?? 1d;
        var plateOpacity = Math.Max(MinimumPlateOpacity, plateBase);
        return new LauncherReadability(resolvedOpacity, useDarkInk, plateOpacity);
    }

    private static double? FindMinimumReadableOpacity(
        ColorContrastRules.SrgbColor surface,
        ColorContrastRules.SrgbColor ink,
        double startAlpha)
    {
        var black = new ColorContrastRules.SrgbColor(0d, 0d, 0d, 1d);
        var white = new ColorContrastRules.SrgbColor(1d, 1d, 1d, 1d);
        if (IsReadable(surface, ink, startAlpha, black, white))
        {
            return startAlpha;
        }

        var startStep = Math.Clamp((int)Math.Ceiling(startAlpha * 100d - 1e-9), 1, 100);
        for (var step = startStep; step <= 100; step++)
        {
            var alpha = step / 100d;
            if (alpha <= startAlpha)
            {
                continue;
            }

            if (IsReadable(surface, ink, alpha, black, white))
            {
                return alpha;
            }
        }

        return null;
    }

    private static bool IsReadable(
        ColorContrastRules.SrgbColor surface,
        ColorContrastRules.SrgbColor ink,
        double alpha,
        ColorContrastRules.SrgbColor black,
        ColorContrastRules.SrgbColor white)
    {
        var layer = surface with { A = alpha };
        var overBlack = ColorContrastRules.CompositeOver(layer, black);
        var overWhite = ColorContrastRules.CompositeOver(layer, white);
        return ColorContrastRules.ContrastRatio(ink, overBlack) >= MinimumContrast
            && ColorContrastRules.ContrastRatio(ink, overWhite) >= MinimumContrast;
    }
}
