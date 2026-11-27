using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using BingLan.App.Interop;
using BingLan.App.Dock;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace BingLan.App.Windows;

/// <summary>
/// The small floating handle shown while the dock is smart-hidden: hovering it expands
/// the dock beside it, dragging moves the handle. It never takes focus or the taskbar,
/// and its surface follows the dock's colours through the shared dynamic brushes.
/// </summary>
public partial class DockHandleWindow : Window
{
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const int DragStartPixels = 3;

    private nint _handle;
    private bool _dragging;
    private bool _dragStarted;
    private bool _closing;
    private bool _isChangingVisibilityByApp;
    private PixelPoint _pressStartCursor;
    private PixelRect _pressStartRect;
    private readonly DockGlassSurface _glass;

    internal DockHandleWindow()
    {
        InitializeComponent();
        _glass = new DockGlassSurface(this, HandleSurface, "DockHandleBrush", "DockSurfaceBorderBrush");
        SourceInitialized += OnSourceInitialized;
        Closing += (_, _) => _closing = true;
    }

    internal event Action? ShellRecovered;

    internal nint WindowHandle => _handle;

    /// <summary>Raised once the pointer moves past the press threshold: the owner stops hovering expansions.</summary>
    internal event Action? DragStarted;

    /// <summary>Raised on release after a real drag: the owner clamps and persists the position.</summary>
    internal event Action? DragEnded;

    /// <summary>Raised on a click without dragging so the dock reveals immediately at its resting position.</summary>
    internal event Action? Clicked;

    /// <summary>Raised while external files are dragged over the handle so the dock can expand.</summary>
    internal event Action? FileDragEntered;

    /// <summary>Raised when external files are dropped directly onto the handle.</summary>
    internal event System.Windows.DragEventHandler? ExternalDrop;

    internal PixelRect CurrentRect
    {
        get
        {
            if (_handle == 0)
            {
                return new PixelRect(0, 0, 0, 0);
            }

            DockNativeMethods.GetWindowRect(_handle, out var rectangle);
            return new PixelRect(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
        }
    }

    internal bool IsDragActive => _dragging;

    internal void SetFrostedGlass(bool enabled) => _glass.SetFrostedGlass(enabled);

    internal void MoveTo(PixelRect rect)
    {
        if (_handle == 0 || rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        DockNativeMethods.MoveWindow(_handle, rect.Left, rect.Top, rect.Width, rect.Height, true);
    }

    internal void SetVisible(bool visible)
    {
        if (visible && !IsVisible)
        {
            Show();
        }
        else if (!visible && IsVisible)
        {
            _isChangingVisibilityByApp = true;
            try
            {
                Hide();
            }
            finally
            {
                _isChangingVisibilityByApp = false;
            }
        }
    }

    internal void EnsureAboveDesktop()
    {
        if (_closing || _handle == 0 || !IsVisible)
        {
            return;
        }
        if (NativeMethods.IsIconic(_handle) || !NativeMethods.IsWindowVisible(_handle))
        {
            NativeMethods.ShowWindow(_handle, NativeMethods.SwShowNoActivate);
        }
        NativeMethods.SetWindowPos(
            _handle,
            DockNativeMethods.HwndTopmost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
    }

    // The three-step drag keeps the window on the pointer itself, so the move feels the
    // same whatever the system's "show window contents while dragging" setting is.
    internal void BeginDrag(int cursorX, int cursorY)
    {
        if (_dragging)
        {
            return;
        }

        _dragging = true;
        _dragStarted = false;
        _pressStartCursor = new PixelPoint(cursorX, cursorY);
        _pressStartRect = CurrentRect;
    }

    internal void DragTo(int cursorX, int cursorY)
    {
        if (!_dragging)
        {
            return;
        }

        var deltaX = cursorX - _pressStartCursor.X;
        var deltaY = cursorY - _pressStartCursor.Y;
        if (!_dragStarted && Math.Abs(deltaX) + Math.Abs(deltaY) < DragStartPixels)
        {
            return;
        }

        if (!_dragStarted)
        {
            _dragStarted = true;
            DragStarted?.Invoke();
        }

        MoveTo(new PixelRect(
            _pressStartRect.Left + deltaX,
            _pressStartRect.Top + deltaY,
            _pressStartRect.Left + deltaX + _pressStartRect.Width,
            _pressStartRect.Top + deltaY + _pressStartRect.Height));
    }

    internal void EndDrag(bool allowClick = false)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        var started = _dragStarted;
        _dragStarted = false;
        if (started)
        {
            DragEnded?.Invoke();
        }
        else if (allowClick)
        {
            Clicked?.Invoke();
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        var style = (long)NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle);
        NativeMethods.SetWindowLongPtr(
            _handle,
            NativeMethods.GwlExStyle,
            (nint)(style | NativeMethods.WsExToolWindow | DockNativeMethods.WsExNoActivate));
        var cornerPreference = NativeMethods.DwmWindowCornerDoNotRound;
        NativeMethods.DwmSetWindowAttribute(
            _handle,
            NativeMethods.DwmwaWindowCornerPreference,
            ref cornerPreference,
            sizeof(int));
        HwndSource.FromHwnd(_handle)?.AddHook(PreventActivation);
    }

    private nint PreventActivation(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmMouseActivate)
        {
            handled = true;
            return MaNoActivate;
        }

        if (!_closing && !_isChangingVisibilityByApp)
        {
            if (message == NativeMethods.WmSize && wParam == NativeMethods.SizeMinimized)
            {
                RecoverFromShell(minimized: true);
            }
            else if (message == NativeMethods.WmShowWindow && wParam == 0)
            {
                RecoverFromShell(minimized: false);
            }
            else if (message == NativeMethods.WmWindowPosChanged
                     && (System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.WindowPos>(lParam).Flags
                         & NativeMethods.SwpHideWindow) != 0)
            {
                RecoverFromShell(minimized: false);
            }
        }

        return 0;
    }

    private void RecoverFromShell(bool minimized)
    {
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Send,
            new Action(() =>
            {
                if (_closing || _handle == 0 || !IsVisible)
                {
                    return;
                }
                if (minimized ? !NativeMethods.IsIconic(_handle) : NativeMethods.IsWindowVisible(_handle))
                {
                    return;
                }
                EnsureAboveDesktop();
                ShellRecovered?.Invoke();
            }));
    }

    private void OnHandleDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = (e.AllowedEffects & DragDropEffects.Link) != 0
                ? DragDropEffects.Link
                : e.AllowedEffects & DragDropEffects.Copy;
            e.Handled = true;
            FileDragEntered?.Invoke();
            return;
        }

        if (DockShortcuts.HasShellItemDrop(e.Data) && (e.AllowedEffects & DragDropEffects.Link) != 0)
        {
            e.Effects = DragDropEffects.Link;
            e.Handled = true;
            FileDragEntered?.Invoke();
            return;
        }

        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnHandleDrop(object sender, DragEventArgs e)
    {
        ExternalDrop?.Invoke(this, e);
    }

    private void OnHandleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DockNativeMethods.GetCursorPos(out var cursor);
        BeginDrag(cursor.X, cursor.Y);
        // Without the capture, a release outside the window would never arrive and the
        // drag would hang; abort immediately instead of getting stuck.
        if (!CaptureMouse())
        {
            EndDrag();
        }
    }

    private void OnHandleMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        DockNativeMethods.GetCursorPos(out var cursor);
        DragTo(cursor.X, cursor.Y);
    }

    private void OnHandleMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndDrag(allowClick: true);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    private void OnHandleLostMouseCapture(object sender, MouseEventArgs e)
    {
        EndDrag(allowClick: false);
    }

    private void OnHandleMouseEnter(object sender, MouseEventArgs e)
    {
        ZoomGlyph(DesktopStyleRules.HoverZoom(
            IceBlueDockWindow.Motion,
            SystemParameters.ClientAreaAnimation));
    }

    private void OnHandleMouseLeave(object sender, MouseEventArgs e)
    {
        ZoomGlyph(1d);
    }

    private void ZoomGlyph(double scale)
    {
        var animation = new DoubleAnimation(scale, DesktopStyleRules.HoverZoomDuration(IceBlueDockWindow.Motion));
        HandleGlyphScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, animation);
        HandleGlyphScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, animation);
    }

    // The landing bounce when the dock row collapses into the handle: the glyph pops
    // once so the absorption reads as a single motion. Standard motion level only,
    // and never against the system "show animations" switch.
    internal void Pulse()
    {
        if (!SystemParameters.ClientAreaAnimation || IceBlueDockWindow.Motion != MotionLevel.Standard)
        {
            return;
        }

        var duration = DockMorphMotion.HandlePulseDuration;
        var pulse = new DoubleAnimationUsingKeyFrames { Duration = new Duration(duration) };
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.15d, KeyTime.FromTimeSpan(duration / 2))
        {
            EasingFunction = new QuadraticEase()
        });
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1d, KeyTime.FromTimeSpan(duration))
        {
            EasingFunction = new QuadraticEase()
        });
        HandleGlyphScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, pulse);
        HandleGlyphScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, pulse);
    }

    /// <summary>
    /// A few pulses of the handle while the dock is tucked away and an app asks for
    /// attention, so the hidden dock still says something happened.
    /// </summary>
    internal void PulseAttention(int count)
    {
        if (!SystemParameters.ClientAreaAnimation || count <= 0)
        {
            return;
        }

        var duration = DockMorphMotion.HandlePulseDuration;
        var pulse = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(duration),
            RepeatBehavior = new RepeatBehavior(count)
        };
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.35d, KeyTime.FromTimeSpan(duration / 2))
        {
            EasingFunction = new QuadraticEase()
        });
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1d, KeyTime.FromTimeSpan(duration))
        {
            EasingFunction = new QuadraticEase()
        });
        HandleGlyphScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, pulse);
        HandleGlyphScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, pulse);
    }

    private readonly record struct PixelPoint(int X, int Y);
}
