using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.Core.Models;
using BingLan.App.Taskbar;
using BingLan.App.Tray;
using BingLan.Core.Dock;

namespace BingLan.App.Windows;

public partial class TopBarWindow
{
    private const double TrayPanelGap = 4;
    private const double TrayPanelScreenMargin = 8;
    private const double MinimumTrayPanelHeight = 110;

    private static bool CanShowTrayPanel =>
        TrayOverflowController.IsSupported && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private static bool _trayPanelWarmupAllowed;
    private TrayPanelWindow? _trayPanel;
    private nint _trayPreviousForeground;
    private bool HasOpenTrayPanel => _trayPanel is { IsVisible: true };

    internal static void EnableTrayPanelWarmup()
    {
        if (!CanShowTrayPanel)
        {
            return;
        }

        _trayPanelWarmupAllowed = true;
        if (Application.Current?.Windows.OfType<TopBarWindow>().Any(window => !window._closing
                && window._modules.TryGetValue(TopBarModuleKind.TrayOverflow, out var module) && module.IsLoaded) == true)
        {
            _ = TrayReader.WarmSnapshotAsync();
        }
    }

    private static bool IsTrayPanelWindow(nint handle) =>
        HwndSource.FromHwnd(handle)?.RootVisual is TrayPanelWindow { Owner: TopBarWindow };

    internal nint ActiveMonitorHandle => (_appBar?.Monitor ?? _monitor)?.MonitorHandle ?? 0;

    internal bool TryOpenTrayForApplication()
    {
        if (!CanShowTrayPanel || _closing || !IsVisible
            || !_modules.TryGetValue(TopBarModuleKind.TrayOverflow, out var element)
            || element is not Button { IsLoaded: true, IsVisible: true } anchor)
        {
            return false;
        }
        if (!HasOpenTrayPanel)
        {
            OpenTrayPanel(anchor);
        }
        return HasOpenTrayPanel;
    }

    internal nint ResolveTrayPanelForeground(nint current)
    {
        if (_trayPanel is not { } panel || current == 0 || current != new WindowInteropHelper(panel).Handle)
            return current;
        return DockForegroundRules.EffectiveForeground(current, current,
            DockNativeMethods.IsWindow(_trayPreviousForeground) ? _trayPreviousForeground : 0);
    }

    private void AddTrayPanelModule(StackPanel modules)
    {
        var wasOpenOnPress = false;
        AddModule(TopBarModuleKind.TrayOverflow, modules, "托盘", () =>
        {
            var wasOpen = wasOpenOnPress;
            wasOpenOnPress = false;
            if (!CanShowTrayPanel)
            {
                if (!wasOpen || TrayOverflowController.IsVisible)
                {
                    OpenNativeTrayOverflow();
                }
                return;
            }

            if (_trayPanel is not null)
            {
                CloseTrayPanel();
            }
            else if (!wasOpen && _modules.TryGetValue(TopBarModuleKind.TrayOverflow, out var element)
                && element is Button anchor)
            {
                OpenTrayPanel(anchor);
            }
        }, "\uE70E");
        if (ModuleText(TopBarModuleKind.TrayOverflow) is { } trayText)
        {
            ApplyIconTextModule(TopBarModuleKind.TrayOverflow, trayText,
                TopBarRules.EffectiveTrayIconOnly(_state) ? string.Empty : "托盘",
                CanShowTrayPanel ? "打开托盘" : "打开 Windows 托盘");
        }
        if (_modules.TryGetValue(TopBarModuleKind.TrayOverflow, out var trayButton))
        {
            trayButton.Loaded += static (_, _) =>
            {
                if (_trayPanelWarmupAllowed) _ = TrayReader.WarmSnapshotAsync();
            };
            trayButton.PreviewMouseLeftButtonDown += (_, _) =>
                wasOpenOnPress = CanShowTrayPanel ? HasOpenTrayPanel : TrayOverflowController.IsVisible;
            trayButton.MouseLeave += (_, _) => wasOpenOnPress = false;
        }
    }

    private void OpenNativeTrayOverflow()
    {
        CloseTrayPanel();
        _environment.OpenTrayOverflow();
    }

    private void OpenTrayPanel(Button anchor)
    {
        if (_closing || !IsVisible || !anchor.IsLoaded || (_appBar?.Monitor ?? _monitor) is not { } monitor)
        {
            return;
        }

        var previous = DockNativeMethods.GetForegroundWindow();
        if (previous != 0 && HwndSource.FromHwnd(previous)?.RootVisual is TrayPanelWindow { Owner: TopBarWindow owner })
            previous = owner.ResolveTrayPanelForeground(previous);
        CloseFlyoutsBeforeOpening();
        var highContrast = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast;
        var look = ResolveSystemFlyoutLook(highContrast);
        var palette = ResolveTopBarPopupPalette();
        var panel = new TrayPanelWindow(anchored: true, activateApp: TrayPanelWindow.ActivateTrayAppAsync)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        if (!highContrast)
        {
            panel.Resources["Surface"] = FrozenBrush(look.Palette.Background);
            panel.Resources["Edge"] = FrozenBrush(look.Palette.Border);
            panel.Resources["Ink"] = FrozenBrush(look.Palette.Foreground);
            panel.Resources["SoftInk"] = FrozenBrush(palette.MutedForeground);
            panel.Resources["Hover"] = FrozenBrush(look.Palette.HoverRowBackground);
            panel.Resources["FocusRing"] = FrozenBrush(look.AccentHex);
        }
        else
        {
            panel.Resources["Surface"] = SystemColors.WindowBrush;
            panel.Resources["Edge"] = panel.Resources["Ink"] = panel.Resources["SoftInk"] = SystemColors.WindowTextBrush;
            panel.Resources["Hover"] = SystemColors.ControlBrush;
            panel.Resources["FocusRing"] = SystemColors.HighlightBrush;
        }

        var scale = VisualTreeHelper.GetDpi(anchor);
        var workArea = DockAppBarController.GetLiveWorkingArea(monitor);
        if (!workArea.HasArea) workArea = monitor.Bounds;
        var shadowMargin = panel.SurfaceCard.Margin;
        var marginX = TrayPanelScreenMargin * scale.DpiScaleX;
        var marginY = TrayPanelScreenMargin * scale.DpiScaleY;
        var anchorOrigin = anchor.PointToScreen(new Point());
        var anchorEnd = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        var opensUpward = _state.ShowAtBottom;
        var topLimit = workArea.Top + marginY;
        var bottomLimit = Math.Max(topLimit, workArea.Bottom - marginY);
        double NearEdge(bool upward) => Math.Clamp(upward
            ? anchorOrigin.Y + (shadowMargin.Bottom - TrayPanelGap) * scale.DpiScaleY
            : anchorEnd.Y + (TrayPanelGap - shadowMargin.Top) * scale.DpiScaleY, topLimit, bottomLimit);
        var nearEdge = NearEdge(opensUpward);
        var availableHeight = opensUpward ? nearEdge - topLimit : bottomLimit - nearEdge;
        if (availableHeight < MinimumTrayPanelHeight * scale.DpiScaleY)
        {
            var otherEdge = NearEdge(!opensUpward);
            var otherHeight = opensUpward ? bottomLimit - otherEdge : otherEdge - topLimit;
            if (otherHeight > availableHeight)
            {
                opensUpward = !opensUpward;
                nearEdge = otherEdge;
                availableHeight = otherHeight;
            }
            if (availableHeight < MinimumTrayPanelHeight * scale.DpiScaleY)
            {
                nearEdge = opensUpward ? bottomLimit : topLimit;
                availableHeight = bottomLimit - topLimit;
            }
        }

        panel.MaxWidth = Math.Max(1, (workArea.Width - 2 * marginX) / scale.DpiScaleX);
        panel.MaxHeight = Math.Max(1, availableHeight / scale.DpiScaleY);
        panel.Measure(new Size(panel.MaxWidth, panel.MaxHeight));
        var alignRight = ReferenceEquals(anchor.Parent, RightModules);

        void Place()
        {
            var handle = new WindowInteropHelper(panel).Handle;
            if (handle == 0 || !ReferenceEquals(_trayPanel, panel)) return;

            var width = (panel.ActualWidth > 0 ? panel.ActualWidth : Math.Min(panel.Width, panel.MaxWidth))
                * scale.DpiScaleX;
            var minLeft = workArea.Left + marginX;
            var maxLeft = Math.Max(minLeft, workArea.Right - marginX - width);
            var desiredLeft = alignRight
                ? anchorEnd.X + shadowMargin.Right * scale.DpiScaleX - width
                : anchorOrigin.X - shadowMargin.Left * scale.DpiScaleX;
            var left = (int)Math.Round(Math.Clamp(desiredLeft, minLeft, maxLeft));
            var height = panel.ActualHeight > 0 ? panel.ActualHeight : panel.DesiredSize.Height;
            var top = (int)Math.Round(opensUpward ? Math.Max(topLimit, nearEdge - height * scale.DpiScaleY) : nearEdge);
            if (!DockNativeMethods.GetWindowRect(handle, out var bounds) || bounds.Left != left || bounds.Top != top)
            {
                NativeMethods.SetWindowPos(handle, 0, left, top, 0, 0,
                    NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
            }
        }

        panel.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(panel).Handle;
            var style = NativeMethods.GetExStyleOf(handle) | NativeMethods.WsExToolWindow;
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, (nint)style);
            Place();
        };
        panel.Loaded += (_, _) => Place();
        panel.SizeChanged += (_, _) => Place();
        if (look.UseTextShadow)
        {
            panel.ApplyCaptionShadow(look.Palette.UseDarkInk ? Colors.White : Colors.Black);
        }
        if (look.AllowBackdropBlur)
        {
            _ = new PopupGlassBackdrop(panel, panel.SurfaceCard, look);
        }
        panel.Deactivated += (_, _) =>
        {
            // Activation can change before WPF receives the owner's mouse down.
            // Leave a press over the anchor to its Click toggle.
            var pressingAnchor = IsCursorOverTrayPanelAnchor(anchor)
                && (anchor.IsPressed || TopBarNativeMethods.IsMouseButtonDown(TopBarNativeMethods.LeftMouseButton));
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (ReferenceEquals(_trayPanel, panel) && !panel.IsActive
                    && !pressingAnchor && !(anchor.IsPressed && IsCursorOverTrayPanelAnchor(anchor)))
                {
                    if (!panel.HideForContextMenuHandoff()) CloseTrayPanel();
                }
            });
        };
        panel.Closed += (_, _) =>
        {
            if (ReferenceEquals(_trayPanel, panel))
            {
                _trayPanel = null;
            }
            UpdateForegroundTimerInterval();
        };
        _trayPanel = panel;
        _trayPreviousForeground = previous;
        TopBarNativeMethods.ConsumeMouseButtonDown(TopBarNativeMethods.LeftMouseButton);
        TopBarNativeMethods.ConsumeMouseButtonDown(TopBarNativeMethods.RightMouseButton);
        panel.Show();
        if (DockNativeMethods.GetForegroundWindow() != new WindowInteropHelper(panel).Handle && panel.Activate())
            panel.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
        UpdateForegroundTimerInterval();
    }

    private void CloseTrayPanel()
    {
        var panel = _trayPanel;
        _trayPanel = null;
        panel?.Close();
    }

    private void CheckTrayPanelDismissal()
    {
        if (_trayPanel is not { IsVisible: true } panel) return;
        // The bar and some other desktop surfaces process clicks without taking focus.
        var leftPressed = TopBarNativeMethods.IsMouseButtonDown(TopBarNativeMethods.LeftMouseButton);
        var rightPressed = TopBarNativeMethods.IsMouseButtonDown(TopBarNativeMethods.RightMouseButton);
        if ((!leftPressed && !rightPressed) || !DockNativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }
        if (TrayPanelElementContains(panel.SurfaceCard, cursor)
            || (_modules.TryGetValue(TopBarModuleKind.TrayOverflow, out var anchor) && TrayPanelElementContains(anchor, cursor)))
        {
            return;
        }
        CloseTrayPanel();
    }

    private static bool TrayPanelElementContains(FrameworkElement element, DockNativeMethods.NativePoint cursor)
    {
        if (!element.IsLoaded || PresentationSource.FromVisual(element) is null) return false;
        var start = element.PointToScreen(new Point());
        var end = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return cursor.X >= start.X && cursor.X < end.X && cursor.Y >= start.Y && cursor.Y < end.Y;
    }

    private bool IsCursorOverTrayPanelAnchor(Button anchor) =>
        DockNativeMethods.GetCursorPos(out var cursor)
        && TrayPanelElementContains(anchor, cursor)
        && DesktopDoubleClickNativeMethods.WindowFromPoint(cursor) == _handle;
}
