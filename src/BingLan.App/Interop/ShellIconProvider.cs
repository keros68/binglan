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
    // 系统图像列表能给出图标自带的最大帧，显示端再由 WPF 高质量缩小。
    private static ImageSource? FromSystemImageList(int iconIndex)
    {
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
                    return ToImageSource(iconHandle);
                }
            }
            finally
            {
                Marshal.Release(imageList);
            }
        }

        return null;
    }

    private static ImageSource? ToImageSource(nint iconHandle)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                iconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }
}
