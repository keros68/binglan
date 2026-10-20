using System.Reflection;

internal static class TrayOverflowExperimentTests
{
    internal static void Run()
    {
        // Controlled pending work verifies the gates without issuing a native
        // toggle. Actual Explorer behavior is checked in the opt-in preview.
        using var experiment = new TrayOverflowExperiment();
        var type = typeof(TrayOverflowExperiment);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callField = type.GetField("_uiaCall", flags)!;
        var claimField = type.GetField("_claimUntil", flags)!;
        var stoppingField = type.GetField("_stopping", flags)!;
        var claimed = type.GetProperty("Claimed", flags)!;
        var invoke = type.GetMethod("InvokeAsync", flags)!;
        callField.SetValue(experiment, pending.Task);

        foreach (var expectedVisible in new[] { false, true })
        {
            var request = (Task<string>)invoke.Invoke(experiment, [expectedVisible, CancellationToken.None])!;
            Assert(request.IsCompletedSuccessfully && request.Result == "pending",
                "调用未返回时，新请求应立即报告 pending，不排队执行反向切换");
            Assert(ReferenceEquals(callField.GetValue(experiment), pending.Task), "新请求不能替换未完成调用");
        }

        claimField.SetValue(experiment, Environment.TickCount64 - 1);
        stoppingField.SetValue(experiment, true);
        Assert((bool)claimed.GetValue(experiment)!, "退出期间尚未返回的调用仍应保留迟到浮层归属");
        claimField.SetValue(experiment, 0L);
        Assert(!(bool)claimed.GetValue(experiment)!, "尚未进入 Invoke 的任务不能认领用户打开的浮层");
        claimField.SetValue(experiment, Environment.TickCount64 - 1);
        pending.SetResult("invoked");
        Assert(!(bool)claimed.GetValue(experiment)!, "调用结束且认领期限已过时应释放归属");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
