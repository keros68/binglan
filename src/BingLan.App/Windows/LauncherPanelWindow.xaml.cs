using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.App.TopBar;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.TopBar;

namespace BingLan.App.Windows;

internal sealed record LauncherPanelEntry(TopBarSearchCandidate Candidate, string Source, DockPinnedApp? Pinned = null,
    bool IsSavedFallback = false, string? StartMenuFolder = null)
{
    internal string Key
    {
        get
        {
            var target = Pinned?.AppUserModelId is { Length: > 0 } id
                ? "aumid:" + id : Pinned?.TargetPath ?? Pinned?.ExecutablePath ?? Candidate.Path;
            return DockShortcuts.LaunchVariantKey(target, Pinned?.Arguments, Pinned?.WorkingDirectory);
        }
    }
}

internal sealed record LauncherPanelSnapshot(
    IReadOnlyList<LauncherPanelEntry> Entries,
    IReadOnlyList<LauncherPanelEntry> Favorites,
    bool EverythingEnabled);

public partial class LauncherPanelWindow : Window
{
    // Native file checks cannot be cancelled. Limit the outstanding checks across
    // repeated panel sessions; consider a dedicated provider only if this proves insufficient.
    private static readonly SemaphoreSlim PathCheckGate = new(2, 2);
    private static readonly SemaphoreSlim IconGate = new(2, 2);
    private readonly Func<Task<LauncherPanelSnapshot>> _loadSnapshot;
    private readonly Action<LauncherPanelEntry, bool> _openTarget;
    private readonly Func<LauncherPanelEntry, string>? _pinToDock;
    private readonly Func<IList<LauncherGroupState>> _readGroups;
    private readonly Func<IDictionary<string, LauncherAppCategory>>? _readCategoryOverrides;
    private readonly Action? _saveOrganizer;
    private readonly Func<IReadOnlyList<(Guid Id, string Title)>> _readFileBoxes;
    private readonly Func<Guid, string, Task<string>> _sendToFileBox;
    private readonly MonitorSnapshot _monitor;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _debounce;
    private readonly DockGlassSurface _glass;
    private readonly LauncherAppearanceState _appearance;
    private readonly DesktopStyleState _style;
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _searchCancellation;
    private Task _searchRowsReady = Task.CompletedTask;
    private LauncherPanelSnapshot? _snapshot;
    private EverythingSearchClient? _everything;
    private ContextMenu? _menu;
    private List<LauncherPanelEntry> _supplemental = [];
    private List<PanelRow> _unfilteredRows = [];
    private List<PanelRow> _rows = [];
    private string? _category;
    private nint _handle;
    private int _searchVersion;
    private int _renderedSearchVersion = -1;
    private bool _closed;
    private bool _dismissing;
    private bool _composing;
    private bool _openingTarget;
    private bool _pinningTarget;
    private bool _queryWasEmpty = true;
    private PanelFilter _filter = PanelFilter.Apps;
    private PanelFilter _browseFilter = PanelFilter.Apps;

    internal LauncherPanelWindow(
        Func<Task<LauncherPanelSnapshot>> loadSnapshot,
        Action<LauncherPanelEntry, bool> openTarget,
        Func<IReadOnlyList<(Guid Id, string Title)>> readFileBoxes,
        Func<Guid, string, Task<string>> sendToFileBox,
        nint previousForeground,
        MonitorSnapshot monitor,
        LauncherAppearanceState? appearance = null,
        DesktopStyleState? style = null,
        Func<LauncherPanelEntry, string>? pinToDock = null,
        Func<IList<LauncherGroupState>>? readGroups = null,
        Action? saveOrganizer = null,
        Func<IDictionary<string, LauncherAppCategory>>? readCategoryOverrides = null)
    {
        _loadSnapshot = loadSnapshot;
        _openTarget = openTarget;
        _readFileBoxes = readFileBoxes;
        _sendToFileBox = sendToFileBox;
        _pinToDock = pinToDock;
        _readGroups = readGroups ?? (() => Array.Empty<LauncherGroupState>());
        _saveOrganizer = saveOrganizer;
        _readCategoryOverrides = readCategoryOverrides;
        PreviousForeground = previousForeground;
        _monitor = monitor;
        _appearance = appearance ?? new LauncherAppearanceState();
        _style = style ?? new DesktopStyleState();
        InitializeComponent();
        _glass = new DockGlassSurface(this, PanelSurface, "FloatingGlassSurfaceBrush",
            "FloatingGlassBorderBrush", "FloatingGlassPanelBrush");
        InitializePanelAppearance();
        NewGroupButton.Visibility = saveOrganizer is null ? Visibility.Collapsed : Visibility.Visible;
        ApplyAppearance();
        AccessibilityThemeManager.HighContrastChanged += OnHighContrastChanged;
        _debounce = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = SearchAsync(); };
        InputMethod.SetPreferredImeState(SearchBox, InputMethodState.On);
        InputMethod.SetPreferredImeConversionMode(SearchBox, ImeConversionModeValues.Native);
        TextCompositionManager.AddPreviewTextInputStartHandler(SearchBox, (_, args) =>
        {
            if (args.TextComposition.CompositionText.Length > 0) SuspendForComposition();
        });
        TextCompositionManager.AddPreviewTextInputUpdateHandler(SearchBox, (_, args) =>
        {
            if (args.TextComposition.CompositionText.Length > 0) SuspendForComposition();
            else ResumeAfterComposition();
        });
        SearchBox.PreviewTextInput += (_, _) => ResumeAfterComposition();
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            var style = (long)NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle);
            NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExStyle, (nint)(style | NativeMethods.WsExToolWindow));
            if (HwndSource.FromHwnd(_handle) is { } source) _everything = new EverythingSearchClient(source);
            PositionPanel();
        };
        Loaded += OnPanelLoaded;
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(PositionPanel);
        Closed += (_, _) =>
        {
            AccessibilityThemeManager.HighContrastChanged -= OnHighContrastChanged;
            _closed = true;
            _debounce.Stop();
            _lifetime.Cancel();
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _searchCancellation = null;
            _menu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
            _everything?.Dispose();
            _glass.Dispose();
            _lifetime.Dispose();
        };
    }

    private void OnHighContrastChanged(object? sender, EventArgs e) => ApplyAppearance();

    internal void ApplyAppearance()
    {
        PanelSurface.CornerRadius = new CornerRadius(_appearance.FollowCardLook
            ? LauncherAppearanceState.DefaultCornerRadius : _appearance.CornerRadius);
        var glass = _appearance.ResolveGlass(_style);
        FloatingGlassPalette.Apply(Resources, glass,
            AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast, launcher: true);
        if (_appearance.UseClearGlass || ((Color)ColorConverter.ConvertFromString(glass.Surface)).A < 90)
        {
            Resources["FloatingGlassTileLabelBrush"] = Resources["FloatingGlassLabelBrush"];
            Resources["FloatingGlassNavigationBrush"] = Resources["FloatingGlassLabelBrush"];
            Resources["FloatingGlassHeadingBrush"] = Resources["FloatingGlassLabelBrush"];
        }
        ApplyPanelMaterial();
        MaterialButton.Content = _appearance.UseClearGlass ? "清透" : "磨砂";
        MaterialButton.ToolTip = _appearance.UseClearGlass ? "切换为磨砂玻璃" : "切换为清透玻璃";
    }

    private void OnMaterialClick(object sender, RoutedEventArgs args)
    {
        _appearance.UseClearGlass = !_appearance.UseClearGlass;
        ApplyAppearance();
        _saveOrganizer?.Invoke();
    }

    internal nint PreviousForeground { get; }
    internal bool DismissedOnDeactivation { get; private set; }
    internal IReadOnlyList<LauncherPanelEntry> VisibleEntries => ResultsList.Items.OfType<PanelRow>().Select(row => row.Entry).ToArray();

    internal void ShowPanel()
    {
        PreparePanelAppearance();
        Show();
    }

    internal void Dismiss(bool restoreForeground = false)
    {
        // Focus and visibility changes leave the input message before ending an IME text store.
        Dispatcher.BeginInvoke(() => ClosePanel(restoreForeground), DispatcherPriority.Input);
    }

    private void ClosePanel(bool restoreForeground, bool animate = true)
    {
        if (_closed || _dismissing) return;
        _dismissing = true;
        _debounce.Stop();
        CancelSearch();
        IsHitTestVisible = false;
        void Finish()
        {
            if (_closed) return;
            var ownsForeground = DockNativeMethods.GetForegroundWindow() == _handle;
            Close();
            if (restoreForeground && ownsForeground && PreviousForeground != 0 && PreviousForeground != _handle
                && DockNativeMethods.IsWindow(PreviousForeground))
                _ = DockNativeMethods.SetForegroundWindow(PreviousForeground);
        }
        if (animate) AnimatePanelDismiss(Finish);
        else Finish();
    }

    private async void OnPanelLoaded(object sender, RoutedEventArgs args)
    {
        PositionPanel();
        Activate();
        SearchBox.Focus();
        if (DockNativeMethods.GetForegroundWindow() != _handle)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closed) return;
                _ = DockNativeMethods.SetForegroundWindow(_handle);
                Activate();
                SearchBox.Focus();
                if (DockNativeMethods.GetForegroundWindow() != _handle) ClosePanel(restoreForeground: false, animate: false);
            }, DispatcherPriority.Input);
        }
        if (_closed) return;
        try
        {
            var snapshot = await _loadSnapshot();
            if (_closed) return;
            _snapshot = snapshot;
            await SearchAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (!_closed) StatusText.Text = "暂时无法载入项目，请关闭后重试";
        }
    }

    private void PositionPanel()
    {
        if (_closed || _handle == 0) return;
        var monitors = MonitorCatalog.GetAll();
        var current = monitors.FirstOrDefault(screen => screen.DeviceName == _monitor.DeviceName)
            ?? monitors.FirstOrDefault() ?? _monitor;
        var monitorWork = MonitorCatalog.TryGetWorkingArea(current.MonitorHandle) ?? current.WorkingArea;
        var work = TaskbarEdgeGuard.AvoidNativeTaskbar(monitorWork, current.Bounds);
        var scale = Math.Max(1d, current.Dpi / 96d);
        Width = Math.Min(984, Math.Max(120, work.Width / scale - 24));
        Height = Math.Min(610, Math.Max(120, work.Height / scale - 24));
        BrowseNavigation.Width = Math.Clamp(Width * 0.14, 80, 136);
        var width = (int)Math.Round(Width * scale);
        var height = (int)Math.Round(Height * scale);
        var left = work.Left + Math.Max(0, (work.Width - width) / 2);
        var top = work.Top + Math.Clamp((work.Height - height) / 3, 0, Math.Max(0, work.Height - height));
        NativeMethods.SetWindowPos(_handle, 0, left, top, width, height, NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs args)
    {
        if (SearchPlaceholder is null || _debounce is null) return;
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var empty = string.IsNullOrWhiteSpace(SearchBox.Text);
        if (empty != _queryWasEmpty)
        {
            _queryWasEmpty = empty;
            _filter = empty ? _browseFilter : PanelFilter.All;
            ResetSearchScope();
        }
        CancelSearch();
        _supplemental = [];
        _debounce.Stop();
        if (!_composing) _debounce.Start();
        UpdateFilters();
    }

    private void SuspendForComposition()
    {
        _composing = true;
        _debounce.Stop();
        CancelSearch();
    }

    private void ResumeAfterComposition()
    {
        if (!_composing) return;
        _composing = false;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed || _composing) return;
            _debounce.Stop();
            _debounce.Start();
        }, DispatcherPriority.Input);
    }

    private void CancelSearch()
    {
        ++_searchVersion;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
    }

    private async Task SearchAsync()
    {
        if (_closed || _dismissing || _composing || _snapshot is not { } snapshot) return;
        snapshot = IncludeGroupEntries(snapshot);
        CancelSearch();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _searchCancellation = cancellation;
        var token = cancellation.Token;
        var version = _searchVersion;
        var rowsReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _searchRowsReady = rowsReady.Task;
        var query = SearchBox.Text.Trim();
        var filter = _queryWasEmpty ? PanelFilter.All : _filter;
        var supplemental = _supplemental;
        try
        {
            var results = await Task.Run(() => BuildRows(snapshot, supplemental, query, filter), token);
            if (!IsCurrent(version, token)) return;
            Render(results, preserveSelection: false);
            _renderedSearchVersion = version;
            UpdateFilters();
            rowsReady.TrySetResult();
            if (query.Length == 0 || filter == PanelFilter.Apps) return;
            StatusText.Text = "正在补充本机搜索结果…";
            var indexed = await WindowsIndexSearchClient.SearchAsync(query, token);
            if (!IsCurrent(version, token)) return;
            var extra = indexed.Candidates.Select(candidate => new LauncherPanelEntry(candidate, "Windows 索引")).ToList();
            var available = indexed.Available;
            if (snapshot.EverythingEnabled && _everything is { } everything)
            {
                var found = await everything.SearchAsync(query, token);
                if (!IsCurrent(version, token)) return;
                available |= found.Available;
                extra.AddRange(found.Candidates.Select(candidate => new LauncherPanelEntry(candidate, "Everything")));
            }
            _supplemental = extra;
            results = await Task.Run(() => BuildRows(snapshot, extra, query, filter), token);
            if (!IsCurrent(version, token)) return;
            Render(results, preserveSelection: true);
            if (!available) StatusText.Text = _rows.Count == 0
                ? "没有匹配项目，本机索引暂不可用"
                : $"{_rows.Count} 个项目 · 本机索引暂不可用";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            if (IsCurrent(version, token)) StatusText.Text = "本机搜索暂不可用，可继续使用已有项目";
        }
        finally { rowsReady.TrySetResult(); }
    }

    private bool IsCurrent(int version, CancellationToken token) => !_closed && !token.IsCancellationRequested && version == _searchVersion;

    private static List<PanelRow> BuildRows(LauncherPanelSnapshot snapshot, IReadOnlyList<LauncherPanelEntry> supplemental, string query, PanelFilter filter)
    {
        var favorites = snapshot.Favorites.Select(Identity).ToHashSet(StringComparer.Ordinal);
        var all = snapshot.Favorites.Concat(snapshot.Entries).Concat(supplemental)
            .GroupBy(Identity, StringComparer.Ordinal)
            .Select(group =>
            {
                var primary = group.FirstOrDefault(entry => !entry.IsSavedFallback && entry.Pinned is not null)
                    ?? group.FirstOrDefault(entry => !entry.IsSavedFallback) ?? group.First();
                var sources = primary.IsSavedFallback ? group : group.Where(entry => !entry.IsSavedFallback);
                var folder = group.Select(entry => entry.StartMenuFolder).FirstOrDefault(name => !string.IsNullOrEmpty(name));
                return new PanelRow(primary, favorites.Contains(group.Key),
                    sources.Select(entry => entry.Source).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), folder);
            })
            .Where(row => MatchesFilter(row.Entry, filter)).ToArray();
        if (query.Length == 0)
        {
            return all.OrderByDescending(row => row.IsFavorite)
                .ThenBy(row => row.IsFavorite ? string.Empty : row.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        var byCandidate = all.ToLookup(row => row.Entry.Candidate);
        return TopBarSearchRules.Search(byCandidate.Select(group => group.Key), query, int.MaxValue)
            .SelectMany(candidate => byCandidate[candidate]).ToList();
    }

    private static string Identity(LauncherPanelEntry entry) => entry.Key;

    private static bool MatchesFilter(LauncherPanelEntry entry, PanelFilter filter) => filter == PanelFilter.All
        || (entry.Candidate.Kind == TopBarSearchItemKind.App) == (filter == PanelFilter.Apps);

    private void Render(List<PanelRow> rows, bool preserveSelection)
    {
        var previous = preserveSelection ? (ResultsList.SelectedItem as PanelRow)?.Entry : null;
        _unfilteredRows = rows;
        var scope = ApplyOrganizerFilters(rows);
        var offset = preserveSelection ? ResultViewport?.VerticalOffset ?? 0 : 0;
        if (_queryWasEmpty && _restoreBrowseOffset)
        {
            offset = _browseOffset;
            _restoreBrowseOffset = false;
        }
        var items = BuildDisplayItems();
        _rows = items.OfType<PanelRow>().ToList();
        foreach (var row in _rows)
            if (_iconCache.TryGetValue(IconIdentity(row.Entry), out var icon)) row.Icon = icon;

        ResultsList.ItemsSource = items;
        ResultsList.SelectedItem = (previous is null ? null : _rows.FirstOrDefault(row => row.Entry.Key == previous.Key))
            ?? _rows.FirstOrDefault();
        SectionTitle.Text = scope ?? "";
        SectionTitle.Visibility = scope is null || !_queryWasEmpty ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = _queryWasEmpty && _category is not null ? "右键项目可加入此分组" : "没有匹配项目";
        StatusText.Text = _rows.Count == 0 ? "" : $"{_rows.Count} 个项目";
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed || !ReferenceEquals(ResultsList.ItemsSource, items)) return;
            ResultViewport?.ScrollToVerticalOffset(offset);
            QueueVisibleIcons();
        }, DispatcherPriority.Loaded);
    }

    private async Task LoadIconsAsync(List<PanelRow> rows, int version)
    {
        // Keep shell work bounded when the user changes pages or a path responds slowly.
        // An individual timeout leaves a type glyph and does not cancel the rest of the page.
        foreach (var row in rows)
        {
            if (_closed || _dismissing || version != _iconViewVersion) return;
            var path = row.Entry.Candidate.Path;
            var iconKey = IconIdentity(row.Entry);
            if (_iconCache.ContainsKey(iconKey)) continue;
            try
            {
                if (!await IconGate.WaitAsync(TimeSpan.FromMilliseconds(600), _lifetime.Token)) return;
                if (_closed || _dismissing || version != _iconViewVersion)
                {
                    IconGate.Release();
                    return;
                }
                var work = Task.Run(() =>
                {
                    try
                    {
                        return row.Entry.Pinned is { } pin
                            ? DockAppResolver.Resolve(new DockItem(iconKey, row.Name, pin, null)).Icon
                            : ShellIconProvider.GetIcon(path);
                    }
                    finally { IconGate.Release(); }
                });
                var icon = await work.WaitAsync(TimeSpan.FromMilliseconds(600), _lifetime.Token);
                if (_closed) return;
                _iconCache[iconKey] = icon;
                if (version == _iconViewVersion) row.Icon = icon;
            }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { return; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                or System.Runtime.InteropServices.COMException) { }
        }
    }

    private static string IconIdentity(LauncherPanelEntry entry) =>
        entry.Candidate.Path + '\0' + entry.Pinned?.AppUserModelId + '\0' + entry.Pinned?.IconFile;

    private void UpdateFilters()
    {
        SearchTools.Visibility = _queryWasEmpty ? Visibility.Collapsed : Visibility.Visible;
        BrowseNavigation.Visibility = _queryWasEmpty ? Visibility.Visible : Visibility.Collapsed;
        if (!_queryWasEmpty) SectionTitle.Visibility = Visibility.Collapsed;
        foreach (var (button, filter) in new[] { (AllFilter, PanelFilter.All), (AppsFilter, PanelFilter.Apps), (FilesFilter, PanelFilter.Files) })
        {
            button.SetResourceReference(BackgroundProperty,
                filter == _filter ? "FloatingGlassSelectedBrush" : "FloatingGlassControlBrush");
            button.SetResourceReference(BorderBrushProperty,
                filter == _filter ? "FloatingGlassFocusBrush" : "FloatingGlassControlBorderBrush");
            System.Windows.Automation.AutomationProperties.SetItemStatus(button, filter == _filter ? "已选择" : "");
        }
    }

    private void SelectFilter(PanelFilter filter)
    {
        if (_queryWasEmpty)
        {
            SelectBrowse(filter);
            return;
        }
        _filter = filter;
        UpdateFilters();
        _debounce.Stop();
        _ = SearchAsync();
    }

    private void OnAllClick(object sender, RoutedEventArgs args) => SelectFilter(PanelFilter.All);
    private void OnAppsClick(object sender, RoutedEventArgs args) => SelectFilter(PanelFilter.Apps);
    private void OnFilesClick(object sender, RoutedEventArgs args) => SelectFilter(PanelFilter.Files);
    private void OnCloseClick(object sender, RoutedEventArgs args) => Dismiss(restoreForeground: true);

    private void OnPanelKeyDown(object sender, KeyEventArgs args)
    {
        if (_dismissing) { args.Handled = true; return; }
        if (GroupEditor.Visibility == Visibility.Visible && GroupNameEditor.IsKeyboardFocusWithin)
        {
            if (args.Key == Key.Return && Keyboard.Modifiers == ModifierKeys.None) { SaveNewGroup(); args.Handled = true; }
            if (args.Key == Key.Escape) { EndGroupEditing(); args.Handled = true; }
            return;
        }
        if (SourceFilter.IsKeyboardFocusWithin && (args.Key != Key.Escape || SourceFilter.IsDropDownOpen)) return;
        if (SearchCurrentCategory.IsKeyboardFocusWithin && args.Key == Key.Return)
        {
            SearchCurrentCategory.IsChecked = SearchCurrentCategory.IsChecked != true;
            Render(_unfilteredRows, preserveSelection: false);
            args.Handled = true;
            return;
        }
        if (args.Key == Key.ImeProcessed) { SuspendForComposition(); return; }
        if (_composing) return;
        if (_menu?.IsOpen == true)
        {
            if (args.Key == Key.Escape) { _menu.IsOpen = false; SearchBox.Focus(); args.Handled = true; }
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (args.Key is Key.D1 or Key.NumPad1) { SelectFilter(PanelFilter.Apps); args.Handled = true; return; }
            if (args.Key is Key.D2 or Key.NumPad2) { SelectFilter(PanelFilter.Files); args.Handled = true; return; }
            if (args.Key == Key.Tab) { SelectFilter(_filter == PanelFilter.Files ? PanelFilter.Apps : PanelFilter.Files); args.Handled = true; return; }
        }
        if (Keyboard.FocusedElement is Button && args.Key == Key.Return) return;
        switch (args.Key)
        {
            case Key.Escape:
                Dismiss(restoreForeground: true);
                args.Handled = true;
                break;
            case Key.Return:
                _ = OpenSelectionAsync(Keyboard.Modifiers == ModifierKeys.Control);
                args.Handled = true;
                break;
            case Key.Left when !SearchBox.IsKeyboardFocusWithin:
            case Key.Right when !SearchBox.IsKeyboardFocusWithin:
            case Key.Up:
            case Key.Down:
                if (_rows.Count > 0)
                {
                    SelectNeighbor(args.Key);
                }
                args.Handled = true;
                break;
            case Key.PageUp:
            case Key.PageDown:
                ScrollResultPage(args.Key == Key.PageDown);
                args.Handled = true;
                break;
            case Key.Apps:
            case Key.F10 when Keyboard.Modifiers == ModifierKeys.Shift:
                if (ResultsList.SelectedItem is PanelRow row) OpenMenu(row);
                args.Handled = true;
                break;
        }
    }

    private static ListBoxItem? FindItem(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ListBoxItem item) return item;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private void OnResultMouseUp(object sender, MouseButtonEventArgs args)
    {
        if (FindItem(args.OriginalSource as DependencyObject)?.DataContext is not PanelRow row) return;
        QueueOpen(row.Entry, locate: false);
        args.Handled = true;
    }

    private void OnResultRightClick(object sender, MouseButtonEventArgs args)
    {
        if (FindItem(args.OriginalSource as DependencyObject)?.DataContext is not PanelRow row) return;
        ResultsList.SelectedItem = row;
        OpenMenu(row);
        args.Handled = true;
    }

    private void OpenMenu(PanelRow row)
    {
        if (_closed || _dismissing) return;
        _menu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "打开" };
        open.Click += (_, _) => QueueOpen(row.Entry, locate: false);
        menu.Items.Add(open);
        if (LocalPath(row.Entry) is not null)
        {
            var locate = new MenuItem { Header = "打开所在位置" };
            locate.Click += (_, _) => QueueOpen(row.Entry, locate: true);
            menu.Items.Add(locate);
        }
        if (_pinToDock is not null && (row.Entry.Pinned is not null || LocalPath(row.Entry) is not null))
        {
            var pin = new MenuItem { Header = "固定到 Dock", IsEnabled = !row.IsFavorite, IsChecked = row.IsFavorite };
            pin.Click += async (_, _) => await PinEntryAsync(row.Entry);
            menu.Items.Add(pin);
        }
        AddOrganizerMenu(menu, row);
        AddPurposeMenu(menu, row);
        if (SendPath(row.Entry) is { } path)
        {
            var send = new MenuItem { Header = "发送到分组盒" };
            var boxes = _readFileBoxes();
            if (boxes.Count == 0) send.Items.Add(new MenuItem { Header = "尚无分组盒", IsEnabled = false });
            for (var index = 0; index < boxes.Count; index++)
            {
                var box = boxes[index];
                var target = new MenuItem { Header = box.Title };
                target.Click += async (_, _) =>
                {
                    try
                    {
                        var message = await _sendToFileBox(box.Id, path);
                        if (!_closed) StatusText.Text = message;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        if (!_closed) StatusText.Text = "暂时无法发送到分组盒";
                    }
                };
                send.Items.Add(target);
            }
            menu.Items.Add(send);
        }
        menu.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            menu.IsOpen = false;
            args.Handled = true;
        };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu)) _menu = null;
            Dispatcher.BeginInvoke(() =>
            {
                if (_closed || _dismissing) return;
                if (IsActive || DockNativeMethods.GetForegroundWindow() == _handle)
                {
                    if (GroupEditor.Visibility != Visibility.Visible) SearchBox.Focus();
                    else GroupNameEditor.Focus();
                }
                else CloseOnDeactivation();
            }, DispatcherPriority.ContextIdle);
        };
        ResultsList.ScrollIntoView(row);
        menu.PlacementTarget = ResultsList.ItemContainerGenerator.ContainerFromItem(row) as UIElement ?? ResultsList;
        menu.Placement = PlacementMode.Bottom;
        _menu = menu;
        menu.IsOpen = true;
    }

    private static string? LocalPath(LauncherPanelEntry entry)
    {
        var path = entry.Pinned?.TargetPath ?? entry.Pinned?.ExecutablePath ?? entry.Candidate.Path;
        return !string.IsNullOrWhiteSpace(path) && !FileMappingService.IsShellEntry(path) && Path.IsPathFullyQualified(path) ? path : null;
    }

    private static string? SendPath(LauncherPanelEntry entry)
    {
        var path = LocalPath(entry);
        if (path is null || entry.Pinned is not { TargetPath: null } pin) return path;
        // A file mapping cannot carry app activation metadata or shortcut arguments.
        // Only a real shortcut or an ordinary executable can be represented faithfully.
        if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return path;
        if (!string.IsNullOrWhiteSpace(pin.AppUserModelId) || !string.IsNullOrWhiteSpace(pin.Arguments)) return null;
        var defaultDirectory = Path.GetDirectoryName(path);
        return string.IsNullOrWhiteSpace(pin.WorkingDirectory)
            || string.Equals(pin.WorkingDirectory.TrimEnd('\\', '/'), defaultDirectory?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                ? path : null;
    }

    private async Task OpenSelectionAsync(bool locate)
    {
        if (_closed || _dismissing || _composing) return;
        _debounce.Stop();
        if (_renderedSearchVersion != _searchVersion && _searchCancellation is null) _ = SearchAsync();
        var version = _searchVersion;
        await _searchRowsReady;
        if (!IsActivationCurrent(version) || _renderedSearchVersion != version) return;
        if (ResultsList.SelectedItem is PanelRow selected) QueueOpen(selected.Entry, locate, version);
    }

    private bool IsActivationCurrent(int? version) =>
        !_closed && !_dismissing && (version is null || (!_composing && version == _searchVersion));

    private void QueueOpen(LauncherPanelEntry entry, bool locate, int? searchVersion = null)
    {
        Dispatcher.BeginInvoke(() => _ = OpenAsync(entry, locate, searchVersion), DispatcherPriority.Input);
    }

    private async Task OpenAsync(LauncherPanelEntry entry, bool locate, int? searchVersion = null)
    {
        if (!IsActivationCurrent(searchVersion) || _openingTarget) return;
        var path = LocalPath(entry);
        if (locate && path is null) return;
        _openingTarget = true;
        try
        {
            if (path is not null && (locate || string.IsNullOrWhiteSpace(entry.Pinned?.AppUserModelId))
                && !await CheckLocalTargetAsync(path)) return;
            if (!IsActivationCurrent(searchVersion)) return;
            ClosePanel(restoreForeground: false, animate: false);
            _openTarget(entry, locate);
        }
        catch (OperationCanceledException) { }
        finally { _openingTarget = false; }
    }

    private async Task PinEntryAsync(LauncherPanelEntry entry)
    {
        if (_closed || _dismissing || _pinningTarget || _pinToDock is null) return;
        _pinningTarget = true;
        try
        {
            if (string.IsNullOrWhiteSpace(entry.Pinned?.AppUserModelId) && LocalPath(entry) is { } path
                && !await CheckLocalTargetAsync(path)) return;
            if (_closed || _dismissing) return;
            var message = _pinToDock(entry);
            var snapshot = await _loadSnapshot();
            if (_closed || _dismissing) return;
            _snapshot = snapshot;
            await SearchAsync();
            if (!_closed && !_dismissing) StatusText.Text = message;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            if (!_closed && !_dismissing) StatusText.Text = "暂时无法刷新 Dock 状态，请检查 Dock 后重试";
        }
        finally { _pinningTarget = false; }
    }

    private async Task<bool> CheckLocalTargetAsync(string path)
    {
        StatusText.Text = "正在检查项目…";
        var token = _lifetime.Token;
        try
        {
            if (!await PathCheckGate.WaitAsync(TimeSpan.FromMilliseconds(200), token))
            {
                if (!_closed && !_dismissing) StatusText.Text = "项目响应较慢，请稍后重试";
                return false;
            }
            var check = Task.Run(() =>
            {
                try { return File.Exists(path) || Directory.Exists(path); }
                finally { PathCheckGate.Release(); }
            });
            var exists = await check.WaitAsync(TimeSpan.FromMilliseconds(1200), token);
            if (!exists && !_closed && !_dismissing) StatusText.Text = "文件或应用已不存在，可继续选择其他项目";
            return exists;
        }
        catch (TimeoutException)
        {
            if (!_closed && !_dismissing) StatusText.Text = "项目响应较慢，请检查连接后重试";
            return false;
        }
    }

    private void OnPanelDeactivated(object? sender, EventArgs args)
    {
        Dispatcher.BeginInvoke(CloseOnDeactivation, DispatcherPriority.ContextIdle);
    }

    private void CloseOnDeactivation()
    {
        if (_closed || IsActive || _menu?.IsOpen == true || DockNativeMethods.GetForegroundWindow() == _handle) return;
        DismissedOnDeactivation = true;
        ClosePanel(restoreForeground: false);
    }

    private enum PanelFilter { All, Apps, Files }

    private sealed record CategoryChip(string? Key, string Name, bool IsSelected, LauncherAppCategory? Purpose = null)
    {
        public string SelectionStatus => IsSelected ? "已选择" : "";
    }

    private sealed class PanelRow(LauncherPanelEntry entry, bool isFavorite, IReadOnlyList<string> sources,
        string? startMenuFolder) : INotifyPropertyChanged
    {
        private ImageSource? _icon;
        public LauncherPanelEntry Entry { get; } = entry;
        public bool IsFavorite { get; } = isFavorite;
        public bool IsSection => false;
        public IReadOnlyList<string> Sources { get; } = sources;
        public LauncherAppCategory AutomaticCategory { get; } = entry.Candidate.Kind == TopBarSearchItemKind.App
            ? LauncherAppCategoryRules.Classify(entry.Candidate.DisplayName, entry.Candidate.Path,
                entry.Pinned?.Arguments, startMenuFolder)
            : LauncherAppCategory.Unclassified;
        public string Name => Entry.Candidate.DisplayName;
        public string Glyph => Entry.Candidate.Kind switch { TopBarSearchItemKind.App => "\uE71D", TopBarSearchItemKind.Folder => "\uE8B7", _ => "\uE8A5" };
        public string Location => $"{Name}\n{string.Join("、", Sources)} · {TopBarSearchRules.FormatLocation(Entry.Candidate.Path, 76)}";
        public ImageSource? Icon
        {
            get => _icon;
            set { _icon = value; OnPropertyChanged(); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
