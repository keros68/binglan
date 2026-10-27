using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BingLan.Core.Services;

namespace BingLan.App.Interop;

internal static class ShellIconProvider
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint IconHandle;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(
        string path,
        uint fileAttributes,
        out ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(
        nint itemIdList,
        uint fileAttributes,
        out ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string name,
        nint bindContext,
        out nint itemIdList,
        uint attributesIn,
        out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern void ILFree(nint itemIdList);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint iconHandle);

    public static ImageSource? GetIcon(string path)
    {
        if (FileMappingService.IsShellEntry(path))
        {
            return GetShellNamespaceIcon(path);
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return null;
        }

        var result = SHGetFileInfo(
            path,
            0,
            out var info,
            (uint)Marshal.SizeOf<ShellFileInfo>(),
            DockNativeMethods.ShgfiSysIconIndex);
        return result != 0 ? FromSystemImageList(info.IconIndex) : null;
    }

    private static ImageSource? GetShellNamespaceIcon(string shellPath)
    {
        if (SHParseDisplayName(shellPath, 0, out var itemIdList, 0, out _) != 0 || itemIdList == 0)
        {
            return null;
        }

        try
        {
            var result = SHGetFileInfo(
                itemIdList,
                0,
                out var info,
                (uint)Marshal.SizeOf<ShellFileInfo>(),
                DockNativeMethods.ShgfiSysIconIndex | DockNativeMethods.ShgfiPidl);
            return result != 0 ? FromSystemImageList(info.IconIndex) : null;
        }
        finally
        {
            ILFree(itemIdList);
        }
    }

    // 卡片网格在高 DPI 下把图标画到 60+ 物理像素，SHGFI_LARGEICON 只有 32px 帧会被拉糊；
    // 优先取真正的高清帧；旧图标在 Jumbo 中可能只填左上角，其余是透明占位。
    private static ImageSource? FromSystemImageList(int iconIndex)
    {
        BitmapSource? fallback = null;
        foreach (var listSize in new[] { DockNativeMethods.ShilJumbo, DockNativeMethods.ShilExtraLarge })
        {
            if (DockNativeMethods.SHGetImageList(
                    listSize,
                    DockNativeMethods.ImageListId,
                    out var imageList) != 0
                || imageList == 0)
            {
                continue;
            }

            try
            {
                var iconHandle = DockNativeMethods.ImageList_GetIcon(
                    imageList,
                    iconIndex,
                    DockNativeMethods.IldTransparent);
                if (iconHandle != 0)
                {
                    var source = ToImageSource(iconHandle);
                    if (listSize == DockNativeMethods.ShilJumbo && IsJumboPlaceholder(source))
                    {
                        fallback = source;
                        continue;
                    }
                    return source;
                }
            }
            finally
            {
                Marshal.Release(imageList);
            }
        }

        return fallback;
    }

    internal static bool IsJumboPlaceholder(BitmapSource source)
    {
        // 仅识别图案全部局限于左上四分之一区域的 Shell 占位帧；保留居中图案的原有留白。
        // 扫描限于系统 Jumbo 帧（256×256），不裁剪资源，也不扩大到任意用户图片。
        var rgba = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = rgba.PixelWidth * 4;
        var pixels = new byte[stride * rgba.PixelHeight];
        rgba.CopyPixels(pixels, stride, 0);
        for (var y = 0; y < rgba.PixelHeight; y++)
        {
            for (var x = y < rgba.PixelHeight / 2 ? rgba.PixelWidth / 2 : 0; x < rgba.PixelWidth; x++)
            {
                if (pixels[y * stride + x * 4 + 3] != 0) return false;
            }
        }
        return true;
    }

    private static BitmapSource ToImageSource(nint iconHandle)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                iconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            var result = source.PixelWidth == JumboSize && source.PixelHeight == JumboSize
                ? HalveJumbo(source)
                : source;
            result.Freeze();
            return result;
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    private const int JumboSize = 256;

    // Icons are drawn at most ~43 DIP × 1.12 hover zoom (≈ 96 px at 200 % scaling), so
    // 128 px keeps every displayed size a downscale while holding a quarter of the memory.
    // A 2×2 average on premultiplied pixels is exact for halving and needs no UI thread.
    internal static BitmapSource HalveJumbo(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var stride = JumboSize * 4;
        var pixels = new byte[stride * JumboSize];
        converted.CopyPixels(pixels, stride, 0);
        const int half = JumboSize / 2;
        var halfStride = half * 4;
        var output = new byte[halfStride * half];
        for (var y = 0; y < half; y++)
        {
            var top = y * 2 * stride;
            var bottom = top + stride;
            for (var x = 0; x < half; x++)
            {
                var left = x * 8;
                var target = y * halfStride + x * 4;
                for (var channel = 0; channel < 4; channel++)
                {
                    var sum = pixels[top + left + channel] + pixels[top + left + 4 + channel]
                        + pixels[bottom + left + channel] + pixels[bottom + left + 4 + channel];
                    output[target + channel] = (byte)((sum + 2) / 4);
                }
            }
        }
        return BitmapSource.Create(half, half, 96, 96, PixelFormats.Pbgra32, null, output, halfStride);
    }
}
