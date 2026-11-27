using System.IO;

namespace BingLan.Core.Dock;

public sealed record TrackedWindow(
    nint Handle,
    uint ProcessId,
    string Title,
    string? AppUserModelId,
    string? ExecutablePath,
    bool IsForeground,
    bool IsMinimized);

public sealed record WindowGroup(
    string Key,
    string DisplayName,
    IReadOnlyList<TrackedWindow> Windows);

public static class WindowGrouping
{
    public static string IdentityKey(TrackedWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!string.IsNullOrWhiteSpace(window.AppUserModelId))
        {
            return $"aumid:{window.AppUserModelId.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(window.ExecutablePath))
        {
            return $"exe:{NormalizeExecutablePath(window.ExecutablePath)}";
        }

        return $"pid:{window.ProcessId}";
    }

    public static IReadOnlyList<WindowGroup> Group(IEnumerable<TrackedWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        var orderedGroups = new List<GroupBuilder>();
        var groupsByKey = new Dictionary<string, GroupBuilder>(StringComparer.OrdinalIgnoreCase);

        foreach (var window in windows)
        {
            var key = IdentityKey(window);
            if (!groupsByKey.TryGetValue(key, out var group))
            {
                group = new GroupBuilder(key);
                groupsByKey.Add(key, group);
                orderedGroups.Add(group);
            }
            group.Windows.Add(window);
        }

        return orderedGroups
            .Select(group => new WindowGroup(
                group.Key,
                GetDisplayName(group.Windows),
                group.Windows.ToArray()))
            .ToArray();
    }

    /// <summary>
    /// Keeps running apps in the order they first appeared, so activating a window
    /// (which changes the z-order used for enumeration) does not move dock icons.
    /// </summary>
    public static IReadOnlyList<WindowGroup> OrderByFirstSeen(
        IReadOnlyList<WindowGroup> groups,
        IReadOnlyList<string> previousOrder)
    {
        ArgumentNullException.ThrowIfNull(groups);
        return OrderByFirstSeen(groups, previousOrder, group => group.Key);
    }

    internal static IReadOnlyList<T> OrderByFirstSeen<T>(
        IReadOnlyList<T> items,
        IReadOnlyList<string> previousOrder,
        Func<T, string> keyOf)
    {
        ArgumentNullException.ThrowIfNull(previousOrder);

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < previousOrder.Count; index++)
        {
            rank.TryAdd(previousOrder[index], index);
        }

        // LINQ OrderBy keeps the input order for items that have no previous rank.
        return items
            .OrderBy(item => rank.TryGetValue(keyOf(item), out var known) ? known : int.MaxValue)
            .ToArray();
    }

    public static string NormalizeExecutablePath(string executablePath)
    {
        var trimmed = executablePath.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return trimmed;
        }
    }

    private static string GetDisplayName(IReadOnlyList<TrackedWindow> windows)
    {
        foreach (var window in windows)
        {
            if (string.IsNullOrWhiteSpace(window.ExecutablePath))
            {
                continue;
            }

            var path = window.ExecutablePath.Trim().Trim('"');
            var name = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return AppDisplayNameRules.Normalize(name, path) ?? name;
            }
        }

        var title = windows
            .Select(window => window.Title.Trim())
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
        if (title is not null)
        {
            return title;
        }

        var appUserModelId = windows
            .Select(window => window.AppUserModelId?.Trim())
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
        return appUserModelId ?? $"PID {windows[0].ProcessId}";
    }

    private sealed class GroupBuilder(string key)
    {
        public string Key { get; } = key;
        public List<TrackedWindow> Windows { get; } = [];
    }
}
