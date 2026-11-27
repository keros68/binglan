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

    /// <summary>
    /// Best-effort edge inference for dock yield checks when a third-party or floating
    /// side-docked taskbar does not span the exact full monitor length. Does not replace
    /// <see cref="GetEdge"/> for conservative work-area avoidance.
    /// </summary>
    public static DockEdge? InferDockedEdge(PixelRect taskbar, PixelRect monitor, int edgeTolerancePixels = 32)
    {
        if (!taskbar.HasArea || !monitor.HasArea || !taskbar.Intersects(monitor))
        {
            return null;
        }

        var tolerance = Math.Max(0, edgeTolerancePixels);
        if (taskbar.Height > taskbar.Width && taskbar.Width < monitor.Width / 2)
        {
            var distLeft = Math.Abs(taskbar.Left - monitor.Left);
            var distRight = Math.Abs(monitor.Right - taskbar.Right);
            if (distLeft <= tolerance && distLeft < distRight) return DockEdge.Left;
            if (distRight <= tolerance && distRight < distLeft) return DockEdge.Right;
        }

        if (taskbar.Width > taskbar.Height && taskbar.Height < monitor.Height / 2)
        {
            var distTop = Math.Abs(taskbar.Top - monitor.Top);
            var distBottom = Math.Abs(monitor.Bottom - taskbar.Bottom);
            if (distTop <= tolerance && distTop < distBottom) return DockEdge.Top;
            if (distBottom <= tolerance && distBottom < distTop) return DockEdge.Bottom;
        }

        return null;
    }

    /// <summary>
    /// Whether an auto-hidden taskbar on <paramref name="edge"/> is currently collapsed to
    /// its thin screen-edge strip rather than expanded over the desktop.
    /// </summary>
    public static bool IsCollapsedAutoHideTaskbar(PixelRect taskbar, PixelRect monitor, DockEdge edge)
    {
        if (!taskbar.HasArea || !monitor.HasArea)
        {
            return false;
        }

        var visiblePixels = edge switch
        {
            DockEdge.Top => taskbar.Bottom - monitor.Top,
            DockEdge.Left => taskbar.Right - monitor.Left,
            DockEdge.Right => monitor.Right - taskbar.Left,
            _ => monitor.Bottom - taskbar.Top
        };
        return visiblePixels <= TaskbarCornerRules.ExpandedTaskbarThresholdPixels;
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
