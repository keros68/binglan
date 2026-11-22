using System.IO;
using System.Text.Json;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.Themes;

namespace BingLan.SmokeTests;

internal static class SettingsNavigationRulesTests
{
    public static void Run()
    {
        // 1. 导航结构：首页在最上，中间六页按屏幕位置排列，通用设置单独放最后（前面一条分隔线）。
        var groups = SettingsNavigationRules.Groups;
        Check(groups.Count == 2, $"导航分组应为 2 组，实际 {groups.Count} 组");
        Check(groups.Select(group => group.GroupKey).SequenceEqual(
                [SettingsNavigationRules.MainGroupKey, SettingsNavigationRules.FooterGroupKey]),
            "导航分组顺序应为：主体页面、通用设置");

        Check(groups[0].Pages.Select(page => (page.PageKey, page.Title)).SequenceEqual([
                (SettingsNavigationRules.SchemePageKey, "桌面方案"),
                (SettingsNavigationRules.ComponentsPageKey, "桌面卡片"),
                (SettingsNavigationRules.TopBarPageKey, "顶部信息条"),
                (SettingsNavigationRules.DockPageKey, "冰蓝 Dock"),
                (SettingsNavigationRules.TaskbarPageKey, "Windows 任务栏"),
                (SettingsNavigationRules.LauncherPageKey, "冰蓝总览卡"),
                (SettingsNavigationRules.AppearancePageKey, "外观样式")
            ]),
            "主体页面清单与顺序不匹配");
        Check(groups[1].Pages.Select(page => (page.PageKey, page.Title)).SequenceEqual([
                (SettingsNavigationRules.GeneralPageKey, "通用设置")
            ]),
            "末尾分组只应包含通用设置");

        var flatPages = SettingsNavigationRules.Pages;
        Check(flatPages.Count == 8, $"扁平页面清单应为 8 页，实际 {flatPages.Count} 页");
        Check(flatPages.SequenceEqual(groups.SelectMany(group => group.Pages)),
            "Pages 清单必须与 Groups 展开顺序完全一致");

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in flatPages)
        {
            Check(!string.IsNullOrWhiteSpace(page.PageKey), "页面键不能为空");
            Check(seenKeys.Add(page.PageKey), $"页面键不得重复：{page.PageKey}");
            Check(SettingsNavigationRules.IsKnownPageKey(page.PageKey),
                $"IsKnownPageKey 应识别已登记页面键：{page.PageKey}");
            Check(!string.IsNullOrWhiteSpace(page.Title), $"页面 {page.PageKey} 标题不能为空");
            Check(!string.IsNullOrWhiteSpace(page.Subtitle), $"页面 {page.PageKey} 副标题不能为空");
            Check(!UserFacingTerms.ContainsInternalIdentifier(page.Title)
                    && !UserFacingTerms.ContainsInternalIdentifier(page.Subtitle),
                $"页面 {page.PageKey} 标题或副标题不得包含内部英文标识");
        }
        Check(!SettingsNavigationRules.IsKnownPageKey(SettingsNavigationRules.LegacyBarsPageKey),
            "旧页面键 Bars 不应再作为现行页面键");

        // 2. 页面键规范化：旧「Bars」映射到 Dock，未知键回退到首页（Scheme）。
        Check(SettingsNavigationRules.NormalizePageKey(null) == SettingsNavigationRules.SchemePageKey
                && SettingsNavigationRules.NormalizePageKey("") == SettingsNavigationRules.SchemePageKey
                && SettingsNavigationRules.NormalizePageKey("   ") == SettingsNavigationRules.SchemePageKey
                && SettingsNavigationRules.NormalizePageKey("UnknownPage") == SettingsNavigationRules.SchemePageKey
                && SettingsNavigationRules.NormalizePageKey("ObsoleteTab") == SettingsNavigationRules.SchemePageKey,
            "空值或未知页面键必须回退到首页 Scheme");

        Check(SettingsNavigationRules.NormalizePageKey("Bars") == SettingsNavigationRules.DockPageKey
                && SettingsNavigationRules.NormalizePageKey("bars") == SettingsNavigationRules.DockPageKey
                && SettingsNavigationRules.NormalizePageKey("  Bars ") == SettingsNavigationRules.DockPageKey,
            "旧页面键 Bars 必须迁移映射到 Dock");

        foreach (var page in flatPages)
        {
            Check(SettingsNavigationRules.NormalizePageKey(page.PageKey) == page.PageKey,
                $"合法页面键 {page.PageKey} 规范化后应保持不变");
            Check(SettingsNavigationRules.NormalizePageKey(page.PageKey.ToLowerInvariant()) == page.PageKey,
                $"大小写不同的合法页面键 {page.PageKey} 应规范化为标准大小写");
        }

        Check(SettingsNavigationRules.NormalizePageKey(SettingsNavigationRules.TopBarPageKey, topBarAvailable: false)
                == SettingsNavigationRules.DockPageKey,
            "顶部信息条不可用时 TopBar 应回退到 Dock");

        // 3. 深链解析：Bars/Dock/TopBar/Taskbar 分别映射到独立页，未知深链安全回退首页。
        Check(SettingsNavigationRules.ResolveLink("Bars") == (SettingsNavigationRules.DockPageKey, null),
            "旧入口 Bars 应映射到 Dock 页");
        Check(SettingsNavigationRules.ResolveLink("Dock") == (SettingsNavigationRules.DockPageKey, null)
                && SettingsNavigationRules.ResolveLink("TopBar") == (SettingsNavigationRules.TopBarPageKey, null)
                && SettingsNavigationRules.ResolveLink("Taskbar") == (SettingsNavigationRules.TaskbarPageKey, null),
            "Dock/TopBar/Taskbar 应分别映射到各自独立页");
        Check(SettingsNavigationRules.ResolveLink("TopBar", topBarAvailable: false)
                == (SettingsNavigationRules.DockPageKey, null),
            "顶部信息条不可用时 TopBar 深链应回退到 Dock 页");
        Check(SettingsNavigationRules.ResolveLink("Layout") == (SettingsNavigationRules.AppearancePageKey, "Layout")
                && SettingsNavigationRules.ResolveLink("Theme") == (SettingsNavigationRules.AppearancePageKey, "Layout")
                && SettingsNavigationRules.ResolveLink("CleanDesktop") == (SettingsNavigationRules.SchemePageKey, "CleanDesktop")
                && SettingsNavigationRules.ResolveLink("About") == (SettingsNavigationRules.GeneralPageKey, null)
                && SettingsNavigationRules.ResolveLink("Backup") == (SettingsNavigationRules.GeneralPageKey, "Backup")
                && SettingsNavigationRules.ResolveLink("Privacy") == (SettingsNavigationRules.GeneralPageKey, "Privacy"),
            "既有深链映射应保持完整兼容");
        Check(SettingsNavigationRules.ResolveLink("NoSuchPage") == (SettingsNavigationRules.SchemePageKey, null)
                && SettingsNavigationRules.ResolveLink(null) == (SettingsNavigationRules.SchemePageKey, null),
            "未知深链应安全回退到首页 Scheme 而非空白页");

        // 4. 键盘上下/Home/End 移动时跳过不可点击的分隔线与隐藏项。
        // [0:Scheme, 1:Components, 2:TopBar, 3:Dock, 4:Taskbar, 5:Launcher, 6:Appearance, 7:分隔线, 8:General]
        bool[] fullSidebar = [true, true, true, true, true, true, true, false, true];
        Check(SettingsNavigationRules.FindNextSelectableIndex(fullSidebar, 6, +1) == 8,
            "向下移动从外观样式应跳过分隔线落到通用设置");
        Check(SettingsNavigationRules.FindNextSelectableIndex(fullSidebar, 8, -1) == 6,
            "向上移动从通用设置应跳过分隔线落到外观样式");
        Check(SettingsNavigationRules.FindNextSelectableIndex(fullSidebar, 0, -1) == 0,
            "位于首项时向上移动应停在首项");
        Check(SettingsNavigationRules.FindNextSelectableIndex(fullSidebar, 8, +1) == 8,
            "位于末项时向下移动应停在末项");
        Check(SettingsNavigationRules.FindEdgeSelectableIndex(fullSidebar, fromStart: true) == 0
                && SettingsNavigationRules.FindEdgeSelectableIndex(fullSidebar, fromStart: false) == 8,
            "Home/End 应分别落到首个与最后一个可选导航项");

        bool[] withoutTopBar = [true, true, false, true, true, true, true, false, true];
        Check(SettingsNavigationRules.FindNextSelectableIndex(withoutTopBar, 1, +1) == 3
                && SettingsNavigationRules.FindNextSelectableIndex(withoutTopBar, 3, -1) == 1,
            "隐藏顶部信息条时上下键应同时跳过隐藏项");

        // 5. 本地持久化 LastSettingsPage 迁移（Bars -> Dock，未知键 -> Scheme，不进入主题包）。
        var tempDir = Path.Combine(Path.GetTempPath(), $"BingLan-nav-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new LocalStateStore(tempDir);
            var statePath = store.StatePath;

            File.WriteAllText(statePath, """{"SchemaVersion":57,"LastSettingsPage":"Bars"}""");
            var migratedBars = store.Load();
            Check(migratedBars.LastSettingsPage == SettingsNavigationRules.DockPageKey,
                $"旧持久化值 Bars 应迁移为 Dock，实际：{migratedBars.LastSettingsPage}");

            File.WriteAllText(statePath, """{"SchemaVersion":57,"LastSettingsPage":"NonExistentPage"}""");
            var migratedUnknown = store.Load();
            Check(migratedUnknown.LastSettingsPage == SettingsNavigationRules.SchemePageKey,
                $"未知持久化页面键应回退到 Scheme，实际：{migratedUnknown.LastSettingsPage}");

            File.WriteAllText(statePath, """{"SchemaVersion":57}""");
            var missingField = store.Load();
            Check(missingField.LastSettingsPage is null
                    && SettingsNavigationRules.NormalizePageKey(missingField.LastSettingsPage) == SettingsNavigationRules.SchemePageKey,
                "缺省 LastSettingsPage 时默认应回落到首页 Scheme");

            var validState = new AppState { LastSettingsPage = SettingsNavigationRules.TaskbarPageKey };
            store.Save(validState);
            var reloaded = store.Load();
            Check(reloaded.LastSettingsPage == SettingsNavigationRules.TaskbarPageKey,
                "合法页面键 Taskbar 保存重载后应保持不变");

            var theme = ThemeRules.Export(validState, "NavTest", new ThemeArea(0, 0, 1920, 1080), new Version(0, 3, 0));
            var themeJson = JsonSerializer.Serialize(theme);
            Check(!themeJson.Contains("LastSettingsPage", StringComparison.OrdinalIgnoreCase),
                "导出主题不得包含本机导航页记忆 LastSettingsPage");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
