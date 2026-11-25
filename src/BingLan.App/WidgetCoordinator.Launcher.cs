using System.Diagnostics;
using System.IO;
using System.Windows.Interop;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.App.Windows;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.TopBar;

namespace BingLan.App;

public sealed partial class WidgetCoordinator
{
    private static readonly TimeSpan LauncherToggleDismissDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan LauncherAppCacheLifetime = TimeSpan.FromMinutes(10);
    private LauncherPanelWindow? _launcherPanel;
    private long? _launcherLostFocusAt;
    private Task<(DockPinnedApp Pin, string? Folder, string? Shortcut)[]>? _launcherApps;
    private Task<(DockPinnedApp Pin, string? Folder, string? Shortcut)[]>? _launcherAppsRefresh;
    private DateTimeOffset _launcherAppsAt;

    internal void ToggleLauncherPanel() => ToggleLauncherPanelForTab(targetTab: null);

    internal void ToggleLauncherPanelForTab(LauncherMainTab? targetTab)
    {
        if (_isExiting || _exitRequested || !_state.LauncherEnabled || _gameModeActive)
        {
            return;
        }
        var foreground = DockNativeMethods.GetForegroundWindow();
        if (FullScreenWatcher.IsFullScreen(foreground, excludeMaximized: true)
            || FullScreenWatcher.IsTrueFullScreenContext(foreground))
        {
            return;
        }
        if (_launcherPanel is { } open)
        {
            if (targetTab == LauncherMainTab.Files && !open.IsAtFilesCommonPlaces)
            {
                open.NavigateToFilesCommonPlaces();
                return;
            }
            open.Dismiss(restoreForeground: true);
            return;
        }
        if (_launcherLostFocusAt is { } dismissedAt
            && Environment.TickCount64 - dismissedAt < LauncherToggleDismissDelay.TotalMilliseconds)
        {
            _launcherLostFocusAt = null;
            return;
        }

        var monitors = MonitorCatalog.GetAll();
        var monitor = DockNativeMethods.GetCursorPos(out var cursor)
            ? monitors.FirstOrDefault(screen => cursor.X >= screen.Bounds.Left && cursor.X < screen.Bounds.Right
                && cursor.Y >= screen.Bounds.Top && cursor.Y < screen.Bounds.Bottom)
            : null;
        monitor ??= monitors.FirstOrDefault();
        if (monitor is null)
        {
            return;
        }

        var minimizedWindows = _state.LauncherPreferences.BackdropMode == LauncherBackdropMode.ShowDesktop
            ? LauncherDesktopBackdropService.MinimizeOtherWindows()
            : Array.Empty<nint>();

        var panel = new LauncherPanelWindow(
            ReadLauncherSnapshotAsync, OpenLauncherTarget, ReadLauncherFileBoxes,
            SendLauncherPathToFileBoxAsync, foreground, monitor, _state.LauncherAppearance, _state.Style,
            PinLauncherEntry, () => _state.LauncherGroups, ScheduleSave, () => _state.LauncherAppCategoryOverrides,
            _state.LauncherPreferences, unpinFromDock: UnpinLauncherEntry, initialTab: targetTab);
        _launcherPanel = panel;
        panel.Closed += (_, _) =>
        {
            if (_launcherPanel != panel)
            {
                return;
            }
            _launcherPanel = null;
            _launcherLostFocusAt = panel.DismissedOnDeactivation ? Environment.TickCount64 : null;
            if (minimizedWindows.Count > 0 && !panel.LaunchedTarget)
            {
                LauncherDesktopBackdropService.RestoreWindows(minimizedWindows, foreground);
            }
            LauncherDesktopBackdropService.ScheduleReleaseWallpaperCache();
            ScheduleIdleMemoryTrim(WindowClosedMemoryTrimDelay);
        };
        panel.Show();
    }

    private void SetLauncherEnabled(bool enabled)
    {
        _state.LauncherEnabled = enabled;
        if (!enabled)
        {
            _launcherPanel?.Dismiss(restoreForeground: false);
            LauncherDesktopBackdropService.ReleaseWallpaperCache();
        }
        _launcherLostFocusAt = null;
        if (_launcherMenuItem is not null)
        {
            _launcherMenuItem.Visible = enabled && !_gameModeActive;
        }
        _dock.Apply();
        _topBar.Apply();
        SyncGlobalHotkeys();
        ScheduleSave();
    }

    private Task<(DockPinnedApp Pin, string? Folder, string? Shortcut)[]> ReadLauncherAppsAsync()
    {
        var now = DateTimeOffset.Now;
        if (_launcherApps is null || _launcherApps.IsFaulted || _launcherApps.IsCanceled)
        {
            _launcherAppsAt = now;
            _launcherApps = ScanLauncherAppsAsync();
            return _launcherApps;
        }

        if (_launcherApps.IsCompletedSuccessfully
            && now - _launcherAppsAt >= LauncherAppCacheLifetime
            && _launcherAppsRefresh is null)
        {
            var refresh = ScanLauncherAppsAsync();
            _launcherAppsRefresh = refresh;
            var scheduler = SynchronizationContext.Current is not null
                ? TaskScheduler.FromCurrentSynchronizationContext()
                : TaskScheduler.Default;
            _ = refresh.ContinueWith(task =>
            {
                if (ReferenceEquals(_launcherAppsRefresh, refresh))
                {
                    _launcherAppsRefresh = null;
                }
                if (task.IsCompletedSuccessfully && !_isExiting && !_exitRequested)
                {
                    _launcherApps = refresh;
                    _launcherAppsAt = DateTimeOffset.Now;
                }
            }, CancellationToken.None, TaskContinuationOptions.None, scheduler);
        }

        return _launcherApps;
    }

    private static Task<(DockPinnedApp Pin, string? Folder, string? Shortcut)[]> ScanLauncherAppsAsync() =>
        Task.Run(() =>
        {
            var folders = new Dictionary<DockPinnedApp, string>();
            var shortcuts = new Dictionary<DockPinnedApp, string>();
            return DockShortcuts.ReadStartMenuApps(preserveLaunchVariants: true, folders, shortcutPaths: shortcuts)
                .Select(pin => (Pin: pin, Folder: folders.GetValueOrDefault(pin), Shortcut: shortcuts.GetValueOrDefault(pin)))
                .ToArray();
        });

    private nint ResolveLauncherForeground(nint current)
    {
        if (_launcherPanel is { } panel)
        {
            var panelHandle = new WindowInteropHelper(panel).Handle;
            var borrowed = DockNativeMethods.IsWindow(panel.PreviousForeground) ? panel.PreviousForeground : 0;
            return DockForegroundRules.EffectiveForeground(current, panelHandle, borrowed);
        }
        return current;
    }

    private IReadOnlyList<(Guid Id, string Title)> ReadLauncherFileBoxes()
    {
        var boxes = _state.FileBoxes;
        var sameNames = boxes.GroupBy(box => box.Title, StringComparer.CurrentCultureIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var labelCounts = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        return boxes.Select(box =>
        {
            if (!sameNames.Contains(box.Title))
            {
                return (box.Id, box.Title);
            }
            var index = labelCounts.GetValueOrDefault(box.Title) + 1;
            labelCounts[box.Title] = index;
            return (box.Id, $"{box.Title}（{index}）");
        }).ToArray();
    }

    private async Task<string> SendLauncherPathToFileBoxAsync(Guid id, string path)
    {
        var box = FindFileBoxWindow(id);
        if (box is null || !_state.FileBoxes.Any(item => item.Id == id))
        {
            return "该文件收纳盒已移除。";
        }

        var status = await box.AddMappingsWithStatusToBoxAsync(id, [path]);
        return _state.FileBoxes.Any(item => item.Id == id) ? status : "该文件收纳盒已移除。";
    }

    private async void OpenLauncherTarget(LauncherPanelEntry entry, bool locate)
    {
        try
        {
            var candidate = entry.Candidate;
            var failure = locate
                ? await Task.Run(() => WindowCommandService.OpenFileLocation(candidate.Path))
                : entry.Pinned is { } pin
                    ? await WindowCommandService.LaunchAsync(pin)
                    : new WindowCommandResult(
                        await ShellLauncher.StartAsync(new ProcessStartInfo(candidate.Path) { UseShellExecute = true }) is null,
                        $"无法打开 {candidate.DisplayName}。");
            if (!failure.Succeeded && !_isExiting)
            {
                _trayIcon.ShowBalloonTip(3000, "冰蓝总览卡", failure.Message, TrayTipKind.Info);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StartupLog.WriteCrash("WidgetCoordinator.OpenLauncherTarget", exception);
            if (!_isExiting)
            {
                _trayIcon.ShowBalloonTip(3000, "冰蓝总览卡", "打开目标时发生异常。", TrayTipKind.Warning);
            }
        }
    }

    private string PinLauncherEntry(LauncherPanelEntry entry)
    {
        var pin = entry.Pinned is { } source
            ? DockPinRules.Copy(source)
            : DockPinRules.CreateFromPath(entry.Candidate.Path, entry.Candidate.DisplayName);
        if (pin is null) return "此入口无法固定到 Dock";
        if (!DockPinRules.Pin(_state.Dock, pin))
        {
            return entry.Pinned is { ExecutablePath: not null }
                ? "Dock 已固定此程序的入口" : "此入口已固定到 Dock";
        }
        ApplyDock();
        return "已固定到 Dock";
    }

    private string UnpinLauncherEntry(LauncherPanelEntry entry)
    {
        var pin = entry.Pinned ?? DockPinRules.CreateFromPath(entry.Candidate.Path, entry.Candidate.DisplayName);
        if (pin is null || !DockPinRules.Unpin(_state.Dock, DockPinRules.IdentityKey(pin)))
        {
            return "此入口不在 Dock 中";
        }
        ApplyDock();
        ScheduleSave();
        return "已从 Dock 取消固定";
    }

    private async Task<LauncherPanelSnapshot> ReadLauncherSnapshotAsync()
    {
        var pins = _state.Dock.PinnedApps.Select(DockPinRules.Copy).ToArray();
        var mapped = _state.FileBoxes.SelectMany(box => box.Items.Select(item =>
            (item.Path, item.Title, Source: box.Title))).ToArray();
        var fileBoxSummaries = _state.FileBoxes.Select(box => (
            box.Id,
            box.Title,
            Items: (IReadOnlyList<(string Path, string? Title)>)box.Items.Select(item => (item.Path, (string?)item.Title)).ToArray())).ToArray();
        var quickPlaces = _state.QuickPlaces.Select(place => (place.Target, place.Name)).ToArray();
        var savedEntries = _state.LauncherGroups.SelectMany(group => group.Members)
            .Where(member => member.FallbackPath is not null || member.FallbackPin is not null)
            .DistinctBy(member => member.Key).ToArray();
        var useEverything = _state.LauncherPreferences.UseEverything;
        var showRecentFiles = _state.LauncherPreferences.ShowRecentFiles;
        var pinnedFolders = _state.LauncherPreferences.PinnedFolders.ToArray();
        var apps = await ReadLauncherAppsAsync();
        return await Task.Run(() =>
        {
            var entries = new List<LauncherPanelEntry>();
            var favorites = new List<LauncherPanelEntry>();
            foreach (var pin in pins)
            {
                AddPin(pin, "Dock", favorites);
            }
            foreach (var (app, folder, shortcut) in apps)
            {
                AddPin(app, "开始菜单", startMenuFolder: folder, iconSource: shortcut);
            }
            foreach (var (path, title, source) in mapped)
            {
                AddPath(path, title, source);
            }
            foreach (var (target, name) in quickPlaces)
            {
                if (QuickPlacesWindow.Resolve(target) is { Length: > 0 } path)
                {
                    AddPath(path, name, "快捷入口");
                }
            }
            foreach (var path in CollectImportableDesktopEntries())
            {
                AddPath(path, null, "桌面");
            }
            var indexed = entries.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var saved in savedEntries)
            {
                if (indexed.Contains(saved.Key)) continue;
                if (saved.FallbackPin is { } pin)
                    AddPin(pin, "已保存入口", isSavedFallback: true);
                else if (saved.FallbackPath is { } path && (Path.IsPathFullyQualified(path) || FileMappingService.IsShellEntry(path)))
                    Add(path, saved.FallbackName, "已保存入口", ClassifyLauncherPath(path), isSavedFallback: true);
            }
            var places = LauncherFileCatalog.BuildPlaces(fileBoxSummaries, pinnedFolders);
            var recent = showRecentFiles ? LauncherFileCatalog.ReadRecentFiles() : [];
            return new LauncherPanelSnapshot(entries, favorites, useEverything, places, recent);

            void AddPath(string path, string? name, string source)
            {
                if (!Path.IsPathFullyQualified(path) && !FileMappingService.IsShellEntry(path))
                {
                    return;
                }
                if (Path.IsPathFullyQualified(path) && FileMappingService.IsLauncherName(Path.GetFileName(path))
                    && DockShortcuts.CreatePin(path) is { } pin)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        pin.DisplayName = name;
                    }
                    var iconSource = Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? path : null;
                    AddPin(pin, source, iconSource: iconSource);
                    return;
                }
                Add(path, name, source, ClassifyLauncherPath(path));
            }

            void AddPin(DockPinnedApp pin, string source, List<LauncherPanelEntry>? favoriteList = null,
                bool isSavedFallback = false, string? startMenuFolder = null, string? iconSource = null)
            {
                var path = pin.TargetPath ?? pin.ExecutablePath
                    ?? (pin.AppUserModelId is { } id ? DockAppResolver.AppsFolderPath(id) : null);
                if (path is not null)
                {
                    var kind = pin.TargetPath is null ? TopBarSearchItemKind.App : ClassifyLauncherPath(path);
                    Add(path, pin.DisplayName, source, kind, pin, favoriteList, isSavedFallback, startMenuFolder, iconSource);
                }
            }

            void Add(string path, string? name, string source, TopBarSearchItemKind kind,
                DockPinnedApp? pin = null, List<LauncherPanelEntry>? favoriteList = null, bool isSavedFallback = false,
                string? startMenuFolder = null, string? iconSource = null)
            {
                var fullName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                var displayName = string.IsNullOrWhiteSpace(name) ? FileMappingService.GetDisplayName(path) : name;
                var entry = new LauncherPanelEntry(
                    new TopBarSearchCandidate(kind, displayName, fullName, path), source, pin, isSavedFallback, startMenuFolder,
                    IconSource: iconSource);
                entries.Add(entry);
                favoriteList?.Add(entry);
            }
        });
    }

    private static TopBarSearchItemKind ClassifyLauncherPath(string path) =>
        FileMappingService.IsShellEntry(path)
        || Path.EndsInDirectorySeparator(path)
        || (!LauncherFileCatalog.IsRemotePath(path) && Directory.Exists(path))
            ? TopBarSearchItemKind.Folder
            : TopBarSearchItemKind.File;

}
