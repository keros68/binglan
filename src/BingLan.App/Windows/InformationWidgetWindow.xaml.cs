using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BingLan.Core.Models;
using BingLan.Core.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace BingLan.App.Windows;

public partial class InformationWidgetWindow : WidgetWindowBase
{
    private readonly IPerformanceSamplingService _sampler;
    private readonly WeatherService _weather;
    private readonly bool _usesLegacyAppearance;
    private readonly DesktopComponentKind? _componentKind;
    private DesktopExperienceState _experience;
    private DateTimeOffset? _lastWeatherSuccess;
    private bool _weatherInFlight;

    public InformationWidgetWindow(
        InformationWidgetState state,
        IPerformanceSamplingService sampler,
        WeatherService weather,
        DesktopExperienceState? experience = null,
        DesktopComponentKind? componentKind = null)
    {
        State = state;
        _sampler = sampler;
        _weather = weather;
        _usesLegacyAppearance = experience is null;
        _componentKind = componentKind;
        _experience = experience ?? CreateExperienceFromLegacyAppearance(state);
        InitializeComponent();
        DataContext = state;
        ApplyAppearance(state.Appearance);
        ApplyPlacement(state.Placement, state.IsLocked);
        WidgetCornerRadius = state.CornerRadius;
        ApplyDesktopExperience(_experience);

        _sampler.Sampled += OnSampled;
        Closed += (_, _) => _sampler.Sampled -= OnSampled;

        UpdateClock();
        UpdatePerformance(new PerformanceSnapshot(0d, 0d, 0d, 0d, DateTimeOffset.Now));
        var cachedWeather = State.HasWeatherLocation
            ? _weather.GetLastSnapshot(
                State.WeatherCity,
                State.WeatherLatitude!.Value,
                State.WeatherLongitude!.Value)
            : null;
        if (cachedWeather is not null)
        {
            UpdateWeatherView(cachedWeather);
        }
        else
        {
            UpdateWeatherView(new WeatherSnapshot(
                State.HasWeatherLocation ? WeatherStatus.Failed : WeatherStatus.NoCity,
                State.WeatherCity.Trim(), null, null, null, null, "", false, null,
                State.HasWeatherLocation ? "等待首次更新" : null));
        }

        Loaded += (_, _) =>
        {
            // 采样服务由宿主共享拥有；窗口只在尚未启动时启动，不在关闭时停止。
            if (!_sampler.IsRunning)
            {
                _sampler.Start();
            }
        };
    }

    public InformationWidgetState State { get; }
    public DesktopComponentKind? ComponentKind => _componentKind;

    private static DesktopExperienceState CreateExperienceFromLegacyAppearance(
        InformationWidgetState state)
    {
        var experience = DesktopExperienceRules.CreateDefault();
        foreach (var kind in new[]
                 {
                     DesktopComponentKind.TimeDate,
                     DesktopComponentKind.Greeting,
                     DesktopComponentKind.Weather,
                     DesktopComponentKind.Performance
                 })
        {
            var component = experience.GetComponent(kind);
            component.Appearance.BackgroundColor = state.Appearance.BackgroundColor;
            component.Appearance.BackgroundOpacity = state.Appearance.BackgroundOpacity;
            component.Appearance.TextColor = state.Appearance.TextColor;
            component.CornerRadius = state.CornerRadius;
        }
        return experience;
    }

    public void ApplyDesktopExperience(DesktopExperienceState experience)
    {
        DesktopExperienceRules.Normalize(experience);
        _experience = experience;

        var timeDate = experience.GetComponent(DesktopComponentKind.TimeDate);
        var greeting = experience.GetComponent(DesktopComponentKind.Greeting);
        var performance = experience.GetComponent(DesktopComponentKind.Performance);
        var weather = experience.GetComponent(DesktopComponentKind.Weather);
        var lowChrome = experience.ActivePreset != DesktopLayoutPreset.GlacierWorkbench;
        var isolatedComponent = _componentKind is { } isolatedKind
            ? experience.GetComponent(isolatedKind)
            : null;

        var showTimeDate = timeDate.IsVisible &&
            (_componentKind is null or DesktopComponentKind.TimeDate);
        var showGreeting = greeting.IsVisible &&
            (_componentKind is null or DesktopComponentKind.Greeting);
        var showPerformance = performance.IsVisible &&
            (_componentKind is null or DesktopComponentKind.Performance);
        var showWeather = weather.IsVisible &&
            (_componentKind is null or DesktopComponentKind.Weather);

        ClockPanel.Visibility = showTimeDate || showGreeting
            ? Visibility.Visible
            : Visibility.Collapsed;
        TimeText.Visibility = showTimeDate ? Visibility.Visible : Visibility.Collapsed;
        DateText.Visibility = showTimeDate ? Visibility.Visible : Visibility.Collapsed;
        GreetingText.Visibility = showGreeting ? Visibility.Visible : Visibility.Collapsed;
        PerformanceCard.Visibility = showPerformance
            ? Visibility.Visible
            : Visibility.Collapsed;
        WeatherCard.Visibility = showWeather ? Visibility.Visible : Visibility.Collapsed;

        if (isolatedComponent is not null)
        {
            // A chosen font replaces the interface font for all of this card's text.
            if (isolatedComponent.FontFamily is { } fontName)
            {
                FontFamily = InstalledFontCatalog.Create(InstalledFontCatalog.Resolve(fontName));
            }
            else
            {
                ClearValue(FontFamilyProperty);
            }
            MinWidth = 180d;
            MinHeight = 72d;
            Title = DesktopExperienceRules.GetComponentName(isolatedComponent.Kind);
            BodySurface.Background = CreateComponentBackgroundBrush(isolatedComponent);
            WidgetCornerRadius = isolatedComponent.CornerRadius;
        }

        var bigClock = experience.ActivePreset == DesktopLayoutPreset.CenterClock
            && _componentKind == DesktopComponentKind.TimeDate;
        TimeText.FontSize = (bigClock ? 96d : 36d * (lowChrome ? 1.3d : 1d)) * timeDate.FontScale;
        var timeFont = timeDate.FontFamily is { } timeFontName ? InstalledFontCatalog.Resolve(timeFontName) : null;
        var defaultTimeWeight = bigClock ? FontWeights.Light : FontWeights.Bold;
        TimeText.FontWeight = timeFont is null
            ? defaultTimeWeight
            : InstalledFontCatalog.WeightOf(timeFont, defaultTimeWeight);
        var clockAlignment = bigClock ? TextAlignment.Center : TextAlignment.Left;
        TimeText.TextAlignment = clockAlignment;
        DateText.TextAlignment = clockAlignment;
        DateText.FontSize = 13d * timeDate.FontScale;
        var isolatedGreeting = _componentKind == DesktopComponentKind.Greeting;
        GreetingText.FontSize = (isolatedGreeting ? 34d : 14d) * greeting.FontScale;
        var defaultGreetingWeight = isolatedGreeting ? FontWeights.Light : FontWeights.Normal;
        GreetingText.FontWeight = greeting.FontFamily is { } greetingFontName
            ? InstalledFontCatalog.WeightOf(InstalledFontCatalog.Resolve(greetingFontName), defaultGreetingWeight)
            : defaultGreetingWeight;
        GreetingText.TextAlignment = isolatedGreeting ? TextAlignment.Center : TextAlignment.Left;
        GreetingText.HorizontalAlignment = isolatedGreeting
            ? System.Windows.HorizontalAlignment.Stretch
            : System.Windows.HorizontalAlignment.Left;
        ClockPanel.VerticalAlignment = isolatedGreeting
            ? System.Windows.VerticalAlignment.Center
            : System.Windows.VerticalAlignment.Top;
        TimeText.Foreground = GetComponentTextBrush(timeDate);
        ClockDivider.Visibility = timeDate.ShowDivider && showTimeDate ? Visibility.Visible : Visibility.Collapsed;
        ClockDivider.Height = timeDate.DividerThickness;
        ClockDivider.RadiusY = timeDate.DividerThickness / 2d;
        ClockDivider.RadiusX = timeDate.DividerThickness / 2d;
        if (GetComponentTextBrush(timeDate) is SolidColorBrush { Color: var ink })
        {
            var faded = Color.FromArgb(0, ink.R, ink.G, ink.B);
            ClockDivider.Fill = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(faded, 0d),
                    new(Color.FromArgb(200, ink.R, ink.G, ink.B), 0.5d),
                    new(faded, 1d)
                },
                new System.Windows.Point(0, 0),
                new System.Windows.Point(1, 0));
        }
        DateText.Foreground = GetComponentTextBrush(timeDate);
        GreetingText.Foreground = GetComponentTextBrush(greeting);
        SettingsButton.Foreground = GetComponentTextBrush(isolatedComponent ?? timeDate);

        ApplyTextBrush(PerformanceCard, GetComponentTextBrush(performance));
        PerformanceCard.Background = _componentKind == DesktopComponentKind.Performance
            ? System.Windows.Media.Brushes.Transparent
            : CreateComponentBackgroundBrush(performance);
        PerformanceCard.CornerRadius = new CornerRadius(performance.CornerRadius);
        PerformanceCard.Padding = GetCardPadding(performance.Density, false);
        CpuValueText.FontSize = 18d * performance.FontScale;
        RamValueText.FontSize = 18d * performance.FontScale;
        UploadValueText.FontSize = 12d * performance.FontScale;
        DownloadValueText.FontSize = 12d * performance.FontScale;
        CpuLabelText.FontSize = 10d * performance.FontScale;
        RamLabelText.FontSize = 10d * performance.FontScale;
        NetworkLabelText.FontSize = 10d * performance.FontScale;
        UploadLabelText.FontSize = 12d * performance.FontScale;
        DownloadLabelText.FontSize = 12d * performance.FontScale;

        ApplyTextBrush(WeatherCard, GetComponentTextBrush(weather));
        WeatherCard.Background = _componentKind == DesktopComponentKind.Weather
            ? System.Windows.Media.Brushes.Transparent
            : CreateComponentBackgroundBrush(weather);
        WeatherCard.CornerRadius = new CornerRadius(weather.CornerRadius);
        WeatherCard.Padding = GetCardPadding(weather.Density, true);
        WeatherCityText.FontSize = 16d * weather.FontScale;
        WeatherTempText.FontSize = 46d * weather.FontScale;
        WeatherConditionText.FontSize = 19d * weather.FontScale;
        WeatherRangeText.FontSize = 13d * weather.FontScale;
        WeatherUpdatedText.FontSize = 12d * weather.FontScale;
        WeatherStatusText.FontSize = 11d * weather.FontScale;
    }

    private Brush GetComponentTextBrush(DesktopComponentState component) =>
        _usesLegacyAppearance
            ? WidgetTextBrush
            : CreateBrush(component.Appearance.TextColor, 1d);

    private static SolidColorBrush CreateComponentBackgroundBrush(
        DesktopComponentState component) =>
        CreateBrush(
            component.Appearance.BackgroundColor,
            component.Appearance.BackgroundOpacity);

    private static SolidColorBrush CreateBrush(string colorValue, double opacity)
    {
        var color = (Color)ColorConverter.ConvertFromString(colorValue)!;
        color.A = (byte)Math.Round(Math.Clamp(opacity, 0d, 1d) * byte.MaxValue);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Thickness GetCardPadding(
        DesktopInformationDensity density,
        bool weather) => density switch
        {
            DesktopInformationDensity.Comfortable => weather
                ? new Thickness(14, 10, 14, 10)
                : new Thickness(14, 11, 14, 11),
            DesktopInformationDensity.Compact => weather
                ? new Thickness(9, 5, 9, 5)
                : new Thickness(10, 6, 10, 6),
            _ => weather
                ? new Thickness(12, 7, 12, 7)
                : new Thickness(12, 9, 12, 9)
        };

    private static void ApplyTextBrush(DependencyObject root, Brush brush)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            switch (child)
            {
                case System.Windows.Controls.TextBlock { Tag: "Accent" }:
                    break;
                case System.Windows.Controls.TextBlock textBlock:
                    textBlock.Foreground = brush;
                    break;
                case System.Windows.Controls.Control control:
                    control.Foreground = brush;
                    break;
            }
            ApplyTextBrush(child, brush);
        }
    }

    protected override void OnLockChanged(bool locked)
    {
        State.IsLocked = locked;
    }

    public void CaptureState()
    {
        if (_componentKind is { } kind)
        {
            var component = _experience.GetComponent(kind);
            CopyPlacementTo(component.Placement);
            component.CornerRadius = WidgetCornerRadius;
        }
        else
        {
            CopyPlacementTo(State.Placement);
            State.CornerRadius = WidgetCornerRadius;
        }
    }

    private void OnSampled(PerformanceSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded)
            {
                return;
            }
            UpdateClock();
            UpdatePerformance(snapshot);
            if (_componentKind is null or DesktopComponentKind.Weather)
            {
                MaybeRefreshWeather(false);
            }
        });
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        TimeText.Text = GreetingService.FormatTime(now, State.Use24HourClock);
        DateText.Text = GreetingService.FormatDate(now);
        GreetingText.Text = GreetingService.BuildGreeting(now, State.GreetingName);
    }

    // 数值直接更新，不使用任何动画，天然遵守系统“减少动画”偏好；
    // CPU、RAM、上传、下载常驻可见，不以悬停为可见条件。
    private void UpdatePerformance(PerformanceSnapshot snapshot)
    {
        CpuValueText.Text = PerformanceSamplingRules.FormatPercent(snapshot.CpuPercent);
        RamValueText.Text = PerformanceSamplingRules.FormatPercent(snapshot.MemoryPercent);
        CpuBarScale.ScaleX = PerformanceSamplingRules.ClampPercent(snapshot.CpuPercent) / 100d;
        RamBarScale.ScaleX = PerformanceSamplingRules.ClampPercent(snapshot.MemoryPercent) / 100d;
        UploadValueText.Text = PerformanceSamplingRules.FormatRate(snapshot.UploadBytesPerSecond);
        DownloadValueText.Text = PerformanceSamplingRules.FormatRate(snapshot.DownloadBytesPerSecond);
    }

    public void RefreshConfiguredValues(bool refreshWeather)
    {
        UpdateClock();
        if (!refreshWeather ||
            (_componentKind is not null and not DesktopComponentKind.Weather))
        {
            return;
        }

        _lastWeatherSuccess = null;
        UpdateWeatherView(new WeatherSnapshot(
            State.HasWeatherLocation ? WeatherStatus.Failed : WeatherStatus.NoCity,
            State.WeatherCity.Trim(), null, null, null, null, "", false, null,
            State.HasWeatherLocation ? "等待首次更新" : null));
        MaybeRefreshWeather(true);
    }

    private void WeatherRefresh_Click(object sender, RoutedEventArgs e) => MaybeRefreshWeather(true);

    private void MaybeRefreshWeather(bool manual)
    {
        if (!State.HasWeatherLocation)
        {
            UpdateWeatherView(new WeatherSnapshot(
                WeatherStatus.NoCity, "", null, null, null, null, "", false, null, null));
            return;
        }
        if (_weatherInFlight)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        if (!_weather.ShouldAttemptRefresh(
                now,
                State.WeatherCity,
                State.WeatherLatitude!.Value,
                State.WeatherLongitude!.Value))
        {
            WeatherStatusText.Text = _weather.NextRetryAt is { } retryAt
                ? $"上次刷新失败，{retryAt.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)} 后重试"
                : "上次刷新失败，稍后重试";
            return;
        }
        // 只有成功后才等满刷新间隔；失败后的重试时机由服务的退避决定。
        if (!manual &&
            _lastWeatherSuccess is { } last &&
            now - last < WeatherService.SuccessRefreshInterval)
        {
            return;
        }

        _weatherInFlight = true;
        WeatherStatusText.Text = "更新中…";
        _ = RefreshWeatherAsync();
    }

    // 网络请求在线程池上完成，UI 线程只负责展示结果。
    private async Task RefreshWeatherAsync()
    {
        var requestedCity = State.WeatherCity;
        var requestedLatitude = State.WeatherLatitude!.Value;
        var requestedLongitude = State.WeatherLongitude!.Value;
        WeatherSnapshot snapshot;
        try
        {
            snapshot = await _weather.RefreshAsync(
                    requestedCity,
                    requestedLatitude,
                    requestedLongitude)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            snapshot = new WeatherSnapshot(
                WeatherStatus.Failed, requestedCity.Trim(),
                null, null, null, null, "", false, null,
                $"刷新异常：{exception.GetType().Name}");
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            _weatherInFlight = false;
            if (!IsLoaded)
            {
                return;
            }

            var locationIsCurrent =
                string.Equals(State.WeatherCity, requestedCity, StringComparison.Ordinal) &&
                State.WeatherLatitude == requestedLatitude &&
                State.WeatherLongitude == requestedLongitude;
            if (locationIsCurrent)
            {
                if (snapshot.Status == WeatherStatus.Fresh)
                {
                    _lastWeatherSuccess = DateTimeOffset.Now;
                }
                UpdateWeatherView(snapshot);
            }
            else
            {
                MaybeRefreshWeather(true);
            }
        });
    }

    private void UpdateWeatherView(WeatherSnapshot snapshot)
    {
        if (snapshot.Status == WeatherStatus.NoCity)
        {
            WeatherCityText.Text = "未设置城市";
            WeatherTempText.Text = "--";
            WeatherConditionText.Text = "";
            WeatherRangeText.Text = "";
            WeatherUpdatedText.Text = "";
            WeatherStatusText.Text = "";
            return;
        }

        // "黑河 · 黑龙江 · 中国" reads as just "黑河"; the full place is in the tooltip.
        WeatherCityText.Text = snapshot.City.Split(" · ")[0];
        WeatherCityText.ToolTip = snapshot.City;
        WeatherTempText.Text = snapshot.TemperatureCelsius is { } temperature
            ? $"{temperature:0.#}°"
            : "--";
        WeatherConditionText.Text = snapshot.Condition;
        var high = snapshot.DailyHighCelsius is { } h ? $"{h:0.#}°" : "--";
        var low = snapshot.DailyLowCelsius is { } l ? $"{l:0.#}°" : "--";
        var humidity = snapshot.HumidityPercent is { } value ? $"{value}%" : "--";
        WeatherRangeText.Text = $"最高 {high}  最低 {low}  湿度 {humidity}";
        WeatherUpdatedText.Text = snapshot.UpdatedAt is { } updatedAt
            ? $"更新于 {updatedAt.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)}"
            : "";
        WeatherStatusText.Text = snapshot.Status switch
        {
            WeatherStatus.Fresh => "",
            WeatherStatus.CachedFallback => $"刷新失败（{snapshot.ErrorMessage}），显示缓存数据",
            WeatherStatus.Failed => $"刷新失败：{snapshot.ErrorMessage}",
            _ => ""
        };
    }
}
