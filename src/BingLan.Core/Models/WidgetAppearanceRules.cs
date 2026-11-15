namespace BingLan.Core.Models;

public static class WidgetAppearanceRules
{
    public const double DefaultCornerRadius = 12d;
    public const double MinimumCornerRadius = 0d;
    public const double MaximumCornerRadius = 32d;
    public const string DefaultBackgroundColor = "#FFFFFF";
    public const double DefaultBackgroundOpacity = 0.66d;
    // A fully transparent pixel lets clicks fall through a layered window, so the body
    // never goes fully clear: at 1 % it is invisible but the card can still be dragged.
    public const double MinimumBackgroundOpacity = 0.01d;
    public const string DefaultHeaderColor = "#5B9BD5";
    public const double DefaultHeaderOpacity = 0.97d;
    public const string DefaultTextColor = "#1E2B3C";
    public const string DefaultTitleFontFamily = "Microsoft YaHei UI";
    public const bool DefaultTitleFontBold = true;
    public const bool DefaultTitleFontItalic = false;
    public const int MaximumTitleFontFamilyLength = 128;

    public static double CoerceCornerRadius(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return DefaultCornerRadius;
        }
        return Math.Clamp(value, MinimumCornerRadius, MaximumCornerRadius);
    }

    /// <summary>A #RRGGBB colour, or the fallback when the value is not one.</summary>
    public static string CoerceColorOrDefault(string? value, string fallback) =>
        CoerceColor(value, fallback);

    public static string CoerceBackgroundColor(string? value) =>
        CoerceColor(value, DefaultBackgroundColor);

    public static string CoerceHeaderColor(string? value) =>
        CoerceColor(value, DefaultHeaderColor);

    public static string CoerceTextColor(string? value) =>
        CoerceColor(value, DefaultTextColor);

    public static double CoerceBackgroundOpacity(double value) =>
        CoerceOpacity(value, DefaultBackgroundOpacity, MinimumBackgroundOpacity);

    public static double CoerceHeaderOpacity(double value) =>
        CoerceOpacity(value, DefaultHeaderOpacity, 0d);

    public static string CoerceTitleFontFamily(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultTitleFontFamily;
        }

        var normalized = value.Trim();
        if (normalized.Length > MaximumTitleFontFamilyLength ||
            normalized.Any(char.IsControl))
        {
            return DefaultTitleFontFamily;
        }

        return normalized;
    }

    private static string CoerceColor(string? value, string defaultValue)
    {
        if (value is null || value.Length != 7 || value[0] != '#')
        {
            return defaultValue;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!char.IsAsciiHexDigit(value[index]))
            {
                return defaultValue;
            }
        }

        return value.ToUpperInvariant();
    }

    private static double CoerceOpacity(
        double value,
        double defaultValue,
        double minimum)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, minimum, 1d);
    }
}
