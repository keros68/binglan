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
using BingLan.App.Services;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using Button = System.Windows.Controls.Button;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using Image = System.Windows.Controls.Image;
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
    private const double CommandGlyphCanvasSize = 24;
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
    private readonly MaximizeStateWatcher _maximizeWatcher;
    private readonly DispatcherTimer _foregroundTimer;
    // After a system surface closes, waits for the taskbar to slide away before the
    // dock comes back, mirroring the order in which they swapped when it opened.
    private readonly DispatcherTimer _yieldReleaseTimer;
    // Ends the yield fade even when its completion is never raised (no frames rendered).
    private readonly DispatcherTimer _yieldHideTimer;
    private long _yieldReleaseDeadline;
    private static readonly TimeSpan YieldReleaseCheckInterval = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan YieldReleaseLimit = TimeSpan.FromMilliseconds(600);
    private DockAutoHideState _autoHide = new();
    private readonly Dictionary<string, DockAppVisual> _visuals = new(StringComparer.OrdinalIgnoreCase);
    private DockAppBarController? _appBar;
    private IReadOnlyList<DockItem> _items = [];
    private IReadOnlyList<string> _groupOrder = [];
    private readonly List<DockButtonParts> _itemButtons = [];
    private string _lastSignature = string.Empty;
    private string _lastStructureKey = string.Empty;
    private (bool Start, bool Launcher, bool Desktop) _renderedCommandButtons;
    private nint _handle;
    private bool _closing;
    private bool _isChangingVisibilityByApp;
    private bool _refreshRunning;
    private bool _refreshQueued;
    private bool _menuOpen;
    private bool _revealPending;
    private bool _suppressedBySystemMenu;
    private bool _hiddenBySmartHide;
    private bool _refreshPending;
    private bool _reservationFailureReported;
    private bool _vacatedForMaximized;
    private DockHandleWindow? _handleWindow;
    private bool _handleExpanded;
    private bool _handleAllowed = true;
    private PixelRect _expandedRect;
    private readonly DockMorphAnimator _morph;
    private readonly DockGlassSurface _glass;
    private bool _trueFullScreenForeground;
    private NonActivatingPopupMenu? _openMenu;
    private DateTimeOffset _lastReservationAttempt;
    private readonly DockAttention _attention = new();
    private readonly DockToggleCooldown _toggleCooldown = new(() => Environment.TickCount64);
    // The icon size the row is built with: the chosen size, shrunk while auto-fitting.
    private double _effectiveIconSize = DockState.DefaultIconSize;
    private bool _dockOverflowing;
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
    private readonly DockDragHoverState _dragHover = new();
    private readonly DispatcherTimer _dragHoverTimer;
    private DockButtonParts? _hoveredDragParts;
    private bool _hoveredDragOpensWithApp;
    private bool _externalDragActive;
    private string[]? _cachedDragPaths;
    private Task<IReadOnlyList<DroppedPathInfo>>? _cachedDragInfoTask;

    internal IceBlueDockWindow(DockState state)
    {
        _state = state;
        InitializeComponent();
        _morph = new DockMorphAnimator(DockSurface);
        _glass = new DockGlassSurface(this, DockSurface, "DockSurfaceBrush", "DockSurfaceBorderBrush");
        _morph.StateChanged += () => _glass.TrackAnimation(_morph.ActiveKind != DockMorphKind.None);
        _morph.CollapseCompleted += OnCollapseMorphCompleted;
        _watcher = new WindowEventWatcher(Dispatcher);
        _watcher.SnapshotInvalidated += RefreshWindows;
        // Start, a tray panel or the taskbar taking the foreground is answered at once
        // instead of on the next poll, so the dock steps aside while the taskbar slides in.
        _watcher.ForegroundChanged += () =>
        {
            if (!_closing) UpdateForForeground();
        };
        // The poll cannot catch the maximize click itself; the accessibility event
        // can, so the reservation release and the collapse start on the same beat as
        // the window's own maximize animation.
        _maximizeWatcher = new MaximizeStateWatcher(Dispatcher);
        _maximizeWatcher.TopLevelStateChanged += () =>
        {
            if (!_closing)
            {
                UpdateForForeground();
            }
        };
        _foregroundTimer = new DispatcherTimer(
            FullScreenCheckInterval,
            DispatcherPriority.Background,
            (_, _) => UpdateForForeground(),
            Dispatcher);
        _foregroundTimer.Stop();
        _yieldReleaseTimer = new DispatcherTimer(
            YieldReleaseCheckInterval,
            DispatcherPriority.Input,
            (_, _) => CheckYieldRelease(),
            Dispatcher);
        _yieldReleaseTimer.Stop();
        _yieldHideTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher);
        _yieldHideTimer.Tick += (_, _) =>
        {
            _yieldHideTimer.Stop();
            if (!_closing && _suppressedBySystemMenu && IsVisible) CompleteSmartHideImmediately();
        };
        _dragHoverTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(100),
            DispatcherPriority.Input,
            (_, _) => OnDragHoverTick(),
            Dispatcher);
        _dragHoverTimer.Stop();
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        DockSurface.DragOver += OnSurfaceDragOver;
        DockSurface.DragLeave += OnSurfaceDragLeave;
        DockSurface.Drop += OnDropOnSurface;
        // When the row overflows, the wheel scrolls it sideways instead of doing nothing.
        ItemScroller.PreviewMouseWheel += (sender, e) =>
        {
            if (ItemScroller.ScrollableWidth <= 0)
            {
                return;
            }
            ItemScroller.ScrollToHorizontalOffset(
                ItemScroller.HorizontalOffset - Math.Sign(e.Delta) * ItemCellStep());
            e.Handled = true;
        };
    }

    private double ItemCellStep() => DockLayoutMetrics.ItemCell(_effectiveIconSize);

    internal event Action? DockStateChanged;

    internal event Action? SettingsRequested;
    internal event Action? LauncherRequested;

    internal Func<IReadOnlyList<(Guid Id, string Title)>>? ReadFileBoxes { get; init; }
    internal Func<Guid, string, Task<string>>? SendToFileBox { get; init; }
    internal Func<nint, nint>? ResolvePanelForeground { get; init; }

    internal event Action? HideRequested;

    internal event Action? ShellRecovered;

    internal event Action? ShowDesktopTriggered;

    internal event Action<string>? CommandFailed;

    internal IEnumerable<nint> ActiveDesktopHandles()
    {
        if (_closing || _trueFullScreenForeground || _suppressedBySystemMenu)
        {
            yield break;
        }
        if (IsVisible && !_hiddenBySmartHide && _handle != 0)
        {
            yield return _handle;
        }
        if (_handleWindow is { IsVisible: true, WindowHandle: var handle } && handle != 0)
        {
            yield return handle;
        }
    }

    internal void EnsureAboveDesktop()
    {
        if (_closing || _trueFullScreenForeground || _suppressedBySystemMenu)
        {
            return;
        }
        if (IsVisible && !_hiddenBySmartHide && _handle != 0)
        {
            if (NativeMethods.IsIconic(_handle) || !NativeMethods.IsWindowVisible(_handle))
            {
                NativeMethods.ShowWindow(_handle, NativeMethods.SwShowNoActivate);
            }
            _appBar?.EnsureLayer();
        }
        _handleWindow?.EnsureAboveDesktop();
    }

    private void HideFromApp()
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

    /// <summary>Re-reads monitor and visibility settings from the dock state.</summary>
    internal void ApplyState()
    {
        if (_appBar is null || _closing)
        {
            return;
        }

        using var _ = UiDiagnostics.Measure("Dock 状态应用");

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
        UpdateEffectiveIconSize(monitor, _items);
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
            if (_morph.IsCollapsing)
            {
                // A flight whose completion would hide the dock must not outlive the
                // switch to a mode that keeps the dock shown.
                CompleteSmartHideImmediately();
            }
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

    // A reserved dock whose AppBar registration failed would float over windows, so it
    // falls back to smart hide until the reservation succeeds (without blinking during
    // Explorer restart recovery).
    private bool UsesSmartHide =>
        _state.VisibilityMode == DockVisibilityMode.SmartHide
        || (_appBar is not { IsReserved: true } && _appBar is not { IsRecovering: true });

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
        _maximizeWatcher.Start();
        ApplyState();
        RefreshWindows();
    }

    private nint PreventActivation(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmMouseActivate)
        {
            handled = true;
            return MaNoActivate;
        }

        if (!_closing && !_isChangingVisibilityByApp)
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
                     && (System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.WindowPos>(lParam).Flags
                         & NativeMethods.SwpHideWindow) != 0)
            {
                RecoverFromShell(minimized: false);
            }
        }
        return 0;
    }

    private void RecoverFromShell(bool minimized)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Send,
            new Action(() =>
            {
                if (_closing || _handle == 0 || !IsVisible || _hiddenBySmartHide || _suppressedBySystemMenu)
                {
                    return;
                }
                if (minimized ? !NativeMethods.IsIconic(_handle) : NativeMethods.IsWindowVisible(_handle))
                {
                    return;
                }
                NativeMethods.ShowWindow(_handle, NativeMethods.SwShowNoActivate);
                _appBar?.EnsureLayer();
                ShellRecovered?.Invoke();
            }));
    }

    private nint OnShellHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_shellHookMessage == 0 || (uint)message != _shellHookMessage)
        {
            return 0;
        }

        var changed = (int)wParam switch
        {
            HshellFlash => MarkFlashed(lParam),
            HshellWindowActivated or HshellRudeAppActivated => ClearActivatedAttention(lParam),
            HshellWindowDestroyed => _attention.Closed(lParam),
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
            _lastStructureKey = string.Empty;
            RefreshWindows();
        }
        return 0;
    }

    private bool MarkFlashed(nint window)
    {
        if (window == 0 || !DockNativeMethods.IsWindow(window))
        {
            return false;
        }

        // Apps like chat clients flash through hidden message windows; remembering the
        // process lets the mark survive on the app's visible windows in the dock, and a
        // count published in the flashed title ("微信(3)") becomes the badge number.
        DockNativeMethods.GetWindowThreadProcessId(window, out var rawProcessId);
        var executablePath = rawProcessId != 0 ? WindowCatalog.ExecutablePathOf(rawProcessId) : null;
        var className = new StringBuilder(64);
        DockNativeMethods.GetClassNameW(window, className, className.Capacity);
        var foreground = DockNativeMethods.GetForegroundWindow();
        var foregroundMinimized = foreground != 0 && DockNativeMethods.IsIconic(foreground);
        if (!AttentionFlashRules.ShouldRecordFlash(
                window,
                isLiveWindow: true,
                rawProcessId,
                (uint)Environment.ProcessId,
                executablePath,
                className.ToString(),
                foreground,
                EffectiveAttentionProcessId(foreground),
                foregroundMinimized))
        {
            return false;
        }

        var title = new StringBuilder(256);
        DockNativeMethods.GetWindowTextW(window, title, title.Capacity);
        return _attention.Flashed(
            window,
            AttentionFlashRules.EffectiveProcessId(rawProcessId, executablePath),
            UnreadBadgeRules.ParseCount(title.ToString()));
    }

    private bool ClearActivatedAttention(nint window)
    {
        var active = window != 0 ? window : DockNativeMethods.GetForegroundWindow();
        return active != 0 && _attention.Cleared(active, EffectiveAttentionProcessId(active));
    }

    private static uint EffectiveAttentionProcessId(nint window)
    {
        if (window == 0)
        {
            return 0;
        }
        DockNativeMethods.GetWindowThreadProcessId(window, out var processId);
        return processId == 0
            ? 0
            : AttentionFlashRules.EffectiveProcessId(processId, WindowCatalog.ExecutablePathOf(processId));
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        if (_openMenu is { } menu)
        {
            menu.IsOpen = false;
            _openMenu = null;
        }
        EndExternalDrag();
        _glass.Dispose();
        _morph.Snap();
        if (_handle != 0)
        {
            DockNativeMethods.DeregisterShellHookWindow(_handle);
        }
        _foregroundTimer.Stop();
        _yieldReleaseTimer.Stop();
        _yieldHideTimer.Stop();
        _watcher.Dispose();
        _maximizeWatcher.Dispose();
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
                UiDiagnostics.Hit(UiActivity.DockRefresh);
                _refreshQueued = false;
                var pinned = _state.PinnedApps.ToArray();
                var hidden = _state.HiddenApps.ToArray();
                var snapshot = await Task.Run(_catalog.Capture);
                IReadOnlyList<DockItem> items;
                using (UiDiagnostics.Measure("Dock 窗口分组"))
                {
                    var groups = WindowGrouping.OrderByFirstSeen(WindowGrouping.Group(snapshot), _groupOrder);
                    _groupOrder = groups.Select(group => group.Key).ToArray();
                    items = DockItemComposer.Compose(pinned, groups, hidden);
                    _attention.RemoveRange(_attention.MarkedWindows
                        .Where(handle => !DockNativeMethods.IsWindow(handle))
                        .ToArray());
                }
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
        using var _ = UiDiagnostics.Measure("Dock 应用行");
        if (_appBar?.Monitor is { } monitor)
        {
            // The row is built from the fitted size; the reserved length follows it.
            UpdateEffectiveIconSize(monitor, items);
        }
        var commands = VisibleCommands;
        var rowSettings = $"{_effectiveIconSize}|{commands}|{_dockOverflowing}";
        var signature = $"{rowSettings}|{BuildSignature(items)}|{string.Join(",", items.Select(item => $"{(_attention.Wants(item.Group) ? '!' : '-')}{_attention.UnreadCount(item.Group)}"))}";
        if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
        {
            return;
        }

        var countChanged = items.Count != _items.Count
            || items.Count(item => item.IsPinned) != _items.Count(item => item.IsPinned)
            || _renderedCommandButtons != commands;
        _renderedCommandButtons = commands;
        _lastSignature = signature;
        _items = items;

        var structureKey = $"{rowSettings}|{BuildStructureKey(items)}";
        if (string.Equals(structureKey, _lastStructureKey, StringComparison.Ordinal)
            && _itemButtons.Count == items.Count)
        {
            for (var index = 0; index < items.Count; index++)
            {
                UpdateItemButton(_itemButtons[index], items[index]);
            }
            if (countChanged)
            {
                ApplyState();
            }
            return;
        }

        ClearDragTargetVisual();
        _lastStructureKey = structureKey;
        _itemButtons.Clear();
        ItemPanel.Children.Clear();
        if (commands.Start)
        {
            ItemPanel.Children.Add(BuildStartMenuButton());
        }
        if (commands.Launcher)
        {
            ItemPanel.Children.Add(BuildLauncherButton());
        }
        if (commands.Desktop)
        {
            ItemPanel.Children.Add(BuildShowDesktopButton());
        }
        if (LeadingButtonCount > 0 && items.Count > 0)
        {
            ItemPanel.Children.Add(BuildDivider());
        }
        for (var index = 0; index < items.Count; index++)
        {
            // Pinned apps come first; a thin line sets the other running apps apart.
            if (index > 0 && items[index - 1].IsPinned && !items[index].IsPinned)
            {
                ItemPanel.Children.Add(BuildDivider());
            }
            var parts = BuildItem(items[index]);
            _itemButtons.Add(parts);
            ItemPanel.Children.Add(parts.Button);
        }

        if (_dockOverflowing)
        {
            // Too many apps even at the minimum icon: the row scrolls, and this menu
            // keeps every app reachable without hunting for the scroll position.
            ItemPanel.Children.Add(BuildOverflowButton());
        }

        if (countChanged)
        {
            ApplyState();
        }
    }

    private Button BuildOverflowButton()
    {
        var glyph = new TextBlock
        {
            Text = "\uE712",
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"),
            FontSize = Math.Max(10, _effectiveIconSize * 0.5),
            Foreground = System.Windows.Media.Brushes.White,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var button = new Button
        {
            Content = glyph,
            Width = _effectiveIconSize + 16,
            Height = _effectiveIconSize + 16,
            Style = (Style)Resources["DockItemButtonStyle"],
            ToolTip = "应用较多，点击查看全部应用；也可用滚轮横向滚动",
            Focusable = true
        };
        AutomationProperties.SetName(button, "查看全部应用");
        button.Click += (_, _) =>
        {
            var menu = CreateMenu(button, PlacementMode.Top);
            foreach (var item in _items)
            {
                var icon = _visuals.TryGetValue(item.Key, out var visual) ? visual.Icon : null;
                var captured = item;
                menu.AddItem(
                    DisplayNameOf(item),
                    () => Invoke(captured),
                    icon);
            }
            menu.IsOpen = true;
        };
        return button;
    }

    private Button BuildStartMenuButton()
    {
        var glyph = new Grid { Width = CommandGlyphCanvasSize, Height = CommandGlyphCanvasSize };
        glyph.RowDefinitions.Add(new RowDefinition());
        glyph.RowDefinitions.Add(new RowDefinition());
        glyph.ColumnDefinitions.Add(new ColumnDefinition());
        glyph.ColumnDefinitions.Add(new ColumnDefinition());
        for (var row = 0; row < 2; row++)
        {
            for (var column = 0; column < 2; column++)
            {
                var tile = new Border
                {
                    Margin = new Thickness(1.15),
                    CornerRadius = new CornerRadius(1.25)
                };
                tile.SetResourceReference(Border.BackgroundProperty, "DockRunningDotBrush");
                Grid.SetRow(tile, row);
                Grid.SetColumn(tile, column);
                glyph.Children.Add(tile);
            }
        }

        var button = BuildDockCommandButton(glyph, "打开 Windows 开始菜单", "打开 Windows 开始菜单；右键打开系统快捷菜单");
        AutomationProperties.SetHelpText(button, "单击打开 Windows 开始菜单；右键或按菜单键、Shift+F10 打开系统快捷菜单");
        button.Click += async (_, _) =>
        {
            var result = await ShellSurfaceService.OpenStartMenuAsync();
            if (result != ShellSurfaceResult.Opened)
            {
                CommandFailed?.Invoke(WindowActivationRules.FormatStartMenuBlockedMessage(
                    ShellSurfaceNativeMethods.IsTaskbarElevated(),
                    result == ShellSurfaceResult.ElevatedForeground
                        || ShellSurfaceNativeMethods.IsForegroundElevated()));
            }
        };
        button.MouseRightButtonUp += async (_, e) =>
        {
            e.Handled = true;
            await OpenQuickLinkMenuAsync();
        };
        button.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
            {
                e.Handled = true;
                if (!e.IsRepeat) await OpenQuickLinkMenuAsync();
            }
        };
        return button;
    }

    private Button BuildShowDesktopButton()
    {
        var glyph = CreateDockCommandOutline("M4,3.5 H20 A2,2 0 0 1 22,5.5 V15.5 A2,2 0 0 1 20,17.5 H4 A2,2 0 0 1 2,15.5 V5.5 A2,2 0 0 1 4,3.5 Z M12,17.5 V21 M7.5,21 H16.5");
        var button = BuildDockCommandButton(glyph, "显示桌面", "显示桌面；再次点击恢复窗口（快捷键 Win+D）");
        AutomationProperties.SetHelpText(button, "最小化所有窗口显示桌面，再次点击恢复；全屏时可按 Win+D");
        button.Click += async (_, _) =>
        {
            if (!await ShellSurfaceService.ToggleDesktopAsync())
            {
                CommandFailed?.Invoke("无法显示桌面，请按 Win+D。");
                return;
            }
            ShowDesktopTriggered?.Invoke();
        };
        return button;
    }

    private Button BuildLauncherButton()
    {
        var glyph = CreateDockCommandOutline("M4,4 H20 A2,2 0 0 1 22,6 V18 A2,2 0 0 1 20,20 H4 A2,2 0 0 1 2,18 V6 A2,2 0 0 1 4,4 Z M2,9 H22 M6,13 H13 M6,16 H17");
        var button = BuildDockCommandButton(glyph, "打开启动面板", "打开启动面板");
        button.Click += (_, _) => LauncherRequested?.Invoke();
        return button;
    }

    private static System.Windows.Shapes.Path CreateDockCommandOutline(string data)
    {
        var frame = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(data),
            Width = CommandGlyphCanvasSize,
            Height = CommandGlyphCanvasSize,
            StrokeThickness = 1.75,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        frame.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "DockRunningDotBrush");
        return frame;
    }

    private Button BuildDockCommandButton(FrameworkElement glyph, string name, string toolTip)
    {
        var size = _effectiveIconSize * 0.72;
        var icon = new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = glyph,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var iconSlot = new Grid { Width = _effectiveIconSize, Height = _effectiveIconSize };
        iconSlot.Children.Add(icon);
        // Reserve the running-indicator slot to align with the app icons.
        var content = new StackPanel();
        content.Children.Add(iconSlot);
        content.Children.Add(new Border { Height = 7, Visibility = Visibility.Hidden });
        var button = new Button
        {
            Content = content,
            Width = _effectiveIconSize + 16,
            Height = _effectiveIconSize + 16,
            Style = (Style)Resources["DockItemButtonStyle"],
            ToolTip = toolTip,
            Focusable = true
        };
        AutomationProperties.SetName(button, name);
        AddHoverZoom(button, icon);
        return button;
    }

    private async Task OpenQuickLinkMenuAsync()
    {
        switch (await ShellSurfaceService.OpenQuickLinkMenuAsync())
        {
            case ShellSurfaceResult.ElevatedForeground:
                CommandFailed?.Invoke("当前前台是管理员程序，Windows 不允许向它发送快捷键。请先切换到其他窗口再试。");
                break;
            case ShellSurfaceResult.Failed:
                CommandFailed?.Invoke("无法打开 Windows 系统快捷菜单，请稍后重试。");
                break;
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
            Height = _effectiveIconSize * 0.8,
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        divider.SetResourceReference(Border.BackgroundProperty, "DockSurfaceBorderBrush");
        return divider;
    }

    private sealed record DockButtonParts(
        Button Button,
        Image Icon,
        Grid IconHost,
        Border Dot,
        ScaleTransform IconScale);

    private DockButtonParts BuildItem(DockItem item)
    {
        var content = new StackPanel { Orientation = Orientation.Vertical };
        var icon = new Image
        {
            Width = _effectiveIconSize,
            Height = _effectiveIconSize,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var iconHost = new Grid();
        iconHost.Children.Add(icon);
        content.Children.Add(iconHost);
        // The dot under a running app: wider for several windows, blue for the current one.
        var dot = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        content.Children.Add(dot);

        var button = new Button
        {
            Content = content,
            Width = _effectiveIconSize + 16,
            Height = _effectiveIconSize + 16,
            Style = (Style)Resources["DockItemButtonStyle"],
            Tag = item,
            AllowDrop = true
        };

        button.Click += (_, _) =>
        {
            if (button.Tag is not DockItem current) return;
            var currentName = DisplayNameOf(current);
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                LaunchNewInstance(current, currentName);
            }
            else
            {
                Invoke(current);
            }
        };
        button.MouseUp += (_, eventArgs) =>
        {
            if (eventArgs.ChangedButton == MouseButton.Middle && button.Tag is DockItem current)
            {
                eventArgs.Handled = true;
                LaunchNewInstance(current, DisplayNameOf(current));
            }
        };
        var iconScale = AddHoverZoom(button, icon);
        button.ContextMenuOpening += (_, eventArgs) => eventArgs.Handled = true;
        button.MouseRightButtonUp += (_, eventArgs) =>
        {
            if (button.Tag is not DockItem current) return;
            eventArgs.Handled = true;
            var currentName = DisplayNameOf(current);
            Dispatcher.BeginInvoke(
                () =>
                {
                    if (!_closing && IsVisible)
                    {
                        OpenItemMenu(current, currentName);
                    }
                },
                DispatcherPriority.Input);
        };
        button.PreviewKeyDown += (_, eventArgs) =>
        {
            if ((eventArgs.Key == Key.Apps || (eventArgs.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
                && button.Tag is DockItem current)
            {
                eventArgs.Handled = true;
                if (!eventArgs.IsRepeat)
                {
                    OpenItemMenu(current, DisplayNameOf(current), button);
                }
            }
        };
        button.PreviewMouseLeftButtonDown += (_, eventArgs) =>
        {
            if (button.Tag is not DockItem current) return;
            // Pinned apps are dragged to reorder; running apps are dragged in to pin them.
            _dragStart = eventArgs.GetPosition(this);
            _dragItem = current;
        };
        button.PreviewMouseMove += OnItemMouseMove;

        var parts = new DockButtonParts(button, icon, iconHost, dot, iconScale);
        button.DragOver += (_, eventArgs) => OnItemDragOver(parts, eventArgs);
        button.Drop += (_, eventArgs) =>
        {
            if (parts.Button.Tag is DockItem current)
            {
                OnItemDrop(current, eventArgs);
            }
        };
        UpdateItemButton(parts, item);
        return parts;
    }

    private void UpdateItemButton(DockButtonParts parts, DockItem item)
    {
        parts.Button.Tag = item;
        parts.Icon.Source = _visuals.TryGetValue(item.Key, out var visual) ? visual.Icon : null;
        while (parts.IconHost.Children.Count > 1)
        {
            parts.IconHost.Children.RemoveAt(parts.IconHost.Children.Count - 1);
        }

        var wantsAttention = _attention.Wants(item.Group);
        var unread = _attention.UnreadCount(item.Group);
        if (wantsAttention && unread > 0)
        {
            // A count the app published becomes a red pill; like macOS, "99+" past the cap.
            var pill = new Border
            {
                MinWidth = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4, 0, 4, 0),
                Margin = new Thickness(0, -4, -4, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Background = UnreadPillBrush,
                BorderBrush = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = UnreadBadgeRules.BadgeText(unread),
                    Foreground = System.Windows.Media.Brushes.White,
                    FontSize = Math.Max(9, _effectiveIconSize * 0.28),
                    FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                    FontWeight = System.Windows.FontWeights.SemiBold,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            parts.IconHost.Children.Add(pill);
        }
        else if (wantsAttention)
        {
            parts.IconHost.Children.Add(new Border
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

        parts.Dot.Width = item.HasMultipleWindows ? 10 : 4;
        parts.Dot.Visibility = item.IsRunning ? Visibility.Visible : Visibility.Hidden;
        if (wantsAttention)
        {
            parts.Dot.Background = AttentionBrush;
        }
        else
        {
            parts.Dot.SetResourceReference(
                Border.BackgroundProperty,
                item.IsActive ? "DockActiveDotBrush" : "DockRunningDotBrush");
        }

        var name = DisplayNameOf(item);
        parts.Button.ToolTip = BuildToolTip(item, name);
        AutomationProperties.SetName(parts.Button, name);
        AutomationProperties.SetHelpText(parts.Button, wantsAttention ? "有新提醒" : item.State switch
        {
            DockItemState.Active => "当前窗口",
            DockItemState.Running => item.HasMultipleWindows ? $"{item.WindowCount} 个窗口" : "正在运行",
            _ => "已固定，未运行"
        });
    }

    private static readonly SolidColorBrush UnreadPillBrush = CreateUnreadPillBrush();

    private static SolidColorBrush CreateUnreadPillBrush()
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x3B, 0x30));
        brush.Freeze();
        return brush;
    }

    private static readonly SolidColorBrush AttentionBrush = CreateAttentionBrush();

    private static SolidColorBrush CreateAttentionBrush()
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x7A, 0x2F));
        brush.Freeze();
        return brush;
    }

    private async void Invoke(DockItem item)
    {
        if (item.Group is not null)
        {
            var result = await WindowCommandService.ToggleAsync(
                item.Group,
                _toggleCooldown,
                DisplayNameOf(item));
            if (!result.NoLiveWindows)
            {
                Report(result);
                return;
            }

            RefreshWindows();
            if (item.Pinned is null)
            {
                return;
            }
        }

        if (item.Pinned is null)
        {
            return;
        }

        // A second click while a tray app is being raised would send its toggle
        // hotkey again and hide the window that is just coming back.
        if (_revealPending)
        {
            return;
        }

        _revealPending = true;
        try
        {
            Report(await WindowCommandService.RevealOrLaunchAsync(item.Pinned));
        }
        finally
        {
            _revealPending = false;
        }
    }

    private async Task FocusWindowFromMenuAsync(TrackedWindow window, string displayName)
    {
        var result = await WindowCommandService.FocusAsync(
            window,
            DockNativeMethods.IsIconic(window.Handle),
            displayName);
        if (result.NoLiveWindows)
        {
            RefreshWindows();
            return;
        }
        Report(result);
    }

    private async void LaunchNewInstance(DockItem item, string name)
    {
        var pin = item.Pinned ?? DockAppResolver.CreatePin(item, name);
        Report(pin is not null
            ? await WindowCommandService.LaunchAsync(pin)
            : new WindowCommandResult(false, $"{name} 不支持从 Dock 启动新实例"));
    }

    private static ScaleTransform AddHoverZoom(Button button, UIElement icon)
    {
        var scale = new ScaleTransform(1, 1);
        icon.RenderTransformOrigin = new Point(0.5, 1);
        icon.RenderTransform = scale;
        button.MouseEnter += (_, _) => Zoom(
            scale,
            DesktopStyleRules.HoverZoom(Motion, SystemParameters.ClientAreaAnimation));
        button.MouseLeave += (_, _) => Zoom(scale, 1);
        return scale;
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

    private void OpenItemMenu(DockItem item, string name, UIElement? anchor = null)
    {
        var menu = anchor is null
            ? CreateMenu()
            : CreateMenu(anchor, PlacementMode.Top);
        if (item.Group is { } group)
        {
            if (item.HasMultipleWindows)
            {
                foreach (var window in group.Windows)
                {
                    var captured = window;
                    menu.AddItem(
                        string.IsNullOrWhiteSpace(captured.Title) ? name : captured.Title,
                        () => _ = FocusWindowFromMenuAsync(captured, name));
                }
                menu.AddSeparator();
            }

            var launchPin = item.Pinned ?? DockAppResolver.CreatePin(item, name);
            if (launchPin is not null)
            {
                menu.AddItem("启动新实例", async () => Report(await WindowCommandService.LaunchAsync(launchPin)));
            }
            menu.AddItem(
                item.HasMultipleWindows ? $"关闭全部 {item.WindowCount} 个窗口" : "关闭窗口",
                () => Report(WindowCommandService.Close(group.Windows)));
        }

        var isPathPin = item.Pinned is { } pathPin && DockPinRules.IsPathPin(pathPin);
        var executablePath = item.Pinned?.ExecutablePath
            ?? item.Pinned?.TargetPath
            ?? item.Group?.Windows
                .Select(window => window.ExecutablePath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (executablePath is not null
            && !DockAppResolver.IsFrameHost(executablePath)
            && !DockAppResolver.IsPackagedPath(executablePath))
        {
            menu.AddItem(
                "打开文件位置",
                async () => Report(await Task.Run(() => WindowCommandService.OpenFileLocation(executablePath))));
        }
        var mappingPath = item.Pinned is { AppUserModelId: null } mappingPin
            && string.IsNullOrWhiteSpace(mappingPin.Arguments)
            && (string.IsNullOrWhiteSpace(mappingPin.WorkingDirectory)
                || string.Equals(mappingPin.WorkingDirectory, System.IO.Path.GetDirectoryName(mappingPin.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase))
            ? mappingPin.TargetPath ?? mappingPin.ExecutablePath
            : null;
        if (mappingPath is not null && System.IO.Path.IsPathFullyQualified(mappingPath)
            && ReadFileBoxes is not null && SendToFileBox is not null)
        {
            menu.AddItem("发送到分组盒…", () => OpenSendToFileBoxMenu(mappingPath, anchor));
        }

        if (menu.HasItems)
        {
            menu.AddSeparator();
        }
        if (!isPathPin && item.Pinned is { } iconPin && DockAppResolver.IconStore is { } iconStore)
        {
            menu.AddItem("更换图标…", () => ChangeIcon(item, iconPin, iconStore));
            if (iconPin.IconFile is not null)
            {
                menu.AddItem("恢复默认图标", () => SetIcon(item, iconPin, iconStore, null));
            }
        }
        if (item.Pinned is { } pinned)
        {
            menu.AddItem("从 Dock 取消固定", () =>
            {
                if (DockPinRules.Unpin(_state, DockPinRules.IdentityKey(pinned)))
                {
                    DockAppResolver.IconStore?.Delete(pinned.IconFile);
                    PinsChanged();
                }
            });
        }
        else if (DockAppResolver.CreatePin(item, name) is { } pin)
        {
            menu.AddItem("固定到 Dock", () =>
            {
                if (DockPinRules.Pin(_state, pin))
                {
                    PinsChanged();
                }
            });
            menu.AddItem("不在 Dock 中显示此应用", () =>
            {
                if (DockPinRules.Hide(_state, pin))
                {
                    PinsChanged();
                }
            });
        }
        menu.AddItem("Dock 设置…", () => SettingsRequested?.Invoke());
        menu.IsOpen = true;
    }

    private void OpenSendToFileBoxMenu(string path, UIElement? anchor)
    {
        if (ReadFileBoxes is not { } read || SendToFileBox is not { } send)
        {
            return;
        }

        var boxes = read();
        if (boxes.Count == 0)
        {
            CommandFailed?.Invoke("请先新建分组盒。");
            return;
        }

        var menu = anchor is null ? CreateMenu() : CreateMenu(anchor, PlacementMode.Top);
        foreach (var (id, title) in boxes)
        {
            menu.AddItem(title, async () =>
            {
                var status = await send(id, path);
                CommandFailed?.Invoke(status);
            });
        }
        menu.IsOpen = true;
    }

    private void OnDockSurfaceRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                if (_closing || !IsVisible)
                {
                    return;
                }
                var menu = CreateMenu();
                menu.AddItem("添加应用…", PickAppToPin);
                menu.AddItem("Dock 设置…", () => SettingsRequested?.Invoke());
                menu.AddItem("关闭冰蓝 Dock", () => HideRequested?.Invoke());
                menu.IsOpen = true;
            },
            DispatcherPriority.Input);
    }

    private NonActivatingPopupMenu CreateMenu(
        UIElement? placementTarget = null,
        PlacementMode placement = PlacementMode.Mouse)
    {
        if (_openMenu is { IsOpen: true } existing)
        {
            existing.IsOpen = false;
        }
        var darkSurface = TryFindResource("DockSurfaceBrush") is SolidColorBrush brush
            && (0.2126d * brush.Color.R + 0.7152d * brush.Color.G + 0.0722d * brush.Color.B) / 255d <= 0.6d;
        var menu = new NonActivatingPopupMenu(placementTarget ?? DockSurface, placement, darkSurface);
        menu.Opened += (_, _) =>
        {
            _menuOpen = true;
            _openMenu = menu;
        };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openMenu, menu))
            {
                _menuOpen = false;
                _openMenu = null;
            }
        };
        return menu;
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

    private void OnSurfaceDragOver(object sender, DragEventArgs e)
    {
        ClearDragTargetVisual();
        _dragHover.Clear();

        if (e.Data.GetDataPresent(PinDragFormat) || e.Data.GetDataPresent(RunningDragFormat))
        {
            e.Effects = (e.AllowedEffects & DragDropEffects.Move) != 0
                ? DragDropEffects.Move
                : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            _externalDragActive = true;
            if (!_dragHoverTimer.IsEnabled)
            {
                _dragHoverTimer.Start();
            }
            e.Effects = (e.AllowedEffects & DragDropEffects.Link) != 0
                ? DragDropEffects.Link
                : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnItemDragOver(DockButtonParts parts, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(PinDragFormat) || e.Data.GetDataPresent(RunningDragFormat))
        {
            ClearDragTargetVisual();
            _dragHover.Clear();
            e.Effects = (e.AllowedEffects & DragDropEffects.Move) != 0
                ? DragDropEffects.Move
                : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (parts.Button.Tag is not DockItem current
            || e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files)
        {
            ClearDragTargetVisual();
            _dragHover.Clear();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        _externalDragActive = true;
        if (!_dragHoverTimer.IsEnabled)
        {
            _dragHoverTimer.Start();
        }

        var inspection = InspectDragPathsAsync(files);
        // Accept a quick copy-only drop while the first disk read is pending. The
        // drop awaits the real classification before opening files or pinning paths.
        var opensWithApp = inspection.IsCompletedSuccessfully
            ? DockFileDropRules.Decide(current, inspection.Result) == DockFileDropAction.OpenWithApp
            : files.Length > 0 && DockFileDropRules.CanOpenFiles(current);
        if (opensWithApp)
        {
            e.Effects = (e.AllowedEffects & DragDropEffects.Copy) != 0
                ? DragDropEffects.Copy
                : e.AllowedEffects & DragDropEffects.Link;
            SetDragTargetVisual(parts, opensWithApp: true);
        }
        else
        {
            e.Effects = (e.AllowedEffects & DragDropEffects.Link) != 0
                ? DragDropEffects.Link
                : DragDropEffects.None;
            SetDragTargetVisual(parts, opensWithApp: false);
        }

        TryHoverRaise(current);
        e.Handled = true;
    }

    private void OnSurfaceDragLeave(object sender, DragEventArgs e)
    {
        if (!IsCursorInsideDockWindow())
        {
            EndExternalDrag();
        }
    }

    private void OnDragHoverTick()
    {
        if (_closing
            || !IsVisible
            || !ShellSurfaceNativeMethods.IsAnyMouseButtonPressed()
            || !IsCursorInsideDockWindow())
        {
            EndExternalDrag();
            return;
        }

        if (_hoveredDragParts?.Button.Tag is DockItem item)
        {
            TryHoverRaise(item);
        }
    }

    private void TryHoverRaise(DockItem item)
    {
        if (_dragHover.Hover(item.Key, item.IsRunning, DateTimeOffset.UtcNow)
            && item.Group is { } group)
        {
            WindowCommandService.RaiseForDrag(group);
        }
    }

    private Task<IReadOnlyList<DroppedPathInfo>> InspectDragPathsAsync(string[] paths)
    {
        if (_cachedDragInfoTask is not null && _cachedDragPaths is not null
            && _cachedDragPaths.SequenceEqual(paths, StringComparer.Ordinal))
        {
            return _cachedDragInfoTask;
        }

        _cachedDragPaths = paths.ToArray();
        _cachedDragInfoTask = DockShortcuts.InspectDroppedPathsAsync(_cachedDragPaths);
        return _cachedDragInfoTask;
    }

    private void SetDragTargetVisual(DockButtonParts parts, bool opensWithApp)
    {
        if (ReferenceEquals(_hoveredDragParts, parts) && _hoveredDragOpensWithApp == opensWithApp)
        {
            return;
        }

        ClearDragTargetVisual();
        _hoveredDragParts = parts;
        _hoveredDragOpensWithApp = opensWithApp;
        if (!opensWithApp)
        {
            return;
        }

        Zoom(parts.IconScale, DesktopStyleRules.HoverZoom(Motion, SystemParameters.ClientAreaAnimation));
        parts.Button.SetResourceReference(Button.BackgroundProperty, "WidgetTileHoverBrush");
        parts.Button.SetResourceReference(Button.BorderBrushProperty, "DockActiveDotBrush");
    }

    private void ClearDragTargetVisual()
    {
        if (_hoveredDragParts is null)
        {
            return;
        }

        if (_hoveredDragOpensWithApp)
        {
            Zoom(_hoveredDragParts.IconScale, 1);
            _hoveredDragParts.Button.ClearValue(Button.BackgroundProperty);
            _hoveredDragParts.Button.ClearValue(Button.BorderBrushProperty);
        }
        _hoveredDragParts = null;
        _hoveredDragOpensWithApp = false;
    }

    private void EndExternalDrag()
    {
        _externalDragActive = false;
        _dragHoverTimer.Stop();
        _dragHover.Clear();
        ClearDragTargetVisual();
        _cachedDragPaths = null;
        _cachedDragInfoTask = null;
    }

    private bool IsCursorInsideDockWindow()
    {
        if (_handle == 0 || !IsVisible)
        {
            return false;
        }

        return DockNativeMethods.GetCursorPos(out var cursor)
            && DockNativeMethods.GetWindowRect(_handle, out var rect)
            && cursor.X >= rect.Left && cursor.X < rect.Right
            && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
    }

    private async Task OpenFilesWithItemAsync(DockItem item, IReadOnlyList<DroppedPathInfo> paths) =>
        Report(await WindowCommandService.OpenFilesAsync(item, DisplayNameOf(item), paths));

    private async void OnItemDrop(DockItem target, DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files)
        {
            e.Handled = true;
            var inspection = InspectDragPathsAsync(files);
            EndExternalDrag();
            var infos = await inspection;
            if (_closing)
            {
                return;
            }
            if (DockFileDropRules.Decide(target, infos) == DockFileDropAction.OpenWithApp)
            {
                await OpenFilesWithItemAsync(target, infos);
            }
            else
            {
                await PinFilesAsync(files);
            }
            return;
        }

        EndExternalDrag();
        DropPin(e, target);
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

    // Programs, folders and files dropped on the dock are pinned, as are shortcuts to
    // them; only their paths are recorded.
    private async Task PinFilesAsync(IEnumerable<string> paths)
    {
        var apps = await DockShortcuts.CreatePinsAsync(paths);
        if (_closing)
        {
            return;
        }

        var pinned = 0;
        foreach (var app in apps)
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
            CommandFailed?.Invoke("可固定应用、文件和文件夹，或指向它们的快捷方式");
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
        EndExternalDrag();
        if (!e.Handled && e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files)
        {
            e.Handled = true;
            _ = PinFilesAsync(files);
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
        _lastStructureKey = string.Empty;
        PinsChanged();
    }

    private void PinsChanged()
    {
        DockStateChanged?.Invoke();
        RefreshWindows();
    }

    private double ContentLengthDip()
    {
        return DockLayoutMetrics.ContentLengthForItems(
                   _items.Count + LeadingButtonCount + (_dockOverflowing ? 1 : 0),
                   _effectiveIconSize)
               + (DividerCount(_items) * DockLayoutMetrics.DividerDip);
    }

    private (bool Start, bool Launcher, bool Desktop) VisibleCommands =>
        (_state.ShowStartMenuButton, _state.ShowLauncherButton && LauncherRequested is not null, _state.ShowDesktopButton);

    private int LeadingButtonCount
    {
        get
        {
            var (start, launcher, desktop) = VisibleCommands;
            return (start ? 1 : 0) + (launcher ? 1 : 0) + (desktop ? 1 : 0);
        }
    }

    private int DividerCount(IReadOnlyList<DockItem> items) =>
        (LeadingButtonCount > 0 && items.Count > 0 ? 1 : 0)
        + (items.Any(item => item.IsPinned) && items.Any(item => !item.IsPinned) ? 1 : 0);

    private void UpdateEffectiveIconSize(MonitorSnapshot monitor, IReadOnlyList<DockItem> items)
    {
        // The reserved strip spans the monitor, so the row takes up to its fraction of
        // the work-area width without taking space from any window.
        var workArea = TaskbarEdgeGuard.AvoidNativeTaskbar(monitor.WorkingArea, monitor.Bounds);
        var availableDip = workArea.Width * 96d / monitor.Dpi * DockLayoutMetrics.MaxExtentFraction;
        var cellCount = items.Count + LeadingButtonCount;
        var dividersDip = DividerCount(items) * DockLayoutMetrics.DividerDip;
        var preferred = _state.AutoIconSize
            ? DockLayoutMetrics.FitIconSize(cellCount, dividersDip, availableDip, _state.IconSize)
            : _state.IconSize;
        // Even at the minimum icon size not everything fits: the row keeps the overflow
        // button and scrolls horizontally instead of hiding apps for good.
        _dockOverflowing = DockLayoutMetrics.ContentLengthForItems(cellCount, DockLayoutMetrics.MinIconDip) + dividersDip
            > availableDip;
        if (_effectiveIconSize != preferred)
        {
            _effectiveIconSize = preferred;
            _lastSignature = string.Empty;
            _lastStructureKey = string.Empty;
        }
    }

    private void UpdateForForeground()
    {
        UiDiagnostics.Hit(UiActivity.ForegroundCheck);
        if (_appBar?.Monitor is not { } monitor || _closing)
        {
            return;
        }

        using var _ = UiDiagnostics.Measure("Dock 前台判定");

        if (_attention.Count > 0)
        {
            var activeWindow = DockNativeMethods.GetForegroundWindow();
            if (activeWindow != 0
                && !DockNativeMethods.IsIconic(activeWindow)
                && _attention.Cleared(activeWindow, EffectiveAttentionProcessId(activeWindow)))
            {
                _lastSignature = string.Empty;
                RefreshWindows();
            }
        }

        // Native menus own this interaction. The taskbar foreground window has no
        // useful menu bounds, so overlap detection alone would reveal the Dock over it.
        if (YieldToSystemMenu(ShellSurfaceNativeMethods.IsSystemMenuForeground())) return;

        var bounds = _appBar.Bounds;
        var foreground = ForegroundWindowRect(out var foregroundBounds, out var foregroundWindow);
        var fullScreen = foreground
            && foregroundBounds.Left <= monitor.Bounds.Left
            && foregroundBounds.Top <= monitor.Bounds.Top
            && foregroundBounds.Right >= monitor.Bounds.Right
            && foregroundBounds.Bottom >= monitor.Bounds.Bottom;
        var foregroundZoomed = foreground && foregroundWindow != 0 && DockNativeMethods.IsZoomed(foregroundWindow);
        // A maximized window on the dock's monitor both vacates the reservation and,
        // below, hides the dock at once — the row collapses while Windows re-fills
        // the maximized window over the freed strip, as one motion.
        var maximizedOnDockMonitor = foregroundZoomed
            && DockNativeMethods.MonitorFromWindow(
                foregroundWindow,
                DockNativeMethods.MonitorDefaultToNearest) == monitor.MonitorHandle;
        // A maximized window filling the monitor (for example while the taskbar auto-hides)
        // is still working space the handle may reveal the dock over.
        var trueFullScreen = DockVacateRules.IsTrueFullScreen(fullScreen, foregroundZoomed);
        _trueFullScreenForeground = trueFullScreen;
        var shouldVacate = DockVacateRules.ShouldReleaseForMaximized(
            _state.VisibilityMode,
            _state.ReleaseWhenMaximized,
            maximizedOnDockMonitor);
        if (shouldVacate != _vacatedForMaximized)
        {
            _vacatedForMaximized = shouldVacate;
            ApplyState();
        }

        if (!UsesSmartHide)
        {
            _appBar.SetFullScreenDetected(trueFullScreen);
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
        // A visible native taskbar below the dock owns the bottom edge: pointing at it
        // must not bring up the dock; see DockRevealRules for what reveals it then.
        var bottomEdgeFree = MonitorCatalog.TryGetWorkingArea(monitor.MonitorHandle) is not { } liveWorkArea
            || DockRevealRules.IsBottomEdgeFree(liveWorkArea, monitor.Bounds);
        var edgeReveal = DockRevealRules.EdgeReveal(_state.RevealMode, bottomEdgeFree);
        var handleAllowed = DockRevealRules.UsesHandle(_state.RevealMode);
        if (handleAllowed != _handleAllowed)
        {
            _handleAllowed = handleAllowed;
            SyncHandle();
        }

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
            // This input is only built while smart-hiding, where a maximized window on
            // this monitor is guaranteed to take over the strip the moment the
            // reservation releases — count it as overlap before its animated resize
            // has covered the pixels, or the collapse would lag the maximize click.
            WindowOverlapsDock: overlaps || maximizedOnDockMonitor,
            ForegroundIsFullScreen: trueFullScreen,
            PointerInRevealZone: edgeReveal != DockEdgeReveal.None
                && DockRevealRules.IsInRevealZone(
                    cursor.X,
                    cursor.Y,
                    // Over an occupied edge only the last rows count: treat the dock as
                    // flush with the screen edge so the zone shrinks to the thin strip.
                    edgeReveal == DockEdgeReveal.EdgeStrip
                        ? bounds with { Bottom = monitor.Bounds.Bottom }
                        : bounds,
                    monitor.Bounds,
                    revealDepth),
            PointerOverDock: pointerOverDock,
            InteractionActive: _menuOpen || _dragging || _externalDragActive || DateTimeOffset.UtcNow < _attentionRevealUntil,
            OverlapIsMaximizedWindow: maximizedOnDockMonitor);
        if (_trueFullScreenForeground && _morph.IsCollapsing)
        {
            // Nothing may animate over a truly full-screen app: land the pending
            // collapse immediately instead of letting the flight finish.
            CompleteSmartHideImmediately();
        }

        if (_autoHide.Update(input, DateTimeOffset.UtcNow))
        {
            if (_autoHide.IsShown)
            {
                // Revealing beside the handle: the row grows out of the handle, so it
                // must already sit at the expanded rect when it appears — place first,
                // then show, or the morph would start from the strip geometry.
                if (pointerOverHandle)
                {
                    ApplyHandleExpansion(monitor, handleRect);
                }
                ShowDock(fromHandle: pointerOverHandle);
            }
            else
            {
                if (_openMenu is { } menu)
                {
                    menu.IsOpen = false;
                }
                _hiddenBySmartHide = true;
                BeginSmartHide();
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
            if (_handleExpanded && !_morph.IsCollapsing
                && (!_autoHide.IsShown || (pointerLeftDock && !overlaps)))
            {
                // A collapse flight owns the expanded placement until it completes;
                // this branch only serves the still-shown strip jump.
                CollapseHandleExpansion();
            }
        }
        SyncHandle();
    }

    private bool YieldToSystemMenu(bool active)
    {
        if (active)
        {
            _yieldReleaseTimer.Stop();
            _suppressedBySystemMenu = true;
            _foregroundTimer.Interval = SmartHideCheckInterval;
            if (IsVisible)
            {
                if (MorphEnabled && !_handleExpanded && !_morph.IsCollapsing)
                {
                    // Fade out in place; OnCollapseMorphCompleted hides the window.
                    _morph.BeginYield(BottomEdgeCentre(_appBar?.Bounds ?? default), Motion);
                    _yieldHideTimer.Interval = DockMorphMotion.YieldDuration(Motion) + TimeSpan.FromMilliseconds(80);
                    _yieldHideTimer.Start();
                }
                else
                {
                    CompleteSmartHideImmediately();
                }
            }
            _handleWindow?.SetVisible(false);
            return true;
        }
        if (_suppressedBySystemMenu && !_yieldReleaseTimer.IsEnabled)
        {
            if (TaskbarCoversDock())
            {
                _yieldReleaseDeadline = Environment.TickCount64 + (long)YieldReleaseLimit.TotalMilliseconds;
                _yieldReleaseTimer.Start();
            }
            else
            {
                ReleaseSystemMenuYield();
            }
        }
        return false;
    }

    private void CheckYieldRelease()
    {
        if (_closing || !_suppressedBySystemMenu)
        {
            _yieldReleaseTimer.Stop();
            return;
        }
        if (ShellSurfaceNativeMethods.IsSystemMenuForeground())
        {
            // Reopened before the taskbar left: stay aside.
            _yieldReleaseTimer.Stop();
            return;
        }
        if (TaskbarCoversDock() && Environment.TickCount64 < _yieldReleaseDeadline)
        {
            return;
        }
        _yieldReleaseTimer.Stop();
        ReleaseSystemMenuYield();
    }

    private void ReleaseSystemMenuYield()
    {
        _yieldHideTimer.Stop();
        _suppressedBySystemMenu = false;
        if (!UsesSmartHide || _autoHide.IsShown) ShowDock();
        SyncHandle();
    }

    // An auto-hidden taskbar still sliding back down overlaps the dock's strip; a
    // taskbar that is always shown sits beside it and never delays the return.
    private bool TaskbarCoversDock()
    {
        if (_appBar is not { } appBar) return false;
        var tray = TaskbarNativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (tray == 0
            || !DockNativeMethods.IsWindowVisible(tray)
            || !DockNativeMethods.GetWindowRect(tray, out var rect))
        {
            return false;
        }
        return new PixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom).Intersects(appBar.Bounds);
    }

    private void ShowDock(bool fromHandle = false)
    {
        if (_closing || _suppressedBySystemMenu)
        {
            return;
        }

        _hiddenBySmartHide = false;
        if (_morph.IsCollapsing)
        {
            // The reveal interrupted a collapse flight: undo the placement the flight
            // had taken and grow back from wherever the shrink had reached. Replacing
            // the animation cancels the deferred hide.
            var placement = _handleExpanded ? _expandedRect : (_appBar?.Bounds ?? default);
            _appBar?.SetFloatingOverride(_handleExpanded ? _expandedRect : null);
            _morph.BeginExpand(MorphCentre(placement), Motion);
        }
        if (!IsVisible)
        {
            if (MorphEnabled && !_morph.IsCollapsing)
            {
                var placement = _handleExpanded ? _expandedRect : (_appBar?.Bounds ?? default);
                if (fromHandle)
                {
                    // Fresh reveal beside the handle: grow out of it.
                    _morph.BeginExpand(MorphCentre(placement), Motion);
                }
                else
                {
                    // Reappear in place with a fade and a slight grow from the row's
                    // own bottom edge — not a handle morph: the pointer summoned the
                    // dock where it is, and a pure fade reads as an abrupt pop.
                    _morph.BeginReappear(BottomEdgeCentre(placement), Motion);
                }
            }
            Show();
        }
        if (_refreshPending)
        {
            _refreshPending = false;
            RefreshWindows();
        }
        SyncHandle();
    }

    // True when the morph may run at all: the system "show animations" switch and the
    // style's motion level both gate it, exactly like the hover zoom.
    private bool MorphEnabled => SystemParameters.ClientAreaAnimation && Motion != MotionLevel.Off;

    // The smart-hide transition: instead of vanishing, the row shrinks into the
    // handle. A full-screen foreground, a missing handle or disabled animation keep
    // the instant hide. While the flight runs the window keeps its (possibly
    // widened) placement; the completion restores the strip placement and hides.
    private void BeginSmartHide()
    {
        if (!MorphEnabled
            || _trueFullScreenForeground
            || _handleWindow is not { IsDragActive: false } handle)
        {
            _morph.Snap();
            HideFromApp();
            SyncHandle();
            return;
        }

        var handleRect = handle.CurrentRect;
        PixelRect placement;
        if (_handleExpanded)
        {
            // Beside the handle already: shrink in place, keeping the override so
            // AppBar notifications cannot pull the row back to the strip mid-flight.
            placement = _expandedRect;
        }
        else if (!_handleAllowed)
        {
            // No handle to land in: the row shrinks in place at the strip.
            placement = _appBar?.Bounds ?? default;
        }
        else
        {
            // From the strip: widen the window over the whole travel path first, or
            // the shrinking row would clip at the strip's edge far from the handle.
            placement = DockMorphMotion.TravelBounds(_appBar?.Bounds ?? default, handleRect);
            _appBar?.SetFloatingOverride(placement);
        }

        _morph.BeginCollapse(MorphCentre(placement), Motion);
    }

    private void CompleteSmartHideImmediately()
    {
        _morph.Snap();
        if (_handleExpanded)
        {
            CollapseHandleExpansion();
        }
        else
        {
            _appBar?.SetFloatingOverride(null);
        }
        HideFromApp();
        SyncHandle();
    }

    private void OnCollapseMorphCompleted()
    {
        if (_closing)
        {
            return;
        }

        // The row has fully disappeared into the handle: restore the strip placement
        // invisibly, hide the window, and let the handle bounce once to land it.
        if (_handleExpanded)
        {
            CollapseHandleExpansion();
        }
        else
        {
            _appBar?.SetFloatingOverride(null);
        }
        HideFromApp();
        if (!_trueFullScreenForeground && _handleAllowed && !_suppressedBySystemMenu)
        {
            _handleWindow?.Pulse();
        }
        SyncHandle();
    }

    // The point a morph converges on, in the surface's own DIP coordinates for the
    // given placement: the handle's centre while the row travels, the surface's own
    // centre when Reduced shrinks in place or there is no handle to land in. The point may lie outside the surface —
    // a ScaleTransform centred there pulls the shrinking row toward it.
    private Point MorphCentre(PixelRect placement)
    {
        var scale = _appBar?.Monitor is { } monitor ? monitor.Dpi / 96d : 1d;
        if (_handleWindow is { } handle && _handleAllowed && DockMorphMotion.IsTravelEnabled(Motion))
        {
            var handleRect = handle.CurrentRect;
            var (x, y) = DockMorphMotion.ToElementPoint(
                (handleRect.Left + handleRect.Right) / 2,
                (handleRect.Top + handleRect.Bottom) / 2,
                placement,
                scale);
            return new Point(x, y);
        }

        return new Point(placement.Width / 2d / scale, placement.Height / 2d / scale);
    }

    // The anchor a reappear grows from: the middle of the row's bottom edge, so the
    // row emerges upward from its own resting footprint.
    private Point BottomEdgeCentre(PixelRect placement)
    {
        var scale = _appBar?.Monitor is { } monitor ? monitor.Dpi / 96d : 1d;
        return new Point(placement.Width / 2d / scale, placement.Height / scale);
    }

    // The handle stands in for the dock while it is smart-hidden: it appears then and
    // disappears with the dock — including while nothing may show over a true
    // full-screen app, and stays away when the user reveals the dock from the edge
    // alone. While the dock is expanded beside it the handle stays visible
    // as the row's end cap, so the pointer has somewhere to rest, and a drag in
    // progress can never lose the window to a visibility change.
    private void SyncHandle()
    {
        if (_handleWindow is null || _closing)
        {
            return;
        }

        _handleWindow.SetVisible(
            !_trueFullScreenForeground && !_suppressedBySystemMenu
            && ((_hiddenBySmartHide && _handleAllowed) || _handleExpanded || _handleWindow.IsDragActive));
    }

    private void CreateHandleWindow()
    {
        _handleWindow = new DockHandleWindow();
        _handleWindow.DragStarted += () =>
        {
            if (_morph.IsCollapsing)
            {
                // Dragging the handle mid-collapse: finish instantly — a row still
                // flying into a handle the user is moving would fight the drag.
                CompleteSmartHideImmediately();
            }
            else if (_handleExpanded)
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
        _handleWindow.FileDragEntered += () =>
        {
            if (!_closing)
            {
                UpdateForForeground();
            }
        };
        _handleWindow.FilesDropped += files =>
        {
            if (!_closing)
            {
                _ = PinFilesAsync(files);
            }
        };
        _handleWindow.ShellRecovered += () => ShellRecovered?.Invoke();
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
        // Instantly drop any morph in flight: this collapse moves a still-visible
        // dock straight to the strip, and a half-grown row would otherwise land
        // mid-scale. A smart-hide collapse flight never reaches here — its placement
        // is released by the animation completion or CompleteSmartHideImmediately.
        _morph.Snap();
        _handleExpanded = false;
        _expandedRect = default;
        _appBar?.SetFloatingOverride(null);
    }

    private bool ForegroundWindowRect(out PixelRect bounds, out nint window)
    {
        bounds = default;
        // While the top bar's search session holds the foreground, keep evaluating the
        // window it was borrowed from: focusing the box must not read as the desktop
        // coming back and show a dock that was hidden for a maximized window in front.
        var (searchHolder, searchBorrowed) = TopBarWindow.SearchForeground;
        var currentForeground = DockNativeMethods.GetForegroundWindow();
        window = DockForegroundRules.EffectiveForeground(
            ResolvePanelForeground?.Invoke(currentForeground) ?? currentForeground,
            searchHolder,
            searchBorrowed);
        if (window == 0 || window == _handle || window == (_openMenu?.PopupHandle ?? 0) || DockNativeMethods.IsIconic(window))
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

    private static string BuildStructureKey(IReadOnlyList<DockItem> items)
    {
        var builder = new StringBuilder(items.Count * 24);
        foreach (var item in items)
        {
            builder.Append(item.Key)
                .Append(':')
                .Append(item.IsPinned ? 'P' : '-')
                .Append('|');
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
