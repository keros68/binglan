using System.IO;
using BingLan.Core.Models;
using BingLan.Core.Services;

/// <summary>The summary shown for each backup in the settings list.</summary>
internal static class BackupSummaryTests
{
    internal static void TestBackupIsDescribed()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-backup-summary-{Guid.NewGuid():N}");
        try
        {
            var store = new LocalStateStore(temp);
            var state = store.Load();
            state.DesktopExperience.ActivePreset = DesktopLayoutPreset.GlacierWorkbench;
            state.NoteWidgets.Add(new NoteWidgetState());
            state.Dock.PinnedApps.Add(new DockPinnedApp { DisplayName = "浏览器", ExecutablePath = @"C:\Apps\browser.exe" });
            var backup = store.CreateBackup(state);

            var summary = LocalStateStore.DescribeBackup(backup);
            var visibleInformation = new[]
                {
                    DesktopComponentKind.TimeDate, DesktopComponentKind.Greeting,
                    DesktopComponentKind.Weather, DesktopComponentKind.Performance
                }
                .Count(kind => state.DesktopExperience.GetComponent(kind).IsVisible);
            var expectedCards = visibleInformation + state.TodoWidgets.Count + state.NoteWidgets.Count + state.FileBoxes.Count;
            Check(summary is { PresetName: "冰川工作台", DockAppCount: 1 } && summary.CardCount == expectedCards,
                "备份摘要应显示布局、卡片数和 Dock 应用数");

            var broken = Path.Combine(temp, "broken.json");
            File.WriteAllText(broken, "{ not json");
            Check(LocalStateStore.DescribeBackup(broken) is null, "无法读取的备份应返回空摘要");
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, true);
            }
        }
    }

    internal static void TestNewCardTitlesAreUnique()
    {
        Check(WidgetTitleRules.NextTitle("今日待办", []) == "今日待办", "第一张卡片沿用默认标题");
        Check(WidgetTitleRules.NextTitle("今日待办", ["今日待办"]) == "今日待办 2", "重名时应编号");
        Check(WidgetTitleRules.NextTitle("桌面分组", ["桌面分组", "桌面分组 2", "工作"]) == "桌面分组 3",
            "应跳过已用的编号");
    }

    internal static void TestBackupCanBeDeleted()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-backup-delete-{Guid.NewGuid():N}");
        try
        {
            var store = new LocalStateStore(temp);
            var backup = store.CreateBackup(store.Load());
            Check(store.ListBackups().Count == 1, "创建后应列出一个备份");

            Check(store.DeleteBackup(backup), "应能删除备份目录内的备份");
            Check(store.ListBackups().Count == 0, "删除后列表应为空");
            Check(!store.DeleteBackup(backup), "删除不存在的备份应返回 false 而不是抛错");

            var outside = Path.Combine(temp, "outside.json");
            File.WriteAllText(outside, "{}");
            Check(!store.DeleteBackup(outside), "备份目录外的文件应拒绝删除");
            Check(File.Exists(outside), "备份目录外的文件不应被删除");
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, true);
            }
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
