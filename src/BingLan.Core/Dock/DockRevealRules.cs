namespace BingLan.Core.Dock;

/// <summary>
/// The strip that keeps a smart-hidden dock shown while the pointer is on its way to
/// it from the screen edge. A dock flush with the edge needs only the thin edge strip;
/// a dock floating above the edge (taskbar strip, bottom gap, or a reservation that is
/// still settling) extends the zone down to the edge so the dock does not hide while
/// the pointer crosses the gap between the edge and the dock.
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
}
