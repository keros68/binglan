using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.Themes;
using BingLan.Core.Taskbar;
using BingLan.Core.TopBar;

var failures = new List<string>();
Run("Dock 四边几何与负坐标", TestDockGeometry);
Run("原生任务栏四边占用与顶栏保留预留冲突", TestNativeTaskbarGeometry);
Run("Dock 非法厚度", TestInvalidThickness);
Run("断开的显示器位置恢复", TestPlacementRecovery);
Run("新排列首次出现的收拢布局", TestDisplayArrivalRules);
Run("卡片让出任务栏与 Dock 占用的屏幕边缘", TestKeepOutOfReservedEdges);
Run("组件拖动吸附与参考线", TestSnapRules);
Run("窗口身份优先级", TestIdentityPriority);
Run("同应用多窗口稳定分组", TestStableGrouping);
Run("空标题窗口显示名回退", TestUntitledWindowDisplayName);
Run("运行应用按首次出现顺序保持稳定", TestFirstSeenOrder);
Run("Dock 点击动作决策", TestWindowActionPolicy);
Run("Dock 刚最小化应用的再次点击冷却", TestDockToggleCooldown);
Run("未读数字角标的标题解析与上限", TestUnreadBadgeRules);
Run("托盘驻留应用的唤回快捷键", TestTrayAppRevealRules);
Run("Dock 内容尺寸边界", TestContentLengthBounds);
Run("Dock 图标大小决定按钮与高度并被钳制", TestDockIconSize);
Run("Dock 悬停余弦放大与设置规范化", TestDockHoverMagnification);
Run("Dock 左右停靠：几何、放大轴与内侧隆起、触发区、避让顶栏与任务栏、v58 迁移", TestDockSideDocking);
Run("Dock 设置页文案：底部保持原文，左右停靠时按边命名", TestDockEdgeWording);
Run("Dock 距底部高度默认等于原固定间距并被钳制", TestDockBottomGap);
Run("Dock 隐藏把手几何：默认角位、钳制与左右展开锚定", TestDockHandleGeometry);
Run("Dock 隐藏把手位置状态规范化", TestDockHandleState);
Run("应用闪烁提醒在激活或关闭窗口后清除", TestDockAttention);
Run("底部与顶部水平居中几何", TestHorizontalCentering);
Run("左右垂直几何", TestVerticalGeometry);
Run("固定/运行/活动/分组状态映射", TestDockItemStateMapping);
Run("AppBar 预留、释放与重建路径", TestAppBarReservation);
Run("AppBar 位置通知不与其他贴边工具栏互相抢占", TestAppBarPositionGuard);
Run("最大化让出预留空间的判定规则", TestDockVacateRules);
Run("固定应用身份键规则", TestDockPinIdentityKey);
Run("固定应用与运行分组匹配规则", TestDockPinMatches);
Run("可执行文件固定项优先匹配普通窗口分组", TestExecutablePinPrefersPlainGroup);
Run("从运行分组创建固定项", TestDockPinCreateFromGroup);
Run("从可执行文件创建固定项", TestDockPinCreateFromExecutable);
Run("固定操作去重、插入与钳制", TestDockPin);
Run("取消固定", TestDockUnpin);
Run("移动固定项顺序", TestDockMove);
Run("固定状态规范化", TestDockNormalize);
Run("Dock 自动隐藏初始为显示", TestDockAutoHideStartsShown);
Run("无重叠时保持显示", TestDockAutoHideNoOverlapStaysShown);
Run("托盘里的应用闪烁：固定图标显示提醒，激活后清除", TestDockAttentionForTrayApp);
Run("Dock 结束应用：保护系统与自身进程，匹配普通/子目录启动器与打包应用", TestDockQuitRules);
Run("固定的启动器图标匹配子目录里的真实进程，且不误吞同目录或系统程序", TestDockPinLauncherStub);
Run("提醒弹跳：次数随动画档位、系统开关和设置变化，同一应用有间隔", TestDockBounceRules);
Run("始终自动隐藏：无重叠也收起，指针到边缘才出现", TestDockAutoHideAlways);
Run("从把手唤出正中 Dock：跨越期间保留宽限，进入条带或全屏立即清零", TestDockHandleRevealGrace);
Run("Dock 玻璃着色与回退规则：自定义使用完整浓度、省电与关闭实时毛玻璃自动回退", TestDockGlassRules);
Run("重叠超过延迟后才隐藏且仅触发一次", TestDockAutoHideDelay);
Run("指针进入唤出区域应立即显示并重置计时器", TestDockAutoHidePointerRevealResetsTimer);
Run("指针悬停在 Dock 上应立即显示", TestDockAutoHidePointerOverDockShowsImmediately);
Run("唤出区从悬空 Dock 底边延伸到屏幕底", TestDockRevealRules);
Run("前台全屏应立即隐藏", TestDockAutoHideFullScreenHidesImmediately);
Run("全屏优先于进行中的交互", TestDockAutoHideFullScreenOverridesInteraction);
Run("资源管理器桌面、任务栏与账户提示窗口不算全屏应用", TestShellSurfaceWindows);
Run("v12 状态迁移到 v13 应补齐 Dock 默认值", TestDockStateV12Migration);
Run("v24 状态迁移后应补 Dock 新设置默认值", TestDockStateV24Migration);
Run("v25 状态迁移后把手位置保持默认角位", TestDockStateV25Migration);
Run("v31 状态迁移保留个人内容并开启开始菜单入口", TestStartMenuMigration);
Run("Dock 状态保存与加载往返保留顺序与取值", TestDockStateRoundTrip);
Run("重复固定项保存后应去重", TestDockStateDuplicatePinsDeduped);
Run("隐藏的应用不出现在 Dock 且可恢复", TestDockHiddenApps);
Run("搜索会话持有前台时 Dock 评估借用前原窗口", TestDockForegroundRules);
Run("文件夹与文件固定项：识别、去重、不匹配运行窗口", TestPathPins);
Run("文件夹与文件固定项保存往返且不进入主题导出", TestPathPinsPersistAndStayOutOfThemes);
Run("拖拽文件到应用判定是否直接打开及收集有效目标", TestDockFileDropCanOpenFilesAndDecide);
Run("拖拽文件命令行参数转义与悬停 500ms 唤起状态机", TestDockFileDropLaunchArgumentsAndHoverState);
Run("Dock 顶层窗口过滤规则：排除零尺寸、穿透阴影与无标题禁用辅助窗", TestTopLevelWindowFilterRules);
Run("Dock 激活目标选择：WPS 最小化主窗优先于未最小化的禁用辅助窗", TestSelectActivationTarget);
Run("Dock 权限隔离提示文案：区分目标提权、前台提权与常规拦截", TestActivationBlockedMessages);
Run("免激活右键菜单防闪退与外部点击收起状态机", TestMenuDismissState);

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("全部 Dock 核心测试通过。");
return 0;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"通过：{name}");
    }
    catch (Exception exception)
    {
        failures.Add($"失败：{name} - {exception}");
    }
}

static void TestDockGeometry()
{
    var monitor = new PixelRect(-1920, -100, 0, 980);

    AssertEqual(
        new PixelRect(-1920, -100, -1872, 980),
        DockGeometry.Calculate(monitor, DockEdge.Left, 48),
        "左侧 Dock 几何错误");
    AssertEqual(
        new PixelRect(-1920, -100, 0, -52),
        DockGeometry.Calculate(monitor, DockEdge.Top, 48),
        "顶部 Dock 几何错误");
    AssertEqual(
        new PixelRect(-48, -100, 0, 980),
        DockGeometry.Calculate(monitor, DockEdge.Right, 48),
        "右侧 Dock 几何错误");
    AssertEqual(
        new PixelRect(-1920, 932, 0, 980),
        DockGeometry.Calculate(monitor, DockEdge.Bottom, 48),
        "底部 Dock 几何错误");

    Assert(monitor.HasArea, "有效显示器矩形应有面积");
    Assert(monitor.Intersects(new PixelRect(-100, 0, 100, 100)), "负坐标矩形应正确相交");
    Assert(!monitor.Intersects(new PixelRect(0, 0, 100, 100)), "仅边缘接触不应视为相交");
}

static void TestNativeTaskbarGeometry()
{
    var monitor = new PixelRect(-1920, -100, 0, 980);
    foreach (var edge in Enum.GetValues<DockEdge>())
    {
        var taskbar = DockGeometry.Calculate(monitor, edge, 48);
        AssertEqual<DockEdge?>(edge, TaskbarLayoutRules.GetEdge(taskbar, monitor, monitor),
            "原生任务栏应识别所在边缘");
        var safe = TaskbarLayoutRules.AvoidTaskbar(monitor, monitor, taskbar, edge, 0);
        Assert(!safe.Intersects(taskbar), "安全区域应让出原生任务栏全部范围");
    }
    AssertEqual<DockEdge?>(null,
        TaskbarLayoutRules.GetEdge(new PixelRect(-1800, 200, -100, 248), monitor, monitor),
        "未知悬浮布局不应猜测任务栏边缘");
    var retainedWorkArea = monitor with { Top = -20 };
    var topTaskbarBelowExistingReservation = new PixelRect(-1920, -68, 0, -20);
    AssertEqual<DockEdge?>(DockEdge.Top,
        TaskbarLayoutRules.GetEdge(topTaskbarBelowExistingReservation, monitor, retainedWorkArea),
        "已有顶栏预留不能掩盖工作区边界上的原生顶部任务栏");
    Assert(!TopBarReserveRules.ShouldReserve(true, true, monitor, retainedWorkArea,
        nativeTopEdgeOccupied: true), "确认原生顶部任务栏后必须释放已有顶栏预留");
    Assert(TopBarReserveRules.ShouldReserve(true, true, monitor, retainedWorkArea),
        "仅有自己的预留时应保持原有行为");
    var hiddenTop = new PixelRect(-1920, -146, 0, -98);
    var inset = TaskbarLayoutRules.AvoidTaskbar(monitor, monitor, hiddenTop, DockEdge.Top, 8);
    AssertEqual(-92, inset.Top, "顶部自动隐藏时应保留原生唤出边缘");
    Assert(!TopBarRevealRules.IsInRevealZone(-1000, -100, inset, 2),
        "原生顶部唤出边缘不应同时唤出顶栏");
    Assert(TopBarRevealRules.IsInRevealZone(-1000, -92, inset, 2),
        "移到让出后的顶栏位置应可唤出");

    // 自动隐藏任务栏展开瞬间不应把 48px 整条当成常驻禁区：四边均应按 8px 唤出条避让。
    foreach (var edge in Enum.GetValues<DockEdge>())
    {
        var revealStrip = DockGeometry.Calculate(monitor, edge, 8);
        var autoHideSafe = TaskbarLayoutRules.AvoidTaskbar(monitor, monitor, revealStrip, edge, 8);
        var expected = edge switch
        {
            DockEdge.Left => monitor with { Left = monitor.Left + 8 },
            DockEdge.Top => monitor with { Top = monitor.Top + 8 },
            DockEdge.Right => monitor with { Right = monitor.Right - 8 },
            DockEdge.Bottom => monitor with { Bottom = monitor.Bottom - 8 },
            _ => monitor
        };
        AssertEqual(expected, autoHideSafe, $"自动隐藏任务栏在 {edge} 边应仅按 8px 唤出条避让");
    }
}

static void TestInvalidThickness()
{
    var monitor = new PixelRect(0, 0, 1920, 1080);
    AssertThrows<ArgumentOutOfRangeException>(
        () => DockGeometry.Calculate(monitor, DockEdge.Bottom, 0),
        "厚度 0 应被拒绝");
    AssertThrows<ArgumentOutOfRangeException>(
        () => DockGeometry.Calculate(monitor, DockEdge.Left, 1921),
        "垂直 Dock 厚度超过显示器宽度应被拒绝");
    AssertThrows<ArgumentOutOfRangeException>(
        () => DockGeometry.Calculate(monitor, DockEdge.Top, 1081),
        "水平 Dock 厚度超过显示器高度应被拒绝");
}

static void TestDockAttention()
{
    var chat = new WindowGroup("exe:chat", "微信", [Window(101, 1, "微信", null, @"C:\Apps\Weixin.exe")]);
    var browser = new WindowGroup("exe:browser", "浏览器", [Window(202, 2, "网页", null, @"C:\Apps\Browser.exe")]);
    var attention = new DockAttention();
    Assert(!attention.Wants(chat), "初始不应有提醒");
    Assert(attention.Flashed(101, 1), "闪烁应记录提醒");
    Assert(!attention.Flashed(101, 1), "重复闪烁不应重复记录");
    Assert(attention.Wants(chat) && !attention.Wants(browser), "只有闪烁窗口所属的应用有提醒");
    Assert(!attention.Flashed(0, 1), "空窗口句柄不应记录");
    Assert(attention.Cleared(101) && !attention.Wants(chat), "激活窗口后提醒应清除");
    attention.Flashed(101, 1);
    attention.Flashed(202, 2);
    attention.RemoveRange([101]);
    Assert(!attention.Wants(chat) && attention.Wants(browser), "已销毁的窗口不应保留提醒");

    // 聊天应用常从隐藏消息窗口发起闪烁：句柄不在枚举里，同进程的可见窗口也要亮标记。
    var hidden = new WindowGroup("exe:hidden", "聊天", [Window(301, 7, "聊天", null, @"C:\Apps\Chat.exe")]);
    attention.Flashed(999, 7);
    Assert(attention.Wants(hidden), "同进程的窗口应命中闪烁标记");
    Assert(
        attention.MarkedWindows.Contains(999),
        "不在枚举里的闪烁句柄应保留标记");
    Assert(!attention.Closed(888) && attention.Wants(hidden), "同进程无关辅助窗口销毁不应清掉未读提醒");
    Assert(attention.Cleared(301, 7) && !attention.Wants(hidden), "激活同进程的可见主窗口应清除隐藏消息窗口标记");

    attention.Flashed(999, 7);
    attention.RemoveRange([999]);
    Assert(!attention.Wants(hidden), "隐藏闪烁窗口销毁后标记清除");
}

static void TestDockIconSize()
{
    AssertEqual(DockLayoutMetrics.ItemCellDip, DockLayoutMetrics.ItemCell(DockState.DefaultIconSize), "默认图标大小应保持原单元宽度");
    AssertEqual(DockLayoutMetrics.DockThicknessDip, DockLayoutMetrics.Thickness(DockState.DefaultIconSize), "默认图标大小应保持原高度");
    AssertEqual(88d, DockLayoutMetrics.ItemCell(64), "64 图标的单元宽度");
    AssertEqual(92d, DockLayoutMetrics.Thickness(64), "64 图标的 Dock 高度（上下内边距收紧为 5）");
    Assert(
        DockLayoutMetrics.ContentLengthForItems(5, 64) > DockLayoutMetrics.ContentLengthForItems(5, 32),
        "图标越大 Dock 越宽");

    var state = new DockState { IconSize = 200 };
    DockPinRules.Normalize(state);
    AssertEqual(DockState.MaximumIconSize, state.IconSize, "过大的图标大小应钳制");
    state.IconSize = double.NaN;
    DockPinRules.Normalize(state);
    AssertEqual(DockState.DefaultIconSize, state.IconSize, "无效图标大小应回到默认值");
}

static void TestDockBottomGap()
{
    AssertEqual(DockLayoutMetrics.EdgeGapDip, DockState.DefaultBottomGapDip, "默认距底高度应等于原固定间距");

    var state = new DockState { BottomGapDip = 200 };
    DockPinRules.Normalize(state);
    AssertEqual(DockState.MaximumBottomGapDip, state.BottomGapDip, "过大的距底高度应钳制");
    state.BottomGapDip = -12;
    DockPinRules.Normalize(state);
    AssertEqual(DockState.MinimumBottomGapDip, state.BottomGapDip, "负的距底高度应钳制");
    state.BottomGapDip = double.NaN;
    DockPinRules.Normalize(state);
    AssertEqual(DockState.DefaultBottomGapDip, state.BottomGapDip, "无效距底高度应回到默认值");
}

static void TestDockVacateRules()
{
    Assert(
        DockVacateRules.ShouldReleaseForMaximized(DockVisibilityMode.ReserveWorkArea, true, true),
        "保留模式且开启开关且前台在该显示器最大化时应让出");
    Assert(
        !DockVacateRules.ShouldReleaseForMaximized(DockVisibilityMode.ReserveWorkArea, true, false),
        "前台未最大化时不应让出");
    Assert(
        !DockVacateRules.ShouldReleaseForMaximized(DockVisibilityMode.ReserveWorkArea, false, true),
        "开关关闭时不应让出");
    Assert(
        !DockVacateRules.ShouldReleaseForMaximized(DockVisibilityMode.SmartHide, true, true),
        "智能隐藏模式无需让出规则");
    Assert(
        !new DockState().ReleaseWhenMaximized,
        "DockState.ReleaseWhenMaximized 默认应关闭，避免始终显示模式下最大化窗口时误隐藏");
    Assert(
        DockVacateRules.IsTrueFullScreen(true, false),
        "几何铺满且未最大化应视为真全屏");
    Assert(
        !DockVacateRules.IsTrueFullScreen(true, true),
        "最大化铺满（如任务栏自动隐藏时）不应视为真全屏");
    Assert(
        !DockVacateRules.IsTrueFullScreen(false, true),
        "未铺满显示器不是全屏");
    Assert(
        !DockVacateRules.IsTrueFullScreen(true, false, hasStandardWindowChrome: true, isToolOrOverlayWindow: false, isTopmost: false),
        "带标准标题栏与可调边框的非置顶满屏窗口不应误判为真全屏");
    Assert(
        DockVacateRules.IsTrueFullScreen(true, false, hasStandardWindowChrome: true, isToolOrOverlayWindow: false, isTopmost: true),
        "置顶全屏窗口（如部分全屏播放器/远程桌面）仍应视为真全屏");
    Assert(
        !DockVacateRules.IsTrueFullScreen(true, false, hasStandardWindowChrome: false, isToolOrOverlayWindow: true, isTopmost: true),
        "工具窗口或穿透遮罩即使铺满置顶也不应视为真全屏");
}

static void TestDockHandleGeometry()
{
    var workArea = new PixelRect(0, 0, 1920, 1032);

    // 默认角位：工作区右端、与 Dock 同带（距底等于 Dock 的边距）。
    AssertEqual(
        new PixelRect(1860, 960, 1904, 1004),
        DockHandleGeometry.Default(workArea, 44, 16, 28),
        "默认把手角位错误");

    AssertEqual(
        new PixelRect(0, 0, 44, 44),
        DockHandleGeometry.Default(new PixelRect(0, 0, 0, 0), 44, 16, 28),
        "退化工作区应回退原点");

    // 拖出工作区被钳制回来并保持尺寸；工作区内不动。
    AssertEqual(
        new PixelRect(0, 0, 44, 44),
        DockHandleGeometry.ClampToWorkArea(new PixelRect(-50, -50, -6, -6), workArea),
        "拖出左上应钳制回工作区");
    AssertEqual(
        new PixelRect(1876, 988, 1920, 1032),
        DockHandleGeometry.ClampToWorkArea(new PixelRect(2000, 1100, 2044, 1144), workArea),
        "拖出右下应钳制回工作区");
    Assert(
        DockHandleGeometry.ClampToWorkArea(new PixelRect(600, 500, 644, 544), workArea)
            == new PixelRect(600, 500, 644, 544),
        "工作区内的把手不应被移动");

    // 右半屏把手：Dock 行在其左侧、底部对齐。
    AssertEqual(
        new PixelRect(1352, 928, 1852, 1004),
        DockHandleGeometry.ExpandedDock(new PixelRect(1860, 960, 1904, 1004), workArea, 76, 500, 8),
        "右半屏展开锚定错误");

    // 左半屏把手：Dock 行在其右侧。
    AssertEqual(
        new PixelRect(72, 928, 572, 1004),
        DockHandleGeometry.ExpandedDock(new PixelRect(20, 960, 64, 1004), workArea, 76, 500, 8),
        "左半屏展开锚定错误");

    // 展开行宽受 Dock 常规位置的 65% 上限钳制。
    var capped = DockHandleGeometry.ExpandedDock(new PixelRect(1860, 960, 1904, 1004), workArea, 76, 3000, 8);
    AssertEqual(1248, capped.Width, "展开行宽应受上限钳制");
    Assert(
        capped.Left >= workArea.Left && capped.Right <= workArea.Right,
        "展开行应留在工作区内");
}

static void TestDockHandleState()
{
    var state = new DockState { HiddenHandleLeftDip = 200, HiddenHandleTopDip = 300 };
    DockPinRules.Normalize(state);
    Assert(
        state.HiddenHandleLeftDip == 200 && state.HiddenHandleTopDip == 300,
        "有效把手位置应保留");

    state.HiddenHandleLeftDip = double.PositiveInfinity;
    DockPinRules.Normalize(state);
    Assert(
        state.HiddenHandleLeftDip is null && state.HiddenHandleTopDip is null,
        "非有限位置应回到默认角位");

    state.HiddenHandleLeftDip = 100;
    DockPinRules.Normalize(state);
    Assert(
        state.HiddenHandleLeftDip is null && state.HiddenHandleTopDip is null,
        "只有一侧的位置应整体作废");
}

static void TestKeepOutOfReservedEdges()
{
    // A 2560×1440 monitor whose bottom 190 px are reserved by the taskbar and the dock.
    var monitor = new PixelRect(0, 0, 2560, 1440);
    var workArea = new PixelRect(0, 0, 2560, 1250);

    AssertEqual(
        new PixelRect(640, 1130, 1660, 1250),
        PlacementResolver.KeepOutOfReservedEdges(new PixelRect(640, 1278, 1660, 1398), monitor, workArea),
        "落在 Dock 占用区域内的卡片应上移到工作区底边");
    AssertEqual(
        new PixelRect(640, 1130, 1660, 1250),
        PlacementResolver.KeepOutOfReservedEdges(new PixelRect(640, 1200, 1660, 1320), monitor, workArea),
        "被 Dock 盖住一部分的卡片也应完整回到工作区");

    var inside = new PixelRect(90, 90, 600, 300);
    AssertEqual(
        inside,
        PlacementResolver.KeepOutOfReservedEdges(inside, monitor, workArea),
        "工作区内的卡片不应移动");
    var parkedOffLeft = new PixelRect(-120, 200, 280, 500);
    AssertEqual(
        parkedOffLeft,
        PlacementResolver.KeepOutOfReservedEdges(parkedOffLeft, monitor, workArea),
        "没有被占用的屏幕边缘不应限制卡片");
    AssertEqual(
        new PixelRect(100, 0, 500, 1300),
        PlacementResolver.KeepOutOfReservedEdges(new PixelRect(100, 100, 500, 1400), monitor, workArea),
        "比工作区更高的卡片应贴齐工作区顶边");

    var leftTaskbar = new PixelRect(80, 0, 2560, 1440);
    AssertEqual(
        new PixelRect(80, 300, 480, 600),
        PlacementResolver.KeepOutOfReservedEdges(new PixelRect(20, 300, 420, 600), monitor, leftTaskbar),
        "左侧被占用时卡片应右移到工作区内");
}

static void TestPlacementRecovery()
{
    var primary = new PixelRect(0, 0, 1920, 1040);
    var disconnected = new PixelRect(-1600, 120, -1200, 420);
    AssertEqual(
        new PixelRect(0, 120, 400, 420),
        PlacementResolver.EnsureVisible(disconnected, [primary], primary),
        "断屏后的窗口应夹取到最近可用工作区");

    var stillVisible = new PixelRect(-30, 80, 170, 280);
    AssertEqual(
        stillVisible,
        PlacementResolver.EnsureVisible(stillVisible, [primary], primary),
        "达到最小可见宽高时不应改变原位置");

    AssertEqual(
        new PixelRect(1620, 740, 1920, 1040),
        PlacementResolver.EnsureVisible(new PixelRect(3000, 2000, 3300, 2300), [], primary),
        "没有工作区快照时应使用回退工作区");

    AssertEqual(
        PerformanceSamplingMode.Reduced,
        PerformanceSamplingRules.ResolveMode(fullScreenInFront: true, desktopHidden: false),
        "全屏应用在前台时应降低采样频率");
    AssertEqual(
        PerformanceSamplingMode.Reduced,
        PerformanceSamplingRules.ResolveMode(fullScreenInFront: false, desktopHidden: true),
        "桌面组件隐藏时应降低采样频率");
    AssertEqual(
        PerformanceSamplingMode.Normal,
        PerformanceSamplingRules.ResolveMode(fullScreenInFront: false, desktopHidden: false),
        "普通状态应保持正常采样");
}

static void TestDisplayArrivalRules()
{
    var primary = new PixelRect(0, 0, 1536, 824);
    var kept = new PixelRect(96, 96, 396, 296);

    // 两张卡在已拔掉的右侧屏上，一张半出剩余屏的右下角。
    var targets = DisplayArrivalRules.Gather(
        [
            new PixelRect(1920, 96, 2220, 296),
            new PixelRect(1920, 480, 2220, 680),
            new PixelRect(1300, 700, 1600, 900),
            kept
        ],
        [primary],
        primary);

    AssertEqual(kept, targets[3], "已在工作区内的卡片应保持原位");
    AssertEqual(new PixelRect(1224, 96, 1524, 296), targets[0], "拔掉屏上的卡片应收拢到最近工作区边缘");
    AssertEqual(new PixelRect(1224, 480, 1524, 680), targets[1], "第二张卡应收拢到下方且不与第一张堆叠");
    AssertEqual(new PixelRect(408, 0, 708, 200), targets[2], "半出界卡片应收拢到不压任何卡片的位置");
    foreach (var target in targets)
    {
        AssertEqual(
            true,
            target.Left >= primary.Left && target.Right <= primary.Right
            && target.Top >= primary.Top && target.Bottom <= primary.Bottom,
            $"收拢后的卡片应完全在工作区内：{target}");
    }

    var stacked = DisplayArrivalRules.Gather(
        [new PixelRect(1920, 96, 2220, 296), new PixelRect(1920, 96, 2220, 296)],
        [primary],
        primary);
    AssertEqual(false, stacked[0].Intersects(stacked[1]), "原位重叠的两张卡收拢后应错开");
    AssertEqual(true, stacked[0].Top <= stacked[1].Top, "收拢应保持卡片原有的阅读顺序");

    // 已回原位的卡不进收拢列表（reserved），但收拢落位必须避让它们的占位。
    var avoidReserved = DisplayArrivalRules.Gather(
        [new PixelRect(1920, 96, 2220, 296)],
        [primary],
        primary,
        [new PixelRect(1224, 96, 1524, 296)]);
    AssertEqual(new PixelRect(0, 120, 300, 320), avoidReserved[0], "收拢应避开已回原位卡片的位置");

    // 屏满放不下时先按比例缩小找空位；顶部整条被占，只有缩到 70% 才放得下。
    var shrink = DisplayArrivalRules.Gather(
        [new PixelRect(1920, 700, 2520, 1000)],
        [primary],
        primary,
        [new PixelRect(0, 0, 1536, 600)],
        [(200, 150)]);
    AssertEqual(new PixelRect(1104, 600, 1524, 810), shrink[0], "放不下时应缩小到能容身的尺寸并避让占位");
    AssertEqual(true, shrink[0].Width < 600, "收拢后的卡片宽度应已缩小");

    // 下限之内仍放不下时退回贴边全尺寸，允许重叠但保证完全可见。
    var floorKept = DisplayArrivalRules.Gather(
        [new PixelRect(1920, 700, 2520, 1300)],
        [primary],
        primary,
        [new PixelRect(0, 0, 1536, 600)],
        [(500, 450)]);
    AssertEqual(new PixelRect(936, 224, 1536, 824), floorKept[0], "高于下限仍放不下时应退回贴边全尺寸");

    // 不传下限时保持原行为：只按原尺寸找位，不做缩放。
    var noShrinkByDefault = DisplayArrivalRules.Gather(
        [new PixelRect(1920, 700, 2520, 1000)],
        [primary],
        primary,
        [new PixelRect(0, 0, 1536, 600)]);
    AssertEqual(new PixelRect(936, 524, 1536, 824), noShrinkByDefault[0], "未传下限时不应缩放，退回贴边全尺寸");

    AssertThrows<ArgumentException>(
        () => DisplayArrivalRules.Gather(
            [new PixelRect(0, 0, 300, 200)],
            [primary],
            primary,
            null,
            []),
        "下限数量与卡片数量不一致时应报参数错误");

    var oversized = DisplayArrivalRules.Gather(
        [new PixelRect(1920, 96, 3420, 1296)],
        [new PixelRect(0, 0, 1000, 600)],
        new PixelRect(0, 0, 1000, 600));
    AssertEqual(new PixelRect(0, 0, 1000, 600), oversized[0], "大于工作区的卡片应收拢为工作区大小");

    var fallback = DisplayArrivalRules.Gather(
        [new PixelRect(3000, 2000, 3300, 2200)],
        [],
        new PixelRect(0, 0, 1920, 1040));
    AssertEqual(new PixelRect(1608, 840, 1908, 1040), fallback[0], "没有工作区快照时应收拢进回退工作区");

    AssertThrows<ArgumentException>(
        () => DisplayArrivalRules.Gather([new PixelRect(0, 0, 10, 10)], [], default),
        "没有可用工作区且无回退时应报参数错误");
    AssertThrows<ArgumentException>(
        () => DisplayArrivalRules.Gather([default], [primary], primary),
        "零面积卡片应报参数错误");
}

static void TestSnapRules()
{
    var area = new PixelRect(0, 0, 1920, 1040);
    var other = new PixelRect(100, 100, 400, 300);

    var nearEdge = SnapRules.Snap(new PixelRect(6, 500, 206, 600), [other], area, threshold: 10, gap: 16, reach: 64);
    AssertEqual(new PixelRect(0, 500, 200, 600), nearEdge.Bounds, "靠近工作区左边缘时应吸附");
    Assert(nearEdge.Guides.Any(guide => guide.X1 == 0 && guide.X2 == 0), "吸附到左边缘应显示竖向参考线");

    var besideOther = SnapRules.Snap(new PixelRect(420, 106, 620, 206), [other], area, threshold: 10, gap: 16, reach: 64);
    AssertEqual(416, besideOther.Bounds.Left, "靠近其他卡片右侧时应按标准间距吸附");
    Assert(besideOther.Guides.Any(guide => guide.Kind == SnapGuideKind.Spacing), "间距吸附应显示间距参考线");
    AssertEqual(100, besideOther.Bounds.Top, "顶边接近其他卡片顶边时应对齐");

    var far = new PixelRect(700, 600, 900, 700);
    var free = SnapRules.Snap(far, [other], area, threshold: 10, gap: 16, reach: 64);
    AssertEqual(far, free.Bounds, "超出吸附距离时位置不变");
    AssertEqual(0, free.Guides.Count, "没有吸附时不显示参考线");

    // Left edges line up, but the other card is 300 px above: too far to matter.
    var alignedButDistant = new PixelRect(104, 600, 304, 700);
    AssertEqual(
        alignedButDistant,
        SnapRules.Snap(alignedButDistant, [other], area, threshold: 10, gap: 16, reach: 64).Bounds,
        "远处卡片的边缘不应吸附");
    var alignedAndNear = new PixelRect(104, 340, 304, 440);
    AssertEqual(
        100,
        SnapRules.Snap(alignedAndNear, [other], area, threshold: 10, gap: 16, reach: 64).Bounds.Left,
        "附近卡片的同侧边缘应对齐");

    // 3 px from touching the other card's right edge: cards snap to the standard gap,
    // never to a flush fit.
    var touching = new PixelRect(403, 120, 603, 220);
    AssertEqual(
        touching,
        SnapRules.Snap(touching, [other], area, threshold: 10, gap: 16, reach: 64).Bounds,
        "卡片不应贴边吸附到另一张卡片");

    // Grid alignment replaces edge snapping: leading edges land on lattice lines anchored
    // at the work-area corner and the size never changes.
    AssertEqual(
        new PixelRect(132, 72, 332, 272),
        SnapRules.SnapToGrid(new PixelRect(130, 70, 330, 270), originX: 100, originY: 40, cell: 16),
        "网格吸附应把左上角对齐到最近格点");
    AssertEqual(
        new PixelRect(100, 40, 200, 200),
        SnapRules.SnapToGrid(new PixelRect(105, 44, 205, 204), originX: 100, originY: 40, cell: 16),
        "离原点不到半格时应吸附到工作区角落的格线");
    AssertEqual(
        100,
        SnapRules.SnapToGrid(new PixelRect(108, 600, 308, 700), originX: 100, originY: 40, cell: 16).Left,
        "正中半格距离时取偶数条格线");
    AssertEqual(far, SnapRules.SnapToGrid(far, 0, 0, cell: 0), "无效格距不应改动位置");
}

static void TestIdentityPriority()
{
    var allIdentities = Window(
        1,
        10,
        "浏览器",
        "Microsoft.Edge.Stable",
        @"C:\Program Files\Edge\msedge.exe");
    AssertEqual(
        "aumid:Microsoft.Edge.Stable",
        WindowGrouping.IdentityKey(allIdentities),
        "AUMID 应优先于路径和 PID");

    var byPath = Window(2, 20, "记事本", null, @"C:/Windows/System32/notepad.exe");
    AssertEqual(
        $"exe:{Path.GetFullPath(@"C:\Windows\System32\notepad.exe")}",
        WindowGrouping.IdentityKey(byPath),
        "可执行文件路径应被规范化");

    var byPid = Window(3, 30, "未知窗口", null, null);
    AssertEqual("pid:30", WindowGrouping.IdentityKey(byPid), "缺少应用身份时应回退 PID");
}

static void TestStableGrouping()
{
    var firstBrowser = Window(10, 100, "第一页", null, @"C:\Apps\Browser.exe");
    var editor = Window(20, 200, "文档", null, @"C:\Apps\Editor.exe");
    var secondBrowser = Window(11, 101, "第二页", null, @"c:\apps\BROWSER.EXE");
    var firstPackaged = Window(30, 300, "设置一", "Windows.Settings", null);
    var secondPackaged = Window(31, 301, "设置二", "windows.settings", null);
    var unknown = Window(40, 400, "工具窗口", null, null);

    var groups = WindowGrouping.Group(
        [firstBrowser, editor, secondBrowser, firstPackaged, secondPackaged, unknown]);

    AssertEqual(4, groups.Count, "应按应用身份合并为四组");
    AssertEqual("Browser", groups[0].DisplayName, "首组显示名应来自可执行文件名");
    AssertEqual(2, groups[0].Windows.Count, "同一路径的两个窗口应合并");
    AssertEqual(firstBrowser.Handle, groups[0].Windows[0].Handle, "组内窗口顺序应保持输入顺序");
    AssertEqual(secondBrowser.Handle, groups[0].Windows[1].Handle, "组内窗口顺序应保持输入顺序");
    AssertEqual("Editor", groups[1].DisplayName, "分组顺序应按首次出现保持稳定");
    AssertEqual("设置一", groups[2].DisplayName, "无路径的分组显示名应回退到标题");
    AssertEqual("工具窗口", groups[3].DisplayName, "PID 分组显示名应优先使用标题");
}

static void TestWindowActionPolicy()
{
    AssertEqual(
        DockClickAction.MinimizeForeground,
        WindowActionPolicy.Decide(groupContainsForeground: true, foregroundIsMinimized: false, targetIsMinimized: false),
        "点击当前前台应用应最小化");
    AssertEqual(
        DockClickAction.Activate,
        WindowActionPolicy.Decide(groupContainsForeground: false, foregroundIsMinimized: false, targetIsMinimized: false),
        "点击后台可见应用应激活");
    AssertEqual(
        DockClickAction.RestoreAndActivate,
        WindowActionPolicy.Decide(groupContainsForeground: false, foregroundIsMinimized: false, targetIsMinimized: true),
        "点击最小化应用应恢复并激活");
    AssertEqual(
        DockClickAction.RestoreAndActivate,
        WindowActionPolicy.Decide(groupContainsForeground: true, foregroundIsMinimized: true, targetIsMinimized: true),
        "前台指针还指着刚最小化的窗口时应恢复而不是再最小化");
}

static void TestDockToggleCooldown()
{
    var now = 0L;
    var cooldown = new DockToggleCooldown(() => now);
    Assert(!cooldown.RecentlyMinimized(101), "未记录过窗口不应命中冷却");

    cooldown.MarkMinimized(101);
    Assert(cooldown.RecentlyMinimized(101), "刚最小化的窗口应命中冷却");
    Assert(!cooldown.RecentlyMinimized(202), "其他窗口不应命中冷却");

    now = DockToggleCooldown.RetentionMs;
    Assert(!cooldown.RecentlyMinimized(101), "超过保留窗口后不应命中冷却");

    cooldown.MarkMinimized(101);
    now = 100;
    cooldown.Clear(101);
    Assert(!cooldown.RecentlyMinimized(101), "恢复后再点应允许最小化（冷却已清）");
}

static void TestUnreadBadgeRules()
{
    AssertEqual(3, UnreadBadgeRules.ParseCount("微信(3)"), "半角括号末尾计数应解析");
    AssertEqual(12, UnreadBadgeRules.ParseCount("微信（12）"), "全角括号计数应解析");
    AssertEqual(5, UnreadBadgeRules.ParseCount("App [5]"), "方括号计数应解析");
    AssertEqual< int?>(null, UnreadBadgeRules.ParseCount("记事本 (2) - 记事本"), "多实例副本标题不是未读");
    AssertEqual<int?>(null, UnreadBadgeRules.ParseCount("Chrome (64 位)"), "括号内非纯数字不是未读");
    AssertEqual<int?>(null, UnreadBadgeRules.ParseCount("微信"), "无括号标题无计数");
    AssertEqual<int?>(null, UnreadBadgeRules.ParseCount(""), "空标题无计数");
    AssertEqual<int?>(null, UnreadBadgeRules.ParseCount(null), "空引用无计数");
    AssertEqual<int?>(null, UnreadBadgeRules.ParseCount("微信(0)"), "零不计入未读");
    AssertEqual("99+", UnreadBadgeRules.BadgeText(100), "超过上限显示 99+");
    AssertEqual("7", UnreadBadgeRules.BadgeText(7), "上限内显示原数字");

    var chat = new WindowGroup("exe:chat", "聊天", [Window(501, 9, "聊天", null, @"C:\Apps\Chat.exe")]);
    var attention = new DockAttention();
    attention.Flashed(999, 9, UnreadBadgeRules.ParseCount("聊天(6)"));
    AssertEqual(6, attention.UnreadCount(chat), "同进程闪烁窗口发布的计数应显示");
    Assert(attention.UnreadCount(chat) > 0 && attention.Wants(chat), "有计数时同样命中注意标记");
    attention.Cleared(999);
    AssertEqual(0, attention.UnreadCount(chat), "闪烁清除后计数归零");
}

static void TestTrayAppRevealRules()
{
    var weixin = TrayAppRevealRules.HotkeyFor(@"D:\App\Weixin\Weixin.exe");
    Assert(weixin is not null, "微信 4.x 应有唤回快捷键");
    AssertEqual(0x0003u, weixin!.Modifiers, "微信唤回快捷键应为 Ctrl+Alt");
    AssertEqual((int)'W', weixin.VirtualKey, "微信唤回快捷键主键应为 W");
    AssertEqual(weixin, TrayAppRevealRules.HotkeyFor(@"C:\Program Files\Tencent\WeChat\WECHAT.EXE"), "微信 3.x 不区分大小写也应匹配");
    AssertEqual<TrayRevealHotkey?>(null, TrayAppRevealRules.HotkeyFor(@"C:\Program Files\Tencent\QQNT\QQ.exe"), "其他托盘应用不发送快捷键");
    AssertEqual<TrayRevealHotkey?>(null, TrayAppRevealRules.HotkeyFor(@"D:\Tools\Weixin.exe.lnk"), "只按可执行文件名匹配");
    AssertEqual<TrayRevealHotkey?>(null, TrayAppRevealRules.HotkeyFor(null), "无路径的固定项（打包应用）不发送快捷键");
}

static void TestUntitledWindowDisplayName()
{
    var packaged = Window(50, 500, string.Empty, "Package.Sample!App", null);
    var unknown = Window(51, 501, string.Empty, null, null);
    var groups = WindowGrouping.Group([packaged, unknown]);

    AssertEqual("Package.Sample!App", groups[0].DisplayName, "空标题打包应用应回退 AUMID");
    AssertEqual("PID 501", groups[1].DisplayName, "完全缺少身份时应回退 PID");
}

static void TestContentLengthBounds()
{
    AssertEqual(
        DockLayoutMetrics.EmptyLengthDip,
        DockLayoutMetrics.ContentLengthForItems(0),
        "0 个项目应使用空 Dock 最小长度");
    AssertEqual(
        DockLayoutMetrics.EmptyLengthDip,
        DockLayoutMetrics.ContentLengthForItems(-3),
        "负项目数应按 0 个项目处理");
    AssertEqual(
        (2 * DockLayoutMetrics.AxisPaddingDip) + (3 * DockLayoutMetrics.ItemCellDip),
        DockLayoutMetrics.ContentLengthForItems(3),
        "少量项目应按单元格线性增长");
    Assert(
        DockLayoutMetrics.ContentLengthForItems(12) > DockLayoutMetrics.ContentLengthForItems(3),
        "项目越多内容长度应越大");

    // Fitting: the preferred size stays while it fits, shrinks proportionally when it
    // does not, and never goes below the minimum.
    var preferred = 48d;
    var fewAvailable = DockLayoutMetrics.ContentLengthForItems(4, preferred);
    AssertEqual(
        preferred,
        DockLayoutMetrics.FitIconSize(4, 0, fewAvailable, preferred),
        "放得下时应保持首选图标大小");
    var many = 30;
    var shrinkAvailable = 1700d;
    var fitted = DockLayoutMetrics.FitIconSize(many, 0, shrinkAvailable, preferred);
    Assert(
        DockLayoutMetrics.MinIconDip <= fitted && fitted < preferred,
        "放不下时应按比例缩小且不低于下限");
    Assert(
        DockLayoutMetrics.ContentLengthForItems(many, fitted) <= shrinkAvailable,
        "缩小后的整行应放得下");
    AssertEqual(
        DockLayoutMetrics.MinIconDip,
        DockLayoutMetrics.FitIconSize(many, 0, 300d, preferred),
        "再挤也不能低于最小图标");
    AssertEqual(
        preferred,
        DockLayoutMetrics.FitIconSize(0, 0, 300d, preferred),
        "没有应用时保持首选大小");

    var capped = FloatingDockGeometry.ClampExtent(
        (int)DockLayoutMetrics.ContentLengthForItems(500),
        1920,
        DockLayoutMetrics.MaxExtentFraction,
        (int)DockLayoutMetrics.EmptyLengthDip);
    AssertEqual(
        (int)Math.Floor(1920 * DockLayoutMetrics.MaxExtentFraction),
        capped,
        "大量项目应被工作区上限截断");

    var belowMinimum = FloatingDockGeometry.ClampExtent(
        10,
        1920,
        DockLayoutMetrics.MaxExtentFraction,
        (int)DockLayoutMetrics.EmptyLengthDip);
    AssertEqual(
        (int)DockLayoutMetrics.EmptyLengthDip,
        belowMinimum,
        "小于最小长度应回到空 Dock 长度");

    var unclamped = FloatingDockGeometry.ClampExtent(
        200,
        1920,
        DockLayoutMetrics.MaxExtentFraction,
        (int)DockLayoutMetrics.EmptyLengthDip);
    AssertEqual(200, unclamped, "上限内的内容长度应保持不变");
}

static void TestHorizontalCentering()
{
    var monitor = new PixelRect(0, 0, 1920, 1080);
    var bottomStrip = DockGeometry.Calculate(monitor, DockEdge.Bottom, 66);
    var bottom = FloatingDockGeometry.Calculate(bottomStrip, DockEdge.Bottom, 164, 56, 10);
    AssertEqual(
        new PixelRect(878, 1014, 1042, 1070),
        bottom,
        "底部 Dock 应水平居中并与屏幕边缘保持间距");

    var topStrip = DockGeometry.Calculate(monitor, DockEdge.Top, 66);
    var top = FloatingDockGeometry.Calculate(topStrip, DockEdge.Top, 164, 56, 10);
    AssertEqual(
        new PixelRect(878, 10, 1042, 66),
        top,
        "顶部 Dock 应水平居中并与屏幕边缘保持间距");

    var negativeMonitor = new PixelRect(-1920, -100, 0, 980);
    var negativeStrip = DockGeometry.Calculate(negativeMonitor, DockEdge.Bottom, 66);
    var negativeDock = FloatingDockGeometry.Calculate(negativeStrip, DockEdge.Bottom, 164, 56, 10);
    AssertEqual(
        new PixelRect(-1042, 914, -878, 970),
        negativeDock,
        "负坐标显示器上的底部 Dock 也应水平居中");
}

static void TestVerticalGeometry()
{
    var monitor = new PixelRect(0, 0, 1920, 1080);
    var leftStrip = DockGeometry.Calculate(monitor, DockEdge.Left, 66);
    var left = FloatingDockGeometry.Calculate(leftStrip, DockEdge.Left, 164, 56, 10);
    AssertEqual(
        new PixelRect(10, 458, 66, 622),
        left,
        "左侧 Dock 应垂直居中并与屏幕边缘保持间距");

    var rightStrip = DockGeometry.Calculate(monitor, DockEdge.Right, 66);
    var right = FloatingDockGeometry.Calculate(rightStrip, DockEdge.Right, 164, 56, 10);
    AssertEqual(
        new PixelRect(1854, 458, 1910, 622),
        right,
        "右侧 Dock 应垂直居中并与屏幕边缘保持间距");

    var clamped = FloatingDockGeometry.Calculate(leftStrip, DockEdge.Left, 5000, 56, 10);
    AssertEqual(
        new PixelRect(10, 0, 66, 1080),
        clamped,
        "内容长度超过工作区高度时应截断到条带范围");
}

static void TestDockItemStateMapping()
{
    var pinnedExplorer = new DockPinnedApp { DisplayName = "文件管理器", ExecutablePath = @"C:\Windows\explorer.exe" };
    var pinnedEditor = new DockPinnedApp { DisplayName = "编辑器", ExecutablePath = @"C:\Apps\Editor.exe" };
    var explorerWindow = Window(
        100,
        900,
        "文件夹",
        null,
        @"c:\windows\EXPLORER.EXE",
        isForeground: true);
    var firstBrowser = Window(200, 800, "第一页", null, @"C:\Apps\Browser.exe");
    var secondBrowser = Window(201, 801, "第二页", null, @"C:\Apps\Browser.exe");
    var groups = WindowGrouping.Group([explorerWindow, firstBrowser, secondBrowser]);

    var items = DockItemComposer.Compose([pinnedExplorer, pinnedEditor], groups);

    AssertEqual(3, items.Count, "固定项与未固定运行项应合并为一个列表");
    Assert(items[0].IsPinned && items[0].IsRunning, "固定的运行中应用应保持固定身份并合并窗口");
    AssertEqual("文件管理器", items[0].DisplayName, "固定运行中的应用应显示固定名称而非分组名称");
    AssertEqual(DockItemState.Active, items[0].State, "包含前台窗口的组应映射为活动状态");
    AssertEqual(DockItemState.Pinned, items[1].State, "未运行的固定应用应映射为仅固定状态");
    Assert(!items[1].IsRunning && items[1].WindowCount == 0, "未运行的固定应用不应携带窗口");
    AssertEqual(DockItemState.Running, items[2].State, "未固定的后台运行应用应映射为运行状态");
    Assert(items[2].HasMultipleWindows, "同应用多窗口应保留分组标记");
    AssertEqual(2, items[2].WindowCount, "分组窗口计数应与快照一致");
    Assert(!items[2].IsPinned, "未固定的运行应用不应误标为固定");

    var unpinnedOnly = DockItemComposer.Compose([], groups);
    AssertEqual(2, unpinnedOnly.Count, "没有固定项时只列出运行分组");
    Assert(!unpinnedOnly[0].IsPinned, "纯运行项不应有固定身份");
}

static void TestAppBarPositionGuard()
{
    var guard = new AppBarPositionGuard();
    var strip = new PixelRect(0, 1380, 2560, 1440);
    Assert(guard.NeedsSubmit(strip), "首次定位必须提交位置");
    guard.Submitted(strip);
    Assert(!guard.NeedsSubmit(strip), "位置未变时不应再次提交，避免触发其他 AppBar 的通知");
    Assert(guard.NeedsSubmit(strip with { Top = 1340, Bottom = 1400 }), "位置变化后应重新提交");
    guard.Reset();
    Assert(guard.NeedsSubmit(strip), "重新注册后应重新提交位置");

    // 换到另一缩放的显示器后，资源管理器可能仍按旧尺寸保留空间。
    var check = new AppBarWorkAreaCheck();
    Assert(!check.NeedsResubmit(strip.Top, strip.Top), "工作区正好止于 Dock 上沿时无需重提");
    Assert(check.NeedsResubmit(1100, strip.Top), "资源管理器多保留了底部空间时应重提一次");
    Assert(!check.NeedsResubmit(1100, strip.Top), "同一工作区边界只重提一次，避免与相邻工具栏来回");
    Assert(check.NeedsResubmit(1400, strip.Top), "资源管理器少保留时同样应重提");
    Assert(!check.NeedsResubmit(strip.Top, strip.Top), "工作区恢复后不再重提");
    Assert(check.NeedsResubmit(1100, strip.Top), "再次出现偏差时应重新纠正");
    check.Reset();
    Assert(check.NeedsResubmit(1100, strip.Top), "重置后对同一边界应允许重新提交");

    long now = 10_000;
    for (var i = 0; i < AppBarPositionGuard.BurstLimit; i++)
    {
        Assert(guard.TryHandleNow(now + i * 10), $"突发上限内的第 {i + 1} 次通知应立即处理");
    }
    Assert(!guard.TryHandleNow(now + 100), "超过突发上限的通知应延后处理");
    Assert(!guard.TryHandleNow(now + 500), "持续来回通知期间仍应延后处理");
    var quiet = now + 500 + (long)AppBarPositionGuard.BurstWindow.TotalMilliseconds + 1;
    Assert(guard.TryHandleNow(quiet), "通知平息后应恢复立即处理");
}

static void TestAppBarReservation()
{
    var reservation = new AppBarReservationState();
    AssertEqual(
        AppBarMessage.Register,
        Single(reservation.Apply(true)),
        "首次预留应注册 AppBar");
    AssertEqual(0, reservation.Apply(true).Count, "重复预留不应重复注册");
    AssertEqual(
        AppBarMessage.Unregister,
        Single(reservation.Apply(false)),
        "关闭预留应发送释放");
    AssertEqual(0, reservation.Release().Count, "未注册时退出不应重复释放");

    reservation.Apply(true);
    AssertEqual(
        AppBarMessage.Unregister,
        Single(reservation.Release()),
        "退出路径必须释放已注册的工作区");
    Assert(!reservation.IsRegistered, "释放后状态应为未注册");

    reservation.Apply(true);
    reservation.ResetRegistration();
    AssertEqual(
        AppBarMessage.Register,
        Single(reservation.Apply(true)),
        "TaskbarCreated 重置后应重新注册");

    reservation.MarkRegisterFailed();
    Assert(!reservation.IsRegistered, "注册失败后状态应回退为未注册");
    AssertEqual(
        AppBarMessage.Register,
        Single(reservation.Apply(true)),
        "注册失败后下一次预留应允许重试");

    AssertEqual(
        AppBarMessage.Unregister,
        Single(reservation.Release()),
        "重试注册成功后退出仍应释放一次");

    reservation.Apply(true);
    AssertEqual(0, reservation.Apply(true).Count, "已注册状态下重复预留保持安静");

    Assert(
        reservation.Apply(true, targetChanged: true).SequenceEqual([AppBarMessage.Unregister, AppBarMessage.Register]),
        "已注册时切换显示器应先释放旧屏再注册新屏");
    Assert(reservation.IsRegistered, "切换显示器重注册后应保持已注册状态");

    reservation.Release();
    AssertEqual(
        AppBarMessage.Register,
        Single(reservation.Apply(true, targetChanged: true)),
        "未注册时切换显示器只需注册一次");

    var monitor = new PixelRect(0, 0, 1920, 1080);
    var negativeTopMonitor = new PixelRect(0, -1080, 1920, 0);
    Assert(
        AppBarReservationState.IsValidStrip(new PixelRect(0, 976, 1920, 1080), monitor, 76),
        "落在目标显示器底部的正常预留条带应有效");
    Assert(
        AppBarReservationState.IsValidStrip(new PixelRect(0, -104, 1920, 0), negativeTopMonitor, 76),
        "上下叠屏上方显示器的负坐标底边条带应有效");
    Assert(
        !AppBarReservationState.IsValidStrip(new PixelRect(0, 1080, 1920, 1080), monitor, 76),
        "高度折叠为零的条带不应视为有效预留");
    Assert(
        !AppBarReservationState.IsValidStrip(new PixelRect(0, 1040, 1920, 1080), monitor, 76),
        "被系统挤压到小于 Dock 最小厚度的条带不应视为有效预留");
    Assert(
        !AppBarReservationState.IsValidStrip(new PixelRect(0, 0, 1920, 104), negativeTopMonitor, 76),
        "被推到相邻显示器上的越界条带不应视为有效预留");

    var builtInBounds = new PixelRect(0, 0, 2560, 1600);
    var externalBounds = new PixelRect(0, 0, 3840, 2160);
    Assert(
        !AppBarReservationState.HasMonitorTargetChanged(null, builtInBounds, 144, @"\\.\DISPLAY1", builtInBounds, 144),
        "首次应用显示器快照不应视为切换显示器");
    Assert(
        !AppBarReservationState.HasMonitorTargetChanged(@"\\.\DISPLAY1", builtInBounds, 144, @"\\.\display1", builtInBounds, 144),
        "同一显示器设备名、边界和 DPI 均未变化时不应触发重注册");
    Assert(
        AppBarReservationState.HasMonitorTargetChanged(@"\\.\DISPLAY1", builtInBounds, 144, @"\\.\DISPLAY2", builtInBounds, 144),
        "设备名变化时应判定为目标显示器变化");
    Assert(
        AppBarReservationState.HasMonitorTargetChanged(@"\\.\DISPLAY1", builtInBounds, 144, @"\\.\DISPLAY1", externalBounds, 144),
        "单屏切换复用同一设备名但分辨率边界变化时应触发重注册");
    Assert(
        AppBarReservationState.HasMonitorTargetChanged(@"\\.\DISPLAY1", externalBounds, 144, @"\\.\DISPLAY1", externalBounds, 168),
        "单屏切换复用同一设备名但有效 DPI 变化时应触发重注册");

    Assert(
        AppBarPositionGuard.ShouldNotifyWindowPosChanged(isRegistered: true, isPositioning: false, isUpdatingLayer: false),
        "已注册 AppBar 收到外部窗口位置变化时应通知 Explorer");
    Assert(
        !AppBarPositionGuard.ShouldNotifyWindowPosChanged(isRegistered: true, isPositioning: false, isUpdatingLayer: true),
        "仅重申 Z 序（UpdateWindowLayer）时不得发送 ABM_WINDOWPOSCHANGED，避免 Explorer 把 Shell_TrayWnd 重新抬到任务栏底边挡板之上");
    Assert(
        !AppBarPositionGuard.ShouldNotifyWindowPosChanged(isRegistered: true, isPositioning: true, isUpdatingLayer: false),
        "Position 主动移动期间不应重复发送 ABM_WINDOWPOSCHANGED");
    Assert(
        !AppBarPositionGuard.ShouldNotifyWindowPosChanged(isRegistered: false, isPositioning: false, isUpdatingLayer: false),
        "未注册 AppBar 时不应发送 ABM_WINDOWPOSCHANGED");
    Assert(
        !AppBarPositionGuard.ShouldNotifyWindowPosChanged(isRegistered: true, isPositioning: false, isUpdatingLayer: false, isZOrderOnly: true),
        "位置与尺寸未变化（SWP_NOMOVE | SWP_NOSIZE）的纯 Z 序消息不应发送 ABM_WINDOWPOSCHANGED");
    Assert(
        AppBarPositionGuard.IsZOrderOnlyWindowPosFlags(0x0001 | 0x0002),
        "仅带 SWP_NOSIZE | SWP_NOMOVE 时应判为纯 Z 序消息");
    Assert(
        !AppBarPositionGuard.IsZOrderOnlyWindowPosFlags(0x0001 | 0x0002 | 0x0040),
        "同时携带 SWP_SHOWWINDOW 时属于可见性变化，不得当成纯 Z 序忽略");
    Assert(
        !AppBarPositionGuard.IsZOrderOnlyWindowPosFlags(0x0001 | 0x0002 | 0x0080),
        "同时携带 SWP_HIDEWINDOW 时属于可见性变化，不得当成纯 Z 序忽略");
}

static void TestDockPinIdentityKey()
{
    var aumidPreferred = new DockPinnedApp
    {
        AppUserModelId = " Microsoft.Edge.Stable ",
        ExecutablePath = @"C:\Program Files\Edge\msedge.exe"
    };
    AssertEqual(
        "aumid:Microsoft.Edge.Stable",
        DockPinRules.IdentityKey(aumidPreferred),
        "AUMID 应优先于可执行文件路径");

    var exeOnly = new DockPinnedApp { ExecutablePath = @"C:/Windows/System32/notepad.exe" };
    AssertEqual(
        $"exe:{Path.GetFullPath(@"C:\Windows\System32\notepad.exe")}",
        DockPinRules.IdentityKey(exeOnly),
        "仅有可执行文件路径时应规范化路径");

    var neither = new DockPinnedApp();
    AssertEqual(string.Empty, DockPinRules.IdentityKey(neither), "缺少身份信息时应返回空字符串");
}

static void TestDockPinMatches()
{
    var browserWindow = Window(1, 100, "浏览器窗口", null, @"C:\Apps\Browser\msedge.exe");
    var plainExeGroup = WindowGrouping.Group([browserWindow])[0];

    var aumidPinned = new DockPinnedApp { AppUserModelId = "Contoso.WebApp" };
    Assert(
        !DockPinRules.Matches(aumidPinned, plainExeGroup),
        "AUMID 固定的应用不应匹配同一浏览器可执行文件的普通分组");

    var exePinned = new DockPinnedApp { ExecutablePath = @"C:\Apps\Browser\msedge.exe" };
    var aumidWindow = new BingLan.Core.Dock.TrackedWindow(
        (nint)2,
        200,
        "打包窗口",
        "Contoso.WebApp",
        @"C:\Apps\Browser\MSEDGE.EXE",
        false,
        false);
    var aumidKeyedGroup = WindowGrouping.Group([aumidWindow])[0];
    Assert(
        DockPinRules.Matches(exePinned, aumidKeyedGroup),
        "可执行文件固定的应用应匹配身份键为 AUMID 但窗口共享同一可执行文件的分组");
}

static void TestExecutablePinPrefersPlainGroup()
{
    var webApp = Window(1, 100, "网页应用", "Contoso.WebApp", @"C:\Apps\Browser\browser.exe");
    var browser = Window(2, 100, "浏览器", null, @"C:\Apps\Browser\browser.exe");
    var groups = WindowGrouping.Group([webApp, browser]);
    var pinned = new DockPinnedApp
    {
        DisplayName = "浏览器",
        ExecutablePath = @"C:\Apps\Browser\browser.exe"
    };

    var items = DockItemComposer.Compose([pinned], groups);
    AssertEqual(2, items.Count, "固定浏览器与网页应用应各占一个图标");
    AssertEqual(WindowGrouping.IdentityKey(browser), items[0].Key, "固定的浏览器应合并普通浏览器窗口");
    Assert(!items[1].IsPinned, "先出现的网页应用不应被浏览器固定项吸收");

    var onlyWebApp = DockItemComposer.Compose([pinned], WindowGrouping.Group([webApp]));
    AssertEqual(1, onlyWebApp.Count, "没有普通浏览器窗口时仍可回退匹配同一可执行文件");
}

static void TestDockHiddenApps()
{
    var capsule = Window(1, 100, "M", null, @"C:\Users\me\AppData\Local\Metrik\metrik.exe");
    var editor = Window(2, 200, "文档", null, @"C:\Apps\Editor.exe");
    var groups = WindowGrouping.Group([capsule, editor]);
    var state = new DockState();
    var hidden = DockPinRules.CreateFromGroup(groups.First(group => group.Windows.Contains(capsule)), "Metrik")!;

    Assert(DockPinRules.Hide(state, hidden), "首次隐藏应成功");
    Assert(!DockPinRules.Hide(state, hidden), "重复隐藏应被忽略");
    var items = DockItemComposer.Compose(state.PinnedApps, groups, state.HiddenApps);
    AssertEqual(1, items.Count, "隐藏应用的窗口不应出现在 Dock");
    AssertEqual(WindowGrouping.IdentityKey(editor), items[0].Key, "其他应用不受影响");

    Assert(DockPinRules.Unhide(state, DockPinRules.IdentityKey(hidden)), "恢复显示应成功");
    AssertEqual(2, DockItemComposer.Compose(state.PinnedApps, groups, state.HiddenApps).Count, "恢复后窗口重新出现");

    DockPinRules.Hide(state, hidden);
    Assert(DockPinRules.Pin(state, DockPinRules.CreateFromGroup(groups.First(group => group.Windows.Contains(capsule)), "Metrik")!),
        "隐藏的应用仍可固定");
    AssertEqual(0, state.HiddenApps.Count, "固定应用后不再隐藏");

    state.HiddenApps.Add(new DockPinnedApp { ExecutablePath = @"C:\Apps\Tool.exe" });
    state.HiddenApps.Add(new DockPinnedApp { ExecutablePath = @"c:\apps\TOOL.EXE" });
    DockPinRules.Normalize(state);
    AssertEqual(1, state.HiddenApps.Count, "规范化应去重隐藏列表");
    AssertEqual("Tool", state.HiddenApps[0].DisplayName, "缺少名称时用可执行文件名");
}

static void TestDockPinCreateFromGroup()
{
    var aumidWindow = Window(1, 10, "打包窗口", "Package.Sample!App", @"C:\Apps\Sample.exe");
    var aumidGroup = WindowGrouping.Group([aumidWindow])[0];

    var created = DockPinRules.CreateFromGroup(aumidGroup, string.Empty);
    Assert(created is not null, "AUMID 分组应可创建固定项");
    AssertEqual("Package.Sample!App", created!.AppUserModelId, "固定项应保留分组的 AUMID");
    AssertEqual(@"C:\Apps\Sample.exe", created.ExecutablePath, "固定项应保留首个窗口的可执行文件路径");
    AssertEqual("Sample", created.DisplayName, "缺少显示名时应回退到分组显示名");

    var withCustomName = DockPinRules.CreateFromGroup(aumidGroup, "  自定义名称  ");
    AssertEqual("自定义名称", withCustomName!.DisplayName, "提供显示名时应使用去除首尾空白后的显示名");

    var unknownWindow = Window(2, 20, "工具窗口", null, null);
    var unknownGroup = WindowGrouping.Group([unknownWindow])[0];
    Assert(
        DockPinRules.CreateFromGroup(unknownGroup, string.Empty) is null,
        "既无 AUMID 也无可执行文件路径时应返回 null");
}

static void TestDockPinCreateFromExecutable()
{
    var created = DockPinRules.CreateFromExecutable(@"C:/Apps/Sample Tool.exe");
    Assert(created is not null, "有效可执行文件路径应可创建固定项");
    AssertEqual(
        Path.GetFullPath(@"C:\Apps\Sample Tool.exe"),
        created!.ExecutablePath,
        "固定项应保留规范化后的可执行文件路径");
    AssertEqual("Sample Tool", created.DisplayName, "显示名应回退到不含扩展名的文件名");

    Assert(DockPinRules.CreateFromExecutable(string.Empty) is null, "空路径不应创建固定项");
    Assert(DockPinRules.CreateFromExecutable("   ") is null, "空白路径不应创建固定项");
}

static void TestDockPin()
{
    var state = new DockState();
    var a = new DockPinnedApp { DisplayName = "A", ExecutablePath = @"C:\Apps\A.exe" };
    var aCased = new DockPinnedApp { DisplayName = "A2", ExecutablePath = @"C:\APPS\A.EXE" };
    Assert(DockPinRules.Pin(state, a), "首次固定应成功");
    Assert(!DockPinRules.Pin(state, aCased), "大小写不同的重复身份不应重复固定");
    AssertEqual(1, state.PinnedApps.Count, "重复身份不应增加固定项数量");

    var withoutIdentity = new DockPinnedApp { DisplayName = "空身份" };
    Assert(!DockPinRules.Pin(state, withoutIdentity), "缺少身份信息的应用不应可固定");

    var b = new DockPinnedApp { DisplayName = "B", ExecutablePath = @"C:\Apps\B.exe" };
    var c = new DockPinnedApp { DisplayName = "C", ExecutablePath = @"C:\Apps\C.exe" };
    Assert(DockPinRules.Pin(state, b), "追加固定应成功");
    Assert(DockPinRules.Pin(state, c, 0), "指定索引固定应插入到对应位置");
    AssertEqual(3, state.PinnedApps.Count, "固定项数量应随固定操作增长");
    AssertEqual("C", state.PinnedApps[0].DisplayName, "指定索引 0 应插入到最前");

    var d = new DockPinnedApp { DisplayName = "D", ExecutablePath = @"C:\Apps\D.exe" };
    Assert(DockPinRules.Pin(state, d, 999), "越界的过大索引应被钳制");
    AssertEqual(state.PinnedApps.Count - 1, state.PinnedApps.IndexOf(d), "过大索引应被钳制到末尾");

    var e = new DockPinnedApp { DisplayName = "E", ExecutablePath = @"C:\Apps\E.exe" };
    Assert(DockPinRules.Pin(state, e, -5), "越界的负数索引应被钳制");
    AssertEqual(0, state.PinnedApps.IndexOf(e), "负数索引应被钳制到最前");
}

static void TestDockUnpin()
{
    var state = new DockState();
    var a = new DockPinnedApp { DisplayName = "A", ExecutablePath = @"C:\Apps\A.exe" };
    DockPinRules.Pin(state, a);
    var key = DockPinRules.IdentityKey(a);

    Assert(DockPinRules.Unpin(state, key), "应可取消固定已存在的应用");
    AssertEqual(0, state.PinnedApps.Count, "取消固定后应移除对应项");
    Assert(!DockPinRules.Unpin(state, key), "重复取消固定应返回 false");
    Assert(!DockPinRules.Unpin(state, "exe:not-exist"), "不存在的身份键取消固定应返回 false");
}

static void TestDockMove()
{
    var state = new DockState();
    var b = new DockPinnedApp { DisplayName = "B", ExecutablePath = @"C:\Apps\B.exe" };
    var c = new DockPinnedApp { DisplayName = "C", ExecutablePath = @"C:\Apps\C.exe" };
    var d = new DockPinnedApp { DisplayName = "D", ExecutablePath = @"C:\Apps\D.exe" };
    DockPinRules.Pin(state, b);
    DockPinRules.Pin(state, c);
    DockPinRules.Pin(state, d);

    Assert(DockPinRules.Move(state, DockPinRules.IdentityKey(d), 0), "应可移动到最前");
    AssertEqual(0, state.PinnedApps.IndexOf(d), "移动后应位于目标索引");

    Assert(!DockPinRules.Move(state, DockPinRules.IdentityKey(d), 0), "已在目标位置的移动应为空操作");
    Assert(!DockPinRules.Move(state, "exe:not-exist", 1), "不存在的身份键移动应返回 false");

    Assert(DockPinRules.Move(state, DockPinRules.IdentityKey(b), 999), "越界目标索引应被钳制到末尾");
    AssertEqual(state.PinnedApps.Count - 1, state.PinnedApps.IndexOf(b), "钳制后应移动到末尾");
}

static void TestDockNormalize()
{
    var state = new DockState
    {
        VisibilityMode = (DockVisibilityMode)99,
        MonitorDeviceName = "   ",
        PinnedApps =
        [
            null!,
            new DockPinnedApp { ExecutablePath = " C:\\Apps\\Foo.exe " },
            new DockPinnedApp { ExecutablePath = @"C:\Apps\FOO.EXE" },
            new DockPinnedApp { AppUserModelId = " ", ExecutablePath = "" },
            new DockPinnedApp { AppUserModelId = "Vendor.App" }
        ]
    };

    DockPinRules.Normalize(state);

    AssertEqual(
        DockVisibilityMode.ReserveWorkArea,
        state.VisibilityMode,
        "非法的可见性模式应回退为保留工作区");
    Assert(state.MonitorDeviceName is null, "空白显示器名应规范化为 null");
    AssertEqual(2, state.PinnedApps.Count, "应丢弃空项并按身份去重");
    AssertEqual("Foo", state.PinnedApps[0].DisplayName, "缺少显示名应回退为可执行文件名");
    AssertEqual("Vendor.App", state.PinnedApps[1].DisplayName, "缺少显示名应回退为 AUMID");
}

static void TestDockAutoHideStartsShown()
{
    var state = new DockAutoHideState();
    Assert(state.IsShown, "初始状态应为显示");
}

static void TestDockAutoHideNoOverlapStaysShown()
{
    var state = new DockAutoHideState();
    var now = DateTimeOffset.UtcNow;
    var changed = state.Update(new DockAutoHideInput(false, false, false, false, false, false), now);
    Assert(!changed, "无重叠时不应触发状态变化");
    Assert(state.IsShown, "无重叠时应保持显示");
}

static void TestDockAttentionForTrayApp()
{
    var launcher = new DockPinnedApp { DisplayName = "DingtalkLauncher", ExecutablePath = @"D:\App\GRP\DingtalkLauncher.exe" };
    var other = new DockPinnedApp { DisplayName = "微信", ExecutablePath = @"C:\Apps\Weixin.exe" };
    var items = DockItemComposer.Compose([launcher, other], []);
    var attention = new DockAttention();

    Assert(!attention.Wants(items[0]) && !attention.Wants(items[1]), "没有闪烁时都不应有提醒");

    // The app is in the tray: no visible window, so no running group, but its hidden
    // window flashed from the real exe below the launcher's folder.
    Assert(attention.Flashed(900, 31352, 2, @"D:\App\GRP\main\current\GRP.exe"), "隐藏窗口的闪烁应记录");
    Assert(attention.Wants(items[0]), "没有可见窗口时，固定的启动器图标应显示提醒");
    Assert(attention.UnreadCount(items[0]) == 2, "固定图标应带上闪烁时发布的未读数");
    Assert(!attention.Wants(items[1]), "其他固定图标不应被提醒");

    Assert(attention.Cleared(901, 31352), "激活该进程的窗口应清除提醒");
    Assert(!attention.Wants(items[0]), "清除后固定图标不应再显示提醒");

    var sameExe = new DockPinnedApp { DisplayName = "x", ExecutablePath = @"C:\Apps\Weixin.exe" };
    var exactItems = DockItemComposer.Compose([sameExe], []);
    attention.Flashed(902, 77, null, @"C:\Apps\Weixin.exe");
    Assert(attention.Wants(exactItems[0]), "同一个 exe 的闪烁应命中固定项");
}

static void TestDockPinLauncherStub()
{
    static WindowGroup Group(string exe) => WindowGrouping.Group(
        [new TrackedWindow(1, 100, "t", null, exe, false, false)]).Single();

    var launcher = new DockPinnedApp { DisplayName = "DingtalkLauncher", ExecutablePath = @"D:\App\GRP\DingtalkLauncher.exe" };
    var dingTalk = Group(@"D:\App\GRP\main\current\GRP.exe");

    Assert(!DockPinRules.Matches(launcher, dingTalk), "精确匹配不应命中启动器与真实进程");
    Assert(DockPinRules.MatchesLauncherStub(launcher, dingTalk), "名为 Launcher 的固定项应匹配其子目录里的真实进程");

    var items = DockItemComposer.Compose([launcher], [dingTalk]);
    Assert(items.Count == 1 && items[0].IsPinned && items[0].IsRunning, "启动器固定项应显示为运行中，且不再多出一个单独图标");

    var squirrel = new DockPinnedApp { DisplayName = "x", ExecutablePath = @"C:\Users\a\AppData\Local\X\x.exe" };
    Assert(DockPinRules.MatchesLauncherStub(squirrel, Group(@"C:\Users\a\AppData\Local\X\app-1.2.3\x.exe")), "同名 exe 在子目录应匹配");
    Assert(!DockPinRules.MatchesLauncherStub(squirrel, Group(@"C:\Users\a\AppData\Local\X\app-1.2.3\other.exe")), "非启动器且名字不同不应匹配");

    var excel = new DockPinnedApp { DisplayName = "Excel", ExecutablePath = @"C:\Program Files\Office\EXCEL.EXE" };
    Assert(!DockPinRules.MatchesLauncherStub(excel, Group(@"C:\Program Files\Office\WINWORD.EXE")), "同目录的另一个程序不应匹配");

    var cmd = new DockPinnedApp { DisplayName = "cmd Launcher", ExecutablePath = @"C:\Windows\System32\cmd.exe" };
    Assert(!DockPinRules.MatchesLauncherStub(cmd, Group(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")), "Windows 目录下的固定项不应使用启动器匹配");

    var exact = new DockPinnedApp { DisplayName = "GRP", ExecutablePath = @"D:\App\GRP\main\current\GRP.exe" };
    var both = DockItemComposer.Compose([launcher, exact], [dingTalk]);
    Assert(both[1].IsRunning && !both[0].IsRunning, "精确固定项应优先于启动器匹配");
}

static void TestDockBounceRules()
{
    Assert(DockBounceRules.BounceCount(true, MotionLevel.Standard, true) == 3, "标准动画应弹跳 3 次");
    Assert(DockBounceRules.BounceCount(true, MotionLevel.Reduced, true) == 1, "减弱动画应只弹跳 1 次");
    Assert(DockBounceRules.BounceCount(true, MotionLevel.Off, true) == 0, "关闭动画不应弹跳");
    Assert(DockBounceRules.BounceCount(false, MotionLevel.Standard, true) == 0, "设置关闭时不应弹跳");
    Assert(DockBounceRules.BounceCount(true, MotionLevel.Standard, false) == 0, "系统动画关闭时不应弹跳");

    Assert(DockBounceRules.AmplitudeDip(10, MotionLevel.Standard) == DockBounceRules.MinAmplitudeDip, "小图标振幅应取下限");
    Assert(DockBounceRules.AmplitudeDip(200, MotionLevel.Standard) == DockBounceRules.MaxAmplitudeDip, "大图标振幅应取上限");
    Assert(
        DockBounceRules.AmplitudeDip(40, MotionLevel.Reduced) < DockBounceRules.AmplitudeDip(40, MotionLevel.Standard),
        "减弱动画的振幅应更小");

    var now = DateTimeOffset.UtcNow;
    Assert(DockBounceRules.IntervalElapsed(null, now), "从未弹跳过应允许");
    Assert(!DockBounceRules.IntervalElapsed(now - TimeSpan.FromSeconds(3), now), "间隔未到不应再次弹跳");
    Assert(DockBounceRules.IntervalElapsed(now - DockBounceRules.MinInterval, now), "间隔已到应允许");
    Assert(DockBounceRules.IntervalElapsed(now + TimeSpan.FromMinutes(1), now), "时钟回拨后应允许");
    Assert(!new DockState().BounceOnAttention, "弹跳提醒默认应关闭");

    // 老用户从 Schema v59 升级到 v60 时默认关闭弹跳提醒；升级后用户主动打开应持久化保留。
    var folder = Path.Combine(Path.GetTempPath(), $"BingLanDockBounce-{Guid.NewGuid():N}");
    try
    {
        var store = new LocalStateStore(folder);
        var legacy = new AppState();
        legacy.Dock.BounceOnAttention = true;
        store.Save(legacy);
        var raw = File.ReadAllText(store.StatePath)
            .Replace($"\"SchemaVersion\": {AppState.CurrentSchemaVersion}", "\"SchemaVersion\": 59", StringComparison.Ordinal);
        File.WriteAllText(store.StatePath, raw);

        var migrated = store.Load();
        AssertEqual(AppState.CurrentSchemaVersion, migrated.SchemaVersion, "v59 状态应迁移到当前 Schema");
        Assert(!migrated.Dock.BounceOnAttention, "v59 老用户升级后弹跳提醒应默认关闭");

        migrated.Dock.BounceOnAttention = true;
        store.Save(migrated);
        var reopened = store.Load();
        Assert(reopened.Dock.BounceOnAttention, "v60 用户主动开启弹跳提醒后重启应保持开启");
    }
    finally
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

static void TestDockAutoHideAlways()
{
    var state = new DockAutoHideState();
    var t0 = DateTimeOffset.UtcNow;
    Assert(!DockAutoHideState.TreatAsOverlapping(false, false), "未开启时无窗口重叠不应视为被遮挡");
    Assert(DockAutoHideState.TreatAsOverlapping(false, true), "未开启时窗口重叠仍视为被遮挡");
    Assert(DockAutoHideState.TreatAsOverlapping(true, false), "开启后无窗口重叠也应视为被遮挡");

    var idle = new DockAutoHideInput(
        DockAutoHideState.TreatAsOverlapping(true, false), false, false, false, false, false);
    Assert(!state.Update(idle, t0), "开启后应先经过隐藏延迟");
    Assert(state.Update(idle, t0 + DockAutoHideState.HideDelay), "开启后无窗口重叠也应在延迟后收起");
    Assert(!state.IsShown, "收起后应处于隐藏状态");

    var atEdge = new DockAutoHideInput(
        DockAutoHideState.TreatAsOverlapping(true, false), false, true, false, false, false);
    Assert(state.Update(atEdge, t0 + DockAutoHideState.HideDelay), "指针到屏幕边缘应唤出");
    Assert(state.IsShown, "唤出后应显示");
    var away = new DockAutoHideInput(
        DockAutoHideState.TreatAsOverlapping(true, false), false, false, false, false, false);
    Assert(!state.Update(away, t0 + DockAutoHideState.HideDelay), "指针离开后重新计时");
    Assert(
        state.Update(away, t0 + DockAutoHideState.HideDelay + DockAutoHideState.HideDelay),
        "指针离开一个延迟后应再次收起");
}

static void TestDockHandleRevealGrace()
{
    var t0 = DateTimeOffset.UtcNow;
    var deadline = DockAutoHideState.UpdateHandleRevealDeadline(
        DateTimeOffset.MinValue,
        t0,
        revealedFromHandle: true,
        pointerOverStrip: false,
        clearRequested: false);
    AssertEqual(t0 + DockAutoHideState.HandleRevealGrace, deadline, "从把手唤出应设置宽限截止时间");

    // 鼠标离开把手、正在移向正中 Dock：最大化窗口覆盖期间，宽限期内应保持显示。
    var state = new DockAutoHideState();
    var maximizedCovered = new DockAutoHideInput(
        WindowOverlapsDock: true,
        ForegroundIsFullScreen: false,
        PointerInRevealZone: false,
        PointerOverDock: false,
        InteractionActive: false,
        OverlapIsMaximizedWindow: true);
    Assert(state.Update(maximizedCovered, t0), "最大化覆盖时应先收起");
    Assert(!state.IsShown, "应已处于隐藏态");

    var tMid = t0 + TimeSpan.FromMilliseconds(500);
    deadline = DockAutoHideState.UpdateHandleRevealDeadline(
        deadline, tMid, revealedFromHandle: false, pointerOverStrip: false, clearRequested: false);
    Assert(tMid < deadline, "移动途中宽限尚未到期");
    var travellingToStrip = maximizedCovered with { InteractionActive = tMid < deadline };
    Assert(state.Update(travellingToStrip, tMid), "从把手唤出后的宽限期内正中 Dock 应保持展开");
    Assert(state.IsShown, "宽限期内正中 Dock 应保持显示");

    // 鼠标到达正中 Dock：宽限立即清零，之后离开正中 Dock 即可按常规规则收起。
    var tArrive = t0 + TimeSpan.FromMilliseconds(900);
    deadline = DockAutoHideState.UpdateHandleRevealDeadline(
        deadline, tArrive, revealedFromHandle: false, pointerOverStrip: true, clearRequested: false);
    AssertEqual(DateTimeOffset.MinValue, deadline, "指针到达正中 Dock 后应清除把手宽限");
    Assert(!state.Update(maximizedCovered with { PointerOverDock = true }, tArrive), "停在正中 Dock 上应保持显示");
    Assert(state.Update(maximizedCovered, tArrive + TimeSpan.FromMilliseconds(50)), "离开正中 Dock 后最大化覆盖应立即收起");

    // 真全屏或拖动把手请求清除时应立刻失效。
    var cleared = DockAutoHideState.UpdateHandleRevealDeadline(
        t0 + DockAutoHideState.HandleRevealGrace,
        tMid,
        revealedFromHandle: true,
        pointerOverStrip: false,
        clearRequested: true);
    AssertEqual(DateTimeOffset.MinValue, cleared, "全屏或拖动把手时应立即清除宽限");
}

static void TestDockGlassRules()
{
    // 自定义外观（如 #FFFFFF 96% -> A=245）不再被 0.24 压成 23% 灰雾。
    AssertEqual((byte)245, DockGlassRules.ComputeBackdropTintAlpha(245, fullTintOpacity: true),
        "自定义 Dock 应按完整不透明度着色，避免白色 96% 变成黑灰");
    AssertEqual((byte)59, DockGlassRules.ComputeBackdropTintAlpha(245, fullTintOpacity: false),
        "跟随卡片外观的默认 Dock 应保持 0.24 通透衰减");
    AssertEqual((byte)0, DockGlassRules.ComputeBackdropTintAlpha(0, fullTintOpacity: true),
        "零透明度保持 0");

    // 任一门控不满足（省电模式、关闭实时毛玻璃、Win10、关透明、高对比度、零透明）均应回退 WPF 半透明底。
    Assert(DockGlassRules.ShouldUseCompositionBackdrop(true, true, true, false, false, 200),
        "Win11 且未开省电、开启透明与毛玻璃时应启用原生合成背板");
    Assert(!DockGlassRules.ShouldUseCompositionBackdrop(true, true, true, batterySaverActive: true, false, 200),
        "笔记本省电模式下应自动回退半透明纯色，避免 HostBackdropBrush 发黑");
    Assert(!DockGlassRules.ShouldUseCompositionBackdrop(true, frostedGlassEnabled: false, true, false, false, 200),
        "关闭 Dock 实时毛玻璃开关时应回退半透明纯色");
    Assert(!DockGlassRules.ShouldUseCompositionBackdrop(isWindows11OrLater: false, true, true, false, false, 200),
        "Win10 不应启用 Win11 原生合成背板");
    Assert(!DockGlassRules.ShouldUseCompositionBackdrop(true, true, transparencyEnabled: false, false, false, 200),
        "系统透明效果关闭时应回退半透明纯色");
    Assert(!DockGlassRules.ShouldUseCompositionBackdrop(true, true, true, false, highContrastEnabled: true, 200),
        "高对比度下应回退系统色");
    Assert(!DockGlassRules.ShouldUseCompositionBackdrop(true, true, true, false, false, surfaceAlpha: 0),
        "无底板零透明时不启用合成背板");
    Assert(!DockGlassRules.ShouldUseCompositionBackdrop(true, true, true, false, false, surfaceAlpha: 1),
        "纯透明无底板占位色 #01FFFFFF（Alpha=1）不应启用磨砂合成背板");

    // 清透玻璃模式下跟随卡片外观应直接使用卡片玻璃浓度，与分组盒透明度一致。
    var lowDensityStyle = new DesktopStyleState { Glass = GlassMode.Light, GlassDensity = 0.30d };
    var clearGlassDock = new DockState { FollowCardLook = true, FrostedGlass = false };
    var frostedDock = new DockState { FollowCardLook = true, FrostedGlass = true };
    var clearSurface = DesktopStyleRules.Dock(lowDensityStyle, clearGlassDock).Surface;
    var frostedSurface = DesktopStyleRules.Dock(lowDensityStyle, frostedDock).Surface;
    Assert(clearSurface.StartsWith("#4C", StringComparison.OrdinalIgnoreCase),
        $"清透玻璃 Dock 跟随 30% 卡片浓度时应直接取 30% 不透明度，实际为 {clearSurface}");
    Assert(clearSurface != frostedSurface, "清透玻璃与磨砂玻璃在低卡片浓度下应有区分");

    // DockState.FrostedGlass 默认为 true 且可持久化关闭。
    var folder = Path.Combine(Path.GetTempPath(), $"BingLanDockFrosted-{Guid.NewGuid():N}");
    try
    {
        var store = new LocalStateStore(folder);
        var state = new AppState();
        Assert(state.Dock.FrostedGlass, "Dock 实时毛玻璃默认开启");
        state.Dock.FrostedGlass = false;
        store.Save(state);
        var reloaded = store.Load();
        Assert(!reloaded.Dock.FrostedGlass, "关闭 Dock 实时毛玻璃后重载应保持关闭");
    }
    finally
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

static void TestDockAutoHideDelay()
{
    var state = new DockAutoHideState();
    var t0 = DateTimeOffset.UtcNow;
    var overlapInput = new DockAutoHideInput(true, false, false, false, false, false);

    Assert(!state.Update(overlapInput, t0), "重叠发生瞬间不应立即隐藏");
    Assert(state.IsShown, "延迟期内应保持显示");

    Assert(
        !state.Update(overlapInput, t0 + DockAutoHideState.HideDelay - TimeSpan.FromMilliseconds(1)),
        "延迟结束前不应隐藏");
    Assert(state.IsShown, "延迟结束前应保持显示");

    Assert(
        state.Update(overlapInput, t0 + DockAutoHideState.HideDelay),
        "达到延迟阈值应隐藏且返回 true");
    Assert(!state.IsShown, "达到延迟后应处于隐藏状态");

    Assert(
        !state.Update(overlapInput, t0 + DockAutoHideState.HideDelay + TimeSpan.FromSeconds(1)),
        "已隐藏后重复满足重叠条件不应再次返回 true");
    Assert(!state.IsShown, "重复调用后应保持隐藏");
}

static void TestDockAutoHidePointerRevealResetsTimer()
{
    var state = new DockAutoHideState();
    var t0 = DateTimeOffset.UtcNow;
    var overlapInput = new DockAutoHideInput(true, false, false, false, false, false);
    state.Update(overlapInput, t0);

    var pointerInZone = t0 + TimeSpan.FromMilliseconds(300);
    Assert(
        !state.Update(new DockAutoHideInput(true, false, true, false, false, false), pointerInZone),
        "指针进入唤出区域时已处于显示状态不应报告变化");
    Assert(state.IsShown, "指针进入唤出区域应保持显示");

    var originalDeadline = t0 + DockAutoHideState.HideDelay + TimeSpan.FromMilliseconds(1);
    Assert(
        !state.Update(overlapInput, originalDeadline),
        "计时器被重置后原定延迟到达时不应隐藏");
    Assert(state.IsShown, "计时器被重置后原定延迟到达时应仍显示");

    // 计时器已在指针进入唤出区域时被清空；originalDeadline 处的重叠输入是重置后第一次
    // 令计时器重新起算的调用，因此新的隐藏时限应从 originalDeadline 起再等待一个 HideDelay。
    var beforeResetDeadline = originalDeadline + DockAutoHideState.HideDelay - TimeSpan.FromMilliseconds(1);
    Assert(!state.Update(overlapInput, beforeResetDeadline), "重新起算的延迟到达前不应隐藏");
    Assert(state.IsShown, "重新起算的延迟到达前应仍显示");

    var resetDeadline = originalDeadline + DockAutoHideState.HideDelay;
    Assert(state.Update(overlapInput, resetDeadline), "重置后重新起算的延迟到达应隐藏");
}

static void TestDockAutoHidePointerOverDockShowsImmediately()
{
    var state = new DockAutoHideState();
    var t0 = DateTimeOffset.UtcNow;
    state.Update(new DockAutoHideInput(true, false, false, false, false, false), t0);
    state.Update(new DockAutoHideInput(true, false, false, false, false, false), t0 + DockAutoHideState.HideDelay);
    Assert(!state.IsShown, "前置条件：延迟到达后应已隐藏");

    var t1 = t0 + DockAutoHideState.HideDelay + TimeSpan.FromSeconds(1);
    Assert(
        state.Update(new DockAutoHideInput(false, false, false, true, false, false), t1),
        "指针悬停在 Dock 上应立即显示");
    Assert(state.IsShown, "指针悬停在 Dock 上应处于显示状态");
}

static void TestDockRevealRules()
{
    var monitor = new PixelRect(0, 0, 1920, 1080);

    // 贴底 Dock：唤出区保持屏幕底部 2px 的窄条。
    var flush = new PixelRect(687, 1012, 1232, 1080);
    Assert(
        DockRevealRules.IsInRevealZone(960, 1079, flush, monitor, 2),
        "贴底 Dock 时屏幕底部 2px 应属于唤出区");
    Assert(
        !DockRevealRules.IsInRevealZone(960, 1076, flush, monitor, 2),
        "贴底 Dock 时边缘条上方不属于唤出区");
    Assert(
        !DockRevealRules.IsInRevealZone(400, 1079, flush, monitor, 2),
        "唤出区横向限制在 Dock 范围内");

    // 悬空 Dock（任务栏条、距底高度或让出残留）：Dock 底边到屏幕底都是唤出区，
    // 否则指针穿越空档时 Dock 会在 600ms 延迟后中途隐藏，图标点不到。
    var floating = new PixelRect(687, 944, 1232, 1012);
    Assert(
        DockRevealRules.IsInRevealZone(960, 1019, floating, monitor, 2),
        "悬空 Dock 下方空档应属于唤出区");
    Assert(
        DockRevealRules.IsInRevealZone(960, 1079, floating, monitor, 2),
        "悬空 Dock 时屏幕最底应属于唤出区");
    Assert(
        !DockRevealRules.IsInRevealZone(960, 1011, floating, monitor, 2),
        "Dock 矩形内部由悬停判定负责，不属于唤出区");
    Assert(
        !DockRevealRules.IsInRevealZone(600, 1079, floating, monitor, 2),
        "悬空 Dock 的唤出区同样限制横向范围");

    // 防御：零或负深度按 1px 处理；Dock 底边超出屏幕时钳回边缘条。
    Assert(
        !DockRevealRules.IsInRevealZone(960, 1075, flush, monitor, 0),
        "深度 0 应按 1px 边缘条处理");
    var overhang = new PixelRect(687, 1012, 1232, 1090);
    Assert(
        DockRevealRules.IsInRevealZone(960, 1079, overhang, monitor, 2),
        "底边超出屏幕的 Dock 仍应保留边缘条唤出区");

    // 原生任务栏常显时底边被占用：碰到任务栏不唤出 Dock；有把手时用把手，“只用底边”不显示把手、只认屏幕最底部窄条。
    var taskbarVisible = new PixelRect(0, 0, 1920, 1032);
    var taskbarHidden = new PixelRect(0, 0, 1920, 1080);
    Assert(!DockRevealRules.IsBottomEdgeFree(taskbarVisible, monitor), "任务栏常显时底边应视为被占用");
    Assert(DockRevealRules.IsBottomEdgeFree(taskbarHidden, monitor), "任务栏自动隐藏时底边应可用");
    AssertEqual(DockEdgeReveal.Zone, DockRevealRules.EdgeReveal(DockRevealMode.EdgeAndHandle, true), "默认应从底边唤出");
    AssertEqual(DockEdgeReveal.None, DockRevealRules.EdgeReveal(DockRevealMode.EdgeAndHandle, false), "默认模式下碰到常显任务栏不应唤出");
    AssertEqual(DockEdgeReveal.None, DockRevealRules.EdgeReveal(DockRevealMode.HandleOnly, true), "只用把手时底边不唤出");
    AssertEqual(DockEdgeReveal.Zone, DockRevealRules.EdgeReveal(DockRevealMode.EdgeOnly, true), "只用底边时从底边唤出");
    AssertEqual(DockEdgeReveal.EdgeStrip, DockRevealRules.EdgeReveal(DockRevealMode.EdgeOnly, false),
        "只用底边且任务栏常显时只认屏幕最底部窄条");
    Assert(DockRevealRules.UsesHandle(DockRevealMode.EdgeAndHandle), "默认应显示把手");
    Assert(DockRevealRules.UsesHandle(DockRevealMode.HandleOnly), "只用把手时显示把手");
    Assert(!DockRevealRules.UsesHandle(DockRevealMode.EdgeOnly), "只用底边时无论任务栏是否常显都不显示把手");

    // 窄条：Dock 悬在任务栏上方时，任务栏中部不算唤出区，屏幕最底部 2px 才算。
    var aboveTaskbar = new PixelRect(687, 940, 1232, 1004);
    var strip = aboveTaskbar with { Bottom = monitor.Bottom };
    Assert(!DockRevealRules.IsInRevealZone(960, 1050, strip, monitor, 2), "任务栏中部不应唤出");
    Assert(DockRevealRules.IsInRevealZone(960, 1079, strip, monitor, 2), "屏幕最底部窄条应唤出");
}

static void TestDockAutoHideFullScreenHidesImmediately()
{
    var state = new DockAutoHideState();
    Assert(
        state.Update(new DockAutoHideInput(false, true, true, false, false, false), DateTimeOffset.UtcNow),
        "全屏前台窗口即使指针在唤出区域也应立即隐藏");
    Assert(!state.IsShown, "全屏前台窗口应隐藏 Dock");
}

static void TestDockAutoHideFullScreenOverridesInteraction()
{
    var state = new DockAutoHideState();
    var t0 = DateTimeOffset.UtcNow;
    Assert(
        state.Update(new DockAutoHideInput(false, true, false, true, true, false), t0),
        "菜单或拖动进行中出现全屏窗口时也应隐藏 Dock");
    Assert(!state.IsShown, "全屏窗口在前台时 Dock 应保持隐藏");

    Assert(
        state.Update(new DockAutoHideInput(false, false, false, false, true, false), t0 + TimeSpan.FromSeconds(1)),
        "全屏结束后进行中的交互应让 Dock 重新显示");
    Assert(state.IsShown, "全屏结束后 Dock 应显示");
}

static void TestShellSurfaceWindows()
{
    foreach (var className in new[] { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Shell_OOBEProxy" })
    {
        Assert(ShellSurfaceWindows.IsShellSurface(className), $"{className} 不应被当作全屏应用");
    }

    foreach (var className in new[] { "Chrome_WidgetWin_1", "CabinetWClass", "UnityWndClass", "" })
    {
        Assert(!ShellSurfaceWindows.IsShellSurface(className), $"{className} 应参与全屏判断");
    }
}

static void TestDockStateV12Migration()
{
    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-dock-migration-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temp);
    try
    {
        var store = new LocalStateStore(temp);
        File.WriteAllText(
            store.StatePath,
            """
            {
              "SchemaVersion": 12,
              "TodoWidgets": [],
              "NoteWidgets": [],
              "InformationWidgets": [],
              "FileBoxes": []
            }
            """);

        var migrated = store.Load();
        var dock = migrated.Dock;
        AssertEqual(AppState.CurrentSchemaVersion, migrated.SchemaVersion, "v12 状态未迁移到当前 Schema");
        Assert(!dock.IsEnabled, "迁移后 Dock 默认不应启用");
        AssertEqual(
            DockVisibilityMode.ReserveWorkArea,
            dock.VisibilityMode,
            "迁移后可见性模式应为保留工作区");
        AssertEqual(0, dock.PinnedApps.Count, "迁移后不应凭空产生固定项");
    }
    finally
    {
        Directory.Delete(temp, true);
    }
}

static void TestDockStateV24Migration()
{
    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-dock-v24-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temp);
    try
    {
        var store = new LocalStateStore(temp);
        File.WriteAllText(
            store.StatePath,
            """
            {
              "SchemaVersion": 24,
              "TodoWidgets": [],
              "NoteWidgets": [],
              "InformationWidgets": [],
              "FileBoxes": [],
              "Dock": {
                "IsEnabled": true,
                "VisibilityMode": 1,
                "IconSize": 48
              }
            }
            """);

        var migrated = store.Load();
        var dock = migrated.Dock;
        AssertEqual(AppState.CurrentSchemaVersion, migrated.SchemaVersion, "v24 状态未迁移到当前 Schema");
        Assert(dock.IsEnabled, "迁移后 Dock 启用状态未保留");
        AssertEqual(DockVisibilityMode.SmartHide, dock.VisibilityMode, "迁移后可见性模式未保留");
        AssertEqual(48d, dock.IconSize, "迁移后图标大小未保留");
        AssertEqual(DockState.DefaultBottomGapDip, dock.BottomGapDip, "迁移后距底高度应为默认值");
        Assert(!dock.ReleaseWhenMaximized, "迁移后最大化让出应为关闭");
    }
    finally
    {
        Directory.Delete(temp, true);
    }
}

static void TestDockStateV25Migration()
{
    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-dock-v25-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temp);
    try
    {
        var store = new LocalStateStore(temp);
        File.WriteAllText(
            store.StatePath,
            """
            {
              "SchemaVersion": 25,
              "TodoWidgets": [],
              "NoteWidgets": [],
              "InformationWidgets": [],
              "FileBoxes": [],
              "Dock": {
                "IsEnabled": true,
                "BottomGapDip": 44
              }
            }
            """);

        var migrated = store.Load();
        var dock = migrated.Dock;
        AssertEqual(AppState.CurrentSchemaVersion, migrated.SchemaVersion, "v25 状态未迁移到当前 Schema");
        Assert(dock.IsEnabled, "迁移后 Dock 启用状态未保留");
        AssertEqual(44d, dock.BottomGapDip, "迁移后距底高度未保留");
        Assert(
            dock.HiddenHandleLeftDip is null && dock.HiddenHandleTopDip is null,
            "v25 状态没有把手位置，迁移后应保持默认角位");
    }
    finally
    {
        Directory.Delete(temp, true);
    }
}

static void TestStartMenuMigration()
{
    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-start-migration-{Guid.NewGuid():N}");
    try
    {
        Directory.CreateDirectory(temp);
        var store = new LocalStateStore(temp);
        File.WriteAllText(store.StatePath, """
            { "SchemaVersion": 31, "Dock": { "IsEnabled": true, "IconSize": 48,
              "PinnedApps": [{ "DisplayName": "应用", "ExecutablePath": "C:\\Apps\\Example.exe" }] },
              "OnboardingCompleted": true }
            """);
        var state = store.Load();
        AssertEqual(AppState.CurrentSchemaVersion, state.SchemaVersion, "应迁移到当前结构");
        Assert(state.Dock.ShowStartMenuButton, "开始菜单入口默认开启");
        Assert(state.Dock.ShowLauncherButton, "旧版本数据中没有的启动面板入口默认开启");
        Assert(state.Dock.ShowDesktopButton, "旧版本数据中没有的“显示桌面”按钮默认开启");
        AssertEqual(DockRevealMode.EdgeAndHandle, state.Dock.RevealMode, "旧版本迁移后唤出方式应为底边和把手");
        AssertEqual(48d, state.Dock.IconSize, "图标大小应保留");
        AssertEqual("C:\\Apps\\Example.exe", state.Dock.PinnedApps.Single().ExecutablePath!, "应用绑定应保留");
        Assert(state.OnboardingCompleted, "升级不应触发首次引导");
    }
    finally
    {
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
    }
}

static void TestDockStateRoundTrip()
{
    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-dock-roundtrip-{Guid.NewGuid():N}");
    try
    {
        var store = new LocalStateStore(temp);
        var state = LocalStateStore.CreateDefault();
        state.Dock.IsEnabled = true;
        state.Dock.ShowStartMenuButton = false;
        state.Dock.ShowLauncherButton = false;
        state.Dock.ShowDesktopButton = false;
        state.Dock.MonitorDeviceName = @"\\.\DISPLAY1";
        state.Dock.VisibilityMode = DockVisibilityMode.SmartHide;
        state.Dock.BottomGapDip = 52;
        state.Dock.ReleaseWhenMaximized = true;
        state.Dock.RevealMode = DockRevealMode.EdgeOnly;
        state.Dock.HiddenHandleLeftDip = 120;
        state.Dock.HiddenHandleTopDip = 88;
        state.Dock.PinnedApps.Add(new DockPinnedApp
        {
            DisplayName = "Edge",
            AppUserModelId = "Microsoft.Edge.Stable"
        });
        state.Dock.PinnedApps.Add(new DockPinnedApp
        {
            DisplayName = "记事本",
            ExecutablePath = @"C:\Windows\System32\notepad.exe"
        });

        state.Dock.HiddenApps.Add(new DockPinnedApp
        {
            DisplayName = "Metrik",
            ExecutablePath = @"C:\Tools\metrik.exe"
        });

        store.Save(state);
        var loaded = store.Load();
        Assert(!loaded.Dock.ShowStartMenuButton, "关闭开始菜单入口的个人设置应保留");
        Assert(!loaded.Dock.ShowLauncherButton, "关闭启动面板入口的个人设置应保留");
        Assert(!loaded.Dock.ShowDesktopButton, "关闭“显示桌面”按钮的个人设置应保留");

        Assert(loaded.Dock.IsEnabled, "Dock 启用状态未保留");
        AssertEqual(@"\\.\DISPLAY1", loaded.Dock.MonitorDeviceName, "显示器名称未保留");
        AssertEqual(DockVisibilityMode.SmartHide, loaded.Dock.VisibilityMode, "可见性模式未保留");
        AssertEqual(52d, loaded.Dock.BottomGapDip, "距底高度未保留");
        Assert(loaded.Dock.ReleaseWhenMaximized, "最大化让出开关未保留");
        AssertEqual(DockRevealMode.EdgeOnly, loaded.Dock.RevealMode, "唤出方式未保留");
        AssertEqual(120d, loaded.Dock.HiddenHandleLeftDip, "把手横坐标未保留");
        AssertEqual(88d, loaded.Dock.HiddenHandleTopDip, "把手纵坐标未保留");
        AssertEqual(2, loaded.Dock.PinnedApps.Count, "固定项数量未保留");
        AssertEqual("Edge", loaded.Dock.PinnedApps[0].DisplayName, "第一个固定项顺序或名称未保留");
        AssertEqual("Microsoft.Edge.Stable", loaded.Dock.PinnedApps[0].AppUserModelId, "AUMID 固定项未保留");
        AssertEqual("记事本", loaded.Dock.PinnedApps[1].DisplayName, "第二个固定项顺序或名称未保留");
        AssertEqual(
            Path.GetFullPath(@"C:\Windows\System32\notepad.exe"),
            loaded.Dock.PinnedApps[1].ExecutablePath,
            "可执行文件固定项未保留");
        AssertEqual(1, loaded.Dock.HiddenApps.Count, "隐藏的应用未保留");
        AssertEqual("Metrik", loaded.Dock.HiddenApps[0].DisplayName, "隐藏应用名称未保留");
    }
    finally
    {
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, true);
        }
    }
}

static void TestDockStateDuplicatePinsDeduped()
{
    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-dock-dedupe-{Guid.NewGuid():N}");
    try
    {
        var store = new LocalStateStore(temp);
        var state = LocalStateStore.CreateDefault();
        state.Dock.PinnedApps.Add(new DockPinnedApp
        {
            DisplayName = "Edge",
            AppUserModelId = "Microsoft.Edge.Stable"
        });
        state.Dock.PinnedApps.Add(new DockPinnedApp
        {
            DisplayName = "Edge 副本",
            AppUserModelId = "microsoft.edge.stable"
        });

        store.Save(state);
        var loaded = store.Load();

        AssertEqual(1, loaded.Dock.PinnedApps.Count, "大小写不同的重复固定项未去重");
        AssertEqual("Edge", loaded.Dock.PinnedApps[0].DisplayName, "去重后应保留先出现的固定项");
    }
    finally
    {
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, true);
        }
    }
}

static TMessage Single<TMessage>(IReadOnlyList<TMessage> messages)
{
    AssertEqual(1, messages.Count, "应恰好产生一条消息");
    return messages[0];
}

static void TestFirstSeenOrder()
{
    var browser = Window(1, 10, "浏览器", null, @"C:\Apps\Browser.exe");
    var editor = Window(2, 20, "编辑器", null, @"C:\Apps\Editor.exe");
    var chat = Window(3, 30, "聊天", null, @"C:\Apps\Chat.exe");
    var first = WindowGrouping.OrderByFirstSeen(WindowGrouping.Group([browser, editor]), []);
    var order = first.Select(group => group.Key).ToArray();

    // Activating the editor moves it to the top of the z-order and a new app appears.
    var reordered = WindowGrouping.OrderByFirstSeen(
        WindowGrouping.Group([chat, editor, browser]),
        order);
    AssertEqual(3, reordered.Count, "应保留全部运行分组");
    AssertEqual(WindowGrouping.IdentityKey(browser), reordered[0].Key, "已出现的应用不应因激活而换位");
    AssertEqual(WindowGrouping.IdentityKey(editor), reordered[1].Key, "已出现的应用应保持原顺序");
    AssertEqual(WindowGrouping.IdentityKey(chat), reordered[2].Key, "新出现的应用应追加到末尾");

    var closed = WindowGrouping.OrderByFirstSeen(
        WindowGrouping.Group([chat, browser]),
        reordered.Select(group => group.Key).ToArray());
    AssertEqual(WindowGrouping.IdentityKey(browser), closed[0].Key, "关闭应用后其余应用顺序不变");
    AssertEqual(WindowGrouping.IdentityKey(chat), closed[1].Key, "关闭应用后其余应用顺序不变");
}

static TrackedWindow Window(
    long handle,
    uint processId,
    string title,
    string? appUserModelId,
    string? executablePath,
    bool isForeground = false) =>
    new((nint)handle, processId, title, appUserModelId, executablePath, isForeground, false);

static void TestPathPins()
{
    var folder = DockPinRules.CreateFromPath(@"C:\Work\Reports\");
    var sameFolder = DockPinRules.CreateFromPath(@"c:/work/reports");
    var file = DockPinRules.CreateFromPath(@"C:\Work\报告.docx");
    var root = DockPinRules.CreateFromPath(@"C:\");
    Assert(folder is not null && sameFolder is not null && file is not null && root is not null, "路径应可创建固定项");
    AssertEqual(@"C:\Work\Reports", folder!.TargetPath, "文件夹路径应去掉末尾分隔符");
    AssertEqual("Reports", folder.DisplayName, "文件夹显示名应为文件夹名");
    AssertEqual("报告.docx", file!.DisplayName, "文件显示名应保留扩展名");
    AssertEqual(@"C:\", root!.TargetPath, "盘符根目录应保留分隔符");
    Assert(DockPinRules.IsPathPin(folder) && !DockPinRules.IsPathPin(new DockPinnedApp { ExecutablePath = @"C:\A.exe" }),
        "应区分路径固定项与应用固定项");
    Assert(DockPinRules.CreateFromPath("  ") is null, "空路径不应创建固定项");

    var state = new DockState();
    Assert(DockPinRules.Pin(state, folder), "文件夹应可固定");
    Assert(!DockPinRules.Pin(state, sameFolder!), "大小写或分隔符不同的同一文件夹应去重");
    Assert(DockPinRules.Pin(state, file), "文件应可固定");
    AssertEqual(2, state.PinnedApps.Count, "固定项数量错误");
    Assert(DockPinRules.Move(state, DockPinRules.IdentityKey(file), 0), "路径固定项应可排序");
    AssertEqual(0, state.PinnedApps.IndexOf(file), "排序后位置错误");

    // Explorer windows (and the program that opens a file) never attach to path pins.
    var explorer = Window(1, 10, "Reports", null, @"C:\Windows\explorer.exe");
    var editor = Window(2, 20, "报告", null, @"C:\Apps\Editor.exe");
    var groups = WindowGrouping.Group([explorer, editor]);
    var items = DockItemComposer.Compose(state.PinnedApps, groups);
    AssertEqual(4, items.Count, "路径固定项不应吸收运行窗口");
    Assert(items.Take(2).All(item => item.Group is null && item.IsPinned), "路径固定项应保持未运行状态");
    Assert(groups.All(group => !DockPinRules.Matches(folder, group)), "路径固定项不应匹配任何分组");

    Assert(DockPinRules.Unpin(state, DockPinRules.IdentityKey(folder)), "路径固定项应可移除");
    AssertEqual(1, state.PinnedApps.Count, "移除后数量错误");
}

static void TestPathPinsPersistAndStayOutOfThemes()
{
    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-dock-pathpin-{Guid.NewGuid():N}");
    try
    {
        var store = new LocalStateStore(temp);
        var state = LocalStateStore.CreateDefault();
        state.Dock.PinnedApps.Add(DockPinRules.CreateFromPath(@"C:\Work\Reports")!);
        state.Dock.PinnedApps.Add(new DockPinnedApp { DisplayName = "记事本", ExecutablePath = @"C:\Windows\System32\notepad.exe" });
        state.Dock.PinnedApps.Add(DockPinRules.CreateFromPath(@"C:\Work\报告.docx")!);
        store.Save(state);
        var loaded = store.Load();
        AssertEqual(3, loaded.Dock.PinnedApps.Count, "路径固定项未保留");
        AssertEqual(@"C:\Work\Reports", loaded.Dock.PinnedApps[0].TargetPath, "文件夹路径未保留");
        Assert(DockPinRules.IsPathPin(loaded.Dock.PinnedApps[2]), "文件固定项未保留");
        AssertEqual("报告.docx", loaded.Dock.PinnedApps[2].DisplayName, "文件显示名未保留");

        var package = ThemeRules.Export(loaded, "测试", new ThemeArea(0, 0, 1920, 1040), new Version(0, 1, 0));
        AssertEqual(1, package.Bindings.Apps.Count, "主题只应包含应用固定项");
        var json = System.Text.Json.JsonSerializer.Serialize(package);
        Assert(!json.Contains("Reports") && !json.Contains("报告") && !json.Contains("Work"),
            "主题导出不应含文件夹或文件的名称与路径");
    }
    finally
    {
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, true);
        }
    }
}

static void TestDockForegroundRules()
{
    Assert(
        DockForegroundRules.EffectiveForeground(40, 40, 30) == 30,
        "搜索会话持有前台时应评估借用前的原窗口");
    Assert(
        DockForegroundRules.EffectiveForeground(50, 40, 30) == 50,
        "前台不在会话持有者窗口上时不替换");
    Assert(
        DockForegroundRules.EffectiveForeground(50, 0, 30) == 50,
        "没有会话持有者时不替换");
    Assert(
        DockForegroundRules.EffectiveForeground(50, 40, 0) == 50,
        "没有借用前的窗口时不替换");
    Assert(
        DockForegroundRules.EffectiveForeground(50, 40, 40) == 50,
        "会话从桌面自身开始（借用者即顶栏）时不替换");
}

static void TestDockFileDropCanOpenFilesAndDecide()
{
    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-dock-drop-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temp);
    try
    {
        var sampleFile = Path.Combine(temp, "notes.txt");
        File.WriteAllText(sampleFile, "hello");

        var pinnedExe = new DockPinnedApp
        {
            DisplayName = "Photoshop",
            ExecutablePath = @"C:\Apps\Photoshop.exe"
        };
        var pinnedAumid = new DockPinnedApp
        {
            DisplayName = "Photos",
            AppUserModelId = "Microsoft.Windows.Photos_8wekyb3d8bbwe!App"
        };
        var pinnedFrameHostOnly = new DockPinnedApp
        {
            DisplayName = "宿主",
            ExecutablePath = @"C:\Windows\System32\ApplicationFrameHost.exe"
        };
        var pinnedFolder = DockPinRules.CreateFromPath(temp, "临时目录")!;
        var pinnedFile = DockPinRules.CreateFromPath(sampleFile, "笔记")!;

        var runningExeWindow = Window(10, 100, "画布", null, @"C:\Apps\Gimp.exe");
        var runningUwpWindow = Window(
            11,
            200,
            "计算器",
            "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            @"C:\Windows\System32\ApplicationFrameHost.exe");
        var bareFrameHostWindow = Window(
            12,
            300,
            "未知宿主",
            null,
            @"C:\Windows\System32\ApplicationFrameHost.exe");

        var items = DockItemComposer.Compose(
            [pinnedExe, pinnedAumid, pinnedFolder, pinnedFile],
            WindowGrouping.Group([runningExeWindow, runningUwpWindow]));

        var psItem = items[0];
        var photosItem = items[1];
        var folderItem = items[2];
        var fileItem = items[3];
        var gimpItem = items[4];
        var calcItem = items[5];
        var barePinnedHostItem = DockItemComposer.Compose([pinnedFrameHostOnly], [])[0];
        var bareRunningHostItem = DockItemComposer.Compose(
            [],
            WindowGrouping.Group([bareFrameHostWindow]))[0];

        Assert(!DockFileDropRules.CanOpenFiles(null), "空目标（空白区域或功能按钮）不可作为打开目标");
        Assert(DockFileDropRules.CanOpenFiles(psItem), "固定桌面应用可接收拖拽文件打开");
        Assert(DockFileDropRules.CanOpenFiles(photosItem), "固定商店应用（AUMID）可接收拖拽文件打开");
        Assert(!DockFileDropRules.CanOpenFiles(barePinnedHostItem), "仅有 ApplicationFrameHost 而无 AUMID 的固定项不能打开文件");
        Assert(!DockFileDropRules.CanOpenFiles(folderItem), "固定文件夹不能接收拖拽文件打开");
        Assert(!DockFileDropRules.CanOpenFiles(fileItem), "固定文件不能接收拖拽文件打开");
        Assert(DockFileDropRules.CanOpenFiles(gimpItem), "仅运行中的桌面应用可接收拖拽文件打开");
        Assert(DockFileDropRules.CanOpenFiles(calcItem), "运行中的商店应用有 AUMID 时可接收拖拽文件打开");
        Assert(!DockFileDropRules.CanOpenFiles(bareRunningHostItem), "无 AUMID 的 ApplicationFrameHost 运行分组不能打开文件");

        var imageDrop = new DroppedPathInfo(@"C:\Pics\hero.psd", @"C:\Pics\hero.psd", false, false);
        var shortcutToImage = new DroppedPathInfo(@"C:\Desktop\hero.lnk", @"D:\Art\hero.psd", false, false);
        var duplicateCaseDrop = new DroppedPathInfo(@"d:/Art/HERO.PSD", @"d:/Art/HERO.PSD", false, false);
        var exeDrop = new DroppedPathInfo(@"C:\Tools\tool.exe", @"C:\Tools\tool.exe", false, true);
        var storeShortcutDrop = new DroppedPathInfo(@"C:\Desktop\App.lnk", string.Empty, false, true);
        var dirDrop = new DroppedPathInfo(@"C:\Projects", @"C:\Projects", true, false);

        AssertEqual(
            DockFileDropAction.None,
            DockFileDropRules.Decide(psItem, []),
            "无有效拖入路径时应返回 None");
        AssertEqual(
            DockFileDropAction.OpenWithApp,
            DockFileDropRules.Decide(psItem, [imageDrop]),
            "图片拖到 Photoshop 上应判定为打开文件");
        AssertEqual(
            DockFileDropAction.OpenWithApp,
            DockFileDropRules.Decide(psItem, [shortcutToImage]),
            "指向文件的快捷方式拖到应用上应按目标文件判定为打开");
        AssertEqual(
            DockFileDropAction.PinToDock,
            DockFileDropRules.Decide(psItem, [exeDrop]),
            "程序拖到应用图标上仍应保留固定到 Dock 语义");
        AssertEqual(
            DockFileDropAction.PinToDock,
            DockFileDropRules.Decide(psItem, [storeShortcutDrop]),
            "不可解析目标路径的程序快捷方式不应尝试当文档打开");
        AssertEqual(
            DockFileDropAction.PinToDock,
            DockFileDropRules.Decide(psItem, [dirDrop]),
            "目录拖到应用图标上仍应保留固定语义");
        AssertEqual(
            DockFileDropAction.PinToDock,
            DockFileDropRules.Decide(null, [imageDrop]),
            "拖到 Dock 空白处/分隔线/功能按钮应保留固定");
        AssertEqual(
            DockFileDropAction.PinToDock,
            DockFileDropRules.Decide(folderItem, [imageDrop]),
            "拖到固定文件夹上应保留固定语义");

        IReadOnlyList<DroppedPathInfo> mixedDrops =
        [
            shortcutToImage,
            duplicateCaseDrop,
            dirDrop,
            exeDrop,
            imageDrop
        ];
        AssertEqual(
            DockFileDropAction.OpenWithApp,
            DockFileDropRules.Decide(psItem, mixedDrops),
            "混合拖拽包含可打开文件与目录/程序时应选择打开可打开的文件");

        var targets = DockFileDropRules.SelectOpenTargets(mixedDrops);
        AssertEqual(2, targets.Count, "应过滤程序和目录并按路径去重");
        AssertEqual(Path.GetFullPath(@"D:\Art\hero.psd"), targets[0], "快捷方式应提取解析后的真实路径");
        AssertEqual(Path.GetFullPath(@"C:\Pics\hero.psd"), targets[1], "普通文件应规范化并保留顺序");
    }
    finally
    {
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, true);
        }
    }
}

static void TestDockFileDropLaunchArgumentsAndHoverState()
{
    AssertEqual(
        "\"C:\\Pics\\a.png\"",
        DockFileDropRules.QuoteArgument(@"C:\Pics\a.png"),
        "普通路径应加引号");
    AssertEqual(
        "\"C:\\My Pics\\hero photo.psd\"",
        DockFileDropRules.QuoteArgument(@"C:\My Pics\hero photo.psd"),
        "含空格路径应保持完整参数");
    AssertEqual(
        "\"D:\\\\\"",
        DockFileDropRules.QuoteArgument(@"D:\"),
        "结尾反斜杠应在闭合引号前成倍转义");
    AssertEqual(
        "\"a\\\\\\\"b\\\\\"",
        DockFileDropRules.QuoteArgument("a\\\"b\\"),
        "内部引号与反斜杠应按 CommandLineToArgvW 规则转义");

    AssertEqual(
        "\"C:\\a.png\" \"C:\\My Docs\\b.psd\"",
        DockFileDropRules.BuildLaunchArguments(null, [@"C:\a.png", @"C:\My Docs\b.psd"]),
        "多文件应依次加引号并空格拼接");
    AssertEqual(
        "--new-window \"C:\\a.png\"",
        DockFileDropRules.BuildLaunchArguments("  --new-window  ", [@"C:\a.png"]),
        "快捷方式原有参数应保留在文件路径之前");
    AssertEqual(
        "--flag",
        DockFileDropRules.BuildLaunchArguments("--flag", []),
        "无文件目标时只保留修剪后的原有参数");

    var hover = new DockDragHoverState();
    var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    Assert(!hover.Hover("exe:ps.exe", isRunning: true, t0), "刚进入运行应用图标不应立即抬起窗口");
    AssertEqual("exe:ps.exe", hover.HoveredKey, "应记录当前悬停的应用键");
    Assert(
        !hover.Hover("exe:ps.exe", isRunning: true, t0 + TimeSpan.FromMilliseconds(499)),
        "未满 500ms 不应触发窗口抬起");
    Assert(
        hover.Hover("exe:ps.exe", isRunning: true, t0 + DockDragHoverState.RaiseDelay),
        "悬停满 500ms 应触发一次窗口抬起");
    Assert(
        !hover.Hover("EXE:PS.EXE", isRunning: true, t0 + TimeSpan.FromMilliseconds(650)),
        "同一次连续悬停不应重复触发窗口抬起");

    Assert(
        !hover.Hover("exe:edge.exe", isRunning: true, t0 + TimeSpan.FromMilliseconds(800)),
        "切换到另一个运行应用应重新计时");
    AssertEqual("exe:edge.exe", hover.HoveredKey, "切换后应更新悬停键");
    Assert(
        !hover.Hover("exe:edge.exe", isRunning: true, t0 + TimeSpan.FromMilliseconds(1299)),
        "新应用未满 500ms 不应触发");
    Assert(
        hover.Hover("exe:edge.exe", isRunning: true, t0 + TimeSpan.FromMilliseconds(1300)),
        "新应用悬停满 500ms 应触发");

    Assert(!hover.Hover("exe:pinned-only.exe", isRunning: false, t0 + TimeSpan.FromSeconds(2)), "未运行的固定项不应保持悬停计时");
    Assert(hover.HoveredKey is null, "未运行项应清空悬停状态");

    Assert(!hover.Hover("exe:ps.exe", isRunning: true, t0 + TimeSpan.FromSeconds(3)), "重新进入运行项应从头计时");
    Assert(hover.Hover("exe:ps.exe", isRunning: true, t0 + TimeSpan.FromSeconds(3.5)), "满 500ms 后应触发");
    hover.Clear();
    Assert(hover.HoveredKey is null, "Clear 应清除悬停记录");
    Assert(!hover.Hover("exe:ps.exe", isRunning: true, t0 + TimeSpan.FromSeconds(4)), "Clear 后再次悬停应重新计时");
    Assert(hover.Hover("exe:ps.exe", isRunning: true, t0 + TimeSpan.FromSeconds(4.5)), "Clear 后再次满 500ms 应允许触发");
}

static void TestTopLevelWindowFilterRules()
{
    Assert(
        WindowActivationRules.ShouldIncludeTopLevelWindow(
            isAppWindow: false,
            isToolWindow: false,
            isNoActivate: false,
            isCloaked: false,
            isMinimized: false,
            isLayeredClickThrough: false,
            isDisabled: false,
            hasTitle: true,
            width: 1200,
            height: 800),
        "正常可见顶层窗口应纳入 Dock");

    Assert(
        WindowActivationRules.ShouldIncludeTopLevelWindow(
            isAppWindow: false,
            isToolWindow: false,
            isNoActivate: false,
            isCloaked: false,
            isMinimized: true,
            isLayeredClickThrough: false,
            isDisabled: false,
            hasTitle: true,
            width: 0,
            height: 0),
        "最小化窗口即使矩形为 0 也不应被尺寸规则过滤");

    Assert(
        WindowActivationRules.ShouldIncludeTopLevelWindow(
            isAppWindow: true,
            isToolWindow: true,
            isNoActivate: false,
            isCloaked: false,
            isMinimized: false,
            isLayeredClickThrough: false,
            isDisabled: true,
            hasTitle: false,
            width: 0,
            height: 0),
        "显式 WS_EX_APPWINDOW 未隐藏时应保留");

    Assert(
        !WindowActivationRules.ShouldIncludeTopLevelWindow(
            isAppWindow: true,
            isToolWindow: false,
            isNoActivate: false,
            isCloaked: true,
            isMinimized: false,
            isLayeredClickThrough: false,
            isDisabled: false,
            hasTitle: true,
            width: 800,
            height: 600),
        "DWM 隐藏（Cloaked）的窗口不应纳入");

    Assert(
        !WindowActivationRules.ShouldIncludeTopLevelWindow(
            isAppWindow: false,
            isToolWindow: false,
            isNoActivate: false,
            isCloaked: false,
            isMinimized: false,
            isLayeredClickThrough: false,
            isDisabled: false,
            hasTitle: true,
            width: 1,
            height: 1),
        "1x1 占位顶层窗口应被过滤");

    Assert(
        !WindowActivationRules.ShouldIncludeTopLevelWindow(
            isAppWindow: false,
            isToolWindow: false,
            isNoActivate: false,
            isCloaked: false,
            isMinimized: false,
            isLayeredClickThrough: true,
            isDisabled: false,
            hasTitle: true,
            width: 1200,
            height: 800),
        "分层鼠标穿透阴影/水印层不应纳入");

    Assert(
        !WindowActivationRules.ShouldIncludeTopLevelWindow(
            isAppWindow: false,
            isToolWindow: false,
            isNoActivate: false,
            isCloaked: false,
            isMinimized: false,
            isLayeredClickThrough: false,
            isDisabled: true,
            hasTitle: false,
            width: 300,
            height: 200),
        "禁用且无标题的辅助顶层窗口应被过滤");

    Assert(
        WindowActivationRules.ShouldIncludeTopLevelWindow(
            isAppWindow: false,
            isToolWindow: false,
            isNoActivate: false,
            isCloaked: false,
            isMinimized: false,
            isLayeredClickThrough: false,
            isDisabled: true,
            hasTitle: true,
            width: 1200,
            height: 800),
        "带标题的禁用主窗口（如模态框背后）应保留");

    Assert(
        !WindowActivationRules.ShouldIncludeTopLevelWindow(
            isAppWindow: false,
            isToolWindow: true,
            isNoActivate: false,
            isCloaked: false,
            isMinimized: false,
            isLayeredClickThrough: false,
            isDisabled: false,
            hasTitle: true,
            width: 800,
            height: 600),
        "无 WS_EX_APPWINDOW 的工具窗口不应纳入");
}

static void TestSelectActivationTarget()
{
    Assert(WindowActivationRules.SelectActivationTarget([]) is null, "空候选列表应返回 null");
    AssertThrows<ArgumentNullException>(() => WindowActivationRules.SelectActivationTarget(null!), "null 候选列表应抛异常");

    var helperUnminimizedDisabled = new WindowCandidateInfo(100, IsMinimized: false, IsEnabled: false, HasEnabledPopup: false, HasTitle: false);
    var mainMinimized = new WindowCandidateInfo(200, IsMinimized: true, IsEnabled: true, HasEnabledPopup: false, HasTitle: true);
    AssertEqual(
        (nint)200,
        WindowActivationRules.SelectActivationTarget([helperUnminimizedDisabled, mainMinimized])?.Handle ?? 0,
        "WPS 主窗口最小化时应优先选它，而不是未最小化的禁用辅助窗");

    var mainWithModalPopup = new WindowCandidateInfo(300, IsMinimized: false, IsEnabled: false, HasEnabledPopup: true, HasTitle: true);
    AssertEqual(
        (nint)300,
        WindowActivationRules.SelectActivationTarget([mainMinimized, mainWithModalPopup])?.Handle ?? 0,
        "带可用模态弹窗的未最小化主窗应视为可直接激活");

    var unminimizedUntitled = new WindowCandidateInfo(400, IsMinimized: false, IsEnabled: true, HasEnabledPopup: false, HasTitle: false);
    var minimizedUntitled = new WindowCandidateInfo(500, IsMinimized: true, IsEnabled: true, HasEnabledPopup: false, HasTitle: false);
    AssertEqual(
        (nint)400,
        WindowActivationRules.SelectActivationTarget([minimizedUntitled, mainMinimized, unminimizedUntitled])?.Handle ?? 0,
        "未最小化可激活窗口应优先于最小化窗口");
    AssertEqual(
        (nint)200,
        WindowActivationRules.SelectActivationTarget([minimizedUntitled, mainMinimized])?.Handle ?? 0,
        "同为最小化可激活窗口时应优先选带标题的主窗");
    AssertEqual(
        (nint)100,
        WindowActivationRules.SelectActivationTarget([helperUnminimizedDisabled])?.Handle ?? 0,
        "全不可直接激活时应兜底返回首项");
}

static void TestActivationBlockedMessages()
{
    var targetElevated = WindowActivationRules.FormatSwitchBlockedMessage("WPS Office", targetElevated: true, foregroundElevated: true);
    Assert(targetElevated.Contains("WPS Office", StringComparison.Ordinal), "目标提权提示应包含应用名");
    Assert(targetElevated.Contains("管理员", StringComparison.Ordinal), "目标提权提示应说明管理员权限原因");
    Assert(!targetElevated.Contains("已闪烁提醒", StringComparison.Ordinal), "目标提权时不得谎称已闪烁提醒");

    var foregroundElevated = WindowActivationRules.FormatSwitchBlockedMessage("WPS Office", targetElevated: false, foregroundElevated: true);
    Assert(foregroundElevated.Contains("前台", StringComparison.Ordinal) && foregroundElevated.Contains("管理员", StringComparison.Ordinal),
        "前台提权提示应说明前台程序为管理员");
    Assert(foregroundElevated.Contains("已闪烁提醒", StringComparison.Ordinal), "同权限目标窗口仍应提示已闪烁");

    AssertEqual(
        "Windows 阻止了切换到 WPS Office，已闪烁提醒",
        WindowActivationRules.FormatSwitchBlockedMessage("WPS Office", targetElevated: false, foregroundElevated: false),
        "常规拦截文案应保留既有格式并使用展示名");

    var taskbarElevated = WindowActivationRules.FormatStartMenuBlockedMessage(taskbarElevated: true, foregroundElevated: true);
    Assert(taskbarElevated.Contains("资源管理器", StringComparison.Ordinal) && taskbarElevated.Contains("Win", StringComparison.Ordinal),
        "任务栏提权时应提示资源管理器以管理员运行并引导键盘 Win 键");

    var startForegroundElevated = WindowActivationRules.FormatStartMenuBlockedMessage(taskbarElevated: false, foregroundElevated: true);
    Assert(startForegroundElevated.Contains("前台", StringComparison.Ordinal) && startForegroundElevated.Contains("管理员", StringComparison.Ordinal),
        "前台提权时应提示先切换窗口或按 Win 键");

    AssertEqual(
        "无法打开 Windows 开始菜单，请稍后重试。",
        WindowActivationRules.FormatStartMenuBlockedMessage(taskbarElevated: false, foregroundElevated: false),
        "常规开始菜单失败应保留既有提示");
}

static void TestMenuDismissState()
{
    // 触屏本 / 精密触控板双指轻点打开菜单时，打开瞬间右键尚未完全松开：不得把同一次抬起当成外部点击闪退。
    var state = new MenuDismissState();
    state.Start(initialForeground: 100, anyMouseCurrentlyHeld: true, escapeCurrentlyHeld: false);
    Assert(!state.IsMouseArmed && state.IsEscapeArmed, "按键尚未松开时鼠标外部收起保持未就绪");
    Assert(
        !state.ShouldCloseOnPoll(
            anyMouseCurrentlyHeld: true,
            anyMousePressedLatch: false,
            escapeCurrentlyHeld: false,
            escapePressedLatch: false,
            pointerInsideMenu: false,
            currentForeground: 100,
            menuPopupHandle: 500,
            systemMenuForeground: false,
            out var armedMouse,
            out _),
        "打开点击尚未松开时菜单保持打开");
    Assert(!armedMouse, "仍按住时不提前就绪");

    Assert(
        !state.ShouldCloseOnPoll(
            anyMouseCurrentlyHeld: false,
            anyMousePressedLatch: false,
            escapeCurrentlyHeld: false,
            escapePressedLatch: false,
            pointerInsideMenu: false,
            currentForeground: 100,
            menuPopupHandle: 500,
            systemMenuForeground: false,
            out armedMouse,
            out _),
        "打开点击刚松开当拍只完成就绪并清空残余按键位，不关闭菜单");
    Assert(armedMouse && state.IsMouseArmed, "松开后进入外部点击监测就绪态");

    // 菜单内的点击不提前关掉弹窗，交由菜单项自身执行命令。
    Assert(
        !state.ShouldCloseOnPoll(
            anyMouseCurrentlyHeld: true,
            anyMousePressedLatch: true,
            escapeCurrentlyHeld: false,
            escapePressedLatch: false,
            pointerInsideMenu: true,
            currentForeground: 100,
            menuPopupHandle: 500,
            systemMenuForeground: false,
            out _,
            out _),
        "菜单内部按下左键不应由外部监测提前关闭");

    // 悬停在菜单内时后台窗口自然切换前台不误关菜单；移出菜单后点击外部或切换管理员窗口立即收起。
    Assert(
        !state.ShouldCloseOnPoll(
            anyMouseCurrentlyHeld: false,
            anyMousePressedLatch: false,
            escapeCurrentlyHeld: false,
            escapePressedLatch: false,
            pointerInsideMenu: true,
            currentForeground: 200,
            menuPopupHandle: 500,
            systemMenuForeground: false,
            out _,
            out _),
        "指针位于菜单内部时前台变化不误关菜单");

    Assert(
        state.ShouldCloseOnPoll(
            anyMouseCurrentlyHeld: false,
            anyMousePressedLatch: true,
            escapeCurrentlyHeld: false,
            escapePressedLatch: false,
            pointerInsideMenu: false,
            currentForeground: 200,
            menuPopupHandle: 500,
            systemMenuForeground: false,
            out _,
            out _),
        "松开就绪后在菜单外点击应立即收起菜单");

    // 逃逸键与点击提权窗口（UIPI 下无按键锁存但前台句柄变化）同样可收起。
    var escapeState = new MenuDismissState();
    escapeState.Start(initialForeground: 100, anyMouseCurrentlyHeld: false, escapeCurrentlyHeld: false);
    Assert(
        escapeState.ShouldCloseOnPoll(
            anyMouseCurrentlyHeld: false,
            anyMousePressedLatch: false,
            escapeCurrentlyHeld: false,
            escapePressedLatch: true,
            pointerInsideMenu: true,
            currentForeground: 100,
            menuPopupHandle: 500,
            systemMenuForeground: false,
            out _,
            out _),
        "按 Esc 应立即收起菜单");

    var elevatedSwitchState = new MenuDismissState();
    elevatedSwitchState.Start(initialForeground: 100, anyMouseCurrentlyHeld: false, escapeCurrentlyHeld: false);
    Assert(
        elevatedSwitchState.ShouldCloseOnPoll(
            anyMouseCurrentlyHeld: false,
            anyMousePressedLatch: false,
            escapeCurrentlyHeld: false,
            escapePressedLatch: false,
            pointerInsideMenu: false,
            currentForeground: 300,
            menuPopupHandle: 500,
            systemMenuForeground: false,
            out _,
            out _),
        "指针在菜单外且前台切换到其他窗口时应立即收起菜单");
}

static void TestDockHoverMagnification()
{
    var state = new DockState();
    Assert(!state.HoverMagnification, "悬停放大应默认关闭");
    AssertEqual(DockState.DefaultHoverMagnificationScale, state.HoverMagnificationScale, "默认放大强度应为 1.5");

    // 规范化钳制范围 [1.2, 1.7] 与非法值回退。
    state.HoverMagnificationScale = 2.5d;
    DockPinRules.Normalize(state);
    AssertEqual(1.7d, state.HoverMagnificationScale, "超出上限的放大强度应钳制到 1.7");
    state.HoverMagnificationScale = 0.8d;
    DockPinRules.Normalize(state);
    AssertEqual(1.2d, state.HoverMagnificationScale, "低于下限的放大强度应钳制到 1.2");
    state.HoverMagnificationScale = double.NaN;
    DockPinRules.Normalize(state);
    AssertEqual(1.5d, state.HoverMagnificationScale, "NaN 放大强度应回退到默认值 1.5");

    // 余弦衰减：中心最大、对称、单调递减、边界精确为 1.0。
    const double radius = 140d;
    var peak = DockMagnificationRules.ComputeScale(0d, radius, 1.5d);
    var near = DockMagnificationRules.ComputeScale(35d, radius, 1.5d);
    var mid = DockMagnificationRules.ComputeScale(70d, radius, 1.5d);
    var far = DockMagnificationRules.ComputeScale(105d, radius, 1.5d);
    var edge = DockMagnificationRules.ComputeScale(radius, radius, 1.5d);
    var outside = DockMagnificationRules.ComputeScale(radius + 40d, radius, 1.5d);
    var symmetric = DockMagnificationRules.ComputeScale(-35d, radius, 1.5d);
    Assert(Math.Abs(peak - 1.5d) < 1e-6, "距离为 0 时应达到最大放大倍数");
    Assert(Math.Abs(mid - 1.25d) < 1e-6, "半半径处余弦权重应为 0.5");
    Assert(peak > near && near > mid && mid > far && far > 1d, "余弦衰减应随距离严格单调递减");
    Assert(Math.Abs(near - symmetric) < 1e-6, "左右两侧距离对称");
    AssertEqual(1d, edge, "影响半径边界处应精确回落到 1.0");
    AssertEqual(1d, outside, "超出影响半径应保持 1.0");

    // 动效档位与系统动画偏好：标准有缩放和位移，减弱缩短时长且零位移，关闭/系统关动画不放大。
    AssertEqual(1.5d, DockMagnificationRules.EffectiveMaxScale(true, 1.5d, MotionLevel.Standard, true), "标准档允许放大");
    AssertEqual(1.5d, DockMagnificationRules.EffectiveMaxScale(true, 1.5d, MotionLevel.Reduced, true), "减弱档允许放大");
    AssertEqual(1d, DockMagnificationRules.EffectiveMaxScale(true, 1.5d, MotionLevel.Off, true), "关闭档不放大");
    AssertEqual(1d, DockMagnificationRules.EffectiveMaxScale(true, 1.5d, MotionLevel.Standard, false), "系统关闭动画时不放大");
    AssertEqual(1d, DockMagnificationRules.EffectiveMaxScale(false, 1.5d, MotionLevel.Standard, true), "开关关闭时不放大");

    Assert(DockMagnificationRules.ComputeLiftDip(1.5d, 40d, MotionLevel.Standard) < 0d, "标准档应有向上位移");
    AssertEqual(0d, DockMagnificationRules.ComputeLiftDip(1.5d, 40d, MotionLevel.Reduced), "减弱档不得产生位移");
    AssertEqual(0d, DockMagnificationRules.ComputeLiftDip(1.5d, 40d, MotionLevel.Off), "关闭档不得产生位移");
    Assert(DockMagnificationRules.SmoothingRatePerSecond(MotionLevel.Reduced)
        > DockMagnificationRules.SmoothingRatePerSecond(MotionLevel.Standard),
        "减弱档收敛速率应快于标准档");
    Assert(DockMagnificationRules.ReturnDuration(MotionLevel.Reduced)
        < DockMagnificationRules.ReturnDuration(MotionLevel.Standard),
        "减弱档归位时长应短于标准档");

    // 顶部溢出缓冲区计算：开启时覆盖放大增量 + 抬升，关闭时为 0。
    var overflowStandard = DockMagnificationRules.RequiredTopOverflowDip(true, 1.7d, 64d, MotionLevel.Standard, true);
    var overflowReduced = DockMagnificationRules.RequiredTopOverflowDip(true, 1.7d, 64d, MotionLevel.Reduced, true);
    Assert(overflowStandard > (0.7d * 64d) && overflowStandard > overflowReduced, "标准档缓冲区应大于图标净增量且覆盖抬升");
    AssertEqual(0d, DockMagnificationRules.RequiredTopOverflowDip(false, 1.7d, 64d, MotionLevel.Standard, true), "关闭时不应预留顶部缓冲区");
    AssertEqual(0d, DockMagnificationRules.RequiredTopOverflowDip(true, 1.7d, 64d, MotionLevel.Off, true), "关闭动效时不应预留顶部缓冲区");

    // 命中区域（SetWindowRgn）与玻璃背景窗边界：常态完全排除顶部预留区，放大时才临时放开 Dock 窗口区域。
    var idleRegion = DockMagnificationRules.ComputeHitRegionRect(1425, 134, 44, isMagnificationActive: false, isMorphAnimating: false);
    Assert(idleRegion is { Left: 0, Top: 44, Right: 1425, Bottom: 134, Height: 90 },
        "常态下 Dock 命中区域必须排除顶部 44 px 预留区，仅覆盖 90 px 本体");
    Assert(DockMagnificationRules.ComputeHitRegionRect(1425, 134, 44, isMagnificationActive: true, isMorphAnimating: false) is null,
        "悬停放大进行中应放开窗口区域，避免裁切放大的图标");
    Assert(DockMagnificationRules.ComputeHitRegionRect(1425, 134, 44, isMagnificationActive: false, isMorphAnimating: true) is null,
        "形变动画期间不应限制窗口区域");
    Assert(DockMagnificationRules.ComputeHitRegionRect(1425, 90, 0, isMagnificationActive: false, isMorphAnimating: false) is null,
        "关闭放大无预留区时不应设置裁剪区域");

    var fullWindow = new PixelRect(567, 1424, 1992, 1558);
    var (backdropIdle, insetIdle) = DockMagnificationRules.ComputeBackdropSurfaceBounds(fullWindow, 44, isMorphAnimating: false);
    AssertEqual(44, insetIdle, "玻璃背景窗口应向下缩进 44 px 以避让预留区");
    Assert(backdropIdle == new PixelRect(567, 1468, 1992, 1558) && backdropIdle.Height == 90,
        "玻璃背景窗口的屏幕边界必须严格落在 Dock 本体 90 px 内，不得覆盖上方预留区");
    var (backdropMorph, insetMorph) = DockMagnificationRules.ComputeBackdropSurfaceBounds(fullWindow, 44, isMorphAnimating: true);
    AssertEqual(0, insetMorph, "形变动画期间玻璃背景应保留完整包围盒");
    Assert(backdropMorph == fullWindow, "形变动画期间玻璃背景边界不应缩进");

    // 运行中切换放大开关（关→开 / 开→关）需要重置槽位并剥离残余动画时钟；已开启状态下调整倍率不打断当前悬停。
    Assert(DockMagnificationRules.ShouldResetSlotsOnOverflowChange(0d, 44d),
        "悬停放大从关闭切换到开启时必须重置槽位以清除普通悬停 Zoom 留下的动画时钟");
    Assert(DockMagnificationRules.ShouldResetSlotsOnOverflowChange(44d, 0d),
        "悬停放大从开启切换到关闭时必须重置槽位");
    Assert(DockMagnificationRules.ShouldResetSlotsOnOverflowChange(0d, 0d),
        "保持关闭时允许重置槽位");
    Assert(!DockMagnificationRules.ShouldResetSlotsOnOverflowChange(44d, 52d),
        "保持开启仅改变放大倍率或尺寸时不应中断当前悬停放大");

    // 悬停放大受系统动画或动效档位限制时的设置页原因提示。
    Assert(DockMagnificationRules.InactiveReason(false, MotionLevel.Off, false) is null,
        "开关关闭时不应显示暂不生效提示");
    Assert(DockMagnificationRules.InactiveReason(true, MotionLevel.Standard, true) is null
            && DockMagnificationRules.InactiveReason(true, MotionLevel.Reduced, true) is null,
        "系统动画开启且动效非关闭时不应显示暂不生效提示");
    Assert(DockMagnificationRules.SettingDescription(true, MotionLevel.Standard, true)
            == DockMagnificationRules.DefaultSettingDescription,
        "正常生效时应保留默认说明文案");
    var osOffReason = DockMagnificationRules.InactiveReason(true, MotionLevel.Standard, false);
    Assert(osOffReason is not null && osOffReason.Contains("动画效果", StringComparison.Ordinal),
        "系统关闭动画时应明确提示开启 Windows 动画效果");
    var motionOffReason = DockMagnificationRules.InactiveReason(true, MotionLevel.Off, true);
    Assert(motionOffReason is not null && motionOffReason.Contains("动效", StringComparison.Ordinal),
        "Dock 动效设为关闭时应明确提示修改动效档位");
    var bothOffReason = DockMagnificationRules.InactiveReason(true, MotionLevel.Off, false);
    Assert(bothOffReason is not null
            && bothOffReason.Contains("动画效果", StringComparison.Ordinal)
            && bothOffReason.Contains("动效", StringComparison.Ordinal),
        "系统动画与 Dock 动效同时关闭时应同时提示两项原因");
    Assert(DockMagnificationRules.ToggleStatusMessage(true, MotionLevel.Standard, false).Contains("暂不生效", StringComparison.Ordinal)
            && DockMagnificationRules.ToggleStatusMessage(true, MotionLevel.Off, true).Contains("暂不生效", StringComparison.Ordinal)
            && DockMagnificationRules.ToggleStatusMessage(true, MotionLevel.Standard, true) == "已开启 Dock 悬停放大"
            && DockMagnificationRules.ToggleStatusMessage(false, MotionLevel.Off, false) == "已关闭 Dock 悬停放大",
        "切换悬停放大后的状态反馈应如实反映是否受动画设置限制");
}

static void TestDockEdgeWording()
{
    // Bottom keeps the original wording word for word.
    Assert(DockEdgeWording.PageSubtitle(DockEdge.Bottom) == "屏幕底部的应用栏：显示哪些应用、怎么出现、多大。", "底部副标题应保持原文");
    Assert(DockEdgeWording.EnabledDescription(DockEdge.Bottom) == "屏幕底部居中显示固定的应用和正在运行的应用", "底部启用说明应保持原文");
    Assert(DockEdgeWording.ReserveLabel(DockEdge.Bottom) == "始终显示，保留底部空间", "底部预留选项应保持原文");
    Assert(DockEdgeWording.ReserveAutomationName(DockEdge.Bottom) == "始终显示，并保留屏幕底部空间", "底部预留选项朗读名应保持原文");
    Assert(DockEdgeWording.GapHeader(DockEdge.Bottom) == "距底部高度", "底部距离标题应保持原文");
    Assert(DockEdgeWording.MaximizeReleaseHeader(DockEdge.Bottom) == "最大化窗口时临时隐藏并释放底部空间", "底部最大化释放标题应保持原文");
    Assert(DockEdgeWording.MaximizeReleaseDescription(DockEdge.Bottom).Contains("鼠标移到屏幕底边或把手"), "底部最大化释放说明应保持原文");

    foreach (var (edge, side) in new[] { (DockEdge.Left, "左侧"), (DockEdge.Right, "右侧") })
    {
        var texts = new[]
        {
            DockEdgeWording.PageSubtitle(edge), DockEdgeWording.EnabledDescription(edge),
            DockEdgeWording.ReserveLabel(edge), DockEdgeWording.ReserveAutomationName(edge),
            DockEdgeWording.AdvancedCaption(edge), DockEdgeWording.GapHeader(edge),
            DockEdgeWording.GapDescription(edge), DockEdgeWording.GapAutomationName(edge),
            DockEdgeWording.MaximizeReleaseHeader(edge), DockEdgeWording.MaximizeReleaseDescription(edge)
        };
        Assert(texts.All(text => !text.Contains('底')), $"{side}停靠的设置文案不应再出现“底”字");
        Assert(DockEdgeWording.ReserveLabel(edge).Contains(side), $"{side}停靠的预留选项应写明{side}");
    }
}

static void TestDockSideDocking()
{
    // 1. 停靠位置规范化：仅支持底部、左侧、右侧；顶部及非法值回落到底部。
    AssertEqual(DockEdge.Bottom, DockEdgeRules.Normalize(DockEdge.Bottom), "底部停靠应保持");
    AssertEqual(DockEdge.Left, DockEdgeRules.Normalize(DockEdge.Left), "左侧停靠应保持");
    AssertEqual(DockEdge.Right, DockEdgeRules.Normalize(DockEdge.Right), "右侧停靠应保持");
    AssertEqual(DockEdge.Bottom, DockEdgeRules.Normalize(DockEdge.Top), "不支持顶部停靠（避免与顶部信息条冲突），应回落到底部");
    AssertEqual(DockEdge.Bottom, DockEdgeRules.Normalize((DockEdge)99), "非法停靠边枚举应回落到底部");
    Assert(!DockEdgeRules.IsVertical(DockEdge.Bottom), "底部为横向排布");
    Assert(DockEdgeRules.IsVertical(DockEdge.Left) && DockEdgeRules.IsVertical(DockEdge.Right), "左右为纵向排布");

    var normalizedState = new DockState { Edge = DockEdge.Top };
    DockPinRules.Normalize(normalizedState);
    AssertEqual(DockEdge.Bottom, normalizedState.Edge, "DockPinRules.Normalize 应把顶部停靠纠正为底部");

    // 2. 放大锚点与向屏幕内侧隆起方向。
    AssertEqual((0.5d, 1d), DockEdgeRules.MagnificationOrigin(DockEdge.Bottom), "底部锚点在底边中点");
    AssertEqual((0d, 0.5d), DockEdgeRules.MagnificationOrigin(DockEdge.Left), "左侧锚点在左边中点（向右放大）");
    AssertEqual((1d, 0.5d), DockEdgeRules.MagnificationOrigin(DockEdge.Right), "右侧锚点在右边中点（向左放大）");

    var bottomLift = DockEdgeRules.ComputeLiftVectorDip(DockEdge.Bottom, 1.5d, 40d, MotionLevel.Standard);
    var leftLift = DockEdgeRules.ComputeLiftVectorDip(DockEdge.Left, 1.5d, 40d, MotionLevel.Standard);
    var rightLift = DockEdgeRules.ComputeLiftVectorDip(DockEdge.Right, 1.5d, 40d, MotionLevel.Standard);
    Assert(bottomLift.X == 0d && bottomLift.Y < 0d, "底部停靠放大时应向上隆起");
    Assert(leftLift.X > 0d && leftLift.Y == 0d, "左侧停靠放大时应向右（屏幕内侧）隆起");
    Assert(rightLift.X < 0d && rightLift.Y == 0d, "右侧停靠放大时应向左（屏幕内侧）隆起");
    AssertEqual(Math.Abs(bottomLift.Y), Math.Abs(leftLift.X), "左右隆起幅度应与底部一致");
    AssertEqual((0d, 0d), DockEdgeRules.ComputeLiftVectorDip(DockEdge.Left, 1.5d, 40d, MotionLevel.Reduced), "减弱档不应产生位移");

    // 3. 放大透明留白扩展、SetWindowRgn 命中区域与玻璃背板裁剪。
    var monitor = new PixelRect(0, 0, 1920, 1080);
    var leftPlacement = new PixelRect(12, 240, 80, 840);
    var leftExpanded = DockEdgeRules.ExpandWindowForOverflow(leftPlacement, monitor, DockEdge.Left, 44);
    AssertEqual(new PixelRect(12, 240, 124, 840), leftExpanded, "左侧停靠放大留白应向右扩展");
    var leftRegion = DockMagnificationRules.ComputeHitRegionRect(leftExpanded.Width, leftExpanded.Height, DockEdge.Left, 44, false, false);
    AssertEqual(new PixelRect(0, 0, 68, 600), leftRegion!.Value, "左侧停靠常态命中区域应排除右侧 44px 透明留白");
    var (leftBackdrop, leftInsetX, leftInsetY) = DockMagnificationRules.ComputeBackdropSurfaceBounds(leftExpanded, 0, 0, 68, 600, false);
    AssertEqual(new PixelRect(12, 240, 80, 840), leftBackdrop, "左侧停靠玻璃背板应仅覆盖本体，不覆盖右侧透明留白");
    Assert(leftInsetX == 0 && leftInsetY == 0, "左侧停靠背板原点无偏移");

    var rightPlacement = new PixelRect(1840, 240, 1908, 840);
    var rightExpanded = DockEdgeRules.ExpandWindowForOverflow(rightPlacement, monitor, DockEdge.Right, 44);
    AssertEqual(new PixelRect(1796, 240, 1908, 840), rightExpanded, "右侧停靠放大留白应向左扩展");
    var rightRegion = DockMagnificationRules.ComputeHitRegionRect(rightExpanded.Width, rightExpanded.Height, DockEdge.Right, 44, false, false);
    AssertEqual(new PixelRect(44, 0, 112, 600), rightRegion!.Value, "右侧停靠常态命中区域应排除左侧 44px 透明留白");
    var (rightBackdrop, rightInsetX, rightInsetY) = DockMagnificationRules.ComputeBackdropSurfaceBounds(rightExpanded, 44, 0, 68, 600, false);
    AssertEqual(new PixelRect(1840, 240, 1908, 840), rightBackdrop, "右侧停靠玻璃背板应向右缩进 44px 仅覆盖本体");
    Assert(rightInsetX == 44 && rightInsetY == 0, "右侧停靠背板应记录 44px 左侧偏移");

    // 4. AppBar 预留校验、工作区边对齐与顶部信息条/底部任务栏共存避让。
    var leftStrip = new PixelRect(0, 32, 96, 1032);
    Assert(AppBarReservationState.IsValidStrip(leftStrip, monitor, 68, DockEdge.Left), "有效左侧预留条应通过校验");
    Assert(!AppBarReservationState.IsValidStrip(new PixelRect(0, 32, 20, 1032), monitor, 68, DockEdge.Left), "宽度不足的左侧预留条应判无效");
    AssertEqual(new PixelRect(0, 32, 96, 1032), DockEdgeRules.AdjustQueriedAppBarStrip(new PixelRect(0, 32, 120, 1032), DockEdge.Left, 96), "左侧查询结果应按左侧基准保留厚度");
    AssertEqual(new PixelRect(1824, 32, 1920, 1032), DockEdgeRules.AdjustQueriedAppBarStrip(new PixelRect(1800, 32, 1920, 1032), DockEdge.Right, 96), "右侧查询结果应按右侧基准保留厚度");
    AssertEqual((96, 96), DockEdgeRules.WorkAreaEdgesForCheck(new PixelRect(96, 32, 1920, 1032), leftStrip, DockEdge.Left), "左侧预留条应比对工作区 Left 与预留条 Right");
    AssertEqual((1824, 1824), DockEdgeRules.WorkAreaEdgesForCheck(new PixelRect(0, 32, 1824, 1032), new PixelRect(1824, 32, 1920, 1032), DockEdge.Right), "右侧预留条应比对工作区 Right 与预留条 Left");

    var safeVertical = DockEdgeRules.ComputeSafeVerticalBounds(
        monitor,
        liveWorkArea: new PixelRect(0, 0, 1920, 1032),
        nativeTaskbarSafeArea: new PixelRect(0, 0, 1920, 1032),
        topBarBottomPx: 40);
    AssertEqual(new PixelRect(0, 40, 1920, 1032), safeVertical, "左右 Dock 纵向范围应避开顶部信息条（Top=40）与底部任务栏（Bottom=1032）");
    var clippedLeft = DockEdgeRules.ClipStripCrossAxis(new PixelRect(0, 0, 96, 1080), safeVertical, DockEdge.Left);
    AssertEqual(new PixelRect(0, 40, 96, 1032), clippedLeft, "裁剪后的左侧预留条不得与顶栏或底栏重叠");
    AssertEqual(992, DockEdgeRules.AvailableExtentPixels(safeVertical, DockEdge.Left), "左右 Dock 可用长度应取纵向高度");

    // 5. 自动隐藏触发区与任务栏共存判定。
    var bottomTaskbarWorkArea = new PixelRect(0, 0, 1920, 1032);
    Assert(DockRevealRules.IsEdgeFree(bottomTaskbarWorkArea, monitor, DockEdge.Left), "底部任务栏不应视为占用左侧边缘");
    Assert(DockRevealRules.IsEdgeFree(bottomTaskbarWorkArea, monitor, DockEdge.Right), "底部任务栏不应视为占用右侧边缘");
    Assert(!DockRevealRules.IsEdgeFree(new PixelRect(60, 0, 1920, 1080), monitor, DockEdge.Left), "左侧任务栏应视为占用左侧边缘");

    Assert(DockRevealRules.IsInRevealZone(0, 500, leftPlacement, monitor, 2, DockEdge.Left), "指针贴屏幕左边缘应在左侧唤出区");
    Assert(DockRevealRules.IsInRevealZone(10, 500, leftPlacement, monitor, 2, DockEdge.Left), "指针穿越左缘与悬空 Dock 间空隙时应保持在唤出区");
    Assert(!DockRevealRules.IsInRevealZone(50, 500, leftPlacement, monitor, 2, DockEdge.Left), "深入屏幕内部不在左侧唤出区");
    Assert(!DockRevealRules.IsInRevealZone(0, 100, leftPlacement, monitor, 2, DockEdge.Left), "纵向超出 Dock 范围不应触发左侧唤出");
    Assert(DockRevealRules.IsInRevealZone(1919, 500, rightPlacement, monitor, 2, DockEdge.Right), "指针贴屏幕右边缘应在右侧唤出区");
    Assert(DockRevealRules.IsInRevealZone(1910, 500, rightPlacement, monitor, 2, DockEdge.Right), "指针穿越右缘与悬空 Dock 间空隙时应保持在唤出区");
    Assert(!DockRevealRules.IsInRevealZone(1860, 500, rightPlacement, monitor, 2, DockEdge.Right), "深入屏幕内部不在右侧唤出区");

    Assert(!DockVacateRules.ShouldYieldToSystemSurface(true, isTaskbarWindow: true, DockEdge.Left, DockEdge.Bottom, surfaceIntersectsDock: false),
        "底部任务栏 + 左侧 Dock 时点击底部任务栏不应无故让位");
    Assert(!DockVacateRules.ShouldYieldToSystemSurface(true, isTaskbarWindow: true, DockEdge.Right, DockEdge.Bottom, surfaceIntersectsDock: false),
        "底部任务栏 + 右侧 Dock 时点击底部任务栏不应无故让位");
    Assert(DockVacateRules.ShouldYieldToSystemSurface(true, isTaskbarWindow: true, DockEdge.Bottom, DockEdge.Bottom, surfaceIntersectsDock: false),
        "底部任务栏 + 底部 Dock 时仍应让位");
    Assert(DockVacateRules.ShouldYieldToSystemSurface(true, isTaskbarWindow: false, DockEdge.Left, DockEdge.Bottom, surfaceIntersectsDock: false),
        "系统开始菜单或快速设置弹出时左右 Dock 仍应礼让");

    // 6. 隐藏把手默认角位与纵向展开、重现锚点、Y 轴拖拽排序索引。
    var leftHandle = DockHandleGeometry.Default(bottomTaskbarWorkArea, 44, 16, 28, DockEdge.Left);
    AssertEqual(new PixelRect(28, 972, 72, 1016), leftHandle, "左侧 Dock 默认把手应贴近左下工作区");
    var leftHandleExpanded = DockHandleGeometry.ExpandedDock(leftHandle, bottomTaskbarWorkArea, 68, 400, 8, DockEdge.Left);
    Assert(leftHandleExpanded.Width == 68 && leftHandleExpanded.Height == 400 && leftHandleExpanded.Bottom <= leftHandle.Top - 8,
        "左下把手展开左侧 Dock 时应纵向向上生长");
    AssertEqual((0d, 300d), DockEdgeRules.StripAnchorPointDip(leftPlacement, DockEdge.Left, 1d), "左侧重现锚点在左边中点");
    AssertEqual((68d, 300d), DockEdgeRules.StripAnchorPointDip(rightPlacement, DockEdge.Right, 1d), "右侧重现锚点在右边中点");

    AssertEqual(1, DockEdgeRules.ComputePinInsertIndex(1, pointerAxis: 10d, itemLength: 56d, pinnedCount: 4), "拖入上半格应插在目标之前");
    AssertEqual(2, DockEdgeRules.ComputePinInsertIndex(1, pointerAxis: 40d, itemLength: 56d, pinnedCount: 4), "拖入下半格应插在目标之后");
    AssertEqual(2, DockEdgeRules.ComputePinMoveIndex(sourceIndex: 0, targetIndex: 2, pointerAxis: 40d, itemLength: 56d, pinnedCount: 4), "从上方拖到目标下半格应落在目标位置");
    AssertEqual(1, DockEdgeRules.ComputePinMoveIndex(sourceIndex: 3, targetIndex: 1, pointerAxis: 10d, itemLength: 56d, pinnedCount: 4), "从下方拖到目标上半格应落在目标位置");

    // 7. Schema v57 -> v58 迁移与持久化往返。
    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-dock-edge-{Guid.NewGuid():N}");
    try
    {
        Directory.CreateDirectory(temp);
        var store = new LocalStateStore(temp);
        File.WriteAllText(store.StatePath, """{ "SchemaVersion": 57, "Dock": { "IsEnabled": true, "IconSize": 48 } }""");
        var migrated = store.Load();
        AssertEqual(AppState.CurrentSchemaVersion, migrated.SchemaVersion, "v57 状态应迁移到最新 Schema 版本");
        AssertEqual(DockEdge.Bottom, migrated.Dock.Edge, "旧配置缺省停靠位置应迁移为底部");
        AssertEqual(48d, migrated.Dock.IconSize, "迁移不得丢失原有 Dock 设置");

        migrated.Dock.Edge = DockEdge.Right;
        store.Save(migrated);
        var reloaded = store.Load();
        AssertEqual(DockEdge.Right, reloaded.Dock.Edge, "右侧停靠设置应完整持久化往返");

        File.WriteAllText(store.StatePath, """{ "SchemaVersion": 58, "Dock": { "Edge": 1 } }""");
        var sanitized = store.Load();
        AssertEqual(DockEdge.Bottom, sanitized.Dock.Edge, "手工写入的顶部停靠枚举在加载时应被规范化为底部");
    }
    finally
    {
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, true);
        }
    }
}

static void TestDockQuitRules()
{
    Assert(DockQuitRules.IsProtectedExecutable(@"C:\Windows\explorer.exe"), "不能允许结束 explorer.exe");
    Assert(DockQuitRules.IsProtectedExecutable(@"C:\Windows\System32\ApplicationFrameHost.exe"), "不能允许结束 ApplicationFrameHost.exe");
    Assert(DockQuitRules.IsProtectedExecutable(@"C:\Users\demo\AppData\Local\Programs\BingLan\BingLan.exe"), "不能允许结束冰蓝桌面自身");
    Assert(!DockQuitRules.IsProtectedExecutable(@"D:\App\Weixin\Weixin.exe"), "普通桌面应用不应被保护拦截");

    var explorerPin = new DockPinnedApp { DisplayName = "资源管理器", ExecutablePath = @"C:\Windows\explorer.exe" };
    Assert(!DockQuitRules.CanOfferQuit(new DockItem("exe:explorer", "资源管理器", explorerPin, null)),
        "资源管理器不应显示结束应用入口");

    var pathPin = new DockPinnedApp { DisplayName = "下载", TargetPath = @"C:\Users\demo\Downloads" };
    Assert(!DockQuitRules.CanOfferQuit(new DockItem("path:dl", "下载", pathPin, null)),
        "固定文件/文件夹不应显示结束应用入口");

    var wechatPin = new DockPinnedApp { DisplayName = "微信", ExecutablePath = @"D:\App\Weixin\Weixin.exe" };
    Assert(DockQuitRules.CanOfferQuit(new DockItem("exe:weixin", "微信", wechatPin, null)),
        "固定在 Dock 的普通应用即使收进托盘也应提供结束后台应用入口");

    var dingTalkPin = new DockPinnedApp { DisplayName = "钉钉", ExecutablePath = @"D:\App\GRP\DingtalkLauncher.exe" };
    var emptySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    Assert(DockQuitRules.MatchesProcess(dingTalkPin, emptySet, emptySet, @"D:\App\GRP\main\current\GRP.exe", null),
        "启动器固定项应能匹配子目录中的真实进程");
    Assert(!DockQuitRules.MatchesProcess(dingTalkPin, emptySet, emptySet, @"C:\Windows\explorer.exe", null),
        "保护列表中的进程无论如何不得被匹配");

    var uwpPin = new DockPinnedApp { DisplayName = "ChatGPT", AppUserModelId = "OpenAI.Codex_2p2nqsd0c76g0!App" };
    Assert(DockQuitRules.CanOfferQuit(new DockItem("aumid:codex", "ChatGPT", uwpPin, null)),
        "普通商店/UWP 应用应允许提供结束应用入口");
    Assert(DockQuitRules.MatchesProcess(uwpPin, emptySet, emptySet, @"C:\Program Files\WindowsApps\OpenAI\Codex.exe", "OpenAI.Codex_2p2nqsd0c76g0!App"),
        "商店应用应按 AUMID 匹配真实进程而非宿主");
    Assert(!DockQuitRules.MatchesProcess(uwpPin, emptySet, emptySet, @"C:\Windows\System32\ApplicationFrameHost.exe", "OpenAI.Codex_2p2nqsd0c76g0!App"),
        "不得匹配 ApplicationFrameHost.exe 共享宿主");

    var edgePin = new DockPinnedApp { DisplayName = "Edge", ExecutablePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" };
    var pwaPin = new DockPinnedApp
    {
        DisplayName = "Notion PWA",
        ExecutablePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        Arguments = "--profile-directory=Default --app-id=abcdef"
    };
    var edgeItem = new DockItem("exe:msedge", "Edge", edgePin, null);
    var pwaItem = new DockItem("aumid:MSEdge._crx_abcdef", "Notion PWA", pwaPin, null);
    Assert(!DockQuitRules.CanOfferQuit(pwaItem),
        "浏览器 PWA 共享宿主进程，不得提供进程级结束应用以免误杀浏览器");
    Assert(!DockQuitRules.CanOfferQuit(edgeItem, [edgeItem, pwaItem]),
        "同一可执行文件被多个 Dock 项共享时不得按进程名全杀");
    Assert(DockQuitRules.CanOfferQuit(edgeItem, [edgeItem]),
        "没有其他共享项时普通浏览器项仍允许结束应用");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message}。期望：{expected}；实际：{actual}");
    }
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}
