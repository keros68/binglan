using BingLan.Core.Dock;

namespace BingLan.Core.Desktop;

/// <summary>What the card windows should do about their z-order position.</summary>
public enum DesktopSurfaceAction
{
    /// <summary>Nothing changed that concerns the cards.</summary>
    None,

    /// <summary>Lift the cards just above the desktop surface.</summary>
    Raise,

    /// <summary>Put the cards back below every ordinary window.</summary>
    Lower
}

/// <summary>
/// Rules that keep cards above the wallpaper while the shell shows the desktop
/// and return them to the bottom of the ordinary window order when apps return.
/// </summary>
public static class DesktopSurfaceRules
{
    /// <summary>
    /// Whether the desktop surface currently covers the cards: the shell has raised
    /// it and no visible, un-minimised ordinary window is left above it. Reading the
    /// state from the other windows (not from the cards' own position) means lifting
    /// the cards cannot flip the verdict.
    /// </summary>
    public static bool IsDesktopShown(
        bool desktopSurfaceFound,
        int ordinaryWindowsAboveSurface,
        bool hasActiveApplication) =>
        desktopSurfaceFound && ordinaryWindowsAboveSurface == 0 && !hasActiveApplication;

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
    /// Whether a foreground or restoring window is a visible, non-topmost application
    /// window, including tool-styled launchers or login dialogs that
    /// <see cref="CountsAsOrdinaryBlocker"/> skips while passive in the background.
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
        int height,
        bool isRestoring = false) =>
        hasWindow
        && !isOwnProcess
        && !isMonitoredDesktopSurface
        && CountsAsOrdinaryWindow(shellProcess, className)
        && isVisible
        && (!isMinimized || isRestoring)
        && !isCloaked
        && !isTopmost
        && !isLayeredClickThrough
        && width > 1
        && height > 1;

    /// <summary>
    /// Whether an <c>EVENT_OBJECT_SHOW</c> on a non-shell top-level window while cards are
    /// raised should trigger immediate evaluation and exit settle checks. Passive tooltips,
    /// IME popups and click-through overlays (<c>WS_EX_NOACTIVATE</c> without
    /// <c>WS_EX_APPWINDOW</c>, or layered transparent) cannot lower cards and are skipped.
    /// </summary>
    public static bool ShouldEvaluateWindowShowWhileRaised(
        bool isAppWindow,
        bool isNoActivate,
        bool isLayeredClickThrough) =>
        !isLayeredClickThrough && (!isNoActivate || isAppWindow);

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
