using BingLan.Core.Dock;

namespace BingLan.Core.Desktop;

/// <summary>What the card windows should do about their z-order position.</summary>
public enum DesktopSurfaceAction
{
    /// <summary>Nothing changed that concerns the cards.</summary>
    None,

    /// <summary>Lift the cards into the topmost band, under the taskbar.</summary>
    Raise,

    /// <summary>Put the cards back below every ordinary window.</summary>
    Lower
}

/// <summary>
/// Rules that keep the desktop cards visible while the shell shows the desktop.
/// Pressing "show desktop" makes Explorer minimise every ordinary window and raise
/// the desktop surface above the non-topmost band, which covers the cards that sit
/// at the bottom of that band behind the wallpaper. Only the topmost band is left
/// alone, so while that state lasts the cards are lifted into it — under the taskbar
/// so the taskbar stays reachable — and put back once ordinary windows return.
/// </summary>
public static class DesktopSurfaceRules
{
    /// <summary>
    /// Whether the desktop surface currently covers the cards: the shell has raised
    /// it and no visible, un-minimised ordinary window is left above it. Reading the
    /// state from the other windows (not from the cards' own position) means lifting
    /// the cards cannot flip the verdict.
    /// </summary>
    public static bool IsDesktopShown(bool desktopSurfaceFound, int ordinaryWindowsAboveSurface) =>
        IsDesktopShown(desktopSurfaceFound, ordinaryWindowsAboveSurface, desktopSurfaceRaised: true);

    /// <summary>
    /// Whether the shell is actively showing the desktop: the desktop surface exists,
    /// has actually been raised over the desktop layer (rather than sitting passively at
    /// the shell bottom on an empty desktop), and no visible ordinary window is open.
    /// </summary>
    public static bool IsDesktopShown(
        bool desktopSurfaceFound,
        int ordinaryWindowsAboveSurface,
        bool desktopSurfaceRaised) =>
        desktopSurfaceFound
        && ordinaryWindowsAboveSurface == 0
        && desktopSurfaceRaised;

    /// <summary>
    /// Whether Explorer's desktop surface (<c>WorkerW</c> or <c>Progman</c>) is raised
    /// above the normal desktop layer. On an ordinary empty desktop (fresh boot, closing
    /// the last app, or minimising windows individually / via Win+M) <c>Progman</c> stays
    /// below <c>HWND_BOTTOM</c>, so all visible desktop surfaces already sit above it and
    /// must remain non-topmost so newly launched windows naturally appear on top of them.
    /// </summary>
    public static bool IsDesktopSurfaceRaised(
        bool desktopSurfaceIsTopmost,
        bool cardsRaised,
        bool raisedForTopmostSurface,
        string baseSurfaceClassName,
        int visibleSurfaceCount,
        int visibleSurfacesAboveDesktop)
    {
        if (desktopSurfaceIsTopmost)
        {
            return true;
        }

        if (!cardsRaised)
        {
            return visibleSurfaceCount > 0 && visibleSurfacesAboveDesktop < visibleSurfaceCount;
        }

        // In Win32 the shell window (Progman) is pinned below HWND_BOTTOM unless topmost;
        // once topmost is gone and no surface sits below Progman, "show desktop" is inactive.
        if (baseSurfaceClassName == "Progman"
            && (visibleSurfaceCount == 0 || visibleSurfacesAboveDesktop >= visibleSurfaceCount))
        {
            return false;
        }

        return !raisedForTopmostSurface || baseSurfaceClassName != "Progman";
    }

    /// <summary>
    /// Whether a window above the surface counts as ordinary. Windows of the shell's
    /// own process are its surface windows (wallpaper, taskbars) — except the
    /// file-manager windows and standard shell dialogs (Run, Properties, file progress),
    /// which run in explorer.exe too but are ordinary windows that cards must stay below.
    /// </summary>
    public static bool CountsAsOrdinaryWindow(bool shellProcess, string className) =>
        !shellProcess
        || className is "CabinetWClass" or "ExploreWClass" or "#32770" or "OperationStatusWindow";

    /// <summary>
    /// Whether a visible, non-topmost window above the desktop surface is an ordinary
    /// application window that proves the shell is not showing the desktop. Tool windows,
    /// non-activating floating widgets, 1px helper windows, click-through layers and
    /// cloaked hosts are not minimised by "show desktop", so they must not block raising
    /// the desktop cards.
    /// </summary>
    public static bool CountsAsOrdinaryBlocker(
        bool shellProcess,
        string className,
        bool isAppWindow,
        bool isToolWindow,
        bool isNoActivate,
        bool isCloaked,
        bool isMinimized,
        bool isLayeredClickThrough,
        bool isDisabled,
        bool hasTitle,
        int width,
        int height) =>
        CountsAsOrdinaryWindow(shellProcess, className)
        && WindowActivationRules.ShouldIncludeTopLevelWindow(
            isAppWindow,
            isToolWindow,
            isNoActivate,
            isCloaked,
            isMinimized,
            isLayeredClickThrough,
            isDisabled,
            hasTitle,
            width,
            height);

    /// <summary>
    /// Whether the live foreground window proves the user is interacting with a visible,
    /// non-topmost application window (including tool-styled launchers or login dialogs
    /// that <see cref="CountsAsOrdinaryBlocker"/> skips while passive in the background).
    /// Topmost shell flyouts (Start menu, Quick Settings) are excluded so opening them
    /// during "show desktop" does not drop cards behind a still-topmost <c>WorkerW</c>.
    /// </summary>
    public static bool CountsAsForegroundBlocker(
        bool hasWindow,
        bool isOwnProcess,
        bool isMonitoredDesktopSurface,
        bool shellProcess,
        string className,
        bool isVisible,
        bool isMinimized,
        bool isCloaked,
        bool isTopmost,
        bool isLayeredClickThrough,
        int width,
        int height) =>
        hasWindow
        && !isOwnProcess
        && !isMonitoredDesktopSurface
        && CountsAsOrdinaryWindow(shellProcess, className)
        && isVisible
        && !isMinimized
        && !isCloaked
        && !isTopmost
        && !isLayeredClickThrough
        && width > 1
        && height > 1;

    /// <summary>
    /// Whether a top-level window hosts the live shell desktop view (<c>SHELLDLL_DefView</c>),
    /// which lives on <c>Progman</c> normally and moves to <c>WorkerW</c> during wallpaper
    /// transitions or "show desktop".
    /// </summary>
    public static bool IsDesktopSurfaceHost(string className, bool hasShellDefView) =>
        hasShellDefView && className is "Progman" or "WorkerW";

    /// <summary>
    /// Whether an Explorer window is the raised topmost desktop surface that covers
    /// existing topmost toolbars and cards during "show desktop".
    /// </summary>
    public static bool IsTopmostShellDesktopSurface(
        bool shellProcess,
        string className,
        bool isVisible,
        bool isCloaked,
        bool isTopmost,
        int width,
        int height,
        bool isClickThrough = false,
        bool isTransparentLayered = false) =>
        shellProcess
        && isVisible
        && !isCloaked
        && isTopmost
        && !isClickThrough
        && !isTransparentLayered
        && width > 100
        && height > 100
        && className is "Progman" or "WorkerW";

    /// <summary>What the cards should do now, given whether they were already lifted.</summary>
    public static DesktopSurfaceAction ActionFor(bool cardsRaised, bool desktopShown) =>
        ActionFor(cardsRaised, desktopShown, anySurfaceCovered: false);

    /// <summary>
    /// What the desktop surfaces should do now. Even when already in the shown-desktop
    /// state, Explorer toggling "show desktop" can raise <c>WorkerW</c> above already-lifted
    /// surfaces, so any covered surface triggers another raise pass.
    /// </summary>
    public static DesktopSurfaceAction ActionFor(
        bool cardsRaised,
        bool desktopShown,
        bool anySurfaceCovered) =>
        ActionFor(cardsRaised, desktopShown, anySurfaceCovered, holdPreRaise: false);

    /// <summary>
    /// What the desktop surfaces should do now, accounting for a brief pre-raise window
    /// initiated right before the app itself asks Explorer to toggle "show desktop" so
    /// cards are already above <c>Progman</c>/<c>WorkerW</c> before it rises (preventing
    /// a wallpaper blink while ordinary windows are still mid-minimise).
    /// </summary>
    public static DesktopSurfaceAction ActionFor(
        bool cardsRaised,
        bool desktopShown,
        bool anySurfaceCovered,
        bool holdPreRaise) =>
        desktopShown
            ? (!cardsRaised || anySurfaceCovered ? DesktopSurfaceAction.Raise : DesktopSurfaceAction.None)
            : (holdPreRaise ? DesktopSurfaceAction.None : (cardsRaised ? DesktopSurfaceAction.Lower : DesktopSurfaceAction.None));
}
