using BingLan.Core.Models;

namespace BingLan.Core.Services;

public static class TodoService
{
    public static TodoWidgetState CreateDefaultWidget()
    {
        var widget = new TodoWidgetState();
        for (var index = 0; index < 3; index++)
        {
            Add(widget);
        }
        return widget;
    }

    public static TodoItemState Add(TodoWidgetState widget, string text = "")
    {
        var item = new TodoItemState
        {
            Text = text,
            Order = widget.Items.Count == 0 ? 0 : widget.Items.Max(x => x.Order) + 1,
            CreatedDate = DateTimeOffset.Now
        };
        widget.Items.Add(item);
        Normalize(widget);
        return item;
    }

    /// <summary>Adds an entry placed on one calendar day of the weekly view.</summary>
    public static TodoItemState Add(TodoWidgetState widget, string text, DateTime? date)
    {
        var item = Add(widget, text);
        item.Date = date;
        return item;
    }

    public static bool Remove(TodoWidgetState widget, Guid itemId)
    {
        var removed = widget.Items.RemoveAll(x => x.Id == itemId) > 0;
        if (removed)
        {
            Normalize(widget);
        }
        return removed;
    }

    public static int RemoveCompleted(TodoWidgetState widget)
    {
        var removed = widget.Items.RemoveAll(x => x.IsCompleted);
        if (removed > 0)
        {
            Normalize(widget);
        }
        return removed;
    }

    /// <summary>
    /// Clears completed entries inside one visible scope only, so tidying the today
    /// list never wipes the dated history shown in the weekly view.
    /// </summary>
    public static int RemoveCompleted(TodoWidgetState widget, Func<TodoItemState, bool> scope)
    {
        var removed = widget.Items.RemoveAll(x => x.IsCompleted && scope(x));
        if (removed > 0)
        {
            Normalize(widget);
        }
        return removed;
    }

    /// <summary>
    /// Toggles completion by id and records when the entry was completed.
    /// Returns false when the entry is missing or already in that state.
    /// </summary>
    public static bool SetCompleted(TodoWidgetState widget, Guid itemId, bool completed)
    {
        var item = widget.Items.FirstOrDefault(x => x.Id == itemId);
        if (item is null || item.IsCompleted == completed)
        {
            return false;
        }
        item.IsCompleted = completed;
        StampCompletion(item);
        return true;
    }

    /// <summary>
    /// Stamps when an entry was completed from its current state, so every toggle
    /// path (card checkbox, weekly cell, top bar row) records the same field.
    /// Idempotent: re-running it on container regeneration never rewrites an
    /// existing stamp, so render passes cannot distort completion times.
    /// </summary>
    public static void StampCompletion(TodoItemState item)
    {
        if (item.IsCompleted && item.CompletedDate is null)
        {
            item.CompletedDate = DateTimeOffset.Now;
        }
        else if (!item.IsCompleted && item.CompletedDate is not null)
        {
            item.CompletedDate = null;
        }
    }

    public static bool Move(TodoWidgetState widget, Guid itemId, int offset)
    {
        var ordered = widget.Items.OrderBy(x => x.Order).ToList();
        var index = ordered.FindIndex(x => x.Id == itemId);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= ordered.Count)
        {
            return false;
        }

        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Order = i;
        }
        widget.Items = ordered;
        return true;
    }

    /// <summary>
    /// Moves an entry past its neighbours inside one visible scope, leaving entries
    /// outside the scope (other days, other views) untouched.
    /// </summary>
    public static bool MoveWithinScope(
        TodoWidgetState widget, Guid itemId, int offset, Func<TodoItemState, bool> scope)
    {
        var ordered = widget.Items.Where(scope).OrderBy(x => x.Order).ToList();
        var index = ordered.FindIndex(x => x.Id == itemId);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= ordered.Count)
        {
            return false;
        }

        (ordered[index].Order, ordered[target].Order) = (ordered[target].Order, ordered[index].Order);
        Normalize(widget);
        return true;
    }

    /// <summary>Moves an entry past its neighbours on the same calendar day.</summary>
    public static bool MoveWithinDate(TodoWidgetState widget, Guid itemId, int offset)
    {
        var item = widget.Items.FirstOrDefault(x => x.Id == itemId);
        return item?.Date is { } date && MoveWithinScope(widget, itemId, offset, x => x.Date == date);
    }

    /// <summary>The entries placed on one calendar day, in their manual order.</summary>
    public static List<TodoItemState> ItemsForDate(TodoWidgetState widget, DateTime date) =>
        widget.Items.Where(x => x.Date == date).OrderBy(x => x.Order).ToList();

    /// <summary>
    /// Which entries the today list shows: undated ones plus entries placed on today,
    /// so a task written into today's cell appears in both views.
    /// </summary>
    public static bool BelongsToTodayList(TodoItemState item, DateTime today) =>
        item.Date is null || item.Date.Value == today;

    public static void Normalize(TodoWidgetState widget)
    {
        widget.Items = widget.Items
            .OrderBy(x => x.Order)
            .Select((item, index) =>
            {
                item.Order = index;
                return item;
            })
            .ToList();
    }
}
