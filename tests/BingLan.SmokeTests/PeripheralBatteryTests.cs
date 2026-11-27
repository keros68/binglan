using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.TopBar;

/// <summary>外设电量服务：引擎 JSON 解析、失败退避与陈旧快照保留。</summary>
internal static class PeripheralBatteryTests
{
    private const string SampleJson = """
        {
          "version": 1,
          "updated": "2026-11-27T00:30:00Z",
          "source": "dll",
          "devices": [
            {"key": "logi:1", "name": "G Pro Wireless Gaming Mouse", "kind": "mouse", "via": "2.4 GHz receiver", "percent": 100, "live": true, "charging": false, "asleep": false, "connected": true, "low": false, "levelText": "", "detail": "", "millivolts": 4150},
            {"key": "bt:{95D4F91B}", "name": "HUAWEI Eyewear 2", "kind": "headset", "via": "Bluetooth", "percent": 60, "live": false, "charging": false, "asleep": true, "connected": true, "low": false, "levelText": "", "detail": "", "millivolts": 0}
          ]
        }
        """;

    internal static void RunAll()
    {
        TestParseSample();
        TestParseEmptyDevices();
        TestEngineUnavailableThenBackoff();
        TestFailureKeepsStaleSnapshot();
        TestModuleSwitchDefaultsOffAndRoundTrips();
        TestFormattingRules();
        TestExtrasEnrichment();
        TestPollIntervalAndForce();
    }

    private const string SampleExtrasJson = """
        {
          "devices": [
            {"key": "logi:1", "estimate": "约 12 小时", "timeToEmpty": "约 12 小时"},
            {"key": "bt:{95D4F91B}", "estimate": "", "timeToEmpty": "约 3 小时"}
          ]
        }
        """;

    private static void TestExtrasEnrichment()
    {
        var extras = PeripheralBatteryService.ParseExtras(SampleExtrasJson);
        Check(extras.Count == 2 && extras["logi:1"].Estimate == "约 12 小时", "估算文档应按 key 解析");

        var service = new PeripheralBatteryService(() => SampleJson, pollExtrasJson: () => SampleExtrasJson);
        var snapshot = service.RefreshAsync().GetAwaiter().GetResult();
        Check(snapshot.Devices[0].EstimateText == "约 12 小时", "放电设备应带上历史估算");
        Check(snapshot.Devices[1].EstimateText == "约 3 小时", "估算为空时应退到 TimeToEmpty");

        // 估算提供者坏掉不能拖垮主轮询
        var broken = new PeripheralBatteryService(
            () => SampleJson,
            pollExtrasJson: () => throw new InvalidOperationException("boom"));
        var stillFresh = broken.RefreshAsync().GetAwaiter().GetResult();
        Check(stillFresh.Status == PeripheralBatteryStatus.Fresh && stillFresh.Devices[0].EstimateText == "",
            "估算失败时主快照应照常新鲜、估算留空");
    }

    private static void TestPollIntervalAndForce()
    {
        var service = new PeripheralBatteryService(() => SampleJson)
        {
            PollInterval = TimeSpan.FromMinutes(10)
        };
        var snapshot = service.RefreshAsync().GetAwaiter().GetResult();
        var updated = snapshot.UpdatedAt!.Value;
        Check(!service.IsAutomaticRefreshDue(updated + TimeSpan.FromMinutes(5)),
            "自定义间隔内不应自动刷新");
        Check(service.IsAutomaticRefreshDue(updated + TimeSpan.FromMinutes(11)),
            "超过自定义间隔后应刷新");

        var forced = new PeripheralBatteryService(() => SampleJson);
        _ = forced.RefreshAsync().GetAwaiter().GetResult();
        forced.ForceRefreshDue();
        Check(forced.IsAutomaticRefreshDue(DateTimeOffset.UtcNow), "立即刷新应绕过间隔直接放行");
        Check(!forced.IsAutomaticRefreshDue(DateTimeOffset.UtcNow), "立即刷新只放行一次");

        var state = new TopBarState();
        Check(TopBarRules.EffectivePeripheralPollSeconds(state) == 15, "轮询间隔缺省 15 秒");
        state.PeripheralPollSeconds = 5;
        Check(TopBarRules.EffectivePeripheralPollSeconds(state) == 10, "轮询间隔下限钳到 10 秒");
        state.PeripheralPollSeconds = 99999;
        Check(TopBarRules.EffectivePeripheralPollSeconds(state) == 3600, "轮询间隔上限钳到 1 小时");
    }

    private static void TestModuleSwitchDefaultsOffAndRoundTrips()
    {
        var state = new TopBarState();
        Check(!TopBarRules.IsModuleOn(state.Modules, TopBarModuleKind.PeripheralBattery),
            "外设电量模块缺省应关闭（依赖引擎 DLL）");
        TopBarRules.SetModule(state.Modules, TopBarModuleKind.PeripheralBattery, true);
        Check(TopBarRules.IsModuleOn(state.Modules, TopBarModuleKind.PeripheralBattery),
            "SetModule 应能打开外设电量模块");
        Check(!TopBarRules.DefaultRightOrder.Contains(TopBarModuleKind.PeripheralBattery) &&
              !TopBarRules.DefaultLeftOrder.Contains(TopBarModuleKind.PeripheralBattery),
            "外设电量不应进入默认顺序，老用户布局保持不变");

        state.LeftOrder = [TopBarModuleKind.TodoSummary];
        state.RightOrder = [TopBarModuleKind.PeripheralBattery, TopBarModuleKind.Clock];
        var (left, right) = TopBarRules.NormalizeOrder(state.LeftOrder, state.RightOrder);
        Check(right.Contains(TopBarModuleKind.PeripheralBattery) && left.Contains(TopBarModuleKind.TodoSummary),
            "用户把外设电量排进右序后，归一化应保留其位置");
    }

    private static void TestFormattingRules()
    {
        var snapshot = PeripheralBatteryService.Parse(SampleJson, DateTimeOffset.UnixEpoch);
        Check(TopBarModuleRules.FormatPeripheralBatteryLabel(snapshot) == "鼠100% 耳60%",
            "顶栏文本应按种类汉字加百分比逐台拼接");
        Check(TopBarModuleRules.FormatPeripheralBatteryLabel(snapshot, TopBarBatteryStyle.IconOnly) == "",
            "仅图标样式不应有文本");
        var tooltip = TopBarModuleRules.FormatPeripheralBatteryTooltip(snapshot);
        Check(tooltip.Contains("HUAWEI Eyewear 2：60%（休眠，最后已知）"), "悬停应写明眼镜休眠口径");
        Check(TopBarModuleRules.LowPeripherals(snapshot).Count == 0, "样例里没有低电设备");

        var empty = PeripheralBatteryService.Parse(
            """{"version": 1, "devices": []}""", DateTimeOffset.UnixEpoch);
        Check(TopBarModuleRules.FormatPeripheralBatteryLabel(empty) == "—", "无设备时顶栏显示 —");

        var unavailable = PeripheralBatterySnapshot.Empty with
        {
            Status = PeripheralBatteryStatus.EngineUnavailable
        };
        Check(TopBarModuleRules.FormatPeripheralBatteryLabel(unavailable) == "" &&
              TopBarModuleRules.FormatPeripheralBatteryTooltip(unavailable).Contains("引擎不可用"),
            "引擎不可用时只留图标，悬停说明原因");
    }

    private static void TestParseSample()
    {
        var snapshot = PeripheralBatteryService.Parse(SampleJson, DateTimeOffset.UnixEpoch);
        Check(snapshot.Status == PeripheralBatteryStatus.Fresh, "解析成功应为 Fresh");
        Check(snapshot.Devices.Count == 2, "应解析出两台设备");
        var mouse = snapshot.Devices[0];
        Check(mouse.Kind == PeripheralDeviceKind.Mouse && mouse.Percent == 100 && mouse.Live &&
              mouse.Millivolts == 4150, "鼠标字段应逐项对上");
        var glasses = snapshot.Devices[1];
        Check(glasses.Kind == PeripheralDeviceKind.Headset && glasses.Percent == 60 &&
              !glasses.Live && glasses.Asleep, "眼镜休眠时 percent 应为最后已知值且 live 为 false");
        Check(snapshot.UpdatedAt == new DateTimeOffset(2026, 11, 27, 0, 30, 0, TimeSpan.Zero),
            "updated 应按 UTC 解析");
    }

    private static void TestParseEmptyDevices()
    {
        var snapshot = PeripheralBatteryService.Parse(
            """{"version": 1, "updated": "2026-11-27T00:30:00Z", "source": "dll", "devices": []}""",
            DateTimeOffset.UnixEpoch);
        Check(snapshot.Status == PeripheralBatteryStatus.Fresh && snapshot.Devices.Count == 0,
            "空设备列表是合法的新鲜快照，不是失败");
    }

    private static void TestEngineUnavailableThenBackoff()
    {
        var service = new PeripheralBatteryService(() => null);
        var snapshot = service.RefreshAsync().GetAwaiter().GetResult();
        Check(snapshot.Status == PeripheralBatteryStatus.EngineUnavailable,
            "引擎无结果且从未成功过应为 EngineUnavailable");
        var now = DateTimeOffset.UtcNow;
        Check(!service.ShouldAttemptRefresh(now), "失败后退避期内不应重试");
        Check(service.ShouldAttemptRefresh(now + PeripheralBatteryService.MaxRetryDelay + TimeSpan.FromSeconds(1)),
            "超过最大退避后应放行重试");
    }

    private static void TestFailureKeepsStaleSnapshot()
    {
        string? json = SampleJson;
        var service = new PeripheralBatteryService(() => json);
        var fresh = service.RefreshAsync().GetAwaiter().GetResult();
        Check(fresh.Status == PeripheralBatteryStatus.Fresh && fresh.Devices.Count == 2, "首次轮询应成功");

        json = null;
        var stale = service.RefreshAsync().GetAwaiter().GetResult();
        Check(stale.Status == PeripheralBatteryStatus.Stale && stale.Devices.Count == 2,
            "成功后再失败应保留上次设备并标 Stale");
        Check(stale.ErrorMessage is { Length: > 0 }, "Stale 快照应带失败原因");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("外设电量测试失败：" + message);
        }
    }
}
