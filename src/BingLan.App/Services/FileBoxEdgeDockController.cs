using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.App.Windows;
using BingLan.Core.Dock;
using BingLan.Core.Models;

namespace BingLan.App.Services;

/// <summary>
/// Manages left/right outer-edge docking for file boxes without creating any extra resident
/// window. A single lightweight 80 ms timer runs only while at least one file box is edge-docked;
/// monitor metadata is cached and refreshed only on display changes or dock actions so polling
/// does zero monitor enumeration and zero LINQ allocations.
/// </summary>
internal sealed class FileBoxEdgeDockController : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(80);

    private readonly DesktopPeekController _peek;
    private readonly IReadOnlyList<WidgetWindowBase> _windows;
    private readonly Action _saveState;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<Guid, DockTracker> _trackers = [];
    private EdgeDockMonitorEntry[] _cachedEntries = [];
    private EdgeDockMonitor[] _cachedMonitors = [];
    private PixelRect[] _cachedBounds = [];
    private string _cachedLayoutKey = string.Empty;
    private bool _disposed;

    private readonly record struct EdgeDockMonitorEntry(EdgeDockMonitor Monitor, nint MonitorHandle);

    private sealed class DockTracker
    {
        public bool Expanded;
        public bool WasLeftButtonDown;
        public bool DragFromOutside;
        public long? EnteredTriggerTick;
        public long? LeftExpandedTick;
        public bool HasMonitor;
        public EdgeDockMonitorEntry ResolvedMonitor;
        public PixelRect LastTriggerRect;
        public PixelRect LastExpandedRect;
    }

    internal FileBoxEdgeDockController(
        DesktopPeekController peek,
        IReadOnlyList<WidgetWindowBase> windows,
        Action saveState)
    {
        _peek = peek;
        _windows = windows;
        _saveState = saveState;
        _timer = new DispatcherTimer(PollInterval, DispatcherPriority.Background, (_, _) => Tick(), Dispatcher.CurrentDispatcher);
        _timer.Stop();
    }

    internal bool HasDockedBoxes
    {
        get
        {
            for (var i = 0; i < _windows.Count; i++)
            {
                if (_windows[i] is FileBoxWindow box && box.IsEdgeDocked && box.IsVisible)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Refreshes cached monitor geometry after display settings or DPI changes.</summary>
    internal void InvalidateMonitors()
    {
        if (_disposed)
        {
            return;
        }
        RefreshMonitorCache();
        for (var i = 0; i < _windows.Count; i++)
        {
            if (_windows[i] is FileBoxWindow box && box.IsEdgeDocked && box.IsVisible && box.State.EdgeDockSide is { } side)
            {
                var tracker = GetTracker(box.State.Id);
                UpdateTrackerMonitor(box, side, tracker);
            }
        }
    }

    internal bool ToggleEdgeDock(FileBoxWindow box, bool enable) =>
        ToggleEdgeDock(box, enable, explicitSide: null, explicitEntry: null);

    internal bool TryDockOnDragRelease(FileBoxWindow box, int cursorX, int cursorY)
    {
        if (_disposed || box.IsEdgeDocked || box.State.IsLocked || !box.IsVisible)
        {
            return false;
        }

        RefreshMonitorCache();
        if (_cachedEntries.Length == 0)
        {
            return false;
        }

        var boxRect = ReadWindowRect(box);
        var currentEntry = FindMonitorEntryForDragRelease(boxRect, cursorX, cursorY)
            ?? _cachedEntries[0];
        var side = FileBoxEdgeDockRules.TryDetectDragDockSide(
            cursorX,
            boxRect,
            currentEntry.Monitor.Bounds,
            currentEntry.Monitor.Dpi,
            _cachedBounds);
        if (side is null)
        {
            return false;
        }

        return ToggleEdgeDock(box, enable: true, explicitSide: side, explicitEntry: currentEntry);
    }

    private bool ToggleEdgeDock(
        FileBoxWindow box,
        bool enable,
        FileBoxEdgeDockSide? explicitSide,
        EdgeDockMonitorEntry? explicitEntry)
    {
        if (_disposed)
        {
            return false;
        }

        RefreshMonitorCache();

        if (!enable)
        {
            var wasExpanded = box.IsEdgeDockExpanded;
            var savedDevice = box.State.EdgeDockMonitorDeviceName;
            var savedOffset = box.State.EdgeDockOffsetDip;
            box.State.EdgeDockSide = null;
            box.State.EdgeDockOffsetDip = null;
            box.State.EdgeDockMonitorDeviceName = null;
            _trackers.Remove(box.State.Id);

            var restoreRect = FileBoxEdgeDockRules.ComputeFreeRestoreBounds(
                box.State.Placement,
                savedDevice,
                savedOffset,
                EffectiveCardHeightDip(box),
                _cachedLayoutKey,
                _cachedMonitors);
            box.ExitEdgeDockedVisual(restoreRect);
            if (wasExpanded)
            {
                _peek.ReturnSingleBox(box);
            }
            box.NotifyEdgeDockStateChanged();
            SyncTimerState();
            _saveState();
            return true;
        }

        if (_cachedEntries.Length == 0)
        {
            return false;
        }

        var boxRect = ReadWindowRect(box);
        var currentEntry = explicitEntry ?? FindMonitorEntryForRect(boxRect) ?? _cachedEntries[0];
        var chosenSide = explicitSide ?? FileBoxEdgeDockRules.ChooseDockSide(boxRect, currentEntry.Monitor.Bounds, _cachedBounds);
        EdgeDockMonitorEntry targetEntry;
        FileBoxEdgeDockSide side;
        if (chosenSide is { } localSide)
        {
            targetEntry = currentEntry;
            side = localSide;
        }
        else
        {
            var centerX = (boxRect.Left + boxRect.Right) / 2d;
            var minLeft = _cachedMonitors[0].Bounds.Left;
            var maxRight = _cachedMonitors[0].Bounds.Right;
            for (var i = 1; i < _cachedMonitors.Length; i++)
            {
                if (_cachedMonitors[i].Bounds.Left < minLeft)
                {
                    minLeft = _cachedMonitors[i].Bounds.Left;
                }
                if (_cachedMonitors[i].Bounds.Right > maxRight)
                {
                    maxRight = _cachedMonitors[i].Bounds.Right;
                }
            }
            side = Math.Abs(centerX - minLeft) <= Math.Abs(maxRight - centerX)
                ? FileBoxEdgeDockSide.Left
                : FileBoxEdgeDockSide.Right;
            if (!TryResolveMonitorEntry(null, side, out targetEntry))
            {
                return false;
            }
        }

        var scale = Math.Max(1d, targetEntry.Monitor.Dpi / 96d);
        var work = targetEntry.Monitor.WorkArea.HasArea ? targetEntry.Monitor.WorkArea : targetEntry.Monitor.Bounds;
        var offsetDip = Math.Max(0d, (boxRect.Top - work.Top) / scale);

        box.CaptureState();
        box.State.EdgeDockSide = side;
        box.State.EdgeDockOffsetDip = Math.Round(offsetDip, 1);
        box.State.EdgeDockMonitorDeviceName = targetEntry.Monitor.DeviceName;

        ApplyHiddenDockedBox(box, targetEntry, side);
        box.NotifyEdgeDockStateChanged();
        SyncTimerState();
        _saveState();
        return true;
    }

    internal void RevealImmediatelyForDrag(FileBoxWindow box)
    {
        if (_disposed || !box.IsEdgeDocked || box.IsEdgeDockExpanded || _peek.IsActive)
        {
            return;
        }
        EnsureMonitorCache();
        if (box.State.EdgeDockSide is not { } side
            || !TryResolveMonitorEntry(box.State.EdgeDockMonitorDeviceName, side, out var entry))
        {
            return;
        }
        if (IsTrueFullScreenOnMonitor(entry))
        {
            return;
        }
        ExpandBox(box, entry, side);
    }

    internal void ApplyAll()
    {
        if (_disposed)
        {
            return;
        }
        RefreshMonitorCache();
        for (var i = 0; i < _windows.Count; i++)
        {
            if (_windows[i] is not FileBoxWindow box || !box.IsEdgeDocked || !box.IsVisible || box.State.EdgeDockSide is not { } side)
            {
                continue;
            }
            if (!TryResolveMonitorEntry(box.State.EdgeDockMonitorDeviceName, side, out var entry))
            {
                continue;
            }
            if (box.IsEdgeDockExpanded)
            {
                var expanded = ComputeExpandedRect(box, entry.Monitor, side);
                var tracker = GetTracker(box.State.Id);
                tracker.HasMonitor = true;
                tracker.ResolvedMonitor = entry;
                tracker.LastExpandedRect = expanded;
                tracker.LastTriggerRect = ComputeTriggerRect(box, entry.Monitor, side);
                box.UpdateEdgeDockPosition(expanded);
            }
            else
            {
                ApplyHiddenDockedBox(box, entry, side);
            }
        }
        PruneStaleTrackers();
        SyncTimerState();
    }

    internal void CollapseAllExpanded()
    {
        if (_disposed)
        {
            return;
        }
        EnsureMonitorCache();
        for (var i = 0; i < _windows.Count; i++)
        {
            if (_windows[i] is not FileBoxWindow box || !box.IsEdgeDocked || !box.IsEdgeDockExpanded || box.State.EdgeDockSide is not { } side)
            {
                continue;
            }
            if (TryResolveMonitorEntry(box.State.EdgeDockMonitorDeviceName, side, out var entry))
            {
                CollapseBox(box, entry, side);
            }
            else
            {
                _peek.ReturnSingleBox(box);
            }
        }
    }

    internal void SyncTimerState()
    {
        if (_disposed)
        {
            _timer.Stop();
            return;
        }
        if (HasDockedBoxes)
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
        }
        else
        {
            _timer.Stop();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _timer.Stop();
        _trackers.Clear();
    }

    private void Tick()
    {
        if (_disposed)
        {
            return;
        }

        if (!HasDockedBoxes)
        {
            _timer.Stop();
            return;
        }

        if (_peek.IsActive)
        {
            CollapseAllExpanded();
            return;
        }

        EnsureMonitorCache();
        if (_cachedEntries.Length == 0 || !DockNativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var now = Environment.TickCount64;
        var isLeftButtonDown = TopBarNativeMethods.IsKeyCurrentlyDown(TopBarNativeMethods.LeftMouseButton);
        var foreground = DockNativeMethods.GetForegroundWindow();

        for (var i = 0; i < _windows.Count; i++)
        {
            if (_windows[i] is not FileBoxWindow box || !box.IsEdgeDocked || !box.IsVisible || box.State.EdgeDockSide is not { } side)
            {
                continue;
            }

            var tracker = GetTracker(box.State.Id);
            if (!tracker.HasMonitor)
            {
                UpdateTrackerMonitor(box, side, tracker);
                if (!tracker.HasMonitor)
                {
                    continue;
                }
            }

            var entry = tracker.ResolvedMonitor;
            if (IsTrueFullScreenOnMonitor(foreground, entry))
            {
                tracker.EnteredTriggerTick = null;
                tracker.LeftExpandedTick = null;
                tracker.WasLeftButtonDown = isLeftButtonDown;
                tracker.DragFromOutside = false;
                if (box.IsEdgeDockExpanded)
                {
                    CollapseBox(box, entry, side);
                }
                continue;
            }

            var triggerRect = tracker.LastTriggerRect;
            var inTrigger = FileBoxEdgeDockRules.IsPointerInside(cursor.X, cursor.Y, triggerRect);
            tracker.DragFromOutside = FileBoxEdgeDockRules.UpdateDragFromOutside(
                tracker.WasLeftButtonDown,
                isLeftButtonDown,
                inTrigger,
                tracker.DragFromOutside);
            tracker.WasLeftButtonDown = isLeftButtonDown;

            if (!box.IsEdgeDockExpanded)
            {
                tracker.LeftExpandedTick = null;
                if (inTrigger)
                {
                    tracker.EnteredTriggerTick ??= now;
                    var isDragIntoTrigger = isLeftButtonDown && tracker.DragFromOutside;
                    if (FileBoxEdgeDockRules.ShouldRevealNow(tracker.EnteredTriggerTick.Value, now, isDragIntoTrigger))
                    {
                        ExpandBox(box, entry, side);
                    }
                }
                else
                {
                    tracker.EnteredTriggerTick = null;
                }
            }
            else
            {
                var expandedRect = tracker.LastExpandedRect.HasArea
                    ? tracker.LastExpandedRect
                    : ComputeExpandedRect(box, entry.Monitor, side);
                var inExpanded = FileBoxEdgeDockRules.IsPointerInside(cursor.X, cursor.Y, expandedRect);
                var keep = FileBoxEdgeDockRules.ShouldKeepExpanded(
                    inExpanded,
                    inTrigger,
                    box.IsEdgeDockInteractionBusy);
                if (keep)
                {
                    tracker.LeftExpandedTick = null;
                }
                else
                {
                    tracker.LeftExpandedTick ??= now;
                    if (FileBoxEdgeDockRules.ShouldCollapseAfterLeave(tracker.LeftExpandedTick.Value, now))
                    {
                        CollapseBox(box, entry, side);
                    }
                }
            }
        }
    }

    private void ApplyHiddenDockedBox(FileBoxWindow box, EdgeDockMonitorEntry entry, FileBoxEdgeDockSide side)
    {
        var tabRect = FileBoxEdgeDockRules.ComputeHiddenTabBounds(
            entry.Monitor,
            side,
            box.State.EdgeDockOffsetDip ?? 92d,
            EffectiveCardHeightDip(box));
        var tracker = GetTracker(box.State.Id);
        tracker.Expanded = false;
        tracker.EnteredTriggerTick = null;
        tracker.LeftExpandedTick = null;
        tracker.HasMonitor = true;
        tracker.ResolvedMonitor = entry;
        tracker.LastTriggerRect = ComputeTriggerRect(box, entry.Monitor, side);
        box.EnterEdgeDockedHiddenVisual(side, tabRect);
        AlignClampedHiddenTabToOuterEdge(box, entry.Monitor, side, tabRect);
        _peek.ReturnSingleBox(box);
    }

    // Windows enforces a minimum top-level HWND width (~20 px at 150% DPI) wider than the
    // painted 6 DIP strip. On the right outer edge, align the window's right edge flush with
    // the monitor so the right-aligned 6 DIP strip never overflows off-screen while the extra
    // transparent pixels remain alpha-0 and HTTRANSPARENT.
    private static void AlignClampedHiddenTabToOuterEdge(
        FileBoxWindow box,
        EdgeDockMonitor monitor,
        FileBoxEdgeDockSide side,
        PixelRect requestedTabRect)
    {
        if (side != FileBoxEdgeDockSide.Right)
        {
            return;
        }
        var handle = box.WindowHandle;
        if (handle == 0 || !DockNativeMethods.GetWindowRect(handle, out var actual))
        {
            return;
        }
        var actualWidth = actual.Right - actual.Left;
        if (actualWidth > requestedTabRect.Width && actual.Right != monitor.Bounds.Right)
        {
            var alignedLeft = monitor.Bounds.Right - actualWidth;
            box.UpdateEdgeDockPosition(new PixelRect(alignedLeft, requestedTabRect.Top, monitor.Bounds.Right, requestedTabRect.Bottom));
        }
    }

    private void ExpandBox(FileBoxWindow box, EdgeDockMonitorEntry entry, FileBoxEdgeDockSide side)
    {
        var expandedRect = ComputeExpandedRect(box, entry.Monitor, side);
        var tracker = GetTracker(box.State.Id);
        tracker.Expanded = true;
        tracker.EnteredTriggerTick = null;
        tracker.LeftExpandedTick = null;
        tracker.HasMonitor = true;
        tracker.ResolvedMonitor = entry;
        tracker.LastExpandedRect = expandedRect;
        box.EnterEdgeDockedExpandedVisual(side, expandedRect, CardMotionAnimator.IsAnimationEnabled);
        _peek.RaiseSingleBoxTemporarily(box);
    }

    private void CollapseBox(FileBoxWindow box, EdgeDockMonitorEntry entry, FileBoxEdgeDockSide side)
    {
        ApplyHiddenDockedBox(box, entry, side);
    }

    private void UpdateTrackerMonitor(FileBoxWindow box, FileBoxEdgeDockSide side, DockTracker tracker)
    {
        if (TryResolveMonitorEntry(box.State.EdgeDockMonitorDeviceName, side, out var entry))
        {
            tracker.HasMonitor = true;
            tracker.ResolvedMonitor = entry;
            tracker.LastTriggerRect = ComputeTriggerRect(box, entry.Monitor, side);
            if (box.IsEdgeDockExpanded)
            {
                tracker.LastExpandedRect = ComputeExpandedRect(box, entry.Monitor, side);
            }
        }
        else
        {
            tracker.HasMonitor = false;
        }
    }

    private void EnsureMonitorCache()
    {
        if (_cachedEntries.Length == 0)
        {
            RefreshMonitorCache();
        }
    }

    private void RefreshMonitorCache()
    {
        var all = MonitorCatalog.GetAll();
        var entries = new EdgeDockMonitorEntry[all.Count];
        var snapshots = new EdgeDockMonitor[all.Count];
        var bounds = new PixelRect[all.Count];
        var layoutInputs = new (PixelRect Bounds, uint Dpi)[all.Count];
        for (var i = 0; i < all.Count; i++)
        {
            var m = all[i];
            var work = MonitorCatalog.TryGetWorkingArea(m.MonitorHandle) ?? m.WorkingArea;
            var snapshot = new EdgeDockMonitor(m.DeviceName, m.Bounds, work, m.Dpi, m.IsPrimary);
            entries[i] = new EdgeDockMonitorEntry(snapshot, m.MonitorHandle);
            snapshots[i] = snapshot;
            bounds[i] = m.Bounds;
            layoutInputs[i] = (m.Bounds, m.Dpi);
        }
        _cachedEntries = entries;
        _cachedMonitors = snapshots;
        _cachedBounds = bounds;
        _cachedLayoutKey = DisplayLayoutRules.Key(layoutInputs);
    }

    private bool TryResolveMonitorEntry(string? preferredDeviceName, FileBoxEdgeDockSide side, out EdgeDockMonitorEntry entry)
    {
        if (FileBoxEdgeDockRules.ResolveMonitor(preferredDeviceName, side, _cachedMonitors) is { } resolved)
        {
            for (var i = 0; i < _cachedEntries.Length; i++)
            {
                if (string.Equals(_cachedEntries[i].Monitor.DeviceName, resolved.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    entry = _cachedEntries[i];
                    return true;
                }
            }
        }
        entry = default;
        return false;
    }

    private EdgeDockMonitorEntry? FindMonitorEntryForDragRelease(PixelRect rect, int cursorX, int cursorY)
    {
        // Prefer the monitor with the largest intersection area with the card so edge-dock
        // ownership matches MonitorFromRect / multi-screen reconciliation (even on vertically
        // stacked monitors or when dragging a card's center past a secondary monitor's outer edge).
        EdgeDockMonitorEntry? bestOverlap = null;
        long bestArea = 0;
        for (var i = 0; i < _cachedEntries.Length; i++)
        {
            var entry = _cachedEntries[i];
            var b = entry.Monitor.Bounds;
            var left = Math.Max(rect.Left, b.Left);
            var top = Math.Max(rect.Top, b.Top);
            var right = Math.Min(rect.Right, b.Right);
            var bottom = Math.Min(rect.Bottom, b.Bottom);
            if (right > left && bottom > top)
            {
                var area = (long)(right - left) * (bottom - top);
                if (area > bestArea)
                {
                    bestArea = area;
                    bestOverlap = entry;
                }
            }
        }
        if (bestOverlap is not null)
        {
            return bestOverlap;
        }

        for (var i = 0; i < _cachedEntries.Length; i++)
        {
            var entry = _cachedEntries[i];
            var b = entry.Monitor.Bounds;
            if (cursorX >= b.Left && cursorX < b.Right && cursorY >= b.Top && cursorY < b.Bottom)
            {
                return entry;
            }
        }
        return FindMonitorEntryForRect(rect);
    }

    private EdgeDockMonitorEntry? FindMonitorEntryForRect(PixelRect rect)
    {
        var cx = (rect.Left + rect.Right) / 2;
        var cy = (rect.Top + rect.Bottom) / 2;
        EdgeDockMonitorEntry? primary = null;
        for (var i = 0; i < _cachedEntries.Length; i++)
        {
            var entry = _cachedEntries[i];
            if (entry.Monitor.IsPrimary)
            {
                primary = entry;
            }
            var b = entry.Monitor.Bounds;
            if (cx >= b.Left && cx < b.Right && cy >= b.Top && cy < b.Bottom)
            {
                return entry;
            }
        }
        return primary ?? (_cachedEntries.Length > 0 ? _cachedEntries[0] : null);
    }

    private void PruneStaleTrackers()
    {
        if (_trackers.Count == 0)
        {
            return;
        }
        List<Guid>? stale = null;
        foreach (var id in _trackers.Keys)
        {
            var active = false;
            for (var i = 0; i < _windows.Count; i++)
            {
                if (_windows[i] is FileBoxWindow box && box.IsEdgeDocked && box.IsVisible && box.State.Id == id)
                {
                    active = true;
                    break;
                }
            }
            if (!active)
            {
                stale ??= [];
                stale.Add(id);
            }
        }
        if (stale is not null)
        {
            for (var i = 0; i < stale.Count; i++)
            {
                _trackers.Remove(stale[i]);
            }
        }
    }

    private DockTracker GetTracker(Guid id)
    {
        if (!_trackers.TryGetValue(id, out var tracker))
        {
            tracker = new DockTracker();
            _trackers[id] = tracker;
        }
        return tracker;
    }

    private static PixelRect ComputeTriggerRect(FileBoxWindow box, EdgeDockMonitor monitor, FileBoxEdgeDockSide side) =>
        FileBoxEdgeDockRules.ComputeTriggerRect(
            monitor,
            side,
            box.State.EdgeDockOffsetDip ?? 92d,
            EffectiveCardHeightDip(box));

    private static PixelRect ComputeExpandedRect(FileBoxWindow box, EdgeDockMonitor monitor, FileBoxEdgeDockSide side) =>
        FileBoxEdgeDockRules.ComputeExpandedBounds(
            monitor,
            side,
            box.State.EdgeDockOffsetDip ?? 92d,
            box.State.Placement.Width,
            EffectiveCardHeightDip(box));

    private static double EffectiveCardHeightDip(FileBoxWindow box) =>
        FileBoxEdgeDockRules.ResolveEffectiveCardHeightDip(box.State.ExpandedHeight, box.State.Placement.Height);

    private static PixelRect ReadWindowRect(FileBoxWindow box)
    {
        if (box.WindowHandle is var handle && handle != 0 && DockNativeMethods.GetWindowRect(handle, out var rect))
        {
            return new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }
        return new PixelRect(
            (int)Math.Round(box.Left),
            (int)Math.Round(box.Top),
            (int)Math.Round(box.Left + Math.Max(240, box.Width)),
            (int)Math.Round(box.Top + Math.Max(180, box.Height)));
    }

    private static bool IsTrueFullScreenOnMonitor(EdgeDockMonitorEntry target) =>
        IsTrueFullScreenOnMonitor(DockNativeMethods.GetForegroundWindow(), target);

    private static bool IsTrueFullScreenOnMonitor(nint foreground, EdgeDockMonitorEntry target) =>
        foreground != 0
        && target.MonitorHandle != 0
        && FullScreenWatcher.IsFullScreen(foreground, target.MonitorHandle, excludeMaximized: true);
}
