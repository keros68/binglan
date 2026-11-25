using BingLan.Core.Dock;
using BingLan.Core.Services;
using BingLan.Core.Taskbar;

/// <summary>
/// Regression tests for PR-2 core fixes: sleep/wake stall detector false positives and
/// vertical multi-monitor taskbar bounds checks.
/// </summary>
internal static class BugRegressionTests
{
    internal static void Run()
    {
        TestStallDetectorIgnoresSleepJump();
        TestVerticalMultiMonitorBottomTaskbarUpperBound();
    }

    private static void TestStallDetectorIgnoresSleepJump()
    {
        var detector = new UiStallDetector(2000);
        detector.Sent(1_000);

        // Normal watchdog interval (500ms) is not a clock jump.
        Check(!detector.HandleClockJump(1_000, 1_500, 3_000), "正常看门狗间隔不应判定为休眠跳变");
        Check(detector.Check(1_500) is null, "未超过卡顿阈值时不应触发卡顿");

        // Simulate system sleep / Modern Standby for ~3968 seconds before wake.
        const long afterWake = 1_500 + 3_968_000;
        Check(detector.HandleClockJump(1_500, afterWake, 3_000), "超过跳变阈值的唤醒间隔必须识别为系统挂起恢复");
        Check(detector.CanSend, "休眠唤醒重置后应清除挂起探针");
        Check(detector.Check(afterWake) is null, "休眠唤醒后不得把睡眠时长误报为 UI 卡顿");
        Check(detector.Answered(afterWake + 100) is null, "休眠唤醒重置后迟到应答不得记录假恢复时长");

        // Real UI stall (watchdog keeps ticking every 500ms while UI stays blocked) still fires.
        detector.Sent(afterWake + 500);
        Check(!detector.HandleClockJump(afterWake + 500, afterWake + 1_000, 3_000), "真实卡顿期间看门狗正常轮询不触发跳变重置");
        Check(!detector.HandleClockJump(afterWake + 1_000, afterWake + 1_500, 3_000), "看门狗持续轮询不应重置");
        Check(!detector.HandleClockJump(afterWake + 1_500, afterWake + 2_000, 3_000), "看门狗持续轮询不应重置");
        Check(!detector.HandleClockJump(afterWake + 2_000, afterWake + 2_500, 3_000), "看门狗持续轮询不应重置");
        var started = detector.Check(afterWake + 2_500);
        Check(started is { Started: true, ElapsedMilliseconds: 2_000 }, "真实 2 秒 UI 卡顿仍应准确上报");
    }

    private static void TestVerticalMultiMonitorBottomTaskbarUpperBound()
    {
        // Upper monitor (0, 0, 1920, 1080) with bottom 2px strip; lower monitor stacked below (0, 1080, 1920, 2160).
        var upperStrip = new PixelRect(0, 1078, 1920, 1080);

        // Lower monitor's bottom taskbar (both expanded and collapsed) must NOT match upper monitor strip.
        Check(!TaskbarCornerRules.IsSameMonitorBottomTaskbar(new PixelRect(0, 2112, 1920, 2160), upperStrip),
            "纵向双屏中下屏展开任务栏不得误匹配上屏底部挡板");
        Check(!TaskbarCornerRules.IsSameMonitorBottomTaskbar(new PixelRect(0, 2158, 1920, 2206), upperStrip),
            "纵向双屏中下屏收起任务栏不得误匹配上屏底部挡板");
        Check(!TaskbarCornerRules.IsSameMonitorBottomTaskbar(new PixelRect(0, 1080, 1920, 1128), upperStrip),
            "位于上屏底边下方的任务栏不得匹配上屏底部挡板");

        // Same monitor's bottom taskbar (expanded, sliding, collapsed) still matches.
        Check(TaskbarCornerRules.IsSameMonitorBottomTaskbar(new PixelRect(0, 1032, 1920, 1080), upperStrip),
            "同屏展开任务栏应匹配该屏底部挡板");
        Check(TaskbarCornerRules.IsSameMonitorBottomTaskbar(new PixelRect(0, 1078, 1920, 1126), upperStrip),
            "同屏收起任务栏应匹配该屏底部挡板");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
