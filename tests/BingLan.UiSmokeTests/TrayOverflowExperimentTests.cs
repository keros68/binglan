using BingLan.App.Taskbar;
using BingLan.App.Interop;
using BingLan.App;
using BingLan.App.Tray;
using BingLan.App.Services;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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
