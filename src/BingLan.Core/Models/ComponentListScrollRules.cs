namespace BingLan.Core.Models;

/// <summary>
/// Pure calculations for the desktop-component list overflow hint shown when more cards sit
/// below the visible viewport.
/// </summary>
public static class ComponentListScrollRules
{
    /// <summary>
    /// Estimates how many items remain hidden below the bottom edge of the viewport.
    /// Returns 0 when all items fit or the list is already scrolled to the end.
    /// </summary>
    public static int ComputeHiddenBelowCount(
        int totalItemCount,
        double verticalOffset,
        double viewportHeight,
        double extentHeight,
        bool canContentScroll)
    {
        if (totalItemCount <= 0
            || !double.IsFinite(verticalOffset)
            || !double.IsFinite(viewportHeight)
            || !double.IsFinite(extentHeight)
            || viewportHeight <= 0d
            || extentHeight <= 0d)
        {
            return 0;
        }

        var clampedOffset = Math.Max(0d, verticalOffset);
        var remaining = extentHeight - (clampedOffset + viewportHeight);
        if (canContentScroll)
        {
            if (remaining <= 0.05d)
            {
                return 0;
            }

            return Math.Clamp((int)Math.Ceiling(remaining - 0.05d), 1, totalItemCount);
        }

        if (remaining <= 1.0d)
        {
            return 0;
        }

        var averageItemHeight = extentHeight / totalItemCount;
        if (averageItemHeight <= 0d)
        {
            return 0;
        }

        return Math.Clamp((int)Math.Ceiling((remaining / averageItemHeight) - 0.05d), 1, totalItemCount);
    }

    /// <summary>Formats the bottom overflow hint text for the component list.</summary>
    public static string FormatOverflowHint(int hiddenBelowCount) =>
        hiddenBelowCount > 0 ? $"下方还有 {hiddenBelowCount} 项" : string.Empty;
}
