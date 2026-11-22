using System.Text.Json;
using System.Text.Json.Serialization;
using BingLan.Core.TopBar;

namespace BingLan.Core.Models;

/// <summary>Global desktop actions that can be bound to system-wide hotkeys.</summary>
public enum GlobalHotkeyKind
{
    Launcher = 0,
    PeekFileBoxes = 1,
    ToggleWidgets = 2,
    QuickTodo = 3,
    ToggleDesktopIcons = 4
}

/// <summary>
/// Machine-local global hotkey strings. Empty string explicitly disables a hotkey;
/// never exported in theme packs.
/// </summary>
public sealed class GlobalHotkeysState
{
    public const string DefaultLauncherHotkey = TopBarState.DefaultSearchHotkey;
    public const string DefaultPeekFileBoxesHotkey = "Ctrl+Alt+Space";

    public string Launcher { get; set; } = DefaultLauncherHotkey;
    public string PeekFileBoxes { get; set; } = DefaultPeekFileBoxesHotkey;
    public string ToggleWidgets { get; set; } = string.Empty;
    public string QuickTodo { get; set; } = string.Empty;
    public string ToggleDesktopIcons { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public static class GlobalHotkeyRules
{
    public static IReadOnlyList<GlobalHotkeyKind> AllKinds { get; } =
        Enum.GetValues<GlobalHotkeyKind>();

    public static string DisplayName(GlobalHotkeyKind kind) => kind switch
    {
        GlobalHotkeyKind.Launcher => "打开或收起冰蓝总览卡",
        GlobalHotkeyKind.PeekFileBoxes => "临时速览桌面分组盒",
        GlobalHotkeyKind.ToggleWidgets => "隐藏或显示全部桌面组件",
        GlobalHotkeyKind.QuickTodo => "快速新建一条待办",
        GlobalHotkeyKind.ToggleDesktopIcons => "临时切回原生桌面图标",
        _ => "快捷键"
    };

    public static string Get(GlobalHotkeysState state, GlobalHotkeyKind kind)
    {
        ArgumentNullException.ThrowIfNull(state);
        return kind switch
        {
            GlobalHotkeyKind.Launcher => state.Launcher,
            GlobalHotkeyKind.PeekFileBoxes => state.PeekFileBoxes,
            GlobalHotkeyKind.ToggleWidgets => state.ToggleWidgets,
            GlobalHotkeyKind.QuickTodo => state.QuickTodo,
            GlobalHotkeyKind.ToggleDesktopIcons => state.ToggleDesktopIcons,
            _ => string.Empty
        };
    }

    public static void Set(GlobalHotkeysState state, GlobalHotkeyKind kind, string value)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalized = string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : TopBarSearchRules.ParseHotkey(value) is { } binding
                ? TopBarSearchRules.FormatHotkey(binding)
                : string.Empty;
        switch (kind)
        {
            case GlobalHotkeyKind.Launcher:
                state.Launcher = normalized;
                break;
            case GlobalHotkeyKind.PeekFileBoxes:
                state.PeekFileBoxes = normalized;
                break;
            case GlobalHotkeyKind.ToggleWidgets:
                state.ToggleWidgets = normalized;
                break;
            case GlobalHotkeyKind.QuickTodo:
                state.QuickTodo = normalized;
                break;
            case GlobalHotkeyKind.ToggleDesktopIcons:
                state.ToggleDesktopIcons = normalized;
                break;
        }
    }

    public static TopBarHotkeyBinding? ParseOptional(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : TopBarSearchRules.ParseHotkey(text);

    public static GlobalHotkeyKind? FindInternalConflict(
        GlobalHotkeysState state,
        GlobalHotkeyKind target,
        string candidateText)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (ParseOptional(candidateText) is not { } candidate)
        {
            return null;
        }

        foreach (var kind in AllKinds)
        {
            if (kind == target)
            {
                continue;
            }
            if (ParseOptional(Get(state, kind)) is { } existing && existing == candidate)
            {
                return kind;
            }
        }
        return null;
    }

    public static void Normalize(GlobalHotkeysState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Launcher = Coerce(state.Launcher, GlobalHotkeysState.DefaultLauncherHotkey, allowEmpty: true);
        state.PeekFileBoxes = Coerce(state.PeekFileBoxes, GlobalHotkeysState.DefaultPeekFileBoxesHotkey, allowEmpty: true);
        state.ToggleWidgets = Coerce(state.ToggleWidgets, string.Empty, allowEmpty: true);
        state.QuickTodo = Coerce(state.QuickTodo, string.Empty, allowEmpty: true);
        state.ToggleDesktopIcons = Coerce(state.ToggleDesktopIcons, string.Empty, allowEmpty: true);

        // Two internal slots must never register the same combination; earlier slots win.
        var claimed = new HashSet<TopBarHotkeyBinding>();
        foreach (var kind in AllKinds)
        {
            if (ParseOptional(Get(state, kind)) is not { } binding)
            {
                continue;
            }
            if (!claimed.Add(binding))
            {
                Set(state, kind, string.Empty);
            }
        }
    }

    private static string Coerce(string? text, string fallback, bool allowEmpty)
    {
        if (text is null)
        {
            return fallback;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return allowEmpty ? string.Empty : fallback;
        }
        return TopBarSearchRules.ParseHotkey(text) is { } binding
            ? TopBarSearchRules.FormatHotkey(binding)
            : fallback;
    }
}
