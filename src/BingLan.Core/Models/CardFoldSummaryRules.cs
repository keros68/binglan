using BingLan.Core.Services;

namespace BingLan.Core.Models;

/// <summary>
/// Count-only title-bar summaries shown when a file box or to-do card is folded.
/// Summaries intentionally exclude item text and file paths so folded cards reveal
/// no personal content at a glance.
/// </summary>
public static class CardFoldSummaryRules
{
    public static string FormatFileBoxSummary(int itemCount) =>
        $"{Math.Max(0, itemCount)} 项";

    public static string FormatFileBoxSummary(FileBoxState box)
    {
        ArgumentNullException.ThrowIfNull(box);
        return FormatFileBoxSummary(box.Items.Count);
    }

    public static string FormatTodoSummary(int openCount, int completedCount = 0)
    {
        var safeOpen = Math.Max(0, openCount);
        if (safeOpen == 0 && completedCount > 0)
        {
            return "已全部完成";
        }

        return $"{safeOpen} 项未完成";
    }

    private static (int OpenCount, int CompletedCount) CountScopedTodos(
        IEnumerable<TodoItemState> items,
        bool weeklyView,
        DateTime today,
        DateTime? weekStart,
        DateTime? weekEnd,
        bool carryOverIncomplete = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        var todayDate = today.Date;
        var openCount = 0;
        var completedCount = 0;
        var hasWeekRange = weeklyView && weekStart is not null && weekEnd is not null;
        var startDate = weekStart?.Date ?? default;
        var endDate = weekEnd?.Date ?? default;

        foreach (var item in items)
        {
            if (item.Kind != TodoItemKind.Todo || string.IsNullOrWhiteSpace(item.Text))
            {
                continue;
            }

            var inScope = hasWeekRange
                ? item.Date is { } date && date.Date >= startDate && date.Date <= endDate
                : TodoService.BelongsToTodayList(item, todayDate, carryOverIncomplete);
            if (!inScope)
            {
                continue;
            }

            if (item.IsCompleted)
            {
                completedCount++;
            }
            else
            {
                openCount++;
            }
        }

        return (openCount, completedCount);
    }

    public static int CountOpenTodos(
        IEnumerable<TodoItemState> items,
        bool weeklyView,
        DateTime today,
        DateTime? weekStart = null,
        DateTime? weekEnd = null,
        bool carryOverIncomplete = false) =>
        CountScopedTodos(items, weeklyView, today, weekStart, weekEnd, carryOverIncomplete).OpenCount;

    public static int CountOpenTodos(
        TodoWidgetState widget,
        DateTime today,
        DateTime? weekStart = null,
        DateTime? weekEnd = null)
    {
        ArgumentNullException.ThrowIfNull(widget);
        return CountOpenTodos(widget.Items, widget.WeeklyView, today, weekStart, weekEnd, widget.CarryOverIncomplete);
    }

    public static string FormatTodoSummary(
        IEnumerable<TodoItemState> items,
        bool weeklyView,
        DateTime today,
        DateTime? weekStart = null,
        DateTime? weekEnd = null,
        bool carryOverIncomplete = false)
    {
        var counts = CountScopedTodos(items, weeklyView, today, weekStart, weekEnd, carryOverIncomplete);
        return FormatTodoSummary(counts.OpenCount, counts.CompletedCount);
    }

    public static string FormatTodoSummary(
        TodoWidgetState widget,
        DateTime today,
        DateTime? weekStart = null,
        DateTime? weekEnd = null)
    {
        ArgumentNullException.ThrowIfNull(widget);
        return FormatTodoSummary(widget.Items, widget.WeeklyView, today, weekStart, weekEnd, widget.CarryOverIncomplete);
    }
}
