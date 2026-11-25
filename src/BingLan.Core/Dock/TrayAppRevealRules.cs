using BingLan.Core.TopBar;

namespace BingLan.Core.Dock;

/// <summary>An app's own global "show window" hotkey, in RegisterHotKey terms.</summary>
public sealed record TrayRevealHotkey(uint Modifiers, int VirtualKey);

/// <summary>Process/parent pair used to count independent top-level app instances.</summary>
public readonly record struct ProcessHierarchyEntry(uint ProcessId, uint ParentProcessId);

/// <summary>Decision for how a tray-resident hotkey app should be revealed when no visible window is found.</summary>
public enum TrayRevealAction
{
    LaunchNewProcess,
    SendGlobalHotkey,
    NotifyMultiInstanceInTray,
    NotifyHotkeyUnavailable
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
    /// Decides what to do for a hotkey-table app when no visible window exists for the target.
    /// Multi-instance tray apps never fire the global hotkey (which always wakes instance #1),
    /// and a single running tray instance never falls back to spawning a fresh login window.
    /// </summary>
    public static TrayRevealAction DecideWhenHidden(int rootInstanceCount, bool hotkeyRegistered)
    {
        if (rootInstanceCount <= 0)
        {
            return TrayRevealAction.LaunchNewProcess;
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

    public static string FormatTrayHotkeyHint(string displayName) =>
        $"{displayName} 已驻留托盘，请用其快捷键或从系统托盘打开（需多开请右键“启动新实例”）";
}
