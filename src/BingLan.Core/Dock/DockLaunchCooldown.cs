namespace BingLan.Core.Dock;

/// <summary>
/// Per-app short launch cooldown so rapid left-clicks on a slow machine while an app is still
/// starting up do not spawn duplicate processes (e.g., multiple WeChat/QQ login windows).
/// Explicit "Launch new instance" actions bypass this cooldown.
/// </summary>
public sealed class DockLaunchCooldown(Func<long> clock)
{
    public const int CooldownMs = 2500;

    private readonly Dictionary<string, long> _lastLaunched = new(StringComparer.OrdinalIgnoreCase);

    public bool TryAcquire(string? appKey)
    {
        if (string.IsNullOrWhiteSpace(appKey))
        {
            return true;
        }

        var now = clock();
        if (_lastLaunched.TryGetValue(appKey, out var previous) && now - previous < CooldownMs)
        {
            return false;
        }

        _lastLaunched[appKey] = now;
        return true;
    }

    public void Clear(string? appKey)
    {
        if (!string.IsNullOrWhiteSpace(appKey))
        {
            _lastLaunched.Remove(appKey);
        }
    }
}
