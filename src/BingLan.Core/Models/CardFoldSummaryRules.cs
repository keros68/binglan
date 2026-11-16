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

    public static string FormatTodoSummary(int openCount) =>
        $"{Math.Max(0, openCount)} 项未完成";

    public static int CountOpenTodos(
        IEnumerable<TodoItemState> items,
        bool weeklyView,
        DateTime today,
        DateTime? weekStart = null,
        DateTime? weekEnd = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        var todayDate = today.Date;

        if (weeklyView && weekStart is { } start && weekEnd is { } end)
        {
            var startDate = start.Date;
            var endDate = end.Date;
            return items.Count(item =>
                item.Kind == TodoItemKind.Todo
                && !item.IsCompleted
                && !string.IsNullOrWhiteSpace(item.Text)
                && item.Date is { } date
                && date.Date >= startDate
                && date.Date <= endDate);
        }

        return items.Count(item =>
            item.Kind == TodoItemKind.Todo
            && !item.IsCompleted
            && !string.IsNullOrWhiteSpace(item.Text)
            && TodoService.BelongsToTodayList(item, todayDate));
    }

    public static int CountOpenTodos(
        TodoWidgetState widget,
        DateTime today,
        DateTime? weekStart = null,
        DateTime? weekEnd = null)
    {
        ArgumentNullException.ThrowIfNull(widget);
        return CountOpenTodos(widget.Items, widget.WeeklyView, today, weekStart, weekEnd);
    }

    public static string FormatTodoSummary(
        IEnumerable<TodoItemState> items,
        bool weeklyView,
        DateTime today,
        DateTime? weekStart = null,
        DateTime? weekEnd = null) =>
        FormatTodoSummary(CountOpenTodos(items, weeklyView, today, weekStart, weekEnd));

    public static string FormatTodoSummary(
        TodoWidgetState widget,
        DateTime today,
        DateTime? weekStart = null,
        DateTime? weekEnd = null) =>
        FormatTodoSummary(CountOpenTodos(widget, today, weekStart, weekEnd));

    /// <summary>
    /// Unbacked and near-clear cards raise the folded summary from secondary ink to primary
    /// ink so the count remains legible against bright wallpaper.
    /// </summary>
    public static bool UsesPrimaryInk(double backgroundOpacity) =>
        backgroundOpacity < ColorContrastRules.NearClearBackgroundOpacityThreshold;
}
