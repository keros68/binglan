using BingLan.Core.Models;
using BingLan.Core.TopBar;

namespace BingLan.SmokeTests;

/// <summary>
/// Rows, engine resolution, direct-URL detection, and visual theme rules for the top bar's
/// inline search: local matches first, then either a direct-website action or the configured
/// default-browser search engine fallback.
/// </summary>
public static class TopBarSearchRowTests
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
        TestLocalRowsComeFirst();
        TestBrowserRowIsTheOnlyOneWithoutLocalMatches();
        TestBlankQueryKeepsLocalRowsOnly();
        TestBrowserUrlEscapesTheQuery();
        TestLongQueryShortensOnlyTheTitle();
        TestTemplateWithoutPlaceholder();
        TestTemplateWithStrayBracesDoesNotThrow();
        TestResolveEnginePresetsAndCustomFallback();
        TestDirectUrlDetectionAndLocalFilenameRejection();
        TestClampHorizontalPixels();
        TestSearchLookFollowsDefaultDarkTopBar();
        TestSearchLookFollowsTransparentTopBar();
        TestSearchLookFollowsCustomTopBar();
        TestSearchLookFollowsCardPalettesAndDarkGlass();
        TestSearchLookFrostedContrastAndAccent();
    }

    private static TopBarSearchCandidate File(string name, string path) =>
        new(TopBarSearchItemKind.File, name, name + ".txt", path);

    private static void TestLocalRowsComeFirst()
    {
        var rows = TopBarSearchRowRules.Compose(
            [
                File("报告", @"D:\工作\报告.txt"),
                File("账目", @"D:\财务\账目.txt")
            ],
            "报");

        Assert(rows.Count == 2, "已有本地结果时只应展示本地项，不应追加浏览器搜索行");
        Assert(rows[0].Kind == TopBarSearchRowKind.Local && rows[0].Title == "报告",
            "本地结果按原顺序排在最前");
        Assert(rows[1].Kind == TopBarSearchRowKind.Local, "第二个本地结果紧随其后");
        Assert(rows[0].Subtitle.Contains("报告.txt", StringComparison.Ordinal),
            "本地行副标题显示可读的位置");

        var uwpRows = TopBarSearchRowRules.Compose(
            [new TopBarSearchCandidate(TopBarSearchItemKind.App, "计算器", "Calculator", "shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")],
            "计算器");
        Assert(uwpRows.Count == 1 && uwpRows[0].Subtitle == "系统或商店应用",
            "UWP/商店应用副标题应显示可读说明而不是原始 shell 路径");
    }

    private static void TestBrowserRowIsTheOnlyOneWithoutLocalMatches()
    {
        var rows = TopBarSearchRowRules.Compose([], "明天天气");
        Assert(rows.Count == 1, "没有本地结果时应只剩浏览器兜底一行");
        Assert(rows[0].Kind == TopBarSearchRowKind.Browser, "唯一一行应是浏览器兜底");
        Assert(rows[0].Title == "用必应搜索「明天天气」", "默认兜底行标题应带上搜索词与必应引擎名");
        Assert(rows[0].BrowserUrl == "https://www.bing.com/search?q=%E6%98%8E%E5%A4%A9%E5%A4%A9%E6%B0%94",
            "默认兜底行地址应指向必应");
    }

    private static void TestBlankQueryKeepsLocalRowsOnly()
    {
        var rows = TopBarSearchRowRules.Compose([File("报告", @"D:\报告.txt")], "   ");
        Assert(rows.Count == 1 && rows[0].Kind == TopBarSearchRowKind.Local,
            "空查询不应提供网络搜索行");
    }

    private static void TestBrowserUrlEscapesTheQuery()
    {
        var url = TopBarSearchRowRules.BuildBrowserUrl(
            TopBarSearchRowRules.DefaultBrowserUrlTemplate,
            "a b&c");
        Assert(url == "https://www.bing.com/search?q=a%20b%26c", "查询词应按 URL 规则转义");

        var chinese = TopBarSearchRowRules.BuildBrowserUrl(
            TopBarSearchRowRules.DefaultBrowserUrlTemplate,
            "天气");
        Assert(chinese == "https://www.bing.com/search?q=%E5%A4%A9%E6%B0%94",
            "中文查询词应转为 UTF-8 百分号编码");
    }

    private static void TestLongQueryShortensOnlyTheTitle()
    {
        var query = new string('a', TopBarSearchRowRules.BrowserTitleQueryLimit + 10);
        var rows = TopBarSearchRowRules.Compose([], query);
        Assert(rows.Count == 1, "长查询仍应只有一行兜底");
        Assert(rows[0].Title.Contains('…', StringComparison.Ordinal),
            "超长查询在标题里应截断并加省略号");
        Assert(rows[0].BrowserUrl!.EndsWith(query, StringComparison.Ordinal),
            "标题截断不应影响真正打开的地址");
    }

    private static void TestTemplateWithoutPlaceholder()
    {
        var url = TopBarSearchRowRules.BuildBrowserUrl("https://example.com/search", "关键词");
        Assert(url == "https://example.com/search", "缺少占位符的模板应原样返回，不能抛异常");
    }

    private static void TestTemplateWithStrayBracesDoesNotThrow()
    {
        var url = TopBarSearchRowRules.BuildBrowserUrl("https://example.com/search?q={0}&tag={raw}", "冰蓝");
        Assert(url == "https://example.com/search?q=%E5%86%B0%E8%93%9D&tag={raw}",
            "包含其他花括号的自定义模板不应抛 FormatException");
    }

    private static void TestResolveEnginePresetsAndCustomFallback()
    {
        Assert(TopBarSearchRowRules.ResolveEngine(null, null) == TopBarSearchRowRules.BingEngine,
            "缺省设置应解析为必应");
        Assert(TopBarSearchRowRules.ResolveEngine("unknown", null) == TopBarSearchRowRules.BingEngine,
            "未知引擎名应回退必应");
        Assert(TopBarSearchRowRules.ResolveEngine("google", null) == TopBarSearchRowRules.GoogleEngine,
            "google 应解析为 Google 引擎");
        Assert(TopBarSearchRowRules.ResolveEngine("BAIDU", null) == TopBarSearchRowRules.BaiduEngine,
            "引擎名解析应大小写无关");

        var custom = TopBarSearchRowRules.ResolveEngine("custom", "  https://duckduckgo.com/?q={0}  ");
        Assert(custom.Key == TopBarSearchRowRules.EngineCustom
            && custom.UrlTemplate == "https://duckduckgo.com/?q={0}",
            "合法的 http/https 自定义模板应保留并去掉首尾空白");

        Assert(TopBarSearchRowRules.ResolveEngine("custom", "javascript:alert('{0}')") == TopBarSearchRowRules.BingEngine,
            "非 http/https 协议的自定义模板必须被拒绝并回退必应");
        Assert(TopBarSearchRowRules.ResolveEngine("custom", "https://{0}.example.com/") == TopBarSearchRowRules.BingEngine,
            "把 {0} 放在主机名中的自定义模板会对含空格查询失效，必须拒绝并回退必应");
        Assert(TopBarSearchRowRules.ResolveEngine("custom", "https://{0}@search.example/") == TopBarSearchRowRules.BingEngine,
            "把 {0} 放在凭据段中的自定义模板必须拒绝并回退必应");
        Assert(TopBarSearchRowRules.ResolveEngine("custom", "https://example.com/search") == TopBarSearchRowRules.BingEngine,
            "缺少 {0} 占位符的自定义模板应回退必应");
        Assert(TopBarSearchRowRules.ResolveEngine("custom", "   ") == TopBarSearchRowRules.BingEngine,
            "空白自定义模板应回退必应");

        var googleRows = TopBarSearchRowRules.Compose([], "WPF", TopBarSearchRowRules.GoogleEngine);
        Assert(googleRows.Count == 1
            && googleRows[0].Title == "用Google搜索「WPF」"
            && googleRows[0].BrowserUrl == "https://www.google.com/search?q=WPF",
            "传入 Google 引擎时应生成对应标题和搜索地址");
    }

    private static void TestDirectUrlDetectionAndLocalFilenameRejection()
    {
        Assert(TopBarSearchRowRules.TryNormalizeDirectUrl("https://github.com/keros68/binglan", out var u1, out var h1)
            && u1 == "https://github.com/keros68/binglan" && h1 == "github.com",
            "完整 https 网址应直达打开");

        Assert(TopBarSearchRowRules.TryNormalizeDirectUrl("https://intranet.example.test/Quarterly Draft.xlsx?token=abc", out var uSpace, out var hSpace)
            && uSpace == "https://intranet.example.test/Quarterly%20Draft.xlsx?token=abc"
            && hSpace == "intranet.example.test",
            "粘贴含空格路径的显式 http/https 网址应直达打开并编码空格，避免泄露到搜索引擎");

        Assert(TopBarSearchRowRules.TryNormalizeDirectUrl("github.com", out var u2, out var h2)
            && u2 == "https://github.com/" && h2 == "github.com",
            "常见裸域名应补全 https:// 并直达打开");

        Assert(TopBarSearchRowRules.TryNormalizeDirectUrl("localhost:3000/api", out var u3, out var h3)
            && u3 == "http://localhost:3000/api" && h3 == "localhost:3000",
            "localhost 带端口应补全 http:// 并保留端口展示");

        Assert(TopBarSearchRowRules.TryNormalizeDirectUrl("192.168.1.1:8080", out var u4, out var h4)
            && u4 == "http://192.168.1.1:8080/" && h4 == "192.168.1.1:8080",
            "完整 IPv4 地址应补全 http:// 并直达打开");

        string[] nonUrls =
        [
            "报告.txt",
            "readme.md",
            "Program.cs",
            "config.json",
            "setup.exe",
            "user@gmail.com",
            "javascript:alert(1)",
            "file:///C:/Windows",
            @"C:\Users\demo\github.com",
            "1.2",
            "hello world.com"
        ];
        foreach (var sample in nonUrls)
        {
            Assert(!TopBarSearchRowRules.TryNormalizeDirectUrl(sample, out _, out _),
                $"输入「{sample}」不应被误判为直达网址");
        }

        var directRows = TopBarSearchRowRules.Compose([], "github.com");
        Assert(directRows.Count == 1
            && directRows[0].Kind == TopBarSearchRowKind.Browser
            && directRows[0].Title == "打开网站 github.com"
            && directRows[0].BrowserUrl == "https://github.com/",
            "无本地匹配且输入裸域名时应生成直达网站行而非搜索引擎查询");

        var localBeatsBareDomain = TopBarSearchRowRules.Compose(
            [new TopBarSearchCandidate(TopBarSearchItemKind.File, "logo.ai", "logo.ai", @"D:\Design\logo.ai")],
            "logo.ai");
        Assert(localBeatsBareDomain.Count == 1 && localBeatsBareDomain[0].Kind == TopBarSearchRowKind.Local,
            "本地文件命中时即使文件名像裸域名也应优先展示本地结果");

        var malformedExplicitRows = TopBarSearchRowRules.Compose([], "https://intranet.example.test:99999/doc?token=secret");
        Assert(malformedExplicitRows.Count == 0,
            "格式错误的显式 http/https 链接不应退回外部搜索引擎查询，避免泄露链接内容");
    }

    private static void TestClampHorizontalPixels()
    {
        // Primary monitor centered
        Assert(TopBarSearchRowRules.ClampHorizontalPixels(960, 560, 0, 1920, 8) == 680,
            "主屏居中锚点应按宽度一半向左对齐");
        // Primary monitor clamped to left/right edge
        Assert(TopBarSearchRowRules.ClampHorizontalPixels(100, 560, 0, 1920, 8) == 8,
            "靠近左边缘时应夹紧到工作区左边界加边距");
        Assert(TopBarSearchRowRules.ClampHorizontalPixels(1900, 560, 0, 1920, 8) == 1352,
            "靠近右边缘时应夹紧到工作区右边界减宽度与边距");
        // Negative-coordinate secondary monitor (left of primary)
        Assert(TopBarSearchRowRules.ClampHorizontalPixels(-960, 560, -1920, 0, 12) == -1240,
            "负坐标副屏应按该副屏工作区居中，不能被主屏范围拉偏");
        Assert(TopBarSearchRowRules.ClampHorizontalPixels(-1900, 560, -1920, 0, 12) == -1908,
            "负坐标副屏左边缘夹紧应留在副屏内部");
        // Narrow monitor smaller than popup width
        Assert(TopBarSearchRowRules.ClampHorizontalPixels(200, 560, 100, 500, 8) == 100,
            "工作区窄于弹窗宽度时应回退到工作区左边界");
    }

    private static void TestSearchLookFollowsDefaultDarkTopBar()
    {
        var style = new DesktopStyleState { Glass = GlassMode.Light, TextInk = TextInk.Dark };
        var palette = TopBarSearchLookRules.ResolveForTopBar(style, new TopBarState());

        Assert(!palette.UseDarkInk && palette.AllowBackdropBlur,
            "默认深色磨砂顶栏应打开深色磨砂弹层并使用浅色文字");
        Assert(palette.GlassTint.EndsWith("1E1F24", StringComparison.Ordinal)
            && palette.FallbackSurface.EndsWith("1E1F24", StringComparison.Ordinal),
            "默认顶栏弹层的磨砂和回退应采用顶栏的深灰底色");
    }

    private static void TestSearchLookFollowsTransparentTopBar()
    {
        var style = new DesktopStyleState { Glass = GlassMode.Dark };
        var state = new TopBarState
        {
            FollowCardLook = false,
            SurfaceColor = "#00FFFF",
            SurfaceOpacity = 0d
        };

        foreach (var frosted in new[] { false, true })
        {
            state.FrostedGlass = frosted;
            var overDark = TopBarSearchLookRules.ResolveForTopBar(style, state, adaptiveUseDarkInk: false);
            var overLight = TopBarSearchLookRules.ResolveForTopBar(style, state, adaptiveUseDarkInk: true);

            Assert(overDark.GlassTint == "#0100FFFF" && overDark.FallbackSurface == "#0100FFFF"
                && overLight.GlassTint == "#0100FFFF" && overLight.FallbackSurface == "#0100FFFF",
                "0% 不透明度的青色顶栏应打开同样透明的弹层，保留顶栏用于接收点击的最低 alpha");
            Assert(!overDark.AllowBackdropBlur && !overLight.AllowBackdropBlur,
                "浅色顶栏的常驻表面和弹层均应采用透明画刷");
            Assert(overDark.UseTextShadow && overLight.UseTextShadow
                && !overDark.UseDarkInk && overLight.UseDarkInk && overDark.Foreground != overLight.Foreground,
                "全透明弹层应跟随顶栏的壁纸明暗判定切换字色，并保留文字对比阴影");
        }
    }

    private static void TestSearchLookFollowsCustomTopBar()
    {
        var style = new DesktopStyleState { Glass = GlassMode.Clear, TextInk = TextInk.White };
        var topBar = new TopBarState { FollowCardLook = false, SurfaceOpacity = 0.84d };
        foreach (var (color, lightSurface) in new[] { ("#D8CFF0", true), ("#213A57", false) })
        {
            topBar.SurfaceColor = color;
            foreach (var frosted in new[] { false, true })
            {
                topBar.FrostedGlass = frosted;
                var palette = TopBarSearchLookRules.ResolveForTopBar(style, topBar);
                Assert(palette.GlassTint[3..] == color[1..] && palette.FallbackSurface[3..] == color[1..],
                    $"独立顶栏弹层应保留所选底色 {color}");
                Assert(palette.UseDarkInk == lightSurface,
                    $"独立顶栏弹层应按所选底色 {color} 选择文字色");
                Assert(palette.AllowBackdropBlur == (frosted && !lightSurface),
                    $"独立顶栏弹层应与常驻顶栏使用相同的磨砂条件，底色 {color}，磨砂 {frosted}");
                if (!palette.AllowBackdropBlur)
                {
                    var barSurface = DesktopStyleRules.TopBar(style, topBar).Surface;
                    Assert(palette.GlassTint == barSurface && palette.FallbackSurface == barSurface,
                        $"不使用磨砂时，顶栏弹层应采用与顶栏相同的不透明度，底色 {color}");
                }
            }
        }

        topBar.SurfaceOpacity = 0d;
        topBar.FrostedGlass = false;
        var clearDark = TopBarSearchLookRules.ResolveForTopBar(style, topBar, adaptiveUseDarkInk: false);
        Assert(!clearDark.AllowBackdropBlur && clearDark.FallbackSurface == "#01213A57" && clearDark.UseTextShadow,
            "0% 不透明度的深色顶栏在关闭磨砂时，应使用通透的深色弹层画刷与文字阴影");

        topBar.FrostedGlass = true;
        var clearWithFrostedEnabled = TopBarSearchLookRules.ResolveForTopBar(style, topBar, adaptiveUseDarkInk: false);
        Assert(clearWithFrostedEnabled == clearDark,
            "0% 不透明度应保持全透明的深色弹层，开启磨砂后也不铺设磨砂底");

        topBar.SurfaceOpacity = 2d / 255d;
        var frostedDark = TopBarSearchLookRules.ResolveForTopBar(style, topBar, adaptiveUseDarkInk: true);
        Assert(frostedDark.AllowBackdropBlur && !frostedDark.UseDarkInk,
            "深色顶栏在可使用原生磨砂的最小不透明度应开启磨砂弹层");
    }

    private static void TestSearchLookFollowsCardPalettesAndDarkGlass()
    {
        var topBar = new TopBarState
        {
            FollowCardLook = true,
            SurfaceColor = "#FF00FF",
            SurfaceOpacity = 0d
        };
        foreach (var style in new[]
        {
            new DesktopStyleState { Glass = GlassMode.Light, Palette = DesktopStyleRules.DefaultPalette, TextInk = TextInk.White },
            new DesktopStyleState { Glass = GlassMode.Light, Palette = "milk-tea" },
            new DesktopStyleState { Glass = GlassMode.Dark, TextInk = TextInk.Dark },
            new DesktopStyleState { Glass = GlassMode.Clear }
        })
        {
            var barSurface = DesktopStyleRules.TopBar(style, topBar).Surface;
            var palette = TopBarSearchLookRules.ResolveForTopBar(style, topBar, adaptiveUseDarkInk: true);
            var useBlur = DesktopStyleRules.TopBarFrostedScrim(topBar, barSurface);
            Assert(palette.GlassTint[3..] == barSurface[3..] && palette.FallbackSurface[3..] == barSurface[3..],
                $"跟随全局的顶栏弹层应与顶栏同色，材质 {style.Glass}，配色 {style.Palette}");
            Assert(palette.AllowBackdropBlur == useBlur && palette.UseDarkInk == (style.Glass != GlassMode.Dark),
                $"跟随全局的顶栏弹层应与顶栏采用相同的磨砂条件与明暗文字，材质 {style.Glass}");
            if (style.Glass == GlassMode.Clear)
            {
                Assert(palette.FallbackSurface == "#01FFFFFF" && palette.UseTextShadow,
                    "全局无底板外观应让跟随它的顶栏弹层保持通透，并保留文字阴影");
            }
        }
    }

    private static void TestSearchLookFrostedContrastAndAccent()
    {
        var topBar = new TopBarState { FollowCardLook = false, SurfaceColor = "#1E1F24", SurfaceOpacity = 0.82d };
        foreach (var accent in new[] { DesktopStyleRules.DefaultAccentColor, "#FFFF00", "#000000" })
        {
            var palette = TopBarSearchLookRules.ResolveForTopBar(new DesktopStyleState { AccentColor = accent }, topBar);
            var foreground = ColorContrastRules.ParseHex(palette.Foreground);
            var muted = ColorContrastRules.ParseHex(palette.MutedForeground);
            var placeholder = ColorContrastRules.ParseHex(palette.PlaceholderForeground);
            var fallback = ColorContrastRules.ParseHex(palette.FallbackSurface);
            var activeRow = ColorContrastRules.ParseHex(palette.ActiveRowBackground);
            Assert(palette.AllowBackdropBlur && !palette.UseTextShadow, "深色磨砂弹层应采用自身的对比度，无需文字阴影");
            foreach (var wallpaper in new[] { "#000000", "#FFFFFF" })
            {
                var background = ColorContrastRules.CompositeLayers(wallpaper, fallback);
                Assert(ColorContrastRules.ContrastRatio(foreground, background) >= ColorContrastRules.MinimumNormalTextContrast
                    && ColorContrastRules.ContrastRatio(muted, background) >= ColorContrastRules.MinimumNormalTextContrast
                    && ColorContrastRules.ContrastRatio(placeholder, background) >= ColorContrastRules.MinimumNormalTextContrast,
                    $"磨砂不可用时，顶栏弹层文本在 {wallpaper} 背景上应保持 4.5:1 的对比度");
                var selected = ColorContrastRules.CompositeLayers(wallpaper, fallback, activeRow);
                Assert(ColorContrastRules.ContrastRatio(foreground, selected) >= ColorContrastRules.MinimumNormalTextContrast,
                    $"强调色 {accent} 的搜索选中行在 {wallpaper} 背景上应保持 4.5:1 的对比度");
            }
        }
    }
}
