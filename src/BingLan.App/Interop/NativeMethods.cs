using System.Runtime.InteropServices;

namespace BingLan.App.Interop;

internal static class NativeMethods
{
    internal const long WsExNoRedirectionBitmap = 0x00200000L;
    internal const int DwmwaUseHostBackdropBrush = 17;
    internal delegate nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        internal uint Size, Style;
        internal WindowProcedure Procedure;
        internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background;
        internal string? Menu, Name;
        internal nint SmallIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint owner, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    internal static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint hwnd);
    internal const int WmSize = 0x0005;
    internal const int WmShowWindow = 0x0018;
    internal const int WmSettingChange = 0x001A;
    internal const int SpiSetWorkArea = 0x002F;
    internal const int WmWindowPosChanged = 0x0047;
    internal const int WmNcCalcSize = 0x0083;
    internal const int WmNcHitTest = 0x0084;
    internal const int WmThemeChanged = 0x031A;
    internal const int WmDwmCompositionChanged = 0x031E;

    internal const int SizeMinimized = 1;

    internal const int HtTransparent = -1;
    internal const int HtClient = 1;

    internal const int GwlStyle = -16;
    internal const long WsChild = 0x40000000;
    internal const int GwlExStyle = -20;

    internal const long WsThickFrame = 0x00040000L;
    internal const long WsMaximizeBox = 0x00010000L;
    internal const long WsExAcceptFiles = 0x00000010L;
    internal const long WsExToolWindow = 0x00000080L;
    internal const long WsExAppWindow = 0x00040000L;
    internal const long WsExLayered = 0x00080000L;
    internal const long WsExNoActivate = 0x08000000L;
    internal const long WsExTopmost = 0x00000008L;
    internal const long WsExTransparent = 0x00000020L;

    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpFrameChanged = 0x0020;
    internal const uint SwpShowWindow = 0x0040;
    internal const uint SwpHideWindow = 0x0080;

    internal const int DwmwaNcRenderingPolicy = 2;
    internal const int DwmwaCloaked = 14;
    internal const int DwmwaWindowCornerPreference = 33;
    internal const int DwmwaBorderColor = 34;
    internal const int DwmwaSystemBackdropType = 38;
    internal const int DwmWindowCornerDoNotRound = 1;
    internal const int DwmNcRenderingDisabled = 1;
    internal const int DwmColorNone = unchecked((int)0xFFFFFFFE);
    internal const int DwmSystemBackdropNone = 1;
    internal const int DwmSystemBackdropMainWindow = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Margins
    {
        internal int Left;
        internal int Right;
        internal int Top;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowPos
    {
        internal nint Window;
        internal nint InsertAfter;
        internal int X;
        internal int Y;
        internal int Cx;
        internal int Cy;
        internal uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern nint SetWindowLongPtr(nint window, int index, nint newValue);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    internal const int SwShowNoActivate = 4;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint window);

    internal static long GetExStyleOf(nint window) => GetWindowLongPtr(window, GwlExStyle);

    // Controls inside a window (toolbar buttons, list items) raise the same object
    // events as top-level windows. A destroyed window reads as not a child.
    internal static bool IsChildWindow(nint window) => ((long)GetWindowLongPtr(window, GwlStyle) & WsChild) != 0;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint FindWindowW(string className, string? windowName);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    internal delegate bool EnumWindowsProc(nint window, nint parameter);

    public delegate void WinEventProc(
        nint hook, uint eventType, nint window, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    internal static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint module,
        WinEventProc callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(nint hook);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmExtendFrameIntoClientArea(
        nint window,
        ref Margins margins);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(
        nint window,
        int attribute,
        ref int value,
        int valueSize);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(
        nint window,
        int attribute,
        out int value,
        int valueSize);

    /// <summary>
    /// Whether DWM cloaks the window (suspended UWP hosts and similar stay
    /// IsWindowVisible while being fully off screen), or false when the attribute
    /// is unavailable on the current Windows build.
    /// </summary>
    internal static bool IsWindowCloaked(nint window) =>
        DwmGetWindowAttribute(window, DwmwaCloaked, out int cloaked, sizeof(int)) == 0
        && cloaked != 0;
}
