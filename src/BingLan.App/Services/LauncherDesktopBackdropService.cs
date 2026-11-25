using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using BingLan.App.Interop;
using BingLan.Core.Dock;
using BingLan.Core.Models;

namespace BingLan.App.Services;

/// <summary>
/// Provides optional backdrop isolation for the Ice Blue Overview Card:
/// either cropping the static Windows desktop wallpaper into the panel's bounds,
/// or minimizing other normal windows while the panel is open (leaving live dynamic
/// wallpapers and BingLan's own surfaces untouched) and restoring them on dismiss.
/// </summary>
internal static class LauncherDesktopBackdropService
{
    private static readonly TimeSpan DefaultWallpaperReleaseDelay = TimeSpan.FromSeconds(5);
    private static readonly object WallpaperLock = new();
    private static string? _cachedPath;
    private static DateTime _cachedWriteTimeUtc;
    private static int _cachedMonitorWidth;
    private static int _cachedMonitorHeight;
    private static WeakReference<BitmapSource>? _cachedWallpaper;
    private static BitmapSource? _pinnedWallpaper;
    private static System.Threading.Timer? _releaseTimer;
    private static long _releaseGeneration;

    internal static bool HasPinnedWallpaper
    {
        get
        {
            lock (WallpaperLock)
            {
                return _pinnedWallpaper is not null;
            }
        }
    }

    internal static void ScheduleReleaseWallpaperCache(TimeSpan? delay = null)
    {
        var effectiveDelay = delay ?? DefaultWallpaperReleaseDelay;
        lock (WallpaperLock)
        {
            _releaseGeneration++;
            if (effectiveDelay <= TimeSpan.Zero)
            {
                CancelReleaseTimerLocked();
                _pinnedWallpaper = null;
                return;
            }

            // Always arm the release timer even if _pinnedWallpaper is currently null so an
            // off-lock background decode finishing after panel close cannot stay pinned forever.
            ArmReleaseTimerLocked(effectiveDelay);
        }
    }

    internal static void ReleaseWallpaperCache()
    {
        lock (WallpaperLock)
        {
            _releaseGeneration++;
            CancelReleaseTimerLocked();
            _pinnedWallpaper = null;
        }
    }

    private static void ArmReleaseTimerLocked(TimeSpan delay)
    {
        _releaseTimer ??= new System.Threading.Timer(
            static _ => ReleaseWallpaperCache(),
            null,
            Timeout.Infinite,
            Timeout.Infinite);
        _releaseTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private static void CancelReleaseTimerLocked() =>
        _releaseTimer?.Change(Timeout.Infinite, Timeout.Infinite);

    internal static int ComputeTargetDecodeWidth(int imageWidth, int imageHeight, int monitorWidth, int monitorHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || monitorWidth <= 0 || monitorHeight <= 0)
        {
            return 0;
        }

        // UniformToFill scales the wallpaper so both axes cover the monitor; never decode larger than needed.
        var scale = Math.Max((double)monitorWidth / imageWidth, (double)monitorHeight / imageHeight);
        if (scale >= 1d)
        {
            return 0;
        }

        var targetWidth = (int)Math.Ceiling(imageWidth * scale);
        return targetWidth > 0 && targetWidth < imageWidth ? targetWidth : 0;
    }

    internal static Task<BitmapSource?> TryLoadWallpaperCropAsync(
        PixelRect monitorBounds,
        PixelRect panelBounds,
        CancellationToken token) =>
        Task.Run(() => TryLoadWallpaperCrop(monitorBounds, panelBounds, token), token);

    internal static BitmapSource? TryLoadWallpaperCropFromFileForTesting(
        string path,
        PixelRect monitorBounds,
        PixelRect panelBounds,
        CancellationToken token) =>
        TryLoadWallpaperCropFromPath(path, monitorBounds, panelBounds, token);

    private static BitmapSource? TryLoadWallpaperCrop(
        PixelRect monitorBounds,
        PixelRect panelBounds,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = ReadDesktopWallpaperPath();
        return string.IsNullOrWhiteSpace(path)
            ? null
            : TryLoadWallpaperCropFromPath(path, monitorBounds, panelBounds, token);
    }

    private static BitmapSource? TryLoadWallpaperCropFromPath(
        string path,
        PixelRect monitorBounds,
        PixelRect panelBounds,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            return null;
        }

        BitmapSource? full = null;
        try
        {
            // Keep file IO and image decoding completely outside WallpaperLock so UI calls to
            // ScheduleReleaseWallpaperCache / ReleaseWallpaperCache never stall on disk or WIC.
            var writeUtc = File.GetLastWriteTimeUtc(path);
            long generationBeforeDecode;
            lock (WallpaperLock)
            {
                token.ThrowIfCancellationRequested();
                if (_cachedWallpaper is not null
                    && _cachedWallpaper.TryGetTarget(out var cached)
                    && string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase)
                    && _cachedWriteTimeUtc == writeUtc
                    && _cachedMonitorWidth == monitorBounds.Width
                    && _cachedMonitorHeight == monitorBounds.Height)
                {
                    CancelReleaseTimerLocked();
                    _pinnedWallpaper = cached;
                    full = cached;
                }
                generationBeforeDecode = _releaseGeneration;
            }

            if (full is null)
            {
                token.ThrowIfCancellationRequested();
                full = DecodeWallpaperForMonitor(path, monitorBounds);
                if (full is null)
                {
                    return null;
                }

                lock (WallpaperLock)
                {
                    token.ThrowIfCancellationRequested();
                    _cachedPath = path;
                    _cachedWriteTimeUtc = writeUtc;
                    _cachedMonitorWidth = monitorBounds.Width;
                    _cachedMonitorHeight = monitorBounds.Height;
                    _cachedWallpaper = new WeakReference<BitmapSource>(full);
                    if (_releaseGeneration == generationBeforeDecode)
                    {
                        CancelReleaseTimerLocked();
                        _pinnedWallpaper = full;
                    }
                    else
                    {
                        // A release request arrived while decoding off-lock; do not leave pinned.
                        _pinnedWallpaper = null;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }

        token.ThrowIfCancellationRequested();
        var crop = LauncherBackdropRules.ComputeWallpaperCrop(
            full.PixelWidth,
            full.PixelHeight,
            monitorBounds.Left,
            monitorBounds.Top,
            monitorBounds.Width,
            monitorBounds.Height,
            panelBounds.Left,
            panelBounds.Top,
            panelBounds.Width,
            panelBounds.Height);
        if (crop is not { } rect) return null;

        try
        {
            var cropped = new CroppedBitmap(full, new Int32Rect(rect.X, rect.Y, rect.Width, rect.Height));
            cropped.Freeze();
            return cropped;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static BitmapSource? DecodeWallpaperForMonitor(string path, PixelRect monitorBounds)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var probe = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (probe.Frames.Count == 0)
        {
            return null;
        }

        var targetDecodeWidth = ComputeTargetDecodeWidth(
            probe.Frames[0].PixelWidth,
            probe.Frames[0].PixelHeight,
            monitorBounds.Width,
            monitorBounds.Height);
        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (targetDecodeWidth > 0)
        {
            image.DecodePixelWidth = targetDecodeWidth;
        }
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string? ReadDesktopWallpaperPath()
    {
        var buffer = new StringBuilder(1024);
        if (!NativeMethods.SystemParametersInfoW(NativeMethods.SpiGetDeskWallpaper, (uint)buffer.Capacity, buffer, 0))
        {
            return null;
        }
        var path = buffer.ToString().TrimEnd('\0').Trim();
        return path.Length > 0 ? path : null;
    }

    /// <summary>
    /// Minimizes normal visible top-level windows belonging to other applications so the
    /// desktop (including live dynamic wallpapers hosted under WorkerW/Progman) shows cleanly
    /// behind the overview card. Returns the handles minimized so they can be restored on Esc.
    /// </summary>
    internal static IReadOnlyList<nint> MinimizeOtherWindows()
    {
        var ownPid = (uint)Environment.ProcessId;
        var minimized = new List<nint>();
        var classBuffer = new StringBuilder(128);

        DockNativeMethods.EnumWindows((hwnd, _) =>
        {
            if (hwnd == 0 || !DockNativeMethods.IsWindowVisible(hwnd) || DockNativeMethods.IsIconic(hwnd))
            {
                return true;
            }
            if (NativeMethods.IsWindowCloaked(hwnd))
            {
                return true;
            }
            DockNativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == ownPid)
            {
                return true;
            }
            // Skip tool windows, non-activating overlays, and owned popups.
            var exStyle = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle);
            if ((exStyle & (NativeMethods.WsExToolWindow | DockNativeMethods.WsExNoActivate)) != 0)
            {
                return true;
            }
            if (NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner) != 0)
            {
                return true;
            }
            classBuffer.Clear();
            _ = DockNativeMethods.GetClassNameW(hwnd, classBuffer, classBuffer.Capacity);
            var className = classBuffer.ToString();
            if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                return true;
            }
            if (NativeMethods.ShowWindowAsync(hwnd, DockNativeMethods.SwMinimize))
            {
                minimized.Add(hwnd);
            }
            return true;
        }, 0);

        return minimized;
    }

    internal static void RestoreWindows(IReadOnlyList<nint> windows, nint previousForeground)
    {
        // Restore in reverse Z-order (bottom-most first) to preserve stacking.
        for (var i = windows.Count - 1; i >= 0; i--)
        {
            var hwnd = windows[i];
            if (hwnd != 0 && DockNativeMethods.IsWindow(hwnd) && DockNativeMethods.IsIconic(hwnd))
            {
                _ = NativeMethods.ShowWindowAsync(hwnd, DockNativeMethods.SwRestore);
            }
        }
        if (previousForeground != 0 && DockNativeMethods.IsWindow(previousForeground))
        {
            _ = DockNativeMethods.SetForegroundWindow(previousForeground);
        }
    }
}
