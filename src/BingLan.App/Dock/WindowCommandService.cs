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
    bool OpenTrayRecommended = false)
{
    internal static WindowCommandResult TrayRevealFailed(string displayName) =>
        new(false, TrayAppRevealRules.FormatTrayHotkeyHint(displayName), OpenTrayRecommended: true);
}

internal static class WindowCommandService
{
    private const int ForegroundConfirmationPasses = 3;
    private const int ForegroundConfirmationDelayMs = 40;
    private const int ForegroundRequestLifetimeMs = 200;
    private const uint GwEnabledPopup = 6;
    private const int ErrorNoMoreFiles = 18;

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
        var foregroundInGroup = live
            .Select(entry => entry.Window)
            .FirstOrDefault(window => window.Handle == foreground)
            ?? live.Select(entry => entry.Window)
                .FirstOrDefault(window => WindowActivationRules.IsWindowOrOwnedPopup(
                    window.Handle, foreground, ReadWindowOwner));
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

    internal static Task<WindowCommandResult> FocusAttentionAsync(
        IReadOnlyList<TrackedWindow> instanceWindows,
        nint attentionWindow,
        string displayName)
    {
        var live = FilterLiveCandidates(instanceWindows);
        var selected = WindowActivationRules.SelectActivationTarget(
            live.Select(entry => entry.Candidate).ToArray(),
            handle => handle == attentionWindow ? 2 : 0);
        return selected is null
            ? Task.FromResult(new WindowCommandResult(false, "窗口已关闭", NoLiveWindows: true))
            : FocusAsync(live.First(entry => entry.Window.Handle == selected.Handle).Window, selected.IsMinimized, displayName);
    }

    internal static async Task<WindowCommandResult> FocusAsync(
        TrackedWindow target,
        bool restore,
        string? displayName = null,
        bool flashOnFailure = true)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? target.Title : displayName;
        var handle = target.Handle;
        if (!IsLiveFocusWindow(target))
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
            if (!IsLiveFocusWindow(target))
            {
                return new WindowCommandResult(false, "窗口已关闭", NoLiveWindows: true);
            }

            var foreground = DockNativeMethods.GetForegroundWindow();
            var foregroundRoot = GetRootOwnerOrSelf(foreground);
            var minimized = DockNativeMethods.IsIconic(handle);
            if (!minimized && WindowActivationRules.IsWindowOrOwnedPopup(handle, foreground, ReadWindowOwner))
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

        return BuildFocusFailureResult(handle, name, flashOnFailure);
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
    // opens the tray when available so the user can select the intended account.
    internal static async Task<WindowCommandResult> RevealOrLaunchAsync(
        DockPinnedApp app,
        uint? preferredProcessId = null,
        DockLaunchCooldown? launchCooldown = null)
    {
        if (DockPinRules.IsPathPin(app))
        {
            return await OpenPathAsync(app);
        }

        var targetPid = preferredProcessId.GetValueOrDefault();
        HashSet<uint>? instancePids = null;
        var executablePath = app.ExecutablePath;
        if (targetPid != 0)
        {
            var preferred = await Task.Run(() => ResolveInstanceInfo(targetPid, executablePath));
            instancePids = preferred.ProcessIds;
            executablePath = TrayAppRevealRules.ResolveRevealExecutable(executablePath, preferred.RootExecutablePath);
            if (TrayAppRevealRules.IsWeChatHelperExecutable(executablePath))
            {
                var targetExited = instancePids.Count == 0
                    && await Task.Run(() => ReadParentProcessIds([targetPid]) is { Count: 0 });
                return targetExited
                    ? new WindowCommandResult(false, $"{app.DisplayName} 的提醒窗口已关闭", NoLiveWindows: true)
                    : new WindowCommandResult(false, TrayAppRevealRules.FormatChooseTrayInstanceHint(app.DisplayName),
                        OpenTrayRecommended: true);
            }
        }

        var cooldownKey = executablePath ?? app.AppUserModelId;
        var preventsRelaunch = TrayAppRevealRules.PreventsRelaunchWhenRunning(executablePath);
        var hotkey = TrayAppRevealRules.HotkeyFor(executablePath);
        if (!preventsRelaunch && hotkey is null)
        {
            if (launchCooldown is not null && !launchCooldown.TryAcquire(cooldownKey))
            {
                return new WindowCommandResult(true, $"正在启动 {app.DisplayName}");
            }
            return await LaunchAsync(app);
        }

        var hierarchy = await Task.Run(() => RunningProcessHierarchy(executablePath!));
        var processIds = hierarchy.Where(entry => entry.IsIdentityVerified).Select(entry => entry.ProcessId).ToHashSet();

        // If a specific flashing instance was targeted (e.g. from the top bar or dock attention),
        // check all processes belonging to that root instance before falling back to any global
        // hotkey (an alert may come from a hidden helper process while the main process has a window).
        if (instancePids is not null)
        {
            var preferredShown = FindShownWindow(instancePids);
            if (preferredShown != 0)
            {
                DockNativeMethods.GetWindowThreadProcessId(preferredShown, out var shownPid);
                return await FocusRevealedHandleAsync(preferredShown, shownPid, app);
            }
        }
        else if (processIds.Count > 0)
        {
            var shown = FindShownWindow(processIds);
            if (shown != 0)
            {
                DockNativeMethods.GetWindowThreadProcessId(shown, out var shownPid);
                return await FocusRevealedHandleAsync(shown, shownPid, app);
            }
        }

        var rootCount = TrayAppRevealRules.CountRootInstances(hierarchy);
        var unresolvedTarget = instancePids is not null && !instancePids.Overlaps(processIds);
        var preferredInstanceExited = false;
        if (unresolvedTarget)
        {
            var targetPids = new HashSet<uint>(instancePids!) { targetPid };
            preferredInstanceExited = await Task.Run(() => ReadParentProcessIds(targetPids) is { Count: 0 });
        }

        var ambiguousHotkeyOwner = unresolvedTarget || hierarchy.Any(entry => !entry.IsIdentityVerified);
        if (!unresolvedTarget && hotkey is not null)
        {
            var hotkeyHierarchy = await Task.Run(() => RunningHotkeySharingHierarchy(executablePath!));
            ambiguousHotkeyOwner |= TrayAppRevealRules.IsSharedHotkeyOwnerAmbiguous(
                hotkeyHierarchy, instancePids ?? []);
            if (rootCount > 0)
            {
                rootCount = Math.Max(rootCount, TrayAppRevealRules.CountRootInstances(hotkeyHierarchy));
            }
        }
        var canSendHotkey = rootCount == 1 && !ambiguousHotkeyOwner && hotkey is not null && IsHotkeyRegistered(hotkey);
        var decision = TrayAppRevealRules.DecideWhenHidden(
            rootCount, canSendHotkey, targetPid != 0, preferredInstanceExited, ambiguousHotkeyOwner);
        switch (decision)
        {
            case TrayRevealAction.NotifyTrayInstanceChoiceRequired:
                return new WindowCommandResult(false, TrayAppRevealRules.FormatChooseTrayInstanceHint(app.DisplayName),
                    OpenTrayRecommended: true);
            case TrayRevealAction.NotifyMultiInstanceInTray:
                return new WindowCommandResult(
                    false,
                    TrayAppRevealRules.FormatMultiInstanceTrayHint(app.DisplayName),
                    OpenTrayRecommended: true);
            case TrayRevealAction.NotifyHotkeyUnavailable:
                return WindowCommandResult.TrayRevealFailed(app.DisplayName);
            case TrayRevealAction.NotifyTargetUnavailable:
                return new WindowCommandResult(false, $"{app.DisplayName} 的提醒窗口已关闭", NoLiveWindows: true);
            case TrayRevealAction.LaunchNewProcess:
                if (launchCooldown is not null && !launchCooldown.TryAcquire(cooldownKey))
                {
                    return new WindowCommandResult(true, $"正在启动 {app.DisplayName}");
                }
                return await LaunchAsync(app);
        }

        if (hotkey is null || !ShellSurfaceNativeMethods.SendChord(ModifierKeysOf(hotkey), (ushort)hotkey.VirtualKey))
        {
            return WindowCommandResult.TrayRevealFailed(app.DisplayName);
        }

        var deadline = Environment.TickCount64 + (long)TrayAppRevealRules.RevealTimeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(100);
            if (FindShownWindow(instancePids ?? processIds) != 0)
            {
                return new WindowCommandResult(true, $"已唤回 {app.DisplayName}");
            }
        }

        return WindowCommandResult.TrayRevealFailed(app.DisplayName);
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

    private static bool IsLiveFocusWindow(TrackedWindow window)
    {
        var handle = window.Handle;
        if (handle == 0 || !DockNativeMethods.IsWindow(handle)
            || !DockNativeMethods.IsWindowVisible(handle) || NativeMethods.IsWindowCloaked(handle))
        {
            return false;
        }

        DockNativeMethods.GetWindowThreadProcessId(handle, out var currentProcessId);
        return currentProcessId != 0 && (window.ProcessId == 0 || window.ProcessId == currentProcessId);
    }

    private static WindowCommandResult BuildFocusFailureResult(nint targetHandle, string name, bool flashOnFailure)
    {
        var targetElevated = ShellSurfaceNativeMethods.IsWindowElevated(ResolvePopupOrSelf(targetHandle));
        var foregroundElevated = !targetElevated && ShellSurfaceNativeMethods.IsForegroundElevated();
        if (flashOnFailure && !targetElevated)
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
            if (!IsLiveFocusWindow(window))
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
            if (IsVisibleEnabledPopup(window, popup))
            {
                return popup;
            }
            current = popup;
        }
        if (!DockNativeMethods.IsWindowEnabled(window))
        {
            var enabledPopup = NativeMethods.GetWindow(window, GwEnabledPopup);
            if (IsVisibleEnabledPopup(window, enabledPopup))
            {
                return enabledPopup;
            }
        }
        return window;
    }

    private static bool IsVisibleEnabledPopup(nint window, nint popup) => popup != window
        && DockNativeMethods.IsWindowVisible(popup)
        && !NativeMethods.IsWindowCloaked(popup)
        && DockNativeMethods.IsWindowEnabled(popup)
        && WindowActivationRules.IsWindowOrOwnedPopup(window, popup, ReadWindowOwner);

    private static nint ReadWindowOwner(nint window) => NativeMethods.GetWindow(window, NativeMethods.GwOwner);

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
        if (!DockNativeMethods.ProcessIdToSessionId((uint)Environment.ProcessId, out var currentSession))
        {
            return ([], hintExecutablePath);
        }

        HashSet<uint>? windowProcessIds = null;
        bool HasIndependentWindow(uint pid)
        {
            windowProcessIds ??= new WindowCatalog().Capture().Select(window => window.ProcessId).ToHashSet();
            return windowProcessIds.Contains(pid);
        }

        var (processIds, rootExecutablePath) = TrayAppRevealRules.ResolveAppInstance(
            processId,
            ReadAllParentProcessIds(),
            pid => ReadRunningProcessIdentity(pid, currentSession),
            HasIndependentWindow);
        return (processIds, rootExecutablePath ?? hintExecutablePath);
    }

    private static TrayProcessIdentity? ReadRunningProcessIdentity(uint pid, uint expectedSession)
    {
        if (pid <= 4 || !DockNativeMethods.ProcessIdToSessionId(pid, out var session) || session != expectedSession)
        {
            return null;
        }

        var process = DockNativeMethods.OpenProcess(DockNativeMethods.ProcessQueryLimitedInformation, false, pid);
        if (process == 0)
        {
            return null;
        }

        try
        {
            return DockNativeMethods.GetProcessTimes(process, out var creationTime, out _, out _, out _)
                && WindowCatalog.TryGetExecutablePath(process) is { } executablePath
                ? new TrayProcessIdentity(WindowGrouping.NormalizeExecutablePath(executablePath), creationTime)
                : null;
        }
        finally
        {
            DockNativeMethods.CloseHandle(process);
        }
    }

    private static List<ProcessHierarchyEntry> RunningProcessHierarchy(string executablePath)
    {
        var ids = RunningProcessIds(executablePath);
        return ReadRunningProcessHierarchy(ids,
            path => DockQuitRules.MatchesExecutableOrVersionSibling(executablePath, path));
    }

    private static List<ProcessHierarchyEntry> RunningHotkeySharingHierarchy(string executablePath)
    {
        var names = TrayAppRevealRules.HotkeySharingProcessNames(executablePath);
        if (names.Count == 0)
        {
            return [];
        }

        var ids = new HashSet<uint>();
        foreach (var name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    // Session and image are verified against the fresh process snapshot.
                    // A matching name whose identity cannot be read still makes the hotkey ambiguous.
                    ids.Add((uint)process.Id);
                }
            }
        }

        return ReadRunningProcessHierarchy(ids, path =>
            names.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase));
    }

    private static List<ProcessHierarchyEntry> ReadRunningProcessHierarchy(
        HashSet<uint> ids, Func<string, bool> matchesExecutable)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        if (!DockNativeMethods.ProcessIdToSessionId((uint)Environment.ProcessId, out var session))
        {
            return ids.Select(pid => new ProcessHierarchyEntry(pid, 0, IsIdentityVerified: false)).ToList();
        }

        var parents = ReadParentProcessIds(ids);
        var identityByPid = new Dictionary<uint, TrayProcessIdentity?>(ids.Count);
        foreach (var pid in ids)
        {
            if ((parents is not null && !parents.ContainsKey(pid))
                || (DockNativeMethods.ProcessIdToSessionId(pid, out var candidateSession) && candidateSession != session))
            {
                continue;
            }
            var identity = ReadRunningProcessIdentity(pid, session);
            if (identity is null || matchesExecutable(identity.Value.ExecutablePath))
            {
                identityByPid.Add(pid, identity);
            }
        }
        if (identityByPid.Values.Any(identity => identity is null))
        {
            // A process may exit between enumeration and the identity read. Only discard
            // an unreadable candidate when a complete fresh snapshot confirms it vanished.
            parents = ReadParentProcessIds(ids) ?? parents;
        }
        return TrayAppRevealRules.ConfirmRunningHierarchy(parents, identityByPid);
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

    private static Dictionary<uint, uint>? ReadParentProcessIds(HashSet<uint> targetPids)
    {
        var result = new Dictionary<uint, uint>(targetPids.Count);
        var snapshot = DockNativeMethods.CreateToolhelp32Snapshot(DockNativeMethods.Th32csSnapProcess, 0);
        if (snapshot == 0 || snapshot == DockNativeMethods.InvalidHandleValue)
        {
            return null;
        }

        try
        {
            var entry = new DockNativeMethods.ProcessEntry32W
            {
                Size = (uint)Marshal.SizeOf<DockNativeMethods.ProcessEntry32W>()
            };
            if (!DockNativeMethods.Process32FirstW(snapshot, ref entry))
            {
                return null;
            }
            do
            {
                if (targetPids.Contains(entry.ProcessId))
                {
                    result[entry.ProcessId] = entry.ParentProcessId;
                }
            }
            while (DockNativeMethods.Process32NextW(snapshot, ref entry));
            return Marshal.GetLastWin32Error() == ErrorNoMoreFiles ? result : null;
        }
        finally
        {
            DockNativeMethods.CloseHandle(snapshot);
        }

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
