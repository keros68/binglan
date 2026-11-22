using System.Windows.Media;

namespace BingLan.App.Services;

/// <summary>
/// Process-wide cache of frozen launcher icons. Coalesces concurrent requests for the same
/// icon key so startup prewarming and an open Overview Card share in-flight extractions
/// without duplicate Shell work or skipped icons.
/// </summary>
internal static class LauncherIconCache
{
    private const int MaxEntries = 1000;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ImageSource> Icons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> InsertionOrder = new();
    private static readonly Dictionary<string, Task<ImageSource?>> Inflight = new(StringComparer.OrdinalIgnoreCase);

    internal static bool TryGet(string key, out ImageSource? icon)
    {
        lock (Gate)
        {
            if (Icons.TryGetValue(key, out var found))
            {
                icon = found;
                return true;
            }
            icon = null;
            return false;
        }
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
                return Task.FromResult<ImageSource?>(found);
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
            Complete(key, resolved);
            tcs.TrySetResult(resolved);
        }
    }

    internal static void Complete(string key, ImageSource? icon)
    {
        lock (Gate)
        {
            Inflight.Remove(key);
            if (icon is null)
            {
                return;
            }
            if (!Icons.ContainsKey(key))
            {
                while (Icons.Count >= MaxEntries && InsertionOrder.Count > 0)
                {
                    var oldest = InsertionOrder.Dequeue();
                    Icons.Remove(oldest);
                }
                InsertionOrder.Enqueue(key);
            }
            Icons[key] = icon;
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            Icons.Clear();
            InsertionOrder.Clear();
        }
    }
}
