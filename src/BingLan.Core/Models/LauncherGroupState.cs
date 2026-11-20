namespace BingLan.Core.Models;

public sealed class LauncherGroupState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public List<LauncherGroupMember> Members { get; set; } = [];
}

public sealed record LauncherGroupMember(
    string Key, string? FallbackPath = null, string? FallbackName = null, DockPinnedApp? FallbackPin = null);

public static class LauncherGroupRules
{
    public const int MaximumNameLength = 32;
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
        { "全部", "常用", "未分组" };

    public static bool TryCreate(IList<LauncherGroupState> groups, string name, out LauncherGroupState? group)
    {
        group = null;
        var normalized = NormalizeName(name);
        if (normalized is null || groups.Any(candidate =>
                string.Equals(candidate.Name, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        group = new LauncherGroupState { Name = normalized };
        groups.Add(group);
        return true;
    }

    public static bool TryAdd(LauncherGroupState group, LauncherGroupMember member)
    {
        if (string.IsNullOrWhiteSpace(member.Key)
            || group.Members.Any(existing => string.Equals(existing.Key, member.Key, StringComparison.Ordinal)))
        {
            return false;
        }

        group.Members.Add(member);
        return true;
    }

    public static bool Remove(LauncherGroupState group, string key) =>
        group.Members.RemoveAll(member => string.Equals(member.Key, key, StringComparison.Ordinal)) > 0;

    public static List<LauncherGroupState> Normalize(List<LauncherGroupState>? groups)
    {
        var normalized = new List<LauncherGroupState>();
        var ids = new HashSet<Guid>();
        foreach (var group in groups ?? [])
        {
            if (group is null || NormalizeName(group.Name) is not { } name)
            {
                continue;
            }

            var members = (group.Members ?? [])
                .Where(member => member is not null && !string.IsNullOrWhiteSpace(member.Key))
                .DistinctBy(member => member.Key, StringComparer.Ordinal)
                .ToList();
            var sameName = normalized.FirstOrDefault(existing =>
                string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase));
            if (sameName is not null)
            {
                foreach (var member in members) TryAdd(sameName, member);
                continue;
            }

            if (group.Id == Guid.Empty || !ids.Add(group.Id))
            {
                group.Id = Guid.NewGuid();
                ids.Add(group.Id);
            }
            group.Name = name;
            group.Members = members;
            normalized.Add(group);
        }
        if (groups is null)
        {
            return normalized;
        }
        groups.Clear();
        groups.AddRange(normalized);
        return groups;
    }

    private static string? NormalizeName(string? name)
    {
        var value = name?.Trim();
        return string.IsNullOrEmpty(value) || value.Length > MaximumNameLength
            || value.Any(char.IsControl) || ReservedNames.Contains(value)
                ? null : value;
    }
}
