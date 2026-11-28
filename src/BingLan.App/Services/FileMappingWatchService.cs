using System.ComponentModel;
using System.IO;
using System.Windows.Threading;

namespace BingLan.App.Services;

/// <summary>
/// Watches the folders that hold file-box mappings, plus the desktop folders. Deletes and
/// renames request a debounced sweep so mappings whose originals were deleted stop lingering
/// as “已失效”; creations and in-place renames on the desktops request a debounced import so
/// new desktop items join their boxes on their own. One service covers every box; each
/// folder gets a single watcher. Callbacks start on the UI thread and must do filesystem
/// work off it; they return tasks that are discarded, so failures must be caught inside.
/// </summary>
public sealed class FileMappingWatchService : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(1);

    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingImports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _visibilityCandidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _sweepDebounce;
    private readonly DispatcherTimer _importDebounce;
    private readonly Func<Task> _sweep;
    private readonly Func<IReadOnlyList<string>, Task<IReadOnlySet<string>>> _import;
    private HashSet<string> _mappingDirectories = new(StringComparer.OrdinalIgnoreCase);
    // Watcher threads read the routing table. Directory snapshots are updated on the UI thread.
    private volatile Dictionary<string, DesktopSnapshot> _importDirectories = new(StringComparer.OrdinalIgnoreCase);
    private bool _isImporting;
    private bool _disposed;
    private long _candidateRevision;

    private sealed class DesktopSnapshot
    {
        public HashSet<string>? Entries { get; set; }
        public Dictionary<string, bool>? ChangesDuringScan { get; set; }
        public bool ScanAgain { get; set; }
    }

    public FileMappingWatchService(Func<Task> sweep, Func<IReadOnlyList<string>, Task<IReadOnlySet<string>>> import)
    {
        _sweep = sweep;
        _import = import;
        _sweepDebounce = new DispatcherTimer { Interval = DebounceDelay };
        _sweepDebounce.Tick += (_, _) =>
        {
            _sweepDebounce.Stop();
            _ = _sweep();
        };
        _importDebounce = new DispatcherTimer { Interval = DebounceDelay };
        _importDebounce.Tick += (_, _) =>
        {
            _importDebounce.Stop();
            if (!_isImporting && _pendingImports.Count > 0)
            {
                _ = ImportPendingPathsAsync();
            }
        };
    }

    /// <summary>
    /// Keeps exactly the given folders under watch. The caller filters to folders that
    /// still exist, off the UI thread.
    /// </summary>
    public void UpdateWatchedDirectories(IEnumerable<string> directories)
    {
        _mappingDirectories = new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase);
        UpdateWatchers();
    }

    /// <summary>Keeps desktop folders under watch even when they have no mappings.</summary>
    public void UpdateImportDirectories(IEnumerable<string> directories)
    {
        var previous = _importDirectories;
        var updated = new Dictionary<string, DesktopSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            updated.TryAdd(directory, previous.GetValueOrDefault(directory) ?? new DesktopSnapshot());
        }

        _importDirectories = updated;
        foreach (var path in _visibilityCandidates.Keys.Where(path => !updated.ContainsKey(Path.GetDirectoryName(path) ?? string.Empty)).ToArray())
        {
            ForgetCandidate(path);
        }
        if (_pendingImports.Count == 0)
        {
            _importDebounce.Stop();
        }

        UpdateWatchers();
    }

    private void UpdateWatchers()
    {
        if (_disposed)
        {
            return;
        }

        var wanted = new HashSet<string>(_mappingDirectories, StringComparer.OrdinalIgnoreCase);
        wanted.UnionWith(_importDirectories.Keys);
        foreach (var stale in _watchers.Keys.Where(directory => !wanted.Contains(directory)).ToList())
        {
            _watchers[stale].Dispose();
            _watchers.Remove(stale);
        }
        foreach (var directory in wanted)
        {
            if (!_watchers.ContainsKey(directory) && CreateWatcher(directory) is { } created)
            {
                _watchers[directory] = created;
            }
        }
        foreach (var (directory, snapshot) in _importDirectories)
        {
            if (snapshot.Entries is null && snapshot.ChangesDuringScan is null && _watchers.ContainsKey(directory))
            {
                _ = ScanDesktopAsync(directory, snapshot);
            }
        }
    }

    /// <summary>Schedules a debounced sweep; safe to call from any thread.</summary>
    public void RequestSweep() => Restart(_sweepDebounce);

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

    private FileSystemWatcher? CreateWatcher(string directory)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes,
                IncludeSubdirectories = false
            };
            watcher.Deleted += (sender, e) =>
            {
                RequestSweep();
                RequestDesktopChange(directory, sender, removed: e.FullPath, added: null);
            };
            watcher.Renamed += (sender, e) =>
            {
                RequestSweep();
                // An in-place rename lands the new name in this folder; a finished download
                // (.crdownload → final name) arrives exactly this way.
                RequestDesktopChange(directory, sender, removed: e.OldFullPath, added: e.FullPath);
            };
            watcher.Created += (sender, e) => RequestDesktopChange(directory, sender, removed: null, added: e.FullPath);
            watcher.Changed += (sender, e) => RequestVisibilityCheck(directory, sender, e.FullPath);
            watcher.Error += (sender, e) =>
            {
                RequestSweep();
                if (e.GetException() is InternalBufferOverflowException)
                {
                    RequestDesktopRecovery(directory, sender);
                }
            };
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or Win32Exception)
        {
            watcher?.Dispose();
            return null;
        }
    }

    private void RequestDesktopChange(string directory, object sender, string? removed, string? added)
    {
        if (!_importDirectories.TryGetValue(directory, out var snapshot))
        {
            return;
        }

        _importDebounce.Dispatcher.BeginInvoke(() =>
        {
            if (!IsCurrentDesktopWatcher(directory, snapshot, sender))
            {
                return;
            }

            if (removed is not null)
            {
                ForgetCandidate(removed);
                snapshot.Entries?.Remove(removed);
                if (snapshot.ChangesDuringScan is { } changes)
                {
                    changes[removed] = false;
                }
            }
            if (added is not null)
            {
                snapshot.Entries?.Add(added);
                if (snapshot.ChangesDuringScan is { } changes)
                {
                    changes[added] = true;
                }
                TrackCandidate(added);
                RestartImportDebounce();
            }
        });
    }

    private void RequestVisibilityCheck(string directory, object sender, string path)
    {
        if (!_importDirectories.TryGetValue(directory, out var snapshot))
        {
            return;
        }

        _importDebounce.Dispatcher.BeginInvoke(() =>
        {
            if (IsCurrentDesktopWatcher(directory, snapshot, sender) && _visibilityCandidates.ContainsKey(path))
            {
                _pendingImports.Add(path);
                RestartImportDebounce();
            }
        });
    }

    private void TrackCandidate(string path)
    {
        _visibilityCandidates[path] = ++_candidateRevision;
        _pendingImports.Add(path);
    }

    private void ForgetCandidate(string path)
    {
        _visibilityCandidates.Remove(path);
        _pendingImports.Remove(path);
    }

    private void RestartImportDebounce()
    {
        _importDebounce.Stop();
        _importDebounce.Start();
    }

    private void RequestDesktopRecovery(string directory, object sender)
    {
        if (!_importDirectories.TryGetValue(directory, out var snapshot))
        {
            return;
        }

        _importDebounce.Dispatcher.BeginInvoke(() =>
        {
            if (!IsCurrentDesktopWatcher(directory, snapshot, sender))
            {
                return;
            }

            if (snapshot.ChangesDuringScan is null)
            {
                _ = ScanDesktopAsync(directory, snapshot);
            }
            else
            {
                snapshot.ScanAgain = true;
            }
        });
    }

    private bool IsCurrentDesktopSnapshot(string directory, DesktopSnapshot snapshot) =>
        !_disposed && ReferenceEquals(_importDirectories.GetValueOrDefault(directory), snapshot);

    private bool IsCurrentDesktopWatcher(string directory, DesktopSnapshot snapshot, object sender) =>
        IsCurrentDesktopSnapshot(directory, snapshot) && ReferenceEquals(_watchers.GetValueOrDefault(directory), sender);

    private async Task ScanDesktopAsync(string directory, DesktopSnapshot snapshot)
    {
        snapshot.ScanAgain = false;
        var changes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        snapshot.ChangesDuringScan = changes;
        var current = await Task.Run(() => ReadDesktopSnapshot(directory)).ConfigureAwait(false);
        await _importDebounce.Dispatcher.InvokeAsync(() => CompleteDesktopScan(directory, snapshot, changes, current));
    }

    private void CompleteDesktopScan(string directory, DesktopSnapshot snapshot, Dictionary<string, bool> changes,
        HashSet<string>? current)
    {
        snapshot.ChangesDuringScan = null;
        if (!IsCurrentDesktopSnapshot(directory, snapshot))
        {
            return;
        }

        if (current is not null)
        {
            foreach (var (path, exists) in changes)
            {
                if (exists)
                {
                    current.Add(path);
                }
                else
                {
                    current.Remove(path);
                }
            }

            // The first successful scan is a baseline. Recovery only enqueues paths
            // absent from the preceding snapshot or normal directory notifications.
            if (snapshot.Entries is { } before)
            {
                foreach (var path in current.Where(path => !before.Contains(path)))
                {
                    TrackCandidate(path);
                }
                foreach (var path in before.Where(path => !current.Contains(path)))
                {
                    ForgetCandidate(path);
                }
                if (_pendingImports.Count > 0)
                {
                    _importDebounce.Start();
                }
            }
            snapshot.Entries = current;
        }
        if (snapshot.ScanAgain)
        {
            _ = ScanDesktopAsync(directory, snapshot);
        }
    }

    private static HashSet<string>? ReadDesktopSnapshot(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private async Task ImportPendingPathsAsync()
    {
        var paths = _pendingImports.ToArray();
        var revisions = paths.Select(path => _visibilityCandidates[path]).ToArray();
        _pendingImports.Clear();
        _isImporting = true;
        try
        {
            var hidden = await _import(paths);
            for (var i = 0; i < paths.Length; i++)
            {
                var path = paths[i];
                if (!hidden.Contains(path) && _visibilityCandidates.GetValueOrDefault(path) == revisions[i])
                {
                    ForgetCandidate(path);
                }
            }
        }
        finally
        {
            _isImporting = false;
            if (!_disposed && _pendingImports.Count > 0)
            {
                _importDebounce.Start();
            }
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
        _pendingImports.Clear();
        _visibilityCandidates.Clear();
        foreach (var watcher in _watchers.Values)
        {
            watcher.Dispose();
        }
        _watchers.Clear();
    }
}
