using System.Windows.Media;

namespace BingLan.App.Services;

/// <summary>
/// Process-wide bounded LRU cache of frozen launcher and file-box icons. Coalesces
/// concurrent requests for the same icon key without duplicate Shell work, enforces an
/// upper cap of <see cref="MaxEntries"/> items, and trims idle/expired entries after
/// <see cref="IdleTrimDelay"/> so unused bitmaps do not remain pinned indefinitely.
/// </summary>
internal static class LauncherIconCache
{
    internal const int MaxEntries = 256;
    internal static readonly TimeSpan IdleTrimDelay = TimeSpan.FromMinutes(5);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, CacheEntry> Icons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> AccessOrder = new();
    private static readonly Dictionary<string, Task<ImageSource?>> Inflight = new(StringComparer.OrdinalIgnoreCase);
    private static System.Threading.Timer? _idleTrimTimer;
    private static bool _idleTrimArmed;

    private sealed class CacheEntry(ImageSource icon, LinkedListNode<string> node, long lastAccessedTick)
    {
        public ImageSource Icon { get; set; } = icon;
        public LinkedListNode<string> Node { get; } = node;
        public long LastAccessedTick { get; set; } = lastAccessedTick;
    }

    internal static int Count
    {
        get
        {
            lock (Gate)
            {
                return Icons.Count;
            }
        }
    }

    /// <summary>
    /// Cache key namespace for FileBox tiles (128 px <see cref="Interop.ShellIconProvider"/> icons),
    /// kept distinct from <c>LauncherPanelWindow</c> keys that may resolve pinned entries via
    /// 48 px <c>DockAppResolver</c> so low-resolution icons never shadow FileBox tiles.
    /// Shared system image-list icons still deduplicate across both via
    /// <see cref="Interop.ShellIconProvider"/>'s <c>sysIconIndex</c> weak cache.
    /// </summary>
    internal static string KeyForFileBoxPath(string path) => "filebox\0" + path;

    internal static bool TryGet(string key, out ImageSource? icon)
    {
        lock (Gate)
        {
            if (Icons.TryGetValue(key, out var found))
            {
                TouchLocked(found, Environment.TickCount64);
                icon = found.Icon;
                return true;
            }
            icon = null;
            return false;
        }
    }

    /// <summary>
    /// Synchronous lookup/load helper for background worker loops (e.g. FileBox probing).
    /// Never disturbs concurrent async <see cref="GetOrLoadAsync"/> in-flight entries.
    /// </summary>
    internal static ImageSource? GetOrLoadSync(string key, Func<ImageSource?> loader)
    {
        if (TryGet(key, out var cached))
        {
            return cached;
        }

        var loaded = loader();
        if (loaded is System.Windows.Freezable { IsFrozen: false, CanFreeze: true } freezable)
        {
            freezable.Freeze();
        }
        return StoreIcon(key, loaded);
    }

    /// <summary>
    /// Returns the cached icon immediately if present, joins an existing in-flight extraction
    /// for the same key, or starts a new extraction via <paramref name="loader"/>.
    /// </summary>
    internal static Task<ImageSource?> GetOrLoadAsync(string key, Func<Task<ImageSource?>> loader)
    {
        TaskCompletionSource<ImageSource?> created;
        lock (Gate)
        {
            if (Icons.TryGetValue(key, out var found))
            {
                TouchLocked(found, Environment.TickCount64);
                return Task.FromResult<ImageSource?>(found.Icon);
            }
            if (Inflight.TryGetValue(key, out var existing))
            {
                return existing;
            }
            created = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Inflight[key] = created.Task;
        }

        _ = RunLoaderAsync(key, loader, created);
        return created.Task;
    }

    private static async Task RunLoaderAsync(
        string key,
        Func<Task<ImageSource?>> loader,
        TaskCompletionSource<ImageSource?> tcs)
    {
        ImageSource? resolved = null;
        try
        {
            resolved = await loader().ConfigureAwait(false);
        }
        catch
        {
            resolved = null;
        }
        finally
        {
            var canonical = CompleteInflight(key, tcs.Task, resolved);
            tcs.TrySetResult(canonical);
        }
    }

    private static ImageSource? StoreIcon(string key, ImageSource? icon)
    {
        if (icon is null)
        {
            return null;
        }

        lock (Gate)
        {
            return StoreIconLocked(key, icon);
        }
    }

    private static ImageSource? CompleteInflight(string key, Task<ImageSource?> expectedTask, ImageSource? icon)
    {
        lock (Gate)
        {
            if (Inflight.TryGetValue(key, out var current) && ReferenceEquals(current, expectedTask))
            {
                Inflight.Remove(key);
            }
            return icon is null ? null : StoreIconLocked(key, icon);
        }
    }

    private static ImageSource StoreIconLocked(string key, ImageSource icon)
    {
        var now = Environment.TickCount64;
        if (Icons.TryGetValue(key, out var existing))
        {
            TouchLocked(existing, now);
            return existing.Icon;
        }

        while (Icons.Count >= MaxEntries && AccessOrder.First is { } oldest)
        {
            AccessOrder.RemoveFirst();
            Icons.Remove(oldest.Value);
        }

        var node = AccessOrder.AddLast(key);
        Icons[key] = new CacheEntry(icon, node, now);
        if (!_idleTrimArmed)
        {
            ScheduleIdleTrimLocked(IdleTrimDelay);
        }
        return icon;
    }

    internal static void Remove(string key)
    {
        lock (Gate)
        {
            if (Icons.Remove(key, out var existing))
            {
                AccessOrder.Remove(existing.Node);
            }
            if (Icons.Count == 0)
            {
                StopIdleTrimLocked();
            }
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            Icons.Clear();
            AccessOrder.Clear();
            StopIdleTrimLocked();
        }
    }

    /// <summary>
    /// Removes entries that have remained untouched for at least <paramref name="maxAge"/>
    /// (defaulting to <see cref="IdleTrimDelay"/>) and enforces <see cref="MaxEntries"/>.
    /// </summary>
    internal static int TrimExpiredEntries(long? nowTick = null, TimeSpan? maxAge = null)
    {
        lock (Gate)
        {
            if (Icons.Count == 0)
            {
                StopIdleTrimLocked();
                return 0;
            }

            var now = nowTick ?? Environment.TickCount64;
            var expiryMs = Math.Max(0L, (long)(maxAge ?? IdleTrimDelay).TotalMilliseconds);
            var removed = 0;

            while (AccessOrder.First is { } oldest)
            {
                if (!Icons.TryGetValue(oldest.Value, out var entry))
                {
                    AccessOrder.RemoveFirst();
                    continue;
                }

                var ageMs = unchecked(now - entry.LastAccessedTick);
                if (ageMs >= expiryMs || Icons.Count > MaxEntries)
                {
                    AccessOrder.RemoveFirst();
                    Icons.Remove(oldest.Value);
                    removed++;
                }
                else
                {
                    break;
                }
            }

            if (AccessOrder.First is { } nextOldest && Icons.TryGetValue(nextOldest.Value, out var nextEntry))
            {
                var remainingMs = Math.Max(1000L, expiryMs - Math.Max(0L, unchecked(now - nextEntry.LastAccessedTick)));
                ScheduleIdleTrimLocked(TimeSpan.FromMilliseconds(remainingMs));
            }
            else
            {
                StopIdleTrimLocked();
            }

            return removed;
        }
    }

    private static void TouchLocked(CacheEntry entry, long now)
    {
        entry.LastAccessedTick = now;
        if (!ReferenceEquals(AccessOrder.Last, entry.Node))
        {
            AccessOrder.Remove(entry.Node);
            AccessOrder.AddLast(entry.Node);
        }
    }

    private static void ScheduleIdleTrimLocked(TimeSpan delay)
    {
        var ms = Math.Clamp((long)delay.TotalMilliseconds, 1000L, (long)IdleTrimDelay.TotalMilliseconds);
        if (_idleTrimTimer is null)
        {
            _idleTrimTimer = new System.Threading.Timer(
                static _ => TrimExpiredEntries(),
                null,
                ms,
                Timeout.Infinite);
        }
        else
        {
            _idleTrimTimer.Change(ms, Timeout.Infinite);
        }
        _idleTrimArmed = true;
    }

    private static void StopIdleTrimLocked()
    {
        _idleTrimTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _idleTrimArmed = false;
    }
}
