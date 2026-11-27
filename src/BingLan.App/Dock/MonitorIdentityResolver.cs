using System.Runtime.InteropServices;
using BingLan.Core.Dock;
using BingLan.Core.Models;

namespace BingLan.App.Dock;

/// <summary>
/// A connected display with live Win32 geometry/DPI and its physical identity when available.
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
    internal MonitorIdentityKind IdentityKind { get; init; }

    internal bool IsPersistentIdentity => IdentityKind == MonitorIdentityKind.DeviceInterfacePath;

    // Legacy role-based ownership uses the primary sentinel and exact device paths.
    internal string? LegacyScreenKey { get; init; }

    internal string LegacyDeviceKey { get; init; } = string.Empty;

    /// <summary>
    /// Legacy ownership key: null for the Windows primary and the prior device key otherwise.
    /// </summary>
    internal string? ScreenKey => IsPrimary ? null : LegacyScreenKey ?? MonitorKey;

    internal int ScalePercent => Dpi > 0 ? (int)Math.Round(Dpi * 100d / 96d) : 100;

    internal string ResolutionSummary => $"{Bounds.Width}×{Bounds.Height} · {ScalePercent}%";
}

internal enum MonitorIdentityKind
{
    Fallback,
    DeviceInterfacePath
}

internal sealed record MonitorHardwareInfo(
    string DevicePath,
    string FriendlyName,
    uint? TargetId = null,
    bool IsAmbiguous = false,
    bool IsVirtual = false);

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
    private const uint DisplayDeviceActive = 0x00000001;
    private const uint DisplayDeviceMirroringDriver = 0x00000008;
    private const uint OutputTechnologyIndirectVirtual = 17;

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
    /// Checks all attached roles with lightweight geometry enumeration only.
    /// </summary>
    internal static bool IsDeviceConnected(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return false;
        if (ResolveOverrideForTests is { } overrideResolver)
        {
            return overrideResolver().Any(m => string.Equals(m.DeviceName.Trim(), deviceName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return MonitorCatalog.GetAll().Any(m => string.Equals(m.DeviceName.Trim(), deviceName.Trim(), StringComparison.OrdinalIgnoreCase));
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

        // Include the primary when rejecting duplicated exact device paths.
        var hardwareCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var legacyHardwareCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var snapshot in ordered)
        {
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
                    if (!snapshot.IsPrimary)
                    {
                        legacyHardwareCounts[norm] = legacyHardwareCounts.GetValueOrDefault(norm) + 1;
                    }
                }

            }
        }

        var seenKeys = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenLegacyKeys = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<ResolvedMonitor>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var snapshot = ordered[index];
            var displayIndex = index + 1;
            var shortGdi = ShortGdiName(snapshot.DeviceName);

            string rawHardwarePath = string.Empty;
            string friendlyName = string.Empty;
            uint? targetId = null;
            MonitorHardwareInfo? hardware = null;
            if (hardwareLookup is not null
                && !string.IsNullOrWhiteSpace(snapshot.DeviceName)
                && hardwareLookup.TryGetValue(snapshot.DeviceName, out var info))
            {
                hardware = info;
                rawHardwarePath = info.DevicePath;
                friendlyName = info.FriendlyName;
                targetId = info.TargetId;
            }

            var normalizedHardware = NormalizeHardwareMonitorPath(rawHardwarePath);
            var slotKey = !string.IsNullOrEmpty(shortGdi)
                ? $"slot:{shortGdi.ToUpperInvariant()}"
                : $"slot:{displayIndex}";
            var legacyKey = snapshot.IsPrimary
                ? MultiScreenRules.PrimaryScreenSentinel
                : !string.IsNullOrEmpty(normalizedHardware)
                    ? legacyHardwareCounts.GetValueOrDefault(normalizedHardware) > 1 && targetId is { } legacyPort
                        ? $"{normalizedHardware}@PORT:{legacyPort}"
                        : normalizedHardware
                    : slotKey;
            legacyKey = MakeUniqueKey(legacyKey, seenLegacyKeys);

            var identityKind = MonitorIdentityKind.Fallback;
            var baseKey = slotKey;
            if (hardware is { IsAmbiguous: false, IsVirtual: false }
                && IsPhysicalDevicePath(normalizedHardware)
                && hardwareCounts.GetValueOrDefault(normalizedHardware) == 1)
            {
                baseKey = normalizedHardware;
                identityKind = MonitorIdentityKind.DeviceInterfacePath;
            }
            var uniqueKey = MakeUniqueKey(baseKey, seenKeys);

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
                Dpi: snapshot.Dpi)
            {
                IdentityKind = identityKind,
                LegacyScreenKey = snapshot.IsPrimary ? null : legacyKey,
                LegacyDeviceKey = normalizedHardware
            });
        }

        return result;
    }

    private static string MakeUniqueKey(string key, Dictionary<string, int> seen)
    {
        var count = seen.GetValueOrDefault(key) + 1;
        seen[key] = count;
        return count == 1 ? key : $"{key}#{count}";
    }

    private static bool IsPhysicalDevicePath(string normalizedPath)
    {
        var parts = normalizedPath.Split('#');
        return parts.Length == 3
            && parts[0] == "DISPLAY"
            && parts[1].Length > 0 && parts[2].Length > 0
            && !parts[1].Contains("DEFAULT", StringComparison.Ordinal)
            && !parts[1].Contains("RDP", StringComparison.Ordinal)
            && !parts[1].Contains("VIRTUAL", StringComparison.Ordinal);
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
            if (snapshots.Count > 1)
            {
                Interlocked.Increment(ref _queryDisplayConfigCallCountForTests);
                QueryDisplayConfigActivePaths(map);
            }
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

            if (TryQueryEnumDisplayDevice(snapshot.DeviceName) is { } fallback)
            {
                var friendly = !string.IsNullOrWhiteSpace(existing?.FriendlyName)
                    ? existing.FriendlyName
                    : fallback.FriendlyName;
                map[snapshot.DeviceName] = fallback with
                {
                    FriendlyName = friendly,
                    TargetId = existing?.TargetId,
                    IsAmbiguous = fallback.IsAmbiguous || existing?.IsAmbiguous == true,
                    IsVirtual = fallback.IsVirtual || existing?.IsVirtual == true
                };
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
                if (map.TryGetValue(gdiName, out var previous))
                {
                    // Cloned targets share one logical desktop surface; no individual
                    // physical display may inherit that surface's saved profile.
                    map[gdiName] = previous with { IsAmbiguous = true };
                }
                else
                {
                    map[gdiName] = new MonitorHardwareInfo(devicePath, friendly, path.TargetInfo.Id,
                        IsVirtual: targetName.OutputTechnology == OutputTechnologyIndirectVirtual);
                }
            }

            return;
        }
    }

    private static MonitorHardwareInfo? TryQueryEnumDisplayDevice(string gdiDeviceName)
    {
        MonitorHardwareInfo? result = null;
        for (uint index = 0; ; index++)
        {
            var monitorDevice = new DisplayDeviceW { Cb = (uint)Marshal.SizeOf<DisplayDeviceW>() };
            if (!EnumDisplayDevicesW(gdiDeviceName, index, ref monitorDevice, EddGetDeviceInterfaceName)) break;
            if ((monitorDevice.StateFlags & DisplayDeviceActive) == 0) continue;

            if (result is not null)
            {
                result = result with { IsAmbiguous = true };
                continue;
            }

            var friendlyName = monitorDevice.DeviceString?.Trim() ?? string.Empty;
            if (string.Equals(friendlyName, "Generic PnP Monitor", StringComparison.OrdinalIgnoreCase)
                || string.Equals(friendlyName, "通用即插即用监视器", StringComparison.OrdinalIgnoreCase))
            {
                friendlyName = string.Empty;
            }
            result = new MonitorHardwareInfo(monitorDevice.DeviceID?.Trim() ?? string.Empty, friendlyName,
                IsVirtual: (monitorDevice.StateFlags & DisplayDeviceMirroringDriver) != 0);
        }

        return result;
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
