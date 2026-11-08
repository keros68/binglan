using System.Windows;
using System.Windows.Interop;
using BingLan.App.Dock;
using BingLan.App.Interop;

namespace BingLan.App.Windows;

/// <summary>
/// Click-through, non-activating grid shown over a monitor's work area while a card is
/// dragged with grid alignment on. One static window per drag, tiled by a brush, so the
/// grid never redraws while the pointer moves. Hidden when the drag ends or Alt is held.
/// </summary>
internal sealed class SnapGridOverlay : Window
{
    private static SnapGridOverlay? _instance;
    private double _drawnCell;

    private SnapGridOverlay()
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

    /// <summary>Covers the work area with grid lines one cell apart, in physical pixels.</summary>
    internal static void ShowGrid(MonitorSnapshot monitor, int cellPixels)
    {
        _instance ??= new SnapGridOverlay();
        _instance.Draw(monitor, cellPixels);
    }

    internal static void HideGrid()
    {
        _instance?.HideSelf();
    }

    private void HideSelf()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    private void Draw(MonitorSnapshot monitor, int cellPixels)
    {
        if (cellPixels <= 0)
        {
            HideSelf();
            return;
        }

        var scale = monitor.Dpi / 96d;
        var area = monitor.WorkingArea;
        // Bounds are set in DIP before showing so the window never flashes at another
        // size, then corrected to exact physical pixels.
        Left = area.Left / scale;
        Top = area.Top / scale;
        Width = (area.Right - area.Left) / scale;
        Height = (area.Bottom - area.Top) / scale;
        if (!IsVisible)
        {
            Show();
        }

        var cellDip = cellPixels / scale;
        if (Math.Abs(_drawnCell - cellDip) > 0.01d)
        {
            _drawnCell = cellDip;
            Background = GridBrush(cellDip, scale);
        }

        NativeMethods.SetWindowPos(
            new WindowInteropHelper(this).Handle,
            DockNativeMethods.HwndTopmost,
            area.Left,
            area.Top,
            area.Right - area.Left,
            area.Bottom - area.Top,
            NativeMethods.SwpNoActivate);
    }

    // A faint line at each cell corner of the shared accent colour; thin enough to read
    // as a hint, unlike the alignment guides' solid lines.
    private static System.Windows.Media.Brush GridBrush(double cellDip, double scale)
    {
        var colour = System.Windows.Media.Color.FromRgb(0x1E, 0xA9, 0xFF);
        if (System.Windows.Application.Current?.TryFindResource("IceActionBrush")
            is System.Windows.Media.SolidColorBrush accent)
        {
            colour = accent.Color;
        }
        var stroke = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(0x42, colour.R, colour.G, colour.B));
        stroke.Freeze();

        var drawing = new System.Windows.Media.DrawingGroup();
        using (var context = drawing.Open())
        {
            var pen = new System.Windows.Media.Pen(stroke, 1d / scale);
            pen.Freeze();
            context.DrawLine(pen, new System.Windows.Point(0, 0), new System.Windows.Point(cellDip, 0));
            context.DrawLine(pen, new System.Windows.Point(0, 0), new System.Windows.Point(0, cellDip));
        }
        var brush = new System.Windows.Media.DrawingBrush(drawing)
        {
            TileMode = System.Windows.Media.TileMode.Tile,
            Viewport = new Rect(0, 0, cellDip, cellDip),
            ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute
        };
        brush.Freeze();
        return brush;
    }
}
