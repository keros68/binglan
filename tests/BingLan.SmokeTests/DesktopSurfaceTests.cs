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
        Check(!DesktopSurfaceRules.IsDesktopShown(true, 0, desktopSurfaceRaised: false),
            "空桌面但桌面表面仍位于桌面层底部（开机、关闭全部窗口或 Win+M）时不应判定为显示桌面");
        Check(DesktopSurfaceRules.IsDesktopShown(true, 0, desktopSurfaceRaised: true),
            "桌面表面实际抬起且无普通窗口时应判定为显示桌面");
        Check(!DesktopSurfaceRules.IsDesktopShown(true, 1, desktopSurfaceRaised: true),
            "即使存在置顶 WorkerW，只要有非全屏普通窗口打开就必须退出显示桌面");
        Check(!DesktopSurfaceRules.IsDesktopShown(true, 1), "仍有普通窗口在上时不应判定为显示桌面");
        Check(!DesktopSurfaceRules.IsDesktopShown(true, 7), "多个普通窗口在上时不应判定为显示桌面");
        Check(!DesktopSurfaceRules.IsDesktopShown(false, 0), "找不到桌面表面时不应判定为显示桌面");

        Check(!DesktopSurfaceRules.IsDesktopSurfaceRaised(
                desktopSurfaceIsTopmost: false, cardsRaised: false, raisedForTopmostSurface: false,
                baseSurfaceClassName: "Progman", visibleSurfaceCount: 5, visibleSurfacesAboveDesktop: 5),
            "开机或空桌面且所有卡片均已在 Progman 之上时不应把卡片提升进置顶带");
        Check(!DesktopSurfaceRules.IsDesktopSurfaceRaised(
                desktopSurfaceIsTopmost: false, cardsRaised: false, raisedForTopmostSurface: false,
                baseSurfaceClassName: "WorkerW", visibleSurfaceCount: 4, visibleSurfacesAboveDesktop: 4),
            "壁纸轮播 WorkerW 位于卡片下方时不应误提升卡片");
        Check(DesktopSurfaceRules.IsDesktopSurfaceRaised(
                desktopSurfaceIsTopmost: true, cardsRaised: false, raisedForTopmostSurface: false,
                baseSurfaceClassName: "Progman", visibleSurfaceCount: 5, visibleSurfacesAboveDesktop: 5),
            "Windows 11 将桌面表面抬入置顶带时应识别为桌面表面抬起");
        Check(DesktopSurfaceRules.IsDesktopSurfaceRaised(
                desktopSurfaceIsTopmost: false, cardsRaised: false, raisedForTopmostSurface: false,
                baseSurfaceClassName: "WorkerW", visibleSurfaceCount: 5, visibleSurfacesAboveDesktop: 0),
            "Windows 10 非置顶 WorkerW 盖过桌面层卡片时应识别为桌面表面抬起");
        Check(!DesktopSurfaceRules.IsDesktopSurfaceRaised(
                desktopSurfaceIsTopmost: false, cardsRaised: true, raisedForTopmostSurface: true,
                baseSurfaceClassName: "Progman", visibleSurfaceCount: 5, visibleSurfacesAboveDesktop: 5),
            "Windows 11 置顶 WorkerW 撤下且基底为 Progman 时应结束提升状态");
        Check(!DesktopSurfaceRules.IsDesktopSurfaceRaised(
                desktopSurfaceIsTopmost: false, cardsRaised: true, raisedForTopmostSurface: false,
                baseSurfaceClassName: "Progman", visibleSurfaceCount: 5, visibleSurfacesAboveDesktop: 5),
            "预提升窗口期结束后若基底仍为非置顶 Progman 且无卡片被盖住，应回落到桌面层底部");
        Check(DesktopSurfaceRules.IsDesktopSurfaceRaised(
                desktopSurfaceIsTopmost: false, cardsRaised: true, raisedForTopmostSurface: false,
                baseSurfaceClassName: "WorkerW", visibleSurfaceCount: 5, visibleSurfacesAboveDesktop: 5),
            "Windows 10 显示桌面提升期间保持状态直到普通窗口出现");

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
        Check(DesktopSurfaceRules.ActionFor(true, false, anySurfaceCovered: false, holdPreRaise: true) == DesktopSurfaceAction.None,
            "主动显示桌面预提升窗口期内普通窗口尚在最小化动画中时不应提前回落闪烁");
        Check(DesktopSurfaceRules.ActionFor(true, true, anySurfaceCovered: true, holdPreRaise: true) == DesktopSurfaceAction.Raise,
            "预提升期间若置顶 WorkerW 盖过任一受控表面仍应立即补提");
        Check(DesktopSurfaceRules.ActionFor(true, false, anySurfaceCovered: false, holdPreRaise: false) == DesktopSurfaceAction.Lower,
            "预提升窗口期结束后若未进入显示桌面状态应回落到普通窗口下方");
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
        Check(!DesktopSurfaceRules.IsTopmostShellDesktopSurface(
                shellProcess: true, "WorkerW", isVisible: true, isCloaked: false, isTopmost: true, width: 1920, height: 1080,
                isClickThrough: true),
            "鼠标穿透的置顶 WorkerW 不应误判为显示桌面表面");
        Check(!DesktopSurfaceRules.IsTopmostShellDesktopSurface(
                shellProcess: true, "WorkerW", isVisible: true, isCloaked: false, isTopmost: true, width: 1920, height: 1080,
                isClickThrough: false, isTransparentLayered: true),
            "全透明分层 WorkerW 不应误判为显示桌面表面");

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

        // 前台可见非置顶的外部软件窗口（含工具样式启动器/登录窗）必须让卡片回落，而置顶系统面板与自身表面不误回落。
        Check(DesktopSurfaceRules.CountsAsForegroundBlocker(
                hasWindow: true, isOwnProcess: false, isMonitoredDesktopSurface: false,
                shellProcess: false, "LauncherToolWindow",
                isVisible: true, isMinimized: false, isCloaked: false,
                isTopmost: false, isLayeredClickThrough: false, width: 480, height: 320),
            "前台可见的非置顶外部软件窗口（即使使用工具窗口样式）也应让冰蓝卡片回落");
        Check(!DesktopSurfaceRules.CountsAsForegroundBlocker(
                hasWindow: true, isOwnProcess: false, isMonitoredDesktopSurface: false,
                shellProcess: false, "Windows.UI.Core.CoreWindow",
                isVisible: true, isMinimized: false, isCloaked: false,
                isTopmost: true, isLayeredClickThrough: false, width: 640, height: 720),
            "开始菜单或快速设置等置顶面板不应在显示桌面期间误把卡片压回置顶 WorkerW 下方");
        Check(!DesktopSurfaceRules.CountsAsForegroundBlocker(
                hasWindow: true, isOwnProcess: true, isMonitoredDesktopSurface: false,
                shellProcess: false, "HwndWrapper[BingLan;;popup]",
                isVisible: true, isMinimized: false, isCloaked: false,
                isTopmost: false, isLayeredClickThrough: false, width: 280, height: 220),
            "冰蓝自身卡片或浮层获得前台时不应在显示桌面期间误回落");
        Check(!DesktopSurfaceRules.CountsAsForegroundBlocker(
                hasWindow: true, isOwnProcess: false, isMonitoredDesktopSurface: false,
                shellProcess: true, "WorkerW",
                isVisible: true, isMinimized: false, isCloaked: false,
                isTopmost: false, isLayeredClickThrough: false, width: 1920, height: 1080),
            "显示桌面期间 WorkerW 获得前台不应阻断卡片提升");
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
        Check(DesktopSurfaceRules.CountsAsOrdinaryWindow(true, "#32770"),
            "运行与文件属性等外壳对话框打开时组件也须落到其下方");
        Check(DesktopSurfaceRules.CountsAsOrdinaryWindow(true, "OperationStatusWindow"),
            "文件复制移动进度窗打开时组件也须落到其下方");
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
