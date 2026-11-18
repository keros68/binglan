namespace BingLan.Core.TopBar;

/// <summary>
/// Apps that asked for attention, in the order they asked. Dedicated apps often flash
/// through multiple hidden message windows, so flashes sharing a non-zero process ID
/// coalesce into one entry and clear together as soon as any window of that app activates.
/// </summary>
public sealed class TopBarAttentionQueue
{
    private readonly List<AttentionEntry> _entries = [];
    private readonly List<nint> _windows = [];

    public IReadOnlyList<nint> OrderedWindows => _windows;

    /// <summary>Records a flash request with its process; false when the window or app had already asked.</summary>
    public bool Flashed(nint window, uint processId)
    {
        if (window == 0)
        {
            return false;
        }

        foreach (var existing in _entries)
        {
            if (existing.Window == window || existing.ExtraWindows.Contains(window))
            {
                return false;
            }
        }

        if (processId != 0 && _entries.FirstOrDefault(entry => entry.ProcessId == processId) is { } sameProcess)
        {
            sameProcess.ExtraWindows.Add(window);
            return false;
        }

        _entries.Add(new AttentionEntry(window, processId));
        _windows.Add(window);
        return true;
    }

    /// <summary>The process the window belonged to when it flashed, for name lookups and activation.</summary>
    public uint ProcessOf(nint window) =>
        _entries.FirstOrDefault(entry => entry.Window == window || entry.ExtraWindows.Contains(window))?.ProcessId ?? 0;

    /// <summary>
    /// An app window was activated; drops the window itself and, when <paramref name="processId"/>
    /// names a dedicated app process, any hidden message window queued for that process.
    /// </summary>
    public bool Cleared(nint window, uint processId = 0)
    {
        var removed = _entries.RemoveAll(entry =>
            (window != 0 && (entry.Window == window || entry.ExtraWindows.Contains(window)))
            || (processId != 0 && entry.ProcessId == processId));
        if (removed == 0)
        {
            return false;
        }

        SyncWindows();
        return true;
    }

    /// <summary>One window handle was destroyed; promotes any remaining flashed handle of that app.</summary>
    public bool Closed(nint window) =>
        window != 0 && RemoveDeadHandles([window]);

    /// <summary>
    /// Drops only the windows the caller verified as destroyed. An enumeration
    /// snapshot must not be used here: a flash often names a hidden message window
    /// that never appears in it, and the queue would drop the notice at once.
    /// </summary>
    public void RemoveRange(IEnumerable<nint> windows) =>
        RemoveDeadHandles(windows.ToHashSet());

    /// <summary>
    /// The windows to show, capped at <paramref name="maximum"/>, and how many more are
    /// waiting behind them.
    /// </summary>
    public static (IReadOnlyList<nint> Shown, int Overflow) ResolveDisplay(
        IReadOnlyList<nint> ordered,
        int maximum)
    {
        var count = Math.Max(0, ordered.Count);
        var shownCount = Math.Min(Math.Max(0, maximum), count);
        return (ordered.Take(shownCount).ToList(), count - shownCount);
    }

    private bool RemoveDeadHandles(HashSet<nint> dead)
    {
        if (dead.Count == 0 || _entries.Count == 0)
        {
            return false;
        }

        var changed = false;
        for (var index = _entries.Count - 1; index >= 0; index--)
        {
            var entry = _entries[index];
            entry.ExtraWindows.RemoveWhere(dead.Contains);
            if (!dead.Contains(entry.Window))
            {
                continue;
            }

            changed = true;
            if (entry.ExtraWindows.Count > 0)
            {
                var replacement = entry.ExtraWindows.First();
                entry.ExtraWindows.Remove(replacement);
                entry.Window = replacement;
            }
            else
            {
                _entries.RemoveAt(index);
            }
        }

        if (changed)
        {
            SyncWindows();
        }
        return changed;
    }

    private void SyncWindows()
    {
        _windows.Clear();
        foreach (var entry in _entries)
        {
            _windows.Add(entry.Window);
        }
    }

    private sealed class AttentionEntry(nint window, uint processId)
    {
        public nint Window { get; set; } = window;
        public uint ProcessId { get; } = processId;
        public HashSet<nint> ExtraWindows { get; } = [];
    }
}
