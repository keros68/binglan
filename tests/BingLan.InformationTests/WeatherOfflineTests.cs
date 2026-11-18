using System.Net;
using System.Net.Http;
using BingLan.Core.Models;
using BingLan.Core.Services;
using static BingLan.InformationTests.TestSupport;

namespace BingLan.InformationTests;

// 全部天气测试都通过注入的 HttpMessageHandler 完成，不访问真实网络。
internal static class WeatherOfflineTests
{
    private const string ForecastJson = """
        {
          "latitude": 39.9,
          "longitude": 116.4,
          "current": {
            "time": "2026-08-04T12:00",
            "temperature_2m": 31.5,
            "relative_humidity_2m": 62,
            "weather_code": 2
          },
          "daily": {
            "time": ["2026-08-04"],
            "temperature_2m_max": [34.2],
            "temperature_2m_min": [24.8]
          }
        }
        """;

    public static void SuccessParse()
    {
        var stub = new StubHttpMessageHandler
        {
            Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ForecastJson)
            })
        };
        var service = new WeatherService(stub);
        var snapshot = Await(service.RefreshAsync("北京", 39.9, 116.4));

        AssertEqual(WeatherStatus.Fresh, snapshot.Status, "成功状态");
        AssertEqual("北京", snapshot.City, "城市名");
        AssertEqual(31.5, snapshot.TemperatureCelsius, "当前温度");
        AssertEqual(34.2, snapshot.DailyHighCelsius, "最高温");
        AssertEqual(24.8, snapshot.DailyLowCelsius, "最低温");
        AssertEqual(62, snapshot.HumidityPercent, "湿度");
        AssertEqual("局部多云", snapshot.Condition, "天气现象");
        Assert(snapshot.UpdatedAt is not null, "记录更新时间");
        Assert(!snapshot.FromCache, "非缓存");
        AssertEqual(snapshot, service.GetLastSnapshot("北京", 39.9, 116.4), "共享快照保存成功状态");
        AssertEqual(1, stub.CallCount, "只请求一次");
        Assert(service.ShouldAttemptRefresh(DateTimeOffset.Now), "成功后立即可再次刷新");
        var refreshedAt = snapshot.UpdatedAt!.Value;
        Assert(!service.IsAutomaticRefreshDue(refreshedAt, "北京", 39.9, 116.4), "自动刷新应等待同一地点的成功间隔");
        Assert(!service.IsAutomaticRefreshDue(refreshedAt.AddMinutes(15).AddTicks(-1), "北京", 39.9, 116.4), "成功间隔到期前不能自动重试");
        Assert(service.IsAutomaticRefreshDue(refreshedAt.AddMinutes(15), "北京", 39.9, 116.4), "成功间隔到期后自动刷新");
        Assert(service.IsAutomaticRefreshDue(refreshedAt, "上海", 31.23, 121.47), "切换城市不能继承旧城市的成功间隔");
        Assert(service.IsAutomaticRefreshDue(refreshedAt, "北京", 40.1, 116.4), "同名地点纬度变化应立即刷新");
        Assert(service.IsAutomaticRefreshDue(refreshedAt, "北京", 39.9, 116.8), "同名地点经度变化应立即刷新");
    }

    public static void TimeoutFailure()
    {
        var stub = new StubHttpMessageHandler
        {
            Behavior = async (_, cancellationToken) =>
            {
                await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };
        var service = new WeatherService(stub, TimeSpan.FromMilliseconds(200));
        var snapshot = Await(service.RefreshAsync("北京", 39.9, 116.4));

        AssertEqual(WeatherStatus.Failed, snapshot.Status, "超时状态");
        AssertEqual("请求超时", snapshot.ErrorMessage, "超时消息");
        Assert(snapshot.TemperatureCelsius is null, "超时无数据");
        Assert(service.NextRetryAt is not null, "失败后记录退避时间");
        Assert(!service.ShouldAttemptRefresh(DateTimeOffset.Now), "退避期间不允许快速重试");
    }

    public static void ErrorResponse()
    {
        var stub = new StubHttpMessageHandler
        {
            Behavior = (_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.InternalServerError))
        };
        var service = new WeatherService(stub);
        var snapshot = Await(service.RefreshAsync("北京", 39.9, 116.4));

        AssertEqual(WeatherStatus.Failed, snapshot.Status, "500 状态");
        AssertEqual("网络请求失败", snapshot.ErrorMessage, "500 消息");
        AssertEqual(snapshot, service.GetLastSnapshot("北京", 39.9, 116.4), "首次失败也应提供共享快照");
        Assert(!service.ShouldAttemptRefresh(DateTimeOffset.Now, "北京", 39.9, 116.4), "失败快照与城市退避一致");
    }

    public static void CacheFallback()
    {
        var stub = new StubHttpMessageHandler
        {
            Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ForecastJson)
            })
        };
        var service = new WeatherService(stub);
        var fresh = Await(service.RefreshAsync("北京", 39.9, 116.4));
        AssertEqual(WeatherStatus.Fresh, fresh.Status, "首次成功");

        stub.Behavior = (_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var snapshot = Await(service.RefreshAsync("北京", 39.9, 116.4));

        AssertEqual(WeatherStatus.CachedFallback, snapshot.Status, "缓存回退状态");
        Assert(snapshot.FromCache, "标记为缓存");
        AssertEqual(31.5, snapshot.TemperatureCelsius, "缓存保留温度");
        AssertEqual("局部多云", snapshot.Condition, "缓存保留现象");
        AssertEqual(fresh.UpdatedAt, snapshot.UpdatedAt, "缓存保留原更新时间");
        AssertEqual("网络请求失败", snapshot.ErrorMessage, "缓存附带失败原因");
        AssertEqual(snapshot, service.GetLastSnapshot("北京", 39.9, 116.4), "共享快照应保留缓存回退和失败原因");
        var retryAt = service.NextRetryAt!.Value;
        Assert(!service.IsAutomaticRefreshDue(retryAt.AddTicks(-1), "北京", 39.9, 116.4), "缓存回退应等待失败退避结束");
        Assert(service.IsAutomaticRefreshDue(retryAt, "北京", 39.9, 116.4), "缓存回退在退避到期后重试，不再等待原成功间隔");
        Assert(service.IsAutomaticRefreshDue(retryAt.AddTicks(-1), "上海", 31.23, 121.47), "切换城市不继承旧城市的失败退避");
        var repeatedFailure = Await(service.RefreshAsync("北京", 39.9, 116.4));
        AssertEqual(fresh.UpdatedAt, repeatedFailure.UpdatedAt, "连续失败仍沿用最近成功数据的时间");
        AssertEqual(WeatherStatus.CachedFallback, repeatedFailure.Status, "连续失败保留成功缓存");
    }

    public static void LocationChangeDropsOldCache()
    {
        var stub = new StubHttpMessageHandler
        {
            Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ForecastJson)
            })
        };
        var service = new WeatherService(stub);
        Await(service.RefreshAsync("北京", 39.9, 116.4));

        stub.Behavior = (_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var cached = Await(service.RefreshAsync("北京", 39.9, 116.4));
        AssertEqual(WeatherStatus.CachedFallback, cached.Status, "同城市失败使用缓存");
        Assert(
            service.ShouldAttemptRefresh(DateTimeOffset.Now, "上海", 31.23, 121.47),
            "切换城市不应继承旧城市退避");

        var changed = Await(service.RefreshAsync("上海", 31.23, 121.47));
        AssertEqual(WeatherStatus.Failed, changed.Status, "新城市失败不得显示旧城市缓存");
        AssertEqual("上海", changed.City, "失败状态保留新城市");
        Assert(changed.TemperatureCelsius is null, "新城市失败不得保留旧温度");
        Assert(
            service.GetLastSnapshot("北京", 39.9, 116.4) is null,
            "切换城市后旧城市缓存不应作为当前快照");
        AssertEqual(changed, service.GetLastSnapshot("上海", 31.23, 121.47), "新城市展示自身失败状态");

        LocationChangeWhileRefreshInFlight();
    }

    private static void LocationChangeWhileRefreshInFlight()
    {
        var oldResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newRequestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<string>();
        var stub = new StubHttpMessageHandler
        {
            Behavior = (request, _) =>
            {
                requests.Add(request.RequestUri!.Query);
                if (requests.Count == 2)
                {
                    newRequestStarted.SetResult(true);
                }
                return requests.Count == 1 ? oldResponse.Task : newResponse.Task;
            }
        };
        var service = new WeatherService(stub);
        var oldRefresh = service.RefreshAsync("北京", 39.9, 116.4);
        var newRefresh = service.RefreshAsync("上海", 31.23, 121.47);
        AssertEqual(1, stub.CallCount, "切换城市时已有请求保持串行");
        Assert(service.ShouldAttemptRefresh(DateTimeOffset.Now, "上海", 31.23, 121.47), "新城市不继承在途城市的等待状态");

        oldResponse.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ForecastJson) });
        AssertEqual("北京", Await(oldRefresh).City, "旧请求保留原城市");
        Await(newRequestStarted.Task);
        Assert(service.GetLastSnapshot("上海", 31.23, 121.47) is null, "新城市等待期间不能展示旧城市的成功快照");
        Assert(requests[1].Contains("latitude=31.23") && requests[1].Contains("longitude=121.47"), "后续请求使用新城市坐标");

        newResponse.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ForecastJson) });
        var changed = Await(newRefresh);
        AssertEqual("上海", changed.City, "后续请求完成后展示新城市");
        AssertEqual(changed, service.GetLastSnapshot("上海", 31.23, 121.47), "共享快照跟随新城市");
        Assert(service.GetLastSnapshot("北京", 39.9, 116.4) is null, "旧城市的迟到结果不能作为新城市快照");
    }

    public static void BackoffSchedule()
    {
        AssertEqual(TimeSpan.Zero, WeatherService.ComputeRetryDelay(0), "无失败不退避");
        AssertEqual(TimeSpan.FromMinutes(1), WeatherService.ComputeRetryDelay(1), "第 1 次失败");
        AssertEqual(TimeSpan.FromMinutes(2), WeatherService.ComputeRetryDelay(2), "第 2 次失败");
        AssertEqual(TimeSpan.FromMinutes(4), WeatherService.ComputeRetryDelay(3), "第 3 次失败");
        AssertEqual(TimeSpan.FromMinutes(8), WeatherService.ComputeRetryDelay(4), "第 4 次失败");
        AssertEqual(TimeSpan.FromMinutes(16), WeatherService.ComputeRetryDelay(5), "第 5 次失败");
        AssertEqual(TimeSpan.FromMinutes(30), WeatherService.ComputeRetryDelay(6), "第 6 次封顶");
        AssertEqual(TimeSpan.FromMinutes(30), WeatherService.ComputeRetryDelay(12), "持续失败保持封顶");
    }

    public static void BackoffBlocksRapidRetry()
    {
        var stub = new StubHttpMessageHandler
        {
            Behavior = (_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.InternalServerError))
        };
        var service = new WeatherService(stub);
        Await(service.RefreshAsync("北京", 39.9, 116.4));

        var now = DateTimeOffset.Now;
        Assert(!service.ShouldAttemptRefresh(now), "失败后立即重试被拦截");
        Assert(service.ShouldAttemptRefresh(now.AddMinutes(1).AddSeconds(1)), "退避结束后放行");

        stub.Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ForecastJson)
        });
        Await(service.RefreshAsync("北京", 39.9, 116.4));
        Assert(service.ShouldAttemptRefresh(DateTimeOffset.Now), "成功后清除退避");
    }

    public static void EmptyCitySkipsNetwork()
    {
        var stub = new StubHttpMessageHandler();
        var service = new WeatherService(stub);
        var snapshot = Await(service.RefreshAsync("", 0, 0));

        AssertEqual(WeatherStatus.NoCity, snapshot.Status, "空城市状态");
        AssertEqual(0, stub.CallCount, "空城市不发起请求");
    }

    public static void RequestUrlShape()
    {
        var url = WeatherService.BuildRequestUrl(39.9, 116.4);
        Assert(url.StartsWith(WeatherService.ForecastEndpoint, StringComparison.Ordinal), "使用 Open-Meteo 端点");
        Assert(url.Contains("latitude=39.9"), "纬度参数");
        Assert(url.Contains("longitude=116.4"), "经度参数");
        Assert(url.Contains("current=temperature_2m"), "当前天气字段");
        Assert(url.Contains("daily=temperature_2m_max"), "每日字段");
        Assert(url.Contains("forecast_days=1"), "只取一天");
    }

    public static void WeatherCodeMapping()
    {
        AssertEqual("晴", WeatherService.DescribeWeatherCode(0), "晴");
        AssertEqual("阴", WeatherService.DescribeWeatherCode(3), "阴");
        AssertEqual("小雨", WeatherService.DescribeWeatherCode(61), "小雨");
        AssertEqual("大雪", WeatherService.DescribeWeatherCode(75), "大雪");
        AssertEqual("雷暴", WeatherService.DescribeWeatherCode(95), "雷暴");
        AssertEqual("未知天气", WeatherService.DescribeWeatherCode(999), "未知代码");
    }

    public static void MalformedJson()
    {
        foreach (var json in new[]
        {
            "这不是 JSON",
            "{}",
            "{\"current\":null}",
            "{\"current\":{\"temperature_2m\":null}}",
            "{\"current\":{\"temperature_2m\":20,\"relative_humidity_2m\":2.5}}"
        })
        {
            var stub = new StubHttpMessageHandler
            {
                Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                })
            };
            var service = new WeatherService(stub);
            var snapshot = Await(service.RefreshAsync("北京", 39.9, 116.4));

            AssertEqual(WeatherStatus.Failed, snapshot.Status, "非法天气数据状态");
            AssertEqual("返回数据无法解析", snapshot.ErrorMessage, "非法天气数据消息");
            AssertEqual(snapshot, service.GetLastSnapshot("北京", 39.9, 116.4), "解析失败进入共享状态");
            Assert(!service.ShouldAttemptRefresh(DateTimeOffset.Now), "解析失败后退避");
        }
    }

    private static T Await<T>(Task<T> task)
    {
        PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10), "异步操作完成");
        return task.GetAwaiter().GetResult();
    }
}
