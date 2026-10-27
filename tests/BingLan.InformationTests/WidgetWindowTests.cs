using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using BingLan.App.Windows;
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
                Require<TextBlock>(window, "WeatherRangeText").Text.Contains("湿度 62%"),
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
            var updated = Require<TextBlock>(window, "WeatherUpdatedText");
            Assert(updated.Text.Contains("更新于"), "天气更新时间常驻显示");
            Assert(window.FindName("WeatherSourceText") is null, "天气卡片不包含供应商说明");
            var detailsBottom = updated
                .TranslatePoint(new Point(0d, updated.ActualHeight), window)
                .Y;
            Assert(
                detailsBottom <= window.ActualHeight - 8d,
                $"默认高度下天气内容被裁切：底部 {detailsBottom:0.#}，窗口 {window.ActualHeight:0.#}");
        }
        finally
        {
            window.CanClose = true;
            window.Close();
            Pump();
        }
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

    public static void NakedTextContrastDefaults()
    {
        var experience = DesktopExperienceRules.CreateDefault();
        var timeDate = experience.GetComponent(DesktopComponentKind.TimeDate);
        AssertEqual("#FFFFFF", timeDate.Appearance.TextColor,
            "时间日期默认文字色应为白色");
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
            Assert(timeText.Effect is System.Windows.Media.Effects.DropShadowEffect,
                "时间文字缺少对比度阴影");
            Assert(
                Require<TextBlock>(window, "DateText").Effect
                    is System.Windows.Media.Effects.DropShadowEffect,
                "日期文字缺少对比度阴影");
            Assert(
                Require<TextBlock>(window, "GreetingText").Effect
                    is System.Windows.Media.Effects.DropShadowEffect,
                "问候文字缺少对比度阴影");
            Assert(
                timeText.Foreground is System.Windows.Media.SolidColorBrush
                {
                    Color: { R: 255, G: 255, B: 255 }
                },
                "时间文字未使用白色默认前景");
        }
        finally
        {
            window.CanClose = true;
            window.Close();
            Pump();
        }
    }

}
