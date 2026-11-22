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

internal sealed record LauncherPanelEntry(
    TopBarSearchCandidate Candidate,
    string Source,
    DockPinnedApp? Pinned = null,
    bool IsSavedFallback = false,
    string? StartMenuFolder = null,
    DateTimeOffset? LastAccessedLocal = null,
    string? IconSource = null)
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
    bool EverythingEnabled,
    IReadOnlyList<LauncherPlace>? Places = null,
    IReadOnlyList<LauncherPanelEntry>? RecentEntries = null);

public partial class LauncherPanelWindow : Window
{
    // Native file checks cannot be cancelled. Limit the outstanding checks across
    // repeated panel sessions; consider a dedicated provider only if this proves insufficient.
    private static readonly SemaphoreSlim PathCheckGate = new(2, 2);
    private static readonly SemaphoreSlim IconGate = new(2, 2);
    private readonly Func<Task<LauncherPanelSnapshot>> _loadSnapshot;
    private readonly Action<LauncherPanelEntry, bool> _openTarget;
    private readonly Func<IReadOnlyList<(Guid Id, string Title)>> _readFileBoxes;
    private readonly Func<Guid, string, Task<string>> _sendToFileBox;
    private readonly Func<LauncherPanelEntry, string>? _pinToDock;
    private readonly Func<LauncherPanelEntry, string>? _unpinFromDock;
    private readonly Func<IList<LauncherGroupState>> _readGroups;
    private readonly Func<IDictionary<string, LauncherAppCategory>>? _readCategoryOverrides;
    private readonly Action? _saveOrganizer;
    private readonly MonitorSnapshot _monitor;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _debounce;
    private readonly DockGlassSurface _glass;
    private readonly LauncherAppearanceState _appearance;
    private readonly LauncherPreferencesState _preferences;
    private readonly DesktopStyleState _style;
    private CancellationTokenSource? _searchCancellation;
    private Task _searchRowsReady = Task.CompletedTask;
    private LauncherPanelSnapshot? _snapshot;
    private EverythingSearchClient? _everything;
    private ContextMenu? _menu;
    private List<LauncherPanelEntry> _supplemental = [];
    private List<PanelRow> _unfilteredRows = [];
    private List<PanelRow> _rows = [];
    private LauncherMainTab _activeTab;
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
        Func<IDictionary<string, LauncherAppCategory>>? readCategoryOverrides = null,
        LauncherPreferencesState? preferences = null,
        Func<LauncherPanelEntry, string>? unpinFromDock = null)
    {
        _loadSnapshot = loadSnapshot;
        _openTarget = openTarget;
        _readFileBoxes = readFileBoxes;
        _sendToFileBox = sendToFileBox;
        _pinToDock = pinToDock;
        _unpinFromDock = unpinFromDock;
        _readGroups = readGroups ?? (() => Array.Empty<LauncherGroupState>());
        _saveOrganizer = saveOrganizer;
        _readCategoryOverrides = readCategoryOverrides;
        PreviousForeground = previousForeground;
        _monitor = monitor;
        _appearance = appearance ?? new LauncherAppearanceState();
        _preferences = preferences ?? new LauncherPreferencesState();
        _preferences.Normalize();
        _activeTab = _preferences.LastTab;
        _filter = _browseFilter = FilterForTab(_activeTab);
        _style = style ?? new DesktopStyleState();
        InitializeComponent();
        _glass = new DockGlassSurface(this, PanelSurface, "FloatingGlassSurfaceBrush",
            "FloatingGlassBorderBrush", "FloatingGlassPanelBrush");
        NewGroupButton.Visibility = saveOrganizer is null ? Visibility.Collapsed : Visibility.Visible;
        ApplyAppearance();
        UpdateMainTabButtons();
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
            CancelDirectoryLoad();
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
        var (glass, readability) = _appearance.ResolveReadableGlass(_style);
        var highContrast = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast;
        FloatingGlassPalette.Apply(Resources, glass, highContrast, launcher: true, darkInk: readability.DarkInk);
        if (_appearance.UseClearGlass || readability.SurfaceOpacity < 0.35d)
        {
            Resources["FloatingGlassNavigationBrush"] = Resources["FloatingGlassLabelBrush"];
            Resources["FloatingGlassHeadingBrush"] = Resources["FloatingGlassLabelBrush"];
        }
        if (_appearance.UseClearGlass && !highContrast)
        {
            var baseColor = (Color)ColorConverter.ConvertFromString(glass.Surface);
            var plateColor = Color.FromArgb((byte)Math.Round(readability.PlateOpacity * 255d),
                baseColor.R, baseColor.G, baseColor.B);
            var plateBrush = new SolidColorBrush(plateColor);
            plateBrush.Freeze();
            Resources["LauncherLabelPlateBrush"] = plateBrush;
        }
        else
        {
            Resources["LauncherLabelPlateBrush"] = Brushes.Transparent;
        }
        _glass.SetClearMode(_appearance.UseClearGlass);
        MaterialButton.Content = _appearance.UseClearGlass ? "清透" : "磨砂";
        MaterialButton.ToolTip = _appearance.UseClearGlass ? "切换为磨砂玻璃" : "切换为清透玻璃";
    }

    internal void RefreshRecentPreference()
    {
        if (_closed || _dismissing) return;
        _ = ReloadSnapshotAfterPreferenceChangeAsync();
    }

    private async Task ReloadSnapshotAfterPreferenceChangeAsync()
    {
        try
        {
            if (_preferences.ShowRecentFiles && (_snapshot?.RecentEntries is null or { Count: 0 }))
            {
                var snapshot = await _loadSnapshot();
                if (_closed || _dismissing) return;
                _snapshot = snapshot;
                await SearchAsync(preserveView: true);
            }
            else if (_snapshot is not null)
            {
                await SearchAsync(preserveView: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
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

    internal void Dismiss(bool restoreForeground = false)
    {
        // Focus and visibility changes leave the input message before ending an IME text store.
        Dispatcher.BeginInvoke(() => ClosePanel(restoreForeground), DispatcherPriority.Input);
    }

    private void ClosePanel(bool restoreForeground)
    {
        if (_closed || _dismissing) return;
        _dismissing = true;
        _debounce.Stop();
        CancelSearch();
        CancelDirectoryLoad();
        IsHitTestVisible = false;
        var ownsForeground = DockNativeMethods.GetForegroundWindow() == _handle;
        Close();
        if (restoreForeground && ownsForeground && PreviousForeground != 0 && PreviousForeground != _handle
            && DockNativeMethods.IsWindow(PreviousForeground))
            _ = DockNativeMethods.SetForegroundWindow(PreviousForeground);
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
                // Keep the panel visible if Windows denies foreground activation so the user
                // can click to focus it or click the launcher trigger again to dismiss it.
            }, DispatcherPriority.Input);
        }
        if (_closed) return;
        StatusText.Text = "正在载入项目…";
        try
        {
            var snapshot = await _loadSnapshot();
            if (_closed) return;
            _snapshot = snapshot;
            if (_composing) StatusText.Text = "";
            else await SearchAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or ArgumentException)
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
        var bounds = LauncherLayoutRules.ComputeBounds(work.Left, work.Top, work.Width, work.Height, current.Dpi);
        Width = bounds.DipWidth;
        Height = bounds.DipHeight;
        BrowseNavigation.Width = bounds.NavigationDipWidth;
        PanelSurface.Padding = bounds.DipWidth < 680
            ? new Thickness(16, 14, 16, 14)
            : new Thickness(26, 20, 26, 18);
        NativeMethods.SetWindowPos(_handle, 0, bounds.PixelLeft, bounds.PixelTop, bounds.PixelWidth, bounds.PixelHeight,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
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
            if (_closed || _composing || _searchCancellation is not null || _renderedSearchVersion == _searchVersion) return;
            _debounce.Stop();
            if (_snapshot is not null && _renderedSearchVersion < 0) _ = SearchAsync();
            else _debounce.Start();
        }, DispatcherPriority.Input);
    }

    private void CancelSearch()
    {
        ++_searchVersion;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
    }

    private async Task SearchAsync(bool preserveView = false)
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
        var includeRecent = _preferences.ShowRecentFiles;
        try
        {
            var results = await Task.Run(() => BuildRows(snapshot, supplemental, query, filter, includeRecent), token);
            if (!IsCurrent(version, token)) return;
            Render(results, preserveSelection: preserveView);
            _renderedSearchVersion = version;
            UpdateFilters();
            rowsReady.TrySetResult();
            if (query.Length == 0 || filter == PanelFilter.Apps) return;
            StatusText.Text = "正在补充本机搜索结果…";
            var indexed = await WindowsIndexSearchClient.SearchAsync(query, token);
            if (!IsCurrent(version, token)) return;
            var raw = indexed.Candidates.Select(candidate => new LauncherPanelEntry(candidate, "Windows 索引")).ToList();
            var available = indexed.Available;
            if (snapshot.EverythingEnabled && _everything is { } everything)
            {
                var found = await everything.SearchAsync(query, token);
                if (!IsCurrent(version, token)) return;
                available |= found.Available;
                raw.AddRange(found.Candidates.Select(candidate => new LauncherPanelEntry(candidate, "Everything")));
            }
            var extra = await Task.Run(() => LauncherSupplementalNormalizer.Normalize(raw), token);
            if (!IsCurrent(version, token)) return;
            _supplemental = extra;
            results = await Task.Run(() => BuildRows(snapshot, extra, query, filter, includeRecent), token);
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

    private static List<PanelRow> BuildRows(
        LauncherPanelSnapshot snapshot,
        IReadOnlyList<LauncherPanelEntry> supplemental,
        string query,
        PanelFilter filter,
        bool includeRecent)
    {
        var favoriteKeys = snapshot.Favorites.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        var dockKeys = snapshot.Favorites
            .Where(entry => entry.Pinned is not null)
            .Select(entry => DockPinRules.IdentityKey(entry.Pinned!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recent = includeRecent ? snapshot.RecentEntries ?? [] : [];
        var all = LauncherIdentityRules.Group(
                snapshot.Favorites.Concat(snapshot.Entries).Concat(recent).Concat(supplemental),
                IdentityOf)
            .Select(group =>
            {
                var primary = group.FirstOrDefault(entry => !entry.IsSavedFallback && entry.Pinned is not null)
                    ?? group.FirstOrDefault(entry => !entry.IsSavedFallback && entry.Source != "最近使用")
                    ?? group.FirstOrDefault(entry => !entry.IsSavedFallback)
                    ?? group[0];
                var sources = primary.IsSavedFallback ? group : group.Where(entry => !entry.IsSavedFallback);
                var folder = group.Select(entry => entry.StartMenuFolder).FirstOrDefault(name => !string.IsNullOrEmpty(name));
                var lastAccessed = group.Select(entry => entry.LastAccessedLocal).Where(t => t.HasValue).Max();
                var merged = lastAccessed.HasValue && primary.LastAccessedLocal is null
                    ? primary with { LastAccessedLocal = lastAccessed } : primary;
                var preferredName = LauncherIdentityRules.PreferDisplayName(
                    merged.Candidate.DisplayName,
                    merged.Pinned?.ExecutablePath,
                    group.Where(entry => entry.IconSource is not null).Select(entry => entry.Candidate.DisplayName));
                if (!string.Equals(preferredName, merged.Candidate.DisplayName, StringComparison.Ordinal))
                {
                    merged = merged with { Candidate = merged.Candidate with { DisplayName = preferredName } };
                }
                if (merged.IconSource is null
                    && string.IsNullOrWhiteSpace(merged.Pinned?.IconFile)
                    && string.IsNullOrWhiteSpace(merged.Pinned?.AppUserModelId))
                {
                    var shortcutIcon = group.Select(entry => entry.IconSource)
                        .FirstOrDefault(path => !string.IsNullOrEmpty(path));
                    if (shortcutIcon is not null)
                    {
                        merged = merged with { IconSource = shortcutIcon };
                    }
                }
                var isFavorite = group.Any(entry => favoriteKeys.Contains(entry.Key)
                    || (entry.Pinned is { } pin && dockKeys.Contains(DockPinRules.IdentityKey(pin))));
                var keys = group.Select(entry => entry.Key).Prepend(primary.Key).Distinct(StringComparer.Ordinal).ToArray();
                return new PanelRow(merged, isFavorite,
                    sources.Select(entry => entry.Source).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    folder, keys);
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

    internal static LauncherIdentityInput IdentityOf(LauncherPanelEntry entry) =>
        new(entry.Pinned?.ExecutablePath, entry.Pinned?.AppUserModelId, entry.Pinned?.TargetPath,
            entry.Candidate.Path, entry.Pinned?.Arguments, entry.Candidate.DisplayName);

    private static bool MatchesFilter(LauncherPanelEntry entry, PanelFilter filter) => filter == PanelFilter.All
        || (entry.Candidate.Kind == TopBarSearchItemKind.App) == (filter == PanelFilter.Apps);

    private static PanelFilter FilterForTab(LauncherMainTab tab) => tab switch
    {
        LauncherMainTab.Apps => PanelFilter.Apps,
        LauncherMainTab.Files => PanelFilter.Files,
        _ => PanelFilter.All
    };

    private void Render(List<PanelRow> rows, bool preserveSelection)
    {
        var previous = preserveSelection ? (ResultsList.SelectedItem as PanelRow)?.Entry : null;
        if (!preserveSelection) ResultsList.Tag = !_queryWasEmpty;
        _unfilteredRows = rows;
        ComputeEffectiveAppCategories(rows);
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
        {
            if (LauncherIconCache.TryGet(IconIdentity(row.Entry), out var icon)) row.Icon = icon;
        }

        UpdateResultsLayoutMode();
        ResultsList.ItemsSource = items;
        ResultsList.SelectedItem = (previous is null ? null : _rows.FirstOrDefault(row => row.Entry.Key == previous.Key))
            ?? _rows.FirstOrDefault();
        UpdateContextHeader(scope);
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = ResolveEmptyHint();
        StatusText.Text = ResolveStatusText();
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed || !ReferenceEquals(ResultsList.ItemsSource, items)) return;
            ResultViewport?.ScrollToVerticalOffset(offset);
            QueueVisibleIcons();
        }, DispatcherPriority.Loaded);
    }

    private void UpdateResultsLayoutMode()
    {
        var listMode = IsListViewActive();
        ResultsList.ItemTemplate = (DataTemplate)Resources[listMode ? "ListItemTemplate" : "TileItemTemplate"];
        if (FindGrid(ResultsList) is { } grid && grid.IsListMode != listMode)
        {
            grid.IsListMode = listMode;
        }
    }

    private async Task LoadIconsAsync(List<PanelRow> rows, int version)
    {
        // Keep shell work bounded when the user changes pages or a path responds slowly.
        // A gate or extraction timeout skips waiting on that item, lets the remaining items
        // load, and writes late completions back through the process-wide icon cache.
        foreach (var row in rows)
        {
            if (_closed || _dismissing || version != _iconViewVersion) return;
            var iconKey = IconIdentity(row.Entry);
            if (LauncherIconCache.TryGet(iconKey, out var cached))
            {
                row.Icon = cached;
                continue;
            }
            if (!LauncherIconCache.TryBeginLoad(iconKey))
            {
                if (LauncherIconCache.TryGet(iconKey, out cached))
                {
                    row.Icon = cached;
                }
                continue;
            }
            try
            {
                if (!await IconGate.WaitAsync(TimeSpan.FromMilliseconds(600), _lifetime.Token))
                {
                    LauncherIconCache.Complete(iconKey, null);
                    continue;
                }
                if (_closed || _dismissing || version != _iconViewVersion)
                {
                    IconGate.Release();
                    LauncherIconCache.Complete(iconKey, null);
                    return;
                }
                var entry = row.Entry;
                var name = row.Name;
                var work = Task.Run(() =>
                {
                    try
                    {
                        return ExtractIcon(entry, iconKey, name);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                        or System.Runtime.InteropServices.COMException)
                    {
                        return null;
                    }
                    finally { IconGate.Release(); }
                });
                _ = work.ContinueWith(completed =>
                {
                    var resolved = completed.IsCompletedSuccessfully ? completed.Result : null;
                    LauncherIconCache.Complete(iconKey, resolved);
                    if (resolved is not null && !_closed)
                    {
                        Dispatcher.BeginInvoke(() => ApplyIcon(iconKey, resolved));
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

                var icon = await work.WaitAsync(TimeSpan.FromMilliseconds(600), _lifetime.Token);
                if (_closed) return;
                if (icon is not null)
                {
                    ApplyIcon(iconKey, icon);
                }
            }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { return; }
        }
    }

    private void ApplyIcon(string iconKey, ImageSource? icon)
    {
        if (_closed || icon is null) return;
        foreach (var row in _rows)
        {
            if (string.Equals(IconIdentity(row.Entry), iconKey, StringComparison.OrdinalIgnoreCase))
            {
                row.Icon = icon;
            }
        }
    }

    private static ImageSource? ExtractIcon(LauncherPanelEntry entry, string iconKey, string name)
    {
        ImageSource? icon;
        if (entry.Pinned is { } customPin
            && (!string.IsNullOrWhiteSpace(customPin.IconFile) || !string.IsNullOrWhiteSpace(customPin.AppUserModelId)))
        {
            icon = DockAppResolver.Resolve(new DockItem(iconKey, name, customPin, null)).Icon;
        }
        else if (!string.IsNullOrWhiteSpace(entry.IconSource))
        {
            icon = ShellIconProvider.GetIcon(entry.IconSource)
                ?? (entry.Pinned is { } pin
                    ? DockAppResolver.Resolve(new DockItem(iconKey, name, pin, null)).Icon
                    : ShellIconProvider.GetIcon(entry.Candidate.Path));
        }
        else if (entry.Pinned is { } pin)
        {
            icon = DockAppResolver.Resolve(new DockItem(iconKey, name, pin, null)).Icon;
        }
        else
        {
            icon = ShellIconProvider.GetIcon(entry.Candidate.Path);
        }

        if (icon is Freezable { IsFrozen: false, CanFreeze: true } freezable)
        {
            freezable.Freeze();
        }
        return icon;
    }

    private static string IconIdentity(LauncherPanelEntry entry) =>
        (entry.IconSource ?? entry.Candidate.Path) + '\0' + entry.Pinned?.AppUserModelId + '\0' + entry.Pinned?.IconFile;

    private void UpdateFilters()
    {
        SearchTools.Visibility = _queryWasEmpty ? Visibility.Collapsed : Visibility.Visible;
        BrowseNavigation.Visibility = _queryWasEmpty ? Visibility.Visible : Visibility.Collapsed;
        ContentHeaderBar.Visibility = _queryWasEmpty ? Visibility.Visible : Visibility.Collapsed;
        UpdateMainTabButtons();
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
            if (filter == PanelFilter.Files) SwitchMainTab(LauncherMainTab.Files, persist: false);
            else if (filter == PanelFilter.Apps) SwitchMainTab(LauncherMainTab.Apps, persist: false);
            else SelectBrowse(filter);
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
        if (Keyboard.Modifiers == ModifierKeys.Alt && args.SystemKey == Key.Left && _queryWasEmpty
            && _activeTab == LauncherMainTab.Files && _folderStack.Count > 0)
        {
            NavigateBackFolder();
            args.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (args.Key is Key.D1 or Key.NumPad1) { SwitchMainTab(LauncherMainTab.Apps); args.Handled = true; return; }
            if (args.Key is Key.D2 or Key.NumPad2) { SwitchMainTab(LauncherMainTab.Files); args.Handled = true; return; }
            if (args.Key is Key.D3 or Key.NumPad3) { SwitchMainTab(LauncherMainTab.Groups); args.Handled = true; return; }
            if (args.Key == Key.Tab)
            {
                var next = _activeTab switch
                {
                    LauncherMainTab.Apps => LauncherMainTab.Files,
                    LauncherMainTab.Files => LauncherMainTab.Groups,
                    _ => LauncherMainTab.Apps
                };
                SwitchMainTab(next);
                args.Handled = true;
                return;
            }
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

    private static bool IsInsideScrollBar(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ScrollBar) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void OnResultMouseUp(object sender, MouseButtonEventArgs args)
    {
        if (FindItem(args.OriginalSource as DependencyObject)?.DataContext is not PanelRow row)
        {
            if (_queryWasEmpty && !IsInsideScrollBar(args.OriginalSource as DependencyObject)) ResultsList.Tag = false;
            return;
        }
        ActivateEntry(row.Entry, locate: false);
        args.Handled = true;
    }

    private void OnResultRightClick(object sender, MouseButtonEventArgs args)
    {
        if (FindItem(args.OriginalSource as DependencyObject)?.DataContext is not PanelRow row)
        {
            if (_queryWasEmpty) ResultsList.Tag = false;
            return;
        }
        ResultsList.Tag = true;
        ResultsList.SelectedItem = row;
        OpenMenu(row);
        args.Handled = true;
    }

    private void OpenMenu(PanelRow row)
    {
        if (_closed || _dismissing) return;
        _menu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        var menu = new ContextMenu();
        if (CanDrillDown(row.Entry))
        {
            var expand = new MenuItem { Header = "在面板内展开" };
            expand.Click += (_, _) => _ = DrillIntoDirectoryAsync(LocalPath(row.Entry)!, row.Name);
            menu.Items.Add(expand);

            var openExplorer = new MenuItem { Header = "在资源管理器中打开" };
            openExplorer.Click += (_, _) => QueueOpen(row.Entry, locate: false);
            menu.Items.Add(openExplorer);
        }
        else
        {
            var open = new MenuItem { Header = "打开" };
            open.Click += (_, _) => QueueOpen(row.Entry, locate: false);
            menu.Items.Add(open);
        }
        if (LocalPath(row.Entry) is not null)
        {
            var locate = new MenuItem { Header = "打开所在位置" };
            locate.Click += (_, _) => QueueOpen(row.Entry, locate: true);
            menu.Items.Add(locate);
        }
        if ((row.IsFavorite ? _unpinFromDock is not null || _pinToDock is not null : _pinToDock is not null)
            && (row.Entry.Pinned is not null || LocalPath(row.Entry) is not null))
        {
            if (row.IsFavorite && _unpinFromDock is not null)
            {
                var unpin = new MenuItem { Header = "从 Dock 取消固定" };
                unpin.Click += async (_, _) => await ChangeDockPinAsync(row.Entry, pin: false);
                menu.Items.Add(unpin);
            }
            else
            {
                var pin = new MenuItem { Header = "固定到 Dock", IsEnabled = !row.IsFavorite, IsChecked = row.IsFavorite };
                pin.Click += async (_, _) => await ChangeDockPinAsync(row.Entry, pin: true);
                menu.Items.Add(pin);
            }
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

    private static bool CanDrillDown(LauncherPanelEntry entry) =>
        entry.Candidate.Kind == TopBarSearchItemKind.Folder
        && LocalPath(entry) is { Length: > 0 } path
        && !path.StartsWith(@"\\", StringComparison.Ordinal);

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

    private void ActivateEntry(LauncherPanelEntry entry, bool locate, int? searchVersion = null)
    {
        if (!locate && _queryWasEmpty && _activeTab == LauncherMainTab.Files && CanDrillDown(entry))
        {
            _ = DrillIntoDirectoryAsync(LocalPath(entry)!, entry.Candidate.DisplayName);
            return;
        }
        QueueOpen(entry, locate, searchVersion);
    }

    private async Task OpenSelectionAsync(bool locate)
    {
        if (_closed || _dismissing || _composing || _snapshot is null) return;
        _debounce.Stop();
        var version = _searchVersion;
        if (_renderedSearchVersion != version && _searchCancellation is null)
        {
            _ = SearchAsync();
            version = _searchVersion;
            if (_renderedSearchVersion != version && _searchCancellation is null) return;
        }
        await _searchRowsReady;
        if (!IsActivationCurrent(version) || _renderedSearchVersion != version) return;
        if (ResultsList.SelectedItem is PanelRow selected) ActivateEntry(selected.Entry, locate, version);
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
            ClosePanel(restoreForeground: false);
            _openTarget(entry, locate);
        }
        catch (OperationCanceledException) { }
        finally { _openingTarget = false; }
    }

    private async Task ChangeDockPinAsync(LauncherPanelEntry entry, bool pin)
    {
        var action = pin ? _pinToDock : _unpinFromDock;
        if (_closed || _dismissing || _pinningTarget || action is null) return;
        _pinningTarget = true;
        var updated = false;
        try
        {
            if (pin && string.IsNullOrWhiteSpace(entry.Pinned?.AppUserModelId) && LocalPath(entry) is { } path
                && !await CheckLocalTargetAsync(path)) return;
            if (_closed || _dismissing) return;
            var message = action(entry);
            updated = true;
            var snapshot = await _loadSnapshot();
            if (_closed || _dismissing) return;
            _snapshot = snapshot;
            await SearchAsync(preserveView: true);
            if (!_closed && !_dismissing) StatusText.Text = message;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            if (!_closed && !_dismissing)
            {
                StatusText.Text = updated
                    ? "已更新 Dock，但暂时无法刷新列表"
                    : "暂时无法更新 Dock，请稍后重试";
            }
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

    private sealed record CategoryChip(
        string? Key,
        string Name,
        bool IsSelected,
        LauncherAppCategory? Purpose = null,
        string Glyph = "\uE71D",
        string CountText = "",
        LauncherFileCategory? FileKind = null,
        string? PlaceId = null)
    {
        public string SelectionStatus => IsSelected ? "已选择" : "";
    }

    private sealed class PanelRow : INotifyPropertyChanged
    {
        private ImageSource? _icon;
        public PanelRow(LauncherPanelEntry entry, bool isFavorite, IReadOnlyList<string> sources,
            string? startMenuFolder, IReadOnlyList<string>? keys = null)
        {
            Entry = entry;
            IsFavorite = isFavorite;
            Sources = sources;
            StartMenuFolder = startMenuFolder;
            Keys = keys is { Count: > 0 } ? keys : [entry.Key];
            AutomaticCategory = entry.Candidate.Kind == TopBarSearchItemKind.App
                ? LauncherAppCategoryRules.Classify(entry.Candidate.DisplayName, entry.Candidate.Path,
                    entry.Pinned?.Arguments, startMenuFolder)
                : LauncherAppCategory.Unclassified;
            EffectiveCategory = AutomaticCategory;
            FileCategory = entry.Candidate.Kind == TopBarSearchItemKind.App
                ? LauncherFileCategory.Other
                : LauncherFileCategoryRules.Classify(entry.Candidate.Path, entry.Candidate.Kind == TopBarSearchItemKind.Folder);
        }

        public LauncherPanelEntry Entry { get; }
        public bool IsFavorite { get; }
        public bool IsSection => false;
        public IReadOnlyList<string> Sources { get; }
        public string? StartMenuFolder { get; }
        public IReadOnlyList<string> Keys { get; }
        public LauncherAppCategory AutomaticCategory { get; }
        public LauncherAppCategory EffectiveCategory { get; set; }
        public LauncherFileCategory FileCategory { get; }
        public string Name => Entry.Candidate.DisplayName;
        public string Glyph => Entry.Candidate.Kind == TopBarSearchItemKind.App
            ? "\uE71D" : LauncherFileCategoryRules.Glyph(FileCategory);
        public string Location => $"{Name}\n{string.Join("、", Sources)} · {TopBarSearchRules.FormatLocation(Entry.Candidate.Path, 76)}";

        public string SecondaryText
        {
            get
            {
                var sourceText = string.Join("、", Sources);
                if (Entry.LastAccessedLocal is { } time)
                {
                    return $"{sourceText} · {LauncherFileCategoryRules.FormatRecentTime(time, DateTime.Today)} · {TopBarSearchRules.FormatLocation(Entry.Candidate.Path, 46)}";
                }
                return $"{sourceText} · {TopBarSearchRules.FormatLocation(Entry.Candidate.Path, 56)}";
            }
        }

        public string KindLabel => Entry.Candidate.Kind switch
        {
            TopBarSearchItemKind.App => LauncherAppCategoryRules.DisplayName(EffectiveCategory),
            TopBarSearchItemKind.Folder when CanDrillDown(Entry) => "展开目录 ›",
            _ => LauncherFileCategoryRules.DisplayName(FileCategory)
        };

        public ImageSource? Icon
        {
            get => _icon;
            set { _icon = value; OnPropertyChanged(); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
