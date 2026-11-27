namespace BingLan.Core.Models;

public readonly record struct SurfaceMonitorTarget(string MonitorKey, bool IsPrimary, bool IsPersistentIdentity);

/// <summary>Default and per-display targeting for the Dock and top bar.</summary>
public static class SurfaceMonitorTargetRules
{
    // A legacy DISPLAYn name cannot establish a physical target. Without a key, use Windows primary.
    public static bool FollowsPrimary(string? monitorKey) => string.IsNullOrWhiteSpace(monitorKey);

    public static bool IsEnabledOnMonitor(bool defaultEnabled, bool isDefaultTarget, ScreenSurfaceEnableMode mode) => mode switch
    {
        ScreenSurfaceEnableMode.Enabled => true,
        ScreenSurfaceEnableMode.Disabled => false,
        ScreenSurfaceEnableMode.FollowGlobal => defaultEnabled,
        _ => defaultEnabled && isDefaultTarget
    };

    public static int? ResolveIndex(
        string? monitorKey,
        IReadOnlyList<SurfaceMonitorTarget> monitors)
    {
        var followsPrimary = FollowsPrimary(monitorKey);

        int? match = null;
        for (var index = 0; index < monitors.Count; index++)
        {
            var monitor = monitors[index];
            var matches = followsPrimary
                ? monitor.IsPrimary
                : monitor.IsPersistentIdentity && string.Equals(
                    monitor.MonitorKey, monitorKey?.Trim(), StringComparison.OrdinalIgnoreCase);
            if (!matches)
            {
                continue;
            }
            if (match is not null)
            {
                return null;
            }
            match = index;
        }
        return match;
    }
}
