using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.App.Windows;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.TopBar;

/// <summary>
/// Drives the top bar without a mouse: window style, module layout, deep-link clicks and
/// the module switches. The tests always use smart-hide mode so no real AppBar is
/// registered on the machine running the suite.
/// </summary>
internal static class TopBarWindowTests
{
    private const int GwlExStyle = -20;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal static void Run(Action<Window> show, Action pump)
    {
        ShowModulesAndStyles(show, pump);
        ModuleSwitchesRemoveButtons(show, pump);
        ClicksOpenSettings(show, pump);
        SurfacePaintsGlass(show, pump);
        TodoFlyoutShowsItemsAndCloses(show, pump);
        WeatherHoverOpensTooltip(show, pump);
    }

    private static void WeatherHoverOpensTooltip(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        using var sampler = new WindowsPerformanceSamplingService();
        var environment = new FakeEnvironment
        {
            Weather = new WeatherSnapshot(
                WeatherStatus.Fresh, "北京", 18.4d, 24d, 11d, 42, "多云", false,
                DateTimeOffset.Now, null)
        };
        var bar = new TopBarWindow(state, new DesktopStyleState(), environment, sampler);
        show(bar);
        try
        {
            var weather = ElementByName(bar, "天气模块");
            var toolTip = (weather as Border)?.ToolTip as ToolTip
                ?? throw new InvalidOperationException("天气模块应带悬停详情 tooltip");

            // WPF's own tool tips never open in this no-activate window; the bar opens
            // them itself after the pointer rests, and closes them on leave.
            weather.RaiseEvent(new System.Windows.Input.MouseEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0)
            {
                RoutedEvent = UIElement.MouseEnterEvent
            });
            var opened = false;
            for (var i = 0; i < 24 && !opened; i++)
            {
                pump();
                Thread.Sleep(50);
                opened = toolTip.IsOpen;
            }
            Assert(opened, "指针停留后天气悬停详情应打开");

            weather.RaiseEvent(new System.Windows.Input.MouseEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0)
            {
                RoutedEvent = UIElement.MouseLeaveEvent
            });
            pump();
            Assert(!toolTip.IsOpen, "指针离开后天气悬停详情应收起");
        }
        finally
        {
            bar.Close();
            pump();
        }
    }

    private static void SurfacePaintsGlass(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        state.FollowCardLook = false;
        // A light custom colour keeps the bar on the plain-brush path: the WCA acrylic
        // scrim only applies to dark surfaces, so this test stays deterministic.
        state.SurfaceColor = "#D8CFF0";
        state.SurfaceOpacity = 0.84d;
        using var sampler = new WindowsPerformanceSamplingService();
        var bar = new TopBarWindow(state, new DesktopStyleState(), new FakeEnvironment(), sampler);
        show(bar);
        try
        {
            var background = bar.Surface.Background as SolidColorBrush
                ?? throw new InvalidOperationException("Surface.Background 应为 SolidColorBrush");
            var color = background.Color;
            Assert(color.A > 200 && color.R == 0xD8 && color.G == 0xCF && color.B == 0xF0,
                $"玻璃底画刷应为淡紫 84%，实际 A={color.A} #{color.R:X2}{color.G:X2}{color.B:X2}");
            Assert(!bar.IsAcrylicSurface, "浅色自定义材质不应启用亚克力薄纱（否则 0% 不透明度下发灰）");

            // What actually reaches the screen: render the surface and sample the middle.
            var render = new System.Windows.Media.Imaging.RenderTargetBitmap(
                200, 32, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            render.Render(bar.Surface);
            render.Freeze();
            var pixels = new byte[200 * 32 * 4];
            render.CopyPixels(pixels, 200 * 4, 0);
            var sample = pixels.AsSpan((160 * 4) + (16 * 200 * 4), 4);
            Assert(sample[3] > 100,
                $"玻璃底应实际渲染（中心像素 alpha={sample[3]}），而非只剩文字和边线");

            // And through the real layered-window path: capture this window's own pixels
            // off the screen and look for the glass tint in the middle of the strip.
            var handle = new WindowInteropHelper(bar).Handle;
            GetWindowRect(handle, out var screenRect);
            using var capture = new System.Drawing.Bitmap(
                Math.Max(1, screenRect.Right - screenRect.Left),
                Math.Max(1, screenRect.Bottom - screenRect.Top));
            using (var graphics = System.Drawing.Graphics.FromImage(capture))
            {
                graphics.CopyFromScreen(screenRect.Left, screenRect.Top, 0, 0, capture.Size);
            }
            var middle = capture.GetPixel(capture.Width / 2, capture.Height / 2);
            Assert(middle.R > 40 && middle.B > 60 && middle.B > middle.G,
                $"屏幕合成应有玻璃底色（中点 #{middle.R:X2}{middle.G:X2}{middle.B:X2}），" +
                "分层窗口只画了文字没有底");
        }
        finally
        {
            bar.Close();
            pump();
        }
    }

    private static void ShowModulesAndStyles(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        using var sampler = new WindowsPerformanceSamplingService();
        var environment = new FakeEnvironment
        {
            Todos = 2,
            Weather = new WeatherSnapshot(
                WeatherStatus.Fresh, "北京", 18.4d, 24d, 11d, 42, "多云", false,
                DateTimeOffset.Now, null)
        };
        var bar = new TopBarWindow(state, new DesktopStyleState(), environment, sampler);
        show(bar);
        try
        {
            var hwnd = new WindowInteropHelper(bar).Handle;
            var style = (long)GetWindowLongPtr(hwnd, GwlExStyle);
            if ((style & NativeMethods.WsExToolWindow) == 0)
            {
                throw new InvalidOperationException("顶栏窗口缺少 ToolWindow 扩展样式，会出现在任务栏");
            }
            if ((style & DockNativeMethods.WsExNoActivate) == 0)
            {
                throw new InvalidOperationException("顶栏窗口缺少 NoActivate 扩展样式，点击会抢焦点");
            }
            Assert(bar.IsAcrylicSurface, "默认深色表面应启用亚克力薄纱");

            var buttons = AllButtons(bar);
            var names = buttons.Select(AutomationProperties.GetName).ToHashSet();
            foreach (var module in new[]
                     {
                         "待办模块", "性能模块", "电量模块", "网络模块", "音量模块", "时间日期模块"
                     })
            {
                if (!names.Contains(module))
                {
                    throw new InvalidOperationException($"顶栏缺少模块按钮：{module}");
                }
            }

            // The weather and the input method are display-only modules: present as
            // elements, not buttons.
            var elementNames = DescendantElements(bar)
                .OfType<FrameworkElement>()
                .Select(AutomationProperties.GetName)
                .ToHashSet();
            foreach (var module in new[] { "天气模块", "输入法模块" })
            {
                if (!elementNames.Contains(module))
                {
                    throw new InvalidOperationException($"顶栏缺少展示元素模块：{module}");
                }
            }

            var clock = ButtonByName(bar, "时间日期模块");
            if (!TextOf(clock).Contains("月"))
            {
                throw new InvalidOperationException("时钟模块应显示日期");
            }
            var todo = ButtonByName(bar, "待办模块");
            if (TextOf(todo) != "2")
            {
                throw new InvalidOperationException($"待办概要应在勾选图标旁显示未完成计数，实际：{TextOf(todo)}");
            }
            var weather = ElementByName(bar, "天气模块");
            if (!TextOf(weather).StartsWith("北京") || !TextOf(weather).Contains("18°"))
            {
                throw new InvalidOperationException($"天气模块应显示城市与温度，实际：{TextOf(weather)}");
            }
            if (weather.ToolTip is not ToolTip { Content: TextBlock detail }
                || !TextOfTextBlock(detail).Contains("湿度"))
            {
                throw new InvalidOperationException("天气模块悬停应提供高低温与湿度详情");
            }
            // System facts arrive from a background read; wait for them to land, then
            // hold them to the real thing: a dash or fallback label on this machine
            // means the underlying API call is dead, not that the value is unknown.
            var batteryButton = ButtonByName(bar, "电量模块");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (string.IsNullOrWhiteSpace(TextOf(batteryButton)) && DateTime.UtcNow < deadline)
            {
                pump();
            }
            if (string.IsNullOrWhiteSpace(TextOf(batteryButton)))
            {
                throw new InvalidOperationException("系统状态模块应在显示后数秒内完成首次渲染");
            }
            if (TextOf(ButtonByName(bar, "音量模块")) == "—")
            {
                throw new InvalidOperationException("音量模块应读到真实音量而非占位符（COM 路径失效）");
            }
            var inputMethod = ElementByName(bar, "输入法模块");
            if (TextOf(inputMethod) == "键盘")
            {
                throw new InvalidOperationException("输入法模块应读到真实输入法状态而非回退文案（注册表或 IME 路径失效）");
            }
            // The module shows the taskbar indicator's shape: the mode badge in a
            // rounded tile with the input method's own name beside it, and the full
            // layout name on the hover tooltip. It is a display-only element: clicking
            // it does nothing.
            if (inputMethod is not Border { Child: StackPanel imePanel })
            {
                throw new InvalidOperationException("输入法模块应为无点击的展示元素并承载徽标布局");
            }
            if (imePanel.Children.OfType<Border>().FirstOrDefault() is not { } imeTile
                || imeTile.CornerRadius == default)
            {
                throw new InvalidOperationException("输入法模块应以圆角徽标显示任务栏式中/英文状态");
            }
            var imeName = imePanel.Children.OfType<TextBlock>().FirstOrDefault();
            if (imeName is null || imeName.Foreground != Brushes.White)
            {
                throw new InvalidOperationException("输入法模块应在徽标旁以白字显示当前输入法名");
            }
            if (inputMethod.ToolTip is not ToolTip)
            {
                throw new InvalidOperationException("输入法模块悬停应提供完整布局名");
            }
        }
        finally
        {
            bar.Close();
            pump();
        }
    }

    private static void ModuleSwitchesRemoveButtons(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        state.Modules.Volume = false;
        state.Modules.Network = false;
        using var sampler = new WindowsPerformanceSamplingService();
        var bar = new TopBarWindow(state, new DesktopStyleState(), new FakeEnvironment(), sampler);
        show(bar);
        try
        {
            var names = AllButtons(bar).Select(AutomationProperties.GetName).ToHashSet();
            if (names.Contains("音量模块") || names.Contains("网络模块"))
            {
                throw new InvalidOperationException("关闭的模块不应出现在顶栏");
            }
            if (!names.Contains("时间日期模块"))
            {
                throw new InvalidOperationException("未关闭的模块应保持显示");
            }
        }
        finally
        {
            bar.Close();
            pump();
        }
    }

    private static void ClicksOpenSettings(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        using var sampler = new WindowsPerformanceSamplingService();
        var environment = new FakeEnvironment();
        var bar = new TopBarWindow(state, new DesktopStyleState(), environment, sampler);
        show(bar);
        try
        {
            var clock = ButtonByName(bar, "时间日期模块");
            Invoke(clock);
            pump();
            var calendar = bar.ClockFlyout;
            if (calendar?.IsOpen != true)
            {
                throw new InvalidOperationException("点击时钟模块应弹出日历信息栏");
            }
            var calendarTexts = DescendantTextBlocks(calendar.Child)
                .Select(text => text.Text)
                .ToList();
            var today = DateTime.Now;
            Assert(calendarTexts.Any(text =>
                    text.Contains(today.Year + "年") && text.Contains(today.Month + "月")
                    && text.Contains(today.Day + "日")),
                "日历信息栏应显示今天的日期");
            Assert(calendarTexts.Contains("一") && calendarTexts.Contains("日"),
                "日历信息栏应显示星期表头");
            Invoke(clock);
            pump();
            Assert(bar.ClockFlyout is null, "再次点击时钟模块应收起日历信息栏");
            Invoke(ButtonByName(bar, "音量模块"));
            if (environment.QuickSettingsRequested != 1)
            {
                throw new InvalidOperationException("点击音量模块应唤起系统快速设置");
            }
            Invoke(ButtonByName(bar, "电量模块"));
            if (environment.QuickSettingsRequested != 2)
            {
                throw new InvalidOperationException("点击电量模块应唤起系统快速设置");
            }
            Invoke(ButtonByName(bar, "性能模块"));
            if (environment.TaskManagerRequested != 1)
            {
                throw new InvalidOperationException("点击性能模块应打开任务管理器性能页");
            }
            if (AllButtons(bar).Any(candidate =>
                    AutomationProperties.GetName(candidate) == "天气模块"))
            {
                throw new InvalidOperationException("天气模块应不可点击（无按钮语义）");
            }
        }
        finally
        {
            bar.Close();
            pump();
        }
    }

    private static TopBarState BarState() => new() { IsEnabled = true };

    private static IReadOnlyList<Button> AllButtons(DependencyObject root)
    {
        var buttons = new List<Button>();
        Collect(root, buttons);
        return buttons;

        static void Collect(DependencyObject node, List<Button> into)
        {
            if (node is Button button)
            {
                into.Add(button);
            }
            var children = VisualTreeHelper.GetChildrenCount(node);
            for (var index = 0; index < children; index++)
            {
                Collect(VisualTreeHelper.GetChild(node, index), into);
            }
        }
    }

    private static Button ButtonByName(DependencyObject root, string name) =>
        AllButtons(root).FirstOrDefault(button =>
            AutomationProperties.GetName(button) == name)
        ?? throw new InvalidOperationException($"找不到顶栏模块：{name}");

    private static FrameworkElement ElementByName(DependencyObject root, string name) =>
        DescendantElements(root).OfType<FrameworkElement>().FirstOrDefault(element =>
            AutomationProperties.GetName(element) == name)
        ?? throw new InvalidOperationException($"找不到顶栏元素：{name}");

    private static string TextOf(FrameworkElement module) => module switch
    {
        Button button => ButtonContentText(button.Content),
        Border border => ButtonContentText(border.Child),
        _ => string.Empty
    };

    // The input method button packs a rounded badge tile plus the input method's name
    // into a horizontal panel; the badge's character is the module's spoken text. The
    // other icon modules pack a glyph TextBlock (tagged) plus the value text; the glyph
    // is decoration, the value is the module's spoken text.
    private static string ButtonContentText(object? content) => content switch
    {
        TextBlock text => text.Text,
        StackPanel panel => TextOfTextBlock(
            panel.Children.OfType<Border>().FirstOrDefault()?.Child as TextBlock
            ?? panel.Children.OfType<TextBlock>().FirstOrDefault(text => text.Tag is not "topbar-icon")),
        Border border => TextOfTextBlock(border.Child as TextBlock),
        _ => string.Empty
    };

    private static string TextOfTextBlock(TextBlock? text) => text?.Text ?? string.Empty;

    private static void Invoke(Button button)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(button)
            ?? throw new InvalidOperationException("模块按钮没有自动化对等项");
        if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider)
        {
            throw new InvalidOperationException("模块按钮应支持 Invoke 模式");
        }
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private sealed class FakeEnvironment : ITopBarEnvironment
    {
        internal int Todos { get; set; }
        internal WeatherSnapshot? Weather { get; set; }
        internal TopBarTodoList? TodoList { get; set; }
        internal int Toggles { get; private set; }
        internal int QuickSettingsRequested { get; private set; }
        internal int TaskManagerRequested { get; private set; }
        internal DesktopComponentKind? ComponentRequested { get; private set; }
        internal int TopBarPageRequested { get; private set; }

        public bool Use24HourClock => true;

        public WeatherSnapshot? ReadWeather() => Weather;

        public void RefreshWeatherIfDue()
        {
        }

        public int CountIncompleteTodos() => Todos;

        public TopBarTodoList? ReadTodoList() => TodoList;

        public void TodoItemToggled() => Toggles++;

        public void OpenTopBarSettings() => TopBarPageRequested++;

        public void OpenQuickSettings() => QuickSettingsRequested++;

        public void OpenTaskManagerPerformance() => TaskManagerRequested++;

        public void OpenComponentSettings(DesktopComponentKind kind) => ComponentRequested = kind;

        public bool ActivateWindow(nint handle) => false;

        public void ExitApp()
        {
        }
    }

    private static void TodoFlyoutShowsItemsAndCloses(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        using var sampler = new WindowsPerformanceSamplingService();
        var first = new TodoItemState { Text = "测试顶端信息条" };
        var second = new TodoItemState { Text = "已完成的一条", IsCompleted = true };
        var environment = new FakeEnvironment
        {
            Todos = 2,
            TodoList = new TopBarTodoList("今日待办", [first, second])
        };
        var bar = new TopBarWindow(state, new DesktopStyleState(), environment, sampler);
        show(bar);
        try
        {
            var todo = ButtonByName(bar, "待办模块");
            Invoke(todo);
            pump();
            var flyout = bar.TodoFlyout
                ?? throw new InvalidOperationException("点击待办模块应弹出待办信息栏");
            Assert(flyout.IsOpen, "待办信息栏应处于打开状态");
            var texts = DescendantTextBlocks(flyout.Child)
                .Select(text => text.Text)
                .ToList();
            Assert(texts.Contains("今日待办"), "待办信息栏应显示列表标题");
            Assert(texts.Contains("测试顶端信息条"), "待办信息栏应显示未完成项");
            Assert(texts.Contains("已完成的一条"), "待办信息栏应显示已完成项");
            Assert(environment.ComponentRequested is null,
                "点击待办模块不应跳转待办组件设置");

            // A row click toggles that item, like the card's checkbox, and the flyout
            // follows the live state.
            DescendantElements(flyout.Child)
                .OfType<StackPanel>()
                .First(panel => panel.DataContext == first)
                .RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice,
                    0,
                    System.Windows.Input.MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonUpEvent
                });
            pump();
            Assert(first.IsCompleted, "点击信息栏条目应切换完成状态");
            Assert(environment.Toggles == 1, "切换后应通知宿主保存");
            var glyphs = DescendantTextBlocks(flyout.Child)
                .Where(text => text.Text is "✓" or "○")
                .ToList();
            Assert(glyphs.Count == 2 && glyphs.Count(glyph => glyph.Text == "✓") == 2,
                "两条待办都应显示为已完成");

            Invoke(todo);
            pump();
            Assert(bar.TodoFlyout is null, "再次点击待办模块应收起信息栏");
        }
        finally
        {
            bar.Close();
            pump();
        }
    }

    private static IEnumerable<TextBlock> DescendantTextBlocks(DependencyObject root) =>
        DescendantElements(root).OfType<TextBlock>();

    private static IEnumerable<DependencyObject> DescendantElements(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in DescendantElements(child))
            {
                yield return descendant;
            }
        }
    }
}
