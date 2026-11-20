using BingLan.Core.Dock;

namespace BingLan.Core.Taskbar;

/// <summary>Recognizes full-edge native taskbar rectangles without guessing floating layouts.</summary>
public static class TaskbarLayoutRules
{
    public static DockEdge? GetEdge(PixelRect taskbar, PixelRect monitor, PixelRect workArea)
    {
        if (!taskbar.HasArea || !monitor.HasArea) return null;
        if (taskbar.Left == monitor.Left && taskbar.Right == monitor.Right
            && taskbar.Height < monitor.Height / 2)
        {
            if (taskbar.Top == monitor.Top || taskbar.Bottom == workArea.Top) return DockEdge.Top;
            if (taskbar.Bottom == monitor.Bottom || taskbar.Top == workArea.Bottom) return DockEdge.Bottom;
        }
        if (taskbar.Top == monitor.Top && taskbar.Bottom == monitor.Bottom
            && taskbar.Width < monitor.Width / 2)
        {
            if (taskbar.Left == monitor.Left || taskbar.Right == workArea.Left) return DockEdge.Left;
            if (taskbar.Right == monitor.Right || taskbar.Left == workArea.Right) return DockEdge.Right;
        }
        return null;
    }

    public static PixelRect AvoidTaskbar(
        PixelRect workingArea, PixelRect monitor, PixelRect taskbar, DockEdge edge, int revealInset = 0) => edge switch
    {
        DockEdge.Top => workingArea with { Top = Math.Max(workingArea.Top, Math.Clamp(Math.Max(taskbar.Bottom, monitor.Top + revealInset), monitor.Top, monitor.Bottom - 1)) },
        DockEdge.Bottom => workingArea with { Bottom = Math.Min(workingArea.Bottom, Math.Clamp(Math.Min(taskbar.Top, monitor.Bottom - revealInset), monitor.Top + 1, monitor.Bottom)) },
        DockEdge.Left => workingArea with { Left = Math.Max(workingArea.Left, Math.Clamp(Math.Max(taskbar.Right, monitor.Left + revealInset), monitor.Left, monitor.Right - 1)) },
        DockEdge.Right => workingArea with { Right = Math.Min(workingArea.Right, Math.Clamp(Math.Min(taskbar.Left, monitor.Right - revealInset), monitor.Left + 1, monitor.Right)) },
        _ => workingArea
    };
}
