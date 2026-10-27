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
    private bool _hideCompleted;
    private int _hiddenCount;
    private bool _isCompact;

    public DateTime Date { get; init; }
    public string DayNumber { get; init; } = "";
    public string LunarText { get; init; } = "";
    public bool IsToday { get; init; }
    public bool IsWeekend { get; init; }

    /// <summary>True for the blank padding cells around a month fragment.</summary>
    public bool IsFiller { get; init; }

    /// <summary>Whether completed entries are hidden from the cell preview and overflow count.</summary>
    public bool HideCompleted
    {
        get => _hideCompleted;
        set
        {
            if (_hideCompleted == value)
            {
                return;
            }
            _hideCompleted = value;
            RefreshPreview();
        }
    }

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

    /// <summary>The visible entries of the day, scrolled inside the compact cell.</summary>
    public ObservableCollection<TodoItemState> PreviewItems { get; } = [];

    /// <summary>Whether some entries do not fit the cell; shown as a "+N" chip.</summary>
    public bool HasOverflow => _hiddenCount > 0;

    /// <summary>How many entries the cell cannot show in full at its current size.</summary>
    public int OverflowCount => _hiddenCount;

    /// <summary>Whether the cell is too short for a two-line entry, so entries show one line each.</summary>
    public bool IsCompact
    {
        get => _isCompact;
        set
        {
            if (_isCompact == value)
            {
                return;
            }
            _isCompact = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Set by the cell's layout: the entries outside its visible area.</summary>
    public void SetHiddenCount(int count)
    {
        if (_hiddenCount == count)
        {
            return;
        }
        _hiddenCount = count;
        OnPropertyChanged(nameof(HasOverflow));
        OnPropertyChanged(nameof(OverflowCount));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public WeekCellView()
    {
        Items.CollectionChanged += (_, _) => RefreshPreview();
    }

    public void RefreshPreview()
    {
        var visible = _hideCompleted
            ? Items.Where(x => !x.IsCompleted)
            : Items;
        PreviewItems.Clear();
        foreach (var entry in visible)
        {
            PreviewItems.Add(entry);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
