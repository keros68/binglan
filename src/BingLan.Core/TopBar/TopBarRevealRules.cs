using BingLan.Core.Dock;

namespace BingLan.Core.TopBar;

/// <summary>
/// The thin strip at the top edge that keeps a smart-hidden top bar shown while the
/// pointer is on its way in. The bar is always flush with the edge and full width, so
/// the zone is the strip between the monitor top and a small reveal depth.
/// </summary>
public static class TopBarRevealRules
{
    public static bool IsInRevealZone(
        int cursorX,
        int cursorY,
        PixelRect monitorBounds,
        int revealDepth)
    {
        var depth = Math.Max(1, revealDepth);
        return cursorY >= monitorBounds.Top
            && cursorY < monitorBounds.Top + depth
            && cursorX >= monitorBounds.Left
            && cursorX < monitorBounds.Right;
    }
}

/// <summary>
/// Whether the top edge of a monitor is free to reserve: only a working area whose top
/// sits below the monitor top (a taskbar moved to the top, Windows 10) blocks it. An
/// auto-hiding taskbar does not shrink the working area, so that case stays undetected
/// and is handled by the formal QA matrix.
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
        PixelRect workingArea) =>
        requested && (currentlyReserved || IsTopEdgeFree(monitorBounds, workingArea));
}
