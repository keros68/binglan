using System.Runtime;
using System.Windows.Threading;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.App.Windows;

namespace BingLan.App;

public sealed partial class WidgetCoordinator
{
    private static readonly TimeSpan StartupMemoryTrimDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WindowClosedMemoryTrimDelay = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan MinimumMemoryTrimInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DragDeferralRetryDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan CooldownRescheduleMargin = TimeSpan.FromSeconds(1);

    private DispatcherTimer? _memoryTrimTimer;
    private bool _memoryTrimIdleQueued;
    private long _lastMemoryTrimTick;

    /// <summary>
    /// Clears process-wide icon caches once when Explorer restarts (the system image list
    /// may be rebuilt), rather than on every Win+D / shell window recovery.
    /// </summary>
    internal static void ClearProcessIconCachesOnExplorerRestart()
    {
        ShellIconProvider.ClearIconCache();
        LauncherIconCache.Clear();
    }

    /// <summary>
    /// Determines whether an idle memory trim may run right now. Never silently drops a
    /// requested trim: defers during active drags and reschedules past the remaining
    /// <see cref="MinimumMemoryTrimInterval"/> cooldown window (+1s margin).
    /// </summary>
    internal static (bool ShouldRun, TimeSpan? RetryAfter) EvaluateIdleMemoryTrim(
        long nowTick,
        long lastTrimTick,
        bool isDragging)
    {
        if (isDragging)
        {
            return (false, DragDeferralRetryDelay);
        }

        if (lastTrimTick != 0)
        {
            var elapsedMs = unchecked(nowTick - lastTrimTick);
            var minIntervalMs = (long)MinimumMemoryTrimInterval.TotalMilliseconds;
            if (elapsedMs >= 0 && elapsedMs < minIntervalMs)
            {
                var remainingMs = minIntervalMs - elapsedMs;
                return (false, TimeSpan.FromMilliseconds(remainingMs) + CooldownRescheduleMargin);
            }
        }

        return (true, null);
    }

    /// <summary>
    /// Schedules a debounced, idle-time compacting Gen2/LOH collection after startup bursts
    /// or after heavy transient windows (Settings, Overview Card) close so committed managed
    /// heap segments return to the OS without stalling active UI interactions.
    /// </summary>
    private void ScheduleIdleMemoryTrim(TimeSpan? delay = null)
    {
        if (_isExiting || _exitRequested)
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        var effectiveDelay = delay ?? WindowClosedMemoryTrimDelay;
        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => ScheduleIdleMemoryTrim(effectiveDelay));
            return;
        }

        if (_memoryTrimTimer is null)
        {
            _memoryTrimTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
            _memoryTrimTimer.Tick += OnMemoryTrimTimerTick;
        }

        _memoryTrimTimer.Stop();
        _memoryTrimTimer.Interval = effectiveDelay;
        _memoryTrimTimer.Start();
    }

    private void OnMemoryTrimTimerTick(object? sender, EventArgs e)
    {
        _memoryTrimTimer?.Stop();
        if (_isExiting || _exitRequested || _memoryTrimIdleQueued)
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        _memoryTrimIdleQueued = true;
        _ = dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, ExecuteIdleMemoryTrim);
    }

    private void ExecuteIdleMemoryTrim()
    {
        _memoryTrimIdleQueued = false;
        if (_isExiting || _exitRequested)
        {
            return;
        }

        var now = Environment.TickCount64;
        var (shouldRun, retryAfter) = EvaluateIdleMemoryTrim(now, _lastMemoryTrimTick, IsAnyWidgetDragging());
        if (!shouldRun)
        {
            if (retryAfter is { } delay)
            {
                ScheduleIdleMemoryTrim(delay);
            }
            return;
        }

        _lastMemoryTrimTick = now;
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        // Protected by ApplicationIdle scheduling, >=60s cooldown, and active-drag deferral:
        // Aggressive collection actively decommits free Gen2/LOH segments back to the OS.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private static bool IsAnyWidgetDragging()
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return false;
        }

        foreach (var window in app.Windows)
        {
            if (window is WidgetWindowBase { IsDraggingOrResizing: true })
            {
                return true;
            }
        }
        return false;
    }
}
