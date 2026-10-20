using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using BingLan.App;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.App.Taskbar;
using BingLan.App.Windows;
using BingLan.Core.Models;
using BingLan.Core.Services;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Window = System.Windows.Window;

/// <summary>Opt-in system-surface checks using production windows and disposable personal state.</summary>
internal static class ShellSurfacePreview
{
    internal static void Run(System.Windows.Application app)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-shell-surfaces-{Guid.NewGuid():N}");
        var output = Path.GetFullPath(Path.Combine("artifacts/shell-surfaces", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(output);
        var store = new LocalStateStore(temp);
        var state = LocalStateStore.CreateDefault();
        foreach (var component in state.DesktopExperience.Components) component.IsVisible = false;
        state.TodoWidgets.Clear();
        state.FileBoxes.Clear();
        state.NoteWidgets.Clear();
        state.OnboardingCompleted = true;
        state.Dock.IsEnabled = true;
        state.Dock.VisibilityMode = DockVisibilityMode.SmartHide;
        state.TopBar.IsEnabled = true;
        state.TopBar.Modules.Search = false;
        store.Save(state);
        var coordinator = new WidgetCoordinator(interactiveQa: true, dataDirectory: temp);
        var taskbar = new TaskbarAdapter(Path.Combine(temp, "preview-taskbar.json"));
        var originalTaskbarState = ReadTaskbarState();
        var controls = new WrapPanel();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
        var root = new StackPanel { Children = { controls, status } };
        var host = new Window { Title = "冰蓝系统入口验收", Width = 760, Height = 250, Content = root };
        var mode = (originalTaskbarState & TaskbarNativeMethods.AbsAutoHide) != 0 ? "autohide" : "visible";
        var captures = 0;

        async Task Capture(string name)
        {
            await Task.Delay(2000);
            var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            var centered = name is "start" or "quicklink";
            var width = Math.Min(centered ? 1200 : 900, screen.Width);
            var height = Math.Min(850, screen.Height);
            using var bitmap = new System.Drawing.Bitmap(width, height);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            var left = centered ? screen.Left + (screen.Width - width) / 2 : screen.Right - width;
            graphics.CopyFromScreen(left, screen.Bottom - height, 0, 0, bitmap.Size);
            var path = Path.Combine(output, $"{++captures:00}-{mode}-{name}.png");
            bitmap.Save(path);
            status.Text = $"已保存 {path}\n任务栏状态：{ReadTaskbarState()}；原状态：{originalTaskbarState}";
            Console.WriteLine($"CAPTURE {path}");
            TaskbarNativeMethods.EnumWindows((handle, _) =>
            {
                if (!DockNativeMethods.IsWindowVisible(handle)) return true;
                var className = new System.Text.StringBuilder(256);
                DockNativeMethods.GetClassNameW(handle, className, className.Capacity);
                if (className.ToString() is "ControlCenterWindow" or "TopLevelWindowForOverflowXamlIsland"
                    or "Xaml_WindowedPopupClass" or "Shell_TrayWnd")
                {
                    var title = new System.Text.StringBuilder(256);
                    DockNativeMethods.GetWindowTextW(handle, title, title.Capacity);
                    DockNativeMethods.GetWindowRect(handle, out var rect);
                    Console.WriteLine($"NATIVE class={className} title={title} rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}");
                }
                return true;
            }, 0);
            await Task.Run(() =>
            {
                var element = AutomationElement.FocusedElement;
                for (var depth = 0; element is not null && depth < 12; depth++)
                {
                    Console.WriteLine($"FOCUS id={element.Current.AutomationId} class={element.Current.ClassName} offscreen={element.Current.IsOffscreen} process={element.Current.ProcessId}");
                    element = TreeWalker.RawViewWalker.GetParent(element);
                }
            });
        }

        void Add(string title, Func<Task> action)
        {
            var button = new Button { Content = title, Margin = new Thickness(6), Padding = new Thickness(10, 8, 10, 8),
                Foreground = Brushes.Black, Background = Brushes.LightGray };
            button.Click += async (_, _) => await action();
            controls.Children.Add(button);
        }

        foreach (var label in new[] { "音量", "网络", "电量", "输入法" })
        {
            Add("顶部" + label, async () =>
            {
                var topbar = app.Windows.OfType<TopBarWindow>().Single();
                FindButton(topbar, label + "模块").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Capture(label);
            });
        }
        Add("Dock 开始", async () =>
        {
            var dock = app.Windows.OfType<IceBlueDockWindow>().Single();
            FindButton(dock, "打开 Windows 开始菜单").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Capture("start");
        });
        Add("开始右键", async () =>
        {
            var dock = app.Windows.OfType<IceBlueDockWindow>().Single();
            var args = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                Environment.TickCount, System.Windows.Input.MouseButton.Right)
                { RoutedEvent = UIElement.MouseRightButtonUpEvent };
            FindButton(dock, "打开 Windows 开始菜单").RaiseEvent(args);
            Console.WriteLine($"QUICKLINK handled={args.Handled}");
            await Capture("quicklink");
        });
        Add("原生快速设置实验", async () =>
        {
            Console.WriteLine($"QUICKSETTINGS shortcut={ShellSurfaceNativeMethods.OpenQuickSettings()}");
            await Capture("quicksettings");
        });
        Add("原生托盘实验", async () =>
        {
            var before = ReadTaskbarState();
            Console.WriteLine($"OVERFLOW focus={ShellSurfaceNativeMethods.SendChord(0x5B, 0x42)} state={before}");
            await Task.Delay(600);
            var opened = await Task.Run(() =>
            {
                var tray = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
                if (tray is null) return false;
                foreach (AutomationElement button in tray.FindAll(TreeScope.Descendants,
                             new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayIcon")))
                {
                    var name = button.Current.Name;
                    if (button.Current.ClassName == "SystemTray.NormalButton"
                        && (name == "显示隐藏的图标" || name == "Show hidden icons")
                        && button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)
                        && pattern is InvokePattern invoke)
                    {
                        invoke.Invoke();
                        return true;
                    }
                }
                return false;
            });
            Console.WriteLine($"OVERFLOW invoke={opened}");
            await Capture("overflow");
        });
        Add("临时自动隐藏任务栏", async () =>
        {
            await taskbar.ApplyAsync(TaskbarMode.SmartHide);
            mode = "autohide";
            status.Text = $"任务栏状态：{ReadTaskbarState()}；关闭验收窗口时恢复原状态";
        });
        Add("恢复任务栏", async () =>
        {
            await taskbar.ApplyAsync(TaskbarMode.SystemDefault);
            mode = "visible";
            status.Text = $"任务栏状态：{ReadTaskbarState()}；原状态：{originalTaskbarState}";
        });
        Add("检查收回", async () => await Capture("dismissed"));
        Add("退出验收", () => { host.Close(); return Task.CompletedTask; });
        host.Loaded += (_, _) =>
        {
            coordinator.Start();
            status.Text = $"Windows {Environment.OSVersion.Version}\n{string.Join("\n", MonitorCatalog.GetAll().Select(m => m.Label))}\n原任务栏状态：{originalTaskbarState}";
            Console.WriteLine(status.Text);
        };
        host.Closed += (_, _) =>
        {
            taskbar.Dispose();
            coordinator.Dispose();
            Console.WriteLine($"RESTORE taskbar={ReadTaskbarState()} original={originalTaskbarState}");
            if (ReadTaskbarState() == originalTaskbarState && !File.Exists(Path.Combine(temp, "preview-taskbar.json")))
            {
                Directory.Delete(temp, true);
            }
            app.Shutdown();
        };
        app.Run(host);
    }

    private static int ReadTaskbarState()
    {
        var data = new TaskbarNativeMethods.AppBarData { Size = (uint)Marshal.SizeOf<TaskbarNativeMethods.AppBarData>() };
        return (int)TaskbarNativeMethods.SHAppBarMessage(TaskbarNativeMethods.AbmGetState, ref data);
    }

    private static Button FindButton(DependencyObject root, string name)
    {
        if (root is Button button && AutomationProperties.GetName(button) == name) return button;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            try { return FindButton(VisualTreeHelper.GetChild(root, index), name); }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException($"找不到按钮 {name}");
    }
}
