using System.Collections.Frozen;
using BingLan.Core.Models;

namespace BingLan.Core.Dock;

/// <summary>Process evidence for a pin, to be checked against the latest window grouping.</summary>
public readonly record struct DockBackgroundPinMatch(
    string PinKey, string? PinnedExecutable, string? PinnedArguments, string? ProcessExecutable)
{
    public string? IdentityExecutablePath { get; init; }
}

/// <summary>Read-only index of pinned application identities for background process snapshots.</summary>
public sealed class DockBackgroundPinMatcher
{
    private static readonly StringComparer IdentityComparer = StringComparer.OrdinalIgnoreCase;

    private readonly DockPinnedApp[] _pins;
    private readonly FrozenDictionary<string, DockPinnedApp> _pinsByKey;
    private readonly ILookup<string, ExecutablePin> _exactPaths;
    private readonly ILookup<string, ExecutablePin> _launcherDirectories;

    public DockBackgroundPinMatcher(IReadOnlyList<DockPinnedApp> pinnedApps)
    {
        ArgumentNullException.ThrowIfNull(pinnedApps);
        _pins = pinnedApps.Where(pin => !DockPinRules.IsPathPin(pin))
            .Select(pin => new DockPinnedApp
            {
                DisplayName = pin.DisplayName,
                ExecutablePath = pin.ExecutablePath,
                AppUserModelId = pin.AppUserModelId,
                Arguments = pin.Arguments
            })
            .ToArray();
        _pinsByKey = _pins.DistinctBy(DockPinRules.IdentityKey, IdentityComparer)
            .ToFrozenDictionary(DockPinRules.IdentityKey, IdentityComparer);

        var executablePins = new List<ExecutablePin>();
        foreach (var pin in _pins)
        {
            if (string.IsNullOrWhiteSpace(pin.ExecutablePath) || DockQuitRules.IsProtectedExecutable(pin.ExecutablePath)
                || DockFileDropRules.IsFrameHostPath(pin.ExecutablePath)
                || DockQuitRules.IsProtectedAumid(pin.AppUserModelId))
            {
                continue;
            }

            var path = WindowGrouping.NormalizeExecutablePath(pin.ExecutablePath);
            var hasAppId = !string.IsNullOrWhiteSpace(pin.AppUserModelId);
            var key = DockPinRules.IdentityKey(pin);
            if (DockQuitRules.IsBrowserHostExecutable(path)
                && (hasAppId || DockQuitRules.IsSharedBrowserPwa(new DockItem(key, pin.DisplayName, pin, null))))
            {
                continue;
            }

            executablePins.Add(new ExecutablePin(key, path, hasAppId, DockQuitRules.ExtractSquirrelStartExecutable(pin)));
        }

        _exactPaths = executablePins.Where(pin => pin.SquirrelTarget is null)
            .ToLookup(pin => pin.Path, IdentityComparer);
        _launcherDirectories = executablePins.ToLookup(pin => Path.GetDirectoryName(pin.Path) ?? string.Empty, IdentityComparer);
    }

    /// <summary>
    /// Resolves pinned keys for live processes, including processes whose own windows are
    /// still open. A window owned by a different item blocks executable-only inference.
    /// </summary>
    public IReadOnlySet<string> Match(
        IReadOnlyList<WindowGroup> runningGroups,
        IReadOnlyList<RunningAppProcess> runningProcesses) =>
        VerifyMatches(DockItemComposer.Compose(_pins, runningGroups), runningGroups, MatchProcesses(runningProcesses));

    public bool HasSamePins(IReadOnlyList<DockPinnedApp> pins)
    {
        var count = 0;
        foreach (var pin in pins)
        {
            if (DockPinRules.IsPathPin(pin))
            {
                continue;
            }
            if (!_pinsByKey.TryGetValue(DockPinRules.IdentityKey(pin), out var previous)
                || pin.ExecutablePath != previous.ExecutablePath || pin.Arguments != previous.Arguments)
            {
                return false;
            }
            count++;
        }
        return count == _pins.Length;
    }

    public IReadOnlySet<DockBackgroundPinMatch> MatchProcesses(IReadOnlyList<RunningAppProcess> runningProcesses)
    {
        ArgumentNullException.ThrowIfNull(runningProcesses);
        if (_pins.Length == 0 || runningProcesses.Count == 0)
        {
            return FrozenSet<DockBackgroundPinMatch>.Empty;
        }

        var result = new HashSet<DockBackgroundPinMatch>();
        foreach (var (path, appId) in runningProcesses)
        {
            if (DockQuitRules.IsProtectedExecutable(path) || DockQuitRules.IsProtectedAumid(appId))
            {
                continue;
            }

            var hasAppId = !string.IsNullOrWhiteSpace(appId);
            if (hasAppId)
            {
                var appKey = $"aumid:{appId!.Trim()}";
                if (_pinsByKey.TryGetValue(appKey, out var pin))
                {
                    result.Add(new DockBackgroundPinMatch(DockPinRules.IdentityKey(pin), pin.ExecutablePath, pin.Arguments, null)
                    {
                        IdentityExecutablePath = string.IsNullOrWhiteSpace(path)
                            ? null
                            : WindowGrouping.NormalizeExecutablePath(path)
                    });
                    continue;
                }
            }
            if (string.IsNullOrWhiteSpace(path) || (hasAppId && DockQuitRules.IsBrowserHostExecutable(path)))
            {
                continue;
            }

            var executable = WindowGrouping.NormalizeExecutablePath(path);
            if (MatchExecutable(executable, allowAppIdFallback: !hasAppId) is { } key)
            {
                var pin = _pinsByKey[key];
                result.Add(new DockBackgroundPinMatch(key, pin.ExecutablePath, pin.Arguments, executable));
            }
        }
        return result.ToFrozenSet();
    }

    internal static IReadOnlySet<string> VerifyMatches(
        IReadOnlyList<DockItem> items, IReadOnlyList<WindowGroup> allGroups, IReadOnlySet<DockBackgroundPinMatch> matches)
    {
        if (matches.Count == 0)
        {
            return FrozenSet<string>.Empty;
        }

        var currentPins = new Dictionary<string, DockPinnedApp>(IdentityComparer);
        var groupOwners = new Dictionary<string, string>(IdentityComparer);
        foreach (var item in items)
        {
            if (item.Pinned is { } pin)
            {
                var owner = DockPinRules.IdentityKey(pin);
                currentPins.TryAdd(owner, pin);
                if (item.Group is { } group)
                {
                    groupOwners.TryAdd(group.Key, owner);
                }
            }
        }

        var windowOwners = new Dictionary<string, string?>(IdentityComparer);
        foreach (var group in allGroups)
        {
            var owner = groupOwners.GetValueOrDefault(group.Key, group.Key);
            foreach (var window in group.Windows)
            {
                if (string.IsNullOrWhiteSpace(window.ExecutablePath))
                {
                    continue;
                }

                var path = WindowGrouping.NormalizeExecutablePath(window.ExecutablePath);
                if (!windowOwners.TryAdd(path, owner) && !IdentityComparer.Equals(windowOwners[path], owner))
                {
                    windowOwners[path] = null;
                }
            }
        }

        var result = new HashSet<string>(IdentityComparer);
        foreach (var match in matches)
        {
            if (!currentPins.TryGetValue(match.PinKey, out var pin)
                || match.PinnedExecutable != pin.ExecutablePath || match.PinnedArguments != pin.Arguments)
            {
                continue;
            }
            if (match.ProcessExecutable is not { } path
                || !windowOwners.TryGetValue(path, out var owner) || IdentityComparer.Equals(owner, match.PinKey))
            {
                result.Add(match.PinKey);
            }
        }
        return result;
    }

    private string? MatchExecutable(string executable, bool allowAppIdFallback)
    {
        if (_exactPaths.Contains(executable))
        {
            var exact = _exactPaths[executable];
            foreach (var pin in exact)
            {
                if (!pin.HasAppId)
                {
                    return pin.Key;
                }
            }
            return allowAppIdFallback ? UniqueKey(exact) : null;
        }

        // Exact paths take precedence. Inferred targets only consult the pinned launchers
        // in this executable's directory and its ancestors, never the full list of pins.
        for (var directory = Path.GetDirectoryName(executable); !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
        {
            if (!_launcherDirectories.Contains(directory))
            {
                continue;
            }

            string? key = null;
            foreach (var pin in _launcherDirectories[directory])
            {
                if ((pin.HasAppId && !allowAppIdFallback) || !(pin.SquirrelTarget is { } target
                    ? DockQuitRules.MatchesSquirrelTarget(directory, target, executable)
                    : DockQuitRules.IsSubfolderStubPair(pin.Path, executable)))
                {
                    continue;
                }
                if (key is not null && !IdentityComparer.Equals(key, pin.Key))
                {
                    return null;
                }
                key = pin.Key;
            }
            if (key is not null)
            {
                return key;
            }
        }
        return null;
    }

    private static string? UniqueKey(IEnumerable<ExecutablePin> matches)
    {
        string? key = null;
        foreach (var pin in matches)
        {
            if (key is not null && !IdentityComparer.Equals(key, pin.Key))
            {
                return null;
            }
            key = pin.Key;
        }
        return key;
    }

    private readonly record struct ExecutablePin(string Key, string Path, bool HasAppId, string? SquirrelTarget);
}
