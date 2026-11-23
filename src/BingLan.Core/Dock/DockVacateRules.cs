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
