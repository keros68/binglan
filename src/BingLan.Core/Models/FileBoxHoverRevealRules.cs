namespace BingLan.Core.Models;

/// <summary>
/// Pure rules governing a file box's optional idle-hidden / hover-reveal mode
/// (fully transparent while idle, revealing smoothly when the pointer or a drag enters).
/// </summary>
public static class FileBoxHoverRevealRules
{
    public const double DefaultHideDelaySeconds = 0.5d;
    public const double MinimumHideDelaySeconds = 0.1d;
    public const double MaximumHideDelaySeconds = 5d;
    public const int RevealAnimationMilliseconds = 120;
    public const int HideAnimationMilliseconds = 180;

    public static IReadOnlyList<double> DelayOptionsSeconds { get; } = [0.2d, 0.5d, 1d, 2d];

    public static double CoerceDelaySeconds(double? seconds)
    {
        if (seconds is not { } value || !double.IsFinite(value) || value <= 0d)
        {
            return DefaultHideDelaySeconds;
        }

        return Math.Clamp(value, MinimumHideDelaySeconds, MaximumHideDelaySeconds);
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

    /// <summary>
    /// Returns true when the file box's visual surface must remain visible rather than fading out.
    /// </summary>
    public static bool ShouldStayVisible(
        bool hideWhenIdle,
        bool isEdgeDocked,
        bool isHighContrast,
        bool isPointerOver,
        bool isTitleEditing,
        bool isInteractionOpen,
        bool isDragging,
        bool isSelected,
        bool isKeyboardFocusWithin,
        bool isPeeked,
        bool isSettingsHighlighted) =>
        !hideWhenIdle
        || isEdgeDocked
        || isHighContrast
        || isPointerOver
        || isTitleEditing
        || isInteractionOpen
        || isDragging
        || isSelected
        || isKeyboardFocusWithin
        || isPeeked
        || isSettingsHighlighted;

    /// <summary>
    /// Returns true when the idle hide countdown should run to fade the box out.
    /// </summary>
    public static bool ShouldRunHideTimer(
        bool hideWhenIdle,
        bool isEdgeDocked,
        bool isHighContrast,
        bool isPointerOver,
        bool isTitleEditing,
        bool isInteractionOpen,
        bool isDragging,
        bool isSelected,
        bool isKeyboardFocusWithin,
        bool isPeeked,
        bool isSettingsHighlighted) =>
        hideWhenIdle
        && !ShouldStayVisible(
            hideWhenIdle,
            isEdgeDocked,
            isHighContrast,
            isPointerOver,
            isTitleEditing,
            isInteractionOpen,
            isDragging,
            isSelected,
            isKeyboardFocusWithin,
            isPeeked,
            isSettingsHighlighted);
}
