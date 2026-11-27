using System.Runtime.InteropServices;
using System.Text;
using BingLan.Core.Dock;

namespace BingLan.App.Interop;

internal readonly record struct BackingWindowContext(
    nint Handle,
    PixelRect Bounds,
    bool IsZoomed,
    nint MonitorHandle);

/// <summary>
/// Resolves the underlying host application window when the live foreground is a transient
/// context menu (<c>#32768</c>), custom popup menu, dialog, tool overlay, or momentarily
/// <c>0</c> during Win32 menu mode. Shared by the smart-hide Dock and TopBar so opening a
/// right-click menu inside a maximized or overlapping app does not pop either bar up.
/// </summary>
internal static class TransientForegroundResolver
{
    private static readonly uint CurrentProcessId = (uint)Environment.ProcessId;
    private const uint GuiInMenuMode = 0x00000004;
    private const uint GuiPopupMenuMode = 0x00000010;

    internal static bool TryResolveBackingWindow(
        nint foregroundWindow,
        nint ownWindowToIgnore,
        out BackingWindowContext backing)
    {
        backing = default;

        var inMenuMode = TryReadMenuOwner(out var menuOwner, out var activeWindow);
        if (foregroundWindow == 0)
        {
            if (inMenuMode)
            {
                var menuTarget = menuOwner != 0 ? menuOwner : activeWindow;
                if (TryBuildContext(menuTarget, ownWindowToIgnore, out backing))
                {
                    return true;
                }
            }
            return false;
        }

        if (foregroundWindow == ownWindowToIgnore || DockNativeMethods.IsIconic(foregroundWindow))
        {
            return false;
        }

        DockNativeMethods.GetWindowThreadProcessId(foregroundWindow, out var foregroundPid);
        if (foregroundPid == 0 || foregroundPid == CurrentProcessId)
        {
            return false;
        }

        var fgClass = ClassNameOf(foregroundWindow);
        if (ShellSurfaceWindows.IsShellSurface(fgClass))
        {
            return false;
        }

        var isTransient = IsTransientPopupWindow(foregroundWindow, fgClass);
        if (!isTransient && !(inMenuMode && menuOwner != 0 && menuOwner != foregroundWindow))
        {
            return false;
        }

        // 1. Direct menu owner from GUI thread info (covers Win32 #32768 and owned popups).
        if (inMenuMode && menuOwner != 0 && menuOwner != foregroundWindow
            && TryBuildContext(menuOwner, ownWindowToIgnore, out backing))
        {
            return true;
        }

        // 2. Owner chain (GW_OWNER / GA_ROOTOWNER).
        var visited = new HashSet<nint> { foregroundWindow };
        var owner = NativeMethods.GetWindow(foregroundWindow, NativeMethods.GwOwner);
        BackingWindowContext? fallbackOwner = null;
        for (var hop = 0; hop < 8 && owner != 0 && visited.Add(owner); hop++)
        {
            if (TryBuildContext(owner, ownWindowToIgnore, out var candidate))
            {
                if (!IsTransientPopupWindow(candidate.Handle, ClassNameOf(candidate.Handle)))
                {
                    backing = candidate;
                    return true;
                }
                fallbackOwner ??= candidate;
            }
            owner = NativeMethods.GetWindow(owner, NativeMethods.GwOwner);
        }

        var rootOwner = DockNativeMethods.GetAncestor(foregroundWindow, DockNativeMethods.GaRootOwner);
        if (rootOwner != 0
            && rootOwner != foregroundWindow
            && TryBuildContext(rootOwner, ownWindowToIgnore, out var rootContext)
            && !IsTransientPopupWindow(rootContext.Handle, ClassNameOf(rootContext.Handle)))
        {
            backing = rootContext;
            return true;
        }

        if (fallbackOwner is { } resolvedFallback)
        {
            backing = resolvedFallback;
            return true;
        }

        // 3. Unowned same-process transient popup/custom context menu (e.g. CorelDRAW/Qt/Chromium):
        // scan immediately below in Z-order for the same process's visible host window, stopping
        // at any unrelated non-shell application window.
        var explorerPid = ReadExplorerProcessId();
        var cursor = NativeMethods.GetWindow(foregroundWindow, NativeMethods.GwHwndNext);
        var steps = 0;
        var visibleChecked = 0;
        while (cursor != 0 && steps++ < 128 && visibleChecked < 32)
        {
            if (!DockNativeMethods.IsWindowVisible(cursor)
                || DockNativeMethods.IsIconic(cursor)
                || NativeMethods.IsWindowCloaked(cursor))
            {
                cursor = NativeMethods.GetWindow(cursor, NativeMethods.GwHwndNext);
                continue;
            }

            DockNativeMethods.GetWindowThreadProcessId(cursor, out var candidatePid);
            if (candidatePid == 0 || candidatePid == CurrentProcessId)
            {
                cursor = NativeMethods.GetWindow(cursor, NativeMethods.GwHwndNext);
                continue;
            }

            var exStyle = NativeMethods.GetExStyleOf(cursor);
            if ((exStyle & (NativeMethods.WsExToolWindow | NativeMethods.WsExTransparent)) != 0)
            {
                cursor = NativeMethods.GetWindow(cursor, NativeMethods.GwHwndNext);
                continue;
            }

            var candidateClass = ClassNameOf(cursor);
            if (ShellSurfaceWindows.IsShellSurface(candidateClass))
            {
                cursor = NativeMethods.GetWindow(cursor, NativeMethods.GwHwndNext);
                continue;
            }

            visibleChecked++;
            if (candidatePid == foregroundPid)
            {
                if (!IsTransientPopupWindow(cursor, candidateClass)
                    && TryBuildContext(cursor, ownWindowToIgnore, out backing))
                {
                    return true;
                }
            }
            else if ((explorerPid == 0 || candidatePid != explorerPid)
                && !IsTransientPopupWindow(cursor, candidateClass))
            {
                break;
            }

            cursor = NativeMethods.GetWindow(cursor, NativeMethods.GwHwndNext);
        }

        return false;
    }

    /// <summary>
    /// Scans visible ordinary application windows on <paramref name="monitorHandle"/> (stopping
    /// at the desktop surface) to detect any visible maximized or bar-overlapping window even
    /// when focus temporarily moved to the taskbar, another monitor, or a small floating dialog.
    /// Skips tool/click-through overlays (including BingLan desktop cards, Dock, handle, and bars).
    /// </summary>
    internal static bool TryFindCoveringWindowOnMonitor(
        nint monitorHandle,
        PixelRect barBounds,
        nint ownWindowToIgnore,
        out bool anyOverlapsBar,
        out bool anyMaximizedOnMonitor,
        out PixelRect coveringBounds)
    {
        anyOverlapsBar = false;
        anyMaximizedOnMonitor = false;
        coveringBounds = default;
        if (monitorHandle == 0 || !barBounds.HasArea)
        {
            return false;
        }

        var localOverlaps = false;
        var localMaximized = false;
        PixelRect localBounds = default;
        var visibleChecked = 0;

        NativeMethods.EnumWindows((window, _) =>
        {
            if (window == 0 || window == ownWindowToIgnore)
            {
                return true;
            }
            if (!DockNativeMethods.IsWindowVisible(window)
                || DockNativeMethods.IsIconic(window)
                || NativeMethods.IsWindowCloaked(window))
            {
                return true;
            }

            var className = ClassNameOf(window);
            if (className is "Progman" or "WorkerW")
            {
                return false;
            }
            if (ShellSurfaceWindows.IsShellSurface(className))
            {
                return true;
            }

            var exStyle = NativeMethods.GetExStyleOf(window);
            if ((exStyle & (NativeMethods.WsExToolWindow | NativeMethods.WsExTransparent)) != 0)
            {
                return true;
            }

            visibleChecked++;
            if (visibleChecked > 64)
            {
                return false;
            }

            if (DockNativeMethods.MonitorFromWindow(window, DockNativeMethods.MonitorDefaultToNearest) != monitorHandle)
            {
                return true;
            }

            if (DockNativeMethods.DwmGetWindowAttribute(
                    window,
                    DockNativeMethods.DwmwaExtendedFrameBounds,
                    out DockNativeMethods.NativeRect rectangle,
                    Marshal.SizeOf<DockNativeMethods.NativeRect>()) != 0
                && !DockNativeMethods.GetWindowRect(window, out rectangle))
            {
                return true;
            }

            var rect = new PixelRect(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
            if (!rect.HasArea)
            {
                return true;
            }

            var zoomed = DockNativeMethods.IsZoomed(window);
            var intersects = rect.Intersects(barBounds);
            if (zoomed || intersects)
            {
                if (!localBounds.HasArea)
                {
                    localBounds = rect;
                }
                if (intersects || zoomed)
                {
                    localOverlaps = true;
                }
                if (zoomed)
                {
                    localMaximized = true;
                }
            }

            return !(localOverlaps && localMaximized);
        }, 0);

        anyOverlapsBar = localOverlaps;
        anyMaximizedOnMonitor = localMaximized;
        coveringBounds = localBounds;
        return localOverlaps || localMaximized;
    }

    private static bool TryReadMenuOwner(out nint menuOwner, out nint activeWindow)
    {
        menuOwner = 0;
        activeWindow = 0;
        var info = new TopBarNativeMethods.GuiThreadInfo
        {
            Size = Marshal.SizeOf<TopBarNativeMethods.GuiThreadInfo>()
        };
        if (!TopBarNativeMethods.GetGUIThreadInfo(0, ref info))
        {
            return false;
        }
        menuOwner = info.HwndMenuOwner;
        activeWindow = info.HwndActive;
        return (info.Flags & (GuiInMenuMode | GuiPopupMenuMode)) != 0;
    }

    private static bool TryBuildContext(
        nint rawWindow,
        nint ownWindowToIgnore,
        out BackingWindowContext context)
    {
        context = default;
        if (rawWindow == 0)
        {
            return false;
        }

        var window = NativeMethods.IsChildWindow(rawWindow)
            ? DockNativeMethods.GetAncestor(rawWindow, DockNativeMethods.GaRootOwner)
            : rawWindow;
        if (window == 0
            || window == ownWindowToIgnore
            || !DockNativeMethods.IsWindowVisible(window)
            || DockNativeMethods.IsIconic(window)
            || NativeMethods.IsWindowCloaked(window))
        {
            return false;
        }

        DockNativeMethods.GetWindowThreadProcessId(window, out var pid);
        if (pid == 0 || pid == CurrentProcessId)
        {
            return false;
        }

        var className = ClassNameOf(window);
        if (ShellSurfaceWindows.IsShellSurface(className))
        {
            return false;
        }

        if (DockNativeMethods.DwmGetWindowAttribute(
                window,
                DockNativeMethods.DwmwaExtendedFrameBounds,
                out DockNativeMethods.NativeRect rectangle,
                Marshal.SizeOf<DockNativeMethods.NativeRect>()) != 0
            && !DockNativeMethods.GetWindowRect(window, out rectangle))
        {
            return false;
        }

        var bounds = new PixelRect(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
        if (!bounds.HasArea)
        {
            return false;
        }

        context = new BackingWindowContext(
            window,
            bounds,
            DockNativeMethods.IsZoomed(window),
            DockNativeMethods.MonitorFromWindow(window, DockNativeMethods.MonitorDefaultToNearest));
        return true;
    }

    private static bool IsTransientPopupWindow(nint window, string className)
    {
        var style = (long)NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlStyle);
        var exStyle = NativeMethods.GetExStyleOf(window);
        return DockVacateRules.IsTransientPopupForSmartHide(
            className,
            isZoomed: DockNativeMethods.IsZoomed(window),
            isPopup: (style & NativeMethods.WsPopup) != 0,
            isToolWindow: (exStyle & NativeMethods.WsExToolWindow) != 0,
            hasStandardResizableFrame: (style & NativeMethods.WsCaption) == NativeMethods.WsCaption
                && (style & NativeMethods.WsThickFrame) != 0,
            isAppWindow: (exStyle & NativeMethods.WsExAppWindow) != 0);
    }

    private static string ClassNameOf(nint window)
    {
        var buffer = new StringBuilder(64);
        return DockNativeMethods.GetClassNameW(window, buffer, buffer.Capacity) > 0
            ? buffer.ToString()
            : string.Empty;
    }

    private static uint ReadExplorerProcessId()
    {
        var tray = TaskbarNativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (tray == 0)
        {
            return 0;
        }
        DockNativeMethods.GetWindowThreadProcessId(tray, out var pid);
        return pid;
    }
}
