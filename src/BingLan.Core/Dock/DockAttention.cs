namespace BingLan.Core.Dock;

/// <summary>
/// Windows that asked for attention by flashing (a new chat message, a finished
/// download), until they are activated or closed. The dock marks their apps, and
/// shows the unread count an app published in the flashed window's title.
/// </summary>
public sealed class DockAttention
{
    private readonly Dictionary<nint, (uint ProcessId, int Unread)> _windows = [];

    public int Count => _windows.Count;

    /// <summary>The marked window handles, for liveness sweeps by the caller.</summary>
    public IReadOnlyCollection<nint> MarkedWindows => _windows.Keys;

    /// <summary>Records a flash request with the owning process and optional unread count.</summary>
    public bool Flashed(nint window, uint processId, int? unread = null) =>
        window != 0 && _windows.TryAdd(window, (processId, unread ?? 0));

    /// <summary>The window was activated or closed; false when it was not marked.</summary>
    public bool Cleared(nint window) => _windows.Remove(window);

    /// <summary>
    /// Whether any of an app's windows is waiting for attention. Apps flash through
    /// hidden message windows that never show up in an enumeration, so a window counts
    /// when either its handle or its process is marked.
    /// </summary>
    public bool Wants(WindowGroup? group) =>
        group is not null && group.Windows.Any(window =>
            _windows.ContainsKey(window.Handle)
            || _windows.Values.Any(marked => marked.ProcessId == window.ProcessId));

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
            foreach (var marked in _windows.Values)
            {
                if (marked.ProcessId == window.ProcessId && marked.Unread > best)
                {
                    best = marked.Unread;
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
