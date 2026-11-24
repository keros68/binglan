using System.Text;
using System.Windows.Threading;
using BingLan.App.Interop;
using BingLan.Core.Desktop;

namespace BingLan.App.Services;

/// <summary>
/// Tracks the shell's "show desktop" state and the band occupied by its wallpaper.
/// Ordinary desktop surfaces stay in the ordinary band wherever Explorer does.
/// </summary>
internal sealed class DesktopSurfaceWatcher : IDisposable
{
    private static readonly uint CurrentProcessId = (uint)Environment.ProcessId;
    private static readonly TimeSpan FastCoalesceDelay = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan TrailingSettleDelay = TimeSpan.FromMilliseconds(500);
    private const long MinImmediateIntervalMs = 15;
    private const long MaxBurstDebounceMs = 200;
    private const int ObjidWindow = 0;

    private readonly Func<IReadOnlyList<nint>> _cardHandles;
    private readonly Dispatcher _dispatcher;
    private readonly BackgroundWinEventPump _pump;
    private readonly List<BackgroundWinEventPump.WinEventSubscription> _subscriptions = [];
    private readonly DispatcherTimer _fastTimer;
    private readonly DispatcherTimer _evaluateTimer;
    private readonly DispatcherTimer _trailingTimer;
    private BackgroundWinEventPump.WinEventSubscription? _shellShowHook;
    private BackgroundWinEventPump.WinEventSubscription? _raisedGlobalShowHook;
    private uint _shellProcessId;
    private long _lastEvaluateTick;
    private long _burstStartedTick;
    private nint _restoringWindow;
    private long _restoreDeadline;
    private int _reorderQueued;
    private bool _started;
    private bool _trailingForExit;
    private int _trailingPassesRemaining;
    private bool _disposed;

    internal DesktopSurfaceWatcher(
        Func<IReadOnlyList<nint>> cardHandles,
        BackgroundWinEventPump? pump = null)
    {
        _cardHandles = cardHandles;
        _pump = pump ?? BackgroundWinEventPump.Shared;
        var dispatcher = Dispatcher.CurrentDispatcher;
        _dispatcher = dispatcher;
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

    internal bool IsTopmostLayerRequired { get; private set; }

    internal bool HasGlobalShowHook => _raisedGlobalShowHook is { IsActive: true };

    internal uint ShellHookProcessId => Volatile.Read(ref _shellProcessId);

    internal void Start()
    {
        if (_started || _disposed)
        {
            return;
        }
        _started = true;
        Hook(
            DockNativeMethods.EventSystemForeground,
            DockNativeMethods.EventSystemForeground,
            static e => e.ChildId == 0 && e.ObjectId == ObjidWindow);
        Hook(
            DockNativeMethods.EventSystemMinimizeStart,
            DockNativeMethods.EventSystemMinimizeEnd,
            static e => e.ChildId == 0 && e.ObjectId == ObjidWindow);
        Hook(
            DockNativeMethods.EventSystemDesktopSwitch,
            DockNativeMethods.EventSystemDesktopSwitch,
            static e => e.ChildId == 0);
        Hook(
            DockNativeMethods.EventObjectCloaked,
            DockNativeMethods.EventObjectUncloaked,
            static e => BackgroundWinEventPump.IsTopLevelWindowEvent(e));
        _ = FindDesktopSurfaceWindow(out var shellProcess);
        RefreshShellShowHook(shellProcess);
        ScheduleCheck();
    }

    internal void ScheduleToggleDesktopCheck()
    {
        if (_disposed)
        {
            return;
        }
        EvaluateImmediate();
        ScheduleCheck();
        ArmTrailingSettleCheck(forExit: false);
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

    private void ArmTrailingSettleCheck(bool forExit)
    {
        // Slow multi-window minimise bursts (5+ heavy windows) can finish becoming iconic
        // ~250-400 ms after EVENT_SYSTEM_MINIMIZESTART, and slow-starting apps (CEF, Qt,
        // WebView2) can become visible/sized up to ~1 s after EVENT_SYSTEM_FOREGROUND.
        _trailingForExit = forExit;
        _trailingPassesRemaining = forExit ? 2 : 1;
        _trailingTimer.Stop();
        _trailingTimer.Start();
    }

    public void Dispose()
    {
        _disposed = true;
        _fastTimer.Stop();
        _evaluateTimer.Stop();
        _trailingTimer.Stop();
        _raisedGlobalShowHook?.Dispose();
        _raisedGlobalShowHook = null;
        _shellShowHook?.Dispose();
        _shellShowHook = null;
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
        _subscriptions.Clear();
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
        var isTopmost = NativeMethods.IsTopmostWindow(window);
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

    private void Hook(uint eventMin, uint eventMax, Func<WinEventMessage, bool> filter)
    {
        var subscription = _pump.Subscribe(
            eventMin,
            eventMax,
            filter,
            ForwardBackgroundEvent);
        if (subscription.IsActive)
        {
            _subscriptions.Add(subscription);
        }
        else
        {
            subscription.Dispose();
        }
    }

    internal static bool ShouldForwardShellObjectEvent(in WinEventMessage message)
    {
        if (message.ChildId != 0)
        {
            return false;
        }
        if (message.EventType == DockNativeMethods.EventObjectReorder)
        {
            return message.Window == 0 || message.Window == DockNativeMethods.GetDesktopWindow();
        }
        return BackgroundWinEventPump.IsTopLevelWindowEvent(message);
    }

    internal static bool ShouldForwardGlobalShowEvent(in WinEventMessage message, uint shellProcessId)
    {
        if (message.EventType != DockNativeMethods.EventObjectShow
            || !BackgroundWinEventPump.IsTopLevelWindowEvent(message))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(message.Window, out var processId);
        if (shellProcessId != 0 && processId == shellProcessId)
        {
            // Explorer show events already arrive via the process-scoped shell hook.
            return false;
        }

        var exStyle = NativeMethods.GetExStyleOf(message.Window);
        var isAppWindow = (exStyle & NativeMethods.WsExAppWindow) != 0;
        var isNoActivate = (exStyle & NativeMethods.WsExNoActivate) != 0;
        var isClickThrough = (exStyle & NativeMethods.WsExLayered) != 0
            && (exStyle & NativeMethods.WsExTransparent) != 0;
        return DesktopSurfaceRules.ShouldEvaluateWindowShowWhileRaised(
            isAppWindow,
            isNoActivate,
            isClickThrough);
    }

    private void ForwardBackgroundEvent(WinEventMessage message)
    {
        if (_disposed || _dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (message.EventType == DockNativeMethods.EventObjectReorder)
        {
            if (Interlocked.CompareExchange(ref _reorderQueued, 1, 0) != 0)
            {
                return;
            }
            _dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                Interlocked.Exchange(ref _reorderQueued, 0);
                if (!_disposed)
                {
                    OnWinEvent(
                        0,
                        message.EventType,
                        message.Window,
                        message.ObjectId,
                        message.ChildId,
                        message.EventThread,
                        message.EventTime);
                }
            });
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!_disposed)
            {
                OnWinEvent(
                    0,
                    message.EventType,
                    message.Window,
                    message.ObjectId,
                    message.ChildId,
                    message.EventThread,
                    message.EventTime);
            }
        });
    }

    internal void RefreshShellShowHook(uint shellProcessId)
    {
        if (!_started || _disposed)
        {
            return;
        }
        if (shellProcessId == 0)
        {
            _shellShowHook?.Dispose();
            _shellShowHook = null;
            Volatile.Write(ref _shellProcessId, 0);
            return;
        }
        if (shellProcessId == Volatile.Read(ref _shellProcessId) && _shellShowHook is { IsActive: true })
        {
            return;
        }
        _shellShowHook?.Dispose();
        _shellShowHook = null;
        Volatile.Write(ref _shellProcessId, shellProcessId);
        // Scope EVENT_OBJECT_SHOW..EVENT_OBJECT_REORDER strictly to Explorer in normal mode
        // so high-frequency non-Explorer UIA show bursts (e.g. Task Manager) never reach us.
        var subscription = _pump.Subscribe(
            DockNativeMethods.EventObjectShow,
            DockNativeMethods.EventObjectReorder,
            static message => ShouldForwardShellObjectEvent(message),
            ForwardBackgroundEvent,
            processId: shellProcessId,
            flags: DockNativeMethods.WineventOutOfContext);
        if (subscription.IsActive)
        {
            _shellShowHook = subscription;
        }
        else
        {
            subscription.Dispose();
        }
    }

    private void SyncRaisedGlobalShowHook(bool desktopShown)
    {
        if (!_started || _disposed || !desktopShown)
        {
            _raisedGlobalShowHook?.Dispose();
            _raisedGlobalShowHook = null;
            return;
        }

        if (_raisedGlobalShowHook is { IsActive: true })
        {
            return;
        }

        // Only listen for non-Explorer top-level window shows while cards are raised over
        // "show desktop", pre-filtered on the background WinEvent pump thread.
        var subscription = _pump.Subscribe(
            DockNativeMethods.EventObjectShow,
            DockNativeMethods.EventObjectShow,
            message => ShouldForwardGlobalShowEvent(message, Volatile.Read(ref _shellProcessId)),
            ForwardBackgroundEvent);
        if (subscription.IsActive)
        {
            _raisedGlobalShowHook = subscription;
        }
        else
        {
            subscription.Dispose();
        }
    }

    private void TryRebindShellProcess()
    {
        var shellWindow = NativeMethods.FindWindowW("Progman", null);
        if (shellWindow == 0)
        {
            shellWindow = DockNativeMethods.GetShellWindow();
        }
        if (shellWindow == 0)
        {
            return;
        }
        NativeMethods.GetWindowThreadProcessId(shellWindow, out var shellProcess);
        if (shellProcess != 0 && shellProcess != Volatile.Read(ref _shellProcessId))
        {
            RefreshShellShowHook(shellProcess);
        }
    }

    private void EvaluateImmediate(bool force = false)
    {
        if (_disposed)
        {
            return;
        }
        var now = Environment.TickCount64;
        if (force || now - _lastEvaluateTick >= MinImmediateIntervalMs)
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
        if (_disposed)
        {
            return;
        }
        if (_trailingPassesRemaining > 0)
        {
            _trailingPassesRemaining--;
        }
        Evaluate();
        if (_trailingPassesRemaining > 0 && (_trailingForExit ? IsDesktopShown : !IsDesktopShown))
        {
            _trailingTimer.Start();
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
            if (IsDesktopShown)
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
                if (className == "Shell_TrayWnd")
                {
                    NativeMethods.GetWindowThreadProcessId(window, out var trayProcess);
                    if (trayProcess != 0 && trayProcess != _shellProcessId)
                    {
                        RefreshShellShowHook(trayProcess);
                        EvaluateImmediate();
                        ScheduleCheck();
                        return;
                    }
                }
                if (className is "WorkerW" or "Progman")
                {
                    EvaluateImmediate();
                    ScheduleCheck();
                    return;
                }
                if (IsDesktopShown)
                {
                    var exStyle = NativeMethods.GetExStyleOf(window);
                    var isAppWindow = (exStyle & NativeMethods.WsExAppWindow) != 0;
                    var isNoActivate = (exStyle & NativeMethods.WsExNoActivate) != 0;
                    var isClickThrough = (exStyle & NativeMethods.WsExLayered) != 0
                        && (exStyle & NativeMethods.WsExTransparent) != 0;
                    if (DesktopSurfaceRules.ShouldEvaluateWindowShowWhileRaised(
                            isAppWindow,
                            isNoActivate,
                            isClickThrough))
                    {
                        EvaluateAndFollowUpWhileRaised();
                    }
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
                    if (IsDesktopShown)
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
            else if (eventType == DockNativeMethods.EventObjectUncloaked && IsDesktopShown)
            {
                EvaluateAndFollowUpWhileRaised();
                return;
            }
        }
        if (eventType == DockNativeMethods.EventSystemForeground && window != 0)
        {
            TryRebindShellProcess();
            if (IsDesktopShown)
            {
                EvaluateAndFollowUpWhileRaised(force: true);
                return;
            }
            if (ClassNameOf(window) is "Progman" or "WorkerW")
            {
                EvaluateImmediate();
                ScheduleCheck();
                return;
            }
        }
        if (eventType == DockNativeMethods.EventSystemMinimizeEnd && IsDesktopShown)
        {
            // MINIMIZEEND can arrive while the target is still iconic, before it
            // becomes visible in front of the desktop under the normal Z-order.
            if (IsApplicationWindowBlocking(window, _shellProcessId, isRestoring: true))
            {
                _restoringWindow = window;
                _restoreDeadline = Environment.TickCount64 + (long)SettleDelay.TotalMilliseconds;
                UpdateDesktopState(shown: false, anySurfaceCovered: false);
                ScheduleCheck();
            }
            else
            {
                EvaluateAndFollowUpWhileRaised(force: true);
            }
            return;
        }
        if (eventType == DockNativeMethods.EventSystemMinimizeStart)
        {
            if (!IsDesktopShown)
            {
                EvaluateImmediate();
            }
            ArmTrailingSettleCheck(forExit: false);
        }
        ScheduleCheck();
    }

    /// <summary>
    /// Evaluates immediately so raised cards drop without lag when an ordinary window
    /// is already visible, and schedules settled follow-up checks if cards remain raised
    /// (for apps that fire <c>EVENT_SYSTEM_FOREGROUND</c> before becoming visible, sized
    /// or uncloaked, or before Explorer finishes lowering <c>WorkerW</c>).
    /// </summary>
    private void EvaluateAndFollowUpWhileRaised(bool force = false)
    {
        EvaluateImmediate(force);
        if (IsDesktopShown)
        {
            ScheduleCheck();
            ArmTrailingSettleCheck(forExit: true);
        }
    }

    /// <summary>
    /// Reads the live foreground window independently of the desktop surface's Z-order.
    /// Explorer can still be lowering <c>WorkerW</c> when an application takes focus.
    /// The scan stops at the desktop surface for background windows, since Explorer can
    /// cover those with the wallpaper without minimising them.
    /// </summary>
    private void Evaluate()
    {
        using var _ = UiDiagnostics.Measure("桌面状态评估");
        _lastEvaluateTick = Environment.TickCount64;
        var desktopSurface = FindDesktopSurfaceWindow(out var shellProcess);
        RefreshShellShowHook(shellProcess);
        var found = desktopSurface != 0;
        // A restore notification can precede the window leaving its iconic state.
        // Keep it counted during that interval so shell reorders cannot lift cards again.
        if (_restoringWindow != 0 && (_lastEvaluateTick >= _restoreDeadline || !NativeMethods.IsIconic(_restoringWindow)))
        {
            _restoringWindow = 0;
        }
        var hasActiveApplication = IsApplicationWindowBlocking(DockNativeMethods.GetForegroundWindow(), shellProcess)
            || IsApplicationWindowBlocking(_restoringWindow, shellProcess, isRestoring: true);
        var blockers = 0;
        var surfaces = _cardHandles()
            .Where(handle => handle != 0
                && DockNativeMethods.IsWindow(handle)
                && NativeMethods.IsWindowVisible(handle)
                && !NativeMethods.IsIconic(handle))
            .ToHashSet();
        var surfacesAbove = 0;
        if (found && !hasActiveApplication)
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
                var isShellWindow = shellProcess != 0 && process == shellProcess;
                var className = ClassNameOf(window);
                if (DesktopSurfaceRules.CountsAsOrdinaryBlocker(
                        isShellWindow,
                        className,
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
        var shown = DesktopSurfaceRules.IsDesktopShown(found, blockers, hasActiveApplication);
        UpdateDesktopState(shown, anySurfaceCovered: surfacesAbove < surfaces.Count,
            topmostLayerRequired: shown && NativeMethods.IsTopmostWindow(desktopSurface));
    }

    private void UpdateDesktopState(bool shown, bool anySurfaceCovered, bool topmostLayerRequired = false)
    {
        if (shown)
        {
            if (!_trailingForExit)
            {
                _trailingPassesRemaining = 0;
                _trailingTimer.Stop();
            }
        }
        else if (_trailingForExit)
        {
            _trailingPassesRemaining = 0;
            _trailingTimer.Stop();
        }
        var action = DesktopSurfaceRules.ActionFor(IsDesktopShown, shown,
            anySurfaceCovered || IsTopmostLayerRequired != topmostLayerRequired);
        IsDesktopShown = shown;
        IsTopmostLayerRequired = topmostLayerRequired;
        SyncRaisedGlobalShowHook(shown);
        if (action != DesktopSurfaceAction.None)
        {
            Changed?.Invoke();
        }
    }

    private static bool IsApplicationWindowBlocking(nint window, uint shellProcess, bool isRestoring = false)
    {
        if (window == 0 || NativeMethods.IsChildWindow(window) || !NativeMethods.IsWindowVisible(window))
        {
            return false;
        }
        NativeMethods.GetWindowThreadProcessId(window, out var process);
        if (process == CurrentProcessId || !DockNativeMethods.GetWindowRect(window, out var rect))
        {
            return false;
        }

        var exStyle = NativeMethods.GetExStyleOf(window);
        return DesktopSurfaceRules.CountsAsForegroundBlocker(
            hasWindow: true,
            isOwnProcess: false,
            isMonitoredDesktopSurface: false,
            shellProcess != 0 && process == shellProcess,
            ClassNameOf(window),
            isVisible: true,
            NativeMethods.IsIconic(window),
            NativeMethods.IsWindowCloaked(window),
            isTopmost: (exStyle & NativeMethods.WsExTopmost) != 0,
            isLayeredClickThrough: (exStyle & NativeMethods.WsExLayered) != 0
                && (exStyle & NativeMethods.WsExTransparent) != 0,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top,
            isRestoring);
    }

    private static string ClassNameOf(nint window)
    {
        var className = new StringBuilder(64);
        return DockNativeMethods.GetClassNameW(window, className, className.Capacity) > 0
            ? className.ToString()
            : string.Empty;
    }
}
