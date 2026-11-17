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
        desktopSurfaceFound && ordinaryWindowsAboveSurface == 0;

    /// <summary>
    /// Whether a window above the surface counts as ordinary. Windows of the shell's
    /// own process are its surface windows (wallpaper, taskbars) — except the
    /// file-manager windows, which run in explorer.exe too but are ordinary windows:
    /// one being open means the desktop is not shown, and the cards must go below it.
    /// </summary>
    public static bool CountsAsOrdinaryWindow(bool shellProcess, string className) =>
        !shellProcess || className is "CabinetWClass" or "ExploreWClass";

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
        int height) =>
        shellProcess
        && isVisible
        && !isCloaked
        && isTopmost
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
        desktopShown
            ? (!cardsRaised || anySurfaceCovered ? DesktopSurfaceAction.Raise : DesktopSurfaceAction.None)
            : (cardsRaised ? DesktopSurfaceAction.Lower : DesktopSurfaceAction.None);
}
