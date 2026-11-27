using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.Themes;

namespace BingLan.SmokeTests;

internal static class FileBoxExpansionTests
{
    public static void Run()
    {
        foreach (var scale in new[] { 1d, 1.25d, 1.5d })
        {
            var work = new PixelRect(-1920, -1200, 0, 0);
            var folded = new PixelRect(-1500, -180, -1100, -180 + (int)Math.Round(48d * scale));
            var expanded = FileBoxExpansionRules.ResolveUpwardExpandedBounds(folded, work, 306d, scale);
            Check(expanded.Bottom == folded.Bottom && expanded.Height == (int)Math.Round(306d * scale),
                "向上展开应保持标题底边，按当前 DPI 恢复高度并支持负坐标显示器");
            Check(FileBoxExpansionRules.ResolveUpwardCollapsedBounds(expanded, scale) == folded,
                "折叠后应回到原标题位置与高度");

            var tall = FileBoxExpansionRules.ResolveUpwardExpandedBounds(folded, work, 2000d, scale);
            Check(tall.Top == work.Top && tall.Bottom == folded.Bottom,
                "展开高度应受工作区上边界限制且保留标题位置");
            var nearTop = new PixelRect(-1500, -1190, -1100, -1190 + (int)Math.Round(48d * scale));
            var shifted = FileBoxExpansionRules.ResolveUpwardExpandedBounds(nearTop, work, 306d, scale);
            Check(shifted.Top == work.Top && shifted.Height >= (int)Math.Ceiling(166d * scale),
                "标题靠近工作区上边缘时应让盒子留在屏内并保持最小可用高度");
        }

        var groupId = Guid.NewGuid();
        var first = new FileBoxState { GroupId = groupId, IsActiveTab = true, ExpandUpward = true };
        var second = new FileBoxState { GroupId = groupId };
        FileBoxGroupRules.NormalizeGroups([first, second]);
        Check(second.ExpandUpward, "组合收纳盒应共享展开方向");
        first.ExpandUpward = false;
        FileBoxGroupRules.SyncGroupSharedProperties(first, [first, second]);
        Check(!second.ExpandUpward, "恢复向下展开应同步所有标签");

        var directory = Path.Combine(Path.GetTempPath(), $"BingLan-expansion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new LocalStateStore(directory);
            File.WriteAllText(store.StatePath, """{ "SchemaVersion": 67, "FileBoxes": [{ "Title": "旧盒" }] }""");
            var state = store.Load();
            Check(!state.FileBoxes[0].ExpandUpward, "旧配置应保持向下展开");
            state.FileBoxes[0].ExpandUpward = true;
            state.FileBoxes[0].IsCollapsed = true;
            state.FileBoxes[0].ExpandedHeight = 500d;
            state.FileBoxes[0].Placement.Top = 700d;
            state.FileBoxes[0].Placement.Height = 48d;
            store.Save(state);
            var restored = store.Load().FileBoxes[0];
            Check(restored.ExpandUpward && restored.IsCollapsed && restored.ExpandedHeight == 500d
                && restored.Placement.Top == 700d && restored.Placement.Height == 48d,
                "展开方向、折叠标题位置与用户展开高度应在重载后保留");

            var theme = ThemeRules.Export(state, "方向隔离", new ThemeArea(0, 0, 1920, 1080), new Version(1, 0));
            Check(!System.Text.Json.JsonSerializer.Serialize(theme).Contains("ExpandUpward", StringComparison.OrdinalIgnoreCase),
                "本机展开方向应排除在主题包之外");
        }
        finally
        {
            Directory.Delete(directory, true);
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
