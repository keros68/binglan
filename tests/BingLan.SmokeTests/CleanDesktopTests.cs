using System.IO;
using BingLan.Core.Desktop;
using BingLan.Core.Models;
using BingLan.Core.Services;

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

    internal static void TestEmptyStateFallsBackToBackup()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-null-state-{Guid.NewGuid():N}");
        try
        {
            var store = new LocalStateStore(temp);
            var state = store.Load();
            state.OnboardingCompleted = true;
            store.Save(state);
            store.Save(state);

            File.WriteAllText(store.StatePath, "null");
            var recovered = store.Load();
            Check(recovered.OnboardingCompleted, "主状态为空时应从备份恢复");

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

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
