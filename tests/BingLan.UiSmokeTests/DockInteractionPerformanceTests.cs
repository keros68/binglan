using BingLan.App.Dock;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BingLan.App.Windows;
using BingLan.Core.Dock;
using BingLan.Core.Models;

internal static class DockInteractionPerformanceTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run()
    {
        var dock = new IceBlueDockWindow(new DockState
        {
            ShowStartMenuButton = false,
            ShowLauncherButton = false,
            ShowDesktopButton = false,
            HoverMagnification = true
        });
        var originalMotion = IceBlueDockWindow.Motion;
        IceBlueDockWindow.Motion = MotionLevel.Standard;
        try
        {
            var first = Item(1, "First window");
            var second = Item(2, "Second window");
            TestRunningDivider();
            var attention = (DockAttention)typeof(IceBlueDockWindow)
                .GetField("_attention", PrivateInstance)!.GetValue(dock)!;
            attention.Flashed(1, 1, unread: 3);
            TestLayoutAndBadgeUpdates(dock, attention, first, second);
            TestLatestPointerSample(dock);
            TestCurrentLaunchTarget(dock);
            TestRunningPinDrag();
        }
        finally
        {
            IceBlueDockWindow.Motion = originalMotion;
            dock.Close();
        }
    }

    internal static void TestBackgroundRefresh(Action<Func<bool>, string> until)
    {
        const string path = @"C:\Program Files\DockIndicator\DockIndicator.exe";
        const string secondPath = @"C:\Program Files\DockIndicator\Second.exe";
        const string addedPath = @"C:\Program Files\DockIndicator\Third.exe";
        var win32Pin = new DockPinnedApp { DisplayName = "Dock indicator", AppUserModelId = "dock.indicator", ExecutablePath = path };
        var secondPin = DockPinRules.CreateFromExecutable(secondPath)!;
        var state = new DockState { PinnedApps = [win32Pin, secondPin] };
        var key = DockPinRules.IdentityKey(win32Pin);
        using var releaseScan = new ManualResetEventSlim();
        var scans = 0;
        long ticks = 10_000;
        var runtime = new DockRuntime(() =>
        {
            Interlocked.Increment(ref scans);
            if (!releaseScan.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("后台扫描阻塞了窗口刷新");
            return [new(path, null), new(secondPath, null), new(addedPath, null)];
        }, () => ticks);
        var changes = 0;
        runtime.BackgroundPinsChanged += () => changes++;

        IReadOnlyList<DockItem> Refresh(IReadOnlyList<WindowGroup> groups) =>
            DockItemComposer.Compose(state.PinnedApps, groups, backgroundMatches: runtime.GetBackgroundPins(state.PinnedApps));

        var foreignWindow = new TrackedWindow(17, 101, "Other app", "other.identity", path, false, false);
        var currentGroups = WindowGrouping.Group([foreignWindow]);
        try
        {
            Check(!Refresh([])[0].IsRunning, "进程扫描返回前窗口刷新应即时完成");
            until(() => Volatile.Read(ref scans) == 1, "应开始后台进程扫描");
            var changedWhileScanning = Refresh(currentGroups);
            Check(changedWhileScanning.Count == 3 && !changedWhileScanning[0].IsRunning,
                "第二块屏幕应能在共享扫描未返回时采用新窗口分组");
        }
        finally
        {
            releaseScan.Set();
        }

        until(() => changes != 0, "后台识别完成后应通知 Dock");
        var afterScan = Refresh(currentGroups);
        Check(!afterScan[0].IsRunning && afterScan[1].IsRunningInBackground,
            "迟到结果不能点亮已由另一 AUMID 窗口认领的可执行文件");
        Check(Refresh([]).All(item => item.IsRunningInBackground) && scans == 1 && changes == 1,
            "当前窗口关闭后应直接复用同一进程快照");

        var cached = runtime.GetBackgroundPins(state.PinnedApps);
        Check(DockPinRules.Move(state, key, 1) && Refresh([]).All(item => item.IsRunningInBackground),
            "固定项排序时应保留所有后台白点");
        win32Pin.IconFile = "updated.png";
        Check(Refresh([]).All(item => item.IsRunningInBackground)
            && ReferenceEquals(cached, runtime.GetBackgroundPins(state.PinnedApps)) && scans == 1,
            "换图或排序应保留同一个已完成的后台匹配和原生扫描");

        Check(DockPinRules.Pin(state, DockPinRules.CreateFromExecutable(addedPath)!), "应能固定第三个应用");
        Refresh([]);
        until(() => Refresh([]).All(item => item.IsRunningInBackground), "新增固定项应从未过期的进程快照匹配");
        Check(scans == 1, "固定项实质变化后仍应尽可能复用两秒内的原生扫描");

        ticks += 2_000;
        Refresh([]);
        until(() => Volatile.Read(ref scans) == 2, "缓存到期后应启动第二次后台扫描");

        TestVisualCachePinTransitions();
        TestUnpinnedTrayScan(until);
    }

    private static void TestUnpinnedTrayScan(Action<Func<bool>, string> until)
    {
        const string weixin = @"D:\Apps\Weixin\Weixin.exe";
        var scans = 0;
        long ticks = 0;
        var runtime = new DockRuntime(() => Interlocked.Increment(ref scans) == 1
            ? [new RunningAppProcess(weixin, null), new RunningAppProcess(@"D:\Apps\Other.exe", null)]
            : [], () => ticks);
        var changes = 0;
        runtime.BackgroundPinsChanged += () => changes++;
        runtime.GetBackgroundPins([]);
        runtime.GetBackgroundPins([]);
        until(() => changes == 1, "没有固定应用时，微信缩到托盘也应通知两块 Dock 刷新");
        Check(scans == 1 && runtime.RunningTrayExecutables.SequenceEqual([weixin]),
            "同一扫描只列出受支持的托盘应用，普通后台程序不占 Dock 位置");
        ticks += 2_000;
        runtime.GetBackgroundPins([]);
        until(() => changes == 2, "微信退出也应通知没有固定项的 Dock");
        Check(runtime.RunningTrayExecutables.Count == 0 && scans == 2, "退出后清除托盘项");
    }

    private static void TestVisualCachePinTransitions()
    {
        const string path = @"C:\Program Files\DockIndicator\DockIndicator.exe";
        var pin = DockPinRules.CreateFromExecutable(path)!;
        pin.IconFile = "custom.png";
        var state = new DockState { PinnedApps = [pin] };
        var window = new TrackedWindow(17, 101, "Dock indicator", null, path, false, false);
        var groups = WindowGrouping.Group([window]);
        var runtime = new DockRuntime(resolveVisual: item => new DockAppVisual(
            null, item.Pinned?.IconFile ?? (item.IsPinned ? "pinned-default" : "running-default")));

        DockItem ComposeItem() => DockItemComposer.Compose(state.PinnedApps, groups).Single();

        var pinned = ComposeItem();
        var customVisual = runtime.ResolveVisualAsync(pinned);
        Check(ReferenceEquals(customVisual, runtime.ResolveVisualAsync(pinned))
            && customVisual.GetAwaiter().GetResult().DisplayName == "custom.png",
            "两个 Dock 应共享已固定应用的自定义图标请求");

        Check(DockPinRules.Unpin(state, DockPinRules.IdentityKey(pin)), "应能取消固定仍在运行的应用");
        var running = ComposeItem();
        var runningVisual = runtime.ResolveVisualAsync(running);
        Check(running.Key == pinned.Key && !ReferenceEquals(customVisual, runningVisual)
            && runningVisual.GetAwaiter().GetResult().DisplayName == "running-default",
            "相同窗口组键取消固定后，应丢弃自定义图标并解析运行项外观");
        Check(ReferenceEquals(runningVisual, runtime.ResolveVisualAsync(running)),
            "另一块屏幕应复用同一个更新后的运行项外观");

        pin.IconFile = null;
        Check(DockPinRules.Pin(state, pin), "应能重新固定仍在运行的应用");
        var pinnedAgain = ComposeItem();
        var pinnedVisual = runtime.ResolveVisualAsync(pinnedAgain);
        Check(pinnedAgain.Key == running.Key && !ReferenceEquals(runningVisual, pinnedVisual)
            && pinnedVisual.GetAwaiter().GetResult().DisplayName == "pinned-default",
            "相同窗口组键重新固定后，应重新解析固定项外观");

        state.PinnedApps[0].IconFile = "new-custom.png";
        var changedVisual = runtime.ResolveVisualAsync(ComposeItem());
        Check(!ReferenceEquals(pinnedVisual, changedVisual)
            && changedVisual.GetAwaiter().GetResult().DisplayName == "new-custom.png",
            "固定项对象原地换图也应按元数据失效");
    }

    private static void TestLayoutAndBadgeUpdates(
        IceBlueDockWindow dock, DockAttention attention, DockItem first, DockItem second)
    {
        var apply = Bind<Action<IReadOnlyList<DockItem>>>(dock, "ApplyItems");
        apply([first, second]);
        var panel = (StackPanel)dock.FindName("ItemPanel");
        var firstButton = (Button)panel.Children[0];
        var badgeLayer = (Grid)IconHost(firstButton).Children[1];
        var originalBadge = badgeLayer.Children[0];
        var originalTooltip = firstButton.ToolTip;

        var refreshedFirst = first with { };
        apply([refreshedFirst, Item(2, "Second window renamed")]);
        Check(ReferenceEquals(ReadButtonItem(firstButton), refreshedFirst), "固定结构刷新后应保留最新交互目标");
        Check(ReferenceEquals(badgeLayer.Children[0], originalBadge)
            && ReferenceEquals(firstButton.ToolTip, originalTooltip), "其他应用变化时，原徽标和提示应保留实例");
        Check(((Button)panel.Children[1]).ToolTip is string tooltip
            && tooltip.Contains("Second window renamed", StringComparison.Ordinal), "变化的窗口标题应更新提示");

        attention.Flashed(1, 1, unread: 8);
        Bind<Action>(dock, "RefreshAttentionBadges")();
        apply([refreshedFirst, second]);
        Check(ReferenceEquals(panel.Children[0], firstButton), "来提醒时应保留当前按钮及悬停状态");
        Check(badgeLayer.Children[0] is Border { Child: TextBlock { Text: "8" } }, "未读计数变化应更新徽标");
        attention.Cleared(1, 1);
        Bind<Action>(dock, "RefreshAttentionBadges")();
        apply([refreshedFirst, second]);
        Check(badgeLayer.Children.Count == 0, "清除提醒应移除徽标");
    }

    private static void TestCurrentLaunchTarget(IceBlueDockWindow dock)
    {
        var firstPin = DockPinRules.CreateFromExecutable(@"D:\Apps\Editor.exe")!;
        firstPin.Arguments = "--layout personal";
        firstPin.WorkingDirectory = @"D:\Personal";
        firstPin.IconFile = "editor.png";
        var apply = Bind<Action<IReadOnlyList<DockItem>>>(dock, "ApplyItems");
        apply(DockItemComposer.Compose([firstPin], []));
        var panel = (StackPanel)dock.FindName("ItemPanel");
        var button = (Button)panel.Children[0];
        var toolTip = new object();
        button.ToolTip = toolTip;

        var newPin = DockPinRules.Copy(firstPin);
        newPin.Arguments = "--layout work";
        newPin.WorkingDirectory = @"D:\Work";
        var refreshedItems = DockItemComposer.Compose([newPin], []);
        apply(refreshedItems);
        var currentItems = typeof(IceBlueDockWindow).GetField("_items", PrivateInstance)!.GetValue(dock);
        Check(ReferenceEquals(panel.Children[0], button) && ReferenceEquals(button.ToolTip, toolTip),
            "启动参数变化时复用原按钮及其视觉内容");
        Check(ReferenceEquals(currentItems, refreshedItems)
            && ReadButtonItem(button)?.LaunchTarget is { Arguments: "--layout work", WorkingDirectory: @"D:\Work" } launchTarget
            && ReferenceEquals(launchTarget, newPin), "视觉相同的固定项替换应刷新按钮和整行使用的启动目标");

        var identicalPin = DockPinRules.Copy(newPin);
        apply(DockItemComposer.Compose([identicalPin], []));
        Check(ReferenceEquals(ReadButtonItem(button)?.Pinned, identicalPin),
            "即使所有字段相同，菜单也应操作当前保存的固定项对象");
    }

    private static void TestRunningDivider()
    {
        var dock = new IceBlueDockWindow(new DockState
        {
            ShowStartMenuButton = false,
            ShowLauncherButton = false,
            ShowDesktopButton = false,
            ShowTaskViewButton = true
        });
        try
        {
            const string appPath = @"D:\Apps\Editor.exe";
            var pin = DockPinRules.CreateFromExecutable(appPath)!;
            var folder = DockPinRules.CreateFromPath(@"D:\Work")!;
            var groups = WindowGrouping.Group([new TrackedWindow(1, 1, "Editor", null, appPath, false, false)]);
            var apply = Bind<Action<IReadOnlyList<DockItem>>>(dock, "ApplyItems");
            var contentLength = Bind<Func<double>>(dock, "ContentLengthDip");
            var panel = (StackPanel)dock.FindName("ItemPanel");

            apply(DockItemComposer.Compose([folder, pin], groups));
            var initialLength = contentLength();
            Check(panel.Children.Count == 4 && panel.Children[1] is Border && panel.Children[2] is Button,
                "默认布局只隔开系统按钮和固定项");

            apply(DockItemComposer.Compose([folder, pin], groups, showRunningPinsSeparately: true));
            Check(panel.Children.Count == 5 && panel.Children[1] is Border && panel.Children[3] is Border
                && ReadButtonItem((Button)panel.Children[4]) is { IsPinned: true, IsInRunningSection: true },
                "顺序及运行状态相同时，切换设置也应绘制运行区分隔线");
            Check(contentLength() == initialLength + DockLayoutMetrics.DividerDip,
                "增加分隔线时应同步更新 Dock 长度");

            apply(DockItemComposer.Compose([pin], groups, showRunningPinsSeparately: true));
            Check(panel.Children.Count == 3 && panel.Children[1] is Border
                && ReadButtonItem((Button)panel.Children[2]) is { IsPinned: true, IsInRunningSection: true },
                "没有固定区图标时只保留系统功能按钮的分隔线");

            apply(DockItemComposer.Compose([folder, pin], groups));
            Check(panel.Children.Count == 4 && contentLength() == initialLength,
                "关闭设置后应恢复固定区和原有宽度");
        }
        finally
        {
            dock.Close();
        }
    }

    private static void TestRunningPinDrag()
    {
        const string appPath = @"D:\Apps\Editor.exe";
        var pin = DockPinRules.CreateFromExecutable(appPath)!;
        var first = DockPinRules.CreateFromPath(@"D:\Work")!;
        var last = DockPinRules.CreateFromPath(@"D:\Personal")!;
        var state = new DockState
        {
            ShowStartMenuButton = false,
            ShowLauncherButton = false,
            ShowDesktopButton = false,
            ShowRunningPinsSeparately = true,
            PinnedApps = [first, pin, last]
        };
        var dock = new IceBlueDockWindow(state);
        try
        {
            var groups = WindowGrouping.Group([new TrackedWindow(1, 1, "Editor", null, appPath, false, false)]);
            Bind<Action<IReadOnlyList<DockItem>>>(dock, "ApplyItems")(
                DockItemComposer.Compose(state.PinnedApps, groups, showRunningPinsSeparately: true));
            var panel = (StackPanel)dock.FindName("ItemPanel");
            var runningButton = (Button)panel.Children[3];
            var fixedButton = (Button)panel.Children[0];
            var surface = (Border)dock.FindName("DockSurface");
            var drag = new DataObject("BingLan.DockPin", DockPinRules.IdentityKey(pin));
            drag.SetData("BingLan.DockRunningPin", true);

            foreach (var invalidTarget in new UIElement[] { runningButton, surface })
            {
                Check(RaiseDockDrag(invalidTarget, drag, DragDrop.DragOverEvent) == DragDropEffects.None,
                    "拖动运行中的固定项时，运行区和空白处应拒绝排序");
                Check(RaiseDockDrag(invalidTarget, drag, DragDrop.DropEvent) == DragDropEffects.None
                    && state.PinnedApps.SequenceEqual([first, pin, last]),
                    "原地放下与空白处放下均应保留退出后的归位顺序");
            }

            Check(RaiseDockDrag(fixedButton, drag, DragDrop.DragOverEvent) == DragDropEffects.Move,
                "固定区的图标应接受来自运行区的固定项排序");
            RaiseDockDrag(fixedButton, drag, DragDrop.DropEvent);
            Check(state.PinnedApps.SequenceEqual([pin, first, last]), "拖到固定区图标前应更新归位顺序");
        }
        finally
        {
            dock.Close();
        }
    }

    private static DragDropEffects RaiseDockDrag(UIElement target, DataObject data, RoutedEvent routedEvent)
    {
        var args = (DragEventArgs?)Activator.CreateInstance(typeof(DragEventArgs), PrivateInstance, binder: null,
            args: [data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, target, new Point()],
            culture: null) ?? throw new InvalidOperationException("无法创建 WPF 拖放事件");
        args.RoutedEvent = routedEvent;
        target.RaiseEvent(args);
        return args.Effects;
    }

    private static DockItem? ReadButtonItem(Button button) =>
        (button as IceBlueDockWindow.DockItemButton)?.Item;

    private static void TestLatestPointerSample(IceBlueDockWindow dock)
    {
        var panel = (StackPanel)dock.FindName("ItemPanel");
        panel.Measure(new Size(double.PositiveInfinity, 100));
        panel.Arrange(new Rect(0, 0, panel.DesiredSize.Width, 100));
        var firstButton = (Button)panel.Children[0];
        var secondButton = (Button)panel.Children[1];
        var firstScale = IconScale(firstButton);
        var secondScale = IconScale(secondButton);
        var queue = Bind<Action<double, double>>(dock, "QueueMagnificationTarget");
        var step = Bind<Action>(dock, "StepMagnification");
        var stop = Bind<Action<bool>>(dock, "StopMagnification");

        queue(CenterX(firstButton), 1.5d);
        queue(CenterX(secondButton), 1.5d);
        Check(firstScale.ScaleX == 1d && secondScale.ScaleX == 1d, "目标应在下一渲染帧应用");
        step();
        Check(secondScale.ScaleX > firstScale.ScaleX && firstScale.ScaleX >= 1d,
            "同一帧多个位置只应应用最后一个位置");

        queue(CenterX(firstButton), 1.5d);
        stop(false);
        step();
        Check(firstScale.ScaleX == 1d && secondScale.ScaleX == 1d, "取消应丢弃尚未应用的位置并还原缩放");
    }

    private static DockItem Item(int id, string title)
    {
        var key = $"pid:{id}";
        var name = $"App {id}";
        var window = new TrackedWindow(id, (uint)id, title, null, null, false, false);
        return new DockItem(key, name, null, new WindowGroup(key, name, [window]));
    }

    private static Grid IconHost(Button button) => (Grid)((StackPanel)button.Content).Children[0];

    private static ScaleTransform IconScale(Button button) =>
        (ScaleTransform)((TransformGroup)IconHost(button).Children[0].RenderTransform).Children[0];

    private static double CenterX(Button button) => VisualTreeHelper.GetOffset(button).X + button.ActualWidth / 2d;

    private static T Bind<T>(IceBlueDockWindow dock, string name) where T : Delegate =>
        typeof(IceBlueDockWindow).GetMethod(name, PrivateInstance)!.CreateDelegate<T>(dock);

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
