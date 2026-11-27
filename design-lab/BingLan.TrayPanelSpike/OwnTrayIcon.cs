using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace BingLan.TrayPanelSpike;

internal sealed class OwnTrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 137;
    private readonly HwndSource _source;
    private bool _disposed;
    private const nuint MenuTimerId = 0x771;
    private bool _menuActive;
    internal bool IsMenuOpen => _menuActive;
    internal string Name { get; } = $"BingLan Tray Spike {Environment.ProcessId}";
    internal nint Handle => _source.Handle;
    internal bool Added { get; }
    internal bool Version4 { get; }
    internal int SelectionCount { get; private set; }
    internal int ContextCount { get; private set; }
    internal int MenuPopupCount { get; private set; }
    internal List<string> Messages { get; } = [];
    internal event Action? Callback;

    internal OwnTrayIcon()
    {
        _source = new HwndSource(new HwndSourceParameters("BingLanTraySpikeCallback")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000),
            ExtendedWindowStyle = 0x80
        });
        _source.AddHook(WindowProc);
        var data = Data();
        data.Flags = 1 | 2 | 4 | 128; // MESSAGE, ICON, TIP, SHOWTIP (version 4).
        data.CallbackMessage = CallbackMessage;
        data.Icon = LoadIconW(0, 32512);
        data.Tip = Name;
        Added = Shell_NotifyIconW(0, ref data);
        if (Added)
        {
            data.TimeoutOrVersion = 4;
            Version4 = Shell_NotifyIconW(4, ref data);
        }
        Console.WriteLine($"OWN NIM_ADD={Added}; NIM_SETVERSION={Version4}; name={Name}; hwnd=0x{Handle:X}");
    }

    private nint WindowProc(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_menuActive && message == 0x0113 && (nuint)wParam == MenuTimerId)
        {
            Console.WriteLine($"OWN-MENU-TIMEOUT utc={DateTime.UtcNow:O}");
            CloseMenu();
            handled = true;
            return 0;
        }
        if (_menuActive && message is 0x0211 or 0x0117)
        {
            if (message == 0x0117) MenuPopupCount++;
            Console.WriteLine($"OWN-MENU-MESSAGE=0x{message:X4}; utc={DateTime.UtcNow:O}");
        }
        if (message != CallbackMessage) return 0;
        var notification = Version4 ? (int)(lParam.ToInt64() & 0xFFFF) : (int)lParam;
        Messages.Add($"0x{notification:X4}");
        // Version 4 sends mouse notifications plus NIN_SELECT for one activation.
        // Count the semantic selection once, retaining every raw message as evidence.
        if (Version4 ? notification is 0x0400 or 0x0401 : notification == 0x0202) SelectionCount++;
        var contextRequested = Version4 ? notification == 0x007B : notification == 0x0205;
        if (contextRequested) ContextCount++;
        Console.WriteLine($"OWN-CALLBACK=0x{notification:X4}; selections={SelectionCount}; contexts={ContextCount}; utc={DateTime.UtcNow:O}");
        Callback?.Invoke();
        if (contextRequested) ShowTestMenu();
        return 0;
    }

    private void ShowTestMenu()
    {
        if (_disposed || _menuActive) return;
        var menu = CreatePopupMenu();
        if (menu == 0) { Console.WriteLine("OWN-MENU-CREATE=False"); return; }
        try
        {
            if (!AppendMenuW(menu, 0x2, 0, "冰蓝托盘测试菜单")
                || !AppendMenuW(menu, 0x800, 0, null)
                || !AppendMenuW(menu, 0, 1, "关闭测试菜单"))
            {
                Console.WriteLine("OWN-MENU-ITEMS=False");
                return;
            }
            if (!GetCursorPos(out var point)) { Console.WriteLine("OWN-MENU-POSITION=False"); return; }
            _menuActive = true;
            if (SetTimer(Handle, MenuTimerId, 4000, 0) == 0)
            {
                Console.WriteLine("OWN-MENU-TIMER=False");
                return;
            }
            // Notification-area menus use a foreground owner and WM_NULL afterwards
            // so a click outside closes them. This owner has no taskbar button.
            var foreground = SetForegroundWindow(Handle);
            Console.WriteLine($"OWN-MENU-TRACK-BEGIN menu=0x{menu:X}; x={point.X}; y={point.Y}; foreground={foreground}; utc={DateTime.UtcNow:O}");
            const uint returnCommand = 0x0100, rightButton = 0x0002;
            var selected = TrackPopupMenuEx(menu, returnCommand | rightButton, point.X, point.Y, Handle, 0);
            PostMessageW(Handle, 0, 0, 0);
            Console.WriteLine($"OWN-MENU-TRACK-END selected={selected}; utc={DateTime.UtcNow:O}");
        }
        finally
        {
            KillTimer(Handle, MenuTimerId);
            _menuActive = false;
            DestroyMenu(menu);
        }
    }

    internal void CloseMenu()
    {
        if (_menuActive) EndMenu();
    }

    private NotifyIconData Data() => new() { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = Handle, Id = 1, Tip = "", Info = "", InfoTitle = "" };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Added)
        {
            var data = Data();
            Console.WriteLine($"OWN NIM_DELETE={Shell_NotifyIconW(2, ref data)}");
        }
        _source.RemoveHook(WindowProc);
        _source.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        internal uint Size;
        internal nint Window;
        internal uint Id, Flags, CallbackMessage;
        internal nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
        internal uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid GuidItem;
        internal nint BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadIconW(nint instance, nint icon);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nuint SetTimer(nint window, nuint id, uint interval, nint callback);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool KillTimer(nint window, nuint id);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EndMenu();
    [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
}
