using BingLan.Core.Dock;

namespace BingLan.Core.Models;

/// <summary>Monitor geometry snapshot used by pure edge-docking rules.</summary>
public readonly record struct EdgeDockMonitor(
    string DeviceName,
    PixelRect Bounds,
    PixelRect WorkArea,
    uint Dpi,
    bool IsPrimary);

/// <summary>
/// Pure geometry and timing rules for file-box edge docking (left/right outer screen edges only).
/// Multi-monitor interior shared edges are excluded so crossing between monitors never triggers
/// a docked file box; bottom and top edges are intentionally omitted to avoid Dock/TopBar conflicts.
/// </summary>
public static class FileBoxEdgeDockRules
{
    public const double VisibleTabWidthDip = 6d;
    public const int HotEdgeWidthPixels = 2;
    public const double MinTabHeightDip = 96d;
    public const double MaxTabHeightDip = 220d;
    public const double MinExpandedWidthDip = 240d;
    public const double MinExpandedHeightDip = 180d;
    public const int EdgeAdjacencyTolerancePixels = 2;

    public static readonly TimeSpan RevealDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan DragRevealDelay = TimeSpan.FromMilliseconds(80);
    public static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(420);

    /// <summary>
    /// Formats the hover tooltip shown on the collapsed edge-dock tab strip.
    /// </summary>
    public static string FormatTabToolTip(string? title) =>
        $"{BingLan.Core.Services.WidgetTitleRules.CommitTitle(title, BingLan.Core.Services.EditableWidgetTitleKind.FileBox)}（移入展开）";

    /// <summary>
    /// Returns true when <paramref name="side"/> on <paramref name="targetMonitor"/> is an
    /// outer boundary of the virtual desktop rather than an interior edge shared with another monitor.
    /// </summary>
    public static bool IsOuterEdge(
        PixelRect targetMonitor,
        FileBoxEdgeDockSide side,
        IReadOnlyList<PixelRect> allMonitors)
    {
        ArgumentNullException.ThrowIfNull(allMonitors);
        if (!targetMonitor.HasArea)
        {
            return false;
        }

        foreach (var other in allMonitors)
        {
            if (other == targetMonitor || !other.HasArea)
            {
                continue;
            }
            var verticalOverlap = other.Top < targetMonitor.Bottom && other.Bottom > targetMonitor.Top;
            if (!verticalOverlap)
            {
                continue;
            }
            if (side == FileBoxEdgeDockSide.Left
                && Math.Abs(other.Right - targetMonitor.Left) <= EdgeAdjacencyTolerancePixels)
            {
                return false;
            }
            if (side == FileBoxEdgeDockSide.Right
                && Math.Abs(other.Left - targetMonitor.Right) <= EdgeAdjacencyTolerancePixels)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Picks the nearest outer left/right edge on <paramref name="monitorBounds"/> for a card,
    /// or null if both left and right edges of that monitor are interior shared edges.
    /// </summary>
    public static FileBoxEdgeDockSide? ChooseDockSide(
        PixelRect cardBounds,
        PixelRect monitorBounds,
        IReadOnlyList<PixelRect> allMonitors)
    {
        var leftOuter = IsOuterEdge(monitorBounds, FileBoxEdgeDockSide.Left, allMonitors);
        var rightOuter = IsOuterEdge(monitorBounds, FileBoxEdgeDockSide.Right, allMonitors);
        if (!leftOuter && !rightOuter)
        {
            return null;
        }
        if (leftOuter && !rightOuter)
        {
            return FileBoxEdgeDockSide.Left;
        }
        if (rightOuter && !leftOuter)
        {
            return FileBoxEdgeDockSide.Right;
        }

        var centerX = (cardBounds.Left + cardBounds.Right) / 2d;
        var distLeft = Math.Abs(centerX - monitorBounds.Left);
        var distRight = Math.Abs(monitorBounds.Right - centerX);
        return distLeft <= distRight ? FileBoxEdgeDockSide.Left : FileBoxEdgeDockSide.Right;
    }

    /// <summary>
    /// Resolves which monitor owns a docked file box on <paramref name="side"/>: prefers the
    /// saved device if its <paramref name="side"/> is still an outer edge, otherwise falls back
    /// to the primary monitor (if outer) or the outermost monitor on that side.
    /// </summary>
    public static EdgeDockMonitor? ResolveMonitor(
        string? preferredDeviceName,
        FileBoxEdgeDockSide side,
        IReadOnlyList<EdgeDockMonitor> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
        {
            return null;
        }

        var boundsList = monitors.Select(m => m.Bounds).ToArray();
        if (!string.IsNullOrWhiteSpace(preferredDeviceName))
        {
            foreach (var candidate in monitors)
            {
                if (string.Equals(candidate.DeviceName, preferredDeviceName, StringComparison.OrdinalIgnoreCase)
                    && IsOuterEdge(candidate.Bounds, side, boundsList))
                {
                    return candidate;
                }
            }
        }

        var outerCandidates = monitors
            .Where(m => IsOuterEdge(m.Bounds, side, boundsList))
            .ToList();
        if (outerCandidates.Count == 0)
        {
            return null;
        }

        if (outerCandidates.FirstOrDefault(m => m.IsPrimary) is { Bounds.HasArea: true } primary)
        {
            return primary;
        }

        return side == FileBoxEdgeDockSide.Left
            ? outerCandidates.OrderBy(m => m.Bounds.Left).First()
            : outerCandidates.OrderByDescending(m => m.Bounds.Right).First();
    }

    /// <summary>
    /// Resolves the effective card height (in DIPs) used for hidden-tab and trigger sizing,
    /// ignoring a collapsed title-bar height (e.g. 48 DIP) so a newly docked collapsed box
    /// immediately uses the full rule-sized tab matching its trigger zone.
    /// </summary>
    public static double ResolveEffectiveCardHeightDip(double expandedHeightDip, double placementHeightDip)
    {
        if (double.IsFinite(expandedHeightDip) && expandedHeightDip >= MinExpandedHeightDip)
        {
            return expandedHeightDip;
        }
        return double.IsFinite(placementHeightDip) && placementHeightDip >= MinExpandedHeightDip
            ? placementHeightDip
            : 306d;
    }

    public static PixelRect ComputeHiddenTabBounds(
        EdgeDockMonitor monitor,
        FileBoxEdgeDockSide side,
        double offsetDip,
        double cardHeightDip)
    {
        var scale = Math.Max(1d, monitor.Dpi / 96d);
        var work = monitor.WorkArea.HasArea ? monitor.WorkArea : monitor.Bounds;
        var tabWidthPx = Math.Max(6, (int)Math.Round(VisibleTabWidthDip * scale));
        var desiredHeightDip = Math.Clamp(
            double.IsFinite(cardHeightDip) && cardHeightDip > 0 ? cardHeightDip : MaxTabHeightDip,
            MinTabHeightDip,
            MaxTabHeightDip);
        var tabHeightPx = Math.Clamp((int)Math.Round(desiredHeightDip * scale), 32, Math.Max(32, work.Height));
        var topPx = ClampTop(work, offsetDip, scale, tabHeightPx);

        return side == FileBoxEdgeDockSide.Left
            ? new PixelRect(monitor.Bounds.Left, topPx, monitor.Bounds.Left + tabWidthPx, topPx + tabHeightPx)
            : new PixelRect(monitor.Bounds.Right - tabWidthPx, topPx, monitor.Bounds.Right, topPx + tabHeightPx);
    }

    public static PixelRect ComputeTriggerRect(
        EdgeDockMonitor monitor,
        FileBoxEdgeDockSide side,
        double offsetDip,
        double cardHeightDip)
    {
        var tab = ComputeHiddenTabBounds(monitor, side, offsetDip, cardHeightDip);
        return side == FileBoxEdgeDockSide.Left
            ? new PixelRect(
                monitor.Bounds.Left,
                tab.Top,
                Math.Min(monitor.Bounds.Right, monitor.Bounds.Left + HotEdgeWidthPixels),
                tab.Bottom)
            : new PixelRect(
                Math.Max(monitor.Bounds.Left, monitor.Bounds.Right - HotEdgeWidthPixels),
                tab.Top,
                monitor.Bounds.Right,
                tab.Bottom);
    }

    /// <summary>
    /// Computes the free-floating pixel rectangle to restore when edge docking is turned off.
    /// Prefers the remembered per-layout physical spot; otherwise converts the saved placement
    /// using the target monitor's own DPI (never hardcoding the primary monitor's DPI) and clamps
    /// cleanly into that monitor's work area.
    /// </summary>
    public static PixelRect ComputeFreeRestoreBounds(
        WindowPlacement placement,
        string? preferredDeviceName,
        double? savedOffsetDip,
        double cardHeightDip,
        string? currentLayoutKey,
        IReadOnlyList<EdgeDockMonitor> monitors)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
        {
            var fallbackWidth = Math.Max(220, (int)Math.Round(placement.Width));
            var fallbackHeight = Math.Max(160, (int)Math.Round(cardHeightDip));
            var fallbackLeft = (int)Math.Round(placement.Left);
            var fallbackTop = (int)Math.Round(placement.Top);
            return new PixelRect(fallbackLeft, fallbackTop, fallbackLeft + fallbackWidth, fallbackTop + fallbackHeight);
        }

        if (!string.IsNullOrWhiteSpace(currentLayoutKey)
            && DisplayLayoutRules.TryRecall(placement, currentLayoutKey, out var recalled)
            && recalled.HasArea)
        {
            var owner = FindMonitorContainingPoint(
                    (recalled.Left + recalled.Right) / 2,
                    (recalled.Top + recalled.Bottom) / 2,
                    monitors)
                ?? FindMonitorByDevice(preferredDeviceName, monitors)
                ?? FindPrimaryOrFirst(monitors);
            var ownerScale = Math.Max(1d, owner.Dpi / 96d);
            var ownerWork = owner.WorkArea.HasArea ? owner.WorkArea : owner.Bounds;
            var recalledW = Math.Clamp(
                Math.Max(recalled.Width, (int)Math.Round(MinExpandedWidthDip * ownerScale)),
                180,
                Math.Max(180, ownerWork.Width));
            var recalledH = Math.Clamp(
                Math.Max(recalled.Height, (int)Math.Round(MinExpandedHeightDip * ownerScale)),
                120,
                Math.Max(120, ownerWork.Height));
            var recalledLeft = Math.Clamp(recalled.Left, ownerWork.Left, Math.Max(ownerWork.Left, ownerWork.Right - recalledW));
            var recalledTop = Math.Clamp(recalled.Top, ownerWork.Top, Math.Max(ownerWork.Top, ownerWork.Bottom - recalledH));
            return new PixelRect(recalledLeft, recalledTop, recalledLeft + recalledW, recalledTop + recalledH);
        }

        var target = FindMonitorByDevice(preferredDeviceName, monitors) ?? FindPrimaryOrFirst(monitors);
        var scale = Math.Max(1d, target.Dpi / 96d);
        var work = target.WorkArea.HasArea ? target.WorkArea : target.Bounds;
        var widthDip = double.IsFinite(placement.Width) && placement.Width >= MinExpandedWidthDip
            ? placement.Width
            : 366d;
        var heightDip = double.IsFinite(cardHeightDip) && cardHeightDip >= MinExpandedHeightDip
            ? cardHeightDip
            : 306d;
        var widthPx = Math.Clamp((int)Math.Round(widthDip * scale), 180, Math.Max(180, work.Width));
        var heightPx = Math.Clamp((int)Math.Round(heightDip * scale), 120, Math.Max(120, work.Height));

        var rawLeft = (int)Math.Round(placement.Left * scale);
        var rawTop = (int)Math.Round(placement.Top * scale);
        var fitsTarget = rawLeft >= target.Bounds.Left - widthPx / 2
            && rawLeft <= target.Bounds.Right - 48
            && rawTop >= target.Bounds.Top - 48
            && rawTop <= target.Bounds.Bottom - 48;

        int leftPx;
        int topPx;
        if (fitsTarget)
        {
            leftPx = Math.Clamp(rawLeft, work.Left, Math.Max(work.Left, work.Right - widthPx));
            topPx = Math.Clamp(rawTop, work.Top, Math.Max(work.Top, work.Bottom - heightPx));
        }
        else
        {
            var insetPx = (int)Math.Round(24d * scale);
            leftPx = Math.Clamp(work.Left + insetPx, work.Left, Math.Max(work.Left, work.Right - widthPx));
            topPx = ClampTop(work, savedOffsetDip ?? 92d, scale, heightPx);
        }

        return new PixelRect(leftPx, topPx, leftPx + widthPx, topPx + heightPx);
    }

    public static PixelRect ComputeExpandedBounds(
        EdgeDockMonitor monitor,
        FileBoxEdgeDockSide side,
        double offsetDip,
        double cardWidthDip,
        double cardHeightDip)
    {
        var scale = Math.Max(1d, monitor.Dpi / 96d);
        var work = monitor.WorkArea.HasArea ? monitor.WorkArea : monitor.Bounds;
        var widthDip = double.IsFinite(cardWidthDip) && cardWidthDip >= MinExpandedWidthDip
            ? cardWidthDip
            : 366d;
        var heightDip = double.IsFinite(cardHeightDip) && cardHeightDip >= MinExpandedHeightDip
            ? cardHeightDip
            : 306d;
        var widthPx = Math.Clamp((int)Math.Round(widthDip * scale), 180, Math.Max(180, work.Width));
        var heightPx = Math.Clamp((int)Math.Round(heightDip * scale), 120, Math.Max(120, work.Height));
        var topPx = ClampTop(work, offsetDip, scale, heightPx);

        return side == FileBoxEdgeDockSide.Left
            ? new PixelRect(work.Left, topPx, work.Left + widthPx, topPx + heightPx)
            : new PixelRect(work.Right - widthPx, topPx, work.Right, topPx + heightPx);
    }

    public static bool IsPointerInside(int cursorX, int cursorY, PixelRect rect) =>
        rect.HasArea
        && cursorX >= rect.Left
        && cursorX < rect.Right
        && cursorY >= rect.Top
        && cursorY < rect.Bottom;

    /// <summary>
    /// Tracks whether the current left-button press started outside the trigger zone.
    /// Pressing inside the trigger zone (such as dragging a maximized window's edge scrollbar)
    /// never counts as a drag-in approach.
    /// </summary>
    public static bool UpdateDragFromOutside(
        bool wasLeftButtonDown,
        bool isLeftButtonDown,
        bool pointerInTrigger,
        bool dragFromOutside)
    {
        if (!isLeftButtonDown)
        {
            return false;
        }
        if (!wasLeftButtonDown)
        {
            return !pointerInTrigger;
        }
        return dragFromOutside;
    }

    public static bool ShouldRevealNow(long enteredTickMs, long nowTickMs, bool isDragFromOutside)
    {
        var requiredMs = isDragFromOutside
            ? DragRevealDelay.TotalMilliseconds
            : RevealDelay.TotalMilliseconds;
        return (nowTickMs - enteredTickMs) >= requiredMs;
    }

    public static bool ShouldKeepExpanded(
        bool pointerInExpandedRect,
        bool pointerInTriggerRect,
        bool interactionBusy) =>
        pointerInExpandedRect || pointerInTriggerRect || interactionBusy;

    public static bool ShouldCollapseAfterLeave(long leftTickMs, long nowTickMs) =>
        (nowTickMs - leftTickMs) >= HideDelay.TotalMilliseconds;

    private static EdgeDockMonitor? FindMonitorByDevice(string? deviceName, IReadOnlyList<EdgeDockMonitor> monitors)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return null;
        }
        for (var i = 0; i < monitors.Count; i++)
        {
            if (string.Equals(monitors[i].DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return monitors[i];
            }
        }
        return null;
    }

    private static EdgeDockMonitor? FindMonitorContainingPoint(int x, int y, IReadOnlyList<EdgeDockMonitor> monitors)
    {
        for (var i = 0; i < monitors.Count; i++)
        {
            var b = monitors[i].Bounds;
            if (x >= b.Left && x < b.Right && y >= b.Top && y < b.Bottom)
            {
                return monitors[i];
            }
        }
        return null;
    }

    private static EdgeDockMonitor FindPrimaryOrFirst(IReadOnlyList<EdgeDockMonitor> monitors)
    {
        for (var i = 0; i < monitors.Count; i++)
        {
            if (monitors[i].IsPrimary)
            {
                return monitors[i];
            }
        }
        return monitors[0];
    }

    private static int ClampTop(PixelRect work, double offsetDip, double scale, int heightPx)
    {
        var safeOffsetDip = double.IsFinite(offsetDip) ? Math.Max(0d, offsetDip) : 92d;
        var desiredTop = work.Top + (int)Math.Round(safeOffsetDip * scale);
        var maxTop = Math.Max(work.Top, work.Bottom - heightPx);
        return Math.Clamp(desiredTop, work.Top, maxTop);
    }
}
