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
}
