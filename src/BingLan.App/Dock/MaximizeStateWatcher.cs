using System.Windows.Threading;
using BingLan.App.Interop;
using BingLan.App.Services;

namespace BingLan.App.Dock;

/// <summary>
/// Immediate notice that a top-level window changed state, via the accessibility
/// event stream — the 150ms foreground poll cannot react to the maximize click
/// itself, which reads as a pause before the dock collapses. No prediction is done
/// here (not even IsZoomed): the earliest state events of a maximize can arrive
/// before the zoomed style is applied, so pre-filtering would drop them and fall
/// back to the poll. The receiver's own foreground evaluation is cheap and decides
/// what actually happened. The event is raised on the dispatcher of the thread the
/// watcher was created on.
/// </summary>
internal sealed class MaximizeStateWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly BackgroundWinEventPump _pump;
    private BackgroundWinEventPump.WinEventSubscription? _subscription;
    private int _disposed;
    private int _notifyQueued;

    internal MaximizeStateWatcher(Dispatcher dispatcher, BackgroundWinEventPump? pump = null)
    {
        _dispatcher = dispatcher;
        _pump = pump ?? BackgroundWinEventPump.Shared;
    }

    /// <summary>Raised on the dispatcher thread whenever a top-level window changes state.</summary>
    internal event Action? TopLevelStateChanged;

    internal void Start()
    {
        if (Volatile.Read(ref _disposed) != 0 || _subscription is not null)
        {
            return;
        }
        var subscription = _pump.Subscribe(
            DockNativeMethods.EventObjectStateChange,
            DockNativeMethods.EventObjectStateChange,
            static message => BackgroundWinEventPump.IsTopLevelWindowEvent(message),
            message => OnWinEvent(
                0,
                message.EventType,
                message.Window,
                message.ObjectId,
                message.ChildId,
                message.EventThread,
                message.EventTime));
        if (subscription.IsActive)
        {
            _subscription = subscription;
        }
        else
        {
            subscription.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _subscription?.Dispose();
        _subscription = null;
    }

    // Receives pre-filtered top-level events from BackgroundWinEventPump (and direct test calls).
    internal void OnWinEvent(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        UiDiagnostics.Hit(UiActivity.StateEvent);
        if (Volatile.Read(ref _disposed) != 0
            || _dispatcher.HasShutdownStarted
            || window == 0
            || objectId != DockNativeMethods.ObjidWindow
            || childId != 0
            || NativeMethods.IsChildWindow(window)
            || Interlocked.CompareExchange(ref _notifyQueued, 1, 0) != 0)
        {
            return;
        }

        // Some apps raise state changes in bursts; the receiver reads the current
        // state anyway, so one queued notice covers every event before it runs.
        _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            Interlocked.Exchange(ref _notifyQueued, 0);
            if (Volatile.Read(ref _disposed) == 0)
            {
                TopLevelStateChanged?.Invoke();
            }
        });
    }
}
