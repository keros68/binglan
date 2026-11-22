using System.Runtime.InteropServices;

namespace BingLan.App.Interop;

/// <summary>
/// Win32 Raw Input, message-loop and read-only MSAA (<c>oleacc.dll</c>) interop used by
/// the opt-in desktop empty-area double-click watcher. Uses only documented non-blocking
/// APIs: no global hooks (<c>SetWindowsHookEx</c>) and no cross-process memory access.
/// </summary>
internal static class DesktopDoubleClickNativeMethods
{
    internal static readonly nint HwndMessage = new(-3);

    internal const uint WmClose = 0x0010;
    internal const uint WmNcDestroy = 0x0082;
    internal const uint WmInput = 0x00FF;

    internal const ushort HidUsagePageGeneric = 0x01;
    internal const ushort HidUsageGenericMouse = 0x02;

    internal const uint RidevRemove = 0x00000001;
    internal const uint RidevInputSink = 0x00000100;

    internal const uint RidInput = 0x10000003;
    internal const uint RimTypeMouse = 0;
    internal const ushort RiMouseLeftButtonDown = 0x0001;

    internal const int SmCxDoubleClk = 36;
    internal const int SmCyDoubleClk = 37;

    internal const uint GaParent = 1;

    internal const uint CoinitApartmentThreaded = 0x2;
    internal const int SOk = 0;
    internal const int SFalse = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputDevice
    {
        internal ushort UsagePage;
        internal ushort Usage;
        internal uint Flags;
        internal nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputHeader
    {
        internal uint Type;
        internal uint Size;
        internal nint Device;
        internal nint WParam;
    }

    // Matches Win32 RAWMOUSE including the 2-byte alignment padding between usFlags
    // (USHORT) and the anonymous ULONG/USHORT button union.
    [StructLayout(LayoutKind.Sequential)]
    internal struct RawMouse
    {
        internal ushort Flags;
        internal ushort Reserved;
        internal ushort ButtonFlags;
        internal ushort ButtonData;
        internal uint RawButtons;
        internal int LastX;
        internal int LastY;
        internal uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputMouse
    {
        internal RawInputHeader Header;
        internal RawMouse Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMessage
    {
        internal nint Hwnd;
        internal uint Message;
        internal nint WParam;
        internal nint LParam;
        internal uint Time;
        internal DockNativeMethods.NativePoint Point;
        internal uint Private;
    }

    [ComImport]
    [Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface IAccessible
    {
        void get_accParent();
        void get_accChildCount();
        void get_accChild();
        void get_accName();
        void get_accValue();
        void get_accDescription();

        [PreserveSig]
        int get_accRole(
            [MarshalAs(UnmanagedType.Struct)] object varChild,
            [MarshalAs(UnmanagedType.Struct)] out object? pvarRole);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterClassW(string className, nint instance);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    internal static extern int GetMessageW(
        out NativeMessage message,
        nint hwnd,
        uint messageFilterMin,
        uint messageFilterMax);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static extern nint DispatchMessageW(ref NativeMessage message);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterRawInputDevices(
        ref RawInputDevice devices,
        uint numberOfDevices,
        uint size);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputData(
        nint rawInput,
        uint command,
        out RawInputMouse data,
        ref uint size,
        uint headerSize);

    [DllImport("user32.dll")]
    internal static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    internal static extern nint WindowFromPoint(DockNativeMethods.NativePoint point);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromPoint(DockNativeMethods.NativePoint point, uint flags);

    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(nint reserved, uint coInit);

    [DllImport("ole32.dll")]
    internal static extern void CoUninitialize();

    [DllImport("oleacc.dll")]
    internal static extern int AccessibleObjectFromPoint(
        DockNativeMethods.NativePoint point,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessibleObject,
        [MarshalAs(UnmanagedType.Struct)] out object? childId);

    [DllImport("oleacc.dll")]
    internal static extern int WindowFromAccessibleObject(
        IAccessible accessibleObject,
        out nint hwnd);
}
