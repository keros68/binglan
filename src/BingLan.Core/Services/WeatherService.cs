using System.Globalization;
using System.Text.Json;
using BingLan.Core.Models;

namespace BingLan.Core.Services;

// Open-Meteo 公开预报接口客户端。只发送调用方传入的城市坐标，不做任何自动定位。
// 城市到坐标的解析（地理编码）由上层在选择城市时完成，不在本模块范围内。
public sealed class WeatherService
{
    public const string ForecastEndpoint = "https://api.open-meteo.com/v1/forecast";

    public static readonly TimeSpan SuccessRefreshInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan BaseRetryDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    // 15 秒覆盖直连（约 1–2 秒）和较慢的代理路径（实测可到 7 秒以上）。
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    // Refreshes are serialized; readers on the UI thread see one complete location state.
    private volatile LocationState _state = new(null, null, null, 0, null);

    private sealed record LocationState(
        string? Key,
        WeatherSnapshot? LastSuccess,
        WeatherSnapshot? LastSnapshot,
        int ConsecutiveFailures,
        DateTimeOffset? NextRetryAt);

    public WeatherService()
        : this(null, null)
    {
    }

    public WeatherService(
        HttpMessageHandler? handler,
        TimeSpan? timeout = null,
        TimeProvider? time = null)
    {
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.Timeout = timeout ?? DefaultTimeout;
        _time = time ?? TimeProvider.System;
    }

    public DateTimeOffset? NextRetryAt => _state.NextRetryAt;

    // 退避是强制的调度建议：失败后调用方应等待 ShouldAttemptRefresh 放行再重试。
    public bool ShouldAttemptRefresh(DateTimeOffset now) => ShouldAttemptRefresh(_state, now);

    private static bool ShouldAttemptRefresh(LocationState state, DateTimeOffset now) =>
        state.ConsecutiveFailures == 0 || state.NextRetryAt is null || now >= state.NextRetryAt.Value;

    public bool ShouldAttemptRefresh(
        DateTimeOffset now,
        string city,
        double latitude,
        double longitude)
    {
        var state = _state;
        return !string.Equals(state.Key, CreateLocationKey(city, latitude, longitude), StringComparison.Ordinal) ||
               ShouldAttemptRefresh(state, now);
    }

    public WeatherSnapshot? GetLastSnapshot(string city, double latitude, double longitude)
    {
        var state = _state;
        return string.Equals(state.Key, CreateLocationKey(city, latitude, longitude), StringComparison.Ordinal)
            ? state.LastSnapshot
            : null;
    }

    public bool IsAutomaticRefreshDue(DateTimeOffset now, string city, double latitude, double longitude)
    {
        var state = _state;
        if (!string.Equals(state.Key, CreateLocationKey(city, latitude, longitude), StringComparison.Ordinal))
        {
            return true;
        }

        return ShouldAttemptRefresh(state, now) &&
               (state.LastSnapshot is not { Status: WeatherStatus.Fresh, UpdatedAt: { } last }
                || now - last >= SuccessRefreshInterval);
    }

    // 指数退避：1、2、4、8、16、30 分钟封顶，避免无限快速重试。
    public static TimeSpan ComputeRetryDelay(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return TimeSpan.Zero;
        }
        var shift = Math.Min(consecutiveFailures - 1, 5);
        var delay = TimeSpan.FromTicks(BaseRetryDelay.Ticks << shift);
        return delay > MaxRetryDelay ? MaxRetryDelay : delay;
    }

    public Task<WeatherSnapshot> RefreshAsync(
        string city,
        double latitude,
        double longitude,
        CancellationToken cancellationToken = default) =>
        RefreshAsync(city, latitude, longitude, forceRefresh: true, cancellationToken);

    public async Task<WeatherSnapshot> RefreshAsync(
        string city,
        double latitude,
        double longitude,
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        var trimmedCity = city?.Trim() ?? "";
        if (trimmedCity.Length == 0)
        {
            return new WeatherSnapshot(
                WeatherStatus.NoCity, "", null, null, null, null, "", false, null, null);
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var locationKey = CreateLocationKey(trimmedCity, latitude, longitude);
            var state = _state;
            if (!string.Equals(state.Key, locationKey, StringComparison.Ordinal))
            {
                state = new LocationState(locationKey, null, null, 0, null);
                _state = state;
            }
            else if (!forceRefresh)
            {
                // Deduplicate concurrent/automatic refreshes across multiple monitors sharing one WeatherService.
                var now = _time.GetLocalNow();
                if (state.LastSnapshot is { Status: WeatherStatus.Fresh, UpdatedAt: { } last }
                    && now - last < SuccessRefreshInterval)
                {
                    return state.LastSnapshot;
                }
            }

            try
            {
                var json = await _httpClient
                    .GetStringAsync(BuildRequestUrl(latitude, longitude), cancellationToken)
                    .ConfigureAwait(false);
                WeatherSnapshot snapshot;
                try
                {
                    snapshot = ParseForecast(json, trimmedCity, _time.GetLocalNow());
                }
                catch (Exception exception) when (exception is KeyNotFoundException
                    or InvalidOperationException or FormatException)
                {
                    throw new JsonException("天气数据字段无效", exception);
                }
                _state = new LocationState(locationKey, snapshot, snapshot, 0, null);
                return snapshot;
            }
            catch (Exception exception) when (exception is OperationCanceledException
                or HttpRequestException
                or JsonException)
            {
                var failures = state.ConsecutiveFailures + 1;
                var retryAt = _time.GetLocalNow() + ComputeRetryDelay(failures);
                var message = exception switch
                {
                    OperationCanceledException => "请求超时",
                    JsonException => "返回数据无法解析",
                    _ => "网络请求失败"
                };
                var snapshot = state.LastSuccess is not null
                    ? state.LastSuccess with
                    {
                        Status = WeatherStatus.CachedFallback,
                        FromCache = true,
                        ErrorMessage = message
                    }
                    : new WeatherSnapshot(
                        WeatherStatus.Failed, trimmedCity,
                        null, null, null, null, "", false, null, message);
                _state = state with
                {
                    LastSnapshot = snapshot,
                    ConsecutiveFailures = failures,
                    NextRetryAt = retryAt
                };
                return snapshot;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public static string BuildRequestUrl(double latitude, double longitude) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{ForecastEndpoint}?latitude={latitude:0.#####}&longitude={longitude:0.#####}&current=temperature_2m,relative_humidity_2m,weather_code&daily=temperature_2m_max,temperature_2m_min&forecast_days=1&timezone=auto");

    public static WeatherSnapshot ParseForecast(string json, string city, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var current = root.GetProperty("current");
        var temperature = current.GetProperty("temperature_2m").GetDouble();
        int? humidity = current.TryGetProperty("relative_humidity_2m", out var humidityElement)
            ? humidityElement.GetInt32()
            : null;
        var condition = current.TryGetProperty("weather_code", out var codeElement)
            ? DescribeWeatherCode(codeElement.GetInt32())
            : "未知天气";

        double? high = null;
        double? low = null;
        if (root.TryGetProperty("daily", out var daily))
        {
            high = ReadFirstDailyValue(daily, "temperature_2m_max");
            low = ReadFirstDailyValue(daily, "temperature_2m_min");
        }

        return new WeatherSnapshot(
            WeatherStatus.Fresh, city, temperature, high, low, humidity,
            condition, false, now, null);
    }

    // WMO 天气代码，含义见 Open-Meteo 官方文档。
    public static string DescribeWeatherCode(int code) =>
        code switch
        {
            0 => "晴",
            1 => "大致晴",
            2 => "局部多云",
            3 => "阴",
            45 or 48 => "雾",
            51 or 53 or 55 => "毛毛雨",
            56 or 57 => "冻毛毛雨",
            61 => "小雨",
            63 => "中雨",
            65 => "大雨",
            66 or 67 => "冻雨",
            71 => "小雪",
            73 => "中雪",
            75 => "大雪",
            77 => "雪粒",
            80 => "小阵雨",
            81 => "阵雨",
            82 => "强阵雨",
            85 or 86 => "阵雪",
            95 => "雷暴",
            96 or 99 => "雷暴伴冰雹",
            _ => "未知天气"
        };

    private static double? ReadFirstDailyValue(JsonElement daily, string property)
    {
        if (daily.TryGetProperty(property, out var values) &&
            values.ValueKind == JsonValueKind.Array &&
            values.GetArrayLength() > 0 &&
            values[0].ValueKind == JsonValueKind.Number)
        {
            return values[0].GetDouble();
        }
        return null;
    }

    private static string CreateLocationKey(string city, double latitude, double longitude) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{city.Trim().ToUpperInvariant()}|{latitude:R}|{longitude:R}");
}
