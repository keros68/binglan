using System.Runtime.InteropServices;
using System.Diagnostics;
using System.ComponentModel;
using System.Text;

namespace BingLan.App.Interop;

internal static class ShellSurfaceNativeMethods
{
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const ushort TabKey = 0x09;
    private const ushort ControlKey = 0x11;
    private const ushort EscapeKey = 0x1B;
    private const ushort WindowsKey = 0x5B;
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const int VkMButton = 0x04;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        internal uint Size, Tick;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        nint token, int informationClass, out int information, int informationLength, out int returnLength);

    internal static bool OpenStartMenu() => SendChord(ControlKey, EscapeKey);

    internal static bool ShowDesktop() => SendChord(WindowsKey, 0x44);

    internal static bool OpenTaskView() => SendChord(WindowsKey, TabKey);

    internal static uint? GetLastInputTick()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? info.Tick : null;
    }

    /// <summary>
    /// Asks Explorer itself to open Start (WM_SYSCOMMAND/SC_TASKLIST). Before posting,
    /// grants Explorer foreground activation rights because a click on a WS_EX_NOACTIVATE
    /// dock leaves last-input ownership with BingLan.
    /// </summary>
    internal static bool PostStartMenuCommand()
    {
        var tray = TaskbarNativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (tray == 0)
        {
            return false;
        }

        DockNativeMethods.GetWindowThreadProcessId(tray, out var explorerPid);
        if (explorerPid != 0)
        {
            _ = DockNativeMethods.AllowSetForegroundWindow(explorerPid);
        }
        return DockNativeMethods.PostMessageW(tray, DockNativeMethods.WmSysCommand, ScTaskList, 0);
    }

    internal static bool OpenQuickLinkMenu() => SendChord(WindowsKey, 0x58);

    internal static bool OpenQuickSettings() => SendChord(WindowsKey, 0x41);

    /// <summary>Closes the quick settings panel only while it holds the foreground.</summary>
    internal static bool CloseQuickSettings() => IsQuickSettingsForeground() && SendKey(EscapeKey);

    internal static bool HasHeldModifier() =>
        new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    internal static bool IsAnyMouseButtonPressed() =>
        (GetAsyncKeyState(VkLButton) & 0x8000) != 0
        || (GetAsyncKeyState(VkRButton) & 0x8000) != 0
        || (GetAsyncKeyState(VkMButton) & 0x8000) != 0;

    internal static bool IsSystemMenuForeground() =>
        TryGetForegroundSystemMenu(out var kind, out _) && kind != BingLan.Core.Dock.SystemSurfaceKind.None;

    internal static bool TryGetForegroundSystemMenu(
        out BingLan.Core.Dock.SystemSurfaceKind surfaceKind,
        out BingLan.Core.Dock.PixelRect bounds) =>
        TryGetSystemMenu(DockNativeMethods.GetForegroundWindow(), out surfaceKind, out bounds);

    internal static bool TryGetSystemMenu(
        nint window,
        out BingLan.Core.Dock.SystemSurfaceKind surfaceKind,
        out BingLan.Core.Dock.PixelRect bounds)
    {
        surfaceKind = BingLan.Core.Dock.SystemSurfaceKind.None;
        bounds = default;
        var name = ClassNameOf(window);
        // Keyboard focus can sit in a XAML child of the native taskbar or tray panel.
        if (name is "Windows.UI.Composition.DesktopWindowContentBridge" or "Windows.UI.Input.InputSite.WindowClass")
        {
            window = DockNativeMethods.GetAncestor(window, DockNativeMethods.GaRoot);
            name = ClassNameOf(window);
        }
        if (name is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "Windows.UI.Core.CoreWindow" or "ControlCenterWindow") && !IsTaskbarPopupClass(name)) return false;
        if (ProcessNameOf(window) is not { } processName)
        {
            return false;
        }

        surfaceKind = ClassifySystemMenu(name, processName);
        if (surfaceKind == BingLan.Core.Dock.SystemSurfaceKind.None)
        {
            return false;
        }

        if ((surfaceKind is BingLan.Core.Dock.SystemSurfaceKind.Taskbar or BingLan.Core.Dock.SystemSurfaceKind.TaskbarPopup)
            && (!DockNativeMethods.IsWindowVisible(window) || NativeMethods.IsWindowCloaked(window)))
        {
            return true;
        }

        if ((surfaceKind == BingLan.Core.Dock.SystemSurfaceKind.TaskbarPopup
            && DockNativeMethods.DwmGetWindowAttribute(
                window, DockNativeMethods.DwmwaExtendedFrameBounds, out DockNativeMethods.NativeRect rect,
                Marshal.SizeOf<DockNativeMethods.NativeRect>()) == 0)
            || DockNativeMethods.GetWindowRect(window, out rect))
        {
            bounds = new BingLan.Core.Dock.PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }
        return true;
    }

    internal static bool IsStartMenuForeground()
    {
        var window = DockNativeMethods.GetForegroundWindow();
        return ProcessNameOf(window) is { } processName && IsStartMenuSurface(ClassNameOf(window), processName);
    }

    /// <summary>
    /// The Start menu is a CoreWindow of StartMenuExperienceHost (or SearchHost on
    /// Windows 11 24H2+, or ShellExperienceHost on older builds); classic/third-party
    /// menus hosted by explorer use DV2ControlHost or OpenShellMenu.CMenuContainer.
    /// </summary>
    internal static bool IsStartMenuSurface(string className, string processName) =>
        (className == "Windows.UI.Core.CoreWindow"
            && processName is "StartMenuExperienceHost" or "SearchHost" or "ShellExperienceHost")
        || (processName == "explorer"
            && className is "DV2ControlHost" or "OpenShellMenu.CMenuContainer");

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
    /// UIPI blocks or drops input injected toward a higher-integrity foreground window
    /// (Task Manager, apps run as administrator); SendInput returns 0 under UIPI on
    /// Windows 11, and SetForegroundWindow/ShowWindowAsync also fail across integrity
    /// levels. An elevated BingLan is not affected.
    /// </summary>
    internal static bool IsForegroundElevated() =>
        IsWindowElevated(DockNativeMethods.GetForegroundWindow());

    internal static bool IsTaskbarElevated()
    {
        var tray = TaskbarNativeMethods.FindWindowW("Shell_TrayWnd", null);
        return tray != 0 && IsWindowElevated(tray);
    }

    internal static bool IsWindowElevated(nint window)
    {
        if (Environment.IsPrivilegedProcess || window == 0) return false;
        DockNativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == (uint)Environment.ProcessId) return false;
        var process = DockNativeMethods.OpenProcess(DockNativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return Marshal.GetLastWin32Error() == ErrorAccessDenied;
        }
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

    internal static BingLan.Core.Dock.SystemSurfaceKind ClassifySystemMenu(string className, string processName)
    {
        if (processName == "explorer")
        {
            if (className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                return BingLan.Core.Dock.SystemSurfaceKind.Taskbar;
            }
            if (IsTaskbarPopupClass(className))
            {
                return BingLan.Core.Dock.SystemSurfaceKind.TaskbarPopup;
            }
        }

        if (IsStartMenuSurface(className, processName)
            || (processName is "ShellHost" or "ShellExperienceHost" && className == "ControlCenterWindow"))
        {
            return BingLan.Core.Dock.SystemSurfaceKind.GlobalSystemMenu;
        }

        return BingLan.Core.Dock.SystemSurfaceKind.None;
    }

    internal static bool IsSystemMenu(string className, string processName) =>
        ClassifySystemMenu(className, processName) != BingLan.Core.Dock.SystemSurfaceKind.None;

    internal static bool IsExplorerTaskbarPopup(nint window, string className) =>
        IsTaskbarPopupClass(className) && ProcessNameOf(window) == "explorer";

    private static bool IsTaskbarPopupClass(string className) =>
        className is "Xaml_WindowedPopupClass" or "TopLevelWindowForOverflowXamlIsland" or "NotifyIconOverflowWindow";

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
