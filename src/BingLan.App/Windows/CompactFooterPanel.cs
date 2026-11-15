using System.Windows;
using Panel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;

namespace BingLan.App.Windows;

/// <summary>
/// Measures body and footer vertically so the footer hugs the bottom of the list when
/// items fit comfortably, pins to the card bottom when overflowing, or lets the body fill
/// remaining height when <see cref="FillBody"/> is true.
/// </summary>
public sealed class CompactFooterPanel : Panel
{
    public static readonly DependencyProperty FillBodyProperty =
        DependencyProperty.Register(
            nameof(FillBody),
            typeof(bool),
            typeof(CompactFooterPanel),
            new FrameworkPropertyMetadata(
                false,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public bool FillBody
    {
        get => (bool)GetValue(FillBodyProperty);
        set => SetValue(FillBodyProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = availableSize.Width;
        var height = availableSize.Height;

        var body = InternalChildren.Count > 0 ? InternalChildren[0] : null;
        var footer = InternalChildren.Count > 1 ? InternalChildren[1] : null;

        var footerHeight = 0d;
        var footerWidth = 0d;
        if (footer is { Visibility: not Visibility.Collapsed })
        {
            footer.Measure(new Size(width, double.PositiveInfinity));
            footerHeight = footer.DesiredSize.Height;
            footerWidth = footer.DesiredSize.Width;
        }

        var bodyHeight = 0d;
        var bodyWidth = 0d;
        if (body is { Visibility: not Visibility.Collapsed })
        {
            var bodyAvailableHeight = double.IsPositiveInfinity(height)
                ? double.PositiveInfinity
                : Math.Max(0d, height - footerHeight);
            body.Measure(new Size(width, bodyAvailableHeight));
            bodyHeight = body.DesiredSize.Height;
            bodyWidth = body.DesiredSize.Width;
        }

        var desiredWidth = double.IsPositiveInfinity(width)
            ? Math.Max(bodyWidth, footerWidth)
            : width;
        var desiredHeight = FillBody && !double.IsPositiveInfinity(height)
            ? height
            : Math.Min(height, bodyHeight + footerHeight);
        return new Size(desiredWidth, desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var body = InternalChildren.Count > 0 ? InternalChildren[0] : null;
        var footer = InternalChildren.Count > 1 ? InternalChildren[1] : null;

        var footerHeight = footer is { Visibility: not Visibility.Collapsed }
            ? Math.Min(finalSize.Height, footer.DesiredSize.Height)
            : 0d;
        var maxBodyHeight = Math.Max(0d, finalSize.Height - footerHeight);

        var bodyHeight = body is { Visibility: not Visibility.Collapsed }
            ? FillBody
                ? maxBodyHeight
                : Math.Min(maxBodyHeight, body.DesiredSize.Height)
            : 0d;

        if (body is { Visibility: not Visibility.Collapsed })
        {
            body.Arrange(new Rect(0d, 0d, finalSize.Width, bodyHeight));
        }

        if (footer is { Visibility: not Visibility.Collapsed })
        {
            footer.Arrange(new Rect(0d, bodyHeight, finalSize.Width, footerHeight));
        }

        return finalSize;
    }
}

/// <summary>
/// Places visible children in one non-wrapping row and drops trailing cells that do not
/// fit whole within available width.
/// </summary>
public sealed class WholeCellRowPanel : Panel
{
    public static readonly DependencyProperty GapProperty =
        DependencyProperty.Register(
            nameof(Gap),
            typeof(double),
            typeof(WholeCellRowPanel),
            new FrameworkPropertyMetadata(
                12d,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var gap = Math.Max(0d, Gap);
        var x = 0d;
        var height = 0d;
        var fittedFirst = false;
        var overflowed = false;

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            var width = child.DesiredSize.Width;
            if (width <= 0d)
            {
                continue;
            }

            height = Math.Max(height, child.DesiredSize.Height);
            var spacing = fittedFirst ? gap : 0d;
            var candidate = x + spacing + width;
            if (!overflowed && (double.IsPositiveInfinity(availableSize.Width) || candidate <= availableSize.Width + 0.5d))
            {
                x = candidate;
                fittedFirst = true;
            }
            else
            {
                overflowed = true;
            }
        }

        return new Size(
            double.IsPositiveInfinity(availableSize.Width) ? x : Math.Min(availableSize.Width, x),
            double.IsPositiveInfinity(availableSize.Height) ? height : Math.Min(availableSize.Height, height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var gap = Math.Max(0d, Gap);
        var x = 0d;
        var fittedFirst = false;
        var overflowed = false;

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            var width = child.DesiredSize.Width;
            if (width <= 0d)
            {
                child.Arrange(new Rect(0d, 0d, 0d, 0d));
                continue;
            }

            var spacing = fittedFirst ? gap : 0d;
            if (overflowed || x + spacing + width > finalSize.Width + 0.5d)
            {
                overflowed = true;
                child.Arrange(new Rect(0d, 0d, 0d, 0d));
                continue;
            }

            child.Arrange(new Rect(x + spacing, 0d, width, finalSize.Height));
            x += spacing + width;
            fittedFirst = true;
        }

        return finalSize;
    }
}
