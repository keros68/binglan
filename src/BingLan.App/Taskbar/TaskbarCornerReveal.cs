using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.Core.Dock;
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
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(80);
    private readonly Dictionary<string, EdgeStrip> _strips = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _settle;
    private readonly DockNativeMethods.WinEventProc _taskbarEventCallback;
    private nint _showHook;
    private nint _locationHook;
    private uint _explorerProcessId;
    private long _lastRaise;
    private bool _enabled;
    private bool _suspended;

    internal TaskbarCornerReveal()
    {
        // The taskbar raises itself as it slides in and out. Its show and move events put
        // the strips back on top at once; the timer remains as a fallback and notices
        // auto-hide being switched on or off.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = CheckInterval };
        _timer.Tick += (_, _) => Update();
        // The slide moves the bar several times; raise once more after the last move.
        _settle = new DispatcherTimer(DispatcherPriority.Input) { Interval = SettleDelay };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Raise();
        };
        _taskbarEventCallback = OnTaskbarEvent;
    }

    /// <summary>Turns the behaviour on or off.</summary>
    internal void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (enabled)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            _settle.Stop();
        }
        Update();
    }

    /// <summary>A full-screen app is in front: the strips step aside until it leaves.</summary>
    internal void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        Update();
    }

    internal void Update()
    {
        var wanted = new Dictionary<string, PixelRect>(StringComparer.OrdinalIgnoreCase);
        if (_enabled && !_suspended)
        {
            foreach (var monitor in MonitorCatalog.GetAll())
            {
                if (TaskbarEdgeGuard.HasBottomAutoHideBar(monitor.Bounds))
                {
                    wanted[monitor.DeviceName] = TaskbarCornerRules.MiddleStrip(monitor.Bounds, monitor.Dpi);
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
        if (_strips.Count > 0) WatchTaskbar();
        else Unhook();
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
        if (processId == _explorerProcessId && _showHook != 0 && _locationHook != 0) return;
        Unhook();
        _explorerProcessId = processId;
        // Two single-event hooks instead of a range keep unrelated Explorer events out.
        _showHook = DockNativeMethods.SetWinEventHook(EventObjectShow, EventObjectShow, 0,
            _taskbarEventCallback, processId, 0, DockNativeMethods.WineventOutOfContext);
        _locationHook = DockNativeMethods.SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange, 0,
            _taskbarEventCallback, processId, 0, DockNativeMethods.WineventOutOfContext);
        _timer.Interval = _showHook != 0 && _locationHook != 0 ? HookedCheckInterval : CheckInterval;
    }

    private void Unhook()
    {
        if (_showHook != 0) DockNativeMethods.UnhookWinEvent(_showHook);
        if (_locationHook != 0) DockNativeMethods.UnhookWinEvent(_locationHook);
        _showHook = 0;
        _locationHook = 0;
        _explorerProcessId = 0;
        _timer.Interval = CheckInterval;
    }

    private void OnTaskbarEvent(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (_strips.Count == 0 || objectId != DockNativeMethods.ObjidWindow || childId != 0 || hwnd == 0) return;
        var className = new System.Text.StringBuilder(32);
        DockNativeMethods.GetClassNameW(hwnd, className, className.Capacity);
        if (!IsTaskbarClass(className.ToString())) return;
        if (Environment.TickCount64 - _lastRaise >= RaiseThrottleMs) Raise();
        _settle.Stop();
        _settle.Start();
    }

    /// <summary>Puts the existing strips back above the taskbar without re-reading monitors.</summary>
    private void Raise()
    {
        _lastRaise = Environment.TickCount64;
        foreach (var strip in _strips.Values)
        {
            strip.Raise();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _settle.Stop();
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

        internal void Place(PixelRect bounds) =>
            NativeMethods.SetWindowPos(
                new WindowInteropHelper(this).Handle,
                DockNativeMethods.HwndTopmost,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                NativeMethods.SwpNoActivate);

        internal void Raise() =>
            NativeMethods.SetWindowPos(
                new WindowInteropHelper(this).Handle,
                DockNativeMethods.HwndTopmost,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);

        private static SolidColorBrush CreateNearlyClear()
        {
            var brush = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));
            brush.Freeze();
            return brush;
        }
    }
}
