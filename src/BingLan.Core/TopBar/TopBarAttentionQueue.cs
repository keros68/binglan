using BingLan.Core.Models;

namespace BingLan.Core.TopBar;

/// <summary>
/// Windows that asked for attention, in the order they asked. The dock's own tracker is
/// an unordered set; the top bar shows the apps that flashed first, so it keeps order.
/// </summary>
public sealed class TopBarAttentionQueue
{
    private readonly List<nint> _windows = [];
    private readonly Dictionary<nint, uint> _processes = [];

    public IReadOnlyList<nint> OrderedWindows => _windows;

    /// <summary>Records a flash request with its process; false when the window had already asked.</summary>
    public bool Flashed(nint window, uint processId)
    {
        if (window == 0 || _windows.Contains(window))
        {
            return false;
        }

        _windows.Add(window);
        _processes[window] = processId;
        return true;
    }

    /// <summary>The process the window belonged to when it flashed, for name lookups.</summary>
    public uint ProcessOf(nint window) =>
        _processes.TryGetValue(window, out var processId) ? processId : 0;

    /// <summary>The window was activated or closed; false when it was not in the queue.</summary>
    public bool Cleared(nint window)
    {
        _processes.Remove(window);
        return _windows.Remove(window);
    }

    /// <summary>
    /// Drops only the windows the caller verified as destroyed. An enumeration
    /// snapshot must not be used here: a flash often names a hidden message window
    /// that never appears in it, and the queue would drop the notice at once.
    /// </summary>
    public void RemoveRange(IEnumerable<nint> windows)
    {
        var dead = windows.ToHashSet();
        _windows.RemoveAll(window => dead.Contains(window));
        foreach (var window in dead)
        {
            _processes.Remove(window);
        }
    }

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
}
