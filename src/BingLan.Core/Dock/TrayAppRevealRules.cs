using BingLan.Core.TopBar;

namespace BingLan.Core.Dock;

/// <summary>An app's own global "show window" hotkey, in RegisterHotKey terms.</summary>
public sealed record TrayRevealHotkey(uint Modifiers, int VirtualKey);

/// <summary>
/// Apps that close to the tray keep only hidden windows, which must not be shown from
/// outside. Some of them register a global hotkey that raises their main window through
/// the app's own path; the dock sends it instead of launching the program again.
/// </summary>
public static class TrayAppRevealRules
{
    /// <summary>How long the dock waits for the window before launching the program.</summary>
    public static readonly TimeSpan RevealTimeout = TimeSpan.FromMilliseconds(1500);

    // WeChat 3.x (WeChat.exe) and 4.x (Weixin.exe) default to Ctrl+Alt+W.
    private static readonly TrayRevealHotkey WeChat = new(
        TopBarSearchRules.ModifierControl | TopBarSearchRules.ModifierAlt,
        'W');

    public static TrayRevealHotkey? HotkeyFor(string? executablePath) =>
        Path.GetFileName(executablePath) is { } name
        && (name.Equals("Weixin.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("WeChat.exe", StringComparison.OrdinalIgnoreCase))
            ? WeChat
            : null;
}
