using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.Core.Models;
using BingLan.Core.Services;
using Button = System.Windows.Controls.Button;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfOpenFolderDialog = Microsoft.Win32.OpenFolderDialog;

namespace BingLan.App.Windows;

public partial class FileBoxWindow : WidgetWindowBase
{
    private const string MyComputerEntry = "shell:MyComputerFolder";
    private const string RecycleBinEntry = "shell:RecycleBinFolder";
    private static readonly SemaphoreSlim ProbeLimiter = new(2, 2);

    private readonly ObservableCollection<FileTile> _items;
    private readonly HashSet<Button> _singleClickFileTiles = [];
    // The selection lives on mapping ids so it survives tiles being replaced (icon refresh)
    // or the list being rebuilt; the anchor remembers where a Shift range starts.
    private readonly HashSet<Guid> _selectedIds = [];
    private readonly CancellationTokenSource _closeCts = new();
    private readonly Dictionary<Guid, long> _pendingSorts = [];
    private long _sortRevision;
    private readonly CardFoldAnimator _foldAnimator;
    private readonly System.Windows.Threading.DispatcherTimer _autoCollapseTimer = new()
    {
        Interval = FileBoxAutoCollapseRules.IdleDelay
    };
    private readonly System.Windows.Threading.DispatcherTimer _dragExpandTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(220)
    };
    private readonly System.Windows.Threading.DispatcherTimer _hoverExpandTimer = new()
    {
        Interval = FileBoxAutoCollapseRules.HoverExpandDelay
    };
    private Guid? _selectionAnchor;
    private bool _initialIconLoadStarted;
    private int _modalDialogDepth;
    private bool _isTileOrFileDragActive;
    private bool _externalDragOver;
    private bool _expandedForExternalDrag;
    private bool _pinnedOpenForSession;
    private bool _suppressHoverExpandUntilLeave;
    private bool _expandedHeightTemporarilyClamped;
    private (int X, int Y)? _foldedTitlePressPoint;

    internal event Action? DragOutStarted;
    internal event Action? DragOutEnded;
    internal event Action? ItemOpened;
    internal event Action? PeekEscapeRequested;

    public FileBoxWindow(FileBoxState state)
        : this([state])
    {
    }

    public FileBoxWindow(IReadOnlyList<FileBoxState> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count == 0)
        {
            throw new ArgumentException("A file box window requires at least one box state.", nameof(members));
        }

        _tabs = members.OrderBy(m => m.TabOrder).ToList();
        var state = _tabs.FirstOrDefault(m => m.IsActiveTab) ?? _tabs[0];
        State = state;
        ConfirmDeleteOriginal = AskDeleteOriginal;
        ConfirmDeleteOriginals = AskDeleteOriginals;
        _items = new ObservableCollection<FileTile>(
            state.Items.OrderBy(x => x.Order).Select(x => new FileTile(x, null)));
        InitializeComponent();
        _foldAnimator = new CardFoldAnimator(this, BodySurface);
        DataContext = state;
        ApplyAppearance(state.Appearance);
        FileList.ItemsSource = _items;
        ApplyViewMode();
        ApplyPlacement(state.Placement, state.IsLocked);
        WidgetCornerRadius = state.CornerRadius;
        InitializeGroups();
        RefreshCount();
        if (state.IsCollapsed)
        {
            SetCollapsed(true);
        }
        RefreshPinOpenButton();
        InitializeHoverReveal();
        _autoCollapseTimer.Tick += (_, _) => OnAutoCollapseTimerTick();
        _dragExpandTimer.Tick += (_, _) => OnDragExpandTimerTick();
        _hoverExpandTimer.Tick += (_, _) => OnHoverExpandTimerTick();
        HookAutoCollapseContextMenus();
        MouseEnter += (_, _) =>
        {
            RefreshAutoCollapseTimer();
            RefreshHoverExpandTimer();
            RefreshHoverRevealState();
        };
        MouseLeave += (_, _) =>
        {
            _suppressHoverExpandUntilLeave = false;
            RefreshAutoCollapseTimer();
            RefreshHoverExpandTimer();
            RefreshHoverRevealState();
        };
        DragEnter += (_, e) =>
        {
            _externalDragOver = true;
            _hoverExpandTimer.Stop();
            if (IsEdgeDocked && !IsEdgeDockExpanded && e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                EdgeDockFileDragNear?.Invoke(this);
            }
            else if (State.IsCollapsed && e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                _dragExpandTimer.Stop();
                _dragExpandTimer.Start();
            }
            RefreshAutoCollapseTimer();
            RefreshHoverRevealState();
        };
        DragLeave += (_, _) =>
        {
            _externalDragOver = false;
            _dragExpandTimer.Stop();
            if (_expandedForExternalDrag && !IsCursorInsideWindow())
            {
                _expandedForExternalDrag = false;
                SetCollapsed(true, animate: true);
            }
            RefreshAutoCollapseTimer();
            RefreshHoverExpandTimer();
            RefreshHoverRevealState();
        };
        Loaded += FileBoxWindow_Loaded;
        PreviewKeyDown += FileBoxWindow_PreviewKeyDown;
        PreviewMouseLeftButtonDown += FileBoxWindow_BackgroundPointerDown;
        PreviewMouseLeftButtonDown += FileBoxWindow_TitleBarPointerDown;
        PreviewMouseLeftButtonUp += FileBoxWindow_TitleBarPointerUp;
        PreviewMouseRightButtonDown += (_, _) =>
        {
            _hoverExpandTimer.Stop();
            if (State.IsCollapsed)
            {
                _suppressHoverExpandUntilLeave = true;
            }
        };
    }

    public FileBoxState State { get; private set; }

    internal bool IsFoldAnimating => _foldAnimator.IsAnimating;

    internal bool InitialProbeCompleted { get; private set; }

    internal bool IsAutoCollapseTimerRunning => _autoCollapseTimer.IsEnabled;

    internal bool IsHoverExpandTimerRunning => _hoverExpandTimer.IsEnabled;

    internal bool IsEdgeDocked => State.EdgeDockSide is not null;

    private bool _edgeDockExpanded;

    // The saved docking preference stays checked while a shared interior edge temporarily shows a full box.
    internal bool IsEdgeDockSuspended { get; private set; }

    internal bool IsEdgeDockExpanded
    {
        get => _edgeDockExpanded || IsEdgeDockSuspended;
        private set => _edgeDockExpanded = value;
    }

    internal bool IsEdgeDockInteractionBusy =>
        IsTitleEditing
        || HasOpenContextMenu()
        || _modalDialogDepth > 0
        || _isTileOrFileDragActive
        || _externalDragOver
        || IsDraggingOrResizing;

    internal event Action<FileBoxWindow, bool>? EdgeDockToggled;

    internal event Action<FileBoxWindow, bool>? EdgeDockStateChanged;

    internal event Action<FileBoxWindow>? EdgeDockFileDragNear;

    internal void NotifyEdgeDockStateChanged() =>
        EdgeDockStateChanged?.Invoke(this, IsEdgeDocked);

    protected override void PopulateContextMenu(System.Windows.Controls.ContextMenu menu)
    {
        var hoverExpand = new System.Windows.Controls.MenuItem
        {
            Header = "悬停标题栏自动展开",
            IsCheckable = true
        };
        hoverExpand.Click += (_, _) => SetHoverExpand(hoverExpand.IsChecked);

        var hideWhenIdle = new System.Windows.Controls.MenuItem
        {
            Header = "平时隐藏（移入显示）",
            IsCheckable = true
        };
        hideWhenIdle.Click += (_, _) => SetHideWhenIdle(hideWhenIdle.IsChecked);

        var edgeDock = new System.Windows.Controls.MenuItem
        {
            Header = "贴边隐藏",
            IsCheckable = true
        };
        edgeDock.Click += (_, _) => EdgeDockToggled?.Invoke(this, edgeDock.IsChecked);

        var mergeInto = new System.Windows.Controls.MenuItem
        {
            Header = "合并到其他文件收纳盒",
            Visibility = Visibility.Collapsed
        };

        var hoverSwitch = new System.Windows.Controls.MenuItem
        {
            Header = "悬停标签自动切换",
            IsCheckable = true,
            Visibility = Visibility.Collapsed
        };
        hoverSwitch.Click += (_, _) => SetHoverSwitchTabs(hoverSwitch.IsChecked);

        var detachTab = new System.Windows.Controls.MenuItem
        {
            Header = "移出当前标签",
            Visibility = Visibility.Collapsed
        };
        detachTab.Click += (_, _) => DetachTabRequested?.Invoke(this, State);

        var splitGroup = new System.Windows.Controls.MenuItem
        {
            Header = "拆分为独立收纳盒",
            Visibility = Visibility.Collapsed
        };
        splitGroup.Click += (_, _) => SplitGroupRequested?.Invoke(this);

        var deleteTab = new System.Windows.Controls.MenuItem
        {
            Header = "删除当前标签…",
            Visibility = Visibility.Collapsed
        };
        deleteTab.Click += (_, _) => DeleteTabRequested?.Invoke(this, State);

        var groupSeparator = new System.Windows.Controls.Separator
        {
            Visibility = Visibility.Collapsed
        };

        menu.Opened += (_, _) =>
        {
            hoverExpand.IsChecked = State.HoverExpand;
            hoverExpand.IsEnabled = !IsEdgeDocked;
            hideWhenIdle.IsChecked = State.HideWhenIdle;
            hideWhenIdle.IsEnabled = !IsEdgeDocked;
            edgeDock.IsChecked = IsEdgeDocked;

            mergeInto.Items.Clear();
            foreach (var other in OtherBoxes())
            {
                if (!other.IsVisible || other.IsEdgeDocked)
                {
                    continue;
                }
                var captured = other;
                var label = FileBoxGroupRules.FormatGroupDisplayTitle(captured.Tabs, captured.State.Id);
                var target = new System.Windows.Controls.MenuItem { Header = label };
                target.Click += (_, _) => MergeRequested?.Invoke(this, captured);
                mergeInto.Items.Add(target);
            }
            mergeInto.Visibility = !IsEdgeDocked && mergeInto.Items.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            var groupedVisibility = IsGrouped ? Visibility.Visible : Visibility.Collapsed;
            groupSeparator.Visibility = groupedVisibility;
            hoverSwitch.Visibility = groupedVisibility;
            hoverSwitch.IsChecked = State.HoverSwitchTabs;
            detachTab.Visibility = groupedVisibility;
            splitGroup.Visibility = groupedVisibility;
            deleteTab.Visibility = groupedVisibility;
        };

        menu.Items.Add(hoverExpand);
        menu.Items.Add(hideWhenIdle);
        menu.Items.Add(edgeDock);
        menu.Items.Add(mergeInto);
        menu.Items.Add(groupSeparator);
        menu.Items.Add(hoverSwitch);
        menu.Items.Add(detachTab);
        menu.Items.Add(splitGroup);
        menu.Items.Add(deleteTab);
    }

    protected override bool SuppressLayeredClip => IsEdgeDocked && !IsEdgeDockExpanded;

    internal override bool PreferClampHeightAtBottomEdge => true;

    protected override bool IsHitTestPointInside(System.Windows.Point point)
    {
        if (IsEdgeDocked && !IsEdgeDockExpanded && State.EdgeDockSide is { } side)
        {
            var width = ActualWidth;
            var height = ActualHeight;
            if (point.Y < 0d || point.Y > height || width <= 0d || height <= 0d)
            {
                return false;
            }
            var strip = Math.Min(FileBoxEdgeDockRules.VisibleTabWidthDip, width);
            return side == FileBoxEdgeDockSide.Left
                ? point.X >= 0d && point.X <= strip
                : point.X >= width - strip && point.X <= width;
        }
        return base.IsHitTestPointInside(point);
    }

    internal void EnterEdgeDockedHiddenVisual(FileBoxEdgeDockSide side, BingLan.Core.Dock.PixelRect tabBounds)
    {
        IsEdgeDockSuspended = false;
        IsEdgeDockExpanded = false;
        _autoCollapseTimer.Stop();
        _hoverExpandTimer.Stop();
        _idleHideTimer.Stop();
        _isIdleHiddenVisual = false;
        IdleHitSurface.Visibility = Visibility.Collapsed;
        TabStripHost.Visibility = Visibility.Collapsed;
        EdgeDockSlideTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        EdgeDockSlideTransform.X = 0;
        BodySurface.BeginAnimation(OpacityProperty, null);
        BodySurface.Opacity = 1;
        BodySurface.Visibility = Visibility.Collapsed;
        BoxLayout.Visibility = Visibility.Collapsed;
        EdgeDockTab.Width = FileBoxEdgeDockRules.VisibleTabWidthDip;
        EdgeDockTab.HorizontalAlignment = side == FileBoxEdgeDockSide.Left
            ? System.Windows.HorizontalAlignment.Left
            : System.Windows.HorizontalAlignment.Right;
        EdgeDockTab.CornerRadius = side == FileBoxEdgeDockSide.Left
            ? new CornerRadius(0, 3, 3, 0)
            : new CornerRadius(3, 0, 0, 3);
        RefreshEdgeDockTabToolTip();
        EdgeDockTab.Visibility = Visibility.Visible;
        MinWidth = FileBoxEdgeDockRules.VisibleTabWidthDip;
        MinHeight = 32;
        // Clear any collapsed title-bar MaxHeight (48 DIP) so a newly docked collapsed box
        // immediately takes the full rule-computed tab height matching its trigger zone.
        MaxHeight = double.PositiveInfinity;
        RefreshLayeredClip();
        UpdateEdgeDockPosition(tabBounds);
    }

    internal void EnterEdgeDockedExpandedVisual(FileBoxEdgeDockSide side, BingLan.Core.Dock.PixelRect expandedBounds, bool animate)
    {
        IsEdgeDockSuspended = false;
        IsEdgeDockExpanded = true;
        _autoCollapseTimer.Stop();
        _hoverExpandTimer.Stop();
        _idleHideTimer.Stop();
        _isIdleHiddenVisual = false;
        IdleHitSurface.Visibility = Visibility.Collapsed;
        if (State.IsCollapsed)
        {
            SetCollapsed(false, animate: false);
        }
        EdgeDockTab.Visibility = Visibility.Collapsed;
        BodySurface.Visibility = Visibility.Visible;
        BoxLayout.Visibility = Visibility.Visible;
        RefreshTabStripLayout();
        MinWidth = IsGrouped
            ? Math.Max(FileBoxEdgeDockRules.MinExpandedWidthDip, FileBoxGroupRules.GroupedMinWidthDip)
            : FileBoxEdgeDockRules.MinExpandedWidthDip;
        MinHeight = FileBoxEdgeDockRules.MinExpandedHeightDip;
        UpdateEdgeDockPosition(expandedBounds);
        RefreshLayeredClip();

        EdgeDockSlideTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        BodySurface.BeginAnimation(OpacityProperty, null);
        if (!animate || CardMotionAnimator.Motion == MotionLevel.Off)
        {
            EdgeDockSlideTransform.X = 0;
            BodySurface.Opacity = 1;
            return;
        }

        if (CardMotionAnimator.Motion == MotionLevel.Reduced)
        {
            EdgeDockSlideTransform.X = 0;
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0.25, 1d, TimeSpan.FromMilliseconds(90));
            BodySurface.BeginAnimation(OpacityProperty, fade);
            return;
        }

        var fromX = side == FileBoxEdgeDockSide.Left ? -28d : 28d;
        var slide = new System.Windows.Media.Animation.DoubleAnimation(fromX, 0d, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        EdgeDockSlideTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slide);
        BodySurface.Opacity = 1;
    }

    internal void ExitEdgeDockedVisual(BingLan.Core.Dock.PixelRect restoredBounds) =>
        RestoreFreeVisual(restoredBounds, suspended: false);

    internal void SuspendEdgeDockedVisual(BingLan.Core.Dock.PixelRect restoredBounds) =>
        RestoreFreeVisual(restoredBounds, suspended: true);

    private void RestoreFreeVisual(BingLan.Core.Dock.PixelRect restoredBounds, bool suspended)
    {
        IsEdgeDockSuspended = suspended;
        IsEdgeDockExpanded = false;
        EdgeDockSlideTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        EdgeDockSlideTransform.X = 0;
        BodySurface.BeginAnimation(OpacityProperty, null);
        BodySurface.Opacity = 1;
        EdgeDockTab.Visibility = Visibility.Collapsed;
        BodySurface.Visibility = Visibility.Visible;
        BoxLayout.Visibility = Visibility.Visible;
        MinWidth = IsGrouped
            ? Math.Max(FileBoxEdgeDockRules.MinExpandedWidthDip, FileBoxGroupRules.GroupedMinWidthDip)
            : FileBoxEdgeDockRules.MinExpandedWidthDip;
        MinHeight = State.IsCollapsed ? CollapsedHeight : FileBoxEdgeDockRules.MinExpandedHeightDip;
        MaxHeight = State.IsCollapsed ? CollapsedHeight : double.PositiveInfinity;
        // Crossing to a monitor with different DPI triggers WM_DPICHANGED resizing on the first
        // move; a second pass locks the exact restored size.
        UpdateEdgeDockPosition(restoredBounds);
        UpdateEdgeDockPosition(restoredBounds);
        RefreshLayeredClip();
        RefreshTabStripLayout();
        RefreshAutoCollapseTimer();
        RefreshHoverExpandTimer();
        RefreshHoverRevealState();
    }

    internal void UpdateEdgeDockPosition(BingLan.Core.Dock.PixelRect bounds)
    {
        if (WindowHandle is var handle && handle != 0 && bounds.HasArea)
        {
            // Crossing monitors with different DPI triggers WM_DPICHANGED resizing on the
            // first SetWindowPos; a second pass locks exact physical bounds when needed.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (BingLan.App.Interop.DockNativeMethods.GetWindowRect(handle, out var native)
                    && new BingLan.Core.Dock.PixelRect(native.Left, native.Top, native.Right, native.Bottom) == bounds)
                {
                    break;
                }
                BingLan.App.Interop.NativeMethods.SetWindowPos(
                    handle,
                    0,
                    bounds.Left,
                    bounds.Top,
                    bounds.Width,
                    bounds.Height,
                    BingLan.App.Interop.NativeMethods.SwpNoZOrder | BingLan.App.Interop.NativeMethods.SwpNoActivate);
            }
        }
    }

    public void SetAutoCollapse(bool enabled)
    {
        if (State.AutoCollapse == enabled)
        {
            RefreshPinOpenButton();
            RefreshAutoCollapseTimer();
            return;
        }
        State.AutoCollapse = enabled;
        SyncGroupSharedProperties();
        if (!enabled)
        {
            _pinnedOpenForSession = false;
        }
        RefreshPinOpenButton();
        RefreshAutoCollapseTimer();
        NotifyStateChanged();
    }

    public void SetHoverExpand(bool enabled)
    {
        if (State.HoverExpand == enabled)
        {
            RefreshHoverExpandTimer();
            return;
        }
        State.HoverExpand = enabled;
        _suppressHoverExpandUntilLeave = false;
        SyncGroupSharedProperties();
        RefreshHoverExpandTimer();
        NotifyStateChanged();
    }

    public void SetAutoCollapseDelay(double seconds)
    {
        var coerced = FileBoxAutoCollapseRules.CoerceDelaySeconds(seconds);
        if (State.AutoCollapseDelaySeconds is { } current && Math.Abs(current - coerced) < 0.001d)
        {
            return;
        }

        State.AutoCollapseDelaySeconds = coerced;
        SyncGroupSharedProperties();
        RefreshPinOpenButton();
        RefreshAutoCollapseTimer();
        NotifyStateChanged();
    }

    private void PinOpenButton_Click(object sender, RoutedEventArgs e)
    {
        _pinnedOpenForSession = !_pinnedOpenForSession;
        RefreshPinOpenButton();
        RefreshAutoCollapseTimer();
    }

    private void RefreshPinOpenButton()
    {
        if (PinOpenButton is null)
        {
            return;
        }
        var show = State.AutoCollapse && !State.IsCollapsed;
        PinOpenButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PinOpenButton.Content = _pinnedOpenForSession ? "\uE840" : "\uE718";
        PinOpenButton.Opacity = _pinnedOpenForSession ? 1.0 : 0.72;
        PinOpenButton.ToolTip = _pinnedOpenForSession
            ? $"已保持展开（点击恢复 {FileBoxAutoCollapseRules.FormatShortDelay(State.AutoCollapseDelaySeconds)} 自动收起）"
            : "暂时保持展开（鼠标移开不自动收起）";
    }

    private void OnDragExpandTimerTick()
    {
        _dragExpandTimer.Stop();
        if (!_externalDragOver || !State.IsCollapsed)
        {
            return;
        }
        _expandedForExternalDrag = true;
        SetCollapsed(false, animate: true);
    }

    private bool IsCursorInsideWindow()
    {
        var handle = WindowHandle;
        if (handle == 0 || !DockNativeMethods.GetCursorPos(out var pt)
            || !DockNativeMethods.GetWindowRect(handle, out var rect))
        {
            return false;
        }
        return pt.X >= rect.Left && pt.X <= rect.Right && pt.Y >= rect.Top && pt.Y <= rect.Bottom;
    }

    internal void TriggerAutoCollapseTickForTesting() => OnAutoCollapseTimerTick();

    internal void TriggerHoverExpandTickForTesting() => OnHoverExpandTimerTick();

    protected override void OnClosed(EventArgs e)
    {
        _autoCollapseTimer.Stop();
        _dragExpandTimer.Stop();
        _hoverExpandTimer.Stop();
        DisposeHoverReveal();
        DisposeGroups();
        _closeCts.Cancel();
        _closeCts.Dispose();
        base.OnClosed(e);
    }

    protected override void OnTitleEditingChanged(bool isEditing)
    {
        RefreshAutoCollapseTimer();
        RefreshHoverExpandTimer();
        RefreshHoverRevealState();
    }

    protected override void OnResizeEnded()
    {
        _expandedHeightTemporarilyClamped = false;
        base.OnResizeEnded();
    }

    protected override void OnDragEnded()
    {
        if (State.IsCollapsed && IsMouseOver)
        {
            _suppressHoverExpandUntilLeave = true;
        }
        RefreshAutoCollapseTimer();
        RefreshHoverExpandTimer();
        RefreshHoverRevealState();
        base.OnDragEnded();
    }

    /// <summary>The other open boxes a mapping can be transferred to.</summary>
    internal Func<IReadOnlyList<FileBoxWindow>> OtherBoxes { get; set; } = () => [];

    /// <summary>Whether the user turned on deleting originals to the Recycle Bin.</summary>
    internal Func<bool> IsDeleteOriginalsEnabled { get; set; } = () => false;

    /// <summary>Asks before an original goes to the Recycle Bin; true to continue.</summary>
    internal Func<FileTile, bool> ConfirmDeleteOriginal { get; set; }

    /// <summary>Asks once before several originals go to the Recycle Bin; true to continue.</summary>
    internal Func<IReadOnlyList<FileTile>, bool> ConfirmDeleteOriginals { get; set; }

    internal Func<string, nint, Task<RecycleResult>> RecycleOriginal { get; set; } = RecycleBin.SendAsync;

    /// <summary>Sets the file types and whether folders this box collects when organising.</summary>
    internal void SetCollectRule(IEnumerable<string> extensions, bool folders)
    {
        State.CollectExtensions = FileMappingService.NormalizeExtensions(extensions);
        State.CollectFolders = folders;
        NotifyStateChanged();
    }

    /// <summary>Switches the box between the tile view and the compact list view.</summary>
    internal void SetViewMode(FileBoxViewMode mode)
    {
        if (State.ViewMode == mode)
        {
            return;
        }
        State.ViewMode = mode;
        ApplyViewMode();
        NotifyStateChanged();
    }

    /// <summary>Applies the item template and panel that match the box's view mode.</summary>
    private void ApplyViewMode()
    {
        var list = State.ViewMode == FileBoxViewMode.List;
        FileList.ItemTemplate = (DataTemplate)FindResource(list ? "FileRowTemplate" : "FileTileTemplate");
        FileList.ItemsPanel = (System.Windows.Controls.ItemsPanelTemplate)FindResource(
            list ? "FileListPanelTemplate" : "FileTilePanelTemplate");
    }

    internal void AssignDesktopCategory(DesktopGroupCategory category, string title)
    {
        var previous = State.Title;
        State.DesktopCategory = category;
        State.Title = title;
        TitleEditor.Text = title;
        RefreshEdgeDockTabToolTip();
        NotifyStateChanged();
        if (!string.Equals(previous, State.Title, StringComparison.Ordinal))
        {
            NotifyTitleChanged();
        }
    }

    protected override System.Windows.Controls.TextBox? TitleEditorBox => TitleEditor;

    private void RefreshEdgeDockTabToolTip()
    {
        if (EdgeDockTab is not null)
        {
            EdgeDockTab.ToolTip = FileBoxEdgeDockRules.FormatTabToolTip(State.Title);
        }
    }

    private IEnumerable<string> GetOtherFileBoxTitles() =>
        SameKindOtherTitles?.Invoke() ?? OtherBoxes().Select(other => other.State.Title);

    public override bool ApplyWidgetTitle(string? title, IEnumerable<string>? otherSameKindTitles = null)
    {
        var committed = WidgetTitleRules.CommitUniqueTitle(
            title,
            EditableWidgetTitleKind.FileBox,
            otherSameKindTitles ?? GetOtherFileBoxTitles());
        var changed = !string.Equals(State.Title, committed, StringComparison.Ordinal);
        State.Title = committed;
        if (!string.Equals(TitleEditor.Text, committed, StringComparison.Ordinal))
        {
            TitleEditor.Text = committed;
        }
        RefreshTabStripItems();
        RefreshEdgeDockTabToolTip();
        if (changed)
        {
            NotifyStateChanged();
            NotifyTitleChanged();
        }
        return changed;
    }

    protected override void CommitTitleEdit()
    {
        var previous = State.Title;
        State.Title = WidgetTitleRules.CommitUniqueTitle(
            TitleEditor.Text,
            EditableWidgetTitleKind.FileBox,
            GetOtherFileBoxTitles());
        TitleEditor.Text = State.Title;
        RefreshTabStripItems();
        RefreshEdgeDockTabToolTip();
        NotifyStateChanged();
        if (!string.Equals(previous, State.Title, StringComparison.Ordinal))
        {
            NotifyTitleChanged();
        }
    }

    protected override void OnLockChanged(bool locked)
    {
        State.IsLocked = locked;
        SyncGroupSharedProperties();
    }

    private const double CollapsedHeight = 48d;
    private const double DefaultMinHeight = 166d;

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        var collapsing = !State.IsCollapsed;
        if (collapsing && IsMouseOver)
        {
            // Keep the box folded until the pointer leaves the title bar so hovering over
            // the collapse button does not immediately bounce it back open.
            _suppressHoverExpandUntilLeave = true;
        }
        SetCollapsed(collapsing, animate: true);
        NotifyStateChanged();
    }

    private UIElement[] FoldBodyElements() =>
        BoxLayout.Children.OfType<FrameworkElement>()
            .Where(element => System.Windows.Controls.Grid.GetRow(element) > 0)
            .Append<UIElement>(TitleDivider)
            .ToArray();

    /// <summary>
    /// Folds the box up to its title bar, or unfolds it to the height it had. A folded box
    /// keeps its width and can still be dragged; only its height is fixed.
    /// </summary>
    internal void SetCollapsed(bool collapsed, bool animate = false)
    {
        if (collapsed && !State.IsCollapsed)
        {
            if (!_expandedHeightTemporarilyClamped || State.ExpandedHeight <= 0)
            {
                State.ExpandedHeight = Height;
            }
            _expandedHeightTemporarilyClamped = false;
        }
        if (collapsed)
        {
            _pinnedOpenForSession = false;
        }
        else
        {
            _suppressHoverExpandUntilLeave = false;
        }
        State.IsCollapsed = collapsed;
        CollapseButton.Content = collapsed ? "\uE70D" : "\uE70E";
        CollapseButton.ToolTip = collapsed ? "展开文件收纳盒" : "折叠成标题条";
        AddButton.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SortButton.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapsedSummaryText.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        RefreshPinOpenButton();
        RefreshCollapsedSummary();
        SyncGroupSharedProperties();

        var bodyElements = FoldBodyElements();
        if (collapsed)
        {
            TabStripHost.Visibility = Visibility.Collapsed;
            FileBoxMotionOverlay.Children.Clear();
            void CommitCollapsedLayout()
            {
                foreach (var element in bodyElements)
                {
                    element.Visibility = Visibility.Collapsed;
                }
                MinHeight = CollapsedHeight;
                MaxHeight = CollapsedHeight;
                Height = CollapsedHeight;
            }

            if (animate && IsLoaded && CardMotionAnimator.IsAnimationEnabled)
            {
                _foldAnimator.BeginCollapse(CollapsedHeight, bodyElements, CommitCollapsedLayout);
            }
            else
            {
                _foldAnimator.Snap();
                CommitCollapsedLayout();
            }
        }
        else
        {
            foreach (var element in bodyElements)
            {
                element.Visibility = Visibility.Visible;
            }
            MaxHeight = double.PositiveInfinity;
            MinHeight = DefaultMinHeight;
            var desiredHeight = Math.Max(DefaultMinHeight, State.ExpandedHeight > 0 ? State.ExpandedHeight : 306d);
            var clampedHeight = ClampExpandedHeightToWorkArea(desiredHeight);
            _expandedHeightTemporarilyClamped = clampedHeight < desiredHeight - 0.5d;
            Height = clampedHeight;
            // 展开向下长高，等原生矩形反映新尺寸后确认不越界（优先收缩高度，不把顶边往上顶）。
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded, EnsureFullyOnScreen);

            if (animate && IsLoaded && CardMotionAnimator.IsAnimationEnabled)
            {
                _foldAnimator.BeginExpand(CollapsedHeight, Height, bodyElements);
            }
            else
            {
                _foldAnimator.Snap();
            }
        }
        RefreshTabStripLayout();
        RefreshAutoCollapseTimer();
        RefreshHoverExpandTimer();
        RefreshHoverRevealState();
    }

    private double ClampExpandedHeightToWorkArea(double desiredHeight)
    {
        if (!IsLoaded || WindowHandle is var handle && handle == 0
            || !DockNativeMethods.GetWindowRect(handle, out var native))
        {
            return desiredHeight;
        }

        var monitors = BingLan.App.Dock.MonitorCatalog.GetAll();
        if (monitors.Count == 0)
        {
            return desiredHeight;
        }

        var centerX = (native.Left + native.Right) / 2;
        var centerY = (native.Top + native.Bottom) / 2;
        var monitor = monitors.FirstOrDefault(candidate =>
                centerX >= candidate.Bounds.Left && centerX < candidate.Bounds.Right
                && centerY >= candidate.Bounds.Top && centerY < candidate.Bounds.Bottom)
            ?? monitors.FirstOrDefault(candidate => candidate.IsPrimary)
            ?? monitors[0];
        var scaleY = VisualTreeHelper.GetDpi(this).DpiScaleY;
        if (scaleY <= 0d || !monitor.WorkingArea.HasArea)
        {
            return desiredHeight;
        }

        var safeWork = TaskbarEdgeGuard.AvoidNativeTaskbar(monitor.WorkingArea, monitor.Bounds);
        var availableDip = (safeWork.Bottom - native.Top) / scaleY;
        if (availableDip >= DefaultMinHeight && desiredHeight > availableDip)
        {
            return Math.Max(DefaultMinHeight, Math.Floor(availableDip));
        }

        return desiredHeight;
    }

    private async void FileBoxWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (State.IsCollapsed)
        {
            SetCollapsed(true);
        }
        RefreshTabStripLayout();
        RefreshAutoCollapseTimer();
        RefreshHoverRevealState();
        if (_initialIconLoadStarted)
        {
            return;
        }

        _initialIconLoadStarted = true;
        try
        {
            await ProbeTilesAsync(_items.ToArray(), loadMissingIcons: true);
        }
        finally
        {
            InitialProbeCompleted = true;
        }
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WpfOpenFileDialog
        {
            Title = "选择要映射到文件收纳盒的文件",
            CheckFileExists = true,
            Multiselect = true
        };
        if (RunModalInteraction(() => dialog.ShowDialog(this) == true))
        {
            await AddMappingsWithStatusAsync(dialog.FileNames);
        }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WpfOpenFolderDialog
        {
            Title = "选择要映射到文件收纳盒的文件夹"
        };
        if (RunModalInteraction(() => dialog.ShowDialog(this) == true))
        {
            await AddMappingsWithStatusAsync([dialog.FolderName]);
        }
    }

    private async void ImportDesktop_Click(object sender, RoutedEventArgs e)
    {
        var targetTab = State;
        AddButton.IsEnabled = false;
        StatusText.Text = "正在扫描用户与公共桌面…";

        try
        {
            var desktopDirectories = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            };
            var paths = await Task.Run(
                () => FileMappingService.EnumerateDirectChildren(desktopDirectories));
            var added = await AddMappingsToBoxAsync(targetTab.Id, paths);
            StatusText.Text = added > 0
                ? (targetTab.Id == State.Id
                    ? $"已新增 {added} 项"
                    : $"已向“{targetTab.Title}”新增 {added} 项")
                : "没有新的桌面项目可导入";
        }
        finally
        {
            AddButton.IsEnabled = true;
        }
    }

    private void AutoOrganize_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "正在扫描并生成分类盒…";
        RequestDesktopOrganization();
    }

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    private async void SortManual_Click(object sender, RoutedEventArgs e) =>
        await ApplySort(FileMappingSortMode.Manual);

    private async void SortName_Click(object sender, RoutedEventArgs e) =>
        await ApplySort(FileMappingSortMode.Name);

    private async void SortType_Click(object sender, RoutedEventArgs e) =>
        await ApplySort(FileMappingSortMode.Type);

    private async void SortModified_Click(object sender, RoutedEventArgs e) =>
        await ApplySort(FileMappingSortMode.Modified);

    public void CaptureState()
    {
        if (!IsEdgeDocked)
        {
            CopyPlacementTo(State.Placement);
            if (State.IsCollapsed)
            {
                State.Placement.Height = CollapsedHeight;
            }
        }
        State.CornerRadius = WidgetCornerRadius;
        State.Items = _items.Select(x => x.Mapping).ToList();
        SyncGroupSharedProperties();
    }

    private const string InternalBoxDragFormat = "BingLan.FileBoxMappings";

    private sealed record InternalBoxDragPayload(Guid SourceBoxId, Guid[] MappingIds);

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(InternalBoxDragFormat)
            && e.Data.GetData(InternalBoxDragFormat) is InternalBoxDragPayload payload)
        {
            if (payload.SourceBoxId == State.Id || payload.MappingIds.Length == 0)
            {
                e.Effects = System.Windows.DragDropEffects.None;
                e.Handled = true;
                return;
            }

            // Plain drag between boxes transfers the mapping; holding Ctrl copies the mapping
            // while keeping it in the source box. Originals on disk are never touched.
            e.Effects = (e.KeyStates & System.Windows.DragDropKeyStates.ControlKey) != 0
                ? System.Windows.DragDropEffects.Copy
                : System.Windows.DragDropEffects.Link;
            e.Handled = true;
            return;
        }

        // A tile dragged within its own box is not a new item.
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] dragged
            && dragged.All(path => _items.Any(tile => string.Equals(tile.Path, path, StringComparison.OrdinalIgnoreCase))))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Link
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        var restoreCollapse = BeginExternalDropCleanup();
        if (e.Data.GetDataPresent(InternalBoxDragFormat)
            && e.Data.GetData(InternalBoxDragFormat) is InternalBoxDragPayload payload
            && payload.SourceBoxId != State.Id)
        {
            if (ResolveBoxAndTab(payload.SourceBoxId) is { } resolved)
            {
                var copyOnly = (e.KeyStates & System.Windows.DragDropKeyStates.ControlKey) != 0;
                TransferMappingIdsBetweenTabs(
                    resolved.Window,
                    resolved.Tab,
                    this,
                    State,
                    payload.MappingIds,
                    copyOnly);
            }
        }
        else if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
        {
            await AddMappingsWithStatusAsync(paths);
        }
        EndExternalDropCleanup(restoreCollapse);
    }

    private System.Windows.Point? _tileDragStart;

    // A tile can be dragged out to external apps/Dock (copy or link only; Move is never
    // offered so dragging out cannot move original files on disk), or dropped onto another
    // BingLan file box to transfer (or Ctrl-drag to copy) its mapping directly.
    private void FileTile_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_tileDragStart is not { } start
            || e.LeftButton != MouseButtonState.Pressed
            || sender is not Button { DataContext: FileTile tile } button)
        {
            return;
        }

        var offset = e.GetPosition(this) - start;
        if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var draggedTiles = tile.IsSelected
            ? _items
                .Where(item => item.IsSelected)
                .Append(tile)
                .Distinct()
                .ToList()
            : new List<FileTile> { tile };

        var externalPaths = draggedTiles
            .Where(item => !item.IsShellEntry && !item.IsMissing)
            .Select(item => item.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var data = new System.Windows.DataObject();
        data.SetData(
            InternalBoxDragFormat,
            new InternalBoxDragPayload(State.Id, draggedTiles.Select(item => item.Mapping.Id).ToArray()));
        if (externalPaths.Length > 0)
        {
            data.SetData(System.Windows.DataFormats.FileDrop, externalPaths);
        }

        _tileDragStart = null;
        _singleClickFileTiles.Remove(button);
        _isTileOrFileDragActive = true;
        RefreshAutoCollapseTimer();
        DragOutStarted?.Invoke();
        try
        {
            DragDrop.DoDragDrop(
                button,
                data,
                // Copy lets a folder or chat window receive the file itself; Move is never
                // offered, so dragging out cannot take the original away.
                System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Link);
        }
        finally
        {
            _isTileOrFileDragActive = false;
            RefreshAutoCollapseTimer();
            RefreshHoverRevealState();
            DragOutEnded?.Invoke();
        }
    }

    private void FileTile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { DataContext: FileTile tile } button)
        {
            return;
        }
        _tileDragStart = e.GetPosition(this);

        if (e.ClickCount == 1)
        {
            _singleClickFileTiles.Add(button);
            // A plain press only highlights the tile; opening stays on the double click.
            HandleTilePointerSelect(tile, Keyboard.Modifiers);
        }
        else
        {
            _singleClickFileTiles.Remove(button);
        }
    }

    // Right-clicking a tile inside a multi-selection opens the batch menu; right-clicking
    // any other tile falls back to the regular single-item menu without changing selection.
    private void FileTile_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { DataContext: FileTile tile } button)
        {
            return;
        }

        var selected = SelectedTiles();
        var useBatch = selected.Count > 1 && _selectedIds.Contains(tile.Mapping.Id);
        var menu = (System.Windows.Controls.ContextMenu)FindResource(
            useBatch ? "FileTileBatchContextMenu" : "FileTileContextMenu");
        if (!ReferenceEquals(button.ContextMenu, menu))
        {
            button.ContextMenu = menu;
        }
    }

    // A press that does not land on a tile — empty space, the header, the footer — clears
    // the selection, like clicking empty space in Explorer. The window drag gesture starts
    // from the same press and is unaffected.
    private void FileBoxWindow_BackgroundPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsInsideFileTileButton(e.OriginalSource as DependencyObject))
        {
            ClearSelection();
        }
    }

    private static bool IsInsideFileTileButton(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Button { DataContext: FileTile })
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Applies the selection semantics of a pointer press: a plain press selects one item,
    /// Ctrl toggles one item, Shift selects the range from the anchor. Split from the mouse
    /// handler so tests can drive it without synthesizing modifier keys.
    /// </summary>
    internal void HandleTilePointerSelect(FileTile tile, ModifierKeys modifiers)
    {
        var id = tile.Mapping.Id;
        if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (!_selectedIds.Remove(id))
            {
                _selectedIds.Add(id);
            }
            _selectionAnchor = id;
        }
        else if ((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            _selectedIds.Clear();
            _selectedIds.Add(id);
            if (_selectionAnchor is { } anchor)
            {
                var anchorIndex = -1;
                for (var index = 0; index < _items.Count; index++)
                {
                    if (_items[index].Mapping.Id == anchor)
                    {
                        anchorIndex = index;
                        break;
                    }
                }

                var targetIndex = _items.IndexOf(tile);
                if (anchorIndex >= 0 && targetIndex >= 0)
                {
                    for (var index = Math.Min(anchorIndex, targetIndex);
                         index <= Math.Max(anchorIndex, targetIndex);
                         index++)
                    {
                        _selectedIds.Add(_items[index].Mapping.Id);
                    }
                }
            }
            // The anchor stays where it was so the next Shift press can extend or shrink
            // the range; without an anchor the pressed item becomes the whole selection.
        }
        else
        {
            _selectedIds.Clear();
            _selectedIds.Add(id);
            _selectionAnchor = id;
        }
        SyncSelectionVisuals();
    }

    /// <summary>The selected tiles in display order; ids of removed tiles never appear.</summary>
    internal IReadOnlyList<FileTile> SelectedTiles() =>
        _items.Where(tile => _selectedIds.Contains(tile.Mapping.Id)).ToList();

    internal void ClearSelection()
    {
        if (_selectedIds.Count == 0 && _selectionAnchor is null)
        {
            return;
        }
        _selectedIds.Clear();
        _selectionAnchor = null;
        SyncSelectionVisuals();
    }

    /// <summary>Drops selection ids that no longer match a live tile, e.g. after sweeping.</summary>
    private void PruneSelection()
    {
        if (_selectedIds.Count == 0 && _selectionAnchor is null)
        {
            return;
        }
        var liveIds = _items.Select(tile => tile.Mapping.Id).ToHashSet();
        _selectedIds.IntersectWith(liveIds);
        if (_selectionAnchor is { } anchor && !liveIds.Contains(anchor))
        {
            _selectionAnchor = null;
        }
    }

    private void SyncSelectionVisuals()
    {
        foreach (var tile in _items)
        {
            tile.IsSelected = _selectedIds.Contains(tile.Mapping.Id);
        }
    }

    // Tiles are immutable snapshots; whenever one is replaced the selection follows the
    // mapping id instead of being lost with the old instance.
    private FileTile CreateTile(FileMappingState mapping, ImageSource? icon)
    {
        var tile = new FileTile(mapping, icon);
        tile.IsSelected = _selectedIds.Contains(mapping.Id);
        return tile;
    }

    private void FileTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _singleClickFileTiles.Remove(button))
        {
            return;
        }

        if (button.DataContext is not FileTile tile)
        {
            return;
        }

        if (tile.IsMissing && FileMappingService.IsMissing(tile.Mapping))
        {
            BeginRelink(tile);
            return;
        }

        Open(tile);
        ItemOpened?.Invoke();
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileTile tile)
        {
            Open(tile);
            ItemOpened?.Invoke();
        }
    }

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileTile tile || tile.IsShellEntry)
        {
            return;
        }
        StartShell(new ProcessStartInfo("explorer.exe", $"/select,\"{tile.Path}\"") { UseShellExecute = true });
    }

    private void CopyFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileTile tile)
        {
            CopyToClipboard([tile]);
        }
    }

    private void BatchCopyFiles_Click(object sender, RoutedEventArgs e) => CopyToClipboard(SelectedTiles());

    /// <summary>
    /// Puts the originals on the clipboard as a file copy, the same as Ctrl+C in Explorer;
    /// pasting makes a copy and the original stays where it is.
    /// </summary>
    private static void CopyToClipboard(IEnumerable<FileTile> tiles)
    {
        var paths = new System.Collections.Specialized.StringCollection();
        foreach (var tile in tiles.Where(tile => !tile.IsShellEntry && !tile.IsMissing))
        {
            paths.Add(tile.Path);
        }
        if (paths.Count == 0)
        {
            return;
        }

        var data = new System.Windows.DataObject();
        data.SetFileDropList(paths);
        // DROPEFFECT_COPY, so Explorer pastes a copy rather than moving the file.
        data.SetData("Preferred DropEffect", new System.IO.MemoryStream(BitConverter.GetBytes(1)));
        try
        {
            System.Windows.Clipboard.SetDataObject(data, true);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another program holds the clipboard; the user can simply try again.
        }
    }

    private async void RefreshIcon_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileTile tile)
        {
            return;
        }

        LauncherIconCache.Remove(LauncherIconCache.KeyForFileBoxPath(tile.Path));
        ShellIconProvider.InvalidateIconForPath(tile.Path);
        await ProbeTilesAsync([tile], loadMissingIcons: true, forceReloadIcon: true);
    }

    private void FileTileContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu { DataContext: FileTile tile } menu)
        {
            return;
        }

        // Shell namespace entries never resolve to a real path, so relocating or revealing
        // their location in Explorer does not apply to them.
        var locationVisibility = tile.IsShellEntry ? Visibility.Collapsed : Visibility.Visible;
        var deleteVisibility = IsDeleteOriginalsEnabled() && !tile.IsShellEntry && !tile.IsMissing
            ? Visibility.Visible
            : Visibility.Collapsed;
        foreach (var element in menu.Items.OfType<FrameworkElement>()
                     .Where(element => Equals(element.Tag, "DeleteOriginal")))
        {
            element.Visibility = deleteVisibility;
        }
        foreach (var item in menu.Items.OfType<System.Windows.Controls.MenuItem>())
        {
            if (Equals(item.Header, "打开所在位置") || Equals(item.Header, "重新定位…"))
            {
                item.Visibility = locationVisibility;
            }
            else if (Equals(item.Header, "转移到其他文件收纳盒"))
            {
                PopulateTransferTargets(item, [tile]);
            }
        }
    }

    private void RenameMapping_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileTile tile)
        {
            return;
        }

        PromptRenameMapping(tile);
    }

    private void PromptRenameMapping(FileTile tile)
    {
        var dialog = new TextPromptDialog(
            "修改显示名称",
            "留空则恢复原名称。",
            tile.Name)
        {
            Owner = this
        };
        if (RunModalInteraction(() => dialog.ShowDialog() == true))
        {
            RenameMapping(tile, dialog.Value);
        }
    }

    protected override bool HandleRenameKey()
    {
        if (!IsTitleEditing
            && Keyboard.FocusedElement is FrameworkElement { DataContext: FileTile focusedTile } focused
            && FileList.IsAncestorOf(focused))
        {
            var selected = SelectedTiles();
            if (selected.Count <= 1 || !_selectedIds.Contains(focusedTile.Mapping.Id))
            {
                PromptRenameMapping(focusedTile);
                return true;
            }
        }

        return base.HandleRenameKey();
    }

    internal void RenameMapping(FileTile tile, string displayName)
    {
        CaptureState();
        if (FileMappingService.Rename(State, tile.Mapping.Id, displayName))
        {
            tile.RefreshName();
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// Moves this PC, the Recycle Bin, Downloads and the desktop folder to the given box;
    /// they are places, not apps or files.
    /// </summary>
    internal void MoveSystemPlacesTo(FileBoxWindow target) =>
        MoveSystemPlacesToTab(target, target.State);

    internal void MoveSystemPlacesToTab(FileBoxWindow targetWindow, FileBoxState targetTab)
    {
        CaptureState();
        var places = new[] { KnownFolders.GetDownloadsPath(), DesktopFolderPath() };
        foreach (var sourceTab in _tabs.ToList())
        {
            if (sourceTab.Id == targetTab.Id)
            {
                continue;
            }

            var matchingIds = sourceTab.Items
                .Where(item => FileMappingService.IsShellEntry(item.Path)
                    || places.Any(place => string.Equals(place, item.Path, StringComparison.OrdinalIgnoreCase)))
                .Select(item => item.Id)
                .ToList();
            if (matchingIds.Count > 0)
            {
                TransferMappingIdsBetweenTabs(
                    this,
                    sourceTab,
                    targetWindow,
                    targetTab,
                    matchingIds,
                    copyOnly: false);
            }
        }
    }

    internal void TransferMapping(FileTile tile, FileBoxWindow target)
    {
        CaptureState();
        target.CaptureState();
        if (!FileMappingService.Transfer(State, target.State, tile.Mapping.Id))
        {
            StatusText.Text = $"“{target.State.Title}”中已有这个项目";
            return;
        }

        _items.Remove(tile);
        _selectedIds.Remove(tile.Mapping.Id);
        RefreshCount();
        NotifyStateChanged();
        var transferred = target.CreateTile(tile.Mapping, tile.Icon);
        transferred.IsMissing = tile.IsMissing;
        target._items.Add(transferred);
        target.RefreshCount();
        target.NotifyStateChanged();
        StatusText.Text = $"已转移到“{target.State.Title}”";
    }

    private void RelinkFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileTile tile)
        {
            BeginRelink(tile);
        }
    }

    private void RelinkFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileTile tile)
        {
            return;
        }

        var dialog = new WpfOpenFolderDialog
        {
            Title = "选择用于重新定位的文件夹"
        };
        if (RunModalInteraction(() => dialog.ShowDialog(this) == true))
        {
            ApplyRelink(tile, dialog.FolderName);
        }
    }

    private void BeginRelink(FileTile tile)
    {
        var dialog = new WpfOpenFileDialog
        {
            Title = "选择用于重新定位的文件",
            CheckFileExists = true
        };
        if (RunModalInteraction(() => dialog.ShowDialog(this) == true))
        {
            ApplyRelink(tile, dialog.FileName);
        }
    }

    private void ApplyRelink(FileTile tile, string newPath)
    {
        CaptureState();
        if (!FileMappingService.Relink(State, tile.Mapping.Id, newPath))
        {
            StatusText.Text = "重新定位失败，请确认所选项目存在";
            return;
        }

        ReloadItems();
        NotifyStateChanged();
        StatusText.Text = "已重新定位映射";
    }

    private void Open(FileTile tile)
    {
        if (tile.IsShellEntry)
        {
            StartShell(new ProcessStartInfo("explorer.exe", tile.Path) { UseShellExecute = true });
            return;
        }

        var exists = File.Exists(tile.Path) || Directory.Exists(tile.Path);
        if (exists == tile.IsMissing)
        {
            // The item appeared or disappeared since the card was drawn; redraw its state.
            RefreshTile(tile);
        }
        if (!exists)
        {
            StatusText.Text = "原文件或文件夹已不存在，已标记为“已失效”，可重新定位或移除";
            return;
        }
        StartShell(new ProcessStartInfo(tile.Path) { UseShellExecute = true });
    }

    // Opening can fail (no app for the file type, the elevation prompt declined); the card
    // says so instead of the app closing.
    private async void StartShell(ProcessStartInfo start)
    {
        if (await ShellLauncher.StartAsync(start) is { } failure)
        {
            StatusText.Text = $"无法打开：{failure.Message}";
        }
    }

    private async void AddSystemEntryMyComputer_Click(object sender, RoutedEventArgs e) =>
        await AddSystemEntryAsync(MyComputerEntry, "此电脑");

    private async void AddSystemEntryRecycleBin_Click(object sender, RoutedEventArgs e) =>
        await AddSystemEntryAsync(RecycleBinEntry, "回收站");

    private async void AddSystemEntryDownloads_Click(object sender, RoutedEventArgs e)
    {
        var path = KnownFolders.GetDownloadsPath();
        if (string.IsNullOrEmpty(path))
        {
            StatusText.Text = "无法定位下载文件夹";
            return;
        }
        await AddSystemEntryAsync(path, "下载");
    }

    private async void AddSystemEntryDesktop_Click(object sender, RoutedEventArgs e) =>
        await AddSystemEntryAsync(DesktopFolderPath(), "桌面");

    private static string DesktopFolderPath() =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>Adds a folder or system location under a fixed display name, such as “下载”.</summary>
    internal Task<bool> AddNamedEntryAsync(string path, string title) => AddSystemEntryAsync(path, title);

    private async Task<bool> AddSystemEntryAsync(string path, string title)
    {
        var targetTab = State;
        var (icon, isMissing) = await Task.Run(() => (
            ShellIconProvider.GetIcon(path),
            !FileMappingService.IsShellEntry(path) && !File.Exists(path) && !Directory.Exists(path)));
        if (_closeCts.IsCancellationRequested || !ContainsBox(targetTab.Id))
        {
            return false;
        }
        CaptureState();
        var added = FileMappingService.AddSystemEntry(targetTab, path, title);
        if (added == 0)
        {
            StatusText.Text = "该系统入口已存在";
            return false;
        }

        var mapping = targetTab.Items.Single(item => item.Path == path);
        if (targetTab.Id == State.Id)
        {
            var tile = new FileTile(mapping, icon) { IsMissing = isMissing };
            _items.Add(tile);
            RefreshCount();
            NotifyStateChanged();
            if (IsLoaded)
            {
                AnimateTileEnter([tile]);
            }
            StatusText.Text = $"已添加“{title}”";
        }
        else
        {
            if (icon is not null)
            {
                _tabIconCache[mapping.Id] = (icon, isMissing);
            }
            RefreshTabStripItems();
            NotifyStateChanged();
            StatusText.Text = $"已向“{targetTab.Title}”添加“{title}”";
        }
        return true;
    }

    private void RemoveMapping_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileTile tile)
        {
            return;
        }
        AnimateTileExit(tile);
        _items.Remove(tile);
        _selectedIds.Remove(tile.Mapping.Id);
        FileMappingService.Remove(State, tile.Mapping.Id);
        CaptureState();
        RefreshCount();
        NotifyStateChanged();
    }

    private async void DeleteOriginal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileTile tile)
        {
            await DeleteOriginalAsync(tile);
        }
    }

    // Delete acts only on focused or selected items and only while deleting originals is
    // on; it never falls back to removing the mapping, so the key has a single meaning.
    // Esc clears a multi-selection; while a title is being edited the key stays with the
    // editor, which commits or cancels the edit itself.
    private async void FileBoxWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (IsTitleEditing)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_selectedIds.Count > 0)
            {
                ClearSelection();
                e.Handled = true;
                return;
            }
            PeekEscapeRequested?.Invoke();
            return;
        }

        if (e.Key == Key.Space
            && Keyboard.Modifiers == ModifierKeys.None
            && Keyboard.FocusedElement is FrameworkElement { DataContext: FileTile spaceTile } spaceFocus
            && FileList.IsAncestorOf(spaceFocus)
            && !spaceTile.IsShellEntry
            && !spaceTile.IsMissing)
        {
            e.Handled = true;
            _ = QuickPreviewLauncher.TryTogglePreviewAsync(spaceTile.Path);
            return;
        }

        if (e.Key == Key.C
            && Keyboard.Modifiers == ModifierKeys.Control
            && Keyboard.FocusedElement is FrameworkElement { DataContext: FileTile copyTile } copyFocus
            && FileList.IsAncestorOf(copyFocus))
        {
            e.Handled = true;
            var selection = SelectedTiles();
            CopyToClipboard(selection.Count > 1 && _selectedIds.Contains(copyTile.Mapping.Id) ? selection : [copyTile]);
            return;
        }

        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            string[]? clipboardPaths = null;
            try
            {
                if (System.Windows.Clipboard.ContainsFileDropList())
                {
                    var list = System.Windows.Clipboard.GetFileDropList();
                    if (list is { Count: > 0 })
                    {
                        clipboardPaths = list.Cast<string>().Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
                    }
                }
            }
            catch
            {
                // Clipboard may be briefly locked by another application; stay quiet.
            }

            if (clipboardPaths is { Length: > 0 })
            {
                e.Handled = true;
                await AddMappingsWithStatusAsync(clipboardPaths);
                return;
            }
        }

        if (e.Key != Key.Delete
            || Keyboard.Modifiers != ModifierKeys.None
            || !IsDeleteOriginalsEnabled()
            || Keyboard.FocusedElement is not FrameworkElement { DataContext: FileTile focusedTile } focused
            || !FileList.IsAncestorOf(focused))
        {
            return;
        }

        var selected = SelectedTiles();
        if (selected.Count > 1 && _selectedIds.Contains(focusedTile.Mapping.Id))
        {
            e.Handled = true;
            await DeleteOriginalsAsync(selected);
            return;
        }

        e.Handled = true;
        await DeleteOriginalAsync(focusedTile);
    }

    private void FileTileBatchContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu menu)
        {
            return;
        }

        var selected = SelectedTiles();
        if (selected.Count == 0)
        {
            menu.IsOpen = false;
            return;
        }

        // Shell entries and stale mappings have no original to recycle.
        var deletable = selected
            .Where(tile => !tile.IsShellEntry && !tile.IsMissing)
            .ToList();
        var deleteVisibility = IsDeleteOriginalsEnabled() && deletable.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        foreach (var element in menu.Items.OfType<FrameworkElement>()
                     .Where(element => Equals(element.Tag, "DeleteOriginal")))
        {
            element.Visibility = deleteVisibility;
        }
        foreach (var item in menu.Items.OfType<System.Windows.Controls.MenuItem>())
        {
            switch (item.Tag as string)
            {
                case "BatchRemove":
                    item.Header = $"移除 {selected.Count} 项映射";
                    break;
                case "DeleteOriginal":
                    item.Header = $"删除 {deletable.Count} 项到回收站…";
                    break;
                case "BatchTransfer":
                    PopulateTransferTargets(item, selected);
                    break;
            }
        }
    }

    private void BatchRemoveMappings_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedTiles();
        if (selected.Count == 0)
        {
            return;
        }

        foreach (var tile in selected)
        {
            AnimateTileExit(tile);
            _items.Remove(tile);
            FileMappingService.Remove(State, tile.Mapping.Id);
        }
        ClearSelection();
        CaptureState();
        RefreshCount();
        NotifyStateChanged();
        StatusText.Text = $"已移除 {selected.Count} 项映射";
    }

    internal void TransferMappingsByIdsTo(IReadOnlyList<Guid> mappingIds, FileBoxWindow target)
    {
        var idSet = mappingIds.ToHashSet();
        var tiles = _items.Where(tile => idSet.Contains(tile.Mapping.Id)).ToList();
        if (tiles.Count == 0)
        {
            return;
        }
        var (moved, skipped) = BatchTransferMappings(target, tiles);
        target.StatusText.Text = moved > 0
            ? (skipped > 0
                ? $"已从“{State.Title}”转入 {moved} 项（跳过 {skipped} 项重复）"
                : $"已从“{State.Title}”转入 {moved} 项")
            : "所选项目已在此收纳盒中";
    }

    internal void CopyMappingsFrom(FileBoxWindow sourceBox, IReadOnlyList<Guid> mappingIds)
    {
        var idSet = mappingIds.ToHashSet();
        var tiles = sourceBox._items.Where(tile => idSet.Contains(tile.Mapping.Id)).ToList();
        if (tiles.Count == 0)
        {
            return;
        }

        CaptureState();
        var added = 0;
        var skipped = 0;
        foreach (var tile in tiles)
        {
            if (!FileMappingService.CopyMapping(sourceBox.State, State, tile.Mapping.Id, out var clone)
                || clone is null)
            {
                skipped++;
                continue;
            }

            var copyTile = CreateTile(clone, tile.Icon);
            copyTile.IsMissing = tile.IsMissing;
            _items.Add(copyTile);
            added++;
        }

        if (added > 0)
        {
            RefreshCount();
            NotifyStateChanged();
        }

        StatusText.Text = added > 0
            ? (skipped > 0
                ? $"已从“{sourceBox.State.Title}”复制 {added} 项映射（跳过 {skipped} 项重复）"
                : $"已从“{sourceBox.State.Title}”复制 {added} 项映射")
            : "所选项目已在此收纳盒中";
    }

    private (int Moved, int Skipped) BatchTransferMappings(FileBoxWindow target, IReadOnlyList<FileTile> selected)
    {
        CaptureState();
        target.CaptureState();
        var moved = 0;
        var skipped = 0;
        foreach (var tile in selected)
        {
            if (!FileMappingService.Transfer(State, target.State, tile.Mapping.Id))
            {
                skipped++;
                continue;
            }

            _items.Remove(tile);
            _selectedIds.Remove(tile.Mapping.Id);
            var transferred = target.CreateTile(tile.Mapping, tile.Icon);
            transferred.IsMissing = tile.IsMissing;
            target._items.Add(transferred);
            moved++;
        }

        ClearSelection();
        RefreshCount();
        NotifyStateChanged();
        target.RefreshCount();
        target.NotifyStateChanged();
        StatusText.Text = skipped > 0
            ? $"已转移 {moved} 项；{skipped} 项已存在于“{target.State.Title}”"
            : $"已转移 {moved} 项到“{target.State.Title}”";
        return (moved, skipped);
    }

    private async void BatchDeleteOriginals_Click(object sender, RoutedEventArgs e)
    {
        await DeleteOriginalsAsync(SelectedTiles());
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e) => ClearSelection();

    private bool _deletingOriginal;

    /// <summary>
    /// Moves the item's original to the Recycle Bin after the user confirms, then removes
    /// its mapping. Locations that cannot be recycled are refused, never deleted outright.
    /// </summary>
    internal async Task<bool> DeleteOriginalAsync(FileTile tile)
    {
        if (_deletingOriginal || !IsDeleteOriginalsEnabled())
        {
            return false;
        }

        var exists = File.Exists(tile.Path) || Directory.Exists(tile.Path);
        var block = OriginalFileDeleteRules.Check(
            tile.Path,
            exists,
            RecycleBin.IsOnFixedLocalDrive(tile.Path),
            RecycleBin.ProtectedFolders());
        if (block != OriginalFileDeleteBlock.None)
        {
            StatusText.Text = OriginalFileDeleteRules.Describe(block);
            return false;
        }

        if (!RunModalInteraction(() => ConfirmDeleteOriginal(tile)))
        {
            return false;
        }

        var targetTab = State;
        _deletingOriginal = true;
        RecycleResult result;
        try
        {
            result = await RecycleOriginal(tile.Path, new System.Windows.Interop.WindowInteropHelper(this).Handle);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or DllNotFoundException or EntryPointNotFoundException)
        {
            result = RecycleResult.Failed;
        }
        finally
        {
            _deletingOriginal = false;
        }

        if (result != RecycleResult.Recycled)
        {
            StatusText.Text = result == RecycleResult.Cancelled
                ? "已取消，原文件未删除"
                : "未能删除，文件可能正在使用或没有权限";
            return false;
        }

        if (targetTab.Id == State.Id)
        {
            AnimateTileExit(tile);
            _items.Remove(tile);
            _selectedIds.Remove(tile.Mapping.Id);
        }
        FileMappingService.Remove(targetTab, tile.Mapping.Id);
        CaptureState();
        if (targetTab.Id == State.Id)
        {
            RefreshCount();
        }
        RefreshTabStripItems();
        NotifyStateChanged();
        StatusText.Text = $"已将“{tile.Name}”移到回收站";
        return true;
    }

    /// <summary>
    /// Moves several originals to the Recycle Bin after one summary confirmation. Items the
    /// safety rules refuse are skipped up front and reported; like the single-item flow,
    /// locations that cannot be recycled are never deleted outright, and a cancellation
    /// stops the remaining items.
    /// </summary>
    internal async Task<int> DeleteOriginalsAsync(IReadOnlyList<FileTile> tiles)
    {
        if (_deletingOriginal || !IsDeleteOriginalsEnabled() || tiles.Count == 0)
        {
            return 0;
        }

        var eligible = new List<FileTile>();
        var blocked = 0;
        foreach (var tile in tiles)
        {
            var exists = File.Exists(tile.Path) || Directory.Exists(tile.Path);
            var block = OriginalFileDeleteRules.Check(
                tile.Path,
                exists,
                RecycleBin.IsOnFixedLocalDrive(tile.Path),
                RecycleBin.ProtectedFolders());
            if (block == OriginalFileDeleteBlock.None)
            {
                eligible.Add(tile);
            }
            else
            {
                blocked++;
            }
        }

        if (eligible.Count == 0)
        {
            if (blocked > 0)
            {
                StatusText.Text = "所选项目都不能移到回收站，未做任何更改";
            }
            return 0;
        }

        if (!RunModalInteraction(() => ConfirmDeleteOriginals(eligible)))
        {
            return 0;
        }

        var targetTab = State;
        _deletingOriginal = true;
        var recycled = 0;
        var failed = 0;
        var cancelled = false;
        try
        {
            foreach (var tile in eligible)
            {
                RecycleResult result;
                try
                {
                    result = await RecycleOriginal(
                        tile.Path, new System.Windows.Interop.WindowInteropHelper(this).Handle);
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                    or DllNotFoundException or EntryPointNotFoundException)
                {
                    result = RecycleResult.Failed;
                }

                if (result == RecycleResult.Recycled)
                {
                    if (targetTab.Id == State.Id)
                    {
                        AnimateTileExit(tile);
                        _items.Remove(tile);
                        _selectedIds.Remove(tile.Mapping.Id);
                    }
                    FileMappingService.Remove(targetTab, tile.Mapping.Id);
                    recycled++;
                }
                else if (result == RecycleResult.Cancelled)
                {
                    cancelled = true;
                    break;
                }
                else
                {
                    failed++;
                }
            }
        }
        finally
        {
            _deletingOriginal = false;
        }

        if (recycled > 0)
        {
            if (targetTab.Id == State.Id)
            {
                ClearSelection();
                RefreshCount();
            }
            CaptureState();
            RefreshTabStripItems();
            NotifyStateChanged();
        }
        StatusText.Text = cancelled
            ? $"已取消，已移到回收站 {recycled} 项，其余未删除"
            : failed > 0
                ? $"已移到回收站 {recycled} 项；{failed} 项未能删除，可能正在使用或没有权限"
                : recycled > 0
                    ? $"已将 {recycled} 项移到回收站"
                    : "未能删除，文件可能正在使用或没有权限";
        return recycled;
    }

    private bool AskDeleteOriginals(IReadOnlyList<FileTile> tiles)
    {
        var names = string.Join("\n", tiles.Select(tile => $"· {tile.Name}").Take(8));
        if (tiles.Count > 8)
        {
            names += $"\n… 共 {tiles.Count} 项";
        }
        return System.Windows.MessageBox.Show(
                this,
                $"将 {tiles.Count} 个项目移到回收站吗？\n\n{names}\n\n删除的是原文件，可以从回收站恢复。",
                "删除到回收站",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private bool AskDeleteOriginal(FileTile tile)
    {
        var what = Directory.Exists(tile.Path)
            ? $"将文件夹“{tile.Name}”及其中所有内容移到回收站吗？"
            : $"将“{tile.Name}”移到回收站吗？";
        return System.Windows.MessageBox.Show(
                this,
                $"{what}\n\n位置：{tile.Path}\n\n删除的是原文件，可以从回收站恢复。",
                "删除到回收站",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void MoveMappingUp_Click(object sender, RoutedEventArgs e) =>
        MoveMapping(sender, -1);

    private void MoveMappingDown_Click(object sender, RoutedEventArgs e) =>
        MoveMapping(sender, 1);

    private void MoveMapping(object sender, int offset)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileTile tile)
        {
            return;
        }

        CaptureState();
        if (!FileMappingService.Move(State, tile.Mapping.Id, offset))
        {
            return;
        }

        ReloadItems();
        NotifyStateChanged();
    }

    private async void ReloadItems()
    {
        var previous = _items.ToDictionary(
            tile => tile.Mapping.Id,
            tile => (tile.Icon, tile.IsMissing));
        _items.Clear();
        var pendingProbe = new List<FileTile>();
        foreach (var item in State.Items.OrderBy(x => x.Order))
        {
            var known = previous.TryGetValue(item.Id, out var prev);
            var tile = CreateTile(item, known ? prev.Icon : null);
            if (known)
            {
                tile.IsMissing = prev.IsMissing;
            }
            if (!known || tile.Icon is null)
            {
                pendingProbe.Add(tile);
            }
            _items.Add(tile);
        }
        RefreshCount();
        if (pendingProbe.Count == 0)
        {
            return;
        }

        // Existence and missing icons are probed off the UI thread so sorting, moving or
        // relinking never blocks the window on offline network paths or shell icon extraction.
        await ProbeTilesAsync(pendingProbe, loadMissingIcons: true);
    }

    // Drop sources can hand over malformed paths; those are skipped instead of failing the drop.
    private static string? TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    internal async Task<int> AddMappingsAsync(
        IEnumerable<string> paths, Func<string, ImageSource?>? iconLoader = null) =>
        (await PrepareAndAddMappingsForTabAsync(State, paths, countInvalid: false, iconLoader)).Added;

    internal async Task<(int Added, int Invalid, int Total)> PrepareAndAddMappingsForTabAsync(
        FileBoxState targetTab,
        IEnumerable<string> paths,
        bool countInvalid,
        Func<string, ImageSource?>? iconLoader = null)
    {
        var knownPaths = (targetTab.Id == State.Id
                ? _items.Select(tile => tile.Path)
                : targetTab.Items.Select(item => item.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalized = paths.Select(TryGetFullPath).ToArray();
        var candidates = normalized
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => countInvalid || !knownPaths.Contains(path))
            .ToArray();
        if (candidates.Length == 0)
        {
            return (0, countInvalid ? normalized.Length : 0, normalized.Length);
        }

        var loadIcons = targetTab.Id == State.Id;
        var (existingCandidates, preparedIcons, invalid) = await Task.Run(() =>
        {
            var existing = candidates.Where(path => File.Exists(path) || Directory.Exists(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var invalidCount = countInvalid ? normalized.Count(path => path is null || !existing.Contains(path)) : 0;
            var newPaths = candidates.Where(path => existing.Contains(path) && !knownPaths.Contains(path)).ToArray();
            var icons = loadIcons ? LoadIcons(newPaths, iconLoader ?? LoadSharedIcon) : new Dictionary<string, ImageSource?>(StringComparer.OrdinalIgnoreCase);
            return (newPaths, icons, invalidCount);
        });
        if (existingCandidates.Length == 0 || _closeCts.IsCancellationRequested || !ContainsBox(targetTab.Id))
        {
            return (0, invalid, normalized.Length);
        }

        CaptureState();
        var existingIds = targetTab.Items.Select(item => item.Id).ToHashSet();
        var added = FileMappingService.AddPrepared(targetTab, existingCandidates);
        if (added == 0)
        {
            return (0, invalid, normalized.Length);
        }

        var newMappings = targetTab.Items
            .Where(item => !existingIds.Contains(item.Id))
            .OrderBy(item => item.Order)
            .ToList();
        if (targetTab.Id == State.Id)
        {
            var addedTiles = new List<FileTile>(newMappings.Count);
            foreach (var mapping in newMappings)
            {
                preparedIcons.TryGetValue(mapping.Path, out var icon);
                var tile = new FileTile(mapping, icon);
                _items.Add(tile);
                addedTiles.Add(tile);
            }
            RefreshCount();
            NotifyStateChanged();
            if (IsLoaded)
            {
                AnimateTileEnter(addedTiles);
            }
        }
        else
        {
            foreach (var mapping in newMappings)
            {
                if (preparedIcons.TryGetValue(mapping.Path, out var icon) && icon is not null)
                {
                    _tabIconCache[mapping.Id] = (icon, false);
                }
            }
            RefreshTabStripItems();
            NotifyStateChanged();
        }
        return (added, invalid, normalized.Length);
    }

    internal int RemoveGoneMappings(IReadOnlyDictionary<Guid, string> gonePaths)
    {
        CaptureState();
        var totalRemoved = 0;
        var activeRemoved = 0;
        foreach (var tab in _tabs)
        {
            var removed = FileMappingService.RemoveGone(tab, gonePaths);
            totalRemoved += removed;
            if (tab.Id == State.Id)
            {
                activeRemoved = removed;
            }
        }
        if (activeRemoved > 0)
        {
            ReloadItems();
            PruneSelection();
        }
        if (totalRemoved > 0)
        {
            RefreshTabStripItems();
            NotifyStateChanged();
        }
        return totalRemoved;
    }

    internal void SetOperationStatus(string message) => StatusText.Text = message;

    internal async Task<string> AddMappingsWithStatusAsync(
        IEnumerable<string> paths, Func<string, ImageSource?>? iconLoader = null) =>
        await AddMappingsWithStatusToBoxAsync(State.Id, paths, iconLoader);

    private void RefreshTile(FileTile tile)
    {
        _ = ProbeTilesAsync([tile], loadMissingIcons: tile.Icon is null);
    }

    private async Task ProbeTilesAsync(
        IReadOnlyList<FileTile> tiles,
        bool loadMissingIcons,
        bool forceReloadIcon = false)
    {
        if (tiles.Count == 0 || _closeCts.IsCancellationRequested)
        {
            return;
        }

        var token = _closeCts.Token;
        var snapshots = tiles
            .Select(tile => (Tile: tile, tile.Mapping, NeedIcon: forceReloadIcon || (loadMissingIcons && tile.Icon is null)))
            .ToArray();

        try
        {
            await ProbeLimiter.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        (FileTile Tile, bool IsMissing, ImageSource? Icon, bool HasIconUpdate)[] results;
        try
        {
            results = await Task.Run(() =>
            {
                var computed = new List<(FileTile, bool, ImageSource?, bool)>(snapshots.Length);
                foreach (var (tile, mapping, needIcon) in snapshots)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    var isMissing = FileMappingService.IsMissing(mapping);
                    ImageSource? icon = null;
                    if (needIcon)
                    {
                        try
                        {
                            icon = forceReloadIcon
                                ? ReloadSharedIcon(mapping.Path)
                                : LoadSharedIcon(mapping.Path);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            icon = null;
                        }
                    }
                    computed.Add((tile, isMissing, icon, needIcon));
                }
                return computed.ToArray();
            }, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            ProbeLimiter.Release();
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        foreach (var (tile, isMissing, icon, hasIconUpdate) in results)
        {
            tile.IsMissing = isMissing;
            if (hasIconUpdate)
            {
                tile.Icon = icon;
            }
        }
    }

    private static ImageSource? LoadSharedIcon(string path) =>
        LauncherIconCache.GetOrLoadSync(LauncherIconCache.KeyForFileBoxPath(path), () => ShellIconProvider.GetIcon(path));

    private static ImageSource? ReloadSharedIcon(string path)
    {
        var key = LauncherIconCache.KeyForFileBoxPath(path);
        LauncherIconCache.Remove(key);
        ShellIconProvider.InvalidateIconForPath(path);
        return LauncherIconCache.GetOrLoadSync(key, () => ShellIconProvider.GetIcon(path));
    }

    private static IReadOnlyDictionary<string, ImageSource?> LoadIcons(
        IEnumerable<string> paths, Func<string, ImageSource?> iconLoader)
    {
        var icons = new Dictionary<string, ImageSource?>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            try
            {
                icons[path] = iconLoader(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                icons[path] = null;
            }
        }
        return icons;
    }

    private async Task ApplySort(FileMappingSortMode mode)
    {
        CaptureState();
        var box = State;
        if (mode is not (FileMappingSortMode.Type or FileMappingSortMode.Modified))
        {
            _pendingSorts.Remove(box.Id);
            FileMappingService.Sort(box, mode);
            ReloadItems();
            NotifyStateChanged();
            return;
        }

        var revision = ++_sortRevision;
        _pendingSorts[box.Id] = revision;
        var snapshot = box.Items.Select(SortValues).ToArray();
        var token = _closeCts.Token;
        try
        {
            await ProbeLimiter.WaitAsync(token);
            Dictionary<Guid, int> order;
            try
            {
                if (_pendingSorts.GetValueOrDefault(box.Id) != revision)
                {
                    return;
                }

                order = await Task.Run(() =>
                {
                    var detached = new FileBoxState
                    {
                        Items = snapshot.Select(item => new FileMappingState
                        {
                            Id = item.Id,
                            Path = item.Path,
                            Title = item.Title,
                            Order = item.Order
                        }).ToList()
                    };
                    FileMappingService.Sort(detached, mode);
                    return detached.Items.ToDictionary(item => item.Id, item => item.Order);
                }, token);
            }
            finally
            {
                ProbeLimiter.Release();
            }

            if (token.IsCancellationRequested || _pendingSorts.GetValueOrDefault(box.Id) != revision || !_tabs.Contains(box))
            {
                return;
            }
            var isActiveTab = ReferenceEquals(State, box);
            if (isActiveTab)
            {
                CaptureState();
            }
            if (!box.Items.Select(SortValues).SequenceEqual(snapshot))
            {
                return;
            }
            foreach (var item in box.Items)
            {
                item.Order = order[item.Id];
            }
            if (isActiveTab)
            {
                ReloadItems();
            }
            NotifyStateChanged();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            if (_pendingSorts.GetValueOrDefault(box.Id) == revision)
            {
                _pendingSorts.Remove(box.Id);
            }
        }
    }

    private static (Guid Id, string Path, string? Title, int Order) SortValues(FileMappingState item) =>
        (item.Id, item.Path, item.Title, item.Order);

    private void AnimateTileEnter(IReadOnlyList<FileTile> tiles)
    {
        if (tiles.Count == 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!FileList.IsVisible)
            {
                return;
            }
            foreach (var tile in tiles.Take(8))
            {
                if (FileList.ItemContainerGenerator.ContainerFromItem(tile) is FrameworkElement visual)
                {
                    CardMotionAnimator.AnimateItemEnter(visual);
                }
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void AnimateTileExit(FileTile tile)
    {
        if (FileList.ItemContainerGenerator.ContainerFromItem(tile) is FrameworkElement visual)
        {
            CardMotionAnimator.AnimateItemExit(visual, FileBoxMotionOverlay);
        }
    }

    internal void RefreshCollapsedSummary()
    {
        CollapsedSummaryText.Text = FileBoxGroupRules.FormatCollapsedSummary(_items.Count, _tabs?.Count ?? 1);
    }

    private void RefreshCount()
    {
        CountText.Text = CardFoldSummaryRules.FormatFileBoxSummary(_items.Count);
        RefreshCollapsedSummary();
        RefreshTabStripItems();
        var isEmpty = _items.Count == 0;
        var becameEmpty = isEmpty && FileBoxEmptyState.Visibility != Visibility.Visible;
        FileBoxEmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        CountText.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (becameEmpty && IsLoaded)
        {
            CardMotionAnimator.AnimateEmptyStateReveal(FileBoxEmptyState);
        }
        else if (isEmpty)
        {
            FileBoxEmptyState.BeginAnimation(UIElement.OpacityProperty, null);
            FileBoxEmptyState.Opacity = 1d;
        }
    }

    private void HookAutoCollapseContextMenus()
    {
        System.Windows.Controls.ContextMenu?[] menus =
        [
            ContextMenu,
            AddButton.ContextMenu,
            SortButton.ContextMenu,
            TryFindResource("FileTileContextMenu") as System.Windows.Controls.ContextMenu,
            TryFindResource("FileTileBatchContextMenu") as System.Windows.Controls.ContextMenu,
            TryFindResource("FileBoxTabContextMenu") as System.Windows.Controls.ContextMenu
        ];
        foreach (var menu in menus)
        {
            if (menu is null)
            {
                continue;
            }
            menu.Opened += (_, _) =>
            {
                RefreshAutoCollapseTimer();
                RefreshHoverExpandTimer();
                RefreshHoverRevealState();
            };
            menu.Closed += (_, _) =>
            {
                RefreshAutoCollapseTimer();
                RefreshHoverExpandTimer();
                RefreshHoverRevealState();
            };
        }
    }

    private bool HasOpenContextMenu() =>
        ContextMenu is { IsOpen: true }
        || AddButton.ContextMenu is { IsOpen: true }
        || SortButton.ContextMenu is { IsOpen: true }
        || TryFindResource("FileTileContextMenu") is System.Windows.Controls.ContextMenu { IsOpen: true }
        || TryFindResource("FileTileBatchContextMenu") is System.Windows.Controls.ContextMenu { IsOpen: true }
        || TryFindResource("FileBoxTabContextMenu") is System.Windows.Controls.ContextMenu { IsOpen: true };

    private T RunModalInteraction<T>(Func<T> action)
    {
        _modalDialogDepth++;
        RefreshAutoCollapseTimer();
        RefreshHoverExpandTimer();
        RefreshHoverRevealState();
        try
        {
            return action();
        }
        finally
        {
            _modalDialogDepth = Math.Max(0, _modalDialogDepth - 1);
            RefreshAutoCollapseTimer();
            RefreshHoverExpandTimer();
            RefreshHoverRevealState();
        }
    }

    private void RefreshAutoCollapseTimer()
    {
        if (IsEdgeDocked)
        {
            _autoCollapseTimer.Stop();
            return;
        }
        var shouldRun = FileBoxAutoCollapseRules.ShouldRunCountdown(
            State.AutoCollapse,
            State.IsCollapsed,
            IsMouseOver,
            IsTitleEditing,
            _modalDialogDepth > 0 || _isTileOrFileDragActive || _externalDragOver || HasOpenContextMenu(),
            IsDraggingOrResizing,
            _pinnedOpenForSession);
        if (shouldRun)
        {
            _autoCollapseTimer.Stop();
            _autoCollapseTimer.Interval = FileBoxAutoCollapseRules.ResolveDelay(State.AutoCollapseDelaySeconds);
            _autoCollapseTimer.Start();
        }
        else
        {
            _autoCollapseTimer.Stop();
        }
    }

    private void OnAutoCollapseTimerTick()
    {
        _autoCollapseTimer.Stop();
        if (!FileBoxAutoCollapseRules.ShouldRunCountdown(
                State.AutoCollapse,
                State.IsCollapsed,
                IsMouseOver,
                IsTitleEditing,
                _modalDialogDepth > 0 || _isTileOrFileDragActive || _externalDragOver || HasOpenContextMenu(),
                IsDraggingOrResizing,
                _pinnedOpenForSession))
        {
            return;
        }

        SetCollapsed(true, animate: true);
        NotifyStateChanged();
    }

    internal void RefreshHoverExpandTimer()
    {
        var mouseButtonPressed = Mouse.LeftButton == MouseButtonState.Pressed
            || Mouse.RightButton == MouseButtonState.Pressed;
        var shouldRun = FileBoxAutoCollapseRules.ShouldRunHoverExpandTimer(
            State.HoverExpand,
            State.IsCollapsed,
            IsEdgeDocked,
            IsMouseOver,
            IsTitleEditing,
            _modalDialogDepth > 0 || _isTileOrFileDragActive || _externalDragOver || HasOpenContextMenu() || mouseButtonPressed,
            IsDraggingOrResizing,
            _suppressHoverExpandUntilLeave);
        if (shouldRun)
        {
            if (!_hoverExpandTimer.IsEnabled)
            {
                _hoverExpandTimer.Interval = FileBoxAutoCollapseRules.HoverExpandDelay;
                _hoverExpandTimer.Start();
            }
        }
        else
        {
            _hoverExpandTimer.Stop();
        }
    }

    private void OnHoverExpandTimerTick()
    {
        _hoverExpandTimer.Stop();
        var mouseButtonPressed = Mouse.LeftButton == MouseButtonState.Pressed
            || Mouse.RightButton == MouseButtonState.Pressed;
        if (!FileBoxAutoCollapseRules.ShouldRunHoverExpandTimer(
                State.HoverExpand,
                State.IsCollapsed,
                IsEdgeDocked,
                IsMouseOver,
                IsTitleEditing,
                _modalDialogDepth > 0 || _isTileOrFileDragActive || _externalDragOver || HasOpenContextMenu() || mouseButtonPressed,
                IsDraggingOrResizing,
                _suppressHoverExpandUntilLeave))
        {
            return;
        }

        SetCollapsed(false, animate: true);
        NotifyStateChanged();
    }

    private void FileBoxWindow_TitleBarPointerDown(object sender, MouseButtonEventArgs e)
    {
        _hoverExpandTimer.Stop();
        if (e.ClickCount != 1
            || !DockNativeMethods.GetCursorPos(out var screenPt))
        {
            _foldedTitlePressPoint = null;
            return;
        }
        TryBeginFoldedTitleBarClick(e.GetPosition(this), e.ClickCount, screenPt.X, screenPt.Y);
    }

    private void FileBoxWindow_TitleBarPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (_foldedTitlePressPoint is null
            || !DockNativeMethods.GetCursorPos(out var screenPt))
        {
            _foldedTitlePressPoint = null;
            RefreshHoverExpandTimer();
            return;
        }
        if (!CompleteFoldedTitleBarClick(screenPt.X, screenPt.Y))
        {
            RefreshHoverExpandTimer();
        }
    }

    internal bool TryBeginFoldedTitleBarClick(
        System.Windows.Point windowPoint,
        int clickCount,
        int screenX,
        int screenY)
    {
        _foldedTitlePressPoint = null;
        if (!State.AutoCollapse
            || !State.IsCollapsed
            || IsTitleEditing
            || clickCount != 1
            || TitleBarSurface.ActualHeight <= 0
            || windowPoint.X < 0
            || windowPoint.X > ActualWidth
            || windowPoint.Y < 0
            || windowPoint.Y > TitleBarSurface.ActualHeight
            || ResizeEdgesAt(windowPoint) != ResizeEdges.None
            || IsInteractive(windowPoint))
        {
            return false;
        }

        _foldedTitlePressPoint = (screenX, screenY);
        return true;
    }

    internal bool CompleteFoldedTitleBarClick(int screenX, int screenY)
    {
        if (_foldedTitlePressPoint is not { } press)
        {
            return false;
        }
        _foldedTitlePressPoint = null;

        if (!FileBoxAutoCollapseRules.CanClickTitleBarToExpand(
                State.AutoCollapse,
                State.IsCollapsed,
                IsTitleEditing,
                screenX - press.X,
                screenY - press.Y))
        {
            return false;
        }

        SetCollapsed(false, animate: true);
        NotifyStateChanged();
        return true;
    }

    public sealed class FileTile : INotifyPropertyChanged
    {
        public FileTile(FileMappingState mapping)
            : this(mapping, LoadSharedIcon(mapping.Path))
        {
            _isMissing = FileMappingService.IsMissing(mapping);
        }

        public FileTile(FileMappingState mapping, ImageSource? icon)
        {
            Mapping = mapping;
            Path = mapping.Path;
            IsShellEntry = FileMappingService.IsShellEntry(mapping.Path);
            _name = FileMappingService.GetDisplayName(mapping);
            _icon = icon;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public FileMappingState Mapping { get; }
        public string Path { get; }
        public bool IsShellEntry { get; }

        private string _name;
        public string Name => _name;

        internal void RefreshName()
        {
            var next = FileMappingService.GetDisplayName(Mapping);
            if (_name == next)
            {
                return;
            }
            _name = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }

        private bool _isMissing;
        public bool IsMissing
        {
            get => _isMissing;
            internal set
            {
                if (_isMissing == value)
                {
                    return;
                }
                _isMissing = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMissing)));
            }
        }

        private ImageSource? _icon;
        public ImageSource? Icon
        {
            get => _icon;
            internal set
            {
                if (ReferenceEquals(_icon, value))
                {
                    return;
                }
                _icon = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            }
        }

        private bool _isSelected;
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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
}
