using BingLan.Core.Models;

namespace BingLan.Core.Dock;

/// <summary>
/// Timing and geometry rules for the morph between the dock row and its floating
/// handle: the row shrinks into the handle when smart-hidden and grows back out of
/// it on reveal, and reappearing at the strip fades in with a slight grow from the
/// row's own bottom edge. The system "show animations" switch turns every morph off,
/// exactly like the hover zoom; Reduced keeps only a small in-place scale without
/// the travel toward the handle.
/// </summary>
public static class DockMorphMotion
{
    /// <summary>True when any morph should run at all for this motion level.</summary>
    public static bool IsAnimated(MotionLevel motion, bool systemAnimationsEnabled) =>
        systemAnimationsEnabled && motion != MotionLevel.Off;

    /// <summary>True when the row should travel toward the handle while morphing.</summary>
    public static bool IsTravelEnabled(MotionLevel motion) => motion == MotionLevel.Standard;

    public static TimeSpan ExpandDuration(MotionLevel motion) =>
        motion == MotionLevel.Reduced
            ? TimeSpan.FromMilliseconds(100)
            : TimeSpan.FromMilliseconds(200);

    public static TimeSpan CollapseDuration(MotionLevel motion) =>
        motion == MotionLevel.Reduced
            ? TimeSpan.FromMilliseconds(100)
            : TimeSpan.FromMilliseconds(360);

    /// <summary>
    /// Stepping aside for a system surface (the taskbar, Start, a tray panel): quick
    /// enough to clear before the taskbar finishes sliding in.
    /// </summary>
    public static TimeSpan YieldDuration(MotionLevel motion) =>
        motion == MotionLevel.Reduced
            ? TimeSpan.FromMilliseconds(80)
            : TimeSpan.FromMilliseconds(140);

    public static TimeSpan ReappearDuration(MotionLevel motion) =>
        motion == MotionLevel.Reduced
            ? TimeSpan.FromMilliseconds(80)
            : TimeSpan.FromMilliseconds(200);

    /// <summary>The row's scale at the handle end of a morph; 1 is the resting row.</summary>
    public static double MorphScale(MotionLevel motion) =>
        motion == MotionLevel.Reduced ? 0.9d : 0.15d;

    /// <summary>
    /// The row's starting scale when it reappears at its strip: a slight grow from
    /// its own bottom edge. 1 means a pure fade (Reduced).
    /// </summary>
    public static double ReappearScale(MotionLevel motion) =>
        motion == MotionLevel.Reduced ? 1d : 0.85d;

    /// <summary>The landing bounce of the handle glyph after the row collapsed into it.</summary>
    public static TimeSpan HandlePulseDuration => TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Converts a physical pixel point (the handle's centre) into the surface's own
    /// DIP coordinates for the window placement <paramref name="placement"/>. The
    /// point may lie outside the surface: a ScaleTransform centred there pulls the
    /// shrinking row toward it.
    /// </summary>
    public static (double X, double Y) ToElementPoint(
        int pixelX,
        int pixelY,
        PixelRect placement,
        double dpiScale)
    {
        var scale = dpiScale <= 0 ? 1d : dpiScale;
        return ((pixelX - placement.Left) / scale, (pixelY - placement.Top) / scale);
    }

    /// <summary>
    /// The union of the dock's strip rect and the handle: while the strip row shrinks
    /// toward a distant handle, the window itself must cover the whole travel path or
    /// the row clips at the strip's edge.
    /// </summary>
    public static PixelRect TravelBounds(PixelRect dock, PixelRect handle) => new(
        Math.Min(dock.Left, handle.Left),
        Math.Min(dock.Top, handle.Top),
        Math.Max(dock.Right, handle.Right),
        Math.Max(dock.Bottom, handle.Bottom));
}
