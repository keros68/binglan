using BingLan.Core.Dock;

namespace BingLan.Core.TopBar;

/// <summary>
/// Where the Windows hidden-icons panel goes when the top bar's tray button opens it:
/// centred under the button and kept inside the monitor.
/// </summary>
public static class TrayPanelPlacement
{
    public static (int Left, int Top) Calculate(
        PixelRect anchor,
        int panelWidth,
        int panelHeight,
        PixelRect monitor,
        int gapPixels)
    {
        var left = anchor.Left + (anchor.Width - panelWidth) / 2;
        left = Math.Clamp(left, monitor.Left, Math.Max(monitor.Left, monitor.Right - panelWidth));
        var top = Math.Clamp(
            anchor.Bottom + gapPixels,
            monitor.Top,
            Math.Max(monitor.Top, monitor.Bottom - panelHeight));
        return (left, top);
    }
}
