using System.Windows;
using System.Windows.Controls;
using Panel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;

namespace BingLan.App.Windows;

/// <summary>
/// Lays tiles out in as many equal columns as fit, stretching them to fill the row, so a
/// box never shows a strip of empty space on the right after the last full column.
/// </summary>
public sealed class TileGridPanel : Panel
{
    /// <summary>The narrowest a tile, including its margin, may become.</summary>
    public double MinTileWidth { get; set; } = 66d;

    private int Columns(double width) =>
        double.IsInfinity(width) || width <= 0d
            ? Math.Max(1, InternalChildren.Count)
            : Math.Max(1, (int)Math.Floor(width / MinTileWidth));

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Columns(availableSize.Width);
        var cellWidth = double.IsInfinity(availableSize.Width)
            ? MinTileWidth
            : availableSize.Width / columns;
        var rowHeight = 0d;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(cellWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }

        var rows = (InternalChildren.Count + columns - 1) / columns;
        return new Size(cellWidth * Math.Min(columns, Math.Max(1, InternalChildren.Count)), rowHeight * rows);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var cellWidth = finalSize.Width / columns;
        var rowHeight = InternalChildren.Cast<UIElement>()
            .Select(child => child.DesiredSize.Height)
            .DefaultIfEmpty(0d)
            .Max();
        for (var index = 0; index < InternalChildren.Count; index++)
        {
            InternalChildren[index].Arrange(new Rect(
                index % columns * cellWidth,
                index / columns * rowHeight,
                cellWidth,
                rowHeight));
        }
        return finalSize;
    }
}
