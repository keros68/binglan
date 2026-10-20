using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using BingLan.App.Interop;

namespace BingLan.App.Services;

internal enum ShellSurfaceResult
{
    Opened,
    Failed,
    /// <summary>An elevated window is in front, so injected shortcuts would be dropped.</summary>
    ElevatedForeground
}

/// <summary>Opens Windows-owned surfaces without hosting or moving their controls.</summary>
internal static class ShellSurfaceService
{
    // A click outside the panel can dismiss it just before the button handler runs;
    // treat that click as "close" instead of reopening the panel.
    private const long RecentCloseMs = 400;
    private static int _openingTray;
    private static int _openingQuickLink;
    private static int _openingStart;
    private static int _watchingQuickSettings;
    private static long _quickSettingsClosedAt;

    internal static async Task<bool> OpenStartMenuAsync()
    {
        if (Interlocked.CompareExchange(ref _openingStart, 1, 0) != 0) return true;
        try
        {
            if (ShellSurfaceNativeMethods.PostStartMenuCommand())
            {
                for (var check = 0; check < 16; check++)
                {
                    await Task.Delay(50);
                    if (ShellSurfaceNativeMethods.IsStartMenuForeground()) return true;
                }
                Debug.WriteLine("开始菜单消息未确认，改用 Ctrl+Esc");
            }
            if (ShellSurfaceNativeMethods.IsStartMenuForeground()) return true;
            return await RunWhenModifiersReleasedAsync(ShellSurfaceNativeMethods.OpenStartMenu);
        }
        finally
        {
            Volatile.Write(ref _openingStart, 0);
        }
    }

    internal static async Task<ShellSurfaceResult> OpenQuickLinkMenuAsync()
    {
        if (Interlocked.CompareExchange(ref _openingQuickLink, 1, 0) != 0) return ShellSurfaceResult.Opened;
        try
        {
            // Win+X has no message equivalent; report the cause instead of a silent no-op.
            if (ShellSurfaceNativeMethods.IsForegroundElevated()) return ShellSurfaceResult.ElevatedForeground;
            return await RunWhenModifiersReleasedAsync(ShellSurfaceNativeMethods.OpenQuickLinkMenu)
                ? ShellSurfaceResult.Opened
                : ShellSurfaceResult.Failed;
        }
        finally
        {
            Volatile.Write(ref _openingQuickLink, 0);
        }
    }

    /// <summary>
    /// Shift+F10 or a modified click can still be held when WPF raises the request. Wait
    /// briefly; if the user keeps holding a modifier, ignore the request quietly rather
    /// than turn it into another shortcut or report a failure.
    /// </summary>
    private static async Task<bool> RunWhenModifiersReleasedAsync(Func<bool> action)
    {
        for (var attempt = 0; attempt < 10 && ShellSurfaceNativeMethods.HasHeldModifier(); attempt++)
        {
            await Task.Delay(50);
        }
        return ShellSurfaceNativeMethods.HasHeldModifier() || action();
    }

    internal static async Task<bool> OpenQuickSettingsAsync(string fallbackUri)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return await OpenTraySurfaceAsync(IsQuickSettingsButton, fallbackUri);
        }
        if (Interlocked.CompareExchange(ref _openingTray, 1, 0) != 0) return true;
        try
        {
            return await Task.Run(async () =>
            {
                // The top bar never takes activation, so an open panel keeps the
                // foreground: a second click closes it like the native tray button.
                if (ShellSurfaceNativeMethods.IsQuickSettingsForeground())
                {
                    ShellSurfaceNativeMethods.CloseQuickSettings();
                    return true;
                }
                if (Environment.TickCount64 - Interlocked.Read(ref _quickSettingsClosedAt) < RecentCloseMs) return true;
                // Win+A works even when auto-hide removes the taskbar's UIA buttons.
                // Input acceptance alone is not proof that the panel opened.
                var injected = !ShellSurfaceNativeMethods.IsForegroundElevated()
                    && ShellSurfaceNativeMethods.OpenQuickSettings();
                if (injected)
                {
                    if (await WaitForQuickSettingsAsync()) return true;
                    // The tray button is a toggle: invoking it now would close a panel
                    // that opened without being detected, then open Settings on top.
                    Debug.WriteLine("快速设置未确认出现，回退设置页");
                }
                else if (TryInvokeTrayButton(IsQuickSettingsButton, () => !IsQuickSettingsOpen())
                         && await WaitForQuickSettingsAsync())
                {
                    return true;
                }
                return IsQuickSettingsOpen() || OpenSettings(fallbackUri);
            });
        }
        finally
        {
            Volatile.Write(ref _openingTray, 0);
        }
    }

    internal static Task<bool> OpenInputMethodAsync() =>
        OpenTraySurfaceAsync(IsInputMethodButton, "ms-settings:keyboard");

    // Explorer's control classes are implementation details. Match the known quick-
    // settings buttons only; an unknown tree falls back to the requested Settings page.
    // In particular, OmniButtonLeft/Right open the calendar/notifications, not settings.
    internal static bool IsQuickSettingsButton(string id, string className, string name) =>
        id == "SystemTrayIcon"
        && className is "SystemTray.OmniButton" or "SystemTray.OmniButtonCenter";

    // The IME mode icon also looks like an input indicator, but invoking it toggles
    // Chinese/English instead of opening the layout picker. Do not infer from its name.
    internal static bool IsInputMethodButton(string id, string className, string name) =>
        id == "InputIndicatorButton";

    internal static bool IsQuickSettingsSurface(string id, string className, string processName) =>
        (id == "ControlCenterRegion" || className == "ControlCenterWindow")
        && processName is "ShellHost" or "ShellExperienceHost";

    private static async Task<bool> WaitForQuickSettingsAsync()
    {
        // Bound this user-triggered check; no resident polling or cached Shell objects.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(100);
            if (!IsQuickSettingsOpen()) continue;
            WatchQuickSettings();
            return true;
        }
        return false;
    }

    // Native window state first; the UIA focus chain stays as the verified fallback.
    private static bool IsQuickSettingsOpen() =>
        ShellSurfaceNativeMethods.IsQuickSettingsForeground()
        || ShellSurfaceNativeMethods.IsQuickSettingsVisible()
        || IsQuickSettingsFocused();

    /// <summary>
    /// Watches only the panel this service just opened, and only until it loses the
    /// foreground, to record when it closed. No watcher runs while the panel is closed.
    /// </summary>
    private static void WatchQuickSettings()
    {
        if (Interlocked.CompareExchange(ref _watchingQuickSettings, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                for (var check = 0; check < 3000 && ShellSurfaceNativeMethods.IsQuickSettingsForeground(); check++)
                {
                    await Task.Delay(100);
                }
                Interlocked.Exchange(ref _quickSettingsClosedAt, Environment.TickCount64);
            }
            finally
            {
                Volatile.Write(ref _watchingQuickSettings, 0);
            }
        });
    }

    private static bool IsQuickSettingsFocused()
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            for (var depth = 0; element is not null && depth < 12; depth++)
            {
                var current = element.Current;
                if (current.AutomationId == "ControlCenterRegion" || current.ClassName == "ControlCenterWindow")
                {
                    using var process = Process.GetProcessById(current.ProcessId);
                    return !current.IsOffscreen && IsQuickSettingsSurface(current.AutomationId, current.ClassName, process.ProcessName);
                }
                element = TreeWalker.RawViewWalker.GetParent(element);
            }
        }
        catch (Exception exception) when (exception is ElementNotAvailableException
            or InvalidOperationException or COMException or ArgumentException or Win32Exception)
        {
            Debug.WriteLine($"快速设置状态不可用：{exception.Message}");
        }
        return false;
    }

    private static async Task<bool> OpenTraySurfaceAsync(
        Func<string, string, string, bool> matches, string fallbackUri)
    {
        // A single UIA walk serves this user action. Repeated clicks while it runs
        // are ignored, rather than queuing toggles or starting concurrent COM walks.
        if (Interlocked.CompareExchange(ref _openingTray, 1, 0) != 0)
        {
            return true;
        }
        try
        {
            return await Task.Run(() => TryInvokeTrayButton(matches)) || OpenSettings(fallbackUri);
        }
        finally
        {
            Volatile.Write(ref _openingTray, 0);
        }
    }

    private static bool TryInvokeTrayButton(Func<string, string, string, bool> matches, Func<bool>? stillWanted = null)
    {
        // Re-read after an Explorer rebuild; there is no resident polling or cached UIA element.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var taskbar = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
                if (taskbar is null)
                {
                    return false;
                }
                foreach (AutomationElement button in taskbar.FindAll(TreeScope.Descendants,
                             new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                {
                    if (matches(button.Current.AutomationId, button.Current.ClassName, button.Current.Name)
                        && button.Current.IsEnabled
                        && button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)
                        && pattern is InvokePattern invoke)
                    {
                        // Re-check just before the toggle; a late panel must not be closed.
                        if (stillWanted is not null && !stillWanted()) return true;
                        invoke.Invoke();
                        return true;
                    }
                }
                return false;
            }
            catch (Exception exception) when (exception is ElementNotAvailableException
                or ElementNotEnabledException or InvalidOperationException or COMException)
            {
                Debug.WriteLine($"系统面板入口不可用：{exception.Message}");
            }
        }
        return false;
    }

    private static bool OpenSettings(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"系统设置无法打开：{exception.Message}");
            return false;
        }
    }
}
