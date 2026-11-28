using BingLan.Core.Models;

namespace BingLan.Core.Services;

public enum FileMappingSortMode
{
    Manual,
    Name,
    Type,
    Modified
}

/// <summary>A named group of file types a box can collect, such as documents or images.</summary>
public sealed record CollectType(string Key, string Name, IReadOnlyList<string> Extensions);

/// <summary>Which box each desktop item goes to, and the items no box collects.</summary>
public sealed record DesktopDistribution(
    IReadOnlyDictionary<Guid, IReadOnlyList<string>> ByBox,
    IReadOnlyDictionary<DesktopGroupCategory, IReadOnlyList<string>> Unclaimed);

public sealed record PreparedDesktopEntries(
    IReadOnlyDictionary<DesktopGroupCategory, IReadOnlyList<string>> Entries,
    IReadOnlySet<string> TemporarilyHiddenPaths);

public static class FileMappingService
{
    /// <summary>File type groups offered when choosing what a box collects.</summary>
    public static IReadOnlyList<CollectType> CollectTypes { get; } =
    [
        new("apps", "软件与快捷方式", [".lnk", ".url", ".exe", ".com", ".bat", ".cmd", ".appref-ms"]),
        new("documents", "文档", [".doc", ".docx", ".pdf", ".txt", ".md", ".rtf", ".xls", ".xlsx", ".csv", ".ppt", ".pptx", ".wps", ".et", ".dps"]),
        new("images", "图片", [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".heic", ".ico", ".tif", ".tiff"]),
        new("videos", "视频", [".mp4", ".mkv", ".mov", ".avi", ".wmv", ".flv", ".webm"]),
        new("audio", "音乐", [".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg"]),
        new("archives", "压缩包", [".zip", ".rar", ".7z", ".tar", ".gz", ".iso"])
    ];

    /// <summary>
    /// Turns typed extensions ("pdf, .DOCX  txt") into a clean list (".pdf", ".docx",
    /// ".txt"): lower case, leading dot, no duplicates, no blanks.
    /// </summary>
    public static List<string> NormalizeExtensions(IEnumerable<string>? extensions) =>
        (extensions ?? [])
            .SelectMany(value => (value ?? string.Empty).Split([' ', ',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries))
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value.Length > 0 && value.Trim('.').Length > 0 && value.Length <= 16)
            .Select(value => value.StartsWith('.') ? value : "." + value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Assigns each desktop item to the first box, in box order, that collects it: boxes
    /// with their own file types first, then the original category boxes (software,
    /// folders, other files). Items no box collects are returned by category, so missing
    /// category boxes can be created for them. Nothing on disk is touched.
    /// </summary>
    public static DesktopDistribution Distribute(IEnumerable<string> paths, IReadOnlyList<FileBoxState> boxes) =>
        Distribute(ClassifyDesktopEntries(paths), boxes);

    private static DesktopDistribution Distribute(
        IReadOnlyDictionary<DesktopGroupCategory, IReadOnlyList<string>> entries,
        IReadOnlyList<FileBoxState> boxes,
        IReadOnlySet<string>? automaticCandidates = null)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        var custom = boxes.Where(box => box.CollectFolders || box.CollectExtensions.Count > 0).ToList();
        var byBox = new Dictionary<Guid, List<string>>();
        var unclaimed = new Dictionary<DesktopGroupCategory, List<string>>();
        foreach (var (category, categoryPaths) in entries)
        {
            foreach (var path in categoryPaths)
            {
                if (automaticCandidates is not null && !automaticCandidates.Contains(path))
                {
                    continue;
                }

                var isFolder = category == DesktopGroupCategory.Folders;
                var extension = Path.GetExtension(path).ToLowerInvariant();
                var box = custom.FirstOrDefault(candidate => isFolder
                        ? candidate.CollectFolders
                        : candidate.CollectExtensions.Contains(extension))
                    ?? boxes.FirstOrDefault(candidate => candidate.DesktopCategory == category);
                if (automaticCandidates is not null && box is not null
                    && box.AutoCollectExcludedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var list = box is null
                    ? unclaimed.TryGetValue(category, out var pending) ? pending : unclaimed[category] = []
                    : byBox.TryGetValue(box.Id, out var assigned) ? assigned : byBox[box.Id] = [];
                list.Add(path);
            }
        }

        return new DesktopDistribution(
            byBox.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value),
            unclaimed.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value));
    }

    /// <summary>Reads the current file type of desktop notification paths before routing.</summary>
    public static PreparedDesktopEntries PrepareAutomaticDesktopEntries(IEnumerable<string> paths)
    {
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new PreparedDesktopEntries(ClassifyDesktopEntries(paths, hidden), hidden);
    }

    /// <summary>Routes prepared paths using current collection rules and mappings on all screens.</summary>
    public static DesktopDistribution DistributePreparedAutomatically(
        PreparedDesktopEntries prepared,
        IReadOnlyList<FileBoxState> eligibleBoxes, IEnumerable<FileBoxState> allBoxes)
    {
        var entries = prepared.Entries;
        // Keep the lookup proportional to the notification batch while checking the live mappings once.
        var pending = entries.Values.SelectMany(paths => paths)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in allBoxes.SelectMany(box => box.Items))
        {
            if (pending.Count == 0)
            {
                break;
            }
            pending.Remove(mapping.Path);
        }
        return Distribute(entries, eligibleBoxes, pending);
    }

    private const string ShellEntryPrefix = "shell:";

    private static readonly HashSet<string> ApplicationExtensions = new(
        [".lnk", ".url", ".exe", ".com", ".bat", ".cmd", ".appref-ms"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Shell namespace entries (e.g. "shell:MyComputerFolder") are identified by path
    /// prefix rather than a filesystem check, since they never resolve to a real path.
    /// </summary>
    public static bool IsShellEntry(string path) =>
        !string.IsNullOrEmpty(path) && path.StartsWith(ShellEntryPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsMissing(FileMappingState mapping) => IsMissing(mapping.Path);

    public static bool IsMissing(string path) =>
        !IsShellEntry(path) && !File.Exists(path) && !Directory.Exists(path);

    /// <summary>
    /// A confirmed missing leaf in a readable directory. Inaccessible paths retain their mappings.
    /// </summary>
    public static bool IsGone(string path, Func<string, FileAttributes>? readAttributes = null,
        Action<string>? probeDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(path) || IsShellEntry(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var attributesOf = readAttributes ?? File.GetAttributes;
        try
        {
            attributesOf(path);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // An unavailable mount or share can also report not-found.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }

        try
        {
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
            if (string.IsNullOrEmpty(parent))
            {
                return false;
            }
            // Opening the containing directory follows junctions and mounts. Reading
            // only the first entry also works for an empty directory without a full scan.
            (probeDirectory ?? ProbeDirectory)(parent);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static void ProbeDirectory(string path)
    {
        using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        _ = entries.MoveNext();
    }

    /// <summary>Removes only mappings whose identity and path still match the cleanup snapshot.</summary>
    public static int RemoveGone(FileBoxState box, IReadOnlyDictionary<Guid, string> gonePaths)
    {
        var removed = box.Items.RemoveAll(item => gonePaths.TryGetValue(item.Id, out var originalPath)
            && string.Equals(item.Path, originalPath, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            Normalize(box);
        }
        return removed;
    }

    /// <summary>Checks immutable path snapshots off the UI thread for later conditional cleanup.</summary>
    public static IReadOnlyDictionary<Guid, string> CollectGonePaths(IEnumerable<(Guid Id, string Path)> mappings)
    {
        var gone = new Dictionary<Guid, string>();
        foreach (var (id, path) in mappings)
        {
            if (IsGone(path))
            {
                gone[id] = path;
            }
        }
        return gone;
    }

    /// <summary>Extensions of in-progress downloads and temp files; not collected yet.</summary>
    private static readonly HashSet<string> PartialDownloadExtensions = new(
        [".crdownload", ".part", ".partial", ".download", ".tmp", ".opdownload"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True while a download or temp file is still being written; it becomes collectable
    /// when renamed to its final name.
    /// </summary>
    public static bool IsPartialDownload(string path) =>
        PartialDownloadExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Existing parent folders of the given paths, used to watch for deletions. Shell
    /// namespace entries have no real folder and are skipped.
    /// </summary>
    public static IReadOnlySet<string> CollectExistingParentDirectories(IEnumerable<string> paths)
    {
        var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || IsShellEntry(path))
            {
                continue;
            }

            string? parent;
            try
            {
                parent = Path.GetDirectoryName(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                parents.Add(parent);
            }
        }
        return parents;
    }

    public static IReadOnlyList<string> EnumerateDirectChildren(IEnumerable<string> directories)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var roots = new HashSet<string>(comparer);
        var entries = new HashSet<string>(comparer);

        foreach (var rawDirectory in directories)
        {
            if (string.IsNullOrWhiteSpace(rawDirectory))
            {
                continue;
            }

            string directory;
            try
            {
                directory = Path.GetFullPath(rawDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                continue;
            }

            if (!roots.Add(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(
                             directory,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    if (ReadDesktopEntryAttributes(entry) is { } attributes && IsVisibleDesktopEntry(attributes))
                    {
                        entries.Add(Path.GetFullPath(entry));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One unavailable desktop root must not block imports from the other root.
            }
        }

        return entries
            .OrderBy(GetDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(path => path, comparer)
            .ToArray();
    }

    private static FileAttributes? ReadDesktopEntryAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static int AddExisting(FileBoxState box, IEnumerable<string> paths) =>
        AddPaths(box, paths, checkExistence: true);

    /// <summary>
    /// Commits full paths whose existence was checked during background preparation.
    /// Only mapping records are updated; a final dedupe handles overlapping imports.
    /// </summary>
    public static int AddPrepared(FileBoxState box, IEnumerable<string> fullPaths, bool explicitImport = true) =>
        AddPaths(box, fullPaths, checkExistence: false, explicitImport);

    private static int AddPaths(FileBoxState box, IEnumerable<string> paths, bool checkExistence, bool explicitImport = true)
    {
        var known = box.Items.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var rawPath in paths)
        {
            var path = rawPath;
            if (checkExistence)
            {
                try
                {
                    path = Path.GetFullPath(rawPath);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }
            }
            if (known.Contains(path) || (checkExistence && !File.Exists(path) && !Directory.Exists(path)))
            {
                continue;
            }

            known.Add(path);
            if (explicitImport)
            {
                AllowAutoCollection(box, path);
            }
            box.Items.Add(new FileMappingState
            {
                Path = path,
                Order = box.Items.Count
            });
            added++;
        }

        Normalize(box);
        return added;
    }

    /// <summary>
    /// Adds a named system entry (a shell namespace path such as "shell:RecycleBinFolder",
    /// or a real known-folder path such as Downloads) with a fixed display title. Dedupes
    /// against existing mappings by path, same as <see cref="AddExisting"/>.
    /// </summary>
    public static int AddSystemEntry(FileBoxState box, string path, string title)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        if (box.Items.Any(item => comparer.Equals(item.Path, path)))
        {
            return 0;
        }

        if (!IsShellEntry(path) && !File.Exists(path) && !Directory.Exists(path))
        {
            return 0;
        }

        box.Items.Add(new FileMappingState
        {
            Path = path,
            Title = title,
            Order = box.Items.Count
        });
        AllowAutoCollection(box, path);
        Normalize(box);
        return 1;
    }

    /// <summary>
    /// Replaces an invalid mapping's path (and any stored title) while keeping its Id and
    /// order. Never touches the filesystem; shell namespace entries cannot be relinked
    /// since they never resolve to a real path.
    /// </summary>
    public static bool Relink(FileBoxState box, Guid itemId, string newPath)
    {
        var item = box.Items.FirstOrDefault(x => x.Id == itemId);
        if (item is null || IsShellEntry(item.Path))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(newPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }

        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            return false;
        }

        var comparer = StringComparer.OrdinalIgnoreCase;
        if (box.Items.Any(other => other.Id != itemId && comparer.Equals(other.Path, fullPath)))
        {
            return false;
        }

        ExcludeAutoCollection(box, item.Path);
        AllowAutoCollection(box, fullPath);
        item.Path = fullPath;
        item.Title = null;
        return true;
    }

    /// <summary>
    /// Sets the name a mapping shows in its box; a blank name goes back to the file name.
    /// Only the box's own state changes, never the file on disk.
    /// </summary>
    public static bool Rename(FileBoxState box, Guid itemId, string? displayName)
    {
        var item = box.Items.FirstOrDefault(x => x.Id == itemId);
        var blank = string.IsNullOrWhiteSpace(displayName);
        // A shell namespace entry has no file name to fall back to.
        if (item is null || (blank && IsShellEntry(item.Path)))
        {
            return false;
        }

        item.Title = blank ? null : displayName!.Trim();
        return true;
    }

    /// <summary>
    /// Moves a mapping to the end of another box, keeping its display name. Returns false
    /// and leaves both boxes unchanged when the target already maps the same path.
    /// </summary>
    public static bool Transfer(FileBoxState source, FileBoxState target, Guid itemId)
    {
        var item = source.Items.FirstOrDefault(x => x.Id == itemId);
        if (item is null ||
            ReferenceEquals(source, target) ||
            target.Items.Any(other => StringComparer.OrdinalIgnoreCase.Equals(other.Path, item.Path)))
        {
            return false;
        }

        source.Items.Remove(item);
        ExcludeAutoCollection(source, item.Path);
        AllowAutoCollection(target, item.Path);
        Normalize(source);
        item.Order = target.Items.Count;
        target.Items.Add(item);
        Normalize(target);
        return true;
    }

    /// <summary>
    /// Copies one mapping from <paramref name="source"/> into <paramref name="target"/> while
    /// keeping the source box and original file on disk untouched.
    /// </summary>
    public static bool CopyMapping(
        FileBoxState source,
        FileBoxState target,
        Guid itemId,
        out FileMappingState? copied)
    {
        copied = null;
        var item = source.Items.FirstOrDefault(x => x.Id == itemId);
        if (item is null ||
            ReferenceEquals(source, target) ||
            target.Items.Any(other => StringComparer.OrdinalIgnoreCase.Equals(other.Path, item.Path)))
        {
            return false;
        }

        copied = new FileMappingState
        {
            Path = item.Path,
            Title = item.Title,
            Order = target.Items.Count
        };
        target.Items.Add(copied);
        AllowAutoCollection(target, copied.Path);
        Normalize(target);
        return true;
    }

    public static IReadOnlyDictionary<DesktopGroupCategory, IReadOnlyList<string>>
        ClassifyDesktopEntries(IEnumerable<string> paths) => ClassifyDesktopEntries(paths, temporarilyHiddenPaths: null);

    private static bool IsVisibleDesktopEntry(FileAttributes attributes) =>
        (attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0;

    private static IReadOnlyDictionary<DesktopGroupCategory, IReadOnlyList<string>>
        ClassifyDesktopEntries(IEnumerable<string> paths, ISet<string>? temporarilyHiddenPaths)
    {
        var groups = new Dictionary<DesktopGroupCategory, List<string>>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawPath in paths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            string path;
            try
            {
                path = Path.GetFullPath(rawPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                continue;
            }

            if (!known.Add(path) || GetDesktopCategory(path, temporarilyHiddenPaths) is not { } category)
            {
                continue;
            }

            if (!groups.TryGetValue(category, out var entries))
            {
                entries = [];
                groups.Add(category, entries);
            }
            entries.Add(path);
        }

        return groups.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value
                .OrderBy(GetDisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    public static bool Remove(FileBoxState box, Guid itemId, bool excludeFromAutomaticCollection = true)
    {
        var item = box.Items.FirstOrDefault(candidate => candidate.Id == itemId);
        var removed = box.Items.RemoveAll(x => x.Id == itemId) > 0;
        if (removed)
        {
            if (excludeFromAutomaticCollection)
            {
                ExcludeAutoCollection(box, item!.Path);
            }
            Normalize(box);
        }
        return removed;
    }

    private static void ExcludeAutoCollection(FileBoxState box, string path)
    {
        if (!IsShellEntry(path) && !box.AutoCollectExcludedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            box.AutoCollectExcludedPaths.Add(path);
        }
    }

    private static void AllowAutoCollection(FileBoxState box, string path) =>
        box.AutoCollectExcludedPaths.RemoveAll(excluded => string.Equals(excluded, path, StringComparison.OrdinalIgnoreCase));

    public static bool Move(FileBoxState box, Guid itemId, int offset)
    {
        var ordered = box.Items.OrderBy(x => x.Order).ToList();
        var currentIndex = ordered.FindIndex(x => x.Id == itemId);
        if (currentIndex < 0)
        {
            return false;
        }

        var targetIndex = Math.Clamp(currentIndex + offset, 0, ordered.Count - 1);
        if (targetIndex == currentIndex)
        {
            return false;
        }

        (ordered[currentIndex], ordered[targetIndex]) = (ordered[targetIndex], ordered[currentIndex]);
        ApplyOrder(ordered);
        return true;
    }

    /// <summary>Inserts the dragged mappings next to the target, preserving their relative order.</summary>
    public static bool Reorder(FileBoxState box, IReadOnlyCollection<Guid> itemIds, Guid targetId, bool after)
    {
        var selected = itemIds.ToHashSet();
        if (selected.Count == 0 || selected.Contains(targetId))
        {
            return false;
        }

        var ordered = box.Items.OrderBy(item => item.Order).ToList();
        var moving = ordered.Where(item => selected.Contains(item.Id)).ToList();
        var remaining = ordered.Where(item => !selected.Contains(item.Id)).ToList();
        var targetIndex = remaining.FindIndex(item => item.Id == targetId);
        if (targetIndex < 0 || moving.Count != selected.Count)
        {
            return false;
        }

        remaining.InsertRange(after ? targetIndex + 1 : targetIndex, moving);
        if (remaining.SequenceEqual(ordered))
        {
            return false;
        }

        ApplyOrder(remaining);
        box.Items = remaining;
        return true;
    }

    public static void Sort(FileBoxState box, FileMappingSortMode mode)
    {
        IEnumerable<FileMappingState> ordered = box.Items.OrderBy(x => x.Order);
        ordered = mode switch
        {
            FileMappingSortMode.Name => ordered
                .OrderBy(GetDisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase),
            FileMappingSortMode.Type => ordered
                .OrderBy(item => GetTypeSortKey(item.Path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(GetDisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase),
            // Newest first; a target that no longer exists sinks to the end.
            FileMappingSortMode.Modified => ordered
                .OrderByDescending(item => GetModifiedTime(item.Path))
                .ThenBy(GetDisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase),
            _ => ordered
        };

        ApplyOrder(ordered.ToList());
    }

    public static void Normalize(FileBoxState box)
    {
        var ordered = box.Items.OrderBy(x => x.Order).ToList();
        ApplyOrder(ordered);
        box.Items = ordered;
    }

    private static void ApplyOrder(IReadOnlyList<FileMappingState> ordered)
    {
        for (var index = 0; index < ordered.Count; index++)
        {
            ordered[index].Order = index;
        }
    }

    /// <summary>
    /// The name a mapping shows in its box: the user's title, or the file name with the
    /// shortcut/program suffix hidden the way Explorer hides known extensions. This is
    /// also the name sort key, so tiles and ordering never disagree.
    /// </summary>
    public static string GetDisplayName(FileMappingState item) =>
        string.IsNullOrWhiteSpace(item.Title) ? GetDisplayName(item.Path) : item.Title;

    /// <summary>Whether the path's extension marks a launcher (shortcut, program or
    /// script), the way the desktop's own grouping classifies applications.</summary>
    public static bool IsLauncherName(string path) =>
        ApplicationExtensions.Contains(Path.GetExtension(path));

    public static string GetDisplayName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name))
        {
            return path;
        }

        // Shortcut and program suffixes are noise next to the icon, so they are hidden
        // the way Explorer hides them; a name that is only the suffix keeps it.
        return ApplicationExtensions.Contains(Path.GetExtension(name)) &&
               Path.GetFileNameWithoutExtension(name) is { Length: > 0 } stripped
            ? stripped
            : name;
    }

    private static DateTime GetModifiedTime(string path)
    {
        try
        {
            // Works for folders too; a missing path reports 1601-01-01 and so sorts last.
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return DateTime.MinValue;
        }
    }

    private static string GetTypeSortKey(string path)
    {
        if (Directory.Exists(path))
        {
            return "0|文件夹";
        }

        var extension = Path.GetExtension(path);
        return string.IsNullOrEmpty(extension)
            ? "1|无扩展名"
            : $"2|{extension}";
    }

    private static DesktopGroupCategory? GetDesktopCategory(string path, ISet<string>? temporarilyHiddenPaths)
    {
        if (temporarilyHiddenPaths is not null)
        {
            if (IsPartialDownload(path) || ReadDesktopEntryAttributes(path) is not { } attributes)
            {
                return null;
            }
            if (!IsVisibleDesktopEntry(attributes))
            {
                temporarilyHiddenPaths.Add(path);
                return null;
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                return DesktopGroupCategory.Folders;
            }
        }
        else if (Directory.Exists(path))
        {
            return DesktopGroupCategory.Folders;
        }
        else if (!File.Exists(path))
        {
            return null;
        }

        return ApplicationExtensions.Contains(Path.GetExtension(path))
            ? DesktopGroupCategory.Applications
            : DesktopGroupCategory.Files;
    }
}
