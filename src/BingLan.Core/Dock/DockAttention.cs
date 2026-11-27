using System.IO;
using BingLan.Core.Models;

namespace BingLan.Core.Dock;

/// <summary>Snapshot of a single window attention entry with monotonic recency order.</summary>
public readonly record struct AttentionEntry(
    nint Window,
    uint ProcessId,
    int Unread,
    string? ExecutablePath,
    long Sequence);

/// <summary>
/// Windows that asked for attention by flashing (a new chat message, a finished
/// download), until they are activated or closed. The dock marks their apps, and
/// shows the unread count an app published in the flashed window's title.
/// </summary>
public sealed class DockAttention
{
    private readonly Dictionary<nint, (uint ProcessId, int Unread, string? ExecutablePath, long Sequence)> _windows = [];
    private long _sequence;

    public int Count => _windows.Count;

    /// <summary>The marked window handles, for liveness sweeps by the caller.</summary>
    public IReadOnlyCollection<nint> MarkedWindows => _windows.Keys;

    /// <summary>Records a flash request with the owning process and optional unread count.</summary>
    public bool Flashed(nint window, uint processId, int? unread = null, string? executablePath = null)
    {
        if (window == 0)
        {
            return false;
        }

        var nextUnread = unread ?? 0;
        var nextSequence = ++_sequence;
        if (_windows.TryGetValue(window, out var existing))
        {
            var mergedUnread = Math.Max(existing.Unread, nextUnread);
            _windows[window] = (
                existing.ProcessId != 0 ? existing.ProcessId : processId,
                mergedUnread,
                existing.ExecutablePath ?? executablePath,
                nextSequence);
            return nextUnread > existing.Unread;
        }

        _windows.Add(window, (processId, nextUnread, executablePath, nextSequence));
        return true;
    }

    /// <summary>
    /// The app was activated; drops the window itself and, when <paramref name="processId"/>
    /// names a dedicated app process, any hidden message window that flashed on its behalf.
    /// </summary>
    public bool Cleared(nint window, uint processId = 0)
    {
        var removed = window != 0 && _windows.Remove(window);
        if (processId != 0)
        {
            foreach (var candidate in _windows
                         .Where(pair => pair.Value.ProcessId == processId)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                removed |= _windows.Remove(candidate);
            }
        }
        return removed;
    }

    /// <summary>
    /// Clears the activated window and any marked helper/message windows belonging to the
    /// same application instance (e.g. a multi-process WeChat instance's helper process),
    /// without touching marks owned by another dual-opened instance.
    /// </summary>
    public bool Cleared(nint window, IReadOnlySet<uint> instanceProcessIds)
    {
        ArgumentNullException.ThrowIfNull(instanceProcessIds);
        var removed = window != 0 && _windows.Remove(window);
        if (instanceProcessIds.Count > 0)
        {
            foreach (var candidate in _windows
                         .Where(pair => pair.Value.ProcessId != 0 && instanceProcessIds.Contains(pair.Value.ProcessId))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                removed |= _windows.Remove(candidate);
            }
        }
        return removed;
    }

    /// <summary>One window was destroyed; other windows of the same process keep their marks.</summary>
    public bool Closed(nint window) => window != 0 && _windows.Remove(window);

    /// <summary>
    /// Returns 2 when this exact window handle flashed, 1 when a hidden/other window of the
    /// same process (or instance process set) flashed, or 0 when the window is not requesting attention.
    /// </summary>
    public int AttentionPriority(TrackedWindow window) => AttentionPriority(window, relatedProcessIds: null);

    public int AttentionPriority(TrackedWindow window, IReadOnlySet<uint>? relatedProcessIds)
    {
        if (window.Handle != 0 && _windows.ContainsKey(window.Handle))
        {
            return 2;
        }

        if (window.ProcessId != 0
            && _windows.Values.Any(marked =>
                marked.ProcessId != 0
                && (marked.ProcessId == window.ProcessId
                    || (relatedProcessIds is not null && relatedProcessIds.Contains(marked.ProcessId)))))
        {
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Returns the recency-weighted attention rank <c>(Sequence, Priority)</c> for a window,
    /// where more recently flashed instances outrank older stale marks, and exact handle matches
    /// break ties within the same flash sequence.
    /// </summary>
    public (long Sequence, int Priority) AttentionRank(
        TrackedWindow window,
        IReadOnlySet<uint>? relatedProcessIds = null)
    {
        var bestSequence = 0L;
        var bestPriority = 0;

        if (window.Handle != 0 && _windows.TryGetValue(window.Handle, out var direct))
        {
            bestSequence = direct.Sequence;
            bestPriority = 2;
        }

        if (window.ProcessId != 0)
        {
            foreach (var marked in _windows.Values)
            {
                if (marked.ProcessId == 0)
                {
                    continue;
                }

                var sameInstance = marked.ProcessId == window.ProcessId
                    || (relatedProcessIds is not null && relatedProcessIds.Contains(marked.ProcessId));
                if (!sameInstance)
                {
                    continue;
                }

                if (marked.Sequence > bestSequence
                    || (marked.Sequence == bestSequence && 1 > bestPriority))
                {
                    bestSequence = marked.Sequence;
                    bestPriority = 1;
                }
            }
        }

        return (bestSequence, bestPriority);
    }

    public bool WantsWindow(TrackedWindow window) => AttentionPriority(window) > 0;

    public bool Wants(WindowGroup? group) =>
        group is not null && group.Windows.Any(WantsWindow);

    /// <summary>
    /// Whether the item's app is waiting for attention. Matches both visible windows in the
    /// running group and tray-hidden instances (including a second dual-opened instance hidden
    /// in the tray while the first instance has a visible window in <see cref="DockItem.Group"/>).
    /// </summary>
    public bool Wants(DockItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Wants(item.Group) || _windows.Values.Any(marked => MatchesItemExecutable(item, marked.ExecutablePath));
    }

    /// <summary>
    /// Returns all active attention entries associated with <paramref name="item"/>,
    /// ordered from most recently flashed to oldest.
    /// </summary>
    public IReadOnlyList<AttentionEntry> EntriesFor(DockItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_windows.Count == 0)
        {
            return [];
        }

        var groupHandles = item.Group is { Windows.Count: > 0 } handleGroup
            ? handleGroup.Windows.Where(w => w.Handle != 0).Select(w => w.Handle).ToHashSet()
            : null;
        var groupPids = item.Group is { Windows.Count: > 0 } pidGroup
            ? pidGroup.Windows.Where(w => w.ProcessId != 0).Select(w => w.ProcessId).ToHashSet()
            : null;

        var matched = new List<AttentionEntry>();
        foreach (var pair in _windows)
        {
            var entry = pair.Value;
            var directMatch = (groupHandles is not null && groupHandles.Contains(pair.Key))
                || (groupPids is not null && entry.ProcessId != 0 && groupPids.Contains(entry.ProcessId));
            if (directMatch || MatchesItemExecutable(item, entry.ExecutablePath))
            {
                matched.Add(new AttentionEntry(
                    pair.Key,
                    entry.ProcessId,
                    entry.Unread,
                    entry.ExecutablePath,
                    entry.Sequence));
            }
        }

        if (matched.Count > 1)
        {
            matched.Sort((left, right) => right.Sequence.CompareTo(left.Sequence));
        }
        return matched;
    }

    /// <summary>
    /// Cheap pre-filter indicating whether any marked attention entry belongs to the same
    /// executable or dedicated app install directory as <paramref name="executablePath"/>.
    /// </summary>
    public bool HasMarkedExecutableMatching(string? executablePath)
    {
        if (_windows.Count == 0
            || string.IsNullOrWhiteSpace(executablePath)
            || DockFileDropRules.IsFrameHostPath(executablePath))
        {
            return false;
        }

        var normalized = WindowGrouping.NormalizeExecutablePath(executablePath);
        var root = DockQuitRules.ResolveDedicatedAppRoot(normalized);
        foreach (var marked in _windows.Values)
        {
            if (string.IsNullOrWhiteSpace(marked.ExecutablePath)
                || DockFileDropRules.IsFrameHostPath(marked.ExecutablePath))
            {
                continue;
            }

            if (DockQuitRules.MatchesExecutableOrVersionSibling(normalized, marked.ExecutablePath))
            {
                return true;
            }

            if (root is not null
                && string.Equals(
                    root,
                    DockQuitRules.ResolveDedicatedAppRoot(marked.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The unread count for the item, across its visible windows and any tray-hidden instances.</summary>
    public int UnreadCount(DockItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var best = UnreadCount(item.Group);
        foreach (var marked in _windows.Values)
        {
            if (marked.Unread > best && MatchesItemExecutable(item, marked.ExecutablePath))
            {
                best = marked.Unread;
            }
        }

        return best;
    }

    private static bool MatchesItemExecutable(DockItem item, string? candidateExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(candidateExecutablePath)
            || DockFileDropRules.IsFrameHostPath(candidateExecutablePath))
        {
            return false;
        }

        if (item.Pinned is { } pinned)
        {
            if (DockPinRules.MatchesExecutablePath(pinned, candidateExecutablePath))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(pinned.ExecutablePath)
                && !string.IsNullOrWhiteSpace(pinned.AppUserModelId)
                && !DockQuitRules.IsBrowserHostExecutable(pinned.ExecutablePath)
                && !DockFileDropRules.IsFrameHostPath(pinned.ExecutablePath)
                && DockQuitRules.MatchesExecutableOrVersionSibling(pinned.ExecutablePath, candidateExecutablePath))
            {
                return true;
            }
        }

        if (item.Group is { Windows.Count: > 0 } group)
        {
            if (group.Key.StartsWith("aumid:", StringComparison.OrdinalIgnoreCase)
                && DockQuitRules.IsBrowserHostExecutable(candidateExecutablePath))
            {
                return false;
            }

            foreach (var window in group.Windows)
            {
                if (string.IsNullOrWhiteSpace(window.ExecutablePath)
                    || DockFileDropRules.IsFrameHostPath(window.ExecutablePath))
                {
                    continue;
                }

                if (DockQuitRules.MatchesExecutableOrVersionSibling(window.ExecutablePath, candidateExecutablePath))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The largest unread count the app published with its flash; 0 when none did.</summary>
    public int UnreadCount(WindowGroup? group)
    {
        if (group is null)
        {
            return 0;
        }

        var best = 0;
        foreach (var window in group.Windows)
        {
            if (_windows.TryGetValue(window.Handle, out var direct) && direct.Unread > best)
            {
                best = direct.Unread;
            }
            if (window.ProcessId != 0)
            {
                foreach (var marked in _windows.Values)
                {
                    if (marked.ProcessId == window.ProcessId && marked.Unread > best)
                    {
                        best = marked.Unread;
                    }
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Drops only the windows the caller verified as destroyed. An enumeration
    /// snapshot must not be used here: a marked window missing from it (freshly
    /// created, hidden by DWM, a tool window) would lose its mark at once.
    /// </summary>
    public void RemoveRange(IEnumerable<nint> windows)
    {
        foreach (var window in windows)
        {
            _windows.Remove(window);
        }
    }
}

/// <summary>
/// Pure rules shared by the dock and the top bar for filtering shell/foreground flashes,
/// scoping process-level matching away from shared UWP hosts, and naming attention items.
/// </summary>
public static class AttentionFlashRules
{
    /// <summary>
    /// Dedicated desktop apps share their process across visible and hidden message windows;
    /// <c>ApplicationFrameHost.exe</c> hosts unrelated packaged apps in one process and must
    /// match strictly by window handle.
    /// </summary>
    public static uint EffectiveProcessId(uint processId, string? executablePath) =>
        processId != 0 && !DockFileDropRules.IsFrameHostPath(executablePath)
            ? processId
            : 0;

    /// <summary>
    /// System notification hosts, input-method helpers and Explorer's non-folder shell
    /// surfaces are not user applications that the dock or top bar should flag.
    /// </summary>
    public static bool IsIgnoredSystemFlash(string? executablePath, string? className)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(executablePath.Trim().Trim('"'));
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (name.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ShellHost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("SearchHost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("LockApp", StringComparison.OrdinalIgnoreCase)
            || name.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ChsIME", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase))
        {
            return className is not ("CabinetWClass" or "ExploreWClass");
        }

        return false;
    }

    public static bool ShouldRecordFlash(
        nint window,
        bool isLiveWindow,
        uint rawProcessId,
        uint currentProcessId,
        string? executablePath,
        string? className,
        nint foregroundWindow,
        uint foregroundProcessId,
        bool foregroundIsMinimized)
    {
        if (window == 0 || !isLiveWindow || rawProcessId == 0 || rawProcessId == currentProcessId)
        {
            return false;
        }

        if (IsIgnoredSystemFlash(executablePath, className))
        {
            return false;
        }

        var effectiveProcessId = EffectiveProcessId(rawProcessId, executablePath);
        if (!foregroundIsMinimized && foregroundWindow != 0)
        {
            if (window == foregroundWindow)
            {
                return false;
            }
            if (effectiveProcessId != 0 && effectiveProcessId == foregroundProcessId)
            {
                return false;
            }
        }

        return true;
    }

    public static string ChooseDisplayName(
        string? fileDescription,
        string? groupDisplayName,
        string? executablePath,
        string? windowTitle)
    {
        if (AppDisplayNameRules.Normalize(fileDescription, executablePath) is { } normalizedDescription)
        {
            return normalizedDescription;
        }

        if (AppDisplayNameRules.Normalize(groupDisplayName, executablePath) is { } normalizedGroup)
        {
            return normalizedGroup;
        }

        if (!string.IsNullOrWhiteSpace(executablePath)
            && !DockFileDropRules.IsFrameHostPath(executablePath))
        {
            var fileName = Path.GetFileNameWithoutExtension(executablePath.Trim().Trim('"'));
            if (AppDisplayNameRules.Normalize(fileName, executablePath) is { } normalizedExe)
            {
                return normalizedExe;
            }
        }

        if (AppDisplayNameRules.Normalize(windowTitle) is { } normalizedTitle)
        {
            return normalizedTitle;
        }

        return "消息";
    }
}
