using System.Runtime.InteropServices;

namespace BingLan.App.Interop;

internal static class ShellSurfaceNativeMethods
{
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const ushort ControlKey = 0x11;
    private const ushort EscapeKey = 0x1B;

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

    internal static bool OpenStartMenu()
    {
        // Ctrl+Esc is Windows' documented Start-menu shortcut. Do not release a
        // modifier the user is holding, or turn a modified click into another shortcut.
        if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0))
        {
            return false;
        }
        Input Key(ushort key, bool up = false) => new()
        {
            Type = InputKeyboard,
            Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? KeyUp : 0 } }
        };
        var inputs = new[] { Key(ControlKey), Key(EscapeKey), Key(EscapeKey, true), Key(ControlKey, true) };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent > 0 && sent < inputs.Length)
        {
            var releases = new[] { Key(EscapeKey, true), Key(ControlKey, true) };
            _ = SendInput((uint)releases.Length, releases, Marshal.SizeOf<Input>());
        }
        return sent == inputs.Length;
    }
}
