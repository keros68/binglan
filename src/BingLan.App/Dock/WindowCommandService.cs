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

internal sealed record WindowCommandResult(
    bool Succeeded,
    string Message,
    bool NoLiveWindows = false,
    bool OpenSystemTrayRecommended = false);

internal static class WindowCommandService
{
    private const int ForegroundConfirmationPasses = 3;
    private const int ForegroundConfirmationDelayMs = 40;
    private const int ForegroundRequestLifetimeMs = 200;

    internal static async Task<WindowCommandResult> ToggleAsync(
        WindowGroup group,
        DockToggleCooldown cooldown,
        string? displayName = null,
        Func<TrackedWindow, int>? attentionPriority = null)
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
        Func<nint, int>? handlePriority = null;
        if (attentionPriority is not null)
        {
            var priorities = new Dictionary<nint, int>(live.Count);
            var maxActivatablePriority = 0;
            foreach (var entry in live)
            {
                var priority = attentionPriority(entry.Window);
                priorities[entry.Window.Handle] = priority;
                if (entry.Candidate.CanActivateDirectly && priority > maxActivatablePriority)
                {
                    maxActivatablePriority = priority;
                }
            }

            if (maxActivatablePriority > 0)
            {
                handlePriority = handle => priorities.TryGetValue(handle, out var p) && p == maxActivatablePriority
                    ? 2
                    : 0;
            }
        }
        var selected = WindowActivationRules.SelectActivationTarget(
            live.Select(entry => entry.Candidate).ToArray(),
            handlePriority)!;
        var target = live.First(entry => entry.Window.Handle == selected.Handle).Window;

        // If another window in this group (e.g., a second WeChat/QQ instance or a flashing
        // sibling window) is requesting attention, switch straight to it instead of minimizing
        // the currently active sibling.
        if (attentionPriority is not null
            && attentionPriority(target) > 0
            && (foregroundInGroup is null || foregroundInGroup.Handle != target.Handle))
        {
            cooldown.Clear(target.Handle);
            return await FocusAsync(target, DockNativeMethods.IsIconic(target.Handle), name);
        }

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
            var handle = foregroundInGroup!.Handle;
            if (!DockNativeMethods.ShowWindowAsync(handle, DockNativeMethods.SwMinimize))
            {
                var reason = ShellSurfaceNativeMethods.IsWindowElevated(handle)
                    ? $"{name} 以管理员权限运行，Dock 无法最小化它。"
                    : $"Dock 无法最小化 {name}。";
                return new WindowCommandResult(
                    false,
                    $"{reason}请使用该窗口的最小化按钮。");
            }
            cooldown.MarkMinimized(handle);
            return new WindowCommandResult(true, $"已最小化 {name}");
        }

        if (foregroundInGroup is { } restored)
        {
            cooldown.Clear(restored.Handle);
        }
        return await FocusAsync(target, action == DockClickAction.RestoreAndActivate, name);
    }

    internal static async Task<WindowCommandResult> FocusAsync(
        TrackedWindow target,
        bool restore,
        string? displayName = null)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? target.Title : displayName;
        var handle = target.Handle;
        if (!IsLiveFocusWindow(handle))
        {
            return new WindowCommandResult(false, "窗口已关闭", NoLiveWindows: true);
        }

        var originalForeground = GetRootOwnerOrSelf(DockNativeMethods.GetForegroundWindow());
        var inputTick = ShellSurfaceNativeMethods.GetLastInputTick();
        var deadline = Environment.TickCount64 + ForegroundRequestLifetimeMs;
        if (restore)
        {
            DockNativeMethods.ShowWindowAsync(handle, DockNativeMethods.SwRestore);
        }

        var activationTarget = ResolvePopupOrSelf(handle);
        var requestAccepted = DockNativeMethods.SetForegroundWindow(activationTarget);
        for (var pass = 0; pass <= ForegroundConfirmationPasses; pass++)
        {
            if (!IsLiveFocusWindow(handle))
            {
                return new WindowCommandResult(false, "窗口已关闭", NoLiveWindows: true);
            }

            var foreground = DockNativeMethods.GetForegroundWindow();
            var foregroundRoot = GetRootOwnerOrSelf(foreground);
            var minimized = DockNativeMethods.IsIconic(handle);
            if (!minimized && foreground != 0
                && (foreground == activationTarget || foregroundRoot == GetRootOwnerOrSelf(handle)))
            {
                return new WindowCommandResult(true, $"已切换到 {name}");
            }

            if (pass > 0 && (Environment.TickCount64 > deadline
                || !WindowActivationRules.CanRetryForegroundRequest(
                    originalForeground, foregroundRoot, inputTick, ShellSurfaceNativeMethods.GetLastInputTick())))
            {
                return new WindowCommandResult(false, string.Empty);
            }
            if (!requestAccepted && (ShellSurfaceNativeMethods.IsWindowElevated(activationTarget)
                    || ShellSurfaceNativeMethods.IsForegroundElevated()))
            {
                break;
            }
            if (pass is > 0 and < ForegroundConfirmationPasses && !minimized)
            {
                activationTarget = ResolvePopupOrSelf(handle);
                requestAccepted = DockNativeMethods.SetForegroundWindow(activationTarget);
            }
            if (pass < ForegroundConfirmationPasses)
            {
                // A cross-thread foreground request completes when the target processes it.
                await Task.Delay(ForegroundConfirmationDelayMs);
            }
        }

        return BuildFocusFailureResult(handle, name);
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

        var appUserModelId = item.LaunchTarget?.AppUserModelId ?? DockAppResolver.GetGroupAppUserModelId(item.Group);
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
        if (!DockPinRules.IsSystemPlace(path)
            && !await Task.Run(() => Directory.Exists(path) || File.Exists(path)))
        {
            return new WindowCommandResult(false, "文件或文件夹已不存在");
        }

        var failure = await ShellLauncher.StartAsync(new ProcessStartInfo(path) { UseShellExecute = true });
        return failure is null
            ? new WindowCommandResult(true, $"已打开 {item.DisplayName}")
            : new WindowCommandResult(false, $"无法打开 {item.DisplayName}：{failure.Message}");
    }

    // Apps like WeChat and QQ close to the tray with hidden windows that must not be force-shown
    // from outside, and re-launching their exe spawns a new login panel. Single-instance tray
    // residency uses the app's own reveal hotkey; multi-instance tray residency or hotkey failure
    // shows a clear hint instead of popping the wrong account or opening an unwanted login window.
    internal static async Task<WindowCommandResult> RevealOrLaunchAsync(
        DockPinnedApp app,
        uint? preferredProcessId = null,
        DockLaunchCooldown? launchCooldown = null)
    {
        if (DockPinRules.IsPathPin(app))
        {
            return await OpenPathAsync(app);
        }

        var cooldownKey = app.ExecutablePath ?? app.AppUserModelId;
        var preventsRelaunch = TrayAppRevealRules.PreventsRelaunchWhenRunning(app.ExecutablePath);
        var hotkey = TrayAppRevealRules.HotkeyFor(app.ExecutablePath);
        if (!preventsRelaunch && hotkey is null)
        {
            if (launchCooldown is not null && !launchCooldown.TryAcquire(cooldownKey))
            {
                return new WindowCommandResult(true, $"正在启动 {app.DisplayName}");
            }
            return await LaunchAsync(app);
        }

        var hierarchy = await Task.Run(() => RunningProcessHierarchy(app.ExecutablePath!));
        if (hierarchy.Count == 0)
        {
            if (launchCooldown is not null && !launchCooldown.TryAcquire(cooldownKey))
            {
                return new WindowCommandResult(true, $"正在启动 {app.DisplayName}");
            }
            return await LaunchAsync(app);
        }

        var processIds = hierarchy.Select(entry => entry.ProcessId).ToHashSet();

        // If a specific flashing instance was targeted (e.g. from the top bar or dock attention),
        // check all processes belonging to that root instance before falling back to any global
        // hotkey (an alert may come from a hidden helper process while the main process has a window).
        if (preferredProcessId is { } targetPid && targetPid != 0)
        {
            var instancePids = await Task.Run(() =>
            {
                var resolved = ResolveInstanceProcessIds(targetPid, app.ExecutablePath);
                foreach (var pid in TrayAppRevealRules.InstanceProcessIdsFor(hierarchy, targetPid))
                {
                    resolved.Add(pid);
                }
                return resolved;
            });
            var preferredShown = FindShownWindow(instancePids);
            if (preferredShown != 0)
            {
                DockNativeMethods.GetWindowThreadProcessId(preferredShown, out var shownPid);
                return await FocusRevealedHandleAsync(preferredShown, shownPid, app);
            }
        }
        else
        {
            var shown = FindShownWindow(processIds);
            if (shown != 0)
            {
                DockNativeMethods.GetWindowThreadProcessId(shown, out var shownPid);
                return await FocusRevealedHandleAsync(shown, shownPid, app);
            }
        }

        var rootCount = TrayAppRevealRules.CountRootInstances(hierarchy);
        if (hotkey is not null)
        {
            var hotkeyHierarchy = await Task.Run(() => RunningHotkeySharingHierarchy(app.ExecutablePath!));
            rootCount = Math.Max(rootCount, TrayAppRevealRules.CountRootInstances(hotkeyHierarchy));
        }
        var canSendHotkey = hotkey is not null && IsHotkeyRegistered(hotkey);
        var decision = TrayAppRevealRules.DecideWhenHidden(rootCount, canSendHotkey);
        switch (decision)
        {
            case TrayRevealAction.NotifyMultiInstanceInTray:
                return new WindowCommandResult(
                    false,
                    TrayAppRevealRules.FormatMultiInstanceTrayHint(app.DisplayName),
                    OpenSystemTrayRecommended: true);
            case TrayRevealAction.NotifyHotkeyUnavailable:
                return new WindowCommandResult(false, TrayAppRevealRules.FormatTrayHotkeyHint(app.DisplayName));
            case TrayRevealAction.LaunchNewProcess:
                if (launchCooldown is not null && !launchCooldown.TryAcquire(cooldownKey))
                {
                    return new WindowCommandResult(true, $"正在启动 {app.DisplayName}");
                }
                return await LaunchAsync(app);
        }

        if (hotkey is null || !ShellSurfaceNativeMethods.SendChord(ModifierKeysOf(hotkey), (ushort)hotkey.VirtualKey))
        {
            return new WindowCommandResult(false, TrayAppRevealRules.FormatTrayHotkeyHint(app.DisplayName));
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

        return new WindowCommandResult(false, TrayAppRevealRules.FormatTrayHotkeyHint(app.DisplayName));
    }

    private static Task<WindowCommandResult> FocusRevealedHandleAsync(
        nint handle,
        uint processId,
        DockPinnedApp app)
    {
        var tracked = new TrackedWindow(
            handle,
            processId,
            app.DisplayName,
            app.AppUserModelId,
            app.ExecutablePath,
            false,
            DockNativeMethods.IsIconic(handle));
        return FocusAsync(tracked, tracked.IsMinimized, app.DisplayName);
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

    internal static Task<WindowCommandResult> QuitAsync(
        DockItem item,
        string displayName,
        IReadOnlyList<DockItem>? allItems = null)
    {
        if (!DockQuitRules.CanOfferQuit(item, allItems))
        {
            return Task.FromResult(new WindowCommandResult(false, $"不能从 Dock 结束 {displayName}"));
        }

        var itemsSnapshot = allItems?.ToArray();
        return Task.Run(() => QuitCore(item, displayName, itemsSnapshot));
    }

    private static WindowCommandResult QuitCore(
        DockItem item,
        string displayName,
        IReadOnlyList<DockItem>? allItems)
    {
        var selfPid = (uint)Environment.ProcessId;
        int currentSessionId;
        using (var current = Process.GetCurrentProcess())
        {
            currentSessionId = current.SessionId;
        }

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
                }
                if (!string.IsNullOrWhiteSpace(window.AppUserModelId)
                    && !DockQuitRules.IsProtectedAumid(window.AppUserModelId))
                {
                    groupAumids.Add(window.AppUserModelId.Trim());
                }
            }
        }

        var otherItemExecutablePaths = CollectOtherItemExecutables(item, allItems);
        var launchTarget = item.LaunchTarget;
        var checkAumid = !string.IsNullOrWhiteSpace(launchTarget?.AppUserModelId) || groupAumids.Count > 0;

        var matchedAny = false;
        var terminated = 0;
        var accessDenied = false;

        for (var pass = 0; pass < 2; pass++)
        {
            var passResult = TerminateMatchingPass(
                launchTarget,
                groupExecutablePaths,
                groupAumids,
                otherItemExecutablePaths,
                checkAumid,
                selfPid,
                currentSessionId);

            matchedAny |= passResult.MatchedAny;
            terminated += passResult.Terminated;
            accessDenied |= passResult.AccessDenied;

            if (passResult.Terminated == 0)
            {
                break;
            }

            if (pass == 0)
            {
                // Brief pause off the UI thread so any watchdog/parent-spawned child or
                // tray helper that spun up during pass 1 is caught in the follow-up sweep.
                Thread.Sleep(80);
            }
        }

        if (!matchedAny)
        {
            return new WindowCommandResult(false, $"{displayName} 当前未在运行");
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

    private static (bool MatchedAny, int Terminated, bool AccessDenied) TerminateMatchingPass(
        DockPinnedApp? launchTarget,
        HashSet<string> groupExecutablePaths,
        HashSet<string> groupAumids,
        HashSet<string> otherItemExecutablePaths,
        bool checkAumid,
        uint selfPid,
        int currentSessionId)
    {
        var allParents = ReadAllParentProcessIds();
        var candidates = new List<ProcessQuitCandidate>();
        var expectedExeByPid = new Dictionary<uint, string?>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var pid = (uint)process.Id;
                if (pid <= 4 || pid == selfPid)
                {
                    continue;
                }

                try
                {
                    if (process.SessionId != currentSessionId)
                    {
                        continue;
                    }
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                {
                    continue;
                }

                var previewExe = WindowCatalog.ExecutablePathOf(pid);
                if (DockQuitRules.IsProtectedExecutable(previewExe))
                {
                    continue;
                }

                var previewAumid = checkAumid ? WindowCatalog.AppUserModelIdOf(pid) : null;
                if (DockQuitRules.IsProtectedAumid(previewAumid))
                {
                    continue;
                }

                allParents.TryGetValue(pid, out var parentPid);
                candidates.Add(new ProcessQuitCandidate(pid, parentPid, previewExe, previewAumid));
                expectedExeByPid[pid] = string.IsNullOrWhiteSpace(previewExe)
                    ? null
                    : WindowGrouping.NormalizeExecutablePath(previewExe);
            }
        }

        var targets = DockQuitRules.SelectProcessesToTerminate(
            launchTarget,
            groupExecutablePaths,
            groupAumids,
            candidates,
            otherItemExecutablePaths);
        if (targets.Count == 0)
        {
            return (false, 0, false);
        }

        var terminated = 0;
        var accessDenied = false;
        foreach (var targetPid in targets)
        {
            try
            {
                using var process = Process.GetProcessById((int)targetPid);
                if (process.SessionId != currentSessionId)
                {
                    continue;
                }

                // Hold one Process handle across final identity verification and termination so
                // PID reuse cannot target an unrelated process.
                var handle = process.Handle;
                var verifiedExe = WindowCatalog.TryGetExecutablePath(handle);
                var verifiedAumid = checkAumid ? WindowCatalog.TryGetAppUserModelId(handle) : null;
                if (DockQuitRules.IsProtectedExecutable(verifiedExe)
                    || DockQuitRules.IsProtectedAumid(verifiedAumid))
                {
                    continue;
                }

                if (expectedExeByPid.TryGetValue(targetPid, out var expectedExe) && expectedExe is not null)
                {
                    if (string.IsNullOrWhiteSpace(verifiedExe)
                        || !string.Equals(
                            WindowGrouping.NormalizeExecutablePath(verifiedExe),
                            expectedExe,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }
                else if (!DockQuitRules.MatchesProcess(launchTarget, groupExecutablePaths, groupAumids, verifiedExe, verifiedAumid))
                {
                    continue;
                }

                process.Kill();
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

        return (true, terminated, accessDenied);
    }

    private static HashSet<string> CollectOtherItemExecutables(DockItem item, IReadOnlyList<DockItem>? allItems)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (allItems is null)
        {
            return result;
        }

        foreach (var other in allItems)
        {
            if (ReferenceEquals(other, item)
                || string.Equals(other.Key, item.Key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var executablePath = other.LaunchTarget?.ExecutablePath;
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                result.Add(WindowGrouping.NormalizeExecutablePath(executablePath));
            }
            if (other.Group is not null)
            {
                foreach (var window in other.Group.Windows)
                {
                    if (!string.IsNullOrWhiteSpace(window.ExecutablePath))
                    {
                        result.Add(WindowGrouping.NormalizeExecutablePath(window.ExecutablePath));
                    }
                }
            }
        }

        return result;
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

    private static bool IsLiveFocusWindow(nint window) =>
        window != 0 && DockNativeMethods.IsWindow(window) && DockNativeMethods.IsWindowVisible(window)
        && !NativeMethods.IsWindowCloaked(window);

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

    /// <summary>
    /// Brings one of BingLan's own activatable windows (such as the TopBar inline search popup)
    /// to the foreground when triggered from a <c>WS_EX_NOACTIVATE</c> surface like the TopBar.
    /// Requests activation without connecting the foreground application's input queue.
    /// </summary>
    internal static bool TryActivateOwnWindow(nint handle)
    {
        if (handle == 0 || !DockNativeMethods.IsWindow(handle))
        {
            return false;
        }
        return DockNativeMethods.SetForegroundWindow(handle);
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

    internal static TrackedWindow SelectBestWindow(IReadOnlyList<TrackedWindow> windows)
    {
        var live = FilterLiveCandidates(windows);
        if (live.Count == 0)
        {
            return windows.FirstOrDefault(window => !window.IsMinimized) ?? windows[0];
        }

        var selected = WindowActivationRules.SelectActivationTarget(
            live.Select(entry => entry.Candidate).ToArray())!;
        return live.First(entry => entry.Window.Handle == selected.Handle).Window;
    }

    internal static HashSet<uint> ResolveInstanceProcessIds(uint processId, string? hintExecutablePath = null) =>
        ResolveInstanceInfo(processId, hintExecutablePath).ProcessIds;

    internal static (HashSet<uint> ProcessIds, string? RootExecutablePath) ResolveInstanceInfo(
        uint processId,
        string? hintExecutablePath = null)
    {
        if (processId == 0)
        {
            return ([], hintExecutablePath);
        }

        var seedExe = WindowCatalog.ExecutablePathOf(processId) ?? hintExecutablePath;
        if (string.IsNullOrWhiteSpace(seedExe))
        {
            return ([processId], null);
        }

        var normalizedSeed = WindowGrouping.NormalizeExecutablePath(seedExe);
        if (DockQuitRules.IsProtectedExecutable(normalizedSeed)
            || DockQuitRules.IsGenericShellExecutable(normalizedSeed))
        {
            return ([processId], normalizedSeed);
        }

        var appRoot = DockQuitRules.ResolveDedicatedAppRoot(normalizedSeed);
        int currentSessionId;
        using (var current = Process.GetCurrentProcess())
        {
            currentSessionId = current.SessionId;
        }

        var allParents = ReadAllParentProcessIds();
        if (allParents.Count == 0)
        {
            return ([processId], normalizedSeed);
        }

        string? TryGetSameAppExecutable(uint pid)
        {
            if (pid <= 4)
            {
                return null;
            }
            if (pid == processId)
            {
                return normalizedSeed;
            }
            if (!IsProcessInSession(pid, currentSessionId))
            {
                return null;
            }

            var candidateExe = WindowCatalog.ExecutablePathOf(pid);
            if (string.IsNullOrWhiteSpace(candidateExe))
            {
                return null;
            }

            var normalizedCandidate = WindowGrouping.NormalizeExecutablePath(candidateExe);
            if (DockQuitRules.IsProtectedExecutable(normalizedCandidate)
                || DockQuitRules.IsGenericShellExecutable(normalizedCandidate))
            {
                return null;
            }

            var sameApp = DockQuitRules.MatchesExecutableOrVersionSibling(normalizedSeed, normalizedCandidate)
                || (appRoot is not null
                    && string.Equals(
                        appRoot,
                        DockQuitRules.ResolveDedicatedAppRoot(normalizedCandidate),
                        StringComparison.OrdinalIgnoreCase));
            return sameApp ? normalizedCandidate : null;
        }

        // Walk UP only through ancestors belonging to the same app to find this instance's root.
        var rootPid = processId;
        var rootExe = normalizedSeed;
        var visitedUp = new HashSet<uint> { processId };
        while (allParents.TryGetValue(rootPid, out var parentPid)
            && parentPid > 4
            && visitedUp.Add(parentPid))
        {
            if (TryGetSameAppExecutable(parentPid) is not { } parentExe)
            {
                break;
            }

            rootPid = parentPid;
            rootExe = parentExe;
        }

        // Traverse DOWN only descendants reachable from rootPid, querying process metadata
        // strictly along same-app edges instead of scanning every OS process.
        var childrenByParent = new Dictionary<uint, List<uint>>();
        foreach (var (childPid, parentPid) in allParents)
        {
            if (childPid <= 4 || parentPid <= 4)
            {
                continue;
            }

            if (!childrenByParent.TryGetValue(parentPid, out var children))
            {
                children = [];
                childrenByParent[parentPid] = children;
            }
            children.Add(childPid);
        }

        var instancePids = new HashSet<uint> { rootPid };
        var queue = new Queue<uint>();
        queue.Enqueue(rootPid);
        if (instancePids.Add(processId))
        {
            queue.Enqueue(processId);
        }
        while (queue.Count > 0)
        {
            var currentPid = queue.Dequeue();
            if (!childrenByParent.TryGetValue(currentPid, out var children))
            {
                continue;
            }

            foreach (var childPid in children)
            {
                if (instancePids.Contains(childPid))
                {
                    continue;
                }

                if (TryGetSameAppExecutable(childPid) is null)
                {
                    continue;
                }

                instancePids.Add(childPid);
                queue.Enqueue(childPid);
            }
        }

        return (instancePids, rootExe);
    }

    private static bool IsProcessInSession(uint processId, int expectedSessionId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.SessionId == expectedSessionId;
        }
        catch (Exception exception) when (exception is ArgumentException or Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static List<ProcessHierarchyEntry> RunningProcessHierarchy(string executablePath)
    {
        var ids = RunningProcessIds(executablePath);
        if (ids.Count == 0)
        {
            return [];
        }

        var parents = ReadParentProcessIds(ids);
        var entries = new List<ProcessHierarchyEntry>(ids.Count);
        foreach (var pid in ids)
        {
            parents.TryGetValue(pid, out var ppid);
            entries.Add(new ProcessHierarchyEntry(pid, ppid));
        }
        return entries;
    }

    private static List<ProcessHierarchyEntry> RunningHotkeySharingHierarchy(string executablePath)
    {
        var names = TrayAppRevealRules.HotkeySharingProcessNames(executablePath);
        if (names.Count == 0)
        {
            return [];
        }

        var ids = new HashSet<uint>();
        int currentSessionId;
        using (var current = Process.GetCurrentProcess())
        {
            currentSessionId = current.SessionId;
        }

        foreach (var name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.SessionId != currentSessionId)
                        {
                            continue;
                        }
                    }
                    catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                    {
                        continue;
                    }

                    // Include all same-session processes whose image name shares this global hotkey,
                    // even if installed in another folder or elevated (where path lookup may return null).
                    ids.Add((uint)process.Id);
                }
            }
        }

        if (ids.Count == 0)
        {
            return [];
        }

        var parents = ReadParentProcessIds(ids);
        var entries = new List<ProcessHierarchyEntry>(ids.Count);
        foreach (var pid in ids)
        {
            parents.TryGetValue(pid, out var ppid);
            entries.Add(new ProcessHierarchyEntry(pid, ppid));
        }
        return entries;
    }

    private static Dictionary<uint, uint> ReadAllParentProcessIds()
    {
        var result = new Dictionary<uint, uint>();
        var snapshot = DockNativeMethods.CreateToolhelp32Snapshot(DockNativeMethods.Th32csSnapProcess, 0);
        if (snapshot == 0 || snapshot == DockNativeMethods.InvalidHandleValue)
        {
            return result;
        }

        try
        {
            var entry = new DockNativeMethods.ProcessEntry32W
            {
                Size = (uint)Marshal.SizeOf<DockNativeMethods.ProcessEntry32W>()
            };
            if (DockNativeMethods.Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    result[entry.ProcessId] = entry.ParentProcessId;
                }
                while (DockNativeMethods.Process32NextW(snapshot, ref entry));
            }
        }
        finally
        {
            DockNativeMethods.CloseHandle(snapshot);
        }

        return result;
    }

    private static Dictionary<uint, uint> ReadParentProcessIds(HashSet<uint> targetPids)
    {
        var result = new Dictionary<uint, uint>(targetPids.Count);
        var snapshot = DockNativeMethods.CreateToolhelp32Snapshot(DockNativeMethods.Th32csSnapProcess, 0);
        if (snapshot == 0 || snapshot == DockNativeMethods.InvalidHandleValue)
        {
            return result;
        }

        try
        {
            var entry = new DockNativeMethods.ProcessEntry32W
            {
                Size = (uint)Marshal.SizeOf<DockNativeMethods.ProcessEntry32W>()
            };
            if (DockNativeMethods.Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    if (targetPids.Contains(entry.ProcessId))
                    {
                        result[entry.ProcessId] = entry.ParentProcessId;
                    }
                }
                while (DockNativeMethods.Process32NextW(snapshot, ref entry));
            }
        }
        finally
        {
            DockNativeMethods.CloseHandle(snapshot);
        }

        return result;
    }

    private static HashSet<uint> RunningProcessIds(string executablePath)
    {
        var ids = new HashSet<uint>();
        var normalizedTarget = WindowGrouping.NormalizeExecutablePath(executablePath);
        int currentSessionId;
        using (var current = Process.GetCurrentProcess())
        {
            currentSessionId = current.SessionId;
        }

        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(normalizedTarget)))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != currentSessionId)
                    {
                        continue;
                    }
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                {
                    continue;
                }

                var candidatePath = WindowCatalog.ExecutablePathOf((uint)process.Id);
                if (!string.IsNullOrWhiteSpace(candidatePath)
                    && DockQuitRules.MatchesExecutableOrVersionSibling(normalizedTarget, candidatePath))
                {
                    ids.Add((uint)process.Id);
                }
            }
        }
        return ids;
    }

    // The same test the dock uses to list a window, so a hit means the app is back.
    // Selects the best activatable window rather than stopping at the first Z-order match.
    private static nint FindShownWindow(HashSet<uint> processIds)
    {
        var shell = DockNativeMethods.GetShellWindow();
        var candidates = new List<TrackedWindow>();
        DockNativeMethods.EnumWindows((window, _) =>
        {
            DockNativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (processIds.Contains(processId) && WindowCatalog.ShouldInclude(window, shell))
            {
                candidates.Add(new TrackedWindow(
                    window,
                    processId,
                    string.Empty,
                    null,
                    null,
                    false,
                    DockNativeMethods.IsIconic(window)));
            }
            return true;
        }, 0);

        if (candidates.Count == 0)
        {
            return 0;
        }

        return SelectBestWindow(candidates).Handle;
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
            : item.LaunchTarget?.WorkingDirectory is { } folder && Directory.Exists(folder)
                ? folder
                : Path.GetDirectoryName(executablePath);
        return (executablePath, workingDirectory);
    }

    private static IEnumerable<string> CandidateExecutables(DockItem item)
    {
        var launchExecutable = item.LaunchTarget?.ExecutablePath;
        if (!string.IsNullOrWhiteSpace(launchExecutable))
        {
            yield return launchExecutable;
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
        var arguments = DockFileDropRules.BuildLaunchArguments(item.LaunchTarget?.Arguments, filePaths);
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
