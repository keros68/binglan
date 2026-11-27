using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BingLan.App.Interop;
using BingLan.Core.Models;
using BingLan.Core.Taskbar;
using Microsoft.Win32;

namespace BingLan.App.Taskbar;

/// <summary>
/// Isolated adapter for the native Explorer taskbar. It records what it changes in a
/// checkpoint before changing it, restores the system default on exit, after a crash
/// (next start) and on request. On Windows 11, transparent mode leases only Explorer's XAML
/// background; on Windows 10 transparent and blur are window accents on the Win32 taskbar.
/// Transparent and blur turn the user-level transparency effects on when they are off —
/// recorded in the same checkpoint and put back on restore — instead of asking the user
/// to flip the system setting. It only changes the taskbar while no other taskbar tool
/// runs, so the state it found is the system default, which is what a restore puts back.
/// </summary>
internal sealed class TaskbarAdapter : IDisposable
{
    private const int WsPopup = unchecked((int)0x80000000);
    private static readonly TimeSpan ReapplyDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumEventReapplyInterval = TimeSpan.FromMilliseconds(150);
    private const int CompetitorCheckTicks = 10;
    private const int AutoHideRecoveryAttemptCount = 5;
    private static readonly string[] CompetingCustomizers =
        ["TranslucentTB", "TaskbarX", "RoundedTB", "StartAllBack"];
    private static readonly string[] TaskbarClasses =
        ["Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    private readonly string _checkpointPath;
    private readonly TaskbarFailureBreaker _breaker = new();
    private readonly DispatcherTimer _reapplyTimer;
    private readonly DispatcherTimer _keepAliveTimer;
    private readonly Services.BackgroundWinEventPump _pump;
    private readonly TaskbarPlatformOps _ops;
    private readonly List<Services.BackgroundWinEventPump.WinEventSubscription> _winEventSubscriptions = [];
    private List<nint> _taskbars = [];
    private readonly uint _taskbarCreatedMessage;
    private HwndSource? _messageWindow;
    private DateTime _lastEventReapply;
    private int _winEventQueued;
    private int _keepAliveTicks;
    private volatile bool _disposed;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly object _systemWriteLock = new();
    private CancellationTokenSource? _applyCancellation;
    private CancellationTokenSource? _reconcileCancellation;
    private volatile Task? _inFlightCompetitorRollback;
    private volatile TaskbarXamlSession? _xamlSession;
    private int _requestVersion;
    private uint _explorerProcessId;
    private volatile int _heldAccent;
    private (nint Window, uint ProcessId) _autoHideTaskbar;
    private bool _ownsAutoHide;
    private int _autoHideRecoveryAttempts;

    internal TaskbarAdapter(
        string checkpointPath,
        Services.BackgroundWinEventPump? pump = null,
        TaskbarPlatformOps? ops = null)
    {
        _checkpointPath = checkpointPath;
        _pump = pump ?? Services.BackgroundWinEventPump.Shared;
        _ops = ops ?? TaskbarPlatformOps.Live;
        _taskbarCreatedMessage = TaskbarNativeMethods.RegisterWindowMessageW("TaskbarCreated");
        _reapplyTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ReapplyDelay };
        _reapplyTimer.Tick += (_, _) =>
        {
            _reapplyTimer.Stop();
            Guard(Reapply);
        };
        _keepAliveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = KeepAliveInterval };
        _keepAliveTimer.Tick += (_, _) => Guard(KeepAlive);
    }

    /// <summary>The mode currently held on the taskbar.</summary>
    internal TaskbarMode ActiveMode { get; private set; } = TaskbarMode.SystemDefault;

    /// <summary>Why the requested mode is not active, or empty.</summary>
    internal string Problem { get; private set; } = string.Empty;

    internal event Action? StatusChanged;

    /// <summary>Explorer restarted and broadcast <c>TaskbarCreated</c>.</summary>
    internal event Action? ExplorerRestarted;

    /// <summary>The user turned auto-hide off in Windows while this app held it.</summary>
    internal event Action? AutoHideTakenOver;

    private static readonly Lazy<int?> WindowsUpdateRevision = new(() =>
    {
        try
        {
            return Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", null) as int?;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    });

    // Looking for other taskbar tools lists processes, so the keep-alive tick leaves it to
    // its less frequent competitor check.
    internal static TaskbarEnvironment ReadEnvironment(bool lookForCompetitors = true) => new(
        Environment.OSVersion.Version.Build,
        SystemParameters.HighContrast,
        TaskbarNativeMethods.GetSystemMetrics(TaskbarNativeMethods.SmRemoteSession) != 0,
        IsSystemTransparencyEnabled(),
        lookForCompetitors ? CompetingCustomizers.FirstOrDefault(IsRunning) : null,
        Environment.OSVersion.Version.Build == 26300 ? WindowsUpdateRevision.Value : null);

    private static bool IsRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        foreach (var process in processes)
        {
            process.Dispose();
        }
        return processes.Length > 0;
    }

    /// <summary>Undoes changes left by a previous run that did not exit cleanly.</summary>
    internal static bool RecoverFromCheckpoint(string checkpointPath) =>
        RecoverFromCheckpoint(checkpointPath, TaskbarPlatformOps.Live);

    internal static bool RecoverFromCheckpoint(string checkpointPath, TaskbarPlatformOps ops)
        => RecoverFromCheckpoint(checkpointPath, ops, null);

    private static bool RecoverFromCheckpoint(
        string checkpointPath, TaskbarPlatformOps ops, TaskbarXamlSession? liveSession)
    {
        if (!File.Exists(checkpointPath))
        {
            return liveSession is null || liveSession.Restore() == TaskbarXamlRestoreResult.Restored;
        }

        TaskbarCheckpoint? checkpoint;
        try
        {
            checkpoint = TaskbarCheckpoint.Parse(File.ReadAllText(checkpointPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (checkpoint is null)
        {
            if (liveSession is not null && liveSession.Restore() != TaskbarXamlRestoreResult.Restored) return false;
            // Unreadable or from a newer version: undo the one change that is always
            // safe to undo and keep the file aside instead of pretending success.
            // The file is only set aside once that undo worked, so a failed attempt is
            // retried on the next start.
            if (ops.TrySetAccent(ops.FindTaskbars(), TaskbarNativeMethods.AccentDisabled, out _))
            {
                TryMove(checkpointPath, checkpointPath + ".invalid");
            }
            return false;
        }

        var restored = Restore(checkpointPath, checkpoint, ops, liveSession);
        if (restored)
        {
            TryDelete(checkpointPath);
        }
        return restored;
    }

    /// <summary>
    /// Applies a mode; requests run one at a time and a newer one cancels an older one.
    /// Failures end at the system default taskbar and are reported through Problem, so
    /// the returned task never faults.
    /// </summary>
    internal async Task ApplyAsync(TaskbarMode mode)
    {
        if (_disposed) return;
        var version = Interlocked.Increment(ref _requestVersion);
        _applyCancellation?.Cancel();
        _reconcileCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _applyCancellation = cancellation;
        _xamlSession?.RequestStop();
        var entered = false;
        try
        {
            await _applyGate.WaitAsync(cancellation.Token);
            entered = true;
            if (_disposed || version != Volatile.Read(ref _requestVersion))
            {
                return;
            }
            await ApplyCoreAsync(mode, version, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // The next serialized request restores the previous lease before applying.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Any failure inside the adapter ends at the system default taskbar.
            Debug.WriteLine($"任务栏适配失败：{exception}");
            bool restored;
            try
            {
                restored = await RestoreActiveAsync();
            }
            catch (Exception restoreException) when (restoreException is not OutOfMemoryException)
            {
                Debug.WriteLine($"任务栏恢复失败：{restoreException}");
                restored = false;
            }
            if (!_disposed && version == Volatile.Read(ref _requestVersion))
                SetStatus(TaskbarMode.SystemDefault, restored
                    ? $"任务栏适配未能启用，已恢复进入前的状态：{exception.Message}"
                    : "任务栏适配出错，未能确认恢复，下次启动时会再次恢复");
        }
        finally
        {
            if (entered) _applyGate.Release();
            if (ReferenceEquals(_applyCancellation, cancellation)) _applyCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task ApplyCoreAsync(TaskbarMode mode, int version, CancellationToken cancellationToken)
    {
        _breaker.Reset();
        if (!await RestoreActiveAsync())
        {
            SetStatus(TaskbarMode.SystemDefault, "未能恢复任务栏原来的状态，暂不应用新的设置");
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || version != Volatile.Read(ref _requestVersion)) return;
        SetStatus(TaskbarMode.SystemDefault, string.Empty);

        var compatibility = TaskbarCompatibility.Assess(mode, _ops.ReadEnvironment(true));
        if (!compatibility.IsSupported)
        {
            SetStatus(TaskbarMode.SystemDefault, compatibility.Reason);
            return;
        }

        var effectiveMode = compatibility.SubstituteMode ?? mode;
        switch (effectiveMode)
        {
            case TaskbarMode.Transparent when TaskbarCompatibility.UsesWin32Taskbar(Environment.OSVersion.Version.Build):
                // Windows 10: the taskbar is a Win32 window, so transparency is a window
                // accent held and restored the same way as blur. It needs no system setting.
                await ApplyAccentAsync(
                    TaskbarMode.Transparent,
                    TaskbarNativeMethods.AccentEnableTransparentGradient,
                    turnOnSystemTransparency: false,
                    notice: string.Empty,
                    version,
                    cancellationToken);
                break;
            case TaskbarMode.Transparent:
                var explorer = _ops.ReadTaskbarIdentity().ProcessId;
                if (explorer == 0)
                {
                    SetStatus(TaskbarMode.SystemDefault, "尚未找到 Windows 任务栏，保持系统默认");
                    return;
                }
                var xamlSession = new TaskbarXamlSession(Path.GetDirectoryName(_checkpointPath)!, explorer);
                _xamlSession = xamlSession;
                // The record says what to undo before the effect itself is turned on;
                // the effect is only claimed when the app was the one switching it on.
                var originalTransparency = _ops.IsSystemTransparencyEnabled() ? (bool?)null : false;
                var checkpoint = new TaskbarCheckpoint
                {
                    XamlSessionName = xamlSession.MappingName,
                    XamlExplorerProcessId = explorer,
                    OriginalSystemTransparency = originalTransparency
                };
                var prepOutcome = await Task.Run(() =>
                {
                    lock (_systemWriteLock)
                    {
                        if (_disposed || cancellationToken.IsCancellationRequested || version != Volatile.Read(ref _requestVersion))
                        {
                            return (Superseded: true, TransparencyOk: false);
                        }
                        WriteCheckpoint(checkpoint);
                        if (originalTransparency == false && !_ops.TryWriteSystemTransparency(true))
                        {
                            return (Superseded: false, TransparencyOk: false);
                        }
                        return (Superseded: false, TransparencyOk: true);
                    }
                });
                if (prepOutcome.Superseded || _disposed || version != Volatile.Read(ref _requestVersion))
                {
                    return;
                }
                if (!prepOutcome.TransparencyOk)
                {
                    await RestoreActiveAsync();
                    SetStatus(TaskbarMode.SystemDefault, "未能开启 Windows 透明效果，保持系统默认");
                    return;
                }
                cancellationToken.ThrowIfCancellationRequested();
                var taskbarCount = await Task.Run(() => _ops.FindTaskbars().Count, cancellationToken);
                if (_ops.ReadTaskbarIdentity().ProcessId != explorer)
                    throw new InvalidOperationException("资源管理器正在重建任务栏");
                await xamlSession.StartAsync(taskbarCount, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (_disposed || version != Volatile.Read(ref _requestVersion)) return;
                if (xamlSession.ExplorerProcessId != explorer || _ops.ReadTaskbarIdentity().ProcessId != explorer)
                    throw new InvalidOperationException("资源管理器正在重建任务栏");
                EnsureMessageWindow();
                _explorerProcessId = explorer;
                _keepAliveTimer.Start();
                SetStatus(mode, string.Empty);
                break;
            case TaskbarMode.Blur:
                await ApplyAccentAsync(
                    TaskbarMode.Blur,
                    TaskbarNativeMethods.AccentEnableBlurBehind,
                    turnOnSystemTransparency: true,
                    notice: compatibility.SubstituteMode is null ? string.Empty : compatibility.Reason,
                    version,
                    cancellationToken);
                break;
            case TaskbarMode.SmartHide:
                await ApplyAutoHideAsync(version, cancellationToken);
                break;
            default:
                SetStatus(TaskbarMode.SystemDefault, string.Empty);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _requestVersion);
        _applyCancellation?.Cancel();
        _reconcileCancellation?.Cancel();
        _xamlSession?.RequestStop();
        try
        {
            RestoreActive();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Debug.WriteLine($"任务栏恢复失败：{exception}");
        }
        _messageWindow?.Dispose();
        _messageWindow = null;
    }

    private bool HoldsAccent => _heldAccent != TaskbarNativeMethods.AccentDisabled;

    // Holds a window accent on the native taskbars: blur on either Windows, and on
    // Windows 10 also transparency. The checkpoint's TransparencyApplied restores both,
    // because a restore always sets the accent back to disabled.
    private async Task ApplyAccentAsync(
        TaskbarMode mode,
        int accent,
        bool turnOnSystemTransparency,
        string notice,
        int version,
        CancellationToken cancellationToken)
    {
        var originalTransparency = turnOnSystemTransparency && !_ops.IsSystemTransparencyEnabled() ? (bool?)false : null;
        var prep = await Task.Run(() =>
        {
            lock (_systemWriteLock)
            {
                if (_disposed || cancellationToken.IsCancellationRequested || version != Volatile.Read(ref _requestVersion))
                {
                    return (Superseded: true, TransparencyOk: false, Taskbars: (List<nint>?)null);
                }
                WriteCheckpoint(new TaskbarCheckpoint
                {
                    TransparencyApplied = true,
                    OriginalSystemTransparency = originalTransparency
                });
                if (originalTransparency == false && !_ops.TryWriteSystemTransparency(true))
                {
                    TryDelete(_checkpointPath);
                    return (Superseded: false, TransparencyOk: false, Taskbars: (List<nint>?)null);
                }
                if (_disposed || cancellationToken.IsCancellationRequested || version != Volatile.Read(ref _requestVersion))
                {
                    return (Superseded: true, TransparencyOk: true, Taskbars: (List<nint>?)null);
                }
                return (Superseded: false, TransparencyOk: true, Taskbars: _ops.FindTaskbars());
            }
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (prep.Superseded || _disposed || version != Volatile.Read(ref _requestVersion))
        {
            return;
        }
        if (!prep.TransparencyOk)
        {
            SetStatus(TaskbarMode.SystemDefault, "未能开启 Windows 透明效果，保持系统默认");
            return;
        }
        _taskbars = prep.Taskbars!;
        if (_ops.TrySetAccent(_taskbars, accent, out var error))
        {
            _heldAccent = accent;
            EnsureMessageWindow();
            StartKeepingTransparent();
            SetStatus(mode, notice);
            return;
        }

        // Keep the checkpoint when the rollback itself fails, so the next start retries.
        var rolledBack = await Task.Run(() =>
        {
            lock (_systemWriteLock)
            {
                if (_ops.TrySetAccent(_taskbars, TaskbarNativeMethods.AccentDisabled, out _))
                {
                    if (originalTransparency == false && _ops.IsSystemTransparencyEnabled())
                    {
                        _ = _ops.TryWriteSystemTransparency(false);
                    }
                    TryDelete(_checkpointPath);
                    return true;
                }
                return false;
            }
        });
        if (_disposed || version != Volatile.Read(ref _requestVersion))
        {
            return;
        }
        if (rolledBack)
        {
            SetStatus(TaskbarMode.SystemDefault, $"无法设置任务栏外观：{error}");
            return;
        }
        SetStatus(TaskbarMode.SystemDefault, $"无法设置任务栏外观，也未能完全恢复：{error}。下次启动时会再次恢复");
    }

    private async Task ApplyAutoHideAsync(int version, CancellationToken cancellationToken)
    {
        if (await Task.Run(_ops.GetAutoHide, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed || version != Volatile.Read(ref _requestVersion))
            {
                return;
            }
            // Already hidden by the user; nothing of ours to restore later.
            _autoHideTaskbar = _ops.ReadTaskbarIdentity();
            EnsureMessageWindow();
            SetStatus(TaskbarMode.SmartHide, string.Empty);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var applied = await Task.Run(() =>
        {
            lock (_systemWriteLock)
            {
                if (_disposed || cancellationToken.IsCancellationRequested || version != Volatile.Read(ref _requestVersion))
                {
                    return (Written: false, Succeeded: false);
                }
                WriteCheckpoint(new TaskbarCheckpoint { AutoHideApplied = true, OriginalAutoHide = false });
                if (_ops.SetAutoHide(true))
                {
                    return (Written: true, Succeeded: true);
                }
                TryDelete(_checkpointPath);
                return (Written: true, Succeeded: false);
            }
        });
        if (!applied.Written || _disposed || version != Volatile.Read(ref _requestVersion))
        {
            return;
        }
        if (applied.Succeeded)
        {
            _autoHideTaskbar = _ops.ReadTaskbarIdentity();
            _ownsAutoHide = true;
            EnsureMessageWindow();
            SetStatus(TaskbarMode.SmartHide, string.Empty);
            return;
        }

        SetStatus(TaskbarMode.SystemDefault, "Windows 没有接受任务栏自动隐藏设置");
    }

    private bool RestoreActive()
    {
        StopReapplying();
        ActiveMode = TaskbarMode.SystemDefault;
        return RestoreActiveCore();
    }

    private Task<bool> RestoreActiveAsync()
    {
        StopReapplying();
        ActiveMode = TaskbarMode.SystemDefault;
        return Task.Run(RestoreActiveCore);
    }

    private bool RestoreActiveCore()
    {
        lock (_systemWriteLock)
        {
            var session = _xamlSession;
            try
            {
                return RecoverFromCheckpoint(_checkpointPath, _ops, session);
            }
            finally
            {
                // Keep the stopped lease while this host's native connection is still
                // in flight, so a later request also waits for that call to finish.
                if (session?.HasPendingConnection != true || _disposed)
                {
                    session?.Dispose();
                    _xamlSession = null;
                }
            }
        }
    }

    private static bool Restore(string checkpointPath, TaskbarCheckpoint checkpoint,
        TaskbarPlatformOps ops, TaskbarXamlSession? liveSession)
    {
        if (checkpoint.XamlSessionName is { } name)
        {
            TaskbarXamlRestoreResult result;
            if (liveSession is not null && liveSession.MappingName == name)
            {
                result = liveSession.Restore(ops.IsProcessRunning);
            }
            else if (checkpoint.XamlRestoreFailed && checkpoint.XamlExplorerProcessId is { } failedExplorer)
            {
                if (ops.IsProcessRunning(failedExplorer)) return false;
                result = TaskbarXamlRestoreResult.Restored;
            }
            else
            {
                result = TaskbarXamlSession.Restore(name);
            }

            if (result == TaskbarXamlRestoreResult.Failed)
            {
                var explorer = liveSession?.ExplorerProcessId ?? checkpoint.XamlExplorerProcessId
                    ?? ops.ReadTaskbarIdentity().ProcessId;
                checkpoint.XamlExplorerProcessId = explorer != 0 ? explorer : null;
                checkpoint.XamlRestoreFailed = true;
                WriteCheckpoint(checkpointPath, checkpoint);
                return false;
            }
            if (result == TaskbarXamlRestoreResult.Unconfirmed) return false;

            // A later system transparency retry no longer depends on the XAML lease.
            checkpoint.XamlExplorerProcessId = null;
            checkpoint.XamlRestoreFailed = false;
            checkpoint.XamlSessionName = null;
            WriteCheckpoint(checkpointPath, checkpoint);
        }
        var restored = true;
        if (checkpoint.AutoHideApplied && !ops.HasTaskbarWindow())
        {
            // Without the taskbar window Windows reports "not hidden", which is not the
            // user's choice: keep the checkpoint and try again once Explorer is up.
            restored = false;
        }
        var plan = TaskbarRecovery.Plan(checkpoint, ops.GetAutoHide());
        if (plan.ResetTransparency)
        {
            restored &= ops.TrySetAccent(ops.FindTaskbars(), TaskbarNativeMethods.AccentDisabled, out _);
        }
        if (plan.ResetSystemTransparency && ops.IsSystemTransparencyEnabled())
        {
            // Only when the effect is still on: if the user turned it off themselves,
            // the value to restore to is already in place.
            restored &= ops.TryWriteSystemTransparency(false);
        }
        if (plan.SetAutoHide is { } autoHide && ops.HasTaskbarWindow())
        {
            restored &= ops.SetAutoHide(autoHide);
        }
        return restored;
    }

    // Explorer puts its own material back when the Start menu or another shell surface
    // opens, and recreates the taskbar on restart, display or theme changes. There is no
    // API to read the current material, so transparency is re-applied after such events
    // and on a slow keep-alive tick.
    private void StartKeepingTransparent()
    {
        if (_winEventSubscriptions.Count == 0)
        {
            Hook(TaskbarNativeMethods.EventSystemForeground, TaskbarNativeMethods.EventSystemForeground);
            Hook(TaskbarNativeMethods.EventObjectShow, TaskbarNativeMethods.EventObjectHide);
            Hook(TaskbarNativeMethods.EventObjectCloaked, TaskbarNativeMethods.EventObjectUncloaked);
        }
        _keepAliveTimer.Start();
    }

    private void StopReapplying()
    {
        _heldAccent = TaskbarNativeMethods.AccentDisabled;
        _autoHideTaskbar = default;
        _ownsAutoHide = false;
        _autoHideRecoveryAttempts = 0;
        _reapplyTimer.Stop();
        _keepAliveTimer.Stop();
        foreach (var subscription in _winEventSubscriptions)
        {
            subscription.Dispose();
        }
        _winEventSubscriptions.Clear();
    }

    private void Hook(uint eventMin, uint eventMax)
    {
        var subscription = _pump.Subscribe(
            eventMin,
            eventMax,
            static message => ShouldForwardWinEvent(message),
            OnBackgroundWinEvent);
        if (subscription.IsActive)
        {
            _winEventSubscriptions.Add(subscription);
        }
        else
        {
            subscription.Dispose();
        }
    }

    internal static bool ShouldForwardWinEvent(in Services.WinEventMessage message)
    {
        if (message.EventType == TaskbarNativeMethods.EventSystemForeground)
        {
            return message.ChildId == 0 && message.ObjectId == TaskbarNativeMethods.ObjidWindow;
        }
        return Services.BackgroundWinEventPump.IsTopLevelWindowEvent(message);
    }

    private void OnBackgroundWinEvent(Services.WinEventMessage message)
    {
        if (_disposed || !HoldsAccent)
        {
            return;
        }
        Services.UiDiagnostics.Hit(Services.UiActivity.TaskbarEvent);
        if (Interlocked.CompareExchange(ref _winEventQueued, 1, 0) != 0)
        {
            return;
        }
        var dispatcher = _reapplyTimer.Dispatcher;
        if (dispatcher.HasShutdownStarted)
        {
            Interlocked.Exchange(ref _winEventQueued, 0);
            return;
        }
        dispatcher.BeginInvoke(DispatcherPriority.Background, HandleCoalescedWinEvent);
    }

    private void HandleCoalescedWinEvent()
    {
        Interlocked.Exchange(ref _winEventQueued, 0);
        if (_disposed || !HoldsAccent)
        {
            return;
        }

        // Fast-path only touches cached taskbar handles; full FindTaskbars() recovery
        // runs on the debounced timer tick rather than synchronously on event bursts.
        var now = DateTime.UtcNow;
        if (_taskbars.Count > 0 && now - _lastEventReapply >= MinimumEventReapplyInterval)
        {
            _lastEventReapply = now;
            if (TrySetAccent(_taskbars, _heldAccent, out _))
            {
                _breaker.RecordSuccess();
            }
        }
        _reapplyTimer.Stop();
        _reapplyTimer.Start();
    }

    private void KeepAlive()
    {
        if (++_keepAliveTicks % CompetitorCheckTicks == 0
            && _ops.ReadEnvironment(true).CompetingCustomizer is { } customizer)
        {
            // Another tool now owns the taskbar; stop without resetting its appearance.
            var takeoverVersion = Interlocked.Increment(ref _requestVersion);
            _applyCancellation?.Cancel();
            _reconcileCancellation?.Cancel();
            StopReapplying();
            if (_xamlSession is null)
            {
                // Only the effect this app switched on goes back; the other tool keeps the taskbar.
                bool needsTransparencyRollback;
                lock (_systemWriteLock)
                {
                    var recorded = File.Exists(_checkpointPath) ? ReadCheckpoint(_checkpointPath) : null;
                    needsTransparencyRollback = recorded?.OriginalSystemTransparency == false;
                    if (needsTransparencyRollback)
                    {
                        // Clear TransparencyApplied before yielding so crash/exit recovery never resets
                        // the third-party tool's accent, while retaining OriginalSystemTransparency=false
                        // until the registry rollback actually succeeds.
                        WriteCheckpoint(new TaskbarCheckpoint
                        {
                            TransparencyApplied = false,
                            AutoHideApplied = false,
                            OriginalSystemTransparency = false
                        });
                    }
                    else
                    {
                        TryDelete(_checkpointPath);
                    }
                }

                if (needsTransparencyRollback)
                {
                    _inFlightCompetitorRollback = Task.Run(() =>
                    {
                        lock (_systemWriteLock)
                        {
                            if (_disposed || takeoverVersion != Volatile.Read(ref _requestVersion))
                            {
                                return;
                            }
                            if (!_ops.IsSystemTransparencyEnabled() || _ops.TryWriteSystemTransparency(false))
                            {
                                TryDelete(_checkpointPath);
                            }
                        }
                    });
                }
            }
            else
            {
                _ = RestoreRuntimeAsync(
                    $"{customizer} 正在调整任务栏，冰蓝桌面已停止调整",
                    $"{customizer} 正在调整任务栏，冰蓝桌面已停止调整，等待任务栏恢复确认");
                return;
            }
            SetStatus(TaskbarMode.SystemDefault, $"{customizer} 正在调整任务栏，冰蓝桌面已停止调整");
            return;
        }

        if (_xamlSession is not null && ActiveMode == TaskbarMode.Transparent)
        {
            var environment = _ops.ReadEnvironment(false);
            if (!environment.IsSystemTransparencyEnabled)
            {
                // The user turned the transparency effects off while we held the mode.
                // Step aside; the restore leaves the off value alone and only clears
                // what the app itself switched on.
                _ = RestoreRuntimeAsync(
                    "Windows 透明效果已被关闭，已恢复系统默认；重新选择“透明”会自动重新开启",
                    "Windows 透明效果已被关闭，已停止调整；下次启动时会核对恢复状态");
                return;
            }

            var compatibility = TaskbarCompatibility.Assess(TaskbarMode.Transparent, environment);
            if (!compatibility.IsSupported)
            {
                StopReapplying();
                _ = RestoreRuntimeAsync(compatibility.Reason, "已停止调整，等待任务栏恢复确认");
                return;
            }
            if (TryReconnectXaml())
            {
                return;
            }
            if (_xamlSession?.IsActive != true && _breaker.RecordFailure())
            {
                StopReapplying();
                _ = RestoreRuntimeAsync("任务栏背景不可用，已恢复系统默认",
                    "任务栏背景不可用，已停止调整；下次启动时会核对恢复状态");
            }
            else if (_xamlSession?.IsActive == true) _breaker.RecordSuccess();
            return;
        }

        Reapply();
    }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Debug.WriteLine($"任务栏适配异常：{exception}");
            var restored = false;
            try
            {
                restored = RestoreActive();
            }
            catch (Exception restoreException) when (restoreException is not OutOfMemoryException)
            {
                Debug.WriteLine($"任务栏恢复失败：{restoreException}");
            }
            SetStatus(
                TaskbarMode.SystemDefault,
                restored ? "任务栏适配出错，已恢复系统默认" : "任务栏适配出错，下次启动时恢复");
        }
    }

    private void Reapply()
    {
        if (_disposed)
        {
            return;
        }
        if (ActiveMode == TaskbarMode.SmartHide)
        {
            ReconcileAutoHide();
            return;
        }
        if (!HoldsAccent)
        {
            return;
        }

        if (_ops.TrySetAccent(_taskbars, _heldAccent, out _))
        {
            _breaker.RecordSuccess();
            return;
        }

        _taskbars = _ops.FindTaskbars();
        if (_ops.TrySetAccent(_taskbars, _heldAccent, out var error))
        {
            _breaker.RecordSuccess();
            return;
        }

        if (_breaker.RecordFailure())
        {
            _ = RestoreRuntimeAsync(
                $"任务栏外观多次未能保持，已恢复系统默认：{error}",
                $"任务栏外观多次未能保持，下次启动时恢复：{error}");
            return;
        }

        _reapplyTimer.Start();
    }

    private void EnsureMessageWindow()
    {
        if (_messageWindow is not null)
        {
            return;
        }

        // A hidden top-level window receives the TaskbarCreated broadcast.
        _messageWindow = new HwndSource(new HwndSourceParameters("BingLan.TaskbarWatcher")
        {
            WindowStyle = WsPopup,
            Width = 0,
            Height = 0
        });
        _messageWindow.AddHook(WatchTaskbar);
    }

    private nint WatchTaskbar(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)message == _taskbarCreatedMessage)
        {
            ExplorerRestarted?.Invoke();
        }
        if ((uint)message == _taskbarCreatedMessage
            || message is TaskbarNativeMethods.WmSettingChange
                or TaskbarNativeMethods.WmThemeChanged
                or TaskbarNativeMethods.WmDwmColorizationColorChanged
                or TaskbarNativeMethods.WmDisplayChange)
        {
            if (_xamlSession is not null && ActiveMode == TaskbarMode.Transparent
                && (uint)message == _taskbarCreatedMessage)
            {
                TryReconnectXaml();
            }
            else if (HoldsAccent)
            {
                _taskbars = [];
                _reapplyTimer.Stop();
                _reapplyTimer.Start();
            }
            else if (ActiveMode == TaskbarMode.SmartHide
                && ((uint)message == _taskbarCreatedMessage || message == TaskbarNativeMethods.WmSettingChange))
            {
                var taskbar = _ops.ReadTaskbarIdentity();
                // Explorer can report its new default state before sending TaskbarCreated.
                // Recheck the taskbar identity after the shell has had time to settle.
                if (taskbar != _autoHideTaskbar || !_ops.GetAutoHide())
                {
                    _reapplyTimer.Start();
                }
            }
        }
        return 0;
    }

    private bool TryReconnectXaml()
    {
        var explorer = _ops.ReadTaskbarIdentity().ProcessId;
        if (explorer == 0 || explorer == _explorerProcessId) return false;
        _explorerProcessId = explorer;
        _ = ApplyAsync(TaskbarMode.Transparent);
        return true;
    }

    private void CheckAutoHideTakeover()
    {
        // No taskbar window (Explorer restarting) reads as "not hidden"; wait for the next change.
        if (!_ops.HasTaskbarWindow() || _ops.GetAutoHide())
        {
            return;
        }

        // The user switched auto-hide off in Windows; that choice now wins.
        lock (_systemWriteLock)
        {
            TryDelete(_checkpointPath);
        }
        StopReapplying();
        SetStatus(TaskbarMode.SystemDefault, string.Empty);
        AutoHideTakenOver?.Invoke();
    }

    private bool _reconcilingAutoHide;

    private async Task RestoreRuntimeAsync(string restoredMessage, string pendingMessage)
    {
        var version = Interlocked.Increment(ref _requestVersion);
        _applyCancellation?.Cancel();
        _reconcileCancellation?.Cancel();
        var entered = false;
        var restored = false;
        try
        {
            await _applyGate.WaitAsync();
            entered = true;
            if (_disposed || version != Volatile.Read(ref _requestVersion))
            {
                return;
            }
            restored = await RestoreActiveAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Debug.WriteLine($"任务栏异步恢复失败：{exception}");
        }
        finally
        {
            if (entered) _applyGate.Release();
        }
        if (_disposed || version != Volatile.Read(ref _requestVersion))
        {
            return;
        }
        SetStatus(TaskbarMode.SystemDefault, restored ? restoredMessage : pendingMessage);
    }

    internal void RunKeepAliveForTests(int? forceTickCount = null)
    {
        if (forceTickCount is { } tick)
        {
            _keepAliveTicks = tick;
        }
        Guard(KeepAlive);
    }

    internal Task WaitForPendingRollbackForTests() => _inFlightCompetitorRollback ?? Task.CompletedTask;

    internal Task ReconcileAutoHideForTestsAsync() => ReconcileAutoHideAsync();

    private void ReconcileAutoHide()
    {
        if (_disposed || _reconcilingAutoHide || ActiveMode != TaskbarMode.SmartHide)
        {
            return;
        }
        _ = ReconcileAutoHideAsync();
    }

    private async Task ReconcileAutoHideAsync()
    {
        _reconcilingAutoHide = true;
        var version = Volatile.Read(ref _requestVersion);
        _reconcileCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _reconcileCancellation = cancellation;
        var entered = false;
        try
        {
            await _applyGate.WaitAsync(cancellation.Token);
            entered = true;
            if (_disposed
                || cancellation.IsCancellationRequested
                || version != Volatile.Read(ref _requestVersion)
                || ActiveMode != TaskbarMode.SmartHide)
            {
                return;
            }

            var taskbar = _ops.ReadTaskbarIdentity();
            if (_autoHideRecoveryAttempts == 0)
            {
                if (taskbar.Window != 0 && taskbar == _autoHideTaskbar)
                {
                    CheckAutoHideTakeover();
                    return;
                }
                _autoHideRecoveryAttempts = AutoHideRecoveryAttemptCount;
            }
            var ownsAutoHide = _ownsAutoHide;
            var recovered = taskbar.Window != 0
                && await Task.Run(() =>
                {
                    lock (_systemWriteLock)
                    {
                        if (_disposed
                            || cancellation.IsCancellationRequested
                            || version != Volatile.Read(ref _requestVersion))
                        {
                            return false;
                        }
                        return _ops.GetAutoHide() || (ownsAutoHide && _ops.SetAutoHide(true));
                    }
                });
            if (_disposed
                || cancellation.IsCancellationRequested
                || version != Volatile.Read(ref _requestVersion)
                || ActiveMode != TaskbarMode.SmartHide)
            {
                return;
            }
            if (recovered)
            {
                _autoHideTaskbar = taskbar;
                _autoHideRecoveryAttempts = 0;
                return;
            }
            if (--_autoHideRecoveryAttempts > 0)
            {
                _reapplyTimer.Start();
                return;
            }

            if (!_ownsAutoHide && taskbar.Window != 0)
            {
                _autoHideTaskbar = taskbar;
                CheckAutoHideTakeover();
                return;
            }
            var restored = await RestoreActiveAsync();
            if (_disposed || version != Volatile.Read(ref _requestVersion))
            {
                return;
            }
            SetStatus(TaskbarMode.SystemDefault, restored
                ? "Explorer 重启后未能恢复任务栏自动隐藏，已恢复系统默认"
                : "Explorer 重启后未能恢复任务栏自动隐藏，下次启动时会再次核对");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Debug.WriteLine($"任务栏自动隐藏恢复异常：{exception}");
        }
        finally
        {
            if (entered) _applyGate.Release();
            if (ReferenceEquals(_reconcileCancellation, cancellation)) _reconcileCancellation = null;
            cancellation.Dispose();
            _reconcilingAutoHide = false;
        }
    }

    internal static (nint Window, uint ProcessId) ReadTaskbarIdentity()
    {
        var window = TaskbarNativeMethods.FindWindowW("Shell_TrayWnd", null);
        return window != 0 && TaskbarNativeMethods.GetWindowThreadProcessId(window, out var processId) != 0
            ? (window, processId) : default;
    }

    internal static bool IsProcessRunning(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return true;
        }
    }

    private void SetStatus(TaskbarMode mode, string problem)
    {
        ActiveMode = mode;
        Problem = problem;
        StatusChanged?.Invoke();
    }

    private void WriteCheckpoint(TaskbarCheckpoint checkpoint) => WriteCheckpoint(_checkpointPath, checkpoint);

    private static void WriteCheckpoint(string checkpointPath, TaskbarCheckpoint checkpoint)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(checkpointPath)!);
        var temporaryPath = checkpointPath + ".tmp";
        File.WriteAllText(temporaryPath, checkpoint.Serialize());
        File.Move(temporaryPath, checkpointPath, true);
    }

    private static TaskbarCheckpoint? ReadCheckpoint(string path)
    {
        try
        {
            return TaskbarCheckpoint.Parse(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryMove(string source, string destination)
    {
        try
        {
            File.Move(source, destination, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static bool HasTaskbarWindow() =>
        TaskbarNativeMethods.FindWindowW("Shell_TrayWnd", null) != 0;

    /// <summary>
    /// What Windows keeps for the next sign-in or Explorer restart. The live state can be
    /// off while this still says hidden; null when it cannot be read.
    /// </summary>
    internal static bool? IsAutoHideStored()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3");
            return key?.GetValue("Settings") is byte[] { Length: > 8 } settings
                ? (settings[8] & 1) != 0
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Whether Windows currently hides the taskbar automatically.</summary>
    internal static bool IsWindowsAutoHideOn() => GetAutoHide();

    /// <summary>Turns Windows' auto-hide off on the user's explicit request.</summary>
    internal static bool TryShowTaskbarAlways() => SetAutoHide(false);

    internal static bool GetAutoHide()
    {
        var data = new TaskbarNativeMethods.AppBarData
        {
            Size = (uint)Marshal.SizeOf<TaskbarNativeMethods.AppBarData>()
        };
        var state = TaskbarNativeMethods.SHAppBarMessage(TaskbarNativeMethods.AbmGetState, ref data);
        return ((int)state & TaskbarNativeMethods.AbsAutoHide) != 0;
    }

    // Explorer applies the state asynchronously, so a read right after the write can still
    // return the old value. Waiting for it (up to about half a second) keeps the next
    // GetAutoHide from mistaking our own pending change for the user's setting.
    internal static bool SetAutoHide(bool autoHide)
    {
        if (!HasTaskbarWindow()) return false;
        var data = new TaskbarNativeMethods.AppBarData
        {
            Size = (uint)Marshal.SizeOf<TaskbarNativeMethods.AppBarData>(),
            Window = TaskbarNativeMethods.FindWindowW("Shell_TrayWnd", null),
            Parameter = autoHide ? TaskbarNativeMethods.AbsAutoHide : 0
        };
        TaskbarNativeMethods.SHAppBarMessage(TaskbarNativeMethods.AbmSetState, ref data);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (GetAutoHide() == autoHide) return true;
            Thread.Sleep(50);
        }
        return GetAutoHide() == autoHide;
    }

    internal static bool TrySetAccent(IReadOnlyList<nint> taskbars, int accentState, out string error)
    {
        if (taskbars.Count == 0)
        {
            error = "没有找到任务栏";
            return false;
        }

        var policy = new TaskbarNativeMethods.AccentPolicy { State = accentState };
        var size = Marshal.SizeOf<TaskbarNativeMethods.AccentPolicy>();
        var pointer = Marshal.AllocHGlobal(size);
        var firstError = string.Empty;
        try
        {
            Marshal.StructureToPtr(policy, pointer, false);
            foreach (var taskbar in taskbars)
            {
                var data = new TaskbarNativeMethods.WindowCompositionAttributeData
                {
                    Attribute = TaskbarNativeMethods.WcaAccentPolicy,
                    Data = pointer,
                    SizeOfData = size
                };
                // A window that vanished must not keep the other monitors' taskbars from being set.
                if (!TaskbarNativeMethods.SetWindowCompositionAttribute(taskbar, ref data)
                    && firstError.Length == 0)
                {
                    firstError = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        error = firstError;
        return firstError.Length == 0;
    }

    internal static List<nint> FindTaskbars()
    {
        var taskbars = new List<nint>();
        var desktop = DockNativeMethods.GetDesktopWindow();
        foreach (var className in TaskbarClasses)
        {
            nint window = 0;
            while ((window = NativeMethods.FindWindowExW(desktop, window, className, null)) != 0)
            {
                if (IsExplorerWindow(window))
                {
                    taskbars.Add(window);
                }
            }
        }
        return taskbars;
    }

    private static bool IsExplorerWindow(nint window)
    {
        TaskbarNativeMethods.GetWindowThreadProcessId(window, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool IsSystemTransparencyEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("EnableTransparency") is not int value || value != 0;
    }

    /// <summary>
    /// Writes the user-level transparency effects — the same value the personalization
    /// page writes, so no elevation is needed — and broadcasts the change so it takes
    /// effect without signing out. False when Windows did not accept the value.
    /// </summary>
    internal static bool TryWriteSystemTransparency(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: true);
            key?.SetValue("EnableTransparency", enabled ? 1 : 0, RegistryValueKind.DWord);
            TaskbarNativeMethods.SendMessageTimeoutString(
                TaskbarNativeMethods.HwndBroadcast,
                (uint)TaskbarNativeMethods.WmSettingChange,
                0,
                "ImmersiveColorSet",
                TaskbarNativeMethods.SmtoAbortIfHung,
                2000,
                out _);
            return IsSystemTransparencyEnabled() == enabled;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            Debug.WriteLine($"写入系统透明效果失败：{exception.Message}");
            return false;
        }
    }
}

internal sealed class TaskbarPlatformOps
{
    internal delegate bool SetAccentDelegate(IReadOnlyList<nint> taskbars, int accentState, out string error);

    internal static TaskbarPlatformOps Live { get; } = new();

    internal Func<bool> GetAutoHide { get; init; } = TaskbarAdapter.GetAutoHide;
    internal Func<bool, bool> SetAutoHide { get; init; } = TaskbarAdapter.SetAutoHide;
    internal Func<bool> HasTaskbarWindow { get; init; } = TaskbarAdapter.HasTaskbarWindow;
    internal Func<(nint Window, uint ProcessId)> ReadTaskbarIdentity { get; init; } = TaskbarAdapter.ReadTaskbarIdentity;
    internal Func<uint, bool> IsProcessRunning { get; init; } = TaskbarAdapter.IsProcessRunning;
    internal Func<bool> IsSystemTransparencyEnabled { get; init; } = TaskbarAdapter.IsSystemTransparencyEnabled;
    internal Func<bool, bool> TryWriteSystemTransparency { get; init; } = TaskbarAdapter.TryWriteSystemTransparency;
    internal Func<bool, TaskbarEnvironment> ReadEnvironment { get; init; } = TaskbarAdapter.ReadEnvironment;
    internal Func<List<nint>> FindTaskbars { get; init; } = TaskbarAdapter.FindTaskbars;
    internal SetAccentDelegate TrySetAccent { get; init; } = TaskbarAdapter.TrySetAccent;
}
