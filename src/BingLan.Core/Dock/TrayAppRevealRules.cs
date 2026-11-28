using BingLan.Core.TopBar;

namespace BingLan.Core.Dock;

/// <summary>An app's own global "show window" hotkey, in RegisterHotKey terms.</summary>
public sealed record TrayRevealHotkey(uint Modifiers, int VirtualKey);

/// <summary>Process/parent pair used to count independent top-level app instances.</summary>
public readonly record struct ProcessHierarchyEntry(uint ProcessId, uint ParentProcessId, bool IsIdentityVerified = true);

public readonly record struct TrayProcessIdentity(string ExecutablePath, long CreationTime);

/// <summary>Decision for how a tray-resident hotkey app should be revealed when no visible window is found.</summary>
public enum TrayRevealAction
{
    LaunchNewProcess,
    SendGlobalHotkey,
    NotifyMultiInstanceInTray,
    NotifyHotkeyUnavailable,
    NotifyTargetUnavailable,
    NotifyTrayInstanceChoiceRequired
}

/// <summary>
/// Apps that close to the tray keep only hidden windows, which must not be shown from
/// outside. A small static table maps apps whose re-launch opens a fresh login panel
/// (WeChat, QQ) to their default global reveal hotkey. Other apps rely on their own
/// single-instance launch path plus launch cooldown.
/// </summary>
public static class TrayAppRevealRules
{
    /// <summary>How long the dock waits for the hotkey-raised window to appear.</summary>
    public static readonly TimeSpan RevealTimeout = TimeSpan.FromMilliseconds(2500);

    // WeChat 3.x (WeChat.exe) and 4.x (Weixin.exe) default to Ctrl+Alt+W (verified with WeChat 4.1.15).
    private static readonly TrayRevealHotkey WeChat = new(
        TopBarSearchRules.ModifierControl | TopBarSearchRules.ModifierAlt,
        'W');

    private static readonly string[] WeChatProcessNames = ["Weixin", "WeChat"];

    public static TrayRevealHotkey? HotkeyFor(string? executablePath)
    {
        if (Path.GetFileName(executablePath) is not { Length: > 0 } name)
        {
            return null;
        }

        if (name.Equals("Weixin.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("WeChat.exe", StringComparison.OrdinalIgnoreCase))
        {
            return WeChat;
        }

        return null;
    }

    /// <summary>
    /// Process image names (without extension) that compete for the same global reveal hotkey.
    /// WeChat 3.x (<c>WeChat.exe</c>) and 4.x (<c>Weixin.exe</c>) both claim Ctrl+Alt+W, as do
    /// dual-opened copies installed in different folders.
    /// </summary>
    public static IReadOnlyList<string> HotkeySharingProcessNames(string? executablePath)
    {
        var hotkey = HotkeyFor(executablePath);
        if (hotkey == WeChat)
        {
            return WeChatProcessNames;
        }

        var name = Path.GetFileNameWithoutExtension(executablePath);
        return string.IsNullOrWhiteSpace(name) ? [] : [name];
    }

    public static bool SharesHotkey(string? firstExecutablePath, string? secondExecutablePath) =>
        HotkeyFor(firstExecutablePath) is { } first
        && Equals(first, HotkeyFor(secondExecutablePath));

    /// <summary>Uses the alert instance's main executable to reveal an account from its tray.</summary>
    public static string? ResolveRevealExecutable(string? pinnedExecutable, string? instanceRootExecutable) =>
        PreventsRelaunchWhenRunning(instanceRootExecutable) ? instanceRootExecutable : pinnedExecutable;

    public static bool IsWeChatHelperExecutable(string? executablePath) =>
        string.Equals(Path.GetFileName(executablePath), "WeChatAppEx.exe", StringComparison.OrdinalIgnoreCase);

    public static bool IsWeChatProfileHelper(string? executablePath)
    {
        if (!IsWeChatHelperExecutable(executablePath))
        {
            return false;
        }

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return roaming.Length != 0 && WindowGrouping.NormalizeExecutablePath(executablePath!).StartsWith(
            Path.Combine(roaming, "Tencent", "xwechat", "xplugin", "plugins", "RadiumWMPF") + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    internal static bool MatchesWeChatHelper(string executablePath, string candidatePath) =>
        HotkeyFor(executablePath) == WeChat
        && IsWeChatHelperExecutable(candidatePath)
        && DockQuitRules.ResolveDedicatedAppRoot(executablePath) is { } appRoot
        && string.Equals(appRoot, DockQuitRules.ResolveDedicatedAppRoot(candidatePath), StringComparison.OrdinalIgnoreCase);

    public static (HashSet<uint> ProcessIds, string? RootExecutablePath) ResolveAppInstance(
        uint processId,
        IReadOnlyDictionary<uint, uint> parentByPid,
        Func<uint, TrayProcessIdentity?> readIdentity,
        Func<uint, bool>? hasIndependentWindow = null)
    {
        if (processId == 0 || readIdentity(processId) is not { } seed)
        {
            return ([], null);
        }

        if (DockQuitRules.IsProtectedExecutable(seed.ExecutablePath)
            || DockQuitRules.IsGenericShellExecutable(seed.ExecutablePath))
        {
            return ([processId], seed.ExecutablePath);
        }

        var seedDirectory = DockQuitRules.ResolveDedicatedAppRoot(seed.ExecutablePath);
        var isWeChatHelper = IsWeChatHelperExecutable(seed.ExecutablePath);
        var isWeChatMain = HotkeyFor(seed.ExecutablePath) == WeChat;
        var identities = new Dictionary<uint, TrayProcessIdentity?> { [processId] = seed };
        TrayProcessIdentity? ReadSameApp(uint pid, string reference, string? appRoot)
        {
            if (pid <= 4)
            {
                return null;
            }
            if (!identities.TryGetValue(pid, out var identity))
            {
                identity = readIdentity(pid);
                identities[pid] = identity;
            }
            if (identity is not { } candidate
                || DockQuitRules.IsProtectedExecutable(candidate.ExecutablePath)
                || DockQuitRules.IsGenericShellExecutable(candidate.ExecutablePath))
            {
                return null;
            }
            return DockQuitRules.MatchesExecutableOrVersionSibling(reference, candidate.ExecutablePath)
                || (IsWeChatProfileHelper(reference) && HotkeyFor(candidate.ExecutablePath) == WeChat)
                || (HotkeyFor(reference) == WeChat && IsWeChatProfileHelper(candidate.ExecutablePath))
                || (appRoot is not null && string.Equals(appRoot,
                    DockQuitRules.ResolveDedicatedAppRoot(candidate.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                ? candidate
                : null;
        }

        var children = parentByPid.ToLookup(pair => pair.Value, pair => pair.Key);
        bool IsSeparateWeChatProcess(uint pid, TrayProcessIdentity identity)
        {
            if (HotkeyFor(identity.ExecutablePath) != WeChat)
            {
                return false;
            }
            if (hasIndependentWindow?.Invoke(pid) == true)
            {
                return true;
            }
            var directory = DockQuitRules.ResolveDedicatedAppRoot(identity.ExecutablePath);
            return children[pid].Any(childPid => ReadSameApp(childPid, identity.ExecutablePath, directory) is { } child
                && IsWeChatHelperExecutable(child.ExecutablePath)
                && child.CreationTime >= identity.CreationTime);
        }

        var rootPid = processId;
        var root = seed;
        var visited = new HashSet<uint> { processId };
        while ((!isWeChatHelper || HotkeyFor(root.ExecutablePath) != WeChat)
            && (!isWeChatMain || !IsSeparateWeChatProcess(rootPid, root))
            && parentByPid.TryGetValue(rootPid, out var parentPid)
            && visited.Add(parentPid)
            && ReadSameApp(parentPid, seed.ExecutablePath, seedDirectory) is { } parent
            && parent.CreationTime <= root.CreationTime)
        {
            rootPid = parentPid;
            root = parent;
        }

        if (isWeChatHelper && HotkeyFor(root.ExecutablePath) != WeChat)
        {
            return ([processId], seed.ExecutablePath);
        }

        var rootDirectory = DockQuitRules.ResolveDedicatedAppRoot(root.ExecutablePath);
        var instancePids = new HashSet<uint> { rootPid };
        var pending = new Queue<(uint Pid, TrayProcessIdentity Identity)>();
        pending.Enqueue((rootPid, root));
        while (pending.TryDequeue(out var ancestor))
        {
            foreach (var childPid in children[ancestor.Pid])
            {
                if (!instancePids.Contains(childPid)
                    && ReadSameApp(childPid, root.ExecutablePath, rootDirectory) is { } child
                    && child.CreationTime >= ancestor.Identity.CreationTime
                    && (!(isWeChatHelper || isWeChatMain) || !IsSeparateWeChatProcess(childPid, child)))
                {
                    instancePids.Add(childPid);
                    pending.Enqueue((childPid, child));
                }
            }
        }

        return (instancePids, root.ExecutablePath);
    }

    public static bool IsSharedHotkeyOwnerAmbiguous(
        IReadOnlyList<ProcessHierarchyEntry> hotkeyProcesses, IReadOnlySet<uint> targetInstance)
    {
        return hotkeyProcesses.Any(entry => !entry.IsIdentityVerified
            || (targetInstance.Count > 0 && !targetInstance.Contains(entry.ProcessId)));
    }

    public static List<ProcessHierarchyEntry> ConfirmRunningHierarchy(
        IReadOnlyDictionary<uint, uint>? parentSnapshot,
        IReadOnlyDictionary<uint, TrayProcessIdentity?> identities)
    {
        var entries = new List<ProcessHierarchyEntry>(identities.Count);
        foreach (var (pid, identity) in identities)
        {
            uint parentPid = 0;
            if (parentSnapshot is not null && !parentSnapshot.TryGetValue(pid, out parentPid))
            {
                continue;
            }
            var hasConfirmedParent = parentSnapshot is not null && parentSnapshot.ContainsKey(parentPid)
                && identity is { } child
                && identities.TryGetValue(parentPid, out var parentIdentity) && parentIdentity is { } parent
                && parent.CreationTime <= child.CreationTime;
            entries.Add(new ProcessHierarchyEntry(pid, hasConfirmedParent ? parentPid : 0, identity is not null));
        }
        return entries;
    }

    /// <summary>
    /// Returns true for tray-resident chat apps (WeChat, QQ) whose re-launch while already
    /// running opens a new login panel instead of raising the logged-in main window.
    /// </summary>
    public static bool PreventsRelaunchWhenRunning(string? executablePath)
    {
        if (Path.GetFileName(executablePath) is not { Length: > 0 } name)
        {
            return false;
        }

        return name.Equals("Weixin.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("WeChat.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("QQ.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Counts independent root instances among processes running the same executable.
    /// Multi-process apps (WeChat 4.x, NTQQ/Electron, Chromium) spawn child helpers whose
    /// ParentProcessId points to their own main process; only root processes count as instances.
    /// </summary>
    public static int CountRootInstances(IReadOnlyList<ProcessHierarchyEntry> sameExecutableProcesses)
    {
        ArgumentNullException.ThrowIfNull(sameExecutableProcesses);
        if (sameExecutableProcesses.Count == 0)
        {
            return 0;
        }

        var pids = new HashSet<uint>(sameExecutableProcesses.Count);
        foreach (var entry in sameExecutableProcesses)
        {
            if (entry.ProcessId != 0)
            {
                pids.Add(entry.ProcessId);
            }
        }

        if (pids.Count == 0)
        {
            return 0;
        }

        var roots = 0;
        foreach (var entry in sameExecutableProcesses)
        {
            if (entry.ProcessId != 0
                && (entry.ParentProcessId == 0 || !pids.Contains(entry.ParentProcessId)))
            {
                roots++;
            }
        }

        return Math.Max(1, roots);
    }

    public static HashSet<uint> InstanceProcessIdsFor(
        IReadOnlyList<ProcessHierarchyEntry> sameExecutableProcesses,
        uint processId)
    {
        ArgumentNullException.ThrowIfNull(sameExecutableProcesses);
        if (processId == 0)
        {
            return [];
        }

        var parentByPid = new Dictionary<uint, uint>(sameExecutableProcesses.Count);
        foreach (var entry in sameExecutableProcesses)
        {
            if (entry.ProcessId != 0)
            {
                parentByPid[entry.ProcessId] = entry.ParentProcessId;
            }
        }

        if (!parentByPid.ContainsKey(processId))
        {
            return [processId];
        }

        uint FindRoot(uint pid)
        {
            var current = pid;
            var visited = new HashSet<uint> { current };
            while (parentByPid.TryGetValue(current, out var parent)
                && parent != 0
                && parentByPid.ContainsKey(parent)
                && visited.Add(parent))
            {
                current = parent;
            }
            return current;
        }

        var targetRoot = FindRoot(processId);
        var instancePids = new HashSet<uint> { processId };
        foreach (var pid in parentByPid.Keys)
        {
            if (FindRoot(pid) == targetRoot)
            {
                instancePids.Add(pid);
            }
        }

        return instancePids;
    }

    /// <summary>
    /// Walks up <paramref name="sameExecutableProcesses"/> to locate the root process ID of
    /// the instance containing <paramref name="processId"/>, or returns <paramref name="processId"/>
    /// when it is not present in the hierarchy.
    /// </summary>
    public static uint ResolveRootProcessId(
        IReadOnlyList<ProcessHierarchyEntry> sameExecutableProcesses,
        uint processId)
    {
        ArgumentNullException.ThrowIfNull(sameExecutableProcesses);
        if (processId == 0)
        {
            return 0;
        }

        var parentByPid = new Dictionary<uint, uint>(sameExecutableProcesses.Count);
        foreach (var entry in sameExecutableProcesses)
        {
            if (entry.ProcessId != 0)
            {
                parentByPid[entry.ProcessId] = entry.ParentProcessId;
            }
        }

        if (!parentByPid.ContainsKey(processId))
        {
            return processId;
        }

        var current = processId;
        var visited = new HashSet<uint> { current };
        while (parentByPid.TryGetValue(current, out var parent)
            && parent != 0
            && parentByPid.ContainsKey(parent)
            && visited.Add(parent))
        {
            current = parent;
        }

        return current;
    }

    /// <summary>
    /// Decides what to do for a hotkey-table app when no visible window exists for the target.
    /// Multi-instance tray apps never fire the global hotkey (which always wakes instance #1),
    /// and a single running tray instance never falls back to spawning a fresh login window.
    /// </summary>
    public static TrayRevealAction DecideWhenHidden(
        int rootInstanceCount,
        bool hotkeyRegistered,
        bool hasPreferredInstance = false,
        bool preferredInstanceExited = false,
        bool ambiguousHotkeyOwner = false)
    {
        if (preferredInstanceExited)
        {
            return TrayRevealAction.NotifyTargetUnavailable;
        }
        if (ambiguousHotkeyOwner)
        {
            return TrayRevealAction.NotifyTrayInstanceChoiceRequired;
        }
        if (rootInstanceCount <= 0)
        {
            return hasPreferredInstance
                ? TrayRevealAction.NotifyTargetUnavailable
                : TrayRevealAction.LaunchNewProcess;
        }

        if (rootInstanceCount > 1)
        {
            return TrayRevealAction.NotifyMultiInstanceInTray;
        }

        return hotkeyRegistered
            ? TrayRevealAction.SendGlobalHotkey
            : TrayRevealAction.NotifyHotkeyUnavailable;
    }

    public static string FormatMultiInstanceTrayHint(string displayName) =>
        $"检测到多个 {displayName} 驻留托盘，请从系统托盘点对应图标";

    public static string FormatChooseTrayInstanceHint(string displayName) =>
        $"请从系统托盘打开对应的 {displayName} 账号";

    public static string FormatTrayHotkeyHint(string displayName) =>
        $"{displayName} 已驻留托盘，请用其快捷键或从系统托盘打开（需多开请右键“启动新实例”）";
}
