using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using BingLan.App.Interop;
using BingLan.Core.Dock;
using BingLan.Core.Taskbar;

namespace BingLan.App.Services;

/// <summary>
/// Reports whether the foreground window covers its whole monitor (a game, video or
/// presentation), checked on a slow timer so the desktop can pause non-essential work.
/// </summary>
internal sealed class FullScreenWatcher : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);
    private readonly DispatcherTimer _timer;

    internal FullScreenWatcher()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = CheckInterval };
        _timer.Tick += (_, _) => Check();
    }

    internal bool IsFullScreenInFront { get; private set; }

    /// <summary>
    /// Whether the foreground window is truly full-screen (covers its monitor and is NOT a
    /// maximized ordinary window). Used where maximized windows over an auto-hidden taskbar
    /// should not suspend edge protection.
    /// </summary>
    internal bool IsTrueFullScreenInFront { get; private set; }

    /// <summary>
    /// Whether the foreground window is a full-screen game or video surface (covers its
    /// monitor, not maximized, not a tool/click-through overlay, and without standard
    /// resizable chrome unless explicitly topmost).
    /// </summary>
    internal bool IsGameLikeFullScreenInFront { get; private set; }

    /// <summary>
    /// Monitor handle (<c>HMONITOR</c>) currently covered by a full-screen foreground window,
    /// or 0 when no monitor is covered. Populated on the existing single timer with zero extra ticks;
    /// global performance sampling intentionally continues throttling via <see cref="IsFullScreenInFront"/>
    /// to protect frame pacing during full-screen gaming, while per-monitor queries support
    /// display-scoped surfaces.
    /// </summary>
    internal nint FullScreenMonitorHandle { get; private set; }

    internal nint TrueFullScreenMonitorHandle { get; private set; }

    internal nint GameLikeFullScreenMonitorHandle { get; private set; }

    internal bool IsFullScreenOnMonitor(nint monitorHandle) =>
        monitorHandle != 0 && FullScreenMonitorHandle == monitorHandle;

    internal bool IsTrueFullScreenOnMonitor(nint monitorHandle) =>
        monitorHandle != 0 && TrueFullScreenMonitorHandle == monitorHandle;

    internal bool IsGameLikeFullScreenOnMonitor(nint monitorHandle) =>
        monitorHandle != 0 && GameLikeFullScreenMonitorHandle == monitorHandle;

    internal event Action? Changed;

    internal void Start()
    {
        Check();
        _timer.Start();
    }

    public void Dispose() => _timer.Stop();

    private void Check()
    {
        var foreground = DockNativeMethods.GetForegroundWindow();
        var directFullScreen = TryGetFullScreenMonitor(foreground, out var fullScreenMonitor);
        var directTrueFullScreen = directFullScreen && !DockNativeMethods.IsZoomed(foreground);
        var gameLikeFullScreen = IsTrueFullScreenContext(foreground);
        var trueFullScreen = directTrueFullScreen || gameLikeFullScreen;
        var fullScreen = directFullScreen || gameLikeFullScreen;
        var resolvedMonitor = fullScreenMonitor != 0
            ? fullScreenMonitor
            : (fullScreen && foreground != 0
                ? DockNativeMethods.MonitorFromWindow(foreground, DockNativeMethods.MonitorDefaultToNearest)
                : 0);
        var nextFullScreenMonitor = fullScreen ? resolvedMonitor : 0;
        var nextTrueMonitor = trueFullScreen ? resolvedMonitor : 0;
        var nextGameLikeMonitor = gameLikeFullScreen ? resolvedMonitor : 0;
        if (fullScreen == IsFullScreenInFront
            && trueFullScreen == IsTrueFullScreenInFront
            && gameLikeFullScreen == IsGameLikeFullScreenInFront
            && nextFullScreenMonitor == FullScreenMonitorHandle
            && nextTrueMonitor == TrueFullScreenMonitorHandle
            && nextGameLikeMonitor == GameLikeFullScreenMonitorHandle)
        {
            return;
        }

        IsFullScreenInFront = fullScreen;
        IsTrueFullScreenInFront = trueFullScreen;
        IsGameLikeFullScreenInFront = gameLikeFullScreen;
        FullScreenMonitorHandle = nextFullScreenMonitor;
        TrueFullScreenMonitorHandle = nextTrueMonitor;
        GameLikeFullScreenMonitorHandle = nextGameLikeMonitor;
        Changed?.Invoke();
    }

    internal static bool IsGameLikeFullScreen(nint window, nint targetMonitor = 0) =>
        IsFullScreen(window, targetMonitor, excludeMaximized: true)
        && IsGameLikeFullScreenChrome(window);

    /// <summary>
    /// Returns true when <paramref name="foregroundWindow"/> itself is a true full-screen
    /// surface on <paramref name="targetMonitor"/>, OR when it is an owned dialog/popup or
    /// same-process transient window (such as a browser F11 download bubble or Save As dialog)
    /// sitting directly in front of a visible true full-screen window on that monitor.
    /// Switching to an unrelated normal app still returns false.
    /// </summary>
    internal static bool IsTrueFullScreenContext(nint foregroundWindow, nint targetMonitor = 0)
    {
        if (foregroundWindow == 0 || DockNativeMethods.IsIconic(foregroundWindow))
        {
            return false;
        }

        if (IsGameLikeFullScreen(foregroundWindow, targetMonitor))
        {
            return true;
        }

        DockNativeMethods.GetWindowThreadProcessId(foregroundWindow, out var foregroundPid);
        if (foregroundPid == 0 || foregroundPid == (uint)Environment.ProcessId)
        {
            return false;
        }

        var effectiveMonitor = targetMonitor != 0
            ? targetMonitor
            : DockNativeMethods.MonitorFromWindow(foregroundWindow, DockNativeMethods.MonitorDefaultToNearest);

        // 1. Check owner chain (e.g. Save As #32770 or modal popup owned by a full-screen window).
        var visited = new HashSet<nint> { foregroundWindow };
        var owner = NativeMethods.GetWindow(foregroundWindow, NativeMethods.GwOwner);
        for (var hop = 0; hop < 8 && owner != 0 && visited.Add(owner); hop++)
        {
            if (DockNativeMethods.IsWindowVisible(owner)
                && IsGameLikeFullScreen(owner, effectiveMonitor))
            {
                return true;
            }
            owner = NativeMethods.GetWindow(owner, NativeMethods.GwOwner);
        }

        var rootOwner = DockNativeMethods.GetAncestor(foregroundWindow, DockNativeMethods.GaRootOwner);
        if (rootOwner != 0
            && rootOwner != foregroundWindow
            && DockNativeMethods.IsWindowVisible(rootOwner)
            && IsGameLikeFullScreen(rootOwner, effectiveMonitor))
        {
            return true;
        }

        // 2. Check for a visible same-process full-screen window immediately behind a transient
        // popup/bubble (such as Chromium's unowned download bubble in F11 mode). Only transient
        // foreground windows may inherit full-screen status by PID so switching from an F11 browser
        // window to a normal window of the same browser still restores normal Dock/TopBar behavior.
        var fgClass = new StringBuilder(64);
        DockNativeMethods.GetClassNameW(foregroundWindow, fgClass, fgClass.Capacity);
        var fgClassName = fgClass.ToString();
        if (ShellSurfaceWindows.IsShellSurface(fgClassName))
        {
            return false;
        }

        if (!IsTransientWindow(foregroundWindow, fgClassName))
        {
            return false;
        }

        var explorerPid = ReadShellExplorerProcessId();
        if (explorerPid != 0 && foregroundPid == explorerPid)
        {
            return false;
        }

        var cursor = NativeMethods.GetWindow(foregroundWindow, NativeMethods.GwHwndNext);
        var steps = 0;
        var visibleChecked = 0;
        var candidateClass = new StringBuilder(64);
        while (cursor != 0 && steps++ < 256 && visibleChecked < 48)
        {
            if (!DockNativeMethods.IsWindowVisible(cursor)
                || DockNativeMethods.IsIconic(cursor)
                || NativeMethods.IsWindowCloaked(cursor))
            {
                cursor = NativeMethods.GetWindow(cursor, NativeMethods.GwHwndNext);
                continue;
            }

            DockNativeMethods.GetWindowThreadProcessId(cursor, out var candidatePid);
            if (candidatePid == 0 || candidatePid == (uint)Environment.ProcessId)
            {
                cursor = NativeMethods.GetWindow(cursor, NativeMethods.GwHwndNext);
                continue;
            }

            var exStyle = NativeMethods.GetExStyleOf(cursor);
            if ((exStyle & (NativeMethods.WsExToolWindow | NativeMethods.WsExTransparent)) != 0)
            {
                cursor = NativeMethods.GetWindow(cursor, NativeMethods.GwHwndNext);
                continue;
            }

            candidateClass.Clear();
            DockNativeMethods.GetClassNameW(cursor, candidateClass, candidateClass.Capacity);
            var candidateClassName = candidateClass.ToString();
            if (ShellSurfaceWindows.IsShellSurface(candidateClassName))
            {
                cursor = NativeMethods.GetWindow(cursor, NativeMethods.GwHwndNext);
                continue;
            }

            if (effectiveMonitor != 0
                && DockNativeMethods.MonitorFromWindow(cursor, DockNativeMethods.MonitorDefaultToNearest) != effectiveMonitor)
            {
                cursor = NativeMethods.GetWindow(cursor, NativeMethods.GwHwndNext);
                continue;
            }

            visibleChecked++;
            if (candidatePid == foregroundPid)
            {
                if (IsGameLikeFullScreen(cursor, effectiveMonitor))
                {
                    return true;
                }
                if (!IsTransientWindow(cursor, candidateClassName))
                {
                    // Another normal window of the same process sits between this popup and any
                    // older full-screen window beneath it; treat it as the boundary so a bubble
                    // over a normal browser window does not inherit an older F11 window's state.
                    break;
                }
            }
            else if (explorerPid == 0 || candidatePid != explorerPid)
            {
                // An unrelated normal application window sits between foregroundWindow and any
                // lower window, so foregroundWindow is not a transient overlay on a full-screen app.
                break;
            }

            cursor = NativeMethods.GetWindow(cursor, NativeMethods.GwHwndNext);
        }

        return false;
    }

    private static bool IsTransientWindow(nint window, string className)
    {
        var style = (long)NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlStyle);
        var exStyle = NativeMethods.GetExStyleOf(window);
        return DockVacateRules.IsTransientFullScreenChildWindow(
            isZoomed: DockNativeMethods.IsZoomed(window),
            isPopup: (style & NativeMethods.WsPopup) != 0,
            isDialogClass: string.Equals(className, "#32770", StringComparison.Ordinal),
            isToolWindow: (exStyle & NativeMethods.WsExToolWindow) != 0,
            hasStandardResizableFrame: (style & NativeMethods.WsCaption) == NativeMethods.WsCaption
                && (style & NativeMethods.WsThickFrame) != 0,
            isAppWindow: (exStyle & NativeMethods.WsExAppWindow) != 0);
    }

    private static uint ReadShellExplorerProcessId()
    {
        var tray = TaskbarNativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (tray == 0)
        {
            return 0;
        }
        DockNativeMethods.GetWindowThreadProcessId(tray, out var pid);
        return pid;
    }

    private static bool IsGameLikeFullScreenChrome(nint window)
    {
        var style = (long)NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlStyle);
        var exStyle = NativeMethods.GetExStyleOf(window);
        var className = new StringBuilder(64);
        DockNativeMethods.GetClassNameW(window, className, className.Capacity);
        var isApplicationFrame = string.Equals(
            className.ToString(),
            "ApplicationFrameWindow",
            StringComparison.Ordinal);
        var hasCaptionAndThickFrame = (style & NativeMethods.WsCaption) == NativeMethods.WsCaption
            && (style & NativeMethods.WsThickFrame) != 0;
        var isToolOrOverlay = (exStyle & (NativeMethods.WsExToolWindow | NativeMethods.WsExTransparent)) != 0;
        var isTopmost = (exStyle & NativeMethods.WsExTopmost) != 0;
        return TaskbarCornerRules.ShouldSuspendForFullScreen(
            coversMonitor: true,
            foregroundIsZoomed: false,
            hasCaptionAndThickFrame: hasCaptionAndThickFrame,
            isApplicationFrameWindow: isApplicationFrame,
            isToolOrOverlayWindow: isToolOrOverlay,
            isTopmost: isTopmost);
    }

    internal static bool IsFullScreen(nint window, nint targetMonitor = 0, bool excludeMaximized = false) =>
        TryGetFullScreenMonitor(window, out var monitor, targetMonitor, excludeMaximized)
        && (targetMonitor == 0 || targetMonitor == monitor);

    private static bool TryGetFullScreenMonitor(
        nint window,
        out nint monitor,
        nint targetMonitor = 0,
        bool excludeMaximized = false)
    {
        monitor = 0;
        if (window == 0 || DockNativeMethods.IsIconic(window)
            || (excludeMaximized && DockNativeMethods.IsZoomed(window)))
        {
            return false;
        }

        DockNativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == (uint)Environment.ProcessId)
        {
            return false;
        }

        var className = new StringBuilder(64);
        DockNativeMethods.GetClassNameW(window, className, className.Capacity);
        if (ShellSurfaceWindows.IsShellSurface(className.ToString()))
        {
            return false;
        }

        if (DockNativeMethods.DwmGetWindowAttribute(
                window,
                DockNativeMethods.DwmwaExtendedFrameBounds,
                out DockNativeMethods.NativeRect bounds,
                Marshal.SizeOf<DockNativeMethods.NativeRect>()) != 0
            && !DockNativeMethods.GetWindowRect(window, out bounds))
        {
            return false;
        }

        monitor = DockNativeMethods.MonitorFromWindow(window, DockNativeMethods.MonitorDefaultToNearest);
        var info = new DockNativeMethods.MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<DockNativeMethods.MonitorInfo>(),
            DeviceName = string.Empty
        };
        return monitor != 0
            && (targetMonitor == 0 || targetMonitor == monitor)
            && DockNativeMethods.GetMonitorInfoW(monitor, ref info)
            && DockVacateRules.CoversMonitor(
                new PixelRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
                new PixelRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom));
    }
}
