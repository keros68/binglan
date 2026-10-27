using BingLan.Core.Models;

namespace BingLan.Core.Services;

public static class TodoService
{
    /// <summary>
    /// A new card starts empty: the user adds entries themselves (the + button, a
    /// cell click, or the day editor) instead of receiving placeholder blanks.
    /// </summary>
    public static TodoWidgetState CreateDefaultWidget() => new();

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
    /// Removes entries whose text is empty or whitespace. A blank entry is only a
    /// place to type, so it survives solely while it is being typed into
    /// (<paramref name="keep"/>); once editing ends it is not a task.
    /// </summary>
    public static List<TodoItemState> RemoveBlank(
        TodoWidgetState widget,
        Func<TodoItemState, bool>? keep = null)
    {
        var blanks = widget.Items
            .Where(x => string.IsNullOrWhiteSpace(x.Text) && keep?.Invoke(x) != true)
            .ToList();
        if (blanks.Count > 0)
        {
            widget.Items.RemoveAll(blanks.Contains);
            Normalize(widget);
        }
        return blanks;
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

    /// <summary>
    /// Reorders entries within <paramref name="scope"/> after <paramref name="toggledItemId"/>
    /// changes completion state so incomplete entries stay above completed entries:
    /// a newly completed entry sinks to the bottom of the scope (tail of completed entries),
    /// while a newly uncompleted entry rises back to the tail of incomplete entries.
    /// Entries outside <paramref name="scope"/> keep their relative positions.
    /// </summary>
    public static bool SinkCompletedWithinScope(
        TodoWidgetState widget, Guid toggledItemId, Func<TodoItemState, bool> scope)
    {
        var ordered = widget.Items.OrderBy(x => x.Order).ToList();
        var scopedIndices = new List<int>();
        var scopedItems = new List<TodoItemState>();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (scope(ordered[i]))
            {
                scopedIndices.Add(i);
                scopedItems.Add(ordered[i]);
            }
        }

        var toggled = scopedItems.FirstOrDefault(x => x.Id == toggledItemId);
        if (toggled is null || scopedItems.Count <= 1)
        {
            return false;
        }

        var incomplete = scopedItems
            .Where(x => !x.IsCompleted && x.Id != toggledItemId)
            .ToList();
        var completed = scopedItems
            .Where(x => x.IsCompleted && x.Id != toggledItemId)
            .ToList();

        if (toggled.IsCompleted)
        {
            completed.Add(toggled);
        }
        else
        {
            incomplete.Add(toggled);
        }

        var reorderedScope = new List<TodoItemState>(scopedItems.Count);
        reorderedScope.AddRange(incomplete);
        reorderedScope.AddRange(completed);

        var changed = false;
        for (var i = 0; i < scopedItems.Count; i++)
        {
            if (!ReferenceEquals(scopedItems[i], reorderedScope[i]))
            {
                changed = true;
                break;
            }
        }

        if (!changed)
        {
            return false;
        }

        for (var i = 0; i < scopedIndices.Count; i++)
        {
            ordered[scopedIndices[i]] = reorderedScope[i];
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Order = i;
        }

        widget.Items = ordered;
        return true;
    }

    /// <summary>
    /// Automatically sinks a completed entry to the bottom (or raises an uncompleted entry
    /// back above completed entries) within its active scope: today's list scope for undated
    /// or today-dated entries, or the entry's calendar day for other dates.
    /// </summary>
    public static bool SinkCompleted(TodoWidgetState widget, Guid toggledItemId, DateTime today)
    {
        var item = widget.Items.FirstOrDefault(x => x.Id == toggledItemId);
        if (item is null)
        {
            return false;
        }

        if (BelongsToTodayList(item, today))
        {
            return SinkCompletedWithinScope(
                widget, toggledItemId, x => BelongsToTodayList(x, today));
        }

        return item.Date is { } date &&
               SinkCompletedWithinScope(widget, toggledItemId, x => x.Date == date);
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

    /// <summary>
    /// Moves one entry to a different calendar day, keeping text and completion state.
    /// When <paramref name="withinDayIndex"/> is provided, inserts the entry at that
    /// 0-based position within <paramref name="targetDate"/>'s entries and normalizes
    /// global <see cref="TodoItemState.Order"/> without collisions.
    /// </summary>
    public static bool MoveToDate(
        TodoWidgetState widget, Guid itemId, DateTime targetDate, int? withinDayIndex = null)
    {
        var item = widget.Items.FirstOrDefault(x => x.Id == itemId);
        if (item is null || item.Date == targetDate)
        {
            return false;
        }
        item.Date = targetDate;
        if (withinDayIndex is { } targetDayIndex)
        {
            var targetGroup = widget.Items
                .Where(x => x.Date == targetDate && x.Id != item.Id)
                .OrderBy(x => x.Order)
                .ToList();
            var insertIndex = Math.Clamp(targetDayIndex, 0, targetGroup.Count);
            targetGroup.Insert(insertIndex, item);
            var nonTarget = widget.Items
                .Where(x => x.Date != targetDate)
                .OrderBy(x => x.Order)
                .ToList();
            nonTarget.AddRange(targetGroup);
            for (var i = 0; i < nonTarget.Count; i++)
            {
                nonTarget[i].Order = i;
            }
            widget.Items = nonTarget;
        }
        else
        {
            Normalize(widget);
        }
        return true;
    }

    /// <summary>The entries placed on one calendar day, in their manual order.</summary>
    public static List<TodoItemState> ItemsForDate(TodoWidgetState widget, DateTime date) =>
        widget.Items.Where(x => x.Date == date).OrderBy(x => x.Order).ToList();

    /// <summary>
    /// Replaces one calendar day's entries with the given editor lines, in order:
    /// unchanged lines match by text first (establishing order anchors), remaining lines
    /// reuse unclaimed existing entries within the same anchor span in order (in-place
    /// edits keep their Id and completion state, whereas lines inserted outside an
    /// unclaimed span become new incomplete entries). A span holding fewer lines than
    /// unclaimed entries had something deleted, so it reuses none of them. Surplus
    /// entries are removed, and
    /// blank lines are skipped. A leading "1. "/"1、" numbering prefix is display sugar
    /// and is stripped, while content like "3D建模" survives untouched.
    /// Returns how many entries the day holds afterwards.
    /// </summary>
    public static int SyncDayItems(TodoWidgetState widget, DateTime date, IReadOnlyList<string> lines)
    {
        var texts = lines
            .Select(StripNumberPrefix)
            .Where(line => line.Length > 0)
            .ToList();

        var dayItems = ItemsForDate(widget, date);
        var resolved = new TodoItemState?[texts.Count];
        var claimed = new bool[dayItems.Count];
        var matchedOldIndex = new int[texts.Count];
        Array.Fill(matchedOldIndex, -1);

        for (var i = 0; i < texts.Count; i++)
        {
            for (var j = 0; j < dayItems.Count; j++)
            {
                if (!claimed[j] && dayItems[j].Text == texts[i])
                {
                    resolved[i] = dayItems[j];
                    claimed[j] = true;
                    matchedOldIndex[i] = j;
                    break;
                }
            }
        }

        // For each unmatched line, only reuse an unclaimed entry that sits within the
        // same anchor span (prevJ, nextJ) bounded by the nearest preceding and succeeding
        // exact-text matches. That preserves Id and completion timestamps for in-place
        // text edits (whether completed or not), while preventing a deleted entry before
        // an anchor from transferring its completion state to a newly added line after
        // the anchor (or vice versa).
        // A span with fewer lines than free entries means something in it was deleted, and
        // which free entry belonged to which edited line can no longer be told. Handing the
        // edited line the first free entry would give it a deleted entry's completion state,
        // so such a span only produces new entries. Counted before any reuse claims entries.
        var spanOf = new (int Prev, int Next)[texts.Count];
        var spanLines = new Dictionary<(int, int), int>();
        var spanFreeEntries = new Dictionary<(int, int), int>();
        for (var i = 0; i < texts.Count; i++)
        {
            if (resolved[i] is not null)
            {
                continue;
            }

            var prevJ = -1;
            for (var p = i - 1; p >= 0; p--)
            {
                if (matchedOldIndex[p] >= 0)
                {
                    prevJ = matchedOldIndex[p];
                    break;
                }
            }

            var nextJ = dayItems.Count;
            for (var s = i + 1; s < texts.Count; s++)
            {
                if (matchedOldIndex[s] >= 0)
                {
                    nextJ = matchedOldIndex[s];
                    break;
                }
            }

            var key = (prevJ, nextJ);
            spanOf[i] = key;
            spanLines[key] = spanLines.GetValueOrDefault(key) + 1;
            if (!spanFreeEntries.ContainsKey(key))
            {
                var free = 0;
                for (var j = prevJ + 1; j < nextJ; j++)
                {
                    if (!claimed[j])
                    {
                        free++;
                    }
                }
                spanFreeEntries[key] = free;
            }
        }

        for (var i = 0; i < texts.Count; i++)
        {
            if (resolved[i] is not null)
            {
                continue;
            }

            var (prevJ, nextJ) = spanOf[i];
            var reuseIndex = -1;
            if (spanLines[spanOf[i]] >= spanFreeEntries[spanOf[i]])
            {
                for (var j = prevJ + 1; j < nextJ; j++)
                {
                    if (!claimed[j])
                    {
                        reuseIndex = j;
                        break;
                    }
                }
            }

            if (reuseIndex >= 0)
            {
                var reused = dayItems[reuseIndex];
                claimed[reuseIndex] = true;
                reused.Text = texts[i];
                resolved[i] = reused;
            }
            else
            {
                resolved[i] = new TodoItemState
                {
                    Text = texts[i],
                    Date = date,
                    CreatedDate = DateTimeOffset.Now
                };
            }
        }

        var daySet = dayItems.ToHashSet();
        var others = widget.Items
            .OrderBy(x => x.Order)
            .Where(x => !daySet.Contains(x))
            .ToList();
        others.AddRange(resolved!);
        for (var i = 0; i < others.Count; i++)
        {
            others[i].Order = i;
        }
        widget.Items = others;
        return resolved.Length;
    }

    private static string StripNumberPrefix(string line)
    {
        var trimmed = line.Trim();
        var index = 0;
        while (index < trimmed.Length && char.IsAsciiDigit(trimmed[index]))
        {
            index++;
        }
        if (index == 0)
        {
            return trimmed;
        }
        var after = index;
        if (after < trimmed.Length && (trimmed[after] == '.' || trimmed[after] == '、' || trimmed[after] == '．'))
        {
            after++;
            while (after < trimmed.Length && char.IsWhiteSpace(trimmed[after]))
            {
                after++;
            }
            return trimmed[after..];
        }
        return trimmed;
    }

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
