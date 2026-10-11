using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BingLan.App;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.App.Taskbar;
using BingLan.App.Themes;
using BingLan.App.Windows;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.Themes;

internal static class Program
{
    private const int WmNcHitTest = 0x0084;
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x00080000L;
    private const long WsExTopmost = 0x00000008L;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmWindowCornerDoNotRound = 1;
    private const int HtTransparent = -1;
    private const int HtClient = 1;
    private const int WmSettingChange = 0x001A;
    private const int SpiSetWorkArea = 0x002F;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);

    [DllImport("shell32.dll")]
    private static extern nint SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(nint window, nint region);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    private const int SwHide = 0;
    private const int SwMinimize = 6;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    private const uint GwHwndNext = 2;

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        nint window,
        int attribute,
        out int value,
        int valueSize);

    [STAThread]
    private static int Main()
    {
        // Async UI handlers (e.g. file-box background icon loads) rely on the Dispatcher
        // synchronization context to resume on this thread; the harness drives the message
        // loop manually via Pump() instead of Application.Run(), so it must be installed here.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());

        var failures = new List<string>();
        AppContext.SetSwitch(BingLan.App.App.SuppressCoordinatorStartupSwitch, true);
        var app = new BingLan.App.App();
        app.InitializeComponent();
        BingLan.App.App.UseSoftwareRendering();

        Run("待办真实控件编辑与勾选", TestTodoControls, failures);
        Run("逐像素透明 HWND 与单一可调圆角", TestUnifiedCornerAndSettings, failures);
        Run("卡片自己跟随指针拖动，锁定后不动，边缘不缩放", TestNativeHitTesting, failures);
        Run("分组盒图标按宽度均分成整列", TestFileBoxTilesFillRow, failures);
        Run("快捷入口显示系统位置图标", TestQuickPlaces, failures);
        Run("新建卡片不会重置 Dock 与卡片颜色", TestNewCardKeepsAppBrushes, failures);
        Run("卡片显示后与切换窗口后位于普通窗口下方", TestCardsStayBehindWindows, failures);
        Run("开始菜单应用列表可直接固定到 Dock", TestStartMenuApps, failures);
        Run("应用列表打开时其他窗口仍可用且按钮可见", TestAppPickerKeepsAppUsable, failures);
        Run("内置开源字体从程序中加载并带对应字重", TestBundledFonts, failures);
        Run("自动隐藏任务栏边缘不被组件占用", TestTaskbarRevealEdgeGuard, failures);
        Run("任务栏 XAML 恢复边界与取消", TestTaskbarXamlRecovery, failures);
        Run("工作区变化后卡片让出任务栏或 Dock 占用的边缘", TestCardLeavesReservedEdge, failures);
        Run("预设布局在同步状态捕获时完整应用", TestApplyPlacementDuringCapture, failures);
        Run("窗口位置尺寸关闭后重载", TestWindowPersistence, failures);
        Run("桌面分组盒导入入口、排序和图标网格", TestFileBoxWindow, failures);
        Run("桌面分组盒失效状态与系统入口菜单", TestFileBoxInvalidStateAndSystemEntries, failures);
        Run("原文件删除后映射自动从分组盒清除", TestFileMappingWatchClearsDeletedMapping, failures);
        Run("桌面新文件触发自动收纳，非桌面目录不触发", TestFileMappingWatchImportTrigger, failures);
        Run("分组盒自动刷新开关默认关闭并按需写回", TestFileBoxAutomationToggle, failures);
        Run("桌面分组盒显示名称与盒间转移", TestFileBoxRenameAndTransfer, failures);
        Run("桌面分组盒图标与列表视图切换", TestFileBoxViewModeSwitch, failures);
        Run("屏幕阅读器名称、Tab 顺序与实际 DPI 边界", TestAccessibilityAndKeyboardNavigation, failures);
        Run("高对比度系统色切换与冰蓝材质恢复", TestHighContrastThemeSwitch, failures);
        Run("单实例门禁占用与释放", TestSingleInstanceGate, failures);
        Run("托盘双击打开并恢复设置窗口", TestTrayDoubleClick, failures);
        Run("缺失本机标题字体安全回退", TestUnavailableTitleFontFallback, failures);
        Run("桌面模式同时设置 Dock 与任务栏", TestDesktopModeSelection, failures);
        Run("Dock 距底高度与最大化让出设置即时生效", TestDockPlacementSettings, failures);
        Run("待办按 Enter 提交后文字写入状态", TestTodoEnterCommitsText, failures);
        Run("待办勾选后各行仍可被辅助技术访问", TestTodoRowsStayAccessible, failures);
        Run("托盘隐藏与显示桌面组件", TestTrayToggleDesktop, failures);
        Run("Shell 隐藏或最小化组件后自动恢复显示", TestShellHideAndMinimizeRecovery, failures);
        Run("显示桌面时组件提升到任务栏下方、恢复后回落", TestDesktopSurfaceRaiseAndLower, failures);
        Run("组件恢复默认位置", TestResetComponentPlacement, failures);
        Run("清爽桌面开关", TestCleanDesktopToggle, failures);
        Run("设置行分隔线对齐标题", TestSettingsRowDividersAlignWithTitles, failures);
        Run("整体风格与清爽桌面恢复提示", TestStyleSettingsAndRestoreNotice, failures);
        Run("便签尺寸输入后自动生效", TestAppearancePanelSizeAppliesOnItsOwn, failures);
        Run("首次使用引导逐步应用设置", TestOnboardingWizard, failures);
        Run("卡片拖动吸附到相邻卡片且不会被系统吸附成半屏", () => SnapMoveTests.Run(ShowAndPump, Pump), failures);
        Run("显示器排列切换后卡片回到该排列下的位置", () => DisplayLayoutWindowTests.Run(ShowAndPump, Pump), failures);
        Run("Dock 隐藏把手的窗口样式、拖动与显隐", () => DockHandleTests.Run(ShowAndPump, Pump), failures);
        Run("顶端信息条的窗口样式、模块与点击深链", () => TopBarWindowTests.Run(ShowAndPump, Pump), failures);
        Run("主题导入对话框显示名称与预览图", TestThemeImportDialogShowsNameAndPreview, failures);

        if (failures.Count > 0)
        {
            Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
            return 1;
        }

        Console.WriteLine("全部 WPF UI 烟雾测试通过。");
        return 0;
    }

    private static void TestUnifiedCornerAndSettings()
    {
        var state = new TodoWidgetState { CornerRadius = 15.5d };
        var window = new TodoWidgetWindow(state);
        ShowAndPump(window);
        var appearancePanel = new WidgetAppearancePanel();
        var appearanceHost = new Window
        {
            Content = appearancePanel,
            Width = 420,
            Height = 700,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            var exStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            Assert(window.AllowsTransparency, "正式组件没有启用逐像素透明窗口");
            Assert((exStyle & WsExLayered) != 0, "正式组件缺少 WS_EX_LAYERED");
            Assert(
                window.BackdropResult.Mode == WidgetBackdropMode.LayeredTint,
                "逐像素正式路径不应重新启用矩形 WCA 背景");
            Assert(window.NativeStyleError is null, $"原生样式失败：{window.NativeStyleError}");
            Assert(window.NativeShapeError is null, $"原生圆角失败：{window.NativeShapeError}");
            Assert(
                GetDwmAttribute(handle, DwmwaWindowCornerPreference) == DwmWindowCornerDoNotRound,
                "正式组件没有关闭第二层 DWM 圆角");
            AssertWindowRegionCleared(handle);
            AssertLayeredClip(window, 15.5d, "默认圆角");
            AssertAntialiasedCorner(window);

            var surface = Require<Border>(window, "Surface");
            Assert(surface.Background == Brushes.Transparent, "可见描边不能再绘制第二层背景");
            Assert(surface.BorderThickness == new Thickness(0), "逐像素外轮廓仍叠加白色描边");
            Assert(surface.CornerRadius == new CornerRadius(15.5d), "默认描边圆角未复用宿主值");
            Assert(
                Near(surface.ActualWidth, window.ActualWidth) &&
                Near(surface.ActualHeight, window.ActualHeight),
                "组件表面仍被旧阴影留白内缩");
            AssertAppearanceBrush(
                window.WidgetBackgroundBrush,
                Color.FromArgb(82, 255, 255, 255),
                SystemColors.WindowColor,
                "Pogget 默认主体材质");
            AssertAppearanceBrush(
                window.WidgetHeaderBrush,
                Color.FromArgb(247, 160, 180, 225),
                SystemColors.ControlColor,
                "Pogget 默认标题材质");
            var titleEditor = Require<TextBox>(window, "TitleEditor");
            Assert(
                titleEditor.FontFamily.Source == WidgetAppearanceRules.DefaultTitleFontFamily &&
                titleEditor.FontWeight == FontWeights.Bold &&
                titleEditor.FontStyle == FontStyles.Normal,
                "标题没有采用微软雅黑、粗体、非斜体默认值");
            Assert(
                ReferenceEquals(Require<Grid>(window, "BodySurface").Background, window.WidgetBackgroundBrush),
                "主体没有复用宿主材质 Brush");
            var titleBar = Require<Border>(window, "TitleBarSurface");
            Assert(
                ReferenceEquals(titleBar.Background, window.WidgetHeaderBrush),
                "标题栏没有复用宿主材质 Brush");
            Assert(
                titleBar.CornerRadius == window.HeaderCornerRadius &&
                Near(titleBar.CornerRadius.TopLeft, 9.2d),
                "默认标题栏圆角没有按外圆角比例派生为 9.2 DIP");
            Assert(
                Near(window.ItemCornerRadius.TopLeft, 10d),
                "默认内容卡片圆角没有按外圆角比例派生为 10 DIP");
            Assert(titleBar.Margin == new Thickness(10, 10, 10, 0), "标题栏没有采用内嵌布局");

            var liveCapturePath = Environment.GetEnvironmentVariable("BINGLAN_UI_CAPTURE");
            if (!string.IsNullOrWhiteSpace(liveCapturePath))
            {
                SaveLivePreview(window, liveCapturePath);
            }

            appearancePanel.Attach(window);
            ShowAndPump(appearanceHost);
            var backgroundSlider = Require<Slider>(appearancePanel, "BackgroundOpacitySlider");
            var headerSlider = Require<Slider>(appearancePanel, "HeaderOpacitySlider");
            var slider = Require<Slider>(appearancePanel, "CornerRadiusSlider");
            var fontPicker = Require<ComboBox>(appearancePanel, "TitleFontFamilyPicker");
            var boldToggle = Require<CheckBox>(appearancePanel, "TitleFontBoldToggle");
            var italicToggle = Require<CheckBox>(appearancePanel, "TitleFontItalicToggle");
            Assert(fontPicker.Items.OfType<string>().Contains("Segoe UI"),
                "本机字体选择器没有列出 Segoe UI");
            var appearanceChangeNotified = false;
            window.WidgetChanged += _ => appearanceChangeNotified = true;
            var clipBeforeMaterialChange = window.Clip;
            backgroundSlider.Value = 0.46d;
            headerSlider.Value = 0.74d;
            var backgroundEditor = Require<TextBox>(appearancePanel, "BackgroundColorEditor");
            backgroundEditor.Text = "#C1D2E3";
            backgroundEditor.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            var headerEditor = Require<TextBox>(appearancePanel, "HeaderColorEditor");
            headerEditor.Text = "#91A2B3";
            headerEditor.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            var textColorEditor = Require<TextBox>(appearancePanel, "TextColorEditor");
            textColorEditor.Text = "#FFFFFF";
            textColorEditor.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Pump();
            Assert(state.Appearance.BackgroundOpacity == 0.46d, "主体透明度没有实时写回状态");
            Assert(state.Appearance.HeaderOpacity == 0.74d, "标题栏透明度没有实时写回状态");
            Assert(state.Appearance.BackgroundColor == "#C1D2E3", "主体颜色没有实时写回状态");
            Assert(state.Appearance.HeaderColor == "#91A2B3", "标题栏颜色没有实时写回状态");
            Assert(state.Appearance.TextColor == "#FFFFFF", "卡片文字颜色没有实时写回状态");
            AssertAppearanceBrush(
                window.WidgetBackgroundBrush,
                Color.FromArgb(117, 193, 210, 227),
                SystemColors.WindowColor,
                "实时主体材质");
            AssertAppearanceBrush(
                window.WidgetHeaderBrush,
                Color.FromArgb(189, 145, 162, 179),
                SystemColors.ControlColor,
                "实时标题材质");
            AssertAppearanceBrush(
                window.WidgetTextBrush,
                Color.FromArgb(255, 255, 255, 255),
                SystemColors.WindowTextColor,
                "实时卡片文字颜色");
            var blackTextButton = Require<Button>(appearancePanel, "BlackTextColorButton");
            blackTextButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(state.Appearance.TextColor == "#000000", "黑色文字快捷项没有生效");
            var whiteTextButton = Require<Button>(appearancePanel, "WhiteTextColorButton");
            whiteTextButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(state.Appearance.TextColor == "#FFFFFF", "白色文字快捷项没有生效");
            Assert(ReferenceEquals(clipBeforeMaterialChange, window.Clip),
                "调色不应生成第二份外轮廓");
            Assert(appearanceChangeNotified, "材质设置没有触发现有防抖保存链路");

            appearanceChangeNotified = false;
            var clipBeforeTypographyChange = window.Clip;
            fontPicker.SelectedItem = "Segoe UI";
            boldToggle.IsChecked = false;
            boldToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            italicToggle.IsChecked = true;
            italicToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(
                state.Appearance.TitleFontFamily == "Segoe UI" &&
                !state.Appearance.TitleFontBold &&
                state.Appearance.TitleFontItalic,
                "标题字体设置没有实时写回状态");
            Assert(
                titleEditor.FontFamily.Source == "Segoe UI" &&
                titleEditor.FontWeight == FontWeights.Normal &&
                titleEditor.FontStyle == FontStyles.Italic,
                "标题字体设置没有即时更新可见标题");
            Assert(ReferenceEquals(clipBeforeTypographyChange, window.Clip),
                "切换标题字体不应生成第二份外轮廓");
            Assert(appearanceChangeNotified, "标题字体设置没有触发现有防抖保存链路");

            var radiusChangeNotified = false;
            window.WidgetChanged += _ => radiusChangeNotified = true;
            slider.Value = 3d;
            Pump();
            window.CaptureState();
            Assert(window.WidgetCornerRadius == 3d, "圆角滑块没有更新宿主");
            Assert(radiusChangeNotified, "圆角滑块没有触发现有防抖保存链路");
            Assert(state.CornerRadius == 3d, "圆角滑块没有写回待办状态");
            Assert(surface.CornerRadius == new CornerRadius(3d), "圆角滑块没有同步可见描边");
            Assert(
                Near(titleBar.CornerRadius.TopLeft, 3d * 9.2d / 15.5d) &&
                Near(window.ItemCornerRadius.TopLeft, 3d * 10d / 15.5d),
                "圆角滑块没有按同一比例同步标题栏与内容卡片圆角");
            AssertLayeredClip(window, 3d, "3 DIP 圆角");
            AssertClipPoint(window, new Point(8, 8), true, "3 DIP 固定采样点");

            var resetAppearance = FindVisualChildren<Button>(appearancePanel)
                .Single(x => Equals(x.Content, "恢复冰蓝默认"));
            resetAppearance.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            window.CaptureState();
            Assert(window.WidgetCornerRadius == 15.5d, "恢复默认圆角菜单没有工作");
            Assert(state.CornerRadius == 15.5d, "默认圆角没有写回待办状态");
            Assert(state.Appearance.BackgroundColor == "#FFFFFF" &&
                   state.Appearance.BackgroundOpacity == 0.32d &&
                   state.Appearance.HeaderColor == "#A0B4E1" &&
                   state.Appearance.HeaderOpacity == 0.97d &&
                   state.Appearance.TextColor == WidgetAppearanceRules.DefaultTextColor &&
                   state.Appearance.TitleFontFamily == WidgetAppearanceRules.DefaultTitleFontFamily &&
                   state.Appearance.TitleFontBold &&
                   !state.Appearance.TitleFontItalic,
                "恢复冰蓝默认没有重置完整材质");
            Assert(
                titleEditor.FontFamily.Source == WidgetAppearanceRules.DefaultTitleFontFamily &&
                titleEditor.FontWeight == FontWeights.Bold &&
                titleEditor.FontStyle == FontStyles.Normal,
                "恢复冰蓝默认没有重置标题字体");
            window.WidgetCornerRadius = 100d;
            Pump();
            Assert(window.WidgetCornerRadius == 32d, "正式宿主没有钳制最大圆角");
            AssertLayeredClip(window, 32d, "32 DIP 圆角");
            AssertClipPoint(window, new Point(8, 8), false, "32 DIP 固定采样点");

            window.WidgetCornerRadius = 0d;
            Pump();
            AssertWindowRegionCleared(handle);
            Assert(window.Clip is null, "直角模式没有移除逐像素圆角 Clip");
            Assert(surface.CornerRadius == new CornerRadius(0d), "直角模式仍保留 WPF 描边圆角");

            window.WidgetCornerRadius = 15.5d;
            window.Width += 40d;
            Pump();
            AssertLayeredClip(window, 15.5d, "调整大小后的圆角");
        }
        finally
        {
            appearanceHost.Close();
            Close(window);
        }
    }

    private static void TestTodoControls()
    {
        var state = new TodoWidgetState();
        TodoService.Add(state, "原待办");
        var window = new TodoWidgetWindow(state);
        ShowAndPump(window);
        try
        {
            var title = Require<TextBox>(window, "TitleEditor");
            Assert(Near(title.FontSize, 19d) && title.FontWeight == FontWeights.Bold,
                "标题没有采用 Pogget 字体层级");
            Assert(title.IsReadOnly && !title.IsHitTestVisible,
                "普通桌面状态下标题应是拖动区域，不直接进入编辑");
            var settingsButton = FindVisualChildren<Button>(window)
                .Single(x => Equals(x.Content, "•••"));
            Assert(settingsButton.Visibility == Visibility.Collapsed,
                "普通桌面状态仍显示设置控件");
            Assert(
                !FindVisualChildren<Button>(window).Any(x =>
                    Equals(x.ToolTip, "拖动组件") || Equals(x.ToolTip, "关闭组件")) &&
                !FindVisualChildren<Thumb>(window).Any(x => Equals(x.ToolTip, "拖动调整卡片宽高")),
                "桌面卡片不应带拖动、关闭或缩放手柄");
            window.ApplySelection(true);
            Pump();
            Assert(settingsButton.Visibility == Visibility.Visible,
                "选中卡片后没有显示统一设置入口");
            window.ApplySelection(false);

            var titleCenter = title.TranslatePoint(
                new Point(title.ActualWidth / 2d, title.ActualHeight / 2d),
                window);
            Assert(window.CanStartDragAt(titleCenter), "未编辑时标题应可拖动卡片");
            Assert(window.TryBeginTitleEditAt(titleCenter), "双击标题没有进入标题编辑");
            Pump();
            Assert(
                window.IsTitleEditing && !title.IsReadOnly && title.IsHitTestVisible && title.Focusable,
                "双击标题后标题框不可编辑");
            Assert(!window.CanStartDragAt(titleCenter), "编辑标题时标题不应再拖动卡片");
            title.Text = "  直接编辑标题  ";
            PressKey(title, Key.Enter);
            Pump();
            Assert(
                !window.IsTitleEditing && title.IsReadOnly &&
                state.Title == "直接编辑标题" && title.Text == "直接编辑标题",
                "Enter 没有提交标题并结束编辑");

            PressKey(title, Key.F2);
            Pump();
            Assert(window.IsTitleEditing, "F2 没有进入标题编辑");
            title.Text = "放弃的标题";
            PressKey(title, Key.Escape);
            Pump();
            Assert(
                !window.IsTitleEditing && state.Title == "直接编辑标题" && title.Text == "直接编辑标题",
                "Esc 没有放弃标题修改");

            window.BeginTitleEdit();
            title.Text = "";
            window.EndTitleEdit(commit: true);
            Assert(state.Title == "待办便签", "空标题没有回退到默认标题");

            var todoCard = FindVisualChildren<Border>(window)
                .Single(x => Equals(x.Tag, "Interactive"));
            Assert(
                todoCard.CornerRadius == window.ItemCornerRadius &&
                Near(todoCard.CornerRadius.TopLeft, 10d),
                "待办卡片没有复用宿主派生圆角");
            window.WidgetCornerRadius = 3d;
            Pump();
            Assert(
                todoCard.CornerRadius == window.ItemCornerRadius &&
                Near(todoCard.CornerRadius.TopLeft, 3d * 10d / 15.5d),
                "待办卡片没有随外圆角同步变化");
            window.WidgetCornerRadius = WidgetAppearanceRules.DefaultCornerRadius;
            Pump();
            var checkBox = FindVisualChildren<CheckBox>(window).Single();
            checkBox.IsChecked = true;
            Pump();
            Assert(state.Items.Single().IsCompleted, "勾选框未更新完成状态");

            var add = FindVisualChildren<Button>(window).Single(x => Equals(x.Content, "+"));
            add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(state.Items.Count == 2, "新增按钮未创建待办");

            WidgetWindowBase? settingsTarget = null;
            window.OpenAppSettingsRequested += target => settingsTarget = target;
            settingsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(ReferenceEquals(settingsTarget, window),
                "设置按钮没有把当前卡片传给统一设置 App");

            var deleteButtons = FindVisualChildren<Button>(window)
                .Where(x => Equals(x.Content, "×"))
                .ToList();
            Assert(deleteButtons.Count == 2, "每条待办都应有自己的删除按钮");
            Assert(
                deleteButtons.All(x => x.ActualWidth >= 32d && x.ActualHeight >= 32d),
                "待办删除按钮的点击区域小于 32 DIP");
            deleteButtons[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(
                state.Items.Count == 1 && state.Items[0].Text != "原待办",
                "“×”没有只删除所在的整条待办");
            SavePreview(window, Path.Combine(Path.GetTempPath(), "BingLan-TodoPreview.png"));
        }
        finally
        {
            Close(window);
        }
    }

    private static void TestAccessibilityAndKeyboardNavigation()
    {
        TestTodoAccessibility();
        TestFileBoxAccessibility();
        TestSettingsAccessibility();
    }

    private static void TestHighContrastThemeSwitch()
    {
        var todo = new TodoWidgetWindow(new TodoWidgetState());
        var note = new NoteWidgetWindow(new NoteWidgetState());
        using var sampler = new WindowsPerformanceSamplingService();
        var information = new InformationWidgetWindow(
            new InformationWidgetState(),
            sampler,
            new WeatherService());
        var fileBox = new FileBoxWindow(new FileBoxState());
        var settings = new SettingsWindow(
            new CityLookupService(),
            new InformationWidgetState(),
            DesktopExperienceRules.CreateDefault(),
            (_, _) => { },
            _ => { },
            _ => { });
        var appearancePanel = new WidgetAppearancePanel();
        var appearanceHost = new Window
        {
            Content = appearancePanel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false
        };
        var widgetWindows = new WidgetWindowBase[] { todo, note, information, fileBox };

        try
        {
            foreach (var window in widgetWindows)
            {
                ShowAndPump(window);
            }
            ShowAndPump(settings);
            ShowAndPump(appearanceHost);

            AccessibilityThemeManager.SetHighContrastForTesting(true);
            Pump();
            Assert(AccessibilityThemeManager.IsHighContrastEnabled,
                "模拟高对比度没有激活统一主题状态");
            AssertThemeResourcesOpaque();
            foreach (var window in widgetWindows)
            {
                AssertWidgetHighContrast(window);
                AssertVisibleBrushesOpaque(window, window.GetType().Name);
            }
            AssertSettingsHighContrast(settings);
            AssertVisibleBrushesOpaque(settings, "设置窗口");
            AssertVisibleBrushesOpaque(appearancePanel, "外观面板");
            SavePreview(
                todo,
                Path.Combine(Path.GetTempPath(), "BingLan-HighContrast-Todo.png"));
            SaveElementPreview(
                settings.Content as FrameworkElement ??
                    throw new InvalidOperationException("设置窗口内容不可用于高对比度截图"),
                settings.Background,
                Path.Combine(Path.GetTempPath(), "BingLan-HighContrast-Settings.png"));

            if (!SystemParameters.HighContrast)
            {
                AccessibilityThemeManager.SetHighContrastForTesting(false);
                Pump();
                Assert(!AccessibilityThemeManager.IsHighContrastEnabled,
                    "退出模拟高对比度后统一主题状态没有恢复");
                AssertAppearanceBrush(
                    todo.WidgetBackgroundBrush,
                    Color.FromArgb(82, 255, 255, 255),
                    SystemColors.WindowColor,
                    "退出高对比度后的待办主体材质");
                AssertAppearanceBrush(
                    todo.WidgetHeaderBrush,
                    Color.FromArgb(247, 160, 180, 225),
                    SystemColors.ControlColor,
                    "退出高对比度后的待办标题材质");
                AssertBrushColor(
                    settings.Background,
                    settings.IsBackdropActive ? Colors.Transparent : Color.FromRgb(238, 247, 253),
                    "退出高对比度后的设置背景（Mica 时透明，否则为冰蓝页面色）");
                AssertResourceBrushColor(
                    "SettingsPanelBorderBrush",
                    Color.FromArgb(143, 255, 255, 255));
                AssertResourceBrushColor(
                    "AppearanceInputTextBrush",
                    Color.FromRgb(36, 49, 70));
                Assert(
                    todo.BackdropResult.Detail.Contains("半透明材质", StringComparison.Ordinal),
                    "退出高对比度后组件没有恢复半透明材质说明");
            }
        }
        finally
        {
            AccessibilityThemeManager.RestoreSystemModeForTesting();
            foreach (var window in widgetWindows)
            {
                if (PresentationSource.FromVisual(window) is not null)
                {
                    Close(window);
                }
            }
            if (settings.IsVisible)
            {
                settings.Close();
            }
            if (appearanceHost.IsVisible)
            {
                appearanceHost.Close();
            }
            Pump();
        }
    }

    private static void TestTodoAccessibility()
    {
        var state = new TodoWidgetState();
        TodoService.Add(state, "检查今日计划");
        var window = new TodoWidgetWindow(state);
        ShowAndPump(window);
        try
        {
            window.ApplySelection(true);
            Pump();
            var buttons = FindVisualChildren<Button>(window).ToList();
            var expectedOrder = new Control[]
            {
                buttons.Single(button => Equals(button.ToolTip, "设置")),
                buttons.Single(button => Equals(button.ToolTip, "新增待办")),
                FindVisualChildren<CheckBox>(window).Single(),
                FindVisualChildren<TextBox>(window).Single(textBox => textBox.Name != "TitleEditor"),
                buttons.Single(button => Equals(button.ToolTip, "删除待办")),
                buttons.Single(button => Equals(button.Content, "隐藏已完成"))
            };
            AssertAccessibleControls(window, "待办组件");
            AssertTabOrderContains(window, expectedOrder, "待办组件");
            ReportDpi(window, "待办组件");
        }
        finally
        {
            Close(window);
        }
    }

    private static void TestFileBoxAccessibility()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-a11y-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var path = Path.Combine(temp, "键盘测试.txt");
        File.WriteAllText(path, "accessibility");
        var state = new FileBoxState();
        FileMappingService.AddExisting(state, [path]);
        var window = new FileBoxWindow(state);
        ShowAndPump(window);
        try
        {
            window.ApplySelection(true);
            PumpUntil(
                () => Require<ItemsControl>(window, "FileList").Items.Count == 1,
                "文件盒无障碍测试项没有生成");
            var tile = FindVisualChildren<Button>(window)
                .Single(button => button.DataContext is FileBoxWindow.FileTile);
            Assert(tile.Focusable, "文件映射卡片不能通过键盘聚焦");
            var tilePeer = UIElementAutomationPeer.CreatePeerForElement(tile) ??
                throw new InvalidOperationException("文件映射卡片没有 UI Automation Peer");
            Assert(
                tilePeer.GetPattern(PatternInterface.Invoke) is not null,
                "文件映射卡片没有向屏幕阅读器提供调用操作");

            var buttons = FindVisualChildren<Button>(window).ToList();
            var expectedOrder = new Control[]
            {
                Require<Button>(window, "AddButton"),
                Require<Button>(window, "SortButton"),
                buttons.Single(button => Equals(button.ToolTip, "设置")),
                tile,
                Require<Button>(window, "AutoOrganizeButton")
            };
            AssertAccessibleControls(window, "桌面分组盒");
            AssertTabOrderContains(window, expectedOrder, "桌面分组盒");
            ReportDpi(window, "桌面分组盒");
        }
        finally
        {
            Close(window);
            Directory.Delete(temp, true);
        }
    }

    private static void TestSettingsAccessibility()
    {
        var window = new SettingsWindow(
            new CityLookupService(),
            new InformationWidgetState(),
            DesktopExperienceRules.CreateDefault(),
            (_, _) => { },
            _ => { },
            _ => { });
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        ShowAndPump(window);
        try
        {
            var navigation = Require<ListBox>(window, "SettingsNavigationList");
            AssertAccessibleControls(window, "设置-布局页");
            var layoutNavigationItem = navigation.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem ??
                throw new InvalidOperationException("布局导航项没有生成");
            AssertTabOrderContains(
                window,
                [
                    layoutNavigationItem,
                    Require<RadioButton>(window, "QuietInformationPresetRadio"),
                    Require<Button>(window, "ApplyLayoutButton")
                ],
                "设置-布局页");

            navigation.SelectedIndex = 1;
            Pump();
            var desktopComponentList = Require<ListBox>(window, "DesktopComponentList");
            var componentNavigationItem = navigation.ItemContainerGenerator.ContainerFromIndex(1) as ListBoxItem ??
                throw new InvalidOperationException("桌面组件导航项没有生成");
            var selectedComponentItem = desktopComponentList.ItemContainerGenerator.ContainerFromItem(
                desktopComponentList.SelectedItem) as ListBoxItem ??
                throw new InvalidOperationException("所选桌面组件项没有生成");
            var componentOrder = new Control[]
            {
                componentNavigationItem,
                selectedComponentItem,
                Require<CheckBox>(window, "SelectedComponentVisibleCheckBox"),
                Require<CheckBox>(window, "SelectedComponentLockedCheckBox"),
                Require<TextBox>(window, "GreetingNameEditor"),
                Require<CheckBox>(window, "Use24HourClockCheckBox"),
                Require<Slider>(window, "SelectedComponentFontScaleSlider"),
                Require<TextBox>(window, "SelectedComponentTextColorEditor"),
                Require<TextBox>(window, "SelectedComponentBackgroundColorEditor"),
                Require<Slider>(window, "SelectedComponentOpacitySlider"),
                Require<Slider>(window, "SelectedComponentCornerRadiusSlider"),
                Require<ComboBox>(window, "SelectedComponentDensitySelector"),
                Require<TextBox>(window, "SelectedComponentWidthEditor"),
                Require<TextBox>(window, "SelectedComponentHeightEditor")
            };
            AssertAccessibleControls(window, "设置-桌面组件页");
            AssertTabOrderContains(window, componentOrder, "设置-桌面组件页");
            ReportDpi(window, "设置窗口");
            ReportHighContrast();
        }
        finally
        {
            window.Close();
            Pump();
        }
    }

    private static void TestNativeHitTesting()
    {
        var state = new TodoWidgetState
        {
            Placement = new WindowPlacement { Left = 150, Top = 120, Width = 310, Height = 360 }
        };
        var window = new TodoWidgetWindow(state);
        ShowAndPump(window);
        try
        {
            window.SetWidgetTitleFontFamily("Segoe UI");
            window.SetWidgetTitleFontBold(false);
            window.SetWidgetTitleFontItalic(true);
            Pump();
            var width = window.ActualWidth;
            var height = window.ActualHeight;
            // The whole surface is client area: the card moves itself, so dragging works
            // even when Windows is set to drag only a window outline.
            AssertHit(window, new Point(45, height - 28), HtClient, "空白区");
            AssertHit(window, new Point(width - 2, height / 2), HtClient, "右边缘不缩放");
            AssertHit(window, new Point(0, 0), HtTransparent, "透明左上角");
            var blank = new Point(45, height - 28);
            Assert(window.CanStartDragAt(blank), "未锁定卡片的空白区应可拖动");
            Assert(window.CanStartDragAt(new Point(width - 2, height / 2)), "未锁定卡片的边缘应可拖动");
            var addButton = FindVisualChildren<Button>(window).Single(x => Equals(x.Content, "+"));
            Assert(
                !window.CanStartDragAt(addButton.TranslatePoint(new Point(4, 4), window)),
                "按钮上按下不应拖动卡片");

            var handle = new WindowInteropHelper(window).Handle;
            GetWindowRect(handle, out var before);
            Assert(window.BeginDrag(1000, 600), "未锁定卡片没有开始拖动");
            window.DragTo(1001, 601);
            Pump();
            GetWindowRect(handle, out var jitter);
            Assert(jitter.Left == before.Left && jitter.Top == before.Top, "按下后的轻微抖动不应移动卡片");
            window.DragTo(1137, 683);
            Pump();
            GetWindowRect(handle, out var moved);
            Assert(
                moved.Left == before.Left + 137 && moved.Top == before.Top + 83 &&
                moved.Right - moved.Left == before.Right - before.Left,
                $"卡片没有跟随指针移动：期望 {before.Left + 137},{before.Top + 83}，实际 {moved.Left},{moved.Top}");
            window.EndDrag();
            Pump();

            Assert(window.ResizeEdgesAt(blank) == ResizeEdges.None, "空白区应移动而不是改大小");
            Assert(
                window.ResizeEdgesAt(new Point(width - 2, height - 2)) == (ResizeEdges.Right | ResizeEdges.Bottom),
                "右下角应同时调整宽高");
            Assert(window.ResizeEdgesAt(new Point(2, height / 2)) == ResizeEdges.Left, "左边缘应调整宽度");

            GetWindowRect(handle, out var beforeResize);
            Assert(window.BeginDrag(1500, 900, ResizeEdges.Right | ResizeEdges.Bottom), "右下角没有开始调整大小");
            window.DragTo(1560, 940);
            Pump();
            GetWindowRect(handle, out var resized);
            Assert(
                resized.Left == beforeResize.Left && resized.Top == beforeResize.Top &&
                resized.Right - resized.Left == beforeResize.Right - beforeResize.Left + 60 &&
                resized.Bottom - resized.Top == beforeResize.Bottom - beforeResize.Top + 40,
                "拖动右下角应只改变宽高，左上角不动");
            window.DragTo(500, 300);
            Pump();
            GetWindowRect(handle, out var smallest);
            var dpi = VisualTreeHelper.GetDpi(window);
            Assert(
                smallest.Right - smallest.Left >= (int)Math.Floor(window.MinWidth * dpi.DpiScaleX) &&
                smallest.Bottom - smallest.Top >= (int)Math.Floor(window.MinHeight * dpi.DpiScaleY),
                "调整大小不应小于卡片的最小尺寸");
            window.EndDrag();
            Pump();
            window.CaptureState();
            Assert(Near(state.Placement.Width, window.ActualWidth), "调整后的宽度没有写回状态");

            state.IsLocked = true;
            window.ApplyPlacement(state.Placement, state.IsLocked);
            Pump();
            Assert(!window.CanStartDragAt(blank), "锁定后空白区不应拖动");
            Assert(!window.BeginDrag(1000, 600), "锁定后不应开始拖动");
        }
        finally
        {
            Close(window);
        }
    }

    private static void TestStartMenuApps()
    {
        var apps = BingLan.App.Dock.DockShortcuts.ReadStartMenuApps();
        Console.WriteLine($"信息：开始菜单中可固定的应用 {apps.Count} 个");
        Assert(apps.All(app => app.ExecutablePath is { } path && File.Exists(path)
                && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)),
            "开始菜单列表只应包含存在的程序");
        Assert(apps.Select(app => app.ExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == apps.Count,
            "同一个程序只应列出一次");
        Assert(!apps.Any(app => app.DisplayName.Contains("卸载") || app.DisplayName.Contains("Uninstall", StringComparison.OrdinalIgnoreCase)),
            "卸载程序不应列出");
        Assert(apps.Any(app => app.DisplayName == "文件资源管理器"
                && string.Equals(Path.GetFileName(app.ExecutablePath), "explorer.exe", StringComparison.OrdinalIgnoreCase)),
            "应用列表应包含文件资源管理器");
    }

    private static void TestBundledFonts()
    {
        Assert(InstalledFontCatalog.Names.Take(InstalledFontCatalog.BundledNames.Count)
                .SequenceEqual(InstalledFontCatalog.BundledNames),
            "内置字体应排在字体列表最前面");
        foreach (var (name, file) in new[]
                 {
                     ("得意黑", "SMILEYSANS-OBLIQUE.TTF"),
                     ("Jost 极细", "JOST-200-THIN.TTF"),
                     ("Jost", "JOST-400-BOOK.TTF"),
                     ("Quicksand 细", "QUICKSAND-LIGHT.TTF")
                 })
        {
            var family = InstalledFontCatalog.Create(name);
            var weight = InstalledFontCatalog.WeightOf(name, FontWeights.Normal);
            var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
            Assert(typeface.TryGetGlyphTypeface(out var glyphs), $"{name} 没有加载到字形");
            Assert(
                string.Equals(Path.GetFileName(glyphs.FontUri.LocalPath), file, StringComparison.OrdinalIgnoreCase),
                $"{name} 应来自 {file}，实际 {glyphs.FontUri}");
        }
        Assert(InstalledFontCatalog.WeightOf("Jost 极细", FontWeights.Bold) == FontWeights.ExtraLight
            && InstalledFontCatalog.WeightOf("Segoe UI", FontWeights.Bold) == FontWeights.Bold,
            "内置字体带自己的字重，系统字体用调用方的字重");

        var card = new TodoWidgetWindow(new TodoWidgetState());
        ShowAndPump(card);
        try
        {
            card.SetWidgetTitleFontFamily("得意黑");
            card.SetWidgetTitleFontBold(false);
            Pump();
            var title = Require<TextBox>(card, "TitleEditor");
            Assert(new Typeface(title.FontFamily, title.FontStyle, title.FontWeight, title.FontStretch)
                    .TryGetGlyphTypeface(out var titleGlyphs)
                && titleGlyphs.CharacterToGlyphMap.ContainsKey('冰'),
                "卡片标题选用得意黑后应能显示中文");
        }
        finally
        {
            Close(card);
        }
    }

    private static void TestAppPickerKeepsAppUsable()
    {
        var card = new TodoWidgetWindow(new TodoWidgetState());
        ShowAndPump(card);
        IReadOnlyList<DockPinnedApp>? chosen = null;
        AppPickerDialog.Open(null, [], apps => chosen = apps);
        Pump();
        var picker = System.Windows.Application.Current.Windows.OfType<AppPickerDialog>().Single();
        try
        {
            Assert(card.IsEnabled, "应用列表打开时卡片不应被禁用");
            AppPickerDialog.Open(null, [], _ => { });
            Pump();
            Assert(System.Windows.Application.Current.Windows.OfType<AppPickerDialog>().Count() == 1,
                "重复打开应切回已开的应用列表");

            foreach (var name in new[] { "固定所选", "取消", "浏览程序文件…" })
            {
                var button = FindVisualChildren<Button>(picker).Single(candidate => Equals(candidate.Content, name));
                Assert(button.IsVisible && button.ActualHeight >= 30, $"“{name}”按钮应可见");
                var text = ((SolidColorBrush)button.Foreground).Color;
                var back = ((SolidColorBrush)button.Background).Color;
                Assert(text != back && !(text.R > 240 && text.G > 240 && text.B > 240 && back.A < 40),
                    $"“{name}”按钮的文字不应是透明底上的白字");
            }

            var search = FindVisualChildren<TextBox>(picker).Single();
            var searchText = ((SolidColorBrush)search.Foreground).Color;
            Assert(search.IsVisible && search.BorderThickness.Left >= 1 && !(searchText.R > 240 && searchText.G > 240 && searchText.B > 240),
                "搜索框应有边框且文字不是白色");

            FindVisualChildren<Button>(picker).Single(candidate => Equals(candidate.Content, "固定所选"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(chosen is null && picker.IsVisible, "未勾选时点击固定不应关闭列表");
        }
        finally
        {
            picker.Close();
            Close(card);
            Pump();
        }
    }

    private static void TestCardsStayBehindWindows()
    {
        var card = new TodoWidgetWindow(new TodoWidgetState
        {
            Placement = new WindowPlacement { Left = 300, Top = 300, Width = 300, Height = 300 }
        });
        var other = new Window { Left = 350, Top = 350, Width = 300, Height = 300, ShowInTaskbar = false, Title = "普通窗口" };
        ShowAndPump(other);
        ShowAndPump(card);
        try
        {
            var cardHandle = new WindowInteropHelper(card).Handle;
            var otherHandle = new WindowInteropHelper(other).Handle;
            Assert(IsAbove(otherHandle, cardHandle), "卡片显示后不应压在已打开的普通窗口上面");

            card.Activate();
            Pump();
            Assert(IsAbove(cardHandle, otherHandle), "点击卡片时卡片应到前面以便操作");
            other.Activate();
            Pump();
            Assert(IsAbove(otherHandle, cardHandle), "切换到其他窗口后卡片应回到下方");
        }
        finally
        {
            Close(card);
            other.Close();
            Pump();
        }
    }

    // True when `upper` is earlier than `lower` in the top-down window order.
    private static bool IsAbove(nint upper, nint lower)
    {
        for (var window = GetWindow(upper, GwHwndNext); window != 0; window = GetWindow(window, GwHwndNext))
        {
            if (window == lower)
            {
                return true;
            }
        }
        return false;
    }

    private static void TestFileBoxTilesFillRow()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-tiles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var paths = Enumerable.Range(1, 6).Select(index =>
        {
            var path = Path.Combine(temp, $"文件{index}.txt");
            File.WriteAllText(path, "冰蓝");
            return path;
        }).ToArray();
        var state = new FileBoxState();
        FileMappingService.AddExisting(state, paths);
        var window = new FileBoxWindow(state);
        var list = Require<ItemsControl>(window, "FileList");
        ShowAndPump(window);
        try
        {
            PumpUntil(
                () => Enumerable.Range(0, 6).All(index => list.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement),
                "分组盒图标容器未生成");
            var tiles = Enumerable.Range(0, 6)
                .Select(index => (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(index)!)
                .ToArray();
            var lefts = tiles.Select(tile => Math.Round(tile.TranslatePoint(new Point(), list).X)).ToArray();
            var columns = lefts.Distinct().Count();
            var rowRight = tiles.Max(tile => tile.TranslatePoint(new Point(tile.ActualWidth, 0), list).X);
            Assert(columns == 4, $"默认宽度的分组盒应排成 4 列，实际 {columns} 列");
            Assert(
                Math.Abs(rowRight - list.ActualWidth) < 1d,
                $"整行图标应填满宽度，右侧留白 {list.ActualWidth - rowRight:0.#} DIP");

            window.Width = 300;
            Pump();
            var narrowColumns = tiles
                .Select(tile => Math.Round(tile.TranslatePoint(new Point(), list).X))
                .Distinct()
                .Count();
            Assert(narrowColumns == 3, $"变窄后应改为 3 列，实际 {narrowColumns} 列");

            window.Height = 320;
            Pump();
            window.SetCollapsed(true);
            Pump();
            Assert(state.IsCollapsed && Math.Abs(window.ActualHeight - 72) < 1 && !list.IsVisible,
                "折叠后分组盒只剩标题条");
            window.SetCollapsed(false);
            Pump();
            Assert(!state.IsCollapsed && Math.Abs(window.ActualHeight - 320) < 1 && list.IsVisible,
                "展开后恢复折叠前的高度");
        }
        finally
        {
            Close(window);
            Directory.Delete(temp, true);
        }
    }

    private static void TestNewCardKeepsAppBrushes()
    {
        var resources = System.Windows.Application.Current.Resources;
        var original = resources["DockSurfaceBrush"];
        var custom = new SolidColorBrush(Color.FromArgb(0x2B, 0x12, 0x34, 0x56));
        resources["DockSurfaceBrush"] = custom;
        var card = new TodoWidgetWindow(new TodoWidgetState());
        try
        {
            Assert(ReferenceEquals(resources["DockSurfaceBrush"], custom),
                "新建卡片后 Dock 底色不应被重置为默认");
        }
        finally
        {
            card.CanClose = true;
            card.Close();
            resources["DockSurfaceBrush"] = original;
        }
    }

    private static void TestQuickPlaces()
    {
        var window = new QuickPlacesWindow();
        ShowAndPump(window);
        try
        {
            var names = FindVisualChildren<Button>(window)
                .Select(button => System.Windows.Automation.AutomationProperties.GetName(button))
                .ToArray();
            Assert(names.SequenceEqual(["此电脑", "桌面", "文档", "下载", "图片", "回收站"]),
                $"快捷入口应依次是六个系统位置，实际：{string.Join("、", names)}");
            var component = DesktopExperienceRules.CreateDefaultComponent(DesktopComponentKind.QuickLaunch);
            component.FontScale = 1.5;
            component.Appearance.TextColor = "#FF8800";
            window.ApplyComponent(component);
            Pump();
            var first = FindVisualChildren<Button>(window).First();
            Assert(Math.Abs(first.FontSize - 33) < 0.01
                && first.Foreground is SolidColorBrush { Color: { R: 0xFF, G: 0x88, B: 0x00 } },
                "快捷入口图标应跟随组件的字号缩放和文字颜色");

            double ShownWidth() => first.TransformToAncestor(window)
                .TransformBounds(new Rect(0, 0, first.ActualWidth, first.ActualHeight)).Width;
            window.Width = 300;
            window.Height = 60;
            Pump();
            var small = ShownWidth();
            window.Width = 600;
            window.Height = 120;
            Pump();
            Assert(ShownWidth() > small * 1.6, "放大卡片后图标应跟着变大");

            window.SetItems(
            [
                new QuickPlaceState { Icon = "music", Name = "网易云音乐", Target = @"C:\Apps\cloudmusic.exe" },
                new QuickPlaceState { Icon = "downloads", Name = "下载", Target = @"F:\Downloads" }
            ]);
            Pump();
            var custom = FindVisualChildren<Button>(window).ToArray();
            Assert(custom.Length == 2
                && System.Windows.Automation.AutomationProperties.GetName(custom[0]) == "网易云音乐"
                && Equals(custom[1].Tag, @"F:\Downloads")
                && Math.Abs(custom[0].FontSize - 33) < 0.01,
                "快捷入口应按自定义列表显示，并保留字号缩放");
            Assert(QuickPlacesWindow.Resolve("known:downloads") is { Length: > 0 }
                && QuickPlacesWindow.Resolve(@"F:\Downloads") == @"F:\Downloads",
                "系统文件夹在点击时查找，自定义路径原样打开");
        }
        finally
        {
            Close(window);
        }
    }

    private static void TestCardLeavesReservedEdge()
    {
        var area = SystemParameters.WorkArea;
        if (area.Bottom >= SystemParameters.PrimaryScreenHeight - 0.5d)
        {
            Console.WriteLine("信息：主显示器底边没有被任务栏或 Dock 占用，跳过占用边缘检查");
            return;
        }

        var state = new TodoWidgetState
        {
            Placement = new WindowPlacement { Left = area.Left + 200, Top = area.Top + 200, Width = 300, Height = 240 }
        };
        var window = new TodoWidgetWindow(state);
        ShowAndPump(window);
        try
        {
            window.Top = area.Bottom - 100d;
            Pump();
            Assert(window.Top + window.ActualHeight > area.Bottom + 1d, "测试卡片没有进入被占用的屏幕边缘");

            var handle = new WindowInteropHelper(window).Handle;
            SendMessage(handle, WmSettingChange, SpiSetWorkArea, 0);
            Pump();
            Assert(
                window.Top + window.ActualHeight <= area.Bottom + 1d,
                $"工作区变化后卡片仍被任务栏或 Dock 盖住：底边 {window.Top + window.ActualHeight}，工作区底边 {area.Bottom}");
            Assert(Near(window.Left, area.Left + 200d), "卡片让出底边时不应水平移动");
        }
        finally
        {
            Close(window);
        }
    }

    private static void TestWindowPersistence()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-ui-{Guid.NewGuid():N}");
        var state = new AppState
        {
            TodoWidgets = [new TodoWidgetState()],
            FileBoxes = []
        };
        var window = new TodoWidgetWindow(state.TodoWidgets[0]);
        ShowAndPump(window);
        try
        {
            window.Left = 222;
            window.Top = 177;
            window.Width = 333;
            window.Height = 299;
            window.WidgetCornerRadius = 24;
            window.SetWidgetBackgroundColor("#D4E5F6");
            window.SetWidgetBackgroundOpacity(0.41d);
            window.SetWidgetHeaderColor("#8293A4");
            window.SetWidgetHeaderOpacity(0.86d);
            window.SetWidgetTextColor("#FFFFFF");
            window.SetWidgetTitleFontFamily("Segoe UI");
            window.SetWidgetTitleFontBold(false);
            window.SetWidgetTitleFontItalic(true);
            Pump();
            window.CaptureState();
            new LocalStateStore(temp).Save(state);
        }
        finally
        {
            Close(window);
        }

        try
        {
            var loaded = new LocalStateStore(temp).Load().TodoWidgets.Single();
            Assert(Near(loaded.Placement.Left, 222) && Near(loaded.Placement.Top, 177), "位置未重载");
            Assert(Near(loaded.Placement.Width, 333) && Near(loaded.Placement.Height, 299), "尺寸未重载");
            Assert(Near(loaded.CornerRadius, 24), "圆角未重载");
            Assert(loaded.Appearance.BackgroundColor == "#D4E5F6" &&
                   loaded.Appearance.BackgroundOpacity == 0.41d &&
                   loaded.Appearance.HeaderColor == "#8293A4" &&
                   loaded.Appearance.HeaderOpacity == 0.86d &&
                   loaded.Appearance.TextColor == "#FFFFFF" &&
                   loaded.Appearance.TitleFontFamily == "Segoe UI" &&
                   !loaded.Appearance.TitleFontBold &&
                   loaded.Appearance.TitleFontItalic,
                "材质外观未重载");

            var restoredWindow = new TodoWidgetWindow(loaded);
            ShowAndPump(restoredWindow);
            try
            {
                Assert(Near(restoredWindow.WidgetCornerRadius, 24), "重建窗口未应用保存的圆角");
                AssertAppearanceBrush(
                    restoredWindow.WidgetBackgroundBrush,
                    Color.FromArgb(105, 212, 229, 246),
                    SystemColors.WindowColor,
                    "重建窗口主体材质");
                AssertAppearanceBrush(
                    restoredWindow.WidgetTextBrush,
                    Color.FromArgb(255, 255, 255, 255),
                    SystemColors.WindowTextColor,
                    "重建窗口卡片文字颜色");
                var restoredTitle = Require<TextBox>(restoredWindow, "TitleEditor");
                Assert(
                    restoredTitle.FontFamily.Source == "Segoe UI" &&
                    restoredTitle.FontWeight == FontWeights.Normal &&
                    restoredTitle.FontStyle == FontStyles.Italic,
                    "重建窗口未应用保存的标题字体");
            }
            finally
            {
                Close(restoredWindow);
            }
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    private static void TestTaskbarXamlRecovery()
    {
        var unrelated = "Local\\BingLan.Unrelated." + Guid.NewGuid().ToString("N");
        using (var mapping = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew(unrelated, 32))
        using (var view = mapping.CreateViewAccessor())
        {
            view.Write(8, 123);
            Assert(!TaskbarXamlSession.Restore(unrelated), "恢复记录不得写入其他映射");
            Assert(view.ReadInt32(8) == 123, "其他映射内容应保持不变");
        }
        Assert(!TaskbarXamlSession.IsValidMappingName(TaskbarXamlSession.MappingPrefix + "not-a-guid"),
            "畸形会话名称必须拒绝");
        var missing = TaskbarXamlSession.MappingPrefix + Guid.NewGuid().ToString("N");
        Assert(TaskbarXamlSession.Restore(missing), "Explorer 已退出或会话已释放时无需再次修改");
        using var session = new TaskbarXamlSession(Path.GetTempPath());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var attempt = session.StartAsync(1, cancellation.Token);
        Pump();
        Assert(attempt.IsCanceled, "已取消的启用请求不得加载 Explorer 组件");
        Assert(!session.IsActive, "取消后不能报告透明已启用");
    }

    private static void TestTaskbarRevealEdgeGuard()
    {
        var monitor = new TaskbarEdgeGuard.PixelRect
        {
            Left = 100,
            Top = -50,
            Right = 2660,
            Bottom = 1390
        };
        var moving = new TaskbarEdgeGuard.PixelRect
        {
            Left = 400,
            Top = 1090,
            Right = 710,
            Bottom = 1390
        };
        var originalWidth = moving.Width;
        var originalHeight = moving.Height;
        Assert(
            TaskbarEdgeGuard.ConstrainToAutoHideEdges(
                ref moving,
                monitor,
                TaskbarEdgeGuard.AutoHideEdges.Bottom),
            "覆盖自动隐藏底边时应修正组件位置");
        Assert(
            moving.Bottom == monitor.Bottom - TaskbarEdgeGuard.RevealInsetPixels,
            $"底部任务栏唤出带未保留：Bottom={moving.Bottom}");
        Assert(
            moving.Width == originalWidth && moving.Height == originalHeight,
            "任务栏边缘保护不应改变组件尺寸");

        var unguarded = new TaskbarEdgeGuard.PixelRect
        {
            Left = 400,
            Top = 1090,
            Right = 710,
            Bottom = 1390
        };
        Assert(
            !TaskbarEdgeGuard.ConstrainToAutoHideEdges(
                ref unguarded,
                monitor,
                TaskbarEdgeGuard.AutoHideEdges.None),
            "未启用自动隐藏时不应擅自移动组件");
        Assert(unguarded.Bottom == monitor.Bottom, "未启用的边缘不应被改写");

        var outsideMonitor = new TaskbarEdgeGuard.PixelRect
        {
            Left = 400,
            Top = 1410,
            Right = 710,
            Bottom = 1710
        };
        Assert(
            !TaskbarEdgeGuard.ConstrainToAutoHideEdges(
                ref outsideMonitor,
                monitor,
                TaskbarEdgeGuard.AutoHideEdges.Bottom),
            "完全位于显示器之外的窗口不应被吸附到任务栏边缘");

        var state = new TodoWidgetState
        {
            Placement = new WindowPlacement { Left = 150, Top = 120, Width = 310, Height = 300 }
        };
        var window = new TodoWidgetWindow(state);
        ShowAndPump(window);
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            var taskbarStateBefore = GetTaskbarState();
            _ = TaskbarEdgeGuard.EnsureWindowBounds(handle);
            Assert(
                GetTaskbarState() == taskbarStateBefore,
                "组件边缘保护不应修改 Windows 自动隐藏状态");
        }
        finally
        {
            Close(window);
        }
    }

    private static void TestApplyPlacementDuringCapture()
    {
        var state = new TodoWidgetState();
        var window = new TodoWidgetWindow(state);
        ShowAndPump(window);
        try
        {
            window.WidgetChanged += changed => ((TodoWidgetWindow)changed).CaptureState();
            state.Placement = new WindowPlacement
            {
                Left = 144,
                Top = 108,
                Width = 420,
                Height = 390
            };

            window.ApplyPlacement(state.Placement, false);
            Pump();

            Assert(Near(window.Width, 420) && Near(window.Height, 390),
                "同步位置捕获不应覆盖尚未应用的预设宽高");
        }
        finally
        {
            Close(window);
        }
    }

    private static void TestSingleInstanceGate()
    {
        var name = $"Local\\BingLan.UiSmokeTests.{Guid.NewGuid():N}";
        Assert(
            SingleInstanceGate.TryAcquire(name, out var first) && first is not null,
            "首次启动未取得单实例门禁");
        using (var firstGate = first ?? throw new InvalidOperationException("单实例门禁为空"))
        {
            Assert(
                !SingleInstanceGate.TryAcquire(name, out var duplicate) && duplicate is null,
                "重复启动仍取得同名单实例门禁");
        }

        Assert(
            SingleInstanceGate.TryAcquire(name, out var restarted) && restarted is not null,
            "原实例退出后无法重新取得单实例门禁");
        (restarted ?? throw new InvalidOperationException("重启门禁为空")).Dispose();

        Assert(
            SingleInstanceGate.TryAcquire(name + ".Production", out var production) &&
            production is not null,
            "正式模式未取得独立门禁");
        using (var productionGate = production ??
               throw new InvalidOperationException("正式模式门禁为空"))
        {
            Assert(
                SingleInstanceGate.TryAcquire(name + ".InteractiveQa", out var interactiveQa) &&
                interactiveQa is not null,
                "交互测试模式未取得独立门禁");
            (interactiveQa ?? throw new InvalidOperationException("交互测试门禁为空")).Dispose();
        }
    }

    // 图标与列表是同一份映射的两种显示：切换只换模板，不增删映射、不触碰原文件。
    private static void TestFileBoxViewModeSwitch()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-view-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var fileA = Path.Combine(temp, "文档甲.txt");
        var fileB = Path.Combine(temp, "文档乙.txt");
        File.WriteAllText(fileA, "a");
        File.WriteAllText(fileB, "b");
        var state = new FileBoxState();
        FileMappingService.AddExisting(state, [fileA, fileB]);
        var window = new FileBoxWindow(state);
        var list = Require<ItemsControl>(window, "FileList");
        Assert(
            Equals(list.ItemTemplate, window.TryFindResource("FileTileTemplate")),
            "新分组盒默认使用图标视图");
        ShowAndPump(window);
        try
        {
            window.SetViewMode(FileBoxViewMode.List);
            Pump();
            Assert(
                Equals(list.ItemTemplate, window.TryFindResource("FileRowTemplate")),
                "切换后使用列表模板");
            Assert(list.Items.Count == 2, "列表视图仍显示全部映射");
            var rows = FindVisualChildren<Button>(window)
                .Where(button => button.DataContext is FileBoxWindow.FileTile)
                .ToList();
            Assert(rows.Count == 2, "列表视图为每个映射渲染一行");
            Assert(
                rows.All(row => row.ActualHeight is > 0d and <= 40d),
                "列表行是紧凑的单行高度");
            Assert(
                rows.All(row => Equals(System.Windows.Automation.AutomationProperties.GetName(row), "文档甲.txt")
                    || Equals(System.Windows.Automation.AutomationProperties.GetName(row), "文档乙.txt")),
                "列表行保留无障碍名称");
            Assert(state.ViewMode == FileBoxViewMode.List, "视图模式写入分组盒状态");
            Assert(File.Exists(fileA) && File.Exists(fileB), "切换视图不触碰原文件");

            window.SetViewMode(FileBoxViewMode.Tiles);
            Pump();
            Assert(
                Equals(list.ItemTemplate, window.TryFindResource("FileTileTemplate")),
                "可以切回图标视图");
            Assert(list.Items.Count == 2, "切回图标后映射不丢失");
        }
        finally
        {
            window.CanClose = true;
            window.Close();
            Directory.Delete(temp, recursive: true);
        }
    }

    private static void TestFileBoxWindow()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var file = Path.Combine(temp, "映射示例.txt");
        var shortcut = Path.Combine(temp, "快捷方式.lnk");
        var folder = Path.Combine(temp, "资料");
        File.WriteAllText(file, "冰蓝桌面");
        File.WriteAllText(shortcut, "快捷方式占位内容");
        Directory.CreateDirectory(folder);
        var state = new FileBoxState { CornerRadius = 3 };
        FileMappingService.AddExisting(state, [file, shortcut, folder]);
        var backgroundTile = Task.Run(
                () => new FileBoxWindow.FileTile(state.Items.Single(item => item.Path == file)))
            .GetAwaiter()
            .GetResult();
        Assert(
            backgroundTile.Icon is null || backgroundTile.Icon.IsFrozen,
            "后台提取的桌面项目图标未冻结，不能安全返回 UI 线程");
        var window = new FileBoxWindow(state);
        var list = Require<ItemsControl>(window, "FileList");
        Assert(
            list.Items.Cast<FileBoxWindow.FileTile>().All(tile => tile.Icon is null),
            "文件盒构造阶段仍在 UI 线程同步提取持久化图标");
        ShowAndPump(window);
        try
        {
            PumpUntil(
                () => list.Items.Cast<FileBoxWindow.FileTile>().Any(tile => tile.Icon is not null),
                "窗口 Loaded 后没有完成后台图标回填");
            Assert(
                list.Items.Cast<FileBoxWindow.FileTile>()
                    .All(tile => tile.Icon is null || tile.Icon.IsFrozen),
                "回填到 UI 的图标没有冻结");
            Assert(list.Items.Count == 3, "文件映射未显示为网格项");
            Assert(File.Exists(file), "创建 UI 映射不应移动原文件");
            Assert(
                window.ContextMenu.Items.OfType<MenuItem>()
                    .Select(item => item.Header?.ToString())
                    .SequenceEqual(["在冰蓝桌面中设置…", "锁定位置和大小", "删除此组件", "退出冰蓝桌面"]),
                "组件右键菜单仍承载桌面内设置或创建入口");
            var buttons = FindVisualChildren<Button>(window).ToList();
            var importButton = buttons.Single(
                button => Equals(button.ToolTip, "添加文件、文件夹或桌面项目"));
            Assert(Equals(importButton.Content, "+"), "桌面分组盒缺少可见导入入口");
            importButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            var addItems = importButton.ContextMenu?.Items.OfType<MenuItem>().ToList() ?? [];
            Assert(
                addItems.Select(item => item.Header?.ToString()).SequenceEqual(
                    ["选择文件…", "选择文件夹…", "导入桌面项目（用户与公共桌面）", "添加系统入口"]),
                "桌面分组盒加号没有打开文件、文件夹、桌面导入和系统入口菜单");
            importButton.ContextMenu!.IsOpen = false;
            var organizeRequested = false;
            window.OrganizeDesktopRequested += () => organizeRequested = true;
            var autoOrganizeButton = buttons.Single(
                button => Equals(button.Content, "自动整理"));
            autoOrganizeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(organizeRequested, "可见自动整理按钮没有发出桌面归类请求");
            var sortButton = buttons.Single(button => Equals(button.ToolTip, "排序映射"));
            var sortItems = sortButton.ContextMenu?.Items.OfType<MenuItem>().ToList() ?? [];
            Assert(
                sortItems.Select(item => item.Header?.ToString())
                    .SequenceEqual(["手动调整（保留当前顺序）", "按名称", "按类型"]),
                "桌面分组盒缺少手动、名称或类型排序入口");
            sortItems.Single(item => Equals(item.Header, "按名称"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump();
            Assert(
                state.Items.OrderBy(item => item.Order).Select(item => Path.GetFileName(item.Path))
                    .SequenceEqual(["快捷方式.lnk", "映射示例.txt", "资料"]),
                "名称排序入口未更新映射顺序");
            Assert(
                Require<TextBlock>(window, "CountText").Text == "3 项",
                "桌面分组盒没有明确提示仅保存映射");
            var presenter = list.ItemContainerGenerator.ContainerFromIndex(0) as ContentPresenter ??
                throw new InvalidOperationException("文件项容器没有生成");
            var tile = FindVisualChildren<Border>(presenter)
                .Single(x => Equals(x.Tag, "Interactive"));
            Assert(tile.ActualWidth >= 70d && Near(tile.Height, 92d),
                "文件网格没有采用 Pogget 单元尺寸");
            Assert(tile.Background == Brushes.Transparent,
                "文件项仍叠加常驻白色卡片");
            Assert(
                tile.CornerRadius == window.ItemCornerRadius &&
                Near(tile.CornerRadius.TopLeft, 3d * 10d / 15.5d),
                "文件卡片没有随外圆角同步变化");
            var icon = FindVisualChildren<Image>(presenter).Single();
            Assert(Near(icon.Width, 43d) && Near(icon.Height, 43d),
                "文件图标没有采用 Pogget 43 DIP 尺寸");
            Assert(window.WidgetCornerRadius == 3, "文件盒没有应用自己的圆角状态");
            Assert(
                Require<Border>(window, "Surface").CornerRadius == new CornerRadius(3),
                "文件盒描边没有复用宿主圆角");
            Assert(
                Near(
                    Require<Border>(window, "TitleBarSurface").CornerRadius.TopLeft,
                    3d * 9.2d / 15.5d),
                "文件盒标题栏没有随外圆角同步变化");
            AssertAppearanceBrush(
                window.WidgetBackgroundBrush,
                Color.FromArgb(82, 255, 255, 255),
                SystemColors.WindowColor,
                "文件盒默认主体材质");
            AssertAppearanceBrush(
                window.WidgetHeaderBrush,
                Color.FromArgb(247, 160, 180, 225),
                SystemColors.ControlColor,
                "文件盒默认标题材质");
            var title = Require<TextBox>(window, "TitleEditor");
            Assert(
                title.FontFamily.Source == WidgetAppearanceRules.DefaultTitleFontFamily &&
                title.FontWeight == FontWeights.Bold &&
                title.FontStyle == FontStyles.Normal,
                "文件盒没有复用可自定义标题字体");
            var liveCapturePath = Environment.GetEnvironmentVariable("BINGLAN_UI_FILEBOX_CAPTURE");
            if (!string.IsNullOrWhiteSpace(liveCapturePath))
            {
                SaveLivePreview(window, liveCapturePath);
            }
            SavePreview(window, Path.Combine(Path.GetTempPath(), "BingLan-FileBoxPreview.png"));
        }
        finally
        {
            Close(window);
            Directory.Delete(temp, true);
        }
    }

    private static void TestFileBoxInvalidStateAndSystemEntries()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-file-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var validFile = Path.Combine(temp, "有效文件.txt");
        var missingFile = Path.Combine(temp, "已删除文件.txt");
        File.WriteAllText(validFile, "冰蓝桌面");
        File.WriteAllText(missingFile, "占位");
        var state = new FileBoxState();
        FileMappingService.AddExisting(state, [missingFile, validFile]);
        File.Delete(missingFile);

        var window = new FileBoxWindow(state);
        var list = Require<ItemsControl>(window, "FileList");
        ShowAndPump(window);
        try
        {
            PumpUntil(
                () => list.ItemContainerGenerator.ContainerFromIndex(0) is ContentPresenter &&
                    list.ItemContainerGenerator.ContainerFromIndex(1) is ContentPresenter,
                "文件盒列表容器未生成");

            var missingPresenter = (ContentPresenter)list.ItemContainerGenerator.ContainerFromIndex(0)!;
            var missingLabel = FindVisualChildren<TextBlock>(missingPresenter)
                .Single(tb => Equals(tb.Text, "已失效"));
            var missingIcon = FindVisualChildren<Image>(missingPresenter).Single();
            Assert(missingLabel.Visibility == Visibility.Visible, "已失效标签在缺失映射上没有显示");
            Assert(missingIcon.Opacity < 1d, "缺失映射的图标没有变暗");
            Assert(
                ((FileBoxWindow.FileTile)list.Items[0]!).IsMissing,
                "已删除路径没有在加载时标记为失效");

            var validPresenter = (ContentPresenter)list.ItemContainerGenerator.ContainerFromIndex(1)!;
            var validLabel = FindVisualChildren<TextBlock>(validPresenter)
                .Single(tb => Equals(tb.Text, "已失效"));
            var validIcon = FindVisualChildren<Image>(validPresenter).Single();
            Assert(validLabel.Visibility == Visibility.Collapsed, "存在的映射不应显示已失效标签");
            Assert(validIcon.Opacity == 1d, "存在的映射不应变暗图标");
            Assert(
                !((FileBoxWindow.FileTile)list.Items[1]!).IsMissing,
                "存在的路径被错误标记为失效");

            var missingButton = FindVisualChildren<Button>(missingPresenter)
                .Single(button => button.DataContext is FileBoxWindow.FileTile);
            var missingMenu = missingButton.ContextMenu ?? throw new InvalidOperationException("找不到文件项右键菜单");
            missingMenu.DataContext = missingButton.DataContext;
            InvokeContextMenuOpened(window, missingMenu);
            var missingMenuHeaders = missingMenu.Items.OfType<MenuItem>()
                .Select(item => item.Header?.ToString()).ToList();
            Assert(
                missingMenuHeaders.SequenceEqual(
                    ["打开", "打开所在位置", "刷新图标", "上移", "下移", "修改显示名称…", "转移到其他分组盒", "重新定位…", "移除映射"]),
                "文件项右键菜单缺少打开所在位置、刷新图标、显示名称、转移或重新定位入口");
            Assert(
                missingMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "转移到其他分组盒"))
                    .Visibility == Visibility.Collapsed,
                "只有一个分组盒时不应显示转移入口");
            Assert(
                missingMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "打开所在位置"))
                    .Visibility == Visibility.Visible,
                "普通映射不应隐藏打开所在位置");
            Assert(
                missingMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "重新定位…"))
                    .Visibility == Visibility.Visible,
                "失效映射应提供重新定位入口");

            var buttons = FindVisualChildren<Button>(window).ToList();
            var addButton = buttons.Single(
                button => Equals(button.ToolTip, "添加文件、文件夹或桌面项目"));
            addButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            var systemEntryMenu = addButton.ContextMenu!.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, "添加系统入口"));
            Assert(
                systemEntryMenu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString())
                    .SequenceEqual(["此电脑", "回收站", "下载", "桌面"]),
                "系统入口子菜单没有提供此电脑、回收站、下载和桌面");
            var recycleBinItem = systemEntryMenu.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, "回收站"));
            recycleBinItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            addButton.ContextMenu!.IsOpen = false;
            PumpUntil(
                () => list.Items.Cast<FileBoxWindow.FileTile>().Any(tile => tile.Name == "回收站"),
                "系统入口没有加入分组盒");

            var recycleTile = list.Items.Cast<FileBoxWindow.FileTile>().Single(tile => tile.Name == "回收站");
            Assert(recycleTile.IsShellEntry, "回收站条目没有识别为系统入口");
            Assert(!recycleTile.IsMissing, "系统入口不应被标记为已失效");

            var recycleIndex = list.Items.IndexOf(recycleTile);
            PumpUntil(
                () => list.ItemContainerGenerator.ContainerFromIndex(recycleIndex) is ContentPresenter,
                "系统入口的容器没有生成");
            var recyclePresenter = (ContentPresenter)list.ItemContainerGenerator.ContainerFromIndex(recycleIndex)!;
            var recycleLabel = FindVisualChildren<TextBlock>(recyclePresenter)
                .Single(tb => Equals(tb.Text, "已失效"));
            Assert(recycleLabel.Visibility == Visibility.Collapsed, "系统入口不应显示已失效标签");

            var recycleButton = FindVisualChildren<Button>(recyclePresenter)
                .Single(button => button.DataContext is FileBoxWindow.FileTile);
            var recycleMenu = recycleButton.ContextMenu ?? throw new InvalidOperationException("系统入口没有右键菜单");
            recycleMenu.DataContext = recycleButton.DataContext;
            InvokeContextMenuOpened(window, recycleMenu);
            Assert(
                recycleMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "打开所在位置"))
                    .Visibility == Visibility.Collapsed,
                "系统入口不应提供打开所在位置");
            Assert(
                recycleMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "重新定位…"))
                    .Visibility == Visibility.Collapsed,
                "系统入口不应提供重新定位");
        }
        finally
        {
            Close(window);
            Directory.Delete(temp, true);
        }
    }

    private static void TestFileMappingWatchClearsDeletedMapping()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-watch-gone-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var file = Path.Combine(temp, "将被删除.txt");
        File.WriteAllText(file, "冰蓝桌面");
        var state = new FileBoxState();
        FileMappingService.AddExisting(state, [file]);
        var window = new FileBoxWindow(state);
        var list = Require<ItemsControl>(window, "FileList");
        ShowAndPump(window);
        var watcher = new FileMappingWatchService(
            () => Task.FromResult(window.RemoveGoneMappings(
                FileMappingService.CollectGoneIds(state.Items))),
            () => Task.CompletedTask);
        watcher.UpdateWatchedDirectories(
            FileMappingService.CollectExistingParentDirectories(state.Items.Select(item => item.Path)));
        try
        {
            Assert(list.Items.Count == 1, "测试开始时分组盒应有一项映射");
            File.Delete(file);
            PumpUntil(() => list.Items.Count == 0, "原文件被删除后映射没有自动移除");
            Assert(state.Items.Count == 0, "自动清除后状态里不应残留失效映射");
            Assert(!File.Exists(file), "自动清除不应恢复或改动原文件");
        }
        finally
        {
            watcher.Dispose();
            Close(window);
            Directory.Delete(temp, true);
        }
    }

    private static void TestFileBoxAutomationToggle()
    {
        var automation = false;
        var applied = new List<bool>();
        var window = new SettingsWindow(
            new CityLookupService(),
            new InformationWidgetState(),
            DesktopExperienceRules.CreateDefault(),
            (_, _) => { },
            _ => { },
            _ => { },
            maintenance: new SettingsMaintenance
            {
                IsFileBoxAutomationEnabled = () => automation,
                SetFileBoxAutomation = value =>
                {
                    automation = value;
                    applied.Add(value);
                }
            });
        ShowAndPump(window);
        try
        {
            var toggle = Require<CheckBox>(window, "FileBoxAutomationCheckBox");
            Assert(toggle.IsChecked == false && applied.Count == 0, "分组盒自动刷新应默认关闭且加载时不写回");
            toggle.IsChecked = true;
            Pump();
            Assert(applied.SequenceEqual([true]) && automation, "勾选后应开启分组盒自动刷新");
            toggle.IsChecked = false;
            Pump();
            Assert(applied.SequenceEqual([true, false]) && !automation, "取消勾选后应关闭分组盒自动刷新");
        }
        finally
        {
            window.Close();
        }
    }

    private static void TestFileMappingWatchImportTrigger()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-watch-import-{Guid.NewGuid():N}");
        var importDir = Path.Combine(temp, "桌面");
        var otherDir = Path.Combine(temp, "其他目录");
        Directory.CreateDirectory(importDir);
        Directory.CreateDirectory(otherDir);
        var importRequested = 0;
        var watcher = new FileMappingWatchService(
            () => Task.CompletedTask,
            () =>
            {
                importRequested++;
                return Task.CompletedTask;
            });
        watcher.UpdateWatchedDirectories([importDir, otherDir]);
        watcher.UpdateImportDirectories([importDir]);
        try
        {
            File.WriteAllText(Path.Combine(otherDir, "普通.txt"), "x");
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                Pump();
                Thread.Sleep(10);
            }
            Assert(importRequested == 0, "非桌面目录的新文件不应触发自动收纳");

            File.WriteAllText(Path.Combine(importDir, "新桌面文件.txt"), "x");
            PumpUntil(() => importRequested > 0, "桌面目录的新文件没有触发自动收纳");
        }
        finally
        {
            watcher.Dispose();
            Directory.Delete(temp, true);
        }
    }

    private static void TestFileBoxRenameAndTransfer()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-file-transfer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var file = Path.Combine(temp, "季度报告.txt");
        File.WriteAllText(file, "冰蓝桌面");
        var sourceState = new FileBoxState();
        FileMappingService.AddExisting(sourceState, [file]);
        var source = new FileBoxWindow(sourceState);
        var target = new FileBoxWindow(new FileBoxState
        {
            Title = "工作",
            Placement = new WindowPlacement { Left = 860, Top = 92, Width = 366, Height = 306 }
        });
        source.OtherBoxes = () => [target];
        var sourceList = Require<ItemsControl>(source, "FileList");
        var targetList = Require<ItemsControl>(target, "FileList");
        ShowAndPump(source);
        ShowAndPump(target);
        try
        {
            source.RenameMapping((FileBoxWindow.FileTile)sourceList.Items[0]!, "  Q3 报告  ");
            PumpUntil(
                () => sourceList.ItemContainerGenerator.ContainerFromIndex(0) is ContentPresenter,
                "改名后的文件项容器未生成");
            var tile = (FileBoxWindow.FileTile)sourceList.Items[0]!;
            Assert(
                tile.Name == "Q3 报告" && sourceState.Items[0].Title == "Q3 报告" && tile.Path == file,
                "显示名称没有写入映射或改变了路径");
            Assert(File.Exists(file), "修改显示名称不得重命名原文件");

            var presenter = (ContentPresenter)sourceList.ItemContainerGenerator.ContainerFromIndex(0)!;
            var button = FindVisualChildren<Button>(presenter)
                .Single(candidate => candidate.DataContext is FileBoxWindow.FileTile);
            var menu = button.ContextMenu ?? throw new InvalidOperationException("找不到文件项右键菜单");
            menu.DataContext = tile;
            InvokeContextMenuOpened(source, menu);
            var transfer = menu.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, "转移到其他分组盒"));
            var destination = transfer.Items.OfType<MenuItem>().Single();
            Assert(
                transfer.Visibility == Visibility.Visible && Equals(destination.Header, "工作"),
                "转移菜单没有列出另一个分组盒");
            destination.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump();

            Assert(
                sourceList.Items.Count == 0 && source.State.Items.Count == 0,
                "转移后原分组盒仍保留映射");
            Assert(
                targetList.Items.Count == 1 &&
                ((FileBoxWindow.FileTile)targetList.Items[0]!).Name == "Q3 报告" &&
                target.State.Items.Single().Path == file,
                "转移后目标分组盒没有得到带显示名称的映射");
            Assert(File.Exists(file), "转移映射不得移动原文件");
        }
        finally
        {
            Close(source);
            Close(target);
            Directory.Delete(temp, true);
        }
    }

    private static void InvokeContextMenuOpened(FileBoxWindow window, ContextMenu menu)
    {
        var method = typeof(FileBoxWindow).GetMethod(
            "FileTileContextMenu_Opened",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("找不到右键菜单打开处理方法");
        method.Invoke(window, [menu, new RoutedEventArgs()]);
    }

    private static void TestTrayDoubleClick()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-tray-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var coordinator = new WidgetCoordinator(interactiveQa: true, dataDirectory: temp);
        try
        {
            var trayIcon = typeof(WidgetCoordinator)
                .GetField("_trayIcon", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)
                ?.GetValue(coordinator) as System.Windows.Forms.NotifyIcon ??
                throw new InvalidOperationException("托盘图标没有创建");
            var raiseDoubleClick = typeof(System.Windows.Forms.NotifyIcon)
                .GetMethod("OnDoubleClick", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic) ??
                throw new InvalidOperationException("无法触发托盘双击测试");

            raiseDoubleClick.Invoke(trayIcon, [EventArgs.Empty]);
            Pump();
            var settings = Application.Current.Windows
                .OfType<SettingsWindow>()
                .SingleOrDefault(window => window.IsVisible) ??
                throw new InvalidOperationException("托盘双击没有打开冰蓝桌面设置");

            settings.WindowState = WindowState.Minimized;
            Pump();
            raiseDoubleClick.Invoke(trayIcon, [EventArgs.Empty]);
            Pump();
            Assert(
                settings.IsVisible && settings.WindowState == WindowState.Normal,
                "托盘双击没有恢复已最小化的设置窗口");

            var navigation = Require<ListBox>(settings, "SettingsNavigationList");
            navigation.SelectedIndex = 2;
            Pump();
            Assert(
                Require<FrameworkElement>(settings, "DockPage").IsVisible,
                "Dock 设置页没有显示");
            Assert(
                Require<CheckBox>(settings, "DockEnabledCheckBox").IsChecked == false,
                "冰蓝 Dock 默认应保持关闭");

            navigation.SelectedIndex = 3;
            Pump();
            Assert(
                Require<FrameworkElement>(settings, "TopBarPage").IsVisible &&
                Require<CheckBox>(settings, "TopBarEnabledCheckBox").IsChecked == false,
                "顶端信息条页没有显示或默认没有保持关闭");

            navigation.SelectedIndex = 4;
            Pump();
            Assert(
                Require<FrameworkElement>(settings, "TaskbarPage").IsVisible &&
                Require<RadioButton>(settings, "TaskbarDefaultRadio").IsChecked == true,
                "任务栏设置页没有显示或默认没有保持系统默认");

            navigation.SelectedIndex = 5;
            Pump();
            Assert(
                Require<FrameworkElement>(settings, "GeneralPage").IsVisible &&
                Require<ListBox>(settings, "BackupList").IsVisible &&
                !Require<CheckBox>(settings, "StartupCheckBox").IsEnabled,
                "通用页没有显示备份，或测试模式下仍允许修改开机启动");

            var settingsCapturePath =
                Environment.GetEnvironmentVariable("BINGLAN_UI_SETTINGS_CAPTURE");
            if (!string.IsNullOrWhiteSpace(settingsCapturePath))
            {
                navigation.SelectedIndex =
                    Environment.GetEnvironmentVariable("BINGLAN_UI_SETTINGS_PAGE") switch
                    {
                        "Components" => 1,
                        "Dock" => 2,
                        _ => 0
                    };
                Pump();
                PrepareSettingsCapture(settings);
                SaveLivePreview(settings, settingsCapturePath);

                var settingsRenderCapturePath =
                    Environment.GetEnvironmentVariable("BINGLAN_UI_SETTINGS_RENDER_CAPTURE");
                if (!string.IsNullOrWhiteSpace(settingsRenderCapturePath))
                {
                    var settingsContent = settings.Content as FrameworkElement ??
                        throw new InvalidOperationException("设置窗口内容不可用于渲染截图");
                    SaveElementPreview(
                        settingsContent,
                        settings.Background,
                        settingsRenderCapturePath);
                }
            }
        }
        finally
        {
            coordinator.Dispose();
            Directory.Delete(temp, true);
            Pump();
        }
    }

    private static void TestUnavailableTitleFontFallback()
    {
        var state = new TodoWidgetState();
        state.Appearance.TitleFontFamily = "file:///C:/missing-font.ttf#Missing Font";
        var window = new TodoWidgetWindow(state);
        ShowAndPump(window);
        try
        {
            var installed = Fonts.SystemFontFamilies
                .Select(font => font.Source)
                .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            Assert(installed.Contains(state.Appearance.TitleFontFamily),
                "不存在或 URI 标题字体没有在首次渲染前回退到本机字体");
            Assert(window.WidgetTitleFontFamily.Source == state.Appearance.TitleFontFamily,
                "宿主标题字体与回退后的状态不一致");
            Assert(
                Require<TextBox>(window, "TitleEditor").FontFamily.Source ==
                state.Appearance.TitleFontFamily,
                "可见标题没有应用本机回退字体");
        }
        finally
        {
            Close(window);
        }
    }

    private static void ShowAndPump(Window window)
    {
        window.Show();
        Pump();
        Assert(window.IsVisible && new WindowInteropHelper(window).Handle != 0, "窗口未真实创建");
    }

    private static void PrepareSettingsCapture(Window window)
    {
        window.WindowState = WindowState.Normal;
        window.Width = 1180;
        window.Height = 820;
        window.UpdateLayout();
        window.Left = SystemParameters.WorkArea.Left +
            Math.Max(0, (SystemParameters.WorkArea.Width - window.ActualWidth) / 2d);
        window.Top = SystemParameters.WorkArea.Top +
            Math.Max(0, (SystemParameters.WorkArea.Height - window.ActualHeight) / 2d);
        window.Activate();
        Pump();
    }

    private static void Close(WidgetWindowBase window)
    {
        window.CanClose = true;
        window.Close();
        Pump();
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new DispatcherOperationCallback(value =>
            {
                ((DispatcherFrame)value!).Continue = false;
                return null;
            }),
            frame);
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntil(Func<bool> condition, string failureMessage)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Pump();
            Thread.Sleep(10);
        }
        Assert(condition(), failureMessage);
    }

    private static void TestTodoRowsStayAccessible()
    {
        var state = TodoService.CreateDefaultWidget();
        state.Items[0].Text = "第一条";
        state.Items[1].Text = "第二条";
        var window = new TodoWidgetWindow(state);
        ShowAndPump(window);
        try
        {
            var list = Require<ItemsControl>(window, "TodoList");
            var peer = UIElementAutomationPeer.CreatePeerForElement(list);
            int AccessibleRows() => (peer.GetChildren() ?? [])
                .Count(row => (row.GetChildren() ?? []).Any(child =>
                    child.GetAutomationControlType() == AutomationControlType.Edit && !child.IsOffscreen()));
            Assert(AccessibleRows() == 3, "初始应有三行可访问的待办");

            FindVisualChildren<CheckBox>(list).First().IsChecked = true;
            Pump();
            Assert(AccessibleRows() == 3, "勾选一行后其他行仍应暴露复选框和编辑框");

            var toggle = Require<Button>(window, "CompletedVisibilityButton");
            Assert(Equals(toggle.Content, "隐藏已完成"), "未隐藏时按钮应为“隐藏已完成”");
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(Equals(toggle.Content, "显示已完成") && AccessibleRows() == 2,
                "隐藏后按钮应改为“显示已完成”，其余两行仍可访问");
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(Equals(toggle.Content, "隐藏已完成") && AccessibleRows() == 3, "再次点击应恢复显示");

            Assert(Require<TextBlock>(window, "SummaryText").Text == "1/2 已完成", "统计应只计算写了内容的待办");
        }
        finally
        {
            window.CanClose = true;
            window.Close();
        }
    }

    private static void TestTodoEnterCommitsText()
    {
        var state = new TodoWidgetState();
        TodoService.Add(state, string.Empty);
        var window = new TodoWidgetWindow(state);
        ShowAndPump(window);
        try
        {
            var list = Require<ItemsControl>(window, "TodoList");
            var editor = FindVisualChildren<TextBox>(list).First();
            window.Activate();
            Assert(editor.Focus(), "待办文字框无法聚焦");
            Pump();
            editor.Text = "按 Enter 提交的待办";
            editor.RaiseEvent(new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(editor),
                0,
                Key.Enter)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            });
            Pump();
            window.CaptureState();
            Assert(
                state.Items[0].Text == "按 Enter 提交的待办",
                $"Enter 后待办文字没有写入状态，实际为“{state.Items[0].Text}”");

            Assert(editor.Focus(), "待办文字框无法再次聚焦");
            Pump();
            editor.Text = "不应保留的修改";
            editor.RaiseEvent(new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(editor),
                0,
                Key.Escape)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            });
            Pump();
            window.CaptureState();
            Assert(
                state.Items[0].Text == "按 Enter 提交的待办" && editor.Text == "按 Enter 提交的待办",
                "Esc 应取消本次编辑并恢复原文字");

            var clear = Require<Button>(window, "ClearCompletedButton");
            Assert(!clear.IsEnabled, "没有已完成待办时清理按钮应不可用");
            var check = FindVisualChildren<CheckBox>(list).First();
            check.IsChecked = true;
            Pump();
            Assert(clear.IsEnabled, "有已完成待办时清理按钮应可用");
            clear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            window.CaptureState();
            Assert(state.Items.Count == 0, "清理已完成后应删除已完成的待办");
        }
        finally
        {
            window.CanClose = true;
            window.Close();
        }
    }

    private static void TestTrayToggleDesktop()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-toggle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var coordinator = new WidgetCoordinator(interactiveQa: true, dataDirectory: temp);
        try
        {
            coordinator.Start();
            Pump();
            var widgets = Application.Current.Windows.OfType<WidgetWindowBase>().Where(window => window.IsVisible).ToList();
            Assert(widgets.Count > 0, "启动后应显示桌面组件");
            var trayIcon = typeof(WidgetCoordinator)
                .GetField("_trayIcon", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)
                ?.GetValue(coordinator) as System.Windows.Forms.NotifyIcon ??
                throw new InvalidOperationException("托盘图标没有创建");
            var toggle = trayIcon.ContextMenuStrip!.Items
                .OfType<System.Windows.Forms.ToolStripMenuItem>()
                .Single(item => item.Text == "隐藏桌面组件");

            toggle.PerformClick();
            Pump();
            Assert(widgets.All(window => !window.IsVisible), "隐藏后桌面组件仍可见");
            Assert(toggle.Text == "显示桌面组件", "隐藏后菜单应改为显示桌面组件");

            toggle.PerformClick();
            Pump();
            Assert(widgets.All(window => window.IsVisible), "再次点击应恢复隐藏前可见的组件");
            Assert(toggle.Text == "隐藏桌面组件", "显示后菜单应恢复为隐藏桌面组件");
        }
        finally
        {
            coordinator.Dispose();
            foreach (var window in Application.Current.Windows.OfType<WidgetWindowBase>().ToList())
            {
                window.CanClose = true;
                window.Close();
            }
            Pump();
            Directory.Delete(temp, true);
        }
    }

    // "显示桌面" and "最小化所有窗口" reach a card either as ShowWindow(SW_HIDE) or as
    // ShowWindow(SW_MINIMIZE); on Windows builds that do not skip tool windows the card
    // puts itself back, while hides the app asked for stay in effect.
    private static void TestShellHideAndMinimizeRecovery()
    {
        var state = TodoService.CreateDefaultWidget();
        var window = new TodoWidgetWindow(state)
        {
            Left = 240,
            Top = 240,
            Width = 320,
            Height = 260
        };
        ShowAndPump(window);
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            GetWindowRect(handle, out var before);

            // The shell hides the window behind WPF's back, so the wait must watch the
            // native visibility, not the WPF property.
            ShowWindow(handle, SwHide);
            PumpUntil(() => IsWindowVisible(handle), "Shell 隐藏后组件应自动恢复显示");
            Assert(window.IsVisible, "恢复后 WPF 可见状态应一致");

            ShowWindow(handle, SwMinimize);
            PumpUntil(() => !IsIconic(handle) && IsWindowVisible(handle),
                "Shell 最小化后组件应自动还原");
            Assert(window.WindowState == WindowState.Normal, "还原后 WPF 窗口状态应为 Normal");
            GetWindowRect(handle, out var after);
            Assert(before.Left == after.Left && before.Top == after.Top &&
                before.Right == after.Right && before.Bottom == after.Bottom,
                "还原后组件应回到原位置和尺寸");

            window.HideFromApp();
            Pump();
            Thread.Sleep(150);
            Pump();
            Assert(!window.IsVisible, "应用主动隐藏（托盘开关、组件可见性）不应被恢复");
            window.Show();
            Pump();
            Assert(window.IsVisible, "再次显示应用隐藏的组件应正常");
        }
        finally
        {
            window.CanClose = true;
            window.Close();
            Pump();
        }
    }

    // While the shell shows the desktop the cards are lifted into the topmost band
    // under the taskbar; when ordinary windows return they go back below them. The
    // z-order move keeps position and visibility and never activates the card.
    private static void TestDesktopSurfaceRaiseAndLower()
    {
        var state = TodoService.CreateDefaultWidget();
        var window = new TodoWidgetWindow(state)
        {
            Left = 300,
            Top = 260,
            Width = 320,
            Height = 260
        };
        ShowAndPump(window);
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            Assert(handle != 0, "窗口应已创建");
            Assert(window.WindowHandle == handle, "WindowHandle 应返回原生句柄");
            window.SendToBack();
            Pump();
            GetWindowRect(handle, out var before);
            Assert((GetWindowLongPtr(handle, GwlExStyle) & WsExTopmost) == 0, "常态下组件不应置顶");

            window.RaiseAboveDesktop(BingLan.App.Interop.DockNativeMethods.HwndTopmost);
            PumpUntil(() => (GetWindowLongPtr(handle, GwlExStyle) & WsExTopmost) != 0,
                "提升后组件应进入置顶带");
            Assert(IsWindowVisible(handle), "提升后组件应保持可见");
            GetWindowRect(handle, out var raised);
            Assert(before.Left == raised.Left && before.Top == raised.Top &&
                before.Right == raised.Right && before.Bottom == raised.Bottom,
                "提升只改 Z 序，不应移动或缩放组件");

            window.SendToBack();
            PumpUntil(() => (GetWindowLongPtr(handle, GwlExStyle) & WsExTopmost) == 0,
                "回落后组件应离开置顶带");
            Assert(IsWindowVisible(handle), "回落后组件应保持可见");
        }
        finally
        {
            window.CanClose = true;
            window.Close();
            Pump();
        }
    }

    private static void TestOnboardingWizard()
    {
        DesktopLayoutPreset? preset = null;
        string? greeting = null;
        IReadOnlyList<DockPinnedApp>? pinned = null;
        string? downloads = null;
        string? dockMonitor = null;
        DesktopMode? mode = null;
        var completed = 0;
        var settingsOpened = 0;
        var app = new DockPinnedApp { DisplayName = "测试浏览器", ExecutablePath = @"C:\Apps\Browser.exe" };
        var window = new OnboardingWindow(new OnboardingActions
        {
            Monitors =
            [
                new OnboardingMonitor(@"\\.\DISPLAY1", "主显示器 1 · 2560×1440 · 125%", true),
                new OnboardingMonitor(@"\\.\DISPLAY2", "显示器 2 · 1920×1080 · 100%", false)
            ],
            ApplyMonitor = device => dockMonitor = device,
            DownloadsFolder = Path.GetTempPath(),
            GreetingName = "原称呼",
            ApplyPreset = value => preset = value,
            ApplyGreetingName = value => greeting = value,
            DetectEntries = () => Task.FromResult<IReadOnlyList<OnboardingEntry>>(
                [new OnboardingEntry("browser", app), new OnboardingEntry("music", null)]),
            ApplyEntries = (apps, downloadsFolder) =>
            {
                pinned = apps;
                downloads = downloadsFolder;
            },
            ApplyDesktopMode = value => mode = value,
            Complete = () => completed++,
            OpenSettings = () => settingsOpened++
        });
        ShowAndPump(window);
        try
        {
            Assert(Require<TextBlock>(window, "StepText").Text == "第 1 步，共 4 步", "引导应从第一步开始");
            var monitorSelector = Require<ComboBox>(window, "MonitorSelector");
            Assert(monitorSelector.Items.Count == 2 && monitorSelector.SelectedIndex == 0, "应列出显示器并默认选中主显示器");
            monitorSelector.SelectedIndex = 1;
            Pump();
            Assert(dockMonitor == @"\\.\DISPLAY2", "选择显示器应用于 Dock");
            var next = Require<Button>(window, "NextButton");
            Require<RadioButton>(window, "GlacierPresetRadio").IsChecked = true;
            next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(preset == DesktopLayoutPreset.GlacierWorkbench, "第一步应应用所选布局");

            Require<TextBox>(window, "GreetingNameEditor").Text = " 新称呼 ";
            next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(greeting == "新称呼", "第二步应保存去除空白后的称呼");

            var rows = FindVisualChildren<CheckBox>(Require<StackPanel>(window, "EntryRows")).ToList();
            Assert(rows.Count == 2 && rows[0].IsChecked == true && rows[1].IsEnabled == false,
                "找到的应用应默认勾选，未找到的应不可勾选");
            next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(pinned is { Count: 1 } && pinned[0] == app && downloads == Path.GetTempPath(),
                "第三步应固定勾选的应用并添加所选下载文件夹");

            Assert(Require<TextBlock>(window, "DockNoteText").Visibility == Visibility.Visible,
                "选了应用时最后一步应说明 Dock 何时显示");
            Require<CheckBox>(window, "OpenSettingsCheckBox").IsChecked = true;
            Require<RadioButton>(window, "AppleModeRadio").IsChecked = true;
            Assert(Equals(next.Content, "进入桌面"), "最后一步按钮应为进入桌面");
            next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(mode == DesktopMode.AppleStyle && completed == 1 && !window.IsVisible,
                "完成引导应应用桌面模式、标记完成并关闭");
            Assert(settingsOpened == 1, "勾选后完成引导应打开设置中心");
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
            }
        }

        var skipped = 0;
        var skipWindow = new OnboardingWindow(new OnboardingActions { Complete = () => skipped++ });
        ShowAndPump(skipWindow);
        Require<Button>(skipWindow, "SkipButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump();
        Assert(skipped == 1 && !skipWindow.IsVisible, "跳过引导应标记完成且只标记一次");
    }

    private static void TestResetComponentPlacement()
    {
        (WidgetWindowBase? Window, DesktopComponentKind? Kind)? requested = null;
        var window = new SettingsWindow(
            new CityLookupService(),
            new InformationWidgetState(),
            DesktopExperienceRules.CreateDefault(),
            (_, _) => { },
            _ => { },
            _ => { },
            maintenance: new SettingsMaintenance
            {
                ResetWidgetPlacement = (target, kind) =>
                {
                    requested = (target, kind);
                    return true;
                }
            });
        ShowAndPump(window);
        try
        {
            Require<ListBox>(window, "SettingsNavigationList").SelectedIndex = 1;
            Pump();
            var reset = FindVisualChildren<Button>(window)
                .First(button => button.IsVisible && Equals(button.Content, "恢复默认位置"));
            reset.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(requested is { Kind: not null }, "恢复默认位置应针对所选内置组件");
            Assert(
                Require<TextBlock>(window, "ComponentSettingsStatusText").Text == "已恢复默认位置",
                "恢复默认位置后应显示结果");
        }
        finally
        {
            window.Close();
        }
    }

    private static void TestSettingsRowDividersAlignWithTitles()
    {
        var window = new SettingsWindow(
            new CityLookupService(),
            new InformationWidgetState(),
            DesktopExperienceRules.CreateDefault(),
            (_, _) => { },
            _ => { },
            _ => { });
        ShowAndPump(window);
        try
        {
            var navigation = Require<ListBox>(window, "SettingsNavigationList");
            for (var page = 0; page < navigation.Items.Count; page++)
            {
                navigation.SelectedIndex = page;
                Pump();
                foreach (var row in FindVisualChildren<SettingsRow>(window).Where(row => row.IsVisible))
                {
                    var divider = (FrameworkElement)row.Template.FindName("Divider", row);
                    var title = FindVisualChildren<StackPanel>(row).First(panel => Grid.GetColumn(panel) == 1);
                    var dividerLeft = divider.TranslatePoint(new Point(), row).X;
                    var titleLeft = title.TranslatePoint(new Point(), row).X;
                    Assert(
                        Math.Abs(dividerLeft - titleLeft) < 0.5,
                        $"设置行“{row.Header}”的分隔线应与标题左对齐（{dividerLeft} ≠ {titleLeft}）");
                }
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void TestCleanDesktopToggle()
    {
        var enabled = false;
        var requests = new List<bool>();
        var window = new SettingsWindow(
            new CityLookupService(),
            new InformationWidgetState(),
            DesktopExperienceRules.CreateDefault(),
            (_, _) => { },
            _ => { },
            _ => { },
            maintenance: new SettingsMaintenance
            {
                IsCleanDesktopEnabled = () => enabled,
                SetCleanDesktop = request =>
                {
                    requests.Add(request);
                    enabled = false;
                    return Task.FromResult("当前桌面不支持清爽模式");
                }
            });
        ShowAndPump(window);
        try
        {
            var checkBox = Require<CheckBox>(window, "CleanDesktopCheckBox");
            Assert(checkBox.IsChecked == false, "清爽桌面默认应关闭");
            checkBox.IsChecked = true;
            Pump();
            Assert(requests.SequenceEqual([true]), "勾选应只请求开启一次");
            Assert(checkBox.IsChecked == false && checkBox.IsEnabled, "开启失败后开关应回到实际状态");
            Assert(
                Require<TextBlock>(window, "CleanDesktopStatusText").Text == "当前桌面不支持清爽模式",
                "开启失败时应显示原因");
        }
        finally
        {
            window.Close();
        }
    }

    private static void TestAppearancePanelSizeAppliesOnItsOwn()
    {
        var note = new NoteWidgetWindow(new NoteWidgetState
        {
            Placement = new WindowPlacement { Left = 300, Top = 300, Width = 306, Height = 266 }
        });
        ShowAndPump(note);
        var panel = new WidgetAppearancePanel();
        var host = new Window { Content = panel, Width = 420, Height = 700, ShowActivated = false };
        try
        {
            panel.Attach(note);
            ShowAndPump(host);
            Assert(!FindVisualChildren<Button>(panel).Any(button => Equals(button.Content, "应用尺寸")),
                "尺寸不应再需要单独的应用按钮");

            var width = Require<TextBox>(panel, "WidthEditor");
            width.Text = "420";
            width.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Pump();
            Assert(Math.Abs(note.Width - 420) < 0.5, $"离开宽度输入框后应立即应用，实际 {note.Width}");

            width.Text = "380";
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (Math.Abs(note.Width - 380) >= 0.5 && DateTime.UtcNow < deadline)
            {
                Pump();
                Thread.Sleep(50);
            }
            Assert(Math.Abs(note.Width - 380) < 0.5, $"输入停顿后应自动应用，实际 {note.Width}");
        }
        finally
        {
            host.Close();
            note.CanClose = true;
            note.Close();
            Pump();
        }
    }

    private static void TestStyleSettingsAndRestoreNotice()
    {
        var style = new DesktopStyleState();
        var applied = 0;
        var retried = 0;
        var notice = "上次隐藏的桌面图标未能自动恢复。";
        var window = new SettingsWindow(
            new CityLookupService(),
            new InformationWidgetState(),
            DesktopExperienceRules.CreateDefault(),
            (_, _) => { },
            _ => { },
            _ => { },
            maintenance: new SettingsMaintenance
            {
                Style = style,
                ApplyStyle = () => applied++,
                CleanDesktopNotice = () => notice,
                RetryCleanDesktopRestore = () =>
                {
                    retried++;
                    notice = string.Empty;
                    return Task.FromResult("桌面图标已恢复");
                }
            });
        ShowAndPump(window);
        try
        {
            var shadow = Require<ComboBox>(window, "CardShadowSelector");
            Assert(shadow.SelectedItem is ComboBoxItem { Tag: "None" } && applied == 0, "应显示当前阴影且加载时不写回");
            shadow.SelectedIndex = 2;
            Require<ComboBox>(window, "CardSpacingSelector").SelectedIndex = 0;
            Pump();
            Assert(style.CardShadow == CardShadow.Strong && style.CardSpacing == 8 && applied == 2,
                "更改整体风格应写回并立即应用");

            var retry = Require<Button>(window, "CleanDesktopRetryButton");
            Assert(retry.Visibility == Visibility.Visible
                && Require<TextBlock>(window, "CleanDesktopStatusText").Text == "上次隐藏的桌面图标未能自动恢复。",
                "恢复失败应常驻显示并提供重新恢复");
            retry.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(retried == 1 && retry.Visibility == Visibility.Collapsed
                && Require<TextBlock>(window, "CleanDesktopStatusText").Text == "桌面图标已恢复",
                "重新恢复成功后应隐藏按钮并显示结果");
        }
        finally
        {
            window.Close();
        }

        var card = new TodoWidgetWindow(new TodoWidgetState());
        ShowAndPump(card);
        try
        {
            card.ApplyStyle(new DesktopStyleState { CardShadow = CardShadow.Soft });
            Assert(((UIElement)card.Content).Effect is System.Windows.Media.Effects.DropShadowEffect, "柔和阴影应加到卡片内容");
            card.ApplyStyle(new DesktopStyleState());
            Assert(((UIElement)card.Content).Effect is null, "无阴影时不应有效果");

            card.ApplySelection(false);
            card.Activate();
            Require<TextBox>(card, "TitleEditor").Focus();
            Pump();
            var settingsButton = FindVisualChildren<Button>(card)
                .First(button => System.Windows.Automation.AutomationProperties.GetName(button) == "设置"
                    || Equals(button.ToolTip, "设置"));
            Assert(card.IsWidgetSelected && settingsButton.Visibility == Visibility.Visible,
                "键盘焦点进入卡片后应显示设置入口");
        }
        finally
        {
            card.CanClose = true;
            card.Close();
        }
    }

    private static void TestThemeImportDialogShowsNameAndPreview()
    {
        var package = new ThemePackage();
        package.Manifest.Name = "预览测试主题";
        var preview = ThemePreviewRenderer.Render(package);

        var dialog = new ThemeImportDialog(package, preview);
        try
        {
            ShowAndPump(dialog);

            var nameText = Require<TextBlock>(dialog, "ThemeNameText");
            Assert(
                nameText.Text.Contains("预览测试主题", StringComparison.Ordinal),
                "导入对话框应显示主题名称");

            var previewImage = Require<Image>(dialog, "PreviewImage");
            Assert(previewImage.Visibility == Visibility.Visible, "导入对话框应显示预览图");
            Assert(previewImage.Source is not null, "导入对话框预览图应有图像源");

            var noPreviewText = Require<TextBlock>(dialog, "NoPreviewText");
            Assert(noPreviewText.Visibility == Visibility.Collapsed, "有预览图时不应显示占位文字");
        }
        finally
        {
            dialog.Close();
            Pump();
        }
    }

    private static void TestDesktopModeSelection()
    {
        var dock = new DockState();
        var taskbar = new TaskbarState();
        var dockApplied = 0;
        var taskbarApplied = 0;
        var window = new SettingsWindow(
            new CityLookupService(),
            new InformationWidgetState(),
            DesktopExperienceRules.CreateDefault(),
            (_, _) => { },
            _ => { },
            _ => { },
            dockState: dock,
            applyDock: () => dockApplied++,
            taskbarState: taskbar,
            applyTaskbar: () => taskbarApplied++,
            taskbarStatus: () => (taskbar.Mode, string.Empty));
        ShowAndPump(window);
        try
        {
            Assert(
                Require<RadioButton>(window, "WindowsNativeModeRadio").IsChecked == true,
                "默认状态应显示为 Windows 原生模式");

            Require<RadioButton>(window, "AppleStyleModeRadio").IsChecked = true;
            Pump();
            Assert(
                dock.IsEnabled && taskbar.Mode == TaskbarMode.SmartHide,
                "苹果式应开启 Dock 并让任务栏自动隐藏");
            Assert(dockApplied == 1 && taskbarApplied == 1, "切换模式应分别应用 Dock 与任务栏各一次");
            Assert(
                Require<CheckBox>(window, "DockEnabledCheckBox").IsChecked == true &&
                Require<RadioButton>(window, "TaskbarSmartHideRadio").IsChecked == true,
                "Dock 页与任务栏页应同步显示新设置");
            Assert(Require<FrameworkElement>(window, "DockOptionsPanel").IsEnabled, "Dock 开启后其余 Dock 设置应可用");

            Require<RadioButton>(window, "TaskbarTransparentRadio").IsChecked = true;
            Pump();
            Assert(
                Require<RadioButton>(window, "IceBlueHybridModeRadio").IsChecked == true,
                "单独改任务栏为透明后应识别为冰蓝混合");

            Require<CheckBox>(window, "DockEnabledCheckBox").IsChecked = false;
            Pump();
            Assert(
                Require<RadioButton>(window, "WindowsNativeModeRadio").IsChecked != true &&
                Require<RadioButton>(window, "IceBlueHybridModeRadio").IsChecked != true &&
                Require<RadioButton>(window, "AppleStyleModeRadio").IsChecked != true &&
                Require<TextBlock>(window, "DesktopModeStatusText").Text.Contains("自定义", StringComparison.Ordinal),
                "不属于三种模式的组合应显示为自定义");
            Assert(!Require<FrameworkElement>(window, "DockOptionsPanel").IsEnabled, "Dock 关闭后其余 Dock 设置应变灰");

            window.ShowSettingsPage("About");
            Pump();
            Assert(Require<FrameworkElement>(window, "GeneralPage").IsVisible, "旧的“关于”入口应打开通用页");
            window.ShowSettingsPage("Layout");
            Pump();
            Assert(Require<FrameworkElement>(window, "AppearancePage").IsVisible, "旧的“布局”入口应打开外观页");
            foreach (var swatch in Require<WrapPanel>(window, "GlassPaletteSwatches").Children.OfType<Button>())
            {
                Assert(Near(swatch.ActualWidth, swatch.ActualHeight), $"配色色块应为正圆，实际 {swatch.ActualWidth}×{swatch.ActualHeight}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void TestDockPlacementSettings()
    {
        var dock = new DockState { IsEnabled = true };
        var dockApplied = 0;
        var window = new SettingsWindow(
            new CityLookupService(),
            new InformationWidgetState(),
            DesktopExperienceRules.CreateDefault(),
            (_, _) => { },
            _ => { },
            _ => { },
            dockState: dock,
            applyDock: () => dockApplied++,
            taskbarState: new TaskbarState(),
            applyTaskbar: () => { },
            taskbarStatus: () => (TaskbarMode.SystemDefault, string.Empty));
        ShowAndPump(window);
        try
        {
            window.ShowSettingsPage("Dock");
            Pump();
            Assert(
                Near(Require<Slider>(window, "DockBottomGapSlider").Value, DockState.DefaultBottomGapDip),
                "距底高度默认应为原固定间距");
            Assert(
                Require<CheckBox>(window, "DockMaximizeReleaseCheckBox").IsChecked != true,
                "最大化让出底部空间默认应关闭");

            Require<Slider>(window, "DockBottomGapSlider").Value = 44;
            Pump();
            Assert(Near(dock.BottomGapDip, 44d), "调整滑块应写入 Dock 状态");
            Assert(
                Require<TextBlock>(window, "DockBottomGapText").Text.Contains("44", StringComparison.Ordinal),
                "距底高度文本应回显新值");
            Assert(dockApplied == 1, "调整距底高度应触发一次应用");

            Require<CheckBox>(window, "DockMaximizeReleaseCheckBox").IsChecked = true;
            Pump();
            Assert(dock.ReleaseWhenMaximized, "勾选后应开启最大化让出");
            Assert(dockApplied == 2, "切换最大化让出应再触发一次应用");
        }
        finally
        {
            window.Close();
        }
    }

    private static T Require<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        root.FindName(name) as T ?? throw new InvalidOperationException($"找不到控件 {name}");

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result)
            {
                yield return result;
            }
            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static nint Pack(Point point)
    {
        var x = (int)Math.Round(point.X);
        var y = (int)Math.Round(point.Y);
        return (nint)((y << 16) | (x & 0xFFFF));
    }

    private static double RoundedCornerOffset(double radius) =>
        radius - (radius - 1d) / Math.Sqrt(2d);

    private static void AssertHit(
        Window window,
        Point clientPoint,
        int expected,
        string name)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var screenPoint = window.PointToScreen(clientPoint);
        var actual = SendMessage(handle, WmNcHitTest, 0, Pack(screenPoint)).ToInt32();
        Assert(actual == expected, $"{name} 应为 {expected}，实际为 {actual}");
    }

    private static void PressKey(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target) ??
            throw new InvalidOperationException("按键目标没有可用的输入源");
        target.RaiseEvent(
            new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            });
    }

    private static int GetDwmAttribute(nint handle, int attribute)
    {
        var result = DwmGetWindowAttribute(
            handle,
            attribute,
            out var value,
            Marshal.SizeOf<int>());
        if (result < 0)
        {
            throw new InvalidOperationException(
                $"DwmGetWindowAttribute({attribute}) 失败：0x{result:X8}");
        }
        return value;
    }

    private static long GetTaskbarState()
    {
        var data = new AppBarData
        {
            Size = Marshal.SizeOf<AppBarData>()
        };
        return SHAppBarMessage(4, ref data).ToInt64();
    }

    private static void AssertLayeredClip(Window window, double radius, string name)
    {
        var geometry = window.Clip as RectangleGeometry ??
            throw new InvalidOperationException($"{name}没有逐像素 RectangleGeometry");
        Assert(Near(geometry.RadiusX, radius) && Near(geometry.RadiusY, radius),
            $"{name} Clip 半径错误");
        Assert(
            Near(geometry.Rect.Width, window.ActualWidth) &&
            Near(geometry.Rect.Height, window.ActualHeight),
            $"{name} Clip 没有覆盖完整窗口");
    }

    private static void AssertClipPoint(
        Window window,
        Point point,
        bool expectedInside,
        string name)
    {
        var actual = window.Clip?.FillContains(point) ?? true;
        Assert(actual == expectedInside, $"{name} Clip 判定与预期不符");
    }

    private static void AssertAntialiasedCorner(WidgetWindowBase window)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        var sampleSize = Math.Min(
            Math.Min(width, height),
            (int)Math.Ceiling(window.WidgetCornerRadius) + 3);
        var hasTransparent = false;
        var hasPartialCoverage = false;
        var hasSurface = false;
        for (var y = 0; y < sampleSize; y++)
        {
            for (var x = 0; x < sampleSize; x++)
            {
                var alpha = pixels[y * stride + x * 4 + 3];
                hasTransparent |= alpha == 0;
                hasPartialCoverage |= alpha is > 0 and < 200;
                hasSurface |= alpha >= 230;
            }
        }

        Assert(hasTransparent, "圆角外没有逐像素透明区域");
        Assert(hasPartialCoverage, "圆角边缘没有抗锯齿半透明覆盖像素");
        Assert(hasSurface, "圆角采样未覆盖组件主体");
    }

    private static void AssertWindowRegionCleared(nint handle)
    {
        var region = CreateRectRgn(0, 0, 1, 1);
        if (region == 0)
        {
            throw new InvalidOperationException("直角模式测试区域创建失败");
        }

        try
        {
            Assert(GetWindowRgn(handle, region) == 0, "直角模式没有移除 HWND 圆角区域");
        }
        finally
        {
            _ = DeleteObject(region);
        }
    }

    private static void AssertAppearanceBrush(
        Brush value,
        Color standardExpected,
        Color highContrastExpected,
        string name)
    {
        var brush = value as SolidColorBrush ??
            throw new InvalidOperationException($"{name}不是纯色 Brush");
        var expected = AccessibilityThemeManager.IsHighContrastEnabled
            ? highContrastExpected
            : standardExpected;
        Assert(brush.Color == expected,
            $"{name}颜色错误：实际 {brush.Color}，预期 {expected}");
    }

    private static void SavePreview(Window window, string path)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void SaveElementPreview(
        FrameworkElement element,
        Brush background,
        string path)
    {
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var panel = element as Panel;
        var originalBackground = panel?.Background;
        try
        {
            if (panel is not null)
            {
                panel.Background = background;
            }
            bitmap.Render(element);
        }
        finally
        {
            if (panel is not null)
            {
                panel.Background = originalBackground;
            }
        }
        var flattened = new RenderTargetBitmap(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            context.DrawImage(bitmap, new Rect(0, 0, width, height));
        }
        flattened.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(flattened));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void SaveLivePreview(Window window, string path)
    {
        var captureBackdrop = new Window
        {
            Left = window.Left - 24,
            Top = window.Top - 24,
            Width = window.Width + 48,
            Height = window.Height + 48,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Background = new SolidColorBrush(GetLivePreviewBackdropColor())
        };
        captureBackdrop.Show();
        try
        {
            window.Topmost = true;
            Pump();
            Thread.Sleep(500);
            LiveWindowCapture.Save(window, path, 12);
        }
        finally
        {
            window.Topmost = false;
            captureBackdrop.Close();
        }
    }

    private static Color GetLivePreviewBackdropColor()
    {
        var value = Environment.GetEnvironmentVariable("BINGLAN_UI_BACKDROP_COLOR");
        if (!string.IsNullOrWhiteSpace(value) &&
            ColorConverter.ConvertFromString(value) is Color color)
        {
            return color;
        }
        return Color.FromRgb(88, 105, 122);
    }

    private static void AssertAccessibleControls(FrameworkElement root, string surfaceName)
    {
        var controls = FindVisualChildren<Control>(root)
            .Where(control =>
                control.IsVisible &&
                control.IsEnabled &&
                control.Focusable &&
                control.IsTabStop)
            .Distinct()
            .ToList();
        Assert(controls.Count > 0, $"{surfaceName}没有可测试的键盘控件");

        foreach (var control in controls)
        {
            var peer = UIElementAutomationPeer.CreatePeerForElement(control) ??
                throw new InvalidOperationException(
                    $"{surfaceName}控件 {Describe(control)} 没有 UI Automation Peer");
            var accessibleName = peer.GetName()?.Trim() ?? string.Empty;
            Assert(
                !string.IsNullOrWhiteSpace(accessibleName),
                $"{surfaceName}控件 {Describe(control)} 没有屏幕阅读器名称");
            Assert(
                accessibleName is not "+" and not "×" and not "✥" and not "↕" and not "•••",
                $"{surfaceName}控件 {Describe(control)} 只有符号名称“{accessibleName}”");
            Assert(
                peer.GetAutomationControlType() != AutomationControlType.Custom,
                $"{surfaceName}控件 {Describe(control)} 没有可识别的 UI Automation 角色");
        }
    }

    private static void AssertTabOrderContains(
        Window window,
        IReadOnlyList<Control> expectedOrder,
        string surfaceName)
    {
        Assert(expectedOrder.Count > 0, $"{surfaceName}没有定义 Tab 顺序");
        window.Activate();
        expectedOrder[0].BringIntoView();
        Assert(expectedOrder[0].Focus(), $"{surfaceName}首个控件无法聚焦");
        Pump();
        AssertFocusedControlsVisible(window, expectedOrder, surfaceName);
        expectedOrder[0].BringIntoView();
        Assert(expectedOrder[0].Focus(), $"{surfaceName}首个控件无法重新聚焦");
        Pump();
        AssertTraversalContains(expectedOrder, FocusNavigationDirection.Next, surfaceName, "Tab");

        expectedOrder[^1].BringIntoView();
        Assert(expectedOrder[^1].Focus(), $"{surfaceName}末个控件无法聚焦");
        Pump();
        AssertTraversalContains(
            expectedOrder.Reverse().ToArray(),
            FocusNavigationDirection.Previous,
            surfaceName,
            "Shift+Tab");
    }

    private static void AssertTraversalContains(
        IReadOnlyList<Control> expectedOrder,
        FocusNavigationDirection direction,
        string surfaceName,
        string keyName)
    {
        var expectedIndex = 0;
        for (var step = 0; step < 256 && expectedIndex < expectedOrder.Count; step++)
        {
            if (ReferenceEquals(Keyboard.FocusedElement, expectedOrder[expectedIndex]))
            {
                expectedIndex++;
                if (expectedIndex == expectedOrder.Count)
                {
                    break;
                }
            }

            var focused = Keyboard.FocusedElement as UIElement ??
                throw new InvalidOperationException($"{surfaceName}在 {keyName} 遍历时丢失焦点");
            Assert(
                focused.MoveFocus(new TraversalRequest(direction)),
                $"{surfaceName}无法继续执行 {keyName} 遍历");
            Pump();
        }

        if (expectedIndex != expectedOrder.Count)
        {
            throw new InvalidOperationException(
                $"{surfaceName}的 {keyName} 顺序没有到达 {Describe(expectedOrder[expectedIndex])}");
        }
    }

    private static void AssertFocusedControlsVisible(
        Window window,
        IEnumerable<Control> controls,
        string surfaceName)
    {
        var clientBounds = new Rect(0, 0, window.ActualWidth, window.ActualHeight);
        foreach (var control in controls)
        {
            control.BringIntoView();
            Assert(control.Focus(), $"{surfaceName}控件 {Describe(control)} 无法聚焦");
            Pump();
            var origin = control.TranslatePoint(new Point(0, 0), window);
            var bounds = new Rect(origin, new Size(control.ActualWidth, control.ActualHeight));
            Assert(
                control.ActualWidth > 0 &&
                control.ActualHeight > 0 &&
                clientBounds.IntersectsWith(bounds),
                $"{surfaceName}控件 {Describe(control)} 聚焦后仍在窗口可视边界外");
        }
    }

    private static void ReportDpi(Visual visual, string surfaceName)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        Assert(
            dpi.DpiScaleX > 0 && dpi.DpiScaleY > 0,
            $"{surfaceName}没有有效的 WPF DPI 信息");
        Console.WriteLine(
            $"信息：{surfaceName} 当前 DPI {dpi.PixelsPerInchX:0}/{dpi.PixelsPerInchY:0} " +
            $"({dpi.DpiScaleX * 100:0}%/{dpi.DpiScaleY * 100:0}%)");
        if (SystemParameters.HighContrast && visual is Window window)
        {
            AssertHighContrastAppearance(window, surfaceName);
        }

        var expectedValue = Environment.GetEnvironmentVariable("BINGLAN_EXPECTED_DPI_PERCENT");
        if (double.TryParse(expectedValue, out var expectedPercent))
        {
            Assert(
                Near(dpi.DpiScaleX * 100d, expectedPercent) &&
                Near(dpi.DpiScaleY * 100d, expectedPercent),
                $"{surfaceName}实际缩放为 {dpi.DpiScaleX * 100:0}%/" +
                $"{dpi.DpiScaleY * 100:0}%，预期 {expectedPercent:0}%");
        }
    }

    private static void ReportHighContrast()
    {
        Console.WriteLine(
            $"信息：系统高对比度当前{(SystemParameters.HighContrast ? "已开启" : "未开启")}");
        if (string.Equals(
                Environment.GetEnvironmentVariable("BINGLAN_REQUIRE_HIGH_CONTRAST"),
                "1",
                StringComparison.Ordinal))
        {
            Assert(SystemParameters.HighContrast, "本轮要求高对比度，但 Windows 高对比度未开启");
        }
    }

    private static void AssertHighContrastAppearance(Window window, string surfaceName)
    {
        if (window is WidgetWindowBase widget)
        {
            var background = widget.WidgetBackgroundBrush as SolidColorBrush ??
                throw new InvalidOperationException($"{surfaceName}高对比度背景不是纯色");
            var text = widget.WidgetTextBrush as SolidColorBrush ??
                throw new InvalidOperationException($"{surfaceName}高对比度文字不是纯色");
            var systemText = SystemColors.WindowTextBrush as SolidColorBrush ??
                throw new InvalidOperationException("Windows 高对比度文字色不是纯色");
            Assert(background.Color.A == byte.MaxValue, $"{surfaceName}高对比度背景仍有透明度");
            Assert(
                text.Color == systemText.Color,
                $"{surfaceName}高对比度文字没有使用 Windows 系统文字色");
            return;
        }

        var windowBackground = window.Background as SolidColorBrush ??
            throw new InvalidOperationException($"{surfaceName}高对比度窗口背景不是纯色");
        var systemBackground = SystemColors.WindowBrush as SolidColorBrush ??
            throw new InvalidOperationException("Windows 高对比度窗口色不是纯色");
        Assert(
            windowBackground.Color == systemBackground.Color,
            $"{surfaceName}高对比度背景没有使用 Windows 系统窗口色");
    }

    private static void AssertWidgetHighContrast(WidgetWindowBase window)
    {
        var name = window.GetType().Name;
        AssertBrushColor(window.WidgetBackgroundBrush, SystemColors.WindowColor, $"{name}主体背景");
        AssertBrushColor(window.WidgetHeaderBrush, SystemColors.ControlColor, $"{name}标题背景");
        AssertBrushColor(window.WidgetTextBrush, SystemColors.WindowTextColor, $"{name}主要文字");
        AssertBrushColor(
            window.WidgetSecondaryTextBrush,
            SystemColors.WindowTextColor,
            $"{name}次要文字");
        Assert(
            window.BackdropResult.Detail.Contains("不透明系统色回退", StringComparison.Ordinal),
            $"{name}没有报告高对比度不透明系统色回退");
    }

    private static void AssertSettingsHighContrast(SettingsWindow settings)
    {
        AssertBrushColor(settings.Background, SystemColors.WindowColor, "设置窗口背景");
        AssertBrushColor(settings.Foreground, SystemColors.WindowTextColor, "设置窗口文字");
        AssertResourceBrushColor("WidgetItemSurfaceBrush", SystemColors.ControlColor);
        AssertResourceBrushColor("WidgetControlBrush", SystemColors.ControlColor);
        AssertResourceBrushColor("WidgetControlBorderBrush", SystemColors.ControlTextColor);
        AssertResourceBrushColor("IceActionBrush", SystemColors.HighlightColor);
        AssertResourceBrushColor("IceActionTextBrush", SystemColors.HighlightTextColor);
    }

    private static void AssertVisibleBrushesOpaque(FrameworkElement root, string surfaceName)
    {
        foreach (var element in FindVisualChildren<FrameworkElement>(root).Where(x => x.IsVisible))
        {
            if (element.TemplatedParent is not null &&
                element.Name is not "Card" and not "ButtonBorder" and not "TileSurface")
            {
                continue;
            }
            if (element is Panel { Background: SolidColorBrush panelBrush })
            {
                AssertOpaqueOrTransparent(panelBrush, element, surfaceName, "背景");
            }
            if (element is Border
                {
                    Background: SolidColorBrush borderBackground,
                    BorderBrush: SolidColorBrush borderBrush
                })
            {
                AssertOpaqueOrTransparent(borderBackground, element, surfaceName, "背景");
                AssertOpaqueOrTransparent(borderBrush, element, surfaceName, "边框");
            }
            if (element is Control { Background: SolidColorBrush controlBackground })
            {
                AssertOpaqueOrTransparent(controlBackground, element, surfaceName, "背景");
            }
            if (element is Control { Foreground: SolidColorBrush controlForeground })
            {
                Assert(controlForeground.Color.A == byte.MaxValue,
                    $"{surfaceName}的 {element.GetType().Name} 文字仍有透明度");
            }
            if (element is TextBlock { Foreground: SolidColorBrush textForeground })
            {
                Assert(textForeground.Color.A == byte.MaxValue,
                    $"{surfaceName}的 TextBlock 文字仍有透明度");
            }
        }
    }

    private static void AssertOpaqueOrTransparent(
        SolidColorBrush brush,
        FrameworkElement element,
        string surfaceName,
        string propertyName)
    {
        Assert(
            brush.Color.A is 0 or byte.MaxValue,
            $"{surfaceName}的 {element.GetType().Name}#{element.Name} {propertyName}仍为半透明 " +
            $"({brush.Color}, Alpha={brush.Color.A}, " +
            $"TemplatedParent={element.TemplatedParent?.GetType().Name ?? "无"})");
    }

    private static void AssertResourceBrushColor(string key, Color expected)
    {
        var brush = Application.Current.Resources[key] as Brush ??
            throw new InvalidOperationException($"找不到主题资源 {key}");
        AssertBrushColor(brush, expected, $"主题资源 {key}");
    }

    private static void AssertThemeResourcesOpaque()
    {
        foreach (var key in Application.Current.Resources.Keys.Cast<object>())
        {
            if (Application.Current.Resources[key] is SolidColorBrush brush)
            {
                Assert(
                    brush.Color.A == byte.MaxValue,
                    $"高对比度主题资源 {key} 仍为半透明 ({brush.Color})");
            }
        }
    }

    private static void AssertBrushColor(Brush brush, Color expected, string name)
    {
        var solid = brush as SolidColorBrush ??
            throw new InvalidOperationException($"{name}不是纯色 Brush");
        Assert(solid.Color == expected,
            $"{name}颜色错误：实际 {solid.Color}，预期 {expected}");
    }

    private static string Describe(Control control) =>
        string.IsNullOrWhiteSpace(control.Name)
            ? control.GetType().Name
            : $"{control.GetType().Name}#{control.Name}";

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 0.5;

    private static void Run(string name, Action test, List<string> failures)
    {
        try
        {
            test();
            Console.WriteLine($"通过：{name}");
        }
        catch (Exception ex)
        {
            failures.Add("失败：" + name + " - " + ex.Message + " @ " + (ex.StackTrace ?? string.Empty).Split('\n').FirstOrDefault());
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        internal int Size;
        internal nint Window;
        internal uint CallbackMessage;
        internal uint Edge;
        internal TaskbarEdgeGuard.PixelRect Rectangle;
        internal nint Parameter;
    }
}
