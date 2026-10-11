using static BingLan.InformationTests.TestSupport;

namespace BingLan.InformationTests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var failures = new List<string>();
        AppContext.SetSwitch(BingLan.App.App.SuppressCoordinatorStartupSwitch, true);
        var app = new BingLan.App.App();
        app.InitializeComponent();

        Run("时间格式 12/24 小时与边界", CoreLogicTests.TimeFormatting, failures);
        Run("日期与星期格式", CoreLogicTests.DateFormatting, failures);
        Run("问候时段与边界", CoreLogicTests.GreetingPeriods, failures);
        Run("问候称呼处理", CoreLogicTests.GreetingWithName, failures);
        Run("采样间隔规则", CoreLogicTests.SamplingIntervals, failures);
        Run("百分比截断规则", CoreLogicTests.PercentClamping, failures);
        Run("吞吐计算规则", CoreLogicTests.ThroughputRules, failures);
        Run("速率格式化", CoreLogicTests.RateFormatting, failures);
        Run("天气城市与坐标守卫", CoreLogicTests.WeatherCityGuard, failures);

        Run("天气成功解析（离线桩）", WeatherOfflineTests.SuccessParse, failures);
        Run("天气超时（离线桩）", WeatherOfflineTests.TimeoutFailure, failures);
        Run("天气错误响应（离线桩）", WeatherOfflineTests.ErrorResponse, failures);
        Run("天气缓存回退（离线桩）", WeatherOfflineTests.CacheFallback, failures);
        Run("切换城市不复用旧缓存", WeatherOfflineTests.LocationChangeDropsOldCache, failures);
        Run("天气退避序列", WeatherOfflineTests.BackoffSchedule, failures);
        Run("退避阻止快速重试", WeatherOfflineTests.BackoffBlocksRapidRetry, failures);
        Run("空城市不发起请求", WeatherOfflineTests.EmptyCitySkipsNetwork, failures);
        Run("天气请求 URL 结构", WeatherOfflineTests.RequestUrlShape, failures);
        Run("天气代码映射", WeatherOfflineTests.WeatherCodeMapping, failures);
        Run("天气非法 JSON（离线桩）", WeatherOfflineTests.MalformedJson, failures);

        Run("城市搜索成功解析与请求结构", CitySearchTests.SuccessParseAndUrl, failures);
        Run("城市搜索短关键词不联网", CitySearchTests.ShortQuerySkipsNetwork, failures);
        Run("城市搜索空结果", CitySearchTests.EmptyResults, failures);
        Run("城市搜索请求间隔", CitySearchTests.ConsecutiveSearchesAreSpaced, failures);
        Run("城市搜索失败安静回退", CitySearchTests.FailuresAreQuiet, failures);
        Run("城市搜索超时安静回退", CitySearchTests.TimeoutIsQuiet, failures);

        Run("内嵌城市库加载", CityLibraryTests.EmbeddedLibraryLoads, failures);
        Run("城市库搜索排序与同名消歧", CityLibraryTests.SearchRanksAndDisambiguates, failures);
        Run("城市库数量上限与非法输入", CityLibraryTests.SearchLimitsAndRejects, failures);
        Run("城市查找本地优先不联网", CityLibraryTests.LookupPrefersLocalLibrary, failures);
        Run("城市查找本地无匹配转在线", CityLibraryTests.LookupFallsBackOnline, failures);

        Run("性能采样启动、降频、停止与释放", SamplingServiceTests.StartSampleThrottleStopDispose, failures);

        Run("信息组件性能数值常显无需悬停", WidgetWindowTests.MetricsAlwaysVisibleWithoutHover, failures);
        Run("信息组件默认高度完整显示天气内容", WidgetWindowTests.DefaultHeightShowsWeatherDetails, failures);
        Run("独立信息表面显示与保存位置", WidgetWindowTests.IsolatedSurfaceVisibilityAndPlacement, failures);
        Run("时间细线选项与右键锁定", WidgetWindowTests.ClockDividerAndLockMenu, failures);
        Run("裸文字表面白色默认与对比度阴影", WidgetWindowTests.NakedTextContrastDefaults, failures);
        Run("信息组件天气成功管线", WidgetWindowTests.WeatherPipelineUpdatesWindow, failures);
        Run("信息组件天气失败状态", WidgetWindowTests.WeatherFailureShowsStatus, failures);
        Run("天气失败后按退避自动重试", WidgetWindowTests.WeatherRetriesAfterBackoffWithoutManualRefresh, failures);
        Run("信息组件城市设置入口", WidgetWindowTests.CitySettingsEntryRaisesRequest, failures);
        Run("切换城市忽略旧天气返回", WidgetWindowTests.CityChangeIgnoresStaleWeather, failures);
        Run("统一设置搜索选择与保存", SettingsWindowTests.SearchSelectAndSave, failures);
        Run("三套布局预设与组件开关可应用", SettingsWindowTests.ApplyPresetAndComponents, failures);
        Run("通用页显示新版本", SettingsWindowTests.UpdatePageShowsFoundRelease, failures);
        Run("顶端信息条设置页开关与写入", SettingsWindowTests.TopBarSettingsPage, failures);

        if (failures.Count > 0)
        {
            Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
            return 1;
        }

        Console.WriteLine("全部信息组件测试通过。");
        return 0;
    }
}
