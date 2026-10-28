using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BingLan.App.Interop;
using BingLan.Core.Models;
using BingLan.Core.Services;
using Forms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace BingLan.App.Windows;

public partial class FileBoxWindow : WidgetWindowBase
{
    private const string MyComputerEntry = "shell:MyComputerFolder";
    private const string RecycleBinEntry = "shell:RecycleBinFolder";

    private readonly ObservableCollection<FileTile> _items;
    private readonly HashSet<Button> _singleClickFileTiles = [];
    private bool _initialIconLoadStarted;

    public FileBoxWindow(FileBoxState state)
    {
        State = state;
        _items = new ObservableCollection<FileTile>(
            state.Items.OrderBy(x => x.Order).Select(x => new FileTile(x, null)));
        InitializeComponent();
        DataContext = state;
        ApplyAppearance(state.Appearance);
        FileList.ItemsSource = _items;
        ApplyViewMode();
        ApplyPlacement(state.Placement, state.IsLocked);
        WidgetCornerRadius = state.CornerRadius;
        RefreshCount();
        Loaded += FileBoxWindow_Loaded;
    }

    public FileBoxState State { get; }

    /// <summary>The other open boxes a mapping can be transferred to.</summary>
    internal Func<IReadOnlyList<FileBoxWindow>> OtherBoxes { get; set; } = () => [];

    /// <summary>Sets the file types and whether folders this box collects when organising.</summary>
    internal void SetCollectRule(IEnumerable<string> extensions, bool folders)
    {
        State.CollectExtensions = FileMappingService.NormalizeExtensions(extensions);
        State.CollectFolders = folders;
        NotifyStateChanged();
    }

    /// <summary>Switches the box between the tile view and the compact list view.</summary>
    internal void SetViewMode(FileBoxViewMode mode)
    {
        if (State.ViewMode == mode)
        {
            return;
        }
        State.ViewMode = mode;
        ApplyViewMode();
        NotifyStateChanged();
    }

    /// <summary>Applies the item template and panel that match the box's view mode.</summary>
    private void ApplyViewMode()
    {
        var list = State.ViewMode == FileBoxViewMode.List;
        FileList.ItemTemplate = (DataTemplate)FindResource(list ? "FileRowTemplate" : "FileTileTemplate");
        FileList.ItemsPanel = (System.Windows.Controls.ItemsPanelTemplate)FindResource(
            list ? "FileListPanelTemplate" : "FileTilePanelTemplate");
    }

    internal void AssignDesktopCategory(DesktopGroupCategory category, string title)
    {
        State.DesktopCategory = category;
        State.Title = title;
        TitleEditor.Text = title;
        NotifyStateChanged();
    }

    protected override System.Windows.Controls.TextBox? TitleEditorBox => TitleEditor;

    protected override void CommitTitleEdit()
    {
        State.Title = string.IsNullOrWhiteSpace(TitleEditor.Text) ? "桌面分组" : TitleEditor.Text.Trim();
        TitleEditor.Text = State.Title;
        NotifyStateChanged();
    }

    protected override void OnLockChanged(bool locked)
    {
        State.IsLocked = locked;
    }

    private const double CollapsedHeight = 72d;
    private const double DefaultMinHeight = 166d;

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        SetCollapsed(!State.IsCollapsed);
        NotifyStateChanged();
    }

    /// <summary>
    /// Folds the box up to its title bar, or unfolds it to the height it had. A folded box
    /// keeps its width and can still be dragged; only its height is fixed.
    /// </summary>
    internal void SetCollapsed(bool collapsed)
    {
        if (collapsed && !State.IsCollapsed)
        {
            State.ExpandedHeight = Height;
        }
        State.IsCollapsed = collapsed;
        var body = collapsed ? Visibility.Collapsed : Visibility.Visible;
        foreach (var element in BoxLayout.Children.OfType<FrameworkElement>().Where(element => System.Windows.Controls.Grid.GetRow(element) > 0))
        {
            element.Visibility = body;
        }
        if (collapsed)
        {
            MinHeight = CollapsedHeight;
            MaxHeight = CollapsedHeight;
            Height = CollapsedHeight;
        }
        else
        {
            MaxHeight = double.PositiveInfinity;
            MinHeight = DefaultMinHeight;
            Height = Math.Max(DefaultMinHeight, State.ExpandedHeight > 0 ? State.ExpandedHeight : 306d);
            // 展开向下长高，等原生矩形反映新尺寸后把卡片整体收回屏幕内。
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded, EnsureFullyOnScreen);
        }
        CollapseButton.Content = collapsed ? "" : "";
        CollapseButton.ToolTip = collapsed ? "展开分组盒" : "折叠成标题条";
    }

    private async void FileBoxWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (State.IsCollapsed)
        {
            SetCollapsed(true);
        }
        if (_initialIconLoadStarted)
        {
            return;
        }

        _initialIconLoadStarted = true;
        var initialMappings = _items
            .Select(tile => tile.Mapping)
            .ToDictionary(mapping => mapping.Id);
        var icons = await Task.Run(() => LoadIcons(initialMappings.Values.Select(mapping => mapping.Path)));

        for (var index = 0; index < _items.Count; index++)
        {
            var tile = _items[index];
            if (!initialMappings.ContainsKey(tile.Mapping.Id))
            {
                continue;
            }

            icons.TryGetValue(tile.Path, out var icon);
            _items[index] = new FileTile(tile.Mapping, icon);
        }
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WpfOpenFileDialog
        {
            Title = "选择要映射到桌面分组盒的文件",
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            await AddMappingsWithStatusAsync(dialog.FileNames);
        }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择要映射到桌面分组盒的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            await AddMappingsWithStatusAsync([dialog.SelectedPath]);
        }
    }

    private async void ImportDesktop_Click(object sender, RoutedEventArgs e)
    {
        AddButton.IsEnabled = false;
        StatusText.Text = "正在扫描用户与公共桌面…";

        try
        {
            var desktopDirectories = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            };
            var paths = await Task.Run(
                () => FileMappingService.EnumerateDirectChildren(desktopDirectories));
            var added = await AddMappingsAsync(paths);
            StatusText.Text = added > 0
                ? $"已新增 {added} 项"
                : "没有新的桌面项目可导入";
        }
        finally
        {
            AddButton.IsEnabled = true;
        }
    }

    private void AutoOrganize_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "正在扫描并生成分类盒…";
        RequestDesktopOrganization();
    }

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void SortManual_Click(object sender, RoutedEventArgs e) =>
        ApplySort(FileMappingSortMode.Manual);

    private void SortName_Click(object sender, RoutedEventArgs e) =>
        ApplySort(FileMappingSortMode.Name);

    private void SortType_Click(object sender, RoutedEventArgs e) =>
        ApplySort(FileMappingSortMode.Type);

    public void CaptureState()
    {
        CopyPlacementTo(State.Placement);
        State.CornerRadius = WidgetCornerRadius;
        State.Items = _items.Select(x => x.Mapping).ToList();
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        // A tile dragged within its own box is not a new item.
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] dragged
            && dragged.All(path => _items.Any(tile => string.Equals(tile.Path, path, StringComparison.OrdinalIgnoreCase))))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Link
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        await AddMappingsWithStatusAsync(paths);
    }

    private System.Windows.Point? _tileDragStart;

    // A tile can be dragged out, for example onto the dock to pin an app. Only a link is
    // offered, so a drop elsewhere creates a shortcut and never copies or moves the file.
    private void FileTile_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_tileDragStart is not { } start
            || e.LeftButton != MouseButtonState.Pressed
            || sender is not Button { DataContext: FileTile tile } button
            || tile.IsShellEntry
            || tile.IsMissing)
        {
            return;
        }

        var offset = e.GetPosition(this) - start;
        if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _tileDragStart = null;
        _singleClickFileTiles.Remove(button);
        DragDrop.DoDragDrop(
            button,
            new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new[] { tile.Path }),
            System.Windows.DragDropEffects.Link);
    }

    private void FileTile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }
        _tileDragStart = e.GetPosition(this);

        if (e.ClickCount == 1)
        {
            _singleClickFileTiles.Add(button);
        }
        else
        {
            _singleClickFileTiles.Remove(button);
        }
    }

    private void FileTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _singleClickFileTiles.Remove(button))
        {
            return;
        }

        if (button.DataContext is not FileTile tile)
        {
            return;
        }

        if (tile.IsMissing && FileMappingService.IsMissing(tile.Mapping))
        {
            BeginRelink(tile);
            return;
        }

        Open(tile);
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileTile tile)
        {
            Open(tile);
        }
    }

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileTile tile || tile.IsShellEntry)
        {
            return;
        }
        StartShell(new ProcessStartInfo("explorer.exe", $"/select,\"{tile.Path}\"") { UseShellExecute = true });
    }

    private async void RefreshIcon_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileTile tile)
        {
            return;
        }

        var icon = await Task.Run(() => ShellIconProvider.GetIcon(tile.Path));
        // The tile may have moved or gone while the icon loaded.
        var index = _items.IndexOf(tile);
        if (index >= 0)
        {
            _items[index] = new FileTile(tile.Mapping, icon);
        }
    }

    private void FileTileContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu { DataContext: FileTile tile } menu)
        {
            return;
        }

        // Shell namespace entries never resolve to a real path, so relocating or revealing
        // their location in Explorer does not apply to them.
        var locationVisibility = tile.IsShellEntry ? Visibility.Collapsed : Visibility.Visible;
        foreach (var item in menu.Items.OfType<System.Windows.Controls.MenuItem>())
        {
            if (Equals(item.Header, "打开所在位置") || Equals(item.Header, "重新定位…"))
            {
                item.Visibility = locationVisibility;
            }
            else if (Equals(item.Header, "转移到其他分组盒"))
            {
                var others = OtherBoxes();
                item.Items.Clear();
                foreach (var other in others)
                {
                    var target = new System.Windows.Controls.MenuItem { Header = other.State.Title };
                    target.Click += (_, _) => TransferMapping(tile, other);
                    item.Items.Add(target);
                }
                item.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private void RenameMapping_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileTile tile)
        {
            return;
        }

        var dialog = new TextPromptDialog(
            "修改显示名称",
            "留空则恢复原名称。",
            tile.Name)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            RenameMapping(tile, dialog.Value);
        }
    }

    internal void RenameMapping(FileTile tile, string displayName)
    {
        CaptureState();
        if (FileMappingService.Rename(State, tile.Mapping.Id, displayName))
        {
            RefreshTile(tile);
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// Moves this PC, the Recycle Bin, Downloads and the desktop folder to the given box;
    /// they are places, not apps or files.
    /// </summary>
    internal void MoveSystemPlacesTo(FileBoxWindow target)
    {
        var places = new[] { KnownFolders.GetDownloadsPath(), DesktopFolderPath() };
        foreach (var tile in _items
                     .Where(tile => tile.IsShellEntry
                         || places.Any(place => string.Equals(place, tile.Path, StringComparison.OrdinalIgnoreCase)))
                     .ToList())
        {
            TransferMapping(tile, target);
        }
    }

    internal void TransferMapping(FileTile tile, FileBoxWindow target)
    {
        CaptureState();
        target.CaptureState();
        if (!FileMappingService.Transfer(State, target.State, tile.Mapping.Id))
        {
            StatusText.Text = $"“{target.State.Title}”中已有这个项目";
            return;
        }

        _items.Remove(tile);
        RefreshCount();
        NotifyStateChanged();
        target._items.Add(new FileTile(tile.Mapping, tile.Icon));
        target.RefreshCount();
        target.NotifyStateChanged();
        StatusText.Text = $"已转移到“{target.State.Title}”";
    }

    private void RelinkFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileTile tile)
        {
            BeginRelink(tile);
        }
    }

    private void RelinkFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileTile tile)
        {
            return;
        }

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择用于重新定位的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            ApplyRelink(tile, dialog.SelectedPath);
        }
    }

    private void BeginRelink(FileTile tile)
    {
        var dialog = new WpfOpenFileDialog
        {
            Title = "选择用于重新定位的文件",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            ApplyRelink(tile, dialog.FileName);
        }
    }

    private void ApplyRelink(FileTile tile, string newPath)
    {
        CaptureState();
        if (!FileMappingService.Relink(State, tile.Mapping.Id, newPath))
        {
            StatusText.Text = "重新定位失败，请确认所选项目存在";
            return;
        }

        ReloadItems();
        NotifyStateChanged();
        StatusText.Text = "已重新定位映射";
    }

    private void Open(FileTile tile)
    {
        if (tile.IsShellEntry)
        {
            StartShell(new ProcessStartInfo("explorer.exe", tile.Path) { UseShellExecute = true });
            return;
        }

        var exists = File.Exists(tile.Path) || Directory.Exists(tile.Path);
        if (exists == tile.IsMissing)
        {
            // The item appeared or disappeared since the card was drawn; redraw its state.
            RefreshTile(tile);
        }
        if (!exists)
        {
            StatusText.Text = "原文件或文件夹已不存在，已标记为“已失效”，可重新定位或移除";
            return;
        }
        StartShell(new ProcessStartInfo(tile.Path) { UseShellExecute = true });
    }

    // Opening can fail (no app for the file type, the elevation prompt declined); the card
    // says so instead of the app closing.
    private void StartShell(ProcessStartInfo start)
    {
        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException)
        {
            StatusText.Text = $"无法打开：{exception.Message}";
        }
    }

    private async void AddSystemEntryMyComputer_Click(object sender, RoutedEventArgs e) =>
        await AddSystemEntryAsync(MyComputerEntry, "此电脑");

    private async void AddSystemEntryRecycleBin_Click(object sender, RoutedEventArgs e) =>
        await AddSystemEntryAsync(RecycleBinEntry, "回收站");

    private async void AddSystemEntryDownloads_Click(object sender, RoutedEventArgs e)
    {
        var path = KnownFolders.GetDownloadsPath();
        if (string.IsNullOrEmpty(path))
        {
            StatusText.Text = "无法定位下载文件夹";
            return;
        }
        await AddSystemEntryAsync(path, "下载");
    }

    private async void AddSystemEntryDesktop_Click(object sender, RoutedEventArgs e) =>
        await AddSystemEntryAsync(DesktopFolderPath(), "桌面");

    private static string DesktopFolderPath() =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>Adds a folder or system location under a fixed display name, such as “下载”.</summary>
    internal Task<bool> AddNamedEntryAsync(string path, string title) => AddSystemEntryAsync(path, title);

    private async Task<bool> AddSystemEntryAsync(string path, string title)
    {
        var icon = await Task.Run(() => ShellIconProvider.GetIcon(path));
        CaptureState();
        var added = FileMappingService.AddSystemEntry(State, path, title);
        if (added == 0)
        {
            StatusText.Text = "该系统入口已存在";
            return false;
        }

        var mapping = State.Items.Single(item => item.Path == path);
        _items.Add(new FileTile(mapping, icon));
        RefreshCount();
        NotifyStateChanged();
        StatusText.Text = $"已添加“{title}”";
        return true;
    }

    private void RemoveMapping_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileTile tile)
        {
            return;
        }
        _items.Remove(tile);
        FileMappingService.Remove(State, tile.Mapping.Id);
        CaptureState();
        RefreshCount();
        NotifyStateChanged();
    }

    private void MoveMappingUp_Click(object sender, RoutedEventArgs e) =>
        MoveMapping(sender, -1);

    private void MoveMappingDown_Click(object sender, RoutedEventArgs e) =>
        MoveMapping(sender, 1);

    private void MoveMapping(object sender, int offset)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileTile tile)
        {
            return;
        }

        CaptureState();
        if (!FileMappingService.Move(State, tile.Mapping.Id, offset))
        {
            return;
        }

        ReloadItems();
        NotifyStateChanged();
    }

    private void ReloadItems()
    {
        var icons = _items.ToDictionary(
            tile => tile.Mapping.Id,
            tile => tile.Icon);
        _items.Clear();
        foreach (var item in State.Items.OrderBy(x => x.Order))
        {
            _items.Add(new FileTile(
                item,
                icons.TryGetValue(item.Id, out var icon)
                    ? icon
                    : ShellIconProvider.GetIcon(item.Path)));
        }
        RefreshCount();
    }

    // Drop sources can hand over malformed paths; those are skipped instead of failing the drop.
    private static string? TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    internal async Task<int> AddMappingsAsync(IEnumerable<string> paths)
    {
        var candidates = paths
            .Select(TryGetFullPath)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var preparedIcons = await Task.Run(() => LoadIcons(candidates));

        CaptureState();
        var existingIds = State.Items.Select(item => item.Id).ToHashSet();
        var added = FileMappingService.AddExisting(State, candidates);
        if (added == 0)
        {
            return 0;
        }

        foreach (var mapping in State.Items
                     .Where(item => !existingIds.Contains(item.Id))
                     .OrderBy(item => item.Order))
        {
            preparedIcons.TryGetValue(mapping.Path, out var icon);
            _items.Add(new FileTile(mapping, icon));
        }
        RefreshCount();
        NotifyStateChanged();
        return added;
    }

    internal int RemoveGoneMappings(IReadOnlySet<Guid> goneIds)
    {
        CaptureState();
        var removed = FileMappingService.RemoveGone(State, goneIds);
        if (removed > 0)
        {
            ReloadItems();
            NotifyStateChanged();
        }
        return removed;
    }

    internal void SetOperationStatus(string message) => StatusText.Text = message;

    private async Task AddMappingsWithStatusAsync(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        var invalid = list.Count(path => TryGetFullPath(path) is not { } full
            || (!File.Exists(full) && !Directory.Exists(full)));
        var added = await AddMappingsAsync(list);
        var skipped = invalid > 0 ? $"{invalid} 项路径无效，已跳过" : string.Empty;
        StatusText.Text = added > 0
            ? skipped.Length > 0 ? $"已新增 {added} 项；{skipped}" : $"已新增 {added} 项"
            : skipped.Length > 0 && invalid == list.Count ? skipped : "这些项目已经在分组盒中" + (skipped.Length > 0 ? $"；{skipped}" : string.Empty);
    }

    private void RefreshTile(FileTile tile)
    {
        var index = _items.IndexOf(tile);
        if (index >= 0)
        {
            _items[index] = new FileTile(tile.Mapping, tile.Icon);
        }
    }

    private static IReadOnlyDictionary<string, ImageSource?> LoadIcons(IEnumerable<string> paths)
    {
        var icons = new Dictionary<string, ImageSource?>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            try
            {
                icons[path] = ShellIconProvider.GetIcon(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                icons[path] = null;
            }
        }
        return icons;
    }

    private void ApplySort(FileMappingSortMode mode)
    {
        CaptureState();
        FileMappingService.Sort(State, mode);
        ReloadItems();
        NotifyStateChanged();
    }

    private void RefreshCount()
    {
        CountText.Text = $"{_items.Count} 项";
    }

    public sealed class FileTile
    {
        public FileTile(FileMappingState mapping)
            : this(mapping, ShellIconProvider.GetIcon(mapping.Path))
        {
        }

        public FileTile(FileMappingState mapping, ImageSource? icon)
        {
            Mapping = mapping;
            Path = mapping.Path;
            IsShellEntry = FileMappingService.IsShellEntry(mapping.Path);
            IsMissing = FileMappingService.IsMissing(mapping);
            Name = FileMappingService.GetDisplayName(mapping);
            Icon = icon;
        }

        public FileMappingState Mapping { get; }
        public string Path { get; }
        public string Name { get; }
        public bool IsShellEntry { get; }
        public bool IsMissing { get; }
        public ImageSource? Icon { get; }
    }
}
