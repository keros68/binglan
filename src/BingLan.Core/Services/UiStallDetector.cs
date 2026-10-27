namespace BingLan.Core.Services;

/// <summary>A stall of the UI thread began or ended, with how long the probe has waited.</summary>
public readonly record struct UiStallChange(bool Started, long ElapsedMilliseconds);

/// <summary>
/// Tracks one probe at a time sent to the UI thread. A probe unanswered for longer
/// than the threshold marks a stall; its answer marks the end. Times are milliseconds
/// from one monotonic clock.
/// </summary>
public sealed class UiStallDetector(long thresholdMilliseconds)
{
    private long? _pendingSince;
    private bool _stalled;

    public bool IsStalled => _stalled;

    /// <summary>True when no probe is outstanding and a new one may be sent.</summary>
    public bool CanSend => _pendingSince is null;

    public void Sent(long now) => _pendingSince ??= now;

    public UiStallChange? Answered(long now)
    {
        if (_pendingSince is not { } since)
        {
            return null;
        }

        _pendingSince = null;
        if (!_stalled)
        {
            return null;
        }

        _stalled = false;
        return new UiStallChange(false, now - since);
    }

    public UiStallChange? Check(long now)
    {
        if (_stalled || _pendingSince is not { } since || now - since < thresholdMilliseconds)
        {
            return null;
        }

        _stalled = true;
        return new UiStallChange(true, now - since);
    }
}
