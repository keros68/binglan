using System.Runtime.InteropServices;
using BingLan.App.Dock;
using BingLan.App.Taskbar;
using BingLan.Core.Dock;
using BingLan.Core.Taskbar;
using CoreRect = BingLan.Core.Dock.PixelRect;

namespace BingLan.App.Interop;

internal static class TaskbarEdgeGuard
{
    internal const int RevealInsetPixels = 8;

    private const uint AbmGetAutoHideBarEx = 0x0000000B;
    private const uint AbeLeft = 0;
    private const uint AbeTop = 1;
    private const uint AbeRight = 2;
    private const uint AbeBottom = 3;
    private const uint MonitorDefaultToNull = 0;
    private const uint MonitorDefaultToNearest = 2;

    internal sealed record NativeTaskbarLayout(DockEdge Edge, CoreRect Bounds, bool AutoHidden, nint Window = 0);

    // Shared by the top bar's placement and foreground checks. Half a second bounds
    // enumeration cost; owning layout notifications invalidate before repositioning.
    private static readonly Dictionary<CoreRect, (long ReadAt, NativeTaskbarLayout? Layout)> NativeLayouts = [];

    internal static void InvalidateNativeTaskbarLayout(CoreRect monitor) => NativeLayouts.Remove(monitor);

    internal static NativeTaskbarLayout? GetNativeTaskbarLayout(CoreRect monitor)
    {
        var now = Environment.TickCount64;
        if (NativeLayouts.TryGetValue(monitor, out var cached) && now - cached.ReadAt < 500)
            return cached.Layout;

        NativeTaskbarLayout? layout = null;
        foreach (var window in TaskbarAdapter.FindTaskbars())
        {
            if (!DockNativeMethods.GetWindowRect(window, out var rectangle)) continue;
            var bounds = new CoreRect(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
            for (uint edge = AbeLeft; edge <= AbeBottom; edge++)
            {
                var data = new AppBarData
                {
                    Size = Marshal.SizeOf<AppBarData>(), Edge = edge,
                    Rectangle = new PixelRect { Left = monitor.Left, Top = monitor.Top, Right = monitor.Right, Bottom = monitor.Bottom }
                };
                if (SHAppBarMessage(AbmGetAutoHideBarEx, ref data) == window)
                {
                    layout = new NativeTaskbarLayout((DockEdge)edge, bounds, true, window);
                    break;
                }
            }
            if (layout is not null) break;
            var nativeMonitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(DockNativeMethods.MonitorFromWindow(window, MonitorDefaultToNearest), ref nativeMonitor)
                || nativeMonitor.MonitorArea.Left != monitor.Left || nativeMonitor.MonitorArea.Top != monitor.Top
                || nativeMonitor.MonitorArea.Right != monitor.Right || nativeMonitor.MonitorArea.Bottom != monitor.Bottom)
                continue;
            var workArea = nativeMonitor.WorkArea;
            if (TaskbarLayoutRules.GetEdge(bounds, monitor,
                new CoreRect(workArea.Left, workArea.Top, workArea.Right, workArea.Bottom)) is { } confirmedEdge)
            {
                layout = new NativeTaskbarLayout(confirmedEdge, bounds, false, window);
                break;
            }
        }
        // Display arrangements are few; discard old monitor keys after reconfiguration.
        if (NativeLayouts.Count > 32) NativeLayouts.Clear();
        NativeLayouts[monitor] = (now, layout);
        return layout;
    }

    /// <summary>
    /// Resolves the native taskbar's monitor edge for dock-yield decisions, falling back to
    /// geometry inference when a third-party side-docked or floating taskbar does not match
    /// the strict full-edge <see cref="TaskbarLayoutRules.GetEdge"/> check.
    /// </summary>
    internal static DockEdge? InferNativeTaskbarEdge(CoreRect monitor, CoreRect? foregroundTaskbarBounds = null)
    {
        if (GetNativeTaskbarLayout(monitor) is { } layout)
        {
            return layout.Edge;
        }

        if (foregroundTaskbarBounds is { HasArea: true } fgBounds
            && TaskbarLayoutRules.InferDockedEdge(fgBounds, monitor) is { } fgEdge)
        {
            return fgEdge;
        }

        foreach (var window in TaskbarAdapter.FindTaskbars())
        {
            if (DockNativeMethods.IsWindowVisible(window)
                && DockNativeMethods.GetWindowRect(window, out var rect))
            {
                var bounds = new CoreRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
                if (TaskbarLayoutRules.InferDockedEdge(bounds, monitor) is { } inferred)
                {
                    return inferred;
                }
            }
        }

        return null;
    }

    internal static bool IsVisibleTaskbarOverlapping(MonitorSnapshot monitor, CoreRect bounds)
    {
        if (!bounds.HasArea)
        {
            return false;
        }

        // An auto-hide bar can slide across a neighboring monitor's bounds. Use its
        // registered monitor when known, but read the moving rectangle on every check.
        var window = GetNativeTaskbarLayout(monitor.Bounds)?.Window ?? 0;
        if (window != 0 && DockNativeMethods.IsWindow(window))
        {
            return VisibleWindowOverlaps(window, bounds);
        }

        foreach (var taskbar in TaskbarAdapter.FindTaskbars())
        {
            if (DockNativeMethods.MonitorFromWindow(taskbar, MonitorDefaultToNearest) == monitor.MonitorHandle
                && VisibleWindowOverlaps(taskbar, bounds))
            {
                return true;
            }
        }
        return false;
    }

    private static bool VisibleWindowOverlaps(nint window, CoreRect bounds) =>
        DockNativeMethods.IsWindowVisible(window)
        && DockNativeMethods.GetWindowRect(window, out var rect)
        && new CoreRect(rect.Left, rect.Top, rect.Right, rect.Bottom).Intersects(bounds);

    internal static CoreRect AvoidNativeTaskbar(CoreRect workArea, CoreRect monitor)
    {
        if (!workArea.HasArea || !monitor.HasArea || GetNativeTaskbarLayout(monitor) is not { } layout)
        {
            return workArea;
        }
        return AvoidTaskbarLayout(workArea, monitor, layout);
    }

    internal static CoreRect AvoidTaskbarLayout(CoreRect workArea, CoreRect monitor, NativeTaskbarLayout layout)
    {
        if (!workArea.HasArea || !monitor.HasArea)
        {
            return workArea;
        }
        // An auto-hidden taskbar may be temporarily expanded when its rect is read and cached;
        // reserve only the thin reveal strip instead of treating the full expanded bar as permanent.
        var taskbarBounds = layout.AutoHidden
            ? AutoHideRevealStrip(monitor, layout.Edge, RevealInsetPixels)
            : layout.Bounds;
        var safeArea = TaskbarLayoutRules.AvoidTaskbar(
            workArea,
            monitor,
            taskbarBounds,
            layout.Edge,
            layout.AutoHidden ? RevealInsetPixels : 0);
        return safeArea.HasArea ? safeArea : workArea;
    }

    private static CoreRect AutoHideRevealStrip(CoreRect monitor, DockEdge edge, int revealInset)
    {
        var maxThickness = edge is DockEdge.Left or DockEdge.Right ? monitor.Width : monitor.Height;
        var thickness = Math.Clamp(revealInset, 1, Math.Max(1, maxThickness));
        return DockGeometry.Calculate(monitor, edge, thickness);
    }

    [Flags]
    internal enum AutoHideEdges
    {
        None = 0,
        Left = 1,
        Top = 2,
        Right = 4,
        Bottom = 8
    }

    /// <summary>Moves a proposed card position off an auto-hidden taskbar's reveal edge.</summary>
    internal static bool ConstrainRect(ref PixelRect rect) => ConstrainRectToDetectedEdges(ref rect);

    /// <summary>Whether an auto-hidden taskbar sits on the bottom edge of this monitor.</summary>
    internal static bool HasBottomAutoHideBar(BingLan.Core.Dock.PixelRect monitor) =>
        HasAutoHideBar(
            new PixelRect { Left = monitor.Left, Top = monitor.Top, Right = monitor.Right, Bottom = monitor.Bottom },
            AbeBottom);

    internal static bool EnsureWindowBounds(nint window)
    {
        if (window == 0 || !GetWindowRect(window, out var rect))
        {
            return false;
        }

        if (!ConstrainRectToDetectedEdges(ref rect))
        {
            return false;
        }

        return NativeMethods.SetWindowPos(
            window,
            0,
            rect.Left,
            rect.Top,
            0,
            0,
            NativeMethods.SwpNoSize |
            NativeMethods.SwpNoZOrder |
            NativeMethods.SwpNoActivate);
    }

    internal static bool EnsureWindowBoundsClampingBottom(nint window, int minimumHeight, bool keepTopFixed)
    {
        if (window == 0 || !GetWindowRect(window, out var rect))
        {
            return false;
        }

        var originalWidth = rect.Width;
        var originalHeight = rect.Height;
        var monitor = MonitorFromRect(ref rect, MonitorDefaultToNearest);
        if (monitor == 0)
        {
            return false;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        var edges = GetAutoHideEdges(monitorInfo.MonitorArea);
        if (!ConstrainToAutoHideEdgesClampingBottom(ref rect, monitorInfo.MonitorArea, edges, minimumHeight, keepTopFixed))
        {
            return false;
        }

        var sizeChanged = rect.Width != originalWidth || rect.Height != originalHeight;
        var flags = NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate | (sizeChanged ? 0u : NativeMethods.SwpNoSize);
        return NativeMethods.SetWindowPos(
            window,
            0,
            rect.Left,
            rect.Top,
            sizeChanged ? rect.Width : 0,
            sizeChanged ? rect.Height : 0,
            flags);
    }

    internal static int? GetBottomResizeLimit(PixelRect anchorRect, bool includeReservedWorkArea)
    {
        var topAnchor = new NativePoint
        {
            X = anchorRect.Left + (anchorRect.Width / 2),
            Y = anchorRect.Top
        };
        var monitor = MonitorFromPoint(topAnchor, MonitorDefaultToNull);
        if (monitor == 0)
        {
            monitor = MonitorFromRect(ref anchorRect, MonitorDefaultToNearest);
        }
        if (monitor == 0)
        {
            return null;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return null;
        }

        int? limit = null;
        if (includeReservedWorkArea && monitorInfo.WorkArea.Bottom < monitorInfo.MonitorArea.Bottom)
        {
            limit = monitorInfo.WorkArea.Bottom;
        }
        if (GetAutoHideEdges(monitorInfo.MonitorArea).HasFlag(AutoHideEdges.Bottom))
        {
            var safeBottom = monitorInfo.MonitorArea.Bottom - RevealInsetPixels;
            limit = limit is { } existing ? Math.Min(existing, safeBottom) : safeBottom;
        }
        return limit;
    }

    internal static bool ConstrainToAutoHideEdgesClampingBottom(
        ref PixelRect rect,
        PixelRect monitorArea,
        AutoHideEdges edges,
        int minimumHeight,
        bool keepTopFixed)
    {
        var original = rect;
        var minHeight = Math.Max(1, minimumHeight);
        var overlapsMonitorHorizontally =
            rect.Right > monitorArea.Left && rect.Left < monitorArea.Right;
        var overlapsMonitorVertically =
            rect.Bottom > monitorArea.Top && rect.Top < monitorArea.Bottom;

        if (overlapsMonitorVertically && edges.HasFlag(AutoHideEdges.Left))
        {
            var safeLeft = monitorArea.Left + RevealInsetPixels;
            if (rect.Left < safeLeft && rect.Right > monitorArea.Left)
            {
                rect.Offset(safeLeft - rect.Left, 0);
            }
        }

        if (overlapsMonitorVertically && edges.HasFlag(AutoHideEdges.Right))
        {
            var safeRight = monitorArea.Right - RevealInsetPixels;
            if (rect.Right > safeRight && rect.Left < monitorArea.Right)
            {
                rect.Offset(safeRight - rect.Right, 0);
            }
        }

        if (overlapsMonitorHorizontally && edges.HasFlag(AutoHideEdges.Top))
        {
            var safeTop = monitorArea.Top + RevealInsetPixels;
            if (rect.Top < safeTop && rect.Bottom > monitorArea.Top)
            {
                rect.Offset(0, safeTop - rect.Top);
            }
        }

        if (overlapsMonitorHorizontally && edges.HasFlag(AutoHideEdges.Bottom))
        {
            var safeBottom = monitorArea.Bottom - RevealInsetPixels;
            if (rect.Bottom > safeBottom && rect.Top < monitorArea.Bottom)
            {
                var available = safeBottom - rect.Top;
                if (available >= minHeight)
                {
                    rect.Bottom = safeBottom;
                }
                else if (keepTopFixed)
                {
                    rect.Bottom = Math.Min(rect.Bottom, rect.Top + minHeight);
                }
                else
                {
                    rect.Offset(0, safeBottom - rect.Bottom);
                }
            }
        }

        return !rect.Equals(original);
    }

    internal static bool ConstrainToAutoHideEdges(
        ref PixelRect rect,
        PixelRect monitorArea,
        AutoHideEdges edges)
    {
        var original = rect;
        var overlapsMonitorHorizontally =
            rect.Right > monitorArea.Left && rect.Left < monitorArea.Right;
        var overlapsMonitorVertically =
            rect.Bottom > monitorArea.Top && rect.Top < monitorArea.Bottom;

        if (overlapsMonitorVertically && edges.HasFlag(AutoHideEdges.Left))
        {
            var safeLeft = monitorArea.Left + RevealInsetPixels;
            if (rect.Left < safeLeft && rect.Right > monitorArea.Left)
            {
                rect.Offset(safeLeft - rect.Left, 0);
            }
        }

        if (overlapsMonitorVertically && edges.HasFlag(AutoHideEdges.Right))
        {
            var safeRight = monitorArea.Right - RevealInsetPixels;
            if (rect.Right > safeRight && rect.Left < monitorArea.Right)
            {
                rect.Offset(safeRight - rect.Right, 0);
            }
        }

        if (overlapsMonitorHorizontally && edges.HasFlag(AutoHideEdges.Top))
        {
            var safeTop = monitorArea.Top + RevealInsetPixels;
            if (rect.Top < safeTop && rect.Bottom > monitorArea.Top)
            {
                rect.Offset(0, safeTop - rect.Top);
            }
        }

        if (overlapsMonitorHorizontally && edges.HasFlag(AutoHideEdges.Bottom))
        {
            var safeBottom = monitorArea.Bottom - RevealInsetPixels;
            if (rect.Bottom > safeBottom && rect.Top < monitorArea.Bottom)
            {
                rect.Offset(0, safeBottom - rect.Bottom);
            }
        }

        return !rect.Equals(original);
    }

    private static bool ConstrainRectToDetectedEdges(ref PixelRect rect)
    {
        var monitor = MonitorFromRect(ref rect, MonitorDefaultToNearest);
        if (monitor == 0)
        {
            return false;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        var edges = GetAutoHideEdges(monitorInfo.MonitorArea);
        return ConstrainToAutoHideEdges(ref rect, monitorInfo.MonitorArea, edges);
    }

    // Each lookup asks Explorer four times; during a drag the answer cannot change, so it
    // is kept per monitor until the drag ends.
    private static Dictionary<PixelRect, AutoHideEdges>? _moveEdges;

    internal static void BeginMove() => _moveEdges = [];

    internal static void EndMove() => _moveEdges = null;

    private static AutoHideEdges GetAutoHideEdges(PixelRect monitorArea)
    {
        if (_moveEdges is null)
        {
            return QueryAutoHideEdges(monitorArea);
        }
        if (!_moveEdges.TryGetValue(monitorArea, out var edges))
        {
            edges = QueryAutoHideEdges(monitorArea);
            _moveEdges[monitorArea] = edges;
        }
        return edges;
    }

    private static AutoHideEdges QueryAutoHideEdges(PixelRect monitorArea)
    {
        var edges = AutoHideEdges.None;
        if (HasAutoHideBar(monitorArea, AbeLeft))
        {
            edges |= AutoHideEdges.Left;
        }
        if (HasAutoHideBar(monitorArea, AbeTop))
        {
            edges |= AutoHideEdges.Top;
        }
        if (HasAutoHideBar(monitorArea, AbeRight))
        {
            edges |= AutoHideEdges.Right;
        }
        if (HasAutoHideBar(monitorArea, AbeBottom))
        {
            edges |= AutoHideEdges.Bottom;
        }
        return edges;
    }

    private static bool HasAutoHideBar(PixelRect monitorArea, uint edge)
    {
        var data = new AppBarData
        {
            Size = Marshal.SizeOf<AppBarData>(),
            Edge = edge,
            Rectangle = monitorArea
        };
        return SHAppBarMessage(AbmGetAutoHideBarEx, ref data) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PixelRect : IEquatable<PixelRect>
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;

        internal readonly int Width => Right - Left;

        internal readonly int Height => Bottom - Top;

        internal void Offset(int horizontal, int vertical)
        {
            Left += horizontal;
            Right += horizontal;
            Top += vertical;
            Bottom += vertical;
        }

        public readonly bool Equals(PixelRect other) =>
            Left == other.Left &&
            Top == other.Top &&
            Right == other.Right &&
            Bottom == other.Bottom;

        public override readonly bool Equals(object? obj) => obj is PixelRect other && Equals(other);

        public override readonly int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal int Size;
        internal PixelRect MonitorArea;
        internal PixelRect WorkArea;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        internal int Size;
        internal nint Window;
        internal uint CallbackMessage;
        internal uint Edge;
        internal PixelRect Rectangle;
        internal nint Parameter;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out PixelRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint pt, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(
        ref PixelRect rect,
        uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        nint monitor,
        ref MonitorInfo monitorInfo);

    [DllImport("shell32.dll")]
    private static extern nint SHAppBarMessage(
        uint message,
        ref AppBarData data);
}
