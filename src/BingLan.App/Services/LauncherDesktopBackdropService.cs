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
    private static readonly object WallpaperLock = new();
    private static string? _cachedPath;
    private static DateTime _cachedWriteTimeUtc;
    private static BitmapSource? _cachedWallpaper;

    internal static Task<BitmapSource?> TryLoadWallpaperCropAsync(
        PixelRect monitorBounds,
        PixelRect panelBounds,
        CancellationToken token) =>
        Task.Run(() => TryLoadWallpaperCrop(monitorBounds, panelBounds, token), token);

    private static BitmapSource? TryLoadWallpaperCrop(
        PixelRect monitorBounds,
        PixelRect panelBounds,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = ReadDesktopWallpaperPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        BitmapSource? full;
        try
        {
            var writeUtc = File.GetLastWriteTimeUtc(path);
            lock (WallpaperLock)
            {
                if (_cachedWallpaper is not null
                    && string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase)
                    && _cachedWriteTimeUtc == writeUtc)
                {
                    full = _cachedWallpaper;
                }
                else
                {
                    token.ThrowIfCancellationRequested();
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count == 0) return null;
                    var frame = decoder.Frames[0];
                    frame.Freeze();
                    _cachedPath = path;
                    _cachedWriteTimeUtc = writeUtc;
                    _cachedWallpaper = frame;
                    full = frame;
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
