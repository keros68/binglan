using BingLan.Core.Models;

namespace BingLan.Core.Dock;

/// <summary>
/// Decides when a reserved dock gives up its work-area strip: while a window is
/// maximized on the dock's monitor, the strip is released so the window can use
/// the full height, and the dock follows smart-hide rules until it is restored.
/// Smart hide already clears the dock for maximized windows, so the rule only
/// applies to the reserved mode.
/// </summary>
public static class DockVacateRules
{
    public static bool ShouldReleaseForMaximized(
        DockVisibilityMode visibilityMode,
        bool releaseWhenMaximized,
        bool foregroundMaximizedOnDockMonitor) =>
        visibilityMode == DockVisibilityMode.ReserveWorkArea
        && releaseWhenMaximized
        && foregroundMaximizedOnDockMonitor;

    /// <summary>
    /// Per-edge tolerance in physical pixels when deciding whether a foreground window covers a
    /// monitor. Fractional DPI scales (such as 125% or 150%, e.g. 2560x1600 -> 1706.67x1066.67 DIP)
    /// can round a full-screen window's DWM extended frame bounds inward by 1-2 physical pixels.
    /// </summary>
    public const int FullScreenEdgeTolerancePixels = 2;

    /// <summary>
    /// Intrusion tolerance in physical pixels along the dock's inner edge so minor frame rounding
    /// next to the work-area boundary does not count as covering a reserved dock.
    /// </summary>
    public const int ReservedDockIntrusionTolerancePixels = 4;

    public static bool CoversMonitor(
        PixelRect windowBounds,
        PixelRect monitorBounds,
        int edgeTolerancePixels = FullScreenEdgeTolerancePixels)
    {
        if (!windowBounds.HasArea || !monitorBounds.HasArea)
        {
            return false;
        }

        var tolerance = Math.Max(0, edgeTolerancePixels);
        return windowBounds.Left <= monitorBounds.Left + tolerance
            && windowBounds.Top <= monitorBounds.Top + tolerance
            && windowBounds.Right >= monitorBounds.Right - tolerance
            && windowBounds.Bottom >= monitorBounds.Bottom - tolerance;
    }

    /// <summary>
    /// Whether an external non-overlay foreground window spans along the dock's screen edge and
    /// intrudes into the reserved dock strip (for example a fullscreen video clipped below a top bar
    /// or a maximized browser that ignored the AppBar work-area reservation). Ordinary floating
    /// windows dragged partway onto the dock do not span the monitor edge and return false.
    /// </summary>
    public static bool IsSpanningWindowCoveringReservedDock(
        PixelRect foregroundBounds,
        PixelRect dockBounds,
        PixelRect monitorBounds,
        DockEdge edge)
    {
        if (!foregroundBounds.HasArea || !dockBounds.HasArea || !monitorBounds.HasArea)
        {
            return false;
        }

        var normalizedEdge = DockEdgeRules.Normalize(edge);
        var edgeTol = FullScreenEdgeTolerancePixels;
        var seamTol = ReservedDockIntrusionTolerancePixels;

        return normalizedEdge switch
        {
            DockEdge.Left =>
                foregroundBounds.Left <= monitorBounds.Left + edgeTol
                && foregroundBounds.Left < dockBounds.Right - seamTol
                && foregroundBounds.Right > dockBounds.Left
                && (foregroundBounds.Top <= monitorBounds.Top + edgeTol
                    || foregroundBounds.Bottom >= monitorBounds.Bottom - edgeTol)
                && foregroundBounds.Height >= monitorBounds.Height * 85 / 100,
            DockEdge.Right =>
                foregroundBounds.Right >= monitorBounds.Right - edgeTol
                && foregroundBounds.Right > dockBounds.Left + seamTol
                && foregroundBounds.Left < dockBounds.Right
                && (foregroundBounds.Top <= monitorBounds.Top + edgeTol
                    || foregroundBounds.Bottom >= monitorBounds.Bottom - edgeTol)
                && foregroundBounds.Height >= monitorBounds.Height * 85 / 100,
            _ =>
                foregroundBounds.Bottom >= monitorBounds.Bottom - edgeTol
                && foregroundBounds.Bottom > dockBounds.Top + seamTol
                && foregroundBounds.Top < dockBounds.Bottom
                && foregroundBounds.Left <= monitorBounds.Left + edgeTol
                && foregroundBounds.Right >= monitorBounds.Right - edgeTol
        };
    }

    /// <summary>
    /// Decides whether a Dock actively reserving work area should drop behind the foreground
    /// window. Unlike smart-hide mode (where a maximized window legitimately fills an unreserved
    /// monitor), an ordinary maximized window obeying our AppBar reservation stops at the work-area
    /// boundary and never covers the monitor or spans across the reserved dock strip.
    /// </summary>
    public static bool ShouldReservedDockYield(
        bool isTrueFullScreen,
        bool coversMonitor,
        bool foregroundOwnProcess,
        bool isToolOrOverlayWindow,
        PixelRect foregroundBounds,
        PixelRect dockBounds,
        PixelRect monitorBounds,
        DockEdge edge)
    {
        if (isTrueFullScreen)
        {
            return true;
        }

        if (foregroundOwnProcess || isToolOrOverlayWindow)
        {
            return false;
        }

        if (coversMonitor)
        {
            return true;
        }

        return IsSpanningWindowCoveringReservedDock(foregroundBounds, dockBounds, monitorBounds, edge);
    }

    /// <summary>
    /// A window that fills the monitor because it is maximized (for example while the
    /// taskbar auto-hides), a normal resizable desktop window with standard chrome
    /// (caption + sizing frame), or a tool/click-through overlay is working space the
    /// user may want the dock over, not a full-screen app. Only a window that covers
    /// the monitor without being maximized, without being a tool/overlay window, and
    /// without standard resizable chrome (unless explicitly topmost) counts as truly
    /// full-screen and keeps the dock fully out of sight.
    /// </summary>
    public static bool IsTrueFullScreen(
        bool coversMonitor,
        bool foregroundIsZoomed,
        bool hasStandardWindowChrome = false,
        bool isToolOrOverlayWindow = false,
        bool isTopmost = false) =>
        coversMonitor
        && !foregroundIsZoomed
        && !isToolOrOverlayWindow
        && (!hasStandardWindowChrome || isTopmost);

    /// <summary>
    /// Whether an unowned foreground window may inherit full-screen status from a same-process
    /// full-screen window immediately behind it in Z-order (for example Chromium's unowned F11
    /// download bubble or a Save As dialog). Normal standalone top-level application windows
    /// (maximized, or with standard resizable caption/frame chrome without popup/dialog/tool
    /// characteristics) return false so switching between two windows of the same browser
    /// restores normal Dock/TopBar behavior.
    /// </summary>
    public static bool IsTransientFullScreenChildWindow(
        bool isZoomed,
        bool isPopup,
        bool isDialogClass,
        bool isToolWindow,
        bool hasStandardResizableFrame,
        bool isAppWindow)
    {
        if (isZoomed)
        {
            return false;
        }
        if (isDialogClass || isToolWindow)
        {
            return true;
        }
        if (isPopup && !hasStandardResizableFrame && !isAppWindow)
        {
            return true;
        }
        return !hasStandardResizableFrame && !isAppWindow;
    }

    /// <summary>
    /// Effective full-screen state on the bar's monitor: true when either the foreground window
    /// itself is a true full-screen surface, or the foreground window is an owned dialog/popup
    /// (e.g. a browser download bubble or Save As dialog) sitting directly in front of a true
    /// full-screen window belonging to the same owner/process on that monitor.
    /// </summary>
    public static bool IsEffectiveFullScreen(
        bool foregroundIsTrueFullScreen,
        bool backingWindowIsTrueFullScreen) =>
        foregroundIsTrueFullScreen || backingWindowIsTrueFullScreen;

    /// <summary>
    /// Whether a taskbar flash notification (<c>HSHELL_FLASH</c>) may temporarily reveal a
    /// smart-hidden dock. Never reveals over a true full-screen app or while suppressed.
    /// </summary>
    public static bool ShouldRevealOnAttention(
        bool usesSmartHide,
        bool revealOnAttention,
        bool isFullScreen,
        bool isSuppressed = false) =>
        usesSmartHide && revealOnAttention && !isFullScreen && !isSuppressed;

    /// <summary>
    /// Decides whether the dock should step aside for a foreground Windows shell surface
    /// (taskbar, Start menu, Quick Settings, tray overflow). When the foreground is the
    /// taskbar itself on a different screen edge and does not overlap the dock (for example
    /// a bottom Windows taskbar paired with a left/right dock), the dock stays visible.
    /// </summary>
    public static bool ShouldYieldToSystemSurface(
        bool isSystemMenuForeground,
        bool isTaskbarWindow,
        DockEdge dockEdge,
        DockEdge? nativeTaskbarEdge,
        bool surfaceIntersectsDock)
    {
        if (!isSystemMenuForeground)
        {
            return false;
        }

        if (isTaskbarWindow
            && nativeTaskbarEdge is { } taskbarEdge
            && taskbarEdge != DockEdgeRules.Normalize(dockEdge)
            && !surfaceIntersectsDock)
        {
            return false;
        }

        return true;
    }
}
