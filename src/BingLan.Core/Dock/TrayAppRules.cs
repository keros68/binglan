using BingLan.Core.Models;

namespace BingLan.Core.Dock;

/// <summary>
/// A top-level window of a running process, carrying only the styles needed to
/// judge whether it is the main window a dock click should reveal.
/// </summary>
public sealed record TrayWindowSnapshot(
    nint Handle,
    string Title,
    bool IsAppWindow,
    bool IsToolWindow,
    bool IsVisible,
    bool IsMinimized,
    long Area);

/// <summary>
/// Apps closed to the tray (WeChat, QQ) keep running with no visible window, so
/// their dock items look pinned-but-not-running. Clicking should then reveal the
/// hidden window instead of starting a second instance that asks for a login.
/// </summary>
public static class TrayAppRules
{
    public static bool ShouldRevealInsteadOfLaunch(DockPinnedApp pinned)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        return !string.IsNullOrWhiteSpace(pinned.ExecutablePath);
    }

    public static bool MatchesProcess(DockPinnedApp pinned, string? processExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        if (string.IsNullOrWhiteSpace(pinned.ExecutablePath)
            || string.IsNullOrWhiteSpace(processExecutablePath))
        {
            return false;
        }

        return string.Equals(
            WindowGrouping.NormalizeExecutablePath(pinned.ExecutablePath),
            WindowGrouping.NormalizeExecutablePath(processExecutablePath),
            StringComparison.OrdinalIgnoreCase);
    }

    public static TrayWindowSnapshot? PickRevealTarget(IReadOnlyList<TrayWindowSnapshot> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        return windows
            .Where(window => !window.IsToolWindow
                && !window.IsVisible
                && (window.IsAppWindow || !string.IsNullOrWhiteSpace(window.Title)))
            .OrderByDescending(window => window.IsAppWindow)
            .ThenByDescending(window => window.Area)
            .FirstOrDefault();
    }
}
