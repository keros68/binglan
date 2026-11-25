using BingLan.Core.Dock;
using BingLan.Core.Services;

namespace BingLan.Core.Models;

/// <summary>
/// Summary of a card currently stored on a disconnected secondary monitor so the settings UI
/// can surface it and let the user move it back to the primary display.
/// </summary>
public sealed record DisconnectedScreenWidget(
    Guid Id,
    string Title,
    string KindName,
    string ScreenKey,
    bool IsHidden);

public sealed record MonitorIdentityCandidate(
    string MonitorKey,
    string? HardwareId,
    string? DeviceName,
    string? FriendlyName,
    string? DisplayLabel);

/// <summary>
/// Result of copying the primary display's desktop card profile to a secondary monitor.
/// </summary>
public sealed record CopyPrimaryProfileResult(
    ScreenProfileState Profile,
    IReadOnlyDictionary<DesktopComponentKind, PixelRect> ComponentPixelBounds,
    TodoWidgetState? CreatedTodo,
    PixelRect? CreatedTodoPixelBounds);

/// <summary>
/// Unified pure accessor bridging the primary display (stored in root <see cref="AppState"/>
/// fields) and secondary displays (stored in <see cref="AppState.MultiScreen"/>), plus
/// target-monitor local-DIP to physical-pixel placement rules.
/// </summary>
public static class ScreenProfileAccessor
{
    public const double DefaultDpi = 96d;
    public const double MinimumWidgetWidthDip = 226d;
    public const double MinimumWidgetHeightDip = 166d;

    public const string DisconnectedTodoKind = "待办卡片";
    public const string DisconnectedNoteKind = "普通便签";
    public const string DisconnectedFileBoxKind = "文件收纳盒";
    public const string DisconnectedQuickLaunchKind = "快捷入口";
    public const string DisconnectedBuiltInInfoKind = "桌面信息卡片";

    public static bool IsPrimary(string? screenKey) =>
        MultiScreenRules.NormalizeScreenKey(screenKey) is null;

    public static IReadOnlyList<FileBoxState> FilterPrimaryFileBoxes(IEnumerable<FileBoxState> boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        return boxes.Where(box => IsPrimary(box.ScreenKey)).ToList();
    }

    public static bool IsMultiScreenEnabled(AppState state) =>
        state?.MultiScreen is { IsEnabled: true };

    public static DesktopExperienceState GetExperience(AppState state, string? screenKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalized = MultiScreenRules.NormalizeScreenKey(screenKey);
        if (normalized is null)
        {
            return state.DesktopExperience;
        }

        return state.MultiScreen?.Screens.TryGetValue(normalized, out var profile) == true
            ? profile.Experience
            : MultiScreenRules.CreateBlankExperience();
    }

    public static ScreenProfileState EnsureScreenProfile(
        AppState state,
        string monitorKey,
        string? deviceName = null,
        string? displayLabel = null,
        string? hardwareModelId = null,
        string? friendlyName = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalized = MultiScreenRules.NormalizeScreenKey(monitorKey)
            ?? throw new ArgumentException("Secondary monitor key cannot be empty or primary.", nameof(monitorKey));

        var resolvedModelId = MultiScreenRules.ExtractHardwareModelId(hardwareModelId)
            ?? MultiScreenRules.ExtractHardwareModelId(normalized);
        var resolvedFriendlyName = MultiScreenRules.NormalizeFriendlyName(friendlyName);

        state.MultiScreen ??= new MultiScreenState();
        if (!state.MultiScreen.Screens.TryGetValue(normalized, out var profile))
        {
            profile = MultiScreenRules.CreateBlankProfile(normalized, deviceName, displayLabel);
            profile.HardwareModelId = resolvedModelId;
            profile.FriendlyName = resolvedFriendlyName;
            state.MultiScreen.Screens[normalized] = profile;
        }
        else
        {
            profile.MonitorKey = normalized;
            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                profile.LastKnownDeviceName = deviceName.Trim();
            }
            if (!string.IsNullOrWhiteSpace(displayLabel))
            {
                profile.DisplayLabel = displayLabel.Trim();
            }
            if (resolvedModelId is not null)
            {
                profile.HardwareModelId = resolvedModelId;
            }
            if (resolvedFriendlyName is not null)
            {
                profile.FriendlyName = resolvedFriendlyName;
            }
            profile.Experience ??= MultiScreenRules.CreateBlankExperience();
            DesktopExperienceRules.Normalize(profile.Experience);
            profile.QuickPlaces = QuickPlaceRules.Normalize(profile.QuickPlaces);
        }

        return profile;
    }

    public static List<QuickPlaceState> GetQuickPlaces(AppState state, string? screenKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalized = MultiScreenRules.NormalizeScreenKey(screenKey);
        if (normalized is null)
        {
            return state.QuickPlaces;
        }

        return state.MultiScreen?.Screens.TryGetValue(normalized, out var profile) == true
            ? profile.QuickPlaces
            : QuickPlaceRules.CreateDefaults();
    }

    public static void SetQuickPlaces(AppState state, string? screenKey, List<QuickPlaceState> items)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalizedItems = QuickPlaceRules.Normalize(items);
        var normalizedKey = MultiScreenRules.NormalizeScreenKey(screenKey);
        if (normalizedKey is null)
        {
            state.QuickPlaces = normalizedItems;
            return;
        }

        var profile = EnsureScreenProfile(state, normalizedKey);
        profile.QuickPlaces = normalizedItems;
    }

    public static Guid? GetPrimaryTodoId(AppState state, string? screenKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalizedKey = MultiScreenRules.NormalizeScreenKey(screenKey);
        if (normalizedKey is null)
        {
            return state.TodoWidgets.FirstOrDefault(todo => IsPrimary(todo.ScreenKey))?.Id;
        }

        if (state.MultiScreen?.Screens.TryGetValue(normalizedKey, out var profile) == true
            && profile.ParsedPrimaryTodoId is { } id
            && state.TodoWidgets.Any(todo => todo.Id == id && MatchesScreen(todo.ScreenKey, normalizedKey)))
        {
            return id;
        }

        return state.TodoWidgets.FirstOrDefault(todo => MatchesScreen(todo.ScreenKey, normalizedKey))?.Id;
    }

    public static void SetPrimaryTodoId(AppState state, string? screenKey, Guid? todoId)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalizedKey = MultiScreenRules.NormalizeScreenKey(screenKey);
        if (normalizedKey is null)
        {
            return;
        }

        if (state.MultiScreen?.Screens.TryGetValue(normalizedKey, out var profile) == true)
        {
            profile.PrimaryTodoId = todoId is { } id && id != Guid.Empty ? id.ToString("D") : null;
        }
    }

    public static bool MatchesScreen(string? candidateScreenKey, string? targetScreenKey) =>
        string.Equals(
            MultiScreenRules.NormalizeScreenKey(candidateScreenKey),
            MultiScreenRules.NormalizeScreenKey(targetScreenKey),
            StringComparison.Ordinal);

    public static IReadOnlyList<TodoWidgetState> GetTodosForScreen(AppState state, string? screenKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.TodoWidgets.Where(todo => MatchesScreen(todo.ScreenKey, screenKey)).ToArray();
    }

    public static IReadOnlyList<NoteWidgetState> GetNotesForScreen(AppState state, string? screenKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.NoteWidgets.Where(note => MatchesScreen(note.ScreenKey, screenKey)).ToArray();
    }

    public static IReadOnlyList<FileBoxState> GetFileBoxesForScreen(AppState state, string? screenKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.FileBoxes.Where(box => MatchesScreen(box.ScreenKey, screenKey)).ToArray();
    }

    public static IReadOnlyList<FileBoxState> GetFileBoxGroupMembers(AppState state, FileBoxState box)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(box);
        if (box.GroupId is not { } gid || gid == Guid.Empty)
        {
            return [box];
        }

        var members = state.FileBoxes
            .Where(candidate => candidate.GroupId == gid)
            .OrderBy(candidate => candidate.TabOrder)
            .ToList();
        return members.Count > 0 ? members : [box];
    }

    /// <summary>
    /// Converts a local DIP rectangle relative to <paramref name="targetWorkAreaPx"/>'s top-left
    /// corner into physical screen pixels clamped inside the target monitor's work area.
    /// Correctly handles secondary monitors with arbitrary positive or negative offsets (e.g. X=2048, Y=-50)
    /// and mixed DPI.
    /// </summary>
    public static PixelRect LocalDipToPhysicalBounds(
        double localLeftDip,
        double localTopDip,
        double widthDip,
        double heightDip,
        PixelRect targetWorkAreaPx,
        uint targetDpi)
    {
        var scale = ResolveScale(targetDpi);
        var rawWidth = Math.Max(1, (int)Math.Round(Math.Max(1d, widthDip) * scale));
        var rawHeight = Math.Max(1, (int)Math.Round(Math.Max(1d, heightDip) * scale));
        var widthPx = targetWorkAreaPx.HasArea ? Math.Min(rawWidth, targetWorkAreaPx.Width) : rawWidth;
        var heightPx = targetWorkAreaPx.HasArea ? Math.Min(rawHeight, targetWorkAreaPx.Height) : rawHeight;
        var desiredLeft = targetWorkAreaPx.Left + (int)Math.Round(localLeftDip * scale);
        var desiredTop = targetWorkAreaPx.Top + (int)Math.Round(localTopDip * scale);
        if (!targetWorkAreaPx.HasArea)
        {
            return new PixelRect(desiredLeft, desiredTop, desiredLeft + widthPx, desiredTop + heightPx);
        }

        var maxLeft = Math.Max(targetWorkAreaPx.Left, targetWorkAreaPx.Right - widthPx);
        var maxTop = Math.Max(targetWorkAreaPx.Top, targetWorkAreaPx.Bottom - heightPx);
        var clampedLeft = Math.Clamp(desiredLeft, targetWorkAreaPx.Left, maxLeft);
        var clampedTop = Math.Clamp(desiredTop, targetWorkAreaPx.Top, maxTop);
        return new PixelRect(clampedLeft, clampedTop, clampedLeft + widthPx, clampedTop + heightPx);
    }

    /// <summary>
    /// Generates preset placements in the target monitor's local DIP space and maps each card
    /// to physical pixels inside <paramref name="targetWorkAreaPx"/>.
    /// </summary>
    public static IReadOnlyDictionary<DesktopComponentKind, (WindowPlacement Placement, PixelRect PixelBounds)>
        ComputePresetPlacementsForMonitor(
            DesktopLayoutPreset preset,
            PixelRect targetWorkAreaPx,
            uint targetDpi,
            string? currentLayoutKey = null)
    {
        var scale = ResolveScale(targetDpi);
        var localWidthDip = targetWorkAreaPx.HasArea ? Math.Max(1d, targetWorkAreaPx.Width / scale) : 1920d;
        var localHeightDip = targetWorkAreaPx.HasArea ? Math.Max(1d, targetWorkAreaPx.Height / scale) : 1040d;
        var localPresets = DesktopExperienceRules.PresetPlacements(preset, 0d, 0d, localWidthDip, localHeightDip);

        var result = new Dictionary<DesktopComponentKind, (WindowPlacement Placement, PixelRect PixelBounds)>(localPresets.Count);
        foreach (var (kind, local) in localPresets)
        {
            var pixelBounds = LocalDipToPhysicalBounds(
                local.Left,
                local.Top,
                local.Width,
                local.Height,
                targetWorkAreaPx,
                targetDpi);
            var placement = new WindowPlacement
            {
                Left = pixelBounds.Left / scale,
                Top = pixelBounds.Top / scale,
                Width = pixelBounds.Width / scale,
                Height = pixelBounds.Height / scale
            };
            RecordMonitorLocalPlacement(placement, pixelBounds, targetWorkAreaPx, targetDpi);
            if (!string.IsNullOrEmpty(currentLayoutKey))
            {
                DisplayLayoutRules.Remember(placement, currentLayoutKey, pixelBounds);
            }
            result[kind] = (placement, pixelBounds);
        }

        return result;
    }

    /// <summary>
    /// Computes cascaded local-DIP and physical-pixel bounds for a newly created card on the
    /// target monitor.
    /// </summary>
    public static (WindowPlacement Placement, PixelRect PixelBounds) ComputeNewWidgetBoundsForMonitor(
        PixelRect targetWorkAreaPx,
        uint targetDpi,
        double defaultLocalLeftDip,
        double defaultLocalTopDip,
        double widthDip,
        double heightDip,
        int existingCountOnScreen,
        string? currentLayoutKey = null)
    {
        var scale = ResolveScale(targetDpi);
        var cascadeDip = (Math.Max(0, existingCountOnScreen) % 8) * 24d;
        var pixelBounds = LocalDipToPhysicalBounds(
            defaultLocalLeftDip + cascadeDip,
            defaultLocalTopDip + cascadeDip,
            widthDip,
            heightDip,
            targetWorkAreaPx,
            targetDpi);
        var placement = new WindowPlacement
        {
            Left = pixelBounds.Left / scale,
            Top = pixelBounds.Top / scale,
            Width = pixelBounds.Width / scale,
            Height = pixelBounds.Height / scale
        };
        RecordMonitorLocalPlacement(placement, pixelBounds, targetWorkAreaPx, targetDpi);
        if (!string.IsNullOrEmpty(currentLayoutKey))
        {
            DisplayLayoutRules.Remember(placement, currentLayoutKey, pixelBounds);
        }
        return (placement, pixelBounds);
    }

    /// <summary>
    /// Computes physical pixel bounds on the destination monitor when moving a card across
    /// screens, preserving relative position inside the source work area and scaling DIP size
    /// to the destination monitor's DPI.
    /// </summary>
    public static PixelRect ComputeMovedWidgetPixelBounds(
        PixelRect? sourceBoundsPx,
        PixelRect? sourceWorkAreaPx,
        uint sourceDpi,
        PixelRect targetWorkAreaPx,
        uint targetDpi,
        double fallbackWidthDip,
        double fallbackHeightDip)
    {
        var srcScale = ResolveScale(sourceDpi);
        var dstScale = ResolveScale(targetDpi);
        var widthDip = sourceBoundsPx is { HasArea: true } srcRect
            ? srcRect.Width / srcScale
            : Math.Max(1d, fallbackWidthDip);
        var heightDip = sourceBoundsPx is { HasArea: true } srcRectH
            ? srcRectH.Height / srcScale
            : Math.Max(1d, fallbackHeightDip);

        double localLeftDip;
        double localTopDip;
        if (sourceBoundsPx is { HasArea: true } rect && sourceWorkAreaPx is { HasArea: true } srcWork)
        {
            var freeX = Math.Max(1, srcWork.Width - rect.Width);
            var freeY = Math.Max(1, srcWork.Height - rect.Height);
            var fracX = Math.Clamp((rect.Left - srcWork.Left) / (double)freeX, 0d, 1d);
            var fracY = Math.Clamp((rect.Top - srcWork.Top) / (double)freeY, 0d, 1d);
            var targetFreeWidthDip = Math.Max(0d, (targetWorkAreaPx.Width / dstScale) - widthDip);
            var targetFreeHeightDip = Math.Max(0d, (targetWorkAreaPx.Height / dstScale) - heightDip);
            localLeftDip = fracX * targetFreeWidthDip;
            localTopDip = fracY * targetFreeHeightDip;
        }
        else
        {
            localLeftDip = 92d;
            localTopDip = 92d;
        }

        return LocalDipToPhysicalBounds(localLeftDip, localTopDip, widthDip, heightDip, targetWorkAreaPx, targetDpi);
    }

    public static IReadOnlyDictionary<DesktopComponentKind, PixelRect> ApplyPresetToScreen(
        AppState state,
        string? screenKey,
        DesktopLayoutPreset preset,
        PixelRect targetWorkAreaPx,
        uint targetDpi,
        string? currentLayoutKey = null,
        bool preserveGreetingColor = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalizedKey = MultiScreenRules.NormalizeScreenKey(screenKey);
        var experience = normalizedKey is null
            ? state.DesktopExperience
            : EnsureScreenProfile(state, normalizedKey).Experience;
        DesktopExperienceRules.Normalize(experience);
        experience.ActivePreset = preset;
        if (!preserveGreetingColor)
        {
            experience.GetComponent(DesktopComponentKind.Greeting).Appearance.TextColor = "#FFFFFF";
        }

        var computed = ComputePresetPlacementsForMonitor(preset, targetWorkAreaPx, targetDpi, currentLayoutKey);
        var pixelMap = new Dictionary<DesktopComponentKind, PixelRect>(computed.Count);
        foreach (var (kind, entry) in computed)
        {
            var component = experience.GetComponent(kind);
            var existingLayouts = component.Placement.DisplayLayouts;
            if (normalizedKey is null)
            {
                entry.Placement.MonitorLocal = null;
            }
            component.Placement = entry.Placement;
            if (existingLayouts is { Count: > 0 })
            {
                var merged = new List<DisplayLayoutPlacement>(existingLayouts.Count + component.Placement.DisplayLayouts.Count);
                foreach (var old in existingLayouts)
                {
                    if (old is not null && old.Layout != currentLayoutKey)
                    {
                        merged.Add(old);
                    }
                }
                foreach (var current in component.Placement.DisplayLayouts)
                {
                    if (current is not null)
                    {
                        merged.Add(current);
                    }
                }
                if (merged.Count > DisplayLayoutRules.MaximumRememberedLayouts)
                {
                    merged.RemoveRange(0, merged.Count - DisplayLayoutRules.MaximumRememberedLayouts);
                }
                component.Placement.DisplayLayouts = merged;
            }
            pixelMap[kind] = entry.PixelBounds;
        }

        // Keep quick launch inside the target work area as well.
        var quickComponent = experience.GetComponent(DesktopComponentKind.QuickLaunch);
        var quickPixel = LocalDipToPhysicalBounds(
            92d,
            Math.Max(92d, (targetWorkAreaPx.Height / ResolveScale(targetDpi)) - 120d),
            quickComponent.Placement.Width > 0 ? quickComponent.Placement.Width : 328d,
            quickComponent.Placement.Height > 0 ? quickComponent.Placement.Height : 72d,
            targetWorkAreaPx,
            targetDpi);
        var scale = ResolveScale(targetDpi);
        quickComponent.Placement.Left = quickPixel.Left / scale;
        quickComponent.Placement.Top = quickPixel.Top / scale;
        if (normalizedKey is not null)
        {
            RecordMonitorLocalPlacement(quickComponent.Placement, quickPixel, targetWorkAreaPx, targetDpi);
        }
        else
        {
            quickComponent.Placement.MonitorLocal = null;
        }
        if (!string.IsNullOrEmpty(currentLayoutKey))
        {
            DisplayLayoutRules.Remember(quickComponent.Placement, currentLayoutKey, quickPixel);
        }
        pixelMap[DesktopComponentKind.QuickLaunch] = quickPixel;

        var primaryTodoId = GetPrimaryTodoId(state, normalizedKey);
        if (primaryTodoId is { } id
            && state.TodoWidgets.FirstOrDefault(todo => todo.Id == id) is { } primaryTodo
            && computed.TryGetValue(DesktopComponentKind.Todo, out var todoPreset))
        {
            primaryTodo.Placement = ClonePlacement(experience.GetComponent(DesktopComponentKind.Todo).Placement);
            if (!string.IsNullOrEmpty(currentLayoutKey))
            {
                DisplayLayoutRules.Remember(primaryTodo.Placement, currentLayoutKey, todoPreset.PixelBounds);
            }
        }

        return pixelMap;
    }

    /// <summary>
    /// Deep-copies the primary display's built-in card configuration and quick places to
    /// <paramref name="targetScreenKey"/>, re-laying out cards in the target monitor's local DIP
    /// space and creating an empty secondary to-do card if the primary to-do component is visible
    /// and the target screen has none yet.
    /// </summary>
    public static CopyPrimaryProfileResult CopyPrimaryProfileToScreen(
        AppState state,
        string targetScreenKey,
        PixelRect targetWorkAreaPx,
        uint targetDpi,
        string? deviceName = null,
        string? displayLabel = null,
        string? currentLayoutKey = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalizedKey = MultiScreenRules.NormalizeScreenKey(targetScreenKey)
            ?? throw new ArgumentException("Target screen key must identify a secondary monitor.", nameof(targetScreenKey));

        DesktopExperienceRules.Normalize(state.DesktopExperience);
        var profile = EnsureScreenProfile(state, normalizedKey, deviceName, displayLabel);

        var copiedExperience = new DesktopExperienceState
        {
            ActivePreset = state.DesktopExperience.ActivePreset,
            Components = state.DesktopExperience.Components
                .Select(CloneComponent)
                .ToList()
        };
        DesktopExperienceRules.Normalize(copiedExperience);
        profile.Experience = copiedExperience;
        var existingQuickPlaces = QuickPlaceRules.Normalize(profile.QuickPlaces);
        var hasCustomQuickPlaces = !AreQuickPlacesEquivalent(existingQuickPlaces, QuickPlaceRules.CreateDefaults());
        if (!hasCustomQuickPlaces)
        {
            profile.QuickPlaces = state.QuickPlaces
                .Select(place => new QuickPlaceState
                {
                    Icon = place.Icon,
                    Name = place.Name,
                    Target = place.Target
                })
                .ToList();
        }

        var pixelMap = ApplyPresetToScreen(
            state,
            normalizedKey,
            copiedExperience.ActivePreset,
            targetWorkAreaPx,
            targetDpi,
            currentLayoutKey,
            preserveGreetingColor: true);

        TodoWidgetState? createdTodo = null;
        PixelRect? createdTodoPixels = null;
        var todoComponent = profile.Experience.GetComponent(DesktopComponentKind.Todo);
        if (todoComponent.IsVisible && GetTodosForScreen(state, normalizedKey).Count == 0)
        {
            createdTodo = TodoService.CreateDefaultWidget();
            createdTodo.Title = WidgetTitleRules.NextTitle(
                createdTodo.Title,
                state.TodoWidgets.Select(todo => todo.Title));
            createdTodo.ScreenKey = normalizedKey;
            createdTodo.IsLocked = todoComponent.IsLocked;
            createdTodo.CornerRadius = todoComponent.CornerRadius;
            createdTodo.FontScale = todoComponent.FontScale;
            createdTodo.FontFamily = todoComponent.FontFamily;
            CopyAppearance(createdTodo.Appearance, todoComponent.Appearance);
            createdTodo.Placement = ClonePlacement(todoComponent.Placement);
            if (pixelMap.TryGetValue(DesktopComponentKind.Todo, out var todoPx))
            {
                createdTodoPixels = todoPx;
                if (!string.IsNullOrEmpty(currentLayoutKey))
                {
                    DisplayLayoutRules.Remember(createdTodo.Placement, currentLayoutKey, todoPx);
                }
            }
            state.TodoWidgets.Add(createdTodo);
            profile.PrimaryTodoId = createdTodo.Id.ToString("D");
        }

        return new CopyPrimaryProfileResult(profile, pixelMap, createdTodo, createdTodoPixels);
    }

    public static PixelRect? MoveTodoToScreen(
        AppState state,
        Guid todoId,
        string? targetScreenKey,
        PixelRect targetWorkAreaPx,
        uint targetDpi,
        PixelRect? sourceBoundsPx = null,
        PixelRect? sourceWorkAreaPx = null,
        uint sourceDpi = 96,
        string? currentLayoutKey = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var todo = state.TodoWidgets.FirstOrDefault(item => item.Id == todoId);
        if (todo is null)
        {
            return null;
        }

        var previousKey = MultiScreenRules.NormalizeScreenKey(todo.ScreenKey);
        var destinationKey = MultiScreenRules.NormalizeScreenKey(targetScreenKey);
        if (string.Equals(previousKey, destinationKey, StringComparison.Ordinal))
        {
            return null;
        }

        var targetPx = ComputeMovedWidgetPixelBounds(
            sourceBoundsPx,
            sourceWorkAreaPx,
            sourceDpi,
            targetWorkAreaPx,
            targetDpi,
            todo.Placement.Width,
            todo.Placement.Height);
        var scale = ResolveScale(targetDpi);
        todo.ScreenKey = destinationKey;
        todo.Placement.Left = targetPx.Left / scale;
        todo.Placement.Top = targetPx.Top / scale;
        todo.Placement.Width = targetPx.Width / scale;
        todo.Placement.Height = targetPx.Height / scale;
        if (destinationKey is not null)
        {
            RecordMonitorLocalPlacement(todo.Placement, targetPx, targetWorkAreaPx, targetDpi);
        }
        else
        {
            todo.Placement.MonitorLocal = null;
        }
        todo.Placement.DisplayLayouts = [];
        if (!string.IsNullOrEmpty(currentLayoutKey))
        {
            DisplayLayoutRules.Remember(todo.Placement, currentLayoutKey, targetPx);
        }

        ReconcileTodoOwnershipAfterMove(state, todo, previousKey, destinationKey);
        return targetPx;
    }

    public static PixelRect? MoveNoteToScreen(
        AppState state,
        Guid noteId,
        string? targetScreenKey,
        PixelRect targetWorkAreaPx,
        uint targetDpi,
        PixelRect? sourceBoundsPx = null,
        PixelRect? sourceWorkAreaPx = null,
        uint sourceDpi = 96,
        string? currentLayoutKey = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var note = state.NoteWidgets.FirstOrDefault(item => item.Id == noteId);
        if (note is null)
        {
            return null;
        }

        var previousKey = MultiScreenRules.NormalizeScreenKey(note.ScreenKey);
        var destinationKey = MultiScreenRules.NormalizeScreenKey(targetScreenKey);
        if (string.Equals(previousKey, destinationKey, StringComparison.Ordinal))
        {
            return null;
        }

        if (destinationKey is not null)
        {
            EnsureScreenProfile(state, destinationKey);
        }

        var targetPx = ComputeMovedWidgetPixelBounds(
            sourceBoundsPx,
            sourceWorkAreaPx,
            sourceDpi,
            targetWorkAreaPx,
            targetDpi,
            note.Placement.Width,
            note.Placement.Height);
        var scale = ResolveScale(targetDpi);
        note.ScreenKey = destinationKey;
        note.Placement.Left = targetPx.Left / scale;
        note.Placement.Top = targetPx.Top / scale;
        note.Placement.Width = targetPx.Width / scale;
        note.Placement.Height = targetPx.Height / scale;
        if (destinationKey is not null)
        {
            RecordMonitorLocalPlacement(note.Placement, targetPx, targetWorkAreaPx, targetDpi);
        }
        else
        {
            note.Placement.MonitorLocal = null;
        }
        note.Placement.DisplayLayouts = [];
        if (!string.IsNullOrEmpty(currentLayoutKey))
        {
            DisplayLayoutRules.Remember(note.Placement, currentLayoutKey, targetPx);
        }
        return targetPx;
    }

    public static PixelRect? MoveFileBoxToScreen(
        AppState state,
        Guid boxId,
        string? targetScreenKey,
        PixelRect targetWorkAreaPx,
        uint targetDpi,
        PixelRect? sourceBoundsPx = null,
        PixelRect? sourceWorkAreaPx = null,
        uint sourceDpi = 96,
        string? currentLayoutKey = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var box = state.FileBoxes.FirstOrDefault(item => item.Id == boxId);
        if (box is null)
        {
            return null;
        }

        var previousKey = MultiScreenRules.NormalizeScreenKey(box.ScreenKey);
        var destinationKey = MultiScreenRules.NormalizeScreenKey(targetScreenKey);
        if (string.Equals(previousKey, destinationKey, StringComparison.Ordinal))
        {
            return null;
        }

        if (destinationKey is not null)
        {
            EnsureScreenProfile(state, destinationKey);
        }

        // Moving a file box to another screen clears any monitor-specific edge dock anchor and keeps
        // all tabs in the same GroupId synchronized.
        var members = GetFileBoxGroupMembers(state, box);
        box.EdgeDockSide = null;
        box.EdgeDockOffsetDip = null;
        box.EdgeDockMonitorDeviceName = null;

        var targetPx = ComputeMovedWidgetPixelBounds(
            sourceBoundsPx,
            sourceWorkAreaPx,
            sourceDpi,
            targetWorkAreaPx,
            targetDpi,
            box.Placement.Width,
            box.Placement.Height);
        var scale = ResolveScale(targetDpi);
        box.ScreenKey = destinationKey;
        box.Placement.Left = targetPx.Left / scale;
        box.Placement.Top = targetPx.Top / scale;
        box.Placement.Width = targetPx.Width / scale;
        box.Placement.Height = targetPx.Height / scale;
        if (destinationKey is not null)
        {
            RecordMonitorLocalPlacement(box.Placement, targetPx, targetWorkAreaPx, targetDpi);
        }
        else
        {
            box.Placement.MonitorLocal = null;
        }
        box.Placement.DisplayLayouts = [];
        if (!string.IsNullOrEmpty(currentLayoutKey))
        {
            DisplayLayoutRules.Remember(box.Placement, currentLayoutKey, targetPx);
        }

        foreach (var member in members)
        {
            if (ReferenceEquals(member, box))
            {
                continue;
            }

            member.ScreenKey = destinationKey;
            member.EdgeDockSide = null;
            member.EdgeDockOffsetDip = null;
            member.EdgeDockMonitorDeviceName = null;
            member.Placement = ClonePlacement(box.Placement);
        }
        return targetPx;
    }

    /// <summary>
    /// Updates <see cref="TodoWidgetState.ScreenKey"/> and per-screen <see cref="ScreenProfileState.PrimaryTodoId"/>
    /// after a user moves or drags a to-do card onto another monitor.
    /// </summary>
    public static void ReconcileTodoOwnershipAfterMove(
        AppState state,
        TodoWidgetState todo,
        string? previousScreenKey,
        string? destinationScreenKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(todo);

        var prev = MultiScreenRules.NormalizeScreenKey(previousScreenKey);
        var dest = MultiScreenRules.NormalizeScreenKey(destinationScreenKey);
        var wasPrimaryRootTodo = prev is null
            && state.TodoWidgets.FirstOrDefault(item => item.Id == todo.Id || IsPrimary(item.ScreenKey))?.Id == todo.Id;
        var destAlreadyHadPrimaryTodo = dest is null
            && state.TodoWidgets.Any(item => item.Id != todo.Id && IsPrimary(item.ScreenKey));

        todo.ScreenKey = dest;

        if (prev is null)
        {
            var todoComponent = state.DesktopExperience.GetComponent(DesktopComponentKind.Todo);
            var remainingPrimary = state.TodoWidgets.FirstOrDefault(item => IsPrimary(item.ScreenKey));
            if (remainingPrimary is null)
            {
                todoComponent.IsVisible = false;
            }
            else if (wasPrimaryRootTodo)
            {
                SyncTodoComponentFromWidget(todoComponent, remainingPrimary);
            }
        }
        else if (state.MultiScreen?.Screens.TryGetValue(prev, out var prevProfile) == true)
        {
            var remaining = state.TodoWidgets.FirstOrDefault(item => MatchesScreen(item.ScreenKey, prev));
            if (remaining is null)
            {
                prevProfile.PrimaryTodoId = null;
                prevProfile.Experience.GetComponent(DesktopComponentKind.Todo).IsVisible = false;
            }
            else if (prevProfile.ParsedPrimaryTodoId == todo.Id || prevProfile.ParsedPrimaryTodoId is null)
            {
                prevProfile.PrimaryTodoId = remaining.Id.ToString("D");
                SyncTodoComponentFromWidget(prevProfile.Experience.GetComponent(DesktopComponentKind.Todo), remaining);
            }
        }

        if (dest is null)
        {
            var primaryTodoComponent = state.DesktopExperience.GetComponent(DesktopComponentKind.Todo);
            primaryTodoComponent.IsVisible = true;
            if (destAlreadyHadPrimaryTodo)
            {
                if (state.TodoWidgets.Remove(todo))
                {
                    state.TodoWidgets.Add(todo);
                }
            }
            else
            {
                SyncTodoComponentFromWidget(primaryTodoComponent, todo);
            }
        }
        else
        {
            var destProfile = EnsureScreenProfile(state, dest);
            destProfile.Experience.GetComponent(DesktopComponentKind.Todo).IsVisible = true;
            if (destProfile.ParsedPrimaryTodoId is null)
            {
                destProfile.PrimaryTodoId = todo.Id.ToString("D");
                SyncTodoComponentFromWidget(destProfile.Experience.GetComponent(DesktopComponentKind.Todo), todo);
            }
        }
    }

    private static void SyncTodoComponentFromWidget(DesktopComponentState component, TodoWidgetState source)
    {
        component.Placement = ClonePlacement(source.Placement);
        component.IsLocked = source.IsLocked;
        CopyAppearance(component.Appearance, source.Appearance);
        component.CornerRadius = source.CornerRadius;
        component.FontScale = source.FontScale;
        component.FontFamily = source.FontFamily;
    }

    /// <summary>
    /// Moves the <c>QuickPlaces</c> icon list from <paramref name="sourceScreenKey"/> to
    /// <paramref name="targetScreenKey"/>, merging into existing custom or visible items on the
    /// destination screen when present, and resetting the source screen back to defaults.
    /// </summary>
    public static List<QuickPlaceState> MoveQuickPlacesToScreen(
        AppState state,
        string? sourceScreenKey,
        string? targetScreenKey) =>
        MoveQuickPlacesCore(state, sourceScreenKey, targetScreenKey).DestinationItems;

    private static (List<QuickPlaceState> DestinationItems, List<QuickPlaceState> LeftoverItems, bool TransferredAny)
        MoveQuickPlacesCore(
            AppState state,
            string? sourceScreenKey,
            string? targetScreenKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        var srcKey = MultiScreenRules.NormalizeScreenKey(sourceScreenKey);
        var dstKey = MultiScreenRules.NormalizeScreenKey(targetScreenKey);
        var sourceItems = GetQuickPlaces(state, srcKey)
            .Select(place => new QuickPlaceState
            {
                Icon = place.Icon,
                Name = place.Name,
                Target = place.Target
            })
            .ToList();
        var normalized = QuickPlaceRules.Normalize(sourceItems);
        if (string.Equals(srcKey, dstKey, StringComparison.Ordinal))
        {
            return (normalized, [], false);
        }

        var destComponent = GetExperience(state, dstKey).GetComponent(DesktopComponentKind.QuickLaunch);
        var destExisting = QuickPlaceRules.Normalize(GetQuickPlaces(state, dstKey));
        var shouldMerge = destComponent.IsVisible
            || !AreQuickPlacesEquivalent(destExisting, QuickPlaceRules.CreateDefaults());
        List<QuickPlaceState> resultItems;
        var leftover = new List<QuickPlaceState>();
        var transferredAny = false;
        if (shouldMerge)
        {
            var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var merged = new List<QuickPlaceState>(QuickPlaceRules.MaximumItems);
            foreach (var item in destExisting)
            {
                if (merged.Count >= QuickPlaceRules.MaximumItems)
                {
                    break;
                }
                if (seenTargets.Add(item.Target))
                {
                    merged.Add(item);
                }
            }
            foreach (var item in normalized)
            {
                if (seenTargets.Contains(item.Target))
                {
                    transferredAny = true;
                    continue;
                }
                if (merged.Count < QuickPlaceRules.MaximumItems)
                {
                    seenTargets.Add(item.Target);
                    merged.Add(item);
                    transferredAny = true;
                }
                else
                {
                    leftover.Add(item);
                }
            }
            resultItems = QuickPlaceRules.Normalize(merged);
        }
        else
        {
            resultItems = normalized;
            transferredAny = true;
        }

        if (transferredAny)
        {
            SetQuickPlaces(state, dstKey, resultItems);
            SetQuickPlaces(
                state,
                srcKey,
                leftover.Count > 0 ? QuickPlaceRules.Normalize(leftover) : QuickPlaceRules.CreateDefaults());
        }
        return (GetQuickPlaces(state, dstKey), leftover, transferredAny);
    }

    public static bool MoveDisconnectedQuickPlacesToPrimary(AppState state, string sourceScreenKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        var srcKey = MultiScreenRules.NormalizeScreenKey(sourceScreenKey);
        if (srcKey is null
            || state.MultiScreen is null
            || !state.MultiScreen.Screens.TryGetValue(srcKey, out var profile))
        {
            return false;
        }

        var srcComponent = profile.Experience.GetComponent(DesktopComponentKind.QuickLaunch);
        var normalizedSrc = QuickPlaceRules.Normalize(profile.QuickPlaces);
        var hadContentToMove = srcComponent.IsVisible
            || !AreQuickPlacesEquivalent(normalizedSrc, QuickPlaceRules.CreateDefaults());
        if (!hadContentToMove)
        {
            return false;
        }

        var primaryComponent = state.DesktopExperience.GetComponent(DesktopComponentKind.QuickLaunch);
        var (_, leftover, transferredAny) = MoveQuickPlacesCore(state, srcKey, targetScreenKey: null);
        if (!transferredAny && leftover.Count > 0)
        {
            return false;
        }
        if (!primaryComponent.IsVisible)
        {
            primaryComponent.IsVisible = true;
            CopyAppearance(primaryComponent.Appearance, srcComponent.Appearance);
            primaryComponent.CornerRadius = srcComponent.CornerRadius;
            primaryComponent.FontScale = srcComponent.FontScale;
        }
        if (leftover.Count == 0)
        {
            srcComponent.IsVisible = false;
        }
        return true;
    }

    private static bool AreQuickPlacesEquivalent(
        IReadOnlyList<QuickPlaceState> a,
        IReadOnlyList<QuickPlaceState> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Icon, b[i].Icon, StringComparison.Ordinal)
                || !string.Equals(a[i].Name, b[i].Name, StringComparison.Ordinal)
                || !string.Equals(a[i].Target, b[i].Target, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    public static bool MoveDisconnectedBuiltInToPrimary(
        AppState state,
        string sourceScreenKey,
        DesktopComponentKind kind)
    {
        ArgumentNullException.ThrowIfNull(state);
        var srcKey = MultiScreenRules.NormalizeScreenKey(sourceScreenKey);
        if (srcKey is null
            || kind is not (DesktopComponentKind.TimeDate or DesktopComponentKind.Greeting
                or DesktopComponentKind.Weather or DesktopComponentKind.Performance)
            || state.MultiScreen is null
            || !state.MultiScreen.Screens.TryGetValue(srcKey, out var profile))
        {
            return false;
        }

        var srcComponent = profile.Experience.GetComponent(kind);
        if (!srcComponent.IsVisible)
        {
            return false;
        }

        var primaryComponent = state.DesktopExperience.GetComponent(kind);
        if (!primaryComponent.IsVisible)
        {
            primaryComponent.IsVisible = true;
            CopyAppearance(primaryComponent.Appearance, srcComponent.Appearance);
            primaryComponent.CornerRadius = srcComponent.CornerRadius;
            primaryComponent.FontScale = srcComponent.FontScale;
            primaryComponent.FontFamily = srcComponent.FontFamily;
            primaryComponent.Density = srcComponent.Density;
            primaryComponent.ShowDivider = srcComponent.ShowDivider;
            primaryComponent.DividerThickness = srcComponent.DividerThickness;
        }
        srcComponent.IsVisible = false;
        return true;
    }

    public static Guid CreateDisconnectedComponentId(string screenKey, DesktopComponentKind kind)
    {
        var input = System.Text.Encoding.UTF8.GetBytes($"binglan:ms:{screenKey}:{(int)kind}");
        var hash = System.Security.Cryptography.SHA256.HashData(input);
        return new Guid(hash.AsSpan(0, 16));
    }

    public static IReadOnlyList<DisconnectedScreenWidget> FindDisconnectedScreenWidgets(
        AppState state,
        IReadOnlySet<string> connectedSecondaryKeys)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(connectedSecondaryKeys);

        var result = new List<DisconnectedScreenWidget>();
        foreach (var todo in state.TodoWidgets)
        {
            var key = MultiScreenRules.NormalizeScreenKey(todo.ScreenKey);
            if (key is not null && !connectedSecondaryKeys.Contains(key))
            {
                result.Add(new DisconnectedScreenWidget(todo.Id, todo.Title, DisconnectedTodoKind, key, IsHidden: false));
            }
        }
        foreach (var note in state.NoteWidgets)
        {
            var key = MultiScreenRules.NormalizeScreenKey(note.ScreenKey);
            if (key is not null && !connectedSecondaryKeys.Contains(key))
            {
                result.Add(new DisconnectedScreenWidget(note.Id, note.Title, DisconnectedNoteKind, key, note.IsHidden));
            }
        }
        var disconnectedBoxes = state.FileBoxes
            .Where(box => MultiScreenRules.NormalizeScreenKey(box.ScreenKey) is { } k
                && !connectedSecondaryKeys.Contains(k))
            .ToList();
        foreach (var members in FileBoxGroupRules.PartitionIntoHosts(disconnectedBoxes))
        {
            var lead = members.FirstOrDefault(m => m.IsActiveTab) ?? members[0];
            var key = MultiScreenRules.NormalizeScreenKey(lead.ScreenKey)!;
            var title = FileBoxGroupRules.FormatGroupDisplayTitle(members, lead.Id);
            result.Add(new DisconnectedScreenWidget(lead.Id, title, DisconnectedFileBoxKind, key, lead.IsHidden));
        }

        if (state.MultiScreen?.Screens is { Count: > 0 } screens)
        {
            foreach (var (rawKey, profile) in screens)
            {
                var key = MultiScreenRules.NormalizeScreenKey(rawKey);
                if (key is null || profile is null || connectedSecondaryKeys.Contains(key))
                {
                    continue;
                }

                var quickComponent = profile.Experience.GetComponent(DesktopComponentKind.QuickLaunch);
                if (quickComponent.IsVisible || HasCustomQuickPlaces(profile.QuickPlaces))
                {
                    result.Add(new DisconnectedScreenWidget(
                        CreateDisconnectedComponentId(key, DesktopComponentKind.QuickLaunch),
                        DesktopExperienceRules.GetComponentName(DesktopComponentKind.QuickLaunch),
                        DisconnectedQuickLaunchKind,
                        key,
                        IsHidden: !quickComponent.IsVisible));
                }

                foreach (var kind in new[]
                         {
                             DesktopComponentKind.TimeDate,
                             DesktopComponentKind.Greeting,
                             DesktopComponentKind.Weather,
                             DesktopComponentKind.Performance
                         })
                {
                    if (profile.Experience.GetComponent(kind).IsVisible)
                    {
                        result.Add(new DisconnectedScreenWidget(
                            CreateDisconnectedComponentId(key, kind),
                            DesktopExperienceRules.GetComponentName(kind),
                            DisconnectedBuiltInInfoKind,
                            key,
                            IsHidden: false));
                    }
                }
            }
        }

        return result;
    }

    private static bool HasCustomQuickPlaces(IReadOnlyList<QuickPlaceState>? items)
    {
        if (items is null || items.Count == 0)
        {
            return false;
        }

        var defaults = QuickPlaceRules.CreateDefaults();
        if (items.Count != defaults.Count)
        {
            return true;
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (!string.Equals(items[i].Icon, defaults[i].Icon, StringComparison.Ordinal)
                || !string.Equals(items[i].Name, defaults[i].Name, StringComparison.Ordinal)
                || !string.Equals(items[i].Target, defaults[i].Target, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static void RecordMonitorLocalPlacement(
        WindowPlacement placement,
        PixelRect boundsPx,
        PixelRect workAreaPx,
        uint dpi)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (!boundsPx.HasArea || !workAreaPx.HasArea)
        {
            return;
        }

        var scale = ResolveScale(dpi);
        var localLeft = (boundsPx.Left - workAreaPx.Left) / scale;
        var localTop = (boundsPx.Top - workAreaPx.Top) / scale;
        var widthDip = boundsPx.Width / scale;
        var heightDip = boundsPx.Height / scale;
        var workW = workAreaPx.Width / scale;
        var workH = workAreaPx.Height / scale;
        var freeX = Math.Max(0d, workW - widthDip);
        var freeY = Math.Max(0d, workH - heightDip);

        placement.MonitorLocal = new MonitorLocalPlacement
        {
            LocalLeftDip = localLeft,
            LocalTopDip = localTop,
            WidthDip = widthDip,
            HeightDip = heightDip,
            AnchorX = freeX > 0.5d ? Math.Clamp(localLeft / freeX, 0d, 1d) : 0d,
            AnchorY = freeY > 0.5d ? Math.Clamp(localTop / freeY, 0d, 1d) : 0d,
            SourceWorkWidthDip = workW,
            SourceWorkHeightDip = workH
        };
    }

    public static PixelRect? ResolveMonitorLocalBounds(
        WindowPlacement placement,
        PixelRect targetWorkAreaPx,
        uint targetDpi)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var local = placement.MonitorLocal;
        if (local is null || local.WidthDip <= 0d || local.HeightDip <= 0d || !targetWorkAreaPx.HasArea)
        {
            return null;
        }

        var scale = ResolveScale(targetDpi);
        var targetWorkW = targetWorkAreaPx.Width / scale;
        var targetWorkH = targetWorkAreaPx.Height / scale;

        double resolvedLeftDip;
        if (local.SourceWorkWidthDip > 0d
            && Math.Abs(targetWorkW - local.SourceWorkWidthDip) <= 24d
            && local.LocalLeftDip + local.WidthDip <= targetWorkW + 1d)
        {
            resolvedLeftDip = Math.Max(0d, local.LocalLeftDip);
        }
        else if (local.SourceWorkWidthDip <= 0d && local.LocalLeftDip + local.WidthDip <= targetWorkW + 1d)
        {
            resolvedLeftDip = Math.Max(0d, local.LocalLeftDip);
        }
        else
        {
            var freeX = Math.Max(0d, targetWorkW - local.WidthDip);
            resolvedLeftDip = Math.Clamp(local.AnchorX, 0d, 1d) * freeX;
        }

        double resolvedTopDip;
        if (local.SourceWorkHeightDip > 0d
            && Math.Abs(targetWorkH - local.SourceWorkHeightDip) <= 24d
            && local.LocalTopDip + local.HeightDip <= targetWorkH + 1d)
        {
            resolvedTopDip = Math.Max(0d, local.LocalTopDip);
        }
        else if (local.SourceWorkHeightDip <= 0d && local.LocalTopDip + local.HeightDip <= targetWorkH + 1d)
        {
            resolvedTopDip = Math.Max(0d, local.LocalTopDip);
        }
        else
        {
            var freeY = Math.Max(0d, targetWorkH - local.HeightDip);
            resolvedTopDip = Math.Clamp(local.AnchorY, 0d, 1d) * freeY;
        }

        return LocalDipToPhysicalBounds(
            resolvedLeftDip,
            resolvedTopDip,
            local.WidthDip,
            local.HeightDip,
            targetWorkAreaPx,
            targetDpi);
    }

    public static bool MigrateScreenKey(
        AppState state,
        string oldKey,
        string newKey,
        string? deviceName = null,
        string? displayLabel = null,
        string? hardwareModelId = null,
        string? friendlyName = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalizedOld = MultiScreenRules.NormalizeScreenKey(oldKey);
        var normalizedNew = MultiScreenRules.NormalizeScreenKey(newKey);
        if (normalizedOld is null
            || normalizedNew is null
            || string.Equals(normalizedOld, normalizedNew, StringComparison.Ordinal)
            || state.MultiScreen?.Screens is not { } screens
            || !screens.TryGetValue(normalizedOld, out var profile)
            || screens.ContainsKey(normalizedNew))
        {
            return false;
        }

        var normalizedDeviceName = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName.Trim();
        screens.Remove(normalizedOld);
        profile.MonitorKey = normalizedNew;
        if (normalizedDeviceName is not null)
        {
            profile.LastKnownDeviceName = normalizedDeviceName;
        }
        if (!string.IsNullOrWhiteSpace(displayLabel))
        {
            profile.DisplayLabel = displayLabel.Trim();
        }
        profile.HardwareModelId = MultiScreenRules.ExtractHardwareModelId(hardwareModelId)
            ?? MultiScreenRules.ExtractHardwareModelId(normalizedNew)
            ?? profile.HardwareModelId;
        profile.FriendlyName = MultiScreenRules.NormalizeFriendlyName(friendlyName)
            ?? profile.FriendlyName;
        screens[normalizedNew] = profile;

        foreach (var todo in state.TodoWidgets)
        {
            if (MatchesScreen(todo.ScreenKey, normalizedOld))
            {
                todo.ScreenKey = normalizedNew;
            }
        }
        foreach (var note in state.NoteWidgets)
        {
            if (MatchesScreen(note.ScreenKey, normalizedOld))
            {
                note.ScreenKey = normalizedNew;
            }
        }
        foreach (var box in state.FileBoxes)
        {
            if (MatchesScreen(box.ScreenKey, normalizedOld))
            {
                box.ScreenKey = normalizedNew;
                if (box.EdgeDockSide is not null || !string.IsNullOrWhiteSpace(box.EdgeDockMonitorDeviceName))
                {
                    if (normalizedDeviceName is not null)
                    {
                        box.EdgeDockMonitorDeviceName = normalizedDeviceName;
                    }
                    else
                    {
                        box.EdgeDockSide = null;
                        box.EdgeDockOffsetDip = null;
                        box.EdgeDockMonitorDeviceName = null;
                    }
                }
            }
        }

        return true;
    }

    public static int ReconcileConnectedProfileKeys(
        AppState state,
        IReadOnlyList<MonitorIdentityCandidate> connectedSecondary,
        string? primaryHardwareId = null,
        string? primaryModelId = null,
        string? primaryFriendlyName = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(connectedSecondary);

        if (state.MultiScreen?.Screens is not { Count: > 0 } screens || connectedSecondary.Count == 0)
        {
            return 0;
        }

        var normalizedPrimaryKey = MultiScreenRules.NormalizeScreenKey(primaryHardwareId);
        var excludedPrimaryModel = MultiScreenRules.ExtractHardwareModelId(primaryModelId)
            ?? MultiScreenRules.ExtractHardwareModelId(primaryHardwareId);
        var excludedPrimaryFriendly = MultiScreenRules.NormalizeFriendlyName(primaryFriendlyName);

        var claimedMonitors = new HashSet<string>(StringComparer.Ordinal);
        var claimedProfiles = new HashSet<string>(StringComparer.Ordinal);

        // Pass 1: exact MonitorKey matches take strict precedence.
        foreach (var monitor in connectedSecondary)
        {
            var key = MultiScreenRules.NormalizeScreenKey(monitor.MonitorKey);
            if (key is not null && screens.ContainsKey(key))
            {
                claimedMonitors.Add(key);
                claimedProfiles.Add(key);
            }
        }

        static string? EffectiveProfileModel(string key, ScreenProfileState profile) =>
            MultiScreenRules.ExtractHardwareModelId(profile.HardwareModelId)
            ?? MultiScreenRules.ExtractHardwareModelId(key);

        static string? EffectiveMonitorModel(MonitorIdentityCandidate monitor) =>
            MultiScreenRules.ExtractHardwareModelId(monitor.HardwareId)
            ?? MultiScreenRules.ExtractHardwareModelId(monitor.MonitorKey);

        var migrated = 0;

        // Pass 2: 1-to-1 unambiguous EDID hardware model match (e.g. re-plugged on a different HDMI/USB-C port).
        var unmatchedMonitorsByModel = connectedSecondary
            .Where(m => MultiScreenRules.NormalizeScreenKey(m.MonitorKey) is { } k && !claimedMonitors.Contains(k))
            .Select(m => (Candidate: m, ModelId: EffectiveMonitorModel(m)))
            .Where(x => x.ModelId is not null
                && !string.Equals(x.ModelId, excludedPrimaryModel, StringComparison.Ordinal))
            .GroupBy(x => x.ModelId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Candidate).ToList(), StringComparer.Ordinal);

        var offlineProfiles = screens
            .Where(pair => !claimedProfiles.Contains(pair.Key)
                && !string.Equals(pair.Key, normalizedPrimaryKey, StringComparison.Ordinal))
            .ToList();

        var offlineByModel = offlineProfiles
            .Select(pair => (pair.Key, Profile: pair.Value, ModelId: EffectiveProfileModel(pair.Key, pair.Value)))
            .Where(x => x.ModelId is not null
                && !string.Equals(x.ModelId, excludedPrimaryModel, StringComparison.Ordinal))
            .GroupBy(x => x.ModelId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var (modelId, monitorGroup) in unmatchedMonitorsByModel)
        {
            if (monitorGroup.Count != 1
                || !offlineByModel.TryGetValue(modelId, out var profileGroup)
                || profileGroup.Count != 1)
            {
                continue;
            }

            var targetMonitor = monitorGroup[0];
            var targetNewKey = MultiScreenRules.NormalizeScreenKey(targetMonitor.MonitorKey);
            var sourceOldKey = profileGroup[0].Key;
            if (targetNewKey is null
                || claimedMonitors.Contains(targetNewKey)
                || claimedProfiles.Contains(sourceOldKey))
            {
                continue;
            }

            if (MigrateScreenKey(
                    state,
                    sourceOldKey,
                    targetNewKey,
                    targetMonitor.DeviceName,
                    targetMonitor.DisplayLabel,
                    modelId,
                    targetMonitor.FriendlyName))
            {
                claimedMonitors.Add(targetNewKey);
                claimedProfiles.Add(targetNewKey);
                claimedProfiles.Add(sourceOldKey);
                migrated++;
            }
        }

        // Pass 3: 1-to-1 unambiguous non-generic FriendlyName fallback when hardware models do not conflict.
        // Group before excluding primary-model entries so two displays/profiles sharing a friendly name
        // remain ambiguous even if only one carries a known primary model ID.
        var remainingMonitorsByFriendly = connectedSecondary
            .Where(m => MultiScreenRules.NormalizeScreenKey(m.MonitorKey) is { } k && !claimedMonitors.Contains(k))
            .Select(m => (Candidate: m, Friendly: MultiScreenRules.NormalizeFriendlyName(m.FriendlyName)))
            .Where(x => x.Friendly is not null
                && !string.Equals(x.Friendly, excludedPrimaryFriendly, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.Friendly!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Candidate).ToList(), StringComparer.OrdinalIgnoreCase);

        var remainingProfilesByFriendly = screens
            .Where(pair => !claimedProfiles.Contains(pair.Key)
                && !string.Equals(pair.Key, normalizedPrimaryKey, StringComparison.Ordinal))
            .Select(pair => (pair.Key, Profile: pair.Value, Friendly: MultiScreenRules.NormalizeFriendlyName(pair.Value.FriendlyName)))
            .Where(x => x.Friendly is not null
                && !string.Equals(x.Friendly, excludedPrimaryFriendly, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.Friendly!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var (friendly, monitorGroup) in remainingMonitorsByFriendly)
        {
            if (monitorGroup.Count != 1
                || !remainingProfilesByFriendly.TryGetValue(friendly, out var profileGroup)
                || profileGroup.Count != 1)
            {
                continue;
            }

            var targetMonitor = monitorGroup[0];
            var sourceOldKey = profileGroup[0].Key;
            var monitorModel = EffectiveMonitorModel(targetMonitor);
            var profileModel = EffectiveProfileModel(sourceOldKey, profileGroup[0].Profile);
            if (excludedPrimaryModel is not null
                && (string.Equals(monitorModel, excludedPrimaryModel, StringComparison.Ordinal)
                    || string.Equals(profileModel, excludedPrimaryModel, StringComparison.Ordinal)))
            {
                continue;
            }
            if (monitorModel is not null
                && profileModel is not null
                && !string.Equals(monitorModel, profileModel, StringComparison.Ordinal))
            {
                continue;
            }

            var targetNewKey = MultiScreenRules.NormalizeScreenKey(targetMonitor.MonitorKey);
            if (targetNewKey is null
                || claimedMonitors.Contains(targetNewKey)
                || claimedProfiles.Contains(sourceOldKey))
            {
                continue;
            }

            if (MigrateScreenKey(
                    state,
                    sourceOldKey,
                    targetNewKey,
                    targetMonitor.DeviceName,
                    targetMonitor.DisplayLabel,
                    monitorModel,
                    targetMonitor.FriendlyName))
            {
                claimedMonitors.Add(targetNewKey);
                claimedProfiles.Add(targetNewKey);
                claimedProfiles.Add(sourceOldKey);
                migrated++;
            }
        }

        return migrated;
    }

    public static double ResolveScale(uint dpi) =>
        dpi > 0 ? dpi / DefaultDpi : 1d;

    public static WindowPlacement ClonePlacement(WindowPlacement source) => new()
    {
        Left = source.Left,
        Top = source.Top,
        Width = source.Width,
        Height = source.Height,
        DisplayLayouts = (source.DisplayLayouts ?? [])
            .Where(entry => entry is not null)
            .Select(entry => new DisplayLayoutPlacement
            {
                Layout = entry.Layout,
                Left = entry.Left,
                Top = entry.Top,
                Right = entry.Right,
                Bottom = entry.Bottom
            })
            .ToList(),
        MonitorLocal = source.MonitorLocal is null
            ? null
            : new MonitorLocalPlacement
            {
                LocalLeftDip = source.MonitorLocal.LocalLeftDip,
                LocalTopDip = source.MonitorLocal.LocalTopDip,
                WidthDip = source.MonitorLocal.WidthDip,
                HeightDip = source.MonitorLocal.HeightDip,
                AnchorX = source.MonitorLocal.AnchorX,
                AnchorY = source.MonitorLocal.AnchorY,
                SourceWorkWidthDip = source.MonitorLocal.SourceWorkWidthDip,
                SourceWorkHeightDip = source.MonitorLocal.SourceWorkHeightDip
            }
    };

    private static DesktopComponentState CloneComponent(DesktopComponentState source)
    {
        var copy = new DesktopComponentState
        {
            Kind = source.Kind,
            IsVisible = source.IsVisible,
            IsLocked = source.IsLocked,
            ShowDivider = source.ShowDivider,
            DividerThickness = source.DividerThickness,
            Density = source.Density,
            Placement = ClonePlacement(source.Placement),
            FontFamily = source.FontFamily,
            FontScale = source.FontScale,
            CornerRadius = source.CornerRadius
        };
        CopyAppearance(copy.Appearance, source.Appearance);
        return copy;
    }

    private static void CopyAppearance(WidgetAppearanceState target, WidgetAppearanceState source)
    {
        target.BackgroundColor = source.BackgroundColor;
        target.BackgroundOpacity = source.BackgroundOpacity;
        target.HeaderColor = source.HeaderColor;
        target.HeaderOpacity = source.HeaderOpacity;
        target.TextColor = source.TextColor;
        target.TitleFontFamily = source.TitleFontFamily;
        target.TitleFontBold = source.TitleFontBold;
        target.TitleFontItalic = source.TitleFontItalic;
    }
}
