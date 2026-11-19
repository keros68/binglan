using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.Core.Models;

namespace BingLan.App.Windows;

/// <summary>A movable host for the same search controls and session as the top bar.</summary>
public partial class SearchWindow : Window
{
    private readonly TopBarState _state;
    private readonly Action _save;
    private int _pressX;
    private int _pressY;
    private int _startLeft;
    private int _startTop;
    private double _startWidth;
    private bool _allowClose;
    private bool _layoutChanging;
    private bool _isChangingVisibilityByApp;
    private bool _raisedAboveDesktop;
    private bool _sessionActive;
    private HwndSource? _source;

    internal SearchWindow(TopBarState state, Border search, Action save)
    {
        _state = state;
        _save = save;
        InitializeComponent();
        Width = state.SearchWindowWidth;
        Left = state.SearchWindowPlacement.Left;
        Top = state.SearchWindowPlacement.Top;
        AttachSearchContent(search);
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(WindowProc);
            var style = (long)NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle);
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle,
                (nint)(style | NativeMethods.WsExToolWindow | DockNativeMethods.WsExNoActivate));
            if (!_raisedAboveDesktop && !_sessionActive)
            {
                SendToBackCore();
            }
        };
        Loaded += (_, _) => RecoverPlacement();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(RecoverPlacement);
        MoveHandle.DragStarted += BeginLayoutChange;
        WidthHandle.DragStarted += BeginLayoutChange;
        MoveHandle.DragDelta += (_, _) => MoveFromPointer();
        WidthHandle.DragDelta += (_, _) => ResizeFromPointer();
        MoveHandle.DragCompleted += EndLayoutChange;
        WidthHandle.DragCompleted += EndLayoutChange;
        Closing += (_, args) =>
        {
            if (!_allowClose)
            {
                args.Cancel = true;
                // Leave the close/input message before changing foreground or hiding
                // the focused text store, as with the shared search session teardown.
                Dispatcher.BeginInvoke(() =>
                {
                    if (_allowClose) return;
                    Dismissed?.Invoke();
                    HideFromApp();
                }, System.Windows.Threading.DispatcherPriority.Input);
            }
        };
    }

    internal event Action? Dismissed;
    internal event Action? ShellRecovered;

    internal nint WindowHandle => _source is { IsDisposed: false } source ? source.Handle : 0;

    internal void HideFromApp()
    {
        _isChangingVisibilityByApp = true;
        try
        {
            Hide();
        }
        finally
        {
            _isChangingVisibilityByApp = false;
        }
    }

    internal void SetSessionActive(bool active, bool desktopShown)
    {
        _sessionActive = active;
        if (active)
        {
            if (_source is { IsDisposed: false } source && !_raisedAboveDesktop)
            {
                NativeMethods.SetWindowPos(
                    source.Handle,
                    0,
                    0,
                    0,
                    0,
                    0,
                    NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
            }
            return;
        }

        if (desktopShown)
        {
            RaiseAboveDesktop(DockNativeMethods.HwndTopmost);
        }
        else
        {
            ReturnBelowOrdinaryWindows();
        }
    }

    internal void RaiseAboveDesktop(nint insertAfter)
    {
        _raisedAboveDesktop = true;
        if (_source is not { IsDisposed: false } source)
        {
            return;
        }
        if (NativeMethods.IsIconic(source.Handle) || !NativeMethods.IsWindowVisible(source.Handle))
        {
            NativeMethods.ShowWindow(source.Handle, NativeMethods.SwShowNoActivate);
        }
        const uint flags = NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate;
        if ((NativeMethods.GetExStyleOf(source.Handle) & NativeMethods.WsExTopmost) == 0
            || insertAfter == 0
            || insertAfter == DockNativeMethods.HwndTopmost)
        {
            NativeMethods.SetWindowPos(source.Handle, DockNativeMethods.HwndTopmost, 0, 0, 0, 0, flags);
        }
        if (insertAfter != 0 && insertAfter != DockNativeMethods.HwndTopmost && insertAfter != source.Handle)
        {
            NativeMethods.SetWindowPos(source.Handle, insertAfter, 0, 0, 0, 0, flags);
        }
    }

    internal void ReturnBelowOrdinaryWindows()
    {
        _raisedAboveDesktop = false;
        if (!_sessionActive)
        {
            SendToBackCore();
        }
    }

    private void SendToBackCore()
    {
        if (_source is { IsDisposed: false } source)
        {
            NativeMethods.SetWindowPos(
                source.Handle,
                DockNativeMethods.HwndBottom,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
        }
    }

    private nint WindowProc(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmWindowPosChanging)
        {
            KeepOnDesktopLayer(lParam);
        }

        if (!_allowClose && !_isChangingVisibilityByApp)
        {
            if (message == NativeMethods.WmSize && wParam == NativeMethods.SizeMinimized)
            {
                RecoverFromShell(minimized: true);
            }
            else if (message == NativeMethods.WmShowWindow && wParam == 0)
            {
                RecoverFromShell(minimized: false);
            }
            else if (message == NativeMethods.WmWindowPosChanged
                     && (Marshal.PtrToStructure<NativeMethods.WindowPos>(lParam).Flags
                         & NativeMethods.SwpHideWindow) != 0)
            {
                RecoverFromShell(minimized: false);
            }
        }
        return 0;
    }

    private void KeepOnDesktopLayer(nint lParam)
    {
        if (_raisedAboveDesktop || _sessionActive)
        {
            return;
        }

        var position = Marshal.PtrToStructure<NativeMethods.WindowPos>(lParam);
        if ((position.Flags & NativeMethods.SwpNoZOrder) == 0
            && position.InsertAfter != DockNativeMethods.HwndBottom)
        {
            position.InsertAfter = DockNativeMethods.HwndBottom;
            Marshal.StructureToPtr(position, lParam, false);
        }
    }

    private void RecoverFromShell(bool minimized)
    {
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Send,
            new Action(() =>
            {
                if (_allowClose || _source is not { IsDisposed: false } source || !IsVisible)
                {
                    return;
                }
                var handle = source.Handle;
                if (minimized ? !NativeMethods.IsIconic(handle) : NativeMethods.IsWindowVisible(handle))
                {
                    return;
                }
                NativeMethods.ShowWindow(handle, NativeMethods.SwShowNoActivate);
                if (!_raisedAboveDesktop && !_sessionActive)
                {
                    SendToBackCore();
                }
                ShellRecovered?.Invoke();
            }));
    }

    internal void DetachSearchContent() => SearchContent.Content = null;

    internal void AttachSearchContent(Border search)
    {
        search.Width = double.NaN;
        search.Height = 30;
        SearchContent.Content = search;
    }

    internal void ApplyAppearance(DockGlass glass)
    {
        var surface = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(glass.Surface));
        var border = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(glass.Border));
        var ink = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(glass.RunningDot));
        surface.Freeze(); border.Freeze(); ink.Freeze();
        SearchFrame.Background = surface;
        SearchFrame.BorderBrush = border;
        SearchFrame.CornerRadius = new CornerRadius(_state.SearchCornerRadius);
        foreach (var handle in new[] { MoveHandle, WidthHandle })
        {
            handle.ApplyTemplate();
            if (System.Windows.Media.VisualTreeHelper.GetChildrenCount(handle) > 0
                && System.Windows.Media.VisualTreeHelper.GetChild(handle, 0) is Border { Child: TextBlock text }) text.Foreground = ink;
        }
    }

    internal void RecoverPlacement()
    {
        WindowScreenRecovery.RestoreLayout(this, _state.SearchWindowPlacement);
        WindowScreenRecovery.EnsureOnScreen(this);
        WindowScreenRecovery.KeepOutOfReservedEdges(this);
    }

    internal void Shutdown()
    {
        _allowClose = true;
        Close();
    }

    private void BeginLayoutChange(object sender, DragStartedEventArgs args)
    {
        _layoutChanging = false;
        if (!DockNativeMethods.GetCursorPos(out var cursor)
            || !DockNativeMethods.GetWindowRect(new WindowInteropHelper(this).Handle, out var bounds))
        {
            return;
        }
        _pressX = cursor.X;
        _pressY = cursor.Y;
        _startLeft = bounds.Left;
        _startTop = bounds.Top;
        _startWidth = Width;
        _layoutChanging = true;
    }

    private void MoveFromPointer()
    {
        if (_layoutChanging && DockNativeMethods.GetCursorPos(out var cursor))
        {
            NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, 0,
                _startLeft + cursor.X - _pressX, _startTop + cursor.Y - _pressY, 0, 0,
                NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        }
    }

    private void ResizeFromPointer()
    {
        if (_layoutChanging && DockNativeMethods.GetCursorPos(out var cursor))
        {
            var scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
            Width = Math.Clamp(_startWidth + (cursor.X - _pressX) / scale, MinWidth, MaxWidth);
        }
    }

    private void EndLayoutChange(object sender, DragCompletedEventArgs args)
    {
        if (!_layoutChanging)
        {
            return;
        }
        _layoutChanging = false;
        WindowScreenRecovery.EnsureOnScreen(this);
        WindowScreenRecovery.KeepOutOfReservedEdges(this);
        _state.SearchWindowWidth = Width;
        var placement = _state.SearchWindowPlacement;
        placement.Left = Left;
        placement.Top = Top;
        placement.Width = Width;
        placement.Height = Height;
        WindowScreenRecovery.RememberLayout(this, placement);
        _save();
    }
}
