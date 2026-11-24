namespace BingLan.Core.Models;

public enum LauncherAppCategory
{
    Unclassified = 0,
    Office = 1,
    WebAndCommunication = 2,
    // 3 was StorageAndTransfer (Schema v52–v53), migrated to SystemTools in v54.
    MediaAndEntertainment = 4,
    // 5 was MapsAndEngineering (Schema v52–v53), migrated to Unclassified in v54.
    Development = 6,
    Games = 7,
    SystemTools = 8,
    Auxiliary = 9,
    Design = 10,
    Ai = 11
}

public static class LauncherAppCategoryRules
{
    public const int MinimumCategoryItemCount = 3;

    public static readonly LauncherAppCategory[] OrderedCategories =
    [
        LauncherAppCategory.Office,
        LauncherAppCategory.Ai,
        LauncherAppCategory.WebAndCommunication,
        LauncherAppCategory.MediaAndEntertainment,
        LauncherAppCategory.Games,
        LauncherAppCategory.Design,
        LauncherAppCategory.Development,
        LauncherAppCategory.SystemTools,
        LauncherAppCategory.Unclassified,
        LauncherAppCategory.Auxiliary
    ];

    private static readonly Dictionary<string, LauncherAppCategory> Executables = BuildExecutableCategories();
    private static readonly string[] AuxiliaryWords =
    [
        "uninstall", "uninstaller", "setup", "installer", "repair", "diagnostics", "updater", "upgrade",
        "documentation", "readme", "faq", "faqs", "changelog", "copying", "website"
    ];
    private static readonly string[] AuxiliaryPhrases =
    [
        "release notes", "revision history", "example scripts", "user's guide", "user’s guide",
        "manual page", "online help", "sample desktop apps", "sample uwp apps",
        "documentation for desktop apps", "documentation for uwp apps"
    ];
    private static readonly string[] AuxiliaryNames =
    [
        "卸载", "安装程序", "安装向导", "修复工具", "诊断工具", "维护工具", "升级工具", "更新工具", "检查更新",
        "帮助文档", "在线帮助", "用户手册", "用户手则", "新机手册", "使用说明", "访问我们的网站", "软件广播站",
        "文件清单", "崩溃补丁", "故障排查", "重置为默认值", "从早期版本进行移植", "批处理检查器", "附着数字签名",
        "语言首选项", "人工智能(AI)政策"
    ];
    private static readonly string[] AuxiliaryFolders =
    [
        "Documentation", "smartctl Examples", "smartd Examples", "移植自定义设置", "Microsoft Office 工具"
    ];
    private static readonly string[] AuxiliaryExtensions = [".chm", ".html", ".htm", ".txt"];

    public static string DisplayName(LauncherAppCategory category) => category switch
    {
        LauncherAppCategory.Office => "办公学习",
        LauncherAppCategory.Ai => "AI 应用",
        LauncherAppCategory.WebAndCommunication => "聊天上网",
        LauncherAppCategory.MediaAndEntertainment => "影音娱乐",
        LauncherAppCategory.Games => "游戏休闲",
        LauncherAppCategory.Design => "设计创作",
        LauncherAppCategory.Development => "开发工具",
        LauncherAppCategory.SystemTools => "系统工具",
        LauncherAppCategory.Auxiliary => "辅助入口",
        _ => "其他应用"
    };

    public static string Glyph(LauncherAppCategory category) => category switch
    {
        LauncherAppCategory.Office => "\uE8A5",
        LauncherAppCategory.Ai => "\uE99A",
        LauncherAppCategory.WebAndCommunication => "\uE8BD",
        LauncherAppCategory.MediaAndEntertainment => "\uE8D6",
        LauncherAppCategory.Games => "\uE7FC",
        LauncherAppCategory.Design => "\uE790",
        LauncherAppCategory.Development => "\uE943",
        LauncherAppCategory.SystemTools => "\uE90F",
        LauncherAppCategory.Auxiliary => "\uE7BA",
        _ => "\uE71D"
    };

    /// <summary>
    /// Folds sparse automatic categories (fewer than <see cref="MinimumCategoryItemCount"/> items
    /// and without any manual override) into <see cref="LauncherAppCategory.Unclassified"/> ("其他").
    /// Manual overrides and <see cref="LauncherAppCategory.Auxiliary"/> are always preserved.
    /// </summary>
    public static IReadOnlyDictionary<TKey, LauncherAppCategory> ResolveEffectiveCategories<TKey>(
        IEnumerable<(TKey Key, LauncherAppCategory Category, bool IsManual)> items,
        IEqualityComparer<TKey>? comparer = null) where TKey : notnull
    {
        var list = items.ToList();
        var counts = new Dictionary<LauncherAppCategory, int>();
        var hasManual = new HashSet<LauncherAppCategory>();

        foreach (var (_, category, isManual) in list)
        {
            counts[category] = counts.GetValueOrDefault(category) + 1;
            if (isManual) hasManual.Add(category);
        }

        var result = new Dictionary<TKey, LauncherAppCategory>(comparer);
        foreach (var (key, category, isManual) in list)
        {
            if (isManual
                || category is LauncherAppCategory.Unclassified or LauncherAppCategory.Auxiliary
                || hasManual.Contains(category)
                || counts.GetValueOrDefault(category) >= MinimumCategoryItemCount)
            {
                result[key] = category;
            }
            else
            {
                result[key] = LauncherAppCategory.Unclassified;
            }
        }
        return result;
    }

    public static LauncherAppCategory Classify(string displayName, string target, string? arguments,
        string? startMenuFolder = null)
    {
        var fileName = FileName(target);
        var extension = FileExtension(fileName);
        var folders = (startMenuFolder ?? string.Empty).Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var tokens = displayName.Split([' ', '\t', '-', '_', '.', '(', ')', '[', ']'],
            StringSplitOptions.RemoveEmptyEntries);

        if (AuxiliaryNames.Any(name => displayName.Contains(name, StringComparison.OrdinalIgnoreCase))
            || AuxiliaryPhrases.Any(phrase => displayName.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            || tokens.Any(token => AuxiliaryWords.Contains(token, StringComparer.OrdinalIgnoreCase))
            || folders.Any(folder => AuxiliaryFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            || AuxiliaryExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            || fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
            || AuxiliaryWords.Contains(Path.GetFileNameWithoutExtension(fileName), StringComparer.OrdinalIgnoreCase)
            || string.Equals(fileName, "msiexec.exe", StringComparison.OrdinalIgnoreCase)
            || HasUninstallArgument(arguments))
        {
            return LauncherAppCategory.Auxiliary;
        }

        if (Executables.TryGetValue(fileName, out var category)) return category;

        if (ClassifyByPackagedIdentity(target) is { } packagedCategory) return packagedCategory;
        if (ClassifyByStartMenuFolder(folders) is { } folderCategory) return folderCategory;
        if (ClassifyByKnownBrandName(displayName) is { } brandCategory) return brandCategory;

        return LauncherAppCategory.Unclassified;
    }

    private static LauncherAppCategory? ClassifyByPackagedIdentity(string target)
    {
        if (target.Contains("OpenAI.ChatGPT", StringComparison.OrdinalIgnoreCase)
            || target.Contains("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)
            || target.Contains("Claude_", StringComparison.OrdinalIgnoreCase)
            || target.Contains("Anthropic.", StringComparison.OrdinalIgnoreCase)
            || target.Contains("Microsoft.Copilot_", StringComparison.OrdinalIgnoreCase)
            || target.Contains("Google.Gemini", StringComparison.OrdinalIgnoreCase)
            || target.Contains("ByteDance.Doubao", StringComparison.OrdinalIgnoreCase)
            || target.Contains("Tencent.Yuanbao", StringComparison.OrdinalIgnoreCase))
            return LauncherAppCategory.Ai;

        if (target.Contains("Microsoft.WindowsCalculator_", StringComparison.OrdinalIgnoreCase)
            || target.Contains("Microsoft.WindowsNotepad_", StringComparison.OrdinalIgnoreCase)
            || target.Contains("Microsoft.ScreenSketch_", StringComparison.OrdinalIgnoreCase)
            || target.Contains("Microsoft.WindowsAlarms_", StringComparison.OrdinalIgnoreCase))
            return LauncherAppCategory.SystemTools;

        if (target.Contains("Microsoft.WindowsTerminal_", StringComparison.OrdinalIgnoreCase)
            || target.Contains("Microsoft.PowerShell_", StringComparison.OrdinalIgnoreCase))
            return LauncherAppCategory.Development;

        if (target.Contains("Microsoft.ZuneMusic_", StringComparison.OrdinalIgnoreCase)
            || target.Contains("Microsoft.ZuneVideo_", StringComparison.OrdinalIgnoreCase)
            || target.Contains("SpotifyAB.SpotifyMusic_", StringComparison.OrdinalIgnoreCase))
            return LauncherAppCategory.MediaAndEntertainment;

        if (target.Contains("Microsoft.Windows.Photos_", StringComparison.OrdinalIgnoreCase)
            || target.Contains("Microsoft.Paint_", StringComparison.OrdinalIgnoreCase)
            || target.Contains("Clipchamp.Clipchamp_", StringComparison.OrdinalIgnoreCase))
            return LauncherAppCategory.Design;

        if (target.Contains("Microsoft.GamingApp_", StringComparison.OrdinalIgnoreCase))
            return LauncherAppCategory.Games;

        if (target.Contains("Microsoft.Todos_", StringComparison.OrdinalIgnoreCase)
            || target.Contains("Microsoft.Office.OneNote_", StringComparison.OrdinalIgnoreCase)
            || target.Contains("Microsoft.OutlookForWindows_", StringComparison.OrdinalIgnoreCase)
            || target.Contains("Microsoft.MicrosoftStickyNotes_", StringComparison.OrdinalIgnoreCase)
            || target.Contains("Microsoft.Whiteboard_", StringComparison.OrdinalIgnoreCase))
            return LauncherAppCategory.Office;

        if (target.Contains("MSTeams_", StringComparison.OrdinalIgnoreCase)
            || target.Contains("5319275A.WhatsAppDesktop_", StringComparison.OrdinalIgnoreCase))
            return LauncherAppCategory.WebAndCommunication;

        return null;
    }

    private static LauncherAppCategory? ClassifyByStartMenuFolder(string[] folders)
    {
        if (folders.Any(folder => folder.Equals("Microsoft Office", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("WPS Office", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("LibreOffice", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("MiKTeX", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("知网研学", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("增值税电子发票阅读器", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("VDI登录客户端", StringComparison.OrdinalIgnoreCase)))
            return LauncherAppCategory.Office;

        if (folders.Any(folder => folder.Equals("微信", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("QQ", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("腾讯会议", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Telegram Desktop", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("WeLink", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("钉钉", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("飞书", StringComparison.OrdinalIgnoreCase)))
            return LauncherAppCategory.WebAndCommunication;

        if (folders.Any(folder => folder.Equals("网易云音乐", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("网易千千壁纸", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("网易叭哥说", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("QQ音乐", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("酷狗音乐", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("哔哩哔哩", StringComparison.OrdinalIgnoreCase)))
            return LauncherAppCategory.MediaAndEntertainment;

        if (folders.Any(folder => folder.Equals("暴雪战网", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("魔兽世界", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("HeyboxApp", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Steam", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Epic Games", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("WeGame", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Riot Games", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Battle.net", StringComparison.OrdinalIgnoreCase)))
            return LauncherAppCategory.Games;

        if (folders.Any(folder => folder.Equals("Adobe", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Inkscape", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Blender", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Section增强辅助制图系统", StringComparison.OrdinalIgnoreCase)
            || folder.StartsWith("CorelDRAW", StringComparison.OrdinalIgnoreCase)
            || folder.StartsWith("AutoCAD", StringComparison.OrdinalIgnoreCase)
            || folder.StartsWith("Autodesk", StringComparison.OrdinalIgnoreCase)
            || folder.StartsWith("ArcGIS", StringComparison.OrdinalIgnoreCase)
            || folder.StartsWith("QGIS", StringComparison.OrdinalIgnoreCase)
            || folder.StartsWith("mapgis", StringComparison.OrdinalIgnoreCase)))
            return LauncherAppCategory.Design;

        if (folders.Any(folder => folder.Equals("JetBrains", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Visual Studio Code", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Visual Studio Tools", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("VC", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Git", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Python", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Node.js", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("R", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("PowerShell", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Windows PowerShell", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Windows Software Development Kit", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Windows Performance Toolkit", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Windows App Certification Kit", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("微信开发者工具", StringComparison.OrdinalIgnoreCase)
            || folder.StartsWith("Anaconda", StringComparison.OrdinalIgnoreCase)
            || folder.StartsWith("Miniforge", StringComparison.OrdinalIgnoreCase)
            || folder.StartsWith("Inno Setup", StringComparison.OrdinalIgnoreCase)
            || folder.StartsWith("Application Verifier", StringComparison.OrdinalIgnoreCase)))
            return LauncherAppCategory.Development;

        if (folders.Any(folder => folder.Equals("Windows Accessories", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Windows System", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Windows Administrative Tools", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Accessories", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Administrative Tools", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("System Tools", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Accessibility", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Windows 附件", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Windows 系统", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Windows 管理工具", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("360 Zip", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("傲梅轻松备份", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("向日葵远程控制", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("百度网盘", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Lenovo", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("NVIDIA Corporation", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Windows优化大师", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("WizTree", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("SSDFresh", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("SSLVPN登录客户端", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("smartmontools", StringComparison.OrdinalIgnoreCase)
            || folder.StartsWith("HP ", StringComparison.OrdinalIgnoreCase)))
            return LauncherAppCategory.SystemTools;

        return null;
    }

    private static LauncherAppCategory? ClassifyByKnownBrandName(string displayName)
    {
        var trimmed = displayName.Trim();
        if (trimmed.Length == 0) return null;

        // Exact or brand-prefixed AI clients (including PWAs / Electron wrappers whose target is chrome_proxy.exe).
        if (MatchesBrand(trimmed,
            "ChatGPT", "Claude", "Gemini", "DeepSeek", "Kimi", "豆包", "腾讯元宝", "元宝", "通义",
            "文心一言", "智谱清言", "Grok", "Cursor", "Windsurf", "Trae", "Qoder", "ZCode",
            "Coffee CLI", "Cherry Studio", "LM Studio", "Ollama", "Antigravity", "CC Switch",
            "aicross", "Copilot", "yourmem", "skills-manager"))
            return LauncherAppCategory.Ai;

        if (MatchesBrand(trimmed,
            "知网研学", "增值税电子发票", "协同办公系统", "Sticky Notes", "LibreOffice", "draw.io", "文颜", "Adobe Acrobat"))
            return LauncherAppCategory.Office;

        if (MatchesBrand(trimmed, "小黑盒", "黑盒工坊", "暴雪战网", "魔兽世界"))
            return LauncherAppCategory.Games;

        if (MatchesBrand(trimmed, "GeoLibre Desktop", "PicGo", "X-Rite Color Assistant"))
            return LauncherAppCategory.Design;

        if (MatchesBrand(trimmed, "Local Project Bridge"))
            return LauncherAppCategory.Development;

        if (MatchesBrand(trimmed, "冰蓝桌面", "Metrik", "UU远程", "Clash Verge", "LocalSend", "联想电脑管家", "联想应用商店", "File Explorer", "文件资源管理器"))
            return LauncherAppCategory.SystemTools;

        return null;
    }

    private static bool MatchesBrand(string displayName, params string[] brands)
    {
        foreach (var brand in brands)
        {
            if (displayName.Equals(brand, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (displayName.StartsWith(brand, StringComparison.OrdinalIgnoreCase)
                && displayName.Length > brand.Length
                && displayName[brand.Length] is ' ' or '-' or '_' or '(' or '（' or '.')
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasUninstallArgument(string? arguments) =>
        (arguments ?? string.Empty).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Any(argument => argument.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)
                || argument.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));

    private static string FileName(string target)
    {
        try
        {
            return Path.GetFileName(target.Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar));
        }
        catch (ArgumentException) { return string.Empty; }
    }

    private static string FileExtension(string fileName)
    {
        try
        {
            return Path.GetExtension(fileName);
        }
        catch (ArgumentException) { return string.Empty; }
    }

    private static Dictionary<string, LauncherAppCategory> BuildExecutableCategories()
    {
        var categories = new Dictionary<string, LauncherAppCategory>(StringComparer.OrdinalIgnoreCase);

        Add(LauncherAppCategory.Office,
            "winword.exe", "excel.exe", "powerpnt.exe", "onenote.exe", "outlook.exe", "olk.exe",
            "msaccess.exe", "mspub.exe", "visio.exe", "winproj.exe",
            "wps.exe", "et.exe", "wpp.exe", "wpsoffice.exe", "ksolaunch.exe",
            "soffice.exe", "swriter.exe", "scalc.exe", "simpress.exe", "sbase.exe", "sdraw.exe", "smath.exe",
            "obsidian.exe", "notion.exe", "typora.exe", "zotero.exe", "logseq.exe", "siyuan.exe",
            "acrobat.exe", "acrord32.exe", "sumatrapdf.exe", "foxitpdfreader.exe", "foxitphantompdf.exe",
            "pdfgear.exe", "xmind.exe", "edrawmax.exe", "edrawmind.exe", "mindmaster.exe",
            "calibre.exe", "anki.exe", "evernote.exe", "yinxiang.exe", "yuque.exe",
            "ticktick.exe", "todoist.exe", "youdaonote.exe", "cajviewer.exe",
            "e-study.exe", "cnkiestudy.exe", "texworks.exe", "miktex-console.exe",
            "draw.io.exe", "drawio.exe", "wenyan.exe", "ofdreader.exe", "suwellreader.exe");

        Add(LauncherAppCategory.Ai,
            "chatgpt.exe", "claude.exe", "gemini.exe", "deepseek.exe", "kimi.exe",
            "doubao.exe", "yuanbao.exe", "tongyi.exe", "grok.exe", "grok bot.exe",
            "cherrystudio.exe", "cherry studio.exe", "ollama app.exe", "ollama.exe",
            "lm studio.exe", "lmstudio.exe", "jan.exe", "chatbox.exe", "anythingllm.exe",
            "poe.exe", "perplexity.exe", "copilot.exe",
            "cursor.exe", "windsurf.exe", "zcode.exe", "qoder.exe", "qoder cn.exe",
            "trae.exe", "trae cn.exe", "codebuddy.exe", "coffee-cli.exe", "coffee cli.exe",
            "antigravity.exe", "kiro.exe", "comate.exe", "lingma.exe",
            "aicross.exe", "cc switch.exe", "cc-switch.exe", "ccswitch.exe",
            "yourmem.exe", "skills-manager.exe");

        Add(LauncherAppCategory.WebAndCommunication,
            "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe",
            "arc.exe", "centbrowser.exe", "360chrome.exe", "360chromex.exe", "360se.exe", "quark.exe",
            "sogouexplorer.exe", "qqbrowser.exe", "ucbrowser.exe", "floorp.exe", "zen.exe",
            "librewolf.exe", "waterfox.exe", "thorium.exe", "chromium.exe",
            "wechat.exe", "weixin.exe", "qq.exe", "qqnt.exe", "tim.exe", "telegram.exe", "discord.exe",
            "slack.exe", "teams.exe", "ms-teams.exe", "zoom.exe", "dingtalk.exe", "dingtalklauncher.exe",
            "lark.exe", "feishu.exe", "feishulauncher.exe", "wxwork.exe", "wemeetapp.exe", "wemeet.exe",
            "welink.exe", "thunderbird.exe", "foxmail.exe", "mailmaster.exe", "neteasemail.exe",
            "skype.exe", "whatsapp.exe", "element.exe", "signal.exe", "kook.exe", "yy.exe");

        Add(LauncherAppCategory.MediaAndEntertainment,
            "vlc.exe", "potplayermini64.exe", "potplayermini.exe", "potplayer64.exe", "potplayer.exe",
            "mpv.exe", "mpvnet.exe", "mpc-hc64.exe", "mpc-be64.exe", "wmplayer.exe",
            "cloudmusic.exe", "qqmusic.exe", "kugou.exe", "kuwo.exe", "spotify.exe", "itunes.exe",
            "applemusic.exe", "foobar2000.exe", "musicbee.exe", "aimp.exe", "lx-music-desktop.exe",
            "yesplaymusic.exe", "saltplayer.exe", "ximalaya.exe",
            "bilibili.exe", "qqlive.exe", "qyclient.exe", "iqiyi.exe", "youkumedia.exe", "youku.exe",
            "mgtv.exe", "douyin.exe", "kodi.exe", "plex.exe", "jellyfinmediaplayer.exe",
            "duoduowallpaper.exe", "wallpaper32.exe", "wallpaper64.exe", "lively.exe",
            "qianqianwallpaper.exe", "bagspeak.exe");

        Add(LauncherAppCategory.Games,
            "steam.exe", "epicgameslauncher.exe", "galaxyclient.exe", "goggalaxy.exe",
            "battle.net.exe", "battle.net launcher.exe", "battlenet.exe",
            "ubisoftconnect.exe", "upc.exe", "eadesktop.exe", "riotclientservices.exe",
            "wegame.exe", "playnite.desktopapp.exe", "playnite.fullscreenapp.exe", "xboxpcapp.exe",
            "uu.exe", "leigod.exe", "biubiu.exe", "qiyou.exe", "xunyou.exe",
            "heybox.exe", "heyboxapp.exe", "xiaoheihe.exe", "wow.exe", "wowclassic.exe",
            "yuanshen.exe", "starrail.exe", "zenlesszonezero.exe", "leagueclient.exe",
            "prismlauncher.exe", "hmcl.exe", "pcl2.exe", "hyp.exe", "curseforge.exe", "modrinth.exe");

        Add(LauncherAppCategory.Design,
            "photoshop.exe", "illustrator.exe", "indesign.exe", "afterfx.exe",
            "adobe premiere pro.exe", "lightroom.exe", "lightroomclassic.exe", "adobe audition.exe",
            "figma.exe", "blender.exe", "gimp.exe", "gimp-2.10.exe", "gimp-3.exe", "inkscape.exe", "inkview.exe",
            "krita.exe", "paintdotnet.exe", "mspaint.exe", "obs64.exe", "obs32.exe",
            "capcut.exe", "jianyingpro.exe", "resolve.exe", "audacity.exe",
            "snipaste.exe", "pixpin.exe", "sharex.exe", "picgo.exe", "pureref.exe",
            "eagle.exe", "billfish.exe", "honeyview.exe", "imageglass.exe", "xnviewmp.exe", "fsviewer.exe",
            "photo.exe", "designer.exe", "publisher.exe", "aseprite.exe", "cinema 4d.exe", "maya.exe", "3dsmax.exe",
            "acad.exe", "coreldrw.exe", "corelpp.exe", "corelcap.exe",
            "qgis-bin.exe", "qgis-ltr-bin.exe", "arcgispro.exe", "arcmap.exe", "geolibre.exe",
            "sketchup.exe", "rhino.exe", "revit.exe", "sldworks.exe", "canva.exe", "mastergo.exe",
            "x-rite color assistant.exe");

        Add(LauncherAppCategory.Development,
            "code.exe", "zed.exe", "devenv.exe",
            "idea64.exe", "pycharm64.exe", "webstorm64.exe", "clion64.exe", "rider64.exe",
            "datagrip64.exe", "goland64.exe", "phpstorm64.exe", "rubymine64.exe", "rustrover64.exe",
            "androidstudio64.exe", "studio64.exe", "rstudio.exe", "r.exe", "rgui.exe",
            "powershell.exe", "pwsh.exe", "cmd.exe", "wt.exe", "windowsterminal.exe", "mintty.exe",
            "putty.exe", "mobaxterm.exe", "xshell.exe", "xftp.exe", "finalshell.exe", "termius.exe",
            "tabby.exe", "electerm.exe", "windterm.exe",
            "git-bash.exe", "git-gui.exe", "git-cmd.exe", "node.exe", "python.exe", "pythonw.exe",
            "compil32.exe", "wechatdevtools.exe", "hbuilderx.exe",
            "dbeaver.exe", "navicat.exe", "tableplus.exe", "heidisql.exe", "docker desktop.exe",
            "postman.exe", "apifox.exe", "insomnia.exe", "bruno.exe", "reqable.exe",
            "notepad++.exe", "sublime_text.exe", "winmergeu.exe", "bcompare.exe",
            "githubdesktop.exe", "sourcetree.exe", "fork.exe", "fiddler.exe", "wireshark.exe",
            "local project bridge.exe");

        Add(LauncherAppCategory.SystemTools,
            "explorer.exe", "notepad.exe", "calc.exe", "taskmgr.exe",
            "control.exe", "regedit.exe", "mmc.exe", "msinfo32.exe", "resmon.exe", "cleanmgr.exe",
            "dfrgui.exe", "eventvwr.exe", "perfmon.exe", "msconfig.exe", "mdsched.exe",
            "charmap.exe", "magnify.exe", "narrator.exe", "osk.exe", "livecaptions.exe", "voiceaccess.exe",
            "psr.exe", "recdisc.exe", "iscsicpl.exe", "odbcad32.exe", "dcomcnfg.exe",
            "snippingtool.exe", "7zfm.exe", "winrar.exe", "bandizip.exe", "peazip.exe", "360zip.exe",
            "everything.exe", "powertoys.exe", "procexp.exe", "procexp64.exe", "processhacker.exe", "systeminformer.exe",
            "wiztree.exe", "wiztree64.exe", "treesize.exe", "spacesniffer.exe",
            "onedrive.exe", "baidunetdisk.exe", "aliyundrive.exe", "alidrive.exe", "quarkclouddrive.exe",
            "dropbox.exe", "googledrivefs.exe", "filezilla.exe", "winscp.exe", "syncthing.exe",
            "qbittorrent.exe", "transmission-qt.exe", "motrix.exe", "idman.exe", "thunder.exe", "ndm.exe", "fdm.exe",
            "localsend_app.exe", "todesk.exe", "rustdesk.exe", "sunloginclient.exe", "teamviewer.exe",
            "anydesk.exe", "mstsc.exe", "gameviewer.exe", "easyconnect.exe",
            "cpuz.exe", "gpuz.exe", "diskinfo64.exe", "crystaldiskinfo.exe", "hwinfo64.exe", "aida64.exe",
            "ssdfresh.exe", "aomeibackupper.exe", "backupper.exe",
            "360safe.exe", "huorong.exe", "sysdiag.exe", "dism++.exe", "geek.exe",
            "nvidia app.exe", "lenovopcmgr.exe", "pcmgr.exe", "qqpcmgr.exe",
            "binglan.exe", "metrik.exe", "quicklook.exe", "ditto.exe", "ecopaste.exe",
            "clash-verge.exe", "clash-nyanpasu.exe", "flclash.exe", "mihomo-party.exe", "v2rayn.exe", "hiddify.exe",
            "utools.exe", "quicker.exe", "listary.exe");

        return categories;

        void Add(LauncherAppCategory category, params string[] names)
        {
            foreach (var name in names) categories[name] = category;
        }
    }
}
