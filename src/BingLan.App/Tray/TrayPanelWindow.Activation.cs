using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;

namespace BingLan.App.Tray;

internal sealed partial class TrayPanelWindow
{
    internal static async Task<bool?> ActivateFeishuAsync(uint processId, string executablePath, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var processMainWindow = ReadProcessMainWindow(processId);
        if (processMainWindow == 0)
        {
            return null;
        }

        var windows = new WindowCatalog().Capture()
            .Where(window => window.ProcessId == processId
                && string.Equals(window.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (windows is not [var target] || target.Handle != processMainWindow
            || Application.Current?.Dispatcher is not { } dispatcher)
        {
            return null;
        }

        return await dispatcher.InvokeAsync(async Task<bool?> () =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (DockNativeMethods.GetWindowThreadProcessId(target.Handle, out var currentProcessId) == 0
                || currentProcessId != processId
                || !WindowCatalog.ShouldInclude(target.Handle, DockNativeMethods.GetShellWindow()))
            {
                return null;
            }

            var title = new StringBuilder(8);
            DockNativeMethods.GetWindowTextW(target.Handle, title, title.Capacity);
            if (!IsFeishuMainWindow(title.ToString(), Native.ClassName(target.Handle)))
            {
                return null;
            }

            var result = await WindowCommandService.FocusAsync(
                target, DockNativeMethods.IsIconic(target.Handle), flashOnFailure: false);
            return result.Succeeded;
        }, DispatcherPriority.Input, cancellation).Task.Unwrap();
    }

    // The class is shared with other Feishu surfaces. Require the exact observed primary title too.
    internal static bool IsFeishuMainWindow(string title, string windowClass) =>
        title == "飞书" && windowClass == "Chrome_WidgetWin_1";

    private static nint ReadProcessMainWindow(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.MainWindowHandle;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return 0;
        }
    }
}
