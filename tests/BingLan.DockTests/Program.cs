using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Services;

var failures = new List<string>();
Run("Dock 四边几何与负坐标", TestDockGeometry);
Run("Dock 非法厚度", TestInvalidThickness);
Run("断开的显示器位置恢复", TestPlacementRecovery);
Run("卡片让出任务栏与 Dock 占用的屏幕边缘", TestKeepOutOfReservedEdges);
Run("组件拖动吸附与参考线", TestSnapRules);
Run("窗口身份优先级", TestIdentityPriority);
Run("同应用多窗口稳定分组", TestStableGrouping);
Run("空标题窗口显示名回退", TestUntitledWindowDisplayName);
Run("运行应用按首次出现顺序保持稳定", TestFirstSeenOrder);
Run("Dock 点击动作决策", TestWindowActionPolicy);
Run("Dock 内容尺寸边界", TestContentLengthBounds);
Run("Dock 图标大小决定按钮与高度并被钳制", TestDockIconSize);
Run("Dock 距底部高度默认等于原固定间距并被钳制", TestDockBottomGap);
Run("Dock 隐藏把手几何：默认角位、钳制与左右展开锚定", TestDockHandleGeometry);
Run("Dock 隐藏把手位置状态规范化", TestDockHandleState);
Run("应用闪烁提醒在激活或关闭窗口后清除", TestDockAttention);
Run("底部与顶部水平居中几何", TestHorizontalCentering);
Run("左右垂直几何", TestVerticalGeometry);
Run("固定/运行/活动/分组状态映射", TestDockItemStateMapping);
Run("AppBar 预留、释放与重建路径", TestAppBarReservation);
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
Run("Dock 状态保存与加载往返保留顺序与取值", TestDockStateRoundTrip);
Run("重复固定项保存后应去重", TestDockStateDuplicatePinsDeduped);
Run("隐藏的应用不出现在 Dock 且可恢复", TestDockHiddenApps);

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
        failures.Add($"失败：{name} - {exception.Message}");
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
    Assert(attention.Flashed(101), "闪烁应记录提醒");
    Assert(!attention.Flashed(101), "重复闪烁不应重复记录");
    Assert(attention.Wants(chat) && !attention.Wants(browser), "只有闪烁窗口所属的应用有提醒");
    Assert(!attention.Flashed(0), "空窗口句柄不应记录");
    Assert(attention.Cleared(101) && !attention.Wants(chat), "激活窗口后提醒应清除");
    attention.Flashed(101);
    attention.Flashed(202);
    attention.Retain([202]);
    Assert(!attention.Wants(chat) && attention.Wants(browser), "已关闭的窗口不应保留提醒");
}

static void TestDockIconSize()
{
    AssertEqual(DockLayoutMetrics.ItemCellDip, DockLayoutMetrics.ItemCell(DockState.DefaultIconSize), "默认图标大小应保持原单元宽度");
    AssertEqual(DockLayoutMetrics.DockThicknessDip, DockLayoutMetrics.Thickness(DockState.DefaultIconSize), "默认图标大小应保持原高度");
    AssertEqual(88d, DockLayoutMetrics.ItemCell(64), "64 图标的单元宽度");
    AssertEqual(100d, DockLayoutMetrics.Thickness(64), "64 图标的 Dock 高度");
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
        DockVacateRules.IsTrueFullScreen(true, false),
        "几何铺满且未最大化应视为真全屏");
    Assert(
        !DockVacateRules.IsTrueFullScreen(true, true),
        "最大化铺满（如任务栏自动隐藏时）不应视为真全屏");
    Assert(
        !DockVacateRules.IsTrueFullScreen(false, true),
        "未铺满显示器不是全屏");
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
        WindowActionPolicy.Decide(groupContainsForeground: true, targetIsMinimized: false),
        "点击当前前台应用应最小化");
    AssertEqual(
        DockClickAction.Activate,
        WindowActionPolicy.Decide(groupContainsForeground: false, targetIsMinimized: false),
        "点击后台可见应用应激活");
    AssertEqual(
        DockClickAction.RestoreAndActivate,
        WindowActionPolicy.Decide(groupContainsForeground: false, targetIsMinimized: true),
        "点击最小化应用应恢复并激活");
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
    var changed = state.Update(new DockAutoHideInput(false, false, false, false, false), now);
    Assert(!changed, "无重叠时不应触发状态变化");
    Assert(state.IsShown, "无重叠时应保持显示");
}

static void TestDockAutoHideDelay()
{
    var state = new DockAutoHideState();
    var t0 = DateTimeOffset.UtcNow;
    var overlapInput = new DockAutoHideInput(true, false, false, false, false);

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
    var overlapInput = new DockAutoHideInput(true, false, false, false, false);
    state.Update(overlapInput, t0);

    var pointerInZone = t0 + TimeSpan.FromMilliseconds(300);
    Assert(
        !state.Update(new DockAutoHideInput(true, false, true, false, false), pointerInZone),
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
    state.Update(new DockAutoHideInput(true, false, false, false, false), t0);
    state.Update(new DockAutoHideInput(true, false, false, false, false), t0 + DockAutoHideState.HideDelay);
    Assert(!state.IsShown, "前置条件：延迟到达后应已隐藏");

    var t1 = t0 + DockAutoHideState.HideDelay + TimeSpan.FromSeconds(1);
    Assert(
        state.Update(new DockAutoHideInput(false, false, false, true, false), t1),
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
}

static void TestDockAutoHideFullScreenHidesImmediately()
{
    var state = new DockAutoHideState();
    Assert(
        state.Update(new DockAutoHideInput(false, true, true, false, false), DateTimeOffset.UtcNow),
        "全屏前台窗口即使指针在唤出区域也应立即隐藏");
    Assert(!state.IsShown, "全屏前台窗口应隐藏 Dock");
}

static void TestDockAutoHideFullScreenOverridesInteraction()
{
    var state = new DockAutoHideState();
    var t0 = DateTimeOffset.UtcNow;
    Assert(
        state.Update(new DockAutoHideInput(false, true, false, true, true), t0),
        "菜单或拖动进行中出现全屏窗口时也应隐藏 Dock");
    Assert(!state.IsShown, "全屏窗口在前台时 Dock 应保持隐藏");

    Assert(
        state.Update(new DockAutoHideInput(false, false, false, false, true), t0 + TimeSpan.FromSeconds(1)),
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

static void TestDockStateRoundTrip()
{
    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-dock-roundtrip-{Guid.NewGuid():N}");
    try
    {
        var store = new LocalStateStore(temp);
        var state = LocalStateStore.CreateDefault();
        state.Dock.IsEnabled = true;
        state.Dock.MonitorDeviceName = @"\\.\DISPLAY1";
        state.Dock.VisibilityMode = DockVisibilityMode.SmartHide;
        state.Dock.BottomGapDip = 52;
        state.Dock.ReleaseWhenMaximized = true;
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

        Assert(loaded.Dock.IsEnabled, "Dock 启用状态未保留");
        AssertEqual(@"\\.\DISPLAY1", loaded.Dock.MonitorDeviceName, "显示器名称未保留");
        AssertEqual(DockVisibilityMode.SmartHide, loaded.Dock.VisibilityMode, "可见性模式未保留");
        AssertEqual(52d, loaded.Dock.BottomGapDip, "距底高度未保留");
        Assert(loaded.Dock.ReleaseWhenMaximized, "最大化让出开关未保留");
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
