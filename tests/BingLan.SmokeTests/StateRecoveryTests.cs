using System.IO;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.Themes;

/// <summary>Starting up with a state file or theme package that cannot be used as is.</summary>
internal static class StateRecoveryTests
{
    internal static void TestUnreadableStateStartsFromDefaults()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-state-recovery-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temp);
            var store = new LocalStateStore(temp);
            File.WriteAllText(store.StatePath, "{ not json");
            Check(store.Load().NoteWidgets.Count == 0, "主文件和备份都损坏时应以默认状态启动");
            Check(store.ListBackups().Any(path => Path.GetFileName(path).StartsWith("widgets-unreadable-", StringComparison.Ordinal)),
                "无法读取的状态文件应留在备份里");

            File.WriteAllText(store.StatePath, $"{{\"SchemaVersion\":{AppState.CurrentSchemaVersion + 1}}}");
            File.Delete(store.StatePath + ".bak");
            store.Load();
            Check(File.ReadAllText(store.StatePath).Contains($"{AppState.CurrentSchemaVersion + 1}", StringComparison.Ordinal),
                "新版本写的状态文件在加载时不应被改写");

            var newer = Path.Combine(temp, "newer.json");
            File.WriteAllText(newer, $"{{\"SchemaVersion\":{AppState.CurrentSchemaVersion + 1}}}");
            Check(LocalStateStore.DescribeBackup(newer) is null, "新版本写的备份应返回空摘要而不是出错");

            File.WriteAllText(store.StatePath, $"{{\"SchemaVersion\":{AppState.CurrentSchemaVersion},\"TodoWidgets\":null,\"FileBoxes\":null}}");
            var nulls = store.Load();
            Check(nulls.TodoWidgets is not null && nulls.FileBoxes is not null, "空值列表应被补成空列表");

            File.WriteAllText(store.StatePath, "{\"SchemaVersion\":18,\"DismissedDesktopCategories\":[0,2,2,9]}");
            var v18 = store.Load();
            Check(v18.SchemaVersion == AppState.CurrentSchemaVersion
                && v18.DismissedDesktopCategories.SequenceEqual([DesktopGroupCategory.Folders]),
                "v18 迁移后已删除的分类只保留有效且不重复的项");
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, true);
            }
        }
    }

    internal static void TestHugePngIsRejected()
    {
        var png = ThemePreviewTests.BuildMinimalPng();
        Check(ThemeArchive.IsValidPng(png, ThemeArchive.MaximumIconBytes), "1×1 PNG 应可用");

        // Same bytes, header claiming 30000 × 30000 pixels.
        var huge = (byte[])png.Clone();
        huge[16] = 0x00; huge[17] = 0x00; huge[18] = 0x75; huge[19] = 0x30;
        huge[20] = 0x00; huge[21] = 0x00; huge[22] = 0x75; huge[23] = 0x30;
        Check(!ThemeArchive.IsValidPng(huge, ThemeArchive.MaximumIconBytes), "像素尺寸过大的 PNG 应被拒绝");
    }

    internal static void TestTodoCollapseStateMigratesAndPersists()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-todo-collapse-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temp);
            var store = new LocalStateStore(temp);
            // v34 文件没有折叠字段：迁移后应保持展开，而不是误收起。
            File.WriteAllText(
                store.StatePath,
                """
                {"SchemaVersion":34,"TodoWidgets":[{"Title":"官方待办","WeeklyView":true,"WeeklyWeekCount":2,
                "Items":[{"Id":"00000000-0000-0000-0000-000000000001","Text":"跨版本条目","Date":"2026-10-28T00:00:00"}]}]}
                """);
            var state = store.Load();
            var migrated = state.TodoWidgets.Single();
            Check(state.SchemaVersion == AppState.CurrentSchemaVersion
                && !migrated.IsCollapsed
                && migrated.ExpandedHeight == 0
                && migrated.WeeklyView
                && migrated.WeeklyWeekCount == 2
                && migrated.Items.Single().Text == "跨版本条目",
                "v34 待办迁移到当前版本后应保持展开并保留周历与条目");

            // 折叠状态与展开高度保存后应原样读回；手工改坏的高度回到 0。
            migrated.IsCollapsed = true;
            migrated.ExpandedHeight = 336;
            store.Save(state);
            var reloaded = store.Load().TodoWidgets.Single();
            Check(reloaded.IsCollapsed && reloaded.ExpandedHeight == 336,
                "待办折叠状态与展开高度应随状态文件持久化");
            var corrupted = store.Load();
            corrupted.TodoWidgets.Single().ExpandedHeight = -40;
            store.Save(corrupted);
            Check(store.Load().TodoWidgets.Single().ExpandedHeight == 0,
                "负的展开高度应被归一化为 0");
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, true);
            }
        }
    }

    internal static void TestQuickPlaceRules()
    {
        var defaults = QuickPlaceRules.Normalize(null);
        Check(defaults.Select(item => item.Name).SequenceEqual(["此电脑", "桌面", "文档", "下载", "图片", "回收站"]),
            "没有保存过的快捷入口应是默认的六个系统位置");
        var many = Enumerable.Range(0, 20)
            .Select(index => new QuickPlaceState { Icon = "browser", Name = $"站点{index}", Target = $@"F:\站点{index}" })
            .ToList();
        many.Insert(0, new QuickPlaceState { Icon = "nope", Name = "", Target = @"F:\下载" });
        many.Insert(1, new QuickPlaceState { Icon = "music", Name = "空", Target = " " });
        var normalized = QuickPlaceRules.Normalize(many);
        Check(normalized.Count == QuickPlaceRules.MaximumItems, "快捷入口最多保留 12 个");
        Check(normalized[0] is { Icon: QuickPlaceRules.FolderIcon, Name: "文件夹", Target: @"F:\下载" },
            "未知图标回到文件夹图标，空名称用图标名");
        Check(normalized.All(item => item.Name != "空"), "没有位置的项不保存");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
