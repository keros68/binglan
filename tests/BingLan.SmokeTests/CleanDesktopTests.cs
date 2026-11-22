using System.IO;
using System.IO.Compression;
using System.Text.Json;
using BingLan.Core.Desktop;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.Themes;

/// <summary>Rules and records behind clean desktop; the Shell calls themselves are checked by hand.</summary>
internal static class CleanDesktopTests
{
    private const uint RealDesktopFlags = 0x40200224;

    internal static void TestOnlyTheIconBitChanges()
    {
        var hidden = CleanDesktopRules.Apply(RealDesktopFlags, true);
        Check(hidden == (RealDesktopFlags | CleanDesktopRules.NoIconsFlag), "隐藏只应设置图标位");
        Check(CleanDesktopRules.Apply(hidden, false) == RealDesktopFlags, "恢复应回到原有标志");
        Check(CleanDesktopRules.Apply(hidden, true) == hidden, "重复隐藏应无变化");
        Check(CleanDesktopRules.AreIconsHidden(hidden) && !CleanDesktopRules.AreIconsHidden(RealDesktopFlags),
            "图标状态识别错误");
    }

    internal static void TestHideAndRestorePlans()
    {
        Check(CleanDesktopRules.NeedsHide(RealDesktopFlags), "图标可见时应需要隐藏");
        Check(!CleanDesktopRules.NeedsHide(RealDesktopFlags | CleanDesktopRules.NoIconsFlag),
            "用户已隐藏的图标不应再写入");

        var checkpoint = new CleanDesktopCheckpoint { OriginalIconsHidden = false };
        Check(CleanDesktopRules.NeedsRestore(checkpoint, RealDesktopFlags | CleanDesktopRules.NoIconsFlag),
            "隐藏中应需要恢复");
        Check(!CleanDesktopRules.NeedsRestore(checkpoint, RealDesktopFlags), "已恢复时不应再写入");

        var alreadyHidden = new CleanDesktopCheckpoint { OriginalIconsHidden = true };
        Check(!CleanDesktopRules.NeedsRestore(alreadyHidden, RealDesktopFlags | CleanDesktopRules.NoIconsFlag),
            "原本隐藏的图标应保持隐藏");
    }

    internal static void TestTakeoverDetection()
    {
        Check(CleanDesktopRules.IsTakenOver(true, RealDesktopFlags), "隐藏期间图标重新显示应视为用户接管");
        Check(!CleanDesktopRules.IsTakenOver(true, RealDesktopFlags | CleanDesktopRules.NoIconsFlag),
            "仍隐藏时不是接管");
        Check(!CleanDesktopRules.IsTakenOver(false, RealDesktopFlags), "未开启时不是接管");
    }

    internal static void TestTurnOffWithoutRecordPlan()
    {
        Check(
            CleanDesktopRules.TurnOffMustShowIcons(false, RealDesktopFlags | CleanDesktopRules.NoIconsFlag),
            "检查点丢失而图标仍隐藏时，关闭必须恢复显示而不是虚报成功");
        Check(
            !CleanDesktopRules.TurnOffMustShowIcons(true, RealDesktopFlags | CleanDesktopRules.NoIconsFlag),
            "有检查点时应按检查点恢复，不走兜底");
        Check(!CleanDesktopRules.TurnOffMustShowIcons(false, RealDesktopFlags),
            "检查点丢失但图标可见时无需写入");
    }

    internal static void TestAdoptionOfOrphanedHiddenIcons()
    {
        Check(
            CleanDesktopRules.NeedsAdoption(false, RealDesktopFlags | CleanDesktopRules.NoIconsFlag),
            "开启时遇到无记录的隐藏图标应接手：先恢复显示再隐藏");
        Check(!CleanDesktopRules.NeedsAdoption(false, RealDesktopFlags),
            "图标可见时无需接手");
        Check(
            !CleanDesktopRules.NeedsAdoption(true, RealDesktopFlags | CleanDesktopRules.NoIconsFlag),
            "有检查点时按检查点恢复，不接手");

        var resumeCheckpoint = new CleanDesktopCheckpoint { OriginalIconsHidden = false, Phase = CleanDesktopPhase.Applied };
        Check(
            CleanDesktopRules.PlanStartupResume(resumeCheckpoint, RealDesktopFlags | CleanDesktopRules.NoIconsFlag)
                == CleanDesktopStartupPlan.KeepHidden,
            "开机自启且桌面图标已隐藏时应直接无闪烁接续，不先显示再隐藏");
        Check(
            CleanDesktopRules.PlanStartupResume(resumeCheckpoint, RealDesktopFlags)
                == CleanDesktopStartupPlan.HidePreservingCheckpoint
                && !resumeCheckpoint.OriginalIconsHidden,
            "开机恢复隐藏时应保留检查点中记录的原始图标状态");
    }

    internal static void TestCheckpointRoundTripAndRejection()
    {
        var checkpoint = new CleanDesktopCheckpoint
        {
            Phase = CleanDesktopPhase.Applied,
            OriginalIconsHidden = false
        };
        var parsed = CleanDesktopCheckpoint.Parse(checkpoint.Serialize());
        Check(parsed is { Phase: CleanDesktopPhase.Applied, OriginalIconsHidden: false, SchemaVersion: 1 },
            "检查点往返读取错误");
        Check(checkpoint.Serialize().Contains("\"Applied\""), "阶段应以名称保存，便于人工核对");
        Check(CleanDesktopCheckpoint.Parse("{ not json") is null, "损坏的检查点应被拒绝");
        Check(CleanDesktopCheckpoint.Parse("{}") is null, "缺少字段的检查点应被拒绝");
        Check(CleanDesktopCheckpoint.Parse("null") is null, "空检查点应被拒绝");
        Check(CleanDesktopCheckpoint.Parse("""{ "SchemaVersion": 1, "Phase": "Applied", "CreatedAtUtc": "2026-09-28T00:00:00Z" }""") is null,
            "缺少原始状态的检查点应被拒绝");
        Check(CleanDesktopCheckpoint.Parse("""{ "SchemaVersion": 99 }""") is null, "更新版本的检查点应被拒绝");
    }

    internal static void TestSettingMigration()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-clean-desktop-{Guid.NewGuid():N}");
        try
        {
            var store = new LocalStateStore(temp);
            Check(!store.Load().CleanDesktopEnabled, "新安装默认不隐藏桌面图标");

            File.WriteAllText(store.StatePath, """{ "SchemaVersion": 15 }""");
            var upgraded = store.Load();
            Check(!upgraded.CleanDesktopEnabled && upgraded.SchemaVersion == AppState.CurrentSchemaVersion,
                "升级后清爽桌面应保持关闭");

            upgraded.CleanDesktopEnabled = true;
            store.Save(upgraded);
            Check(store.Load().CleanDesktopEnabled, "清爽桌面设置应保存");
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, true);
            }
        }
    }

    internal static void TestStateFallsBackToBackup(bool primaryMissing)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-null-state-{Guid.NewGuid():N}");
        try
        {
            var store = new LocalStateStore(temp);
            var state = store.Load();
            state.OnboardingCompleted = true;
            store.Save(state);
            store.Save(state);

            if (primaryMissing)
            {
                File.Delete(store.StatePath);
            }
            else
            {
                File.WriteAllText(store.StatePath, "null");
            }
            var recovered = store.Load();
            Check(recovered.OnboardingCompleted, "主状态缺失或为空时应从备份恢复");

            store.Save(recovered);
            Check(store.Load().OnboardingCompleted, "恢复后的保存应可读");
            Check(File.ReadAllText(store.StatePath + ".bak") != "null", "损坏的主状态不应覆盖备份");
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, true);
            }
        }
    }

    internal static void TestDesktopDoubleClickDetection()
    {
        // First press only records pending stamp.
        var first = DesktopDoubleClickRules.EvaluateLeftButtonDown(
            null, x: 500, y: 300, timestampMs: 1000, maxIntervalMs: 500, doubleClickWidth: 4, doubleClickHeight: 4);
        Check(!first.IsDoubleClick && first.NextPendingClick == new DesktopClickStamp(500, 300, 1000),
            "首次左键按下不应触发双击且应记录坐标与时间");

        // Second press within exact time and rectangle boundaries triggers and clears pending stamp.
        var exactBoundary = DesktopDoubleClickRules.EvaluateLeftButtonDown(
            first.NextPendingClick, x: 502, y: 298, timestampMs: 1500, maxIntervalMs: 500, doubleClickWidth: 4, doubleClickHeight: 4);
        Check(exactBoundary.IsDoubleClick && exactBoundary.NextPendingClick is null,
            "刚好在双击时间上限与矩形边界内的第二次按下应判定为双击并清空计数");

        // A 3rd rapid click right after a double-click must NOT chain into another trigger.
        var third = DesktopDoubleClickRules.EvaluateLeftButtonDown(
            exactBoundary.NextPendingClick, x: 502, y: 298, timestampMs: 1550, maxIntervalMs: 500, doubleClickWidth: 4, doubleClickHeight: 4);
        Check(!third.IsDoubleClick && third.NextPendingClick == new DesktopClickStamp(502, 298, 1550),
            "第三次快速按下不得与前两次合并成连续触发，应重新开始计数");

        // A 4th rapid click pairs with the 3rd click and triggers a fresh double-click.
        var fourth = DesktopDoubleClickRules.EvaluateLeftButtonDown(
            third.NextPendingClick, x: 501, y: 299, timestampMs: 1700, maxIntervalMs: 500, doubleClickWidth: 4, doubleClickHeight: 4);
        Check(fourth.IsDoubleClick && fourth.NextPendingClick is null,
            "重新计数后的第四次按下应与第三次配对触发双击");

        // Exceeding double-click time does not trigger, and moves pending click forward.
        var tooSlow = DesktopDoubleClickRules.EvaluateLeftButtonDown(
            first.NextPendingClick, x: 500, y: 300, timestampMs: 1501, maxIntervalMs: 500, doubleClickWidth: 4, doubleClickHeight: 4);
        Check(!tooSlow.IsDoubleClick && tooSlow.NextPendingClick == new DesktopClickStamp(500, 300, 1501),
            "超过系统双击间隔时不应触发，并应以本次按下刷新起点");
        var followUpAfterSlow = DesktopDoubleClickRules.EvaluateLeftButtonDown(
            tooSlow.NextPendingClick, x: 500, y: 300, timestampMs: 1700, maxIntervalMs: 500, doubleClickWidth: 4, doubleClickHeight: 4);
        Check(followUpAfterSlow.IsDoubleClick,
            "超时后紧跟的第三次按下应可与第二次组成有效双击");

        // Exceeding X or Y rectangle does not trigger, and updates pending click.
        var tooFarX = DesktopDoubleClickRules.EvaluateLeftButtonDown(
            first.NextPendingClick, x: 503, y: 300, timestampMs: 1100, maxIntervalMs: 500, doubleClickWidth: 4, doubleClickHeight: 4);
        Check(!tooFarX.IsDoubleClick && tooFarX.NextPendingClick == new DesktopClickStamp(503, 300, 1100),
            "横向超出系统双击矩形时不应触发");
        var tooFarY = DesktopDoubleClickRules.EvaluateLeftButtonDown(
            first.NextPendingClick, x: 500, y: 303, timestampMs: 1100, maxIntervalMs: 500, doubleClickWidth: 4, doubleClickHeight: 4);
        Check(!tooFarY.IsDoubleClick && tooFarY.NextPendingClick == new DesktopClickStamp(500, 303, 1100),
            "纵向超出系统双击矩形时不应触发");

        // Negative multi-monitor coordinates work symmetrically.
        var negFirst = DesktopDoubleClickRules.EvaluateLeftButtonDown(
            null, x: -1200, y: -400, timestampMs: 2000, maxIntervalMs: 500, doubleClickWidth: 6, doubleClickHeight: 6);
        var negSecond = DesktopDoubleClickRules.EvaluateLeftButtonDown(
            negFirst.NextPendingClick, x: -1203, y: -397, timestampMs: 2250, maxIntervalMs: 500, doubleClickWidth: 6, doubleClickHeight: 6);
        Check(negSecond.IsDoubleClick, "负坐标副屏应正确计算双击距离");

        // Backwards clock or non-positive interval must never trigger.
        var rollback = DesktopDoubleClickRules.EvaluateLeftButtonDown(
            first.NextPendingClick, x: 500, y: 300, timestampMs: 999, maxIntervalMs: 500, doubleClickWidth: 4, doubleClickHeight: 4);
        Check(!rollback.IsDoubleClick, "时间戳倒退时不应触发双击");
        var invalidInterval = DesktopDoubleClickRules.EvaluateLeftButtonDown(
            first.NextPendingClick, x: 500, y: 300, timestampMs: 1100, maxIntervalMs: 0, doubleClickWidth: 4, doubleClickHeight: 4);
        Check(!invalidInterval.IsDoubleClick, "非正双击时间参数不应触发双击");
    }

    internal static void TestDesktopDoubleClickHitRules()
    {
        Check(DesktopDoubleClickRules.IsDesktopAncestorChain(["SysListView32", "SHELLDLL_DefView", "Progman"]),
            "Progman 下的桌面图标列表应判定为桌面");
        Check(DesktopDoubleClickRules.IsDesktopAncestorChain(["SysListView32", "SHELLDLL_DefView", "WorkerW"]),
            "WorkerW 下的桌面图标列表应判定为桌面");
        Check(DesktopDoubleClickRules.IsDesktopAncestorChain(["SHELLDLL_DefView", "Progman"]),
            "Progman 下的 SHELLDLL_DefView 应判定为桌面");
        Check(DesktopDoubleClickRules.IsDesktopAncestorChain(["SHELLDLL_DefView", "WorkerW"]),
            "WorkerW 下的 SHELLDLL_DefView 应判定为桌面");
        Check(DesktopDoubleClickRules.IsDesktopAncestorChain(["Progman"]),
            "隐藏图标后直接命中 Progman 应判定为桌面壁纸层");
        Check(DesktopDoubleClickRules.IsDesktopAncestorChain(["WorkerW"]),
            "隐藏图标后直接命中 WorkerW 应判定为桌面壁纸层");
        Check(DesktopDoubleClickRules.IsDesktopAncestorChain(["WorkerW", "Progman"]),
            "Windows 11 24H2+ 中 Progman 下的壁纸 WorkerW 应判定为桌面");

        Check(!DesktopDoubleClickRules.IsDesktopAncestorChain(["Edit", "SysListView32", "SHELLDLL_DefView", "Progman"]),
            "桌面重命名输入框 Edit 不应判定为桌面空白处");
        Check(!DesktopDoubleClickRules.IsDesktopAncestorChain(["SysListView32", "SHELLDLL_DefView", "CabinetWClass"]),
            "资源管理器文件夹窗口不得判定为桌面");
        Check(!DesktopDoubleClickRules.IsDesktopAncestorChain(["Shell_TrayWnd"]),
            "任务栏不得判定为桌面");
        Check(!DesktopDoubleClickRules.IsDesktopAncestorChain(["Chrome_WidgetWin_1"]),
            "普通应用窗口不得判定为桌面");
        Check(!DesktopDoubleClickRules.IsDesktopAncestorChain([]),
            "空窗口链不得判定为桌面");

        Check(DesktopDoubleClickRules.IsDesktopBlankAccessibleHit(0, DesktopDoubleClickRules.RoleSystemList),
            "childId=0 且角色为 LIST 应判定为桌面空白区");
        Check(DesktopDoubleClickRules.IsDesktopBlankAccessibleHit(0, DesktopDoubleClickRules.RoleSystemClient),
            "childId=0 且角色为 CLIENT 应判定为桌面空白区");
        Check(DesktopDoubleClickRules.IsDesktopBlankAccessibleHit(0, DesktopDoubleClickRules.RoleSystemWindow),
            "childId=0 且角色为 WINDOW 应判定为桌面空白区");
        Check(DesktopDoubleClickRules.IsDesktopBlankAccessibleHit(0, DesktopDoubleClickRules.RoleSystemPane),
            "childId=0 且角色为 PANE 应判定为桌面空白区");

        Check(!DesktopDoubleClickRules.IsDesktopBlankAccessibleHit(1, DesktopDoubleClickRules.RoleSystemListItem),
            "命中桌面图标（childId>0 / LISTITEM）必须忽略");
        Check(!DesktopDoubleClickRules.IsDesktopBlankAccessibleHit(0, DesktopDoubleClickRules.RoleSystemListItem),
            "即使 childId=0，角色为 LISTITEM 也必须忽略");
        Check(!DesktopDoubleClickRules.IsDesktopBlankAccessibleHit(null, DesktopDoubleClickRules.RoleSystemList),
            "childId 缺失时应按不确定忽略");
        Check(!DesktopDoubleClickRules.IsDesktopBlankAccessibleHit(0, null),
            "role 缺失或获取失败时应按不确定忽略");
        Check(!DesktopDoubleClickRules.IsDesktopBlankAccessibleHit(0, 0x2B),
            "未知按钮角色不得判定为空白区");
    }

    internal static void TestDoubleClickSettingDefaultsMigrationAndThemeIsolation()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-dblclick-desktop-{Guid.NewGuid():N}");
        try
        {
            var store = new LocalStateStore(temp);
            var fresh = store.Load();
            Check(fresh.DoubleClickDesktopToToggleIcons is null or false,
                "新安装默认应关闭双击桌面空白处切换桌面图标");

            // Old schema without the nullable property keeps SchemaVersion unchanged and defaults to off.
            File.WriteAllText(store.StatePath, """{ "SchemaVersion": 57, "CleanDesktopEnabled": true }""");
            var loadedCurrent = store.Load();
            Check(loadedCurrent.DoubleClickDesktopToToggleIcons is null
                    && loadedCurrent.SchemaVersion == AppState.CurrentSchemaVersion,
                "缺少可空字段的旧配置应缺省为关闭且 Schema 版本保持不变");

            // Much older schema migrates forward and keeps the feature off.
            File.WriteAllText(store.StatePath, """{ "SchemaVersion": 15 }""");
            var upgraded = store.Load();
            Check(upgraded.DoubleClickDesktopToToggleIcons is null
                    && upgraded.SchemaVersion == AppState.CurrentSchemaVersion,
                "旧版本升级后双击桌面空白处开关应默认关闭");

            // Round-trip true and false.
            upgraded.DoubleClickDesktopToToggleIcons = true;
            store.Save(upgraded);
            Check(store.Load().DoubleClickDesktopToToggleIcons == true,
                "开启双击桌面空白处开关应持久化保存");

            // Theme export must never include this machine-local setting.
            var package = ThemeRules.Export(
                upgraded,
                "测试主题",
                new ThemeArea(0, 0, 1920, 1040),
                new Version(0, 3, 0));
            using var stream = new MemoryStream();
            ThemeArchive.Write(stream, package);
            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var content = reader.ReadToEnd();
                Check(!content.Contains("DoubleClickDesktopToToggleIcons", StringComparison.OrdinalIgnoreCase),
                    $"主题包文件 {entry.FullName} 不得包含本机双击桌面空白处设置");
            }

            upgraded.DoubleClickDesktopToToggleIcons = false;
            store.Save(upgraded);
            Check(store.Load().DoubleClickDesktopToToggleIcons == false,
                "关闭双击桌面空白处开关应持久化保存");
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
