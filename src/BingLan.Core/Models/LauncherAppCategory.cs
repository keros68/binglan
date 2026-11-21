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
    Design = 10
}

public static class LauncherAppCategoryRules
{
    public const int MinimumCategoryItemCount = 3;

    public static readonly LauncherAppCategory[] OrderedCategories =
    [
        LauncherAppCategory.Office,
        LauncherAppCategory.WebAndCommunication,
        LauncherAppCategory.MediaAndEntertainment,
        LauncherAppCategory.Design,
        LauncherAppCategory.Development,
        LauncherAppCategory.Games,
        LauncherAppCategory.SystemTools,
        LauncherAppCategory.Unclassified,
        LauncherAppCategory.Auxiliary
    ];

    private static readonly Dictionary<string, LauncherAppCategory> Executables = BuildExecutableCategories();
    private static readonly string[] AuxiliaryWords =
        ["uninstall", "uninstaller", "setup", "installer", "repair", "diagnostics", "updater", "upgrade"];
    private static readonly string[] AuxiliaryNames =
        ["卸载", "安装程序", "安装向导", "修复工具", "诊断工具", "维护工具", "升级工具", "更新工具", "检查更新"];

    public static string DisplayName(LauncherAppCategory category) => category switch
    {
        LauncherAppCategory.Office => "效率办公",
        LauncherAppCategory.WebAndCommunication => "通讯与浏览",
        LauncherAppCategory.MediaAndEntertainment => "影音娱乐",
        LauncherAppCategory.Design => "设计与创作",
        LauncherAppCategory.Development => "开发工具",
        LauncherAppCategory.Games => "游戏",
        LauncherAppCategory.SystemTools => "系统与工具",
        LauncherAppCategory.Auxiliary => "辅助入口",
        _ => "其他"
    };

    public static string Glyph(LauncherAppCategory category) => category switch
    {
        LauncherAppCategory.Office => "\uE8A5",
        LauncherAppCategory.WebAndCommunication => "\uE8BD",
        LauncherAppCategory.MediaAndEntertainment => "\uE8D6",
        LauncherAppCategory.Design => "\uE790",
        LauncherAppCategory.Development => "\uE943",
        LauncherAppCategory.Games => "\uE7FC",
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
        var tokens = displayName.Split([' ', '\t', '-', '_', '.', '(', ')', '[', ']'],
            StringSplitOptions.RemoveEmptyEntries);
        if (AuxiliaryNames.Any(name => displayName.Contains(name, StringComparison.OrdinalIgnoreCase))
            || tokens.Any(token => AuxiliaryWords.Contains(token, StringComparer.OrdinalIgnoreCase))
            || fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
            || AuxiliaryWords.Contains(Path.GetFileNameWithoutExtension(fileName), StringComparer.OrdinalIgnoreCase)
            || string.Equals(fileName, "msiexec.exe", StringComparison.OrdinalIgnoreCase)
            || HasUninstallArgument(arguments))
        {
            return LauncherAppCategory.Auxiliary;
        }
        if (Executables.TryGetValue(fileName, out var category)) return category;

        // Packaged app identities can outlive changes to the executable's installation path.
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
            || target.Contains("Microsoft.OutlookForWindows_", StringComparison.OrdinalIgnoreCase))
            return LauncherAppCategory.Office;
        if (target.Contains("MSTeams_", StringComparison.OrdinalIgnoreCase)
            || target.Contains("5319275A.WhatsAppDesktop_", StringComparison.OrdinalIgnoreCase))
            return LauncherAppCategory.WebAndCommunication;

        var folders = (startMenuFolder ?? string.Empty).Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (folders.Any(folder => folder.Equals("Microsoft Office", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("WPS Office", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("LibreOffice", StringComparison.OrdinalIgnoreCase)))
            return LauncherAppCategory.Office;
        if (folders.Any(folder => folder.Equals("JetBrains", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Visual Studio Code", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Git", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Python", StringComparison.OrdinalIgnoreCase)))
            return LauncherAppCategory.Development;
        if (folders.Any(folder => folder.Equals("Adobe", StringComparison.OrdinalIgnoreCase)))
            return LauncherAppCategory.Design;
        if (folders.Any(folder => folder.Equals("Windows Accessories", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Windows System", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Windows Administrative Tools", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Windows 附件", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Windows 系统", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("Windows 管理工具", StringComparison.OrdinalIgnoreCase)))
            return LauncherAppCategory.SystemTools;

        return LauncherAppCategory.Unclassified;
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

    private static Dictionary<string, LauncherAppCategory> BuildExecutableCategories()
    {
        var categories = new Dictionary<string, LauncherAppCategory>(StringComparer.OrdinalIgnoreCase);

        Add(LauncherAppCategory.Office,
            "winword.exe", "excel.exe", "powerpnt.exe", "onenote.exe", "outlook.exe", "olk.exe",
            "msaccess.exe", "mspub.exe", "visio.exe", "winproj.exe",
            "wps.exe", "et.exe", "wpp.exe", "wpsoffice.exe", "ksolaunch.exe",
            "soffice.exe", "swriter.exe", "scalc.exe", "simpress.exe",
            "obsidian.exe", "notion.exe", "typora.exe", "zotero.exe", "logseq.exe", "siyuan.exe",
            "acrobat.exe", "acrord32.exe", "sumatrapdf.exe", "foxitpdfreader.exe", "foxitphantompdf.exe",
            "xmind.exe", "calibre.exe", "anki.exe", "evernote.exe", "yinxiang.exe", "yuque.exe",
            "ticktick.exe", "todoist.exe", "youdaonote.exe", "cajviewer.exe");

        Add(LauncherAppCategory.WebAndCommunication,
            "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe",
            "arc.exe", "centbrowser.exe", "360chrome.exe", "360chromex.exe", "360se.exe", "quark.exe",
            "wechat.exe", "weixin.exe", "qq.exe", "tim.exe", "telegram.exe", "discord.exe",
            "slack.exe", "teams.exe", "ms-teams.exe", "zoom.exe", "dingtalk.exe", "dingtalklauncher.exe",
            "lark.exe", "feishu.exe", "wxwork.exe", "wemeetapp.exe", "thunderbird.exe", "foxmail.exe",
            "skype.exe", "whatsapp.exe", "element.exe", "signal.exe");

        Add(LauncherAppCategory.MediaAndEntertainment,
            "vlc.exe", "potplayermini64.exe", "potplayermini.exe", "mpv.exe", "mpc-hc64.exe", "mpc-be64.exe",
            "cloudmusic.exe", "qqmusic.exe", "kugou.exe", "kuwo.exe", "spotify.exe", "itunes.exe",
            "applemusic.exe", "foobar2000.exe", "musicbee.exe", "aimp.exe", "lx-music-desktop.exe",
            "bilibili.exe", "qqlive.exe", "qyclient.exe", "youkumedia.exe", "douyin.exe",
            "kodi.exe", "plex.exe", "jellyfinmediaplayer.exe");

        Add(LauncherAppCategory.Design,
            "photoshop.exe", "illustrator.exe", "indesign.exe", "afterfx.exe",
            "adobe premiere pro.exe", "lightroom.exe", "lightroomclassic.exe", "adobe audition.exe",
            "figma.exe", "blender.exe", "gimp.exe", "gimp-2.10.exe", "gimp-3.exe", "inkscape.exe", "krita.exe",
            "paintdotnet.exe", "mspaint.exe", "obs64.exe", "obs32.exe", "capcut.exe", "jianyingpro.exe",
            "resolve.exe", "audacity.exe", "snipaste.exe", "pixpin.exe", "sharex.exe", "pureref.exe",
            "photo.exe", "designer.exe", "publisher.exe", "aseprite.exe", "cinema 4d.exe", "maya.exe", "3dsmax.exe");

        Add(LauncherAppCategory.Development,
            "code.exe", "cursor.exe", "windsurf.exe", "zed.exe", "devenv.exe",
            "idea64.exe", "pycharm64.exe", "webstorm64.exe", "clion64.exe", "rider64.exe",
            "datagrip64.exe", "goland64.exe", "phpstorm64.exe", "rubymine64.exe", "rustrover64.exe",
            "androidstudio64.exe", "studio64.exe", "rstudio.exe", "r.exe", "rgui.exe",
            "powershell.exe", "pwsh.exe", "cmd.exe", "wt.exe", "windowsterminal.exe", "mintty.exe",
            "putty.exe", "mobaxterm.exe", "xshell.exe", "xftp.exe", "finalshell.exe", "termius.exe",
            "git-bash.exe", "dbeaver.exe", "navicat.exe", "docker desktop.exe",
            "postman.exe", "apifox.exe", "insomnia.exe", "notepad++.exe", "sublime_text.exe",
            "githubdesktop.exe", "sourcetree.exe", "fork.exe", "fiddler.exe", "wireshark.exe");

        Add(LauncherAppCategory.Games,
            "steam.exe", "epicgameslauncher.exe", "galaxyclient.exe",
            "battle.net.exe", "ubisoftconnect.exe", "upc.exe", "eadesktop.exe", "riotclientservices.exe",
            "wegame.exe", "playnite.desktopapp.exe", "playnite.fullscreenapp.exe", "xboxpcapp.exe",
            "uu.exe", "leigod.exe", "prismlauncher.exe", "hmcl.exe", "pcl2.exe", "hyp.exe");

        Add(LauncherAppCategory.SystemTools,
            "explorer.exe", "notepad.exe", "calc.exe", "taskmgr.exe",
            "control.exe", "regedit.exe", "mmc.exe", "msinfo32.exe", "resmon.exe", "cleanmgr.exe",
            "snippingtool.exe", "7zfm.exe", "winrar.exe", "bandizip.exe", "peazip.exe",
            "everything.exe", "powertoys.exe", "procexp.exe", "procexp64.exe", "processhacker.exe", "systeminformer.exe",
            "wiztree.exe", "wiztree64.exe", "treesize.exe", "spacesniffer.exe",
            "onedrive.exe", "baidunetdisk.exe", "aliyundrive.exe", "quarkclouddrive.exe",
            "dropbox.exe", "googledrivefs.exe", "filezilla.exe", "winscp.exe", "syncthing.exe",
            "qbittorrent.exe", "transmission-qt.exe", "motrix.exe", "idman.exe", "thunder.exe",
            "localsend_app.exe", "todesk.exe", "rustdesk.exe", "sunloginclient.exe", "teamviewer.exe",
            "anydesk.exe", "mstsc.exe", "cpuz.exe", "gpuz.exe", "diskinfo64.exe", "hwinfo64.exe",
            "clash-verge.exe", "mihomo-party.exe", "v2rayn.exe", "utools.exe", "quicker.exe", "listary.exe");

        return categories;

        void Add(LauncherAppCategory category, params string[] names)
        {
            foreach (var name in names) categories[name] = category;
        }
    }
}
