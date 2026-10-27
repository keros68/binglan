namespace BingLan.Core.Dock;

/// <summary>
/// Keeps the dock's AppBar from trading position notifications with another AppBar
/// (for example a toolbar docked to the screen edge by a file manager). Each ABM_SETPOS
/// makes Explorer notify every other AppBar; two bars that both answer by setting their
/// position again can loop and keep re-laying out every window on the desktop.
/// The dock re-submits only a changed strip, and answers a burst of notifications at
/// most once per <see cref="DeferredDelay"/>.
/// </summary>
public sealed class AppBarPositionGuard
{
    /// <summary>Window in which notifications count towards a burst.</summary>
    public static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(2);

    /// <summary>Notifications answered at once within <see cref="BurstWindow"/>.</summary>
    public const int BurstLimit = 6;

    /// <summary>Delay before a notification held back during a burst is answered.</summary>
    public static readonly TimeSpan DeferredDelay = TimeSpan.FromSeconds(1);

    private readonly Queue<long> _recent = new();
    private PixelRect? _submitted;

    /// <summary>
    /// Records a position-changed notification. False means a burst is in progress and
    /// the caller should answer once after <see cref="DeferredDelay"/> instead.
    /// </summary>
    public bool TryHandleNow(long nowMilliseconds)
    {
        var windowStart = nowMilliseconds - (long)BurstWindow.TotalMilliseconds;
        while (_recent.Count > 0 && _recent.Peek() <= windowStart)
        {
            _recent.Dequeue();
        }

        _recent.Enqueue(nowMilliseconds);
        return _recent.Count <= BurstLimit;
    }

    /// <summary>True when the strip differs from the one Explorer already holds.</summary>
    public bool NeedsSubmit(PixelRect strip) => _submitted != strip;

    public void Submitted(PixelRect strip) => _submitted = strip;

    /// <summary>Forget the held strip after the AppBar is registered again or released.</summary>
    public void Reset() => _submitted = null;
}
