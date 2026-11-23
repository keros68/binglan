using BingLan.Core.Models;

namespace BingLan.Core.Dock;

/// <summary>
/// The bounce an app's dock icon makes when the app flashes for attention (a chat message,
/// a finished download): a few short hops, never an endless loop, skipped when motion is
/// off, and at most once in a while per app so a chatty app does not keep it jumping.
/// </summary>
public static class DockBounceRules
{
    public const double MinAmplitudeDip = 6d;
    public const double MaxAmplitudeDip = 14d;

    /// <summary>The pause before the same app's icon may bounce again.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(8);

    public static readonly TimeSpan RiseDuration = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan FallDuration = TimeSpan.FromMilliseconds(180);

    /// <summary>
    /// Hops per attention event: three at the standard motion level, one gentle hop when
    /// motion is reduced, none when it is off, when the setting is off, or when Windows
    /// has its animations switched off.
    /// </summary>
    public static int BounceCount(bool enabled, MotionLevel motion, bool systemAnimationsEnabled)
    {
        if (!enabled || !systemAnimationsEnabled)
        {
            return 0;
        }

        return motion switch
        {
            MotionLevel.Off => 0,
            MotionLevel.Reduced => 1,
            _ => 3
        };
    }

    /// <summary>How far the icon rises, in DIPs: a third of its size, within a small range.</summary>
    public static double AmplitudeDip(double iconSizeDip, MotionLevel motion)
    {
        var amplitude = Math.Clamp(iconSizeDip * 0.3d, MinAmplitudeDip, MaxAmplitudeDip);
        return motion == MotionLevel.Reduced ? amplitude * 0.6d : amplitude;
    }

    /// <summary>Whether enough time has passed since the app's icon last bounced.</summary>
    public static bool IntervalElapsed(DateTimeOffset? lastBounce, DateTimeOffset now) =>
        lastBounce is not { } last || last > now || now - last >= MinInterval;
}
