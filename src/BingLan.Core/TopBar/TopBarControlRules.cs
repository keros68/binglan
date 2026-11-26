namespace BingLan.Core.TopBar;

public sealed record TopBarAudioDevice(
    string Id,
    string Name,
    bool IsDefault);

public sealed record TopBarAudioSession(
    uint ProcessId,
    string DisplayName,
    int VolumePercent,
    bool IsMuted,
    bool IsActive);

public sealed record TopBarAudioSnapshot(
    int MasterPercent,
    bool MasterMuted,
    bool Available,
    IReadOnlyList<TopBarAudioDevice> Devices,
    IReadOnlyList<TopBarAudioSession> Sessions,
    string? DefaultDeviceId = null)
{
    public static TopBarAudioSnapshot Unavailable { get; } = new(0, false, false, [], [], null);
}

public enum TopBarPowerMode
{
    Unknown = 0,
    BestPowerEfficiency = 1,
    Balanced = 2,
    BestPerformance = 3
}

public sealed record TopBarBatterySnapshot(
    int Percent,
    TopBarBatteryStatus Status,
    bool IsPowerSaver,
    int? RemainingSeconds,
    bool PowerModeSupported,
    TopBarPowerMode PowerMode,
    bool BrightnessSupported,
    int BrightnessPercent);

public sealed record TopBarWifiNetwork(
    string Ssid,
    string? ProfileName,
    int SignalQuality,
    bool IsConnected,
    bool IsSaved,
    bool IsSecured);

public sealed record TopBarNetworkSnapshot(
    bool IsConnected,
    bool IsWireless,
    string ConnectionTitle,
    string? CurrentSsid,
    string? CurrentProfileName,
    int? SignalQuality,
    long? LinkSpeedBitsPerSecond,
    string? Ipv4Address,
    string? AdapterName,
    Guid InterfaceGuid,
    bool HasWifiInterface,
    bool WifiListAccessDenied,
    IReadOnlyList<TopBarWifiNetwork> AvailableNetworks)
{
    public static TopBarNetworkSnapshot Disconnected { get; } = new(
        IsConnected: false,
        IsWireless: false,
        ConnectionTitle: "未连接",
        CurrentSsid: null,
        CurrentProfileName: null,
        SignalQuality: null,
        LinkSpeedBitsPerSecond: null,
        Ipv4Address: null,
        AdapterName: null,
        InterfaceGuid: Guid.Empty,
        HasWifiInterface: false,
        WifiListAccessDenied: false,
        AvailableNetworks: []);
}

/// <summary>
/// Pure formatting, ordering and normalization rules for the TopBar Volume, Battery and Network flyouts.
/// </summary>
public static class TopBarControlRules
{
    public const int DefaultVolumeWheelStep = 4;
    public const int MaxAudioSessions = 10;
    public const long FlyoutLeaveAutoCloseMs = 1500;

    /// <summary>
    /// Decides whether an open TopBar flyout should auto-close after the pointer has
    /// left both the flyout and its owning bar button without clicking anywhere else.
    /// </summary>
    public static bool ShouldAutoCloseFlyoutOnLeave(
        long? leftSinceTickMs,
        long nowTickMs,
        bool pointerInsideFlyoutOrAnchor,
        bool interactionBusy)
    {
        if (pointerInsideFlyoutOrAnchor || interactionBusy || leftSinceTickMs is not { } started)
        {
            return false;
        }
        return nowTickMs - started >= FlyoutLeaveAutoCloseMs;
    }

    public static int ClampPercent(int value) => Math.Clamp(value, 0, 100);

    public static int AdjustVolumeByWheelDelta(int currentPercent, int wheelDelta, int step = DefaultVolumeWheelStep)
    {
        if (wheelDelta == 0)
        {
            return ClampPercent(currentPercent);
        }

        var safeStep = Math.Max(1, step);
        var notches = Math.Max(1, Math.Abs(wheelDelta) / 120);
        var signedDelta = wheelDelta > 0 ? notches * safeStep : -(notches * safeStep);
        return ClampPercent(currentPercent + signedDelta);
    }

    public static string PowerModeLabel(TopBarPowerMode mode) => mode switch
    {
        TopBarPowerMode.BestPowerEfficiency => "最佳能效",
        TopBarPowerMode.BestPerformance => "最佳性能",
        _ => "平衡"
    };

    public static string FormatBatteryHeadline(TopBarBatteryStatus status, int percent, bool isPowerSaver)
    {
        var baseText = status switch
        {
            TopBarBatteryStatus.Charging when percent >= 100 => "已充满",
            TopBarBatteryStatus.Charging => "正在充电",
            TopBarBatteryStatus.PluggedIn when percent < 0 => "外接电源",
            TopBarBatteryStatus.PluggedIn => "已接通电源",
            TopBarBatteryStatus.OnBattery => "使用电池",
            _ => "电源状态未知"
        };
        return isPowerSaver && status is TopBarBatteryStatus.OnBattery or TopBarBatteryStatus.Charging
            ? $"{baseText} · 省电模式"
            : baseText;
    }

    public static string FormatBatteryRemaining(
        TopBarBatteryStatus status,
        int percent,
        int? remainingSeconds,
        bool isPowerSaver)
    {
        if (status == TopBarBatteryStatus.PluggedIn && percent < 0)
        {
            return "未检测到电池";
        }
        if (status == TopBarBatteryStatus.Charging)
        {
            return percent >= 100 ? "电池已充满" : "接通电源充电中";
        }
        if (status == TopBarBatteryStatus.PluggedIn)
        {
            return percent >= 100 ? "已充满并接通电源" : "已接通电源";
        }
        if (status == TopBarBatteryStatus.OnBattery && remainingSeconds is > 0 and < 360000)
        {
            var totalMinutes = Math.Max(1, (int)Math.Round(remainingSeconds.Value / 60d));
            var hours = totalMinutes / 60;
            var minutes = totalMinutes % 60;
            var estimate = hours switch
            {
                > 0 when minutes > 0 => $"预计可用 {hours} 小时 {minutes} 分钟",
                > 0 => $"预计可用 {hours} 小时",
                _ => $"预计可用 {minutes} 分钟"
            };
            return isPowerSaver ? $"{estimate}（省电模式）" : estimate;
        }
        if (status == TopBarBatteryStatus.OnBattery)
        {
            return isPowerSaver ? "正在使用电池（省电模式）" : "正在计算剩余使用时间";
        }
        return "等待系统汇报电源状态";
    }

    public static string? FormatLinkSpeed(long? bitsPerSecond)
    {
        if (bitsPerSecond is not > 0)
        {
            return null;
        }

        if (bitsPerSecond.Value >= 1_000_000_000L)
        {
            return $"{bitsPerSecond.Value / 1_000_000_000d:0.#} Gbps";
        }
        if (bitsPerSecond.Value >= 1_000_000L)
        {
            return $"{Math.Max(1L, bitsPerSecond.Value / 1_000_000L)} Mbps";
        }
        return $"{Math.Max(1L, bitsPerSecond.Value / 1_000L)} Kbps";
    }

    public static IReadOnlyList<TopBarWifiNetwork> DeduplicateAndSortWifiNetworks(
        IEnumerable<TopBarWifiNetwork> raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        // Preserve distinct (SSID, security, profile) combinations so a saved profile is never
        // collapsed onto a different network or mismatched security configuration.
        var byKey = new Dictionary<(string Ssid, bool IsSecured, string ProfileKey), TopBarWifiNetwork>();
        foreach (var item in raw)
        {
            var ssid = item.Ssid?.Trim();
            if (string.IsNullOrEmpty(ssid))
            {
                continue;
            }
            var profile = string.IsNullOrWhiteSpace(item.ProfileName) ? null : item.ProfileName;
            var key = (ssid, item.IsSecured, profile ?? string.Empty);
            var normalized = item with
            {
                Ssid = ssid,
                ProfileName = profile,
                IsSaved = item.IsSaved && profile is not null,
                SignalQuality = ClampPercent(item.SignalQuality)
            };
            if (!byKey.TryGetValue(key, out var existing))
            {
                byKey[key] = normalized;
                continue;
            }

            byKey[key] = existing with
            {
                SignalQuality = Math.Max(existing.SignalQuality, normalized.SignalQuality),
                IsConnected = existing.IsConnected || normalized.IsConnected,
                IsSaved = existing.IsSaved || normalized.IsSaved
            };
        }

        // If an AP broadcasts both a profiled entry and an unprofiled duplicate beacon for the
        // exact same (SSID, security), keep the stronger signal on the profiled entry and drop
        // the redundant unprofiled duplicate.
        var profiledKeys = byKey.Keys
            .Where(k => k.ProfileKey.Length > 0)
            .Select(k => (k.Ssid, k.IsSecured))
            .ToHashSet();
        var merged = new List<TopBarWifiNetwork>(byKey.Count);
        foreach (var (key, value) in byKey)
        {
            if (key.ProfileKey.Length == 0 && profiledKeys.Contains((key.Ssid, key.IsSecured)))
            {
                continue;
            }
            var signal = value.SignalQuality;
            if (key.ProfileKey.Length > 0
                && byKey.TryGetValue((key.Ssid, key.IsSecured, string.Empty), out var unprofiled))
            {
                signal = Math.Max(signal, unprofiled.SignalQuality);
            }
            merged.Add(value with { SignalQuality = signal });
        }

        return merged
            .OrderByDescending(network => network.IsConnected)
            .ThenByDescending(network => network.IsSaved)
            .ThenByDescending(network => network.SignalQuality)
            .ThenBy(network => network.Ssid, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<TopBarAudioSession> NormalizeAudioSessions(
        IEnumerable<TopBarAudioSession> raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var byPid = new Dictionary<uint, TopBarAudioSession>();
        foreach (var session in raw)
        {
            if (session.ProcessId == 0)
            {
                continue;
            }
            var name = session.DisplayName?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }
            var normalized = session with
            {
                DisplayName = name,
                VolumePercent = ClampPercent(session.VolumePercent)
            };
            if (!byPid.TryGetValue(session.ProcessId, out var existing))
            {
                byPid[session.ProcessId] = normalized;
                continue;
            }

            byPid[session.ProcessId] = new TopBarAudioSession(
                ProcessId: session.ProcessId,
                DisplayName: existing.DisplayName,
                VolumePercent: Math.Max(existing.VolumePercent, normalized.VolumePercent),
                IsMuted: existing.IsMuted && normalized.IsMuted,
                IsActive: existing.IsActive || normalized.IsActive);
        }

        return byPid.Values
            .OrderByDescending(session => session.IsActive)
            .ThenBy(session => session.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(MaxAudioSessions)
            .ToList();
    }
}
