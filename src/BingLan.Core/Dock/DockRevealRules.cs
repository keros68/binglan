using BingLan.Core.Models;

namespace BingLan.Core.Dock;

/// <summary>
/// How a smart-hidden dock comes back. The bottom-edge zone keeps the dock shown while
/// the pointer is on its way to it from the screen edge: a dock flush with the edge
/// needs only the thin edge strip; a dock floating above the edge (bottom gap, or a
/// reservation that is still settling) extends the zone down to the edge so the dock
/// does not hide while the pointer crosses the gap between the edge and the dock.
/// When a visible native taskbar (or another bar) occupies the bottom edge, there is
/// no free edge to hit: pointing at the taskbar must not bring up the dock, so only
/// the handle reveals it, and the handle stays available even when the user chose the
/// edge alone — otherwise the dock could not be reached at all.
/// </summary>
public static class DockRevealRules
{
    public static bool IsInRevealZone(
        int cursorX,
        int cursorY,
        PixelRect dockBounds,
        PixelRect monitorBounds,
        int revealDepth)
    {
        var zoneTop = Math.Min(dockBounds.Bottom, monitorBounds.Bottom - Math.Max(1, revealDepth));
        return cursorY >= zoneTop
            && cursorY < monitorBounds.Bottom
            && cursorX >= dockBounds.Left
            && cursorX < dockBounds.Right;
    }

    /// <summary>
    /// Whether the monitor's bottom edge is free for the pointer, measured from the work
    /// area the dock itself does not reserve (smart hide or a released reservation).
    /// </summary>
    public static bool IsBottomEdgeFree(PixelRect workArea, PixelRect monitorBounds) =>
        workArea.Bottom >= monitorBounds.Bottom;

    public static bool UsesEdge(DockRevealMode mode, bool bottomEdgeFree) =>
        bottomEdgeFree && mode != DockRevealMode.HandleOnly;

    public static bool UsesHandle(DockRevealMode mode, bool bottomEdgeFree) =>
        !bottomEdgeFree || mode != DockRevealMode.EdgeOnly;
}
