using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.Core.Dock;

namespace BingLan.App.Dock;

/// <summary>
/// Positions the dock along the selected monitor edge (bottom, left, or right) and
/// optionally reserves the work area through the documented AppBar API. Registration is
/// released on dispose, edge/monitor changes, and replayed after Explorer recreates the taskbar.
/// </summary>
internal sealed class DockAppBarController : IDisposable
{
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
    private DockEdge _edge = DockEdge.Bottom;
    private int? _topBarReservedBottomPx;
    private PixelRect _reservedStrip;
    private MonitorSnapshot? _monitor;
    private bool _reserveWorkArea;
    private double _contentLengthDip = DockLayoutMetrics.EmptyLengthDip;
    private double _thicknessDip = DockLayoutMetrics.DockThicknessDip;
    private double _edgeGapDip = DockLayoutMetrics.EdgeGapDip;
    private double _overflowDip;
    private PixelRect? _floatingOverride;
    private bool _fullScreenDetected;
    private bool _positioning;
    private bool _updatingLayer;
    private bool _applying;
    private bool _refreshNativeTaskbarLayout;
    private bool _explorerRecovering;
    private bool _transientRecovering;
    private int _consecutiveReserveFailures;
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
                if (!_disposed && _reserveWorkArea)
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

    internal PixelRect Bounds { get; private set; }

    internal DockEdge Edge => _edge;

    internal bool IsReserved => _reservation.IsRegistered;

    internal bool IsRecovering => _explorerRecovering || _transientRecovering;

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
        double edgeGapDip,
        double overflowDip = 0d,
        DockEdge edge = DockEdge.Bottom,
        int? topBarReservedBottomPx = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalizedEdge = DockEdgeRules.Normalize(edge);
        var edgeChanged = _edge != normalizedEdge;
        var monitorChanged = _monitor is not null
            && AppBarReservationState.HasMonitorTargetChanged(
                _monitor.DeviceName,
                _monitor.Bounds,
                _monitor.Dpi,
                monitor.DeviceName,
                monitor.Bounds,
                monitor.Dpi);
        var targetChanged = monitorChanged || edgeChanged;
        if (_monitor?.Bounds != monitor.Bounds || _monitor?.WorkingArea != monitor.WorkingArea)
        {
            TaskbarEdgeGuard.InvalidateNativeTaskbarLayout(monitor.Bounds);
        }
        _monitor = monitor;
        _edge = normalizedEdge;
        _topBarReservedBottomPx = topBarReservedBottomPx;
        _reserveWorkArea = reserveWorkArea;
        _contentLengthDip = Math.Max(DockLayoutMetrics.EmptyLengthDip, contentLengthDip);
        _thicknessDip = thicknessDip;
        _edgeGapDip = edgeGapDip;
        _overflowDip = double.IsFinite(overflowDip) ? Math.Max(0d, overflowDip) : 0d;
        if (!reserveWorkArea || targetChanged)
        {
            _consecutiveReserveFailures = 0;
            _transientRecovering = false;
            _reserveRetryTimer.Stop();
            _reservedStrip = default;
        }
        if (!reserveWorkArea)
        {
            _explorerRecovering = false;
        }

        _applying = true;
        try
        {
            // When switching edges or monitors while reserved, AppBarReservationState emits
            // [Unregister, Register] so the old edge reservation is released before claiming the new one.
            Dispatch(_reservation.Apply(reserveWorkArea, targetChanged));
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
        _explorerRecovering = false;
        _transientRecovering = false;
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
                    _explorerRecovering = false;
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
                _consecutiveReserveFailures = 0;
                _transientRecovering = false;
                _reserveRetryTimer.Stop();
                strip = reserved;
            }
            else if (_reserveWorkArea
                && AppBarReservationState.IsValidStrip(_reservedStrip, _monitor.Bounds, thicknessPixels, _edge)
                && _consecutiveReserveFailures < MaxTransientReserveFailures)
            {
                // Transient Explorer hiccups can make ABM_QUERYPOS/ABM_SETPOS fail briefly;
                // retain the active reservation and previous strip while retrying so windows
                // do not expand and the dock does not fall back to smart-hide.
                _consecutiveReserveFailures++;
                _transientRecovering = true;
                strip = _reservedStrip;
                if (!_reserveRetryTimer.IsEnabled)
                {
                    _reserveRetryTimer.Start();
                }
            }
            else
            {
                var wasRetaining = _reservation.IsRegistered || _transientRecovering;
                _consecutiveReserveFailures = 0;
                _transientRecovering = false;
                _reserveRetryTimer.Stop();
                _reservedStrip = default;
                if (_reservation.IsRegistered)
                {
                    Dispatch(_reservation.Release());
                }
                if (wasRetaining && !_applying)
                {
                    _window.Dispatcher.BeginInvoke(
                        DispatcherPriority.Background,
                        () => EnvironmentChanged?.Invoke());
                }

                var workArea = GetLiveWorkingArea(_monitor);
                var fallbackArea = workArea.HasArea ? workArea : _monitor.Bounds;
                var maxFallbackThickness = DockEdgeRules.IsVertical(_edge)
                    ? fallbackArea.Width
                    : fallbackArea.Height;
                strip = DockGeometry.Calculate(
                    fallbackArea,
                    _edge,
                    Math.Min(stripThickness, maxFallbackThickness));
            }

            var nativeSafeArea = TaskbarEdgeGuard.AvoidNativeTaskbar(_monitor.Bounds, _monitor.Bounds);
            var safeCrossArea = DockEdgeRules.IsVertical(_edge)
                ? DockEdgeRules.ComputeSafeVerticalBounds(
                    _monitor.Bounds,
                    GetLiveWorkingArea(_monitor),
                    nativeSafeArea,
                    _topBarReservedBottomPx)
                : nativeSafeArea;
            strip = DockEdgeRules.ClipStripCrossAxis(strip, safeCrossArea, _edge);
            var requestedLength = Math.Max(1, (int)Math.Round(_contentLengthDip * scale));
            var stripExtent = DockEdgeRules.AvailableExtentPixels(strip, _edge);
            var contentLength = FloatingDockGeometry.ClampExtent(
                requestedLength,
                stripExtent,
                DockLayoutMetrics.MaxExtentFraction,
                Math.Max(1, (int)Math.Round(DockLayoutMetrics.EmptyLengthDip * scale)));
            Bounds = FloatingDockGeometry.Calculate(
                strip,
                _edge,
                contentLength,
                thicknessPixels,
                edgeGapPixels);

            var placement = _floatingOverride ?? Bounds;
            var overflowPixels = _overflowDip > 0d
                ? (int)Math.Round(_overflowDip * scale)
                : 0;
            // Extend only the transparent inward magnification headroom of the HWND; AppBar
            // reservation and resting Bounds remain unchanged.
            var windowRect = DockEdgeRules.ExpandWindowForOverflow(
                placement,
                _monitor.Bounds,
                _edge,
                overflowPixels);
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

    private PixelRect? TryReserveStrip(MonitorSnapshot monitor, int thicknessPixels, int stripThickness)
    {
        var maxThickness = DockEdgeRules.IsVertical(_edge)
            ? monitor.Bounds.Width
            : monitor.Bounds.Height;
        var requestedThickness = Math.Min(stripThickness, maxThickness);
        var requestBase = monitor.Bounds;
        if (DockEdgeRules.IsVertical(_edge)
            && _topBarReservedBottomPx is { } topLimit
            && topLimit > requestBase.Top
            && topLimit < requestBase.Bottom)
        {
            requestBase = requestBase with { Top = topLimit };
        }
        var requested = DockGeometry.Calculate(requestBase, _edge, requestedThickness);
        var data = CreateData();
        data.Edge = (uint)_edge;
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
            data.Edge = (uint)_edge;
            data.Rectangle = ToNative(requested);
            if (DockNativeMethods.SHAppBarMessage(DockNativeMethods.AbmQueryPos, ref data) == 0)
            {
                return null;
            }
        }

        var queried = ToPixel(data.Rectangle);
        var adjusted = DockEdgeRules.AdjustQueriedAppBarStrip(queried, _edge, requestedThickness);
        if (!AppBarReservationState.IsValidStrip(adjusted, monitor.Bounds, thicknessPixels, _edge))
        {
            return null;
        }

        // Explorer already holds an unchanged strip; setting it again would only
        // notify the other AppBars and invite them to answer in kind — unless the
        // work area shows Explorer reserving more than that strip.
        var (workAreaEdge, stripInnerEdge) = DockEdgeRules.WorkAreaEdgesForCheck(
            GetLiveWorkingArea(monitor),
            adjusted,
            _edge);
        if (_positionGuard.NeedsSubmit(adjusted)
            || _workAreaCheck.NeedsResubmit(workAreaEdge, stripInnerEdge))
        {
            data.Rectangle = ToNative(adjusted);
            if (DockNativeMethods.SHAppBarMessage(DockNativeMethods.AbmSetPos, ref data) == 0)
            {
                return null;
            }

            UiDiagnostics.Hit(UiActivity.AppBarSetPosition);
            var actual = ToPixel(data.Rectangle);
            if (!AppBarReservationState.IsValidStrip(actual, monitor.Bounds, thicknessPixels, _edge))
            {
                return null;
            }

            _positionGuard.Submitted(adjusted);
            _reservedStrip = actual;
        }

        return AppBarReservationState.IsValidStrip(_reservedStrip, monitor.Bounds, thicknessPixels, _edge)
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
            _explorerRecovering = _reserveWorkArea;
            _transientRecovering = false;
            _consecutiveReserveFailures = 0;
            _reserveRetryTimer.Stop();
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
                when AppBarPositionGuard.ShouldNotifyWindowPosChanged(_reservation.IsRegistered, _positioning, _updatingLayer):
                NotifyAppBar(DockNativeMethods.AbmWindowPosChanged, 0);
                break;
            // A work-area change can leave Explorer holding a stale strip for the dock;
            // Position compares the two once things settle.
            case NativeMethods.WmSettingChange
                when wParam == NativeMethods.SpiSetWorkArea && !_reservation.IsRegistered:
                _window.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    NotifyEnvironmentChanged);
                break;
            case NativeMethods.WmSettingChange
                when wParam == NativeMethods.SpiSetWorkArea
                    && _reservation.IsRegistered:
                _refreshNativeTaskbarLayout = true;
                if (!_deferredPosition.IsEnabled)
                {
                    _deferredPosition.Start();
                }
                break;
            case DockNativeMethods.WmDisplayChange:
            case DockNativeMethods.WmDpiChanged:
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
                _explorerRecovering = false;
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
