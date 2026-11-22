namespace BingLan.Core.Models;

public enum LauncherFileCategory
{
    Folder,
    Document,
    Image,
    Media,
    Archive,
    Other
}

public static class LauncherFileCategoryRules
{
    public static readonly LauncherFileCategory[] OrderedCategories =
    [
        LauncherFileCategory.Folder,
        LauncherFileCategory.Document,
        LauncherFileCategory.Image,
        LauncherFileCategory.Media,
        LauncherFileCategory.Archive,
        LauncherFileCategory.Other
    ];

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf",
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".rtf",
        ".odt", ".ods", ".odp", ".wps", ".et", ".dps", ".epub",
        ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".log"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg",
        ".ico", ".tif", ".tiff", ".heic", ".avif", ".psd", ".ai",
        ".raw", ".cr2", ".nef", ".dng"
    };

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts",
        ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".wma", ".opus"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".iso", ".cab"
    };

    public static LauncherFileCategory Classify(string path, bool isFolder)
    {
        if (isFolder) return LauncherFileCategory.Folder;
        var extension = SafeExtension(path);
        if (string.IsNullOrEmpty(extension)) return LauncherFileCategory.Other;
        if (DocumentExtensions.Contains(extension)) return LauncherFileCategory.Document;
        if (ImageExtensions.Contains(extension)) return LauncherFileCategory.Image;
        if (MediaExtensions.Contains(extension)) return LauncherFileCategory.Media;
        if (ArchiveExtensions.Contains(extension)) return LauncherFileCategory.Archive;
        return LauncherFileCategory.Other;
    }

    public static string DisplayName(LauncherFileCategory category) => category switch
    {
        LauncherFileCategory.Folder => "文件夹",
        LauncherFileCategory.Document => "文档",
        LauncherFileCategory.Image => "图片",
        LauncherFileCategory.Media => "影音",
        LauncherFileCategory.Archive => "压缩包",
        _ => "其他文件"
    };

    public static string Glyph(LauncherFileCategory category) => category switch
    {
        LauncherFileCategory.Folder => "\uE8B7",
        LauncherFileCategory.Document => "\uE8A5",
        LauncherFileCategory.Image => "\uEB9F",
        LauncherFileCategory.Media => "\uE714",
        LauncherFileCategory.Archive => "\uE7B8",
        _ => "\uE7C3"
    };

    public static string RecentBucket(DateTimeOffset accessedLocal, DateTime today)
    {
        var day = accessedLocal.Date;
        if (day >= today.Date) return "今天";
        if (day >= today.Date.AddDays(-7)) return "本周";
        return "更早";
    }

    public static string FormatRecentTime(DateTimeOffset accessedLocal, DateTime today)
    {
        var day = accessedLocal.Date;
        if (day == today.Date) return $"今天 {accessedLocal:HH:mm}";
        if (day == today.Date.AddDays(-1)) return $"昨天 {accessedLocal:HH:mm}";
        if (day >= today.Date.AddDays(-6)) return $"{(int)(today.Date - day).TotalDays} 天前";
        return accessedLocal.ToString("MM-dd");
    }

    public static string FormatFileSize(long bytes)
    {
        if (bytes < 0) return string.Empty;
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024L) return $"{bytes / 1024d:0.#} KB";
        if (bytes < 1024L * 1024L * 1024L) return $"{bytes / (1024d * 1024d):0.#} MB";
        return $"{bytes / (1024d * 1024d * 1024d):0.##} GB";
    }

    private static string SafeExtension(string path)
    {
        try { return Path.GetExtension(path); }
        catch (ArgumentException) { return string.Empty; }
    }
}
