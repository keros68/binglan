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
            [DesktopLayoutPreset.QuietInformation] = [(70,70,380,176), (500,908,820,96), (70,258,380,137), (70,407,380,86), (1550,86,330,410)],
            [DesktopLayoutPreset.GlacierWorkbench] = [(62,70,320,170), (430,902,760,100), (62,252,320,137), (62,401,320,86), (1530,110,350,420)],
            [DesktopLayoutPreset.TransparentApple] = [(70,60,380,170), (510,910,820,96), (70,242,380,137), (70,391,380,86), (1530,500,350,360)],
            [DesktopLayoutPreset.CenterClock] = [(680,56,560,200), (640,890,640,84), (1520,128,370,137), (1520,30,370,86), (1520,277,370,300)]
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
                    $"{preset}/{order[i]} 横屏坐标应与预期一致");
            }
        }

        foreach (var (workWidth, workHeight) in new[] { (1920d, 1040d), (2560d, 1440d) })
        {
            foreach (var preset in Enum.GetValues<DesktopLayoutPreset>())
            {
                var placements = DesktopExperienceRules.PresetPlacements(preset, 0, 0, workWidth, workHeight);
                var column = preset == DesktopLayoutPreset.CenterClock
                    ? new[] { DesktopComponentKind.Performance, DesktopComponentKind.Weather, DesktopComponentKind.Todo }
                    : new[] { DesktopComponentKind.TimeDate, DesktopComponentKind.Weather, DesktopComponentKind.Performance };
                AssertAlignedColumn(
                    column.Select(kind => placements[kind]).ToArray(),
                    $"{preset} ({workWidth}x{workHeight})");
            }
        }

        var defaultColumn = new[]
        {
            DesktopComponentKind.TimeDate,
            DesktopComponentKind.Weather,
            DesktopComponentKind.Performance,
            DesktopComponentKind.Todo,
            DesktopComponentKind.QuickLaunch
        }.Select(DesktopExperienceRules.CreateDefaultComponent).Select(c => c.Placement).ToArray();
        AssertAlignedColumn(defaultColumn, "CreateDefaultComponents");
        Check(Math.Abs((defaultColumn[0].Left + defaultColumn[0].Width + DesktopExperienceRules.PresetColumnGap) - new FileBoxState().Placement.Left) < 0.001d,
            "默认左列右缘与默认分组盒左缘应保持 12 DIP 间距");

        var tiny = DesktopExperienceRules.PresetPlacements(DesktopLayoutPreset.CenterClock, -100, -50, 100, 250);
        Check(tiny.Values.All(card => card.Width > 0 && card.Height > 0
            && card.Left >= -100 && card.Top >= -50 && card.Left + card.Width <= 100
            && card.Top + card.Height <= 250), "极小工作区仍应保留可见卡片");
    }

    // 设置中心“未跟随全局的部分”与组件“独立外观”判断依据：内置卡片的颜色是否等于整体材质给它的颜色。
    internal static void TestComponentLookFollowsSharedMaterial()
    {
        const double clearBar = 0.18;
        var style = new DesktopStyleState();
        var experience = DesktopExperienceRules.CreateDefault();
        foreach (var component in experience.Components)
        {
            Check(DesktopStyleRules.FollowsSharedLook(component, style, clearBar),
                $"默认 {component.Kind} 卡片应视为跟随全局外观");
        }

        var weather = experience.GetComponent(DesktopComponentKind.Weather);
        weather.Appearance.BackgroundColor = "#A0B4E1";
        Check(!DesktopStyleRules.FollowsSharedLook(weather, style, clearBar), "单独改过玻璃颜色的卡片应视为独立外观");
        weather.Appearance.BackgroundColor = "#ffffff";
        Check(DesktopStyleRules.FollowsSharedLook(weather, style, clearBar), "颜色比较不区分大小写");

        var todo = experience.GetComponent(DesktopComponentKind.Todo);
        todo.Appearance.HeaderColor = "#123456";
        Check(!DesktopStyleRules.FollowsSharedLook(todo, style, clearBar), "单独改过待办标题栏颜色也应视为独立外观");
        todo.Appearance.HeaderColor = WidgetAppearanceRules.DefaultHeaderColor;
        Check(DesktopStyleRules.FollowsSharedLook(todo, style, clearBar), "恢复默认标题栏颜色后应回到跟随全局外观");

        style.Glass = GlassMode.Clear;
        var time = DesktopStyleRules.ComponentLook(DesktopComponentKind.TimeDate, style, clearBar);
        var strip = DesktopStyleRules.ComponentLook(DesktopComponentKind.QuickLaunch, style, clearBar);
        Check(time.BackgroundOpacity == WidgetAppearanceRules.MinimumBackgroundOpacity && time.TextColor == "#FFFFFF",
            "时间日期始终是壁纸上的裸文字");
        Check(strip.BackgroundColor == "#FFFFFF" && strip.BackgroundOpacity == clearBar,
            "无底板时快捷入口保留浅色细条");
        Check(!DesktopStyleRules.FollowsSharedLook(weather, style, clearBar), "整体材质变化后未重新应用的卡片不再算跟随");
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
        Check(DesktopStyleRules.Appearance(style).ItemSurfaceColor == "#52FFFFFF"
            && DesktopStyleRules.SubtleControlColor(style) == "#23FFFFFF",
            "全局浅色外观逻辑值保持用户设定的浅色调，不随文字颜色篡改持久配置");
        var lightWhiteRendered = DesktopStyleRules.ResolveRenderedCard(style);
        Check(lightWhiteRendered.BackgroundColor == "#16202C"
            && lightWhiteRendered.ItemSurfaceColor == "#14FFFFFF"
            && lightWhiteRendered.SubtleControlColor == "#35FFFFFF"
            && lightWhiteRendered.ControlColor == "#1CFFFFFF",
            "高密度浅色玻璃搭配白字时，整卡渲染切换为深色玻璃与对应高对比内嵌层");
        style.ItemSurfaceTone = SurfaceTone.Dark;
        style.ItemSurfaceStrength = 0.4;
        Check(DesktopStyleRules.Appearance(style).ItemSurfaceColor == "#661B2635"
            && DesktopStyleRules.SubtleControlColor(style) == "#2C1B2635",
            "内部底色可选深色并调整浓度，按钮和日期格按同一比例变淡");
        style.ItemSurfaceStrength = 5;
        Check(style.ItemSurfaceStrength == DesktopStyleRules.MaximumItemSurfaceStrength, "内部底色浓度应钳制");
        style.Glass = GlassMode.Dark;
        Check(DesktopStyleRules.SubtleControlColor(style) == "#35FFFFFF", "内部底色只作用于浅色玻璃");
        style.Glass = GlassMode.Light;
        style.ItemSurfaceTone = SurfaceTone.Light;
        style.ItemSurfaceStrength = DesktopStyleRules.DefaultItemSurfaceStrength;
        style.TextInk = TextInk.Auto;
        Check(DesktopStyleRules.Appearance(style).ItemSurfaceColor == "#52FFFFFF"
            && DesktopStyleRules.SubtleControlColor(style) == "#23FFFFFF", "浅色玻璃深字保持浅色行底");
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
            Check(!upgraded.Style.GridSnap, "网格对齐应默认关闭");

            File.WriteAllText(store.StatePath, """{ "SchemaVersion": 42 }""");
            var v42 = store.Load();
            Check(v42.SchemaVersion == AppState.CurrentSchemaVersion && !v42.Style.GridSnap,
                "v42 状态迁移后网格对齐应为默认关闭");

            upgraded.Style.CardShadow = CardShadow.Strong;
            upgraded.Style.CardSpacing = 24;
            upgraded.Style.Motion = MotionLevel.Off;
            upgraded.Style.GridSnap = true;
            store.Save(upgraded);
            var reloaded = store.Load();
            Check(reloaded.Style is { CardShadow: CardShadow.Strong, CardSpacing: 24, Motion: MotionLevel.Off, GridSnap: true },
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
        state.Style.GridSnap = true;
        state.Style.Motion = MotionLevel.Reduced;
        state.Style.Glass = GlassMode.Dark;
        state.Style.Palette = "matcha";
        state.Style.GlassDensity = 0.62;
        state.Style.TextInk = TextInk.Custom;
        state.Style.CustomTextColor = "#224433";
        state.Style.AccentColor = "#33AA77";
        state.Style.ItemSurfaceTone = SurfaceTone.Dark;
        state.Style.ItemSurfaceStrength = 0.35;
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
                GridSnap: true,
                Motion: MotionLevel.Reduced,
                TaskbarMaterial: TaskbarMaterial.Blur
            }, "整体风格应往返保存");
        Check(result.Icons is { Count: 1 } icons && icons["icons/0.png"].SequenceEqual(icon), "自定义图标应往返保存");

        var target = new DesktopStyleState();
        var taskbar = new TaskbarState();
        ThemeRules.ApplyStyle(result.Package, target, taskbar);
        Check(target is
            {
                CardShadow: CardShadow.Soft,
                CardSpacing: 8,
                GridSnap: true,
                Motion: MotionLevel.Reduced,
                Glass: GlassMode.Dark,
                Palette: "matcha",
                TextInk: TextInk.Custom,
                CustomTextColor: "#224433",
                AccentColor: "#33AA77",
                ItemSurfaceTone: SurfaceTone.Dark
            }
            && Math.Abs(target.GlassDensity - 0.62) < 1e-6
            && Math.Abs(target.ItemSurfaceStrength - 0.35) < 1e-6
            && taskbar.Mode == TaskbarMode.Blur, "导入应完整应用整体材质、配色、强调色与任务栏材质");

        var legacyStyle = new ThemePackage
        {
            Tokens = new ThemeTokens
            {
                Style = new ThemeStyleTokens { CardShadow = CardShadow.Strong }
            }
        };
        var preserved = new DesktopStyleState
        {
            Glass = GlassMode.Dark,
            Palette = "nord",
            AccentColor = "#FF8844",
            TextInk = TextInk.White
        };
        ThemeRules.ApplyStyle(legacyStyle, preserved, new TaskbarState());
        Check(preserved is { CardShadow: CardShadow.Strong, Glass: GlassMode.Dark, Palette: "nord", AccentColor: "#FF8844", TextInk: TextInk.White },
            "旧版主题缺少新增材质字段时应保留用户原有材质与配色");

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

    internal static void TestBuiltInMaterialReadabilityContrast()
    {
        // Reproduces the previous failures before WP3 readability fixes:
        // 1) White ink over light #7AFFFFFF row on bright wallpaper (~1.18:1)
        // 2) Legacy dark glass (#18222F @ 0.32) over bright wallpaper (< 4.5:1)
        // 3) Legacy light glass (#FFFFFF @ 0.32) secondary ink (0.68) over dark wallpaper (< 3.0:1)
        var oldLightWhiteRow = ColorContrastRules.CompositeLayers(
            ColorContrastRules.BrightWallpaper,
            ColorContrastRules.WithAlpha("#FFFFFF", 0.32d),
            ColorContrastRules.ParseHex("#7AFFFFFF"));
        Check(ColorContrastRules.ContrastRatio("#FFFFFF", "#FFFFFF") == 1d
            && ColorContrastRules.ContrastRatio(ColorContrastRules.ParseHex("#FFFFFF"), oldLightWhiteRow) < 2d,
            "旧浅色白字压浅白行应显著低于可读底线");

        var oldDarkBody = ColorContrastRules.CompositeLayers(
            ColorContrastRules.BrightWallpaper,
            ColorContrastRules.WithAlpha("#18222F", 0.32d));
        Check(ColorContrastRules.ContrastRatio(ColorContrastRules.ParseHex("#FFFFFF"), oldDarkBody) < 4.5d,
            "旧深色玻璃 0.32 浓度在亮壁纸上白字对比度不足");

        var oldLightDarkBody = ColorContrastRules.CompositeLayers(
            ColorContrastRules.DarkWallpaper,
            ColorContrastRules.WithAlpha("#FFFFFF", 0.32d));
        Check(ColorContrastRules.ContrastRatio(
                ColorContrastRules.WithAlpha("#243146", 0.68d), oldLightDarkBody) < 3d,
            "旧浅色玻璃 0.32 在暗壁纸上次要文字对比度不足");

        foreach (var wallpaper in new[] { ColorContrastRules.BrightWallpaper, ColorContrastRules.DarkWallpaper })
        {
            foreach (var palette in DesktopStyleRules.Palettes)
            {
                var style = new DesktopStyleState { Glass = GlassMode.Light, Palette = palette.Key };
                var look = DesktopStyleRules.Appearance(style);
                var bodyBg = ColorContrastRules.CompositeLayers(
                    wallpaper,
                    ColorContrastRules.WithAlpha(look.BackgroundColor, look.BackgroundOpacity));
                var rowBg = ColorContrastRules.CompositeLayers(
                    wallpaper,
                    ColorContrastRules.WithAlpha(look.BackgroundColor, look.BackgroundOpacity),
                    ColorContrastRules.ParseHex(look.ItemSurfaceColor));
                var primary = ColorContrastRules.ParseHex(look.TextColor);
                var secondary = ColorContrastRules.WithAlpha(look.TextColor, ColorContrastRules.SecondaryTextAlpha);

                Check(ColorContrastRules.ContrastRatio(primary, bodyBg) >= ColorContrastRules.MinimumNormalTextContrast,
                    $"{palette.Key} 在 {wallpaper} 下主体正文对比度不足");
                Check(ColorContrastRules.ContrastRatio(secondary, bodyBg) >= ColorContrastRules.MinimumLargeTextContrast,
                    $"{palette.Key} 在 {wallpaper} 下主体次要文字对比度不足");
                Check(ColorContrastRules.ContrastRatio(primary, rowBg) >= ColorContrastRules.MinimumNormalTextContrast,
                    $"{palette.Key} 在 {wallpaper} 下内嵌行正文对比度不足");
                Check(ColorContrastRules.ContrastRatio(secondary, rowBg) >= ColorContrastRules.MinimumLargeTextContrast,
                    $"{palette.Key} 在 {wallpaper} 下内嵌行次要文字对比度不足");
            }

            var darkStyle = new DesktopStyleState { Glass = GlassMode.Dark };
            var darkLook = DesktopStyleRules.Appearance(darkStyle);
            var darkBody = ColorContrastRules.CompositeLayers(
                wallpaper,
                ColorContrastRules.WithAlpha(darkLook.BackgroundColor, darkLook.BackgroundOpacity));
            var darkRow = ColorContrastRules.CompositeLayers(
                wallpaper,
                ColorContrastRules.WithAlpha(darkLook.BackgroundColor, darkLook.BackgroundOpacity),
                ColorContrastRules.ParseHex(darkLook.ItemSurfaceColor));
            var whiteInk = ColorContrastRules.ParseHex(darkLook.TextColor);
            var whiteSecondary = ColorContrastRules.WithAlpha(darkLook.TextColor, ColorContrastRules.SecondaryTextAlpha);

            Check(ColorContrastRules.ContrastRatio(whiteInk, darkBody) >= ColorContrastRules.MinimumNormalTextContrast
                && ColorContrastRules.ContrastRatio(whiteSecondary, darkBody) >= ColorContrastRules.MinimumLargeTextContrast
                && ColorContrastRules.ContrastRatio(whiteInk, darkRow) >= ColorContrastRules.MinimumNormalTextContrast
                && ColorContrastRules.ContrastRatio(whiteSecondary, darkRow) >= ColorContrastRules.MinimumLargeTextContrast,
                $"深色玻璃在 {wallpaper} 下未满足对比度要求");

            var lightWhiteStyle = new DesktopStyleState { Glass = GlassMode.Light, TextInk = TextInk.White };
            var lightWhiteRendered = DesktopStyleRules.ResolveRenderedCard(lightWhiteStyle);
            var renderedBody = ColorContrastRules.CompositeLayers(
                wallpaper,
                ColorContrastRules.WithAlpha(lightWhiteRendered.BackgroundColor, lightWhiteRendered.BackgroundOpacity));
            var renderedRow = ColorContrastRules.CompositeLayers(
                wallpaper,
                ColorContrastRules.WithAlpha(lightWhiteRendered.BackgroundColor, lightWhiteRendered.BackgroundOpacity),
                ColorContrastRules.ParseHex(lightWhiteRendered.ItemSurfaceColor));
            var renderedControl = ColorContrastRules.CompositeLayers(
                wallpaper,
                ColorContrastRules.WithAlpha(lightWhiteRendered.BackgroundColor, lightWhiteRendered.BackgroundOpacity),
                ColorContrastRules.ParseHex(lightWhiteRendered.ControlColor));
            Check(lightWhiteRendered.BackgroundColor == "#16202C"
                && Math.Abs(lightWhiteRendered.BackgroundOpacity - 0.66d) < 0.0001d
                && lightWhiteStyle.Glass == GlassMode.Light
                && ColorContrastRules.ContrastRatio(whiteInk, renderedBody) >= ColorContrastRules.MinimumNormalTextContrast
                && ColorContrastRules.ContrastRatio(whiteSecondary, renderedBody) >= ColorContrastRules.MinimumLargeTextContrast
                && ColorContrastRules.ContrastRatio(whiteInk, renderedRow) >= ColorContrastRules.MinimumNormalTextContrast
                && ColorContrastRules.ContrastRatio(whiteSecondary, renderedRow) >= ColorContrastRules.MinimumLargeTextContrast
                && ColorContrastRules.ContrastRatio(whiteInk, renderedControl) >= ColorContrastRules.MinimumNormalTextContrast,
                $"浅色玻璃配白字（不低于 0.2）在 {wallpaper} 下整卡按深色玻璃渲染后应全区域对比度达标");
        }

        // T1: < 0.2 (near clear) keeps light backing with soft text shadow, dark interior surface <= 0.16, and transparent note body
        Check(Math.Abs(ColorContrastRules.NearClearMaxItemSurfaceStrength - 0.16d) < 0.0001d,
            "接近透明条目底板上限应为 0.16");
        var nearClearLightWhite = new DesktopStyleState
        {
            Glass = GlassMode.Light,
            GlassDensity = 0.05d,
            TextInk = TextInk.White,
            ItemSurfaceTone = SurfaceTone.Light,
            ItemSurfaceStrength = 0.48d
        };
        var nearClearRendered = DesktopStyleRules.ResolveRenderedCard(nearClearLightWhite);
        var nearClearItemAlpha = ColorContrastRules.ParseHex(nearClearRendered.ItemSurfaceColor).A;
        var nearClearControlAlpha = ColorContrastRules.ParseHex(nearClearRendered.ControlColor).A;
        var nearClearSubtleAlpha = ColorContrastRules.ParseHex(nearClearRendered.SubtleControlColor).A;
        Check(nearClearRendered.BackgroundColor == "#FFFFFF"
            && Math.Abs(nearClearRendered.BackgroundOpacity - 0.05d) < 0.0001d
            && nearClearRendered.ItemSurfaceColor == "#281B2635"
            && nearClearRendered.ControlColor == "#281B2635"
            && nearClearItemAlpha <= ColorContrastRules.NearClearMaxItemSurfaceStrength
            && nearClearControlAlpha <= ColorContrastRules.NearClearMaxItemSurfaceStrength
            && nearClearSubtleAlpha > 0d
            && nearClearSubtleAlpha < nearClearItemAlpha
            && nearClearRendered.NoteBodySurfaceColor == "#00FFFFFF"
            && nearClearRendered.EffectiveShadow == CardShadow.Soft
            && nearClearLightWhite.Glass == GlassMode.Light
            && nearClearLightWhite.ItemSurfaceTone == SurfaceTone.Light
            && Math.Abs(nearClearLightWhite.ItemSurfaceStrength - 0.48d) < 0.0001d,
            "低浓度（< 0.2）浅色玻璃配白字应开启柔和文字阴影、<=0.16 深色调条目底板与透明便签正文，同时不改写持久配置");

        // When ItemSurfaceStrength is lower (e.g. 0.10), subtle controls stay proportionally lighter inside the 0.16 ceiling
        var nearClearPerCard = new WidgetAppearanceState
        {
            BackgroundColor = "#EDF6FF",
            BackgroundOpacity = 0.05d,
            TextColor = "#FFFFFF"
        };
        var nearClearCardRendered = DesktopStyleRules.ResolveRenderedCard(nearClearPerCard, nearClearLightWhite);
        Check(nearClearCardRendered.NoteBodySurfaceColor == "#00FFFFFF"
            && ColorContrastRules.ParseHex(nearClearCardRendered.ItemSurfaceColor).A <= ColorContrastRules.NearClearMaxItemSurfaceStrength
            && Math.Abs(nearClearPerCard.BackgroundOpacity - 0.05d) < 0.0001d,
            "单卡低浓度（< 0.2）浅色玻璃配白字时便签正文底色应为透明且条纹底板不超过 0.16");

        // R2/F3: >= 0.2 uses user opacity without clamping; 0.20..0.95 either hits >= 4.5:1 on both wallpapers or enables soft text shadow
        foreach (var textHex in new[] { "#FFFFFF", "#DCE6F2" })
        {
            var ink = ColorContrastRules.ParseHex(textHex);
            var sawShadowTier = false;
            var sawUnshadowedTier = false;
            for (var step = 20; step <= 95; step++)
            {
                var opacity = step / 100d;
                var globalStyle = new DesktopStyleState
                {
                    Glass = GlassMode.Light,
                    GlassDensity = opacity,
                    TextInk = TextInk.Custom,
                    CustomTextColor = textHex,
                    CardShadow = CardShadow.None
                };
                var perCard = new WidgetAppearanceState
                {
                    BackgroundColor = "#FFFFFF",
                    BackgroundOpacity = opacity,
                    TextColor = textHex
                };

                foreach (var (rendered, isGlobal) in new[]
                         {
                             (DesktopStyleRules.ResolveRenderedCard(globalStyle), true),
                             (DesktopStyleRules.ResolveRenderedCard(perCard, new DesktopStyleState { CardShadow = CardShadow.None }), false)
                         })
                {
                    var brightBackdrop = ColorContrastRules.CompositeLayers(
                        ColorContrastRules.BrightWallpaper,
                        ColorContrastRules.WithAlpha(rendered.BackgroundColor, rendered.BackgroundOpacity));
                    var darkBackdrop = ColorContrastRules.CompositeLayers(
                        ColorContrastRules.DarkWallpaper,
                        ColorContrastRules.WithAlpha(rendered.BackgroundColor, rendered.BackgroundOpacity));
                    var brightContrast = ColorContrastRules.ContrastRatio(ink, brightBackdrop);
                    var darkContrast = ColorContrastRules.ContrastRatio(ink, darkBackdrop);
                    var meetsContrast = brightContrast >= ColorContrastRules.MinimumNormalTextContrast
                        && darkContrast >= ColorContrastRules.MinimumNormalTextContrast;
                    var hasShadow = rendered.EffectiveShadow != CardShadow.None;

                    Check(rendered.BackgroundColor == "#16202C"
                        && Math.Abs(rendered.BackgroundOpacity - opacity) < 0.0001d
                        && rendered.NoteBodySurfaceColor == rendered.ItemSurfaceColor
                        && globalStyle.Glass == GlassMode.Light
                        && Math.Abs(globalStyle.GlassDensity - opacity) < 0.0001d
                        && Math.Abs(perCard.BackgroundOpacity - opacity) < 0.0001d,
                        $"浅字({textHex})浅底({opacity:0.00}, {(isGlobal ? "全局" : "单卡")})应按深底渲染并原样保留不透明度");
                    Check(meetsContrast || hasShadow,
                        $"浅字({textHex})浅底({opacity:0.00}, {(isGlobal ? "全局" : "单卡")})对比度不足 4.5:1 时必须启用文字阴影");

                    if (brightContrast < ColorContrastRules.MinimumNormalTextContrast)
                    {
                        Check(rendered.EffectiveShadow == CardShadow.Soft,
                            $"浅字({textHex})浅底({opacity:0.00})亮壁纸对比度 {brightContrast:0.00} < 4.5 时应加柔和文字阴影");
                        sawShadowTier = true;
                    }
                    else
                    {
                        Check(rendered.EffectiveShadow == CardShadow.None,
                            $"浅字({textHex})浅底({opacity:0.00})亮壁纸对比度 {brightContrast:0.00} >= 4.5 时不应强加文字阴影");
                        sawUnshadowedTier = true;
                    }
                }
            }

            Check(sawShadowTier && sawUnshadowedTier,
                $"浅字({textHex})在 0.20..0.95 区间应动态区分需要阴影与无需阴影的档位");
        }

        var defaultDock = DesktopStyleRules.Dock(new DesktopStyleState());
        Check(defaultDock.Surface == "#D1FFFFFF", "提升卡片默认玻璃浓度不应改变 Dock 默认表面不透明度");

        var singleCustomDark = new WidgetAppearanceState
        {
            BackgroundColor = "#16202C",
            BackgroundOpacity = 0.66d,
            TextColor = "#FFFFFF"
        };
        Check(DesktopStyleRules.ResolveCardItemSurfaceColor(singleCustomDark, new DesktopStyleState()) == "#14FFFFFF",
            "单卡单独切为深底白字时应解析出深底高对比行底色");

        var singleCustomWhiteInk = new WidgetAppearanceState
        {
            BackgroundColor = "#FFFFFF",
            BackgroundOpacity = 0.66d,
            TextColor = "#FFFFFF"
        };
        var singleRendered = DesktopStyleRules.ResolveRenderedCard(singleCustomWhiteInk, new DesktopStyleState());
        Check(singleRendered.BackgroundColor == "#16202C"
            && singleRendered.ItemSurfaceColor == "#14FFFFFF"
            && singleRendered.ControlColor == "#1CFFFFFF"
            && singleCustomWhiteInk.BackgroundColor == "#FFFFFF",
            "单卡单独设白字压浅色玻璃时应整卡切为深底高对比渲染且不改写单卡持久底色");

        var quickLightLook = DesktopStyleRules.ComponentLook(
            DesktopComponentKind.QuickLaunch,
            new DesktopStyleState { Glass = GlassMode.Light, CardShadow = CardShadow.None },
            0.18d);
        var quickLightRendered = DesktopStyleRules.ResolveRenderedCard(
            new WidgetAppearanceState
            {
                BackgroundColor = quickLightLook.BackgroundColor,
                BackgroundOpacity = quickLightLook.BackgroundOpacity,
                TextColor = quickLightLook.TextColor
            },
            new DesktopStyleState { CardShadow = CardShadow.None });
        Check(quickLightRendered.EffectiveShadow == CardShadow.None,
            "浅色玻璃默认深色图标在亮底上不应添加多余阴影");

        var quickClearStyle = new DesktopStyleState { Glass = GlassMode.Clear, CardShadow = CardShadow.None };
        var quickClearLook = DesktopStyleRules.ComponentLook(
            DesktopComponentKind.QuickLaunch,
            quickClearStyle,
            0.18d);
        var quickClearRendered = DesktopStyleRules.ResolveRenderedCard(
            new WidgetAppearanceState
            {
                BackgroundColor = quickClearLook.BackgroundColor,
                BackgroundOpacity = quickClearLook.BackgroundOpacity,
                TextColor = quickClearLook.TextColor
            },
            quickClearStyle);
        Check(quickClearRendered.EffectiveShadow == CardShadow.Soft,
            "无底板下快捷入口浅色图标应与无底板文字一致启用柔和阴影");

        var quickClearDarkInk = DesktopStyleRules.ComponentLook(
            DesktopComponentKind.QuickLaunch,
            new DesktopStyleState { Glass = GlassMode.Clear, TextInk = TextInk.Dark, CardShadow = CardShadow.None },
            0.18d);
        var quickClearDarkRendered = DesktopStyleRules.ResolveRenderedCard(
            new WidgetAppearanceState
            {
                BackgroundColor = quickClearDarkInk.BackgroundColor,
                BackgroundOpacity = quickClearDarkInk.BackgroundOpacity,
                TextColor = quickClearDarkInk.TextColor
            },
            new DesktopStyleState { CardShadow = CardShadow.None });
        Check(quickClearDarkRendered.EffectiveShadow == CardShadow.None,
            "无底板配深色图标且关闭阴影时不应强加阴影");

        var nakedWhite = DesktopStyleRules.ResolveRenderedCard(
            new WidgetAppearanceState
            {
                BackgroundColor = "#FFFFFF",
                BackgroundOpacity = WidgetAppearanceRules.MinimumBackgroundOpacity,
                TextColor = "#FFFFFF"
            },
            new DesktopStyleState { CardShadow = CardShadow.None });
        Check(nakedWhite.EffectiveShadow == CardShadow.Soft,
            "普通单卡切到 0.01 无底板配白字时应启用柔和阴影");

        foreach (var nakedKind in new[] { DesktopComponentKind.TimeDate, DesktopComponentKind.Greeting })
        {
            var nakedComponent = DesktopExperienceRules.CreateDefaultComponent(nakedKind);
            Check(
                DesktopStyleRules.ResolveRenderedCard(
                    nakedComponent,
                    new DesktopStyleState { CardShadow = CardShadow.None }).EffectiveShadow == CardShadow.None,
                $"{nakedKind} 自带 XAML 裸文字对比度阴影，无底板分支不应重复返回容器阴影");
            Check(
                DesktopStyleRules.ResolveRenderedCard(
                    nakedComponent,
                    new DesktopStyleState { Glass = GlassMode.Clear, CardShadow = CardShadow.Soft }).EffectiveShadow == CardShadow.None,
                $"{nakedKind} 在无底板模式开启全局阴影时也不应叠加第二层容器阴影");
        }
    }

    internal static void TestSchemaV46LegacyDefaultsMigration()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"binglan-v46-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var store = new LocalStateStore(temp);

            // 1) Legacy v45 default config -> migrated to v46 defaults & follows shared look + transitions to Dark ink.
            File.WriteAllText(
                store.StatePath,
                """
                {
                  "SchemaVersion": 45,
                  "Style": {
                    "Glass": 0,
                    "Palette": "ice",
                    "TextInk": 0,
                    "GlassDensity": 0.32,
                    "ItemSurfaceTone": 0,
                    "ItemSurfaceStrength": 0.48
                  },
                  "TodoWidgets": [
                    {
                      "Title": "待办",
                      "CornerRadius": 15.5,
                      "Appearance": {
                        "BackgroundColor": "#FFFFFF",
                        "BackgroundOpacity": 0.32,
                        "HeaderColor": "#A0B4E1",
                        "HeaderOpacity": 0.97,
                        "TextColor": "#243146"
                      }
                    }
                  ],
                  "FileBoxes": [
                    {
                      "Title": "桌面",
                      "CornerRadius": 15.5,
                      "Appearance": {
                        "BackgroundColor": "#FFFFFF",
                        "BackgroundOpacity": 0.32,
                        "HeaderColor": "#A0B4E1",
                        "HeaderOpacity": 0.97,
                        "TextColor": "#243146"
                      }
                    }
                  ],
                  "NoteWidgets": [
                    {
                      "Title": "便签",
                      "CornerRadius": 15.5,
                      "BodyTypography": { "Color": "#243146" },
                      "Appearance": {
                        "BackgroundColor": "#FFFFFF",
                        "BackgroundOpacity": 0.32,
                        "HeaderColor": "#A0B4E1",
                        "HeaderOpacity": 0.97,
                        "TextColor": "#243146"
                      }
                    }
                  ],
                  "InformationWidgets": [
                    {
                      "CornerRadius": 15.5,
                      "Appearance": {
                        "BackgroundColor": "#FFFFFF",
                        "BackgroundOpacity": 0.32,
                        "HeaderColor": "#A0B4E1",
                        "HeaderOpacity": 0.97,
                        "TextColor": "#243146"
                      }
                    }
                  ],
                  "DesktopExperience": {
                    "ActivePreset": 0,
                    "Components": [
                      { "Kind": 0, "CornerRadius": 15.5, "Appearance": { "BackgroundColor": "#FFFFFF", "BackgroundOpacity": 0.01, "HeaderColor": "#A0B4E1", "HeaderOpacity": 0.97, "TextColor": "#FFFFFF" } },
                      { "Kind": 1, "CornerRadius": 15.5, "Appearance": { "BackgroundColor": "#FFFFFF", "BackgroundOpacity": 0.01, "HeaderColor": "#A0B4E1", "HeaderOpacity": 0.97, "TextColor": "#FFFFFF" } },
                      { "Kind": 2, "CornerRadius": 15.5, "Appearance": { "BackgroundColor": "#FFFFFF", "BackgroundOpacity": 0.32, "HeaderColor": "#A0B4E1", "HeaderOpacity": 0.97, "TextColor": "#243146" } },
                      { "Kind": 3, "CornerRadius": 15.5, "Appearance": { "BackgroundColor": "#FFFFFF", "BackgroundOpacity": 0.32, "HeaderColor": "#A0B4E1", "HeaderOpacity": 0.97, "TextColor": "#243146" } },
                      { "Kind": 4, "CornerRadius": 15.5, "Appearance": { "BackgroundColor": "#FFFFFF", "BackgroundOpacity": 0.32, "HeaderColor": "#A0B4E1", "HeaderOpacity": 0.97, "TextColor": "#243146" } },
                      { "Kind": 5, "CornerRadius": 15.5, "Appearance": { "BackgroundColor": "#FFFFFF", "BackgroundOpacity": 0.32, "HeaderColor": "#A0B4E1", "HeaderOpacity": 0.97, "TextColor": "#243146" } },
                      { "Kind": 6, "CornerRadius": 15.5, "Appearance": { "BackgroundColor": "#FFFFFF", "BackgroundOpacity": 0.32, "HeaderColor": "#A0B4E1", "HeaderOpacity": 0.97, "TextColor": "#243146" } }
                    ]
                  }
                }
                """);
            var migrated = store.Load();
            Check(migrated.SchemaVersion == 46, "v45 状态应迁移到 SchemaVersion 46");
            Check(Math.Abs(migrated.Style.GlassDensity - 0.66d) < 0.0001d
                && Math.Abs(migrated.Style.ItemSurfaceStrength - 0.32d) < 0.0001d,
                "v45 默认玻璃浓度 0.32 与条目强度 0.48 应迁移到 0.66 与 0.32");

            var todo = migrated.TodoWidgets.Single();
            var box = migrated.FileBoxes.Single();
            var note = migrated.NoteWidgets.Single();
            var info = migrated.InformationWidgets.Single();
            foreach (var (radius, app, label) in new (double, WidgetAppearanceState, string)[]
                     {
                         (todo.CornerRadius, todo.Appearance, "待办"),
                         (box.CornerRadius, box.Appearance, "分组盒"),
                         (note.CornerRadius, note.Appearance, "便签"),
                         (info.CornerRadius, info.Appearance, "信息卡")
                     })
            {
                Check(Math.Abs(radius - 12d) < 0.0001d
                    && Math.Abs(app.BackgroundOpacity - 0.66d) < 0.0001d
                    && app.HeaderColor == "#5B9BD5"
                    && app.TextColor == "#1E2B3C"
                    && DesktopStyleRules.FollowsSharedCardLook(app, migrated.Style),
                    $"迁移后默认 {label} 应使用新默认值并被判定为跟随全局外观");
            }
            Check(note.BodyTypography.Color == "#1E2B3C", "便签正文旧默认墨色应同步迁移为 #1E2B3C");
            foreach (var component in migrated.DesktopExperience.Components)
            {
                Check(Math.Abs(component.CornerRadius - 12d) < 0.0001d
                    && DesktopStyleRules.FollowsSharedLook(component, migrated.Style, 0.18d),
                    $"迁移后默认组件 {component.Kind} 应跟随全局外观");
            }

            var darkStyle = new DesktopStyleState { Glass = GlassMode.Dark };
            Check(DesktopStyleRules.ResolveTransitionTextColor(todo.Appearance.TextColor, migrated.Style, darkStyle) == "#FFFFFF",
                "迁移后的卡片从浅色切深色玻璃时，文字应跟随切换为 #FFFFFF");
            foreach (var component in migrated.DesktopExperience.Components)
            {
                Check(DesktopStyleRules.ResolveComponentTransitionTextColor(
                        component.Kind,
                        component.Appearance.TextColor,
                        migrated.Style,
                        darkStyle,
                        0.18d) == "#FFFFFF",
                    $"迁移后的组件 {component.Kind} 从浅色切深色玻璃时，文字应跟随切换为 #FFFFFF");
            }

            store.Save(migrated);
            var migrationBackups = Directory.GetFiles(store.BackupDirectory, "widgets-before-migration-*.json");
            Check(migrationBackups.Length == 1
                && File.ReadAllText(migrationBackups[0]).Contains("\"SchemaVersion\": 45", StringComparison.Ordinal),
                "v45 升级到 v46 后首次保存应在备份目录留存含原 v45 内容的迁移前备份");

            // 2) Remaining 5 light-glass palettes migrate only when paired with their palette body color.
            File.WriteAllText(
                store.StatePath,
                """
                {
                  "SchemaVersion": 45,
                  "Style": { "Glass": 0, "Palette": "milk-tea" },
                  "TodoWidgets": [
                    { "Appearance": { "BackgroundColor": "#F6EBDD", "TextColor": "#4A3728" } },
                    { "Appearance": { "BackgroundColor": "#EEF5E6", "TextColor": "#2F4A2A" } },
                    { "Appearance": { "BackgroundColor": "#ECEFF4", "TextColor": "#2E3440" } },
                    { "Appearance": { "BackgroundColor": "#FBEDEF", "TextColor": "#4E2A33" } },
                    { "Appearance": { "BackgroundColor": "#E8F7F2", "TextColor": "#1F4A40" } },
                    { "Appearance": { "BackgroundColor": "#FFFFFF", "TextColor": "#4A3728" } }
                  ]
                }
                """);
            var palettesState = store.Load();
            Check(palettesState.TodoWidgets[0].Appearance.TextColor == "#2E2118"
                && palettesState.TodoWidgets[1].Appearance.TextColor == "#1F331B"
                && palettesState.TodoWidgets[2].Appearance.TextColor == "#232933"
                && palettesState.TodoWidgets[3].Appearance.TextColor == "#331B22"
                && palettesState.TodoWidgets[4].Appearance.TextColor == "#16352D",
                "五套配色的旧默认文字色应在底色对应时迁往新版深墨色");
            Check(palettesState.TodoWidgets[5].Appearance.TextColor == "#4A3728",
                "跨配色手选的同色值（#4A3728 配 #FFFFFF 底）不应被误替换");

            // 3) Real user shape with customized density/text/radius preserves user fields and migrates only ItemSurfaceStrength 0.48 -> 0.32.
            File.WriteAllText(
                store.StatePath,
                """
                {
                  "SchemaVersion": 45,
                  "Style": {
                    "Glass": 0,
                    "Palette": "ice",
                    "TextInk": 1,
                    "GlassDensity": 0.05,
                    "ItemSurfaceTone": 0,
                    "ItemSurfaceStrength": 0.48
                  },
                  "TodoWidgets": [
                    {
                      "CornerRadius": 7.64,
                      "Appearance": {
                        "BackgroundColor": "#FFFFFF",
                        "BackgroundOpacity": 0.05,
                        "HeaderColor": "#5B9BD5",
                        "HeaderOpacity": 0.97,
                        "TextColor": "#FFFFFF"
                      }
                    }
                  ]
                }
                """);
            var realUser = store.Load();
            var userTodo = realUser.TodoWidgets.Single();
            Check(Math.Abs(realUser.Style.ItemSurfaceStrength - 0.32d) < 0.0001d
                && Math.Abs(realUser.Style.GlassDensity - 0.05d) < 0.0001d
                && realUser.Style.TextInk == TextInk.White
                && Math.Abs(userTodo.CornerRadius - 7.64d) < 0.0001d
                && Math.Abs(userTodo.Appearance.BackgroundOpacity - 0.05d) < 0.0001d
                && userTodo.Appearance.HeaderColor == "#5B9BD5"
                && userTodo.Appearance.TextColor == "#FFFFFF",
                "实测用户形态只应迁移 0.48 -> 0.32，其余自定义浓度/白字/色标/圆角必须原样保留");

            // 4) Reloading SchemaVersion 46 never re-runs v45 -> v46 default migration.
            File.WriteAllText(
                store.StatePath,
                """
                {
                  "SchemaVersion": 46,
                  "Style": { "GlassDensity": 0.32, "ItemSurfaceStrength": 0.48 },
                  "TodoWidgets": [
                    {
                      "CornerRadius": 15.5,
                      "Appearance": {
                        "BackgroundColor": "#FFFFFF",
                        "BackgroundOpacity": 0.32,
                        "HeaderColor": "#A0B4E1",
                        "TextColor": "#243146"
                      }
                    }
                  ]
                }
                """);
            var reloaded = store.Load();
            var explicitTodo = reloaded.TodoWidgets.Single();
            Check(Math.Abs(reloaded.Style.GlassDensity - 0.32d) < 0.0001d
                && Math.Abs(reloaded.Style.ItemSurfaceStrength - 0.48d) < 0.0001d
                && Math.Abs(explicitTodo.CornerRadius - 15.5d) < 0.0001d
                && Math.Abs(explicitTodo.Appearance.BackgroundOpacity - 0.32d) < 0.0001d
                && explicitTodo.Appearance.HeaderColor == "#A0B4E1"
                && explicitTodo.Appearance.TextColor == "#243146",
                "v46 状态再次读取不得重复触发 v45 默认值迁移");
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    internal static void TestDockOpacityFormulaTable()
    {
        static double AlphaOf(string argb) => Convert.ToInt32(argb.Substring(1, 2), 16) / 255d;

        var low = DesktopStyleRules.Dock(new DesktopStyleState { GlassDensity = 0.05d });
        Check(Math.Abs(AlphaOf(low.Surface) - 0.545d) < 0.005d,
            $"浓度 0.05 时 Dock 不透明度应约为 0.545，实际 {AlphaOf(low.Surface):F3}");

        var current = DesktopStyleRules.Dock(new DesktopStyleState { GlassDensity = 0.66d });
        Check(Math.Abs(AlphaOf(current.Surface) - 0.82d) < 0.005d,
            $"浓度 0.66 时 Dock 不透明度应约为 0.82，实际 {AlphaOf(current.Surface):F3}");

        var temp = Path.Combine(Path.GetTempPath(), $"binglan-dock-migrated-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var store = new LocalStateStore(temp);
            File.WriteAllText(store.StatePath, """{ "SchemaVersion": 45, "Style": { "GlassDensity": 0.32 } }""");
            var migrated = store.Load();
            var dockAfterMigration = DesktopStyleRules.Dock(migrated.Style);
            Check(Math.Abs(AlphaOf(dockAfterMigration.Surface) - 0.82d) < 0.005d,
                $"v45 默认状态迁移后 Dock 不透明度应仍约为 0.82，实际 {AlphaOf(dockAfterMigration.Surface):F3}");
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    internal static void TestMaterialSwitchPreservesCustomTextColor()
    {
        var prev = new DesktopStyleState { Glass = GlassMode.Light, Palette = "ice" };
        var next = new DesktopStyleState { Glass = GlassMode.Dark };

        Check(DesktopStyleRules.ResolveTransitionTextColor(WidgetAppearanceRules.DefaultTextColor, prev, next) == "#FFFFFF",
            "未自定义的卡片文字应跟随新材质切换为白色");
        Check(DesktopStyleRules.ResolveTransitionTextColor("#336699", prev, next) == "#336699",
            "用户自定义的卡片文字颜色在切换全局材质后必须保留");

        Check(DesktopStyleRules.ResolveComponentTransitionTextColor(
                DesktopComponentKind.Weather, WidgetAppearanceRules.DefaultTextColor, prev, next, 0.18d) == "#FFFFFF",
            "未自定义的内嵌组件文字应跟随新材质");
        Check(DesktopStyleRules.ResolveComponentTransitionTextColor(
                DesktopComponentKind.Weather, "#AA2244", prev, next, 0.18d) == "#AA2244",
            "用户自定义的天气文字颜色在切换全局材质后必须保留");
        Check(DesktopStyleRules.ResolveComponentTransitionTextColor(
                DesktopComponentKind.TimeDate, "#FFFFFF", prev, next, 0.18d) == "#FFFFFF",
            "默认白色的裸时钟保持白色");
    }

    internal static void TestWeatherIconMapping()
    {
        var wmoCodes = new[]
        {
            0, 1, 2, 3, 45, 48, 51, 53, 55, 56, 57,
            61, 63, 65, 66, 67, 71, 73, 75, 77, 80, 81, 82, 85, 86, 95, 96, 99, -1
        };
        foreach (var code in wmoCodes)
        {
            var condition = WeatherService.DescribeWeatherCode(code);
            var kind = WeatherIconRules.FromCondition(condition);
            var path = WeatherIconRules.GetPathData(kind);
            Check(!string.IsNullOrWhiteSpace(path), $"WMO 天气码 {code}（{condition}）必须映射到非空矢量路径");
        }

        Check(WeatherIconRules.FromCondition("晴") == WeatherIconKind.Clear, "晴天映射");
        Check(WeatherIconRules.FromCondition("大致晴") == WeatherIconKind.Clear, "大致晴映射");
        Check(WeatherIconRules.FromCondition("局部多云") == WeatherIconKind.PartlyCloudy, "局部多云映射");
        Check(WeatherIconRules.FromCondition("阴") == WeatherIconKind.Cloudy, "阴天映射");
        Check(WeatherIconRules.FromCondition("雾") == WeatherIconKind.Fog, "雾天映射");
        Check(WeatherIconRules.FromCondition("毛毛雨") == WeatherIconKind.Rain, "毛毛雨映射");
        Check(WeatherIconRules.FromCondition("冻雨") == WeatherIconKind.Rain, "冻雨映射");
        Check(WeatherIconRules.FromCondition("雪粒") == WeatherIconKind.Snow, "雪粒映射");
        Check(WeatherIconRules.FromCondition("阵雪") == WeatherIconKind.Snow, "阵雪映射");
        Check(WeatherIconRules.FromCondition("雷暴伴冰雹") == WeatherIconKind.Thunderstorm, "雷暴伴冰雹映射");
        Check(WeatherIconRules.FromCondition("未设置") == WeatherIconKind.Unset
            && WeatherIconRules.FromCondition(null) == WeatherIconKind.Unset
            && !string.IsNullOrWhiteSpace(WeatherIconRules.GetPathData((string?)null)),
            "未设置城市与空状态应提供稳定占位矢量图标");
    }

    internal static void TestDefaultRadiusAndHeaderTagDefaults()
    {
        Check(WidgetAppearanceRules.DefaultCornerRadius == 12d, "WP2 默认外圆角应为 12 DIP");
        Check(WidgetAppearanceRules.DefaultHeaderColor == "#5B9BD5", "WP2 默认标题色标应为 #5B9BD5");
        var style = new DesktopStyleState();
        var light = DesktopStyleRules.Appearance(style);
        Check(light.HeaderColor == "#5B9BD5", "浅色玻璃默认标题色标应与 WidgetAppearanceRules 一致");
    }

    internal static void TestWeatherCardMinimumHeightScalesWithFontAndDensity()
    {
        var scales = new[] { 1d, 1.3d, DesktopExperienceRules.MaximumFontScale };
        var densities = new[]
        {
            DesktopInformationDensity.Comfortable,
            DesktopInformationDensity.Compact
        };

        foreach (var density in densities)
        {
            var previousHeight = 0d;
            var padding = DesktopExperienceRules.GetWeatherVerticalPadding(density);
            foreach (var scale in scales)
            {
                var calculated = DesktopExperienceRules.CalculateWeatherMinHeight(scale, density);
                var requiredContentHeight = 16d
                    + padding
                    + 8d
                    + Math.Max(22d, 15d * scale * 1.28d)
                    + 46d * scale * 1.28d
                    + 2d
                    + 12d * scale * 1.28d;
                Check(calculated >= requiredContentHeight,
                    $"天气最小高度（字号 {scale:0.###}，疏密 {density}）计算值 {calculated} 不得小于内容所需高度 {requiredContentHeight:0.##}");
                Check(calculated > previousHeight,
                    $"天气最小高度应随字号单调递增（{density}：{scale:0.###} -> {calculated} <= {previousHeight}）");
                previousHeight = calculated;
            }
        }

        Check(
            Math.Abs(
                DesktopExperienceRules.DefaultWeatherCardHeight
                - DesktopExperienceRules.CalculateWeatherMinHeight(1d, DesktopInformationDensity.Standard)) < 0.01d,
            "天气卡片默认高度应与 1 倍标准疏密最小高度一致");
        Check(
            Math.Abs(DesktopExperienceRules.DefaultPerformanceCardHeight - 86d) < 0.01d,
            "性能卡片默认高度应收紧为 86 DIP");
        Check(DesktopExperienceRules.CalculateWeatherMinHeight(1.296d, DesktopInformationDensity.Standard) > 154d,
            "标准疏密下字号 1.296 的天气最小高度应高于历史默认高度 154");

        var experience = DesktopExperienceRules.CreateDefault();
        experience.GetComponent(DesktopComponentKind.Weather).FontScale = 1.3d;
        experience.GetComponent(DesktopComponentKind.Weather).Density = DesktopInformationDensity.Comfortable;
        var combinedMin = DesktopExperienceRules.CalculateCombinedInformationMinHeight(experience);
        var weatherMin = DesktopExperienceRules.CalculateWeatherMinHeight(1.3d, DesktopInformationDensity.Comfortable);
        Check(combinedMin > weatherMin,
            $"合并信息卡最小高度（{combinedMin}）应大于其中天气区自身最小高度（{weatherMin}）");
    }

    public static void TestWp6CardMotionRules()
    {
        Check(
            CardMotionRules.FoldDuration.TotalMilliseconds >= CardMotionRules.MinimumDurationMs &&
            CardMotionRules.FoldDuration.TotalMilliseconds <= CardMotionRules.MaximumDurationMs,
            $"折叠过渡时长（{CardMotionRules.FoldDuration.TotalMilliseconds} ms）应落在 {CardMotionRules.MinimumDurationMs}–{CardMotionRules.MaximumDurationMs} ms 区间内");
        Check(
            CardMotionRules.ItemDuration.TotalMilliseconds >= CardMotionRules.MinimumDurationMs &&
            CardMotionRules.ItemDuration.TotalMilliseconds <= CardMotionRules.MaximumDurationMs,
            $"条目增删过渡时长（{CardMotionRules.ItemDuration.TotalMilliseconds} ms）应落在 {CardMotionRules.MinimumDurationMs}–{CardMotionRules.MaximumDurationMs} ms 区间内");
        Check(CardMotionRules.IsAnimated(true), "系统启用动画效果时应允许卡片过渡");
        Check(!CardMotionRules.IsAnimated(false), "系统关闭动画效果时应禁用卡片过渡");
        Check(
            CardMotionRules.ResolveFoldDuration(true) == CardMotionRules.FoldDuration &&
            CardMotionRules.ResolveFoldDuration(false) == TimeSpan.Zero,
            "折叠过渡时长应仅在系统启用动画时非零");
        Check(
            CardMotionRules.ResolveItemDuration(true) == CardMotionRules.ItemDuration &&
            CardMotionRules.ResolveItemDuration(false) == TimeSpan.Zero,
            "条目增删过渡时长应仅在系统启用动画时非零");
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

    private static void AssertAlignedColumn(IReadOnlyList<WindowPlacement> cards, string label)
    {
        var left = cards[0].Left;
        var width = cards[0].Width;
        var right = left + width;
        for (var i = 0; i < cards.Count; i++)
        {
            Check(Math.Abs(cards[i].Left - left) < 0.001d
                && Math.Abs(cards[i].Width - width) < 0.001d
                && Math.Abs((cards[i].Left + cards[i].Width) - right) < 0.001d,
                $"{label} 列内第 {i + 1} 张卡片左右边缘或宽度不一致");
            if (i > 0)
            {
                var gap = cards[i].Top - (cards[i - 1].Top + cards[i - 1].Height);
                Check(Math.Abs(gap - DesktopExperienceRules.PresetColumnGap) < 0.001d,
                    $"{label} 列内第 {i} 与第 {i + 1} 张卡片纵向间距应为 {DesktopExperienceRules.PresetColumnGap}，实际 {gap:0.##}");
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
