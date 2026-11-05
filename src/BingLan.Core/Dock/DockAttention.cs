namespace BingLan.Core.Dock;

/// <summary>
/// Windows that asked for attention by flashing (a new chat message, a finished
/// download), until they are activated or closed. The dock marks their apps.
/// </summary>
public sealed class DockAttention
{
    private readonly Dictionary<nint, uint> _windows = [];

    public int Count => _windows.Count;

    /// <summary>The marked window handles, for liveness sweeps by the caller.</summary>
    public IReadOnlyCollection<nint> MarkedWindows => _windows.Keys;

    /// <summary>Records a flash request with the owning process; false when already marked.</summary>
    public bool Flashed(nint window, uint processId) =>
        window != 0 && _windows.TryAdd(window, processId);

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
            || _windows.Values.Contains(window.ProcessId));

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
