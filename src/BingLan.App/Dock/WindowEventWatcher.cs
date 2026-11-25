using System.Windows.Threading;
using BingLan.App.Interop;
using BingLan.App.Services;

namespace BingLan.App.Dock;

internal sealed class WindowEventWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly BackgroundWinEventPump _pump;
    private readonly DispatcherTimer _debounceTimer;
    private readonly DispatcherTimer _reconcileTimer;
    private readonly List<BackgroundWinEventPump.WinEventSubscription> _subscriptions = [];
    private int _disposed;
    private int _refreshQueued;
    private int _foregroundQueued;

    internal WindowEventWatcher(Dispatcher dispatcher, BackgroundWinEventPump? pump = null)
    {
        _dispatcher = dispatcher;
        _pump = pump ?? BackgroundWinEventPump.Shared;
        _debounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(90),
            DispatcherPriority.Background,
            (_, _) => Flush(),
            dispatcher);
        _debounceTimer.Stop();
        _reconcileTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(5),
            DispatcherPriority.Background,
            (_, _) => SnapshotInvalidated?.Invoke(),
            dispatcher);
        _reconcileTimer.Stop();
    }

    internal event Action? SnapshotInvalidated;

    /// <summary>Raised on the next input turn after the foreground window changes, without the snapshot debounce.</summary>
    internal event Action? ForegroundChanged;

    internal int Start()
    {
        Hook(DockNativeMethods.EventSystemForeground, DockNativeMethods.EventSystemForeground, isObjectRange: false);
        Hook(DockNativeMethods.EventSystemMinimizeStart, DockNativeMethods.EventSystemMinimizeEnd, isObjectRange: false);
        Hook(DockNativeMethods.EventSystemDesktopSwitch, DockNativeMethods.EventSystemDesktopSwitch, isObjectRange: false);
        Hook(DockNativeMethods.EventObjectCreate, DockNativeMethods.EventObjectHide, isObjectRange: true);
        Hook(DockNativeMethods.EventObjectNameChange, DockNativeMethods.EventObjectNameChange, isObjectRange: true);
        Hook(DockNativeMethods.EventObjectCloaked, DockNativeMethods.EventObjectUncloaked, isObjectRange: true);
        _reconcileTimer.Start();
        return _subscriptions.Count;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _debounceTimer.Stop();
        _reconcileTimer.Stop();
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
        _subscriptions.Clear();
    }

    private void Hook(uint eventMin, uint eventMax, bool isObjectRange)
    {
        var subscription = _pump.Subscribe(
            eventMin,
            eventMax,
            isObjectRange
                ? static message => BackgroundWinEventPump.IsTopLevelWindowEvent(message)
                : static message => message.ChildId == 0,
            OnBackgroundWindowEvent);
        if (subscription.IsActive)
        {
            _subscriptions.Add(subscription);
        }
        else
        {
            subscription.Dispose();
        }
    }

    private void OnBackgroundWindowEvent(WinEventMessage message)
    {
        if (Volatile.Read(ref _disposed) != 0 || _dispatcher.HasShutdownStarted)
        {
            return;
        }
        UiDiagnostics.Hit(UiActivity.WindowEvent);

        if (message.EventType == DockNativeMethods.EventSystemForeground
            && Interlocked.CompareExchange(ref _foregroundQueued, 1, 0) == 0)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                Interlocked.Exchange(ref _foregroundQueued, 0);
                if (Volatile.Read(ref _disposed) == 0) ForegroundChanged?.Invoke();
            });
        }

        if (Interlocked.CompareExchange(ref _refreshQueued, 1, 0) != 0)
        {
            return;
        }
        _dispatcher.BeginInvoke(DispatcherPriority.Background, QueueRefresh);
    }

    private void QueueRefresh()
    {
        Interlocked.Exchange(ref _refreshQueued, 0);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void Flush()
    {
        _debounceTimer.Stop();
        SnapshotInvalidated?.Invoke();
    }
}
