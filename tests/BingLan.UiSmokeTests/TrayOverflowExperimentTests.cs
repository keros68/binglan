using BingLan.App.Taskbar;
using BingLan.App;
using System.IO;
using System.Reflection;

internal static class TrayOverflowExperimentTests
{
    internal static void Run(Action pump)
    {
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

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
