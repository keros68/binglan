using System.IO;
using System.Windows;
using BingLan.App.Services;
using BingLan.Core.Models;
using BingLan.Core.TopBar;

namespace BingLan.App.Windows;

public partial class LauncherPanelWindow
{
    private enum FileBrowseMode { Overview, Recent, FileType, Place }

    private FileBrowseMode _fileMode = FileBrowseMode.Overview;
    private LauncherFileCategory? _selectedFileType;
    private string? _selectedPlaceId;
    private readonly Stack<(string Path, string Title)> _folderStack = new();
    private IReadOnlyList<PanelRow> _directoryRows = [];
    private CancellationTokenSource? _directoryCts;
    private bool _loadingDirectory;
    private bool _directoryTruncated;
    private string? _directoryError;

    private void OnFilesOverviewClick(object sender, RoutedEventArgs args)
    {
        CancelDirectoryLoad();
        _folderStack.Clear();
        _fileMode = FileBrowseMode.Overview;
        _selectedFileType = null;
        _selectedPlaceId = null;
        Render(_unfilteredRows, preserveSelection: false);
    }

    private void OnFilesRecentClick(object sender, RoutedEventArgs args)
    {
        CancelDirectoryLoad();
        _folderStack.Clear();
        _fileMode = FileBrowseMode.Recent;
        _selectedFileType = null;
        _selectedPlaceId = null;
        Render(_unfilteredRows, preserveSelection: false);
    }

    private void OnPlaceCardClick(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is LauncherPlace place)
        {
            SelectPlace(place.Id);
        }
    }

    private void OnPlaceCardRightClick(object sender, System.Windows.Input.MouseButtonEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not LauncherPlace place || place.IsFileBox)
        {
            return;
        }
        args.Handled = true;
        if (_closed || _dismissing) return;
        _menu?.SetCurrentValue(System.Windows.Controls.ContextMenu.IsOpenProperty, false);
        var menu = new System.Windows.Controls.ContextMenu();
        var expand = new System.Windows.Controls.MenuItem { Header = "在面板内展开" };
        expand.Click += (_, _) => SelectPlace(place.Id);
        menu.Items.Add(expand);

        if (place.Path is { Length: > 0 } dirPath)
        {
            var openExplorer = new System.Windows.Controls.MenuItem { Header = "在资源管理器中打开" };
            openExplorer.Click += (_, _) =>
            {
                var entry = new LauncherPanelEntry(
                    new TopBarSearchCandidate(TopBarSearchItemKind.Folder, place.Name, Path.GetFileName(dirPath), dirPath),
                    "位置");
                QueueOpen(entry, locate: false);
            };
            menu.Items.Add(openExplorer);

            if (place.IsPinnedCustom && _saveOrganizer is not null)
            {
                menu.Items.Add(new System.Windows.Controls.Separator());
                var unpin = new System.Windows.Controls.MenuItem { Header = "取消收藏" };
                unpin.Click += (_, _) => _ = TogglePinnedFolderAsync(dirPath);
                menu.Items.Add(unpin);
            }
        }
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu)) _menu = null;
        };
        menu.PlacementTarget = sender as UIElement ?? FilePlacesBar;
        _menu = menu;
        menu.IsOpen = true;
    }

    private async void OnAddPinnedFolderClick(object sender, RoutedEventArgs args)
    {
        if (_closed || _dismissing || _saveOrganizer is null) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要收藏到常用位置的文件夹"
        };
        _modalDialogOpen = true;
        bool? picked;
        try
        {
            picked = dialog.ShowDialog(this);
        }
        finally
        {
            _modalDialogOpen = false;
        }
        if (picked != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return;
        }
        await TogglePinnedFolderAsync(dialog.FolderName, pinOnly: true);
    }

    private async Task TogglePinnedFolderAsync(string path, bool pinOnly = false)
    {
        if (_closed || _dismissing || _saveOrganizer is null) return;
        var normalized = LauncherPreferencesState.NormalizeFolderPath(path);
        if (normalized is null) return;

        bool added;
        if (_preferences.IsFolderPinned(normalized))
        {
            if (pinOnly)
            {
                StatusText.Text = "该文件夹已在常用位置中";
                return;
            }
            _preferences.UnpinFolder(normalized);
            added = false;
        }
        else
        {
            if (!_preferences.PinFolder(normalized))
            {
                return;
            }
            added = true;
        }
        _saveOrganizer();
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(normalized));
        if (string.IsNullOrWhiteSpace(folderName)) folderName = normalized;
        try
        {
            var snapshot = await _loadSnapshot();
            if (_closed || _dismissing) return;
            _snapshot = snapshot;
            if (!added && _fileMode == FileBrowseMode.Place && _selectedPlaceId == "pinned:" + normalized)
            {
                _fileMode = FileBrowseMode.Overview;
                _selectedPlaceId = null;
                _folderStack.Clear();
            }
            await SearchAsync(preserveView: true);
            if (!_closed && !_dismissing)
            {
                StatusText.Text = added ? $"已收藏常用文件夹：{folderName}" : $"已从常用位置移除：{folderName}";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    private void OnBackFolderClick(object sender, RoutedEventArgs args) => NavigateBackFolder();

    private void OnBreadcrumbClick(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is BreadcrumbSegment segment && !segment.IsCurrent)
        {
            NavigateToBreadcrumbDepth(segment.Depth);
        }
    }

    private void OnToggleRecentPrivacyClick(object sender, RoutedEventArgs args)
    {
        _preferences.ShowRecentFiles = !_preferences.ShowRecentFiles;
        _saveOrganizer?.Invoke();
        RefreshRecentPreference();
        StatusText.Text = _preferences.ShowRecentFiles ? "已开启最近使用文件" : "已隐藏最近使用文件";
    }

    private void OnOpenCurrentDirectoryClick(object sender, RoutedEventArgs args)
    {
        if (CurrentOpenableDirectoryPath() is not { } dirPath) return;
        var title = _folderStack.Count > 0 ? _folderStack.Peek().Title : Path.GetFileName(Path.TrimEndingDirectorySeparator(dirPath));
        var entry = new LauncherPanelEntry(
            new TopBarSearchCandidate(TopBarSearchItemKind.Folder, string.IsNullOrWhiteSpace(title) ? dirPath : title,
                Path.GetFileName(Path.TrimEndingDirectorySeparator(dirPath)), dirPath),
            "位置");
        QueueOpen(entry, locate: false);
    }

    private void SelectFileType(LauncherFileCategory kind)
    {
        CancelDirectoryLoad();
        _folderStack.Clear();
        _activeTab = LauncherMainTab.Files;
        _fileMode = FileBrowseMode.FileType;
        _selectedFileType = kind;
        _selectedPlaceId = null;
        UpdateMainTabButtons();
        Render(_unfilteredRows, preserveSelection: false);
    }

    private void SelectPlace(string placeId)
    {
        var place = (_snapshot?.Places ?? []).FirstOrDefault(p => p.Id == placeId);
        if (place is null) return;
        CancelDirectoryLoad();
        _folderStack.Clear();
        _activeTab = LauncherMainTab.Files;
        _fileMode = FileBrowseMode.Place;
        _selectedPlaceId = placeId;
        _selectedFileType = null;
        UpdateMainTabButtons();
        if (place.IsFileBox)
        {
            Render(_unfilteredRows, preserveSelection: false);
        }
        else if (place.Path is { Length: > 0 } dirPath)
        {
            _ = LoadDirectoryRowsAsync(dirPath);
        }
    }

    private async Task DrillIntoDirectoryAsync(string path, string title)
    {
        _folderStack.Push((path, title));
        await LoadDirectoryRowsAsync(path);
    }

    private void NavigateBackFolder()
    {
        if (_folderStack.Count == 0) return;
        _folderStack.Pop();
        if (_folderStack.Count > 0)
        {
            _ = LoadDirectoryRowsAsync(_folderStack.Peek().Path);
            return;
        }
        if (_fileMode == FileBrowseMode.Place && _selectedPlaceId is { } placeId)
        {
            var place = (_snapshot?.Places ?? []).FirstOrDefault(p => p.Id == placeId);
            if (place is { IsFileBox: false, Path: { Length: > 0 } dirPath })
            {
                _ = LoadDirectoryRowsAsync(dirPath);
                return;
            }
        }
        CancelDirectoryLoad();
        Render(_unfilteredRows, preserveSelection: false);
    }

    private void NavigateToBreadcrumbDepth(int targetDepth)
    {
        while (_folderStack.Count > targetDepth)
        {
            _folderStack.Pop();
        }
        if (_folderStack.Count > 0)
        {
            _ = LoadDirectoryRowsAsync(_folderStack.Peek().Path);
            return;
        }
        if (_fileMode == FileBrowseMode.Place && _selectedPlaceId is { } placeId)
        {
            var place = (_snapshot?.Places ?? []).FirstOrDefault(p => p.Id == placeId);
            if (place is { IsFileBox: false, Path: { Length: > 0 } dirPath })
            {
                _ = LoadDirectoryRowsAsync(dirPath);
                return;
            }
        }
        CancelDirectoryLoad();
        Render(_unfilteredRows, preserveSelection: false);
    }

    private IReadOnlyList<BreadcrumbSegment> BuildFileBreadcrumbs()
    {
        if (_activeTab != LauncherMainTab.Files || _folderStack.Count == 0)
        {
            return [];
        }
        var rootTitle = _fileMode switch
        {
            FileBrowseMode.Place when _selectedPlaceId is { } id =>
                (_snapshot?.Places ?? []).FirstOrDefault(p => p.Id == id)?.Name ?? "文件",
            FileBrowseMode.FileType when _selectedFileType is { } kind =>
                LauncherFileCategoryRules.DisplayName(kind),
            FileBrowseMode.Recent => "最近使用",
            _ => "文件总览"
        };
        var segments = new List<BreadcrumbSegment>(_folderStack.Count + 1)
        {
            new(0, rootTitle, IsCurrent: false)
        };
        var stackBottomUp = _folderStack.Reverse().ToArray();
        for (var i = 0; i < stackBottomUp.Length; i++)
        {
            segments.Add(new BreadcrumbSegment(i + 1, stackBottomUp[i].Title, IsCurrent: i == stackBottomUp.Length - 1));
        }
        return segments;
    }

    private string? CurrentDirectoryDisplayName()
    {
        if (_activeTab != LauncherMainTab.Files) return null;
        if (_folderStack.Count > 0) return _folderStack.Peek().Title;
        if (_fileMode == FileBrowseMode.Place && _selectedPlaceId is { } id)
        {
            var place = (_snapshot?.Places ?? []).FirstOrDefault(p => p.Id == id);
            return place is { IsFileBox: false, Path: { Length: > 0 } } ? place.Name : null;
        }
        return null;
    }

    private void CancelDirectoryLoad()
    {
        _directoryCts?.Cancel();
        _directoryCts?.Dispose();
        _directoryCts = null;
        _loadingDirectory = false;
        _directoryTruncated = false;
        _directoryError = null;
        _directoryRows = [];
    }

    private async Task LoadDirectoryRowsAsync(string path)
    {
        CancelDirectoryLoad();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _directoryCts = cts;
        _loadingDirectory = true;
        _directoryError = null;
        Render(_unfilteredRows, preserveSelection: false);
        try
        {
            var listing = await LauncherFileCatalog.ReadDirectoryEntriesAsync(path, cts.Token);
            if (_closed || _dismissing || cts.IsCancellationRequested || !ReferenceEquals(_directoryCts, cts)) return;
            _loadingDirectory = false;
            _directoryTruncated = listing.Truncated;
            _directoryError = listing.Error;
            var canManageGroups = _saveOrganizer is not null;
            _directoryRows = listing.Entries
                .Select(entry =>
                {
                    var row = new PanelRow(entry, isFavorite: false, isDockPinned: false, [entry.Source],
                        startMenuFolder: null, canManageGroups: canManageGroups);
                    row.RefreshFavorite(_preferences, canManageGroups);
                    return row;
                })
                .ToArray();
            Render(_unfilteredRows, preserveSelection: false);
        }
        catch (OperationCanceledException) { }
    }

    private string? CurrentOpenableDirectoryPath()
    {
        if (_activeTab != LauncherMainTab.Files) return null;
        if (_folderStack.Count > 0) return _folderStack.Peek().Path;
        if (_fileMode == FileBrowseMode.Place && _selectedPlaceId is { } id)
        {
            var place = (_snapshot?.Places ?? []).FirstOrDefault(p => p.Id == id);
            return place is { IsFileBox: false, Path: { Length: > 0 } } ? place.Path : null;
        }
        return null;
    }

    private void UpdateFilesNavigation(List<PanelRow> rows)
    {
        var fileRows = rows.Where(row => row.Entry.Candidate.Kind != TopBarSearchItemKind.App
            && (_preferences.ShowRecentFiles || !IsRecentOnly(row))).ToList();
        var recentRows = _preferences.ShowRecentFiles ? BuildRecentRows() : [];

        FilesOverviewButton.DataContext = new CategoryChip(null, "文件总览",
            _activeTab == LauncherMainTab.Files && _folderStack.Count == 0 && _fileMode == FileBrowseMode.Overview);
        FilesRecentButton.DataContext = new CategoryChip(null, "最近使用",
            _activeTab == LauncherMainTab.Files && _folderStack.Count == 0 && _fileMode == FileBrowseMode.Recent,
            CountText: _preferences.ShowRecentFiles ? recentRows.Count.ToString() : "已隐藏");

        var byType = fileRows.ToLookup(row => row.FileCategory);
        var typeChips = LauncherFileCategoryRules.OrderedCategories
            .Where(kind => kind != LauncherFileCategory.Other || byType[kind].Any() || _selectedFileType == kind)
            .Select(kind => new CategoryChip(
                null,
                LauncherFileCategoryRules.DisplayName(kind),
                _activeTab == LauncherMainTab.Files && _folderStack.Count == 0 && _fileMode == FileBrowseMode.FileType && _selectedFileType == kind,
                Glyph: LauncherFileCategoryRules.Glyph(kind),
                CountText: byType[kind].Count().ToString(),
                FileKind: kind));
        RefreshNavigation(FileTypeList, typeChips);

        var places = _snapshot?.Places ?? [];
        FilePlacesNavHeading.Visibility = places.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var placeChips = places.Select(place => new CategoryChip(
            null,
            place.Name,
            _activeTab == LauncherMainTab.Files && _folderStack.Count == 0 && _fileMode == FileBrowseMode.Place && _selectedPlaceId == place.Id,
            Glyph: place.Glyph,
            CountText: place.IsFileBox && place.BoxEntries is not null ? place.BoxEntries.Count.ToString() : "",
            PlaceId: place.Id));
        RefreshNavigation(FilePlacesNavList, placeChips);
    }

    private List<PanelRow> ApplyFilesFilter(List<PanelRow> available)
    {
        if (_folderStack.Count > 0)
        {
            return SortFileRows(_directoryRows);
        }
        if (_fileMode == FileBrowseMode.Recent)
        {
            return _preferences.ShowRecentFiles ? BuildRecentRows() : [];
        }
        if (_fileMode == FileBrowseMode.Place && _selectedPlaceId is { } placeId)
        {
            var place = (_snapshot?.Places ?? []).FirstOrDefault(p => p.Id == placeId);
            if (place is null) return [];
            if (place.IsFileBox)
            {
                var canManageGroups = _saveOrganizer is not null;
                var boxRows = (place.BoxEntries ?? [])
                    .Select(entry =>
                    {
                        var row = new PanelRow(entry, isFavorite: false, isDockPinned: false, [entry.Source],
                            startMenuFolder: null, canManageGroups: canManageGroups);
                        row.RefreshFavorite(_preferences, canManageGroups);
                        return row;
                    });
                return SortFileRows(boxRows);
            }
            return SortFileRows(_directoryRows);
        }

        var files = available.Where(row => row.Entry.Candidate.Kind != TopBarSearchItemKind.App
            && (_preferences.ShowRecentFiles || !IsRecentOnly(row))).ToList();
        if (_fileMode == FileBrowseMode.FileType && _selectedFileType is { } kind)
        {
            return SortFileRows(files.Where(row => row.FileCategory == kind));
        }
        return files;
    }

    private List<PanelRow> SortFileRows(IEnumerable<PanelRow> source) =>
        _preferences.FilesSortByModified
            ? source
                .OrderByDescending(row => row.Entry.Candidate.Kind == TopBarSearchItemKind.Folder)
                .ThenByDescending(row => row.Entry.LastAccessedLocal ?? DateTimeOffset.MinValue)
                .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList()
            : source
                .OrderByDescending(row => row.Entry.Candidate.Kind == TopBarSearchItemKind.Folder)
                .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

    private IReadOnlyList<object> BuildFilesDisplayItems()
    {
        if (_folderStack.Count > 0 || _fileMode is FileBrowseMode.FileType or FileBrowseMode.Place)
        {
            return _rows;
        }

        if (_fileMode == FileBrowseMode.Recent)
        {
            if (!_preferences.ShowRecentFiles || _rows.Count == 0) return [];
            var today = DateTime.Today;
            var grouped = _rows.ToLookup(row => row.Entry.LastAccessedLocal is { } dt
                ? LauncherFileCategoryRules.RecentBucket(dt, today) : "更早");
            var items = new List<object>();
            foreach (var bucket in new[] { "今天", "本周", "更早" })
            {
                AddSection(items, bucket, grouped[bucket]);
            }
            return items;
        }

        // 文件总览：优先展示最近使用（开启时），再展示桌面/分组盒/固定文件
        var overviewItems = new List<object>();
        var recentKeys = new HashSet<string>(StringComparer.Ordinal);
        if (_preferences.ShowRecentFiles)
        {
            var recent = BuildRecentRows().Take(8).ToList();
            foreach (var r in recent) recentKeys.Add(r.Entry.Key);
            AddSection(overviewItems, "最近使用", recent);
        }

        var remaining = _rows.Where(row => !recentKeys.Contains(row.Entry.Key) && !IsRecentOnly(row)).ToList();
        if (overviewItems.Count > 0)
        {
            AddSection(overviewItems, "已收录文件与文件夹", remaining);
            return overviewItems;
        }
        return remaining;
    }

    private List<PanelRow> BuildRecentRows()
    {
        var canManageGroups = _saveOrganizer is not null;
        return (_snapshot?.RecentEntries ?? [])
            .Select(entry =>
            {
                var row = new PanelRow(entry, isFavorite: false, isDockPinned: false, [entry.Source],
                    startMenuFolder: null, canManageGroups: canManageGroups);
                row.RefreshFavorite(_preferences, canManageGroups);
                return row;
            })
            .ToList();
    }

    private static bool IsRecentOnly(PanelRow row) =>
        row.Sources.Count == 1 && string.Equals(row.Sources[0], "最近使用", StringComparison.OrdinalIgnoreCase);

    private (string Title, string Subtitle) ResolveFilesHeader()
    {
        if (_folderStack.Count > 0)
        {
            var breadcrumb = string.Join(" › ", _folderStack.Reverse().Select(x => x.Title));
            var subtitle = _loadingDirectory
                ? "正在读取…"
                : (_directoryTruncated ? "面板内只读浏览" : $"{_rows.Count} 项 · 面板内只读浏览");
            return (breadcrumb, subtitle);
        }
        if (_fileMode == FileBrowseMode.Recent)
        {
            return ("最近使用", _preferences.ShowRecentFiles ? "来自 Windows 最近记录" : "已隐藏");
        }
        if (_fileMode == FileBrowseMode.FileType && _selectedFileType is { } kind)
        {
            return (LauncherFileCategoryRules.DisplayName(kind), string.Empty);
        }
        if (_fileMode == FileBrowseMode.Place && _selectedPlaceId is { } id)
        {
            var place = (_snapshot?.Places ?? []).FirstOrDefault(p => p.Id == id);
            if (place is not null)
            {
                var subtitle = place.IsFileBox
                    ? "文件收纳盒"
                    : (_loadingDirectory
                        ? "正在读取…"
                        : (_directoryTruncated ? (place.Path ?? string.Empty) : $"{place.Path} · {_rows.Count} 项"));
                return (place.Name, subtitle);
            }
        }
        return ("文件总览", "常用位置、文件收纳盒与最近使用（只读路径，不移动原文件）");
    }
}
