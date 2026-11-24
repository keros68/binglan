using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.Core.Dock;
using BingLan.Core.TopBar;

namespace BingLan.App.TopBar;

/// <summary>
/// Places a full-width strip at the top edge of a monitor and optionally reserves the
/// work area through the documented AppBar API, mirroring what the dock's bottom
/// controller does. Registration is released on dispose and replayed after Explorer
/// recreates the taskbar.
/// </summary>
internal sealed class TopBarAppBarController : IDisposable
{
    private const DockEdge Edge = DockEdge.Top;
    private const int ExplorerRecoveryAttemptCount = 5;
    private const int MaxTransientReserveFailures = 3;
    private static readonly TimeSpan ExplorerRecoveryDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan TransientReserveRetryDelay = TimeSpan.FromMilliseconds(400);
    private readonly Window _window;
    private readonly nint _handle;
    private readonly HwndSource _source;
    private readonly uint _callbackMessage;
    private readonly uint _taskbarCreatedMessage;
    private readonly AppBarReservationState _reservation = new();
    private readonly AppBarPositionGuard _positionGuard = new();
    private readonly AppBarWorkAreaCheck _workAreaCheck = new();
    private readonly DispatcherTimer _deferredPosition;
    private readonly DispatcherTimer _reserveRetryTimer;
    private PixelRect _reservedStrip;
    private MonitorSnapshot? _monitor;
    private double _heightDip;
    private double? _compactWidthDip;
    private bool _reserveWorkArea;
    private bool _fullScreenDetected;
    private bool _positioning;
    private bool _updatingLayer;
    private bool _refreshNativeTaskbarLayout;
    private bool _transientRecovering;
    private bool _reserveFallbackLatched;
    private int _consecutiveReserveFailures;
    private bool _disposed;
    private bool _setPositionApplied;
    private int _recoveryGeneration;

    internal TopBarAppBarController(Window window, double heightDip)
    {
        _window = window;
        _heightDip = heightDip;
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle)
            ?? throw new InvalidOperationException("无法取得顶端信息条窗口消息源。");
        _callbackMessage = DockNativeMethods.RegisterWindowMessageW(
            $"BingLan.TopBar.AppBar.{Environment.ProcessId}");
        _taskbarCreatedMessage = DockNativeMethods.RegisterWindowMessageW("TaskbarCreated");
        _deferredPosition = new DispatcherTimer(
            AppBarPositionGuard.DeferredDelay,
            DispatcherPriority.Background,
            (_, _) =>
            {
                _deferredPosition!.Stop();
                if (!_disposed)
                {
                    if (_refreshNativeTaskbarLayout)
                    {
                        _refreshNativeTaskbarLayout = false;
                        InvalidateNativeTaskbarLayout();
                    }
                    Position();
                }
            },
            window.Dispatcher);
        _deferredPosition.Stop();
        _reserveRetryTimer = new DispatcherTimer(
            TransientReserveRetryDelay,
            DispatcherPriority.Background,
            (_, _) =>
            {
                _reserveRetryTimer!.Stop();
                if (!_disposed && _reserveWorkArea && !_reserveFallbackLatched)
                {
                    if (!_reservation.IsRegistered)
                    {
                        Dispatch(_reservation.Apply(true));
                    }
                    Position();
                }
            },
            window.Dispatcher);
        _reserveRetryTimer.Stop();
        _source.AddHook(WindowProc);
    }

    internal event Action? EnvironmentChanged;

    internal event Action? ExplorerRestarted;

    internal event Action? ExplorerNotified;

    internal event Action? ReservationFallbackTriggered;

    internal PixelRect Bounds { get; private set; }

    internal bool IsReserved => _reservation.IsRegistered;

    internal bool IsFullScreenDetected => _fullScreenDetected;

    internal MonitorSnapshot? Monitor => _monitor;

    internal void EnsureLayer() => UpdateWindowLayer();

    internal void SetFullScreenDetected(bool detected)
    {
        if (_disposed || _fullScreenDetected == detected)
        {
            return;
        }

        _fullScreenDetected = detected;
        UpdateWindowLayer();
    }

    internal void Apply(MonitorSnapshot monitor, bool reserveWorkArea, double heightDip, double? compactWidthDip = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var monitorChanged = _monitor is not null
            && AppBarReservationState.HasMonitorTargetChanged(
                _monitor.DeviceName,
                _monitor.Bounds,
                _monitor.Dpi,
                monitor.DeviceName,
                monitor.Bounds,
                monitor.Dpi);
        if (_monitor?.Bounds != monitor.Bounds || _monitor?.WorkingArea != monitor.WorkingArea)
        {
            TaskbarEdgeGuard.InvalidateNativeTaskbarLayout(monitor.Bounds);
        }
        var clampedHeightDip = Math.Max(1d, heightDip);
        var heightChanged = Math.Abs(_heightDip - clampedHeightDip) > 0.01d;
        _monitor = monitor;
        _reserveWorkArea = reserveWorkArea;
        _heightDip = clampedHeightDip;
        _compactWidthDip = compactWidthDip;

        if (!reserveWorkArea || monitorChanged || heightChanged)
        {
            _consecutiveReserveFailures = 0;
            _transientRecovering = false;
            _reserveFallbackLatched = false;
            _reserveRetryTimer.Stop();
            _reservedStrip = default;
            _setPositionApplied = false;
        }

        var nativeTopTaskbar = TaskbarEdgeGuard.GetNativeTaskbarLayout(monitor.Bounds)?.Edge == DockEdge.Top;
        Dispatch(_reservation.Apply(reserveWorkArea && !nativeTopTaskbar && !_reserveFallbackLatched, monitorChanged));
        Position();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _recoveryGeneration++;
        _transientRecovering = false;
        _reserveFallbackLatched = false;
        _consecutiveReserveFailures = 0;
        _deferredPosition.Stop();
        _reserveRetryTimer.Stop();
        Dispatch(_reservation.Release());
        _source.RemoveHook(WindowProc);
        _disposed = true;
    }

    private void Dispatch(IReadOnlyList<AppBarMessage> messages)
    {
        foreach (var message in messages)
        {
            _positionGuard.Reset();
            _workAreaCheck.Reset();
            var data = CreateData();
            if (message == AppBarMessage.Register)
            {
                data.CallbackMessage = _callbackMessage;
                if (DockNativeMethods.SHAppBarMessage(DockNativeMethods.AbmNew, ref data) == 0)
                {
                    _reservation.MarkRegisterFailed();
                }
                else
                {
                    // ABM_NEW registers the bar but reserves nothing; the shell only
                    // learns the rectangle from the next ABM_SETPOS, so the first
                    // Position after every registration must send one even when the
                    // rect matches what a previous registration used.
                    _setPositionApplied = false;
                }
            }
            else
            {
                DockNativeMethods.SHAppBarMessage(DockNativeMethods.AbmRemove, ref data);
            }
        }
    }

    private void Position()
    {
        if (_monitor is null || _positioning)
        {
            return;
        }

        _positioning = true;
        try
        {
            // The native taskbar may move to or away from the top edge after Apply.
            // Yield the reservation while a native top taskbar is present, while keeping
            // _reserveWorkArea intact so the reservation restores once it leaves.
            var nativeTopTaskbar = TaskbarEdgeGuard.GetNativeTaskbarLayout(_monitor.Bounds)?.Edge == DockEdge.Top;
            if (nativeTopTaskbar)
            {
                if (_reservation.IsRegistered)
                {
                    Dispatch(_reservation.Release());
                }
            }
            else if (_reserveWorkArea && !_reservation.IsRegistered && !_reserveFallbackLatched)
            {
                Dispatch(_reservation.Apply(true));
            }
            var scale = _monitor.Dpi / 96d;
            var thicknessPixels = Math.Max(1, (int)Math.Round(_heightDip * scale));
            // The reservation is one pixel shorter than the bar: a maximized window
            // tucks its top edge under the bar's last row, so the two meet without a
            // visible seam.
            var reservePixels = Math.Max(1, thicknessPixels - 1);

            PixelRect strip;
            if (_reservation.IsRegistered && TryReserveStrip(_monitor, reservePixels) is { } reserved)
            {
                _consecutiveReserveFailures = 0;
                _transientRecovering = false;
                _reserveRetryTimer.Stop();
                strip = reserved;
            }
            else if (_reserveWorkArea
                && !nativeTopTaskbar
                && !_reserveFallbackLatched
                && _consecutiveReserveFailures < MaxTransientReserveFailures)
            {
                _consecutiveReserveFailures++;
                _transientRecovering = true;
                strip = AppBarReservationState.IsValidStrip(_reservedStrip, _monitor.Bounds, reservePixels, Edge)
                    ? _reservedStrip
                    : DockGeometry.Calculate(_monitor.Bounds, Edge, Math.Min(reservePixels, _monitor.Bounds.Height));
                if (!_reserveRetryTimer.IsEnabled)
                {
                    _reserveRetryTimer.Start();
                }
            }
            else
            {
                var fellBackFromReserve = _reserveWorkArea && !nativeTopTaskbar
                    && (_reservation.IsRegistered || _transientRecovering);
                _consecutiveReserveFailures = 0;
                _transientRecovering = false;
                _reserveRetryTimer.Stop();
                _reservedStrip = default;
                _setPositionApplied = false;
                if (_reservation.IsRegistered)
                {
                    Dispatch(_reservation.Release());
                }
                if (fellBackFromReserve)
                {
                    _reserveFallbackLatched = true;
                    StartupLog.Write("顶部信息条未能确认顶边工作区预留，已临时切换为智能隐藏");
                    _window.Dispatcher.BeginInvoke(
                        DispatcherPriority.Background,
                        () => ReservationFallbackTriggered?.Invoke());
                }

                var workArea = TaskbarEdgeGuard.AvoidNativeTaskbar(
                    DockAppBarController.GetLiveWorkingArea(_monitor), _monitor.Bounds);
                strip = DockGeometry.Calculate(
                    workArea,
                    Edge,
                    Math.Min(reservePixels, workArea.Height));
                if (_compactWidthDip is { } compactWidth)
                {
                    var width = Math.Clamp((int)Math.Round(compactWidth * scale), 1, workArea.Width);
                    var top = Math.Min(workArea.Top + (int)Math.Round(6d * scale),
                        Math.Max(workArea.Top, workArea.Bottom - thicknessPixels));
                    var left = workArea.Left + (workArea.Width - width) / 2;
                    strip = new PixelRect(left, top, left + width, Math.Min(top + thicknessPixels, workArea.Bottom));
                }
            }

            Bounds = strip;
            var windowRect = strip with
            {
                Bottom = Math.Min(strip.Top + thicknessPixels, _monitor.Bounds.Bottom)
            };
            if (!DockNativeMethods.GetWindowRect(_handle, out var actualRect)
                || ToPixel(actualRect) != windowRect)
            {
                DockNativeMethods.MoveWindow(
                    _handle,
                    windowRect.Left,
                    windowRect.Top,
                    windowRect.Width,
                    windowRect.Height,
                    true);
            }
            UpdateWindowLayer();
        }
        finally
        {
            _positioning = false;
        }
    }

    private PixelRect? TryReserveStrip(MonitorSnapshot monitor, int reservePixels)
    {
        var requestedThickness = Math.Min(reservePixels, monitor.Bounds.Height);
        var requested = DockGeometry.Calculate(monitor.Bounds, Edge, requestedThickness);
        var data = CreateData();
        data.Edge = (uint)Edge;
        data.Rectangle = ToNative(requested);
        if (DockNativeMethods.SHAppBarMessage(DockNativeMethods.AbmQueryPos, ref data) == 0)
        {
            _reservation.ResetRegistration();
            Dispatch(_reservation.Apply(true));
            if (!_reservation.IsRegistered)
            {
                return null;
            }

            data = CreateData();
            data.Edge = (uint)Edge;
            data.Rectangle = ToNative(requested);
            if (DockNativeMethods.SHAppBarMessage(DockNativeMethods.AbmQueryPos, ref data) == 0)
            {
                return null;
            }
        }

        // Keep the strip at the top of whatever the system allows: other bars at
        // the same edge push it down rather than off the monitor.
        var adjusted = ToPixel(data.Rectangle) with
        {
            Bottom = data.Rectangle.Top + requestedThickness
        };
        if (!AppBarReservationState.IsValidStrip(adjusted, monitor.Bounds, requestedThickness, Edge))
        {
            return null;
        }

        // ABN_POSCHANGED arrives for every bar's move, and every ABM_SETPOS rebroadcasts
        // a work-area change system-wide. Skip redundant SETPOS when the strip is unchanged
        // and the live work area already reflects it, but resubmit once if another app or
        // display transition reset the work area.
        var liveWorkArea = DockAppBarController.GetLiveWorkingArea(monitor);
        if (!_setPositionApplied
            || adjusted != _reservedStrip
            || _workAreaCheck.NeedsResubmit(liveWorkArea.Top, adjusted.Bottom))
        {
            data.Rectangle = ToNative(adjusted);
            if (DockNativeMethods.SHAppBarMessage(DockNativeMethods.AbmSetPos, ref data) == 0)
            {
                _setPositionApplied = false;
                return null;
            }

            var actual = ToPixel(data.Rectangle);
            if (!AppBarReservationState.IsValidStrip(actual, monitor.Bounds, requestedThickness, Edge))
            {
                _setPositionApplied = false;
                return null;
            }

            liveWorkArea = DockAppBarController.GetLiveWorkingArea(monitor);
            if (!TopBarReserveRules.IsWorkAreaReserved(liveWorkArea, actual))
            {
                _setPositionApplied = false;
                return null;
            }

            _setPositionApplied = true;
            _reservedStrip = actual;
            ExplorerNotified?.Invoke();
        }
        else if (!TopBarReserveRules.IsWorkAreaReserved(liveWorkArea, _reservedStrip))
        {
            return null;
        }

        return AppBarReservationState.IsValidStrip(_reservedStrip, monitor.Bounds, requestedThickness, Edge)
            ? _reservedStrip
            : null;
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_disposed)
        {
            return 0;
        }

        if ((uint)message == _taskbarCreatedMessage)
        {
            InvalidateNativeTaskbarLayout();
            _transientRecovering = false;
            _reserveFallbackLatched = false;
            _consecutiveReserveFailures = 0;
            _reserveRetryTimer.Stop();
            _reservedStrip = default;
            _setPositionApplied = false;
            _reservation.ResetRegistration();
            _positionGuard.Reset();
            _workAreaCheck.Reset();
            var recoveryGeneration = ++_recoveryGeneration;
            _window.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                () => _ = RecoverAfterExplorerRestartAsync(recoveryGeneration));
            return 0;
        }

        if ((uint)message == _callbackMessage)
        {
            switch ((int)wParam)
            {
                case DockNativeMethods.AbnPosChanged:
                    if (IsMonitorSnapshotStale())
                    {
                        _window.Dispatcher.BeginInvoke(
                            DispatcherPriority.Background,
                            NotifyEnvironmentChanged);
                    }
                    else if (_positionGuard.TryHandleNow(Environment.TickCount64))
                    {
                        Position();
                    }
                    else if (!_deferredPosition.IsEnabled)
                    {
                        _deferredPosition.Start();
                    }
                    break;
            }
            return 0;
        }

        switch (message)
        {
            case DockNativeMethods.WmActivate when _reservation.IsRegistered:
                var activationState = (ushort)(wParam.ToInt64() & 0xFFFF);
                NotifyAppBar(
                    DockNativeMethods.AbmActivate,
                    activationState != DockNativeMethods.WaInactive ? 1 : 0);
                break;
            case DockNativeMethods.WmWindowPosChanged
                when AppBarPositionGuard.ShouldNotifyWindowPosChanged(
                    _reservation.IsRegistered,
                    _positioning,
                    _updatingLayer,
                    IsZOrderOnlyWindowPosChanged(lParam)):
                NotifyAppBar(DockNativeMethods.AbmWindowPosChanged, 0);
                break;
            // A work-area change can leave Explorer holding a stale strip for the bar,
            // for example after moving to a monitor with another scale.
            case NativeMethods.WmSettingChange
                when wParam == NativeMethods.SpiSetWorkArea && !_reservation.IsRegistered:
                _window.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    NotifyEnvironmentChanged);
                break;
            case NativeMethods.WmSettingChange
                when wParam == NativeMethods.SpiSetWorkArea && _reservation.IsRegistered:
                _refreshNativeTaskbarLayout = true;
                if (!_deferredPosition.IsEnabled)
                {
                    _deferredPosition.Start();
                }
                break;
            case DockNativeMethods.WmDisplayChange:
            case DockNativeMethods.WmDpiChanged:
                _reserveFallbackLatched = false;
                _window.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    NotifyEnvironmentChanged);
                break;
        }

        return 0;
    }

    private bool IsMonitorSnapshotStale()
    {
        if (_monitor is null)
        {
            return false;
        }

        foreach (var candidate in MonitorCatalog.GetAll())
        {
            if (string.Equals(candidate.DeviceName, _monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                return candidate.Bounds != _monitor.Bounds || candidate.Dpi != _monitor.Dpi;
            }
        }

        return true;
    }

    private async Task RecoverAfterExplorerRestartAsync(int recoveryGeneration)
    {
        for (var attempt = 1; attempt <= ExplorerRecoveryAttemptCount; attempt++)
        {
            if (_disposed || recoveryGeneration != _recoveryGeneration)
            {
                return;
            }

            if (!_reserveWorkArea
                || (_monitor is not null
                    && TaskbarEdgeGuard.GetNativeTaskbarLayout(_monitor.Bounds)?.Edge == DockEdge.Top))
            {
                break;
            }

            Dispatch(_reservation.Apply(true));
            if (_reservation.IsRegistered)
            {
                break;
            }

            if (attempt < ExplorerRecoveryAttemptCount)
            {
                await Task.Delay(ExplorerRecoveryDelay);
            }
        }

        if (_disposed || recoveryGeneration != _recoveryGeneration)
        {
            return;
        }

        InvalidateNativeTaskbarLayout();
        Position();
        ExplorerRestarted?.Invoke();
    }

    private void InvalidateNativeTaskbarLayout()
    {
        if (_monitor is not null)
        {
            TaskbarEdgeGuard.InvalidateNativeTaskbarLayout(_monitor.Bounds);
        }
    }

    private void NotifyEnvironmentChanged()
    {
        if (_disposed) return;
        InvalidateNativeTaskbarLayout();
        EnvironmentChanged?.Invoke();
    }

    private void NotifyAppBar(uint message, nint parameter)
    {
        var data = CreateData();
        data.Parameter = parameter;
        DockNativeMethods.SHAppBarMessage(message, ref data);
        ExplorerNotified?.Invoke();
    }

    private static bool IsZOrderOnlyWindowPosChanged(nint lParam)
    {
        if (lParam == 0)
        {
            return false;
        }
        return AppBarPositionGuard.IsZOrderOnlyWindowPosFlags(
            Marshal.PtrToStructure<NativeMethods.WindowPos>(lParam).Flags);
    }

    // A reserved bar behaves like the taskbar: above normal windows, below a full-screen
    // app. A smart-hide bar is only on top while it is shown.
    private void UpdateWindowLayer()
    {
        var insertAfter = _reservation.IsRegistered && _fullScreenDetected
            ? DockNativeMethods.HwndBottom
            : DockNativeMethods.HwndTopmost;
        var previous = _updatingLayer;
        _updatingLayer = true;
        try
        {
            NativeMethods.SetWindowPos(
                _handle,
                insertAfter,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
        }
        finally
        {
            _updatingLayer = previous;
        }
    }

    private DockNativeMethods.AppBarData CreateData() => new()
    {
        Size = (uint)Marshal.SizeOf<DockNativeMethods.AppBarData>(),
        Window = _handle
    };

    private static DockNativeMethods.NativeRect ToNative(PixelRect rectangle) => new()
    {
        Left = rectangle.Left,
        Top = rectangle.Top,
        Right = rectangle.Right,
        Bottom = rectangle.Bottom
    };

    private static PixelRect ToPixel(DockNativeMethods.NativeRect rectangle) =>
        new(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
}
