using System.Windows;
using System.Windows.Controls;
using BingLan.App.Windows;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Services;

/// <summary>Exercises the real settings controls without showing a window or injecting input.</summary>
internal static class ScreenSurfaceSettingsTests
{
    internal static void Run()
    {
        const string rootMonitor = "DISPLAY#SETTINGS_ROOT#1";
        const string otherMonitor = "DISPLAY#SETTINGS_OTHER#2";
        const string offlineMonitor = "DISPLAY#SETTINGS_OFFLINE#3";
        var preferences = new Dictionary<string, ScreenSurfacePreferences>();
        var writes = new List<string>();
        var archiveQueries = 0;
        var associations = new List<(string Source, string Destination)>();
        var copiedTo = new List<string>();
        var canBind = true;
        var bridge = new MultiScreenSettingsBridge
        {
            IsEnabled = () => true,
            HasMultipleMonitors = () => true,
            GetConnectedMonitorCount = () => 2,
            GetScreens = () =>
            [
                new(null, rootMonitor, "原屏", "原屏", "1920×1080", 1, true, true,
                    new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 96, 0),
                new(otherMonitor, otherMonitor, "第二屏", "第二屏", "2560×1440", 2, false, true,
                    new(1920, 0, 4480, 1440), new(1920, 0, 4480, 1400), 120, canBind ? 0 : 1)
            ],
            GetSurfacePreferences = key => preferences.GetValueOrDefault(key),
            ApplySurfacePreferences = (key, value) =>
            {
                preferences[key] = value;
                writes.Add(key);
            },
            GetSurfaceStatus = key => new("已显示", "未启用", DockEnabled: key == rootMonitor),
            CopyPrimaryProfileToScreen = key =>
            {
                copiedTo.Add(key);
                return "已复制主屏设置。";
            },
            GetOfflineProfiles = _ =>
            {
                archiveQueries++;
                return [new(offlineMonitor, "离线屏幕", 2, canBind)];
            },
            BindOfflineProfile = (source, destination) =>
            {
                associations.Add((source, destination));
                return canBind;
            }
        };
        var settings = new SettingsWindow(new CityLookupService(), new InformationWidgetState(),
            DesktopExperienceRules.CreateDefault(), (_, _) => { }, _ => { }, _ => { },
            maintenance: new SettingsMaintenance { MultiScreen = bridge });
        try
        {
            Check(!settings.IsVisible, "范围测试不得显示设置窗口");
            Check(archiveQueries == 0, "展开离线区域前不得查询或创建档案条目");
            settings.ShowSettingsPage("Displays");
            Check(DisplaySections(settings).Children.OfType<SettingsGroup>().Single().Items.OfType<SettingsRow>()
                .Select(row => row.Content).OfType<CheckBox>()
                .Select(toggle => toggle.IsChecked).SequenceEqual(new bool?[] { true, false }),
                "显示器页应显示两条栏的独立开关");

            Choose(settings, "Dock", "开启");
            Check(writes.Last() == rootMonitor && preferences[rootMonitor].DockEnabled == true,
                "根布局的空 ScreenKey 必须转换为选中屏的物理 MonitorKey");
            settings.ShowSettingsPage("Components");
            settings.SelectActiveScreen(otherMonitor);
            var compactSelector = Topology(settings);
            Check(compactSelector.Children.Count == 1 && compactSelector.Children[0] is Grid,
                "桌面卡片页应只显示选屏、复制与前往显示器的入口");
            var actions = compactSelector.Children.OfType<Grid>().Single().Children.OfType<Button>().ToArray();
            var copy = actions
                .Single(button => Equals(button.Content, "复制主屏设置"));
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(copiedTo.SequenceEqual([otherMonitor]), "复制应传入选定目标的物理屏身份");
            Topology(settings).Children.OfType<Grid>().Single().Children.OfType<Button>()
                .Single(button => Equals(button.Content, "显示器设置")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(settings.FindName("DisplaysPage") is ScrollViewer { Visibility: Visibility.Visible }
                && settings.ActiveScreenKey == otherMonitor, "前往显示器设置应保留当前选中的副屏");
            var dockSwitch = (CheckBox)DisplaySections(settings).Children.OfType<SettingsGroup>().Single().Items.OfType<SettingsRow>()
                .Single(row => row.Header == "冰蓝 Dock").Content;
            dockSwitch.IsChecked = true;
            Check(preferences[otherMonitor].DockEnableMode == ScreenSurfaceEnableMode.Enabled,
                "快速开关只为当前屏写入显式状态");
            Choose(settings, "Dock", "关闭");
            Choose(settings, "Dock 位置", "左侧");
            Choose(settings, "Dock 显示方式", "智能隐藏");
            Choose(settings, "顶端信息条", "开启");
            Choose(settings, "顶栏显示方式", "智能隐藏");
            Choose(settings, "顶栏高度", "40 DIP");
            var saved = preferences[otherMonitor];
            Check(saved.DockEnabled == false && saved.DockEdge == DockEdge.Left
                && saved.DockVisibilityMode == DockVisibilityMode.SmartHide && saved.TopBarEnabled == true
                && saved.TopBarVisibilityMode == TopBarVisibilityMode.SmartHide && saved.TopBarHeight == 40,
                "各个本屏控件应只改变对应字段并保留之前的单独设置");
            Check(preferences[rootMonitor].DockEnabled == true && archiveQueries == 0,
                "修改第二屏不得改动根屏设置或展开离线档案");
            Choose(settings, "Dock", "此屏跟随全局");
            Check(preferences[otherMonitor].DockEnableMode == ScreenSurfaceEnableMode.FollowGlobal
                && preferences[otherMonitor].TopBarEnabled == true,
                "此屏跟随全局应记录逐屏继承意图并保留其他栏选择");
            Choose(settings, "顶端信息条", "此屏跟随全局");
            Check(preferences[otherMonitor].TopBarEnableMode == ScreenSurfaceEnableMode.FollowGlobal,
                "顶栏也可独立跟随全局开关");
            Choose(settings, "Dock", "按全局目标显示");
            Check(preferences[otherMonitor].DockEnableMode == ScreenSurfaceEnableMode.GlobalTarget,
                "按全局目标显示应移除此屏跟随开关的行为");
            var reset = (Button)SurfaceRows(settings).Single(row => row.Header == "恢复默认设置").Content;
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(preferences[otherMonitor].IsEmpty, "恢复跟随应清除本屏全部表面设置");

            var offline = OfflineSection(settings);
            Check(offline.Content is null && archiveQueries == 0, "未展开的离线区域应保持惰性创建");
            offline.IsExpanded = true;
            var list = offline.Content as StackPanel ?? throw new InvalidOperationException("离线面板未创建");
            var row = list.Children.OfType<SettingsGroup>().Single().Items.OfType<SettingsRow>().Single();
            var associate = (Button)row.Content;
            Check(archiveQueries == 1 && associate.IsEnabled, "展开后才查询档案，并允许关联空白屏");
            associate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(associations.SequenceEqual([(offlineMonitor, otherMonitor)]) && settings.ActiveScreenKey == otherMonitor,
                "明确点击后应将该档案关联到当前物理屏，并保持屏幕选择");

            canBind = false;
            settings.RefreshMultiScreenUi();
            offline = OfflineSection(settings);
            row = ((StackPanel)offline.Content).Children.OfType<SettingsGroup>().Single().Items.OfType<SettingsRow>().Single();
            Check(!((Button)row.Content).IsEnabled && associations.Count == 1,
                "已有内容的屏幕应禁用关联，刷新不得自动迁移或合并档案");
        }
        finally
        {
            settings.Close();
        }
    }

    private static StackPanel Topology(SettingsWindow settings) =>
        (StackPanel)((Border)((Border)settings.FindName("ComponentsMultiScreenBarHost")).Child).Child;

    private static StackPanel DisplaySections(SettingsWindow settings) =>
        (StackPanel)((Border)settings.FindName("DisplaysMultiScreenHost")).Child;

    private static Expander OfflineSection(SettingsWindow settings) => DisplaySections(settings).Children.OfType<Border>()
        .Select(card => card.Child).OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<Expander>())
        .Single(item => Equals(item.Header, "未连接的桌面档案"));

    private static IEnumerable<SettingsRow> SurfaceRows(SettingsWindow settings)
    {
        var options = DisplaySections(settings).Children.OfType<Expander>()
            .Single(item => Equals(item.Header, "位置、显示方式与高度"));
        options.IsExpanded = true;
        return ((SettingsGroup)options.Content).Items.OfType<SettingsRow>();
    }

    private static void Choose(SettingsWindow settings, string rowHeader, string label)
    {
        var combo = (ComboBox)SurfaceRows(settings).Single(row => row.Header == rowHeader).Content;
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Content, label));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
