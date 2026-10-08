using System.IO;
using System.Windows.Threading;

namespace BingLan.App.Services;

/// <summary>
/// Watches the folders that hold file-box mappings, plus the desktop folders. Deletes and
/// renames request a debounced sweep so mappings whose originals were deleted stop lingering
/// as “已失效”; creations and in-place renames on the desktops request a debounced import so
/// new desktop items join their boxes on their own. One service covers every box; each
/// folder gets a single watcher. Callbacks run on the UI thread and must do filesystem work
/// off it.
/// </summary>
public sealed class FileMappingWatchService : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(1);

    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _sweepDebounce;
    private readonly DispatcherTimer _importDebounce;
    private readonly Action _sweep;
    private readonly Action _import;
    // Replaced atomically so watcher threads read a stable snapshot.
    private volatile HashSet<string> _importDirectories = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public FileMappingWatchService(Action sweep, Action import)
    {
        _sweep = sweep;
        _import = import;
        _sweepDebounce = new DispatcherTimer { Interval = DebounceDelay };
        _sweepDebounce.Tick += (_, _) =>
        {
            _sweepDebounce.Stop();
            _sweep();
        };
        _importDebounce = new DispatcherTimer { Interval = DebounceDelay };
        _importDebounce.Tick += (_, _) =>
        {
            _importDebounce.Stop();
            _import();
        };
    }

    /// <summary>
    /// Keeps exactly the given folders under watch. The caller filters to folders that
    /// still exist, off the UI thread.
    /// </summary>
    public void UpdateWatchedDirectories(IEnumerable<string> directories)
    {
        if (_disposed)
        {
            return;
        }

        var wanted = new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _watchers.Keys.Where(directory => !wanted.Contains(directory)).ToList())
        {
            _watchers[stale].Dispose();
            _watchers.Remove(stale);
        }
        foreach (var directory in wanted)
        {
            if (!_watchers.ContainsKey(directory))
            {
                _watchers[directory] = CreateWatcher(directory);
            }
        }
    }

    /// <summary>The folders whose creations also request an import (the user and public desktops).</summary>
    public void UpdateImportDirectories(IEnumerable<string> directories) =>
        _importDirectories = new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase);

    /// <summary>Schedules a debounced sweep; safe to call from any thread.</summary>
    public void RequestSweep() => Restart(_sweepDebounce);

    /// <summary>Schedules a debounced import; safe to call from any thread.</summary>
    public void RequestImport() => Restart(_importDebounce);

    private void Restart(DispatcherTimer timer) =>
        timer.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }
            timer.Stop();
            timer.Start();
        });

    private FileSystemWatcher CreateWatcher(string directory)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };
        watcher.Deleted += (_, _) => RequestSweep();
        watcher.Renamed += (_, _) =>
        {
            RequestSweep();
            // An in-place rename lands the new name in this folder; a finished download
            // (.crdownload → final name) arrives exactly this way.
            RequestImportIfDesktop(directory);
        };
        watcher.Created += (_, _) => RequestImportIfDesktop(directory);
        // Deleting a watched folder itself, or overflowing the event buffer, surfaces here;
        // the sweep catches every gone mapping no matter which event was missed.
        watcher.Error += (_, _) => RequestSweep();
        return watcher;
    }

    private void RequestImportIfDesktop(string directory)
    {
        if (_importDirectories.Contains(directory))
        {
            RequestImport();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _sweepDebounce.Stop();
        _importDebounce.Stop();
        foreach (var watcher in _watchers.Values)
        {
            watcher.Dispose();
        }
        _watchers.Clear();
    }
}
