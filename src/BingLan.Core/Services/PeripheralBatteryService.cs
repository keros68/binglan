using System.Text.Json;
using BingLan.Core.Models;

namespace BingLan.Core.Services;

/// <summary>Parses BatteryHub CLI output and shares one bounded refresh schedule.</summary>
public sealed class PeripheralBatteryService
{
    public static readonly TimeSpan SuccessRefreshInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    private readonly Func<CancellationToken, Task<string?>> _read;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private PeripheralBatterySnapshot _snapshot = PeripheralBatterySnapshot.Empty;
    private DateTimeOffset _nextAttemptAt = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private Task<PeripheralBatterySnapshot>? _refresh;

    public PeripheralBatteryService(
        Func<CancellationToken, Task<string?>> read,
        TimeProvider? time = null)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _time = time ?? TimeProvider.System;
    }

    public PeripheralBatterySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    public bool IsAutomaticRefreshDue()
    {
        lock (_gate)
        {
            return _refresh is not { IsCompleted: false } && _time.GetUtcNow() >= _nextAttemptAt;
        }
    }

    public Task<PeripheralBatterySnapshot> RefreshAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_refresh is { IsCompleted: false } inFlight)
            {
                return inFlight.WaitAsync(cancellationToken);
            }
            if (!force && _time.GetUtcNow() < _nextAttemptAt)
            {
                return Task.FromResult(_snapshot);
            }

            var completion = new TaskCompletionSource<PeripheralBatterySnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _refresh = completion.Task;
            _ = ReadAndPublishAsync(completion, cancellationToken);
            // The owner can release the scan slot once the provider has finished shutting down.
            return completion.Task;
        }
    }

    private async Task ReadAndPublishAsync(
        TaskCompletionSource<PeripheralBatterySnapshot> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = await _read(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new JsonException("No BatteryHub output.");
            }

            var completedAt = _time.GetUtcNow();
            var snapshot = Parse(json, completedAt);
            lock (_gate)
            {
                // Providers may finish after cancellation even when they ignore their token.
                cancellationToken.ThrowIfCancellationRequested();
                _snapshot = snapshot;
                _consecutiveFailures = 0;
                _nextAttemptAt = completedAt + SuccessRefreshInterval;
            }
            completion.TrySetResult(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }
                _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 5);
                var delay = TimeSpan.FromTicks(Math.Min(
                    BaseRetryDelay.Ticks << (_consecutiveFailures - 1),
                    MaxRetryDelay.Ticks));
                _nextAttemptAt = _time.GetUtcNow() + delay;
                _snapshot = _snapshot with
                {
                    Status = _snapshot.UpdatedAt is null
                        ? PeripheralBatteryStatus.SourceUnavailable
                        : PeripheralBatteryStatus.Stale,
                    ErrorMessage = ex is JsonException
                        ? "外设电量来源返回的数据无效。"
                        : "暂时无法读取外设电量。"
                };
                completion.TrySetResult(_snapshot);
            }
        }
    }

    /// <summary>Maps the documented BatteryHub v1.1.1 --json top-level array.</summary>
    public static PeripheralBatterySnapshot Parse(string json, DateTimeOffset updatedAt)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("BatteryHub output must be an array.");
        }

        var devices = new List<PeripheralDevice>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("BatteryHub device must be an object.");
            }
            var key = GetString(element, "key");
            if (key.Length == 0 || !keys.Add(key))
            {
                continue;
            }
            var name = GetString(element, "name");
            devices.Add(new PeripheralDevice(
                key,
                name.Length > 0 ? name : "未命名设备",
                ParseKind(GetString(element, "kind")),
                GetString(element, "via"),
                GetPercent(element),
                GetBool(element, "charging"),
                GetBool(element, "asleep"),
                GetBool(element, "connected")));
        }

        return new PeripheralBatterySnapshot(
            PeripheralBatteryStatus.Fresh, devices.AsReadOnly(), updatedAt);
    }

    private static PeripheralDeviceKind ParseKind(string kind) => kind.ToLowerInvariant() switch
    {
        "mouse" => PeripheralDeviceKind.Mouse,
        "headset" => PeripheralDeviceKind.Headset,
        "keyboard" => PeripheralDeviceKind.Keyboard,
        "gamepad" => PeripheralDeviceKind.Gamepad,
        "phone" => PeripheralDeviceKind.Phone,
        _ => PeripheralDeviceKind.Other
    };

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static int GetPercent(JsonElement element) =>
        element.TryGetProperty("percent", out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var percent) &&
        percent is >= 0 and <= 100 ? percent : -1;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
