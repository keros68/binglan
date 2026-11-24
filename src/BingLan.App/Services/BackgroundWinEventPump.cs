using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using BingLan.App.Interop;

namespace BingLan.App.Services;

/// <summary>Snapshot of a WinEvent notification pre-filtered on the background pump thread.</summary>
internal readonly record struct WinEventMessage(
    uint EventType,
    nint Window,
    int ObjectId,
    int ChildId,
    uint EventThread,
    uint EventTime);

/// <summary>
/// Dedicated Win32 STA background message pump for out-of-context <c>SetWinEventHook</c>
/// subscriptions so high-frequency OS accessibility/UIA bursts (such as Task Manager's
/// <c>EVENT_OBJECT_SHOW</c> stream) are pre-filtered off the WPF UI thread instead of
/// starving WPF's dispatcher message loop. Starts lazily on the first active hook and
/// exits cleanly via <c>PostThreadMessage(WM_QUIT)</c> + <c>Join</c> when zero hooks remain.
/// </summary>
internal sealed class BackgroundWinEventPump : IDisposable
{
    internal const uint DefaultFlags =
        DockNativeMethods.WineventOutOfContext | DockNativeMethods.WineventSkipOwnProcess;

    private const uint WmQuit = 0x0012;
    private const uint WmAppProcessQueue = 0x8001;
    private const uint PmNoRemove = 0x0000;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    internal static BackgroundWinEventPump Shared { get; } = new();

    private readonly object _lifecycleLock = new();
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly ConcurrentDictionary<nint, HookRegistration> _hooks = new();
    // Keep the native callback delegate rooted for the pump's entire lifetime.
    private readonly NativeMethods.WinEventProc _winEventProc;

    private Thread? _thread;
    private uint _threadId;
    private int _activeSubscriptions;
    private bool _disposed;

    internal BackgroundWinEventPump()
    {
        _winEventProc = OnNativeWinEvent;
    }

    internal bool IsRunning
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _thread is { IsAlive: true };
            }
        }
    }

    internal int ActiveHookCount
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _activeSubscriptions;
            }
        }
    }

    /// <summary>
    /// Fast background-thread filter for top-level window WinEvents: drops non-window
    /// objects, child elements and Win32 child controls without touching the UI thread.
    /// </summary>
    internal static bool IsTopLevelWindowEvent(in WinEventMessage message) =>
        message.Window != 0
        && message.ObjectId == DockNativeMethods.ObjidWindow
        && message.ChildId == 0
        && !NativeMethods.IsChildWindow(message.Window);

    /// <summary>
    /// Registers an out-of-context WinEvent hook on the background pump thread.
    /// <paramref name="backgroundFilter"/> runs on the pump thread before any UI dispatch.
    /// </summary>
    internal WinEventSubscription Subscribe(
        uint eventMin,
        uint eventMax,
        Func<WinEventMessage, bool>? backgroundFilter,
        Action<WinEventMessage> onEvent,
        uint processId = 0,
        uint flags = DefaultFlags,
        int coalesceWindowMs = 0)
    {
        ArgumentNullException.ThrowIfNull(onEvent);

        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return WinEventSubscription.Inactive;
            }

            if (!EnsureThreadRunningLocked())
            {
                return WinEventSubscription.Inactive;
            }

            var registration = new HookRegistration(backgroundFilter, onEvent, coalesceWindowMs);
            nint hookHandle = 0;

            if (Thread.CurrentThread == _thread)
            {
                hookHandle = InstallHookOnPumpThread(eventMin, eventMax, processId, flags, registration);
            }
            else
            {
                using var done = new ManualResetEventSlim(false);
                _commands.Enqueue(() =>
                {
                    try
                    {
                        hookHandle = InstallHookOnPumpThread(eventMin, eventMax, processId, flags, registration);
                    }
                    finally
                    {
                        try { done.Set(); } catch (ObjectDisposedException) { }
                    }
                });
                if (PostThreadMessageW(Volatile.Read(ref _threadId), WmAppProcessQueue, 0, 0))
                {
                    done.Wait(CommandTimeout);
                }
            }

            if (hookHandle == 0)
            {
                if (_activeSubscriptions == 0)
                {
                    StopThreadLocked();
                }
                return WinEventSubscription.Inactive;
            }

            _activeSubscriptions++;
            return new WinEventSubscription(this, hookHandle, registration);
        }
    }

    /// <summary>
    /// Convenience helper that pre-filters on the background pump thread and coalesces
    /// accepted events into at most one in-flight <see cref="Dispatcher.BeginInvoke"/>.
    /// </summary>
    internal WinEventSubscription SubscribeCoalesced(
        uint eventMin,
        uint eventMax,
        Func<WinEventMessage, bool>? backgroundFilter,
        Dispatcher dispatcher,
        DispatcherPriority priority,
        Action onCoalesced,
        uint processId = 0,
        uint flags = DefaultFlags,
        int coalesceWindowMs = 0)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(onCoalesced);

        var pending = 0;
        var disposed = 0;
        var subscription = Subscribe(
            eventMin,
            eventMax,
            backgroundFilter,
            _ =>
            {
                if (Volatile.Read(ref disposed) != 0
                    || dispatcher.HasShutdownStarted
                    || Interlocked.CompareExchange(ref pending, 1, 0) != 0)
                {
                    return;
                }
                dispatcher.BeginInvoke(priority, () =>
                {
                    Interlocked.Exchange(ref pending, 0);
                    if (Volatile.Read(ref disposed) == 0)
                    {
                        onCoalesced();
                    }
                });
            },
            processId,
            flags,
            coalesceWindowMs);

        subscription.SetDisposedFlag(() => Volatile.Write(ref disposed, 1));
        return subscription;
    }

    internal void Unsubscribe(nint hookHandle, HookRegistration registration)
    {
        registration.MarkDisposed();
        if (hookHandle == 0)
        {
            return;
        }

        lock (_lifecycleLock)
        {
            if (_thread is { IsAlive: true })
            {
                if (Thread.CurrentThread == _thread)
                {
                    RemoveHookOnPumpThread(hookHandle);
                }
                else
                {
                    using var done = new ManualResetEventSlim(false);
                    _commands.Enqueue(() =>
                    {
                        try
                        {
                            RemoveHookOnPumpThread(hookHandle);
                        }
                        finally
                        {
                            try { done.Set(); } catch (ObjectDisposedException) { }
                        }
                    });
                    if (PostThreadMessageW(Volatile.Read(ref _threadId), WmAppProcessQueue, 0, 0))
                    {
                        done.Wait(CommandTimeout);
                    }
                }
            }

            if (_activeSubscriptions > 0)
            {
                _activeSubscriptions--;
            }

            if (_activeSubscriptions == 0)
            {
                StopThreadLocked();
            }
        }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _activeSubscriptions = 0;
            StopThreadLocked();
        }
    }

    private bool EnsureThreadRunningLocked()
    {
        if (_thread is { IsAlive: true } && Volatile.Read(ref _threadId) != 0)
        {
            return true;
        }

        // Clear stale queued commands from any prior lifecycle.
        while (_commands.TryDequeue(out _))
        {
        }

        using var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() => RunMessageLoop(ready))
        {
            IsBackground = true,
            Name = "BingLan.WinEventPump"
        };
        thread.SetApartmentState(ApartmentState.STA);
        _thread = thread;
        Volatile.Write(ref _threadId, 0);
        thread.Start();

        ready.Wait(StartupTimeout);
        return Volatile.Read(ref _threadId) != 0 && thread.IsAlive;
    }

    private void StopThreadLocked()
    {
        var thread = _thread;
        var threadId = Volatile.Read(ref _threadId);
        if (thread is null)
        {
            return;
        }

        if (threadId != 0)
        {
            _ = PostThreadMessageW(threadId, WmQuit, 0, 0);
        }

        if (thread != Thread.CurrentThread && thread.IsAlive)
        {
            thread.Join(ShutdownTimeout);
        }

        _thread = null;
        Volatile.Write(ref _threadId, 0);
        while (_commands.TryDequeue(out _))
        {
        }
    }

    private void RunMessageLoop(ManualResetEventSlim ready)
    {
        try
        {
            // Force creation of the thread's Win32 message queue before accepting PostThreadMessageW.
            _ = PeekMessageW(out _, 0, 0, 0, PmNoRemove);
            Volatile.Write(ref _threadId, GetCurrentThreadId());
            try
            {
                ready.Set();
            }
            catch (ObjectDisposedException)
            {
            }

            DrainCommands();

            while (DesktopDoubleClickNativeMethods.GetMessageW(out var message, 0, 0, 0) > 0)
            {
                if (message.Message == WmAppProcessQueue)
                {
                    DrainCommands();
                    continue;
                }
                DesktopDoubleClickNativeMethods.DispatchMessageW(ref message);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StartupLog.WriteCrash("BackgroundWinEventPump.RunMessageLoop", exception);
        }
        finally
        {
            DrainCommands();
            var currentTid = GetCurrentThreadId();
            foreach (var pair in _hooks)
            {
                if (pair.Value.OwnerThreadId == currentTid && _hooks.TryRemove(pair.Key, out _))
                {
                    _ = NativeMethods.UnhookWinEvent(pair.Key);
                }
            }
            Interlocked.CompareExchange(ref _threadId, 0, currentTid);
            try
            {
                ready.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void DrainCommands()
    {
        while (_commands.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                StartupLog.WriteCrash("BackgroundWinEventPump.Command", exception);
            }
        }
    }

    private nint InstallHookOnPumpThread(
        uint eventMin,
        uint eventMax,
        uint processId,
        uint flags,
        HookRegistration registration)
    {
        var hook = NativeMethods.SetWinEventHook(
            eventMin,
            eventMax,
            0,
            _winEventProc,
            processId,
            0,
            flags);
        if (hook != 0)
        {
            registration.OwnerThreadId = GetCurrentThreadId();
            _hooks[hook] = registration;
        }
        return hook;
    }

    private void RemoveHookOnPumpThread(nint hook)
    {
        if (_hooks.TryRemove(hook, out _))
        {
            _ = NativeMethods.UnhookWinEvent(hook);
        }
    }

    private void OnNativeWinEvent(
        nint hook,
        uint eventType,
        nint window,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        if (!_hooks.TryGetValue(hook, out var registration) || registration.IsDisposed)
        {
            return;
        }

        try
        {
            var message = new WinEventMessage(eventType, window, idObject, idChild, eventThread, eventTime);
            if (registration.BackgroundFilter is { } filter && !filter(message))
            {
                return;
            }
            if (registration.CoalesceWindowMs > 0)
            {
                var now = Environment.TickCount64;
                if (now - registration.LastAcceptedTick < registration.CoalesceWindowMs)
                {
                    return;
                }
                registration.LastAcceptedTick = now;
            }
            registration.OnEvent(message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StartupLog.WriteCrash("BackgroundWinEventPump.OnWinEvent", exception);
        }
    }

    internal sealed class HookRegistration(
        Func<WinEventMessage, bool>? backgroundFilter,
        Action<WinEventMessage> onEvent,
        int coalesceWindowMs)
    {
        private int _disposed;

        internal Func<WinEventMessage, bool>? BackgroundFilter { get; } = backgroundFilter;
        internal Action<WinEventMessage> OnEvent { get; } = onEvent;
        internal int CoalesceWindowMs { get; } = coalesceWindowMs;
        internal uint OwnerThreadId { get; set; }
        internal long LastAcceptedTick { get; set; } = long.MinValue / 2;
        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        internal void MarkDisposed() => Volatile.Write(ref _disposed, 1);
    }

    internal sealed class WinEventSubscription : IDisposable
    {
        internal static WinEventSubscription Inactive { get; } = new();

        private readonly BackgroundWinEventPump? _pump;
        private readonly HookRegistration? _registration;
        private Action? _onDispose;
        private nint _hookHandle;
        private int _disposed;

        private WinEventSubscription()
        {
            _disposed = 1;
        }

        internal WinEventSubscription(
            BackgroundWinEventPump pump,
            nint hookHandle,
            HookRegistration registration)
        {
            _pump = pump;
            _hookHandle = hookHandle;
            _registration = registration;
        }

        internal bool IsActive => _hookHandle != 0 && Volatile.Read(ref _disposed) == 0;

        internal void SetDisposedFlag(Action onDispose) => _onDispose = onDispose;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _onDispose?.Invoke();
            var handle = Interlocked.Exchange(ref _hookHandle, 0);
            if (_pump is not null && _registration is not null)
            {
                _pump.Unsubscribe(handle, _registration);
            }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint idThread, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(
        out DesktopDoubleClickNativeMethods.NativeMessage message,
        nint hwnd,
        uint messageFilterMin,
        uint messageFilterMax,
        uint removeMessage);
}
