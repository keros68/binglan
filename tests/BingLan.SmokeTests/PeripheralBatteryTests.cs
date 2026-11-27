using System.Text.Json;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.TopBar;

/// <summary>BatteryHub CLI parsing, cancellation, shared refresh cadence and stale data.</summary>
internal static class PeripheralBatteryTests
{
    private const string SampleJson = """
        [
          {"key":"kysona:373B:1085","name":"CX Wireless mouse","via":"2.4 GHz receiver","kind":"mouse","percent":85,"charging":false,"asleep":false,"connected":true},
          {"key":"bt:headset","name":"无线耳机","via":"Bluetooth","kind":"headset","percent":60,"charging":true,"asleep":true,"connected":true}
        ]
        """;

    internal static void RunAll()
    {
        TestParseAndNormalize();
        TestRejectMalformedDocuments();
        TestCadenceAndForce().GetAwaiter().GetResult();
        TestFailureBackoffAndStaleData().GetAwaiter().GetResult();
        TestSingleFlightAndCompletionCadence().GetAwaiter().GetResult();
        TestCancellationDoesNotPublish().GetAwaiter().GetResult();
        TestWaiterCancellationIsIndependent().GetAwaiter().GetResult();
        TestModuleDefaultsAndFormatting();
    }

    private static void TestParseAndNormalize()
    {
        var now = DateTimeOffset.UnixEpoch;
        var snapshot = PeripheralBatteryService.Parse(SampleJson, now);
        Check(snapshot.Status == PeripheralBatteryStatus.Fresh && snapshot.UpdatedAt == now,
            "合法数组应使用本次采样时间生成新鲜快照");
        Check(snapshot.Devices.Count == 2 &&
            snapshot.Devices[0] == new PeripheralDevice("kysona:373B:1085", "CX Wireless mouse",
                PeripheralDeviceKind.Mouse, "2.4 GHz receiver", 85, false, false, true),
            "公开 CLI 字段应逐项映射");
        Check(snapshot.Devices[1].Charging && snapshot.Devices[1].Asleep &&
            snapshot.Devices[1].Connected && snapshot.Devices[1].Kind == PeripheralDeviceKind.Headset,
            "充电、休眠和连接状态应保留来源值");
        Check(PeripheralBatteryService.Parse("[]", now).Devices.Count == 0,
            "空数组应作为合法成功结果");
        Check(PeripheralBatterySnapshot.Empty.Status == PeripheralBatteryStatus.NoData &&
            PeripheralBatterySnapshot.NotConfigured.Status == PeripheralBatteryStatus.NotConfigured,
            "未采样与未配置应有明确独立状态");

        var normalized = PeripheralBatteryService.Parse("""
            [
              {"key":" mouse ","name":" 鼠标 ","kind":" MOUSE ","via":" USB ","percent":101,"charging":"true","connected":true},
              {"key":"mouse","percent":30},
              {"key":"unknown","name":false,"kind":"future-kind","percent":null,"connected":1},
              {"key":"zero","percent":0},
              {"key":"full","percent":100},
              {"key":"negative","percent":-2},
              {"key":"missing"},
              {"key":"text","percent":"40"},
              {"key":"fraction","percent":50.5},
              {"key":"","name":"没有稳定标识"},
              {"name":"缺少标识"},
              {"key":42}
            ]
            """, now);
        Check(normalized.Devices.Count == 8, "空标识、错误类型标识和重复标识应被跳过");
        var first = normalized.Devices[0];
        Check(first.Key == "mouse" && first.Name == "鼠标" && first.Via == "USB" &&
            first.Kind == PeripheralDeviceKind.Mouse && first.Percent == -1 && !first.Charging,
            "文本应清洗，错误字段不能伪造有效百分比或布尔值");
        var unknown = normalized.Devices.Single(device => device.Key == "unknown");
        Check(unknown.Kind == PeripheralDeviceKind.Other && unknown.Name == "未命名设备" &&
            !unknown.Connected && unknown.Percent == -1,
            "未来种类、无名称和未知电量应使用明确回退");
        Check(normalized.Devices.Single(device => device.Key == "zero").Percent == 0 &&
            normalized.Devices.Single(device => device.Key == "full").Percent == 100,
            "0% 与 100% 都是有效值");
        Check(normalized.Devices.Where(device => device.Key is "negative" or "missing" or "text" or "fraction")
            .All(device => device.Percent == -1), "未知电量统一保留为 -1");
    }

    private static void TestRejectMalformedDocuments()
    {
        foreach (var json in new[] { "", "{", "null", "true", "42", "{}", """{"devices":[]}""", "[null]", "[3]" })
        {
            try
            {
                PeripheralBatteryService.Parse(json, DateTimeOffset.UnixEpoch);
            }
            catch (JsonException)
            {
                continue;
            }
            throw new InvalidOperationException("错误的 CLI 根结构或数组成员应被拒绝：" + json);
        }
    }

    private static async Task TestCadenceAndForce()
    {
        var time = new ManualTimeProvider();
        var calls = 0;
        var service = new PeripheralBatteryService(_ =>
        {
            calls++;
            return Task.FromResult<string?>(SampleJson);
        }, time);
        Check(service.IsAutomaticRefreshDue() && service.IsAutomaticRefreshDue(),
            "到期检查不应消费首次刷新机会");
        var fresh = await service.RefreshAsync();
        Check(calls == 1 && !service.IsAutomaticRefreshDue(), "首次成功后应进入刷新间隔");
        time.Advance(TimeSpan.FromSeconds(59));
        Check(ReferenceEquals(await service.RefreshAsync(), fresh) && calls == 1,
            "60 秒内重复请求应复用快照");
        time.Advance(TimeSpan.FromSeconds(1));
        Check(service.IsAutomaticRefreshDue(), "成功完成 60 秒后应允许自动刷新");
        await service.RefreshAsync();
        Check(calls == 2, "到期应重新读取来源");
        await service.RefreshAsync(force: true);
        Check(calls == 3 && !service.IsAutomaticRefreshDue(), "手动刷新可绕过本轮间隔");
        await service.RefreshAsync();
        Check(calls == 3, "手动刷新不能遗留下一轮放行标志");
    }

    private static async Task TestFailureBackoffAndStaleData()
    {
        var time = new ManualTimeProvider();
        string? json = null;
        var calls = 0;
        var service = new PeripheralBatteryService(_ =>
        {
            calls++;
            return Task.FromResult(json);
        }, time);
        foreach (var delay in new[] { 15, 30, 60, 60, 60 })
        {
            var unavailable = await service.RefreshAsync();
            Check(unavailable.Status == PeripheralBatteryStatus.SourceUnavailable &&
                unavailable.UpdatedAt is null && unavailable.Devices.Count == 0,
                "首次成功前失败应标为来源不可用");
            var previousCalls = calls;
            time.Advance(TimeSpan.FromSeconds(delay - 1));
            await service.RefreshAsync();
            Check(calls == previousCalls && !service.IsAutomaticRefreshDue(),
                "连续失败应遵守有上限的退避间隔");
            time.Advance(TimeSpan.FromSeconds(1));
            Check(service.IsAutomaticRefreshDue(), "退避结束后应允许再试");
        }

        json = SampleJson;
        var fresh = await service.RefreshAsync();
        var lastUpdated = fresh.UpdatedAt;
        json = """{"devices":[]}""";
        time.Advance(PeripheralBatteryService.SuccessRefreshInterval);
        var stale = await service.RefreshAsync();
        Check(stale.Status == PeripheralBatteryStatus.Stale &&
            stale.UpdatedAt == lastUpdated && ReferenceEquals(stale.Devices, fresh.Devices) &&
            !string.IsNullOrWhiteSpace(stale.ErrorMessage),
            "失败应保留最后成功读数与采样时间，并明确标记过期");
        json = "[]";
        time.Advance(PeripheralBatteryService.BaseRetryDelay);
        var recovered = await service.RefreshAsync();
        Check(recovered.Status == PeripheralBatteryStatus.Fresh && recovered.Devices.Count == 0 &&
            recovered.ErrorMessage is null && recovered.UpdatedAt > lastUpdated,
            "恢复后空数组应替换旧设备并清除错误状态");

        var throwing = new PeripheralBatteryService(_ => throw new IOException("读取失败"), time);
        Check((await throwing.RefreshAsync()).Status == PeripheralBatteryStatus.SourceUnavailable,
            "来源抛出异常应安静降级");
    }

    private static async Task TestSingleFlightAndCompletionCadence()
    {
        var time = new ManualTimeProvider();
        var output = NewOutput();
        var calls = 0;
        var service = new PeripheralBatteryService(_ =>
        {
            Interlocked.Increment(ref calls);
            return output.Task;
        }, time);
        var first = service.RefreshAsync();
        var joined = Enumerable.Range(0, 12).Select(_ => service.RefreshAsync(force: true)).ToArray();
        Check(calls == 1 && !service.IsAutomaticRefreshDue(), "并发自动与强制请求只能产生一次实际读取");
        time.Advance(TimeSpan.FromSeconds(90));
        output.SetResult(SampleJson);
        var snapshot = await first;
        await Task.WhenAll(joined);
        Check(joined.All(task => ReferenceEquals(task.Result, snapshot)),
            "并发请求应共享同一次读取结果");
        Check(snapshot.UpdatedAt == time.GetUtcNow(), "采样时间应记录读取完成时间");
        time.Advance(TimeSpan.FromSeconds(59));
        await service.RefreshAsync();
        Check(calls == 1, "较慢读取完成后仍应完整等待 60 秒");
        time.Advance(TimeSpan.FromSeconds(1));
        output = NewOutput();
        var next = service.RefreshAsync();
        Check(calls == 2, "间隔应从实际读取完成后计算");
        output.SetResult("[]");
        await next;
        Check(!service.IsAutomaticRefreshDue(), "并发强制请求不能排队追加另一轮读取");
    }

    private static async Task TestCancellationDoesNotPublish()
    {
        foreach (var failLate in new[] { false, true })
        {
            var time = new ManualTimeProvider();
            var output = NewOutput();
            var calls = 0;
            var service = new PeripheralBatteryService(_ =>
            {
                calls++;
                return calls == 1 ? Task.FromResult<string?>(SampleJson) : output.Task;
            }, time);
            var before = await service.RefreshAsync();
            using var cancellation = new CancellationTokenSource();
            var pending = service.RefreshAsync(force: true, cancellation.Token);
            cancellation.Cancel();
            time.Advance(PeripheralBatteryService.SuccessRefreshInterval);
            Check(!pending.IsCompleted && !service.IsAutomaticRefreshDue(),
                "取消后应等来源退出，再释放单次读取名额");
            Check(ReferenceEquals(service.GetSnapshot(), before), "取消不能发布中间状态");
            if (failLate)
            {
                output.SetException(new IOException("取消后迟到失败"));
            }
            else
            {
                output.SetResult("[]");
            }
            await ExpectCanceled(pending.WaitAsync(TimeSpan.FromSeconds(3)));
            Check(service.IsAutomaticRefreshDue(),
                "忽略取消的来源结束后应释放单次读取");
            Check(ReferenceEquals(service.GetSnapshot(), before),
                "取消后的迟到成功或失败均不能覆盖已有快照或发布过期状态");
        }

        var callsBeforeCancellation = 0;
        var canceledService = new PeripheralBatteryService(_ =>
        {
            callsBeforeCancellation++;
            return Task.FromResult<string?>(SampleJson);
        });
        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        try
        {
            await canceledService.RefreshAsync(cancellationToken: alreadyCanceled.Token);
            throw new InvalidOperationException("已取消请求应抛出取消异常");
        }
        catch (OperationCanceledException)
        {
            Check(callsBeforeCancellation == 0, "已取消请求不应读取来源");
        }
    }

    private static async Task TestWaiterCancellationIsIndependent()
    {
        var output = NewOutput();
        var service = new PeripheralBatteryService(_ => output.Task);
        var first = service.RefreshAsync();
        using var cancellation = new CancellationTokenSource();
        var waiter = service.RefreshAsync(force: true, cancellation.Token);
        cancellation.Cancel();
        await ExpectCanceled(waiter);
        Check(!first.IsCompleted, "取消等待者不能取消已有读取");
        output.SetResult(SampleJson);
        Check((await first).Status == PeripheralBatteryStatus.Fresh,
            "等待者取消后，发起者仍应发布成功快照");
    }

    private static void TestModuleDefaultsAndFormatting()
    {
        var state = new TopBarState();
        Check(!TopBarRules.IsModuleOn(state.Modules, TopBarModuleKind.PeripheralBattery),
            "外设电量模块默认关闭");
        TopBarRules.SetModule(state.Modules, TopBarModuleKind.PeripheralBattery, true);
        Check(TopBarRules.IsModuleOn(state.Modules, TopBarModuleKind.PeripheralBattery),
            "用户可开启外设电量模块");
        Check(TopBarRules.ResolveOrder(state).Right.Contains(TopBarModuleKind.PeripheralBattery),
            "启用模块后可直接在顶栏右侧显示");
        var (left, right) = TopBarRules.NormalizeOrder(
            [TopBarModuleKind.TodoSummary], [TopBarModuleKind.PeripheralBattery, TopBarModuleKind.Clock]);
        Check(right.Contains(TopBarModuleKind.PeripheralBattery) && left.Contains(TopBarModuleKind.TodoSummary),
            "用户调整后的顺序应保留");
        var snapshot = PeripheralBatteryService.Parse(SampleJson, DateTimeOffset.UnixEpoch);
        Check(TopBarModuleRules.ShownPeripherals(snapshot)
            .Select(device => TopBarModuleRules.FormatPeripheralPercent(device.Percent)).SequenceEqual(["85%", "60%"]),
            "顶栏应显示各已连接设备的电量");
        Check(TopBarModuleRules.FormatPeripheralBatteryTooltip(snapshot).Contains("休眠"),
            "详情应保留来源的休眠状态");
        var unknown = PeripheralBatteryService.Parse(
            """[{"key":"unknown","name":"未知电量","connected":true}]""", DateTimeOffset.UnixEpoch);
        Check(TopBarModuleRules.FormatPeripheralPercent(TopBarModuleRules.ShownPeripherals(unknown).Single().Percent) == "—",
            "已连接但电量未知的设备应保留并显示 —");
    }

    private static TaskCompletionSource<string?> NewOutput() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ExpectCanceled(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException("外设电量请求应被取消");
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks = DateTimeOffset.UnixEpoch.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("外设电量测试失败：" + message);
        }
    }
}
