using System.Runtime.InteropServices;
using System.Text;

namespace BingLan.App.Interop;

internal static class TaskbarNativeMethods
{
    internal const int WcaAccentPolicy = 19;
    internal const int AccentDisabled = 0;
    internal const int AccentEnableBlurBehind = 3;
    internal const int AccentEnableAcrylicBlurBehind = 4;
    internal const int SmRemoteSession = 0x1000;
    internal const uint AbmGetState = 0x00000004;
    internal const uint AbmSetState = 0x0000000A;
    internal const int AbsAutoHide = 0x0000001;
    internal const int WmSettingChange = 0x001A;
    internal const int WmThemeChanged = 0x031A;
    internal const int WmDwmColorizationColorChanged = 0x0320;
    internal const int WmDisplayChange = 0x007E;
    internal const uint EventSystemForeground = 0x0003;
    internal const uint EventObjectShow = 0x8002;
    internal const uint EventObjectHide = 0x8003;
    internal const uint EventObjectCloaked = 0x8017;
    internal const uint EventObjectUncloaked = 0x8018;
    internal const uint WineventOutOfContext = 0x0000;
    internal const uint WineventSkipOwnProcess = 0x0002;
    internal const int ObjidWindow = 0;

    internal delegate bool EnumWindowsProc(nint window, nint parameter);

    /// <summary>
    /// Applies the WCA acrylic accent to one window. The tint is an ABGR value whose
    /// alpha sets how strongly the blurred backdrop is darkened; false means the
    /// composition attribute is unavailable and the caller keeps its plain brush.
    /// </summary>
    internal static bool TryEnableAcrylicBlur(nint window, uint tintAbgr)
    {
        var policy = new AccentPolicy
        {
            State = AccentEnableAcrylicBlurBehind,
            GradientColor = tintAbgr
        };
        var size = Marshal.SizeOf<AccentPolicy>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, pointer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = pointer,
                SizeOfData = size
            };
            return SetWindowCompositionAttribute(window, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    internal delegate void WinEventProc(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    internal struct AccentPolicy
    {
        internal int State;
        internal int Flags;
        internal uint GradientColor;
        internal int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowCompositionAttributeData
    {
        internal int Attribute;
        internal nint Data;
        internal int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AppBarData
    {
        internal uint Size;
        internal nint Window;
        internal uint CallbackMessage;
        internal uint Edge;
        internal Rect Rectangle;
        internal nint Parameter;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassNameW(nint window, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    // Undocumented for general use; Microsoft recommends DwmSetWindowAttribute, which has
    // no equivalent for Explorer's taskbar. Only used behind the Windows 11 build gate.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowCompositionAttribute(
        nint window,
        ref WindowCompositionAttributeData data);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint FindWindowW(string? className, string? windowName);

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

    [DllImport("shell32.dll")]
    internal static extern nuint SHAppBarMessage(uint message, ref AppBarData data);
}
