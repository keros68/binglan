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
    private const nint ScTaskList = 0xF130;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;
    private const int ErrorAccessDenied = 5;
    private const string QuickSettingsClass = "ControlCenterWindow";

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

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint token, int informationClass, out int information, int informationLength, out int returnLength);

    internal static bool OpenStartMenu() => SendChord(ControlKey, EscapeKey);

    /// <summary>
    /// Asks Explorer itself to open Start (WM_SYSCOMMAND/SC_TASKLIST). Unlike injected
    /// keys this is not affected by held modifiers or by UIPI when an elevated window is
    /// in front: the message goes to Explorer, which runs at the same integrity level.
    /// </summary>
    internal static bool PostStartMenuCommand()
    {
        var tray = TaskbarNativeMethods.FindWindowW("Shell_TrayWnd", null);
        return tray != 0 && DockNativeMethods.PostMessageW(tray, DockNativeMethods.WmSysCommand, ScTaskList, 0);
    }

    internal static bool OpenQuickLinkMenu() => SendChord(WindowsKey, 0x58);

    internal static bool OpenQuickSettings() => SendChord(WindowsKey, 0x41);

    /// <summary>Closes the quick settings panel only while it holds the foreground.</summary>
    internal static bool CloseQuickSettings() => IsQuickSettingsForeground() && SendKey(EscapeKey);

    internal static bool HasHeldModifier() =>
        new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    internal static bool IsSystemMenuForeground()
    {
        var window = DockNativeMethods.GetForegroundWindow();
        var name = ClassNameOf(window);
        if (name is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Xaml_WindowedPopupClass"
            or "Windows.UI.Core.CoreWindow" or "ControlCenterWindow" or "TopLevelWindowForOverflowXamlIsland")) return false;
        return ProcessNameOf(window) is { } processName && IsSystemMenu(name, processName);
    }

    internal static bool IsStartMenuForeground()
    {
        var window = DockNativeMethods.GetForegroundWindow();
        return ProcessNameOf(window) is { } processName && IsStartMenuSurface(ClassNameOf(window), processName);
    }

    /// <summary>
    /// The Start menu is a CoreWindow of StartMenuExperienceHost; Windows 11 24H2+ hosts
    /// the same menu under SearchHost instead, verified live on 25H2 build 26200.
    /// </summary>
    internal static bool IsStartMenuSurface(string className, string processName) =>
        className == "Windows.UI.Core.CoreWindow"
        && processName is "StartMenuExperienceHost" or "SearchHost";

    internal static bool IsQuickSettingsForeground()
    {
        var window = DockNativeMethods.GetForegroundWindow();
        return ClassNameOf(window) == QuickSettingsClass && IsQuickSettingsProcess(ProcessNameOf(window));
    }

    /// <summary>
    /// Whether a quick settings host window is shown. ShellHost keeps the window while the
    /// panel is closed, so a cloaked or empty window does not count.
    /// </summary>
    internal static bool IsQuickSettingsVisible()
    {
        var found = false;
        DockNativeMethods.EnumWindows((window, _) =>
        {
            if (!DockNativeMethods.IsWindowVisible(window) || ClassNameOf(window) != QuickSettingsClass
                || NativeMethods.IsWindowCloaked(window)
                || !DockNativeMethods.GetWindowRect(window, out var rect)
                || rect.Right <= rect.Left || rect.Bottom <= rect.Top) return true;
            found = IsQuickSettingsProcess(ProcessNameOf(window));
            return !found;
        }, 0);
        return found;
    }

    private static bool IsQuickSettingsProcess(string? processName) =>
        processName is "ShellHost" or "ShellExperienceHost";

    /// <summary>
    /// UIPI silently drops input injected toward a higher-integrity foreground window
    /// (Task Manager, apps run as administrator) while SendInput still reports success.
    /// An elevated BingLan is not affected.
    /// </summary>
    internal static bool IsForegroundElevated()
    {
        if (Environment.IsPrivilegedProcess) return false;
        var window = DockNativeMethods.GetForegroundWindow();
        if (window == 0) return false;
        DockNativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == (uint)Environment.ProcessId) return false;
        var process = DockNativeMethods.OpenProcess(DockNativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (process == 0) return false;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token))
            {
                // The token of a higher-integrity process may refuse the query itself.
                return Marshal.GetLastWin32Error() == ErrorAccessDenied;
            }
            try
            {
                return GetTokenInformation(token, TokenElevationClass, out var elevated, sizeof(int), out _) && elevated != 0;
            }
            finally
            {
                DockNativeMethods.CloseHandle(token);
            }
        }
        finally
        {
            DockNativeMethods.CloseHandle(process);
        }
    }

    private static string ClassNameOf(nint window)
    {
        var className = new StringBuilder(128);
        DockNativeMethods.GetClassNameW(window, className, className.Capacity);
        return className.ToString();
    }

    private static string? ProcessNameOf(nint window)
    {
        try
        {
            DockNativeMethods.GetWindowThreadProcessId(window, out var processId);
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    internal static bool IsSystemMenu(string className, string processName) =>
        (processName == "explorer" && className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Xaml_WindowedPopupClass" or "TopLevelWindowForOverflowXamlIsland")
        || IsStartMenuSurface(className, processName)
        || (processName is "ShellHost" or "ShellExperienceHost" && className == "ControlCenterWindow");

    internal static bool SendChord(ushort modifier, ushort key) => SendChord([modifier], key);

    internal static bool SendChord(ushort[] modifiers, ushort key)
    {
        // Do not release a modifier the user is holding, or turn a modified click
        // into another shortcut. Entry points send documented Windows shortcuts or an
        // app's own registered hotkey.
        if (HasHeldModifier())
        {
            return false;
        }
        Input Key(ushort key, bool up = false) => new()
        {
            Type = InputKeyboard,
            Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? KeyUp : 0 } }
        };
        var releases = new[] { Key(key, true) }
            .Concat(modifiers.Reverse().Select(modifier => Key(modifier, true)))
            .ToArray();
        var inputs = modifiers.Select(modifier => Key(modifier))
            .Append(Key(key))
            .Concat(releases)
            .ToArray();
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent > 0 && sent < inputs.Length)
        {
            _ = SendInput((uint)releases.Length, releases, Marshal.SizeOf<Input>());
        }
        if (sent != inputs.Length) Debug.WriteLine($"SendInput 未完整发送：{sent}/{inputs.Length} error={Marshal.GetLastWin32Error()}");
        return sent == inputs.Length;
    }

    private static bool SendKey(ushort key)
    {
        if (HasHeldModifier()) return false;
        var inputs = new[]
        {
            new Input { Type = InputKeyboard, Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = key } } },
            new Input { Type = InputKeyboard, Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = key, Flags = KeyUp } } }
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }
}
