using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using BingLan.App.Interop;
using BingLan.Core.Dock;
using BingLan.Core.Models;

namespace BingLan.App.Dock;

internal sealed record WindowCommandResult(bool Succeeded, string Message);

internal static class WindowCommandService
{
    internal static WindowCommandResult Toggle(WindowGroup group)
    {
        if (group.Windows.Count == 0)
        {
            return new WindowCommandResult(false, "窗口组为空");
        }

        var foreground = DockNativeMethods.GetForegroundWindow();
        var foregroundInGroup = group.Windows.FirstOrDefault(window => window.Handle == foreground);
        var target = group.Windows.FirstOrDefault(
                window => !DockNativeMethods.IsIconic(window.Handle))
            ?? group.Windows[0];
        var action = WindowActionPolicy.Decide(
            foregroundInGroup is not null,
            DockNativeMethods.IsIconic(target.Handle));
        if (action == DockClickAction.MinimizeForeground)
        {
            DockNativeMethods.ShowWindowAsync(foregroundInGroup!.Handle, DockNativeMethods.SwMinimize);
            return new WindowCommandResult(true, $"已最小化 {group.DisplayName}");
        }

        return Focus(target, action == DockClickAction.RestoreAndActivate, group.DisplayName);
    }

    internal static WindowCommandResult Focus(TrackedWindow window) =>
        Focus(window, DockNativeMethods.IsIconic(window.Handle), window.Title);

    /// <summary>
    /// Click path for a pinned app: an app closed to the tray still runs without
    /// visible windows, and revealing its hidden window beats starting a second
    /// instance that would land on a login screen again.
    /// </summary>
    internal static async Task<WindowCommandResult> LaunchAsync(DockPinnedApp app)
    {
        if (TrayAppRules.ShouldRevealInsteadOfLaunch(app))
        {
            var hidden = await Task.Run(() => FindHiddenWindow(app));
            if (hidden is not null)
            {
                var name = string.IsNullOrWhiteSpace(hidden.Title) ? app.DisplayName : hidden.Title.Trim();
                var window = new TrackedWindow(
                    hidden.Handle,
                    0,
                    name,
                    null,
                    app.ExecutablePath,
                    false,
                    hidden.IsMinimized);
                return Focus(window, restore: true, name);
            }
        }

        return Launch(app);
    }

    internal static WindowCommandResult Launch(DockPinnedApp app)
    {
        if (app.AppUserModelId is not null)
        {
            var result = Start(DockAppResolver.AppsFolderPath(app.AppUserModelId), app.DisplayName);
            if (result.Succeeded || app.ExecutablePath is null)
            {
                return result;
            }
        }

        if (string.IsNullOrWhiteSpace(app.ExecutablePath) || !File.Exists(app.ExecutablePath))
        {
            return new WindowCommandResult(false, $"找不到 {app.DisplayName} 的程序文件");
        }

        // Start in the folder the shortcut named, like Explorer does; if that folder is
        // gone (for example after an update), the program's own folder is the fallback.
        var workingDirectory = app.WorkingDirectory is { } folder && Directory.Exists(folder)
            ? folder
            : Path.GetDirectoryName(app.ExecutablePath);
        return Start(app.ExecutablePath, app.DisplayName, app.Arguments, workingDirectory);
    }

    internal static WindowCommandResult Close(IEnumerable<TrackedWindow> windows)
    {
        var count = 0;
        foreach (var window in windows)
        {
            if (DockNativeMethods.PostMessageW(
                    window.Handle,
                    DockNativeMethods.WmSysCommand,
                    DockNativeMethods.ScClose,
                    0))
            {
                count++;
            }
        }
        return new WindowCommandResult(count > 0, $"已请求关闭 {count} 个窗口");
    }

    internal static WindowCommandResult OpenFileLocation(string executablePath)
    {
        if (!File.Exists(executablePath) && !Directory.Exists(executablePath))
        {
            return new WindowCommandResult(false, "文件或文件夹已不存在");
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                ArgumentList = { $"/select,{executablePath}" },
                UseShellExecute = false
            });
            return new WindowCommandResult(true, "已打开文件位置");
        }
        catch (Win32Exception exception)
        {
            return new WindowCommandResult(false, $"无法打开文件位置：{exception.Message}");
        }
    }

    private static WindowCommandResult Focus(TrackedWindow target, bool restore, string name)
    {
        if (restore)
        {
            DockNativeMethods.ShowWindowAsync(target.Handle, DockNativeMethods.SwRestore);
        }

        if (DockNativeMethods.SetForegroundWindow(target.Handle))
        {
            return new WindowCommandResult(true, $"已切换到 {name}");
        }

        var info = new DockNativeMethods.FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<DockNativeMethods.FlashWindowInfo>(),
            Window = target.Handle,
            Flags = 2,
            Count = 3,
            Timeout = 0
        };
        DockNativeMethods.FlashWindowEx(ref info);
        return new WindowCommandResult(false, $"Windows 阻止了切换到 {name}，已闪烁提醒");
    }

    private static WindowCommandResult Start(
        string target,
        string displayName,
        string? arguments = null,
        string? workingDirectory = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                Arguments = arguments ?? string.Empty,
                WorkingDirectory = workingDirectory ?? string.Empty
            })?.Dispose();
            return new WindowCommandResult(true, $"已启动 {displayName}");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return new WindowCommandResult(false, $"无法启动 {displayName}：{exception.Message}");
        }
    }

    private static TrayWindowSnapshot? FindHiddenWindow(DockPinnedApp app)
    {
        var candidates = new List<TrayWindowSnapshot>();
        var processMatches = new Dictionary<uint, bool>();
        DockNativeMethods.EnumWindows((window, _) =>
        {
            DockNativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (processId == 0)
            {
                return true;
            }

            if (!processMatches.TryGetValue(processId, out var matches))
            {
                matches = TrayAppRules.MatchesProcess(app, TryGetProcessExecutablePath(processId));
                processMatches.Add(processId, matches);
            }
            if (matches)
            {
                candidates.Add(DescribeWindow(window));
            }

            return true;
        }, 0);
        return TrayAppRules.PickRevealTarget(candidates);
    }

    private static TrayWindowSnapshot DescribeWindow(nint window)
    {
        var extendedStyle = NativeMethods.GetExStyleOf(window);
        long area = 0;
        if (DockNativeMethods.GetWindowRect(window, out var rectangle)
            && rectangle.Right > rectangle.Left
            && rectangle.Bottom > rectangle.Top)
        {
            area = (long)(rectangle.Right - rectangle.Left) * (rectangle.Bottom - rectangle.Top);
        }

        return new TrayWindowSnapshot(
            window,
            GetWindowTitle(window),
            (extendedStyle & NativeMethods.WsExAppWindow) != 0,
            (extendedStyle & NativeMethods.WsExToolWindow) != 0,
            DockNativeMethods.IsWindowVisible(window),
            DockNativeMethods.IsIconic(window),
            area);
    }

    private static string GetWindowTitle(nint window)
    {
        var length = DockNativeMethods.GetWindowTextLengthW(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        var title = new StringBuilder(length + 1);
        DockNativeMethods.GetWindowTextW(window, title, title.Capacity);
        return title.ToString().Trim();
    }

    private static string? TryGetProcessExecutablePath(uint processId)
    {
        var process = DockNativeMethods.OpenProcess(
            DockNativeMethods.ProcessQueryLimitedInformation,
            false,
            processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(32768);
            var size = (uint)buffer.Capacity;
            return DockNativeMethods.QueryFullProcessImageNameW(process, 0, buffer, ref size)
                ? buffer.ToString()
                : null;
        }
        finally
        {
            DockNativeMethods.CloseHandle(process);
        }
    }
}
