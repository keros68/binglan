using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.Themes;
using BingLan.Core.TopBar;
using System.Runtime.InteropServices;
using System.Text;
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
        TestFrostedScrimRule();
        TestPersistenceAndMigration();
        TestDesktopModeOrchestration();
        TestAttentionQueue();
        TestModuleRules();
        TestCapsuleSummary();
        TestRevealZoneAndTopEdge();
        TestThemeTopBarFields();
        TestModuleOrderAndMigration();
    }

    private static void TestDefaultsAndNormalize()
    {
        var state = new TopBarState();
        Assert(!state.IsEnabled, "顶栏默认关闭");
        Assert(state.VisibilityMode == TopBarVisibilityMode.ReserveTopEdge, "默认预留顶边");
        Assert(state.DisplayMode == TopBarDisplayMode.FullWidth, "默认整条布局");
        Assert(state.MonitorDeviceName is null, "默认主屏");
        Assert(Math.Abs(state.SurfaceOpacity - 0.82d) < 0.001d, "默认不透明度");
        Assert(state.SurfaceColor == TopBarState.DefaultSurfaceColor, "默认表面色");
        Assert(!state.FollowCardLook, "默认深色菜单栏外观，不跟随卡片");
        Assert(Math.Abs(state.Height - TopBarState.DefaultHeightDip) < 0.001d, "默认高度");
        Assert(state.FrostedGlass, "默认开启磨砂");
        foreach (var kind in TopBarRules.ModuleOrder)
        {
            Assert(TopBarRules.IsModuleOn(state.Modules, kind), $"模块 {kind} 默认开启");
        }

        state.VisibilityMode = (TopBarVisibilityMode)99;
        state.DisplayMode = (TopBarDisplayMode)99;
        state.SurfaceOpacity = 5d;
        state.SurfaceColor = "not-a-color";
        state.Height = 500d;
        // A file from before the top bar carries no module node, which reads as null.
        state.Modules = JsonSerializer.Deserialize<TopBarState>("{}")!.Modules;
        TopBarRules.Normalize(state);
        Assert(state.VisibilityMode == TopBarVisibilityMode.ReserveTopEdge, "未知显示方式回退预留");
        Assert(state.DisplayMode == TopBarDisplayMode.FullWidth, "未知布局回退整条");
        Assert(Math.Abs(state.SurfaceOpacity - 1d) < 0.001d, "不透明度钳制到 1");
        Assert(Math.Abs(state.Height - TopBarState.MaximumHeightDip) < 0.001d, "高度钳制到上限");
        Assert(state.Modules is not null, "模块开关缺失时补默认");

        TopBarRules.SetModule(state.Modules!, TopBarModuleKind.Volume, false);
        Assert(!TopBarRules.IsModuleOn(state.Modules!, TopBarModuleKind.Volume), "模块开关可关闭");
        Assert(TopBarRules.IsModuleOn(state.Modules!, TopBarModuleKind.Clock), "其他模块不受影响");
    }

    private static void TestFrostedScrimRule()
    {
        var state = new TopBarState();
        Assert(DesktopStyleRules.TopBarFrostedScrim(state, "#D61E1F24"), "深色表面默认应有磨砂");
        Assert(!DesktopStyleRules.TopBarFrostedScrim(state, "#D6D8CFF0"), "浅色表面不应有磨砂");
        Assert(!DesktopStyleRules.TopBarFrostedScrim(state, "#01FFFFFF"), "近透明表面不应有磨砂");
        state.FrostedGlass = false;
        Assert(!DesktopStyleRules.TopBarFrostedScrim(state, "#D61E1F24"), "关闭磨砂后深色表面也不应有磨砂");
        Assert(!DesktopStyleRules.TopBarFrostedScrim(null, "#D61E1F24"), "无状态不应有磨砂");
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
            state.TopBar.DisplayMode = TopBarDisplayMode.Capsule;
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
            Assert(reloaded.TopBar.DisplayMode == TopBarDisplayMode.Capsule, "胶囊布局持久化");
            Assert(!TopBarRules.IsModuleOn(reloaded.TopBar.Modules, TopBarModuleKind.Network), "模块开关持久化");

            var v32 = JsonSerializer.SerializeToNode(state)!;
            v32["SchemaVersion"] = 32;
            v32["TopBar"]!["Modules"]!.AsObject().Remove("TrayOverflow");
            File.WriteAllText(store.StatePath, v32.ToJsonString());
            var withTray = store.Load();
            Assert(withTray.SchemaVersion == AppState.CurrentSchemaVersion && withTray.TopBar.Modules.TrayOverflow,
                "v32 迁移后默认显示托盘入口");
            Assert(!withTray.TopBar.Modules.Network && withTray.TopBar.IsEnabled, "迁移保留其他顶栏设置");
            TopBarRules.SetModule(withTray.TopBar.Modules, TopBarModuleKind.TrayOverflow, false);
            store.Save(withTray);
            Assert(!store.Load().TopBar.Modules.TrayOverflow, "托盘入口关闭后持久化，不在重载时重新开启");

            // A v26 file has no TopBar node; loading gives a bar that is off.
            var v26 = JsonSerializer.SerializeToNode(state);
            v26!["SchemaVersion"] = 26;
            v26["TopBar"] = null;
            File.WriteAllText(
                Path.Combine(directory, "widgets.json"),
                v26.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var migrated = store.Load();
            Assert(migrated.SchemaVersion == AppState.CurrentSchemaVersion, "v26 迁移到当前版本");
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
        Assert(DesktopModeRules.Match(dock, taskbar, topBar) is { Mode: DesktopMode.WindowsNative, IsExact: true },
            "刚应用的方案应完全匹配");

        DesktopModeRules.Apply(DesktopMode.AppleStyle, dock, taskbar, topBar, experience);
        taskbar.Mode = TaskbarMode.Transparent;
        var drifted = DesktopModeRules.Match(dock, taskbar, topBar);
        Assert(drifted.Mode == DesktopMode.AppleStyle && drifted.Differences.SequenceEqual(["任务栏改为透明"]),
            "苹果式下改任务栏应显示为在苹果式基础上自定义，而不是误认成冰蓝混合");
        Assert(drifted is { TaskbarChanged: true, DockChanged: false, TopBarChanged: false },
            "方案摘要只把任务栏标为已自定义");
        taskbar.Mode = TaskbarMode.SmartHide;
        topBar.IsEnabled = false;
        drifted = DesktopModeRules.Match(dock, taskbar, topBar);
        Assert(drifted.Mode == DesktopMode.IceBlueHybrid && drifted.Differences.SequenceEqual(["任务栏改为自动隐藏"]),
            "没有顶栏的悬浮 Dock 按冰蓝混合描述，差异只剩任务栏样式");
        dock.IsEnabled = false;
        topBar.IsEnabled = true;
        taskbar.Mode = TaskbarMode.SystemDefault;
        drifted = DesktopModeRules.Match(dock, taskbar, topBar);
        Assert(drifted is { Mode: DesktopMode.WindowsNative, TopBarChanged: true, DockChanged: false, TaskbarChanged: false },
            "只开顶栏时按 Windows 原生描述，摘要只标出顶端信息条");

        DesktopModeRules.Apply(DesktopMode.AppleStyle, dock, taskbar, topBar, experience);
        Assert(DesktopModeRules.GetDockVisibilitySummary(dock) == "始终显示，保留底部空间",
            "方案默认应显示始终显示并保留底部空间");
        dock.ReleaseWhenMaximized = true;
        drifted = DesktopModeRules.Match(dock, taskbar, topBar);
        Assert(drifted is { Mode: DesktopMode.AppleStyle, DockChanged: true }
                && drifted.Differences.Contains("Dock 开启了最大化让出底部空间"),
            "开启最大化让出时应在偏离详情和 Dock 摘要标记中体现");
        Assert(DesktopModeRules.GetDockVisibilitySummary(dock) == "始终显示（最大化时让出底部空间）",
            "摘要应区分始终显示与最大化让出状态");
        DesktopModeRules.Apply(DesktopMode.AppleStyle, dock, taskbar, topBar, experience);
        Assert(!dock.ReleaseWhenMaximized, "重新应用桌面方案应复位最大化让出开关");

        dock.VisibilityMode = DockVisibilityMode.SmartHide;
        drifted = DesktopModeRules.Match(dock, taskbar, topBar);
        Assert(drifted is { Mode: DesktopMode.AppleStyle, DockChanged: true }
                && drifted.Differences.Contains("Dock 改为智能隐藏"),
            "切为智能隐藏时应体现 Dock 偏离");
        Assert(DesktopModeRules.GetDockVisibilitySummary(dock) == "智能隐藏",
            "智能隐藏摘要应显示智能隐藏");
    }

    private static void TestAttentionQueue()
    {
        var queue = new TopBarAttentionQueue();
        Assert(queue.Flashed(101, 1), "首次闪烁入队");
        Assert(!queue.Flashed(101, 1), "重复闪烁不重复入队");
        Assert(!queue.Flashed(0, 1), "空句柄忽略");
        Assert(
            queue.Flashed(202, 2) && queue.Flashed(303, 3) && queue.Flashed(404, 4) && queue.Flashed(505, 5),
            "多个窗口依次入队");
        Assert(
            queue.OrderedWindows.SequenceEqual([101, 202, 303, 404, 505]),
            "按请求先后排序");

        Assert(queue.Cleared(303), "激活清除");
        Assert(!queue.Cleared(999), "未入队清除返回假");
        Assert(queue.ProcessOf(101) == 1, "闪烁时记下进程供名字回退");
        queue.Flashed(303, 3);
        Assert(
            queue.OrderedWindows.SequenceEqual([101, 202, 404, 505, 303]),
            "再次闪烁排到队尾");

        // 只清真正销毁的句柄；隐藏消息窗不在任何枚举里，也不能被顺手删掉。
        queue.RemoveRange([202, 404]);
        Assert(
            queue.OrderedWindows.SequenceEqual([101, 505, 303]),
            "只移除已销毁的窗口");

        var (shown, overflow) = TopBarAttentionQueue.ResolveDisplay(queue.OrderedWindows, 3);
        Assert(shown.Count == 3 && overflow == 0, "刚好上限时全部显示");
        queue.Flashed(606, 6);
        queue.Flashed(707, 7);
        (shown, overflow) = TopBarAttentionQueue.ResolveDisplay(queue.OrderedWindows, 3);
        Assert(shown.SequenceEqual([101, 505, 303]), "显示前三个");
        Assert(overflow == 2, "剩余折叠计数");
        (shown, overflow) = TopBarAttentionQueue.ResolveDisplay([], 3);
        Assert(shown.Count == 0 && overflow == 0, "空队列无溢出");

        // 同一应用的多个隐藏消息窗口闪烁时合并为一条，激活该应用的可见主窗口后一并清除。
        var dedupQueue = new TopBarAttentionQueue();
        Assert(dedupQueue.Flashed(9001, 42), "隐藏消息窗口首次闪烁入队");
        Assert(!dedupQueue.Flashed(9002, 42), "同一进程第二个隐藏消息窗口不重复生成消息项");
        Assert(dedupQueue.OrderedWindows.SequenceEqual([9001]), "同一应用只保留一条顶栏消息提示");
        Assert(dedupQueue.Closed(9001), "首个隐藏窗口销毁时顺延到仍存活的闪烁窗口");
        Assert(dedupQueue.OrderedWindows.SequenceEqual([9002]) && dedupQueue.ProcessOf(9002) == 42, "顺延后保留进程归属");
        Assert(dedupQueue.Cleared(3001, 42), "激活同进程的可见主窗口应清除隐藏窗口残留的消息提示");
        Assert(dedupQueue.OrderedWindows.Count == 0, "激活主窗口后顶栏不再残留消息提示");

        // 应用宿主共享进程（ApplicationFrameHost）按句柄独立计数，不跨应用误合并或误清。
        var frameHostPid = AttentionFlashRules.EffectiveProcessId(88, @"C:\Windows\System32\ApplicationFrameHost.exe");
        Assert(frameHostPid == 0, "ApplicationFrameHost 不参与按进程合并");
        Assert(dedupQueue.Flashed(7001, frameHostPid) && dedupQueue.Flashed(7002, frameHostPid), "两个打包应用窗口分别保留");
        Assert(dedupQueue.Cleared(7001, frameHostPid) && dedupQueue.OrderedWindows.SequenceEqual([7002]), "激活其中一个打包应用不清另一个");

        // 过滤系统外壳、通知宿主、输入法及已处于前台的应用自身的闪烁，避免产生无法消除的“消息”。
        Assert(!AttentionFlashRules.ShouldRecordFlash(
                8001, true, 50, 99, @"C:\Windows\SystemApps\ShellExperienceHost_cw5n1h2txyewy\ShellExperienceHost.exe",
                "Windows.UI.Core.CoreWindow", 200, 20, false),
            "系统通知宿主闪烁不应生成顶栏消息");
        Assert(!AttentionFlashRules.ShouldRecordFlash(
                8002, true, 51, 99, @"C:\Windows\explorer.exe", "Shell_TrayWnd", 200, 20, false),
            "任务栏自身闪烁不应生成顶栏消息");
        Assert(AttentionFlashRules.ShouldRecordFlash(
                8003, true, 51, 99, @"C:\Windows\explorer.exe", "CabinetWClass", 200, 20, false),
            "文件资源管理器窗口不在前台时的闪烁允许提示");
        Assert(!AttentionFlashRules.ShouldRecordFlash(
                9001, true, 42, 99, @"D:\Program Files\Tencent\Weixin\Weixin.exe", "Qt51514QWindowIcon",
                3001, 42, foregroundIsMinimized: false),
            "微信主窗口已处于前台未最小化时，其隐藏窗口闪烁不应挂出常驻消息");
        Assert(AttentionFlashRules.ShouldRecordFlash(
                9001, true, 42, 99, @"D:\Program Files\Tencent\Weixin\Weixin.exe", "Qt51514QWindowIcon",
                3001, 42, foregroundIsMinimized: true),
            "微信主窗口已最小化时，其隐藏窗口闪烁仍应提示");
        Assert(AttentionFlashRules.ChooseDisplayName("微信", null, @"D:\App\Weixin.exe", "") == "微信",
            "托盘驻留无可见窗口时优先显示程序友好名称而非泛化的“消息”");
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

        var wifiDefault = TopBarModuleRules.DescribeNetwork(true, true, "Office-5G", 82);
        Assert(wifiDefault is { Connected: true, IsWireless: true, Label: "WLAN", Ssid: "Office-5G", SignalQuality: 82 }
            && wifiDefault.Tooltip == "WLAN：Office-5G（信号 82%）",
            "默认状态顶栏应只显示 WLAN，悬停保留名称与信号强度");
        Assert(TopBarModuleRules.FormatNetworkLabel(wifiDefault, showWifiName: false) == "WLAN"
            && TopBarModuleRules.FormatNetworkLabel(wifiDefault, showWifiName: true) == "Office-5G",
            "按开关切换顶栏是否显示 Wi-Fi 名称");
        Assert(new TopBarState().ShowWifiName is null or false, "新状态默认不显示 Wi-Fi 名称");

        var wifiShown = TopBarModuleRules.DescribeNetwork(true, true, "Office-5G", 82, showWifiName: true);
        Assert(wifiShown is { Connected: true, IsWireless: true, Label: "Office-5G", SignalQuality: 82 }
            && wifiShown.Tooltip == "WLAN：Office-5G（信号 82%）",
            "开启显示名称后顶栏应展示 Wi-Fi 名称与信号提示");
        var longWifi = TopBarModuleRules.DescribeNetwork(true, true, "VeryLongHomeNetworkName5G", 40, showWifiName: true);
        Assert(longWifi.Label.Length == TopBarModuleRules.MaxNetworkSsidDisplayLength
            && longWifi.Label.EndsWith("…", StringComparison.Ordinal)
            && longWifi.Tooltip.Contains("VeryLongHomeNetworkName5G", StringComparison.Ordinal),
            "超长 SSID 在顶栏截断、悬停保留全称");
        var fallbackWifi = TopBarModuleRules.DescribeNetwork(true, true, "  \0 ", null, showWifiName: true);
        Assert(fallbackWifi is { Label: "WLAN", Ssid: null, Tooltip: "WLAN" },
            "无可读 SSID 时应安静回退 WLAN");
        Assert(TopBarModuleRules.WifiSignalBars(10) == 1
            && TopBarModuleRules.WifiSignalBars(35) == 2
            && TopBarModuleRules.WifiSignalBars(60) == 3
            && TopBarModuleRules.WifiSignalBars(90) == 4
            && TopBarModuleRules.WifiSignalBars(null) == 4,
            "信号格数档位映射正确");
        var eth = TopBarModuleRules.DescribeNetwork(true, false, "Ignored", 100, showWifiName: true);
        Assert(eth is { Connected: true, IsWireless: false, Label: "以太网", Tooltip: "以太网" },
            "有线连接不显示 Wi-Fi SSID");

        // SSID 解码：合法 UTF-8 优先，非法 UTF-8（如 GBK 路由器广播）回退系统 ANSI 不出乱码。
        var utf8Ssid = Encoding.UTF8.GetBytes("客厅WiFi");
        var fallbackInvoked = false;
        var decodedUtf8 = TopBarModuleRules.DecodeSsidBytes(utf8Ssid, _ => { fallbackInvoked = true; return "wrong"; });
        Assert(decodedUtf8 == "客厅WiFi" && !fallbackInvoked, "合法 UTF-8 SSID 不应触发 ANSI 回退");

        // "客厅WiFi" 的 GBK (CP936) 字节序列：客=BF CD，厅=CC FC，WiFi=57 69 46 69。
        byte[] gbkSsid = [0xBF, 0xCD, 0xCC, 0xFC, 0x57, 0x69, 0x46, 0x69];
        var decodedGbk = TopBarModuleRules.DecodeSsidBytes(gbkSsid, DecodeCodePage936);
        Assert(decodedGbk == "客厅WiFi" && !decodedGbk.Contains('\uFFFD'),
            "GBK 非 UTF-8 SSID 回退 CP936 解码后不应出乱码");
        var decodedWithoutFallback = TopBarModuleRules.DecodeSsidBytes(gbkSsid, null);
        Assert(decodedWithoutFallback is null, "缺少 ANSI 回退时非法 UTF-8 应安全返回 null 而非乱码");
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

        // 64 位进程下符号扩展的 IMM32 HKL（如 0xFFFFFFFFE0200804）应只取低 32 位查注册表。
        var signExtendedIme = unchecked((nint)0xFFFFFFFFE0200804L);
        Assert(TopBarModuleRules.FormatLayoutRegistryCandidates(signExtendedIme).SequenceEqual(["E0200804", "0000E020", "00000804"]),
            "符号扩展的 IMM32 HKL 应规范化为 8 位注册表键名");
        Assert(TopBarModuleRules.FormatLayoutRegistryCandidates(0).Count == 0, "空 HKL 不生成注册表候选");
        Assert(TopBarModuleRules.IsLegacyImeLayout(signExtendedIme), "0xE0xx 高字应识别为传统 IMM32 输入法");
        Assert(!TopBarModuleRules.IsLegacyImeLayout(0x08040804), "0x08040804 不是传统 IMM32 输入法");
        Assert(!TopBarModuleRules.IsLegacyImeLayout(0x04090409), "英语键盘不是传统 IMM32 输入法");
        Assert(!TopBarModuleRules.IsLegacyImeLayout(unchecked((nint)0xFFFFFFFFF0010409L)), "0xF0xx 替代键盘布局不是传统 IMM32 输入法");
        Assert(TopBarModuleRules.ResolveInputMethodDisplayName("微信输入法", 0x08040804, "中文(简体) - 美式键盘") == "微信输入法",
            "TSF 输入法优先展示处理器名称");
        Assert(TopBarModuleRules.ResolveInputMethodDisplayName(null, signExtendedIme, "搜狗拼音输入法") == "搜狗拼音输入法",
            "无 TSF 名称时传统 IMM32 输入法回退注册表布局名");
        Assert(TopBarModuleRules.ResolveInputMethodDisplayName(null, 0x04090409, "美式键盘") is null,
            "普通键盘布局折叠名称仅留徽标与悬停提示");
        Assert(TopBarModuleRules.ResolveInputMethodDisplayName("微软拼音", 0x04090409, "美式键盘", 0x0804) is null,
            "跨语言前台窗口切到英文键盘时不应错配本线程残留的中文 TSF 输入法名");
        Assert(TopBarModuleRules.ResolveInputMethodDisplayName("微软拼音", 0x08040804, "中文(简体) - 美式键盘", 0x0804) == "微软拼音",
            "同语言 TSF 输入法应继续展示处理器名称");
        Assert(TopBarModuleRules.FormatInputMethodLanguageTag(0x0804) == "中"
            && TopBarModuleRules.FormatInputMethodLanguageTag(0x0404) == "繁"
            && TopBarModuleRules.FormatInputMethodLanguageTag(0x0409) == "EN",
            "输入法列表短标应区分简繁与外语");
        var msPinyin = new TopBarInputMethodOption(
            TopBarModuleRules.BuildInputMethodKey(true, 0x0804, Guid.Parse("81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E"), Guid.Parse("FA550B04-5AD7-411F-A5AC-CA038EC515D7"), 0x08040804),
            "微软拼音", "中", 0x0804, true, false, 0x08040804,
            Guid.Parse("81D4E9C9-1D3B-41BC-9E6C-4B40BF79E35E"), Guid.Parse("FA550B04-5AD7-411F-A5AC-CA038EC515D7"));
        var wechatIme = new TopBarInputMethodOption(
            TopBarModuleRules.BuildInputMethodKey(true, 0x0804, Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"), 0x08040804),
            " 微信输入法 ", "中", 0x0804, true, true, 0x08040804,
            Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var normalizedOptions = TopBarModuleRules.NormalizeInputMethodOptions([
            msPinyin,
            msPinyin with { IsActive = true },
            wechatIme,
            new TopBarInputMethodOption("bad", "   ", "EN", 0x0409, false, false, 0x04090409, Guid.Empty, Guid.Empty)
        ]);
        Assert(normalizedOptions.Count == 2
            && normalizedOptions[0].IsActive
            && normalizedOptions[1].DisplayName == "微信输入法"
            && msPinyin.Key != wechatIme.Key,
            "同 HKL 的不同中文 TSF 输入法应拥有独立标识、去重保留激活态并剔除空白项");

        Assert(TopBarModuleRules.VolumePercentFromScalar(0.42f) == 42, "音量换算百分比");
        Assert(TopBarModuleRules.VolumePercentFromScalar(1.3f) == 100, "音量钳制上限");
        Assert(TopBarModuleRules.VolumePercentFromScalar(-0.5f) == 0, "音量钳制下限");
        Assert(TopBarModuleRules.VolumePercentFromScalar(float.NaN) == 0, "非数值按 0");
    }

    private static void TestCapsuleSummary()
    {
        var now = new DateTime(2026, 11, 20, 10, 0, 0);
        var next = new TodoItemState { Text = "下一条", DeadlineDate = now.Date, DeadlineTime = new TimeOnly(11, 0) };
        var items = new TodoItemState[]
        {
            new() { Text = "已过期", DeadlineDate = now.Date, DeadlineTime = new TimeOnly(9, 0) },
            new() { Text = "明天", DeadlineDate = now.Date.AddDays(1), DeadlineTime = new TimeOnly(10, 30) },
            new() { Text = "已完成", IsCompleted = true, DeadlineDate = now.Date, DeadlineTime = new TimeOnly(10, 30) },
            new() { Text = "仅日期", DeadlineDate = now.Date },
            new() { Text = "行程", Kind = TodoItemKind.Event, DeadlineDate = now.Date, DeadlineTime = new TimeOnly(10, 30) },
            new() { Text = "稍后", DeadlineDate = now.Date, DeadlineTime = new TimeOnly(12, 0) },
            next
        };
        Assert(ReferenceEquals(TopBarModuleRules.NextTimedTodoToday(items, now), next), "胶囊选今日最近未完成定时待办");
        Assert(TopBarModuleRules.NextTimedTodoToday(items, now.AddHours(3)) is null, "无未来定时待办时不显示过期条目");
        Assert(JsonSerializer.Deserialize<TopBarState>("{}")!.DisplayMode == TopBarDisplayMode.FullWidth,
            "旧状态缺少布局字段时保持整条");
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

        // 智能隐藏右上角避让区：按实际重叠窗口右边缘计算，兼容右侧 Dock、半屏窗口与多 DPI。
        var fullBar = new PixelRect(0, 0, 1920, 32);
        var maximizedBrowser = new PixelRect(0, 0, 1920, 1040);
        var excluded100 = TopBarRevealRules.ComputeCaptionButtonExclusion(maximizedBrowser, fullBar, 160);
        Assert(excluded100 == (1760, 1920), "100% 缩放满屏最大化窗口排除最右 160 像素");
        Assert(!TopBarRevealRules.IsInRevealZone(1910, 0, fullBar, 2, excluded100), "右上角关闭按钮处不触发顶栏唤出");
        Assert(!TopBarRevealRules.IsInRevealZone(1760, 1, fullBar, 2, excluded100), "避让区左边界不触发唤出");
        Assert(TopBarRevealRules.IsInRevealZone(1759, 0, fullBar, 2, excluded100), "避让区左侧仍正常触顶唤出");
        Assert(TopBarRevealRules.IsInRevealZone(960, 0, fullBar, 2, excluded100), "屏幕顶边中部正常触顶唤出");

        var rightDockBrowser = new PixelRect(0, 0, 1820, 1080);
        var excludedWithRightDock = TopBarRevealRules.ComputeCaptionButtonExclusion(rightDockBrowser, fullBar, 240);
        Assert(excludedWithRightDock == (1580, 1820), "右侧 Dock 推窄最大化窗口时按窗口右缘而非屏幕右缘排除（150% DPI）");
        Assert(!TopBarRevealRules.IsInRevealZone(1800, 0, fullBar, 2, excludedWithRightDock), "右侧 Dock 场景下窗口关闭按钮不误唤出");
        Assert(TopBarRevealRules.IsInRevealZone(1850, 0, fullBar, 2, excludedWithRightDock), "窗口右侧空白区仍可唤出");

        var leftSnapped = new PixelRect(0, 0, 960, 1040);
        var excludedSnapped = TopBarRevealRules.ComputeCaptionButtonExclusion(leftSnapped, fullBar, 320);
        Assert(excludedSnapped == (640, 960), "左半屏贴顶窗口在 200% DPI 下按窗口右缘排除 320 像素");
        Assert(!TopBarRevealRules.IsInRevealZone(950, 0, fullBar, 2, excludedSnapped), "左半屏贴顶窗口关闭按钮不误唤出");

        var narrowWindow = new PixelRect(400, 0, 500, 400);
        Assert(TopBarRevealRules.ComputeCaptionButtonExclusion(narrowWindow, fullBar, 160) == (400, 500),
            "窄于排除宽度的窗口应钳制在窗口自身范围内");
        Assert(TopBarRevealRules.ComputeCaptionButtonExclusion(new PixelRect(0, 40, 1920, 1040), fullBar, 160) is null,
            "未与顶栏重叠的窗口不产生排除区");

        // 预留模式工作区实效校验与异常贴顶窗口检测。
        var reservedStrip = new PixelRect(0, 0, 1920, 31);
        Assert(TopBarReserveRules.IsWorkAreaReserved(new PixelRect(0, 31, 1920, 1040), reservedStrip),
            "工作区顶边下移到预留条下沿应判已生效");
        Assert(TopBarReserveRules.IsWorkAreaReserved(new PixelRect(0, 60, 1920, 1040), reservedStrip),
            "下方叠放其他 AppBar 使工作区更低仍应判已生效");
        Assert(!TopBarReserveRules.IsWorkAreaReserved(new PixelRect(0, 0, 1920, 1040), reservedStrip),
            "工作区顶边仍在 0 时不应把预留判为成功");

        var negStrip = new PixelRect(-1920, -1080, 0, -1049);
        Assert(TopBarReserveRules.IsWorkAreaReserved(new PixelRect(-1920, -1049, 0, -40), negStrip),
            "负坐标副屏工作区顶边下移应判已生效");
        Assert(!TopBarReserveRules.IsWorkAreaReserved(new PixelRect(-1920, -1080, 0, -40), negStrip),
            "负坐标副屏工作区未下移应判失败");

        Assert(!TopBarReserveRules.IsWindowCoveringReservedBar(new PixelRect(0, 31, 1920, 1040), reservedStrip),
            "正常位于预留条下方的最大化窗口不算侵入顶栏");
        Assert(!TopBarReserveRules.IsWindowCoveringReservedBar(new PixelRect(0, 30, 1920, 1040), reservedStrip),
            "底部 1 像素无缝贴合不应误判为侵入顶栏");
        Assert(TopBarReserveRules.IsWindowCoveringReservedBar(new PixelRect(120, 20, 1400, 900), reservedStrip),
            "顶边侵入预留顶栏下半段超过衔接缝容差的窗口应判侵入，避免顶栏压住标签栏");
        Assert(TopBarReserveRules.IsWindowCoveringReservedBar(new PixelRect(0, 0, 1920, 1040), reservedStrip),
            "顶边仍贴在 y=0 的最大化浏览器应判侵入预留顶栏");
        Assert(!TopBarReserveRules.IsWindowCoveringReservedBar(new PixelRect(1920, 0, 3840, 1040), reservedStrip),
            "另一块显示器上的贴顶窗口不应触发当前屏顶栏退避");
        Assert(TopBarReserveRules.IsWindowCoveringReservedBar(new PixelRect(-1920, -1080, 0, -40), negStrip),
            "负坐标副屏上顶边侵入的窗口应判侵入");
        Assert(!TopBarReserveRules.IsWindowCoveringReservedBar(new PixelRect(-1920, -1049, 0, -40), negStrip),
            "负坐标副屏上位于预留条下方的窗口不判侵入");

        // 新开普通非最大化窗口若顶边钻进预留顶栏，应一次性下移到工作区顶边，且不影响全屏/最大化/弹窗/另一屏。
        var workArea = new PixelRect(0, 32, 1920, 1000);
        var nudged = TopBarReserveRules.ComputeNudgedWindowRect(
            visibleBounds: new PixelRect(100, 0, 1300, 800),
            windowRect: new PixelRect(93, 0, 1307, 807),
            barBounds: fullBar,
            workArea: workArea,
            isMaximized: false,
            isFullScreen: false,
            isTopmost: false,
            isToolOrPopup: false,
            hasOwner: false,
            hasCaption: true);
        Assert(nudged == new PixelRect(93, 32, 1307, 839),
            "新开普通窗口在 y=0 时应保持尺寸下移到工作区顶边，并保留 DWM 阴影边偏移");

        var tallNudged = TopBarReserveRules.ComputeNudgedWindowRect(
            visibleBounds: new PixelRect(100, 0, 1300, 995),
            windowRect: new PixelRect(93, 0, 1307, 1002),
            barBounds: fullBar,
            workArea: workArea,
            isMaximized: false,
            isFullScreen: false,
            isTopmost: false,
            isToolOrPopup: false,
            hasOwner: false,
            hasCaption: true);
        Assert(tallNudged == new PixelRect(93, 32, 1307, 1007),
            "下移后底边超出工作区时应按工作区底边收紧高度");

        Assert(TopBarReserveRules.ComputeNudgedWindowRect(
                new PixelRect(100, 32, 1300, 800), new PixelRect(93, 32, 1307, 807),
                fullBar, workArea, false, false, false, false, false, true) is null,
            "已在顶栏下方的新窗口无需移动");
        Assert(TopBarReserveRules.ComputeNudgedWindowRect(
                new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1080),
                fullBar, workArea, false, true, false, false, false, true) is null,
            "全屏窗口不得移动");
        Assert(TopBarReserveRules.ComputeNudgedWindowRect(
                new PixelRect(0, 0, 1920, 1000), new PixelRect(0, 0, 1920, 1000),
                fullBar, workArea, true, false, false, false, false, true) is null,
            "最大化窗口不得移动");
        Assert(TopBarReserveRules.ComputeNudgedWindowRect(
                new PixelRect(100, 0, 600, 400), new PixelRect(100, 0, 600, 400),
                fullBar, workArea, false, false, true, false, false, true) is null,
            "置顶窗口不得移动");
        Assert(TopBarReserveRules.ComputeNudgedWindowRect(
                new PixelRect(100, 0, 600, 400), new PixelRect(100, 0, 600, 400),
                fullBar, workArea, false, false, false, true, false, false) is null,
            "无标题栏弹窗或工具窗口不得移动");
        Assert(TopBarReserveRules.ComputeNudgedWindowRect(
                new PixelRect(100, 0, 600, 400), new PixelRect(100, 0, 600, 400),
                fullBar, workArea, false, false, false, false, true, true) is null,
            "带宿主窗口的子对话框不得移动");
    }

    private static void TestThemeTopBarFields()
    {
        var state = new AppState();
        state.TopBar.IsEnabled = true;
        state.TopBar.VisibilityMode = TopBarVisibilityMode.SmartHide;
        TopBarRules.SetModule(state.TopBar.Modules, TopBarModuleKind.InputMethod, false);
        TopBarRules.SetModule(state.TopBar.Modules, TopBarModuleKind.TrayOverflow, false);
        state.TopBar.FollowCardLook = false;
        state.TopBar.SurfaceColor = "#112233";
        state.TopBar.SurfaceOpacity = 0.5d;
        state.TopBar.ShowWifiName = true;
        state.Dock.PinnedApps.Clear();

        var area = new ThemeArea(0, 0, 1920, 1040);
        var package = ThemeRules.Export(state, "顶栏主题", area, new Version(0, 2, 13));
        Assert(package.Bindings.TopBarEnabled, "导出顶栏开关");
        Assert(package.Bindings.TopBarVisibility == TopBarVisibilityMode.SmartHide, "导出显示方式");
        Assert(!package.Bindings.TopBarModules!.InputMethod, "导出模块开关");
        Assert(!package.Bindings.TopBarModules.TrayOverflow, "导出托盘入口开关");
        Assert(!package.Bindings.TopBarFollowCardLook, "导出自定义外观");
        Assert(package.Bindings.TopBarSurfaceColor == "#112233", "导出自定义颜色");

        // 主题不含显示器标识与本机隐私开关 ShowWifiName。
        var json = JsonSerializer.Serialize(package);
        Assert(!json.Contains("DISPLAY"), "主题不携带显示器标识");
        Assert(!json.Contains(state.TopBar.MonitorDeviceName ?? "null-marker"), "无显示器名");
        Assert(!json.Contains("ShowWifiName", StringComparison.OrdinalIgnoreCase), "主题不携带 ShowWifiName 本机设置");

        var target = new TopBarState();
        target.MonitorDeviceName = "\\\\.\\DISPLAY7";
        target.ShowWifiName = false;
        ThemeRules.ApplyTopBar(package, target);
        Assert(target.IsEnabled, "导入顶栏开关");
        Assert(target.VisibilityMode == TopBarVisibilityMode.SmartHide, "导入显示方式");
        Assert(!TopBarRules.IsModuleOn(target.Modules, TopBarModuleKind.InputMethod), "导入模块开关");
        Assert(!target.Modules.TrayOverflow, "导入保留托盘入口开关");
        Assert(target.MonitorDeviceName == "\\\\.\\DISPLAY7", "导入不动显示器");
        Assert(target.ShowWifiName == false, "导入不动本机 ShowWifiName 设置");

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

    private static void TestModuleOrderAndMigration()
    {
        var state = new TopBarState();
        Assert(TopBarRules.IsDefaultOrder(state), "新建顶栏默认顺序");
        var (defaultLeft, defaultRight) = TopBarRules.ResolveOrder(state);
        Assert(defaultLeft.SequenceEqual(TopBarRules.DefaultLeftOrder), "默认左侧顺序");
        Assert(defaultRight.SequenceEqual(TopBarRules.DefaultRightOrder), "默认右侧顺序");

        // 左/右移与跨侧移动
        Assert(!TopBarRules.MoveModule(state, TopBarModuleKind.TodoSummary, -1), "首位不能再左移");
        Assert(TopBarRules.MoveModule(state, TopBarModuleKind.Weather, -1), "天气应能左移到首位");
        var (afterLeftMove, _) = TopBarRules.ResolveOrder(state);
        Assert(afterLeftMove[0] == TopBarModuleKind.Weather && afterLeftMove[1] == TopBarModuleKind.TodoSummary,
            "左移后顺序应更新");

        Assert(TopBarRules.MoveModuleToSide(state, TopBarModuleKind.Clock, TopBarModuleSide.Left),
            "时钟应能移到左侧");
        var (withClockLeft, withoutClockRight) = TopBarRules.ResolveOrder(state);
        Assert(withClockLeft.Contains(TopBarModuleKind.Clock) && !withoutClockRight.Contains(TopBarModuleKind.Clock),
            "跨侧移动不应重复出现");
        Assert(!TopBarRules.MoveModuleToSide(state, TopBarModuleKind.Clock, TopBarModuleSide.Left),
            "已在目标侧时返回 false");
        Assert(!TopBarRules.MoveModule(state, TopBarModuleKind.Search, 1), "搜索入口固定居中，不参与排序");

        // 跳过不可用模块（模拟 Win10 隐藏 TrayOverflow）
        var win10State = new TopBarState();
        Assert(TopBarRules.MoveModule(
                win10State,
                TopBarModuleKind.Volume,
                1,
                kind => kind != TopBarModuleKind.TrayOverflow),
            "跳过不可用模块右移应成功");
        var (_, win10Right) = TopBarRules.ResolveOrder(win10State);
        var win10RightList = win10Right.ToList();
        Assert(win10RightList.IndexOf(TopBarModuleKind.Volume) > win10RightList.IndexOf(TopBarModuleKind.InputMethod),
            "右移应越过不可见的 TrayOverflow 排到输入法之后");

        // 异常数据规范化：去重、忽略 Search 与非法枚举、补齐缺失项
        var messy = new TopBarState
        {
            LeftOrder = [TopBarModuleKind.Clock, TopBarModuleKind.Search, (TopBarModuleKind)999, TopBarModuleKind.Clock],
            RightOrder = [TopBarModuleKind.Clock, TopBarModuleKind.Weather]
        };
        TopBarRules.Normalize(messy);
        var (cleanLeft, cleanRight) = TopBarRules.ResolveOrder(messy);
        Assert(cleanLeft[0] == TopBarModuleKind.Clock && !cleanLeft.Contains(TopBarModuleKind.Search),
            "规范化应保留有效项并剔除 Search 和非法枚举");
        Assert(!cleanRight.Contains(TopBarModuleKind.Clock) && cleanRight[0] == TopBarModuleKind.Weather,
            "重复模块以左侧优先");
        Assert(cleanLeft.Count + cleanRight.Count == TopBarRules.DefaultLeftOrder.Count + TopBarRules.DefaultRightOrder.Count,
            "缺失的可排序模块应自动补齐");

        // 允许清空一侧（非 null 空列表）
        var allRight = new TopBarState();
        foreach (var kind in TopBarRules.DefaultLeftOrder)
        {
            Assert(TopBarRules.MoveModuleToSide(allRight, kind, TopBarModuleSide.Right), $"应允许将 {kind} 移到右侧");
        }
        var (emptyLeft, fullRight) = TopBarRules.ResolveOrder(allRight);
        Assert(emptyLeft.Count == 0 && fullRight.Count == 10, "应允许左侧全部移到右侧");

        // 恢复默认顺序
        Assert(TopBarRules.ResetOrder(state), "自定义顺序应可恢复默认");
        Assert(TopBarRules.IsDefaultOrder(state) && state.LeftOrder is null && state.RightOrder is null,
            "恢复默认后回到空引用默认态");
        Assert(!TopBarRules.ResetOrder(state), "已是默认时再次恢复返回 false");

        // Schema v58 -> v59 迁移与持久化往返
        var folder = Path.Combine(Path.GetTempPath(), $"BingLanTopBarOrderTest-{Guid.NewGuid():N}");
        try
        {
            var store = new LocalStateStore(folder);
            var appState = new AppState { SchemaVersion = 58 };
            appState.TopBar.IsEnabled = true;
            TopBarRules.MoveModuleToSide(appState.TopBar, TopBarModuleKind.Clock, TopBarModuleSide.Left);
            TopBarRules.MoveModule(appState.TopBar, TopBarModuleKind.Weather, -1);
            store.Save(appState);

            var stateFile = Path.Combine(folder, "widgets.json");
            var raw = File.ReadAllText(stateFile);
            File.WriteAllText(stateFile, raw.Replace(
                $"\"SchemaVersion\": {AppState.CurrentSchemaVersion}",
                "\"SchemaVersion\": 58"));

            var reloaded = store.Load();
            Assert(reloaded.SchemaVersion == AppState.CurrentSchemaVersion, "v58 应迁移到当前 SchemaVersion");
            Assert(TopBarRules.OrderSignature(reloaded.TopBar) == TopBarRules.OrderSignature(appState.TopBar),
                "迁移并重载后应保留自定义顶栏模块顺序");

            // 主题包导出与导入保留顺序，老主题包保留本机顺序
            var exported = ThemeRules.Export(reloaded, "排序主题", new ThemeArea(0, 0, 1920, 1040), new Version(0, 4, 2));
            Assert(exported.Bindings.TopBarLeftOrder is not null && exported.Bindings.TopBarLeftOrder.Contains(TopBarModuleKind.Clock),
                "主题包应导出自定义顶栏顺序");
            var importedTarget = new TopBarState();
            ThemeRules.ApplyTopBar(exported, importedTarget);
            Assert(TopBarRules.OrderSignature(importedTarget) == TopBarRules.OrderSignature(reloaded.TopBar),
                "导入主题包应恢复顶栏模块顺序");

            var legacyBindings = new ThemePackage();
            ThemeRules.ApplyTopBar(legacyBindings, importedTarget);
            Assert(TopBarRules.OrderSignature(importedTarget) == TopBarRules.OrderSignature(reloaded.TopBar),
                "不含顺序字段的旧主题包不应覆盖本机模块顺序");
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int MultiByteToWideChar(
        uint codePage,
        uint flags,
        byte[] multiByteStr,
        int multiByteLength,
        [Out] char[]? wideCharStr,
        int wideCharLength);

    private static string? DecodeCodePage936(ReadOnlySpan<byte> bytes)
    {
        var raw = bytes.ToArray();
        var needed = MultiByteToWideChar(936, 0, raw, raw.Length, null, 0);
        if (needed <= 0) return null;
        var chars = new char[needed];
        var written = MultiByteToWideChar(936, 0, raw, raw.Length, chars, chars.Length);
        return written > 0 ? new string(chars, 0, written) : null;
    }
}
