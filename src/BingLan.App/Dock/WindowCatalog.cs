using System.Runtime.InteropServices;
using System.Text;
using BingLan.App.Interop;
using BingLan.Core.Dock;

namespace BingLan.App.Dock;

internal sealed class WindowCatalog
{
    private const int ErrorInsufficientBuffer = 122;
    private const long NullAumidRetryMs = 5000;
    private readonly object _cacheGate = new();
    private readonly Dictionary<uint, (long CreationTime, ProcessIdentity Identity)> _processCache = [];
    private readonly Dictionary<nint, (uint ProcessId, string? AppUserModelId, long CheckedTick)> _windowAumidCache = [];

    internal IReadOnlyList<TrackedWindow> Capture()
    {
        var windows = new List<TrackedWindow>();
        var foreground = DockNativeMethods.GetForegroundWindow();
        var foregroundRootOwner = GetRootOwnerOrSelf(foreground);
        var shell = DockNativeMethods.GetShellWindow();
        var identities = new Dictionary<uint, ProcessIdentity>();
        var seenWindows = new HashSet<nint>();

        lock (_cacheGate)
        {
            DockNativeMethods.EnumWindows((window, _) =>
            {
                if (!ShouldInclude(window, shell))
                {
                    return true;
                }

                // The app's own cards, dock and guides are tool windows and already left out;
                // its settings window is an ordinary window and shows like any other app.
                DockNativeMethods.GetWindowThreadProcessId(window, out var processId);
                if (processId == 0)
                {
                    return true;
                }

                seenWindows.Add(window);
                if (!identities.TryGetValue(processId, out var identity))
                {
                    identity = ResolveCachedProcessIdentity(processId);
                    identities.Add(processId, identity);
                }

                var windowAumid = ResolveCachedWindowAppUserModelId(window, processId, identity.ExecutablePath);
                windows.Add(new TrackedWindow(
                    window,
                    processId,
                    GetTitle(window),
                    windowAumid ?? identity.AppUserModelId,
                    identity.ExecutablePath,
                    window == foreground
                        || GetRootOwnerOrSelf(window) == foregroundRootOwner,
                    DockNativeMethods.IsIconic(window)));
                return true;
            }, 0);

            if (_windowAumidCache.Count > seenWindows.Count)
            {
                foreach (var staleWindow in _windowAumidCache.Keys.Where(handle => !seenWindows.Contains(handle)).ToArray())
                {
                    _windowAumidCache.Remove(staleWindow);
                }
            }

            if (_processCache.Count > identities.Count)
            {
                foreach (var stalePid in _processCache.Keys.Where(pid => !identities.ContainsKey(pid)).ToArray())
                {
                    _processCache.Remove(stalePid);
                }
            }
        }

        return windows;
    }

    private ProcessIdentity ResolveCachedProcessIdentity(uint processId)
    {
        var process = DockNativeMethods.OpenProcess(
            DockNativeMethods.ProcessQueryLimitedInformation,
            false,
            processId);
        if (process == 0)
        {
            _processCache.Remove(processId);
            return new ProcessIdentity(null, null);
        }

        try
        {
            var hasCreationTime = DockNativeMethods.GetProcessTimes(
                process, out var creationTime, out _, out _, out _);
            if (hasCreationTime
                && _processCache.TryGetValue(processId, out var cached)
                && cached.CreationTime == creationTime)
            {
                return cached.Identity;
            }

            var identity = new ProcessIdentity(
                TryGetAppUserModelId(process),
                TryGetExecutablePath(process));
            if (hasCreationTime)
            {
                _processCache[processId] = (creationTime, identity);
            }
            return identity;
        }
        finally
        {
            DockNativeMethods.CloseHandle(process);
        }
    }

    private string? ResolveCachedWindowAppUserModelId(nint window, uint processId, string? executablePath)
    {
        var nowTick = Environment.TickCount64;
        if (_windowAumidCache.TryGetValue(window, out var cached)
            && cached.ProcessId == processId
            && (cached.AppUserModelId is not null
                || (!DockAppResolver.IsFrameHost(executablePath) && nowTick - cached.CheckedTick < NullAumidRetryMs)))
        {
            return cached.AppUserModelId;
        }

        var resolved = TryGetWindowAppUserModelId(window);
        if (resolved is not null || !DockAppResolver.IsFrameHost(executablePath))
        {
            _windowAumidCache[window] = (processId, resolved, nowTick);
        }
        return resolved;
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

    internal static bool ShouldInclude(nint window, nint shell)
    {
        if (window == 0
            || window == shell
            || !DockNativeMethods.IsWindowVisible(window))
        {
            return false;
        }

        var extendedStyle = (long)NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlExStyle);
        var appWindow = (extendedStyle & NativeMethods.WsExAppWindow) != 0;
        if (!appWindow
            && ((extendedStyle & NativeMethods.WsExToolWindow) != 0
                || (extendedStyle & DockNativeMethods.WsExNoActivate) != 0))
        {
            return false;
        }

        if (DockNativeMethods.DwmGetWindowAttribute(
                window,
                DockNativeMethods.DwmwaCloaked,
                out int cloaked,
                sizeof(int)) == 0
            && cloaked != 0)
        {
            return false;
        }

        if (appWindow)
        {
            return true;
        }

        var rootOwner = DockNativeMethods.GetAncestor(window, DockNativeMethods.GaRootOwner);
        if (rootOwner == 0)
        {
            return true;
        }

        var representative = rootOwner;
        while (true)
        {
            var popup = DockNativeMethods.GetLastActivePopup(representative);
            if (popup == representative)
            {
                break;
            }
            representative = popup;
            if (DockNativeMethods.IsWindowVisible(representative))
            {
                break;
            }
        }

        return window == representative;
    }

    private static string GetTitle(nint window)
    {
        var length = DockNativeMethods.GetWindowTextLengthW(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        var title = new StringBuilder(length + 1);
        DockNativeMethods.GetWindowTextW(window, title, title.Capacity);
        return title.ToString().Trim();
    }

    // Packaged apps hosted by ApplicationFrameHost and browser web apps publish their
    // identity on the window rather than on the process that owns it.
    private static string? TryGetWindowAppUserModelId(nint window)
    {
        DockNativeMethods.IPropertyStore? store = null;
        try
        {
            if (DockNativeMethods.SHGetPropertyStoreForWindow(
                    window,
                    DockNativeMethods.PropertyStoreId,
                    out store) != 0)
            {
                return null;
            }

            if (store.GetValue(DockNativeMethods.AppUserModelIdKey, out var value) != 0)
            {
                return null;
            }

            try
            {
                return value.ValueType == DockNativeMethods.VtLpwstr && value.Value != 0
                    ? NullIfBlank(Marshal.PtrToStringUni(value.Value))
                    : null;
            }
            finally
            {
                DockNativeMethods.PropVariantClear(ref value);
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (store is not null)
            {
                Marshal.ReleaseComObject(store);
            }
        }
    }

    internal static string? ExecutablePathOf(uint processId)
    {
        var process = DockNativeMethods.OpenProcess(
            DockNativeMethods.ProcessQueryLimitedInformation,
            false,
            processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            return TryGetExecutablePath(process);
        }
        finally
        {
            DockNativeMethods.CloseHandle(process);
        }
    }

    private static string? TryGetExecutablePath(nint process)
    {
        var buffer = new StringBuilder(512);
        var size = (uint)buffer.Capacity;
        if (DockNativeMethods.QueryFullProcessImageNameW(process, 0, buffer, ref size))
        {
            return buffer.ToString();
        }
        if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
        {
            return null;
        }
        buffer.EnsureCapacity(32768);
        size = (uint)buffer.Capacity;
        return DockNativeMethods.QueryFullProcessImageNameW(process, 0, buffer, ref size)
            ? buffer.ToString()
            : null;
    }

    private static string? TryGetAppUserModelId(nint process)
    {
        uint length = 0;
        var result = DockNativeMethods.GetApplicationUserModelId(process, ref length, null);
        if (result != ErrorInsufficientBuffer || length == 0)
        {
            return null;
        }

        var buffer = new StringBuilder((int)length);
        result = DockNativeMethods.GetApplicationUserModelId(process, ref length, buffer);
        return result == 0 ? NullIfBlank(buffer.ToString()) : null;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ProcessIdentity(string? AppUserModelId, string? ExecutablePath);
}
