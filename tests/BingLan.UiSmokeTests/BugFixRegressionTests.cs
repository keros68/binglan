using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BingLan.App;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.App.Taskbar;
using BingLan.App.Windows;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.TopBar;

/// <summary>
/// Regression tests for PR-2 fixes: background WinEvent pump & non-Explorer event burst
/// filtering, LauncherPanelWindow GC collection after close, bad-directory watcher resilience,
/// mixed-DPI + negative-coordinate placement, WidgetCoordinator.Dispose cleanup, and
/// UiDiagnostics untracked description text.
/// </summary>
internal static class BugFixRegressionTests
{
    internal static void Run(Action<Window> show, Action pump, Action<Func<bool>, string> pumpUntil)
    {
        TestBackgroundWinEventPumpLifecycle();
        TestDesktopSurfaceWatcherNormalModeHasNoGlobalShowHookAndFiltersStorms(show, pump);
        TestTaskbarAdapterFiltersChildWinEvents(show, pump);
        TestLauncherPanelWindowCollectsAfterClose(show, pump);
        TestFileMappingWatchServiceSkipsBadDirectoryAndWatchesValidOnes(pump, pumpUntil);
        TestMixedDpiAndNegativeOffsetPlacementRules();
        TestBoxMarqueePhysicalPlacement(pump, pumpUntil);
        TestWidgetCoordinatorDisposeStopsTimerAndDisposesServices(pump, pumpUntil);
        TestUiDiagnosticsUntrackedDescription();
        TestUiDiagnosticsKeepsSystemEventsHealthy(pump, pumpUntil);
    }

    private static void TestBackgroundWinEventPumpLifecycle()
    {
        using var pump = new BackgroundWinEventPump();
        Check(!pump.IsRunning && pump.ActiveHookCount == 0, "零订阅时后台 WinEvent 泵不应启动线程");

        var sub = pump.Subscribe(
            DockNativeMethods.EventSystemForeground,
            DockNativeMethods.EventSystemForeground,
            static m => m.ChildId == 0,
            static _ => { });
        try
        {
            Check(sub.IsActive, "有效订阅应成功安装钩子");
            Check(pump.IsRunning && pump.ActiveHookCount == 1, "存在订阅时后台泵线程应运行且计数为 1");
        }
        finally
        {
            sub.Dispose();
        }

        Check(!pump.IsRunning && pump.ActiveHookCount == 0, "最后一个订阅释放后后台泵应自动退出线程");

        // Verify the pump restarts cleanly on a subsequent subscription.
        using var sub2 = pump.Subscribe(
            DockNativeMethods.EventSystemForeground,
            DockNativeMethods.EventSystemForeground,
            static m => m.ChildId == 0,
            static _ => { });
        Check(sub2.IsActive && pump.IsRunning && pump.ActiveHookCount == 1, "再次订阅应能重新拉起后台泵");
    }

    private static void TestDesktopSurfaceWatcherNormalModeHasNoGlobalShowHookAndFiltersStorms(
        Action<Window> show,
        Action pump)
    {
        using var bgPump = new BackgroundWinEventPump();
        var watcher = new DesktopSurfaceWatcher(() => [], bgPump);
        var host = new Window
        {
            Width = 240,
            Height = 160,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        show(host);
        var hwnd = new WindowInteropHelper(host).Handle;
        using var child = new HwndSource(new HwndSourceParameters("冰蓝PR2测试子控件", 32, 20)
        {
            ParentWindow = hwnd,
            WindowStyle = unchecked((int)(NativeMethods.WsChild | 0x10000000))
        });

        try
        {
            watcher.Start();
            pump();

            // Core P0-1 regression: in normal mode (!IsDesktopShown), no global EVENT_OBJECT_SHOW
            // hook is installed; Explorer events are process-scoped.
            Check(!watcher.HasGlobalShowHook, "常态（未显示桌面）不得安装全局 EVENT_OBJECT_SHOW 钩子");

            // Simulate 5,000 high-frequency Taskmgr-style child/UIA EVENT_OBJECT_SHOW notifications:
            // every single one must be dropped on the background filter before touching UI.
            var passed = 0;
            for (var i = 0; i < 5_000; i++)
            {
                var nonWindowObj = new WinEventMessage(
                    DockNativeMethods.EventObjectShow, hwnd, -4, 0, 0, (uint)i);
                var childItem = new WinEventMessage(
                    DockNativeMethods.EventObjectShow, hwnd, 0, 1, 0, (uint)i);
                var childControl = new WinEventMessage(
                    DockNativeMethods.EventObjectShow, child.Handle, 0, 0, 0, (uint)i);

                if (DesktopSurfaceWatcher.ShouldForwardGlobalShowEvent(nonWindowObj, 0)
                    || DesktopSurfaceWatcher.ShouldForwardGlobalShowEvent(childItem, 0)
                    || DesktopSurfaceWatcher.ShouldForwardGlobalShowEvent(childControl, 0)
                    || DesktopSurfaceWatcher.ShouldForwardShellObjectEvent(nonWindowObj)
                    || DesktopSurfaceWatcher.ShouldForwardShellObjectEvent(childItem)
                    || DesktopSurfaceWatcher.ShouldForwardShellObjectEvent(childControl))
                {
                    passed++;
                }
            }
            Check(passed == 0, $"高频子对象/子控件 EVENT_OBJECT_SHOW 必须 100% 在后台过滤，实际放行 {passed} 次");

            // Verify Explorer PID rebinding works without leaking hooks.
            var originalPid = watcher.ShellHookProcessId;
            watcher.RefreshShellShowHook((uint)Environment.ProcessId);
            Check(watcher.ShellHookProcessId == (uint)Environment.ProcessId, "Explorer PID 变更时应能重绑进程级钩子");
            watcher.RefreshShellShowHook(originalPid);
        }
        finally
        {
            watcher.Dispose();
            host.Close();
            pump();
        }

        Check(!bgPump.IsRunning && bgPump.ActiveHookCount == 0, "DesktopSurfaceWatcher 释放后后台泵应清空钩子并退出");
    }

    private static void TestTaskbarAdapterFiltersChildWinEvents(Action<Window> show, Action pump)
    {
        var host = new Window
        {
            Width = 200,
            Height = 140,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        show(host);
        var hwnd = new WindowInteropHelper(host).Handle;
        using var child = new HwndSource(new HwndSourceParameters("冰蓝任务栏测试子控件", 20, 20)
        {
            ParentWindow = hwnd,
            WindowStyle = unchecked((int)(NativeMethods.WsChild | 0x10000000))
        });

        try
        {
            Check(!TaskbarAdapter.ShouldForwardWinEvent(
                    new WinEventMessage(TaskbarNativeMethods.EventObjectShow, hwnd, -4, 0, 0, 0)),
                "非窗口对象 EVENT_OBJECT_SHOW 应被 TaskbarAdapter 过滤");
            Check(!TaskbarAdapter.ShouldForwardWinEvent(
                    new WinEventMessage(TaskbarNativeMethods.EventObjectShow, hwnd, 0, 1, 0, 0)),
                "非零 childId 的 EVENT_OBJECT_SHOW 应被 TaskbarAdapter 过滤");
            Check(!TaskbarAdapter.ShouldForwardWinEvent(
                    new WinEventMessage(TaskbarNativeMethods.EventObjectShow, child.Handle, 0, 0, 0, 0)),
                "子窗口控件的 EVENT_OBJECT_SHOW 应被 TaskbarAdapter 过滤");
            Check(TaskbarAdapter.ShouldForwardWinEvent(
                    new WinEventMessage(TaskbarNativeMethods.EventObjectShow, hwnd, 0, 0, 0, 0)),
                "顶层窗口的 EVENT_OBJECT_SHOW 应通过 TaskbarAdapter 过滤");
        }
        finally
        {
            host.Close();
            pump();
        }
    }

    private static void TestLauncherPanelWindowCollectsAfterClose(Action<Window> show, Action pump)
    {
        var weakRef = CreateOpenAndCloseLauncherPanel(show, pump);

        // Give WPF dispatcher (including SystemIdle cleanup) and GC multiple full passes
        // to collect the closed window. Never call weakRef.TryGetTarget(out _) in the same
        // frame as GC.Collect: its address-exposed discard slot stays rooted by RyuJIT for
        // the whole method lifetime.
        for (var pass = 0; pass < 6 && IsWeakTargetAlive(weakRef); pass++)
        {
            pump();
            DrainIdle();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            DrainIdle();
        }

        Check(!IsWeakTargetAlive(weakRef),
            "LauncherPanelWindow 关闭后必须可被 GC 回收（不得因 DependencyPropertyDescriptor.AddValueChanged 或未停定时器永久泄漏）");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsWeakTargetAlive<T>(WeakReference<T> weakRef) where T : class =>
        weakRef.TryGetTarget(out _);

    private static void DrainIdle()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.SystemIdle,
            new System.Windows.Threading.DispatcherOperationCallback(state =>
            {
                ((System.Windows.Threading.DispatcherFrame)state).Continue = false;
                return null;
            }),
            frame);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<LauncherPanelWindow> CreateOpenAndCloseLauncherPanel(
        Action<Window> show,
        Action pump)
    {
        var monitor = MonitorCatalog.GetAll().FirstOrDefault()
            ?? new MonitorSnapshot(0, @"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), true, 96);
        var entry = new LauncherPanelEntry(
            new TopBarSearchCandidate(TopBarSearchItemKind.File, "演示", "demo.txt", @"C:\demo.txt"),
            "桌面");
        var snapshot = new LauncherPanelSnapshot([entry], [], false);
        var panel = new LauncherPanelWindow(
            () => Task.FromResult(snapshot),
            static (_, _) => { },
            static () => Array.Empty<(Guid, string)>(),
            static (_, _) => Task.FromResult(string.Empty),
            0,
            monitor);
        show(panel);
        panel.SetStatusText("已复制路径：演示");
        pump();
        panel.Close();
        pump();
        return new WeakReference<LauncherPanelWindow>(panel);
    }

    private static void TestFileMappingWatchServiceSkipsBadDirectoryAndWatchesValidOnes(
        Action pump,
        Action<Func<bool>, string> pumpUntil)
    {
        var root = Path.Combine(Path.GetTempPath(), $"BingLan-watch-resilience-{Guid.NewGuid():N}");
        var validDir = Path.Combine(root, "valid");
        var missingDir = Path.Combine(root, "does-not-exist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(validDir);
        var seedFile = Path.Combine(validDir, "sample.txt");
        File.WriteAllText(seedFile, "ok");

        var sweepCount = 0;
        using var watcher = new FileMappingWatchService(
            () =>
            {
                Interlocked.Increment(ref sweepCount);
                return Task.CompletedTask;
            },
            static () => Task.CompletedTask);
        try
        {
            // Place missing directory BEFORE valid directory: missing directory must NOT abort
            // watcher registration for subsequent valid directories.
            watcher.UpdateWatchedDirectories([missingDir, validDir]);
            pump();

            File.Delete(seedFile);
            pumpUntil(() => Volatile.Read(ref sweepCount) >= 1,
                "坏目录在前时，后续合法目录仍应建立 FileSystemWatcher 并触发删除清理回调");
        }
        finally
        {
            watcher.Dispose();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static void TestMixedDpiAndNegativeOffsetPlacementRules()
    {
        // Primary 150% (2560x1440) + Secondary 100% (1920x1080) placed to the right with negative Y = -50.
        var monitors = new[]
        {
            new MonitorSnapshot(1, @"\\.\DISPLAY2",
                new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1392), true, 144),
            new MonitorSnapshot(2, @"\\.\DISPLAY1",
                new PixelRect(2560, -50, 4480, 1030), new PixelRect(2560, -50, 4480, 990), false, 96)
        };

        var secondaryCard = new WindowPlacement { Left = 3900, Top = -40, Width = 360, Height = 300 };
        WidgetCoordinator.SanitizePlacement(secondaryCard, monitors);
        Check(Math.Abs(secondaryCard.Left - 3900) < 0.01 && Math.Abs(secondaryCard.Top - (-40)) < 0.01,
            $"混合 DPI + 负 Y 副屏卡片不应被 SanitizePlacement 错裁，实际 ({secondaryCard.Left}, {secondaryCard.Top})");

        var wildlyOffscreen = new WindowPlacement { Left = 99999, Top = -99999, Width = double.NaN, Height = 300 };
        WidgetCoordinator.SanitizePlacement(wildlyOffscreen, monitors);
        Check(double.IsFinite(wildlyOffscreen.Width) && wildlyOffscreen.Left < 4480 && wildlyOffscreen.Top >= -50,
            "极端越界或 NaN 位置仍应被安全钳制到多屏包围盒内");

        // BoxMarqueeOverlay union covers full physical area including negative Y, and hint
        // host maps inside the target monitor's visible work area (never clipped above Y=0).
        var union = BoxMarqueeOverlay.ComputePhysicalUnion(monitors);
        Check(union == new PixelRect(0, -50, 4480, 1440),
            $"物理虚拟屏并集应包含副屏负 Y 与完整右边界，实际 {union}");
        var primaryHintSlot = BoxMarqueeOverlay.ComputeHintHostRect(union!.Value, monitors[0], overlayScale: 1.5d);
        Check(primaryHintSlot is { } slot && Math.Abs(slot.Top - (50d / 1.5d)) < 0.01 && Math.Abs(slot.Left) < 0.01,
            $"负 Y 错位双屏下主屏提示条应下移至主屏可见工作区内，实际 {primaryHintSlot}");

        // TodoWidgetWindow toggle on secondary monitor with negative Y and 150% DPI.
        var secondaryWork = new PixelRect(2560, -75, 5120, 1465);
        var currentListPx = new PixelRect(4700, 1100, 5100, 1450);
        var weeklyPx = TodoWidgetWindow.ComputeToggledMonitorPixelBounds(
            currentListPx, targetWidthDip: 640, targetHeightDip: 520, secondaryWork, scale: 1.5d, savedAnchorPx: null);
        Check(weeklyPx.Left >= secondaryWork.Left && weeklyPx.Right <= secondaryWork.Right
            && weeklyPx.Top >= secondaryWork.Top && weeklyPx.Bottom <= secondaryWork.Bottom,
            $"副屏切周视图目标矩形必须完整留在目标屏工作区内，实际 {weeklyPx}");

        var restoredListPx = TodoWidgetWindow.ComputeToggledMonitorPixelBounds(
            weeklyPx, targetWidthDip: 286, targetHeightDip: 336, secondaryWork, scale: 1.5d, savedAnchorPx: (4700, -60));
        Check(restoredListPx.Top == -60 && restoredListPx.Left >= secondaryWork.Left,
            $"副屏负 Y 锚点切回列表时应精确恢复原物理位置，实际 {restoredListPx}");
    }

    private static void TestBoxMarqueePhysicalPlacement(
        Action pump,
        Action<Func<bool>, string> pumpUntil)
    {
        var monitors = MonitorCatalog.GetAll();
        var target = monitors.FirstOrDefault(m => !m.IsPrimary && m.WorkingArea.HasArea)
            ?? monitors.FirstOrDefault(m => m.WorkingArea.HasArea);
        if (target is null)
        {
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-marquee-phys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var coordinator = new WidgetCoordinator(interactiveQa: true, dataDirectory: temp);
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        var previousGridSnap = WidgetWindowBase.GridSnapEnabled;
        WidgetWindowBase.GridSnapEnabled = false;
        try
        {
            coordinator.Start();
            pumpUntil(
                () => Application.Current.Windows.OfType<WidgetWindowBase>().Any(w => w.IsLoaded),
                "物理框选回归测试启动后应至少打开一张卡片");
            var appState = (AppState)typeof(WidgetCoordinator).GetField("_state", Private)!.GetValue(coordinator)!;
            var boxesBefore = appState.FileBoxes.Count;

            typeof(WidgetCoordinator).GetMethod("StartMarqueeFileBox", Private)!.Invoke(coordinator, []);
            BoxMarqueeOverlay? overlay = null;
            pumpUntil(
                () => (overlay = Application.Current.Windows.OfType<BoxMarqueeOverlay>().SingleOrDefault()) != null,
                "物理框选回归测试遮罩应打开");

            var work = target.WorkingArea;
            var scale = target.Dpi > 0 ? target.Dpi / 96d : 1d;
            var startX = work.Left + Math.Min(60, Math.Max(10, work.Width / 8));
            var startY = work.Top + Math.Min(16, Math.Max(4, work.Height / 16));
            var widthPx = Math.Min((int)Math.Round(360d * scale), Math.Max((int)Math.Ceiling(226d * scale), work.Width - 80));
            var heightPx = Math.Min((int)Math.Round(280d * scale), Math.Max((int)Math.Ceiling(166d * scale), work.Height - 80));

            overlay!.BeginPhysicalMarquee(startX, startY);
            overlay.UpdatePhysicalMarquee(startX + widthPx, startY + heightPx);
            overlay.CompleteMarquee();
            pump();

            Check(appState.FileBoxes.Count == boxesBefore + 1, "物理坐标框选完成后应新建一个分组盒");
            var createdState = appState.FileBoxes[^1];
            var createdWindow = Application.Current.Windows.OfType<FileBoxWindow>()
                .LastOrDefault(w => w.State.Id == createdState.Id);
            Check(createdWindow is not null, "物理坐标框选后应打开对应分组盒窗口");
            var handle = new WindowInteropHelper(createdWindow!).Handle;
            Check(handle != 0, "新建分组盒应已创建窗口句柄");
            Check(DockNativeMethods.GetWindowRect(handle, out var actual),
                "新建分组盒应能读取真实物理矩形");
            Check(Math.Abs(actual.Left - startX) <= 2 && Math.Abs(actual.Top - startY) <= 2,
                $"物理框选新建分组盒应精确落在目标屏（含负坐标）物理起点 ({startX}, {startY})，实际 ({actual.Left}, {actual.Top})");
            Check(Math.Abs((actual.Right - actual.Left) - widthPx) <= 2
                && Math.Abs((actual.Bottom - actual.Top) - heightPx) <= 2,
                $"物理框选新建分组盒尺寸应匹配 ({widthPx}x{heightPx})，实际 ({actual.Right - actual.Left}x{actual.Bottom - actual.Top})");
        }
        finally
        {
            WidgetWindowBase.GridSnapEnabled = previousGridSnap;
            coordinator.Dispose();
            foreach (var window in Application.Current.Windows.OfType<WidgetWindowBase>().ToList())
            {
                window.CanClose = true;
                window.Close();
            }
            pump();
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }
    }

    private static void TestWidgetCoordinatorDisposeStopsTimerAndDisposesServices(
        Action pump,
        Action<Func<bool>, string> pumpUntil)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-dispose-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var coordinator = new WidgetCoordinator(interactiveQa: true, dataDirectory: temp);
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            coordinator.Start();
            pumpUntil(
                () => Application.Current.Windows.OfType<WidgetWindowBase>().Any(window => window.IsLoaded),
                "启动后应打开至少一张卡片");

            typeof(WidgetCoordinator).GetMethod("ScheduleSave", Private)!.Invoke(coordinator, []);
            var saveTimer = (DispatcherTimer)typeof(WidgetCoordinator).GetField("_saveTimer", Private)!.GetValue(coordinator)!;
            Check(saveTimer.IsEnabled, "调用 ScheduleSave 后保存定时器应处于运行态");

            coordinator.Dispose();

            Check(!saveTimer.IsEnabled, "WidgetCoordinator.Dispose() 必须停止 _saveTimer");
            var peek = typeof(WidgetCoordinator).GetField("_peek", Private)!.GetValue(coordinator)!;
            var peekDisposed = (bool)peek.GetType().GetField("_disposed", Private)!.GetValue(peek)!;
            Check(peekDisposed, "WidgetCoordinator.Dispose() 必须释放 _peek");
        }
        finally
        {
            foreach (var window in Application.Current.Windows.OfType<WidgetWindowBase>().ToList())
            {
                window.CanClose = true;
                window.Close();
            }
            pump();
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }
    }

    private static void TestUiDiagnosticsUntrackedDescription()
    {
        var untracked = UiDiagnostics.FormatDescription(
            operation: null,
            hooksAttached: false,
            lastActivity: (int)UiActivity.DesktopEvent,
            currentStarted: 0,
            now: 1000);
        Check(untracked.Contains("未启用调度追踪", StringComparison.Ordinal)
            && !untracked.Contains("不在调度任务中", StringComparison.Ordinal),
            $"未启用调度钩子时应明确提示「未启用调度追踪」，实际：{untracked}");

        var tracked = UiDiagnostics.FormatDescription(
            operation: null,
            hooksAttached: true,
            lastActivity: -1,
            currentStarted: 0,
            now: 1000);
        Check(tracked.Contains("不在调度任务中", StringComparison.Ordinal),
            $"启用调度钩子且当前无活动任务时应提示「不在调度任务中」，实际：{tracked}");
    }

    private static void TestUiDiagnosticsKeepsSystemEventsHealthy(
        Action pump,
        Action<Func<bool>, string> pumpUntil)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-uidiag-sysevents-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        EventHandler dummyDisplayHandler = static (_, _) => { };
        try
        {
            // Start diagnostics before subscribing to DisplaySettingsChanged (mirroring
            // WidgetCoordinator's constructor order) and confirm SystemEvents' hidden
            // broadcast window remains responsive on a live message loop.
            UiDiagnostics.Start(Dispatcher.CurrentDispatcher, temp, trackOperations: false);
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += dummyDisplayHandler;

            var invoked = 0;
            Microsoft.Win32.SystemEvents.InvokeOnEventsThread((Action)(() => Interlocked.Exchange(ref invoked, 1)));
            pumpUntil(() => Volatile.Read(ref invoked) == 1,
                "UiDiagnostics.Start 后进程级 SystemEvents 消息泵必须保持健康并能正常分发回调");
            pump();
        }
        finally
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= dummyDisplayHandler;
            try { Directory.Delete(temp, true); } catch (IOException) { }
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
