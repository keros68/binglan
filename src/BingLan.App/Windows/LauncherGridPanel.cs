using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace BingLan.App.Windows;

internal sealed record LauncherPanelSection(string Name)
{
    public bool IsSection => true;
}

public sealed class LauncherGridPanel : VirtualizingPanel, IScrollInfo
{
    internal const double TileWidth = 110;
    internal const double TileHeight = 95;
    private const double SectionHeight = 30;
    private const double ScrollLineHeight = 16;
    private readonly List<Rect> _bounds = [];
    private double _layoutWidth = -1;
    private Size _extent;
    private Size _viewport;
    private double _offset;

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset;
    public ScrollViewer? ScrollOwner { get; set; }

    // Keep geometry for every data item, but generate WPF controls only near the
    // viewport. Rebuild this O(n) index on collection/width changes, not on scroll.
    internal IEnumerable<(object Item, Rect Bounds)> ItemPositions()
    {
        if (ItemsControl.GetItemsOwner(this) is not { } owner) yield break;
        for (var index = 0; index < Math.Min(owner.Items.Count, _bounds.Count); index++)
            yield return (owner.Items[index], _bounds[index]);
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        _layoutWidth = -1;
        if (args.Action == NotifyCollectionChangedAction.Reset)
        {
            if (InternalChildren.Count > 0) RemoveInternalChildRange(0, InternalChildren.Count);
            _bounds.Clear();
            _offset = 0;
            ScrollOwner?.InvalidateScrollInfo();
        }
        else if (args.ItemUICount > 0 && args.Action is (NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace))
            RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
        else if (args.ItemUICount > 0 && args.Action == NotifyCollectionChangedAction.Move)
            RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
        base.OnItemsChanged(sender, args);
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (ItemsControl.GetItemsOwner(this) is not { } owner) return default;
        var width = double.IsInfinity(availableSize.Width) ? TileWidth : Math.Max(0, availableSize.Width);
        if (_layoutWidth != width || _bounds.Count != owner.Items.Count) BuildLayout(owner, width);
        _viewport = new Size(width, double.IsInfinity(availableSize.Height) ? _extent.Height : Math.Max(0, availableSize.Height));
        _offset = Math.Clamp(_offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        ScrollOwner?.InvalidateScrollInfo();

        var top = _offset - TileHeight;
        var bottom = _offset + ViewportHeight + TileHeight;
        var needed = new HashSet<int>();
        for (var index = 0; index < _bounds.Count; index++)
            if (_bounds[index].Bottom > top && _bounds[index].Top < bottom) needed.Add(index);

        // Retain only the selected/focused control outside the viewport so a
        // keyboard focus or an open item menu never loses its placement target.
        for (var childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            var position = new GeneratorPosition(childIndex, 0);
            var index = ItemContainerGenerator.IndexFromGeneratorPosition(position);
            var child = InternalChildren[childIndex];
            if (index >= 0 && (needed.Contains(index) || child.IsKeyboardFocusWithin || child is ListBoxItem { IsSelected: true })) continue;
            if (index >= 0)
            {
                if (ItemContainerGenerator is IRecyclingItemContainerGenerator recycling) recycling.Recycle(position, 1);
                else ItemContainerGenerator.Remove(position, 1);
            }
            RemoveInternalChildRange(childIndex, 1);
        }

        foreach (var index in needed.Order())
        {
            var position = ItemContainerGenerator.GeneratorPositionFromIndex(index);
            var childIndex = position.Offset == 0 ? position.Index : position.Index + 1;
            using (ItemContainerGenerator.StartAt(position, GeneratorDirection.Forward, true))
            {
                var child = (UIElement)ItemContainerGenerator.GenerateNext(out var newlyRealized);
                // WPF reports recycled containers as already realized even
                // though they have been detached from this visual collection.
                if (newlyRealized || !InternalChildren.Contains(child))
                {
                    InsertInternalChild(childIndex, child);
                    ItemContainerGenerator.PrepareItemContainer(child);
                }
            }
        }
        for (var childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
            if (index >= 0 && index < _bounds.Count) InternalChildren[childIndex].Measure(_bounds[index].Size);
        }
        return _viewport;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
            if (index < 0 || index >= _bounds.Count) continue;
            var bounds = _bounds[index];
            bounds.Offset(0, -_offset);
            InternalChildren[childIndex].Arrange(bounds);
        }
        return finalSize;
    }

    private void BuildLayout(ItemsControl owner, double width)
    {
        _layoutWidth = width;
        _bounds.Clear();
        var tileWidth = Math.Min(width, TileWidth);
        var columns = Math.Max(1, (int)(width / TileWidth));
        var column = 0;
        var top = 0d;
        foreach (var item in owner.Items)
        {
            if (item is LauncherPanelSection)
            {
                if (column != 0) top += TileHeight;
                column = 0;
                _bounds.Add(new Rect(0, top, width, SectionHeight));
                top += SectionHeight;
                continue;
            }

            _bounds.Add(new Rect(column * tileWidth, top, tileWidth, TileHeight));
            if (++column == columns)
            {
                column = 0;
                top += TileHeight;
            }
        }
        _extent = new Size(width, top + (column == 0 ? 0 : TileHeight));
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0 || index >= _bounds.Count) return;
        Reveal(_bounds[index]);
        // ListBox.ScrollIntoView is also used immediately before opening a menu.
        // Complete realization before the caller asks for its placement target.
        UpdateLayout();
    }

    private void Reveal(Rect bounds)
    {
        if (bounds.Top < _offset) SetVerticalOffset(bounds.Top);
        else if (bounds.Bottom > _offset + ViewportHeight) SetVerticalOffset(bounds.Bottom - ViewportHeight);
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        DependencyObject? child = visual;
        while (child is not null && VisualTreeHelper.GetParent(child) != this) child = VisualTreeHelper.GetParent(child);
        if (child is not UIElement element) return Rect.Empty;
        var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(InternalChildren.IndexOf(element), 0));
        if (index < 0 || index >= _bounds.Count) return Rect.Empty;
        Reveal(_bounds[index]);
        var visible = _bounds[index];
        visible.Offset(0, -_offset);
        visible.Intersect(new Rect(new Point(), _viewport));
        return visible;
    }

    public void SetVerticalOffset(double offset)
    {
        if (double.IsNaN(offset)) return;
        offset = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        if (Math.Abs(offset - _offset) < 0.01) return;
        _offset = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void SetHorizontalOffset(double offset) { }
    public void LineUp() => SetVerticalOffset(_offset - ScrollLineHeight);
    public void LineDown() => SetVerticalOffset(_offset + ScrollLineHeight);
    public void PageUp() => SetVerticalOffset(_offset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(_offset + ViewportHeight);
    private double WheelStep => SystemParameters.WheelScrollLines < 0 ? ViewportHeight : ScrollLineHeight * SystemParameters.WheelScrollLines;
    public void MouseWheelUp() => SetVerticalOffset(_offset - WheelStep);
    public void MouseWheelDown() => SetVerticalOffset(_offset + WheelStep);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
}
