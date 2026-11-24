using BingLan.Core.Models;

namespace BingLan.Core.Dock;

/// <summary>
/// Safety boundaries and process-matching rules for quitting an application from the Dock
/// context menu (including tray-resident pinned apps and launcher-stub child processes).
/// </summary>
public static class DockQuitRules
{
    private static readonly HashSet<string> ProtectedExecutableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe",
        "applicationframehost.exe",
        "svchost.exe",
        "csrss.exe",
        "dwm.exe",
        "lsass.exe",
        "winlogon.exe",
        "services.exe",
        "smss.exe",
        "sihost.exe",
        "shellexperiencehost.exe",
        "startmenuexperiencehost.exe",
        "searchhost.exe",
        "textinputhost.exe",
        "runtimebroker.exe",
        "conhost.exe",
        "taskhostw.exe",
        "fontdrvhost.exe",
        "wininit.exe",
        "system",
        "idle",
        "binglan.exe"
    };

    private static readonly HashSet<string> ProtectedAumids = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.Windows.Explorer",
        "Microsoft.Windows.ShellExperienceHost_cw5n1h2txyewy!App",
        "Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy!App"
    };

    private static readonly HashSet<string> BrowserHostExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge.exe",
        "chrome.exe",
        "brave.exe",
        "opera.exe",
        "vivaldi.exe",
        "firefox.exe",
        "chromium.exe",
        "360chrome.exe",
        "360chromex.exe",
        "qqbrowser.exe"
    };

    /// <summary>
    /// Whether an executable is a multi-app browser host that can host independent PWAs
    /// inside the same executable image.
    /// </summary>
    public static bool IsBrowserHostExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }
        var fileName = Path.GetFileName(WindowGrouping.NormalizeExecutablePath(executablePath));
        return !string.IsNullOrEmpty(fileName) && BrowserHostExecutables.Contains(fileName);
    }

    /// <summary>
    /// Whether a Dock item represents a browser-hosted PWA/web-app shortcut whose process
    /// image is shared with the browser and other PWAs (so process termination would close
    /// unrelated browser windows).
    /// </summary>
    public static bool IsSharedBrowserPwa(DockItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var exe = PrimaryExecutableOf(item);
        if (!IsBrowserHostExecutable(exe))
        {
            return false;
        }
        if (item.Pinned is { Arguments: { Length: > 0 } args }
            && (args.Contains("--app-id=", StringComparison.OrdinalIgnoreCase)
                || args.Contains("--app=", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        if (item.Key.StartsWith("aumid:", StringComparison.OrdinalIgnoreCase))
        {
            var aumid = item.Key["aumid:".Length..];
            if (aumid.Contains("_crx_", StringComparison.OrdinalIgnoreCase)
                || aumid.Contains("Chrome._crx_", StringComparison.OrdinalIgnoreCase)
                || aumid.Contains("MSEdge._crx_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether another distinct Dock item shares the same executable path as <paramref name="item"/>,
    /// indicating that terminating by executable path would affect another app (for example a browser
    /// and its PWA running side by side).
    /// </summary>
    public static bool SharesExecutableWithOtherItem(DockItem item, IReadOnlyList<DockItem>? allItems)
    {
        if (allItems is null || allItems.Count <= 1)
        {
            return false;
        }
        var targetExe = PrimaryExecutableOf(item);
        if (string.IsNullOrWhiteSpace(targetExe))
        {
            return false;
        }
        foreach (var other in allItems)
        {
            if (ReferenceEquals(other, item)
                || string.Equals(other.Key, item.Key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var otherExe = PrimaryExecutableOf(other);
            if (!string.IsNullOrWhiteSpace(otherExe)
                && string.Equals(targetExe, otherExe, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string? PrimaryExecutableOf(DockItem item)
    {
        var candidate = item.Pinned?.ExecutablePath
            ?? item.Group?.Windows
                .Select(window => window.ExecutablePath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        return string.IsNullOrWhiteSpace(candidate)
            ? null
            : WindowGrouping.NormalizeExecutablePath(candidate);
    }

    /// <summary>
    /// Whether an executable belongs to Windows Shell, a critical system host, or BingLan itself
    /// and must never be terminated from the Dock.
    /// </summary>
    public static bool IsProtectedExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var normalized = WindowGrouping.NormalizeExecutablePath(executablePath);
        var fileName = Path.GetFileName(normalized);
        return !string.IsNullOrEmpty(fileName) && ProtectedExecutableNames.Contains(fileName);
    }

    public static bool IsProtectedAumid(string? appUserModelId) =>
        !string.IsNullOrWhiteSpace(appUserModelId)
        && ProtectedAumids.Contains(appUserModelId.Trim());

    /// <summary>
    /// Whether the Dock context menu may offer a "quit application" action for this item.
    /// Excludes pinned files/folders, Windows Explorer, BingLan itself, system hosts, and
    /// apps sharing a browser host executable with other Dock items (such as PWAs).
    /// </summary>
    public static bool CanOfferQuit(DockItem item, IReadOnlyList<DockItem>? allItems = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Pinned is { } pin && DockPinRules.IsPathPin(pin))
        {
            return false;
        }
        if (IsSharedBrowserPwa(item) || SharesExecutableWithOtherItem(item, allItems))
        {
            return false;
        }

        if (item.Pinned is not null && !string.IsNullOrWhiteSpace(item.Pinned.AppUserModelId))
        {
            return !IsProtectedAumid(item.Pinned.AppUserModelId);
        }

        if (item.Group is { Windows.Count: > 0 } group)
        {
            var hasNonProtectedExe = group.Windows.Any(window =>
                !string.IsNullOrWhiteSpace(window.ExecutablePath)
                && !IsProtectedExecutable(window.ExecutablePath));
            var hasNonProtectedAumid = group.Windows.Any(window =>
                !string.IsNullOrWhiteSpace(window.AppUserModelId)
                && !IsProtectedAumid(window.AppUserModelId));
            return hasNonProtectedExe || hasNonProtectedAumid;
        }

        return item.Pinned is { ExecutablePath: { Length: > 0 } exe }
            && !IsProtectedExecutable(exe);
    }

    /// <summary>
    /// Whether a running process matches the Dock item's executable, launcher-stub child
    /// directory, or packaged AUMID, while unconditionally refusing protected executables.
    /// </summary>
    public static bool MatchesProcess(
        DockPinnedApp? pinned,
        IReadOnlySet<string> groupExecutablePaths,
        IReadOnlySet<string> groupAumids,
        string? candidateExecutablePath,
        string? candidateAumid)
    {
        if (IsProtectedExecutable(candidateExecutablePath) || IsProtectedAumid(candidateAumid))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(candidateExecutablePath))
        {
            var normalized = WindowGrouping.NormalizeExecutablePath(candidateExecutablePath);
            if (groupExecutablePaths.Contains(normalized))
            {
                return true;
            }
            if (pinned is not null && DockPinRules.MatchesExecutablePath(pinned, normalized))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(candidateAumid))
        {
            var trimmed = candidateAumid.Trim();
            if (groupAumids.Contains(trimmed))
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(pinned?.AppUserModelId)
                && string.Equals(pinned.AppUserModelId.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
