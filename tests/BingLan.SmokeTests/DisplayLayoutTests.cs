using System.IO;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Services;

/// <summary>Cards keep one position per arrangement of monitors.</summary>
internal static class DisplayLayoutTests
{
    private static readonly PixelRect Laptop = new(0, 0, 1920, 1200);
    private static readonly PixelRect External = new(1920, 0, 4480, 1440);

    internal static void TestPositionPerArrangement()
    {
        var single = DisplayLayoutRules.Key([(Laptop, 144u)]);
        var dual = DisplayLayoutRules.Key([(External, 96u), (Laptop, 144u)]);
        Check(dual == DisplayLayoutRules.Key([(Laptop, 144u), (External, 96u)]), "显示器枚举顺序不应改变排列名");
        Check(single != dual, "单屏与双屏应是不同排列");
        Check(single != DisplayLayoutRules.Key([(Laptop, 96u)]), "缩放不同应是不同排列");

        var placement = new WindowPlacement();
        DisplayLayoutRules.Remember(placement, dual, new PixelRect(2400, 300, 2800, 700));
        DisplayLayoutRules.Remember(placement, single, new PixelRect(100, 100, 600, 700));
        Check(DisplayLayoutRules.TryRecall(placement, dual, out var onDual) && onDual == new PixelRect(2400, 300, 2800, 700),
            "回到双屏应取回双屏上的位置，而不是单屏时挪到的位置");
        Check(DisplayLayoutRules.TryRecall(placement, single, out var onSingle) && onSingle == new PixelRect(100, 100, 600, 700),
            "单屏位置应单独保留");
        Check(!DisplayLayoutRules.TryRecall(placement, "unknown", out _), "未见过的排列不应取回位置");

        DisplayLayoutRules.Remember(placement, dual, new PixelRect(2500, 300, 2900, 700));
        Check(placement.DisplayLayouts.Count(entry => entry.Layout == dual) == 1
            && DisplayLayoutRules.TryRecall(placement, dual, out onDual) && onDual.Left == 2500,
            "同一排列只保留最新位置");

        DisplayLayoutRules.Remember(placement, single, new PixelRect(5, 5, 5, 400));
        Check(DisplayLayoutRules.TryRecall(placement, single, out onSingle) && onSingle.Left == 100, "无面积的位置不应记录");

        for (var index = 0; index < DisplayLayoutRules.MaximumRememberedLayouts + 2; index++)
        {
            DisplayLayoutRules.Remember(placement, $"layout-{index}", new PixelRect(0, 0, 10, 10));
        }
        Check(placement.DisplayLayouts.Count == DisplayLayoutRules.MaximumRememberedLayouts
            && !DisplayLayoutRules.TryRecall(placement, dual, out _)
            && DisplayLayoutRules.TryRecall(placement, $"layout-{DisplayLayoutRules.MaximumRememberedLayouts + 1}", out _),
            "超出上限时丢弃最久未用的排列");

        var empty = new WindowPlacement { DisplayLayouts = null! };
        Check(!DisplayLayoutRules.TryRecall(empty, dual, out _), "空列表不应出错");
        DisplayLayoutRules.Remember(empty, dual, new PixelRect(0, 0, 10, 10));
        Check(empty.DisplayLayouts.Count == 1, "空列表应能记录");
    }

    internal static void TestPersistenceAndMigration()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-display-layout-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temp);
            var store = new LocalStateStore(temp);
            var note = new NoteWidgetState();
            DisplayLayoutRules.Remember(note.Placement, "dual", new PixelRect(2400, 300, 2800, 700));
            store.Save(new AppState { NoteWidgets = [note] });
            var loaded = store.Load().NoteWidgets.Single().Placement;
            Check(DisplayLayoutRules.TryRecall(loaded, "dual", out var bounds) && bounds == new PixelRect(2400, 300, 2800, 700),
                "按排列记住的位置应在重启后保留");

            File.WriteAllText(store.StatePath,
                "{\"SchemaVersion\":21,\"NoteWidgets\":[{\"Placement\":{\"Left\":300,\"Top\":200,\"Width\":320,\"Height\":240}}]}");
            File.Delete(store.StatePath + ".bak");
            var migrated = store.Load();
            var placement = migrated.NoteWidgets.Single().Placement;
            Check(migrated.SchemaVersion == AppState.CurrentSchemaVersion
                && placement.Left == 300 && placement.DisplayLayouts.Count == 0,
                "v21 迁移应保留原位置并从空的排列记录开始");

            File.WriteAllText(store.StatePath, "{\"SchemaVersion\":22}");
            File.Delete(store.StatePath + ".bak");
            var automation = store.Load();
            Check(automation.SchemaVersion == AppState.CurrentSchemaVersion && !automation.FileBoxAutomationEnabled,
                "v22 迁移应默认关闭分组盒自动刷新");
        }
        finally
        {
            Directory.Delete(temp, true);
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
