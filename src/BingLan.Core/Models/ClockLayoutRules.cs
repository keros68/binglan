using System.Text.Json;
using System.Text.Json.Serialization;
using BingLan.Core.Services;

namespace BingLan.Core.Models;

/// <summary>
/// Built-in typography and spatial arrangements offered on the clock card.
/// </summary>
[JsonConverter(typeof(ClockLayoutPresetJsonConverter))]
public enum ClockLayoutPreset
{
    Classic = 0,
    MinimalDigits = 1,
    DatePoster = 2,
    VerticalDigits = 3
}

/// <summary>
/// Spatial relationship between time digits and date metadata for a clock preset.
/// </summary>
public enum ClockDateArrangement
{
    TimeAboveDate = 0,
    MinimalCentered = 1,
    DateAboveTime = 2,
    VerticalSideBySide = 3
}

/// <summary>
/// Resolved clock layout, fonts, OpenType weights, DIP sizes and ink hierarchy.
/// A user-chosen custom font family, font scale and text color always take priority
/// over preset typography defaults.
/// </summary>
public sealed record ClockTypographySpec(
    ClockLayoutPreset Preset,
    ClockDateArrangement Arrangement,
    string? EffectiveTimeFontFamily,
    string? EffectiveDateFontFamily,
    int TimeFontWeightOpenType,
    int DateFontWeightOpenType,
    double TimeFontSize,
    double DateFontSize,
    double PeriodFontSize,
    double? TimeLineHeight,
    bool CenterHorizontally,
    bool DateUsesPrimaryInk,
    string EffectiveTextColor);

/// <summary>
/// Formatted text segments rendered by the clock card for a given local timestamp,
/// layout preset, 12/24-hour preference and date visibility flag.
/// </summary>
public sealed record ClockFormattedParts(string TimeText, string? PeriodText, string DateText);

public sealed class ClockLayoutPresetJsonConverter : JsonConverter<ClockLayoutPreset>
{
    public override ClockLayoutPreset Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var numeric)
                    ? ClockLayoutRules.CoercePreset((ClockLayoutPreset)numeric)
                    : ClockLayoutPreset.Classic;
            case JsonTokenType.String:
                var raw = reader.GetString();
                if (int.TryParse(raw, out var parsedNumber))
                {
                    return ClockLayoutRules.CoercePreset((ClockLayoutPreset)parsedNumber);
                }
                return Enum.TryParse<ClockLayoutPreset>(raw, ignoreCase: true, out var parsedEnum)
                    ? ClockLayoutRules.CoercePreset(parsedEnum)
                    : ClockLayoutPreset.Classic;
            case JsonTokenType.Null:
                return ClockLayoutPreset.Classic;
            default:
                reader.Skip();
                return ClockLayoutPreset.Classic;
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        ClockLayoutPreset value,
        JsonSerializerOptions options) =>
        writer.WriteNumberValue((int)ClockLayoutRules.CoercePreset(value));
}

public static class ClockLayoutRules
{
    private static readonly string[] WeekdayNames =
    [
        "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"
    ];

    public static IReadOnlyList<ClockLayoutPreset> AllPresets { get; } =
    [
        ClockLayoutPreset.Classic,
        ClockLayoutPreset.MinimalDigits,
        ClockLayoutPreset.DatePoster,
        ClockLayoutPreset.VerticalDigits
    ];

    public static IReadOnlyList<string> BundledFontNames { get; } =
    [
        "得意黑",
        "Jost 极细",
        "Jost 细",
        "Jost",
        "Jost 中粗",
        "Quicksand 细",
        "Quicksand",
        "Abril Fatface 衬线"
    ];

    public static ClockLayoutPreset CoercePreset(ClockLayoutPreset value) =>
        Enum.IsDefined(value) ? value : ClockLayoutPreset.Classic;

    public static string GetDisplayName(ClockLayoutPreset preset) => CoercePreset(preset) switch
    {
        ClockLayoutPreset.MinimalDigits => "极简大数字",
        ClockLayoutPreset.DatePoster => "日期海报",
        ClockLayoutPreset.VerticalDigits => "纵向数字",
        _ => "经典"
    };

    public static ClockTypographySpec ResolveTypography(
        ClockLayoutPreset preset,
        string? userFontFamily,
        double fontScale,
        string textColor,
        bool centerClockPreset = false,
        bool lowChrome = true)
    {
        var safePreset = CoercePreset(preset);
        var scale = DesktopExperienceRules.CoerceFontScale(fontScale);
        var customFont = string.IsNullOrWhiteSpace(userFontFamily)
            ? null
            : WidgetAppearanceRules.CoerceTitleFontFamily(userFontFamily);
        var effectiveColor = WidgetAppearanceRules.CoerceTextColor(textColor);

        return safePreset switch
        {
            ClockLayoutPreset.MinimalDigits => BuildMinimalDigits(
                customFont, scale, effectiveColor, centerClockPreset),
            ClockLayoutPreset.DatePoster => BuildDatePoster(
                customFont, scale, effectiveColor, centerClockPreset),
            ClockLayoutPreset.VerticalDigits => BuildVerticalDigits(
                customFont, scale, effectiveColor, centerClockPreset),
            _ => BuildClassic(
                customFont, scale, effectiveColor, centerClockPreset, lowChrome)
        };
    }

    public static ClockFormattedParts FormatClockParts(
        DateTime localTime,
        ClockLayoutPreset preset,
        bool use24HourClock,
        bool showDate)
    {
        var safePreset = CoercePreset(preset);
        if (safePreset == ClockLayoutPreset.Classic)
        {
            return new ClockFormattedParts(
                GreetingService.FormatTime(localTime, use24HourClock),
                null,
                showDate ? GreetingService.FormatDate(localTime) : string.Empty);
        }

        var hour = use24HourClock
            ? localTime.Hour
            : localTime.Hour % 12 is var remainder && remainder == 0 ? 12 : remainder;
        var period = use24HourClock
            ? null
            : localTime.Hour < 12 ? "上午" : "下午";
        var weekday = WeekdayNames[(int)localTime.DayOfWeek];

        return safePreset switch
        {
            ClockLayoutPreset.MinimalDigits => new ClockFormattedParts(
                $"{hour:D2}:{localTime.Minute:D2}",
                period,
                showDate ? $"{localTime.Year}.{localTime.Month:D2}.{localTime.Day:D2}  {weekday}" : string.Empty),
            ClockLayoutPreset.DatePoster => new ClockFormattedParts(
                $"{hour:D2}:{localTime.Minute:D2}",
                period,
                showDate ? $"{localTime.Month}月{localTime.Day}日 · {weekday}" : string.Empty),
            ClockLayoutPreset.VerticalDigits => new ClockFormattedParts(
                $"{hour:D2}\n{localTime.Minute:D2}",
                period,
                showDate ? $"{localTime.Month}月{localTime.Day}日\n{weekday}" : string.Empty),
            _ => new ClockFormattedParts(
                GreetingService.FormatTime(localTime, use24HourClock),
                null,
                showDate ? GreetingService.FormatDate(localTime) : string.Empty)
        };
    }

    private static ClockTypographySpec BuildClassic(
        string? customFont,
        double scale,
        string effectiveColor,
        bool centerClockPreset,
        bool lowChrome)
    {
        var baseTimeSize = centerClockPreset ? 96d : 36d * (lowChrome ? 1.3d : 1d);
        return new ClockTypographySpec(
            ClockLayoutPreset.Classic,
            ClockDateArrangement.TimeAboveDate,
            customFont,
            customFont,
            centerClockPreset ? 300 : 700,
            400,
            baseTimeSize * scale,
            13d * scale,
            13d * scale,
            null,
            centerClockPreset,
            DateUsesPrimaryInk: true,
            effectiveColor);
    }

    private static ClockTypographySpec BuildMinimalDigits(
        string? customFont,
        double scale,
        string effectiveColor,
        bool centerClockPreset)
    {
        var timeSize = (centerClockPreset ? 104d : 68d) * scale;
        var dateSize = (centerClockPreset ? 14d : 12.5d) * scale;
        return new ClockTypographySpec(
            ClockLayoutPreset.MinimalDigits,
            ClockDateArrangement.MinimalCentered,
            customFont ?? "Jost 极细",
            customFont ?? "Quicksand 细",
            200,
            300,
            timeSize,
            dateSize,
            dateSize,
            Math.Round(timeSize * 1.04d, 2),
            CenterHorizontally: true,
            DateUsesPrimaryInk: false,
            effectiveColor);
    }

    private static ClockTypographySpec BuildDatePoster(
        string? customFont,
        double scale,
        string effectiveColor,
        bool centerClockPreset)
    {
        var timeSize = (centerClockPreset ? 76d : 48d) * scale;
        var dateSize = (centerClockPreset ? 30d : 22d) * scale;
        var periodSize = (centerClockPreset ? 16d : 13d) * scale;
        return new ClockTypographySpec(
            ClockLayoutPreset.DatePoster,
            ClockDateArrangement.DateAboveTime,
            customFont ?? "Abril Fatface 衬线",
            customFont ?? "得意黑",
            400,
            400,
            timeSize,
            dateSize,
            periodSize,
            Math.Round(timeSize * 1.05d, 2),
            CenterHorizontally: centerClockPreset,
            DateUsesPrimaryInk: true,
            effectiveColor);
    }

    private static ClockTypographySpec BuildVerticalDigits(
        string? customFont,
        double scale,
        string effectiveColor,
        bool centerClockPreset)
    {
        var timeSize = (centerClockPreset ? 68d : 54d) * scale;
        var dateSize = (centerClockPreset ? 15d : 13d) * scale;
        var periodSize = (centerClockPreset ? 14d : 12.5d) * scale;
        return new ClockTypographySpec(
            ClockLayoutPreset.VerticalDigits,
            ClockDateArrangement.VerticalSideBySide,
            customFont ?? "Quicksand",
            customFont ?? "Quicksand",
            700,
            400,
            timeSize,
            dateSize,
            periodSize,
            Math.Round(timeSize * 0.94d, 2),
            CenterHorizontally: centerClockPreset,
            DateUsesPrimaryInk: false,
            effectiveColor);
    }
}
