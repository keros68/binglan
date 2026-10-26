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

    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint GetCapture();

    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
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

    private static void MoveMouseTo(int x, int y)
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

    private static void BeginFocusedSearch(TopBarWindow bar, Action pump)
    {
        // Renderer tests call the session directly, without the foreground grant
        // provided by a real user click. Establish that precondition before input.
        var input = bar.FloatingSearchWindow as Window ?? bar;
        if (input.IsVisible) ForceForeground(new WindowInteropHelper(input).Handle);
        pump();
        bar.BeginSearchSession();
        input = bar.FloatingSearchWindow as Window ?? bar;
        var handle = new WindowInteropHelper(input).Handle;
        ForceForeground(handle);
        bar.SearchBox?.Focus();
        Assert(WaitUntil(pump, () => GetForegroundWindow() == handle
            && bar.SearchSessionActive && bar.SearchBox?.IsKeyboardFocused == true),
            "测试输入前搜索窗口应在前台并持有键盘焦点");
    }

    internal static void Run(Action<Window> show, Action pump)
    {
        ShowModulesAndStyles(show, pump);
        ModuleSwitchesRemoveButtons(show, pump);
        ClicksOpenSettings(show, pump);
        SurfacePaintsGlass(show, pump);
        TodoFlyoutShowsItemsAndCloses(show, pump);
        WeatherHoverOpensTooltip(show, pump);
        SearchShowsResultsAndOpens(show, pump);
        SearchImeKeysYieldAndOpenDefers(show, pump);
        SearchSessionTogglesActivation(show, pump);
        SearchModuleSwitchHidesBox(show, pump);
        RateDigitsDoNotShiftSearchBox(show, pump);
        SearchBoxSitsAtBarCentre(show, pump);
        RealClickOnResultOpensIt(show, pump);
        SearchResultWindowDoesNotResize(show, pump);
        SearchPresentationModes(show, pump);
        EverythingProtocolAndFallback(show, pump);
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

    // ---------------------------------------------------------------- 搜索

    private const long WsExNoActivateFlag = 0x08000000;

    private static IReadOnlyList<TopBarSearchCandidate> SearchSource() =>
    [
        new TopBarSearchCandidate(
            TopBarSearchItemKind.App, "微信", "微信.lnk", @"C:\Users\a\Desktop\微信.lnk"),
        new TopBarSearchCandidate(
            TopBarSearchItemKind.App, "文件资源管理器", "explorer.exe", @"C:\Windows\explorer.exe"),
        new TopBarSearchCandidate(
            TopBarSearchItemKind.Folder, "云盘资料", "云盘资料", @"C:\Users\a\Desktop\云盘资料"),
        new TopBarSearchCandidate(
            TopBarSearchItemKind.File, "季度报告", "季度报告.docx", @"C:\Users\a\Desktop\季度报告.docx")
    ];

    /// <summary>
    /// Drives the full search flow without a real keyboard: a session loads the
    /// sources, typing ranks them, the pinyin initials reach Chinese names, a row
    /// click opens exactly that result, and the session end clears box and flyout.
    /// </summary>
    private static void SearchShowsResultsAndOpens(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        using var sampler = new WindowsPerformanceSamplingService();
        var environment = new FakeEnvironment { SearchCandidates = SearchSource() };
        var bar = new TopBarWindow(state, new DesktopStyleState(), environment, sampler);
        show(bar);
        GetCursorPos(out var originalCursor);
        try
        {
            var box = bar.SearchBox
                ?? throw new InvalidOperationException("搜索模块应提供输入框");
            Assert(box.Text == string.Empty, "会话开始前输入框为空");

            BeginFocusedSearch(bar, pump);
            Assert(WaitUntil(pump, () => environment.CandidateReads > 0),
                "会话开始应在后台读取搜索数据源");

            TypeQueryAndAwaitResults(bar, environment, "wx", pump);
            var flyout = bar.SearchFlyout
                ?? throw new InvalidOperationException("有结果时应打开结果浮层");
            Assert(flyout.IsOpen, "结果浮层应处于打开状态");
            Assert(bar.SearchResults[0].DisplayName == "微信", "首字母 wx 应首选微信");
            // The popup's ScrollViewer only gains visual children once its template is
            // applied, which needs a real layout pass; the logical tree is immediate.
            Assert(WaitUntil(pump, () => LogicalDescendants(flyout.Child)
                    .OfType<TextBlock>()
                    .Any(text => text.Text.Contains("微信"))),
                "浮层应显示应用名称");

            box.Text = "ypzl";
            bar.PumpSearch();
            Assert(WaitUntil(pump, () => bar.SearchResults.Count == 1
                && bar.SearchResults[0].DisplayName == "云盘资料"),
                "新关键词应重新排序出文件夹");

            // One click opens exactly the clicked row's target.
            box.Text = "季度报告";
            bar.PumpSearch();
            Assert(WaitUntil(pump, () => bar.SearchResults.Count == 1), "文件名匹配命中");
            LogicalDescendants(bar.SearchFlyout?.Child)
                .OfType<Border>()
                .First(row => row.Tag is 0)
                .RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice,
                    0,
                    System.Windows.Input.MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonUpEvent
                });
            pump();
            Assert(environment.OpenedTargets.Count == 1
                && environment.OpenedTargets[0].EndsWith("季度报告.docx"),
                "单击应打开对应结果");
            Assert(!bar.SearchSessionActive, "打开结果后结束会话");
            Assert(box.Text == string.Empty, "会话结束应清空输入框");
            Assert(bar.SearchFlyout is null, "会话结束应关闭结果浮层");
            Assert(!((Panel)bar.FindName("CenterHost")).Children.OfType<Popup>().Any(),
                "会话结束应释放浮层的结果控件和路径引用");

            // A second session opens its first result with the keyboard: physical Enter
            // reports Key.Return (WPF's Key.Enter is a different value no real key
            // produces), which once slipped past the box's key handling.
            BeginFocusedSearch(bar, pump);
            pump();
            TypeQueryAndAwaitResults(bar, environment, "wx", pump);
            PressKey(box, System.Windows.Input.Key.Return);
            pump();
            Assert(environment.OpenedTargets.Count == 2
                && environment.OpenedTargets[1].EndsWith("微信.lnk"),
                "回车应打开选中的第一条结果");
            Assert(!bar.SearchSessionActive && box.Text == string.Empty,
                "回车打开后应结束会话并清空输入框");

            // Regression checks: non-wrapping arrow keys, stationary mouse hover,
            // incremental stage-2 row append preserving selection, cross-row press/release,
            // and flyout gap right-click not bubbling to TopBarWindow.ContextMenu.
            BeginFocusedSearch(bar, pump);
            pump();
            // These renderer checks supply rows directly; finish the empty query's
            // pending cleanup before injecting them into the active session.
            bar.PumpSearch();
            pump();
            var render = typeof(TopBarWindow).GetMethod("RenderSearchResults",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var stage1 = SearchSource().Take(2).ToList();
            render.Invoke(bar, [stage1, "测试", "正在查询文件索引…"]);
            Thread.Sleep(300);
            pump();
            Assert(bar.SearchResults.Count == stage1.Count,
                $"首阶段结果应在延迟回调后保留（输入 '{box.Text}'，结果 {bar.SearchResults.Count}，会话 {bar.SearchSessionActive}）");
            Assert(bar.SearchSelectedIndex == 0, "首阶段渲染默认选中第 0 行");
            // Keep the physical pointer outside the rows while injecting keyboard
            // and stationary-hover events, so layout-generated mouse moves cannot select a row.
            var inputPoint = box.PointToScreen(new Point(box.ActualWidth / 2, box.ActualHeight / 2));
            MoveMouseTo((int)inputPoint.X, (int)inputPoint.Y);
            pump();
            PressKey(box, System.Windows.Input.Key.Up);
            pump();
            Assert(bar.SearchSelectedIndex == 0, "在首行按上方向键不应首尾循环到末行");
            PressKey(box, System.Windows.Input.Key.Down);
            pump();
            Assert(bar.SearchSelectedIndex == 1, "按下方向键应移动到第 1 行");
            PressKey(box, System.Windows.Input.Key.Down);
            pump();
            Assert(bar.SearchSelectedIndex == 1, "在末行按下方向键不应首尾循环回首行");

            var rowsBefore = LogicalDescendants(bar.SearchFlyout?.Child).OfType<Border>().Where(r => r.Tag is int).ToList();
            // Stationary mouse enter on row 0 must not steal keyboard selection from row 1.
            GetCursorPos(out var stationaryCursor);
            rowsBefore[0].RaiseEvent(new System.Windows.Input.MouseEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0)
            {
                RoutedEvent = UIElement.MouseEnterEvent
            });
            pump();
            GetCursorPos(out var afterHoverCursor);
            Assert(bar.SearchSelectedIndex == 1,
                $"鼠标未发生位移时悬停事件不应抢走键盘选中项（鼠标 {stationaryCursor.X},{stationaryCursor.Y} → {afterHoverCursor.X},{afterHoverCursor.Y}）");

            // Stage-2 append keeps existing row Border instances and preserves selected index 1.
            var stage2 = SearchSource().Take(3).ToList();
            render.Invoke(bar, [stage2, "测试", "桌面与应用 · Windows 已索引文件"]);
            pump();
            var rowsAfter = LogicalDescendants(bar.SearchFlyout?.Child).OfType<Border>().Where(r => r.Tag is int).ToList();
            Assert(rowsAfter.Count == 3
                && ReferenceEquals(rowsBefore[0], rowsAfter[0])
                && ReferenceEquals(rowsBefore[1], rowsAfter[1])
                && bar.SearchSelectedIndex == 1,
                $"二阶段追加文件结果应复用已有行控件并保持当前选中项不跳变（行数 {rowsAfter.Count}，" +
                $"选中 {bar.SearchSelectedIndex}，输入 {box.Text}，会话 {bar.SearchSessionActive}，" +
                $"候选读取 {environment.CandidateReads}，结果 {string.Join(',', bar.SearchResults.Select(item => item.DisplayName))}）");

            // Pressing on row 0 and releasing on row 1 must not open row 1.
            var openedBeforeCrossClick = environment.OpenedTargets.Count;
            rowsAfter[0].RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent
            });
            rowsAfter[1].RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent
            });
            pump();
            Assert(environment.OpenedTargets.Count == openedBeforeCrossClick,
                "跨行按下与松开不应误触打开结果");

            // Right-clicking on the flyout card gap/footer must be handled and never open TopBarWindow.ContextMenu.
            var backing = (FrameworkElement)bar.SearchFlyout!.Child;
            var card = backing as Border ?? ((Grid)backing).Children.OfType<Border>().Single();
            var rightUpArgs = new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Right)
            {
                RoutedEvent = UIElement.MouseRightButtonUpEvent
            };
            card.RaiseEvent(rightUpArgs);
            pump();
            Assert(rightUpArgs.Handled && bar.ContextMenu?.IsOpen != true,
                "在搜索弹层缝隙或底部右键不应冒泡弹出顶栏退出菜单");
        }
        finally
        {
            bar.Close();
            SetCursorPos(originalCursor.X, originalCursor.Y);
            pump();
        }
    }

    /// <summary>
    /// Keys a composing IME wrapped (Key.ImeProcessed) belong to the IME: opening a
    /// result or ending the session from inside one tears the composition down
    /// re-entrantly and fail-fasts the process (real-machine crash with WeChat IME:
    /// composition Enter → synchronous session end → WPF text-store FailFast). A
    /// plain Return may open, but only after the key's own processing finished.
    /// </summary>
    private static void SearchImeKeysYieldAndOpenDefers(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        using var sampler = new WindowsPerformanceSamplingService();
        var environment = new FakeEnvironment { SearchCandidates = SearchSource() };
        var bar = new TopBarWindow(state, new DesktopStyleState(), environment, sampler);
        show(bar);
        try
        {
            var box = bar.SearchBox
                ?? throw new InvalidOperationException("搜索模块应提供输入框");
            Assert(InputMethod.GetPreferredImeState(box) == InputMethodState.On,
                "搜索框获得焦点时应启用输入法，避免沿用关闭状态而直接输入英文字母");
            Assert(InputMethod.GetPreferredImeConversionMode(box) == ImeConversionModeValues.Native,
                "搜索框应首选本地文字转换，启用输入法但仍为英文转换也无法输入中文");
            bar.BeginSearchSession();
            if (InputLanguageManager.Current.CurrentInputLanguage.TwoLetterISOLanguageName == "zh"
                && box.IsKeyboardFocused)
            {
                InputMethod.Current.ImeConversionMode = ImeConversionModeValues.Alphanumeric;
                bar.EndSearchSession();
                bar.BeginSearchSession();
                Assert((InputMethod.Current.ImeConversionMode & ImeConversionModeValues.Native) != 0,
                    "重新打开搜索应恢复中文转换，即使输入框保留逻辑焦点、没有再次触发焦点事件");
            }
            Assert(WaitUntil(pump, () => bar.SearchSourcesLoaded), "输入前后台候选应已发布到顶栏");
            box.Text = "wx";
            bar.PumpSearch();
            Assert(WaitUntil(pump, () => bar.SearchResults.Count > 0),
                $"搜索应有结果（已发布 {bar.SearchSourcesLoaded}，候选读取 {environment.CandidateReads}，" +
                $"会话 {bar.SearchSessionActive}，组合中 {bar.SearchSuspendedForComposition}）");

            // 组合态中的键：让渡给输入法（合成事件无法伪造 ImeProcessedKey，恰好只驱动
            // Key 编码这条门控），既不打开结果也不结束会话。
            var opened = environment.OpenedTargets.Count;
            PressKey(box, System.Windows.Input.Key.ImeProcessed);
            pump();
            Assert(environment.OpenedTargets.Count == opened, "组合态按键不得打开结果");
            Assert(bar.SearchSessionActive, "组合态按键不得结束会话");
            box.Text = "ni";
            bar.PumpSearch();
            pump();
            Assert(bar.SearchFlyout is null && bar.SearchResults.Count == 0,
                "拼音组合态不能弹出搜索结果遮挡输入法候选");
            box.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,
                new TextComposition(InputManager.Current, box, "微信"))
                { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
            box.Text = "wx";
            bar.PumpSearch();
            Assert(WaitUntil(pump, () => bar.SearchResults.Count > 0), "组合提交后应恢复搜索");

            // 普通回车：同步栈内不得有任何动作，一拍后才打开。
            PressKey(box, System.Windows.Input.Key.Return);
            Assert(environment.OpenedTargets.Count == opened,
                "打开动作必须延后到键消息处理之后（同步切换前台会触发输入法重入崩溃）");
            Assert(WaitUntil(pump, () => environment.OpenedTargets.Count == opened + 1),
                "延后一拍后应打开选中的结果");
            Assert(!bar.SearchSessionActive && box.Text == string.Empty,
                "打开后应结束会话并清空输入框");
        }
        finally
        {
            bar.Close();
            pump();
        }
    }

    /// <summary>
    /// The bar is normally never-activating; only a search session lifts that flag so
    /// the box can take keyboard input, and Escape restores it.
    /// </summary>
    private static void SearchSessionTogglesActivation(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        using var sampler = new WindowsPerformanceSamplingService();
        var environment = new FakeEnvironment { SearchCandidates = SearchSource() };
        var bar = new TopBarWindow(state, new DesktopStyleState(), environment, sampler);
        show(bar);
        try
        {
            var handle = new WindowInteropHelper(bar).Handle;
            var style = (long)GetWindowLongPtr(handle, GwlExStyle);
            Assert((style & WsExNoActivateFlag) != 0, "常态应保持永不激活");

            var foregroundBefore = GetForegroundWindow();
            bar.BeginSearchSession();
            pump();
            Assert(bar.SearchSessionActive, "会话应处于激活状态");
            var currentBox = bar.SearchBox;
            currentBox!.Text = "微信";
            bar.ApplyState();
            Assert(ReferenceEquals(currentBox, bar.SearchBox) && bar.SearchSessionActive && currentBox.Text == "微信",
                "未改变模块的外观或显示器刷新不能重建输入框、清除中文或结束会话");
            style = (long)GetWindowLongPtr(handle, GwlExStyle);
            Assert((style & WsExNoActivateFlag) == 0, "会话期间允许窗口激活接收键盘");
            // The dock looks through the session to the window it borrowed the
            // foreground from, so focusing the box cannot read as the desktop back.
            Assert(TopBarWindow.SearchForeground.Holder == handle, "会话前台持有者应是顶栏自身");
            Assert(TopBarWindow.SearchForeground.Borrowed == foregroundBefore,
                "会话应报告借用前台前的原窗口");

            PressKey(bar.SearchBox!, System.Windows.Input.Key.Escape);
            pump();
            Assert(!bar.SearchSessionActive, "Escape 应结束会话");
            Assert(TopBarWindow.SearchForeground == (0, 0), "会话结束应清空前台借用信息");
            style = (long)GetWindowLongPtr(handle, GwlExStyle);
            Assert((style & WsExNoActivateFlag) != 0, "会话结束应恢复永不激活");

            // A click on the box starts the session too, deferred off the mouse
            // message; the deferred begin must survive a dispatcher pass.
            bar.SearchBox!.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice,
                0,
                System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
            });
            Assert(WaitUntil(pump, () => bar.SearchSessionActive), "点击搜索框应开始会话");
            PressKey(bar.SearchBox, System.Windows.Input.Key.Escape);
            pump();
            Assert(!bar.SearchSessionActive, "再次 Escape 应结束会话");
        }
        finally
        {
            bar.Close();
            pump();
        }
    }

    private static void SearchModuleSwitchHidesBox(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        state.Modules.Search = false;
        using var sampler = new WindowsPerformanceSamplingService();
        var bar = new TopBarWindow(state, new DesktopStyleState(), new FakeEnvironment(), sampler);
        show(bar);
        try
        {
            Assert(bar.SearchBox is null, "关闭搜索模块后不应有输入框");
            bar.BeginSearchSession();
            pump();
            Assert(!bar.SearchSessionActive, "模块关闭时会话不应开始");

            state.Modules.Search = true;
            bar.ApplyState();
            pump();
            Assert(bar.SearchBox is not null, "重新开启模块应重建输入框");
        }
        finally
        {
            bar.Close();
            pump();
        }
    }

    /// <summary>
    /// The upload/download digits change width every second; the centred search box must
    /// stay put instead of riding the performance module's resize (the reported flicker).
    /// </summary>
    private static void RateDigitsDoNotShiftSearchBox(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        using var sampler = new ScriptedSamplingService();
        var bar = new TopBarWindow(state, new DesktopStyleState(), new FakeEnvironment(), sampler);
        show(bar);
        try
        {
            var host = bar.SearchHost
                ?? throw new InvalidOperationException("搜索模块应提供输入框宿主");
            double HostX()
            {
                pump();
                return host.PointToScreen(new Point(0, 0)).X;
            }

            sampler.Emit(new PerformanceSnapshot(12, 34, 0, 0, DateTimeOffset.Now));
            var idleX = HostX();
            sampler.Emit(new PerformanceSnapshot(
                12, 34, 1023.9d * 1024 * 1024, 1023.9d * 1024 * 1024, DateTimeOffset.Now));
            var busyX = HostX();
            Assert(TextOf(ButtonByName(bar, "性能模块")).Contains("MB/s"),
                "第二次采样应已把网速渲染为 MB/s");
            Assert(Math.Abs(busyX - idleX) < 0.5,
                $"网速数字变宽不应移动居中的搜索框：{idleX} → {busyX}");
        }
        finally
        {
            bar.Close();
            pump();
        }
    }

    /// <summary>
    /// The box is a search field at the top centre of the screen, not a leftover in the
    /// gap between the side modules: its centre must match the bar's centre whatever the
    /// modules around it measure.
    /// </summary>
    private static void SearchBoxSitsAtBarCentre(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        using var sampler = new WindowsPerformanceSamplingService();
        var bar = new TopBarWindow(state, new DesktopStyleState(), new FakeEnvironment(), sampler);
        show(bar);
        try
        {
            var host = bar.SearchHost
                ?? throw new InvalidOperationException("搜索模块应提供输入框宿主");
            pump();
            var barLeft = bar.PointToScreen(new Point(0, 0)).X;
            var barRight = bar.PointToScreen(new Point(bar.ActualWidth, 0)).X;
            var hostLeft = host.PointToScreen(new Point(0, 0)).X;
            var hostRight = host.PointToScreen(new Point(host.ActualWidth, 0)).X;
            Assert(host.ActualWidth >= 140, "搜索框不应窄于最小可用宽度");
            Assert(Math.Abs((hostLeft + hostRight) / 2d - (barLeft + barRight) / 2d) <= 1d,
                $"搜索框应位于顶栏正中：框心 {(hostLeft + hostRight) / 2d}，栏心 {(barLeft + barRight) / 2d}");
        }
        finally
        {
            bar.Close();
            pump();
        }
    }

    /// <summary>
    /// A click on a result row must open that result through the real message path,
    /// and the flyout must never activate — an activating popup loses the bar the
    /// foreground mid-click, the session tears down, and the click lands nowhere (the
    /// result never opens). System mouse input exercises native activation, WPF hit
    /// testing and the row handler after the popup is visible and ready.
    /// </summary>
    private static void RealClickOnResultOpensIt(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        using var sampler = new WindowsPerformanceSamplingService();
        var environment = new FakeEnvironment { SearchCandidates = SearchSource() };
        var bar = new TopBarWindow(state, new DesktopStyleState(), environment, sampler);
        show(bar);
        GetCursorPos(out var originalCursor);
        try
        {
            var box = bar.SearchBox
                ?? throw new InvalidOperationException("搜索模块应提供输入框");
            var focusChanges = new List<string>();
            box.LostKeyboardFocus += (_, args) => focusChanges.Add($"失焦到 {args.NewFocus?.GetType().Name ?? "无"}");
            bar.Deactivated += (_, _) => focusChanges.Add($"顶栏失活，前台 {GetForegroundWindow():X}");
            var barHandle = new WindowInteropHelper(bar).Handle;
            // A real click gives this process Windows' foreground grant. Calling
            // BeginSearchSession from a background test does not receive that grant.
            var boxCentre = box.PointToScreen(new Point(box.ActualWidth / 2, box.ActualHeight / 2));
            var boxX = (int)boxCentre.X;
            var boxY = (int)boxCentre.Y;
            MoveMouseTo(boxX, boxY);
            Assert(WaitUntil(pump, () => WindowFromPoint(new NativePoint { X = boxX, Y = boxY }) == barHandle),
                "点击搜索框前应由顶栏接收原生命中，避免点击其他应用");
            SendMouseInputs(new MouseInput { Flags = MouseLeftDown }, new MouseInput { Flags = MouseLeftUp });
            Assert(WaitUntil(pump, () => GetForegroundWindow() == barHandle && box.IsKeyboardFocused),
                $"真实点击测试应先取得前台和搜索框焦点（前台 {GetForegroundWindow():X}，顶栏 {barHandle:X}，" +
                $"会话 {bar.SearchSessionActive}，WPF 激活 {bar.IsActive}，焦点 {Keyboard.FocusedElement?.GetType().Name ?? "无"}，" +
                $"{string.Join("；", focusChanges)}）");
            Assert(WaitUntil(pump, () => environment.CandidateReads > 0),
                "会话开始应在后台读取搜索数据源");
            TypeQueryAndAwaitResults(bar, environment, "wx", pump);
            var flyout = bar.SearchFlyout
                ?? throw new InvalidOperationException("有结果时应打开结果浮层");
            Assert(WaitUntil(pump, () => LogicalDescendants(flyout.Child)
                    .OfType<Border>()
                    .Any(rowBorder => rowBorder.Tag is int && rowBorder.ActualWidth > 20)),
                "结果行应完成布局");

            var row = LogicalDescendants(flyout.Child)
                .OfType<Border>()
                .First(rowBorder => rowBorder.Tag is int);
            var rowSawClick = false;
            row.MouseLeftButtonUp += (_, _) => rowSawClick = true;
            var cardSawClick = false;
            ((FrameworkElement)flyout.Child).MouseLeftButtonUp += (_, _) => cardSawClick = true;
            var topLeft = row.PointToScreen(new Point(0, 0));
            var bottomRight = row.PointToScreen(new Point(row.ActualWidth, row.ActualHeight));
            var targetX = (int)((topLeft.X + bottomRight.X) / 2d);
            var targetY = (int)((topLeft.Y + bottomRight.Y) / 2d);
            // A stray injected click would land on whatever is really under the cursor,
            // so only click when the row provably sits inside the flyout's own window.
            if (PresentationSource.FromVisual(flyout.Child) is not HwndSource source
                || source.Handle == 0)
            {
                throw new InvalidOperationException("结果浮层应有自己的窗口");
            }
            var rawDownCount = 0;
            var rawUpCount = 0;
            source.AddHook((_, message, _, _, ref _) =>
            {
                if (message == 0x0201)
                {
                    rawDownCount++;
                }
                if (message == 0x0202)
                {
                    rawUpCount++;
                }
                return 0;
            });
            GetWindowRect(source.Handle, out var flyoutRect);
            if (targetX < flyoutRect.Left || targetX >= flyoutRect.Right
                || targetY < flyoutRect.Top || targetY >= flyoutRect.Bottom)
            {
                throw new InvalidOperationException(
                    $"结果行不在浮层窗口内（行心 {targetX},{targetY}，浮层 {flyoutRect.Left},{flyoutRect.Top}" +
                    $"-{flyoutRect.Right},{flyoutRect.Bottom}），拒绝注入点击");
            }

            // The popup must be never-activating: an activating popup loses the bar the
            // foreground on click, the session tears down between the mouse's down and
            // up, and the flyout closes under the cursor before the row sees the
            // release — the reported "found it, but clicking does nothing".
            var flyoutStyle = (long)GetWindowLongPtr(source.Handle, GwlExStyle);
            Assert((flyoutStyle & WsExNoActivateFlag) != 0,
                "结果浮层窗口应永不激活，点击不应拆散搜索会话");

            // The session's bar must be WPF-active for the flyout to route mouse
            // input (a real session always is); recreate that foreground here.
            ForceForeground(barHandle);
            SetCursorPos(targetX, targetY);
            // A layered popup can finish layout before its first pixels are presented.
            // Native hit testing must see the popup before system input enters it.
            Assert(WaitUntil(pump, () => WindowFromPoint(new NativePoint { X = targetX, Y = targetY }) == source.Handle),
                "透明浮层应完成呈现并接受原生鼠标命中");
            // Absolute system input keeps coordinates independent of mouse speed
            // and acceleration. System routing must enter this window before clicks;
            // posted messages alone can be followed by a stale WM_MOUSELEAVE.
            // A separate entry point guarantees movement even at the row centre.
            var entryX = targetX - Math.Min(16, (int)((bottomRight.X - topLeft.X) / 4d));
            // A displaced physical cursor is ambient desktop interference, not a
            // product bug, so the click is retried once when the cursor moved away
            // between the readiness check and the release. A cursor that stayed on
            // the row without opening it must fail loudly instead.
            var openedByClick = false;
            for (var clickAttempt = 1; clickAttempt <= 2 && !openedByClick; clickAttempt++)
            {
                MoveMouseTo(entryX, targetY);
                Assert(WaitUntil(pump, () => row.IsMouseOver), "系统鼠标移动应命中结果行");
                MoveMouseTo(targetX, targetY);
                var ready = WaitUntil(pump, () => GetForegroundWindow() == barHandle
                    && bar.SearchSessionActive && row.IsMouseOver);
                GetCursorPos(out var readyCursor);
                Assert(ready,
                    $"发送点击前顶栏应保持前台，WPF 鼠标应命中结果行（前台 {GetForegroundWindow():X}，顶栏 {barHandle:X}，" +
                    $"会话 {bar.SearchSessionActive}，WPF 激活 {bar.IsActive}，焦点 {box.IsKeyboardFocused}，" +
                    $"鼠标 {readyCursor.X},{readyCursor.Y}，原生命中 {WindowFromPoint(readyCursor):X}，浮层 {source.Handle:X}，" +
                    $"WPF 命中 {System.Windows.Input.Mouse.DirectlyOver?.GetType().Name ?? "无"}，位置 {System.Windows.Input.Mouse.GetPosition(row)}）");
                Assert(WindowFromPoint(readyCursor) == source.Handle,
                    "发送系统点击前鼠标应仍在结果浮层，避免点击其他应用");
                SendMouseInputs(new MouseInput { Flags = MouseLeftDown });
                try
                {
                    Assert(WaitUntil(pump, () => rawDownCount > 0), "结果浮层应收到鼠标按下");
                    // A physical click leaves time for focus events between down and up.
                    // Sending both messages in one batch can hide premature teardown.
                    var releaseAt = Environment.TickCount64 + 150;
                    while (Environment.TickCount64 < releaseAt)
                    {
                        pump();
                        Thread.Sleep(10);
                    }
                    Assert(bar.SearchSessionActive && flyout.IsOpen && box.IsKeyboardFocused,
                        $"按住结果行时搜索框应保留焦点且浮层保持打开（会话 {bar.SearchSessionActive}，浮层 {flyout.IsOpen}，焦点 {System.Windows.Input.Keyboard.FocusedElement?.GetType().Name}，{string.Join("；", focusChanges)}）");
                }
                finally
                {
                    SendMouseInputs(new MouseInput { Flags = MouseLeftUp });
                }
                openedByClick = WaitUntil(pump, () => environment.OpenedTargets.Count >= 1);
                if (openedByClick)
                {
                    break;
                }
                GetCursorPos(out var actualCursor);
                var displaced = Math.Abs(actualCursor.X - targetX) > 16 || Math.Abs(actualCursor.Y - targetY) > 16;
                if (clickAttempt >= 2 || !displaced)
                {
                    GetWindowRect(source.Handle, out var popupRect);
                    var cursorWindow = WindowFromPoint(actualCursor);
                    GetWindowThreadProcessId(cursorWindow, out var cursorProcess);
                    throw new InvalidOperationException(
                        "点击结果行应打开该结果" +
                        $"（浮层收到原始按下/抬起 {rawDownCount}/{rawUpCount}，" +
                        $"顶栏 WPF 激活 {bar.IsActive}，鼠标命中 {System.Windows.Input.Mouse.DirectlyOver?.GetType().Name ?? "无"}，" +
                        $"卡片收到抬起 {cardSawClick}，行收到抬起 {rowSawClick}，" +
                        $"会话活跃 {bar.SearchSessionActive}，结果数 {bar.SearchResults.Count}，" +
                        $"浮层矩形 {popupRect.Left},{popupRect.Top}-{popupRect.Right},{popupRect.Bottom}，" +
                        $"行心 {targetX},{targetY}，实际鼠标 {actualCursor.X},{actualCursor.Y}，" +
                        $"命中窗口 {cursorWindow:X}/进程 {cursorProcess}，浮层 {source.Handle:X}，" +
                        $"前台 {GetForegroundWindow():X}，顶栏 {new WindowInteropHelper(bar).Handle:X}，捕获 {GetCapture():X}）");
                }
                Assert(bar.SearchSessionActive && flyout.IsOpen, "点击被环境移开重试前，会话与浮层应保持打开");
            }
            Assert(environment.OpenedTargets[0] == @"C:\Users\a\Desktop\微信.lnk",
                "点击的首行应是微信");
            Assert(environment.OpenedTargets.Count == 1 && rowSawClick && cardSawClick,
                "一次系统点击应经过结果行并且只打开一次");
            Assert(!bar.SearchSessionActive, "打开结果后搜索会话应结束");
        }
        finally
        {
            SetCursorPos(originalCursor.X, originalCursor.Y);
            bar.Close();
            pump();
        }
    }

    private static void SearchResultWindowDoesNotResize(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        using var sampler = new WindowsPerformanceSamplingService();
        var bar = new TopBarWindow(state, new DesktopStyleState(), new FakeEnvironment(), sampler);
        show(bar);
        try
        {
            var render = typeof(TopBarWindow).GetMethod("RenderSearchResults",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var one = SearchSource().Take(1).ToList();
            render.Invoke(bar, [one, "one", "test"]);
            pump();
            var backing = (FrameworkElement)bar.SearchFlyout!.Child;
            var card = backing as Border ?? ((Grid)backing).Children.OfType<Border>().Single();
            var handle = ((HwndSource)PresentationSource.FromVisual(backing)).Handle;
            GetWindowRect(handle, out var small);
            var many = Enumerable.Range(0, 24).Select(index => new TopBarSearchCandidate(
                TopBarSearchItemKind.File, $"测试 {index}", $"test{index}.txt", $"C:\\BingLan-UiTest\\test{index}.txt")).ToList();
            render.Invoke(bar, [many, "many", "test"]);
            pump();
            GetWindowRect(handle, out var large);
            Assert(large.Bottom - large.Top == small.Bottom - small.Top && card.ActualHeight > 250,
                "24 条结果使用相同原生窗口大小并提供滚动区域");
            render.Invoke(bar, [one, "one", "test"]);
            pump();
            GetWindowRect(handle, out var restored);
            Assert(restored.Bottom - restored.Top == small.Bottom - small.Top && card.ActualHeight < 150,
                "结果减少只缩小可见卡片，不触发原生窗口调整");
            Assert(card.ActualHeight < backing.ActualHeight && backing.InputHitTest(new Point(10, backing.ActualHeight - 5)) is null,
                "透明空白区域不能挡住下方窗口");
            var empty = backing.PointToScreen(new Point(10, backing.ActualHeight - 5));
            var dpi = VisualTreeHelper.GetDpi(backing);
            var clicks = 0;
            var receiverButton = new Button { Content = "点击穿透测试" };
            receiverButton.Click += (_, _) => clicks++;
            var receiver = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, ShowActivated = false, Owner = bar,
                Width = 100, Height = 40, Left = empty.X / dpi.DpiScaleX - 50,
                Top = empty.Y / dpi.DpiScaleY - 20, Content = receiverButton
            };
            GetCursorPos(out var cursor);
            try
            {
                show(receiver);
                var receiverHandle = new WindowInteropHelper(receiver).Handle;
                NativeMethods.SetWindowPos(receiverHandle, handle, 0, 0, 0, 0,
                    NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
                pump();
                SetCursorPos((int)empty.X, (int)empty.Y);
                pump();
                GetCursorPos(out var actual);
                var hit = WindowFromPoint(actual);
                Assert(hit == handle || hit == receiverHandle, "穿透测试仅点击两个测试窗口的交叠区域");
                SendMouseInputs(new MouseInput { Flags = MouseLeftDown }, new MouseInput { Flags = MouseLeftUp });
                Assert(WaitUntil(pump, () => clicks == 1), "透明缓冲区域的真实点击应交给下方按钮");
            }
            finally { SetCursorPos(cursor.X, cursor.Y); receiver.Close(); pump(); }
        }
        finally { bar.Close(); pump(); }
    }

    internal static void SearchResultContextMenuStaysOpen(Action<Window> show, Action pump)
    {
        foreach (var mode in Enum.GetValues<SearchPresentation>())
        {
            var state = BarState();
            state.VisibilityMode = TopBarVisibilityMode.SmartHide;
            state.SearchPresentation = mode;
            var surface = mode == SearchPresentation.TopBar ? "#FFFFFF"
                : mode == SearchPresentation.Floating ? "#00FFFF" : "#18222F";
            state.FollowCardLook = false;
            state.SurfaceColor = surface;
            state.SurfaceOpacity = 0.08d;
            state.SearchFollowCardLook = false;
            state.SearchSurfaceColor = surface;
            state.SearchSurfaceOpacity = 0.08d;
            using var sampler = new WindowsPerformanceSamplingService();
            var environment = new FakeEnvironment { SearchCandidates = SearchSource() };
            var bar = new TopBarWindow(state, new DesktopStyleState(), environment, sampler);
            show(bar);
            GetCursorPos(out var cursor);
            try
            {
                BeginFocusedSearch(bar, pump);
                var box = bar.SearchBox ?? throw new InvalidOperationException("搜索模块应提供输入框");
                Assert(WaitUntil(pump, () => box.IsKeyboardFocused && environment.CandidateReads > 0), "右键测试先取得搜索焦点");
                InputMethod.Current.ImeConversionMode = ImeConversionModeValues.Alphanumeric;
                Assert(WaitUntil(pump, () => bar.SearchSourcesLoaded), "右键测试输入前后台候选应已发布");
                box.Text = "wx";
                bar.PumpSearch();
                Assert(WaitUntil(pump, () => bar.SearchResults.Count > 0 && LogicalDescendants(bar.SearchFlyout?.Child)
                    .OfType<Border>().Any(row => row.Tag is int && row.ActualWidth > 20)), "右键测试先等待结果布局");
                Assert(WaitUntil(pump, () => !LogicalDescendants(bar.SearchFlyout?.Child).OfType<TextBlock>()
                    .Any(text => text.Text == "正在查询文件索引…")), "等待当前索引回复后再定位右键行");
                var row = LogicalDescendants(bar.SearchFlyout!.Child).OfType<Border>().First(item => item.Tag is int);
                var point = row.PointToScreen(new Point(row.ActualWidth / 2, row.ActualHeight / 2));
                var popup = (HwndSource)PresentationSource.FromVisual(bar.SearchFlyout.Child);
                SetCursorPos((int)point.X, (int)point.Y);
                Assert(WaitUntil(pump, () => WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y }) == popup.Handle), "右键前等待原生命中结果窗口");
                MoveMouseTo((int)point.X - 12, (int)point.Y);
                Assert(WaitUntil(pump, () => row.IsMouseOver), "右键前鼠标进入结果行");
                MoveMouseTo((int)point.X, (int)point.Y);
                pump();
                GetCursorPos(out var actual);
                Assert(WindowFromPoint(actual) == popup.Handle, "右键只发给结果窗口");
                SendMouseInputs(new MouseInput { Flags = MouseRightDown });
                try
                {
                    var releaseAt = Environment.TickCount64 + 150;
                    while (Environment.TickCount64 < releaseAt) { pump(); Thread.Sleep(10); }
                    Assert(bar.SearchSessionActive && bar.SearchFlyout.IsOpen, "右键按住期间结果保持打开");
                }
                finally { SendMouseInputs(new MouseInput { Flags = MouseRightUp }); }
                Assert(WaitUntil(pump, () => row.ContextMenu?.IsOpen == true), $"{mode} 结果右键应打开菜单");
                var menu = row.ContextMenu!;
                var until = Environment.TickCount64 + 300;
                while (Environment.TickCount64 < until) { pump(); Thread.Sleep(10); }
                Assert(menu.IsOpen && bar.SearchSessionActive && bar.SearchFlyout?.IsOpen == true, "菜单取得焦点后搜索和结果不能消失");
                var card = LogicalDescendants(bar.SearchFlyout!.Child).OfType<Border>().First();
                var background = ((SolidColorBrush)card.Background).Color;
                Assert(background.A == 255, "低透明度搜索框的结果面板应遮住桌面内容");
                foreach (var text in LogicalDescendants(row).OfType<TextBlock>())
                    AssertSearchContrast(((SolidColorBrush)text.Foreground).Color, background, text.Opacity);
                var menuBackground = ((SolidColorBrush)menu.Background).Color;
                Assert(menuBackground.A == 255, "命令菜单应有独立的不透明底色");
                foreach (var item in menu.Items.OfType<MenuItem>())
                {
                    AssertSearchContrast(((SolidColorBrush)item.Foreground).Color, menuBackground);
                    item.ApplyTemplate();
                    var gesture = (TextBlock)item.Template.FindName("GestureText", item);
                    AssertSearchContrast(((SolidColorBrush)gesture.Foreground).Color, menuBackground, gesture.Opacity);
                    Assert(item.ActualHeight >= 34, "菜单项应有足够的点击高度");
                }
                if (Environment.GetEnvironmentVariable("BINGLAN_UI_SEARCH_CAPTURE") is { Length: > 0 } captureDirectory)
                {
                    System.IO.Directory.CreateDirectory(captureDirectory);
                    SaveSearchPreview(card, System.IO.Path.Combine(captureDirectory, $"search-{mode}.png"));
                    SaveSearchPreview(menu, System.IO.Path.Combine(captureDirectory, $"menu-{mode}.png"));
                }
                // A late index reply must keep the menu's placement row alive.
                bar.PumpSearch();
                until = Environment.TickCount64 + 300;
                while (Environment.TickCount64 < until) { pump(); Thread.Sleep(10); }
                Assert(menu.IsOpen && LogicalDescendants(bar.SearchFlyout?.Child).Contains(row), "索引更新期间保留右键结果");
                menu.IsOpen = false;
                Assert(WaitUntil(pump, () => box.IsKeyboardFocused), "关闭菜单后恢复输入焦点");
                Assert(bar.SearchSessionActive && box.Text == "wx", "关闭菜单保留查询");
                row = LogicalDescendants(bar.SearchFlyout!.Child).OfType<Border>().First(item => item.Tag is int);
                PressKey(box, Key.Apps);
                Assert(WaitUntil(pump, () => row.ContextMenu?.IsOpen == true), "键盘菜单键也能操作选中结果");
                menu = row.ContextMenu!;
                var location = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "打开文件位置"));
                location.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert(WaitUntil(pump, () => environment.OpenedLocations.Count == 1), "文件位置操作应被执行");
                Assert(environment.OpenedLocations[0] == @"C:\Users\a\Desktop\微信.lnk" && environment.OpenedTargets.Count == 0
                    && !bar.SearchSessionActive && !menu.IsOpen, "位置操作使用右键候选且只执行一次");
            }
            finally
            {
                SetCursorPos(cursor.X, cursor.Y);
                bar.Close();
                pump();
            }
        }
    }

    private static void AssertSearchContrast(Color foreground, Color background, double opacity = 1d)
    {
        static double Linear(double value) => value <= 0.04045d ? value / 12.92d : Math.Pow((value + 0.055d) / 1.055d, 2.4d);
        static double Luminance(double red, double green, double blue) =>
            0.2126d * Linear(red) + 0.7152d * Linear(green) + 0.0722d * Linear(blue);
        var alpha = foreground.A / 255d * opacity;
        var text = Luminance((foreground.R * alpha + background.R * (1 - alpha)) / 255d,
            (foreground.G * alpha + background.G * (1 - alpha)) / 255d,
            (foreground.B * alpha + background.B * (1 - alpha)) / 255d);
        var surface = Luminance(background.R / 255d, background.G / 255d, background.B / 255d);
        var contrast = (Math.Max(text, surface) + 0.05d) / (Math.Min(text, surface) + 0.05d);
        Assert(contrast >= 4.5d, $"搜索文字与背景对比度应达到 4.5:1，实际 {contrast:F2}:1");
    }

    private static void SaveSearchPreview(FrameworkElement element, string path)
    {
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
    }

    private static void SearchPresentationModes(Action<Window> show, Action pump)
    {
        GetCursorPos(out var originalCursor);
        try
        {
            foreach (var mode in new[] { SearchPresentation.Floating, SearchPresentation.Icon })
            {
                var state = BarState();
                state.VisibilityMode = TopBarVisibilityMode.SmartHide;
                state.SearchPresentation = mode;
                using var sampler = new WindowsPerformanceSamplingService();
                var environment = new FakeEnvironment { SearchCandidates = SearchSource() };
                var bar = new TopBarWindow(state, new DesktopStyleState(), environment, sampler);
                show(bar);
                try
                {
                    var floating = bar.FloatingSearchWindow ?? throw new InvalidOperationException("应创建独立搜索宿主");
                    Assert(floating.IsVisible == (mode == SearchPresentation.Floating), "图标模式默认收起，独立模式默认显示");
                    if (mode == SearchPresentation.Icon)
                    {
                        var expand = LogicalDescendants(bar).OfType<Button>()
                            .Single(button => AutomationProperties.GetName(button) == "展开搜索");
                        expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        pump();
                    }
                    else
                    {
                        bar.BeginSearchSession();
                    }
                    ForceForeground(new WindowInteropHelper(floating).Handle);
                    bar.SearchBox!.Focus();
                    Assert(WaitUntil(pump, () => bar.SearchSessionActive && bar.SearchBox.IsKeyboardFocused),
                        "独立搜索应激活自己的输入框");
                    Assert(floating.IsVisible, "热键会话应展开独立搜索框");
                    Assert(Window.GetWindow(bar.SearchHost) == floating, "独立模式应复用原搜索控件");
                    TypeQueryAndAwaitResults(bar, environment, "wx", pump);
                    PressKey(bar.SearchBox, System.Windows.Input.Key.Return);
                    Assert(WaitUntil(pump, () => environment.OpenedTargets.Count == 1), "独立搜索 Enter 应打开结果");
                    Assert(floating.IsVisible == (mode == SearchPresentation.Floating), "完成搜索后图标模式应收起");

                    if (mode == SearchPresentation.Floating)
                    {
                        var widthHandle = (Thumb)floating.FindName("WidthHandle");
                        var oldWidth = floating.Width;
                        var cursorStart = floating.PointToScreen(new Point(40, 20));
                        SetCursorPos((int)cursorStart.X, (int)cursorStart.Y);
                        GetCursorPos(out var start);
                        widthHandle.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                        SetCursorPos(start.X + 90, start.Y);
                        widthHandle.RaiseEvent(new DragDeltaEventArgs(90, 0) { RoutedEvent = Thumb.DragDeltaEvent });
                        widthHandle.RaiseEvent(new DragCompletedEventArgs(90, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
                        pump();
                        Assert(floating.Width > oldWidth && state.SearchWindowWidth == floating.Width && environment.LayoutSaves == 1,
                            $"调宽手柄应保存独立搜索框宽度（原宽 {oldWidth}，当前 {floating.Width}，保存 {state.SearchWindowWidth}，次数 {environment.LayoutSaves}）");
                        var moveHandle = (Thumb)floating.FindName("MoveHandle");
                        var oldLeft = floating.Left;
                        GetCursorPos(out start);
                        moveHandle.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                        SetCursorPos(start.X + 60, start.Y + 30);
                        moveHandle.RaiseEvent(new DragDeltaEventArgs(60, 30) { RoutedEvent = Thumb.DragDeltaEvent });
                        moveHandle.RaiseEvent(new DragCompletedEventArgs(60, 30, false) { RoutedEvent = Thumb.DragCompletedEvent });
                        pump();
                        Assert(floating.Left != oldLeft && state.SearchWindowPlacement.Left == floating.Left && environment.LayoutSaves == 2,
                            "专用拖动手柄应移动并保存位置");
                        floating.Left = -10000;
                        floating.Top = -10000;
                        floating.RecoverPlacement();
                        pump();
                        Assert(floating.Left > -10000 && floating.Top > -10000, "屏幕外位置应恢复到可见工作区");
                        floating.Close();
                        pump();
                        Assert(!floating.IsVisible, "关闭独立框应收起并保留热键宿主");
                        bar.BeginSearchSession();
                        pump();
                        Assert(floating.IsVisible, "收起的独立框应能由搜索会话重新展开");
                    }
                    var sharedBox = bar.SearchBox;
                    if (mode == SearchPresentation.Floating)
                    {
                        state.SearchPresentation = SearchPresentation.Icon;
                        bar.ApplyState();
                        Assert(ReferenceEquals(floating, bar.FloatingSearchWindow) && ReferenceEquals(sharedBox, bar.SearchBox) && !floating.IsVisible,
                            "独立框与图标切换应复用窗口和输入控件");
                        state.SearchPresentation = SearchPresentation.Floating;
                        state.SearchSurfaceColor = "#F3F6FC";
                        state.SearchSurfaceOpacity = 0.8d;
                        state.SearchCornerRadius = 18d;
                        bar.ApplyState();
                        Assert(ReferenceEquals(floating, bar.FloatingSearchWindow) && ((SolidColorBrush)bar.SearchBox!.Foreground).Color.R < 100,
                            "浅色搜索框应沿用宿主并自动显示深色文字");
                        var frame = (Border)floating.FindName("SearchFrame");
                        Assert(frame.CornerRadius.TopLeft == 18d && ((SolidColorBrush)frame.Background).Color.A == 204,
                            "搜索框透明度和圆角应实际应用到窗口表面");
                    }
                    state.SearchPresentation = SearchPresentation.TopBar;
                    bar.ApplyState();
                    pump();
                    Assert(bar.FloatingSearchWindow is null && !floating.IsVisible && Window.GetWindow(bar.SearchHost) == bar
                        && ReferenceEquals(sharedBox, bar.SearchBox),
                        "切回顶栏应隐藏独立宿主并恢复居中输入框");
                    var floatingHandle = new WindowInteropHelper(floating).Handle;
                    state.SearchPresentation = SearchPresentation.Floating;
                    bar.ApplyState();
                    pump();
                    Assert(ReferenceEquals(floating, bar.FloatingSearchWindow)
                        && new WindowInteropHelper(bar.FloatingSearchWindow!).Handle == floatingHandle
                        && ReferenceEquals(sharedBox, bar.SearchBox), "从顶栏再次切出也复用同一个宿主和输入框");
                    state.Modules.Search = false;
                    bar.ApplyState();
                    pump();
                    Assert(HwndSource.FromHwnd(floatingHandle) is null && bar.FloatingSearchWindow is null,
                        "关闭搜索模块时释放独立窗口");
                }
                finally
                {
                    bar.Close();
                    pump();
                }
            }
        }
        finally
        {
            SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }

    private static void EverythingProtocolAndFallback(Action<Window> show, Action pump)
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
        var absentState = BarState();
        absentState.VisibilityMode = TopBarVisibilityMode.SmartHide;
        absentState.UseEverything = true;
        using var sampler = new WindowsPerformanceSamplingService();
        var environment = new FakeEnvironment { SearchCandidates = SearchSource() };
        var bar = new TopBarWindow(absentState, new DesktopStyleState(), environment, sampler);
        show(bar);
        try
        {
            bar.BeginSearchSession();
            Assert(WaitUntil(pump, () => environment.CandidateReads > 0), "应继续读取基础候选");
            bar.SearchBox!.Text = "微信";
            bar.PumpSearch();
            Assert(WaitUntil(pump, () => bar.SearchResults.Any(result => result.Path == @"C:\Users\a\Desktop\微信.lnk")),
                "可选 Everything 不应取消桌面与应用搜索");
        }
        finally
        {
            bar.Close();
            pump();
        }
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

    private static void PressKey(UIElement element, System.Windows.Input.Key key)
    {
        var source = PresentationSource.FromVisual(element)
            ?? throw new InvalidOperationException("元素应有呈现源");
        element.RaiseEvent(new System.Windows.Input.KeyEventArgs(
            System.Windows.Input.Keyboard.PrimaryDevice,
            source,
            0,
            key)
        {
            RoutedEvent = UIElement.PreviewKeyDownEvent
        });
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

    /// <summary>
    /// Types a plain query and waits for results. Typing waits for the background
    /// candidate load to be published first: CandidateReads increments before
    /// publication, so a first search could otherwise run against an empty list and
    /// depend on the re-rank rescue winning a race with session teardown on a busy
    /// desktop. The conversion mode is forced to alphanumeric so ambient typing on
    /// the live desktop cannot open a real IME composition over the focused box and
    /// silently suspend every later search.
    /// </summary>
    private static void TypeQueryAndAwaitResults(TopBarWindow bar, FakeEnvironment environment, string query, Action pump)
    {
        InputMethod.Current.ImeConversionMode = ImeConversionModeValues.Alphanumeric;
        Assert(WaitUntil(pump, () => bar.SearchSourcesLoaded), "后台候选应已发布到顶栏");
        bar.SearchBox!.Text = query;
        bar.PumpSearch();
        Assert(WaitUntil(pump, () => bar.SearchResults.Count > 0),
            $"搜索应给出结果（已发布 {bar.SearchSourcesLoaded}，候选读取 {environment.CandidateReads}，" +
            $"会话 {bar.SearchSessionActive}，组合中 {bar.SearchSuspendedForComposition}）");
    }

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject? root)
    {
        if (root is null)
        {
            yield break;
        }
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in LogicalDescendants(child))
            {
                yield return descendant;
            }
        }
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
        internal IReadOnlyList<TopBarSearchCandidate> SearchCandidates { get; set; } = [];
        internal List<string> OpenedTargets { get; } = [];
        internal List<string> OpenedLocations { get; } = [];
        internal int CandidateReads { get; private set; }
        internal int LayoutSaves { get; private set; }
        public void SearchLayoutChanged() => LayoutSaves++;

        public bool Use24HourClock => true;

        public WeatherSnapshot? ReadWeather() => Weather;

        public void RefreshWeatherIfDue()
        {
        }

        public int CountIncompleteTodos() => Todos;

        public TopBarTodoList? ReadTodoList() => TodoList;

        public void TodoItemToggled() => Toggles++;

        public IReadOnlyList<TopBarSearchCandidate> ReadSearchCandidates()
        {
            CandidateReads++;
            return SearchCandidates;
        }

        public void OpenSearchTarget(TopBarSearchCandidate candidate) => OpenedTargets.Add(candidate.Path);

        public void OpenSearchTargetLocation(TopBarSearchCandidate candidate) => OpenedLocations.Add(candidate.Path);

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

        public bool ActivateWindow(nint handle) => false;

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
