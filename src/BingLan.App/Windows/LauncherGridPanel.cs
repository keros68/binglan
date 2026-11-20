using System.Windows;
using System.Windows.Controls;

namespace BingLan.App.Windows;

internal sealed record LauncherPanelSection(string Name)
{
    public bool IsSection => true;
}

public sealed class LauncherGridPanel : Panel
{
    internal const double TileWidth = 110;
    internal const double TileHeight = 95;
    private const double SectionHeight = 30;

    protected override Size MeasureOverride(Size availableSize) => Layout(availableSize.Width, measure: true);

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, measure: false);
        return finalSize;
    }

    private Size Layout(double width, bool measure)
    {
        if (double.IsInfinity(width)) width = TileWidth;
        var tileWidth = Math.Min(width, TileWidth);
        var columns = Math.Max(1, (int)(width / TileWidth));
        var column = 0;
        var top = 0d;
        foreach (UIElement child in InternalChildren)
        {
            if (child is ListBoxItem { Content: LauncherPanelSection })
            {
                if (column != 0) top += TileHeight;
                column = 0;
                Place(child, new Rect(0, top, width, SectionHeight));
                top += SectionHeight;
                continue;
            }

            Place(child, new Rect(column * tileWidth, top, tileWidth, TileHeight));
            if (++column == columns)
            {
                column = 0;
                top += TileHeight;
            }
        }
        return new Size(width, top + (column == 0 ? 0 : TileHeight));

        void Place(UIElement child, Rect bounds)
        {
            if (measure) child.Measure(bounds.Size);
            else child.Arrange(bounds);
        }
    }
}
