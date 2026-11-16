namespace BingLan.Core.Models;

/// <summary>
/// Rules governing a file box's optional 5-second idle auto-collapse drawer behaviour.
/// </summary>
public static class FileBoxAutoCollapseRules
{
    public static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    public const double ClickMoveThresholdPixels = 4d;

    public static bool ShouldRunCountdown(
        bool autoCollapseEnabled,
        bool isCollapsed,
        bool isPointerOver,
        bool isTitleEditing,
        bool isInteractionOpen,
        bool isDragging) =>
        autoCollapseEnabled
        && !isCollapsed
        && !isPointerOver
        && !isTitleEditing
        && !isInteractionOpen
        && !isDragging;

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
