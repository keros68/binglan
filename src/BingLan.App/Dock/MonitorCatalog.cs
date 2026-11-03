using System.Runtime.InteropServices;
using BingLan.App.Interop;
using BingLan.Core.Dock;

namespace BingLan.App.Dock;

internal sealed record MonitorSnapshot(
    nint MonitorHandle,
    string DeviceName,
    PixelRect Bounds,
    PixelRect WorkingArea,
    bool IsPrimary,
    uint Dpi)
{
    internal string Label =>
        $"{(IsPrimary ? "主显示器" : "显示器")} {DeviceName.Replace("\\\\.\\", string.Empty)} · {Bounds.Width}×{Bounds.Height} · {Dpi * 100 / 96}%";
}

internal static class MonitorCatalog
{
    /// <summary>The live work area of one monitor, without enumerating all of them.</summary>
    internal static PixelRect? TryGetWorkingArea(nint monitor)
    {
        var info = new DockNativeMethods.MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<DockNativeMethods.MonitorInfo>(),
            DeviceName = string.Empty
        };
        return monitor != 0 && DockNativeMethods.GetMonitorInfoW(monitor, ref info)
            ? ToPixelRect(info.WorkArea)
            : null;
    }

    internal static IReadOnlyList<MonitorSnapshot> GetAll()
    {
        var monitors = new List<MonitorSnapshot>();
        DockNativeMethods.EnumDisplayMonitors(0, 0, AddMonitor, 0);

        return monitors
            .OrderByDescending(screen => screen.IsPrimary)
            .ThenBy(screen => screen.Bounds.Left)
            .ThenBy(screen => screen.Bounds.Top)
            .ToArray();

        bool AddMonitor(
            nint monitor,
            nint deviceContext,
            ref DockNativeMethods.NativeRect monitorRectangle,
            nint data)
        {
            var info = new DockNativeMethods.MonitorInfo
            {
                Size = (uint)Marshal.SizeOf<DockNativeMethods.MonitorInfo>(),
                DeviceName = string.Empty
            };
            if (!DockNativeMethods.GetMonitorInfoW(monitor, ref info))
            {
                return true;
            }

            monitors.Add(new MonitorSnapshot(
                monitor,
                info.DeviceName,
                ToPixelRect(info.Monitor),
                ToPixelRect(info.WorkArea),
                (info.Flags & DockNativeMethods.MonitorInfoPrimary) != 0,
                GetDpi(monitor)));
            return true;
        }
    }

    private static uint GetDpi(nint monitor)
    {
        if (monitor != 0
            && DockNativeMethods.GetDpiForMonitor(
                monitor,
                DockNativeMethods.MdtEffectiveDpi,
                out var dpiX,
                out _) == 0
            && dpiX > 0)
        {
            return dpiX;
        }

        return 96;
    }

    private static PixelRect ToPixelRect(DockNativeMethods.NativeRect rectangle) =>
        new(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
}
