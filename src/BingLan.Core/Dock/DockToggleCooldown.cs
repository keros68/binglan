namespace BingLan.Core.Dock;

/// <summary>
/// Remembers which window the dock just minimized. The system's foreground pointer
/// keeps naming that window for a moment, so without this memory a quick second
/// click reads as "it is still the foreground app" and minimizes it again - a no-op
/// the user feels as the dock not responding.
/// </summary>
public sealed class DockToggleCooldown(Func<long> clock)
{
    public const int RetentionMs = 800;

    private nint _minimizedWindow;
    private long _minimizedAt;

    /// <summary>Records that the dock minimized this window right now.</summary>
    public void MarkMinimized(nint window)
    {
        _minimizedWindow = window;
        _minimizedAt = clock();
    }

    /// <summary>Whether this window was minimized by the dock within the retention window.</summary>
    public bool RecentlyMinimized(nint window) =>
        window != 0
        && _minimizedWindow == window
        && clock() - _minimizedAt < RetentionMs;

    /// <summary>Drops the memory once the window is back, so a third click can minimize again.</summary>
    public void Clear(nint window)
    {
        if (_minimizedWindow == window)
        {
            _minimizedWindow = 0;
        }
    }
}
