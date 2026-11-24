namespace BingLan.Core.Services;

/// <summary>
/// Pure layout rules shared by the to-do weekly grid and the top-bar month calendar
/// so Monday-first and Sunday-first week layouts stay consistent and testable.
/// </summary>
public static class WeeklyCalendarRules
{
    private static readonly string[] MondayFirstHeaders = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"];
    private static readonly string[] SundayFirstHeaders = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
    private static readonly string[] MondayFirstShortHeaders = ["一", "二", "三", "四", "五", "六", "日"];
    private static readonly string[] SundayFirstShortHeaders = ["日", "一", "二", "三", "四", "五", "六"];

    public static IReadOnlyList<string> WeekdayHeaders(bool weekStartsOnSunday, bool shortForm = false) =>
        (weekStartsOnSunday, shortForm) switch
        {
            (true, true) => SundayFirstShortHeaders,
            (true, false) => SundayFirstHeaders,
            (false, true) => MondayFirstShortHeaders,
            (false, false) => MondayFirstHeaders
        };

    public static bool IsWeekendColumn(int columnIndex, bool weekStartsOnSunday) =>
        weekStartsOnSunday ? columnIndex is 0 or 6 : columnIndex is 5 or 6;

    public static DateTime WeekStartOf(DateTime date, bool weekStartsOnSunday)
    {
        var day = date.Date;
        var offset = weekStartsOnSunday
            ? (int)day.DayOfWeek
            : ((int)day.DayOfWeek + 6) % 7;
        return day.AddDays(-offset);
    }

    public static int LeadingDaysForMonth(DateTime firstOfMonth, bool weekStartsOnSunday) =>
        weekStartsOnSunday
            ? (int)firstOfMonth.DayOfWeek
            : ((int)firstOfMonth.DayOfWeek + 6) % 7;
}
