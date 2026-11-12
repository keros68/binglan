using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using BingLan.App.Interop;

namespace BingLan.App.Services;

internal enum TrayTipKind : uint
{
    None = 0,
    Info = 1,
    Warning = 2
}

internal sealed class TrayMenuItem
{
    private readonly Action? _click;

    internal TrayMenuItem(string text, Action? click, bool isSeparator = false)
    {
        Text = text;
        _click = click;
        IsSeparator = isSeparator;
    }

    public string Text { get; set; }
    public bool Visible { get; set; } = true;
    internal bool IsSeparator { get; }

    public void PerformClick() => _click?.Invoke();
}

/// <summary>
/// Win32 notification-area icon and popup menu host built on Shell_NotifyIconW.
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;

    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const uint WmTrayCallback = 0x0400 + 2048;
    private const int WmLeftButtonDoubleClick = 0x0203;
    private const int WmRightButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const int NinBalloonHide = 0x0403;
    private const int NinBalloonTimeout = 0x0404;
    private const int NinBalloonUserClick = 0x0405;

    private const uint WsPopup = 0x80000000;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;
    private const nint IdiApplication = 32512;

    private static readonly uint TaskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
    private static int _nextClassId;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly List<TrayMenuItem> _items = [];
    private readonly NativeMethods.WindowProcedure _windowProc;
    private readonly string _className;
    private readonly bool _classRegistered;
    private nint _window;
    private nint _iconHandle;
    private bool _ownsIcon;
    private bool _visible;
    private bool _added;
    private bool _disposed;
    private string _text = string.Empty;

    internal TrayIconHost(string text)
    {
        _text = text;
        _windowProc = WindowProc;
        _className = $"BingLanTrayHost_{Interlocked.Increment(ref _nextClassId)}";
        var cls = new NativeMethods.WindowClass
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WindowClass>(),
            Procedure = _windowProc,
            Name = _className
        };
        _classRegistered = NativeMethods.RegisterClassEx(ref cls) != 0;
        if (_classRegistered)
        {
            // Must be a hidden top-level window (not HWND_MESSAGE) so Explorer's broadcast
            // of TaskbarCreated and session-ending notifications reach us.
            _window = NativeMethods.CreateWindowEx(
                (uint)NativeMethods.WsExToolWindow,
                _className,
                string.Empty,
                WsPopup,
                0, 0, 0, 0, 0, 0, 0, 0);
        }
        if (_window == 0)
        {
            StartupLog.Write("托盘宿主窗口创建失败");
        }
        (_iconHandle, _ownsIcon) = LoadApplicationIcon();
    }

    internal event EventHandler? DoubleClick;
    internal event EventHandler? BalloonTipClicked;
    internal event EventHandler? BalloonTipClosed;
    /// <summary>Raised on WM_ENDSESSION: true when the session is ending, false when shutdown was cancelled.</summary>
    internal event Action<bool>? SessionEndingResolved;

    internal IReadOnlyList<TrayMenuItem> Items => _items;

    internal TrayMenuItem AddItem(string text, Action click)
    {
        var item = new TrayMenuItem(text, click);
        _items.Add(item);
        return item;
    }

    internal void AddSeparator() => _items.Add(new TrayMenuItem(string.Empty, null, isSeparator: true));

    internal void InvokeDoubleClick() => DoubleClick?.Invoke(this, EventArgs.Empty);

    internal bool Visible
    {
        get => _visible;
        set
        {
            if (_disposed || _visible == value)
            {
                return;
            }
            _visible = value;
            if (_visible)
            {
                AddOrUpdateShellIcon(forceAdd: true);
            }
            else if (_added)
            {
                var data = CreateBaseData();
                Shell_NotifyIconW(NimDelete, ref data);
                _added = false;
            }
        }
    }

    internal void ShowBalloonTip(int timeoutMilliseconds, string title, string message, TrayTipKind kind)
    {
        if (_disposed || !_visible || _window == 0)
        {
            return;
        }
        if (!_added)
        {
            AddOrUpdateShellIcon(forceAdd: true);
        }

        var data = CreateBaseData();
        data.Flags |= NifInfo;
        data.TimeoutOrVersion = (uint)Math.Clamp(timeoutMilliseconds, 1000, 30000);
        data.InfoTitle = Truncate(title, 63);
        data.Info = Truncate(message, 255);
        data.InfoFlags = (uint)kind;
        Shell_NotifyIconW(NimModify, ref data);
    }

    private void AddOrUpdateShellIcon(bool forceAdd)
    {
        if (_disposed || _window == 0)
        {
            return;
        }
        var data = CreateBaseData();
        if (forceAdd || !_added)
        {
            _added = Shell_NotifyIconW(NimAdd, ref data) || Shell_NotifyIconW(NimModify, ref data);
        }
        else if (!Shell_NotifyIconW(NimModify, ref data))
        {
            _added = Shell_NotifyIconW(NimAdd, ref data);
        }
    }

    private NotifyIconData CreateBaseData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _window,
        Id = 1,
        Flags = NifMessage | NifIcon | NifTip,
        CallbackMessage = WmTrayCallback,
        Icon = _iconHandle,
        Tip = Truncate(_text, 127),
        Info = string.Empty,
        InfoTitle = string.Empty
    };

    private static string Truncate(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
        {
            return value ?? string.Empty;
        }
        var length = maxChars > 0 && char.IsHighSurrogate(value[maxChars - 1])
            ? maxChars - 1
            : maxChars;
        return value[..length];
    }

    private nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmQueryEndSession)
        {
            return 1;
        }

        if (message == WmEndSession && !_disposed)
        {
            SessionEndingResolved?.Invoke(wParam != 0);
            return 0;
        }

        if (!_disposed && TaskbarCreatedMessage != 0 && message == TaskbarCreatedMessage)
        {
            _added = false;
            if (_visible)
            {
                AddOrUpdateShellIcon(forceAdd: true);
            }
            return 0;
        }

        if (message == WmTrayCallback && !_disposed)
        {
            var notification = (int)(lParam & 0xFFFF);
            switch (notification)
            {
                case WmLeftButtonDoubleClick:
                    _dispatcher.BeginInvoke(InvokeDoubleClick);
                    return 0;
                case WmRightButtonUp:
                case WmContextMenu:
                    _dispatcher.BeginInvoke(ShowContextMenu);
                    return 0;
                case NinBalloonUserClick:
                    _dispatcher.BeginInvoke(() => BalloonTipClicked?.Invoke(this, EventArgs.Empty));
                    return 0;
                case NinBalloonHide:
                case NinBalloonTimeout:
                    _dispatcher.BeginInvoke(() => BalloonTipClosed?.Invoke(this, EventArgs.Empty));
                    return 0;
            }
        }

        return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (_disposed || _window == 0)
        {
            return;
        }

        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            var commandMap = new Dictionary<int, TrayMenuItem>();
            var nextId = 1;
            var lastWasSeparator = true;

            foreach (var item in _items)
            {
                if (!item.Visible)
                {
                    continue;
                }
                if (item.IsSeparator)
                {
                    if (!lastWasSeparator)
                    {
                        AppendMenuW(menu, MfSeparator, 0, null);
                        lastWasSeparator = true;
                    }
                    continue;
                }

                var id = nextId++;
                commandMap[id] = item;
                AppendMenuW(menu, MfString, (nuint)id, item.Text);
                lastWasSeparator = false;
            }

            if (commandMap.Count == 0)
            {
                return;
            }

            DockNativeMethods.GetCursorPos(out var cursor);
            // Documented Shell_NotifyIcon requirement (KB135788): make the hidden owner
            // foreground before TrackPopupMenuEx and post WM_NULL afterwards so clicking
            // anywhere outside dismisses the menu reliably.
            DockNativeMethods.SetForegroundWindow(_window);
            var selected = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd, cursor.X, cursor.Y, _window, 0);
            PostMessageW(_window, 0, 0, 0);

            if (selected != 0 && commandMap.TryGetValue(selected, out var chosen))
            {
                chosen.PerformClick();
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static (nint Handle, bool Owned) LoadApplicationIcon()
    {
        try
        {
            if (System.Windows.Application.Current is not null)
            {
                var resource = System.Windows.Application.GetResourceStream(
                    new Uri("pack://application:,,,/BingLan;component/Assets/Brand/BingLan.ico", UriKind.Absolute));
                if (resource is not null)
                {
                    using var stream = resource.Stream;
                    using var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    var bytes = memory.ToArray();
                    if (TryCreateIconFromIcoBytes(bytes) is var handle and not 0)
                    {
                        return (handle, true);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UriFormatException)
        {
            // Unit/smoke hosts without registered pack:// scheme fall back to default app icon.
        }

        return (LoadIconW(0, IdiApplication), false);
    }

    private static nint TryCreateIconFromIcoBytes(ReadOnlySpan<byte> ico)
    {
        if (ico.Length < 6)
        {
            return 0;
        }
        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(ico[..2]);
        var type = BinaryPrimitives.ReadUInt16LittleEndian(ico.Slice(2, 2));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(ico.Slice(4, 2));
        if (reserved != 0 || type != 1 || count == 0 || ico.Length < 6 + (count * 16))
        {
            return 0;
        }

        var targetWidth = Math.Max(16, GetSystemMetrics(SmCxSmIcon));
        var targetHeight = Math.Max(16, GetSystemMetrics(SmCySmIcon));
        var bestOffset = -1;
        var bestLength = 0;
        var bestScore = int.MaxValue;

        for (var i = 0; i < count; i++)
        {
            var entry = ico.Slice(6 + (i * 16), 16);
            var width = entry[0] == 0 ? 256 : entry[0];
            var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(6, 2));
            var length = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(8, 4));
            var offset = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(12, 4));
            if (length <= 0 || offset < 0 || (long)offset + length > ico.Length)
            {
                continue;
            }

            // Prefer >= target size (downscaling is sharper than upscaling) and 32-bpp alpha.
            var sizeDelta = width >= targetWidth ? (width - targetWidth) : (targetWidth - width) * 4;
            var depthPenalty = bitCount >= 32 ? 0 : 100;
            var score = sizeDelta + depthPenalty;
            if (score < bestScore)
            {
                bestScore = score;
                bestOffset = offset;
                bestLength = length;
            }
        }

        if (bestOffset < 0)
        {
            return 0;
        }

        ref var resourceStart = ref MemoryMarshal.GetReference(ico.Slice(bestOffset, bestLength));
        return CreateIconFromResourceEx(ref resourceStart, (uint)bestLength, true, 0x00030000, targetWidth, targetHeight, 0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_added && _window != 0)
        {
            var data = CreateBaseData();
            Shell_NotifyIconW(NimDelete, ref data);
            _added = false;
        }
        _visible = false;
        if (_window != 0)
        {
            NativeMethods.DestroyWindow(_window);
            _window = 0;
        }
        if (_ownsIcon && _iconHandle != 0)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = 0;
        }
        if (_classRegistered)
        {
            UnregisterClassW(_className, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        internal uint Size;
        internal nint Window;
        internal uint Id;
        internal uint Flags;
        internal uint CallbackMessage;
        internal nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string Tip;
        internal uint State;
        internal uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string Info;
        internal uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid GuidItem;
        internal nint BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(nint menu, uint flags, nuint newItemId, string? newItem);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint paramsPtr);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint LoadIconW(nint instance, nint iconName);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint CreateIconFromResourceEx(
        ref byte presbits,
        uint dwResSize,
        [MarshalAs(UnmanagedType.Bool)] bool fIcon,
        uint dwVer,
        int cxDesired,
        int cyDesired,
        uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string className, nint instance);
}
