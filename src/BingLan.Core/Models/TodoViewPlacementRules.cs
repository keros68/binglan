namespace BingLan.Core.Models;

public readonly record struct CardRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// <summary>
/// Keeps a todo card anchored to its screen side when toggling between the compact list
/// view and the wider weekly calendar view, so a card parked on the right or bottom edge
/// expands inward and returns to its original position instead of drifting left/up.
/// </summary>
public static class TodoViewPlacementRules
{
    public static (double Left, double Top) ComputeToggledPosition(
        CardRect current,
        double nextWidth,
        double nextHeight,
        CardRect workArea,
        (double Left, double Top)? savedAnchor = null)
    {
        double left;
        double top;
        if (savedAnchor is { } anchor)
        {
            left = anchor.Left;
            top = anchor.Top;
        }
        else
        {
            var anchorRight = (current.Left + current.Right) * 0.5d > (workArea.Left + workArea.Right) * 0.5d;
            var anchorBottom = (current.Top + current.Bottom) * 0.5d > (workArea.Top + workArea.Bottom) * 0.5d;
            left = anchorRight ? current.Right - nextWidth : current.Left;
            top = anchorBottom ? current.Bottom - nextHeight : current.Top;
        }

        var maxLeft = Math.Max(workArea.Left, workArea.Right - nextWidth);
        var maxTop = Math.Max(workArea.Top, workArea.Bottom - nextHeight);
        return (
            Math.Clamp(left, workArea.Left, maxLeft),
            Math.Clamp(top, workArea.Top, maxTop));
    }
}
