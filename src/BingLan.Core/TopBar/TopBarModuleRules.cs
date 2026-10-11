using System.Globalization;
using BingLan.Core.Models;

namespace BingLan.Core.TopBar;

public enum TopBarBatteryStatus
{
    OnBattery,
    PluggedIn,
    Charging,
    Unknown
}

/// <summary>Read-only value rules for the top bar's system status modules.</summary>
public static class TopBarModuleRules
{
    private const int BatteryChargingFlag = 8;

    /// <summary>How many to-do items with text are still open across all to-do cards.</summary>
    public static int CountIncompleteTodos(IEnumerable<TodoWidgetState> widgets) =>
        widgets.Sum(widget => widget.Items.Count(item =>
            !item.IsCompleted && !string.IsNullOrWhiteSpace(item.Text)));

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
