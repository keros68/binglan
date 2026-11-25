using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using BingLan.App;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.App.Themes;
using BingLan.App.Windows;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Services;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Image = System.Windows.Controls.Image;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBox = System.Windows.Controls.TextBox;

internal static class MemoryOptimizationTests
{
    internal static void Run(Action<Window> show, Action pump, Action<Func<bool>, string> until)
    {
        TestDynamicDragBitmapCacheAndFrozenShadows(show, pump, until);
        TestShellIconDeduplicationWeakCacheArrayPoolAndCacheEviction();
        TestTodoSharedContextMenuDisconnectedViewsAndSelectionRoundTrip(show, pump, until);
        TestLazySettingsFontPreservationOverlaysBackdropAndGcPolicy(show, pump, until);
    }

    private static void TestDynamicDragBitmapCacheAndFrozenShadows(
        Action<Window> show,
        Action pump,
        Action<Func<bool>, string> until)
    {
        var window = new TodoWidgetWindow(new TodoWidgetState
        {
            Placement = new WindowPlacement { Left = 220, Top = 220, Width = 300, Height = 320 }
        });
        show(window);
        try
        {
            var content = window.Content as UIElement
                ?? throw new InvalidOperationException("卡片内容应为 UIElement");
            Check(content.CacheMode is null, "静止卡片不应常驻 BitmapCache 离屏缓存");

            // Plain click without exceeding drag threshold must NOT allocate BitmapCache.
            Check(window.BeginDrag(900, 500), "卡片应能进入拖拽预处理态");
            Check(content.CacheMode is null, "普通单击（未超过移动阈值）不应提前开启 BitmapCache");
            window.EndDrag();
            Check(content.CacheMode is null, "单击结束时应保持无 BitmapCache");

            // Edge resize drag must NOT allocate BitmapCache (resizing invalidates offscreen cache every frame).
            Check(window.BeginDrag(900, 500, ResizeEdges.Right), "卡片应能进入边缘缩放态");
            window.DragTo(920, 500);
            Check(content.CacheMode is null, "边缘缩放期间不应开启 BitmapCache");
            window.EndDrag();
            Check(content.CacheMode is null, "缩放结束时应保持无 BitmapCache");

            // Actual move drag past threshold enables BitmapCache and releases it after 500ms debounce.
            Check(window.BeginDrag(900, 500), "卡片应能正常开始移动拖拽");
            window.DragTo(920, 510);
            Check(content.CacheMode is BitmapCache, "卡片实际移动拖拽期间应按需开启 BitmapCache");

            window.EndDrag();
            until(() => content.CacheMode is null, "拖拽结束后应在 500ms 去抖后释放 BitmapCache");

            var softA = WidgetWindowBase.CreateCardShadowEffect(CardShadow.Soft);
            var softB = WidgetWindowBase.CreateCardShadowEffect(CardShadow.Soft);
            var strongA = WidgetWindowBase.CreateCardShadowEffect(CardShadow.Strong);
            var strongB = WidgetWindowBase.CreateCardShadowEffect(CardShadow.Strong);
            Check(softA is not null && softA.IsFrozen && ReferenceEquals(softA, softB),
                "Soft 卡片阴影应冻结并跨卡片共享同一实例");
            Check(strongA is not null && strongA.IsFrozen && ReferenceEquals(strongA, strongB),
                "Strong 卡片阴影应冻结并跨卡片共享同一实例");
            Check(WidgetWindowBase.CreateCardShadowEffect(CardShadow.None) is null,
                "CardShadow.None 不应分配阴影特效");

            var nakedA = QuickPlacesWindow.CreateNakedTextContrastShadow();
            var nakedB = QuickPlacesWindow.CreateNakedTextContrastShadow();
            Check(nakedA.IsFrozen && ReferenceEquals(nakedA, nakedB),
                "NakedTextContrastShadow 应冻结并复用共享实例");

            if (System.Windows.Application.Current?.TryFindResource("CardPopupDropShadow") is DropShadowEffect popupA
                && System.Windows.Application.Current.TryFindResource("CardPopupDropShadow") is DropShadowEffect popupB)
            {
                Check(popupA.IsFrozen && ReferenceEquals(popupA, popupB),
                    "CardPopupDropShadow 应冻结并复用共享实例");
            }
        }
        finally
        {
            window.CanClose = true;
            window.Close();
            pump();
        }
    }

    private static void TestShellIconDeduplicationWeakCacheArrayPoolAndCacheEviction()
    {
        ShellIconProvider.ClearIconCache();
        LauncherIconCache.Clear();
        try
        {
            // Find two distinct valid system image list icon indices.
            var validIndices = new List<int>();
            for (var index = 0; index < 32 && validIndices.Count < 2; index++)
            {
                if (ShellIconProvider.FromSystemImageList(index) is BitmapSource { IsFrozen: true })
                {
                    validIndices.Add(index);
                }
            }
            ShellIconProvider.ClearIconCache();
            Check(validIndices.Count >= 1, "系统图像列表应返回有效的冻结图标");
            var validIndex = validIndices[0];

            // While strongly held, repeated queries return the exact same frozen BitmapSource.
            var first = ShellIconProvider.FromSystemImageList(validIndex);
            Check(first is BitmapSource firstBitmap && firstBitmap.IsFrozen,
                "系统图像列表应返回冻结的 BitmapSource 图标");
            var second = ShellIconProvider.FromSystemImageList(validIndex);
            Check(ReferenceEquals(first, second),
                "相同 sysIconIndex 必须命中去重缓存并返回同一冻结 BitmapSource 引用");
            Check(ShellIconProvider.CachedSystemIconCount >= 1,
                "系统图标缓存应记录已去重的存活索引");

            // Explorer restart helper clears both icon caches in one shot.
            _ = LauncherIconCache.GetOrLoadSync("explorer-restart-probe", () => first);
            Check(LauncherIconCache.Count == 1, "测试前 LauncherIconCache 应含 1 条图标");
            WidgetCoordinator.ClearProcessIconCachesOnExplorerRestart();
            Check(ShellIconProvider.CachedSystemIconCount == 0 && LauncherIconCache.Count == 0,
                "Explorer 重启清理应一次性清空两级图标缓存");

            var reloaded = ShellIconProvider.FromSystemImageList(validIndex);
            Check(reloaded is BitmapSource { IsFrozen: true } && !ReferenceEquals(first, reloaded),
                "清空缓存后再次读取同 sysIconIndex 应重新提取冻结图标");
            first = null;
            second = null;
            reloaded = null;

            // M2: WeakReference system icon cache must allow GC collection once strong references drop.
            // Check liveness in a NoInlining helper so C#'s `out _` local does not stay rooted on this frame.
            ShellIconProvider.ClearIconCache();
            var weakIcon = LoadSystemIconWeakRef(validIndex);
            for (var round = 0; round < 6 && IsWeakTargetAlive(weakIcon); round++)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
            Check(!IsWeakTargetAlive(weakIcon),
                "ShellIconProvider 系统图标弱引用缓存不得长期钉住已无人引用的位图");

            // L5: InvalidateIconForPath removes only the target path's sysIconIndex, not unrelated entries.
            ShellIconProvider.ClearIconCache();
            var notepadPath = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            var systemDir = Environment.SystemDirectory;
            if (File.Exists(notepadPath) && Directory.Exists(systemDir))
            {
                var fileIcon = ShellIconProvider.GetIcon(notepadPath);
                var dirIcon = ShellIconProvider.GetIcon(systemDir);
                if (fileIcon is not null && dirIcon is not null && !ReferenceEquals(fileIcon, dirIcon))
                {
                    ShellIconProvider.InvalidateIconForPath(notepadPath);
                    var dirIconAfter = ShellIconProvider.GetIcon(systemDir);
                    Check(ReferenceEquals(dirIcon, dirIconAfter),
                        "单条目 InvalidateIconForPath 不应误清其他无关路径的 sysIconIndex 缓存");
                }
            }

            // Verify HalveJumbo with ArrayPool produces exact pixels and never aliases across calls.
            var solidA = CreateSolidJumbo(b: 20, g: 40, r: 60, a: 255);
            var solidB = CreateSolidJumbo(b: 200, g: 150, r: 100, a: 255);
            var halfA = ShellIconProvider.HalveJumbo(solidA);
            var halfB = ShellIconProvider.HalveJumbo(solidB);
            Check(halfA.PixelWidth == 128 && halfA.PixelHeight == 128, "HalveJumbo 应输出 128×128 位图");
            Check(halfB.PixelWidth == 128 && halfB.PixelHeight == 128, "二次 HalveJumbo 应输出 128×128 位图");
            VerifyPixel(halfA, 0, 0, b: 20, g: 40, r: 60, a: 255, "首次缩图首像素应精确保留颜色且不被复用池污染");
            VerifyPixel(halfA, 127, 127, b: 20, g: 40, r: 60, a: 255, "首次缩图末像素应精确保留颜色且不被复用池污染");
            VerifyPixel(halfB, 64, 64, b: 200, g: 150, r: 100, a: 255, "二次缩图像素应精确匹配新源图");

            var placeholderJumbo = CreateTopLeftQuarterJumbo();
            Check(ShellIconProvider.IsJumboPlaceholder(placeholderJumbo),
                "仅左上 1/4 有像素的 256×256 图标应判定为 Jumbo 占位图");
            Check(!ShellIconProvider.IsJumboPlaceholder(solidA),
                "全幅不透明的 256×256 图标不应误判为 Jumbo 占位图");

            // L2: Distinct FileBox key namespace avoids shadowing 48px DockAppResolver entries, and
            // GetOrLoadSync must NOT drop an active concurrent GetOrLoadAsync Inflight entry.
            halfA.Freeze();
            halfB.Freeze();
            Check(!string.Equals(LauncherIconCache.KeyForFileBoxPath(@"C:\test.exe"), "C:\\test.exe\0\0", StringComparison.Ordinal),
                "FileBox 缓存键命名空间应与总览卡可能含 48px DockAppResolver 的键隔离");

            LauncherIconCache.Clear();
            var inflightGate = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var asyncLoaderCalls = 0;
            var task1 = LauncherIconCache.GetOrLoadAsync("inflight-collision-key", () =>
            {
                asyncLoaderCalls++;
                return inflightGate.Task;
            });
            _ = LauncherIconCache.GetOrLoadSync("inflight-collision-key", () => halfA);
            // A subsequent async caller before task1 finishes now hits Icons directly or joins inflight,
            // never launching a second duplicate loader.
            var task2 = LauncherIconCache.GetOrLoadAsync("inflight-collision-key", () =>
            {
                asyncLoaderCalls++;
                return Task.FromResult<ImageSource?>(halfB);
            });
            inflightGate.SetResult(halfB);
            var r1 = task1.GetAwaiter().GetResult();
            var r2 = task2.GetAwaiter().GetResult();
            Check(asyncLoaderCalls == 1 && ReferenceEquals(r1, halfA) && ReferenceEquals(r2, halfA),
                "GetOrLoadSync 不应破坏并发 GetOrLoadAsync 去重，且应收敛到同一规范实例");

            // Stale inflight completion after Clear() must NOT backfill icons from before Explorer restart.
            LauncherIconCache.Clear();
            var staleGate = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var staleTask = LauncherIconCache.GetOrLoadAsync("stale-after-clear-key", () => staleGate.Task);
            LauncherIconCache.Clear();
            staleGate.SetResult(halfA);
            _ = staleTask.GetAwaiter().GetResult();
            Check(!LauncherIconCache.TryGet("stale-after-clear-key", out _),
                "Clear() 之后晚到的旧在途图标不得回填缓存");

            var fullScreenTrim = WidgetCoordinator.EvaluateIdleMemoryTrim(
                nowTick: 120_000,
                lastTrimTick: 0,
                isDragging: false,
                isFullScreenOrGameMode: true);
            Check(!fullScreenTrim.ShouldRun && fullScreenTrim.RetryAfter == WidgetCoordinator.FullScreenDeferralRetryDelay,
                "全屏或游戏免打扰期间应延后空闲压缩");

            // Verify LauncherIconCache 256 cap, LRU eviction, and 5-minute idle expiration.
            LauncherIconCache.Clear();
            var totalInserted = LauncherIconCache.MaxEntries + 44;
            for (var i = 0; i < totalInserted; i++)
            {
                _ = LauncherIconCache.GetOrLoadSync($"mem-opt-cap-{i}", () => halfA);
            }

            Check(LauncherIconCache.Count == LauncherIconCache.MaxEntries,
                $"LauncherIconCache 条目数应封顶在 {LauncherIconCache.MaxEntries}，实际 {LauncherIconCache.Count}");

            var hitLoaderCalls = 0;
            var recent = LauncherIconCache.GetOrLoadSync(
                $"mem-opt-cap-{totalInserted - 1}",
                () =>
                {
                    hitLoaderCalls++;
                    return halfA;
                });
            Check(hitLoaderCalls == 0 && ReferenceEquals(recent, halfA),
                "最近访问的图标应保留在 LRU 缓存中");

            var evictedLoaderCalls = 0;
            _ = LauncherIconCache.GetOrLoadSync(
                "mem-opt-cap-0",
                () =>
                {
                    evictedLoaderCalls++;
                    return halfA;
                });
            Check(evictedLoaderCalls == 1, "超出上限后最久未访问条目应被 LRU 驱逐");

            var futureTick = Environment.TickCount64 + (long)TimeSpan.FromMinutes(10).TotalMilliseconds;
            var removed = LauncherIconCache.TrimExpiredEntries(futureTick, TimeSpan.FromMinutes(5));
            Check(removed == LauncherIconCache.MaxEntries && LauncherIconCache.Count == 0,
                "超过闲置过期阈值的图标缓存应全部被驱逐");
        }
        finally
        {
            ShellIconProvider.ClearIconCache();
            LauncherIconCache.Clear();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<ImageSource> LoadSystemIconWeakRef(int index)
    {
        var icon = ShellIconProvider.FromSystemImageList(index)
            ?? throw new InvalidOperationException($"系统图像列表索引 {index} 未返回图标");
        return new WeakReference<ImageSource>(icon);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsWeakTargetAlive<T>(WeakReference<T> reference) where T : class =>
        reference.TryGetTarget(out _);

    private static void TestTodoSharedContextMenuDisconnectedViewsAndSelectionRoundTrip(
        Action<Window> show,
        Action pump,
        Action<Func<bool>, string> until)
    {
        var state = new TodoWidgetState { WeeklyView = false };
        TodoService.Add(state, "共享菜单条目一", DateTime.Today);
        TodoService.Add(state, "共享菜单条目二", DateTime.Today);
        TodoService.Add(state, "共享菜单条目三", DateTime.Today);

        var window = new TodoWidgetWindow(state);
        show(window);
        try
        {
            var todoList = Require<ItemsControl>(window, "TodoList");
            var weekRows = Require<ItemsControl>(window, "WeekRows");
            Check(todoList.ItemsSource is not null && weekRows.ItemsSource is null,
                "列表视图下周历 ItemsSource 应断开为 null");

            var rowBorders = FindVisualChildren<Border>(todoList)
                .Where(border => border.Name == "TodoRowBorder")
                .ToList();
            var textBoxes = FindVisualChildren<TextBox>(todoList).ToList();
            Check(rowBorders.Count == 3 && textBoxes.Count == 3, "应渲染 3 条今日待办行与文本框");

            var sharedMenu = rowBorders[0].ContextMenu
                ?? throw new InvalidOperationException("待办行应绑定共享 ContextMenu");
            Check(rowBorders.All(border => ReferenceEquals(border.ContextMenu, sharedMenu)),
                "所有列表行必须共享同一个 ContextMenu 实例");
            Check(textBoxes.All(box => ReferenceEquals(box.ContextMenu, sharedMenu)),
                "所有列表文本框必须复用同一个 ContextMenu 实例");

            var fixedItemCount = sharedMenu.Items.Count;
            for (var round = 0; round < 3; round++)
            {
                var targetBox = textBoxes[round % textBoxes.Count];
                sharedMenu.PlacementTarget = targetBox;
                sharedMenu.IsOpen = true;
                pump();
                Check(sharedMenu.Items.Count == fixedItemCount,
                    "多次右键文本框打开共享菜单不应累加重复 MenuItem");
                var editing = sharedMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "TodoTextEditing"));
                Check(editing.Visibility == Visibility.Visible,
                    "文本框打开共享菜单时应显示“编辑文本”子菜单");
                Check(editing.Items.OfType<MenuItem>().All(sub => ReferenceEquals(sub.CommandTarget, targetBox)),
                    "编辑子命令应指向当前右键的文本框");
                var listOnlyItems = sharedMenu.Items.OfType<FrameworkElement>()
                    .Where(element => Equals(element.Tag, "ListOnly"))
                    .ToList();
                Check(listOnlyItems.Count > 0 && listOnlyItems.All(element => element.Visibility == Visibility.Visible),
                    "列表视图打开共享菜单时应显示列表专属入口");
                var moveTo = sharedMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "MoveToDate"));
                Check(moveTo.Visibility == Visibility.Collapsed,
                    "列表视图打开共享菜单时应隐藏周历“移到”入口");
                sharedMenu.IsOpen = false;
                pump();

                var targetRow = rowBorders[round % rowBorders.Count];
                sharedMenu.PlacementTarget = targetRow;
                sharedMenu.IsOpen = true;
                pump();
                Check(sharedMenu.Items.Count == fixedItemCount,
                    "多次右键行容器打开共享菜单不应改变菜单项数量");
                Check(editing.Visibility == Visibility.Collapsed,
                    "行容器打开共享菜单时应隐藏“编辑文本”子菜单");
                sharedMenu.IsOpen = false;
                pump();
            }

            var editingAfterClose = sharedMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "TodoTextEditing"));
            until(() => editingAfterClose.Items.OfType<MenuItem>().All(sub => sub.CommandTarget is null),
                "共享菜单关闭并空闲后应清空 CommandTarget 引用");

            // Switch to weekly view: list ItemsSource disconnects, weekly rows reuse the same ContextMenu.
            Require<Button>(window, "ViewModeButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            pump();
            Check(todoList.ItemsSource is null && weekRows.ItemsSource is not null,
                "切到周历视图后隐藏的列表视图 ItemsSource 应断开为 null");

            var weekItemBorders = FindVisualChildren<Border>(weekRows)
                .Where(border => border.DataContext is TodoItemState && border.ContextMenu is not null)
                .ToList();
            Check(weekItemBorders.Count >= 2, "周历今日单元格应渲染待办条目");
            Check(weekItemBorders.All(border => ReferenceEquals(border.ContextMenu, sharedMenu)),
                "周历单元格条目应与列表条目共享同一个 ContextMenu 实例");

            for (var round = 0; round < 2; round++)
            {
                sharedMenu.PlacementTarget = weekItemBorders[0];
                sharedMenu.IsOpen = true;
                pump();
                Check(sharedMenu.Items.Count == fixedItemCount,
                    "周历条目多次打开共享菜单不应累加重复“移到”菜单项");
                var moveTo = sharedMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "MoveToDate"));
                Check(moveTo.Visibility == Visibility.Visible && moveTo.Items.Count == 3,
                    "周历有日期条目打开共享菜单应显示 3 个目标日期子项");
                Check(sharedMenu.Items.OfType<FrameworkElement>()
                    .Where(element => Equals(element.Tag, "ListOnly"))
                    .All(element => element.Visibility == Visibility.Collapsed),
                    "周历视图打开共享菜单应隐藏列表专属入口");
                sharedMenu.IsOpen = false;
                pump();
            }

            // M4: Select a non-today date cell, switch Weekly -> List -> Weekly, and verify selection restores.
            var targetDate = DateTime.Today.AddDays(1);
            var targetCell = weekRows.Items.OfType<WeekRowView>()
                .SelectMany(row => row.Days)
                .Single(cell => cell.Date == targetDate);
            typeof(TodoWidgetWindow)
                .GetMethod("SelectCell", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [targetCell]);
            Check(targetCell.IsSelected, "周历目标日期格应处于选中状态");

            // Switch back to list view: weekly ItemsSource disconnects cleanly.
            Require<Button>(window, "ViewModeButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            pump();
            Check(todoList.ItemsSource is not null && weekRows.ItemsSource is null,
                "切回列表视图后隐藏的周历视图 ItemsSource 应断开为 null");

            // Switch back to weekly view: previously selected date cell must be restored.
            Require<Button>(window, "ViewModeButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            pump();
            var restoredCell = weekRows.Items.OfType<WeekRowView>()
                .SelectMany(row => row.Days)
                .Single(cell => cell.Date == targetDate);
            Check(restoredCell.IsSelected,
                "周历 → 列表 → 周历往返后应恢复原先选中的日期格，不应丢失为未选中");
        }
        finally
        {
            window.CanClose = true;
            window.Close();
            pump();
        }
    }

    private static void TestLazySettingsFontPreservationOverlaysBackdropAndGcPolicy(
        Action<Window> show,
        Action pump,
        Action<Func<bool>, string> until)
    {
        // M5: Idle GC trim policy enforces 60s minimum interval by rescheduling (never dropping) and defers while dragging.
        var (firstRun, firstRetry) = WidgetCoordinator.EvaluateIdleMemoryTrim(nowTick: 10_000, lastTrimTick: 0, isDragging: false);
        Check(firstRun && firstRetry is null, "首次闲置 GC 紧缩应允许立即执行");
        var (dragRun, dragRetry) = WidgetCoordinator.EvaluateIdleMemoryTrim(nowTick: 100_000, lastTrimTick: 10_000, isDragging: true);
        Check(!dragRun && dragRetry == TimeSpan.FromSeconds(5), "拖动卡片期间必须推迟闲置 GC 紧缩并延后 5 秒重试");
        var (cooldownRun, cooldownRetry) = WidgetCoordinator.EvaluateIdleMemoryTrim(nowTick: 69_000, lastTrimTick: 10_000, isDragging: false);
        Check(!cooldownRun && cooldownRetry == TimeSpan.FromSeconds(2),
            $"60 秒冷却期内（剩余 1 秒）不应静默丢弃回收，应顺延 剩余+1s（2 秒），实际 {cooldownRetry}");
        var (afterCooldownRun, afterCooldownRetry) = WidgetCoordinator.EvaluateIdleMemoryTrim(nowTick: 70_000, lastTrimTick: 10_000, isDragging: false);
        Check(afterCooldownRun && afterCooldownRetry is null, "达到 60 秒最小间隔后应允许再次执行闲置 GC 紧缩");

        // P1-3c: ThemePreviewRenderer returns frozen BitmapSource directly.
        var presetImage = ThemePreviewRenderer.RenderPreset(
            DesktopLayoutPreset.QuietInformation,
            DesktopExperienceRules.CreateDefault(),
            new Rect(0, 0, 1920, 1080));
        Check(presetImage is BitmapSource { IsFrozen: true, PixelWidth: > 0, PixelHeight: > 0 },
            "ThemePreviewRenderer.RenderPreset 应直接返回冻结的位图");

        // P1-3a & M3: Wallpaper decode width calculation, cancellation safety, and unconditional release timer.
        Check(LauncherDesktopBackdropService.ComputeTargetDecodeWidth(3840, 2160, 1920, 1080) == 1920,
            "4K 壁纸在 1080p 屏幕应按 1920 宽降采样解码");
        Check(LauncherDesktopBackdropService.ComputeTargetDecodeWidth(7680, 4320, 2560, 1440) == 2560,
            "8K 壁纸在 2K 屏幕应按 2560 宽降采样解码");
        Check(LauncherDesktopBackdropService.ComputeTargetDecodeWidth(1280, 720, 1920, 1080) == 0,
            "小于屏幕分辨率的壁纸不应放大解码");

        var tempWallpaper = Path.Combine(Path.GetTempPath(), $"binglan-wallpaper-test-{Guid.NewGuid():N}.png");
        try
        {
            SaveSolidPng(tempWallpaper, 64, 64);
            var monitorRect = new PixelRect(0, 0, 64, 64);
            var panelRect = new PixelRect(8, 8, 48, 48);

            LauncherDesktopBackdropService.ReleaseWallpaperCache();
            // Arming release timer when nothing is pinned must not throw or short-circuit.
            LauncherDesktopBackdropService.ScheduleReleaseWallpaperCache(TimeSpan.FromMilliseconds(20));

            var crop = LauncherDesktopBackdropService.TryLoadWallpaperCropFromFileForTesting(
                tempWallpaper, monitorRect, panelRect, CancellationToken.None);
            Check(crop is { IsFrozen: true, PixelWidth: > 0, PixelHeight: > 0 }
                && LauncherDesktopBackdropService.HasPinnedWallpaper,
                "壁纸加载后应返回冻结裁切位图并暂驻强引用");

            LauncherDesktopBackdropService.ScheduleReleaseWallpaperCache(TimeSpan.FromMilliseconds(30));
            until(() => !LauncherDesktopBackdropService.HasPinnedWallpaper,
                "面板关闭调度释放后应在延迟到期时解除壁纸强引用");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var threwCancel = false;
            try
            {
                _ = LauncherDesktopBackdropService.TryLoadWallpaperCropFromFileForTesting(
                    tempWallpaper, monitorRect, panelRect, cancelled.Token);
            }
            catch (OperationCanceledException)
            {
                threwCancel = true;
            }
            Check(threwCancel && !LauncherDesktopBackdropService.HasPinnedWallpaper,
                "已取消的壁纸加载即使命中缓存也不得重新钉住 _pinnedWallpaper");
        }
        finally
        {
            LauncherDesktopBackdropService.ReleaseWallpaperCache();
            if (File.Exists(tempWallpaper))
            {
                File.Delete(tempWallpaper);
            }
        }

        // P1-3b & L6: SnapGridOverlay and SnapGuideOverlay idle disposal.
        var monitor = new MonitorSnapshot(
            0,
            "primary",
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1040),
            true,
            96u);
        try
        {
            SnapGridOverlay.ShowGrid(monitor, 24);
            pump();
            Check(SnapGridOverlay.HasCachedWindow, "显示吸附网格后应创建覆盖窗");
            SnapGridOverlay.HideGrid();
            SnapGridOverlay.DisposeHiddenOverlay();
            pump();
            Check(!SnapGridOverlay.HasCachedWindow, "隐藏吸附网格闲置回收后应关闭并释放窗口");

            SnapGuideOverlay.ShowGuides(monitor, [new SnapGuide(100, 100, 100, 400, SnapGuideKind.Align)]);
            pump();
            Check(SnapGuideOverlay.CachedWindowCount == 1, "显示对齐线后应创建 1 个覆盖窗");
            SnapGuideOverlay.ShowGuides(monitor, []);
            SnapGuideOverlay.ShowGuides(monitor, []);
            SnapGuideOverlay.HideGuides();
            SnapGuideOverlay.DisposeHiddenOverlays();
            pump();
            Check(SnapGuideOverlay.CachedWindowCount == 0, "隐藏对齐线闲置回收后应关闭并清空窗口池");
        }
        finally
        {
            SnapGridOverlay.HideGrid();
            SnapGridOverlay.DisposeHiddenOverlay();
            SnapGuideOverlay.HideGuides();
            SnapGuideOverlay.DisposeHiddenOverlays();
        }

        // P1-3d & H2: Lazy SearchIndex & off-page preset previews, while preserving legacy/cased FontFamily on edit.
        var state = LocalStateStore.CreateDefault();
        state.DesktopExperience.GetComponent(DesktopComponentKind.TimeDate).FontFamily = "Google Sans";
        DesktopExperienceState? committedExperience = null;
        var info = new InformationWidgetState();
        var settings = new SettingsWindow(
            new CityLookupService(),
            info,
            state.DesktopExperience,
            (_, _) => { },
            _ => { },
            updated => committedExperience = updated,
            dockState: state.Dock,
            maintenance: new SettingsMaintenance
            {
                Style = state.Style,
                GetLastSettingsPage = () => SettingsNavigationRules.GeneralPageKey
            });
        show(settings);
        try
        {
            Check(!settings.HasBuiltSearchIndex, "设置窗口打开后未搜索时不应提前构建搜索索引");
            Check(Require<Image>(settings, "PresetPreviewImage").Source is null,
                "打开通用设置页时不应提前渲染 5 张布局预设预览图");

            // H2: Switching to ComponentsPage and editing opacity must NOT wipe legacy-mapped FontFamily to null.
            settings.ShowSettingsPage(SettingsNavigationRules.ComponentsPageKey);
            pump();
            var fontPicker = Require<ComboBox>(settings, "SelectedComponentFontPicker");
            var expectedResolved = InstalledFontCatalog.Resolve("Google Sans");
            Check(Equals(fontPicker.SelectedItem, expectedResolved),
                $"旧字体 Google Sans 应正确映射并选中 {expectedResolved}，实际 {fontPicker.SelectedItem ?? "<null>"}");

            Require<Slider>(settings, "SelectedComponentOpacitySlider").Value = 0.65;
            settings.CommitPendingChanges();
            Check(committedExperience is not null
                && string.Equals(
                    committedExperience.GetComponent(DesktopComponentKind.TimeDate).FontFamily,
                    expectedResolved,
                    StringComparison.Ordinal),
                "修改组件其他外观属性时不得清空原有字体配置");

            settings.ShowSettingsPage(SettingsNavigationRules.SchemePageKey);
            until(() => Require<Image>(settings, "PresetPreviewImage").Source is BitmapSource { IsFrozen: true },
                "切到桌面方案页时应按需渲染冻结布局预览图");

            var entries = settings.SearchIndex;
            Check(settings.HasBuiltSearchIndex && entries.Count > 0,
                "首次访问搜索索引应按需构建有效结果");
        }
        finally
        {
            settings.Close();
            pump();
        }
    }

    private static void SaveSolidPng(string path, int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 240;
            pixels[i + 1] = 180;
            pixels[i + 2] = 120;
            pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static BitmapSource CreateSolidJumbo(byte b, byte g, byte r, byte a)
    {
        const int size = 256;
        const int stride = size * 4;
        var pixels = new byte[stride * size];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = a;
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateTopLeftQuarterJumbo()
    {
        const int size = 256;
        const int stride = size * 4;
        var pixels = new byte[stride * size];
        for (var y = 0; y < 32; y++)
        {
            var row = y * stride;
            for (var x = 0; x < 32; x++)
            {
                var offset = row + x * 4;
                pixels[offset] = 255;
                pixels[offset + 1] = 255;
                pixels[offset + 2] = 255;
                pixels[offset + 3] = 255;
            }
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static void VerifyPixel(
        BitmapSource bitmap,
        int x,
        int y,
        byte b,
        byte g,
        byte r,
        byte a,
        string message)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        Check(pixel[0] == b && pixel[1] == g && pixel[2] == r && pixel[3] == a,
            $"{message}（期望 BGRA={b},{g},{r},{a}，实际 {pixel[0]},{pixel[1]},{pixel[2]},{pixel[3]}）");
    }

    private static T Require<T>(FrameworkElement root, string name) where T : class =>
        root.FindName(name) as T
        ?? throw new InvalidOperationException($"未找到控件：{name}");

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
