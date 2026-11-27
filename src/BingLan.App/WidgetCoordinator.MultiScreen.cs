using BingLan.App.Dock;
using BingLan.App.Services;
using BingLan.App.Windows;
using BingLan.Core.Models;
using BingLan.Core.Dock;
using BingLan.Core.Services;

namespace BingLan.App;

public sealed partial class WidgetCoordinator
{
    private MultiScreenHost? _multiScreen;
    private ResolvedMonitor? _rootMonitor;
    private bool _cardRuntimeStarted;
    private bool _rootWindowsRestored;

    private bool CanPresentRootWidgets =>
        !ScreenProfileAccessor.IsMultiScreenEnabled(_state) || _rootMonitor is not null;

    internal bool HasActiveMultiScreenHost => _multiScreen is not null;

    internal int SecondaryWindowCount => _multiScreen?.SecondaryWindowCount ?? 0;

    internal MultiScreenHost? ActiveMultiScreenHost => _multiScreen;

    internal IReadOnlyList<WidgetWindowBase> GetWindowsForScreen(string? screenKey)
    {
        var normalized = MultiScreenRules.NormalizeScreenKey(screenKey);
        if (normalized is null)
        {
            return _windows.Where(window => !IsSecondaryWindow(window)).ToList();
        }

        return _multiScreen?.GetWindowsForSecondaryScreen(normalized) ?? [];
    }

    partial void NotifyMultiScreenUiChanged(bool refreshComponentList = false);

    private bool IsSecondaryWindow(WidgetWindowBase window) =>
        _multiScreen?.IsSecondaryWindow(window) == true;

    private DesktopExperienceState GetExperienceForWindow(WidgetWindowBase window) =>
        _multiScreen?.TryGetExperienceForWindow(window) ?? _state.DesktopExperience;

    private void SyncMultiScreenRuntime()
    {
        if (_isExiting)
        {
            return;
        }

        if (ScreenProfileAccessor.IsMultiScreenEnabled(_state))
        {
            var monitors = MonitorIdentityResolver.ResolveConnected();
            var rootKey = _state.MultiScreen?.RootMonitorKey;
            _rootMonitor = monitors.FirstOrDefault(m => m.IsPersistentIdentity
                && string.Equals(m.MonitorKey, rootKey, StringComparison.Ordinal));
            _multiScreen ??= CreateMultiScreenHost();
            _multiScreen.SyncConnectedScreens(monitors);
        }
        else
        {
            _rootMonitor = null;
            DisposeMultiScreenHost(capturePlacement: !WindowScreenRecovery.IsLayoutTransitioning());
        }

        if (_cardRuntimeStarted)
        {
            SyncRootWindowLifecycle();
        }
    }

    private void SyncRootWindowLifecycle()
    {
        if (!CanPresentRootWidgets)
        {
            _rootWindowsRestored = false;
            foreach (var window in _windows.Where(window => !IsSecondaryWindow(window)).ToList())
            {
                Capture(window);
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
        }
        else if (!_rootWindowsRestored)
        {
            _rootWindowsRestored = true;
            RestoreRootWindows();
        }
    }

    private void ShowRootWindow(WidgetWindowBase window)
    {
        if (!CanPresentRootWidgets)
        {
            return;
        }

        if (_desktopHidden)
        {
            if (!_windowsHiddenFromTray.Contains(window))
            {
                _windowsHiddenFromTray.Add(window);
            }
            return;
        }

        if (_rootMonitor is null)
        {
            window.Show();
            return;
        }

        var wasApplying = _isApplyingDesktopExperience;
        _isApplyingDesktopExperience = true;
        try
        {
            using (WindowScreenRecovery.DeferShowRecovery())
            using (window.SuppressPlacementCapture())
            {
                window.Show();
                PlaceRootWindow(window);
            }
        }
        finally
        {
            _isApplyingDesktopExperience = wasApplying;
        }
    }

    private void PlaceRootWindow(WidgetWindowBase window)
    {
        if (_rootMonitor is not { } monitor || _multiScreen is not { } host
            || GetRootPlacement(window) is not { } placement)
        {
            return;
        }

        var layoutKey = MultiScreenHost.CurrentLayoutKey(host.ConnectedMonitors);
        var bounds = MultiScreenHost.ResolveTargetPixelBounds(placement, monitor, layoutKey);
        using (window.SuppressPlacementCapture())
        {
            MultiScreenHost.ApplyPhysicalBounds(window, bounds, monitor.Dpi);
        }
        if (placement.MonitorLocal is null && window.WindowHandle != 0)
        {
            DisplayLayoutRules.Remember(placement, layoutKey, bounds);
            ScreenProfileAccessor.RecordMonitorLocalPlacement(placement, bounds, monitor.WorkingArea, monitor.Dpi);
        }
    }

    private void CaptureRootLocalPosition(WidgetWindowBase window)
    {
        if (_rootMonitor is not { } monitor || _multiScreen is not { } host
            || !window.IsVisible || window is FileBoxWindow { IsEdgeDocked: true }
            || GetRootPlacement(window) is not { } placement
            || MultiScreenHost.TryReadWindowPixelBounds(window) is not { } bounds
            || MonitorIdentityResolver.FindByBounds(host.ConnectedMonitors, bounds)?.MonitorKey != monitor.MonitorKey)
        {
            return;
        }

        var layoutKey = MultiScreenHost.CurrentLayoutKey(host.ConnectedMonitors);
        if (DisplayLayoutRules.TryRecall(placement, layoutKey, out var remembered))
        {
            bounds = remembered;
        }
        ScreenProfileAccessor.RecordMonitorLocalPlacement(placement, bounds, monitor.WorkingArea, monitor.Dpi);
    }

    private WindowPlacement? GetRootPlacement(WidgetWindowBase window) => window switch
    {
        TodoWidgetWindow todo => todo.State.Placement,
        NoteWidgetWindow note => note.State.Placement,
        FileBoxWindow box => box.State.Placement,
        InformationWidgetWindow { ComponentKind: { } kind } =>
            _state.DesktopExperience.GetComponent(kind).Placement,
        QuickPlacesWindow => _state.DesktopExperience.GetComponent(DesktopComponentKind.QuickLaunch).Placement,
        _ => null
    };

    private string? GetFileBoxOwnerDeviceName(FileBoxWindow box)
    {
        if (_multiScreen is not { } host)
        {
            return null;
        }

        var screenKey = host.TryGetScreenKey(box);
        return screenKey is null ? _rootMonitor?.DeviceName
            : host.ConnectedMonitors.FirstOrDefault(m => m.IsPersistentIdentity && m.MonitorKey == screenKey)?.DeviceName;
    }

    private void SanitizeRootPlacement(WindowPlacement placement)
    {
        if (!ScreenProfileAccessor.IsMultiScreenEnabled(_state))
        {
            SanitizePlacement(placement);
        }
    }

    internal bool BindLegacyRootToScreen(string monitorKey)
    {
        if (!ScreenProfileAccessor.IsMultiScreenEnabled(_state)
            || _multiScreen?.ConnectedMonitors.Any(m => m.IsPersistentIdentity && m.MonitorKey == monitorKey) != true
            || !ScreenProfileAccessor.BindLegacyRootToScreen(_state, monitorKey))
        {
            return false;
        }

        SyncMultiScreenRuntime();
        ApplyDisplayRecovery();
        _edgeDock.ApplyAll();
        SaveNow();
        NotifyMultiScreenUiChanged(refreshComponentList: true);
        return true;
    }

    internal void HandleMultiScreenDisplayLayoutChanged()
    {
        if (_isExiting)
        {
            return;
        }

        // Always runs BEFORE ApplyDisplayRecovery so disconnected secondary monitors immediately
        // destroy their secondary windows instead of letting GatherOnArrival pull them onto primary.
        SyncMultiScreenRuntime();
        if (ScreenProfileAccessor.IsMultiScreenEnabled(_state))
        {
            ApplyDisplayRecovery();
        }
        NotifyMultiScreenUiChanged(refreshComponentList: true);
    }

    private void DisposeMultiScreenHost(bool capturePlacement = true)
    {
        if (_multiScreen is null)
        {
            return;
        }

        _multiScreen.Dispose(capturePlacement);
        _multiScreen = null;
        UpdateSamplingMode();
    }

    private MultiScreenHost CreateMultiScreenHost() => new(
        _state,
        _windows,
        _windowsHiddenFromTray,
        new MultiScreenCoordinatorCallbacks
        {
            GetInformationState = InformationState,
            CreateInformationWindow = CreateSecondaryInformationWindow,
            CreateTodoWindow = CreateSecondaryTodoWindow,
            CreateNoteWindow = CreateSecondaryNoteWindow,
            CreateFileBoxWindow = CreateSecondaryFileBoxWindow,
            CreateQuickPlacesWindow = CreateSecondaryQuickPlacesWindow,
            ApplyTodoComponent = ApplyTodoComponent,
            CopyTodoLookFromComponent = CopyTodoLookFromComponent,
            SyncComponentFromTodo = SyncComponentFromTodo,
            UseSharedLook = UseSharedLook,
            ApplyEdgeDock = () => _edgeDock.ApplyAll(),
            UpdateSamplingMode = UpdateSamplingMode,
            ScheduleSave = ScheduleSave,
            SaveNow = SaveNow,
            RefreshSettingsUi = () => NotifyMultiScreenUiChanged(refreshComponentList: true),
            IsDesktopHidden = () => _desktopHidden,
            EnsureDesktopVisible = () =>
            {
                if (_desktopHidden)
                {
                    ToggleDesktop();
                }
            },
            RunSuppressingExperienceChanges = action =>
            {
                var previous = _isApplyingDesktopExperience;
                _isApplyingDesktopExperience = true;
                try
                {
                    action();
                }
                finally
                {
                    _isApplyingDesktopExperience = previous;
                }
            },
            IsInformationCardSuppressed = IsInformationCardSuppressed
        });

    private InformationWidgetWindow CreateSecondaryInformationWindow(
        InformationWidgetState state,
        DesktopExperienceState experience,
        DesktopComponentKind kind)
    {
        var window = new InformationWidgetWindow(
            state,
            _performanceSampler,
            _weatherService,
            experience,
            kind)
        {
            ShowInTaskbar = false
        };
        window.SetAudioSpectrumSuspended(_fullScreenWatcher.IsFullScreenInFront || _gameModeActive);
        Wire(window);
        _windows.Add(window);
        return window;
    }

    private TodoWidgetWindow CreateSecondaryTodoWindow(
        TodoWidgetState state,
        DesktopComponentState? primaryComponent)
    {
        var window = new TodoWidgetWindow(state)
        {
            ShowInTaskbar = false
        };
        window.SameKindOtherTitles = () => _state.TodoWidgets
            .Where(other => other.Id != state.Id)
            .Select(other => other.Title);
        if (primaryComponent is not null)
        {
            CopyTodoLookFromComponent(state, primaryComponent);
            ApplyTodoComponent(window, primaryComponent);
        }
        Wire(window);
        _windows.Add(window);
        return window;
    }

    private NoteWidgetWindow CreateSecondaryNoteWindow(NoteWidgetState state)
    {
        var window = new NoteWidgetWindow(state)
        {
            ShowInTaskbar = false
        };
        window.SameKindOtherTitles = () => _state.NoteWidgets
            .Where(other => other.Id != state.Id)
            .Select(other => other.Title);
        Wire(window);
        _windows.Add(window);
        return window;
    }

    private FileBoxWindow CreateSecondaryFileBoxWindow(IReadOnlyList<FileBoxState> members)
    {
        var ordered = members.OrderBy(m => m.TabOrder).ToList();
        var lead = ordered.FirstOrDefault(m => m.IsActiveTab) ?? ordered[0];
        if (ordered.Count > 1)
        {
            FileBoxGroupRules.SyncGroupSharedProperties(lead, ordered);
        }

        var window = new FileBoxWindow(ordered)
        {
            ShowInTaskbar = false
        };
        window.OtherBoxes = () => _windows
            .OfType<FileBoxWindow>()
            .Where(other => !ReferenceEquals(other, window))
            .ToList();
        window.SameKindOtherTitles = () => _state.FileBoxes
            .Where(other => other.Id != window.State.Id)
            .Select(other => other.Title);
        window.IsDeleteOriginalsEnabled = () => _state.FileBoxDeleteOriginalsEnabled;
        window.EdgeDockToggled += (box, enabled) => _edgeDock.ToggleEdgeDock(box, enabled);
        window.EdgeDockFileDragNear += box => _edgeDock.RevealImmediatelyForDrag(box);
        window.MergeRequested += (source, target) => MergeFileBoxes(source, target);
        window.DetachTabRequested += (host, tab) => DetachFileBoxTab(host, tab);
        window.SplitGroupRequested += SplitFileBoxGroup;
        window.DeleteTabRequested += DeleteFileBoxTab;
        window.DragMoveEvaluated += OnFileBoxDragMoveEvaluated;
        window.DragMoveFinished += OnFileBoxDragMoveFinished;
        Wire(window);
        _windows.Add(window);
        return window;
    }

    private QuickPlacesWindow CreateSecondaryQuickPlacesWindow()
    {
        var window = new QuickPlacesWindow
        {
            ShowInTaskbar = false
        };
        Wire(window);
        _windows.Add(window);
        return window;
    }

    private void ApplyGlassToSecondaryProfiles(DesktopStyleState previousStyle, DesktopStyleState nextStyle)
    {
        if (_multiScreen is not null)
        {
            _multiScreen.ApplyGlassToSecondaryScreens(previousStyle, nextStyle);
            return;
        }

        if (_state.MultiScreen is null)
        {
            return;
        }

        foreach (var profile in _state.MultiScreen.Screens.Values)
        {
            foreach (var component in profile.Experience.Components)
            {
                DesktopStyleRules.ApplySharedComponentLook(
                    component,
                    previousStyle,
                    nextStyle,
                    QuickPlacesWindow.ClearBarOpacity);
            }
        }
    }

    private bool HasSavedSecondaryState()
    {
        if (_state.MultiScreen is { Screens.Count: > 0 } or { SurfacePreferences.Count: > 0 } or { RootMonitorKey: not null })
        {
            return true;
        }

        return _state.TodoWidgets.Any(item => !ScreenProfileAccessor.IsPrimary(item.ScreenKey))
            || _state.NoteWidgets.Any(item => !ScreenProfileAccessor.IsPrimary(item.ScreenKey))
            || _state.FileBoxes.Any(item => !ScreenProfileAccessor.IsPrimary(item.ScreenKey));
    }

    internal void SetMultiScreenEnabled(bool enabled)
    {
        if (enabled == ScreenProfileAccessor.IsMultiScreenEnabled(_state))
        {
            return;
        }

        var firstActivation = _state.MultiScreen is null;
        _state.MultiScreen ??= new MultiScreenState();
        if (enabled && firstActivation)
        {
            // Enabling from the legacy desktop explicitly adopts the current main screen,
            // provided it has no independent profile. Previously enabled v65 state awaits
            // a screen choice because the upgrade-time main screen may be a different device.
            var primary = MonitorIdentityResolver.ResolveConnected().FirstOrDefault(m => m.IsPrimary);
            if (primary is { IsPersistentIdentity: true })
            {
                CaptureAll();
                ScreenProfileAccessor.BindLegacyRootToScreen(_state, primary.MonitorKey);
            }
        }

        _state.MultiScreen.IsEnabled = enabled;
        SyncMultiScreenRuntime();
        _topBar.Apply();
        _dock.Apply();
        if (enabled)
        {
            RefreshInformationCardVisibility();
        }
        else if (CanPresentRootWidgets && _cardRuntimeStarted)
        {
            ApplyRootInformationCards(_state.DesktopExperience);
        }
        ApplyDisplayRecovery();
        UpdateSamplingMode();
        SaveNow();
        NotifyMultiScreenUiChanged(refreshComponentList: true);
    }
}
