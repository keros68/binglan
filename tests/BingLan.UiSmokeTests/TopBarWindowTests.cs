using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.App.Windows;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.TopBar;
using BingLan.App.TopBar;
using System.Buffers.Binary;
using System.Text;

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

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint MouseMove = 0x0001;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseMoveNoCoalesce = 0x2000;
    private const uint MouseVirtualDesktop = 0x4000;
    private const uint MouseAbsolute = 0x8000;
    private const int VirtualScreenLeft = 76;
    private const int VirtualScreenTop = 77;
    private const int VirtualScreenWidth = 78;
    private const int VirtualScreenHeight = 79;

    // MOUSEINPUT is the largest INPUT union member; sequential layout includes the
    // native alignment after the type field on both x86 and x64.
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputEvent
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public nuint ExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, MouseInputEvent[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private static void SendMouseInputs(params MouseInput[] mouseInputs)
    {
        var inputs = mouseInputs.Select(mouse => new MouseInputEvent { Type = 0, Mouse = mouse }).ToArray();
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<MouseInputEvent>());
        if (sent != inputs.Length)
        {
            var error = Marshal.GetLastWin32Error();
            // A partially inserted click must not leave the mouse button held.
            MouseInputEvent[] release = [new() { Mouse = new MouseInput { Flags = MouseLeftUp } }];
            _ = SendInput(1, release, Marshal.SizeOf<MouseInputEvent>());
            throw new InvalidOperationException($"系统鼠标输入失败：{sent}/{inputs.Length}，错误 {error}");
        }
    }

    internal static void MoveMouseTo(int x, int y)
    {
        var left = GetSystemMetrics(VirtualScreenLeft);
        var top = GetSystemMetrics(VirtualScreenTop);
        var width = GetSystemMetrics(VirtualScreenWidth);
        var height = GetSystemMetrics(VirtualScreenHeight);
        Assert(width > 0 && height > 0, "虚拟桌面应有有效尺寸");
        SendMouseInputs(new MouseInput
        {
            X = (int)((x - left + 0.5) * 65536d / width),
            Y = (int)((y - top + 0.5) * 65536d / height),
            Flags = MouseMove | MouseMoveNoCoalesce | MouseVirtualDesktop | MouseAbsolute
        });
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    /// <summary>
    /// Interactive windows receive the foreground after a user opens them. Windows
    /// can deny that grant to an automated test, so tests recreate the foreground
    /// before sending real keyboard or mouse input.
    /// </summary>
    internal static void ForceForeground(nint window)
    {
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var currentThread = GetCurrentThreadId();
        if (foregroundThread == currentThread || foregroundThread == 0)
        {
            _ = SetForegroundWindow(window);
            return;
        }
        _ = AttachThreadInput(currentThread, foregroundThread, true);
        _ = SetForegroundWindow(window);
        _ = AttachThreadInput(currentThread, foregroundThread, false);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            var foreground = GetForegroundWindow();
            GetWindowThreadProcessId(foreground, out var processId);
            throw new InvalidOperationException($"{message}（前台窗口 {foreground:X}，进程 {processId}）");
        }
    }

    internal static void Run(Action<Window> show, Action pump)
    {
        ShowModulesAndStyles(show, pump);
        ModuleSwitchesRemoveButtons(show, pump);
        ClicksOpenSettings(show, pump);
        SurfacePaintsGlass(show, pump);
        TodoFlyoutShowsItemsAndCloses(show, pump);
        CapsuleExpandsWithinItsSurface(show, pump);
        WeatherHoverOpensTooltip(show, pump);
        SearchEntryButtonOpensLauncher(show, pump);
        SearchModuleSwitchHidesEntry(show, pump);
        RateDigitsDoNotShiftSearchEntry(show, pump);
        SearchEntrySitsAtBarCentre(show, pump);
        EverythingProtocolAndFallback(pump);
        WindowsIndexQueryRules();
    }

    private static void WeatherHoverOpensTooltip(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        using var sampler = new WindowsPerformanceSamplingService();
        var environment = new FakeEnvironment
        {
            Weather = new WeatherSnapshot(
                WeatherStatus.Fresh, "南岗区 · 黑龙江省·哈尔滨市 · 中国", 18.4d, 24d, 11d, 42, "多云", false,
                DateTimeOffset.Now, null)
        };
        var bar = new TopBarWindow(state, new DesktopStyleState(), environment, sampler);
        show(bar);
        try
        {
            var weather = ElementByName(bar, "天气模块");
            var display = TextOf(weather);
            Assert(display == "南岗区 18° 多云",
                $"顶栏天气应只显示最细定位名，避免在小屏截断温度和天气：实际「{display}」");
            var toolTip = (weather as Border)?.ToolTip as ToolTip
                ?? throw new InvalidOperationException("天气模块应带悬停详情 tooltip");
            var toolTipText = (toolTip.Content as TextBlock)?.Text ?? string.Empty;
            Assert(toolTipText.Contains("南岗区 · 黑龙江省·哈尔滨市 · 中国"),
                $"天气悬停详情应保留完整定位名称：实际「{toolTipText}」");

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
                         "待办模块", "性能模块", "电量模块", "网络模块", "音量模块", "输入法模块", "时间日期模块"
                     })
            {
                if (!names.Contains(module))
                {
                    throw new InvalidOperationException($"顶栏缺少模块按钮：{module}");
                }
            }

            // The weather is a display-only module, with no button semantics.
            var elementNames = DescendantElements(bar)
                .OfType<FrameworkElement>()
                .Select(AutomationProperties.GetName)
                .ToHashSet();
            foreach (var module in new[] { "天气模块" })
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
            var freshWeather = environment.Weather!;
            foreach (var status in new[] { WeatherStatus.CachedFallback, WeatherStatus.Failed })
            {
                environment.Weather = freshWeather with
                {
                    Status = status,
                    FromCache = status == WeatherStatus.CachedFallback,
                    ErrorMessage = "网络请求失败"
                };
                bar.ApplyState();
                Assert(ElementByName(bar, "天气模块").ToolTip is ToolTip { Content: TextBlock failureDetail }
                       && TextOfTextBlock(failureDetail).Contains("刷新失败：网络请求失败"),
                    $"天气 {status} 状态应在悬停详情展示失败原因");
            }
            environment.Weather = freshWeather;
            bar.ApplyState();
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
            // The cloud test machine has no audio device, so only a machine with one can
            // tell a broken COM path from a missing speaker.
            if (TextOf(ButtonByName(bar, "音量模块")) == "—"
                && !string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
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
            // layout name on the hover tooltip. Clicking opens the system selector.
            if (inputMethod is not Button { Content: StackPanel imePanel })
            {
                throw new InvalidOperationException("输入法模块应为按钮并承载徽标布局");
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
        state.Modules.TodoSummary = false;
        state.Modules.Attention = false;
        state.Modules.InputMethod = false;
        state.Modules.Battery = false;
        state.Modules.TrayOverflow = false;
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
            Invoke(ButtonByName(bar, "网络模块"));
            Assert(environment.QuickSettingsKinds.SequenceEqual(new[]
                { TopBarModuleKind.Volume, TopBarModuleKind.Battery, TopBarModuleKind.Network }),
                "系统状态点击应保留对应模块，以便回退到各自设置页");
            Invoke(ButtonByName(bar, "输入法模块"));
            Assert(environment.InputMethodRequested == 1, "点击输入法应打开系统选择器");
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                var tray = ButtonByName(bar, "托盘模块");
                Assert(tray.ActualWidth >= 32 && tray.ActualHeight >= 32, "托盘入口应有可点击的热区");
                Invoke(tray);
                Assert(environment.TrayOverflowRequested == 1, "托盘按钮应调用原生面板入口");
                state.Modules.TrayOverflow = false;
                bar.ApplyState();
                pump();
                Assert(!AllButtons(bar).Any(button => AutomationProperties.GetName(button) == "托盘模块"),
                    "关闭托盘模块应移除入口");
                Assert(environment.TrayOverflowClosed > 0, "关闭托盘模块应清理本次面板会话");
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

    // ---------------------------------------------------------------- 搜索入口

    private static void SearchEntryButtonOpensLauncher(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        using var sampler = new WindowsPerformanceSamplingService();
        var environment = new FakeEnvironment();
        var bar = new TopBarWindow(state, new DesktopStyleState(), environment, sampler);
        show(bar);
        try
        {
            var entry = bar.SearchEntryButton
                ?? throw new InvalidOperationException("搜索模块应提供顶栏入口按钮");
            Assert(AutomationProperties.GetName(entry) == "搜索入口", "搜索入口按钮应保留自动化名称");
            Invoke(entry);
            Assert(WaitUntil(pump, () => environment.LauncherToggles == 1), "点击搜索入口应打开/切换冰蓝总览卡");
            bar.TriggerSearch();
            Assert(environment.LauncherToggles == 2, "全局热键入口应复用同一总览卡切换逻辑");
            var style = (long)GetWindowLongPtr(new WindowInteropHelper(bar).Handle, GwlExStyle);
            Assert((style & DockNativeMethods.WsExNoActivate) != 0, "触发总览卡后顶栏应保持永不激活");
        }
        finally
        {
            bar.Close();
            pump();
        }
    }

    private static void SearchModuleSwitchHidesEntry(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        state.Modules.Search = false;
        using var sampler = new WindowsPerformanceSamplingService();
        var environment = new FakeEnvironment();
        var bar = new TopBarWindow(state, new DesktopStyleState(), environment, sampler);
        show(bar);
        try
        {
            Assert(bar.SearchEntryButton is null, "关闭搜索模块后不应有搜索入口按钮");
            bar.TriggerSearch();
            pump();
            Assert(environment.LauncherToggles == 0, "模块关闭时热键触发不应切换总览卡");

            state.Modules.Search = true;
            bar.ApplyState();
            pump();
            Assert(bar.SearchEntryButton is not null, "重新开启模块应重建搜索入口按钮");

            environment.LauncherEnabled = false;
            bar.ApplyState();
            pump();
            Assert(bar.SearchEntryButton is null, "关闭冰蓝总览卡时应隐藏顶栏搜索入口");
            bar.TriggerSearch();
            pump();
            Assert(environment.LauncherToggles == 0, "总览卡关闭时不应响应顶栏搜索触发");
        }
        finally
        {
            bar.Close();
            pump();
        }
    }

    /// <summary>
    /// The upload/download digits change width every second; the centred search entry must
    /// stay put instead of riding the performance module's resize.
    /// </summary>
    private static void RateDigitsDoNotShiftSearchEntry(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        using var sampler = new ScriptedSamplingService();
        var bar = new TopBarWindow(state, new DesktopStyleState(), new FakeEnvironment(), sampler);
        show(bar);
        try
        {
            var entry = bar.SearchEntryButton
                ?? throw new InvalidOperationException("搜索模块应提供入口按钮");
            double EntryX()
            {
                pump();
                return entry.PointToScreen(new Point(0, 0)).X;
            }

            sampler.Emit(new PerformanceSnapshot(12, 34, 0, 0, DateTimeOffset.Now));
            var idleX = EntryX();
            sampler.Emit(new PerformanceSnapshot(
                12, 34, 1023.9d * 1024 * 1024, 1023.9d * 1024 * 1024, DateTimeOffset.Now));
            var busyX = EntryX();
            Assert(TextOf(ButtonByName(bar, "性能模块")).Contains("MB/s"),
                "第二次采样应已把网速渲染为 MB/s");
            Assert(Math.Abs(busyX - idleX) < 0.5,
                $"网速数字变宽不应移动居中的搜索入口：{idleX} → {busyX}");
        }
        finally
        {
            bar.Close();
            pump();
        }
    }

    private static void SearchEntrySitsAtBarCentre(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        // Centring is what is under test, not how many modules fit a small screen.
        state.Modules.Weather = false;
        state.Modules.Performance = false;
        state.Modules.Network = false;
        state.Modules.Volume = false;
        using var sampler = new WindowsPerformanceSamplingService();
        var bar = new TopBarWindow(state, new DesktopStyleState(), new FakeEnvironment(), sampler);
        show(bar);
        try
        {
            var entry = bar.SearchEntryButton
                ?? throw new InvalidOperationException("搜索模块应提供入口按钮");
            pump();
            var leftModules = (FrameworkElement)bar.FindName("LeftModules");
            var rightModules = (FrameworkElement)bar.FindName("RightModules");
            var barLeft = bar.PointToScreen(new Point(0, 0)).X;
            var barRight = bar.PointToScreen(new Point(bar.ActualWidth, 0)).X;
            var entryLeft = entry.PointToScreen(new Point(0, 0)).X;
            var entryRight = entry.PointToScreen(new Point(entry.ActualWidth, 0)).X;
            var leftEdge = leftModules.PointToScreen(new Point(leftModules.ActualWidth, 0)).X;
            var rightEdge = rightModules.PointToScreen(new Point(0, 0)).X;
            Assert(entry.ActualWidth >= TopBarSearchRules.MinCenteredEntryWidth,
                $"搜索入口不应窄于最小可用宽度：入口 {entry.ActualWidth}，栏 {bar.ActualWidth}，左模块 {leftModules.ActualWidth}，右模块 {rightModules.ActualWidth}");
            Assert(Math.Abs((entryLeft + entryRight) / 2d - (barLeft + barRight) / 2d) <= 1d,
                $"搜索入口应位于顶栏正中：入口心 {(entryLeft + entryRight) / 2d}，栏心 {(barLeft + barRight) / 2d}");
            Assert(entryLeft >= leftEdge - 0.5 && entryRight <= rightEdge + 0.5,
                $"居中搜索入口不得侵入左右模块区域：入口 [{entryLeft}, {entryRight}]，左边界 {leftEdge}，右边界 {rightEdge}");

            // Asymmetric wider left side must shrink the centered button by the tighter side's room without overlap.
            leftModules.MinWidth = Math.Max(rightModules.ActualWidth + 100d, (bar.ActualWidth - 160d) / 2d);
            pump();
            Assert(entry.Visibility == Visibility.Visible && entry.ActualWidth < TopBarSearchRules.DefaultCenteredEntryWidth,
                "一侧模块较宽时居中搜索入口应按对称净空收缩");
            var shrunkLeft = entry.PointToScreen(new Point(0, 0)).X;
            var shrunkRight = entry.PointToScreen(new Point(entry.ActualWidth, 0)).X;
            var widenedLeftEdge = leftModules.PointToScreen(new Point(leftModules.ActualWidth, 0)).X;
            var narrowedRightEdge = rightModules.PointToScreen(new Point(0, 0)).X;
            Assert(shrunkLeft >= widenedLeftEdge - 0.5 && shrunkRight <= narrowedRightEdge + 0.5,
                $"收缩后的居中搜索入口仍不得侵入较宽一侧模块：入口 [{shrunkLeft}, {shrunkRight}]，左边界 {widenedLeftEdge}");
            Assert(Math.Abs((shrunkLeft + shrunkRight) / 2d - (barLeft + barRight) / 2d) <= 1d,
                "收缩后搜索入口仍应保持居中");

            // When symmetric clear room drops below the minimum width, collapse instead of overlapping side modules.
            leftModules.MinWidth = (bar.ActualWidth - 40d) / 2d;
            pump();
            Assert(entry.Visibility == Visibility.Collapsed,
                "对称净空不足最小宽度时应临时隐藏搜索入口以避免遮挡侧栏模块");
        }
        finally
        {
            bar.Close();
            pump();
        }
    }

    private static void EverythingProtocolAndFallback(Action pump)
    {
        var request = EverythingSearchClient.CreateRequest((nint)0x1234, 91, "年度 报告");
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(8)) == 8
            && BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(16)) == EverythingSearchClient.MaximumCandidates,
            "Everything 应使用有上限的文件名查询");
        var pattern = Encoding.Unicode.GetString(request.AsSpan(20)).TrimEnd('\0');
        Assert(System.Text.RegularExpressions.Regex.IsMatch("报告_年度.docx", pattern), "文件名多词应不受顺序限制");
        var response = EverythingResponse(("年度报告.txt", @"D:\资料", 0u), ("年度报告", @"D:\资料", 1u));
        var parsed = EverythingSearchClient.ParseResponse(response);
        Assert(parsed.Available && parsed.Candidates.Count == 2
            && parsed.Candidates[0].Path == @"D:\资料\年度报告.txt"
            && parsed.Candidates[1].Kind == TopBarSearchItemKind.Folder, "应解析 Unicode 文件和文件夹路径");
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(32), uint.MaxValue);
        Assert(!EverythingSearchClient.ParseResponse(response).Available, "损坏的回复偏移应安全回退");
        Assert(!EverythingSearchClient.ParseResponse(new byte[12]).Available, "截断回复应安全回退");
        Assert(EverythingSearchClient.ParseResponse(new byte[28]).Available, "有效的零结果不等于 Everything 不可用");

        using var reply = new HwndSource(new HwndSourceParameters("BingLan Everything protocol test") { ParentWindow = (nint)(-3) });
        using var client = new EverythingSearchClient(reply);
        var missing = client.SearchAsync("报告", CancellationToken.None, 0);
        Assert(WaitUntil(pump, () => missing.IsCompleted) && !missing.GetAwaiter().GetResult().Available,
            "未运行 Everything 应立即返回可回退状态");
        using var endpoint = new HwndSource(new HwndSourceParameters("BingLan IPC endpoint") { ParentWindow = (nint)(-3) });
        var requestIds = new List<uint>();
        endpoint.AddHook((_, message, _, lParam, ref handled) =>
        {
            if (message != 0x004A) return 0;
            var data = Marshal.PtrToStructure<EverythingNativeMethods.CopyData>(lParam);
            requestIds.Add(unchecked((uint)Marshal.ReadInt32(data.Data, 4)));
            handled = true;
            return 1;
        });
        void Reply(uint id)
        {
            var payload = EverythingResponse(("年度报告.txt", @"D:\资料", 0u));
            var pinned = GCHandle.Alloc(payload, GCHandleType.Pinned);
            try
            {
                var data = new EverythingNativeMethods.CopyData { Id = (nint)id, Length = payload.Length, Data = pinned.AddrOfPinnedObject() };
                EverythingNativeMethods.SendMessageTimeout(reply.Handle, 0x004A, endpoint.Handle, ref data, 2, 150, out _);
            }
            finally { pinned.Free(); }
        }
        var oldQuery = client.SearchAsync("旧查询", CancellationToken.None, endpoint.Handle);
        Assert(WaitUntil(pump, () => requestIds.Count == 1), "本机查询应通过真实 WM_COPYDATA 发出");
        var newQuery = client.SearchAsync("年度", CancellationToken.None, endpoint.Handle);
        Assert(WaitUntil(pump, () => requestIds.Count == 2), "新查询应使用独立回复标识");
        Reply(requestIds[0]);
        pump();
        Assert(!newQuery.IsCompleted, "旧查询的迟到回复不能覆盖新结果");
        Reply(requestIds[1]);
        Assert(WaitUntil(pump, () => newQuery.IsCompleted) && newQuery.GetAwaiter().GetResult().Candidates.Count == 1,
            "当前查询应接受对应的原生回复");
        try { oldQuery.GetAwaiter().GetResult(); throw new InvalidOperationException("旧查询应取消"); }
        catch (OperationCanceledException) { }
        var noReply = client.SearchAsync("无回复", CancellationToken.None, endpoint.Handle);
        Assert(WaitUntil(pump, () => noReply.IsCompleted) && !noReply.GetAwaiter().GetResult().Available,
            "无回复应在有限等待后回退");
    }

    private static void WindowsIndexQueryRules()
    {
        var sql = WindowsIndexSearchClient.CreateQuery("年度 报告'_%[");
        Assert(sql.StartsWith("SELECT TOP 128 ", StringComparison.Ordinal)
            && sql.Contains("System.ItemUrl, System.FileAttributes", StringComparison.Ordinal)
            && sql.Contains("scope='file:'", StringComparison.Ordinal)
            && sql.Contains("System.FileName LIKE '%年度%' AND System.FileName LIKE '%报告''[_][%][[]%'", StringComparison.Ordinal),
            "系统索引应限制候选数量，并把多词、引号和通配符作为文件名字面量");
        Assert(sql.Contains("CONTAINS(System.FileName, '\"年度*\"')", StringComparison.Ordinal)
            && !WindowsIndexSearchClient.CreateQuery("报告", useWordPrefix: false).Contains("CONTAINS", StringComparison.Ordinal),
            "系统索引应先用文件名词前缀缩小范围，再在空结果时回退包含查询");
        Assert(WindowsIndexSearchClient.CreateQuery("content:hello").Contains("System.FileName LIKE '%content:hello%'", StringComparison.Ordinal),
            "系统索引不应把用户输入解释为内容搜索");
        Assert(!WindowsIndexSearchClient.ShouldFallbackToSubstringScan("wx")
            && WindowsIndexSearchClient.ShouldFallbackToSubstringScan("报告")
            && WindowsIndexSearchClient.ShouldFallbackToSubstringScan("report"),
            "短 ASCII 拼音首字母查询不应触发全表无索引子串回退扫描");
        var parsed = WindowsIndexSearchClient.ParseRows(new object[,]
        {
            { "file:///D:/%E8%B5%84%E6%96%99/%E5%B9%B4%E5%BA%A6%E6%8A%A5%E5%91%8A.txt", "file:///D:/%E8%B5%84%E6%96%99", "relative.txt",
                "file:///C:/Users/test/Desktop/a%20b%23%2520.txt", "https://example.com/file.txt" },
            { 0L, 16L, 0L, 0L, 0L }
        });
        Assert(parsed.Count == 3 && parsed[0].FullFileName == "年度报告.txt"
            && parsed[1].Kind == TopBarSearchItemKind.Folder, "系统索引应保留文件与目录类型，排除非文件地址");
        Assert(parsed[2].Path == @"C:\Users\test\Desktop\a b#%20.txt",
            "索引地址应还原真实 Users/Desktop 路径及空格、井号、百分号，不能使用本地化显示路径");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            WindowsIndexSearchClient.SearchAsync("报告", cancellation.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("取消的查询不应访问系统索引");
        }
        catch (OperationCanceledException) { }
    }

    private static byte[] EverythingResponse(params (string Name, string Directory, uint Flags)[] entries)
    {
        using var stream = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(stream, Encoding.Unicode, true);
        writer.Write(new byte[28 + entries.Length * 12]);
        for (var index = 0; index < entries.Length; index++)
        {
            var nameOffset = (uint)stream.Position;
            writer.Write(Encoding.Unicode.GetBytes(entries[index].Name + '\0'));
            var pathOffset = (uint)stream.Position;
            writer.Write(Encoding.Unicode.GetBytes(entries[index].Directory + '\0'));
            var next = stream.Position;
            stream.Position = 28 + index * 12;
            writer.Write(entries[index].Flags);
            writer.Write(nameOffset);
            writer.Write(pathOffset);
            stream.Position = next;
        }
        stream.Position = 20;
        writer.Write(entries.Length);
        return stream.ToArray();
    }

    private static bool WaitUntil(Action pump, Func<bool> condition, int attempts = 240)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            pump();
            if (condition())
            {
                return true;
            }
            Thread.Sleep(25);
        }
        return condition();
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
        internal bool LauncherEnabled { get; set; } = true;
        internal int LauncherToggles { get; private set; }

        public bool Use24HourClock => true;

        public WeatherSnapshot? ReadWeather() => Weather;

        public void RefreshWeatherIfDue()
        {
        }

        public int CountIncompleteTodos() => Todos;

        public TopBarTodoList? ReadTodoList() => TodoList;

        public void TodoItemToggled() => Toggles++;

        public bool IsLauncherEnabled => LauncherEnabled;

        public void ToggleLauncherPanel() => LauncherToggles++;

        public void OpenTopBarSettings() => TopBarPageRequested++;

        internal List<TopBarModuleKind> QuickSettingsKinds { get; } = [];

        internal int InputMethodRequested { get; private set; }

        public void OpenQuickSettings(TopBarModuleKind kind)
        {
            QuickSettingsRequested++;
            QuickSettingsKinds.Add(kind);
        }

        public void OpenInputMethod() => InputMethodRequested++;

        internal int TrayOverflowRequested { get; private set; }
        internal int TrayOverflowClosed { get; private set; }
        public void OpenTrayOverflow() => TrayOverflowRequested++;
        public void CloseTrayOverflow() => TrayOverflowClosed++;

        public void OpenTaskManagerPerformance() => TaskManagerRequested++;

        public void OpenComponentSettings(DesktopComponentKind kind) => ComponentRequested = kind;

        public bool ActivateWindow(nint handle, uint processId = 0) => false;

        public void ExitApp()
        {
        }
    }

    /// <summary>A sampling service the test drives by hand, for deterministic ticks.</summary>
    private sealed class ScriptedSamplingService : IPerformanceSamplingService
    {
        public event Action<PerformanceSnapshot>? Sampled;

        public bool IsRunning => false;

        public PerformanceSamplingMode Mode => PerformanceSamplingMode.Normal;

        public TimeSpan CurrentInterval => PerformanceSamplingRules.NormalInterval;

        public void Start()
        {
        }

        public void SetMode(PerformanceSamplingMode mode)
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }

        internal void Emit(PerformanceSnapshot snapshot) => Sampled?.Invoke(snapshot);
    }

    private static void TodoFlyoutShowsItemsAndCloses(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        using var sampler = new WindowsPerformanceSamplingService();
        var first = new TodoItemState { Text = "测试顶端信息条" };
        first.DeadlineDate = DateTime.Today.AddDays(2);
        first.DeadlineTime = new TimeOnly(14, 30);
        var second = new TodoItemState { Text = "已完成的一条", IsCompleted = true };
        var countdown = new TodoItemState
        {
            Text = "考试", Kind = TodoItemKind.Countdown,
            DeadlineDate = DateTime.Today.AddDays(10)
        };
        var environment = new FakeEnvironment
        {
            Todos = 2,
            TodoList = new TopBarTodoList("今日待办", [first, second, countdown])
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
            Assert(texts.Contains(first.CountdownLabel!) && texts.Contains(countdown.CountdownLabel!),
                "顶栏应显示待办截止倒计时和独立倒数日");
            var countdownRow = DescendantElements(flyout.Child).OfType<Grid>()
                .First(row => ReferenceEquals(row.DataContext, countdown));
            countdownRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Assert(!countdown.IsCompleted && environment.Toggles == 0, "独立倒数日不应响应完成切换");
            Assert(environment.ComponentRequested is null,
                "点击待办模块不应跳转待办组件设置");

            // A row click toggles that item, like the card's checkbox, and the flyout
            // follows the live state.
            DescendantElements(flyout.Child)
                .OfType<Grid>()
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
            Assert(DescendantTextBlocks(flyout.Child).Where(text => ReferenceEquals(text.DataContext, first))
                .All(text => text.Text != first.DeadlineDescription && !text.Text.StartsWith("还剩")),
                "顶栏完成待办后应停止显示倒计时");
            var glyphs = DescendantTextBlocks(flyout.Child)
                .Where(text => text.Text is "✓" or "○")
                .ToList();
            Assert(glyphs.Count == 2 && glyphs.Count(glyph => glyph.Text == "✓") == 2,
                "两条待办都应显示为已完成");

            Invoke(todo);
            pump();
            Assert(bar.TodoFlyout is null, "再次点击待办模块应收起信息栏");
            Invoke(todo);
            pump();
            Invoke(todo);
            pump();
            Assert(bar.TodoFlyout is null, "同一分钟内反复开关待办信息栏也应正常关闭");
        }
        finally
        {
            bar.Close();
            pump();
        }
    }

    private static void CapsuleExpandsWithinItsSurface(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.DisplayMode = TopBarDisplayMode.Capsule;
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        using var sampler = new WindowsPerformanceSamplingService();
        var item = new TodoItemState { Text = "今天的事项", Date = DateTime.Today };
        var tomorrow = new TodoItemState { Text = "明天的事项", Date = DateTime.Today.AddDays(1) };
        var environment = new FakeEnvironment { TodoList = new TopBarTodoList("待办", [item, tomorrow]) };
        var bar = new TopBarWindow(state, new DesktopStyleState(), environment, sampler, enableExperimentalCapsule: true);
        var previousMotion = IceBlueDockWindow.Motion;
        IceBlueDockWindow.Motion = MotionLevel.Off;
        show(bar);
        try
        {
            var surface = (Border)bar.FindName("Surface");
            Assert(surface.Width <= 180 && surface.Height == 40, "空闲胶囊应为紧凑状态岛");
            Assert(!AllButtons(bar).Any(button => button.IsVisible
                && AutomationProperties.GetName(button) == "展开搜索"), "胶囊不应常驻搜索图标");
            var summary = ButtonByName(bar, "今日待办概要，点击查看列表");
            Invoke(summary);
            pump();
            Assert(bar.CapsuleDetailsExpanded && bar.TodoFlyout is null,
                "胶囊应在自身表面展开，不能另开待办 Popup");
            Assert(surface.Width == 352 && surface.Height > 80, "展开应增大同一胶囊表面的宽高");
            var texts = DescendantTextBlocks(bar.CapsuleDetailsElement!).Select(text => text.Text).ToList();
            Assert(texts.Contains("今天的事项") && !texts.Contains("明天的事项"),
                "展开内容应复用今日列表的日期范围");
            Invoke(summary);
            pump();
            Assert(!bar.CapsuleDetailsExpanded && bar.CapsuleDetailsElement is null
                && surface.Width <= 180 && surface.Height == 40, "再次点击应收起并恢复紧凑尺寸");
            Invoke(summary);
            bar.TriggerSearch();
            Assert(!bar.CapsuleDetailsExpanded && environment.LauncherToggles == 1,
                "保留的搜索入口应收起待办并切换冰蓝总览卡");
        }
        finally
        {
            bar.Close();
            IceBlueDockWindow.Motion = previousMotion;
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
