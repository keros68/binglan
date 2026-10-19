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

    internal static bool OpenStartMenu() => ShellSurfaceNativeMethods.OpenStartMenu();

    internal static Task<bool> OpenQuickSettingsAsync(string fallbackUri) =>
        OpenTraySurfaceAsync(IsQuickSettingsButton, fallbackUri);

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
