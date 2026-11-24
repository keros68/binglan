using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using BingLan.App;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.App.Windows;
using BingLan.Core.Models;
using BingLan.Core.Services;

internal static class FullScreenHotkeyTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    // FullScreenWatcher deliberately excludes the host process's own windows.
    internal static int RunProbe(Application app)
    {
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
        return app.Run(new Window
        {
            Title = "冰蓝全屏快捷键测试窗口",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Width = 320,
            Height = 240
        });
    }

    internal static void Run(Action pump, Action<Func<bool>, string> until)
    {
        var monitors = MonitorCatalog.GetAll();
        var gameMonitor = monitors.First();
        var cursorMonitor = monitors.Last();
        var bounds = gameMonitor.Bounds;
        var cursorBounds = cursorMonitor.Bounds;
        Console.WriteLine($"{monitors.Count} 块显示器，前台窗口 {gameMonitor.Label}，指针 {cursorMonitor.Label}");

        var temp = Directory.CreateTempSubdirectory("BingLan-fullscreen-hotkeys-");
        var state = new AppState();
        state.Hotkeys.Launcher = "Ctrl+Alt+Shift+F9";
        state.Hotkeys.PeekFileBoxes = "Ctrl+Alt+Shift+F10";
        new LocalStateStore(temp.FullName).Save(state);
        var previousForeground = DockNativeMethods.GetForegroundWindow();
        Check(DockNativeMethods.GetCursorPos(out var originalCursor), "无法读取测试前的鼠标位置");

        using var probe = Process.Start(new ProcessStartInfo(
            Path.Combine(AppContext.BaseDirectory, "BingLan.UiSmokeTests.exe"), "--fullscreen-hotkey-probe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        WidgetCoordinator? coordinator = null;
        FileBoxWindow? box = null;
        try
        {
            coordinator = new WidgetCoordinator(interactiveQa: true, dataDirectory: temp.FullName);
            var hotkeys = Field<GlobalHotkeyHost>(coordinator, "_hotkeys");
            var peek = Field<DesktopPeekController>(coordinator, "_peek");
            var preferences = Field<AppState>(coordinator, "_state").LauncherPreferences;
            box = new FileBoxWindow(new FileBoxState()) { CanClose = true };
            Field<List<WidgetWindowBase>>(coordinator, "_windows").Add(box);
            box.Show();
            GlobalHotkeyKind? received = null;
            hotkeys.HotkeyPressed += kind => received = kind;
            hotkeys.Sync(state.Hotkeys, launcherEnabled: true, cleanDesktopEnabled: false);
            Check(hotkeys.IsRegistered(GlobalHotkeyKind.Launcher) && hotkeys.IsRegistered(GlobalHotkeyKind.PeekFileBoxes),
                "测试快捷键被占用");

            until(() => { probe.Refresh(); return probe.MainWindowHandle != 0; }, "全屏测试窗口未启动");
            var hwnd = probe.MainWindowHandle;
            Check(NativeMethods.SetWindowPos(hwnd, 0, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate), "无法设置测试窗口全屏");
            until(() => FullScreenWatcher.IsFullScreen(hwnd, excludeMaximized: true), "测试窗口未覆盖整个显示器");
            TopBarWindowTests.ForceForeground(hwnd);
            until(() => DockNativeMethods.GetForegroundWindow() == hwnd, "测试窗口未获得前台焦点");
            var cursorX = cursorBounds.Left + cursorBounds.Width / 2;
            var cursorY = cursorBounds.Top + cursorBounds.Height / 2;
            Press(GlobalHotkeyKind.Launcher, Key.F9);
            // Verify the path that would minimize other windows only after testing the shared guard.
            preferences.BackdropMode = LauncherBackdropMode.ShowDesktop;
            Press(GlobalHotkeyKind.Launcher, Key.F9);
            Press(GlobalHotkeyKind.PeekFileBoxes, Key.F10);

            void Press(GlobalHotkeyKind kind, Key key)
            {
                Check(SetCursorPos(cursorX, cursorY) && DockNativeMethods.GetCursorPos(out var cursor)
                    && cursor.X >= cursorBounds.Left && cursor.X < cursorBounds.Right
                    && cursor.Y >= cursorBounds.Top && cursor.Y < cursorBounds.Bottom,
                    "鼠标未进入指定显示器");
                Check(DockNativeMethods.GetForegroundWindow() == hwnd
                    && FullScreenWatcher.IsFullScreen(hwnd, excludeMaximized: true), "测试前窗口已失焦或退出全屏");
                received = null;
                Check(ShellSurfaceNativeMethods.SendChord([0x11, 0x12, 0x10], (ushort)KeyInterop.VirtualKeyFromKey(key)),
                    $"无法发送 {key}");
                until(() => received == kind, $"冰蓝未收到 {kind} 快捷键");
                pump();
                Check(Field<LauncherPanelWindow?>(coordinator, "_launcherPanel") is null && !peek.IsActive
                    && !DockNativeMethods.IsIconic(hwnd) && DockNativeMethods.GetForegroundWindow() == hwnd,
                    $"{kind} 唤出了面板、最小化测试窗口或改变了前台焦点");
            }
        }
        finally
        {
            if (coordinator is not null)
            {
                Field<DesktopPeekController>(coordinator, "_peek").End(restoreForeground: false);
                coordinator.Dispose();
            }
            box?.Close();
            if (!probe.HasExited && (!probe.CloseMainWindow() || !probe.WaitForExit(2000)))
            {
                probe.Kill();
                probe.WaitForExit(2000);
            }
            SetCursorPos(originalCursor.X, originalCursor.Y);
            if (previousForeground != 0 && DockNativeMethods.IsWindow(previousForeground))
            {
                TopBarWindowTests.ForceForeground(previousForeground);
            }
            pump();
            temp.Delete(recursive: true);
        }
    }

    private static T Field<T>(WidgetCoordinator coordinator, string name) =>
        (T)typeof(WidgetCoordinator).GetField(name, Private)!.GetValue(coordinator)!;

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
