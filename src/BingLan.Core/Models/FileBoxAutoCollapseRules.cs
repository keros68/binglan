namespace BingLan.Core.Models;

/// <summary>
/// Rules governing a file box's optional idle auto-collapse drawer behaviour.
/// </summary>
public static class FileBoxAutoCollapseRules
{
    public const double DefaultDelaySeconds = 5d;
    public const double MinimumDelaySeconds = 0.5d;
    public const double MaximumDelaySeconds = 30d;
    public static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(DefaultDelaySeconds);
    public static readonly TimeSpan HoverExpandDelay = TimeSpan.FromMilliseconds(180);
    public static IReadOnlyList<double> DelayOptionsSeconds { get; } = [0.5d, 1d, 3d, 5d, 10d];
    public const double ClickMoveThresholdPixels = 4d;

    public static double CoerceDelaySeconds(double? seconds)
    {
        if (seconds is not { } value || !double.IsFinite(value) || value <= 0d)
        {
            return DefaultDelaySeconds;
        }

        return Math.Clamp(value, MinimumDelaySeconds, MaximumDelaySeconds);
    }

    public static TimeSpan ResolveDelay(double? seconds) =>
        TimeSpan.FromSeconds(CoerceDelaySeconds(seconds));

    public static string FormatShortDelay(double? seconds)
    {
        var value = CoerceDelaySeconds(seconds);
        return Math.Abs(value - Math.Round(value)) < 0.001d
            ? $"{Math.Round(value):0} 秒"
            : $"{value:0.#} 秒";
    }

    public static bool ShouldRunCountdown(
        bool autoCollapseEnabled,
        bool isCollapsed,
        bool isPointerOver,
        bool isTitleEditing,
        bool isInteractionOpen,
        bool isDragging,
        bool isPinnedOpen = false) =>
        autoCollapseEnabled
        && !isCollapsed
        && !isPinnedOpen
        && !isPointerOver
        && !isTitleEditing
        && !isInteractionOpen
        && !isDragging;

    public static bool ShouldRunHoverExpandTimer(
        bool hoverExpandEnabled,
        bool isCollapsed,
        bool isEdgeDocked,
        bool isPointerOverTitleBar,
        bool isTitleEditing,
        bool isInteractionOpen,
        bool isDragging,
        bool suppressUntilPointerLeaves = false) =>
        hoverExpandEnabled
        && isCollapsed
        && !isEdgeDocked
        && isPointerOverTitleBar
        && !isTitleEditing
        && !isInteractionOpen
        && !isDragging
        && !suppressUntilPointerLeaves;

    public static bool CanClickTitleBarToExpand(
        bool autoCollapseEnabled,
        bool isCollapsed,
        bool isTitleEditing,
        double deltaX,
        double deltaY) =>
        autoCollapseEnabled
        && isCollapsed
        && !isTitleEditing
        && (deltaX * deltaX + deltaY * deltaY) <= ClickMoveThresholdPixels * ClickMoveThresholdPixels;
}
