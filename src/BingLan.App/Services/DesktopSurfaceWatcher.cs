using System.Text;
using System.Windows.Threading;
using BingLan.App.Interop;
using BingLan.Core.Desktop;

namespace BingLan.App.Services;

/// <summary>
/// Watches for the shell's "show desktop": Explorer minimises the ordinary windows
/// and raises the desktop surface (<c>WorkerW</c> or <c>Progman</c>) above the
/// non-topmost band — and on Windows 11 into the topmost band — which would cover
/// desktop cards and bars behind the wallpaper. While that state lasts the surfaces
/// are kept in the topmost band above the desktop surface, and cards return below
/// every ordinary window once ordinary windows come back.
/// </summary>
internal sealed class DesktopSurfaceWatcher : IDisposable
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(150);
    private const long MaxBurstDebounceMs = 200;
    private const int ObjidWindow = 0;

    private readonly Func<IReadOnlyList<nint>> _cardHandles;
    private readonly NativeMethods.WinEventProc _hookCallback;
    private readonly List<nint> _hooks = [];
    private readonly DispatcherTimer _evaluateTimer;
    private nint _shellShowHook;
    private uint _shellProcessId;
    private long _burstStartedTick;
    private bool _started;
    private bool _cardsRaised;
    private bool _disposed;

    internal DesktopSurfaceWatcher(Func<IReadOnlyList<nint>> cardHandles)
    {
        _cardHandles = cardHandles;
        // Keep the delegate alive for as long as the hook exists.
        _hookCallback = OnWinEvent;
        _evaluateTimer = new DispatcherTimer(
            SettleDelay,
            DispatcherPriority.Background,
            OnEvaluateTick,
            Dispatcher.CurrentDispatcher);
        _evaluateTimer.Stop();
    }

    /// <summary>
    /// Raised when <see cref="IsDesktopShown"/> changed, or when the desktop is shown
    /// and Explorer raised the desktop surface above a monitored window.
    /// </summary>
    internal event Action? Changed;

    internal bool IsDesktopShown { get; private set; }

    internal void Start()
    {
        if (_started || _disposed)
        {
            return;
        }
        _started = true;
        Hook(DockNativeMethods.EventSystemForeground, DockNativeMethods.EventSystemForeground);
        Hook(DockNativeMethods.EventSystemMinimizeStart, DockNativeMethods.EventSystemMinimizeEnd);
        Hook(DockNativeMethods.EventSystemDesktopSwitch, DockNativeMethods.EventSystemDesktopSwitch);
        Hook(DockNativeMethods.EventObjectCloaked, DockNativeMethods.EventObjectUncloaked);
        _ = FindDesktopSurfaceWindow(out var shellProcess);
        RefreshShellShowHook(shellProcess);
        ScheduleCheck();
    }

    /// <summary>Schedules a settled re-evaluation after a shell desktop action.</summary>
    internal void ScheduleCheck()
    {
        if (_disposed)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (!_evaluateTimer.IsEnabled)
        {
            _burstStartedTick = now;
            _evaluateTimer.Start();
            return;
        }

        // Multi-window minimise animations arrive over ~150-200 ms; extend the settle
        // window up to a bounded cap so evaluation sees the final z-order.
        if (now - _burstStartedTick < MaxBurstDebounceMs)
        {
            _evaluateTimer.Stop();
            _evaluateTimer.Start();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _evaluateTimer.Stop();
        if (_shellShowHook != 0)
        {
            NativeMethods.UnhookWinEvent(_shellShowHook);
            _shellShowHook = 0;
        }
        foreach (var hook in _hooks)
        {
            NativeMethods.UnhookWinEvent(hook);
        }
        _hooks.Clear();
    }

    /// <summary>
    /// Finds the live desktop surface window: a raised topmost <c>WorkerW</c>/<c>Progman</c>
    /// of Explorer during "show desktop", otherwise the window currently hosting
    /// <c>SHELLDLL_DefView</c>, falling back to <c>Progman</c>.
    /// </summary>
    internal static nint FindDesktopSurfaceWindow(out uint shellProcessId)
    {
        shellProcessId = 0;
        var progman = NativeMethods.FindWindowW("Progman", null);
        if (progman != 0)
        {
            NativeMethods.GetWindowThreadProcessId(progman, out shellProcessId);
        }
        else if (DockNativeMethods.GetShellWindow() is var shell && shell != 0)
        {
            NativeMethods.GetWindowThreadProcessId(shell, out shellProcessId);
        }

        nint topmostSurface = 0;
        nint defViewHost = 0;
        if (progman != 0)
        {
            if (IsTopmostDesktopCandidate(progman, "Progman", shellProcessId))
            {
                topmostSurface = progman;
            }
            if (NativeMethods.FindWindowExW(progman, 0, "SHELLDLL_DefView", null) != 0)
            {
                defViewHost = progman;
            }
        }

        nint worker = 0;
        while ((worker = NativeMethods.FindWindowExW(0, worker, "WorkerW", null)) != 0)
        {
            NativeMethods.GetWindowThreadProcessId(worker, out var workerProcess);
            if (topmostSurface == 0
                && shellProcessId != 0
                && workerProcess == shellProcessId
                && IsTopmostDesktopCandidate(worker, "WorkerW", shellProcessId))
            {
                topmostSurface = worker;
            }

            if (defViewHost == 0
                && NativeMethods.FindWindowExW(worker, 0, "SHELLDLL_DefView", null) != 0)
            {
                defViewHost = worker;
                if (shellProcessId == 0)
                {
                    shellProcessId = workerProcess;
                }
            }

            if (topmostSurface != 0 && defViewHost != 0)
            {
                break;
            }
        }

        return topmostSurface != 0
            ? topmostSurface
            : defViewHost != 0
                ? defViewHost
                : progman;
    }

    private static bool IsTopmostDesktopCandidate(nint window, string className, uint shellProcessId)
    {
        if (shellProcessId == 0)
        {
            return false;
        }
        var isVisible = NativeMethods.IsWindowVisible(window);
        if (!isVisible)
        {
            return false;
        }
        var isTopmost = (NativeMethods.GetExStyleOf(window) & NativeMethods.WsExTopmost) != 0;
        if (!isTopmost)
        {
            return false;
        }
        var isCloaked = NativeMethods.IsWindowCloaked(window);
        if (!DockNativeMethods.GetWindowRect(window, out var rect))
        {
            return false;
        }
        return DesktopSurfaceRules.IsTopmostShellDesktopSurface(
            shellProcess: true,
            className,
            isVisible,
            isCloaked,
            isTopmost,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top);
    }

    private void Hook(uint eventMin, uint eventMax)
    {
        var hook = NativeMethods.SetWinEventHook(
            eventMin,
            eventMax,
            nint.Zero,
            _hookCallback,
            0,
            0,
            DockNativeMethods.WineventOutOfContext | DockNativeMethods.WineventSkipOwnProcess);
        if (hook != 0)
        {
            _hooks.Add(hook);
        }
    }

    private void RefreshShellShowHook(uint shellProcessId)
    {
        if (!_started || _disposed || shellProcessId == 0)
        {
            return;
        }
        if (shellProcessId == _shellProcessId && _shellShowHook != 0)
        {
            return;
        }
        if (_shellShowHook != 0)
        {
            NativeMethods.UnhookWinEvent(_shellShowHook);
            _shellShowHook = 0;
        }
        _shellProcessId = shellProcessId;
        _shellShowHook = NativeMethods.SetWinEventHook(
            DockNativeMethods.EventObjectShow,
            DockNativeMethods.EventObjectHide,
            nint.Zero,
            _hookCallback,
            shellProcessId,
            0,
            DockNativeMethods.WineventOutOfContext);
    }

    private void OnEvaluateTick(object? sender, EventArgs e)
    {
        _evaluateTimer.Stop();
        if (!_disposed)
        {
            Evaluate();
        }
    }

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint window,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        UiDiagnostics.Hit(UiActivity.DesktopEvent);
        if (_disposed || idObject != ObjidWindow || idChild != 0)
        {
            return;
        }
        if (eventType is DockNativeMethods.EventObjectShow
                or DockNativeMethods.EventObjectHide
                or DockNativeMethods.EventObjectCloaked
                or DockNativeMethods.EventObjectUncloaked)
        {
            if (window == 0 || NativeMethods.IsChildWindow(window))
            {
                return;
            }
            if (eventType is DockNativeMethods.EventObjectShow or DockNativeMethods.EventObjectHide
                && ClassNameOf(window) is not ("WorkerW" or "Progman" or "CabinetWClass" or "ExploreWClass"))
            {
                return;
            }
        }
        ScheduleCheck();
    }

    /// <summary>
    /// Reads whether the desktop surface covers the cards: the live desktop surface
    /// (<c>WorkerW</c> or <c>Progman</c>) has no visible, un-minimised ordinary window
    /// above it. Tool windows, non-activating widgets, 1px helper windows, topmost
    /// windows, DWM-cloaked hosts and Explorer's shell windows do not count. Also
    /// checks whether every monitored BingLan surface currently sits above the desktop
    /// surface so a <c>WorkerW</c> raised over already-lifted windows triggers a re-lift.
    /// </summary>
    private void Evaluate()
    {
        using var _ = UiDiagnostics.Measure("桌面状态评估");
        var desktopSurface = FindDesktopSurfaceWindow(out var shellProcess);
        RefreshShellShowHook(shellProcess);
        var found = desktopSurface != 0;
        var blockers = 0;
        var surfaces = _cardHandles()
            .Where(handle => handle != 0 && DockNativeMethods.IsWindow(handle))
            .ToHashSet();
        var surfacesAbove = 0;
        if (found)
        {
            NativeMethods.EnumWindows((window, _) =>
            {
                if (window == desktopSurface)
                {
                    return false;
                }
                if (!NativeMethods.IsWindowVisible(window) || NativeMethods.IsIconic(window))
                {
                    return true;
                }
                if (surfaces.Contains(window))
                {
                    surfacesAbove++;
                    return true;
                }
                if (NativeMethods.IsWindowCloaked(window))
                {
                    return true;
                }
                var exStyle = NativeMethods.GetExStyleOf(window);
                if ((exStyle & NativeMethods.WsExTopmost) != 0)
                {
                    return true;
                }

                var isAppWindow = (exStyle & NativeMethods.WsExAppWindow) != 0;
                var isToolWindow = (exStyle & NativeMethods.WsExToolWindow) != 0;
                var isNoActivate = (exStyle & NativeMethods.WsExNoActivate) != 0;
                var isLayeredClickThrough = (exStyle & NativeMethods.WsExLayered) != 0
                    && (exStyle & NativeMethods.WsExTransparent) != 0;
                var isDisabled = !DockNativeMethods.IsWindowEnabled(window);
                var width = 100;
                var height = 100;
                var hasTitle = true;
                if (!isAppWindow && !isToolWindow && !isNoActivate)
                {
                    if (DockNativeMethods.GetWindowRect(window, out var rect))
                    {
                        width = rect.Right - rect.Left;
                        height = rect.Bottom - rect.Top;
                    }
                    else
                    {
                        width = 0;
                        height = 0;
                    }

                    if (isDisabled)
                    {
                        hasTitle = DockNativeMethods.GetWindowTextLengthW(window) > 0;
                    }
                }

                NativeMethods.GetWindowThreadProcessId(window, out var process);
                if (DesktopSurfaceRules.CountsAsOrdinaryBlocker(
                        shellProcess != 0 && process == shellProcess,
                        ClassNameOf(window),
                        isAppWindow,
                        isToolWindow,
                        isNoActivate,
                        isCloaked: false,
                        isMinimized: false,
                        isLayeredClickThrough,
                        isDisabled,
                        hasTitle,
                        width,
                        height))
                {
                    blockers++;
                }
                // A few are enough to prove ordinary windows are still around.
                return blockers < 3;
            }, 0);
        }
        var shown = DesktopSurfaceRules.IsDesktopShown(found, blockers);
        var anySurfaceCovered = shown && surfacesAbove < surfaces.Count;
        var action = DesktopSurfaceRules.ActionFor(_cardsRaised, shown, anySurfaceCovered);
        IsDesktopShown = shown;
        _cardsRaised = shown;
        if (action == DesktopSurfaceAction.None)
        {
            return;
        }
        Changed?.Invoke();
    }

    private static string ClassNameOf(nint window)
    {
        var className = new StringBuilder(64);
        return DockNativeMethods.GetClassNameW(window, className, className.Capacity) > 0
            ? className.ToString()
            : string.Empty;
    }
}
