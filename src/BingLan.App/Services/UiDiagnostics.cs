using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Threading;
using BingLan.App.Interop;
using BingLan.Core.Services;

namespace BingLan.App.Services;

/// <summary>Paths that react to other programs or to the window system, counted for stall reports.</summary>
internal enum UiActivity
{
    WindowEvent,
    StateEvent,
    DockRefresh,
    ForegroundCheck,
    AppBarNotice,
    AppBarSetPosition,
    GlassSync,
    GlassMove,
    TaskbarEvent,
    DesktopEvent,
    AppBarNotifyExplorer
}

/// <summary>
/// Local record of UI thread stalls in <c>stall.log</c>. A watchdog thread probes the
/// UI thread; when a probe waits more than two seconds it writes what the thread was
/// running, the process CPU use, the foreground program (process and window class, no
/// titles) and how often the counted paths ran. Bursts of those paths are written even
/// without a stall. Nothing leaves this machine.
/// </summary>
internal static class UiDiagnostics
{
    private const long StallThresholdMs = 2000;
    private const int ProbeIntervalMs = 250;
    private const long WindowMs = 10_000;
    private const long FloodPerWindow = 3000;
    private const long FloodLogIntervalMs = 60_000;
    private const long MaximumBytes = 256 * 1024;
    private static readonly string[] Names =
    [
        "窗口事件", "窗口状态事件", "Dock 刷新", "前台判定", "AppBar 位置通知", "AppBar 提交位置",
        "玻璃同步", "玻璃窗口移动", "任务栏事件", "桌面事件", "Dock 通知资源管理器"
    ];

    private static readonly long[] Counts = new long[Names.Length];
    private static readonly long[] WindowStart = new long[Names.Length];
    private static readonly Stack<(DispatcherOperation Operation, long Started)> Running = new();
    private static DispatcherOperation? _current;
    private static long _currentStarted;
    private static int _lastActivity = -1;
    private static long _answeredAt;
    private static string? _path;
    private static Thread? _thread;
    private static volatile bool _stopping;

    internal static string? LogPath => _path;

    internal static void Hit(UiActivity activity)
    {
        Interlocked.Increment(ref Counts[(int)activity]);
        Volatile.Write(ref _lastActivity, (int)activity);
    }

    /// <summary>Starts the watchdog once; a later call only moves the log to that directory.</summary>
    internal static void Start(Dispatcher dispatcher, string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "stall.log");
        if (_thread is not null)
        {
            return;
        }

        dispatcher.Hooks.OperationStarted += (_, e) => Push(e.Operation);
        dispatcher.Hooks.OperationCompleted += (_, e) => Pop(e.Operation);
        dispatcher.Hooks.OperationAborted += (_, e) => Pop(e.Operation);
        _thread = new Thread(() => Watch(dispatcher))
        {
            IsBackground = true,
            Name = "冰蓝界面卡顿监测"
        };
        _thread.Start();
    }

    internal static void Stop() => _stopping = true;

    // Hooks run on the UI thread; nested pumps (dialogs, menus) start operations inside
    // others, so the innermost running one is published for the watchdog.
    private static void Push(DispatcherOperation operation)
    {
        Running.Push((operation, Environment.TickCount64));
        Publish();
    }

    private static void Pop(DispatcherOperation operation)
    {
        while (Running.Count > 0 && !ReferenceEquals(Running.Pop().Operation, operation))
        {
        }
        Publish();
    }

    private static void Publish()
    {
        if (Running.TryPeek(out var top))
        {
            Volatile.Write(ref _currentStarted, top.Started);
            Volatile.Write(ref _current, top.Operation);
        }
        else
        {
            Volatile.Write(ref _current, null);
        }
    }

    private static void Watch(Dispatcher dispatcher)
    {
        var detector = new UiStallDetector(StallThresholdMs);
        long sentAt = 0;
        var windowBegan = Environment.TickCount64;
        long lastFloodLog = -FloodLogIntervalMs;
        var cpuAt = Environment.TickCount64;
        var cpuTime = CpuTime();
        Write($"监测启动：冰蓝 {Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)}，Windows {Environment.OSVersion.Version}");

        while (!_stopping && !dispatcher.HasShutdownStarted)
        {
            Thread.Sleep(ProbeIntervalMs);
            var now = Environment.TickCount64;
            if (Volatile.Read(ref _answeredAt) >= sentAt && !detector.CanSend
                && detector.Answered(now) is { } ended)
            {
                Write($"界面恢复响应，卡住约 {ended.ElapsedMilliseconds / 1000.0:0.0} 秒。{Counters(now - windowBegan)}");
            }

            if (detector.CanSend)
            {
                sentAt = now;
                detector.Sent(now);
                dispatcher.BeginInvoke(
                    DispatcherPriority.Send,
                    () => Volatile.Write(ref _answeredAt, Environment.TickCount64));
            }
            else if (detector.Check(now) is { } started)
            {
                var cpu = CpuTime();
                var cpuShare = (cpu - cpuTime).TotalMilliseconds / Math.Max(1, now - cpuAt) / Environment.ProcessorCount;
                Write($"界面卡住 {started.ElapsedMilliseconds / 1000.0:0.0} 秒：{Describe()}；"
                    + $"进程 CPU {cpuShare:P0}（占全部核心）；前台 {Foreground()}；{Counters(now - windowBegan)}");
            }

            if (now - windowBegan >= WindowMs)
            {
                var flood = Enumerable.Range(0, Names.Length)
                    .Any(i => Interlocked.Read(ref Counts[i]) - WindowStart[i] >= FloodPerWindow);
                if (flood && now - lastFloodLog >= FloodLogIntervalMs)
                {
                    lastFloodLog = now;
                    Write($"事件密集：前台 {Foreground()}；{Counters(now - windowBegan)}");
                }
                for (var i = 0; i < Names.Length; i++)
                {
                    WindowStart[i] = Interlocked.Read(ref Counts[i]);
                }
                windowBegan = now;
                cpuAt = now;
                cpuTime = CpuTime();
            }
        }
    }

    private static string Describe()
    {
        var operation = Volatile.Read(ref _current);
        var last = Volatile.Read(ref _lastActivity);
        var lastText = last >= 0 ? $"，最近计数路径：{Names[last]}" : string.Empty;
        if (operation is null)
        {
            return $"不在调度任务中（处理 Windows 消息或回调）{lastText}";
        }
        var elapsed = Environment.TickCount64 - Volatile.Read(ref _currentStarted);
        return $"正在执行 {OperationName(operation)}（{operation.Priority}，已运行 {elapsed / 1000.0:0.0} 秒）{lastText}";
    }

    // WPF names an operation after its delegate only internally; fall back to the priority.
    private static readonly PropertyInfo? NameProperty =
        typeof(DispatcherOperation).GetProperty("Name", BindingFlags.Instance | BindingFlags.NonPublic);

    private static string OperationName(DispatcherOperation operation)
    {
        try
        {
            return NameProperty?.GetValue(operation) as string ?? "未知任务";
        }
        catch (Exception exception) when (exception is TargetInvocationException or InvalidOperationException)
        {
            return "未知任务";
        }
    }

    private static string Counters(long windowMs)
    {
        var text = new StringBuilder($"近 {windowMs / 1000.0:0} 秒计数：");
        var any = false;
        for (var i = 0; i < Names.Length; i++)
        {
            var count = Interlocked.Read(ref Counts[i]) - WindowStart[i];
            if (count == 0)
            {
                continue;
            }
            text.Append(any ? "，" : string.Empty).Append(Names[i]).Append(' ').Append(count);
            any = true;
        }
        return any ? text.ToString() : text.Append('无').ToString();
    }

    private static string Foreground()
    {
        var window = DockNativeMethods.GetForegroundWindow();
        if (window == 0)
        {
            return "无";
        }
        var className = new StringBuilder(128);
        DockNativeMethods.GetClassNameW(window, className, className.Capacity);
        DockNativeMethods.GetWindowThreadProcessId(window, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return $"{process.ProcessName}（{className}）";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return $"进程 {processId}（{className}）";
        }
    }

    private static TimeSpan CpuTime()
    {
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    private static void Write(string message)
    {
        if (_path is { } path)
        {
            LocalLogFile.Append(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}", MaximumBytes);
        }
    }
}
