using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Taskbar;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace BingLan.App.Taskbar;

/// <summary>
/// Lets an auto-hidden taskbar at the bottom of a monitor appear only when the pointer
/// reaches the left or right end of the edge. A strip two pixels high, nearly invisible
/// but able to take the pointer, covers the middle of the edge so the hidden taskbar
/// beneath it is not reached there. Nothing in Explorer or the Windows settings changes;
/// closing the strips puts the usual behaviour back.
/// </summary>
internal sealed class TaskbarCornerReveal : IDisposable
{
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectLocationChange = 0x800B;
    private const long RaiseThrottleMs = 50;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);
    // With taskbar events hooked the poll is only a fallback and the auto-hide check.
    private static readonly TimeSpan HookedCheckInterval = TimeSpan.FromSeconds(5);
    // Cheap Z-order-only check (walks topmost HWNDs above the strip) catches silent Explorer
    // Z-order bumps (notifications, tray updates, Start menu close animations) within 0.4 s.
    private static readonly TimeSpan ZOrderWatchInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(120);
    private readonly Dictionary<string, EdgeStrip> _strips = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Text.StringBuilder _classNameBuffer = new(32);
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _zOrderTimer;
    private readonly DispatcherTimer _settle;
    private readonly DockNativeMethods.WinEventProc _taskbarEventCallback;
    private nint _showHook;
    private nint _locationHook;
    private nint _foregroundHook;
    private uint _explorerProcessId;
    private long _lastRaise;
    private TaskbarEdgeRevealMode _mode = TaskbarEdgeRevealMode.AnyEdge;
    private bool _suspended;
    private bool _raising;
    private bool _settleForceRaise;

    internal TaskbarCornerReveal()
    {
        // The taskbar raises itself as it slides in and out. Its show and move events put
        // the strips back on top at once; the timer remains as a fallback and notices
        // auto-hide being switched on or off.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = CheckInterval };
        _timer.Tick += (_, _) => Update();
        _zOrderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ZOrderWatchInterval };
        _zOrderTimer.Tick += (_, _) => RaiseIfBehindTaskbar();
        // The slide moves the bar several times, and Explorer may re-raise Shell_TrayWnd
        // slightly after foreground switches or AppBar notifications.
        _settle = new DispatcherTimer(DispatcherPriority.Input) { Interval = SettleDelay };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            var force = _settleForceRaise;
            _settleForceRaise = false;
            if (force)
            {
                Raise();
            }
            else
            {
                RaiseIfBehindTaskbar();
            }
        };
        _taskbarEventCallback = OnTaskbarEvent;
    }

    /// <summary>Turns the behaviour on or off (legacy boolean wrapper).</summary>
    internal void SetEnabled(bool enabled) =>
        SetMode(enabled ? TaskbarEdgeRevealMode.CornersOnly : TaskbarEdgeRevealMode.AnyEdge);

    /// <summary>Sets how pointer contact along the bottom edge may reveal an auto-hidden taskbar.</summary>
    internal void SetMode(TaskbarEdgeRevealMode mode)
    {
        _mode = mode;
        if (mode != TaskbarEdgeRevealMode.AnyEdge)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            _zOrderTimer.Stop();
            _settle.Stop();
            _settleForceRaise = false;
        }
        Update();
    }

    /// <summary>Raised after strips are placed or re-raised to topmost so visible dock surfaces can keep higher Z-order.</summary>
    internal event Action? Raised;

    /// <summary>A truly full-screen app is in front: the strips step aside until it leaves.</summary>
    internal void SetSuspended(bool suspended)
    {
        if (_suspended == suspended)
        {
            return;
        }
        _suspended = suspended;
        Update();
        if (!suspended && _strips.Count > 0)
        {
            Raise();
        }
    }

    internal void Update()
    {
        var wanted = new Dictionary<string, PixelRect>(StringComparer.OrdinalIgnoreCase);
        if (_mode != TaskbarEdgeRevealMode.AnyEdge && !_suspended)
        {
            foreach (var monitor in MonitorCatalog.GetAll())
            {
                if (TaskbarEdgeGuard.HasBottomAutoHideBar(monitor.Bounds)
                    && TaskbarCornerRules.StripForMode(monitor.Bounds, monitor.Dpi, _mode) is { } stripBounds)
                {
                    wanted[monitor.DeviceName] = stripBounds;
                }
            }
        }

        foreach (var stale in _strips.Keys.Except(wanted.Keys).ToList())
        {
            _strips[stale].Close();
            _strips.Remove(stale);
        }
        foreach (var (device, bounds) in wanted)
        {
            if (!_strips.TryGetValue(device, out var strip))
            {
                strip = new EdgeStrip();
                _strips[device] = strip;
                strip.Show();
            }
            strip.Place(bounds);
        }
        if (_strips.Count > 0)
        {
            WatchTaskbar();
            if (!_zOrderTimer.IsEnabled) _zOrderTimer.Start();
            NotifyRaised();
        }
        else
        {
            _zOrderTimer.Stop();
            _settle.Stop();
            _settleForceRaise = false;
            Unhook();
        }
    }

    internal static bool IsTaskbarClass(string className) =>
        className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    private void WatchTaskbar()
    {
        var tray = TaskbarNativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (tray == 0)
        {
            Unhook();
            return;
        }
        // Explorer restarts with a new process; re-hook when it changed.
        DockNativeMethods.GetWindowThreadProcessId(tray, out var processId);
        if (processId == _explorerProcessId && _showHook != 0 && _locationHook != 0 && _foregroundHook != 0) return;
        Unhook();
        _explorerProcessId = processId;
        // Two single-event hooks instead of a range keep unrelated Explorer events out.
        _showHook = DockNativeMethods.SetWinEventHook(EventObjectShow, EventObjectShow, 0,
            _taskbarEventCallback, processId, 0, DockNativeMethods.WineventOutOfContext);
        _locationHook = DockNativeMethods.SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, 0,
            _taskbarEventCallback, processId, 0, DockNativeMethods.WineventOutOfContext);
        // Catch Z-order-only taskbar reassertions (which do not fire show/location events)
        // after Start menu, popups, or window switches.
        _foregroundHook = DockNativeMethods.SetWinEventHook(
            DockNativeMethods.EventSystemForeground,
            DockNativeMethods.EventSystemForeground,
            0,
            _taskbarEventCallback,
            0,
            0,
            DockNativeMethods.WineventOutOfContext);
        _timer.Interval = _showHook != 0 && _locationHook != 0 ? HookedCheckInterval : CheckInterval;
    }

    private void Unhook()
    {
        if (_showHook != 0) DockNativeMethods.UnhookWinEvent(_showHook);
        if (_locationHook != 0) DockNativeMethods.UnhookWinEvent(_locationHook);
        if (_foregroundHook != 0) DockNativeMethods.UnhookWinEvent(_foregroundHook);
        _showHook = 0;
        _locationHook = 0;
        _foregroundHook = 0;
        _explorerProcessId = 0;
        _timer.Interval = CheckInterval;
    }

    private void OnTaskbarEvent(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (_strips.Count == 0 || objectId != DockNativeMethods.ObjidWindow || childId != 0 || hwnd == 0) return;
        if (eventType == DockNativeMethods.EventSystemForeground)
        {
            if (Environment.TickCount64 - _lastRaise >= RaiseThrottleMs) RaiseIfBehindTaskbar();
            // Explorer often re-asserts Shell_TrayWnd Z-order slightly after the foreground switch.
            ScheduleZOrderCheck();
            return;
        }
        _classNameBuffer.Clear();
        DockNativeMethods.GetClassNameW(hwnd, _classNameBuffer, _classNameBuffer.Capacity);
        if (!IsTaskbarClass(_classNameBuffer.ToString())) return;
        if (Environment.TickCount64 - _lastRaise >= RaiseThrottleMs) Raise();
        _settleForceRaise = true;
        _settle.Stop();
        _settle.Start();
    }

    /// <summary>Schedules a short follow-up Z-order check after foreground switches or AppBar Explorer calls.</summary>
    internal void ScheduleZOrderCheck()
    {
        if (_strips.Count == 0)
        {
            return;
        }
        _settle.Stop();
        _settle.Start();
    }

    /// <summary>Raises strips only if a taskbar window sits above any strip or a strip was hidden.</summary>
    internal void RaiseIfBehindTaskbar()
    {
        if (_strips.Count == 0 || _raising || !IsAnyTaskbarAboveStrips())
        {
            return;
        }
        Raise();
    }

    private bool IsAnyTaskbarAboveStrips()
    {
        foreach (var strip in _strips.Values)
        {
            var handle = new WindowInteropHelper(strip).Handle;
            if (handle == 0)
            {
                continue;
            }
            if (!NativeMethods.IsWindowVisible(handle) || !NativeMethods.IsTopmostWindow(handle))
            {
                return true;
            }
            var cursor = NativeMethods.GetWindow(handle, NativeMethods.GwHwndPrev);
            var steps = 0;
            while (cursor != 0 && steps++ < 64)
            {
                _classNameBuffer.Clear();
                if (DockNativeMethods.GetClassNameW(cursor, _classNameBuffer, _classNameBuffer.Capacity) > 0
                    && IsTaskbarClass(_classNameBuffer.ToString()))
                {
                    return true;
                }
                cursor = NativeMethods.GetWindow(cursor, NativeMethods.GwHwndPrev);
            }
        }
        return false;
    }

    /// <summary>Puts the existing strips back above the taskbar without re-reading monitors.</summary>
    internal void Raise()
    {
        if (_strips.Count == 0 || _raising)
        {
            return;
        }
        _lastRaise = Environment.TickCount64;
        foreach (var strip in _strips.Values)
        {
            strip.Raise();
        }
        NotifyRaised();
    }

    private void NotifyRaised()
    {
        if (_raising)
        {
            return;
        }
        _raising = true;
        try
        {
            Raised?.Invoke();
        }
        finally
        {
            _raising = false;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _zOrderTimer.Stop();
        _settle.Stop();
        _settleForceRaise = false;
        Unhook();
        foreach (var strip in _strips.Values)
        {
            strip.Close();
        }
        _strips.Clear();
    }

    private sealed class EdgeStrip : Window
    {
        private static readonly SolidColorBrush NearlyClear = CreateNearlyClear();

        internal EdgeStrip()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            // Fully transparent pixels let the pointer through to the taskbar below, so
            // the strip keeps an alpha of one: invisible, but it takes the pointer.
            Background = NearlyClear;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            Left = -10000;
            Top = -10000;
            Width = 1;
            Height = 1;
            Title = "冰蓝任务栏边缘";
            SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(this).Handle;
                var style = (long)NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle);
                NativeMethods.SetWindowLongPtr(
                    handle,
                    NativeMethods.GwlExStyle,
                    (nint)(style | NativeMethods.WsExToolWindow | DockNativeMethods.WsExNoActivate));
            };
        }

        internal void Place(PixelRect bounds)
        {
            var handle = new WindowInteropHelper(this).Handle;
            var flags = NativeMethods.SwpNoActivate;
            if (!NativeMethods.IsWindowVisible(handle))
            {
                flags |= NativeMethods.SwpShowWindow;
            }
            NativeMethods.SetWindowPos(
                handle,
                DockNativeMethods.HwndTopmost,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                flags);
        }

        internal void Raise()
        {
            var handle = new WindowInteropHelper(this).Handle;
            var flags = NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate;
            if (!NativeMethods.IsWindowVisible(handle))
            {
                flags |= NativeMethods.SwpShowWindow;
            }
            NativeMethods.SetWindowPos(
                handle,
                DockNativeMethods.HwndTopmost,
                0,
                0,
                0,
                0,
                flags);
        }

        private static SolidColorBrush CreateNearlyClear()
        {
            var brush = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));
            brush.Freeze();
            return brush;
        }
    }
}
