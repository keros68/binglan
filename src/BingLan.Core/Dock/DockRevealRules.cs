using BingLan.Core.Models;

namespace BingLan.Core.Dock;

/// <summary>
/// How a smart-hidden dock comes back. The bottom-edge zone keeps the dock shown while
/// the pointer is on its way to it from the screen edge: a dock flush with the edge
/// needs only the thin edge strip; a dock floating above the edge (bottom gap, or a
/// reservation that is still settling) extends the zone down to the edge so the dock
/// does not hide while the pointer crosses the gap between the edge and the dock.
/// When a visible native taskbar (or another bar) occupies the bottom edge, pointing at
/// the taskbar must not bring up the dock: with the handle available only the handle
/// reveals it. A user who chose the edge alone gets no handle; the dock then comes up
/// only at the screen's last pixel rows, never over the rest of the taskbar.
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

    public static DockEdgeReveal EdgeReveal(DockRevealMode mode, bool bottomEdgeFree) => mode switch
    {
        DockRevealMode.HandleOnly => DockEdgeReveal.None,
        _ when bottomEdgeFree => DockEdgeReveal.Zone,
        DockRevealMode.EdgeOnly => DockEdgeReveal.EdgeStrip,
        _ => DockEdgeReveal.None
    };

    public static bool UsesHandle(DockRevealMode mode) => mode != DockRevealMode.EdgeOnly;
}

/// <summary>Which part of the bottom edge reveals a smart-hidden dock.</summary>
public enum DockEdgeReveal
{
    /// <summary>The edge does not reveal the dock.</summary>
    None,
    /// <summary>From the dock's bottom down to the screen edge.</summary>
    Zone,
    /// <summary>Only the screen's last pixel rows under the dock, over an occupied edge.</summary>
    EdgeStrip
}
