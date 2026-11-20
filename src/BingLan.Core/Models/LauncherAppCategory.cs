namespace BingLan.Core.Models;

public enum LauncherAppCategory
{
    Unclassified,
    Office,
    WebAndCommunication,
    StorageAndTransfer,
    MediaAndDesign,
    MapsAndEngineering,
    Development,
    Games,
    SystemTools,
    Auxiliary
}

public static class LauncherAppCategoryRules
{
    private static readonly Dictionary<string, LauncherAppCategory> Executables = BuildExecutableCategories();
    private static readonly string[] AuxiliaryWords =
        ["uninstall", "uninstaller", "setup", "installer", "repair", "diagnostics", "updater", "upgrade"];
    private static readonly string[] AuxiliaryNames =
        ["卸载", "安装程序", "安装向导", "修复工具", "诊断工具", "维护工具", "升级工具", "更新工具", "检查更新"];

    public static string DisplayName(LauncherAppCategory category) => category switch
    {
        LauncherAppCategory.Office => "办公协作",
        LauncherAppCategory.WebAndCommunication => "浏览与沟通",
        LauncherAppCategory.StorageAndTransfer => "网盘与传输",
        LauncherAppCategory.MediaAndDesign => "影音设计",
        LauncherAppCategory.MapsAndEngineering => "地图与工程",
        LauncherAppCategory.Development => "开发与终端",
        LauncherAppCategory.Games => "游戏",
        LauncherAppCategory.SystemTools => "系统工具",
        LauncherAppCategory.Auxiliary => "辅助入口",
        _ => "未分类"
    };

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
            || target.Contains("Microsoft.WindowsNotepad_", StringComparison.OrdinalIgnoreCase))
            return LauncherAppCategory.SystemTools;
        if (target.Contains("Microsoft.WindowsTerminal_", StringComparison.OrdinalIgnoreCase))
            return LauncherAppCategory.Development;
        if (target.Contains("Microsoft.Windows.Photos_", StringComparison.OrdinalIgnoreCase)
            || target.Contains("Microsoft.ZuneMusic_", StringComparison.OrdinalIgnoreCase)
            || target.Contains("Microsoft.ZuneVideo_", StringComparison.OrdinalIgnoreCase))
            return LauncherAppCategory.MediaAndDesign;

        var folders = (startMenuFolder ?? string.Empty).Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (folders.Any(folder => folder.Equals("Microsoft Office", StringComparison.OrdinalIgnoreCase)))
            return LauncherAppCategory.Office;
        if (folders.Any(folder => folder.Equals("ArcGIS", StringComparison.OrdinalIgnoreCase)
            || folder.Equals("QGIS", StringComparison.OrdinalIgnoreCase)))
            return LauncherAppCategory.MapsAndEngineering;
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
        Add(LauncherAppCategory.Office, "winword.exe", "excel.exe", "powerpnt.exe", "onenote.exe", "outlook.exe",
            "wps.exe", "et.exe", "wpp.exe", "wpsoffice.exe", "soffice.exe", "obsidian.exe", "notion.exe",
            "zotero.exe", "acrobat.exe", "acrord32.exe", "sumatrapdf.exe");
        Add(LauncherAppCategory.WebAndCommunication, "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe",
            "brave.exe", "vivaldi.exe", "wechat.exe", "weixin.exe", "qq.exe", "tim.exe", "telegram.exe",
            "discord.exe", "slack.exe", "teams.exe", "ms-teams.exe", "zoom.exe", "dingtalk.exe", "lark.exe",
            "feishu.exe", "wxwork.exe", "thunderbird.exe");
        Add(LauncherAppCategory.StorageAndTransfer, "onedrive.exe", "baidunetdisk.exe", "aliyundrive.exe",
            "quarkclouddrive.exe", "dropbox.exe", "googledrivefs.exe", "filezilla.exe", "winscp.exe",
            "syncthing.exe", "qbittorrent.exe", "transmission-qt.exe", "thunder.exe", "localsend_app.exe",
            "todesk.exe", "rustdesk.exe", "sunloginclient.exe", "teamviewer.exe", "mstsc.exe");
        Add(LauncherAppCategory.MediaAndDesign, "photoshop.exe", "illustrator.exe", "indesign.exe",
            "afterfx.exe", "adobe premiere pro.exe", "lightroom.exe", "figma.exe", "blender.exe",
            "gimp.exe", "inkscape.exe", "krita.exe", "obs64.exe", "obs32.exe", "vlc.exe", "potplayermini64.exe",
            "potplayermini.exe", "mpv.exe", "cloudmusic.exe", "qqmusic.exe", "spotify.exe", "capcut.exe",
            "jianyingpro.exe", "audacity.exe", "resolve.exe", "mspaint.exe");
        Add(LauncherAppCategory.MapsAndEngineering, "arcgispro.exe", "arcmap.exe", "arccatalog.exe",
            "qgis-bin.exe", "qgis-ltr-bin.exe", "global_mapper.exe", "global_mapper64.exe", "googleearth.exe",
            "acad.exe", "civil3d.exe", "revit.exe", "sketchup.exe", "sldworks.exe", "surfer.exe",
            "envi.exe", "cloudcompare.exe", "metashape.exe");
        Add(LauncherAppCategory.Development, "code.exe", "cursor.exe", "windsurf.exe", "devenv.exe",
            "idea64.exe", "pycharm64.exe", "webstorm64.exe", "clion64.exe", "rider64.exe",
            "datagrip64.exe", "androidstudio64.exe", "studio64.exe", "rstudio.exe", "r.exe", "rgui.exe",
            "powershell.exe", "pwsh.exe", "cmd.exe", "wt.exe", "windowsterminal.exe", "mintty.exe",
            "putty.exe", "mobaxterm.exe", "git-bash.exe", "dbeaver.exe", "docker desktop.exe",
            "postman.exe", "notepad++.exe", "sublime_text.exe");
        Add(LauncherAppCategory.Games, "steam.exe", "epicgameslauncher.exe", "galaxyclient.exe",
            "battle.net.exe", "ubisoftconnect.exe", "upc.exe", "eadesktop.exe", "riotclientservices.exe",
            "wegame.exe", "playnite.desktopapp.exe", "playnite.fullscreenapp.exe");
        Add(LauncherAppCategory.SystemTools, "explorer.exe", "notepad.exe", "calc.exe", "taskmgr.exe",
            "control.exe", "regedit.exe", "mmc.exe", "msinfo32.exe", "resmon.exe", "cleanmgr.exe",
            "snippingtool.exe", "7zfm.exe", "winrar.exe", "bandizip.exe", "peazip.exe",
            "everything.exe", "powertoys.exe", "procexp.exe", "procexp64.exe", "processhacker.exe",
            "wiztree.exe", "treesize.exe");
        return categories;

        void Add(LauncherAppCategory category, params string[] names)
        {
            foreach (var name in names) categories.Add(name, category);
        }
    }
}
