using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BingLan.App.Dock;
using BingLan.App.Windows;
using BingLan.Core.Models;
using BingLan.Core.TopBar;

internal static class LauncherPanelWindowTests
{
    internal static void Run(Action<Window> show, Action pump, Action<Func<bool>, string> until)
    {
        // A user's click grants the launcher foreground ownership. A normal test
        // window supplies the same precondition before the tool window opens.
        var host = new Window { Title = "冰蓝启动面板验收", Width = 280, Height = 120 };
        show(host);
        void ShowFromUser(Window window)
        {
            if (window is LauncherPanelWindow)
            {
                TopBarWindowTests.ForceForeground(new WindowInteropHelper(host).Handle);
                host.Activate();
                until(() => host.IsActive, "启动面板验收窗口应先取得前台");
                window.Owner = host;
                window.Loaded += (_, _) =>
                {
                    TopBarWindowTests.ForceForeground(new WindowInteropHelper(window).Handle);
                    window.Activate();
                };
            }
            show(window);
        }
        try
        {
            BrowseAndSearch(ShowFromUser, pump, until);
            GroupsGlobalSearchAndDockPin(ShowFromUser, pump, until);
            PurposeCategoriesAndOverrides(ShowFromUser, pump, until);
            ContinuousGridAndIcons(ShowFromUser, pump, until);
            SendToFileBox(ShowFromUser, pump, until);
            MissingAndValidTargets(ShowFromUser, pump, until);
            ClosedLoadCannotRepopulate(ShowFromUser, pump, until);
        }
        finally
        {
            host.Close();
        }
    }

    private static void GroupsGlobalSearchAndDockPin(Action<Window> show, Action pump,
        Action<Func<bool>, string> until)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var executable = Environment.ProcessPath!;
        var work = new LauncherPanelEntry(
            new TopBarSearchCandidate(TopBarSearchItemKind.App, "办公工具" + suffix, "work.exe", executable),
            "开始菜单", new DockPinnedApp
            {
                DisplayName = "办公工具" + suffix, ExecutablePath = executable,
                Arguments = "--workspace work", WorkingDirectory = AppContext.BaseDirectory
            });
        var game = work with
        {
            Candidate = new TopBarSearchCandidate(TopBarSearchItemKind.App, "游戏工具" + suffix, "game.exe", executable),
            Pinned = new DockPinnedApp
            {
                DisplayName = "游戏工具" + suffix, ExecutablePath = executable,
                Arguments = "--workspace game", WorkingDirectory = AppContext.BaseDirectory
            }
        };
        var snapshot = new LauncherPanelSnapshot([work, game], [], false);
        var groups = new List<LauncherGroupState>();
        LauncherPanelEntry? pinned = null;
        var saves = 0;
        var panel = new LauncherPanelWindow(() => Task.FromResult(snapshot),
            (_, _) => throw new InvalidOperationException("整理入口不能启动项目"), () => [],
            (_, _) => Task.FromResult("完成"), 0, MonitorCatalog.GetAll().First(),
            pinToDock: entry =>
            {
                pinned = entry;
                return "已固定到 Dock";
            }, readGroups: () => groups, saveOrganizer: () => saves++);
        try
        {
            show(panel);
            until(() => panel.VisibleEntries.Count == 2, "分组测试候选未载入");
            CreateGroup("测试项目");
            SelectCategory(panel, "全部");
            until(() => panel.VisibleEntries.Count == 2, "全部分类未恢复候选");
            SelectEntry(work);
            AddToGroup("测试项目");
            until(() => groups.Single(group => group.Name == "测试项目").Members.Count == 1,
                "右键加入面板分组未保存关联");
            CreateGroup("测试常用");
            SelectCategory(panel, "全部");
            until(() => panel.VisibleEntries.Count == 2, "新建第二组后全部候选未恢复");
            SelectEntry(work);
            AddToGroup("测试常用");
            until(() => groups.All(group => group.Members.Count == 1), "同一入口未能保留两个分组关联");
            Assert(groups.Select(group => group.Members.Single().Key).Distinct().Count() == 1 && saves >= 4,
                "多个分组应关联同一个入口并通知保存");

            SelectCategory(panel, "测试项目");
            until(() => panel.VisibleEntries.Count == 1, "自建分组应只显示已加入的项目");
            var search = Require<TextBox>(panel, "SearchBox");
            search.Text = game.Candidate.DisplayName;
            until(() => panel.VisibleEntries.Any(entry => entry.Candidate.DisplayName == game.Candidate.DisplayName),
                "默认搜索应跨当前分组找到游戏入口");
            var currentOnly = Require<CheckBox>(panel, "SearchCurrentCategory");
            currentOnly.IsChecked = true;
            currentOnly.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            until(() => panel.VisibleEntries.Count == 0, "仅搜当前分组应排除其他分组的入口");
            currentOnly.IsChecked = false;
            currentOnly.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            search.Text = "";
            until(() => panel.VisibleEntries.Count == 1 && panel.VisibleEntries[0] == work,
                "清空查询应回到当前分组");

            Press(panel, Key.Apps);
            pump();
            var menu = OpenMenu(panel);
            menu.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "固定到 Dock")
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            until(() => pinned is not null, "直接固定到 Dock 未调用固定操作");
            Assert(pinned?.Pinned is { } pinnedApp && pinnedApp.Arguments == work.Pinned!.Arguments
                && pinnedApp.WorkingDirectory == work.Pinned.WorkingDirectory,
                "固定到 Dock 应保留程序启动参数与工作目录");
            menu.IsOpen = false;

            Press(panel, Key.Apps);
            pump();
            menu = OpenMenu(panel);
            menu.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "移出当前面板分组")
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            until(() => groups.Single(group => group.Name == "测试项目").Members.Count == 0,
                "移出当前组应删除这一组的关联");
            Assert(groups.Single(group => group.Name == "测试常用").Members.Count == 1,
                "移出当前分组应保留其他分组关联");
            menu.IsOpen = false;
        }
        finally { panel.Close(); }

        void CreateGroup(string name)
        {
            Click(panel, "NewGroupButton");
            Require<TextBox>(panel, "GroupNameEditor").Text = name;
            Click(panel, "SaveGroupButton");
            until(() => groups.Any(group => group.Name == name), "新建面板分组未保存");
        }

        void SelectEntry(LauncherPanelEntry entry)
        {
            SelectResult(panel, entry);
        }

        void AddToGroup(string name)
        {
            Press(panel, Key.Apps);
            pump();
            var menu = OpenMenu(panel);
            menu.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "加入面板分组")
                .Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == name)
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            pump();
        }
    }

    private static void BrowseAndSearch(Action<Window> show, Action pump, Action<Func<bool>, string> until)
    {
        var name = "LauncherRegression" + Guid.NewGuid().ToString("N");
        var executable = Environment.ProcessPath!;
        var first = new LauncherPanelEntry(new TopBarSearchCandidate(TopBarSearchItemKind.App, name, "launcher.exe", executable), "Dock",
            new DockPinnedApp { DisplayName = name, ExecutablePath = executable, Arguments = "--profile work" });
        var second = first with { Pinned = new DockPinnedApp { DisplayName = name, ExecutablePath = executable, Arguments = "--profile Work" } };
        var file = new LauncherPanelEntry(new TopBarSearchCandidate(TopBarSearchItemKind.File, name, "launcher.txt", Path.Combine(Path.GetTempPath(), name + ".txt")), "分组盒");
        var snapshot = new LauncherPanelSnapshot([first, first with { Source = "开始菜单" }, second, file], [first, second, file], false);
        var opened = new List<LauncherPanelEntry>();
        var panel = Create(() => Task.FromResult(snapshot), (entry, _) => opened.Add(entry));
        try
        {
            show(panel);
            until(() => panel.VisibleEntries.Count == 2, "常用列表未载入，或参数大小写不同的固定项被合并");
            Assert(panel.VisibleEntries[0].Pinned?.Arguments == "--profile work"
                && panel.VisibleEntries[1].Pinned?.Arguments == "--profile Work", "常用项目应保留 Dock 顺序和完整参数");
            var list = Require<ListBox>(panel, "ResultsList");
            Assert(SelectedEntry(list)?.Key == first.Key, "初次结果应选择首个入口");
            var sources = Require<ComboBox>(panel, "SourceFilter");
            sources.SelectedItem = sources.Items.Cast<object>().Single(item => OptionName(item) == "开始菜单");
            until(() => panel.VisibleEntries.Count == 1, "合并后的常用入口应保留开始菜单来源");
            Assert(panel.VisibleEntries[0].Pinned?.Arguments == "--profile work",
                "按其他来源筛选时应保留固定项的启动参数");
            sources.SelectedIndex = 0;
            until(() => panel.VisibleEntries.Count == 2, "清除来源筛选应恢复全部应用");
            Click(panel, "FilesFilter");
            until(() => panel.VisibleEntries.Count == 1, "文件页只显示文件，常用应用应遵守类型筛选");
            Assert(panel.VisibleEntries.Single().Candidate.Kind == TopBarSearchItemKind.File,
                "固定文件应保留在文件页");
            var search = Require<TextBox>(panel, "SearchBox");
            search.Text = name;
            until(() => Require<Button>(panel, "AllFilter").IsVisible && panel.VisibleEntries.Count == 3,
                "输入查询应默认跨应用和文件搜索并隐藏重复常用项目");
            Assert(SelectedEntry(list)?.Key == first.Key, "新查询应选择首个匹配入口");
            Click(panel, "AppsFilter");
            until(() => panel.VisibleEntries.Count == 2, "应用筛选应保留不同启动参数");
            Press(panel, Key.Apps);
            pump();
            var menu = (ContextMenu?)typeof(LauncherPanelWindow).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel);
            Assert(menu?.IsOpen == true, "键盘菜单键应打开所选项目菜单");
            Assert(!menu!.Items.OfType<MenuItem>().Any(item => item.Header?.ToString() == "发送到分组盒"), "带参数应用不可发送为会丢失参数的文件映射");
            Press(panel, Key.Escape);
            pump();
            Assert(panel.IsVisible && !menu.IsOpen, "菜单内 Escape 应只关闭菜单");
            search.Text = "";
            until(() => panel.VisibleEntries.Count == 1 && !Require<Button>(panel, "AllFilter").IsVisible, "清空查询应恢复文件浏览");
            Assert(opened.Count == 0, "筛选和右键菜单不能启动项目");
        }
        finally { panel.Close(); }
    }

    private static void PurposeCategoriesAndOverrides(Action<Window> show, Action pump,
        Action<Func<bool>, string> until)
    {
        var suffix = Guid.NewGuid().ToString("N");
        LauncherPanelEntry App(string name, string executable) => new(
            new TopBarSearchCandidate(TopBarSearchItemKind.App, name + suffix, executable,
                Path.Combine(Path.GetTempPath(), "BingLan-purpose", executable)), "开始菜单",
            new DockPinnedApp { DisplayName = name + suffix, ExecutablePath = Path.Combine(Path.GetTempPath(), "BingLan-purpose", executable) });
        var office = App("Word", "winword.exe");
        var maps = App("QGIS", "qgis-bin.exe");
        var unknown = App("项目工具", "unknown-purpose.exe");
        var auxiliary = App("安装程序", "setup.exe");
        var file = new LauncherPanelEntry(new TopBarSearchCandidate(TopBarSearchItemKind.File,
            "项目资料" + suffix, "report.txt", Path.Combine(Path.GetTempPath(), "report-" + suffix + ".txt")), "桌面");
        var mixed = new LauncherGroupState { Name = "软件与资料" };
        LauncherGroupRules.TryAdd(mixed, new LauncherGroupMember(office.Key));
        LauncherGroupRules.TryAdd(mixed, new LauncherGroupMember(file.Key));
        var groups = new List<LauncherGroupState> { mixed };
        var overrides = new Dictionary<string, LauncherAppCategory>(StringComparer.Ordinal);
        var saves = 0;
        var snapshot = new LauncherPanelSnapshot([office, maps, unknown, auxiliary, file], [office], false);
        LauncherPanelWindow NewPanel() => new(() => Task.FromResult(snapshot),
            (_, _) => throw new InvalidOperationException("分类整理不能启动项目"), () => [],
            (_, _) => Task.FromResult("完成"), 0, MonitorCatalog.GetAll().First(),
            readGroups: () => groups, saveOrganizer: () => saves++, readCategoryOverrides: () => overrides);
        var panel = NewPanel();
        try
        {
            show(panel);
            until(() => panel.VisibleEntries.Count == 3, "概览应包含常用与正常应用，并收起辅助入口");
            Assert(panel.VisibleEntries.Count(entry => entry.Key == office.Key) == 1, "概览中的常用应用应只出现一次");
            Assert(Require<ScrollViewer>(panel, "BrowseNavigation").IsVisible, "浏览时应显示用途导航");
            Assert(!Require<FrameworkElement>(panel, "SearchTools").IsVisible, "空查询应收起搜索类型工具");

            SelectPurpose(panel, "地图与工程");
            until(() => panel.VisibleEntries.Count == 1 && panel.VisibleEntries[0].Key == maps.Key,
                "用途分类应筛选对应软件");
            var search = Require<TextBox>(panel, "SearchBox");
            search.Text = office.Candidate.DisplayName;
            until(() => panel.VisibleEntries.Any(entry => entry.Key == office.Key), "用途内搜索应默认跨类别");
            Assert(!Require<ScrollViewer>(panel, "BrowseNavigation").IsVisible
                && Require<FrameworkElement>(panel, "SearchTools").IsVisible, "搜索时应收起导航并显示搜索类型");
            search.Text = "";
            until(() => panel.VisibleEntries.Count == 1 && panel.VisibleEntries[0].Key == maps.Key,
                "清空搜索应恢复用途分类");

            SelectCategory(panel, mixed.Name);
            until(() => panel.VisibleEntries.Count == 2, "自建分组应同时显示应用和文件");
            Assert(panel.VisibleEntries.Any(entry => entry.Key == file.Key), "切到自建分组应保留文件成员");
            search.Text = file.Candidate.DisplayName;
            until(() => panel.VisibleEntries.Count == 1 && panel.VisibleEntries[0].Key == file.Key,
                "混合分组中搜索应找到文件");
            search.Text = "";
            until(() => panel.VisibleEntries.Count == 2 && panel.VisibleEntries.Any(entry => entry.Key == office.Key),
                "清空搜索应恢复混合分组的应用和文件");

            Click(panel, "OverviewButton");
            until(() => panel.VisibleEntries.Count == 3, "返回概览应恢复正常应用");
            SelectResult(panel, unknown);
            Press(panel, Key.Apps);
            pump();
            var menu = OpenMenu(panel);
            var change = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "更改类别"));
            change.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "办公协作"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            until(() => overrides.TryGetValue(unknown.Key, out var category) && category == LauncherAppCategory.Office,
                "手动归类应保存用户选择");
            Assert(saves > 0, "手动归类应触发持久化");
            SelectPurpose(panel, "办公协作");
            until(() => panel.VisibleEntries.Count == 2, "手动类别应立即影响用途筛选");
            panel.Close();

            panel = NewPanel();
            show(panel);
            until(() => panel.VisibleEntries.Count == 3, "重新打开后概览未载入");
            SelectPurpose(panel, "办公协作");
            until(() => panel.VisibleEntries.Count == 2, "重新打开后应继续采用手动类别");
            SelectResult(panel, unknown);
            Press(panel, Key.Apps);
            pump();
            menu = OpenMenu(panel);
            change = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "更改类别"));
            change.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "恢复自动分类"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            until(() => !overrides.ContainsKey(unknown.Key), "恢复自动分类应删除手动覆盖");
            SelectPurpose(panel, "辅助入口");
            until(() => panel.VisibleEntries.Count == 1 && panel.VisibleEntries[0].Key == auxiliary.Key,
                "辅助入口应能通过单独分类访问");
            search = Require<TextBox>(panel, "SearchBox");
            search.Text = auxiliary.Candidate.DisplayName;
            until(() => panel.VisibleEntries.Any(entry => entry.Key == auxiliary.Key), "辅助入口应参与全局搜索");
        }
        finally { panel.Close(); }
    }

    private static void ContinuousGridAndIcons(Action<Window> show, Action pump, Action<Func<bool>, string> until)
    {
        var directory = Path.Combine(Path.GetTempPath(), "BingLan-LauncherGrid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        LauncherPanelWindow? panel = null;
        try
        {
            var entries = Enumerable.Range(0, 30).Select(index =>
            {
                var path = Path.Combine(directory, $"项目{index:D2}.txt");
                File.WriteAllText(path, "grid icon regression");
                return new LauncherPanelEntry(new TopBarSearchCandidate(TopBarSearchItemKind.File, $"项目{index:D2}", Path.GetFileName(path), path), "测试分组盒");
            }).ToArray();
            panel = Create(() => Task.FromResult(new LauncherPanelSnapshot(entries, [], false)), (_, _) => throw new InvalidOperationException("滚动不得启动文件"));
            show(panel);
            Click(panel, "FilesFilter");
            until(() => panel.VisibleEntries.Count == 30, "连续网格应保留所有筛选结果");
            var list = Require<ListBox>(panel, "ResultsList");
            var grid = FindVisual<LauncherGridPanel>(list) ?? throw new InvalidOperationException("启动面板应使用连续图标网格");
            until(() => FindVisual<Image>(list)?.Source is not null, "网格应载入真实 Shell 图标");
            var columns = Math.Max(1, (int)(grid.ActualWidth / LauncherGridPanel.TileWidth));
            Press(panel, Key.Down);
            Assert(list.SelectedIndex == columns, "向下选择应按实际网格列数移动");
            var scroll = FindVisual<ScrollViewer>(list)!;
            scroll.ScrollToEnd();
            pump();
            Assert(scroll.VerticalOffset > 0, "连续网格应能滚动至末尾");
            Assert(panel.VisibleEntries[^1].Candidate.DisplayName == "项目29", "连续浏览应保留稳定的名称顺序");
            var offset = scroll.VerticalOffset;
            Require<TextBox>(panel, "SearchBox").Text = "项目00";
            until(() => panel.VisibleEntries.Count == 1, "搜索应缩小连续浏览结果");
            Require<TextBox>(panel, "SearchBox").Text = "";
            until(() => panel.VisibleEntries.Count == 30, "清空搜索应恢复文件浏览");
            pump();
            Assert(Math.Abs(scroll.VerticalOffset - offset) < 2, "清空搜索应恢复原来的滚动位置");
            var sources = Require<ComboBox>(panel, "SourceFilter");
            sources.SelectedItem = sources.Items.Cast<object>().Single(item => OptionName(item) == "测试分组盒");
            pump();
            Assert(panel.VisibleEntries.Count == 30, "来源筛选应保留全部连续结果");
        }
        finally
        {
            panel?.Close();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void SendToFileBox(Action<Window> show, Action pump, Action<Func<bool>, string> until)
    {
        var path = Path.Combine(Path.GetTempPath(), "BingLan-LauncherSend-" + Guid.NewGuid().ToString("N") + ".txt");
        const string contents = "launcher send preserves the original file";
        File.WriteAllText(path, contents);
        var modified = File.GetLastWriteTimeUtc(path);
        var state = new FileBoxState { Title = "测试收纳" };
        var box = new FileBoxWindow(state);
        var entry = new LauncherPanelEntry(new TopBarSearchCandidate(TopBarSearchItemKind.File, "发送测试", Path.GetFileName(path), path), "桌面");
        var sends = 0;
        var panel = new LauncherPanelWindow(() => Task.FromResult(new LauncherPanelSnapshot([entry], [], false)),
            (_, _) => throw new InvalidOperationException("发送到分组盒不得启动文件"),
            () => [(state.Id, state.Title)], async (id, selectedPath) =>
            {
                Assert(id == state.Id && selectedPath == path, "发送菜单应携带所选分组盒及原始路径");
                var status = await box.AddMappingsWithStatusAsync([selectedPath]);
                sends++;
                return status;
            }, 0, MonitorCatalog.GetAll().First());
        try
        {
            show(panel);
            Click(panel, "FilesFilter");
            until(() => panel.VisibleEntries.Count == 1, "发送测试文件未载入");
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                Press(panel, Key.Apps);
                pump();
                var menu = (ContextMenu?)typeof(LauncherPanelWindow).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel);
                Assert(menu?.IsOpen == true, "发送测试应打开实际项目右键菜单");
                var send = menu!.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "发送到分组盒");
                var target = send.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == state.Title);
                target.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                var expected = attempt;
                until(() => sends == expected, "发送到真实分组盒未完成");
                menu.IsOpen = false;
                pump();
                Assert(state.Items.Count == 1 && state.Items[0].Path == path, "重复发送应只保留一个真实文件映射");
                Assert(File.ReadAllText(path) == contents && File.GetLastWriteTimeUtc(path) == modified, "发送到分组盒不得修改原始文件");
            }
            Assert(Require<TextBlock>(panel, "StatusText").Text.Contains("已经在分组盒"), "重复发送应显示已存在反馈");
        }
        finally
        {
            panel.Close();
            box.Close();
            File.Delete(path);
        }
    }

    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject => Visuals<T>(root).FirstOrDefault();

    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Visuals<T>(child)) yield return descendant;
        }
    }

    private static void MissingAndValidTargets(Action<Window> show, Action pump, Action<Func<bool>, string> until)
    {
        var path = Path.Combine(Path.GetTempPath(), "BingLan-Launcher-" + Guid.NewGuid().ToString("N") + ".txt");
        var entry = new LauncherPanelEntry(new TopBarSearchCandidate(TopBarSearchItemKind.File, "测试文件", Path.GetFileName(path), path), "分组盒");
        var launches = 0;
        var closedBeforeOpen = false;
        var panel = Create(() => Task.FromResult(new LauncherPanelSnapshot([entry], [], false)), (_, _) => launches++);
        try
        {
            show(panel);
            Click(panel, "FilesFilter");
            until(() => panel.VisibleEntries.Count == 1, "文件列表未载入");
            Press(panel, Key.Return);
            until(() => Require<TextBlock>(panel, "StatusText").Text.Contains("不存在"), "失效文件应显示反馈");
            Assert(panel.IsVisible && launches == 0, "失效文件不得收起面板或调用启动器");
        }
        finally { panel.Close(); }

        File.WriteAllText(path, "launcher regression");
        LauncherPanelWindow? valid = null;
        try
        {
            valid = Create(() => Task.FromResult(new LauncherPanelSnapshot([entry], [], false)), (_, _) =>
            {
                closedBeforeOpen = valid?.IsVisible == false;
                launches++;
            });
            show(valid);
            Click(valid, "FilesFilter");
            until(() => valid.VisibleEntries.Count == 1, "有效文件列表未载入");
            Press(valid, Key.Return);
            until(() => launches == 1, "有效文件应调用启动器");
            Assert(closedBeforeOpen, "启动目标前必须关闭面板");
        }
        finally
        {
            valid?.Close();
            File.Delete(path);
        }
    }

    private static void ClosedLoadCannotRepopulate(Action<Window> show, Action pump, Action<Func<bool>, string> until)
    {
        var completion = new TaskCompletionSource<LauncherPanelSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var panel = Create(() => completion.Task, (_, _) => throw new InvalidOperationException("已关闭面板不得启动项目"));
        show(panel);
        panel.Close();
        completion.SetResult(new LauncherPanelSnapshot([], [], false));
        until(() => completion.Task.IsCompleted, "延迟候选未完成");
        pump();
        Assert(!panel.IsVisible && panel.VisibleEntries.Count == 0, "关闭后异步候选不得回填窗口");
    }

    private static LauncherPanelWindow Create(Func<Task<LauncherPanelSnapshot>> snapshot, Action<LauncherPanelEntry, bool> open) =>
        new(snapshot, open, () => [], (_, _) => Task.FromResult("完成"), 0, MonitorCatalog.GetAll().First());

    private static T Require<T>(Window window, string name) where T : FrameworkElement =>
        window.FindName(name) as T ?? throw new InvalidOperationException("找不到 " + name);

    private static ContextMenu OpenMenu(LauncherPanelWindow panel) =>
        typeof(LauncherPanelWindow).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(panel) as ContextMenu ?? throw new InvalidOperationException("项目菜单未打开");

    private static void SelectCategory(LauncherPanelWindow panel, string name)
    {
        if (name == "全部")
        {
            Click(panel, "OverviewButton");
            return;
        }
        var categories = Require<ItemsControl>(panel, "CategoriesList");
        categories.UpdateLayout();
        var button = Visuals<Button>(categories).Single(candidate =>
            Visuals<TextBlock>(candidate).Any(text => text.Text == name));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static string? OptionName(object option) =>
        option.GetType().GetProperty("Name")?.GetValue(option)?.ToString() ?? option.ToString();

    private static void SelectPurpose(LauncherPanelWindow panel, string name)
    {
        var purposes = Require<ItemsControl>(panel, "PurposeList");
        purposes.UpdateLayout();
        var button = Visuals<Button>(purposes).Single(candidate =>
            System.Windows.Automation.AutomationProperties.GetName(candidate) == name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static LauncherPanelEntry? EntryOf(object? item) =>
        item?.GetType().GetProperty("Entry")?.GetValue(item) as LauncherPanelEntry;

    private static LauncherPanelEntry? SelectedEntry(ListBox list) => EntryOf(list.SelectedItem);

    private static void SelectResult(LauncherPanelWindow panel, LauncherPanelEntry entry)
    {
        var list = Require<ListBox>(panel, "ResultsList");
        var row = list.Items.Cast<object>().Single(item => EntryOf(item)?.Key == entry.Key);
        list.SelectedItem = row;
        list.ScrollIntoView(row);
    }

    private static void Click(Window window, string name) => Require<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Press(FrameworkElement element, Key key) => element.RaiseEvent(new KeyEventArgs(
        Keyboard.PrimaryDevice, PresentationSource.FromVisual(element)!, 0, key) { RoutedEvent = UIElement.PreviewKeyDownEvent });

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
