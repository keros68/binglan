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
        var fullScreen = IsFullScreen(foreground);
        var trueFullScreen = fullScreen && !DockNativeMethods.IsZoomed(foreground);
        var gameLikeFullScreen = trueFullScreen && IsGameLikeFullScreenChrome(foreground);
        if (fullScreen == IsFullScreenInFront
            && trueFullScreen == IsTrueFullScreenInFront
            && gameLikeFullScreen == IsGameLikeFullScreenInFront)
        {
            return;
        }

        IsFullScreenInFront = fullScreen;
        IsTrueFullScreenInFront = trueFullScreen;
        IsGameLikeFullScreenInFront = gameLikeFullScreen;
        Changed?.Invoke();
    }

    internal static bool IsGameLikeFullScreen(nint window, nint targetMonitor = 0) =>
        IsFullScreen(window, targetMonitor, excludeMaximized: true)
        && IsGameLikeFullScreenChrome(window);

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

    internal static bool IsFullScreen(nint window, nint targetMonitor = 0, bool excludeMaximized = false)
    {
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

        var monitor = DockNativeMethods.MonitorFromWindow(window, DockNativeMethods.MonitorDefaultToNearest);
        var info = new DockNativeMethods.MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<DockNativeMethods.MonitorInfo>(),
            DeviceName = string.Empty
        };
        return monitor != 0
            && (targetMonitor == 0 || targetMonitor == monitor)
            && DockNativeMethods.GetMonitorInfoW(monitor, ref info)
            && bounds.Left <= info.Monitor.Left
            && bounds.Top <= info.Monitor.Top
            && bounds.Right >= info.Monitor.Right
            && bounds.Bottom >= info.Monitor.Bottom;
    }
}
