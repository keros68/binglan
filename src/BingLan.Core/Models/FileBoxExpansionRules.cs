using BingLan.Core.Dock;

namespace BingLan.Core.Models;

/// <summary>Bottom-anchored file box geometry, using physical pixels for mixed-DPI desktops.</summary>
public static class FileBoxExpansionRules
{
    public static PixelRect ResolveUpwardExpandedBounds(
        PixelRect current,
        PixelRect workArea,
        double desiredHeightDip,
        double dpiScaleY,
        double minimumHeightDip = 166d)
    {
        if (!current.HasArea || !workArea.HasArea || !double.IsFinite(dpiScaleY) || dpiScaleY <= 0d)
        {
            return current;
        }

        var minimum = Math.Max(1, (int)Math.Ceiling(minimumHeightDip * dpiScaleY));
        var desired = double.IsFinite(desiredHeightDip) && desiredHeightDip > 0d
            ? Math.Max(minimum, (int)Math.Round(desiredHeightDip * dpiScaleY))
            : minimum;
        // Keep the title's bottom fixed whenever there is room for a usable expanded box.
        // A title too close to the top edge must move down to retain the minimum height.
        var bottom = Math.Clamp(current.Bottom, workArea.Top + Math.Min(minimum, workArea.Height), workArea.Bottom);
        var height = Math.Max(minimum, Math.Min(desired, bottom - workArea.Top));
        return new PixelRect(current.Left, bottom - height, current.Right, bottom);
    }

    public static PixelRect ResolveUpwardCollapsedBounds(PixelRect current, double dpiScaleY, double collapsedHeightDip = 48d)
    {
        if (!current.HasArea || !double.IsFinite(dpiScaleY) || dpiScaleY <= 0d)
        {
            return current;
        }

        var height = Math.Max(1, (int)Math.Round(collapsedHeightDip * dpiScaleY));
        return new PixelRect(current.Left, current.Bottom - height, current.Right, current.Bottom);
    }
}
