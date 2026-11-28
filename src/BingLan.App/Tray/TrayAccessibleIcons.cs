using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Windows;
using Accessibility;
using Microsoft.Win32.SafeHandles;

namespace BingLan.App.Tray;

internal static class TrayAccessibleIcons
{
    private const uint ClientObject = 0xFFFFFFFC;
    private const int Self = 0;
    private const int PushButtonRole = 0x2B;
    private const int Unavailable = 0x1;
    private const string InputSiteClass = "Windows.UI.Input.InputSite.WindowClass";
    private static readonly Guid AccessibleInterface = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    internal static TrayEntry[] Read(nint tray, uint explorerPid, out bool failed)
    {
        failed = false;
        var dpi = TrayIconArtwork.SetThreadDpiAwarenessContext(-4);
        try
        {
            var registered = TrayIconArtwork.ReadRegisteredIcons();
            if (registered is null)
            {
                failed = true;
                return [];
            }
            if (registered.Length == 0) return [];
            if (!GetWindowRect(tray, out var frame))
            {
                failed = true;
                return [];
            }
            var candidates = registered.Where(icon => frame.Contains(Center(icon.Bounds)))
                .OrderBy(icon => icon.Bounds.Top).ThenBy(icon => icon.Bounds.Left).Take(128).ToArray();
            // No registered taskbar tiles means this fallback has no applicable source.
            if (candidates.Length == 0) return [];
            if (!TryOpenClient(tray, explorerPid, out var client, out _))
            {
                failed = true;
                return [];
            }
            try
            {
                return ReadCandidates(candidates, icon =>
                {
                    if (HitButton(client, icon.Bounds) is not { } button) return null;
                    try
                    {
                        var name = button.get_accName(Self) ?? "";
                        if (name.Length > 4096) name = name[..4096];
                        return new TrayEntry(icon.Key, name, "taskbar", explorerPid, null, icon);
                    }
                    finally { Marshal.ReleaseComObject(button); }
                }, out failed);
            }
            finally { Marshal.ReleaseComObject(client); }
        }
        catch (Exception exception) when (IsRecoverableAccessibilityFailure(exception))
        {
            failed = true;
            return [];
        }
        finally { if (dpi != 0) TrayIconArtwork.SetThreadDpiAwarenessContext(dpi); }
    }

    internal static TrayEntry[] ReadCandidates(IReadOnlyList<TrayRegisteredIcon> candidates,
        Func<TrayRegisteredIcon, TrayEntry?> read, out bool failed)
    {
        failed = false;
        var entries = new List<TrayEntry>();
        foreach (var icon in candidates)
        {
            try
            {
                if (read(icon) is { } entry) entries.Add(entry);
                else failed = true;
            }
            catch (Exception exception) when (IsRecoverableAccessibilityFailure(exception)) { failed = true; }
        }
        return entries.ToArray();
    }

    internal static string Invoke(TrayRegisteredIcon registration, uint explorerPid, bool contextMenu, CancellationToken cancellation)
    {
        var dpi = TrayIconArtwork.SetThreadDpiAwarenessContext(-4);
        try
        {
            if (cancellation.IsCancellationRequested || (registration.Owner != 0
                && (Native.GetWindowThreadProcessId(registration.Owner, out var ownerPid) == 0 || ownerPid != registration.ProcessId))
                || TrayIconArtwork.ReadRegisteredBounds(registration.Owner, registration.IconId, registration.IconGuid) is not { } bounds
                || !TryOpenClient(Native.FindWindowW("Shell_TrayWnd", null), explorerPid, out var client, out var input))
                return "element-unavailable";
            try
            {
                if (HitButton(client, bounds) is not { } button) return "element-unavailable";
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (contextMenu)
                    {
                        var accessible = Marshal.GetIUnknownForObject(button);
                        try
                        {
                            var handle = cancellation.WaitHandle.SafeWaitHandle;
                            cancellation.ThrowIfCancellationRequested();
                            var result = BingLanTrayShowAccessibleContextMenu(accessible, input, explorerPid, handle);
                            return result >= 0 ? "context-requested" : $"context-error-0x{result:X8}";
                        }
                        finally { Marshal.Release(accessible); }
                    }
                    button.accDoDefaultAction(Self);
                    return "invoked";
                }
                finally { Marshal.ReleaseComObject(button); }
            }
            finally { Marshal.ReleaseComObject(client); }
        }
        catch (Exception exception) when (IsRecoverableAccessibilityFailure(exception)) { return "element-unavailable"; }
        finally { if (dpi != 0) TrayIconArtwork.SetThreadDpiAwarenessContext(dpi); }
    }

    private static bool TryOpenClient(nint tray, uint explorerPid,
        [NotNullWhen(true)] out IAccessible? client, out nint inputSite)
    {
        client = null;
        inputSite = 0;
        if (tray == 0 || Native.GetWindowThreadProcessId(tray, out var pid) == 0 || pid != explorerPid)
            return false;
        nint input = 0;
        Native.EnumChildWindows(tray, (child, _) =>
        {
            if (Native.ClassName(child) != InputSiteClass
                || Native.GetWindowThreadProcessId(child, out var owner) == 0 || owner != explorerPid) return true;
            input = child;
            return false;
        }, 0);
        var iid = AccessibleInterface;
        if (input == 0 || GetAccessibleClient(input, ClientObject, ref iid, out var accessible) != 0 || accessible is null)
            return false;
        client = accessible;
        inputSite = input;
        return true;
    }

    private static IAccessible? HitButton(IAccessible client, Rect registered)
    {
        var center = Center(registered);
        object? hit = null;
        try
        {
            hit = client.accHitTest((int)center.X, (int)center.Y);
            // XAML may hit the button's image first. Only climb within the fragment to the real button.
            for (var depth = 0; depth < 3 && hit is IAccessible node; depth++)
            {
                if (node.get_accRole(Self) is int role && role == PushButtonRole)
                {
                    node.accLocation(out var x, out var y, out var width, out var height, Self);
                    if (width > 0 && height > 0 && TrayIconArtwork.MatchBounds(new Rect(x, y, width, height), registered)
                        && node.get_accState(Self) is int state && (state & Unavailable) == 0
                        && !string.IsNullOrEmpty(node.get_accDefaultAction(Self)))
                    {
                        hit = null;
                        return node;
                    }
                    return null;
                }
                var parent = node.accParent;
                Marshal.ReleaseComObject(node);
                hit = parent;
            }
            return null;
        }
        catch (Exception exception) when (IsRecoverableAccessibilityFailure(exception)) { return null; }
        finally { if (hit is not null && Marshal.IsComObject(hit)) Marshal.ReleaseComObject(hit); }
    }

    private static Point Center(Rect bounds) => new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

    internal static bool IsRecoverableAccessibilityFailure(Exception exception) => exception is COMException
        or ArgumentException or NotImplementedException or UnauthorizedAccessException or InvalidCastException
        or InvalidOperationException or InvalidComObjectException;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        internal int Left, Top, Right, Bottom;

        internal bool Contains(Point point) => point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out WindowRect bounds);

    [DllImport("oleacc.dll", EntryPoint = "AccessibleObjectFromWindow")]
    private static extern int GetAccessibleClient(nint window, uint objectId, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible);

    [DllImport("BingLan.TrayContextNative.dll", ExactSpelling = true)]
    private static extern int BingLanTrayShowAccessibleContextMenu(nint accessible, nint inputSite,
        uint expectedExplorerPid, SafeWaitHandle cancellationHandle);
}
