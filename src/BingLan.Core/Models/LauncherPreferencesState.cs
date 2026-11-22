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
