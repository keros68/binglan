using BingLan.Core.Dock;
using BingLan.Core.Models;

namespace BingLan.SmokeTests;

internal static class LauncherIdentityTests
{
    public static void Run()
    {
        TestChromeAndProfileGrouping();
        TestAumidAndOneNoteVariants();
        TestFileAndAppSeparation();
        TestPreferDisplayName();
        TestArgumentCaseAndEmptyFallback();
        TestStripBenignArguments();
        TestFindLaunchVariantFamilies();
        TestReadabilityMatrix();
        TestAppearancePreservesIceBlueStyle();
        TestFavoritePreferencesAndFileSize();
        TestPinCopy();
    }

    private static void TestChromeAndProfileGrouping()
    {
        var chromeExe = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        var entries = new[]
        {
            new LauncherIdentityInput(chromeExe, null, null, chromeExe, null, "Google Chrome"),
            new LauncherIdentityInput(chromeExe, null, null, chromeExe, "--source-shortcut-location=start-menu", "Google Chrome"),
            new LauncherIdentityInput(chromeExe, null, null, chromeExe, "", "Google Chrome"),
            new LauncherIdentityInput(chromeExe, null, null, chromeExe, "--profile-directory=\"Profile 1\"", "工作 - Chrome")
        };

        var groups = LauncherIdentityRules.Group(entries, item => item);
        Check(groups.Count == 2, $"Chrome 三个普通来源应合并为 1 组，多账号单独 1 组，实际 {groups.Count} 组");
        Check(groups[0].Count == 3 && groups[0][0].Arguments is null, "Chrome 第一组应合并三个来源并保持首次出现顺序");
        Check(groups[1].Count == 1 && groups[1][0].DisplayName == "工作 - Chrome", "多账号配置应独立保留");
    }

    private static void TestAumidAndOneNoteVariants()
    {
        var edgeExe = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
        var withAumid = new LauncherIdentityInput(edgeExe, "MSEdge", null, edgeExe, null, "Microsoft Edge");
        var startMenuEdge = new LauncherIdentityInput(edgeExe, null, null, edgeExe, "--source-shortcut-location=start-menu", "Microsoft Edge");
        var edgeGroups = LauncherIdentityRules.Group([withAumid, startMenuEdge], item => item);
        Check(edgeGroups.Count == 1 && edgeGroups[0].Count == 2, "带 AUMID 且带 exe 的 Dock 项应与开始菜单同 exe 项合并");

        var oneNoteExe = @"C:\Program Files\Microsoft Office\root\Office16\ONENOTE.EXE";
        var oneNote = new LauncherIdentityInput(oneNoteExe, null, null, oneNoteExe, null, "OneNote");
        var sticky = new LauncherIdentityInput(oneNoteExe, null, null, oneNoteExe, "/memoryWindow start", "Sticky Notes (new)");
        var officeGroups = LauncherIdentityRules.Group([oneNote, sticky], item => item);
        Check(officeGroups.Count == 2, "OneNote 与 Sticky Notes (new) 参数不同应保持为 2 组");
    }

    private static void TestFileAndAppSeparation()
    {
        var fileLower = new LauncherIdentityInput(null, null, null, @"c:\tools\demo.exe", null, "demo.exe");
        var fileUpper = new LauncherIdentityInput(null, null, null, @"C:\Tools\DEMO.EXE", null, "demo.exe");
        var appEntry = new LauncherIdentityInput(@"C:\Tools\demo.exe", null, null, @"C:\Tools\demo.exe", null, "demo.exe");

        var groups = LauncherIdentityRules.Group([fileLower, fileUpper, appEntry], item => item);
        Check(groups.Count == 2 && groups[0].Count == 2 && groups[1].Count == 1,
            "大小写不同的同路径文件应合并，但 path: 文件与 exe: 应用即使同路径也不得合并");
    }

    private static void TestPreferDisplayName()
    {
        var dingExe = @"D:\App\GRP\DingtalkLauncher.exe";
        var dockItem = new LauncherIdentityInput(dingExe, null, null, dingExe, null, "DingtalkLauncher");
        var shortcutItem = new LauncherIdentityInput(dingExe, null, null, dingExe, null, "钉钉");

        var groups = LauncherIdentityRules.Group([dockItem, shortcutItem], item => item);
        Check(groups.Count == 1 && groups[0].Count == 2, "DingtalkLauncher 与 钉钉 同 exe 且无参数应合并为 1 组");
        Check(LauncherIdentityRules.PreferDisplayName("DingtalkLauncher", dingExe, ["钉钉"]) == "钉钉",
            "主名称与 exe 文件名一致时应优先采用快捷方式友好名称");
        Check(LauncherIdentityRules.PreferDisplayName("微信", @"D:\App\Weixin\Weixin.exe", ["WeChat"]) == "微信",
            "主名称已是友好名称时应保持原样");
    }

    private static void TestArgumentCaseAndEmptyFallback()
    {
        var exe = @"C:\Tools\app.exe";
        var lower = new LauncherIdentityInput(exe, null, null, exe, "--profile work", "SameName");
        var upper = new LauncherIdentityInput(exe, null, null, exe, "--profile Work", "SameName");
        var caseGroups = LauncherIdentityRules.Group([lower, upper], item => item);
        Check(caseGroups.Count == 2, "同 exe 同名但非空参数大小写不同不得合并");

        var noArgs = new LauncherIdentityInput(exe, null, null, exe, null, "SameName");
        var withArgs = new LauncherIdentityInput(exe, null, null, exe, "/x", "SameName");
        Check(LauncherIdentityRules.Group([noArgs, withArgs], item => item).Count == 1,
            "同 exe 同名时无参数项与带参数项应合并");
        Check(LauncherIdentityRules.Group([withArgs, noArgs], item => item).Count == 1,
            "同 exe 同名时带参数项在先也应与无参数项合并");
    }

    private static void TestStripBenignArguments()
    {
        Check(LauncherIdentityRules.StripBenignArguments("--source-shortcut-location=taskbar") == string.Empty,
            "单独的无害来源参数应被剥离为空串");
        Check(LauncherIdentityRules.StripBenignArguments("--profile-directory=\"Profile 1\" --source-shortcut-location=start-menu")
            == "--profile-directory=\"Profile 1\"",
            "剥离无害来源参数时应保留带空格引号的配置文件参数");
    }

    private static void TestFindLaunchVariantFamilies()
    {
        var exeTarget = "exe:C:\\TOOLS\\RUNCMDU.EXE";
        var rows = new (string RowKey, string TargetKey, string? Folder, bool HasArguments)[]
        {
            ("r1", exeTarget, "smartmontools", true),
            ("r2", exeTarget, "smartmontools", true),
            ("r3", exeTarget, "smartmontools", true),
            ("r4", "exe:C:\\TOOLS\\TWO.EXE", "smartmontools", true),
            ("r5", "exe:C:\\TOOLS\\TWO.EXE", "smartmontools", true),
            ("r6", "exe:C:\\TOOLS\\SPLIT.EXE", "folderA", true),
            ("r7", "exe:C:\\TOOLS\\SPLIT.EXE", "folderB", true),
            ("r8", "exe:C:\\TOOLS\\SPLIT.EXE", "folderC", true)
        };

        var result = LauncherIdentityRules.FindLaunchVariantFamilies(rows);
        Check(result.SetEquals(["r1", "r2", "r3"]), "同 exe 同文件夹达到 3 个带参数项应命中，不足 3 项或分散目录不应命中");
    }

    private static void TestReadabilityMatrix()
    {
        var colors = new[] { "#D8F3FF", "#18222F", "#FFFFFF", "#000000", "#808080" };
        var opacities = new[] { 0.05d, 0.12d, 0.30d, 0.46d, 0.66d, 0.96d };
        var black = new ColorContrastRules.SrgbColor(0d, 0d, 0d, 1d);
        var white = new ColorContrastRules.SrgbColor(1d, 1d, 1d, 1d);

        foreach (var color in colors)
        {
            var surface = ColorContrastRules.ParseHex(color);
            foreach (var requested in opacities)
            {
                var result = LauncherReadabilityRules.Resolve(color, requested);
                Check(result.SurfaceOpacity >= requested - 1e-9,
                    $"{color}@{requested} 的解析不透明度 {result.SurfaceOpacity} 不应低于请求值");
                if (result.SurfaceOpacity < 1d - 1e-9)
                {
                    var ink = ColorContrastRules.ParseHex(
                        result.DarkInk ? LauncherReadabilityRules.DarkInkColor : LauncherReadabilityRules.LightInkColor);
                    var layer = surface with { A = result.SurfaceOpacity };
                    var overBlack = ColorContrastRules.CompositeOver(layer, black);
                    var overWhite = ColorContrastRules.CompositeOver(layer, white);
                    Check(ColorContrastRules.ContrastRatio(ink, overBlack) >= LauncherReadabilityRules.MinimumContrast
                        && ColorContrastRules.ContrastRatio(ink, overWhite) >= LauncherReadabilityRules.MinimumContrast,
                        $"{color}@{requested} 解析后在纯黑与纯白底上对比度均应达到 4.5:1");
                }
            }
        }

        var iceBlue = LauncherReadabilityRules.Resolve("#D8F3FF", 0.12d);
        Check(iceBlue.DarkInk && iceBlue.SurfaceOpacity is >= 0.55d and <= 0.70d,
            $"#D8F3FF @ 0.12 应使用深色文字且不透明度落在 [0.55, 0.70]，实际 {iceBlue.SurfaceOpacity:F2}");
    }

    private static void TestAppearancePreservesIceBlueStyle()
    {
        var appearance = new LauncherAppearanceState { FollowCardLook = true };
        var lightIceStyle = new DesktopStyleState
        {
            Glass = GlassMode.Light,
            TextInk = TextInk.White,
            GlassDensity = 0.05d
        };
        var (iceGlass, iceReadability) = appearance.ResolveReadableGlass(lightIceStyle);
        Check(iceReadability.DarkInk && iceGlass.Surface.EndsWith("D8F3FF", StringComparison.OrdinalIgnoreCase),
            $"浅色冰蓝风格下总览卡应保持冰蓝底色而非切为深色，实际 Surface={iceGlass.Surface}, DarkInk={iceReadability.DarkInk}");
        Check(iceReadability.SurfaceOpacity is >= 0.55d and <= 0.70d,
            $"冰蓝磨砂不透明度应保证纯黑背景也达到 4.5:1 对比度，实际 {iceReadability.SurfaceOpacity:F2}");

        var darkStyle = new DesktopStyleState { Glass = GlassMode.Dark };
        var (darkGlass, darkReadability) = appearance.ResolveReadableGlass(darkStyle);
        Check(!darkReadability.DarkInk && darkGlass.Surface.EndsWith("18222F", StringComparison.OrdinalIgnoreCase),
            "仅在桌面整体为深色玻璃模式时总览卡才使用深色底与白字");
    }

    private static void TestFavoritePreferencesAndFileSize()
    {
        var prefs = new LauncherPreferencesState
        {
            FavoriteMode = (LauncherFavoriteMode)99,
            BackdropMode = (LauncherBackdropMode)77,
            FavoriteKeys = ["  ", "app:one", "app:one", "app:two"]
        };
        prefs.Normalize();
        Check(prefs.FavoriteMode == LauncherFavoriteMode.FollowDock, "无效星标模式应归一化为跟随 Dock");
        Check(prefs.BackdropMode == LauncherBackdropMode.LiveWindow, "无效背景模式应归一化为实时透视");
        Check(prefs.FavoriteKeys.SequenceEqual(["app:one", "app:two"]), "独立收藏键应去空去重");
        Check(prefs.IsIndependentFavorite(["other", "app:two"]), "多键命中其一应视为已收藏");
        Check(!prefs.ToggleFavoriteKey("app:one"), "再次切换已有收藏键应移除并返回 false");
        Check(!prefs.IsIndependentFavorite(["app:one"]), "移除后不应再命中收藏");
        Check(prefs.ToggleFavoriteKey("app:three"), "切换新收藏键应加入并返回 true");
        Check(prefs.IsIndependentFavorite(["app:three"]), "加入后应命中收藏");

        // UniformToFill wallpaper crop: 1920x1080 wallpaper on 1920x1080 monitor -> 1:1 crop.
        var exactCrop = LauncherBackdropRules.ComputeWallpaperCrop(1920, 1080, 0, 0, 1920, 1080, 460, 220, 1000, 640);
        Check(exactCrop == new WallpaperCropRect(460, 220, 1000, 640),
            $"同分辨率壁纸裁切应 1:1 对应面板坐标，实际 {exactCrop}");
        // Negative-coordinate secondary monitor with 3840x2160 wallpaper on 1920x1080 screen -> 2x coordinates.
        var hiDpiCrop = LauncherBackdropRules.ComputeWallpaperCrop(3840, 2160, -1920, 0, 1920, 1080, -1420, 200, 920, 600);
        Check(hiDpiCrop == new WallpaperCropRect(1000, 400, 1840, 1200),
            $"负坐标副屏 4K 壁纸裁切应按比例换算，实际 {hiDpiCrop}");
        Check(LauncherBackdropRules.ComputeWallpaperCrop(0, 1080, 0, 0, 1920, 1080, 0, 0, 500, 400) is null,
            "无效图像尺寸应返回 null");

        Check(LauncherFileCategoryRules.FormatFileSize(0) == "0 B", "0 字节格式");
        Check(LauncherFileCategoryRules.FormatFileSize(1536) == "1.5 KB", "KB 格式");
        Check(LauncherFileCategoryRules.FormatFileSize(2 * 1024 * 1024) == "2 MB", "MB 格式");
        Check(LauncherFileCategoryRules.FormatFileSize((long)(1.25 * 1024 * 1024 * 1024)) == "1.25 GB", "GB 格式");
    }

    private static void TestPinCopy()
    {
        var source = new DockPinnedApp
        {
            DisplayName = "测试",
            ExecutablePath = @"C:\App\test.exe",
            AppUserModelId = "Test.App",
            TargetPath = @"C:\Target",
            Arguments = "--arg",
            WorkingDirectory = @"C:\Work",
            IconFile = "icon.png"
        };
        var clone = DockPinRules.Copy(source);
        Check(!ReferenceEquals(source, clone)
            && clone.DisplayName == source.DisplayName
            && clone.ExecutablePath == source.ExecutablePath
            && clone.AppUserModelId == source.AppUserModelId
            && clone.TargetPath == source.TargetPath
            && clone.Arguments == source.Arguments
            && clone.WorkingDirectory == source.WorkingDirectory
            && clone.IconFile == source.IconFile,
            "DockPinRules.Copy 应生成独立实例并复制全部属性");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
