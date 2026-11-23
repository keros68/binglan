using BingLan.Core.Dock;

namespace BingLan.Core.TopBar;

/// <summary>
/// The thin strip at the top edge that keeps a smart-hidden top bar shown while the
/// pointer is on its way in.
/// </summary>
public static class TopBarRevealRules
{
    /// <summary>
    /// Horizontal width from an overlapping foreground window's top-right corner kept clear
    /// of smart-hide reveal triggers so moving to minimize/maximize/close buttons does not
    /// pull the top bar down over them (standard Windows caption buttons are ~138 DIP wide).
    /// </summary>
    public const double CaptionButtonsExclusionDip = 160d;

    public static bool IsInRevealZone(
        int cursorX,
        int cursorY,
        PixelRect zoneBounds,
        int revealDepth) =>
        IsInRevealZone(cursorX, cursorY, zoneBounds, revealDepth, excludedHorizontalSpan: null);

    public static bool IsInRevealZone(
        int cursorX,
        int cursorY,
        PixelRect zoneBounds,
        int revealDepth,
        (int Left, int Right)? excludedHorizontalSpan)
    {
        var depth = Math.Max(1, revealDepth);
        if (cursorY < zoneBounds.Top
            || cursorY >= zoneBounds.Top + depth
            || cursorX < zoneBounds.Left
            || cursorX >= zoneBounds.Right)
        {
            return false;
        }

        if (excludedHorizontalSpan is { } excluded
            && cursorX >= excluded.Left
            && cursorX < excluded.Right)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Computes the horizontal span covering an overlapping foreground window's top-right
    /// caption buttons, clamped to the window and top bar bounds in physical pixels.
    /// </summary>
    public static (int Left, int Right)? ComputeCaptionButtonExclusion(
        PixelRect foregroundBounds,
        PixelRect barBounds,
        int exclusionWidthPixels)
    {
        if (!foregroundBounds.HasArea
            || !barBounds.HasArea
            || exclusionWidthPixels <= 0
            || !foregroundBounds.Intersects(barBounds))
        {
            return null;
        }

        var rawLeft = Math.Max(foregroundBounds.Left, foregroundBounds.Right - exclusionWidthPixels);
        var left = Math.Max(rawLeft, barBounds.Left);
        var right = Math.Min(foregroundBounds.Right, barBounds.Right);
        return left < right ? (left, right) : null;
    }
}

/// <summary>
/// Whether the top edge is free to reserve, including a separately confirmed native taskbar.
/// </summary>
public static class TopBarReserveRules
{
    public static bool IsTopEdgeFree(PixelRect monitorBounds, PixelRect workingArea) =>
        workingArea.Top <= monitorBounds.Top;

    /// <summary>
    /// Whether the bar should hold a reservation now. The bar's own reservation pushes
    /// the working area down, so re-running the occupied check while registered would
    /// read itself as a foreign taskbar and let go on every settings change; a
    /// registered bar keeps its edge instead.
    /// </summary>
    public static bool ShouldReserve(
        bool requested,
        bool currentlyReserved,
        PixelRect monitorBounds,
        PixelRect workingArea,
        bool nativeTopEdgeOccupied = false) =>
        requested && !nativeTopEdgeOccupied
            && (currentlyReserved || IsTopEdgeFree(monitorBounds, workingArea));

    /// <summary>
    /// Confirms that Explorer actually pushed the monitor's working area top edge down to
    /// at least the bottom of the reserved strip. ABM_QUERYPOS / ABM_SETPOS often return
    /// TRUE even when a shared interior edge or shell state prevented shrinking the work area.
    /// </summary>
    public static bool IsWorkAreaReserved(PixelRect liveWorkArea, PixelRect reservedStrip) =>
        liveWorkArea.HasArea
        && reservedStrip.HasArea
        && liveWorkArea.Top >= reservedStrip.Bottom;

    /// <summary>
    /// Whether an active foreground window intrudes deep into a reserved top bar (e.g. a
    /// borderless window or an already-maximized window that ignored the work-area change),
    /// ignoring the intentional 1-pixel tuck at the bar's bottom seam.
    /// </summary>
    public static bool IsWindowCoveringReservedBar(PixelRect foregroundBounds, PixelRect barBounds)
    {
        if (!foregroundBounds.HasArea || !barBounds.HasArea)
        {
            return false;
        }

        if (foregroundBounds.Left >= barBounds.Right || foregroundBounds.Right <= barBounds.Left)
        {
            return false;
        }

        var midpointY = barBounds.Top + Math.Max(1, barBounds.Height / 2);
        return foregroundBounds.Top < midpointY && foregroundBounds.Bottom > midpointY;
    }
}
