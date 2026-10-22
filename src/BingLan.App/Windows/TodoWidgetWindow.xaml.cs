using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BingLan.Core.Models;
using BingLan.Core.Services;

namespace BingLan.App.Windows;

public partial class TodoWidgetWindow : WidgetWindowBase
{
    // The weekly view shows this many calendar rows, Monday first.
    private const int WeeklyRowCount = 5;

    // Minimum card sizes: the weekly grid needs wider columns and taller rows than
    // the today list, which stays comfortable at the card's original minimums.
    private const double ListMinimumWidth = 226;
    private const double WeeklyMinimumWidth = 520;
    private const double ListMinimumHeight = 166;
    private const double WeeklyMinimumHeight = 460;

    // Paging far enough to overflow DateTime.AddDays would throw; a century of weeks
    // is far beyond any real back-tracking session.
    private const int WeeklyOffsetLimit = 5200;

    private readonly ObservableCollection<TodoItemState> _items;
    private readonly Dictionary<DateTime, WeekCellView> _cellsByDate = [];
    private string? _textBeforeEdit;
    private int _weekOffset;

    public TodoWidgetWindow(TodoWidgetState state)
    {
        State = state;
        _items = new ObservableCollection<TodoItemState>(state.Items.OrderBy(x => x.Order));
        InitializeComponent();
        DataContext = state;
        ApplyAppearance(state.Appearance);
        TodoList.ItemsSource = _items;
        TodoList.ItemContainerGenerator.StatusChanged += (_, _) => ApplyCompletedVisibility();
        ApplyPlacement(state.Placement, state.IsLocked);
        WidgetCornerRadius = state.CornerRadius;
        ApplyViewMode();
    }

    public TodoWidgetState State { get; }

    protected override System.Windows.Controls.TextBox? TitleEditorBox => TitleEditor;

    protected override void CommitTitleEdit()
    {
        State.Title = string.IsNullOrWhiteSpace(TitleEditor.Text) ? "待办便签" : TitleEditor.Text.Trim();
        TitleEditor.Text = State.Title;
        NotifyStateChanged();
    }

    protected override void OnLockChanged(bool locked)
    {
        State.IsLocked = locked;
    }

    public void CaptureState()
    {
        CopyPlacementTo(State.Placement);
        State.CornerRadius = WidgetCornerRadius;
        State.Items = _items.OrderBy(x => x.Order).ToList();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        CaptureState();
        var item = State.WeeklyView
            ? TodoService.Add(State, "", DateTime.Today)
            : TodoService.Add(State);
        _items.Add(item);
        if (State.WeeklyView && _cellsByDate.TryGetValue(DateTime.Today, out var cell))
        {
            cell.Items.Add(item);
        }
        RefreshSummary();
        NotifyStateChanged();

        Dispatcher.BeginInvoke(() =>
        {
            if (TodoList.ItemContainerGenerator.ContainerFromItem(item) is not ContentPresenter presenter)
            {
                return;
            }
            var editor = FindVisualChild<System.Windows.Controls.TextBox>(presenter);
            editor?.Focus();
        }, DispatcherPriority.Input);
    }

    private void Completion_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }
        if (sender is FrameworkElement { DataContext: TodoItemState item })
        {
            TodoService.StampCompletion(item);
        }
        ApplyCompletedVisibility();
        RefreshSummary();
        CaptureState();
        NotifyStateChanged();
    }

    private void TodoText_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _textBeforeEdit = (sender as System.Windows.Controls.TextBox)?.Text;
    }

    private void TodoText_LostFocus(object sender, RoutedEventArgs e)
    {
        RefreshSummary();
        CaptureState();
        NotifyStateChanged();
    }

    private void TodoText_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && sender is System.Windows.Controls.TextBox box && _textBeforeEdit is not null)
        {
            box.Text = _textBeforeEdit;
        }

        if (e.Key is Key.Enter or Key.Escape)
        {
            // Clearing keyboard focus does not raise LostFocus, so save here.
            CaptureState();
            NotifyStateChanged();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void ClearCompleted_Click(object sender, RoutedEventArgs e)
    {
        CaptureState();
        // Only today's completed entries are cleaned; dated history in the weekly
        // view stays until it is deleted one by one.
        if (TodoService.RemoveCompleted(
                State, x => TodoService.BelongsToTodayList(x, DateTime.Today)) == 0)
        {
            return;
        }

        _items.Clear();
        foreach (var item in State.Items)
        {
            _items.Add(item);
        }
        RefreshSummary();
        NotifyStateChanged();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(sender, -1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(sender, 1);

    private void Move(object sender, int offset)
    {
        if ((sender as FrameworkElement)?.DataContext is not TodoItemState item)
        {
            return;
        }
        CaptureState();
        bool moved;
        DateTime? movedDate = null;
        if (State.WeeklyView && item.Date is { } date)
        {
            moved = TodoService.MoveWithinDate(State, item.Id, offset);
            movedDate = date;
        }
        else
        {
            moved = TodoService.MoveWithinScope(
                State, item.Id, offset, x => TodoService.BelongsToTodayList(x, DateTime.Today));
        }
        if (!moved)
        {
            return;
        }

        if (movedDate is { } cellDate && _cellsByDate.TryGetValue(cellDate, out var movedCell))
        {
            ResortCell(movedCell);
        }
        else
        {
            ReloadItems();
        }
        NotifyStateChanged();
    }

    private void DeleteTodo_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TodoItemState item)
        {
            return;
        }
        _items.Remove(item);
        if (item.Date is { } date && _cellsByDate.TryGetValue(date, out var cell))
        {
            cell.Items.Remove(item);
        }
        TodoService.Remove(State, item.Id);
        CaptureState();
        RefreshSummary();
        NotifyStateChanged();
    }

    private void ToggleCompletedVisibility_Click(object sender, RoutedEventArgs e)
    {
        State.HideCompleted = !State.HideCompleted;
        if (State.WeeklyView)
        {
            // The weekly rows read HideCompleted through style triggers, which only
            // re-evaluate when the row containers are rebuilt.
            RenderWeekView();
        }
        else
        {
            ApplyCompletedVisibility();
        }
        RefreshSummary();
        NotifyStateChanged();
    }

    private void ToggleViewMode_Click(object sender, RoutedEventArgs e)
    {
        State.WeeklyView = !State.WeeklyView;
        ApplyViewMode();
        NotifyStateChanged();
    }

    private void WeekPrev_Click(object sender, RoutedEventArgs e)
    {
        _weekOffset = Math.Max(_weekOffset - 1, -WeeklyOffsetLimit);
        RenderWeekView();
    }

    private void WeekNext_Click(object sender, RoutedEventArgs e)
    {
        _weekOffset = Math.Min(_weekOffset + 1, WeeklyOffsetLimit);
        RenderWeekView();
    }

    private void WeekToday_Click(object sender, RoutedEventArgs e)
    {
        _weekOffset = 0;
        RenderWeekView();
    }

    private void WeekCellSurface_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WeekCellView } surface
            && FindDirectVisualChild<System.Windows.Controls.TextBox>(surface) is { } editor)
        {
            editor.Visibility = Visibility.Visible;
            editor.Focus();
            e.Handled = true;
        }
    }

    private void WeekCellEditor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox editor)
        {
            return;
        }
        if (e.Key is Key.Enter or Key.Escape)
        {
            var text = editor.Text.Trim();
            if (e.Key == Key.Enter && editor.DataContext is WeekCellView cell && text.Length > 0)
            {
                AddWeeklyItem(cell, text);
            }
            editor.Clear();
            CloseCellEditor(editor);
            e.Handled = true;
        }
    }

    private void WeekCellEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox editor)
        {
            return;
        }
        var text = editor.Text.Trim();
        if (editor.DataContext is WeekCellView cell && text.Length > 0)
        {
            AddWeeklyItem(cell, text);
        }
        CloseCellEditor(editor);
    }

    private void AddWeeklyItem(WeekCellView cell, string text)
    {
        CaptureState();
        var item = TodoService.Add(State, text, cell.Date);
        _items.Add(item);
        cell.Items.Add(item);
        RefreshSummary();
        NotifyStateChanged();
    }

    private static void CloseCellEditor(System.Windows.Controls.TextBox editor)
    {
        editor.Text = "";
        editor.Visibility = Visibility.Collapsed;
    }

    private void ApplyViewMode()
    {
        ListHost.Visibility = State.WeeklyView ? Visibility.Collapsed : Visibility.Visible;
        WeekHost.Visibility = State.WeeklyView ? Visibility.Visible : Visibility.Collapsed;
        ViewModeButton.Content = State.WeeklyView ? "列表" : "周历";
        ClearCompletedButton.Visibility = State.WeeklyView ? Visibility.Collapsed : Visibility.Visible;
        // Seven day cells only stay readable when each column has room for text and
        // the five rows have real height; the today list stays comfortable at the
        // card's original minimum size.
        MinWidth = State.WeeklyView ? WeeklyMinimumWidth : ListMinimumWidth;
        MinHeight = State.WeeklyView ? WeeklyMinimumHeight : ListMinimumHeight;
        if (State.WeeklyView)
        {
            // Min sizes alone only clamp later layout passes; widen and raise an
            // already smaller card right away so the grid is usable from frame one.
            Width = Math.Max(Width, WeeklyMinimumWidth);
            Height = Math.Max(Height, WeeklyMinimumHeight);
            RenderWeekView();
        }
        else
        {
            RefreshSummary();
        }
    }

    private void RenderWeekView()
    {
        var firstMonday = MondayOf(DateTime.Today).AddDays(7 * (_weekOffset - 1));
        var lastDay = firstMonday.AddDays(7 * WeeklyRowCount - 1);
        WeekRange.Text = $"{firstMonday:M月d日} – {lastDay:M月d日}";
        WeekTodayButton.Visibility = _weekOffset == 0 ? Visibility.Collapsed : Visibility.Visible;

        _cellsByDate.Clear();
        var rows = new List<WeekRowView>(WeeklyRowCount);
        for (var row = 0; row < WeeklyRowCount; row++)
        {
            var monday = firstMonday.AddDays(row * 7);
            var days = new List<WeekCellView>(7);
            for (var day = 0; day < 7; day++)
            {
                var date = monday.AddDays(day);
                var cell = new WeekCellView
                {
                    Date = date,
                    DayNumber = date.Day.ToString(),
                    LunarText = LunarDayText.For(date),
                    IsToday = date == DateTime.Today,
                    IsWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                };
                foreach (var entry in State.Items.Where(x => x.Date == date).OrderBy(x => x.Order))
                {
                    cell.Items.Add(entry);
                }
                _cellsByDate[date] = cell;
                days.Add(cell);
            }
            rows.Add(new WeekRowView { Days = days });
        }
        WeekRows.ItemsSource = rows;
        RefreshSummary();
    }

    private void ResortCell(WeekCellView cell)
    {
        var ordered = State.Items
            .Where(x => x.Date == cell.Date)
            .OrderBy(x => x.Order)
            .ToList();
        cell.Items.Clear();
        foreach (var entry in ordered)
        {
            cell.Items.Add(entry);
        }
        RefreshSummary();
    }

    private void ReloadItems()
    {
        _items.Clear();
        foreach (var item in State.Items.OrderBy(x => x.Order))
        {
            _items.Add(item);
        }
        RefreshSummary();
    }

    // Rows outside the today scope and completed rows are hidden in place rather than
    // filtered out of the list, so the other rows keep their controls and stay reachable
    // for screen readers and other assistive tools. _items itself stays complete so
    // CaptureState keeps persisting every entry, dated or not.
    private void ApplyCompletedVisibility()
    {
        var today = DateTime.Today;
        foreach (var item in _items)
        {
            if (TodoList.ItemContainerGenerator.ContainerFromItem(item) is UIElement row)
            {
                var visible = TodoService.BelongsToTodayList(item, today)
                    && (!State.HideCompleted || !item.IsCompleted);
                row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    // Blank rows are places to type, not tasks, so they are left out of the count.
    private void RefreshSummary()
    {
        if (State.WeeklyView)
        {
            var firstMonday = MondayOf(DateTime.Today).AddDays(7 * (_weekOffset - 1));
            var lastDay = firstMonday.AddDays(7 * WeeklyRowCount - 1);
            var written = _items
                .Where(x => !string.IsNullOrWhiteSpace(x.Text)
                    && x.Date is { } date && date >= firstMonday && date <= lastDay)
                .ToList();
            var completed = written.Count(x => x.IsCompleted);
            SummaryText.Text = written.Count == 0 ? "这段时间还没有记录" : $"{completed}/{written.Count} 已完成";
            CompletedVisibilityButton.Content = State.HideCompleted ? "显示已完成" : "隐藏已完成";
            return;
        }

        var today = DateTime.Today;
        var listed = _items
            .Where(x => !string.IsNullOrWhiteSpace(x.Text)
                && TodoService.BelongsToTodayList(x, today))
            .ToList();
        var done = listed.Count(x => x.IsCompleted);
        SummaryText.Text = listed.Count == 0 ? "写下第一条待办" : $"{done}/{listed.Count} 已完成";
        ClearCompletedButton.IsEnabled = _items.Any(x => x.IsCompleted);
        CompletedVisibilityButton.Content = State.HideCompleted ? "显示已完成" : "隐藏已完成";
    }

    private static DateTime MondayOf(DateTime date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private static T? FindDirectVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            if (System.Windows.Media.VisualTreeHelper.GetChild(parent, i) is T child)
            {
                return child;
            }
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T result)
            {
                return result;
            }
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }
        return null;
    }
}
