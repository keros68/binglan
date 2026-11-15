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

    internal static void TestResolveShownBounds()
    {
        var workArea = new PixelRect(0, 0, 1138, 711);
        var currentLayout = DisplayLayoutRules.Key([(workArea, 144u)]);
        var dualLayout = DisplayLayoutRules.Key([(Laptop, 144u), (External, 96u)]);

        var placementWithOtherLayout = new WindowPlacement();
        DisplayLayoutRules.Remember(placementWithOtherLayout, dualLayout, new PixelRect(2400, 300, 2800, 700));

        // 越界到右侧、底边外、仅露出角位，以及来自断开副屏的位置都应完整收拢进工作区。
        var rightEdge = PlacementResolver.ResolveShownBounds(
            new PixelRect(818, 27, 1378, 227),
            placementWithOtherLayout,
            currentLayout,
            [workArea],
            workArea);
        Check(rightEdge == new PixelRect(578, 27, 1138, 227),
            $"右侧越界位置应完整收拢到工作区内，实际 {rightEdge}");

        var bottomEdge = PlacementResolver.ResolveShownBounds(
            new PixelRect(707, 898, 1347, 982),
            placementWithOtherLayout,
            currentLayout,
            [workArea],
            workArea);
        Check(bottomEdge == new PixelRect(498, 627, 1138, 711),
            $"底边越界位置应完整收拢到工作区内，实际 {bottomEdge}");

        var sliver = PlacementResolver.ResolveShownBounds(
            new PixelRect(1058, 651, 1618, 851),
            placementWithOtherLayout,
            currentLayout,
            [workArea],
            workArea);
        Check(sliver == new PixelRect(578, 511, 1138, 711),
            $"只露出小角时也应完整收拢到工作区内，实际 {sliver}");

        var disconnected = PlacementResolver.ResolveShownBounds(
            new PixelRect(2400, 300, 2800, 500),
            placementWithOtherLayout,
            currentLayout,
            [workArea],
            workArea);
        Check(FitsInside(disconnected, workArea)
            && DisplayLayoutRules.TryRecall(placementWithOtherLayout, dualLayout, out var recalledDual)
            && recalledDual == new PixelRect(2400, 300, 2800, 700),
            "断开的副屏位置应收拢到当前工作区且不破坏其他排列记录");

        // 本来就在工作区内的位置原样返回。
        var inArea = new PixelRect(120, 80, 480, 280);
        var preserved = PlacementResolver.ResolveShownBounds(
            inArea,
            placementWithOtherLayout,
            currentLayout,
            [workArea],
            workArea);
        Check(preserved == inArea, $"本就在工作区内的位置应原样返回，实际 {preserved}");

        // 当前排列已有记忆位置时，可见则按记忆取回，记忆本身越界时拉回工作区。
        var rememberedVisible = new WindowPlacement();
        DisplayLayoutRules.Remember(rememberedVisible, currentLayout, new PixelRect(140, 100, 500, 300));
        var recalled = PlacementResolver.ResolveShownBounds(
            new PixelRect(2400, 300, 2800, 500),
            rememberedVisible,
            currentLayout,
            [workArea],
            workArea);
        Check(recalled == new PixelRect(140, 100, 500, 300),
            $"当前排列下的可见记忆位置应被恢复，实际 {recalled}");

        var rememberedOffscreen = new WindowPlacement();
        DisplayLayoutRules.Remember(rememberedOffscreen, currentLayout, new PixelRect(2400, 300, 2800, 500));
        var clampedRecalled = PlacementResolver.ResolveShownBounds(
            new PixelRect(2400, 300, 2800, 500),
            rememberedOffscreen,
            currentLayout,
            [workArea],
            workArea);
        Check(FitsInside(clampedRecalled, workArea),
            $"当前排列下越界的记忆位置也应收拢到工作区内，实际 {clampedRecalled}");
    }

    private static bool FitsInside(PixelRect bounds, PixelRect area) =>
        bounds.Left >= area.Left
        && bounds.Right <= area.Right
        && bounds.Top >= area.Top
        && bounds.Bottom <= area.Bottom;

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
