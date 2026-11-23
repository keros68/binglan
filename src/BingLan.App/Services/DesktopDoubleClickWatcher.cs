using System.Runtime.InteropServices;
using System.Windows.Threading;
using BingLan.App.Interop;
using BingLan.Core.Desktop;

namespace BingLan.App.Services;

/// <summary>
/// Opt-in Raw Input (<c>RIDEV_INPUTSINK</c>) listener running on a dedicated background
/// Win32 message thread so high-polling-rate mice never press the WPF UI thread.
/// Created only when enabled; ignores everything except <c>RI_MOUSE_LEFT_BUTTON_DOWN</c>
/// on the first line and posts only matched double-click candidates to the UI dispatcher.
/// Disposing unregisters the Raw Input device, destroys the message-only window and stops
/// the background thread cleanly.
/// </summary>
internal sealed class DesktopDoubleClickWatcher : IDisposable
{
    private static readonly uint RawInputHeaderSize =
        (uint)Marshal.SizeOf<DesktopDoubleClickNativeMethods.RawInputHeader>();
    private static readonly uint RawInputMouseSize =
        (uint)Marshal.SizeOf<DesktopDoubleClickNativeMethods.RawInputMouse>();
    private static readonly uint RawInputDeviceSize =
        (uint)Marshal.SizeOf<DesktopDoubleClickNativeMethods.RawInputDevice>();
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly Dispatcher _dispatcher;
    private readonly string _className;
    // Keep the delegate rooted for the full lifetime of the Win32 window class.
    private readonly NativeMethods.WindowProcedure _windowProc;
    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private nint _hwnd;
    private bool _rawInputRegistered;
    private DesktopClickStamp? _pendingClick;
    private int _started;
    private int _disposed;

    internal DesktopDoubleClickWatcher(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        _className = $"BingLanDesktopDoubleClick_{Guid.NewGuid():N}";
        _windowProc = WndProc;
    }

    internal event Action<int, int>? DoubleClickDetected;

    internal bool IsRegistered => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _rawInputRegistered);

    internal bool Start()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return IsRegistered;
        }

        var thread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "BingLan.DesktopDoubleClick"
        };
        _thread = thread;
        thread.Start();

        _ready.Wait(StartupTimeout);
        return IsRegistered;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Wait briefly if Start() is still bringing up the message-only window so WM_CLOSE
        // never races with HWND creation.
        if (Volatile.Read(ref _started) != 0)
        {
            _ready.Wait(StartupTimeout);
        }

        var hwnd = Interlocked.CompareExchange(ref _hwnd, 0, 0);
        if (hwnd != 0)
        {
            _ = DockNativeMethods.PostMessageW(hwnd, DesktopDoubleClickNativeMethods.WmClose, 0, 0);
        }

        var thread = _thread;
        if (thread is not null && thread != Thread.CurrentThread && thread.IsAlive)
        {
            thread.Join(ShutdownTimeout);
        }

        _ready.Dispose();
    }

    private void RunMessageLoop()
    {
        var instance = DesktopDoubleClickNativeMethods.GetModuleHandleW(null);
        var classRegistered = false;
        nint hwnd = 0;

        try
        {
            var windowClass = new NativeMethods.WindowClass
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.WindowClass>(),
                Procedure = _windowProc,
                Instance = instance,
                Name = _className
            };
            if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
            {
                return;
            }
            classRegistered = true;

            hwnd = NativeMethods.CreateWindowEx(
                0,
                _className,
                "BingLanDesktopDoubleClick",
                0,
                0,
                0,
                0,
                0,
                DesktopDoubleClickNativeMethods.HwndMessage,
                0,
                instance,
                0);
            if (hwnd == 0)
            {
                return;
            }

            Interlocked.Exchange(ref _hwnd, hwnd);

            if (Volatile.Read(ref _disposed) != 0 || !RegisterRawInput(hwnd))
            {
                return;
            }

            _ready.Set();

            while (DesktopDoubleClickNativeMethods.GetMessageW(out var message, 0, 0, 0) > 0)
            {
                DesktopDoubleClickNativeMethods.DispatchMessageW(ref message);
            }
        }
        finally
        {
            UnregisterRawInput();
            var liveHwnd = Interlocked.Exchange(ref _hwnd, 0);
            if (liveHwnd != 0)
            {
                _ = NativeMethods.DestroyWindow(liveHwnd);
            }
            if (classRegistered)
            {
                _ = DesktopDoubleClickNativeMethods.UnregisterClassW(_className, instance);
            }
            // Ensure Start() / Dispose() never stall even on early initialization failure.
            if (Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    _ready.Set();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    private bool RegisterRawInput(nint hwnd)
    {
        var device = new DesktopDoubleClickNativeMethods.RawInputDevice
        {
            UsagePage = DesktopDoubleClickNativeMethods.HidUsagePageGeneric,
            Usage = DesktopDoubleClickNativeMethods.HidUsageGenericMouse,
            Flags = DesktopDoubleClickNativeMethods.RidevInputSink,
            Target = hwnd
        };
        var ok = DesktopDoubleClickNativeMethods.RegisterRawInputDevices(ref device, 1, RawInputDeviceSize);
        Volatile.Write(ref _rawInputRegistered, ok);
        return ok;
    }

    private void UnregisterRawInput()
    {
        if (!Volatile.Read(ref _rawInputRegistered))
        {
            return;
        }
        Volatile.Write(ref _rawInputRegistered, false);
        var device = new DesktopDoubleClickNativeMethods.RawInputDevice
        {
            UsagePage = DesktopDoubleClickNativeMethods.HidUsagePageGeneric,
            Usage = DesktopDoubleClickNativeMethods.HidUsageGenericMouse,
            Flags = DesktopDoubleClickNativeMethods.RidevRemove,
            Target = 0
        };
        _ = DesktopDoubleClickNativeMethods.RegisterRawInputDevices(ref device, 1, RawInputDeviceSize);
    }

    private nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (message == DesktopDoubleClickNativeMethods.WmInput)
            {
                HandleRawInput(lParam);
                return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
            }

            if (message == DesktopDoubleClickNativeMethods.WmClose)
            {
                UnregisterRawInput();
                _ = NativeMethods.DestroyWindow(hwnd);
                return 0;
            }

            if (message == DesktopDoubleClickNativeMethods.WmNcDestroy)
            {
                UnregisterRawInput();
                Interlocked.Exchange(ref _hwnd, 0);
                DesktopDoubleClickNativeMethods.PostQuitMessage(0);
                return 0;
            }
        }
        catch (Exception ex)
        {
            StartupLog.WriteCrash("DesktopDoubleClickWatcher.WndProc", ex);
        }

        return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void HandleRawInput(nint rawInputHandle)
    {
        var size = RawInputMouseSize;
        if (DesktopDoubleClickNativeMethods.GetRawInputData(
                rawInputHandle,
                DesktopDoubleClickNativeMethods.RidInput,
                out var raw,
                ref size,
                RawInputHeaderSize) == uint.MaxValue)
        {
            return;
        }

        // First-line fast-path filter: drop all mouse moves, wheel events, button-ups and
        // non-primary buttons immediately with zero allocations and zero extra Win32 calls.
        if (raw.Header.Type != DesktopDoubleClickNativeMethods.RimTypeMouse
            || (raw.Mouse.ButtonFlags & DesktopDoubleClickNativeMethods.RiMouseLeftButtonDown) == 0)
        {
            return;
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (!DockNativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var now = Environment.TickCount64;
        var maxInterval = unchecked((int)DesktopDoubleClickNativeMethods.GetDoubleClickTime());
        if (maxInterval <= 0)
        {
            maxInterval = DesktopDoubleClickRules.DefaultDoubleClickTimeMs;
        }

        var cx = TaskbarNativeMethods.GetSystemMetrics(DesktopDoubleClickNativeMethods.SmCxDoubleClk);
        var cy = TaskbarNativeMethods.GetSystemMetrics(DesktopDoubleClickNativeMethods.SmCyDoubleClk);
        if (cx < 0)
        {
            cx = DesktopDoubleClickRules.DefaultDoubleClickWidth;
        }
        if (cy < 0)
        {
            cy = DesktopDoubleClickRules.DefaultDoubleClickHeight;
        }

        var decision = DesktopDoubleClickRules.EvaluateLeftButtonDown(
            _pendingClick,
            cursor.X,
            cursor.Y,
            now,
            maxInterval,
            cx,
            cy);
        _pendingClick = decision.NextPendingClick;

        if (!decision.IsDoubleClick)
        {
            return;
        }

        var point = cursor;
        _ = _dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                DoubleClickDetected?.Invoke(point.X, point.Y);
            }
        });
    }
}
