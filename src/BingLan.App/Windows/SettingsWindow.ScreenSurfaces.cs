using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BingLan.Core.Dock;
using BingLan.Core.Models;

namespace BingLan.App.Windows;

public partial class SettingsWindow
{
    private static readonly (string Label, ScreenSurfaceEnableMode? Value)[] ScreenEnableChoices =
    [
        ("按全局目标显示", ScreenSurfaceEnableMode.GlobalTarget),
        ("此屏跟随全局", ScreenSurfaceEnableMode.FollowGlobal),
        ("开启", ScreenSurfaceEnableMode.Enabled),
        ("关闭", ScreenSurfaceEnableMode.Disabled)
    ];

    private bool _screenSurfaceOptionsExpanded;
    private bool _offlineScreenProfilesExpanded;

    private void AddSelectedScreenSurfaceControls(
        StackPanel host,
        MultiScreenSettingsBridge bridge,
        MonitorCardOption screen)
    {
        var status = bridge.GetSurfaceStatus(screen.MonitorKey);
        var switches = new SettingsGroup { Margin = new Thickness(0, 16, 0, 0) };
        switches.Items.Add(BuildScreenSurfaceRow("冰蓝 Dock", status.DockEnabled, status.DockStatus,
            mode => ChangeScreenSurfacePreferences(bridge, screen.MonitorKey, current => current.DockEnableMode = mode)));
        switches.Items.Add(BuildScreenSurfaceRow("顶部信息条", status.TopBarEnabled, status.TopBarStatus,
            mode => ChangeScreenSurfacePreferences(bridge, screen.MonitorKey, current => current.TopBarEnableMode = mode)));
        host.Children.Add(switches);

        var options = new Expander
        {
            Header = "位置、显示方式与高度",
            Margin = new Thickness(0, 10, 0, 0)
        };
        options.Expanded += (_, _) =>
        {
            _screenSurfaceOptionsExpanded = true;
            options.Content ??= BuildSelectedScreenSurfaceOptions(bridge, screen.MonitorKey);
        };
        options.Collapsed += (_, _) => _screenSurfaceOptionsExpanded = false;
        options.IsExpanded = _screenSurfaceOptionsExpanded;
        host.Children.Add(options);
    }

    private SettingsRow BuildScreenSurfaceRow(string label, bool enabled, string status, Action<ScreenSurfaceEnableMode> apply)
    {
        var control = new CheckBox { IsChecked = enabled };
        if (TryFindResource("ToggleSwitchStyle") is Style style)
        {
            control.Style = style;
        }
        AutomationProperties.SetName(control, $"此屏的{label}");
        AutomationProperties.SetItemStatus(control, status);
        control.Checked += (_, _) => apply(ScreenSurfaceEnableMode.Enabled);
        control.Unchecked += (_, _) => apply(ScreenSurfaceEnableMode.Disabled);
        return new SettingsRow { Header = label, Description = status, Content = control };
    }

    private void ChangeScreenSurfacePreferences(MultiScreenSettingsBridge bridge, string monitorKey,
        Action<ScreenSurfacePreferences> change)
    {
        var current = bridge.GetSurfacePreferences(monitorKey) is { } saved ? saved with { } : new ScreenSurfacePreferences();
        change(current);
        bridge.ApplySurfacePreferences(monitorKey, current);
        RefreshMultiScreenUi();
    }

    private SettingsGroup BuildSelectedScreenSurfaceOptions(MultiScreenSettingsBridge bridge, string monitorKey)
    {
        var preferences = bridge.GetSurfacePreferences(monitorKey) ?? new ScreenSurfacePreferences();
        var group = new SettingsGroup { Margin = new Thickness(0, 8, 0, 0) };
        void Save(Action<ScreenSurfacePreferences> change) => ChangeScreenSurfacePreferences(bridge, monitorKey, change);

        group.Items.Add(new SettingsRow
        {
            Header = "Dock",
            Description = "此屏跟随全局时，随桌面方案一起开关。",
            Content = BuildScreenPreferenceCombo(ScreenEnableChoices, preferences.DockEnableMode,
                value => Save(current => current.DockEnableMode = value ?? ScreenSurfaceEnableMode.GlobalTarget))
        });
        group.Items.Add(new SettingsRow
        {
            Header = "Dock 位置",
            Content = BuildScreenPreferenceCombo<DockEdge>(
                [("跟随全局", null), ("底部", DockEdge.Bottom), ("左侧", DockEdge.Left), ("右侧", DockEdge.Right)],
                preferences.DockEdge,
                value => Save(current => current.DockEdge = value))
        });
        group.Items.Add(new SettingsRow
        {
            Header = "Dock 显示方式",
            Content = BuildScreenPreferenceCombo<DockVisibilityMode>(
                [("跟随全局", null), ("始终显示", DockVisibilityMode.ReserveWorkArea), ("智能隐藏", DockVisibilityMode.SmartHide)],
                preferences.DockVisibilityMode,
                value => Save(current => current.DockVisibilityMode = value))
        });
        group.Items.Add(new SettingsRow
        {
            Header = "顶端信息条",
            Description = "此屏跟随全局时，随桌面方案一起开关。",
            Content = BuildScreenPreferenceCombo(ScreenEnableChoices, preferences.TopBarEnableMode,
                value => Save(current => current.TopBarEnableMode = value ?? ScreenSurfaceEnableMode.GlobalTarget))
        });
        group.Items.Add(new SettingsRow
        {
            Header = "顶栏显示方式",
            Content = BuildScreenPreferenceCombo<TopBarVisibilityMode>(
                [("跟随全局", null), ("始终显示", TopBarVisibilityMode.ReserveTopEdge), ("智能隐藏", TopBarVisibilityMode.SmartHide)],
                preferences.TopBarVisibilityMode,
                value => Save(current => current.TopBarVisibilityMode = value))
        });

        var heights = new List<(string Label, double? Value)> { ("跟随全局", null) };
        for (var height = TopBarState.MinimumHeightDip; height <= TopBarState.MaximumHeightDip; height += 4)
        {
            heights.Add(($"{height:0} DIP", height));
        }
        if (preferences.TopBarHeight is { } customHeight && !heights.Any(choice => choice.Value == customHeight))
        {
            heights.Add(($"{customHeight:0.#} DIP", customHeight));
        }
        group.Items.Add(new SettingsRow
        {
            Header = "顶栏高度",
            Content = BuildScreenPreferenceCombo<double>(heights, preferences.TopBarHeight,
                value => Save(current => current.TopBarHeight = value))
        });

        var reset = new Button { Content = "恢复默认" };
        AutomationProperties.SetName(reset, "恢复此屏 Dock 与顶栏的默认设置");
        reset.Click += (_, _) =>
        {
            bridge.ApplySurfacePreferences(monitorKey, new ScreenSurfacePreferences());
            RefreshMultiScreenUi();
        };
        group.Items.Add(new SettingsRow
        {
            Header = "恢复默认设置",
            Description = "清除此屏的栏设置，恢复按全局目标显示。",
            Content = reset
        });
        return group;
    }

    private static ComboBox BuildScreenPreferenceCombo<T>(
        IReadOnlyList<(string Label, T? Value)> choices,
        T? selected,
        Action<T?> apply) where T : struct
    {
        var combo = new ComboBox { MinWidth = 136 };
        foreach (var choice in choices)
        {
            var item = new ComboBoxItem { Content = choice.Label, Tag = choice.Value };
            combo.Items.Add(item);
            if (EqualityComparer<T?>.Default.Equals(choice.Value, selected))
            {
                combo.SelectedItem = item;
            }
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem item)
            {
                apply(item.Tag is T value ? value : null);
            }
        };
        return combo;
    }

    private void AddOfflineScreenProfileControls(
        StackPanel host,
        MultiScreenSettingsBridge bridge,
        MonitorCardOption screen)
    {
        var offline = new Expander
        {
            Header = bridge.CanBindLegacyRoot() ? "未连接的桌面档案（旧档案待关联）" : "未连接的桌面档案",
            Margin = new Thickness(0, 8, 0, 0)
        };
        offline.Expanded += (_, _) =>
        {
            _offlineScreenProfilesExpanded = true;
            offline.Content ??= BuildOfflineScreenProfileList(bridge, screen);
        };
        offline.Collapsed += (_, _) => _offlineScreenProfilesExpanded = false;
        offline.IsExpanded = _offlineScreenProfilesExpanded;
        host.Children.Add(offline);
    }

    private StackPanel BuildOfflineScreenProfileList(MultiScreenSettingsBridge bridge, MonitorCardOption screen)
    {
        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        list.Children.Add(new TextBlock
        {
            Text = "档案保留在原显示器，重新连接后恢复。可关联到当前空白屏幕。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        var group = new SettingsGroup();
        var profiles = bridge.GetOfflineProfiles(screen.MonitorKey);
        foreach (var profile in profiles)
        {
            var bind = new Button { Content = "关联此屏", IsEnabled = profile.CanBind };
            AutomationProperties.SetName(bind, $"将 {profile.DisplayLabel} 关联到 {screen.ShortLabel}");
            bind.Click += (_, _) =>
            {
                FlushPendingComponentEditsBeforeScreenSwitch();
                var succeeded = bridge.BindOfflineProfile(profile.ScreenKey, screen.MonitorKey);
                SetScreenProfileStatus(succeeded ? "桌面档案已关联到此屏。" : "此屏已有桌面内容，或档案已连接。请选择空白屏幕关联。");
                if (succeeded)
                {
                    _activeScreenKey = bridge.GetScreens()
                        .FirstOrDefault(option => string.Equals(option.MonitorKey, screen.MonitorKey, StringComparison.Ordinal))
                        ?.ScreenKey;
                }
                RefreshMultiScreenUi(refreshComponentList: true);
            };
            group.Items.Add(new SettingsRow
            {
                Header = profile.DisplayLabel,
                Description = $"已保存 {profile.CardCount} 张卡片。" + (profile.CanBind ? string.Empty : "此屏已有桌面内容。"),
                Content = bind
            });
        }
        if (screen.ScreenKey is { } legacyTarget && bridge.CanBindLegacyRoot())
        {
            var bindLegacy = new Button { Content = "关联此屏", IsEnabled = screen.CardCount == 0 };
            AutomationProperties.SetName(bindLegacy, "将旧桌面档案关联到此屏");
            bindLegacy.Click += (_, _) =>
            {
                FlushPendingComponentEditsBeforeScreenSwitch();
                if (bridge.BindLegacyRoot(legacyTarget))
                {
                    SetScreenProfileStatus("旧桌面档案已关联到此屏。");
                    SelectActiveScreen(null);
                }
                else
                {
                    SetScreenProfileStatus("此屏已有桌面内容。请选择空白屏幕关联。");
                }
            };
            group.Items.Add(new SettingsRow { Header = "旧桌面档案", Content = bindLegacy });
        }
        if (group.Items.Count > 0)
        {
            list.Children.Add(group);
        }
        else
        {
            list.Children.Add(new TextBlock { Text = "没有未连接的桌面档案。" });
        }
        return list;
    }

    private void SetScreenProfileStatus(string message)
    {
        MonitorProfileStatusText.Text = message;
        MonitorProfileStatusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        if (ComponentSettingsStatusText is not null)
        {
            ComponentSettingsStatusText.Text = message;
        }
        if (LayoutStatusText is not null)
        {
            LayoutStatusText.Text = message;
        }
    }
}
