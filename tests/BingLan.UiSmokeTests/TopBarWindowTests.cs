using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using BingLan.App.Dock;
using BingLan.App;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.App.Windows;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.TopBar;
using BingLan.App.TopBar;
using BingLan.Core.Dock;
using System.Buffers.Binary;
using System.Text;

/// <summary>
/// The default suite exercises modules in smart-hide mode. The opt-in reserved-bar
/// test registers a real AppBar and sends mouse input to its modules.
/// </summary>
internal static class TopBarWindowTests
{
    private const int GwlExStyle = -20;
    private const uint MakeProbeBorderless = 0x8001;

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
    private const uint MouseLeftDown = 0x0002;
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

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(DockNativeMethods.NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

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

    internal static int RunReservedFlyoutProbe(Application app)
    {
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
        var window = new Window { Title = "冰蓝顶栏浮层测试窗口", Width = 640, Height = 480 };
        window.SourceInitialized += (_, _) =>
        {
            ((HwndSource)PresentationSource.FromVisual(window)).AddHook(MakeBorderless);
        };
        return app.Run(window);

        nint MakeBorderless(nint handle, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (message == MakeProbeBorderless)
            {
                window.WindowStyle = WindowStyle.None;
                window.ResizeMode = ResizeMode.NoResize;
                handled = true;
            }
            return 0;
        }
    }

    internal static void ReservedBarFlyoutsStayOpen(Action<Window> show, Action pump)
    {
        var monitor = MonitorCatalog.GetAll().FirstOrDefault(screen => screen.Bounds.Top == screen.WorkingArea.Top)
            ?? throw new InvalidOperationException("测试需要一块顶边未被预留的显示器");
        var previousForeground = GetForegroundWindow();
        Assert(DockNativeMethods.GetCursorPos(out var previousCursor), "无法读取测试前的鼠标位置");
        using var probe = Process.Start(new ProcessStartInfo(
            System.IO.Path.Combine(AppContext.BaseDirectory, "BingLan.UiSmokeTests.exe"), "--topbar-reserved-flyout-probe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.ReserveTopEdge;
        state.Height = TopBarState.MaximumHeightDip;
        using var sampler = new WindowsPerformanceSamplingService();
        var target = new SurfaceMonitorSelection(monitor.DeviceName, monitor, null);
        var bar = new TopBarWindow(state, new DesktopStyleState(), new FakeEnvironment(), sampler, screenTarget: target);
        try
        {
            show(bar);
            Assert(WaitUntil(pump, () => bar.GetReservedBottomForMonitor(monitor) is not null), "顶栏未获得实际顶边预留");
            var barHandle = new WindowInteropHelper(bar).Handle;
            Assert(GetWindowRect(barHandle, out var barRect), "无法读取顶栏坐标");
            Assert(WaitUntil(pump, () => { probe.Refresh(); return probe.MainWindowHandle != 0; }), "测试应用未创建窗口");
            var window = probe.MainWindowHandle;

            Assert(NativeMethods.SetWindowPos(window, 0,
                monitor.Bounds.Left + monitor.Bounds.Width / 2 - 320,
                monitor.Bounds.Top + monitor.Bounds.Height / 2 - 240,
                640, 480, NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate), "无法放置测试应用");
            const int maximize = 3;
            Assert(DockNativeMethods.ShowWindowAsync(window, maximize), "无法最大化测试应用");
            Assert(WaitUntil(pump, () => DockNativeMethods.IsZoomed(window)
                && DockNativeMethods.MonitorFromWindow(window, DockNativeMethods.MonitorDefaultToNearest) == monitor.MonitorHandle),
                "测试应用未在顶栏所在屏幕最大化");
            ForceForeground(window);
            if (!WaitUntil(pump, () => GetForegroundWindow() == window, attempts: 25))
            {
                Assert(GetWindowRect(window, out var appRect), "无法读取最大化测试应用坐标");
                Click(window, appRect.Left + (appRect.Right - appRect.Left) * 3 / 4, (appRect.Top + appRect.Bottom) / 2);
                Assert(WaitUntil(pump, () => GetForegroundWindow() == window), "测试应用未获得前台");
            }
            CheckFlyouts("正常最大化");
            CheckSmartHide();

            Assert(DockNativeMethods.ShowWindowAsync(window, DockNativeMethods.SwRestore), "无法还原测试应用");
            Assert(WaitUntil(pump, () => !DockNativeMethods.IsZoomed(window)), "测试应用未还原");
            const int overlapPixels = 12;
            Assert(NativeMethods.SetWindowPos(window, 0, monitor.Bounds.Left, barRect.Bottom - overlapPixels,
                monitor.Bounds.Width, monitor.WorkingArea.Height - 80, NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate),
                "无法将测试应用移到顶栏边缘");
            Assert(WaitUntil(pump, () =>
                DockNativeMethods.DwmGetWindowAttribute(window, DockNativeMethods.DwmwaExtendedFrameBounds,
                    out DockNativeMethods.NativeRect frame, Marshal.SizeOf<DockNativeMethods.NativeRect>()) == 0
                    && frame.Top < barRect.Bottom - TopBarReserveRules.SeamTolerancePixels), "测试应用未压进顶栏");
            CheckFlyouts("窗口边缘重叠");

            ClickModule("时间日期模块");
            Assert(WaitUntil(pump, () => bar.ClockFlyout?.IsOpen == true), "切入真全屏前日历应打开");
            Assert(DockNativeMethods.PostMessageW(window, MakeProbeBorderless, 0, 0), "无法切换测试窗口的边框");
            Assert(WaitUntil(pump, () => ((long)GetWindowLongPtr(window, NativeMethods.GwlStyle) & NativeMethods.WsCaption) == 0),
                "测试窗口仍有标题栏");
            Assert(NativeMethods.SetWindowPos(window, 0, monitor.Bounds.Left, monitor.Bounds.Top,
                monitor.Bounds.Width, monitor.Bounds.Height, NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate),
                "无法令测试应用覆盖整个屏幕");
            Assert(WaitUntil(pump, () => FullScreenWatcher.IsFullScreen(window, excludeMaximized: true)
                && bar.ClockFlyout?.IsOpen != true), "真全屏应收起日历浮层");
            Assert(GetForegroundWindow() == window, "切入真全屏时前台应用应保持不变");
            Console.WriteLine($"通过：{monitor.Label}，真全屏仍会收起日历");

            void CheckSmartHide()
            {
                MoveMouseTo(monitor.Bounds.Left + monitor.Bounds.Width / 2, monitor.Bounds.Top + monitor.Bounds.Height / 2);
                var smartState = BarState();
                smartState.VisibilityMode = TopBarVisibilityMode.SmartHide;
                var smart = new TopBarWindow(smartState, new DesktopStyleState(), new FakeEnvironment(), sampler,
                    screenTarget: target, ownsGlobalEvents: false);
                try
                {
                    var handle = ShowBarAndGetHandle(smart, pump);
                    Assert(GetWindowRect(handle, out var smartRect) && WaitUntil(pump, () => ReadAppFrame().Top >= smartRect.Bottom),
                        "顶部预留应让最大化窗口完全位于较矮的信息条下方");
                    Hold(TimeSpan.FromMilliseconds(850));
                    Assert(smart.IsVisible, "未覆盖顶边的最大化窗口应保留智能隐藏信息条");
                }
                finally
                {
                    smart.Close();
                    pump();
                }

                state.VisibilityMode = TopBarVisibilityMode.SmartHide;
                try
                {
                    bar.ApplyState();
                    Assert(WaitUntil(pump, () => ReadAppFrame().Top < barRect.Bottom && !bar.IsVisible)
                        && DockNativeMethods.IsWindow(barHandle), "最大化窗口覆盖顶边时，信息条应收起并保留窗口");
                    MoveMouseTo(monitor.Bounds.Right - 8, barRect.Top);
                    Hold(TimeSpan.FromMilliseconds(850));
                    Assert(!bar.IsVisible, "从窗口右上角标题按钮区域接近时不应唤出信息条");

                    RevealSmartBar(bar, barHandle, pump);
                    var clock = ButtonByName(bar, "时间日期模块");
                    var clockPoint = clock.PointToScreen(new Point(clock.ActualWidth / 2, clock.ActualHeight / 2));
                    Assert(clockPoint.X >= monitor.Bounds.Right - TopBarRevealRules.CaptionButtonsExclusionDip * monitor.Dpi / 96d,
                        "测试的时间日期按钮应处于窗口右上角标题按钮范围");
                    MoveMouseTo((int)clockPoint.X, (int)clockPoint.Y);
                    Hold(TimeSpan.FromMilliseconds(850));
                    Assert(bar.IsVisible, "唤出信息条后移动到右端时，信息条应保持显示");
                    ClickModule("时间日期模块");
                    Assert(WaitUntil(pump, () => bar.ClockFlyout is
                        { IsOpen: true, Child: FrameworkElement { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } }),
                        "智能隐藏模式下应能打开日历");
                    var calendar = (FrameworkElement)bar.ClockFlyout!.Child;
                    var calendarPoint = calendar.PointToScreen(new Point(calendar.ActualWidth / 2, calendar.ActualHeight / 2));
                    MoveMouseTo((int)calendarPoint.X, (int)calendarPoint.Y);
                    Hold(TimeSpan.FromMilliseconds(850));
                    Assert(bar.IsVisible && bar.ClockFlyout?.IsOpen == true, "移入日历后浮层和信息条应保持显示");
                    ClickModule("时间日期模块");
                    Assert(WaitUntil(pump, () => bar.ClockFlyout?.IsOpen != true), "再次点击应关闭日历浮层");
                    Console.WriteLine($"通过：{monitor.Label}，智能隐藏可从边缘唤出并操作右端模块和日历");
                }
                finally
                {
                    state.VisibilityMode = TopBarVisibilityMode.ReserveTopEdge;
                    bar.ApplyState();
                }
                Assert(WaitUntil(pump, () => bar.GetReservedBottomForMonitor(monitor) is not null && bar.IsVisible),
                    "应恢复顶边预留以继续测试");

                DockNativeMethods.NativeRect ReadAppFrame()
                {
                    Assert(DockNativeMethods.DwmGetWindowAttribute(window, DockNativeMethods.DwmwaExtendedFrameBounds,
                        out DockNativeMethods.NativeRect frame, Marshal.SizeOf<DockNativeMethods.NativeRect>()) == 0,
                        "无法读取测试应用的可见边界");
                    return frame;
                }
            }

            void ClickModule(string name)
            {
                Assert(GetForegroundWindow() == window, "点击前测试应用必须在前台");
                var button = ButtonByName(bar, name);
                // This part of the button clears the shorter bar and the window edge in the overlap scenario.
                var point = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight * 0.72));
                Click(barHandle, (int)point.X, (int)point.Y);
            }

            void CheckFlyouts(string scenario)
            {
                Hold(TimeSpan.FromMilliseconds(350));
                Check("时间日期模块", () => bar.ClockFlyout?.IsOpen == true);
                Check("音量模块", () => bar.VolumeFlyout?.IsOpen == true);
                Console.WriteLine($"通过：{monitor.Label}，{scenario}，日历和音量均可保持打开");

                void Check(string name, Func<bool> isOpen)
                {
                    ClickModule(name);
                    Hold(TimeSpan.FromMilliseconds(850));
                    Assert(isOpen(), $"{scenario}时 {name} 应保持打开");
                    ClickModule(name);
                    Assert(WaitUntil(pump, () => !isOpen()), $"再次点击 {name} 应关闭浮层");
                }
            }

            void Click(nint window, int x, int y)
            {
                MoveMouseTo(x, y);
                var point = new DockNativeMethods.NativePoint { X = x, Y = y };
                Assert(WaitUntil(pump, () => WindowFromPoint(point) == window),
                    $"点击位置 ({x},{y}) 被其他窗口遮挡，命中 {WindowFromPoint(point):X}，预期 {window:X}");
                SendMouseInputs(new MouseInput { Flags = MouseLeftDown }, new MouseInput { Flags = MouseLeftUp });
            }

            void Hold(TimeSpan duration)
            {
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < duration)
                {
                    pump();
                    Thread.Sleep(10);
                }
            }
        }
        finally
        {
            bar.Close();
            if (!probe.HasExited && (!probe.CloseMainWindow() || !probe.WaitForExit(2000)))
            {
                probe.Kill();
                probe.WaitForExit(2000);
            }
            SetCursorPos(previousCursor.X, previousCursor.Y);
            if (previousForeground != 0 && DockNativeMethods.IsWindow(previousForeground))
            {
                ForceForeground(previousForeground);
            }
            pump();
        }
    }

    private static nint ShowBarAndGetHandle(Window bar, Action pump)
    {
        bar.Show();
        pump();
        var handle = new WindowInteropHelper(bar).Handle;
        Assert(handle != 0 && DockNativeMethods.IsWindow(handle), "顶栏未创建原生窗口");
        return handle;
    }

    private static void RevealSmartBar(Window bar, nint handle, Action pump)
    {
        Assert(GetWindowRect(handle, out var bounds) && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top,
            "无法读取已创建顶栏的屏幕位置");
        var monitorHandle = DockNativeMethods.MonitorFromWindow(handle, DockNativeMethods.MonitorDefaultToNearest);
        var screen = MonitorCatalog.GetAll().Single(monitor => monitor.MonitorHandle == monitorHandle).Bounds;
        var isTopBar = bounds.Top + (bounds.Bottom - bounds.Top) / 2 < screen.Top + screen.Height / 2;
        var x = bounds.Left + (bounds.Right - bounds.Left) / 2;
        var y = isTopBar ? bounds.Top : bounds.Bottom - 2;
        MoveMouseTo(x, y);
        if (!WaitUntil(pump, () => bar.IsVisible))
        {
            DockNativeMethods.GetCursorPos(out var cursor);
            Assert(false, $"信息条在唤醒区停留后仍未显示，目标指针 ({x},{y})，实际 ({cursor.X},{cursor.Y})，"
                + $"窗口边界 ({bounds.Left},{bounds.Top})-({bounds.Right},{bounds.Bottom})");
        }
    }

    internal static void Run(Action pump)
    {
        Assert(DockNativeMethods.GetCursorPos(out var previousCursor), "无法读取测试前的鼠标位置");
        var movedPointer = false;
        try
        {
            ShowModulesAndStyles(ShowForInteraction, pump);
            ModuleSwitchesRemoveButtons(ShowForInteraction, pump);
            ClicksOpenSettings(ShowForInteraction, pump);
            BottomBarAvoidsDockAndOpensCalendarInward(ShowForInteraction, pump);
            PowerDialogAvailableWithoutBattery(ShowForInteraction, pump);
            SurfacePaintsGlass(ShowForInteraction, pump);
            ReservedModeClearsSmartHideAppearance();
            ForegroundTickClosesBarOnLostMonitor(pump);
            TodoFlyoutShowsItemsAndCloses(ShowForInteraction, pump);
            CapsuleExpandsWithinItsSurface(ShowForInteraction, pump);
            WeatherHoverOpensTooltip(ShowForInteraction, pump);
            SearchEntryButtonOpensLauncher(ShowForInteraction, pump);
            SearchAndModuleFlyoutsAreMutuallyExclusive(ShowForInteraction, pump);
            SearchAndModuleFlyoutsUseSystemColorsInHighContrast(ShowForInteraction, pump);
            SearchModuleSwitchHidesEntry(ShowForInteraction, pump);
            RateDigitsDoNotShiftSearchEntry(ShowForInteraction, pump);
            SearchEntrySitsAtBarCentre(ShowForInteraction, pump);
            CustomModuleOrderUpdatesLayout(ShowForInteraction, pump);
            PeripheralBatteryReadoutAndRefresh(ShowForInteraction, pump);
            PeripheralBatterySettingsPath(pump);
            EverythingProtocolAndFallback(pump);
            WindowsIndexQueryRules();
        }
        finally
        {
            if (movedPointer)
            {
                SetCursorPos(previousCursor.X, previousCursor.Y);
            }
        }

        void ShowForInteraction(Window bar)
        {
            try
            {
                var handle = ShowBarAndGetHandle(bar, pump);
                if (!bar.IsVisible)
                {
                    movedPointer = true;
                    RevealSmartBar(bar, handle, pump);
                }
            }
            catch
            {
                bar.Close();
                pump();
                throw;
            }
        }
    }

    private static void BottomBarAvoidsDockAndOpensCalendarInward(Action<Window> show, Action pump)
    {
        var previousForeground = GetForegroundWindow();
        var foreground = new Window
        {
            Width = 320,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false
        };
        try
        {
            foreground.Show();
            pump();
            var foregroundHandle = new WindowInteropHelper(foreground).Handle;
            ForceForeground(foregroundHandle);
            Assert(WaitUntil(pump, () => GetForegroundWindow() == foregroundHandle), "底部信息条测试窗口应取得前台");
            var monitorHandle = DockNativeMethods.MonitorFromWindow(foregroundHandle, DockNativeMethods.MonitorDefaultToNearest);
            var screen = MonitorCatalog.GetAll().Single(monitor => monitor.MonitorHandle == monitorHandle).Bounds;
            Assert(GetWindowRect(foregroundHandle, out var frame)
                && !DockVacateRules.CoversMonitor(new PixelRect(frame.Left, frame.Top, frame.Right, frame.Bottom), screen),
                "底部信息条测试需要非全屏前台窗口");

            var state = BarState();
            state.ShowAtBottom = true;
            state.VisibilityMode = TopBarVisibilityMode.SmartHide;
            using var sampler = new WindowsPerformanceSamplingService();
            int? dockTop = null;
            var bar = new TopBarWindow(state, new DesktopStyleState(), new FakeEnvironment(), sampler)
            {
                ResolveBottomDockTop = monitor => dockTop =
                    TaskbarEdgeGuard.AvoidNativeTaskbar(monitor.Bounds, monitor.Bounds).Bottom - 100
            };
            show(bar);
            try
            {
                Assert(GetForegroundWindow() == foregroundHandle, "底部信息条显示时普通测试窗口应保持前台");
                Assert(GetWindowRect(new WindowInteropHelper(bar).Handle, out var rect) && rect.Bottom == dockTop,
                    "底部信息条应贴齐同屏底部 Dock 上沿");
                Invoke(ButtonByName(bar, "时间日期模块"));
                pump();
                Assert(bar.ClockFlyout is { IsOpen: true, Placement: PlacementMode.Top },
                    "底部信息条从条上方向工作区展开日历");
            }
            finally
            {
                bar.Close();
                pump();
            }
        }
        finally
        {
            foreground.Close();
            if (previousForeground != 0 && DockNativeMethods.IsWindow(previousForeground))
            {
                ForceForeground(previousForeground);
            }
            pump();
        }
    }

    private static void PeripheralBatteryReadoutAndRefresh(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        state.Modules.PeripheralBattery = true;
        state.ShowAtBottom = true;
        state.Modules.Performance = false;
        state.BatteryStyle = TopBarBatteryStyle.IconOnly;
        var environment = new FakeEnvironment
        {
            PeripheralSnapshot = new(PeripheralBatteryStatus.Fresh,
                [new("mouse", "鼠标", PeripheralDeviceKind.Mouse, "Bluetooth", -1, false, false, true)],
                DateTimeOffset.Now)
        };
        using var sampler = new WindowsPerformanceSamplingService();
        var bar = new TopBarWindow(state, new DesktopStyleState(), environment, sampler);
        var foreground = new Window
        {
            Width = 320,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false
        };
        try
        {
            show(foreground);
            ForceForeground(new WindowInteropHelper(foreground).Handle);
            show(bar);
            var deadline = Stopwatch.StartNew();
            while (environment.PeripheralAutomaticRefreshes == 0 && deadline.Elapsed < TimeSpan.FromSeconds(7))
            {
                Thread.Sleep(20);
                pump();
            }
            Assert(!sampler.IsRunning && environment.PeripheralAutomaticRefreshes > 0,
                "关闭性能采样时，可见顶栏仍应自动刷新外设电量");

            var button = ButtonByName(bar, "外设电量模块");
            Assert(DescendantElements(button).OfType<TextBlock>().Any(text => text.Text == "—"),
                "未知外设电量应显示 —，本机电量的仅图标选项不影响外设百分比");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            pump();
            var flyout = bar.PeripheralBatteryFlyout;
            Assert(flyout is { IsOpen: true, Placement: PlacementMode.Top }
                && DescendantTextBlocks(flyout.Child).Any(text => text.Text.Contains("已连接")),
                "底部外设浮层应向上打开并显示连接状态");
            var refresh = AllButtons(flyout!.Child).Single(item => AutomationProperties.GetName(item) == "立即刷新外设电量");
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(environment.PeripheralRefreshes == 1 && bar.PeripheralBatteryFlyout == flyout && flyout.IsOpen,
                "手动刷新应调用宿主并保持当前浮层打开");

            environment.PeripheralSnapshot = new(PeripheralBatteryStatus.Fresh,
                [new("mouse", "鼠标", PeripheralDeviceKind.Mouse, "Bluetooth", 42, false, true, true)],
                DateTimeOffset.Now);
            bar.RenderPeripheralModule();
            Assert(flyout.IsOpen && DescendantTextBlocks(flyout.Child).Any(text => text.Text == "42%")
                && DescendantTextBlocks(flyout.Child).Any(text => text.Text.Contains("休眠")),
                "后台刷新完成后，打开的浮层应显示新的电量与连接状态");
            Assert(DescendantElements(button).OfType<TextBlock>().Any(text => text.Text == "42%"),
                "刷新后的电量应同步到顶栏");

            environment.PeripheralSnapshot = PeripheralBatterySnapshot.NotConfigured;
            bar.RefreshTodoSummary();
            Assert(DescendantTextBlocks(flyout.Child).Any(text => text.Text.Contains("选择 BatteryHub.exe")),
                "清除程序配置后，打开的浮层应显示配置入口说明");
        }
        finally
        {
            bar.Close();
            foreground.Close();
            pump();
        }
    }

    internal static void PeripheralBatteryLocalPath()
    {
        var directory = Directory.CreateTempSubdirectory("BingLan-BatteryHub-");
        try
        {
            var localFile = Path.Combine(directory.FullName, "BatteryHub.exe");
            Assert(BatteryHubCli.ResolveLocalExecutable(localFile) is null, "缺失的程序应当不可用");
            Assert(BatteryHubCli.ResolveLocalExecutable("BatteryHub.exe") is null, "不得执行相对路径");
            Assert(BatteryHubCli.ResolveLocalExecutable(@"\\server\share\BatteryHub.exe") is null, "不得执行网络共享路径");
            Assert(BatteryHubCli.ResolveLocalExecutable(@"\\?\C:\Tools\BatteryHub.exe") is null, "不得接受设备路径");
            File.Copy(Environment.ProcessPath!, localFile);
            Assert(string.Equals(BatteryHubCli.ResolveLocalExecutable(localFile), localFile, StringComparison.OrdinalIgnoreCase),
                "允许本机存在的 BatteryHub.exe");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void PeripheralBatterySettingsPath(Action pump)
    {
        var state = new TopBarState { BatteryHubExecutablePath = @"C:\Tools\BatteryHub.exe" };
        state.Modules.PeripheralBattery = true;
        var applies = 0;
        var settings = new SettingsWindow(new CityLookupService(), new InformationWidgetState(),
            DesktopExperienceRules.CreateDefault(), (_, _) => { }, _ => { }, _ => { },
            topBarState: state, applyTopBar: () => applies++);
        try
        {
            pump();
            applies = 0;
            settings.SelectTopBarModule(TopBarModuleKind.PeripheralBattery);
            Assert(((FrameworkElement)settings.FindName("TopBarPeripheralBatteryPanel")).Visibility == Visibility.Visible
                && ((FrameworkElement)settings.FindName("TopBarSelectedStyleRow")).Visibility == Visibility.Collapsed,
                "外设电量设置应显示程序路径，不显示刷新间隔选项");
            Assert(((TextBlock)settings.FindName("TopBarBatteryHubPathText")).Text == state.BatteryHubExecutablePath,
                "设置页应显示本机配置的 BatteryHub 程序路径");
            ((Button)settings.FindName("TopBarBatteryHubClearButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            pump();
            Assert(state.BatteryHubExecutablePath is null && applies == 1
                && !((Button)settings.FindName("TopBarBatteryHubClearButton")).IsEnabled,
                "清除路径应通过顶栏变更回调保存，并更新按钮状态");
        }
        finally
        {
            settings.Close();
        }
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

    private static void PowerDialogAvailableWithoutBattery(Action<Window> show, Action pump)
    {
        var pending = new TaskCompletionSource<TopBarBatterySnapshot>();
        var unavailable = new TopBarBatterySnapshot(
            -1, TopBarBatteryStatus.Unknown, false, null, false, TopBarPowerMode.Unknown, false, 0);
        (string State, Task<TopBarBatterySnapshot> Read)[] cases =
        [
            ("无电池", Task.FromResult(unavailable)),
            ("状态加载中", pending.Task),
            ("状态读取失败", Task.FromException<TopBarBatterySnapshot>(new InvalidOperationException("测试读取失败")))
        ];
        foreach (var (stateName, read) in cases)
        {
            var state = BarState();
            state.VisibilityMode = TopBarVisibilityMode.SmartHide;
            using var sampler = new WindowsPerformanceSamplingService();
            var environment = new FakeEnvironment { BatteryReadTask = read };
            var bar = new TopBarWindow(state, new DesktopStyleState(), environment, sampler);
            show(bar);
            try
            {
                Invoke(ButtonByName(bar, "电量模块"));
                pump();
                var action = AllButtons(bar.BatteryFlyout!.Child)
                    .Single(candidate => AutomationProperties.GetName(candidate) == "打开 Windows 关机或重启对话框");
                Assert(action.IsEnabled, $"{stateName}时应保留关机或重启入口");
                Invoke(action);
                pump();
                Assert(bar.BatteryFlyout is null && environment.ShutdownDialogRequested == 1
                    && environment.QuickSettingsRequested == 0 && environment.OpenedSettingsUris.Count == 0,
                    $"{stateName}时只应请求系统选择对话框并收起浮层");
                if (read == pending.Task)
                {
                    pending.SetResult(unavailable);
                    pump();
                    Assert(bar.BatteryFlyout is null, "读取状态迟到时不应重新打开电源浮层");
                }
            }
            finally
            {
                bar.Close();
                pump();
            }
        }
    }

    private static void SurfacePaintsGlass(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        state.FollowCardLook = false;
        state.FrostedGlass = false;
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
            Assert(!bar.IsAcrylicSurface, "关闭磨砂时应使用普通透色底板");
            Assert(bar.UsesDarkInk, "浅色不透明自定义顶栏应自动切换为深色文字与图标");

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

            state.FrostedGlass = true;
            bar.RefreshAppearance();
            var accentTint = typeof(TopBarWindow).GetField("_appliedAcrylicTint", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert(bar.IsAcrylicSurface && bar.UsesDarkInk && accentTint.GetValue(bar) is 0xD6F0CFD8u,
                "开启浅紫色磨砂应将对应颜色和不透明度写入亚克力底板，保留深色文字");

            state.SurfaceColor = "#213A57";
            state.SurfaceOpacity = 0.8d;
            bar.RefreshAppearance();
            Assert(bar.IsAcrylicSurface && !bar.UsesDarkInk && accentTint.GetValue(bar) is 0xCC573A21u,
                "磨砂开启时改为深色及不同不透明度应立即刷新底板，切换为浅色文字");
        }
        finally
        {
            bar.Close();
            pump();
        }
    }

    private static void ForegroundTickClosesBarOnLostMonitor(Action pump)
    {
        var monitor = MonitorCatalog.GetAll().First();
        var target = new SurfaceMonitorSelection(monitor.DeviceName, monitor, null);
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        using var sampler = new WindowsPerformanceSamplingService();
        var bar = new TopBarWindow(state, new DesktopStyleState(), new FakeEnvironment(), sampler,
            screenTarget: target, ownsGlobalEvents: false);
        var closed = false;
        bar.Closed += (_, _) => closed = true;
        try
        {
            ShowBarAndGetHandle(bar, pump);
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var live = TaskbarEdgeGuard.GetNativeTaskbarLayout(monitor.Bounds);
            var changed = new TaskbarEdgeGuard.NativeTaskbarLayout(DockEdge.Bottom, monitor.Bounds, !(live?.AutoHidden ?? false));
            typeof(TopBarWindow).GetField("_nativeTaskbar", fields)!.SetValue(bar, changed);
            typeof(TopBarWindow).GetField("_screenTarget", fields)!.SetValue(bar,
                target with { Monitor = monitor with { MonitorHandle = 0 } });
            typeof(TopBarWindow).GetMethod("UpdateForForeground", fields)!.Invoke(bar, null);
            Assert(closed, "任务栏布局刷新时，已断开屏幕上的顶栏应能安全关闭");
        }
        finally
        {
            if (!closed) bar.Close();
        }
    }

    private static void ReservedModeClearsSmartHideAppearance()
    {
        var monitor = MonitorCatalog.GetAll().FirstOrDefault()
            ?? throw new InvalidOperationException("顶栏外观回归需要一个显示器");
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        state.FollowCardLook = false;
        state.SurfaceColor = "#FFFFFF";
        state.SurfaceOpacity = 0d;
        state.FrostedGlass = true;
        using var sampler = new WindowsPerformanceSamplingService();
        // Keep the window unshown so ApplyState can exercise the reservation decision
        // without registering a real AppBar or changing the desktop work area.
        var style = new DesktopStyleState { Glass = GlassMode.Clear };
        var bar = new TopBarWindow(state, style,
            new FakeEnvironment(), sampler,
            screenTarget: new SurfaceMonitorSelection(monitor.DeviceName, monitor, null),
            ownsGlobalEvents: false);
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var overlap = typeof(TopBarWindow).GetField("_smartHideOverlappingWindow", fields)!;
        var dwell = typeof(TopBarWindow).GetField("_revealZoneEnteredTick", fields)!;
        var sample = typeof(TopBarWindow).GetField("_sampledBackdrop", fields)!;
        var previousInk = typeof(TopBarWindow).GetField("_lastAdaptiveDarkInk", fields)!;
        try
        {
            bar.ApplyState();
            var configuredBackground = (bar.Surface.Background as SolidColorBrush)?.Color
                ?? throw new InvalidOperationException("顶栏底板应使用纯色画刷");
            // A 0% custom surface keeps one alpha step so its hit area still works.
            Assert(configuredBackground == Color.FromArgb(1, 255, 255, 255),
                $"白色 0% 自定义顶栏应接近透明，实际为 {configuredBackground}");
            overlap.SetValue(bar, true);
            dwell.SetValue(bar, Environment.TickCount64);
            sample.SetValue(bar, new ColorContrastRules.SrgbColor(1d, 1d, 1d));
            previousInk.SetValue(bar, true);
            bar.RefreshAppearance();
            Assert(bar.Surface.Background is SolidColorBrush overlapBrush && overlapBrush.Color == configuredBackground
                && !bar.IsAcrylicSurface && bar.UsesDarkInk && bar.ModuleHeader.Effect is not null,
                "智能隐藏覆盖浅色应用时，白色 0% 顶栏应保留清透并使用带阴影的深色文字");

            state.SurfaceColor = "#214365";
            state.SurfaceOpacity = 0.32d;
            bar.RefreshAppearance();
            Assert(bar.Surface.Background is SolidColorBrush customBrush && customBrush.Color == Color.FromArgb(82, 33, 67, 101),
                "智能隐藏覆盖窗口时应保留可见的自定义底色和不透明度");

            state.FollowCardLook = true;
            bar.RefreshAppearance();
            Assert(bar.Surface.Background is SolidColorBrush sharedBrush && sharedBrush.Color == Color.FromArgb(1, 255, 255, 255),
                "跟随全局无底板外观时，智能隐藏覆盖窗口也应保留清透");

            style.Glass = GlassMode.Dark;
            style.GlassDensity = DesktopStyleRules.MinimumGlassDensity;
            bar.RefreshAppearance();
            Assert(bar.Surface.Background is SolidColorBrush sharedDarkBrush && sharedDarkBrush.Color == Color.FromArgb(139, 24, 34, 47),
                "智能隐藏覆盖窗口时应保留全局深色材质的底色和不透明度");

            style.Glass = GlassMode.Clear;
            bar.RefreshAppearance();

            var canReserveTop = TopBarReserveRules.ShouldReserve(true, false, monitor.Bounds,
                DockAppBarController.GetLiveWorkingArea(monitor),
                TaskbarEdgeGuard.GetNativeTaskbarLayout(monitor.Bounds)?.Edge == DockEdge.Top);
            state.VisibilityMode = TopBarVisibilityMode.ReserveTopEdge;
            bar.ApplyState();
            if (!canReserveTop)
            {
                Assert(overlap.GetValue(bar) is true && dwell.GetValue(bar) is not null,
                    "顶边被占用时应继续使用智能隐藏");
                state.ShowAtBottom = true;
                bar.ApplyState();
            }
            Assert(bar.Surface.Background is SolidColorBrush restoredBrush && restoredBrush.Color == configuredBackground,
                "切换预留模式后应保持所选透明外观");
            Assert(overlap.GetValue(bar) is false && dwell.GetValue(bar) is null
                && sample.GetValue(bar) is null && !bar.UsesDarkInk,
                "切换预留模式后应清除覆盖状态、唤出停留时间和旧背景采样");
        }
        finally
        {
            bar.Close();
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
            if (imeName is null || imeName.Foreground is not SolidColorBrush imeInk || imeInk.Color != Colors.White)
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
            var expectedLunarToday = LunarDayText.FullDate(today);
            Assert(!string.IsNullOrEmpty(expectedLunarToday)
                    && calendarTexts.Any(text => text.Contains(expectedLunarToday)),
                "日历信息栏标题应显示今日完整农历");
            var expectedLunarCell = LunarDayText.For(today);
            Assert(!string.IsNullOrEmpty(expectedLunarCell)
                    && calendarTexts.Contains(expectedLunarCell),
                "日历信息栏日期格应显示农历标注");
            Assert(calendarTexts.Contains("一") && calendarTexts.Contains("日"),
                "日历信息栏应显示星期表头");
            var imeModule = ButtonByName(bar, "输入法模块");
            Invoke(imeModule);
            pump();
            Assert(bar.ClockFlyout is null && bar.InputMethodFlyout?.IsOpen == true,
                "打开输入法浮层时应互斥关闭已打开的日历浮层");
            Invoke(clock);
            pump();
            Assert(bar.InputMethodFlyout is null && bar.ClockFlyout?.IsOpen == true,
                "打开日历浮层时应互斥关闭已打开的输入法浮层");
            Invoke(clock);
            pump();
            Assert(bar.ClockFlyout is null, "再次点击时钟模块应收起日历信息栏");
            Invoke(ButtonByName(bar, "音量模块"));
            pump();
            Assert(bar.VolumeFlyout?.IsOpen == true && environment.QuickSettingsRequested == 0,
                "点击音量模块应弹出冰蓝自有声音控制浮层，不唤起原生任务栏快速设置");
            var muteBtn = AllButtons(bar.VolumeFlyout!.Child)
                .Single(candidate => AutomationProperties.GetName(candidate) == "静音");
            Invoke(muteBtn);
            pump();
            Assert(environment.MasterMutedCalls.Count == 1 && environment.MasterMutedCalls[0],
                "点击声音浮层静音按钮应联动系统静音");
            var switchDeviceBtn = AllButtons(bar.VolumeFlyout!.Child)
                .Single(candidate => AutomationProperties.GetName(candidate) == "切换输出设备 蓝牙耳机");
            Invoke(switchDeviceBtn);
            pump();
            Assert(environment.SwitchedAudioDevices.SequenceEqual(["dev-bt"]),
                "点击声音浮层输出设备应切换默认播放设备");
            var soundSettingsBtn = AllButtons(bar.VolumeFlyout!.Child)
                .Single(candidate => AutomationProperties.GetName(candidate) == "打开 Windows 声音设置");
            Invoke(soundSettingsBtn);
            pump();
            Assert(bar.VolumeFlyout is null && environment.OpenedSettingsUris.Contains("ms-settings:sound"),
                "点击声音浮层底部设置项应收起浮层并打开系统声音设置");

            Invoke(ButtonByName(bar, "电量模块"));
            pump();
            Assert(bar.BatteryFlyout?.IsOpen == true && bar.VolumeFlyout is null,
                "点击电量模块应弹出冰蓝自有电源与电池浮层");
            var perfModeBtn = AllButtons(bar.BatteryFlyout!.Child)
                .Single(candidate => AutomationProperties.GetName(candidate) == "电源模式 最佳性能");
            Invoke(perfModeBtn);
            pump();
            Assert(environment.PowerModeChanges.SequenceEqual([TopBarPowerMode.BestPerformance]),
                "点击电池浮层电源模式应联动系统切换");
            var powerSettingsBtn = AllButtons(bar.BatteryFlyout!.Child)
                .Single(candidate => AutomationProperties.GetName(candidate) == "打开 Windows 电源与电池设置");
            Invoke(powerSettingsBtn);
            pump();
            Assert(bar.BatteryFlyout is null && environment.OpenedSettingsUris.Contains("ms-settings:powersleep"),
                "点击电池浮层底部设置项应收起浮层并打开系统电源设置");

            Invoke(ButtonByName(bar, "电量模块"));
            pump();
            Invoke(AllButtons(bar.BatteryFlyout!.Child)
                .Single(candidate => AutomationProperties.GetName(candidate) == "打开 Windows 关机或重启对话框"));
            pump();
            Assert(bar.BatteryFlyout is null && environment.ShutdownDialogRequested == 1
                && environment.OpenedSettingsUris.Count == 2 && environment.QuickSettingsRequested == 0,
                "关机或重启入口应收起浮层并只请求 Windows 选择对话框");

            Invoke(ButtonByName(bar, "网络模块"));
            pump();
            Assert(bar.NetworkFlyout?.IsOpen == true && bar.BatteryFlyout is null,
                "点击网络模块应弹出冰蓝自有网络与 WLAN 浮层");
            var connectSavedBtn = AllButtons(bar.NetworkFlyout!.Child)
                .Single(candidate => AutomationProperties.GetName(candidate) == "连接到已保存 WLAN Office5G");
            Invoke(connectSavedBtn);
            pump();
            Assert(environment.ConnectedWifiProfiles.SequenceEqual(["Office5G"]),
                "点击已保存 WLAN 应直接触发系统配置连接");
            var wifiSettingsBtn = AllButtons(bar.NetworkFlyout!.Child)
                .Single(candidate => AutomationProperties.GetName(candidate) == "打开 Windows 网络和 Wi-Fi 设置");
            Invoke(wifiSettingsBtn);
            pump();
            Assert(bar.NetworkFlyout is null && environment.OpenedSettingsUris.Contains("ms-settings:network-wifi")
                && environment.QuickSettingsRequested == 0,
                "点击网络浮层底部设置项应收起浮层并直接打开 Wi-Fi 设置页而不弹出原生任务栏");
            var imeButton = ButtonByName(bar, "输入法模块");
            Invoke(imeButton);
            pump();
            var imeFlyout = bar.InputMethodFlyout;
            Assert(imeFlyout?.IsOpen == true, "点击输入法模块应弹出输入法切换浮层");
            var wechatButton = AllButtons(imeFlyout!.Child)
                .Single(candidate => AutomationProperties.GetName(candidate) == "微信输入法");
            Invoke(wechatButton);
            pump();
            Assert(environment.ActivatedInputMethods.Count == 1
                && environment.ActivatedInputMethods[0].DisplayName == "微信输入法",
                "点击浮层中的输入法条目应触发切换");
            Assert(bar.InputMethodFlyout is null, "切换输入法后浮层应收起");

            Invoke(imeButton);
            pump();
            var imeSettingsButton = AllButtons(bar.InputMethodFlyout!.Child)
                .Single(candidate => AutomationProperties.GetName(candidate) == "打开 Windows 键盘与语言设置");
            Invoke(imeSettingsButton);
            pump();
            Assert(environment.InputMethodRequested == 1, "点击浮层底部设置项应打开系统键盘与语言设置");
            Assert(bar.InputMethodFlyout is null, "打开系统键盘设置后浮层应收起");
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                var tray = ButtonByName(bar, "托盘模块");
                Assert(tray.ActualWidth >= 32 && tray.ActualHeight >= 32, "托盘入口应有可点击的热区");
                Invoke(tray);
                pump();
                if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64)
                {
                    var trayPanel = Application.Current.Windows.OfType<BingLan.App.Tray.TrayPanelWindow>()
                        .Single(window => window.Owner == bar && window.IsVisible);
                    Assert(trayPanel.ShowActivated, "键盘和辅助功能打开的托盘应能接收焦点");
                    trayPanel.Close();

                    Assert(bar.TryOpenTrayForApplication() && bar.TryOpenTrayForApplication(),
                        "驻留应用应打开冰蓝托盘，并保留已打开的面板");
                    trayPanel = Application.Current.Windows.OfType<BingLan.App.Tray.TrayPanelWindow>()
                        .Single(window => window.Owner == bar && window.IsVisible);
                    Assert(trayPanel.ShowActivated && environment.TrayOverflowRequested == 0,
                        "驻留应用的账号选择应支持键盘操作，并保持 Windows 托盘关闭");
                    trayPanel.Close();

                    tray.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    {
                        RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
                    });
                    Invoke(tray);
                    pump();
                    trayPanel = Application.Current.Windows.OfType<BingLan.App.Tray.TrayPanelWindow>()
                        .Single(window => window.Owner == bar && window.IsVisible);
                    Assert(trayPanel.ShowActivated
                        && (NativeMethods.GetExStyleOf(new WindowInteropHelper(trayPanel).Handle) & NativeMethods.WsExNoActivate) == 0,
                        "鼠标打开托盘后应能在面板内使用 Tab 和 Escape");
                    Invoke((Button)trayPanel.FindName("NativeTrayButton"));
                    pump();
                    Assert(!trayPanel.IsVisible, "切换到 Windows 托盘时应关闭冰蓝托盘");
                }
                Assert(environment.TrayOverflowRequested == 1, "Windows 托盘入口应调用原生面板");
                state.Modules.TrayOverflow = false;
                bar.ApplyState();
                pump();
                Assert(!AllButtons(bar).Any(button => AutomationProperties.GetName(button) == "托盘模块"),
                    "关闭托盘模块应移除入口");
                Assert(!bar.TryOpenTrayForApplication() && environment.TrayOverflowRequested == 1,
                    "独立托盘入口关闭时，驻留应用应回退到提示");
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
            Assert(WaitUntil(pump, () => environment.LauncherToggles == 1) && bar.SearchPopup is null,
                "默认状态下点击搜索入口应打开冰蓝总览卡");

            state.SearchInline = true;
            bar.ApplyState();
            pump();
            entry = bar.SearchEntryButton
                ?? throw new InvalidOperationException("开启内联搜索后应保留顶栏入口按钮");
            Invoke(entry);
            Assert(WaitUntil(pump, () => bar.SearchPopup is not null), "开启内联搜索后点击入口应弹出内联搜索窗口");
            bar.TriggerSearch();
            Assert(bar.SearchPopup is null, "再次触发应关闭内联搜索窗口");

            // Turning the switch off has to restore the previous behaviour: the same trigger
            // opens the overview card and no popup appears.
            state.SearchInline = null;
            bar.ApplyState();
            pump();
            bar.TriggerSearch();
            Assert(bar.SearchPopup is null && environment.LauncherToggles == 2,
                "关闭开关后搜索入口应重新打开冰蓝总览卡");
            var style = (long)GetWindowLongPtr(new WindowInteropHelper(bar).Handle, GwlExStyle);
            Assert((style & DockNativeMethods.WsExNoActivate) != 0, "触发总览卡后顶栏应保持永不激活");
        }
        finally
        {
            bar.Close();
            pump();
        }
    }

    private static void SearchAndModuleFlyoutsAreMutuallyExclusive(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        state.SearchInline = true;
        state.FollowCardLook = false;
        state.SurfaceColor = "#00FFFF";
        state.SurfaceOpacity = 0d;
        var expectedSurface = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast
            ? SystemColors.WindowColor
            : Color.FromArgb(1, 0, 255, 255);
        using var sampler = new WindowsPerformanceSamplingService();
        var bar = new TopBarWindow(state, new DesktopStyleState(), new FakeEnvironment(), sampler);
        show(bar);
        try
        {
            (string Name, Func<Popup?> Current)[] modules =
            [
                ("待办模块", () => bar.TodoFlyout),
                ("时间日期模块", () => bar.ClockFlyout),
                ("输入法模块", () => bar.InputMethodFlyout),
                ("音量模块", () => bar.VolumeFlyout),
                ("电量模块", () => bar.BatteryFlyout),
                ("网络模块", () => bar.NetworkFlyout)
            ];
            foreach (var (name, current) in modules)
            {
                Invoke(bar.SearchEntryButton!);
                Assert(WaitUntil(pump, () => bar.SearchPopup?.IsVisible == true),
                    $"应打开内联搜索（存在：{bar.SearchPopup is not null}，可见：{bar.SearchPopup?.IsVisible}）");
                var search = bar.SearchPopup!;
                search.UpdateLayout();
                var searchColor = ((search.Content as Border)?.Background as SolidColorBrush)?.Color;
                Assert(searchColor == expectedSurface,
                    $"内联搜索的底色和不透明度应跟随顶栏（实际：{searchColor}，预期：{expectedSurface}）");
                var input = DescendantElements(search).OfType<TextBox>().Single();
                input.Text = "报告";
                Assert(search.HasUserInput && search.IsVisible, "搜索浮层应保留已输入的查询");

                // Invoke without changing the foreground window or generating an outside click.
                Invoke(ButtonByName(bar, name));
                var flyout = current();
                Assert(flyout is not null && bar.SearchPopup is null && !search.IsVisible,
                    $"打开{name}浮层应立即收起已有的搜索窗口（模块浮层存在：{flyout is not null}，" +
                    $"已打开：{flyout?.IsOpen}，搜索引用已清除：{bar.SearchPopup is null}，原搜索可见：{search.IsVisible}）");
                var openedFlyout = flyout!;
                Assert(WaitUntil(pump, () => openedFlyout.IsOpen), $"{name}浮层应在 WPF 加载后显示");
                var flyoutColor = ((openedFlyout.Child as Border)?.Background as SolidColorBrush)?.Color;
                Assert(flyoutColor == expectedSurface,
                    $"{name}浮层的底色和不透明度应与内联搜索一致（实际：{flyoutColor}，预期：{expectedSurface}）");

                bar.TriggerSearch();
                Assert(!openedFlyout.IsOpen && current() is null && bar.SearchPopup?.IsVisible == true,
                    $"触发搜索应立即收起已有的{name}浮层（模块浮层仍打开：{openedFlyout.IsOpen}，" +
                    $"模块引用已清除：{current() is null}，搜索可见：{bar.SearchPopup?.IsVisible}）");
                var reopenedSearch = bar.SearchPopup!;
                reopenedSearch.UpdateLayout();
                var reopenedInput = DescendantElements(reopenedSearch).OfType<TextBox>().Single();
                var source = PresentationSource.FromVisual(reopenedInput)
                    ?? throw new InvalidOperationException("搜索框没有呈现源");
                reopenedInput.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                });
                Assert(bar.SearchPopup is null && !reopenedSearch.IsVisible,
                    $"按 Esc 应关闭当前搜索浮层（搜索引用已清除：{bar.SearchPopup is null}，原搜索可见：{reopenedSearch.IsVisible}）");
                pump();
            }
        }
        finally
        {
            bar.Close();
            pump();
        }
    }

    private static void SearchAndModuleFlyoutsUseSystemColorsInHighContrast(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        state.SearchInline = true;
        using var sampler = new WindowsPerformanceSamplingService();
        var bar = new TopBarWindow(state, new DesktopStyleState(), new FakeEnvironment(), sampler);
        try
        {
            AccessibilityThemeManager.SetHighContrastForTesting(true);
            show(bar);

            Invoke(bar.SearchEntryButton!);
            Assert(WaitUntil(pump, () => bar.SearchPopup?.IsVisible == true),
                $"应打开高对比度内联搜索（存在：{bar.SearchPopup is not null}，可见：{bar.SearchPopup?.IsVisible}）");
            var search = bar.SearchPopup!;
            search.UpdateLayout();
            var searchCard = search.Content as Border;
            var searchBackground = (searchCard?.Background as SolidColorBrush)?.Color;
            var searchBorder = (searchCard?.BorderBrush as SolidColorBrush)?.Color;
            Assert(searchBackground == SystemColors.WindowColor && searchBorder == SystemColors.WindowTextColor,
                $"高对比度内联搜索应使用系统背景和边框颜色（实际：{searchBackground}、{searchBorder}，" +
                $"预期：{SystemColors.WindowColor}、{SystemColors.WindowTextColor}）");
            var searchInk = (DescendantElements(search).OfType<TextBox>().Single().Foreground as SolidColorBrush)?.Color;
            Assert(searchInk == SystemColors.WindowTextColor,
                $"高对比度内联搜索应使用系统文字颜色（实际：{searchInk}，预期：{SystemColors.WindowTextColor}）");

            Invoke(ButtonByName(bar, "音量模块"));
            var volume = bar.VolumeFlyout;
            Assert(volume is not null && bar.SearchPopup is null && !search.IsVisible,
                $"高对比度音量浮层应打开并收起搜索（音量浮层存在：{volume is not null}，已打开：{volume?.IsOpen}，" +
                $"搜索引用已清除：{bar.SearchPopup is null}，原搜索可见：{search.IsVisible}）");
            Assert(WaitUntil(pump, () => volume!.IsOpen), "高对比度音量浮层应在 WPF 加载后显示");
            var volumeCard = volume!.Child as Border;
            var volumeBackground = (volumeCard?.Background as SolidColorBrush)?.Color;
            var volumeBorder = (volumeCard?.BorderBrush as SolidColorBrush)?.Color;
            Assert(volumeBackground == SystemColors.WindowColor && volumeBorder == SystemColors.WindowTextColor,
                $"高对比度音量浮层应使用系统背景和边框颜色（实际：{volumeBackground}、{volumeBorder}，" +
                $"预期：{SystemColors.WindowColor}、{SystemColors.WindowTextColor}）");
        }
        finally
        {
            bar.Close();
            AccessibilityThemeManager.RestoreSystemModeForTesting();
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
            Assert(bar.SearchPopup is null, "模块关闭时热键触发不应弹出搜索窗口");

            state.Modules.Search = true;
            bar.ApplyState();
            pump();
            Assert(bar.SearchEntryButton is not null, "重新开启模块应重建搜索入口按钮");

            environment.LauncherEnabled = false;
            bar.ApplyState();
            pump();
            Assert(bar.SearchEntryButton is null, "关闭冰蓝总览卡且未开启内联搜索时应隐藏顶栏搜索入口");
            bar.TriggerSearch();
            pump();
            Assert(bar.SearchPopup is null && environment.LauncherToggles == 0,
                "总览卡关闭且未开启内联搜索时不应响应顶栏搜索触发");

            state.SearchInline = true;
            bar.ApplyState();
            pump();
            Assert(bar.SearchEntryButton is not null, "总览卡关闭但开启内联搜索时应保留顶栏搜索入口");
            bar.TriggerSearch();
            Assert(WaitUntil(pump, () => bar.SearchPopup is not null),
                "总览卡关闭但开启内联搜索时触发搜索应弹出内联搜索窗口");
            bar.TriggerSearch();
            Assert(bar.SearchPopup is null, "再次触发应关闭内联搜索窗口");
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

    private static void CustomModuleOrderUpdatesLayout(Action<Window> show, Action pump)
    {
        var state = BarState();
        state.VisibilityMode = TopBarVisibilityMode.SmartHide;
        TopBarRules.MoveModule(state, TopBarModuleKind.Weather, -1);
        TopBarRules.MoveModuleToSide(state, TopBarModuleKind.Clock, TopBarModuleSide.Left);

        using var sampler = new WindowsPerformanceSamplingService();
        var bar = new TopBarWindow(state, new DesktopStyleState(), new FakeEnvironment(), sampler);
        show(bar);
        try
        {
            pump();
            var leftModules = (StackPanel)bar.FindName("LeftModules");
            var rightModules = (StackPanel)bar.FindName("RightModules");
            var weather = ElementByName(bar, "天气模块");
            var todo = ElementByName(bar, "待办模块");
            var clock = ElementByName(bar, "时间日期模块");
            Assert(ReferenceEquals(weather.Parent, leftModules) && ReferenceEquals(clock.Parent, leftModules),
                "自定义排序后天气与移到左侧的时钟应位于 LeftModules");
            Assert(leftModules.Children.IndexOf(weather) < leftModules.Children.IndexOf(todo),
                "天气左移后应排在待办概要之前");
            Assert(!ReferenceEquals(clock.Parent, rightModules), "移到左侧的时钟不应再留在 RightModules");

            TopBarRules.ResetOrder(state);
            bar.ApplyState();
            pump();
            var resetWeather = ElementByName(bar, "天气模块");
            var resetTodo = ElementByName(bar, "待办模块");
            var resetClock = ElementByName(bar, "时间日期模块");
            Assert(leftModules.Children.IndexOf(resetTodo) < leftModules.Children.IndexOf(resetWeather),
                "恢复默认顺序后 ApplyState 应立即把待办排回天气之前");
            Assert(ReferenceEquals(resetClock.Parent, rightModules),
                "恢复默认顺序后时钟应回到 RightModules");
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

    internal static void WindowsIndexQueryRules()
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

        Task.Run(() => VerifyAbandonedWindowsIndexQueryAsync(cancel: true)).GetAwaiter().GetResult();
        Task.Run(() => VerifyAbandonedWindowsIndexQueryAsync(cancel: false)).GetAwaiter().GetResult();
    }

    private static async Task VerifyAbandonedWindowsIndexQueryAsync(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var lateFailure = new InvalidOperationException("Windows index test provider failed after timeout.");
        var unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            if (args.Exception.InnerExceptions.Any(exception => ReferenceEquals(exception, lateFailure)
                || exception is OperationCanceledException canceled && canceled.CancellationToken == cancellation.Token))
            {
                Interlocked.Increment(ref unobserved);
                args.SetObserved();
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await AbandonWindowsIndexQueryAsync(cancellation, cancel, lateFailure);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert(Volatile.Read(ref unobserved) == 0, "查询取消或超时后，后台任务的异常应被观察");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    private static async Task AbandonWindowsIndexQueryAsync(
        CancellationTokenSource cancellation, bool cancel, Exception lateFailure)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var search = WindowsIndexSearchClient.SearchAsync("报告", cancellation.Token, (_, token) =>
        {
            started.SetResult();
            releaseProvider.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            throw lateFailure;
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancel)
            {
                cancellation.Cancel();
                try
                {
                    await search;
                    throw new InvalidOperationException("取消时应立即结束外层等待");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
            else
            {
                Assert(!(await search).Available, "超时应回退到索引暂不可用");
            }
            var busy = await WindowsIndexSearchClient.SearchAsync("报告", CancellationToken.None,
                (_, _) => throw new InvalidOperationException("旧查询退出前不能开始另一轮原生查询"));
            Assert(!busy.Available, "仍在后台工作的查询应保留查询槽");
        }
        finally
        {
            releaseProvider.TrySetResult();
        }
        var next = await WindowsIndexSearchClient.SearchAsync("报告", CancellationToken.None,
            (_, _) => new WindowsIndexSearchResult(true, []));
        Assert(next.Available, "后台查询完成后应释放查询槽");
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
        internal PeripheralBatterySnapshot PeripheralSnapshot { get; set; } = PeripheralBatterySnapshot.Empty;
        internal int PeripheralRefreshes { get; private set; }
        internal int PeripheralAutomaticRefreshes { get; private set; }

        public PeripheralBatterySnapshot ReadPeripheralBattery() => PeripheralSnapshot;

        public void RefreshPeripheralBatteryIfDue() => PeripheralAutomaticRefreshes++;

        public void ForceRefreshPeripheralBattery() => PeripheralRefreshes++;

        public bool Use24HourClock => true;

        public WeatherSnapshot? ReadWeather() => Weather;

        public void RefreshWeatherIfDue()
        {
        }

        public int CountIncompleteTodos() => Todos;

        public TopBarTodoList? ReadTodoList() => TodoList;

        public void TodoItemToggled() => Toggles++;

        public bool IsLauncherEnabled => LauncherEnabled;

        public bool IsEverythingSearchEnabled => false;

        public void ToggleLauncherPanel() => LauncherToggles++;

        public void OpenTopBarSettings() => TopBarPageRequested++;

        internal List<TopBarModuleKind> QuickSettingsKinds { get; } = [];

        internal int InputMethodRequested { get; private set; }
        internal List<TopBarInputMethodOption> ActivatedInputMethods { get; } = [];
        internal IReadOnlyList<TopBarInputMethodOption> InputMethodOptions { get; set; } =
        [
            new(
                "tip:0804:ms:pinyin",
                "微软拼音",
                "中",
                0x0804,
                IsInputProcessor: true,
                IsActive: true,
                KeyboardLayout: 0x08040804,
                Clsid: Guid.Parse("81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E"),
                ProfileGuid: Guid.Parse("FA550B04-5AD7-411F-A5AC-CA038EC515D7")),
            new(
                "tip:0804:wx:ime",
                "微信输入法",
                "中",
                0x0804,
                IsInputProcessor: true,
                IsActive: false,
                KeyboardLayout: 0x08040804,
                Clsid: Guid.Parse("11111111-1111-1111-1111-111111111111"),
                ProfileGuid: Guid.Parse("22222222-2222-2222-2222-222222222222"))
        ];

        internal List<string> OpenedSettingsUris { get; } = [];
        internal int ShutdownDialogRequested { get; private set; }
        internal Task<TopBarBatterySnapshot>? BatteryReadTask { get; set; }
        internal List<bool> MasterMutedCalls { get; } = [];
        internal List<string> SwitchedAudioDevices { get; } = [];
        internal List<TopBarPowerMode> PowerModeChanges { get; } = [];
        internal List<string> ConnectedWifiProfiles { get; } = [];

        public void OpenQuickSettings(TopBarModuleKind kind)
        {
            QuickSettingsRequested++;
            QuickSettingsKinds.Add(kind);
        }

        public void OpenSystemSettings(string settingsUri) => OpenedSettingsUris.Add(settingsUri);

        public void OpenShutdownDialog() => ShutdownDialogRequested++;

        public Task<TopBarAudioSnapshot> ReadAudioSnapshotAsync() => Task.FromResult(
            new TopBarAudioSnapshot(
                MasterPercent: 60,
                MasterMuted: false,
                Available: true,
                Devices:
                [
                    new TopBarAudioDevice("dev-spk", "扬声器", IsDefault: true),
                    new TopBarAudioDevice("dev-bt", "蓝牙耳机", IsDefault: false)
                ],
                Sessions:
                [
                    new TopBarAudioSession(42, "Edge", 80, false, true)
                ]));

        public Task<bool> SetMasterVolumeAsync(int percent) => Task.FromResult(true);

        public Task<bool> SetMasterMuteAsync(bool muted)
        {
            MasterMutedCalls.Add(muted);
            return Task.FromResult(true);
        }

        public Task<bool> SetDefaultOutputDeviceAsync(string deviceId)
        {
            SwitchedAudioDevices.Add(deviceId);
            return Task.FromResult(true);
        }

        public Task<bool> SetAudioSessionVolumeAsync(string? deviceId, uint processId, int percent) => Task.FromResult(true);

        public Task<bool> SetAudioSessionMuteAsync(string? deviceId, uint processId, bool muted) => Task.FromResult(true);

        public Task<TopBarBatterySnapshot> ReadBatterySnapshotAsync() => BatteryReadTask ?? Task.FromResult(
            new TopBarBatterySnapshot(
                Percent: 85,
                Status: TopBarBatteryStatus.OnBattery,
                IsPowerSaver: false,
                RemainingSeconds: 7200,
                PowerModeSupported: true,
                PowerMode: TopBarPowerMode.Balanced,
                BrightnessSupported: true,
                BrightnessPercent: 70));

        public Task<bool> SetPowerModeAsync(TopBarPowerMode mode)
        {
            PowerModeChanges.Add(mode);
            return Task.FromResult(true);
        }

        public Task<bool> SetBuiltInBrightnessAsync(int percent) => Task.FromResult(true);

        public Task<TopBarNetworkSnapshot> ReadNetworkSnapshotAsync() => Task.FromResult(
            new TopBarNetworkSnapshot(
                IsConnected: true,
                IsWireless: true,
                ConnectionTitle: "Home5G",
                CurrentSsid: "Home5G",
                CurrentProfileName: "Home5G",
                SignalQuality: 90,
                LinkSpeedBitsPerSecond: 866_000_000L,
                Ipv4Address: "192.168.1.25",
                AdapterName: "Wi-Fi 6",
                InterfaceGuid: Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"),
                HasWifiInterface: true,
                WifiListAccessDenied: false,
                AvailableNetworks:
                [
                    new TopBarWifiNetwork("Home5G", "Home5G", 90, true, true, true),
                    new TopBarWifiNetwork("Office5G", "Office5G", 76, false, true, true),
                    new TopBarWifiNetwork("CafeGuest", null, 55, false, false, true)
                ]));

        public Task<bool> ConnectSavedWifiAsync(Guid interfaceGuid, string profileName)
        {
            ConnectedWifiProfiles.Add(profileName);
            return Task.FromResult(true);
        }

        public Task<bool> DisconnectWifiAsync(Guid interfaceGuid) => Task.FromResult(true);

        public Task<bool> TriggerWifiScanAsync(Guid interfaceGuid) => Task.FromResult(true);

        public void OpenInputMethod() => InputMethodRequested++;

        public IReadOnlyList<TopBarInputMethodOption> ReadInputMethodOptions() => InputMethodOptions;

        public bool ActivateInputMethod(TopBarInputMethodOption option, nint foregroundWindow)
        {
            ActivatedInputMethods.Add(option);
            return true;
        }

        internal int TrayOverflowRequested { get; private set; }
        internal int TrayOverflowClosed { get; private set; }
        public void OpenTrayOverflow() => TrayOverflowRequested++;
        public void CloseTrayOverflow() => TrayOverflowClosed++;

        public void OpenTaskManagerPerformance() => TaskManagerRequested++;

        public void OpenComponentSettings(DesktopComponentKind kind) => ComponentRequested = kind;

        public bool ActivateWindow(nint handle, uint processId, nint monitorHandle) => false;

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
            Assert(!bar.CapsuleDetailsExpanded && environment.LauncherToggles == 1 && bar.SearchPopup is null,
                "默认模式下胶囊搜索入口应收起待办并打开冰蓝总览卡");

            state.SearchInline = true;
            bar.ApplyState();
            pump();
            Invoke(summary);
            bar.TriggerSearch();
            Assert(!bar.CapsuleDetailsExpanded && bar.SearchPopup is not null,
                "开启内联搜索后胶囊搜索入口应收起待办并弹出内联搜索窗口");
            Invoke(ButtonByName(bar, "今日待办概要，点击查看列表"));
            Assert(bar.CapsuleDetailsExpanded && bar.SearchPopup is null,
                "展开胶囊待办应收起已有的内联搜索窗口");
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
