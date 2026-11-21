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

    private void OnBackFolderClick(object sender, RoutedEventArgs args) => NavigateBackFolder();

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
            _directoryRows = listing.Entries
                .Select(entry => new PanelRow(entry, isFavorite: false, [entry.Source], startMenuFolder: null))
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
            return _directoryRows.ToList();
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
                return (place.BoxEntries ?? [])
                    .Select(entry => new PanelRow(entry, isFavorite: false, [entry.Source], startMenuFolder: null))
                    .ToList();
            }
            return _directoryRows.ToList();
        }

        var files = available.Where(row => row.Entry.Candidate.Kind != TopBarSearchItemKind.App
            && (_preferences.ShowRecentFiles || !IsRecentOnly(row))).ToList();
        if (_fileMode == FileBrowseMode.FileType && _selectedFileType is { } kind)
        {
            return files.Where(row => row.FileCategory == kind)
                .OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        return files;
    }

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

    private List<PanelRow> BuildRecentRows() =>
        (_snapshot?.RecentEntries ?? [])
            .Select(entry => new PanelRow(entry, isFavorite: false, [entry.Source], startMenuFolder: null))
            .ToList();

    private static bool IsRecentOnly(PanelRow row) =>
        row.Sources.Count == 1 && string.Equals(row.Sources[0], "最近使用", StringComparison.OrdinalIgnoreCase);

    private (string Title, string Subtitle) ResolveFilesHeader()
    {
        if (_folderStack.Count > 0)
        {
            var breadcrumb = string.Join(" › ", _folderStack.Reverse().Select(x => x.Title));
            return (breadcrumb, _loadingDirectory ? "正在读取…" : $"{_rows.Count} 项 · 面板内只读浏览");
        }
        if (_fileMode == FileBrowseMode.Recent)
        {
            return ("最近使用", _preferences.ShowRecentFiles ? $"{_rows.Count} 项 · 来自 Windows 最近记录" : "已隐藏");
        }
        if (_fileMode == FileBrowseMode.FileType && _selectedFileType is { } kind)
        {
            return (LauncherFileCategoryRules.DisplayName(kind), $"{_rows.Count} 项");
        }
        if (_fileMode == FileBrowseMode.Place && _selectedPlaceId is { } id)
        {
            var place = (_snapshot?.Places ?? []).FirstOrDefault(p => p.Id == id);
            if (place is not null)
            {
                var subtitle = place.IsFileBox
                    ? $"桌面分组盒 · {_rows.Count} 项"
                    : (_loadingDirectory ? "正在读取…" : $"{place.Path} · {_rows.Count} 项");
                return (place.Name, subtitle);
            }
        }
        return ("文件总览", "常用位置、桌面分组盒与最近使用（只读路径，不移动原文件）");
    }
}
