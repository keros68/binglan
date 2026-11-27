using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace BingLan.App.Taskbar;

internal enum TaskbarXamlRestoreResult
{
    Restored,
    Missing,
    Unconfirmed,
    Failed
}

/// <summary>A short lease on the taskbar background, restored by Explorer if this host exits.</summary>
internal sealed class TaskbarXamlSession : IDisposable
{
    internal const string MappingPrefix = @"Local\BingLan.Taskbar.Xaml.";
    private const string ComponentFileName = "BingLan.Taskbar.Xaml.dll";
    private const int Protocol = 0x424c0001;
    private const int RequestedOffset = 8;
    private const int ActiveTargetsOffset = 12;
    private const int StoppedOffset = 16;
    private const int ErrorOffset = 20;
    private const int HeartbeatOffset = 24;
    // The Explorer worker can spend up to 3.5 seconds draining its XAML targets.
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromSeconds(4);
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private readonly System.Threading.Timer _heartbeat;
    private readonly object _sync = new();
    private readonly string _cacheDirectory;
    private bool _disposed;
    private bool _stopping;
    private bool _nativeConnectionStarted;
    private Task<int>? _connection;
    private volatile uint _explorerProcessId;

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int ConnectDelegate([MarshalAs(UnmanagedType.LPWStr)] string mappingName);

    internal TaskbarXamlSession(string dataDirectory, uint explorerProcessId = 0)
    {
        MappingName = MappingPrefix + Guid.NewGuid().ToString("N");
        _explorerProcessId = explorerProcessId;
        _cacheDirectory = Path.Combine(dataDirectory, "Native");
        _mapping = MemoryMappedFile.CreateNew(MappingName, 32);
        _view = _mapping.CreateViewAccessor(0, 32);
        _view.Write(0, Protocol);
        _view.Write(4, Environment.ProcessId);
        _view.Write(HeartbeatOffset, ReadUnbiasedNowMilliseconds());
        _view.Write(RequestedOffset, 1);
        _heartbeat = new System.Threading.Timer(_ =>
        {
            lock (_sync)
            {
                if (!_disposed && !_stopping) _view.Write(HeartbeatOffset, ReadUnbiasedNowMilliseconds());
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    internal static long ReadUnbiasedNowMilliseconds() =>
        QueryUnbiasedInterruptTime(out var unbiasedTime)
            ? (long)(unbiasedTime / 10_000UL)
            : Environment.TickCount64;

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    internal string MappingName { get; }
    internal uint ExplorerProcessId => _explorerProcessId;

    internal bool IsActive
    {
        get
        {
            lock (_sync)
            {
                return !_disposed && !_stopping && _view.ReadInt32(RequestedOffset) == 1
                    && _view.ReadInt32(ActiveTargetsOffset) > 0 && _view.ReadInt32(ErrorOffset) == 0;
            }
        }
    }

    internal async Task StartAsync(
        int expectedTaskbars, CancellationToken cancellationToken, Func<int>? connectForTests = null)
    {
        Task<int> connection;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            connection = _connection = Task.Run(() => connectForTests is null
                ? Connect(cancellationToken)
                : InvokeConnection(connectForTests, cancellationToken), cancellationToken);
        }
        try
        {
            var result = await connection.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Marshal.ThrowExceptionForHR(result);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(5))
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    Marshal.ThrowExceptionForHR(_view.ReadInt32(ErrorOffset));
                    if (_view.ReadInt32(ActiveTargetsOffset) >= Math.Max(1, expectedTaskbars)) return;
                }
                await Task.Delay(50, cancellationToken);
            }
            throw new InvalidOperationException("没有识别到所有显示器的任务栏背景");
        }
        catch
        {
            RequestStop();
            // Cancelling the wait does not cancel an in-progress native call.
            _ = connection.ContinueWith(static task => { _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    private int Connect(CancellationToken cancellationToken)
    {
        // Explorer may keep the module mapped until it exits. A content-addressed copy
        // permits app upgrades without overwriting a loaded DLL or restarting Explorer.
        var source = Path.Combine(AppContext.BaseDirectory, ComponentFileName);
        var bytes = File.ReadAllBytes(source);
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        var directory = Path.Combine(_cacheDirectory, digest);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ComponentFileName);
        if (!File.Exists(path))
        {
            var temporaryPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temporaryPath, bytes);
                try { File.Move(temporaryPath, path); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        }
        if (!SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
            throw new InvalidDataException("任务栏组件校验失败");
        cancellationToken.ThrowIfCancellationRequested();
        var library = NativeLibrary.Load(path);
        try
        {
            var connect = Marshal.GetDelegateForFunctionPointer<ConnectDelegate>(
                NativeLibrary.GetExport(library, "BingLanConnect"));
            return InvokeConnection(() => connect(MappingName), cancellationToken);
        }
        finally { NativeLibrary.Free(library); }
    }

    private int InvokeConnection(Func<int> connect, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopping) throw new OperationCanceledException(cancellationToken);
            if (_explorerProcessId != 0 && TaskbarAdapter.ReadTaskbarIdentity().ProcessId != _explorerProcessId)
                throw new InvalidOperationException("资源管理器正在重建任务栏");
            _nativeConnectionStarted = true;
        }
        try { return connect(); }
        finally
        {
            if (_explorerProcessId != 0)
            {
                var current = TaskbarAdapter.ReadTaskbarIdentity().ProcessId;
                if (current != 0) _explorerProcessId = current;
            }
        }
    }

    internal void RequestStop()
    {
        lock (_sync)
        {
            if (_disposed || _stopping) return;
            _stopping = true;
            _view.Write(RequestedOffset, 0);
            _heartbeat.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    internal bool HasPendingConnection
    {
        get
        {
            lock (_sync)
            {
                return _nativeConnectionStarted && _connection?.IsCompleted == false;
            }
        }
    }

    internal TaskbarXamlRestoreResult Restore(Func<uint, bool>? isExplorerRunning = null)
    {
        RequestStop();
        var watch = Stopwatch.StartNew();
        var checkedExplorer = false;
        while (watch.Elapsed < RestoreTimeout)
        {
            lock (_sync)
            {
                if (!_nativeConnectionStarted) return TaskbarXamlRestoreResult.Restored;
                if (_disposed) return TaskbarXamlRestoreResult.Unconfirmed;
                if (_connection?.IsCompleted == true)
                {
                    if (!checkedExplorer)
                    {
                        checkedExplorer = true;
                        if (ExplorerProcessId != 0 && isExplorerRunning?.Invoke(ExplorerProcessId) == false)
                            return TaskbarXamlRestoreResult.Restored;
                    }
                    if (ReadRestoreResult(_view) is { } restored) return restored;
                }
            }
            Thread.Sleep(50);
        }
        lock (_sync)
        {
            return !_disposed && ReadRestoreResult(_view) == TaskbarXamlRestoreResult.Failed
                ? TaskbarXamlRestoreResult.Failed
                : TaskbarXamlRestoreResult.Unconfirmed;
        }
    }

    private static TaskbarXamlRestoreResult? ReadRestoreResult(MemoryMappedViewAccessor view) => view.ReadInt32(StoppedOffset) switch
    {
        1 when view.ReadInt32(ActiveTargetsOffset) == 0 => TaskbarXamlRestoreResult.Restored,
        2 => TaskbarXamlRestoreResult.Failed,
        _ => null
    };

    internal static bool IsValidMappingName(string name) => name.StartsWith(MappingPrefix, StringComparison.Ordinal)
        && Guid.TryParseExact(name[MappingPrefix.Length..], "N", out _);

    internal static TaskbarXamlRestoreResult Restore(string mappingName)
    {
        if (!IsValidMappingName(mappingName)) return TaskbarXamlRestoreResult.Unconfirmed;
        try
        {
            using var mapping = MemoryMappedFile.OpenExisting(mappingName);
            using var view = mapping.CreateViewAccessor(0, 32);
            if (view.ReadInt32(0) != Protocol) return TaskbarXamlRestoreResult.Unconfirmed;
            view.Write(RequestedOffset, 0);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < RestoreTimeout)
            {
                if (ReadRestoreResult(view) is { } restored) return restored;
                Thread.Sleep(50);
            }
            return TaskbarXamlRestoreResult.Unconfirmed;
        }
        catch (FileNotFoundException)
        {
            // Explorer must hold this mapping for the lifetime of a lease. A late
            // SetSite also needs to open this exact name before it can start one.
            return TaskbarXamlRestoreResult.Missing;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return TaskbarXamlRestoreResult.Unconfirmed;
        }
    }

    public void Dispose()
    {
        RequestStop();
        _heartbeat.Dispose();
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _view.Dispose();
            _mapping.Dispose();
        }
    }
}
