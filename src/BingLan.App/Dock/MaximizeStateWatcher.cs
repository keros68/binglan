using System.Windows.Threading;
using BingLan.App.Interop;

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
    private readonly DockNativeMethods.WinEventProc _callback;
    private readonly List<nint> _hooks = [];
    private bool _disposed;
    private bool _notifyQueued;

    internal MaximizeStateWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _callback = OnWinEvent;
    }

    /// <summary>Raised on the dispatcher thread whenever a top-level window changes state.</summary>
    internal event Action? TopLevelStateChanged;

    internal void Start()
    {
        Hook(DockNativeMethods.EventObjectStateChange, DockNativeMethods.EventObjectStateChange);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
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

    // Out-of-context: the callback arrives on this thread's message loop, which the
    // WPF dispatcher already pumps.
    internal void OnWinEvent(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed
            || window == 0
            || objectId != DockNativeMethods.ObjidWindow
            || childId != 0
            || NativeMethods.IsChildWindow(window)
            || _notifyQueued)
        {
            return;
        }

        // Some apps raise state changes in bursts; the receiver reads the current
        // state anyway, so one queued notice covers every event before it runs.
        _notifyQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            _notifyQueued = false;
            if (!_disposed)
            {
                TopLevelStateChanged?.Invoke();
            }
        });
    }
}
