using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BingLan.Core.Models;
using BingLan.Core.TopBar;

namespace BingLan.App.Windows;

public partial class LauncherPanelWindow
{
    private static readonly LauncherAppCategory[] Purposes = Enum.GetValues<LauncherAppCategory>()
        .Where(category => category is not LauncherAppCategory.Unclassified and not LauncherAppCategory.Auxiliary)
        .Concat([LauncherAppCategory.Unclassified, LauncherAppCategory.Auxiliary]).ToArray();
    private LauncherAppCategory? _purpose;
    private double _browseOffset;
    private bool _restoreBrowseOffset;
    private bool _iconLoadQueued;
    private int _iconViewVersion;

    private ScrollViewer? ResultViewport => ResultsList.Template?.FindName("ResultViewport", ResultsList) as ScrollViewer;
    private bool IsOverview => _queryWasEmpty && _browseFilter == PanelFilter.Apps && _purpose is null;

    private LauncherAppCategory PurposeOf(PanelRow row) =>
        _readCategoryOverrides?.Invoke().TryGetValue(row.Entry.Key, out var category) == true
            ? category : row.AutomaticCategory;

    private void OnOverviewClick(object sender, RoutedEventArgs args) => SelectBrowse(PanelFilter.Apps);

    private void OnCategoryClick(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not CategoryChip category) return;
        if (category.Purpose is { } purpose) SelectBrowse(PanelFilter.Apps, purpose: purpose);
        else SelectBrowse(PanelFilter.All, group: category.Key);
    }

    private void SelectBrowse(PanelFilter filter, LauncherAppCategory? purpose = null, string? group = null)
    {
        _filter = _browseFilter = filter;
        _category = group;
        _purpose = purpose;
        _restoreBrowseOffset = false;
        Render(_unfilteredRows, preserveSelection: false);
    }

    private void UpdateBrowseNavigation(List<PanelRow> rows, IList<LauncherGroupState> groups)
    {
        var purposesPresent = rows.Where(row => row.Entry.Candidate.Kind == TopBarSearchItemKind.App)
            .Select(PurposeOf).ToHashSet();
        var purposeItems = Purposes.Where(purpose => purposesPresent.Contains(purpose) || purpose == _purpose)
            .Select(purpose => new CategoryChip(null, LauncherAppCategoryRules.DisplayName(purpose), purpose == _purpose, purpose));
        RefreshNavigation(PurposeList, purposeItems);
        RefreshNavigation(CategoriesList, groups.Select(group =>
        {
            var key = group.Id.ToString("N");
            return new CategoryChip(key, group.Name, key == _category);
        }));
        OverviewButton.DataContext = new CategoryChip(null, "应用概览", IsOverview);
        BrowseFilesButton.DataContext = new CategoryChip(null, "文件与文件夹", _browseFilter == PanelFilter.Files);
    }

    private static void RefreshNavigation(ItemsControl control, IEnumerable<CategoryChip> choices)
    {
        var items = choices.ToArray();
        if (control.ItemsSource is not IEnumerable<CategoryChip> previous || !previous.SequenceEqual(items))
        {
            control.ItemsSource = items;
        }
    }

    private IReadOnlyList<object> BuildDisplayItems()
    {
        if (!IsOverview) return _rows;

        var items = new List<object>();
        var width = Width - PanelSurface.Padding.Left - PanelSurface.Padding.Right - BrowseNavigation.Width
            - NavigationSurface.Margin.Right - SystemParameters.VerticalScrollBarWidth;
        var pinnedLimit = Math.Clamp((int)(width / LauncherGridPanel.TileWidth), 1, 8);
        var pinned = _rows.Where(row => row.IsFavorite).Take(pinnedLimit).ToHashSet();
        AddSection("常用", _rows.Where(pinned.Contains));

        var categorized = _rows.Where(row => !pinned.Contains(row)).ToLookup(PurposeOf);
        foreach (var purpose in Purposes)
        {
            if (purpose == LauncherAppCategory.Auxiliary) continue;
            AddSection(LauncherAppCategoryRules.DisplayName(purpose), categorized[purpose]);
        }
        return items;

        void AddSection(string name, IEnumerable<PanelRow> rows)
        {
            using var iterator = rows.GetEnumerator();
            if (!iterator.MoveNext()) return;
            items.Add(new LauncherPanelSection(name));
            do { items.Add(iterator.Current); } while (iterator.MoveNext());
        }
    }

    private void AddPurposeMenu(ContextMenu menu, PanelRow row)
    {
        if (row.Entry.Candidate.Kind != TopBarSearchItemKind.App || _readCategoryOverrides is null || _saveOrganizer is null) return;
        var key = row.Entry.Key;
        var categoryMenu = new MenuItem { Header = "更改类别" };
        var current = PurposeOf(row);
        foreach (var purpose in Purposes)
        {
            var name = LauncherAppCategoryRules.DisplayName(purpose);
            var choice = new MenuItem { Header = name, IsChecked = purpose == current };
            choice.Click += (_, _) =>
            {
                _readCategoryOverrides()[key] = purpose;
                _saveOrganizer();
                Render(_unfilteredRows, preserveSelection: true);
                StatusText.Text = $"已归入「{name}」";
            };
            categoryMenu.Items.Add(choice);
        }
        categoryMenu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "恢复自动分类", IsEnabled = _readCategoryOverrides().ContainsKey(key) };
        reset.Click += (_, _) =>
        {
            if (!_readCategoryOverrides().Remove(key)) return;
            _saveOrganizer();
            Render(_unfilteredRows, preserveSelection: true);
            StatusText.Text = "已恢复自动分类";
        };
        categoryMenu.Items.Add(reset);
        menu.Items.Add(categoryMenu);
    }

    private void OnResultScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        if (ReferenceEquals(args.OriginalSource, ResultViewport)) QueueVisibleIcons();
    }

    private IEnumerable<(PanelRow Row, Rect Bounds)> TilePositions()
    {
        if (FindGrid(ResultsList) is not { } grid) yield break;
        foreach (var (item, bounds) in grid.ItemPositions())
        {
            if (item is PanelRow row) yield return (row, bounds);
        }
    }

    private static LauncherGridPanel? FindGrid(DependencyObject element)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            var child = VisualTreeHelper.GetChild(element, index);
            if (child is LauncherGridPanel grid) return grid;
            if (FindGrid(child) is { } descendant) return descendant;
        }
        return null;
    }

    private void QueueVisibleIcons()
    {
        if (_closed || _dismissing || _iconLoadQueued) return;
        _iconLoadQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _iconLoadQueued = false;
            if (_closed || _dismissing || ResultViewport is not { } viewport) return;
            var top = viewport.VerticalOffset;
            var bottom = top + viewport.ViewportHeight + LauncherGridPanel.TileHeight;
            var rows = TilePositions().Where(tile => tile.Bounds.Bottom > top && tile.Bounds.Top < bottom)
                .Select(tile => tile.Row).ToList();
            _ = LoadIconsAsync(rows, ++_iconViewVersion);
        }, DispatcherPriority.Background);
    }

    private void SelectNeighbor(Key direction)
    {
        ResultsList.Tag = true;
        var current = ResultsList.SelectedItem as PanelRow;
        if (current is null)
        {
            SelectAndReveal(_rows[0]);
            return;
        }
        if (direction is Key.Left or Key.Right)
        {
            var index = _rows.IndexOf(current) + (direction == Key.Right ? 1 : -1);
            SelectAndReveal(_rows[Math.Clamp(index, 0, _rows.Count - 1)]);
            return;
        }
        var positions = TilePositions().ToArray();
        var origin = positions.FirstOrDefault(tile => tile.Row == current).Bounds;
        var step = direction == Key.Down ? 1 : -1;
        var next = positions.Where(tile => (tile.Bounds.Y - origin.Y) * step > 1)
            .OrderBy(tile => Math.Abs(tile.Bounds.Y - origin.Y))
            .ThenBy(tile => Math.Abs(tile.Bounds.X - origin.X))
            .FirstOrDefault().Row;
        if (next is not null) SelectAndReveal(next);
    }

    private void SelectAndReveal(PanelRow row)
    {
        ResultsList.SelectedItem = row;
        ResultsList.ScrollIntoView(row);
    }

    private void ScrollResultPage(bool down)
    {
        if (ResultViewport is not { } viewport) return;
        ResultsList.Tag = true;
        var offset = Math.Clamp(viewport.VerticalOffset + (down ? 1 : -1) * viewport.ViewportHeight, 0, viewport.ScrollableHeight);
        viewport.ScrollToVerticalOffset(offset);
        var target = TilePositions().FirstOrDefault(tile => tile.Bounds.Top >= offset).Row
            ?? TilePositions().FirstOrDefault(tile => tile.Bounds.Bottom > offset).Row;
        if (target is not null) SelectAndReveal(target);
    }
}
