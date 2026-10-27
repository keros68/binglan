using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
/// (next start) and on request. Transparent mode leases only Explorer's XAML background.
/// It only changes the taskbar while no other taskbar tool runs, so the state it found is the system
/// default, which is what a restore puts back.
/// </summary>
internal sealed class TaskbarAdapter : IDisposable
{
    private const int WsPopup = unchecked((int)0x80000000);
    private static readonly TimeSpan ReapplyDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumEventReapplyInterval = TimeSpan.FromMilliseconds(150);
    private const int CompetitorCheckTicks = 10;
    private static readonly string[] CompetingCustomizers =
        ["TranslucentTB", "TaskbarX", "RoundedTB", "StartAllBack"];
    private static readonly HashSet<string> TaskbarClasses =
        ["Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    private readonly string _checkpointPath;
    private readonly TaskbarFailureBreaker _breaker = new();
    private readonly DispatcherTimer _reapplyTimer;
    private readonly DispatcherTimer _keepAliveTimer;
    private readonly TaskbarNativeMethods.WinEventProc _winEventCallback;
    private readonly List<nint> _winEventHooks = [];
    private List<nint> _taskbars = [];
    private readonly uint _taskbarCreatedMessage;
    private HwndSource? _messageWindow;
    private DateTime _lastEventReapply;
    private int _keepAliveTicks;
    private bool _disposed;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private CancellationTokenSource? _applyCancellation;
    private TaskbarXamlSession? _xamlSession;
    private int _requestVersion;
    private uint _explorerProcessId;

    internal TaskbarAdapter(string checkpointPath)
    {
        _checkpointPath = checkpointPath;
        _taskbarCreatedMessage = TaskbarNativeMethods.RegisterWindowMessageW("TaskbarCreated");
        _reapplyTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ReapplyDelay };
        _reapplyTimer.Tick += (_, _) =>
        {
            _reapplyTimer.Stop();
            Guard(Reapply);
        };
        _keepAliveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = KeepAliveInterval };
        _keepAliveTimer.Tick += (_, _) => Guard(KeepAlive);
        _winEventCallback = OnWinEvent;
    }

    /// <summary>The mode currently held on the taskbar.</summary>
    internal TaskbarMode ActiveMode { get; private set; } = TaskbarMode.SystemDefault;

    /// <summary>Why the requested mode is not active, or empty.</summary>
    internal string Problem { get; private set; } = string.Empty;

    internal event Action? StatusChanged;

    /// <summary>The user turned auto-hide off in Windows while this app held it.</summary>
    internal event Action? AutoHideTakenOver;

    // Looking for other taskbar tools lists processes, so the keep-alive tick leaves it to
    // its less frequent competitor check.
    internal static TaskbarEnvironment ReadEnvironment(bool lookForCompetitors = true) => new(
        Environment.OSVersion.Version.Build,
        SystemParameters.HighContrast,
        TaskbarNativeMethods.GetSystemMetrics(TaskbarNativeMethods.SmRemoteSession) != 0,
        IsSystemTransparencyEnabled(),
        lookForCompetitors ? CompetingCustomizers.FirstOrDefault(IsRunning) : null);

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
    internal static bool RecoverFromCheckpoint(string checkpointPath)
    {
        if (!File.Exists(checkpointPath))
        {
            return true;
        }

        TaskbarCheckpoint? checkpoint;
        try
        {
            checkpoint = TaskbarCheckpoint.Parse(File.ReadAllText(checkpointPath));
        }
        catch (IOException)
        {
            return false;
        }

        if (checkpoint is null)
        {
            // Unreadable or from a newer version: undo the one change that is always
            // safe to undo and keep the file aside instead of pretending success.
            // The file is only set aside once that undo worked, so a failed attempt is
            // retried on the next start.
            if (TrySetAccent(FindTaskbars(), TaskbarNativeMethods.AccentDisabled, out _))
            {
                TryMove(checkpointPath, checkpointPath + ".invalid");
            }
            return false;
        }

        var restored = Restore(checkpoint);
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
        var version = ++_requestVersion;
        _applyCancellation?.Cancel();
        _applyCancellation = new CancellationTokenSource();
        var cancellation = _applyCancellation;
        _xamlSession?.RequestStop();
        var entered = false;
        try
        {
            await _applyGate.WaitAsync(cancellation.Token);
            entered = true;
            await ApplyCoreAsync(mode, cancellation.Token);
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
            if (!_disposed && version == _requestVersion)
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

    private async Task ApplyCoreAsync(TaskbarMode mode, CancellationToken cancellationToken)
    {
        _breaker.Reset();
        if (!await RestoreActiveAsync())
        {
            SetStatus(TaskbarMode.SystemDefault, "未能恢复任务栏原来的状态，暂不应用新的设置");
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed) return;
        SetStatus(TaskbarMode.SystemDefault, string.Empty);

        var compatibility = TaskbarCompatibility.Assess(mode, ReadEnvironment());
        if (!compatibility.IsSupported)
        {
            SetStatus(TaskbarMode.SystemDefault, compatibility.Reason);
            return;
        }

        switch (mode)
        {
            case TaskbarMode.Transparent:
                _xamlSession = new TaskbarXamlSession(Path.GetDirectoryName(_checkpointPath)!);
                WriteCheckpoint(new TaskbarCheckpoint { XamlSessionName = _xamlSession.MappingName });
                await _xamlSession.StartAsync(FindTaskbars().Count, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureMessageWindow();
                TaskbarNativeMethods.GetWindowThreadProcessId(
                    TaskbarNativeMethods.FindWindowW("Shell_TrayWnd", null), out _explorerProcessId);
                _keepAliveTimer.Start();
                SetStatus(mode, string.Empty);
                break;
            case TaskbarMode.Blur:
                ApplyBlur();
                break;
            case TaskbarMode.SmartHide:
                ApplyAutoHide();
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
        _requestVersion++;
        _applyCancellation?.Cancel();
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

    private bool HoldsAccent => ActiveMode == TaskbarMode.Blur;

    private void ApplyBlur()
    {
        WriteCheckpoint(new TaskbarCheckpoint { TransparencyApplied = true });
        _taskbars = FindTaskbars();
        if (TrySetAccent(_taskbars, TaskbarNativeMethods.AccentEnableBlurBehind, out var error))
        {
            EnsureMessageWindow();
            StartKeepingTransparent();
            SetStatus(TaskbarMode.Blur, string.Empty);
            return;
        }

        // Keep the checkpoint when the rollback itself fails, so the next start retries.
        if (TrySetAccent(_taskbars, TaskbarNativeMethods.AccentDisabled, out _))
        {
            TryDelete(_checkpointPath);
            SetStatus(TaskbarMode.SystemDefault, $"无法设置任务栏外观：{error}");
            return;
        }
        SetStatus(TaskbarMode.SystemDefault, $"无法设置任务栏外观，也未能完全恢复：{error}。下次启动时会再次恢复");
    }

    private void ApplyAutoHide()
    {
        if (GetAutoHide())
        {
            // Already hidden by the user; nothing of ours to restore later.
            SetStatus(TaskbarMode.SmartHide, string.Empty);
            return;
        }

        WriteCheckpoint(new TaskbarCheckpoint { AutoHideApplied = true, OriginalAutoHide = false });
        SetAutoHide(true);
        if (GetAutoHide())
        {
            EnsureMessageWindow();
            SetStatus(TaskbarMode.SmartHide, string.Empty);
            return;
        }

        TryDelete(_checkpointPath);
        SetStatus(TaskbarMode.SystemDefault, "Windows 没有接受任务栏自动隐藏设置");
    }

    private bool RestoreActive()
    {
        StopKeepingTransparent();
        _xamlSession?.Dispose();
        _xamlSession = null;
        ActiveMode = TaskbarMode.SystemDefault;
        return RecoverFromCheckpoint(_checkpointPath);
    }

    private async Task<bool> RestoreActiveAsync()
    {
        StopKeepingTransparent();
        _xamlSession?.Dispose();
        _xamlSession = null;
        ActiveMode = TaskbarMode.SystemDefault;
        return await Task.Run(() => RecoverFromCheckpoint(_checkpointPath));
    }

    private static bool Restore(TaskbarCheckpoint checkpoint)
    {
        var plan = TaskbarRecovery.Plan(checkpoint, GetAutoHide());
        var restored = true;
        if (checkpoint.XamlSessionName is { } name)
            restored &= TaskbarXamlSession.Restore(name);
        if (plan.ResetTransparency)
        {
            restored &= TrySetAccent(FindTaskbars(), TaskbarNativeMethods.AccentDisabled, out _);
        }
        if (plan.SetAutoHide is { } autoHide)
        {
            SetAutoHide(autoHide);
            restored &= GetAutoHide() == autoHide;
        }
        return restored;
    }

    // Explorer puts its own material back when the Start menu or another shell surface
    // opens, and recreates the taskbar on restart, display or theme changes. There is no
    // API to read the current material, so transparency is re-applied after such events
    // and on a slow keep-alive tick.
    private void StartKeepingTransparent()
    {
        if (_winEventHooks.Count == 0)
        {
            Hook(TaskbarNativeMethods.EventSystemForeground, TaskbarNativeMethods.EventSystemForeground);
            Hook(TaskbarNativeMethods.EventObjectShow, TaskbarNativeMethods.EventObjectHide);
            Hook(TaskbarNativeMethods.EventObjectCloaked, TaskbarNativeMethods.EventObjectUncloaked);
        }
        _keepAliveTimer.Start();
    }

    private void StopKeepingTransparent()
    {
        _reapplyTimer.Stop();
        _keepAliveTimer.Stop();
        foreach (var hook in _winEventHooks)
        {
            TaskbarNativeMethods.UnhookWinEvent(hook);
        }
        _winEventHooks.Clear();
    }

    private void Hook(uint eventMin, uint eventMax)
    {
        var hook = TaskbarNativeMethods.SetWinEventHook(
            eventMin,
            eventMax,
            0,
            _winEventCallback,
            0,
            0,
            TaskbarNativeMethods.WineventOutOfContext | TaskbarNativeMethods.WineventSkipOwnProcess);
        if (hook != 0)
        {
            _winEventHooks.Add(hook);
        }
    }

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        Services.UiDiagnostics.Hit(Services.UiActivity.TaskbarEvent);
        if (objectId != TaskbarNativeMethods.ObjidWindow || !HoldsAccent)
        {
            return;
        }

        // Once now (at most every 150 ms while events stream in) and once after
        // Explorer has finished its own transition.
        var now = DateTime.UtcNow;
        if (now - _lastEventReapply >= MinimumEventReapplyInterval)
        {
            _lastEventReapply = now;
            Guard(Reapply);
        }
        _reapplyTimer.Stop();
        _reapplyTimer.Start();
    }

    private void KeepAlive()
    {
        if (++_keepAliveTicks % CompetitorCheckTicks == 0
            && ReadEnvironment().CompetingCustomizer is { } customizer)
        {
            // Another tool now owns the taskbar; stop without resetting its appearance.
            StopKeepingTransparent();
            var usedXaml = _xamlSession is not null;
            _xamlSession?.Dispose();
            _xamlSession = null;
            if (!usedXaml) TryDelete(_checkpointPath);
            SetStatus(TaskbarMode.SystemDefault, $"{customizer} 正在调整任务栏，冰蓝桌面已停止调整");
            return;
        }

        if (ActiveMode == TaskbarMode.Transparent)
        {
            var environment = ReadEnvironment(lookForCompetitors: false);
            var compatibility = TaskbarCompatibility.Assess(TaskbarMode.Transparent, environment);
            if (!compatibility.IsSupported)
            {
                StopKeepingTransparent();
                _xamlSession?.Dispose();
                _xamlSession = null;
                SetStatus(TaskbarMode.SystemDefault, compatibility.Reason);
                return;
            }
            TaskbarNativeMethods.GetWindowThreadProcessId(
                TaskbarNativeMethods.FindWindowW("Shell_TrayWnd", null), out var explorer);
            if (explorer != 0 && explorer != _explorerProcessId)
            {
                _ = ApplyAsync(TaskbarMode.Transparent);
                return;
            }
            if (_xamlSession?.IsActive != true && _breaker.RecordFailure())
            {
                StopKeepingTransparent();
                _xamlSession?.Dispose();
                _xamlSession = null;
                SetStatus(TaskbarMode.SystemDefault, "任务栏背景不可用，已停止调整；下次启动时会核对恢复状态");
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
        if (_disposed || !HoldsAccent)
        {
            return;
        }

        if (TrySetAccent(_taskbars, TaskbarNativeMethods.AccentEnableBlurBehind, out _))
        {
            _breaker.RecordSuccess();
            return;
        }

        _taskbars = FindTaskbars();
        if (TrySetAccent(_taskbars, TaskbarNativeMethods.AccentEnableBlurBehind, out var error))
        {
            _breaker.RecordSuccess();
            return;
        }

        if (_breaker.RecordFailure())
        {
            var restored = RestoreActive();
            SetStatus(
                TaskbarMode.SystemDefault,
                restored
                    ? $"任务栏外观多次未能保持，已恢复系统默认：{error}"
                    : $"任务栏外观多次未能保持，下次启动时恢复：{error}");
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
        if ((uint)message == _taskbarCreatedMessage
            || message is TaskbarNativeMethods.WmSettingChange
                or TaskbarNativeMethods.WmThemeChanged
                or TaskbarNativeMethods.WmDwmColorizationColorChanged
                or TaskbarNativeMethods.WmDisplayChange)
        {
            if (ActiveMode == TaskbarMode.Transparent && (uint)message == _taskbarCreatedMessage)
            {
                _ = ApplyAsync(TaskbarMode.Transparent);
            }
            else if (HoldsAccent)
            {
                _taskbars = [];
                _reapplyTimer.Stop();
                _reapplyTimer.Start();
            }
            else if (ActiveMode == TaskbarMode.SmartHide && message == TaskbarNativeMethods.WmSettingChange)
            {
                Guard(CheckAutoHideTakeover);
            }
        }
        return 0;
    }

    private void CheckAutoHideTakeover()
    {
        if (GetAutoHide())
        {
            return;
        }

        // The user switched auto-hide off in Windows; that choice now wins.
        TryDelete(_checkpointPath);
        SetStatus(TaskbarMode.SystemDefault, string.Empty);
        AutoHideTakenOver?.Invoke();
    }

    private void SetStatus(TaskbarMode mode, string problem)
    {
        ActiveMode = mode;
        Problem = problem;
        StatusChanged?.Invoke();
    }

    private void WriteCheckpoint(TaskbarCheckpoint checkpoint)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_checkpointPath)!);
        var temporaryPath = _checkpointPath + ".tmp";
        File.WriteAllText(temporaryPath, checkpoint.Serialize());
        File.Move(temporaryPath, _checkpointPath, true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static void TryMove(string source, string destination)
    {
        try
        {
            File.Move(source, destination, true);
        }
        catch (IOException)
        {
        }
    }

    private static bool GetAutoHide()
    {
        var data = new TaskbarNativeMethods.AppBarData
        {
            Size = (uint)Marshal.SizeOf<TaskbarNativeMethods.AppBarData>()
        };
        var state = TaskbarNativeMethods.SHAppBarMessage(TaskbarNativeMethods.AbmGetState, ref data);
        return ((int)state & TaskbarNativeMethods.AbsAutoHide) != 0;
    }

    private static void SetAutoHide(bool autoHide)
    {
        var data = new TaskbarNativeMethods.AppBarData
        {
            Size = (uint)Marshal.SizeOf<TaskbarNativeMethods.AppBarData>(),
            Window = TaskbarNativeMethods.FindWindowW("Shell_TrayWnd", null),
            Parameter = autoHide ? TaskbarNativeMethods.AbsAutoHide : 0
        };
        TaskbarNativeMethods.SHAppBarMessage(TaskbarNativeMethods.AbmSetState, ref data);
    }

    private static bool TrySetAccent(IReadOnlyList<nint> taskbars, int accentState, out string error)
    {
        if (taskbars.Count == 0)
        {
            error = "没有找到任务栏";
            return false;
        }

        var policy = new TaskbarNativeMethods.AccentPolicy { State = accentState };
        var size = Marshal.SizeOf<TaskbarNativeMethods.AccentPolicy>();
        var pointer = Marshal.AllocHGlobal(size);
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
                if (!TaskbarNativeMethods.SetWindowCompositionAttribute(taskbar, ref data))
                {
                    error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    return false;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        error = string.Empty;
        return true;
    }

    private static List<nint> FindTaskbars()
    {
        var taskbars = new List<nint>();
        TaskbarNativeMethods.EnumWindows((window, _) =>
        {
            var className = new StringBuilder(64);
            if (TaskbarNativeMethods.GetClassNameW(window, className, className.Capacity) > 0
                && TaskbarClasses.Contains(className.ToString())
                && IsExplorerWindow(window))
            {
                taskbars.Add(window);
            }
            return true;
        }, 0);
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
}
