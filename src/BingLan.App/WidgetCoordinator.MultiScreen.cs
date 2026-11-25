using System.Windows;
using BingLan.App.Dock;
using BingLan.App.Services;
using BingLan.App.Windows;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Services;

namespace BingLan.App;

public sealed partial class WidgetCoordinator
{
    private MultiScreenHost? _multiScreen;

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

    /// <summary>
    /// Lazy startup/sync for secondary displays. Zero allocations, zero windows, and zero
    /// <c>QueryDisplayConfig</c> calls when multi-screen is disabled or only one monitor is present.
    /// </summary>
    private void SyncMultiScreenRuntime()
    {
        if (_isExiting)
        {
            return;
        }

        if (!ScreenProfileAccessor.IsMultiScreenEnabled(_state))
        {
            var canCapturePlacement = !WindowScreenRecovery.IsLayoutTransitioning()
                && MonitorIdentityResolver.HasMultipleMonitors();
            DisposeMultiScreenHost(capturePlacement: canCapturePlacement);
            return;
        }

        if (!MonitorIdentityResolver.HasMultipleMonitors())
        {
            DisposeMultiScreenHost(capturePlacement: false);
            return;
        }

        var monitors = MonitorIdentityResolver.ResolveConnected();
        if (monitors.Count < 2)
        {
            DisposeMultiScreenHost(capturePlacement: false);
            return;
        }

        _multiScreen ??= CreateMultiScreenHost();
        _multiScreen.SyncConnectedScreens(monitors);
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
            }
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
        if (_state.MultiScreen is { Screens.Count: > 0 })
        {
            return true;
        }

        return _state.TodoWidgets.Any(item => !ScreenProfileAccessor.IsPrimary(item.ScreenKey))
            || _state.NoteWidgets.Any(item => !ScreenProfileAccessor.IsPrimary(item.ScreenKey))
            || _state.FileBoxes.Any(item => !ScreenProfileAccessor.IsPrimary(item.ScreenKey));
    }

    internal void SetMultiScreenEnabled(bool enabled)
    {
        if (!enabled)
        {
            if (_state.MultiScreen is not null)
            {
                _state.MultiScreen.IsEnabled = false;
            }
            var canCapturePlacement = !WindowScreenRecovery.IsLayoutTransitioning()
                && MonitorIdentityResolver.HasMultipleMonitors();
            DisposeMultiScreenHost(capturePlacement: canCapturePlacement);
            SaveNow();
            NotifyMultiScreenUiChanged();
            return;
        }

        _state.MultiScreen ??= new MultiScreenState();
        _state.MultiScreen.IsEnabled = true;
        SyncMultiScreenRuntime();
        SaveNow();
        NotifyMultiScreenUiChanged();
    }

    internal IReadOnlyList<DisconnectedScreenWidget> GetDisconnectedSecondaryWidgets()
    {
        if (!HasSavedSecondaryState())
        {
            return [];
        }

        var connectedSecondaryKeys = new HashSet<string>(StringComparer.Ordinal);
        if (MonitorIdentityResolver.HasMultipleMonitors())
        {
            if (ScreenProfileAccessor.IsMultiScreenEnabled(_state))
            {
                foreach (var monitor in MonitorIdentityResolver.ResolveConnected())
                {
                    if (!monitor.IsPrimary)
                    {
                        connectedSecondaryKeys.Add(monitor.MonitorKey);
                    }
                }
            }
            else
            {
                // Multi-screen switch is OFF while secondary display(s) remain physically attached:
                // match against lightweight EnumDisplayMonitors device names (0 QueryDisplayConfig calls)
                // so online secondary monitors are never misreported as disconnected.
                var connectedDeviceNames = MonitorIdentityResolver.GetConnectedSecondaryDeviceNames();
                if (_state.MultiScreen?.Screens is { Count: > 0 } savedScreens)
                {
                    foreach (var (key, profile) in savedScreens)
                    {
                        if (!string.IsNullOrWhiteSpace(profile.LastKnownDeviceName)
                            && connectedDeviceNames.Contains(profile.LastKnownDeviceName.Trim()))
                        {
                            connectedSecondaryKeys.Add(key);
                        }
                    }
                }
            }
        }

        return ScreenProfileAccessor.FindDisconnectedScreenWidgets(_state, connectedSecondaryKeys);
    }

    internal bool MoveDisconnectedWidgetToPrimary(Guid widgetId, string kindName)
    {
        var primarySnapshot = MonitorCatalog.GetAll().FirstOrDefault(m => m.IsPrimary);
        var workArea = primarySnapshot?.WorkingArea ?? PrimaryWorkAreaPixels();
        var dpi = primarySnapshot?.Dpi ?? 96u;
        var layoutKey = WindowScreenRecovery.CurrentLayout();

        switch (kindName)
        {
            case ScreenProfileAccessor.DisconnectedTodoKind:
            {
                var movedPx = ScreenProfileAccessor.MoveTodoToScreen(
                    _state,
                    widgetId,
                    targetScreenKey: null,
                    workArea,
                    dpi,
                    currentLayoutKey: layoutKey);
                if (movedPx is not { } targetPx)
                {
                    return false;
                }

                var todo = _state.TodoWidgets.FirstOrDefault(item => item.Id == widgetId);
                if (todo is null)
                {
                    return false;
                }

                var window = _windows.OfType<TodoWidgetWindow>().FirstOrDefault(win => win.State.Id == widgetId);
                using (WindowScreenRecovery.DeferShowRecovery())
                {
                    window ??= ShowTodo(todo);
                    if (!window.IsVisible)
                    {
                        window.Show();
                    }
                    MultiScreenHost.ApplyPhysicalBounds(window, targetPx, dpi, todo.Placement, layoutKey);
                }
                SaveNow();
                NotifyMultiScreenUiChanged(refreshComponentList: true);
                return true;
            }
            case ScreenProfileAccessor.DisconnectedNoteKind:
            {
                var movedPx = ScreenProfileAccessor.MoveNoteToScreen(
                    _state,
                    widgetId,
                    targetScreenKey: null,
                    workArea,
                    dpi,
                    currentLayoutKey: layoutKey);
                if (movedPx is not { } targetPx)
                {
                    return false;
                }

                var note = _state.NoteWidgets.FirstOrDefault(item => item.Id == widgetId);
                if (note is null)
                {
                    return false;
                }

                note.IsHidden = false;
                var window = _windows.OfType<NoteWidgetWindow>().FirstOrDefault(win => win.State.Id == widgetId);
                using (WindowScreenRecovery.DeferShowRecovery())
                {
                    if (window is null)
                    {
                        ShowNote(note);
                        window = _windows.OfType<NoteWidgetWindow>().FirstOrDefault(win => win.State.Id == widgetId);
                    }
                    else if (!window.IsVisible)
                    {
                        window.Show();
                    }
                    if (window is not null)
                    {
                        MultiScreenHost.ApplyPhysicalBounds(window, targetPx, dpi, note.Placement, layoutKey);
                    }
                }
                SaveNow();
                NotifyMultiScreenUiChanged(refreshComponentList: true);
                return true;
            }
            case ScreenProfileAccessor.DisconnectedFileBoxKind:
            {
                var movedPx = ScreenProfileAccessor.MoveFileBoxToScreen(
                    _state,
                    widgetId,
                    targetScreenKey: null,
                    workArea,
                    dpi,
                    currentLayoutKey: layoutKey);
                if (movedPx is not { } targetPx)
                {
                    return false;
                }

                var box = _state.FileBoxes.FirstOrDefault(item => item.Id == widgetId);
                if (box is null)
                {
                    return false;
                }

                var members = ScreenProfileAccessor.GetFileBoxGroupMembers(_state, box);
                foreach (var member in members)
                {
                    member.IsHidden = false;
                }
                var window = FindFileBoxWindow(box.Id);
                using (WindowScreenRecovery.DeferShowRecovery())
                {
                    window ??= ShowFileBoxGroup(members);
                    if (!window.IsVisible)
                    {
                        window.Show();
                    }
                    MultiScreenHost.ApplyPhysicalBounds(window, targetPx, dpi, box.Placement, layoutKey);
                    window.SyncGroupSharedProperties();
                }
                _edgeDock.ApplyAll();
                SaveNow();
                NotifyMultiScreenUiChanged(refreshComponentList: true);
                return true;
            }
            case ScreenProfileAccessor.DisconnectedQuickLaunchKind:
            {
                var entry = GetDisconnectedSecondaryWidgets()
                    .FirstOrDefault(item => item.Id == widgetId && item.KindName == kindName);
                if (entry is null
                    || !ScreenProfileAccessor.MoveDisconnectedQuickPlacesToPrimary(_state, entry.ScreenKey))
                {
                    return false;
                }

                ApplyQuickPlaces();
                SaveNow();
                NotifyMultiScreenUiChanged(refreshComponentList: true);
                return true;
            }
            case ScreenProfileAccessor.DisconnectedBuiltInInfoKind:
            {
                var entry = GetDisconnectedSecondaryWidgets()
                    .FirstOrDefault(item => item.Id == widgetId && item.KindName == kindName);
                if (entry is null)
                {
                    return false;
                }

                DesktopComponentKind? matchedKind = null;
                foreach (var candidate in InformationComponentKinds)
                {
                    if (ScreenProfileAccessor.CreateDisconnectedComponentId(entry.ScreenKey, candidate) == widgetId)
                    {
                        matchedKind = candidate;
                        break;
                    }
                }
                if (matchedKind is not { } kind
                    || !ScreenProfileAccessor.MoveDisconnectedBuiltInToPrimary(_state, entry.ScreenKey, kind))
                {
                    return false;
                }

                ApplyDesktopExperience(_state.DesktopExperience);
                NotifyMultiScreenUiChanged(refreshComponentList: true);
                return true;
            }
            default:
                return false;
        }
    }

    internal int MoveAllDisconnectedFromScreenToPrimary(string screenKey)
    {
        var items = GetDisconnectedSecondaryWidgets()
            .Where(item => string.Equals(item.ScreenKey, screenKey, StringComparison.Ordinal))
            .ToList();
        var moved = 0;
        foreach (var item in items)
        {
            if (MoveDisconnectedWidgetToPrimary(item.Id, item.KindName))
            {
                moved++;
            }
        }
        return moved;
    }

    private static PixelRect PrimaryWorkAreaPixels()
    {
        var primary = MonitorCatalog.GetAll().FirstOrDefault(m => m.IsPrimary);
        if (primary is not null)
        {
            return primary.WorkingArea;
        }

        var area = SystemParameters.WorkArea;
        return new PixelRect(
            (int)Math.Round(area.Left),
            (int)Math.Round(area.Top),
            (int)Math.Round(area.Right),
            (int)Math.Round(area.Bottom));
    }
}
