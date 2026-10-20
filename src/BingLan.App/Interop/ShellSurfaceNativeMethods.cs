using System.Runtime.InteropServices;
using System.Diagnostics;
using System.ComponentModel;
using System.Text;

namespace BingLan.App.Interop;

internal static class ShellSurfaceNativeMethods
{
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const ushort ControlKey = 0x11;
    private const ushort EscapeKey = 0x1B;
    private const ushort WindowsKey = 0x5B;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        internal uint Type;
        internal InputData Data;
    }

    // INPUT contains a union: its size/alignment must include MOUSEINPUT even for
    // keyboard-only input (40 bytes on x64, 28 on x86).
    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] internal KeyboardInput Keyboard;
        [FieldOffset(0)] internal MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        internal ushort VirtualKey, Scan;
        internal uint Flags, Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int X, Y;
        internal uint MouseData, Flags, Time;
        internal nuint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    internal static bool OpenStartMenu() => SendChord(ControlKey, EscapeKey);

    internal static bool OpenQuickLinkMenu() => SendChord(WindowsKey, 0x58);

    internal static bool OpenQuickSettings() => SendChord(WindowsKey, 0x41);

    internal static bool HasHeldModifier() =>
        new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    internal static bool IsSystemMenuForeground()
    {
        var window = DockNativeMethods.GetForegroundWindow();
        var className = new StringBuilder(128);
        DockNativeMethods.GetClassNameW(window, className, className.Capacity);
        var name = className.ToString();
        if (name is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Xaml_WindowedPopupClass"
            or "Windows.UI.Core.CoreWindow" or "ControlCenterWindow" or "TopLevelWindowForOverflowXamlIsland")) return false;
        try
        {
            DockNativeMethods.GetWindowThreadProcessId(window, out var processId);
            using var process = Process.GetProcessById((int)processId);
            return IsSystemMenu(name, process.ProcessName);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    internal static bool IsSystemMenu(string className, string processName) =>
        (processName == "explorer" && className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Xaml_WindowedPopupClass" or "TopLevelWindowForOverflowXamlIsland")
        || (processName == "StartMenuExperienceHost" && className == "Windows.UI.Core.CoreWindow")
        || (processName is "ShellHost" or "ShellExperienceHost" && className == "ControlCenterWindow");

    internal static bool SendChord(ushort modifier, ushort key)
    {
        // Do not release a modifier the user is holding, or turn a modified click
        // into another shortcut. All entry points use documented Windows shortcuts.
        if (HasHeldModifier())
        {
            return false;
        }
        Input Key(ushort key, bool up = false) => new()
        {
            Type = InputKeyboard,
            Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? KeyUp : 0 } }
        };
        var inputs = new[] { Key(modifier), Key(key), Key(key, true), Key(modifier, true) };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent > 0 && sent < inputs.Length)
        {
            var releases = new[] { Key(key, true), Key(modifier, true) };
            _ = SendInput((uint)releases.Length, releases, Marshal.SizeOf<Input>());
        }
        return sent == inputs.Length;
    }
}
