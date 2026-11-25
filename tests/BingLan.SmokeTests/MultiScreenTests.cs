using System.Net;
using System.Text.Json;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.Themes;

internal static class MultiScreenTests
{
    private const string SecondaryKey = "DISPLAY#DELA07A#5&123456&0&UID4352";

    // Real dual-monitor topology: Primary DISPLAY2 (0,0 2048x1152 @ 125%),
    // Secondary DISPLAY1 (2048,-50 1707x1067 @ 100%, negative Y + mixed DPI).
    private static readonly PixelRect PrimaryWorkPx = new(0, 0, 2048, 1104);
    private static readonly PixelRect SecondaryWorkPx = new(2048, -50, 3755, 977);
    private const uint PrimaryDpi = 120;
    private const uint SecondaryDpi = 96;

    internal static void RunAll()
    {
        TestSchemaV64ToV65MigrationAndNullZeroOverhead();
        TestMonitorKeyNormalizationAndBlankProfile();
        TestLocalDipToPhysicalBoundsNegativeYAndMixedDpi();
        TestCopyPrimaryProfileCrossScreenMoveAndOfflineDiscovery();
        TestThemeExportImportDoesNotTouchMultiScreen();
        TestWeatherServiceConcurrentDeduplication();
        TestMonitorLocalPlacementAndPortMigrationRules();
        TestQuickPlacesMergeTodoOwnershipAndLayoutOrder();
        TestGroupedFileBoxMultiScreenSync();
    }

    private static void TestSchemaV64ToV65MigrationAndNullZeroOverhead()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "binglan-ms-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalStateStore(tempDir);
            Directory.CreateDirectory(tempDir);

            foreach (var legacyVersion in new[] { 62, 63, 64 })
            {
                var legacyState = LocalStateStore.CreateDefault();
                legacyState.SchemaVersion = legacyVersion;
                legacyState.MultiScreen = null;
                legacyState.InformationWidgets.Add(new InformationWidgetState
                {
                    ClockAlignment = ClockHorizontalAlignment.Center
                });
                var groupId = Guid.NewGuid();
                var tabA = new FileBoxState
                {
                    Title = "组合A",
                    GroupId = groupId,
                    TabOrder = 0,
                    IsActiveTab = true,
                    HoverSwitchTabs = true,
                    HideWhenIdle = true,
                    IdleHideDelaySeconds = 1.5
                };
                var tabB = new FileBoxState
                {
                    Title = "组合B",
                    GroupId = groupId,
                    TabOrder = 1,
                    IsActiveTab = false
                };
                legacyState.FileBoxes.Add(tabA);
                legacyState.FileBoxes.Add(tabB);
                legacyState.TodoWidgets[0].Items.Add(new TodoItemState
                {
                    Text = "跨天固定待办",
                    Kind = TodoItemKind.Todo,
                    IsPersistent = true
                });
                var legacyJson = JsonSerializer.Serialize(legacyState)
                    .Replace($"\"SchemaVersion\":{AppState.CurrentSchemaVersion}", $"\"SchemaVersion\":{legacyVersion}", StringComparison.Ordinal);
                File.WriteAllText(store.StatePath, legacyJson);

                var loaded = store.Load();
                Check(loaded.SchemaVersion == AppState.CurrentSchemaVersion && AppState.CurrentSchemaVersion == 65,
                    $"v{legacyVersion} 状态应平滑迁移到 v65，实际 {loaded.SchemaVersion}");
                Check(loaded.MultiScreen is null, "单屏旧状态迁移后 MultiScreen 应保持 null 零分配");
                Check(loaded.InformationWidgets.Any(i => i.ClockAlignment == ClockHorizontalAlignment.Center),
                    "迁移到 v65 后应保留 v0.4.9 的 ClockAlignment 设置");
                Check(loaded.FileBoxes.Count(b => b.GroupId == groupId) == 2
                    && loaded.FileBoxes.Any(b => b.Id == tabA.Id && b.HoverSwitchTabs && b.HideWhenIdle && Math.Abs((b.IdleHideDelaySeconds ?? 0d) - 1.5d) < 0.01d)
                    && loaded.FileBoxes.Any(b => b.Id == tabB.Id && b.HoverSwitchTabs && b.HideWhenIdle),
                    "v64->v65 迁移应无损保留 v0.5.0 组合收纳盒 GroupId/HoverSwitchTabs/HideWhenIdle 设置");
                Check(loaded.TodoWidgets[0].Items.Any(i => i.Text == "跨天固定待办" && i.IsPersistent),
                    "v64->v65 迁移应无损保留 v0.5.0 跨天固定待办项");
                Check(loaded.TodoWidgets.All(t => t.ScreenKey is null)
                    && loaded.FileBoxes.All(b => b.ScreenKey is null)
                    && loaded.NoteWidgets.All(n => n.ScreenKey is null),
                    "旧卡片迁移后 ScreenKey 均应保持 null（归属主屏）");

                store.Save(loaded);
                var savedText = File.ReadAllText(store.StatePath);
                Check(!savedText.Contains("\"MultiScreen\"", StringComparison.Ordinal),
                    "未启用多屏时保存的 JSON 不应写入 MultiScreen 字段");
                Check(!savedText.Contains("\"ScreenKey\"", StringComparison.Ordinal),
                    "主屏卡片保存的 JSON 不应冗余写入 ScreenKey 字段");
            }

            // Forward-compatibility: ExtensionData on MultiScreenState & ScreenProfileState survives round-trip.
            var multiState = LocalStateStore.CreateDefault();
            var profile = ScreenProfileAccessor.EnsureScreenProfile(multiState, SecondaryKey, @"\\.\DISPLAY1", "副屏 2");
            multiState.MultiScreen!.IsEnabled = true;
            multiState.MultiScreen.ExtensionData = new Dictionary<string, JsonElement>
            {
                ["FutureFlag"] = JsonDocument.Parse("true").RootElement.Clone()
            };
            profile.ExtensionData = new Dictionary<string, JsonElement>
            {
                ["FutureDockMode"] = JsonDocument.Parse("\"mirror\"").RootElement.Clone()
            };

            store.Save(multiState);
            var reloadedMulti = store.Load();
            Check(reloadedMulti.MultiScreen is { IsEnabled: true }, "多屏配置应正确持久化并加载");
            Check(reloadedMulti.MultiScreen!.ExtensionData?.ContainsKey("FutureFlag") == true,
                "MultiScreenState.ExtensionData 应向前兼容保留");
            Check(reloadedMulti.MultiScreen.Screens.TryGetValue(SecondaryKey, out var reloadedProfile)
                && reloadedProfile.ExtensionData?.ContainsKey("FutureDockMode") == true,
                "ScreenProfileState.ExtensionData 应向前兼容保留");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static void TestMonitorKeyNormalizationAndBlankProfile()
    {
        Check(MultiScreenRules.NormalizeScreenKey(null) is null, "null 键应归一为 null");
        Check(MultiScreenRules.NormalizeScreenKey("   ") is null, "空白键应归一为 null");
        Check(MultiScreenRules.NormalizeScreenKey("primary") is null, "primary 哨兵应归一为 null");
        Check(MultiScreenRules.NormalizeScreenKey("PRIMARY") is null, "大小写 PRIMARY 哨兵应归一为 null");
        Check(MultiScreenRules.NormalizeScreenKey($"  {SecondaryKey}  ") == SecondaryKey, "合法硬件键应裁剪空格保留");

        var blank = MultiScreenRules.CreateBlankExperience();
        Check(blank.Components.All(c => !c.IsVisible), "新副屏默认方案应全组件关闭（空白）");

        var state = new MultiScreenState
        {
            IsEnabled = true,
            Screens = new Dictionary<string, ScreenProfileState>(StringComparer.Ordinal)
            {
                ["primary"] = new ScreenProfileState { MonitorKey = "primary" },
                [SecondaryKey] = new ScreenProfileState
                {
                    MonitorKey = SecondaryKey,
                    PrimaryTodoId = Guid.NewGuid().ToString("D")
                }
            }
        };
        var liveSecondaryTodo = TodoService.CreateDefaultWidget();
        liveSecondaryTodo.ScreenKey = SecondaryKey;
        MultiScreenRules.Normalize(state, [liveSecondaryTodo]);

        Check(!state.Screens.ContainsKey("primary"), "Normalize 应剔除误写入的 primary 键");
        Check(state.Screens[SecondaryKey].ParsedPrimaryTodoId == liveSecondaryTodo.Id,
            "失效的 PrimaryTodoId 应自动纠偏到该副屏现有待办卡片");
    }

    private static void TestLocalDipToPhysicalBoundsNegativeYAndMixedDpi()
    {
        // Local (48, 24) DIP on SecondaryWorkPx (2048, -50, 3755, 977) @ 96 DPI -> exact physical (2096, -26)!
        var placed = ScreenProfileAccessor.LocalDipToPhysicalBounds(
            48d, 24d, 300d, 200d, SecondaryWorkPx, SecondaryDpi);
        Check(placed.Left == 2096 && placed.Top == -26 && placed.Width == 300 && placed.Height == 200,
            $"负 Y 副屏局部 DIP 坐标应精确映射到物理像素 (2096,-26,300x200)，实际 ({placed.Left},{placed.Top},{placed.Width}x{placed.Height})");

        // Clamping inside negative-Y secondary work area.
        var clamped = ScreenProfileAccessor.LocalDipToPhysicalBounds(
            9999d, 9999d, 300d, 200d, SecondaryWorkPx, SecondaryDpi);
        Check(clamped.Right == SecondaryWorkPx.Right && clamped.Bottom == SecondaryWorkPx.Bottom,
            $"越界坐标应贴边夹紧在副屏工作区内，实际 Right={clamped.Right} Bottom={clamped.Bottom}");

        // Preset placement for all 4 presets on negative-Y secondary monitor.
        var layoutKey = DisplayLayoutRules.Key([
            (new PixelRect(0, 0, 2048, 1152), PrimaryDpi),
            (new PixelRect(2048, -50, 3755, 1017), SecondaryDpi)
        ]);
        foreach (var preset in Enum.GetValues<DesktopLayoutPreset>())
        {
            var map = ScreenProfileAccessor.ComputePresetPlacementsForMonitor(
                preset, SecondaryWorkPx, SecondaryDpi, layoutKey);
            Check(map.Count >= 4, $"预设 {preset} 应生成全部内置卡片落点");
            foreach (var (kind, entry) in map)
            {
                var px = entry.PixelBounds;
                Check(px.Left >= SecondaryWorkPx.Left && px.Top >= SecondaryWorkPx.Top
                    && px.Right <= SecondaryWorkPx.Right && px.Bottom <= SecondaryWorkPx.Bottom,
                    $"预设 {preset} 的 {kind} 必须完全落在副屏工作区内，实际 ({px.Left},{px.Top})-({px.Right},{px.Bottom})");
                Check(DisplayLayoutRules.TryRecall(entry.Placement, layoutKey, out var recalled) && recalled == px,
                    $"预设 {preset} 的 {kind} 应记住当前多屏排列的物理坐标");
            }
        }
    }

    private static void TestCopyPrimaryProfileCrossScreenMoveAndOfflineDiscovery()
    {
        var state = LocalStateStore.CreateDefault();
        state.DesktopExperience.ActivePreset = DesktopLayoutPreset.GlacierWorkbench;
        state.DesktopExperience.GetComponent(DesktopComponentKind.Weather).IsVisible = true;
        state.DesktopExperience.GetComponent(DesktopComponentKind.Todo).IsVisible = true;
        state.QuickPlaces =
        [
            new QuickPlaceState { Icon = QuickPlaceRules.FolderIcon, Name = "工作目录", Target = @"C:\Work" }
        ];

        var copyResult = ScreenProfileAccessor.CopyPrimaryProfileToScreen(
            state, SecondaryKey, SecondaryWorkPx, SecondaryDpi, @"\\.\DISPLAY1", "副屏 2");
        Check(copyResult.Profile.Experience.ActivePreset == DesktopLayoutPreset.GlacierWorkbench,
            "复制主屏方案后副屏应继承当前布局预设");
        Check(copyResult.Profile.Experience.GetComponent(DesktopComponentKind.Weather).IsVisible,
            "复制主屏方案后副屏应开启主屏可见的天气卡片");
        Check(copyResult.CreatedTodo is not null && copyResult.CreatedTodo.ScreenKey == SecondaryKey,
            "主屏开启待办且副屏无待办时，复制方案应自动创建副屏独立待办卡片");
        Check(copyResult.Profile.ParsedPrimaryTodoId == copyResult.CreatedTodo!.Id,
            "副屏 PrimaryTodoId 应指向新建的副屏待办卡片");

        // Deep copy isolation: mutating secondary quick places must NOT mutate primary.
        copyResult.Profile.QuickPlaces[0].Name = "副屏专属入口";
        Check(state.QuickPlaces[0].Name == "工作目录", "副屏快捷入口修改不应影响主屏");

        // Move Note & FileBox from primary to secondary keeping data intact & clearing edge dock.
        var note = NoteService.CreateDefaultWidget();
        note.Title = "跨屏便签";
        note.Content = "重要会议纪要";
        state.NoteWidgets.Add(note);

        var box = new FileBoxState
        {
            Title = "跨屏收纳盒",
            EdgeDockSide = FileBoxEdgeDockSide.Right,
            EdgeDockOffsetDip = 120d,
            EdgeDockMonitorDeviceName = @"\\.\DISPLAY2"
        };
        box.Items.Add(new FileMappingState { Path = @"C:\Demo\report.pdf", Title = "report" });
        state.FileBoxes.Add(box);

        var movedNotePx = ScreenProfileAccessor.MoveNoteToScreen(
            state, note.Id, SecondaryKey, SecondaryWorkPx, SecondaryDpi,
            sourceBoundsPx: new PixelRect(100, 100, 400, 360), sourceWorkAreaPx: PrimaryWorkPx, sourceDpi: PrimaryDpi);
        var movedBoxPx = ScreenProfileAccessor.MoveFileBoxToScreen(
            state, box.Id, SecondaryKey, SecondaryWorkPx, SecondaryDpi,
            sourceBoundsPx: new PixelRect(400, 100, 760, 400), sourceWorkAreaPx: PrimaryWorkPx, sourceDpi: PrimaryDpi);

        Check(movedNotePx is { } nPx && nPx.Left >= SecondaryWorkPx.Left && nPx.Top >= SecondaryWorkPx.Top,
            "便签移至副屏后物理像素应落在副屏工作区内");
        Check(note.ScreenKey == SecondaryKey && note.Content == "重要会议纪要",
            "便签移至副屏后 ScreenKey 更新且正文完整保留");
        Check(movedBoxPx is { } bPx && bPx.Left >= SecondaryWorkPx.Left && box.ScreenKey == SecondaryKey
            && box.Items.Count == 1 && box.EdgeDockSide is null && box.EdgeDockMonitorDeviceName is null,
            "文件收纳盒移至副屏后映射完整保留，且清空原屏贴边停靠锚点");

        // Primary-only file box filtering excludes secondary boxes (OrganizeDesktop / ImportNewDesktopEntries isolation).
        var primaryOnlyBoxes = ScreenProfileAccessor.FilterPrimaryFileBoxes(state.FileBoxes);
        Check(!primaryOnlyBoxes.Any(b => b.Id == box.Id),
            "主屏整理/自动导入过滤必须排除副屏文件收纳盒");

        // Move QuickPlaces across screens merges with destination custom items and resets source screen to defaults.
        state.QuickPlaces =
        [
            new QuickPlaceState { Icon = QuickPlaceRules.FolderIcon, Name = "主屏定制入口", Target = @"C:\CustomA" }
        ];
        var movedQuick = ScreenProfileAccessor.MoveQuickPlacesToScreen(state, sourceScreenKey: null, SecondaryKey);
        Check(movedQuick.Count == 2
            && movedQuick.Any(p => p.Target == @"C:\Work")
            && movedQuick.Any(p => p.Name == "主屏定制入口" && p.Target == @"C:\CustomA")
            && ScreenProfileAccessor.GetQuickPlaces(state, SecondaryKey).Any(p => p.Target == @"C:\CustomA")
            && !state.QuickPlaces.Any(p => p.Target == @"C:\CustomA"),
            "跨屏迁移快捷入口应合并保留目标自定义项、完整搬迁源定制项并重置源屏列表");
        copyResult.Profile.Experience.GetComponent(DesktopComponentKind.QuickLaunch).IsVisible = true;

        // Disconnected discovery when SecondaryKey is unplugged covers Todo, Note, FileBox, QuickLaunch, and visible built-ins.
        var offline = ScreenProfileAccessor.FindDisconnectedScreenWidgets(
            state, connectedSecondaryKeys: new HashSet<string>(StringComparer.Ordinal));
        Check(offline.Any(item => item.Id == copyResult.CreatedTodo!.Id && item.KindName == ScreenProfileAccessor.DisconnectedTodoKind)
            && offline.Any(item => item.Id == note.Id && item.KindName == ScreenProfileAccessor.DisconnectedNoteKind)
            && offline.Any(item => item.Id == box.Id && item.KindName == ScreenProfileAccessor.DisconnectedFileBoxKind)
            && offline.Any(item => item.KindName == ScreenProfileAccessor.DisconnectedQuickLaunchKind && item.ScreenKey == SecondaryKey)
            && offline.Any(item => item.KindName == ScreenProfileAccessor.DisconnectedBuiltInInfoKind && item.Title == DesktopExperienceRules.GetComponentName(DesktopComponentKind.Weather)),
            $"副屏拔出后应发现待办/便签/收纳盒/快捷入口及可见内置信息卡，实际 {offline.Count} 项");

        // Recover disconnected QuickPlaces & built-in Weather back to primary.
        state.DesktopExperience.GetComponent(DesktopComponentKind.Weather).IsVisible = false;
        Check(ScreenProfileAccessor.MoveDisconnectedQuickPlacesToPrimary(state, SecondaryKey),
            "离线快捷入口应支持一键移回主屏");
        Check(state.QuickPlaces.Any(p => p.Target == @"C:\CustomA")
            && !copyResult.Profile.Experience.GetComponent(DesktopComponentKind.QuickLaunch).IsVisible,
            "离线快捷入口移回主屏后主屏应获得自定义项且离线屏槽位关闭");
        Check(ScreenProfileAccessor.MoveDisconnectedBuiltInToPrimary(state, SecondaryKey, DesktopComponentKind.Weather),
            "离线可见内置天气卡应支持移回主屏");
        Check(state.DesktopExperience.GetComponent(DesktopComponentKind.Weather).IsVisible
            && !copyResult.Profile.Experience.GetComponent(DesktopComponentKind.Weather).IsVisible,
            "离线内置天气卡移回主屏后主屏应开启且离线屏槽位关闭");

        // Move Todo back to primary and verify ownership reconciliation.
        ScreenProfileAccessor.MoveTodoToScreen(
            state, copyResult.CreatedTodo!.Id, targetScreenKey: null, PrimaryWorkPx, PrimaryDpi);
        Check(copyResult.CreatedTodo.ScreenKey is null, "待办移回主屏后 ScreenKey 应归 null");
        Check(copyResult.Profile.ParsedPrimaryTodoId is null
            && !copyResult.Profile.Experience.GetComponent(DesktopComponentKind.Todo).IsVisible,
            "副屏最后一张待办移走后应清空其 PrimaryTodoId 并关闭待办槽位");
    }

    private static void TestWeatherServiceConcurrentDeduplication()
    {
        const string forecastJson = """
            {
              "current": { "temperature_2m": 31.5, "weather_code": 2 },
              "daily": { "temperature_2m_max": [34.0], "temperature_2m_min": [22.0] }
            }
            """;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var handler = new DelegatingStubHandler(async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            await gate.Task.ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(forecastJson)
            };
        });
        var service = new WeatherService(handler);

        var t1 = service.RefreshAsync("北京", 39.9, 116.4, forceRefresh: false);
        var t2 = service.RefreshAsync("北京", 39.9, 116.4, forceRefresh: false);
        gate.SetResult();
        Task.WaitAll(t1, t2);

        Check(t1.Result.Status == WeatherStatus.Fresh && t2.Result.Status == WeatherStatus.Fresh
            && t2.Result.TemperatureCelsius == 31.5,
            "并发多屏天气卡自动刷新均应获得有效温度快照");
        Check(calls == 1, $"多屏共享 WeatherService 并发自动刷新应只发 1 次 HTTP 请求，实际 {calls} 次");

        var third = service.RefreshAsync("北京", 39.9, 116.4, forceRefresh: false).GetAwaiter().GetResult();
        Check(third.Status == WeatherStatus.Fresh && calls == 1,
            $"SuccessRefreshInterval 内再次自动刷新应复用缓存不发请求，实际 {calls} 次");

        var manual = service.RefreshAsync("北京", 39.9, 116.4, forceRefresh: true).GetAwaiter().GetResult();
        Check(manual.Status == WeatherStatus.Fresh && calls == 2,
            $"手动强制刷新仍应发出网络请求，实际 {calls} 次");
    }

    private sealed class DelegatingStubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private static void TestThemeExportImportDoesNotTouchMultiScreen()
    {
        var state = LocalStateStore.CreateDefault();
        state.MultiScreen = new MultiScreenState { IsEnabled = true };
        var secondary = ScreenProfileAccessor.EnsureScreenProfile(state, SecondaryKey, @"\\.\DISPLAY1", "副屏 2");
        secondary.Experience.ActivePreset = DesktopLayoutPreset.CenterClock;
        secondary.Experience.GetComponent(DesktopComponentKind.Performance).IsVisible = true;

        var area = new ThemeArea(0, 0, 1920, 1040);
        var package = ThemeRules.Export(state, "测试主题", area, new Version(0, 4, 8));
        var exportedJson = JsonSerializer.Serialize(package);
        Check(!exportedJson.Contains("MultiScreen", StringComparison.OrdinalIgnoreCase)
            && !exportedJson.Contains(SecondaryKey, StringComparison.OrdinalIgnoreCase),
            ".binglan 主题导出不得包含本机 MultiScreen 配置");

        // Importing a theme only mutates root DesktopExperience/Style, never touching MultiScreen.
        ThemeRules.ApplyVisuals(package, state.DesktopExperience, area);
        ThemeRules.ApplyStyle(package, state.Style, state.Taskbar);
        Check(state.MultiScreen is { IsEnabled: true }
            && state.MultiScreen.Screens.TryGetValue(SecondaryKey, out var kept)
            && kept.Experience.ActivePreset == DesktopLayoutPreset.CenterClock
            && kept.Experience.GetComponent(DesktopComponentKind.Performance).IsVisible,
            "导入主题只能影响主屏，不得清空或覆盖副屏独立方案");
    }

    private static void TestMonitorLocalPlacementAndPortMigrationRules()
    {
        // 1. Primary-screen cards must NEVER persist MonitorLocal metadata.
        var tempDir = Path.Combine(Path.GetTempPath(), "binglan-ms-local-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalStateStore(tempDir);
            Directory.CreateDirectory(tempDir);
            var singleState = LocalStateStore.CreateDefault();
            var primaryTodo = TodoService.CreateDefaultWidget();
            singleState.TodoWidgets.Add(primaryTodo);
            store.Save(singleState);
            var json = File.ReadAllText(store.StatePath);
            Check(!json.Contains("\"MonitorLocal\"", StringComparison.Ordinal),
                "主屏卡片不得写入 MonitorLocal 字段以保证单屏 JSON 零冗余");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }

        // 2. MonitorLocalPlacement restores exact relative DIP & size across rearranged LEFT monitor & 125% DPI.
        var placement = new WindowPlacement();
        var originalSecBounds = new PixelRect(2048 + 240, -50 + 180, 2048 + 240 + 420, -50 + 180 + 480);
        ScreenProfileAccessor.RecordMonitorLocalPlacement(placement, originalSecBounds, SecondaryWorkPx, SecondaryDpi);
        Check(placement.MonitorLocal is { } local
            && Math.Abs(local.LocalLeftDip - 240d) < 0.01d
            && Math.Abs(local.LocalTopDip - 180d) < 0.01d
            && Math.Abs(local.WidthDip - 420d) < 0.01d
            && Math.Abs(local.HeightDip - 480d) < 0.01d,
            "RecordMonitorLocalPlacement 应记录精确屏内局部 DIP 位置与尺寸");

        // 2a. MonitorLocalPlacement restores exact local DIP & size across rearranged LEFT monitor & 125% DPI (same logical work area).
        var sameLogicalLeft125WorkPx = new PixelRect(-2134, 0, 0, 1284); // 1707x1027 DIP at 120 DPI
        var restoredExact125 = ScreenProfileAccessor.ResolveMonitorLocalBounds(placement, sameLogicalLeft125WorkPx, 120);
        Check(restoredExact125 is { } pxExact125
            && pxExact125.Left == -2134 + 300 && pxExact125.Top == 225
            && pxExact125.Width == 525 && pxExact125.Height == 600,
            $"重排至左侧 + 125% DPI（相同逻辑工作区）应精确按局部 DIP 与尺寸还原，实际 ({restoredExact125?.Left},{restoredExact125?.Top},{restoredExact125?.Width}x{restoredExact125?.Height})");

        // 2b. When logical width changes (1707 -> 1920 DIP) at 125% DPI, X scales proportionally via AnchorX while DIP size stays exact.
        var left125WorkPx = new PixelRect(-2400, 0, 0, 1300); // 1920x1040 DIP at 120 DPI
        var restored125 = ScreenProfileAccessor.ResolveMonitorLocalBounds(placement, left125WorkPx, 120);
        Check(restored125 is { } px125
            && px125.Left == -2050 && px125.Top == 225
            && px125.Width == 525 && px125.Height == 600,
            $"分辨率变化（1707->1920 DIP）+ 125% DPI 后应按比例锚点与原 DIP 尺寸还原，实际 ({restored125?.Left},{restored125?.Top},{restored125?.Width}x{restored125?.Height})");

        // Shrunk work area clamps/scales safely inside bounds without losing DIP size when possible.
        var smallWorkPx = new PixelRect(0, 0, 1024, 720);
        var restoredSmall = ScreenProfileAccessor.ResolveMonitorLocalBounds(placement, smallWorkPx, 96);
        Check(restoredSmall is { } pxSmall
            && pxSmall.Left >= smallWorkPx.Left && pxSmall.Top >= smallWorkPx.Top
            && pxSmall.Right <= smallWorkPx.Right && pxSmall.Bottom <= smallWorkPx.Bottom
            && pxSmall.Width == 420 && pxSmall.Height == 480,
            $"较小工作区下应按锚点还原并完整留在工作区内且保持原尺寸，实际 {restoredSmall}");

        // 3. ReconcileConnectedProfileKeys handles port change (EDID model) & dock FriendlyName unambiguously.
        var state = LocalStateStore.CreateDefault();
        state.MultiScreen = new MultiScreenState { IsEnabled = true };
        var oldBoeKey = "DISPLAY#BOE0C8B#5&2C737580&0&UID4357";
        var profile = ScreenProfileAccessor.EnsureScreenProfile(state, oldBoeKey, @"\\.\DISPLAY1", "副屏 2 · BOE");
        profile.HardwareModelId = "DISPLAY#BOE0C8B";
        profile.FriendlyName = "Laptop Built-in";
        var secTodo = TodoService.CreateDefaultWidget();
        secTodo.ScreenKey = oldBoeKey;
        var secNote = NoteService.CreateDefaultWidget();
        secNote.ScreenKey = oldBoeKey;
        var secBox = new FileBoxState { ScreenKey = oldBoeKey };
        state.TodoWidgets.Add(secTodo);
        state.NoteWidgets.Add(secNote);
        state.FileBoxes.Add(secBox);

        // Connected on a different port (new UID) while Primary is SAM7180.
        var newPortBoeKey = "DISPLAY#BOE0C8B#5&99AABBCC&0&UID8888";
        var migratedCount = ScreenProfileAccessor.ReconcileConnectedProfileKeys(
            state,
            [new MonitorIdentityCandidate(newPortBoeKey, newPortBoeKey, @"\\.\DISPLAY3", "Laptop Built-in", "副屏 2 · Laptop Built-in")],
            primaryHardwareId: "DISPLAY#SAM7180#5&2C737580&0&UID4356",
            primaryModelId: "DISPLAY#SAM7180",
            primaryFriendlyName: "Odyssey G52A");
        Check(migratedCount == 1
            && state.MultiScreen.Screens.Count == 1
            && !state.MultiScreen.Screens.ContainsKey(oldBoeKey)
            && state.MultiScreen.Screens.ContainsKey(newPortBoeKey)
            && secTodo.ScreenKey == newPortBoeKey
            && secNote.ScreenKey == newPortBoeKey
            && secBox.ScreenKey == newPortBoeKey,
            "换接口重连同 EDID 型号副屏时应 1 对 1 自动迁移档案及所有所属卡片 ScreenKey，且不得产生重复档案");

        // Ambiguous case: two identical offline/online monitors OR primary monitor's own model must NEVER guess.
        var ambState = LocalStateStore.CreateDefault();
        ambState.MultiScreen = new MultiScreenState { IsEnabled = true };
        var p1 = ScreenProfileAccessor.EnsureScreenProfile(ambState, "DISPLAY#DELL123#1&A&0&UID1", @"\\.\DISPLAY2", "Dell U2723QE", "DISPLAY#DELL123", "DELL U2723QE");
        var p2 = ScreenProfileAccessor.EnsureScreenProfile(ambState, "DISPLAY#DELL123#2&B&0&UID2", @"\\.\DISPLAY3", "Dell U2723QE", "DISPLAY#DELL123", "DELL U2723QE");
        _ = (p1, p2);
        var ambMigrated = ScreenProfileAccessor.ReconcileConnectedProfileKeys(
            ambState,
            [new MonitorIdentityCandidate("DISPLAY#DELL123#3&C&0&UID9", "DISPLAY#DELL123#3&C&0&UID9", @"\\.\DISPLAY4", "DELL U2723QE", "副屏 4")],
            primaryHardwareId: "DISPLAY#SAM7180#0",
            primaryModelId: "DISPLAY#SAM7180");
        Check(ambMigrated == 0 && ambState.MultiScreen.Screens.Count == 2,
            "存在多份同型号离线档案时严禁盲猜迁移，必须保持零误迁");

        // Unfamiliar FriendlyName-only dock migration works cleanly when non-generic and unique.
        var dockState = LocalStateStore.CreateDefault();
        dockState.MultiScreen = new MultiScreenState { IsEnabled = true };
        var dockOldKey = "slot:DISPLAY4";
        ScreenProfileAccessor.EnsureScreenProfile(dockState, dockOldKey, @"\\.\DISPLAY4", "副屏 2 · ThinkVision P27", null, "ThinkVision P27");
        var dockMigrated = ScreenProfileAccessor.ReconcileConnectedProfileKeys(
            dockState,
            [new MonitorIdentityCandidate("slot:DISPLAY7", null, @"\\.\DISPLAY7", "ThinkVision P27", "副屏 2 · ThinkVision P27")],
            primaryHardwareId: "DISPLAY#BOE0C8B#0",
            primaryModelId: "DISPLAY#BOE0C8B",
            primaryFriendlyName: "BOE");
        Check(dockMigrated == 1 && dockState.MultiScreen.Screens.ContainsKey("slot:DISPLAY7"),
            "非泛化友好名称 1 对 1 唯一匹配时应支持虚拟/扩展坞副屏迁移");
        Check(MultiScreenRules.IsGenericMonitorName("Generic PnP Monitor")
            && MultiScreenRules.IsGenericMonitorName("通用即插即用监视器")
            && MultiScreenRules.IsGenericMonitorName(@"\\.\DISPLAY2")
            && !MultiScreenRules.IsGenericMonitorName("Odyssey G52A"),
            "泛化显示器名称过滤应准确排除 Generic PnP Monitor / DISPLAYn 并保留真实型号");
    }

    private static void TestQuickPlacesMergeTodoOwnershipAndLayoutOrder()
    {
        // P1-2 & P2-3: Moving QuickPlaces onto a visible/customized screen merges (dedup by Target, keeps order & <= 12).
        var qState = LocalStateStore.CreateDefault();
        qState.MultiScreen = new MultiScreenState { IsEnabled = true };
        var secProfile = ScreenProfileAccessor.EnsureScreenProfile(qState, SecondaryKey);
        qState.DesktopExperience.GetComponent(DesktopComponentKind.QuickLaunch).IsVisible = true;
        qState.QuickPlaces =
        [
            new QuickPlaceState { Icon = QuickPlaceRules.FolderIcon, Name = "主屏A", Target = @"C:\PrimaryA" },
            new QuickPlaceState { Icon = QuickPlaceRules.FolderIcon, Name = "共有项", Target = @"C:\Shared" }
        ];
        secProfile.Experience.GetComponent(DesktopComponentKind.QuickLaunch).IsVisible = true;
        secProfile.QuickPlaces =
        [
            new QuickPlaceState { Icon = QuickPlaceRules.FolderIcon, Name = "副屏重复", Target = @"c:\shared" },
            new QuickPlaceState { Icon = QuickPlaceRules.FolderIcon, Name = "副屏B", Target = @"C:\SecondaryB" }
        ];
        Check(ScreenProfileAccessor.MoveDisconnectedQuickPlacesToPrimary(qState, SecondaryKey),
            "含有效自定义项的离线快捷入口移回主屏应返回 true");
        Check(qState.QuickPlaces.Count == 3
            && qState.QuickPlaces[0].Target == @"C:\PrimaryA"
            && qState.QuickPlaces[1].Target == @"C:\Shared"
            && qState.QuickPlaces[2].Target == @"C:\SecondaryB",
            $"移回主屏应合并保留主屏自定义项与顺序并去重，实际 {string.Join(',', qState.QuickPlaces.Select(p => p.Target))}");

        // Hidden + defaults-only secondary screen must report false without touching Primary.
        var emptySecKey = "DISPLAY#EMPTY#0";
        var emptyProfile = ScreenProfileAccessor.EnsureScreenProfile(qState, emptySecKey);
        emptyProfile.Experience.GetComponent(DesktopComponentKind.QuickLaunch).IsVisible = false;
        emptyProfile.QuickPlaces = QuickPlaceRules.CreateDefaults();
        Check(!ScreenProfileAccessor.MoveDisconnectedQuickPlacesToPrimary(qState, emptySecKey),
            "默认且隐藏的离线快捷入口不应虚报移回主屏");

        // Cap enforcement at QuickPlaceRules.MaximumItems (12).
        qState.QuickPlaces = Enumerable.Range(1, 10)
            .Select(i => new QuickPlaceState { Icon = QuickPlaceRules.FolderIcon, Name = $"P{i}", Target = $@"C:\P{i}" })
            .ToList();
        secProfile.Experience.GetComponent(DesktopComponentKind.QuickLaunch).IsVisible = true;
        secProfile.QuickPlaces = Enumerable.Range(1, 5)
            .Select(i => new QuickPlaceState { Icon = QuickPlaceRules.FolderIcon, Name = $"S{i}", Target = $@"C:\S{i}" })
            .ToList();
        var mergedCap = ScreenProfileAccessor.MoveQuickPlacesToScreen(qState, SecondaryKey, null);
        Check(mergedCap.Count == QuickPlaceRules.MaximumItems && mergedCap[0].Target == @"C:\P1" && mergedCap[11].Target == @"C:\S2",
            $"合并后超出 {QuickPlaceRules.MaximumItems} 项时应截断上限并保留主屏优先顺序，实际 {mergedCap.Count}");

        // P1-4: Moving primary Todo out syncs remaining Todo to component; moving secondary in appends to end.
        var tState = LocalStateStore.CreateDefault();
        tState.MultiScreen = new MultiScreenState { IsEnabled = true };
        var todoA = tState.TodoWidgets[0];
        todoA.Placement.Left = 100d;
        todoA.Placement.Top = 100d;
        var todoB = TodoService.CreateDefaultWidget();
        todoB.Placement.Left = 520d;
        todoB.Placement.Top = 280d;
        tState.TodoWidgets.Add(todoB);
        ScreenProfileAccessor.MoveTodoToScreen(tState, todoA.Id, SecondaryKey, SecondaryWorkPx, SecondaryDpi);
        var rootComp = tState.DesktopExperience.GetComponent(DesktopComponentKind.Todo);
        Check(rootComp.IsVisible && Math.Abs(rootComp.Placement.Left - 520d) <= 1d && Math.Abs(rootComp.Placement.Top - 280d) <= 1d,
            $"移走原主待办 A 后组件应同步剩余主待办 B 坐标，实际 ({rootComp.Placement.Left},{rootComp.Placement.Top})");
        ScreenProfileAccessor.MoveTodoToScreen(tState, todoA.Id, null, PrimaryWorkPx, PrimaryDpi);
        Check(ScreenProfileAccessor.GetPrimaryTodoId(tState, null) == todoB.Id
            && tState.TodoWidgets.Last().Id == todoA.Id,
            "主屏已有待办时移回的待办应追加末尾，不得抢占当前主待办 B");

        // P2-12: ApplyPresetToScreen preserves oldest-first DisplayLayouts order and evicts oldest past 6.
        var lState = LocalStateStore.CreateDefault();
        lState.MultiScreen = new MultiScreenState { IsEnabled = true };
        var lProfile = ScreenProfileAccessor.EnsureScreenProfile(lState, SecondaryKey);
        var weatherComp = lProfile.Experience.GetComponent(DesktopComponentKind.Weather);
        weatherComp.Placement.DisplayLayouts = Enumerable.Range(1, 6)
            .Select(i => new DisplayLayoutPlacement { Layout = $"L{i}", Left = i, Top = i, Right = i + 100, Bottom = i + 100 })
            .ToList();
        ScreenProfileAccessor.ApplyPresetToScreen(
            lState,
            SecondaryKey,
            DesktopLayoutPreset.GlacierWorkbench,
            SecondaryWorkPx,
            SecondaryDpi,
            "L7");
        var layoutsAfter = lProfile.Experience.GetComponent(DesktopComponentKind.Weather).Placement.DisplayLayouts;
        Check(layoutsAfter.Count == DisplayLayoutRules.MaximumRememberedLayouts
            && layoutsAfter[0].Layout == "L2"
            && layoutsAfter[^1].Layout == "L7",
            $"ApplyPresetToScreen 应保留旧→新顺序并仅淘汰最老记录 L1，实际 [{string.Join(',', layoutsAfter.Select(d => d.Layout))}]");
    }

    private static void TestGroupedFileBoxMultiScreenSync()
    {
        var gid = Guid.NewGuid();
        var activeTab = new FileBoxState
        {
            Title = "组主卡",
            GroupId = gid,
            TabOrder = 0,
            IsActiveTab = true,
            ScreenKey = SecondaryKey
        };
        var mixedTab = new FileBoxState
        {
            Title = "组副卡",
            GroupId = gid,
            TabOrder = 1,
            IsActiveTab = false,
            ScreenKey = null // Intentional mismatch repaired by NormalizeGroups.
        };
        FileBoxGroupRules.NormalizeGroups([activeTab, mixedTab]);
        Check(mixedTab.ScreenKey == SecondaryKey,
            $"NormalizeGroups 应将同 GroupId 成员 ScreenKey 对齐到活动标签页 ({SecondaryKey})，实际 {mixedTab.ScreenKey}");

        var state = LocalStateStore.CreateDefault();
        state.MultiScreen = new MultiScreenState { IsEnabled = true };
        ScreenProfileAccessor.EnsureScreenProfile(state, SecondaryKey, @"\\.\DISPLAY1", "副屏 2");
        activeTab.EdgeDockSide = FileBoxEdgeDockSide.Right;
        mixedTab.EdgeDockSide = FileBoxEdgeDockSide.Right;
        state.FileBoxes.Add(activeTab);
        state.FileBoxes.Add(mixedTab);

        // Moving one tab in the group to primary moves the entire group together & clears EdgeDock on all members.
        var movedPx = ScreenProfileAccessor.MoveFileBoxToScreen(
            state, mixedTab.Id, null, PrimaryWorkPx, PrimaryDpi,
            sourceBoundsPx: new PixelRect(2148, 50, 2500, 350), sourceWorkAreaPx: SecondaryWorkPx, sourceDpi: SecondaryDpi);
        Check(movedPx is not null && activeTab.ScreenKey is null && mixedTab.ScreenKey is null
            && activeTab.EdgeDockSide is null && mixedTab.EdgeDockSide is null
            && activeTab.Placement.MonitorLocal is null && mixedTab.Placement.MonitorLocal is null,
            "MoveFileBoxToScreen 必须将同 GroupId 所有标签页整组同步迁移并清除所有贴边停靠锚点");

        // Move group back to secondary, then verify FindDisconnectedScreenWidgets emits ONE grouped entry.
        ScreenProfileAccessor.MoveFileBoxToScreen(
            state, activeTab.Id, SecondaryKey, SecondaryWorkPx, SecondaryDpi,
            sourceBoundsPx: new PixelRect(120, 120, 480, 420), sourceWorkAreaPx: PrimaryWorkPx, sourceDpi: PrimaryDpi);
        Check(activeTab.ScreenKey == SecondaryKey && mixedTab.ScreenKey == SecondaryKey
            && activeTab.Placement.MonitorLocal is not null && mixedTab.Placement.MonitorLocal is not null,
            "整组移回副屏后全部标签页应统一记录 ScreenKey 与 MonitorLocal");

        var offline = ScreenProfileAccessor.FindDisconnectedScreenWidgets(
            state, connectedSecondaryKeys: new HashSet<string>(StringComparer.Ordinal));
        var boxEntries = offline.Where(e => e.KindName == ScreenProfileAccessor.DisconnectedFileBoxKind).ToList();
        Check(boxEntries.Count == 1 && boxEntries[0].Title.Contains("组合", StringComparison.Ordinal),
            $"离线发现对同 GroupId 多页收纳盒只能上报 1 条组合条目，实际 {boxEntries.Count} 条");

        // Merge adopts target's ScreenKey & MonitorLocal; Detach & SplitAll preserve host's ScreenKey and offset MonitorLocal.
        var standalonePrimary = new FileBoxState { Title = "主屏独立盒", ScreenKey = null };
        FileBoxGroupRules.Merge([standalonePrimary], [activeTab, mixedTab]);
        Check(standalonePrimary.GroupId == gid && standalonePrimary.ScreenKey == SecondaryKey
            && standalonePrimary.Placement.MonitorLocal is not null,
            "Merge 应使并入标签继承目标组合盒的 ScreenKey 与 MonitorLocal");

        var prevLocalLeft = standalonePrimary.Placement.MonitorLocal!.LocalLeftDip;
        var remaining = FileBoxGroupRules.Detach(standalonePrimary, [activeTab, mixedTab, standalonePrimary]);
        Check(remaining.Count == 2 && standalonePrimary.GroupId is null && standalonePrimary.ScreenKey == SecondaryKey
            && Math.Abs((standalonePrimary.Placement.MonitorLocal?.LocalLeftDip ?? 0d) - (prevLocalLeft + FileBoxGroupRules.DefaultDetachOffsetDip)) < 0.01d,
            "Detach 后独立出来的文件盒应保留原 ScreenKey 并在 MonitorLocal 中保持偏移防重叠");

        var split = FileBoxGroupRules.SplitAll(remaining);
        Check(split.Count == 2 && split.All(b => b.GroupId is null && b.ScreenKey == SecondaryKey)
            && split[1].Placement.MonitorLocal is not null
            && split[1].Placement.MonitorLocal!.LocalLeftDip > split[0].Placement.MonitorLocal!.LocalLeftDip,
            "SplitAll 后所有拆出成员应保留所属 ScreenKey 且 MonitorLocal 带级联偏移");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
