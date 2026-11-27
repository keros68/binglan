using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using BingLan.App.Windows;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Services;
using static BingLan.InformationTests.TestSupport;

namespace BingLan.InformationTests;

internal static class WidgetWindowTests
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

    private const string ChangedForecastJson = """
        {
          "current": {
            "time": "2026-08-04T12:00",
            "temperature_2m": 22.5,
            "relative_humidity_2m": 70,
            "weather_code": 61
          },
          "daily": {
            "temperature_2m_max": [25.0],
            "temperature_2m_min": [18.0]
          }
        }
        """;

    public static void MetricsAlwaysVisibleWithoutHover()
    {
        var state = new InformationWidgetState
        {
            GreetingName = "小明",
            Use24HourClock = true
        };
        var sampler = new FakeSamplingService();
        var weather = new WeatherService(new StubHttpMessageHandler());
        var window = new InformationWidgetWindow(state, sampler, weather);
        ShowAndPump(window);
        try
        {
            Assert(sampler.IsRunning, "窗口加载后共享采样已启动");

            sampler.Emit(new PerformanceSnapshot(12.3, 45.6, 102400d, 204800d, DateTimeOffset.Now));
            Pump();

            var cpu = Require<TextBlock>(window, "CpuValueText");
            var ram = Require<TextBlock>(window, "RamValueText");
            var upload = Require<TextBlock>(window, "UploadValueText");
            var download = Require<TextBlock>(window, "DownloadValueText");
            AssertEqual("12%", cpu.Text, "CPU 数值");
            AssertEqual("46%", ram.Text, "RAM 数值");
            AssertEqual("100 KB/s", upload.Text, "上传速率");
            AssertEqual("200 KB/s", download.Text, "下载速率");
            foreach (var block in new[] { cpu, ram, upload, download })
            {
                Assert(block.Visibility == Visibility.Visible, "性能数值未悬停也必须可见");
                Assert(!block.IsMouseOver, "测试期间未悬停");
            }

            var time = Require<TextBlock>(window, "TimeText");
            var date = Require<TextBlock>(window, "DateText");
            var greeting = Require<TextBlock>(window, "GreetingText");
            Assert(time.Text.Length > 0 && time.Text.Contains(':'), "时间文本非空");
            Assert(date.Text.Contains('年') && date.Text.Contains("星期"), "日期文本非空");
            Assert(greeting.Text.Contains("小明"), "问候包含称呼");
            state.ShowTimeOfDayGreeting = false;
            window.RefreshConfiguredValues(refreshWeather: false);
            AssertEqual("小明", greeting.Text, "关闭后卡片立即只显示称呼");
            state.ShowTimeOfDayGreeting = true;
            window.RefreshConfiguredValues(refreshWeather: false);
            Assert(greeting.Text.EndsWith("，小明", StringComparison.Ordinal), "重新开启后立即恢复时段问候");

            var city = Require<TextBlock>(window, "WeatherCityText");
            var status = Require<TextBlock>(window, "WeatherStatusText");
            AssertEqual("未设置城市", city.Text, "无城市提示");
            AssertEqual("", status.Text, "桌面卡片不显示城市设置指引");
            Assert(window.FindName("WeatherSourceText") is null, "桌面卡片不显示天气供应商说明");
        }
        finally
        {
            window.CanClose = true;
            window.Close();
            Pump();
        }
    }

    public static void WeatherPipelineUpdatesWindow()
    {
        var state = new InformationWidgetState
        {
            WeatherCity = "北京",
            WeatherLatitude = 39.9,
            WeatherLongitude = 116.4
        };
        var sampler = new FakeSamplingService();
        var stub = new StubHttpMessageHandler
        {
            Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ForecastJson)
            })
        };
        var weather = new WeatherService(stub);
        var window = new InformationWidgetWindow(state, sampler, weather);
        ShowAndPump(window);
        try
        {
            sampler.Emit(new PerformanceSnapshot(1d, 2d, 0d, 0d, DateTimeOffset.Now));
            var temp = Require<TextBlock>(window, "WeatherTempText");
            PumpUntil(
                () => temp.Text.Contains("31.5"),
                TimeSpan.FromSeconds(5),
                "天气数据写入窗口");

            AssertEqual("北京", Require<TextBlock>(window, "WeatherCityText").Text, "天气城市");
            AssertEqual("局部多云", Require<TextBlock>(window, "WeatherConditionText").Text, "天气现象");
            Assert(
                Require<TextBlock>(window, "WeatherHumidityText").Text.Contains("湿度 62%"),
                "湿度显示");
            Assert(
                Require<TextBlock>(window, "WeatherUpdatedText").Text.Contains("更新于"),
                "更新时间显示");
            AssertEqual("", Require<TextBlock>(window, "WeatherStatusText").Text, "成功后无错误状态");
        }
        finally
        {
            window.CanClose = true;
            window.Close();
            Pump();
        }
    }

    public static void WeatherFailureShowsStatus()
    {
        var state = new InformationWidgetState
        {
            WeatherCity = "北京",
            WeatherLatitude = 39.9,
            WeatherLongitude = 116.4
        };
        var sampler = new FakeSamplingService();
        var stub = new StubHttpMessageHandler
        {
            Behavior = (_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.InternalServerError))
        };
        var weather = new WeatherService(stub);
        var window = new InformationWidgetWindow(state, sampler, weather);
        ShowAndPump(window);
        try
        {
            sampler.Emit(new PerformanceSnapshot(1d, 2d, 0d, 0d, DateTimeOffset.Now));
            var status = Require<TextBlock>(window, "WeatherStatusText");
            PumpUntil(
                () => status.Text.Contains("刷新失败"),
                TimeSpan.FromSeconds(5),
                "失败状态写入窗口");
        }
        finally
        {
            window.CanClose = true;
            window.Close();
            Pump();
        }
    }

    // 开机时网络未就绪导致首次失败：退避到期后的下一次采样应自动重试，不再等满成功刷新间隔。
    public static void WeatherRetriesAfterBackoffWithoutManualRefresh()
    {
        var state = new InformationWidgetState
        {
            WeatherCity = "北京",
            WeatherLatitude = 39.9,
            WeatherLongitude = 116.4
        };
        var sampler = new FakeSamplingService();
        var stub = new StubHttpMessageHandler
        {
            Behavior = (_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.InternalServerError))
        };
        // 服务时钟落后十分钟，首次失败安排的重试时间在真实时间里已经过去。
        var weather = new WeatherService(
            stub,
            time: new OffsetTimeProvider(TimeSpan.FromMinutes(-10)));
        var window = new InformationWidgetWindow(state, sampler, weather);
        ShowAndPump(window);
        try
        {
            var status = Require<TextBlock>(window, "WeatherStatusText");
            sampler.Emit(new PerformanceSnapshot(1d, 2d, 0d, 0d, DateTimeOffset.Now));
            PumpUntil(
                () => status.Text.Contains("刷新失败"),
                TimeSpan.FromSeconds(5),
                "首次失败写入窗口");

            stub.Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ForecastJson)
            });
            sampler.Emit(new PerformanceSnapshot(1d, 2d, 0d, 0d, DateTimeOffset.Now));
            PumpUntil(
                () => Require<TextBlock>(window, "WeatherTempText").Text.Contains("31.5"),
                TimeSpan.FromSeconds(5),
                "退避到期后自动重试成功");
            AssertEqual(2, stub.CallCount, "失败一次后自动重试一次");
        }
        finally
        {
            window.CanClose = true;
            window.Close();
            Pump();
        }
    }

    private sealed class OffsetTimeProvider(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }

    public static void DefaultHeightShowsWeatherDetails()
    {
        var state = new InformationWidgetState
        {
            WeatherCity = "北京 · 北京市 · 中国",
            WeatherLatitude = 39.9,
            WeatherLongitude = 116.4
        };
        var sampler = new FakeSamplingService();
        var stub = new StubHttpMessageHandler
        {
            Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ForecastJson)
            })
        };
        var window = new InformationWidgetWindow(
            state,
            sampler,
            new WeatherService(stub));
        ShowAndPump(window);
        try
        {
            sampler.Emit(new PerformanceSnapshot(1d, 2d, 0d, 0d, DateTimeOffset.Now));
            PumpUntil(
                () => Require<TextBlock>(window, "WeatherTempText").Text.Contains("31.5"),
                TimeSpan.FromSeconds(5),
                "完整天气数据写入窗口");
            var temp = Require<TextBlock>(window, "WeatherTempText");
            var detailsPanel = Require<WholeCellRowPanel>(window, "WeatherDetailsPanel");
            var high = Require<TextBlock>(window, "WeatherHighText");
            var low = Require<TextBlock>(window, "WeatherLowText");
            var humidity = Require<TextBlock>(window, "WeatherHumidityText");
            var updated = Require<TextBlock>(window, "WeatherUpdatedText");
            Assert(detailsPanel.IsVisible, "天气细节行默认应可见");
            Assert(high.IsVisible && high.ActualWidth > 0 && high.Text.Contains("34.2"), "最高温度格应完整显示");
            Assert(low.IsVisible && low.ActualWidth > 0 && low.Text.Contains("24.8"), "最低温度格应完整显示");
            Assert(humidity.IsVisible && humidity.ActualWidth > 0 && humidity.Text.Contains("62%"),
                "默认宽度下湿度格应完整显示，不整格省略");
            var tempBottom = temp.TranslatePoint(new Point(0d, temp.ActualHeight), window).Y;
            var detailsTop = detailsPanel.TranslatePoint(new Point(0d, 0d), window).Y;
            var humidityRight = humidity.TranslatePoint(new Point(humidity.ActualWidth, 0d), window).X;
            Assert(detailsTop >= tempBottom - 2d, "天气高低温与湿度行应位于温度下方并占满宽度");
            Assert(humidityRight <= window.ActualWidth - 4d, "湿度格应落在默认卡片宽度内");
            Assert(updated.Text.Contains("更新于"), "天气更新时间常驻显示");
            Assert(window.FindName("WeatherSourceText") is null, "天气卡片不包含供应商说明");
            var detailsBottom = updated
                .TranslatePoint(new Point(0d, updated.ActualHeight), window)
                .Y;
            Assert(
                detailsBottom <= window.ActualHeight - 8d,
                $"默认高度下天气内容被裁切：底部 {detailsBottom:0.#}，窗口 {window.ActualHeight:0.#}");
            if (window.FindName("WeatherDetailsPanel") is FrameworkElement weatherDetailsRow
                && weatherDetailsRow.Visibility == Visibility.Visible)
            {
                var detailsRowBottom = weatherDetailsRow
                    .TranslatePoint(new Point(0d, weatherDetailsRow.ActualHeight), window)
                    .Y;
                Assert(
                    detailsRowBottom <= window.ActualHeight - 8d,
                    $"默认高度下天气详情行被裁切：底部 {detailsRowBottom:0.#}，窗口 {window.ActualHeight:0.#}");
            }
        }
        finally
        {
            window.CanClose = true;
            window.Close();
            Pump();
        }
    }

    public static void ScaledWeatherMinHeightKeepsDetailsInsideCard()
    {
        var experience = DesktopExperienceRules.CreateDefault();
        var weatherComponent = experience.GetComponent(DesktopComponentKind.Weather);
        weatherComponent.IsVisible = true;
        weatherComponent.FontScale = 1.296d;
        weatherComponent.Density = DesktopInformationDensity.Standard;
        weatherComponent.Placement.Width = 370d;
        weatherComponent.Placement.Height = 154d;

        var state = new InformationWidgetState
        {
            WeatherCity = "杭州",
            WeatherLatitude = 30.25d,
            WeatherLongitude = 120.16d,
            Placement = new WindowPlacement
            {
                Left = weatherComponent.Placement.Left,
                Top = weatherComponent.Placement.Top,
                Width = 370d,
                Height = 154d
            }
        };

        using var sampler = new R1StubPerformanceSampler();
        using var handler = new R1StubWeatherHandler();
        var weather = new WeatherService(handler);
        weather.RefreshAsync("杭州", 30.25d, 120.16d).GetAwaiter().GetResult();

        var window = new InformationWidgetWindow(
            state,
            sampler,
            weather,
            experience,
            DesktopComponentKind.Weather);
        try
        {
            window.ApplyPlacement(weatherComponent.Placement, false);
            window.Show();
            Pump();
            window.UpdateLayout();
            Pump();
            window.CaptureState();

            var expectedMinHeight = DesktopExperienceRules.CalculateWeatherMinHeight(
                weatherComponent.FontScale,
                weatherComponent.Density);
            Assert(
                window.ActualHeight >= expectedMinHeight - 0.5d,
                $"字号 1.296 天气卡片实际高度 {window.ActualHeight:0.#} 不应低于计算最小高度 {expectedMinHeight:0.#}");
            Assert(
                Math.Abs(weatherComponent.Placement.Height - 154d) < 0.01d,
                $"加载与显示不应改写用户保存的天气卡片高度 154，实际为 {weatherComponent.Placement.Height:0.##}");

            var detailsPanel = window.FindName("WeatherDetailsPanel") as FrameworkElement
                ?? throw new InvalidOperationException("找不到 WeatherDetailsPanel");
            Assert(
                detailsPanel.Visibility == Visibility.Visible && detailsPanel.ActualHeight > 0d,
                "天气详情行应正常显示");
            var detailsBottom = detailsPanel
                .TranslatePoint(new Point(0d, detailsPanel.ActualHeight), window)
                .Y;
            Assert(
                detailsBottom <= window.ActualHeight - 8d,
                $"字号 1.296、宽 370、保存高度 154 时底部详情行被裁切：底部 {detailsBottom:0.#}，窗口 {window.ActualHeight:0.#}");
        }
        finally
        {
            window.CanClose = true;
            window.Close();
            Pump();
        }

        AssertIsolatedWeatherSymmetry(state, sampler, weather, 1d, DesktopExperienceRules.DefaultWeatherCardHeight, "字号 1.0");
        AssertIsolatedWeatherSymmetry(state, sampler, weather, 1.3d, DesktopExperienceRules.CalculateWeatherMinHeight(1.3d, DesktopInformationDensity.Standard), "字号 1.3");
        AssertIsolatedPerformanceDefaultFits(state, sampler, weather);
    }

    private static void AssertIsolatedWeatherSymmetry(
        InformationWidgetState state,
        R1StubPerformanceSampler sampler,
        WeatherService weather,
        double fontScale,
        double savedHeight,
        string label)
    {
        var experience = DesktopExperienceRules.CreateDefault();
        var weatherComponent = experience.GetComponent(DesktopComponentKind.Weather);
        weatherComponent.IsVisible = true;
        weatherComponent.FontScale = fontScale;
        weatherComponent.Density = DesktopInformationDensity.Standard;
        weatherComponent.Placement.Width = 370d;
        weatherComponent.Placement.Height = savedHeight;

        var window = new InformationWidgetWindow(
            state,
            sampler,
            weather,
            experience,
            DesktopComponentKind.Weather);
        try
        {
            window.ApplyPlacement(weatherComponent.Placement, false);
            window.Show();
            Pump();
            window.UpdateLayout();
            Pump();

            var city = Require<TextBlock>(window, "WeatherCityText");
            var detailsPanel = Require<WholeCellRowPanel>(window, "WeatherDetailsPanel");
            var topWhitespace = Math.Max(0d, city.TranslatePoint(new Point(0d, 0d), window).Y - 8d);
            var detailsBottom = detailsPanel.TranslatePoint(new Point(0d, detailsPanel.ActualHeight), window).Y;
            var bottomWhitespace = window.ActualHeight - detailsBottom;
            // Windows Server CI images use fallback CJK font metrics that are ~5-6 DIP taller across the three rows.
            Assert(
                bottomWhitespace >= 8d && Math.Abs(bottomWhitespace - topWhitespace) <= 6d,
                $"{label} 天气卡上下留白应接近：顶部 {topWhitespace:0.#}，底部 {bottomWhitespace:0.#}，窗口高 {window.ActualHeight:0.#}");
        }
        finally
        {
            window.CanClose = true;
            window.Close();
            Pump();
        }
    }

    private static void AssertIsolatedPerformanceDefaultFits(
        InformationWidgetState state,
        R1StubPerformanceSampler sampler,
        WeatherService weather)
    {
        var experience = DesktopExperienceRules.CreateDefault();
        var performanceComponent = experience.GetComponent(DesktopComponentKind.Performance);
        performanceComponent.IsVisible = true;
        var window = new InformationWidgetWindow(
            state,
            sampler,
            weather,
            experience,
            DesktopComponentKind.Performance);
        try
        {
            window.ApplyPlacement(performanceComponent.Placement, false);
            window.Show();
            Pump();
            window.UpdateLayout();
            Pump();

            var downloadValue = Require<TextBlock>(window, "DownloadValueText");
            var cpuBar = window.FindName("CpuBar") as FrameworkElement
                ?? throw new InvalidOperationException("找不到 CpuBar");
            var contentBottom = Math.Max(
                downloadValue.TranslatePoint(new Point(0d, downloadValue.ActualHeight), window).Y,
                cpuBar.TranslatePoint(new Point(0d, cpuBar.ActualHeight), window).Y);
            Assert(
                contentBottom <= window.ActualHeight - 10d,
                $"性能卡片默认高度 {window.ActualHeight:0.#} 下内容被裁切或过紧：底部 {contentBottom:0.#}");
        }
        finally
        {
            window.CanClose = true;
            window.Close();
            Pump();
        }
    }

    private sealed class R1StubPerformanceSampler : IPerformanceSamplingService
    {
        public event Action<PerformanceSnapshot>? Sampled { add { } remove { } }
        public bool IsRunning => false;
        public PerformanceSamplingMode Mode => PerformanceSamplingMode.Normal;
        public TimeSpan CurrentInterval => TimeSpan.FromSeconds(1);
        public void Start() { }
        public void SetMode(PerformanceSamplingMode mode) { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class R1StubWeatherHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(
                    """{"current":{"temperature_2m":23.4,"relative_humidity_2m":58,"weather_code":1},"daily":{"temperature_2m_max":[27.2],"temperature_2m_min":[16.8]}}""",
                    System.Text.Encoding.UTF8,
                    "application/json")
            });
    }

    public static void CitySettingsEntryRaisesRequest()
    {
        var window = new InformationWidgetWindow(
            new InformationWidgetState(),
            new FakeSamplingService(),
            new WeatherService(new StubHttpMessageHandler()));
        var requestCount = 0;
        window.OpenAppSettingsRequested += _ => requestCount++;
        ShowAndPump(window);
        try
        {
            Assert(window.FindName("SetWeatherCityButton") is null, "城市设置不应常驻桌面");
            var settingsItem = window.ContextMenu!.Items
                .OfType<MenuItem>()
                .Single(item => Equals(item.Header, "在冰蓝桌面中设置…"));
            settingsItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump();
            AssertEqual(1, requestCount, "统一设置入口没有打开 App 设置");
        }
        finally
        {
            window.CanClose = true;
            window.Close();
            Pump();
        }
    }

    public static void CityChangeIgnoresStaleWeather()
    {
        var firstRequestGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestIndex = 0;
        var stub = new StubHttpMessageHandler
        {
            Behavior = async (_, _) =>
            {
                var currentRequest = Interlocked.Increment(ref requestIndex);
                if (currentRequest == 1)
                {
                    await firstRequestGate.Task;
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        currentRequest == 1 ? ForecastJson : ChangedForecastJson)
                };
            }
        };
        var state = new InformationWidgetState
        {
            WeatherCity = "北京",
            WeatherLatitude = 39.9,
            WeatherLongitude = 116.4
        };
        var sampler = new FakeSamplingService();
        var window = new InformationWidgetWindow(
            state,
            sampler,
            new WeatherService(stub));
        ShowAndPump(window);
        try
        {
            sampler.Emit(new PerformanceSnapshot(1d, 2d, 0d, 0d, DateTimeOffset.Now));
            PumpUntil(() => stub.CallCount == 1, TimeSpan.FromSeconds(5), "旧城市天气请求启动");

            state.WeatherCity = "上海";
            state.WeatherLatitude = 31.23;
            state.WeatherLongitude = 121.47;
            window.RefreshConfiguredValues(true);
            firstRequestGate.SetResult();

            var temperature = Require<TextBlock>(window, "WeatherTempText");
            PumpUntil(
                () => stub.CallCount == 2 && temperature.Text.Contains("22.5"),
                TimeSpan.FromSeconds(5),
                "新城市天气替换旧请求结果");
            AssertEqual("上海", Require<TextBlock>(window, "WeatherCityText").Text,
                "旧城市返回不得覆盖新城市");
        }
        finally
        {
            firstRequestGate.TrySetResult();
            window.CanClose = true;
            window.Close();
            Pump();
        }
    }

    // 单组件卡片每秒只刷新自己显示的内容：天气卡不再改动折叠着的性能数值。
    public static void IsolatedCardsOnlyRefreshWhatTheyShow()
    {
        var experience = DesktopExperienceRules.CreateDefault();
        var sampler = new FakeSamplingService();
        var weatherService = new WeatherService(new StubHttpMessageHandler());
        var weather = new InformationWidgetWindow(
            new InformationWidgetState(), sampler, weatherService, experience, DesktopComponentKind.Weather);
        var performance = new InformationWidgetWindow(
            new InformationWidgetState(), sampler, weatherService, experience, DesktopComponentKind.Performance);
        ShowAndPump(weather);
        ShowAndPump(performance);
        try
        {
            sampler.Emit(new PerformanceSnapshot(12.3, 45.6, 102400d, 204800d, DateTimeOffset.Now));
            Pump();
            AssertEqual("12%", Require<TextBlock>(performance, "CpuValueText").Text, "性能卡应刷新 CPU");
            AssertEqual("0%", Require<TextBlock>(weather, "CpuValueText").Text, "天气卡不应刷新折叠的性能数值");
        }
        finally
        {
            foreach (var window in new[] { weather, performance })
            {
                window.CanClose = true;
                window.Close();
            }
            Pump();
        }
    }

    public static void ClockDividerAndLockMenu()
    {
        var experience = DesktopExperienceRules.CreateDefault();
        var timeDate = experience.GetComponent(DesktopComponentKind.TimeDate);
        var window = new InformationWidgetWindow(
            new InformationWidgetState(),
            new FakeSamplingService(),
            new WeatherService(new StubHttpMessageHandler()),
            experience,
            DesktopComponentKind.TimeDate);
        ShowAndPump(window);
        try
        {
            var divider = Require<System.Windows.Shapes.Rectangle>(window, "ClockDivider");
            AssertEqual(Visibility.Collapsed, divider.Visibility, "默认不显示时间下方的细线");
            timeDate.ShowDivider = true;
            window.ApplyDesktopExperience(experience);
            Pump();
            Assert(divider.Visibility == Visibility.Visible
                && divider.Fill is System.Windows.Media.LinearGradientBrush { GradientStops: [{ Color.A: 0 }, _, { Color.A: 0 }] },
                "打开后应显示两端透明的渐变细线");
            AssertEqual(2d, divider.Height, "细线默认 2 像素");
            timeDate.DividerThickness = 4;
            window.ApplyDesktopExperience(experience);
            Pump();
            AssertEqual(4d, divider.Height, "细线粗细应跟随设置");

            var lockItem = window.ContextMenu!.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, "锁定位置和大小"));
            lockItem.IsChecked = true;
            lockItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump();
            Assert(window.IsWidgetLocked, "右键菜单应能锁定卡片");
        }
        finally
        {
            window.CanClose = true;
            window.Close();
            Pump();
        }
    }

    public static void IsolatedSurfaceVisibilityAndPlacement()
    {
        var state = new InformationWidgetState();
        var originalStatePlacement = state.Placement.Left;
        var experience = DesktopExperienceRules.CreateDefault();
        var greeting = experience.GetComponent(DesktopComponentKind.Greeting);
        var window = new InformationWidgetWindow(
            state,
            new FakeSamplingService(),
            new WeatherService(new StubHttpMessageHandler()),
            experience,
            DesktopComponentKind.Greeting);
        window.ApplyPlacement(greeting.Placement, greeting.IsLocked);
        ShowAndPump(window);
        try
        {
            AssertEqual(Visibility.Collapsed, Require<TextBlock>(window, "TimeText").Visibility,
                "问候表面不应重复显示时间");
            var greetingText = Require<TextBlock>(window, "GreetingText");
            AssertEqual(Visibility.Visible, greetingText.Visibility,
                "问候表面应显示问候语");
            Assert(
                !string.IsNullOrWhiteSpace(greetingText.Text) &&
                greetingText.FontSize >= 34d &&
                greetingText.ActualHeight >= 32d,
                "独立问候表面应使用可见的大字号信息层级");
            AssertEqual(Visibility.Collapsed, Require<Border>(window, "PerformanceCard").Visibility,
                "问候表面不应重复显示性能");
            AssertEqual(Visibility.Collapsed, Require<Border>(window, "WeatherCard").Visibility,
                "问候表面不应重复显示天气");
            var settingsButton = Require<Button>(window, "SettingsButton");
            AssertEqual(Visibility.Collapsed, settingsButton.Visibility,
                "未选中的独立信息表面应保持低干扰");
            window.ApplySelection(true);
            Pump();
            AssertEqual(Visibility.Visible, settingsButton.Visibility,
                "选中信息表面后应显示设置入口");
            window.ApplySelection(false);

            window.Left = 244;
            window.Top = 188;
            window.Width = 620;
            window.Height = 110;
            Pump();
            window.CaptureState();

            Assert(
                Math.Abs(greeting.Placement.Left - 244) < 0.5 &&
                Math.Abs(greeting.Placement.Width - 620) < 0.5,
                "独立问候表面位置与尺寸未写回组件状态");
            AssertEqual(originalStatePlacement, state.Placement.Left,
                "独立表面不应覆盖共享个人信息窗口位置");
        }
        finally
        {
            window.CanClose = true;
            window.Close();
            Pump();
        }
    }

    // 设置中心改圆角后，独立的时间、天气、性能卡要换成新的外形，而不只是待办卡。
    public static void IsolatedCardsFollowComponentCornerRadius()
    {
        foreach (var kind in new[]
                 {
                     DesktopComponentKind.TimeDate,
                     DesktopComponentKind.Weather,
                     DesktopComponentKind.Performance
                 })
        {
            var experience = DesktopExperienceRules.CreateDefault();
            var component = experience.GetComponent(kind);
            component.CornerRadius = 4d;
            var window = new InformationWidgetWindow(
                new InformationWidgetState(),
                new FakeSamplingService(),
                new WeatherService(new StubHttpMessageHandler()),
                experience,
                kind);
            window.ApplyPlacement(component.Placement, component.IsLocked);
            ShowAndPump(window);
            try
            {
                AssertCorner(window, 4d, $"{kind} 打开时");

                component.CornerRadius = 24d;
                window.ApplyDesktopExperience(experience);
                Pump();
                AssertCorner(window, 24d, $"{kind} 改圆角后");
            }
            finally
            {
                window.CanClose = true;
                window.Close();
                Pump();
            }
        }

        static void AssertCorner(InformationWidgetWindow window, double expected, string label)
        {
            AssertEqual(expected, window.WidgetCornerRadius, $"{label}窗口圆角");
            AssertEqual(new CornerRadius(expected), Require<Border>(window, "Surface").CornerRadius,
                $"{label}外框圆角");
            Assert(window.Clip is System.Windows.Media.RectangleGeometry clip &&
                   Math.Abs(clip.RadiusX - expected) < 0.01,
                $"{label}窗口裁剪形状应使用新圆角");
        }
    }

    // 字号缩放覆盖日期、性能标签和天气状态文字（按钮除外）。
    public static void FontScaleCoversAllText()
    {
        var experience = DesktopExperienceRules.CreateDefault();
        foreach (var kind in new[]
                 {
                     DesktopComponentKind.TimeDate,
                     DesktopComponentKind.Performance,
                     DesktopComponentKind.Weather
                 })
        {
            experience.GetComponent(kind).FontScale = 1.5;
        }
        var window = new InformationWidgetWindow(
            new InformationWidgetState(),
            new FakeSamplingService(),
            new WeatherService(new StubHttpMessageHandler()),
            experience);
        ShowAndPump(window);
        try
        {
            AssertEqual(19.5, Require<TextBlock>(window, "DateText").FontSize, "日期跟随时间组件的缩放");
            AssertEqual(16.5, Require<TextBlock>(window, "CpuLabelText").FontSize, "CPU 标签跟随缩放");
            AssertEqual(16.5, Require<TextBlock>(window, "RamLabelText").FontSize, "RAM 标签跟随缩放");
            AssertEqual(16.5, Require<TextBlock>(window, "NetworkLabelText").FontSize, "NETWORK 标签跟随缩放");
            AssertEqual(18d, Require<TextBlock>(window, "UploadLabelText").FontSize, "UP 标签跟随缩放");
            AssertEqual(18d, Require<TextBlock>(window, "DownloadLabelText").FontSize, "DN 标签跟随缩放");
            AssertEqual(16.5, Require<TextBlock>(window, "WeatherStatusText").FontSize, "天气状态文字跟随缩放");
            AssertEqual(15d, Require<Button>(window, "WeatherRefreshButton").FontSize, "刷新按钮不缩放");
        }
        finally
        {
            window.CanClose = true;
            window.Close();
            Pump();
        }
    }

    public static void NakedTextContrastDefaults()
    {
        var experience = DesktopExperienceRules.CreateDefault();
        var timeDate = experience.GetComponent(DesktopComponentKind.TimeDate);
        AssertEqual(WidgetAppearanceRules.DefaultTextColor, timeDate.Appearance.TextColor,
            "时间日期默认文字色应跟随全局浅色玻璃");
        AssertEqual(WidgetAppearanceRules.DefaultBackgroundOpacity, timeDate.Appearance.BackgroundOpacity,
            "时间日期默认底板不透明度应跟随全局浅色玻璃");
        AssertEqual("#FFFFFF",
            experience.GetComponent(DesktopComponentKind.Greeting).Appearance.TextColor,
            "问候默认文字色应为白色");

        var window = new InformationWidgetWindow(
            new InformationWidgetState(),
            new FakeSamplingService(),
            new WeatherService(new StubHttpMessageHandler()),
            experience,
            DesktopComponentKind.TimeDate);
        window.ApplyPlacement(timeDate.Placement, timeDate.IsLocked);
        ShowAndPump(window);
        try
        {
            var timeText = Require<TextBlock>(window, "TimeText");
            Assert(timeText.Effect is null, "有玻璃底板时时间文字不应叠加裸文字阴影");

            // Switching TimeDate to Clear/unbacked applies single-layer naked contrast shadow.
            timeDate.Appearance.BackgroundOpacity = WidgetAppearanceRules.MinimumBackgroundOpacity;
            timeDate.Appearance.TextColor = "#FFFFFF";
            window.ApplyDesktopExperience(experience);
            Pump();

            Assert(timeText.Effect is System.Windows.Media.Effects.DropShadowEffect,
                "无底板时间文字缺少对比度阴影");
            Assert(
                Require<TextBlock>(window, "DateText").Effect
                    is System.Windows.Media.Effects.DropShadowEffect,
                "无底板日期文字缺少对比度阴影");
            Assert(
                Require<TextBlock>(window, "GreetingText").Effect
                    is System.Windows.Media.Effects.DropShadowEffect,
                "问候文字缺少对比度阴影");
            Assert(
                ((UIElement)window.Content).Effect is null,
                "无底板时间日期卡片容器本身不应叠加第二层阴影");
            Assert(
                timeText.Foreground is System.Windows.Media.SolidColorBrush
                {
                    Color: { R: 255, G: 255, B: 255 }
                },
                "无底板时间文字未使用白色前景");
        }
        finally
        {
            window.CanClose = true;
            window.Close();
            Pump();
        }
    }

    public static void OffScreenHiddenComponentRecoversOnShow()
    {
        const string earlierLayout = "earlier-layout";
        var savedDualSpot = new PixelRect(2400, 300, 2960, 500);
        foreach (var (kind, width, height) in new[]
                 {
                     (DesktopComponentKind.TimeDate, 560d, 200d),
                     (DesktopComponentKind.Greeting, 640d, 84d)
                 })
        {
            var experience = DesktopExperienceRules.CreateDefault();
            var component = experience.GetComponent(kind);
            component.IsVisible = true;
            component.Placement = new WindowPlacement
            {
                Left = 20000d,
                Top = 15000d,
                Width = width,
                Height = height
            };
            DisplayLayoutRules.Remember(component.Placement, earlierLayout, savedDualSpot);

            var window = new InformationWidgetWindow(
                new InformationWidgetState(),
                new FakeSamplingService(),
                new WeatherService(new StubHttpMessageHandler()),
                experience,
                kind);
            try
            {
                window.ApplyPlacement(component.Placement, component.IsLocked);
                ShowAndPump(window);

                AssertWindowInsideWorkArea(window, $"{kind} 首次显示越界位置");
                Assert(
                    DisplayLayoutRules.TryRecall(component.Placement, earlierLayout, out var preserved)
                    && preserved == savedDualSpot,
                    $"{kind} 收拢回工作区不应丢弃其他显示器排列的记忆位置");

                window.Hide();
                Pump();
                var overhang = new WindowPlacement
                {
                    Left = SystemParameters.WorkArea.Right - 90d,
                    Top = SystemParameters.WorkArea.Bottom - 30d,
                    Width = width,
                    Height = height
                };
                DisplayLayoutRules.Remember(overhang, earlierLayout, savedDualSpot);
                window.ApplyPlacement(overhang, component.IsLocked);
                ShowAndPump(window);

                AssertWindowInsideWorkArea(window, $"{kind} 隐藏后再显示边缘越界位置");
                Assert(
                    DisplayLayoutRules.TryRecall(overhang, earlierLayout, out preserved)
                    && preserved == savedDualSpot,
                    $"{kind} 隐藏后再显示不应破坏其他显示器排列记忆");
            }
            finally
            {
                window.CanClose = true;
                window.Close();
                Pump();
            }
        }
    }

    private static void AssertWindowInsideWorkArea(Window window, string label)
    {
        var handle = new WindowInteropHelper(window).Handle;
        Assert(handle != 0, $"{label} 缺少窗口句柄");
        Assert(GetWindowRect(handle, out var rect), $"{label} 无法读取窗口矩形");
        var monitor = MonitorFromWindow(handle, 2);
        var info = new MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>(),
            DeviceName = string.Empty
        };
        Assert(monitor != 0 && GetMonitorInfoW(monitor, ref info), $"{label} 无法读取所属显示器工作区");
        var workArea = info.WorkArea;
        Assert(
            rect.Left >= workArea.Left
            && rect.Right <= workArea.Right
            && rect.Top >= workArea.Top
            && rect.Bottom <= workArea.Bottom,
            $"{label} 应完整落在工作区内：窗口 ({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})，工作区 ({workArea.Left},{workArea.Top})-({workArea.Right},{workArea.Bottom})");
    }

    public static void ClockLayoutPresetsRespectCustomizationsAndToggles()
    {
        var state = new InformationWidgetState
        {
            Use24HourClock = true,
            ShowDate = true,
            ClockLayout = ClockLayoutPreset.Classic
        };
        var experience = DesktopExperienceRules.CreateDefault();
        var clockComponent = experience.GetComponent(DesktopComponentKind.TimeDate);
        var window = new InformationWidgetWindow(
            state,
            new FakeSamplingService(),
            new WeatherService(new StubHttpMessageHandler()),
            experience,
            DesktopComponentKind.TimeDate);
        window.ApplyPlacement(clockComponent.Placement, clockComponent.IsLocked);
        ShowAndPump(window);

        var settings = new SettingsWindow(
            new CityLookupService(new CitySearchService(new StubHttpMessageHandler()), CityLibrary.Empty),
            state,
            experience,
            (greeting, use24) =>
            {
                state.GreetingName = greeting;
                state.Use24HourClock = use24;
            },
            _ => { },
            updated => window.ApplyDesktopExperience(updated),
            widgetProvider: () => [window]);
        ShowAndPump(settings);

        try
        {
            settings.ShowComponentSettings(DesktopComponentKind.TimeDate);
            Pump();

            var selector = Require<ComboBox>(settings, "ClockLayoutSelector");
            var alignmentSelector = Require<ComboBox>(settings, "ClockAlignmentSelector");
            var showDateBox = Require<CheckBox>(settings, "ShowClockDateCheckBox");
            var showLunarBox = Require<CheckBox>(settings, "ShowClockLunarDateCheckBox");
            var use24Box = Require<CheckBox>(settings, "Use24HourClockCheckBox");
            var timeText = Require<TextBlock>(window, "TimeText");
            var dateText = Require<TextBlock>(window, "DateText");
            var periodText = Require<TextBlock>(window, "TimePeriodText");

            void SelectLayout(ClockLayoutPreset preset)
            {
                selector.SelectedItem = selector.Items.OfType<ComboBoxItem>()
                    .Single(item => Equals(item.Tag, preset.ToString()));
                Pump();
            }

            void SelectAlignment(ClockHorizontalAlignment alignment)
            {
                alignmentSelector.SelectedItem = alignmentSelector.Items.OfType<ComboBoxItem>()
                    .Single(item => Equals(item.Tag, alignment.ToString()));
                Pump();
                AssertEqual(alignment, state.ClockAlignment, "设置页应保存时钟水平对齐选项");
            }

            // Classic: time sits above date, and lunar text shows by default when enabled.
            SelectLayout(ClockLayoutPreset.Classic);
            AssertEqual(ClockLayoutPreset.Classic, state.ClockLayout, "设置页选中经典版式应写入状态");
            Assert(dateText.Text.Contains("农历", StringComparison.Ordinal), "默认开启显示农历时日期应包含农历文本");
            var timeRect = timeText.TransformToAncestor(window).TransformBounds(new Rect(0, 0, timeText.ActualWidth, timeText.ActualHeight));
            var dateRect = dateText.TransformToAncestor(window).TransformBounds(new Rect(0, 0, dateText.ActualWidth, dateText.ActualHeight));
            Assert(timeRect.Top < dateRect.Top, "经典版式时间应位于日期上方");

            // MinimalDigits with default glass card backing keeps ExtraLight and secondary-ink date.
            SelectLayout(ClockLayoutPreset.MinimalDigits);
            AssertEqual(TextAlignment.Center, timeText.TextAlignment, "极简大数字应居中排版");
            AssertEqual(FontWeights.ExtraLight, timeText.FontWeight,
                "有底板时极简大数字应保持 ExtraLight 字重");
            Assert(((System.Windows.Media.SolidColorBrush)dateText.Foreground).Color.A < byte.MaxValue,
                "有底板时极简大数字日期应保留次级墨色");

            SelectLayout(ClockLayoutPreset.VerticalDigits);
            Assert(((System.Windows.Media.SolidColorBrush)dateText.Foreground).Color.A < byte.MaxValue,
                "有底板时纵向数字日期应保留次级墨色");

            // Switching to unbacked lifts MinimalDigits weight to Light and uses primary ink for date.
            clockComponent.Appearance.BackgroundOpacity = WidgetAppearanceRules.MinimumBackgroundOpacity;
            clockComponent.Appearance.TextColor = "#FFFFFF";
            window.ApplyDesktopExperience(experience);
            Pump();

            SelectLayout(ClockLayoutPreset.MinimalDigits);
            Assert(timeText.FontSize >= 60d && timeText.FontWeight == FontWeights.Light,
                "无底板下极简大数字应将数字字重提高一档为 Light");
            AssertEqual(byte.MaxValue, ((System.Windows.Media.SolidColorBrush)dateText.Foreground).Color.A,
                "无底板下极简大数字日期应使用主墨色");

            // DatePoster: date sits above time; 12h period renders beside date.
            SelectLayout(ClockLayoutPreset.DatePoster);
            use24Box.IsChecked = false;
            Pump();
            timeRect = timeText.TransformToAncestor(window).TransformBounds(new Rect(0, 0, timeText.ActualWidth, timeText.ActualHeight));
            dateRect = dateText.TransformToAncestor(window).TransformBounds(new Rect(0, 0, dateText.ActualWidth, dateText.ActualHeight));
            Assert(dateRect.Top < timeRect.Top, "日期海报版式日期应位于时间上方");
            AssertEqual(Visibility.Visible, periodText.Visibility, "非经典版式在 12 小时制下应显示上午/下午标记");
            AssertEqual(TextAlignment.Left, timeText.TextAlignment, "日期海报默认在普通桌面布局下左对齐");

            SelectAlignment(ClockHorizontalAlignment.Center);
            AssertEqual(TextAlignment.Center, timeText.TextAlignment, "日期海报应可在普通桌面布局下直接居中");
            AssertEqual(TextAlignment.Center, dateText.TextAlignment, "日期应与时间采用同一水平对齐");
            AssertEqual(DesktopLayoutPreset.QuietInformation, experience.ActivePreset, "时钟对齐设置应保留整桌布局");

            experience.ActivePreset = DesktopLayoutPreset.CenterClock;
            window.ApplyDesktopExperience(experience);
            SelectAlignment(ClockHorizontalAlignment.Left);
            AssertEqual(TextAlignment.Left, timeText.TextAlignment, "日期海报应可在居中时钟布局下左对齐");
            SelectAlignment(ClockHorizontalAlignment.FollowLayout);
            AssertEqual(TextAlignment.Center, timeText.TextAlignment, "恢复跟随布局时，日期海报应重新居中");
            experience.ActivePreset = DesktopLayoutPreset.QuietInformation;
            window.ApplyDesktopExperience(experience);

            // VerticalDigits: stacked HH/MM digits sit left of date block and use primary ink when unbacked.
            SelectLayout(ClockLayoutPreset.VerticalDigits);
            Assert(timeText.Text.Contains('\n'), "纵向数字版式时间应按时/分两行纵向排列");
            AssertEqual(byte.MaxValue, ((System.Windows.Media.SolidColorBrush)dateText.Foreground).Color.A,
                "无底板下纵向数字日期应使用主墨色");
            timeRect = timeText.TransformToAncestor(window).TransformBounds(new Rect(0, 0, timeText.ActualWidth, timeText.ActualHeight));
            dateRect = dateText.TransformToAncestor(window).TransformBounds(new Rect(0, 0, dateText.ActualWidth, dateText.ActualHeight));
            Assert(timeRect.Left < dateRect.Left, "纵向数字版式日期应位于纵向数字右侧");

            // ShowLunarDate toggle turns lunar date off/on, and ShowDate disables the lunar switch.
            showLunarBox.IsChecked = false;
            Pump();
            Assert(!state.ShowLunarDate && !dateText.Text.Contains("农历", StringComparison.Ordinal),
                "关闭显示农历后日期文本应去掉农历");
            showLunarBox.IsChecked = true;
            Pump();
            Assert(state.ShowLunarDate && dateText.Text.Contains("农历", StringComparison.Ordinal),
                "重新开启显示农历后日期文本应恢复农历");

            // ShowDate toggle hides DateText across presets without hiding time.
            showDateBox.IsChecked = false;
            Pump();
            Assert(!showLunarBox.IsEnabled, "关闭显示日期后显示农历开关应置灰");
            AssertEqual(Visibility.Collapsed, dateText.Visibility, "关闭显示日期后日期文本应隐藏");
            AssertEqual(Visibility.Visible, timeText.Visibility, "关闭显示日期后时间仍应显示");
            showDateBox.IsChecked = true;
            Pump();
            Assert(showLunarBox.IsEnabled, "重新开启显示日期后显示农历开关应恢复可用");
            AssertEqual(Visibility.Visible, dateText.Visibility, "重新开启显示日期后日期应恢复显示");

            // Switching clock layout keeps user custom FontFamily, FontScale, and TextColor intact.
            clockComponent.FontFamily = "Segoe UI";
            clockComponent.FontScale = 1.4d;
            clockComponent.Appearance.TextColor = "#D8E8FF";
            window.ApplyDesktopExperience(experience);
            Pump();

            var scaledVerticalSize = timeText.FontSize;
            SelectLayout(ClockLayoutPreset.DatePoster);
            AssertEqual("Segoe UI", clockComponent.FontFamily, "切换时钟版式不得改写用户已选字体");
            AssertEqual(1.4d, clockComponent.FontScale, "切换时钟版式不得改写用户已选字号缩放");
            AssertEqual("#D8E8FF", clockComponent.Appearance.TextColor, "切换时钟版式不得改写用户文字颜色");
            Assert(timeText.FontFamily.Source.Equals("Segoe UI", StringComparison.OrdinalIgnoreCase)
                && dateText.FontFamily.Source.Equals("Segoe UI", StringComparison.OrdinalIgnoreCase),
                "用户自定义字体应覆盖版式默认字体");
            Assert(scaledVerticalSize > 54d && timeText.FontSize > 48d, "用户字号缩放应在各版式上持续生效");
        }
        finally
        {
            settings.Close();
            window.CanClose = true;
            window.Close();
            Pump();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    public static void TimeDateAndGreetingDoNotSubscribe1HzSampler()
    {
        var experience = DesktopExperienceRules.CreateDefault();
        var sampler = new CountingSamplingService();
        var weatherService = new WeatherService(new StubHttpMessageHandler());
        var timeCard = new InformationWidgetWindow(
            new InformationWidgetState(), sampler, weatherService, experience, DesktopComponentKind.TimeDate);
        var greetingCard = new InformationWidgetWindow(
            new InformationWidgetState(), sampler, weatherService, experience, DesktopComponentKind.Greeting);
        var weatherCard = new InformationWidgetWindow(
            new InformationWidgetState(), sampler, weatherService, experience, DesktopComponentKind.Weather);
        ShowAndPump(timeCard);
        ShowAndPump(greetingCard);
        ShowAndPump(weatherCard);
        try
        {
            AssertEqual(0, sampler.SubscriberCount, "独立时间/问候/天气卡片不应订阅 1Hz 性能采样事件");

            var perfCard = new InformationWidgetWindow(
                new InformationWidgetState(), sampler, weatherService, experience, DesktopComponentKind.Performance);
            ShowAndPump(perfCard);
            try
            {
                AssertEqual(1, sampler.SubscriberCount, "独立性能卡片应订阅 1Hz 性能采样事件");
            }
            finally
            {
                perfCard.CanClose = true;
                perfCard.Close();
                Pump();
            }
            AssertEqual(0, sampler.SubscriberCount, "性能卡片关闭后应取消订阅 1Hz 性能采样事件");
        }
        finally
        {
            foreach (var window in new[] { timeCard, greetingCard, weatherCard })
            {
                window.CanClose = true;
                window.Close();
            }
            Pump();
        }
    }

    private sealed class CountingSamplingService : IPerformanceSamplingService
    {
        public int SubscriberCount { get; private set; }

        public event Action<PerformanceSnapshot>? Sampled
        {
            add => SubscriberCount++;
            remove => SubscriberCount = Math.Max(0, SubscriberCount - 1);
        }

        public bool IsRunning { get; private set; } = true;
        public PerformanceSamplingMode Mode => PerformanceSamplingMode.Normal;
        public TimeSpan CurrentInterval => TimeSpan.FromSeconds(1);
        public void Start() => IsRunning = true;
        public void SetMode(PerformanceSamplingMode mode) { }
        public void Stop() => IsRunning = false;
        public void Dispose() => Stop();
    }
}
