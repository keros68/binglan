using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.Core.Dock;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using Application = System.Windows.Application;

namespace BingLan.App.Windows;

/// <summary>
/// Transient mask over the whole virtual screen, opened from the tray to create a file
/// box by dragging a rectangle ("框选新建分组盒"). The user explicitly starts the mode,
/// so a short-lived topmost window is acceptable; completing the drag, Esc, right-click
/// or losing focus closes it again. Mouse handlers record physical cursor coordinates via
/// <c>GetCursorPos</c> so mixed-DPI and negative-offset secondary monitors place the new
/// box accurately, while the DIP seam methods remain intact for synthetic test input.
/// </summary>
internal sealed class BoxMarqueeOverlay : Window
{
    // Below this the drag reads as an accidental click rather than a selection.
    private const double MinDragDip = 40d;

    private readonly Rectangle _marquee = CreateMarquee();
    private Point? _startPoint;
    private Point _lastPoint;
    private (int X, int Y)? _physicalStart;
    private (int X, int Y) _physicalLast;
    // Close() must not re-enter itself: closing deactivates the window, which would
    // otherwise call Cancel → Close again while the window is still closing.
    private bool _closeRequested;

    internal BoxMarqueeOverlay()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = AccentBrush(0x12);
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        Focusable = true;
        Cursor = Cursors.Cross;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        var root = new Grid();
        root.Children.Add(_marquee);
        root.Children.Add(CreateHint());
        Content = root;

        SourceInitialized += (_, _) => PositionAcrossPhysicalVirtualScreen();
        MouseLeftButtonDown += (_, e) =>
        {
            CaptureMouse();
            if (DockNativeMethods.GetCursorPos(out var cursor))
            {
                _physicalStart = (cursor.X, cursor.Y);
                _physicalLast = (cursor.X, cursor.Y);
            }
            BeginMarquee(e.GetPosition(this));
        };
        MouseMove += (_, e) =>
        {
            if (_physicalStart is not null && DockNativeMethods.GetCursorPos(out var cursor))
            {
                _physicalLast = (cursor.X, cursor.Y);
            }
            UpdateMarquee(e.GetPosition(this));
        };
        MouseLeftButtonUp += (_, _) =>
        {
            if (_physicalStart is not null && DockNativeMethods.GetCursorPos(out var cursor))
            {
                _physicalLast = (cursor.X, cursor.Y);
            }
            CompleteMarquee();
        };
        MouseRightButtonUp += (_, _) => Cancel();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Cancel();
                e.Handled = true;
            }
        };
        // Alt+Tab or any focus steal ends the mode instead of leaving a screen-wide mask.
        Deactivated += (_, _) => Cancel();
        // Capture stolen mid-drag (another window popping up) also ends the mode.
        LostMouseCapture += (_, _) =>
        {
            if (_startPoint is not null || _physicalStart is not null)
            {
                Cancel();
            }
        };
    }

    /// <summary>Raised with the selected rectangle in screen DIPs when driven via synthetic DIP seams.</summary>
    internal event Action<Rect>? BoxSelected;

    /// <summary>Raised with the exact physical screen pixel rectangle for real pointer drags across mixed DPIs.</summary>
    internal event Action<PixelRect>? PhysicalBoxSelected;

    internal static PixelRect? ComputePhysicalUnion(IReadOnlyList<MonitorSnapshot> monitors)
    {
        if (monitors.Count == 0)
        {
            return null;
        }
        var left = monitors.Min(m => m.Bounds.Left);
        var top = monitors.Min(m => m.Bounds.Top);
        var right = monitors.Max(m => m.Bounds.Right);
        var bottom = monitors.Max(m => m.Bounds.Bottom);
        return right > left && bottom > top ? new PixelRect(left, top, right, bottom) : null;
    }

    private void PositionAcrossPhysicalVirtualScreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || ComputePhysicalUnion(MonitorCatalog.GetAll()) is not { } union)
        {
            return;
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (DockNativeMethods.GetWindowRect(handle, out var native)
                && new PixelRect(native.Left, native.Top, native.Right, native.Bottom) == union)
            {
                break;
            }
            NativeMethods.SetWindowPos(
                handle,
                DockNativeMethods.HwndTopmost,
                union.Left,
                union.Top,
                union.Width,
                union.Height,
                NativeMethods.SwpNoActivate);
        }
    }

    // The seam methods below are pure state transitions of the marquee rectangle.

    internal void BeginPhysicalMarquee(int x, int y)
    {
        _physicalStart = (x, y);
        _physicalLast = (x, y);
        _startPoint = new Point(0d, 0d);
        _lastPoint = new Point(0d, 0d);
    }

    internal void UpdatePhysicalMarquee(int x, int y)
    {
        if (_physicalStart is not null)
        {
            _physicalLast = (x, y);
        }
    }

    internal void BeginMarquee(Point point)
    {
        _startPoint = point;
        _lastPoint = point;
        _marquee.Visibility = Visibility.Visible;
        UpdateMarquee(point);
    }

    internal void UpdateMarquee(Point point)
    {
        if (_startPoint is not { } start)
        {
            return;
        }
        _lastPoint = point;
        var rect = new Rect(start, point);
        _marquee.Margin = new Thickness(
            rect.X, rect.Y, Math.Max(0d, ActualWidth - rect.Right), Math.Max(0d, ActualHeight - rect.Bottom));
    }

    internal void CompleteMarquee()
    {
        if (_physicalStart is { } physStart)
        {
            var physEnd = _physicalLast;
            _physicalStart = null;
            _startPoint = null;
            var physical = new PixelRect(
                Math.Min(physStart.X, physEnd.X),
                Math.Min(physStart.Y, physEnd.Y),
                Math.Max(physStart.X, physEnd.X),
                Math.Max(physStart.Y, physEnd.Y));
            var centerX = (physical.Left + physical.Right) / 2;
            var centerY = (physical.Top + physical.Bottom) / 2;
            var monitors = MonitorCatalog.GetAll();
            var target = monitors.FirstOrDefault(m =>
                    centerX >= m.Bounds.Left && centerX < m.Bounds.Right
                    && centerY >= m.Bounds.Top && centerY < m.Bounds.Bottom)
                ?? monitors.FirstOrDefault(m => m.IsPrimary)
                ?? monitors.FirstOrDefault();
            var scale = target is { Dpi: > 0 } ? target.Dpi / 96d : 1d;
            var minDragPx = MinDragDip * scale;
            if (physical.Width < minDragPx || physical.Height < minDragPx)
            {
                Cancel();
                return;
            }
            CloseOnce();
            PhysicalBoxSelected?.Invoke(physical);
            return;
        }

        if (_startPoint is not { } start)
        {
            Cancel();
            return;
        }
        var local = new Rect(start, _lastPoint);
        _startPoint = null;
        if (local.Width < MinDragDip || local.Height < MinDragDip)
        {
            Cancel();
            return;
        }

        var selected = new Rect(Left + local.X, Top + local.Y, local.Width, local.Height);
        CloseOnce();
        BoxSelected?.Invoke(selected);
    }

    internal void Cancel()
    {
        _startPoint = null;
        _physicalStart = null;
        CloseOnce();
    }

    private void CloseOnce()
    {
        if (_closeRequested || !IsLoaded)
        {
            return;
        }
        _closeRequested = true;
        Close();
    }

    private static Rectangle CreateMarquee()
    {
        return new Rectangle
        {
            Stroke = AccentBrush(0xCC),
            StrokeThickness = 1.5d,
            Fill = AccentBrush(0x22),
            StrokeDashArray = new DoubleCollection { 4d, 3d },
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
    }

    private static FrameworkElement CreateHint()
    {
        var text = new TextBlock
        {
            Text = "拖动框选桌面区域，松开后在此处新建文件收纳盒 · Esc 或右键取消",
            Foreground = Brushes.White,
            FontSize = 13d
        };
        return new Border
        {
            Child = text,
            Background = new SolidColorBrush(Color.FromArgb(0xC4, 0x18, 0x22, 0x2F)),
            BorderBrush = AccentBrush(0x66),
            BorderThickness = new Thickness(1d),
            CornerRadius = new CornerRadius(8d),
            Padding = new Thickness(14d, 8d, 14d, 8d),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Margin = new Thickness(0d, 24d, 0d, 0d),
            IsHitTestVisible = false
        };
    }

    private static SolidColorBrush AccentBrush(byte alpha)
    {
        var colour = Color.FromRgb(0x1E, 0xA9, 0xFF);
        if (Application.Current?.TryFindResource("IceActionBrush") is SolidColorBrush accent)
        {
            colour = accent.Color;
        }
        var brush = new SolidColorBrush(Color.FromArgb(alpha, colour.R, colour.G, colour.B));
        brush.Freeze();
        return brush;
    }
}
