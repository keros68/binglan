using System.Diagnostics;
using System.IO;
using System.Windows.Interop;
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
    private Task<(DockPinnedApp Pin, string? Folder)[]>? _launcherApps;
    private DateTimeOffset _launcherAppsAt;

    internal void ToggleLauncherPanel()
    {
        if (_isExiting || _exitRequested)
        {
            return;
        }
        if (_launcherPanel is { } open)
        {
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
        var foreground = DockNativeMethods.GetForegroundWindow();
        var (searchHolder, searchBorrowed) = TopBarWindow.SearchForeground;
        if (foreground == searchHolder && searchBorrowed != 0)
        {
            foreground = searchBorrowed;
        }
        if (monitor is null || FullScreenWatcher.IsFullScreen(foreground, monitor.MonitorHandle, excludeMaximized: true))
        {
            return;
        }

        var panel = new LauncherPanelWindow(
            ReadLauncherSnapshotAsync, OpenLauncherTarget, ReadLauncherFileBoxes,
            SendLauncherPathToFileBoxAsync, foreground, monitor, _state.LauncherAppearance, _state.Style,
            PinLauncherEntry, () => _state.LauncherGroups, ScheduleSave, () => _state.LauncherAppCategoryOverrides);
        _launcherPanel = panel;
        panel.Closed += (_, _) =>
        {
            if (_launcherPanel != panel)
            {
                return;
            }
            _launcherPanel = null;
            _launcherLostFocusAt = panel.DismissedOnDeactivation ? Environment.TickCount64 : null;
        };
        panel.ShowPanel();
    }

    private nint ResolveLauncherForeground(nint current)
    {
        if (_launcherPanel is { } panel
            && new WindowInteropHelper(panel).Handle == current
            && DockNativeMethods.IsWindow(panel.PreviousForeground))
        {
            return panel.PreviousForeground;
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
        var box = _windows.OfType<FileBoxWindow>().FirstOrDefault(window => window.State.Id == id);
        if (box is null || !_state.FileBoxes.Any(item => item.Id == id))
        {
            return "该分组盒已移除。";
        }

        var status = await box.AddMappingsWithStatusAsync([path]);
        return _state.FileBoxes.Any(item => item.Id == id) ? status : "该分组盒已移除。";
    }

    private async void OpenLauncherTarget(LauncherPanelEntry entry, bool locate)
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
            _trayIcon.ShowBalloonTip(3000, "启动面板", failure.Message, TrayTipKind.Info);
        }
    }

    private string PinLauncherEntry(LauncherPanelEntry entry)
    {
        var pin = entry.Pinned ?? DockPinRules.CreateFromPath(entry.Candidate.Path, entry.Candidate.DisplayName);
        if (pin is null) return "此入口无法固定到 Dock";
        if (!DockPinRules.Pin(_state.Dock, pin))
        {
            return entry.Pinned is { ExecutablePath: not null }
                ? "Dock 已固定此程序的入口" : "此入口已固定到 Dock";
        }
        ApplyDock();
        return "已固定到 Dock";
    }

    private async Task<LauncherPanelSnapshot> ReadLauncherSnapshotAsync()
    {
        var pins = _state.Dock.PinnedApps.Select(pin => new DockPinnedApp
        {
            DisplayName = pin.DisplayName,
            ExecutablePath = pin.ExecutablePath,
            TargetPath = pin.TargetPath,
            AppUserModelId = pin.AppUserModelId,
            Arguments = pin.Arguments,
            WorkingDirectory = pin.WorkingDirectory,
            IconFile = pin.IconFile
        }).ToArray();
        var mapped = _state.FileBoxes.SelectMany(box => box.Items.Select(item =>
            (item.Path, item.Title, Source: box.Title))).ToArray();
        var quickPlaces = _state.QuickPlaces.Select(place => (place.Target, place.Name)).ToArray();
        var savedEntries = _state.LauncherGroups.SelectMany(group => group.Members)
            .Where(member => member.FallbackPath is not null || member.FallbackPin is not null)
            .DistinctBy(member => member.Key).ToArray();
        var useEverything = _state.TopBar.UseEverything;
        var now = DateTimeOffset.Now;
        if (_launcherApps is null || _launcherApps.IsFaulted || now - _launcherAppsAt >= LauncherAppCacheLifetime)
        {
            _launcherAppsAt = now;
            _launcherApps = Task.Run(() =>
            {
                var folders = new Dictionary<DockPinnedApp, string>();
                return DockShortcuts.ReadStartMenuApps(preserveLaunchVariants: true, folders)
                    .Select(pin => (Pin: pin, Folder: folders.GetValueOrDefault(pin))).ToArray();
            });
        }
        var apps = await _launcherApps;
        return await Task.Run(() =>
        {
            var entries = new List<LauncherPanelEntry>();
            var favorites = new List<LauncherPanelEntry>();
            foreach (var pin in pins)
            {
                AddPin(pin, "Dock", favorites);
            }
            foreach (var (app, folder) in apps)
            {
                AddPin(app, "开始菜单", startMenuFolder: folder);
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
            return new LauncherPanelSnapshot(entries, favorites, useEverything);

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
                    AddPin(pin, source);
                    return;
                }
                Add(path, name, source, ClassifyLauncherPath(path));
            }

            void AddPin(DockPinnedApp pin, string source, List<LauncherPanelEntry>? favoriteList = null,
                bool isSavedFallback = false, string? startMenuFolder = null)
            {
                var path = pin.TargetPath ?? pin.ExecutablePath
                    ?? (pin.AppUserModelId is { } id ? DockAppResolver.AppsFolderPath(id) : null);
                if (path is not null)
                {
                    var kind = pin.TargetPath is null ? TopBarSearchItemKind.App : ClassifyLauncherPath(path);
                    Add(path, pin.DisplayName, source, kind, pin, favoriteList, isSavedFallback, startMenuFolder);
                }
            }

            void Add(string path, string? name, string source, TopBarSearchItemKind kind,
                DockPinnedApp? pin = null, List<LauncherPanelEntry>? favoriteList = null, bool isSavedFallback = false,
                string? startMenuFolder = null)
            {
                var fullName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                var displayName = string.IsNullOrWhiteSpace(name) ? FileMappingService.GetDisplayName(path) : name;
                var entry = new LauncherPanelEntry(
                    new TopBarSearchCandidate(kind, displayName, fullName, path), source, pin, isSavedFallback, startMenuFolder);
                entries.Add(entry);
                favoriteList?.Add(entry);
            }
        });
    }

    private static TopBarSearchItemKind ClassifyLauncherPath(string path) =>
        FileMappingService.IsShellEntry(path)
        || Path.EndsInDirectorySeparator(path)
        || (!path.StartsWith(@"\\", StringComparison.Ordinal) && Directory.Exists(path))
            ? TopBarSearchItemKind.Folder
            : TopBarSearchItemKind.File;

}
