using System.Runtime.InteropServices;

namespace BingLan.App.Interop;

/// <summary>
/// Minimal GDI bindings for reading a 1-pixel strip from the desktop composition surface
/// when a low-opacity top bar or dock needs to adapt its ink contrast to the wallpaper.
/// </summary>
internal static class BackdropSamplingNativeMethods
{
    internal const uint Srccopy = 0x00CC0020u;
    internal const uint DibRgbColors = 0u;
    internal const uint BiRgb = 0u;

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Color0;
    }

    [DllImport("user32.dll")]
    internal static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateDIBSection(
        nint hdc,
        ref BitmapInfo pbmi,
        uint usage,
        out nint ppvBits,
        nint hSection,
        uint offset);

    [DllImport("gdi32.dll")]
    internal static extern nint SelectObject(nint hdc, nint h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BitBlt(
        nint hdcDest,
        int xDest,
        int yDest,
        int width,
        int height,
        nint hdcSrc,
        int xSrc,
        int ySrc,
        uint rop);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint ho);
}
