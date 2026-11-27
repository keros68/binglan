namespace BingLan.Core.Models;

/// <summary>
/// Describes one settings navigation page entry.
/// </summary>
public sealed record SettingsNavigationPageDescriptor(
    string PageKey,
    string GroupKey,
    string Title,
    string Subtitle);

/// <summary>
/// Describes one non-clickable navigation group in the settings sidebar.
/// </summary>
public sealed record SettingsNavigationGroup(
    string GroupKey,
    IReadOnlyList<SettingsNavigationPageDescriptor> Pages);

/// <summary>
/// Pure navigation structure, canonical page keys, legacy link/page normalization,
/// and keyboard stepping rules for the settings window.
/// </summary>
public static class SettingsNavigationRules
{
    public const string SchemePageKey = "Scheme";
    public const string DisplaysPageKey = "Displays";
    public const string ComponentsPageKey = "Components";
    public const string AppearancePageKey = "Appearance";
    public const string DockPageKey = "Dock";
    public const string TopBarPageKey = "TopBar";
    public const string TaskbarPageKey = "Taskbar";
    public const string LauncherPageKey = "Launcher";
    public const string GeneralPageKey = "General";

    public const string DefaultPageKey = SchemePageKey;
    public const string LegacyBarsPageKey = "Bars";

    public const string MainGroupKey = "Main";
    public const string FooterGroupKey = "Footer";

    public static IReadOnlyList<SettingsNavigationGroup> Groups { get; } =
    [
        // 首页和显示器在最上；中间按它们在屏幕上的位置从上到下排（桌面卡片、顶部信息条、Dock、任务栏），
        // 再是冰蓝总览卡，最后是作用于以上各部分的「外观样式」；通用设置与具体对象无关，单独放最后。
        new(
            MainGroupKey,
            [
                new(
                    SchemePageKey,
                    MainGroupKey,
                    "桌面方案",
                    "选一套方案，一次设好冰蓝 Dock、顶部信息条和 Windows 任务栏。之后可在左侧的 Dock、顶部信息条、Windows 任务栏页里单独微调。"),
                new(
                    DisplaysPageKey,
                    MainGroupKey,
                    "显示器",
                    "选择屏幕，调整此屏的 Dock 和顶部信息条，或恢复离线档案。"),
                new(
                    ComponentsPageKey,
                    MainGroupKey,
                    "桌面卡片",
                    "选择一张卡片，在右侧调整显示、内容和外观；修改立即生效。"),
                new(
                    TopBarPageKey,
                    MainGroupKey,
                    "顶部信息条",
                    "屏幕最上方的状态栏：显示哪些模块、常驻还是智能隐藏、多高。"),
                new(
                    DockPageKey,
                    MainGroupKey,
                    "冰蓝 Dock",
                    "屏幕底部的应用栏：显示哪些应用、怎么出现、多大。"),
                new(
                    TaskbarPageKey,
                    MainGroupKey,
                    "Windows 任务栏",
                    "系统任务栏的外观和隐藏方式：保持默认、透明、模糊或自动隐藏。"),
                new(
                    LauncherPageKey,
                    MainGroupKey,
                    "冰蓝总览卡",
                    "快速找应用、翻常用文件夹的全局面板（按快捷键随时呼出）。也可从 Dock、顶部信息条搜索或托盘菜单打开。"),
                new(
                    AppearancePageKey,
                    MainGroupKey,
                    "外观样式",
                    "所有桌面卡片、冰蓝总览卡、Dock 和顶部信息条默认跟随这里的共享外观；也可以在下方“各部分外观”单独调整。")
            ]),
        new(
            FooterGroupKey,
            [
                new(
                    GeneralPageKey,
                    FooterGroupKey,
                    "通用设置",
                    "开机启动、检查更新、全局快捷键、备份与数据管理。")
            ])
    ];

    public static IReadOnlyList<SettingsNavigationPageDescriptor> Pages { get; } =
        Groups.SelectMany(group => group.Pages).ToArray();

    private static readonly Dictionary<string, string> CanonicalPageKeys = Pages.ToDictionary(
        page => page.PageKey,
        page => page.PageKey,
        StringComparer.OrdinalIgnoreCase);

    public static bool IsKnownPageKey(string? pageKey) =>
        !string.IsNullOrWhiteSpace(pageKey) && CanonicalPageKeys.ContainsKey(pageKey.Trim());

    /// <summary>
    /// Normalizes a persisted or requested settings page key:
    /// maps legacy "Bars" to "Dock", falls back from unavailable "TopBar" to "Dock",
    /// and falls back any null/unknown key to <see cref="DefaultPageKey"/>.
    /// </summary>
    public static string NormalizePageKey(string? pageKey, bool topBarAvailable = true)
    {
        if (string.IsNullOrWhiteSpace(pageKey))
        {
            return DefaultPageKey;
        }

        var trimmed = pageKey.Trim();
        if (string.Equals(trimmed, LegacyBarsPageKey, StringComparison.OrdinalIgnoreCase))
        {
            return DockPageKey;
        }

        if (CanonicalPageKeys.TryGetValue(trimmed, out var canonical))
        {
            return canonical == TopBarPageKey && !topBarAvailable
                ? DockPageKey
                : canonical;
        }

        return DefaultPageKey;
    }

    /// <summary>
    /// Resolves a current or legacy settings deep link to a canonical page key and optional section.
    /// Unknown links fall back safely to <see cref="DefaultPageKey"/>.
    /// </summary>
    public static (string Page, string? Section) ResolveLink(string? link, bool topBarAvailable = true)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return (DefaultPageKey, null);
        }

        var trimmed = link.Trim();
        if (EqualsIgnoreCase(trimmed, SchemePageKey)
            || EqualsIgnoreCase(trimmed, "DesktopMode")
            || EqualsIgnoreCase(trimmed, "Overall"))
        {
            return (SchemePageKey, null);
        }

        if (EqualsIgnoreCase(trimmed, "CleanDesktop"))
        {
            return (SchemePageKey, "CleanDesktop");
        }

        if (EqualsIgnoreCase(trimmed, DisplaysPageKey))
        {
            return (DisplaysPageKey, null);
        }

        if (EqualsIgnoreCase(trimmed, ComponentsPageKey))
        {
            return (ComponentsPageKey, null);
        }

        if (EqualsIgnoreCase(trimmed, AppearancePageKey) || EqualsIgnoreCase(trimmed, "Cards"))
        {
            return (AppearancePageKey, null);
        }

        if (EqualsIgnoreCase(trimmed, "Layout") || EqualsIgnoreCase(trimmed, "Theme"))
        {
            return (AppearancePageKey, "Layout");
        }

        if (EqualsIgnoreCase(trimmed, LegacyBarsPageKey) || EqualsIgnoreCase(trimmed, DockPageKey))
        {
            return (DockPageKey, null);
        }

        if (EqualsIgnoreCase(trimmed, TopBarPageKey))
        {
            return (topBarAvailable ? TopBarPageKey : DockPageKey, null);
        }

        if (EqualsIgnoreCase(trimmed, TaskbarPageKey))
        {
            return (TaskbarPageKey, null);
        }

        if (EqualsIgnoreCase(trimmed, LauncherPageKey))
        {
            return (LauncherPageKey, null);
        }

        if (EqualsIgnoreCase(trimmed, GeneralPageKey)
            || EqualsIgnoreCase(trimmed, "About")
            || EqualsIgnoreCase(trimmed, "Update"))
        {
            return (GeneralPageKey, null);
        }

        if (EqualsIgnoreCase(trimmed, "Privacy"))
        {
            return (GeneralPageKey, "Privacy");
        }

        if (EqualsIgnoreCase(trimmed, "Backup"))
        {
            return (GeneralPageKey, "Backup");
        }

        return (DefaultPageKey, null);
    }

    /// <summary>
    /// Finds the next selectable navigation list index when moving up (-1) or down (+1),
    /// skipping non-clickable group headers and hidden items. Returns <paramref name="currentIndex"/>
    /// when already at the boundary (or -1 if no selectable item exists).
    /// </summary>
    public static int FindNextSelectableIndex(IReadOnlyList<bool> isSelectable, int currentIndex, int direction)
    {
        ArgumentNullException.ThrowIfNull(isSelectable);
        if (isSelectable.Count == 0 || direction == 0)
        {
            return -1;
        }

        var step = direction > 0 ? 1 : -1;
        var index = currentIndex;
        if (index < 0 || index >= isSelectable.Count)
        {
            return FindEdgeSelectableIndex(isSelectable, fromStart: step > 0);
        }

        for (var candidate = index + step; candidate >= 0 && candidate < isSelectable.Count; candidate += step)
        {
            if (isSelectable[candidate])
            {
                return candidate;
            }
        }

        return isSelectable[index] ? index : FindEdgeSelectableIndex(isSelectable, fromStart: step > 0);
    }

    public static int FindEdgeSelectableIndex(IReadOnlyList<bool> isSelectable, bool fromStart)
    {
        ArgumentNullException.ThrowIfNull(isSelectable);
        if (fromStart)
        {
            for (var i = 0; i < isSelectable.Count; i++)
            {
                if (isSelectable[i])
                {
                    return i;
                }
            }
        }
        else
        {
            for (var i = isSelectable.Count - 1; i >= 0; i--)
            {
                if (isSelectable[i])
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static bool EqualsIgnoreCase(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
