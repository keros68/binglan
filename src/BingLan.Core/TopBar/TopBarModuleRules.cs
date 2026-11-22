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
            TodoService.BelongsToTodayList(item, target)));
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
    public static string FormatNetworkLabel(TopBarNetworkStatus status, bool showWifiName)
    {
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
}
