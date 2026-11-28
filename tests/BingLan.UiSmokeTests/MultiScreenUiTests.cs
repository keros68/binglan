using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using BingLan.App;
using BingLan.App.Dock;
using BingLan.App.Services;
using BingLan.App.Windows;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Services;

/// <summary>
/// UI and lifecycle tests for Phase 1 multi-screen independent desktop card profiles:
/// single-screen zero-overhead UI hiding, disabled dual-monitor zero-QueryDisplayConfig & no false offline,
/// simulated dual-monitor topology with negative Y and mixed DPI (DISPLAY2 0,0 2048x1152@120 + DISPLAY1 2048,-50 1707x1067@96),
/// copy from primary, QuickPlaces move/drag data migration, FileBox drag EdgeDock clearing,
/// ApplyPhysicalBounds zero-handle guard, real WidgetCoordinator toggle/OrganizeDesktop primary isolation,
/// shared WeatherService cross-screen dedup & active-sampler initial refresh, and hot-unplug hard-Close destruction.
/// Uses WPF dispatching and native window rects only (no SendInput / CopyFromScreen).
/// </summary>
internal static class MultiScreenUiTests
{
    private const string PrimaryHardwareKey = "DISPLAY#PRIMARY#0";
    private const string SecondaryHardwareKey = "DISPLAY#TESTMON2#5&1A2B3C&0&UID4352";
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    internal static void Run(Action<Window> show, Action pump)
    {
        var previousOverride = MonitorIdentityResolver.ResolveOverrideForTests;
        try
        {
            MonitorIdentityTests.Run();
            TestMonitorIdentityStability();
            TestSingleScreenZeroOverheadSettingsUi(show, pump);
            TestDisabledDualMonitorAndDisconnectedCardsStayOffScreen(show, pump);
            TestDualMonitorLifecycleSettingsAndHotUnplug(show, pump);
            TestMultiScreenHostLifecycleAndHotUnplug(show, pump);
            TestRealCoordinatorToggleAndPrimaryOrganizeIsolation(pump);
            TestDualScreenWeatherDedupWithRunningSampler(show, pump);
            TestDisconnectRebootAndReconnectTodoMemory(pump);
            TestUserDragCompletedCrossScreenOwnership(pump);
            TestQueuedDisplayLayoutAfterDisposeDoesNotResurrectMultiScreenHost(pump);
            TestBucket1EdgeCasesIntegration(pump);
            TestSecondaryGroupedFileBoxLifecycleAndCrossScreenDrag(pump);
            TestThreePhysicalScreensKeepOwnership(pump);
            TestLegacyRootRequiresExplicitBinding(pump);
        }
        finally
        {
            MonitorIdentityResolver.ResolveOverrideForTests = previousOverride;
            MonitorIdentityResolver.ResetQueryDisplayConfigCallCountForTests();
        }
    }

    private static void TestMonitorIdentityStability()
    {
        var pathA = MonitorIdentityResolver.NormalizeHardwareMonitorPath(
            @"\\?\DISPLAY#DELA07A#5&12345&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}");
        var pathB = MonitorIdentityResolver.NormalizeHardwareMonitorPath(
            @"DISPLAY#dela07a#5&12345&0&UID4352");
        Check(pathA == pathB && pathA == "DISPLAY#DELA07A#5&12345&0&UID4352",
            $"QueryDisplayConfig 与 EnumDisplayDevicesW 应归一化出相同的硬件键，实际 {pathA} vs {pathB}");

        var snapshotsBefore = new[]
        {
            new MonitorSnapshot(1, @"\\.\DISPLAY2", new PixelRect(0, 0, 2048, 1152), new PixelRect(0, 0, 2048, 1104), true, 120),
            new MonitorSnapshot(2, @"\\.\DISPLAY1", new PixelRect(2048, -50, 3755, 1017), new PixelRect(2048, -50, 3755, 977), false, 96)
        };
        var snapshotsAfterRenumber = new[]
        {
            new MonitorSnapshot(1, @"\\.\DISPLAY2", new PixelRect(0, 0, 2048, 1152), new PixelRect(0, 0, 2048, 1104), true, 120),
            new MonitorSnapshot(9, @"\\.\DISPLAY9", new PixelRect(2048, -50, 3755, 1017), new PixelRect(2048, -50, 3755, 977), false, 96)
        };
        var hwBefore = new Dictionary<string, MonitorHardwareInfo>(StringComparer.OrdinalIgnoreCase)
        {
            [@"\\.\DISPLAY1"] = new(@"\\?\DISPLAY#TESTMON2#5&1A2B3C&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", "副屏测试", 10)
        };
        var hwAfter = new Dictionary<string, MonitorHardwareInfo>(StringComparer.OrdinalIgnoreCase)
        {
            [@"\\.\DISPLAY9"] = new(@"\\?\DISPLAY#TESTMON2#5&1A2B3C&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", "副屏测试", 10)
        };

        var before = MonitorIdentityResolver.BuildResolvedMonitors(snapshotsBefore, hwBefore);
        var after = MonitorIdentityResolver.BuildResolvedMonitors(snapshotsAfterRenumber, hwAfter);
        Check(before[0].ScreenKey is null && before[1].MonitorKey == SecondaryHardwareKey,
            $"主屏 ScreenKey 应为 null 且副屏硬件键应稳定，实际 {before[1].MonitorKey}");
        Check(after[1].MonitorKey == before[1].MonitorKey,
            "GDI 显示器编号从 DISPLAY1 变为 DISPLAY9 后硬件 MonitorKey 必须保持不变");

        // Duplicate exact paths remain unreliable across geometry swaps.
        var dupLeftRight = new[]
        {
            new MonitorSnapshot(1, @"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), true, 96),
            new MonitorSnapshot(2, @"\\.\DISPLAY2", new PixelRect(1920, 0, 3840, 1080), new PixelRect(1920, 0, 3840, 1040), false, 96),
            new MonitorSnapshot(3, @"\\.\DISPLAY3", new PixelRect(3840, 0, 5760, 1080), new PixelRect(3840, 0, 5760, 1040), false, 96)
        };
        var dupSwapped = new[]
        {
            new MonitorSnapshot(1, @"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), true, 96),
            new MonitorSnapshot(3, @"\\.\DISPLAY3", new PixelRect(1920, 0, 3840, 1080), new PixelRect(1920, 0, 3840, 1040), false, 96),
            new MonitorSnapshot(2, @"\\.\DISPLAY2", new PixelRect(3840, 0, 5760, 1080), new PixelRect(3840, 0, 5760, 1040), false, 96)
        };
        var dupHw = new Dictionary<string, MonitorHardwareInfo>(StringComparer.OrdinalIgnoreCase)
        {
            [@"\\.\DISPLAY2"] = new(@"\\?\DISPLAY#SAMEDID#0", "同型号副屏", 101),
            [@"\\.\DISPLAY3"] = new(@"\\?\DISPLAY#SAMEDID#0", "同型号副屏", 202)
        };
        var resolvedA = MonitorIdentityResolver.BuildResolvedMonitors(dupLeftRight, dupHw);
        var resolvedB = MonitorIdentityResolver.BuildResolvedMonitors(dupSwapped, dupHw);
        var keyD2A = resolvedA.Single(m => m.DeviceName == @"\\.\DISPLAY2").MonitorKey;
        var keyD2B = resolvedB.Single(m => m.DeviceName == @"\\.\DISPLAY2").MonitorKey;
        Check(keyD2A == "slot:DISPLAY2" && keyD2A == keyD2B
            && !resolvedA.Single(m => m.DeviceName == @"\\.\DISPLAY2").IsPersistentIdentity,
            $"重复精确设备路径必须标记为不可持久绑定，实际 {keyD2A} vs {keyD2B}");

        // Resolution-independent fallback slot when hardware path is unavailable.
        var virtual1080 = new[]
        {
            new MonitorSnapshot(1, @"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), true, 96),
            new MonitorSnapshot(2, @"\\.\DISPLAY3", new PixelRect(1920, 0, 3840, 1080), new PixelRect(1920, 0, 3840, 1040), false, 96)
        };
        var virtual1440 = new[]
        {
            new MonitorSnapshot(1, @"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), true, 96),
            new MonitorSnapshot(2, @"\\.\DISPLAY3", new PixelRect(1920, 0, 4480, 1440), new PixelRect(1920, 0, 4480, 1400), false, 96)
        };
        var keyV1 = MonitorIdentityResolver.BuildResolvedMonitors(virtual1080, null)[1].MonitorKey;
        var keyV2 = MonitorIdentityResolver.BuildResolvedMonitors(virtual1440, null)[1].MonitorKey;
        Check(keyV1 == "slot:DISPLAY3" && keyV1 == keyV2,
            $"无硬件路径回退键不应绑定易变分辨率，实际 {keyV1} vs {keyV2}");
    }

    private static void TestSingleScreenZeroOverheadSettingsUi(Action<Window> show, Action pump)
    {
        var single = CreatePrimaryOnlyMonitors();
        MonitorIdentityResolver.ResolveOverrideForTests = () => single;
        MonitorIdentityResolver.ResetQueryDisplayConfigCallCountForTests();

        var info = new InformationWidgetState();
        var exp = DesktopExperienceRules.CreateDefault();
        var hasSaved = false;
        var bridge = new MultiScreenSettingsBridge
        {
            IsEnabled = () => false,
            HasMultipleMonitors = () => false,
            GetConnectedMonitorCount = () => 1,
            HasSavedSecondaryState = () => hasSaved,
            GetScreens = () => []
        };
        var settings = new SettingsWindow(
            new CityLookupService(),
            info,
            exp,
            (_, _) => { },
            _ => { },
            _ => { },
            maintenance: new SettingsMaintenance { MultiScreen = bridge });
        show(settings);
        try
        {
            pump();
            Check(!settings.IsMultiScreenUiVisible, "单屏无多屏存档时桌面方案页多屏区块必须保持 Collapsed");
            Check(!settings.IsMultiScreenTopologyBarVisible, "单屏无多屏存档时拓扑切换栏必须保持 Collapsed");
            Check(!settings.HasInstantiatedMultiScreenChildren,
                "单屏模式下不得实例化任何多屏子控件（Child 全部为 null）");
            Check(MonitorIdentityResolver.QueryDisplayConfigCallCountForTests == 0,
                "单屏模式不得触发 QueryDisplayConfig");

            hasSaved = true;
            settings.RefreshMultiScreenUi();
            Check(settings.FindName("DisplaysIndependentSwitch") is CheckBox { IsEnabled: true },
                "只剩一屏时应仍能重新启用已保存的屏幕档案");
        }
        finally
        {
            settings.Close();
            pump();
        }
    }

    private static void TestDisabledDualMonitorAndDisconnectedCardsStayOffScreen(Action<Window> show, Action pump)
    {
        var temp = Path.Combine(Path.GetTempPath(), "binglan-ms-qdc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var activeMonitors = CreateDualMonitors(@"\\.\DISPLAY1");
        MonitorIdentityResolver.ResolveOverrideForTests = () => activeMonitors;

        // Pre-seed a saved disabled multi-screen state with cards assigned to DISPLAY1.
        var store = new LocalStateStore(temp);
        var seed = LocalStateStore.CreateDefault();
        var profile = ScreenProfileAccessor.EnsureScreenProfile(seed, SecondaryHardwareKey, @"\\.\DISPLAY1", "副屏 2");
        profile.Experience.GetComponent(DesktopComponentKind.Weather).IsVisible = true;
        seed.MultiScreen!.IsEnabled = false;
        var savedNote = NoteService.CreateDefaultWidget();
        savedNote.Title = "在线但关闭多屏的便签";
        savedNote.ScreenKey = SecondaryHardwareKey;
        seed.NoteWidgets.Add(savedNote);
        store.Save(seed);

        var coordinator = new WidgetCoordinator(interactiveQa: true, dataDirectory: temp);
        SettingsWindow? settings = null;
        try
        {
            MonitorIdentityResolver.ResetQueryDisplayConfigCallCountForTests();
            var bridge = coordinator.BuildMultiScreenSettingsBridge();
            settings = new SettingsWindow(
                new CityLookupService(),
                new InformationWidgetState(),
                DesktopExperienceRules.CreateDefault(),
                (_, _) => { },
                _ => { },
                _ => { },
                maintenance: new SettingsMaintenance { MultiScreen = bridge });
            show(settings);
            pump();

            Check(settings.IsMultiScreenUiVisible && !settings.IsMultiScreenTopologyBarVisible,
                "双屏但多屏关闭时，桌面方案页只读取连接数量");
            Check(MonitorIdentityResolver.QueryDisplayConfigCallCountForTests == 0,
                $"双屏但关闭多屏开关时打开设置页严禁触发 QueryDisplayConfig，实际触发 {MonitorIdentityResolver.QueryDisplayConfigCallCountForTests} 次");

            settings.ShowSettingsPage("Components");
            settings.SelectActiveScreen(SecondaryHardwareKey);
            pump();
            Check(settings.IsMultiScreenTopologyBarVisible && !bridge.IsEnabled() && !coordinator.HasActiveMultiScreenHost
                && settings.FindName("ComponentEditorHost") is Grid { Visibility: Visibility.Collapsed },
                "关闭多屏时可直接选副屏，选屏保持独立桌面未启用，并隐藏主屏编辑器");
            Button CopyButton() => ((StackPanel)((Border)((Border)settings.FindName("ComponentsMultiScreenBarHost")).Child).Child)
                .Children.OfType<Grid>().Single().Children.OfType<Button>()
                .Single(button => Equals(button.Content, "复制主屏设置"));
            var unavailableCopy = CopyButton();
            var queryCount = MonitorIdentityResolver.QueryDisplayConfigCallCountForTests;
            activeMonitors = CreatePrimaryOnlyMonitors();
            unavailableCopy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!bridge.IsEnabled(), "点击时副屏已断开应保持独立桌面未启用");
            settings.RefreshMultiScreenUi();
            pump();

            Check(settings.IsMultiScreenUiVisible && !settings.IsMultiScreenTopologyBarVisible
                && settings.FindName("ComponentsMultiScreenBarHost") is Border { Child: null }
                && settings.FindName("SchemeMultiScreenHost") is Border { Child: SettingsGroup shortcut }
                && shortcut.Items.OfType<SettingsRow>().Single().Content is Button,
                "副屏断开后保留显示器设置入口，桌面卡片页不出现迁移入口");
            Check(MonitorIdentityResolver.QueryDisplayConfigCallCountForTests == queryCount,
                "副屏拔出成单屏后刷新设置不应触发 QueryDisplayConfig");
            var saved = store.Load();
            Check(saved.NoteWidgets.Any(note => note.Id == savedNote.Id && note.ScreenKey == SecondaryHardwareKey)
                && saved.MultiScreen!.Screens[SecondaryHardwareKey].Experience.GetComponent(DesktopComponentKind.Weather).IsVisible,
                "拔屏后副屏便签、天气卡和原屏归属仍保留在存档");

            activeMonitors = CreateDualMonitors(@"\\.\DISPLAY1");
            coordinator.Start();
            settings.RefreshMultiScreenUi();
            settings.SelectActiveScreen(SecondaryHardwareKey);
            CopyButton().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            pump();
            saved = store.Load();
            Check(bridge.IsEnabled() && coordinator.HasActiveMultiScreenHost && settings.ActiveScreenKey == SecondaryHardwareKey
                && settings.FindName("ComponentEditorHost") is Grid { Visibility: Visibility.Visible }
                && saved.MultiScreen?.RootMonitorKey == PrimaryHardwareKey
                && saved.NoteWidgets.Any(note => note.Id == savedNote.Id && note.ScreenKey == SecondaryHardwareKey),
                "直接点击复制应绑定当前主屏、启用各屏编辑并保留副屏已有内容");
        }
        finally
        {
            settings?.Close();
            DisposeCoordinatorWindows(coordinator);
            coordinator.Dispose();
            pump();
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static void TestDualMonitorLifecycleSettingsAndHotUnplug(Action<Window> show, Action pump)
    {
        var primaryOnly = CreatePrimaryOnlyMonitors();
        var dualMonitors = CreateDualMonitors(@"\\.\DISPLAY1");
        var repluggedDualMonitors = CreateDualMonitors(@"\\.\DISPLAY9");
        var activeMonitors = dualMonitors;
        MonitorIdentityResolver.ResolveOverrideForTests = () => activeMonitors;

        var state = LocalStateStore.CreateDefault();
        state.DesktopExperience.GetComponent(DesktopComponentKind.TimeDate).IsVisible = true;
        state.DesktopExperience.GetComponent(DesktopComponentKind.Todo).IsVisible = true;

        var windows = new List<WidgetWindowBase>();
        var hiddenFromTray = new List<WidgetWindowBase>();
        using var sampler = new WindowsPerformanceSamplingService();
        var weather = new WeatherService();

        var host = new MultiScreenHost(
            state,
            windows,
            hiddenFromTray,
            new MultiScreenCoordinatorCallbacks
            {
                GetInformationState = () => state.InformationWidgets[0],
                CreateInformationWindow = (infoState, experience, kind) =>
                {
                    var win = new InformationWidgetWindow(infoState, sampler, weather, experience, kind)
                    {
                        ShowInTaskbar = false
                    };
                    windows.Add(win);
                    return win;
                },
                CreateTodoWindow = (todoState, _) =>
                {
                    var win = new TodoWidgetWindow(todoState) { ShowInTaskbar = false };
                    windows.Add(win);
                    return win;
                },
                CreateNoteWindow = noteState =>
                {
                    var win = new NoteWidgetWindow(noteState) { ShowInTaskbar = false };
                    windows.Add(win);
                    return win;
                },
                CreateFileBoxWindow = members =>
                {
                    var win = new FileBoxWindow(members) { ShowInTaskbar = false };
                    windows.Add(win);
                    return win;
                },
                CreateQuickPlacesWindow = () =>
                {
                    var win = new QuickPlacesWindow { ShowInTaskbar = false };
                    windows.Add(win);
                    return win;
                },
                ApplyTodoComponent = (_, _) => { },
                CopyTodoLookFromComponent = (_, _) => { },
                SyncComponentFromTodo = (_, _) => { },
                UseSharedLook = _ => { },
                ApplyEdgeDock = () => { },
                UpdateSamplingMode = () => { },
                ScheduleSave = () => { },
                SaveNow = () => { },
                RefreshSettingsUi = () => { },
                IsDesktopHidden = () => false,
                EnsureDesktopVisible = () => { },
                RunSuppressingExperienceChanges = action => action()
            });

        SettingsWindow? settings = null;
        try
        {
            // 0. ApplyPhysicalBounds zero-handle guard must NOT mutate Placement/DisplayLayouts when Handle == 0.
            var unshownNote = NoteService.CreateDefaultWidget();
            unshownNote.Placement = new WindowPlacement { Left = 111, Top = 222, Width = 300, Height = 260 };
            var unshownWin = new NoteWidgetWindow(unshownNote) { ShowInTaskbar = false };
            var appliedUnshown = MultiScreenHost.ApplyPhysicalBounds(
                unshownWin, new PixelRect(2100, -20, 2400, 240), 96, unshownNote.Placement, "test-layout");
            Check(!appliedUnshown && unshownNote.Placement.Left == 111 && unshownNote.Placement.Top == 222
                && unshownNote.Placement.DisplayLayouts.Count == 0,
                "未创建 HWND（handle==0）时 ApplyPhysicalBounds 必须返回 false 且不得提前改写 Placement 或 DisplayLayouts");

            // 1. Enabling multi-screen on a brand-new unknown secondary monitor keeps it blank (0 windows).
            state.MultiScreen = new MultiScreenState { IsEnabled = true, RootMonitorKey = PrimaryHardwareKey };
            host.SyncConnectedScreens(activeMonitors);
            pump();
            Check(host.SecondaryWindowCount == 0 && windows.Count == 0,
                $"新接入未知副屏应保持空白零窗口，实际 SecondaryWindowCount={host.SecondaryWindowCount}");

            // 2. Settings UI shows multi-screen toggle & topology switcher when dual monitors connected.
            var bridge = new MultiScreenSettingsBridge
            {
                IsEnabled = () => state.MultiScreen is { IsEnabled: true },
                HasMultipleMonitors = () => activeMonitors.Count >= 2,
                GetConnectedMonitorCount = () => activeMonitors.Count,
                HasSavedSecondaryState = () => state.MultiScreen is { Screens.Count: > 0 },
                SetEnabled = enabled =>
                {
                    state.MultiScreen ??= new MultiScreenState();
                    state.MultiScreen.IsEnabled = enabled;
                    if (enabled)
                    {
                        host.SyncConnectedScreens(activeMonitors);
                    }
                    else
                    {
                        host.SyncConnectedScreens(primaryOnly);
                    }
                },
                GetScreens = () => activeMonitors.Select(m => new MonitorCardOption(
                    m.ScreenKey, m.MonitorKey, m.DisplayLabel, m.IsPrimary ? "主屏" : $"副屏 {m.DisplayIndex}",
                    m.ResolutionSummary, m.DisplayIndex, m.IsPrimary, true, m.Bounds, m.WorkingArea, m.Dpi,
                    m.IsPrimary ? 2 : host.GetWindowsForSecondaryScreen(m.MonitorKey).Count)).ToList(),
                GetExperience = key => ScreenProfileAccessor.GetExperience(state, key),
                ApplyExperience = (key, exp) =>
                {
                    if (key is not null)
                    {
                        host.ApplyExperienceForScreen(key, exp, saveImmediate: false);
                    }
                },
                ApplyLayoutPreset = (key, exp) =>
                {
                    if (key is not null)
                    {
                        host.ApplyLayoutPresetForScreen(key, exp);
                    }
                },
                GetQuickPlaces = key => ScreenProfileAccessor.GetQuickPlaces(state, key),
                SetQuickPlaces = (key, items) =>
                {
                    if (key is not null)
                    {
                        host.ApplyQuickPlacesForScreen(key, items);
                    }
                },
                GetPrimaryTodoId = key => ScreenProfileAccessor.GetPrimaryTodoId(state, key),
                GetWindowsForScreen = key => key is null
                    ? windows.Where(w => !host.IsSecondaryWindow(w)).ToList()
                    : host.GetWindowsForSecondaryScreen(key),
                GetScreenKeyForWindow = w => host.TryGetScreenKey(w),
                AddWidgetOnScreen = (key, kind) => key is not null ? host.AddWidgetOnScreen(key, kind) : null,
                CopyPrimaryProfileToScreen = key => host.CopyPrimaryProfileToScreen(key),
                MoveWidgetToScreen = (w, key) => host.MoveWindowToScreen(w, key, activeMonitors)
            };

            settings = new SettingsWindow(
                new CityLookupService(),
                state.InformationWidgets[0],
                state.DesktopExperience,
                (_, _) => { },
                _ => { },
                _ => { },
                widgetProvider: () => windows.ToList(),
                maintenance: new SettingsMaintenance { MultiScreen = bridge });
            show(settings);
            pump();

            Check(settings.IsMultiScreenUiVisible && settings.IsMultiScreenTopologyBarVisible,
                "双屏且开启多屏时，设置页应显示多屏入口与拓扑切换栏");

            // Switch to secondary monitor and copy primary profile onto DISPLAY1 (2048, -50).
            settings.SelectActiveScreen(SecondaryHardwareKey);
            pump();
            Check(settings.ActiveScreenKey == SecondaryHardwareKey, "切换屏幕后 ActiveScreenKey 应指向副屏");

            var copyMsg = bridge.CopyPrimaryProfileToScreen(SecondaryHardwareKey);
            pump();
            Check(copyMsg.Contains("副屏", StringComparison.Ordinal), $"复制方案提示异常：{copyMsg}");
            Check(host.SecondaryWindowCount >= 2, $"复制主屏方案后副屏应创建至少 2 个窗口，实际 {host.SecondaryWindowCount}");

            // Verify secondary window physical bounds sit on DISPLAY1 (X >= 2048, Y >= -50).
            var secondaryLive = host.GetWindowsForSecondaryScreen(SecondaryHardwareKey).ToList();
            foreach (var secWin in secondaryLive)
            {
                var handle = new WindowInteropHelper(secWin).Handle;
                if (handle == 0 || !GetWindowRect(handle, out var rect))
                {
                    throw new InvalidOperationException("副屏窗口应具备有效 HWND 与位置");
                }
                Check(rect.Left >= 2048 && rect.Top >= -50 && rect.Right <= 3755 && rect.Bottom <= 1017,
                    $"副屏窗口物理坐标应落在 DISPLAY1 显示区 (2048,-50)-(3755,1017) 内，实际 ({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})");
            }

            // 3. QuickPlaces button-move & drag-reconcile preserve custom list and reset source screen.
            state.QuickPlaces =
            [
                new QuickPlaceState { Icon = QuickPlaceRules.FolderIcon, Name = "专属工程", Target = @"C:\Projects" }
            ];
            state.DesktopExperience.GetComponent(DesktopComponentKind.QuickLaunch).IsVisible = true;
            state.MultiScreen!.Screens[SecondaryHardwareKey].Experience.GetComponent(DesktopComponentKind.QuickLaunch).IsVisible = false;
            var quickWin = new QuickPlacesWindow { ShowInTaskbar = false };
            quickWin.SetItems(state.QuickPlaces);
            windows.Add(quickWin);
            show(quickWin);
            pump();

            var moveQuickRes = host.MoveWindowToScreen(quickWin, SecondaryHardwareKey, activeMonitors);
            pump();
            Check(moveQuickRes.Succeeded && host.IsSecondaryWindow(quickWin),
                $"按钮移屏快捷入口应成功，实际：{moveQuickRes.Message}");
            Check(ScreenProfileAccessor.GetQuickPlaces(state, SecondaryHardwareKey).Any(p => p.Target == @"C:\Projects")
                && !state.QuickPlaces.Any(p => p.Target == @"C:\Projects"),
                "按钮移屏快捷入口必须连同自定义列表搬迁并重置源屏列表");

            // Drag QuickPlaces back onto primary monitor and run ReconcileWindowScreenAfterMove.
            var quickHandle = new WindowInteropHelper(quickWin).Handle;
            SetWindowPos(quickHandle, 0, 120, 120, 320, 64, SwpNoZOrder | SwpNoActivate);
            pump();
            Check(host.ReconcileWindowScreenAfterMove(quickWin) && !host.IsSecondaryWindow(quickWin),
                "拖拽快捷入口回主屏后应自动切换归属");
            Check(state.QuickPlaces.Any(p => p.Target == @"C:\Projects")
                && !ScreenProfileAccessor.GetQuickPlaces(state, SecondaryHardwareKey).Any(p => p.Target == @"C:\Projects"),
                "拖拽快捷入口跨屏后必须同步搬迁自定义列表并重置源屏");

            // 4. Add a secondary Note and move it to Primary keeping its text intact.
            var noteWin = host.AddWidgetOnScreen(SecondaryHardwareKey, NewWidgetKind.Note) as NoteWidgetWindow
                ?? throw new InvalidOperationException("副屏新建便签失败");
            noteWin.State.Content = "副屏迁移测试正文";
            pump();
            Check(host.IsSecondaryWindow(noteWin) && noteWin.State.ScreenKey == SecondaryHardwareKey,
                "副屏新建便签应归属副屏");

            var moveResult = host.MoveWindowToScreen(noteWin, targetScreenKey: null, activeMonitors);
            pump();
            Check(moveResult.Succeeded && !host.IsSecondaryWindow(noteWin)
                && noteWin.State.ScreenKey is null && noteWin.State.Content == "副屏迁移测试正文",
                "跨屏移回主屏后便签正文与归属应正确更新");

            // 5. Cross-screen drag of a FileBox must clear EdgeDock anchor metadata.
            var boxWin = host.AddWidgetOnScreen(SecondaryHardwareKey, NewWidgetKind.FileBox) as FileBoxWindow
                ?? throw new InvalidOperationException("副屏新建收纳盒失败");
            boxWin.State.EdgeDockSide = FileBoxEdgeDockSide.Right;
            boxWin.State.EdgeDockOffsetDip = 88d;
            boxWin.State.EdgeDockMonitorDeviceName = @"\\.\DISPLAY1";
            var boxHandle = new WindowInteropHelper(boxWin).Handle;
            SetWindowPos(boxHandle, 0, 200, 180, 360, 300, SwpNoZOrder | SwpNoActivate);
            pump();
            Check(host.ReconcileWindowScreenAfterMove(boxWin)
                && !host.IsSecondaryWindow(boxWin)
                && boxWin.State.ScreenKey is null
                && boxWin.State.EdgeDockSide is null
                && boxWin.State.EdgeDockOffsetDip is null
                && boxWin.State.EdgeDockMonitorDeviceName is null,
                "跨屏拖拽文件收纳盒后必须更新 ScreenKey 并清空原屏贴边停靠锚点");

            // 6. Hot-unplug DISPLAY1: secondary windows MUST Close() (not Hide()), while JSON retains profile.
            var beforeUnplugWindows = host.GetWindowsForSecondaryScreen(SecondaryHardwareKey).ToList();
            Check(beforeUnplugWindows.Count >= 2, "拔屏前副屏应存在活窗口");
            activeMonitors = primaryOnly;
            host.SyncConnectedScreens(activeMonitors);
            settings.RefreshMultiScreenUi();
            pump();
            Check(settings.IsMultiScreenTopologyBarVisible,
                "启用多屏时仅剩一屏仍应显示物理屏幕与离线档案入口");
            Check(host.SecondaryWindowCount == 0,
                $"副屏拔出后 SecondaryWindowCount 必须归零，实际 {host.SecondaryWindowCount}");
            Check(beforeUnplugWindows.All(w => !w.IsVisible && !w.IsLoaded && !windows.Contains(w)),
                "副屏拔出后所有副屏窗口必须彻底 Close 销毁并移出窗口列表，绝不能仅 Hide 驻留");
            Check(state.MultiScreen!.Screens.ContainsKey(SecondaryHardwareKey),
                "副屏拔出后 JSON 副屏配置必须保留以便重连恢复");

            // 7. Replug DISPLAY1 even with a changed GDI device name (DISPLAY9) -> restores by MonitorKey!
            activeMonitors = repluggedDualMonitors;
            host.SyncConnectedScreens(activeMonitors);
            pump();
            Check(host.SecondaryWindowCount >= 2,
                $"副屏改号重连（DISPLAY9 但硬件 MonitorKey 不变）后应自动恢复副屏窗口，实际 {host.SecondaryWindowCount}");

            // 8. Disabling / Disposing MultiScreenHost destroys every secondary window cleanly.
            var beforeDisposeWindows = host.GetWindowsForSecondaryScreen(SecondaryHardwareKey).ToList();
            host.Dispose();
            pump();
            Check(host.SecondaryWindowCount == 0
                && beforeDisposeWindows.All(w => !w.IsVisible && !w.IsLoaded && !windows.Contains(w)),
                "关闭多屏并释放 MultiScreenHost 后所有副屏窗口必须彻底 Close 销毁");
        }
        finally
        {
            settings?.Close();
            host.Dispose();
            foreach (var win in windows.ToList())
            {
                win.CanClose = true;
                try { win.Close(); } catch (InvalidOperationException) { }
            }
            windows.Clear();
            pump();
        }
    }


    private static void TestMultiScreenHostLifecycleAndHotUnplug(Action<Window> show, Action pump)
    {
        var primaryOnly = CreatePrimaryOnlyMonitors();
        var dualMonitors = CreateDualMonitors(@"\\.\DISPLAY1");
        var repluggedDualMonitors = CreateDualMonitors(@"\\.\DISPLAY9");
        var activeMonitors = dualMonitors;
        MonitorIdentityResolver.ResolveOverrideForTests = () => activeMonitors;

        var state = LocalStateStore.CreateDefault();
        state.DesktopExperience.GetComponent(DesktopComponentKind.TimeDate).IsVisible = true;
        state.DesktopExperience.GetComponent(DesktopComponentKind.Todo).IsVisible = true;
        state.DesktopExperience.GetComponent(DesktopComponentKind.AudioVisualizer).IsVisible = true;
        state.DesktopExperience.GetComponent(DesktopComponentKind.AudioVisualizer).SpectrumBarWidth = 0.67d;

        var windows = new List<WidgetWindowBase>();
        var hiddenFromTray = new List<WidgetWindowBase>();
        using var sampler = new WindowsPerformanceSamplingService();
        var weather = new WeatherService();

        var host = new MultiScreenHost(
            state,
            windows,
            hiddenFromTray,
            new MultiScreenCoordinatorCallbacks
            {
                GetInformationState = () => state.InformationWidgets[0],
                CreateInformationWindow = (infoState, experience, kind) =>
                {
                    var win = new InformationWidgetWindow(infoState, sampler, weather, experience, kind)
                    {
                        ShowInTaskbar = false
                    };
                    win.SetAudioSpectrumSuspended(true);
                    windows.Add(win);
                    return win;
                },
                CreateTodoWindow = (todoState, _) =>
                {
                    var win = new TodoWidgetWindow(todoState) { ShowInTaskbar = false };
                    windows.Add(win);
                    return win;
                },
                CreateNoteWindow = noteState =>
                {
                    var win = new NoteWidgetWindow(noteState) { ShowInTaskbar = false };
                    windows.Add(win);
                    return win;
                },
                CreateFileBoxWindow = members =>
                {
                    var win = new FileBoxWindow(members) { ShowInTaskbar = false };
                    windows.Add(win);
                    return win;
                },
                CreateQuickPlacesWindow = () =>
                {
                    var win = new QuickPlacesWindow { ShowInTaskbar = false };
                    windows.Add(win);
                    return win;
                },
                ApplyTodoComponent = (_, _) => { },
                CopyTodoLookFromComponent = (_, _) => { },
                SyncComponentFromTodo = (_, _) => { },
                UseSharedLook = _ => { },
                ApplyEdgeDock = () => { },
                UpdateSamplingMode = () => { },
                ScheduleSave = () => { },
                SaveNow = () => { },
                RefreshSettingsUi = () => { },
                IsDesktopHidden = () => false,
                EnsureDesktopVisible = () => { },
                RunSuppressingExperienceChanges = action => action()
            });

        try
        {
            // 0. ApplyPhysicalBounds zero-handle guard must NOT mutate Placement/DisplayLayouts when Handle == 0.
            var unshownNote = NoteService.CreateDefaultWidget();
            unshownNote.Placement = new WindowPlacement { Left = 111, Top = 222, Width = 300, Height = 260 };
            var unshownWin = new NoteWidgetWindow(unshownNote) { ShowInTaskbar = false };
            var appliedUnshown = MultiScreenHost.ApplyPhysicalBounds(
                unshownWin, new PixelRect(2100, -20, 2400, 240), 96, unshownNote.Placement, "test-layout");
            Check(!appliedUnshown && unshownNote.Placement.Left == 111 && unshownNote.Placement.Top == 222
                && unshownNote.Placement.DisplayLayouts.Count == 0,
                "未创建 HWND（handle==0）时 ApplyPhysicalBounds 必须返回 false 且不得提前改写 Placement 或 DisplayLayouts");

            // 1. Enabling multi-screen on a brand-new unknown secondary monitor keeps it blank (0 windows).
            state.MultiScreen = new MultiScreenState { IsEnabled = true, RootMonitorKey = PrimaryHardwareKey };
            host.SyncConnectedScreens(activeMonitors);
            pump();
            Check(host.SecondaryWindowCount == 0 && windows.Count == 0,
                $"新接入未知副屏应保持空白零窗口，实际 SecondaryWindowCount={host.SecondaryWindowCount}");

            // 2. Copy primary profile onto DISPLAY1 (2048, -50).
            var copyMsg = host.CopyPrimaryProfileToScreen(SecondaryHardwareKey);
            pump();
            Check(copyMsg.Contains("副屏", StringComparison.Ordinal), $"复制方案提示异常：{copyMsg}");
            Check(host.SecondaryWindowCount >= 2, $"复制主屏方案后副屏应创建至少 2 个窗口，实际 {host.SecondaryWindowCount}");

            // Verify secondary window physical bounds sit on DISPLAY1 (X >= 2048, Y >= -50).
            var secondaryLive = host.GetWindowsForSecondaryScreen(SecondaryHardwareKey).ToList();
            foreach (var secWin in secondaryLive)
            {
                var handle = new WindowInteropHelper(secWin).Handle;
                if (handle == 0 || !GetWindowRect(handle, out var rect))
                {
                    throw new InvalidOperationException("副屏窗口应具备有效 HWND 与位置");
                }
                Check(rect.Left >= 2048 && rect.Top >= -50 && rect.Right <= 3755 && rect.Bottom <= 1017,
                    $"副屏窗口物理坐标应落在 DISPLAY1 显示区 (2048,-50)-(3755,1017) 内，实际 ({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})");
            }

            var secondarySpectrum = secondaryLive.OfType<InformationWidgetWindow>()
                .Single(window => window.ComponentKind == DesktopComponentKind.AudioVisualizer);
            Check(secondarySpectrum.IsVisible, "复制启用频谱的档案后副屏应显示频谱");
            Check(secondarySpectrum.FindName("SpectrumHost") is ContentControl { Content: AudioSpectrumView { BarWidth: 0.67d } },
                "复制到副屏的频谱窗口应使用自定义柱宽");
            Check(host.ResetPlacementForScreen(SecondaryHardwareKey, secondarySpectrum, DesktopComponentKind.AudioVisualizer),
                "副屏频谱应可恢复默认位置");

            // 3. QuickPlaces button-move & drag-reconcile preserve custom list and reset source screen.
            state.QuickPlaces =
            [
                new QuickPlaceState { Icon = QuickPlaceRules.FolderIcon, Name = "专属工程", Target = @"C:\Projects" }
            ];
            state.DesktopExperience.GetComponent(DesktopComponentKind.QuickLaunch).IsVisible = true;
            state.MultiScreen!.Screens[SecondaryHardwareKey].Experience.GetComponent(DesktopComponentKind.QuickLaunch).IsVisible = false;
            var quickWin = new QuickPlacesWindow { ShowInTaskbar = false };
            quickWin.SetItems(state.QuickPlaces);
            windows.Add(quickWin);
            show(quickWin);
            pump();

            var moveQuickRes = host.MoveWindowToScreen(quickWin, SecondaryHardwareKey, activeMonitors);
            pump();
            Check(moveQuickRes.Succeeded && host.IsSecondaryWindow(quickWin),
                $"按钮移屏快捷入口应成功，实际：{moveQuickRes.Message}");
            Check(ScreenProfileAccessor.GetQuickPlaces(state, SecondaryHardwareKey).Any(p => p.Target == @"C:\Projects")
                && !state.QuickPlaces.Any(p => p.Target == @"C:\Projects"),
                "按钮移屏快捷入口必须连同自定义列表搬迁并重置源屏列表");

            // Drag QuickPlaces back onto primary monitor and run ReconcileWindowScreenAfterMove.
            var quickHandle = new WindowInteropHelper(quickWin).Handle;
            SetWindowPos(quickHandle, 0, 120, 120, 320, 64, SwpNoZOrder | SwpNoActivate);
            pump();
            Check(host.ReconcileWindowScreenAfterMove(quickWin) && !host.IsSecondaryWindow(quickWin),
                "拖拽快捷入口回主屏后应自动切换归属");
            Check(state.QuickPlaces.Any(p => p.Target == @"C:\Projects")
                && !ScreenProfileAccessor.GetQuickPlaces(state, SecondaryHardwareKey).Any(p => p.Target == @"C:\Projects"),
                "拖拽快捷入口跨屏后必须同步搬迁自定义列表并重置源屏");

            // 4. Add a secondary Note and move it to Primary keeping its text intact.
            var noteWin = host.AddWidgetOnScreen(SecondaryHardwareKey, NewWidgetKind.Note) as NoteWidgetWindow
                ?? throw new InvalidOperationException("副屏新建便签失败");
            noteWin.State.Content = "副屏迁移测试正文";
            pump();
            Check(host.IsSecondaryWindow(noteWin) && noteWin.State.ScreenKey == SecondaryHardwareKey,
                "副屏新建便签应归属副屏");

            var moveResult = host.MoveWindowToScreen(noteWin, targetScreenKey: null, activeMonitors);
            pump();
            Check(moveResult.Succeeded && !host.IsSecondaryWindow(noteWin)
                && noteWin.State.ScreenKey is null && noteWin.State.Content == "副屏迁移测试正文",
                "跨屏移回主屏后便签正文与归属应正确更新");

            // 5. Cross-screen drag of a FileBox must clear EdgeDock anchor metadata.
            var boxWin = host.AddWidgetOnScreen(SecondaryHardwareKey, NewWidgetKind.FileBox) as FileBoxWindow
                ?? throw new InvalidOperationException("副屏新建收纳盒失败");
            boxWin.State.EdgeDockSide = FileBoxEdgeDockSide.Right;
            boxWin.State.EdgeDockOffsetDip = 88d;
            boxWin.State.EdgeDockMonitorDeviceName = @"\\.\DISPLAY1";
            var boxHandle = new WindowInteropHelper(boxWin).Handle;
            SetWindowPos(boxHandle, 0, 200, 180, 360, 300, SwpNoZOrder | SwpNoActivate);
            pump();
            Check(host.ReconcileWindowScreenAfterMove(boxWin)
                && !host.IsSecondaryWindow(boxWin)
                && boxWin.State.ScreenKey is null
                && boxWin.State.EdgeDockSide is null
                && boxWin.State.EdgeDockOffsetDip is null
                && boxWin.State.EdgeDockMonitorDeviceName is null,
                "跨屏拖拽文件收纳盒后必须更新 ScreenKey 并清空原屏贴边停靠锚点");

            // 6. Hot-unplug DISPLAY1: secondary windows MUST Close() (not Hide()), while JSON retains profile.
            var beforeUnplugWindows = host.GetWindowsForSecondaryScreen(SecondaryHardwareKey).ToList();
            Check(beforeUnplugWindows.Count >= 2, "拔屏前副屏应存在活窗口");
            activeMonitors = primaryOnly;
            host.SyncConnectedScreens(activeMonitors);
            pump();
            Check(host.SecondaryWindowCount == 0,
                $"副屏拔出后 SecondaryWindowCount 必须归零，实际 {host.SecondaryWindowCount}");
            Check(beforeUnplugWindows.All(w => !w.IsVisible && !w.IsLoaded && !windows.Contains(w)),
                "副屏拔出后所有副屏窗口必须彻底 Close 销毁并移出窗口列表，绝不能仅 Hide 驻留");
            Check(state.MultiScreen!.Screens.ContainsKey(SecondaryHardwareKey),
                "副屏拔出后 JSON 副屏配置必须保留以便重连恢复");

            // 7. Replug DISPLAY1 even with a changed GDI device name (DISPLAY9) -> restores by MonitorKey!
            activeMonitors = repluggedDualMonitors;
            host.SyncConnectedScreens(activeMonitors);
            pump();
            Check(host.SecondaryWindowCount >= 2,
                $"副屏改号重连（DISPLAY9 但硬件 MonitorKey 不变）后应自动恢复副屏窗口，实际 {host.SecondaryWindowCount}");

            Check(host.GetWindowsForSecondaryScreen(SecondaryHardwareKey).OfType<InformationWidgetWindow>()
                .Any(window => window.ComponentKind == DesktopComponentKind.AudioVisualizer && window.IsVisible),
                "重连后应依据副屏档案恢复频谱");

            // 8. Disabling / Disposing MultiScreenHost destroys every secondary window cleanly.
            var beforeDisposeWindows = host.GetWindowsForSecondaryScreen(SecondaryHardwareKey).ToList();
            host.Dispose();
            pump();
            Check(host.SecondaryWindowCount == 0
                && beforeDisposeWindows.All(w => !w.IsVisible && !w.IsLoaded && !windows.Contains(w)),
                "关闭多屏并释放 MultiScreenHost 后所有副屏窗口必须彻底 Close 销毁");
        }
        finally
        {
            host.Dispose();
            foreach (var win in windows.ToList())
            {
                win.CanClose = true;
                try { win.Close(); } catch (InvalidOperationException) { }
            }
            windows.Clear();
            pump();
        }
    }

    private static void TestRealCoordinatorToggleAndPrimaryOrganizeIsolation(Action pump)
    {
        var temp = Path.Combine(Path.GetTempPath(), "binglan-ms-coord-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        MonitorIdentityResolver.ResolveOverrideForTests = () => CreateDualMonitors(@"\\.\DISPLAY1");

        var coordinator = new WidgetCoordinator(interactiveQa: true, dataDirectory: temp);
        try
        {
            Check(!coordinator.HasActiveMultiScreenHost && coordinator.SecondaryWindowCount == 0,
                "默认关闭多屏时真实 WidgetCoordinator 不得初始化 MultiScreenHost");

            coordinator.SetMultiScreenEnabled(true);
            pump();
            Check(coordinator.HasActiveMultiScreenHost, "开启多屏后真实 WidgetCoordinator 应懒加载 MultiScreenHost");

            var host = coordinator.ActiveMultiScreenHost!;
            host.CopyPrimaryProfileToScreen(SecondaryHardwareKey);
            var secondaryFolderBox = host.AddWidgetOnScreen(SecondaryHardwareKey, NewWidgetKind.FileBox) as FileBoxWindow
                ?? throw new InvalidOperationException("副屏新建文件夹收纳盒失败");
            secondaryFolderBox.State.DesktopCategory = DesktopGroupCategory.Folders;
            secondaryFolderBox.State.CollectFolders = true;
            pump();

            // Verify FolderBoxTarget() never picks a secondary screen's FileBoxWindow.
            var folderBoxMethod = typeof(WidgetCoordinator).GetMethod(
                "FolderBoxTarget", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("找不到 WidgetCoordinator.FolderBoxTarget");
            var picked = folderBoxMethod.Invoke(coordinator, null);
            Check(picked is null || !ReferenceEquals(
                    picked.GetType().GetField("Item1")?.GetValue(picked) ?? picked.GetType().GetProperty("Window")?.GetValue(picked),
                    secondaryFolderBox),
                "一键整理桌面的 FolderBoxTarget() 严禁命中副屏收纳盒");

            var liveBeforeDisable = coordinator.GetWindowsForScreen(SecondaryHardwareKey).ToList();
            Check(liveBeforeDisable.Count >= 3, $"关闭多屏前副屏应有活窗口，实际 {liveBeforeDisable.Count}");

            // Turning multi-screen OFF on real WidgetCoordinator must dispose host and hard-Close every secondary window.
            coordinator.SetMultiScreenEnabled(false);
            pump();
            Check(!coordinator.HasActiveMultiScreenHost && coordinator.SecondaryWindowCount == 0,
                "真实 WidgetCoordinator.SetMultiScreenEnabled(false) 后 MultiScreenHost 必须置空且副屏窗口数归零");
            Check(liveBeforeDisable.All(w => !w.IsVisible && !w.IsLoaded),
                "真实 WidgetCoordinator 关闭多屏后所有副屏窗口必须彻底 Close 销毁");
        }
        finally
        {
            DisposeCoordinatorWindows(coordinator);
            coordinator.Dispose();
            pump();
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static void TestDualScreenWeatherDedupWithRunningSampler(Action<Window> show, Action pump)
    {
        const string forecastJson = """
            {
              "current": { "temperature_2m": 31.5, "weather_code": 2 },
              "daily": { "temperature_2m_max": [34.0], "temperature_2m_min": [22.0] }
            }
            """;
        var calls = 0;
        var handler = new CountingHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(forecastJson)
            });
        });

        using var sampler = new WindowsPerformanceSamplingService();
        sampler.Start();
        var weather = new WeatherService(handler);
        var infoState = new InformationWidgetState
        {
            WeatherCity = "北京",
            WeatherLatitude = 39.9,
            WeatherLongitude = 116.4
        };
        var primaryExp = DesktopExperienceRules.CreateDefault();
        var secondaryExp = DesktopExperienceRules.CreateDefault();
        primaryExp.GetComponent(DesktopComponentKind.Weather).IsVisible = true;
        secondaryExp.GetComponent(DesktopComponentKind.Weather).IsVisible = true;

        var primaryWeather = new InformationWidgetWindow(infoState, sampler, weather, primaryExp, DesktopComponentKind.Weather)
        {
            ShowInTaskbar = false
        };
        var secondaryWeather = new InformationWidgetWindow(infoState, sampler, weather, secondaryExp, DesktopComponentKind.Weather)
        {
            ShowInTaskbar = false
        };
        show(primaryWeather);
        show(secondaryWeather);
        primaryWeather.RefreshWeatherIfNeeded();
        secondaryWeather.RefreshWeatherIfNeeded();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            var primaryTemp = (TextBlock)primaryWeather.FindName("WeatherTempText")!;
            var secondaryTemp = (TextBlock)secondaryWeather.FindName("WeatherTempText")!;
            while (DateTime.UtcNow < deadline
                && (!primaryTemp.Text.Contains("31.5") || !secondaryTemp.Text.Contains("31.5")))
            {
                pump();
                Thread.Sleep(20);
            }

            Check(primaryTemp.Text.Contains("31.5") && secondaryTemp.Text.Contains("31.5"),
                $"性能采样运行中时主副屏天气卡均应完成首次刷新，实际 主={primaryTemp.Text} 副={secondaryTemp.Text}");
            Check(calls == 1,
                $"主副屏双天气卡共享 WeatherService 首次刷新应合并为 1 次 HTTP 请求，实际 {calls} 次");
        }
        finally
        {
            primaryWeather.CanClose = true;
            secondaryWeather.CanClose = true;
            primaryWeather.Close();
            secondaryWeather.Close();
            sampler.Stop();
            pump();
        }
    }

    private static void DisposeCoordinatorWindows(WidgetCoordinator coordinator)
    {
        var field = typeof(WidgetCoordinator).GetField("_windows", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(coordinator) is List<WidgetWindowBase> windows)
        {
            foreach (var win in windows.ToList())
            {
                win.CanClose = true;
                try { win.Close(); } catch (InvalidOperationException) { }
            }
            windows.Clear();
        }
    }

    private static IReadOnlyList<ResolvedMonitor> CreatePrimaryOnlyMonitors() =>
    [
        new ResolvedMonitor(
            MonitorKey: PrimaryHardwareKey,
            HardwareId: "DISPLAY#PRIMARY#0",
            DeviceName: @"\\.\DISPLAY2",
            FriendlyName: "主屏测试",
            DisplayLabel: "主屏 · 主屏测试",
            DisplayIndex: 1,
            MonitorHandle: 101,
            Bounds: new PixelRect(0, 0, 2048, 1152),
            WorkingArea: new PixelRect(0, 0, 2048, 1104),
            IsPrimary: true,
            Dpi: 120) { IdentityKind = MonitorIdentityKind.DeviceInterfacePath }
    ];

    internal static IReadOnlyList<ResolvedMonitor> CreateDualMonitors(string secondaryGdiName) =>
    [
        new ResolvedMonitor(
            MonitorKey: PrimaryHardwareKey,
            HardwareId: "DISPLAY#PRIMARY#0",
            DeviceName: @"\\.\DISPLAY2",
            FriendlyName: "主屏测试",
            DisplayLabel: "主屏 · 主屏测试",
            DisplayIndex: 1,
            MonitorHandle: 101,
            Bounds: new PixelRect(0, 0, 2048, 1152),
            WorkingArea: new PixelRect(0, 0, 2048, 1104),
            IsPrimary: true,
            Dpi: 120) { IdentityKind = MonitorIdentityKind.DeviceInterfacePath },
        new ResolvedMonitor(
            MonitorKey: SecondaryHardwareKey,
            HardwareId: SecondaryHardwareKey,
            DeviceName: secondaryGdiName,
            FriendlyName: "副屏测试",
            DisplayLabel: "副屏 2 · 副屏测试",
            DisplayIndex: 2,
            MonitorHandle: 202,
            Bounds: new PixelRect(2048, -50, 3755, 1017),
            WorkingArea: new PixelRect(2048, -50, 3755, 977),
            IsPrimary: false,
            Dpi: 96) { IdentityKind = MonitorIdentityKind.DeviceInterfacePath }
    ];

    private sealed class CountingHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private static void TestDisconnectRebootAndReconnectTodoMemory(Action pump)
    {
        var temp = Path.Combine(Path.GetTempPath(), "binglan-ms-reconn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var activeMonitors = CreateDualMonitors(@"\\.\DISPLAY1");
        MonitorIdentityResolver.ResolveOverrideForTests = () => activeMonitors;

        Guid todoId;
        var applyDisplayRecovery = typeof(WidgetCoordinator).GetMethod(
            "ApplyDisplayRecovery", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("找不到 WidgetCoordinator.ApplyDisplayRecovery");
        var getStateField = typeof(WidgetCoordinator).GetField(
            "_state", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("找不到 WidgetCoordinator._state");

        var coordinator = new WidgetCoordinator(interactiveQa: true, dataDirectory: temp);
        try
        {
            coordinator.Start();
            coordinator.SetMultiScreenEnabled(true);
            pump();

            var host = coordinator.ActiveMultiScreenHost!;
            var todoWin = host.AddWidgetOnScreen(SecondaryHardwareKey, NewWidgetKind.Todo) as TodoWidgetWindow
                ?? throw new InvalidOperationException("副屏新建待办失败");
            pump();
            todoId = todoWin.State.Id;
            var addTodoItemMethod = typeof(TodoWidgetWindow).GetMethod(
                "Add_Click", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("找不到 TodoWidgetWindow.Add_Click");
            addTodoItemMethod.Invoke(todoWin, [todoWin, new RoutedEventArgs()]);
            pump();
            todoWin.State.Items.Single().Text = "出差前副屏待办";
            todoWin.CaptureState();
            pump();

            var hwnd = new WindowInteropHelper(todoWin).Handle;
            Check(hwnd != 0, "副屏待办应具备有效 HWND");
            // Move & resize secondary Todo to local (240, 180) with size 420x480 inside DISPLAY1 (2048, -50).
            SetWindowPos(hwnd, 0, 2048 + 240, -50 + 180, 420, 480, SwpNoZOrder | SwpNoActivate);
            if (!GetWindowRect(hwnd, out var immediateRect)
                || immediateRect.Left != 2048 + 240
                || immediateRect.Top != -50 + 180)
            {
                throw new InvalidOperationException(
                    $"SetWindowPos 后 HWND 立即位置异常：({immediateRect.Left},{immediateRect.Top},{immediateRect.Right - immediateRect.Left}x{immediateRect.Bottom - immediateRect.Top})");
            }
            pump();

            var state = (AppState)getStateField.GetValue(coordinator)!;
            var savedLocal = todoWin.State.Placement.MonitorLocal
                ?? throw new InvalidOperationException("副屏定位后应记录 MonitorLocal 相对坐标");
            Check(Math.Abs(savedLocal.LocalLeftDip - 240d) <= 2d
                && Math.Abs(savedLocal.LocalTopDip - 180d) <= 2d
                && Math.Abs(savedLocal.WidthDip - 420d) <= 2d
                && Math.Abs(savedLocal.HeightDip - 480d) <= 2d,
                $"副屏调整位置与大小后应记住 (240,180,420x480)，实际 ({savedLocal.LocalLeftDip:F1},{savedLocal.LocalTopDip:F1},{savedLocal.WidthDip:F1}x{savedLocal.HeightDip:F1})");

            // Exact Windows disconnect race: Windows detaches DISPLAY1 and synchronously pushes the
            // secondary Todo window onto the Primary monitor BEFORE DisplaySettingsChanged fires!
            activeMonitors = CreatePrimaryOnlyMonitors();
            SetWindowPos(hwnd, 0, 100, 100, 300, 260, SwpNoZOrder | SwpNoActivate);
            pump();

            coordinator.HandleMultiScreenDisplayLayoutChanged();
            applyDisplayRecovery.Invoke(coordinator, null);
            pump();

            Check(coordinator.SecondaryWindowCount == 0 && coordinator.HasActiveMultiScreenHost,
                $"断线切成单屏后宿主应保留且离线屏窗口归零，实际 SecondaryWindowCount={coordinator.SecondaryWindowCount}");
            Check(!todoWin.IsVisible && !todoWin.IsLoaded,
                "断线后副屏待办窗口应彻底 Close 销毁");
            Check(todoWin.State.ScreenKey == SecondaryHardwareKey,
                $"断线系统推回主屏不得劫持待办归属，实际 ScreenKey={todoWin.State.ScreenKey ?? "null"}");
            Check(state.MultiScreen!.Screens.TryGetValue(SecondaryHardwareKey, out var secProfile)
                && secProfile.ParsedPrimaryTodoId == todoId
                && secProfile.Experience.GetComponent(DesktopComponentKind.Todo).IsVisible,
                "断线后副屏 PrimaryTodoId 与 Todo 可见状态必须保持 intact");
            var afterDisconnectLocal = todoWin.State.Placement.MonitorLocal
                ?? throw new InvalidOperationException("断线后 MonitorLocal 不得丢失");
            Check(Math.Abs(afterDisconnectLocal.LocalLeftDip - 240d) <= 2d
                && Math.Abs(afterDisconnectLocal.LocalTopDip - 180d) <= 2d
                && Math.Abs(afterDisconnectLocal.WidthDip - 420d) <= 2d
                && Math.Abs(afterDisconnectLocal.HeightDip - 480d) <= 2d,
                $"断线时系统推回主屏不得覆写已保存坐标尺寸，实际 ({afterDisconnectLocal.LocalLeftDip:F1},{afterDisconnectLocal.LocalTopDip:F1},{afterDisconnectLocal.WidthDip:F1}x{afterDisconnectLocal.HeightDip:F1})");
        }
        finally
        {
            DisposeCoordinatorWindows(coordinator);
            coordinator.Dispose();
            pump();
        }

        // Reboot cycle 1 on single screen (simulating multi-day business trip) + save, then cycle 2, then reconnect!
        var saveNowMethod = typeof(WidgetCoordinator).GetMethod(
            "SaveNow", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("找不到 WidgetCoordinator.SaveNow");
        var coordinator2a = new WidgetCoordinator(interactiveQa: true, dataDirectory: temp);
        try
        {
            coordinator2a.Start();
            pump();
            Check(coordinator2a.HasActiveMultiScreenHost && coordinator2a.SecondaryWindowCount == 0,
                "单屏重启第 1 轮应保留多屏宿主但不创建离线屏窗口");
            saveNowMethod.Invoke(coordinator2a, null);
        }
        finally
        {
            DisposeCoordinatorWindows(coordinator2a);
            coordinator2a.Dispose();
            pump();
        }

        var coordinator2 = new WidgetCoordinator(interactiveQa: true, dataDirectory: temp);
        try
        {
            coordinator2.Start();
            pump();
            Check(coordinator2.HasActiveMultiScreenHost && coordinator2.SecondaryWindowCount == 0,
                "单屏重启第 2 轮应保留多屏宿主但不创建离线屏窗口");
            saveNowMethod.Invoke(coordinator2, null);

            var state2 = (AppState)getStateField.GetValue(coordinator2)!;
            var persistedTodo = state2.TodoWidgets.Single(t => t.Id == todoId);
            Check(persistedTodo.ScreenKey == SecondaryHardwareKey
                && persistedTodo.Items.Any(i => i.Text == "出差前副屏待办")
                && persistedTodo.Placement.MonitorLocal is { } preLocal
                && Math.Abs(preLocal.LocalLeftDip - 240d) <= 2d
                && !persistedTodo.Placement.DisplayLayouts.Any(d =>
                    d.Left == 100 && d.Top == 100 && (d.Right - d.Left) == 300 && (d.Bottom - d.Top) == 260),
                "单屏重启后副屏待办归属/内容/MonitorLocal应完整保留且关闭副屏窗口不得污染 DisplayLayouts");

            // Reconnect A: same dual-monitor topology.
            activeMonitors = CreateDualMonitors(@"\\.\DISPLAY9");
            coordinator2.HandleMultiScreenDisplayLayoutChanged();
            var restoredTodoWin = coordinator2.GetWindowsForScreen(SecondaryHardwareKey)
                .OfType<TodoWidgetWindow>()
                .Single(w => w.State.Id == todoId);
            var restoredHandle = new WindowInteropHelper(restoredTodoWin).Handle;
            applyDisplayRecovery.Invoke(coordinator2, null);
            pump();
            if (restoredHandle == 0 || !GetWindowRect(restoredHandle, out var restoredRect))
            {
                throw new InvalidOperationException("重新投屏后副屏待办窗口应自动重建显示");
            }
            Check(Math.Abs(restoredRect.Left - (2048 + 240)) <= 2
                && Math.Abs(restoredRect.Top - (-50 + 180)) <= 2
                && Math.Abs((restoredRect.Right - restoredRect.Left) - 420) <= 2
                && Math.Abs((restoredRect.Bottom - restoredRect.Top) - 480) <= 2,
                $"重新投屏后副屏待办应直接恢复原位置与大小 (2288,130,420x480)，实际 ({restoredRect.Left},{restoredRect.Top},{restoredRect.Right - restoredRect.Left}x{restoredRect.Bottom - restoredRect.Top})");

            // A same-model replacement with a different exact device instance remains blank.
            activeMonitors = CreatePrimaryOnlyMonitors();
            coordinator2.HandleMultiScreenDisplayLayoutChanged();
            pump();
            Check(coordinator2.SecondaryWindowCount == 0, "二次断线应再次销毁全部副屏窗口");

            const string newPortKey = "DISPLAY#TESTMON2#5&9F8E7D&0&UID9999";
            activeMonitors =
            [
                new ResolvedMonitor(
                    MonitorKey: PrimaryHardwareKey,
                    HardwareId: "DISPLAY#PRIMARY#0",
                    DeviceName: @"\\.\DISPLAY2",
                    FriendlyName: "主屏测试",
                    DisplayLabel: "主屏 · 主屏测试",
                    DisplayIndex: 1,
                    MonitorHandle: 101,
                    Bounds: new PixelRect(0, 0, 2048, 1152),
                    WorkingArea: new PixelRect(0, 0, 2048, 1104),
                    IsPrimary: true,
                    Dpi: 120) { IdentityKind = MonitorIdentityKind.DeviceInterfacePath },
                new ResolvedMonitor(
                    MonitorKey: newPortKey,
                    HardwareId: newPortKey,
                    DeviceName: @"\\.\DISPLAY5",
                    FriendlyName: "副屏测试",
                    DisplayLabel: "副屏 2 · 副屏测试",
                    DisplayIndex: 2,
                    MonitorHandle: 303,
                    Bounds: new PixelRect(-2134, 0, 0, 1334),
                    WorkingArea: new PixelRect(-2134, 0, 0, 1284),
                    IsPrimary: false,
                    Dpi: 120) { IdentityKind = MonitorIdentityKind.DeviceInterfacePath }
            ];
            coordinator2.HandleMultiScreenDisplayLayoutChanged();
            applyDisplayRecovery.Invoke(coordinator2, null);
            pump();

            Check(state2.MultiScreen!.Screens.ContainsKey(SecondaryHardwareKey)
                && persistedTodo.ScreenKey == SecondaryHardwareKey
                && coordinator2.GetWindowsForScreen(newPortKey).Count == 0,
                "同型号且精确实例不同的显示器应保持空白，原档案保持离线归属");

            activeMonitors = [activeMonitors[0], activeMonitors[1] with
            {
                MonitorKey = SecondaryHardwareKey,
                HardwareId = SecondaryHardwareKey
            }];
            coordinator2.HandleMultiScreenDisplayLayoutChanged();
            applyDisplayRecovery.Invoke(coordinator2, null);
            pump();

            var migratedTodoWin = coordinator2.GetWindowsForScreen(SecondaryHardwareKey)
                .OfType<TodoWidgetWindow>()
                .Single(w => w.State.Id == todoId);
            var migratedHandle = new WindowInteropHelper(migratedTodoWin).Handle;
            if (migratedHandle == 0 || !GetWindowRect(migratedHandle, out var migratedRect))
            {
                throw new InvalidOperationException("原物理显示器重连后副屏待办应成功恢复");
            }
            Check(migratedRect.Left >= -2134 && migratedRect.Right <= 0
                && Math.Abs(migratedRect.Left - (-2134 + 300)) <= 4
                && Math.Abs(migratedRect.Top - 225) <= 4
                && Math.Abs((migratedRect.Right - migratedRect.Left) - 525) <= 4
                && Math.Abs((migratedRect.Bottom - migratedRect.Top) - 600) <= 4,
                $"原屏置于左侧并以 125% DPI 重连后应保持相对位置和 DIP 尺寸 (-1834,225,525x600)，实际 ({migratedRect.Left},{migratedRect.Top},{migratedRect.Right - migratedRect.Left}x{migratedRect.Bottom - migratedRect.Top})");

            // Connecting a brand-new unfamiliar monitor must remain completely blank.
            activeMonitors =
            [
                activeMonitors[0],
                new ResolvedMonitor(
                    MonitorKey: "DISPLAY#OTHER999#5&111&0&UID1",
                    HardwareId: "DISPLAY#OTHER999#5&111&0&UID1",
                    DeviceName: @"\\.\DISPLAY6",
                    FriendlyName: "陌生会议大屏",
                    DisplayLabel: "副屏 2 · 陌生会议大屏",
                    DisplayIndex: 2,
                    MonitorHandle: 404,
                    Bounds: new PixelRect(2048, 0, 3968, 1080),
                    WorkingArea: new PixelRect(2048, 0, 3968, 1040),
                    IsPrimary: false,
                    Dpi: 96) { IdentityKind = MonitorIdentityKind.DeviceInterfacePath }
            ];
            coordinator2.HandleMultiScreenDisplayLayoutChanged();
            pump();
            Check(coordinator2.SecondaryWindowCount == 0 && state2.MultiScreen.Screens.ContainsKey(SecondaryHardwareKey),
                "接入陌生显示器必须保持空白且不覆盖已有副屏记忆档案");
        }
        finally
        {
            DisposeCoordinatorWindows(coordinator2);
            coordinator2.Dispose();
            pump();
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static void TestUserDragCompletedCrossScreenOwnership(Action pump)
    {
        var temp = Path.Combine(Path.GetTempPath(), "binglan-ms-drag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var activeMonitors = CreateDualMonitors(@"\\.\DISPLAY1");
        MonitorIdentityResolver.ResolveOverrideForTests = () => activeMonitors;

        var coordinator = new WidgetCoordinator(interactiveQa: true, dataDirectory: temp);
        try
        {
            coordinator.Start();
            coordinator.SetMultiScreenEnabled(true);
            pump();

            var host = coordinator.ActiveMultiScreenHost!;
            var todoWin = host.AddWidgetOnScreen(SecondaryHardwareKey, NewWidgetKind.Todo) as TodoWidgetWindow
                ?? throw new InvalidOperationException("副屏新建待办失败");
            var boxWin = host.AddWidgetOnScreen(SecondaryHardwareKey, NewWidgetKind.FileBox) as FileBoxWindow
                ?? throw new InvalidOperationException("副屏新建收纳盒失败");
            pump();

            // Simulate an authentic user drag (BeginDrag -> DragTo across onto Primary -> EndDrag) on both Todo & FileBox!
            var todoHandle = new WindowInteropHelper(todoWin).Handle;
            var boxHandle = new WindowInteropHelper(boxWin).Handle;
            GetWindowRect(todoHandle, out var todoStart);
            todoWin.BeginDrag(todoStart.Left + 20, todoStart.Top + 20);
            todoWin.DragTo(120, 120);
            todoWin.EndDrag();

            GetWindowRect(boxHandle, out var boxStart);
            boxWin.BeginDrag(boxStart.Left + 20, boxStart.Top + 20);
            boxWin.DragTo(460, 140);
            boxWin.EndDrag();
            pump();

            Check(todoWin.State.ScreenKey is null && host.TryGetScreenKey(todoWin) is null,
                "用户真实手动拖拽待办回主屏（EndDrag）后应切换为主屏归属");
            Check(boxWin.State.ScreenKey is null && host.TryGetScreenKey(boxWin) is null,
                "用户真实手动拖拽文件收纳盒回主屏（EndDrag -> base.OnDragEnded）后应切换为主屏归属");
        }
        finally
        {
            DisposeCoordinatorWindows(coordinator);
            coordinator.Dispose();
            pump();
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static void TestQueuedDisplayLayoutAfterDisposeDoesNotResurrectMultiScreenHost(Action pump)
    {
        var temp = Path.Combine(Path.GetTempPath(), "binglan-ms-dispqueue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var activeMonitors = CreateDualMonitors(@"\\.\DISPLAY1");
        MonitorIdentityResolver.ResolveOverrideForTests = () => activeMonitors;

        var coordinator = new WidgetCoordinator(interactiveQa: true, dataDirectory: temp);
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            coordinator.Start();
            coordinator.SetMultiScreenEnabled(true);
            pump();

            var host = coordinator.ActiveMultiScreenHost!;
            _ = host.AddWidgetOnScreen(SecondaryHardwareKey, NewWidgetKind.Todo) as TodoWidgetWindow
                ?? throw new InvalidOperationException("排队布局销毁测试创建副屏待办失败");
            pump();
            Check(coordinator.HasActiveMultiScreenHost && coordinator.SecondaryWindowCount >= 1,
                "前置条件：应处于双屏活动且存在副屏窗口的状态");

            // Queue a real Dispatcher.BeginInvoke(ContextIdle, ...) layout callback, then dispose before draining.
            var onDisplayLayoutChanged = typeof(WidgetCoordinator).GetMethod("OnDisplayLayoutChanged", Private)
                ?? throw new InvalidOperationException("找不到 WidgetCoordinator.OnDisplayLayoutChanged");
            onDisplayLayoutChanged.Invoke(coordinator, null);

            DisposeCoordinatorWindows(coordinator);
            coordinator.Dispose();
            var saveTimer = (System.Windows.Threading.DispatcherTimer)typeof(WidgetCoordinator)
                .GetField("_saveTimer", Private)!
                .GetValue(coordinator)!;
            // Standalone PR-4 is based on main (PR-2 adds _saveTimer.Stop() inside Dispose); stop here so
            // any post-Dispose _saveTimer.IsEnabled == true isolates a restart from the queued callback.
            saveTimer.Stop();

            // Drain queued ContextIdle callbacks: must NOT resurrect _multiScreen or secondary windows or _saveTimer.
            pump();

            Check(!coordinator.HasActiveMultiScreenHost && coordinator.SecondaryWindowCount == 0,
                $"Dispose 后到来的已排队 OnDisplayLayoutChanged 不得复活 MultiScreenHost 或副屏窗口（Actual Host={coordinator.HasActiveMultiScreenHost}, Count={coordinator.SecondaryWindowCount}）");
            Check(!saveTimer.IsEnabled, "Dispose 后已排队的 OnDisplayLayoutChanged 不得重启 _saveTimer");
        }
        finally
        {
            DisposeCoordinatorWindows(coordinator);
            coordinator.Dispose();
            pump();
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static void TestBucket1EdgeCasesIntegration(Action pump)
    {
        var temp = Path.Combine(Path.GetTempPath(), "binglan-ms-b1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var activeMonitors = CreateDualMonitors(@"\\.\DISPLAY1");
        MonitorIdentityResolver.ResolveOverrideForTests = () => activeMonitors;

        var coordinator = new WidgetCoordinator(interactiveQa: true, dataDirectory: temp);
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        var toggleDesktop = typeof(WidgetCoordinator).GetMethod("ToggleDesktop", Private)!;
        var applyDesktopExp = typeof(WidgetCoordinator).GetMethod(
            "ApplyDesktopExperience", Private, null, [typeof(DesktopExperienceState)], null)!;
        var getWindowsField = typeof(WidgetCoordinator).GetField("_windows", Private)!;
        var getStateField = typeof(WidgetCoordinator).GetField("_state", Private)!;
        var getMultiScreenField = typeof(WidgetCoordinator).GetField("_multiScreen", Private)!;

        try
        {
            coordinator.Start();
            coordinator.SetMultiScreenEnabled(true);
            pump();

            var hostRef = coordinator.ActiveMultiScreenHost!;
            var secTodo = hostRef.AddWidgetOnScreen(SecondaryHardwareKey, NewWidgetKind.Todo) as TodoWidgetWindow
                ?? throw new InvalidOperationException("P1-1 测试创建副屏待办失败");
            pump();

            // P1-1: hide desktop -> unplug -> replug -> show desktop -> secondary Todo must become visible.
            toggleDesktop.Invoke(coordinator, null);
            pump();
            Check(!secTodo.IsVisible, "隐藏桌面组件后副屏待办应隐藏");

            activeMonitors = CreatePrimaryOnlyMonitors();
            coordinator.HandleMultiScreenDisplayLayoutChanged();
            pump();
            Check(coordinator.SecondaryWindowCount == 0, "隐藏态断线仍应销毁副屏窗口");

            activeMonitors = CreateDualMonitors(@"\\.\DISPLAY1");
            coordinator.HandleMultiScreenDisplayLayoutChanged();
            pump();

            toggleDesktop.Invoke(coordinator, null);
            pump();
            var restoredSecTodo = coordinator.GetWindowsForScreen(SecondaryHardwareKey)
                .OfType<TodoWidgetWindow>()
                .Single(w => w.State.Id == secTodo.State.Id);
            Check(restoredSecTodo.IsVisible,
                "P1-1 回归：隐藏桌面期间副屏重连，恢复显示后副屏待办必须可见");

            // P1-3: Primary once showed then hid Weather & QuickLaunch -> move secondary Weather & QuickLaunch
            // back to Primary -> ApplyDesktopExperience must leave exactly 1 window of each.
            var state = (AppState)getStateField.GetValue(coordinator)!;
            var windows = (List<WidgetWindowBase>)getWindowsField.GetValue(coordinator)!;
            var primaryWeatherComp = state.DesktopExperience.GetComponent(DesktopComponentKind.Weather);
            var primaryQuickComp = state.DesktopExperience.GetComponent(DesktopComponentKind.QuickLaunch);
            primaryWeatherComp.IsVisible = true;
            primaryQuickComp.IsVisible = true;
            applyDesktopExp.Invoke(coordinator, [state.DesktopExperience]);
            pump();
            primaryWeatherComp.IsVisible = false;
            primaryQuickComp.IsVisible = false;
            applyDesktopExp.Invoke(coordinator, [state.DesktopExperience]);
            pump();

            var secProfile = ScreenProfileAccessor.EnsureScreenProfile(state, SecondaryHardwareKey);
            secProfile.Experience.GetComponent(DesktopComponentKind.Weather).IsVisible = true;
            secProfile.Experience.GetComponent(DesktopComponentKind.QuickLaunch).IsVisible = true;
            coordinator.HandleMultiScreenDisplayLayoutChanged();
            pump();

            var secWeatherWin = coordinator.GetWindowsForScreen(SecondaryHardwareKey)
                .OfType<InformationWidgetWindow>()
                .Single(w => w.ComponentKind == DesktopComponentKind.Weather);
            var secQuickWin = coordinator.GetWindowsForScreen(SecondaryHardwareKey)
                .OfType<QuickPlacesWindow>()
                .Single();

            Check(coordinator.ActiveMultiScreenHost!.MoveWindowToScreen(secWeatherWin, null, activeMonitors).Succeeded, "副屏天气移回主屏应成功");
            Check(coordinator.ActiveMultiScreenHost!.MoveWindowToScreen(secQuickWin, null, activeMonitors).Succeeded, "副屏快捷入口移回主屏应成功");
            applyDesktopExp.Invoke(coordinator, [state.DesktopExperience]);
            pump();

            var weatherWindows = windows.OfType<InformationWidgetWindow>()
                .Count(w => w.ComponentKind == DesktopComponentKind.Weather);
            var quickWindows = windows.OfType<QuickPlacesWindow>().Count();
            Check(weatherWindows == 1 && quickWindows == 1,
                $"P1-3 回归：移回主屏后不应存在重复隐藏旧窗（Weather={weatherWindows}, Quick={quickWindows}）");

            // P1-4: Two primary Todos (A at index 0, B at index 1) -> moving A to secondary must sync B's own placement
            // so ApplyDesktopExperience does not jump B to A's spot; moving A back appends to end.
            var addTodoMethod = typeof(WidgetCoordinator).GetMethod("AddTodo", Private)!;
            addTodoMethod.Invoke(coordinator, null);
            pump();
            var primaryTodos = ScreenProfileAccessor.GetTodosForScreen(state, null);
            Check(primaryTodos.Count >= 2, "P1-4 前置条件：主屏应至少有两张待办");
            var todoA = primaryTodos[0];
            var todoB = primaryTodos[1];
            var todoAWin = windows.OfType<TodoWidgetWindow>().Single(w => w.State.Id == todoA.Id);
            var todoBWin = windows.OfType<TodoWidgetWindow>().Single(w => w.State.Id == todoB.Id);
            var owner = activeMonitors.Single(m => m.MonitorKey == PrimaryHardwareKey);
            var layoutKey = MultiScreenHost.CurrentLayoutKey(activeMonitors);
            var targetA = ScreenProfileAccessor.LocalDipToPhysicalBounds(120, 120, 286, 336, owner.WorkingArea, owner.Dpi);
            var targetB = ScreenProfileAccessor.LocalDipToPhysicalBounds(640, 360, 286, 336, owner.WorkingArea, owner.Dpi);
            MultiScreenHost.ApplyPhysicalBounds(todoAWin, targetA, owner.Dpi, todoA.Placement, layoutKey, owner.WorkingArea);
            MultiScreenHost.ApplyPhysicalBounds(todoBWin, targetB, owner.Dpi, todoB.Placement, layoutKey, owner.WorkingArea);
            pump();

            Check(coordinator.ActiveMultiScreenHost!.MoveWindowToScreen(todoAWin, SecondaryHardwareKey, activeMonitors).Succeeded, "主待办 A 移去副屏应成功");
            applyDesktopExp.Invoke(coordinator, [state.DesktopExperience]);
            pump();
            Check(GetWindowRect(todoBWin.WindowHandle, out var remainingTodoBounds)
                && Math.Abs(remainingTodoBounds.Left - targetB.Left) <= 4
                && Math.Abs(remainingTodoBounds.Top - targetB.Top) <= 4,
                $"移走主待办 A 后剩余待办 B 应留在原位，实际 ({remainingTodoBounds.Left},{remainingTodoBounds.Top})");
            Check(coordinator.ActiveMultiScreenHost!.MoveWindowToScreen(todoAWin, null, activeMonitors).Succeeded, "待办 A 移回主屏应成功");
            applyDesktopExp.Invoke(coordinator, [state.DesktopExperience]);
            pump();
            Check(ScreenProfileAccessor.GetPrimaryTodoId(state, null) == todoB.Id,
                "P1-4 回归：待办 A 移回主屏时主待办归属仍应保持 B 不被抢占");

            // A marquee placed in the secondary work area is assigned before showing the window.
            coordinator.AddMarqueeFileBox(new PixelRect(2200, 80, 2520, 340));
            pump();
            var secCreatedBox = state.FileBoxes.Last();
            Check(secCreatedBox.ScreenKey == SecondaryHardwareKey,
                $"P2-1 回归：落在副屏区域的新建文件盒应自动归属副屏，实际 ScreenKey={secCreatedBox.ScreenKey ?? "null"}");

            // P2-13: Collapsed secondary FileBox under capturePlacement:false must keep uncollapsed Placement.Height & MonitorLocal.
            var secBoxWin = coordinator.GetWindowsForScreen(SecondaryHardwareKey)
                .OfType<FileBoxWindow>()
                .Single(w => w.State.Id == secCreatedBox.Id);
            secCreatedBox.IsCollapsed = true;
            secCreatedBox.Placement.Height = 260d;
            var preMl = secCreatedBox.Placement.MonitorLocal is null ? 260d : secCreatedBox.Placement.MonitorLocal.HeightDip;
            var host = getMultiScreenField.GetValue(coordinator)!;
            var captureSec = host.GetType().GetMethod("CaptureSecondaryWindow", Private)!;
            captureSec.Invoke(host, [secBoxWin, false]);
            Check(Math.Abs(secCreatedBox.Placement.Height - 260d) <= 2d,
                $"P2-13 回归：capturePlacement:false 不得让折叠态分组盒覆盖保存高度，实际 Height={secCreatedBox.Placement.Height}");
            if (secCreatedBox.Placement.MonitorLocal is { } mlAfter)
            {
                Check(Math.Abs(mlAfter.HeightDip - preMl) <= 2d, "P2-13 回归：MonitorLocal.HeightDip 应保持不变");
            }
        }
        finally
        {
            DisposeCoordinatorWindows(coordinator);
            coordinator.Dispose();
            pump();
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static void TestSecondaryGroupedFileBoxLifecycleAndCrossScreenDrag(Action pump)
    {
        var temp = Path.Combine(Path.GetTempPath(), "binglan-ms-grp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var activeMonitors = CreateDualMonitors(@"\\.\DISPLAY1");
        MonitorIdentityResolver.ResolveOverrideForTests = () => activeMonitors;

        // Pre-seed two grouped tabs assigned to SecondaryHardwareKey.
        var groupId = Guid.NewGuid();
        var tabA = new FileBoxState
        {
            Title = "副屏组A",
            GroupId = groupId,
            TabOrder = 0,
            IsActiveTab = true,
            ScreenKey = SecondaryHardwareKey,
            Placement = new WindowPlacement
            {
                Left = 2200,
                Top = 80,
                Width = 366,
                Height = 306,
                MonitorLocal = new MonitorLocalPlacement
                {
                    LocalLeftDip = 152,
                    LocalTopDip = 130,
                    WidthDip = 366,
                    HeightDip = 306,
                    SourceWorkWidthDip = 1707,
                    SourceWorkHeightDip = 1027
                }
            }
        };
        var tabB = new FileBoxState
        {
            Title = "副屏组B",
            GroupId = groupId,
            TabOrder = 1,
            IsActiveTab = false,
            ScreenKey = SecondaryHardwareKey,
            Placement = ScreenProfileAccessor.ClonePlacement(tabA.Placement)
        };
        var seed = LocalStateStore.CreateDefault();
        seed.MultiScreen = new MultiScreenState { IsEnabled = true, RootMonitorKey = PrimaryHardwareKey };
        ScreenProfileAccessor.EnsureScreenProfile(seed, SecondaryHardwareKey, @"\\.\DISPLAY1", "副屏 2");
        seed.FileBoxes.Add(tabA);
        seed.FileBoxes.Add(tabB);
        new LocalStateStore(temp).Save(seed);

        var coordinator = new WidgetCoordinator(interactiveQa: true, dataDirectory: temp);
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        var state = (AppState)typeof(WidgetCoordinator).GetField("_state", Private)!.GetValue(coordinator)!;
        try
        {
            coordinator.Start();
            pump();

            // 1. Secondary grouped FileBoxes render as ONE grouped FileBoxWindow with 2 tabs.
            var secBoxes = coordinator.GetWindowsForScreen(SecondaryHardwareKey).OfType<FileBoxWindow>().ToList();
            Check(secBoxes.Count == 1 && secBoxes[0].IsGrouped && secBoxes[0].Tabs.Count == 2,
                $"副屏同 GroupId 多页收纳盒只能渲染 1 个组合窗口（2 页），实际窗口数={secBoxes.Count}, Tabs={secBoxes.FirstOrDefault()?.Tabs.Count}");

            // 2. Detaching a tab on secondary keeps the detached window on SecondaryHardwareKey.
            var hostWin = secBoxes[0];
            var liveTabB = hostWin.Tabs.Single(t => t.Id == tabB.Id);
            var detachedWin = coordinator.DetachFileBoxTab(hostWin, liveTabB)
                ?? throw new InvalidOperationException("副屏组合收纳盒移出标签失败");
            pump();
            Check(liveTabB.GroupId is null
                && liveTabB.ScreenKey == SecondaryHardwareKey
                && coordinator.ActiveMultiScreenHost!.TryGetScreenKey(detachedWin) == SecondaryHardwareKey,
                "副屏组合收纳盒移出标签页后，拆出的窗口必须归属并注册在原副屏上");

            // 3. Merging detached back into host and dragging the grouped window to primary moves BOTH tabs & clears EdgeDock.
            var mergedWin = coordinator.MergeFileBoxes(detachedWin, hostWin)
                ?? throw new InvalidOperationException("副屏重新合并收纳盒失败");
            pump();
            Check(mergedWin.IsGrouped && mergedWin.Tabs.Count == 2,
                $"重新合并后应还原为 2 页组合窗，实际 Tabs={mergedWin.Tabs.Count}");
            foreach (var tab in mergedWin.Tabs)
            {
                tab.EdgeDockSide = FileBoxEdgeDockSide.Right;
                tab.EdgeDockMonitorDeviceName = @"\\.\DISPLAY1";
            }

            var mergedHandle = new WindowInteropHelper(mergedWin).Handle;
            GetWindowRect(mergedHandle, out var startRect);
            mergedWin.BeginDrag(startRect.Left + 24, startRect.Top + 20);
            mergedWin.DragTo(220, 140);
            mergedWin.EndDrag();
            pump();
            Check(mergedWin.Tabs.All(t => t.ScreenKey is null && t.EdgeDockSide is null && t.EdgeDockMonitorDeviceName is null)
                && coordinator.ActiveMultiScreenHost!.TryGetScreenKey(mergedWin) is null,
                "跨屏拖拽组合收纳盒回主屏必须同步更新全部标签页 ScreenKey 并清除所有 EdgeDock 锚点");

            // 4. Move the group to the secondary monitor and reconnect it after hot-unplug.
            var movedBack = coordinator.ActiveMultiScreenHost!.MoveWindowToScreen(mergedWin, SecondaryHardwareKey, activeMonitors);
            pump();
            Check(movedBack.Succeeded
                && mergedWin.Tabs.All(t => t.ScreenKey == SecondaryHardwareKey && t.Placement.MonitorLocal is not null),
                "组合收纳盒移回副屏后全部标签页应同步更新 ScreenKey 与 MonitorLocal");

            activeMonitors = CreatePrimaryOnlyMonitors();
            coordinator.HandleMultiScreenDisplayLayoutChanged();
            pump();
            Check(coordinator.SecondaryWindowCount == 0
                && state.FileBoxes.Where(b => b.GroupId == mergedWin.Tabs[0].GroupId).All(b => b.ScreenKey == SecondaryHardwareKey),
                "热拔出副屏后组合收纳盒窗体应关闭销毁且全部标签页保留副屏 ScreenKey");

            activeMonitors = CreateDualMonitors(@"\\.\DISPLAY9");
            coordinator.HandleMultiScreenDisplayLayoutChanged();
            pump();
            var restoredSecBoxes = coordinator.GetWindowsForScreen(SecondaryHardwareKey).OfType<FileBoxWindow>().ToList();
            Check(restoredSecBoxes.Count == 1 && restoredSecBoxes[0].IsGrouped && restoredSecBoxes[0].Tabs.Count == 2,
                $"副屏重连后应恢复 1 个包含 2 页的组合文件收纳盒，实际窗口数={restoredSecBoxes.Count}");
        }
        finally
        {
            DisposeCoordinatorWindows(coordinator);
            coordinator.Dispose();
            pump();
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static void TestThreePhysicalScreensKeepOwnership(Action pump)
    {
        const string thirdKey = "DISPLAY#TESTMON3#INSTANCE-C";
        var temp = Path.Combine(Path.GetTempPath(), "binglan-ms-physical-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var dual = CreateDualMonitors(@"\\.\DISPLAY1");
        IReadOnlyList<ResolvedMonitor> activeMonitors =
        [
            dual[0], dual[1],
            new ResolvedMonitor(thirdKey, thirdKey, @"\\.\DISPLAY3", "第三屏", "副屏 3", 3, 303,
                new PixelRect(-1920, 0, 0, 1080), new PixelRect(-1920, 0, 0, 1040), false, 144)
            { IdentityKind = MonitorIdentityKind.DeviceInterfacePath }
        ];
        var original = activeMonitors;
        var overrides = new Dictionary<string, ScreenSurfacePreferences>(StringComparer.OrdinalIgnoreCase)
        {
            [PrimaryHardwareKey] = new() { DockEnabled = false },
            [SecondaryHardwareKey] = new() { TopBarEnabled = false },
            [thirdKey] = new() { DockEnabled = true, TopBarEnabled = true, TopBarHeight = 40 }
        };
        IReadOnlyList<SurfaceMonitorSelection> DockTargets(string? fixedKey = null) =>
            SurfaceMonitorTargetResolver.ResolveTargets(fixedKey, true, overrides, item => item.DockEnableMode, activeMonitors);
        IReadOnlyList<SurfaceMonitorSelection> BarTargets() =>
            SurfaceMonitorTargetResolver.ResolveTargets(null, true, overrides, item => item.TopBarEnableMode, activeMonitors);
        Check(DockTargets().Select(target => target.Key).SequenceEqual([thirdKey])
              && BarTargets().Select(target => target.Key).SequenceEqual([PrimaryHardwareKey, thirdKey])
              && BarTargets()[1].Monitor.Dpi == 144 && BarTargets()[1].Preferences?.TopBarHeight == 40,
            "三屏中的显式开关、默认目标及第三屏的 DPI 和高度应独立解析");
        Check(DockTargets(SecondaryHardwareKey).Select(target => target.Key).SequenceEqual([SecondaryHardwareKey, thirdKey]),
            "固定目标和额外开启的 Dock 应同时匹配两个独立物理屏");
        MonitorIdentityResolver.ResolveOverrideForTests = () => activeMonitors;
        var coordinator = new WidgetCoordinator(interactiveQa: true, dataDirectory: temp);
        SettingsWindow? settings = null;
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            coordinator.Start();
            coordinator.SetMultiScreenEnabled(true);
            pump();
            var state = (AppState)typeof(WidgetCoordinator).GetField("_state", Private)!.GetValue(coordinator)!;
            var bridge = coordinator.BuildMultiScreenSettingsBridge();
            var rootNote = bridge.AddWidgetOnScreen(null, NewWidgetKind.Note) as NoteWidgetWindow
                ?? throw new InvalidOperationException("根布局新建便签失败");
            var secondTodo = bridge.AddWidgetOnScreen(SecondaryHardwareKey, NewWidgetKind.Todo) as TodoWidgetWindow
                ?? throw new InvalidOperationException("第二物理屏新建待办失败");
            var thirdNote = bridge.AddWidgetOnScreen(thirdKey, NewWidgetKind.Note) as NoteWidgetWindow
                ?? throw new InvalidOperationException("第三物理屏新建便签失败");
            ((TextBox)rootNote.FindName("BodyEditor")!).Text = "root physical owner";
            ((TextBox)thirdNote.FindName("BodyEditor")!).Text = "third physical owner";
            pump();
            Check(state.MultiScreen?.RootMonitorKey == PrimaryHardwareKey, "启用时根布局应明确绑定原物理屏幕");

            var newOrigin = original.Single(m => m.MonitorKey == SecondaryHardwareKey).Bounds;
            PixelRect Translate(PixelRect rect) => new(rect.Left - newOrigin.Left, rect.Top - newOrigin.Top,
                rect.Right - newOrigin.Left, rect.Bottom - newOrigin.Top);
            activeMonitors = original.Select(m => m with
            {
                IsPrimary = m.MonitorKey == SecondaryHardwareKey,
                Bounds = Translate(m.Bounds),
                WorkingArea = Translate(m.WorkingArea)
            }).ToArray();
            Check(DockTargets().Select(target => target.Key).SequenceEqual([SecondaryHardwareKey, thirdKey])
                  && BarTargets().Select(target => target.Key).SequenceEqual([thirdKey]),
                "更换主屏只改变跟随全局的用途，显式关闭持续生效");
            coordinator.HandleMultiScreenDisplayLayoutChanged();
            pump();
            Check(bridge.GetScreens().Single(m => m.MonitorKey == SecondaryHardwareKey).ScreenKey == SecondaryHardwareKey
                && bridge.GetScreens().Single(m => m.MonitorKey == PrimaryHardwareKey).ScreenKey is null,
                "Windows 主副角色互换不得改变根布局别名或独立档案归属");
            Check(coordinator.GetWindowsForScreen(SecondaryHardwareKey).Contains(secondTodo)
                && coordinator.GetWindowsForScreen(thirdKey).Contains(thirdNote)
                && coordinator.GetWindowsForScreen(null).Contains(rootNote),
                "三屏角色互换应保留各自窗口与物理归属");
            var rootAfterSwap = activeMonitors.Single(m => m.MonitorKey == PrimaryHardwareKey);
            Check(GetWindowRect(rootNote.WindowHandle, out var rootRect)
                && rootRect.Left >= rootAfterSwap.Bounds.Left && rootRect.Left < rootAfterSwap.Bounds.Right,
                "Windows 桌面坐标原点变化后旧根档案窗口仍须落在原物理屏");

            settings = new SettingsWindow(new CityLookupService(), state.InformationWidgets[0], state.DesktopExperience,
                (_, _) => { }, _ => { }, _ => { }, widgetProvider: () =>
                    coordinator.GetWindowsForScreen(null)
                        .Concat(coordinator.GetWindowsForScreen(SecondaryHardwareKey))
                        .Concat(coordinator.GetWindowsForScreen(thirdKey)).ToArray(),
                maintenance: new SettingsMaintenance { MultiScreen = bridge });
            settings.Show();
            settings.ShowSettingsPage("Components");
            settings.SelectActiveScreen(SecondaryHardwareKey);
            pump();
            var list = (ListBox)settings.FindName("DesktopComponentList")!;
            list.SelectedItem = list.Items.OfType<SettingsWindow.DesktopComponentSettingsEntry>()
                .Single(entry => ReferenceEquals(entry.Window, secondTodo));
            pump();
            var rootScale = state.DesktopExperience.GetComponent(DesktopComponentKind.Todo).FontScale;
            ((Slider)settings.FindName("SelectedComponentFontScaleSlider")!).Value = 1.35;
            typeof(SettingsWindow).GetMethod("ApplySelectedComponent", Private)!.Invoke(settings, [false]);
            pump();
            Check(Math.Abs(secondTodo.State.FontScale - 1.35) < 0.01
                && state.DesktopExperience.GetComponent(DesktopComponentKind.Todo).FontScale == rootScale,
                "选中新 Windows 主屏的卡片缩放必须写入其独立档案，根布局不受影响");

            var marquee = coordinator.AddMarqueeFileBox(Translate(new PixelRect(2250, 100, 2650, 420)))
                ?? throw new InvalidOperationException("第二物理屏框选新建文件盒失败");
            pump();
            Check(marquee.State.ScreenKey == SecondaryHardwareKey
                && coordinator.ActiveMultiScreenHost!.TryGetScreenKey(marquee) == SecondaryHardwareKey
                && marquee.State.Placement.MonitorLocal is not null,
                "实际框选入口应按物理坐标选择显示器并记录独立归属与本地位置");

            state.MultiScreen!.SurfacePreferences[SecondaryHardwareKey] = new ScreenSurfacePreferences
            {
                DockEnableMode = ScreenSurfaceEnableMode.Disabled, DockEdge = DockEdge.Left,
                TopBarEnableMode = ScreenSurfaceEnableMode.Disabled, TopBarHeight = 40
            };
            var copyStatus = bridge.CopyPrimaryProfileToScreen(PrimaryHardwareKey);
            pump();
            var copiedPreferences = state.MultiScreen.SurfacePreferences[PrimaryHardwareKey];
            Check(copyStatus.Contains("已复制", StringComparison.Ordinal)
                && Math.Abs(state.DesktopExperience.GetComponent(DesktopComponentKind.Todo).FontScale - 1.35) < 0.01
                && copiedPreferences.DockEdge == DockEdge.Left && copiedPreferences.TopBarHeight == 40
                && copiedPreferences.DockEnableMode == ScreenSurfaceEnableMode.Disabled
                && coordinator.GetWindowsForScreen(null).Contains(rootNote),
                "复制应取当前 Windows 主屏的卡片和栏设置，目标是原根屏时也保留便签");

            settings.Close();
            settings = null;
            var rootNoteId = rootNote.State.Id;
            activeMonitors = activeMonitors.Where(m => m.MonitorKey != PrimaryHardwareKey).ToArray();
            coordinator.HandleMultiScreenDisplayLayoutChanged();
            pump();
            Check(coordinator.HasActiveMultiScreenHost && coordinator.GetWindowsForScreen(null).Count == 0
                && !rootNote.IsLoaded && !rootNote.IsVisible
                && coordinator.GetWindowsForScreen(SecondaryHardwareKey).Contains(secondTodo)
                && coordinator.GetWindowsForScreen(thirdKey).Contains(thirdNote),
                "拔出原根屏只关闭根窗口，两台仍在线物理屏的窗口必须保持存活");

            activeMonitors = activeMonitors.Where(m => m.MonitorKey == SecondaryHardwareKey).ToArray();
            coordinator.HandleMultiScreenDisplayLayoutChanged();
            pump();
            Check(coordinator.HasActiveMultiScreenHost
                && coordinator.GetWindowsForScreen(SecondaryHardwareKey).Contains(secondTodo)
                && coordinator.GetWindowsForScreen(SecondaryHardwareKey).Contains(marquee)
                && coordinator.GetWindowsForScreen(thirdKey).Count == 0,
                "只剩原独立档案屏时该屏卡片仍应存活，不得因屏幕数量改变而停用");

            activeMonitors = original;
            coordinator.HandleMultiScreenDisplayLayoutChanged();
            pump();
            var restoredRoot = coordinator.GetWindowsForScreen(null).OfType<NoteWidgetWindow>()
                .Single(w => w.State.Id == rootNoteId);
            Check(restoredRoot.State.Content == "root physical owner"
                && restoredRoot.State.ScreenKey is null
                && coordinator.GetWindowsForScreen(thirdKey).OfType<NoteWidgetWindow>()
                    .Single(w => w.State.Id == thirdNote.State.Id).State.Content == "third physical owner"
                && state.MultiScreen!.RootMonitorKey == PrimaryHardwareKey,
                "三屏恢复后应分别重建离线档案且不合并卡片内容或物理归属");
        }
        finally
        {
            settings?.Close();
            DisposeCoordinatorWindows(coordinator);
            coordinator.Dispose();
            pump();
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static void TestLegacyRootRequiresExplicitBinding(Action pump)
    {
        var temp = Path.Combine(Path.GetTempPath(), "binglan-ms-unbound-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        MonitorIdentityResolver.ResolveOverrideForTests = () => CreateDualMonitors(@"\\.\DISPLAY1");
        var seed = LocalStateStore.CreateDefault();
        seed.SchemaVersion = 65;
        seed.MultiScreen = new MultiScreenState { IsEnabled = true };
        ScreenProfileAccessor.EnsureScreenProfile(seed, SecondaryHardwareKey);
        var savedNote = NoteService.CreateDefaultWidget();
        savedNote.ScreenKey = SecondaryHardwareKey;
        seed.NoteWidgets.Add(savedNote);
        new LocalStateStore(temp).Save(seed);
        var coordinator = new WidgetCoordinator(interactiveQa: true, dataDirectory: temp);
        try
        {
            coordinator.Start();
            pump();
            Check(coordinator.GetWindowsForScreen(null).Count == 0
                && coordinator.GetWindowsForScreen(SecondaryHardwareKey).OfType<NoteWidgetWindow>().Any(w => w.State.Id == savedNote.Id),
                "旧版已启用但根屏未知时仅停放根布局，已确认独立屏档案照常恢复");
            Check(!coordinator.BindLegacyRootToScreen(SecondaryHardwareKey), "有独立档案的物理屏不得接管旧根布局");
            Check(coordinator.BindLegacyRootToScreen(PrimaryHardwareKey), "用户明确选择空白物理屏后应绑定根布局");
            pump();
            Check(coordinator.GetWindowsForScreen(null).Count > 0
                && new LocalStateStore(temp).Load().MultiScreen?.RootMonitorKey == PrimaryHardwareKey,
                "显式选择后应恢复根窗口并立即持久保存根屏归属");
        }
        finally
        {
            DisposeCoordinatorWindows(coordinator);
            coordinator.Dispose();
            pump();
            try { Directory.Delete(temp, recursive: true); } catch { }
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
