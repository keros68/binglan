namespace BingLan.Core.Models;

public enum WeatherStatus
{
    NoCity,
    Fresh,
    CachedFallback,
    Failed
}

public sealed record PerformanceSnapshot(
    double CpuPercent,
    double MemoryPercent,
    double UploadBytesPerSecond,
    double DownloadBytesPerSecond,
    DateTimeOffset CapturedAt);

public sealed record WeatherSnapshot(
    WeatherStatus Status,
    string City,
    double? TemperatureCelsius,
    double? DailyHighCelsius,
    double? DailyLowCelsius,
    int? HumidityPercent,
    string Condition,
    bool FromCache,
    DateTimeOffset? UpdatedAt,
    string? ErrorMessage)
{
    private static readonly char[] HierarchySeparators = ['·', '•', ',', '，', '/'];

    /// <summary>
    /// Extracts the most specific location name (e.g. "南岗区" from "南岗区 · 黑龙江省·哈尔滨市 · 中国")
    /// for compact surfaces such as the top bar and weather header, while tooltips keep the full string.
    /// </summary>
    public static string CompactLocation(string? city)
    {
        if (string.IsNullOrWhiteSpace(city))
        {
            return string.Empty;
        }

        var trimmed = city.Trim();
        var separatorIndex = trimmed.IndexOfAny(HierarchySeparators);
        if (separatorIndex > 0)
        {
            var head = trimmed[..separatorIndex].Trim();
            if (head.Length > 0)
            {
                return head;
            }
        }

        var dashIndex = trimmed.IndexOf('-');
        if (dashIndex > 0)
        {
            var head = trimmed[..dashIndex].Trim();
            if (head.Length > 0)
            {
                return head;
            }
        }

        return trimmed;
    }
}
