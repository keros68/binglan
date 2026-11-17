using BingLan.Core.Desktop;

/// <summary>
/// Rules that decide when the cards must rise into the topmost band because the
/// shell is showing the desktop. The Win32 calls themselves are checked by hand.
/// </summary>
internal static class DesktopSurfaceTests
{
    internal static void TestShownStateAndActions()
    {
        Check(DesktopSurfaceRules.IsDesktopShown(true, 0), "桌面表面已抬高且无普通窗口时应判定为显示桌面");
        Check(!DesktopSurfaceRules.IsDesktopShown(true, 1), "仍有普通窗口在上时不应判定为显示桌面");
        Check(!DesktopSurfaceRules.IsDesktopShown(true, 7), "多个普通窗口在上时不应判定为显示桌面");
        Check(!DesktopSurfaceRules.IsDesktopShown(false, 0), "找不到桌面表面时不应判定为显示桌面");

        Check(DesktopSurfaceRules.ActionFor(false, true) == DesktopSurfaceAction.Raise,
            "未提升且桌面已显示时应提升");
        Check(DesktopSurfaceRules.ActionFor(true, true) == DesktopSurfaceAction.None,
            "已提升且桌面仍显示时应保持");
        Check(DesktopSurfaceRules.ActionFor(true, true, anySurfaceCovered: true) == DesktopSurfaceAction.Raise,
            "已提升但 WorkerW 再次盖过任一桌面或顶栏窗口时应重新提升");
        Check(DesktopSurfaceRules.ActionFor(true, true, anySurfaceCovered: false) == DesktopSurfaceAction.None,
            "所有受控窗口均在桌面表面之上时不应重复提升");
        Check(DesktopSurfaceRules.ActionFor(true, false) == DesktopSurfaceAction.Lower,
            "已提升且桌面不再显示时应回落");
        Check(DesktopSurfaceRules.ActionFor(true, false, anySurfaceCovered: true) == DesktopSurfaceAction.Lower,
            "普通窗口出现后即使曾被遮挡也应回落到普通窗口下方");
        Check(DesktopSurfaceRules.ActionFor(false, false) == DesktopSurfaceAction.None,
            "未提升且桌面未显示时应不动");
    }

    internal static void TestSurfaceHostAndBlockerFiltering()
    {
        Check(DesktopSurfaceRules.IsDesktopSurfaceHost("Progman", hasShellDefView: true),
            "承载 SHELLDLL_DefView 的 Progman 应识别为桌面表面");
        Check(DesktopSurfaceRules.IsDesktopSurfaceHost("WorkerW", hasShellDefView: true),
            "显示桌面或壁纸切换将 SHELLDLL_DefView 移入 WorkerW 时应识别为桌面表面");
        Check(!DesktopSurfaceRules.IsDesktopSurfaceHost("WorkerW", hasShellDefView: false),
            "不含 SHELLDLL_DefView 的普通 WorkerW 不应直接算作视图宿主");

        Check(DesktopSurfaceRules.IsTopmostShellDesktopSurface(
                shellProcess: true, "WorkerW", isVisible: true, isCloaked: false, isTopmost: true, width: 1920, height: 1080),
            "外壳抬到置顶带的满屏 WorkerW 应识别为显示桌面表面");
        Check(!DesktopSurfaceRules.IsTopmostShellDesktopSurface(
                shellProcess: true, "WorkerW", isVisible: true, isCloaked: false, isTopmost: false, width: 1920, height: 1080),
            "常态下非置顶的壁纸 WorkerW 不应算置顶桌面表面");
        Check(!DesktopSurfaceRules.IsTopmostShellDesktopSurface(
                shellProcess: true, "WorkerW", isVisible: true, isCloaked: false, isTopmost: true, width: 1, height: 1),
            "1x1 占位窗口不应误判为置顶桌面表面");
        Check(!DesktopSurfaceRules.IsTopmostShellDesktopSurface(
                shellProcess: false, "WorkerW", isVisible: true, isCloaked: false, isTopmost: true, width: 1920, height: 1080),
            "非外壳进程的置顶窗口不应误判为桌面表面");

        // 普通应用窗口与文件资源管理器窗口应阻断“显示桌面”状态。
        Check(DesktopSurfaceRules.CountsAsOrdinaryBlocker(
                shellProcess: false, "Chrome_WidgetWin_1",
                isAppWindow: false, isToolWindow: false, isNoActivate: false,
                isCloaked: false, isMinimized: false, isLayeredClickThrough: false,
                isDisabled: false, hasTitle: true, width: 1200, height: 800),
            "普通应用窗口未最小化时应算普通窗口");
        Check(DesktopSurfaceRules.CountsAsOrdinaryBlocker(
                shellProcess: true, "CabinetWClass",
                isAppWindow: false, isToolWindow: false, isNoActivate: false,
                isCloaked: false, isMinimized: false, isLayeredClickThrough: false,
                isDisabled: false, hasTitle: true, width: 1000, height: 700),
            "文件资源管理器窗口未最小化时应算普通窗口");
        Check(DesktopSurfaceRules.CountsAsOrdinaryBlocker(
                shellProcess: false, "Tauri Window",
                isAppWindow: true, isToolWindow: true, isNoActivate: false,
                isCloaked: false, isMinimized: false, isLayeredClickThrough: false,
                isDisabled: false, hasTitle: true, width: 900, height: 600),
            "显式声明 WS_EX_APPWINDOW 的窗口应算普通窗口");

        // Shell 的“显示桌面”不会最小化工具窗口、免激活悬浮窗、1px 辅助窗与鼠标穿透层，它们不能阻断卡片提升。
        Check(!DesktopSurfaceRules.CountsAsOrdinaryBlocker(
                shellProcess: false, "Tauri Window",
                isAppWindow: false, isToolWindow: true, isNoActivate: false,
                isCloaked: false, isMinimized: false, isLayeredClickThrough: false,
                isDisabled: false, hasTitle: true, width: 320, height: 160),
            "第三方桌面工具窗口不会被显示桌面最小化，不应阻断冰蓝卡片提升");
        Check(!DesktopSurfaceRules.CountsAsOrdinaryBlocker(
                shellProcess: false, "HwndWrapper[BingLan;;search]",
                isAppWindow: false, isToolWindow: true, isNoActivate: true,
                isCloaked: false, isMinimized: false, isLayeredClickThrough: false,
                isDisabled: false, hasTitle: true, width: 240, height: 48),
            "免激活工具窗（如独立搜索框）不应阻断卡片提升");
        Check(!DesktopSurfaceRules.CountsAsOrdinaryBlocker(
                shellProcess: false, "Qt51514QWindowIcon",
                isAppWindow: false, isToolWindow: false, isNoActivate: false,
                isCloaked: false, isMinimized: false, isLayeredClickThrough: false,
                isDisabled: false, hasTitle: false, width: 1, height: 1),
            "后台 1x1 占位辅助窗口不应阻断卡片提升");
        Check(!DesktopSurfaceRules.CountsAsOrdinaryBlocker(
                shellProcess: false, "OverlayWindow",
                isAppWindow: false, isToolWindow: false, isNoActivate: false,
                isCloaked: false, isMinimized: false, isLayeredClickThrough: true,
                isDisabled: false, hasTitle: true, width: 1920, height: 1080),
            "鼠标穿透的透明覆盖层不应阻断卡片提升");
        Check(!DesktopSurfaceRules.CountsAsOrdinaryBlocker(
                shellProcess: false, "GhostHelper",
                isAppWindow: false, isToolWindow: false, isNoActivate: false,
                isCloaked: false, isMinimized: false, isLayeredClickThrough: false,
                isDisabled: true, hasTitle: false, width: 400, height: 300),
            "无标题的禁用辅助窗不应阻断卡片提升");
    }

    internal static void TestOrdinaryWindowClassification()
    {
        Check(DesktopSurfaceRules.CountsAsOrdinaryWindow(false, "Chrome_WidgetWin_1"),
            "其他进程的窗口应算普通窗口");
        Check(DesktopSurfaceRules.CountsAsOrdinaryWindow(false, string.Empty),
            "取不到类名的其他进程窗口仍应算普通窗口");
        Check(DesktopSurfaceRules.CountsAsOrdinaryWindow(true, "CabinetWClass"),
            "资源管理器窗口属于外壳进程，仍应算普通窗口，打开后组件须落到它下面");
        Check(DesktopSurfaceRules.CountsAsOrdinaryWindow(true, "ExploreWClass"),
            "旧式资源管理器窗口也应算普通窗口");
        Check(!DesktopSurfaceRules.CountsAsOrdinaryWindow(true, "WorkerW"),
            "外壳壁纸窗口不应算普通窗口");
        Check(!DesktopSurfaceRules.CountsAsOrdinaryWindow(true, "Shell_TrayWnd"),
            "任务栏不应算普通窗口");
        Check(!DesktopSurfaceRules.CountsAsOrdinaryWindow(true, "Progman"),
            "桌面表面自身不应算普通窗口");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
