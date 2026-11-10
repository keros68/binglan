using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
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
/// or losing focus closes it again. Mouse handlers only forward into the seam methods
/// the tests drive, so a selection can be simulated without real input.
/// </summary>
internal sealed class BoxMarqueeOverlay : Window
{
    // Below this the drag reads as an accidental click rather than a selection.
    private const double MinDragDip = 40d;

    private readonly Rectangle _marquee = CreateMarquee();
    private Point? _startPoint;
    private Point _lastPoint;
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

        MouseLeftButtonDown += (_, e) =>
        {
            CaptureMouse();
            BeginMarquee(e.GetPosition(this));
        };
        MouseMove += (_, e) => UpdateMarquee(e.GetPosition(this));
        MouseLeftButtonUp += (_, _) => CompleteMarquee();
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
            if (_startPoint is not null)
            {
                Cancel();
            }
        };
    }

    /// <summary>Raised with the selected rectangle in screen DIPs after the mask closed.</summary>
    internal event Action<Rect>? BoxSelected;

    // The three seam methods below are pure state transitions of the marquee rectangle.

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
            Text = "拖动框选桌面区域，松开后在此处新建分组盒 · Esc 或右键取消",
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
