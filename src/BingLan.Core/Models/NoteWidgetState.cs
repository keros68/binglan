using System.Text.Json;
using System.Text.Json.Serialization;

namespace BingLan.Core.Models;

public enum NoteBodyTextAlignment
{
    Left,
    Center,
    Right
}

public sealed class NoteBodyTypography
{
    private string _fontFamily = NoteBodyTypographyRules.DefaultFontFamily;
    private double _fontSize = NoteBodyTypographyRules.DefaultFontSize;
    private string _color = NoteBodyTypographyRules.DefaultColor;

    public string FontFamily
    {
        get => _fontFamily;
        set => _fontFamily = NoteBodyTypographyRules.CoerceFontFamily(value);
    }

    public double FontSize
    {
        get => _fontSize;
        set => _fontSize = NoteBodyTypographyRules.CoerceFontSize(value);
    }

    public string Color
    {
        get => _color;
        set => _color = NoteBodyTypographyRules.CoerceColor(value);
    }

    public bool Bold { get; set; }

    public bool Italic { get; set; }

    public NoteBodyTextAlignment Alignment { get; set; } = NoteBodyTextAlignment.Left;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public static class NoteBodyTypographyRules
{
    public const string DefaultFontFamily = WidgetAppearanceRules.DefaultTitleFontFamily;
    public const double DefaultFontSize = 14d;
    public const double MinimumFontSize = 9d;
    public const double MaximumFontSize = 48d;
    public const string DefaultColor = "#243146";

    public static string CoerceFontFamily(string? value) =>
        WidgetAppearanceRules.CoerceTitleFontFamily(value);

    public static double CoerceFontSize(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return DefaultFontSize;
        }
        return Math.Clamp(value, MinimumFontSize, MaximumFontSize);
    }

    public static string CoerceColor(string? value)
    {
        if (value is null || value.Length != 7 || value[0] != '#')
        {
            return DefaultColor;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!char.IsAsciiHexDigit(value[index]))
            {
                return DefaultColor;
            }
        }

        return value.ToUpperInvariant();
    }
}

public sealed class NoteWidgetState
{
    private double _cornerRadius = WidgetAppearanceRules.DefaultCornerRadius;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "便签";
    public string Content { get; set; } = "";
    public bool IsLocked { get; set; }
    public WidgetAppearanceState Appearance { get; set; } = new();
    public double CornerRadius
    {
        get => _cornerRadius;
        set => _cornerRadius = WidgetAppearanceRules.CoerceCornerRadius(value);
    }
    public WindowPlacement Placement { get; set; } = new()
    {
        Left = 232,
        Top = 148,
        Width = 306,
        Height = 266
    };
    public NoteBodyTypography BodyTypography { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
