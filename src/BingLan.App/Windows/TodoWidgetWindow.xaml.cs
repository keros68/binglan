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
    // 周历窗口周数来自用户设置（1~5，默认 4），随 State 变化即时生效。
    private int WeeklyRowCount => Math.Clamp(State.WeeklyWeekCount, 1, 5);

    // 周数 ≥2 时窗口从上周一始（本周在第二行，历史行为）；1 周窗口必须
    // 含本周，否则整周看不到今天。
    private int WeekWindowBackShift => WeeklyRowCount >= 2 ? 1 : 0;

    private DateTime WeekWindowStart => MondayOf(DateTime.Today)
        .AddDays(7 * (_weekOffset - WeekWindowBackShift));

    // Minimum card sizes: the weekly grid needs wider columns and taller rows than
    // the today list, which stays comfortable at the card's original minimums.
    private const double ListMinimumWidth = 226;
    private const double WeeklyMinimumWidth = 520;
    private const double ListMinimumHeight = 166;
    // 4 周时与历史值 460 一致；随周数线性伸缩。
    private double WeeklyMinimumHeight => 220 + 60 * WeeklyRowCount;

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
    private TodoItemState? _pendingFocusItem;
    private string? _editingOriginalText;
    private System.Windows.Controls.TextBox? _editingTextBox;
    private TodoItemState? _lastMovedItem;
    private DateTime _lastMovedFrom;
    private int _lastMovedOrder;
    private DispatcherTimer? _moveToastTimer;
    private DispatcherTimer? _peekDwellTimer;
    private DispatcherTimer? _todayRefreshTimer;
    private DateTime _todayScopeDate = DateTime.Today;
    private System.Windows.Point _peekDwellAnchor;
    private System.Windows.Point _peekArmPoint;
    private WeekCellView? _peekDragCell;
    private TodoItemState? _peekDragItem;
    private System.Windows.Controls.ContentPresenter? _peekDragContainer;
    private System.Windows.Controls.Border? _peekDragRow;
    private System.Windows.Point _peekPressPoint;
    private int _peekFromIndex;
    private int _peekTargetIndex;
    private PeekDragPhase _peekDragPhase = PeekDragPhase.Idle;
    private double[]? _peekSlotTops;
    private double[]? _peekSlotHeights;
    private readonly HashSet<Guid> _completedItemIds = [];
    private System.Windows.Controls.ItemsControl? _peekDragList;
    private System.Windows.DependencyObject? _peekDwellSource;
    private System.Windows.Point _peekContainerClickPoint;
    private bool _peekEverDragged;

    private enum PeekDragPhase
    {
        Idle,
        HoverArmed,
        Dragging
    }

    public TodoWidgetWindow(TodoWidgetState state)
    {
        State = state;
        // Nothing is being edited at load, so blank leftovers from earlier sessions go.
        var blanksRemoved = TodoService.RemoveBlank(state).Count > 0;
        var legacyDatesChanged = TodoService.MigrateUndatedItems(state, DateTime.Today) > 0;
        var restoredOrderChanged = TodoService.NormalizeTodayCompletionOrder(state, DateTime.Today);
        _items = new ObservableCollection<TodoItemState>(state.Items.OrderBy(x => x.Order));
        SyncCompletedSnapshot();
        InitializeComponent();
        DataContext = state;
        ApplyAppearance(state.Appearance);
        SetContentFontScale(1d);
        SetContentFontFamily(null);
        TodoList.ItemsSource = _items;
        TodoList.ItemContainerGenerator.StatusChanged += (_, _) => ApplyCompletedVisibility();
        ApplyPlacement(state.Placement, state.IsLocked);
        WidgetCornerRadius = state.CornerRadius;
        ApplyViewMode();
        ScheduleTodayRefresh();
        if (blanksRemoved || legacyDatesChanged || restoredOrderChanged)
        {
            Loaded += (_, _) => NotifyStateChanged();
        }
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
        _dayEditor?.CommitPendingChanges();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (State.WeeklyView)
        {
            var targetCell = _selectedCellView is { IsFiller: false } selected
                ? selected
                : null;
            if (targetCell is null && !_cellsByDate.TryGetValue(DateTime.Today, out targetCell))
            {
                _weekOffset = 0;
                RenderWeekView();
                _cellsByDate.TryGetValue(DateTime.Today, out targetCell);
            }
            if (targetCell is not null)
            {
                SelectCell(targetCell);
                OpenDayPeek(targetCell, FindCellElement(targetCell));
                Dispatcher.BeginInvoke(() => WeekPeekInput.Focus(), DispatcherPriority.Input);
            }
            return;
        }

        CaptureState();
        var item = TodoService.Add(State, string.Empty, DateTime.Today);
        _items.Add(item);
        _pendingFocusItem = item;
        NormalizeTodayList();
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
            var wasCompleted = _completedItemIds.Contains(item.Id);
            if (wasCompleted != item.IsCompleted)
            {
                if (item.IsCompleted)
                {
                    _completedItemIds.Add(item.Id);
                }
                else
                {
                    _completedItemIds.Remove(item.Id);
                }

                _peekDwellTimer?.Stop();
                _peekDwellSource = null;
                if (_peekDragPhase != PeekDragPhase.Idle)
                {
                    CancelPeekDrag();
                }

                CaptureState();
                var orderChanged = TodoService.SinkCompleted(State, item.Id, DateTime.Today);
                orderChanged |= TodoService.NormalizeTodayCompletionOrder(State, DateTime.Today);
                if (orderChanged)
                {
                    SyncObservableOrder(_items, State.Items.OrderBy(x => x.Order).ToList());
                    // The list and calendar day contain the same item instances.
                    if (item.Date is { } movedDate
                        && _cellsByDate.TryGetValue(movedDate, out var movedCell))
                    {
                        SyncObservableOrder(
                            movedCell.Items,
                            State.Items.Where(x => x.Date == movedDate).OrderBy(x => x.Order).ToList());
                        movedCell.RefreshPreview();
                    }
                }
            }
            if (item.Date is { } itemDate && _cellsByDate.TryGetValue(itemDate, out var itemCell))
            {
                itemCell.RefreshPreview();
            }
        }
        ApplyCompletedVisibility();
        RefreshSummary();
        CaptureState();
        NotifyStateChanged();
    }

    private void TodoText_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _textBeforeEdit = (sender as System.Windows.Controls.TextBox)?.Text;
        // The new row has its caret now, so the caret alone decides from here.
        if ((sender as FrameworkElement)?.DataContext is TodoItemState item
            && ReferenceEquals(item, _pendingFocusItem))
        {
            _pendingFocusItem = null;
        }
    }

    // Keyboard focus also leaves when the card deactivates, so a blank row without a
    // caret is pruned even if the user clicks into another app.
    private void TodoText_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        RefreshSummary();
        CaptureState();
        NotifyStateChanged();
        SchedulePruneBlankItems();
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
            SchedulePruneBlankItems();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Deferred so the row losing focus is not torn down mid focus change; the
    /// caret owner is re-read when the prune runs.
    /// </summary>
    private void SchedulePruneBlankItems() =>
        Dispatcher.BeginInvoke(() => PruneBlankItems(), DispatcherPriority.Input);

    private void PruneBlankItems()
    {
        var typing = Keyboard.FocusedElement is System.Windows.Controls.TextBox { DataContext: TodoItemState focused }
            ? focused
            : null;
        CaptureState();
        // A row just created by + has not received its caret yet, but is about to.
        var removed = TodoService.RemoveBlank(
            State, x => ReferenceEquals(x, typing) || ReferenceEquals(x, _pendingFocusItem));
        if (removed.Count == 0)
        {
            return;
        }

        _peekDwellTimer?.Stop();
        _peekDwellSource = null;
        if (_peekDragPhase != PeekDragPhase.Idle)
        {
            CancelPeekDrag();
        }
        foreach (var item in removed)
        {
            if (ReferenceEquals(_editingItem, item))
            {
                _editingItem = null;
                _editingOriginalText = null;
                _editingTextBox = null;
            }
            if (ReferenceEquals(_lastMovedItem, item))
            {
                _lastMovedItem = null;
                HideMoveToast(this, EventArgs.Empty);
            }
            _items.Remove(item);
            if (item.Date is { } date && _cellsByDate.TryGetValue(date, out var cell))
            {
                cell.Items.Remove(item);
                cell.RefreshPreview();
            }
            _completedItemIds.Remove(item.Id);
        }
        RefreshSummary();
        CaptureState();
        NotifyStateChanged();
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
        _peekDwellTimer?.Stop();
        _peekDwellSource = null;
        if (_peekDragPhase != PeekDragPhase.Idle)
        {
            CancelPeekDrag();
        }
        SyncCompletedSnapshot();
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

        ReloadItems();
        if (movedDate is { } cellDate && _cellsByDate.TryGetValue(cellDate, out var movedCell))
        {
            ResortCell(movedCell);
        }
        NotifyStateChanged();
    }

    private void DeleteTodo_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TodoItemState item)
        {
            return;
        }
        if (ReferenceEquals(_editingItem, item))
        {
            _editingItem = null;
            _editingOriginalText = null;
            _editingTextBox = null;
        }
        if (ReferenceEquals(_lastMovedItem, item))
        {
            _lastMovedItem = null;
            HideMoveToast(this, EventArgs.Empty);
        }
        _items.Remove(item);
        if (item.Date is { } date && _cellsByDate.TryGetValue(date, out var cell))
        {
            cell.Items.Remove(item);
        }
        _peekDwellTimer?.Stop();
        _peekDwellSource = null;
        if (_peekDragPhase != PeekDragPhase.Idle)
        {
            CancelPeekDrag();
        }
        _completedItemIds.Remove(item.Id);
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

    // Base sizes of the card's reading text at 100%. The 字号缩放 setting scales them
    // together, so the list stays larger than the weekly cells by the same ratio.
    // Buttons and glyphs keep their sizes: they sit in fixed boxes. The cell text's
    // fixed line box scales with it, or wrapped lines would overlap.
    private static readonly (string Key, double Size)[] ScaledFontSizes =
    [
        ("TodoFont.ListItem", 14),
        ("TodoFont.CellItem", 12),
        ("TodoFont.CellLineHeight", 16),
        ("TodoFont.CellTwoLines", 32),
        ("TodoFont.Weekday", 11.5),
        ("TodoFont.DayNumber", 13.5),
        ("TodoFont.Lunar", 9.5),
        ("TodoFont.Overflow", 10.5),
        ("TodoFont.WeekRange", 12.5),
        ("TodoFont.PeekTitle", 13),
        ("TodoFont.PeekItem", 12),
        ("TodoFont.Status", 11.5)
    ];

    /// <summary>The todo component's 字号缩放 value, also handed to the day editor.</summary>
    public double ContentFontScale { get; private set; } = 1d;

    /// <summary>The todo component's 字体 choice (or the interface font), also handed to the day editor.</summary>
    public System.Windows.Media.FontFamily ContentFontFamily =>
        (System.Windows.Media.FontFamily)Resources["TodoContentFont"];

    /// <summary>
    /// Applies the 字体 setting to the card's reading text; null restores the interface
    /// font. The app-wide TextBox style pins its font, so the card's editors read this
    /// resource explicitly instead of inheriting it. The title keeps its own title font.
    /// </summary>
    public void SetContentFontFamily(string? fontName) =>
        Resources["TodoContentFont"] = fontName is { } name
            ? InstalledFontCatalog.Create(InstalledFontCatalog.Resolve(name))
            : (System.Windows.Media.FontFamily)FindResource("UiFont");

    public void SetContentFontScale(double scale)
    {
        ContentFontScale = DesktopExperienceRules.CoerceFontScale(scale);
        foreach (var (key, size) in ScaledFontSizes)
        {
            Resources[key] = size * ContentFontScale;
        }
    }

    /// <summary>设置窗口用：调整周历窗口的周数（1~5），立即重排并通知保存。</summary>
    public void SetWeeklyWeekCount(int weeks)
    {
        var clamped = Math.Clamp(weeks, 1, 5);
        if (State.WeeklyWeekCount == clamped)
        {
            return;
        }
        State.WeeklyWeekCount = clamped;
        ApplyViewMode();
        if (State.WeeklyView)
        {
            // 修改立即生效：缩小周数后卡片高度也要收回来（保留两行余量给
            // 跨月断行），放大时 ApplyViewMode 已负责抬到最小高度。
            Height = Math.Max(WeeklyMinimumHeight, Math.Min(Height, WeeklyMinimumHeight + 120));
        }
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
        // Handled up front: both dispatch paths (header bubble, body scroller
        // re-dispatch) want the event consumed on every branch, including the
        // early returns.
        e.Handled = true;
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

    // The body ScrollViewer marks left-button-down handled (it takes keyboard
    // focus on click, which also painted a focus rectangle inside the cell),
    // so the cell surface handler on the root Border never sees body clicks.
    // Re-dispatch from the ScrollViewer itself, seeing handled events. Row
    // checkbox/delete clicks are excluded defensively: whether they reach
    // this handler depends on WPF's re-raise guard for already-handled
    // MouseLeftButtonDown, and the exclusion keeps cell selection correct
    // under either reading of that mechanism. The weak table maps each cell
    // Border to the scroller it was hooked on; if a template pass ever
    // rebuilt the scroller under a living Border, the hook is refreshed.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject, object?> CellScrollHooked = [];

    private void WeekCellSurface_Preview(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Border border) return;
        if (FindVisualChild<System.Windows.Controls.ScrollViewer>(border) is not { } scroller)
        {
            return;
        }
        if (CellScrollHooked.TryGetValue(border, out var hooked) && ReferenceEquals(hooked, scroller))
        {
            return;
        }
        CellScrollHooked.Remove(border);
        CellScrollHooked.Add(border, scroller);
        scroller.AddHandler(MouseLeftButtonDownEvent,
            new MouseButtonEventHandler((_, e2) =>
            {
                if (e2.OriginalSource is DependencyObject source
                    && HitsInteractiveChild(source, scroller))
                {
                    return;
                }
                WeekCellSurface_Click(border, e2);
            }), handledEventsToo: true);
    }

    // Safe for ContentElement hits (Run/Hyperlink are not Visuals, so
    // VisualTreeHelper would throw on them). A text box being edited counts as
    // interactive: pressing and dragging in it selects text instead of moving the row.
    private static bool HitsInteractiveChild(DependencyObject source, DependencyObject container)
    {
        var current = source;
        while (current is not null && !ReferenceEquals(current, container))
        {
            if (current is System.Windows.Controls.CheckBox
                or System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.TextBox { IsKeyboardFocusWithin: true })
            {
                return true;
            }
            current = current is System.Windows.Media.Visual
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : System.Windows.LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    // ------------------------------------------------------------------
    // Dwell-lift drag reordering inside the day peek popup.
    // State machine: Idle → HoverArmed(cursor dwells over a row for 300 ms
    // with no button down; row lifts) → Dragging(left button pressed on the
    // armed row; follows the cursor, other rows shift live) → settle on
    // release. Moving the cursor past the drag threshold, leaving the
    // overlay, pressing a checkbox/× or a different row, and Esc all drop
    // the lift; nothing is reordered unless a drag actually moved the row.
    // Row identity is resolved through the item container generator: a hit
    // can land on a TextBoxView/TextBlock whose DataContext is the same todo
    // item, so no hit element may be assumed to be the row visual. Lift
    // visuals (background/border) live on the template-root Border; z-order
    // and drag transforms live on the generated ContentPresenter container.
    // The collection is only moved once, on release. All cancel paths funnel
    // through CancelPeekDrag().
    // ------------------------------------------------------------------
    private System.Windows.Controls.ItemsControl? ActiveDragList
    {
        get
        {
            if (State.WeeklyView)
            {
                return WeekPeekOverlay.Visibility == Visibility.Visible
                    && _peekCell is not null
                    && _editingItem is null
                    ? WeekPeekList
                    : null;
            }

            return ListHost.Visibility == Visibility.Visible && !IsTitleEditing
                ? TodoList
                : null;
        }
    }

    protected override void OnPreviewMouseRightButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        _peekDwellTimer?.Stop();
        _peekDwellSource = null;
        if (_peekDragPhase == PeekDragPhase.HoverArmed)
        {
            CancelPeekDrag();
        }
        base.OnPreviewMouseRightButtonDown(e);
    }

    protected override void OnPreviewMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        // Any press is a new intent: a pending dwell countdown must not fire
        // behind it (clicks on nav buttons, ✕, the input box, …).
        _peekDwellTimer?.Stop();
        _peekDwellSource = null;
        // A press anywhere on the card outside the day peek closes it, and the
        // press still reaches its target (another cell, a header button, …).
        // The toast stays usable for undo; context-menu presses come from their
        // own popup window and never count as outside.
        if (WeekPeekOverlay.Visibility == Visibility.Visible
            && _peekDragPhase != PeekDragPhase.Dragging
            && e.OriginalSource is System.Windows.Media.Visual pressed
            && ReferenceEquals(PresentationSource.FromVisual(pressed)?.RootVisual, this)
            && !WeekPeekPanel.IsAncestorOf(pressed)
            && !MoveToast.IsAncestorOf(pressed))
        {
            CloseDayPeek();
        }
        if (_peekDragPhase != PeekDragPhase.HoverArmed)
        {
            // Idle presses no longer arm anything — only a dwell does.
            return;
        }
        var dragList = _peekDragList;
        if (dragList is null || !ReferenceEquals(dragList, ActiveDragList))
        {
            CancelPeekDrag();
            return;
        }
        if (ReferenceEquals(dragList, WeekPeekList) && (_peekCell is null || _editingItem is not null))
        {
            CancelPeekDrag();
            return;
        }
        // Checkbox / × handle their own clicks: drop the lift, let them work.
        if (_peekDragContainer is not null
            && e.OriginalSource is System.Windows.DependencyObject originalSource
            && HitsInteractiveChild(originalSource, _peekDragContainer))
        {
            CancelPeekDrag();
            return;
        }
        // If the press is no longer over the armed row (the list scrolled or
        // re-laid out under a stationary cursor), drop the lift; a fresh dwell
        // can re-arm on the row now under the cursor.
        if (e.OriginalSource is not System.Windows.DependencyObject pressSource
            || FindRowContainer(pressSource, dragList) is not { } container
            || !ReferenceEquals(container, _peekDragContainer))
        {
            CancelPeekDrag();
            return;
        }
        // The armed item may have left the collection between the dwell tick
        // and this press (toast undo, external refresh): re-validate identity
        // before dragging.
        var validItem = _peekDragItem is TodoItemState armedItem
            && (ReferenceEquals(dragList, WeekPeekList)
                ? _peekCell is not null && _peekCell.Items.Contains(armedItem)
                : _items.Contains(armedItem) && TodoService.BelongsToTodayList(armedItem, DateTime.Today));
        if (!validItem)
        {
            CancelPeekDrag();
            return;
        }
        // Begin the drag. Capture before the row's TextBox sees the press (it
        // would steal the capture for text selection) and mark the press
        // handled so no child starts a selection or takes focus.
        if (!_peekDragContainer!.CaptureMouse())
        {
            CancelPeekDrag();
            return;
        }
        _peekDragContainer.LostMouseCapture -= PeekDragContainer_LostMouseCapture;
        _peekDragContainer.LostMouseCapture += PeekDragContainer_LostMouseCapture;
        _peekPressPoint = e.GetPosition(dragList);
        _peekContainerClickPoint = e.GetPosition(_peekDragContainer);
        _peekEverDragged = false;
        _peekDragPhase = PeekDragPhase.Dragging;
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        if (_peekDragPhase == PeekDragPhase.HoverArmed)
        {
            if (_peekDragList is null || !ReferenceEquals(_peekDragList, ActiveDragList))
            {
                CancelPeekDrag();
                return;
            }
            var pos = e.GetPosition(_peekDragList);
            if (Math.Abs(pos.X - _peekArmPoint.X) > SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(pos.Y - _peekArmPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                // The cursor moved on — the lift was a dwell artifact, drop it;
                // the same event seeds a fresh dwell so stopping right after
                // the move still re-arms per the dwell semantics.
                CancelPeekDrag();
                UpdatePeekDwell(e);
            }
        }
        else if (_peekDragPhase == PeekDragPhase.Dragging)
        {
            if (_peekDragList is null)
            {
                CancelPeekDrag();
                return;
            }
            DoPeekDragMove(e.GetPosition(_peekDragList));
            e.Handled = true;
        }
        else
        {
            UpdatePeekDwell(e);
        }
    }

    protected override void OnPreviewMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_peekDragPhase == PeekDragPhase.Dragging)
        {
            SettlePeekDrag();
        }
    }

    // The row lifts when the cursor dwells over it with no button down: every
    // move beyond the drag threshold restarts the countdown, and the tick
    // re-validates what is actually under the cursor before arming.
    private void UpdatePeekDwell(System.Windows.Input.MouseEventArgs e)
    {
        if (ActiveDragList is not { } dragList
            || Mouse.LeftButton == MouseButtonState.Pressed)
        {
            _peekDwellTimer?.Stop();
        _peekDwellSource = null;
            return;
        }
        if (e.OriginalSource is not System.Windows.DependencyObject source
            || FindRowContainer(source, dragList) is not { } container
            || container.Visibility != Visibility.Visible
            || HitsInteractiveChild(source, container))
        {
            _peekDwellTimer?.Stop();
        _peekDwellSource = null;
            return;
        }
        _peekDwellSource = source;
        var pos = e.GetPosition(dragList);
        if (_peekDwellTimer is { IsEnabled: true }
            && Math.Abs(pos.X - _peekDwellAnchor.X) <= SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _peekDwellAnchor.Y) <= SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }
        _peekDwellAnchor = pos;
        _peekDwellTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _peekDwellTimer.Tick -= PeekDwellTimer_Tick;
        _peekDwellTimer.Tick += PeekDwellTimer_Tick;
        _peekDwellTimer.Stop();
        _peekDwellTimer.Start();
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        // Any keystroke kills a pending dwell countdown; if a row is hover-armed
        // while the user starts typing, drop the lift without swallowing the key.
        _peekDwellTimer?.Stop();
        _peekDwellSource = null;
        if (e.Key != Key.Escape)
        {
            if (_peekDragPhase == PeekDragPhase.HoverArmed)
            {
                CancelPeekDrag();
            }
            return;
        }
        // Esc cancels an active drag first; otherwise closes DayPeek. A row that is
        // only hover-lifted drops the lift and lets Esc reach the text box being edited.
        if (_peekDragPhase == PeekDragPhase.Dragging)
        {
            CancelPeekDrag();
            e.Handled = true;
            return;
        }
        if (_peekDragPhase == PeekDragPhase.HoverArmed)
        {
            CancelPeekDrag();
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox)
            {
                return;
            }
            e.Handled = true;
            return;
        }
        if (_editingItem is null && WeekPeekOverlay.Visibility == Visibility.Visible)
        {
            CloseDayPeek();
            e.Handled = true;
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        // Losing activation kills the pending dwell; an active lift would
        // otherwise persist invisibly. A drag in progress is left to the
        // LostMouseCapture settle path.
        _peekDwellTimer?.Stop();
        _peekDwellSource = null;
        if (_peekDragPhase == PeekDragPhase.HoverArmed)
        {
            CancelPeekDrag();
        }
        // Clicking outside the card is also "somewhere else": close the day peek.
        if (WeekPeekOverlay.Visibility == Visibility.Visible && _peekDragPhase != PeekDragPhase.Dragging)
        {
            CloseDayPeek();
        }
        // No caret survives deactivation, so a new row that never got one goes too.
        _pendingFocusItem = null;
        SchedulePruneBlankItems();
        base.OnDeactivated(e);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel)
        {
            _dayEditor?.Close();
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (DateTime.Today != _todayScopeDate)
        {
            TodayRefreshTimer_Tick(this, EventArgs.Empty);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _peekDwellTimer?.Stop();
        _todayRefreshTimer?.Stop();
        _peekDwellSource = null;
        if (_peekDragPhase != PeekDragPhase.Idle)
        {
            CancelPeekDrag();
        }
        base.OnClosed(e);
    }

    private System.Windows.Controls.ContentPresenter? FindPeekRowContainer(DependencyObject source) =>
        FindRowContainer(source, WeekPeekList);

    private static System.Windows.Controls.ContentPresenter? FindRowContainer(
        DependencyObject? source,
        System.Windows.Controls.ItemsControl list)
    {
        var current = source;
        while (current is not null)
        {
            if (current is System.Windows.Controls.ContentPresenter element
                && list.ItemContainerGenerator.IndexFromContainer(element) >= 0)
            {
                return element;
            }
            if (ReferenceEquals(current, list))
            {
                return null;
            }
            current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : System.Windows.LogicalTreeHelper.GetParent(current);
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

    private void PeekDwellTimer_Tick(object? sender, EventArgs e)
    {
        _peekDwellTimer?.Stop();
        if (_peekDragPhase != PeekDragPhase.Idle
            || ActiveDragList is not { } dragList
            || Mouse.LeftButton == MouseButtonState.Pressed)
        {
            return;
        }
        // Re-validate at fire time: the cursor may have left the window or the
        // row may have moved under it since the countdown started.
        var hovered = _peekDwellSource ?? (Mouse.DirectlyOver as System.Windows.DependencyObject)
            ?? _peekDwellSource;
        if (hovered is null
            || FindRowContainer(hovered, dragList) is not { } container
            || container.Visibility != Visibility.Visible
            || HitsInteractiveChild(hovered, container)
            || container.DataContext is not TodoItemState item)
        {
            return;
        }
        if (ReferenceEquals(dragList, WeekPeekList))
        {
            if (_peekCell is null || !_peekCell.Items.Contains(item))
            {
                return;
            }
            _peekDragCell = _peekCell;
        }
        else
        {
            if (!_items.Contains(item) || !TodoService.BelongsToTodayList(item, DateTime.Today))
            {
                return;
            }
            _peekDragCell = null;
        }
        _peekDragList = dragList;
        _peekDragItem = item;
        _peekDragContainer = container;
        _peekDragRow = PeekRowVisual(container);
        _peekArmPoint = _peekDwellAnchor;
        _peekPressPoint = _peekArmPoint;
        ArmPeekDrag();
    }

    private void ArmPeekDrag()
    {
        if (_peekDragContainer is null || _peekDragList is null || _peekDragItem is null)
        {
            return;
        }
        _peekDragPhase = PeekDragPhase.HoverArmed;
        // Target matching must read untransformed slot geometry: the dragged
        // row follows the cursor 1:1, so any hit test over live (transformed)
        // positions would keep hitting the dragged row's own band. Snapshot
        // the pristine layout before the first shift is applied.
        var count = _peekDragList.Items.Count;
        _peekSlotTops = new double[count];
        _peekSlotHeights = new double[count];
        for (var index = 0; index < count; index++)
        {
            if (_peekDragList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container
                && container.Visibility == Visibility.Visible)
            {
                _peekSlotTops[index] = container.TranslatePoint(new System.Windows.Point(0, 0), _peekDragList).Y;
                _peekSlotHeights[index] = container.ActualHeight;
            }
        }
        // Z-order and drag transforms live on the generated container (a direct
        // child of the list panel); the lift visuals live on the template Border.
        System.Windows.Controls.Panel.SetZIndex(_peekDragContainer, 100);
        _peekDragContainer.RenderTransform = new System.Windows.Media.TranslateTransform(
            0, ReferenceEquals(_peekDragList, TodoList) ? -1.5 : 0);
        if (_peekDragRow is not null)
        {
            if (ReferenceEquals(_peekDragList, WeekPeekList))
            {
                _peekDragRow.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
            }
            else
            {
                _peekDragRow.Padding = new System.Windows.Thickness(6.5);
            }
            _peekDragRow.BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0xFF, 0x7F, 0xA8, 0xD9));
            _peekDragRow.BorderThickness = new System.Windows.Thickness(1.5);
            _peekDragRow.Cursor = System.Windows.Input.Cursors.SizeAll;
            _peekDragRow.ForceCursor = true;
        }
        _peekFromIndex = _peekDragCell is not null
            ? _peekDragCell.Items.IndexOf(_peekDragItem)
            : _items.IndexOf(_peekDragItem);
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
        var deltaY = posInList.Y - _peekPressPoint.Y;
        if (Math.Abs(deltaY) > SystemParameters.MinimumVerticalDragDistance)
        {
            _peekEverDragged = true;
        }
        if (_peekDragContainer?.RenderTransform is System.Windows.Media.TranslateTransform translate)
        {
            translate.Y = deltaY;
        }
        var target = PeekTargetIndexForDragOffset();
        if (target >= 0 && target != _peekTargetIndex)
        {
            _peekTargetIndex = target;
            ApplyPeekShifts();
        }
    }

    // Nearest-slot-center match against the pristine snapshot, with the
    // dragged row's own slot as the baseline candidate: the swap fires only
    // when the dragged row's visual center crosses the midpoint towards a
    // neighbour. Staying at the origin therefore never reorders (zero
    // side-effect on release), the decision is monotonic in the follow
    // offset (no transform feedback), and jumping over several rows in one
    // move lands on the nearest crossed slot.
    private int PeekTargetIndexForDragOffset()
    {
        if (_peekSlotTops is null || _peekSlotHeights is null
            || _peekFromIndex < 0
            || _peekFromIndex >= _peekSlotTops.Length
            || _peekDragContainer?.RenderTransform is not System.Windows.Media.TranslateTransform translate)
        {
            return -1;
        }
        var draggedCenter = _peekSlotTops[_peekFromIndex] + translate.Y + (_peekSlotHeights[_peekFromIndex] / 2);
        var best = _peekFromIndex;
        var bestDistance = Math.Abs(draggedCenter - (_peekSlotTops[_peekFromIndex] + (_peekSlotHeights[_peekFromIndex] / 2)));
        for (var index = 0; index < _peekSlotTops.Length; index++)
        {
            if (index == _peekFromIndex || _peekSlotHeights[index] <= 0)
            {
                continue;
            }
            var distance = Math.Abs(draggedCenter - (_peekSlotTops[index] + (_peekSlotHeights[index] / 2)));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = index;
            }
        }
        return best;
    }

    private void ApplyPeekShifts()
    {
        if (_peekDragContainer is null || _peekDragList is null)
        {
            return;
        }
        // Index-range shift, recomputed from scratch each call (idempotent):
        // rows between the origin and the current target move over by one row
        // height. Never read container positions here — TranslatePoint would
        // include the transforms applied by the previous call and oscillate.
        // Note: In TodoList, the ContentPresenter container wraps the child Border's
        // Margin="2,2,6,3", so _peekSlotHeights[_peekFromIndex] / ActualHeight already
        // includes the 5px vertical row spacing.
        var baseHeight = _peekSlotHeights is not null
            && _peekFromIndex >= 0
            && _peekFromIndex < _peekSlotHeights.Length
            && _peekSlotHeights[_peekFromIndex] > 0
            ? _peekSlotHeights[_peekFromIndex]
            : _peekDragContainer.ActualHeight;
        var rowHeight = ReferenceEquals(_peekDragList, WeekPeekList)
            ? _peekDragContainer.ActualHeight + 4
            : baseHeight;
        for (var index = 0; index < _peekDragList.Items.Count; index++)
        {
            if (_peekDragList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container
                || container.Visibility != Visibility.Visible
                || container.ActualHeight <= 0)
            {
                continue;
            }
            double shift;
            if (index == _peekFromIndex)
            {
                // The dragged row's transform carries the cursor-follow offset —
                // never touch it here.
                continue;
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
        var dragList = _peekDragList;
        var dragCell = _peekDragCell;
        var dragItem = _peekDragItem;
        var dragContainer = _peekDragContainer;
        var fromIndex = _peekFromIndex;
        var targetIndex = _peekTargetIndex;
        var everDragged = _peekEverDragged;
        var clickPointInContainer = _peekContainerClickPoint;

        ResetPeekDragVisual();
        if (dragContainer is not null)
        {
            dragContainer.LostMouseCapture -= PeekDragContainer_LostMouseCapture;
            dragContainer.ReleaseMouseCapture();
        }
        ClearPeekDragFields();

        if (dragList is null || dragItem is null)
        {
            return;
        }

        if (ReferenceEquals(dragList, WeekPeekList) && dragCell is not null)
        {
            var finalIndex = Math.Clamp(targetIndex, 0, Math.Max(0, dragCell.Items.Count - 1));
            var current = dragCell.Items.IndexOf(dragItem);
            if (finalIndex != fromIndex && current >= 0 && current != finalIndex)
            {
                dragCell.Items.Move(current, finalIndex);
                var nonCellItems = State.Items
                    .Where(x => x.Date != dragCell.Date)
                    .OrderBy(x => x.Order)
                    .ToList();
                nonCellItems.AddRange(dragCell.Items);
                for (var i = 0; i < nonCellItems.Count; i++)
                {
                    nonCellItems[i].Order = i;
                }
                State.Items = nonCellItems;
                ReloadItems();
                CaptureState();
                NotifyStateChanged();
            }
            return;
        }

        if (ReferenceEquals(dragList, TodoList))
        {
            var finalIndex = Math.Clamp(targetIndex, 0, Math.Max(0, _items.Count - 1));
            var current = _items.IndexOf(dragItem);
            if (finalIndex != fromIndex && current >= 0 && current != finalIndex)
            {
                CaptureState();
                _items.Move(current, finalIndex);
                for (var i = 0; i < _items.Count; i++)
                {
                    _items[i].Order = i;
                }
                State.Items = _items.ToList();
                if (dragItem.Date is { } itemDate && _cellsByDate.TryGetValue(itemDate, out var cell))
                {
                    ResortCell(cell);
                }
                CaptureState();
                NotifyStateChanged();
            }
            else if (!everDragged && dragContainer is not null)
            {
                if (FindVisualChild<System.Windows.Controls.TextBox>(dragContainer) is { } editor)
                {
                    editor.Focus();
                    var pointInEditor = dragContainer.TranslatePoint(clickPointInContainer, editor);
                    var caretIndex = editor.GetCharacterIndexFromPoint(pointInEditor, snapToText: true);
                    if (caretIndex >= 0)
                    {
                        editor.CaretIndex = caretIndex;
                    }
                }
            }
        }
    }

    private void CancelPeekDrag()
    {
        // Idle before releasing so the LostMouseCapture handler stays quiet.
        _peekDwellTimer?.Stop();
        _peekDwellSource = null;
        _peekDragPhase = PeekDragPhase.Idle;
        ResetPeekDragVisual();
        if (_peekDragContainer is not null)
        {
            _peekDragContainer.LostMouseCapture -= PeekDragContainer_LostMouseCapture;
            _peekDragContainer.ReleaseMouseCapture();
        }
        ClearPeekDragFields();
    }

    private void ClearPeekDragFields()
    {
        _peekDragPhase = PeekDragPhase.Idle;
        _peekDragList = null;
        _peekDragCell = null;
        _peekDragItem = null;
        _peekDragContainer = null;
        _peekDragRow = null;
        _peekSlotTops = null;
        _peekSlotHeights = null;
        _peekEverDragged = false;
    }

    private void ResetPeekDragVisual()
    {
        foreach (var list in new[] { WeekPeekList, TodoList })
        {
            if (list is null)
            {
                continue;
            }
            for (var index = 0; index < list.Items.Count; index++)
            {
                if (list.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
                {
                    container.RenderTransform = System.Windows.Media.Transform.Identity;
                    System.Windows.Controls.Panel.SetZIndex(container, 0);
                }
            }
        }
        if (_peekDragRow is not null)
        {
            if (ReferenceEquals(_peekDragList, WeekPeekList))
            {
                _peekDragRow.Background = System.Windows.Media.Brushes.Transparent;
                _peekDragRow.BorderBrush = System.Windows.Media.Brushes.Transparent;
                _peekDragRow.BorderThickness = new System.Windows.Thickness(0);
                _peekDragRow.Cursor = null;
            }
            else
            {
                _peekDragRow.ClearValue(System.Windows.Controls.Border.BackgroundProperty);
                _peekDragRow.ClearValue(System.Windows.Controls.Border.BorderBrushProperty);
                _peekDragRow.ClearValue(System.Windows.Controls.Border.BorderThicknessProperty);
                _peekDragRow.ClearValue(System.Windows.Controls.Border.PaddingProperty);
                _peekDragRow.ClearValue(FrameworkElement.CursorProperty);
            }
            _peekDragRow.ClearValue(FrameworkElement.ForceCursorProperty);
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
        // Re-opening (day navigation included) regenerates the row containers:
        // any armed lift, pending dwell, or inline edit is stale by definition.
        _peekDwellTimer?.Stop();
        _peekDwellSource = null;
        if (_peekDragPhase != PeekDragPhase.Idle)
        {
            CancelPeekDrag();
        }
        if (_editingItem is not null)
        {
            EndPeekTextEdit(_editingTextBox, revert: false);
        }
        _peekCell = cell;
        WeekPeekTitle.Text = cell.Date.ToString("M月d日") + " · " +
            WeekdayName(cell.Date);
        WeekPeekList.ItemsSource = cell.Items;
        WeekPeekInput.Clear();
        MoveToast.Visibility = Visibility.Collapsed;
        WeekPeekOverlay.Visibility = Visibility.Visible;
        WeekPeekPanel.UpdateLayout();
        UpdateDayPeekBounds(cellElement);
    }

    private void DayPeek_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateDayPeekBounds();

    private void UpdateDayPeekBounds(FrameworkElement? cellElement = null)
    {
        if (_peekCell is null || WeekPeekOverlay.Visibility != Visibility.Visible
            || WeekHost.ActualWidth <= 0 || WeekHost.ActualHeight <= 0)
        {
            return;
        }
        // Bound the whole panel so its header and input retain space while the list scrolls.
        WeekPeekPanel.MaxHeight = WeekHost.ActualHeight;
        cellElement ??= FindCellElement(_peekCell);
        if (cellElement is not null && WeekHost.IsAncestorOf(cellElement))
        {
            var panelWidth = WeekPeekPanel.ActualWidth > 0 ? WeekPeekPanel.ActualWidth : WeekPeekPanel.Width;
            var panelHeight = WeekPeekPanel.ActualHeight > 0 ? WeekPeekPanel.ActualHeight : 140;
            var rect = cellElement.TransformToAncestor(WeekHost)
                .TransformBounds(new Rect(0, 0, cellElement.ActualWidth, cellElement.ActualHeight));
            var left = rect.Right + 6;
            if (left + panelWidth > WeekHost.ActualWidth)
            {
                left = rect.Left - panelWidth - 6;
            }
            left = Math.Clamp(left, 0, Math.Max(0, WeekHost.ActualWidth - panelWidth));
            var top = Math.Clamp(rect.Top, 0, Math.Max(0, WeekHost.ActualHeight - panelHeight));
            Canvas.SetLeft(WeekPeekPanel, left);
            Canvas.SetTop(WeekPeekPanel, top);
        }
    }

    private void CloseDayPeek()
    {
        _peekDwellTimer?.Stop();
        _peekDwellSource = null;
        if (_peekDragPhase != PeekDragPhase.Idle)
        {
            CancelPeekDrag();
        }
        if (_editingItem is not null)
        {
            EndPeekTextEdit(_editingTextBox, revert: false);
        }
        WeekPeekOverlay.Visibility = Visibility.Collapsed;
        WeekPeekList.ItemsSource = null;
        _peekCell = null;
    }

    private void WeekPeekOverlay_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // With mouse capture active (Dragging) WPF suppresses leave events for
        // other elements, so this only fires while merely hovering — drop the
        // dwell lift when the cursor exits the popup.
        if (_peekDragPhase == PeekDragPhase.HoverArmed)
        {
            CancelPeekDrag();
        }
        _peekDwellTimer?.Stop();
        _peekDwellSource = null;
    }

    private void WeekPeekOverlay_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        // Scrolling moves the rows under a stationary cursor: the dwell anchor
        // and the lift's slot snapshot both go stale. Drop the lift and let
        // the scroll proceed. During a drag the snapshot must not go stale at
        // all, so scrolling is suppressed until release.
        if (_peekDragPhase == PeekDragPhase.HoverArmed)
        {
            CancelPeekDrag();
        }
        else if (_peekDragPhase == PeekDragPhase.Dragging)
        {
            e.Handled = true;
        }
        else
        {
            // Idle with a pending dwell: scrolling moved the rows under a
            // stationary cursor, so the running countdown no longer applies
            // to the row now under it — kill it; the next mouse move re-seeds
            // a fresh dwell for that row.
            _peekDwellTimer?.Stop();
        _peekDwellSource = null;
        }
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
            SelectCell(adjacent);
            OpenDayPeek(adjacent, FindCellElement(adjacent));
        }
    }

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

    private void MoveItemToDate(TodoItemState item, DateTime target, bool isUndo = false, int? restoreOrder = null)
    {
        if (item.Date is not { } from)
        {
            return;
        }
        var previousDayIndex = Math.Max(0, State.Items
            .Where(x => x.Date == from)
            .OrderBy(x => x.Order)
            .ToList()
            .IndexOf(item));
        if (!TodoService.MoveToDate(State, item.Id, target, isUndo ? restoreOrder : null))
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
        ReloadItems();
        CaptureState();
        NotifyStateChanged();
        if (!isUndo)
        {
            ShowMoveToast($"已移到 {target:M月d日}（{WeekdayName(target)}）", item, from, previousDayIndex);
        }
    }

    private void ShowMoveToast(string message, TodoItemState item, DateTime from, int previousOrder)
    {
        _lastMovedItem = item;
        _lastMovedFrom = from;
        _lastMovedOrder = previousOrder;
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
            var from = _lastMovedFrom;
            var order = _lastMovedOrder;
            _lastMovedItem = null;
            HideMoveToast(this, EventArgs.Empty);
            MoveItemToDate(item, from, isUndo: true, restoreOrder: order);
        }
    }

    private void PeekText_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Double-click on a peek row's text unlocks in-place editing for that entry.
        if (e.ClickCount == 2
            && sender is System.Windows.Controls.TextBox { IsReadOnly: true, DataContext: TodoItemState item } editor)
        {
            if (_editingItem is not null && !ReferenceEquals(_editingItem, item))
            {
                EndPeekTextEdit(_editingTextBox, revert: false);
            }
            _editingItem = item;
            _editingOriginalText = item.Text;
            _editingTextBox = editor;
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
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EndPeekTextEdit(sender, revert: true);
            Keyboard.ClearFocus();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            EndPeekTextEdit(sender, revert: false);
            Keyboard.ClearFocus();
        }
    }

    private void PeekText_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_editingTextBox is not null && !ReferenceEquals(sender, _editingTextBox))
        {
            return;
        }
        EndPeekTextEdit(sender, revert: false);
    }

    private void EndPeekTextEdit(object? sender, bool revert = false)
    {
        if (sender is System.Windows.Controls.TextBox senderBox
            && _editingTextBox is not null
            && !ReferenceEquals(senderBox, _editingTextBox))
        {
            senderBox.IsReadOnly = true;
            senderBox.Focusable = false;
            senderBox.Cursor = System.Windows.Input.Cursors.Arrow;
            return;
        }

        var targetEditor = sender as System.Windows.Controls.TextBox ?? _editingTextBox;
        var item = _editingItem;
        var originalText = _editingOriginalText;
        _editingItem = null;
        _editingOriginalText = null;
        _editingTextBox = null;

        if (revert && item is not null && originalText is not null)
        {
            item.Text = originalText;
            if (targetEditor is not null)
            {
                targetEditor.Text = originalText;
            }
        }

        if (targetEditor is not null)
        {
            targetEditor.IsReadOnly = true;
            targetEditor.Focusable = false;
            targetEditor.Cursor = System.Windows.Input.Cursors.Arrow;
        }

        if (!revert && item is not null && item.Text != originalText)
        {
            RefreshSummary();
            CaptureState();
            NotifyStateChanged();
        }
        SchedulePruneBlankItems();
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
        // A right-click menu is a different intent: kill any pending dwell
        // countdown and drop an active lift.
        _peekDwellTimer?.Stop();
        _peekDwellSource = null;
        if (_peekDragPhase == PeekDragPhase.HoverArmed)
        {
            CancelPeekDrag();
        }
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
        _dayEditor = new DayEditWindow(cell.Date, lines, ContentFontScale, ContentFontFamily) { Owner = this };
        _dayEditor.Committed += (date, committedLines) => CommitDayEditor(date, committedLines);
        _dayEditor.Closed += (_, _) => _dayEditor = null;
        _dayEditor.ShowDialog();
    }

    private void CommitDayEditor(DateTime date, IReadOnlyList<string> lines)
    {
        CaptureState();
        TodoService.SyncDayItems(State, date, lines);
        if (date == DateTime.Today)
        {
            TodoService.NormalizeTodayCompletionOrder(State, DateTime.Today);
        }
        ReloadItems();
        if (_cellsByDate.TryGetValue(date, out var cell))
        {
            ResortCell(cell);
        }
        RefreshSummary();
        NotifyStateChanged();
    }

    private void AddWeeklyItem(WeekCellView cell, string text)
    {
        CaptureState();
        var item = TodoService.Add(State, text, cell.Date);
        _items.Add(item);
        cell.Items.Add(item);
        if (cell.Date == DateTime.Today)
        {
            NormalizeTodayList();
        }
        RefreshSummary();
        NotifyStateChanged();
    }

    private void ApplyViewMode()
    {
        _peekDwellTimer?.Stop();
        _peekDwellSource = null;
        if (_peekDragPhase != PeekDragPhase.Idle)
        {
            CancelPeekDrag();
        }
        SchedulePruneBlankItems();
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
            CloseDayPeek();
            if (NormalizeTodayList())
            {
                NotifyStateChanged();
            }
            RefreshSummary();
        }
    }

    private void RenderWeekView()
    {
        // The window still slides one week per page, but rows are month-aligned:
        // when a month ends mid-week the row ends there and the next month starts
        // on a fresh row at its own weekday column, so months read at a glance.
        var firstMonday = WeekWindowStart;
        var lastDay = firstMonday.AddDays(7 * WeeklyRowCount - 1);
        WeekRange.Text = $"{firstMonday:M月d日} – {lastDay:M月d日}";
        WeekTodayButton.Visibility = _weekOffset == 0 ? Visibility.Collapsed : Visibility.Visible;

        var previousSelectedDate = _selectedCellView?.Date;
        var previousPeekDate = _peekCell?.Date;

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
                IsWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                HideCompleted = State.HideCompleted
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

        if (previousSelectedDate is { } selDate && _cellsByDate.TryGetValue(selDate, out var restoredSelected))
        {
            SelectCell(restoredSelected);
        }
        if (WeekPeekOverlay.Visibility == Visibility.Visible)
        {
            if (previousPeekDate is { } peekDate && _cellsByDate.TryGetValue(peekDate, out var restoredPeek))
            {
                if (_editingItem is not null)
                {
                    EndPeekTextEdit(_editingTextBox, revert: false);
                }
                _peekCell = restoredPeek;
                WeekPeekList.ItemsSource = restoredPeek.Items;
                Dispatcher.BeginInvoke(() =>
                {
                    if (_peekCell == restoredPeek && WeekPeekOverlay.Visibility == Visibility.Visible)
                    {
                        OpenDayPeek(restoredPeek, FindCellElement(restoredPeek));
                    }
                }, DispatcherPriority.Loaded);
            }
            else
            {
                CloseDayPeek();
            }
        }

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

    private void SyncCompletedSnapshot()
    {
        _completedItemIds.Clear();
        foreach (var entry in State.Items)
        {
            if (entry.IsCompleted)
            {
                _completedItemIds.Add(entry.Id);
            }
        }
    }

    private bool NormalizeTodayList()
    {
        CaptureState();
        var orderChanged = TodoService.NormalizeTodayCompletionOrder(State, DateTime.Today);
        if (orderChanged)
        {
            SyncObservableOrder(_items, State.Items.OrderBy(x => x.Order).ToList());
            if (_cellsByDate.TryGetValue(DateTime.Today, out var todayCell))
            {
                SyncObservableOrder(
                    todayCell.Items,
                    State.Items.Where(x => x.Date == DateTime.Today).OrderBy(x => x.Order).ToList());
                todayCell.RefreshPreview();
            }
        }
        ApplyCompletedVisibility();
        return orderChanged;
    }

    private void ScheduleTodayRefresh()
    {
        _todayRefreshTimer ??= new DispatcherTimer();
        _todayRefreshTimer.Tick -= TodayRefreshTimer_Tick;
        _todayRefreshTimer.Tick += TodayRefreshTimer_Tick;
        _todayRefreshTimer.Interval = DateTime.Today.AddDays(1).AddSeconds(1) - DateTime.Now;
        _todayRefreshTimer.Start();
    }

    private void TodayRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _todayRefreshTimer?.Stop();
        var today = DateTime.Today;
        if (today != _todayScopeDate)
        {
            _todayScopeDate = today;
            SchedulePruneBlankItems();
            var changed = NormalizeTodayList();
            if (State.WeeklyView)
            {
                RenderWeekView();
            }
            ApplyCompletedVisibility();
            RefreshSummary();
            if (changed)
            {
                NotifyStateChanged();
            }
        }
        ScheduleTodayRefresh();
    }

    private static void SyncObservableOrder(
        ObservableCollection<TodoItemState> collection,
        IReadOnlyList<TodoItemState> desiredOrder)
    {
        // A task added from the list can enter an already-rendered calendar cell.
        // Synchronize membership first; Move then preserves the existing row
        // containers and automation peers for tasks that were already present.
        var desiredItems = desiredOrder.ToHashSet();
        for (var index = collection.Count - 1; index >= 0; index--)
        {
            if (!desiredItems.Contains(collection[index]))
            {
                collection.RemoveAt(index);
            }
        }
        for (var targetIndex = 0; targetIndex < desiredOrder.Count; targetIndex++)
        {
            var currentIndex = collection.IndexOf(desiredOrder[targetIndex]);
            if (currentIndex < 0)
            {
                collection.Insert(targetIndex, desiredOrder[targetIndex]);
            }
            else if (currentIndex != targetIndex)
            {
                collection.Move(currentIndex, targetIndex);
            }
        }
    }

    private void ReloadItems()
    {
        _items.Clear();
        SyncCompletedSnapshot();
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

    // A blank row only exists while it is being typed into (blanks are pruned once
    // the caret leaves), and it is not a task, so it is left out of the count.
    private void RefreshSummary()
    {
        CompletedVisibilityButton.Content = State.HideCompleted ? "显示已完成" : "隐藏已完成";

        if (State.WeeklyView)
        {
            var firstMonday = WeekWindowStart;
            var lastDay = firstMonday.AddDays(7 * WeeklyRowCount - 1);
            var written = _items
                .Where(x => !string.IsNullOrWhiteSpace(x.Text)
                    && x.Date is { } date && date >= firstMonday && date <= lastDay)
                .ToList();
            var completed = written.Count(x => x.IsCompleted);
            SummaryText.Text = written.Count == 0 ? "这段时间还没有记录" : $"{completed}/{written.Count} 已完成";
            return;
        }

        var today = DateTime.Today;
        var listed = _items
            .Where(x => !string.IsNullOrWhiteSpace(x.Text)
                && TodoService.BelongsToTodayList(x, today))
            .ToList();
        var done = listed.Count(x => x.IsCompleted);
        SummaryText.Text = listed.Count == 0 ? "写下第一条待办" : $"{done}/{listed.Count} 已完成";
        ClearCompletedButton.IsEnabled = _items.Any(x => x.IsCompleted && TodoService.BelongsToTodayList(x, today));
    }

    private static DateTime MondayOf(DateTime date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private FrameworkElement? FindCellElement(WeekCellView cell) =>
        FindVisualDescendantWhere<System.Windows.Controls.Border>(
            WeekRows, border => ReferenceEquals(border.DataContext, cell));

    private static T? FindVisualDescendantWhere<T>(DependencyObject parent, Func<T, bool> predicate) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match && predicate(match))
            {
                return match;
            }
            if (FindVisualDescendantWhere(child, predicate) is { } descendant)
            {
                return descendant;
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
