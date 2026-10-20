using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using BingLan.App.Windows;
using BingLan.Core.Models;
using BingLan.Core.Services;
using static BingLan.InformationTests.TestSupport;

namespace BingLan.InformationTests;

internal static class SettingsWindowTests
{
    private const string SearchJson = """
        [
          {
            "place_id": 219704754,
            "lat": "39.9057",
            "lon": "116.3913",
            "name": "北京市",
            "display_name": "北京市, 中国",
            "address": { "city": "北京市", "country": "中国" }
          }
        ]
        """;

    public static void SearchSelectAndSave()
    {
        var stub = new StubHttpMessageHandler
        {
            Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SearchJson)
            })
        };
        var state = new InformationWidgetState
        {
            GreetingName = "旧称呼",
            Use24HourClock = true
        };
        string? savedGreeting = null;
        bool? savedClock = null;
        CitySearchResult? savedCity = null;
        var window = new SettingsWindow(
            new CityLookupService(new CitySearchService(stub), CityLibrary.Empty),
            state,
            (greeting, use24Hour) =>
            {
                savedGreeting = greeting;
                savedClock = use24Hour;
            },
            city => savedCity = city);
        ShowAndPump(window);
        try
        {
            var currentCity = Require<TextBlock>(window, "CurrentCityText");
            Assert(currentCity.Text.Contains("未设置"), "默认城市状态可见");

            Require<TextBox>(window, "CityQueryEditor").Text = "北京";
            Click(Require<Button>(window, "SearchCityButton"));
            var results = Require<ListBox>(window, "CityResultsList");
            PumpUntil(
                () => results.Items.Count == 1,
                TimeSpan.FromSeconds(5),
                "城市候选写入设置窗口");

            results.SelectedIndex = 0;
            Pump();
            var apply = Require<Button>(window, "ApplyCityButton");
            Assert(apply.IsEnabled, "选择候选后允许应用");
            Click(apply);
            Assert(savedCity is not null, "选择结果传给宿主");
            AssertEqual("北京市 · 中国", savedCity!.DisplayName, "选择城市显示名称");
            Assert(currentCity.Text.Contains("北京"), "当前城市立即更新");

            Require<TextBox>(window, "GreetingNameEditor").Text = "  小明  ";
            Require<CheckBox>(window, "Use24HourClockCheckBox").IsChecked = false;
            AssertEqual(false, savedClock, "切换 24 小时制应立即保存");
            window.CommitPendingChanges();
            AssertEqual("小明", savedGreeting, "称呼去除首尾空白后保存");
            AssertEqual(false, savedClock, "12 小时制设置保存");
            AssertEqual("已保存", Require<TextBlock>(window, "DisplaySettingsStatusText").Text,
                "显示设置保存反馈");
        }
        finally
        {
            window.Close();
            Pump();
        }
    }

    public static void ApplyPresetAndComponents()
    {
        var experience = DesktopExperienceRules.CreateDefault();
        DesktopExperienceState? componentApplied = null;
        DesktopExperienceState? presetApplied = null;
        bool? todoLocked = null;
        var todoWindow = new TodoWidgetWindow(new TodoWidgetState { Title = "今日待办" });
        ShowAndPump(todoWindow);
        var window = new SettingsWindow(
            new CityLookupService(new CitySearchService(new StubHttpMessageHandler()), CityLibrary.Empty),
            new InformationWidgetState(),
            experience,
            (_, _) => { },
            _ => { },
            state => componentApplied = state,
            state => presetApplied = state,
            () => [todoWindow],
            (target, locked) =>
            {
                target.ApplyWidgetLock(locked);
                todoLocked = locked;
            });
        ShowAndPump(window);
        try
        {
            var previewBefore = Require<Image>(window, "PresetPreviewImage").Source;
            Require<RadioButton>(window, "GlacierWorkbenchPresetRadio").IsChecked = true;
            Click(Require<Button>(window, "ApplyLayoutButton"));

            Assert(presetApplied is not null, "应用布局应通知预设宿主");
            AssertEqual(
                DesktopLayoutPreset.GlacierWorkbench,
                presetApplied!.ActivePreset,
                "冰川工作台预设传给宿主");
            var previewAfter = Require<Image>(window, "PresetPreviewImage").Source;
            Assert(previewBefore is not null && previewAfter is not null && !ReferenceEquals(previewBefore, previewAfter),
                "预设选择应重新绘制预览");

            Require<Slider>(window, "SelectedComponentFontScaleSlider").Value = 1.5;
            Require<TextBox>(window, "SelectedComponentTextColorEditor").Text = "#000000";
            Require<TextBox>(window, "SelectedComponentBackgroundColorEditor").Text =
                "#A0B4E1";
            Require<Slider>(window, "SelectedComponentOpacitySlider").Value = 0.5;
            Require<Slider>(window, "SelectedComponentCornerRadiusSlider").Value = 22;
            Require<TextBox>(window, "SelectedComponentWidthEditor").Text = "420";
            Require<TextBox>(window, "SelectedComponentHeightEditor").Text = "240";
            Assert(componentApplied is null, "输入停顿前不应逐字应用");
            window.CommitPendingChanges();

            Assert(componentApplied is not null, "组件设置应自动通知组件宿主");
            var timeDate = componentApplied!.GetComponent(DesktopComponentKind.TimeDate);
            AssertEqual(1.5, timeDate.FontScale, "组件字号缩放传给宿主");
            AssertEqual("#000000", timeDate.Appearance.TextColor, "组件文字颜色传给宿主");
            AssertEqual(0.5, timeDate.Appearance.BackgroundOpacity,
                "组件玻璃不透明度传给宿主");
            AssertEqual("#A0B4E1", timeDate.Appearance.BackgroundColor,
                "组件玻璃颜色传给宿主");
            AssertEqual(22d, timeDate.CornerRadius, "组件圆角传给宿主");
            AssertEqual(420d, timeDate.Placement.Width, "组件宽度传给宿主");
            AssertEqual(240d, timeDate.Placement.Height, "组件高度传给宿主");

            window.ShowWidgetSettings(todoWindow);
            Pump();
            var componentList = Require<ListBox>(window, "DesktopComponentList");
            Assert(
                componentList.SelectedItem is SettingsWindow.DesktopComponentSettingsEntry
                {
                    Kind: DesktopComponentKind.Todo
                },
                "从待办卡片进入设置应在组件列表选中对应卡片");
            AssertEqual(
                "今日待办",
                Require<TextBlock>(window, "SelectedComponentHeadingText").Text,
                "统一设置没有显示当前卡片名称");
            Require<CheckBox>(window, "SelectedComponentLockedCheckBox").IsChecked = true;
            AssertEqual(true, todoLocked, "勾选锁定应立即通知宿主");
        }
        finally
        {
            window.Close();
            todoWindow.CanClose = true;
            todoWindow.Close();
            Pump();
        }
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    public static void TopBarSettingsPage()
    {
        var topBar = new TopBarState();
        var applies = 0;
        var window = new SettingsWindow(
            new CityLookupService(new CitySearchService(new StubHttpMessageHandler()), CityLibrary.Empty),
            new InformationWidgetState(),
            DesktopExperienceRules.CreateDefault(),
            (_, _) => { },
            _ => { },
            _ => { },
            maintenance: null,
            topBarState: topBar,
            applyTopBar: () => applies++);
        ShowAndPump(window);
        try
        {
            var navigation = Require<ListBox>(window, "SettingsNavigationList");
            var tags = navigation.Items.OfType<ListBoxItem>()
                .Select(item => item.Tag as string)
                .ToList();
            Assert(tags.Contains("TopBar"), "导航应包含顶端信息条页");
            Assert(tags.IndexOf("Dock") < tags.IndexOf("TopBar")
                && tags.IndexOf("TopBar") < tags.IndexOf("Taskbar"),
                "顶端信息条应排在 Dock 与任务栏之间");

            navigation.SelectedItem = navigation.Items
                .OfType<ListBoxItem>()
                .First(item => (item.Tag as string) == "TopBar");
            Pump();
            Assert(Require<FrameworkElement>(window, "TopBarPage").IsVisible, "选择导航后显示顶端信息条页");

            var enabled = Require<CheckBox>(window, "TopBarEnabledCheckBox");
            Assert(enabled.IsChecked == false, "顶端信息条默认关闭");
            enabled.IsChecked = true;
            Pump();
            Assert(topBar.IsEnabled, "开关应写入顶栏状态");
            Assert(applies == 1, "开关后应通知宿主应用");

            var volume = Require<CheckBox>(window, "TopBarVolumeCheckBox");
            volume.IsChecked = false;
            Pump();
            Assert(!topBar.Modules.Volume, "模块开关应写入状态");
            var tray = Require<CheckBox>(window, "TopBarTrayOverflowCheckBox");
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                Assert(tray.IsChecked == true && tray.IsVisible, "系统托盘入口默认开启且可设置");
                tray.IsChecked = false;
                Pump();
                Assert(!topBar.Modules.TrayOverflow, "托盘入口开关应写入状态");
            }

            var smartHide = Require<RadioButton>(window, "TopBarSmartHideRadio");
            smartHide.IsChecked = true;
            Pump();
            Assert(topBar.VisibilityMode == TopBarVisibilityMode.SmartHide, "显示方式应写入状态");

            var height = Require<Slider>(window, "TopBarHeightSlider");
            height.Value = 40;
            Pump();
            Assert(Math.Abs(topBar.Height - 40d) < 0.001d, "高度应写入状态");

            var frosted = Require<CheckBox>(window, "TopBarFrostedCheckBox");
            frosted.IsChecked = false;
            Pump();
            Assert(!topBar.FrostedGlass, "磨砂开关应写入状态");

            Require<RadioButton>(window, "TopBarCustomLookRadio").IsChecked = true;
            Pump();
            Assert(!topBar.FollowCardLook, "自定义外观应写入状态");
            var everything = Require<CheckBox>(window, "TopBarEverythingCheckBox");
            Assert(everything.IsChecked == false, "Everything 搜索默认关闭");
            everything.IsChecked = true;
            Pump();
            Assert(topBar.UseEverything, "Everything 选项应写入本机设置");
            Require<ComboBox>(window, "SearchPresentationComboBox").SelectedIndex = 1;
            Pump();
            Assert(topBar.SearchPresentation == SearchPresentation.Floating, "独立搜索入口应写入状态");
            var look = Require<ComboBox>(window, "SearchLookComboBox");
            look.SelectedIndex = 1;
            Require<Slider>(window, "SearchOpacitySlider").Value = 0.7;
            Require<Slider>(window, "SearchCornerSlider").Value = 16;
            Pump();
            Assert(topBar.SearchSurfaceColor == "#F3F6FC" && topBar.SearchSurfaceOpacity == 0.7 && topBar.SearchCornerRadius == 16,
                "搜索框外观选项应写入实际独立设置");
            look.SelectedIndex = 2;
            Pump();
            Assert(topBar.SearchFollowCardLook && !Require<Slider>(window, "SearchOpacitySlider").IsEnabled,
                "跟随卡片时应使用共享材质，并停用自定义透明度");
            var moduleGrid = Require<System.Windows.Controls.Primitives.UniformGrid>(window, "TopBarModulesPanel");
            foreach (var width in new[] { 900d, 1168d, 1600d })
            {
                window.Width = width;
                Pump();
                var cells = moduleGrid.Children.OfType<CheckBox>().Where(cell => cell.Visibility != Visibility.Collapsed).ToArray();
                var count = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? 11 : 10;
                Assert(cells.Length == count && moduleGrid.Columns is >= 2 and <= 5 && cells.All(cell => cell.ActualWidth >= 110),
                    "窄窗与宽窗中模块应按等宽列排布，保留文字空间");
                Assert(moduleGrid.Parent is Border { Padding.Left: >= 16 }, "模块区应与分组边框保持内边距");
                var first = cells[0].TranslatePoint(new Point(0, 0), moduleGrid);
                var nextRow = cells[moduleGrid.Columns].TranslatePoint(new Point(0, 0), moduleGrid);
                Assert(Math.Abs(first.X - nextRow.X) < 1 && nextRow.Y > first.Y,
                    "模块换行后的列起点必须对齐");
            }
        }
        finally
        {
            window.Close();
            Pump();
        }
    }

    public static void UpdatePageShowsFoundRelease()
    {
        var installer = System.Text.Encoding.UTF8.GetBytes("setup");
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(installer)).ToLowerInvariant();
        var releaseJson = $$"""
            {
              "tag_name": "v9.9.9",
              "html_url": "https://github.com/keros68/binglan/releases/tag/v9.9.9",
              "body": "新功能说明",
              "assets": [
                {
                  "name": "BingLan-Setup-9.9.9.exe",
                  "size": {{installer.Length}},
                  "digest": "sha256:{{digest}}",
                  "browser_download_url": "https://github.com/keros68/binglan/releases/download/v9.9.9/BingLan-Setup-9.9.9.exe"
                }
              ]
            }
            """;
        var stub = new StubHttpMessageHandler
        {
            Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(releaseJson)
            })
        };
        var updateState = new UpdateState();
        var saves = 0;
        var updater = new BingLan.App.Services.AppUpdater(
            updateState, new Version(0, 2, 2), () => saves++, () => { }, canAutoCheck: false, stub);
        var window = new SettingsWindow(
            new CityLookupService(new CitySearchService(new StubHttpMessageHandler()), CityLibrary.Empty),
            new InformationWidgetState(),
            DesktopExperienceRules.CreateDefault(),
            (_, _) => { },
            _ => { },
            _ => { },
            maintenance: new SettingsMaintenance { Updater = updater });
        ShowAndPump(window);
        try
        {
            Assert(Require<TextBlock>(window, "AppVersionText").Text == "冰蓝桌面 0.2.2", "显示当前版本");
            var autoCheck = Require<CheckBox>(window, "AutoCheckUpdatesCheckBox");
            Assert(autoCheck.IsChecked == true, "默认每天自动检查更新");
            autoCheck.IsChecked = false;
            Pump();
            Assert(!updateState.AutoCheck && saves == 1, "关闭自动检查后保存设置");

            var card = Require<Border>(window, "UpdateAvailableCard");
            Assert(card.Visibility != Visibility.Visible, "检查前不显示新版本");
            Require<Button>(window, "CheckUpdatesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => card.Visibility == Visibility.Visible, TimeSpan.FromSeconds(5), "发现新版本后显示更新内容");
            AssertEqual("新版本 9.9.9", Require<TextBlock>(window, "UpdateAvailableTitle").Text, "新版本号");
            AssertEqual("新功能说明", Require<TextBox>(window, "UpdateNotesText").Text, "更新说明");
            Assert(Require<Button>(window, "InstallUpdateButton").Visibility == Visibility.Visible, "可校验的安装包提供安装按钮");
            Assert(Require<TextBlock>(window, "UpdateStatusText").Text.Contains("9.9.9"), "状态写明新版本");
            Assert(updateState.LastCheckedAt is not null, "成功检查后记录时间");
        }
        finally
        {
            window.Close();
            Pump();
        }
    }
}
