using System.IO;
using System.IO.Compression;
using System.Text;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.Themes;

/// <summary>Desktop-wide style, custom dock icons and manifest checks in theme packages.</summary>
internal static class ThemeStyleTests
{
    private const string IconFile = "0123456789abcdef0123456789abcdef.png";
    private static readonly Version AppVersion = new(0, 1, 0);
    private static readonly ThemeArea Area = new(0, 0, 1920, 1040);

    internal static void TestPortraitPresets()
    {
        foreach (var preset in Enum.GetValues<DesktopLayoutPreset>())
        foreach (var (width, height) in new[] { (720d, 1280d), (864d, 1536d), (1080d, 1920d) })
        foreach (var (left, top) in new[] { (0d, 0d), (-1080d, -240d) })
        {
            var cards = DesktopExperienceRules.PresetPlacements(preset, left, top, left + width, top + height);
            Check(cards.Count == 5, "竖屏预设应保留五张卡片");
            foreach (var (kind, card) in cards)
            {
                var minimum = kind == DesktopComponentKind.Todo ? (226d, 166d) : (180d, 72d);
                Check(card.Width >= minimum.Item1 && card.Height >= minimum.Item2,
                    $"{preset}/{kind} 应满足最小尺寸");
                Check(card.Left >= left && card.Top >= top
                    && card.Left + card.Width <= left + width + 0.001
                    && card.Top + card.Height <= top + height + 0.001,
                    $"{preset}/{kind} 应位于传入的竖屏工作区内");
            }
            var values = cards.Values.ToArray();
            for (var i = 0; i < values.Length; i++)
            for (var j = i + 1; j < values.Length; j++)
            {
                var a = values[i];
                var b = values[j];
                Check(a.Left + a.Width <= b.Left || b.Left + b.Width <= a.Left
                    || a.Top + a.Height <= b.Top + 0.001 || b.Top + b.Height <= a.Top + 0.001,
                    $"{preset} 竖屏卡片不应重叠");
            }
        }

        // Representative coordinates lock the existing landscape compositions.
        var expected = new Dictionary<DesktopLayoutPreset, (double X, double Y, double W, double H)[]>
        {
            [DesktopLayoutPreset.QuietInformation] = [(70,70,410,176), (500,908,820,96), (70,260,360,150), (70,424,380,126), (1550,86,330,410)],
            [DesktopLayoutPreset.GlacierWorkbench] = [(62,70,320,170), (430,902,760,100), (62,250,320,150), (62,410,320,136), (1530,110,350,420)],
            [DesktopLayoutPreset.TransparentApple] = [(70,60,410,170), (510,910,820,96), (70,248,360,150), (70,414,380,126), (1530,500,350,360)],
            [DesktopLayoutPreset.CenterClock] = [(680,56,560,200), (640,890,640,84), (1520,172,370,150), (1520,30,370,126), (1520,338,370,300)]
        };
        var order = new[] { DesktopComponentKind.TimeDate, DesktopComponentKind.Greeting,
            DesktopComponentKind.Weather, DesktopComponentKind.Performance, DesktopComponentKind.Todo };
        foreach (var (preset, coordinates) in expected)
        {
            var actual = DesktopExperienceRules.PresetPlacements(preset, 0, 0, 1920, 1040);
            for (var i = 0; i < order.Length; i++)
            {
                var card = actual[order[i]];
                Check((card.Left, card.Top, card.Width, card.Height) == coordinates[i],
                    $"{preset}/{order[i]} 横屏坐标应保持不变");
            }
        }

        var tiny = DesktopExperienceRules.PresetPlacements(DesktopLayoutPreset.CenterClock, -100, -50, 100, 250);
        Check(tiny.Values.All(card => card.Width > 0 && card.Height > 0
            && card.Left >= -100 && card.Top >= -50 && card.Left + card.Width <= 100
            && card.Top + card.Height <= 250), "极小工作区仍应保留可见卡片");
    }

    internal static void TestGlassAndCenterClockPreset()
    {
        var style = new DesktopStyleState();
        var light = DesktopStyleRules.Appearance(style);
        Check(light.BackgroundColor == WidgetAppearanceRules.DefaultBackgroundColor
            && light.BackgroundOpacity == WidgetAppearanceRules.DefaultBackgroundOpacity
            && light.HeaderOpacity == WidgetAppearanceRules.DefaultHeaderOpacity
            && light.TextColor == WidgetAppearanceRules.DefaultTextColor,
            "默认浅色玻璃应与原来的卡片外观一致");

        style.Glass = GlassMode.Dark;
        style.GlassDensity = 0.6;
        var dark = DesktopStyleRules.Appearance(style);
        Check(dark.TextColor == "#FFFFFF" && dark.BackgroundOpacity == 0.6, "深色玻璃应为白字并使用所选浓度");

        style.Glass = GlassMode.Clear;
        var clear = DesktopStyleRules.Appearance(style);
        Check(clear.BackgroundOpacity == WidgetAppearanceRules.MinimumBackgroundOpacity
            && clear.BackgroundOpacity > 0 && clear.HeaderOpacity == 0 && clear.TextColor == "#FFFFFF"
            && clear.ItemSurfaceColor == "#00FFFFFF",
            "无底板应只剩文字，主体保留不可见但可点击的最低不透明度");
        style.TextInk = TextInk.Dark;
        Check(DesktopStyleRules.Appearance(style).TextColor == WidgetAppearanceRules.DefaultTextColor
            && DesktopStyleRules.NakedTextColor(style) == WidgetAppearanceRules.DefaultTextColor,
            "浅色壁纸可选深色文字");
        style.TextInk = TextInk.Custom;
        style.CustomTextColor = "#FFD27F";
        Check(DesktopStyleRules.Appearance(style).TextColor == "#FFD27F"
            && DesktopStyleRules.NakedTextColor(style) == "#FFD27F", "自定义文字颜色应用到卡片和裸文字");
        style.CustomTextColor = "not a colour";
        Check(style.CustomTextColor == "#FFFFFF", "无效的自定义颜色应回到白色");
        style.Glass = GlassMode.Light;
        style.TextInk = TextInk.White;
        Check(DesktopStyleRules.Appearance(style).TextColor == "#FFFFFF", "文字颜色独立于外观：浅色玻璃也可用白字");
        style.TextInk = TextInk.Auto;
        Check(DesktopStyleRules.Appearance(style).TextColor == WidgetAppearanceRules.DefaultTextColor
            && DesktopStyleRules.NakedTextColor(style) == "#FFFFFF",
            "跟随外观时浅色玻璃为深字，壁纸上的时间和问候仍为白字");
        style.Glass = GlassMode.Clear;
        style.TextInk = TextInk.Dark;

        style.GlassDensity = 5;
        Check(style.GlassDensity == DesktopStyleRules.MaximumGlassDensity, "玻璃浓度应钳制");

        var dockStyle = new DesktopStyleState();
        Check(DesktopStyleRules.Dock(dockStyle).Surface == "#D1FFFFFF", "默认 Dock 为冰蓝配色的浅色玻璃");
        dockStyle.Palette = "matcha";
        Check(DesktopStyleRules.Dock(dockStyle).Surface.EndsWith("EEF5E6", StringComparison.Ordinal), "Dock 应跟随所选配色");
        dockStyle.Glass = GlassMode.Dark;
        Check(DesktopStyleRules.Dock(dockStyle).RunningDot == "#E6FFFFFF", "深色 Dock 的运行点应为白色");
        dockStyle.Glass = GlassMode.Clear;
        var clearDock = DesktopStyleRules.Dock(dockStyle);
        Check(clearDock.Surface == "#01FFFFFF" && clearDock.Border == "#00FFFFFF",
            "无底板时 Dock 只留图标，底色保留最低不透明度以便点击空隙");

        var ownDock = new DockState { FollowCardLook = false, SurfaceColor = "#1E2A3A", SurfaceOpacity = 0.7 };
        var own = DesktopStyleRules.Dock(dockStyle, ownDock);
        Check(own.Surface == "#B21E2A3A" && own.RunningDot == "#E6FFFFFF", "自定义深色 Dock 应用所选颜色、不透明度和白色运行点");
        ownDock.SurfaceColor = "#FFFFFF";
        Check(DesktopStyleRules.Dock(dockStyle, ownDock).RunningDot == "#C21F3A55", "浅色且较不透明的 Dock 运行点应为深色");
        ownDock.SurfaceOpacity = 0;
        Check(ownDock.SurfaceOpacity == DesktopStyleRules.MinimumGlassDensity, "自定义玻璃浓度下限应与全局玻璃浓度一致（5%）");
        var clearOwn = DesktopStyleRules.Dock(dockStyle, ownDock);
        Check(clearOwn.Surface == "#0DFFFFFF", "玻璃浓度下限 5% 只留淡淡的底色以便点击空隙");
        ownDock.SurfaceOpacity = 1;
        Check(ownDock.SurfaceOpacity == DesktopStyleRules.MaximumGlassDensity, "自定义玻璃浓度上限应与全局玻璃浓度一致（96%）");
        ownDock.FollowCardLook = true;
        Check(DesktopStyleRules.Dock(dockStyle, ownDock) == DesktopStyleRules.Dock(dockStyle), "跟随卡片时忽略 Dock 自己的颜色");
        var invalid = new DockState { SurfaceColor = "red", SurfaceOpacity = double.NaN };
        BingLan.Core.Dock.DockPinRules.Normalize(invalid);
        Check(invalid.SurfaceColor == DockState.DefaultSurfaceColor && invalid.SurfaceOpacity == DockState.DefaultSurfaceOpacity,
            "无效的 Dock 颜色和不透明度应回到默认值");

        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-glass-{Guid.NewGuid():N}");
        try
        {
            var store = new LocalStateStore(temp);
            var state = LocalStateStore.CreateDefault();
            state.Style.Glass = GlassMode.Clear;
            state.Style.TextInk = TextInk.Dark;
            state.Style.GlassDensity = 0.5;
            store.Save(state);
            var reloaded = store.Load().Style;
            Check(reloaded is { Glass: GlassMode.Clear, TextInk: TextInk.Dark, GlassDensity: 0.5 }, "卡片外观应保存");
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, true);
            }
        }

        var placements = DesktopExperienceRules.PresetPlacements(DesktopLayoutPreset.CenterClock, 0, 0, 2048, 1000);
        var clock = placements[DesktopComponentKind.TimeDate];
        Check(Math.Abs(clock.Left + clock.Width / 2 - 1024) < 1, "居中时钟应水平居中");
        Check(placements.Values.All(p => p.Left >= 0 && p.Top >= 0 && p.Left + p.Width <= 2048 && p.Top + p.Height <= 1000),
            "居中时钟的卡片都应在工作区内");
        var column = new[] { DesktopComponentKind.Performance, DesktopComponentKind.Weather, DesktopComponentKind.Todo }
            .Select(kind => placements[kind]).ToArray();
        Check(column.Select(p => p.Left).Distinct().Count() == 1 && column[0].Top < column[1].Top && column[1].Top < column[2].Top,
            "性能、天气、待办应在右侧排成一列");
        Check(DesktopExperienceRules.GetPresetName(DesktopLayoutPreset.CenterClock) == "居中时钟", "预设名称");
    }

    internal static void TestTaskbarCornerStrip()
    {
        var monitor = new BingLan.Core.Dock.PixelRect(0, 0, 2560, 1440);
        var strip = BingLan.Core.Taskbar.TaskbarCornerRules.MiddleStrip(monitor, 120);
        Check(strip.Left == 384 && strip.Right == 2176 && strip.Top == 1438 && strip.Bottom == 1440,
            "底边细条应留出两端各 15% 并贴住最下 2 像素");
        var narrow = BingLan.Core.Taskbar.TaskbarCornerRules.MiddleStrip(new BingLan.Core.Dock.PixelRect(-1024, 0, 0, 768), 96);
        Check(narrow.Left == -864 && narrow.Right == -160, "窄屏两端至少留 160 DIP，负坐标的副屏也正确");
        var tiny = BingLan.Core.Taskbar.TaskbarCornerRules.MiddleStrip(new BingLan.Core.Dock.PixelRect(0, 0, 200, 100), 96);
        Check(tiny.Width == 0, "屏幕太窄时不挡任何位置");
    }

    internal static void TestStyleMigrationAndLimits()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-style-{Guid.NewGuid():N}");
        try
        {
            var store = new LocalStateStore(temp);
            Directory.CreateDirectory(temp);
            File.WriteAllText(store.StatePath, """{ "SchemaVersion": 16 }""");
            var upgraded = store.Load();
            Check(upgraded.SchemaVersion == AppState.CurrentSchemaVersion
                && upgraded.Style is { CardShadow: CardShadow.None, Motion: MotionLevel.Standard }
                && upgraded.Style.CardSpacing == DesktopStyleRules.DefaultCardSpacing,
                "升级后整体风格应为默认值");

            upgraded.Style.CardShadow = CardShadow.Strong;
            upgraded.Style.CardSpacing = 24;
            upgraded.Style.Motion = MotionLevel.Off;
            store.Save(upgraded);
            var reloaded = store.Load();
            Check(reloaded.Style is { CardShadow: CardShadow.Strong, CardSpacing: 24, Motion: MotionLevel.Off },
                "整体风格应保存");
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, true);
            }
        }

        Check(DesktopStyleRules.CoerceCardSpacing(500) == DesktopStyleRules.MaximumCardSpacing, "间距应有上限");
        Check(DesktopStyleRules.CoerceCardSpacing(double.NaN) == DesktopStyleRules.DefaultCardSpacing, "无效间距应恢复默认");
        Check(DesktopStyleRules.HoverZoom(MotionLevel.Standard, systemAnimationsEnabled: false) == 1d,
            "系统关闭动画时不应放大");
        Check(DesktopStyleRules.HoverZoom(MotionLevel.Off, true) == 1d, "动效关闭时不应放大");
        Check(DesktopStyleRules.HoverZoom(MotionLevel.Reduced, true) < DesktopStyleRules.HoverZoom(MotionLevel.Standard, true),
            "减弱动效应小于标准");
    }

    internal static void TestStyleAndIconsRoundTrip()
    {
        var state = LocalStateStore.CreateDefault();
        state.Style.CardShadow = CardShadow.Soft;
        state.Style.CardSpacing = 8;
        state.Style.Motion = MotionLevel.Reduced;
        state.Taskbar.Mode = TaskbarMode.Blur;
        state.Dock.PinnedApps.Add(new DockPinnedApp
        {
            DisplayName = "浏览器",
            ExecutablePath = @"C:\Apps\msedge.exe",
            IconFile = IconFile
        });
        state.Dock.PinnedApps.Add(new DockPinnedApp { DisplayName = "音乐", ExecutablePath = @"C:\Apps\music.exe" });

        var package = ThemeRules.Export(state, "风格主题", Area, AppVersion);
        Check(package.Bindings.Apps[0].IconEntry == "icons/0.png" && package.Bindings.Apps[1].IconEntry is null,
            "只有自定义图标的应用应带图标条目");

        var icon = ThemePreviewTests.BuildMinimalPng();
        using var stream = new MemoryStream();
        ThemeArchive.Write(stream, package, icons: new Dictionary<string, byte[]> { ["icons/0.png"] = icon });
        stream.Position = 0;
        var result = ThemeArchive.Read(stream, AppVersion);
        Check(result.Succeeded, $"主题包应可读取：{result.Error}");
        Check(result.Package!.Tokens.Style is
            {
                CardShadow: CardShadow.Soft,
                CardSpacing: 8,
                Motion: MotionLevel.Reduced,
                TaskbarMaterial: TaskbarMaterial.Blur
            }, "整体风格应往返保存");
        Check(result.Icons is { Count: 1 } icons && icons["icons/0.png"].SequenceEqual(icon), "自定义图标应往返保存");

        var target = new DesktopStyleState();
        var taskbar = new TaskbarState();
        ThemeRules.ApplyStyle(result.Package, target, taskbar);
        Check(target is { CardShadow: CardShadow.Soft, CardSpacing: 8, Motion: MotionLevel.Reduced }
            && taskbar.Mode == TaskbarMode.Blur, "导入应应用整体风格和任务栏材质");

        var hidden = new TaskbarState { Mode = TaskbarMode.SmartHide };
        ThemeRules.ApplyStyle(result.Package, new DesktopStyleState(), hidden);
        Check(hidden.Mode == TaskbarMode.SmartHide, "任务栏自动隐藏时不应被主题改变");

        var resolved = ThemeRules.ResolveBindings(result.Package.Bindings, (binding, executable) =>
            executable is null ? null : new DockPinnedApp { DisplayName = binding.DisplayName, ExecutablePath = $@"D:\{executable}" });
        Check(resolved.ResolvedIconEntries.SequenceEqual(["icons/0.png", null]), "解析结果应带回图标条目");

        var older = new ThemePackage();
        var unchanged = new DesktopStyleState { CardShadow = CardShadow.Strong };
        ThemeRules.ApplyStyle(older, unchanged, new TaskbarState());
        Check(unchanged.CardShadow == CardShadow.Strong, "不含整体风格的旧主题包不应改变风格");
    }

    internal static void TestUnsafeIconsAreDropped()
    {
        Check(ThemeRules.IsIconFileName(IconFile), "生成的图标文件名应被接受");
        Check(!ThemeRules.IsIconFileName(@"..\evil.png") && !ThemeRules.IsIconFileName("C:/x.png"),
            "路径形式的图标文件名应被拒绝");
        Check(ThemeRules.IsIconEntry("icons/3.png") && !ThemeRules.IsIconEntry("icons/../x.png")
            && !ThemeRules.IsIconEntry("icons/a.png"), "图标条目只能是 icons/数字.png");

        var package = new ThemePackage();
        package.Manifest.Name = "图标测试";
        package.Bindings.Apps.Add(new ThemeAppBinding { Slot = "browser", ExecutableName = "a.exe", IconEntry = "icons/0.png" });
        package.Bindings.Apps.Add(new ThemeAppBinding { Slot = "music", ExecutableName = "b.exe", IconEntry = "icons/1.png" });
        using var stream = new MemoryStream();
        ThemeArchive.Write(stream, package, icons: new Dictionary<string, byte[]>
        {
            ["icons/0.png"] = Encoding.UTF8.GetBytes("not a png"),
            ["icons/1.png"] = ThemePreviewTests.BuildMinimalPng()
        });
        stream.Position = 0;
        var result = ThemeArchive.Read(stream, AppVersion);
        Check(result.Succeeded && result.Package!.Bindings.Apps[0].IconEntry is null
            && result.Package.Bindings.Apps[1].IconEntry == "icons/1.png"
            && result.Icons!.Count == 1, "无效图标应被丢弃，有效图标保留");
    }

    internal static void TestManifestIsValidated()
    {
        Check(!Read(Manifest(id: "../bad")).Succeeded, "非法主题 ID 应被拒绝");
        Check(!Read(Manifest(name: "")).Succeeded, "缺少名称应被拒绝");
        Check(!Read(Manifest(themeVersion: "abc")).Succeeded, "无效主题版本应被拒绝");
        Check(!Read(Manifest(minimum: "")).Succeeded, "缺少最低应用版本应被拒绝");
        Check(Read(Manifest()).Succeeded, "完整清单应被接受");
    }

    private static ThemeReadResult Read(string manifest)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in new[] { ("manifest.json", manifest), ("tokens.json", "{}"), ("layout.json", "{}") })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }
        stream.Position = 0;
        return ThemeArchive.Read(stream, AppVersion);
    }

    private static string Manifest(
        string id = "theme-1",
        string name = "测试",
        string themeVersion = "1.0.0",
        string minimum = "0.1.0") =>
        $$"""{ "Format": "binglan-theme", "SchemaVersion": 1, "Id": "{{id}}", "Name": "{{name}}", "ThemeVersion": "{{themeVersion}}", "MinimumAppVersion": "{{minimum}}" }""";

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
