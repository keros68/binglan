using BingLan.Core.Models;
using BingLan.Core.Dock;
using System.Collections.Frozen;

namespace BingLan.App.Dock;

internal readonly record struct DockVisualPinState(
    bool IsPinned, string? ExecutablePath, string? AppUserModelId, string? TargetPath, string? IconFile)
{
    internal static DockVisualPinState Of(DockItem item) => item.Pinned is { } pin
        ? new(true, pin.ExecutablePath, pin.AppUserModelId, pin.TargetPath, pin.IconFile)
        : default;

    internal DockPinnedApp? CreatePinSnapshot() => IsPinned
        ? new DockPinnedApp
        {
            ExecutablePath = ExecutablePath,
            AppUserModelId = AppUserModelId,
            TargetPath = TargetPath,
            IconFile = IconFile
        }
        : null;
}

/// <summary>Shares window and process snapshots and icon extraction across dock surfaces.</summary>
internal sealed class DockRuntime
{
    private readonly WindowCatalog _catalog = new();
    private readonly object _gate = new();
    private Task<IReadOnlyList<TrackedWindow>>? _snapshot;
    private long _snapshotTick;
    private readonly Func<IReadOnlyList<RunningAppProcess>> _captureProcesses;
    private readonly Func<long> _backgroundTick;
    private readonly Func<DockItem, DockAppVisual> _resolveVisual;
    private DockBackgroundPinMatcher? _backgroundMatcher;
    private DockBackgroundPinMatcher? _matchedBy;
    private IReadOnlyList<RunningAppProcess>? _backgroundProcesses;
    private IReadOnlySet<DockBackgroundPinMatch> _backgroundMatches = FrozenSet<DockBackgroundPinMatch>.Empty;
    private IReadOnlySet<string> _runningTrayExecutables = FrozenSet<string>.Empty;
    private long _nextBackgroundScan;
    private bool _backgroundScanRunning;

    internal DockRuntime(
        Func<IReadOnlyList<RunningAppProcess>>? captureProcesses = null,
        Func<long>? backgroundTick = null,
        Func<DockItem, DockAppVisual>? resolveVisual = null)
    {
        _captureProcesses = captureProcesses ?? WindowCatalog.CaptureRunningProcesses;
        _backgroundTick = backgroundTick ?? (() => Environment.TickCount64);
        _resolveVisual = resolveVisual ?? DockAppResolver.Resolve;
    }

    internal event Action? BackgroundPinsChanged;
    internal IReadOnlyCollection<string> RunningTrayExecutables => _runningTrayExecutables;

    private readonly Dictionary<string, (DockVisualPinState Source, Task<DockAppVisual> Task)> _visuals =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _visualExtraction = new(1, 1);

    internal DockToggleCooldown ToggleCooldown { get; } = new(() => Environment.TickCount64);
    internal DockLaunchCooldown LaunchCooldown { get; } = new(() => Environment.TickCount64);

    internal Task<IReadOnlyList<TrackedWindow>> CaptureAsync()
    {
        lock (_gate)
        {
            // Hooks and foreground timers on different surfaces arrive in one burst.
            // The 25 ms reuse window is shorter than the watcher's 90 ms debounce,
            // so a later window-change notification always gets a fresh capture.
            if (_snapshot is { IsCompleted: false }
                || (_snapshot is { IsCompletedSuccessfully: true } && Environment.TickCount64 - _snapshotTick < 25))
            {
                return _snapshot;
            }
            _snapshotTick = Environment.TickCount64;
            return _snapshot = Task.Run(_catalog.Capture);
        }
    }

    // All Dock windows share the same pins and dispatcher. Their window updates use the
    // latest completed match immediately; native process enumeration never holds up activation.
    internal IReadOnlySet<DockBackgroundPinMatch> GetBackgroundPins(IReadOnlyList<DockPinnedApp> pins)
    {
        if (_backgroundMatcher is null || !_backgroundMatcher.HasSamePins(pins))
        {
            _backgroundMatcher = new DockBackgroundPinMatcher(pins);
        }

        var scanDue = _backgroundTick() >= _nextBackgroundScan;
        if (!_backgroundScanRunning
            && (scanDue || (_backgroundProcesses is not null && !ReferenceEquals(_backgroundMatcher, _matchedBy))))
        {
            _backgroundScanRunning = true;
            _ = RefreshBackgroundPinsAsync(_backgroundMatcher, scanDue ? null : _backgroundProcesses);
        }
        return _backgroundMatches;
    }

    private async Task RefreshBackgroundPinsAsync(
        DockBackgroundPinMatcher matcher, IReadOnlyList<RunningAppProcess>? cachedProcesses)
    {
        var changed = false;
        try
        {
            var (processes, matches, trayPaths) = await Task.Run(() =>
            {
                var snapshot = cachedProcesses ?? _captureProcesses();
                // The unpinned running section uses the same native scan as pinned indicators.
                var trayExecutables = cachedProcesses is null
                    ? snapshot.Select(process => process.ExecutablePath).OfType<string>()
                        .Where(TrayAppRevealRules.PreventsRelaunchWhenRunning)
                        .ToFrozenSet(StringComparer.OrdinalIgnoreCase)
                    : null;
                return (snapshot, matcher.MatchProcesses(snapshot), trayExecutables);
            });
            _backgroundProcesses = processes;
            if (trayPaths is not null && !_runningTrayExecutables.SetEquals(trayPaths))
            {
                _runningTrayExecutables = trayPaths;
                changed = true;
            }
            if (ReferenceEquals(matcher, _backgroundMatcher) && !_backgroundMatches.SetEquals(matches))
            {
                _backgroundMatches = matches;
                changed = true;
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Dock 后台状态刷新失败：{exception}");
        }
        finally
        {
            _backgroundScanRunning = false;
            if (ReferenceEquals(matcher, _backgroundMatcher))
            {
                _matchedBy = matcher;
            }
            if (cachedProcesses is null)
            {
                _nextBackgroundScan = _backgroundTick() + 2_000;
            }
        }
        if (changed || !ReferenceEquals(matcher, _backgroundMatcher))
        {
            BackgroundPinsChanged?.Invoke();
        }
    }

    internal Task<DockAppVisual> ResolveVisualAsync(DockItem item)
    {
        var source = DockVisualPinState.Of(item);
        lock (_gate)
        {
            if (_visuals.TryGetValue(item.Key, out var existing) && existing.Source == source
                && !existing.Task.IsFaulted && !existing.Task.IsCanceled)
            {
                return existing.Task;
            }
            var task = ExtractVisualAsync(item with { Pinned = source.CreatePinSnapshot() });
            _visuals[item.Key] = (source, task);
            return task;
        }
    }

    private async Task<DockAppVisual> ExtractVisualAsync(DockItem item)
    {
        await _visualExtraction.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => _resolveVisual(item)).ConfigureAwait(false);
        }
        finally
        {
            _visualExtraction.Release();
        }
    }

    internal void RetainVisuals(IEnumerable<string> keys)
    {
        var active = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var stale in _visuals.Keys.Where(key => !active.Contains(key)).ToArray()) _visuals.Remove(stale);
        }
    }
}
