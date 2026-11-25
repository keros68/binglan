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
        TestExpandedTaskbarDetection();
    }

    private static void TestStripGeometryPerMode()
    {
        var monitor4k = new PixelRect(0, 0, 3840, 2160);
        Assert(
            TaskbarCornerRules.StripForMode(monitor4k, 144, TaskbarEdgeRevealMode.Disabled)
                == new PixelRect(0, 2158, 3840, 2160),
            "Disabled 应覆盖整条底边最下 2 像素");
        Assert(
            TaskbarCornerRules.StripForMode(monitor4k, 144, TaskbarEdgeRevealMode.Disabled, stripPixels: 4)
                == new PixelRect(0, 2156, 3840, 2160),
            "收起任务栏露出 4 像素时 Disabled 挡板应动态覆盖至 4 像素");
        Assert(
            TaskbarCornerRules.ResolveStripThickness(1) == TaskbarCornerRules.StripPixels
            && TaskbarCornerRules.ResolveStripThickness(4) == 4
            && TaskbarCornerRules.ResolveStripThickness(32) == TaskbarCornerRules.ExpandedTaskbarThresholdPixels,
            "挡板厚度应钳制在 2..8 像素范围内");
        Assert(
            TaskbarCornerRules.StripForMode(monitor4k, 144, TaskbarEdgeRevealMode.CornersOnly)
                == new PixelRect(576, 2158, 3264, 2160),
            "CornersOnly 应只覆盖中间段");
        Assert(
            TaskbarCornerRules.StripForMode(monitor4k, 144, TaskbarEdgeRevealMode.AnyEdge) is null,
            "AnyEdge 不应产生挡板");
        Assert(
            TaskbarCornerRules.NeedsGeometryRestore(new PixelRect(0, 2158, 1, 2159), new PixelRect(0, 2158, 3840, 2160))
            && !TaskbarCornerRules.NeedsGeometryRestore(new PixelRect(0, 2158, 3840, 2160), new PixelRect(0, 2158, 3840, 2160)),
            "DPI 变化导致挡板缩回 1x1 时应判定为需要恢复几何尺寸");
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

    private static void TestExpandedTaskbarDetection()
    {
        var strip = new PixelRect(0, 2158, 3840, 2160);
        Assert(!TaskbarCornerRules.IsBottomTaskbarExpanded(new PixelRect(0, 2158, 3840, 2242), strip),
            "收起态仅露出 2 像素的任务栏不应判为展开");
        Assert(TaskbarCornerRules.IsBottomTaskbarExpanded(new PixelRect(0, 2076, 3840, 2160), strip),
            "完整展开的底部任务栏应判为展开，挡板不得抢占其 Z 序");
        Assert(TaskbarCornerRules.IsBottomTaskbarExpanded(new PixelRect(0, 2120, 3840, 2204), strip),
            "正在滑出/收回中途的任务栏应判为展开，等彻底收起后再复位挡板");
        Assert(!TaskbarCornerRules.IsBottomTaskbarExpanded(new PixelRect(0, 2076, 3840, 2160), new PixelRect(3840, 1078, 5760, 1080)),
            "其他显示器上展开的任务栏不应影响当前显示器的挡板");
        Assert(!TaskbarCornerRules.IsSameMonitorBottomTaskbar(new PixelRect(3840, 1078, 5760, 1120), strip),
            "副屏收起的任务栏不得匹配主屏挡板，避免双屏 Z 序绕过主屏展开保护");
        Assert(TaskbarCornerRules.IsSameMonitorBottomTaskbar(new PixelRect(0, 2158, 3840, 2242), strip),
            "同屏底部任务栏应匹配该屏挡板");

        // 副屏底边自动隐藏任务栏识别与上下双屏边界隔离
        var upperMonitor = new PixelRect(0, 0, 1920, 1080);
        var lowerMonitor = new PixelRect(0, 1080, 1920, 2160);
        var upperBottomTaskbar = new PixelRect(0, 1078, 1920, 1126);
        var lowerBottomTaskbar = new PixelRect(0, 2158, 1920, 2206);
        Assert(TaskbarCornerRules.IsBottomTaskbarOnMonitor(upperBottomTaskbar, upperMonitor),
            "上屏底边收起的任务栏应匹配上屏");
        Assert(!TaskbarCornerRules.IsBottomTaskbarOnMonitor(upperBottomTaskbar, lowerMonitor),
            "上屏底边收起任务栏向下探出部分不得误判为下屏任务栏");
        Assert(TaskbarCornerRules.IsBottomTaskbarOnMonitor(lowerBottomTaskbar, lowerMonitor),
            "下屏副屏底边任务栏应匹配下屏");

        // 左侧或右侧停靠的自动隐藏任务栏不得误判为底边任务栏
        var leftCollapsedTaskbar = new PixelRect(-46, 0, 2, 1080);
        var rightCollapsedTaskbar = new PixelRect(1918, 0, 1966, 1080);
        Assert(!TaskbarCornerRules.IsBottomTaskbarOnMonitor(leftCollapsedTaskbar, upperMonitor),
            "左侧停靠任务栏不得误判为底边任务栏");
        Assert(!TaskbarCornerRules.IsBottomTaskbarOnMonitor(rightCollapsedTaskbar, upperMonitor),
            "右侧停靠任务栏不得误判为底边任务栏");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
