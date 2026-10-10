using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using Image = System.Windows.Controls.Image;
using MenuItem = System.Windows.Controls.MenuItem;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;

namespace BingLan.App.Windows;

/// <summary>
/// Floating ice-blue dock: pinned and running apps with launch, focus, minimise,
/// pin, reorder and smart hide. It never activates itself, so clicks can still tell
/// which app window was in the foreground.
/// </summary>
public partial class IceBlueDockWindow : Window
{
    private const string PinDragFormat = "BingLan.DockPin";
    private const string RunningDragFormat = "BingLan.DockRunning";
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private static readonly TimeSpan SmartHideCheckInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan FullScreenCheckInterval = TimeSpan.FromMilliseconds(500);
    /// <summary>How much the dock animates; part of the desktop style.</summary>
    internal static MotionLevel Motion { get; set; } = MotionLevel.Standard;
    private static readonly TimeSpan ReservationRetryInterval = TimeSpan.FromSeconds(30);
    private readonly DockState _state;
    private readonly WindowCatalog _catalog = new();
    private readonly WindowEventWatcher _watcher;
    private readonly DispatcherTimer _foregroundTimer;
    private DockAutoHideState _autoHide = new();
    private readonly Dictionary<string, DockAppVisual> _visuals = new(StringComparer.OrdinalIgnoreCase);
    private DockAppBarController? _appBar;
    private IReadOnlyList<DockItem> _items = [];
    private IReadOnlyList<string> _groupOrder = [];
    private string _lastSignature = string.Empty;
    private nint _handle;
    private bool _closing;
    private bool _refreshRunning;
    private bool _refreshQueued;
    private bool _menuOpen;
    private bool _hiddenBySmartHide;
    private bool _refreshPending;
    private bool _reservationFailureReported;
    private bool _vacatedForMaximized;
    private DockHandleWindow? _handleWindow;
    private bool _handleExpanded;
    private PixelRect _expandedRect;
    private bool _trueFullScreenForeground;
    private ContextMenu? _openMenu;
    private DateTimeOffset _lastReservationAttempt;
    private readonly DockAttention _attention = new();
    private uint _shellHookMessage;
    private DateTimeOffset _attentionRevealUntil;
    private static readonly TimeSpan AttentionReveal = TimeSpan.FromSeconds(4);
    private const int HshellWindowDestroyed = 2;
    private const int HshellWindowActivated = 4;
    private const int HshellRudeAppActivated = 0x8004;
    private const int HshellFlash = 0x8006;
    private bool _dragging;
    private Point? _dragStart;
    private DockItem? _dragItem;

    internal IceBlueDockWindow(DockState state)
    {
        _state = state;
        InitializeComponent();
        _watcher = new WindowEventWatcher(Dispatcher);
        _watcher.SnapshotInvalidated += RefreshWindows;
        _foregroundTimer = new DispatcherTimer(
            FullScreenCheckInterval,
            DispatcherPriority.Background,
            (_, _) => UpdateForForeground(),
            Dispatcher);
        _foregroundTimer.Stop();
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        DockSurface.DragOver += OnDragOver;
        DockSurface.Drop += OnDropOnSurface;
    }

    internal event Action? DockStateChanged;

    internal event Action? SettingsRequested;

    internal event Action? HideRequested;

    internal event Action<string>? CommandFailed;

    /// <summary>Re-reads monitor and visibility settings from the dock state.</summary>
    internal void ApplyState()
    {
        if (_appBar is null || _closing)
        {
            return;
        }

        var monitors = MonitorCatalog.GetAll();
        var monitor = monitors.FirstOrDefault(candidate => string.Equals(
                candidate.DeviceName,
                _state.MonitorDeviceName,
                StringComparison.OrdinalIgnoreCase))
            ?? monitors.FirstOrDefault(candidate => candidate.IsPrimary)
            ?? monitors.FirstOrDefault();
        if (monitor is null)
        {
            return;
        }

        // A window maximized on the dock's monitor may take over the reserved strip;
        // while it does, the dock follows smart-hide rules instead of holding the space.
        var reserveWorkArea = _state.VisibilityMode == DockVisibilityMode.ReserveWorkArea
            && !_vacatedForMaximized;
        _lastReservationAttempt = DateTimeOffset.UtcNow;
        _appBar.Apply(
            monitor,
            reserveWorkArea,
            ContentLengthDip(),
            DockLayoutMetrics.Thickness(_state.IconSize),
            _state.BottomGapDip);
        if (!reserveWorkArea || _appBar.IsReserved)
        {
            _reservationFailureReported = false;
        }
        else if (!_reservationFailureReported)
        {
            _reservationFailureReported = true;
            CommandFailed?.Invoke("无法保留屏幕底部空间，Dock 暂时按智能隐藏显示，稍后自动重试。");
        }
        if (!UsesSmartHide)
        {
            _autoHide = new DockAutoHideState();
            if (_handleExpanded)
            {
                CollapseHandleExpansion();
            }
            ShowDock();
        }
        _foregroundTimer.Interval = UsesSmartHide ? SmartHideCheckInterval : FullScreenCheckInterval;
        _foregroundTimer.Start();
        UpdateHandleBounds();
        if (_handleExpanded && _appBar.Monitor is not null
            && _handleWindow is { IsDragActive: false } handle)
        {
            // Items or the monitor changed while expanded; keep the row beside the handle.
            ApplyHandleExpansion(_appBar.Monitor, handle.CurrentRect);
        }
    }

    private double ContentLengthDip() =>
        DockLayoutMetrics.ContentLengthForItems(_items.Count, _state.IconSize)
            + (_items.Any(item => item.IsPinned) && _items.Any(item => !item.IsPinned)
                ? DockLayoutMetrics.DividerDip
                : 0);

    // A reserved dock whose AppBar registration failed would float over windows, so it
    // falls back to smart hide until the reservation succeeds.
    private bool UsesSmartHide =>
        _state.VisibilityMode == DockVisibilityMode.SmartHide || _appBar?.IsReserved != true;

    internal void RefreshWindows()
    {
        if (_closing || _handle == 0)
        {
            return;
        }

        if (_hiddenBySmartHide)
        {
            _refreshPending = true;
            return;
        }

        if (_refreshRunning)
        {
            _refreshQueued = true;
            return;
        }

        _ = RefreshWindowsLoopAsync();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        var style = (long)NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle);
        NativeMethods.SetWindowLongPtr(
            _handle,
            NativeMethods.GwlExStyle,
            (nint)(style | NativeMethods.WsExToolWindow | DockNativeMethods.WsExNoActivate));
        // The dock draws its own rounded outline; a second rounded corner from Windows
        // shows as a faint square corner outside it.
        var cornerPreference = NativeMethods.DwmWindowCornerDoNotRound;
        NativeMethods.DwmSetWindowAttribute(
            _handle,
            NativeMethods.DwmwaWindowCornerPreference,
            ref cornerPreference,
            sizeof(int));
        HwndSource.FromHwnd(_handle)?.AddHook(PreventActivation);
        // Windows tells shell hook windows when an app flashes for attention, so the dock
        // can mark it even while the taskbar stays hidden.
        _shellHookMessage = DockNativeMethods.RegisterWindowMessageW("SHELLHOOK");
        if (DockNativeMethods.RegisterShellHookWindow(_handle))
        {
            HwndSource.FromHwnd(_handle)?.AddHook(OnShellHook);
        }

        _appBar = new DockAppBarController(this);
        _appBar.EnvironmentChanged += ApplyState;
        _appBar.ExplorerRestarted += () =>
        {
            ApplyState();
            RefreshWindows();
        };
        CreateHandleWindow();
        _watcher.Start();
        ApplyState();
        RefreshWindows();
    }

    private static nint PreventActivation(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmMouseActivate)
        {
            handled = true;
            return MaNoActivate;
        }
        return 0;
    }

    private nint OnShellHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_shellHookMessage == 0 || (uint)message != _shellHookMessage)
        {
            return 0;
        }

        var changed = (int)wParam switch
        {
            HshellFlash => _attention.Flashed(lParam),
            HshellWindowActivated or HshellRudeAppActivated or HshellWindowDestroyed => _attention.Cleared(lParam),
            _ => false
        };
        if (changed)
        {
            if ((int)wParam == HshellFlash && UsesSmartHide)
            {
                // A hidden dock shows itself briefly so the mark is seen.
                _attentionRevealUntil = DateTimeOffset.UtcNow + AttentionReveal;
                ShowDock();
            }
            _lastSignature = string.Empty;
            RefreshWindows();
        }
        return 0;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        if (_handle != 0)
        {
            DockNativeMethods.DeregisterShellHookWindow(_handle);
        }
        _foregroundTimer.Stop();
        _watcher.Dispose();
        _appBar?.Dispose();
        _appBar = null;
        _handleWindow?.Close();
        _handleWindow = null;
    }

    private async Task RefreshWindowsLoopAsync()
    {
        _refreshRunning = true;
        try
        {
            do
            {
                _refreshQueued = false;
                var pinned = _state.PinnedApps.ToArray();
                var hidden = _state.HiddenApps.ToArray();
                var snapshot = await Task.Run(_catalog.Capture);
                var groups = WindowGrouping.OrderByFirstSeen(WindowGrouping.Group(snapshot), _groupOrder);
                _groupOrder = groups.Select(group => group.Key).ToArray();
                var items = DockItemComposer.Compose(pinned, groups, hidden);
                _attention.Retain(groups.SelectMany(group => group.Windows).Select(window => window.Handle));
                foreach (var stale in _visuals.Keys.Except(items.Select(item => item.Key)).ToArray())
                {
                    _visuals.Remove(stale);
                }
                var missing = items.Where(item => !_visuals.ContainsKey(item.Key)).ToArray();
                if (missing.Length > 0)
                {
                    var resolved = await Task.Run(() => missing
                        .Select(item => (item.Key, Visual: DockAppResolver.Resolve(item)))
                        .ToArray());
                    foreach (var (key, visual) in resolved)
                    {
                        _visuals[key] = visual;
                    }
                }

                if (_closing)
                {
                    return;
                }
                ApplyItems(items);
            }
            while (_refreshQueued);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Dock 刷新失败：{exception}");
        }
        finally
        {
            _refreshRunning = false;
        }
    }

    private void ApplyItems(IReadOnlyList<DockItem> items)
    {
        var signature = $"{_state.IconSize}|{BuildSignature(items)}|{string.Join(",", items.Select(item => _attention.Wants(item.Group) ? 1 : 0))}";
        if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
        {
            return;
        }

        var countChanged = items.Count != _items.Count
            || items.Count(item => item.IsPinned) != _items.Count(item => item.IsPinned);
        _lastSignature = signature;
        _items = items;
        ItemPanel.Children.Clear();
        for (var index = 0; index < items.Count; index++)
        {
            // Pinned apps come first; a thin line sets the other running apps apart.
            if (index > 0 && items[index - 1].IsPinned && !items[index].IsPinned)
            {
                ItemPanel.Children.Add(BuildDivider());
            }
            ItemPanel.Children.Add(BuildItem(items[index]));
        }

        if (countChanged)
        {
            ApplyState();
        }
    }

    private string DisplayNameOf(DockItem item) =>
        item.IsPinned
            ? item.DisplayName
            : _visuals.TryGetValue(item.Key, out var visual) && visual.DisplayName is { } name
                ? name
                : item.DisplayName;

    private Border BuildDivider()
    {
        var divider = new Border
        {
            Width = 1,
            Height = _state.IconSize * 0.8,
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        divider.SetResourceReference(Border.BackgroundProperty, "DockSurfaceBorderBrush");
        return divider;
    }

    private Button BuildItem(DockItem item)
    {
        var wantsAttention = _attention.Wants(item.Group);
        var content = new StackPanel { Orientation = Orientation.Vertical };
        var icon = new Image
        {
            Width = _state.IconSize,
            Height = _state.IconSize,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Source = _visuals.TryGetValue(item.Key, out var visual) ? visual.Icon : null
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var iconHost = new Grid();
        iconHost.Children.Add(icon);
        if (wantsAttention)
        {
            // An orange badge at the top-right corner, like an unread count.
            iconHost.Children.Add(new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, -2, -2, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Background = AttentionBrush,
                BorderBrush = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(1.5)
            });
        }
        content.Children.Add(iconHost);
        // The dot under a running app: wider for several windows, blue for the current one.
        var dot = new Border
        {
            Width = item.HasMultipleWindows ? 10 : 4,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Visibility = item.IsRunning ? Visibility.Visible : Visibility.Hidden
        };
        if (wantsAttention)
        {
            dot.Background = AttentionBrush;
        }
        else
        {
            dot.SetResourceReference(
                Border.BackgroundProperty,
                item.IsActive ? "DockActiveDotBrush" : "DockRunningDotBrush");
        }
        content.Children.Add(dot);

        var name = DisplayNameOf(item);
        var button = new Button
        {
            Content = content,
            Width = _state.IconSize + 16,
            Height = _state.IconSize + 16,
            Style = (Style)Resources["DockItemButtonStyle"],
            ToolTip = BuildToolTip(item, name),
            Tag = item,
            AllowDrop = true
        };
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetHelpText(button, wantsAttention ? "有新提醒" : item.State switch
        {
            DockItemState.Active => "当前窗口",
            DockItemState.Running => item.HasMultipleWindows ? $"{item.WindowCount} 个窗口" : "正在运行",
            _ => "已固定，未运行"
        });

        button.Click += (_, _) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                LaunchNewInstance(item, name);
            }
            else
            {
                Invoke(item);
            }
        };
        button.MouseUp += (_, eventArgs) =>
        {
            if (eventArgs.ChangedButton == MouseButton.Middle)
            {
                eventArgs.Handled = true;
                LaunchNewInstance(item, name);
            }
        };
        AddHoverZoom(button, icon);
        button.MouseRightButtonUp += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            OpenItemMenu(item, name);
        };
        button.PreviewMouseLeftButtonDown += (_, eventArgs) =>
        {
            // Pinned apps are dragged to reorder; running apps are dragged in to pin them.
            _dragStart = eventArgs.GetPosition(this);
            _dragItem = item;
        };
        button.PreviewMouseMove += OnItemMouseMove;
        button.DragOver += OnDragOver;
        button.Drop += (_, eventArgs) => DropPin(eventArgs, item);
        return button;
    }

    private static readonly SolidColorBrush AttentionBrush = CreateAttentionBrush();

    private static SolidColorBrush CreateAttentionBrush()
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x7A, 0x2F));
        brush.Freeze();
        return brush;
    }

    private void Invoke(DockItem item)
    {
        var result = item.Group is not null
            ? WindowCommandService.Toggle(item.Group)
            : WindowCommandService.Launch(item.Pinned!);
        Report(result);
    }

    private void LaunchNewInstance(DockItem item, string name)
    {
        var pin = item.Pinned ?? DockAppResolver.CreatePin(item, name);
        Report(pin is not null
            ? WindowCommandService.Launch(pin)
            : new WindowCommandResult(false, $"{name} 不支持从 Dock 启动新实例"));
    }

    private static void AddHoverZoom(Button button, Image icon)
    {
        var scale = new ScaleTransform(1, 1);
        icon.RenderTransformOrigin = new Point(0.5, 1);
        icon.RenderTransform = scale;
        button.MouseEnter += (_, _) => Zoom(
            scale,
            DesktopStyleRules.HoverZoom(Motion, SystemParameters.ClientAreaAnimation));
        button.MouseLeave += (_, _) => Zoom(scale, 1);
    }

    private static void Zoom(ScaleTransform scale, double target)
    {
        // Honour the system "show animations" setting and the style's motion level:
        // no zoom at all when either turns it off.
        if (!SystemParameters.ClientAreaAnimation || Motion == MotionLevel.Off)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = 1;
            scale.ScaleY = 1;
            return;
        }

        var animation = new DoubleAnimation(target, new Duration(DesktopStyleRules.HoverZoomDuration(Motion)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void Report(WindowCommandResult result)
    {
        if (!result.Succeeded)
        {
            CommandFailed?.Invoke(result.Message);
        }
    }

    private void OpenItemMenu(DockItem item, string name)
    {
        var menu = CreateMenu();
        if (item.Group is { } group)
        {
            if (item.HasMultipleWindows)
            {
                foreach (var window in group.Windows)
                {
                    menu.Items.Add(MenuItem(
                        string.IsNullOrWhiteSpace(window.Title) ? name : window.Title,
                        () => Report(WindowCommandService.Focus(window))));
                }
                menu.Items.Add(new Separator());
            }

            var launchPin = item.Pinned ?? DockAppResolver.CreatePin(item, name);
            if (launchPin is not null)
            {
                menu.Items.Add(MenuItem("启动新实例", () => Report(WindowCommandService.Launch(launchPin))));
            }
            menu.Items.Add(MenuItem(
                item.HasMultipleWindows ? $"关闭全部 {item.WindowCount} 个窗口" : "关闭窗口",
                () => Report(WindowCommandService.Close(group.Windows))));
        }

        var executablePath = item.Pinned?.ExecutablePath
            ?? item.Group?.Windows
                .Select(window => window.ExecutablePath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (executablePath is not null
            && !DockAppResolver.IsFrameHost(executablePath)
            && !DockAppResolver.IsPackagedPath(executablePath))
        {
            menu.Items.Add(MenuItem(
                "打开文件位置",
                () => Report(WindowCommandService.OpenFileLocation(executablePath))));
        }

        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new Separator());
        }
        if (item.Pinned is { } iconPin && DockAppResolver.IconStore is { } iconStore)
        {
            menu.Items.Add(MenuItem("更换图标…", () => ChangeIcon(item, iconPin, iconStore)));
            if (iconPin.IconFile is not null)
            {
                menu.Items.Add(MenuItem("恢复默认图标", () => SetIcon(item, iconPin, iconStore, null)));
            }
        }
        if (item.Pinned is { } pinned)
        {
            menu.Items.Add(MenuItem("从 Dock 取消固定", () =>
            {
                if (DockPinRules.Unpin(_state, DockPinRules.IdentityKey(pinned)))
                {
                    DockAppResolver.IconStore?.Delete(pinned.IconFile);
                    PinsChanged();
                }
            }));
        }
        else if (DockAppResolver.CreatePin(item, name) is { } pin)
        {
            menu.Items.Add(MenuItem("固定到 Dock", () =>
            {
                if (DockPinRules.Pin(_state, pin))
                {
                    PinsChanged();
                }
            }));
            menu.Items.Add(MenuItem("不在 Dock 中显示此应用", () =>
            {
                if (DockPinRules.Hide(_state, pin))
                {
                    PinsChanged();
                }
            }));
        }
        menu.Items.Add(MenuItem("Dock 设置…", () => SettingsRequested?.Invoke()));
        menu.IsOpen = true;
    }

    private void OnDockSurfaceRightClick(object sender, MouseButtonEventArgs e)
    {
        var menu = CreateMenu();
        menu.Items.Add(MenuItem("添加应用…", PickAppToPin));
        menu.Items.Add(MenuItem("Dock 设置…", () => SettingsRequested?.Invoke()));
        menu.Items.Add(MenuItem("关闭冰蓝 Dock", () => HideRequested?.Invoke()));
        menu.IsOpen = true;
    }

    private ContextMenu CreateMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.Mouse };
        menu.Opened += (_, _) =>
        {
            _menuOpen = true;
            _openMenu = menu;
        };
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (ReferenceEquals(_openMenu, menu))
            {
                _openMenu = null;
            }
        };
        return menu;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start
            || _dragItem is not { } dragged
            || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var offset = e.GetPosition(this) - start;
        if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragStart = null;
        _dragging = true;
        try
        {
            var data = dragged.Pinned is { } pinned
                ? new DataObject(PinDragFormat, DockPinRules.IdentityKey(pinned))
                : new DataObject(RunningDragFormat, dragged.Key);
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
            _dragItem = null;
        }
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(PinDragFormat) || e.Data.GetDataPresent(RunningDragFormat)
            ? DragDropEffects.Move
            : e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
                ? DragDropEffects.Link
                : DragDropEffects.None;
        e.Handled = true;
    }

    private void PickAppToPin()
    {
        AppPickerDialog.Open(null, _state.PinnedApps, PinApps);
    }

    private void PinApps(IEnumerable<DockPinnedApp> apps)
    {
        if (apps.Count(app => DockPinRules.Pin(_state, app)) > 0)
        {
            PinsChanged();
        }
    }

    // Programs and shortcuts dropped on the dock are pinned; anything else is ignored.
    private void PinFiles(IEnumerable<string> paths)
    {
        var pinned = 0;
        foreach (var app in paths.Select(DockShortcuts.CreatePin).OfType<DockPinnedApp>())
        {
            if (DockPinRules.Pin(_state, app))
            {
                pinned++;
            }
        }
        if (pinned > 0)
        {
            PinsChanged();
        }
        else
        {
            CommandFailed?.Invoke("只能固定应用程序（.exe）或指向应用程序的快捷方式");
        }
    }

    private void DropPin(DragEventArgs e, DockItem target)
    {
        if (e.Data.GetData(RunningDragFormat) is string runningKey)
        {
            e.Handled = true;
            var index = target.Pinned is { } at
                ? DockPinRules.IndexOf(_state, DockPinRules.IdentityKey(at))
                : _state.PinnedApps.Count;
            PinRunning(runningKey, index);
            return;
        }

        if (e.Data.GetData(PinDragFormat) is not string key)
        {
            // Files dropped on an icon are pinned like files dropped beside it.
            OnDropOnSurface(this, e);
            return;
        }

        e.Handled = true;
        var targetIndex = target.Pinned is { } pinned
            ? DockPinRules.IndexOf(_state, DockPinRules.IdentityKey(pinned))
            : _state.PinnedApps.Count - 1;
        if (DockPinRules.Move(_state, key, targetIndex))
        {
            PinsChanged();
        }
    }

    private void OnDropOnSurface(object sender, DragEventArgs e)
    {
        if (!e.Handled && e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files)
        {
            e.Handled = true;
            PinFiles(files);
            return;
        }

        if (!e.Handled && e.Data.GetData(RunningDragFormat) is string runningKey)
        {
            e.Handled = true;
            PinRunning(runningKey, _state.PinnedApps.Count);
            return;
        }

        if (!e.Handled && e.Data.GetData(PinDragFormat) is string key
            && DockPinRules.Move(_state, key, _state.PinnedApps.Count - 1))
        {
            PinsChanged();
        }
    }

    // A running app dragged among the pinned apps is pinned where it was dropped.
    private void PinRunning(string key, int index)
    {
        if (_items.FirstOrDefault(item => item.Key == key && !item.IsPinned) is { } item
            && DockAppResolver.CreatePin(item, DisplayNameOf(item)) is { } pin
            && DockPinRules.Pin(_state, pin, Math.Clamp(index, 0, _state.PinnedApps.Count)))
        {
            PinsChanged();
        }
    }

    private void ChangeIcon(DockItem item, DockPinnedApp pinned, DockIconStore iconStore)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"为“{DisplayNameOf(item)}”选择图标",
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.ico;*.bmp)|*.png;*.jpg;*.jpeg;*.ico;*.bmp"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (iconStore.SaveFromFile(dialog.FileName) is { } iconFile)
        {
            SetIcon(item, pinned, iconStore, iconFile);
        }
        else
        {
            CommandFailed?.Invoke("无法读取这张图片，请选择 PNG、JPG、ICO 或 BMP 文件");
        }
    }

    private void SetIcon(DockItem item, DockPinnedApp pinned, DockIconStore iconStore, string? iconFile)
    {
        var previous = pinned.IconFile;
        pinned.IconFile = iconFile;
        if (!string.Equals(previous, iconFile, StringComparison.Ordinal))
        {
            iconStore.Delete(previous);
        }
        _visuals.Remove(item.Key);
        _lastSignature = string.Empty;
        PinsChanged();
    }

    private void PinsChanged()
    {
        DockStateChanged?.Invoke();
        RefreshWindows();
    }

    private void UpdateForForeground()
    {
        if (_appBar?.Monitor is not { } monitor || _closing)
        {
            return;
        }

        var bounds = _appBar.Bounds;
        var foreground = ForegroundWindowRect(out var foregroundBounds, out var foregroundWindow);
        var fullScreen = foreground
            && foregroundBounds.Left <= monitor.Bounds.Left
            && foregroundBounds.Top <= monitor.Bounds.Top
            && foregroundBounds.Right >= monitor.Bounds.Right
            && foregroundBounds.Bottom >= monitor.Bounds.Bottom;
        var foregroundZoomed = foreground && foregroundWindow != 0 && DockNativeMethods.IsZoomed(foregroundWindow);
        // A maximized window filling the monitor (for example while the taskbar auto-hides)
        // is still working space the handle may reveal the dock over.
        var trueFullScreen = DockVacateRules.IsTrueFullScreen(fullScreen, foregroundZoomed);
        _trueFullScreenForeground = trueFullScreen;
        var shouldVacate = DockVacateRules.ShouldReleaseForMaximized(
            _state.VisibilityMode,
            _state.ReleaseWhenMaximized,
            foregroundZoomed
                && DockNativeMethods.MonitorFromWindow(
                    foregroundWindow,
                    DockNativeMethods.MonitorDefaultToNearest) == monitor.MonitorHandle);
        if (shouldVacate != _vacatedForMaximized)
        {
            _vacatedForMaximized = shouldVacate;
            ApplyState();
            if (!UsesSmartHide)
            {
                // The strip is reserved again and ApplyState has shown the dock.
                return;
            }
        }

        if (!UsesSmartHide)
        {
            _appBar.SetFullScreenDetected(fullScreen);
            return;
        }

        if (_state.VisibilityMode == DockVisibilityMode.ReserveWorkArea
            && !_vacatedForMaximized
            && DateTimeOffset.UtcNow - _lastReservationAttempt >= ReservationRetryInterval)
        {
            ApplyState();
            if (!UsesSmartHide)
            {
                return;
            }
        }

        // Nothing can reveal the dock over a truly full-screen app, so check less often.
        _foregroundTimer.Interval = trueFullScreen ? FullScreenCheckInterval : SmartHideCheckInterval;

        DockNativeMethods.GetCursorPos(out var cursor);
        var revealDepth = Math.Max(2, (int)Math.Round(2 * monitor.Dpi / 96d));
        var handleRect = _handleWindow is null ? default : _handleWindow.CurrentRect;
        var pointerOverHandle = _handleWindow is { IsVisible: true, IsDragActive: false } handle
            && cursor.X >= handleRect.Left && cursor.X < handleRect.Right
            && cursor.Y >= handleRect.Top && cursor.Y < handleRect.Bottom;
        var effectiveBounds = _handleExpanded ? _expandedRect : bounds;
        var overlaps = foreground && foregroundBounds.Intersects(effectiveBounds);
        var pointerOverDock = (IsVisible
                && cursor.X >= effectiveBounds.Left && cursor.X < effectiveBounds.Right
                && cursor.Y >= effectiveBounds.Top && cursor.Y < effectiveBounds.Bottom)
            || pointerOverHandle;
        var input = new DockAutoHideInput(
            WindowOverlapsDock: overlaps,
            ForegroundIsFullScreen: trueFullScreen,
            PointerInRevealZone: DockRevealRules.IsInRevealZone(
                cursor.X,
                cursor.Y,
                bounds,
                monitor.Bounds,
                revealDepth),
            PointerOverDock: pointerOverDock,
            InteractionActive: _menuOpen || _dragging || DateTimeOffset.UtcNow < _attentionRevealUntil);
        if (_autoHide.Update(input, DateTimeOffset.UtcNow))
        {
            if (_autoHide.IsShown)
            {
                ShowDock();
            }
            else
            {
                if (_openMenu is { } menu)
                {
                    menu.IsOpen = false;
                }
                _hiddenBySmartHide = true;
                Hide();
            }
        }

        if (_autoHide.IsShown && pointerOverHandle)
        {
            ApplyHandleExpansion(monitor, handleRect);
        }
        else
        {
            // Restore the strip position only once the dock is hidden, or when it stays
            // shown with nothing overlapping and the pointer gone: moving a visible
            // dock back to the bottom mid-hide would read as it flashing there first.
            var pointerLeftDock = !pointerOverHandle
                && !(cursor.X >= _expandedRect.Left && cursor.X < _expandedRect.Right
                    && cursor.Y >= _expandedRect.Top && cursor.Y < _expandedRect.Bottom);
            if (_handleExpanded && (!_autoHide.IsShown || (pointerLeftDock && !overlaps)))
            {
                CollapseHandleExpansion();
            }
        }
        SyncHandle();
    }

    private void ShowDock()
    {
        if (_closing)
        {
            return;
        }

        _hiddenBySmartHide = false;
        if (!IsVisible)
        {
            Show();
        }
        if (_refreshPending)
        {
            _refreshPending = false;
            RefreshWindows();
        }
        SyncHandle();
    }

    // The handle stands in for the dock while it is smart-hidden: it appears then and
    // disappears with the dock — including while nothing may show over a true
    // full-screen app. While the dock is expanded beside it the handle stays visible
    // as the row's end cap, so the pointer has somewhere to rest, and a drag in
    // progress can never lose the window to a visibility change.
    private void SyncHandle()
    {
        if (_handleWindow is null || _closing)
        {
            return;
        }

        _handleWindow.SetVisible(
            !_trueFullScreenForeground
            && (_hiddenBySmartHide || _handleExpanded || _handleWindow.IsDragActive));
    }

    private void CreateHandleWindow()
    {
        _handleWindow = new DockHandleWindow();
        _handleWindow.DragStarted += () =>
        {
            if (_handleExpanded)
            {
                CollapseHandleExpansion();
            }
        };
        _handleWindow.DragEnded += () =>
        {
            if (_appBar?.Monitor is not { } monitor)
            {
                return;
            }

            // The drag may end anywhere; keep the handle inside the work area and
            // remember the spot in monitor-relative DIPs so it survives restarts.
            var workArea = DockAppBarController.GetLiveWorkingArea(monitor);
            var rect = DockHandleGeometry.ClampToWorkArea(_handleWindow.CurrentRect, workArea);
            _handleWindow.MoveTo(rect);
            var scale = monitor.Dpi / 96d;
            _state.HiddenHandleLeftDip = (rect.Left - monitor.Bounds.Left) / scale;
            _state.HiddenHandleTopDip = (rect.Top - monitor.Bounds.Top) / scale;
            DockStateChanged?.Invoke();
        };
        // Create the HWND now so the handle is already positioned when it first shows.
        _ = new WindowInteropHelper(_handleWindow).EnsureHandle();
    }

    private void UpdateHandleBounds()
    {
        if (_handleWindow is null || _appBar?.Monitor is not { } monitor)
        {
            return;
        }

        var scale = monitor.Dpi / 96d;
        var size = DockHandleGeometry.HandleSizePixels(scale);
        var workArea = DockAppBarController.GetLiveWorkingArea(monitor);
        var edgeGapPixels = Math.Max(0, (int)Math.Round(_state.BottomGapDip * scale));
        PixelRect handleRect;
        if (_state.HiddenHandleLeftDip is { } left && _state.HiddenHandleTopDip is { } top)
        {
            handleRect = DockHandleGeometry.ClampToWorkArea(
                new PixelRect(
                    monitor.Bounds.Left + (int)Math.Round(left * scale),
                    monitor.Bounds.Top + (int)Math.Round(top * scale),
                    monitor.Bounds.Left + (int)Math.Round(left * scale) + size,
                    monitor.Bounds.Top + (int)Math.Round(top * scale) + size),
                workArea);
        }
        else
        {
            handleRect = DockHandleGeometry.Default(
                workArea,
                size,
                DockHandleGeometry.HandleMarginPixels(scale),
                edgeGapPixels);
        }

        if (!_handleWindow.IsDragActive)
        {
            _handleWindow.MoveTo(handleRect);
        }
    }

    private void ApplyHandleExpansion(MonitorSnapshot monitor, PixelRect handleRect)
    {
        if (_appBar is null || !handleRect.HasArea)
        {
            return;
        }

        var scale = monitor.Dpi / 96d;
        var workArea = DockAppBarController.GetLiveWorkingArea(monitor);
        var thicknessPixels = Math.Max(1, (int)Math.Round(DockLayoutMetrics.Thickness(_state.IconSize) * scale));
        var contentLengthPixels = Math.Max(1, (int)Math.Round(ContentLengthDip() * scale));
        var expanded = DockHandleGeometry.ExpandedDock(
            handleRect,
            workArea,
            thicknessPixels,
            contentLengthPixels,
            DockHandleGeometry.ExpansionGapPixels(scale));
        if (_handleExpanded && _expandedRect == expanded)
        {
            return;
        }

        _expandedRect = expanded;
        _handleExpanded = true;
        // The override routes every repositioning path through the expanded rect, so
        // AppBar notifications cannot pull the dock back to the strip mid-hover.
        _appBar.SetFloatingOverride(expanded);
    }

    private void CollapseHandleExpansion()
    {
        _handleExpanded = false;
        _expandedRect = default;
        _appBar?.SetFloatingOverride(null);
    }

    private bool ForegroundWindowRect(out PixelRect bounds, out nint window)
    {
        bounds = default;
        window = DockNativeMethods.GetForegroundWindow();
        if (window == 0 || window == _handle || DockNativeMethods.IsIconic(window))
        {
            return false;
        }

        var className = new StringBuilder(64);
        DockNativeMethods.GetClassNameW(window, className, className.Capacity);
        if (ShellSurfaceWindows.IsShellSurface(className.ToString()))
        {
            return false;
        }

        if (DockNativeMethods.DwmGetWindowAttribute(
                window,
                DockNativeMethods.DwmwaExtendedFrameBounds,
                out DockNativeMethods.NativeRect rectangle,
                System.Runtime.InteropServices.Marshal.SizeOf<DockNativeMethods.NativeRect>()) != 0
            && !DockNativeMethods.GetWindowRect(window, out rectangle))
        {
            return false;
        }

        bounds = new PixelRect(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
        return bounds.HasArea;
    }

    private static string BuildToolTip(DockItem item, string name)
    {
        if (item.Group is null)
        {
            return name;
        }

        var builder = new StringBuilder(name);
        foreach (var window in item.Group.Windows)
        {
            if (!string.IsNullOrWhiteSpace(window.Title) && window.Title != name)
            {
                builder.AppendLine().Append(window.Title);
            }
        }
        return builder.ToString();
    }

    private string BuildSignature(IReadOnlyList<DockItem> items)
    {
        var builder = new StringBuilder();
        foreach (var item in items)
        {
            builder.Append(item.Key)
                .Append(':')
                .Append(DisplayNameOf(item))
                .Append(item.IsPinned ? 'P' : '-')
                .Append(item.IsActive ? 'A' : '-')
                .Append(item.WindowCount)
                .Append(':');
            if (item.Group is not null)
            {
                foreach (var window in item.Group.Windows)
                {
                    builder.Append(window.Handle)
                        .Append(window.IsMinimized ? 'M' : '-')
                        .Append('`')
                        .Append(window.Title)
                        .Append('`');
                }
            }
            builder.Append('|');
        }
        return builder.ToString();
    }
}
