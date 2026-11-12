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

            File.WriteAllText(
                store.StatePath,
                $$"""{"SchemaVersion":{{AppState.CurrentSchemaVersion + 3}},"FutureFlag":{"Enabled":true},"DesktopExperience":{"Components":[{"Kind":99,"IsVisible":true,"FutureCompFlag":7}]},"QuickPlaces":[{"Icon":"folder","Name":"未来入口","Target":"C:\\Future","FuturePlaceFlag":true}],"NoteWidgets":[{"Title":"未来便签","Content":"不应被清空","FutureNoteFlag":42}]}""");
            File.Delete(store.StatePath + ".bak");
            var futureLoaded = store.Load();
            Check(File.ReadAllText(store.StatePath).Contains($"{AppState.CurrentSchemaVersion + 3}", StringComparison.Ordinal),
                "新版本写的状态文件在加载时不应被改写");
            Check(futureLoaded.NoteWidgets.Count == 1
                && futureLoaded.NoteWidgets[0].Title == "未来便签"
                && futureLoaded.NoteWidgets[0].Content == "不应被清空",
                "新版本状态文件加载时应保留已知用户内容而不是清空回退默认");
            Check(store.ListBackups().Count(path => Path.GetFileName(path).StartsWith("widgets-newer-schema-", StringComparison.Ordinal)) == 1,
                "打开高版本状态文件时应留存一份高版本备份快照");

            var newer = Path.Combine(temp, "newer.json");
            File.WriteAllText(newer, $"{{\"SchemaVersion\":{AppState.CurrentSchemaVersion + 1}}}");
            Check(LocalStateStore.DescribeBackup(newer) is not null, "新版本写的备份应可安全读取摘要");

            store.Save(futureLoaded);
            var savedRaw = File.ReadAllText(store.StatePath);
            Check(savedRaw.Contains($"\"SchemaVersion\": {AppState.CurrentSchemaVersion}", StringComparison.Ordinal)
                && savedRaw.Contains("\"FutureFlag\"", StringComparison.Ordinal)
                && savedRaw.Contains("\"FutureNoteFlag\": 42", StringComparison.Ordinal)
                && savedRaw.Contains("\"Kind\": 99", StringComparison.Ordinal)
                && savedRaw.Contains("\"FutureCompFlag\": 7", StringComparison.Ordinal)
                && savedRaw.Contains("\"FuturePlaceFlag\": true", StringComparison.Ordinal),
                "保存高版本状态时应写当前 SchemaVersion 并保留顶层、嵌套及未来未知枚举扩展字段");
            _ = store.Load();
            Check(store.ListBackups().Count(path => Path.GetFileName(path).StartsWith("widgets-newer-schema-", StringComparison.Ordinal)) == 1,
                "回退版保存后再次启动不应重复生成高版本备份冲掉原版快照");

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

            // v44 -> v45：待办组件的字号与字体迁移到每张待办卡片，且多张待办卡各自持久化字号与字体。
            File.WriteAllText(
                store.StatePath,
                """
                {"SchemaVersion":44,
                "DesktopExperience":{"Components":[{"Kind":4,"IsVisible":true,"FontScale":1.35,"FontFamily":"Microsoft YaHei"}]},
                "TodoWidgets":[{"Title":"主待办"},{"Title":"副待办"}]}
                """);
            var v45 = store.Load();
            Check(v45.TodoWidgets.Count == 2
                && Math.Abs(v45.TodoWidgets[0].FontScale - 1.35) < 1e-6
                && v45.TodoWidgets[0].FontFamily == "Microsoft YaHei"
                && Math.Abs(v45.TodoWidgets[1].FontScale - 1.35) < 1e-6,
                "v44 迁移到 v45 时应将待办组件字号与字体同步到已有待办卡片");
            v45.TodoWidgets[1].FontScale = 0.9;
            v45.TodoWidgets[1].FontFamily = "SimSun";
            store.Save(v45);
            var reloadedMulti = store.Load();
            Check(Math.Abs(reloadedMulti.TodoWidgets[0].FontScale - 1.35) < 1e-6
                && Math.Abs(reloadedMulti.TodoWidgets[1].FontScale - 0.9) < 1e-6
                && reloadedMulti.TodoWidgets[1].FontFamily == "SimSun",
                "第二张待办卡片应能独立持久化自己的字号与字体");

            // 自动备份上限裁剪：保留手动备份，自动备份最多保留 MaximumAutomaticBackups 份，且旧状态文件迁移快照不会被误删。
            var manualBackup = store.CreateBackup(reloadedMulti);
            for (var i = 0; i < 22; i++)
            {
                File.Delete(store.StatePath + ".bak");
                File.WriteAllText(store.StatePath, "{ invalid json " + i);
                _ = store.Load();
            }
            File.WriteAllText(store.StatePath, """{"SchemaVersion":44,"TodoWidgets":[{"Title":"旧文件待迁移"}]}""");
            File.SetLastWriteTimeUtc(store.StatePath, DateTime.UtcNow.AddDays(-30));
            var migratedOldFile = store.Load();
            store.Save(migratedOldFile);

            var backups = store.ListBackups();
            var autoCount = backups.Count(path =>
            {
                var name = Path.GetFileName(path);
                return name.StartsWith("widgets-before-migration-", StringComparison.Ordinal)
                    || name.StartsWith("widgets-unreadable-", StringComparison.Ordinal)
                    || name.StartsWith("widgets-newer-schema-", StringComparison.Ordinal);
            });
            Check(backups.Contains(manualBackup), "自动备份裁剪不得删除用户手动备份");
            Check(backups.Any(path => Path.GetFileName(path).StartsWith("widgets-before-migration-", StringComparison.Ordinal)),
                "旧修改时间的状态文件在迁移备份后不应被立即当作旧备份误删");
            Check(autoCount == LocalStateStore.MaximumAutomaticBackups,
                $"自动备份数量应严格裁剪为上限 {LocalStateStore.MaximumAutomaticBackups}（实际 {autoCount}）");
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
