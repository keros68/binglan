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

    /// <summary>
    /// Short top-edge dwell required before revealing a smart-hidden top bar over an
    /// overlapping/maximized window, so quick browser-tab clicks at y=0 do not pull the
    /// bar down accidentally while intentional hovering still reveals it reliably.
    /// </summary>
    public const long MaximizedOverlapRevealDelayMs = 350;

    public static bool ShouldTriggerRevealAfterDwell(
        bool inRevealZone,
        bool overlapIsMaximizedOrCovering,
        bool isCurrentlyShown,
        ref long? enteredRevealZoneSinceTickMs,
        bool leftButtonDown,
        long nowTickMs)
    {
        if (!inRevealZone || leftButtonDown)
        {
            enteredRevealZoneSinceTickMs = null;
            return false;
        }
        enteredRevealZoneSinceTickMs ??= nowTickMs;
        return isCurrentlyShown || !overlapIsMaximizedOrCovering
            || nowTickMs - enteredRevealZoneSinceTickMs.Value >= MaximizedOverlapRevealDelayMs;
    }

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
        (int Left, int Right)? excludedHorizontalSpan,
        DockEdge edge = DockEdge.Top)
    {
        var depth = Math.Max(1, revealDepth);
        var triggerTop = edge == DockEdge.Bottom ? Math.Max(zoneBounds.Top, zoneBounds.Bottom - depth) : zoneBounds.Top;
        var triggerBottom = edge == DockEdge.Bottom ? zoneBounds.Bottom : Math.Min(zoneBounds.Bottom, zoneBounds.Top + depth);
        if (cursorY < triggerTop
            || cursorY >= triggerBottom
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
    /// Whether the pointer is on a visible bar. Once revealed, its entire width is interactive.
    /// </summary>
    public static bool IsPointerOverBar(
        int cursorX,
        int cursorY,
        bool isVisible,
        PixelRect barBounds) =>
        isVisible && barBounds.HasArea
        && cursorX >= barBounds.Left && cursorX < barBounds.Right
        && cursorY >= barBounds.Top && cursorY < barBounds.Bottom;

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
    /// Resolves the monitor host area used by an unreserved / smart-hide top bar. Uses the
    /// monitor bounds constrained only by a confirmed native top taskbar rather than the live
    /// work area, so a stale or foreign top AppBar offset never floats the bar below the top
    /// screen edge.
    /// </summary>
    public static PixelRect ResolveUnreservedHostArea(PixelRect monitorBounds, PixelRect nativeTaskbarSafeArea) =>
        nativeTaskbarSafeArea.HasArea ? nativeTaskbarSafeArea : monitorBounds;

    public static PixelRect ResolveBottomHostArea(PixelRect nativeTaskbarSafeArea, int? bottomDockTop) =>
        bottomDockTop is { } top && top > nativeTaskbarSafeArea.Top && top < nativeTaskbarSafeArea.Bottom
            ? nativeTaskbarSafeArea with { Bottom = top }
            : nativeTaskbarSafeArea;

    /// <summary>
    /// Verifies that a top AppBar reservation actually anchored at the monitor's top edge
    /// instead of being pushed downward by a stale or hidden foreign top AppBar.
    /// </summary>
    public static bool IsStripAnchoredAtTop(
        PixelRect strip,
        PixelRect monitorBounds,
        int maxTopDriftPixels = 0) =>
        strip.HasArea
        && monitorBounds.HasArea
        && strip.Top >= monitorBounds.Top
        && strip.Top <= monitorBounds.Top + Math.Max(0, maxTopDriftPixels);

    /// <summary>
    /// Confirms that Explorer actually pushed the monitor's working area top edge down to
    /// at least the bottom of the reserved strip. ABM_QUERYPOS / ABM_SETPOS often return
    /// TRUE even when a shared interior edge or shell state prevented shrinking the work area.
    /// </summary>
    public static bool IsWorkAreaReserved(PixelRect liveWorkArea, PixelRect reservedStrip, DockEdge edge = DockEdge.Top) =>
        liveWorkArea.HasArea
        && reservedStrip.HasArea
        && (edge == DockEdge.Bottom ? liveWorkArea.Bottom <= reservedStrip.Top : liveWorkArea.Top >= reservedStrip.Bottom);

    /// <summary>
    /// Bottom-edge tolerance in physical pixels so the intentional 1-pixel tuck at the bar's
    /// bottom seam (plus minor DWM frame rounding) does not count as intruding into the bar.
    /// </summary>
    public const int SeamTolerancePixels = 4;

    /// <summary>
    /// Whether an active foreground window intrudes into a reserved top bar beyond the
    /// bottom-seam tolerance (e.g. a borderless window, a restored window opening at Y=0,
    /// or a maximized window that ignored the work-area change).
    /// </summary>
    public static bool IsWindowCoveringReservedBar(
        PixelRect foregroundBounds, PixelRect barBounds, DockEdge edge = DockEdge.Top)
    {
        if (!foregroundBounds.HasArea || !barBounds.HasArea)
        {
            return false;
        }

        if (foregroundBounds.Left >= barBounds.Right || foregroundBounds.Right <= barBounds.Left)
        {
            return false;
        }

        if (edge == DockEdge.Bottom)
        {
            var intrusionTop = Math.Min(barBounds.Bottom - 1, barBounds.Top + SeamTolerancePixels);
            return foregroundBounds.Bottom > intrusionTop && foregroundBounds.Top < barBounds.Bottom;
        }
        var intrusionBottom = Math.Max(barBounds.Top + 1, barBounds.Bottom - SeamTolerancePixels);
        return foregroundBounds.Top < intrusionBottom && foregroundBounds.Bottom > barBounds.Top;
    }

    /// <summary>
    /// Computes a one-time top-edge nudge for a newly opened, ordinary, non-maximized external
    /// window whose visible top intrudes into a reserved top bar (such as Chromium/Electron
    /// apps restoring a saved Y=0 coordinate that ignores <c>SPI_GETWORKAREA</c>). Returns null
    /// when the window is ineligible or already sits below the reserved strip.
    /// </summary>
    public static PixelRect? ComputeNudgedWindowRect(
        PixelRect visibleBounds,
        PixelRect windowRect,
        PixelRect barBounds,
        PixelRect workArea,
        bool isMaximized,
        bool isFullScreen,
        bool isTopmost,
        bool isToolOrPopup,
        bool hasOwner,
        bool hasCaption)
    {
        if (isMaximized || isFullScreen || isTopmost || isToolOrPopup || hasOwner || !hasCaption)
        {
            return null;
        }

        if (!visibleBounds.HasArea || !windowRect.HasArea || !barBounds.HasArea || !workArea.HasArea)
        {
            return null;
        }

        if (visibleBounds.Right <= barBounds.Left || visibleBounds.Left >= barBounds.Right)
        {
            return null;
        }

        var targetVisibleTop = Math.Max(barBounds.Bottom, workArea.Top);
        if (visibleBounds.Top >= targetVisibleTop - SeamTolerancePixels || visibleBounds.Bottom <= barBounds.Top)
        {
            return null;
        }

        var deltaY = targetVisibleTop - visibleBounds.Top;
        if (deltaY <= 0)
        {
            return null;
        }

        var nextTop = windowRect.Top + deltaY;
        var nextBottom = windowRect.Bottom + deltaY;
        var bottomFrameInset = Math.Max(0, windowRect.Bottom - visibleBounds.Bottom);
        var maxWindowBottom = workArea.Bottom + bottomFrameInset;
        if (nextBottom > maxWindowBottom)
        {
            var minHeight = Math.Min(windowRect.Height, 120);
            nextBottom = Math.Max(nextTop + minHeight, maxWindowBottom);
        }

        var nudged = new PixelRect(windowRect.Left, nextTop, windowRect.Right, nextBottom);
        return nudged.HasArea && nudged != windowRect ? nudged : null;
    }
}
