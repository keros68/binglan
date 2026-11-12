using System.Text.Json;
using System.Text.Json.Serialization;

namespace BingLan.Core.Models;

/// <summary>
/// One icon in the quick-launch row: a preset line icon, a name and what it opens. The
/// target is a folder, program or file path, a "shell:" place, or a "known:" folder that
/// is looked up when clicked (so a moved Downloads folder is still found).
/// </summary>
public sealed class QuickPlaceState
{
    public string Icon { get; set; } = QuickPlaceRules.FolderIcon;
    public string Name { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>A preset icon: its key, Segoe Fluent Icons glyph and name in the picker.</summary>
public sealed record QuickPlaceIcon(string Key, string Glyph, string Name);

public static class QuickPlaceRules
{
    public const int MaximumItems = 12;
    public const int MaximumNameLength = 20;
    public const string FolderIcon = "folder";
    public const string KnownPrefix = "known:";

    public static IReadOnlyList<QuickPlaceIcon> Icons { get; } =
    [
        new("pc", "", "此电脑"),
        new("desktop", "", "桌面"),
        new("documents", "", "文档"),
        new("downloads", "", "下载"),
        new("pictures", "", "图片"),
        new("music", "", "音乐"),
        new("videos", "", "视频"),
        new("recycle", "", "回收站"),
        new(FolderIcon, "", "文件夹"),
        new("browser", "", "浏览器"),
        new("app", "", "应用"),
        new("mail", "", "邮件"),
        new("chat", "", "聊天"),
        new("game", "", "游戏"),
        new("code", "", "代码"),
        new("cloud", "", "云盘"),
        new("headphones", "", "耳机"),
        new("settings", "", "设置"),
        new("star", "", "收藏"),
        new("home", "", "主页")
    ];

    public static List<QuickPlaceState> CreateDefaults() =>
    [
        new() { Icon = "pc", Name = "此电脑", Target = "shell:MyComputerFolder" },
        new() { Icon = "desktop", Name = "桌面", Target = KnownPrefix + "desktop" },
        new() { Icon = "documents", Name = "文档", Target = KnownPrefix + "documents" },
        new() { Icon = "downloads", Name = "下载", Target = KnownPrefix + "downloads" },
        new() { Icon = "pictures", Name = "图片", Target = KnownPrefix + "pictures" },
        new() { Icon = "recycle", Name = "回收站", Target = "shell:RecycleBinFolder" }
    ];

    public static QuickPlaceIcon GetIcon(string? key) =>
        Icons.FirstOrDefault(icon => icon.Key == key) ?? Icons.First(icon => icon.Key == FolderIcon);

    /// <summary>
    /// Drops entries without a target, fixes unknown icons, trims names and keeps at most
    /// MaximumItems; a missing list becomes the default places.
    /// </summary>
    public static List<QuickPlaceState> Normalize(List<QuickPlaceState>? items)
    {
        if (items is null)
        {
            return CreateDefaults();
        }
        return items
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Target))
            .Take(MaximumItems)
            .Select(item => new QuickPlaceState
            {
                Icon = GetIcon(item.Icon).Key,
                Name = (item.Name ?? string.Empty).Trim() is { Length: > 0 } name
                    ? name[..Math.Min(name.Length, MaximumNameLength)]
                    : GetIcon(item.Icon).Name,
                Target = item.Target.Trim(),
                ExtensionData = item.ExtensionData
            })
            .ToList();
    }
}
