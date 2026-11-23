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
        _ = VerifyVirtualizedLayout();
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
            EnterUsesCurrentQuery(ShowFromUser, pump, until);
            GroupsGlobalSearchAndDockPin(ShowFromUser, pump, until);
            SupplementalGroupMembers(ShowFromUser, pump, until);
            PurposeCategoriesAndOverrides(ShowFromUser, pump, until);
            ContinuousGridAndIcons(ShowFromUser, pump, until);
            SendToFileBox(ShowFromUser, pump, until);
            FavoriteModesAndFileBrowseEnhancements(ShowFromUser, pump, until);
            MissingAndValidTargets(ShowFromUser, pump, until);
            ClosedLoadCannotRepopulate(ShowFromUser, pump, until);
        }
        finally
        {
            host.Close();
        }
    }

    // Never shows a Window or sends input; safe to run on a user's active desktop.
    internal static string VerifyVirtualizedLayout()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var entries = Enumerable.Range(0, 250).Select(index =>
        {
            var name = (index % 2 == 0 ? "Word " : "QGIS ") + index.ToString("D3");
            var executable = index % 2 == 0 ? "winword.exe" : "qgis-bin.exe";
            var path = Path.Combine(Path.GetTempPath(), "BingLan-grid-" + index, executable);
            return new LauncherPanelEntry(new TopBarSearchCandidate(TopBarSearchItemKind.App, name, executable, path),
                "开始菜单", new DockPinnedApp { DisplayName = name, ExecutablePath = path });
        }).ToArray();
        var snapshot = new LauncherPanelSnapshot(entries, entries.Take(2).ToArray(), false);
        var panel = Create(() => Task.FromResult(snapshot), (_, _) => throw new InvalidOperationException("布局检查不得启动项目"));
        try
        {
            typeof(LauncherPanelWindow).GetField("_snapshot", flags)!.SetValue(panel, snapshot);
            var filter = Enum.Parse(typeof(LauncherPanelWindow).GetNestedType("PanelFilter", BindingFlags.NonPublic)!, "All");
            var rows = typeof(LauncherPanelWindow).GetMethod("BuildRows", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [snapshot, Array.Empty<LauncherPanelEntry>(), "", filter, true, LauncherFavoriteMode.FollowDock, new HashSet<string>(), false, null]);
            typeof(LauncherPanelWindow).GetMethod("Render", flags | BindingFlags.DeclaredOnly)!.Invoke(panel, [rows, false]);
            var surface = (FrameworkElement)panel.Content;
            void Layout(double width = 984)
            {
                surface.Measure(new Size(width, 610));
                surface.Arrange(new Rect(0, 0, width, 610));
                surface.UpdateLayout();
            }
            Layout();
            var list = Require<ListBox>(panel, "ResultsList");
            var grid = FindVisual<LauncherGridPanel>(list) ?? throw new InvalidOperationException("未生成虚拟网格");
            var scroll = FindVisual<ScrollViewer>(list)!;
            var initialCount = grid.Children.Count;
            var maximumCount = initialCount;
            void AssertBounded()
            {
                var columns = Math.Max(1, (int)(grid.ActualWidth / LauncherGridPanel.TileWidth));
                var bound = (int)Math.Ceiling(scroll.ViewportHeight / LauncherGridPanel.TileHeight + 3) * columns + 6;
                maximumCount = Math.Max(maximumCount, grid.Children.Count);
                Assert(grid.Children.Count <= bound && grid.Children.Count < 100,
                    $"250 项只应生成视口附近控件，实际 {grid.Children.Count}，上限 {bound}");
                Assert(grid.Children.OfType<ListBoxItem>().Select(item => item.Content).Distinct().Count() == grid.Children.Count,
                    "回收后每个可视控件应关联不同项目");
            }
            Assert(panel.VisibleEntries.Count == 250 && list.Items.Count > 250, "概览应保留全部数据与分类标题");
            AssertBounded();
            var positions = grid.ItemPositions().ToArray();
            foreach (var heading in positions.Where(item => item.Item is LauncherPanelSection))
                Assert(heading.Bounds.X == 0 && Math.Abs(heading.Bounds.Width - grid.ActualWidth) < 0.1,
                    "分类标题应占满网格宽度");
            Assert(list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1) is null,
                "首屏不应生成末尾控件");

            grid.SetVerticalOffset(123.5);
            Layout();
            Assert(Math.Abs(grid.VerticalOffset - 123.5) < 0.1, "滚动距离应保留像素小数");
            for (var offset = 0d; offset < grid.ExtentHeight; offset += 420)
            {
                grid.SetVerticalOffset(offset);
                Layout();
                AssertBounded();
            }
            Assert(list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1) is ListBoxItem,
                "末尾应在滚入视口后生成");
            var target = list.Items.Cast<object>().Where(item => EntryOf(item) is not null).ElementAt(175);
            list.SelectedItem = target;
            list.ScrollIntoView(target);
            Layout();
            Assert(list.ItemContainerGenerator.ContainerFromItem(target) is ListBoxItem,
                "ScrollIntoView 应生成屏外目标，供菜单、键盘和无障碍操作定位");
            var before = positions.Single(position => ReferenceEquals(position.Item, target)).Bounds;
            typeof(LauncherPanelWindow).GetMethod("SelectNeighbor", flags)!.Invoke(panel, [Key.Down]);
            Layout();
            var after = positions.Single(position => ReferenceEquals(position.Item, list.SelectedItem)).Bounds;
            Assert(after.Y > before.Y && Math.Abs(after.X - before.X) < 0.1,
                "向下应按逻辑布局跨行导航");
            typeof(LauncherPanelWindow).GetMethod("ScrollResultPage", flags)!.Invoke(panel, [true]);
            Layout();
            AssertBounded();
            Layout(760);
            AssertBounded();
            Assert(grid.ItemPositions().All(item => item.Bounds.Right <= grid.ActualWidth + 0.1),
                "改变宽度后所有逻辑项目应按新列数重排");

            // Replacing a filter's data source must clear stale generated controls.
            list.ItemsSource = new object[] { new LauncherPanelSection("筛选"), "one", "two" };
            Layout();
            Assert(grid.Children.Count == 3 && grid.VerticalOffset == 0,
                "筛选后应清理旧控件并钳制滚动位置");
            return $"250 items: initial={initialCount}, maximum={maximumCount}; pixel scroll, recycling, headings, reveal, arrows, page, resize, filter passed";
        }
        finally { panel.Close(); }
    }

    private static void EnterUsesCurrentQuery(Action<Window> show, Action pump, Action<Func<bool>, string> until)
    {
        var suffix = Guid.NewGuid().ToString("N");
        LauncherPanelEntry Entry(string name) => new(new TopBarSearchCandidate(TopBarSearchItemKind.App,
            name + suffix, name + ".exe", Environment.ProcessPath!), "开始菜单",
            new DockPinnedApp { DisplayName = name + suffix, ExecutablePath = Environment.ProcessPath!, Arguments = name });
        var first = Entry("Alpha");
        var second = Entry("Beta");
        var snapshot = new LauncherPanelSnapshot([first, second], [], false);
        foreach (var locate in new[] { false, true })
        {
            var opened = new List<(LauncherPanelEntry Entry, bool Locate)>();
            var panel = Create(() => Task.FromResult(snapshot), (entry, location) => opened.Add((entry, location)));
            try
            {
                show(panel);
                until(() => panel.VisibleEntries.Count == 2, "即时回车候选未载入");
                var search = Require<TextBox>(panel, "SearchBox");
                if (!locate)
                {
                    search.Text = first.Candidate.DisplayName;
                    Press(panel, Key.Return);
                }
                search.Text = second.Candidate.DisplayName;
                if (locate) _ = ActivateSelection(panel, locate: true);
                else Press(panel, Key.Return);
                until(() => opened.Count == 1, "即时回车应完成当前查询的首阶段并打开结果");
                Assert(opened[0].Entry.Key == second.Key && opened[0].Locate == locate,
                    "Enter 与 Ctrl+Enter 不得使用输入变化前的选中项");
            }
            finally { panel.Close(); }
        }

        var launches = 0;
        var guarded = Create(() => Task.FromResult(snapshot), (_, _) => launches++);
        try
        {
            show(guarded);
            until(() => guarded.VisibleEntries.Count == 2, "取消回车候选未载入");
            var search = Require<TextBox>(guarded, "SearchBox");
            search.Text = "zzzz-no-match-" + suffix;
            var emptyActivation = ActivateSelection(guarded, locate: false);
            until(() => emptyActivation.IsCompleted, "空查询结果的回车未完成");
            pump();
            Assert(launches == 0 && guarded.VisibleEntries.Count == 0, "无匹配时不得回退打开旧选中项");

            search.Text = first.Candidate.DisplayName;
            var cancelled = ActivateSelection(guarded, locate: true);
            search.Text = second.Candidate.DisplayName;
            until(() => cancelled.IsCompleted && guarded.VisibleEntries.Count == 1
                && guarded.VisibleEntries[0].Key == second.Key, "新输入应取消等待中的定位操作");
            Assert(launches == 0, "等待首阶段期间输入变化不得打开任一候选");

            Press(guarded, Key.Return);
            typeof(LauncherPanelWindow).GetMethod("SuspendForComposition", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(guarded, []);
            pump();
            Assert(launches == 0 && guarded.IsVisible, "IME 组合开始应取消已经排队的回车");
            typeof(LauncherPanelWindow).GetMethod("ResumeAfterComposition", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(guarded, []);
            until(() => guarded.VisibleEntries.Count == 1, "IME 组合结束后应恢复查询");
            pump();
            Assert(launches == 0, "IME 恢复查询不得重放旧回车");
        }
        finally { guarded.Close(); }

        var navigated = Create(() => Task.FromResult(snapshot), (entry, _) =>
        {
            Assert(entry.Key == second.Key, "当前结果的显式选择不能被首条结果替换");
            launches++;
        });
        try
        {
            show(navigated);
            until(() => navigated.VisibleEntries.Count == 2, "键盘选择候选未载入");
            SelectResult(navigated, second);
            Press(navigated, Key.Return);
            until(() => launches == 1, "当前选中项应可直接打开");
        }
        finally { navigated.Close(); }
    }

    private static Task ActivateSelection(LauncherPanelWindow panel, bool locate) =>
        (Task)typeof(LauncherPanelWindow).GetMethod("OpenSelectionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, [locate])!;

    private static void SupplementalGroupMembers(Action<Window> show, Action pump, Action<Func<bool>, string> until)
    {
        var groups = new List<LauncherGroupState> { new() { Name = "索引资料" } };
        var saves = 0;
        var snapshot = new LauncherPanelSnapshot([], [], false);
        var panel = new LauncherPanelWindow(() => Task.FromResult(snapshot),
            (_, _) => throw new InvalidOperationException("分组整理不得打开项目"), () => [],
            (_, _) => Task.FromResult("完成"), 0, MonitorCatalog.GetAll().First(),
            readGroups: () => groups, saveOrganizer: () => saves++);
        try
        {
            show(panel);
            until(() => Require<TextBlock>(panel, "EmptyHint").IsVisible, "空面板未载入");
            foreach (var source in new[] { "Windows 索引", "Everything" })
            {
                var name = source + Guid.NewGuid().ToString("N");
                var path = Path.Combine(Path.GetTempPath(), name + ".txt");
                var entry = new LauncherPanelEntry(new TopBarSearchCandidate(TopBarSearchItemKind.File,
                    name, name + ".txt", path), source);
                var search = Require<TextBox>(panel, "SearchBox");
                search.Text = name;
                until(() => SearchRowsCurrent(panel), "索引注入前应完成首阶段");
                typeof(LauncherPanelWindow).GetMethod("CancelSearch", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(panel, []);
                // Supply the same incremental rows as an index reply without relying on this machine's index.
                var build = typeof(LauncherPanelWindow).GetMethod("BuildRows", BindingFlags.Static | BindingFlags.NonPublic)!;
                var filter = Enum.Parse(typeof(LauncherPanelWindow).GetNestedType("PanelFilter", BindingFlags.NonPublic)!, "All");
                var rows = build.Invoke(null, [snapshot, new[] { entry }, name, filter, true, LauncherFavoriteMode.FollowDock, new HashSet<string>(), true, null]);
                typeof(LauncherPanelWindow).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!
                    .Invoke(panel, [rows, false]);
                SelectResult(panel, entry);
                Press(panel, Key.Apps);
                pump();
                var savesBeforeEdit = saves;
                var menu = OpenMenu(panel);
                var join = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "加入面板分组"));
                if (source == "Windows 索引")
                {
                    join.Items.OfType<MenuItem>().Single(item => Equals(item.Header, groups[0].Name))
                        .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    menu.IsOpen = false;
                }
                else
                {
                    join.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "新建分组…"))
                        .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    menu.IsOpen = false;
                    until(() => Require<FrameworkElement>(panel, "GroupEditor").IsVisible, "索引结果的新分组编辑器未打开");
                    Require<TextBox>(panel, "GroupNameEditor").Text = "Everything资料";
                    Click(panel, "SaveGroupButton");
                }
                Assert(saves == savesBeforeEdit + 1, "每次分组修改应只保存一次");
                var group = source == "Windows 索引" ? groups[0] : groups[^1];
                Assert(group.Members.Single().FallbackPath == path && group.Members[0].Key == entry.Key,
                    "索引入口应立即保存原路径及稳定键");
                // A Dock refresh may finish with a snapshot captured before the group edit.
                typeof(LauncherPanelWindow).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(panel, snapshot);
                search.Text = "";
                until(() => SearchRowsCurrent(panel)
                    && Require<ScrollViewer>(panel, "BrowseNavigation").IsVisible, "清空查询应恢复分组导航");
                SelectCategory(panel, group.Name);
                until(() => panel.VisibleEntries.Count == 1 && panel.VisibleEntries[0].Key == entry.Key,
                    "索引入口加入分组后应在同次面板会话清空查询仍可见");
            }
            Assert(saves >= 2, "现有分组和新建分组均应持久化");

            foreach (var path in new[] { "shell:MyComputerFolder", "shell:RecycleBinFolder" })
            {
                var entry = new LauncherPanelEntry(new TopBarSearchCandidate(TopBarSearchItemKind.Folder,
                    path, path, path), "分组盒");
                var member = (LauncherGroupMember)typeof(LauncherPanelWindow)
                    .GetMethod("DescribeMember", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [entry])!;
                Assert(member.FallbackPath == path && member.Key == entry.Key,
                    "Shell 系统入口的分组后备路径应保留原有稳定键");
            }
        }
        finally { panel.Close(); }
    }

    private static bool SearchRowsCurrent(LauncherPanelWindow panel)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        return Equals(typeof(LauncherPanelWindow).GetField("_searchVersion", flags)!.GetValue(panel),
            typeof(LauncherPanelWindow).GetField("_renderedSearchVersion", flags)!.GetValue(panel));
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
            Assert(Require<TextBlock>(panel, "StatusText").Text == string.Empty, "浏览自建分组时状态栏不应重复数量摘要");

            search.Text = work.Candidate.DisplayName;
            until(() => panel.VisibleEntries.Count == 1 && Require<Button>(panel, "TabGroupsButton").IsVisible,
                "搜索自建分组中的应用应显示全部搜索结果");
            Click(panel, "TabGroupsButton");
            until(() => SearchRowsCurrent(panel) && panel.VisibleEntries.Count == 1
                && panel.VisibleEntries[0].Key == work.Key,
                "搜索态点击我的分组标签应保留自建分组中的应用结果");
            search.Text = game.Candidate.DisplayName;
            until(() => SearchRowsCurrent(panel) && panel.VisibleEntries.Count == 0,
                "搜索态我的分组标签不应包含未分组的应用");
            search.Text = "";
            until(() => panel.VisibleEntries.Count == 1 && panel.VisibleEntries[0] == work,
                "清空查询应回到测试项目分组");

            SelectEntry(work);
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

            SelectCategory(panel, "测试常用");
            until(() => panel.VisibleEntries.Count == 1 && Require<Button>(panel, "RenameGroupButton").IsVisible
                && Require<Button>(panel, "DeleteGroupButton").IsVisible, "选中自建分组后应显示重命名和删除按钮");
            Click(panel, "RenameGroupButton");
            until(() => Require<FrameworkElement>(panel, "GroupEditor").IsVisible, "点击重命名应显示内联编辑栏");
            var saveGroup = Require<Button>(panel, "SaveGroupButton");
            saveGroup.Focus();
            until(() => saveGroup.IsKeyboardFocused, "重命名编辑栏按钮应可聚焦");
            Press(panel, Key.Escape);
            until(() => !Require<FrameworkElement>(panel, "GroupEditor").IsVisible,
                "焦点位于分组编辑栏按钮时按 Esc 应关闭编辑栏");
            Assert(panel.IsVisible, "关闭分组编辑栏不应关掉总览卡");
            Click(panel, "RenameGroupButton");
            until(() => Require<FrameworkElement>(panel, "GroupEditor").IsVisible, "再次点击重命名应显示内联编辑栏");
            Require<TextBox>(panel, "GroupNameEditor").Text = "测试常用2";
            Click(panel, "SaveGroupButton");
            until(() => groups.Any(group => group.Name == "测试常用2")
                && Require<TextBlock>(panel, "StatusText").Text == "已重命名为「测试常用2」",
                "内联重命名应更新分组名并反馈结果");

            Click(panel, "DeleteGroupButton");
            until(() => Require<FrameworkElement>(panel, "GroupDeleteConfirm").IsVisible, "点击删除分组应显示内联确认条");
            Click(panel, "ConfirmDeleteGroupButton");
            until(() => groups.All(group => group.Name != "测试常用2")
                && Require<TextBlock>(panel, "StatusText").Text == "已删除分组「测试常用2」",
                "确认删除分组应移除分组并反馈结果");
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
            Assert(SelectedEntry(list) is null && Equals(list.Tag, false), "浏览模式初始不应默认选中项目");
            Press(panel, Key.Return);
            pump();
            Assert(opened.Count == 0, "浏览模式未选中项目时按回车不应启动");
            Press(panel, Key.Down);
            until(() => SelectedEntry(list)?.Key == first.Key && Equals(list.Tag, true),
                "按下方向键应选中首个入口并开启键盘选中框线");
            var sources = Require<ComboBox>(panel, "SourceFilter");
            sources.SelectedItem = sources.Items.Cast<object>().Single(item => OptionName(item) == "开始菜单");
            until(() => panel.VisibleEntries.Count == 1, "合并后的常用入口应保留开始菜单来源");
            Assert(panel.VisibleEntries[0].Pinned?.Arguments == "--profile work",
                "按其他来源筛选时应保留固定项的启动参数");
            sources.SelectedIndex = 0;
            until(() => panel.VisibleEntries.Count == 2, "清除来源筛选应恢复全部应用");
            Click(panel, "TabFilesButton");
            until(() => panel.VisibleEntries.Count == 1, "文件页只显示文件，常用应用应遵守类型筛选");
            Assert(panel.VisibleEntries.Single().Candidate.Kind == TopBarSearchItemKind.File,
                "固定文件应保留在文件页");
            var search = Require<TextBox>(panel, "SearchBox");
            search.Text = name;
            until(() => Require<Button>(panel, "TabAllButton").IsVisible && panel.VisibleEntries.Count == 3,
                "输入查询应默认跨应用和文件搜索并展示全部标签");
            Assert(SelectedEntry(list)?.Key == first.Key, "新查询应选择首个匹配入口");
            Click(panel, "TabAppsButton");
            until(() => panel.VisibleEntries.Count == 2 && search.Text == name,
                "搜索态切换顶部应用标签应保留关键词并只保留应用结果");
            Click(panel, "TabFilesButton");
            until(() => panel.VisibleEntries.Count == 1 && search.Text == name,
                "搜索态切换顶部文件标签应保留关键词并只保留文件结果");
            Click(panel, "TabAllButton");
            until(() => panel.VisibleEntries.Count == 3, "切回全部标签应恢复全部搜索结果");
            search.Focus();
            until(() => search.IsKeyboardFocused, "搜索框有输入时应保持可聚焦");
            Assert(!Press(panel, Key.Right) && !Press(panel, Key.Left)
                && !Press(panel, Key.Home) && !Press(panel, Key.End),
                "搜索框有文字时左右方向键与 Home/End 不得被面板拦截，应留给光标编辑");
            pump();
            Assert(SelectedEntry(list)?.Key == first.Key, "搜索框有文字时左右方向键不得改选结果项");
            Press(panel, Key.Apps);
            pump();
            var menu = (ContextMenu?)typeof(LauncherPanelWindow).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel);
            Assert(menu?.IsOpen == true, "键盘菜单键应打开所选项目菜单");
            Assert(!menu!.Items.OfType<MenuItem>().Any(item => item.Header?.ToString() == "发送到文件收纳盒"), "带参数应用不可发送为会丢失参数的文件映射");
            Press(panel, Key.Escape);
            pump();
            Assert(panel.IsVisible && !menu.IsOpen, "菜单内 Escape 应只关闭菜单");
            search.Text = "";
            until(() => panel.VisibleEntries.Count == 1 && !Require<Button>(panel, "TabAllButton").IsVisible,
                "清空查询应恢复文件浏览并隐藏搜索专用全部标签");
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
        var office2 = App("Excel", "excel.exe");
        var office3 = App("PowerPoint", "powerpnt.exe");
        var sparseDesign = App("Photoshop", "photoshop.exe");
        var unknown = App("项目工具", "unknown-purpose.exe");
        var auxiliary = App("安装程序", "setup.exe");
        var file = new LauncherPanelEntry(new TopBarSearchCandidate(TopBarSearchItemKind.File,
            "项目资料" + suffix, "report.txt", Path.Combine(Path.GetTempPath(), "report-" + suffix + ".txt")), "桌面");
        var mixed = new LauncherGroupState { Name = "软件与资料" };
        LauncherGroupRules.TryAdd(mixed, new LauncherGroupMember(office.Key));
        LauncherGroupRules.TryAdd(mixed, new LauncherGroupMember(file.Key));
        var groups = new List<LauncherGroupState> { mixed };
        var overrides = new Dictionary<string, LauncherAppCategory>(StringComparer.Ordinal);
        var preferences = new LauncherPreferencesState();
        var saves = 0;
        var snapshot = new LauncherPanelSnapshot([office, office2, office3, sparseDesign, unknown, auxiliary, file], [office], false);
        LauncherPanelWindow NewPanel() => new(() => Task.FromResult(snapshot),
            (_, _) => throw new InvalidOperationException("分类整理不能启动项目"), () => [],
            (_, _) => Task.FromResult("完成"), 0, MonitorCatalog.GetAll().First(),
            readGroups: () => groups, saveOrganizer: () => saves++, readCategoryOverrides: () => overrides,
            preferences: preferences);
        var panel = NewPanel();
        try
        {
            show(panel);
            until(() => panel.VisibleEntries.Count == 5, "概览应包含常用与正常应用，并收起辅助入口");
            Assert(panel.VisibleEntries.Count(entry => entry.Key == office.Key) == 1, "概览中的常用应用应只出现一次");
            Assert(Require<ScrollViewer>(panel, "BrowseNavigation").IsVisible, "浏览时应显示用途导航");
            Assert(!Require<Button>(panel, "TabAllButton").IsVisible, "空查询应收起顶部全部标签");

            // 仅 1 项的自动类别应折叠进「其他」，满 3 项的「效率办公」保持独立
            SelectPurpose(panel, "效率办公");
            until(() => panel.VisibleEntries.Count == 3 && panel.VisibleEntries.Any(e => e.Key == office.Key),
                "满 3 项的用途分类应独立显示");
            SelectPurpose(panel, "其他");
            until(() => panel.VisibleEntries.Count == 2 && panel.VisibleEntries.Any(e => e.Key == sparseDesign.Key),
                "不足 3 项的自动分类应折叠到其他");

            var search = Require<TextBox>(panel, "SearchBox");
            search.Text = office.Candidate.DisplayName;
            until(() => panel.VisibleEntries.Any(entry => entry.Key == office.Key), "用途内搜索应默认跨类别");
            Assert(!Require<ScrollViewer>(panel, "BrowseNavigation").IsVisible
                && Require<Button>(panel, "TabAllButton").IsVisible, "搜索时应收起导航并在顶部显示全部标签");
            search.Text = "";
            until(() => panel.VisibleEntries.Count == 2 && panel.VisibleEntries.Any(e => e.Key == sparseDesign.Key)
                && !Require<Button>(panel, "TabAllButton").IsVisible,
                "清空搜索应恢复用途分类并收起顶部全部标签");

            SelectCategory(panel, mixed.Name);
            until(() => panel.VisibleEntries.Count == 2, "自建分组应同时显示应用和文件");
            Assert(panel.VisibleEntries.Any(entry => entry.Key == file.Key), "切到自建分组应保留文件成员");
            search.Text = file.Candidate.DisplayName;
            until(() => panel.VisibleEntries.Count == 1 && panel.VisibleEntries[0].Key == file.Key,
                "混合分组中搜索应找到文件");
            search.Text = "";
            until(() => panel.VisibleEntries.Count == 2 && panel.VisibleEntries.Any(entry => entry.Key == office.Key),
                "清空搜索应恢复混合分组的应用和文件");

            Click(panel, "TabAppsButton");
            Click(panel, "OverviewButton");
            until(() => panel.VisibleEntries.Count == 5, "返回概览应恢复正常应用");
            SelectResult(panel, unknown);
            Press(panel, Key.Apps);
            pump();
            var menu = OpenMenu(panel);
            var change = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "更改类别"));
            change.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "设计与创作"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            until(() => overrides.TryGetValue(unknown.Key, out var category) && category == LauncherAppCategory.Design,
                "手动归类应保存用户选择");
            Assert(saves > 0, "手动归类应触发持久化");
            SelectPurpose(panel, "设计与创作");
            until(() => panel.VisibleEntries.Count == 2, "手动类别应立即独立显示并包含同类项");
            panel.Close();

            panel = NewPanel();
            show(panel);
            until(() => panel.VisibleEntries.Count == 5, "重新打开后概览未载入");
            SelectPurpose(panel, "设计与创作");
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
            const int entryCount = 250;
            var entries = Enumerable.Range(0, entryCount).Select(index =>
            {
                var path = Path.Combine(directory, $"项目{index:D3}.txt");
                File.WriteAllText(path, "grid icon regression");
                return new LauncherPanelEntry(new TopBarSearchCandidate(TopBarSearchItemKind.File, $"项目{index:D3}", Path.GetFileName(path), path), "测试分组盒");
            }).ToArray();
            panel = Create(() => Task.FromResult(new LauncherPanelSnapshot(entries, [], false)), (_, _) => throw new InvalidOperationException("滚动不得启动文件"));
            show(panel);
            Click(panel, "TabFilesButton");
            until(() => panel.VisibleEntries.Count == entryCount, "连续网格应保留所有筛选结果");
            var list = Require<ListBox>(panel, "ResultsList");
            var grid = FindVisual<LauncherGridPanel>(list) ?? throw new InvalidOperationException("启动面板应使用连续图标网格");
            until(() => FindVisual<Image>(list)?.Source is not null, "网格应载入真实 Shell 图标");
            var columns = Math.Max(1, (int)(grid.ActualWidth / LauncherGridPanel.TileWidth));
            var scroll = FindVisual<ScrollViewer>(list)!;
            void AssertBounded()
            {
                var bound = (int)Math.Ceiling(scroll.ViewportHeight / LauncherGridPanel.TileHeight + 3) * columns + 2;
                Assert(grid.Children.Count <= bound && grid.Children.Count < entryCount / 2,
                    $"250 项应只生成视口附近的控件：实际 {grid.Children.Count}，上限 {bound}");
            }
            AssertBounded();
            Assert(list.ItemContainerGenerator.ContainerFromIndex(entryCount - 1) is null,
                "未滚动到的末项不应生成 WPF 控件");
            Assert(SelectedEntry(list) is null && Equals(list.Tag, false), "连续网格浏览初始不应选中项目");
            VerifyTileHover(panel, list, until);
            Press(panel, Key.Down);
            Assert(list.SelectedIndex == columns, "向下选择应按实际网格列数移动");
            // Navigation uses data geometry even when the selected item has no control yet.
            var offscreen = columns * 8;
            list.SelectedIndex = offscreen;
            Press(panel, Key.Down);
            pump();
            Assert(list.SelectedIndex == offscreen + columns,
                "未生成控件的项目仍应按实际列数向下选择");
            Assert(list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) is ListBoxItem,
                "方向键应滚动并生成目标控件");
            var beforePage = scroll.VerticalOffset;
            Press(panel, Key.PageDown);
            pump();
            Assert(scroll.VerticalOffset > beforePage && list.SelectedIndex > offscreen,
                "PageDown 应按像素滚动并选择新视口的项目");
            Press(panel, Key.End);
            until(() => list.SelectedIndex == entryCount - 1, "End 键应跳至末尾项目");
            Press(panel, Key.Home);
            until(() => list.SelectedIndex == 0, "Home 键应回到首个项目");
            var search = Require<TextBox>(panel, "SearchBox");
            search.Focus();
            until(() => search.IsKeyboardFocused, "搜索框应可聚焦");
            Assert(Press(panel, Key.Right), "搜索框为空时右键应由网格导航拦截");
            until(() => list.SelectedIndex == 1, "搜索框为空时右键应横向移动到下一网格项");
            Assert(Press(panel, Key.Left), "搜索框为空时左键应由网格导航拦截");
            until(() => list.SelectedIndex == 0, "搜索框为空时左键应横向回到首个网格项");
            var material = Require<Button>(panel, "MaterialButton");
            material.Focus();
            until(() => material.IsKeyboardFocused, "材质切换按钮应可接收焦点");
            Assert(!Press(panel, Key.Down) && !Press(panel, Key.Right)
                && !Press(panel, Key.Home) && !Press(panel, Key.End),
                "焦点不在搜索框或结果列表时方向键与 Home/End 不得被拦截");
            pump();
            Assert(list.SelectedIndex == 0, "焦点不在搜索框或结果列表时方向键不得改选项目");
            AssertBounded();
            scroll.ScrollToEnd();
            pump();
            Assert(scroll.VerticalOffset > 0, "连续网格应能滚动至末尾");
            AssertBounded();
            Assert(list.ItemContainerGenerator.ContainerFromIndex(entryCount - 1) is ListBoxItem,
                "滚动至末尾应生成末项控件");
            Assert(panel.VisibleEntries[^1].Candidate.DisplayName == "项目249", "连续浏览应保留稳定的名称顺序");
            var offset = scroll.VerticalOffset;
            Require<TextBox>(panel, "SearchBox").Text = "项目000";
            until(() => panel.VisibleEntries.Count == 1, "搜索应缩小连续浏览结果");
            Require<TextBox>(panel, "SearchBox").Text = "";
            until(() => panel.VisibleEntries.Count == entryCount, "清空搜索应恢复文件浏览");
            pump();
            Assert(Math.Abs(scroll.VerticalOffset - offset) < 2, "清空搜索应恢复原来的滚动位置");
            var sources = Require<ComboBox>(panel, "SourceFilter");
            sources.SelectedItem = sources.Items.Cast<object>().Single(item => OptionName(item) == "测试分组盒");
            pump();
            Assert(panel.VisibleEntries.Count == entryCount, "来源筛选应保留全部连续结果");
            AssertBounded();
        }
        finally
        {
            panel?.Close();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string RetoggleTag(ListBox list, Border tile)
    {
        list.Tag = false;
        list.Tag = true;
        return tile.BorderBrush.ToString() ?? "空";
    }

    private static string AncestorListInfo(DependencyObject item)
    {
        DependencyObject? current = item;
        while (current is not null && current is not ListBox)
        {
            current = VisualTreeHelper.GetParent(current);
        }
        return current is ListBox list ? $"{list.Name}/Tag={list.Tag}" : "无";
    }

    private static void VerifyTileHover(LauncherPanelWindow panel, ListBox list, Action<Func<bool>, string> until)
    {
        var selected = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        var adjacent = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
        var selectedTile = (Border)selected.Template.FindName("Tile", selected);
        var adjacentTile = (Border)adjacent.Template.FindName("Tile", adjacent);
        var search = Require<TextBox>(panel, "SearchBox");
        var originalCursor = System.Windows.Forms.Cursor.Position;
        try
        {
            MoveTo(search);
            until(() => !selected.IsMouseOver && !adjacent.IsMouseOver, "鼠标应移出图标区");
            Assert(!selected.IsSelected && IsClear(selectedTile) && IsClear(adjacentTile), "浏览模式初始项目不应选中或显示底纹");

            MoveTo(adjacent);
            until(() => adjacent.IsMouseOver, "鼠标应移到相邻项目");
            Assert(IsClear(selectedTile) && adjacentTile.Background is SolidColorBrush { Color.A: > 0 },
                "底纹只应出现在鼠标悬停的项目上");

            MoveTo(search);
            until(() => !adjacent.IsMouseOver, "鼠标应离开相邻项目");
            Press(panel, Key.Down);
            until(() => selected.IsSelected && Equals(list.Tag, true), "浏览模式按向下键应选中首个项目");
            Assert(IsClear(selectedTile) && IsClear(adjacentTile) && selectedTile.BorderBrush is SolidColorBrush { Color.A: > 0 },
                $"移出项目后应移除底纹，键盘选中项仅保留描边：选中底纹 {selectedTile.Background}，相邻底纹 {adjacentTile.Background}，选中描边 {selectedTile.BorderBrush}，选中悬停 {selected.IsMouseOver}，相邻悬停 {adjacent.IsMouseOver}，焦点画刷资源 {panel.TryFindResource("FloatingGlassFocusBrush")}，选中项键盘焦点 {selected.IsKeyboardFocused}，列表标记 {list.Tag}，已选 {selected.IsSelected}，祖先列表 {AncestorListInfo(selected)}，描边来源 {System.Windows.DependencyPropertyHelper.GetValueSource(selectedTile, Border.BorderBrushProperty).BaseValueSource}，模板触发器 {selected.Template?.Triggers.Count}，瓦片取画刷 {selectedTile.TryFindResource("FloatingGlassFocusBrush")}，重切标记后描边 {RetoggleTag(list, selectedTile)}");
        }
        finally
        {
            TopBarWindowTests.MoveMouseTo(originalCursor.X, originalCursor.Y);
        }

        static bool IsClear(Border tile) => tile.Background is SolidColorBrush { Color.A: 0 };

        static void MoveTo(FrameworkElement element)
        {
            var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
            TopBarWindowTests.MoveMouseTo((int)point.X, (int)point.Y);
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
            Click(panel, "TabFilesButton");
            until(() => panel.VisibleEntries.Count == 1, "发送测试文件未载入");
            Press(panel, Key.Down);
            until(() => SelectedEntry(Require<ListBox>(panel, "ResultsList"))?.Key == entry.Key, "按方向键选中发送测试文件");
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                Press(panel, Key.Apps);
                pump();
                var menu = (ContextMenu?)typeof(LauncherPanelWindow).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel);
                Assert(menu?.IsOpen == true, "发送测试应打开实际项目右键菜单");
                var send = menu!.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "发送到文件收纳盒");
                var target = send.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == state.Title);
                target.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                var expected = attempt;
                until(() => sends == expected, "发送到真实分组盒未完成");
                menu.IsOpen = false;
                pump();
                Assert(state.Items.Count == 1 && state.Items[0].Path == path, "重复发送应只保留一个真实文件映射");
                Assert(File.ReadAllText(path) == contents && File.GetLastWriteTimeUtc(path) == modified, "发送到分组盒不得修改原始文件");
            }
            Assert(Require<TextBlock>(panel, "StatusText").Text.Contains("已经在文件收纳盒"), "重复发送应显示已存在反馈");
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
            Click(panel, "TabFilesButton");
            until(() => panel.VisibleEntries.Count == 1, "文件列表未载入");
            Press(panel, Key.Down);
            until(() => SelectedEntry(Require<ListBox>(panel, "ResultsList"))?.Key == entry.Key, "按方向键选中失效文件");
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
            Click(valid, "TabFilesButton");
            until(() => valid.VisibleEntries.Count == 1, "有效文件列表未载入");
            Press(valid, Key.Down);
            until(() => SelectedEntry(Require<ListBox>(valid, "ResultsList"))?.Key == entry.Key, "按方向键选中有效文件");
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
        Press(panel, Key.Return);
        pump();
        Assert(panel.IsVisible && panel.VisibleEntries.Count == 0, "候选载入前的回车应安静等待新输入");
        panel.Close();
        completion.SetResult(new LauncherPanelSnapshot([], [], false));
        until(() => completion.Task.IsCompleted, "延迟候选未完成");
        pump();
        Assert(!panel.IsVisible && panel.VisibleEntries.Count == 0, "关闭后异步候选不得回填窗口");
    }

    private static void FavoriteModesAndFileBrowseEnhancements(Action<Window> show, Action pump, Action<Func<bool>, string> until)
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "BingLan-file-ux-" + Guid.NewGuid().ToString("N"));
        var subDir = Path.Combine(rootDir, "子目录A");
        Directory.CreateDirectory(subDir);
        var olderFile = Path.Combine(subDir, "alpha-old.txt");
        var newerFile = Path.Combine(subDir, "zeta-new.txt");
        File.WriteAllText(olderFile, "old");
        File.WriteAllText(newerFile, "newer content");
        File.SetLastWriteTimeUtc(olderFile, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(newerFile, DateTime.UtcNow);

        var appA = new LauncherPanelEntry(
            new TopBarSearchCandidate(TopBarSearchItemKind.App, "AlphaApp", "alpha.exe", Path.Combine(rootDir, "alpha.exe")),
            "开始菜单");
        var appB = new LauncherPanelEntry(
            new TopBarSearchCandidate(TopBarSearchItemKind.App, "BetaApp", "beta.exe", Path.Combine(rootDir, "beta.exe")),
            "开始菜单");
        var place = new BingLan.App.Services.LauncherPlace("place:test", "测试目录", rootDir, rootDir, "\uE8B7", IsFileBox: false);
        var preferences = new LauncherPreferencesState { FavoriteMode = LauncherFavoriteMode.Independent };
        var saves = 0;
        var dockPins = 0;
        var snapshot = new LauncherPanelSnapshot([appA, appB], [appA], false, Places: [place]);
        var panel = new LauncherPanelWindow(
            () => Task.FromResult(snapshot),
            (_, _) => throw new InvalidOperationException("浏览测试不得打开目标"),
            () => [],
            (_, _) => Task.FromResult("完成"),
            0,
            MonitorCatalog.GetAll().First(),
            pinToDock: _ => { dockPins++; return "已固定"; },
            readGroups: () => new List<LauncherGroupState>(),
            saveOrganizer: () => saves++,
            preferences: preferences);
        try
        {
            show(panel);
            until(() => panel.VisibleEntries.Count == 2, "独立星标模式应载入全部应用");
            SelectResult(panel, appB);
            Press(panel, Key.Apps);
            pump();
            var menu = OpenMenu(panel);
            menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "加入常用"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            until(() => preferences.IsIndependentFavorite([appB.Key]) && dockPins == 0 && saves > 0,
                "独立星标收藏应写入本机偏好且不改动 Dock 固定");

            // 切换到文件页 → 进入测试目录 → 钻入子目录A → 排序、目录内搜索、Backspace 返回
            Click(panel, "TabFilesButton");
            until(() => Require<ItemsControl>(panel, "FilePlacesBar").IsVisible, "文件总览应展示常用位置卡片");
            Button? placeButton = null;
            until(() => (placeButton = Visuals<Button>(Require<ItemsControl>(panel, "FilePlacesBar"))
                .FirstOrDefault(btn => System.Windows.Automation.AutomationProperties.GetName(btn) == "测试目录")) is not null,
                "常用位置卡片应包含测试目录：实际 " + string.Join("、", Visuals<Button>(Require<ItemsControl>(panel, "FilePlacesBar"))
                    .Select(btn => System.Windows.Automation.AutomationProperties.GetName(btn))));
            placeButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            until(() => panel.VisibleEntries.Any(entry => entry.Candidate.DisplayName == "子目录A"),
                "点击常用位置应读取目录内容");
            var list = Require<ListBox>(panel, "ResultsList");
            SelectResult(panel, panel.VisibleEntries.Single(entry => entry.Candidate.DisplayName == "子目录A"));
            Press(panel, Key.Return);
            until(() => panel.VisibleEntries.Count == 2 && Require<ItemsControl>(panel, "BreadcrumbBar").IsVisible,
                "钻入子目录后应显示可点击面包屑");
            Assert(panel.VisibleEntries[0].Candidate.DisplayName == "alpha-old",
                "默认按名称排序时 alpha-old 应排在 zeta-new 前");
            Click(panel, "FileSortByModifiedButton");
            until(() => panel.VisibleEntries[0].Candidate.DisplayName == "zeta-new",
                "切换为最近修改排序后最新文件应排在前面");

            // 目录内筛选
            var search = Require<TextBox>(panel, "SearchBox");
            search.Text = "zeta";
            until(() => Require<Border>(panel, "SearchScope").IsVisible, "目录内输入搜索词应提供仅搜当前目录选项");
            var scopeCheck = Require<CheckBox>(panel, "SearchCurrentCategory");
            scopeCheck.IsChecked = true;
            scopeCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            until(() => panel.VisibleEntries.Count == 1 && panel.VisibleEntries[0].Candidate.DisplayName == "zeta-new",
                "勾选仅搜当前目录应只返回当前目录匹配文件");
            search.Text = "";
            until(() => panel.VisibleEntries.Count == 2, "清空搜索应恢复当前目录列表");

            // Backspace 在空搜索框下应返回上级目录
            search.Focus();
            until(() => search.IsKeyboardFocused, "搜索框应可聚焦");
            Assert(Press(panel, Key.Back), "空搜索框下 Backspace 应触发返回上一级目录");
            until(() => panel.VisibleEntries.Any(entry => entry.Candidate.DisplayName == "子目录A")
                && !Require<ItemsControl>(panel, "BreadcrumbBar").IsVisible,
                "Backspace 应返回到测试目录并收起子目录面包屑");
        }
        finally
        {
            panel.Close();
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, recursive: true);
        }
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
            Click(panel, "TabAppsButton");
            Click(panel, "OverviewButton");
            return;
        }
        Click(panel, "TabGroupsButton");
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
        Click(panel, "TabAppsButton");
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

    private static bool Press(FrameworkElement element, Key key)
    {
        var args = new KeyEventArgs(
            Keyboard.PrimaryDevice, PresentationSource.FromVisual(element)!, 0, key)
        {
            RoutedEvent = UIElement.PreviewKeyDownEvent
        };
        element.RaiseEvent(args);
        return args.Handled;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
