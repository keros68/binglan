using System.Runtime.InteropServices;
using Color = System.Windows.Media.Color;
using BingLan.App.Interop;
using BingLan.Core.Dock;

namespace BingLan.App.Dock;

/// <summary>A native visual layer behind one WPF surface; it never accepts input.</summary>
internal sealed class DockCompositionBackdrop : IDisposable
{
    private static readonly NativeMethods.WindowProcedure Procedure = WindowProc;
    private static bool _classRegistered;
    private nint _window;
    private nint _compositionState;
    private PixelRect? _bounds;
    private bool? _topmost;

    internal DockCompositionBackdrop()
    {
        try
        {
            if (!_classRegistered)
            {
                var cls = new NativeMethods.WindowClass
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.WindowClass>(),
                    Procedure = Procedure,
                    Name = "BingLanDockComposition"
                };
                if (NativeMethods.RegisterClassEx(ref cls) == 0)
                    throw new InvalidOperationException("Cannot register Dock background.");
                _classRegistered = true;
            }
            var exStyle = (uint)(NativeMethods.WsExNoRedirectionBitmap | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate);
            _window = NativeMethods.CreateWindowEx(exStyle, "BingLanDockComposition", "冰蓝 Dock 玻璃背景", 0x80000000,
                0, 0, 1, 1, 0, 0, 0, 0); // WS_POPUP, with pixels supplied only by Composition.
            if (_window == 0) throw new InvalidOperationException("Cannot create Dock background.");
            var enabled = 1;
            Marshal.ThrowExceptionForHR(NativeMethods.DwmSetWindowAttribute(_window, NativeMethods.DwmwaUseHostBackdropBrush, ref enabled, sizeof(int)));
            Marshal.ThrowExceptionForHR(BingLanBackdrop_Attach(_window, out _compositionState));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal nint Handle => _window;
    internal PixelRect? Bounds => _bounds;
    internal bool IsShown { get; private set; }

    internal void Position(nint content, PixelRect bounds, bool reorder)
    {
        if (_window == 0 || _compositionState == 0)
        {
            return;
        }
        var topmost = (NativeMethods.GetExStyleOf(content) & NativeMethods.WsExTopmost) != 0;
        if (_topmost != topmost)
        {
            NativeMethods.SetWindowPos(_window, topmost ? -1 : -2, 0, 0, 0, 0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
            _topmost = topmost;
            reorder = true;
        }
        if (_bounds != bounds || reorder || !IsShown)
        {
            var flags = NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow;
            if (_bounds == bounds) flags |= NativeMethods.SwpNoMove | NativeMethods.SwpNoSize;
            BingLan.App.Services.UiDiagnostics.Hit(BingLan.App.Services.UiActivity.GlassMove);
            if (!NativeMethods.SetWindowPos(_window, content, bounds.Left, bounds.Top, bounds.Width, bounds.Height, flags))
                throw new InvalidOperationException("Cannot position Dock background.");
            if (_bounds?.Width != bounds.Width || _bounds?.Height != bounds.Height)
            {
                BingLanBackdrop_Resize(_compositionState, bounds.Width, bounds.Height);
            }
            _bounds = bounds;
            IsShown = true;
        }
    }

    internal void Hide()
    {
        if (_window != 0 && IsShown)
        {
            NativeMethods.SetWindowPos(_window, 0, 0, 0, 0, 0,
                NativeMethods.SwpHideWindow | NativeMethods.SwpNoActivate | NativeMethods.SwpNoMove
                | NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder);
            IsShown = false;
        }
    }

    internal void Update(System.Windows.Rect rect, double radiusX, double radiusY, double opacity, Color fill, Color border)
    {
        if (_compositionState == 0)
        {
            return;
        }
        BingLanBackdrop_Update(
            _compositionState,
            (float)rect.X + 0.5f,
            (float)rect.Y + 0.5f,
            (float)Math.Max(0, rect.Width - 1),
            (float)Math.Max(0, rect.Height - 1),
            (float)radiusX,
            (float)radiusY,
            (float)Math.Clamp(opacity, 0, 1),
            (byte)Math.Round(fill.A * 0.24),
            fill.R,
            fill.G,
            fill.B,
            border.A,
            border.R,
            border.G,
            border.B);
    }

    public void Dispose()
    {
        IsShown = false;
        if (_compositionState != 0)
        {
            BingLanBackdrop_Destroy(_compositionState);
            _compositionState = 0;
        }
        if (_window != 0)
        {
            NativeMethods.DestroyWindow(_window);
            _window = 0;
        }
    }

    private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam) => message switch
    {
        0x84 => -1, // HTTRANSPARENT: content and its WPF surface own all input.
        0x21 => 3,  // MA_NOACTIVATE
        _ => NativeMethods.DefWindowProc(hwnd, message, wParam, lParam)
    };

    [DllImport("BingLan.Composition.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int BingLanBackdrop_Attach(nint hwnd, out nint outState);

    [DllImport("BingLan.Composition.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern void BingLanBackdrop_Resize(nint state, float width, float height);

    [DllImport("BingLan.Composition.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern void BingLanBackdrop_Update(
        nint state,
        float x,
        float y,
        float width,
        float height,
        float radiusX,
        float radiusY,
        float opacity,
        byte fillA,
        byte fillR,
        byte fillG,
        byte fillB,
        byte borderA,
        byte borderR,
        byte borderG,
        byte borderB);

    [DllImport("BingLan.Composition.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern void BingLanBackdrop_Destroy(nint state);
}
