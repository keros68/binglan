using System.Buffers;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImageW(
        nint hInst,
        string name,
        uint type,
        int cx,
        int cy,
        uint fuLoad);

    [DllImport("user32.dll", EntryPoint = "PrivateExtractIconsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PrivateExtractIconsW(
        string fileName,
        int iconIndex,
        int cxIcon,
        int cyIcon,
        [Out] nint[] phicon,
        [Out] uint[] piconid,
        uint nIcons,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int MultiByteToWideChar(
        uint codePage,
        uint flags,
        byte[] multiByteStr,
        int cbMultiByte,
        [Out] char[]? wideCharStr,
        int cchWideChar);

    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const int MaximumRenderedIconPixels = 128;
    private const int MaxSystemIconCacheEntries = 512;

    private static readonly object SystemIconCacheGate = new();
    private static readonly Dictionary<int, WeakReference<BitmapSource>> SystemIconCache = new();

    internal static int CachedSystemIconCount
    {
        get
        {
            lock (SystemIconCacheGate)
            {
                var live = 0;
                foreach (var entry in SystemIconCache.Values)
                {
                    if (entry.TryGetTarget(out _))
                    {
                        live++;
                    }
                }
                return live;
            }
        }
    }

    /// <summary>
    /// Clears the process-wide system image-list icon cache (e.g. after Explorer restarts).
    /// </summary>
    internal static void ClearIconCache()
    {
        lock (SystemIconCacheGate)
        {
            SystemIconCache.Clear();
        }
    }

    /// <summary>
    /// Evicts only the system image-list cache entry corresponding to <paramref name="path"/>
    /// so refreshing a single tile icon does not flush unrelated cached icons.
    /// </summary>
    internal static void InvalidateIconForPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        int? iconIndex = null;
        if (FileMappingService.IsShellEntry(path))
        {
            if (SHParseDisplayName(path, 0, out var itemIdList, 0, out _) == 0 && itemIdList != 0)
            {
                try
                {
                    var result = SHGetFileInfo(
                        itemIdList,
                        0,
                        out var info,
                        (uint)Marshal.SizeOf<ShellFileInfo>(),
                        DockNativeMethods.ShgfiSysIconIndex | DockNativeMethods.ShgfiPidl);
                    if (result != 0)
                    {
                        iconIndex = info.IconIndex;
                    }
                }
                finally
                {
                    ILFree(itemIdList);
                }
            }
        }
        else if (File.Exists(path) || Directory.Exists(path))
        {
            var result = SHGetFileInfo(
                path,
                0,
                out var info,
                (uint)Marshal.SizeOf<ShellFileInfo>(),
                DockNativeMethods.ShgfiSysIconIndex);
            if (result != 0)
            {
                iconIndex = info.IconIndex;
            }
        }

        if (iconIndex is { } index)
        {
            lock (SystemIconCacheGate)
            {
                SystemIconCache.Remove(index);
            }
        }
    }

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

        if (Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            // Steam games and many launchers create .url shortcuts whose IconFile points to
            // a local .ico/.exe. Windows Shell's .url icon handler is STA-only and often
            // fails on background MTA threads or after browser overrides; reading IconFile
            // directly returns the game's real icon reliably on any thread.
            if (TryGetInternetShortcutIcon(path) is { } shortcutIcon)
            {
                return shortcutIcon;
            }
        }

        var result = SHGetFileInfo(
            path,
            0,
            out var info,
            (uint)Marshal.SizeOf<ShellFileInfo>(),
            DockNativeMethods.ShgfiSysIconIndex);
        if (result != 0 && FromSystemImageList(info.IconIndex) is { } systemIcon)
        {
            return systemIcon;
        }

        var fallbackResult = SHGetFileInfo(
            path,
            0,
            out var fallbackInfo,
            (uint)Marshal.SizeOf<ShellFileInfo>(),
            ShgfiIcon | ShgfiLargeIcon);
        return fallbackResult != 0 && fallbackInfo.IconHandle != 0
            ? ToImageSource(fallbackInfo.IconHandle)
            : null;
    }

    private static ImageSource? TryGetInternetShortcutIcon(string urlPath)
    {
        try
        {
            var fileInfo = new FileInfo(urlPath);
            if (!fileInfo.Exists || fileInfo.Length == 0 || fileInfo.Length > InternetShortcutRules.MaximumShortcutBytes)
            {
                return null;
            }

            var bytes = File.ReadAllBytes(urlPath);
            var text = DecodeShortcutText(bytes);
            var parsed = InternetShortcutRules.ParseIconLocation(text, Path.GetDirectoryName(urlPath));
            if (parsed is not { } target)
            {
                return null;
            }

            var ext = Path.GetExtension(target.FilePath);
            if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase))
            {
                return TryLoadRasterImage(target.FilePath);
            }

            return GetLocalIconResource(target.FilePath, target.Index);
        }
        catch
        {
            return null;
        }
    }

    internal static ImageSource? GetLocalIconResource(string path, int index)
    {
        if (!Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal)
            || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase)
                ? TryLoadIconFile(path)
                : TryExtractIndexedIcon(path, index);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or NotSupportedException or COMException or Win32Exception)
        {
            return null;
        }
    }

    private static string DecodeShortcutText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Steam writes .url files under Chinese installation folders using the active
            // Windows ANSI code page (e.g. GBK / CP936); decode via CP_ACP (0).
            var needed = MultiByteToWideChar(0, 0, bytes, bytes.Length, null, 0);
            if (needed <= 0)
            {
                return Encoding.UTF8.GetString(bytes);
            }
            var chars = new char[needed];
            var written = MultiByteToWideChar(0, 0, bytes, bytes.Length, chars, chars.Length);
            return written > 0 ? new string(chars, 0, written) : Encoding.UTF8.GetString(bytes);
        }
    }

    private static ImageSource? TryLoadIconFile(string iconPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(iconPath);
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var bestFrame = decoder.Frames
                .OrderByDescending(frame => frame.PixelWidth * frame.PixelHeight)
                .ThenByDescending(frame => frame.Format.BitsPerPixel)
                .FirstOrDefault();
            if (bestFrame is not null && bestFrame.PixelWidth > 0 && bestFrame.PixelHeight > 0)
            {
                return NormalizeLoadedBitmap(bestFrame);
            }
        }
        catch
        {
            // Fall through to Win32 LoadImageW for non-standard .ico containers.
        }

        var handle = LoadImageW(0, iconPath, ImageIcon, MaximumRenderedIconPixels, MaximumRenderedIconPixels, LrLoadFromFile);
        if (handle == 0)
        {
            handle = LoadImageW(0, iconPath, ImageIcon, 0, 0, LrLoadFromFile);
        }
        return handle != 0 ? ToImageSource(handle) : null;
    }

    private static ImageSource? TryLoadRasterImage(string imagePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(imagePath);
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = MaximumRenderedIconPixels;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryExtractIndexedIcon(string modulePath, int iconIndex)
    {
        var icons = new nint[1];
        var ids = new uint[1];
        foreach (var size in new[] { MaximumRenderedIconPixels, 48 })
        {
            var extracted = PrivateExtractIconsW(modulePath, iconIndex, size, size, icons, ids, 1, 0);
            if (extracted > 0 && extracted != uint.MaxValue && icons[0] != 0)
            {
                return ToImageSource(icons[0]);
            }
        }
        return null;
    }

    private static BitmapSource NormalizeLoadedBitmap(BitmapSource frame)
    {
        BitmapSource result = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
        var maxSide = Math.Max(result.PixelWidth, result.PixelHeight);
        if (maxSide > MaximumRenderedIconPixels)
        {
            var scale = (double)MaximumRenderedIconPixels / maxSide;
            result = new TransformedBitmap(result, new ScaleTransform(scale, scale));
        }
        var materialized = new WriteableBitmap(result);
        materialized.Freeze();
        return materialized;
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
    // 按 sysIconIndex 缓存冻结后的位图，同类文件与文件夹共享同一份内存实例。
    internal static ImageSource? FromSystemImageList(int iconIndex)
    {
        lock (SystemIconCacheGate)
        {
            if (SystemIconCache.TryGetValue(iconIndex, out var cachedRef)
                && cachedRef.TryGetTarget(out var cached))
            {
                return cached;
            }
        }

        BitmapSource? jumboFallback = null;
        BitmapSource? resolved = null;
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
                    if (listSize == DockNativeMethods.ShilJumbo)
                    {
                        var (source, isPlaceholder) = ExtractJumboOrDetectPlaceholder(iconHandle);
                        if (isPlaceholder)
                        {
                            jumboFallback = source;
                            continue;
                        }
                        resolved = source;
                        break;
                    }

                    resolved = ToImageSource(iconHandle);
                    break;
                }
            }
            finally
            {
                Marshal.Release(imageList);
            }
        }

        if (resolved is null && jumboFallback is not null)
        {
            resolved = jumboFallback.PixelWidth == JumboSize && jumboFallback.PixelHeight == JumboSize
                ? HalveJumbo(jumboFallback)
                : jumboFallback;
            if (!resolved.IsFrozen && resolved.CanFreeze)
            {
                resolved.Freeze();
            }
        }

        return resolved is not null ? GetOrStoreSystemIcon(iconIndex, resolved) : null;
    }

    private static BitmapSource GetOrStoreSystemIcon(int iconIndex, BitmapSource icon)
    {
        lock (SystemIconCacheGate)
        {
            if (SystemIconCache.TryGetValue(iconIndex, out var existingRef)
                && existingRef.TryGetTarget(out var existing))
            {
                return existing;
            }

            if (SystemIconCache.Count >= MaxSystemIconCacheEntries)
            {
                PruneDeadSystemIconEntriesLocked();
                if (SystemIconCache.Count >= MaxSystemIconCacheEntries)
                {
                    var evictKey = SystemIconCache.Keys.First();
                    SystemIconCache.Remove(evictKey);
                }
            }

            SystemIconCache[iconIndex] = new WeakReference<BitmapSource>(icon);
            return icon;
        }
    }

    private static void PruneDeadSystemIconEntriesLocked()
    {
        List<int>? deadKeys = null;
        foreach (var pair in SystemIconCache)
        {
            if (!pair.Value.TryGetTarget(out _))
            {
                deadKeys ??= [];
                deadKeys.Add(pair.Key);
            }
        }
        if (deadKeys is not null)
        {
            foreach (var key in deadKeys)
            {
                SystemIconCache.Remove(key);
            }
        }
    }

    // Decodes a Jumbo HICON once into a pooled buffer, checks the top-left-quarter placeholder
    // pattern BEFORE downsampling, and skips the 2×2 halving pass when falling back to ExtraLarge.
    private static (BitmapSource Source, bool IsPlaceholder) ExtractJumboOrDetectPlaceholder(nint iconHandle)
    {
        BitmapSource raw;
        try
        {
            raw = Imaging.CreateBitmapSourceFromHIcon(
                iconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            DestroyIcon(iconHandle);
        }

        if (raw.PixelWidth != JumboSize || raw.PixelHeight != JumboSize)
        {
            var isPlaceholder = IsJumboPlaceholder(raw);
            raw.Freeze();
            return (raw, isPlaceholder);
        }

        var converted = new FormatConvertedBitmap(raw, PixelFormats.Pbgra32, null, 0);
        const int stride = JumboSize * 4;
        const int byteCount = stride * JumboSize;
        var pixels = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            converted.CopyPixels(pixels, stride, 0);
            if (IsTopLeftQuarterOnly(pixels, stride, JumboSize, JumboSize))
            {
                raw.Freeze();
                return (raw, true);
            }

            var halved = HalveJumboPixels(pixels, stride);
            halved.Freeze();
            return (halved, false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    internal static bool IsJumboPlaceholder(BitmapSource source)
    {
        // 仅识别图案全部局限于左上四分之一区域的 Shell 占位帧；保留居中图案的原有留白。
        // 扫描限于系统 Jumbo 帧（256×256），不裁剪资源，也不扩大到任意用户图片。
        var rgba = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = rgba.PixelWidth;
        var height = rgba.PixelHeight;
        var stride = width * 4;
        var byteCount = stride * height;
        var pixels = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            rgba.CopyPixels(pixels, stride, 0);
            return IsTopLeftQuarterOnly(pixels, stride, width, height);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private static bool IsTopLeftQuarterOnly(byte[] pixels, int stride, int width, int height)
    {
        var halfWidth = width / 2;
        var halfHeight = height / 2;
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            var startX = y < halfHeight ? halfWidth : 0;
            for (var x = startX; x < width; x++)
            {
                if (pixels[rowOffset + x * 4 + 3] != 0)
                {
                    return false;
                }
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
        const int stride = JumboSize * 4;
        const int byteCount = stride * JumboSize;
        var pixels = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            converted.CopyPixels(pixels, stride, 0);
            return HalveJumboPixels(pixels, stride);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private static BitmapSource HalveJumboPixels(byte[] pixels, int stride)
    {
        const int half = JumboSize / 2;
        const int halfStride = half * 4;
        const int outputByteCount = halfStride * half;
        var output = ArrayPool<byte>.Shared.Rent(outputByteCount);
        try
        {
            for (var y = 0; y < half; y++)
            {
                var top = y * 2 * stride;
                var bottom = top + stride;
                var targetRow = y * halfStride;
                for (var x = 0; x < half; x++)
                {
                    var left = x * 8;
                    var target = targetRow + x * 4;
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
        finally
        {
            ArrayPool<byte>.Shared.Return(output);
        }
    }
}
