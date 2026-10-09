using System.Runtime.InteropServices;
using System.Windows.Threading;
using BingLan.App.Interop;
using BingLan.Core.Desktop;

namespace BingLan.App.Services;

/// <summary>
/// Watches for the shell's "show desktop": Explorer minimises the ordinary windows
/// and raises the desktop surface above the non-topmost band, which would cover the
/// cards that sit at the bottom of that band behind the wallpaper. While that state
/// lasts the cards are lifted into the topmost band under the taskbar, and returned
/// below every ordinary window once they come back. The WinEvent hook only triggers
/// the check; the state itself is read from the z-order, so lifting the cards cannot
/// flip the verdict, and clicking the desktop without showing it (windows stay open)
/// never lifts them.
/// </summary>
internal sealed class DesktopSurfaceWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint WinEventOutOfContext = 0;
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(150);
    private const int ObjidWindow = 0;

    private readonly Func<IReadOnlyList<nint>> _cardHandles;
    private readonly NativeMethods.WinEventProc _hookCallback;
    private nint _hook;
    private DispatcherTimer? _evaluateTimer;

    internal DesktopSurfaceWatcher(Func<IReadOnlyList<nint>> cardHandles)
    {
        _cardHandles = cardHandles;
        // Keep the delegate alive for as long as the hook exists.
        _hookCallback = OnWinEvent;
    }

    /// <summary>Raised when <see cref="IsDesktopShown"/> changed.</summary>
    internal event Action? Changed;

    internal bool IsDesktopShown { get; private set; }

    internal void Start()
    {
        if (_hook != 0)
        {
            return;
        }
        _hook = NativeMethods.SetWinEventHook(
            EventSystemForeground,
            EventSystemMinimizeStart,
            nint.Zero,
            _hookCallback,
            0,
            0,
            WinEventOutOfContext);
    }

    public void Dispose()
    {
        _evaluateTimer?.Stop();
        _evaluateTimer = null;
        if (_hook != 0)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = 0;
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
        if (idObject != ObjidWindow)
        {
            return;
        }
        // The shell raises the desktop surface while it minimises everything, so the
        // z-order settles a moment after the first event. Coalesce the burst.
        if (_evaluateTimer is null)
        {
            _evaluateTimer = new DispatcherTimer(
                SettleDelay,
                DispatcherPriority.Background,
                (_, _) =>
                {
                    _evaluateTimer = null;
                    Evaluate();
                },
                Dispatcher.CurrentDispatcher);
            _evaluateTimer.Start();
        }
    }

    /// <summary>
    /// Reads whether the desktop surface covers the cards: Progman has been raised
    /// and no visible, un-minimised window of another program is left above it.
    /// Topmost windows (taskbar, dock), Explorer's own shell windows and the cards
    /// themselves do not count.
    /// </summary>
    private void Evaluate()
    {
        var progman = NativeMethods.FindWindowW("Progman", null);
        var found = progman != 0;
        var blockers = 0;
        if (found)
        {
            NativeMethods.GetWindowThreadProcessId(progman, out var shellProcess);
            var cards = _cardHandles();
            NativeMethods.EnumWindows((window, _) =>
            {
                if (window == progman)
                {
                    return false;
                }
                if (!NativeMethods.IsWindowVisible(window) || NativeMethods.IsIconic(window))
                {
                    return true;
                }
                if ((NativeMethods.GetExStyleOf(window) & NativeMethods.WsExTopmost) != 0)
                {
                    return true;
                }
                if (((IReadOnlyList<nint>)cards).Contains(window))
                {
                    return true;
                }
                NativeMethods.GetWindowThreadProcessId(window, out var process);
                if (process == shellProcess)
                {
                    return true;
                }
                blockers++;
                // A few are enough to prove ordinary windows are still around.
                return blockers < 3;
            }, 0);
        }
        var shown = DesktopSurfaceRules.IsDesktopShown(found, blockers);
        if (shown == IsDesktopShown)
        {
            return;
        }
        IsDesktopShown = shown;
        Changed?.Invoke();
    }
}
