using System.Runtime.InteropServices;
using System.Windows;

namespace BingLan.App.Tray;

internal readonly record struct TrayMenuAnchor(Rect Bounds, double Scale);

internal static class TrayContextMenuPlacement
{
    private static long _requestVersion;

    internal static void CancelPending() => Interlocked.Increment(ref _requestVersion);
    internal static Point Place(Rect tile, Size menu, Rect workArea, double gap)
    {
        Point Clamp(Point point) => new(
            Math.Clamp(point.X, workArea.Left, Math.Max(workArea.Left, workArea.Right - menu.Width)),
            Math.Clamp(point.Y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - menu.Height)));
        var below = tile.Bottom + gap;
        var above = tile.Top - gap - menu.Height;
        if (below >= workArea.Top && below + menu.Height <= workArea.Bottom) return Clamp(new Point(tile.Left, below));
        if (above >= workArea.Top && above + menu.Height <= workArea.Bottom) return Clamp(new Point(tile.Left, above));
        var right = tile.Right + gap;
        var left = tile.Left - gap - menu.Width;
        if (right >= workArea.Left && right + menu.Width <= workArea.Right) return Clamp(new Point(right, tile.Top));
        if (left >= workArea.Left && left + menu.Width <= workArea.Right) return Clamp(new Point(left, tile.Top));

        // Four bounded candidates also cover menus larger than the available work area.
        // When overlap is unavoidable, preserve as much of the source tile as possible.
        var candidates = new[] { new Point(tile.Left, below), new Point(tile.Left, above),
            new Point(right, tile.Top), new Point(left, tile.Top) }.Select(Clamp);
        return candidates.OrderBy(point =>
        {
            var overlap = Rect.Intersect(tile, new Rect(point, menu));
            return overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;
        }).ThenBy(point => (point - tile.TopLeft).LengthSquared).First();
    }

    internal static Task<string> InvokeAsync(TrayRegisteredIcon? owner, TrayMenuAnchor? anchor,
        Func<string> invoke, CancellationToken cancellation)
    {
        var version = Interlocked.Increment(ref _requestVersion);
        if (cancellation.IsCancellationRequested) return Task.FromCanceled<string>(cancellation);
        if (owner is null || anchor is null || !IsCurrent(owner)) return Task.FromResult(invoke());
        var existing = MenuWindows(owner.ProcessId);
        // Never move a submenu or a menu that was already open before this user request.
        if (existing.Count != 0) return Task.FromResult(invoke());
        var stop = new CancellationTokenSource();
        var callerCancellation = cancellation.Register(stop.Cancel);
        stop.CancelAfter(TrayReader.OperationTimeout);
        var monitor = Task.Run(() => WatchAsync(owner, anchor.Value, version, stop.Token));
        var dispatched = false;
        try
        {
            var result = invoke();
            dispatched = result == "context-requested" && !cancellation.IsCancellationRequested;
            if (dispatched) stop.CancelAfter(TimeSpan.FromMilliseconds(1200));
            return Task.FromResult(result);
        }
        finally
        {
            if (!dispatched) stop.Cancel();
            // Dispatch has handed focus to the app. Closing the hidden source panel must
            // not cancel this short observation, nor wait for a custom menu to appear.
            callerCancellation.Dispose();
            _ = monitor.ContinueWith(completed =>
            {
                _ = completed.Exception;
                stop.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static async Task WatchAsync(TrayRegisteredIcon owner, TrayMenuAnchor anchor, long version, CancellationToken cancellation)
    {
        // Only this explicit action gets a bounded observer. Custom menus and ambiguous roots
        // retain their application placement; supporting those needs an application-specific API.
        nint observedMenu = 0;
        try
        {
            while (!cancellation.IsCancellationRequested && Volatile.Read(ref _requestVersion) == version && IsCurrent(owner))
            {
                var menus = MenuWindows(owner.ProcessId);
                if (menus.Count > 1) return;
                if (menus.Count == 0 && observedMenu != 0) return;
                if (menus is [var menu])
                {
                    if (observedMenu != 0 && observedMenu != menu) return;
                    observedMenu = menu;
                    var thread = Native.GetWindowThreadProcessId(menu, out var processId);
                    var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
                    if (processId != owner.ProcessId) return;
                    if (!GetGUIThreadInfo(thread, ref info) || info.MenuOwner == 0)
                    {
                        await Task.Delay(10, cancellation);
                        continue;
                    }
                    if (info.MenuOwner != owner.Owner) return;
                    var dpi = TrayIconArtwork.SetThreadDpiAwarenessContext(-4);
                    try
                    {
                        if (GetWindowRect(menu, out var bounds) && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top)
                        {
                            var tile = ScreenRect.From(anchor.Bounds);
                            var monitor = MonitorFromRect(ref tile, 2);
                            var screen = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                            if (!GetMonitorInfoW(monitor, ref screen) || !IsCurrent(owner) || cancellation.IsCancellationRequested
                                || Volatile.Read(ref _requestVersion) != version || !Native.IsWindowVisible(menu)
                                || Native.GetWindowThreadProcessId(menu, out var currentPid) != thread || currentPid != owner.ProcessId
                                || !GetGUIThreadInfo(thread, ref info) || info.MenuOwner != owner.Owner) return;
                            var position = Place(anchor.Bounds, new Size(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top),
                                screen.WorkArea.ToRect(), 4 * anchor.Scale);
                            // The menu keeps its native activation and input loop; only its root HWND moves.
                            SetWindowPos(menu, 0, (int)Math.Round(position.X), (int)Math.Round(position.Y), 0, 0,
                                0x0001 | 0x0004 | 0x0010 | 0x4000); // NOSIZE | NOZORDER | NOACTIVATE | ASYNCWINDOWPOS
                            return;
                        }
                    }
                    finally { if (dpi != 0) TrayIconArtwork.SetThreadDpiAwarenessContext(dpi); }
                }
                await Task.Delay(10, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private static bool IsCurrent(TrayRegisteredIcon owner) => owner.Owner != 0 && owner.ProcessId != 0
        && Native.GetWindowThreadProcessId(owner.Owner, out var processId) != 0 && processId == owner.ProcessId;

    private static List<nint> MenuWindows(uint expectedPid)
    {
        var menus = new List<nint>();
        Native.EnumWindows((window, _) =>
        {
            if (Native.GetWindowThreadProcessId(window, out var pid) != 0 && pid == expectedPid
                && Native.IsWindowVisible(window) && Native.ClassName(window) == "#32768") menus.Add(window);
            return menus.Count < 2;
        }, 0);
        return menus;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenRect
    {
        internal int Left, Top, Right, Bottom;
        internal readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
        internal static ScreenRect From(Rect rect) => new()
            { Left = (int)Math.Floor(rect.Left), Top = (int)Math.Floor(rect.Top), Right = (int)Math.Ceiling(rect.Right), Bottom = (int)Math.Ceiling(rect.Bottom) };
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { internal uint Size; internal ScreenRect Monitor, WorkArea; internal uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        internal uint Size, Flags;
        internal nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal ScreenRect CaretRect;
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out ScreenRect rect);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref ScreenRect rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
