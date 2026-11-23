namespace BingLan.Core.Dock;

/// <summary>
/// The settings-page wording that names the Dock's screen edge, so it reads "底部", "左侧"
/// or "右侧" to match where the Dock actually sits. The bottom wording is the original text.
/// </summary>
public static class DockEdgeWording
{
    public static string Side(DockEdge edge) => edge switch
    {
        DockEdge.Left => "左侧",
        DockEdge.Right => "右侧",
        _ => "底部"
    };

    private static string Border(DockEdge edge) => edge switch
    {
        DockEdge.Left => "左边",
        DockEdge.Right => "右边",
        _ => "底边"
    };

    public static string PageSubtitle(DockEdge edge) =>
        $"屏幕{Side(edge)}的应用栏：显示哪些应用、怎么出现、多大。";

    public static string EnabledDescription(DockEdge edge) =>
        $"屏幕{Side(edge)}居中显示固定的应用和正在运行的应用";

    public static string ReserveLabel(DockEdge edge) => $"始终显示，保留{Side(edge)}空间";

    public static string ReserveAutomationName(DockEdge edge) => $"始终显示，并保留屏幕{Side(edge)}空间";

    public static string AdvancedCaption(DockEdge edge) => edge == DockEdge.Bottom
        ? "入口按钮、缩放与放大强度、距底高度、唤出与最大化行为及隐藏应用"
        : "入口按钮、缩放与放大强度、距边缘距离、唤出与最大化行为及隐藏应用";

    public static string GapHeader(DockEdge edge) => edge == DockEdge.Bottom ? "距底部高度" : "距屏幕边缘距离";

    public static string GapDescription(DockEdge edge) => edge == DockEdge.Bottom
        ? "Dock 底边到屏幕底边的距离；任务栏常显时从任务栏上沿算起"
        : $"Dock 到屏幕{Border(edge)}的距离；任务栏常显时不含任务栏占用的部分";

    public static string GapAutomationName(DockEdge edge) => edge == DockEdge.Bottom
        ? "Dock 距底部高度"
        : "Dock 距屏幕边缘距离";

    public static string MaximizeReleaseHeader(DockEdge edge) => $"最大化窗口时临时隐藏并释放{Side(edge)}空间";

    public static string MaximizeReleaseDescription(DockEdge edge) =>
        $"开启后，最大化窗口时 Dock 会临时隐藏并释放{Side(edge)}空间，鼠标移到屏幕{Border(edge)}或把手可临时唤出；希望最大化时也始终显示 Dock 请关闭此项";
}
