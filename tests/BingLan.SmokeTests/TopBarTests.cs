using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.Themes;
using BingLan.Core.TopBar;
using System.Text.Json;

namespace BingLan.SmokeTests;

/// <summary>Top bar model, persistence, mode orchestration, geometry and module rules.</summary>
public static class TopBarTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void Run()
    {
        TestDefaultsAndNormalize();
        TestPersistenceAndMigration();
        TestDesktopModeOrchestration();
        TestAttentionQueue();
        TestModuleRules();
        TestRevealZoneAndTopEdge();
        TestThemeTopBarFields();
    }

    private static void TestDefaultsAndNormalize()
    {
        var state = new TopBarState();
        Assert(!state.IsEnabled, "顶栏默认关闭");
        Assert(state.VisibilityMode == TopBarVisibilityMode.ReserveTopEdge, "默认预留顶边");
        Assert(state.MonitorDeviceName is null, "默认主屏");
        Assert(Math.Abs(state.SurfaceOpacity - 0.84d) < 0.001d, "默认不透明度");
        foreach (var kind in TopBarRules.ModuleOrder)
        {
            Assert(TopBarRules.IsModuleOn(state.Modules, kind), $"模块 {kind} 默认开启");
        }

        state.VisibilityMode = (TopBarVisibilityMode)99;
        state.SurfaceOpacity = 5d;
        state.SurfaceColor = "not-a-color";
        // A file from before the top bar carries no module node, which reads as null.
        state.Modules = JsonSerializer.Deserialize<TopBarState>("{}")!.Modules;
        TopBarRules.Normalize(state);
        Assert(state.VisibilityMode == TopBarVisibilityMode.ReserveTopEdge, "未知显示方式回退预留");
        Assert(Math.Abs(state.SurfaceOpacity - 1d) < 0.001d, "不透明度钳制到 1");
        Assert(state.Modules is not null, "模块开关缺失时补默认");

        TopBarRules.SetModule(state.Modules!, TopBarModuleKind.Volume, false);
        Assert(!TopBarRules.IsModuleOn(state.Modules!, TopBarModuleKind.Volume), "模块开关可关闭");
        Assert(TopBarRules.IsModuleOn(state.Modules!, TopBarModuleKind.Clock), "其他模块不受影响");
    }

    private static void TestPersistenceAndMigration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "binglan-topbar-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new LocalStateStore(directory);
            var state = store.Load();
            Assert(state.SchemaVersion == AppState.CurrentSchemaVersion, "新状态为当前版本");
            Assert(state.TopBar is { IsEnabled: false }, "新状态顶栏默认关闭");

            state.TopBar.IsEnabled = true;
            state.TopBar.MonitorDeviceName = "\\\\.\\DISPLAY2";
            state.TopBar.VisibilityMode = TopBarVisibilityMode.SmartHide;
            TopBarRules.SetModule(state.TopBar.Modules, TopBarModuleKind.Network, false);
            state.TodoWidgets.Add(new TodoWidgetState
            {
                Items =
                [
                    new TodoItemState { Text = "A" },
                    new TodoItemState { Text = "B", IsCompleted = true }
                ]
            });
            store.Save(state);

            var reloaded = store.Load();
            Assert(reloaded.TopBar.IsEnabled, "顶栏开关持久化");
            Assert(reloaded.TopBar.MonitorDeviceName == "\\\\.\\DISPLAY2", "显示器持久化");
            Assert(reloaded.TopBar.VisibilityMode == TopBarVisibilityMode.SmartHide, "显示方式持久化");
            Assert(!TopBarRules.IsModuleOn(reloaded.TopBar.Modules, TopBarModuleKind.Network), "模块开关持久化");

            // A v26 file has no TopBar node; loading gives a bar that is off.
            var v26 = JsonSerializer.SerializeToNode(state);
            v26!["SchemaVersion"] = 26;
            v26["TopBar"] = null;
            File.WriteAllText(
                Path.Combine(directory, "widgets.json"),
                v26.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var migrated = store.Load();
            Assert(migrated.SchemaVersion == 27, "v26 迁移到 v27");
            Assert(migrated.TopBar is { IsEnabled: false }, "迁移后顶栏默认关闭");
            Assert(migrated.TodoWidgets.Count == state.TodoWidgets.Count, "迁移不改动待办");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void TestDesktopModeOrchestration()
    {
        var dock = new DockState();
        var taskbar = new TaskbarState();
        var topBar = new TopBarState();
        var experience = DesktopExperienceRules.CreateDefault();

        DesktopModeRules.Apply(DesktopMode.AppleStyle, dock, taskbar, topBar, experience);
        Assert(topBar.IsEnabled, "苹果式开顶栏");
        Assert(taskbar.Mode == TaskbarMode.SmartHide, "苹果式任务栏智能隐藏（既有语义）");
        Assert(!experience.GetComponent(DesktopComponentKind.TimeDate).IsVisible, "苹果式关时间卡");
        Assert(!experience.GetComponent(DesktopComponentKind.Weather).IsVisible, "苹果式关天气卡");
        Assert(!experience.GetComponent(DesktopComponentKind.Performance).IsVisible, "苹果式关性能卡");
        Assert(experience.GetComponent(DesktopComponentKind.Greeting).IsVisible, "问候语不受影响");
        Assert(experience.GetComponent(DesktopComponentKind.Todo).IsVisible, "待办卡不受影响");

        // 顶栏开关不参与模式回显：苹果式关掉顶栏后仍回显苹果式（任务栏组合不变）。
        topBar.IsEnabled = false;
        Assert(DesktopModeRules.Detect(dock, taskbar) == DesktopMode.AppleStyle, "顶栏不参与 Detect");

        DesktopModeRules.Apply(DesktopMode.IceBlueHybrid, dock, taskbar, topBar, experience);
        Assert(!topBar.IsEnabled, "冰蓝混合关顶栏");
        Assert(taskbar.Mode == TaskbarMode.Transparent, "冰蓝混合任务栏透明");
        Assert(experience.GetComponent(DesktopComponentKind.TimeDate).IsVisible, "冰蓝混合恢复信息卡");

        DesktopModeRules.Apply(DesktopMode.WindowsNative, dock, taskbar, topBar, experience);
        Assert(!topBar.IsEnabled && !dock.IsEnabled, "Windows 原生关顶栏与 Dock");
        Assert(taskbar.Mode == TaskbarMode.SystemDefault, "Windows 原生任务栏默认");
        Assert(experience.GetComponent(DesktopComponentKind.Weather).IsVisible, "Windows 原生恢复信息卡");
    }

    private static void TestAttentionQueue()
    {
        var queue = new TopBarAttentionQueue();
        Assert(queue.Flashed(101), "首次闪烁入队");
        Assert(!queue.Flashed(101), "重复闪烁不重复入队");
        Assert(!queue.Flashed(0), "空句柄忽略");
        Assert(
            queue.Flashed(202) && queue.Flashed(303) && queue.Flashed(404) && queue.Flashed(505),
            "多个窗口依次入队");
        Assert(
            queue.OrderedWindows.SequenceEqual([101, 202, 303, 404, 505]),
            "按请求先后排序");

        Assert(queue.Cleared(303), "激活清除");
        Assert(!queue.Cleared(999), "未入队清除返回假");
        queue.Flashed(303);
        Assert(
            queue.OrderedWindows.SequenceEqual([101, 202, 404, 505, 303]),
            "再次闪烁排到队尾");

        queue.Retain([101, 505, 303]);
        Assert(
            queue.OrderedWindows.SequenceEqual([101, 505, 303]),
            "保留仍在运行的窗口");

        var (shown, overflow) = TopBarAttentionQueue.ResolveDisplay(queue.OrderedWindows, 3);
        Assert(shown.Count == 3 && overflow == 0, "刚好上限时全部显示");
        queue.Flashed(606);
        queue.Flashed(707);
        (shown, overflow) = TopBarAttentionQueue.ResolveDisplay(queue.OrderedWindows, 3);
        Assert(shown.SequenceEqual([101, 505, 303]), "显示前三个");
        Assert(overflow == 2, "剩余折叠计数");
        (shown, overflow) = TopBarAttentionQueue.ResolveDisplay([], 3);
        Assert(shown.Count == 0 && overflow == 0, "空队列无溢出");
    }

    private static void TestModuleRules()
    {
        Assert(TopBarModuleRules.CountIncompleteTodos([]) == 0, "无待办卡计 0");

        var widgets = new List<TodoWidgetState>
        {
            new()
            {
                Items =
                [
                    new TodoItemState { Text = "A" },
                    new TodoItemState { Text = "B", IsCompleted = true },
                    new TodoItemState { Text = "" }
                ]
            },
            new()
            {
                Items =
                [
                    new TodoItemState { Text = "C" },
                    new TodoItemState { Text = "D" }
                ]
            }
        };
        Assert(TopBarModuleRules.CountIncompleteTodos(widgets) == 3, "统计未完成且有文字的项");

        Assert(
            TopBarModuleRules.ClassifyBattery(0, 1) == TopBarBatteryStatus.OnBattery,
            "AC 断开为电池供电");
        Assert(
            TopBarModuleRules.ClassifyBattery(1, 8) == TopBarBatteryStatus.Charging,
            "AC 接入且充电标志为充电");
        Assert(
            TopBarModuleRules.ClassifyBattery(1, 1) == TopBarBatteryStatus.PluggedIn,
            "AC 接入无充电标志为已接电源");
        Assert(
            TopBarModuleRules.ClassifyBattery(255, 255) == TopBarBatteryStatus.Unknown,
            "未知 AC 状态");
        Assert(TopBarModuleRules.IsBatteryPercentKnown(100), "100% 是已知电量");
        Assert(TopBarModuleRules.IsBatteryPercentKnown(0), "0% 是已知电量");
        Assert(!TopBarModuleRules.IsBatteryPercentKnown(128), "128 表示未知");
        Assert(!TopBarModuleRules.IsBatteryPercentKnown(255), "255 表示未知");

        var network = TopBarModuleRules.ClassifyNetwork([(true, true), (true, false)]);
        Assert(network is { Connected: true, Label: "WLAN" }, "无线优先命名");
        network = TopBarModuleRules.ClassifyNetwork([(true, false)]);
        Assert(network is { Connected: true, Label: "以太网" }, "仅有线为以太网");
        network = TopBarModuleRules.ClassifyNetwork([(false, true)]);
        Assert(network is { Connected: false, Label: "未连接" }, "无可用接口为未连接");

        Assert(TopBarModuleRules.ExtractLanguageId(0x08040804) == 0x0804, "取 HKL 低字语言 ID");
        Assert(TopBarModuleRules.ExtractLanguageId(0x04090409) == 0x0409, "英文布局语言 ID");

        // 微软拼音式 HKL（变体在高 16 位），转换模式的本地文字位决定中/英徽标。
        Assert(TopBarModuleRules.DescribeInputMethod((nint)0xE0200804, 0x0409) == "中", "中文 IME 本地模式显示中");
        Assert(TopBarModuleRules.DescribeInputMethod((nint)0xE0200804, 0x0) == "英", "中文 IME 英文模式显示英");
        Assert(TopBarModuleRules.DescribeInputMethod((nint)0xE0200804, 0x0408) == "英", "全角英文仍非本地模式");
        Assert(TopBarModuleRules.DescribeInputMethod((nint)0xE0080404, 0x0401) == "繁", "繁中 IME 本地模式显示繁");
        Assert(TopBarModuleRules.DescribeInputMethod((nint)0xE0200804, null) == "ZH", "IME 未答问时回退语言码不猜模式");
        Assert(TopBarModuleRules.DescribeInputMethod(0x00000409, 0x0401) == "EN", "普通布局不受转换模式影响");
        Assert(TopBarModuleRules.DescribeInputMethod((nint)0xE0010411, 0x0001) == "JA", "非中文 IME 显示语言码");
        Assert(TopBarModuleRules.DescribeInputMethod(0, null) == "键盘", "无布局回退占位文案");

        Assert(TopBarModuleRules.VolumePercentFromScalar(0.42f) == 42, "音量换算百分比");
        Assert(TopBarModuleRules.VolumePercentFromScalar(1.3f) == 100, "音量钳制上限");
        Assert(TopBarModuleRules.VolumePercentFromScalar(-0.5f) == 0, "音量钳制下限");
        Assert(TopBarModuleRules.VolumePercentFromScalar(float.NaN) == 0, "非数值按 0");
    }

    private static void TestRevealZoneAndTopEdge()
    {
        var monitor = new PixelRect(0, 0, 1920, 1080);

        Assert(TopBarRevealRules.IsInRevealZone(960, 0, monitor, 2), "贴顶命中唤出区");
        Assert(TopBarRevealRules.IsInRevealZone(960, 1, monitor, 2), "唤出区内");
        Assert(!TopBarRevealRules.IsInRevealZone(960, 2, monitor, 2), "唤出区外");
        Assert(!TopBarRevealRules.IsInRevealZone(1920, 0, monitor, 2), "横向越界不命中");
        Assert(TopBarRevealRules.IsInRevealZone(0, 0, monitor, 1), "最小深度仍为 1 像素条");
        // 副屏按各自边界判定：屏内命中，右边界外（主屏）不命中。
        // 现实中的副屏有真实宽度，右边界外（主屏）不命中。
        var secondaryReal = new PixelRect(-1920, 0, 1920, 1080);
        Assert(
            TopBarRevealRules.IsInRevealZone(-5, 1, secondaryReal, 2)
            && !TopBarRevealRules.IsInRevealZone(1920, 1, secondaryReal, 2),
            "副屏右边界外不命中");

        Assert(TopBarReserveRules.IsTopEdgeFree(monitor, new PixelRect(0, 0, 1920, 1040)), "工作区贴顶可预留");
        Assert(
            !TopBarReserveRules.IsTopEdgeFree(monitor, new PixelRect(0, 48, 1920, 1032)),
            "工作区下移（顶部任务栏）不可预留");

        // The bar's own reservation pushes the work area down; re-evaluating while
        // registered must not read that as a foreign taskbar and let go.
        var ownGap = new PixelRect(0, 32, 1920, 1000);
        Assert(
            TopBarReserveRules.ShouldReserve(true, true, monitor, ownGap),
            "已预留时重评估应保持预留（自身预留不是外来占用）");
        Assert(
            !TopBarReserveRules.ShouldReserve(false, true, monitor, ownGap),
            "切到智能隐藏仍应释放");
        Assert(
            !TopBarReserveRules.ShouldReserve(true, false, monitor, new PixelRect(0, 48, 1920, 1032)),
            "未注册且顶边被任务栏占用时不预留");
        Assert(
            TopBarReserveRules.ShouldReserve(true, false, monitor, new PixelRect(0, 0, 1920, 1040)),
            "未注册且顶边空闲时预留");

        // DockGeometry 的顶边条已是满宽，顶栏直接复用。
        var strip = DockGeometry.Calculate(monitor, DockEdge.Top, 40);
        Assert(strip is { Left: 0, Top: 0, Right: 1920, Bottom: 40 }, "顶边条满宽贴顶");
    }

    private static void TestThemeTopBarFields()
    {
        var state = new AppState();
        state.TopBar.IsEnabled = true;
        state.TopBar.VisibilityMode = TopBarVisibilityMode.SmartHide;
        TopBarRules.SetModule(state.TopBar.Modules, TopBarModuleKind.InputMethod, false);
        state.TopBar.FollowCardLook = false;
        state.TopBar.SurfaceColor = "#112233";
        state.TopBar.SurfaceOpacity = 0.5d;
        state.Dock.PinnedApps.Clear();

        var area = new ThemeArea(0, 0, 1920, 1040);
        var package = ThemeRules.Export(state, "顶栏主题", area, new Version(0, 2, 13));
        Assert(package.Bindings.TopBarEnabled, "导出顶栏开关");
        Assert(package.Bindings.TopBarVisibility == TopBarVisibilityMode.SmartHide, "导出显示方式");
        Assert(!package.Bindings.TopBarModules!.InputMethod, "导出模块开关");
        Assert(!package.Bindings.TopBarFollowCardLook, "导出自定义外观");
        Assert(package.Bindings.TopBarSurfaceColor == "#112233", "导出自定义颜色");

        // 主题不含显示器标识。
        var json = JsonSerializer.Serialize(package);
        Assert(!json.Contains("DISPLAY"), "主题不携带显示器标识");
        Assert(!json.Contains(state.TopBar.MonitorDeviceName ?? "null-marker"), "无显示器名");

        var target = new TopBarState();
        target.MonitorDeviceName = "\\\\.\\DISPLAY7";
        ThemeRules.ApplyTopBar(package, target);
        Assert(target.IsEnabled, "导入顶栏开关");
        Assert(target.VisibilityMode == TopBarVisibilityMode.SmartHide, "导入显示方式");
        Assert(!TopBarRules.IsModuleOn(target.Modules, TopBarModuleKind.InputMethod), "导入模块开关");
        Assert(target.MonitorDeviceName == "\\\\.\\DISPLAY7", "导入不动显示器");

        // 旧主题（无顶栏字段）导入：关闭顶栏、保留本机模块开关。
        var legacy = JsonSerializer.SerializeToNode(package)!;
        legacy["Bindings"] = JsonSerializer.SerializeToNode(new ThemeBindings());
        var legacyPackage = legacy.Deserialize<ThemePackage>()!;
        var kept = new TopBarState();
        TopBarRules.SetModule(kept.Modules, TopBarModuleKind.Battery, false);
        ThemeRules.ApplyTopBar(legacyPackage, kept);
        Assert(!kept.IsEnabled, "旧主题导入后顶栏关闭");
        Assert(!TopBarRules.IsModuleOn(kept.Modules, TopBarModuleKind.Battery), "旧主题保留本机模块开关");

        // 越界值经 Normalize 钳制。
        package.Bindings.TopBarSurfaceOpacity = 7d;
        package.Bindings.TopBarVisibility = (TopBarVisibilityMode)50;
        ThemeRules.Normalize(package);
        Assert(Math.Abs(package.Bindings.TopBarSurfaceOpacity - 1d) < 0.001d, "主题顶栏不透明度钳制");
        Assert(
            package.Bindings.TopBarVisibility == TopBarVisibilityMode.ReserveTopEdge,
            "主题顶栏显示方式回退");
    }
}
