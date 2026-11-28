using System.Runtime.InteropServices;
using System.Text;
using BingLan.App.Interop;
using BingLan.Core.Desktop;

namespace BingLan.App.Services;

/// <summary>
/// Multi-gate verifier deciding whether a physical screen point lies on an unobstructed,
/// empty area of the Windows desktop (not a desktop icon, not a BingLan window, not
/// covered by another app or full-screen app). If the desktop icon list can contain a
/// visible icon at the point, runs <c>AccessibleObjectFromPoint</c> on a short-lived
/// STA worker thread with a tight timeout so a slow Explorer never blocks the UI thread.
/// </summary>
internal sealed class DesktopBlankHitTester
{
    private const int MaxAncestorDepth = 16;
    private static readonly uint CurrentProcessId = (uint)Environment.ProcessId;
    private static readonly TimeSpan AccessibleProbeTimeout = TimeSpan.FromMilliseconds(200);

    // Ensures a frozen Explorer cannot accumulate hung STA probe threads.
    private int _activeProbeCount;

    internal async Task<bool> IsDesktopBlankAtAsync(int x, int y)
    {
        // Gate 1: No modifier keys held, no system menu or Win32 popup menu active,
        // and no true full-screen application running.
        if (ShellSurfaceNativeMethods.HasHeldModifier()
            || ShellSurfaceNativeMethods.IsSystemMenuForeground())
        {
            return false;
        }

        var foreground = DockNativeMethods.GetForegroundWindow();
        if (foreground != 0)
        {
            var foregroundClass = ClassNameOf(foreground);
            if (string.Equals(foregroundClass, "#32768", StringComparison.Ordinal))
            {
                return false;
            }
            if (FullScreenWatcher.IsFullScreen(foreground, targetMonitor: 0, excludeMaximized: true))
            {
                return false;
            }
        }

        // Gate 2: Resolve Explorer shell PID.
        _ = DesktopSurfaceWatcher.FindDesktopSurfaceWindow(out var shellProcessId);
        if (shellProcessId == 0 && DockNativeMethods.GetShellWindow() is var shellWindow && shellWindow != 0)
        {
            NativeMethods.GetWindowThreadProcessId(shellWindow, out shellProcessId);
        }
        if (shellProcessId == 0)
        {
            return false;
        }

        var point = new DockNativeMethods.NativePoint { X = x, Y = y };

        // Gate 3: Point under cursor must hit Explorer's desktop hierarchy (not BingLan,
        // not another app, not a file-rename Edit box, not a CabinetWClass folder window).
        var hitWindow = DesktopDoubleClickNativeMethods.WindowFromPoint(point);
        if (!TryValidateDesktopWindowHierarchy(hitWindow, shellProcessId, out var desktopRoot))
        {
            return false;
        }

        // Gate 4: Foreground window (when not the desktop itself) must not cover the point,
        // and no visible non-click-through window of another app may sit above the desktop at (x, y).
        if (IsForegroundBlockingPoint(foreground, shellProcessId, x, y)
            || IsCoveredAboveDesktop(desktopRoot, shellProcessId, x, y))
        {
            return false;
        }

        // A verified hidden icon list cannot intercept the click at a desktop background point.
        // Querying MSAA while Explorer activates that background is unnecessary.
        var hitClass = ClassNameOf(hitWindow);
        var desktopView = hitClass switch
        {
            "SHELLDLL_DefView" => hitWindow,
            "Progman" or "WorkerW" => NativeMethods.FindWindowExW(desktopRoot, 0, "SHELLDLL_DefView", null),
            _ => 0
        };
        if (DesktopDoubleClickRules.IsDesktopBlankWithHiddenIconList(
                hitClass, ReadIconListVisibility(desktopView, shellProcessId)))
        {
            return true;
        }

        // Visible or undetermined icon lists require an affirmative MSAA blank hit.
        return await ProbeBlankDesktopViaMsaaAsync(point, shellProcessId);
    }

    private static bool? ReadIconListVisibility(nint desktopView, uint shellProcessId)
    {
        if (desktopView == 0)
        {
            return null;
        }

        var iconList = NativeMethods.FindWindowExW(desktopView, 0, "SysListView32", null);
        if (iconList == 0
            || NativeMethods.GetWindowThreadProcessId(iconList, out var processId) == 0
            || processId != shellProcessId)
        {
            return null;
        }

        var visible = NativeMethods.IsWindowVisible(iconList);
        return DockNativeMethods.IsWindow(iconList) ? visible : null;
    }

    private async Task<bool> ProbeBlankDesktopViaMsaaAsync(
        DockNativeMethods.NativePoint point,
        uint shellProcessId)
    {
        if (Interlocked.CompareExchange(ref _activeProbeCount, 1, 0) != 0)
        {
            return false;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(ProbeBlankDesktopViaMsaaSync(point, shellProcessId));
            }
            catch
            {
                completion.TrySetResult(false);
            }
            finally
            {
                Interlocked.Exchange(ref _activeProbeCount, 0);
            }
        })
        {
            IsBackground = true,
            Name = "BingLan.DesktopBlankProbe"
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        var completed = await Task.WhenAny(completion.Task, Task.Delay(AccessibleProbeTimeout));
        return ReferenceEquals(completed, completion.Task) && completion.Task.Result;
    }

    private static bool ProbeBlankDesktopViaMsaaSync(
        DockNativeMethods.NativePoint point,
        uint shellProcessId)
    {
        var initHr = DesktopDoubleClickNativeMethods.CoInitializeEx(
            0,
            DesktopDoubleClickNativeMethods.CoinitApartmentThreaded);
        var shouldUninitialize = initHr is DesktopDoubleClickNativeMethods.SOk
            or DesktopDoubleClickNativeMethods.SFalse;
        if (!shouldUninitialize && initHr < 0)
        {
            return false;
        }

        DesktopDoubleClickNativeMethods.IAccessible? accessible = null;
        object? childObject = null;
        try
        {
            var hr = DesktopDoubleClickNativeMethods.AccessibleObjectFromPoint(
                point,
                out accessible,
                out childObject);
            if (hr != DesktopDoubleClickNativeMethods.SOk || accessible is null)
            {
                return false;
            }

            if (DesktopDoubleClickNativeMethods.WindowFromAccessibleObject(accessible, out var accessibleHwnd)
                    != DesktopDoubleClickNativeMethods.SOk
                || !TryValidateDesktopWindowHierarchy(accessibleHwnd, shellProcessId, out _))
            {
                return false;
            }

            int? childId = childObject is int numericChildId ? numericChildId : null;
            if (childId != DesktopDoubleClickRules.ChildIdSelf)
            {
                return false;
            }

            var roleHr = accessible.get_accRole(DesktopDoubleClickRules.ChildIdSelf, out var roleObject);
            int? role = roleHr == DesktopDoubleClickNativeMethods.SOk && roleObject is int numericRole
                ? numericRole
                : null;
            return DesktopDoubleClickRules.IsDesktopBlankAccessibleHit(childId, role);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or ExternalException)
        {
            return false;
        }
        finally
        {
            if (childObject is not null && Marshal.IsComObject(childObject))
            {
                Marshal.ReleaseComObject(childObject);
            }
            if (accessible is not null)
            {
                Marshal.ReleaseComObject(accessible);
            }
            if (shouldUninitialize)
            {
                DesktopDoubleClickNativeMethods.CoUninitialize();
            }
        }
    }

    internal static bool TryValidateDesktopWindowHierarchy(
        nint window,
        uint shellProcessId,
        out nint rootWindow)
    {
        rootWindow = 0;
        if (window == 0 || !DockNativeMethods.IsWindow(window))
        {
            return false;
        }

        var desktopWindow = DockNativeMethods.GetDesktopWindow();
        var classes = new List<string>(4);
        var current = window;
        nint last = 0;

        for (var depth = 0; depth < MaxAncestorDepth && current != 0 && current != desktopWindow; depth++)
        {
            NativeMethods.GetWindowThreadProcessId(current, out var pid);
            if (pid == 0 || pid == CurrentProcessId || pid != shellProcessId)
            {
                return false;
            }

            var className = ClassNameOf(current);
            if (className.Length == 0)
            {
                return false;
            }

            classes.Add(className);
            last = current;
            var parent = DockNativeMethods.GetAncestor(current, DesktopDoubleClickNativeMethods.GaParent);
            if (parent == current)
            {
                break;
            }
            current = parent;
        }

        if (last == 0
            || !NativeMethods.IsWindowVisible(last)
            || NativeMethods.IsWindowCloaked(last))
        {
            return false;
        }

        if (!DesktopDoubleClickRules.IsDesktopAncestorChain(classes))
        {
            return false;
        }

        rootWindow = last;
        return true;
    }

    private static bool IsForegroundBlockingPoint(nint foreground, uint shellProcessId, int x, int y)
    {
        if (foreground == 0
            || !NativeMethods.IsWindowVisible(foreground)
            || NativeMethods.IsIconic(foreground)
            || NativeMethods.IsWindowCloaked(foreground))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(foreground, out var fgPid);
        var fgClass = ClassNameOf(foreground);
        if (fgPid == shellProcessId && DesktopDoubleClickRules.IsDesktopHostClass(fgClass))
        {
            return false;
        }

        var exStyle = NativeMethods.GetExStyleOf(foreground);
        var isClickThrough = (exStyle & NativeMethods.WsExLayered) != 0
            && (exStyle & NativeMethods.WsExTransparent) != 0;
        if (isClickThrough)
        {
            return false;
        }

        return TryGetWindowBounds(foreground, out var rect) && ContainsPoint(rect, x, y);
    }

    private static bool IsCoveredAboveDesktop(nint desktopRoot, uint shellProcessId, int x, int y)
    {
        var covered = false;
        NativeMethods.EnumWindows((window, _) =>
        {
            if (window == desktopRoot)
            {
                return false;
            }

            if (!NativeMethods.IsWindowVisible(window)
                || NativeMethods.IsIconic(window)
                || NativeMethods.IsWindowCloaked(window))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(window, out var pid);
            // Own windows are already authoritatively hit-tested by WindowFromPoint (including
            // per-pixel alpha and HTTRANSPARENT regions such as edge-docked strips).
            if (pid == CurrentProcessId)
            {
                return true;
            }

            if (pid == shellProcessId && DesktopDoubleClickRules.IsDesktopHostClass(ClassNameOf(window)))
            {
                return true;
            }

            var exStyle = NativeMethods.GetExStyleOf(window);
            if ((exStyle & NativeMethods.WsExLayered) != 0)
            {
                // Layered windows may have transparent cutouts or WS_EX_TRANSPARENT; WindowFromPoint
                // already rejects opaque pixels of layered windows.
                return true;
            }

            if (TryGetWindowBounds(window, out var rect) && ContainsPoint(rect, x, y))
            {
                covered = true;
                return false;
            }

            return true;
        }, 0);
        return covered;
    }

    private static bool TryGetWindowBounds(nint window, out DockNativeMethods.NativeRect bounds)
    {
        if (DockNativeMethods.DwmGetWindowAttribute(
                window,
                DockNativeMethods.DwmwaExtendedFrameBounds,
                out bounds,
                Marshal.SizeOf<DockNativeMethods.NativeRect>()) == 0
            && bounds.Right > bounds.Left
            && bounds.Bottom > bounds.Top)
        {
            return true;
        }

        return DockNativeMethods.GetWindowRect(window, out bounds)
            && bounds.Right > bounds.Left
            && bounds.Bottom > bounds.Top;
    }

    private static bool ContainsPoint(DockNativeMethods.NativeRect rect, int x, int y) =>
        x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;

    private static string ClassNameOf(nint window)
    {
        var buffer = new StringBuilder(64);
        return DockNativeMethods.GetClassNameW(window, buffer, buffer.Capacity) > 0
            ? buffer.ToString()
            : string.Empty;
    }
}
