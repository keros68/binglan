using BingLan.Core.Desktop;

/// <summary>
/// Rules that decide when the cards must rise into the topmost band because the
/// shell is showing the desktop. The Win32 calls themselves are checked by hand.
/// </summary>
internal static class DesktopSurfaceTests
{
    internal static void TestShownStateAndActions()
    {
        Check(DesktopSurfaceRules.IsDesktopShown(true, 0), "桌面表面已抬高且无普通窗口时应判定为显示桌面");
        Check(!DesktopSurfaceRules.IsDesktopShown(true, 1), "仍有普通窗口在上时不应判定为显示桌面");
        Check(!DesktopSurfaceRules.IsDesktopShown(true, 7), "多个普通窗口在上时不应判定为显示桌面");
        Check(!DesktopSurfaceRules.IsDesktopShown(false, 0), "找不到桌面表面时不应判定为显示桌面");

        Check(DesktopSurfaceRules.ActionFor(false, true) == DesktopSurfaceAction.Raise,
            "未提升且桌面已显示时应提升");
        Check(DesktopSurfaceRules.ActionFor(true, true) == DesktopSurfaceAction.None,
            "已提升且桌面仍显示时应保持");
        Check(DesktopSurfaceRules.ActionFor(true, false) == DesktopSurfaceAction.Lower,
            "已提升且桌面不再显示时应回落");
        Check(DesktopSurfaceRules.ActionFor(false, false) == DesktopSurfaceAction.None,
            "未提升且桌面未显示时应不动");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
