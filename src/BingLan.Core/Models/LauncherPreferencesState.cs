namespace BingLan.Core.Models;

public enum LauncherMainTab
{
    Apps = 0,
    Files = 1,
    Groups = 2
}

public enum LauncherFavoriteMode
{
    FollowDock = 0,
    Independent = 1,
    Off = 2
}

public enum LauncherBackdropMode
{
    LiveWindow = 0,
    WallpaperCanvas = 1,
    ShowDesktop = 2
}

public readonly record struct WallpaperCropRect(int X, int Y, int Width, int Height);

public static class LauncherBackdropRules
{
    /// <summary>
    /// Computes the source-image pixel rectangle that corresponds to a launcher panel
    /// placed on a monitor whose wallpaper fills the screen with UniformToFill scaling.
    /// </summary>
    public static WallpaperCropRect? ComputeWallpaperCrop(
        int imageWidth,
        int imageHeight,
        int monitorLeft,
        int monitorTop,
        int monitorWidth,
        int monitorHeight,
        int panelLeft,
        int panelTop,
        int panelWidth,
        int panelHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || monitorWidth <= 0 || monitorHeight <= 0
            || panelWidth <= 0 || panelHeight <= 0)
        {
            return null;
        }

        // UniformToFill: scale the wallpaper until both axes cover the monitor, centered.
        var scale = Math.Max((double)monitorWidth / imageWidth, (double)monitorHeight / imageHeight);
        var scaledWidth = imageWidth * scale;
        var scaledHeight = imageHeight * scale;
        var offsetX = (scaledWidth - monitorWidth) / 2d;
        var offsetY = (scaledHeight - monitorHeight) / 2d;

        var relLeft = panelLeft - monitorLeft;
        var relTop = panelTop - monitorTop;

        var srcLeft = (relLeft + offsetX) / scale;
        var srcTop = (relTop + offsetY) / scale;
        var srcRight = (relLeft + panelWidth + offsetX) / scale;
        var srcBottom = (relTop + panelHeight + offsetY) / scale;

        var x = Math.Clamp((int)Math.Floor(srcLeft), 0, imageWidth - 1);
        var y = Math.Clamp((int)Math.Floor(srcTop), 0, imageHeight - 1);
        var right = Math.Clamp((int)Math.Ceiling(srcRight), x + 1, imageWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(srcBottom), y + 1, imageHeight);
        return new WallpaperCropRect(x, y, right - x, bottom - y);
    }
}

/// <summary>
/// Machine-local browsing and privacy preferences for the Ice Blue Overview Card.
/// Never exported in theme packs.
/// </summary>
public sealed class LauncherPreferencesState
{
    public const int MaxFavoriteKeys = 64;
    public const int MaxPinnedFolders = 16;
    public const int MaxOpenCounts = 200;

    public LauncherMainTab LastTab { get; set; } = LauncherMainTab.Apps;
    public bool ShowRecentFiles { get; set; } = true;
    public bool AppsSortByName { get; set; }
    public bool FilesSortByModified { get; set; }
    public bool FilesUseListView { get; set; }
    public bool GroupsUseListView { get; set; }
    public bool UseEverything { get; set; }
    public LauncherFavoriteMode FavoriteMode { get; set; } = LauncherFavoriteMode.FollowDock;
    public LauncherBackdropMode BackdropMode { get; set; } = LauncherBackdropMode.LiveWindow;
    public List<string> FavoriteKeys { get; set; } = [];
    public List<string> PinnedFolders { get; set; } = [];
    public Dictionary<string, int> OpenCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Normalize()
    {
        if (!Enum.IsDefined(LastTab))
        {
            LastTab = LauncherMainTab.Apps;
        }
        if (!Enum.IsDefined(FavoriteMode))
        {
            FavoriteMode = LauncherFavoriteMode.FollowDock;
        }
        if (!Enum.IsDefined(BackdropMode))
        {
            BackdropMode = LauncherBackdropMode.LiveWindow;
        }
        FavoriteKeys = (FavoriteKeys ?? [])
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(MaxFavoriteKeys)
            .ToList();
        PinnedFolders = (PinnedFolders ?? [])
            .Select(NormalizeFolderPath)
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxPinnedFolders)
            .ToList();
        var cleanedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (OpenCounts is { Count: > 0 })
        {
            foreach (var pair in OpenCounts
                         .Where(p => !string.IsNullOrWhiteSpace(p.Key) && p.Value > 0)
                         .OrderByDescending(p => p.Value)
                         .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                var key = pair.Key.Trim();
                if (!cleanedCounts.ContainsKey(key))
                {
                    cleanedCounts[key] = pair.Value;
                    if (cleanedCounts.Count >= MaxOpenCounts)
                    {
                        break;
                    }
                }
            }
        }
        OpenCounts = cleanedCounts;
    }

    public static string? NormalizeFolderPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        var trimmed = path.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
        {
            return null;
        }
        try
        {
            var full = Path.GetFullPath(trimmed);
            var root = Path.GetPathRoot(full);
            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                ? full
                : full.TrimEnd('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    public bool IsFolderPinned(string? path)
    {
        if (NormalizeFolderPath(path) is not { } normalized || PinnedFolders is not { Count: > 0 })
        {
            return false;
        }
        return PinnedFolders.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public bool PinFolder(string? path)
    {
        if (NormalizeFolderPath(path) is not { } normalized)
        {
            return false;
        }
        PinnedFolders ??= [];
        if (PinnedFolders.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        if (PinnedFolders.Count >= MaxPinnedFolders)
        {
            PinnedFolders.RemoveAt(0);
        }
        PinnedFolders.Add(normalized);
        return true;
    }

    public bool UnpinFolder(string? path)
    {
        if (NormalizeFolderPath(path) is not { } normalized || PinnedFolders is null)
        {
            return false;
        }
        return PinnedFolders.RemoveAll(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    public void RecordOpen(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }
        OpenCounts ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var trimmed = key.Trim();
        OpenCounts[trimmed] = OpenCounts.TryGetValue(trimmed, out var count)
            ? Math.Min(count + 1, 10000)
            : 1;
        if (OpenCounts.Count > MaxOpenCounts)
        {
            // Halve older counts and prune least-used entries so the dictionary stays bounded.
            foreach (var existingKey in OpenCounts.Keys.ToArray())
            {
                if (string.Equals(existingKey, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var decayed = OpenCounts[existingKey] / 2;
                if (decayed <= 0)
                {
                    OpenCounts.Remove(existingKey);
                }
                else
                {
                    OpenCounts[existingKey] = decayed;
                }
            }
            while (OpenCounts.Count > MaxOpenCounts)
            {
                var lowest = OpenCounts
                    .Where(p => !string.Equals(p.Key, trimmed, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.Value)
                    .Select(p => p.Key)
                    .FirstOrDefault();
                if (lowest is null) break;
                OpenCounts.Remove(lowest);
            }
        }
    }

    public int GetOpenCount(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || OpenCounts is not { Count: > 0 })
        {
            return 0;
        }
        return OpenCounts.TryGetValue(key.Trim(), out var count) ? count : 0;
    }

    public bool IsIndependentFavorite(IEnumerable<string> keys)
    {
        if (FavoriteKeys is not { Count: > 0 }) return false;
        foreach (var key in keys)
        {
            if (FavoriteKeys.Contains(key, StringComparer.Ordinal)) return true;
        }
        return false;
    }

    public bool ToggleFavoriteKey(string primaryKey, IEnumerable<string>? allKeys = null)
    {
        FavoriteKeys ??= [];
        var candidates = allKeys is null ? [primaryKey] : allKeys.Prepend(primaryKey).Distinct(StringComparer.Ordinal).ToArray();
        var removed = FavoriteKeys.RemoveAll(existing => candidates.Contains(existing, StringComparer.Ordinal)) > 0;
        if (removed)
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(primaryKey))
        {
            FavoriteKeys.Add(primaryKey);
            if (FavoriteKeys.Count > MaxFavoriteKeys)
            {
                FavoriteKeys.RemoveRange(0, FavoriteKeys.Count - MaxFavoriteKeys);
            }
            return true;
        }
        return false;
    }
}
