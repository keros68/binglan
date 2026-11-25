using BingLan.Core.TopBar;

namespace BingLan.SmokeTests;

/// <summary>
/// Rows for the top bar's inline search: local matches first, one browser fallback last,
/// and only the browser row when nothing local matches.
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

        Assert(rows.Count == 3, "两个本地结果加一行浏览器兜底应得到三行");
        Assert(rows[0].Kind == TopBarSearchRowKind.Local && rows[0].Title == "报告",
            "本地结果按原顺序排在最前");
        Assert(rows[1].Kind == TopBarSearchRowKind.Local, "第二个本地结果紧随其后");
        Assert(rows[2].Kind == TopBarSearchRowKind.Browser && rows[2].Local is null,
            "浏览器兜底排在最后且不携带本地候选");
        Assert(rows[0].Subtitle.Contains("报告.txt", StringComparison.Ordinal),
            "本地行副标题显示可读的位置");
    }

    private static void TestBrowserRowIsTheOnlyOneWithoutLocalMatches()
    {
        var rows = TopBarSearchRowRules.Compose([], "明天天气");
        Assert(rows.Count == 1, "没有本地结果时应只剩浏览器兜底一行");
        Assert(rows[0].Kind == TopBarSearchRowKind.Browser, "唯一一行应是浏览器兜底");
        Assert(rows[0].Title == "用百度搜索「明天天气」", "兜底行标题应带上搜索词与引擎名");
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
        Assert(url == "https://www.baidu.com/s?wd=a%20b%26c", "查询词应按 URL 规则转义");

        var chinese = TopBarSearchRowRules.BuildBrowserUrl(
            TopBarSearchRowRules.DefaultBrowserUrlTemplate,
            "天气");
        Assert(chinese == "https://www.baidu.com/s?wd=%E5%A4%A9%E6%B0%94",
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
}
