using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using BingLan.App.Services;
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

        var state = new DockState { IsEnabled = true, VisibilityMode = DockVisibilityMode.SmartHide };
        var dock = new IceBlueDockWindow(state);
        show(dock);
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var yieldToMenu = typeof(IceBlueDockWindow).GetMethod("YieldToSystemMenu", flags)!;
            var showDock = typeof(IceBlueDockWindow).GetMethod("ShowDock", flags)!;
            Assert((bool)yieldToMenu.Invoke(dock, new object[] { true })! && !dock.IsVisible,
                "系统菜单打开时 Dock 应立即收起");
            showDock.Invoke(dock, new object[] { false });
            Assert(!dock.IsVisible, "系统菜单打开时其他显示请求不能覆盖菜单");
            Assert(!(bool)yieldToMenu.Invoke(dock, new object[] { false })! && dock.IsVisible,
                "系统菜单关闭后应恢复原本显示的 Dock");
            var apply = typeof(IceBlueDockWindow).GetMethod("ApplyItems", flags)!;
            var length = typeof(IceBlueDockWindow).GetMethod("ContentLengthDip", flags)!;
            var panel = (StackPanel)dock.FindName("ItemPanel");
            void Apply(params DockItem[] items) => apply.Invoke(dock, new object[] { items });

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
            Apply();
            Assert(panel.Children.Count == 1, "再次开启后应立即恢复入口");
            var apps = DockItemComposer.Compose(new[] { new DockPinnedApp { DisplayName = "应用", ExecutablePath = @"C:\Apps\Example.exe" } }, []);
            apply.Invoke(dock, new object[] { apps });
            Assert(panel.Children[0] is Button first && AutomationProperties.GetName(first) == "打开 Windows 开始菜单",
                "开始入口应固定在应用前面");
            Assert((double)length.Invoke(dock, null)! == DockLayoutMetrics.ContentLengthForItems(2, state.IconSize) + DockLayoutMetrics.DividerDip,
                "Dock 宽度应计入入口、应用和分隔线");
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
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
