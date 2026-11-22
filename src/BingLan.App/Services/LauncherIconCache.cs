using System.Windows.Media;

namespace BingLan.App.Services;

internal static class LauncherIconCache
{
    private const int MaxEntries = 600;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ImageSource> Icons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Inflight = new(StringComparer.OrdinalIgnoreCase);

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

    internal static bool TryBeginLoad(string key)
    {
        lock (Gate)
        {
            if (Icons.ContainsKey(key))
            {
                return false;
            }
            return Inflight.Add(key);
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
            if (Icons.Count >= MaxEntries && !Icons.ContainsKey(key))
            {
                Icons.Clear();
            }
            Icons[key] = icon;
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            Icons.Clear();
        }
    }
}
