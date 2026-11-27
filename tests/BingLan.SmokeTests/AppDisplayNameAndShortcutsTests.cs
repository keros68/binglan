using BingLan.Core.Dock;
using BingLan.Core.Models;

namespace BingLan.SmokeTests;

internal static class AppDisplayNameAndShortcutsTests
{
    public static void Run()
    {
        TestEcommerceClientDisplayNames();
        TestGenericRuntimeAndVersionedDescriptions();
        TestDistinctDescriptionsArePreserved();
        TestTopBarAndDockWindowGroupingIntegration();
        TestControlPanelDisplayNameAndPinDeduplication();
    }

    private static void TestEcommerceClientDisplayNames()
    {
        var cases = new (string ExePath, string? RawName, string Expected)[]
        {
            (@"C:\Program Files (x86)\AliWorkbench\9.28.01N\AliWorkbench.exe", null, "千牛工作台"),
            (@"C:\Program Files (x86)\AliWorkbench\AliWorkbench.exe", "AliWorkbench", "千牛工作台"),
            (@"C:\Program Files (x86)\AliWorkbench\AliWorkbench.exe", "AliWorkbench Application", "千牛工作台"),
            (@"D:\Apps\QianNiu\QianNiu.exe", "QianNiu.exe", "千牛工作台"),
            (@"C:\Program Files (x86)\AliWangWang\AliIM.exe", "AliIM", "阿里旺旺"),
            (@"C:\Program Files (x86)\AliWangWang\AliWangWang.exe", "Alibaba TradeManager", "阿里旺旺"),
            (@"C:\Program Files\CaiNiao\CNPrintClient.exe", null, "菜鸟打印"),
            (@"D:\Pdd\PddWorkbench.exe", "PddWorkbench", "拼多多商家工作台"),
            (@"D:\Pdd\pddwebworkbench.exe", null, "拼多多商家工作台"),
            (@"D:\Pdd\PinduoduoWorkbench.exe", "Pinduoduo Workbench", "拼多多商家工作台"),
            (@"D:\Pdd\PddMerchant.exe", "PddMerchant", "拼多多商家工作台"),
            (@"D:\Pdd\PddIM.exe", null, "拼多多商家客服"),
            (@"D:\Pdd\PDDPrintClient.exe", "PDDPrintClient", "拼多多打印"),
            (@"D:\ByteDance\DouDian.exe", "DouDian", "抖店"),
            (@"D:\ByteDance\DoudianClient.exe", null, "抖店"),
            (@"D:\ByteDance\DoudianFeige.exe", "DoudianFeige", "飞鸽客服"),
            (@"D:\ByteDance\OceanEngine.Feige.exe", null, "飞鸽客服"),
            (@"D:\ByteDance\Feige.exe", "Feige", "飞鸽"),
            (@"D:\JD\JingMai.exe", "JingMai", "京麦"),
            (@"D:\JD\jmworkstation.exe", "jmworkstation", "京麦"),
            (@"D:\JD\JMWorkBench.exe", null, "京麦"),
            (@"D:\JD\JDShopAssistant.exe", null, "京麦"),
            (@"D:\JD\jm_dd_workbench.exe", null, "京东咚咚"),
            (@"D:\JD\dd_workbench_jd.exe", "dd_workbench_jd", "京东咚咚"),
            (@"D:\JD\JDDongDong.exe", "JDDongDong", "京东咚咚"),
            (@"D:\Kuaishou\KuaishouShop.exe", null, "快手小店"),
            (@"D:\Kuaishou\kuaishou_shop.exe", "kuaishou_shop", "快手小店"),
            (@"D:\Kuaishou\KsImClient.exe", null, "快手客服"),
            (@"D:\Xhs\QianFan.exe", "QianFan", "小红书千帆"),
            (@"D:\Xhs\XhsQianFan.exe", null, "小红书千帆"),
            (@"D:\ERP\WangDianTong.exe", "WangDianTong", "旺店通"),
            (@"D:\ERP\WdtErp.exe", null, "旺店通"),
            (@"D:\ERP\WdtClient.exe", "WdtClient", "旺店通"),
            (@"D:\ERP\JuShuiTan.exe", null, "聚水潭")
        };

        foreach (var (exePath, rawName, expected) in cases)
        {
            var actual = AppDisplayNameRules.Normalize(rawName, exePath);
            Assert(actual == expected,
                $"电商客户端显示名应规范化为“{expected}”，实际“{actual ?? "<null>"}”（exe={exePath}, raw={rawName ?? "<null>"}）");
        }
    }

    private static void TestGenericRuntimeAndVersionedDescriptions()
    {
        Assert(AppDisplayNameRules.Normalize("Electron", @"D:\Pdd\PddWorkbench.exe") == "拼多多商家工作台",
            "Electron 壳电商客户端在 FileDescription 为 Electron 时应解析为中文名");
        Assert(AppDisplayNameRules.Normalize("Chromium", @"D:\ByteDance\DouDian.exe") == "抖店",
            "Chromium 壳电商客户端应解析为中文名");
        Assert(AppDisplayNameRules.Normalize("AliWorkbench 9.28.01N", @"C:\Ali\AliWorkbench.exe") == "千牛工作台",
            "带版本号后缀的原始进程描述应解析为中文名");
        Assert(AppDisplayNameRules.Normalize("DouDian Client", @"D:\ByteDance\DouDian.exe") == "抖店",
            "带 Client 后缀的原始进程描述应解析为中文名");
        Assert(AppDisplayNameRules.Normalize("JingMai v6.1", @"D:\JD\JingMai.exe") == "京麦",
            "带 v 版本前缀的原始进程描述应解析为中文名");
        Assert(AppDisplayNameRules.Normalize("Electron", @"D:\Unknown\CustomElectronApp.exe") == "Electron",
            "未知程序的 Electron 描述不应被误清空或改写");
    }

    private static void TestDistinctDescriptionsArePreserved()
    {
        Assert(AppDisplayNameRules.Normalize("My Custom Editor", @"D:\Tools\Code.exe") == "My Custom Editor",
            "通用短文件名 Code.exe 带独立描述时应保留原描述");
        Assert(AppDisplayNameRules.Normalize("Easy Timer", @"D:\Tools\et.exe") == "Easy Timer",
            "短文件名 et.exe 带独立描述时不应被误判为 WPS 表格");
        Assert(AppDisplayNameRules.Normalize("飞鸽传书", @"D:\Tools\Feige.exe") == "飞鸽传书",
            "Feige.exe 已有明确描述“飞鸽传书”时应保留原名避免与电商飞鸽混淆");
        Assert(AppDisplayNameRules.Normalize("飞鸽客服工作台", @"D:\ByteDance\Feige.exe") == "飞鸽客服工作台",
            "Feige.exe 已有明确中文描述“飞鸽客服工作台”时应保留原名");
        Assert(AppDisplayNameRules.Normalize("百度千帆", @"D:\Tools\QianFan.exe") == "百度千帆",
            "QianFan.exe 已有独立产品描述时应保留原名");
    }

    private static void TestTopBarAndDockWindowGroupingIntegration()
    {
        var windows = new[]
        {
            new TrackedWindow(101, 1001, "千牛接待中心", null, @"C:\Ali\AliWorkbench.exe", false, false),
            new TrackedWindow(102, 1002, "商家聊天", null, @"D:\Pdd\PddWorkbench.exe", false, false),
            new TrackedWindow(103, 1003, "抖店工作台", null, @"D:\ByteDance\DouDian.exe", false, false),
            new TrackedWindow(104, 1004, "京麦", null, @"D:\JD\jmworkstation.exe", false, false),
            new TrackedWindow(105, 1005, "Control Panel", null, @"C:\Windows\System32\control.exe", false, false)
        };
        var groups = WindowGrouping.Group(windows);
        Assert(groups.Count == 5, "五个不同程序应分出五个窗口组");
        Assert(groups[0].DisplayName == "千牛工作台"
            && groups[1].DisplayName == "拼多多商家工作台"
            && groups[2].DisplayName == "抖店"
            && groups[3].DisplayName == "京麦"
            && groups[4].DisplayName == "控制面板",
            $"Dock 窗口分组应显示中文名，实际：{string.Join(" / ", groups.Select(group => group.DisplayName))}");

        Assert(AttentionFlashRules.ChooseDisplayName("Electron", "PddWorkbench", @"D:\Pdd\PddWorkbench.exe", "") == "拼多多商家工作台",
            "顶栏消息提示应将 Electron 壳的拼多多商家工作台规范化为中文名");
        Assert(AttentionFlashRules.ChooseDisplayName("AliWorkbench Application", "AliWorkbench", @"C:\Ali\AliWorkbench.exe", "") == "千牛工作台",
            "顶栏消息提示应将千牛工作台规范化为中文名");
        Assert(AttentionFlashRules.ChooseDisplayName(null, null, @"D:\JD\jm_dd_workbench.exe", "") == "京东咚咚",
            "顶栏消息提示应将京东咚咚进程规范化为中文名");
        Assert(AttentionFlashRules.ChooseDisplayName("DouDian", null, @"D:\ByteDance\DouDian.exe", "") == "抖店",
            "顶栏消息提示应将抖店规范化为中文名");
    }

    private static void TestControlPanelDisplayNameAndPinDeduplication()
    {
        const string controlPath = @"C:\Windows\System32\control.exe";
        Assert(AppDisplayNameRules.TryResolveCanonicalName(controlPath) == "控制面板",
            "control.exe 应规范化为“控制面板”");
        Assert(AppDisplayNameRules.Normalize("Windows Control Panel", controlPath) == "控制面板",
            "英文 FileDescription Windows Control Panel 应规范化为“控制面板”");
        Assert(AppDisplayNameRules.Normalize("Control Panel", controlPath) == "控制面板",
            "英文快捷方式名 Control Panel 应规范化为“控制面板”");
        Assert(LauncherAppCategoryRules.Classify("控制面板", controlPath, null) == LauncherAppCategory.SystemTools,
            "控制面板在总览卡中应归类为系统工具");

        var pin = DockPinRules.CreateFromExecutable(controlPath);
        Assert(pin is not null, "control.exe 应可生成 Dock 固定项");
        pin!.DisplayName = AppDisplayNameRules.TryResolveCanonicalName(pin.ExecutablePath) ?? "控制面板";

        var dockState = new DockState();
        Assert(DockPinRules.Pin(dockState, pin), "控制面板应可固定到 Dock");
        var duplicate = DockPinRules.CreateFromExecutable(@"c:\windows\system32\CONTROL.EXE")!;
        duplicate.DisplayName = "Control Panel";
        Assert(!DockPinRules.Pin(dockState, duplicate), "大小写不同的 control.exe 不应重复固定到 Dock");
        Assert(dockState.PinnedApps.Count == 1 && dockState.PinnedApps[0].DisplayName == "控制面板",
            "Dock 固定列表应只保留一条“控制面板”记录");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
