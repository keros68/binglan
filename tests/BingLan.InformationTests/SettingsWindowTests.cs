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
            var showGreeting = Require<CheckBox>(window, "ShowTimeOfDayGreetingCheckBox");
            Assert(showGreeting.IsChecked == true, "时段问候默认开启");
            showGreeting.IsChecked = false;
            Assert(!state.ShowTimeOfDayGreeting && savedGreeting == "小明", "切换时段问候应立即保存并保留称呼");
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

            window.ShowComponentSettings(DesktopComponentKind.AudioVisualizer);
            Pump();
            var spectrum = experience.GetComponent(DesktopComponentKind.AudioVisualizer);
            var showSpectrum = Require<CheckBox>(window, "SelectedComponentVisibleCheckBox");
            Assert(!spectrum.IsVisible && showSpectrum.IsChecked == false, "频谱应在桌面卡片列表中默认关闭");
            showSpectrum.IsChecked = true;
            Assert(spectrum.IsVisible, "显示开关应启用音频频谱");
            AssertEqual("频谱颜色", Require<SettingsRow>(window, "ComponentTextColorRow").Header, "频谱颜色设置");
            AssertEqual(Visibility.Collapsed, Require<SettingsRow>(window, "ComponentMaterialRow").Visibility,
                "无底板频谱应隐藏材质设置");
            Require<TextBox>(window, "SelectedComponentTextColorEditor").Text = "#5689AB";
            window.CommitPendingChanges();
            AssertEqual("#5689AB", spectrum.Appearance.TextColor, "频谱柱颜色应写入组件");
            showSpectrum.IsChecked = false;
            Assert(!spectrum.IsVisible, "关闭显示应停用音频频谱");
        }
        finally
        {
            window.Close();
            todoWindow.CanClose = true;
            todoWindow.Close();
            Pump();
        }
    }

    public static void ComponentSizeAppliesToShownWindow()
    {
        var experience = DesktopExperienceRules.CreateDefault();
        var todoState = TodoService.CreateDefaultWidget();
        var todoWindow = new TodoWidgetWindow(todoState);
        ShowAndPump(todoWindow);
        try
        {
            // 复刻 WidgetCoordinator 的两段关键行为：窗口状态变化会把窗口当前
            // 尺寸回写进组件 Placement（Wire → OnWindowStateChanged → Capture），
            // 应用桌面状态期间这段回写被挂起（_isApplyingDesktopExperience）。
            var applyingExperience = false;
            todoWindow.WidgetChanged += _ =>
            {
                if (applyingExperience)
                {
                    return;
                }
                todoWindow.CaptureState();
                experience.GetComponent(DesktopComponentKind.Todo).Placement = todoState.Placement;
            };
            var window = new SettingsWindow(
                new CityLookupService(new CitySearchService(new StubHttpMessageHandler()), CityLibrary.Empty),
                new InformationWidgetState(),
                experience,
                (_, _) => { },
                _ => { },
                state =>
                {
                    // 与 WidgetCoordinator.ApplyDesktopExperienceCore 的待办分支一致：
                    // 第一张待办卡跟随组件 Placement，外观跟随组件 Appearance。
                    applyingExperience = true;
                    try
                    {
                        var todoComponent = state.GetComponent(DesktopComponentKind.Todo);
                        todoWindow.SetWidgetBackgroundColor(todoComponent.Appearance.BackgroundColor);
                        todoWindow.SetWidgetBackgroundOpacity(todoComponent.Appearance.BackgroundOpacity);
                        todoWindow.SetWidgetHeaderColor(todoComponent.Appearance.HeaderColor);
                        todoWindow.SetWidgetHeaderOpacity(todoComponent.Appearance.HeaderOpacity);
                        todoWindow.SetWidgetTextColor(todoComponent.Appearance.TextColor);
                        todoState.Placement = todoComponent.Placement;
                        todoState.IsLocked = todoComponent.IsLocked;
                        todoWindow.ApplyPlacement(todoState.Placement, todoState.IsLocked);
                    }
                    finally
                    {
                        applyingExperience = false;
                    }
                },
                widgetProvider: () => [todoWindow],
                applyWidgetLock: (target, locked) =>
                {
                    // 与 WidgetCoordinator.ApplyWidgetLock 一致：先改窗口，窗口事件
                    // 随即触发上面的回写。
                    target.ApplyWidgetLock(locked);
                    experience.GetComponent(DesktopComponentKind.Todo).IsLocked = locked;
                });
            ShowAndPump(window);
            try
            {
                window.ShowWidgetSettings(todoWindow);
                Pump();
                Assert(
                    Require<TextBlock>(window, "SelectedComponentHeadingText").Text.Contains("待办"),
                    "进入组件页应选中待办卡片");

                Require<TextBox>(window, "SelectedComponentWidthEditor").Text = "410";
                Require<TextBox>(window, "SelectedComponentHeightEditor").Text = "176";
                window.CommitPendingChanges();
                Pump();

                // The window lands on whole device pixels, so at 175% scaling 410 DIP reads
                // back as 410.29; anything within one pixel is the size that was set.
                Assert(Math.Abs(todoWindow.ActualWidth - 410d) < 1d, $"设置宽应用到已显示的待办窗口：实际 {todoWindow.ActualWidth}");
                Assert(Math.Abs(todoWindow.ActualHeight - 176d) < 1d, $"设置高应用到已显示的待办窗口：实际 {todoWindow.ActualHeight}");

                // 待办卡片是唯一带标题栏的内置组件：标题栏颜色与不透明度可见，
                // 且改动随组件状态落到卡片上（与分组盒的标题栏设置对齐）。
                var headerRows = Require<StackPanel>(window, "SelectedComponentHeaderRows");
                AssertEqual(Visibility.Visible, headerRows.Visibility, "待办卡片显示标题栏设置");
                Require<Slider>(window, "SelectedComponentHeaderOpacitySlider").Value = 0.5;
                Require<TextBox>(window, "SelectedComponentHeaderColorEditor").Text = "#123456";
                window.CommitPendingChanges();
                Pump();
                AssertEqual(0.5,
                    experience.GetComponent(DesktopComponentKind.Todo).Appearance.HeaderOpacity,
                    "标题栏不透明度写入组件");
                AssertEqual("#123456",
                    experience.GetComponent(DesktopComponentKind.Todo).Appearance.HeaderColor,
                    "标题栏颜色写入组件");
                AssertEqual(0.5, todoWindow.WidgetAppearance.HeaderOpacity,
                    "标题栏不透明度应用到待办窗口");
                AssertEqual("#123456", todoWindow.WidgetAppearance.HeaderColor,
                    "标题栏颜色应用到待办窗口");

                // 裸文字信息卡没有标题栏，对应设置行隐藏。
                window.ShowComponentSettings(DesktopComponentKind.TimeDate);
                Pump();
                AssertEqual(Visibility.Collapsed,
                    Require<StackPanel>(window, "SelectedComponentHeaderRows").Visibility,
                    "无标题栏组件隐藏标题栏设置");
            }
            finally
            {
                window.Close();
                Pump();
            }
        }
        finally
        {
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
        var useEverything = false;
        var favoriteMode = LauncherFavoriteMode.FollowDock;
        var backdropMode = LauncherBackdropMode.LiveWindow;
        var window = new SettingsWindow(
            new CityLookupService(new CitySearchService(new StubHttpMessageHandler()), CityLibrary.Empty),
            new InformationWidgetState(),
            DesktopExperienceRules.CreateDefault(),
            (_, _) => { },
            _ => { },
            _ => { },
            maintenance: new SettingsMaintenance
            {
                IsLauncherUseEverything = () => useEverything,
                SetLauncherUseEverything = value => useEverything = value,
                GetLauncherFavoriteMode = () => favoriteMode,
                SetLauncherFavoriteMode = mode => favoriteMode = mode,
                GetLauncherBackdropMode = () => backdropMode,
                SetLauncherBackdropMode = mode => backdropMode = mode
            },
            topBarState: topBar,
            applyTopBar: () => applies++);
        ShowAndPump(window);
        try
        {
            var navigation = Require<ListBox>(window, "SettingsNavigationList");
            var groupHeaders = navigation.Items.OfType<ListBoxItem>()
                .Where(item => item.Tag is null)
                .Select(item => item.Content as string)
                .ToList();
            Assert(groupHeaders.Count == 1 && groupHeaders.All(string.IsNullOrEmpty),
                "导航只在通用设置前保留一条分隔线，不显示分组名称");

            var tags = navigation.Items.OfType<ListBoxItem>()
                .Where(item => item.Tag is string)
                .Select(item => (string)item.Tag)
                .ToList();
            Assert(tags.SequenceEqual(["Scheme", "Displays", "Components", "TopBar", "Dock", "Taskbar", "Launcher", "Appearance", "General"]),
                "显示器设置应与桌面卡片分开，且保留其他导航项顺序");

            navigation.SelectedItem = navigation.Items
                .OfType<ListBoxItem>()
                .First(item => (item.Tag as string) == "Dock");
            Pump();
            Assert(Require<FrameworkElement>(window, "DockPage").IsVisible
                && !Require<FrameworkElement>(window, "TopBarPage").IsVisible
                && !Require<FrameworkElement>(window, "TaskbarPage").IsVisible,
                "切换到冰蓝 Dock 页后应只显示 Dock 设置");

            navigation.SelectedItem = navigation.Items
                .OfType<ListBoxItem>()
                .First(item => (item.Tag as string) == "TopBar");
            Pump();
            Assert(Require<FrameworkElement>(window, "TopBarPage").IsVisible
                && !Require<FrameworkElement>(window, "DockPage").IsVisible
                && !Require<FrameworkElement>(window, "TaskbarPage").IsVisible,
                "切换到顶部信息条页后应只显示顶部信息条设置");

            var enabled = Require<CheckBox>(window, "TopBarEnabledCheckBox");
            Assert(enabled.IsChecked == false, "顶端信息条默认关闭");
            enabled.IsChecked = true;
            Pump();
            Assert(topBar.IsEnabled, "开关应写入顶栏状态");
            Assert(applies == 1, "开关后应通知宿主应用");

            var position = Require<ComboBox>(window, "TopBarPositionComboBox");
            Assert(position.SelectedIndex == 0 && !topBar.ShowAtBottom, "信息条默认在顶部");
            position.SelectedIndex = 1;
            Pump();
            Assert(topBar.ShowAtBottom && applies == 2
                && Equals(Require<RadioButton>(window, "TopBarReserveRadio").Content, "始终显示，预留底部空间"),
                "选择底部应写入位置、立即应用并更新预留选项");

            window.SelectTopBarModule(TopBarModuleKind.Volume);
            Pump();
            var selectedToggle = Require<CheckBox>(window, "TopBarSelectedModuleEnabledCheckBox");
            Assert(selectedToggle.IsChecked == true, "选中音量模块时开关应反映已启用");
            selectedToggle.IsChecked = false;
            Pump();
            Assert(!topBar.Modules.Volume, "通过模块详情开关关闭音量应写入状态");
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                window.SelectTopBarModule(TopBarModuleKind.TrayOverflow);
                Pump();
                Assert(selectedToggle.IsChecked == true && selectedToggle.IsVisible, "系统托盘入口默认开启且可设置");
                selectedToggle.IsChecked = false;
                Pump();
                Assert(!topBar.Modules.TrayOverflow, "托盘入口开关应写入状态");
            }

            Require<RadioButton>(window, "TopBarDensityMacRadio").IsChecked = true;
            Pump();
            Assert(TopBarRules.DetectDensityPreset(topBar) == TopBarDensityPreset.MacMinimal,
                "点击 Mac 极简预设应批量切换顶栏为极简纯图标风格");
            Require<RadioButton>(window, "TopBarDensityDetailedRadio").IsChecked = true;
            Pump();
            Assert(TopBarRules.DetectDensityPreset(topBar) == TopBarDensityPreset.Detailed,
                "点击信息流预设应恢复详细文字与居中搜索条");

            var smartHide = Require<RadioButton>(window, "TopBarSmartHideRadio");
            smartHide.IsChecked = true;
            Pump();
            Assert(topBar.VisibilityMode == TopBarVisibilityMode.SmartHide, "显示方式应写入状态");

            var searchInline = Require<CheckBox>(window, "TopBarSearchInlineCheckBox");
            Assert(searchInline.IsVisible, "顶栏内联搜索开关应直接在页面可见，无需先点选搜索胶囊");

            var resetOrderButton = Require<Button>(window, "TopBarResetOrderButton");
            Assert(!resetOrderButton.IsEnabled, "默认顺序下恢复默认顺序按钮应禁用");
            window.SelectTopBarModule(TopBarModuleKind.Weather);
            Pump();
            Click(Require<Button>(window, "TopBarSelectedMoveLeftButton"));
            var (reorderedLeft, _) = TopBarRules.ResolveOrder(topBar);
            Assert(reorderedLeft[0] == TopBarModuleKind.Weather && resetOrderButton.IsEnabled,
                "点击左移按钮应立即更新顶栏模块顺序并启用恢复按钮");
            window.SelectTopBarModule(TopBarModuleKind.Clock);
            Pump();
            Click(Require<Button>(window, "TopBarSelectedSwitchSideButton"));
            var (clockMovedLeft, clockMovedRight) = TopBarRules.ResolveOrder(topBar);
            Assert(clockMovedLeft.Contains(TopBarModuleKind.Clock) && !clockMovedRight.Contains(TopBarModuleKind.Clock),
                "点击移到左侧应将时钟从右侧移到左侧");
            Click(resetOrderButton);
            Assert(TopBarRules.IsDefaultOrder(topBar) && !resetOrderButton.IsEnabled,
                "点击恢复默认顺序应还原默认排布并禁用按钮");

            var topBarAdvanced = Require<Expander>(window, "TopBarAdvancedFold");
            Assert(!topBarAdvanced.IsExpanded, "顶端信息条高度默认收起");
            topBarAdvanced.IsExpanded = true;
            Pump();

            var height = Require<Slider>(window, "TopBarHeightSlider");
            height.Value = 40;
            Pump();
            Assert(Math.Abs(topBar.Height - 40d) < 0.001d, "高度应写入状态");

            var topBarLookSummary = Require<SettingsRow>(window, "TopBarLookSummaryRow");
            Assert(topBarLookSummary.Header == (topBar.FollowCardLook ? "外观：跟随全局" : "外观：已自定义"), $"顶部信息条页应显示与状态一致的外观单行摘要：实际「{topBarLookSummary.Header}」，跟随 {topBar.FollowCardLook}");
            Click(Require<Button>(window, "TopBarLookJumpButton"));
            Assert(Require<FrameworkElement>(window, "AppearancePage").IsVisible
                && Require<FrameworkElement>(window, "AppearanceTopBarPartEditor").IsVisible,
                "点击顶部信息条页去调整应跳到外观样式页并展开顶部信息条行");

            var frosted = Require<CheckBox>(window, "TopBarFrostedCheckBox");
            Assert(frosted.IsVisible, "展开顶部信息条行后应显示磨砂玻璃开关");
            frosted.IsChecked = false;
            Pump();
            Assert(!topBar.FrostedGlass, "磨砂开关应写入状态");

            Require<RadioButton>(window, "TopBarCustomLookRadio").IsChecked = true;
            Pump();
            Assert(!topBar.FollowCardLook, "自定义外观应写入状态");
            Assert(topBarLookSummary.Header == "外观：已自定义", "自定义后顶部信息条页摘要应同步更新为已自定义");

            navigation.SelectedItem = navigation.Items
                .OfType<ListBoxItem>()
                .First(item => (item.Tag as string) == "TopBar");
            Pump();

            var visualGrid = Require<Grid>(window, "TopBarVisualBarGrid");
            foreach (var width in new[] { 900d, 1168d, 1600d })
            {
                window.Width = width;
                Pump();
                Assert(visualGrid.IsVisible && visualGrid.ActualWidth > 300d,
                    "窄窗与宽窗中模拟顶栏编辑器均应正常展示");
            }

            navigation.SelectedItem = navigation.Items
                .OfType<ListBoxItem>()
                .First(item => (item.Tag as string) == "Launcher");
            Pump();
            Assert(Require<FrameworkElement>(window, "LauncherPage").IsVisible, "冰蓝总览卡应有独立设置页");
            var everything = Require<CheckBox>(window, "LauncherEverythingCheckBox");
            Assert(everything.IsChecked == false, "Everything 搜索默认关闭");
            everything.IsChecked = true;
            Pump();
            Assert(useEverything, "总览卡 Everything 选项应写入本机设置");
            var favCombo = Require<ComboBox>(window, "LauncherFavoriteModeComboBox");
            Assert(favCombo.SelectedIndex == 0, "总览卡常用星标默认跟随 Dock");
            favCombo.SelectedIndex = 1;
            Pump();
            Assert(favoriteMode == LauncherFavoriteMode.Independent, "总览卡星标偏好应支持切换为独立收藏");
            favCombo.SelectedIndex = 2;
            Pump();
            Assert(favoriteMode == LauncherFavoriteMode.Off, "总览卡星标偏好应支持关闭");
            var backdropCombo = Require<ComboBox>(window, "LauncherBackdropModeComboBox");
            Assert(backdropCombo.SelectedIndex == 0, "总览卡背景方式默认实时透视");
            backdropCombo.SelectedIndex = 1;
            Pump();
            Assert(backdropMode == LauncherBackdropMode.WallpaperCanvas, "总览卡背景方式应支持静态壁纸底衬");
            backdropCombo.SelectedIndex = 2;
            Pump();
            Assert(backdropMode == LauncherBackdropMode.ShowDesktop, "总览卡背景方式应支持弹出时切到桌面");
            var launcherLookSummary = Require<SettingsRow>(window, "LauncherLookSummaryRow");
            Assert(launcherLookSummary.Header == "外观：跟随全局", "冰蓝总览卡页应显示外观跟随全局单行摘要");
            Click(Require<Button>(window, "LauncherLookJumpButton"));
            Assert(Require<FrameworkElement>(window, "AppearancePage").IsVisible
                && Require<FrameworkElement>(window, "AppearanceLauncherPartEditor").IsVisible
                && !Require<FrameworkElement>(window, "AppearanceTopBarPartEditor").IsVisible
                && Require<RadioButton>(window, "LauncherFollowLookRadio").IsVisible,
                "点击冰蓝总览卡页去调整应跳到外观样式页并互斥展开冰蓝总览卡外观设置");
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
            updateState, new Version(0, 2, 2), () => saves++, static () => Task.CompletedTask, canAutoCheck: false, stub);
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

    // 卸载入口只在安装版出现；点击只交给宿主启动卸载程序，失败时在页面说明并指向 Windows 设置。
    public static void UninstallEntryFollowsInstallation()
    {
        static SettingsWindow Create(SettingsMaintenance maintenance) => new(
            new CityLookupService(new CitySearchService(new StubHttpMessageHandler()), CityLibrary.Empty),
            new InformationWidgetState(),
            DesktopExperienceRules.CreateDefault(),
            (_, _) => { },
            _ => { },
            _ => { },
            maintenance: maintenance);

        var portable = Create(new SettingsMaintenance());
        ShowAndPump(portable);
        try
        {
            AssertEqual(Visibility.Collapsed, Require<FrameworkElement>(portable, "UninstallRow").Visibility,
                "免安装版不显示卸载入口");
        }
        finally
        {
            portable.Close();
            Pump();
        }

        var calls = 0;
        string? failure = null;
        var installed = Create(new SettingsMaintenance
        {
            CanUninstall = true,
            Uninstall = () =>
            {
                calls++;
                return failure;
            }
        });
        ShowAndPump(installed);
        try
        {
            AssertEqual(Visibility.Visible, Require<FrameworkElement>(installed, "UninstallRow").Visibility,
                "安装版显示卸载入口");
            var button = Require<Button>(installed, "UninstallButton");
            var status = Require<TextBlock>(installed, "UninstallStatusText");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Assert(calls == 1 && status.Visibility == Visibility.Collapsed, "启动成功时只调用一次卸载且不显示错误");

            failure = "找不到文件";
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Assert(calls == 2 && status.Visibility == Visibility.Visible &&
                   status.Text.Contains("找不到文件") && status.Text.Contains("已安装的应用"),
                "启动失败时说明原因并指向 Windows 设置");
        }
        finally
        {
            installed.Close();
            Pump();
        }
    }

    public static void RecommendedClearLookAppliesAndPreservesCustomInk()
    {
        var style = new DesktopStyleState
        {
            Glass = GlassMode.Light,
            TextInk = TextInk.Auto,
            CardShadow = CardShadow.None
        };
        var glassCalls = 0;
        var window = new SettingsWindow(
            new CityLookupService(new CitySearchService(new StubHttpMessageHandler()), CityLibrary.Empty),
            new InformationWidgetState(),
            DesktopExperienceRules.CreateDefault(),
            (_, _) => { },
            _ => { },
            _ => { },
            maintenance: new SettingsMaintenance
            {
                Style = style,
                ApplyGlass = () => glassCalls++
            });
        ShowAndPump(window);
        try
        {
            Click(Require<Button>(window, "RecommendedClearLookButton"));
            Pump();
            AssertEqual(GlassMode.Clear, style.Glass, "点击推荐效果应将材质设为无底板");
            AssertEqual(CardShadow.Soft, style.CardShadow, "点击推荐效果应开启柔和文字阴影");
            AssertEqual(TextInk.White, style.TextInk, "未自定义文字色时应将文字色设为白色");
            AssertEqual(1, glassCalls, "应通知宿主统一应用到卡片");
            AssertEqual(true, Require<RadioButton>(window, "GlassClearRadio").IsChecked, "材质单选应切到无底板");
            AssertEqual(true, Require<RadioButton>(window, "TextInkWhiteRadio").IsChecked, "文字颜色单选应切到白色");

            style.Glass = GlassMode.Light;
            style.CardShadow = CardShadow.None;
            style.TextInk = TextInk.Custom;
            style.CustomTextColor = "#E6A23C";
            Click(Require<Button>(window, "RecommendedClearLookButton"));
            Pump();
            AssertEqual(GlassMode.Clear, style.Glass, "自定义文字色下仍应切到无底板");
            AssertEqual(CardShadow.Soft, style.CardShadow, "自定义文字色下仍应开启柔和阴影");
            AssertEqual(TextInk.Custom, style.TextInk, "自定义文字色下不应重置 TextInk");
            AssertEqual("#E6A23C", style.CustomTextColor, "自定义文字色应保留原色值");
        }
        finally
        {
            window.Close();
            Pump();
        }

        // 拆分天气信息卡验证次要文字通用规则：无底板/低于 0.20 用主墨色，不低于 0.20 保持次墨色。
        var experience = DesktopExperienceRules.CreateDefault();
        var weatherComp = experience.GetComponent(DesktopComponentKind.Weather);
        weatherComp.Appearance.TextColor = "#FFFFFF";
        weatherComp.Appearance.BackgroundColor = "#EDF2F7";
        weatherComp.Appearance.BackgroundOpacity = WidgetAppearanceRules.MinimumBackgroundOpacity;
        var weatherWindow = new InformationWidgetWindow(
            new InformationWidgetState(),
            new FakeSamplingService(),
            new WeatherService(new StubHttpMessageHandler()),
            experience,
            DesktopComponentKind.Weather);
        ShowAndPump(weatherWindow);
        try
        {
            var highText = Require<TextBlock>(weatherWindow, "WeatherHighText");
            var updatedText = Require<TextBlock>(weatherWindow, "WeatherUpdatedText");
            AssertEqual(byte.MaxValue, ((System.Windows.Media.SolidColorBrush)highText.Foreground).Color.A,
                "无底板下天气次要文字（最高/最低/湿度）应使用主墨色");
            AssertEqual(byte.MaxValue, ((System.Windows.Media.SolidColorBrush)updatedText.Foreground).Color.A,
                "无底板下天气更新时间应使用主墨色");

            weatherComp.Appearance.BackgroundOpacity = 0.15d;
            weatherWindow.ApplyDesktopExperience(experience);
            Pump();
            AssertEqual(byte.MaxValue, ((System.Windows.Media.SolidColorBrush)highText.Foreground).Color.A,
                "渲染不透明度 0.15 下天气次要文字应使用主墨色");

            weatherComp.Appearance.BackgroundOpacity = 0.66d;
            weatherWindow.ApplyDesktopExperience(experience);
            Pump();
            Assert(((System.Windows.Media.SolidColorBrush)highText.Foreground).Color.A < byte.MaxValue,
                "有底板（0.66）下天气次要文字应保留次级不透明度");
            Assert(((System.Windows.Media.SolidColorBrush)updatedText.Foreground).Color.A < byte.MaxValue,
                "有底板（0.66）下天气更新时间应保留次级不透明度");
        }
        finally
        {
            weatherWindow.Close();
            Pump();
        }
    }

    private static Button FindButtonByAutomationName(DependencyObject root, string automationName)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is Button button
                && string.Equals(
                    System.Windows.Automation.AutomationProperties.GetName(button),
                    automationName,
                    StringComparison.Ordinal))
            {
                return button;
            }

            foreach (var child in LogicalTreeHelper.GetChildren(current))
            {
                if (child is DependencyObject dep)
                {
                    queue.Enqueue(dep);
                }
            }
        }

        throw new InvalidOperationException($"未找到按钮：{automationName}");
    }
}
