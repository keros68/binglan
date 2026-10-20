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
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // One preview at a time: leftovers from a run that was killed are recovered
        // below, which must not touch the taskbar setting of a live run.
        using var instance = new Mutex(true, @"Local\BingLan-ShellSurfacesPreview", out var firstInstance);
        if (!firstInstance) throw new InvalidOperationException("已有一个验收窗口在运行，请先退出它");
        RecoverAbandonedRuns();
        var temp = Path.Combine(Path.GetTempPath(), $"{TempPrefix}{Guid.NewGuid():N}");
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
        state.Taskbar.RevealOnlyAtCorners = Environment.GetCommandLineArgs().Contains("--tray-corners");
        store.Save(state);
        var coordinator = new WidgetCoordinator(interactiveQa: true, dataDirectory: temp);
        var taskbar = new TaskbarAdapter(Path.Combine(temp, "preview-taskbar.json"));
        var originalTaskbarState = ReadTaskbarState();
        var controls = new WrapPanel();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
        var root = new StackPanel { Children = { controls, status } };
        var host = new Window { Title = "冰蓝系统入口验收", Left = 0, Top = 0,
            Width = SystemParameters.PrimaryScreenWidth, Height = SystemParameters.PrimaryScreenHeight,
            Content = root, ResizeMode = ResizeMode.CanMinimize };
        var mode = (originalTaskbarState & TaskbarNativeMethods.AbsAutoHide) != 0 ? "autohide" : "visible";
        var captures = 0;
        var closing = false;
        var cleanupCompleted = false;
        var captureExitCleanup = false;
        var pendingExits = 0;
        var slowUia = Environment.GetCommandLineArgs().Select(arg => arg.StartsWith(SlowUiaArg, StringComparison.Ordinal)
            && int.TryParse(arg[SlowUiaArg.Length..], out var delay) ? delay : 0).Max();
        using var overflow = new TrayOverflowController(slowUia, Console.WriteLine);

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
            button.Click += async (_, _) =>
            {
                try { await action(); }
                catch (Exception exception)
                {
                    Console.WriteLine($"CHECK FAILED {title}: {exception}");
                    status.Text = $"验收失败：{title}，{exception.Message}；可点击恢复任务栏或退出验收";
                }
            };
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
        async Task WaitForRestore(int before)
        {
            for (var check = 0; check < 30; check++)
            {
                if (ReadTaskbarState() != before) throw new InvalidOperationException("任务栏设置发生变化");
                var tray = TaskbarNativeMethods.FindWindowW("Shell_TrayWnd", null);
                if (!TrayOverflowController.IsVisible && DockNativeMethods.GetWindowRect(tray, out var rect)
                    && (((before & TaskbarNativeMethods.AbsAutoHide) == 0)
                        || rect.Top >= System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Bottom - 2)) return;
                await Task.Delay(100);
            }
            throw new InvalidOperationException("关闭面板后任务栏未恢复");
        }
        Add("原生托盘实验", async () =>
        {
            Console.WriteLine($"OVERFLOW result={await overflow.OpenAsync()} visible={TrayOverflowController.IsVisible} state={ReadTaskbarState()}");
            await Capture("overflow");
        });
        Add("顶栏托盘入口", async () =>
        {
            var topbar = app.Windows.OfType<TopBarWindow>().Single();
            FindButton(topbar, "托盘模块").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Capture("topbar-overflow");
            if (!TrayOverflowController.IsVisible) throw new InvalidOperationException("顶栏托盘面板未出现");
        });
        Add("顶栏托盘打开后退出", async () =>
        {
            var topbar = app.Windows.OfType<TopBarWindow>().Single();
            FindButton(topbar, "托盘模块").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Capture("topbar-overflow-exit");
            if (!TrayOverflowController.IsVisible) throw new InvalidOperationException("顶栏托盘面板未出现");
            Console.WriteLine($"TOPBAR-EXIT visible={TrayOverflowController.IsVisible}");
            topbar.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>()
                .Single(item => (string)item.Header == "退出冰蓝桌面")
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        });
        Add("托盘连点实验", async () =>
        {
            var results = await Task.WhenAll(overflow.OpenAsync(), overflow.OpenAsync());
            if (results.Count(result => result == "opened") != 1 || !TrayOverflowController.IsVisible)
                throw new InvalidOperationException("并发唤出未保持面板打开");
            var repeated = await overflow.OpenAsync();
            if (repeated != "already-open" || !TrayOverflowController.IsVisible)
                throw new InvalidOperationException("重复请求关闭了面板");
            Console.WriteLine($"CONCURRENT results={string.Join(',', results)} repeated={repeated} visible={TrayOverflowController.IsVisible}");
            await Capture("overflow-concurrent");
        });
        Add("托盘循环20次", async () =>
        {
            var before = ReadTaskbarState();
            host.Activate();
            await WaitForRestore(before);
            for (var cycle = 1; cycle <= 20; cycle++)
            {
                host.Activate();
                var opened = await overflow.OpenAsync();
                if (opened != "opened" || !TrayOverflowController.IsVisible)
                    throw new InvalidOperationException($"第 {cycle} 次唤出失败：{opened}");
                var repeated = await overflow.OpenAsync();
                if (repeated != "already-open" || !TrayOverflowController.IsVisible)
                    throw new InvalidOperationException($"第 {cycle} 次重复唤出失败");
                if (cycle % 2 == 0) host.Activate();
                else if (!await overflow.CloseOwnedPanelAsync())
                    throw new InvalidOperationException($"第 {cycle} 次关闭失败");
                await WaitForRestore(before);
                Console.WriteLine($"CYCLE {cycle}/20 opened=True repeated={repeated} close={(cycle % 2 == 0 ? "focus-away" : "native-button")} restored=True state={ReadTaskbarState()}");
            }
            await Capture("overflow-cycles-restored");
        });
        Add("故障恢复实验", async () =>
        {
            var before = ReadTaskbarState();
            for (var attempt = 1; attempt <= 20; attempt++)
            {
                var result = await overflow.OpenAsync(invokeButton: false);
                await WaitForRestore(before);
                if (result != "injected-failure") throw new InvalidOperationException(result);
                Console.WriteLine($"FAULT-INJECTION {attempt}/20 result={result} restored=True state={ReadTaskbarState()}");
            }
            await Capture("overflow-failure-restored");
        });
        Add("托盘保持与焦点切换", async () =>
        {
            var before = ReadTaskbarState();
            var opened = await overflow.OpenAsync();
            if (opened != "opened") throw new InvalidOperationException(opened);
            await Task.Delay(5000);
            if (!TrayOverflowController.IsVisible) throw new InvalidOperationException("面板保持期间被关闭");
            await Capture("overflow-held");
            var probe = new Window { Title = "托盘焦点切换验收", Width = 450, Height = 180,
                Content = new TextBlock { Text = "检查切换窗口后不抢回焦点", Margin = new Thickness(20) } };
            try
            {
                probe.Show();
                probe.Activate();
                var expectedForeground = new System.Windows.Interop.WindowInteropHelper(probe).Handle;
                await WaitForRestore(before);
                if (DockNativeMethods.GetForegroundWindow() != expectedForeground)
                    throw new InvalidOperationException("切换窗口后焦点被抢回");
                Console.WriteLine($"HOLD-AND-FOCUS held=True kept-new-window=True state={ReadTaskbarState()}");
            }
            finally { probe.Close(); }
        });
        Add("托盘打开后退出", async () =>
        {
            var opened = await overflow.OpenAsync();
            if (opened != "opened" || !TrayOverflowController.IsVisible)
                throw new InvalidOperationException($"退出前未打开面板：{opened}");
            Console.WriteLine($"EXIT-WHILE-OPEN result={opened} visible=True");
            await Capture("overflow-exit");
            captureExitCleanup = true;
            host.Close();
        });
        Add("唤出中退出", async () =>
        {
            var pending = overflow.OpenAsync();
            host.Close();
            Console.WriteLine($"EXIT-WHILE-OPENING result={await pending}");
        });
        Add("临时自动隐藏任务栏", async () =>
        {
            await taskbar.ApplyAsync(TaskbarMode.SmartHide);
            mode = "autohide";
            status.Text = $"任务栏状态：{ReadTaskbarState()}；关闭验收窗口时恢复原状态";
        });
        Add("恢复启动前任务栏设置", async () =>
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
            status.Text = $"Windows {Environment.OSVersion.Version}\n{string.Join("\n", MonitorCatalog.GetAll().Select(m => m.Label))}\n原任务栏状态：{originalTaskbarState}；仅两角：{state.Taskbar.RevealOnlyAtCorners}"
                + (slowUia > 0 ? $"；UIA 调用延迟：{slowUia} ms" : "");
            Console.WriteLine(status.Text);
        };
        host.Closing += async (_, args) =>
        {
            if (cleanupCompleted) return;
            args.Cancel = true;
            if (closing) return;
            closing = true;
            // Closing can finish synchronously when no panel is open. Resume
            // after this event returns before issuing the second Close request.
            await Task.Yield();
            try
            {
                var before = ReadTaskbarState();
                var result = await overflow.CloseAsync();
                if (result == "closed") result = await coordinator.CloseTrayOverflowAsync();
                if (result != "closed")
                {
                    // A UIA call that never returns cannot be aborted. After two such
                    // exits the next one gives up waiting and says so in the log.
                    if (result.EndsWith("-pending", StringComparison.Ordinal) && ++pendingExits > 2)
                    {
                        Console.WriteLine($"EXIT-ABANDONED reason={result} visible={TrayOverflowController.IsVisible}");
                        cleanupCompleted = true;
                        host.Close();
                        return;
                    }
                    throw new InvalidOperationException(pendingExits == 2
                        ? $"托盘清理未完成（{result}），原生调用仍未返回；再次退出将放弃等待，请随后检查托盘浮层"
                        : $"托盘清理未完成（{result}）");
                }
                await WaitForRestore(before);
                if (captureExitCleanup) await Capture("overflow-exit-cleaned");
                Console.WriteLine($"EXIT-CLEANUP visible={TrayOverflowController.IsVisible} state={ReadTaskbarState()}");
                cleanupCompleted = true;
                host.Close();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"CHECK FAILED EXIT-CLEANUP: {exception}");
                status.Text = $"退出清理失败：{exception.Message}；可恢复任务栏后重试退出";
                closing = false;
            }
        };
        host.Closed += async (_, _) =>
        {
            overflow.Dispose();
            taskbar.Dispose();
            coordinator.Dispose();
            await Task.Delay(300);
            Console.WriteLine($"RESTORE taskbar={ReadTaskbarState()} original={originalTaskbarState} overflow-visible={TrayOverflowController.IsVisible}");
            if (ReadTaskbarState() == originalTaskbarState && !File.Exists(Path.Combine(temp, "preview-taskbar.json")))
            {
                Directory.Delete(temp, true);
            }
            app.Shutdown();
        };
        app.Run(host);
        Console.WriteLine($"PREVIEW-END taskbar={ReadTaskbarState()} original={originalTaskbarState} overflow-visible={TrayOverflowController.IsVisible}");
    }

    private const string TempPrefix = "BingLan-shell-surfaces-";
    private const string SlowUiaArg = "--tray-slow-uia=";

    /// <summary>
    /// Restores the taskbar setting left by a run that was killed or crashed, before
    /// this run records the "original" state. Each run keeps its checkpoint in its
    /// own temporary directory.
    /// </summary>
    private static void RecoverAbandonedRuns()
    {
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        foreach (var directory in Directory.EnumerateDirectories(tempRoot, TempPrefix + "*"))
        {
            var fullPath = Path.GetFullPath(directory);
            if (!string.Equals(Path.GetDirectoryName(fullPath), tempRoot, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(fullPath)[TempPrefix.Length..], "N", out _)
                || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0) continue;
            var checkpoint = Path.Combine(directory, "preview-taskbar.json");
            var hadCheckpoint = File.Exists(checkpoint);
            if (!TaskbarAdapter.RecoverFromCheckpoint(checkpoint))
                throw new InvalidOperationException(
                    $"上次验收异常结束后未能恢复任务栏设置，请在 Windows 设置中检查“自动隐藏任务栏”后重试（{directory}）");
            if (hadCheckpoint) Console.WriteLine($"RECOVERED previous-run={directory} taskbar={ReadTaskbarState()}");
            try { Directory.Delete(directory, true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
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
