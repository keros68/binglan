using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.Core.Dock;

namespace BingLan.App.Tray;

internal sealed partial class TrayPanelWindow
{
    private const string ClashVergeAppId = "io.github.clash-verge-rev.clash-verge-rev";
    private const int MaximumClashConfigBytes = 256 * 1024;
    private const int SwShowNa = 8;

    internal static bool IsClashVergeMainWindow(string title, string windowClass) =>
        title == TrayIconArtwork.ClashVergeName && windowClass == "Tauri Window";

    internal static Task<bool?> ActivateTrayAppAsync(TrayActivationOwner owner, CancellationToken cancellation) =>
        TrayIconArtwork.IsClashVergeExecutable(owner.ExecutablePath)
            ? ActivateClashVergeAsync(owner, cancellation)
            : ActivateFeishuAsync(owner.ProcessId, owner.ExecutablePath, cancellation);

    private static async Task<bool?> ActivateClashVergeAsync(TrayActivationOwner owner, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        // The default tray action in this release only shows/focuses the existing window.
        // Lightweight mode destroys that window and needs the application's own tray action.
        if (!UsesClashVergeMainWindowAction(owner.ExecutablePath)
            || FindClashVergeMainWindow(owner.ProcessId) is not { } target
            || Application.Current?.Dispatcher is not { } dispatcher)
        {
            return null;
        }

        return await dispatcher.InvokeAsync(async Task<bool?> () =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (!IsClashVergeWindow(target, owner.ProcessId)
                || Native.GetWindowThreadProcessId(owner.Window, out var iconProcessId) == 0
                || iconProcessId != owner.ProcessId)
            {
                return null;
            }

            if (!DockNativeMethods.IsWindowVisible(target))
            {
                if (!DockNativeMethods.ShowWindowAsync(target, SwShowNa)) return null;
                // Only the target thread can complete an asynchronous show.
                var deadline = Environment.TickCount64 + 250;
                while (!DockNativeMethods.IsWindowVisible(target) && Environment.TickCount64 < deadline)
                {
                    await Task.Delay(15, cancellation);
                }
                if (!DockNativeMethods.IsWindowVisible(target) || !IsClashVergeWindow(target, owner.ProcessId))
                    return false;
            }

            var minimized = DockNativeMethods.IsIconic(target);
            var window = new TrackedWindow(target, owner.ProcessId, TrayIconArtwork.ClashVergeName, null, owner.ExecutablePath,
                DockNativeMethods.GetForegroundWindow() == target, minimized);
            cancellation.ThrowIfCancellationRequested();
            var result = await WindowCommandService.FocusAsync(window, minimized, flashOnFailure: false);
            return result.Succeeded;
        }, DispatcherPriority.Input, cancellation).Task.Unwrap();
    }

    private static nint? FindClashVergeMainWindow(uint processId)
    {
        nint? match = null;
        Native.EnumWindows((window, _) =>
        {
            if (!IsClashVergeWindow(window, processId)) return true;
            if (match is null)
            {
                match = window;
                return true;
            }
            match = null;
            return false;
        }, 0);
        return match;
    }

    private static bool IsClashVergeWindow(nint window, uint processId)
    {
        if (DockNativeMethods.GetWindowThreadProcessId(window, out var ownerPid) == 0 || ownerPid != processId
            || DockNativeMethods.GetAncestor(window, DockNativeMethods.GaRootOwner) != window
            || NativeMethods.IsWindowCloaked(window))
        {
            return false;
        }
        var title = new StringBuilder(64);
        DockNativeMethods.GetWindowTextW(window, title, title.Capacity);
        return IsClashVergeMainWindow(title.ToString(), Native.ClassName(window));
    }

    private static bool UsesClashVergeMainWindowAction(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            if (version.ProductName != TrayIconArtwork.ClashVergeName || version.ProductVersion != TrayIconArtwork.ClashVergeVersion)
                return false;
            // v2.5.10 uses this standard per-user folder. Require the exact emitted scalar.
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!Path.IsPathFullyQualified(appData)) return false;
            using var stream = new FileStream(Path.Combine(appData, ClashVergeAppId, "verge.yaml"), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is <= 0 or > MaximumClashConfigBytes) return false;
            using var config = new StreamReader(stream, new UTF8Encoding(false, throwOnInvalidBytes: true));
            return ReadsClashVergeMainWindowAction(config);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or SecurityException or Win32Exception or DecoderFallbackException)
        {
            return false;
        }
    }

    internal static bool ReadsClashVergeMainWindowAction(TextReader config)
    {
        var found = false;
        while (config.ReadLine() is { } line)
        {
            if (!line.Contains("tray_event", StringComparison.Ordinal)) continue;
            if (found || line is not ("tray_event: null" or "tray_event: main_window")) return false;
            found = true;
        }
        return found;
    }

    private static async Task<bool?> ActivateFeishuAsync(uint processId, string executablePath, CancellationToken cancellation)
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
