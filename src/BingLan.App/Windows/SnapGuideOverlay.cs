using System.Windows;
using System.Windows.Interop;
using System.Windows.Shapes;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.Core.Services;

namespace BingLan.App.Windows;

/// <summary>
/// Click-through, non-activating guide lines drawn while a card is dragged. Each guide is
/// its own thin window the size of the line, so moving a guide never redraws a window
/// that covers the whole monitor. Hidden when the drag ends, and closed after a short
/// idle period so inactive overlay HWNDs are not retained indefinitely.
/// </summary>
internal sealed class SnapGuideOverlay : Window
{
    private const int ThicknessPixels = 2;
    private static readonly TimeSpan IdleCloseDelay = TimeSpan.FromSeconds(30);
    private static readonly List<SnapGuideOverlay> Pool = [];
    private static DispatcherTimer? _idleCloseTimer;
    private readonly Line _line = new() { SnapsToDevicePixels = true };
    private SnapGuide? _drawn;

    internal static int CachedWindowCount => Pool.Count;

    private SnapGuideOverlay()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        _line.SetResourceReference(Shape.StrokeProperty, "IceActionBrush");
        Content = _line;
        Closed += (_, _) => Pool.Remove(this);
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = (long)NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle);
            NativeMethods.SetWindowLongPtr(
                handle,
                NativeMethods.GwlExStyle,
                (nint)(style
                    | NativeMethods.WsExTransparent
                    | NativeMethods.WsExToolWindow
                    | NativeMethods.WsExLayered
                    | DockNativeMethods.WsExNoActivate));
        };
    }

    internal static void ShowGuides(MonitorSnapshot monitor, IReadOnlyList<SnapGuide> guides)
    {
        if (guides.Count == 0)
        {
            var anyVisible = false;
            foreach (var overlay in Pool)
            {
                if (overlay.IsVisible)
                {
                    anyVisible = true;
                    overlay.HideGuide();
                }
            }
            if (anyVisible || (Pool.Count > 0 && _idleCloseTimer is not { IsEnabled: true }))
            {
                ScheduleIdleClose();
            }
            return;
        }

        _idleCloseTimer?.Stop();
        while (Pool.Count < guides.Count)
        {
            Pool.Add(new SnapGuideOverlay());
        }

        for (var index = 0; index < Pool.Count; index++)
        {
            if (index < guides.Count)
            {
                Pool[index].Draw(monitor, guides[index]);
            }
            else
            {
                Pool[index].HideGuide();
            }
        }
    }

    internal static void HideGuides()
    {
        foreach (var overlay in Pool)
        {
            overlay.HideGuide();
        }
        ScheduleIdleClose();
    }

    internal static void DisposeHiddenOverlays()
    {
        _idleCloseTimer?.Stop();
        if (Pool.Count == 0 || Pool.Any(overlay => overlay.IsVisible))
        {
            return;
        }

        var toClose = Pool.ToArray();
        Pool.Clear();
        foreach (var overlay in toClose)
        {
            overlay.Close();
        }
    }

    private static void ScheduleIdleClose()
    {
        if (Pool.Count == 0)
        {
            return;
        }

        if (_idleCloseTimer is null)
        {
            _idleCloseTimer = new DispatcherTimer(
                IdleCloseDelay,
                DispatcherPriority.Background,
                (_, _) => DisposeHiddenOverlays(),
                Pool[0].Dispatcher);
        }

        _idleCloseTimer.Stop();
        _idleCloseTimer.Start();
    }

    private void HideGuide()
    {
        _drawn = null;
        if (IsVisible)
        {
            Hide();
        }
    }

    private void Draw(MonitorSnapshot monitor, SnapGuide guide)
    {
        if (IsVisible && _drawn == guide)
        {
            return;
        }
        _drawn = guide;

        var vertical = guide.X1 == guide.X2;
        var left = Math.Min(guide.X1, guide.X2) - (vertical ? ThicknessPixels / 2 : 0);
        var top = Math.Min(guide.Y1, guide.Y2) - (vertical ? 0 : ThicknessPixels / 2);
        var width = vertical ? ThicknessPixels : Math.Max(ThicknessPixels, Math.Abs(guide.X2 - guide.X1));
        var height = vertical ? Math.Max(ThicknessPixels, Math.Abs(guide.Y2 - guide.Y1)) : ThicknessPixels;

        var scale = monitor.Dpi / 96d;
        var length = (vertical ? height : width) / scale;
        var middle = ThicknessPixels / 2d / scale;
        _line.X1 = vertical ? middle : 0;
        _line.Y1 = vertical ? 0 : middle;
        _line.X2 = vertical ? middle : length;
        _line.Y2 = vertical ? length : middle;
        _line.StrokeThickness = ThicknessPixels / scale;
        _line.StrokeDashArray = guide.Kind == SnapGuideKind.Spacing ? [4, 3] : null;

        if (!IsVisible)
        {
            Show();
        }
        NativeMethods.SetWindowPos(
            new WindowInteropHelper(this).Handle,
            DockNativeMethods.HwndTopmost,
            left,
            top,
            width,
            height,
            NativeMethods.SwpNoActivate);
    }
}
