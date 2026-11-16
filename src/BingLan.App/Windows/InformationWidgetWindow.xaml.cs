using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BingLan.Core.Models;
using BingLan.Core.Services;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Grid = System.Windows.Controls.Grid;

namespace BingLan.App.Windows;

public partial class InformationWidgetWindow : WidgetWindowBase
{
    private readonly IPerformanceSamplingService _sampler;
    private readonly WeatherService _weather;
    private readonly bool _usesLegacyAppearance;
    private readonly DesktopComponentKind? _componentKind;
    private DesktopExperienceState _experience;
    private readonly DispatcherTimer _ambientTimer;
    private DateTimeOffset? _lastWeatherSuccess;
    private bool _weatherInFlight;
    private bool _systemEventsHooked;

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
        _ambientTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _ambientTimer.Tick += OnAmbientTick;
        Closed += (_, _) =>
        {
            _sampler.Sampled -= OnSampled;
            _ambientTimer.Stop();
            if (_systemEventsHooked)
            {
                _systemEventsHooked = false;
                SystemEvents.TimeChanged -= OnSystemTimeChanged;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
        };

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
            // 采样服务由宿主共享拥有；需要性能数据的卡片在尚未启动时启动，纯时间/问候/天气卡片使用分钟对齐低频刷新。
            if (_componentKind is null or DesktopComponentKind.Performance && !_sampler.IsRunning)
            {
                _sampler.Start();
            }
            if (!_systemEventsHooked)
            {
                _systemEventsHooked = true;
                SystemEvents.TimeChanged += OnSystemTimeChanged;
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
            }
            if (_componentKind is null or DesktopComponentKind.Weather && !_sampler.IsRunning)
            {
                MaybeRefreshWeather(false);
            }
            ArmAmbientTimer();
        };
    }

    public InformationWidgetState State { get; }
    public DesktopComponentKind? ComponentKind => _componentKind;

    protected override void ApplyEffectiveCardShadow(CardShadow shadow)
    {
        if (_componentKind is DesktopComponentKind.TimeDate or DesktopComponentKind.Greeting
            && (_experience?.GetComponent(_componentKind.Value).Appearance.BackgroundOpacity
                ?? WidgetAppearance.BackgroundOpacity) <= WidgetAppearanceRules.MinimumBackgroundOpacity + 0.005d)
        {
            base.ApplyEffectiveCardShadow(CardShadow.None);
            return;
        }

        base.ApplyEffectiveCardShadow(shadow);
    }

    protected override void OnAppearanceRefreshed()
    {
        if (_experience is not null && Surface is not null)
        {
            ApplyDesktopExperience(_experience);
        }
    }

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

        RenderedCardLook? isolatedRender = null;
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
            MinHeight = isolatedComponent.Kind == DesktopComponentKind.Weather
                ? DesktopExperienceRules.CalculateWeatherMinHeight(weather.FontScale, weather.Density)
                : 72d;
            Title = DesktopExperienceRules.GetComponentName(isolatedComponent.Kind);
            isolatedRender = DesktopStyleRules.ResolveRenderedCard(isolatedComponent, CurrentStyle);
            BodySurface.Background = AccessibilityThemeManager.IsHighContrastEnabled
                ? SystemColors.WindowBrush
                : CreateBrush(isolatedRender.BackgroundColor, isolatedRender.BackgroundOpacity);
            ApplyRenderedSurfaceOverrides(isolatedRender);
            ApplyEffectiveCardShadow(isolatedRender.EffectiveShadow);
            WidgetCornerRadius = isolatedComponent.CornerRadius;
        }
        else
        {
            MinWidth = 250d;
            MinHeight = DesktopExperienceRules.CalculateCombinedInformationMinHeight(
                experience,
                State.ClockLayout,
                State.ShowDate);
        }

        var (surfaceBorderBrush, surfaceBorderThickness) = isolatedRender is not null
            ? ResolveSurfaceBorder(isolatedRender.BackgroundColor, isolatedRender.BackgroundOpacity)
            : (WidgetBorderBrush, WidgetBorderThickness);
        Surface.BorderBrush = surfaceBorderBrush;
        Surface.BorderThickness = surfaceBorderThickness;

        ApplyClockLayout(timeDate, greeting, showTimeDate, showGreeting, lowChrome);
        SettingsButton.Foreground = GetComponentTextBrush(isolatedComponent ?? timeDate);

        var isIsolatedPerformance = _componentKind == DesktopComponentKind.Performance;
        var performanceRender = isIsolatedPerformance
            ? null
            : DesktopStyleRules.ResolveRenderedCard(
                _usesLegacyAppearance ? WidgetAppearance : performance.Appearance,
                CurrentStyle);
        var performanceBackingOpacity = isIsolatedPerformance
            ? isolatedRender!.BackgroundOpacity
            : performanceRender!.BackgroundOpacity;
        ApplyTextBrush(
            PerformanceCard,
            GetComponentTextBrush(performance),
            GetComponentSecondaryTextBrush(performance, performanceBackingOpacity));
        PerformanceCard.Background = isIsolatedPerformance
            ? System.Windows.Media.Brushes.Transparent
            : AccessibilityThemeManager.IsHighContrastEnabled
                ? SystemColors.ControlBrush
                : CreateBrush(performanceRender!.BackgroundColor, performanceRender.BackgroundOpacity);
        var (performanceBorderBrush, performanceBorderThickness) = isIsolatedPerformance
            ? (System.Windows.Media.Brushes.Transparent, new Thickness(0))
            : ResolveSurfaceBorder(performanceRender!.BackgroundColor, performanceRender.BackgroundOpacity);
        PerformanceCard.BorderBrush = performanceBorderBrush;
        PerformanceCard.BorderThickness = performanceBorderThickness;
        PerformanceCard.Effect = isIsolatedPerformance
            || AccessibilityThemeManager.IsHighContrastEnabled
            || BodySurface.Effect is not null
            ? null
            : CreateCardShadowEffect(performanceRender!.EffectiveShadow);
        PerformanceCard.CornerRadius = new CornerRadius(performance.CornerRadius);
        PerformanceCard.Padding = GetCardPadding(performance.Density, false);
        CpuValueText.FontSize = 19d * performance.FontScale;
        RamValueText.FontSize = 19d * performance.FontScale;
        UploadValueText.FontSize = 12d * performance.FontScale;
        DownloadValueText.FontSize = 12d * performance.FontScale;
        CpuLabelText.FontSize = 11d * performance.FontScale;
        RamLabelText.FontSize = 11d * performance.FontScale;
        NetworkLabelText.FontSize = 11d * performance.FontScale;
        UploadLabelText.FontSize = 12d * performance.FontScale;
        DownloadLabelText.FontSize = 12d * performance.FontScale;

        var isIsolatedWeather = _componentKind == DesktopComponentKind.Weather;
        var weatherRender = isIsolatedWeather
            ? null
            : DesktopStyleRules.ResolveRenderedCard(
                _usesLegacyAppearance ? WidgetAppearance : weather.Appearance,
                CurrentStyle);
        var weatherBackingOpacity = isIsolatedWeather
            ? isolatedRender!.BackgroundOpacity
            : weatherRender!.BackgroundOpacity;
        ApplyTextBrush(
            WeatherCard,
            GetComponentTextBrush(weather),
            GetComponentSecondaryTextBrush(weather, weatherBackingOpacity));
        WeatherCard.Background = isIsolatedWeather
            ? System.Windows.Media.Brushes.Transparent
            : AccessibilityThemeManager.IsHighContrastEnabled
                ? SystemColors.ControlBrush
                : CreateBrush(weatherRender!.BackgroundColor, weatherRender.BackgroundOpacity);
        var (weatherBorderBrush, weatherBorderThickness) = isIsolatedWeather
            ? (System.Windows.Media.Brushes.Transparent, new Thickness(0))
            : ResolveSurfaceBorder(weatherRender!.BackgroundColor, weatherRender.BackgroundOpacity);
        WeatherCard.BorderBrush = weatherBorderBrush;
        WeatherCard.BorderThickness = weatherBorderThickness;
        WeatherCard.Effect = isIsolatedWeather
            || AccessibilityThemeManager.IsHighContrastEnabled
            || BodySurface.Effect is not null
            ? null
            : CreateCardShadowEffect(weatherRender!.EffectiveShadow);
        WeatherCard.CornerRadius = new CornerRadius(weather.CornerRadius);
        WeatherCard.Padding = GetCardPadding(weather.Density, true);
        WeatherCityText.FontSize = 15d * weather.FontScale;
        WeatherTempText.FontSize = 46d * weather.FontScale;
        WeatherIconPath.Width = 24d * weather.FontScale;
        WeatherIconPath.Height = 24d * weather.FontScale;
        WeatherConditionText.FontSize = 19d * weather.FontScale;
        var weatherMetaSize = 12d * weather.FontScale;
        WeatherHighText.FontSize = weatherMetaSize;
        WeatherLowText.FontSize = weatherMetaSize;
        WeatherHumidityText.FontSize = weatherMetaSize;
        WeatherUpdatedText.FontSize = weatherMetaSize;
        WeatherStatusText.FontSize = 11d * weather.FontScale;
        UpdateClock();
        RefreshMinimumSizeFloor();
    }

    private void ApplyClockLayout(
        DesktopComponentState timeDate,
        DesktopComponentState greeting,
        bool showTimeDate,
        bool showGreeting,
        bool lowChrome)
    {
        var isIsolatedTimeDate = _componentKind == DesktopComponentKind.TimeDate;
        var bigClock = _experience.ActivePreset == DesktopLayoutPreset.CenterClock
            && isIsolatedTimeDate;
        var preset = ClockLayoutRules.CoercePreset(State.ClockLayout);
        var effectiveBackgroundOpacity = isIsolatedTimeDate
            ? DesktopStyleRules.ResolveRenderedCard(timeDate, CurrentStyle).BackgroundOpacity
            : (_usesLegacyAppearance
                ? DesktopStyleRules.ResolveRenderedCard(WidgetAppearance, CurrentStyle).BackgroundOpacity
                : WidgetAppearanceRules.MinimumBackgroundOpacity);
        var spec = ClockLayoutRules.ResolveTypography(
            preset,
            timeDate.FontFamily,
            timeDate.FontScale,
            timeDate.Appearance.TextColor,
            bigClock,
            lowChrome,
            effectiveBackgroundOpacity);
        var hasCustomFont = !string.IsNullOrWhiteSpace(timeDate.FontFamily);

        ApplyClockElementFont(TimeText, spec.EffectiveTimeFontFamily, hasCustomFont, spec.TimeFontWeightOpenType, allowUiFallback: false);
        ApplyClockElementFont(DateText, spec.EffectiveDateFontFamily, hasCustomFont, spec.DateFontWeightOpenType, allowUiFallback: !hasCustomFont);
        ApplyClockElementFont(TimePeriodText, spec.EffectiveDateFontFamily, hasCustomFont, spec.DateFontWeightOpenType, allowUiFallback: !hasCustomFont);

        TimeText.FontSize = spec.TimeFontSize;
        DateText.FontSize = spec.DateFontSize;
        TimePeriodText.FontSize = spec.PeriodFontSize;
        if (spec.TimeLineHeight is { } timeLineHeight)
        {
            TimeText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            TimeText.LineHeight = timeLineHeight;
        }
        else
        {
            TimeText.LineStackingStrategy = LineStackingStrategy.MaxHeight;
            TimeText.LineHeight = double.NaN;
        }

        var hasVisibleCardBacking = isIsolatedTimeDate
            && timeDate.Appearance.BackgroundOpacity >= 0.05d;
        var textShadow = hasVisibleCardBacking || AccessibilityThemeManager.IsHighContrastEnabled
            ? null
            : (System.Windows.Media.Effects.Effect)FindResource("NakedTextContrastShadow");
        TimeText.Effect = textShadow;
        DateText.Effect = textShadow;
        TimePeriodText.Effect = textShadow;

        switch (spec.Arrangement)
        {
            case ClockDateArrangement.MinimalCentered:
                ClockLayoutGrid.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
                SetClockGridCell(TimeText, row: 0, rowSpan: 1, column: 0, columnSpan: 2);
                TimeText.TextAlignment = TextAlignment.Center;
                TimeText.VerticalAlignment = System.Windows.VerticalAlignment.Top;

                SetClockGridCell(DateMetaPanel, row: 1, rowSpan: 1, column: 0, columnSpan: 2);
                DateMetaPanel.Orientation = System.Windows.Controls.Orientation.Horizontal;
                DateMetaPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
                DateMetaPanel.VerticalAlignment = System.Windows.VerticalAlignment.Top;
                DateMetaPanel.Margin = new Thickness(0d, 4d, 0d, 0d);
                DateText.TextAlignment = TextAlignment.Center;
                TimePeriodText.TextAlignment = TextAlignment.Center;
                break;

            case ClockDateArrangement.DateAboveTime:
                ClockLayoutGrid.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
                SetClockGridCell(DateMetaPanel, row: 0, rowSpan: 1, column: 0, columnSpan: 2);
                DateMetaPanel.Orientation = System.Windows.Controls.Orientation.Horizontal;
                DateMetaPanel.HorizontalAlignment = spec.CenterHorizontally
                    ? System.Windows.HorizontalAlignment.Center
                    : System.Windows.HorizontalAlignment.Left;
                DateMetaPanel.VerticalAlignment = System.Windows.VerticalAlignment.Top;
                DateMetaPanel.Margin = new Thickness(0d, 0d, 0d, 2d);

                SetClockGridCell(TimeText, row: 1, rowSpan: 1, column: 0, columnSpan: 2);
                var posterAlign = spec.CenterHorizontally ? TextAlignment.Center : TextAlignment.Left;
                TimeText.TextAlignment = posterAlign;
                TimeText.VerticalAlignment = System.Windows.VerticalAlignment.Top;
                DateText.TextAlignment = posterAlign;
                TimePeriodText.TextAlignment = posterAlign;
                break;

            case ClockDateArrangement.VerticalSideBySide:
                ClockLayoutGrid.HorizontalAlignment = spec.CenterHorizontally
                    ? System.Windows.HorizontalAlignment.Center
                    : System.Windows.HorizontalAlignment.Left;
                SetClockGridCell(TimeText, row: 0, rowSpan: 2, column: 0, columnSpan: 1);
                TimeText.TextAlignment = TextAlignment.Center;
                TimeText.VerticalAlignment = System.Windows.VerticalAlignment.Center;

                SetClockGridCell(DateMetaPanel, row: 0, rowSpan: 2, column: 1, columnSpan: 1);
                DateMetaPanel.Orientation = System.Windows.Controls.Orientation.Vertical;
                DateMetaPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
                DateMetaPanel.VerticalAlignment = System.Windows.VerticalAlignment.Center;
                DateMetaPanel.Margin = new Thickness(14d, 0d, 0d, 0d);
                DateText.TextAlignment = TextAlignment.Left;
                TimePeriodText.TextAlignment = TextAlignment.Left;
                break;

            default:
                ClockLayoutGrid.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
                SetClockGridCell(TimeText, row: 0, rowSpan: 1, column: 0, columnSpan: 2);
                var classicAlign = spec.CenterHorizontally ? TextAlignment.Center : TextAlignment.Left;
                TimeText.TextAlignment = classicAlign;
                TimeText.VerticalAlignment = System.Windows.VerticalAlignment.Top;

                SetClockGridCell(DateMetaPanel, row: 1, rowSpan: 1, column: 0, columnSpan: 2);
                DateMetaPanel.Orientation = System.Windows.Controls.Orientation.Vertical;
                DateMetaPanel.HorizontalAlignment = spec.CenterHorizontally
                    ? System.Windows.HorizontalAlignment.Center
                    : System.Windows.HorizontalAlignment.Left;
                DateMetaPanel.VerticalAlignment = System.Windows.VerticalAlignment.Top;
                DateMetaPanel.Margin = new Thickness(0d);
                DateText.TextAlignment = classicAlign;
                TimePeriodText.TextAlignment = classicAlign;
                break;
        }

        var isolatedGreeting = _componentKind == DesktopComponentKind.Greeting;
        GreetingText.FontSize = (isolatedGreeting ? 34d : 15d) * greeting.FontScale;
        var defaultGreetingWeight = isolatedGreeting ? FontWeights.Light : FontWeights.Normal;
        GreetingText.FontWeight = greeting.FontFamily is { } greetingFontName
            ? InstalledFontCatalog.WeightOf(InstalledFontCatalog.Resolve(greetingFontName), defaultGreetingWeight)
            : defaultGreetingWeight;
        GreetingText.TextAlignment = isolatedGreeting ? TextAlignment.Center : TextAlignment.Left;
        GreetingText.HorizontalAlignment = isolatedGreeting
            ? System.Windows.HorizontalAlignment.Stretch
            : System.Windows.HorizontalAlignment.Left;

        if (isolatedGreeting)
        {
            Grid.SetRowSpan(ClockPanel, 3);
            ClockPanel.VerticalAlignment = System.Windows.VerticalAlignment.Center;
            ClockPanel.Margin = new Thickness(17d, 8d, 17d, 0d);
        }
        else if (isIsolatedTimeDate && (preset != ClockLayoutPreset.Classic || hasVisibleCardBacking))
        {
            Grid.SetRowSpan(ClockPanel, 3);
            ClockPanel.VerticalAlignment = System.Windows.VerticalAlignment.Center;
            ClockPanel.Margin = new Thickness(18d, 10d, 18d, 10d);
        }
        else
        {
            Grid.SetRowSpan(ClockPanel, isIsolatedTimeDate ? 3 : 1);
            ClockPanel.VerticalAlignment = System.Windows.VerticalAlignment.Top;
            ClockPanel.Margin = new Thickness(17d, 8d, 17d, 0d);
        }

        var primaryBrush = GetComponentTextBrush(timeDate);
        var secondaryBrush = GetComponentSecondaryTextBrush(timeDate, effectiveBackgroundOpacity);
        var dateBrush = spec.DateUsesPrimaryInk ? primaryBrush : secondaryBrush;
        TimeText.Foreground = primaryBrush;
        DateText.Foreground = dateBrush;
        TimePeriodText.Foreground = dateBrush;
        GreetingText.Foreground = GetComponentTextBrush(greeting);

        ClockDivider.Visibility = timeDate.ShowDivider && showTimeDate ? Visibility.Visible : Visibility.Collapsed;
        ClockDivider.Height = timeDate.DividerThickness;
        ClockDivider.RadiusY = timeDate.DividerThickness / 2d;
        ClockDivider.RadiusX = timeDate.DividerThickness / 2d;
        if (primaryBrush is SolidColorBrush { Color: var ink })
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
    }

    private static void ApplyClockElementFont(
        System.Windows.Controls.TextBlock element,
        string? familyName,
        bool hasUserCustomFont,
        int defaultWeightOpenType,
        bool allowUiFallback)
    {
        var fallbackWeight = FontWeight.FromOpenTypeWeight(defaultWeightOpenType);
        if (string.IsNullOrWhiteSpace(familyName))
        {
            element.ClearValue(System.Windows.Controls.TextBlock.FontFamilyProperty);
            element.FontWeight = fallbackWeight;
            return;
        }

        var resolved = InstalledFontCatalog.Resolve(familyName);
        element.FontFamily = allowUiFallback
            ? InstalledFontCatalog.CreateWithUiFallback(resolved)
            : InstalledFontCatalog.Create(resolved);
        element.FontWeight = hasUserCustomFont
            ? InstalledFontCatalog.WeightOf(resolved, fallbackWeight)
            : fallbackWeight;
    }

    private static void SetClockGridCell(UIElement element, int row, int rowSpan, int column, int columnSpan)
    {
        Grid.SetRow(element, row);
        Grid.SetRowSpan(element, rowSpan);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
    }

    private Brush GetComponentTextBrush(DesktopComponentState component) =>
        AccessibilityThemeManager.IsHighContrastEnabled
            ? SystemColors.WindowTextBrush
            : _usesLegacyAppearance
                ? WidgetTextBrush
                : CreateBrush(component.Appearance.TextColor, 1d);

    private Brush GetComponentSecondaryTextBrush(
        DesktopComponentState component,
        double renderedBackgroundOpacity) =>
        AccessibilityThemeManager.IsHighContrastEnabled
            ? SystemColors.WindowTextBrush
            : _usesLegacyAppearance
                ? WidgetSecondaryTextBrush
                : CreateBrush(
                    component.Appearance.TextColor,
                    ColorContrastRules.ResolveSecondaryTextAlpha(renderedBackgroundOpacity));

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

    private static void ApplyTextBrush(DependencyObject root, Brush primaryBrush, Brush secondaryBrush)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            switch (child)
            {
                case System.Windows.Controls.TextBlock { Tag: "Accent" }:
                    break;
                case System.Windows.Controls.TextBlock { Tag: "Secondary" } secondaryBlock:
                    secondaryBlock.Foreground = secondaryBrush;
                    break;
                case System.Windows.Controls.TextBlock textBlock:
                    textBlock.Foreground = primaryBrush;
                    break;
                case System.Windows.Controls.Control control:
                    control.Foreground = primaryBrush;
                    break;
                case System.Windows.Shapes.Path path:
                    path.Stroke = primaryBrush;
                    break;
            }
            ApplyTextBrush(child, primaryBrush, secondaryBrush);
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
            // A single-component card only refreshes what it shows: changing collapsed
            // text still re-renders the whole cached card in software every second.
            if (_componentKind is null or DesktopComponentKind.TimeDate or DesktopComponentKind.Greeting)
            {
                UpdateClock();
            }
            if (_componentKind is null or DesktopComponentKind.Performance)
            {
                UpdatePerformance(snapshot);
            }
            if (_componentKind is null or DesktopComponentKind.Weather)
            {
                MaybeRefreshWeather(false);
            }
        });
    }

    private void OnAmbientTick(object? sender, EventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }
        if (!_sampler.IsRunning)
        {
            if (_componentKind is null or DesktopComponentKind.TimeDate or DesktopComponentKind.Greeting)
            {
                UpdateClock();
            }
            if (_componentKind is null or DesktopComponentKind.Weather)
            {
                MaybeRefreshWeather(false);
            }
        }
        ArmAmbientTimer();
    }

    private void ArmAmbientTimer()
    {
        var now = DateTime.Now;
        var nowOffset = DateTimeOffset.Now;
        var delayMs = Math.Clamp(60_000d - ((now.Second * 1000) + now.Millisecond) + 50d, 250d, 60_050d);
        if ((_componentKind is null or DesktopComponentKind.Weather)
            && State.HasWeatherLocation
            && !_weatherInFlight
            && _weather.NextRetryAt is { } retryAt
            && retryAt > nowOffset)
        {
            var retryMs = Math.Clamp((retryAt - nowOffset).TotalMilliseconds + 100d, 1000d, 60_050d);
            delayMs = Math.Min(delayMs, retryMs);
        }
        _ambientTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _ambientTimer.Start();
    }

    private void OnSystemTimeChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded) return;
            if (_componentKind is null or DesktopComponentKind.TimeDate or DesktopComponentKind.Greeting)
            {
                UpdateClock();
            }
            ArmAmbientTimer();
        });

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded) return;
            if (_componentKind is null or DesktopComponentKind.TimeDate or DesktopComponentKind.Greeting)
            {
                UpdateClock();
            }
            if (_componentKind is null or DesktopComponentKind.Weather)
            {
                MaybeRefreshWeather(false);
            }
            ArmAmbientTimer();
        });
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var showTimeDate = (_experience?.GetComponent(DesktopComponentKind.TimeDate).IsVisible ?? true)
            && (_componentKind is null or DesktopComponentKind.TimeDate);
        var parts = ClockLayoutRules.FormatClockParts(
            now,
            State.ClockLayout,
            State.Use24HourClock,
            State.ShowDate);

        TimeText.Text = parts.TimeText;
        TimeText.Visibility = showTimeDate ? Visibility.Visible : Visibility.Collapsed;

        var showPeriod = showTimeDate && !string.IsNullOrEmpty(parts.PeriodText);
        TimePeriodText.Text = parts.PeriodText ?? string.Empty;
        TimePeriodText.Visibility = showPeriod ? Visibility.Visible : Visibility.Collapsed;

        var showDateText = showTimeDate && State.ShowDate && !string.IsNullOrEmpty(parts.DateText);
        DateText.Text = parts.DateText;
        DateText.Visibility = showDateText ? Visibility.Visible : Visibility.Collapsed;

        TimePeriodText.Margin = showPeriod && showDateText
            ? DateMetaPanel.Orientation == System.Windows.Controls.Orientation.Horizontal
                ? new Thickness(0d, 0d, 8d, 0d)
                : new Thickness(0d, 0d, 0d, 4d)
            : new Thickness(0d);
        DateMetaPanel.Visibility = showPeriod || showDateText
            ? Visibility.Visible
            : Visibility.Collapsed;

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
            SetWeatherStatusText(_weather.NextRetryAt is { } retryAt
                ? $"上次刷新失败，{retryAt.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)} 后重试"
                : "上次刷新失败，稍后重试");
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
        SetWeatherStatusText("更新中…");
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
            WeatherIconPath.Data = Geometry.Parse(WeatherIconRules.GetPathData(WeatherIconKind.Unset));
            WeatherConditionText.Text = "";
            WeatherHighText.Text = "";
            WeatherLowText.Text = "";
            WeatherHumidityText.Text = "";
            WeatherDetailsPanel.Visibility = Visibility.Collapsed;
            WeatherUpdatedText.Text = "";
            SetWeatherStatusText("");
            return;
        }

        WeatherDetailsPanel.Visibility = Visibility.Visible;

        // "黑河 · 黑龙江 · 中国" reads as just "黑河"; the full place is in the tooltip.
        WeatherCityText.Text = snapshot.City.Split(" · ")[0];
        WeatherCityText.ToolTip = snapshot.City;
        WeatherTempText.Text = snapshot.TemperatureCelsius is { } temperature
            ? $"{temperature:0.#}°"
            : "--";
        WeatherIconPath.Data = Geometry.Parse(WeatherIconRules.GetPathData(snapshot.Condition));
        WeatherConditionText.Text = snapshot.Condition;
        var high = snapshot.DailyHighCelsius is { } h ? $"{h:0.#}°" : "--";
        var low = snapshot.DailyLowCelsius is { } l ? $"{l:0.#}°" : "--";
        var humidity = snapshot.HumidityPercent is { } value ? $"{value}%" : "--";
        WeatherHighText.Text = $"最高 {high}";
        WeatherLowText.Text = $"最低 {low}";
        WeatherHumidityText.Text = $"湿度 {humidity}";
        WeatherUpdatedText.Text = snapshot.UpdatedAt is { } updatedAt
            ? $"更新于 {updatedAt.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)}"
            : "";
        SetWeatherStatusText(snapshot.Status switch
        {
            WeatherStatus.Fresh => "",
            WeatherStatus.CachedFallback => $"刷新失败（{snapshot.ErrorMessage}），显示缓存数据",
            WeatherStatus.Failed => $"刷新失败：{snapshot.ErrorMessage}",
            _ => ""
        });
    }

    private void SetWeatherStatusText(string text)
    {
        WeatherStatusText.Text = text;
        WeatherStatusText.Visibility = string.IsNullOrEmpty(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
