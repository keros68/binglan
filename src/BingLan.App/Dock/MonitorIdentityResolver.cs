using System.Runtime.InteropServices;
using BingLan.Core.Dock;
using BingLan.Core.Models;

namespace BingLan.App.Dock;

/// <summary>
/// A connected display with both live Win32 geometry/DPI and a stable hardware-backed
/// <see cref="MonitorKey"/> that survives GDI <c>\\.\DISPLAYn</c> renumbering across reboots
/// and cable replugs.
/// </summary>
internal sealed record ResolvedMonitor(
    string MonitorKey,
    string HardwareId,
    string DeviceName,
    string FriendlyName,
    string DisplayLabel,
    int DisplayIndex,
    nint MonitorHandle,
    PixelRect Bounds,
    PixelRect WorkingArea,
    bool IsPrimary,
    uint Dpi)
{
    /// <summary>
    /// Normalized key used in persisted widget/profile models: null for the primary display,
    /// stable non-empty <see cref="MonitorKey"/> for secondary displays.
    /// </summary>
    internal string? ScreenKey => IsPrimary ? null : MonitorKey;

    internal int ScalePercent => Dpi > 0 ? (int)Math.Round(Dpi * 100d / 96d) : 100;

    internal string ResolutionSummary => $"{Bounds.Width}×{Bounds.Height} · {ScalePercent}%";
}

internal sealed record MonitorHardwareInfo(
    string DevicePath,
    string FriendlyName,
    uint? TargetId = null);

/// <summary>
/// Resolves stable hardware identities for connected monitors on demand.
/// Never invokes <c>QueryDisplayConfig</c> on single-monitor systems or while multi-screen is disabled.
/// </summary>
internal static class MonitorIdentityResolver
{
    private const uint QdcOnlyActivePaths = 0x00000002;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const uint DisplayConfigDeviceInfoGetSourceName = 1;
    private const uint DisplayConfigDeviceInfoGetTargetName = 2;
    private const uint EddGetDeviceInterfaceName = 0x00000001;

    private static int _queryDisplayConfigCallCountForTests;

    /// <summary>
    /// Diagnostic counter incremented every time <c>QueryDisplayConfigActivePaths</c> is entered
    /// (or would be entered in test overrides when <see cref="ResolveConnected"/> resolves >= 2 monitors).
    /// </summary>
    internal static int QueryDisplayConfigCallCountForTests => Volatile.Read(ref _queryDisplayConfigCallCountForTests);

    internal static void ResetQueryDisplayConfigCallCountForTests() =>
        Interlocked.Exchange(ref _queryDisplayConfigCallCountForTests, 0);

    /// <summary>
    /// Optional test hook to supply deterministic virtual multi-monitor topologies without
    /// altering live Windows display settings.
    /// </summary>
    internal static Func<IReadOnlyList<ResolvedMonitor>>? ResolveOverrideForTests { get; set; }

    /// <summary>
    /// Cheap monitor count query without calling <c>QueryDisplayConfig</c>.
    /// </summary>
    internal static int GetConnectedMonitorCount()
    {
        if (ResolveOverrideForTests is { } overrideResolver)
        {
            return overrideResolver().Count;
        }

        return MonitorCatalog.GetAll().Count;
    }

    /// <summary>
    /// Cheap check for whether at least two monitors are currently attached, without calling
    /// <c>QueryDisplayConfig</c>.
    /// </summary>
    internal static bool HasMultipleMonitors() => GetConnectedMonitorCount() >= 2;

    /// <summary>
    /// Returns the GDI device names (e.g. <c>\\.\DISPLAY1</c>) of currently attached secondary
    /// monitors using lightweight <c>EnumDisplayMonitors</c> only (zero <c>QueryDisplayConfig</c> calls).
    /// </summary>
    internal static IReadOnlySet<string> GetConnectedSecondaryDeviceNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ResolveOverrideForTests is { } overrideResolver)
        {
            foreach (var monitor in overrideResolver())
            {
                if (!monitor.IsPrimary && !string.IsNullOrWhiteSpace(monitor.DeviceName))
                {
                    names.Add(monitor.DeviceName.Trim());
                }
            }
            return names;
        }

        foreach (var snapshot in MonitorCatalog.GetAll())
        {
            if (!snapshot.IsPrimary && !string.IsNullOrWhiteSpace(snapshot.DeviceName))
            {
                names.Add(snapshot.DeviceName.Trim());
            }
        }
        return names;
    }

    internal static IReadOnlyList<ResolvedMonitor> ResolveConnected()
    {
        if (ResolveOverrideForTests is { } overrideResolver)
        {
            var resolved = overrideResolver();
            if (resolved.Count >= 2)
            {
                Interlocked.Increment(ref _queryDisplayConfigCallCountForTests);
            }
            return resolved;
        }

        var snapshots = MonitorCatalog.GetAll();
        if (snapshots.Count == 0)
        {
            return [];
        }

        if (snapshots.Count == 1)
        {
            return BuildResolvedMonitors(snapshots, hardwareLookup: null);
        }

        var hardwareLookup = QueryHardwareLookup(snapshots);
        return BuildResolvedMonitors(snapshots, hardwareLookup);
    }

    internal static ResolvedMonitor? FindByPoint(
        IReadOnlyList<ResolvedMonitor> monitors,
        int x,
        int y)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        foreach (var monitor in monitors)
        {
            if (x >= monitor.Bounds.Left
                && x < monitor.Bounds.Right
                && y >= monitor.Bounds.Top
                && y < monitor.Bounds.Bottom)
            {
                return monitor;
            }
        }

        return null;
    }

    internal static ResolvedMonitor? FindByBounds(
        IReadOnlyList<ResolvedMonitor> monitors,
        PixelRect bounds)
    {
        if (monitors.Count == 0)
        {
            return null;
        }

        var centerX = bounds.Left + (bounds.Width / 2);
        var centerY = bounds.Top + (bounds.Height / 2);
        if (FindByPoint(monitors, centerX, centerY) is { } byCenter)
        {
            return byCenter;
        }

        ResolvedMonitor? best = null;
        long bestOverlap = -1;
        foreach (var monitor in monitors)
        {
            var overlapW = Math.Max(0, Math.Min(bounds.Right, monitor.Bounds.Right) - Math.Max(bounds.Left, monitor.Bounds.Left));
            var overlapH = Math.Max(0, Math.Min(bounds.Bottom, monitor.Bounds.Bottom) - Math.Max(bounds.Top, monitor.Bounds.Top));
            var overlap = (long)overlapW * overlapH;
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = monitor;
            }
        }

        return bestOverlap > 0 ? best : monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    internal static ResolvedMonitor? FindByHandle(
        IReadOnlyList<ResolvedMonitor> monitors,
        nint monitorHandle)
    {
        if (monitorHandle == 0)
        {
            return null;
        }

        foreach (var monitor in monitors)
        {
            if (monitor.MonitorHandle == monitorHandle)
            {
                return monitor;
            }
        }

        return null;
    }

    internal static IReadOnlyList<ResolvedMonitor> BuildResolvedMonitors(
        IReadOnlyList<MonitorSnapshot> snapshots,
        IReadOnlyDictionary<string, MonitorHardwareInfo>? hardwareLookup)
    {
        if (snapshots.Count == 0)
        {
            return [];
        }

        var ordered = snapshots
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.Bounds.Left)
            .ThenBy(m => m.Bounds.Top)
            .ToArray();

        // Count duplicate normalized hardware paths ahead of time so identical monitors
        // disambiguate deterministically by QueryDisplayConfig port targetInfo.id first,
        // rather than unstable left-to-right geometry order.
        var hardwareCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var snapshot in ordered)
        {
            if (snapshot.IsPrimary)
            {
                continue;
            }
            if (hardwareLookup is not null
                && !string.IsNullOrWhiteSpace(snapshot.DeviceName)
                && hardwareLookup.TryGetValue(snapshot.DeviceName, out var lookupInfo))
            {
                var norm = NormalizeHardwareMonitorPath(lookupInfo.DevicePath);
                if (!string.IsNullOrEmpty(norm))
                {
                    hardwareCounts[norm] = hardwareCounts.TryGetValue(norm, out var existingCount)
                        ? existingCount + 1
                        : 1;
                }
            }
        }

        var seenKeys = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<ResolvedMonitor>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var snapshot = ordered[index];
            var displayIndex = index + 1;
            var shortGdi = ShortGdiName(snapshot.DeviceName);

            string rawHardwarePath = string.Empty;
            string friendlyName = string.Empty;
            uint? targetId = null;
            if (hardwareLookup is not null
                && !string.IsNullOrWhiteSpace(snapshot.DeviceName)
                && hardwareLookup.TryGetValue(snapshot.DeviceName, out var info))
            {
                rawHardwarePath = info.DevicePath;
                friendlyName = info.FriendlyName;
                targetId = info.TargetId;
            }

            var normalizedHardware = NormalizeHardwareMonitorPath(rawHardwarePath);
            string baseKey;
            if (snapshot.IsPrimary)
            {
                baseKey = MultiScreenRules.PrimaryScreenSentinel;
            }
            else if (!string.IsNullOrEmpty(normalizedHardware))
            {
                baseKey = hardwareCounts.TryGetValue(normalizedHardware, out var dupCount)
                    && dupCount > 1
                    && targetId is { } portId
                        ? $"{normalizedHardware}@PORT:{portId}"
                        : normalizedHardware;
            }
            else
            {
                // Resolution-independent fallback for virtual/RDP monitors without a hardware path.
                baseKey = !string.IsNullOrEmpty(shortGdi)
                    ? $"slot:{shortGdi.ToUpperInvariant()}"
                    : $"slot:{displayIndex}";
            }

            var uniqueKey = baseKey;
            if (!snapshot.IsPrimary)
            {
                if (seenKeys.TryGetValue(baseKey, out var count))
                {
                    count++;
                    seenKeys[baseKey] = count;
                    uniqueKey = $"{baseKey}#{count}";
                }
                else
                {
                    seenKeys[baseKey] = 1;
                }
            }

            var cleanFriendly = string.IsNullOrWhiteSpace(friendlyName) ? string.Empty : friendlyName.Trim();
            var labelSuffix = !string.IsNullOrEmpty(cleanFriendly) ? cleanFriendly : shortGdi;
            var displayLabel = snapshot.IsPrimary
                ? (string.IsNullOrEmpty(labelSuffix) ? "主屏" : $"主屏 · {labelSuffix}")
                : (string.IsNullOrEmpty(labelSuffix) ? $"副屏 {displayIndex}" : $"副屏 {displayIndex} · {labelSuffix}");

            result.Add(new ResolvedMonitor(
                MonitorKey: uniqueKey,
                HardwareId: normalizedHardware,
                DeviceName: snapshot.DeviceName,
                FriendlyName: cleanFriendly,
                DisplayLabel: displayLabel,
                DisplayIndex: displayIndex,
                MonitorHandle: snapshot.MonitorHandle,
                Bounds: snapshot.Bounds,
                WorkingArea: snapshot.WorkingArea,
                IsPrimary: snapshot.IsPrimary,
                Dpi: snapshot.Dpi));
        }

        return result;
    }

    internal static string NormalizeHardwareMonitorPath(string? rawDevicePath)
    {
        if (string.IsNullOrWhiteSpace(rawDevicePath))
        {
            return string.Empty;
        }

        var trimmed = rawDevicePath.Trim();
        if (trimmed.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            trimmed = trimmed[4..];
        }

        // Strip trailing GUID interface suffix (e.g. #{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7})
        // so QueryDisplayConfig and EnumDisplayDevicesW yield identical stable keys.
        var guidIndex = trimmed.IndexOf("#{", StringComparison.Ordinal);
        if (guidIndex > 0)
        {
            trimmed = trimmed[..guidIndex];
        }

        return trimmed.ToUpperInvariant();
    }

    private static string ShortGdiName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return string.Empty;
        }

        var stripped = deviceName.Replace(@"\\.\", string.Empty, StringComparison.Ordinal).Trim();
        return stripped;
    }

    private static Dictionary<string, MonitorHardwareInfo> QueryHardwareLookup(
        IReadOnlyList<MonitorSnapshot> snapshots)
    {
        var map = new Dictionary<string, MonitorHardwareInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            Interlocked.Increment(ref _queryDisplayConfigCallCountForTests);
            QueryDisplayConfigActivePaths(map);
        }
        catch
        {
            // Fall through to EnumDisplayDevicesW fallback when QueryDisplayConfig is unavailable.
        }

        foreach (var snapshot in snapshots)
        {
            if (string.IsNullOrWhiteSpace(snapshot.DeviceName)
                || (map.TryGetValue(snapshot.DeviceName, out var existing)
                    && !string.IsNullOrWhiteSpace(existing.DevicePath)))
            {
                continue;
            }

            if (TryQueryEnumDisplayDevice(snapshot.DeviceName, out var fallbackPath, out var fallbackFriendly))
            {
                var friendly = !string.IsNullOrWhiteSpace(existing?.FriendlyName)
                    ? existing.FriendlyName
                    : fallbackFriendly;
                map[snapshot.DeviceName] = new MonitorHardwareInfo(fallbackPath, friendly, existing?.TargetId);
            }
        }

        return map;
    }

    private static void QueryDisplayConfigActivePaths(
        Dictionary<string, MonitorHardwareInfo> map)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var sizeStatus = GetDisplayConfigBufferSizes(
                QdcOnlyActivePaths,
                out var pathCount,
                out var modeCount);
            if (sizeStatus != ErrorSuccess || pathCount == 0)
            {
                return;
            }

            var paths = new DisplayConfigPathInfo[pathCount];
            var modes = new DisplayConfigModeInfo[Math.Max(1u, modeCount)];
            var queryStatus = QueryDisplayConfig(
                QdcOnlyActivePaths,
                ref pathCount,
                paths,
                ref modeCount,
                modes,
                0);
            if (queryStatus == ErrorInsufficientBuffer)
            {
                continue;
            }

            if (queryStatus != ErrorSuccess)
            {
                return;
            }

            for (var i = 0; i < pathCount && i < paths.Length; i++)
            {
                var path = paths[i];
                var sourceName = new DisplayConfigSourceDeviceName
                {
                    Header = new DisplayConfigDeviceInfoHeader
                    {
                        Type = DisplayConfigDeviceInfoGetSourceName,
                        Size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
                        AdapterId = path.SourceInfo.AdapterId,
                        Id = path.SourceInfo.Id
                    }
                };
                if (DisplayConfigGetDeviceInfo(ref sourceName) != ErrorSuccess
                    || string.IsNullOrWhiteSpace(sourceName.ViewGdiDeviceName))
                {
                    continue;
                }

                var targetName = new DisplayConfigTargetDeviceName
                {
                    Header = new DisplayConfigDeviceInfoHeader
                    {
                        Type = DisplayConfigDeviceInfoGetTargetName,
                        Size = (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                        AdapterId = path.TargetInfo.AdapterId,
                        Id = path.TargetInfo.Id
                    }
                };
                if (DisplayConfigGetDeviceInfo(ref targetName) != ErrorSuccess)
                {
                    continue;
                }

                var gdiName = sourceName.ViewGdiDeviceName.Trim();
                var devicePath = targetName.MonitorDevicePath?.Trim() ?? string.Empty;
                var friendly = targetName.MonitorFriendlyDeviceName?.Trim() ?? string.Empty;
                if (!map.ContainsKey(gdiName) || !string.IsNullOrEmpty(devicePath))
                {
                    map[gdiName] = new MonitorHardwareInfo(devicePath, friendly, path.TargetInfo.Id);
                }
            }

            return;
        }
    }

    private static bool TryQueryEnumDisplayDevice(
        string gdiDeviceName,
        out string devicePath,
        out string friendlyName)
    {
        devicePath = string.Empty;
        friendlyName = string.Empty;

        var monitorDevice = new DisplayDeviceW
        {
            Cb = (uint)Marshal.SizeOf<DisplayDeviceW>()
        };
        if (!EnumDisplayDevicesW(gdiDeviceName, 0, ref monitorDevice, EddGetDeviceInterfaceName))
        {
            return false;
        }

        devicePath = monitorDevice.DeviceID?.Trim() ?? string.Empty;
        friendlyName = monitorDevice.DeviceString?.Trim() ?? string.Empty;
        if (string.Equals(friendlyName, "Generic PnP Monitor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(friendlyName, "通用即插即用监视器", StringComparison.OrdinalIgnoreCase))
        {
            friendlyName = string.Empty;
        }

        return !string.IsNullOrEmpty(devicePath) || !string.IsNullOrEmpty(friendlyName);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DisplayConfigRational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    // Opaque 64-byte placeholder matching sizeof(DISPLAYCONFIG_MODE_INFO).
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DisplayConfigModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDeviceW
    {
        public uint Cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DisplayConfigModeInfo[] modeInfoArray,
        nint currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName requestPacket);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(
        string? lpDevice,
        uint iDevNum,
        ref DisplayDeviceW lpDisplayDevice,
        uint dwFlags);
}
