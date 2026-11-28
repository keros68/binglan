using BingLan.Core.Dock;
using BingLan.Core.Services;

namespace BingLan.Core.Models;

public sealed record MonitorIdentityCandidate(
    string MonitorKey,
    string? HardwareId,
    string? DeviceName,
    string? FriendlyName,
    string? DisplayLabel,
    string? LegacyDeviceKey = null);

/// <summary>
/// Result of copying the primary display's desktop card profile to a secondary monitor.
/// </summary>
public sealed record CopyPrimaryProfileResult(
    ScreenProfileState Profile,
    IReadOnlyDictionary<DesktopComponentKind, PixelRect> ComponentPixelBounds,
    TodoWidgetState? CreatedTodo,
    PixelRect? CreatedTodoPixelBounds);

/// <summary>
/// Unified pure accessor bridging the root profile (stored in <see cref="AppState"/>
/// fields, addressed by a null key) and independent displays (stored in <see cref="AppState.MultiScreen"/>), plus
/// target-monitor local-DIP to physical-pixel placement rules.
/// </summary>
public static class ScreenProfileAccessor
{
    public const double DefaultDpi = 96d;
    public const double MinimumWidgetWidthDip = 226d;
    public const double MinimumWidgetHeightDip = 166d;

    public static bool IsPrimary(string? screenKey) =>
        MultiScreenRules.NormalizeScreenKey(screenKey) is null;

    public static IReadOnlyList<FileBoxState> FilterPrimaryFileBoxes(IEnumerable<FileBoxState> boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        return boxes.Where(box => IsPrimary(box.ScreenKey)).ToList();
    }

    public static bool IsMultiScreenEnabled(AppState state) =>
        state?.MultiScreen is { IsEnabled: true };

    /// <summary>Explicitly pins the existing root layout without copying or moving any card.</summary>
    public static bool BindLegacyRootToScreen(AppState state, string screenKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalized = MultiScreenRules.NormalizeScreenKey(screenKey);
        if (normalized is null
            || MultiScreenRules.NormalizeScreenKey(state.MultiScreen?.RootMonitorKey) is not null
            || state.MultiScreen?.Screens.ContainsKey(normalized) == true
            || HasCardsForScreen(state, normalized))
        {
            return false;
        }

        state.MultiScreen ??= new MultiScreenState();
        state.MultiScreen.RootMonitorKey = normalized;
        return true;
    }

    private static bool HasCardsForScreen(AppState state, string screenKey) =>
        state.TodoWidgets.Any(todo => MatchesScreen(todo.ScreenKey, screenKey))
        || state.NoteWidgets.Any(note => MatchesScreen(note.ScreenKey, screenKey))
        || state.FileBoxes.Any(box => MatchesScreen(box.ScreenKey, screenKey));

    public static bool CanBindArchivedProfile(AppState state, string targetKey) =>
        MultiScreenRules.NormalizeScreenKey(targetKey) is { } target
        && state.MultiScreen is { IsEnabled: true } multiScreen
        && !MatchesScreen(multiScreen.RootMonitorKey, target)
        && !multiScreen.Screens.ContainsKey(target)
        && !multiScreen.SurfacePreferences.ContainsKey(target)
        && !HasCardsForScreen(state, target);

    public static bool BindArchivedProfile(AppState state, string sourceKey, MonitorIdentityCandidate target)
    {
        var source = MultiScreenRules.NormalizeScreenKey(sourceKey);
        var destination = MultiScreenRules.NormalizeScreenKey(target.MonitorKey);
        if (source is null || destination is null || MatchesScreen(source, destination)
            || !CanBindArchivedProfile(state, destination) || state.MultiScreen is not { } multiScreen)
        {
            return false;
        }

        if (MatchesScreen(multiScreen.RootMonitorKey, source))
        {
            multiScreen.RootMonitorKey = destination;
        }
        else if (multiScreen.Screens.ContainsKey(source))
        {
            return MigrateScreenKey(state, source, destination, target.DeviceName, target.DisplayLabel,
                target.HardwareId, target.FriendlyName);
        }
        else if (!multiScreen.SurfacePreferences.ContainsKey(source))
        {
            return false;
        }

        if (multiScreen.SurfacePreferences.Remove(source, out var preferences))
        {
            preferences.DisplayLabel = target.DisplayLabel;
            multiScreen.SurfacePreferences[destination] = preferences;
        }
        return true;
    }

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
            string? currentLayoutKey = null,
            bool audioSpectrumVisible = false)
    {
        var scale = ResolveScale(targetDpi);
        var localWidthDip = targetWorkAreaPx.HasArea ? Math.Max(1d, targetWorkAreaPx.Width / scale) : 1920d;
        var localHeightDip = targetWorkAreaPx.HasArea ? Math.Max(1d, targetWorkAreaPx.Height / scale) : 1040d;
        var localPresets = DesktopExperienceRules.PresetPlacements(
            preset, 0d, 0d, localWidthDip, localHeightDip, audioSpectrumVisible);

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

        var computed = ComputePresetPlacementsForMonitor(preset, targetWorkAreaPx, targetDpi, currentLayoutKey,
            experience.GetComponent(DesktopComponentKind.AudioVisualizer).IsVisible);
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

    public static CopyPrimaryProfileResult CopyPrimaryProfileToScreen(
        AppState state,
        string targetScreenKey,
        PixelRect targetWorkAreaPx,
        uint targetDpi,
        string? deviceName = null,
        string? displayLabel = null,
        string? currentLayoutKey = null)
    {
        var profile = EnsureScreenProfile(state, targetScreenKey, deviceName, displayLabel);
        var (bounds, created) = CopyScreenCardSettings(state,
            (null, default, 96), (targetScreenKey, targetWorkAreaPx, targetDpi), currentLayoutKey);
        return new CopyPrimaryProfileResult(profile, bounds, created,
            created is not null && bounds.TryGetValue(DesktopComponentKind.Todo, out var todoBounds) ? todoBounds : null);
    }

    /// <summary>Copies card settings between physical displays while keeping each screen's personal content.</summary>
    public static (IReadOnlyDictionary<DesktopComponentKind, PixelRect> Bounds, TodoWidgetState? CreatedTodo)
        CopyScreenCardSettings(
            AppState state,
            (string? Key, PixelRect WorkArea, uint Dpi) source,
            (string? Key, PixelRect WorkArea, uint Dpi) target,
            string? layoutKey = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (MatchesScreen(source.Key, target.Key))
        {
            throw new ArgumentException("Source and target must be different screens.", nameof(target));
        }

        var sourceExperience = GetExperience(state, source.Key);
        var targetExperience = GetExperience(state, target.Key);
        DesktopExperienceRules.Normalize(sourceExperience);
        DesktopExperienceRules.Normalize(targetExperience);
        var copiedExperience = new DesktopExperienceState
        {
            ActivePreset = sourceExperience.ActivePreset,
            ExtensionData = targetExperience.ExtensionData,
            Components = sourceExperience.Components.Select(CloneComponent).ToList()
        };
        foreach (var component in copiedExperience.Components)
        {
            var targetComponent = targetExperience.GetComponent(component.Kind);
            component.Placement = ClonePlacement(targetComponent.Placement);
            component.ExtensionData = targetComponent.ExtensionData;
        }
        if (target.Key is { } targetKey)
        {
            EnsureScreenProfile(state, targetKey).Experience = copiedExperience;
        }
        else
        {
            // Settings windows hold the root experience by reference.
            targetExperience.ActivePreset = copiedExperience.ActivePreset;
            targetExperience.Components = copiedExperience.Components;
        }

        var targetPlaces = GetQuickPlaces(state, target.Key);
        if (AreQuickPlacesEquivalent(QuickPlaceRules.Normalize(targetPlaces), QuickPlaceRules.CreateDefaults()))
        {
            SetQuickPlaces(state, target.Key, GetQuickPlaces(state, source.Key).Select(place => new QuickPlaceState
            {
                Icon = place.Icon,
                Name = place.Name,
                Target = place.Target
            }).ToList());
        }

        var bounds = new Dictionary<DesktopComponentKind, PixelRect>(ApplyPresetToScreen(
            state, target.Key, copiedExperience.ActivePreset, target.WorkArea, target.Dpi, layoutKey,
            preserveGreetingColor: true));
        if (source.WorkArea.HasArea)
        {
            foreach (var component in copiedExperience.Components)
            {
                var sourcePlacement = sourceExperience.GetComponent(component.Kind).Placement;
                if (GetSourceCardBounds(sourcePlacement, source.WorkArea, source.Dpi, layoutKey) is not { } sourceBounds)
                {
                    continue;
                }
                var pixels = ComputeMovedWidgetPixelBounds(sourceBounds, source.WorkArea, source.Dpi,
                    target.WorkArea, target.Dpi, sourcePlacement.Width, sourcePlacement.Height);
                var scale = ResolveScale(target.Dpi);
                var placement = component.Placement;
                placement.Left = pixels.Left / scale;
                placement.Top = pixels.Top / scale;
                placement.Width = pixels.Width / scale;
                placement.Height = pixels.Height / scale;
                RecordMonitorLocalPlacement(placement, pixels, target.WorkArea, target.Dpi);
                if (!string.IsNullOrEmpty(layoutKey))
                {
                    DisplayLayoutRules.Remember(placement, layoutKey, pixels);
                }
                bounds[component.Kind] = pixels;
            }
        }

        var todoComponent = copiedExperience.GetComponent(DesktopComponentKind.Todo);
        if (GetPrimaryTodoId(state, target.Key) is { } id && state.TodoWidgets.FirstOrDefault(todo => todo.Id == id) is { } existing)
        {
            existing.Placement = ClonePlacement(todoComponent.Placement);
        }

        if (!todoComponent.IsVisible || GetTodosForScreen(state, target.Key).Count != 0)
        {
            return (bounds, null);
        }

        var created = TodoService.CreateDefaultWidget();
        created.Title = WidgetTitleRules.NextTitle(created.Title, state.TodoWidgets.Select(todo => todo.Title));
        created.ScreenKey = target.Key;
        created.IsLocked = todoComponent.IsLocked;
        created.CornerRadius = todoComponent.CornerRadius;
        created.FontScale = todoComponent.FontScale;
        created.FontFamily = todoComponent.FontFamily;
        CopyAppearance(created.Appearance, todoComponent.Appearance);
        created.Placement = ClonePlacement(todoComponent.Placement);
        state.TodoWidgets.Add(created);
        SetPrimaryTodoId(state, target.Key, created.Id);
        return (bounds, created);
    }

    private static PixelRect? GetSourceCardBounds(WindowPlacement placement, PixelRect workArea, uint dpi, string? layoutKey)
    {
        if (!string.IsNullOrEmpty(layoutKey) && DisplayLayoutRules.TryRecall(placement, layoutKey, out var remembered)
            && remembered.Intersects(workArea))
        {
            return remembered;
        }
        if (ResolveMonitorLocalBounds(placement, workArea, dpi) is { } localBounds)
        {
            return localBounds;
        }
        var scale = ResolveScale(dpi);
        var bounds = new PixelRect(
            (int)Math.Round(placement.Left * scale), (int)Math.Round(placement.Top * scale),
            (int)Math.Round((placement.Left + placement.Width) * scale),
            (int)Math.Round((placement.Top + placement.Height) * scale));
        return bounds.HasArea && bounds.Intersects(workArea) ? bounds : null;
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
            || screens.ContainsKey(normalizedNew)
            || state.MultiScreen.SurfacePreferences.ContainsKey(normalizedNew)
            || MatchesScreen(state.MultiScreen.RootMonitorKey, normalizedNew)
            || HasCardsForScreen(state, normalizedNew))
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
        if (state.MultiScreen.SurfacePreferences.Remove(normalizedOld, out var surfacePreferences))
        {
            surfacePreferences.DisplayLabel = displayLabel ?? surfacePreferences.DisplayLabel;
            state.MultiScreen.SurfacePreferences[normalizedNew] = surfacePreferences;
        }

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

    /// <summary>
    /// Upgrades only an exact old device path supplied by the connected monitor identity.
    /// Model, friendly name, GDI display number and port similarity are not ownership evidence.
    /// </summary>
    public static int ReconcileConnectedProfileKeys(
        AppState state,
        IReadOnlyList<MonitorIdentityCandidate> connectedMonitors)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(connectedMonitors);
        if (state.MultiScreen?.Screens is not { Count: > 0 } screens)
        {
            return 0;
        }

        var candidates = connectedMonitors
            .Select(monitor => (Monitor: monitor, LegacyKey: NormalizeLegacyDeviceKey(monitor.LegacyDeviceKey)))
            .Where(candidate => candidate.LegacyKey is not null)
            .GroupBy(candidate => candidate.LegacyKey!, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .ToArray();
        var migrated = 0;
        foreach (var (monitor, legacyKey) in candidates)
        {
            var matchingKeys = screens.Keys
                .Where(key => string.Equals(NormalizeLegacyDeviceKey(key), legacyKey, StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matchingKeys.Length != 1)
            {
                continue;
            }

            if (MigrateScreenKey(state, matchingKeys[0], monitor.MonitorKey,
                monitor.DeviceName, monitor.DisplayLabel, monitor.HardwareId, monitor.FriendlyName))
            {
                migrated++;
            }
        }
        return migrated;
    }

    private static string? NormalizeLegacyDeviceKey(string? key)
    {
        var normalized = MultiScreenRules.NormalizeScreenKey(key);
        if (normalized is null)
        {
            return null;
        }
        if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            normalized = normalized[4..];
        }
        var guidIndex = normalized.IndexOf("#{", StringComparison.Ordinal);
        if (guidIndex > 0)
        {
            normalized = normalized[..guidIndex];
        }
        var parts = normalized.Split('#');
        return parts.Length == 3
            && string.Equals(parts[0], "DISPLAY", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(parts[1])
            && !string.IsNullOrWhiteSpace(parts[2])
            ? normalized.ToUpperInvariant()
            : null;
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
            SpectrumBarWidth = source.SpectrumBarWidth,
            SpectrumStyle = source.SpectrumStyle,
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
