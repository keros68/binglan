using System.Globalization;

namespace BingLan.Core.Models;

/// <summary>
/// WCAG 2.1 contrast, alpha compositing, and readable ink/scrim helpers for desktop cards.
/// </summary>
public static class ColorContrastRules
{
    public const string BrightWallpaper = "#E8EEF5";
    public const string DarkWallpaper = "#141C28";
    public const double SecondaryTextAlpha = 0.76d;
    public const double DisabledTextAlpha = 0.45d;

    /// <summary>
    /// WCAG relative luminance midpoint separating light card text (such as white) from
    /// dark ink so light-text-on-light-glass cards are switched consistently as a whole card.
    /// </summary>
    public const double LightTextLuminanceThreshold = 0.5d;

    /// <summary>
    /// Below 20% opacity more than 80% of the wallpaper shows through, so the card behaves like
    /// near-clear glass (soft text shadow + capped dark row tint). At or above 20% the user asked
    /// for a visible card backing, so light text is paired with the dark-glass card body as a whole.
    /// </summary>
    public const double NearClearBackgroundOpacityThreshold = 0.2d;

    /// <summary>
    /// Upper bound on dark item-row tint opacity for near-clear (&lt; 20% opacity) light-glass
    /// cards. Kept at 0.16 (~40/255 ≈ 15.7% alpha, subtle controls at ~7% alpha) so interactive
    /// rows and footer buttons retain subtle separation without looking like solid gray patches
    /// over bright photo wallpapers when the outer card frame has nearly vanished.
    /// </summary>
    public const double NearClearMaxItemSurfaceStrength = 0.16d;

    public const double MinimumNormalTextContrast = 4.5d;
    public const double MinimumLargeTextContrast = 3.0d;

    public readonly record struct SrgbColor(double R, double G, double B, double A = 1d);

    public static SrgbColor ParseHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            throw new ArgumentException("Color hex value cannot be empty.", nameof(hex));
        }

        var span = hex.AsSpan().Trim();
        if (span.StartsWith('#'))
        {
            span = span[1..];
        }

        if (span.Length == 6)
        {
            return new SrgbColor(
                ParseChannel(span[..2]),
                ParseChannel(span.Slice(2, 2)),
                ParseChannel(span.Slice(4, 2)),
                1d);
        }

        if (span.Length == 8)
        {
            return new SrgbColor(
                ParseChannel(span.Slice(2, 2)),
                ParseChannel(span.Slice(4, 2)),
                ParseChannel(span.Slice(6, 2)),
                ParseChannel(span[..2]));
        }

        throw new FormatException($"Unsupported hex color format: '{hex}'.");
    }

    public static SrgbColor WithAlpha(string hex, double alpha)
    {
        var parsed = ParseHex(hex);
        var clamped = Math.Clamp(alpha, 0d, 1d);
        return parsed with { A = clamped };
    }

    public static SrgbColor CompositeOver(SrgbColor foreground, SrgbColor background)
    {
        var fa = Math.Clamp(foreground.A, 0d, 1d);
        var ba = Math.Clamp(background.A, 0d, 1d);
        var outA = fa + ba * (1d - fa);
        if (outA <= 0d)
        {
            return new SrgbColor(0d, 0d, 0d, 0d);
        }

        var r = (foreground.R * fa + background.R * ba * (1d - fa)) / outA;
        var g = (foreground.G * fa + background.G * ba * (1d - fa)) / outA;
        var b = (foreground.B * fa + background.B * ba * (1d - fa)) / outA;
        return new SrgbColor(r, g, b, outA);
    }

    public static SrgbColor CompositeLayers(string backdropHex, params SrgbColor[] layers)
    {
        var current = ParseHex(backdropHex);
        foreach (var layer in layers)
        {
            current = CompositeOver(layer, current);
        }
        return current;
    }

    public static double RelativeLuminance(string hex) =>
        RelativeLuminance(ParseHex(hex));

    public static double RelativeLuminance(SrgbColor color) =>
        0.2126d * Linearize(color.R)
        + 0.7152d * Linearize(color.G)
        + 0.0722d * Linearize(color.B);

    public static double ContrastRatio(string foregroundHex, string backgroundHex) =>
        ContrastRatio(ParseHex(foregroundHex), ParseHex(backgroundHex));

    public static double ContrastRatio(SrgbColor foreground, SrgbColor background)
    {
        var resolvedForeground = foreground.A < 1d ? CompositeOver(foreground, background) : foreground;
        var l1 = RelativeLuminance(resolvedForeground);
        var l2 = RelativeLuminance(background);
        var lighter = Math.Max(l1, l2);
        var darker = Math.Min(l1, l2);
        return (lighter + 0.05d) / (darker + 0.05d);
    }

    private static double ParseChannel(ReadOnlySpan<char> pair) =>
        byte.Parse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;

    private static double Linearize(double channel) =>
        channel <= 0.04045d
            ? channel / 12.92d
            : Math.Pow((channel + 0.055d) / 1.055d, 2.4d);
}
