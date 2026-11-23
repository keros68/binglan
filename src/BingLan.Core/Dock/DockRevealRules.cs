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
        int revealDepth) =>
        IsInRevealZone(cursorX, cursorY, dockBounds, monitorBounds, revealDepth, DockEdge.Bottom);

    public static bool IsInRevealZone(
        int cursorX,
        int cursorY,
        PixelRect dockBounds,
        PixelRect monitorBounds,
        int revealDepth,
        DockEdge edge)
    {
        var depth = Math.Max(1, revealDepth);
        return DockEdgeRules.Normalize(edge) switch
        {
            DockEdge.Left =>
                cursorX >= monitorBounds.Left
                && cursorX < Math.Max(dockBounds.Left, monitorBounds.Left + depth)
                && cursorY >= dockBounds.Top
                && cursorY < dockBounds.Bottom,
            DockEdge.Right =>
                cursorX >= Math.Min(dockBounds.Right, monitorBounds.Right - depth)
                && cursorX < monitorBounds.Right
                && cursorY >= dockBounds.Top
                && cursorY < dockBounds.Bottom,
            _ =>
                cursorY >= Math.Min(dockBounds.Bottom, monitorBounds.Bottom - depth)
                && cursorY < monitorBounds.Bottom
                && cursorX >= dockBounds.Left
                && cursorX < dockBounds.Right
        };
    }

    /// <summary>
    /// Whether the monitor's bottom edge is free for the pointer, measured from the work
    /// area the dock itself does not reserve (smart hide or a released reservation).
    /// </summary>
    public static bool IsBottomEdgeFree(PixelRect workArea, PixelRect monitorBounds) =>
        IsEdgeFree(workArea, monitorBounds, DockEdge.Bottom);

    /// <summary>
    /// Whether the chosen dock edge is unoccupied by another bar (such as a visible taskbar).
    /// A bottom taskbar never blocks left/right edge reveal.
    /// </summary>
    public static bool IsEdgeFree(PixelRect workArea, PixelRect monitorBounds, DockEdge edge) => edge switch
    {
        DockEdge.Left => workArea.Left <= monitorBounds.Left,
        DockEdge.Right => workArea.Right >= monitorBounds.Right,
        DockEdge.Top => workArea.Top <= monitorBounds.Top,
        _ => workArea.Bottom >= monitorBounds.Bottom
    };

    /// <summary>Clamps the dock's screen-facing edge flush to the monitor for EdgeStrip reveal.</summary>
    public static PixelRect FlushEdgeBounds(PixelRect dockBounds, PixelRect monitorBounds, DockEdge edge) =>
        DockEdgeRules.Normalize(edge) switch
        {
            DockEdge.Left => dockBounds with { Left = monitorBounds.Left },
            DockEdge.Right => dockBounds with { Right = monitorBounds.Right },
            _ => dockBounds with { Bottom = monitorBounds.Bottom }
        };

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
