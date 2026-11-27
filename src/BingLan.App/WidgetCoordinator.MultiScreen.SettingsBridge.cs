using BingLan.App.Dock;
using BingLan.App.Services;
using BingLan.App.Windows;
using BingLan.Core.Models;
using BingLan.Core.Services;

namespace BingLan.App;

public sealed partial class WidgetCoordinator
{
    partial void NotifyMultiScreenUiChanged(bool refreshComponentList) =>
        _settingsWindow?.RefreshMultiScreenUi(refreshComponentList);

    internal MultiScreenSettingsBridge BuildMultiScreenSettingsBridge() => new()
    {
        IsEnabled = () => ScreenProfileAccessor.IsMultiScreenEnabled(_state),
        HasMultipleMonitors = MonitorIdentityResolver.HasMultipleMonitors,
        GetConnectedMonitorCount = MonitorIdentityResolver.GetConnectedMonitorCount,
        HasSavedSecondaryState = HasSavedSecondaryState,
        SetEnabled = SetMultiScreenEnabled,
        CanBindLegacyRoot = () => _state.MultiScreen?.RootMonitorKey is null,
        BindLegacyRoot = BindLegacyRootToScreen,
        GetScreens = BuildMonitorCardOptions,
        GetSurfacePreferences = monitorKey => _state.MultiScreen?.SurfacePreferences.GetValueOrDefault(monitorKey) is { } preferences
            ? preferences with { } : null,
        ApplySurfacePreferences = ApplyScreenSurfacePreferences,
        GetSurfaceStatus = GetScreenSurfaceStatus,
        GetOfflineProfiles = GetOfflineScreenProfiles,
        BindOfflineProfile = BindOfflineScreenProfile,
        GetExperience = screenKey => ScreenProfileAccessor.GetExperience(_state, screenKey),
        ApplyExperience = (screenKey, experience) =>
        {
            var normalized = MultiScreenRules.NormalizeScreenKey(screenKey);
            if (normalized is null)
            {
                ApplyDesktopExperience(experience);
                return;
            }

            if (_multiScreen is null && ScreenProfileAccessor.IsMultiScreenEnabled(_state))
            {
                SyncMultiScreenRuntime();
            }

            if (_multiScreen is not null)
            {
                _multiScreen.ApplyExperienceForScreen(normalized, experience, saveImmediate: true);
            }
            else
            {
                var profile = ScreenProfileAccessor.EnsureScreenProfile(_state, normalized);
                DesktopExperienceRules.Normalize(experience);
                profile.Experience = experience;
                SaveNow();
            }
        },
        ApplyLayoutPreset = (screenKey, experience) =>
        {
            var normalized = MultiScreenRules.NormalizeScreenKey(screenKey);
            if (normalized is null)
            {
                ApplyLayoutPreset(experience);
                return;
            }

            if (_multiScreen is null && ScreenProfileAccessor.IsMultiScreenEnabled(_state))
            {
                SyncMultiScreenRuntime();
            }

            if (_multiScreen is not null)
            {
                _multiScreen.ApplyLayoutPresetForScreen(normalized, experience);
            }
            else
            {
                var profile = ScreenProfileAccessor.EnsureScreenProfile(_state, normalized);
                DesktopExperienceRules.Normalize(experience);
                profile.Experience = experience;
                SaveNow();
            }
        },
        GetQuickPlaces = screenKey => ScreenProfileAccessor.GetQuickPlaces(_state, screenKey),
        SetQuickPlaces = (screenKey, items) =>
        {
            var normalized = MultiScreenRules.NormalizeScreenKey(screenKey);
            if (normalized is null)
            {
                _state.QuickPlaces = QuickPlaceRules.Normalize(items);
                ApplyQuickPlaces();
                ScheduleSave();
                return;
            }

            if (_multiScreen is not null)
            {
                _multiScreen.ApplyQuickPlacesForScreen(normalized, items);
            }
            else
            {
                ScreenProfileAccessor.SetQuickPlaces(_state, normalized, items);
                ScheduleSave();
            }
        },
        GetPrimaryTodoId = screenKey => ScreenProfileAccessor.GetPrimaryTodoId(_state, screenKey),
        GetWindowsForScreen = GetWindowsForScreen,
        GetScreenKeyForWindow = window => _multiScreen?.TryGetScreenKey(window),
        AddWidgetOnScreen = (screenKey, kind) =>
        {
            var normalized = MultiScreenRules.NormalizeScreenKey(screenKey);
            if (normalized is null)
            {
                switch (kind)
                {
                    case NewWidgetKind.Note:
                        AddRootNote();
                        return _windows.OfType<NoteWidgetWindow>().LastOrDefault(win => !IsSecondaryWindow(win));
                    case NewWidgetKind.FileBox:
                        AddRootFileBox();
                        return _windows.OfType<FileBoxWindow>().LastOrDefault(win => !IsSecondaryWindow(win));
                    default:
                        AddRootTodo();
                        return _windows.OfType<TodoWidgetWindow>().LastOrDefault(win => !IsSecondaryWindow(win));
                }
            }

            if (_multiScreen is null && ScreenProfileAccessor.IsMultiScreenEnabled(_state))
            {
                SyncMultiScreenRuntime();
            }
            return _multiScreen?.AddWidgetOnScreen(normalized, kind);
        },
        ResetWidgetPlacement = (screenKey, window, kind) =>
        {
            var normalized = MultiScreenRules.NormalizeScreenKey(screenKey);
            if (normalized is null)
            {
                return ResetWidgetPlacement(window, kind);
            }

            return _multiScreen?.ResetPlacementForScreen(normalized, window, kind) ?? false;
        },
        CopyPrimaryProfileToScreen = CopyPrimarySettingsToScreen,
        MoveWidgetToScreen = (window, targetScreenKey) =>
        {
            if (_multiScreen is null && ScreenProfileAccessor.IsMultiScreenEnabled(_state))
            {
                SyncMultiScreenRuntime();
            }
            if (_multiScreen is null)
            {
                return new MoveWidgetResult(false, "请先开启多屏模式并连接副屏", null);
            }
            var monitors = MonitorIdentityResolver.ResolveConnected();
            return _multiScreen.MoveWindowToScreen(window, targetScreenKey, monitors);
        }
    };

    private IReadOnlyList<MonitorCardOption> BuildMonitorCardOptions()
    {
        var enabled = ScreenProfileAccessor.IsMultiScreenEnabled(_state);
        if (!enabled && !MonitorIdentityResolver.HasMultipleMonitors())
        {
            return [];
        }

        var monitors = _multiScreen?.ConnectedMonitors ?? MonitorIdentityResolver.ResolveConnected();
        var options = new List<MonitorCardOption>(monitors.Count);
        foreach (var monitor in monitors.Where(m => m.IsPersistentIdentity))
        {
            var screenKey = enabled && _multiScreen is { } host ? host.GetScreenKeyForMonitor(monitor)
                : !enabled && monitor.IsPrimary ? null : monitor.MonitorKey;
            var cardCount = enabled || monitor.IsPrimary ? CountVisibleCardsOnScreen(screenKey) : 0;
            var shortLabel = monitor.IsPrimary ? "主屏" : $"副屏 {monitor.DisplayIndex}";
            options.Add(new MonitorCardOption(
                ScreenKey: screenKey,
                MonitorKey: monitor.MonitorKey,
                DisplayLabel: monitor.DisplayLabel,
                ShortLabel: shortLabel,
                ResolutionSummary: monitor.ResolutionSummary,
                DisplayIndex: monitor.DisplayIndex,
                IsPrimary: monitor.IsPrimary,
                IsConnected: true,
                Bounds: monitor.Bounds,
                WorkingArea: monitor.WorkingArea,
                Dpi: monitor.Dpi,
                CardCount: cardCount));
        }

        return options;
    }

    private int CountVisibleCardsOnScreen(string? screenKey)
    {
        var experience = ScreenProfileAccessor.GetExperience(_state, screenKey);
        var builtIn = DesktopExperienceRules.ComponentOrder
            .Where(kind => kind != DesktopComponentKind.Todo)
            .Count(kind => experience.GetComponent(kind).IsVisible && !IsInformationCardSuppressed(screenKey, kind));
        var todos = experience.GetComponent(DesktopComponentKind.Todo).IsVisible
            ? ScreenProfileAccessor.GetTodosForScreen(_state, screenKey).Count
            : 0;
        var notes = ScreenProfileAccessor.GetNotesForScreen(_state, screenKey).Count(note => !note.IsHidden);
        var visibleBoxes = ScreenProfileAccessor.GetFileBoxesForScreen(_state, screenKey)
            .Where(box => !box.IsHidden)
            .ToList();
        var boxWindows = FileBoxGroupRules.PartitionIntoHosts(visibleBoxes).Count;
        return builtIn + todos + notes + boxWindows;
    }
}
