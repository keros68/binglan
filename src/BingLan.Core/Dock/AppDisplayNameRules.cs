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
        ["control.exe"] = "控制面板",
        ["AliWorkbench.exe"] = "千牛工作台",
        ["QianNiu.exe"] = "千牛工作台",
        ["AliIM.exe"] = "阿里旺旺",
        ["AliWangWang.exe"] = "阿里旺旺",
        ["CNPrintClient.exe"] = "菜鸟打印",
        ["PddWorkbench.exe"] = "拼多多商家工作台",
        ["PddWebWorkbench.exe"] = "拼多多商家工作台",
        ["PinduoduoWorkbench.exe"] = "拼多多商家工作台",
        ["PddMerchant.exe"] = "拼多多商家工作台",
        ["PddIM.exe"] = "拼多多商家客服",
        ["PDDPrintClient.exe"] = "拼多多打印",
        ["DouDian.exe"] = "抖店",
        ["DoudianClient.exe"] = "抖店",
        ["DoudianWorkbench.exe"] = "抖店",
        ["DoudianFeige.exe"] = "飞鸽客服",
        ["OceanEngine.Feige.exe"] = "飞鸽客服",
        ["Feige.exe"] = "飞鸽",
        ["JingMai.exe"] = "京麦",
        ["JMWorkstation.exe"] = "京麦",
        ["JMWorkBench.exe"] = "京麦",
        ["JDWorkbench.exe"] = "京麦",
        ["JDShopAssistant.exe"] = "京麦",
        ["jm_dd_workbench.exe"] = "京东咚咚",
        ["dd_workbench_jd.exe"] = "京东咚咚",
        ["JDDongDong.exe"] = "京东咚咚",
        ["DongDong.exe"] = "京东咚咚",
        ["KuaishouShop.exe"] = "快手小店",
        ["kuaishou_shop.exe"] = "快手小店",
        ["KwaiShop.exe"] = "快手小店",
        ["KsImClient.exe"] = "快手客服",
        ["QianFan.exe"] = "小红书千帆",
        ["XhsQianFan.exe"] = "小红书千帆",
        ["WangDianTong.exe"] = "旺店通",
        ["WdtErp.exe"] = "旺店通",
        ["WdtClient.exe"] = "旺店通",
        ["JuShuiTan.exe"] = "聚水潭",
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
        ["Windows Explorer"] = "文件资源管理器",
        ["Windows Control Panel"] = "控制面板",
        ["Control Panel"] = "控制面板",
        ["AliWorkbench"] = "千牛工作台",
        ["AliWorkbench Application"] = "千牛工作台",
        ["QianNiu"] = "千牛工作台",
        ["QianNiu Workbench"] = "千牛工作台",
        ["Taobao Seller Workbench"] = "千牛工作台",
        ["AliIM"] = "阿里旺旺",
        ["AliWangWang"] = "阿里旺旺",
        ["Alibaba TradeManager"] = "阿里旺旺",
        ["CNPrintClient"] = "菜鸟打印",
        ["PddWorkbench"] = "拼多多商家工作台",
        ["PddWebWorkbench"] = "拼多多商家工作台",
        ["PinduoduoWorkbench"] = "拼多多商家工作台",
        ["Pinduoduo Workbench"] = "拼多多商家工作台",
        ["PDD Workbench"] = "拼多多商家工作台",
        ["PddMerchant"] = "拼多多商家工作台",
        ["PddIM"] = "拼多多商家客服",
        ["PDDPrintClient"] = "拼多多打印",
        ["DouDian"] = "抖店",
        ["DoudianClient"] = "抖店",
        ["DoudianWorkbench"] = "抖店",
        ["DoudianFeige"] = "飞鸽客服",
        ["OceanEngine.Feige"] = "飞鸽客服",
        ["Feige"] = "飞鸽",
        ["JingMai"] = "京麦",
        ["JMWorkstation"] = "京麦",
        ["JMWorkBench"] = "京麦",
        ["JDWorkbench"] = "京麦",
        ["JDShopAssistant"] = "京麦",
        ["jm_dd_workbench"] = "京东咚咚",
        ["dd_workbench_jd"] = "京东咚咚",
        ["JDDongDong"] = "京东咚咚",
        ["DongDong"] = "京东咚咚",
        ["KuaishouShop"] = "快手小店",
        ["kuaishou_shop"] = "快手小店",
        ["KwaiShop"] = "快手小店",
        ["KsImClient"] = "快手客服",
        ["QianFan"] = "小红书千帆",
        ["XhsQianFan"] = "小红书千帆",
        ["WangDianTong"] = "旺店通",
        ["WdtErp"] = "旺店通",
        ["WdtClient"] = "旺店通",
        ["JuShuiTan"] = "聚水潭"
    };

    // Electron/CEF/desktop wrappers often ship with a generic framework FileDescription instead
    // of the product name; treat these as non-distinct so known executables resolve cleanly.
    private static readonly HashSet<string> GenericRuntimeDescriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Electron",
        "Chromium",
        "CEF",
        "CefClient",
        "CefSharp",
        "nwjs",
        "Application",
        "Main",
        "Client",
        "Launcher",
        "Desktop",
        "App"
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
                && IsNonDistinctRawName(cleaned, exeStem)
                && KnownExecutableNames.TryGetValue(exeName, out var byExe))
            {
                return byExe;
            }
        }

        return null;
    }

    private static bool IsNonDistinctRawName(string cleaned, string exeStem)
    {
        if (cleaned.Length == 0
            || string.Equals(cleaned, exeStem, StringComparison.OrdinalIgnoreCase)
            || GenericRuntimeDescriptions.Contains(cleaned))
        {
            return true;
        }

        if (exeStem.Length >= 4
            && cleaned.StartsWith(exeStem, StringComparison.OrdinalIgnoreCase)
            && cleaned.Length > exeStem.Length
            && char.IsWhiteSpace(cleaned[exeStem.Length]))
        {
            var suffix = cleaned[(exeStem.Length + 1)..].Trim();
            if (suffix.Length > 0
                && (GenericRuntimeDescriptions.Contains(suffix)
                    || char.IsDigit(suffix[0])
                    || (suffix[0] is 'v' or 'V' && suffix.Length > 1 && char.IsDigit(suffix[1]))))
            {
                return true;
            }
        }

        return false;
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
