using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.Core.Dock;

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
    private static readonly TimeSpan ExplorerRecoveryDelay = TimeSpan.FromMilliseconds(400);
    private readonly Window _window;
    private readonly nint _handle;
    private readonly HwndSource _source;
    private readonly uint _callbackMessage;
    private readonly uint _taskbarCreatedMessage;
    private readonly AppBarReservationState _reservation = new();
    private MonitorSnapshot? _monitor;
    private double _heightDip;
    private bool _reserveWorkArea;
    private bool _fullScreenDetected;
    private bool _positioning;
    private bool _disposed;
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
        _source.AddHook(WindowProc);
    }

    internal event Action? EnvironmentChanged;

    internal event Action? ExplorerRestarted;

    internal PixelRect Bounds { get; private set; }

    internal bool IsReserved => _reservation.IsRegistered;

    internal MonitorSnapshot? Monitor => _monitor;

    internal void SetFullScreenDetected(bool detected)
    {
        if (_disposed || _fullScreenDetected == detected)
        {
            return;
        }

        _fullScreenDetected = detected;
        UpdateWindowLayer();
    }

    internal void Apply(MonitorSnapshot monitor, bool reserveWorkArea, double heightDip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _monitor = monitor;
        _reserveWorkArea = reserveWorkArea;
        _heightDip = Math.Max(1d, heightDip);

        Dispatch(_reservation.Apply(reserveWorkArea));
        Position();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _recoveryGeneration++;
        Dispatch(_reservation.Release());
        _source.RemoveHook(WindowProc);
        _disposed = true;
    }

    private void Dispatch(IReadOnlyList<AppBarMessage> messages)
    {
        foreach (var message in messages)
        {
            var data = CreateData();
            if (message == AppBarMessage.Register)
            {
                data.CallbackMessage = _callbackMessage;
                if (DockNativeMethods.SHAppBarMessage(DockNativeMethods.AbmNew, ref data) == 0)
                {
                    _reservation.MarkRegisterFailed();
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
            var thicknessPixels = Math.Max(1, (int)Math.Round(_heightDip * scale));
            // The reservation is one pixel shorter than the bar: a maximized window
            // tucks its top edge under the bar's last row, so the two meet without a
            // visible seam.
            var reservePixels = Math.Max(1, thicknessPixels - 1);

            PixelRect strip;
            if (_reservation.IsRegistered)
            {
                var requested = DockGeometry.Calculate(
                    _monitor.Bounds,
                    Edge,
                    Math.Min(reservePixels, _monitor.Bounds.Height));
                var data = CreateData();
                data.Edge = (uint)Edge;
                data.Rectangle = ToNative(requested);
                DockNativeMethods.SHAppBarMessage(DockNativeMethods.AbmQueryPos, ref data);
                // Keep the strip at the top of whatever the system allows: other bars at
                // the same edge push it down rather than off the monitor.
                var adjusted = ToPixel(data.Rectangle) with
                {
                    Bottom = data.Rectangle.Top + Math.Min(reservePixels, _monitor.Bounds.Height)
                };
                // ABN_POSCHANGED arrives for every bar's move (the auto-hiding taskbar
                // fires it constantly), and every ABM_SETPOS rebroadcasts a work-area
                // change system-wide — screen-capture overlays react to that by
                // re-laying out. An unchanged strip needs no new broadcast.
                if (adjusted == Bounds)
                {
                    return;
                }
                data.Rectangle = ToNative(adjusted);
                DockNativeMethods.SHAppBarMessage(DockNativeMethods.AbmSetPos, ref data);
                strip = ToPixel(data.Rectangle);
            }
            else
            {
                var workArea = DockAppBarController.GetLiveWorkingArea(_monitor);
                strip = DockGeometry.Calculate(
                    workArea,
                    Edge,
                    Math.Min(reservePixels, workArea.Height));
            }

            Bounds = strip;
            var windowRect = strip with
            {
                Bottom = Math.Min(strip.Top + thicknessPixels, _monitor.Bounds.Bottom)
            };
            DockNativeMethods.MoveWindow(
                _handle,
                windowRect.Left,
                windowRect.Top,
                windowRect.Width,
                windowRect.Height,
                true);
            UpdateWindowLayer();
        }
        finally
        {
            _positioning = false;
        }
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_disposed)
        {
            return 0;
        }

        if ((uint)message == _taskbarCreatedMessage)
        {
            _reservation.ResetRegistration();
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
                    Position();
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

        if (_disposed || recoveryGeneration != _recoveryGeneration)
        {
            return;
        }

        Position();
        ExplorerRestarted?.Invoke();
    }

    private void NotifyAppBar(uint message, nint parameter)
    {
        var data = CreateData();
        data.Parameter = parameter;
        DockNativeMethods.SHAppBarMessage(message, ref data);
    }

    // A reserved bar behaves like the taskbar: above normal windows, below a full-screen
    // app. A smart-hide bar is only on top while it is shown.
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
