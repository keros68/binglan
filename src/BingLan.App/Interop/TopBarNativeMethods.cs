using System.Runtime.InteropServices;

namespace BingLan.App.Interop;

/// <summary>
/// The top bar's own Win32 surface. The AppBar, shell-hook and window APIs it shares
/// with the dock stay in <see cref="DockNativeMethods"/>; only what no other module
/// uses lives here.
/// </summary>
internal static class TopBarNativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        internal byte AcLineStatus;
        internal byte BatteryFlag;
        internal byte BatteryLifePercent;
        // Windows 10/11 SystemStatusFlag: bit 0 is set while Battery Saver is on.
        internal byte SystemStatusFlag;
        internal int BatteryLifeTime;
        internal int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemPowerStatus(ref SystemPowerStatus status);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetKeyboardLayout(uint idThread);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        internal int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        internal int Size;
        internal uint Flags;
        internal nint HwndActive;
        internal nint HwndFocus;
        internal nint HwndCapture;
        internal nint HwndMenuOwner;
        internal nint HwndMoveSize;
        internal nint HwndCaret;
        internal NativeRect CaretRect;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo info);

    internal const uint WmInputLangChangeRequest = 0x0050;

    // The IME's Chinese/English toggle rides the imm32 bridge the taskbar indicator
    // uses: ask the foreground thread's default IME window for its conversion mode.
    [DllImport("imm32.dll")]
    internal static extern nint ImmGetDefaultIMEWnd(nint window);

    internal const int WmImeControl = 0x0283;
    internal const int ImcGetConversionMode = 0x0005;
    internal const int SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SendMessageTimeout(
        nint window,
        int message,
        nint wParam,
        nint lParam,
        int flags,
        uint timeoutMilliseconds,
        out nint result);

    internal const int LeftMouseButton = 0x01;
    internal const int RightMouseButton = 0x02;
    internal const int MiddleMouseButton = 0x04;
    internal const int EscapeKey = 0x1B;

    // The search box's global focus hotkey, registered on the bar's own window.
    internal const int WmHotkey = 0x0312;

    // RegisterHotKey's own flag beyond the modifiers: a held hotkey must not fire
    // again on the key's auto-repeat.
    internal const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint window, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    /// <summary>
    /// Whether the button is down now or was pressed since the last call in this
    /// thread; a click that falls entirely between two polls still counts.
    /// </summary>
    internal static bool IsMouseButtonDown(int key)
    {
        var state = GetAsyncKeyState(key);
        return (state & 0x8000) != 0 || (state & 0x1) != 0;
    }

    /// <summary>Whether the key or mouse button is physically held down right now.</summary>
    internal static bool IsKeyCurrentlyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    /// <summary>
    /// Reads and discards the "pressed since the last call" latch. The click that opens
    /// the to-do flyout would otherwise look, one poll later, like a fresh click outside
    /// it and close it right away.
    /// </summary>
    internal static void ConsumeMouseButtonDown(int key) => GetAsyncKeyState(key);

    // Native Wi-Fi (wlanapi.dll) read-only query for active SSID and signal quality.
    internal const uint WlanClientVersionVista = 2;
    internal const int WlanInterfaceStateConnected = 1;
    internal const int WlanIntfOpcodeCurrentConnection = 7;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WlanInterfaceInfo
    {
        internal Guid InterfaceGuid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string InterfaceDescription;

        internal int State;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Dot11Ssid
    {
        internal uint SsidLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        internal byte[] Ssid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WlanAssociationAttributes
    {
        internal Dot11Ssid Dot11Ssid;
        internal int Dot11BssType;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        internal byte[] Dot11Bssid;

        internal int Dot11PhyType;
        internal uint Dot11PhyIndex;
        internal uint WlanSignalQuality;
        internal uint RxRate;
        internal uint TxRate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WlanConnectionAttributes
    {
        internal int State;
        internal int ConnectionMode;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string ProfileName;

        internal WlanAssociationAttributes AssociationAttributes;
    }

    [DllImport("wlanapi.dll")]
    internal static extern uint WlanOpenHandle(
        uint clientVersion,
        nint reserved,
        out uint negotiatedVersion,
        out nint clientHandle);

    [DllImport("wlanapi.dll")]
    internal static extern uint WlanCloseHandle(nint clientHandle, nint reserved);

    [DllImport("wlanapi.dll")]
    internal static extern uint WlanEnumInterfaces(
        nint clientHandle,
        nint reserved,
        out nint interfaceList);

    [DllImport("wlanapi.dll")]
    internal static extern uint WlanQueryInterface(
        nint clientHandle,
        ref Guid interfaceGuid,
        int opCode,
        nint reserved,
        out uint dataSize,
        out nint data,
        nint opcodeValueType);

    [DllImport("wlanapi.dll")]
    internal static extern void WlanFreeMemory(nint memory);

    private const uint CpAcp = 0;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int MultiByteToWideChar(
        uint codePage,
        uint flags,
        byte[] multiByteStr,
        int multiByteLength,
        [Out] char[]? wideCharStr,
        int wideCharLength);

    /// <summary>
    /// Decodes non-UTF-8 bytes using the current Windows system ANSI code page (e.g. GBK /
    /// CP936 on Simplified Chinese Windows) without requiring external encoding packages.
    /// </summary>
    internal static string? DecodeSystemAnsi(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return null;
        }
        var buffer = bytes.ToArray();
        var needed = MultiByteToWideChar(CpAcp, 0, buffer, buffer.Length, null, 0);
        if (needed <= 0)
        {
            return null;
        }
        var chars = new char[needed];
        var written = MultiByteToWideChar(CpAcp, 0, buffer, buffer.Length, chars, chars.Length);
        return written > 0 ? new string(chars, 0, written) : null;
    }
}
