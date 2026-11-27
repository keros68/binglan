using BingLan.App.Dock;
using BingLan.App.Services;
using BingLan.App.Windows;
using BingLan.Core.Models;

namespace BingLan.App;

public sealed partial class WidgetCoordinator
{
    private IReadOnlyDictionary<string, ScreenSurfacePreferences>? ReadScreenSurfacePreferences() =>
        _state.MultiScreen is { IsEnabled: true } multiScreen ? multiScreen.SurfacePreferences : null;

    private ResolvedMonitor? FindPhysicalMonitor(string monitorKey) => _multiScreen?.ConnectedMonitors.FirstOrDefault(
        monitor => monitor.IsPersistentIdentity && string.Equals(monitor.MonitorKey, monitorKey, StringComparison.Ordinal));

    private ScreenSurfacePreferences? GetOrCreateScreenSurfacePreferences(string monitorKey)
    {
        if (_state.MultiScreen is not { IsEnabled: true } multiScreen || FindPhysicalMonitor(monitorKey) is not { } monitor)
        {
            return null;
        }
        if (!multiScreen.SurfacePreferences.TryGetValue(monitorKey, out var preferences))
        {
            preferences = new ScreenSurfacePreferences { DisplayLabel = monitor.DisplayLabel };
            multiScreen.SurfacePreferences.Add(monitorKey, preferences);
        }
        return preferences;
    }

    private void ApplyScreenSurfacePreferences(string monitorKey, ScreenSurfacePreferences preferences)
    {
        if (_state.MultiScreen is not { IsEnabled: true } multiScreen || FindPhysicalMonitor(monitorKey) is not { } monitor)
        {
            return;
        }
        StoreScreenSurfacePreferences(multiScreen, monitor, preferences);
        ApplyScreenSurfaceChanges();
    }

    private static void StoreScreenSurfacePreferences(
        MultiScreenState multiScreen, ResolvedMonitor monitor, ScreenSurfacePreferences preferences)
    {
        preferences = preferences with { DisplayLabel = monitor.DisplayLabel };
        preferences.Normalize();
        if (preferences.IsEmpty)
        {
            multiScreen.SurfacePreferences.Remove(monitor.MonitorKey);
        }
        else
        {
            multiScreen.SurfacePreferences[monitor.MonitorKey] = preferences;
        }
    }

    private void ApplyScreenSurfaceChanges()
    {
        _topBar.Apply();
        _dock.Apply();
        RefreshInformationCardVisibility();
        UpdateSamplingMode();
        SaveNow();
        NotifyMultiScreenUiChanged(refreshComponentList: true);
    }

    private string CopyPrimarySettingsToScreen(string targetMonitorKey)
    {
        var connected = MonitorIdentityResolver.ResolveConnected();
        var source = connected.FirstOrDefault(monitor => monitor.IsPrimary && monitor.IsPersistentIdentity);
        var target = connected.FirstOrDefault(monitor => monitor.IsPersistentIdentity && monitor.MonitorKey == targetMonitorKey);
        if (source is null || target is null || target.IsPrimary)
        {
            return "请选择已连接的副屏。";
        }

        var multiScreen = _state.MultiScreen ??= new MultiScreenState();
        var wasEnabled = multiScreen.IsEnabled;
        if (!wasEnabled)
        {
            CaptureAll();
            if (multiScreen.RootMonitorKey is null)
            {
                ScreenProfileAccessor.BindLegacyRootToScreen(_state, source.MonitorKey);
            }
        }

        // While using one shared desktop, the visible source layout lives in the root document.
        var sourceKey = wasEnabled && multiScreen.RootMonitorKey != source.MonitorKey ? source.MonitorKey : null;
        var targetKey = multiScreen.RootMonitorKey == target.MonitorKey ? null : target.MonitorKey;
        var activePreferences = ReadScreenSurfacePreferences();
        var sourcePreferences = activePreferences?.GetValueOrDefault(source.MonitorKey);
        var targetPreferences = multiScreen.SurfacePreferences.GetValueOrDefault(target.MonitorKey) ?? new();
        var dockShown = SurfaceMonitorTargetResolver.ResolveTargets(_state.Dock.MonitorKey, _state.Dock.IsEnabled,
            activePreferences, item => item.DockEnableMode, connected).Any(item => item.Monitor.MonitorHandle == source.MonitorHandle);
        var topBarShown = SurfaceMonitorTargetResolver.ResolveTargets(_state.TopBar.MonitorKey, _state.TopBar.IsEnabled,
            activePreferences, item => item.TopBarEnableMode, connected).Any(item => item.Monitor.MonitorHandle == source.MonitorHandle);

        // Close the old card layouts before copying, so a later window capture cannot restore their previous positions.
        if (!ScreenProfileAccessor.MatchesScreen(sourceKey, targetKey))
        {
            foreach (var window in GetWindowsForScreen(targetKey)
                         .Where(window => window is InformationWidgetWindow or QuickPlacesWindow or TodoWidgetWindow))
            {
                Capture(window);
                _multiScreen?.UntrackSecondaryWindow(window);
                _windows.Remove(window);
                _windowsHiddenFromTray.Remove(window);
                if (!window.IsClosingOrClosed)
                {
                    window.CanClose = true;
                    using (window.SuppressPlacementCapture())
                    {
                        window.Close();
                    }
                }
            }

            ScreenProfileAccessor.CopyScreenCardSettings(_state,
                (sourceKey, source.WorkingArea, source.Dpi), (targetKey, target.WorkingArea, target.Dpi),
                MultiScreenHost.CurrentLayoutKey(connected));
        }
        StoreScreenSurfacePreferences(multiScreen, target, targetPreferences with
        {
            DockEnableMode = dockShown ? ScreenSurfaceEnableMode.Enabled : ScreenSurfaceEnableMode.Disabled,
            TopBarEnableMode = topBarShown ? ScreenSurfaceEnableMode.Enabled : ScreenSurfaceEnableMode.Disabled,
            DockEdge = sourcePreferences?.DockEdge ?? _state.Dock.Edge,
            DockVisibilityMode = sourcePreferences?.DockVisibilityMode ?? _state.Dock.VisibilityMode,
            TopBarVisibilityMode = sourcePreferences?.TopBarVisibilityMode ?? _state.TopBar.VisibilityMode,
            TopBarHeight = sourcePreferences?.TopBarHeight ?? _state.TopBar.Height
        });

        multiScreen.IsEnabled = true;
        SyncMultiScreenRuntime();
        if (targetKey is null)
        {
            ApplyDesktopExperienceWithSave(_state.DesktopExperience, saveImmediate: false);
        }
        if (!wasEnabled)
        {
            ApplyDisplayRecovery();
        }
        ApplyScreenSurfaceChanges();
        return "已复制主屏设置。此屏可单独调整。";
    }

    private ScreenSurfaceStatus GetScreenSurfaceStatus(string monitorKey)
    {
        var monitors = _multiScreen?.ConnectedMonitors ?? [];
        var preferences = ReadScreenSurfacePreferences();
        var dockEnabled = SurfaceMonitorTargetResolver.ResolveTargets(_state.Dock.MonitorKey,
            _state.Dock.IsEnabled, preferences, item => item.DockEnableMode, monitors).Any(item => item.Key == monitorKey);
        var topBarEnabled = SurfaceMonitorTargetResolver.ResolveTargets(_state.TopBar.MonitorKey,
            _state.TopBar.IsEnabled, preferences, item => item.TopBarEnableMode, monitors).Any(item => item.Key == monitorKey);
        return new ScreenSurfaceStatus(
            DescribeScreenSurface(_dock.IsRunningOnMonitor(monitorKey), _dock.IsVisibleOnMonitor(monitorKey), dockEnabled),
            DescribeScreenSurface(_topBar.IsRunningOnMonitor(monitorKey), _topBar.IsVisibleOnMonitor(monitorKey), topBarEnabled),
            dockEnabled, topBarEnabled);
    }

    private static string DescribeScreenSurface(bool running, bool visible, bool configured) =>
        visible ? "正在显示" : running ? "已启用 · 当前隐藏" : configured ? "已启用 · 当前未显示" : "未启用";

    private IReadOnlyList<OfflineScreenProfileOption> GetOfflineScreenProfiles(string targetKey)
    {
        if (_state.MultiScreen is not { IsEnabled: true } multiScreen || _multiScreen is not { } host)
        {
            return [];
        }

        var connected = host.ConnectedMonitors.Where(monitor => monitor.IsPersistentIdentity)
            .Select(monitor => monitor.MonitorKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var canBind = connected.Contains(targetKey) && ScreenProfileAccessor.CanBindArchivedProfile(_state, targetKey);
        var options = new List<OfflineScreenProfileOption>();
        if (multiScreen.RootMonitorKey is { } rootKey && !connected.Contains(rootKey))
        {
            options.Add(new OfflineScreenProfileOption(rootKey, "原桌面档案", CountVisibleCardsOnScreen(null), canBind));
        }

        foreach (var key in multiScreen.Screens.Keys.Concat(multiScreen.SurfacePreferences.Keys)
                     .Distinct(StringComparer.OrdinalIgnoreCase).Where(key => !connected.Contains(key) && key != multiScreen.RootMonitorKey))
        {
            var profile = multiScreen.Screens.GetValueOrDefault(key);
            var label = MultiScreenRules.NormalizeFriendlyName(profile?.FriendlyName)
                ?? profile?.DisplayLabel ?? multiScreen.SurfacePreferences.GetValueOrDefault(key)?.DisplayLabel;
            if (string.IsNullOrWhiteSpace(label) || label == key)
            {
                label = "已保存的屏幕档案";
            }
            options.Add(new OfflineScreenProfileOption(key, label, CountVisibleCardsOnScreen(key), canBind));
        }
        return options;
    }

    private bool BindOfflineScreenProfile(string sourceKey, string targetKey)
    {
        // Re-read the topology at commit time so a just-connected source can never be reassigned.
        var monitors = MonitorIdentityResolver.ResolveConnected();
        var target = monitors.FirstOrDefault(monitor => monitor.IsPersistentIdentity && monitor.MonitorKey == targetKey);
        if (target is null
            || monitors.Any(monitor => monitor.IsPersistentIdentity && monitor.MonitorKey == sourceKey)
            || !ScreenProfileAccessor.BindArchivedProfile(_state, sourceKey, new MonitorIdentityCandidate(
                target.MonitorKey, target.HardwareId, target.DeviceName, target.FriendlyName, target.DisplayLabel)))
        {
            return false;
        }

        SyncMultiScreenRuntime();
        _topBar.Apply();
        _dock.Apply();
        RefreshInformationCardVisibility();
        ApplyDisplayRecovery();
        _edgeDock.ApplyAll();
        SaveNow();
        NotifyMultiScreenUiChanged(refreshComponentList: true);
        return true;
    }

    private bool IsInformationCardSuppressed(string? screenKey, DesktopComponentKind kind)
    {
        if (!ScreenProfileAccessor.IsMultiScreenEnabled(_state) || _gameModeActive
            || DesktopModeRules.Detect(_state.Dock, _state.Taskbar) != DesktopMode.AppleStyle
            || (screenKey ?? _state.MultiScreen?.RootMonitorKey) is not { } monitorKey
            || !_topBar.IsRunningOnMonitor(monitorKey))
        {
            return false;
        }

        return DesktopModeRules.TopBarDuplicatesCard(_state.TopBar.Modules, kind);
    }

    private void RefreshInformationCardVisibility()
    {
        if (!ScreenProfileAccessor.IsMultiScreenEnabled(_state))
        {
            return;
        }
        _multiScreen?.RefreshInformationCardVisibility();
        if (CanPresentRootWidgets && _cardRuntimeStarted)
        {
            ApplyRootInformationCards(_state.DesktopExperience);
        }
    }
}
