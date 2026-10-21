using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using BingLan.App.Interop;
using BingLan.Core.Desktop;

namespace BingLan.App.Services;

/// <summary>
/// Hides the native desktop icons for clean desktop and puts them back. It records the
/// original state in a checkpoint before the first write, restores on turn-off, on exit,
/// on the next start after a crash and from the uninstaller, and changes nothing but the
/// icon visibility flag: no files, icon positions or Explorer processes are touched.
/// A hide is only claimed with its checkpoint behind it, and a turn-off never ends in a
/// reported success with the icons still hidden. All Shell calls run one at a time off
/// the UI thread.
/// </summary>
internal sealed class CleanDesktopAdapter : IDisposable
{
    // Each check asks Explorer for its desktop view, so it runs rarely.
    private static readonly TimeSpan TakeoverCheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitRestoreTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MachineLockTimeout = TimeSpan.FromSeconds(5);
    private static readonly SemaphoreSlim ShellGate = new(1, 1);

    // Shared with the --restore-taskbar process, so an uninstall or update restore never
    // writes the desktop view or the checkpoint at the same time as a running instance.
    private const string MachineLockName = @"Local\BingLan.DesktopIcons";

    private readonly string _checkpointPath;
    private readonly DispatcherTimer _takeoverTimer;
    private bool _checking;
    private bool _disposed;

    internal CleanDesktopAdapter(string checkpointPath)
    {
        _checkpointPath = checkpointPath;
        _takeoverTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TakeoverCheckInterval };
        _takeoverTimer.Tick += async (_, _) => await CheckTakeoverAsync();
    }

    /// <summary>Whether clean desktop currently keeps the icons hidden.</summary>
    internal bool IsActive { get; private set; }

    /// <summary>The icons were shown again by the user or another tool while active.</summary>
    internal event Action? TakenOver;

    /// <summary>
    /// Hides the icons. Returns an empty string on success, otherwise why not. Icons
    /// that are already hidden are never taken over: without a record of the hide,
    /// turning clean desktop off could not bring them back.
    /// </summary>
    internal async Task<string> EnableAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var problem = await RunShellAsync(
            () => WithMachineLock(() => Hide(_checkpointPath), "桌面图标正由另一个冰蓝桌面进程处理，请稍后再试"));
        IsActive = problem.Length == 0 && !_disposed;
        if (IsActive)
        {
            _takeoverTimer.Start();
        }
        return problem;
    }

    /// <summary>Puts the icons back as they were. Returns false when that could not be verified.</summary>
    internal async Task<bool> DisableAsync()
    {
        _takeoverTimer.Stop();
        IsActive = false;
        return await RunShellAsync(() => WithMachineLock(() => ShowIconsForTurnOff(_checkpointPath), false));
    }

    /// <summary>
    /// Restores the icon state left by a previous run, serialised with other Shell calls.
    /// Safe to repeat: with no checkpoint it changes nothing. An unreadable checkpoint is
    /// kept aside and the desktop is left as it is, since the original state is unknown.
    /// </summary>
    internal static bool RecoverNow(string checkpointPath)
    {
        ShellGate.Wait();
        try
        {
            return WithMachineLock(() => RecoverFromCheckpoint(checkpointPath), false);
        }
        finally
        {
            ShellGate.Release();
        }
    }

    private static T WithMachineLock<T>(Func<T> work, T busyResult)
    {
        using var machineLock = new Mutex(false, MachineLockName);
        bool acquired;
        try
        {
            acquired = machineLock.WaitOne(MachineLockTimeout);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            return busyResult;
        }

        try
        {
            return work();
        }
        finally
        {
            machineLock.ReleaseMutex();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _takeoverTimer.Stop();
        IsActive = false;
        // Exit must not hang on a busy Explorer; a restore that does not finish keeps
        // its checkpoint and runs again on the next start.
        try
        {
            var restore = Task.Run(() => RecoverNow(_checkpointPath));
            if (!restore.Wait(ExitRestoreTimeout))
            {
                Debug.WriteLine("退出时桌面图标恢复超时");
            }
        }
        catch (AggregateException exception)
        {
            Debug.WriteLine($"退出时桌面图标恢复失败：{exception.InnerException?.Message}");
        }
    }

    /// <summary>
    /// Whether the icon state from before clean desktop is known. A checkpoint that was
    /// set aside as unreadable means it is not, until the icons are seen visible again.
    /// </summary>
    internal static bool IsOriginalStateKnown(string checkpointPath) =>
        !File.Exists(InvalidCheckpointPath(checkpointPath));

    private static string InvalidCheckpointPath(string checkpointPath) => checkpointPath + ".invalid";

    private static bool RecoverFromCheckpoint(string checkpointPath)
    {
        CleanDesktopCheckpoint? checkpoint;
        try
        {
            // Read directly: only a file that is really absent means there is nothing
            // to restore; any other read failure is reported as a failed restore.
            checkpoint = CleanDesktopCheckpoint.Parse(File.ReadAllText(checkpointPath));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StartupLog.Write("桌面图标检查点读取失败，未改动桌面");
            return false;
        }

        if (checkpoint is null)
        {
            TryMove(checkpointPath, InvalidCheckpointPath(checkpointPath));
            StartupLog.Write("桌面图标检查点损坏，已移旁，未改动桌面");
            return false;
        }

        try
        {
            checkpoint.Phase = CleanDesktopPhase.Restoring;
            WriteCheckpoint(checkpointPath, checkpoint);
            using var view = DesktopShellView.Open();
            if (CleanDesktopRules.NeedsRestore(checkpoint, view.ReadFolderFlags())
                && !view.SetIconsHidden(checkpoint.OriginalIconsHidden))
            {
                StartupLog.Write("桌面图标未能按检查点恢复，下次启动会再次尝试");
                return false;
            }
            File.Delete(checkpointPath);
            StartupLog.Write("已按检查点恢复桌面图标");
            return true;
        }
        catch (Exception exception) when (IsShellFailure(exception))
        {
            Debug.WriteLine($"桌面图标恢复失败：{exception.Message}");
            StartupLog.Write($"桌面图标恢复失败：{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Shows the icons again for an explicit turn-off. The checkpoint is the record of
    /// the state to return to; when it is missing or unusable while the icons are still
    /// hidden, the mode was holding them without its record and they are shown anyway,
    /// instead of reporting a success that changed nothing.
    /// </summary>
    private static bool ShowIconsForTurnOff(string checkpointPath)
    {
        try
        {
            if (File.Exists(checkpointPath) && RecoverFromCheckpoint(checkpointPath))
            {
                return true;
            }

            using var view = DesktopShellView.Open();
            if (!CleanDesktopRules.TurnOffMustShowIcons(false, view.ReadFolderFlags()))
            {
                return true;
            }
            if (view.SetIconsHidden(false))
            {
                StartupLog.Write("桌面图标检查点缺失，关闭清爽桌面时已直接恢复显示");
                return true;
            }
            StartupLog.Write("关闭清爽桌面时未能恢复显示：Windows 未接受写入");
            return false;
        }
        catch (Exception exception) when (IsShellFailure(exception))
        {
            Debug.WriteLine($"关闭清爽桌面恢复失败：{exception.Message}");
            StartupLog.Write($"关闭清爽桌面恢复失败：{exception.Message}");
            return false;
        }
    }

    private static string Hide(string checkpointPath)
    {
        try
        {
            using var view = DesktopShellView.Open();
            if (!CleanDesktopRules.NeedsHide(view.ReadFolderFlags()))
            {
                // Hidden outside this record, whether by the user or after the record
                // was lost: turning clean desktop off could not bring the icons back,
                // so the mode refuses instead of claiming a hide it cannot undo.
                StartupLog.Write("开启清爽桌面被拒：桌面图标当前处于隐藏状态");
                return "桌面图标当前处于隐藏状态。请先在桌面右键菜单“查看”中勾选“显示桌面图标”，再开启清爽桌面";
            }

            var checkpoint = new CleanDesktopCheckpoint { OriginalIconsHidden = false };
            WriteCheckpoint(checkpointPath, checkpoint);
            // The icons were visible just now, so the original state is known again.
            TryDelete(InvalidCheckpointPath(checkpointPath));
            if (view.SetIconsHidden(true))
            {
                checkpoint.Phase = CleanDesktopPhase.Applied;
                WriteCheckpoint(checkpointPath, checkpoint);
                StartupLog.Write("清爽桌面已隐藏桌面图标");
                return string.Empty;
            }

            return RecoverFromCheckpoint(checkpointPath)
                ? "Windows 没有接受隐藏桌面图标"
                : "Windows 没有接受隐藏桌面图标，恢复未完成，下次启动时会再次尝试";
        }
        catch (Exception exception) when (IsShellFailure(exception))
        {
            Debug.WriteLine($"清爽桌面开启失败：{exception.Message}");
            StartupLog.Write($"清爽桌面开启失败：{exception.Message}");
            RecoverFromCheckpoint(checkpointPath);
            return "当前桌面不支持清爽模式";
        }
    }

    private async Task CheckTakeoverAsync()
    {
        if (_checking || !IsActive)
        {
            return;
        }

        _checking = true;
        try
        {
            var iconsShowing = await RunShellAsync(() =>
            {
                try
                {
                    using var view = DesktopShellView.Open();
                    return CleanDesktopRules.IsTakenOver(true, view.ReadFolderFlags());
                }
                catch (Exception exception) when (IsShellFailure(exception))
                {
                    // Explorer may be restarting; the hidden state survives that, so wait.
                    return false;
                }
            });
            if (!iconsShowing || !IsActive)
            {
                return;
            }

            // The icons already show, which is the original state; drop the records.
            _takeoverTimer.Stop();
            IsActive = false;
            await RunShellAsync(() => WithMachineLock(() =>
            {
                TryDelete(_checkpointPath);
                TryDelete(InvalidCheckpointPath(_checkpointPath));
                return true;
            }, false));
            StartupLog.Write("用户重新显示了桌面图标，清爽桌面已退出");
            TakenOver?.Invoke();
        }
        finally
        {
            _checking = false;
        }
    }

    private static async Task<T> RunShellAsync<T>(Func<T> work)
    {
        await ShellGate.WaitAsync();
        try
        {
            return await Task.Run(work);
        }
        finally
        {
            ShellGate.Release();
        }
    }

    private static bool IsShellFailure(Exception exception) =>
        exception is COMException
            or InvalidCastException
            or UnauthorizedAccessException
            or IOException;

    private static void WriteCheckpoint(string path, CleanDesktopCheckpoint checkpoint)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, checkpoint.Serialize());
        File.Move(temporaryPath, path, true);
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
}
