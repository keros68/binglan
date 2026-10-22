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

    /// <summary>How many entries the compact cell shows before the overflow chip.</summary>
    private const int PreviewCapacity = 2;

    private bool _isSelected;
    private bool _hasOverflow;

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

    /// <summary>All entries of the day (full list — the day editor and the peek popup bind this).</summary>
    public ObservableCollection<TodoItemState> Items { get; } = [];

    /// <summary>The first few entries shown inside the compact cell.</summary>
    public ObservableCollection<TodoItemState> PreviewItems { get; } = [];

    /// <summary>Entries beyond the compact preview; shown on the overflow chip.</summary>
    public bool HasOverflow => Items.Count > PreviewCapacity;

    /// <summary>How many entries are hidden behind the overflow chip.</summary>
    public int OverflowCount => Math.Max(0, Items.Count - PreviewCapacity);

    public event PropertyChangedEventHandler? PropertyChanged;

    public WeekCellView()
    {
        Items.CollectionChanged += (_, _) =>
        {
            PreviewItems.Clear();
            foreach (var entry in Items.Take(PreviewCapacity))
            {
                PreviewItems.Add(entry);
            }
            OnPropertyChanged(nameof(HasOverflow));
            OnPropertyChanged(nameof(OverflowCount));
        };
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
