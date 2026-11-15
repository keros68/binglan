namespace BingLan.Core.Services;

public enum WeatherIconKind
{
    Clear,
    PartlyCloudy,
    Cloudy,
    Fog,
    Rain,
    Snow,
    Thunderstorm,
    Unknown,
    Unset
}

/// <summary>
/// Maps WMO weather descriptions to minimalist 24x24 single-weight SVG stroke paths
/// so icons inherit card ink cleanly without emoji font color variance.
/// </summary>
public static class WeatherIconRules
{
    public static WeatherIconKind FromCondition(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition) || condition.Contains("未设置", StringComparison.Ordinal))
        {
            return WeatherIconKind.Unset;
        }

        if (condition.Contains('雷') || condition.Contains("冰雹", StringComparison.Ordinal))
        {
            return WeatherIconKind.Thunderstorm;
        }

        if (condition.Contains('雪'))
        {
            return WeatherIconKind.Snow;
        }

        if (condition.Contains('雨'))
        {
            return WeatherIconKind.Rain;
        }

        if (condition.Contains('雾') || condition.Contains('霾'))
        {
            return WeatherIconKind.Fog;
        }

        if (condition.Contains("多云", StringComparison.Ordinal))
        {
            return WeatherIconKind.PartlyCloudy;
        }

        if (condition.Contains('阴'))
        {
            return WeatherIconKind.Cloudy;
        }

        if (condition.Contains('晴'))
        {
            return WeatherIconKind.Clear;
        }

        return WeatherIconKind.Unknown;
    }

    public static string GetPathData(string? condition) => GetPathData(FromCondition(condition));

    public static string GetPathData(WeatherIconKind kind) =>
        kind switch
        {
            WeatherIconKind.Clear =>
                "M12,7.5 A4.5,4.5 0 1 1 11.99,7.5 Z M12,2.5 L12,4.5 M12,19.5 L12,21.5 M2.5,12 L4.5,12 M19.5,12 L21.5,12 M5.3,5.3 L6.7,6.7 M17.3,17.3 L18.7,18.7 M18.7,5.3 L17.3,6.7 M6.7,17.3 L5.3,18.7",
            WeatherIconKind.PartlyCloudy =>
                "M6.5,11 A3.5,3.5 0 0 1 12.2,8.4 M8.8,4 L8.8,5.5 M4,8.8 L5.5,8.8 M5.4,5.4 L6.5,6.5 M12.2,5.4 L11.1,6.5 M8,19 L17.5,19 A3.5,3.5 0 0 0 18.2,12.1 A4.5,4.5 0 0 0 9.6,11.6 A3.7,3.7 0 0 0 8,19 Z",
            WeatherIconKind.Cloudy =>
                "M7,18 L17.5,18 A3.8,3.8 0 0 0 18.2,10.5 A5,5 0 0 0 8.5,9.6 A4.2,4.2 0 0 0 7,18 Z",
            WeatherIconKind.Fog =>
                "M5,9 L18,9 M3.5,13 L19.5,13 M6,17 L17,17",
            WeatherIconKind.Rain =>
                "M6.5,14.5 L17.5,14.5 A3.3,3.3 0 0 0 18,8 A4.6,4.6 0 0 0 9.1,7.3 A3.8,3.8 0 0 0 6.5,14.5 Z M8.5,17.5 L7.5,20.5 M12.5,17.5 L11.5,20.5 M16.5,17.5 L15.5,20.5",
            WeatherIconKind.Snow =>
                "M6.5,14.5 L17.5,14.5 A3.3,3.3 0 0 0 18,8 A4.6,4.6 0 0 0 9.1,7.3 A3.8,3.8 0 0 0 6.5,14.5 Z M8.5,17.8 L8.5,20.2 M7.3,19 L9.7,19 M12.5,17.3 L12.5,19.7 M11.3,18.5 L13.7,18.5 M16.5,17.8 L16.5,20.2 M15.3,19 L17.7,19",
            WeatherIconKind.Thunderstorm =>
                "M6.5,14 L17.5,14 A3.3,3.3 0 0 0 18,7.5 A4.6,4.6 0 0 0 9.1,6.8 A3.8,3.8 0 0 0 6.5,14 Z M13,14 L10.2,18 L13.2,18 L11,21.5",
            WeatherIconKind.Unset =>
                "M12,4 A6,6 0 0 1 18,10 C18,14.5 12,20.5 12,20.5 C12,20.5 6,14.5 6,10 A6,6 0 0 1 12,4 Z M10.2,9.8 L13.8,9.8",
            _ =>
                "M7,18 L17.5,18 A3.8,3.8 0 0 0 18.2,10.5 A5,5 0 0 0 8.5,9.6 A4.2,4.2 0 0 0 7,18 Z M12,11.5 L12,14 M12,15.8 L12,16.1"
        };
}
