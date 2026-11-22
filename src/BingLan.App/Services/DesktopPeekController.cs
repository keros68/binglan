using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.App.Windows;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace BingLan.App.Services;

/// <summary>
/// Temporarily raises visible desktop file boxes above foreground applications with a subtle
/// dim backdrop so users can grab or drag files directly into active apps without returning
/// to the desktop. Dragging a file out immediately hides the backdrop so drop targets underneath
/// stay unobstructed, and boxes return cleanly to the desktop layer once done.
/// </summary>
internal sealed class DesktopPeekController : IDisposable
{
    private static readonly uint CurrentProcessId = (uint)Environment.ProcessId;
    private readonly Action<IReadOnlyList<FileBoxWindow>> _returnBoxes;
    private readonly DispatcherTimer _foregroundWatchTimer;
    private readonly List<FileBoxWindow> _peekedBoxes = [];
    private PeekBackdropWindow? _backdrop;
    private nint _previousForeground;
    private bool _draggingOut;
    private bool _disposed;

    internal DesktopPeekController(Action<IReadOnlyList<FileBoxWindow>> returnBoxes)
    {
        _returnBoxes = returnBoxes;
        _foregroundWatchTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(200),
            DispatcherPriority.Background,
            OnForegroundWatchTick,
            Dispatcher.CurrentDispatcher);
        _foregroundWatchTimer.Stop();
    }

    internal bool IsActive => _backdrop is not null;

    internal bool Toggle(IReadOnlyList<FileBoxWindow> candidates, bool animate)
    {
        if (_disposed)
        {
            return false;
        }
        if (IsActive)
        {
            End(restoreForeground: true);
            return true;
        }

        var visibleBoxes = candidates.Where(box => box.IsVisible).ToList();
        if (visibleBoxes.Count == 0)
        {
            return false;
        }

        var foreground = DockNativeMethods.GetForegroundWindow();
        var monitors = MonitorCatalog.GetAll();
        var monitor = DockNativeMethods.GetCursorPos(out var cursor)
            ? monitors.FirstOrDefault(screen => cursor.X >= screen.Bounds.Left && cursor.X < screen.Bounds.Right
                && cursor.Y >= screen.Bounds.Top && cursor.Y < screen.Bounds.Bottom)
            : null;
        monitor ??= monitors.FirstOrDefault();
        if (monitor is not null && FullScreenWatcher.IsFullScreen(foreground, monitor.MonitorHandle, excludeMaximized: true))
        {
            return false;
        }

        _previousForeground = foreground;
        _draggingOut = false;

        var backdrop = new PeekBackdropWindow(animate);
        backdrop.DismissRequested += () => End(restoreForeground: true);
        backdrop.BackdropReady += EnsureBoxesAboveBackdrop;
        _backdrop = backdrop;
        backdrop.Show();

        _peekedBoxes.Clear();
        foreach (var box in visibleBoxes)
        {
            HookBox(box);
            _peekedBoxes.Add(box);
        }

        EnsureBoxesAboveBackdrop();

        // Focus the first peeked box so Escape and keyboard shortcuts reach it immediately
        // while keeping the dim backdrop non-activated underneath every file box.
        if (_peekedBoxes[0].WindowHandle is var firstHandle && firstHandle != 0)
        {
            DockNativeMethods.SetForegroundWindow(firstHandle);
        }

        _foregroundWatchTimer.Start();
        return true;
    }

    private void EnsureBoxesAboveBackdrop()
    {
        if (!IsActive || _peekedBoxes.Count == 0)
        {
            return;
        }
        nint lastBoxHandle = 0;
        nint insertAfter = DockNativeMethods.HwndTopmost;
        foreach (var box in _peekedBoxes)
        {
            box.RaiseAboveDesktop(insertAfter, topmost: true);
            if (box.WindowHandle is var handle && handle != 0)
            {
                insertAfter = handle;
                lastBoxHandle = handle;
            }
        }
        if (lastBoxHandle != 0)
        {
            _backdrop?.PlaceDirectlyBelow(lastBoxHandle);
        }
    }

    internal void End(bool restoreForeground)
    {
        if (!IsActive && _peekedBoxes.Count == 0)
        {
            return;
        }

        _foregroundWatchTimer.Stop();
        _draggingOut = false;

        var backdrop = _backdrop;
        _backdrop = null;
        if (backdrop is not null)
        {
            backdrop.DismissNow();
        }

        var boxes = _peekedBoxes.ToArray();
        _peekedBoxes.Clear();
        foreach (var box in boxes)
        {
            UnhookBox(box);
        }

        if (boxes.Length > 0)
        {
            _returnBoxes(boxes);
        }

        var previous = _previousForeground;
        _previousForeground = 0;
        if (restoreForeground && previous != 0 && DockNativeMethods.IsWindow(previous))
        {
            DockNativeMethods.SetForegroundWindow(previous);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        End(restoreForeground: false);
    }

    private void HookBox(FileBoxWindow box)
    {
        box.DragOutStarted += OnBoxDragOutStarted;
        box.DragOutEnded += OnBoxDragOutEnded;
        box.ItemOpened += OnBoxItemOpened;
        box.PeekEscapeRequested += OnBoxEscapeRequested;
        box.Closed += OnBoxClosed;
    }

    private void UnhookBox(FileBoxWindow box)
    {
        box.DragOutStarted -= OnBoxDragOutStarted;
        box.DragOutEnded -= OnBoxDragOutEnded;
        box.ItemOpened -= OnBoxItemOpened;
        box.PeekEscapeRequested -= OnBoxEscapeRequested;
        box.Closed -= OnBoxClosed;
    }

    private void OnBoxDragOutStarted()
    {
        if (!IsActive)
        {
            return;
        }
        _draggingOut = true;
        // Drop the backdrop immediately so WeChat / browser drop targets underneath stay unobstructed.
        _backdrop?.HideForDrag();
    }

    private void OnBoxDragOutEnded()
    {
        if (!IsActive)
        {
            return;
        }
        _draggingOut = false;
        // Return boxes to desktop layer so any target app confirmation dialog is never covered.
        End(restoreForeground: false);
    }

    private void OnBoxItemOpened()
    {
        if (IsActive)
        {
            End(restoreForeground: false);
        }
    }

    private void OnBoxEscapeRequested()
    {
        if (IsActive)
        {
            End(restoreForeground: true);
        }
    }

    private void OnBoxClosed(object? sender, EventArgs e)
    {
        if (sender is FileBoxWindow box)
        {
            UnhookBox(box);
            _peekedBoxes.Remove(box);
            if (_peekedBoxes.Count == 0 && IsActive)
            {
                End(restoreForeground: false);
            }
        }
    }

    private void OnForegroundWatchTick(object? sender, EventArgs e)
    {
        if (!IsActive || _draggingOut)
        {
            return;
        }
        var foreground = DockNativeMethods.GetForegroundWindow();
        if (foreground == 0)
        {
            return;
        }
        NativeMethods.GetWindowThreadProcessId(foreground, out var pid);
        if (pid != CurrentProcessId && pid != 0)
        {
            // User switched away via Alt+Tab or taskbar; exit Peek quietly.
            End(restoreForeground: false);
        }
    }

    private sealed class PeekBackdropWindow : Window
    {
        private readonly bool _animate;
        private nint _handle;
        private bool _closing;

        internal event Action? DismissRequested;
        internal event Action? BackdropReady;

        internal PeekBackdropWindow(bool animate)
        {
            _animate = animate;
            Title = "冰蓝速览遮罩";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
            var fill = new SolidColorBrush(Color.FromArgb(0x52, 0x09, 0x10, 0x1A));
            fill.Freeze();
            Background = fill;
            Opacity = animate ? 0d : 1d;

            SourceInitialized += (_, _) =>
            {
                _handle = new WindowInteropHelper(this).Handle;
                if (_handle != 0)
                {
                    var style = (long)NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle);
                    NativeMethods.SetWindowLongPtr(
                        _handle,
                        NativeMethods.GwlExStyle,
                        (nint)(style | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate));
                }
            };
            Loaded += (_, _) =>
            {
                BackdropReady?.Invoke();
                if (_animate && !_closing)
                {
                    BeginAnimation(OpacityProperty, new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(110))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    });
                }
            };
            MouseDown += (_, e) =>
            {
                e.Handled = true;
                DismissRequested?.Invoke();
            };
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    DismissRequested?.Invoke();
                }
            };
        }

        internal void PlaceDirectlyBelow(nint anchorWindow)
        {
            if (_closing || _handle == 0 || anchorWindow == 0 || anchorWindow == _handle)
            {
                return;
            }
            NativeMethods.SetWindowPos(
                _handle,
                anchorWindow,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
        }

        internal void HideForDrag()
        {
            if (_closing)
            {
                return;
            }
            BeginAnimation(OpacityProperty, null);
            Opacity = 0d;
            Visibility = Visibility.Hidden;
        }

        internal void DismissNow()
        {
            if (_closing)
            {
                return;
            }
            _closing = true;
            BeginAnimation(OpacityProperty, null);
            Close();
        }
    }
}
