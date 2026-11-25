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
            var autoHideOn = TaskbarAdapter.IsWindowsAutoHideOn();
            var visibleTaskbarRects = ReadVisibleTaskbarRects();
            foreach (var monitor in MonitorCatalog.GetAll())
            {
                var matchingTaskbar = FindMatchingBottomTaskbar(visibleTaskbarRects, monitor.Bounds);
                var hasBottomAutoHide = TaskbarEdgeGuard.HasBottomAutoHideBar(monitor.Bounds)
                    || (autoHideOn && matchingTaskbar.HasValue);
                if (!hasBottomAutoHide)
                {
                    continue;
                }

                var stripPixels = TaskbarCornerRules.StripPixels;
                if (matchingTaskbar is { } taskbarRect)
                {
                    var peekPixels = monitor.Bounds.Bottom - taskbarRect.Top;
                    if (peekPixels > TaskbarCornerRules.StripPixels
                        && peekPixels <= TaskbarCornerRules.ExpandedTaskbarThresholdPixels)
                    {
                        stripPixels = TaskbarCornerRules.ResolveStripThickness(peekPixels);
                    }
                    else if (peekPixels > TaskbarCornerRules.ExpandedTaskbarThresholdPixels
                        && _strips.TryGetValue(monitor.DeviceName, out var existingStrip))
                    {
                        // Retain the collapsed thickness while the taskbar is expanded so Update()
                        // does not see a spurious bounds change and re-place the strip above it.
                        stripPixels = TaskbarCornerRules.ResolveStripThickness(existingStrip.Bounds.Height);
                    }
                }

                if (TaskbarCornerRules.StripForMode(monitor.Bounds, monitor.Dpi, _mode, stripPixels) is { } stripBounds)
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
        var raisedAny = false;
        foreach (var (device, bounds) in wanted)
        {
            var isNew = !_strips.TryGetValue(device, out var strip);
            if (isNew)
            {
                strip = new EdgeStrip();
                _strips[device] = strip;
                strip.Show();
            }
            if (!IsTaskbarExpandedOver(bounds))
            {
                strip!.Place(bounds);
                raisedAny = true;
            }
            else
            {
                // Keep expected geometry recorded; RaiseIfBehindTaskbar will place it once the taskbar closes.
                strip!.RecordTargetBounds(bounds);
            }
        }
        if (_strips.Count > 0)
        {
            WatchTaskbar();
            if (!_zOrderTimer.IsEnabled) _zOrderTimer.Start();
            if (raisedAny) NotifyRaised();
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

    private static bool IsTaskbarClass(System.Text.StringBuilder buffer) =>
        EqualsLiteral(buffer, "Shell_TrayWnd") || EqualsLiteral(buffer, "Shell_SecondaryTrayWnd");

    private static bool EqualsLiteral(System.Text.StringBuilder buffer, string literal)
    {
        if (buffer.Length != literal.Length)
        {
            return false;
        }
        for (var i = 0; i < literal.Length; i++)
        {
            if (buffer[i] != literal[i])
            {
                return false;
            }
        }
        return true;
    }

    private static List<PixelRect> ReadVisibleTaskbarRects()
    {
        var rects = new List<PixelRect>(2);
        foreach (var taskbar in TaskbarAdapter.FindTaskbars())
        {
            if (DockNativeMethods.IsWindowVisible(taskbar)
                && DockNativeMethods.GetWindowRect(taskbar, out var rect))
            {
                rects.Add(new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom));
            }
        }
        return rects;
    }

    private static PixelRect? FindMatchingBottomTaskbar(List<PixelRect> taskbarRects, PixelRect monitorBounds)
    {
        foreach (var rect in taskbarRects)
        {
            if (TaskbarCornerRules.IsBottomTaskbarOnMonitor(rect, monitorBounds))
            {
                return rect;
            }
        }
        return null;
    }

    /// <summary>Whether an auto-hidden bottom taskbar is currently expanded on <paramref name="monitorBounds"/>.</summary>
    internal static bool IsTaskbarExpandedOnMonitor(PixelRect monitorBounds)
    {
        var hasAutoHide = TaskbarEdgeGuard.HasBottomAutoHideBar(monitorBounds)
            || (TaskbarAdapter.IsWindowsAutoHideOn()
                && FindMatchingBottomTaskbar(ReadVisibleTaskbarRects(), monitorBounds).HasValue);
        if (!hasAutoHide)
        {
            return false;
        }
        var edgeBand = new PixelRect(
            monitorBounds.Left,
            monitorBounds.Bottom - TaskbarCornerRules.StripPixels,
            monitorBounds.Right,
            monitorBounds.Bottom);
        return IsTaskbarExpandedOver(edgeBand);
    }

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
        if (!IsTaskbarClass(_classNameBuffer)) return;
        // Do not fight the taskbar's Z-order while it is open/expanded; wait until it collapses,
        // but schedule a non-forcing settle check so the final collapse frame is caught promptly.
        if (IsTaskbarWindowExpanded(hwnd))
        {
            ScheduleZOrderCheck();
            return;
        }
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

    /// <summary>Raises strips only if a collapsed taskbar window sits above any strip, a strip was hidden, or its geometry drifted.</summary>
    internal void RaiseIfBehindTaskbar()
    {
        if (_strips.Count == 0 || _raising)
        {
            return;
        }
        var raisedAny = false;
        foreach (var strip in _strips.Values)
        {
            if (NeedsRaiseForStrip(strip, out var needsGeometryRestore))
            {
                if (needsGeometryRestore)
                {
                    strip.Place(strip.Bounds);
                }
                else
                {
                    strip.Raise();
                }
                raisedAny = true;
            }
        }
        if (raisedAny)
        {
            _lastRaise = Environment.TickCount64;
            NotifyRaised();
        }
    }

    private bool NeedsRaiseForStrip(EdgeStrip strip, out bool needsGeometryRestore)
    {
        needsGeometryRestore = false;
        var handle = new WindowInteropHelper(strip).Handle;
        if (handle == 0 || IsTaskbarExpandedOver(strip.Bounds))
        {
            return false;
        }
        // If the strip was first created while the taskbar was expanded, remeasure the collapsed
        // taskbar peek height as soon as it closes instead of waiting for the 5-second Update poll.
        foreach (var taskbarRect in ReadVisibleTaskbarRects())
        {
            if (TaskbarCornerRules.IsSameMonitorBottomTaskbar(taskbarRect, strip.Bounds))
            {
                var peekPixels = strip.Bounds.Bottom - taskbarRect.Top;
                if (peekPixels > strip.Bounds.Height
                    && peekPixels <= TaskbarCornerRules.ExpandedTaskbarThresholdPixels)
                {
                    var thickness = TaskbarCornerRules.ResolveStripThickness(peekPixels);
                    strip.RecordTargetBounds(strip.Bounds with { Top = strip.Bounds.Bottom - thickness });
                    needsGeometryRestore = true;
                    return true;
                }
            }
        }
        if (DockNativeMethods.GetWindowRect(handle, out var currentRect)
            && TaskbarCornerRules.NeedsGeometryRestore(
                new PixelRect(currentRect.Left, currentRect.Top, currentRect.Right, currentRect.Bottom),
                strip.Bounds))
        {
            needsGeometryRestore = true;
            return true;
        }
        if (!NativeMethods.IsWindowVisible(handle) || !NativeMethods.IsTopmostWindow(handle))
        {
            return true;
        }
        var cursor = NativeMethods.GetWindow(handle, NativeMethods.GwHwndPrev);
        var steps = 0;
        while (cursor != 0 && steps++ < 512)
        {
            _classNameBuffer.Clear();
            if (DockNativeMethods.GetClassNameW(cursor, _classNameBuffer, _classNameBuffer.Capacity) > 0
                && IsTaskbarClass(_classNameBuffer)
                && DockNativeMethods.GetWindowRect(cursor, out var rect))
            {
                var taskbarRect = new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
                if (TaskbarCornerRules.IsSameMonitorBottomTaskbar(taskbarRect, strip.Bounds))
                {
                    return !TaskbarCornerRules.IsBottomTaskbarExpanded(taskbarRect, strip.Bounds);
                }
            }
            cursor = NativeMethods.GetWindow(cursor, NativeMethods.GwHwndPrev);
        }
        return false;
    }

    private bool IsTaskbarWindowExpanded(nint hwnd)
    {
        if (!DockNativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }
        var taskbarRect = new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
        foreach (var strip in _strips.Values)
        {
            if (TaskbarCornerRules.IsBottomTaskbarExpanded(taskbarRect, strip.Bounds))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsTaskbarExpandedOver(PixelRect stripBounds)
    {
        foreach (var taskbar in TaskbarAdapter.FindTaskbars())
        {
            if (DockNativeMethods.IsWindowVisible(taskbar)
                && DockNativeMethods.GetWindowRect(taskbar, out var rect)
                && TaskbarCornerRules.IsBottomTaskbarExpanded(
                    new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
                    stripBounds))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Puts the existing strips back above collapsed taskbars without re-reading monitors.</summary>
    internal void Raise()
    {
        if (_strips.Count == 0 || _raising)
        {
            return;
        }
        var raisedAny = false;
        foreach (var strip in _strips.Values)
        {
            if (!IsTaskbarExpandedOver(strip.Bounds))
            {
                strip.Raise();
                raisedAny = true;
            }
        }
        if (raisedAny)
        {
            _lastRaise = Environment.TickCount64;
            NotifyRaised();
        }
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
                HwndSource.FromHwnd(handle)?.AddHook(StripWindowProc);
            };
        }

        private nint StripWindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            // Keep physical pixel geometry intact across DPI transitions (for example when a strip
            // first moves from the off-screen primary-DPI origin onto a secondary monitor with a
            // different DPI); otherwise WPF's default WM_DPICHANGED handler shrinks it to 1x1 DIP.
            if (message == DockNativeMethods.WmDpiChanged && Bounds.HasArea)
            {
                Place(Bounds);
                handled = true;
            }
            return 0;
        }

        internal PixelRect Bounds { get; private set; }

        internal void RecordTargetBounds(PixelRect bounds) => Bounds = bounds;

        internal void Place(PixelRect bounds)
        {
            Bounds = bounds;
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
