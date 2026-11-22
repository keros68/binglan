using System.IO;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.App.Windows;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.TopBar;

namespace BingLan.App.Services;

internal sealed record LauncherPlace(
    string Id,
    string Name,
    string Subtitle,
    string? Path,
    string Glyph,
    bool IsFileBox,
    IReadOnlyList<LauncherPanelEntry>? BoxEntries = null,
    bool IsPinnedCustom = false);

internal sealed record LauncherDirectoryListing(
    IReadOnlyList<LauncherPanelEntry> Entries,
    bool Truncated,
    string? Error);

internal static class LauncherFileCatalog
{
    private const int MaxRecentFiles = 30;
    private const int MaxRecentLinksToInspect = 90;
    private const int MaxDirectoryItems = 300;

    private static readonly HashSet<string> SkippedRecentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".url", ".msi", ".bat", ".cmd", ".com", ".scr",
        ".tmp", ".crdownload", ".part", ".download", ".partial"
    };

    private static readonly HashSet<string> SkippedDirectoryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".crdownload", ".part", ".download", ".partial"
    };

    internal static IReadOnlyList<LauncherPlace> BuildPlaces(
        IReadOnlyList<(Guid Id, string Title, IReadOnlyList<(string Path, string? Title)> Items)> fileBoxes,
        IReadOnlyList<string>? pinnedFolders = null)
    {
        var places = new List<LauncherPlace>();
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddSystemPlace("place:desktop", "桌面", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "\uE7F4");
        AddSystemPlace("place:downloads", "下载", KnownFolders.GetDownloadsPath(), "\uE896");
        AddSystemPlace("place:documents", "文档", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "\uE8A5");
        AddSystemPlace("place:pictures", "图片", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "\uEB9F");

        if (pinnedFolders is { Count: > 0 })
        {
            foreach (var rawPath in pinnedFolders)
            {
                if (LauncherPreferencesState.NormalizeFolderPath(rawPath) is not { } normalized
                    || !seenDirectories.Add(normalized)
                    || !Directory.Exists(normalized))
                {
                    continue;
                }
                var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(normalized));
                if (string.IsNullOrWhiteSpace(folderName))
                {
                    folderName = normalized;
                }
                places.Add(new LauncherPlace(
                    "pinned:" + normalized,
                    folderName,
                    "收藏 · " + TopBarSearchRules.FormatLocation(normalized, 28),
                    normalized,
                    "\uE728",
                    IsFileBox: false,
                    IsPinnedCustom: true));
            }
        }

        foreach (var (id, title, items) in fileBoxes)
        {
            var entries = new List<LauncherPanelEntry>();
            foreach (var (path, itemTitle) in items)
            {
                if (!Path.IsPathFullyQualified(path) && !FileMappingService.IsShellEntry(path))
                {
                    continue;
                }
                var kind = ClassifyPath(path);
                var fullName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                var displayName = string.IsNullOrWhiteSpace(itemTitle)
                    ? FileMappingService.GetDisplayName(path) : itemTitle;
                entries.Add(new LauncherPanelEntry(
                    new TopBarSearchCandidate(kind, displayName, fullName, path), title));
            }
            places.Add(new LauncherPlace(
                "box:" + id.ToString("N"),
                title,
                $"桌面分组盒 · {entries.Count} 项",
                null,
                "\uF12B",
                IsFileBox: true,
                BoxEntries: entries));
        }

        return places;

        void AddSystemPlace(string id, string name, string? path, string glyph)
        {
            if (LauncherPreferencesState.NormalizeFolderPath(path) is not { } normalized || !Directory.Exists(normalized))
            {
                return;
            }
            seenDirectories.Add(normalized);
            places.Add(new LauncherPlace(id, name, TopBarSearchRules.FormatLocation(normalized, 32), normalized, glyph, IsFileBox: false));
        }
    }

    internal static IReadOnlyList<LauncherPanelEntry> ReadRecentFiles(string? recentDirectory = null)
    {
        var folder = recentDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return [];
        }

        var results = new List<LauncherPanelEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var readyDrives = GetReadyDriveRoots();

        try
        {
            var shortcuts = new DirectoryInfo(folder)
                .EnumerateFiles("*.lnk", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(MaxRecentLinksToInspect);

            foreach (var shortcut in shortcuts)
            {
                if (results.Count >= MaxRecentFiles) break;
                var target = DockShortcuts.TryResolveShortcutTarget(shortcut.FullName);
                if (string.IsNullOrWhiteSpace(target)
                    || !Path.IsPathFullyQualified(target)
                    || target.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    continue;
                }

                var root = Path.GetPathRoot(target);
                if (string.IsNullOrEmpty(root) || !readyDrives.Contains(root))
                {
                    continue;
                }

                string fullTarget;
                try { fullTarget = Path.GetFullPath(target).TrimEnd('\\', '/'); }
                catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { continue; }

                if (fullTarget.Length <= 3 || !seen.Add(fullTarget))
                {
                    continue;
                }

                var isDirectory = Directory.Exists(fullTarget);
                if (!isDirectory)
                {
                    var ext = Path.GetExtension(fullTarget);
                    if (SkippedRecentExtensions.Contains(ext) || !File.Exists(fullTarget))
                    {
                        continue;
                    }
                }

                var fileName = Path.GetFileName(fullTarget);
                if (string.IsNullOrWhiteSpace(fileName)) continue;

                var kind = isDirectory ? TopBarSearchItemKind.Folder : TopBarSearchItemKind.File;
                var displayName = isDirectory ? fileName : FileMappingService.GetDisplayName(fullTarget);
                var accessedLocal = new DateTimeOffset(shortcut.LastWriteTimeUtc, TimeSpan.Zero).ToLocalTime();
                long? fileSize = null;
                if (!isDirectory)
                {
                    try { fileSize = new FileInfo(fullTarget).Length; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                results.Add(new LauncherPanelEntry(
                    new TopBarSearchCandidate(kind, displayName, fileName, fullTarget),
                    "最近使用",
                    LastAccessedLocal: accessedLocal,
                    FileSizeBytes: fileSize));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return results;
        }

        return results;
    }

    internal static Task<LauncherDirectoryListing> ReadDirectoryEntriesAsync(string directoryPath, CancellationToken token) =>
        Task.Run(() => ReadDirectoryEntries(directoryPath, token), token);

    internal static LauncherDirectoryListing ReadDirectoryEntries(string directoryPath, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return new LauncherDirectoryListing([], false, "目录路径无效");
        }
        try
        {
            if (!Directory.Exists(directoryPath))
            {
                return new LauncherDirectoryListing([], false, "文件夹已不存在或暂时无法访问");
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
            };

            var source = Path.GetFileName(Path.TrimEndingDirectorySeparator(directoryPath));
            if (string.IsNullOrWhiteSpace(source)) source = directoryPath;

            var info = new DirectoryInfo(directoryPath);
            var directories = new List<LauncherPanelEntry>();
            var files = new List<LauncherPanelEntry>();
            var truncated = false;

            foreach (var dir in info.EnumerateDirectories("*", options).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                if (directories.Count >= MaxDirectoryItems)
                {
                    truncated = true;
                    break;
                }
                var modified = new DateTimeOffset(dir.LastWriteTimeUtc, TimeSpan.Zero).ToLocalTime();
                directories.Add(new LauncherPanelEntry(
                    new TopBarSearchCandidate(TopBarSearchItemKind.Folder, dir.Name, dir.Name, dir.FullName),
                    source,
                    LastAccessedLocal: modified));
            }

            if (!truncated)
            {
                foreach (var file in info.EnumerateFiles("*", options).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    if (SkippedDirectoryExtensions.Contains(file.Extension)) continue;
                    if (directories.Count + files.Count >= MaxDirectoryItems)
                    {
                        truncated = true;
                        break;
                    }
                    var modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero).ToLocalTime();
                    files.Add(new LauncherPanelEntry(
                        new TopBarSearchCandidate(TopBarSearchItemKind.File,
                            FileMappingService.GetDisplayName(file.FullName), file.Name, file.FullName),
                        source,
                        LastAccessedLocal: modified,
                        FileSizeBytes: file.Length));
                }
            }

            directories.AddRange(files);
            return new LauncherDirectoryListing(directories, truncated, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new LauncherDirectoryListing([], false, "暂时无法读取该文件夹");
        }
    }

    internal static bool IsRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }
            return new DriveInfo(root).DriveType is DriveType.Network or DriveType.NoRootDirectory;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return true;
        }
    }

    internal static TopBarSearchItemKind ClassifyPath(string path) =>
        FileMappingService.IsShellEntry(path)
        || Path.EndsInDirectorySeparator(path)
        || (!IsRemotePath(path) && Directory.Exists(path))
            ? TopBarSearchItemKind.Folder
            : TopBarSearchItemKind.File;

    private static HashSet<string> GetReadyDriveRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady)
                {
                    roots.Add(drive.Name);
                }
            }
        }
        catch (IOException) { }
        return roots;
    }
}
