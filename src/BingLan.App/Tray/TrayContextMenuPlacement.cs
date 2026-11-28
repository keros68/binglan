using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Microsoft.Win32.SafeHandles;

namespace BingLan.App.Tray;

internal readonly record struct TrayMenuAnchor(Rect Bounds, double Scale, bool PreferAbove = false);

internal static class TrayContextMenuPlacement
{
    private const int GwlStyle = -16, GwlExStyle = -20;
    private const long WsPopup = 0x80000000L, WsChild = 0x40000000L, WsCaption = 0x00C00000L,
        WsThickFrame = 0x00040000L, WsExToolWindow = 0x00000080L, WsExTopmost = 0x00000008L,
        WsExAppWindow = 0x00040000L, WsExNoRedirectionBitmap = 0x00200000L;
    private const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010,
        SwpAsyncWindowPos = 0x4000, MonitorDefaultToNearest = 2, ProcessQueryLimitedInformation = 0x1000,
        GwOwner = 4;
    private static long _requestVersion;

    internal static void CancelPending() => Interlocked.Increment(ref _requestVersion);
    internal static Point Place(Rect tile, Size menu, Rect workArea, double gap, bool preferAbove = false)
    {
        Point Clamp(Point point) => new(
            Math.Clamp(point.X, workArea.Left, Math.Max(workArea.Left, workArea.Right - menu.Width)),
            Math.Clamp(point.Y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - menu.Height)));
        var below = tile.Bottom + gap;
        var above = tile.Top - gap - menu.Height;
        foreach (var y in preferAbove ? new[] { above, below } : new[] { below, above })
            if (y >= workArea.Top && y + menu.Height <= workArea.Bottom) return Clamp(new Point(tile.Left, y));
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

    internal static async Task<string> InvokeAsync(TrayContextMenuOwner? owner, TrayMenuAnchor? anchor,
        Func<string> invoke, CancellationToken cancellation)
    {
        var version = Interlocked.Increment(ref _requestVersion);
        cancellation.ThrowIfCancellationRequested();
        if (anchor is null) return invoke();
        if (owner is { Window: 0, ProcessId: 0, ObservationExecutablePath: { } observationPath }
            && TrayIconArtwork.IsGuidMenuObservationIdentity(observationPath, owner.ObservationIconGuid)
            && TrayIconArtwork.IsCurrentGuidMenuObservation(owner))
            return await InvokeObservationAsync(owner, anchor.Value, version, invoke, cancellation).ConfigureAwait(false);
        if (owner is null || !IsCurrent(owner) || MenuWindows(owner).Count != 0)
        {
            var result = invoke();
            return result == "context-requested" ? "context-unconfirmed" : result;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        stop.CancelAfter(TrayReader.OperationTimeout);
        var watch = WatchAsync(owner, anchor.Value, version, stop.Token);
        try
        {
            var result = invoke();
            if (result != "context-requested") return result;
            stop.CancelAfter(TimeSpan.FromMilliseconds(1200));
            return await watch.ConfigureAwait(false) ? "context-opened" : "context-unconfirmed";
        }
        finally
        {
            stop.Cancel();
            await watch.ConfigureAwait(false);
        }
    }

    private static async Task<string> InvokeObservationAsync(TrayContextMenuOwner owner, TrayMenuAnchor anchor,
        long version, Func<string> invoke, CancellationToken cancellation)
    {
        var existing = new HashSet<nint>();
        Native.EnumWindows((window, _) =>
        {
            if (IsObservationProcessWindow(window, owner.ObservationExecutablePath!)) existing.Add(window);
            return true;
        }, 0);
        cancellation.ThrowIfCancellationRequested();
        var result = invoke();
        if (result != "context-requested") return result;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        stop.CancelAfter(TimeSpan.FromMilliseconds(1200));
        try
        {
            while (!stop.IsCancellationRequested && Volatile.Read(ref _requestVersion) == version)
            {
                var window = GetForegroundWindow();
                if (!existing.Contains(window) && IsObservationWindow(window, owner.ObservationExecutablePath!))
                {
                    var dpi = TrayIconArtwork.SetThreadDpiAwarenessContext(-4);
                    try
                    {
                        var tile = ScreenRect.From(anchor.Bounds);
                        var monitor = MonitorFromRect(ref tile, MonitorDefaultToNearest);
                        var screen = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                        if (GetWindowRect(window, out var bounds) && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top
                            && GetMonitorInfoW(monitor, ref screen)
                            && GetForegroundWindow() == window && !stop.IsCancellationRequested
                            && Volatile.Read(ref _requestVersion) == version
                            && IsObservationMenuPosition(anchor.Bounds, bounds.ToRect(), screen.WorkArea.ToRect(), anchor.Scale)
                            && TrayIconArtwork.IsCurrentGuidMenuObservation(owner)
                            && IsObservationWindow(window, owner.ObservationExecutablePath!)
                            && GetForegroundWindow() == window && !stop.IsCancellationRequested
                            && Volatile.Read(ref _requestVersion) == version)
                            return "context-opened";
                    }
                    finally { if (dpi != 0) TrayIconArtwork.SetThreadDpiAwarenessContext(dpi); }
                }
                await Task.Delay(10, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        return "context-unconfirmed";
    }

    private static bool IsObservationProcessWindow(nint window, string executablePath) => window != 0
        && Native.IsWindowVisible(window) && Native.ClassName(window) == "Chrome_WidgetWin_1"
        && Native.GetWindowThreadProcessId(window, out var processId) != 0
        && string.Equals(ReadProcessPath(processId), executablePath, StringComparison.OrdinalIgnoreCase);

    private static bool IsObservationWindow(nint window, string executablePath) =>
        IsObservationProcessWindow(window, executablePath)
        && IsObservationPopup(Native.ClassName(window), GetWindowLongPtrW(window, GwlStyle), GetWindowLongPtrW(window, GwlExStyle));

    internal static bool IsObservationPopup(string className, long style, long extendedStyle)
    {
        const long requiredExtendedStyle = WsExToolWindow | WsExTopmost | WsExNoRedirectionBitmap;
        return className == "Chrome_WidgetWin_1" && (style & WsPopup) != 0
            && (style & (WsChild | WsCaption | WsThickFrame)) == 0
            && (extendedStyle & requiredExtendedStyle) == requiredExtendedStyle
            && (extendedStyle & WsExAppWindow) == 0;
    }

    internal static bool IsObservationMenuPosition(Rect tile, Rect menu, Rect workArea, double scale)
    {
        if (tile.IsEmpty || tile.Width <= 0 || tile.Height <= 0 || !double.IsFinite(scale) || scale <= 0) return false;
        // The verified Chrome popup includes a transparent shadow: at 150% its
        // HWND corner is 8 physical pixels outside the tile. Allow at most 8 DIPs.
        tile.Inflate(8 * scale, 8 * scale);
        return IsNativeMenuPosition(tile, menu, workArea);
    }

    private static async Task<bool> WatchAsync(TrayContextMenuOwner owner, TrayMenuAnchor anchor, long version, CancellationToken cancellation)
    {
        // A single new root is eligible during this request. Multiple roots can be submenus.
        nint observedMenu = 0;
        Size? movedSize = null;
        try
        {
            while (!cancellation.IsCancellationRequested && Volatile.Read(ref _requestVersion) == version && IsCurrent(owner))
            {
                var menus = MenuWindows(owner);
                if (menus.Count > 1 || (menus.Count == 0 && observedMenu != 0)) return false;
                if (menus is [var menu])
                {
                    if (observedMenu != 0 && observedMenu != menu) return false;
                    observedMenu = menu;
                    var thread = Native.GetWindowThreadProcessId(menu, out var processId);
                    if (processId != owner.ProcessId) return false;
                    if (!CanPlace(menu, thread, owner))
                    {
                        await Task.Delay(10, cancellation).ConfigureAwait(false);
                        continue;
                    }
                    var dpi = TrayIconArtwork.SetThreadDpiAwarenessContext(-4);
                    try
                    {
                        if (GetWindowRect(menu, out var bounds) && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top)
                        {
                            var tile = ScreenRect.From(anchor.Bounds);
                            var monitor = MonitorFromRect(ref tile, MonitorDefaultToNearest);
                            var screen = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                            if (!GetMonitorInfoW(monitor, ref screen) || !IsCurrent(owner) || cancellation.IsCancellationRequested
                                || Volatile.Read(ref _requestVersion) != version || !Native.IsWindowVisible(menu)
                                || Native.GetWindowThreadProcessId(menu, out var currentPid) != thread || currentPid != owner.ProcessId
                                || !CanPlace(menu, thread, owner)) return false;
                            var size = new Size(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
                            var position = Place(anchor.Bounds, size, screen.WorkArea.ToRect(), 4 * anchor.Scale, anchor.PreferAbove);
                            var x = (int)Math.Round(position.X);
                            var y = (int)Math.Round(position.Y);
                            // A popup that rounds through logical pixels can settle one physical
                            // pixel away at fractional DPI (observed on both Qt versions at 175%).
                            if (Math.Abs(bounds.Left - x) <= 1 && Math.Abs(bounds.Top - y) <= 1) return true;
                            // A cross-monitor DPI change can resize the popup after its first move.
                            if (size != movedSize)
                            {
                                if (!SetWindowPos(menu, 0, x, y, 0, 0, SwpNoSize | SwpNoZOrder
                                    | SwpNoActivate | SwpAsyncWindowPos))
                                    return IsNativeMenuPosition(anchor.Bounds, bounds.ToRect(), screen.WorkArea.ToRect());
                                movedSize = size;
                            }
                            else if (IsNativeMenuPosition(anchor.Bounds, bounds.ToRect(), screen.WorkArea.ToRect()))
                            {
                                // An elevated app can retain its own click-anchored menu
                                // after the requested move. Its confirmed native position is usable.
                                return true;
                            }
                        }
                    }
                    finally { if (dpi != 0) TrayIconArtwork.SetThreadDpiAwarenessContext(dpi); }
                }
                await Task.Delay(10, cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        return false;
    }

    private static bool CanPlace(nint menu, uint thread, TrayContextMenuOwner owner)
    {
        var className = Native.ClassName(menu);
        if (!AllowsMenuClass(owner, className)) return false;
        if (className == "#32768")
        {
            var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
            return GetGUIThreadInfo(thread, ref info) && MatchesStandardMenuOwner(owner, info.MenuOwner);
        }
        // Custom tray popups can have no owner HWND or UIA Menu role. Only the
        // foreground tool popup from the live registered process is eligible.
        if (GetForegroundWindow() != menu || ((long)GetWindowLongPtrW(menu, GwlExStyle) & WsExToolWindow) == 0)
            return false;
        if (IsQtPopup(className)) return true;
        if (!IsKnownAppPopupClass(className) || ((long)GetWindowLongPtrW(menu, GwlStyle) & WsPopup) == 0)
            return false;
        var popupOwner = GetWindow(menu, GwOwner);
        var popupOwnerPid = 0u;
        if (popupOwner != 0) Native.GetWindowThreadProcessId(popupOwner, out popupOwnerPid);
        return MatchesKnownAppPopupOwner(className, owner.ProcessId, popupOwnerPid)
            && MatchesKnownAppProcess(className, owner.ProcessId);
    }

    internal static bool AllowsMenuClass(TrayContextMenuOwner owner, string className) =>
        className == "#32768" || (!owner.StandardMenusOnly && (IsQtPopup(className) || IsKnownAppPopupClass(className)));

    internal static bool MatchesStandardMenuOwner(TrayContextMenuOwner owner, nint menuOwner) =>
        owner.Window != 0 && menuOwner == owner.Window;

    internal static bool IsNativeMenuPosition(Rect tile, Rect menu, Rect workArea) =>
        !tile.IsEmpty && tile.Width > 0 && tile.Height > 0 && !menu.IsEmpty && menu.Width > 0 && menu.Height > 0
        && workArea.Contains(menu)
        && (tile.Contains(menu.TopLeft) || tile.Contains(menu.TopRight)
            || tile.Contains(menu.BottomLeft) || tile.Contains(menu.BottomRight));

    private static bool IsKnownAppPopupClass(string className) =>
        className is "OneDriveReactNativeWin32WindowClass" or "LenovoTrayMenuV2" or "MenuWnd";

    internal static bool IsKnownAppPopup(string className, string executablePath) => className switch
    {
        "OneDriveReactNativeWin32WindowClass" => Path.GetFileName(executablePath).Equals("OneDrive.exe", StringComparison.OrdinalIgnoreCase),
        "LenovoTrayMenuV2" => Path.GetFileName(executablePath).Equals("LenovoTray.exe", StringComparison.OrdinalIgnoreCase),
        "MenuWnd" => Path.GetFileName(executablePath).Equals("cloudmusic.exe", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    internal static bool MatchesKnownAppPopupOwner(string className, uint expectedPid, uint popupOwnerPid) =>
        className != "MenuWnd" || (expectedPid != 0 && popupOwnerPid == expectedPid);

    private static bool MatchesKnownAppProcess(string className, uint processId)
        => ReadProcessPath(processId) is { } path && IsKnownAppPopup(className, path);

    private static string? ReadProcessPath(uint processId)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        var path = new StringBuilder(32768);
        var length = (uint)path.Capacity;
        return !process.IsInvalid && QueryFullProcessImageNameW(process, 0, path, ref length) ? path.ToString() : null;
    }

    internal static bool IsQtPopup(string className)
    {
        const string suffix = "QWindowPopupSaveBits";
        if (!className.StartsWith("Qt", StringComparison.Ordinal) || !className.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        var version = className.AsSpan(2, className.Length - 2 - suffix.Length);
        return !version.IsEmpty && !version.ContainsAnyExceptInRange('0', '9');
    }

    private static bool IsCurrent(TrayContextMenuOwner owner) => owner.Window != 0 && owner.ProcessId != 0
        && Native.GetWindowThreadProcessId(owner.Window, out var processId) != 0 && processId == owner.ProcessId;

    private static List<nint> MenuWindows(TrayContextMenuOwner owner)
    {
        var menus = new List<nint>();
        Native.EnumWindows((window, _) =>
        {
            var thread = Native.GetWindowThreadProcessId(window, out var pid);
            if (thread != 0 && pid == owner.ProcessId && Native.IsWindowVisible(window))
            {
                var className = Native.ClassName(window);
                if (!AllowsMenuClass(owner, className) || (owner.StandardMenusOnly && !CanPlace(window, thread, owner)))
                    return true;
                // Owned decorations such as Lenovo's PerryShadowWnd stay attached to
                // their application menu; they are not independent menu roots.
                if (className == "#32768" || IsQtPopup(className)
                    || (IsKnownAppPopupClass(className) && MatchesKnownAppProcess(className, owner.ProcessId))) menus.Add(window);
            }
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
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
}
