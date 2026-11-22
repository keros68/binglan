using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using BingLan.App.Interop;
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
    private Guid? _editingGroupId;
    private Guid? _deletingGroupId;
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
        if (!_queryWasEmpty && _filter == PanelFilter.Groups)
        {
            typed = typed.Where(row => row.Keys.Any(savedKeys.Contains)).ToList();
        }
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
        var activeDirName = !_queryWasEmpty && _directoryRows.Count > 0 && _filter is PanelFilter.All or PanelFilter.Files
            ? CurrentDirectoryDisplayName()
            : null;
        if (activeDirName is not null)
        {
            SearchScope.Visibility = Visibility.Visible;
            SearchCurrentCategory.Content = $"仅搜「{activeDirName}」";
            if (SearchCurrentCategory.IsChecked == true)
            {
                var dirQuery = SearchBox.Text.Trim();
                var byCandidate = _directoryRows.ToLookup(row => row.Entry.Candidate);
                _rows = TopBarSearchRules.Search(byCandidate.Select(group => group.Key), dirQuery, int.MaxValue)
                    .SelectMany(candidate => byCandidate[candidate])
                    .ToList();
                return activeDirName;
            }
            _rows = typed;
            return null;
        }

        var selectedName = activeGroup?.Name ?? (_purpose is { } category ? LauncherAppCategoryRules.DisplayName(category) : null);
        var showScope = !_queryWasEmpty && (activeGroup is not null
            || (_purpose is not null && _filter is PanelFilter.All or PanelFilter.Apps));
        SearchScope.Visibility = showScope ? Visibility.Visible : Visibility.Collapsed;
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
        var restrict = _queryWasEmpty || (showScope && SearchCurrentCategory.IsChecked == true);
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
    private void OnSaveGroupClick(object sender, RoutedEventArgs args) => SaveGroupEdit();
    private void OnCancelDeleteGroupClick(object sender, RoutedEventArgs args) => CancelGroupDelete();
    private void OnConfirmDeleteGroupClick(object sender, RoutedEventArgs args) => ConfirmGroupDelete();

    private void OnRenameGroupClick(object sender, RoutedEventArgs args)
    {
        if (_readGroups().FirstOrDefault(group => group.Id.ToString("N") == _category) is { } current)
        {
            BeginGroupEditing(null, current);
        }
    }

    private void OnDeleteGroupClick(object sender, RoutedEventArgs args)
    {
        if (_readGroups().FirstOrDefault(group => group.Id.ToString("N") == _category) is { } current)
        {
            BeginGroupDelete(current);
        }
    }

    private void OnGroupNavRightClick(object sender, MouseButtonEventArgs args)
    {
        if (_closed || _dismissing || _saveOrganizer is null) return;
        if ((sender as FrameworkElement)?.DataContext is not CategoryChip { Key: { Length: > 0 } key }) return;
        var group = _readGroups().FirstOrDefault(candidate => candidate.Id.ToString("N") == key);
        if (group is null) return;

        _menu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "重命名分组" };
        rename.Click += (_, _) => Dispatcher.BeginInvoke(() => BeginGroupEditing(null, group));
        var delete = new MenuItem { Header = "删除分组" };
        delete.Click += (_, _) => Dispatcher.BeginInvoke(() => BeginGroupDelete(group));
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        menu.PreviewKeyDown += (_, keyArgs) =>
        {
            if (keyArgs.Key != Key.Escape) return;
            menu.IsOpen = false;
            keyArgs.Handled = true;
        };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu)) _menu = null;
            Dispatcher.BeginInvoke(() =>
            {
                if (_closed || _dismissing) return;
                if (IsActive || DockNativeMethods.GetForegroundWindow() == _handle)
                {
                    if (GroupEditor.Visibility == Visibility.Visible) GroupNameEditor.Focus();
                    else if (GroupDeleteConfirm.Visibility == Visibility.Visible) ConfirmDeleteGroupButton.Focus();
                    else SearchBox.Focus();
                }
                else CloseOnDeactivation();
            }, DispatcherPriority.ContextIdle);
        };
        menu.PlacementTarget = sender as UIElement ?? CategoriesList;
        menu.Placement = PlacementMode.Bottom;
        _menu = menu;
        menu.IsOpen = true;
        args.Handled = true;
    }

    private void BeginGroupEditing(LauncherPanelEntry? entry, LauncherGroupState? existingGroup = null)
    {
        if (_closed || _dismissing || _saveOrganizer is null) return;
        CancelGroupDelete(focusSearch: false);
        _pendingGroupMember = existingGroup is null ? entry : null;
        _editingGroupId = existingGroup?.Id;
        SaveGroupButton.Content = existingGroup is null ? "创建" : "保存";
        if (existingGroup is null)
        {
            GroupNameEditor.Clear();
        }
        else
        {
            GroupNameEditor.Text = existingGroup.Name;
            GroupNameEditor.SelectAll();
        }
        GroupEditor.Visibility = Visibility.Visible;
        GroupNameEditor.Focus();
    }

    private void EndGroupEditing(bool focusSearch = true)
    {
        _pendingGroupMember = null;
        _editingGroupId = null;
        GroupEditor.Visibility = Visibility.Collapsed;
        if (focusSearch) SearchBox.Focus();
    }

    private void SaveGroupEdit()
    {
        if (_saveOrganizer is null) return;
        if (_editingGroupId is { } groupId)
        {
            if (!LauncherGroupRules.TryRename(_readGroups(), groupId, GroupNameEditor.Text, out var renamed) || renamed is null)
            {
                StatusText.Text = "请输入 1–32 个字符。分组名称不能重复或使用「全部」「常用」「未分组」";
                GroupNameEditor.Focus();
                return;
            }
            EndGroupEditing();
            _saveOrganizer();
            Render(_unfilteredRows, preserveSelection: true);
            StatusText.Text = $"已重命名为「{renamed.Name}」";
            return;
        }
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

    private void BeginGroupDelete(LauncherGroupState group)
    {
        if (_closed || _dismissing || _saveOrganizer is null) return;
        EndGroupEditing(focusSearch: false);
        _deletingGroupId = group.Id;
        GroupDeletePrompt.Text = $"删除分组「{group.Name}」？（仅移除分组，不影响原文件和应用）";
        GroupDeleteConfirm.Visibility = Visibility.Visible;
        ConfirmDeleteGroupButton.Focus();
    }

    private void CancelGroupDelete(bool focusSearch = true)
    {
        _deletingGroupId = null;
        GroupDeleteConfirm.Visibility = Visibility.Collapsed;
        if (focusSearch) SearchBox.Focus();
    }

    private void ConfirmGroupDelete()
    {
        if (_saveOrganizer is null || _deletingGroupId is not { } groupId)
        {
            CancelGroupDelete();
            return;
        }
        var groups = _readGroups();
        if (!LauncherGroupRules.TryDelete(groups, groupId, out var removed) || removed is null)
        {
            CancelGroupDelete();
            return;
        }
        var remainingKeys = groups.SelectMany(group => group.Members).Select(member => member.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var member in removed.Members)
        {
            if (!remainingKeys.Contains(member.Key))
            {
                _groupEntries.Remove(member.Key);
            }
        }
        _deletingGroupId = null;
        GroupDeleteConfirm.Visibility = Visibility.Collapsed;
        if (_category == removed.Id.ToString("N"))
        {
            _category = null;
            EnsureGroupSelected();
        }
        _saveOrganizer();
        Render(_unfilteredRows, preserveSelection: false);
        StatusText.Text = $"已删除分组「{removed.Name}」";
        SearchBox.Focus();
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
