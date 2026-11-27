using System.Globalization;
using System.Text;
using BingLan.Core.Models;
using BingLan.Core.Services;

namespace BingLan.Core.TopBar;

public enum TopBarBatteryStatus
{
    OnBattery,
    PluggedIn,
    Charging,
    Unknown
}

public sealed record TopBarNetworkStatus(
    bool Connected,
    bool IsWireless,
    string Label,
    string? Ssid,
    int? SignalQuality,
    string Tooltip);

public sealed record TopBarInputMethodOption(
    string Key,
    string DisplayName,
    string LanguageTag,
    ushort LanguageId,
    bool IsInputProcessor,
    bool IsActive,
    nint KeyboardLayout,
    Guid Clsid,
    Guid ProfileGuid);

/// <summary>Read-only value rules for the top bar's system status modules.</summary>
public static class TopBarModuleRules
{
    private const int BatteryChargingFlag = 8;

    /// <summary>How many to-do items with text are still open across all to-do cards.</summary>
    public static int CountIncompleteTodos(IEnumerable<TodoWidgetState> widgets) =>
        widgets.Sum(widget => widget.Items.Count(item =>
            item.Kind == TodoItemKind.Todo && !item.IsCompleted &&
            !string.IsNullOrWhiteSpace(item.Text)));

    /// <summary>
    /// How many to-do items are still open on today's calendar day.
    /// </summary>
    public static int CountIncompleteTodosToday(IEnumerable<TodoWidgetState> widgets, DateTime? today = null)
    {
        var target = (today ?? DateTime.Today).Date;
        return widgets.Sum(widget => widget.Items.Count(item =>
            item.Kind == TodoItemKind.Todo && !item.IsCompleted &&
            !string.IsNullOrWhiteSpace(item.Text) &&
            TodoService.BelongsToTodayList(item, target, widget.CarryOverIncomplete)));
    }

    /// <summary>The next timed open to-do due today, from the same card as the flyout.</summary>
    public static TodoItemState? NextTimedTodoToday(IEnumerable<TodoItemState> items, DateTime now) =>
        items.Where(item => item.Kind == TodoItemKind.Todo && !item.IsCompleted
                && !string.IsNullOrWhiteSpace(item.Text)
                && item.DeadlineDate?.Date == now.Date
                && item.DeadlineTime is { } time
                && now.Date.Add(time.ToTimeSpan()) >= now)
            .OrderBy(item => item.DeadlineTime)
            .FirstOrDefault();

    public static TopBarBatteryStatus ClassifyBattery(int acLineStatus, int batteryFlag) =>
        acLineStatus switch
        {
            0 => TopBarBatteryStatus.OnBattery,
            1 => batteryFlag == BatteryChargingFlag
                ? TopBarBatteryStatus.Charging
                : TopBarBatteryStatus.PluggedIn,
            _ => TopBarBatteryStatus.Unknown
        };

    /// <summary>Percentages above 100 (128, 255) mean the level is unknown.</summary>
    public static bool IsBatteryPercentKnown(int lifePercent) =>
        lifePercent is >= 0 and <= 100;

    /// <summary>
    /// Classifies the interfaces the app can see: a wireless one wins the label, matching
    /// how Windows prefers Wi-Fi; nothing up means not connected.
    /// </summary>
    public static (bool Connected, string Label) ClassifyNetwork(
        IEnumerable<(bool IsUp, bool IsWireless)> interfaces)
    {
        var usable = interfaces.Where(candidate => candidate.IsUp).ToList();
        if (usable.Count == 0)
        {
            return (false, "未连接");
        }

        return (true, usable.Any(candidate => candidate.IsWireless) ? "WLAN" : "以太网");
    }

    /// <summary>
    /// Normalized snapshot of one OS network interface used by both the TopBar status icon
    /// and the Network flyout so isolated virtual switches, WAN Miniport pseudo-adapters,
    /// and NDIS/WFP filter drivers do not masquerade as a wired Ethernet connection.
    /// </summary>
    public sealed record NetworkAdapterCandidate(
        bool IsUp,
        bool IsWireless,
        bool IsLoopbackOrTunnel,
        bool IsPpp,
        string Name,
        string Description,
        long Speed,
        bool HasDefaultGateway,
        bool HasUsableUnicastIp);

    public static bool IsVirtualOrFilterAdapter(string? name, string? description)
    {
        var text = $"{name} {description}";
        return IsFilterOrPseudoAdapter(name, description)
            || text.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || text.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
            || text.Contains("WSL", StringComparison.OrdinalIgnoreCase)
            || text.Contains("VMware", StringComparison.OrdinalIgnoreCase)
            || text.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Docker", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase)
            || text.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
            || text.Contains("TAP-Windows", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Wintun", StringComparison.OrdinalIgnoreCase)
            || text.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Hamachi", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Radmin", StringComparison.OrdinalIgnoreCase)
            || text.Contains("SoftEther", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Sangfor", StringComparison.OrdinalIgnoreCase)
            || text.Contains("VNIC", StringComparison.OrdinalIgnoreCase)
            || text.Contains("VPN", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFilterOrPseudoAdapter(string? name, string? description)
    {
        var text = $"{name} {description}";
        return text.Contains("WFP ", StringComparison.OrdinalIgnoreCase)
            || text.Contains("LightWeight Filter", StringComparison.OrdinalIgnoreCase)
            || text.Contains("QoS Packet Scheduler", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Filter Driver", StringComparison.OrdinalIgnoreCase)
            || text.Contains("WAN Miniport (IP)", StringComparison.OrdinalIgnoreCase)
            || text.Contains("WAN Miniport (IPv6)", StringComparison.OrdinalIgnoreCase)
            || text.Contains("WAN Miniport (Network Monitor)", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Npcap", StringComparison.OrdinalIgnoreCase)
            || text.Contains("KM-TEST", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUsableNetworkAdapter(NetworkAdapterCandidate adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (!adapter.IsUp || adapter.IsLoopbackOrTunnel)
        {
            return false;
        }

        if (IsVirtualOrFilterAdapter(adapter.Name, adapter.Description))
        {
            // An external Hyper-V switch can own the host's only IP/default route.
            // Keep host-only switches and filter-driver projections out of this fallback.
            return adapter.Speed > 0 && adapter.HasDefaultGateway && adapter.HasUsableUnicastIp
                && !IsFilterOrPseudoAdapter(adapter.Name, adapter.Description)
                && (adapter.Name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
                    || adapter.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase));
        }

        if (adapter.IsPpp)
        {
            return adapter.HasDefaultGateway && adapter.HasUsableUnicastIp;
        }

        if (adapter.Speed <= 0)
        {
            return false;
        }

        return adapter.HasDefaultGateway || adapter.HasUsableUnicastIp;
    }

    /// <summary>
    /// Selects the index of the primary active adapter from <paramref name="adapters"/>, or
    /// <c>-1</c> when no usable wired/wireless/PPPoE or routed Hyper-V host connection is active.
    /// Priority: physical wired with default gateway → wireless with default gateway →
    /// any usable wireless → any usable physical wired (e.g. static LAN without gateway)
    /// → routed Hyper-V host adapter.
    /// </summary>
    public static int SelectActiveNetworkAdapterIndex(IReadOnlyList<NetworkAdapterCandidate> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var wiredWithGateway = -1;
        var wirelessWithGateway = -1;
        var wirelessFallback = -1;
        var wiredFallback = -1;
        var virtualHostFallback = -1;

        for (var i = 0; i < adapters.Count; i++)
        {
            var adapter = adapters[i];
            if (!IsUsableNetworkAdapter(adapter))
            {
                continue;
            }

            if (IsVirtualOrFilterAdapter(adapter.Name, adapter.Description))
            {
                if (virtualHostFallback < 0) virtualHostFallback = i;
                continue;
            }

            if (!adapter.IsWireless)
            {
                if (adapter.HasDefaultGateway && wiredWithGateway < 0)
                {
                    wiredWithGateway = i;
                }
                else if (wiredFallback < 0)
                {
                    wiredFallback = i;
                }
            }
            else
            {
                if (adapter.HasDefaultGateway && wirelessWithGateway < 0)
                {
                    wirelessWithGateway = i;
                }
                else if (wirelessFallback < 0)
                {
                    wirelessFallback = i;
                }
            }
        }

        if (wiredWithGateway >= 0) return wiredWithGateway;
        if (wirelessWithGateway >= 0) return wirelessWithGateway;
        if (wirelessFallback >= 0) return wirelessFallback;
        return wiredFallback >= 0 ? wiredFallback : virtualHostFallback;
    }

    public const int MaxNetworkSsidDisplayLength = 14;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Decodes raw 802.11 SSID bytes: strict UTF-8 first, falling back to the host's ANSI
    /// decoder (e.g. Windows CP_ACP / GBK) when the bytes are not valid UTF-8 so legacy
    /// router broadcasts do not turn into replacement-character garble.
    /// </summary>
    public static string? DecodeSsidBytes(
        ReadOnlySpan<byte> rawBytes,
        Func<ReadOnlySpan<byte>, string?>? ansiFallback = null)
    {
        var nullIndex = rawBytes.IndexOf((byte)0);
        var slice = nullIndex >= 0 ? rawBytes[..nullIndex] : rawBytes;
        if (slice.IsEmpty)
        {
            return null;
        }

        try
        {
            return SanitizeSsid(StrictUtf8.GetString(slice));
        }
        catch (DecoderFallbackException)
        {
            var fallback = ansiFallback?.Invoke(slice);
            if (string.IsNullOrWhiteSpace(fallback) || fallback.Contains('\uFFFD'))
            {
                return null;
            }
            return SanitizeSsid(fallback);
        }
    }

    /// <summary>
    /// Formats a top-bar network snapshot. By default (<paramref name="showWifiName"/> is false)
    /// wireless connections display "WLAN" in the bar to avoid exposing the SSID during screen
    /// recording or streaming, while the hover tooltip keeps the full name and signal quality.
    /// </summary>
    public static TopBarNetworkStatus DescribeNetwork(
        bool connected,
        bool isWireless,
        string? wifiSsid = null,
        int? signalQuality = null,
        bool showWifiName = false)
    {
        if (!connected)
        {
            return new TopBarNetworkStatus(false, false, "未连接", null, null, "网络未连接");
        }

        if (!isWireless)
        {
            return new TopBarNetworkStatus(true, false, "以太网", null, null, "以太网");
        }

        var cleanSsid = SanitizeSsid(wifiSsid);
        var clampedSignal = signalQuality is { } q ? Math.Clamp(q, 0, 100) : (int?)null;
        var label = FormatWirelessLabel(cleanSsid, showWifiName);
        var tooltip = (cleanSsid, clampedSignal) switch
        {
            ({ } ssid, { } sig) => $"WLAN：{ssid}（信号 {sig}%）",
            ({ } ssid, null) => $"WLAN：{ssid}",
            (null, { } sig) => $"WLAN（信号 {sig}%）",
            _ => "WLAN",
        };
        return new TopBarNetworkStatus(true, true, label, cleanSsid, clampedSignal, tooltip);
    }

    /// <summary>Formats the bar text for an already-captured network snapshot.</summary>
    public static string FormatNetworkLabel(
        TopBarNetworkStatus status,
        bool showWifiName,
        bool iconOnly = false)
    {
        if (iconOnly)
        {
            return string.Empty;
        }

        if (!status.Connected)
        {
            return "未连接";
        }
        return status.IsWireless
            ? FormatWirelessLabel(SanitizeSsid(status.Ssid), showWifiName)
            : "以太网";
    }

    private static string FormatWirelessLabel(string? cleanSsid, bool showWifiName) =>
        !showWifiName || cleanSsid is null
            ? "WLAN"
            : cleanSsid.Length <= MaxNetworkSsidDisplayLength
                ? cleanSsid
                : cleanSsid[..(MaxNetworkSsidDisplayLength - 1)] + "…";

    /// <summary>Maps 0–100 native Wi-Fi signal quality into 1–4 display bars.</summary>
    public static int WifiSignalBars(int? signalQuality)
    {
        if (signalQuality is not { } quality)
        {
            return 4;
        }
        var clamped = Math.Clamp(quality, 0, 100);
        return clamped switch
        {
            < 25 => 1,
            < 50 => 2,
            < 75 => 3,
            _ => 4,
        };
    }

    private static string? SanitizeSsid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var cleaned = new string(raw.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>A keyboard layout handle carries the language id in its low word.</summary>
    public static ushort ExtractLanguageId(nint keyboardLayout) =>
        (ushort)(keyboardLayout & 0xFFFF);

    /// <summary>
    /// Returns the registry subkey names under Keyboard Layouts for an HKL, masking to 32 bits
    /// first so sign-extended 64-bit handles (e.g. legacy IMM32 0xFFFFFFFFE0200804) still hit
    /// their 8-character E0200804 subkey.
    /// </summary>
    public static IReadOnlyList<string> FormatLayoutRegistryCandidates(nint keyboardLayout)
    {
        if (keyboardLayout == 0)
        {
            return [];
        }

        var raw = unchecked((uint)keyboardLayout);
        var full = raw.ToString("X8");
        var high = ((raw >> 16) & 0xFFFF).ToString("X8");
        var low = (raw & 0xFFFF).ToString("X8");
        var result = new List<string>(3) { full };
        if (high != full)
        {
            result.Add(high);
        }
        if (low != full && low != high)
        {
            result.Add(low);
        }
        return result;
    }

    /// <summary>
    /// Legacy IMM32 IMEs (0xE000..0xEFFF in the high word) carry their own layout name in the
    /// registry rather than a TSF text input processor profile. Substitute layouts (0xF000+) and
    /// standard layouts (e.g. 0x04090409) are plain keyboards.
    /// </summary>
    public static bool IsLegacyImeLayout(nint keyboardLayout)
    {
        var high = (unchecked((uint)keyboardLayout) >> 16) & 0xFFFF;
        return high is >= 0xE000 and < 0xF000;
    }

    /// <summary>
    /// Chooses the input-method name shown next to the badge: the active TSF processor name when
    /// available, falling back to the registry layout text for legacy IMM32 IMEs, and collapsing
    /// for plain keyboard layouts.
    /// </summary>
    public static string? ResolveInputMethodDisplayName(
        string? inputProcessorName,
        nint keyboardLayout,
        string? layoutText,
        ushort? inputProcessorLanguageId = null)
    {
        if (!string.IsNullOrWhiteSpace(inputProcessorName))
        {
            var layoutLangId = keyboardLayout != 0 ? ExtractLanguageId(keyboardLayout) : (ushort)0;
            if (layoutLangId == 0
                || inputProcessorLanguageId is null or 0
                || inputProcessorLanguageId.Value == layoutLangId)
            {
                return inputProcessorName.Trim();
            }
        }
        if (IsLegacyImeLayout(keyboardLayout)
            && !string.IsNullOrWhiteSpace(layoutText)
            && !string.Equals(layoutText.Trim(), "键盘", StringComparison.Ordinal))
        {
            return layoutText.Trim();
        }
        return null;
    }

    /// <summary>Short language badge for an input method picker row.</summary>
    public static string FormatInputMethodLanguageTag(ushort languageId) => languageId switch
    {
        0x0804 or 0x1004 => "中",
        0x0404 or 0x0C04 or 0x1404 => "繁",
        _ => LanguageCode(languageId)
    };

    /// <summary>Stable key for identifying and deduplicating TSF profiles and keyboard layouts.</summary>
    public static string BuildInputMethodKey(
        bool isInputProcessor,
        ushort languageId,
        Guid clsid,
        Guid profileGuid,
        nint keyboardLayout) =>
        isInputProcessor
            ? $"tip:{languageId:X4}:{clsid:N}:{profileGuid:N}"
            : $"hkl:{languageId:X4}:{unchecked((uint)keyboardLayout):X8}";

    /// <summary>
    /// Drops blank entries and deduplicates by stable key while keeping original order and
    /// preserving any active flag across duplicates.
    /// </summary>
    public static IReadOnlyList<TopBarInputMethodOption> NormalizeInputMethodOptions(
        IEnumerable<TopBarInputMethodOption> options)
    {
        var list = new List<TopBarInputMethodOption>();
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options)
        {
            if (string.IsNullOrWhiteSpace(option.Key) || string.IsNullOrWhiteSpace(option.DisplayName))
            {
                continue;
            }
            var trimmed = option with
            {
                DisplayName = option.DisplayName.Trim(),
                LanguageTag = string.IsNullOrWhiteSpace(option.LanguageTag)
                    ? FormatInputMethodLanguageTag(option.LanguageId)
                    : option.LanguageTag.Trim()
            };
            if (indexByKey.TryGetValue(trimmed.Key, out var existingIndex))
            {
                if (trimmed.IsActive && !list[existingIndex].IsActive)
                {
                    list[existingIndex] = trimmed;
                }
                continue;
            }
            indexByKey[trimmed.Key] = list.Count;
            list.Add(trimmed);
        }
        return list;
    }

    /// <summary>The IME conversion-mode bit that marks native (Chinese) composition.</summary>
    public const int ConversionModeNative = 0x0001;

    /// <summary>
    /// The taskbar-style short badge for the current input method: the Chinese IME's own
    /// Chinese/English toggle shows "中"/"繁"/"英", every other layout shows its ISO
    /// language code. A missing conversion mode (the IME did not answer) falls back to
    /// the language code rather than guessing the mode.
    /// </summary>
    public static string DescribeInputMethod(nint keyboardLayout, int? conversionMode)
    {
        if (keyboardLayout == 0)
        {
            return "键盘";
        }

        var languageId = ExtractLanguageId(keyboardLayout);
        // An IME carries a variant in the high word (E0200804); a plain layout does not
        // (00000409), and only IMEs report a conversion mode worth showing.
        var isChineseIme = languageId is 0x0804 or 0x0404 && (keyboardLayout >> 16) != 0;
        if (isChineseIme && conversionMode is { } mode)
        {
            var native = (mode & ConversionModeNative) != 0;
            return native
                ? (languageId == 0x0804 ? "中" : "繁")
                : "英";
        }

        return LanguageCode(languageId);
    }

    private static string LanguageCode(ushort languageId)
    {
        try
        {
            return new CultureInfo(languageId).TwoLetterISOLanguageName.ToUpperInvariant();
        }
        catch (CultureNotFoundException)
        {
            return "键盘";
        }
    }

    /// <summary>Volume as a whole percent; the API reports a 0–1 float.</summary>
    public static int VolumePercentFromScalar(float scalar) =>
        double.IsNaN(scalar) || scalar < 0 ? 0 : Math.Clamp((int)Math.Round(scalar * 100d), 0, 100);

    /// <summary>
    /// Formats the volume readout for the top bar. Muted state always stays icon-only (empty
    /// text) because the muted glyph already conveys mute; IconOnly hides the percentage too.
    /// </summary>
    public static string FormatVolumeLabel(
        int? percent,
        bool muted,
        TopBarVolumeStyle style = TopBarVolumeStyle.IconAndPercent)
    {
        if (muted || style == TopBarVolumeStyle.IconOnly)
        {
            return string.Empty;
        }

        return percent is { } value ? $"{value}%" : "—";
    }

    public static string FormatVolumeTooltip(int? percent, bool muted)
    {
        if (muted)
        {
            return percent is { } value ? $"音量：静音（{value}%）" : "音量：静音";
        }

        return percent is { } level ? $"音量：{level}%" : "音量";
    }

    public static string FormatBatteryLabel(
        int percent,
        TopBarBatteryStyle style = TopBarBatteryStyle.IconAndPercent)
    {
        if (style == TopBarBatteryStyle.IconOnly)
        {
            return string.Empty;
        }

        return percent < 0 ? "—" : $"{percent}%";
    }

    public static string FormatBatteryTooltip(int percent, TopBarBatteryStatus status)
    {
        if (percent < 0)
        {
            return "电量";
        }

        return status switch
        {
            TopBarBatteryStatus.Charging => $"电量：{percent}%（充电中）",
            TopBarBatteryStatus.PluggedIn => $"电量：{percent}%（已接通电源）",
            _ => $"电量：{percent}%"
        };
    }

    public static string FormatPerformanceLabel(
        PerformanceSnapshot? snapshot,
        TopBarPerformanceStyle style = TopBarPerformanceStyle.Full)
    {
        if (snapshot is null)
        {
            return style == TopBarPerformanceStyle.Full ? "性能 …" : "…";
        }

        return style switch
        {
            TopBarPerformanceStyle.CpuOnly =>
                $"CPU {PerformanceSamplingRules.FormatPercent(snapshot.CpuPercent)}",
            TopBarPerformanceStyle.Compact =>
                $"CPU {PerformanceSamplingRules.FormatPercent(snapshot.CpuPercent)}  ↓{PerformanceSamplingRules.FormatRate(snapshot.DownloadBytesPerSecond)}",
            _ =>
                $"CPU {PerformanceSamplingRules.FormatPercent(snapshot.CpuPercent)}  "
                + $"内存 {PerformanceSamplingRules.FormatPercent(snapshot.MemoryPercent)}  "
                + $"↑{PerformanceSamplingRules.FormatRate(snapshot.UploadBytesPerSecond)} "
                + $"↓{PerformanceSamplingRules.FormatRate(snapshot.DownloadBytesPerSecond)}"
        };
    }

    public static string FormatPerformanceTooltip(PerformanceSnapshot? snapshot) =>
        snapshot is null
            ? "性能（点击打开任务管理器）"
            : $"CPU：{PerformanceSamplingRules.FormatPercent(snapshot.CpuPercent)}\n"
                + $"内存：{PerformanceSamplingRules.FormatPercent(snapshot.MemoryPercent)}\n"
                + $"上传：{PerformanceSamplingRules.FormatRate(snapshot.UploadBytesPerSecond)}\n"
                + $"下载：{PerformanceSamplingRules.FormatRate(snapshot.DownloadBytesPerSecond)}";

    public static string FormatWeatherLabel(
        WeatherSnapshot? snapshot,
        TopBarWeatherStyle style = TopBarWeatherStyle.Full)
    {
        if (snapshot is null)
        {
            return style == TopBarWeatherStyle.Full ? "天气 —" : "—";
        }

        if (snapshot.Status == WeatherStatus.NoCity)
        {
            return style == TopBarWeatherStyle.Full ? "天气 未设置" : "未设城市";
        }

        if (style == TopBarWeatherStyle.Compact)
        {
            return snapshot.TemperatureCelsius is { } temp
                ? $"{Math.Round(temp):0}°"
                : snapshot.Condition;
        }

        if (style == TopBarWeatherStyle.TempAndCondition)
        {
            return snapshot.TemperatureCelsius is { } temp
                ? $"{Math.Round(temp):0}° {snapshot.Condition}"
                : snapshot.Condition;
        }

        var city = WeatherSnapshot.CompactLocation(snapshot.City);
        return snapshot.TemperatureCelsius is { } temperature
            ? $"{city} {Math.Round(temperature):0}° {snapshot.Condition}"
            : $"{city} {snapshot.Condition}";
    }

    /// <summary>
    /// Formats the top bar clock readout, following the time/date widget's 12/24-hour,
    /// date visibility and lunar date preferences while applying the top bar's clock density style.
    /// </summary>
    public static string FormatClock(
        DateTime now,
        bool use24HourClock,
        bool showDate = true,
        bool showLunarDate = false,
        TopBarClockStyle style = TopBarClockStyle.Full)
    {
        var time = use24HourClock
            ? now.ToString("HH:mm", CultureInfo.InvariantCulture)
            : now.ToString("hh:mm tt", CultureInfo.CurrentCulture);
        if (!showDate)
        {
            return time;
        }

        var weekday = $"周{"日一二三四五六"[(int)now.DayOfWeek]}";
        if (style == TopBarClockStyle.Compact)
        {
            return $"{weekday} {time}";
        }

        var date = $"{now.Month}月{now.Day}日 {weekday}";
        if (style == TopBarClockStyle.Full && showLunarDate)
        {
            var lunar = LunarDayText.ShortMonthDay(now);
            if (!string.IsNullOrEmpty(lunar))
            {
                return $"{date} {lunar}  {time}";
            }
        }
        return $"{date}  {time}";
    }
}
