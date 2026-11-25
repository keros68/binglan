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

    internal sealed class PumpThreadInstance
    {
        internal ConcurrentQueue<Action> Commands { get; } = new();
        internal ManualResetEventSlim Ready { get; } = new(false);
        internal Thread? Thread { get; set; }
        internal uint ThreadId;
        internal int Stopping;
    }

    internal static BackgroundWinEventPump Shared { get; } = new();

    private readonly object _lifecycleLock = new();
    private readonly TimeSpan _shutdownTimeout;
    private readonly ConcurrentDictionary<nint, HookRegistration> _hooks = new();
    // Keep the native callback delegate rooted for the pump's entire lifetime.
    private readonly NativeMethods.WinEventProc _winEventProc;

    private PumpThreadInstance? _currentInstance;
    private int _activeSubscriptions;
    private bool _disposed;

    internal BackgroundWinEventPump()
        : this(ShutdownTimeout)
    {
    }

    internal BackgroundWinEventPump(TimeSpan? shutdownTimeout)
    {
        _shutdownTimeout = shutdownTimeout is { } timeout && timeout > TimeSpan.Zero
            ? timeout
            : ShutdownTimeout;
        _winEventProc = OnNativeWinEvent;
    }

    internal bool IsRunning
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _currentInstance?.Thread is { IsAlive: true };
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

            var instance = _currentInstance!;
            var registration = new HookRegistration(backgroundFilter, onEvent, coalesceWindowMs, instance);
            nint hookHandle = 0;

            if (Thread.CurrentThread == instance.Thread)
            {
                if (Volatile.Read(ref instance.Stopping) == 0)
                {
                    hookHandle = InstallHookOnPumpThread(eventMin, eventMax, processId, flags, registration);
                }
            }
            else
            {
                using var done = new ManualResetEventSlim(false);
                // 0 = pending, 1 = installed by pump thread, 2 = timed out on caller thread.
                var state = 0;
                nint installedHandle = 0;
                instance.Commands.Enqueue(() =>
                {
                    try
                    {
                        if (Volatile.Read(ref instance.Stopping) != 0 || Volatile.Read(ref state) != 0)
                        {
                            return;
                        }
                        var installed = InstallHookOnPumpThread(eventMin, eventMax, processId, flags, registration);
                        Volatile.Write(ref installedHandle, installed);
                        if (Interlocked.CompareExchange(ref state, 1, 0) != 0 && installed != 0)
                        {
                            // Caller timed out while SetWinEventHook ran: immediately release so
                            // no orphaned hook or _hooks entry leaks.
                            RemoveHookOnPumpThread(installed);
                        }
                    }
                    finally
                    {
                        try { done.Set(); } catch (ObjectDisposedException) { }
                    }
                });
                var posted = PostThreadMessageW(Volatile.Read(ref instance.ThreadId), WmAppProcessQueue, 0, 0);
                if (posted && done.Wait(CommandTimeout))
                {
                    hookHandle = Volatile.Read(ref installedHandle);
                }
                else if (Interlocked.CompareExchange(ref state, 2, 0) == 1)
                {
                    // Pump thread completed right as Wait timed out; take ownership cleanly.
                    hookHandle = Volatile.Read(ref installedHandle);
                }
                else
                {
                    // Timeout won: pump thread will skip installation or immediately unhook.
                    hookHandle = 0;
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
            var instance = registration.OwnerInstance;
            if (instance is not null && instance.Thread is { IsAlive: true } ownerThread)
            {
                if (Thread.CurrentThread == ownerThread)
                {
                    RemoveHookOnPumpThread(hookHandle);
                }
                else
                {
                    using var done = new ManualResetEventSlim(false);
                    instance.Commands.Enqueue(() =>
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
                    var ownerTid = Volatile.Read(ref instance.ThreadId);
                    if (ownerTid != 0 && PostThreadMessageW(ownerTid, WmAppProcessQueue, 0, 0))
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

    internal int? CurrentThreadManagedIdForTests
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _currentInstance?.Thread?.ManagedThreadId;
            }
        }
    }

    internal void BlockCurrentPumpForTests(ManualResetEventSlim releaseGate)
    {
        lock (_lifecycleLock)
        {
            var instance = _currentInstance;
            if (instance is null || Volatile.Read(ref instance.ThreadId) == 0)
            {
                return;
            }
            instance.Commands.Enqueue(() => releaseGate.Wait(TimeSpan.FromSeconds(5)));
            _ = PostThreadMessageW(Volatile.Read(ref instance.ThreadId), WmAppProcessQueue, 0, 0);
        }
    }

    private bool EnsureThreadRunningLocked()
    {
        if (_currentInstance is { Thread.IsAlive: true } existing
            && Volatile.Read(ref existing.Stopping) == 0
            && Volatile.Read(ref existing.ThreadId) != 0)
        {
            return true;
        }

        // Each pump lifecycle gets its own isolated instance & command queue so a slow-retiring
        // thread can never drain commands queued for a successor thread.
        var instance = new PumpThreadInstance();
        var thread = new Thread(() => RunMessageLoop(instance))
        {
            IsBackground = true,
            Name = "BingLan.WinEventPump"
        };
        thread.SetApartmentState(ApartmentState.STA);
        instance.Thread = thread;
        _currentInstance = instance;
        thread.Start();

        instance.Ready.Wait(StartupTimeout);
        return Volatile.Read(ref instance.ThreadId) != 0 && thread.IsAlive;
    }

    private void StopThreadLocked()
    {
        var instance = _currentInstance;
        if (instance is null)
        {
            return;
        }

        _currentInstance = null;
        Volatile.Write(ref instance.Stopping, 1);
        var threadId = Volatile.Read(ref instance.ThreadId);
        if (threadId != 0)
        {
            _ = PostThreadMessageW(threadId, WmQuit, 0, 0);
        }

        if (instance.Thread is { IsAlive: true } thread && thread != Thread.CurrentThread)
        {
            thread.Join(_shutdownTimeout);
        }
    }

    private void RunMessageLoop(PumpThreadInstance instance)
    {
        try
        {
            // Force creation of the thread's Win32 message queue before accepting PostThreadMessageW.
            _ = PeekMessageW(out _, 0, 0, 0, PmNoRemove);
            Volatile.Write(ref instance.ThreadId, GetCurrentThreadId());
            try
            {
                instance.Ready.Set();
            }
            catch (ObjectDisposedException)
            {
            }

            DrainCommands(instance);

            while (DesktopDoubleClickNativeMethods.GetMessageW(out var message, 0, 0, 0) > 0)
            {
                if (message.Message == WmAppProcessQueue)
                {
                    DrainCommands(instance);
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
            DrainCommands(instance);
            var currentTid = GetCurrentThreadId();
            foreach (var pair in _hooks)
            {
                if (pair.Value.OwnerThreadId == currentTid && _hooks.TryRemove(pair.Key, out _))
                {
                    _ = NativeMethods.UnhookWinEvent(pair.Key);
                }
            }
            Interlocked.CompareExchange(ref instance.ThreadId, 0, currentTid);
            try
            {
                instance.Ready.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static void DrainCommands(PumpThreadInstance instance)
    {
        while (instance.Commands.TryDequeue(out var action))
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
        int coalesceWindowMs,
        PumpThreadInstance ownerInstance)
    {
        private int _disposed;

        internal Func<WinEventMessage, bool>? BackgroundFilter { get; } = backgroundFilter;
        internal Action<WinEventMessage> OnEvent { get; } = onEvent;
        internal int CoalesceWindowMs { get; } = coalesceWindowMs;
        internal PumpThreadInstance? OwnerInstance { get; } = ownerInstance;
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
