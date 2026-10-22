using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using BingLan.Core.Models;

namespace BingLan.App.Windows;

/// <summary>One week row of the weekly view: seven day cells, Monday first.</summary>
public sealed class WeekRowView
{
    public IReadOnlyList<WeekCellView> Days { get; init; } = [];
}

/// <summary>One day cell of the weekly view with the live entries placed on that day.</summary>
public sealed class WeekCellView : INotifyPropertyChanged
{
    /// <summary>A shared blank placeholder that keeps the column grid aligned across month breaks.</summary>
    public static readonly WeekCellView Filler = new() { IsFiller = true };

    private bool _isSelected;

    public DateTime Date { get; init; }
    public string DayNumber { get; init; } = "";
    public string LunarText { get; init; } = "";
    public bool IsToday { get; init; }
    public bool IsWeekend { get; init; }

    /// <summary>True for the blank padding cells around a month fragment.</summary>
    public bool IsFiller { get; init; }

    /// <summary>Whether the cell is the one the user clicked last, drawn with a blue edge.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<TodoItemState> Items { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
