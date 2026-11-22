using System.Text.Json;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.Themes;
using BingLan.Core.TopBar;

namespace BingLan.SmokeTests;

internal static class QuickAccessAndHotkeysTests
{
    public static void Run()
    {
        TestGlobalHotkeyRulesAndDeduplication();
        TestSchemaV56MigrationAndRoundTrip();
        TestPinnedFoldersAndOpenCounts();
        TestSearchFrequencyBoost();
        TestFileBoxAutoCollapsePinnedOpen();
    }

    private static void TestGlobalHotkeyRulesAndDeduplication()
    {
        var state = new GlobalHotkeysState();
        GlobalHotkeyRules.Normalize(state);
        Assert((int)GlobalHotkeyKind.Launcher == 0
            && (int)GlobalHotkeyKind.PeekFileBoxes == 1
            && (int)GlobalHotkeyKind.ToggleWidgets == 2
            && (int)GlobalHotkeyKind.QuickTodo == 3
            && (int)GlobalHotkeyKind.ToggleDesktopIcons == 4
            && (int)GlobalHotkeyKind.LauncherFiles == 5,
            "新增 LauncherFiles 必须追加在末尾且不改变 0–4 编号");
        Assert(state.Launcher == "Ctrl+Alt+F", "默认总览卡热键为 Ctrl+Alt+F");
        Assert(state.PeekFileBoxes == "Ctrl+Alt+Space", "默认速览分组盒热键为 Ctrl+Alt+Space");
        Assert(state.ToggleWidgets == string.Empty
            && state.QuickTodo == string.Empty
            && state.ToggleDesktopIcons == string.Empty
            && state.LauncherFiles == string.Empty, "其余热键（含常用文件夹）默认留空");

        // Invalid text on LauncherFiles normalizes back to empty string.
        state.LauncherFiles = "NotAValidKey";
        GlobalHotkeyRules.Normalize(state);
        Assert(state.LauncherFiles == string.Empty, "常用文件夹非法热键规范化后应回退为空");

        // Internal conflict check detects another slot holding the same combination, and
        // registered-map conflict distinguishes BingLan's own slots from other programs.
        Assert(GlobalHotkeyRules.FindInternalConflict(state, GlobalHotkeyKind.LauncherFiles, "ctrl+alt+f") == GlobalHotkeyKind.Launcher,
            "常用文件夹与总览卡使用同组合键应判为内部冲突");
        Assert(GlobalHotkeyRules.FindInternalConflict(state, GlobalHotkeyKind.QuickTodo, "ctrl+alt+f") == GlobalHotkeyKind.Launcher,
            "应检测到与总览卡热键的内部冲突");
        Assert(GlobalHotkeyRules.FindInternalConflict(state, GlobalHotkeyKind.Launcher, "Ctrl+Alt+F") is null,
            "重新录制同一槽位的原热键不算冲突");

        var registered = new Dictionary<GlobalHotkeyKind, string>
        {
            [GlobalHotkeyKind.Launcher] = "Ctrl+Alt+F",
            [GlobalHotkeyKind.PeekFileBoxes] = "Ctrl+Alt+Space"
        };
        var regConflict = GlobalHotkeyRules.FindRegisteredConflict(registered, GlobalHotkeyKind.LauncherFiles, "ctrl+alt+f");
        Assert(regConflict == GlobalHotkeyKind.Launcher, "已登记热键表应识别为冰蓝自身占用");
        var bingLanProbe = GlobalHotkeyProbeResult.OwnedByBingLan(regConflict!.Value);
        Assert(bingLanProbe.ConflictSource == GlobalHotkeyConflictSource.BingLan
            && GlobalHotkeyRules.FormatConflictHint(bingLanProbe) == "已被冰蓝的‘打开或收起冰蓝总览卡’使用",
            "冰蓝自身冲突提示文案应明确指出冲突项");
        Assert(GlobalHotkeyRules.DisplayName(GlobalHotkeyKind.PeekFileBoxes) == "临时浮起文件收纳盒",
            "文件收纳盒浮起快捷键显示名应使用通俗术语");
        Assert(GlobalHotkeyRules.FormatConflictHint(GlobalHotkeyProbeResult.OwnedByOtherProgram) == "已被其他程序占用",
            "外部程序占用提示文案应独立区分");
        Assert(GlobalHotkeyRules.FindRegisteredConflict(registered, GlobalHotkeyKind.Launcher, "Ctrl+Alt+F") is null,
            "重录同一槽位已登记热键不构成冲突");

        // Duplicate slots in state are deduplicated in enum order (earlier slot wins).
        state.ToggleWidgets = "Ctrl+Alt+Space";
        state.LauncherFiles = "Ctrl+Alt+F";
        GlobalHotkeyRules.Normalize(state);
        Assert(state.PeekFileBoxes == "Ctrl+Alt+Space" && state.ToggleWidgets == string.Empty
            && state.Launcher == "Ctrl+Alt+F" && state.LauncherFiles == string.Empty,
            "重复热键规范化时应保留靠前槽位并清空后续冲突槽位");

        // Explicit empty string disables a slot and survives normalization.
        GlobalHotkeyRules.Set(state, GlobalHotkeyKind.Launcher, string.Empty);
        GlobalHotkeyRules.Normalize(state);
        Assert(state.Launcher == string.Empty, "显式清空热键在规范化后应保持为空");

        // Machine-local hotkeys (including LauncherFiles) must never leak into exported themes.
        var exportState = new AppState();
        GlobalHotkeyRules.Set(exportState.Hotkeys, GlobalHotkeyKind.LauncherFiles, "Ctrl+Alt+D");
        var exported = ThemeRules.Export(exportState, "热键隔离主题", new ThemeArea(0, 0, 1920, 1040), new Version(0, 3, 0));
        var exportedJson = JsonSerializer.Serialize(exported);
        Assert(!exportedJson.Contains("LauncherFiles", StringComparison.OrdinalIgnoreCase)
            && !exportedJson.Contains("Ctrl+Alt+D", StringComparison.OrdinalIgnoreCase),
            "主题导出不得包含 LauncherFiles 等本机快捷键");
    }

    private static void TestSchemaV56MigrationAndRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "BingLanSmokeV56_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new LocalStateStore(dir);
            var initial = store.Load();
            initial.TopBar.SearchHotkey = "Alt+Q";
            var v55Node = JsonSerializer.SerializeToNode(initial)!;
            v55Node["SchemaVersion"] = 55;
            v55Node.AsObject().Remove("Hotkeys");
            File.WriteAllText(Path.Combine(dir, "widgets.json"), v55Node.ToJsonString());

            var upgraded = store.Load();
            Assert(upgraded.SchemaVersion == AppState.CurrentSchemaVersion, "v55 应迁移到 Schema v56");
            Assert(upgraded.Hotkeys.Launcher == "Alt+Q", "v56 迁移应承接用户原顶栏搜索热键");
            Assert(upgraded.Hotkeys.PeekFileBoxes == GlobalHotkeysState.DefaultPeekFileBoxesHotkey,
                "v56 迁移应初始化默认速览分组盒热键");

            // Clearing launcher hotkey and setting custom optional hotkeys must survive save/load.
            GlobalHotkeyRules.Set(upgraded.Hotkeys, GlobalHotkeyKind.Launcher, string.Empty);
            GlobalHotkeyRules.Set(upgraded.Hotkeys, GlobalHotkeyKind.QuickTodo, "Ctrl+Alt+T");
            GlobalHotkeyRules.Set(upgraded.Hotkeys, GlobalHotkeyKind.ToggleWidgets, "Ctrl+Alt+H");
            store.Save(upgraded);

            var reloaded = store.Load();
            Assert(reloaded.Hotkeys.Launcher == string.Empty, "清空的总览卡热键重载后应保持为空");
            Assert(reloaded.Hotkeys.QuickTodo == "Ctrl+Alt+T", "快速新建待办热键应持久化");
            Assert(reloaded.Hotkeys.ToggleWidgets == "Ctrl+Alt+H", "隐藏组件热键应持久化");
            // Old v56 state lacking LauncherFiles must load with LauncherFiles empty and keep existing hotkeys untouched.
            var legacyV56Node = JsonSerializer.SerializeToNode(reloaded)!;
            legacyV56Node["SchemaVersion"] = 56;
            legacyV56Node["Hotkeys"]!.AsObject().Remove("LauncherFiles");
            File.WriteAllText(Path.Combine(dir, "widgets.json"), legacyV56Node.ToJsonString());
            var fromLegacyV56 = store.Load();
            Assert(fromLegacyV56.Hotkeys.LauncherFiles == string.Empty
                && fromLegacyV56.Hotkeys.Launcher == string.Empty
                && fromLegacyV56.Hotkeys.QuickTodo == "Ctrl+Alt+T"
                && fromLegacyV56.Hotkeys.ToggleWidgets == "Ctrl+Alt+H",
                "缺少 LauncherFiles 字段的旧状态载入后该项缺省为空，且不改写其余快捷键");

            GlobalHotkeyRules.Set(fromLegacyV56.Hotkeys, GlobalHotkeyKind.LauncherFiles, "Ctrl+Alt+E");
            store.Save(fromLegacyV56);
            var roundTripped = store.Load();
            Assert(roundTripped.Hotkeys.LauncherFiles == "Ctrl+Alt+E", "常用文件夹快捷键应完整持久化往返");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static void TestPinnedFoldersAndOpenCounts()
    {
        var prefs = new LauncherPreferencesState();
        Assert(prefs.PinFolder(@"C:\Projects\Alpha\"), "应允许收藏绝对路径文件夹");
        Assert(!prefs.PinFolder(@"c:\projects\alpha"), "忽略大小写与末尾斜杠去重");
        Assert(!prefs.PinFolder("relative/path"), "拒绝相对路径");
        Assert(prefs.IsFolderPinned(@"C:\Projects\Alpha"), "应能查询已收藏文件夹");

        // Cap enforcement at MaxPinnedFolders.
        for (var i = 0; i < LauncherPreferencesState.MaxPinnedFolders + 5; i++)
        {
            prefs.PinFolder($@"C:\Folders\Item{i}");
        }
        prefs.Normalize();
        Assert(prefs.PinnedFolders.Count == LauncherPreferencesState.MaxPinnedFolders,
            "常用收藏文件夹数量应受上限约束");

        Assert(prefs.UnpinFolder($@"C:\Folders\Item{LauncherPreferencesState.MaxPinnedFolders + 4}"),
            "应支持取消收藏文件夹");

        // Open counts increment and remain bounded.
        for (var i = 0; i < 8; i++)
        {
            prefs.RecordOpen(@"C:\Docs\Hot.docx");
        }
        Assert(prefs.GetOpenCount(@"c:\docs\hot.docx") == 8, "打开频次按路径忽略大小写累计");
        for (var i = 0; i < LauncherPreferencesState.MaxOpenCounts + 25; i++)
        {
            prefs.RecordOpen($@"C:\Docs\File{i}.txt");
        }
        Assert(prefs.OpenCounts.Count <= LauncherPreferencesState.MaxOpenCounts,
            "打开频次字典应自动衰减并保持在上限内");
    }

    private static void TestSearchFrequencyBoost()
    {
        var cold = new TopBarSearchCandidate(TopBarSearchItemKind.File, "Alpha Report", "Alpha Report.docx", @"C:\Docs\Alpha Report.docx");
        var hot = new TopBarSearchCandidate(TopBarSearchItemKind.File, "Beta Report", "Beta Report.docx", @"C:\Docs\Beta Report.docx");
        var exact = new TopBarSearchCandidate(TopBarSearchItemKind.File, "Report", "Report.docx", @"C:\Docs\Report.docx");

        var withoutCounts = TopBarSearchRules.Search([cold, hot], "report");
        Assert(withoutCounts[0] == cold, "无频次数据时按名称顺序 Alpha 在前");

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [hot.Path] = 7
        };
        var withCounts = TopBarSearchRules.Search([cold, hot], "report", openCounts: counts);
        Assert(withCounts[0] == hot, "高频打开的文件在同等匹配下应排到前面");

        var withExact = TopBarSearchRules.Search([cold, hot, exact], "report", openCounts: counts);
        Assert(withExact[0] == exact, "精确匹配仍应高于高频部分匹配");
    }

    private static void TestFileBoxAutoCollapsePinnedOpen()
    {
        Assert(FileBoxAutoCollapseRules.ShouldRunCountdown(
            autoCollapseEnabled: true,
            isCollapsed: false,
            isPointerOver: false,
            isTitleEditing: false,
            isInteractionOpen: false,
            isDragging: false,
            isPinnedOpen: false), "未固定展开时离开应启动 5 秒自动收起计时");

        Assert(!FileBoxAutoCollapseRules.ShouldRunCountdown(
            autoCollapseEnabled: true,
            isCollapsed: false,
            isPointerOver: false,
            isTitleEditing: false,
            isInteractionOpen: false,
            isDragging: false,
            isPinnedOpen: true), "临时固定展开时不得启动自动收起计时");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
