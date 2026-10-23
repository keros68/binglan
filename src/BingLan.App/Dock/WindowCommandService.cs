using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using BingLan.App.Interop;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.TopBar;

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

    // WeChat closed to the tray can be raised through its own global "show window"
    // hotkey (verified with WeChat 4.1.15: the logged-in main window returns, no blank
    // window, no login panel). The hotkey toggles, so it is sent only while the app runs
    // without a shown window, and only while some app holds that combination. A changed
    // or disabled hotkey, or no window within the timeout, falls back to Launch.
    internal static async Task<WindowCommandResult> RevealOrLaunchAsync(DockPinnedApp app)
    {
        if (TrayAppRevealRules.HotkeyFor(app.ExecutablePath) is not { } hotkey)
        {
            return Launch(app);
        }

        var processIds = RunningProcessIds(app.ExecutablePath!);
        if (processIds.Count == 0)
        {
            return Launch(app);
        }

        // The window is already back but the dock has not refreshed yet (a second click
        // right after a reveal): switch to it, since launching again would open the
        // app's login panel and the hotkey would hide the window.
        var shown = FindShownWindow(processIds);
        if (shown != 0)
        {
            return DockNativeMethods.SetForegroundWindow(shown)
                ? new WindowCommandResult(true, $"已切换到 {app.DisplayName}")
                : new WindowCommandResult(false, $"Windows 阻止了切换到 {app.DisplayName}");
        }

        if (!IsHotkeyRegistered(hotkey)
            || !ShellSurfaceNativeMethods.SendChord(ModifierKeysOf(hotkey), (ushort)hotkey.VirtualKey))
        {
            return Launch(app);
        }

        var deadline = Environment.TickCount64 + (long)TrayAppRevealRules.RevealTimeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(100);
            if (FindShownWindow(processIds) != 0)
            {
                return new WindowCommandResult(true, $"已唤回 {app.DisplayName}");
            }
        }

        return Launch(app);
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

    private static HashSet<uint> RunningProcessIds(string executablePath)
    {
        var ids = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executablePath)))
        {
            using (process)
            {
                if (string.Equals(
                        WindowCatalog.ExecutablePathOf((uint)process.Id),
                        executablePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    ids.Add((uint)process.Id);
                }
            }
        }
        return ids;
    }

    // The same test the dock uses to list a window, so a hit means the app is back.
    private static nint FindShownWindow(HashSet<uint> processIds)
    {
        var shell = DockNativeMethods.GetShellWindow();
        nint found = 0;
        DockNativeMethods.EnumWindows((window, _) =>
        {
            DockNativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (processIds.Contains(processId) && WindowCatalog.ShouldInclude(window, shell))
            {
                found = window;
            }
            return found == 0;
        }, 0);
        return found;
    }

    // A free combination means the app has no such hotkey now; sending it then would
    // reach whatever window is in front instead.
    private static bool IsHotkeyRegistered(TrayRevealHotkey hotkey)
    {
        if (!TopBarNativeMethods.RegisterHotKey(0, ProbeHotkeyId, hotkey.Modifiers | TopBarNativeMethods.ModNoRepeat, hotkey.VirtualKey))
        {
            return true;
        }
        TopBarNativeMethods.UnregisterHotKey(0, ProbeHotkeyId);
        return false;
    }

    private static ushort[] ModifierKeysOf(TrayRevealHotkey hotkey)
    {
        var keys = new List<ushort>();
        if ((hotkey.Modifiers & TopBarSearchRules.ModifierControl) != 0) keys.Add(VkControl);
        if ((hotkey.Modifiers & TopBarSearchRules.ModifierAlt) != 0) keys.Add(VkMenu);
        if ((hotkey.Modifiers & TopBarSearchRules.ModifierShift) != 0) keys.Add(VkShift);
        if ((hotkey.Modifiers & TopBarSearchRules.ModifierWindows) != 0) keys.Add(VkLeftWindows);
        return [.. keys];
    }

    private const int ProbeHotkeyId = 0x7A11;
    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkLeftWindows = 0x5B;

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
