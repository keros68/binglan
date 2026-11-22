using System.Text.RegularExpressions;

namespace BingLan.Core.Models;

/// <summary>
/// Maps one legacy or internal user-facing term to its plain-language display wording
/// and the search synonyms retained for settings search compatibility.
/// </summary>
public sealed record UserFacingTermEntry(
    string Key,
    string LegacyTerm,
    string DisplayText,
    string DescriptionHint,
    IReadOnlyList<string> SearchSynonyms);

/// <summary>
/// Canonical table of plain-language display terms used across settings, tray menu,
/// onboarding, desktop windows and documentation, together with search synonyms that keep
/// legacy terms discoverable in settings search.
/// </summary>
public static partial class UserFacingTerms
{
    private static readonly HashSet<string> AllowedUserFacingTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows",
        "Dock",
        "Esc",
        "Ctrl",
        "Alt",
        "Shift",
        "Win",
        "Tab",
        "Enter",
        "Space",
        "Wi",
        "Fi",
        "WLAN",
        "Everything",
        "Rainmeter",
        "macOS",
        "CPU",
        "RAM",
        "GitHub"
    };

    public const string LauncherKey = "Launcher";
    public const string DesktopFileBoxKey = "DesktopFileBox";
    public const string FileBoxShortKey = "FileBoxShort";
    public const string PeekFileBoxesKey = "PeekFileBoxes";
    public const string BarsNavigationKey = "BarsNavigation";
    public const string DesktopCardsKey = "DesktopCards";
    public const string TopBarKey = "TopBar";
    public const string CleanDesktopKey = "CleanDesktop";
    public const string ClearGlassKey = "ClearGlass";
    public const string CardSpacingKey = "CardSpacing";
    public const string LauncherBackdropLiveKey = "LauncherBackdropLive";
    public const string LauncherBackdropWallpaperKey = "LauncherBackdropWallpaper";
    public const string LauncherBackdropShowDesktopKey = "LauncherBackdropShowDesktop";

    public static IReadOnlyList<UserFacingTermEntry> Entries { get; } =
    [
        new(
            LauncherKey,
            LegacyTerm: "冰蓝总览卡",
            DisplayText: "冰蓝总览卡",
            DescriptionHint: "快速找应用、翻常用文件夹的全局面板（按快捷键随时呼出）",
            SearchSynonyms: ["冰蓝总览卡", "总览卡", "启动台", "启动面板", "Launcher"]),
        new(
            DesktopFileBoxKey,
            LegacyTerm: "桌面分组盒",
            DisplayText: "文件收纳盒",
            DescriptionHint: "把常用文件归类放桌面上，只记路径不搬原文件",
            SearchSynonyms: ["桌面分组盒", "分组盒", "文件盒", "围栏", "Fences"]),
        new(
            FileBoxShortKey,
            LegacyTerm: "分组盒",
            DisplayText: "文件收纳盒",
            DescriptionHint: "把常用文件归类放桌面上，只记路径不搬原文件",
            SearchSynonyms: ["分组盒", "桌面分组盒", "文件盒", "围栏", "Fences"]),
        new(
            PeekFileBoxesKey,
            LegacyTerm: "临时速览桌面分组盒",
            DisplayText: "临时浮起文件收纳盒",
            DescriptionHint: "不回桌面，按快捷键把桌面的文件盒浮到当前窗口上层拿文件或拖文件，松开或按 Esc 退回",
            SearchSynonyms: ["临时速览桌面分组盒", "临时速览", "速览", "Peek", "唤出", "分组盒"]),
        new(
            BarsNavigationKey,
            LegacyTerm: "顶栏与底栏",
            DisplayText: "冰蓝 Dock / 顶部信息条 / Windows 任务栏",
            DescriptionHint: "在左侧分别独立管理冰蓝 Dock、顶部信息条和 Windows 任务栏页",
            SearchSynonyms: ["顶栏与底栏", "顶栏底栏", "Dock", "任务栏", "状态栏"]),
        new(
            DesktopCardsKey,
            LegacyTerm: "桌面组件",
            DisplayText: "桌面卡片",
            DescriptionHint: "管理桌面上的信息卡片、待办、便签和文件收纳盒",
            SearchSynonyms: ["桌面组件", "组件", "桌面卡片", "卡片"]),
        new(
            TopBarKey,
            LegacyTerm: "顶端信息条",
            DisplayText: "顶部信息条",
            DescriptionHint: "屏幕最上方的状态栏（时间、天气、网速、音量等）",
            SearchSynonyms: ["顶端信息条", "顶栏", "状态栏", "菜单栏"]),
        new(
            CleanDesktopKey,
            LegacyTerm: "清爽桌面",
            DisplayText: "隐藏 Windows 桌面图标",
            DescriptionHint: "只显示冰蓝卡片和文件收纳盒，桌面更干净；关闭或退出软件自动恢复原图标",
            SearchSynonyms: ["清爽桌面", "隐藏桌面图标", "桌面图标"]),
        new(
            ClearGlassKey,
            LegacyTerm: "无底板",
            DisplayText: "纯透明（无底板）",
            DescriptionHint: "去掉卡片背景板，文字直接浮在壁纸上",
            SearchSynonyms: ["无底板", "裸文字", "透明"]),
        new(
            CardSpacingKey,
            LegacyTerm: "吸附间距",
            DisplayText: "卡片间自动留白",
            DescriptionHint: "拖动卡片靠近其他卡片时保留的距离",
            SearchSynonyms: ["吸附间距", "间距", "磁吸"]),
        new(
            LauncherBackdropLiveKey,
            LegacyTerm: "实时透视",
            DisplayText: "直接浮在当前窗口上（默认）",
            DescriptionHint: "直接叠加在当前窗口上方，最快捷",
            SearchSynonyms: ["实时透视", "动态壁纸"]),
        new(
            LauncherBackdropWallpaperKey,
            LegacyTerm: "静态壁纸底衬",
            DisplayText: "用桌面壁纸作底",
            DescriptionHint: "在面板内展示当前系统壁纸，不收起窗口",
            SearchSynonyms: ["静态壁纸底衬", "壁纸", "动态壁纸"]),
        new(
            LauncherBackdropShowDesktopKey,
            LegacyTerm: "弹出时切到桌面",
            DisplayText: "先切回桌面（适合动态壁纸）",
            DescriptionHint: "先切回桌面再展开，自动收起其他窗口，适合动态壁纸（按 Esc 还原）",
            SearchSynonyms: ["弹出时切到桌面", "切到桌面", "动态壁纸"])
    ];

    public static UserFacingTermEntry? FindByKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Key, key, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    public static UserFacingTermEntry? FindByLegacyTerm(string? legacyTerm)
    {
        if (string.IsNullOrWhiteSpace(legacyTerm))
        {
            return null;
        }

        foreach (var entry in Entries)
        {
            if (string.Equals(entry.LegacyTerm, legacyTerm, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true if <paramref name="text"/> contains an English identifier token that is not
    /// an approved user-facing product or keyboard name (e.g. code symbols like FileBox, CleanDesktop,
    /// LauncherBackdropMode, TopBarState, GlassMode, SettingsRow or snake_case names).
    /// </summary>
    public static bool ContainsInternalIdentifier(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (Match match in AsciiIdentifierRegex().Matches(text))
        {
            var token = match.Value;
            if (token.Contains('_') || !AllowedUserFacingTokens.Contains(token))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex AsciiIdentifierRegex();
}
