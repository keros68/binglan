using BingLan.App.Taskbar;
using BingLan.App.Interop;
using BingLan.App;
using BingLan.App.Tray;
using BingLan.App.Services;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

internal static class TrayOverflowExperimentTests
{
    internal static int ReadLiveSnapshot()
    {
        var snapshot = TrayReader.RefreshSnapshotAsync().WaitAsync(TrayReader.OperationTimeout).GetAwaiter().GetResult();
        if (snapshot is null)
        {
            Console.Error.WriteLine("Tray snapshot unavailable.");
            return 1;
        }
        var taskbar = snapshot.Where(entry => entry.Source == "taskbar").ToArray();
        var msaa = taskbar.Count(entry => entry.Identity.RuntimeId.StartsWith("msaa.", StringComparison.Ordinal));
        var clash = taskbar.FirstOrDefault(entry => TrayIconArtwork.IsClashVergeTooltip(entry.Name));
        var weixin = taskbar.Any(entry => entry.Name.Trim().StartsWith("微信", StringComparison.Ordinal));
        Console.WriteLine($"Taskbar={taskbar.Length}; Overflow={snapshot.Length - taskbar.Length}; "
            + $"MSAA={msaa}; Clash={clash is not null}; ClashDirect={clash?.ActivationOwner is not null}; Weixin={weixin}");
        return 0;
    }

    // Opt-in integration test against the user's running Clash Verge with its default tray action.
    internal static int ClashCloseToTray()
    {
        const uint wmClose = 0x0010;
        var entries = TrayReader.RefreshSnapshotAsync().GetAwaiter().GetResult();
        var entry = entries?.SingleOrDefault(item => TrayIconArtwork.IsClashVergeTooltip(item.Name));
        if (entry?.ActivationOwner is not { } owner
            || !TrayPanelWindow.UsesClashVergeMainWindowAction(owner.ExecutablePath)
            || TrayPanelWindow.FindClashVergeMainWindow(owner.ProcessId) is not { } window
            || DockNativeMethods.IsWindowVisible(window))
        {
            Console.Error.WriteLine("请先将使用默认托盘动作的 Clash Verge 2.5.10 主窗口收回托盘。");
            return 1;
        }

        using var app = System.Diagnostics.Process.GetProcessById((int)owner.ProcessId);
        for (var round = 1; round <= 3; round++)
        {
            var result = TrayReader.InvokeAsync(entry.Identity, activateApp: TrayPanelWindow.ActivateTrayAppAsync,
                displayedEntry: entry).GetAwaiter().GetResult();
            Assert(result == "invoked" && SpinWait.SpinUntil(() => DockNativeMethods.IsWindowVisible(window),
                TimeSpan.FromSeconds(3)), "托盘点击应显示同一 Clash 窗口");
            Assert(TrayPanelWindow.FindClashVergeMainWindow(owner.ProcessId) == window
                && DockNativeMethods.PostMessageW(window, wmClose, 0, 0), "向该窗口请求关闭");
            Assert(SpinWait.SpinUntil(() => !DockNativeMethods.IsWindowVisible(window),
                TimeSpan.FromSeconds(3)) && !app.HasExited, "关闭后主窗口应隐藏，原进程应继续运行");
            // Clash 2.5.10 coalesces show requests within 625 ms. Observe the hidden window through that interval.
            Assert(!SpinWait.SpinUntil(() => DockNativeMethods.IsWindowVisible(window), TimeSpan.FromMilliseconds(750)),
                "收回托盘后主窗口应保持隐藏");
            Console.WriteLine($"通过：第 {round} 次打开 Clash 并收回托盘");
        }
        return 0;
    }

    internal static void Run(Action pump)
    {
        ActionLifetime();
        Assert(TrayPanelWindow.IsFeishuMainWindow("飞书", "Chrome_WidgetWin_1"), "飞书主窗口应匹配实测标题和窗口类");
        Assert(!TrayPanelWindow.IsFeishuMainWindow("飞书会议", "Chrome_WidgetWin_1"), "独立会议窗口应保留 Explorer 默认动作");
        Assert(!TrayPanelWindow.IsFeishuMainWindow("文档 - 飞书", "Chrome_WidgetWin_1"), "文档窗口应保留 Explorer 默认动作");
        Assert(!TrayPanelWindow.IsFeishuMainWindow("飞书", "未知窗口类"), "未知窗口类应保留 Explorer 默认动作");
        Assert(TrayIconArtwork.IsClashVergeTooltip("Clash Verge 2.5.10\n系统代理: 开启"), "Clash Verge 托盘提示应按首行确认版本");
        Assert(!TrayIconArtwork.IsClashVergeTooltip("Clash Verge 2.5.100"), "未核验的 Clash Verge 构建应保留 Explorer 默认动作");
        Assert(TrayPanelWindow.IsClashVergeMainWindow("Clash Verge", "Tauri Window"), "Clash Verge 应匹配已核验的主窗口");
        Assert(!TrayPanelWindow.IsClashVergeMainWindow("Clash Verge", "tray_icon_app"), "托盘回调窗口不能当成主窗口");
        foreach (var setting in new[] { "tray_event: null", "tray_event: main_window" })
        {
            using var config = new StringReader($"# Clash Verge Config\n{setting}\n");
            Assert(TrayPanelWindow.ReadsClashVergeMainWindowAction(config), "主窗口设置应允许直接切换");
        }
        foreach (var setting in new[] { "tray_event: tray_menu", "tray_event: system_proxy", "tray_event: tun_mode",
            "tray_event: disable", "tray_event: ~", "config:\n  tray_event: null", "", "tray_event: null\ntray_event: tray_menu" })
        {
            using var config = new StringReader(setting);
            Assert(!TrayPanelWindow.ReadsClashVergeMainWindowAction(config), "自定义或未知点击行为应交给原生托盘");
        }

        // Controlled pending work verifies the single-flight gates without issuing a native
        // toggle. Actual Explorer behavior is checked in the opt-in preview.
        using var experiment = new TrayOverflowController();
        var type = typeof(TrayOverflowController);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callField = type.GetField("_uiaCall", flags)!;
        var invoke = type.GetMethod("InvokeAsync", flags)!;
        callField.SetValue(experiment, pending.Task);
        Assert(experiment.OpenAsync().GetAwaiter().GetResult() == "pending", "未返回期间不能再次发出通知区域快捷键");

        foreach (var expectedVisible in new[] { false, true })
        {
            var request = (Task<string>)invoke.Invoke(experiment, [expectedVisible, CancellationToken.None])!;
            Assert(request.IsCompletedSuccessfully && request.Result == "pending",
                "调用未返回时，新请求应立即报告 pending，不排队执行反向切换");
            Assert(ReferenceEquals(callField.GetValue(experiment), pending.Task), "新请求不能替换未完成调用");
        }
        pending.SetResult("invoked");

        using (var toggle = new TrayOverflowController())
        {
            Assert(!toggle.OwnsPanel, "新控制器没有面板会话");
            var ownsField = type.GetField("_ownsPanel", flags)!;
            ownsField.SetValue(toggle, true);
            if (!TrayOverflowController.IsVisible)
            {
                Assert(!toggle.OwnsPanel, "原生面板未显示时控制器不应继续声称归属");
                Assert(toggle.CloseOwnedPanelAsync().GetAwaiter().GetResult(), "面板已消失时收起操作应幂等成功");
                Assert(!(bool)ownsField.GetValue(toggle)!, "幂等收起后应清除内部归属标记");
            }
        }

        var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BingLan-tray-lifetime-" + Guid.NewGuid().ToString("N")));
        try
        {
            using var owner = new WidgetCoordinator(interactiveQa: true, dataDirectory: directory);
            var controller = new TrayOverflowController();
            var cleanupCall = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            callField.SetValue(controller, cleanupCall.Task);
            typeof(WidgetCoordinator).GetField("_trayOverflow", flags)!.SetValue(owner, controller);
            var first = owner.CloseTrayOverflowAsync();
            Assert(ReferenceEquals(first, owner.CloseTrayOverflowAsync()), "模块关闭与退出应复用同一次清理");
            pump();
            Assert(controller.IsStopping, "宿主清理应先取消唤出");
            cleanupCall.SetResult("cancelled");
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!first.IsCompleted && DateTime.UtcNow < deadline) pump();
            Assert(first.IsCompletedSuccessfully && first.Result == "closed", "未发出按钮调用的任务返回后应完成宿主清理");
            Assert(typeof(WidgetCoordinator).GetField("_trayOverflow", flags)!.GetValue(owner) is null,
                "成功清理后释放控制器，以便重新开启模块");
        }
        finally
        {
            if (directory.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    internal static void ActionLifetime()
    {
        AllTrayIconsVisible();
        ContextMenuPlacement();
        TrayContextOwnerMatchingTests.Run();
        TraySnapshotRecoveryTests.Run();
        VerifyTaskbarCreatedDefersRegistration();
        foreach (var error in new[] { 0x80004005u, 0x80070057u, 0x80004001u, 0x80070005u })
        {
            var exception = Marshal.GetExceptionForHR(unchecked((int)error));
            Assert(exception is not null && TrayAccessibleIcons.IsRecoverableAccessibilityFailure(exception),
                $"托盘辅助功能提供程序错误 0x{error:X8} 应安静降级");
        }
        Assert(!TrayAccessibleIcons.IsRecoverableAccessibilityFailure(new OperationCanceledException()),
            "取消仍应按操作取消路径处理");
        Task.Run(() => AssertActionRetainsWaitHandleAsync(cancel: true)).GetAwaiter().GetResult();
        Task.Run(() => AssertActionRetainsWaitHandleAsync(cancel: false)).GetAwaiter().GetResult();
    }

    private static void ContextMenuPlacement()
    {
        var screen = new System.Windows.Rect(0, 55, 3840, 2105);
        var tile = new System.Windows.Rect(3310, 232, 67, 67);
        var menu = new System.Windows.Size(369, 441);
        var below = TrayContextMenuPlacement.Place(tile, menu, screen, 7);
        Assert(below == new System.Windows.Point(3310, 306), "175% 下菜单应紧邻来源图标下沿，且不覆盖图标");
        var bottom = new System.Windows.Rect(3310, 2000, 67, 67);
        Assert(TrayContextMenuPlacement.Place(bottom, menu, screen, 7) == new System.Windows.Point(3310, 1552),
            "下方空间不足时应紧邻图标上沿");
        var leftMonitor = new System.Windows.Rect(-1920, -1200, 1920, 1160);
        Assert(TrayContextMenuPlacement.Place(new System.Windows.Rect(-30, -1150, 28, 28),
            new System.Windows.Size(300, 400), leftMonitor, 4) == new System.Windows.Point(-300, -1118),
            "负坐标显示器应按同屏工作区钳制，不移到主屏");
        Assert(TrayContextMenuPlacement.Place(tile, new System.Windows.Size(5000, 3000), screen, 7)
            == new System.Windows.Point(0, 55), "菜单超出工作区时应保留可见的左上角");
        var shortScreen = new System.Windows.Rect(0, 0, 1000, 600);
        var tallMenu = new System.Windows.Size(200, 550);
        var upperTile = new System.Windows.Rect(400, 100, 40, 40);
        var beside = TrayContextMenuPlacement.Place(upperTile, tallMenu, shortScreen, 4);
        Assert(beside == new System.Windows.Point(444, 50),
            $"上下均不足时应选择相邻侧面而非覆盖图标，实际 {beside}");
        Assert(TrayContextMenuPlacement.Place(new System.Windows.Rect(850, 100, 40, 40), tallMenu, shortScreen, 4)
            == new System.Windows.Point(646, 50), "右侧不足时应在左侧避开图标");
        Assert(TrayContextMenuPlacement.Place(new System.Windows.Rect(400, 300, 40, 40),
            new System.Windows.Size(950, 300), shortScreen, 4) == new System.Windows.Point(50, 0),
            "四面均不足时应选覆盖图标最少的可见位置");
        Assert(TrayContextMenuPlacement.Place(new System.Windows.Rect(-600, -500, 40, 40), tallMenu,
            new System.Windows.Rect(-1000, -600, 1000, 600), 4) == new System.Windows.Point(-556, -550),
            "侧向避让应保持负坐标显示器工作区");
        var owner = new TrayContextMenuOwner(0, 0);
        var calls = 0;
        var result = TrayContextMenuPlacement.InvokeAsync(owner, new TrayMenuAnchor(tile, 1.75), () =>
            { calls++; return "context-requested"; }, CancellationToken.None).GetAwaiter().GetResult();
        Assert(result == "context-unconfirmed" && calls == 1, "无法确认菜单归属时只发出一次请求，并提供系统托盘入口");
        Assert(TrayContextMenuPlacement.Place(new System.Windows.Rect(200, 400, 50, 50), menu, screen, 7, preferAbove: true)
            == new System.Windows.Point(200, 457), "上方空间不足时底栏菜单应能向下避让");
        Assert(TrayContextMenuPlacement.Place(new System.Windows.Rect(200, 800, 50, 50), menu, screen, 7, preferAbove: true)
            == new System.Windows.Point(200, 352), "上下均有空间时底栏菜单优先向上");
        Assert(TrayContextMenuPlacement.IsNativeMenuPosition(tile, new Rect(3000, 250, 350, 400), screen)
            && TrayContextMenuPlacement.IsNativeMenuPosition(tile, new Rect(3350, 250, 350, 400), screen)
            && TrayContextMenuPlacement.IsNativeMenuPosition(bottom, new Rect(3000, 1625, 350, 400), screen),
            "无法移动的应用菜单可保留整体可见且角点落在来源图标内的原生位置");
        Assert(!TrayContextMenuPlacement.IsNativeMenuPosition(tile, new Rect(3000, 100, 600, 600), screen)
            && !TrayContextMenuPlacement.IsNativeMenuPosition(tile, new Rect(3400, 250, 350, 400), screen)
            && !TrayContextMenuPlacement.IsNativeMenuPosition(tile, new Rect(-100, 250, 3450, 400), screen)
            && !TrayContextMenuPlacement.IsNativeMenuPosition(tile, Rect.Empty, screen),
            "部分遮住图标、远离图标、超出工作区及空菜单不得当成可用原生位置");
        Assert(TrayContextMenuPlacement.IsQtPopup("Qt6120QWindowPopupSaveBits")
            && TrayContextMenuPlacement.IsQtPopup("Qt51514QWindowPopupSaveBits")
            && !TrayContextMenuPlacement.IsQtPopup("Qt6120QWindowIcon")
            && !TrayContextMenuPlacement.IsQtPopup("QtQWindowPopupSaveBits")
            && !TrayContextMenuPlacement.IsQtPopup("Qt51514QWindowToolTipSaveBits"),
            "Qt 菜单识别应排除主窗口、提示窗口和未知类名");
        Assert(TrayContextMenuPlacement.IsKnownAppPopup("OneDriveReactNativeWin32WindowClass", @"C:\Apps\OneDrive.exe")
            && TrayContextMenuPlacement.IsKnownAppPopup("LenovoTrayMenuV2", @"C:\Apps\LENOVOTRAY.EXE")
            && TrayContextMenuPlacement.IsKnownAppPopup("MenuWnd", @"C:\Apps\cloudmusic.exe"),
            "已核验专用托盘窗口应匹配精确类名及实际程序文件名");
        foreach (var (className, path) in new[]
        {
            ("OneDriveReactNativeWin32WindowClass", @"C:\Apps\Other.exe"),
            ("LenovoTrayMenuV2", @"C:\Apps\OneDrive.exe"),
            ("LenovoTrayMenuV2", @"C:\Apps\LenovoTrayHelper.exe"),
            ("PerryShadowWnd", @"C:\Apps\LenovoTray.exe"),
            ("OneDriveReactNativeWin32WindowClassOther", @"C:\Apps\OneDrive.exe"),
            ("Chrome_WidgetWin_1", @"C:\Apps\OneDrive.exe"),
            ("MenuWnd", @"C:\Apps\Other.exe"),
            ("MenuWnd", @"C:\Apps\cloudmusic-helper.exe"),
            ("MenuWndOther", @"C:\Apps\cloudmusic.exe"),
            ("LenovoTrayMenuV2", "")
        })
            Assert(!TrayContextMenuPlacement.IsKnownAppPopup(className, path),
                "普通窗口、阴影、不同程序与相似名称均不得被认作已核验托盘菜单");
        Assert(TrayContextMenuPlacement.MatchesKnownAppPopupOwner("MenuWnd", 101, 101)
            && !TrayContextMenuPlacement.MatchesKnownAppPopupOwner("MenuWnd", 101, 102)
            && !TrayContextMenuPlacement.MatchesKnownAppPopupOwner("MenuWnd", 101, 0)
            && !TrayContextMenuPlacement.MatchesKnownAppPopupOwner("MenuWnd", 0, 0),
            "云音乐通用菜单类必须具有属于同一已注册进程的窗口所有者");
        var systemOwner = new TrayContextMenuOwner((nint)100, 101, StandardMenusOnly: true);
        Assert(TrayContextMenuPlacement.AllowsMenuClass(systemOwner, "#32768")
            && TrayContextMenuPlacement.MatchesStandardMenuOwner(systemOwner, (nint)100)
            && !TrayContextMenuPlacement.MatchesStandardMenuOwner(systemOwner, (nint)200)
            && !TrayContextMenuPlacement.MatchesStandardMenuOwner(systemOwner, 0)
            && !TrayContextMenuPlacement.MatchesStandardMenuOwner(owner, 0),
            "系统托盘菜单必须属于已确认的回调窗口，不能仅凭同属 Explorer 进程确认");
        foreach (var className in new[] { "Qt6120QWindowPopupSaveBits", "OneDriveReactNativeWin32WindowClass",
            "LenovoTrayMenuV2", "MenuWnd", "PerryShadowWnd", "Unknown" })
            Assert(!TrayContextMenuPlacement.AllowsMenuClass(systemOwner, className),
                "系统蓝牙只允许标准菜单，不得沿用同进程的 Qt 或应用专用弹窗");
        Assert(TrayContextMenuPlacement.AllowsMenuClass(owner, "#32768")
            && TrayContextMenuPlacement.AllowsMenuClass(owner, "Qt6120QWindowPopupSaveBits")
            && TrayContextMenuPlacement.AllowsMenuClass(owner, "MenuWnd")
            && !TrayContextMenuPlacement.AllowsMenuClass(owner, "Unknown"),
            "正常注册仍保留已核验的标准菜单与应用弹窗路径");
        Assert(TrayContextMenuPlacement.IsObservationPopup("Chrome_WidgetWin_1", 0x96000000, 0x200088),
            "只观察路径应匹配已实测 Chrome 前台透明工具弹窗样式");
        foreach (var (className, style, extendedStyle) in new (string, long, long)[]
        {
            ("Chrome_WidgetWin_1", 0x16CF0000, 0x40000),
            ("Chrome_WidgetWin_1", 0x96CF0000, 0x200088),
            ("Chrome_WidgetWin_1", 0xD6000000, 0x200088),
            ("Chrome_WidgetWin_1", 0x96000000, 0x200008),
            ("Chrome_WidgetWin_1", 0x96000000, 0x88),
            ("Chrome_WidgetWin_1", 0x96000000, 0x200080),
            ("Chrome_WidgetWin_1", 0x96000000, 0x240088),
            ("Chrome_WidgetWin_2", 0x96000000, 0x200088)
        })
            Assert(!TrayContextMenuPlacement.IsObservationPopup(className, style, extendedStyle),
                "普通文档、主窗口、子窗口及缺少已核验透明工具弹窗样式的窗口不得确认");
        var observedTile = new Rect(2062, 142, 57, 57);
        var observedPopup = new Rect(1271, 152, 856, 829);
        var observedWorkArea = new Rect(0, 40, 2560, 1360);
        Assert(!TrayContextMenuPlacement.IsNativeMenuPosition(observedTile, observedPopup, observedWorkArea)
            && TrayContextMenuPlacement.IsObservationMenuPosition(observedTile, observedPopup, observedWorkArea, 1.5),
            "ChatGPT 实测透明阴影可使 HWND 角点离图标 8px，8DIP 上限内仍可确认贴源");
        Assert(!TrayContextMenuPlacement.IsObservationMenuPosition(observedTile,
                new Rect(1271, 152, 870, 829), observedWorkArea, 1.5)
            && !TrayContextMenuPlacement.IsObservationMenuPosition(observedTile,
                new Rect(1200, 100, 1000, 1000), observedWorkArea, 1.5)
            && !TrayContextMenuPlacement.IsObservationMenuPosition(observedTile,
                new Rect(-20, 152, 2147, 829), observedWorkArea, 1.5)
            && !TrayContextMenuPlacement.IsObservationMenuPosition(Rect.Empty, observedPopup, observedWorkArea, 1.5)
            && !TrayContextMenuPlacement.IsObservationMenuPosition(observedTile, observedPopup, observedWorkArea, double.NaN),
            "观察路径必须拒绝超过阴影容差、仅遮挡图标、越屏、空锚点及无效缩放");
        var unverifiedCalls = 0;
        var unverifiedObservation = new TrayContextMenuOwner(0, 0,
            ObservationExecutablePath: @"C:\Apps\ChatGPT.exe", ObservationIconGuid: Guid.NewGuid());
        Assert(TrayContextMenuPlacement.InvokeAsync(unverifiedObservation, new TrayMenuAnchor(observedTile, 1.5),
                () => { unverifiedCalls++; return "context-requested"; }, CancellationToken.None).GetAwaiter().GetResult()
            == "context-unconfirmed" && unverifiedCalls == 1,
            "没有当前严格观察身份的 GUID 不得被当成回调窗口或已确认菜单");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledCall = TrayContextMenuPlacement.InvokeAsync(owner, new TrayMenuAnchor(tile, 1.75), () =>
            { calls++; return "context-requested"; }, canceled.Token);
        Assert(canceledCall.IsCanceled && calls == 1, "调用前已取消时，不得继续发出原生菜单请求");
    }

    private static void AllTrayIconsVisible()
    {
        var nativeOpens = 0;
        var panel = new TrayPanelWindow(anchored: true) { OpenWindowsTray = () => nativeOpens++ };
        try
        {
            var entries = Enumerable.Range(1, 8).Select(i => new TrayPanelEntry(
                new TrayIdentity(1, i.ToString()), $"应用 {i}", i <= 5 ? "taskbar" : "existing-overflow", null)).ToArray();
            panel.ShowEntries(null);
            Assert(panel.NativeTrayLink.Visibility == Visibility.Visible && panel.Status.Text.Contains("无法读取")
                && nativeOpens == 0, "首次读取失败时应显示恢复入口，等待用户操作");
            panel.ShowEntries([]);
            Assert(panel.NativeTrayLink.Visibility == Visibility.Visible && panel.Status.Visibility == Visibility.Visible
                && nativeOpens == 0, "空列表应提供手动打开 Windows 托盘的入口");
            panel.NativeTrayLink.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(nativeOpens == 1, "手动点击应只打开一次 Windows 托盘");
            panel.ShowEntries(entries[..5]);
            panel.ShowEntries(entries);
            Assert(panel.NativeTrayLink.Visibility == Visibility.Collapsed && panel.Status.Visibility == Visibility.Collapsed,
                "读到图标后应收起空状态提示和系统入口");
            panel.ShowEntries(null);
            Assert(panel.NativeTrayLink.Visibility == Visibility.Visible && panel.Entries.Children.Count == entries.Length,
                "刷新暂时失败时应保留已显示的图标并提供恢复入口");
            panel.ShowEntries(entries);
            Assert(panel.NativeTrayLink.Visibility == Visibility.Collapsed && panel.Status.Visibility == Visibility.Collapsed,
                "重新读到相同图标也应收起错误提示");
            var buttons = panel.Entries.Children.Cast<Button>().ToArray();
            Assert(buttons.Length == entries.Length && buttons.All(button => button.Visibility == Visibility.Visible)
                && buttons.Select(AutomationProperties.GetName).SequenceEqual(entries.Select(entry => entry.Name)),
                "托盘应按快照顺序直接显示常显区和折叠区的全部图标，每个图标对应一个按钮");
        }
        finally { panel.Close(); }
    }

    private static void VerifyTaskbarCreatedDefersRegistration()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var calls = 0;
        var pending = TrayIconHost.QueueTaskbarRestore(dispatcher, null, () => calls++);
        var duplicate = TrayIconHost.QueueTaskbarRestore(dispatcher, pending, () => calls++);
        Assert(calls == 0 && pending.Status == DispatcherOperationStatus.Pending && ReferenceEquals(pending, duplicate),
            "TaskbarCreated 回调应先返回，同轮广播只排一次托盘恢复");
        dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert(calls == 1, "调度器继续处理消息后应执行一次托盘恢复");

        var canceled = TrayIconHost.QueueTaskbarRestore(dispatcher, pending, () => calls++);
        Assert(!ReferenceEquals(canceled, pending) && canceled.Abort(), "已完成的恢复不应阻止后续恢复，待执行的恢复可取消");
        dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert(calls == 1, "取消之后不能再次进入托盘恢复");
    }

    private static async Task AssertActionRetainsWaitHandleAsync(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkedHandle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var testTimeout = TimeSpan.FromSeconds(5);
        var wait = TrayReader.RunActionAsync(async token =>
        {
            started.SetResult();
            await release.Task.WaitAsync(testTimeout);
            try
            {
                var handle = token.WaitHandle;
                checkedHandle.SetResult(!handle.SafeWaitHandle.IsClosed && handle.WaitOne(0));
            }
            catch (Exception exception)
            {
                checkedHandle.SetException(exception);
            }
            return "invoked";
        }, cancellation.Token, cancel ? testTimeout : TimeSpan.FromMilliseconds(150));

        try
        {
            await started.Task.WaitAsync(testTimeout);
            if (cancel) cancellation.Cancel();
            try
            {
                await wait;
                throw new InvalidOperationException("关闭面板或超时应结束外层等待");
            }
            catch (OperationCanceledException) { }

            var busy = await TrayReader.RunActionAsync(_ => Task.FromResult("unexpected-action"),
                CancellationToken.None, testTimeout);
            Assert(busy == "action-in-progress", "后台托盘操作结束前应拒绝新动作");
        }
        finally
        {
            release.TrySetResult();
        }

        Assert(await checkedHandle.Task.WaitAsync(testTimeout), "外层等待结束后，后台操作仍应能读取已触发的取消句柄");
        var deadline = Environment.TickCount64 + (long)testTimeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            var next = await TrayReader.RunActionAsync(_ => Task.FromResult("invoked"),
                CancellationToken.None, testTimeout);
            if (next == "invoked") return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("后台托盘操作结束后应允许下一次动作");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
