using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.TopBar;

namespace BingLan.App.Dock;

internal sealed record WindowCommandResult(bool Succeeded, string Message, bool NoLiveWindows = false);

internal static class WindowCommandService
{
    internal static async Task<WindowCommandResult> ToggleAsync(
        WindowGroup group,
        DockToggleCooldown cooldown,
        string? displayName = null)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? group.DisplayName : displayName;
        if (group.Windows.Count == 0)
        {
            return new WindowCommandResult(false, "窗口组为空", NoLiveWindows: true);
        }

        var live = FilterLiveCandidates(group.Windows);
        if (live.Count == 0)
        {
            return new WindowCommandResult(false, "窗口已关闭", NoLiveWindows: true);
        }

        var foreground = DockNativeMethods.GetForegroundWindow();
        var foregroundRoot = GetRootOwnerOrSelf(foreground);
        var foregroundInGroup = live
            .Select(entry => entry.Window)
            .FirstOrDefault(window => window.Handle == foreground
                || (foregroundRoot != 0 && GetRootOwnerOrSelf(window.Handle) == foregroundRoot));
        var selected = WindowActivationRules.SelectActivationTarget(
            live.Select(entry => entry.Candidate).ToArray())!;
        var target = live.First(entry => entry.Window.Handle == selected.Handle).Window;

        // A minimized foreground is either stale (the pointer has not moved on yet) or
        // an already-minimized window the system still names; both mean "restore".
        var foregroundIsMinimized = foregroundInGroup is { } active
            && (DockNativeMethods.IsIconic(active.Handle) || cooldown.RecentlyMinimized(active.Handle));
        var action = WindowActionPolicy.Decide(
            foregroundInGroup is not null,
            foregroundIsMinimized,
            DockNativeMethods.IsIconic(target.Handle));
        if (action == DockClickAction.MinimizeForeground)
        {
            var minimized = DockNativeMethods.ShowWindowAsync(foregroundInGroup!.Handle, DockNativeMethods.SwMinimize);
            if (!minimized && ShellSurfaceNativeMethods.IsWindowElevated(foregroundInGroup.Handle))
            {
                return new WindowCommandResult(
                    false,
                    $"{name} 正在以管理员身份运行，Windows 不允许普通权限的 Dock 最小化它。");
            }
            cooldown.MarkMinimized(foregroundInGroup.Handle);
            return new WindowCommandResult(true, $"已最小化 {name}");
        }

        if (foregroundInGroup is { } restored)
        {
            cooldown.Clear(restored.Handle);
        }
        return await FocusAsync(target, action == DockClickAction.RestoreAndActivate, name);
    }

    internal static WindowCommandResult Focus(TrackedWindow window) =>
        FocusCore(window, DockNativeMethods.IsIconic(window.Handle), window.Title);

    internal static async Task<WindowCommandResult> FocusAsync(
        TrackedWindow target,
        bool restore,
        string? displayName = null)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? target.Title : displayName;
        if (target.Handle == 0
            || !DockNativeMethods.IsWindow(target.Handle)
            || !DockNativeMethods.IsWindowVisible(target.Handle))
        {
            return new WindowCommandResult(false, "窗口已关闭", NoLiveWindows: true);
        }

        if (TryBringToForeground(target.Handle, restore))
        {
            return new WindowCommandResult(true, $"已切换到 {name}");
        }

        if (restore
            && !ShellSurfaceNativeMethods.IsWindowElevated(ResolvePopupOrSelf(target.Handle))
            && !ShellSurfaceNativeMethods.IsForegroundElevated())
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await Task.Delay(40);
                if (!DockNativeMethods.IsWindow(target.Handle)
                    || !DockNativeMethods.IsWindowVisible(target.Handle))
                {
                    return new WindowCommandResult(false, "窗口已关闭", NoLiveWindows: true);
                }
                if (TryBringToForeground(target.Handle, restore: false))
                {
                    return new WindowCommandResult(true, $"已切换到 {name}");
                }
            }
        }

        return BuildFocusFailureResult(target.Handle, name);
    }

    /// <summary>
    /// Restores and brings a running window forward during drag hover without flashing
    /// the taskbar if Windows refuses foreground focus transfer.
    /// </summary>
    internal static void RaiseForDrag(WindowGroup group)
    {
        if (group.Windows.Count == 0)
        {
            return;
        }

        var target = group.Windows.FirstOrDefault(window => !DockNativeMethods.IsIconic(window.Handle))
            ?? group.Windows[0];
        if (DockNativeMethods.IsIconic(target.Handle))
        {
            DockNativeMethods.ShowWindowAsync(target.Handle, DockNativeMethods.SwRestore);
        }

        if (DockNativeMethods.SetForegroundWindow(target.Handle))
        {
            return;
        }

        // Only nudge the window to the top of normal windows; leave already-topmost
        // windows untouched so we do not strip WS_EX_TOPMOST from them.
        if ((NativeMethods.GetExStyleOf(target.Handle) & NativeMethods.WsExTopmost) != 0)
        {
            return;
        }

        const uint flags = NativeMethods.SwpNoMove
            | NativeMethods.SwpNoSize
            | NativeMethods.SwpNoActivate
            | DockNativeMethods.SwpAsyncWindowPos;
        NativeMethods.SetWindowPos(target.Handle, DockNativeMethods.HwndTopmost, 0, 0, 0, 0, flags);
        NativeMethods.SetWindowPos(target.Handle, DockNativeMethods.HwndNoTopmost, 0, 0, 0, 0, flags);
    }

    internal static async Task<WindowCommandResult> OpenFilesAsync(
        DockItem item,
        string displayName,
        IReadOnlyList<DroppedPathInfo> droppedPaths)
    {
        var preparation = await Task.Run(() =>
        {
            var files = DockFileDropRules.SelectOpenTargets(droppedPaths).Where(File.Exists).ToArray();
            var launch = files.Length > 0 ? ResolveFileLaunch(item) : default;
            return (Files: files, Launch: launch);
        });
        var existingFiles = preparation.Files;
        if (existingFiles.Length == 0)
        {
            return new WindowCommandResult(false, "所拖入的文件已不存在");
        }

        if (preparation.Launch.ExecutablePath is { } executablePath)
        {
            var result = await LaunchWithFilesAsync(item, executablePath, preparation.Launch.WorkingDirectory, displayName, existingFiles);
            if (result.Succeeded && item.Group is { Windows.Count: > 0 } group)
            {
                RaiseForDrag(group);
            }
            return result;
        }

        var appUserModelId = item.Pinned?.AppUserModelId ?? DockAppResolver.GetGroupAppUserModelId(item.Group);
        if (!string.IsNullOrWhiteSpace(appUserModelId))
        {
            if (await Task.Run(() => ActivatePackagedAppForFiles(appUserModelId, existingFiles)))
            {
                if (item.Group is { Windows.Count: > 0 } group)
                {
                    RaiseForDrag(group);
                }
                return new WindowCommandResult(true, $"已用 {displayName} 打开所拖入的文件");
            }

            var fallbackLaunch = await Task.Run(() => ResolveFileLaunch(item, allowPackagedExecutable: true));
            if (fallbackLaunch.ExecutablePath is { } fallbackExe)
            {
                var fallback = await LaunchWithFilesAsync(item, fallbackExe, fallbackLaunch.WorkingDirectory, displayName, existingFiles);
                if (fallback.Succeeded && item.Group is { Windows.Count: > 0 } group)
                {
                    RaiseForDrag(group);
                }
                return fallback;
            }

            return new WindowCommandResult(false, $"无法用 {displayName} 打开所拖入的文件");
        }

        return new WindowCommandResult(false, $"找不到 {displayName} 的程序文件");
    }

    // A pinned app closed to the tray still runs with its windows hidden, but those
    // windows must NOT be shown from outside: the app still believes they are hidden,
    // so forcing SW_SHOW yields a blank or input-dead shell (verified with WeChat 4.x
    // in issue #12 - its tray ghost window showed up empty, and the real Qt main
    // window ignores input when force-shown). Launching the executable lets the app's
    // own single-instance logic raise its window properly.
    internal static async Task<WindowCommandResult> LaunchAsync(DockPinnedApp app)
    {
        if (DockPinRules.IsPathPin(app))
        {
            return await OpenPathAsync(app);
        }

        if (app.AppUserModelId is not null)
        {
            var result = await StartAsync(DockAppResolver.AppsFolderPath(app.AppUserModelId), app.DisplayName);
            if (result.Succeeded || app.ExecutablePath is null)
            {
                return result;
            }
        }

        var executablePath = app.ExecutablePath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new WindowCommandResult(false, $"找不到 {app.DisplayName} 的程序文件");
        }
        var preferredWorkingDirectory = app.WorkingDirectory;
        var launch = await Task.Run(() =>
        {
            if (!File.Exists(executablePath))
            {
                return (Exists: false, WorkingDirectory: (string?)null);
            }
            // A shortcut's working directory may be on an offline network share.
            var workingDirectory = preferredWorkingDirectory is { } folder && Directory.Exists(folder)
                ? folder
                : Path.GetDirectoryName(executablePath);
            return (Exists: true, WorkingDirectory: workingDirectory);
        });
        if (!launch.Exists)
        {
            return new WindowCommandResult(false, $"找不到 {app.DisplayName} 的程序文件");
        }

        return await StartAsync(executablePath, app.DisplayName, app.Arguments, launch.WorkingDirectory);
    }

    // A pinned folder opens in Explorer and a file in its default program.
    private static async Task<WindowCommandResult> OpenPathAsync(DockPinnedApp item)
    {
        var path = item.TargetPath!;
        if (!await Task.Run(() => Directory.Exists(path) || File.Exists(path)))
        {
            return new WindowCommandResult(false, "文件或文件夹已不存在");
        }

        var failure = await ShellLauncher.StartAsync(new ProcessStartInfo(path) { UseShellExecute = true });
        return failure is null
            ? new WindowCommandResult(true, $"已打开 {item.DisplayName}")
            : new WindowCommandResult(false, $"无法打开 {item.DisplayName}：{failure.Message}");
    }

    // WeChat closed to the tray can be raised through its own global "show window"
    // hotkey (verified with WeChat 4.1.15: the logged-in main window returns, no blank
    // window, no login panel). The hotkey toggles, so it is sent only while the app runs
    // without a shown window, and only while some app holds that combination. A changed
    // or disabled hotkey, or no window within the timeout, falls back to Launch.
    internal static async Task<WindowCommandResult> RevealOrLaunchAsync(DockPinnedApp app)
    {
        if (DockPinRules.IsPathPin(app))
        {
            return await OpenPathAsync(app);
        }

        if (TrayAppRevealRules.HotkeyFor(app.ExecutablePath) is not { } hotkey)
        {
            return await LaunchAsync(app);
        }

        var processIds = await Task.Run(() => RunningProcessIds(app.ExecutablePath!));
        if (processIds.Count == 0)
        {
            return await LaunchAsync(app);
        }

        // The window is already back but the dock has not refreshed yet (a second click
        // right after a reveal): switch to it, since launching again would open the
        // app's login panel and the hotkey would hide the window.
        var shown = FindShownWindow(processIds);
        if (shown != 0)
        {
            var tracked = new TrackedWindow(
                shown,
                0,
                app.DisplayName,
                app.AppUserModelId,
                app.ExecutablePath,
                false,
                DockNativeMethods.IsIconic(shown));
            return await FocusAsync(tracked, tracked.IsMinimized, app.DisplayName);
        }

        if (!IsHotkeyRegistered(hotkey)
            || !ShellSurfaceNativeMethods.SendChord(ModifierKeysOf(hotkey), (ushort)hotkey.VirtualKey))
        {
            return await LaunchAsync(app);
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

        return await LaunchAsync(app);
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

    internal static Task<WindowCommandResult> QuitAsync(DockItem item, string displayName)
    {
        if (!DockQuitRules.CanOfferQuit(item))
        {
            return Task.FromResult(new WindowCommandResult(false, $"不能从 Dock 结束 {displayName}"));
        }

        return Task.Run(() => QuitCore(item, displayName));
    }

    private static WindowCommandResult QuitCore(DockItem item, string displayName)
    {
        var selfPid = Environment.ProcessId;
        var groupPids = new HashSet<int>();
        var groupExecutablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groupAumids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (item.Group is { } group)
        {
            foreach (var window in group.Windows)
            {
                if (!string.IsNullOrWhiteSpace(window.ExecutablePath)
                    && !DockQuitRules.IsProtectedExecutable(window.ExecutablePath))
                {
                    groupExecutablePaths.Add(WindowGrouping.NormalizeExecutablePath(window.ExecutablePath));
                    if (window.ProcessId > 4 && window.ProcessId != selfPid)
                    {
                        groupPids.Add((int)window.ProcessId);
                    }
                }
                if (!string.IsNullOrWhiteSpace(window.AppUserModelId)
                    && !DockQuitRules.IsProtectedAumid(window.AppUserModelId))
                {
                    groupAumids.Add(window.AppUserModelId.Trim());
                }
            }
        }

        var checkAumid = !string.IsNullOrWhiteSpace(item.Pinned?.AppUserModelId) || groupAumids.Count > 0;
        var targetPids = new HashSet<int>(groupPids);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var pid = process.Id;
                if (pid <= 4 || pid == selfPid)
                {
                    continue;
                }

                var exe = WindowCatalog.ExecutablePathOf((uint)pid);
                if (DockQuitRules.IsProtectedExecutable(exe))
                {
                    targetPids.Remove(pid);
                    continue;
                }

                var aumid = checkAumid ? WindowCatalog.AppUserModelIdOf((uint)pid) : null;
                if (DockQuitRules.MatchesProcess(item.Pinned, groupExecutablePaths, groupAumids, exe, aumid))
                {
                    targetPids.Add(pid);
                }
            }
        }

        if (targetPids.Count == 0)
        {
            return new WindowCommandResult(false, $"{displayName} 当前未在运行");
        }

        var terminated = 0;
        var accessDenied = false;
        foreach (var pid in targetPids)
        {
            if (pid <= 4 || pid == selfPid)
            {
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(pid);
                // Re-check right before termination to guard against PID reuse.
                if (DockQuitRules.IsProtectedExecutable(WindowCatalog.ExecutablePathOf((uint)pid)))
                {
                    continue;
                }

                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is NotSupportedException or Win32Exception)
                {
                    process.Kill();
                }
                terminated++;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                terminated++;
            }
            catch (Exception exception) when (exception is Win32Exception or UnauthorizedAccessException)
            {
                accessDenied = true;
            }
        }

        if (terminated == 0 && accessDenied)
        {
            return new WindowCommandResult(
                false,
                $"{displayName} 正在以管理员身份运行，Windows 不允许普通权限的 Dock 结束它。");
        }
        if (accessDenied)
        {
            return new WindowCommandResult(
                false,
                $"已结束 {displayName} 的部分进程，其余进程需要管理员权限。");
        }
        return new WindowCommandResult(true, $"已结束 {displayName}");
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

    private static WindowCommandResult FocusCore(TrackedWindow target, bool restore, string name)
    {
        if (target.Handle == 0
            || !DockNativeMethods.IsWindow(target.Handle)
            || !DockNativeMethods.IsWindowVisible(target.Handle))
        {
            return new WindowCommandResult(false, "窗口已关闭", NoLiveWindows: true);
        }

        if (TryBringToForeground(target.Handle, restore))
        {
            return new WindowCommandResult(true, $"已切换到 {name}");
        }

        return BuildFocusFailureResult(target.Handle, name);
    }

    private static WindowCommandResult BuildFocusFailureResult(nint targetHandle, string name)
    {
        var targetElevated = ShellSurfaceNativeMethods.IsWindowElevated(ResolvePopupOrSelf(targetHandle));
        var foregroundElevated = !targetElevated && ShellSurfaceNativeMethods.IsForegroundElevated();
        if (!targetElevated)
        {
            var info = new DockNativeMethods.FlashWindowInfo
            {
                Size = (uint)Marshal.SizeOf<DockNativeMethods.FlashWindowInfo>(),
                Window = targetHandle,
                Flags = 2,
                Count = 3,
                Timeout = 0
            };
            DockNativeMethods.FlashWindowEx(ref info);
        }
        return new WindowCommandResult(
            false,
            WindowActivationRules.FormatSwitchBlockedMessage(name, targetElevated, foregroundElevated));
    }

    private static bool TryBringToForeground(nint targetWindow, bool restore)
    {
        if (restore)
        {
            DockNativeMethods.ShowWindowAsync(targetWindow, DockNativeMethods.SwRestore);
        }

        var activationTarget = ResolvePopupOrSelf(targetWindow);
        if (DockNativeMethods.SetForegroundWindow(activationTarget))
        {
            return true;
        }

        if (ShellSurfaceNativeMethods.IsWindowElevated(activationTarget)
            || ShellSurfaceNativeMethods.IsForegroundElevated())
        {
            return false;
        }

        var foreground = DockNativeMethods.GetForegroundWindow();
        if (!IsWindowResponsive(activationTarget)
            || (foreground != 0 && !IsWindowResponsive(foreground)))
        {
            return false;
        }

        var foregroundThread = foreground != 0
            ? DockNativeMethods.GetWindowThreadProcessId(foreground, out _)
            : 0;
        var currentThread = DockNativeMethods.GetCurrentThreadId();
        var attached = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attached = DockNativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
                if (!attached)
                {
                    return false;
                }
            }
            DockNativeMethods.BringWindowToTop(activationTarget);
            _ = DockNativeMethods.SetForegroundWindow(activationTarget);
        }
        finally
        {
            if (attached)
            {
                DockNativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        var currentForeground = DockNativeMethods.GetForegroundWindow();
        return currentForeground != 0
            && (currentForeground == activationTarget
                || currentForeground == targetWindow
                || GetRootOwnerOrSelf(currentForeground) == GetRootOwnerOrSelf(targetWindow));
    }

    private static List<(TrackedWindow Window, WindowCandidateInfo Candidate)> FilterLiveCandidates(
        IReadOnlyList<TrackedWindow> windows)
    {
        var live = new List<(TrackedWindow Window, WindowCandidateInfo Candidate)>(windows.Count);
        foreach (var window in windows)
        {
            var handle = window.Handle;
            if (handle == 0
                || !DockNativeMethods.IsWindow(handle)
                || !DockNativeMethods.IsWindowVisible(handle)
                || NativeMethods.IsWindowCloaked(handle))
            {
                continue;
            }

            var isMinimized = DockNativeMethods.IsIconic(handle);
            var isEnabled = DockNativeMethods.IsWindowEnabled(handle);
            var popup = ResolvePopupOrSelf(handle);
            var hasEnabledPopup = popup != handle
                && DockNativeMethods.IsWindowVisible(popup)
                && DockNativeMethods.IsWindowEnabled(popup);
            var hasTitle = !string.IsNullOrWhiteSpace(window.Title)
                || DockNativeMethods.GetWindowTextLengthW(handle) > 0;
            live.Add((
                window,
                new WindowCandidateInfo(handle, isMinimized, isEnabled, hasEnabledPopup, hasTitle)));
        }
        return live;
    }

    private static nint ResolvePopupOrSelf(nint window)
    {
        var current = window;
        for (var depth = 0; depth < 8 && current != 0; depth++)
        {
            var popup = DockNativeMethods.GetLastActivePopup(current);
            if (popup == 0 || popup == current)
            {
                break;
            }
            if (DockNativeMethods.IsWindowVisible(popup) && DockNativeMethods.IsWindowEnabled(popup))
            {
                return popup;
            }
            current = popup;
        }
        return window;
    }

    private static nint GetRootOwnerOrSelf(nint window)
    {
        if (window == 0)
        {
            return 0;
        }
        var rootOwner = DockNativeMethods.GetAncestor(window, DockNativeMethods.GaRootOwner);
        return rootOwner != 0 ? rootOwner : window;
    }

    private static bool IsWindowResponsive(nint window) =>
        window != 0
        && DockNativeMethods.SendMessageTimeoutW(
            window,
            DockNativeMethods.WmNull,
            0,
            0,
            DockNativeMethods.SmtoAbortIfHung,
            50,
            out _) != 0;

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

    private static (string? ExecutablePath, string? WorkingDirectory) ResolveFileLaunch(
        DockItem item,
        bool allowPackagedExecutable = false)
    {
        var executablePath = CandidateExecutables(item).FirstOrDefault(path =>
            !DockAppResolver.IsFrameHost(path)
            && (allowPackagedExecutable || !DockAppResolver.IsPackagedPath(path))
            && File.Exists(path));
        var workingDirectory = executablePath is null ? null
            : item.Pinned?.WorkingDirectory is { } folder && Directory.Exists(folder)
                ? folder
                : Path.GetDirectoryName(executablePath);
        return (executablePath, workingDirectory);
    }

    private static IEnumerable<string> CandidateExecutables(DockItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Pinned?.ExecutablePath))
        {
            yield return item.Pinned.ExecutablePath;
        }

        if (item.Group is not null)
        {
            foreach (var window in item.Group.Windows)
            {
                if (!string.IsNullOrWhiteSpace(window.ExecutablePath))
                {
                    yield return window.ExecutablePath;
                }
            }
        }
    }

    private static Task<WindowCommandResult> LaunchWithFilesAsync(
        DockItem item,
        string executablePath,
        string? workingDirectory,
        string displayName,
        IReadOnlyList<string> filePaths)
    {
        var arguments = DockFileDropRules.BuildLaunchArguments(item.Pinned?.Arguments, filePaths);
        return StartAsync(executablePath, displayName, arguments, workingDirectory);
    }

    private static bool ActivatePackagedAppForFiles(string appUserModelId, IReadOnlyList<string> files)
    {
        var pidls = new List<nint>(files.Count);
        DockNativeMethods.IShellItemArray? itemArray = null;
        object? managerObject = null;
        try
        {
            foreach (var file in files)
            {
                if (DockNativeMethods.SHParseDisplayName(file, 0, out var pidl, 0, out _) != 0 || pidl == 0)
                {
                    return false;
                }
                pidls.Add(pidl);
            }

            if (pidls.Count == 0
                || DockNativeMethods.SHCreateShellItemArrayFromIDLists(
                    (uint)pidls.Count,
                    [.. pidls],
                    out itemArray) != 0)
            {
                return false;
            }

            managerObject = new DockNativeMethods.ApplicationActivationManager();
            var manager = (DockNativeMethods.IApplicationActivationManager)managerObject;
            var hr = manager.ActivateForFile(appUserModelId, itemArray, null, out _);
            return hr >= 0;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException)
        {
            return false;
        }
        finally
        {
            if (itemArray is not null && Marshal.IsComObject(itemArray))
            {
                Marshal.FinalReleaseComObject(itemArray);
            }
            if (managerObject is not null && Marshal.IsComObject(managerObject))
            {
                Marshal.FinalReleaseComObject(managerObject);
            }
            foreach (var pidl in pidls)
            {
                if (pidl != 0)
                {
                    DockNativeMethods.ILFree(pidl);
                }
            }
        }
    }

    private static async Task<WindowCommandResult> StartAsync(
        string target,
        string displayName,
        string? arguments = null,
        string? workingDirectory = null)
    {
        var failure = await ShellLauncher.StartAsync(new ProcessStartInfo(target)
        {
            UseShellExecute = true,
            Arguments = arguments ?? string.Empty,
            WorkingDirectory = workingDirectory ?? string.Empty
        });
        return failure is null
            ? new WindowCommandResult(true, $"已启动 {displayName}")
            : new WindowCommandResult(false, $"无法启动 {displayName}：{failure.Message}");
    }
}
