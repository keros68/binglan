using System.Globalization;
using System.Text.Json;
using BingLan.Core.Models;

namespace BingLan.Core.Services;

// 外设电量读数服务：通过注入的提供者取得 BatteryEngine.dll 的一次轮询 JSON
// （与 `BatteryHub --json` 同一份文档），解析为快照并维护失败退避。
// 按仓库架构规则，P/Invoke 本身留在 BingLan.App，本服务只依赖一个返回 JSON 的委托，
// 因此解析与状态逻辑可脱离 UI 测试。轮询节奏由上层协调器集中调度，本服务不自带计时器。
public sealed class PeripheralBatteryService
{
    // 引擎自身约 60 秒轮询一次设备；15 秒取一次既跟得上插拔变化，又不会让阻塞 1–2 秒的
    // 轮询调用过于频繁。
    public static readonly TimeSpan SuccessRefreshInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    private readonly Func<string?> _pollJson;
    private readonly Func<string?>? _pollExtrasJson;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private volatile bool _forceDue;

    // 成功后的自动刷新间隔；协调器按用户设置（顶栏状态里的 PeripheralPollSeconds）写入，
    // 这里只做范围钳制。引擎轮询本身阻塞 1–2 秒，间隔下限不能太小。
    public TimeSpan PollInterval { get; set; } = SuccessRefreshInterval;

    private TimeSpan EffectivePollInterval => PollInterval < TimeSpan.FromSeconds(5)
        ? TimeSpan.FromSeconds(5)
        : PollInterval > TimeSpan.FromHours(1)
            ? TimeSpan.FromHours(1)
            : PollInterval;

    // 「立即刷新」：下一次 IsAutomaticRefreshDue 直接放行（仍受正在刷新中的闸门约束）。
    public void ForceRefreshDue() => _forceDue = true;

    private sealed record PollState(
        PeripheralBatterySnapshot? LastSuccess,
        int ConsecutiveFailures,
        DateTimeOffset? NextRetryAt,
        string? LastError);

    private volatile PollState _state = new(null, 0, null, null);

    // pollJson 返回 null 或抛异常都视为本轮失败（引擎缺失时 App 侧的原生封装返回 null）。
    // pollExtrasJson 可选：取 BhExtrasJson 的历史估算文档；它只是装饰，取不到不影响主轮询成败。
    public PeripheralBatteryService(
        Func<string?> pollJson,
        TimeProvider? time = null,
        Func<string?>? pollExtrasJson = null)
    {
        _pollJson = pollJson ?? throw new ArgumentNullException(nameof(pollJson));
        _time = time ?? TimeProvider.System;
        _pollExtrasJson = pollExtrasJson;
    }

    public PeripheralBatterySnapshot GetSnapshot()
    {
        var state = _state;
        if (state.LastSuccess is { } success)
        {
            return state.ConsecutiveFailures == 0
                ? success
                : success with { Status = PeripheralBatteryStatus.Stale, ErrorMessage = state.LastError };
        }

        return state.ConsecutiveFailures > 0
            ? PeripheralBatterySnapshot.Empty with
            {
                Status = PeripheralBatteryStatus.EngineUnavailable,
                ErrorMessage = state.LastError
            }
            : PeripheralBatterySnapshot.Empty;
    }

    // 退避是强制的调度建议：失败后调用方应等待 ShouldAttemptRefresh 放行再重试。
    public bool ShouldAttemptRefresh(DateTimeOffset now)
    {
        var state = _state;
        return state.ConsecutiveFailures == 0 || state.NextRetryAt is null || now >= state.NextRetryAt.Value;
    }

    public bool IsAutomaticRefreshDue(DateTimeOffset now)
    {
        if (_forceDue)
        {
            _forceDue = false;
            return true;
        }

        var state = _state;
        if (state.LastSuccess is null)
        {
            return ShouldAttemptRefresh(now);
        }

        return now - state.LastSuccess.UpdatedAt.GetValueOrDefault() >= EffectivePollInterval &&
               ShouldAttemptRefresh(now);
    }

    public async Task<PeripheralBatterySnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? json;
            try
            {
                // 引擎轮询是阻塞调用（1–2 秒），放到线程池执行，不占 UI 线程。
                json = await Task.Run(_pollJson, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                json = null;
                RegisterFailure($"引擎轮询失败：{ex.Message}");
                return GetSnapshot();
            }

            if (json is null)
            {
                RegisterFailure("引擎不可用（BatteryEngine.dll 缺失或轮询无结果）。");
                return GetSnapshot();
            }

            try
            {
                var snapshot = Parse(json, _time.GetUtcNow());
                snapshot = await EnrichWithExtrasAsync(snapshot, cancellationToken).ConfigureAwait(false);
                _state = new PollState(snapshot, 0, null, null);
                return snapshot;
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
            {
                RegisterFailure($"引擎返回的数据无法解析：{ex.Message}");
                return GetSnapshot();
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    // 历史估算是装饰信息：提供者缺失、抛错或文档坏掉都原样返回主快照，绝不拖垮主轮询。
    private async Task<PeripheralBatterySnapshot> EnrichWithExtrasAsync(
        PeripheralBatterySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (_pollExtrasJson is null)
        {
            return snapshot;
        }

        string? extrasJson;
        try
        {
            extrasJson = await Task.Run(_pollExtrasJson, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return snapshot;
        }

        if (extrasJson is null)
        {
            return snapshot;
        }

        IReadOnlyDictionary<string, (string Estimate, string TimeToEmpty)> extras;
        try
        {
            extras = ParseExtras(extrasJson);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return snapshot;
        }

        if (extras.Count == 0)
        {
            return snapshot;
        }

        var enriched = snapshot.Devices
            .Select(device => extras.TryGetValue(device.Key, out var extra)
                ? device with { EstimateText = PickEstimate(device, extra) }
                : device)
            .ToList();
        return snapshot with { Devices = enriched };
    }

    // 充电时「还能用多久」没有意义（引擎的历史估算针对放电）；放电时优先用估算文本，
    // 没有估算文本时退到 TimeToEmpty。
    private static string PickEstimate(
        PeripheralDevice device,
        (string Estimate, string TimeToEmpty) extra)
    {
        if (device.Charging)
        {
            return string.Empty;
        }

        return extra.Estimate.Length > 0 ? extra.Estimate : extra.TimeToEmpty;
    }

    // 解析 BhExtrasJson 的文档：{"devices":[{"key":...,"estimate":...,"timeToEmpty":...}]}
    public static IReadOnlyDictionary<string, (string Estimate, string TimeToEmpty)> ParseExtras(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("devices", out var devicesEl) &&
            devicesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in devicesEl.EnumerateArray())
            {
                var key = GetString(el, "key");
                if (key.Length == 0)
                {
                    continue;
                }

                result[key] = (GetString(el, "estimate"), GetString(el, "timeToEmpty"));
            }
        }

        return result;
    }

    private void RegisterFailure(string error)
    {
        var state = _state;
        var failures = state.ConsecutiveFailures + 1;
        var shift = Math.Min(failures - 1, 10);
        var delay = TimeSpan.FromTicks(Math.Min(
            BaseRetryDelay.Ticks << shift,
            MaxRetryDelay.Ticks));
        _state = state with
        {
            ConsecutiveFailures = failures,
            NextRetryAt = _time.GetUtcNow() + delay,
            LastError = error
        };
    }

    // 解析 BuildStatusJson 的文档：percent 已是展示值（实时或最后已知），live 区分两者。
    public static PeripheralBatterySnapshot Parse(string json, DateTimeOffset fallbackNow)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var updated = fallbackNow;
        if (root.TryGetProperty("updated", out var updatedEl) &&
            updatedEl.GetString() is { } updatedText &&
            DateTimeOffset.TryParse(updatedText, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            updated = parsed;
        }

        var devices = new List<PeripheralDevice>();
        if (root.TryGetProperty("devices", out var devicesEl) &&
            devicesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in devicesEl.EnumerateArray())
            {
                devices.Add(new PeripheralDevice(
                    GetString(el, "key"),
                    GetString(el, "name"),
                    ParseKind(GetString(el, "kind")),
                    GetString(el, "via"),
                    GetInt(el, "percent", -1),
                    GetBool(el, "live"),
                    GetBool(el, "charging"),
                    GetBool(el, "asleep"),
                    GetBool(el, "connected"),
                    GetBool(el, "low"),
                    GetString(el, "levelText"),
                    GetString(el, "detail"),
                    GetInt(el, "millivolts", 0),
                    string.Empty));
            }
        }

        return new PeripheralBatterySnapshot(
            PeripheralBatteryStatus.Fresh, devices, updated, null);
    }

    private static PeripheralDeviceKind ParseKind(string kind) => kind switch
    {
        "mouse" => PeripheralDeviceKind.Mouse,
        "headset" => PeripheralDeviceKind.Headset,
        "keyboard" => PeripheralDeviceKind.Keyboard,
        "gamepad" => PeripheralDeviceKind.Gamepad,
        "phone" => PeripheralDeviceKind.Phone,
        _ => PeripheralDeviceKind.Other
    };

    private static string GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt(JsonElement el, string name, int fallback) =>
        el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var result)
            ? result
            : fallback;

    private static bool GetBool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True;
}
