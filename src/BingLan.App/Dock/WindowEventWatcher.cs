using System.Windows.Threading;
using BingLan.App.Interop;

namespace BingLan.App.Dock;

internal sealed class WindowEventWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _debounceTimer;
    private readonly DispatcherTimer _reconcileTimer;
    private readonly DockNativeMethods.WinEventProc _callback;
    private readonly List<nint> _hooks = [];
    private bool _disposed;
    private bool _refreshQueued;

    internal WindowEventWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _callback = OnWindowEvent;
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

    internal int Start()
    {
        Hook(DockNativeMethods.EventSystemForeground, DockNativeMethods.EventSystemForeground);
        Hook(DockNativeMethods.EventSystemMinimizeStart, DockNativeMethods.EventSystemMinimizeEnd);
        Hook(DockNativeMethods.EventSystemDesktopSwitch, DockNativeMethods.EventSystemDesktopSwitch);
        Hook(DockNativeMethods.EventObjectCreate, DockNativeMethods.EventObjectHide);
        Hook(DockNativeMethods.EventObjectNameChange, DockNativeMethods.EventObjectNameChange);
        Hook(DockNativeMethods.EventObjectCloaked, DockNativeMethods.EventObjectUncloaked);
        _reconcileTimer.Start();
        return _hooks.Count;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _debounceTimer.Stop();
        _reconcileTimer.Stop();
        foreach (var hook in _hooks)
        {
            DockNativeMethods.UnhookWinEvent(hook);
        }
        _hooks.Clear();
    }

    private void Hook(uint eventMin, uint eventMax)
    {
        var hook = DockNativeMethods.SetWinEventHook(
            eventMin,
            eventMax,
            0,
            _callback,
            0,
            0,
            DockNativeMethods.WineventOutOfContext | DockNativeMethods.WineventSkipOwnProcess);
        if (hook != 0)
        {
            _hooks.Add(hook);
        }
    }

    private void OnWindowEvent(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed)
        {
            return;
        }
        var objectEvent = eventType >= DockNativeMethods.EventObjectCreate;
        if (objectEvent
            && (window == 0
                || objectId != DockNativeMethods.ObjidWindow
                || NativeMethods.IsChildWindow(window)))
        {
            return;
        }

        if (_refreshQueued)
        {
            return;
        }
        _refreshQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, QueueRefresh);
    }

    private void QueueRefresh()
    {
        _refreshQueued = false;
        if (_disposed)
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
