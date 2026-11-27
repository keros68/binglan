using BingLan.Core.Dock;

namespace BingLan.App.Dock;

/// <summary>Shares native window enumeration and icon extraction across dock surfaces.</summary>
internal sealed class DockRuntime
{
    private readonly WindowCatalog _catalog = new();
    private readonly object _gate = new();
    private Task<IReadOnlyList<TrackedWindow>>? _snapshot;
    private long _snapshotTick;
    private readonly Dictionary<string, Task<DockAppVisual>> _visuals = new(StringComparer.OrdinalIgnoreCase);
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

    internal Task<DockAppVisual> ResolveVisualAsync(DockItem item)
    {
        lock (_gate)
        {
            if (_visuals.TryGetValue(item.Key, out var existing) && !existing.IsFaulted && !existing.IsCanceled)
            {
                return existing;
            }
            return _visuals[item.Key] = ExtractVisualAsync(item);
        }
    }

    private async Task<DockAppVisual> ExtractVisualAsync(DockItem item)
    {
        await _visualExtraction.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => DockAppResolver.Resolve(item)).ConfigureAwait(false);
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

    internal void InvalidateVisuals()
    {
        lock (_gate) _visuals.Clear();
    }
}
