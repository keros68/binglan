using System.Diagnostics;
using System.IO;
using System.Windows.Media;

namespace BingLan.App.Dock;

internal sealed class DockFrameDiagnostics(Func<double> readScale)
{
    private static readonly long Deadline = ResolveDeadline();
    private static readonly object LogLock = new();
    private string? _action;
    private List<double>? _intervals;
    private TimeSpan _lastFrame;
    private long _startTicks;
    private long _lastTicks;
    private double _startScale;
    private double _minScale;
    private double _maxScale;
    private bool _subscribed;

    internal static bool CanStart => Deadline != 0 && Stopwatch.GetTimestamp() < Deadline;

    internal void Begin(string action, bool useOwnRendering)
    {
        Finish(completed: false);
        if (!CanStart)
        {
            return;
        }

        _action = action;
        _startTicks = Stopwatch.GetTimestamp();
        _lastTicks = 0;
        _lastFrame = TimeSpan.MinValue;
        _startScale = readScale();
        _minScale = double.PositiveInfinity;
        _maxScale = double.NegativeInfinity;
        _intervals = new List<double>(32);
        _subscribed = useOwnRendering;
        if (_subscribed)
        {
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs args)
        {
            Observe(args.RenderingTime);
        }
    }

    internal void Observe(TimeSpan frame)
    {
        if (_intervals is not { } intervals || frame == TimeSpan.MinValue || frame == _lastFrame)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (now >= Deadline)
        {
            Finish(completed: false);
            return;
        }

        if (_lastTicks != 0)
        {
            intervals.Add(Stopwatch.GetElapsedTime(_lastTicks, now).TotalMilliseconds);
        }
        var scale = readScale();
        _minScale = Math.Min(_minScale, scale);
        _maxScale = Math.Max(_maxScale, scale);
        _lastTicks = now;
        _lastFrame = frame;
    }

    internal void Finish(bool completed)
    {
        if (_action is not { } action || _intervals is not { } intervals)
        {
            return;
        }

        if (_subscribed)
        {
            CompositionTarget.Rendering -= OnRendering;
            _subscribed = false;
        }
        _action = null;
        _intervals = null;

        var now = Stopwatch.GetTimestamp();
        var status = now >= Deadline ? "expired" : completed ? "completed" : "interrupted";
        var endScale = readScale();
        var frameCount = _lastTicks == 0 ? 0 : intervals.Count + 1;
        var elapsedMs = Stopwatch.GetElapsedTime(_startTicks, now).TotalMilliseconds;
        var startScale = _startScale;
        double? minScale = frameCount == 0 ? null : _minScale;
        double? maxScale = frameCount == 0 ? null : _maxScale;
        _ = Task.Run(() => Write(action, status, intervals, frameCount, elapsedMs, startScale, endScale, minScale, maxScale));
    }

    private static void Write(string action, string status, List<double> intervals, int frameCount,
        double elapsedMs, double startScale, double endScale, double? minScale, double? maxScale)
    {
        intervals.Sort();
        double? p95 = intervals.Count == 0 ? null : intervals[(int)Math.Ceiling(intervals.Count * 0.95d) - 1];
        double? max = intervals.Count == 0 ? null : intervals[^1];
        var over33 = intervals.Count(ms => ms > 33d);
        var over50 = intervals.Count(ms => ms > 50d);
        var line = FormattableString.Invariant(
            $"{DateTimeOffset.UtcNow:O} action={action} status={status} frames={frameCount} elapsedMs={elapsedMs:F2} p95Ms={p95:F2} maxMs={max:F2} over33Ms={over33} over50Ms={over50} startScale={startScale:F4} minRenderedScale={minScale:F4} maxRenderedScale={maxScale:F4} endScale={endScale:F4}");
        var path = Path.Combine(Path.GetTempPath(), $"BingLan-Dock-Frames-{Environment.ProcessId}.log");
        try
        {
            lock (LogLock)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static long ResolveDeadline()
    {
        if (Environment.GetEnvironmentVariable("BINGLAN_DIAG_DOCK_FRAMES") != "1")
        {
            return 0;
        }

        using var process = Process.GetCurrentProcess();
        var remaining = process.StartTime.ToUniversalTime().AddMinutes(10) - DateTime.UtcNow;
        return remaining <= TimeSpan.Zero
            ? 0
            : Stopwatch.GetTimestamp() + (long)(remaining.TotalSeconds * Stopwatch.Frequency);
    }
}
