using System.IO;
using BingLan.Core.Models;

namespace BingLan.Core.Dock;

/// <summary>
/// Windows that asked for attention by flashing (a new chat message, a finished
/// download), until they are activated or closed. The dock marks their apps, and
/// shows the unread count an app published in the flashed window's title.
/// </summary>
public sealed class DockAttention
{
    private readonly Dictionary<nint, (uint ProcessId, int Unread, string? ExecutablePath)> _windows = [];

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
        if (_windows.TryGetValue(window, out var existing))
        {
            if (nextUnread > existing.Unread)
            {
                _windows[window] = (
                    existing.ProcessId != 0 ? existing.ProcessId : processId,
                    nextUnread,
                    existing.ExecutablePath ?? executablePath);
                return true;
            }
            return false;
        }

        _windows.Add(window, (processId, nextUnread, executablePath));
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

    /// <summary>One window was destroyed; other windows of the same process keep their marks.</summary>
    public bool Closed(nint window) => window != 0 && _windows.Remove(window);

    /// <summary>
    /// Whether any of an app's windows is waiting for attention. Apps flash through
    /// hidden message windows that never show up in an enumeration, so a window counts
    /// when either its handle or its process is marked.
    /// </summary>
    /// <summary>
    /// Returns 2 when this exact window handle flashed, 1 when a hidden/other window of the
    /// same process flashed, or 0 when the window is not requesting attention.
    /// </summary>
    public int AttentionPriority(TrackedWindow window)
    {
        if (window.Handle != 0 && _windows.ContainsKey(window.Handle))
        {
            return 2;
        }

        if (window.ProcessId != 0
            && _windows.Values.Any(marked => marked.ProcessId == window.ProcessId))
        {
            return 1;
        }

        return 0;
    }

    public bool WantsWindow(TrackedWindow window) => AttentionPriority(window) > 0;

    public bool Wants(WindowGroup? group) =>
        group is not null && group.Windows.Any(WantsWindow);

    /// <summary>
    /// Whether the item's app is waiting for attention. An app tucked into the tray has
    /// no visible window and so no running group; its pinned item still stands for it
    /// when the flash came from a process whose exe the pin matches.
    /// </summary>
    public bool Wants(DockItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Wants(item.Group) || (item.Group is null && WantsPinned(item.Pinned));
    }

    /// <summary>The unread count for the item, from its windows or, with none, its pin.</summary>
    public int UnreadCount(DockItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Group is not null)
        {
            return UnreadCount(item.Group);
        }

        var best = 0;
        if (item.Pinned is { } pinned)
        {
            foreach (var marked in _windows.Values)
            {
                if (marked.ExecutablePath is { Length: > 0 } path
                    && DockPinRules.MatchesExecutablePath(pinned, path)
                    && marked.Unread > best)
                {
                    best = marked.Unread;
                }
            }
        }

        return best;
    }

    private bool WantsPinned(DockPinnedApp? pinned) =>
        pinned is not null
        && _windows.Values.Any(marked => marked.ExecutablePath is { Length: > 0 } path
            && DockPinRules.MatchesExecutablePath(pinned, path));

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
        if (!string.IsNullOrWhiteSpace(fileDescription))
        {
            return fileDescription.Trim();
        }

        if (!string.IsNullOrWhiteSpace(groupDisplayName))
        {
            return groupDisplayName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(executablePath)
            && !DockFileDropRules.IsFrameHostPath(executablePath))
        {
            var fileName = Path.GetFileNameWithoutExtension(executablePath.Trim().Trim('"'));
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }

        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            return windowTitle.Trim();
        }

        return "消息";
    }
}
