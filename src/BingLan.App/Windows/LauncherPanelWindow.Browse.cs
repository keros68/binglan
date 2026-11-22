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
    private static readonly LauncherAppCategory[] Purposes = LauncherAppCategoryRules.OrderedCategories;
    private Dictionary<string, LauncherAppCategory> _effectiveCategories = new(StringComparer.Ordinal);
    private LauncherAppCategory? _purpose;
    private double _browseOffset;
    private bool _restoreBrowseOffset;
    private bool _iconLoadQueued;
    private int _iconViewVersion;

    private ScrollViewer? ResultViewport => ResultsList.Template?.FindName("ResultViewport", ResultsList) as ScrollViewer;
    private bool IsOverview => _queryWasEmpty && _activeTab == LauncherMainTab.Apps && _purpose is null;

    private LauncherAppCategory RawPurposeOf(PanelRow row, out bool isManual)
    {
        if (_readCategoryOverrides?.Invoke() is { } overrides)
        {
            foreach (var key in row.Keys)
            {
                if (overrides.TryGetValue(key, out var category))
                {
                    isManual = true;
                    return category;
                }
            }
        }
        isManual = false;
        return row.AutomaticCategory;
    }

    private LauncherAppCategory PurposeOf(PanelRow row) =>
        _effectiveCategories.TryGetValue(row.Entry.Key, out var effective)
            ? effective : RawPurposeOf(row, out _);

    private void ComputeEffectiveAppCategories(List<PanelRow> rows)
    {
        var families = LauncherIdentityRules.FindLaunchVariantFamilies(rows
            .Where(row => row.Entry.Candidate.Kind == TopBarSearchItemKind.App)
            .Select(row => (
                row.Entry.Key,
                LauncherIdentityRules.TargetKey(IdentityOf(row.Entry)),
                row.StartMenuFolder,
                LauncherIdentityRules.StripBenignArguments(row.Entry.Pinned?.Arguments).Length > 0)));
        var apps = rows.Where(row => row.Entry.Candidate.Kind == TopBarSearchItemKind.App)
            .Select(row =>
            {
                var raw = RawPurposeOf(row, out var isManual);
                if (!isManual && raw == LauncherAppCategory.Unclassified && families.Contains(row.Entry.Key))
                {
                    raw = LauncherAppCategory.Auxiliary;
                }
                return (row.Entry.Key, raw, isManual);
            });
        _effectiveCategories = new Dictionary<string, LauncherAppCategory>(
            LauncherAppCategoryRules.ResolveEffectiveCategories(apps, StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Entry.Candidate.Kind == TopBarSearchItemKind.App)
            {
                row.EffectiveCategory = PurposeOf(row);
            }
        }
    }

    private void OnTabAllClick(object sender, RoutedEventArgs args)
    {
        if (!_queryWasEmpty) SelectSearchFilter(PanelFilter.All);
    }

    private void OnTabAppsClick(object sender, RoutedEventArgs args) => SwitchMainTab(LauncherMainTab.Apps);
    private void OnTabFilesClick(object sender, RoutedEventArgs args) => SwitchMainTab(LauncherMainTab.Files);
    private void OnTabGroupsClick(object sender, RoutedEventArgs args) => SwitchMainTab(LauncherMainTab.Groups);

    private void SwitchMainTab(LauncherMainTab tab, bool persist = true)
    {
        if (!_queryWasEmpty)
        {
            SelectSearchFilter(tab switch
            {
                LauncherMainTab.Apps => PanelFilter.Apps,
                LauncherMainTab.Files => PanelFilter.Files,
                _ => PanelFilter.Groups
            });
            return;
        }
        EndGroupEditing(focusSearch: false);
        CancelGroupDelete(focusSearch: false);
        _activeTab = tab;
        _filter = _browseFilter = FilterForTab(tab);
        _browseSource = null;
        if (tab == LauncherMainTab.Apps)
        {
            _category = null;
            CancelDirectoryLoad();
        }
        else if (tab == LauncherMainTab.Files)
        {
            _purpose = null;
            _category = null;
        }
        else
        {
            _purpose = null;
            CancelDirectoryLoad();
            EnsureGroupSelected();
        }
        if (persist && _preferences.LastTab != tab)
        {
            _preferences.LastTab = tab;
            _saveOrganizer?.Invoke();
        }
        UpdateMainTabButtons();
        _restoreBrowseOffset = false;
        Render(_unfilteredRows, preserveSelection: false);
    }

    private void UpdateMainTabButtons()
    {
        if (TabAppsButton is null) return;
        TabAllButton.Visibility = _queryWasEmpty ? Visibility.Collapsed : Visibility.Visible;
        var allSelected = !_queryWasEmpty && _filter == PanelFilter.All;
        var appsSelected = _queryWasEmpty ? _activeTab == LauncherMainTab.Apps : _filter == PanelFilter.Apps;
        var filesSelected = _queryWasEmpty ? _activeTab == LauncherMainTab.Files : _filter == PanelFilter.Files;
        var groupsSelected = _queryWasEmpty ? _activeTab == LauncherMainTab.Groups : _filter == PanelFilter.Groups;

        TabAllButton.Tag = allSelected ? "Selected" : null;
        TabAppsButton.Tag = appsSelected ? "Selected" : null;
        TabFilesButton.Tag = filesSelected ? "Selected" : null;
        TabGroupsButton.Tag = groupsSelected ? "Selected" : null;

        System.Windows.Automation.AutomationProperties.SetItemStatus(TabAllButton, allSelected ? "已选择" : "");
        System.Windows.Automation.AutomationProperties.SetItemStatus(TabAppsButton, appsSelected ? "已选择" : "");
        System.Windows.Automation.AutomationProperties.SetItemStatus(TabFilesButton, filesSelected ? "已选择" : "");
        System.Windows.Automation.AutomationProperties.SetItemStatus(TabGroupsButton, groupsSelected ? "已选择" : "");

        AppsNavPanel.Visibility = _activeTab == LauncherMainTab.Apps ? Visibility.Visible : Visibility.Collapsed;
        FilesNavPanel.Visibility = _activeTab == LauncherMainTab.Files ? Visibility.Visible : Visibility.Collapsed;
        GroupsNavPanel.Visibility = _activeTab == LauncherMainTab.Groups ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOverviewClick(object sender, RoutedEventArgs args)
    {
        _activeTab = LauncherMainTab.Apps;
        SelectBrowse(PanelFilter.Apps);
    }

    private void OnSortByCategoryClick(object sender, RoutedEventArgs args)
    {
        if (!_preferences.AppsSortByName) return;
        _preferences.AppsSortByName = false;
        _saveOrganizer?.Invoke();
        Render(_unfilteredRows, preserveSelection: false);
    }

    private void OnSortByNameClick(object sender, RoutedEventArgs args)
    {
        if (_preferences.AppsSortByName) return;
        _preferences.AppsSortByName = true;
        _saveOrganizer?.Invoke();
        Render(_unfilteredRows, preserveSelection: false);
    }

    private void OnFileSortByNameClick(object sender, RoutedEventArgs args)
    {
        if (!_preferences.FilesSortByModified) return;
        _preferences.FilesSortByModified = false;
        _saveOrganizer?.Invoke();
        Render(_unfilteredRows, preserveSelection: false);
    }

    private void OnFileSortByModifiedClick(object sender, RoutedEventArgs args)
    {
        if (_preferences.FilesSortByModified) return;
        _preferences.FilesSortByModified = true;
        _saveOrganizer?.Invoke();
        Render(_unfilteredRows, preserveSelection: false);
    }

    private void OnViewGridClick(object sender, RoutedEventArgs args) => SetListViewMode(false);
    private void OnViewListClick(object sender, RoutedEventArgs args) => SetListViewMode(true);

    private void SetListViewMode(bool listMode)
    {
        var changed = false;
        if (_activeTab == LauncherMainTab.Files && _preferences.FilesUseListView != listMode)
        {
            _preferences.FilesUseListView = listMode;
            changed = true;
        }
        else if (_activeTab == LauncherMainTab.Groups && _preferences.GroupsUseListView != listMode)
        {
            _preferences.GroupsUseListView = listMode;
            changed = true;
        }
        if (changed) _saveOrganizer?.Invoke();
        Render(_unfilteredRows, preserveSelection: true);
    }

    private bool IsListViewActive()
    {
        if (!_queryWasEmpty) return false;
        if (_activeTab == LauncherMainTab.Files)
        {
            return _fileMode == FileBrowseMode.Recent || _preferences.FilesUseListView;
        }
        return _activeTab == LauncherMainTab.Groups && _preferences.GroupsUseListView;
    }

    private void OnCategoryClick(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not CategoryChip category) return;
        if (category.Purpose is { } purpose)
        {
            _activeTab = LauncherMainTab.Apps;
            SelectBrowse(PanelFilter.Apps, purpose: purpose);
            return;
        }
        if (category.FileKind is { } fileKind)
        {
            SelectFileType(fileKind);
            return;
        }
        if (category.PlaceId is { } placeId)
        {
            SelectPlace(placeId);
            return;
        }
        _activeTab = LauncherMainTab.Groups;
        SelectBrowse(PanelFilter.All, group: category.Key);
    }

    private void SelectBrowse(PanelFilter filter, LauncherAppCategory? purpose = null, string? group = null)
    {
        if (group != _category)
        {
            EndGroupEditing(focusSearch: false);
            CancelGroupDelete(focusSearch: false);
        }
        _filter = _browseFilter = filter;
        _category = group;
        _purpose = purpose;
        _restoreBrowseOffset = false;
        UpdateMainTabButtons();
        Render(_unfilteredRows, preserveSelection: false);
    }

    private void EnsureGroupSelected()
    {
        var groups = _readGroups();
        if (groups.Count == 0)
        {
            _category = null;
            return;
        }
        if (_category is null || !groups.Any(g => g.Id.ToString("N") == _category))
        {
            _category = groups[0].Id.ToString("N");
        }
    }

    private void UpdateBrowseNavigation(List<PanelRow> rows, IList<LauncherGroupState> groups)
    {
        var appRows = rows.Where(row => row.Entry.Candidate.Kind == TopBarSearchItemKind.App).ToList();
        var normalAppCount = appRows.Count(row => PurposeOf(row) != LauncherAppCategory.Auxiliary);
        var byPurpose = appRows.ToLookup(PurposeOf);

        OverviewButton.DataContext = new CategoryChip(null, "全部应用", IsOverview, null, "\uE80A",
            normalAppCount > 0 ? normalAppCount.ToString() : "");
        var purposeItems = Purposes
            .Where(purpose => byPurpose[purpose].Any() || purpose == _purpose)
            .Select(purpose => new CategoryChip(
                null,
                LauncherAppCategoryRules.DisplayName(purpose),
                _activeTab == LauncherMainTab.Apps && purpose == _purpose,
                purpose,
                LauncherAppCategoryRules.Glyph(purpose),
                byPurpose[purpose].Count().ToString()));
        RefreshNavigation(PurposeList, purposeItems);

        UpdateFilesNavigation(rows);

        RefreshNavigation(CategoriesList, groups.Select(group =>
        {
            var key = group.Id.ToString("N");
            return new CategoryChip(key, group.Name, _activeTab == LauncherMainTab.Groups && key == _category,
                Glyph: "\uE734", CountText: group.Members.Count.ToString());
        }));
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
        if (!_queryWasEmpty) return _rows;

        if (_activeTab == LauncherMainTab.Files)
        {
            return BuildFilesDisplayItems();
        }

        if (_activeTab == LauncherMainTab.Groups)
        {
            return BuildGroupDisplayItems();
        }

        if (!IsOverview) return _rows;

        var items = new List<object>();
        var width = Width - PanelContentGrid.Margin.Left - PanelContentGrid.Margin.Right - BrowseNavigation.Width
            - NavigationSurface.Margin.Right - SystemParameters.VerticalScrollBarWidth;
        var pinnedLimit = Math.Clamp((int)(width / LauncherGridPanel.TileWidth), 1, 8);
        var pinned = _preferences.FavoriteMode == LauncherFavoriteMode.Off
            ? new HashSet<PanelRow>()
            : _rows.Where(row => row.IsFavorite).Take(pinnedLimit).ToHashSet();
        if (pinned.Count > 0)
        {
            AddSection(items, "常用", _rows.Where(pinned.Contains));
        }

        if (_preferences.AppsSortByName)
        {
            AddSection(items, "全部应用（A–Z）", _rows.Where(row => !pinned.Contains(row))
                .OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase));
            return items;
        }

        var categorized = _rows.Where(row => !pinned.Contains(row)).ToLookup(PurposeOf);
        foreach (var purpose in Purposes)
        {
            if (purpose == LauncherAppCategory.Auxiliary) continue;
            AddSection(items, LauncherAppCategoryRules.DisplayName(purpose), categorized[purpose]);
        }
        return items;
    }

    private IReadOnlyList<object> BuildGroupDisplayItems()
    {
        if (_preferences.GroupsUseListView) return _rows;
        var apps = _rows.Where(row => row.Entry.Candidate.Kind == TopBarSearchItemKind.App).ToList();
        var files = _rows.Where(row => row.Entry.Candidate.Kind != TopBarSearchItemKind.App).ToList();
        if (apps.Count == 0 || files.Count == 0) return _rows;
        var items = new List<object>();
        AddSection(items, "相关应用", apps);
        AddSection(items, "相关文件与文件夹", files);
        return items;
    }

    private static void AddSection(List<object> items, string name, IEnumerable<PanelRow> rows)
    {
        using var iterator = rows.GetEnumerator();
        if (!iterator.MoveNext()) return;
        items.Add(new LauncherPanelSection(name));
        do { items.Add(iterator.Current); } while (iterator.MoveNext());
    }

    private void UpdateContextHeader(string? scope)
    {
        if (!_queryWasEmpty)
        {
            SectionTitle.Visibility = Visibility.Collapsed;
            BreadcrumbBar.Visibility = Visibility.Collapsed;
            FilePlacesBar.Visibility = Visibility.Collapsed;
            return;
        }

        BackFolderButton.Visibility = _activeTab == LauncherMainTab.Files && _folderStack.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        AppSortToggle.Visibility = IsOverview ? Visibility.Visible : Visibility.Collapsed;
        SortByCategoryButton.Tag = !_preferences.AppsSortByName ? "Selected" : null;
        SortByNameButton.Tag = _preferences.AppsSortByName ? "Selected" : null;

        var showFileSort = _activeTab == LauncherMainTab.Files
            && (_folderStack.Count > 0 || _fileMode is FileBrowseMode.FileType or FileBrowseMode.Place);
        FileSortToggle.Visibility = showFileSort ? Visibility.Visible : Visibility.Collapsed;
        FileSortByNameButton.Tag = !_preferences.FilesSortByModified ? "Selected" : null;
        FileSortByModifiedButton.Tag = _preferences.FilesSortByModified ? "Selected" : null;

        var showRecentPrivacy = _activeTab == LauncherMainTab.Files
            && _folderStack.Count == 0
            && _fileMode is FileBrowseMode.Overview or FileBrowseMode.Recent;
        RecentPrivacyButton.Visibility = showRecentPrivacy ? Visibility.Visible : Visibility.Collapsed;
        RecentPrivacyButton.Content = _preferences.ShowRecentFiles ? "隐藏最近" : "显示最近";

        var showLayout = _activeTab == LauncherMainTab.Groups
            || (_activeTab == LauncherMainTab.Files && _fileMode != FileBrowseMode.Recent);
        LayoutToggle.Visibility = showLayout ? Visibility.Visible : Visibility.Collapsed;
        var listActive = IsListViewActive();
        ViewGridButton.Tag = !listActive ? "Selected" : null;
        ViewListButton.Tag = listActive ? "Selected" : null;

        var showGroupActions = _activeTab == LauncherMainTab.Groups
            && _saveOrganizer is not null
            && _readGroups().Any(group => group.Id.ToString("N") == _category);
        RenameGroupButton.Visibility = showGroupActions ? Visibility.Visible : Visibility.Collapsed;
        DeleteGroupButton.Visibility = showGroupActions ? Visibility.Visible : Visibility.Collapsed;

        OpenDirectoryButton.Visibility = CurrentOpenableDirectoryPath() is not null
            ? Visibility.Visible : Visibility.Collapsed;

        var places = _snapshot?.Places ?? [];
        var showPlacesBar = _activeTab == LauncherMainTab.Files
            && _fileMode == FileBrowseMode.Overview
            && _folderStack.Count == 0
            && places.Count > 0;
        FilePlacesBar.Visibility = showPlacesBar ? Visibility.Visible : Visibility.Collapsed;
        if (showPlacesBar && !ReferenceEquals(FilePlacesBar.ItemsSource, places))
        {
            FilePlacesBar.ItemsSource = places;
        }

        var (title, subtitle) = ResolveHeaderTitle(scope);
        SectionTitle.Text = title;
        var breadcrumbs = BuildFileBreadcrumbs();
        if (breadcrumbs.Count > 0)
        {
            BreadcrumbBar.ItemsSource = breadcrumbs;
            BreadcrumbBar.Visibility = Visibility.Visible;
            SectionTitle.Visibility = Visibility.Collapsed;
        }
        else
        {
            BreadcrumbBar.Visibility = Visibility.Collapsed;
            SectionTitle.Visibility = string.IsNullOrEmpty(title) ? Visibility.Collapsed : Visibility.Visible;
        }
        SectionSubtitle.Text = subtitle;
    }

    private (string Title, string Subtitle) ResolveHeaderTitle(string? scope)
    {
        if (_activeTab == LauncherMainTab.Apps)
        {
            if (_purpose is { } p)
            {
                var hint = p == LauncherAppCategory.Unclassified
                    ? $"未识别及不足 {LauncherAppCategoryRules.MinimumCategoryItemCount} 项的小类自动归此"
                    : string.Empty;
                return (LauncherAppCategoryRules.DisplayName(p), hint);
            }
            return ("全部应用", _preferences.AppsSortByName ? "按名称排列" : "按用途分段");
        }
        if (_activeTab == LauncherMainTab.Groups)
        {
            if (scope is null) return ("我的分组", "点左侧新建分组，右键任意应用或文件加入");
            return (scope, "支持应用与文件混排");
        }
        return ResolveFilesHeader();
    }

    private string ResolveEmptyHint()
    {
        if (!_queryWasEmpty) return _filter == PanelFilter.Groups ? "我的分组中没有匹配项目" : "没有匹配项目";
        if (_activeTab == LauncherMainTab.Groups)
        {
            return _category is null ? "点击左侧「新建分组」创建你的第一个工作流分组" : "右键应用或文件可加入此分组";
        }
        if (_activeTab == LauncherMainTab.Files)
        {
            if (_fileMode == FileBrowseMode.Recent && !_preferences.ShowRecentFiles)
                return "已隐藏最近使用文件，点击右上角「显示最近」可重新开启";
            if (_directoryError is { Length: > 0 } error) return error;
            if (_loadingDirectory) return "正在读取文件夹…";
            return "此位置暂无可显示项目";
        }
        return "没有匹配项目";
    }

    private string ResolveStatusText()
    {
        if (!_queryWasEmpty) return _rows.Count == 0 ? "" : $"{_rows.Count} 个项目";
        if (_activeTab == LauncherMainTab.Files && _loadingDirectory) return "正在读取文件夹…";
        if (_activeTab == LauncherMainTab.Files && _directoryError is { Length: > 0 } err) return err;
        if (_activeTab == LauncherMainTab.Files && _directoryTruncated) return $"已显示前 {_rows.Count} 项，点击「打开目录」查看全部";
        if (_browseSource is not null) return _rows.Count == 0 ? "" : $"已筛选 · {_rows.Count} 项";
        return "";
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
        var overrides = _readCategoryOverrides();
        var reset = new MenuItem { Header = "恢复自动分类", IsEnabled = row.Keys.Any(overrides.ContainsKey) };
        reset.Click += (_, _) =>
        {
            var removed = false;
            foreach (var candidateKey in row.Keys)
            {
                removed |= overrides.Remove(candidateKey);
            }
            if (!removed) return;
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
            var buffer = IsListViewActive() ? LauncherGridPanel.ListRowHeight * 2 : LauncherGridPanel.TileHeight;
            var top = viewport.VerticalOffset;
            var bottom = top + viewport.ViewportHeight + buffer;
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
        ResultsList.Tag = true;
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
