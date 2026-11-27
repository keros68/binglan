using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BingLan.Core.Models;

namespace BingLan.App.Windows;

public partial class SettingsWindow
{
    private Point _quickPlaceDragStart;
    private QuickPlaceState? _quickPlaceDragItem;

    private sealed record QuickPlaceDrag(SettingsWindow Owner, QuickPlaceState Item);

    private void QuickPlaceList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _quickPlaceDragStart = e.GetPosition(QuickPlaceList);
        var row = ItemsControl.ContainerFromElement(QuickPlaceList, e.OriginalSource as DependencyObject) as ListBoxItem;
        var index = row is null ? -1 : QuickPlaceList.ItemContainerGenerator.IndexFromContainer(row);
        _quickPlaceDragItem = index >= 0 && index < _quickPlaces.Count ? _quickPlaces[index] : null;
    }

    private void QuickPlaceList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _quickPlaceDragItem = null;
            return;
        }

        var point = e.GetPosition(QuickPlaceList);
        if (_quickPlaceDragItem is not { } item
            || (Math.Abs(point.X - _quickPlaceDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(point.Y - _quickPlaceDragStart.Y) < SystemParameters.MinimumVerticalDragDistance))
        {
            return;
        }

        _quickPlaceDragItem = null;
        DragDrop.DoDragDrop(QuickPlaceList, new DataObject(typeof(QuickPlaceDrag), new QuickPlaceDrag(this, item)), DragDropEffects.Move);
        e.Handled = true;
    }

    private QuickPlaceDrag? GetQuickPlaceDrag(DragEventArgs e) =>
        e.Data.GetData(typeof(QuickPlaceDrag)) is QuickPlaceDrag drag
        && ReferenceEquals(drag.Owner, this) && _quickPlaces.Contains(drag.Item)
        && (e.AllowedEffects & DragDropEffects.Move) != 0 ? drag : null;

    private void QuickPlaceList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetQuickPlaceDrag(e) is null ? DragDropEffects.None : DragDropEffects.Move;
        if (e.Effects == DragDropEffects.Move)
        {
            var row = ItemsControl.ContainerFromElement(QuickPlaceList, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (row is not null)
            {
                var index = QuickPlaceList.ItemContainerGenerator.IndexFromContainer(row);
                var y = e.GetPosition(QuickPlaceList).Y;
                if (y < 24 && index > 0)
                {
                    QuickPlaceList.ScrollIntoView(QuickPlaceList.Items[index - 1]);
                }
                else if (y > QuickPlaceList.ActualHeight - 24 && index + 1 < _quickPlaces.Count)
                {
                    QuickPlaceList.ScrollIntoView(QuickPlaceList.Items[index + 1]);
                }
            }
        }
        e.Handled = true;
    }

    private void QuickPlaceList_Drop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (GetQuickPlaceDrag(e) is { } drag)
        {
            var source = _quickPlaces.IndexOf(drag.Item);
            var row = ItemsControl.ContainerFromElement(QuickPlaceList, e.OriginalSource as DependencyObject) as ListBoxItem;
            var insertion = row is null ? _quickPlaces.Count : QuickPlaceList.ItemContainerGenerator.IndexFromContainer(row);
            if (row is not null && e.GetPosition(row).Y >= row.ActualHeight / 2)
            {
                insertion++;
            }
            ReorderQuickPlace(source, insertion > source ? insertion - 1 : insertion);
            e.Effects = DragDropEffects.Move;
        }
        e.Handled = true;
    }

    internal bool ReorderQuickPlace(int source, int target)
    {
        if (source < 0 || source >= _quickPlaces.Count || target < 0 || target >= _quickPlaces.Count || source == target)
        {
            return false;
        }

        var item = _quickPlaces[source];
        _quickPlaces.RemoveAt(source);
        _quickPlaces.Insert(target, item);
        QuickPlaceList.SelectedIndex = target;
        SaveQuickPlaces();
        QuickPlaceList.ScrollIntoView(QuickPlaceList.SelectedItem);
        return true;
    }
}
