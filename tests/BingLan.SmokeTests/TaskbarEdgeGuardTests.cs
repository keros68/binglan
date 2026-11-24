using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Taskbar;

namespace BingLan.SmokeTests;

internal static class TaskbarEdgeGuardTests
{
    public static void Run()
    {
        TestStripGeometryPerMode();
        TestMiddleStripAcrossDpis();
        TestGameLikeFullScreenSuspensionRules();
        TestNeedsModeReapply();
    }

    private static void TestStripGeometryPerMode()
    {
        var monitor4k = new PixelRect(0, 0, 3840, 2160);
        Assert(
            TaskbarCornerRules.StripForMode(monitor4k, 144, TaskbarEdgeRevealMode.Disabled)
                == new PixelRect(0, 2158, 3840, 2160),
            "Disabled 应覆盖整条底边最下 2 像素");
        Assert(
            TaskbarCornerRules.StripForMode(monitor4k, 144, TaskbarEdgeRevealMode.CornersOnly)
                == new PixelRect(576, 2158, 3264, 2160),
            "CornersOnly 应只覆盖中间段");
        Assert(
            TaskbarCornerRules.StripForMode(monitor4k, 144, TaskbarEdgeRevealMode.AnyEdge) is null,
            "AnyEdge 不应产生挡板");
    }

    private static void TestMiddleStripAcrossDpis()
    {
        // 4K 150% (144 DPI): 15% of 3840 = 576 > 160 * 1.5 = 240
        Assert(
            TaskbarCornerRules.MiddleStrip(new PixelRect(0, 0, 3840, 2160), 144)
                == new PixelRect(576, 2158, 3264, 2160),
            "4K 150% MiddleStrip 应各留 576 px");

        // 1080p 100% (96 DPI): 15% of 1920 = 288 > 160 * 1.0 = 160
        Assert(
            TaskbarCornerRules.MiddleStrip(new PixelRect(0, 0, 1920, 1080), 96)
                == new PixelRect(288, 1078, 1632, 1080),
            "1080p 100% MiddleStrip 应各留 288 px");
    }

    private static void TestGameLikeFullScreenSuspensionRules()
    {
        Assert(TaskbarCornerRules.ShouldSuspendForFullScreen(
                coversMonitor: true,
                foregroundIsZoomed: false,
                hasCaptionAndThickFrame: false,
                isApplicationFrameWindow: false,
                isToolOrOverlayWindow: false,
                isTopmost: false),
            "无边框全屏游戏/视频应暂停挡板");

        Assert(!TaskbarCornerRules.ShouldSuspendForFullScreen(
                coversMonitor: true,
                foregroundIsZoomed: true,
                hasCaptionAndThickFrame: false,
                isApplicationFrameWindow: false,
                isToolOrOverlayWindow: false,
                isTopmost: false),
            "最大化窗口不应暂停挡板");

        Assert(!TaskbarCornerRules.ShouldSuspendForFullScreen(
                coversMonitor: true,
                foregroundIsZoomed: false,
                hasCaptionAndThickFrame: true,
                isApplicationFrameWindow: false,
                isToolOrOverlayWindow: false,
                isTopmost: false),
            "带标准标题栏与可调边框的非置顶铺满窗口不应暂停挡板");

        Assert(TaskbarCornerRules.ShouldSuspendForFullScreen(
                coversMonitor: true,
                foregroundIsZoomed: false,
                hasCaptionAndThickFrame: true,
                isApplicationFrameWindow: false,
                isToolOrOverlayWindow: false,
                isTopmost: true),
            "置顶铺满窗口应暂停挡板");

        Assert(TaskbarCornerRules.ShouldSuspendForFullScreen(
                coversMonitor: true,
                foregroundIsZoomed: false,
                hasCaptionAndThickFrame: true,
                isApplicationFrameWindow: true,
                isToolOrOverlayWindow: false,
                isTopmost: false),
            "全屏 UWP ApplicationFrameWindow 应暂停挡板");

        Assert(!TaskbarCornerRules.ShouldSuspendForFullScreen(
                coversMonitor: true,
                foregroundIsZoomed: false,
                hasCaptionAndThickFrame: false,
                isApplicationFrameWindow: false,
                isToolOrOverlayWindow: true,
                isTopmost: true),
            "工具窗或穿透浮层不应暂停挡板");

        Assert(!TaskbarCornerRules.ShouldSuspendForFullScreen(
                coversMonitor: false,
                foregroundIsZoomed: false,
                hasCaptionAndThickFrame: false,
                isApplicationFrameWindow: false,
                isToolOrOverlayWindow: false,
                isTopmost: false),
            "未铺满显示器的窗口不应暂停挡板");
    }

    private static void TestNeedsModeReapply()
    {
        Assert(!TaskbarCornerRules.NeedsModeReapply(TaskbarMode.SmartHide, TaskbarMode.SmartHide, string.Empty),
            "已在自动隐藏且无异常时仅修改触边唤出方式不应拆解并重设任务栏模式");
        Assert(TaskbarCornerRules.NeedsModeReapply(TaskbarMode.SystemDefault, TaskbarMode.SmartHide, string.Empty),
            "模式变化时必须重新应用任务栏模式");
        Assert(TaskbarCornerRules.NeedsModeReapply(TaskbarMode.SmartHide, TaskbarMode.SmartHide, "Windows 没有接受"),
            "当前模式带有异常提示时应允许重新应用");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
