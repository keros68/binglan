namespace BingLan.Core.Models;

public readonly record struct SurfaceMonitorTarget(string MonitorKey, bool IsPrimary, bool IsPersistentIdentity);

/// <summary>Default and per-display targeting for the Dock and top bar.</summary>
public static class SurfaceMonitorTargetRules
{
    public static bool FollowsPrimary(string? monitorKey, string? legacyDeviceName) =>
        string.IsNullOrWhiteSpace(monitorKey) && string.IsNullOrWhiteSpace(legacyDeviceName);

    public static bool IsEnabledOnMonitor(bool defaultEnabled, bool isDefaultTarget, bool? screenOverride) =>
        screenOverride ?? (defaultEnabled && isDefaultTarget);

    public static int? ResolveIndex(
        string? monitorKey,
        string? legacyDeviceName,
        IReadOnlyList<SurfaceMonitorTarget> monitors)
    {
        var followsPrimary = FollowsPrimary(monitorKey, legacyDeviceName);
        if (!followsPrimary && string.IsNullOrWhiteSpace(monitorKey))
        {
            // A DISPLAYn name alone cannot prove which physical display was originally selected.
            return null;
        }

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
