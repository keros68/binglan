using System.Windows;
using System.Windows.Interop;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.App.Windows;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Services;

namespace BingLan.App.Services;

/// <summary>
/// Ownership metadata attached to every window managed on a secondary monitor.
/// </summary>
internal sealed record WindowScreenTag(
    string ScreenKey,
    DesktopComponentKind? BuiltInKind);

internal sealed class MultiScreenCoordinatorCallbacks
{
    public required Func<InformationWidgetState> GetInformationState { get; init; }
    public required Func<InformationWidgetState, DesktopExperienceState, DesktopComponentKind, InformationWidgetWindow> CreateInformationWindow { get; init; }
    public required Func<TodoWidgetState, DesktopComponentState?, TodoWidgetWindow> CreateTodoWindow { get; init; }
    public required Func<NoteWidgetState, NoteWidgetWindow> CreateNoteWindow { get; init; }
    public required Func<IReadOnlyList<FileBoxState>, FileBoxWindow> CreateFileBoxWindow { get; init; }
    public required Func<QuickPlacesWindow> CreateQuickPlacesWindow { get; init; }
    public required Action<TodoWidgetWindow, DesktopComponentState> ApplyTodoComponent { get; init; }
    public required Action<TodoWidgetState, DesktopComponentState> CopyTodoLookFromComponent { get; init; }
    public required Action<DesktopComponentState, TodoWidgetState> SyncComponentFromTodo { get; init; }
    public required Action<WidgetAppearanceState> UseSharedLook { get; init; }
    public required Action ApplyEdgeDock { get; init; }
    public required Action UpdateSamplingMode { get; init; }
    public required Action ScheduleSave { get; init; }
    public required Action SaveNow { get; init; }
    public required Action RefreshSettingsUi { get; init; }
    public required Func<bool> IsDesktopHidden { get; init; }
    public required Action EnsureDesktopVisible { get; init; }
    public required Action<Action> RunSuppressingExperienceChanges { get; init; }
    public Func<string?, DesktopComponentKind, bool>? IsInformationCardSuppressed { get; init; }
}

/// <summary>
/// Lazy-allocated controller owning all desktop card windows on secondary displays.
/// Only instantiated when multi-screen is enabled AND at least two monitors are connected.
/// On monitor disconnect or feature disable, every affected secondary window is explicitly
/// closed (<see cref="Window.Close"/>) and removed rather than hidden, freeing HWND and GPU
/// resources while keeping its persisted JSON profile intact for reconnection.
/// </summary>
internal sealed class MultiScreenHost : IDisposable
{
    private static readonly DesktopComponentKind[] InformationComponentKinds =
    [
        DesktopComponentKind.TimeDate,
        DesktopComponentKind.Greeting,
        DesktopComponentKind.Weather,
        DesktopComponentKind.Performance
    ];

    private readonly AppState _state;
    private readonly List<WidgetWindowBase> _windows;
    private readonly List<WidgetWindowBase> _windowsHiddenFromTray;
    private readonly MultiScreenCoordinatorCallbacks _callbacks;
    private readonly Dictionary<WidgetWindowBase, WindowScreenTag> _secondaryWindows = [];
    private IReadOnlyList<ResolvedMonitor> _connectedMonitors = [];
    private bool _disposed;

    internal MultiScreenHost(
        AppState state,
        List<WidgetWindowBase> windows,
        List<WidgetWindowBase> windowsHiddenFromTray,
        MultiScreenCoordinatorCallbacks callbacks)
    {
        _state = state;
        _windows = windows;
        _windowsHiddenFromTray = windowsHiddenFromTray;
        _callbacks = callbacks;
    }

    internal int SecondaryWindowCount => _secondaryWindows.Count;

    internal IReadOnlyList<ResolvedMonitor> ConnectedMonitors => _connectedMonitors;

    internal bool IsSecondaryWindow(WidgetWindowBase window) =>
        _secondaryWindows.ContainsKey(window);

    internal string? TryGetScreenKey(WidgetWindowBase window) =>
        _secondaryWindows.TryGetValue(window, out var tag) ? tag.ScreenKey : null;

    internal DesktopExperienceState? TryGetExperienceForWindow(WidgetWindowBase window)
    {
        if (!_secondaryWindows.TryGetValue(window, out var tag))
        {
            return null;
        }

        return ScreenProfileAccessor.GetExperience(_state, tag.ScreenKey);
    }

    internal IReadOnlyList<WidgetWindowBase> GetWindowsForSecondaryScreen(string screenKey)
    {
        var normalized = MultiScreenRules.NormalizeScreenKey(screenKey);
        if (normalized is null)
        {
            return [];
        }

        return _secondaryWindows
            .Where(pair => string.Equals(pair.Value.ScreenKey, normalized, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToArray();
    }

    internal void SyncConnectedScreens(IReadOnlyList<ResolvedMonitor> connectedMonitors)
    {
        if (_disposed)
        {
            return;
        }

        _connectedMonitors = connectedMonitors;
        var secondaryByKey = connectedMonitors
            .Where(m => m.IsPersistentIdentity && GetScreenKeyForMonitor(m) is not null)
            .ToDictionary(m => m.MonitorKey, StringComparer.Ordinal);

        // Step 1: Hard-close and destroy windows whose secondary monitor disconnected.
        // Persisted JSON configuration remains intact so replugging restores by MonitorKey,
        // and we must NOT capture OS-clamped primary coordinates during disconnect.
        var disconnectedWindows = _secondaryWindows
            .Where(pair => !secondaryByKey.ContainsKey(pair.Value.ScreenKey))
            .Select(pair => pair.Key)
            .ToList();
        foreach (var window in disconnectedWindows)
        {
            CaptureSecondaryWindow(window, capturePlacement: false);
            DestroySecondaryWindow(window);
        }

        if (secondaryByKey.Count == 0 || !ScreenProfileAccessor.IsMultiScreenEnabled(_state))
        {
            return;
        }

        var secondaryCandidates = secondaryByKey.Values
            .Select(m => new MonitorIdentityCandidate(
                m.MonitorKey,
                m.HardwareId,
                m.DeviceName,
                m.FriendlyName,
                m.DisplayLabel,
                m.LegacyDeviceKey))
            .ToList();
        if (ScreenProfileAccessor.ReconcileConnectedProfileKeys(_state, secondaryCandidates) > 0)
        {
            _callbacks.ScheduleSave();
        }

        var layoutKey = CurrentLayoutKey(connectedMonitors);
        _callbacks.RunSuppressingExperienceChanges(() =>
        {
            using (WindowScreenRecovery.DeferShowRecovery())
            {
                foreach (var monitor in secondaryByKey.Values)
                {
                    if (_state.MultiScreen is null
                        || !_state.MultiScreen.Screens.TryGetValue(monitor.MonitorKey, out var profile))
                    {
                        // Brand-new unfamiliar secondary monitor starts completely blank with zero windows.
                        continue;
                    }

                    profile.LastKnownDeviceName = monitor.DeviceName;
                    profile.DisplayLabel = monitor.DisplayLabel;
                    profile.HardwareModelId = MultiScreenRules.ExtractHardwareModelId(monitor.HardwareId)
                        ?? MultiScreenRules.ExtractHardwareModelId(monitor.MonitorKey)
                        ?? profile.HardwareModelId;
                    profile.FriendlyName = MultiScreenRules.NormalizeFriendlyName(monitor.FriendlyName)
                        ?? profile.FriendlyName;
                    DesktopExperienceRules.Normalize(profile.Experience);
                    profile.QuickPlaces = QuickPlaceRules.Normalize(profile.QuickPlaces);

                    SyncProfileWindows(profile, monitor, layoutKey);
                }
            }
        });

        _callbacks.UpdateSamplingMode();
    }

    internal void ApplyExperienceForScreen(string screenKey, DesktopExperienceState experience, bool saveImmediate)
    {
        var normalized = MultiScreenRules.NormalizeScreenKey(screenKey);
        if (normalized is null)
        {
            return;
        }

        if (_callbacks.IsDesktopHidden())
        {
            _callbacks.EnsureDesktopVisible();
        }

        _callbacks.RunSuppressingExperienceChanges(() =>
        {
            DesktopExperienceRules.Normalize(experience);
            var monitor = FindConnectedMonitor(normalized);
            var profile = ScreenProfileAccessor.EnsureScreenProfile(
                _state,
                normalized,
                monitor?.DeviceName,
                monitor?.DisplayLabel,
                monitor?.HardwareId,
                monitor?.FriendlyName);
            profile.Experience = experience;

            var todoComponent = experience.GetComponent(DesktopComponentKind.Todo);
            if (todoComponent.IsVisible && ScreenProfileAccessor.GetTodosForScreen(_state, normalized).Count == 0)
            {
                var created = TodoService.CreateDefaultWidget();
                created.Title = WidgetTitleRules.NextTitle(
                    created.Title,
                    _state.TodoWidgets.Select(item => item.Title));
                created.ScreenKey = normalized;
                _callbacks.CopyTodoLookFromComponent(created, todoComponent);
                created.IsLocked = todoComponent.IsLocked;
                if (monitor is not null)
                {
                    var layoutKey = CurrentLayoutKey(_connectedMonitors);
                    var (placement, _) = ScreenProfileAccessor.ComputeNewWidgetBoundsForMonitor(
                        monitor.WorkingArea,
                        monitor.Dpi,
                        92d,
                        92d,
                        todoComponent.Placement.Width > 0 ? todoComponent.Placement.Width : 286d,
                        todoComponent.Placement.Height > 0 ? todoComponent.Placement.Height : 336d,
                        0,
                        layoutKey);
                    created.Placement = placement;
                    todoComponent.Placement = ScreenProfileAccessor.ClonePlacement(placement);
                }
                else
                {
                    created.Placement = ScreenProfileAccessor.ClonePlacement(todoComponent.Placement);
                }

                _state.TodoWidgets.Add(created);
                profile.PrimaryTodoId = created.Id.ToString("D");
            }
            else if (ScreenProfileAccessor.GetPrimaryTodoId(_state, normalized) is { } id
                && _state.TodoWidgets.FirstOrDefault(todo => todo.Id == id) is { } firstTodo)
            {
                firstTodo.Placement = ScreenProfileAccessor.ClonePlacement(todoComponent.Placement);
            }

            if (monitor is not null)
            {
                using (WindowScreenRecovery.DeferShowRecovery())
                {
                    SyncProfileWindows(profile, monitor, CurrentLayoutKey(_connectedMonitors));
                }
            }

            _callbacks.UpdateSamplingMode();
            if (saveImmediate)
            {
                _callbacks.SaveNow();
            }
            else
            {
                _callbacks.ScheduleSave();
            }
        });
    }

    internal void ApplyLayoutPresetForScreen(string screenKey, DesktopExperienceState experience)
    {
        var normalized = MultiScreenRules.NormalizeScreenKey(screenKey);
        if (normalized is null)
        {
            return;
        }

        var monitor = FindConnectedMonitor(normalized);
        var profile = ScreenProfileAccessor.EnsureScreenProfile(
            _state,
            normalized,
            monitor?.DeviceName,
            monitor?.DisplayLabel,
            monitor?.HardwareId,
            monitor?.FriendlyName);
        DesktopExperienceRules.Normalize(experience);
        profile.Experience = experience;

        if (monitor is not null)
        {
            var layoutKey = CurrentLayoutKey(_connectedMonitors);
            var pixelMap = ScreenProfileAccessor.ApplyPresetToScreen(
                _state,
                normalized,
                experience.ActivePreset,
                monitor.WorkingArea,
                monitor.Dpi,
                layoutKey);
            ApplyExperienceForScreen(normalized, profile.Experience, saveImmediate: false);
            ApplyPresetPixelMapToLiveWindows(normalized, monitor, pixelMap, layoutKey);
            _callbacks.SaveNow();
        }
        else
        {
            ApplyExperienceForScreen(normalized, profile.Experience, saveImmediate: true);
        }
    }

    internal void ApplyQuickPlacesForScreen(string screenKey, List<QuickPlaceState> items)
    {
        var normalized = MultiScreenRules.NormalizeScreenKey(screenKey);
        if (normalized is null)
        {
            return;
        }

        var monitor = FindConnectedMonitor(normalized);
        var profile = ScreenProfileAccessor.EnsureScreenProfile(
            _state,
            normalized,
            monitor?.DeviceName,
            monitor?.DisplayLabel,
            monitor?.HardwareId,
            monitor?.FriendlyName);
        profile.QuickPlaces = QuickPlaceRules.Normalize(items);

        if (monitor is not null)
        {
            using (WindowScreenRecovery.DeferShowRecovery())
            {
                SyncQuickPlacesWindow(profile, monitor, CurrentLayoutKey(_connectedMonitors));
            }
        }
        _callbacks.ScheduleSave();
    }

    internal string CopyPrimaryProfileToScreen(string targetScreenKey)
    {
        var normalized = MultiScreenRules.NormalizeScreenKey(targetScreenKey);
        if (normalized is null)
        {
            return "请选择一个副屏";
        }

        var monitor = FindConnectedMonitor(normalized);
        if (monitor is null)
        {
            return "该显示器当前未连接，无法复制方案";
        }

        if (_callbacks.IsDesktopHidden())
        {
            _callbacks.EnsureDesktopVisible();
        }

        var layoutKey = CurrentLayoutKey(_connectedMonitors);
        var result = ScreenProfileAccessor.CopyPrimaryProfileToScreen(
            _state,
            normalized,
            monitor.WorkingArea,
            monitor.Dpi,
            monitor.DeviceName,
            monitor.DisplayLabel,
            layoutKey);

        _callbacks.RunSuppressingExperienceChanges(() =>
        {
            using (WindowScreenRecovery.DeferShowRecovery())
            {
                SyncProfileWindows(result.Profile, monitor, layoutKey);
                ApplyPresetPixelMapToLiveWindows(normalized, monitor, result.ComponentPixelBounds, layoutKey);
            }
        });

        _callbacks.UpdateSamplingMode();
        _callbacks.SaveNow();
        return $"已将主屏桌面卡片方案复制到 {monitor.DisplayLabel}";
    }

    internal WidgetWindowBase? AddWidgetOnScreen(string screenKey, NewWidgetKind kind)
    {
        var normalized = MultiScreenRules.NormalizeScreenKey(screenKey);
        if (normalized is null)
        {
            return null;
        }

        var monitor = FindConnectedMonitor(normalized);
        if (monitor is null)
        {
            return null;
        }

        var profile = ScreenProfileAccessor.EnsureScreenProfile(
            _state,
            normalized,
            monitor.DeviceName,
            monitor.DisplayLabel,
            monitor.HardwareId,
            monitor.FriendlyName);
        var layoutKey = CurrentLayoutKey(_connectedMonitors);

        WidgetWindowBase? createdWindow = null;
        _callbacks.RunSuppressingExperienceChanges(() =>
        {
            using (WindowScreenRecovery.DeferShowRecovery())
            {
                switch (kind)
                {
                    case NewWidgetKind.Todo:
                    {
                        var todoComponent = profile.Experience.GetComponent(DesktopComponentKind.Todo);
                        todoComponent.IsVisible = true;
                        var existingCount = ScreenProfileAccessor.GetTodosForScreen(_state, normalized).Count;
                        var todo = TodoService.CreateDefaultWidget();
                        todo.Title = WidgetTitleRules.NextTitle(
                            todo.Title,
                            _state.TodoWidgets.Select(item => item.Title));
                        todo.ScreenKey = normalized;
                        _callbacks.CopyTodoLookFromComponent(todo, todoComponent);
                        var (placement, pixelBounds) = ScreenProfileAccessor.ComputeNewWidgetBoundsForMonitor(
                            monitor.WorkingArea,
                            monitor.Dpi,
                            92d,
                            92d,
                            286d,
                            336d,
                            existingCount,
                            layoutKey);
                        todo.Placement = placement;
                        var isScreenPrimary = existingCount == 0 || profile.ParsedPrimaryTodoId is null;
                        if (isScreenPrimary)
                        {
                            todo.IsLocked = todoComponent.IsLocked;
                            profile.PrimaryTodoId = todo.Id.ToString("D");
                        }

                        _state.TodoWidgets.Add(todo);
                        var window = _callbacks.CreateTodoWindow(todo, isScreenPrimary ? todoComponent : null);
                        _secondaryWindows[window] = new WindowScreenTag(normalized, null);
                        window.Show();
                        ApplyPhysicalBounds(window, pixelBounds, monitor.Dpi, todo.Placement, layoutKey, monitor.WorkingArea);
                        if (isScreenPrimary)
                        {
                            todoComponent.Placement = ScreenProfileAccessor.ClonePlacement(todo.Placement);
                        }
                        createdWindow = window;
                        break;
                    }
                    case NewWidgetKind.Note:
                    {
                        var existingCount = ScreenProfileAccessor.GetNotesForScreen(_state, normalized).Count;
                        var note = NoteService.CreateDefaultWidget();
                        note.Title = WidgetTitleRules.NextTitle(
                            note.Title,
                            _state.NoteWidgets.Select(item => item.Title));
                        note.ScreenKey = normalized;
                        _callbacks.UseSharedLook(note.Appearance);
                        note.BodyTypography.Color = note.Appearance.TextColor;
                        var (placement, pixelBounds) = ScreenProfileAccessor.ComputeNewWidgetBoundsForMonitor(
                            monitor.WorkingArea,
                            monitor.Dpi,
                            232d,
                            148d,
                            306d,
                            266d,
                            existingCount,
                            layoutKey);
                        note.Placement = placement;
                        _state.NoteWidgets.Add(note);
                        var window = _callbacks.CreateNoteWindow(note);
                        _secondaryWindows[window] = new WindowScreenTag(normalized, null);
                        window.Show();
                        ApplyPhysicalBounds(window, pixelBounds, monitor.Dpi, note.Placement, layoutKey, monitor.WorkingArea);
                        createdWindow = window;
                        break;
                    }
                    case NewWidgetKind.FileBox:
                    {
                        var existingCount = ScreenProfileAccessor.GetFileBoxesForScreen(_state, normalized).Count;
                        var box = new FileBoxState { ScreenKey = normalized };
                        box.Title = WidgetTitleRules.NextTitle(
                            box.Title,
                            _state.FileBoxes.Select(item => item.Title));
                        _callbacks.UseSharedLook(box.Appearance);
                        var (placement, pixelBounds) = ScreenProfileAccessor.ComputeNewWidgetBoundsForMonitor(
                            monitor.WorkingArea,
                            monitor.Dpi,
                            432d,
                            92d,
                            366d,
                            306d,
                            existingCount,
                            layoutKey);
                        box.Placement = placement;
                        _state.FileBoxes.Add(box);
                        var window = _callbacks.CreateFileBoxWindow([box]);
                        _secondaryWindows[window] = new WindowScreenTag(normalized, null);
                        window.Show();
                        ApplyPhysicalBounds(window, pixelBounds, monitor.Dpi, box.Placement, layoutKey, monitor.WorkingArea);
                        createdWindow = window;
                        break;
                    }
                }
            }
        });

        _callbacks.ScheduleSave();
        return createdWindow;
    }

    internal bool ResetPlacementForScreen(
        string screenKey,
        WidgetWindowBase? window,
        DesktopComponentKind? kind)
    {
        var normalized = MultiScreenRules.NormalizeScreenKey(screenKey);
        if (normalized is null)
        {
            return false;
        }

        var monitor = FindConnectedMonitor(normalized);
        if (monitor is null)
        {
            return false;
        }

        var profile = ScreenProfileAccessor.EnsureScreenProfile(
            _state,
            normalized,
            monitor.DeviceName,
            monitor.DisplayLabel);
        var layoutKey = CurrentLayoutKey(_connectedMonitors);

        if (kind is { } componentKind)
        {
            var presets = ScreenProfileAccessor.ComputePresetPlacementsForMonitor(
                profile.Experience.ActivePreset,
                monitor.WorkingArea,
                monitor.Dpi,
                layoutKey);
            if (presets.TryGetValue(componentKind, out var preset))
            {
                profile.Experience.GetComponent(componentKind).Placement = preset.Placement;
                if (componentKind == DesktopComponentKind.Todo
                    && ScreenProfileAccessor.GetPrimaryTodoId(_state, normalized) is { } primaryTodoId
                    && _state.TodoWidgets.FirstOrDefault(item => item.Id == primaryTodoId) is { } primaryTodo)
                {
                    primaryTodo.Placement = ScreenProfileAccessor.ClonePlacement(preset.Placement);
                }

                ApplyExperienceForScreen(normalized, profile.Experience, saveImmediate: false);
                var targetWin = FindLiveComponentWindow(normalized, componentKind);
                if (targetWin is not null && targetWin.IsVisible)
                {
                    ApplyPhysicalBounds(
                        targetWin,
                        preset.PixelBounds,
                        monitor.Dpi,
                        profile.Experience.GetComponent(componentKind).Placement,
                        layoutKey,
                        monitor.WorkingArea);
                }
                _callbacks.SaveNow();
                return true;
            }
        }

        var (placement, defaultLeft, defaultTop, fallbackW, fallbackH) = window switch
        {
            NoteWidgetWindow note => (note.State.Placement, 232d, 148d, 306d, 266d),
            FileBoxWindow box => (box.State.Placement, 432d, 92d, 366d, 306d),
            TodoWidgetWindow todo => (todo.State.Placement, 92d, 92d, 286d, 336d),
            _ => (null, 0d, 0d, 0d, 0d)
        };
        if (window is null || placement is null)
        {
            return false;
        }

        CaptureSecondaryWindow(window);
        var widthDip = placement.Width > 0 ? placement.Width : fallbackW;
        var heightDip = placement.Height > 0 ? placement.Height : fallbackH;
        var targetPx = ScreenProfileAccessor.LocalDipToPhysicalBounds(
            defaultLeft,
            defaultTop,
            widthDip,
            heightDip,
            monitor.WorkingArea,
            monitor.Dpi);
        ApplyPhysicalBounds(window, targetPx, monitor.Dpi, placement, layoutKey, monitor.WorkingArea);
        if (window is FileBoxWindow fileBox)
        {
            fileBox.SyncGroupSharedProperties();
        }
        _callbacks.SaveNow();
        return true;
    }

    internal void UntrackSecondaryWindow(WidgetWindowBase window) =>
        _secondaryWindows.Remove(window);

    internal string? GetScreenKeyForMonitor(ResolvedMonitor monitor) =>
        string.Equals(monitor.MonitorKey, _state.MultiScreen?.RootMonitorKey, StringComparison.Ordinal)
            ? null
            : monitor.MonitorKey;

    private ResolvedMonitor? FindRootMonitor() =>
        _connectedMonitors.FirstOrDefault(m => m.IsPersistentIdentity && GetScreenKeyForMonitor(m) is null);

    internal FileBoxWindow? ShowDetachedFileBoxOnScreen(string screenKey, FileBoxState box)
    {
        var normalized = MultiScreenRules.NormalizeScreenKey(screenKey);
        if (normalized is null)
        {
            return null;
        }

        box.ScreenKey = normalized;
        var window = _callbacks.CreateFileBoxWindow([box]);
        _secondaryWindows[window] = new WindowScreenTag(normalized, null);
        var monitor = FindConnectedMonitor(normalized);
        if (monitor is not null && !box.IsHidden && !_callbacks.IsDesktopHidden())
        {
            var layoutKey = CurrentLayoutKey(_connectedMonitors);
            var targetPx = ResolveTargetPixelBounds(box.Placement, monitor, layoutKey);
            using (WindowScreenRecovery.DeferShowRecovery())
            {
                window.Show();
                ApplyPhysicalBounds(window, targetPx, monitor.Dpi, box.Placement, layoutKey, monitor.WorkingArea);
            }
        }
        return window;
    }

    internal MoveWidgetResult MoveWindowToScreen(
        WidgetWindowBase window,
        string? targetScreenKey,
        IReadOnlyList<ResolvedMonitor> allMonitors)
    {
        _connectedMonitors = allMonitors;
        var sourceKey = TryGetScreenKey(window);
        var destKey = MultiScreenRules.NormalizeScreenKey(targetScreenKey);
        if (string.Equals(sourceKey, destKey, StringComparison.Ordinal))
        {
            return new MoveWidgetResult(false, "该卡片已位于目标屏幕", window);
        }

        var targetMonitor = destKey is null ? FindRootMonitor() : FindConnectedMonitor(destKey);
        if (targetMonitor is null)
        {
            return new MoveWidgetResult(false, "目标屏幕当前未连接", null);
        }

        var sourceMonitor = sourceKey is null ? FindRootMonitor() : FindConnectedMonitor(sourceKey);
        var sourceBounds = TryReadWindowPixelBounds(window);
        var sourceWork = sourceMonitor?.WorkingArea;
        var sourceDpi = sourceMonitor?.Dpi ?? 96u;
        var layoutKey = CurrentLayoutKey(allMonitors);
        var targetWorkArea = targetMonitor.WorkingArea;

        if (destKey is not null)
        {
            ScreenProfileAccessor.EnsureScreenProfile(
                _state,
                destKey,
                targetMonitor.DeviceName,
                targetMonitor.DisplayLabel,
                targetMonitor.HardwareId,
                targetMonitor.FriendlyName);
        }

        switch (window)
        {
            case TodoWidgetWindow todo:
            {
                var movedPx = ScreenProfileAccessor.MoveTodoToScreen(
                    _state,
                    todo.State.Id,
                    destKey,
                    targetMonitor.WorkingArea,
                    targetMonitor.Dpi,
                    sourceBounds,
                    sourceWork,
                    sourceDpi,
                    layoutKey);
                if (movedPx is not { } targetPx)
                {
                    return new MoveWidgetResult(false, "未能移动该待办卡片", null);
                }

                UpdateWindowScreenTag(todo, destKey, builtInKind: null);
                _callbacks.RunSuppressingExperienceChanges(() =>
                {
                    using (WindowScreenRecovery.DeferShowRecovery())
                    {
                        if (!todo.IsVisible)
                        {
                            todo.Show();
                        }
                        ApplyPhysicalBounds(todo, targetPx, targetMonitor.Dpi, todo.State.Placement, layoutKey, targetWorkArea);
                    }
                });
                if (destKey is not null && ScreenProfileAccessor.GetPrimaryTodoId(_state, destKey) == todo.State.Id)
                {
                    var destProfile = ScreenProfileAccessor.EnsureScreenProfile(_state, destKey);
                    destProfile.Experience.GetComponent(DesktopComponentKind.Todo).Placement =
                        ScreenProfileAccessor.ClonePlacement(todo.State.Placement);
                }
                else if (destKey is null && ScreenProfileAccessor.GetPrimaryTodoId(_state, screenKey: null) == todo.State.Id)
                {
                    _state.DesktopExperience.GetComponent(DesktopComponentKind.Todo).Placement =
                        ScreenProfileAccessor.ClonePlacement(todo.State.Placement);
                }
                _callbacks.SaveNow();
                return new MoveWidgetResult(true, $"已将“{todo.State.Title}”移至 {targetMonitor.DisplayLabel}", todo);
            }
            case NoteWidgetWindow note:
            {
                var movedPx = ScreenProfileAccessor.MoveNoteToScreen(
                    _state,
                    note.State.Id,
                    destKey,
                    targetMonitor.WorkingArea,
                    targetMonitor.Dpi,
                    sourceBounds,
                    sourceWork,
                    sourceDpi,
                    layoutKey);
                if (movedPx is not { } targetPx)
                {
                    return new MoveWidgetResult(false, "未能移动该便签", null);
                }

                UpdateWindowScreenTag(note, destKey, builtInKind: null);
                _callbacks.RunSuppressingExperienceChanges(() =>
                {
                    using (WindowScreenRecovery.DeferShowRecovery())
                    {
                        if (!note.State.IsHidden && !note.IsVisible)
                        {
                            note.Show();
                        }
                        if (note.IsVisible)
                        {
                            ApplyPhysicalBounds(note, targetPx, targetMonitor.Dpi, note.State.Placement, layoutKey, targetWorkArea);
                        }
                    }
                });
                _callbacks.SaveNow();
                return new MoveWidgetResult(true, $"已将“{note.State.Title}”移至 {targetMonitor.DisplayLabel}", note);
            }
            case FileBoxWindow box:
            {
                var movedPx = ScreenProfileAccessor.MoveFileBoxToScreen(
                    _state,
                    box.State.Id,
                    destKey,
                    targetMonitor.WorkingArea,
                    targetMonitor.Dpi,
                    sourceBounds,
                    sourceWork,
                    sourceDpi,
                    layoutKey);
                if (movedPx is not { } targetPx)
                {
                    return new MoveWidgetResult(false, "未能移动该文件收纳盒", null);
                }

                UpdateWindowScreenTag(box, destKey, builtInKind: null);
                _callbacks.RunSuppressingExperienceChanges(() =>
                {
                    using (WindowScreenRecovery.DeferShowRecovery())
                    {
                        if (!box.State.IsHidden && !box.IsVisible)
                        {
                            box.Show();
                        }
                        if (box.IsVisible)
                        {
                            ApplyPhysicalBounds(box, targetPx, targetMonitor.Dpi, box.State.Placement, layoutKey, targetWorkArea);
                        }
                        box.SyncGroupSharedProperties();
                    }
                });
                _callbacks.ApplyEdgeDock();
                _callbacks.SaveNow();
                return new MoveWidgetResult(true, $"已将“{box.State.Title}”移至 {targetMonitor.DisplayLabel}", box);
            }
            case InformationWidgetWindow { ComponentKind: { } infoKind } infoWindow:
            {
                var destExperience = ScreenProfileAccessor.GetExperience(_state, destKey);
                var destComponent = destExperience.GetComponent(infoKind);
                if (destComponent.IsVisible)
                {
                    return new MoveWidgetResult(
                        false,
                        $"{targetMonitor.DisplayLabel} 已开启“{DesktopExperienceRules.GetComponentName(infoKind)}”",
                        null);
                }

                var srcExperience = ScreenProfileAccessor.GetExperience(_state, sourceKey);
                var srcComponent = srcExperience.GetComponent(infoKind);
                CopyComponentSettings(destComponent, srcComponent);
                destComponent.IsVisible = true;
                srcComponent.IsVisible = false;
                if (destKey is null)
                {
                    destComponent.Placement.MonitorLocal = null;
                }

                var targetPx = ScreenProfileAccessor.ComputeMovedWidgetPixelBounds(
                    sourceBounds,
                    sourceWork,
                    sourceDpi,
                    targetMonitor.WorkingArea,
                    targetMonitor.Dpi,
                    srcComponent.Placement.Width,
                    srcComponent.Placement.Height);

                if (destKey is null)
                {
                    RetireOtherPrimaryComponentWindows(infoWindow, infoKind);
                }
                UpdateWindowScreenTag(infoWindow, destKey, infoKind);
                _callbacks.RunSuppressingExperienceChanges(() =>
                {
                    infoWindow.ApplyDesktopExperience(destExperience);
                    using (WindowScreenRecovery.DeferShowRecovery())
                    {
                        if (!infoWindow.IsVisible)
                        {
                            infoWindow.Show();
                        }
                        ApplyPhysicalBounds(infoWindow, targetPx, targetMonitor.Dpi, destComponent.Placement, layoutKey, targetWorkArea);
                    }
                });
                _callbacks.SaveNow();
                return new MoveWidgetResult(
                    true,
                    $"已将“{DesktopExperienceRules.GetComponentName(infoKind)}”移至 {targetMonitor.DisplayLabel}",
                    infoWindow);
            }
            case QuickPlacesWindow quickWindow:
            {
                var destExperience = ScreenProfileAccessor.GetExperience(_state, destKey);
                var destComponent = destExperience.GetComponent(DesktopComponentKind.QuickLaunch);
                if (destComponent.IsVisible)
                {
                    return new MoveWidgetResult(
                        false,
                        $"{targetMonitor.DisplayLabel} 已开启“快捷入口”",
                        null);
                }

                var srcExperience = ScreenProfileAccessor.GetExperience(_state, sourceKey);
                var srcComponent = srcExperience.GetComponent(DesktopComponentKind.QuickLaunch);
                CopyComponentSettings(destComponent, srcComponent);
                destComponent.IsVisible = true;
                srcComponent.IsVisible = false;
                if (destKey is null)
                {
                    destComponent.Placement.MonitorLocal = null;
                }

                var targetPx = ScreenProfileAccessor.ComputeMovedWidgetPixelBounds(
                    sourceBounds,
                    sourceWork,
                    sourceDpi,
                    targetMonitor.WorkingArea,
                    targetMonitor.Dpi,
                    srcComponent.Placement.Width,
                    srcComponent.Placement.Height);

                var movedPlaces = ScreenProfileAccessor.MoveQuickPlacesToScreen(_state, sourceKey, destKey);
                if (destKey is null)
                {
                    RetireOtherPrimaryComponentWindows(quickWindow, DesktopComponentKind.QuickLaunch);
                }
                UpdateWindowScreenTag(quickWindow, destKey, DesktopComponentKind.QuickLaunch);
                _callbacks.RunSuppressingExperienceChanges(() =>
                {
                    quickWindow.SetItems(movedPlaces);
                    quickWindow.ApplyComponent(destComponent);
                    using (WindowScreenRecovery.DeferShowRecovery())
                    {
                        if (!quickWindow.IsVisible)
                        {
                            quickWindow.Show();
                        }
                        ApplyPhysicalBounds(quickWindow, targetPx, targetMonitor.Dpi, destComponent.Placement, layoutKey, targetWorkArea);
                    }
                });
                _callbacks.SaveNow();
                return new MoveWidgetResult(true, $"已将“快捷入口”移至 {targetMonitor.DisplayLabel}", quickWindow);
            }
            default:
                return new MoveWidgetResult(false, "当前卡片不支持跨屏移动", null);
        }
    }

    internal bool IsMonitorStillLive(string? screenKey)
    {
        if (WindowScreenRecovery.IsLayoutTransitioning())
        {
            return false;
        }

        var normalized = MultiScreenRules.NormalizeScreenKey(screenKey);
        if (normalized is null)
        {
            return true;
        }

        var monitor = FindConnectedMonitor(normalized);
        return monitor is not null && MonitorIdentityResolver.IsDeviceConnected(monitor.DeviceName);
    }

    /// <summary>
    /// Detects when the user dragged a card onto another connected monitor and updates its
    /// persisted <c>ScreenKey</c> ownership automatically.
    /// </summary>
    internal bool ReconcileWindowScreenAfterMove(WidgetWindowBase window)
    {
        if (_disposed
            || !window.IsVisible
            || _connectedMonitors.Count < 2
            || WindowScreenRecovery.IsLayoutTransitioning()
            || !MonitorIdentityResolver.HasMultipleMonitors())
        {
            return false;
        }

        var previousKey = TryGetScreenKey(window);
        if (previousKey is not null && !IsMonitorStillLive(previousKey))
        {
            return false;
        }

        if (TryReadWindowPixelBounds(window) is not { HasArea: true } bounds)
        {
            return false;
        }

        var landingMonitor = MonitorIdentityResolver.FindByBounds(_connectedMonitors, bounds);
        if (landingMonitor is not { IsPersistentIdentity: true })
        {
            return false;
        }

        var destinationKey = GetScreenKeyForMonitor(landingMonitor);
        if (string.Equals(previousKey, destinationKey, StringComparison.Ordinal))
        {
            return false;
        }

        if (destinationKey is not null)
        {
            ScreenProfileAccessor.EnsureScreenProfile(
                _state,
                destinationKey,
                landingMonitor.DeviceName,
                landingMonitor.DisplayLabel,
                landingMonitor.HardwareId,
                landingMonitor.FriendlyName);
        }

        var landingLayoutKey = CurrentLayoutKey(_connectedMonitors);

        void UpdateMonitorLocalForLanding(WindowPlacement targetPlacement)
        {
            DisplayLayoutRules.Remember(targetPlacement, landingLayoutKey, bounds);
            ScreenProfileAccessor.RecordMonitorLocalPlacement(
                targetPlacement, bounds, landingMonitor.WorkingArea, landingMonitor.Dpi);
        }

        switch (window)
        {
            case TodoWidgetWindow todo:
                ScreenProfileAccessor.ReconcileTodoOwnershipAfterMove(_state, todo.State, previousKey, destinationKey);
                UpdateMonitorLocalForLanding(todo.State.Placement);
                if (destinationKey is not null && ScreenProfileAccessor.GetPrimaryTodoId(_state, destinationKey) == todo.State.Id)
                {
                    ScreenProfileAccessor.EnsureScreenProfile(_state, destinationKey)
                        .Experience.GetComponent(DesktopComponentKind.Todo).Placement =
                            ScreenProfileAccessor.ClonePlacement(todo.State.Placement);
                }
                else if (destinationKey is null && ScreenProfileAccessor.GetPrimaryTodoId(_state, screenKey: null) == todo.State.Id)
                {
                    _state.DesktopExperience.GetComponent(DesktopComponentKind.Todo).Placement =
                        ScreenProfileAccessor.ClonePlacement(todo.State.Placement);
                }
                UpdateWindowScreenTag(todo, destinationKey, builtInKind: null);
                _callbacks.RefreshSettingsUi();
                return true;

            case NoteWidgetWindow note:
                note.State.ScreenKey = destinationKey;
                UpdateMonitorLocalForLanding(note.State.Placement);
                UpdateWindowScreenTag(note, destinationKey, builtInKind: null);
                _callbacks.RefreshSettingsUi();
                return true;

            case FileBoxWindow box:
                foreach (var tab in box.Tabs)
                {
                    tab.ScreenKey = destinationKey;
                    // Moving a file box onto another monitor clears any monitor-specific edge dock anchor.
                    tab.EdgeDockSide = null;
                    tab.EdgeDockOffsetDip = null;
                    tab.EdgeDockMonitorDeviceName = null;
                }
                UpdateMonitorLocalForLanding(box.State.Placement);
                box.SyncGroupSharedProperties();
                UpdateWindowScreenTag(box, destinationKey, builtInKind: null);
                _callbacks.ApplyEdgeDock();
                _callbacks.RefreshSettingsUi();
                return true;

            case InformationWidgetWindow { ComponentKind: { } infoKind } infoWindow:
            {
                var destExperience = ScreenProfileAccessor.GetExperience(_state, destinationKey);
                var destComponent = destExperience.GetComponent(infoKind);
                if (destComponent.IsVisible)
                {
                    RestoreDeclinedBuiltInDrop(infoWindow, previousKey, infoKind, landingLayoutKey);
                    return false;
                }

                var srcExperience = ScreenProfileAccessor.GetExperience(_state, previousKey);
                var srcComponent = srcExperience.GetComponent(infoKind);
                CopyComponentSettings(destComponent, srcComponent);
                destComponent.IsVisible = true;
                srcComponent.IsVisible = false;
                UpdateMonitorLocalForLanding(destComponent.Placement);
                if (destinationKey is null)
                {
                    RetireOtherPrimaryComponentWindows(infoWindow, infoKind);
                }
                UpdateWindowScreenTag(infoWindow, destinationKey, infoKind);
                infoWindow.ApplyDesktopExperience(destExperience);
                _callbacks.RefreshSettingsUi();
                return true;
            }
            case QuickPlacesWindow quickWindow:
            {
                var destExperience = ScreenProfileAccessor.GetExperience(_state, destinationKey);
                var destComponent = destExperience.GetComponent(DesktopComponentKind.QuickLaunch);
                if (destComponent.IsVisible)
                {
                    RestoreDeclinedBuiltInDrop(quickWindow, previousKey, DesktopComponentKind.QuickLaunch, landingLayoutKey);
                    return false;
                }

                var srcExperience = ScreenProfileAccessor.GetExperience(_state, previousKey);
                var srcComponent = srcExperience.GetComponent(DesktopComponentKind.QuickLaunch);
                CopyComponentSettings(destComponent, srcComponent);
                destComponent.IsVisible = true;
                srcComponent.IsVisible = false;
                UpdateMonitorLocalForLanding(destComponent.Placement);
                var movedPlaces = ScreenProfileAccessor.MoveQuickPlacesToScreen(_state, previousKey, destinationKey);
                if (destinationKey is null)
                {
                    RetireOtherPrimaryComponentWindows(quickWindow, DesktopComponentKind.QuickLaunch);
                }
                UpdateWindowScreenTag(quickWindow, destinationKey, DesktopComponentKind.QuickLaunch);
                quickWindow.SetItems(movedPlaces);
                quickWindow.ApplyComponent(destComponent);
                _callbacks.RefreshSettingsUi();
                return true;
            }
            default:
                return false;
        }
    }

    private void RestoreDeclinedBuiltInDrop(
        WidgetWindowBase window,
        string? previousKey,
        DesktopComponentKind kind,
        string layoutKey)
    {
        var sourceMonitor = previousKey is null ? FindRootMonitor() : FindConnectedMonitor(previousKey);
        if (sourceMonitor is null)
        {
            return;
        }

        var srcComponent = ScreenProfileAccessor.GetExperience(_state, previousKey).GetComponent(kind);
        PixelRect restorePx;
        if (window.LastDragStartBounds is { HasArea: true } startBounds
            && MonitorIdentityResolver.FindByBounds(_connectedMonitors, startBounds) is { } startMonitor
            && string.Equals(GetScreenKeyForMonitor(startMonitor), previousKey, StringComparison.Ordinal))
        {
            restorePx = startBounds;
        }
        else
        {
            restorePx = ResolveTargetPixelBounds(srcComponent.Placement, sourceMonitor, layoutKey);
        }

        ApplyPhysicalBounds(
            window,
            restorePx,
            sourceMonitor.Dpi,
            srcComponent.Placement,
            layoutKey,
            sourceMonitor.WorkingArea);
        _callbacks.ScheduleSave();
    }

    internal bool TryShowSecondaryWindow(WidgetWindowBase window)
    {
        if (_disposed || !_secondaryWindows.TryGetValue(window, out var tag))
        {
            return false;
        }

        var monitor = _connectedMonitors.FirstOrDefault(m =>
            string.Equals(m.MonitorKey, tag.ScreenKey, StringComparison.Ordinal));
        if (monitor is null)
        {
            return false;
        }

        var placement = window switch
        {
            NoteWidgetWindow note => note.State.Placement,
            FileBoxWindow box => box.State.Placement,
            TodoWidgetWindow todo => todo.State.Placement,
            _ => null
        };
        if (placement is null)
        {
            return false;
        }

        var layoutKey = CurrentLayoutKey(_connectedMonitors);
        var targetPx = ResolveTargetPixelBounds(placement, monitor, layoutKey);
        using (WindowScreenRecovery.DeferShowRecovery())
        {
            window.Show();
            ApplyPhysicalBounds(window, targetPx, monitor.Dpi, placement, layoutKey, monitor.WorkingArea);
            if (window is not FileBoxWindow { IsEdgeDocked: true })
            {
                window.ApplyReservedEdges(WindowScreenRecovery.ReservedEdgeTarget, layoutKey);
            }
        }
        return true;
    }

    internal bool TryCaptureSecondaryWindow(WidgetWindowBase window)
    {
        if (!_secondaryWindows.TryGetValue(window, out var tag))
        {
            return false;
        }

        var canCapturePlacement = IsMonitorStillLive(tag.ScreenKey);
        if (!canCapturePlacement)
        {
            WindowScreenRecovery.NotifyLayoutChangedIfNeeded();
        }

        CaptureSecondaryWindow(window, canCapturePlacement);
        return true;
    }

    internal bool TryApplyWidgetLock(WidgetWindowBase window, bool isLocked)
    {
        if (!_secondaryWindows.TryGetValue(window, out var tag))
        {
            return false;
        }

        window.ApplyWidgetLock(isLocked);
        if (_state.MultiScreen?.Screens.TryGetValue(tag.ScreenKey, out var profile) == true)
        {
            switch (window)
            {
                case InformationWidgetWindow { ComponentKind: { } kind }:
                    profile.Experience.GetComponent(kind).IsLocked = isLocked;
                    break;
                case QuickPlacesWindow:
                    profile.Experience.GetComponent(DesktopComponentKind.QuickLaunch).IsLocked = isLocked;
                    break;
                case TodoWidgetWindow todo
                    when ScreenProfileAccessor.GetPrimaryTodoId(_state, tag.ScreenKey) == todo.State.Id:
                    profile.Experience.GetComponent(DesktopComponentKind.Todo).IsLocked = isLocked;
                    break;
            }
        }
        return true;
    }

    internal bool TryRemoveSecondaryWindow(WidgetWindowBase window)
    {
        if (!_secondaryWindows.TryGetValue(window, out var tag))
        {
            return false;
        }

        var profile = _state.MultiScreen?.Screens.TryGetValue(tag.ScreenKey, out var found) == true
            ? found
            : null;
        switch (window)
        {
            case TodoWidgetWindow todo:
            {
                var wasScreenPrimary = ScreenProfileAccessor.GetPrimaryTodoId(_state, tag.ScreenKey) == todo.State.Id;
                _state.TodoWidgets.RemoveAll(x => x.Id == todo.State.Id);
                if (profile is not null)
                {
                    var remaining = ScreenProfileAccessor.GetTodosForScreen(_state, tag.ScreenKey);
                    if (remaining.Count == 0)
                    {
                        profile.PrimaryTodoId = null;
                        profile.Experience.GetComponent(DesktopComponentKind.Todo).IsVisible = false;
                    }
                    else if (wasScreenPrimary)
                    {
                        profile.PrimaryTodoId = remaining[0].Id.ToString("D");
                        _callbacks.SyncComponentFromTodo(
                            profile.Experience.GetComponent(DesktopComponentKind.Todo),
                            remaining[0]);
                    }
                }
                break;
            }
            case NoteWidgetWindow note:
                _state.NoteWidgets.RemoveAll(x => x.Id == note.State.Id);
                break;
            case FileBoxWindow box:
            {
                var tabIds = box.Tabs.Select(tab => tab.Id).ToHashSet();
                _state.FileBoxes.RemoveAll(x => tabIds.Contains(x.Id));
                foreach (var tab in box.Tabs)
                {
                    if (tab.DesktopCategory != DesktopGroupCategory.None
                        && !_state.DismissedDesktopCategories.Contains(tab.DesktopCategory))
                    {
                        _state.DismissedDesktopCategories.Add(tab.DesktopCategory);
                    }
                }
                break;
            }
            case QuickPlacesWindow:
                if (profile is not null)
                {
                    profile.Experience.GetComponent(DesktopComponentKind.QuickLaunch).IsVisible = false;
                }
                break;
            case InformationWidgetWindow { ComponentKind: { } infoKind }:
                if (profile is not null)
                {
                    profile.Experience.GetComponent(infoKind).IsVisible = false;
                }
                break;
        }

        DestroySecondaryWindow(window);
        _callbacks.UpdateSamplingMode();
        _callbacks.SaveNow();
        _callbacks.RefreshSettingsUi();
        return true;
    }

    internal void ApplyGlassToSecondaryScreens(DesktopStyleState previousStyle, DesktopStyleState nextStyle)
    {
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

        foreach (var (window, tag) in _secondaryWindows.ToList())
        {
            if (_state.MultiScreen.Screens.TryGetValue(tag.ScreenKey, out var profile))
            {
                switch (window)
                {
                    case InformationWidgetWindow info:
                        info.ApplyDesktopExperience(profile.Experience);
                        info.ApplyStyle(nextStyle);
                        break;
                    case QuickPlacesWindow quick:
                        quick.ApplyComponent(profile.Experience.GetComponent(DesktopComponentKind.QuickLaunch));
                        quick.ApplyStyle(nextStyle);
                        break;
                    case TodoWidgetWindow todo
                        when ScreenProfileAccessor.GetPrimaryTodoId(_state, tag.ScreenKey) == todo.State.Id:
                        var component = profile.Experience.GetComponent(DesktopComponentKind.Todo);
                        _callbacks.CopyTodoLookFromComponent(todo.State, component);
                        _callbacks.ApplyTodoComponent(todo, component);
                        break;
                }
            }
        }
    }

    public void Dispose() => Dispose(capturePlacement: true);

    internal void Dispose(bool capturePlacement)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var window in _secondaryWindows.Keys.ToList())
        {
            CaptureSecondaryWindow(window, capturePlacement);
            DestroySecondaryWindow(window);
        }
        _secondaryWindows.Clear();
        _connectedMonitors = [];
    }

    internal void RefreshInformationCardVisibility()
    {
        var layoutKey = CurrentLayoutKey(_connectedMonitors);
        _callbacks.RunSuppressingExperienceChanges(() =>
        {
            using (WindowScreenRecovery.DeferShowRecovery())
            {
                foreach (var monitor in _connectedMonitors.Where(monitor => monitor.IsPersistentIdentity))
                {
                    if (GetScreenKeyForMonitor(monitor) is { } key
                        && _state.MultiScreen?.Screens.TryGetValue(key, out var profile) == true)
                    {
                        SyncInformationCards(profile, monitor, layoutKey);
                    }
                }
            }
        });
    }

    private void SyncInformationCards(ScreenProfileState profile, ResolvedMonitor monitor, string layoutKey)
    {
        var screenKey = profile.MonitorKey;
        var experience = profile.Experience;
        // 1. Information built-in cards (TimeDate, Greeting, Weather, Performance).
        // Shared InformationWidgets[0] metadata, performance sampler, and weather service.
        foreach (var kind in InformationComponentKinds)
        {
            var component = experience.GetComponent(kind);
            var existing = FindLiveComponentWindow(screenKey, kind) as InformationWidgetWindow;
            if (!component.IsVisible || _callbacks.IsInformationCardSuppressed?.Invoke(screenKey, kind) == true)
            {
                if (existing is not null)
                {
                    CaptureSecondaryWindow(existing);
                    DestroySecondaryWindow(existing);
                }
                continue;
            }

            if (existing is null)
            {
                var infoState = _callbacks.GetInformationState();
                existing = _callbacks.CreateInformationWindow(infoState, experience, kind);
                _secondaryWindows[existing] = new WindowScreenTag(screenKey, kind);
            }

            existing.ApplyDesktopExperience(experience);
            existing.ApplyPlacement(component.Placement, component.IsLocked);
            var targetPx = ResolveTargetPixelBounds(component.Placement, monitor, layoutKey);
            if (!_callbacks.IsDesktopHidden())
            {
                existing.Show();
                ApplyPhysicalBounds(existing, targetPx, monitor.Dpi, component.Placement, layoutKey, monitor.WorkingArea);
                if (kind == DesktopComponentKind.Weather)
                {
                    existing.RefreshWeatherIfNeeded();
                }
            }
        }
    }

    private void SyncProfileWindows(
        ScreenProfileState profile,
        ResolvedMonitor monitor,
        string layoutKey)
    {
        var screenKey = profile.MonitorKey;
        var experience = profile.Experience;
        SyncInformationCards(profile, monitor, layoutKey);

        // 2. Quick launch bar.
        SyncQuickPlacesWindow(profile, monitor, layoutKey);

        // 3. To-do cards owned by this screen.
        var todoComponent = experience.GetComponent(DesktopComponentKind.Todo);
        var screenTodos = ScreenProfileAccessor.GetTodosForScreen(_state, screenKey);
        if (todoComponent.IsVisible && screenTodos.Count == 0)
        {
            todoComponent.IsVisible = false;
        }

        var primaryTodoId = ScreenProfileAccessor.GetPrimaryTodoId(_state, screenKey);
        if (primaryTodoId is { } id)
        {
            profile.PrimaryTodoId = id.ToString("D");
        }

        var activeTodoIds = screenTodos.Select(item => item.Id).ToHashSet();
        foreach (var staleTodo in _secondaryWindows
                     .Where(pair => string.Equals(pair.Value.ScreenKey, screenKey, StringComparison.Ordinal)
                         && pair.Key is TodoWidgetWindow tw
                         && (!todoComponent.IsVisible || !activeTodoIds.Contains(tw.State.Id)))
                     .Select(pair => pair.Key)
                     .ToList())
        {
            CaptureSecondaryWindow(staleTodo);
            DestroySecondaryWindow(staleTodo);
        }

        if (todoComponent.IsVisible)
        {
            foreach (var todo in screenTodos)
            {
                var isScreenPrimary = primaryTodoId == todo.Id;
                var existing = _secondaryWindows
                    .Where(pair => string.Equals(pair.Value.ScreenKey, screenKey, StringComparison.Ordinal))
                    .Select(pair => pair.Key)
                    .OfType<TodoWidgetWindow>()
                    .FirstOrDefault(win => win.State.Id == todo.Id);
                if (existing is null)
                {
                    existing = _callbacks.CreateTodoWindow(todo, isScreenPrimary ? todoComponent : null);
                    _secondaryWindows[existing] = new WindowScreenTag(screenKey, null);
                }
                if (isScreenPrimary)
                {
                    _callbacks.CopyTodoLookFromComponent(todo, todoComponent);
                    _callbacks.ApplyTodoComponent(existing, todoComponent);
                    todo.IsLocked = todoComponent.IsLocked;
                    todo.Placement ??= ScreenProfileAccessor.ClonePlacement(todoComponent.Placement);
                }

                existing.ApplyPlacement(todo.Placement, todo.IsLocked);
                var targetPx = ResolveTargetPixelBounds(todo.Placement, monitor, layoutKey);
                if (!_callbacks.IsDesktopHidden())
                {
                    existing.Show();
                    ApplyPhysicalBounds(existing, targetPx, monitor.Dpi, todo.Placement, layoutKey, monitor.WorkingArea);
                }
                if (isScreenPrimary)
                {
                    todoComponent.Placement = ScreenProfileAccessor.ClonePlacement(todo.Placement);
                }
            }
        }

        // 4. Notes owned by this screen.
        var screenNotes = ScreenProfileAccessor.GetNotesForScreen(_state, screenKey);
        var activeNoteIds = screenNotes.Select(item => item.Id).ToHashSet();
        foreach (var staleNote in _secondaryWindows
                     .Where(pair => string.Equals(pair.Value.ScreenKey, screenKey, StringComparison.Ordinal)
                         && pair.Key is NoteWidgetWindow nw
                         && !activeNoteIds.Contains(nw.State.Id))
                     .Select(pair => pair.Key)
                     .ToList())
        {
            CaptureSecondaryWindow(staleNote);
            DestroySecondaryWindow(staleNote);
        }

        foreach (var note in screenNotes)
        {
            var existing = _secondaryWindows
                .Where(pair => string.Equals(pair.Value.ScreenKey, screenKey, StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .OfType<NoteWidgetWindow>()
                .FirstOrDefault(win => win.State.Id == note.Id);
            if (existing is null)
            {
                existing = _callbacks.CreateNoteWindow(note);
                _secondaryWindows[existing] = new WindowScreenTag(screenKey, null);
            }
            if (!note.IsHidden && !_callbacks.IsDesktopHidden())
            {
                var targetPx = ResolveTargetPixelBounds(note.Placement, monitor, layoutKey);
                existing.Show();
                ApplyPhysicalBounds(existing, targetPx, monitor.Dpi, note.Placement, layoutKey, monitor.WorkingArea);
            }
        }

        // 5. File boxes owned by this screen (single or grouped multi-tab hosts).
        var screenBoxes = ScreenProfileAccessor.GetFileBoxesForScreen(_state, screenKey);
        var activeBoxIds = screenBoxes.Select(item => item.Id).ToHashSet();
        var hostBuckets = FileBoxGroupRules.PartitionIntoHosts(screenBoxes);
        var matchedWindows = new HashSet<FileBoxWindow>(ReferenceEqualityComparer.Instance);
        foreach (var members in hostBuckets)
        {
            var lead = members.FirstOrDefault(m => m.IsActiveTab) ?? members[0];
            var memberIds = members.Select(m => m.Id).ToHashSet();
            var existing = _secondaryWindows
                .Where(pair => string.Equals(pair.Value.ScreenKey, screenKey, StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .OfType<FileBoxWindow>()
                .FirstOrDefault(win => !matchedWindows.Contains(win) && win.Tabs.Any(tab => memberIds.Contains(tab.Id)));
            if (existing is null)
            {
                existing = _callbacks.CreateFileBoxWindow(members);
                _secondaryWindows[existing] = new WindowScreenTag(screenKey, null);
            }
            else if (!existing.Tabs.Select(tab => tab.Id).SequenceEqual(members.Select(m => m.Id)))
            {
                existing.SyncTabsFromStates(members, lead.Id);
            }
            matchedWindows.Add(existing);

            if (!lead.IsHidden && !_callbacks.IsDesktopHidden())
            {
                var targetPx = ResolveTargetPixelBounds(lead.Placement, monitor, layoutKey);
                existing.Show();
                ApplyPhysicalBounds(existing, targetPx, monitor.Dpi, lead.Placement, layoutKey, monitor.WorkingArea);
            }
            FileBoxGroupRules.SyncGroupSharedProperties(lead, members);
        }

        foreach (var staleBox in _secondaryWindows
                     .Where(pair => string.Equals(pair.Value.ScreenKey, screenKey, StringComparison.Ordinal)
                         && pair.Key is FileBoxWindow fb
                         && (!matchedWindows.Contains(fb) || !fb.Tabs.Any(tab => activeBoxIds.Contains(tab.Id))))
                     .Select(pair => pair.Key)
                     .ToList())
        {
            CaptureSecondaryWindow(staleBox);
            DestroySecondaryWindow(staleBox);
        }
    }

    private void SyncQuickPlacesWindow(
        ScreenProfileState profile,
        ResolvedMonitor monitor,
        string layoutKey)
    {
        var component = profile.Experience.GetComponent(DesktopComponentKind.QuickLaunch);
        var existing = FindLiveComponentWindow(profile.MonitorKey, DesktopComponentKind.QuickLaunch) as QuickPlacesWindow;
        if (!component.IsVisible)
        {
            if (existing is not null)
            {
                CaptureSecondaryWindow(existing);
                DestroySecondaryWindow(existing);
            }
            return;
        }

        if (existing is null)
        {
            existing = _callbacks.CreateQuickPlacesWindow();
            _secondaryWindows[existing] = new WindowScreenTag(profile.MonitorKey, DesktopComponentKind.QuickLaunch);
        }

        existing.SetItems(profile.QuickPlaces);
        existing.ApplyComponent(component);
        existing.ApplyPlacement(component.Placement, component.IsLocked);
        var targetPx = ResolveTargetPixelBounds(component.Placement, monitor, layoutKey);
        if (!_callbacks.IsDesktopHidden())
        {
            existing.Show();
            ApplyPhysicalBounds(existing, targetPx, monitor.Dpi, component.Placement, layoutKey, monitor.WorkingArea);
        }
    }

    private void ApplyPresetPixelMapToLiveWindows(
        string screenKey,
        ResolvedMonitor monitor,
        IReadOnlyDictionary<DesktopComponentKind, PixelRect> pixelMap,
        string layoutKey)
    {
        if (_state.MultiScreen is null
            || !_state.MultiScreen.Screens.TryGetValue(screenKey, out var profile))
        {
            return;
        }

        foreach (var (kind, targetPx) in pixelMap)
        {
            var component = profile.Experience.GetComponent(kind);
            var window = FindLiveComponentWindow(screenKey, kind);
            if (window is not null && window.IsVisible)
            {
                ApplyPhysicalBounds(window, targetPx, monitor.Dpi, component.Placement, layoutKey, monitor.WorkingArea);
                if (kind == DesktopComponentKind.Todo && window is TodoWidgetWindow todoWindow)
                {
                    todoWindow.State.Placement = ScreenProfileAccessor.ClonePlacement(component.Placement);
                }
            }
        }
    }

    private WidgetWindowBase? FindLiveComponentWindow(string screenKey, DesktopComponentKind kind)
    {
        if (kind == DesktopComponentKind.Todo)
        {
            var primaryId = ScreenProfileAccessor.GetPrimaryTodoId(_state, screenKey);
            return primaryId is { } id
                ? _secondaryWindows
                    .Where(pair => string.Equals(pair.Value.ScreenKey, screenKey, StringComparison.Ordinal))
                    .Select(pair => pair.Key)
                    .OfType<TodoWidgetWindow>()
                    .FirstOrDefault(window => window.State.Id == id)
                : null;
        }

        return _secondaryWindows
            .FirstOrDefault(pair =>
                string.Equals(pair.Value.ScreenKey, screenKey, StringComparison.Ordinal)
                && pair.Value.BuiltInKind == kind)
            .Key;
    }

    private void CaptureSecondaryWindow(WidgetWindowBase window, bool capturePlacement = true)
    {
        if (!_secondaryWindows.TryGetValue(window, out var tag))
        {
            return;
        }

        var profile = _state.MultiScreen?.Screens.TryGetValue(tag.ScreenKey, out var found) == true
            ? found
            : null;
        if (!capturePlacement)
        {
            using (window.SuppressPlacementCapture())
            {
                switch (window)
                {
                    case TodoWidgetWindow todo:
                        var preservedTodo = ScreenProfileAccessor.ClonePlacement(todo.State.Placement);
                        todo.CaptureState();
                        todo.State.Placement = preservedTodo;
                        break;
                    case NoteWidgetWindow note:
                        var preservedNote = ScreenProfileAccessor.ClonePlacement(note.State.Placement);
                        note.CaptureState();
                        note.State.Placement = preservedNote;
                        break;
                    case FileBoxWindow box:
                        var preservedBox = ScreenProfileAccessor.ClonePlacement(box.State.Placement);
                        box.CaptureState();
                        box.State.Placement = preservedBox;
                        box.SyncGroupSharedProperties();
                        break;
                }
            }
            return;
        }

        var monitor = FindConnectedMonitor(tag.ScreenKey);
        var boundsPx = TryReadWindowPixelBounds(window);
        var layoutKey = CurrentLayoutKey(_connectedMonitors);

        void RecordLocal(WindowPlacement targetPlacement)
        {
            if (monitor is not null && boundsPx is { HasArea: true } px)
            {
                if (!string.IsNullOrEmpty(layoutKey))
                {
                    DisplayLayoutRules.Remember(targetPlacement, layoutKey, px);
                }
                ScreenProfileAccessor.RecordMonitorLocalPlacement(
                    targetPlacement,
                    px,
                    monitor.WorkingArea,
                    monitor.Dpi);
            }
        }

        switch (window)
        {
            case TodoWidgetWindow todo:
                todo.CaptureState();
                RecordLocal(todo.State.Placement);
                if (profile is not null && ScreenProfileAccessor.GetPrimaryTodoId(_state, tag.ScreenKey) == todo.State.Id)
                {
                    var component = profile.Experience.GetComponent(DesktopComponentKind.Todo);
                    component.Placement = ScreenProfileAccessor.ClonePlacement(todo.State.Placement);
                    component.IsLocked = todo.State.IsLocked;
                }
                break;
            case NoteWidgetWindow note:
                note.CaptureState();
                RecordLocal(note.State.Placement);
                break;
            case FileBoxWindow box:
                box.CaptureState();
                if (!box.IsEdgeDocked)
                {
                    RecordLocal(box.State.Placement);
                }
                box.SyncGroupSharedProperties();
                break;
            case InformationWidgetWindow info:
                info.CaptureState();
                if (profile is not null && info.ComponentKind is { } infoKind)
                {
                    var component = profile.Experience.GetComponent(infoKind);
                    info.CopyPlacementTo(component.Placement);
                    RecordLocal(component.Placement);
                    component.IsLocked = info.IsWidgetLocked;
                }
                break;
            case QuickPlacesWindow quick:
                if (profile is not null)
                {
                    var component = profile.Experience.GetComponent(DesktopComponentKind.QuickLaunch);
                    quick.CopyPlacementTo(component.Placement);
                    RecordLocal(component.Placement);
                    component.IsLocked = quick.IsWidgetLocked;
                }
                break;
        }
    }

    private void DestroySecondaryWindow(WidgetWindowBase window)
    {
        _secondaryWindows.Remove(window);
        _windows.Remove(window);
        _windowsHiddenFromTray.Remove(window);
        window.CanClose = true;
        using (window.SuppressPlacementCapture())
        {
            try
            {
                window.Close();
            }
            catch (InvalidOperationException)
            {
                // Window was already closing or never shown.
            }
        }
    }

    private void RetireOtherPrimaryComponentWindows(WidgetWindowBase keptWindow, DesktopComponentKind kind)
    {
        var duplicates = _windows
            .Where(candidate => !ReferenceEquals(candidate, keptWindow)
                && !_secondaryWindows.ContainsKey(candidate)
                && ((kind == DesktopComponentKind.QuickLaunch && candidate is QuickPlacesWindow)
                    || (candidate is InformationWidgetWindow info && info.ComponentKind == kind)))
            .ToList();
        foreach (var duplicate in duplicates)
        {
            _windows.Remove(duplicate);
            _windowsHiddenFromTray.Remove(duplicate);
            duplicate.CanClose = true;
            using (duplicate.SuppressPlacementCapture())
            {
                try
                {
                    duplicate.Close();
                }
                catch (InvalidOperationException)
                {
                    // Window was already closing or never shown.
                }
            }
        }
    }

    private void UpdateWindowScreenTag(
        WidgetWindowBase window,
        string? destinationKey,
        DesktopComponentKind? builtInKind)
    {
        if (destinationKey is null)
        {
            _secondaryWindows.Remove(window);
        }
        else
        {
            _secondaryWindows[window] = new WindowScreenTag(destinationKey, builtInKind);
        }
    }

    private ResolvedMonitor? FindConnectedMonitor(string screenKey) =>
        _connectedMonitors.FirstOrDefault(m =>
            m.IsPersistentIdentity && string.Equals(GetScreenKeyForMonitor(m), screenKey, StringComparison.Ordinal));

    internal static string CurrentLayoutKey(IReadOnlyList<ResolvedMonitor> monitors) =>
        monitors.Count > 0
            ? DisplayLayoutRules.Key(monitors.Select(monitor => (monitor.Bounds, monitor.Dpi)))
            : WindowScreenRecovery.CurrentLayout();

    internal static PixelRect ResolveTargetPixelBounds(
        WindowPlacement placement,
        ResolvedMonitor monitor,
        string layoutKey)
    {
        // Priority 1: exact physical pixel match for the active monitor arrangement.
        if (!string.IsNullOrEmpty(layoutKey)
            && DisplayLayoutRules.TryRecall(placement, layoutKey, out var recalled)
            && recalled.HasArea
            && Intersects(recalled, monitor.Bounds))
        {
            return PlacementResolver.EnsureVisible(
                recalled,
                [monitor.WorkingArea],
                monitor.WorkingArea,
                minimumVisiblePixels: 64);
        }

        // Priority 2: monitor-relative DIP placement (resists left/right rearrangement, DPI, or resolution changes).
        if (ScreenProfileAccessor.ResolveMonitorLocalBounds(placement, monitor.WorkingArea, monitor.Dpi) is { HasArea: true } localPx)
        {
            return localPx;
        }

        // Priority 3: legacy JSON fallback without MonitorLocal metadata.
        var scale = ScreenProfileAccessor.ResolveScale(monitor.Dpi);
        var widthDip = placement.Width > 0d ? placement.Width : 286d;
        var heightDip = placement.Height > 0d ? placement.Height : 260d;
        var candidate = new PixelRect(
            (int)Math.Round(placement.Left * scale),
            (int)Math.Round(placement.Top * scale),
            (int)Math.Round((placement.Left + widthDip) * scale),
            (int)Math.Round((placement.Top + heightDip) * scale));
        if (candidate.HasArea && Intersects(candidate, monitor.Bounds))
        {
            return PlacementResolver.EnsureVisible(
                candidate,
                [monitor.WorkingArea],
                monitor.WorkingArea,
                minimumVisiblePixels: 64);
        }

        return ScreenProfileAccessor.LocalDipToPhysicalBounds(
            92d,
            92d,
            widthDip,
            heightDip,
            monitor.WorkingArea,
            monitor.Dpi);
    }

    /// <summary>
    /// Places a top-level WPF window at exact physical screen coordinates using up to two
    /// <c>SetWindowPos</c> passes so crossing onto a monitor with different DPI or negative
    /// coordinates (e.g. X=2048, Y=-50) locks exact physical pixels after <c>WM_DPICHANGED</c>.
    /// </summary>
    internal static bool ApplyPhysicalBounds(
        WidgetWindowBase window,
        PixelRect targetPx,
        uint targetDpi,
        WindowPlacement? placement = null,
        string? layoutKey = null,
        PixelRect? monitorWorkingArea = null)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            return false;
        }

        var scale = ScreenProfileAccessor.ResolveScale(targetDpi);
        var minWidth = window.MinWidth > 0d ? (int)Math.Ceiling(window.MinWidth * scale) : 1;
        var minHeight = window.MinHeight > 0d ? (int)Math.Ceiling(window.MinHeight * scale) : 1;
        var clamped = new PixelRect(
            targetPx.Left,
            targetPx.Top,
            targetPx.Left + Math.Max(targetPx.Width, minWidth),
            targetPx.Top + Math.Max(targetPx.Height, minHeight));

        var moved = false;
        var alreadyAtTarget = false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (DockNativeMethods.GetWindowRect(handle, out var current)
                && new PixelRect(current.Left, current.Top, current.Right, current.Bottom) == clamped)
            {
                alreadyAtTarget = true;
                break;
            }

            window.ClearLastDragStartBounds();
            moved |= NativeMethods.SetWindowPos(
                handle,
                0,
                clamped.Left,
                clamped.Top,
                clamped.Width,
                clamped.Height,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        }

        if (!moved && !alreadyAtTarget)
        {
            return false;
        }

        if (placement is not null)
        {
            var settled = DockNativeMethods.GetWindowRect(handle, out var actual)
                ? new PixelRect(actual.Left, actual.Top, actual.Right, actual.Bottom)
                : clamped;
            if (!settled.HasArea)
            {
                settled = clamped;
            }
            placement.Left = settled.Left / scale;
            placement.Top = settled.Top / scale;
            placement.Width = settled.Width / scale;
            placement.Height = settled.Height / scale;
            if (!string.IsNullOrEmpty(layoutKey))
            {
                DisplayLayoutRules.Remember(placement, layoutKey, settled);
            }
            if (monitorWorkingArea is { HasArea: true } workArea)
            {
                ScreenProfileAccessor.RecordMonitorLocalPlacement(placement, settled, workArea, targetDpi);
            }
        }

        return moved;
    }

    internal static PixelRect? TryReadWindowPixelBounds(WidgetWindowBase window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !DockNativeMethods.GetWindowRect(handle, out var rect))
        {
            return null;
        }

        var bounds = new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return bounds.HasArea ? bounds : null;
    }

    private static bool Intersects(PixelRect a, PixelRect b) =>
        a.Right > b.Left && a.Left < b.Right && a.Bottom > b.Top && a.Top < b.Bottom;

    private static void CopyComponentSettings(DesktopComponentState target, DesktopComponentState source)
    {
        target.IsLocked = source.IsLocked;
        target.ShowDivider = source.ShowDivider;
        target.DividerThickness = source.DividerThickness;
        target.Density = source.Density;
        target.FontFamily = source.FontFamily;
        target.FontScale = source.FontScale;
        target.CornerRadius = source.CornerRadius;
        target.Appearance.BackgroundColor = source.Appearance.BackgroundColor;
        target.Appearance.BackgroundOpacity = source.Appearance.BackgroundOpacity;
        target.Appearance.HeaderColor = source.Appearance.HeaderColor;
        target.Appearance.HeaderOpacity = source.Appearance.HeaderOpacity;
        target.Appearance.TextColor = source.Appearance.TextColor;
        target.Appearance.TitleFontFamily = source.Appearance.TitleFontFamily;
        target.Appearance.TitleFontBold = source.Appearance.TitleFontBold;
        target.Appearance.TitleFontItalic = source.Appearance.TitleFontItalic;
    }
}
