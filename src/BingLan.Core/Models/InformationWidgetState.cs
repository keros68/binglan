using System.Text.Json;
using System.Text.Json.Serialization;

namespace BingLan.Core.Models;

public sealed class InformationWidgetState
{
    private double _cornerRadius = WidgetAppearanceRules.DefaultCornerRadius;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "桌面信息";
    public string GreetingName { get; set; } = "";
    public bool Use24HourClock { get; set; } = true;

    // 城市与坐标只能来自用户手动选择的结果，不允许任何自动定位来源写入。
    public string WeatherCity { get; set; } = "";
    public double? WeatherLatitude { get; set; }
    public double? WeatherLongitude { get; set; }

    public bool IsLocked { get; set; }
    public WidgetAppearanceState Appearance { get; set; } = new();
    public double CornerRadius
    {
        get => _cornerRadius;
        set => _cornerRadius = WidgetAppearanceRules.CoerceCornerRadius(value);
    }
    public WindowPlacement Placement { get; set; } = new()
    {
        Left = 92,
        Top = 470,
        Width = 300,
        Height = 420
    };

    public bool HasWeatherLocation =>
        !string.IsNullOrWhiteSpace(WeatherCity) &&
        WeatherLatitude is >= -90d and <= 90d &&
        WeatherLongitude is >= -180d and <= 180d;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
