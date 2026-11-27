using System.Text.Json;
using System.Text.Json.Serialization;

namespace BingLan.Core.Models;

public enum DesktopLayoutPreset
{
    QuietInformation,
    GlacierWorkbench,
    TransparentApple,
    CenterClock
}

public enum DesktopComponentKind
{
    TimeDate,
    Greeting,
    Weather,
    Performance,
    Todo,
    AudioVisualizer,
    QuickLaunch
}

public enum DesktopInformationDensity
{
    Comfortable,
    Standard,
    Compact
}

public sealed class DesktopComponentState
{
    private double _fontScale = 1d;
    private double _cornerRadius = WidgetAppearanceRules.DefaultCornerRadius;

    public DesktopComponentKind Kind { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; }

    /// <summary>For the time card: a thin line under the date that fades out at both ends.</summary>
    public bool ShowDivider { get; set; }

    private double _dividerThickness = 2d;

    /// <summary>How thick that line is, from 1 to 6 DIPs.</summary>
    public double DividerThickness
    {
        get => _dividerThickness;
        set => _dividerThickness = double.IsFinite(value) ? Math.Clamp(value, 1d, 6d) : 2d;
    }

    public DesktopInformationDensity Density { get; set; } = DesktopInformationDensity.Standard;
    public WindowPlacement Placement { get; set; } = new();
    public WidgetAppearanceState Appearance { get; set; } = new();

    /// <summary>The font of the card's text; null keeps the app's interface font.</summary>
    public string? FontFamily { get; set; }

    public double FontScale
    {
        get => _fontScale;
        set => _fontScale = DesktopExperienceRules.CoerceFontScale(value);
    }

    public double CornerRadius
    {
        get => _cornerRadius;
        set => _cornerRadius = WidgetAppearanceRules.CoerceCornerRadius(value);
    }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class DesktopExperienceState
{
    public DesktopLayoutPreset ActivePreset { get; set; } =
        DesktopLayoutPreset.QuietInformation;
    public List<DesktopComponentState> Components { get; set; } =
        DesktopExperienceRules.CreateDefaultComponents();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public DesktopComponentState GetComponent(DesktopComponentKind kind) =>
        Components.First(component => component.Kind == kind);
}

public static class DesktopExperienceRules
{
    public const double MinimumFontScale = 0.7d;
    public const double MaximumFontScale = 2d;
    public const double PresetColumnGap = 12d;
    public const double DefaultWeatherCardHeight = 137d;
    public const double DefaultPerformanceCardHeight = 86d;

    public static IReadOnlyList<DesktopComponentKind> ComponentOrder { get; } =
    [
        DesktopComponentKind.TimeDate,
        DesktopComponentKind.Greeting,
        DesktopComponentKind.Weather,
        DesktopComponentKind.Performance,
        DesktopComponentKind.Todo,
        DesktopComponentKind.AudioVisualizer,
        DesktopComponentKind.QuickLaunch
    ];

    public static DesktopExperienceState CreateDefault() => new()
    {
        Components = CreateDefaultComponents()
    };

    public static List<DesktopComponentState> CreateDefaultComponents() =>
        ComponentOrder.Select(CreateDefaultComponent).ToList();

    public static DesktopComponentState CreateDefaultComponent(DesktopComponentKind kind)
    {
        var state = new DesktopComponentState
        {
            Kind = kind,
            IsVisible = kind is not DesktopComponentKind.AudioVisualizer and
                not DesktopComponentKind.QuickLaunch,
            Placement = kind switch
            {
                DesktopComponentKind.TimeDate => Placement(92, 92, 328, 166),
                DesktopComponentKind.Greeting => Placement(536, 856, 900, 115),
                DesktopComponentKind.Weather => Placement(92, 270, 328, DefaultWeatherCardHeight),
                DesktopComponentKind.Performance => Placement(92, 419, 328, DefaultPerformanceCardHeight),
                DesktopComponentKind.Todo => Placement(92, 517, 328, 300),
                DesktopComponentKind.AudioVisualizer => Placement(536, 720, 520, 96),
                DesktopComponentKind.QuickLaunch => Placement(92, 829, 328, 72),
                _ => new WindowPlacement()
            }
        };
        if (kind == DesktopComponentKind.Greeting)
        {
            state.Appearance.BackgroundOpacity = WidgetAppearanceRules.MinimumBackgroundOpacity;
            state.Appearance.HeaderOpacity = 0d;
            state.CornerRadius = 0d;
            state.Appearance.TextColor = "#FFFFFF";
        }
        return state;
    }

    public static void Normalize(DesktopExperienceState state)
    {
        if (!Enum.IsDefined(state.ActivePreset))
        {
            state.ActivePreset = DesktopLayoutPreset.QuietInformation;
        }

        state.Components ??= [];
        foreach (var kind in ComponentOrder)
        {
            var component = state.Components.FirstOrDefault(candidate => candidate.Kind == kind);
            if (component is null)
            {
                state.Components.Add(CreateDefaultComponent(kind));
                continue;
            }

            component.Appearance ??= new WidgetAppearanceState();
            component.FontFamily = string.IsNullOrWhiteSpace(component.FontFamily)
                ? null
                : WidgetAppearanceRules.CoerceTitleFontFamily(component.FontFamily);
            component.Placement ??= CreateDefaultComponent(kind).Placement;
            component.FontScale = CoerceFontScale(component.FontScale);
            component.CornerRadius = WidgetAppearanceRules.CoerceCornerRadius(
                component.CornerRadius);
            component.Appearance.BackgroundColor =
                WidgetAppearanceRules.CoerceBackgroundColor(component.Appearance.BackgroundColor);
            component.Appearance.BackgroundOpacity =
                WidgetAppearanceRules.CoerceBackgroundOpacity(component.Appearance.BackgroundOpacity);
            component.Appearance.HeaderColor =
                WidgetAppearanceRules.CoerceHeaderColor(component.Appearance.HeaderColor);
            component.Appearance.HeaderOpacity =
                WidgetAppearanceRules.CoerceHeaderOpacity(component.Appearance.HeaderOpacity);
            component.Appearance.TextColor =
                WidgetAppearanceRules.CoerceTextColor(component.Appearance.TextColor);
            component.Appearance.TitleFontFamily =
                WidgetAppearanceRules.CoerceTitleFontFamily(component.Appearance.TitleFontFamily);
            if (!Enum.IsDefined(component.Density))
            {
                component.Density = DesktopInformationDensity.Standard;
            }
        }

        state.Components = ComponentOrder
            .Select(kind => state.Components.First(component => component.Kind == kind))
            .Concat(state.Components.Where(component => !ComponentOrder.Contains(component.Kind)))
            .ToList();
    }

    public static double CoerceFontScale(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 1d;
        }
        return Math.Clamp(value, MinimumFontScale, MaximumFontScale);
    }

    private const double WeatherSymmetricOuterVerticalMargin = 16d;
    private const double WeatherRuleHeight = 8d;
    private const double WeatherRefreshButtonHeight = 22d;
    private const double WeatherDetailsTopMargin = 2d;
    private const double PerformanceOuterVerticalMargin = 10d;
    private const double TypographicLineHeightFactor = 1.28d;

    public static double GetWeatherVerticalPadding(DesktopInformationDensity density) => density switch
    {
        DesktopInformationDensity.Comfortable => 20d,
        DesktopInformationDensity.Compact => 10d,
        _ => 14d
    };

    public static double GetPerformanceVerticalPadding(DesktopInformationDensity density) => density switch
    {
        DesktopInformationDensity.Comfortable => 22d,
        DesktopInformationDensity.Compact => 12d,
        _ => 18d
    };

    /// <summary>
    /// Computes the minimum DIP height needed by an isolated weather card from its font scale
    /// and density so the bottom detail row stays unclipped with balanced top and bottom breathing room.
    /// </summary>
    /// <remarks>
    /// Known upper bound: row heights are estimated from fixed font sizes multiplied by
    /// <see cref="TypographicLineHeightFactor"/> (1.28) without measuring user-selected custom
    /// fonts, so typefaces with unusually tall ascenders, descenders, or line spacing can exceed
    /// this estimate. Upgrade condition: replace the fixed-factor estimate with measurement of the
    /// actual rendered card content height if custom fonts cause clipping or visibly uneven vertical
    /// breathing room on the weather card.
    /// </remarks>
    public static double CalculateWeatherMinHeight(
        double fontScale,
        DesktopInformationDensity density = DesktopInformationDensity.Standard)
    {
        var scale = CoerceFontScale(fontScale);
        var headerRow = Math.Max(WeatherRefreshButtonHeight, 15d * scale * TypographicLineHeightFactor);
        var temperatureRow = 46d * scale * TypographicLineHeightFactor;
        var detailsRow = WeatherDetailsTopMargin + 12d * scale * TypographicLineHeightFactor;
        var contentHeight = WeatherSymmetricOuterVerticalMargin
            + GetWeatherVerticalPadding(density)
            + WeatherRuleHeight
            + headerRow
            + temperatureRow
            + detailsRow;
        return Math.Ceiling(contentHeight);
    }

    public static double CalculateCombinedInformationMinHeight(
        DesktopExperienceState experience,
        ClockLayoutPreset clockPreset = ClockLayoutPreset.Classic,
        bool showDate = true)
    {
        ArgumentNullException.ThrowIfNull(experience);
        var timeDate = experience.GetComponent(DesktopComponentKind.TimeDate);
        var greeting = experience.GetComponent(DesktopComponentKind.Greeting);
        var performance = experience.GetComponent(DesktopComponentKind.Performance);
        var weather = experience.GetComponent(DesktopComponentKind.Weather);
        var lowChrome = experience.ActivePreset != DesktopLayoutPreset.GlacierWorkbench;

        var total = 0d;
        if (timeDate.IsVisible || greeting.IsVisible)
        {
            total += 8d;
            if (timeDate.IsVisible)
            {
                var spec = ClockLayoutRules.ResolveTypography(
                    clockPreset,
                    timeDate.FontFamily,
                    timeDate.FontScale,
                    timeDate.Appearance.TextColor,
                    centerClockPreset: false,
                    lowChrome: lowChrome,
                    backgroundOpacity: timeDate.Appearance.BackgroundOpacity);
                var timeLineHeight = spec.TimeLineHeight ?? (spec.TimeFontSize * TypographicLineHeightFactor);
                var timeHeight = clockPreset == ClockLayoutPreset.VerticalDigits
                    ? timeLineHeight * 2d
                    : timeLineHeight;
                var dateHeight = showDate ? spec.DateFontSize * TypographicLineHeightFactor : 0d;
                total += spec.Arrangement == ClockDateArrangement.VerticalSideBySide
                    ? Math.Max(timeHeight, dateHeight)
                    : timeHeight + dateHeight;
                if (timeDate.ShowDivider)
                {
                    total += 20d + timeDate.DividerThickness;
                }
            }
            if (greeting.IsVisible)
            {
                total += 3d + 15d * CoerceFontScale(greeting.FontScale) * TypographicLineHeightFactor;
            }
        }
        if (performance.IsVisible)
        {
            var perfScale = CoerceFontScale(performance.FontScale);
            var barColumn = 30d * perfScale * TypographicLineHeightFactor + 8d;
            var networkColumn = 35d * perfScale * TypographicLineHeightFactor + 3d;
            total += PerformanceOuterVerticalMargin
                + GetPerformanceVerticalPadding(performance.Density)
                + Math.Max(barColumn, networkColumn);
        }
        if (weather.IsVisible)
        {
            total += CalculateWeatherMinHeight(weather.FontScale, weather.Density);
        }

        return Math.Ceiling(Math.Max(72d, total));
    }

    public static string GetPresetName(DesktopLayoutPreset preset) => preset switch
    {
        DesktopLayoutPreset.QuietInformation => "静谧信息层",
        DesktopLayoutPreset.GlacierWorkbench => "冰川工作台",
        DesktopLayoutPreset.TransparentApple => "通透苹果桌面",
        DesktopLayoutPreset.CenterClock => "居中时钟",
        _ => "静谧信息层"
    };

    public static string GetComponentName(DesktopComponentKind kind) => kind switch
    {
        DesktopComponentKind.TimeDate => "时间日期",
        DesktopComponentKind.Greeting => "问候语",
        DesktopComponentKind.Weather => "天气",
        DesktopComponentKind.Performance => "性能",
        DesktopComponentKind.Todo => "待办",
        DesktopComponentKind.AudioVisualizer => "音频频谱",
        DesktopComponentKind.QuickLaunch => "快捷入口",
        _ => kind.ToString()
    };

    /// <summary>
    /// Where a layout preset puts each card inside a work area. Used both to apply a
    /// preset and to draw its preview, so the two always match.
    /// </summary>
    public static IReadOnlyDictionary<DesktopComponentKind, WindowPlacement> PresetPlacements(
        DesktopLayoutPreset preset,
        double left,
        double top,
        double right,
        double bottom,
        bool audioSpectrumVisible = false)
    {
        var area = (Left: left, Top: top, Right: right, Bottom: bottom);
        if (bottom - top > right - left)
        {
            return PortraitPlacements(preset, left, top, right - left, bottom - top, audioSpectrumVisible);
        }
        var spectrumWidth = Math.Clamp(right - left - 32, 1, 520);
        var spectrum = Placement((left + right - spectrumWidth) / 2, Math.Max(top, bottom - 258), spectrumWidth, 96);
        if (preset == DesktopLayoutPreset.CenterClock)
        {
            // A large clock centred at the top, the greeting centred near the bottom, and
            // performance, weather and to-dos in one column at the top right.
            var center = (area.Left + area.Right) / 2d;
            var column = area.Right - 400;
            return new Dictionary<DesktopComponentKind, WindowPlacement>
            {
                [DesktopComponentKind.TimeDate] = Placement(center - 280, area.Top + 56, 560, 200),
                [DesktopComponentKind.Greeting] = Placement(center - 320, area.Bottom - 150, 640, 84),
                [DesktopComponentKind.AudioVisualizer] = spectrum,
                [DesktopComponentKind.Performance] = Placement(column, area.Top + 30, 370, DefaultPerformanceCardHeight),
                [DesktopComponentKind.Weather] = Placement(column, area.Top + 116 + PresetColumnGap, 370, DefaultWeatherCardHeight),
                [DesktopComponentKind.Todo] = Placement(column, area.Top + 253 + PresetColumnGap * 2d, 370, 300)
            };
        }

        var (timeDate, greeting, weather, performance) = preset switch
        {
            DesktopLayoutPreset.GlacierWorkbench =>
            (
                Placement(area.Left + 62, area.Top + 70, 320, 170),
                Placement(area.Left + 430, area.Bottom - 138, 760, 100),
                Placement(area.Left + 62, area.Top + 240 + PresetColumnGap, 320, DefaultWeatherCardHeight),
                Placement(area.Left + 62, area.Top + 377 + PresetColumnGap * 2d, 320, DefaultPerformanceCardHeight)
            ),
            DesktopLayoutPreset.TransparentApple =>
            (
                Placement(area.Left + 70, area.Top + 60, 380, 170),
                Placement(area.Left + 510, area.Bottom - 130, 820, 96),
                Placement(area.Left + 70, area.Top + 230 + PresetColumnGap, 380, DefaultWeatherCardHeight),
                Placement(area.Left + 70, area.Top + 367 + PresetColumnGap * 2d, 380, DefaultPerformanceCardHeight)
            ),
            _ =>
            (
                Placement(area.Left + 70, area.Top + 70, 380, 176),
                Placement(area.Left + 500, area.Bottom - 132, 820, 96),
                Placement(area.Left + 70, area.Top + 246 + PresetColumnGap, 380, DefaultWeatherCardHeight),
                Placement(area.Left + 70, area.Top + 383 + PresetColumnGap * 2d, 380, DefaultPerformanceCardHeight)
            )
        };
        var todo = preset switch
        {
            DesktopLayoutPreset.GlacierWorkbench => Placement(area.Right - 390, area.Top + 110, 350, 420),
            DesktopLayoutPreset.TransparentApple => Placement(area.Right - 390, area.Top + 500, 350, 360),
            _ => Placement(area.Right - 370, area.Top + 86, 330, 410)
        };
        return new Dictionary<DesktopComponentKind, WindowPlacement>
        {
            [DesktopComponentKind.TimeDate] = timeDate,
            [DesktopComponentKind.Greeting] = greeting,
            [DesktopComponentKind.AudioVisualizer] = spectrum,
            [DesktopComponentKind.Weather] = weather,
            [DesktopComponentKind.Performance] = performance,
            [DesktopComponentKind.Todo] = todo
        };
    }

    private static IReadOnlyDictionary<DesktopComponentKind, WindowPlacement> PortraitPlacements(
        DesktopLayoutPreset preset, double left, double top, double width, double height, bool audioSpectrumVisible)
    {
        var margin = Math.Min(32d, Math.Max(0d, width / 12d));
        var availableWidth = Math.Max(1d, width - margin * 2d);
        var availableHeight = Math.Max(1d, height - margin * 2d);
        var kinds = preset == DesktopLayoutPreset.CenterClock
            ? new[] { DesktopComponentKind.TimeDate, DesktopComponentKind.Performance,
                DesktopComponentKind.Weather, DesktopComponentKind.Todo, DesktopComponentKind.AudioVisualizer, DesktopComponentKind.Greeting }
            : new[] { DesktopComponentKind.TimeDate, DesktopComponentKind.Weather,
                DesktopComponentKind.Performance, DesktopComponentKind.Todo, DesktopComponentKind.AudioVisualizer, DesktopComponentKind.Greeting };
        var heights = preset switch
        {
            DesktopLayoutPreset.GlacierWorkbench => new[] { 170d, DefaultWeatherCardHeight, DefaultPerformanceCardHeight, 420d, 96d, 100d },
            DesktopLayoutPreset.TransparentApple => new[] { 170d, DefaultWeatherCardHeight, DefaultPerformanceCardHeight, 360d, 96d, 96d },
            DesktopLayoutPreset.CenterClock => new[] { 200d, DefaultPerformanceCardHeight, DefaultWeatherCardHeight, 300d, 96d, 84d },
            _ => new[] { 176d, DefaultWeatherCardHeight, DefaultPerformanceCardHeight, 410d, 96d, 96d }
        };
        const DesktopComponentKind spectrum = DesktopComponentKind.AudioVisualizer;
        if (!audioSpectrumVisible)
        {
            var spectrumIndex = Array.IndexOf(kinds, spectrum);
            kinds = kinds.Where(kind => kind != spectrum).ToArray();
            heights = heights.Where((_, index) => index != spectrumIndex).ToArray();
        }
        var minimums = kinds.Select(kind => kind == DesktopComponentKind.Todo ? 166d : 72d).ToArray();
        var gapCount = kinds.Length - 1;
        var gap = Math.Min(PresetColumnGap, Math.Max(0d, (availableHeight - minimums.Sum()) / gapCount));
        var cardBudget = Math.Max(1d, availableHeight - gap * gapCount);
        if (heights.Sum() > cardBudget)
        {
            var reduction = Math.Clamp((cardBudget - minimums.Sum()) / (heights.Sum() - minimums.Sum()), 0d, 1d);
            heights = heights.Select((value, index) => minimums[index] + (value - minimums[index]) * reduction).ToArray();
        }

        // Below the combined card minimums, visibility takes priority; recovery may
        // overlap cards. Use a larger work area for a non-overlapping preset.
        var result = new Dictionary<DesktopComponentKind, WindowPlacement>();
        var y = top + margin;
        for (var index = 0; index < kinds.Length; index++)
        {
            var kind = kinds[index];
            var desiredWidth = kind switch
            {
                DesktopComponentKind.TimeDate => preset == DesktopLayoutPreset.CenterClock ? 560d : 380d,
                DesktopComponentKind.Greeting => 640d,
                DesktopComponentKind.AudioVisualizer => 520d,
                _ => 380d
            };
            var cardWidth = Math.Min(availableWidth, desiredWidth);
            var cardHeight = Math.Min(heights[index], availableHeight);
            result[kind] = Placement(left + (width - cardWidth) / 2d,
                Math.Min(y, top + height - margin - cardHeight), cardWidth, cardHeight);
            y += cardHeight + gap;
        }
        if (!audioSpectrumVisible)
        {
            // Keep a restorable position for the hidden spectrum without reserving a row.
            var spectrumWidth = Math.Min(availableWidth, 520d);
            var spectrumHeight = Math.Min(availableHeight, 96d);
            var spectrumTop = Math.Min(y, top + height - margin - spectrumHeight);
            result[spectrum] = Placement(left + (width - spectrumWidth) / 2d, spectrumTop, spectrumWidth, spectrumHeight);
        }
        return result;
    }

    private static WindowPlacement Placement(
        double left,
        double top,
        double width,
        double height) => new()
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height
        };
}
