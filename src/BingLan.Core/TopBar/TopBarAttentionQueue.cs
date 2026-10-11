using BingLan.Core.Models;

namespace BingLan.Core.TopBar;

/// <summary>
/// Windows that asked for attention, in the order they asked. The dock's own tracker is
/// an unordered set; the top bar shows the apps that flashed first, so it keeps order.
/// </summary>
public sealed class TopBarAttentionQueue
{
    private readonly List<nint> _windows = [];

    public IReadOnlyList<nint> OrderedWindows => _windows;

    /// <summary>Records a flash request; false when the window had already asked.</summary>
    public bool Flashed(nint window)
    {
        if (window == 0 || _windows.Contains(window))
        {
            return false;
        }

        _windows.Add(window);
        return true;
    }

    /// <summary>The window was activated or closed; false when it was not in the queue.</summary>
    public bool Cleared(nint window) => _windows.Remove(window);

    /// <summary>Drops windows that no longer exist among the running ones.</summary>
    public void Retain(IEnumerable<nint> running)
    {
        var alive = running.ToHashSet();
        _windows.RemoveAll(window => !alive.Contains(window));
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
