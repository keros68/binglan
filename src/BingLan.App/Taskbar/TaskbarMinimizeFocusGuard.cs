using System.Text;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.Core.Dock;
using BingLan.Core.Taskbar;

namespace BingLan.App.Taskbar;

/// <summary>
/// Recovers from Windows 11's passive foreground fallback to <c>Shell_TrayWnd</c> right
/// after minimizing the last visible normal window, which otherwise expands and strands an
/// auto-hidden taskbar over an empty desktop while suppressing the dock. Only acts within
/// a narrow post-minimize window when the pointer is away from the taskbar, no modifier or
/// mouse button is held, no system panel is open, and no app is flashing for attention.
/// </summary>
internal sealed class TaskbarMinimizeFocusGuard : IDisposable
{
    private const int ObjidWindow = 0;
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(60);

    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _hasAttentionFlash;
    private readonly BackgroundWinEventPump _pump;
    private readonly List<BackgroundWinEventPump.WinEventSubscription> _subscriptions = [];
    private readonly DispatcherTimer _settleTimer;
    private long _lastMinimizeTick = long.MinValue;
    private bool _restoredForCurrentMinimize;
    private int _disposed;

    internal TaskbarMinimizeFocusGuard(
        Dispatcher dispatcher,
        Func<bool> hasAttentionFlash,
        BackgroundWinEventPump? pump = null)
    {
        _dispatcher = dispatcher;
        _hasAttentionFlash = hasAttentionFlash;
        _pump = pump ?? BackgroundWinEventPump.Shared;
        _settleTimer = new DispatcherTimer(
            SettleDelay,
            DispatcherPriority.Input,
            (_, _) =>
            {
                _settleTimer!.Stop();
                if (Volatile.Read(ref _disposed) == 0)
                {
                    TryRecoverPassiveTaskbarFocus();
                }
            },
            dispatcher);
        _settleTimer.Stop();
    }

    internal void Start()
    {
        if (Volatile.Read(ref _disposed) != 0 || _subscriptions.Count > 0)
        {
            return;
        }

        Subscribe(
            DockNativeMethods.EventSystemMinimizeStart,
            DockNativeMethods.EventSystemMinimizeEnd);
        Subscribe(
            DockNativeMethods.EventSystemForeground,
            DockNativeMethods.EventSystemForeground);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _settleTimer.Stop();
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
        _subscriptions.Clear();
    }

    private void Subscribe(uint eventMin, uint eventMax)
    {
        var subscription = _pump.Subscribe(
            eventMin,
            eventMax,
            static e => e.ChildId == 0 && e.ObjectId == ObjidWindow && e.Window != 0,
            OnBackgroundEvent);
        if (subscription.IsActive)
        {
            _subscriptions.Add(subscription);
        }
        else
        {
            subscription.Dispose();
        }
    }

    private void OnBackgroundEvent(WinEventMessage message)
    {
        if (Volatile.Read(ref _disposed) != 0 || _dispatcher.HasShutdownStarted)
        {
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (message.EventType is DockNativeMethods.EventSystemMinimizeStart
                or DockNativeMethods.EventSystemMinimizeEnd)
            {
                OnWindowMinimized(message.Window);
                return;
            }

            if (message.EventType == DockNativeMethods.EventSystemForeground)
            {
                TryRecoverPassiveTaskbarFocus();
            }
        });
    }

    private void OnWindowMinimized(nint window)
    {
        var className = ClassNameOf(window);
        var isShellOrTaskbar = ShellSurfaceWindows.IsShellSurface(className)
            || TaskbarCornerReveal.IsTaskbarClass(className);
        var pointerInTaskbar = IsPointerInAnyTaskbar();
        if (!TaskbarPassiveFocusRules.ShouldRecordMinimize(isShellOrTaskbar, pointerInTaskbar))
        {
            return;
        }

        _lastMinimizeTick = Environment.TickCount64;
        _restoredForCurrentMinimize = false;
        _settleTimer.Stop();
        _settleTimer.Start();
    }

    private void TryRecoverPassiveTaskbarFocus()
    {
        if (_lastMinimizeTick == long.MinValue || _restoredForCurrentMinimize)
        {
            return;
        }

        var foreground = DockNativeMethods.GetForegroundWindow();
        if (foreground == 0 || !TaskbarCornerReveal.IsTaskbarClass(ClassNameOf(foreground)))
        {
            return;
        }

        var elapsedMs = Environment.TickCount64 - _lastMinimizeTick;
        var autoHideOn = TaskbarAdapter.IsWindowsAutoHideOn();
        if (!autoHideOn)
        {
            return;
        }

        DockNativeMethods.GetCursorPos(out var cursor);
        PixelRect taskbarBounds = default;
        if (DockNativeMethods.GetWindowRect(foreground, out var rawRect))
        {
            taskbarBounds = new PixelRect(rawRect.Left, rawRect.Top, rawRect.Right, rawRect.Bottom);
        }

        var monitorBounds = ResolveMonitorBounds(foreground);
        var taskbarEdge = TaskbarEdgeGuard.InferNativeTaskbarEdge(monitorBounds, taskbarBounds)
            ?? DockEdge.Bottom;
        var pointerNear = TaskbarPassiveFocusRules.IsPointerNearTaskbarOrEdge(
            cursor.X,
            cursor.Y,
            taskbarBounds,
            monitorBounds,
            taskbarEdge);
        var systemPopupOpen = ShellSurfaceNativeMethods.IsStartMenuForeground()
            || ShellSurfaceNativeMethods.IsQuickSettingsVisible();
        var input = new TaskbarPassiveFocusInput(
            IsTaskbarForeground: true,
            AutoHideEnabled: autoHideOn,
            ElapsedSinceMinimizeMs: elapsedMs,
            AlreadyRestoredForMinimize: _restoredForCurrentMinimize,
            PointerInTaskbarOrEdge: pointerNear,
            AnyModifierHeld: ShellSurfaceNativeMethods.HasHeldModifier(),
            AnyMouseButtonDown: ShellSurfaceNativeMethods.IsAnyMouseButtonPressed(),
            HasAttentionFlash: _hasAttentionFlash(),
            SystemPopupOpen: systemPopupOpen);
        if (!TaskbarPassiveFocusRules.ShouldRestoreDesktopFocus(input))
        {
            return;
        }

        _restoredForCurrentMinimize = true;
        TryReturnFocusToDesktop(foreground);
    }

    private static void TryReturnFocusToDesktop(nint taskbarWindow)
    {
        if (ShellSurfaceNativeMethods.IsWindowElevated(taskbarWindow)
            || !IsWindowResponsive(taskbarWindow))
        {
            return;
        }

        var shellWindow = DockNativeMethods.GetShellWindow();
        if (shellWindow == 0)
        {
            shellWindow = NativeMethods.FindWindowW("Progman", null);
        }
        if (shellWindow == 0 || shellWindow == taskbarWindow || !IsWindowResponsive(shellWindow))
        {
            return;
        }

        if (DockNativeMethods.SetForegroundWindow(shellWindow)
            && DockNativeMethods.GetForegroundWindow() != taskbarWindow)
        {
            return;
        }

        var taskbarThread = DockNativeMethods.GetWindowThreadProcessId(taskbarWindow, out _);
        var currentThread = DockNativeMethods.GetCurrentThreadId();
        var attached = false;
        try
        {
            if (taskbarThread != 0 && taskbarThread != currentThread)
            {
                attached = DockNativeMethods.AttachThreadInput(currentThread, taskbarThread, true);
                if (!attached)
                {
                    return;
                }
            }
            _ = DockNativeMethods.SetForegroundWindow(shellWindow);
        }
        finally
        {
            if (attached)
            {
                DockNativeMethods.AttachThreadInput(currentThread, taskbarThread, false);
            }
        }
    }

    private static bool IsPointerInAnyTaskbar()
    {
        DockNativeMethods.GetCursorPos(out var cursor);
        foreach (var taskbar in TaskbarAdapter.FindTaskbars())
        {
            if (DockNativeMethods.IsWindowVisible(taskbar)
                && DockNativeMethods.GetWindowRect(taskbar, out var rect)
                && cursor.X >= rect.Left && cursor.X < rect.Right
                && cursor.Y >= rect.Top && cursor.Y < rect.Bottom)
            {
                return true;
            }
        }
        return false;
    }

    private static PixelRect ResolveMonitorBounds(nint window)
    {
        var handle = DockNativeMethods.MonitorFromWindow(window, DockNativeMethods.MonitorDefaultToNearest);
        foreach (var monitor in MonitorCatalog.GetAll())
        {
            if (monitor.MonitorHandle == handle)
            {
                return monitor.Bounds;
            }
        }
        return default;
    }

    private static bool IsWindowResponsive(nint window) =>
        window != 0
        && DockNativeMethods.SendMessageTimeoutW(
            window,
            DockNativeMethods.WmNull,
            0,
            0,
            DockNativeMethods.SmtoAbortIfHung,
            50,
            out _) != 0;

    private static string ClassNameOf(nint window)
    {
        var buffer = new StringBuilder(64);
        return DockNativeMethods.GetClassNameW(window, buffer, buffer.Capacity) > 0
            ? buffer.ToString()
            : string.Empty;
    }
}
