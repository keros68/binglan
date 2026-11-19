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
    private static readonly uint CurrentProcessId = (uint)Environment.ProcessId;
    private static readonly TimeSpan FastCoalesceDelay = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan TrailingSettleDelay = TimeSpan.FromMilliseconds(500);
    private const long MinImmediateIntervalMs = 15;
    private const long MaxBurstDebounceMs = 200;
    private const long PreRaiseHoldMs = 450;
    private const int ObjidWindow = 0;

    private readonly Func<IReadOnlyList<nint>> _cardHandles;
    private readonly NativeMethods.WinEventProc _hookCallback;
    private readonly List<nint> _hooks = [];
    private readonly DispatcherTimer _fastTimer;
    private readonly DispatcherTimer _evaluateTimer;
    private readonly DispatcherTimer _trailingTimer;
    private nint _shellShowHook;
    private uint _shellProcessId;
    private long _lastEvaluateTick;
    private long _burstStartedTick;
    private long _preRaiseUntilTick;
    private bool _started;
    private bool _cardsRaised;
    private bool _raisedForTopmostSurface;
    private bool _disposed;

    internal DesktopSurfaceWatcher(Func<IReadOnlyList<nint>> cardHandles)
    {
        _cardHandles = cardHandles;
        // Keep the delegate alive for as long as the hook exists.
        _hookCallback = OnWinEvent;
        var dispatcher = Dispatcher.CurrentDispatcher;
        _fastTimer = new DispatcherTimer(
            FastCoalesceDelay,
            DispatcherPriority.Render,
            OnFastTick,
            dispatcher);
        _fastTimer.Stop();
        _evaluateTimer = new DispatcherTimer(
            SettleDelay,
            DispatcherPriority.Input,
            OnEvaluateTick,
            dispatcher);
        _evaluateTimer.Stop();
        _trailingTimer = new DispatcherTimer(
            TrailingSettleDelay,
            DispatcherPriority.Background,
            OnTrailingTick,
            dispatcher);
        _trailingTimer.Stop();
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
        Hook(DockNativeMethods.EventObjectShow, DockNativeMethods.EventObjectShow);
        Hook(DockNativeMethods.EventObjectCloaked, DockNativeMethods.EventObjectUncloaked);
        _ = FindDesktopSurfaceWindow(out var shellProcess);
        RefreshShellShowHook(shellProcess);
        ScheduleCheck();
    }

    /// <summary>
    /// Pre-lifts monitored desktop surfaces right before the app itself asks Explorer
    /// to toggle "show desktop", so cards are already above <c>Progman</c>/<c>WorkerW</c>
    /// on the very first frame it rises instead of blinking behind the wallpaper.
    /// </summary>
    internal void PrepareForToggleDesktop()
    {
        if (_disposed)
        {
            return;
        }
        if (IsDesktopShown)
        {
            _preRaiseUntilTick = 0;
            ScheduleToggleDesktopCheck();
            return;
        }

        _preRaiseUntilTick = Environment.TickCount64 + PreRaiseHoldMs;
        IsDesktopShown = true;
        _cardsRaised = true;
        _raisedForTopmostSurface = false;
        Changed?.Invoke();
        ScheduleToggleDesktopCheck();
    }

    internal void CancelToggleDesktopPreparation()
    {
        if (_disposed)
        {
            return;
        }
        _preRaiseUntilTick = 0;
        _trailingTimer.Stop();
        EvaluateImmediate();
    }

    internal void ScheduleToggleDesktopCheck()
    {
        if (_disposed)
        {
            return;
        }
        ScheduleCheck();
        ArmTrailingSettleCheck();
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

    private void ArmTrailingSettleCheck()
    {
        // Slow multi-window minimise bursts (5+ heavy windows) can finish becoming iconic
        // ~250-400 ms after EVENT_SYSTEM_MINIMIZESTART with no completion WinEvent; the
        // trailing check also guarantees reconciliation once PreRaiseHoldMs elapses.
        _trailingTimer.Stop();
        _trailingTimer.Start();
    }

    public void Dispose()
    {
        _disposed = true;
        _fastTimer.Stop();
        _evaluateTimer.Stop();
        _trailingTimer.Stop();
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
        FindDesktopSurfaces(out var topmostSurface, out var baseSurface, out _, out shellProcessId);
        return topmostSurface != 0 ? topmostSurface : baseSurface;
    }

    private static void FindDesktopSurfaces(
        out nint topmostSurface,
        out nint baseSurface,
        out string baseSurfaceClass,
        out uint shellProcessId)
    {
        topmostSurface = 0;
        baseSurface = 0;
        baseSurfaceClass = string.Empty;
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

        nint defViewHost = 0;
        var defViewClass = string.Empty;
        if (progman != 0)
        {
            if (IsTopmostDesktopCandidate(progman, "Progman", shellProcessId))
            {
                topmostSurface = progman;
            }
            if (NativeMethods.FindWindowExW(progman, 0, "SHELLDLL_DefView", null) != 0)
            {
                defViewHost = progman;
                defViewClass = "Progman";
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
                defViewClass = "WorkerW";
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

        if (defViewHost != 0)
        {
            baseSurface = defViewHost;
            baseSurfaceClass = defViewClass;
        }
        else if (progman != 0)
        {
            baseSurface = progman;
            baseSurfaceClass = "Progman";
        }
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
        var exStyle = NativeMethods.GetExStyleOf(window);
        var isTopmost = (exStyle & NativeMethods.WsExTopmost) != 0;
        if (!isTopmost)
        {
            return false;
        }
        var isCloaked = NativeMethods.IsWindowCloaked(window);
        if (!DockNativeMethods.GetWindowRect(window, out var rect))
        {
            return false;
        }
        var isClickThrough = (exStyle & NativeMethods.WsExTransparent) != 0;
        var isTransparentLayered = (exStyle & NativeMethods.WsExLayered) != 0
            && NativeMethods.GetLayeredWindowAttributes(window, out _, out var alpha, out var flags)
            && (flags & NativeMethods.LwaAlpha) != 0
            && alpha == 0;
        return DesktopSurfaceRules.IsTopmostShellDesktopSurface(
            shellProcess: true,
            className,
            isVisible,
            isCloaked,
            isTopmost,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top,
            isClickThrough,
            isTransparentLayered);
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
            DockNativeMethods.EventObjectReorder,
            nint.Zero,
            _hookCallback,
            shellProcessId,
            0,
            DockNativeMethods.WineventOutOfContext);
    }

    private void EvaluateImmediate()
    {
        if (_disposed)
        {
            return;
        }
        var now = Environment.TickCount64;
        if (now - _lastEvaluateTick >= MinImmediateIntervalMs)
        {
            _fastTimer.Stop();
            Evaluate();
            return;
        }
        if (!_fastTimer.IsEnabled)
        {
            _fastTimer.Start();
        }
    }

    private void OnFastTick(object? sender, EventArgs e)
    {
        _fastTimer.Stop();
        if (!_disposed)
        {
            Evaluate();
        }
    }

    private void OnEvaluateTick(object? sender, EventArgs e)
    {
        _evaluateTimer.Stop();
        if (!_disposed)
        {
            Evaluate();
        }
    }

    private void OnTrailingTick(object? sender, EventArgs e)
    {
        _trailingTimer.Stop();
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
        if (_disposed || idChild != 0)
        {
            return;
        }
        if (eventType == DockNativeMethods.EventObjectReorder)
        {
            // Top-level z-order changes report the desktop root window (commonly with
            // OBJID_CLIENT = -4). Ignore child-control reorders inside Explorer windows.
            if (window != 0 && window != DockNativeMethods.GetDesktopWindow())
            {
                return;
            }
            EvaluateImmediate();
            if (_cardsRaised)
            {
                ScheduleCheck();
            }
            return;
        }
        if (idObject != ObjidWindow)
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
            if (eventType == DockNativeMethods.EventObjectShow)
            {
                var className = ClassNameOf(window);
                if (className is "WorkerW" or "Progman")
                {
                    EvaluateImmediate();
                    ScheduleCheck();
                    return;
                }
                if (_cardsRaised)
                {
                    EvaluateAndFollowUpWhileRaised();
                    return;
                }
                if (className is not ("CabinetWClass" or "ExploreWClass" or "#32770" or "OperationStatusWindow"))
                {
                    return;
                }
            }
            else if (eventType == DockNativeMethods.EventObjectHide)
            {
                var className = ClassNameOf(window);
                if (className is "WorkerW" or "Progman")
                {
                    EvaluateImmediate();
                    if (_cardsRaised)
                    {
                        ScheduleCheck();
                    }
                    return;
                }
                if (className is not ("CabinetWClass" or "ExploreWClass" or "#32770" or "OperationStatusWindow"))
                {
                    return;
                }
            }
            else if (eventType == DockNativeMethods.EventObjectUncloaked && _cardsRaised)
            {
                EvaluateAndFollowUpWhileRaised();
                return;
            }
        }
        if (eventType == DockNativeMethods.EventSystemForeground && window != 0)
        {
            if (_cardsRaised)
            {
                EvaluateAndFollowUpWhileRaised();
                return;
            }
            if (ClassNameOf(window) is "Progman" or "WorkerW")
            {
                EvaluateImmediate();
                ScheduleCheck();
                return;
            }
        }
        if (eventType == DockNativeMethods.EventSystemMinimizeEnd && _cardsRaised)
        {
            EvaluateAndFollowUpWhileRaised();
            return;
        }
        ScheduleCheck();
        if (eventType == DockNativeMethods.EventSystemMinimizeStart)
        {
            ArmTrailingSettleCheck();
        }
    }

    /// <summary>
    /// Evaluates immediately so raised cards drop without lag when an ordinary window
    /// is already visible, and schedules settled follow-up checks if cards remain raised
    /// (for apps that fire <c>EVENT_SYSTEM_FOREGROUND</c> before becoming visible, sized
    /// or uncloaked, or before Explorer finishes lowering <c>WorkerW</c>).
    /// </summary>
    private void EvaluateAndFollowUpWhileRaised()
    {
        EvaluateImmediate();
        if (_cardsRaised)
        {
            ScheduleCheck();
            ArmTrailingSettleCheck();
        }
    }

    /// <summary>
    /// Reads whether the desktop surface covers the cards: Explorer has raised the live
    /// desktop surface (<c>WorkerW</c> or <c>Progman</c>) and no visible, un-minimised
    /// ordinary window is left above the base desktop surface. Even when a topmost
    /// <c>WorkerW</c> exists, enumeration continues down to the non-topmost base surface
    /// so newly opened non-fullscreen windows are always counted.
    /// </summary>
    private void Evaluate()
    {
        using var _ = UiDiagnostics.Measure("桌面状态评估");
        _lastEvaluateTick = Environment.TickCount64;
        FindDesktopSurfaces(out var topmostSurface, out var baseSurface, out var baseSurfaceClass, out var shellProcess);
        RefreshShellShowHook(shellProcess);
        var effectiveSurface = topmostSurface != 0 ? topmostSurface : baseSurface;
        var found = effectiveSurface != 0;
        var blockers = 0;
        var surfaces = _cardHandles()
            .Where(handle => handle != 0 && DockNativeMethods.IsWindow(handle))
            .ToHashSet();
        var visibleSurfaces = surfaces
            .Where(handle => NativeMethods.IsWindowVisible(handle) && !NativeMethods.IsIconic(handle))
            .ToHashSet();
        var surfacesAbove = 0;
        var visibleSurfacesAbove = 0;
        if (found)
        {
            var passedEffectiveSurface = false;
            NativeMethods.EnumWindows((window, _) =>
            {
                if (window == effectiveSurface)
                {
                    passedEffectiveSurface = true;
                    if (baseSurface == 0 || window == baseSurface)
                    {
                        return false;
                    }
                    return true;
                }
                if (baseSurface != 0 && window == baseSurface)
                {
                    return false;
                }
                if (!NativeMethods.IsWindowVisible(window) || NativeMethods.IsIconic(window))
                {
                    return true;
                }
                if (surfaces.Contains(window))
                {
                    if (!passedEffectiveSurface)
                    {
                        surfacesAbove++;
                        if (visibleSurfaces.Contains(window))
                        {
                            visibleSurfacesAbove++;
                        }
                    }
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
                return !(passedEffectiveSurface && blockers >= 3);
            }, 0);

            if (blockers == 0 && IsForegroundWindowBlocking(surfaces, shellProcess))
            {
                blockers++;
            }
        }
        var surfaceRaised = DesktopSurfaceRules.IsDesktopSurfaceRaised(
            desktopSurfaceIsTopmost: topmostSurface != 0,
            cardsRaised: _cardsRaised,
            raisedForTopmostSurface: _raisedForTopmostSurface,
            baseSurfaceClass,
            visibleSurfaces.Count,
            visibleSurfacesAbove);
        var shown = DesktopSurfaceRules.IsDesktopShown(found, blockers, surfaceRaised);
        if (shown)
        {
            _preRaiseUntilTick = 0;
            _trailingTimer.Stop();
        }
        var holdPreRaise = !shown
            && _preRaiseUntilTick != 0
            && _lastEvaluateTick < _preRaiseUntilTick;
        var anySurfaceCovered = shown && surfacesAbove < surfaces.Count;
        var action = DesktopSurfaceRules.ActionFor(_cardsRaised, shown, anySurfaceCovered, holdPreRaise);
        if (!holdPreRaise)
        {
            _preRaiseUntilTick = 0;
            IsDesktopShown = shown;
            _cardsRaised = shown;
            _raisedForTopmostSurface = shown && (_raisedForTopmostSurface || topmostSurface != 0);
        }
        if (action == DesktopSurfaceAction.None)
        {
            return;
        }
        Changed?.Invoke();
    }

    private static bool IsForegroundWindowBlocking(HashSet<nint> surfaces, uint shellProcess)
    {
        var foreground = DockNativeMethods.GetForegroundWindow();
        if (foreground == 0 || surfaces.Contains(foreground))
        {
            return false;
        }
        NativeMethods.GetWindowThreadProcessId(foreground, out var process);
        if (process == CurrentProcessId)
        {
            return false;
        }
        var isVisible = NativeMethods.IsWindowVisible(foreground);
        var isMinimized = NativeMethods.IsIconic(foreground);
        if (!isVisible || isMinimized)
        {
            return false;
        }
        var isCloaked = NativeMethods.IsWindowCloaked(foreground);
        var exStyle = NativeMethods.GetExStyleOf(foreground);
        var isTopmost = (exStyle & NativeMethods.WsExTopmost) != 0;
        var isLayeredClickThrough = (exStyle & NativeMethods.WsExLayered) != 0
            && (exStyle & NativeMethods.WsExTransparent) != 0;
        var width = 0;
        var height = 0;
        if (DockNativeMethods.GetWindowRect(foreground, out var rect))
        {
            width = rect.Right - rect.Left;
            height = rect.Bottom - rect.Top;
        }
        return DesktopSurfaceRules.CountsAsForegroundBlocker(
            hasWindow: true,
            isOwnProcess: false,
            isMonitoredDesktopSurface: false,
            shellProcess: shellProcess != 0 && process == shellProcess,
            ClassNameOf(foreground),
            isVisible,
            isMinimized,
            isCloaked,
            isTopmost,
            isLayeredClickThrough,
            width,
            height);
    }

    private static string ClassNameOf(nint window)
    {
        var className = new StringBuilder(64);
        return DockNativeMethods.GetClassNameW(window, className, className.Capacity) > 0
            ? className.ToString()
            : string.Empty;
    }
}
