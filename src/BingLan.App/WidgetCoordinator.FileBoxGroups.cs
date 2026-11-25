using System.Windows;
using BingLan.App.Interop;
using BingLan.App.Windows;
using BingLan.Core.Dock;
using BingLan.Core.Models;

namespace BingLan.App;

public sealed partial class WidgetCoordinator
{
    private FileBoxWindow? _dragMergeTarget;

    private FileBoxWindow? FindFileBoxWindow(Guid boxId) =>
        _windows.OfType<FileBoxWindow>().FirstOrDefault(window => window.ContainsBox(boxId));

    private FileBoxWindow ShowFileBoxGroup(IReadOnlyList<FileBoxState> members)
    {
        var ordered = members.OrderBy(m => m.TabOrder).ToList();
        var lead = ordered.FirstOrDefault(m => m.IsActiveTab) ?? ordered[0];
        SanitizePlacement(lead.Placement);
        if (ordered.Count > 1)
        {
            FileBoxGroupRules.SyncGroupSharedProperties(lead, ordered);
        }

        var window = new FileBoxWindow(ordered)
        {
            ShowInTaskbar = false
        };
        window.OtherBoxes = () => _windows
            .OfType<FileBoxWindow>()
            .Where(other => !ReferenceEquals(other, window))
            .ToList();
        window.SameKindOtherTitles = () => _state.FileBoxes
            .Where(other => other.Id != window.State.Id)
            .Select(other => other.Title);
        window.IsDeleteOriginalsEnabled = () => _state.FileBoxDeleteOriginalsEnabled;
        window.EdgeDockToggled += (box, enabled) => _edgeDock.ToggleEdgeDock(box, enabled);
        window.EdgeDockFileDragNear += box => _edgeDock.RevealImmediatelyForDrag(box);
        window.MergeRequested += (source, target) => MergeFileBoxes(source, target);
        window.DetachTabRequested += (host, tab) => DetachFileBoxTab(host, tab);
        window.SplitGroupRequested += SplitFileBoxGroup;
        window.DeleteTabRequested += DeleteFileBoxTab;
        window.DragMoveEvaluated += OnFileBoxDragMoveEvaluated;
        window.DragMoveFinished += OnFileBoxDragMoveFinished;
        Wire(window);
        _windows.Add(window);
        if (!window.State.IsHidden)
        {
            window.Show();
            if (window.IsEdgeDocked)
            {
                _edgeDock.ApplyAll();
            }
        }
        return window;
    }

    private void OnFileBoxDragMoveEvaluated(FileBoxWindow dragged, PixelRect bounds, bool altHeld)
    {
        if (altHeld || dragged.IsEdgeDocked)
        {
            ClearDragMergeHighlight();
            return;
        }

        FileBoxWindow? matched = null;
        foreach (var candidate in _windows.OfType<FileBoxWindow>())
        {
            if (ReferenceEquals(candidate, dragged)
                || !candidate.IsVisible
                || candidate.IsEdgeDocked
                || candidate.WindowHandle == 0
                || !DockNativeMethods.GetWindowRect(candidate.WindowHandle, out var rect))
            {
                continue;
            }

            var targetRect = new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (FileBoxGroupRules.CanMergeOnDrop(bounds, targetRect, altHeld: false))
            {
                matched = candidate;
                break;
            }
        }

        if (!ReferenceEquals(_dragMergeTarget, matched))
        {
            _dragMergeTarget?.SetMergeDropHighlight(false);
            _dragMergeTarget = matched;
            _dragMergeTarget?.SetMergeDropHighlight(true);
        }
    }

    private void OnFileBoxDragMoveFinished(FileBoxWindow dragged, bool wasMoving, bool altHeld)
    {
        var target = _dragMergeTarget;
        ClearDragMergeHighlight();
        if (wasMoving
            && !altHeld
            && target is not null
            && !ReferenceEquals(dragged, target)
            && _windows.Contains(dragged)
            && _windows.Contains(target))
        {
            MergeFileBoxes(dragged, target);
        }
    }

    private void ClearDragMergeHighlight()
    {
        if (_dragMergeTarget is not null)
        {
            _dragMergeTarget.SetMergeDropHighlight(false);
            _dragMergeTarget = null;
        }
    }

    internal FileBoxWindow? MergeFileBoxes(FileBoxWindow source, FileBoxWindow target)
    {
        if (ReferenceEquals(source, target)
            || !_windows.Contains(source)
            || !_windows.Contains(target)
            || source.IsEdgeDocked
            || target.IsEdgeDocked)
        {
            return null;
        }

        ClearDragMergeHighlight();
        source.CaptureState();
        target.CaptureState();

        var activateId = source.State.Id;
        var groupId = FileBoxGroupRules.Merge(source.Tabs, target.Tabs, activateFirstMergedSource: true);

        _windows.Remove(source);
        _windowsHiddenFromTray.Remove(source);
        source.CanClose = true;
        source.Close();

        var combined = _state.FileBoxes
            .Where(box => box.GroupId == groupId)
            .OrderBy(box => box.TabOrder)
            .ToList();
        target.SyncTabsFromStates(combined, activateBoxId: activateId);
        target.SetOperationStatus($"已合并为 {combined.Count} 页组合收纳盒");
        _settingsWindow?.RefreshWidgetsAfterGroupChange(target);
        SaveNow();
        return target;
    }

    internal FileBoxWindow? DetachFileBoxTab(FileBoxWindow host, FileBoxState tab)
    {
        if (!host.IsGrouped || !host.ContainsBox(tab.Id) || !_windows.Contains(host))
        {
            return null;
        }

        host.CaptureState();
        var remaining = FileBoxGroupRules.Detach(tab, host.Tabs);
        host.SyncTabsFromStates(remaining);
        var detachedWindow = ShowFileBox(tab);
        detachedWindow.SetOperationStatus($"已从组合收纳盒移出“{tab.Title}”");
        _settingsWindow?.RefreshWidgetsAfterGroupChange(host);
        SaveNow();
        return detachedWindow;
    }

    internal void SplitFileBoxGroup(FileBoxWindow host)
    {
        if (!host.IsGrouped || !_windows.Contains(host))
        {
            return;
        }

        host.CaptureState();
        var splitBoxes = FileBoxGroupRules.SplitAll(host.Tabs);
        if (splitBoxes.Count == 0)
        {
            return;
        }

        host.SyncTabsFromStates([splitBoxes[0]]);
        for (var i = 1; i < splitBoxes.Count; i++)
        {
            ShowFileBox(splitBoxes[i]);
        }
        host.SetOperationStatus($"已拆分为 {splitBoxes.Count} 个独立收纳盒");
        _settingsWindow?.RefreshWidgetsAfterGroupChange(host);
        SaveNow();
    }

    private void DeleteFileBoxTab(FileBoxWindow host, FileBoxState tab)
    {
        if (!host.IsGrouped)
        {
            DeleteWidget(host);
            return;
        }

        if (!host.ContainsBox(tab.Id) || !_windows.Contains(host))
        {
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            $"将删除标签页“{tab.Title}”及其中的映射（不删除原文件）。继续吗？",
            "删除标签页",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        host.CaptureState();
        _state.FileBoxes.RemoveAll(box => box.Id == tab.Id);
        if (tab.DesktopCategory != DesktopGroupCategory.None
            && !_state.DismissedDesktopCategories.Contains(tab.DesktopCategory))
        {
            _state.DismissedDesktopCategories.Add(tab.DesktopCategory);
        }

        var remaining = host.Tabs
            .Where(candidate => candidate.Id != tab.Id)
            .OrderBy(candidate => candidate.TabOrder)
            .ToList();
        if (remaining.Count == 0)
        {
            RemoveWidget(host);
            return;
        }

        if (remaining.Count == 1)
        {
            remaining[0].GroupId = null;
            remaining[0].TabOrder = 0;
            remaining[0].IsActiveTab = false;
            remaining[0].HoverSwitchTabs = false;
        }
        else
        {
            var nextActive = remaining.FirstOrDefault(candidate => candidate.IsActiveTab) ?? remaining[0];
            for (var i = 0; i < remaining.Count; i++)
            {
                remaining[i].TabOrder = i;
                remaining[i].IsActiveTab = ReferenceEquals(remaining[i], nextActive);
            }
            FileBoxGroupRules.SyncGroupSharedProperties(host.State, remaining);
        }

        host.SyncTabsFromStates(remaining);
        _settingsWindow?.RefreshWidgetsAfterGroupChange(host);
        SaveNow();
    }
}
