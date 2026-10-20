using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using BingLan.App.Interop;

namespace BingLan.App.Services;

/// <summary>Opens Windows-owned surfaces without hosting or moving their controls.</summary>
internal static class ShellSurfaceService
{
    private static int _openingTray;
    private static int _openingQuickLink;

    internal static bool OpenStartMenu() => ShellSurfaceNativeMethods.OpenStartMenu();

    internal static async Task<bool> OpenQuickLinkMenuAsync()
    {
        if (Interlocked.CompareExchange(ref _openingQuickLink, 1, 0) != 0) return true;
        try
        {
            // Shift+F10 can still be held when WPF raises the context-menu request.
            for (var attempt = 0; attempt < 10 && ShellSurfaceNativeMethods.HasHeldModifier(); attempt++)
            {
                await Task.Delay(50);
            }
            return ShellSurfaceNativeMethods.OpenQuickLinkMenu();
        }
        finally
        {
            Volatile.Write(ref _openingQuickLink, 0);
        }
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
                if (IsQuickSettingsFocused()) return true;
                // Win+A works even when auto-hide removes the taskbar's UIA buttons.
                // Input acceptance alone is not proof that the panel opened.
                if (ShellSurfaceNativeMethods.OpenQuickSettings() && await WaitForQuickSettingsAsync()) return true;
                if (TryInvokeTrayButton(IsQuickSettingsButton) && await WaitForQuickSettingsAsync()) return true;
                return OpenSettings(fallbackUri);
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
        for (var attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(100);
            if (IsQuickSettingsFocused()) return true;
        }
        return false;
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

    private static bool TryInvokeTrayButton(Func<string, string, string, bool> matches)
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
