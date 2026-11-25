using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BingLan.App.Services;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Services;

namespace BingLan.App.Windows;

public partial class FileBoxWindow
{
    private readonly List<FileBoxState> _tabs;
    private readonly ObservableCollection<FileBoxTabItem> _tabItems = [];
    // Keeps icons warm across tab switches so flipping tabs inside a grouped box feels instant.
    private readonly Dictionary<Guid, (ImageSource? Icon, bool IsMissing)> _tabIconCache = [];
    private readonly System.Windows.Threading.DispatcherTimer _tabHoverSwitchTimer = new()
    {
        Interval = FileBoxGroupRules.HoverSwitchTabDelay
    };
    private readonly System.Windows.Threading.DispatcherTimer _tabDragSwitchTimer = new()
    {
        Interval = FileBoxGroupRules.DragSwitchTabDelay
    };
    private Guid? _pendingHoverTabId;
    private Guid? _pendingDragTabId;

    internal IReadOnlyList<FileBoxState> Tabs => _tabs;

    internal bool IsGrouped => _tabs.Count > 1;

    internal bool ContainsBox(Guid boxId) => _tabs.Any(tab => tab.Id == boxId);

    internal FileBoxState? FindTab(Guid boxId) => _tabs.FirstOrDefault(tab => tab.Id == boxId);

    internal event Action<FileBoxWindow>? ActiveTabChanged;

    internal event Action<FileBoxWindow, FileBoxWindow>? MergeRequested;

    internal event Action<FileBoxWindow, FileBoxState>? DetachTabRequested;

    internal event Action<FileBoxWindow>? SplitGroupRequested;

    internal event Action<FileBoxWindow, FileBoxState>? DeleteTabRequested;

    internal event Action<FileBoxWindow, PixelRect, bool>? DragMoveEvaluated;

    internal event Action<FileBoxWindow, bool, bool>? DragMoveFinished;

    private void InitializeGroups()
    {
        TabStripList.ItemsSource = _tabItems;
        _tabHoverSwitchTimer.Tick += (_, _) => OnTabHoverSwitchTick();
        _tabDragSwitchTimer.Tick += (_, _) => OnTabDragSwitchTick();
        RefreshTabStripLayout();
    }

    private void DisposeGroups()
    {
        _tabHoverSwitchTimer.Stop();
        _tabDragSwitchTimer.Stop();
    }

    protected override void OnDragMoved(PixelRect bounds, bool altHeld)
    {
        base.OnDragMoved(bounds, altHeld);
        DragMoveEvaluated?.Invoke(this, bounds, altHeld);
    }

    protected override void OnDragMoveEnded(bool wasMoving, bool altHeld)
    {
        base.OnDragMoveEnded(wasMoving, altHeld);
        DragMoveFinished?.Invoke(this, wasMoving, altHeld);
    }

    internal void SetMergeDropHighlight(bool visible)
    {
        if (_isMergeHighlightVisible == visible)
        {
            return;
        }

        _isMergeHighlightVisible = visible;
        if (MergeDropOverlay is not null)
        {
            MergeDropOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        RefreshHoverRevealState();
    }

    internal void SetHoverSwitchTabs(bool enabled)
    {
        if (State.HoverSwitchTabs == enabled)
        {
            return;
        }

        State.HoverSwitchTabs = enabled;
        SyncGroupSharedProperties();
        NotifyStateChanged();
    }

    internal void SyncGroupSharedProperties()
    {
        if (_tabs is null || _tabs.Count <= 1)
        {
            return;
        }

        FileBoxGroupRules.SyncGroupSharedProperties(State, _tabs);
    }

    internal void SyncTabsFromStates(IReadOnlyList<FileBoxState> members, Guid? activateBoxId = null)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count == 0)
        {
            return;
        }

        CacheCurrentTabIcons();
        _tabs.Clear();
        _tabs.AddRange(members.OrderBy(m => m.TabOrder));

        var nextActive = (activateBoxId is { } id ? _tabs.FirstOrDefault(m => m.Id == id) : null)
            ?? _tabs.FirstOrDefault(m => m.IsActiveTab)
            ?? _tabs[0];

        var switched = nextActive.Id != State.Id;
        foreach (var tab in _tabs)
        {
            tab.IsActiveTab = _tabs.Count > 1 && tab.Id == nextActive.Id;
        }

        State = nextActive;
        DataContext = State;
        RebindPlacementReference(State.Placement);
        TitleEditor.Text = State.Title;
        ApplyAppearance(State.Appearance);
        WidgetCornerRadius = State.CornerRadius;
        ApplyViewMode();

        if (switched)
        {
            ClearSelection();
            ReloadItemsFromCache();
        }

        RefreshTabStripLayout();
        RefreshCount();
        RefreshEdgeDockTabToolTip();
        RefreshPinOpenButton();
        RefreshAutoCollapseTimer();
        RefreshHoverRevealState();
        NotifyTitleChanged();
        ActiveTabChanged?.Invoke(this);
    }

    internal bool SwitchActiveTab(Guid boxId)
    {
        var next = _tabs.FirstOrDefault(tab => tab.Id == boxId);
        if (next is null || next.Id == State.Id)
        {
            return false;
        }

        if (IsTitleEditing)
        {
            EndTitleEdit(commit: true);
        }

        CaptureState();
        CacheCurrentTabIcons();
        ClearSelection();

        foreach (var tab in _tabs)
        {
            tab.IsActiveTab = tab.Id == next.Id;
        }

        State = next;
        DataContext = State;
        RebindPlacementReference(State.Placement);
        TitleEditor.Text = State.Title;
        ApplyViewMode();
        ReloadItemsFromCache();
        RefreshTabStripItems();
        RefreshCount();
        RefreshEdgeDockTabToolTip();
        StatusText.Text = string.Empty;
        NotifyTitleChanged();
        ActiveTabChanged?.Invoke(this);
        NotifyStateChanged();
        return true;
    }

    internal void RefreshTabStripLayout()
    {
        if (TabStripHost is null)
        {
            return;
        }

        var showStrip = IsGrouped && !State.IsCollapsed && !(IsEdgeDocked && !IsEdgeDockExpanded);
        TabStripHost.Visibility = showStrip ? Visibility.Visible : Visibility.Collapsed;

        if (!(IsEdgeDocked && !IsEdgeDockExpanded))
        {
            var requiredMinWidth = IsGrouped
                ? FileBoxGroupRules.GroupedMinWidthDip
                : FileBoxGroupRules.StandaloneMinWidthDip;
            MinWidth = requiredMinWidth;
            if (!double.IsNaN(Width) && Width > 0 && Width < requiredMinWidth)
            {
                Width = requiredMinWidth;
            }
        }

        RefreshTabStripItems();
    }

    internal void RefreshTabStripItems()
    {
        if (_tabs is null)
        {
            return;
        }

        if (!IsGrouped)
        {
            if (_tabItems.Count > 0)
            {
                _tabItems.Clear();
            }
            return;
        }

        var ordered = _tabs.OrderBy(t => t.TabOrder).ToList();
        var sameOrder = _tabItems.Count == ordered.Count;
        if (sameOrder)
        {
            for (var i = 0; i < ordered.Count; i++)
            {
                if (_tabItems[i].Box.Id != ordered[i].Id)
                {
                    sameOrder = false;
                    break;
                }
            }
        }

        // Update in place whenever the tab order is unchanged so WPF keeps each tab button
        // container alive and keyboard focus is preserved across tab switches.
        if (sameOrder)
        {
            for (var i = 0; i < ordered.Count; i++)
            {
                var tab = ordered[i];
                var count = tab.Id == State.Id ? _items.Count : tab.Items.Count;
                _tabItems[i].Update(tab.Id == State.Id, count);
            }
            return;
        }

        _tabItems.Clear();
        foreach (var tab in ordered)
        {
            var count = tab.Id == State.Id ? _items.Count : tab.Items.Count;
            _tabItems.Add(new FileBoxTabItem(tab, tab.Id == State.Id, count));
        }
    }

    private void CacheCurrentTabIcons()
    {
        foreach (var tile in _items)
        {
            _tabIconCache[tile.Mapping.Id] = (tile.Icon, tile.IsMissing);
        }
    }

    private async void ReloadItemsFromCache()
    {
        _items.Clear();
        var pendingProbe = new List<FileTile>();
        foreach (var item in State.Items.OrderBy(x => x.Order))
        {
            var cached = _tabIconCache.TryGetValue(item.Id, out var entry);
            var tile = CreateTile(item, cached ? entry.Icon : null);
            if (cached)
            {
                tile.IsMissing = entry.IsMissing;
            }
            if (!cached || tile.Icon is null)
            {
                pendingProbe.Add(tile);
            }
            _items.Add(tile);
        }
        RefreshCount();
        if (pendingProbe.Count > 0)
        {
            await ProbeTilesAsync(pendingProbe, loadMissingIcons: true);
        }
    }

    internal async Task<int> AddMappingsToBoxAsync(
        Guid boxId,
        IEnumerable<string> paths,
        Func<string, ImageSource?>? iconLoader = null)
    {
        var targetTab = FindTab(boxId);
        if (targetTab is null)
        {
            return 0;
        }

        return (await PrepareAndAddMappingsForTabAsync(targetTab, paths, countInvalid: false, iconLoader)).Added;
    }

    internal async Task<string> AddMappingsWithStatusToBoxAsync(
        Guid boxId,
        IEnumerable<string> paths,
        Func<string, ImageSource?>? iconLoader = null)
    {
        var targetTab = FindTab(boxId);
        if (targetTab is null)
        {
            return "该文件收纳盒已移除。";
        }

        var (added, invalid, total) = await PrepareAndAddMappingsForTabAsync(targetTab, paths, countInvalid: true, iconLoader);
        var skipped = invalid > 0 ? $"{invalid} 项路径无效，已跳过" : string.Empty;
        var isActive = targetTab.Id == State.Id;
        var message = added > 0
            ? isActive
                ? (skipped.Length > 0 ? $"已新增 {added} 项；{skipped}" : $"已新增 {added} 项")
                : (skipped.Length > 0 ? $"已添加到“{targetTab.Title}”：{added} 项（{skipped}）" : $"已添加到“{targetTab.Title}”：{added} 项")
            : skipped.Length > 0 && invalid == total
                ? skipped
                : (isActive ? "这些项目已经在文件收纳盒中" : $"这些项目已经在“{targetTab.Title}”中")
                  + (skipped.Length > 0 ? $"；{skipped}" : string.Empty);
        StatusText.Text = message;
        return message;
    }

    internal (FileBoxWindow Window, FileBoxState Tab)? ResolveBoxAndTab(Guid boxId)
    {
        if (FindTab(boxId) is { } localTab)
        {
            return (this, localTab);
        }

        foreach (var other in OtherBoxes())
        {
            if (other.FindTab(boxId) is { } remoteTab)
            {
                return (other, remoteTab);
            }
        }

        return null;
    }

    internal (int Moved, int Skipped) TransferMappingIdsBetweenTabs(
        FileBoxWindow sourceWindow,
        FileBoxState sourceTab,
        FileBoxWindow targetWindow,
        FileBoxState targetTab,
        IReadOnlyList<Guid> mappingIds,
        bool copyOnly = false)
    {
        if (mappingIds.Count == 0 || sourceTab.Id == targetTab.Id)
        {
            return (0, 0);
        }

        sourceWindow.CaptureState();
        if (!ReferenceEquals(targetWindow, sourceWindow))
        {
            targetWindow.CaptureState();
        }

        var idSet = mappingIds.ToHashSet();
        var sourceIsActive = sourceWindow.State.Id == sourceTab.Id;
        var targetIsActive = targetWindow.State.Id == targetTab.Id;

        var sourceMappings = sourceTab.Items
            .Where(item => idSet.Contains(item.Id))
            .OrderBy(item => item.Order)
            .ToList();
        if (sourceMappings.Count == 0)
        {
            return (0, 0);
        }

        var moved = 0;
        var skipped = 0;
        foreach (var mapping in sourceMappings)
        {
            FileTile? activeSourceTile = sourceIsActive
                ? sourceWindow._items.FirstOrDefault(tile => tile.Mapping.Id == mapping.Id)
                : null;
            ImageSource? cachedIcon = activeSourceTile?.Icon;
            var isMissing = activeSourceTile?.IsMissing ?? false;
            if (activeSourceTile is null && sourceWindow._tabIconCache.TryGetValue(mapping.Id, out var cached))
            {
                cachedIcon = cached.Icon;
                isMissing = cached.IsMissing;
            }

            if (copyOnly)
            {
                if (!FileMappingService.CopyMapping(sourceTab, targetTab, mapping.Id, out var clone) || clone is null)
                {
                    skipped++;
                    continue;
                }

                if (targetIsActive)
                {
                    var copiedTile = targetWindow.CreateTile(clone, cachedIcon);
                    copiedTile.IsMissing = isMissing;
                    targetWindow._items.Add(copiedTile);
                }
                else
                {
                    targetWindow._tabIconCache[clone.Id] = (cachedIcon, isMissing);
                }
                moved++;
            }
            else
            {
                if (!FileMappingService.Transfer(sourceTab, targetTab, mapping.Id))
                {
                    skipped++;
                    continue;
                }

                if (activeSourceTile is not null)
                {
                    sourceWindow._items.Remove(activeSourceTile);
                    sourceWindow._selectedIds.Remove(mapping.Id);
                }
                if (targetIsActive)
                {
                    var transferred = targetWindow.CreateTile(mapping, cachedIcon);
                    transferred.IsMissing = isMissing;
                    targetWindow._items.Add(transferred);
                }
                else
                {
                    targetWindow._tabIconCache[mapping.Id] = (cachedIcon, isMissing);
                }
                moved++;
            }
        }

        if (!copyOnly && sourceIsActive)
        {
            sourceWindow.ClearSelection();
        }
        if (sourceIsActive)
        {
            sourceWindow.RefreshCount();
        }
        sourceWindow.RefreshTabStripItems();
        sourceWindow.NotifyStateChanged();

        if (!ReferenceEquals(targetWindow, sourceWindow))
        {
            if (targetIsActive)
            {
                targetWindow.RefreshCount();
            }
            targetWindow.RefreshTabStripItems();
            targetWindow.NotifyStateChanged();
        }
        else if (targetIsActive && !sourceIsActive)
        {
            targetWindow.RefreshCount();
        }

        targetWindow.StatusText.Text = moved > 0
            ? copyOnly
                ? (skipped > 0
                    ? $"已从“{sourceTab.Title}”复制 {moved} 项映射（跳过 {skipped} 项重复）"
                    : $"已从“{sourceTab.Title}”复制 {moved} 项映射")
                : (skipped > 0
                    ? $"已从“{sourceTab.Title}”转入 {moved} 项（跳过 {skipped} 项重复）"
                    : $"已从“{sourceTab.Title}”转入 {moved} 项")
            : "所选项目已在此收纳盒中";
        return (moved, skipped);
    }

    internal (int Moved, int Skipped) TransferTilesToTab(
        FileBoxWindow targetWindow,
        FileBoxState targetTab,
        IReadOnlyList<FileTile> selected,
        bool copyOnly = false)
    {
        if (selected.Count == 0 || targetTab.Id == State.Id)
        {
            return (0, 0);
        }

        CaptureState();
        targetWindow.CaptureState();
        var moved = 0;
        var skipped = 0;
        var targetIsActive = targetWindow.State.Id == targetTab.Id;
        foreach (var tile in selected)
        {
            if (copyOnly)
            {
                if (!FileMappingService.CopyMapping(State, targetTab, tile.Mapping.Id, out var clone) || clone is null)
                {
                    skipped++;
                    continue;
                }
                if (targetIsActive)
                {
                    var copiedTile = targetWindow.CreateTile(clone, tile.Icon);
                    copiedTile.IsMissing = tile.IsMissing;
                    targetWindow._items.Add(copiedTile);
                }
                else
                {
                    targetWindow._tabIconCache[clone.Id] = (tile.Icon, tile.IsMissing);
                }
                moved++;
            }
            else
            {
                if (!FileMappingService.Transfer(State, targetTab, tile.Mapping.Id))
                {
                    skipped++;
                    continue;
                }

                _items.Remove(tile);
                _selectedIds.Remove(tile.Mapping.Id);
                if (targetIsActive)
                {
                    var transferred = targetWindow.CreateTile(tile.Mapping, tile.Icon);
                    transferred.IsMissing = tile.IsMissing;
                    targetWindow._items.Add(transferred);
                }
                else
                {
                    targetWindow._tabIconCache[tile.Mapping.Id] = (tile.Icon, tile.IsMissing);
                }
                moved++;
            }
        }

        if (!copyOnly)
        {
            ClearSelection();
        }
        RefreshCount();
        RefreshTabStripItems();
        NotifyStateChanged();
        if (!ReferenceEquals(targetWindow, this))
        {
            if (targetIsActive)
            {
                targetWindow.RefreshCount();
            }
            targetWindow.RefreshTabStripItems();
            targetWindow.NotifyStateChanged();
        }

        var verb = copyOnly ? "复制" : "转移";
        StatusText.Text = moved > 0
            ? (skipped > 0
                ? $"已{verb} {moved} 项；{skipped} 项已存在于“{targetTab.Title}”"
                : $"已{verb} {moved} 项到“{targetTab.Title}”")
            : $"“{targetTab.Title}”中已有这些项目";
        return (moved, skipped);
    }

    private void PopulateTransferTargets(System.Windows.Controls.MenuItem parentItem, IReadOnlyList<FileTile> tiles)
    {
        parentItem.Items.Clear();

        // Sibling tabs inside this grouped box come first.
        foreach (var tab in _tabs.OrderBy(t => t.TabOrder))
        {
            if (tab.Id == State.Id)
            {
                continue;
            }
            var capturedTab = tab;
            var target = new System.Windows.Controls.MenuItem { Header = $"{capturedTab.Title}（本组合）" };
            target.Click += (_, _) => TransferTilesToTab(this, capturedTab, tiles);
            parentItem.Items.Add(target);
        }

        // Then tabs/boxes from other open FileBoxWindow instances.
        foreach (var other in OtherBoxes())
        {
            foreach (var tab in other.Tabs.OrderBy(t => t.TabOrder))
            {
                var capturedWindow = other;
                var capturedTab = tab;
                var target = new System.Windows.Controls.MenuItem { Header = capturedTab.Title };
                target.Click += (_, _) => TransferTilesToTab(capturedWindow, capturedTab, tiles);
                parentItem.Items.Add(target);
            }
        }

        parentItem.Visibility = parentItem.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TabButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileBoxTabItem tabItem)
        {
            SwitchActiveTab(tabItem.Box.Id);
            e.Handled = true;
        }
    }

    private void TabButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!State.HoverSwitchTabs || (sender as FrameworkElement)?.DataContext is not FileBoxTabItem tabItem)
        {
            return;
        }

        if (tabItem.Box.Id == State.Id)
        {
            _tabHoverSwitchTimer.Stop();
            _pendingHoverTabId = null;
            return;
        }

        _pendingHoverTabId = tabItem.Box.Id;
        _tabHoverSwitchTimer.Stop();
        _tabHoverSwitchTimer.Start();
    }

    private void TabButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileBoxTabItem tabItem
            && _pendingHoverTabId == tabItem.Box.Id)
        {
            _tabHoverSwitchTimer.Stop();
            _pendingHoverTabId = null;
        }
    }

    private void OnTabHoverSwitchTick()
    {
        _tabHoverSwitchTimer.Stop();
        if (!State.HoverSwitchTabs || _pendingHoverTabId is not { } tabId)
        {
            return;
        }

        _pendingHoverTabId = null;
        SwitchActiveTab(tabId);
    }

    private void TabButton_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileBoxTabItem tabItem)
        {
            return;
        }

        tabItem.IsDragTarget = true;
        if (tabItem.Box.Id != State.Id)
        {
            _pendingDragTabId = tabItem.Box.Id;
            _tabDragSwitchTimer.Stop();
            _tabDragSwitchTimer.Start();
        }
    }

    private void TabButton_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileBoxTabItem tabItem)
        {
            return;
        }

        if (e.Data.GetDataPresent(InternalBoxDragFormat)
            && e.Data.GetData(InternalBoxDragFormat) is InternalBoxDragPayload payload)
        {
            if (payload.SourceBoxId == tabItem.Box.Id || payload.MappingIds.Length == 0)
            {
                e.Effects = System.Windows.DragDropEffects.None;
            }
            else
            {
                e.Effects = (e.KeyStates & System.Windows.DragDropKeyStates.ControlKey) != 0
                    ? System.Windows.DragDropEffects.Copy
                    : System.Windows.DragDropEffects.Link;
            }
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Link;
            e.Handled = true;
        }
    }

    private void TabButton_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileBoxTabItem tabItem)
        {
            tabItem.IsDragTarget = false;
            if (_pendingDragTabId == tabItem.Box.Id)
            {
                _tabDragSwitchTimer.Stop();
                _pendingDragTabId = null;
            }
        }
    }

    private bool BeginExternalDropCleanup()
    {
        _tabDragSwitchTimer.Stop();
        _pendingDragTabId = null;
        _externalDragOver = false;
        _dragExpandTimer.Stop();
        var restoreCollapse = _expandedForExternalDrag;
        _expandedForExternalDrag = false;
        RefreshAutoCollapseTimer();
        RefreshHoverRevealState();
        return restoreCollapse;
    }

    private void EndExternalDropCleanup(bool restoreCollapse)
    {
        if (restoreCollapse && !State.IsCollapsed)
        {
            SetCollapsed(true, animate: true);
        }
    }

    private async void TabButton_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileBoxTabItem tabItem)
        {
            return;
        }

        tabItem.IsDragTarget = false;
        var targetTab = tabItem.Box;

        if (e.Data.GetDataPresent(InternalBoxDragFormat)
            && e.Data.GetData(InternalBoxDragFormat) is InternalBoxDragPayload payload
            && payload.SourceBoxId != targetTab.Id)
        {
            e.Handled = true;
            var restoreCollapse = BeginExternalDropCleanup();
            var copyOnly = (e.KeyStates & System.Windows.DragDropKeyStates.ControlKey) != 0;
            if (ResolveBoxAndTab(payload.SourceBoxId) is { } resolved)
            {
                TransferMappingIdsBetweenTabs(
                    resolved.Window,
                    resolved.Tab,
                    this,
                    targetTab,
                    payload.MappingIds,
                    copyOnly);
            }
            EndExternalDropCleanup(restoreCollapse);
            return;
        }

        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] dropped)
        {
            // Mark handled before awaiting so the routed Drop event does not bubble to Window_Drop.
            e.Handled = true;
            var restoreCollapse = BeginExternalDropCleanup();
            SwitchActiveTab(targetTab.Id);
            await AddMappingsWithStatusToBoxAsync(targetTab.Id, dropped);
            EndExternalDropCleanup(restoreCollapse);
        }
    }

    private void OnTabDragSwitchTick()
    {
        _tabDragSwitchTimer.Stop();
        if (_pendingDragTabId is not { } tabId)
        {
            return;
        }

        _pendingDragTabId = null;
        SwitchActiveTab(tabId);
    }

    private void FileBoxTabContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu { DataContext: FileBoxTabItem tabItem } menu)
        {
            return;
        }

        var ordered = _tabs.OrderBy(t => t.TabOrder).ToList();
        var index = ordered.FindIndex(t => t.Id == tabItem.Box.Id);
        foreach (var item in menu.Items.OfType<System.Windows.Controls.MenuItem>())
        {
            switch (item.Tag as string)
            {
                case "TabMoveUp":
                    item.IsEnabled = index > 0;
                    break;
                case "TabMoveDown":
                    item.IsEnabled = index >= 0 && index < ordered.Count - 1;
                    break;
            }
        }
    }

    private void TabRename_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileBoxTabItem tabItem)
        {
            SwitchActiveTab(tabItem.Box.Id);
            Dispatcher.BeginInvoke(BeginTitleEdit);
        }
    }

    private void TabMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileBoxTabItem tabItem
            && FileBoxGroupRules.MoveTab(_tabs, tabItem.Box.Id, -1))
        {
            RefreshTabStripItems();
            NotifyStateChanged();
        }
    }

    private void TabMoveDown_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileBoxTabItem tabItem
            && FileBoxGroupRules.MoveTab(_tabs, tabItem.Box.Id, 1))
        {
            RefreshTabStripItems();
            NotifyStateChanged();
        }
    }

    private void TabDetach_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileBoxTabItem tabItem)
        {
            DetachTabRequested?.Invoke(this, tabItem.Box);
        }
    }

    private void TabSplitAll_Click(object sender, RoutedEventArgs e)
    {
        SplitGroupRequested?.Invoke(this);
    }

    private void TabDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileBoxTabItem tabItem)
        {
            DeleteTabRequested?.Invoke(this, tabItem.Box);
        }
    }

    internal sealed class FileBoxTabItem : INotifyPropertyChanged
    {
        private bool _isActive;
        private int _itemCount;
        private bool _isDragTarget;

        public FileBoxTabItem(FileBoxState box, bool isActive, int itemCount)
        {
            Box = box;
            _isActive = isActive;
            _itemCount = itemCount;
        }

        public FileBoxState Box { get; }

        public bool IsActive => _isActive;

        public int ItemCount => _itemCount;

        public string Title => WidgetTitleRules.CommitTitle(Box.Title, EditableWidgetTitleKind.FileBox);

        public string VerticalLabel => FileBoxGroupRules.FormatVerticalTabLabel(Box.Title);

        public string ToolTipText => $"{Title}（{ItemCount} 项）";

        public void Update(bool isActive, int itemCount)
        {
            if (_isActive != isActive)
            {
                _isActive = isActive;
                OnPropertyChanged(nameof(IsActive));
            }
            if (_itemCount != itemCount)
            {
                _itemCount = itemCount;
                OnPropertyChanged(nameof(ItemCount));
            }
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(VerticalLabel));
            OnPropertyChanged(nameof(ToolTipText));
        }

        public bool IsDragTarget
        {
            get => _isDragTarget;
            set
            {
                if (_isDragTarget == value)
                {
                    return;
                }
                _isDragTarget = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
