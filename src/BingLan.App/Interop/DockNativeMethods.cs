using System.Runtime.InteropServices;
using System.Text;

namespace BingLan.App.Interop;

internal static class DockNativeMethods
{
    internal const long WsExNoActivate = 0x08000000L;

    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint DwmwaExtendedFrameBounds = 9;
    internal const uint DwmwaCloaked = 14;
    internal const uint GaRootOwner = 3;

    internal const int SwMinimize = 6;
    internal const int SwRestore = 9;

    internal const uint EventSystemForeground = 0x0003;
    internal const uint EventSystemMinimizeStart = 0x0016;
    internal const uint EventSystemMinimizeEnd = 0x0017;
    internal const uint EventSystemDesktopSwitch = 0x0020;
    internal const uint EventObjectCreate = 0x8000;
    internal const uint EventObjectHide = 0x8003;
    internal const uint EventObjectNameChange = 0x800C;
    internal const uint EventObjectStateChange = 0x800A;
    internal const uint EventObjectCloaked = 0x8017;
    internal const uint EventObjectUncloaked = 0x8018;
    internal const uint WineventOutOfContext = 0x0000;
    internal const uint WineventSkipOwnProcess = 0x0002;
    internal const int ObjidWindow = 0;

    internal const uint AbmNew = 0x00000000;
    internal const uint AbmRemove = 0x00000001;
    internal const uint AbmQueryPos = 0x00000002;
    internal const uint AbmSetPos = 0x00000003;
    internal const uint AbmActivate = 0x00000006;
    internal const uint AbmWindowPosChanged = 0x00000009;
    internal const int AbnPosChanged = 1;

    internal const int WmActivate = 0x0006;
    internal const ushort WaInactive = 0;
    internal const int WmWindowPosChanged = 0x0047;
    internal const int WmDisplayChange = 0x007E;
    internal const int WmDpiChanged = 0x02E0;
    internal const uint WmGetIcon = 0x007F;
    internal const uint WmSysCommand = 0x0112;
    internal const int ScClose = 0xF060;

    internal const int IconSmall = 0;
    internal const int IconBig = 1;
    internal const int IconSmall2 = 2;
    internal const int GclpHIcon = -14;
    internal const int GclpHIconSm = -34;
    internal const uint SmtoAbortIfHung = 0x0002;

    internal const uint ShgfiDisplayName = 0x00000200;
    internal const uint ShgfiSysIconIndex = 0x00004000;
    internal const uint ShgfiUseFileAttributes = 0x00000010;
    internal const uint ShgfiPidl = 0x00000008;
    internal const uint FileAttributeNormal = 0x00000080;
    internal const int ShilExtraLarge = 2;
    internal const uint IldTransparent = 0x00000001;

    internal static readonly nint HwndBottom = 1;
    internal static readonly nint HwndTopmost = -1;

    internal const uint MonitorDefaultToNearest = 2;
    internal const uint MonitorInfoPrimary = 1;
    internal const int MdtEffectiveDpi = 0;

    internal static readonly Guid PropertyStoreId = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    internal static readonly Guid ImageListId = new("46EB5926-582E-4017-9FDF-E8998DAA0950");
    internal static readonly PropertyKey AppUserModelIdKey =
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    internal delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    internal delegate void WinEventProc(
        nint hook,
        uint eventType,
        nint hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    internal delegate bool MonitorEnumProc(
        nint monitor,
        nint deviceContext,
        ref NativeRect monitorRectangle,
        nint data);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AppBarData
    {
        internal uint Size;
        internal nint Window;
        internal uint CallbackMessage;
        internal uint Edge;
        internal NativeRect Rectangle;
        internal nint Parameter;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        internal uint Size;
        internal NativeRect Monitor;
        internal NativeRect WorkArea;
        internal uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ShFileInfo
    {
        internal nint Icon;
        internal int IconIndex;
        internal uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        internal string TypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FlashWindowInfo
    {
        internal uint Size;
        internal nint Window;
        internal uint Flags;
        internal uint Count;
        internal uint Timeout;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        internal readonly Guid FormatId = formatId;
        internal readonly uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        internal ushort ValueType;
        internal ushort Reserved1;
        internal ushort Reserved2;
        internal ushort Reserved3;
        internal nint Value;
        internal nint Value2;
    }

    internal const ushort VtLpwstr = 31;

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(in PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(in PropertyKey key, in PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    internal static extern int GetWindowTextLengthW(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(nint hwnd, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassNameW(nint hwnd, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    internal static extern nint GetShellWindow();

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    internal static extern nint SendMessageTimeoutW(
        nint hwnd,
        uint message,
        nint wParam,
        nint lParam,
        uint flags,
        uint timeout,
        out nint result);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    internal static extern nint GetClassLongPtr(nint hwnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(nint handle);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    internal static extern nint GetLastActivePopup(nint hwnd);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsZoomed(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindowAsync(nint hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FlashWindowEx(ref FlashWindowInfo info);

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

    [DllImport("user32.dll")]
    internal static extern uint RegisterWindowMessageW([MarshalAs(UnmanagedType.LPWStr)] string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterShellHookWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeregisterShellHookWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MoveWindow(
        nint hwnd,
        int x,
        int y,
        int width,
        int height,
        [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hwnd, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(
        nint deviceContext,
        nint clipRectangle,
        MonitorEnumProc callback,
        nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("kernel32.dll")]
    internal static extern nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageNameW(
        nint process,
        uint flags,
        StringBuilder executableName,
        ref uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetApplicationUserModelId(
        nint process,
        ref uint applicationUserModelIdLength,
        StringBuilder? applicationUserModelId);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(
        nint hwnd,
        uint attribute,
        out int value,
        int valueSize);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(
        nint hwnd,
        uint attribute,
        out NativeRect value,
        int valueSize);

    [DllImport("shell32.dll")]
    internal static extern nuint SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    internal static extern nint SHGetFileInfoW(
        string path,
        uint fileAttributes,
        ref ShFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    internal static extern nint SHGetFileInfoW(
        nint itemIdList,
        uint fileAttributes,
        ref ShFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHParseDisplayName(
        string name,
        nint bindContext,
        out nint itemIdList,
        uint attributesIn,
        out uint attributesOut);

    [DllImport("shell32.dll")]
    internal static extern void ILFree(nint itemIdList);

    [DllImport("shell32.dll")]
    internal static extern int SHGetImageList(int imageList, in Guid interfaceId, out nint list);

    [DllImport("shell32.dll")]
    internal static extern int SHGetPropertyStoreForWindow(
        nint hwnd,
        in Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(ref PropVariant value);

    [DllImport("comctl32.dll")]
    internal static extern nint ImageList_GetIcon(nint list, int index, uint flags);

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(
        nint monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);
}
