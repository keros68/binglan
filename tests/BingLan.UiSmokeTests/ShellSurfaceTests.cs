using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using BingLan.App.Dock;
using BingLan.App.Services;
using BingLan.App.Taskbar;
using BingLan.App.Interop;
using BingLan.App.Windows;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.Themes;
using Button = System.Windows.Controls.Button;

internal static class ShellSurfaceTests
{
    internal static void Run(Action<Window> show, Action pump)
    {
        var input = typeof(ShellSurfaceNativeMethods).GetNestedType("Input", BindingFlags.NonPublic)!;
        Assert(Marshal.SizeOf(input) == (Environment.Is64BitProcess ? 40 : 28),
            "原生快捷键 INPUT 必须保留完整联合体尺寸");
        Assert(ShellSurfaceService.IsQuickSettingsSurface("ControlCenterRegion", "NamedContainerAutomationPeer", "ShellHost"),
            "应识别新版 Shell 的实际快速设置区域");
        Assert(ShellSurfaceService.IsQuickSettingsSurface("", "ControlCenterWindow", "ShellExperienceHost"),
            "应识别快速设置宿主窗口");
        Assert(!ShellSurfaceService.IsQuickSettingsSurface("ControlCenterRegion", "", "Example"),
            "第三方窗口不能当作系统面板打开成功");
        Assert(!ShellSurfaceService.IsQuickSettingsSurface("", "Windows.UI.Core.CoreWindow", "ShellHost"),
            "其他系统面板不能当作快速设置");
        Assert(ShellSurfaceNativeMethods.IsSystemMenu("Shell_TrayWnd", "explorer"), "原生任务栏菜单应让 Dock 让开");
        Assert(ShellSurfaceNativeMethods.IsSystemMenu("Xaml_WindowedPopupClass", "explorer"), "原生快捷菜单应让 Dock 让开");
        Assert(ShellSurfaceNativeMethods.IsSystemMenu("TopLevelWindowForOverflowXamlIsland", "explorer"), "原生托盘浮层应让 Dock 让开");
        Assert(!ShellSurfaceNativeMethods.IsSystemMenu("TopLevelWindowForOverflowXamlIsland", "Example"), "其他程序不能冒充托盘面板");
        Assert(ShellSurfaceNativeMethods.IsSystemMenu("Windows.UI.Core.CoreWindow", "StartMenuExperienceHost"), "开始菜单应让 Dock 让开");
        Assert(ShellSurfaceNativeMethods.IsSystemMenu("Windows.UI.Core.CoreWindow", "SearchHost"), "25H2 由 SearchHost 宿主开始菜单，也应让 Dock 让开");
        Assert(ShellSurfaceNativeMethods.IsStartMenuSurface("Windows.UI.Core.CoreWindow", "SearchHost"), "开始菜单确认应接受 25H2 的 SearchHost 宿主");
        Assert(ShellSurfaceNativeMethods.IsStartMenuSurface("Windows.UI.Core.CoreWindow", "ShellExperienceHost"), "开始菜单确认应接受 ShellExperienceHost 宿主");
        Assert(ShellSurfaceNativeMethods.IsStartMenuSurface("DV2ControlHost", "explorer"), "开始菜单确认应接受 explorer 的经典/第三方开始菜单宿主");
        Assert(ShellSurfaceNativeMethods.IsStartMenuSurface("OpenShellMenu.CMenuContainer", "explorer"), "开始菜单确认应接受 Open-Shell 菜单宿主");
        Assert(!ShellSurfaceNativeMethods.IsStartMenuSurface("DV2ControlHost", "Example"), "第三方进程不能冒充经典开始菜单");
        Assert(!ShellSurfaceNativeMethods.IsStartMenuSurface("Xaml_WindowedPopupClass", "SearchHost"), "非 CoreWindow 不能当作开始菜单");
        Assert(ShellSurfaceService.IsStartButton("StartButton", "ToggleButton"), "UIA 开始按钮应匹配 StartButton 标识");
        Assert(ShellSurfaceService.IsStartButton(string.Empty, "Start"), "传统开始按钮应匹配 Start 类名");
        Assert(!ShellSurfaceService.IsStartButton("SystemTrayIcon", "SystemTray.NormalButton"), "托盘按钮不能当作开始按钮");
        Assert(!ShellSurfaceService.IsStartButton("InputIndicatorButton", "Button"), "输入法按钮不能当作开始按钮");
        Assert(!ShellSurfaceNativeMethods.IsSystemMenu("Windows.UI.Core.CoreWindow", "Example"), "普通应用不能当作系统菜单");
        Assert(!ShellSurfaceNativeMethods.IsSystemMenu("Progman", "explorer"), "桌面不能触发系统菜单避让");
        Assert(ShellSurfaceService.IsQuickSettingsButton("SystemTrayIcon", "SystemTray.OmniButton", "Volume: 50%"),
            "快速设置入口应不依赖中文名称");
        Assert(ShellSurfaceService.IsQuickSettingsButton("SystemTrayIcon", "SystemTray.OmniButtonCenter", "音量 50%"),
            "应支持已验证的分段托盘按钮");
        foreach (var wrongClass in new[] { "SystemTray.OmniButtonLeft", "SystemTray.OmniButtonRight", "SystemTray.NormalButton" })
        {
            Assert(!ShellSurfaceService.IsQuickSettingsButton("SystemTrayIcon", wrongClass, "音量"),
                "不能把日历、通知或普通托盘图标当成快速设置");
        }
        Assert(!ShellSurfaceService.IsQuickSettingsButton("NotifyItemIcon", "SystemTray.OmniButton", "音量工具"),
            "第三方应用名称不能触发系统入口识别");
        Assert(ShellSurfaceService.IsInputMethodButton("InputIndicatorButton", "Button", "IME"), "支持输入指示器标识");
        Assert(!ShellSurfaceService.IsInputMethodButton("SystemTrayIcon", "SystemTray.NormalButton", "任务栏输入指示 中文模式"),
            "中英文模式图标不能当成输入法选择器");
        Assert(!ShellSurfaceService.IsInputMethodButton("NotifyItemIcon", "SystemTray.NormalButton", "任务栏输入指示工具"),
            "输入法入口不能匹配应用托盘图标");
        TestOverflowChevron();
        Assert(TaskbarCornerReveal.IsTaskbarClass("Shell_TrayWnd") && TaskbarCornerReveal.IsTaskbarClass("Shell_SecondaryTrayWnd"),
            "主副屏任务栏移动时都应立即复位两角细条");
        Assert(!TaskbarCornerReveal.IsTaskbarClass("CabinetWClass"), "资源管理器窗口移动不能触发细条复位");

        var state = new DockState
        {
            IsEnabled = true,
            VisibilityMode = DockVisibilityMode.SmartHide,
            RevealMode = DockRevealMode.HandleOnly,
            ShowLauncherButton = false
        };
        nint trayMonitor = 0;
        var trayAvailable = true;
        var dock = new IceBlueDockWindow(state)
        {
            TryOpenTrayForApplication = monitor =>
            {
                trayMonitor = monitor;
                return trayAvailable;
            }
        };
        dock.LauncherRequested += () => { };
        show(dock);
        try
        {
            TestTaskbarLayoutRefresh(dock, pump);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var reportResult = typeof(IceBlueDockWindow).GetMethod("ReportCommandResult", flags)!;
            var trayResult = WindowCommandResult.TrayRevealFailed("微信");
            string? commandHint = null;
            dock.CommandFailed += message => commandHint = message;
            reportResult.Invoke(dock, [trayResult]);
            Assert(trayMonitor != 0 && commandHint is null, "Dock 应以自身屏幕打开冰蓝托盘并略过提示");
            trayAvailable = false;
            reportResult.Invoke(dock, [trayResult]);
            Assert(commandHint == trayResult.Message, "独立托盘不可用时应保留原提示");

            var yieldToMenu = typeof(IceBlueDockWindow).GetMethod("YieldToSystemMenu", flags)!;
            var showDock = typeof(IceBlueDockWindow).GetMethod("ShowDock", flags)!;
            var updateForeground = typeof(IceBlueDockWindow).GetMethod("UpdateForForegroundWindow", flags)!;
            var releaseYield = typeof(IceBlueDockWindow).GetMethod("ReleaseSystemMenuYield", flags)!;
            var autoHideState = typeof(IceBlueDockWindow).GetField("_autoHide", flags)!;
            autoHideState.SetValue(dock, new DockAutoHideState());
            showDock.Invoke(dock, null);
            // With animations on the dock fades out first; with them off it steps aside at once.
            var motion = IceBlueDockWindow.Motion;
            IceBlueDockWindow.Motion = BingLan.Core.Models.MotionLevel.Off;
            try
            {
                Assert((bool)yieldToMenu.Invoke(dock, new object[] { true })! && !dock.IsVisible,
                    "关闭动画时系统菜单打开 Dock 应立即收起");
            }
            finally
            {
                IceBlueDockWindow.Motion = motion;
            }
            showDock.Invoke(dock, null);
            Assert(!dock.IsVisible, "系统菜单打开时其他显示请求不能覆盖菜单");
            Assert(!(bool)yieldToMenu.Invoke(dock, new object[] { false })! && !dock.IsVisible,
                "系统菜单关闭后应先判断当前窗口的遮挡，再决定是否显示 Dock");
            releaseYield.Invoke(dock, [true]);
            Assert(!dock.IsVisible, "系统菜单关闭后，遮挡预留区的前台应用应继续阻止 Dock 恢复");
            releaseYield.Invoke(dock, [false]);
            Assert(dock.IsVisible, "当前显示规则允许时，应恢复 Dock");

            state.AutoHideAlways = true;
            try
            {
                var autoHide = new DockAutoHideState();
                autoHide.Update(new DockAutoHideInput { WindowOverlapsDock = true },
                    DateTimeOffset.UtcNow - DockAutoHideState.HideDelay);
                autoHideState.SetValue(dock, autoHide);
                yieldToMenu.Invoke(dock, new object[] { true });
                var flashed = false;
                DependencyPropertyChangedEventHandler watchVisibility = (_, _) => flashed |= dock.IsVisible;
                dock.IsVisibleChanged += watchVisibility;
                try
                {
                    updateForeground.Invoke(dock, [new WindowInteropHelper(dock).Handle]);
                    Assert(!autoHide.IsShown && !flashed && !dock.IsVisible,
                        "系统菜单关闭后仍应智能隐藏时，Dock 不能先按旧状态显示一帧");
                }
                finally
                {
                    dock.IsVisibleChanged -= watchVisibility;
                }
            }
            finally
            {
                state.AutoHideAlways = false;
            }
            var apply = typeof(IceBlueDockWindow).GetMethod("ApplyItems", flags)!;
            var length = typeof(IceBlueDockWindow).GetMethod("ContentLengthDip", flags)!;
            var panel = (StackPanel)dock.FindName("ItemPanel");
            void Apply(params DockItem[] items) => apply.Invoke(dock, new object[] { items });

            state.ShowDesktopButton = false;
            Apply();
            var button = panel.Children.OfType<Button>().Single();
            Assert(AutomationProperties.GetName(button) == "打开 Windows 开始菜单", "空 Dock 应提供独立开始菜单入口");
            Assert(button.Tag is null, "入口不能成为可拖动应用项");
            Assert(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) is not null, "入口应支持辅助功能调用");
            Assert((double)length.Invoke(dock, null)! == DockLayoutMetrics.ContentLengthForItems(1, state.IconSize),
                "空 Dock 宽度应计入开始入口");

            state.ShowStartMenuButton = false;
            Apply();
            Assert(panel.Children.Count == 0, "关闭开关后同一应用快照也应移除入口");
            state.ShowStartMenuButton = true;

            state.ShowDesktopButton = true;
            Apply();
            var entries = panel.Children.OfType<Button>().ToList();
            Assert(entries.Count == 2
                    && AutomationProperties.GetName(entries[^1]) == "显示桌面"
                    && entries[^1].Tag is null,
                "显示桌面应跟在开始菜单之后，且不参与应用拖动");
            Assert((double)length.Invoke(dock, null)!
                    == DockLayoutMetrics.ContentLengthForItems(2, state.IconSize),
                "功能入口之间不分隔");
            state.ShowDesktopButton = false;
            Apply();
            Apply();
            Assert(panel.Children.Count == 1, "再次开启后应立即恢复入口");
            var apps = DockItemComposer.Compose(new[] { new DockPinnedApp { DisplayName = "应用", ExecutablePath = @"C:\Apps\Example.exe" } }, []);
            apply.Invoke(dock, new object[] { apps });
            Assert(panel.Children[0] is Button first && AutomationProperties.GetName(first) == "打开 Windows 开始菜单",
                "开始入口应固定在应用前面");
            Assert((double)length.Invoke(dock, null)! == DockLayoutMetrics.ContentLengthForItems(2, state.IconSize) + DockLayoutMetrics.DividerDip,
                "Dock 宽度应计入入口、应用和分隔线");

            state.ShowLauncherButton = true;
            state.ShowDesktopButton = true;
            apply.Invoke(dock, [apps]);
            Assert(panel.Children.OfType<Button>().Take(3).Select(AutomationProperties.GetName)
                .SequenceEqual(["打开 Windows 开始菜单", "打开冰蓝总览卡", "显示桌面"]),
                "三个入口应依次显示在应用左侧");
            Assert(panel.Children.OfType<Border>().Count() == 1
                && (double)length.Invoke(dock, null)! == DockLayoutMetrics.ContentLengthForItems(4, state.IconSize) + DockLayoutMetrics.DividerDip,
                "功能组与应用之间只分隔一次");
            state.ShowStartMenuButton = false;
            state.ShowLauncherButton = false;
            state.ShowDesktopButton = false;
            apply.Invoke(dock, [apps]);
            Assert(panel.Children.Count == 1 && panel.Children[0] is IceBlueDockWindow.DockItemButton { Item: not null },
                "隐藏三个入口后只显示应用，不留分隔线");

            state.ShowQuickSettingsButton = true;
            apply.Invoke(dock, [apps]);
            Assert(panel.Children[0] is Button quickSettings
                && AutomationProperties.GetName(quickSettings) == "打开 Windows 快速设置"
                && quickSettings.Tag is null
                && new ButtonAutomationPeer(quickSettings).GetPattern(PatternInterface.Invoke) is not null,
                "快速设置应是可访问的独立功能按钮，固定在应用之前");
            Assert((double)length.Invoke(dock, null)!
                    == DockLayoutMetrics.ContentLengthForItems(2, state.IconSize) + DockLayoutMetrics.DividerDip,
                "快速设置按钮应计入 Dock 宽度与分隔线");
            state.ShowQuickSettingsButton = false;
            apply.Invoke(dock, [apps]);
            Assert(panel.Children.Count == 1 && panel.Children[0] is IceBlueDockWindow.DockItemButton,
                "关闭快速设置后同一应用快照应移除按钮和分隔线");
        }
        finally
        {
            dock.Close();
            pump();
        }

        var appState = LocalStateStore.CreateDefault();
        appState.Dock.ShowStartMenuButton = false;
        var theme = ThemeRules.Export(appState, "系统入口验证", new ThemeArea(0, 0, 1920, 1080), new Version(0, 2, 25));
        Assert(!JsonSerializer.Serialize(theme).Contains("ShowStartMenuButton", StringComparison.Ordinal),
            "本机开始菜单开关不能进入主题包");
        Assert(!JsonSerializer.Serialize(theme).Contains("ShowLauncherButton", StringComparison.Ordinal),
            "本机启动面板入口开关不能进入主题包");
        Assert(!JsonSerializer.Serialize(theme).Contains("ShowDesktopButton", StringComparison.Ordinal),
            "本机“显示桌面”按钮开关不能进入主题包");
    }

    private static void TestTaskbarLayoutRefresh(IceBlueDockWindow dock, Action pump)
    {
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var appBar = (DockAppBarController)typeof(IceBlueDockWindow)
            .GetField("_appBar", privateInstance)!.GetValue(dock)!;
        var monitor = appBar.Monitor!;
        var cache = (Dictionary<PixelRect, (long ReadAt, TaskbarEdgeGuard.NativeTaskbarLayout? Layout)>)
            typeof(TaskbarEdgeGuard).GetField("NativeLayouts", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetValue(null)!;
        var otherMonitor = new PixelRect(int.MinValue, 0, int.MinValue + 1920, 1080);
        var unrelated = (Environment.TickCount64, (TaskbarEdgeGuard.NativeTaskbarLayout?)null);
        var previous = cache.TryGetValue(otherMonitor, out var previousValue);
        var stale = new TaskbarEdgeGuard.NativeTaskbarLayout(DockEdge.Left,
            monitor.Bounds with { Right = monitor.Bounds.Left + monitor.Bounds.Width / 3 }, false);
        var hook = typeof(DockAppBarController).GetMethod("WindowProc", privateInstance)!;
        try
        {
            TaskbarEdgeGuard.InvalidateNativeTaskbarLayout(monitor.Bounds);
            dock.ApplyState();
            var expectedBounds = appBar.Bounds;
            var nativeLayout = TaskbarEdgeGuard.GetNativeTaskbarLayout(monitor.Bounds);
            cache[otherMonitor] = unrelated;

            foreach (var message in new[] { NativeMethods.WmSettingChange,
                DockNativeMethods.WmDisplayChange, DockNativeMethods.WmDpiChanged })
            {
                cache[monitor.Bounds] = (Environment.TickCount64, stale);
                dock.ApplyState();
                Assert(appBar.Bounds != expectedBounds,
                    "缓存中的旧侧边应复现 Dock 水平位置错误，普通内容重排仍复用缓存");
                hook.Invoke(appBar, [(nint)0, message,
                    message == NativeMethods.WmSettingChange ? (nint)NativeMethods.SpiSetWorkArea : (nint)0,
                    (nint)0, false]);
                // Another consumer may read the old layout before the queued work runs.
                cache[monitor.Bounds] = (Environment.TickCount64, stale);
                pump();
                Assert(appBar.Bounds == expectedBounds
                    && TaskbarEdgeGuard.GetNativeTaskbarLayout(monitor.Bounds) == nativeLayout,
                    "工作区、显示器或缩放变化应在重排前刷新当前显示器的任务栏位置");
                Assert(cache[otherMonitor] == unrelated,
                    "当前显示器的布局通知不能清空其他显示器的缓存");
            }

            cache[monitor.Bounds] = (Environment.TickCount64, stale);
            var taskbarCreated = (uint)typeof(DockAppBarController)
                .GetField("_taskbarCreatedMessage", privateInstance)!.GetValue(appBar)!;
            hook.Invoke(appBar, [(nint)0, (int)taskbarCreated, (nint)0, (nint)0, false]);
            pump();
            Assert(appBar.Bounds == expectedBounds
                && TaskbarEdgeGuard.GetNativeTaskbarLayout(monitor.Bounds) == nativeLayout,
                "Explorer 重建后智能隐藏 Dock 应使用新读取的原生任务栏位置");

            if (!dock.IsVisible) dock.Show();
            var fixture = new System.Windows.Interop.WindowInteropHelper(dock).Handle;
            Assert(DockNativeMethods.GetWindowRect(fixture, out var liveRect), "应能读取已显示 Dock 的 HWND 矩形");
            var liveBounds = new PixelRect(liveRect.Left, liveRect.Top, liveRect.Right, liveRect.Bottom);
            var staleBounds = new PixelRect(monitor.Bounds.Right + 100, monitor.Bounds.Bottom + 100,
                monitor.Bounds.Right + 200, monitor.Bounds.Bottom + 200);
            cache[monitor.Bounds] = (Environment.TickCount64,
                new TaskbarEdgeGuard.NativeTaskbarLayout(DockEdge.Bottom, staleBounds, true, fixture));
            Assert(TaskbarEdgeGuard.IsVisibleTaskbarOverlapping(monitor, liveBounds),
                "Dock 让位恢复应读取当前显示器已登记窗口的实时矩形");
            Assert(!TaskbarEdgeGuard.IsVisibleTaskbarOverlapping(monitor, staleBounds),
                "缓存的布局矩形即使相交也不能阻止 Dock 恢复");
        }
        finally
        {
            TaskbarEdgeGuard.InvalidateNativeTaskbarLayout(monitor.Bounds);
            if (previous) cache[otherMonitor] = previousValue;
            else cache.Remove(otherMonitor);
        }
    }

    private static void TestOverflowChevron()
    {
        static TrayOverflowController.TrayButtonInfo Chevron(string name, double left) =>
            new("SystemTrayIcon", "SystemTray.NormalButton", name, left, left + 24);
        static TrayOverflowController.TrayButtonInfo Pinned(double left) =>
            new("NotifyItemIcon", "SystemTray.NormalButton", "应用", left, left + 24);
        static TrayOverflowController.TrayButtonInfo Ime(double left) =>
            new("SystemTrayIcon", "SystemTray.NormalButton", "IME", left, left + 24);
        static TrayOverflowController.TrayButtonInfo Omni(double left) =>
            new("SystemTrayIcon", "SystemTray.OmniButton", "Network", left, left + 80);
        int Find(TrayOverflowController.TrayButtonInfo[] buttons, int focused, int remembered = -1) =>
            TrayOverflowController.FindOverflowChevron(buttons, focused, remembered, out _);

        Assert(Find([Pinned(100), Chevron("Show hidden icons", 60)], -1) == 1, "已验证名称应直接匹配");
        Assert(Find([Chevron("显示隐藏的图标", 60)], -1) == 0, "中文名称无需固定图标也能匹配");
        TrayOverflowController.TrayButtonInfo[] german = [Chevron("Ausgeblendete Symbole einblenden", 60), Pinned(100), Pinned(124), Ime(160), Omni(200)];
        Assert(Find(german, 0) == 0, "其他语言下 Win+B 聚焦的首个箭头应可调用");
        Assert(Find(german, 3) == -1, "焦点在输入法模式按钮上时不能调用");
        Assert(Find(german, -1) == -1, "没有焦点证据时不能按位置猜测");
        Assert(Find([Chevron("Afficher", 60), Ime(160), Omni(200)], 0) == -1, "没有固定图标时无法区分箭头与输入法按钮，应失败");
        Assert(Find([Pinned(100), Ime(160), Omni(200)], 1) == -1, "没有箭头时不能把输入法按钮当成箭头");
        Assert(Find([Chevron("رموز", 300), Pinned(200), Pinned(176)], 0) == 0, "从右到左的任务栏应同样识别");
        Assert(Find([Chevron("x", 110), Pinned(100)], 0) == -1, "与固定图标重叠的按钮不能当作箭头");
        Assert(Find(german, -1, remembered: 0) == 0, "关闭时应复用本次已调用的箭头");
        Assert(Find(german, -1, remembered: 4) == -1, "记录的元素若已不是普通托盘按钮则不能调用");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
