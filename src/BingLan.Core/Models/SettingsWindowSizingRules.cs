namespace BingLan.Core.Models;

public readonly record struct SettingsWindowBounds(
    double Left,
    double Top,
    double Width,
    double Height,
    double MinWidth,
    double MinHeight);

/// <summary>
/// Fits the unified settings window and its minimum resize floor within the host
/// monitor's working area in DIPs while preserving the default layout bounds when
/// the working area is large enough.
/// </summary>
public static class SettingsWindowSizingRules
{
    public const double DefaultWidth = 1180d;
    public const double DefaultHeight = 820d;
    public const double DefaultMinWidth = 980d;
    public const double DefaultMinHeight = 700d;

    public static SettingsWindowBounds Resolve(
        double workAreaLeft,
        double workAreaTop,
        double workAreaWidth,
        double workAreaHeight)
    {
        var safeLeft = double.IsFinite(workAreaLeft) ? workAreaLeft : 0d;
        var safeTop = double.IsFinite(workAreaTop) ? workAreaTop : 0d;
        var safeWidth = double.IsFinite(workAreaWidth) && workAreaWidth > 0d
            ? workAreaWidth
            : DefaultWidth;
        var safeHeight = double.IsFinite(workAreaHeight) && workAreaHeight > 0d
            ? workAreaHeight
            : DefaultHeight;

        var minWidth = Math.Min(DefaultMinWidth, safeWidth);
        var minHeight = Math.Min(DefaultMinHeight, safeHeight);
        var width = Math.Max(minWidth, Math.Min(DefaultWidth, safeWidth));
        var height = Math.Max(minHeight, Math.Min(DefaultHeight, safeHeight));
        var left = safeLeft + Math.Max(0d, (safeWidth - width) / 2d);
        var top = safeTop + Math.Max(0d, (safeHeight - height) / 2d);

        return new SettingsWindowBounds(left, top, width, height, minWidth, minHeight);
    }
}
