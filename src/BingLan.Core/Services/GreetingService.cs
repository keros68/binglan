namespace BingLan.Core.Services;

public static class GreetingService
{
    private static readonly string[] WeekdayNames =
    [
        "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"
    ];

    public static string FormatTime(DateTime localTime, bool use24HourClock)
    {
        if (use24HourClock)
        {
            return $"{localTime.Hour:D2}:{localTime.Minute:D2}";
        }

        var period = localTime.Hour < 12 ? "上午" : "下午";
        var hour12 = localTime.Hour % 12;
        if (hour12 == 0)
        {
            hour12 = 12;
        }
        return $"{period} {hour12}:{localTime.Minute:D2}";
    }

    public static string FormatDate(DateTime localTime) =>
        $"{localTime.Year}年{localTime.Month}月{localTime.Day}日 {WeekdayNames[(int)localTime.DayOfWeek]}";

    public static string GetTimeOfDayGreeting(DateTime localTime) =>
        localTime.Hour switch
        {
            >= 5 and < 9 => "早上好",
            >= 9 and < 12 => "上午好",
            >= 12 and < 14 => "中午好",
            >= 14 and < 18 => "下午好",
            >= 18 and < 23 => "晚上好",
            _ => "夜深了"
        };

    public static string BuildGreeting(DateTime localTime, string? displayName, bool showTimeOfDayGreeting = true)
    {
        var name = displayName?.Trim() ?? string.Empty;
        if (!showTimeOfDayGreeting)
        {
            return name;
        }

        var greeting = GetTimeOfDayGreeting(localTime);
        return name.Length == 0 ? greeting : $"{greeting}，{name}";
    }
}
