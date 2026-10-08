using BingLan.Core.Models;

namespace BingLan.Core.Services;

public enum FileMappingSortMode
{
    Manual,
    Name,
    Type
}

/// <summary>A named group of file types a box can collect, such as documents or images.</summary>
public sealed record CollectType(string Key, string Name, IReadOnlyList<string> Extensions);

/// <summary>Which box each desktop item goes to, and the items no box collects.</summary>
public sealed record DesktopDistribution(
    IReadOnlyDictionary<Guid, IReadOnlyList<string>> ByBox,
    IReadOnlyDictionary<DesktopGroupCategory, IReadOnlyList<string>> Unclaimed);

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
    public static DesktopDistribution Distribute(IEnumerable<string> paths, IReadOnlyList<FileBoxState> boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        var custom = boxes.Where(box => box.CollectFolders || box.CollectExtensions.Count > 0).ToList();
        var byBox = new Dictionary<Guid, List<string>>();
        var unclaimed = new Dictionary<DesktopGroupCategory, List<string>>();
        foreach (var (category, categoryPaths) in ClassifyDesktopEntries(paths))
        {
            foreach (var path in categoryPaths)
            {
                var isFolder = category == DesktopGroupCategory.Folders;
                var extension = Path.GetExtension(path).ToLowerInvariant();
                var box = custom.FirstOrDefault(candidate => isFolder
                        ? candidate.CollectFolders
                        : candidate.CollectExtensions.Contains(extension))
                    ?? boxes.FirstOrDefault(candidate => candidate.DesktopCategory == category);
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
    /// A missing path whose drive or network share is reachable, so the original was
    /// really deleted or moved. Paths on an unplugged drive or an offline share are not
    /// gone and keep their mapping.
    /// </summary>
    public static bool IsGone(string path)
    {
        if (!IsMissing(path))
        {
            return false;
        }

        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && Directory.Exists(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Drops mappings whose originals are gone; only the records are removed.</summary>
    public static int RemoveGone(FileBoxState box, IReadOnlySet<Guid> goneIds)
    {
        var removed = box.Items.RemoveAll(x => goneIds.Contains(x.Id));
        if (removed > 0)
        {
            Normalize(box);
        }
        return removed;
    }

    /// <summary>
    /// Ids of the mappings whose originals are confirmed deleted (see <see cref="IsGone"/>);
    /// mappings on unplugged drives or offline shares are kept.
    /// </summary>
    public static IReadOnlySet<Guid> CollectGoneIds(IEnumerable<FileMappingState> mappings) =>
        mappings
            .Where(item => IsGone(item.Path))
            .Select(item => item.Id)
            .ToHashSet();

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
                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(entry);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    {
                        continue;
                    }

                    entries.Add(Path.GetFullPath(entry));
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

    public static int AddExisting(FileBoxState box, IEnumerable<string> paths)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var known = box.Items.Select(x => x.Path).ToHashSet(comparer);
        var added = 0;

        foreach (var rawPath in paths)
        {
            string path;
            try
            {
                path = System.IO.Path.GetFullPath(rawPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if ((!File.Exists(path) && !Directory.Exists(path)) || !known.Add(path))
            {
                continue;
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
        Normalize(source);
        item.Order = target.Items.Count;
        target.Items.Add(item);
        Normalize(target);
        return true;
    }

    public static IReadOnlyDictionary<DesktopGroupCategory, IReadOnlyList<string>>
        ClassifyDesktopEntries(IEnumerable<string> paths)
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

            if ((!File.Exists(path) && !Directory.Exists(path)) || !known.Add(path))
            {
                continue;
            }

            var category = GetDesktopCategory(path);
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

    public static bool Remove(FileBoxState box, Guid itemId)
    {
        var removed = box.Items.RemoveAll(x => x.Id == itemId) > 0;
        if (removed)
        {
            Normalize(box);
        }
        return removed;
    }

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

    private static string GetDisplayName(FileMappingState item) =>
        string.IsNullOrWhiteSpace(item.Title) ? GetDisplayName(item.Path) : item.Title;

    private static string GetDisplayName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
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

    private static DesktopGroupCategory GetDesktopCategory(string path)
    {
        if (Directory.Exists(path))
        {
            return DesktopGroupCategory.Folders;
        }

        return ApplicationExtensions.Contains(Path.GetExtension(path))
            ? DesktopGroupCategory.Applications
            : DesktopGroupCategory.Files;
    }
}
