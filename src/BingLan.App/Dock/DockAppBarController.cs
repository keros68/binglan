using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.Core.Dock;

namespace BingLan.App.Dock;

/// <summary>
/// Positions the dock at the bottom centre of a monitor and optionally reserves the
/// work area through the documented AppBar API. Registration is released on dispose
/// and replayed after Explorer recreates the taskbar.
/// </summary>
internal sealed class DockAppBarController : IDisposable
{
    private const DockEdge Edge = DockEdge.Bottom;
    private const int ExplorerRecoveryAttemptCount = 5;
    private static readonly TimeSpan ExplorerRecoveryDelay = TimeSpan.FromMilliseconds(400);
    private readonly Window _window;
    private readonly nint _handle;
    private readonly HwndSource _source;
    private readonly uint _callbackMessage;
    private readonly uint _taskbarCreatedMessage;
    private readonly AppBarReservationState _reservation = new();
    private readonly AppBarPositionGuard _positionGuard = new();
    private readonly AppBarWorkAreaCheck _workAreaCheck = new();
    private readonly DispatcherTimer _deferredPosition;
    private PixelRect _reservedStrip;
    private MonitorSnapshot? _monitor;
    private bool _reserveWorkArea;
    private double _contentLengthDip = DockLayoutMetrics.EmptyLengthDip;
    private double _thicknessDip = DockLayoutMetrics.DockThicknessDip;
    private double _edgeGapDip = DockLayoutMetrics.EdgeGapDip;
    private PixelRect? _floatingOverride;
    private bool _fullScreenDetected;
    private bool _positioning;
    private bool _applying;
    private bool _disposed;
    private int _recoveryGeneration;

    internal DockAppBarController(Window window)
    {
        _window = window;
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle)
            ?? throw new InvalidOperationException("无法取得 Dock 窗口消息源。");
        _callbackMessage = DockNativeMethods.RegisterWindowMessageW(
            $"BingLan.Dock.AppBar.{Environment.ProcessId}");
        _taskbarCreatedMessage = DockNativeMethods.RegisterWindowMessageW("TaskbarCreated");
        _deferredPosition = new DispatcherTimer(
            AppBarPositionGuard.DeferredDelay,
            DispatcherPriority.Background,
            (_, _) =>
            {
                _deferredPosition!.Stop();
                if (!_disposed)
                {
                    Position();
                }
            },
            window.Dispatcher);
        _deferredPosition.Stop();
        _source.AddHook(WindowProc);
    }

    internal event Action? EnvironmentChanged;

    internal event Action? ExplorerRestarted;

    internal PixelRect Bounds { get; private set; }

    internal bool IsReserved => _reservation.IsRegistered;

    internal bool IsRecovering { get; private set; }

    internal MonitorSnapshot? Monitor => _monitor;

    /// <summary>
    /// While the dock is expanded beside its handle, this overrides where Position
    /// places the window, so every repositioning path (AppBar notifications, monitor
    /// changes, Explorer restarts) keeps the dock beside the handle instead of pulling
    /// it back to the strip. Null restores the normal strip placement.
    /// </summary>
    internal void SetFloatingOverride(PixelRect? rect)
    {
        if (_disposed || _floatingOverride == rect)
        {
            return;
        }

        _floatingOverride = rect;
        Position();
    }

    /// <summary>
    /// The AppBar full-screen callback only covers topmost windows and does not say
    /// which monitor they are on, so the dock layer follows its own check of the
    /// foreground window on the dock's monitor.
    /// </summary>
    internal void SetFullScreenDetected(bool detected)
    {
        if (_disposed || _fullScreenDetected == detected)
        {
            return;
        }

        _fullScreenDetected = detected;
        UpdateWindowLayer();
    }

    internal void Apply(
        MonitorSnapshot monitor,
        bool reserveWorkArea,
        double contentLengthDip,
        double thicknessDip,
        double edgeGapDip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var monitorChanged = _monitor is not null
            && !string.Equals(_monitor.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase);
        _monitor = monitor;
        _reserveWorkArea = reserveWorkArea;
        _contentLengthDip = Math.Max(DockLayoutMetrics.EmptyLengthDip, contentLengthDip);
        _thicknessDip = thicknessDip;
        _edgeGapDip = edgeGapDip;
        if (!reserveWorkArea)
        {
            IsRecovering = false;
        }

        _applying = true;
        try
        {
            Dispatch(_reservation.Apply(reserveWorkArea, monitorChanged));
            Position();
        }
        finally
        {
            _applying = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _recoveryGeneration++;
        IsRecovering = false;
        _deferredPosition.Stop();
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
                    IsRecovering = false;
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
            var scale = _monitor.Dpi / 96d;
            var thicknessPixels = Math.Max(1, (int)Math.Round(_thicknessDip * scale));
            var edgeGapPixels = Math.Max(0, (int)Math.Round(_edgeGapDip * scale));
            var stripThickness = thicknessPixels + edgeGapPixels;

            PixelRect strip;
            if (_reservation.IsRegistered && TryReserveStrip(_monitor, thicknessPixels, stripThickness) is { } reserved)
            {
                strip = reserved;
            }
            else
            {
                if (_reservation.IsRegistered)
                {
                    Dispatch(_reservation.Release());
                    if (!_applying)
                    {
                        _window.Dispatcher.BeginInvoke(
                            DispatcherPriority.Background,
                            () => EnvironmentChanged?.Invoke());
                    }
                }

                var workArea = GetLiveWorkingArea(_monitor);
                var fallbackArea = workArea.HasArea ? workArea : _monitor.Bounds;
                strip = DockGeometry.Calculate(
                    fallbackArea,
                    Edge,
                    Math.Min(stripThickness, fallbackArea.Height));
            }

            var requestedLength = Math.Max(1, (int)Math.Round(_contentLengthDip * scale));
            var contentLength = FloatingDockGeometry.ClampExtent(
                requestedLength,
                strip.Width,
                DockLayoutMetrics.MaxExtentFraction,
                Math.Max(1, (int)Math.Round(DockLayoutMetrics.EmptyLengthDip * scale)));
            Bounds = FloatingDockGeometry.Calculate(
                strip,
                Edge,
                contentLength,
                thicknessPixels,
                edgeGapPixels);

            var placement = _floatingOverride ?? Bounds;
            DockNativeMethods.MoveWindow(
                _handle,
                placement.Left,
                placement.Top,
                placement.Width,
                placement.Height,
                true);
            UpdateWindowLayer();
        }
        finally
        {
            _positioning = false;
        }
    }

    private PixelRect? TryReserveStrip(MonitorSnapshot monitor, int thicknessPixels, int stripThickness)
    {
        var requestedHeight = Math.Min(stripThickness, monitor.Bounds.Height);
        var requested = DockGeometry.Calculate(monitor.Bounds, Edge, requestedHeight);
        var data = CreateData();
        data.Edge = (uint)Edge;
        data.Rectangle = ToNative(requested);
        if (DockNativeMethods.SHAppBarMessage(DockNativeMethods.AbmQueryPos, ref data) == 0)
        {
            // Explorer may silently drop an AppBar registration (for example across
            // monitor changes or shell hiccups); try registering once before falling back.
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

        var queried = ToPixel(data.Rectangle);
        var adjusted = queried with
        {
            Top = queried.Bottom - requestedHeight
        };
        if (!AppBarReservationState.IsValidStrip(adjusted, monitor.Bounds, thicknessPixels))
        {
            return null;
        }

        // Explorer already holds an unchanged strip; setting it again would only
        // notify the other AppBars and invite them to answer in kind — unless the
        // work area shows Explorer reserving more than that strip.
        if (_positionGuard.NeedsSubmit(adjusted)
            || _workAreaCheck.NeedsResubmit(
                GetLiveWorkingArea(monitor).Bottom,
                adjusted.Top))
        {
            data.Rectangle = ToNative(adjusted);
            if (DockNativeMethods.SHAppBarMessage(DockNativeMethods.AbmSetPos, ref data) == 0)
            {
                return null;
            }

            UiDiagnostics.Hit(UiActivity.AppBarSetPosition);
            var actual = ToPixel(data.Rectangle);
            if (!AppBarReservationState.IsValidStrip(actual, monitor.Bounds, thicknessPixels))
            {
                return null;
            }

            _positionGuard.Submitted(adjusted);
            _reservedStrip = actual;
        }

        return AppBarReservationState.IsValidStrip(_reservedStrip, monitor.Bounds, thicknessPixels)
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
            IsRecovering = _reserveWorkArea;
            _reservation.ResetRegistration();
            _positionGuard.Reset();
            _workAreaCheck.Reset();
            var recoveryGeneration = ++_recoveryGeneration;
            _window.Dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                () => _ = RecoverAfterExplorerRestartAsync(recoveryGeneration));
            return 0;
        }

        if ((uint)message == _callbackMessage)
        {
            switch ((int)wParam)
            {
                case DockNativeMethods.AbnPosChanged:
                    UiDiagnostics.Hit(UiActivity.AppBarNotice);
                    if (_positionGuard.TryHandleNow(Environment.TickCount64))
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
            case DockNativeMethods.WmWindowPosChanged when _reservation.IsRegistered && !_positioning:
                NotifyAppBar(DockNativeMethods.AbmWindowPosChanged, 0);
                break;
            // A work-area change can leave Explorer holding a stale strip for the dock;
            // Position compares the two once things settle.
            case NativeMethods.WmSettingChange
                when wParam == NativeMethods.SpiSetWorkArea
                    && _reservation.IsRegistered
                    && !_deferredPosition.IsEnabled:
                _deferredPosition.Start();
                break;
            case DockNativeMethods.WmDisplayChange:
            case DockNativeMethods.WmDpiChanged:
                _window.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    () => EnvironmentChanged?.Invoke());
                break;
        }

        return 0;
    }

    private async Task RecoverAfterExplorerRestartAsync(int recoveryGeneration)
    {
        try
        {
            for (var attempt = 1; attempt <= ExplorerRecoveryAttemptCount; attempt++)
            {
                if (_disposed || recoveryGeneration != _recoveryGeneration)
                {
                    return;
                }

                if (!_reserveWorkArea)
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
        }
        finally
        {
            if (recoveryGeneration == _recoveryGeneration)
            {
                IsRecovering = false;
            }
        }

        if (_disposed || recoveryGeneration != _recoveryGeneration)
        {
            return;
        }

        Position();
        ExplorerRestarted?.Invoke();
    }

    internal static PixelRect GetLiveWorkingArea(MonitorSnapshot monitor) =>
        MonitorCatalog.GetAll()
            .FirstOrDefault(candidate => string.Equals(
                candidate.DeviceName,
                monitor.DeviceName,
                StringComparison.OrdinalIgnoreCase))?
            .WorkingArea
        ?? monitor.WorkingArea;

    private void NotifyAppBar(uint message, nint parameter)
    {
        // A synchronous call into Explorer; a stall right after this mark waited on it.
        UiDiagnostics.Hit(UiActivity.AppBarNotifyExplorer);
        var data = CreateData();
        data.Parameter = parameter;
        DockNativeMethods.SHAppBarMessage(message, ref data);
    }

    internal void EnsureLayer() => UpdateWindowLayer();

    // A reserved dock behaves like the taskbar: above normal windows, below a
    // full-screen app. A smart-hide dock is only on top while it is shown.
    private void UpdateWindowLayer()
    {
        var insertAfter = _reservation.IsRegistered && _fullScreenDetected
            ? DockNativeMethods.HwndBottom
            : DockNativeMethods.HwndTopmost;
        NativeMethods.SetWindowPos(
            _handle,
            insertAfter,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
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
