using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BingLan.App;
using BingLan.App.Windows;
using BingLan.Core.Dock;
using BingLan.Core.Services;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

internal static class LauncherPreviewCapture
{
    // Render only the loaded WPF surfaces. Desktop pixels can include other apps
    // that are brought in front while an interactive test is running.
    internal static void Run(Application app, Action<Window> show, Action pump, Action<Func<bool>, string> until)
    {
        var state = Environment.GetEnvironmentVariable("BINGLAN_PREVIEW_STATE");
        var output = Environment.GetEnvironmentVariable("BINGLAN_PREVIEW_CAPTURES");
        if (state is null || output is null || !File.Exists(Path.Combine(state, "widgets.json")))
        {
            throw new InvalidOperationException("预览截图需要独立的样例配置和输出目录");
        }

        var tray = SystemParameters.WorkArea;
        var width = Math.Min(tray.Width, 1320);
        var host = new Window
        {
            Title = "冰蓝预览取景",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Left = tray.Left + (tray.Width - width) / 2,
            Top = tray.Top + 76,
            Width = width,
            Height = Math.Min(tray.Height - 76, 960),
            Background = new LinearGradientBrush(
                Color.FromRgb(27, 48, 79), Color.FromRgb(93, 181, 200), new Point(0, 0.7), new Point(1, 0.2))
        };

        using var coordinator = new WidgetCoordinator(interactiveQa: true, dataDirectory: state);
        show(host);
        try
        {
            coordinator.Start();
            TopBarWindowTests.ForceForeground(new WindowInteropHelper(host).Handle);
            host.Activate();
            pump();
            coordinator.ToggleLauncherPanel();
            LauncherPanelWindow? panel = null;
            until(() =>
            {
                panel = app.Windows.OfType<LauncherPanelWindow>().FirstOrDefault(window => window.IsVisible);
                return panel is { VisibleEntries.Count: > 0 };
            }, "启动面板预览未载入");
            var grid = (ListBox)panel!.FindName("ResultsList");
            var renderDeadline = DateTime.UtcNow.AddSeconds(3);
            until(() => CountLoadedIcons(grid) >= Math.Min(panel.VisibleEntries.Count, 16) || DateTime.UtcNow >= renderDeadline,
                "等待面板首屏图标");
            Console.WriteLine($"启动面板已载入 {CountLoadedIcons(grid)} 个系统图标，包含视口附近项目");
            pump();
            var panelSurface = (Border)panel.FindName("PanelSurface");
            SaveSurface(panelSurface, Path.Combine(output, "启动面板.png"), GlassTint(panelSurface, "FloatingGlassPanelBrush"));
            VerifyBrowseModes(panel, output, pump, until);
            VerifyLauncherSend(app, panel, grid, pump, until);
            VerifyLauncherOrganizer(panel, grid, state, output, pump, until);
            panel.Close();
            pump();

            VerifyDockSend(app, pump, until);
            var dock = app.Windows.OfType<IceBlueDockWindow>().Single();
            var row = (Panel)dock.FindName("ItemPanel");
            var commands = row.Children.OfType<Button>().Take(3).ToArray();
            if (!commands.Select(AutomationProperties.GetName)
                .SequenceEqual(["打开 Windows 开始菜单", "打开冰蓝总览卡", "显示桌面"]) || row.Children[3] is not Border)
            {
                throw new InvalidOperationException("三个功能入口应集中在左侧，与应用由一条分隔线隔开");
            }
            if (!Equals(commands[1].ToolTip, "打开冰蓝总览卡") || panel.Title != "冰蓝总览卡")
            {
                throw new InvalidOperationException("Dock 提示与启动面板窗口应使用同一个产品名称");
            }
            var icons = commands.Select(button => ((Grid)((StackPanel)button.Content).Children[0])
                .Children.OfType<Viewbox>().Single()).ToArray();
            if (commands.Any(button => button.Width != commands[0].Width || button.Height != commands[0].Height)
                || icons.Any(icon => icon.Width != icons[0].Width || icon.Height != icons[0].Height))
            {
                throw new InvalidOperationException("Dock 功能图标和点击区应分别采用统一尺寸");
            }
            var dockSurface = (Border)dock.FindName("DockSurface");
            SaveSurface(dockSurface, Path.Combine(output, "Dock.png"), GlassTint(dockSurface, "DockSurfaceBrush"));
            VerifyLauncherAppearance(app, coordinator, host, output, pump, until);
            TopBarWindow? topBar = null;
            until(() => (topBar = app.Windows.OfType<TopBarWindow>().SingleOrDefault()) is { IsLoaded: true },
                "普通顶栏未载入");
            if (((FrameworkElement)topBar!.FindName("CapsuleHeader")).Visibility != Visibility.Collapsed)
            {
                throw new InvalidOperationException("当前预览应使用普通顶栏");
            }
            Console.WriteLine("通过：保存过胶囊布局的配置打开普通顶栏，设置中无胶囊入口");
            VerifyRemovedSendTarget(app, coordinator, state, until);
        }
        finally
        {
            foreach (var window in app.Windows.OfType<LauncherPanelWindow>().ToArray())
            {
                window.Close();
            }
            foreach (var window in app.Windows.OfType<SettingsWindow>().ToArray())
            {
                window.Close();
            }
            host.Close();
        }
    }

    private static void VerifyBrowseModes(LauncherPanelWindow panel, string output, Action pump,
        Action<Func<bool>, string> until)
    {
        var navigation = (ScrollViewer)panel.FindName("BrowseNavigation");
        var purposes = (ItemsControl)panel.FindName("PurposeList");
        purposes.UpdateLayout();
        var button = Visuals<Button>(purposes).FirstOrDefault(candidate =>
            AutomationProperties.GetName(candidate) is { Length: > 0 } name && name != "辅助入口");
        if (button is null) throw new InvalidOperationException("实机应用列表应有可预览的用途分类");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        until(() => panel.VisibleEntries.Count > 0, "用途分类未显示应用");
        var grid = (ListBox)panel.FindName("ResultsList");
        var deadline = DateTime.UtcNow.AddSeconds(2);
        until(() => CountLoadedIcons(grid) > 0 || DateTime.UtcNow >= deadline, "等待分类图标");
        pump();
        if (!navigation.IsVisible) throw new InvalidOperationException("分类浏览时应显示左侧导航");
        var surface = (Border)panel.FindName("PanelSurface");
        SaveSurface(surface, Path.Combine(output, "启动面板-用途分类.png"), GlassTint(surface, "FloatingGlassPanelBrush"));

        var entry = panel.VisibleEntries[0];
        var searchScope = (FrameworkElement)panel.FindName("SearchScope");
        var search = (TextBox)panel.FindName("SearchBox");
        search.Text = entry.Candidate.DisplayName;
        until(() => panel.VisibleEntries.Any(candidate => candidate.Key == entry.Key)
            && !navigation.IsVisible && searchScope.IsVisible, "全局搜索未切换到简洁结果界面");
        pump();
        SaveSurface(surface, Path.Combine(output, "启动面板-搜索.png"), GlassTint(surface, "FloatingGlassPanelBrush"));
        search.Text = "";
        until(() => navigation.IsVisible, "清空搜索后分类导航未恢复");
        ((Button)panel.FindName("OverviewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        pump();
        Console.WriteLine("通过：应用概览、用途分类与搜索状态切换");
    }

    private static void VerifyRemovedSendTarget(Application app, WidgetCoordinator coordinator,
        string dataDirectory, Action<Func<bool>, string> until)
    {
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var coordinatorType = typeof(WidgetCoordinator);
        var existingBoxes = app.Windows.OfType<FileBoxWindow>().ToHashSet();
        coordinatorType.GetMethod("AddFileBox", privateInstance)!.Invoke(coordinator, [null]);
        var target = app.Windows.OfType<FileBoxWindow>().Single(box => !existingBoxes.Contains(box));
        var sample = Path.Combine(dataDirectory, $"发送途中删除目标-{Guid.NewGuid():N}.txt");
        File.WriteAllText(sample, "分组盒只保存路径映射");
        var contents = File.ReadAllBytes(sample);

        // Run both calls on the same dispatcher turn, before the send can resume from file probing.
        var pending = (Task<string>)coordinatorType.GetMethod("SendLauncherPathToFileBoxAsync", privateInstance)!
            .Invoke(coordinator, [target.State.Id, sample])!;
        if (pending.IsCompleted)
        {
            throw new InvalidOperationException("发送已结束，未建立途中删除的测试条件");
        }
        coordinatorType.GetMethod("RemoveWidget", privateInstance)!.Invoke(coordinator, [target]);
        until(() => pending.IsCompleted, "删除目标分组盒后发送未结束");
        if (pending.GetAwaiter().GetResult() != "该文件收纳盒已移除。"
            || target.State.Items.Count != 0
            || new LocalStateStore(dataDirectory).Load().FileBoxes.Any(box => box.Id == target.State.Id)
            || !File.ReadAllBytes(sample).SequenceEqual(contents))
        {
            throw new InvalidOperationException("删除目标后应结束发送、保持删盒结果并保留原文件");
        }
        Console.WriteLine("通过：发送途中删除目标分组盒，提示已移除、未追加映射、原文件未变");
    }

    private static void VerifyLauncherAppearance(Application app, WidgetCoordinator coordinator, Window host,
        string output, Action pump, Action<Func<bool>, string> until)
    {
        typeof(WidgetCoordinator).GetMethod("OpenSettings", BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, types: Type.EmptyTypes, modifiers: null)!.Invoke(coordinator, null);
        var settings = app.Windows.OfType<SettingsWindow>().Single();
        if (settings.FindName("TopBarDisplayModeComboBox") is not null)
        {
            throw new InvalidOperationException("当前预览的设置页不应包含胶囊入口");
        }
        VerifyDockCommandSettings(app.Windows.OfType<IceBlueDockWindow>().Single(), settings, until);
        var follow = (RadioButton)settings.FindName("LauncherFollowLookRadio");
        var custom = (RadioButton)settings.FindName("LauncherCustomLookRadio");
        if (follow.IsChecked != true) throw new InvalidOperationException("默认应跟随卡片外观");
        custom.IsChecked = true;
        ((Slider)settings.FindName("LauncherOpacitySlider")).Value = 0.31;
        ((Slider)settings.FindName("LauncherCornerRadiusSlider")).Value = 8;
        var editor = (TextBox)settings.FindName("LauncherColorEditor");
        editor.Text = "#BEEEDA";
        editor.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
        settings.Close();
        TopBarWindowTests.ForceForeground(new WindowInteropHelper(host).Handle);
        host.Activate();
        until(() => host.IsActive, "外观验收窗口未恢复前台");
        coordinator.ToggleLauncherPanel();
        if (app.Windows.OfType<LauncherPanelWindow>().FirstOrDefault(window => window.IsVisible) is { } opening)
        {
            TopBarWindowTests.ForceForeground(new WindowInteropHelper(opening).Handle);
            opening.Activate();
        }
        LauncherPanelWindow? panel = null;
        until(() =>
        {
            panel = app.Windows.OfType<LauncherPanelWindow>().FirstOrDefault(window => window.IsVisible);
            return panel is { VisibleEntries.Count: > 0 };
        }, "修改外观后启动面板未打开");
        var surface = (Border)panel!.FindName("PanelSurface");
        if (surface.CornerRadius.TopLeft != 8
            || panel.FindResource("FloatingGlassPanelBrush") is not SolidColorBrush { Color: { A: 79, R: 190, G: 238, B: 218 } })
        {
            throw new InvalidOperationException("设置中的底色、玻璃浓度和圆角没有应用到启动面板");
        }
        var grid = (ListBox)panel.FindName("ResultsList");
        var renderDeadline = DateTime.UtcNow.AddSeconds(3);
        until(() => CountLoadedIcons(grid) >= grid.Items.Count || DateTime.UtcNow >= renderDeadline, "启动面板图标未载入");
        SaveSurface(surface, Path.Combine(output, "启动面板-自定义外观.png"), GlassTint(surface, "FloatingGlassPanelBrush"));
        var material = (Button)panel.FindName("MaterialButton");
        material.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        pump();
        if (surface.Background is not SolidColorBrush { Color.A: > 1 and < 255 })
        {
            throw new InvalidOperationException("切换清透材质后应使用无模糊的半透明底色");
        }
        SaveSurface(surface, Path.Combine(output, "启动面板-清透.png"), GlassTint(surface, "FloatingGlassPanelBrush"));
        SaveSurface(surface, Path.Combine(output, "启动面板-明暗与纹理对照.png"),
            System.Windows.Media.Brushes.Transparent, includeContrastBackdrop: true);
        panel.Dismiss(restoreForeground: true);
        until(() => !panel.IsVisible, "启动面板收起动画未结束");
        Console.WriteLine("通过：自定义底色、玻璃浓度、圆角，切换清透材质并收起");
    }

    private static void VerifyDockCommandSettings(IceBlueDockWindow dock, SettingsWindow settings,
        Action<Func<bool>, string> until)
    {
        var row = (Panel)dock.FindName("ItemPanel");
        var entries = new[]
        {
            (Name: "打开 Windows 开始菜单", Switch: "DockStartMenuButtonCheckBox"),
            (Name: "打开冰蓝总览卡", Switch: "DockLauncherButtonCheckBox"),
            (Name: "显示桌面", Switch: "DockShowDesktopButtonCheckBox")
        };
        foreach (var (name, control) in entries)
        {
            var toggle = (CheckBox)settings.FindName(control);
            toggle.IsChecked = false;
            until(() => !Visible(name), $"关闭开关后仍显示{name}");
            if (entries.Any(entry => entry.Name != name && !Visible(entry.Name)))
            {
                throw new InvalidOperationException("隐藏一个入口不应影响其他入口");
            }
            toggle.IsChecked = true;
            until(() => Visible(name), $"开启开关后仍未显示{name}");
        }
        Console.WriteLine("通过：三个 Dock 功能入口可独立隐藏和显示");

        bool Visible(string name) => row.Children.OfType<Button>()
            .Any(button => button.Tag is null && AutomationProperties.GetName(button) == name);
    }

    private static Brush? GlassTint(Border surface, string key)
    {
        var glassActive = surface.Background is SolidColorBrush { Color.A: 1 };
        var clear = surface.Background is SolidColorBrush { Color.A: > 1 and < 255 };
        Console.WriteLine($"{key} 使用{(glassActive ? "原生玻璃" : clear ? "清透玻璃" : "WPF 回退")}");
        return glassActive ? surface.TryFindResource(key) as Brush : clear ? System.Windows.Media.Brushes.Transparent : null;
    }

    private static int CountLoadedIcons(DependencyObject element)
    {
        var count = element is Image { Source: not null } ? 1 : 0;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            count += CountLoadedIcons(VisualTreeHelper.GetChild(element, index));
        }
        return count;
    }

    private static void SaveSurface(Border surface, string output, Brush? tint = null, bool includeContrastBackdrop = false)
    {
        var bounds = new Rect(0, 0, surface.ActualWidth, surface.ActualHeight);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            if (tint is not null)
            {
                var radius = surface.CornerRadius.TopLeft;
                if (includeContrastBackdrop)
                {
                    var light = new SolidColorBrush(Color.FromRgb(238, 241, 247));
                    var dark = new SolidColorBrush(Color.FromRgb(12, 19, 30));
                    var mid = new SolidColorBrush(Color.FromRgb(72, 112, 152));
                    drawing.PushClip(new RectangleGeometry(bounds, radius, radius));
                    drawing.DrawRectangle(dark, null, bounds);
                    drawing.DrawRectangle(light, null, new Rect(bounds.Width / 3, 0, bounds.Width / 3, bounds.Height));
                    for (var y = 0; y < bounds.Height; y += 12)
                    {
                        for (var x = (int)(bounds.Width * 2 / 3); x < bounds.Width; x += 18)
                        {
                            drawing.DrawRectangle((x / 18 + y / 12) % 2 == 0 ? light : mid, null, new Rect(x, y, 18, 12));
                        }
                    }
                    drawing.Pop();
                }
                else
                {
                    drawing.DrawRoundedRectangle(new LinearGradientBrush(Color.FromRgb(40, 63, 89),
                        Color.FromRgb(129, 165, 178), new Point(0, 0.7), new Point(1, 0.2)), null, bounds, radius, radius);
                }
                drawing.DrawRoundedRectangle(tint, null, bounds, radius, radius);
            }
            drawing.DrawRectangle(new VisualBrush(surface), null, bounds);
        }
        var image = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height),
            96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var stream = File.Create(output);
        encoder.Save(stream);
    }

    private static void VerifyLauncherSend(Application app, LauncherPanelWindow panel, ListBox grid,
        Action pump, Action<Func<bool>, string> until)
    {
        var box = app.Windows.OfType<FileBoxWindow>().Single(window => window.State.Title == "预览验收盒");
        var search = (TextBox)panel.FindName("SearchBox");
        search.Text = "冰蓝预览-面板验收";
        string? path = null;
        until(() =>
        {
            var index = panel.VisibleEntries.ToList().FindIndex(entry =>
                entry.Candidate.Path.EndsWith("-面板验收.txt", StringComparison.Ordinal));
            if (index < 0) return false;
            path = panel.VisibleEntries[index].Candidate.Path;
            SelectResult(grid, panel.VisibleEntries[index]);
            return true;
        }, "启动面板未找到发送样例");
        var original = File.ReadAllBytes(path!);
        var field = typeof(LauncherPanelWindow).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            panel.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel)!,
                Environment.TickCount, Key.Apps) { RoutedEvent = UIElement.PreviewKeyDownEvent });
            ContextMenu? menu = null;
            until(() => (menu = field.GetValue(panel) as ContextMenu)?.IsOpen == true, "启动面板条目菜单未打开");
            var send = menu!.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "发送到文件收纳盒");
            send.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == box.State.Title)
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            until(() => ((TextBlock)panel.FindName("StatusText")).Text.Contains(attempt == 0 ? "已新增" : "已经在文件收纳盒"),
                "启动面板发送未返回预期结果");
            menu.IsOpen = false;
            pump();
        }
        if (box.State.Items.Count(item => item.Path == path) != 1 || !File.ReadAllBytes(path!).SequenceEqual(original))
        {
            throw new InvalidOperationException("启动面板重复发送未去重，或原文件发生变化");
        }
        Console.WriteLine("通过：启动面板右键发送、重复去重与原文件校验");
    }

    private static void VerifyLauncherOrganizer(LauncherPanelWindow panel, ListBox grid, string state, string output,
        Action pump, Action<Func<bool>, string> until)
    {
        var index = panel.VisibleEntries.ToList().FindIndex(entry =>
            entry.Candidate.Path.EndsWith("-面板验收.txt", StringComparison.Ordinal));
        if (index < 0) throw new InvalidOperationException("找不到待固定的样例文件");
        var entry = panel.VisibleEntries[index];
        SelectResult(grid, entry);
        var menu = OpenEntryMenu();
        menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "固定到 Dock"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        until(() => ((TextBlock)panel.FindName("StatusText")).Text == "已固定到 Dock", "右键固定未完成");

        SelectResult(grid, entry);
        menu = OpenEntryMenu();
        var groups = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "加入面板分组"));
        groups.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "新建分组…"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        var editor = (TextBox)panel.FindName("GroupNameEditor");
        until(() => editor.IsVisible && editor.IsKeyboardFocused, "新建分组输入框未获得焦点");
        editor.Text = "预览工作区";
        ((Button)panel.FindName("SaveGroupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var store = new LocalStateStore(state);
        until(() =>
        {
            var saved = store.Load();
            return saved.Dock.PinnedApps.Any(pin => pin.TargetPath == entry.Candidate.Path)
                && saved.LauncherGroups.Any(group => group.Name == "预览工作区"
                    && group.Members.Any(member => member.Key == entry.Key));
        }, "固定入口与分组未持久化");
        var surface = (Border)panel.FindName("PanelSurface");
        SaveSurface(surface, Path.Combine(output, "启动面板-分组与搜索.png"), GlassTint(surface, "FloatingGlassPanelBrush"));
        Console.WriteLine("通过：右键固定到真实 Dock、创建面板分组并保存");

        ContextMenu OpenEntryMenu()
        {
            panel.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel)!,
                Environment.TickCount, Key.Apps) { RoutedEvent = UIElement.PreviewKeyDownEvent });
            pump();
            return typeof(LauncherPanelWindow).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(panel) as ContextMenu ?? throw new InvalidOperationException("启动面板条目菜单未打开");
        }
    }

    private static void SelectResult(ListBox grid, LauncherPanelEntry entry)
    {
        var row = grid.Items.Cast<object>().Single(item =>
            (item.GetType().GetProperty("Entry")?.GetValue(item) as LauncherPanelEntry)?.Key == entry.Key);
        grid.SelectedItem = row;
        grid.ScrollIntoView(row);
    }

    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Visuals<T>(child)) yield return descendant;
        }
    }

    private static void VerifyDockSend(Application app, Action pump, Action<Func<bool>, string> until)
    {
        var dock = app.Windows.OfType<IceBlueDockWindow>().Single();
        var box = app.Windows.OfType<FileBoxWindow>().Single(window => window.State.Title == "预览验收盒");
        var row = (Panel)dock.FindName("ItemPanel");
        Button? button = null;
        until(() =>
        {
            button = row.Children.OfType<Button>().FirstOrDefault(candidate =>
                candidate.Tag is DockItem item && item.Pinned?.TargetPath?.EndsWith("-Dock验收.txt", StringComparison.Ordinal) == true);
            return button is not null;
        }, "Dock 验收固定项未载入");
        var target = ((DockItem)button!.Tag).Pinned!.TargetPath!;
        var original = File.ReadAllBytes(target);
        if (box.State.Items.Any(item => item.Path == target))
        {
            throw new InvalidOperationException("Dock 验收须使用尚未映射的样例文件");
        }

        var popupField = typeof(IceBlueDockWindow).GetField("_openMenu", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var itemsField = typeof(NonActivatingPopupMenu).GetField("_itemsPanel", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void ClickMenu(string label)
        {
            NonActivatingPopupMenu? popup = null;
            until(() => (popup = popupField.GetValue(dock) as NonActivatingPopupMenu)?.IsOpen == true, "Dock 右键菜单未打开");
            var items = (Panel)itemsField.GetValue(popup)!;
            var menuRow = items.Children.OfType<Border>().Single(item =>
                System.Windows.Automation.AutomationProperties.GetName(item) == label);
            menuRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent
            });
            pump();
        }
        button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        {
            RoutedEvent = UIElement.MouseRightButtonUpEvent
        });
        pump();
        ClickMenu("发送到文件收纳盒…");
        ClickMenu(box.State.Title);
        until(() => box.State.Items.Count(item => item.Path == target) == 1, "Dock 发送后缺少文件映射");
        if (!File.ReadAllBytes(target).SequenceEqual(original))
        {
            throw new InvalidOperationException("发送操作改变了原文件");
        }
        Console.WriteLine("通过：Dock 右键发送、分组盒映射与原文件校验");
    }
}
