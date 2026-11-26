namespace BingLan.Core.Dock;

/// <summary>
/// Normalizes raw Windows executable names and PE FileDescription values into concise,
/// user-facing display names (for example "Weixin.exe"/"Weixin" → "微信", "WXWork.exe"/"WeChat Work" → "企业微信").
/// </summary>
public static class AppDisplayNameRules
{
    private static readonly Dictionary<string, string> KnownExecutableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Weixin.exe"] = "微信",
        ["WeChat.exe"] = "微信",
        ["WeChatAppEx.exe"] = "微信小程序",
        ["WXWork.exe"] = "企业微信",
        ["DingTalk.exe"] = "钉钉",
        ["DingtalkLauncher.exe"] = "钉钉",
        ["Feishu.exe"] = "飞书",
        ["Lark.exe"] = "Lark",
        ["QQ.exe"] = "QQ",
        ["QQNT.exe"] = "QQ",
        ["TIM.exe"] = "TIM",
        ["QQMusic.exe"] = "QQ音乐",
        ["cloudmusic.exe"] = "网易云音乐",
        ["KuGou.exe"] = "酷狗音乐",
        ["BaiduNetdisk.exe"] = "百度网盘",
        ["AliyunDrive.exe"] = "阿里云盘",
        ["QuarkCloudDrive.exe"] = "夸克网盘",
        ["wps.exe"] = "WPS 文字",
        ["et.exe"] = "WPS 表格",
        ["wpp.exe"] = "WPS 演示",
        ["wpsoffice.exe"] = "WPS Office",
        ["ksolaunch.exe"] = "WPS Office",
        ["Douyin.exe"] = "抖音",
        ["bilibili.exe"] = "哔哩哔哩",
        ["SunloginClient.exe"] = "向日葵",
        ["ToDesk.exe"] = "ToDesk",
        ["explorer.exe"] = "文件资源管理器",
        ["msedge.exe"] = "Edge",
        ["chrome.exe"] = "Chrome",
        ["firefox.exe"] = "Firefox",
        ["Code.exe"] = "VS Code",
        ["ms-teams.exe"] = "Teams",
        ["Teams.exe"] = "Teams",
        ["Telegram.exe"] = "Telegram",
        ["Discord.exe"] = "Discord",
        ["Slack.exe"] = "Slack",
        ["Notion.exe"] = "Notion",
        ["Obsidian.exe"] = "Obsidian"
    };

    private static readonly Dictionary<string, string> KnownRawNameAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Weixin"] = "微信",
        ["WeChat"] = "微信",
        ["WXWork"] = "企业微信",
        ["WeChat Work"] = "企业微信",
        ["WeChatWork"] = "企业微信",
        ["WeCom"] = "企业微信",
        ["DingTalk"] = "钉钉",
        ["DingtalkLauncher"] = "钉钉",
        ["Feishu"] = "飞书",
        ["QQNT"] = "QQ",
        ["QQMusic"] = "QQ音乐",
        ["BaiduNetdisk"] = "百度网盘",
        ["AliyunDrive"] = "阿里云盘",
        ["QuarkCloudDrive"] = "夸克网盘",
        ["SunloginClient"] = "向日葵",
        ["cloudmusic"] = "网易云音乐",
        ["WPS Writer"] = "WPS 文字",
        ["WPS Spreadsheets"] = "WPS 表格",
        ["WPS Presentation"] = "WPS 演示",
        ["Windows Explorer"] = "文件资源管理器"
    };

    public static string? TryResolveCanonicalName(string? executablePath, string? rawName = null)
    {
        var cleaned = string.IsNullOrWhiteSpace(rawName) ? string.Empty : CleanRaw(rawName);
        if (cleaned.Length > 0 && KnownRawNameAliases.TryGetValue(cleaned, out var byAlias))
        {
            return byAlias;
        }

        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var exeName = Path.GetFileName(executablePath.Trim().Trim('"'));
            var exeStem = Path.GetFileNameWithoutExtension(exeName);
            // Only map by executable filename when no distinct descriptive name was supplied,
            // so a custom tool sharing a generic basename (e.g. Code.exe / et.exe) keeps its FileDescription.
            if (!string.IsNullOrWhiteSpace(exeName)
                && (cleaned.Length == 0 || string.Equals(cleaned, exeStem, StringComparison.OrdinalIgnoreCase))
                && KnownExecutableNames.TryGetValue(exeName, out var byExe))
            {
                return byExe;
            }
        }

        return null;
    }

    public static string? Normalize(string? rawName, string? executablePath = null)
    {
        if (TryResolveCanonicalName(executablePath, rawName) is { } canonical)
        {
            return canonical;
        }

        if (string.IsNullOrWhiteSpace(rawName))
        {
            return null;
        }

        var cleaned = CleanRaw(rawName);
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string CleanRaw(string rawName)
    {
        var trimmed = rawName.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 4)
        {
            trimmed = trimmed[..^4].Trim();
        }
        return trimmed;
    }
}
