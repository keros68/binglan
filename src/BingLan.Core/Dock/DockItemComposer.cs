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
    public DockPinnedApp? TrayApp { get; init; }
    public bool IsRunningInBackground { get; init; }
    public bool IsInRunningSection { get; init; } = Pinned is null;
    public bool IsPinned => Pinned is not null;
    public bool IsRunning => Group is not null || IsRunningInBackground || TrayApp is not null;
    public DockPinnedApp? LaunchTarget => Pinned ?? TrayApp;
    public string RunningOrderKey
    {
        get
        {
            // Tray-only entries know the executable path and may have no window app ID.
            var trayExecutable = Group?.Key.StartsWith("aumid:", StringComparison.OrdinalIgnoreCase) == true
                ? Group.Windows.Select(window => window.ExecutablePath)
                    .FirstOrDefault(TrayAppRevealRules.PreventsRelaunchWhenRunning)
                : null;
            return trayExecutable is null ? Key : $"exe:{WindowGrouping.NormalizeExecutablePath(trayExecutable)}";
        }
    }
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

public readonly record struct RunningAppProcess(string? ExecutablePath, string? AppUserModelId);

public static class DockItemComposer
{
    public static IReadOnlyList<DockItem> Compose(
        IReadOnlyList<DockPinnedApp> pinnedApps,
        IReadOnlyList<WindowGroup> runningGroups,
        IReadOnlyList<DockPinnedApp>? hiddenApps = null,
        IReadOnlySet<DockBackgroundPinMatch>? backgroundMatches = null,
        IReadOnlyCollection<string>? runningTrayExecutables = null,
        IReadOnlyList<string>? previousRunningOrder = null,
        bool showRunningPinsSeparately = false)
    {
        ArgumentNullException.ThrowIfNull(pinnedApps);
        ArgumentNullException.ThrowIfNull(runningGroups);

        var allGroups = runningGroups;
        if (hiddenApps is { Count: > 0 })
        {
            runningGroups = runningGroups
                .Where(group => !hiddenApps.Any(hidden => DockPinRules.Matches(hidden, group)))
                .ToArray();
        }

        var items = new List<DockItem>();
        var matchedGroups = new HashSet<WindowGroup>();
        var pinnedGroups = new WindowGroup?[pinnedApps.Count];
        // Reserve every exact identity before offering executable and launcher fallbacks.
        MatchPins(DockPinRules.MatchesExactIdentity);
        MatchPins(DockPinRules.Matches);
        MatchPins(DockPinRules.MatchesLauncherStub);

        for (var index = 0; index < pinnedApps.Count; index++)
        {
            var pinned = pinnedApps[index];
            if (pinnedGroups[index] is { } group)
            {
                items.Add(new DockItem(group.Key, pinned.DisplayName, pinned, group));
            }
            else
            {
                items.Add(new DockItem(DockPinRules.IdentityKey(pinned), pinned.DisplayName, pinned, null));
            }
        }

        var runningItems = new List<DockItem>();
        foreach (var group in runningGroups)
        {
            if (!matchedGroups.Contains(group))
            {
                runningItems.Add(new DockItem(group.Key, group.DisplayName, null, group));
            }
        }

        var trayApps = new Dictionary<string, DockPinnedApp>(StringComparer.OrdinalIgnoreCase);
        if (runningTrayExecutables is { Count: > 0 })
        {
            var visiblePaths = allGroups.SelectMany(group => group.Windows)
                .Where(window => !string.IsNullOrWhiteSpace(window.ExecutablePath))
                .Select(window => WindowGrouping.NormalizeExecutablePath(window.ExecutablePath!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in runningTrayExecutables)
            {
                if (TrayAppRevealRules.PreventsRelaunchWhenRunning(path)
                    && DockPinRules.CreateFromExecutable(path) is { ExecutablePath: { } executable } app
                    && !visiblePaths.Contains(executable))
                {
                    trayApps.TryAdd(executable, app);
                }
            }
        }

        if (backgroundMatches is { Count: > 0 })
        {
            var verifiedKeys = DockBackgroundPinMatcher.VerifyMatches(items, allGroups, backgroundMatches);
            if (trayApps.Count > 0)
            {
                // A process AUMID takes precedence over a path-only match for the same
                // tray executable, such as when the snapshot also includes its helpers.
                var identifiedPaths = backgroundMatches
                    .Where(match => match.ProcessExecutable is null
                        && verifiedKeys.Contains(match.PinKey))
                    .Select(match => match.IdentityExecutablePath)
                    .OfType<string>()
                    .Where(trayApps.ContainsKey)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (identifiedPaths.Count > 0)
                {
                    var distinctMatches = backgroundMatches
                        .Where(match => match.ProcessExecutable is not { } path || !identifiedPaths.Contains(path))
                        .ToHashSet();
                    if (distinctMatches.Count != backgroundMatches.Count)
                    {
                        backgroundMatches = distinctMatches;
                        verifiedKeys = DockBackgroundPinMatcher.VerifyMatches(items, allGroups, backgroundMatches);
                    }
                }

                foreach (var match in backgroundMatches)
                {
                    if (verifiedKeys.Contains(match.PinKey)
                        && (match.ProcessExecutable ?? match.IdentityExecutablePath) is { } path)
                    {
                        trayApps.Remove(path);
                    }
                }
            }

            for (var index = 0; index < pinnedApps.Count; index++)
            {
                var item = items[index];
                if (item.Group is null && verifiedKeys.Contains(item.Key))
                {
                    items[index] = item with { IsRunningInBackground = true };
                }
            }
        }

        foreach (var (executable, app) in trayApps)
        {
            var key = DockPinRules.IdentityKey(app);
            var exactOwner = items.FindIndex(item => item.Pinned is { } pin
                && StringComparer.OrdinalIgnoreCase.Equals(DockPinRules.IdentityKey(pin), key));
            if (exactOwner >= 0)
            {
                var item = items[exactOwner];
                if (item.Group is null)
                {
                    items[exactOwner] = item with { IsRunningInBackground = true };
                }
            }
            else if (hiddenApps?.Any(hidden => hidden.ExecutablePath is { Length: > 0 } hiddenPath
                && StringComparer.OrdinalIgnoreCase.Equals(
                    WindowGrouping.NormalizeExecutablePath(hiddenPath), executable)) != true)
            {
                app.DisplayName = AppDisplayNameRules.Normalize(app.DisplayName, executable) ?? app.DisplayName;
                runningItems.Add(new DockItem(key, app.DisplayName, null, null)
                {
                    TrayApp = app,
                    IsRunningInBackground = true
                });
            }
        }

        if (showRunningPinsSeparately)
        {
            runningItems.InsertRange(0, items.Where(item => item.IsRunning)
                .Select(item => item with { IsInRunningSection = true }));
            items.RemoveAll(item => item.IsRunning);
        }

        items.AddRange(previousRunningOrder is { Count: > 0 }
            ? WindowGrouping.OrderByFirstSeen(runningItems, previousRunningOrder, item => item.RunningOrderKey)
            : runningItems);
        return items;

        void MatchPins(Func<DockPinnedApp, WindowGroup, bool> matches)
        {
            for (var index = 0; index < pinnedApps.Count; index++)
            {
                if (pinnedGroups[index] is not null)
                {
                    continue;
                }

                var pinned = pinnedApps[index];
                var group = runningGroups.FirstOrDefault(candidate =>
                    !matchedGroups.Contains(candidate) && matches(pinned, candidate));
                if (group is not null)
                {
                    matchedGroups.Add(group);
                    pinnedGroups[index] = group;
                }
            }
        }
    }
}

public static class DockPinRules
{
    private const string ComputerParsingName = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
    private const string RecycleBinParsingName = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    public static DockPinnedApp Copy(DockPinnedApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return new DockPinnedApp
        {
            DisplayName = app.DisplayName,
            ExecutablePath = app.ExecutablePath,
            AppUserModelId = app.AppUserModelId,
            TargetPath = app.TargetPath,
            Arguments = app.Arguments,
            WorkingDirectory = app.WorkingDirectory,
            IconFile = app.IconFile,
            ExtensionData = app.ExtensionData is null
                ? null
                : new Dictionary<string, System.Text.Json.JsonElement>(app.ExtensionData)
        };
    }

    public static string IdentityKey(DockPinnedApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!string.IsNullOrWhiteSpace(app.AppUserModelId))
        {
            return $"aumid:{app.AppUserModelId.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(app.ExecutablePath))
        {
            return $"exe:{WindowGrouping.NormalizeExecutablePath(app.ExecutablePath)}";
        }

        return string.IsNullOrWhiteSpace(app.TargetPath)
            ? string.Empty
            : $"path:{NormalizeTargetPath(app.TargetPath)}";
    }

    /// <summary>Whether the pin opens a folder, file or system place.</summary>
    public static bool IsPathPin(DockPinnedApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return string.IsNullOrWhiteSpace(app.AppUserModelId)
            && string.IsNullOrWhiteSpace(app.ExecutablePath)
            && !string.IsNullOrWhiteSpace(app.TargetPath);
    }

    /// <summary>A pin for a folder or file, named after it, or null for an empty path.</summary>
    public static DockPinnedApp? CreateFromPath(string path, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = NormalizeTargetPath(path);
        var name = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(normalized) : displayName.Trim();
        return new DockPinnedApp
        {
            DisplayName = string.IsNullOrWhiteSpace(name) ? normalized : name,
            TargetPath = normalized
        };
    }

    public static DockPinnedApp? CreateFromSystemPlace(string? path) => NormalizeSystemPlace(path) switch
    {
        QuickPlaceRules.ComputerTarget => CreateFromPath(QuickPlaceRules.ComputerTarget, "此电脑"),
        QuickPlaceRules.RecycleBinTarget => CreateFromPath(QuickPlaceRules.RecycleBinTarget, "回收站"),
        _ => null
    };

    public static bool IsSystemPlace(string? path) => NormalizeSystemPlace(path) is not null;

    private static string? NormalizeSystemPlace(string? path)
    {
        var target = path?.Trim();
        if (string.Equals(target, ComputerParsingName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(target, QuickPlaceRules.ComputerTarget, StringComparison.OrdinalIgnoreCase))
        {
            return QuickPlaceRules.ComputerTarget;
        }
        if (string.Equals(target, RecycleBinParsingName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(target, QuickPlaceRules.RecycleBinTarget, StringComparison.OrdinalIgnoreCase))
        {
            return QuickPlaceRules.RecycleBinTarget;
        }
        return null;
    }

    // A folder path with or without a trailing separator is the same pin; a drive root
    // keeps its separator.
    private static string NormalizeTargetPath(string path)
    {
        if (NormalizeSystemPlace(path) is { } place)
        {
            return place;
        }
        var full = WindowGrouping.NormalizeExecutablePath(path);
        var root = Path.GetPathRoot(full);
        return full.Length > (root?.Length ?? 0)
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }

    public static bool Matches(DockPinnedApp pinned, WindowGroup group)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentNullException.ThrowIfNull(group);

        if (MatchesExactIdentity(pinned, group))
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
        var isWebAppShortcut = DockQuitRules.IsBrowserWebApp(pinnedPath, null, pinned.Arguments);
        return group.Windows.Any(window =>
            !string.IsNullOrWhiteSpace(window.ExecutablePath)
            && string.Equals(
                WindowGrouping.NormalizeExecutablePath(window.ExecutablePath),
                pinnedPath,
                StringComparison.OrdinalIgnoreCase)
            && DockQuitRules.IsBrowserWebApp(pinnedPath, window.AppUserModelId, null) == isWebAppShortcut);
    }

    internal static bool MatchesExactIdentity(DockPinnedApp pinned, WindowGroup group) =>
        string.Equals(group.Key, IdentityKey(pinned), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a pin of a launcher stub stands for the running group: the pin has no
    /// app model ID, and a window comes from a related exe in a version or internal
    /// subfolder of the same installation.
    /// </summary>
    public static bool MatchesLauncherStub(DockPinnedApp pinned, WindowGroup group)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentNullException.ThrowIfNull(group);

        if (!string.IsNullOrWhiteSpace(pinned.AppUserModelId)
            || string.IsNullOrWhiteSpace(pinned.ExecutablePath))
        {
            return false;
        }

        var pinnedPath = WindowGrouping.NormalizeExecutablePath(pinned.ExecutablePath);
        var squirrelTarget = DockQuitRules.ExtractSquirrelStartExecutable(pinned);
        return group.Windows.Any(window => !string.IsNullOrWhiteSpace(window.ExecutablePath)
            && !DockQuitRules.IsBrowserWebApp(window.ExecutablePath, window.AppUserModelId, null)
            && MatchesLaunchTarget(WindowGrouping.NormalizeExecutablePath(window.ExecutablePath)));

        bool MatchesLaunchTarget(string executable) => squirrelTarget is null
            ? DockQuitRules.IsSubfolderStubPair(pinnedPath, executable)
            : Path.GetDirectoryName(pinnedPath) is { Length: > 0 } directory
                && DockQuitRules.MatchesSquirrelTarget(directory, squirrelTarget, executable);
    }

    /// <summary>
    /// Whether a process with this exe path stands for the pin: the same exe, or an exe
    /// the launcher-stub rule of <see cref="MatchesLauncherStub"/> accepts.
    /// </summary>
    public static bool MatchesExecutablePath(DockPinnedApp pinned, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        if (string.IsNullOrWhiteSpace(executablePath)
            || !string.IsNullOrWhiteSpace(pinned.AppUserModelId)
            || string.IsNullOrWhiteSpace(pinned.ExecutablePath))
        {
            return false;
        }

        var pinnedPath = WindowGrouping.NormalizeExecutablePath(pinned.ExecutablePath);
        var path = WindowGrouping.NormalizeExecutablePath(executablePath);
        if (string.Equals(pinnedPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return DockQuitRules.IsSubfolderStubPair(pinnedPath, path);
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
        state.Edge = DockEdgeRules.Normalize(state.Edge);
        if (!Enum.IsDefined(state.VisibilityMode))
        {
            state.VisibilityMode = DockVisibilityMode.ReserveWorkArea;
        }

        if (!Enum.IsDefined(state.RevealMode))
        {
            state.RevealMode = DockRevealMode.EdgeAndHandle;
        }

        state.IconSize = double.IsFinite(state.IconSize)
            ? Math.Clamp(state.IconSize, DockState.MinimumIconSize, DockState.MaximumIconSize)
            : DockState.DefaultIconSize;

        state.BottomGapDip = double.IsFinite(state.BottomGapDip)
            ? Math.Clamp(state.BottomGapDip, DockState.MinimumBottomGapDip, DockState.MaximumBottomGapDip)
            : DockState.DefaultBottomGapDip;

        state.HoverMagnificationScale = DockMagnificationRules.ClampScale(state.HoverMagnificationScale);

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
            app.TargetPath = string.IsNullOrWhiteSpace(app.TargetPath)
                ? null
                : NormalizeTargetPath(app.TargetPath);
            if (IsPathPin(app))
            {
                app.Arguments = null;
                app.WorkingDirectory = null;
                app.IconFile = null;
            }
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
                    : app.AppUserModelId ?? Path.GetFileName(app.TargetPath!);
                if (string.IsNullOrWhiteSpace(app.DisplayName))
                {
                    app.DisplayName = app.TargetPath ?? string.Empty;
                }
            }
            app.DisplayName = app.DisplayName.Trim();
            normalized.Add(app);
        }

        return normalized;
    }
}
