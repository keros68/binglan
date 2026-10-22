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
    // The weekly view shows four calendar rows, Monday first — 28 days span at most
    // two months, so month breaks never crowd together.
    private const int WeeklyRowCount = 4;

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
    private DayEditWindow? _dayEditor;
    private WeekCellView? _selectedCellView;
    private WeekCellView? _peekCell;
    private TodoItemState? _editingItem;
    private string? _editingOriginalText;
    private TodoItemState? _lastMovedItem;
    private DateTime _lastMovedFrom;
    private DispatcherTimer? _moveToastTimer;
    private DispatcherTimer? _peekLongPressTimer;
    private WeekCellView? _peekDragCell;
    private TodoItemState? _peekDragItem;
    private System.Windows.Controls.ContentPresenter? _peekDragContainer;
    private System.Windows.Controls.Border? _peekDragRow;
    private System.Windows.Point _peekPressPoint;
    private int _peekFromIndex;
    private int _peekTargetIndex;
    private PeekDragPhase _peekDragPhase = PeekDragPhase.Idle;

    private enum PeekDragPhase
    {
        Idle,
        Pressed,
        Dragging
    }

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
        if (sender is not FrameworkElement { DataContext: WeekCellView cell } surface || cell.IsFiller)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            OpenDayEditor(cell);
            e.Handled = true;
            return;
        }

        SelectCell(cell);
        e.Handled = true;
    }

    private int PeekIndexAt(double y)
    {
        for (var i = 0; i < WeekPeekList.Items.Count; i++)
        {
            if (WeekPeekList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container)
            {
                var topLeft = container.TranslatePoint(new System.Windows.Point(0, 0), WeekPeekList);
                if (y >= topLeft.Y && y < topLeft.Y + container.ActualHeight)
                {
                    return i;
                }
            }
        }
        return -1;
    }

    // ------------------------------------------------------------------
    // Long-press drag reordering inside the day peek popup.
    // State machine: Idle → Pressed(mouse down on a row) → Dragging(300 ms
    // held, within the drift threshold, button still down; follows the
    // cursor, other rows shift live).
    // Row identity is resolved through the item container generator: a press
    // can land on a TextBoxView/TextBlock whose DataContext is the same todo
    // item, so no hit element may be assumed to be the row visual. Lift
    // visuals (background/border) live on the template-root Border; z-order
    // and drag transforms live on the generated ContentPresenter container.
    // The collection is only moved once, on release. All cancel paths funnel
    // through CancelPeekDrag().
    // ------------------------------------------------------------------
    protected override void OnPreviewMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (WeekPeekOverlay.Visibility != Visibility.Visible || _peekDragPhase != PeekDragPhase.Idle)
        {
            return;
        }
        if (_peekCell is null)
        {
            return;
        }
        // While a row's inline editor is open, presses position the caret or
        // select text — long-press must not hijack them.
        if (_editingItem is not null)
        {
            return;
        }
        // A press can land on a TextBoxView/TextBlock inside the row, whose DataContext
        // is also the todo item — and the row Border is not an ancestor of the hit
        // element at a fixed depth. Resolve the row through the item container
        // generator instead of assuming any hit element is the row visual.
        if (FindPeekRowContainer((System.Windows.DependencyObject)e.OriginalSource) is not { } container
            || container.DataContext is not TodoItemState item
            || !_peekCell.Items.Contains(item))
        {
            return;
        }
        // Checkbox / × handle their own clicks; text double-click opens the editor.
        if (HitsChildControl((System.Windows.DependencyObject)e.OriginalSource, container))
        {
            return;
        }

        _peekDragCell = _peekCell;
        _peekDragItem = item;
        _peekDragContainer = container;
        _peekDragRow = PeekRowVisual(container);
        _peekPressPoint = e.GetPosition(WeekPeekList);
        _peekDragPhase = PeekDragPhase.Pressed;
        _peekLongPressTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _peekLongPressTimer.Tick -= PeekLongPress_Tick;
        _peekLongPressTimer.Tick += PeekLongPress_Tick;
        _peekLongPressTimer.Stop();
        _peekLongPressTimer.Start();
    }

    protected override void OnPreviewMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        if (_peekDragPhase == PeekDragPhase.Pressed)
        {
            var pos = e.GetPosition(WeekPeekList);
            if (Math.Abs(pos.X - _peekPressPoint.X) > SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(pos.Y - _peekPressPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                // Moved away early: this was a stray press, not a long-press intent.
                _peekLongPressTimer?.Stop();
                _peekDragPhase = PeekDragPhase.Idle;
                _peekDragCell = null;
                _peekDragItem = null;
                _peekDragContainer = null;
                _peekDragRow = null;
            }
        }
        else if (_peekDragPhase == PeekDragPhase.Dragging)
        {
            DoPeekDragMove(e.GetPosition(WeekPeekList));
            e.Handled = true;
        }
    }

    protected override void OnPreviewMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_peekDragPhase == PeekDragPhase.Pressed)
        {
            _peekLongPressTimer?.Stop();
            _peekDragPhase = PeekDragPhase.Idle;
            _peekDragCell = null;
            _peekDragItem = null;
            _peekDragContainer = null;
            _peekDragRow = null;
        }
        else if (_peekDragPhase == PeekDragPhase.Dragging)
        {
            SettlePeekDrag();
        }
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }
        if (_peekDragPhase == PeekDragPhase.Dragging)
        {
            CancelPeekDrag();
            e.Handled = true;
        }
        else if (_peekDragPhase == PeekDragPhase.Pressed)
        {
            // Esc during the hold: kill the pending arm too, not just the drag.
            _peekLongPressTimer?.Stop();
            _peekDragPhase = PeekDragPhase.Idle;
            _peekDragCell = null;
            _peekDragItem = null;
            _peekDragContainer = null;
            _peekDragRow = null;
            e.Handled = true;
        }
    }

    private System.Windows.Controls.ContentPresenter? FindPeekRowContainer(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is System.Windows.Controls.ContentPresenter element
                && WeekPeekList.ItemContainerGenerator.IndexFromContainer(element) >= 0)
            {
                return element;
            }
            if (ReferenceEquals(current, WeekPeekList))
            {
                return null;
            }
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static System.Windows.Controls.Border? PeekRowVisual(System.Windows.DependencyObject container)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(container);
        for (var index = 0; index < count; index++)
        {
            if (System.Windows.Media.VisualTreeHelper.GetChild(container, index) is System.Windows.Controls.Border border)
            {
                return border;
            }
        }
        return null;
    }

    private static bool HitsChildControl(DependencyObject source, DependencyObject row)
    {
        var current = source;
        while (current is not null && !ReferenceEquals(current, row))
        {
            if (current is System.Windows.Controls.CheckBox or System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void PeekLongPress_Tick(object? sender, EventArgs e)
    {
        _peekLongPressTimer?.Stop();
        if (_peekDragPhase != PeekDragPhase.Pressed
            || Mouse.LeftButton != MouseButtonState.Pressed
            || _peekDragCell is null)
        {
            _peekDragPhase = PeekDragPhase.Idle;
            _peekDragCell = null;
            _peekDragItem = null;
            _peekDragContainer = null;
            _peekDragRow = null;
            return;
        }
        var pos = Mouse.GetPosition(WeekPeekList);
        if (Math.Abs(pos.X - _peekPressPoint.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(pos.Y - _peekPressPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            _peekDragPhase = PeekDragPhase.Idle;
            _peekDragCell = null;
            _peekDragItem = null;
            _peekDragContainer = null;
            _peekDragRow = null;
            return;
        }
        ArmPeekDrag();
    }

    private void ArmPeekDrag()
    {
        _peekDragPhase = PeekDragPhase.Dragging;
        _peekDragContainer!.CaptureMouse();
        // Focus loss (Alt+Tab, a system menu, another capture) silently drops
        // the capture; without this the phase would stay Dragging forever and
        // every later press would be ignored. Settle what was shown so far.
        _peekDragContainer.LostMouseCapture -= PeekDragContainer_LostMouseCapture;
        _peekDragContainer.LostMouseCapture += PeekDragContainer_LostMouseCapture;
        // Z-order and drag transforms live on the generated container (a direct
        // child of the list panel); the lift visuals live on the template Border.
        System.Windows.Controls.Panel.SetZIndex(_peekDragContainer, 100);
        if (_peekDragContainer.RenderTransform is not System.Windows.Media.TranslateTransform)
        {
            _peekDragContainer.RenderTransform = new System.Windows.Media.TranslateTransform();
        }
        if (_peekDragRow is not null)
        {
            _peekDragRow.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
            _peekDragRow.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xFF, 0x7F, 0xA8, 0xD9));
            _peekDragRow.BorderThickness = new System.Windows.Thickness(1.5);
            _peekDragRow.Cursor = System.Windows.Input.Cursors.SizeAll;
        }
        _peekFromIndex = _peekDragCell!.Items.IndexOf(_peekDragItem!);
        _peekTargetIndex = _peekFromIndex;
        ApplyPeekShifts();
    }

    private void PeekDragContainer_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_peekDragPhase == PeekDragPhase.Dragging)
        {
            SettlePeekDrag();
        }
    }

    private void DoPeekDragMove(System.Windows.Point posInList)
    {
        if (_peekDragContainer?.RenderTransform is System.Windows.Media.TranslateTransform translate)
        {
            translate.Y = posInList.Y - _peekPressPoint.Y;
        }
        var target = PeekIndexAt(posInList.Y);
        if (target >= 0 && target != _peekTargetIndex)
        {
            _peekTargetIndex = target;
            ApplyPeekShifts();
        }
    }

    private void ApplyPeekShifts()
    {
        if (_peekDragContainer is null)
        {
            return;
        }
        // Index-range shift, recomputed from scratch each call (idempotent):
        // rows between the origin and the current target move over by one row
        // height. Never read container positions here — TranslatePoint would
        // include the transforms applied by the previous call and oscillate.
        var rowHeight = _peekDragContainer.ActualHeight + 4;
        for (var index = 0; index < WeekPeekList.Items.Count; index++)
        {
            if (WeekPeekList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
            {
                continue;
            }
            double shift;
            if (index == _peekFromIndex)
            {
                shift = 0;
            }
            else if (_peekFromIndex < _peekTargetIndex)
            {
                shift = index > _peekFromIndex && index <= _peekTargetIndex ? -rowHeight : 0;
            }
            else
            {
                shift = index >= _peekTargetIndex && index < _peekFromIndex ? rowHeight : 0;
            }
            if (container.RenderTransform is System.Windows.Media.TranslateTransform existing)
            {
                existing.Y = shift;
            }
            else
            {
                container.RenderTransform = new System.Windows.Media.TranslateTransform { Y = shift };
            }
        }
    }

    private void SettlePeekDrag()
    {
        // Idle before releasing: releasing raises LostMouseCapture, whose
        // handler must not re-enter settle.
        _peekDragPhase = PeekDragPhase.Idle;
        var finalIndex = Math.Clamp(_peekTargetIndex, 0, Math.Max(0, (_peekDragCell?.Items.Count ?? 1) - 1));
        ResetPeekDragVisual();
        _peekDragContainer?.ReleaseMouseCapture();
        if (_peekDragCell is not null
            && _peekDragItem is not null
            && finalIndex != _peekFromIndex
            && _peekDragCell.Items.IndexOf(_peekDragItem) is var current
            && current >= 0
            && current != finalIndex)
        {
            _peekDragCell.Items.Move(current, finalIndex);
            for (var i = 0; i < _peekDragCell.Items.Count; i++)
            {
                _peekDragCell.Items[i].Order = i;
            }
            TodoService.Normalize(State);
            CaptureState();
            NotifyStateChanged();
        }
        _peekDragPhase = PeekDragPhase.Idle;
        _peekDragCell = null;
        _peekDragItem = null;
        _peekDragContainer = null;
        _peekDragRow = null;
    }

    private void CancelPeekDrag()
    {
        // Idle before releasing so the LostMouseCapture handler stays quiet.
        _peekDragPhase = PeekDragPhase.Idle;
        ResetPeekDragVisual();
        _peekDragContainer?.ReleaseMouseCapture();
        _peekDragCell = null;
        _peekDragItem = null;
        _peekDragContainer = null;
        _peekDragRow = null;
    }

    private void ResetPeekDragVisual()
    {
        for (var index = 0; index < WeekPeekList.Items.Count; index++)
        {
            if (WeekPeekList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
            {
                container.RenderTransform = System.Windows.Media.Transform.Identity;
                System.Windows.Controls.Panel.SetZIndex(container, 0);
            }
        }
        if (_peekDragRow is not null)
        {
            _peekDragRow.Background = System.Windows.Media.Brushes.Transparent;
            _peekDragRow.BorderBrush = System.Windows.Media.Brushes.Transparent;
            _peekDragRow.BorderThickness = new System.Windows.Thickness(0);
            _peekDragRow.Cursor = null;
        }
    }

    private void WeekOverflow_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WeekCellView cell && !cell.IsFiller)
        {
            OpenDayPeek(cell, sender as FrameworkElement);
            e.Handled = true;
        }
    }

    private void OpenDayPeek(WeekCellView cell, FrameworkElement? cellElement)
    {
        _peekCell = cell;
        WeekPeekTitle.Text = cell.Date.ToString("M月d日") + " · " +
            WeekdayName(cell.Date);
        WeekPeekList.ItemsSource = cell.Items;
        WeekPeekInput.Clear();
        MoveToast.Visibility = Visibility.Collapsed;
        WeekPeekOverlay.Visibility = Visibility.Visible;

        // Anchor the panel next to the source cell, flipping inward at the edges.
        if (cellElement is not null)
        {
            var rect = cellElement.TransformToAncestor(WeekHost)
                .TransformBounds(new Rect(0, 0, cellElement.ActualWidth, cellElement.ActualHeight));
            var left = rect.Right + 6;
            if (left + WeekPeekPanel.Width > WeekHost.ActualWidth)
            {
                left = rect.Left - WeekPeekPanel.Width - 6;
            }
            left = Math.Max(0, Math.Min(left, Math.Max(0, WeekHost.ActualWidth - WeekPeekPanel.Width)));
            Canvas.SetLeft(WeekPeekPanel, left);
            Canvas.SetTop(WeekPeekPanel, Math.Max(0, rect.Top));
        }
    }

    private void CloseDayPeek()
    {
        WeekPeekOverlay.Visibility = Visibility.Collapsed;
        WeekPeekList.ItemsSource = null;
        _peekCell = null;
    }

    private void WeekPeekPrevDay_Click(object sender, RoutedEventArgs e) => NavigatePeekDay(-1);

    private void WeekPeekNextDay_Click(object sender, RoutedEventArgs e) => NavigatePeekDay(1);

    private void NavigatePeekDay(int offset)
    {
        if (_peekCell is null)
        {
            return;
        }
        var target = _peekCell.Date.AddDays(offset);
        if (_cellsByDate.TryGetValue(target, out var adjacent))
        {
            _selectedCellView = adjacent;
            adjacent.IsSelected = true;
            OpenDayPeek(adjacent, null);
        }
    }

    private void WeekPeekOverlay_BackdropClick(object sender, MouseButtonEventArgs e) => CloseDayPeek();

    private void WeekPeekPanel_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void WeekPeekClose_Click(object sender, RoutedEventArgs e) => CloseDayPeek();

    private void WeekPeekInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is Key.Enter && _peekCell is not null)
        {
            var text = WeekPeekInput.Text.Trim();
            if (text.Length > 0)
            {
                AddWeeklyItem(_peekCell, text);
            }
            WeekPeekInput.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseDayPeek();
            e.Handled = true;
        }
    }

    private void MoveItemToDate(TodoItemState item, DateTime target)
    {
        if (item.Date is not { } from || !TodoService.MoveToDate(State, item.Id, target))
        {
            return;
        }
        if (_cellsByDate.TryGetValue(from, out var oldCell))
        {
            oldCell.Items.Remove(item);
            ResortCell(oldCell);
        }
        if (_cellsByDate.TryGetValue(target, out var newCell))
        {
            newCell.Items.Add(item);
            ResortCell(newCell);
        }
        RefreshSummary();
        NotifyStateChanged();
        ShowMoveToast($"已移到 {target:M月d日}（{WeekdayName(target)}）", item, from);
    }

    private void ShowMoveToast(string message, TodoItemState item, DateTime from)
    {
        _lastMovedItem = item;
        _lastMovedFrom = from;
        MoveToastText.Text = message;
        MoveToast.Visibility = Visibility.Visible;
        _moveToastTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _moveToastTimer.Tick -= HideMoveToast;
        _moveToastTimer.Tick += HideMoveToast;
        _moveToastTimer.Stop();
        _moveToastTimer.Start();
    }

    private void HideMoveToast(object? sender, EventArgs e)
    {
        _moveToastTimer?.Stop();
        MoveToast.Visibility = Visibility.Collapsed;
    }

    private void ToastUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_lastMovedItem is { } item)
        {
            HideMoveToast(this, EventArgs.Empty);
            MoveItemToDate(item, _lastMovedFrom);
        }
    }

        private void PeekText_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Double-click on a peek row's text unlocks in-place editing for that entry.
        if (e.ClickCount == 2
            && sender is System.Windows.Controls.TextBox { IsReadOnly: true, DataContext: TodoItemState item } editor)
        {
            _editingItem = item;
            _editingOriginalText = item.Text;
            editor.IsReadOnly = false;
            editor.Focusable = true;
            editor.Cursor = System.Windows.Input.Cursors.IBeam;
            editor.Focus();
            editor.SelectAll();
            e.Handled = true;
        }
    }

    private void PeekText_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape)
        {
            e.Handled = true;
            EndPeekTextEdit(sender);
        }
    }

    private void PeekText_LostFocus(object sender, RoutedEventArgs e)
    {
        // Live binding has kept the text current; ending the edit state is enough.
        EndPeekTextEdit(sender);
    }

    private void EndPeekTextEdit(object? sender)
    {
        if (sender is System.Windows.Controls.TextBox editor)
        {
            editor.IsReadOnly = true;
            editor.Focusable = false;
            editor.Cursor = System.Windows.Input.Cursors.Arrow;
        }
        _editingItem = null;
    }

    private static string WeekdayName(DateTime date) =>
        date.DayOfWeek switch
        {
            DayOfWeek.Monday => "周一",
            DayOfWeek.Tuesday => "周二",
            DayOfWeek.Wednesday => "周三",
            DayOfWeek.Thursday => "周四",
            DayOfWeek.Friday => "周五",
            DayOfWeek.Saturday => "周六",
            _ => "周日"
        };

    private void PeekRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TodoItemState item)
        {
            return;
        }
        var row = (FrameworkElement)sender;
        row.ContextMenu ??= new System.Windows.Controls.ContextMenu();
        var menu = row.ContextMenu;

        // Rebuild the move-to block each time: the target dates depend on the entry.
        while (menu.Items.Count > 0 && menu.Items[0] is System.Windows.Controls.MenuItem { Header: string h } && h.StartsWith("移到"))
        {
            menu.Items.RemoveAt(0);
        }
        if (menu.Items.Count > 0 && menu.Items[0] is System.Windows.Controls.Separator)
        {
            menu.Items.RemoveAt(0);
        }

        if (item.Date is { } ownDate)
        {
            var moveTo = new System.Windows.Controls.MenuItem { Header = "移到 ▸" };
            foreach (var (offset, label) in new[] { (1, "明天"), (2, "后天"), (3, "大后天") })
            {
                var target = ownDate.AddDays(offset);
                var entry = new System.Windows.Controls.MenuItem
                {
                    Header = $"{label}（{target:M月d日} {WeekdayName(target)}）"
                };
                var captured = item;
                entry.Click += (_, _) => MoveItemToDate(captured, target);
                moveTo.Items.Add(entry);
            }
            menu.Items.Insert(0, new System.Windows.Controls.Separator());
            menu.Items.Insert(0, moveTo);
        }
    }

    private void SelectCell(WeekCellView cell)
    {
        if (_selectedCellView == cell)
        {
            return;
        }
        if (_selectedCellView is not null)
        {
            _selectedCellView.IsSelected = false;
        }
        _selectedCellView = cell;
        cell.IsSelected = true;
    }

    private void OpenDayEditor(WeekCellView cell)
    {
        // One day editor at a time; opening another day commits the previous one first.
        _dayEditor?.Close();

        var lines = State.Items
            .Where(x => x.Date == cell.Date)
            .OrderBy(x => x.Order)
            .Select(x => x.Text)
            .Where(text => text.Length > 0)
            .ToList();
        _dayEditor = new DayEditWindow(cell.Date, lines) { Owner = this };
        _dayEditor.Committed += (date, committedLines) => CommitDayEditor(date, committedLines);
        _dayEditor.Closed += (_, _) => _dayEditor = null;
        _dayEditor.Show();
        _dayEditor.Activate();
    }

    private void CommitDayEditor(DateTime date, IReadOnlyList<string> lines)
    {
        CaptureState();
        TodoService.SyncDayItems(State, date, lines);
        ReloadItems();
        if (_cellsByDate.TryGetValue(date, out var cell))
        {
            ResortCell(cell);
        }
        RefreshSummary();
        NotifyStateChanged();
    }

    // ------------------------------------------------------------------
    // Long-press drag reordering inside the day peek popup.
    // State machine: Idle → Pressed(mouse down on a row) → Armed(300 ms held,

    private void AddWeeklyItem(WeekCellView cell, string text)
    {
        CaptureState();
        var item = TodoService.Add(State, text, cell.Date);
        _items.Add(item);
        cell.Items.Add(item);
        RefreshSummary();
        NotifyStateChanged();
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
        // The window still slides one week per page, but rows are month-aligned:
        // when a month ends mid-week the row ends there and the next month starts
        // on a fresh row at its own weekday column, so months read at a glance.
        var firstMonday = MondayOf(DateTime.Today).AddDays(7 * (_weekOffset - 1));
        var lastDay = firstMonday.AddDays(7 * WeeklyRowCount - 1);
        WeekRange.Text = $"{firstMonday:M月d日} – {lastDay:M月d日}";
        WeekTodayButton.Visibility = _weekOffset == 0 ? Visibility.Collapsed : Visibility.Visible;

        _cellsByDate.Clear();
        _selectedCellView = null;
        var rows = new List<WeekRowView>();
        var current = new List<WeekCellView>(7);
        var currentMonth = -1;
        var weekdayCursor = -1;
        for (var index = 0; index < 7 * WeeklyRowCount; index++)
        {
            var date = firstMonday.AddDays(index);
            if (date.Month != currentMonth)
            {
                if (current.Count > 0)
                {
                    PadRow(current, weekdayCursor);
                    rows.Add(new WeekRowView { Days = current });
                }
                current = new List<WeekCellView>(7);
                currentMonth = date.Month;
                weekdayCursor = 0;
                // The month's first row starts at its own weekday column.
                for (var lead = ((int)date.DayOfWeek + 6) % 7; lead > 0; lead--)
                {
                    current.Add(WeekCellView.Filler);
                    weekdayCursor++;
                }
            }

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
            current.Add(cell);
            weekdayCursor = (weekdayCursor + 1) % 7;
            if (weekdayCursor == 0)
            {
                rows.Add(new WeekRowView { Days = current });
                current = new List<WeekCellView>(7);
            }
        }
        if (current.Count > 0)
        {
            PadRow(current, weekdayCursor);
            rows.Add(new WeekRowView { Days = current });
        }

        WeekRows.DataContext = rows;
        WeekRows.ItemsSource = rows;
        RefreshSummary();
    }

    private static void PadRow(List<WeekCellView> row, int weekdayCursor)
    {
        while (row.Count < 7)
        {
            row.Insert(weekdayCursor, WeekCellView.Filler);
            weekdayCursor++;
        }
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

    // The cell's inline editor sits deep in the template (Border > Grid > StackPanel),
    // so it is found by its registered name rather than by tree depth.
    private static FrameworkElement? FindNamedDescendant(DependencyObject parent, string name)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element && element.Name == name)
            {
                return element;
            }
            if (FindNamedDescendant(child, name) is { } found)
            {
                return found;
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
