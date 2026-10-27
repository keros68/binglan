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
using BingLan.App.Dock;
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

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(nint window);

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
        if (Environment.GetCommandLineArgs().Contains("--shell-surfaces-preview"))
        {
            ShellSurfacePreview.Run(app);
            return 0;
        }
        if (Environment.GetCommandLineArgs().Contains("--dock-glass-preview"))
        {
            DockGlassPreview.Run(app);
            return 0;
        }
        if (Environment.GetCommandLineArgs().Contains("--appearance-preview"))
        {
            AppearancePreview.Run(app);
            return Environment.ExitCode;
        }

        if (Environment.GetCommandLineArgs().Contains("--appearance-tests"))
        {
            try
            {
                AppearancePreviewTests.Run(ShowAndPump, Pump);
                Console.WriteLine("外观试选独立回归检查通过。");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        Run("待办真实控件编辑与勾选", TestTodoControls, failures);
        Run("今日待办字号缩放覆盖列表、周历、当日浮层与编辑窗", TestTodoFontScale, failures);
        Run("今日待办字体覆盖列表、周历、当日浮层与编辑窗", TestTodoFontFamily, failures);
        Run("今日待办字号缩放与字体跟随设置页", TestTodoFontScaleFollowsSetting, failures);
        Run("周历与底部统计跟随文字颜色，当日浮层保持固定配色", TestTodoTextColorFollowsAppearance, failures);
        Run("新建与启动时待办卡立即使用组件外观", TestTodoTakesComponentLookOnCreate, failures);
        Run("组件设置隐藏不生效的信息密度与字体选项", TestComponentEditorHidesUnusedOptions, failures);
        Run("逐像素透明 HWND 与单一可调圆角", TestUnifiedCornerAndSettings, failures);
        Run("卡片自己跟随指针拖动，锁定后不动，边缘不缩放", TestNativeHitTesting, failures);
        Run("分组盒图标按宽度均分成整列", TestFileBoxTilesFillRow, failures);
        Run("快捷入口显示系统位置图标", TestQuickPlaces, failures);
        Run("新建卡片不会重置 Dock 与卡片颜色", TestNewCardKeepsAppBrushes, failures);
        Run("卡片显示后与切换窗口后位于普通窗口下方", TestCardsStayBehindWindows, failures);
        Run("开始菜单应用列表可直接固定到 Dock", TestStartMenuApps, failures);
        Run("固定项保留快捷方式的启动参数与工作目录", TestPinKeepsShortcutArguments, failures);
        Run("应用列表打开时其他窗口仍可用且按钮可见", TestAppPickerKeepsAppUsable, failures);
        Run("内置开源字体从程序中加载并带对应字重", TestBundledFonts, failures);
        Run("自动隐藏任务栏边缘不被组件占用", TestTaskbarRevealEdgeGuard, failures);
        Run("任务栏 XAML 恢复边界与取消", TestTaskbarXamlRecovery, failures);
        Run("工作区变化后卡片让出任务栏或 Dock 占用的边缘", TestCardLeavesReservedEdge, failures);
        Run("隐藏期间丢失显示器后卡片在显示前被拉回", TestHiddenCardRecoversFromLostMonitor, failures);
        Run("预设布局在同步状态捕获时完整应用", TestApplyPlacementDuringCapture, failures);
        Run("窗口位置尺寸关闭后重载", TestWindowPersistence, failures);
        Run("桌面分组盒导入入口、排序和图标网格", TestFileBoxWindow, failures);
        Run("桌面分组盒失效状态与系统入口菜单", TestFileBoxInvalidStateAndSystemEntries, failures);
        Run("原文件删除后映射自动从分组盒清除", TestFileMappingWatchClearsDeletedMapping, failures);
        Run("桌面新文件触发自动收纳，非桌面目录不触发", TestFileMappingWatchImportTrigger, failures);
        Run("分组盒自动刷新开关默认关闭并按需写回", TestFileBoxAutomationToggle, failures);
        Run("桌面分组盒显示名称与盒间转移", TestFileBoxRenameAndTransfer, failures);
        Run("桌面分组盒图标与列表视图切换", TestFileBoxViewModeSwitch, failures);
        Run("卡片图标取高分辨率帧而非 32px 拉伸", TestIconProviderReturnsHighResolutionFrames, failures);
        Run("旧图标不被 Jumbo 透明占位缩成小点", TestSmallShellIconFrames, failures);
        Run("屏幕阅读器名称、Tab 顺序与实际 DPI 边界", TestAccessibilityAndKeyboardNavigation, failures);
        Run("高对比度系统色切换与冰蓝材质恢复", TestHighContrastThemeSwitch, failures);
        Run("今日列表与周历按日期映射", TestTodoTodayCalendarMapping, failures);
        Run("单实例门禁占用与释放", TestSingleInstanceGate, failures);
        Run("托盘双击打开并恢复设置窗口", TestTrayDoubleClick, failures);
        Run("缺失本机标题字体安全回退", TestUnavailableTitleFontFallback, failures);
        Run("桌面模式同时设置 Dock 与任务栏", TestDesktopModeSelection, failures);
        Run("Dock 距底高度与最大化让出设置即时生效", TestDockPlacementSettings, failures);
        Run("系统入口选择与 Dock 开始菜单开关", () => ShellSurfaceTests.Run(ShowAndPump, Pump), failures);
        Run("托盘调用门控、迟到会话与宿主清理", () => TrayOverflowExperimentTests.Run(Pump), failures);
        Run("待办按 Enter 提交后文字写入状态", TestTodoEnterCommitsText, failures);
        Run("待办勾选后各行仍可被辅助技术访问", TestTodoRowsStayAccessible, failures);
        Run("托盘隐藏与显示桌面组件", TestTrayToggleDesktop, failures);
        Run("只开顶栏时共享采样保持运行", TestTopBarOnlyKeepsSamplerAlive, failures);
        Run("Shell 隐藏或最小化组件后自动恢复显示", TestShellHideAndMinimizeRecovery, failures);
        Run("显示桌面时组件提升到任务栏下方、恢复后回落", TestDesktopSurfaceRaiseAndLower, failures);
        Run("显示桌面判定每次事件突发只执行一次，不遗留计时器", TestDesktopSurfaceWatcherChecksOncePerBurst, failures);
        Run("组件恢复默认位置", TestResetComponentPlacement, failures);
        Run("清爽桌面开关", TestCleanDesktopToggle, failures);
        Run("设置行分隔线对齐标题", TestSettingsRowDividersAlignWithTitles, failures);
        Run("待办周历周数设置面板", TestTodoWeekCountSettingPanel, failures);
        Run("待办日期编辑在保存与父窗口关闭时提交", TestTodoDayEditorPersistence, failures);
        Run("空白待办在光标离开后自动删除", TestTodoBlankRowsPruned, failures);
        Run("周历当日浮层点击别处自动关闭", TestTodoDayPeekClosesOnOutsideClick, failures);
        Run("正在编辑的待办可拖选文字，Esc 不被上浮吞掉", TestTodoEditingTextKeepsSelection, failures);
        Run("待办日期编辑对话框独占并保留条目", TestTodoDayEditorExclusive, failures);
        Run("待办详情浮层随内容与尺寸保持可见", TestTodoPeekBounds, failures);
        Run("整体风格与清爽桌面恢复提示", TestStyleSettingsAndRestoreNotice, failures);
        Run("外观草稿取消、对比、限定应用与竖屏预览", () => AppearancePreviewTests.Run(ShowAndPump, Pump), failures);
        Run("便签尺寸输入后自动生效", TestAppearancePanelSizeAppliesOnItsOwn, failures);
        Run("首次使用引导逐步应用设置", TestOnboardingWizard, failures);
        Run("卡片拖动吸附到相邻卡片且不会被系统吸附成半屏", () => SnapMoveTests.Run(ShowAndPump, Pump), failures);
        Run("显示器排列切换后卡片回到该排列下的位置", () => DisplayLayoutWindowTests.Run(ShowAndPump, Pump), failures);
        Run("Dock 隐藏把手的窗口样式、拖动与显隐", () => DockHandleTests.Run(ShowAndPump, Pump), failures);
        Run("Dock 形变动画的收敛点、完成与打断", () => DockMorphAnimatorTests.Run(ShowAndPump, Pump, PumpUntil), failures);
        Run("Dock 原生玻璃形变、层级、回退与窗口清理", () => DockGlassSurfaceTests.Run(ShowAndPump, Pump, PumpUntil), failures);
        Run("Dock 最大化状态钩子的过滤与即时通知", () => MaximizeStateWatcherTests.Run(ShowAndPump, Pump, PumpUntil), failures);
        Run("顶端信息条的窗口样式、模块与点击深链", () => TopBarWindowTests.Run(ShowAndPump, Pump), failures);
        Run("搜索菜单对比度、右键与键盘操作", () => TopBarWindowTests.SearchResultContextMenuStaysOpen(ShowAndPump, Pump), failures);
        Run("主题导入对话框显示名称与预览图", TestThemeImportDialogShowsNameAndPreview, failures);

        if (failures.Count > 0)
        {
            Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
            return 1;
        }

        Console.WriteLine(Environment.GetEnvironmentVariable("BINGLAN_UI_TEST_FILTER") is { Length: > 0 }
            ? "所选 WPF UI 烟雾测试通过。" : "全部 WPF UI 烟雾测试通过。");
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
            Assert(state.Items.Count == 2, "新增按钮未创建待办");
            // 写入文字模拟用户已输入：空白行会在卡片失去光标（如前台被抢走）时被删除。
            state.Items.Single(x => x.Text != "原待办").Text = "新增待办";
            Pump();

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
            deleteButtons.Single(x => x.DataContext is TodoItemState { Text: "原待办" })
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
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

    private static void TestTodoTodayCalendarMapping()
    {
        var state = new TodoWidgetState();
        var yesterdayOpen = TodoService.Add(state, "昨日未完成任务", DateTime.Today.AddDays(-1));
        var yesterdayCompleted = TodoService.Add(state, "昨日完成任务", DateTime.Today.AddDays(-1));
        yesterdayCompleted.IsCompleted = true;
        TodoService.StampCompletion(yesterdayCompleted);
        var first = TodoService.Add(state, "今天的第一项", DateTime.Today);
        var second = TodoService.Add(state, "今天的第二项", DateTime.Today);
        var tomorrow = TodoService.Add(state, "明天的任务", DateTime.Today.AddDays(1));
        var window = new TodoWidgetWindow(state);
        ShowAndPump(window);
        try
        {
            var list = Require<ItemsControl>(window, "TodoList");
            List<Guid> TodayOrder() => list.Items.OfType<TodoItemState>()
                .Where(x => TodoService.BelongsToTodayList(x, DateTime.Today)
                    && list.ItemContainerGenerator.ContainerFromItem(x) is UIElement row
                    && row.Visibility == Visibility.Visible)
                .Select(x => x.Id).ToList();
            Assert(TodayOrder().SequenceEqual([first.Id, second.Id]),
                "重新打开列表时，昨日完成和未完成任务均不得出现在今日列表");
            Assert(
                TodoService.ItemsForDate(state, DateTime.Today.AddDays(-1)).Select(x => x.Id)
                    .SequenceEqual([yesterdayOpen.Id, yesterdayCompleted.Id]),
                "昨日任务应保留在昨日周历，供后续重新安排");

            var viewMode = Require<Button>(window, "ViewModeButton");
            viewMode.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            var weekHost = Require<Grid>(window, "WeekHost");
            var weeklyFirstCheck = FindVisualChildren<CheckBox>(weekHost)
                .First(cb => cb.IsVisible && cb.DataContext is TodoItemState item && item.Id == first.Id);
            weeklyFirstCheck.IsChecked = true;
            Pump();
            Assert(first.IsCompleted && TodoService.ItemsForDate(state, DateTime.Today).Any(x => x.Id == first.Id && x.IsCompleted),
                "周历勾选应修改今日列表使用的同一任务记录");

            viewMode.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(TodayOrder().SequenceEqual([second.Id, first.Id]),
                "从周历切回今日列表后应反映周历勾选后的完成排序");
            foreach (var item in new[] { first, second })
            {
                var row = list.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
                Assert(row is not null, "今日列表应生成可见任务行");
                var renderedText = FindVisualChildren<TextBox>(row).Single().Text;
                Assert(renderedText == item.Text,
                    $"今日列表行文本与任务记录不一致：期望 {item.Text}，实际 {renderedText}");
            }

            var previewPath = Environment.GetEnvironmentVariable("BINGLAN_TODO_ORDER_PREVIEW");
            if (!string.IsNullOrWhiteSpace(previewPath))
            {
                SavePreview(window, previewPath);
            }

            var firstCheck = FindVisualChildren<CheckBox>(list)
                .First(cb => cb.DataContext is TodoItemState item && item.Id == second.Id);
            firstCheck.IsChecked = true;
            Pump();
            Assert(TodayOrder().SequenceEqual([first.Id, second.Id]),
                "列表新勾选的任务应移动到今日已完成区末尾");
            viewMode.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(
                FindVisualChildren<CheckBox>(weekHost)
                    .Any(cb => cb.IsVisible && cb.DataContext is TodoItemState item && item.Id == second.Id && cb.IsChecked == true),
                "列表勾选应立即映射到周历今天格");
            viewMode.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            var secondListCheck = FindVisualChildren<CheckBox>(list)
                .First(cb => cb.DataContext is TodoItemState item && item.Id == second.Id);
            secondListCheck.IsChecked = false;
            Pump();
            Assert(TodayOrder().SequenceEqual([second.Id, first.Id]),
                "取消完成应将任务移回未完成区末尾");

            var add = FindVisualChildren<Button>(window).Single(x => Equals(x.Content, "+"));
            add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            var added = state.Items.Single(x => x.Id != first.Id && x.Id != second.Id
                && x.Id != yesterdayOpen.Id && x.Id != yesterdayCompleted.Id && x.Id != tomorrow.Id);
            Assert(added.Date == DateTime.Today && TodoService.ItemsForDate(state, DateTime.Today).Any(x => x.Id == added.Id),
                "今日列表新增任务应写入今天日期并映射到周历今天格");
            // 模拟用户已在新行输入文字；没写内容的空白行在光标离开后会被删除。
            added.Text = "新增的任务";
            Assert(TodayOrder().SequenceEqual([second.Id, added.Id, first.Id]),
                "列表新增未完成任务应排在今日已完成任务之前");

            // 模拟午夜刷新：当前列表的任务改为昨天，明日任务改为今天；刷新后旧行必须折叠，新行必须出现。
            first.Date = DateTime.Today.AddDays(-1);
            second.Date = DateTime.Today.AddDays(-1);
            added.Date = DateTime.Today.AddDays(-1);
            typeof(TodoWidgetWindow)
                .GetField("_todayScopeDate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(window, DateTime.Today.AddDays(-1));
            tomorrow.Date = DateTime.Today;
            typeof(TodoWidgetWindow)
                .GetMethod("TodayRefreshTimer_Tick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [null, EventArgs.Empty]);
            Pump();
            Assert(TodayOrder().SequenceEqual([tomorrow.Id])
                    && list.ItemContainerGenerator.ContainerFromItem(first) is UIElement firstRow
                    && firstRow.Visibility == Visibility.Collapsed
                    && list.ItemContainerGenerator.ContainerFromItem(added) is UIElement addedRow
                    && addedRow.Visibility == Visibility.Collapsed
                    && list.ItemContainerGenerator.ContainerFromItem(tomorrow) is UIElement tomorrowRow
                    && tomorrowRow.Visibility == Visibility.Visible,
                "跨天刷新后旧日期行必须折叠，只有新当天日期行显示");

            var clear = Require<Button>(window, "ClearCompletedButton");
            clear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(state.Items.Any(x => x.Id == yesterdayCompleted.Id),
                "清理今日已完成不得删除历史日期的已完成任务");
        }
        finally
        {
            Close(window);
        }
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
            var buttons = FindVisibleChildren<Button>(window).ToList();
            var expectedOrder = new Control[]
            {
                buttons.Single(button => Equals(button.ToolTip, "设置")),
                buttons.Single(button => Equals(button.ToolTip, "新增待办")),
                FindVisibleChildren<CheckBox>(window).Single(),
                FindVisibleChildren<TextBox>(window).Single(textBox => textBox.Name != "TitleEditor"),
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

    // Regression for user feedback that some pinned apps never start: a shortcut that
    // only works with its arguments and working directory (AutoCAD, game launchers)
    // must keep them when pinned, and launching must honour both. cmd.exe expands
    // %CD% at run time and writes it to a file, proving it got the arguments verbatim
    // and started in the shortcut's folder.
    private static void TestPinKeepsShortcutArguments()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BingLanPinArgs", Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            var marker = Path.Combine(directory, "launched.txt");
            var shortcutPath = Path.Combine(directory, "带参数的程序.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            var shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            shortcut.Arguments = $"/c echo %CD%>\"{marker}\"";
            shortcut.WorkingDirectory = directory;
            shortcut.Save();

            var pin = DockShortcuts.CreatePin(shortcutPath);
            Assert(pin is not null, "指向程序的快捷方式应能固定");
            Assert(pin!.Arguments == $"/c echo %CD%>\"{marker}\"", "固定项丢失了快捷方式的启动参数");
            Assert(
                string.Equals(pin.WorkingDirectory, directory, StringComparison.OrdinalIgnoreCase),
                $"固定项丢失了快捷方式的工作目录，实际：{pin.WorkingDirectory}");

            var result = WindowCommandService.Launch(pin);
            Assert(result.Succeeded, $"固定项启动失败：{result.Message}");
            Assert(WaitForFileContent(marker, out var content), "被固定的程序没有运行（未写出结果文件）");
            Assert(
                string.Equals(content.Trim(), directory, StringComparison.OrdinalIgnoreCase),
                $"程序没有在快捷方式的工作目录中启动，实际：{content.Trim()}");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // The just-launched cmd may hold the file open for a moment; leaving a
                // temp folder behind is preferred over failing the test during cleanup.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool WaitForFileContent(string path, out string content)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(path))
            {
                try
                {
                    content = File.ReadAllText(path);
                    return true;
                }
                catch (IOException)
                {
                    // Still being written.
                }
            }
            Thread.Sleep(100);
        }

        content = string.Empty;
        return false;
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

    private static void TestHiddenCardRecoversFromLostMonitor()
    {
        var state = new TodoWidgetState
        {
            Placement = new WindowPlacement { Left = 100, Top = 100, Width = 300, Height = 240 }
        };
        var window = new TodoWidgetWindow(state);
        ShowAndPump(window);
        try
        {
            var handle = new WindowInteropHelper(window).Handle;

            // Park the card fully beyond every monitor, as it would sit after the monitor
            // it was on is unplugged, then hide it the way the desktop toggle does.
            NativeMethods.SetWindowPos(
                handle,
                0,
                (int)(SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth) + 300,
                100,
                300,
                240,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
            Pump();
            window.Hide();
            Pump();
            Assert(!window.IsVisible, "卡片应已隐藏");

            WindowScreenRecovery.EnsureOnScreen(window);
            Pump();

            Assert(GetWindowRect(handle, out var rect), "应能读取卡片位置");
            var recovered = MonitorCatalog.GetAll().Any(monitor =>
                monitor.WorkingArea.Left < rect.Right && rect.Left < monitor.WorkingArea.Right
                && monitor.WorkingArea.Top < rect.Bottom && rect.Top < monitor.WorkingArea.Bottom);
            Assert(recovered, "隐藏期间丢失显示器后，卡片在显示前就应被拉回工作区");
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

    private static void TestIconProviderReturnsHighResolutionFrames()
    {
        BitmapSource FrameOf(ImageSource? icon, string what)
        {
            Assert(icon is BitmapSource, $"{what}没有取到位图图标");
            return (BitmapSource)icon!;
        }

        var executablePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe");
        var fileIcon = FrameOf(ShellIconProvider.GetIcon(executablePath), "explorer.exe");
        Assert(
            Math.Max(fileIcon.PixelWidth, fileIcon.PixelHeight) >= 48,
            $"文件图标帧只有 {fileIcon.PixelWidth}×{fileIcon.PixelHeight}，卡片高 DPI 下会被拉糊");

        var shellIcon = FrameOf(ShellIconProvider.GetIcon("shell:MyComputerFolder"), "系统位置");
        Assert(
            Math.Max(shellIcon.PixelWidth, shellIcon.PixelHeight) >= 48,
            $"系统位置图标帧只有 {shellIcon.PixelWidth}×{shellIcon.PixelHeight}，卡片高 DPI 下会被拉糊");

        // 256 px 的 Jumbo 帧减半到 128 px 保存（显示最大约 96 px），内存只占四分之一。
        Assert(Math.Max(fileIcon.PixelWidth, fileIcon.PixelHeight) <= 128
                && Math.Max(shellIcon.PixelWidth, shellIcon.PixelHeight) <= 128,
            $"图标帧应不超过 128 px，实际 {fileIcon.PixelWidth} 与 {shellIcon.PixelWidth}");
        var pattern = new byte[256 * 256 * 4];
        for (var y = 0; y < 256; y++)
        {
            for (var x = 0; x < 256; x++)
            {
                var value = (byte)((x + y) % 2 == 0 ? 200 : 100);
                var offset = (y * 256 + x) * 4;
                pattern[offset] = value;
                pattern[offset + 1] = value;
                pattern[offset + 2] = value;
                pattern[offset + 3] = 255;
            }
        }
        var halved = ShellIconProvider.HalveJumbo(
            BitmapSource.Create(256, 256, 96, 96, PixelFormats.Pbgra32, null, pattern, 256 * 4));
        var pixel = new byte[4];
        halved.CopyPixels(new Int32Rect(37, 81, 1, 1), pixel, 4, 0);
        Assert(halved.PixelWidth == 128 && halved.PixelHeight == 128
                && pixel[0] == 150 && pixel[1] == 150 && pixel[2] == 150 && pixel[3] == 255,
            $"减半应为 2×2 平均：{halved.PixelWidth}×{halved.PixelHeight}，像素 {string.Join(",", pixel)}");
    }

    private static void TestSmallShellIconFrames()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-icons-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            foreach (var size in new[] { 16, 32, 48, 64, 128, 256 })
            {
                var pixels = new byte[size * size * 4];
                for (var y = size / 8; y < size * 7 / 8; y++)
                {
                    for (var x = size / 8; x < size * 7 / 8; x++)
                    {
                        var offset = (y * size + x) * 4;
                        pixels[offset] = 220;
                        pixels[offset + 1] = 100;
                        pixels[offset + 3] = 255;
                    }
                }
                var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
                Assert(!ShellIconProvider.IsJumboPlaceholder(bitmap), "居中图案的原有透明留白不应触发降级");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var png = new MemoryStream();
                encoder.Save(png);
                var path = Path.Combine(temp, $"single-{size}.ico");
                using (var writer = new BinaryWriter(File.Create(path)))
                {
                    writer.Write((ushort)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)1);
                    writer.Write((byte)(size == 256 ? 0 : size));
                    writer.Write((byte)(size == 256 ? 0 : size));
                    writer.Write((ushort)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)32);
                    writer.Write((uint)png.Length);
                    writer.Write((uint)22);
                    writer.Write(png.ToArray());
                }

                var source = ShellIconProvider.GetIcon(path) as BitmapSource;
                Assert(source is not null && source.IsFrozen, $"{size}px 图标未返回可跨线程位图");
                var rgba = new FormatConvertedBitmap(source!, PixelFormats.Bgra32, null, 0);
                var stride = rgba.PixelWidth * 4;
                var output = new byte[stride * rgba.PixelHeight];
                rgba.CopyPixels(output, stride, 0);
                var left = rgba.PixelWidth;
                var top = rgba.PixelHeight;
                var right = -1;
                var bottom = -1;
                for (var y = 0; y < rgba.PixelHeight; y++)
                {
                    for (var x = 0; x < rgba.PixelWidth; x++)
                    {
                        if (output[y * stride + x * 4 + 3] < 16) continue;
                        left = Math.Min(left, x);
                        top = Math.Min(top, y);
                        right = Math.Max(right, x);
                        bottom = Math.Max(bottom, y);
                    }
                }
                Assert(right - left + 1 >= rgba.PixelWidth / 2 && bottom - top + 1 >= rgba.PixelHeight / 2,
                    $"{size}px 图标可见区域 {right - left + 1}×{bottom - top + 1} 被装进 {rgba.PixelWidth}×{rgba.PixelHeight} 画布");
                // Shell 对新文件可能先返回较小帧；原始高清图是否应保留由上面的位图检查直接验证。
                Assert(rgba.PixelWidth >= 48, "Shell 回退帧应仍满足卡片图标的基本尺寸");
            }
        }
        finally
        {
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

    private static void TestTodoFontScale()
    {
        var state = new TodoWidgetState();
        TodoService.Add(state, "今天的任务", DateTime.Today);
        var window = new TodoWidgetWindow(state);
        ShowAndPump(window);
        DayEditWindow? editor = null;
        try
        {
            var list = Require<ItemsControl>(window, "TodoList");
            var plus = FindVisualChildren<Button>(window).Single(x => Equals(x.Content, "+"));
            double ListText() => FindVisualChildren<TextBox>(list).First(x => x.DataContext is TodoItemState).FontSize;
            Assert(ListText() == 14 && plus.FontSize == 20, "100% 时列表待办应为原来的 14 号");

            window.SetContentFontScale(1.5);
            Pump();
            Assert(ListText() == 21, "字号缩放 150% 时列表待办应为 14×1.5");
            Assert(plus.FontSize == 20, "按钮图标不随字号缩放（固定尺寸的框）");

            // 周历：格内任务、日期数字和星期表头一起按同一比例缩放，列表与周历的比例保持不变。
            Require<Button>(window, "ViewModeButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            var weekHost = Require<Grid>(window, "WeekHost");
            var cellText = FindVisualChildren<TextBlock>(weekHost)
                .First(x => x.DataContext is TodoItemState && x.IsVisible);
            Assert(cellText.FontSize == 18, "周历格内待办应为 12×1.5");
            Assert(FindVisualChildren<TextBlock>(weekHost).Any(x => Equals(x.Text, "周一") && x.FontSize == 11.5 * 1.5),
                "星期表头应随字号缩放");

            // 当日浮层与当日编辑窗也跟随同一比例。
            plus.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            var peekRow = FindVisualChildren<TextBox>(Require<ItemsControl>(window, "WeekPeekList")).First();
            Assert(peekRow.FontSize == 18 && Require<TextBox>(window, "WeekPeekInput").FontSize == 18
                    && Require<TextBlock>(window, "WeekPeekTitle").FontSize == 13 * 1.5,
                "当日浮层的标题、任务行与输入框应随字号缩放");

            const System.Reflection.BindingFlags Private =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var cells = (System.Collections.IDictionary)typeof(TodoWidgetWindow).GetField("_cellsByDate", Private)!
                .GetValue(window)!;
            // 当日编辑窗是模态对话框，要在它打开期间由计时器回调读取。
            WithDayEditor(window, dayEditor => Assert(
                Require<TextBox>(dayEditor, "Editor").FontSize == 13 * 1.5
                    && Require<TextBlock>(dayEditor, "DateTitle").FontSize == 15 * 1.5,
                "当日编辑窗的正文与日期标题应随字号缩放"));

            // 越界值按设置项本身的范围钳制。
            window.SetContentFontScale(5);
            Assert(window.ContentFontScale == DesktopExperienceRules.MaximumFontScale, "字号缩放应钳在设置项的上限");
        }
        finally
        {
            editor?.Close();
            Close(window);
        }

        if (Environment.GetEnvironmentVariable("BINGLAN_FONT_SCALE_PREVIEW_DIR") is { Length: > 0 } previewDir)
        {
            SaveFontScalePreviews(previewDir);
        }
    }

    private static void TestTodoFontFamily()
    {
        var state = new TodoWidgetState { WeeklyView = true };
        TodoService.Add(state, "今天的任务", DateTime.Today);
        var window = new TodoWidgetWindow(state);
        ShowAndPump(window);
        DayEditWindow? editor = null;
        try
        {
            var uiFont = (FontFamily)window.FindResource("UiFont");
            var chosen = InstalledFontCatalog.BundledNames.First();
            var expected = InstalledFontCatalog.Create(InstalledFontCatalog.Resolve(chosen)).Source;
            Assert(expected != uiFont.Source, "测试字体应与界面字体不同");
            var weekHost = Require<Grid>(window, "WeekHost");
            var plus = FindVisualChildren<Button>(window).Single(x => Equals(x.Content, "+"));
            string CellFont() => FindVisualChildren<TextBlock>(weekHost)
                .First(x => x.DataContext is TodoItemState && x.IsVisible).FontFamily.Source;
            Assert(CellFont() == uiFont.Source, "默认应使用界面字体");

            window.SetContentFontFamily(chosen);
            Pump();
            Assert(CellFont() == expected, "周历格内待办应改用所选字体");
            Assert(plus.FontFamily.Source == uiFont.Source, "按钮保持界面字体");

            // 当日浮层的任务行与输入框是 TextBox：全局 TextBox 样式固定了界面字体，需显式跟随。
            plus.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(FindVisualChildren<TextBox>(Require<ItemsControl>(window, "WeekPeekList")).First().FontFamily.Source == expected
                    && Require<TextBox>(window, "WeekPeekInput").FontFamily.Source == expected,
                "当日浮层的任务行与输入框应改用所选字体");

            const System.Reflection.BindingFlags Private =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var cells = (System.Collections.IDictionary)typeof(TodoWidgetWindow).GetField("_cellsByDate", Private)!
                .GetValue(window)!;
            WithDayEditor(window, dayEditor => Assert(
                Require<TextBox>(dayEditor, "Editor").FontFamily.Source == expected
                    && Require<TextBlock>(dayEditor, "DateTitle").FontFamily.Source == expected,
                "当日编辑窗的正文与日期标题应改用所选字体"));

            // 列表视图的待办编辑框同样跟随；改回默认后恢复界面字体。
            Require<Button>(window, "ViewModeButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            var listEditor = FindVisualChildren<TextBox>(Require<ItemsControl>(window, "TodoList"))
                .First(x => x.DataContext is TodoItemState);
            Assert(listEditor.FontFamily.Source == expected, "列表待办应改用所选字体");
            window.SetContentFontFamily(null);
            Pump();
            Assert(listEditor.FontFamily.Source == uiFont.Source, "选回默认后应恢复界面字体");
        }
        finally
        {
            editor?.Close();
            Close(window);
        }
    }

    // 设置页“桌面组件 → 今日待办 → 字号缩放”保存在 Todo 组件上；卡片启动时和设置改动后都要跟随。
    private static void TestTodoFontScaleFollowsSetting()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-todo-scale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var coordinator = new WidgetCoordinator(interactiveQa: true, dataDirectory: temp);
        const System.Reflection.BindingFlags Private =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        try
        {
            var appState = (AppState)typeof(WidgetCoordinator).GetField("_state", Private)!.GetValue(coordinator)!;
            foreach (var kind in new[]
                     {
                         DesktopComponentKind.TimeDate,
                         DesktopComponentKind.Greeting,
                         DesktopComponentKind.Weather,
                         DesktopComponentKind.Performance
                     })
            {
                appState.DesktopExperience.GetComponent(kind).IsVisible = false;
            }
            var todoComponent = appState.DesktopExperience.GetComponent(DesktopComponentKind.Todo);
            todoComponent.IsVisible = true;
            todoComponent.FontScale = 1.3;
            var chosen = InstalledFontCatalog.BundledNames.First();
            var expected = InstalledFontCatalog.Create(InstalledFontCatalog.Resolve(chosen)).Source;
            todoComponent.FontFamily = chosen;

            coordinator.Start();
            PumpUntil(() => Application.Current.Windows.OfType<TodoWidgetWindow>().Any(x => x.IsLoaded),
                "今日待办卡片应随启动打开");
            var todo = Application.Current.Windows.OfType<TodoWidgetWindow>().Single();
            Assert(Math.Abs(todo.ContentFontScale - 1.3) < 1e-9, "启动时卡片应读取已保存的字号缩放");
            Assert(todo.ContentFontFamily.Source == expected, "启动时卡片应读取已保存的字体");

            todoComponent.FontScale = 0.8;
            todoComponent.FontFamily = null;
            typeof(WidgetCoordinator).GetMethod("ApplyDesktopExperience", Private)!
                .Invoke(coordinator, [appState.DesktopExperience]);
            Pump();
            Assert(Math.Abs(todo.ContentFontScale - 0.8) < 1e-9, "设置页修改字号缩放后卡片应立即跟随");
            Assert(todo.ContentFontFamily.Source == ((FontFamily)todo.FindResource("UiFont")).Source,
                "设置页把字体改回默认后卡片应恢复界面字体");
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

    // 周历格、周范围和底部统计跟随“文字颜色”；当日浮层自带浅色底板，配色保持固定。
    private static void TestTodoTextColorFollowsAppearance()
    {
        var state = new TodoWidgetState { WeeklyView = true };
        TodoService.Add(state, "今天的任务", DateTime.Today);
        var window = new TodoWidgetWindow(state);
        ShowAndPump(window);
        try
        {
            window.SetWidgetTextColor("#FFFFFF");
            Pump();
            static Color ColorOf(Brush brush) => ((SolidColorBrush)brush).Color;
            var ink = ColorOf(window.WidgetTextBrush);
            var secondary = ColorOf(window.WidgetSecondaryTextBrush);
            Assert(ink == Colors.White, "文字颜色应为白色");
            var weekHost = Require<Grid>(window, "WeekHost");
            var cellText = FindVisualChildren<TextBlock>(weekHost).First(x => x.Text == "今天的任务");
            Assert(ColorOf(cellText.Foreground) == ink, "周历格内待办文字应跟随文字颜色");
            Assert(ColorOf(Require<TextBlock>(window, "WeekRange").Foreground) == ink, "周范围应跟随文字颜色");
            Assert(ColorOf(Require<TextBlock>(window, "SummaryText").Foreground) == secondary,
                "底部统计应跟随文字颜色（次要色），与官方 v0.2.33 一致");
            var chip = FindVisualChildren<Border>(weekHost)
                .First(x => x.DataContext is TodoItemState && Equals(x.Tag, "Interactive"));
            Assert(ReferenceEquals(chip.Background, window.FindResource("WidgetItemSurfaceBrush")),
                "周历条目底色应使用卡片的条目底色");
            Assert(ColorOf(Require<TextBlock>(window, "WeekPeekTitle").Foreground)
                    == (Color)ColorConverter.ConvertFromString("#FF243146"),
                "当日浮层自带浅色底板，标题保持固定深色");
        }
        finally
        {
            Close(window);
        }
        if (Environment.GetEnvironmentVariable("BINGLAN_TEXT_COLOR_PREVIEW_DIR") is { Length: > 0 } previewDir)
        {
            SaveTextColorPreviews(previewDir);
        }
    }

    // 三种外观下的周历隔离预览，铺在接近用户壁纸的蓝灰底色上，用于人工对照可读性。
    // “当前”与上一轮 字号缩放-周历-100.png 使用相同样例和尺寸，可直接前后对照。
    private static void SaveTextColorPreviews(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var (background, opacity, text, itemSurface, label) in new (string, double, string, string, string)[]
                 {
                     ("#ECEFF4", 0.26352941176470596, "#2E3440", "#7AFFFFFF", "当前外观"),
                     ("#18222F", 0.32, "#FFFFFF", "#1FFFFFFF", "深色玻璃白字"),
                     ("#FFFFFF", 0.01, "#FFFFFF", "#00FFFFFF", "无底板白字")
                 })
        {
            var state = new TodoWidgetState { Title = "今日待办", WeeklyView = true };
            TodoService.Add(state, "隔离样例：问一下辅导员助学金的事", DateTime.Today);
            TodoService.Add(state, "隔离样例：买礼品卡", DateTime.Today);
            TodoService.Add(state, "隔离样例：看论文", DateTime.Today.AddDays(1));
            var window = new TodoWidgetWindow(state) { Width = 1150, Height = 1080 };
            window.SetWidgetBackgroundColor(background);
            window.SetWidgetBackgroundOpacity(opacity);
            window.SetWidgetTextColor(text);
            window.Resources["WidgetItemSurfaceBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(itemSurface));
            ShowAndPump(window);
            try
            {
                window.UpdateLayout();
                Pump();
                var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
                var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
                var card = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                card.Render(window);
                var visual = new DrawingVisual();
                using (var context = visual.RenderOpen())
                {
                    context.DrawRectangle(
                        new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF7E9AAF")),
                        null,
                        new Rect(0, 0, width, height));
                    context.DrawImage(card, new Rect(0, 0, width, height));
                }
                var composed = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                composed.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(composed));
                using var stream = File.Create(Path.Combine(directory, $"文字颜色-周历-{label}.png"));
                encoder.Save(stream);
            }
            finally
            {
                Close(window);
            }
        }
    }

    // 隔离样例：同一份数据分别按 100%、150% 和“100% + 第一款内置字体”截取列表、周历与当日编辑窗，供与用户截图对照。
    private static void SaveFontScalePreviews(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var (scale, font, label) in new (double, string?, string)[]
                 {
                     (1d, null, "100"),
                     (1.5d, null, "150"),
                     (1d, InstalledFontCatalog.BundledNames.First(), "字体")
                 })
        {
            var state = new TodoWidgetState { Title = "今日待办" };
            TodoService.Add(state, "隔离样例：问一下辅导员助学金的事", DateTime.Today);
            TodoService.Add(state, "隔离样例：买礼品卡", DateTime.Today);
            TodoService.Add(state, "隔离样例：看论文", DateTime.Today.AddDays(1));
            var window = new TodoWidgetWindow(state) { Width = 1150, Height = 1080 };
            window.SetContentFontScale(scale);
            window.SetContentFontFamily(font);
            ShowAndPump(window);
            DayEditWindow? editor = null;
            try
            {
                var percent = label;
                SavePreview(window, Path.Combine(directory, $"字号缩放-列表-{percent}.png"));
                Require<Button>(window, "ViewModeButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                window.UpdateLayout();
                Pump();
                SavePreview(window, Path.Combine(directory, $"字号缩放-周历-{percent}.png"));
                const System.Reflection.BindingFlags Private =
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var cells = (System.Collections.IDictionary)typeof(TodoWidgetWindow).GetField("_cellsByDate", Private)!
                    .GetValue(window)!;
                WithDayEditor(window, dayEditor =>
                {
                    dayEditor.UpdateLayout();
                    Pump();
                    SavePreview(dayEditor, Path.Combine(directory, $"字号缩放-编辑窗-{percent}.png"));
                });
            }
            finally
            {
                editor?.Close();
                Close(window);
            }
        }
    }

    // 新建与启动时，待办卡立即使用“桌面组件 → 今日待办”的外观，不等下一次改设置。
    private static void TestTodoTakesComponentLookOnCreate()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-todo-look-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var coordinator = new WidgetCoordinator(interactiveQa: true, dataDirectory: temp);
        const System.Reflection.BindingFlags Private =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        try
        {
            var appState = (AppState)typeof(WidgetCoordinator).GetField("_state", Private)!.GetValue(coordinator)!;
            foreach (var kind in new[]
                     {
                         DesktopComponentKind.TimeDate,
                         DesktopComponentKind.Greeting,
                         DesktopComponentKind.Weather,
                         DesktopComponentKind.Performance
                     })
            {
                appState.DesktopExperience.GetComponent(kind).IsVisible = false;
            }
            var todoComponent = appState.DesktopExperience.GetComponent(DesktopComponentKind.Todo);
            todoComponent.IsVisible = true;
            todoComponent.Appearance.TextColor = "#FFFFFF";
            todoComponent.Appearance.BackgroundColor = "#18222F";
            todoComponent.CornerRadius = 6;

            static void AssertLook(TodoWidgetWindow todo, string what)
            {
                Assert(((SolidColorBrush)todo.WidgetTextBrush).Color == Colors.White, $"{what}应使用组件文字颜色");
                var body = ((SolidColorBrush)todo.WidgetBackgroundBrush).Color;
                Assert(body.R == 0x18 && body.G == 0x22 && body.B == 0x2F, $"{what}应使用组件玻璃颜色");
                Assert(Math.Abs(todo.WidgetCornerRadius - 6) < 1e-9, $"{what}应使用组件圆角");
            }

            coordinator.Start();
            PumpUntil(() => Application.Current.Windows.OfType<TodoWidgetWindow>().Any(x => x.IsLoaded),
                "今日待办卡片应随启动打开");
            AssertLook(Application.Current.Windows.OfType<TodoWidgetWindow>().Single(), "启动时的待办卡");

            typeof(WidgetCoordinator).GetMethod("AddTodo", Private)!.Invoke(coordinator, []);
            PumpUntil(() => Application.Current.Windows.OfType<TodoWidgetWindow>().Count(x => x.IsLoaded) == 2,
                "新建待办卡应打开");
            AssertLook(Application.Current.Windows.OfType<TodoWidgetWindow>().Last(), "新建的待办卡");
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

    // “信息密度”只对天气、性能生效，“字体”对只有图标的快捷入口无效；其余组件不显示这些选项。
    private static void TestComponentEditorHidesUnusedOptions()
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
            Require<ListBox>(window, "SettingsNavigationList").SelectedIndex = 1;
            Pump();
            var components = Require<ListBox>(window, "DesktopComponentList");
            var fontRow = Require<FrameworkElement>(window, "SelectedComponentFontRow");
            var densityRow = Require<FrameworkElement>(window, "SelectedComponentDensityRow");
            var seen = new HashSet<DesktopComponentKind>();
            foreach (var entry in components.Items.OfType<SettingsWindow.DesktopComponentSettingsEntry>().ToList())
            {
                if (entry.Kind is not { } kind)
                {
                    continue;
                }
                components.SelectedItem = entry;
                Pump();
                seen.Add(kind);
                var usesDensity = kind is DesktopComponentKind.Weather or DesktopComponentKind.Performance;
                Assert(densityRow.Visibility == (usesDensity ? Visibility.Visible : Visibility.Collapsed),
                    $"{kind} 的信息密度选项可见性不对");
                Assert(fontRow.Visibility == (kind == DesktopComponentKind.QuickLaunch ? Visibility.Collapsed : Visibility.Visible),
                    $"{kind} 的字体选项可见性不对");
            }
            Assert(seen.Contains(DesktopComponentKind.Performance) && seen.Contains(DesktopComponentKind.QuickLaunch)
                    && seen.Contains(DesktopComponentKind.Todo),
                "应覆盖性能、快捷入口与今日待办");
        }
        finally
        {
            window.Close();
            Pump();
        }
    }

    private static void TestTodoRowsStayAccessible()
    {
        var state = TodoService.CreateDefaultWidget();
        TodoService.Add(state, "第一条");
        TodoService.Add(state, "第二条");
        // 第三行稍后清空并保持光标，模拟正在输入的空白行：统计不应把它算进完成度。
        TodoService.Add(state, "第三条");
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

            var typing = FindVisualChildren<TextBox>(list)
                .Single(x => x.DataContext is TodoItemState { Text: "第三条" });
            window.Activate();
            Assert(typing.Focus(), "待办文字框无法聚焦");
            typing.Text = "";
            Pump();
            Assert(state.Items.Count == 3, "仍有输入光标的空白行不应被删除");
            Keyboard.ClearFocus();
            Pump();
            Assert(state.Items.Count == 2 && AccessibleRows() == 2, "光标离开后空白行应删除，其余行仍可访问");
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
        TodoService.Add(state, "初始文字");
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

    // 社区反馈的冻结配置：信息组件全部关闭、只留顶栏。顶栏的时间/天气/性能都靠共享
    // 采样服务的 Sampled 事件刷新，而原先启动它的只有信息卡片窗口的加载逻辑——卡片
    // 删光或从未创建时，顶栏从启动起就停止更新，重启也无法自愈。
    private static void TestTopBarOnlyKeepsSamplerAlive()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-sampler-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var coordinator = new WidgetCoordinator(interactiveQa: true, dataDirectory: temp);
        try
        {
            var state = typeof(WidgetCoordinator)
                .GetField("_state", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)
                ?.GetValue(coordinator) as AppState ??
                throw new InvalidOperationException("应用状态没有创建");
            foreach (var kind in new[]
                     {
                         DesktopComponentKind.TimeDate,
                         DesktopComponentKind.Greeting,
                         DesktopComponentKind.Weather,
                         DesktopComponentKind.Performance
                     })
            {
                state.DesktopExperience.GetComponent(kind).IsVisible = false;
            }
            state.TopBar.IsEnabled = true;

            coordinator.Start();
            // The shell steps ride the dispatcher queue; within the logon window they
            // also wait for the desktop to settle, so the wait outlasts that delay.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!SamplerOf(coordinator).IsRunning && DateTime.UtcNow < deadline)
            {
                Pump();
                Thread.Sleep(50);
            }
            Assert(
                SamplerOf(coordinator).IsRunning,
                "只开顶栏时共享采样服务应运行，顶栏时间/天气/性能才有刷新来源");
            Assert(
                Application.Current.Windows.OfType<TopBarWindow>().Any(window => window.IsLoaded),
                "顶栏窗口应随启动打开");

            state.TopBar.IsEnabled = false;
            InvokeApplyTopBar(coordinator);
            Pump();
            Assert(
                !SamplerOf(coordinator).IsRunning,
                "没有信息卡片且顶栏关闭后，采样服务应停止");

            state.TopBar.IsEnabled = true;
            InvokeApplyTopBar(coordinator);
            Pump();
            Assert(
                SamplerOf(coordinator).IsRunning,
                "重新启用顶栏后采样服务应随之恢复，不依赖任何卡片窗口");
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

    private static IPerformanceSamplingService SamplerOf(WidgetCoordinator coordinator) =>
        typeof(WidgetCoordinator)
            .GetField("_performanceSampler", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(coordinator) as IPerformanceSamplingService ??
        throw new InvalidOperationException("共享采样服务没有创建");

    private static void InvokeApplyTopBar(WidgetCoordinator coordinator) =>
        (typeof(WidgetCoordinator)
            .GetMethod("ApplyTopBar", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("找不到顶栏应用方法"))
        .Invoke(coordinator, null);

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

    // 一次窗口事件只触发一次“显示桌面”判定：合并用的计时器触发后必须自行停止，
    // 否则每次切换窗口都会留下一个每 150ms 枚举全部窗口的计时器（官方 v0.2.33 的 CPU 泄漏）。
    private static void TestDesktopSurfaceWatcherChecksOncePerBurst()
    {
        var checks = 0;
        var watcher = new DesktopSurfaceWatcher(() =>
        {
            checks++;
            return [];
        });
        const System.Reflection.BindingFlags Private =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var onWinEvent = typeof(DesktopSurfaceWatcher).GetMethod("OnWinEvent", Private)!;
        void WaitPumping(TimeSpan duration)
        {
            var until = DateTime.Now + duration;
            while (DateTime.Now < until)
            {
                Pump();
                Thread.Sleep(20);
            }
        }
        try
        {
            for (var burst = 0; burst < 3; burst++)
            {
                onWinEvent.Invoke(watcher, [nint.Zero, 0x0003u, nint.Zero, 0, 0, 0u, 0u]);
                onWinEvent.Invoke(watcher, [nint.Zero, 0x0008u, nint.Zero, 0, 0, 0u, 0u]);
                WaitPumping(TimeSpan.FromMilliseconds(400));
            }
            var afterBursts = checks;
            Assert(afterBursts == 3, $"三次事件突发应各判定一次，实际 {afterBursts} 次");
            WaitPumping(TimeSpan.FromSeconds(1));
            Assert(checks == afterBursts, $"没有新事件时不应继续判定，1 秒内又判定了 {checks - afterBursts} 次");
        }
        finally
        {
            watcher.Dispose();
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

            // Regression: interacting with the card (dragging it, clicking a box's
            // collapse toggle) activates it, and the later Deactivated sends it back —
            // during the raised state that must not drop it behind the desktop surface,
            // where it would stay hidden until the next "show desktop" toggle.
            window.SendToBack();
            Pump();
            Assert((GetWindowLongPtr(handle, GwlExStyle) & WsExTopmost) != 0,
                "显示桌面期间组件失活回落不应把它压回桌面表面之下");
            Assert(IsWindowVisible(handle), "显示桌面期间组件应保持可见");

            window.ReturnBelowOrdinaryWindows();
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

    // 回归：周历周数设置面板只对待办卡片显示（此前挂在 widget 分支、待办卡片
    // 走内置组件分支导致入口不可见），切换周数要立即写入卡片状态。
    private static void TestTodoWeekCountSettingPanel()
    {
        var initialState = new TodoWidgetState();
        var yesterdayItem = TodoService.Add(initialState, "昨日已完成", DateTime.Today.AddDays(-1));
        yesterdayItem.IsCompleted = true;
        TodoService.StampCompletion(yesterdayItem);
        var todo = new TodoWidgetWindow(initialState);
        var note = new NoteWidgetWindow(new NoteWidgetState());
        ShowAndPump(todo);
        ShowAndPump(note);
        try
        {
            var window = new SettingsWindow(
                new CityLookupService(),
                new InformationWidgetState(),
                DesktopExperienceRules.CreateDefault(),
                (_, _) => { },
                _ => { },
                _ => { },
                widgetProvider: () => new WidgetWindowBase[] { todo, note });
            ShowAndPump(window);
            try
            {
                Require<ListBox>(window, "SettingsNavigationList").SelectedIndex = 1;
                Pump();
                var list = Require<ListBox>(window, "DesktopComponentList");
                var panel = Require<StackPanel>(window, "TodoSettingsPanel");

                list.SelectedItem = list.Items.OfType<SettingsWindow.DesktopComponentSettingsEntry>()
                    .First(entry => entry.Window is TodoWidgetWindow);
                Pump();
                Assert(panel.IsVisible, "选中待办卡片应显示周历设置面板");

                var picker = Require<ComboBox>(window, "TodoWeekCountPicker");
                Assert(picker.SelectedIndex == 3, "周数回填应与卡片状态一致（默认 4 周）");

                // 列表模式下修改周历周数不应把列表小卡片撑高到周历尺寸。
                var listHeightBefore = todo.ActualHeight;
                picker.SelectedIndex = 4;
                Pump();
                Assert(todo.State.WeeklyWeekCount == 5, "列表模式下切换周数应立即写入卡片状态");
                Assert(Near(todo.ActualHeight, listHeightBefore), "列表模式下切换周历周数不应改变当前列表卡片高度");

                // 其他日期有已完成项但今日无已完成项时，列表视图“清理已完成”应保持禁用。
                var toggleViewBtn = Require<Button>(todo, "ViewModeButton");
                Assert(!Require<Button>(todo, "ClearCompletedButton").IsEnabled,
                    "仅其他日期存在已完成项时，今日列表的清理已完成按钮应保持禁用");

                // 切到周历视图検証 1 周窗口、+ 按钮打开 DayPeek、行内编辑与跨日撤销。
                toggleViewBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                picker.SelectedIndex = 0;
                Pump();
                Assert(todo.State.WeeklyWeekCount == 1, "切换周数应立即写入卡片状态");
                var monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
                var sunday = monday.AddDays(6);
                Assert(
                    Require<TextBlock>(todo, "WeekRange").Text ==
                        $"{monday:M月d日} – {sunday:M月d日}",
                    "1 周窗口应恰好覆盖本周（含今天）");

                // 周历模式下点击 + 应打开今天（或当前选中格）的 DayPeek 并聚焦输入框，不产生空白孤儿项。
                // 设置窗口与卡片同时打开，前台可能在两者间来回；卡片失活时浮层按“点别处即关闭”规则关闭。
                // 先激活卡片，之后每个依赖浮层的步骤前若浮层已因失活关闭，就重新激活并重开今天的浮层。
                todo.Activate();
                Pump();
                var countBeforePlus = todo.State.Items.Count;
                var addBtn = FindVisualChildren<Button>(todo).Single(x => Equals(x.Content, "+"));
                addBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                var peekOverlay = Require<Canvas>(todo, "WeekPeekOverlay");
                var peekInput = Require<TextBox>(todo, "WeekPeekInput");
                void KeepPeekOpen()
                {
                    if (peekOverlay.Visibility != Visibility.Visible)
                    {
                        Assert(!todo.IsActive, "卡片仍处于激活状态时浮层不应自行关闭");
                        todo.Activate();
                        addBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Pump();
                    }
                }
                Assert(peekOverlay.Visibility == Visibility.Visible, "周历模式下点击 + 应打开 DayPeek 浮层");
                Assert(todo.State.Items.Count == countBeforePlus, "周历模式下点击 + 不应直接插入空白待办");

                var peekInputSource = PresentationSource.FromVisual(peekInput)!;
                KeepPeekOpen();
                peekInput.Text = "周历首条任务";
                peekInput.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, peekInputSource, 0, Key.Enter)
                {
                    RoutedEvent = Keyboard.KeyDownEvent
                });
                Pump();
                KeepPeekOpen();
                peekInput.Text = "周历第二条任务";
                peekInput.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, peekInputSource, 0, Key.Enter)
                {
                    RoutedEvent = Keyboard.KeyDownEvent
                });
                Pump();
                var todayItems = TodoService.ItemsForDate(todo.State, DateTime.Today).ToList();
                Assert(todayItems.Count == 2 && todayItems[0].Text == "周历首条任务" && todayItems[1].Text == "周历第二条任务",
                    "DayPeek 输入框回车应向当前日期添加待办");

                // DayPeek 行内编辑：双击解锁，Esc 还原原文，Enter 提交修改并写回状态。
                KeepPeekOpen();
                var peekList = Require<ItemsControl>(todo, "WeekPeekList");
                var firstRowEditor = FindVisualChildren<TextBox>(peekList).First();
                static MouseButtonEventArgs DoubleClick()
                {
                    var evt = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    {
                        RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
                    };
                    typeof(MouseButtonEventArgs)
                        .GetProperty("ClickCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
                        .SetValue(evt, 2);
                    return evt;
                }
                firstRowEditor.RaiseEvent(DoubleClick());
                Pump();
                Assert(!firstRowEditor.IsReadOnly, "双击 DayPeek 文本应解锁行内编辑");
                firstRowEditor.Text = "放弃的修改";
                PressKey(firstRowEditor, Key.Escape);
                Pump();
                Assert(firstRowEditor.IsReadOnly && todayItems[0].Text == "周历首条任务",
                    "DayPeek 行内编辑按 Esc 应还原原文本并退出编辑");

                firstRowEditor.RaiseEvent(DoubleClick());
                Pump();
                firstRowEditor.Text = "已确认的周历任务";
                PressKey(firstRowEditor, Key.Enter);
                Pump();
                Assert(firstRowEditor.IsReadOnly && todo.State.Items.Single(x => x.Id == todayItems[0].Id).Text == "已确认的周历任务",
                    "DayPeek 行内编辑按 Enter 应提交修改并同步到 State");

                // 跨行切换编辑：第一行编辑中直接双击第二行，第一行修改应自动提交且第二行保持编辑态（Esc 可还原第二行）。
                KeepPeekOpen();
                var rowEditors = FindVisualChildren<TextBox>(peekList).ToList();
                rowEditors[0].RaiseEvent(DoubleClick());
                Pump();
                rowEditors[0].Text = "切行自动保存任务";
                rowEditors[1].RaiseEvent(DoubleClick());
                Pump();
                Assert(rowEditors[0].IsReadOnly && !rowEditors[1].IsReadOnly && todayItems[0].Text == "切行自动保存任务",
                    "从第一行直接双击第二行时，第一行应提交并切入第二行编辑态");
                rowEditors[1].Text = "第二行临时改动";
                PressKey(rowEditors[1], Key.Escape);
                Pump();
                Assert(rowEditors[1].IsReadOnly && todayItems[1].Text == "周历第二条任务",
                    "切入第二行后按 Esc 应正确还原第二行原文");

                // 跨日移动与单步撤销（含多日穿插全局 Order）：插入第三条后移动今日第 1 条与第 2 条验证日内下标精确还原。
                KeepPeekOpen();
                peekInput.Text = "周历第三条任务";
                peekInput.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, peekInputSource, 0, Key.Enter)
                {
                    RoutedEvent = Keyboard.KeyDownEvent
                });
                Pump();
                todayItems = TodoService.ItemsForDate(todo.State, DateTime.Today).ToList();
                var moveTargetDate = DateTime.Today == sunday ? DateTime.Today.AddDays(-1) : DateTime.Today.AddDays(1);
                var moveMethod = typeof(TodoWidgetWindow).GetMethod(
                    "MoveItemToDate",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                moveMethod.Invoke(todo, new object?[] { todayItems[1], moveTargetDate, false, null });
                Pump();
                var moveToast = Require<Border>(todo, "MoveToast");
                Assert(moveToast.Visibility == Visibility.Visible && todayItems[1].Date == moveTargetDate,
                    "跨日移动后应显示撤销 Toast 并更新日期");
                var undoBtn = FindVisualChildren<Button>(moveToast).Single(x => Equals(x.Content, "撤销"));
                undoBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                var restoredTodayItems = TodoService.ItemsForDate(todo.State, DateTime.Today).ToList();
                Assert(
                    moveToast.Visibility == Visibility.Collapsed
                        && todo.State.Items.Any(x => x.Id == yesterdayItem.Id)
                        && restoredTodayItems.Count == 3
                        && restoredTodayItems[0].Id == todayItems[0].Id
                        && restoredTodayItems[1].Id == todayItems[1].Id
                        && restoredTodayItems[2].Id == todayItems[2].Id
                        && todo.State.Items.OrderBy(x => x.Order).Select(x => x.Order)
                            .SequenceEqual(Enumerable.Range(0, todo.State.Items.Count)),
                    "点击撤销应保留其他日期条目、恢复原日期与原日内第 2 行的位置顺序、保持全局 Order 连续唯一，且不再重复弹出 Toast");

                // 浮层保持打开时切换周数（4 周 → 1 周）：浮层应保持打开且坐标与高度被钳制在 WeekHost 内。
                picker.SelectedIndex = 3;
                Pump();
                picker.SelectedIndex = 0;
                Pump();
                KeepPeekOpen();
                var weekHost = Require<Grid>(todo, "WeekHost");
                var peekPanel = Require<Border>(todo, "WeekPeekPanel");
                Assert(
                    peekOverlay.Visibility == Visibility.Visible
                        && Canvas.GetTop(peekPanel) + peekPanel.ActualHeight <= weekHost.ActualHeight + 1.0,
                    "DayPeek 保持打开期间缩小周数后，浮层应重新锚定且不超出周历宿主底部边界");

                // 周历 DayPeek 完成自动下滑：勾选浮层首条待办后应沉底到当日列表末尾，取消勾选后恢复到未完成待办末尾。
                KeepPeekOpen();
                var peekListControl = Require<ItemsControl>(todo, "WeekPeekList");
                var firstPeekItem = TodoService.ItemsForDate(todo.State, DateTime.Today)[0];
                var firstPeekCheck = FindVisualChildren<CheckBox>(peekListControl)
                    .First(cb => cb.DataContext is TodoItemState item && item.Id == firstPeekItem.Id);
                firstPeekCheck.IsChecked = true;
                Pump();
                var peekIdsAfterSink = peekListControl.Items.OfType<TodoItemState>().Select(x => x.Id).ToList();
                Assert(
                    peekIdsAfterSink[^1] == firstPeekItem.Id,
                    "周历 DayPeek 勾选完成待办后，该待办应自动下滑到当日列表末尾");
                firstPeekCheck.IsChecked = false;
                Pump();

                // 周历菜单调序（MoveDown_Click）→ 切回列表视图：今日列表容器顺序应立即按新顺序更新。
                var firstTodayBeforeMove = TodoService.ItemsForDate(todo.State, DateTime.Today)[0];
                var secondTodayBeforeMove = TodoService.ItemsForDate(todo.State, DateTime.Today)[1];
                var moveDownItem = new MenuItem { DataContext = firstTodayBeforeMove };
                typeof(TodoWidgetWindow)
                    .GetMethod("MoveDown_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(todo, [moveDownItem, new RoutedEventArgs()]);
                Pump();
                var viewModeBtn = Require<Button>(todo, "ViewModeButton");
                viewModeBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                var todoListControl = Require<ItemsControl>(todo, "TodoList");
                var visibleTodayIds = todoListControl.Items.OfType<TodoItemState>()
                    .Where(x => TodoService.BelongsToTodayList(x, DateTime.Today))
                    .Select(x => x.Id)
                    .ToList();
                Assert(
                    !todo.State.WeeklyView
                        && visibleTodayIds.Count >= 2
                        && visibleTodayIds[0] == secondTodayBeforeMove.Id
                        && visibleTodayIds[1] == firstTodayBeforeMove.Id,
                    "周历菜单下移今日条目后切回列表视图，列表绑定集合应立即显示更新后的顺序");

                // 列表视图完成自动下滑：勾选首条可见今日待办后，该条应自动沉底到未完成条目之后。
                var firstTodayCheck = FindVisualChildren<CheckBox>(todoListControl)
                    .First(cb => cb.DataContext is TodoItemState item && item.Id == secondTodayBeforeMove.Id);
                firstTodayCheck.IsChecked = true;
                Pump();
                var todayIdsAfterSink = todoListControl.Items.OfType<TodoItemState>()
                    .Where(x => TodoService.BelongsToTodayList(x, DateTime.Today))
                    .Select(x => x.Id)
                    .ToList();
                Assert(
                    todayIdsAfterSink[^1] == secondTodayBeforeMove.Id,
                    "列表视图勾选完成待办后，该待办应自动下滑到今日列表末尾");
                firstTodayCheck.IsChecked = false;
                Pump();

                // 列表视图 300ms 停顿上浮与上下拖拽移动：首条可见今日待办上浮后向下拖动越过邻行，松开后顺序交换且主题底色绑定保持完整。
                var currentTodayItems = todoListControl.Items.OfType<TodoItemState>()
                    .Where(x => TodoService.BelongsToTodayList(x, DateTime.Today))
                    .ToList();
                var dragFirstItem = currentTodayItems[0];
                var dragSecondItem = currentTodayItems[1];
                var firstContainer = (ContentPresenter)todoListControl.ItemContainerGenerator.ContainerFromItem(dragFirstItem);
                var secondContainer = (ContentPresenter)todoListControl.ItemContainerGenerator.ContainerFromItem(dragSecondItem);
                var firstCenterInList = firstContainer.TranslatePoint(new Point(10, firstContainer.ActualHeight / 2), todoListControl);
                var secondCenterInList = secondContainer.TranslatePoint(new Point(10, secondContainer.ActualHeight / 2), todoListControl);
                typeof(TodoWidgetWindow)
                    .GetField("_peekDwellAnchor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(todo, firstCenterInList);
                typeof(TodoWidgetWindow)
                    .GetField("_peekDwellSource", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(todo, firstContainer);
                typeof(TodoWidgetWindow)
                    .GetMethod("PeekDwellTimer_Tick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(todo, [null, EventArgs.Empty]);
                Pump();
                Assert(
                    Panel.GetZIndex(firstContainer) == 100
                        && firstContainer.RenderTransform is TranslateTransform { Y: < 0 },
                    "列表视图停顿满 300ms 后待办行应进入上浮态（ZIndex=100 且向上微抬）");
                typeof(TodoWidgetWindow)
                    .GetMethod("DoPeekDragMove", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(todo, [secondCenterInList]);
                typeof(TodoWidgetWindow)
                    .GetMethod("SettlePeekDrag", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(todo, []);
                Pump();
                var todayIdsAfterDrag = todoListControl.Items.OfType<TodoItemState>()
                    .Where(x => TodoService.BelongsToTodayList(x, DateTime.Today))
                    .Select(x => x.Id)
                    .ToList();
                Assert(
                    todayIdsAfterDrag[0] == dragSecondItem.Id && todayIdsAfterDrag[1] == dragFirstItem.Id,
                    "列表视图拖拽落位后应交换待办顺序并写回状态");

                // 列表视图上浮后取消（CancelPeekDrag）与上浮后不拖动直接松开（进入文本编辑聚焦）：
                var postDragFirstContainer = (ContentPresenter)todoListControl.ItemContainerGenerator.ContainerFromItem(dragSecondItem);
                var postDragCenter = postDragFirstContainer.TranslatePoint(new Point(10, postDragFirstContainer.ActualHeight / 2), todoListControl);
                typeof(TodoWidgetWindow)
                    .GetField("_peekDwellAnchor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(todo, postDragCenter);
                typeof(TodoWidgetWindow)
                    .GetField("_peekDwellSource", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(todo, postDragFirstContainer);
                typeof(TodoWidgetWindow)
                    .GetMethod("PeekDwellTimer_Tick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(todo, [null, EventArgs.Empty]);
                Pump();
                typeof(TodoWidgetWindow)
                    .GetMethod("CancelPeekDrag", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(todo, []);
                Pump();
                Assert(
                    Panel.GetZIndex(postDragFirstContainer) == 0
                        && postDragFirstContainer.RenderTransform == Transform.Identity,
                    "取消列表上浮后容器应恢复 ZIndex=0 与 Identity 变换");

                typeof(TodoWidgetWindow)
                    .GetField("_peekDwellAnchor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(todo, postDragCenter);
                typeof(TodoWidgetWindow)
                    .GetField("_peekDwellSource", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(todo, postDragFirstContainer);
                typeof(TodoWidgetWindow)
                    .GetMethod("PeekDwellTimer_Tick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(todo, [null, EventArgs.Empty]);
                typeof(TodoWidgetWindow)
                    .GetField("_peekContainerClickPoint", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(todo, new Point(40, postDragFirstContainer.ActualHeight / 2));
                typeof(TodoWidgetWindow)
                    .GetMethod("SettlePeekDrag", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(todo, []);
                Pump();
                var rowEditor = FindVisualChildren<TextBox>(postDragFirstContainer).First();
                Assert(
                    rowEditor.IsFocused || ReferenceEquals(FocusManager.GetFocusedElement(todo), rowEditor),
                    "列表视图上浮后未拖动直接松开，应将焦点交还给该行文本输入框");

                list.SelectedItem = list.Items.OfType<SettingsWindow.DesktopComponentSettingsEntry>()
                    .First(entry => entry.Window is NoteWidgetWindow);
                Pump();
                Assert(!panel.IsVisible, "选中非待办卡片应隐藏周历设置面板");
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            Close(todo);
            Close(note);
        }
    }

    private static WeekCellView TodoDayCell(TodoWidgetWindow todo, DateTime? date = null) =>
        Require<ItemsControl>(todo, "WeekRows").Items.OfType<WeekRowView>()
            .SelectMany(row => row.Days)
            .Single(cell => !cell.IsFiller && cell.Date == (date ?? DateTime.Today));

    private static void WithDayEditor(TodoWidgetWindow todo, Action<DayEditWindow> action)
    {
        Exception? failure = null;
        var completed = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                action(todo.OwnedWindows.OfType<DayEditWindow>().Single());
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                todo.OwnedWindows.OfType<DayEditWindow>().FirstOrDefault()?.Close();
                completed = true;
            }
        };
        timer.Start();
        typeof(TodoWidgetWindow).GetMethod("OpenDayEditor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(todo, [TodoDayCell(todo)]);
        PumpUntil(() => completed, "日期编辑对话框未完成交互");
        if (failure is not null)
        {
            throw failure;
        }
    }

    private static void TestTodoBlankRowsPruned()
    {
        var state = new TodoWidgetState();
        TodoService.Add(state, "保留的待办");
        TodoService.Add(state, "");
        TodoService.Add(state, "  ", DateTime.Today.AddDays(-1));
        var window = new TodoWidgetWindow(state);
        ShowAndPump(window);
        try
        {
            Assert(state.Items.Count == 1 && state.Items[0].Text == "保留的待办",
                "加载时应删除今日与历史日期中遗留的空白待办");
            var list = Require<ItemsControl>(window, "TodoList");
            window.Activate();

            // + 新建的空白行在获得光标期间保留，光标离开后自动删除。
            var add = FindVisualChildren<Button>(window).Single(x => Equals(x.Content, "+"));
            add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(state.Items.Count == 2, "+ 新建的空白行在输入期间不应被删除");
            var blankEditor = FindVisualChildren<TextBox>(list)
                .Single(x => x.DataContext is TodoItemState { Text: "" });
            Assert(blankEditor.Focus(), "新建行的文字框无法聚焦");
            Pump();
            Assert(state.Items.Count == 2, "仍有输入光标的空白行不应被删除");
            Keyboard.ClearFocus();
            Pump();
            Assert(state.Items.Count == 1 && FindVisualChildren<TextBox>(list).Count() == 1,
                "光标离开后空白行应从状态和列表中同时删除");

            // 已有文字被删空时：仍在输入则保留，按 Enter 结束后删除。
            var editor = FindVisualChildren<TextBox>(list).Single();
            Assert(editor.Focus(), "待办文字框无法聚焦");
            editor.Text = "";
            Pump();
            Assert(state.Items.Count == 1, "删空文字但仍在输入时不应删除");
            editor.RaiseEvent(new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(editor),
                0,
                Key.Enter)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            });
            Pump();
            Assert(state.Items.Count == 0, "Enter 结束输入后删空的待办应自动删除");
            Assert(Require<TextBlock>(window, "SummaryText").Text == "写下第一条待办", "删空后统计应回到空状态");

            // 周历 DayPeek：双击编辑删空并按 Enter 后，格内与今日列表同时消失。
            var toggleView = Require<Button>(window, "ViewModeButton");
            toggleView.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            var peekInput = Require<TextBox>(window, "WeekPeekInput");
            peekInput.Text = "周历里删空的任务";
            peekInput.RaiseEvent(new KeyEventArgs(
                Keyboard.PrimaryDevice, PresentationSource.FromVisual(peekInput)!, 0, Key.Enter)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            });
            Pump();
            Assert(state.Items.Count == 1, "DayPeek 输入框应新增一条待办");
            var peekList = Require<ItemsControl>(window, "WeekPeekList");
            var peekEditor = FindVisualChildren<TextBox>(peekList).Single();
            var doubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
            };
            typeof(MouseButtonEventArgs)
                .GetProperty("ClickCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(doubleClick, 2);
            peekEditor.RaiseEvent(doubleClick);
            Pump();
            Assert(!peekEditor.IsReadOnly, "双击 DayPeek 文本应进入编辑");
            peekEditor.Text = "";
            Pump();
            Assert(state.Items.Count == 1, "DayPeek 删空但仍在输入时不应删除");
            PressKey(peekEditor, Key.Enter);
            Pump();
            Assert(
                state.Items.Count == 0
                    && !FindVisualChildren<TextBox>(peekList).Any()
                    && !TodoService.ItemsForDate(state, DateTime.Today).Any(),
                "DayPeek 删空并结束编辑后，待办应从周历格与状态中删除");
        }
        finally
        {
            Close(window);
        }

        if (Environment.GetEnvironmentVariable("BINGLAN_BLANK_PRUNE_PREVIEW_DIR") is { Length: > 0 } previewDir)
        {
            SaveBlankPrunePreviews(previewDir);
        }
    }

    private static void TestTodoDayPeekClosesOnOutsideClick()
    {
        var state = new TodoWidgetState { WeeklyView = true };
        TodoService.Add(state, "浮层里的任务", DateTime.Today);
        var window = new TodoWidgetWindow(state);
        ShowAndPump(window);
        try
        {
            var overlay = Require<Canvas>(window, "WeekPeekOverlay");
            var add = FindVisualChildren<Button>(window).Single(x => Equals(x.Content, "+"));
            void OpenPeek()
            {
                add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump();
                Assert(overlay.Visibility == Visibility.Visible, "周历中点 + 应打开当日浮层");
            }
            // 左键按下的 Preview/普通事件都是 Direct 路由，由 Mouse.PreviewMouseDown（隧道）
            // 与 Mouse.MouseDown（冒泡）沿途逐元素拆出；直接触发左键事件到不了窗口。
            static void Press(UIElement target, RoutedEvent routedEvent) =>
                target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = routedEvent
                });

            // 浮层内部的点击（输入框）不关闭浮层。
            OpenPeek();
            Press(Require<TextBox>(window, "WeekPeekInput"), Mouse.PreviewMouseDownEvent);
            Pump();
            Assert(overlay.Visibility == Visibility.Visible, "点浮层内部不应关闭浮层");

            // 点别的日期格：先关闭浮层，点击照常落到格子上（选中该格）。
            var otherDate = DateTime.Today.DayOfWeek == DayOfWeek.Sunday
                ? DateTime.Today.AddDays(-1)
                : DateTime.Today.AddDays(1);
            var otherCell = FindVisualChildren<Border>(Require<Grid>(window, "WeekHost"))
                .First(x => x.DataContext is WeekCellView { IsFiller: false } cell && cell.Date == otherDate
                    && Equals(x.Tag, "Interactive"));
            Press(otherCell, Mouse.PreviewMouseDownEvent);
            Press(otherCell, Mouse.MouseDownEvent);
            Pump();
            Assert(overlay.Visibility == Visibility.Collapsed, "点浮层外的日期格应关闭浮层");
            Assert(((WeekCellView)otherCell.DataContext).IsSelected, "关闭浮层的那次点击应照常选中被点的日期格");

            // 撤销提示条在浮层外，点它不应关闭浮层。
            OpenPeek();
            var toast = Require<Border>(window, "MoveToast");
            toast.Visibility = Visibility.Visible;
            Pump();
            Press(toast, Mouse.PreviewMouseDownEvent);
            Pump();
            Assert(overlay.Visibility == Visibility.Visible, "点撤销提示条不应关闭浮层");
            toast.Visibility = Visibility.Collapsed;

            // 点卡片外（卡片失去激活）也关闭浮层；正在编辑且已删空的那一行随之删除。
            // 先点回今天的格子，让 + 打开有任务的今天浮层。
            var todayCell = FindVisualChildren<Border>(Require<Grid>(window, "WeekHost"))
                .First(x => x.DataContext is WeekCellView { IsFiller: false } cell && cell.Date == DateTime.Today
                    && Equals(x.Tag, "Interactive"));
            Press(todayCell, Mouse.PreviewMouseDownEvent);
            Press(todayCell, Mouse.MouseDownEvent);
            Pump();
            Assert(overlay.Visibility == Visibility.Collapsed, "点浮层外的今天格应关闭浮层");
            OpenPeek();
            var peekEditor = FindVisualChildren<TextBox>(Require<ItemsControl>(window, "WeekPeekList")).Single();
            var doubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
            };
            typeof(MouseButtonEventArgs)
                .GetProperty("ClickCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(doubleClick, 2);
            peekEditor.RaiseEvent(doubleClick);
            Pump();
            Assert(!peekEditor.IsReadOnly, "双击浮层文本应进入编辑");
            peekEditor.Text = "";
            typeof(Window).GetMethod("OnDeactivated",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [EventArgs.Empty]);
            Pump();
            Assert(overlay.Visibility == Visibility.Collapsed, "卡片失去激活时应关闭浮层");
            Assert(state.Items.Count == 0, "失去激活时，浮层里正在编辑且已删空的待办应删除");
        }
        finally
        {
            Close(window);
        }
    }

    // 列表行正在编辑时按住拖动是选中文字：该行不上浮；别的行已上浮时 Esc 交给编辑框。
    private static void TestTodoEditingTextKeepsSelection()
    {
        var state = new TodoWidgetState();
        TodoService.Add(state, "第一条");
        TodoService.Add(state, "第二条");
        var window = new TodoWidgetWindow(state);
        ShowAndPump(window);
        const System.Reflection.BindingFlags Private =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        try
        {
            var list = Require<ItemsControl>(window, "TodoList");
            var items = list.Items.OfType<TodoItemState>().ToList();
            var first = (ContentPresenter)list.ItemContainerGenerator.ContainerFromItem(items[0]);
            var second = (ContentPresenter)list.ItemContainerGenerator.ContainerFromItem(items[1]);
            var editor = FindVisualChildren<TextBox>(first).First(x => x.DataContext is TodoItemState);
            void Dwell(ContentPresenter container, DependencyObject source)
            {
                typeof(TodoWidgetWindow).GetField("_peekDwellAnchor", Private)!
                    .SetValue(window, container.TranslatePoint(new Point(10, container.ActualHeight / 2), list));
                typeof(TodoWidgetWindow).GetField("_peekDwellSource", Private)!.SetValue(window, source);
                typeof(TodoWidgetWindow).GetMethod("PeekDwellTimer_Tick", Private)!
                    .Invoke(window, [null, EventArgs.Empty]);
                Pump();
            }

            window.Activate();
            Keyboard.Focus(editor);
            Pump();
            Assert(editor.IsKeyboardFocusWithin, "测试前提：第一行编辑框持有键盘焦点");
            Dwell(first, editor);
            Assert(Panel.GetZIndex(first) == 0, "在正在编辑的文字上停顿不应上浮该行");

            Dwell(second, second);
            Assert(Panel.GetZIndex(second) == 100, "未在编辑的行停顿后照常上浮");
            var esc = new System.Windows.Input.KeyEventArgs(
                Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor)!, Environment.TickCount, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            editor.RaiseEvent(esc);
            Pump();
            Assert(Panel.GetZIndex(second) == 0, "Esc 应取消上浮");
            Assert(!esc.Handled, "有行上浮时 Esc 不应被吞掉，要交给正在编辑的文本框还原");
        }
        finally
        {
            Close(window);
        }
    }

    // 隔离样例：今日与昨日各混入空白条目，加载后截取列表与周历，供与用户截图对照。
    private static void SaveBlankPrunePreviews(string directory)
    {
        var state = new TodoWidgetState { Title = "今日待办" };
        var yesterday = DateTime.Today.AddDays(-1);
        TodoService.Add(state, "隔离样例：第一项");
        TodoService.Add(state, "");
        TodoService.Add(state, "隔离样例：第二项");
        TodoService.Add(state, "   ");
        var done = TodoService.Add(state, "隔离样例：已完成");
        done.IsCompleted = true;
        TodoService.StampCompletion(done);
        TodoService.Add(state, "", yesterday);
        TodoService.Add(state, "", yesterday);
        TodoService.Add(state, "隔离样例：昨日任务", yesterday);
        // 与用户截图同尺寸，便于并排对照。
        var window = new TodoWidgetWindow(state) { Width = 1150, Height = 1080 };
        ShowAndPump(window);
        try
        {
            Assert(state.Items.Count == 4, "预览样例加载后应只剩四条有内容的待办");
            Directory.CreateDirectory(directory);
            SavePreview(window, Path.Combine(directory, "空白待办删除-列表-修复后隔离.png"));
            Require<Button>(window, "ViewModeButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            window.UpdateLayout();
            Pump();
            SavePreview(window, Path.Combine(directory, "空白待办删除-周历-修复后隔离.png"));
        }
        finally
        {
            Close(window);
        }
    }

    private static void TestTodoDayEditorPersistence()
    {
        foreach (var captureBeforeClose in new[] { false, true })
        {
            var state = new TodoWidgetState { WeeklyView = true };
            var item = TodoService.Add(state, "原任务", DateTime.Today);
            var todo = new TodoWidgetWindow(state);
            ShowAndPump(todo);
            try
            {
                WithDayEditor(todo, editor =>
                {
                    Require<TextBox>(editor, "Editor").Text = "1. 保存的任务";
                    if (captureBeforeClose)
                    {
                        todo.CaptureState();
                        Assert(state.Items.Single().Text == "保存的任务",
                            "状态保存前应提交仍打开的日期编辑窗内容");
                        // Further edits after an intermediate save must also commit on close.
                        Require<TextBox>(editor, "Editor").Text = "1. 关闭时的任务";
                    }
                    todo.CanClose = true;
                    todo.Close();
                });
                Assert(state.Items.Single().Id == item.Id
                    && state.Items.Single().Text == (captureBeforeClose ? "关闭时的任务" : "保存的任务"),
                    "父卡片关闭应提交日期编辑窗内容并保留条目标识");
            }
            finally
            {
                if (todo.IsVisible) Close(todo);
            }
        }
    }

    private static void TestTodoDayEditorExclusive()
    {
        var state = new TodoWidgetState { WeeklyView = true };
        var original = TodoService.Add(state, "原任务", DateTime.Today);
        var todo = new TodoWidgetWindow(state);
        ShowAndPump(todo);
        try
        {
            var handle = new WindowInteropHelper(todo).Handle;
            WithDayEditor(todo, editor =>
            {
                Assert(!IsWindowEnabled(handle), "日期编辑期间应禁用父卡片，防止并行新增被旧文本覆盖");
                Require<TextBox>(editor, "Editor").Text = "1. 修改的任务";
            });
            Assert(IsWindowEnabled(handle), "关闭日期编辑对话框后应恢复父卡片交互");
            Assert(state.Items.Single().Id == original.Id && state.Items.Single().Text == "修改的任务",
                "对话框应提交文本并保留原条目");
            var add = FindVisualChildren<Button>(todo).Single(button => Equals(button.Content, "+"));
            add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            var input = Require<TextBox>(todo, "WeekPeekInput");
            input.Text = "随后新增的任务";
            input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(input)!, 0, Key.Enter)
            {
                RoutedEvent = Keyboard.KeyDownEvent
            });
            Pump();
            Assert(state.Items.Count == 2 && state.Items.Any(item => item.Text == "随后新增的任务"),
                "关闭编辑对话框后应可新增待办且保留已有内容");
        }
        finally
        {
            Close(todo);
        }
    }

    private static void TestTodoPeekBounds()
    {
        var todo = new TodoWidgetWindow(new TodoWidgetState { WeeklyView = true });
        ShowAndPump(todo);
        try
        {
            var cells = Require<ItemsControl>(todo, "WeekRows").Items.OfType<WeekRowView>()
                .SelectMany(row => row.Days).Where(cell => !cell.IsFiller).ToList();
            typeof(TodoWidgetWindow).GetMethod("OpenDayPeek",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(todo, [cells.MaxBy(cell => cell.Date)!, null]);
            Pump();
            var input = Require<TextBox>(todo, "WeekPeekInput");
            for (var i = 0; i < 10; i++)
            {
                input.Text = $"任务 {i + 1}";
                input.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(input)!, 0, Key.Enter)
                {
                    RoutedEvent = Keyboard.KeyDownEvent
                });
                Pump();
            }
            AssertPeekInsideHost();
            todo.Width += 80;
            todo.Height += 60;
            Pump();
            todo.Width = todo.MinWidth;
            todo.Height = todo.MinHeight;
            Pump();
            AssertPeekInsideHost();

            void AssertPeekInsideHost()
            {
                var host = Require<Grid>(todo, "WeekHost");
                var panel = Require<Border>(todo, "WeekPeekPanel");
                var top = Canvas.GetTop(panel);
                var left = Canvas.GetLeft(panel);
                Assert(top >= 0 && top + panel.ActualHeight <= host.ActualHeight + 1
                    && left >= 0 && left + panel.ActualWidth <= host.ActualWidth + 1,
                    "新增条目或调整卡片尺寸后，详情浮层应完整保留在周历内");
                var inputBounds = input.TransformToAncestor(host)
                    .TransformBounds(new Rect(0, 0, input.ActualWidth, input.ActualHeight));
                Assert(inputBounds.Bottom <= host.ActualHeight + 1, "详情浮层的新增输入框应保持可见");
            }
        }
        finally
        {
            Close(todo);
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
            var startEntry = Require<CheckBox>(window, "DockStartMenuButtonCheckBox");
            Assert(startEntry.IsChecked == true, "开始菜单入口默认应开启");
            startEntry.IsChecked = false;
            Pump();
            Assert(!dock.ShowStartMenuButton && dockApplied == 3, "关闭开始菜单入口应保存并立即应用");
            startEntry.IsChecked = true;
            Pump();
            Assert(dock.ShowStartMenuButton && dockApplied == 4, "重新开启入口应立即应用");
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

    // Same walk, but only over what is actually rendered. XAML-declared
    // controls stay in the visual tree while Visibility=Collapsed, so plain
    // tree walks double-count hidden panels (the day peek overlay ships its
    // own input box and toast). Screen readers skip hidden content; these
    // queries must too.
    private static IEnumerable<T> FindVisibleChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { IsVisible: false })
            {
                continue;
            }
            if (child is T result)
            {
                yield return result;
            }
            foreach (var descendant in FindVisibleChildren<T>(child))
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
        if (Environment.GetEnvironmentVariable("BINGLAN_UI_TEST_FILTER") is { Length: > 0 } filter
            && !name.Contains(filter, StringComparison.Ordinal)) return;
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
