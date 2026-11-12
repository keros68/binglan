using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Color = System.Windows.Media.Color;

namespace BingLan.App.Interop;

/// <summary>Native Win32 ChooseColorW wrapper that preserves custom swatches across the session.</summary>
internal static class NativeColorDialog
{
    private const uint CcRgbInit = 0x00000001;
    private const uint CcFullOpen = 0x00000002;
    private const uint CcAnyColor = 0x00000100;

    // Preserves the 16 custom color slots across dialog openings during the session.
    private static readonly uint[] CustomColors = Enumerable.Repeat(0x00FFFFFFu, 16).ToArray();

    internal static bool TryPickColor(Window? owner, Color initial, out string hexColor)
    {
        var customHandle = GCHandle.Alloc(CustomColors, GCHandleType.Pinned);
        try
        {
            var data = new ChooseColorData
            {
                StructSize = (uint)Marshal.SizeOf<ChooseColorData>(),
                Owner = owner is null ? 0 : new WindowInteropHelper(owner).Handle,
                RgbResult = (uint)(initial.R | (initial.G << 8) | (initial.B << 16)),
                CustomColors = customHandle.AddrOfPinnedObject(),
                Flags = CcRgbInit | CcFullOpen | CcAnyColor
            };

            if (!ChooseColorW(ref data))
            {
                hexColor = $"#{initial.R:X2}{initial.G:X2}{initial.B:X2}";
                return false;
            }

            var r = (byte)(data.RgbResult & 0xFF);
            var g = (byte)((data.RgbResult >> 8) & 0xFF);
            var b = (byte)((data.RgbResult >> 16) & 0xFF);
            hexColor = $"#{r:X2}{g:X2}{b:X2}";
            return true;
        }
        finally
        {
            customHandle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChooseColorData
    {
        internal uint StructSize;
        internal nint Owner;
        internal nint Instance;
        internal uint RgbResult;
        internal nint CustomColors;
        internal uint Flags;
        internal nint CustData;
        internal nint Hook;
        internal nint TemplateName;
    }

    [DllImport("comdlg32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChooseColorW(ref ChooseColorData chooseColor);
}
