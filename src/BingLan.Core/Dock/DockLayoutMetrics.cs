namespace BingLan.Core.Dock;

public static class DockLayoutMetrics
{
    public const double DockThicknessDip = 68;
    public const double EdgeGapDip = 28;
    public const double AxisPaddingDip = 14;
    public const double ItemCellDip = 64;
    public const double EmptyLengthDip = 76;

    /// <summary>
    /// How much of the strip width the dock row may span. The reserved strip runs the
    /// full width of the monitor and the row floats on it, so a wider limit takes no
    /// space from windows; it only has to leave room for the handle to breathe.
    /// </summary>
    public const double MaxExtentFraction = 0.92;

    /// <summary>The expanded-from-handle row does cross the screen; it keeps the old cap.</summary>
    public const double HandleMaxExtentFraction = 0.65;

    /// <summary>Icons shrink no further than this before the row overflows and scrolls.</summary>
    public const double MinIconDip = 28;

    private const double DefaultIconDip = 40;

    /// <summary>Width of the divider between pinned apps and other running apps.</summary>
    public const double DividerDip = 13;

    /// <summary>The space one app takes along the dock: its button plus the gap beside it.</summary>
    public static double ItemCell(double iconSize) => ItemCellDip - DefaultIconDip + iconSize;

    /// <summary>The dock's height for an icon size.</summary>
    public static double Thickness(double iconSize) => DockThicknessDip - DefaultIconDip + iconSize;

    public static double ContentLengthForItems(int itemCount, double iconSize = DefaultIconDip)
    {
        if (itemCount <= 0)
        {
            return EmptyLengthDip;
        }

        return (2 * AxisPaddingDip) + (itemCount * ItemCell(iconSize));
    }

    /// <summary>
    /// The icon size that fits every item into the available length: the preferred size
    /// while it fits, then proportionally smaller, never below <see cref="MinIconDip"/>.
    /// The row height keeps the preferred size; only cells shrink.
    /// </summary>
    public static double FitIconSize(int itemCount, double dividersDip, double availableLengthDip, double preferredIconSize)
    {
        if (itemCount <= 0 || availableLengthDip <= 0)
        {
            return preferredIconSize;
        }

        var cells = availableLengthDip - (2 * AxisPaddingDip) - dividersDip;
        if (cells <= itemCount * ItemCell(MinIconDip))
        {
            return MinIconDip;
        }

        var fitted = Math.Floor((cells / itemCount) - (ItemCellDip - DefaultIconDip));
        return Math.Clamp(fitted, MinIconDip, Math.Max(MinIconDip, preferredIconSize));
    }
}
