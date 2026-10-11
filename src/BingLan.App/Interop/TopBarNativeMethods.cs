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
        internal byte Reserved1;
        internal int BatteryLifeTime;
        internal int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemPowerStatus(ref SystemPowerStatus status);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetKeyboardLayout(uint idThread);

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

    /// <summary>
    /// Reads and discards the "pressed since the last call" latch. The click that opens
    /// the to-do flyout would otherwise look, one poll later, like a fresh click outside
    /// it and close it right away.
    /// </summary>
    internal static void ConsumeMouseButtonDown(int key) => GetAsyncKeyState(key);
}
