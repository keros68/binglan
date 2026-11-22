using System.Windows;
using System.Windows.Controls;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.TopBar;

namespace BingLan.App.Windows;

public partial class LauncherPanelWindow
{
    private string? _browseSource;
    private string? _searchSource;
    private bool _changingSourceFilter;
    private LauncherPanelEntry? _pendingGroupMember;
    private readonly Dictionary<string, LauncherPanelEntry> _groupEntries = new(StringComparer.Ordinal);

    private void ResetSearchScope()
    {
        if (_queryWasEmpty) _restoreBrowseOffset = true;
        else _browseOffset = ResultViewport?.VerticalOffset ?? 0;
        _searchSource = null;
        SearchCurrentCategory.IsChecked = false;
    }

    private string? ApplyOrganizerFilters(List<PanelRow> rows)
    {
        var groups = _readGroups();
        var savedKeys = groups.SelectMany(group => group.Members).Select(member => member.Key)
            .ToHashSet(StringComparer.Ordinal);
        var available = rows.Where(row => !row.Entry.IsSavedFallback
            || row.Keys.Any(savedKeys.Contains)).ToList();

        if (_queryWasEmpty && _activeTab == LauncherMainTab.Groups)
        {
            EnsureGroupSelected();
        }
        if (_queryWasEmpty) UpdateBrowseNavigation(available, groups);

        if (_queryWasEmpty && _activeTab == LauncherMainTab.Files)
        {
            SearchScope.Visibility = Visibility.Collapsed;
            var fileRows = ApplyFilesFilter(available);
            var canFilterSource = _folderStack.Count == 0 && _fileMode is FileBrowseMode.Overview or FileBrowseMode.FileType;
            SourceFilter.Visibility = canFilterSource ? Visibility.Visible : Visibility.Collapsed;
            if (canFilterSource)
            {
                var source = UpdateSourceChoices(fileRows);
                SourceFilter.Tag = source.Key is null ? "筛选" : $"来源 · {source.Name}";
                SourceFilter.ToolTip = source.Key is null ? "按来源筛选" : source.Name;
                if (source.Key is not null)
                {
                    fileRows = fileRows.Where(row => row.Sources.Contains(source.Key, StringComparer.OrdinalIgnoreCase)).ToList();
                }
            }
            _rows = fileRows;
            return null;
        }

        var showSourceFilter = !_queryWasEmpty || _activeTab == LauncherMainTab.Apps;
        SourceFilter.Visibility = showSourceFilter ? Visibility.Visible : Visibility.Collapsed;

        var typed = available.Where(row => MatchesFilter(row.Entry, _filter)).ToList();
        if (showSourceFilter)
        {
            var source = UpdateSourceChoices(typed);
            SourceFilter.Tag = source.Key is null ? "筛选" : $"来源 · {source.Name}";
            SourceFilter.ToolTip = source.Key is null ? "按来源筛选" : source.Name;
            if (source.Key is not null)
            {
                typed = typed.Where(row => row.Sources.Contains(source.Key, StringComparer.OrdinalIgnoreCase)).ToList();
            }
        }

        var activeGroup = groups.FirstOrDefault(group => group.Id.ToString("N") == _category);
        var selectedName = activeGroup?.Name ?? (_purpose is { } category ? LauncherAppCategoryRules.DisplayName(category) : null);
        SearchScope.Visibility = !_queryWasEmpty && selectedName is not null ? Visibility.Visible : Visibility.Collapsed;
        SearchCurrentCategory.Content = selectedName is null ? "仅搜当前分组" : $"仅搜「{selectedName}」";
        if (selectedName is null)
        {
            _rows = _queryWasEmpty && _activeTab == LauncherMainTab.Groups
                ? []
                : _queryWasEmpty && _activeTab == LauncherMainTab.Apps
                    ? typed.Where(row => PurposeOf(row) != LauncherAppCategory.Auxiliary).ToList()
                    : typed;
            return null;
        }

        var memberKeys = activeGroup?.Members.Select(member => member.Key).ToHashSet(StringComparer.Ordinal);
        bool InCategory(PanelRow row) => memberKeys is not null
            ? row.Keys.Any(memberKeys.Contains)
            : (row.Entry.Candidate.Kind == TopBarSearchItemKind.App && PurposeOf(row) == _purpose);
        var restrict = _queryWasEmpty || SearchCurrentCategory.IsChecked == true;
        _rows = restrict ? typed.Where(InCategory).ToList() : typed.OrderByDescending(InCategory).ToList();
        return restrict ? selectedName : null;
    }

    private SourceOption UpdateSourceChoices(List<PanelRow> rows)
    {
        var options = new List<SourceOption> { new(null, "全部来源") };
        options.AddRange(rows.SelectMany(row => row.Sources).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(source => source, StringComparer.CurrentCultureIgnoreCase).Select(source => new SourceOption(source, source)));
        var requested = _queryWasEmpty ? _browseSource : _searchSource;
        var selected = options.FirstOrDefault(option => string.Equals(option.Key, requested, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            selected = new SourceOption(requested, requested ?? "全部来源");
            options.Add(selected);
        }
        _changingSourceFilter = true;
        try
        {
            if (SourceFilter.ItemsSource is not IEnumerable<SourceOption> previous || !previous.SequenceEqual(options))
            {
                SourceFilter.ItemsSource = options;
            }
            SourceFilter.SelectedIndex = options.IndexOf(selected);
        }
        finally
        {
            _changingSourceFilter = false;
        }
        return selected;
    }

    private void OnSourceFilterChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_closed || _dismissing || _changingSourceFilter || SourceFilter.SelectedItem is not SourceOption option) return;
        if (_queryWasEmpty) _browseSource = option.Key;
        else _searchSource = option.Key;
        Render(_unfilteredRows, preserveSelection: false);
    }

    private void OnSearchScopeClick(object sender, RoutedEventArgs args) =>
        Render(_unfilteredRows, preserveSelection: false);

    private void OnNewGroupClick(object sender, RoutedEventArgs args) => BeginGroupEditing(null);
    private void OnCancelGroupClick(object sender, RoutedEventArgs args) => EndGroupEditing();
    private void OnSaveGroupClick(object sender, RoutedEventArgs args) => SaveNewGroup();

    private void BeginGroupEditing(LauncherPanelEntry? entry)
    {
        if (_closed || _dismissing) return;
        _pendingGroupMember = entry;
        GroupNameEditor.Clear();
        GroupEditor.Visibility = Visibility.Visible;
        GroupNameEditor.Focus();
    }

    private void EndGroupEditing()
    {
        _pendingGroupMember = null;
        GroupEditor.Visibility = Visibility.Collapsed;
        SearchBox.Focus();
    }

    private void SaveNewGroup()
    {
        if (_saveOrganizer is null) return;
        if (!LauncherGroupRules.TryCreate(_readGroups(), GroupNameEditor.Text, out var group) || group is null)
        {
            StatusText.Text = "请输入 1–32 个字符。分组名称不能重复或使用「全部」「常用」「未分组」";
            GroupNameEditor.Focus();
            return;
        }
        if (_pendingGroupMember is { } entry && LauncherGroupRules.TryAdd(group, DescribeMember(entry)))
            RememberGroupEntry(entry);
        EndGroupEditing();
        _saveOrganizer();
        if (_queryWasEmpty)
        {
            _activeTab = LauncherMainTab.Groups;
            _preferences.LastTab = LauncherMainTab.Groups;
            SelectBrowse(PanelFilter.All, group: group.Id.ToString("N"));
        }
        else Render(_unfilteredRows, preserveSelection: true);
        StatusText.Text = $"已创建「{group.Name}」";
    }

    private void AddOrganizerMenu(ContextMenu menu, PanelRow row)
    {
        if (_saveOrganizer is null) return;
        var groups = _readGroups();
        var member = DescribeMember(row.Entry);
        var join = new MenuItem { Header = "加入面板分组" };
        foreach (var group in groups)
        {
            var belongs = group.Members.Any(existing => row.Keys.Contains(existing.Key));
            var choice = new MenuItem { Header = group.Name, IsEnabled = !belongs, IsChecked = belongs, IsCheckable = true, StaysOpenOnClick = false };
            choice.Click += (_, _) =>
            {
                if (!LauncherGroupRules.TryAdd(group, member)) return;
                RememberGroupEntry(row.Entry);
                _saveOrganizer();
                Render(_unfilteredRows, preserveSelection: true);
                StatusText.Text = $"已加入「{group.Name}」";
            };
            join.Items.Add(choice);
        }
        var create = new MenuItem { Header = "新建分组…" };
        create.Click += (_, _) => Dispatcher.BeginInvoke(() => BeginGroupEditing(row.Entry));
        join.Items.Add(create);
        menu.Items.Add(join);

        var current = groups.FirstOrDefault(group => group.Id.ToString("N") == _category);
        var matchedMember = current?.Members.FirstOrDefault(existing => row.Keys.Contains(existing.Key));
        if (current is null || matchedMember is null) return;
        var remove = new MenuItem { Header = "移出当前面板分组" };
        remove.Click += (_, _) =>
        {
            if (!LauncherGroupRules.Remove(current, matchedMember.Key)) return;
            if (!_readGroups().Any(group => group.Members.Any(existing => existing.Key == matchedMember.Key)))
                _groupEntries.Remove(matchedMember.Key);
            _saveOrganizer();
            Render(_unfilteredRows, preserveSelection: true);
            StatusText.Text = $"已移出「{current.Name}」";
        };
        menu.Items.Add(remove);
    }

    private static LauncherGroupMember DescribeMember(LauncherPanelEntry entry) =>
        new(entry.Key, entry.Pinned is null
            ? LocalPath(entry) ?? (FileMappingService.IsShellEntry(entry.Candidate.Path) ? entry.Candidate.Path : null)
            : null, entry.Candidate.DisplayName, entry.Pinned is { } pin ? BingLan.Core.Dock.DockPinRules.Copy(pin) : null);

    private void RememberGroupEntry(LauncherPanelEntry entry) =>
        _groupEntries[entry.Key] = entry with { Source = "已保存入口", IsSavedFallback = true };

    private LauncherPanelSnapshot IncludeGroupEntries(LauncherPanelSnapshot snapshot) =>
        _groupEntries.Count == 0 ? snapshot : snapshot with
        {
            Entries = snapshot.Entries.Concat(_groupEntries.Values).ToArray()
        };

    private sealed record SourceOption(string? Key, string Name)
    {
        public override string ToString() => Name;
    }
}
