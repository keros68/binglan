using System.Numerics;
using System.Runtime.InteropServices;
using Color = System.Windows.Media.Color;
using BingLan.App.Interop;
using BingLan.Core.Dock;
using global::Windows.UI.Composition;
using global::Windows.UI.Composition.Desktop;
using WinRT;

namespace BingLan.App.Dock;

/// <summary>A native visual layer behind one WPF surface; it never accepts input.</summary>
internal sealed class DockCompositionBackdrop : IDisposable
{
    private static readonly NativeMethods.WindowProcedure Procedure = WindowProc;
    private static bool _classRegistered;
    private static global::Windows.System.DispatcherQueueController? _queue;
    private nint _window;
    private Compositor? _compositor;
    private DesktopWindowTarget? _target;
    private ContainerVisual? _root;
    private SpriteVisual? _glass;
    private ShapeVisual? _decoration;
    private CompositionRoundedRectangleGeometry? _geometry;
    private CompositionColorBrush? _tint;
    private CompositionColorBrush? _stroke;
    private PixelRect? _bounds;
    private bool? _topmost;

    internal DockCompositionBackdrop()
    {
        try
        {
            if (!_classRegistered)
            {
                var cls = new NativeMethods.WindowClass { Size = (uint)Marshal.SizeOf<NativeMethods.WindowClass>(), Procedure = Procedure, Name = "BingLanDockComposition" };
                if (NativeMethods.RegisterClassEx(ref cls) == 0) throw new InvalidOperationException("Cannot register Dock background.");
                _classRegistered = true;
            }
            var exStyle = (uint)(NativeMethods.WsExNoRedirectionBitmap | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate);
            _window = NativeMethods.CreateWindowEx(exStyle, "BingLanDockComposition", "冰蓝 Dock 玻璃背景", 0x80000000,
                0, 0, 1, 1, 0, 0, 0, 0); // WS_POPUP, with pixels supplied only by Composition.
            if (_window == 0) throw new InvalidOperationException("Cannot create Dock background.");
            var enabled = 1;
            Marshal.ThrowExceptionForHR(NativeMethods.DwmSetWindowAttribute(_window, NativeMethods.DwmwaUseHostBackdropBrush, ref enabled, sizeof(int)));
            if (global::Windows.System.DispatcherQueue.GetForCurrentThread() is null)
            {
                var options = new QueueOptions { Size = Marshal.SizeOf<QueueOptions>(), ThreadType = 2, ApartmentType = 2 };
                Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var queue));
                try { _queue = MarshalInterface<global::Windows.System.DispatcherQueueController>.FromAbi(queue); }
                finally { Marshal.Release(queue); }
            }
            _compositor = new Compositor();
            Marshal.ThrowExceptionForHR(_compositor.As<ICompositorDesktopInterop>().CreateDesktopWindowTarget(_window, false, out var target));
            try { _target = MarshalInterface<DesktopWindowTarget>.FromAbi(target); }
            finally { Marshal.Release(target); }
            _root = _compositor.CreateContainerVisual();
            _geometry = _compositor.CreateRoundedRectangleGeometry();
            _glass = _compositor.CreateSpriteVisual();
            _glass.Brush = _compositor.CreateHostBackdropBrush();
            _glass.Clip = _compositor.CreateGeometricClip(_geometry);
            _root.Children.InsertAtTop(_glass);
            _decoration = _compositor.CreateShapeVisual();
            var shape = _compositor.CreateSpriteShape(_geometry);
            _tint = _compositor.CreateColorBrush();
            _stroke = _compositor.CreateColorBrush();
            shape.FillBrush = _tint;
            shape.StrokeBrush = _stroke;
            shape.StrokeThickness = 1;
            _decoration.Shapes.Add(shape);
            _root.Children.InsertAtTop(_decoration);
            _target.Root = _root;
        }
        catch { Dispose(); throw; }
    }

    internal nint Handle => _window;
    internal PixelRect? Bounds => _bounds;

    internal void Position(nint content, PixelRect bounds, bool reorder)
    {
        var topmost = (NativeMethods.GetExStyleOf(content) & NativeMethods.WsExTopmost) != 0;
        if (_topmost != topmost)
        {
            NativeMethods.SetWindowPos(_window, topmost ? -1 : -2, 0, 0, 0, 0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
            _topmost = topmost;
            reorder = true;
        }
        if (_bounds != bounds || reorder)
        {
            var flags = NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow;
            if (_bounds == bounds) flags |= NativeMethods.SwpNoMove | NativeMethods.SwpNoSize;
            BingLan.App.Services.UiDiagnostics.Hit(BingLan.App.Services.UiActivity.GlassMove);
            if (!NativeMethods.SetWindowPos(_window, content, bounds.Left, bounds.Top, bounds.Width, bounds.Height, flags))
                throw new InvalidOperationException("Cannot position Dock background.");
            if (_bounds?.Width != bounds.Width || _bounds?.Height != bounds.Height)
            {
                var size = new Vector2(bounds.Width, bounds.Height);
                _root!.Size = size;
                _glass!.Size = size;
                _decoration!.Size = size;
            }
            _bounds = bounds;
        }
    }

    internal void Update(System.Windows.Rect rect, double radiusX, double radiusY, double opacity, Color fill, Color border)
    {
        _geometry!.Offset = new Vector2((float)rect.X + 0.5f, (float)rect.Y + 0.5f);
        _geometry.Size = new Vector2((float)Math.Max(0, rect.Width - 1), (float)Math.Max(0, rect.Height - 1));
        _geometry.CornerRadius = new Vector2((float)radiusX, (float)radiusY);
        _root!.Opacity = (float)Math.Clamp(opacity, 0, 1);
        _tint!.Color = global::Windows.UI.Color.FromArgb((byte)Math.Round(fill.A * 0.24), fill.R, fill.G, fill.B);
        _stroke!.Color = global::Windows.UI.Color.FromArgb(border.A, border.R, border.G, border.B);
    }

    public void Dispose()
    {
        if (_target is not null) _target.Root = null;
        _root?.Dispose();
        _geometry?.Dispose();
        _tint?.Dispose();
        _stroke?.Dispose();
        _target?.Dispose();
        _compositor?.Dispose();
        if (_window != 0) { NativeMethods.DestroyWindow(_window); _window = 0; }
    }

    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam) => message switch
    {
        0x84 => -1, // HTTRANSPARENT: content and its WPF surface own all input.
        0x21 => 3,  // MA_NOACTIVATE
        _ => NativeMethods.DefWindowProc(hwnd, message, wParam, lParam)
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct QueueOptions { internal int Size, ThreadType, ApartmentType; }
    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(QueueOptions options, out nint controller);
    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        [PreserveSig] int CreateDesktopWindowTarget(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out nint target);
        [PreserveSig] int EnsureOnThread(uint threadId);
    }
}
