using BingLan.Core.Models;

namespace BingLan.Core.Dock;

/// <summary>
/// Pure edge-orientation rules for bottom / left / right docking (top is excluded so the
/// dock never competes with the top bar). Covers layout axis, inward magnification lift,
/// overflow headroom expansion, AppBar edge checks, TopBar/taskbar vertical coexistence,
/// reappear anchors, and drag-reorder insertion along the active axis.
/// </summary>
public static class DockEdgeRules
{
    public static DockEdge Normalize(DockEdge edge) => edge switch
    {
        DockEdge.Left => DockEdge.Left,
        DockEdge.Right => DockEdge.Right,
        _ => DockEdge.Bottom
    };

    public static bool IsVertical(DockEdge edge) =>
        edge is DockEdge.Left or DockEdge.Right;

    /// <summary>Unit vector pointing from the docked screen edge toward the screen interior.</summary>
    public static (int X, int Y) InwardNormal(DockEdge edge) => Normalize(edge) switch
    {
        DockEdge.Left => (1, 0),
        DockEdge.Right => (-1, 0),
        _ => (0, -1)
    };

    /// <summary>RenderTransformOrigin on each icon so hover magnification grows inward.</summary>
    public static (double X, double Y) MagnificationOrigin(DockEdge edge) => Normalize(edge) switch
    {
        DockEdge.Left => (0d, 0.5d),
        DockEdge.Right => (1d, 0.5d),
        _ => (0.5d, 1d)
    };

    /// <summary>
    /// Inward magnification lift vector (in DIPs): upward for bottom dock, rightward for left
    /// dock, leftward for right dock. Strictly (0, 0) when motion is Reduced or Off.
    /// </summary>
    public static (double X, double Y) ComputeLiftVectorDip(
        DockEdge edge,
        double scale,
        double iconSizeDip,
        MotionLevel motion)
    {
        var magnitude = Math.Abs(DockMagnificationRules.ComputeLiftDip(scale, iconSizeDip, motion));
        if (magnitude <= 0d)
        {
            return (0d, 0d);
        }

        var (nx, ny) = InwardNormal(edge);
        return (nx * magnitude, ny * magnitude);
    }

    /// <summary>
    /// Extends the dock HWND inward by <paramref name="overflowPixels"/> to hold transparent
    /// magnification headroom without altering the dock's AppBar reservation or resting Bounds.
    /// </summary>
    public static PixelRect ExpandWindowForOverflow(
        PixelRect placement,
        PixelRect monitorBounds,
        DockEdge edge,
        int overflowPixels)
    {
        if (overflowPixels <= 0 || !placement.HasArea || !monitorBounds.HasArea)
        {
            return placement;
        }

        return Normalize(edge) switch
        {
            DockEdge.Left => placement with
            {
                Right = Math.Min(monitorBounds.Right, placement.Right + overflowPixels)
            },
            DockEdge.Right => placement with
            {
                Left = Math.Max(monitorBounds.Left, placement.Left - overflowPixels)
            },
            _ => placement with
            {
                Top = Math.Max(monitorBounds.Top, placement.Top - overflowPixels)
            }
        };
    }

    /// <summary>Re-applies requested strip thickness after Explorer returns ABM_QUERYPOS.</summary>
    public static PixelRect AdjustQueriedAppBarStrip(
        PixelRect queried,
        DockEdge edge,
        int requestedThickness) => edge switch
    {
        DockEdge.Left => queried with { Right = queried.Left + requestedThickness },
        DockEdge.Top => queried with { Bottom = queried.Top + requestedThickness },
        DockEdge.Right => queried with { Left = queried.Right - requestedThickness },
        _ => queried with { Top = queried.Bottom - requestedThickness }
    };

    /// <summary>Returns (workAreaEdge, stripInnerEdge) for AppBarWorkAreaCheck on any edge.</summary>
    public static (int WorkAreaEdge, int StripInnerEdge) WorkAreaEdgesForCheck(
        PixelRect liveWorkArea,
        PixelRect adjustedStrip,
        DockEdge edge) => edge switch
    {
        DockEdge.Left => (liveWorkArea.Left, adjustedStrip.Right),
        DockEdge.Top => (liveWorkArea.Top, adjustedStrip.Bottom),
        DockEdge.Right => (liveWorkArea.Right, adjustedStrip.Left),
        _ => (liveWorkArea.Bottom, adjustedStrip.Top)
    };

    /// <summary>Extent along the dock's icon layout axis: width for bottom, height for left/right.</summary>
    public static int AvailableExtentPixels(PixelRect safeArea, DockEdge edge) =>
        IsVertical(edge) ? safeArea.Height : safeArea.Width;

    /// <summary>Clips the dock strip along its layout axis to stay clear of cross-edge bars.</summary>
    public static PixelRect ClipStripCrossAxis(PixelRect strip, PixelRect safeArea, DockEdge edge)
    {
        if (!strip.HasArea || !safeArea.HasArea)
        {
            return strip;
        }

        if (IsVertical(edge))
        {
            var safeTop = Math.Max(strip.Top, safeArea.Top);
            var safeBottom = Math.Min(strip.Bottom, safeArea.Bottom);
            return safeBottom > safeTop
                ? strip with { Top = safeTop, Bottom = safeBottom }
                : strip;
        }

        var safeLeft = Math.Max(strip.Left, safeArea.Left);
        var safeRight = Math.Min(strip.Right, safeArea.Right);
        return safeRight > safeLeft
            ? strip with { Left = safeLeft, Right = safeRight }
            : strip;
    }

    /// <summary>
    /// Computes the vertical safe span for a left/right dock so it never overlaps a top bar
    /// (reserved or full-width) or a bottom/top native Windows taskbar.
    /// </summary>
    public static PixelRect ComputeSafeVerticalBounds(
        PixelRect monitorBounds,
        PixelRect liveWorkArea,
        PixelRect nativeTaskbarSafeArea,
        int? topBarBottomPx)
    {
        if (!monitorBounds.HasArea)
        {
            return monitorBounds;
        }

        var top = monitorBounds.Top;
        var bottom = monitorBounds.Bottom;
        if (nativeTaskbarSafeArea.HasArea)
        {
            top = Math.Max(top, nativeTaskbarSafeArea.Top);
            bottom = Math.Min(bottom, nativeTaskbarSafeArea.Bottom);
        }
        if (liveWorkArea.HasArea)
        {
            top = Math.Max(top, liveWorkArea.Top);
            bottom = Math.Min(bottom, liveWorkArea.Bottom);
        }
        if (topBarBottomPx is { } barBottom)
        {
            top = Math.Max(top, Math.Clamp(barBottom, monitorBounds.Top, monitorBounds.Bottom));
        }

        return bottom > top
            ? monitorBounds with { Top = top, Bottom = bottom }
            : monitorBounds;
    }

    /// <summary>
    /// Anchor point (in DIPs within the dock surface) from which a reappear/yield animation
    /// grows or fades: centre of the screen-facing edge.
    /// </summary>
    public static (double X, double Y) StripAnchorPointDip(
        PixelRect placement,
        DockEdge edge,
        double dpiScale)
    {
        var scale = dpiScale <= 0d ? 1d : dpiScale;
        var width = placement.Width / scale;
        var height = placement.Height / scale;
        return Normalize(edge) switch
        {
            DockEdge.Left => (0d, height * 0.5d),
            DockEdge.Right => (width, height * 0.5d),
            _ => (width * 0.5d, height)
        };
    }

    /// <summary>Insertion index when dragging an unpinned running app onto a pinned slot.</summary>
    public static int ComputePinInsertIndex(
        int targetIndex,
        double pointerAxis,
        double itemLength,
        int pinnedCount)
    {
        var safeCount = Math.Max(0, pinnedCount);
        if (targetIndex < 0 || targetIndex >= safeCount)
        {
            return safeCount;
        }

        var afterHalf = itemLength > 0d && pointerAxis >= itemLength * 0.5d;
        return Math.Clamp(afterHalf ? targetIndex + 1 : targetIndex, 0, safeCount);
    }

    /// <summary>Target index for <see cref="DockPinRules.Move"/> when dragging a pinned app.</summary>
    public static int ComputePinMoveIndex(
        int sourceIndex,
        int targetIndex,
        double pointerAxis,
        double itemLength,
        int pinnedCount)
    {
        var safeCount = Math.Max(0, pinnedCount);
        if (safeCount <= 1 || sourceIndex < 0 || sourceIndex >= safeCount)
        {
            return Math.Max(0, safeCount - 1);
        }

        if (targetIndex < 0 || targetIndex >= safeCount)
        {
            return safeCount - 1;
        }

        var afterHalf = itemLength > 0d && pointerAxis >= itemLength * 0.5d;
        var desiredSlot = afterHalf ? targetIndex + 1 : targetIndex;
        var adjusted = sourceIndex < desiredSlot ? desiredSlot - 1 : desiredSlot;
        return Math.Clamp(adjusted, 0, safeCount - 1);
    }
}
