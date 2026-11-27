using BingLan.Core.Models;

namespace BingLan.App.Dock;

internal sealed record SurfaceMonitorSelection(
    string Key, MonitorSnapshot Monitor, ScreenSurfacePreferences? Preferences, bool IsPersistentIdentity = false);

internal static class SurfaceMonitorTargetResolver
{
    internal static IReadOnlyList<SurfaceMonitorSelection> ResolveTargets(
        string? monitorKey, bool defaultEnabled,
        IReadOnlyDictionary<string, ScreenSurfacePreferences>? overrides,
        Func<ScreenSurfacePreferences, ScreenSurfaceEnableMode> enableMode,
        IReadOnlyList<ResolvedMonitor>? connected = null)
    {
        if (overrides is null && connected is null)
        {
            return defaultEnabled && Resolve(monitorKey) is { } monitor
                ? [new SurfaceMonitorSelection(monitor.DeviceName, monitor, null)] : [];
        }

        var monitors = connected ?? MonitorIdentityResolver.ResolveConnected();
        var targets = monitors.Select(monitor => new SurfaceMonitorTarget(
            monitor.MonitorKey, monitor.IsPrimary, monitor.IsPersistentIdentity)).ToArray();
        var defaultIndex = SurfaceMonitorTargetRules.ResolveIndex(monitorKey, targets);
        var result = new List<SurfaceMonitorSelection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var index in Enumerable.Range(0, monitors.Count)
                     .OrderByDescending(index => index == defaultIndex)
                     .ThenByDescending(index => monitors[index].IsPrimary))
        {
            var monitor = monitors[index];
            ScreenSurfacePreferences? preferences = null;
            if (monitor.IsPersistentIdentity)
            {
                overrides?.TryGetValue(monitor.MonitorKey, out preferences);
            }

            if (SurfaceMonitorTargetRules.IsEnabledOnMonitor(defaultEnabled, index == defaultIndex,
                    preferences is null ? ScreenSurfaceEnableMode.GlobalTarget : enableMode(preferences))
                && seen.Add(monitor.MonitorKey))
            {
                result.Add(new SurfaceMonitorSelection(monitor.MonitorKey, ToSnapshot(monitor), preferences, monitor.IsPersistentIdentity));
            }
        }
        return result;
    }

    internal static MonitorSnapshot? Resolve(string? monitorKey)
    {
        if (SurfaceMonitorTargetRules.FollowsPrimary(monitorKey))
        {
            return MonitorCatalog.GetAll().FirstOrDefault(monitor => monitor.IsPrimary);
        }
        var monitors = MonitorIdentityResolver.ResolveConnected();
        var targets = monitors.Select(monitor => new SurfaceMonitorTarget(
            monitor.MonitorKey, monitor.IsPrimary, monitor.IsPersistentIdentity)).ToArray();
        if (SurfaceMonitorTargetRules.ResolveIndex(monitorKey, targets) is not { } index)
        {
            return null;
        }

        return ToSnapshot(monitors[index]);
    }

    private static MonitorSnapshot ToSnapshot(ResolvedMonitor target) => new(
        target.MonitorHandle, target.DeviceName, target.Bounds, target.WorkingArea, target.IsPrimary, target.Dpi);
}
