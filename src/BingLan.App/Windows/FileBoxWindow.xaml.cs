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
    private readonly CardFoldAnimator _foldAnimator;
    private readonly System.Windows.Threading.DispatcherTimer _autoCollapseTimer = new()
    {
        Interval = FileBoxAutoCollapseRules.IdleDelay
    };
    private readonly System.Windows.Threading.DispatcherTimer _dragExpandTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(220)
    };
    private Guid? _selectionAnchor;
    private bool _initialIconLoadStarted;
    private int _modalDialogDepth;
    private bool _isTileOrFileDragActive;
    private bool _externalDragOver;
    private bool _expandedForExternalDrag;
    private bool _pinnedOpenForSession;
    private (int X, int Y)? _foldedTitlePressPoint;

    internal event Action? DragOutStarted;
    internal event Action? DragOutEnded;
    internal event Action? ItemOpened;
    internal event Action? PeekEscapeRequested;

    public FileBoxWindow(FileBoxState state)
    {
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
        RefreshCount();
        if (state.IsCollapsed)
        {
            SetCollapsed(true);
        }
        RefreshPinOpenButton();
        _autoCollapseTimer.Tick += (_, _) => OnAutoCollapseTimerTick();
        _dragExpandTimer.Tick += (_, _) => OnDragExpandTimerTick();
        HookAutoCollapseContextMenus();
        MouseEnter += (_, _) => RefreshAutoCollapseTimer();
        MouseLeave += (_, _) => RefreshAutoCollapseTimer();
        DragEnter += (_, e) =>
        {
            _externalDragOver = true;
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
        };
        Loaded += FileBoxWindow_Loaded;
        PreviewKeyDown += FileBoxWindow_PreviewKeyDown;
        PreviewMouseLeftButtonDown += FileBoxWindow_BackgroundPointerDown;
        PreviewMouseLeftButtonDown += FileBoxWindow_TitleBarPointerDown;
        PreviewMouseLeftButtonUp += FileBoxWindow_TitleBarPointerUp;
    }

    public FileBoxState State { get; }

    internal bool IsFoldAnimating => _foldAnimator.IsAnimating;

    internal bool InitialProbeCompleted { get; private set; }

    internal bool IsAutoCollapseTimerRunning => _autoCollapseTimer.IsEnabled;

    internal bool IsEdgeDocked => State.EdgeDockSide is not null;

    internal bool IsEdgeDockExpanded { get; private set; }

    internal bool IsEdgeDockInteractionBusy =>
        IsTitleEditing
        || HasOpenContextMenu()
        || _modalDialogDepth > 0
        || _isTileOrFileDragActive
        || _externalDragOver
        || IsDraggingOrResizing;

    internal event Action<FileBoxWindow, bool>? EdgeDockToggled;

    internal event Action<FileBoxWindow>? EdgeDockFileDragNear;

    protected override void PopulateContextMenu(System.Windows.Controls.ContextMenu menu)
    {
        var edgeDock = new System.Windows.Controls.MenuItem
        {
            Header = "贴边隐藏",
            IsCheckable = true
        };
        edgeDock.Click += (_, _) => EdgeDockToggled?.Invoke(this, edgeDock.IsChecked);
        menu.Opened += (_, _) => edgeDock.IsChecked = IsEdgeDocked;
        menu.Items.Add(edgeDock);
    }

    protected override bool SuppressLayeredClip => IsEdgeDocked && !IsEdgeDockExpanded;

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
        IsEdgeDockExpanded = false;
        _autoCollapseTimer.Stop();
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
        IsEdgeDockExpanded = true;
        _autoCollapseTimer.Stop();
        if (State.IsCollapsed)
        {
            SetCollapsed(false, animate: false);
        }
        EdgeDockTab.Visibility = Visibility.Collapsed;
        BodySurface.Visibility = Visibility.Visible;
        BoxLayout.Visibility = Visibility.Visible;
        MinWidth = FileBoxEdgeDockRules.MinExpandedWidthDip;
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

    internal void ExitEdgeDockedVisual(BingLan.Core.Dock.PixelRect restoredBounds)
    {
        IsEdgeDockExpanded = false;
        EdgeDockSlideTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        EdgeDockSlideTransform.X = 0;
        BodySurface.BeginAnimation(OpacityProperty, null);
        BodySurface.Opacity = 1;
        EdgeDockTab.Visibility = Visibility.Collapsed;
        BodySurface.Visibility = Visibility.Visible;
        BoxLayout.Visibility = Visibility.Visible;
        MinWidth = FileBoxEdgeDockRules.MinExpandedWidthDip;
        MinHeight = State.IsCollapsed ? CollapsedHeight : FileBoxEdgeDockRules.MinExpandedHeightDip;
        MaxHeight = State.IsCollapsed ? CollapsedHeight : double.PositiveInfinity;
        // Crossing to a monitor with different DPI triggers WM_DPICHANGED resizing on the first
        // move; a second pass locks the exact restored size.
        UpdateEdgeDockPosition(restoredBounds);
        UpdateEdgeDockPosition(restoredBounds);
        RefreshLayeredClip();
        RefreshAutoCollapseTimer();
    }

    internal void UpdateEdgeDockPosition(BingLan.Core.Dock.PixelRect bounds)
    {
        if (WindowHandle is var handle && handle != 0 && bounds.HasArea)
        {
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

    public void SetAutoCollapse(bool enabled)
    {
        if (State.AutoCollapse == enabled)
        {
            RefreshPinOpenButton();
            RefreshAutoCollapseTimer();
            return;
        }
        State.AutoCollapse = enabled;
        if (!enabled)
        {
            _pinnedOpenForSession = false;
        }
        RefreshPinOpenButton();
        RefreshAutoCollapseTimer();
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

    protected override void OnClosed(EventArgs e)
    {
        _autoCollapseTimer.Stop();
        _dragExpandTimer.Stop();
        _closeCts.Cancel();
        _closeCts.Dispose();
        base.OnClosed(e);
    }

    protected override void OnTitleEditingChanged(bool isEditing) => RefreshAutoCollapseTimer();

    protected override void OnDragEnded() => RefreshAutoCollapseTimer();

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
    }

    private const double CollapsedHeight = 48d;
    private const double DefaultMinHeight = 166d;

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        SetCollapsed(!State.IsCollapsed, animate: true);
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
            State.ExpandedHeight = Height;
        }
        if (collapsed)
        {
            _pinnedOpenForSession = false;
        }
        State.IsCollapsed = collapsed;
        CollapseButton.Content = collapsed ? "\uE70D" : "\uE70E";
        CollapseButton.ToolTip = collapsed ? "展开文件收纳盒" : "折叠成标题条";
        AddButton.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SortButton.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapsedSummaryText.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        RefreshPinOpenButton();
        RefreshCollapsedSummary();

        var bodyElements = FoldBodyElements();
        if (collapsed)
        {
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
            Height = Math.Max(DefaultMinHeight, State.ExpandedHeight > 0 ? State.ExpandedHeight : 306d);
            // 展开向下长高，等原生矩形反映新尺寸后把卡片整体收回屏幕内。
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
        RefreshAutoCollapseTimer();
    }

    private async void FileBoxWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (State.IsCollapsed)
        {
            SetCollapsed(true);
        }
        RefreshAutoCollapseTimer();
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
            var added = await AddMappingsAsync(paths);
            StatusText.Text = added > 0
                ? $"已新增 {added} 项"
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

    private void SortManual_Click(object sender, RoutedEventArgs e) =>
        ApplySort(FileMappingSortMode.Manual);

    private void SortName_Click(object sender, RoutedEventArgs e) =>
        ApplySort(FileMappingSortMode.Name);

    private void SortType_Click(object sender, RoutedEventArgs e) =>
        ApplySort(FileMappingSortMode.Type);

    private void SortModified_Click(object sender, RoutedEventArgs e) =>
        ApplySort(FileMappingSortMode.Modified);

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
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
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
        _externalDragOver = false;
        _dragExpandTimer.Stop();
        var restoreCollapse = _expandedForExternalDrag;
        _expandedForExternalDrag = false;
        RefreshAutoCollapseTimer();
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
        {
            await AddMappingsWithStatusAsync(paths);
        }
        if (restoreCollapse && !State.IsCollapsed)
        {
            SetCollapsed(true, animate: true);
        }
    }

    private System.Windows.Point? _tileDragStart;

    // A tile can be dragged out, for example onto the dock to pin an app. Only a link is
    // offered, so a drop elsewhere creates a shortcut and never copies or moves the file.
    // When the dragged tile belongs to a multi-selection, every selected tile that can be
    // dragged leaves with it, like dragging several files at once in Explorer.
    private void FileTile_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_tileDragStart is not { } start
            || e.LeftButton != MouseButtonState.Pressed
            || sender is not Button { DataContext: FileTile tile } button
            || tile.IsShellEntry
            || tile.IsMissing)
        {
            return;
        }

        var offset = e.GetPosition(this) - start;
        if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var paths = tile.IsSelected
            ? _items
                .Where(item => item.IsSelected && !item.IsShellEntry && !item.IsMissing)
                .Select(item => item.Path)
                .Append(tile.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [tile.Path];

        _tileDragStart = null;
        _singleClickFileTiles.Remove(button);
        _isTileOrFileDragActive = true;
        RefreshAutoCollapseTimer();
        DragOutStarted?.Invoke();
        try
        {
            DragDrop.DoDragDrop(
                button,
                new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, paths),
                // Copy lets a folder or chat window receive the file itself; Move is never
                // offered, so dragging out cannot take the original away.
                System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Link);
        }
        finally
        {
            _isTileOrFileDragActive = false;
            RefreshAutoCollapseTimer();
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
                var others = OtherBoxes();
                item.Items.Clear();
                foreach (var other in others)
                {
                    var target = new System.Windows.Controls.MenuItem { Header = other.State.Title };
                    target.Click += (_, _) => TransferMapping(tile, other);
                    item.Items.Add(target);
                }
                item.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private void RenameMapping_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileTile tile)
        {
            return;
        }

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
    internal void MoveSystemPlacesTo(FileBoxWindow target)
    {
        var places = new[] { KnownFolders.GetDownloadsPath(), DesktopFolderPath() };
        foreach (var tile in _items
                     .Where(tile => tile.IsShellEntry
                         || places.Any(place => string.Equals(place, tile.Path, StringComparison.OrdinalIgnoreCase)))
                     .ToList())
        {
            TransferMapping(tile, target);
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
        var (icon, isMissing) = await Task.Run(() => (
            ShellIconProvider.GetIcon(path),
            !FileMappingService.IsShellEntry(path) && !File.Exists(path) && !Directory.Exists(path)));
        CaptureState();
        var added = FileMappingService.AddSystemEntry(State, path, title);
        if (added == 0)
        {
            StatusText.Text = "该系统入口已存在";
            return false;
        }

        var mapping = State.Items.Single(item => item.Path == path);
        var tile = new FileTile(mapping, icon) { IsMissing = isMissing };
        _items.Add(tile);
        RefreshCount();
        NotifyStateChanged();
        if (IsLoaded)
        {
            AnimateTileEnter([tile]);
        }
        StatusText.Text = $"已添加“{title}”";
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
                    var others = OtherBoxes();
                    item.Items.Clear();
                    foreach (var other in others)
                    {
                        var target = new System.Windows.Controls.MenuItem { Header = other.State.Title };
                        target.Click += (_, _) => BatchTransferMappings(other, selected);
                        item.Items.Add(target);
                    }
                    item.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private void BatchTransferMappings(FileBoxWindow target, IReadOnlyList<FileTile> selected)
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

        AnimateTileExit(tile);
        _items.Remove(tile);
        _selectedIds.Remove(tile.Mapping.Id);
        FileMappingService.Remove(State, tile.Mapping.Id);
        CaptureState();
        RefreshCount();
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
                    AnimateTileExit(tile);
                    _items.Remove(tile);
                    _selectedIds.Remove(tile.Mapping.Id);
                    FileMappingService.Remove(State, tile.Mapping.Id);
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
            ClearSelection();
            CaptureState();
            RefreshCount();
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
        (await PrepareAndAddMappingsAsync(paths, countInvalid: false, iconLoader)).Added;

    private async Task<(int Added, int Invalid, int Total)> PrepareAndAddMappingsAsync(
        IEnumerable<string> paths, bool countInvalid, Func<string, ImageSource?>? iconLoader = null)
    {
        var knownPaths = _items.Select(tile => tile.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
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

        var (existingCandidates, preparedIcons, invalid) = await Task.Run(() =>
        {
            var existing = candidates.Where(path => File.Exists(path) || Directory.Exists(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var invalidCount = countInvalid ? normalized.Count(path => path is null || !existing.Contains(path)) : 0;
            var newPaths = candidates.Where(path => existing.Contains(path) && !knownPaths.Contains(path)).ToArray();
            return (newPaths, LoadIcons(newPaths, iconLoader ?? ShellIconProvider.GetIcon), invalidCount);
        });
        if (existingCandidates.Length == 0 || _closeCts.IsCancellationRequested)
        {
            return (0, invalid, normalized.Length);
        }

        CaptureState();
        var existingIds = State.Items.Select(item => item.Id).ToHashSet();
        var added = FileMappingService.AddPrepared(State, existingCandidates);
        if (added == 0)
        {
            return (0, invalid, normalized.Length);
        }

        var addedTiles = new List<FileTile>(added);
        foreach (var mapping in State.Items
                     .Where(item => !existingIds.Contains(item.Id))
                     .OrderBy(item => item.Order))
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
        return (added, invalid, normalized.Length);
    }

    internal int RemoveGoneMappings(IReadOnlySet<Guid> goneIds)
    {
        CaptureState();
        var removed = FileMappingService.RemoveGone(State, goneIds);
        if (removed > 0)
        {
            ReloadItems();
            PruneSelection();
            NotifyStateChanged();
        }
        return removed;
    }

    internal void SetOperationStatus(string message) => StatusText.Text = message;

    internal async Task<string> AddMappingsWithStatusAsync(
        IEnumerable<string> paths, Func<string, ImageSource?>? iconLoader = null)
    {
        var (added, invalid, total) = await PrepareAndAddMappingsAsync(paths, countInvalid: true, iconLoader);
        var skipped = invalid > 0 ? $"{invalid} 项路径无效，已跳过" : string.Empty;
        StatusText.Text = added > 0
            ? skipped.Length > 0 ? $"已新增 {added} 项；{skipped}" : $"已新增 {added} 项"
            : skipped.Length > 0 && invalid == total ? skipped : "这些项目已经在文件收纳盒中" + (skipped.Length > 0 ? $"；{skipped}" : string.Empty);
        return StatusText.Text;
    }

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
                            icon = ShellIconProvider.GetIcon(mapping.Path);
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

    private void ApplySort(FileMappingSortMode mode)
    {
        CaptureState();
        FileMappingService.Sort(State, mode);
        ReloadItems();
        NotifyStateChanged();
    }

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
        CollapsedSummaryText.Text = CardFoldSummaryRules.FormatFileBoxSummary(_items.Count);
    }

    private void RefreshCount()
    {
        CountText.Text = CardFoldSummaryRules.FormatFileBoxSummary(_items.Count);
        RefreshCollapsedSummary();
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
            TryFindResource("FileTileBatchContextMenu") as System.Windows.Controls.ContextMenu
        ];
        foreach (var menu in menus)
        {
            if (menu is null)
            {
                continue;
            }
            menu.Opened += (_, _) => RefreshAutoCollapseTimer();
            menu.Closed += (_, _) => RefreshAutoCollapseTimer();
        }
    }

    private bool HasOpenContextMenu() =>
        ContextMenu is { IsOpen: true }
        || AddButton.ContextMenu is { IsOpen: true }
        || SortButton.ContextMenu is { IsOpen: true }
        || TryFindResource("FileTileContextMenu") is System.Windows.Controls.ContextMenu { IsOpen: true }
        || TryFindResource("FileTileBatchContextMenu") is System.Windows.Controls.ContextMenu { IsOpen: true };

    private T RunModalInteraction<T>(Func<T> action)
    {
        _modalDialogDepth++;
        RefreshAutoCollapseTimer();
        try
        {
            return action();
        }
        finally
        {
            _modalDialogDepth = Math.Max(0, _modalDialogDepth - 1);
            RefreshAutoCollapseTimer();
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

    private void FileBoxWindow_TitleBarPointerDown(object sender, MouseButtonEventArgs e)
    {
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
            return;
        }
        CompleteFoldedTitleBarClick(screenPt.X, screenPt.Y);
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
            : this(mapping, ShellIconProvider.GetIcon(mapping.Path))
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
