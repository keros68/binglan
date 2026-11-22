using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BingLan.Core.Models;
using BingLan.Core.TopBar;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace BingLan.App.Windows;

public partial class SettingsWindow
{
    private GlobalHotkeyKind? _recordingGlobalHotkey;

    private void LoadGlobalHotkeys()
    {
        RefreshGlobalHotkeyRow(GlobalHotkeyKind.LauncherFiles);
        RefreshGlobalHotkeyRow(GlobalHotkeyKind.PeekFileBoxes);
        RefreshGlobalHotkeyRow(GlobalHotkeyKind.ToggleWidgets);
        RefreshGlobalHotkeyRow(GlobalHotkeyKind.QuickTodo);
        RefreshGlobalHotkeyRow(GlobalHotkeyKind.ToggleDesktopIcons);
        LoadTopBarSearchHotkey();
    }

    private (Button? Button, TextBlock? Hint) ResolveGlobalHotkeyControls(GlobalHotkeyKind kind) => kind switch
    {
        GlobalHotkeyKind.LauncherFiles => (LauncherFilesHotkeyButton, LauncherFilesHotkeyHint),
        GlobalHotkeyKind.PeekFileBoxes => (PeekBoxesHotkeyButton, PeekBoxesHotkeyHint),
        GlobalHotkeyKind.ToggleWidgets => (ToggleWidgetsHotkeyButton, ToggleWidgetsHotkeyHint),
        GlobalHotkeyKind.QuickTodo => (QuickTodoHotkeyButton, QuickTodoHotkeyHint),
        GlobalHotkeyKind.ToggleDesktopIcons => (ToggleDesktopIconsHotkeyButton, ToggleDesktopIconsHotkeyHint),
        _ => (TopBarSearchHotkeyButton, TopBarSearchHotkeyHint)
    };

    private void RefreshGlobalHotkeyRow(GlobalHotkeyKind kind)
    {
        var (button, hint) = ResolveGlobalHotkeyControls(kind);
        if (button is null) return;
        if (hint is not null) hint.Text = string.Empty;
        var text = GlobalHotkeyRules.Get(_maintenance.Hotkeys, kind);
        button.Content = string.IsNullOrWhiteSpace(text)
            ? "未设置"
            : TopBarSearchRules.ParseHotkey(text) is { } binding
                ? TopBarSearchRules.FormatHotkey(binding)
                : text;
    }

    private void GlobalHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string raw } button
            || !Enum.TryParse<GlobalHotkeyKind>(raw, out var kind))
        {
            return;
        }
        if (_recordingGlobalHotkey == kind)
        {
            StopGlobalHotkeyRecording(kind);
            return;
        }
        if (_recordingGlobalHotkey is { } previous)
        {
            StopGlobalHotkeyRecording(previous);
        }
        _recordingGlobalHotkey = kind;
        var (_, hint) = ResolveGlobalHotkeyControls(kind);
        if (hint is not null) hint.Text = string.Empty;
        button.Content = "按下组合键…";
        button.Focus();
    }

    private void GlobalHotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recordingGlobalHotkey is not { } kind)
        {
            return;
        }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            StopGlobalHotkeyRecording(kind);
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            return;
        }

        var (_, hint) = ResolveGlobalHotkeyControls(kind);
        if (HotkeyToken(key) is not { } token)
        {
            if (hint is not null) hint.Text = "这个键不能作为热键主键";
            return;
        }
        var parts = CollectModifierParts();
        if (parts.Count == 0)
        {
            if (hint is not null) hint.Text = "需要至少一个修饰键（Ctrl/Alt/Shift/Win）";
            return;
        }
        parts.Add(token);
        var text = string.Join("+", parts);
        if (TopBarSearchRules.ParseHotkey(text) is null)
        {
            if (hint is not null) hint.Text = "组合无效";
            return;
        }
        if (GlobalHotkeyRules.FindInternalConflict(_maintenance.Hotkeys, kind, text) is { } conflict)
        {
            if (hint is not null)
            {
                hint.Text = GlobalHotkeyRules.FormatConflictHint(
                    GlobalHotkeyProbeResult.OwnedByBingLan(conflict));
            }
            return;
        }
        var probe = _maintenance.ProbeGlobalHotkey(kind, text);
        if (!probe.IsAvailable)
        {
            if (hint is not null) hint.Text = GlobalHotkeyRules.FormatConflictHint(probe);
            return;
        }

        GlobalHotkeyRules.Set(_maintenance.Hotkeys, kind, text);
        _recordingGlobalHotkey = null;
        RefreshGlobalHotkeyRow(kind);
        _maintenance.ApplyGlobalHotkeys();
    }

    private void GlobalHotkey_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_recordingGlobalHotkey is { } kind)
        {
            StopGlobalHotkeyRecording(kind);
        }
    }

    private void GlobalHotkeyClear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string raw }
            || !Enum.TryParse<GlobalHotkeyKind>(raw, out var kind))
        {
            return;
        }
        if (_recordingGlobalHotkey == kind)
        {
            _recordingGlobalHotkey = null;
        }
        GlobalHotkeyRules.Set(_maintenance.Hotkeys, kind, string.Empty);
        RefreshGlobalHotkeyRow(kind);
        _maintenance.ApplyGlobalHotkeys();
    }

    private void StopGlobalHotkeyRecording(GlobalHotkeyKind kind)
    {
        _recordingGlobalHotkey = null;
        RefreshGlobalHotkeyRow(kind);
    }

    private void OpenLauncherHotkeys_Click(object sender, RoutedEventArgs e) =>
        SelectNavigationPage(SettingsNavigationRules.LauncherPageKey);

    private static List<string> CollectModifierParts()
    {
        var parts = new List<string>(5);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return parts;
    }
}
