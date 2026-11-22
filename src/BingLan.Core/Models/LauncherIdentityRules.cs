using System.IO;

namespace BingLan.Core.Models;

public readonly record struct LauncherIdentityInput(
    string? ExecutablePath,
    string? AppUserModelId,
    string? TargetPath,
    string? CandidatePath,
    string? Arguments,
    string DisplayName);

public static class LauncherIdentityRules
{
    // 只说明“从哪里启动”的参数，不改变程序行为（Chrome 的开始菜单/任务栏快捷方式会加）。
    private static readonly string[] BenignArgumentPrefixes = ["--source-shortcut-location="];

    public static string TargetKey(LauncherIdentityInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.ExecutablePath))
        {
            return "exe:" + Normalize(input.ExecutablePath);
        }

        if (!string.IsNullOrWhiteSpace(input.AppUserModelId))
        {
            return "aumid:" + input.AppUserModelId.Trim();
        }

        return "path:" + Normalize(input.TargetPath ?? input.CandidatePath ?? string.Empty);
    }

    public static string StripBenignArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return string.Empty;
        }

        var kept = new List<string>();
        var index = 0;
        while (index < arguments.Length)
        {
            while (index < arguments.Length && char.IsWhiteSpace(arguments[index]))
            {
                index++;
            }

            if (index >= arguments.Length)
            {
                break;
            }

            var start = index;
            var inQuotes = false;
            while (index < arguments.Length)
            {
                var ch = arguments[index];
                if (ch == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (char.IsWhiteSpace(ch) && !inQuotes)
                {
                    break;
                }

                index++;
            }

            var token = arguments[start..index];
            if (!IsBenignToken(token))
            {
                kept.Add(token);
            }
        }

        return kept.Count == 0 ? string.Empty : string.Join(' ', kept).Trim();
    }

    public static string NameKey(string? displayName) =>
        (displayName ?? string.Empty).Trim().ToUpperInvariant();

    public static IReadOnlyList<IReadOnlyList<T>> Group<T>(
        IEnumerable<T> items,
        Func<T, LauncherIdentityInput> describe)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(describe);

        var groups = new List<List<T>>();
        var byArgs = new Dictionary<(string Target, string Args), List<T>>();
        var byNameAny = new Dictionary<(string Target, string Name), List<T>>();
        var byNameEmpty = new Dictionary<(string Target, string Name), List<T>>();

        foreach (var item in items)
        {
            var input = describe(item);
            var tk = TargetKey(input);
            var ak = StripBenignArguments(input.Arguments);
            var nk = NameKey(input.DisplayName);

            if (!byArgs.TryGetValue((tk, ak), out var group))
            {
                if (ak.Length == 0)
                {
                    byNameAny.TryGetValue((tk, nk), out group);
                }
                else
                {
                    byNameEmpty.TryGetValue((tk, nk), out group);
                }
            }

            if (group is null)
            {
                group = [];
                groups.Add(group);
            }

            group.Add(item);
            byArgs.TryAdd((tk, ak), group);
            if (nk.Length > 0)
            {
                byNameAny.TryAdd((tk, nk), group);
                if (ak.Length == 0)
                {
                    byNameEmpty.TryAdd((tk, nk), group);
                }
            }
        }

        return groups;
    }

    public static string PreferDisplayName(
        string primaryName,
        string? executablePath,
        IEnumerable<string> shortcutNames)
    {
        ArgumentNullException.ThrowIfNull(shortcutNames);

        var trimmed = (primaryName ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            string? exeStem = null;
            try
            {
                exeStem = Path.GetFileNameWithoutExtension(executablePath);
            }
            catch (ArgumentException)
            {
            }

            if (!string.IsNullOrEmpty(exeStem)
                && string.Equals(trimmed, exeStem, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var shortcutName in shortcutNames)
                {
                    var candidate = shortcutName?.Trim();
                    if (!string.IsNullOrWhiteSpace(candidate)
                        && !string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }
        }

        return primaryName ?? string.Empty;
    }

    public static IReadOnlySet<string> FindLaunchVariantFamilies(
        IEnumerable<(string RowKey, string TargetKey, string? Folder, bool HasArguments)> rows,
        int threshold = 3)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var families = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in rows
                     .Where(row => row.HasArguments)
                     .GroupBy(row => (row.TargetKey, Folder: row.Folder ?? string.Empty)))
        {
            var members = group.ToList();
            if (members.Count >= threshold)
            {
                foreach (var member in members)
                {
                    families.Add(member.RowKey);
                }
            }
        }

        return families;
    }

    private static bool IsBenignToken(string token)
    {
        var unquoted = token.StartsWith('"') ? token.Trim('"') : token;
        foreach (var prefix in BenignArgumentPrefixes)
        {
            if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || unquoted.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string path)
    {
        var trimmed = path.Trim();
        if (Path.IsPathFullyQualified(trimmed))
        {
            try
            {
                trimmed = Path.GetFullPath(trimmed);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
            }

            trimmed = trimmed.TrimEnd('\\', '/');
        }

        return trimmed.ToUpperInvariant();
    }
}
