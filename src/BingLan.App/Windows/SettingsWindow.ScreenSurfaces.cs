using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BingLan.Core.Dock;
using BingLan.Core.Models;

namespace BingLan.App.Windows;

public partial class SettingsWindow
{
    private bool _screenSurfaceOptionsExpanded;
    private bool _offlineScreenProfilesExpanded;

    private void AddSelectedScreenSurfaceControls(
        StackPanel host,
        MultiScreenSettingsBridge bridge,
        MonitorCardOption screen)
    {
        var status = bridge.GetSurfaceStatus(screen.MonitorKey);
        var statusLine = new TextBlock
        {
            Text = $"Dock：{status.DockStatus}　顶栏：{status.TopBarStatus}",
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        statusLine.SetResourceReference(TextBlock.ForegroundProperty, "SettingsFaintInkBrush");
        host.Children.Add(statusLine);

        var options = new Expander
        {
            Header = "此屏 Dock 与顶栏",
            Margin = new Thickness(0, 8, 0, 0)
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

    private SettingsGroup BuildSelectedScreenSurfaceOptions(MultiScreenSettingsBridge bridge, string monitorKey)
    {
        var preferences = bridge.GetSurfacePreferences(monitorKey) ?? new ScreenSurfacePreferences();
        var group = new SettingsGroup { Margin = new Thickness(0, 8, 0, 0) };
        void Save(Action<ScreenSurfacePreferences> change)
        {
            var current = bridge.GetSurfacePreferences(monitorKey) is { } saved
                ? saved with { }
                : new ScreenSurfacePreferences();
            change(current);
            bridge.ApplySurfacePreferences(monitorKey, current);
            RefreshMultiScreenUi();
        }

        group.Items.Add(new SettingsRow
        {
            Header = "Dock",
            Description = "跟随全局时沿用 Dock 页的设置。",
            Content = BuildScreenPreferenceCombo<bool>(
                [("跟随全局", null), ("开启", true), ("关闭", false)],
                preferences.DockEnabled,
                value => Save(current => current.DockEnabled = value))
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
            Description = "跟随全局时沿用顶端信息条页的设置。",
            Content = BuildScreenPreferenceCombo<bool>(
                [("跟随全局", null), ("开启", true), ("关闭", false)],
                preferences.TopBarEnabled,
                value => Save(current => current.TopBarEnabled = value))
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

        var reset = new Button { Content = "恢复跟随" };
        AutomationProperties.SetName(reset, "此屏 Dock 与顶栏恢复跟随全局");
        reset.Click += (_, _) =>
        {
            bridge.ApplySurfacePreferences(monitorKey, new ScreenSurfacePreferences());
            RefreshMultiScreenUi();
        };
        group.Items.Add(new SettingsRow
        {
            Header = "恢复跟随全局",
            Description = "清除这块显示器的 Dock 和顶栏单独设置。",
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
