using System.IO;
using BingLan.App.Dock;
using BingLan.App.Windows;
using BingLan.Core.TopBar;

namespace BingLan.App.Services;

internal static class LauncherSupplementalNormalizer
{
    private const int MaxItems = 256;

    internal static List<LauncherPanelEntry> Normalize(
        IEnumerable<LauncherPanelEntry> entries,
        IEnumerable<string>? startMenuRoots = null)
    {
        var roots = (startMenuRoots ?? new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
            })
            .Select(NormalizeRoot)
            .OfType<string>()
            .ToArray();

        var normalized = new List<LauncherPanelEntry>();
        foreach (var entry in entries.Take(MaxItems))
        {
            var path = entry.Candidate.Path;
            if (!Path.IsPathFullyQualified(path))
            {
                normalized.Add(entry);
                continue;
            }

            if (entry.Candidate.Kind == TopBarSearchItemKind.Folder)
            {
                if (!IsUnderStartMenuRoot(path, roots))
                {
                    normalized.Add(entry);
                }
                continue;
            }

            var extension = Path.GetExtension(path);
            if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                normalized.Add(entry);
                continue;
            }

            var stem = Path.GetFileNameWithoutExtension(path);
            if (DockShortcuts.IsSkippedShortcutName(stem))
            {
                continue;
            }

            if (LauncherFileCatalog.IsRemotePath(path))
            {
                normalized.Add(entry);
                continue;
            }

            if (DockShortcuts.CreatePin(path) is { ExecutablePath: { } executable } pin)
            {
                var iconSource = extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? path : null;
                normalized.Add(new LauncherPanelEntry(
                    new TopBarSearchCandidate(TopBarSearchItemKind.App, pin.DisplayName, Path.GetFileName(executable), executable),
                    entry.Source,
                    pin,
                    IconSource: iconSource));
                continue;
            }

            normalized.Add(entry);
        }

        return normalized;
    }

    private static string? NormalizeRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }
        try
        {
            return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsUnderStartMenuRoot(string path, string[] roots)
    {
        if (roots.Length == 0)
        {
            return false;
        }

        string fullWithSeparator;
        try
        {
            fullWithSeparator = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }

        return roots.Any(root => fullWithSeparator.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }
}
