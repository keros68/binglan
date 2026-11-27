using BingLan.Core.Dock;

namespace BingLan.Core.Taskbar;

public readonly record struct TaskbarPassiveFocusInput(
    bool IsTaskbarForeground,
    bool AutoHideEnabled,
    long ElapsedSinceMinimizeMs,
    bool AlreadyRestoredForMinimize,
    bool PointerInTaskbarOrEdge,
    bool AnyModifierHeld,
    bool AnyMouseButtonDown,
    bool HasAttentionFlash,
    bool SystemPopupOpen);

/// <summary>
/// Pure gating rules for recovering from Windows 11's passive foreground fallback to
/// <c>Shell_TrayWnd</c> right after minimizing an ordinary application window, which
/// otherwise expands and strands an auto-hidden taskbar over an empty desktop.
/// </summary>
public static class TaskbarPassiveFocusRules
{
    public const long MinimizeGraceWindowMs = 450;

    /// <summary>
    /// Records a user minimize only when it originates from a regular window and the
    /// pointer is not over the taskbar (so clicking a taskbar button to minimize is never
    /// intercepted).
    /// </summary>
    public static bool ShouldRecordMinimize(bool isTaskbarOrShellWindow, bool pointerInTaskbar) =>
        !isTaskbarOrShellWindow && !pointerInTaskbar;

    public static bool ShouldRestoreDesktopFocus(TaskbarPassiveFocusInput input) =>
        input.IsTaskbarForeground
        && input.AutoHideEnabled
        && !input.AlreadyRestoredForMinimize
        && input.ElapsedSinceMinimizeMs >= 0
        && input.ElapsedSinceMinimizeMs <= MinimizeGraceWindowMs
        && !input.PointerInTaskbarOrEdge
        && !input.AnyModifierHeld
        && !input.AnyMouseButtonDown
        && !input.HasAttentionFlash
        && !input.SystemPopupOpen;

    public static bool IsPointerNearTaskbarOrEdge(
        int cursorX,
        int cursorY,
        PixelRect taskbarBounds,
        PixelRect monitorBounds,
        DockEdge taskbarEdge,
        int edgeTolerancePixels = 16)
    {
        if (taskbarBounds.HasArea
            && cursorX >= taskbarBounds.Left && cursorX < taskbarBounds.Right
            && cursorY >= taskbarBounds.Top && cursorY < taskbarBounds.Bottom)
        {
            return true;
        }

        if (!monitorBounds.HasArea)
        {
            return false;
        }

        var tol = Math.Max(2, edgeTolerancePixels);
        return taskbarEdge switch
        {
            DockEdge.Top => cursorX >= monitorBounds.Left && cursorX < monitorBounds.Right
                && cursorY >= monitorBounds.Top && cursorY < monitorBounds.Top + tol,
            DockEdge.Left => cursorY >= monitorBounds.Top && cursorY < monitorBounds.Bottom
                && cursorX >= monitorBounds.Left && cursorX < monitorBounds.Left + tol,
            DockEdge.Right => cursorY >= monitorBounds.Top && cursorY < monitorBounds.Bottom
                && cursorX >= monitorBounds.Right - tol && cursorX < monitorBounds.Right,
            _ => cursorX >= monitorBounds.Left && cursorX < monitorBounds.Right
                && cursorY >= monitorBounds.Bottom - tol && cursorY < monitorBounds.Bottom
        };
    }
}
