using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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

    // A pinned app closed to the tray still runs with its windows hidden, but those
    // windows must NOT be shown from outside: the app still believes they are hidden,
    // so forcing SW_SHOW yields a blank or input-dead shell (verified with WeChat 4.x
    // in issue #12 - its tray ghost window showed up empty, and the real Qt main
    // window ignores input when force-shown). Launching the executable lets the app's
    // own single-instance logic raise its window properly.
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
}
