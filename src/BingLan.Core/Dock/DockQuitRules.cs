using BingLan.Core.Models;

namespace BingLan.Core.Dock;

/// <summary>
/// Process metadata snapshot used by <see cref="DockQuitRules.SelectProcessesToTerminate"/>
/// to resolve an application's full process set (including tray hosts, helpers, and children)
/// without crossing into system processes or unrelated applications.
/// </summary>
public readonly record struct ProcessQuitCandidate(
    uint ProcessId,
    uint ParentProcessId,
    string? ExecutablePath,
    string? AppUserModelId);

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
        "shellhost.exe",
        "startmenuexperiencehost.exe",
        "searchhost.exe",
        "searchapp.exe",
        "textinputhost.exe",
        "lockapp.exe",
        "runtimebroker.exe",
        "conhost.exe",
        "taskhostw.exe",
        "fontdrvhost.exe",
        "wininit.exe",
        "ctfmon.exe",
        "dllhost.exe",
        "rundll32.exe",
        "msiexec.exe",
        "smartscreen.exe",
        "chsime.exe",
        "msmpeng.exe",
        "securityhealthservice.exe",
        "securityhealthsystray.exe",
        "widgets.exe",
        "widgetservice.exe",
        "phoneexperiencehost.exe",
        "system",
        "idle",
        "binglan.exe"
    };

    // Generic shells/script hosts can still be quit directly if explicitly targeted,
    // but must never be walked through as a parent tray host or killed as a child/sibling.
    private static readonly HashSet<string> GenericShellExecutableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe",
        "powershell.exe",
        "pwsh.exe",
        "wt.exe",
        "openconsole.exe",
        "wscript.exe",
        "cscript.exe",
        "mmc.exe",
        "taskmgr.exe"
    };

    // Subfolder names that sit beneath a dedicated app's install root (e.g. Weixin\4.1.15,
    // PowerToys\WinUI3Apps, Steam\bin\cef\cef.win7x64, DingDing\main\current, WPS Office\12.1.0\office6).
    private static readonly HashSet<string> InternalLayoutFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "bin64",
        "bin32",
        "x64",
        "x86",
        "arm64",
        "win64",
        "win32",
        "win-x64",
        "win-arm64",
        "cef",
        "cef.win7x64",
        "app",
        "main",
        "current",
        "latest",
        "release",
        "winui3apps",
        "resources",
        "helper",
        "helpers",
        "plugins",
        "runtime",
        "core",
        "service",
        "services",
        "daemon",
        "tray",
        "client",
        "office6",
        "application",
        "versions",
        "lib",
        "libexec"
    };

    // Top-level or user-collection directories that commonly hold multiple unrelated apps.
    private static readonly HashSet<string> SharedContainerFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "program files",
        "program files (x86)",
        "programdata",
        "common files",
        "windowsapps",
        "windows",
        "system32",
        "syswow64",
        "users",
        "appdata",
        "local",
        "locallow",
        "roaming",
        "programs",
        "desktop",
        "downloads",
        "documents",
        "temp",
        "tmp",
        "tools",
        "portable",
        "portableapps",
        "games",
        "utils",
        "utilities",
        "software",
        "apps",
        "app",
        "startup"
    };

    // Known multi-app suite folders where sibling exes belong to distinct products (e.g. Excel vs Word).
    private static readonly HashSet<string> SharedSuiteFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft office",
        "office14",
        "office15",
        "office16",
        "office17",
        "adobe",
        "jetbrains",
        "sysinternals",
        "microsoft visual studio",
        "common"
    };

    private static readonly HashSet<string> GenericExecutableStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "app",
        "main",
        "run",
        "bin",
        "client",
        "update",
        "updater",
        "launcher",
        "setup",
        "install",
        "installer",
        "start",
        "host",
        "service",
        "helper",
        "tray",
        "daemon",
        "core",
        "worker",
        "agent",
        "broker",
        "monitor",
        "exe"
    };

    private static readonly string[] AuxiliaryRoleTokens =
    [
        "tray",
        "helper",
        "daemon",
        "launcher",
        "crashpad",
        "crashhandler",
        "webhelper",
        "appex",
        "cloudsvr",
        "protect"
    ];

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

    public static bool IsGenericShellExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var normalized = WindowGrouping.NormalizeExecutablePath(executablePath);
        var fileName = Path.GetFileName(normalized);
        return !string.IsNullOrEmpty(fileName) && GenericShellExecutableNames.Contains(fileName);
    }

    /// <summary>
    /// Resolves the dedicated single-application installation root directory for an executable,
    /// collapsing known internal/version subfolders (such as <c>4.1.15</c>, <c>app-1.0.9</c>,
    /// <c>bin\cef</c>, <c>main\current</c>, <c>WinUI3Apps</c>, or <c>office6</c>).
    /// Returns <see langword="null"/> for Windows system directories, drive roots, user home
    /// directories, and shared top-level containers such as <c>Program Files</c>, <c>Desktop</c>,
    /// <c>Downloads</c>, or <c>Tools</c>.
    /// </summary>
    public static string? ResolveDedicatedAppRoot(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        var normalized = WindowGrouping.NormalizeExecutablePath(executablePath);
        var directory = Path.GetDirectoryName(normalized);
        if (string.IsNullOrEmpty(directory) || IsUnderWindowsDirectory(directory))
        {
            return null;
        }

        var current = TrimTrailingSeparators(directory);
        for (var depth = 0; depth < 6; depth++)
        {
            var leaf = Path.GetFileName(current);
            if (string.IsNullOrEmpty(leaf) || !IsVersionOrInternalFolderName(leaf))
            {
                break;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent))
            {
                return null;
            }

            var trimmedParent = TrimTrailingSeparators(parent);
            if (IsUnsafeAppRoot(trimmedParent))
            {
                return null;
            }

            current = trimmedParent;
        }

        if (IsUnsafeAppRoot(current))
        {
            return null;
        }

        var finalLeaf = Path.GetFileName(current);
        return string.IsNullOrEmpty(finalLeaf) || IsVersionOrInternalFolderName(finalLeaf)
            ? null
            : current;
    }

    /// <summary>
    /// Whether two executables refer to the same binary or a forward/reverse launcher-stub
    /// and versioned-subfolder pair (for example <c>Weixin\Weixin.exe</c> and
    /// <c>Weixin\4.1.15\Weixin.exe</c>, or <c>GRP\DingtalkLauncher.exe</c> and
    /// <c>GRP\main\current\GRP.exe</c>).
    /// </summary>
    public static bool MatchesExecutableOrVersionSibling(
        string referenceExecutablePath,
        string candidateExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(referenceExecutablePath)
            || string.IsNullOrWhiteSpace(candidateExecutablePath))
        {
            return false;
        }

        var reference = WindowGrouping.NormalizeExecutablePath(referenceExecutablePath);
        var candidate = WindowGrouping.NormalizeExecutablePath(candidateExecutablePath);
        if (string.Equals(reference, candidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsUnderWindowsDirectory(reference) || IsUnderWindowsDirectory(candidate))
        {
            return false;
        }

        return IsSubfolderStubPair(reference, candidate)
            || IsSubfolderStubPair(candidate, reference);
    }

    /// <summary>
    /// Extracts the real app executable name from a Squirrel <c>Update.exe --processStart Foo.exe</c>
    /// shortcut/pin, or returns <see langword="null"/> when not applicable.
    /// </summary>
    public static string? ExtractSquirrelStartExecutable(DockPinnedApp? pinned)
    {
        if (pinned is null
            || string.IsNullOrWhiteSpace(pinned.ExecutablePath)
            || string.IsNullOrWhiteSpace(pinned.Arguments))
        {
            return null;
        }

        var exeName = Path.GetFileName(WindowGrouping.NormalizeExecutablePath(pinned.ExecutablePath));
        if (!string.Equals(exeName, "Update.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var args = pinned.Arguments;
        const string flag = "--processStart";
        var index = args.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var rest = args[(index + flag.Length)..].TrimStart();
        if (rest.StartsWith('='))
        {
            rest = rest[1..].TrimStart();
        }
        if (rest.Length == 0)
        {
            return null;
        }

        string token;
        if (rest[0] == '"')
        {
            var endQuote = rest.IndexOf('"', 1);
            token = endQuote > 1 ? rest[1..endQuote] : rest[1..];
        }
        else
        {
            var endSpace = rest.IndexOfAny([' ', '\t']);
            token = endSpace > 0 ? rest[..endSpace] : rest;
        }

        var fileName = Path.GetFileName(token.Trim());
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }

    /// <summary>
    /// Whether a running process matches the Dock item's executable, launcher-stub child/parent
    /// directory, Squirrel target, or packaged AUMID, while unconditionally refusing protected executables.
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

            foreach (var groupExe in groupExecutablePaths)
            {
                if (MatchesExecutableOrVersionSibling(groupExe, normalized))
                {
                    return true;
                }
            }

            if (pinned is not null)
            {
                if (DockPinRules.MatchesExecutablePath(pinned, normalized))
                {
                    return true;
                }

                // Win32 apps that publish an explicit window AppUserModelId (e.g. Electron, QQ,
                // Feishu, DingTalk) do not expose a packaged process AUMID via GetApplicationUserModelId;
                // match their pinned executable path when it is not a shared browser/frame host.
                if (!string.IsNullOrWhiteSpace(pinned.ExecutablePath)
                    && !string.IsNullOrWhiteSpace(pinned.AppUserModelId)
                    && !IsBrowserHostExecutable(pinned.ExecutablePath)
                    && !DockFileDropRules.IsFrameHostPath(pinned.ExecutablePath)
                    && MatchesExecutableOrVersionSibling(pinned.ExecutablePath, normalized))
                {
                    return true;
                }

                if (MatchesSquirrelTarget(pinned, normalized))
                {
                    return true;
                }
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

    /// <summary>
    /// Selects all processes belonging to the target Dock item — including direct matches,
    /// parent tray hosts and child helpers inside the same dedicated app install directory,
    /// and detached same-app helper/tray siblings — ordered parents-first to prevent respawn.
    /// Unconditionally excludes protected system processes, Windows directories, shared
    /// containers, unrelated browser hosts, and executables belonging to other Dock items.
    /// </summary>
    public static IReadOnlyList<uint> SelectProcessesToTerminate(
        DockPinnedApp? pinned,
        IReadOnlySet<string> groupExecutablePaths,
        IReadOnlySet<string> groupAumids,
        IReadOnlySet<uint> groupProcessIds,
        IReadOnlyList<ProcessQuitCandidate> runningProcesses,
        IReadOnlySet<string>? otherItemExecutablePaths = null)
    {
        ArgumentNullException.ThrowIfNull(groupExecutablePaths);
        ArgumentNullException.ThrowIfNull(groupAumids);
        ArgumentNullException.ThrowIfNull(groupProcessIds);
        ArgumentNullException.ThrowIfNull(runningProcesses);

        var candidatesByPid = new Dictionary<uint, (ProcessQuitCandidate Candidate, string? NormalizedExe)>(runningProcesses.Count);
        foreach (var candidate in runningProcesses)
        {
            if (candidate.ProcessId <= 4
                || IsProtectedExecutable(candidate.ExecutablePath)
                || IsProtectedAumid(candidate.AppUserModelId))
            {
                continue;
            }

            var normalizedExe = string.IsNullOrWhiteSpace(candidate.ExecutablePath)
                ? null
                : WindowGrouping.NormalizeExecutablePath(candidate.ExecutablePath);
            candidatesByPid[candidate.ProcessId] = (candidate, normalizedExe);
        }

        if (candidatesByPid.Count == 0)
        {
            return [];
        }

        var ownNormalizedExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var exe in groupExecutablePaths)
        {
            if (!string.IsNullOrWhiteSpace(exe) && !IsProtectedExecutable(exe))
            {
                ownNormalizedExes.Add(WindowGrouping.NormalizeExecutablePath(exe));
            }
        }
        if (!string.IsNullOrWhiteSpace(pinned?.ExecutablePath)
            && !IsProtectedExecutable(pinned.ExecutablePath)
            && !DockFileDropRules.IsFrameHostPath(pinned.ExecutablePath)
            && !(IsBrowserHostExecutable(pinned.ExecutablePath) && !string.IsNullOrWhiteSpace(pinned.AppUserModelId)))
        {
            ownNormalizedExes.Add(WindowGrouping.NormalizeExecutablePath(pinned.ExecutablePath));
        }

        var targetIsBrowser = ownNormalizedExes.Any(IsBrowserHostExecutable);
        var matchedPids = new HashSet<uint>();
        var seedAppRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seedStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void RecordSeedMetadata(string? normalizedExe)
        {
            if (string.IsNullOrWhiteSpace(normalizedExe) || IsProtectedExecutable(normalizedExe))
            {
                return;
            }

            var stem = Path.GetFileNameWithoutExtension(normalizedExe);
            if (IsSpecificAppStem(stem))
            {
                seedStems.Add(stem);
            }

            if (ResolveDedicatedAppRoot(normalizedExe) is { } root)
            {
                seedAppRoots.Add(root);
                var folderName = Path.GetFileName(root);
                if (IsSpecificAppStem(folderName) && !SharedSuiteFolderNames.Contains(folderName))
                {
                    seedStems.Add(folderName);
                }
            }
        }

        foreach (var exe in ownNormalizedExes)
        {
            RecordSeedMetadata(exe);
        }
        if (ExtractSquirrelStartExecutable(pinned) is { } squirrelExe)
        {
            var squirrelStem = Path.GetFileNameWithoutExtension(squirrelExe);
            if (IsSpecificAppStem(squirrelStem))
            {
                seedStems.Add(squirrelStem);
            }
        }

        // Step 1: direct seed matches (exact exe, launcher stub, Squirrel target, AUMID, or group PID).
        foreach (var (pid, (candidate, normalizedExe)) in candidatesByPid)
        {
            var directMatch = groupProcessIds.Contains(pid)
                || MatchesProcess(pinned, groupExecutablePaths, groupAumids, normalizedExe, candidate.AppUserModelId);
            if (directMatch)
            {
                matchedPids.Add(pid);
                RecordSeedMetadata(normalizedExe);
            }
        }

        bool IsExpansionEligible(string? normalizedExe)
        {
            if (string.IsNullOrWhiteSpace(normalizedExe)
                || IsProtectedExecutable(normalizedExe)
                || IsGenericShellExecutable(normalizedExe))
            {
                return false;
            }
            if (!targetIsBrowser && IsBrowserHostExecutable(normalizedExe))
            {
                return false;
            }
            if (otherItemExecutablePaths is not null
                && otherItemExecutablePaths.Contains(normalizedExe)
                && !ownNormalizedExes.Contains(normalizedExe))
            {
                return false;
            }
            return IsUnderAnyRoot(normalizedExe, seedAppRoots);
        }

        void ExpandProcessTreeWithinAppRoots()
        {
            if (matchedPids.Count == 0 || seedAppRoots.Count == 0)
            {
                return;
            }

            var changed = true;
            while (changed)
            {
                changed = false;

                // Walk UP to a same-app parent/tray host (never through explorer/shells).
                foreach (var pid in matchedPids.ToArray())
                {
                    if (!candidatesByPid.TryGetValue(pid, out var current))
                    {
                        continue;
                    }
                    var parentPid = current.Candidate.ParentProcessId;
                    if (parentPid > 4
                        && !matchedPids.Contains(parentPid)
                        && candidatesByPid.TryGetValue(parentPid, out var parent)
                        && IsExpansionEligible(parent.NormalizedExe))
                    {
                        matchedPids.Add(parentPid);
                        RecordSeedMetadata(parent.NormalizedExe);
                        changed = true;
                    }
                }

                // Walk DOWN to same-app helper/core/tray children.
                foreach (var (pid, (candidate, normalizedExe)) in candidatesByPid)
                {
                    if (!matchedPids.Contains(pid)
                        && candidate.ParentProcessId > 4
                        && matchedPids.Contains(candidate.ParentProcessId)
                        && IsExpansionEligible(normalizedExe))
                    {
                        matchedPids.Add(pid);
                        changed = true;
                    }
                }
            }
        }

        // Step 2: process-tree expansion around direct matches inside the dedicated app root.
        ExpandProcessTreeWithinAppRoots();

        // Step 3: detached same-app tray/helper siblings inside the dedicated app root.
        if (seedAppRoots.Count > 0)
        {
            var addedDetached = false;
            foreach (var (pid, (_, normalizedExe)) in candidatesByPid)
            {
                if (matchedPids.Contains(pid) || !IsExpansionEligible(normalizedExe))
                {
                    continue;
                }

                if (IsDetachedSameAppSibling(normalizedExe!, seedAppRoots, seedStems))
                {
                    matchedPids.Add(pid);
                    addedDetached = true;
                }
            }

            if (addedDetached)
            {
                ExpandProcessTreeWithinAppRoots();
            }
        }

        if (matchedPids.Count <= 1)
        {
            return matchedPids.ToArray();
        }

        // Step 4: order parents/roots before children so a tray host or watchdog cannot
        // respawn a child process during the termination pass.
        int DepthOf(uint pid)
        {
            var depth = 0;
            var current = pid;
            var visited = new HashSet<uint> { current };
            while (candidatesByPid.TryGetValue(current, out var info)
                && info.Candidate.ParentProcessId > 4
                && matchedPids.Contains(info.Candidate.ParentProcessId)
                && visited.Add(info.Candidate.ParentProcessId))
            {
                current = info.Candidate.ParentProcessId;
                depth++;
            }
            return depth;
        }

        return matchedPids
            .OrderBy(DepthOf)
            .ThenBy(pid => pid)
            .ToArray();
    }

    private static bool MatchesSquirrelTarget(DockPinnedApp pinned, string normalizedCandidate)
    {
        var targetFileName = ExtractSquirrelStartExecutable(pinned);
        if (targetFileName is null || string.IsNullOrWhiteSpace(pinned.ExecutablePath))
        {
            return false;
        }

        var pinnedDir = Path.GetDirectoryName(WindowGrouping.NormalizeExecutablePath(pinned.ExecutablePath));
        if (string.IsNullOrEmpty(pinnedDir) || IsUnderWindowsDirectory(pinnedDir))
        {
            return false;
        }

        var prefix = TrimTrailingSeparators(pinnedDir) + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(normalizedCandidate), targetFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSubfolderStubPair(string stubPath, string deepPath)
    {
        var stubDirectory = Path.GetDirectoryName(stubPath);
        if (string.IsNullOrEmpty(stubDirectory) || IsUnsafeAppRoot(TrimTrailingSeparators(stubDirectory)))
        {
            return false;
        }

        var prefix = TrimTrailingSeparators(stubDirectory) + Path.DirectorySeparatorChar;
        if (!deepPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || deepPath.IndexOf(Path.DirectorySeparatorChar, prefix.Length) < 0)
        {
            return false;
        }

        var stubName = Path.GetFileName(stubPath);
        var looksLikeLauncher = Path.GetFileNameWithoutExtension(stubName)
            .Contains("launcher", StringComparison.OrdinalIgnoreCase);
        return looksLikeLauncher
            || string.Equals(Path.GetFileName(deepPath), stubName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDetachedSameAppSibling(
        string normalizedExe,
        HashSet<string> seedAppRoots,
        HashSet<string> seedStems)
    {
        var stem = Path.GetFileNameWithoutExtension(normalizedExe);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return false;
        }

        foreach (var seedStem in seedStems)
        {
            if (stem.StartsWith(seedStem, StringComparison.OrdinalIgnoreCase)
                || (stem.Length >= 3 && seedStem.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                    && IsSpecificAppStem(stem)))
            {
                return true;
            }
        }

        // Auxiliary roles (tray/helper/daemon/launcher/crashpad) match inside a non-suite
        // dedicated app root even when the helper exe drops the brand prefix.
        var matchingRoot = FindMatchingRoot(normalizedExe, seedAppRoots);
        if (matchingRoot is not null && !SharedSuiteFolderNames.Contains(Path.GetFileName(matchingRoot)))
        {
            foreach (var token in AuxiliaryRoleTokens)
            {
                if (stem.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsSpecificAppStem(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length >= 3
        && !GenericExecutableStems.Contains(value)
        && !SharedContainerFolderNames.Contains(value);

    private static bool IsUnderAnyRoot(string normalizedExe, HashSet<string> roots) =>
        FindMatchingRoot(normalizedExe, roots) is not null;

    private static string? FindMatchingRoot(string normalizedExe, HashSet<string> roots)
    {
        foreach (var root in roots)
        {
            var prefix = TrimTrailingSeparators(root) + Path.DirectorySeparatorChar;
            if (normalizedExe.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }
        }
        return null;
    }

    private static bool IsVersionOrInternalFolderName(string folderName)
    {
        if (InternalLayoutFolderNames.Contains(folderName))
        {
            return true;
        }

        // Match version directories like "4.1.15", "12.1.0.15990", "v2.1.0", "app-1.0.9165".
        var span = folderName.AsSpan();
        if (span.StartsWith("app-", StringComparison.OrdinalIgnoreCase))
        {
            span = span[4..];
        }
        else if (span.Length > 1 && (span[0] is 'v' or 'V') && char.IsAsciiDigit(span[1]))
        {
            span = span[1..];
        }

        if (span.Length == 0 || !char.IsAsciiDigit(span[0]))
        {
            return false;
        }

        var hasDot = false;
        foreach (var ch in span)
        {
            if (ch == '.')
            {
                hasDot = true;
            }
            else if (!char.IsAsciiDigit(ch) && ch is not ('-' or '_' or 'a' or 'b' or 'r' or 'c'))
            {
                return false;
            }
        }
        return hasDot;
    }

    private static bool IsUnsafeAppRoot(string trimmedDirectory)
    {
        if (string.IsNullOrWhiteSpace(trimmedDirectory))
        {
            return true;
        }

        var root = Path.GetPathRoot(trimmedDirectory);
        if (string.IsNullOrEmpty(root)
            || trimmedDirectory.Length <= TrimTrailingSeparators(root).Length)
        {
            return true;
        }

        if (IsUnderWindowsDirectory(trimmedDirectory))
        {
            return true;
        }

        var leaf = Path.GetFileName(trimmedDirectory);
        if (string.IsNullOrEmpty(leaf) || SharedContainerFolderNames.Contains(leaf))
        {
            return true;
        }

        // Reject user profile roots such as C:\Users\<username> (including synthetic test usernames).
        var parent = Path.GetDirectoryName(trimmedDirectory);
        if (!string.IsNullOrEmpty(parent)
            && string.Equals(Path.GetFileName(TrimTrailingSeparators(parent)), "Users", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsKnownSpecialContainer(trimmedDirectory);
    }

    private static bool IsKnownSpecialContainer(string trimmedDirectory)
    {
        Span<Environment.SpecialFolder> folders =
        [
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonProgramFiles,
            Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyDocuments
        ];

        foreach (var folder in folders)
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path)
                && string.Equals(trimmedDirectory, TrimTrailingSeparators(path), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var temp = Path.GetTempPath();
        return !string.IsNullOrEmpty(temp)
            && string.Equals(trimmedDirectory, TrimTrailingSeparators(temp), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderWindowsDirectory(string path)
    {
        var normalized = TrimTrailingSeparators(WindowGrouping.NormalizeExecutablePath(path));
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows))
        {
            var normalizedWindows = TrimTrailingSeparators(windows);
            if (string.Equals(normalized, normalizedWindows, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(normalizedWindows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Also guard synthetic drive-letter Windows paths like C:\Windows\System32\...
        var root = Path.GetPathRoot(normalized);
        if (!string.IsNullOrEmpty(root))
        {
            var syntheticWindows = TrimTrailingSeparators(root) + Path.DirectorySeparatorChar + "Windows";
            if (string.Equals(normalized, syntheticWindows, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(syntheticWindows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string TrimTrailingSeparators(string path)
    {
        var root = Path.GetPathRoot(path);
        var rootLen = root?.Length ?? 0;
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length < rootLen && root is not null ? root : trimmed;
    }
}
