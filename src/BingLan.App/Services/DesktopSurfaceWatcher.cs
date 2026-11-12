using System.Runtime.InteropServices;
using System.Text;
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
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(150);
    private const int ObjidWindow = 0;

    private readonly Func<IReadOnlyList<nint>> _cardHandles;
    private readonly NativeMethods.WinEventProc _hookCallback;
    private readonly List<nint> _hooks = [];
    private readonly DispatcherTimer _evaluateTimer;
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

    /// <summary>Raised when <see cref="IsDesktopShown"/> changed.</summary>
    internal event Action? Changed;

    internal bool IsDesktopShown { get; private set; }

    internal void Start()
    {
        if (_hooks.Count > 0 || _disposed)
        {
            return;
        }
        Hook(DockNativeMethods.EventSystemForeground, DockNativeMethods.EventSystemForeground);
        Hook(DockNativeMethods.EventSystemMinimizeStart, DockNativeMethods.EventSystemMinimizeEnd);
    }

    public void Dispose()
    {
        _disposed = true;
        _evaluateTimer.Stop();
        foreach (var hook in _hooks)
        {
            NativeMethods.UnhookWinEvent(hook);
        }
        _hooks.Clear();
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
        // The shell raises the desktop surface while it minimises everything, so the
        // z-order settles a moment after the first event. Coalesce the burst on one
        // reusable timer instead of allocating a new timer per event burst.
        if (!_evaluateTimer.IsEnabled)
        {
            _evaluateTimer.Start();
        }
    }

    /// <summary>
    /// Reads whether the desktop surface covers the cards: Progman has been raised
    /// and no visible, un-minimised window of another program is left above it.
    /// Topmost windows (taskbar, dock), Explorer's own shell surface windows — but
    /// not its file-manager windows — DWM-cloaked windows (suspended UWP hosts
    /// report visible while off screen) and the cards themselves do not count.
    /// </summary>
    private void Evaluate()
    {
        using var _ = UiDiagnostics.Measure("桌面状态评估");
        var progman = NativeMethods.FindWindowW("Progman", null);
        var found = progman != 0;
        var blockers = 0;
        if (found)
        {
            NativeMethods.GetWindowThreadProcessId(progman, out var shellProcess);
            var cards = _cardHandles().ToHashSet();
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
                if (cards.Contains(window))
                {
                    return true;
                }
                if (NativeMethods.IsWindowCloaked(window))
                {
                    return true;
                }
                NativeMethods.GetWindowThreadProcessId(window, out var process);
                if (DesktopSurfaceRules.CountsAsOrdinaryWindow(process == shellProcess, ClassNameOf(window)))
                {
                    blockers++;
                }
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

    private static string ClassNameOf(nint window)
    {
        var className = new StringBuilder(64);
        return DockNativeMethods.GetClassNameW(window, className, className.Capacity) > 0
            ? className.ToString()
            : string.Empty;
    }
}
