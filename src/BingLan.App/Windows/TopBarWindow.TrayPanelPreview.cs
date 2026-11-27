#if TRAY_PANEL_PREVIEW
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.Core.Models;
using BingLan.TrayPanelSpike;
#endif

namespace BingLan.App.Windows;

public partial class TopBarWindow
{
    partial void CloseTrayPanelPreview();
    partial void CheckTrayPanelPreviewDismissal();

#if TRAY_PANEL_PREVIEW
    private const double TrayPanelGap = 4;
    private const double TrayPanelScreenMargin = 8;

    private static bool _trayPanelWarmupAllowed;
    private PanelWindow? _trayPanelPreview;
    private bool HasOpenTrayPanelPreview => _trayPanelPreview is { IsVisible: true };

    internal static void EnableTrayPanelWarmup()
    {
        _trayPanelWarmupAllowed = true;
        if (Application.Current?.Windows.OfType<TopBarWindow>().Any(window => !window._closing
                && window._modules.TryGetValue(TopBarModuleKind.TrayOverflow, out var module) && module.IsLoaded) == true)
        {
            _ = TrayReader.WarmSnapshotAsync();
        }
    }

    private static bool IsTrayPanelPreviewWindow(nint handle) =>
        HwndSource.FromHwnd(handle)?.RootVisual is PanelWindow { Owner: TopBarWindow };

    private void AddTrayPanelPreviewModule(StackPanel modules)
    {
        var wasOpenOnPress = false;
        AddModule(TopBarModuleKind.TrayOverflow, modules, "托盘", () =>
        {
            var wasOpen = wasOpenOnPress;
            wasOpenOnPress = false;
            if (_trayPanelPreview is not null)
            {
                CloseTrayPanelPreview();
            }
            else if (!wasOpen && _modules.TryGetValue(TopBarModuleKind.TrayOverflow, out var element)
                && element is Button anchor)
            {
                OpenTrayPanelPreview(anchor);
            }
        }, "\uE70E");
        if (ModuleText(TopBarModuleKind.TrayOverflow) is { } trayText)
        {
            ApplyIconTextModule(TopBarModuleKind.TrayOverflow, trayText,
                TopBarRules.EffectiveTrayIconOnly(_state) ? string.Empty : "托盘", "打开托盘");
        }
        if (_modules.TryGetValue(TopBarModuleKind.TrayOverflow, out var trayButton))
        {
            trayButton.Loaded += static (_, _) =>
            {
                if (_trayPanelWarmupAllowed) _ = TrayReader.WarmSnapshotAsync();
            };
            trayButton.PreviewMouseLeftButtonDown += (_, _) => wasOpenOnPress = HasOpenTrayPanelPreview;
            trayButton.MouseLeave += (_, _) => wasOpenOnPress = false;
        }
    }

    private void OpenTrayPanelPreview(Button anchor)
    {
        if (_closing || !IsVisible || !anchor.IsLoaded || (_appBar?.Monitor ?? _monitor) is not { } monitor)
        {
            return;
        }

        CloseFlyoutsBeforeOpening();
        var highContrast = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast;
        var look = ResolveSystemFlyoutLook(highContrast);
        var palette = ResolveTopBarPopupPalette();
        var panel = new PanelWindow(null, anchored: true)
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
        var desiredTop = anchorEnd.Y + (TrayPanelGap - shadowMargin.Top) * scale.DpiScaleY;
        var top = Math.Max(workArea.Top, Math.Round(desiredTop));
        panel.MaxWidth = Math.Max(1, (workArea.Width - 2 * marginX) / scale.DpiScaleX);
        panel.MaxHeight = Math.Max(1, (workArea.Bottom - top - marginY) / scale.DpiScaleY);
        var alignRight = ReferenceEquals(anchor.Parent, RightModules);

        void Place()
        {
            var handle = new WindowInteropHelper(panel).Handle;
            if (handle == 0 || !ReferenceEquals(_trayPanelPreview, panel)) return;

            var width = (panel.ActualWidth > 0 ? panel.ActualWidth : Math.Min(panel.Width, panel.MaxWidth))
                * scale.DpiScaleX;
            var minLeft = workArea.Left + marginX;
            var maxLeft = Math.Max(minLeft, workArea.Right - marginX - width);
            var desiredLeft = alignRight
                ? anchorEnd.X + shadowMargin.Right * scale.DpiScaleX - width
                : anchorOrigin.X - shadowMargin.Left * scale.DpiScaleX;
            var left = (int)Math.Round(Math.Clamp(desiredLeft, minLeft, maxLeft));
            if (!DockNativeMethods.GetWindowRect(handle, out var bounds) || bounds.Left != left || bounds.Top != (int)top)
            {
                NativeMethods.SetWindowPos(handle, 0, left, (int)top, 0, 0,
                    NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
            }
        }

        panel.SourceInitialized += (_, _) =>
        {
            // The transparent shadow margin may overlap the bar. Mark the panel as a tool
            // window so the monitor scan treats it as a flyout while it remains activatable.
            var handle = new WindowInteropHelper(panel).Handle;
            var style = NativeMethods.GetExStyleOf(handle);
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, (nint)(style | NativeMethods.WsExToolWindow));
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
                if (ReferenceEquals(_trayPanelPreview, panel) && !panel.IsActive
                    && !pressingAnchor && !(anchor.IsPressed && IsCursorOverTrayPanelAnchor(anchor)))
                {
                    CloseTrayPanelPreview();
                }
            });
        };
        panel.Closed += (_, _) =>
        {
            if (ReferenceEquals(_trayPanelPreview, panel))
            {
                _trayPanelPreview = null;
            }
            UpdateForegroundTimerInterval();
        };
        _trayPanelPreview = panel;
        TopBarNativeMethods.ConsumeMouseButtonDown(TopBarNativeMethods.LeftMouseButton);
        TopBarNativeMethods.ConsumeMouseButtonDown(TopBarNativeMethods.RightMouseButton);
        TopBarNativeMethods.ConsumeMouseButtonDown(TopBarNativeMethods.EscapeKey);
        panel.Show();
        UpdateForegroundTimerInterval();
    }

    partial void CloseTrayPanelPreview()
    {
        var panel = _trayPanelPreview;
        _trayPanelPreview = null;
        panel?.Close();
    }

    partial void CheckTrayPanelPreviewDismissal()
    {
        if (_trayPanelPreview is not { IsVisible: true } panel) return;
        if (TopBarNativeMethods.IsMouseButtonDown(TopBarNativeMethods.EscapeKey))
        {
            CloseTrayPanelPreview();
            return;
        }
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
        CloseTrayPanelPreview();
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
#else
    private static bool HasOpenTrayPanelPreview => false;
    private static bool IsTrayPanelPreviewWindow(nint handle) => false;
#endif
}
