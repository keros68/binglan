using System.Globalization;

namespace BingLan.Core.Services;

/// <summary>
/// Chinese lunar labels for the weekly view's day cells, derived from the built-in
/// lunisolar calendar so no extra dependency ships with the app.
/// </summary>
public static class LunarDayText
{
    private static readonly ChineseLunisolarCalendar Calendar = new();
    private static readonly string[] MonthNames =
        ["正月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "冬月", "腊月"];
    private static readonly string[] DayNames =
    [
        "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
        "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
        "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十"
    ];

    /// <summary>
    /// The lunar label for one day: the month name (with a leap prefix when it is a
    /// leap month) on the first day so months can be told apart, the day name
    /// otherwise. Empty when the date is outside what the calendar can express.
    /// </summary>
    public static string For(DateTime date)
    {
        try
        {
            var month = Calendar.GetMonth(date);
            var day = Calendar.GetDayOfMonth(date);
            var leapMonth = Calendar.GetLeapMonth(Calendar.GetYear(date));
            var monthIndex = leapMonth > 0 && month >= leapMonth ? month - 1 : month;
            var isLeapMonth = leapMonth > 0 && month == leapMonth;
            return day == 1
                ? (isLeapMonth ? "闰" : "") + MonthNames[monthIndex - 1]
                : DayNames[day - 1];
        }
        catch (ArgumentOutOfRangeException)
        {
            // Dates the calendar cannot express (before 1901 or after 2100) show no
            // lunar label rather than throwing while paging far from today.
            return "";
        }
    }
}
