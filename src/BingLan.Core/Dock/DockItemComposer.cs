using BingLan.Core.Models;

namespace BingLan.Core.Dock;

public enum DockItemState
{
    Pinned,
    Running,
    Active
}

public sealed record DockItem(
    string Key,
    string DisplayName,
    DockPinnedApp? Pinned,
    WindowGroup? Group)
{
    public bool IsPinned => Pinned is not null;
    public bool IsRunning => Group is not null;
    public int WindowCount => Group?.Windows.Count ?? 0;
    public bool HasMultipleWindows => WindowCount > 1;
    public bool IsActive => Group?.Windows.Any(window => window.IsForeground) ?? false;

    public DockItemState State =>
        IsActive
            ? DockItemState.Active
            : IsRunning
                ? DockItemState.Running
                : DockItemState.Pinned;
}

public static class DockItemComposer
{
    public static IReadOnlyList<DockItem> Compose(
        IReadOnlyList<DockPinnedApp> pinnedApps,
        IReadOnlyList<WindowGroup> runningGroups,
        IReadOnlyList<DockPinnedApp>? hiddenApps = null)
    {
        ArgumentNullException.ThrowIfNull(pinnedApps);
        ArgumentNullException.ThrowIfNull(runningGroups);

        if (hiddenApps is { Count: > 0 })
        {
            runningGroups = runningGroups
                .Where(group => !hiddenApps.Any(hidden => DockPinRules.Matches(hidden, group)))
                .ToArray();
        }

        var items = new List<DockItem>();
        var matchedGroups = new HashSet<WindowGroup>();
        foreach (var pinned in pinnedApps)
        {
            // Prefer the group with the pinned identity itself; an executable pin only
            // falls back to a group that carries its own app model ID (for example a
            // browser web app) when no plain group of that executable is running.
            var group = runningGroups
                .Where(candidate => !matchedGroups.Contains(candidate)
                    && DockPinRules.Matches(pinned, candidate))
                .OrderBy(candidate => string.Equals(
                    candidate.Key,
                    DockPinRules.IdentityKey(pinned),
                    StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
            if (group is not null)
            {
                matchedGroups.Add(group);
                items.Add(new DockItem(group.Key, pinned.DisplayName, pinned, group));
            }
            else
            {
                items.Add(new DockItem(
                    DockPinRules.IdentityKey(pinned),
                    pinned.DisplayName,
                    pinned,
                    null));
            }
        }

        foreach (var group in runningGroups)
        {
            if (!matchedGroups.Contains(group))
            {
                items.Add(new DockItem(group.Key, group.DisplayName, null, group));
            }
        }

        return items;
    }
}

public static class DockPinRules
{
    public static string IdentityKey(DockPinnedApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!string.IsNullOrWhiteSpace(app.AppUserModelId))
        {
            return $"aumid:{app.AppUserModelId.Trim()}";
        }

        return string.IsNullOrWhiteSpace(app.ExecutablePath)
            ? string.Empty
            : $"exe:{WindowGrouping.NormalizeExecutablePath(app.ExecutablePath)}";
    }

    public static bool Matches(DockPinnedApp pinned, WindowGroup group)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentNullException.ThrowIfNull(group);

        if (string.Equals(group.Key, IdentityKey(pinned), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // A pinned app model ID only matches the same ID; otherwise a pinned web app
        // would absorb every window of the browser that hosts it.
        if (!string.IsNullOrWhiteSpace(pinned.AppUserModelId)
            || string.IsNullOrWhiteSpace(pinned.ExecutablePath))
        {
            return false;
        }

        var pinnedPath = WindowGrouping.NormalizeExecutablePath(pinned.ExecutablePath);
        return group.Windows.Any(window =>
            !string.IsNullOrWhiteSpace(window.ExecutablePath)
            && string.Equals(
                WindowGrouping.NormalizeExecutablePath(window.ExecutablePath),
                pinnedPath,
                StringComparison.OrdinalIgnoreCase));
    }

    public static DockPinnedApp? CreateFromGroup(WindowGroup group, string displayName)
    {
        ArgumentNullException.ThrowIfNull(group);

        var appUserModelId = group.Key.StartsWith("aumid:", StringComparison.OrdinalIgnoreCase)
            ? group.Key["aumid:".Length..]
            : null;
        var executablePath = group.Windows
            .Select(window => window.ExecutablePath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (appUserModelId is null && executablePath is null)
        {
            return null;
        }

        return new DockPinnedApp
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? group.DisplayName : displayName.Trim(),
            AppUserModelId = appUserModelId,
            ExecutablePath = executablePath
        };
    }

    public static DockPinnedApp? CreateFromExecutable(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        var normalized = WindowGrouping.NormalizeExecutablePath(executablePath);
        var name = Path.GetFileNameWithoutExtension(normalized);
        return new DockPinnedApp
        {
            DisplayName = string.IsNullOrWhiteSpace(name) ? normalized : name,
            ExecutablePath = normalized
        };
    }

    public static bool Pin(DockState state, DockPinnedApp app, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(app);

        var key = IdentityKey(app);
        if (key.Length == 0 || IndexOf(state, key) >= 0)
        {
            return false;
        }

        var position = Math.Clamp(index ?? state.PinnedApps.Count, 0, state.PinnedApps.Count);
        state.PinnedApps.Insert(position, app);
        // Pinning an app asks to see it, so it no longer stays hidden.
        state.HiddenApps.RemoveAll(hidden => string.Equals(
            IdentityKey(hidden), key, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    public static bool Hide(DockState state, DockPinnedApp app)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(app);

        var key = IdentityKey(app);
        if (key.Length == 0 || state.HiddenApps.Any(hidden => string.Equals(
                IdentityKey(hidden), key, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        state.HiddenApps.Add(app);
        return true;
    }

    public static bool Unhide(DockState state, string identityKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.HiddenApps.RemoveAll(hidden => string.Equals(
            IdentityKey(hidden), identityKey, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    public static bool Unpin(DockState state, string identityKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        var index = IndexOf(state, identityKey);
        if (index < 0)
        {
            return false;
        }

        state.PinnedApps.RemoveAt(index);
        return true;
    }

    public static bool Move(DockState state, string identityKey, int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(state);
        var index = IndexOf(state, identityKey);
        if (index < 0)
        {
            return false;
        }

        var target = Math.Clamp(targetIndex, 0, state.PinnedApps.Count - 1);
        if (target == index)
        {
            return false;
        }

        var app = state.PinnedApps[index];
        state.PinnedApps.RemoveAt(index);
        state.PinnedApps.Insert(target, app);
        return true;
    }

    public static int IndexOf(DockState state, string identityKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(identityKey))
        {
            return -1;
        }

        return state.PinnedApps.FindIndex(app => string.Equals(
            IdentityKey(app),
            identityKey,
            StringComparison.OrdinalIgnoreCase));
    }

    public static void Normalize(DockState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!Enum.IsDefined(state.VisibilityMode))
        {
            state.VisibilityMode = DockVisibilityMode.ReserveWorkArea;
        }

        state.IconSize = double.IsFinite(state.IconSize)
            ? Math.Clamp(state.IconSize, DockState.MinimumIconSize, DockState.MaximumIconSize)
            : DockState.DefaultIconSize;

        state.BottomGapDip = double.IsFinite(state.BottomGapDip)
            ? Math.Clamp(state.BottomGapDip, DockState.MinimumBottomGapDip, DockState.MaximumBottomGapDip)
            : DockState.DefaultBottomGapDip;

        if ((state.HiddenHandleLeftDip is { } handleLeft && !double.IsFinite(handleLeft))
            || (state.HiddenHandleTopDip is { } handleTop && !double.IsFinite(handleTop))
            || (state.HiddenHandleLeftDip is null) != (state.HiddenHandleTopDip is null))
        {
            state.HiddenHandleLeftDip = null;
            state.HiddenHandleTopDip = null;
        }

        state.SurfaceColor = WidgetAppearanceRules.CoerceColorOrDefault(state.SurfaceColor, DockState.DefaultSurfaceColor);
        state.SurfaceOpacity = double.IsFinite(state.SurfaceOpacity)
            ? Math.Clamp(state.SurfaceOpacity, DesktopStyleRules.MinimumGlassDensity, DesktopStyleRules.MaximumGlassDensity)
            : DockState.DefaultSurfaceOpacity;

        state.MonitorDeviceName = string.IsNullOrWhiteSpace(state.MonitorDeviceName)
            ? null
            : state.MonitorDeviceName.Trim();

        state.PinnedApps = NormalizeApps(state.PinnedApps);
        state.HiddenApps = NormalizeApps(state.HiddenApps);
    }

    private static List<DockPinnedApp> NormalizeApps(List<DockPinnedApp>? apps)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<DockPinnedApp>();
        foreach (var app in apps ?? [])
        {
            if (app is null)
            {
                continue;
            }

            app.AppUserModelId = string.IsNullOrWhiteSpace(app.AppUserModelId)
                ? null
                : app.AppUserModelId.Trim();
            app.ExecutablePath = string.IsNullOrWhiteSpace(app.ExecutablePath)
                ? null
                : WindowGrouping.NormalizeExecutablePath(app.ExecutablePath);
            app.Arguments = string.IsNullOrWhiteSpace(app.Arguments) ? null : app.Arguments.Trim();
            app.WorkingDirectory = string.IsNullOrWhiteSpace(app.WorkingDirectory)
                ? null
                : app.WorkingDirectory.Trim();
            var key = IdentityKey(app);
            if (key.Length == 0 || !seen.Add(key))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(app.DisplayName))
            {
                app.DisplayName = app.ExecutablePath is not null
                    ? Path.GetFileNameWithoutExtension(app.ExecutablePath)
                    : app.AppUserModelId!;
            }
            app.DisplayName = app.DisplayName.Trim();
            normalized.Add(app);
        }

        return normalized;
    }
}
