using System.Collections.ObjectModel;
using BingLan.Core.Models;

namespace BingLan.App.Windows;

/// <summary>One week row of the weekly view: seven day cells, Monday first.</summary>
public sealed class WeekRowView
{
    public IReadOnlyList<WeekCellView> Days { get; init; } = [];
}

/// <summary>One day cell of the weekly view with the live entries placed on that day.</summary>
public sealed class WeekCellView
{
    /// <summary>A shared blank placeholder that keeps the column grid aligned across month breaks.</summary>
    public static readonly WeekCellView Filler = new() { IsFiller = true };

    public DateTime Date { get; init; }
    public string DayNumber { get; init; } = "";
    public string LunarText { get; init; } = "";
    public bool IsToday { get; init; }
    public bool IsWeekend { get; init; }

    /// <summary>True for the blank padding cells around a month fragment.</summary>
    public bool IsFiller { get; init; }
    public ObservableCollection<TodoItemState> Items { get; } = [];
}
