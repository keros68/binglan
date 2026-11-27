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
    private const string RunningPinDragFormat = "BingLan.DockRunningPin";
    private const string RunningDragFormat = "BingLan.DockRunning";
    private const double CommandGlyphCanvasSize = 24;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const long WsCaption = 0x00C00000L;
    private static readonly TimeSpan SmartHideCheckInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan FullScreenCheckInterval = TimeSpan.FromMilliseconds(500);
    /// <summary>How much the dock animates; part of the desktop style.</summary>
    internal static MotionLevel Motion { get; set; } = MotionLevel.Standard;
    private static readonly TimeSpan ReservationRetryInterval = TimeSpan.FromSeconds(30);
    private readonly DockState _state;
    private readonly DockRuntime _runtime;
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
    private readonly Dictionary<string, (DockVisualPinState Source, DockAppVisual Visual)> _visuals =
        new(StringComparer.OrdinalIgnoreCase);
    private DockAppBarController? _appBar;
    private IReadOnlyList<DockItem> _items = [];
    private IReadOnlyList<string> _runningOrder = [];
    private readonly List<DockButtonParts> _itemButtons = [];
    private readonly Dictionary<string, DateTimeOffset> _lastBounce = [];
    private bool _bounceRequested;
    private string _lastSignature = string.Empty;
    private string _lastStructureKey = string.Empty;
    private (bool Start, bool Launcher, bool Desktop, bool TaskView, bool QuickSettings) _renderedCommandButtons;
    private nint _handle;
    private bool _closing;
    private bool _isChangingVisibilityByApp;
    private bool _refreshRunning;
    private bool _refreshQueued;
    private int _sharedItemsVersion;
    private bool _menuOpen;
    private bool _revealPending;
    private bool _suppressedBySystemMenu;
    private bool _hiddenBySmartHide;
    private bool _refreshPending;
    private bool _reservationFailureReported;
    private bool _vacatedForMaximized;
    private DockHandleWindow? _handleWindow;
    private bool _handleAllowed = true;
    private bool _pointerOverHandle;
    private DateTimeOffset _handleRevealUntil = DateTimeOffset.MinValue;
    private readonly DockMorphAnimator _morph;
    private readonly DockGlassSurface _glass;
    private bool _trueFullScreenForeground;
    private NonActivatingPopupMenu? _openMenu;
    private DateTimeOffset _lastReservationAttempt;
    private readonly DockAttention _attention = new();
    private readonly DockToggleCooldown _toggleCooldown;
    private readonly DockLaunchCooldown _launchCooldown;
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
    private DockButtonParts? _pinDragIndicatorParts;
    private bool _pinDragIndicatorAfter;
    private DockEdge _renderedEdge = DockEdge.Bottom;
    private bool _externalDragActive;
    private string[]? _cachedDragPaths;
    private Task<IReadOnlyList<DroppedPathInfo>>? _cachedDragInfoTask;

    // Pre-allocated hover magnification slots (rebuilt only when dock items change) so
    // pointer movement allocates nothing; timer stops immediately on MouseLeave/hide.
    /// <summary>
    /// Steps once per rendered frame so the magnification eases in sync with the display
    /// instead of drifting against a free-running timer.
    /// </summary>
    private sealed class RenderFrameTicker(Action step)
    {
        public bool IsEnabled { get; private set; }
        public TimeSpan LastFrame { get; private set; } = TimeSpan.MinValue;

        public void Start()
        {
            if (IsEnabled) return;
            IsEnabled = true;
            LastFrame = TimeSpan.MinValue;
            CompositionTarget.Rendering += OnRendering;
        }

        public void Stop()
        {
            if (!IsEnabled) return;
            IsEnabled = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            // Rendering can fire more than once for a frame; step only on a new frame time.
            if (e is System.Windows.Media.RenderingEventArgs args)
            {
                if (args.RenderingTime == LastFrame) return;
                LastFrame = args.RenderingTime;
            }
            step();
        }
    }

    private sealed class MagnificationSlot(Button button, ScaleTransform scale, TranslateTransform translate)
    {
        public Button Button { get; } = button;
        public ScaleTransform Scale { get; } = scale;
        public TranslateTransform Translate { get; } = translate;
        public double CurrentScale = 1d;
        public double TargetScale = 1d;
        public double CurrentLiftX;
        public double TargetLiftX;
        public double CurrentLiftY;
        public double TargetLiftY;
    }

    private readonly List<MagnificationSlot> _magSlots = [];
    private readonly RenderFrameTicker _magTimer;
    private readonly DockFrameDiagnostics? _hoverFrames;
    private readonly System.Diagnostics.Stopwatch _magClock = new();
    private long _magLastTicks;
    private (double CursorAxis, double MaxScale)? _magPendingTarget;
    private double _topOverflowDip;
    private bool _magReturnAnimating;
    private bool _magRegionExpanded;
    private bool _hasAppliedHitRegion;
    private PixelRect? _appliedHitRegion;
    private int _magReturnGeneration;

    private static readonly TimeSpan BackdropSampleInterval = TimeSpan.FromMilliseconds(1500);
    private readonly DesktopStyleState _style;
    private readonly DispatcherTimer _backdropTimer;
    private int _backdropSampleInFlight;
    private ColorContrastRules.SrgbColor? _sampledBackdrop;
    private bool? _lastAdaptiveDarkInk;

    internal IceBlueDockWindow(DockState state, DesktopStyleState? style = null, DockRuntime? runtime = null)
    {
        _state = state;
        _runtime = runtime ?? new DockRuntime();
        _toggleCooldown = _runtime.ToggleCooldown;
        _launchCooldown = _runtime.LaunchCooldown;
        _style = style ?? new DesktopStyleState();
        InitializeComponent();
        _morph = new DockMorphAnimator(DockSurface);
        _glass = new DockGlassSurface(
            this,
            DockSurface,
            "DockSurfaceBrush",
            "DockSurfaceBorderBrush",
            fullTintOpacity: !_state.FollowCardLook);
        _glass.SetFrostedGlass(_state.FrostedGlass);
        _morph.StateChanged += () =>
        {
            _glass.TrackAnimation(_morph.ActiveKind != DockMorphKind.None);
            SyncMagnificationHitRegion();
        };
        _morph.CollapseCompleted += OnCollapseMorphCompleted;
        _watcher = new WindowEventWatcher(Dispatcher);
        _watcher.SnapshotInvalidated += RefreshWindows;
        _runtime.BackgroundPinsChanged += RefreshWindows;
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
        _backdropTimer = new DispatcherTimer(
            BackdropSampleInterval,
            DispatcherPriority.Background,
            (_, _) => ScheduleBackdropSample(),
            Dispatcher);
        _backdropTimer.Stop();
        IsVisibleChanged += (_, _) => UpdateBackdropSampling(immediate: IsVisible);
        AccessibilityThemeManager.HighContrastChanged += OnHighContrastChanged;
        _magTimer = new RenderFrameTicker(StepMagnification);
        if (DockFrameDiagnostics.CanStart)
        {
            _hoverFrames = new DockFrameDiagnostics(ReadHoverScale);
        }
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => SyncMagnificationHitRegion();
        Closing += OnClosing;
        DockSurface.DragOver += OnSurfaceDragOver;
        DockSurface.DragLeave += OnSurfaceDragLeave;
        DockSurface.Drop += OnDropOnSurface;
        DockSurface.PreviewMouseMove += OnDockSurfacePreviewMouseMove;
        DockSurface.MouseLeave += OnDockSurfaceMouseLeave;
        // When the dock overflows, the wheel scrolls along the active layout axis.
        ItemScroller.PreviewMouseWheel += (sender, e) =>
        {
            if (IsVertical)
            {
                if (ItemScroller.ScrollableHeight <= 0)
                {
                    return;
                }
                ItemScroller.ScrollToVerticalOffset(
                    ItemScroller.VerticalOffset - Math.Sign(e.Delta) * ItemCellStep());
                e.Handled = true;
                return;
            }
            if (ItemScroller.ScrollableWidth <= 0)
            {
                return;
            }
            ItemScroller.ScrollToHorizontalOffset(
                ItemScroller.HorizontalOffset - Math.Sign(e.Delta) * ItemCellStep());
            e.Handled = true;
        };
    }

    private DockEdge CurrentEdge => DockEdgeRules.Normalize(ScreenSelection?.Preferences?.DockEdge ?? _state.Edge);
    private DockVisibilityMode CurrentVisibilityMode => ScreenSelection?.Preferences?.DockVisibilityMode ?? _state.VisibilityMode;

    internal int? GetBottomDockTopForMonitor(MonitorSnapshot monitor) =>
        !_closing && CurrentEdge == DockEdge.Bottom && _appBar is { Bounds.HasArea: true } bar
            && bar.Monitor?.MonitorHandle == monitor.MonitorHandle
            ? bar.Bounds.Top - (int)Math.Round(_topOverflowDip * monitor.Dpi / 96d)
            : null;
    private bool IsVertical => DockEdgeRules.IsVertical(CurrentEdge);

    private static PlacementMode InwardMenuPlacement(DockEdge edge) => DockEdgeRules.Normalize(edge) switch
    {
        DockEdge.Left => PlacementMode.Right,
        DockEdge.Right => PlacementMode.Left,
        _ => PlacementMode.Top
    };

    private void ApplyToolTipPlacement(FrameworkElement element)
    {
        var edge = CurrentEdge;
        ToolTipService.SetPlacement(element, InwardMenuPlacement(edge));
        ToolTipService.SetHorizontalOffset(element, edge switch
        {
            DockEdge.Left => 6d,
            DockEdge.Right => -6d,
            _ => 0d
        });
        ToolTipService.SetVerticalOffset(element, edge == DockEdge.Bottom ? -6d : 0d);
    }

    private double ItemCellStep() => DockLayoutMetrics.ItemCell(_effectiveIconSize);

    internal event Action? DockStateChanged;
    internal event Action? SharedItemsChanged;

    internal SurfaceMonitorSelection? ScreenSelection { get; set; }
    internal Func<ScreenSurfacePreferences?>? GetOrCreateScreenPreferences { get; init; }

    internal event Action? SettingsRequested;
    internal event Action? LauncherRequested;
    internal bool LauncherEnabled { get; set; } = true;

    internal Func<IReadOnlyList<(Guid Id, string Title)>>? ReadFileBoxes { get; init; }
    internal Func<Guid, string, Task<string>>? SendToFileBox { get; init; }
    internal Func<nint, nint>? ResolvePanelForeground { get; init; }
    internal Func<MonitorSnapshot, int?>? ResolveTopBarReservedBottomPx { get; init; }
    internal Func<Task<bool>>? OpenTrayOverflowRequested { get; init; }

    internal bool HasAttention => _attention.Count > 0;

    internal event Action? HideRequested;

    internal event Action? ShellRecovered;

    internal event Action? ExplorerNotified;

    internal event Action? ShowDesktopTriggered;

    internal event Action<string>? CommandFailed;

    internal IEnumerable<nint> ActiveDesktopHandles()
    {
        if (_closing || _trueFullScreenForeground || _appBar is { IsFullScreenDetected: true } || _suppressedBySystemMenu)
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
        if (_closing || _trueFullScreenForeground || _appBar is { IsFullScreenDetected: true } || _suppressedBySystemMenu)
        {
            return;
        }
        if (_appBar?.Monitor is { } monitor
            && (Taskbar.TaskbarCornerReveal.IsTaskbarExpandedOnMonitor(monitor.Bounds) || TaskbarCoversDock()))
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
        StopMagnification(animateReturn: false);
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

        var monitor = ScreenSelection is { } selection
            ? MonitorCatalog.GetAll().FirstOrDefault(candidate =>
                candidate.MonitorHandle == selection.Monitor.MonitorHandle
                && string.Equals(candidate.DeviceName, selection.Monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
            : SurfaceMonitorTargetResolver.Resolve(_state.MonitorKey);
        if (monitor is null)
        {
            Close();
            return;
        }

        var edge = CurrentEdge;
        _glass.SetFrostedGlass(_state.FrostedGlass);
        _glass.SetFullTintOpacity(!_state.FollowCardLook);
        _handleWindow?.SetFrostedGlass(_state.FrostedGlass);
        // A window maximized on the dock's monitor may take over the reserved strip;
        // while it does, the dock follows smart-hide rules instead of holding the space.
        var reserveWorkArea = CurrentVisibilityMode == DockVisibilityMode.ReserveWorkArea
            && !_vacatedForMaximized;
        _lastReservationAttempt = DateTimeOffset.UtcNow;
        UpdateEffectiveIconSize(monitor, _items);
        var thicknessDip = DockLayoutMetrics.Thickness(_state.IconSize);
        var overflowDip = DockMagnificationRules.RequiredTopOverflowDip(
            _state.HoverMagnification,
            _state.HoverMagnificationScale,
            _effectiveIconSize,
            Motion,
            SystemParameters.ClientAreaAnimation);
        var previousOverflowDip = _topOverflowDip;
        ApplyOverflowLayout(thicknessDip, overflowDip);
        if (_renderedEdge != edge)
        {
            _lastSignature = string.Empty;
            _lastStructureKey = string.Empty;
            ApplyItems(_items);
        }
        if (DockMagnificationRules.ShouldResetSlotsOnOverflowChange(previousOverflowDip, overflowDip))
        {
            StopMagnification(animateReturn: false);
        }
        var topBarBottomPx = DockEdgeRules.IsVertical(edge)
            ? ResolveTopBarReservedBottomPx?.Invoke(monitor)
            : null;
        _appBar.Apply(
            monitor,
            reserveWorkArea,
            ContentLengthDip(),
            thicknessDip,
            _state.BottomGapDip,
            overflowDip,
            edge,
            topBarBottomPx);
        SyncMagnificationHitRegion();
        RefreshAdaptiveAppearance();
        if (!reserveWorkArea || _appBar.IsReserved)
        {
            _reservationFailureReported = false;
        }
        else if (!_reservationFailureReported)
        {
            _reservationFailureReported = true;
            var edgeName = edge switch
            {
                DockEdge.Left => "左侧",
                DockEdge.Right => "右侧",
                _ => "底部"
            };
            CommandFailed?.Invoke($"无法保留屏幕{edgeName}空间，Dock 暂时按智能隐藏显示，稍后自动重试。");
        }
        if (!UsesSmartHide)
        {
            _autoHide = new DockAutoHideState();
            _handleRevealUntil = DateTimeOffset.MinValue;
            _pointerOverHandle = false;
            if (_morph.IsCollapsing)
            {
                // A flight whose completion would hide the dock must not outlive the
                // switch to a mode that keeps the dock shown.
                CompleteSmartHideImmediately();
            }
            _appBar.SetFloatingOverride(null);
            ShowDock();
        }
        _foregroundTimer.Interval = UsesSmartHide ? SmartHideCheckInterval : FullScreenCheckInterval;
        _foregroundTimer.Start();
        UpdateHandleBounds();
    }

    // A reserved dock whose AppBar registration failed would float over windows, so it
    // falls back to smart hide until the reservation succeeds (without blinking during
    // Explorer restart recovery).
    private bool UsesSmartHide =>
        CurrentVisibilityMode == DockVisibilityMode.SmartHide
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

    internal void RefreshSharedItems()
    {
        _sharedItemsVersion++;
        RefreshWindows();
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
        _appBar.ExplorerNotified += () => ExplorerNotified?.Invoke();
        _appBar.FullScreenAppNotified += () =>
        {
            if (!_closing)
            {
                UpdateForForeground();
            }
        };
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
        if ((int)wParam == HshellFlash && !changed)
        {
            // Already marked: the buttons are current, so bounce now (unless hidden under full-screen).
            if (!_trueFullScreenForeground)
            {
                BounceFlashedItems();
            }
        }
        if (changed)
        {
            if ((int)wParam == HshellFlash
                && DockVacateRules.ShouldRevealOnAttention(
                    UsesSmartHide,
                    _state.RevealOnAttention,
                    _trueFullScreenForeground,
                    _closing))
            {
                // A hidden dock shows itself briefly so the mark is seen.
                _attentionRevealUntil = DateTimeOffset.UtcNow + AttentionReveal;
                ShowDock();
            }
            RefreshAttentionBadges();
        }
        return 0;
    }

    private void RefreshAttentionBadges()
    {
        _lastSignature = string.Empty;
        RefreshWindows();
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

        // A chat app keeps flashing while it is marked, and the mark then does not change;
        // each accepted flash still asks for a bounce (the per-app interval limits it).
        _bounceRequested = true;
        var title = new StringBuilder(256);
        DockNativeMethods.GetWindowTextW(window, title, title.Capacity);
        return _attention.Flashed(
            window,
            AttentionFlashRules.EffectiveProcessId(rawProcessId, executablePath),
            UnreadBadgeRules.ParseCount(title.ToString()),
            executablePath);
    }

    private bool ClearActivatedAttention(nint window)
    {
        var active = window != 0 ? window : DockNativeMethods.GetForegroundWindow();
        if (active == 0)
        {
            return false;
        }

        DockNativeMethods.GetWindowThreadProcessId(active, out var rawProcessId);
        var executablePath = rawProcessId != 0 ? WindowCatalog.ExecutablePathOf(rawProcessId) : null;
        var effectivePid = rawProcessId != 0
            ? AttentionFlashRules.EffectiveProcessId(rawProcessId, executablePath)
            : 0;
        var removed = _attention.Cleared(active, effectivePid);

        // If a multi-process app (such as WeChat 4.x) flashed via a child helper process,
        // resolve the instance tree off the UI thread and clear only that instance's helper mark
        // without blocking WndProc or clearing a separate dual-opened instance.
        if (_attention.Count > 0
            && effectivePid != 0
            && _attention.HasMarkedExecutableMatching(executablePath))
        {
            var activeHandle = active;
            var activePid = effectivePid;
            var activeExe = executablePath;
            _ = Task.Run(() => WindowCommandService.ResolveInstanceProcessIds(activePid, activeExe))
                .ContinueWith(
                    task =>
                    {
                        if (!task.IsCompletedSuccessfully || _closing)
                        {
                            return;
                        }

                        var instancePids = task.Result;
                        if (instancePids.Count == 0)
                        {
                            return;
                        }

                        _ = Dispatcher.BeginInvoke(() =>
                        {
                            if (_closing || _attention.Count == 0)
                            {
                                return;
                            }

                            if (_attention.Cleared(activeHandle, instancePids))
                            {
                                RefreshAttentionBadges();
                            }
                        });
                    },
                    TaskScheduler.Default);
        }

        return removed;
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
        _backdropTimer.Stop();
        AccessibilityThemeManager.HighContrastChanged -= OnHighContrastChanged;
        _hoverFrames?.Finish(completed: false);
        _magTimer.Stop();
        _magClock.Stop();
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        _runtime.BackgroundPinsChanged -= RefreshWindows;
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
                var sharedItemsVersion = _sharedItemsVersion;
                var pinList = _state.PinnedApps;
                var pinned = pinList.ToArray();
                var hidden = _state.HiddenApps.ToArray();
                var snapshot = await _runtime.CaptureAsync();
                if (_closing) return;
                if (sharedItemsVersion != _sharedItemsVersion || !ReferenceEquals(pinList, _state.PinnedApps))
                {
                    _refreshQueued = true;
                    continue;
                }
                IReadOnlyList<DockItem> items;
                var showRunningPinsSeparately = _state.ShowRunningPinsSeparately;
                using (UiDiagnostics.Measure("Dock 窗口分组"))
                {
                    items = DockItemComposer.Compose(
                        pinned,
                        WindowGrouping.Group(snapshot),
                        hidden,
                        _runtime.GetBackgroundPins(pinned),
                        _runtime.RunningTrayExecutables,
                        _runningOrder,
                        showRunningPinsSeparately);
                    _attention.RemoveRange(_attention.MarkedWindows
                        .Where(handle => !DockNativeMethods.IsWindow(handle))
                        .ToArray());
                }
                foreach (var stale in _visuals.Keys.Except(items.Select(item => item.Key)).ToArray())
                {
                    _visuals.Remove(stale);
                }
                _runtime.RetainVisuals(items.Select(item => item.Key));
                var missing = items.Select(item => (Item: item, Source: DockVisualPinState.Of(item)))
                    .Where(entry => !_visuals.TryGetValue(entry.Item.Key, out var cached) || cached.Source != entry.Source)
                    .ToArray();
                if (missing.Length > 0)
                {
                    var resolved = await Task.WhenAll(missing.Select(async entry =>
                        (entry.Item.Key, entry.Source, Visual: await _runtime.ResolveVisualAsync(entry.Item))));
                    if (sharedItemsVersion != _sharedItemsVersion
                        || !ReferenceEquals(pinList, _state.PinnedApps)
                        || showRunningPinsSeparately != _state.ShowRunningPinsSeparately)
                    {
                        _refreshQueued = true;
                        continue;
                    }
                    foreach (var (key, source, visual) in resolved)
                    {
                        _visuals[key] = (source, visual);
                    }
                    _lastSignature = string.Empty;
                }

                if (_closing)
                {
                    return;
                }
                _runningOrder = items.Where(item => item.IsInRunningSection).Select(item => item.RunningOrderKey).ToArray();
                ApplyItems(items);
                BounceFlashedItems();
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
        var edge = CurrentEdge;
        var rowSettings = $"{_effectiveIconSize}|{commands}|{_dockOverflowing}|{edge}";
        var signature = $"{rowSettings}|{BuildSignature(items)}|{string.Join(",", items.Select(item => $"{(_attention.Wants(item) ? '!' : '-')}{_attention.UnreadCount(item)}"))}";
        if (string.Equals(signature, _lastSignature, StringComparison.Ordinal) && _itemButtons.Count == items.Count)
        {
            // A fresh snapshot may change launch arguments or replace a pin object without changing the row.
            _items = items;
            for (var index = 0; index < items.Count; index++)
            {
                _itemButtons[index].Button.Item = items[index];
            }
            return;
        }

        var countChanged = items.Count != _items.Count
            || HasRunningDivider(items) != HasRunningDivider(_items)
            || _renderedCommandButtons != commands;
        _renderedCommandButtons = commands;
        _renderedEdge = edge;
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
        ClearPinDragIndicator();
        StopMagnification(animateReturn: false);
        _lastStructureKey = structureKey;
        _itemButtons.Clear();
        _magSlots.Clear();
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
        if (commands.TaskView)
        {
            ItemPanel.Children.Add(BuildTaskViewButton());
        }
        if (commands.QuickSettings)
        {
            ItemPanel.Children.Add(BuildQuickSettingsButton());
        }
        if (LeadingButtonCount > 0 && items.Count > 0)
        {
            ItemPanel.Children.Add(BuildDivider());
        }
        for (var index = 0; index < items.Count; index++)
        {
            if (index > 0 && !items[index - 1].IsInRunningSection && items[index].IsInRunningSection)
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
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "DockRunningDotBrush");
        var button = new Button
        {
            Content = glyph,
            Width = _effectiveIconSize + 16,
            Height = _effectiveIconSize + 16,
            Margin = IsVertical ? new Thickness(0, 4, 0, 4) : new Thickness(4, 0, 4, 0),
            Style = (Style)Resources["DockItemButtonStyle"],
            ToolTip = IsVertical ? "应用较多，点击查看全部应用；也可用滚轮纵向滚动" : "应用较多，点击查看全部应用；也可用滚轮横向滚动",
            Focusable = true
        };
        ApplyToolTipPlacement(button);
        AutomationProperties.SetName(button, "查看全部应用");
        button.Click += (_, _) =>
        {
            var menu = CreateMenu(button, InwardMenuPlacement(CurrentEdge));
            foreach (var item in _items)
            {
                var icon = _visuals.TryGetValue(item.Key, out var visual) ? visual.Visual.Icon : null;
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
        var button = BuildDockCommandButton(glyph, "打开冰蓝总览卡", "打开冰蓝总览卡");
        button.Click += (_, _) => LauncherRequested?.Invoke();
        return button;
    }

    private Button BuildTaskViewButton()
    {
        var glyph = CreateDockCommandOutline("M5,8 H19 A2,2 0 0 1 21,10 V18 A2,2 0 0 1 19,20 H5 A2,2 0 0 1 3,18 V10 A2,2 0 0 1 5,8 Z M7,4 H17 M8,14 H16");
        var button = BuildDockCommandButton(glyph, "打开任务视图", "任务视图（Win+Tab）");
        AutomationProperties.SetHelpText(button, "选择打开的窗口或切换虚拟桌面");
        button.Click += async (_, _) =>
        {
            if (await ShellSurfaceService.OpenTaskViewAsync() != ShellSurfaceResult.Opened)
            {
                CommandFailed?.Invoke("无法打开任务视图，请按 Win+Tab。");
            }
        };
        return button;
    }

    private Button BuildQuickSettingsButton()
    {
        var glyph = CreateDockCommandOutline("M3,6 H21 M8,3 V9 M3,12 H21 M16,9 V15 M3,18 H21 M10,15 V21");
        var button = BuildDockCommandButton(glyph, "打开 Windows 快速设置", "快速设置（Win+A）");
        AutomationProperties.SetHelpText(button, "在 Windows 快速设置中调节音量、网络和蓝牙");
        button.Click += async (_, _) =>
        {
            if (!await ShellSurfaceService.OpenQuickSettingsAsync("ms-settings:"))
            {
                CommandFailed?.Invoke("无法打开快速设置，请打开 Windows 设置。");
            }
        };
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
        // Reserve the running-indicator slot on the screen-edge side to align with app icons.
        var edge = CurrentEdge;
        var vertical = DockEdgeRules.IsVertical(edge);
        var content = new StackPanel
        {
            Orientation = vertical ? Orientation.Horizontal : Orientation.Vertical
        };
        if (edge == DockEdge.Left)
        {
            content.Children.Add(new Border { Width = 7, Visibility = Visibility.Hidden });
            content.Children.Add(iconSlot);
        }
        else if (edge == DockEdge.Right)
        {
            content.Children.Add(iconSlot);
            content.Children.Add(new Border { Width = 7, Visibility = Visibility.Hidden });
        }
        else
        {
            content.Children.Add(iconSlot);
            content.Children.Add(new Border { Height = 7, Visibility = Visibility.Hidden });
        }
        var button = new Button
        {
            Content = content,
            Width = _effectiveIconSize + 16,
            Height = _effectiveIconSize + 16,
            Margin = vertical ? new Thickness(0, 4, 0, 4) : new Thickness(4, 0, 4, 0),
            Style = (Style)Resources["DockItemButtonStyle"],
            ToolTip = toolTip,
            Focusable = true
        };
        ApplyToolTipPlacement(button);
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
            : _visuals.TryGetValue(item.Key, out var visual) && visual.Visual.DisplayName is { } name
                ? name
                : item.DisplayName;

    private Border BuildDivider()
    {
        if (IsVertical)
        {
            var horizontalDivider = new Border
            {
                Width = _effectiveIconSize * 0.8,
                Height = 1,
                Margin = new Thickness(0, 6, 0, 6),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            horizontalDivider.SetResourceReference(Border.BackgroundProperty, "DockDividerBrush");
            return horizontalDivider;
        }

        var divider = new Border
        {
            Width = 1,
            Height = _effectiveIconSize * 0.8,
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        divider.SetResourceReference(Border.BackgroundProperty, "DockDividerBrush");
        return divider;
    }

    internal sealed class DockItemButton : Button
    {
        internal DockItem? Item { get; set; }
    }

    private sealed record DockButtonParts(
        DockItemButton Button,
        Image Icon,
        Grid IconHost,
        Border Dot,
        ScaleTransform IconScale)
    {
        public (bool WantsAttention, int Unread) Badge { get; set; }
    }

    private DockButtonParts BuildItem(DockItem item)
    {
        var edge = CurrentEdge;
        var vertical = DockEdgeRules.IsVertical(edge);
        var (originX, originY) = DockEdgeRules.MagnificationOrigin(edge);
        var content = new StackPanel
        {
            Orientation = vertical ? Orientation.Horizontal : Orientation.Vertical
        };
        var icon = new Image
        {
            Width = _effectiveIconSize,
            Height = _effectiveIconSize,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        // The attention bounce moves icon and marks together, apart from the hover zoom.
        var iconHost = new Grid { RenderTransform = new TranslateTransform() };
        iconHost.Children.Add(icon);
        // The attention dot and unread pill live on their own layer that shares the icon's
        // zoom transform, so they follow the icon without changing how the icon itself scales.
        var badgeLayer = new Grid
        {
            Width = _effectiveIconSize,
            Height = _effectiveIconSize,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(originX, originY)
        };
        iconHost.Children.Add(badgeLayer);
        // Running indicator sits on the screen-facing side of the icon (bottom / left / right).
        var dot = new Border
        {
            Width = 4,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Margin = edge switch
            {
                DockEdge.Left => new Thickness(0, 0, 3, 0),
                DockEdge.Right => new Thickness(3, 0, 0, 0),
                _ => new Thickness(0, 3, 0, 0)
            },
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (edge == DockEdge.Left)
        {
            content.Children.Add(dot);
            content.Children.Add(iconHost);
        }
        else
        {
            content.Children.Add(iconHost);
            content.Children.Add(dot);
        }

        var button = new DockItemButton
        {
            Content = content,
            Width = _effectiveIconSize + 16,
            Height = _effectiveIconSize + 16,
            Margin = vertical ? new Thickness(0, 4, 0, 4) : new Thickness(4, 0, 4, 0),
            Style = (Style)Resources["DockItemButtonStyle"],
            AllowDrop = true
        };
        ApplyToolTipPlacement(button);

        button.Click += (_, _) =>
        {
            if (button.Item is not DockItem current) return;
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
            if (eventArgs.ChangedButton == MouseButton.Middle && button.Item is DockItem current)
            {
                eventArgs.Handled = true;
                LaunchNewInstance(current, DisplayNameOf(current));
            }
        };
        var iconScale = AddHoverZoom(button, icon);
        badgeLayer.RenderTransform = icon.RenderTransform;
        button.ContextMenuOpening += (_, eventArgs) => eventArgs.Handled = true;
        button.MouseRightButtonUp += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            Dispatcher.BeginInvoke(
                () =>
                {
                    if (!_closing && IsVisible && button.Item is DockItem current && ItemPanel.Children.Contains(button))
                    {
                        OpenItemMenu(current, DisplayNameOf(current), button);
                    }
                },
                DispatcherPriority.Input);
        };
        button.PreviewKeyDown += (_, eventArgs) =>
        {
            if ((eventArgs.Key == Key.Apps || (eventArgs.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
                && button.Item is DockItem current)
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
            if (button.Item is not DockItem current) return;
            // Pinned apps are dragged to reorder; running apps are dragged in to pin them.
            _dragStart = eventArgs.GetPosition(this);
            _dragItem = current;
        };
        button.PreviewMouseMove += OnItemMouseMove;

        var parts = new DockButtonParts(button, icon, iconHost, dot, iconScale);
        button.DragOver += (_, eventArgs) => OnItemDragOver(parts, eventArgs);
        button.Drop += (_, eventArgs) =>
        {
            if (parts.Button.Item is DockItem current)
            {
                OnItemDrop(parts, current, eventArgs);
            }
        };
        UpdateItemButton(parts, item);
        return parts;
    }

    // Hops the icons of apps that are asking for attention. A hidden dock cannot show them,
    // so its handle pulses instead.
    private void BounceFlashedItems()
    {
        if (!_bounceRequested)
        {
            return;
        }

        _bounceRequested = false;
        var count = DockBounceRules.BounceCount(
            _state.BounceOnAttention,
            Motion,
            SystemParameters.ClientAreaAnimation);
        if (count == 0 || _closing || _trueFullScreenForeground || _suppressedBySystemMenu)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var bounced = false;
        foreach (var parts in _itemButtons)
        {
            if (parts.Button.Item is not DockItem item
                || !_attention.Wants(item))
            {
                continue;
            }

            DateTimeOffset? last = _lastBounce.TryGetValue(item.Key, out var previous) ? previous : null;
            if (!DockBounceRules.IntervalElapsed(last, now))
            {
                continue;
            }

            _lastBounce[item.Key] = now;
            bounced = true;
            StartBounce(parts, count);
        }

        if (bounced && UsesSmartHide && !_autoHide.IsShown && _handleAllowed)
        {
            _handleWindow?.PulseAttention(count);
        }
    }

    private void StartBounce(DockButtonParts parts, int count)
    {
        if (parts.IconHost.RenderTransform is not TranslateTransform translate)
        {
            return;
        }

        var edge = CurrentEdge;
        var amplitude = DockBounceRules.AmplitudeDip(_effectiveIconSize, Motion);
        // Away from the screen edge the dock sits on.
        var (property, peak) = edge switch
        {
            DockEdge.Left => (TranslateTransform.XProperty, amplitude),
            DockEdge.Right => (TranslateTransform.XProperty, -amplitude),
            _ => (TranslateTransform.YProperty, -amplitude)
        };
        var hop = new DoubleAnimationUsingKeyFrames();
        var time = TimeSpan.Zero;
        for (var index = 0; index < count; index++)
        {
            time += DockBounceRules.RiseDuration;
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromTimeSpan(time))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
            time += DockBounceRules.FallDuration;
            hop.KeyFrames.Add(new EasingDoubleKeyFrame(0d, KeyTime.FromTimeSpan(time))
            {
                EasingFunction = new BounceEase { Bounces = 1, Bounciness = 3, EasingMode = EasingMode.EaseOut }
            });
        }
        hop.Duration = new Duration(time);
        // Back to rest when it ends, with nothing held by the animation.
        hop.FillBehavior = FillBehavior.Stop;
        translate.BeginAnimation(property, hop);
    }

    private void UpdateItemButton(DockButtonParts parts, DockItem item)
    {
        var previous = parts.Button.Item;
        parts.Button.Item = item;
        parts.Icon.Source = _visuals.TryGetValue(item.Key, out var visual) ? visual.Visual.Icon : null;

        var wantsAttention = _attention.Wants(item);
        var unread = wantsAttention ? Math.Min(100, _attention.UnreadCount(item)) : 0;
        var attentionChanged = parts.Badge.WantsAttention != wantsAttention;
        if (parts.Badge != (wantsAttention, unread))
        {
            UpdateAttentionBadge((Grid)parts.IconHost.Children[1], wantsAttention, unread);
            parts.Badge = (wantsAttention, unread);
        }

        if (previous?.HasMultipleWindows != item.HasMultipleWindows)
        {
            if (IsVertical)
            {
                parts.Dot.Width = 4;
                parts.Dot.Height = item.HasMultipleWindows ? 10 : 4;
            }
            else
            {
                parts.Dot.Width = item.HasMultipleWindows ? 10 : 4;
                parts.Dot.Height = 4;
            }
        }
        var runningChanged = previous?.IsRunning != item.IsRunning;
        if (runningChanged)
        {
            parts.Dot.Visibility = item.IsRunning ? Visibility.Visible : Visibility.Hidden;
        }
        var isActive = item.IsActive;
        var activeChanged = previous?.IsActive != isActive;
        if (attentionChanged || (!wantsAttention && activeChanged))
        {
            if (wantsAttention)
            {
                parts.Dot.Background = AttentionBrush;
            }
            else
            {
                parts.Dot.SetResourceReference(
                    Border.BackgroundProperty,
                    isActive ? "DockActiveDotBrush" : "DockRunningDotBrush");
            }
        }

        var name = DisplayNameOf(item);
        var nameChanged = !string.Equals(AutomationProperties.GetName(parts.Button), name, StringComparison.Ordinal);
        if (nameChanged || !HaveSameWindowTitles(previous?.Group, item.Group))
        {
            parts.Button.ToolTip = BuildToolTip(item, name);
        }
        if (nameChanged)
        {
            AutomationProperties.SetName(parts.Button, name);
        }
        if (attentionChanged || activeChanged || runningChanged || previous?.WindowCount != item.WindowCount)
        {
            AutomationProperties.SetHelpText(parts.Button, wantsAttention ? "有新提醒" : item.State switch
            {
                DockItemState.Active => "当前窗口",
                DockItemState.Running => item.HasMultipleWindows ? $"{item.WindowCount} 个窗口" : "正在运行",
                _ => "已固定，未运行"
            });
        }
    }

    private void UpdateAttentionBadge(Grid badgeLayer, bool wantsAttention, int unread)
    {
        badgeLayer.Children.Clear();
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
            badgeLayer.Children.Add(pill);
        }
        else if (wantsAttention)
        {
            badgeLayer.Children.Add(new Border
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

    }

    private static bool HaveSameWindowTitles(WindowGroup? previous, WindowGroup? current)
    {
        if (ReferenceEquals(previous, current))
        {
            return true;
        }
        if (previous is null || current is null || previous.Windows.Count != current.Windows.Count)
        {
            return false;
        }
        for (var index = 0; index < current.Windows.Count; index++)
        {
            if (!string.Equals(previous.Windows[index].Title, current.Windows[index].Title, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    internal void RefreshAdaptiveAppearance()
    {
        if (_closing)
        {
            return;
        }

        var highContrast = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast;
        if (highContrast)
        {
            _sampledBackdrop = null;
            _lastAdaptiveDarkInk = null;
            _backdropTimer.Stop();
            Resources.Remove("DockRunningDotBrush");
            Resources.Remove("DockSurfaceBorderBrush");
            Resources.Remove("DockDividerBrush");
            return;
        }

        var baseGlass = DesktopStyleRules.Dock(_style, _state);
        var needsSampling = BarAdaptiveInkRules.NeedsBackdropSampling(baseGlass.Surface, hasDarkAcrylicScrim: false);
        if (!needsSampling)
        {
            _sampledBackdrop = null;
            _lastAdaptiveDarkInk = null;
            _backdropTimer.Stop();
        }

        ApplyDockAdaptiveResources(baseGlass, needsSampling);
        UpdateBackdropSampling(immediate: needsSampling);
    }

    private void OnHighContrastChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(RefreshAdaptiveAppearance);

    private void ApplyDockAdaptiveResources(DockGlass baseGlass, bool needsSampling)
    {
        var adaptive = BarAdaptiveInkRules.ResolveDockAdaptiveColors(
            baseGlass,
            needsSampling ? _sampledBackdrop : null,
            needsSampling ? _lastAdaptiveDarkInk : null);
        _lastAdaptiveDarkInk = needsSampling ? adaptive.UseDarkInk : null;

        Resources["DockRunningDotBrush"] = CreateFrozenBrush(adaptive.RunningDot);
        Resources["DockSurfaceBorderBrush"] = CreateFrozenBrush(adaptive.Border);
        Resources["DockDividerBrush"] = CreateFrozenBrush(adaptive.Divider);
    }

    private void UpdateBackdropSampling(bool immediate)
    {
        if (_closing || !IsVisible || _hiddenBySmartHide || _trueFullScreenForeground
            || AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast)
        {
            _backdropTimer.Stop();
            return;
        }

        var baseGlass = DesktopStyleRules.Dock(_style, _state);
        if (!BarAdaptiveInkRules.NeedsBackdropSampling(baseGlass.Surface, hasDarkAcrylicScrim: false))
        {
            _backdropTimer.Stop();
            return;
        }

        if (!_backdropTimer.IsEnabled)
        {
            _backdropTimer.Start();
        }
        if (immediate)
        {
            ScheduleBackdropSample();
        }
    }

    private void ScheduleBackdropSample()
    {
        if (_closing || !IsVisible || _hiddenBySmartHide || _trueFullScreenForeground
            || AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast)
        {
            return;
        }

        if (_appBar?.Monitor is not { } monitor)
        {
            return;
        }

        var baseGlass = DesktopStyleRules.Dock(_style, _state);
        if (!BarAdaptiveInkRules.NeedsBackdropSampling(baseGlass.Surface, hasDarkAcrylicScrim: false))
        {
            _backdropTimer.Stop();
            return;
        }

        var dockBounds = GetDockSurfacePixelRect();
        if (!dockBounds.HasArea)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _backdropSampleInFlight, 1, 0) != 0)
        {
            return;
        }

        var monitorBounds = monitor.Bounds;
        var edge = CurrentEdge;
        Task.Run(() =>
        {
            try
            {
                if (_closing) return;
                var sampled = ScreenBackdropSampler.SampleDockBackdrop(dockBounds, monitorBounds, edge);
                if (_closing || sampled is null) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (_closing || !IsVisible || _hiddenBySmartHide) return;
                    if (AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast) return;
                    _sampledBackdrop = sampled;
                    var currentGlass = DesktopStyleRules.Dock(_style, _state);
                    var stillNeedsSampling = BarAdaptiveInkRules.NeedsBackdropSampling(
                        currentGlass.Surface,
                        hasDarkAcrylicScrim: false);
                    ApplyDockAdaptiveResources(currentGlass, stillNeedsSampling);
                });
            }
            finally
            {
                Interlocked.Exchange(ref _backdropSampleInFlight, 0);
            }
        });
    }

    private PixelRect GetDockSurfacePixelRect()
    {
        if (_glass.BackgroundBounds is { HasArea: true } backdropBounds)
        {
            return backdropBounds;
        }
        if (_handle != 0 && DockNativeMethods.GetWindowRect(_handle, out var native))
        {
            return new PixelRect(native.Left, native.Top, native.Right, native.Bottom);
        }
        return _appBar?.Bounds ?? default;
    }

    private static SolidColorBrush CreateFrozenBrush(string hex)
    {
        var brush = new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
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
        var displayName = DisplayNameOf(item);
        var launchTarget = item.LaunchTarget;
        var attentionEntries = _attention.EntriesFor(item);
        if (attentionEntries.Count > 0)
        {
            var latest = attentionEntries[0];
            HashSet<uint> latestInstancePids;
            if (latest.ProcessId != 0
                && (item.Group is null
                    || item.Group.Windows.Count > 1
                    || !item.Group.Windows.Any(w => w.Handle == latest.Window || w.ProcessId == latest.ProcessId)))
            {
                latestInstancePids = await Task.Run(() =>
                    WindowCommandService.ResolveInstanceProcessIds(latest.ProcessId, latest.ExecutablePath));
            }
            else
            {
                latestInstancePids = latest.ProcessId != 0
                    ? new HashSet<uint> { latest.ProcessId }
                    : [];
            }

            if (item.Group is not null)
            {
                var latestInGroup = item.Group.Windows.Any(window =>
                    window.Handle == latest.Window
                    || (window.ProcessId != 0 && latestInstancePids.Contains(window.ProcessId)));

                if (latestInGroup)
                {
                    var result = await WindowCommandService.ToggleAsync(
                        item.Group,
                        _toggleCooldown,
                        displayName,
                        window => ScoreAttentionCandidate(window, latest, latestInstancePids));
                    if (!result.NoLiveWindows)
                    {
                        Report(result);
                        return;
                    }

                    RefreshWindows();
                }
                else
                {
                    // The most recent alert belongs to another instance hidden in the tray
                    // (e.g. a 2nd WeChat account while the 1st is open). Do not activate the
                    // wrong visible instance; route through instance-scoped tray reveal instead.
                    var targetPin = launchTarget ?? CreateTransientRevealPin(item, latest.ExecutablePath, displayName);
                    if (targetPin is not null)
                    {
                        await RevealOrLaunchWithAttentionAsync(targetPin, latest, latestInstancePids);
                        return;
                    }
                }
            }

            if (launchTarget is not null)
            {
                await RevealOrLaunchWithAttentionAsync(launchTarget, latest, latestInstancePids);
                return;
            }
        }

        if (item.Group is not null)
        {
            var result = await WindowCommandService.ToggleAsync(
                item.Group,
                _toggleCooldown,
                displayName,
                _attention.AttentionPriority);
            if (!result.NoLiveWindows)
            {
                Report(result);
                return;
            }

            RefreshWindows();
        }

        if (launchTarget is null)
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
            await ReportCommandResultAsync(
                await WindowCommandService.RevealOrLaunchAsync(
                    launchTarget,
                    launchCooldown: _launchCooldown),
                item.DisplayName);
        }
        finally
        {
            _revealPending = false;
        }
    }

    private int ScoreAttentionCandidate(
        TrackedWindow window,
        AttentionEntry latest,
        HashSet<uint> latestInstancePids)
    {
        if (window.Handle != 0 && window.Handle == latest.Window)
        {
            return 4;
        }
        if (window.ProcessId != 0 && latestInstancePids.Contains(window.ProcessId))
        {
            return 3;
        }
        return _attention.AttentionPriority(window);
    }

    private async Task RevealOrLaunchWithAttentionAsync(
        DockPinnedApp targetPin,
        AttentionEntry latest,
        HashSet<uint> latestInstancePids)
    {
        if (_revealPending)
        {
            return;
        }

        _revealPending = true;
        try
        {
            var result = await WindowCommandService.RevealOrLaunchAsync(
                targetPin,
                preferredProcessId: latest.ProcessId != 0 ? latest.ProcessId : null,
                launchCooldown: _launchCooldown);
            if (!result.Succeeded)
            {
                // Clear the notified tray-hidden alert so subsequent clicks are not permanently
                // trapped on the hint; a new flash will re-mark it immediately.
                if (_attention.Cleared(latest.Window, latestInstancePids))
                {
                    RefreshAttentionBadges();
                }
            }
            await ReportCommandResultAsync(result, targetPin.DisplayName);
        }
        finally
        {
            _revealPending = false;
        }
    }

    private static DockPinnedApp? CreateTransientRevealPin(
        DockItem item,
        string? attentionExecutablePath,
        string displayName)
    {
        var executablePath = !string.IsNullOrWhiteSpace(attentionExecutablePath)
            ? attentionExecutablePath
            : item.Group?.Windows
                .Select(window => window.ExecutablePath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (string.IsNullOrWhiteSpace(executablePath)
            || DockAppResolver.IsFrameHost(executablePath)
            || DockAppResolver.IsPackagedPath(executablePath))
        {
            return null;
        }

        return new DockPinnedApp
        {
            DisplayName = displayName,
            ExecutablePath = executablePath
        };
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

    private ScaleTransform AddHoverZoom(Button button, UIElement icon)
    {
        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform(0, 0);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(translate);
        var (originX, originY) = DockEdgeRules.MagnificationOrigin(CurrentEdge);
        icon.RenderTransformOrigin = new Point(originX, originY);
        icon.RenderTransform = group;
        _magSlots.Add(new MagnificationSlot(button, scale, translate));
        button.MouseEnter += (_, _) =>
        {
            if (!_state.HoverMagnification)
            {
                Zoom(scale, DesktopStyleRules.HoverZoom(Motion, SystemParameters.ClientAreaAnimation));
            }
        };
        button.MouseLeave += (_, _) =>
        {
            if (!_state.HoverMagnification)
            {
                Zoom(scale, 1);
            }
        };
        return scale;
    }

    private void ApplyOverflowLayout(double thicknessDip, double overflowDip)
    {
        var edge = CurrentEdge;
        var vertical = DockEdgeRules.IsVertical(edge);
        _topOverflowDip = overflowDip;
        if (vertical)
        {
            DockSurface.Width = thicknessDip;
            DockSurface.Height = double.NaN;
            DockSurface.HorizontalAlignment = edge == DockEdge.Left
                ? System.Windows.HorizontalAlignment.Left
                : System.Windows.HorizontalAlignment.Right;
            DockSurface.VerticalAlignment = VerticalAlignment.Stretch;
            DockContentHost.Padding = new Thickness(5, 14, 5, 14);
            ItemScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            ItemScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            ItemScroller.Margin = overflowDip > 0d
                ? edge == DockEdge.Left
                    ? new Thickness(0, 0, -overflowDip, 0)
                    : new Thickness(-overflowDip, 0, 0, 0)
                : new Thickness(0);
            ItemPanel.Orientation = Orientation.Vertical;
            ItemPanel.HorizontalAlignment = edge == DockEdge.Left
                ? System.Windows.HorizontalAlignment.Left
                : System.Windows.HorizontalAlignment.Right;
            ItemPanel.VerticalAlignment = VerticalAlignment.Stretch;
            return;
        }

        DockSurface.Width = double.NaN;
        DockSurface.Height = thicknessDip;
        DockSurface.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        DockSurface.VerticalAlignment = VerticalAlignment.Bottom;
        DockContentHost.Padding = new Thickness(14, 5, 14, 5);
        ItemScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        ItemScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        ItemScroller.Margin = overflowDip > 0d
            ? new Thickness(0, -overflowDip, 0, 0)
            : new Thickness(0);
        ItemPanel.Orientation = Orientation.Horizontal;
        ItemPanel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        ItemPanel.VerticalAlignment = VerticalAlignment.Bottom;
    }

    private void OnDockSurfacePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_closing || _dragging || _externalDragActive || _menuOpen || _morph.ActiveKind != DockMorphKind.None)
        {
            return;
        }

        var maxScale = DockMagnificationRules.EffectiveMaxScale(
            _state.HoverMagnification,
            _state.HoverMagnificationScale,
            Motion,
            SystemParameters.ClientAreaAnimation);
        if (maxScale <= 1d || _magSlots.Count == 0)
        {
            return;
        }

        var edge = CurrentEdge;
        var vertical = DockEdgeRules.IsVertical(edge);
        var pointer = e.GetPosition(ItemPanel);
        QueueMagnificationTarget(vertical ? pointer.Y : pointer.X, maxScale);
    }

    private void QueueMagnificationTarget(double cursorAxis, double maxScale)
    {
        // A high-report-rate mouse can deliver several positions before a frame is drawn.
        // The frame only needs the latest one to read the layout and compute every icon target.
        _magPendingTarget = (cursorAxis, maxScale);
        if (_magReturnAnimating)
        {
            _hoverFrames?.Finish(completed: false);
            _magReturnAnimating = false;
            _magReturnGeneration++;
            foreach (var slot in _magSlots)
            {
                if (slot.Scale.HasAnimatedProperties || slot.Translate.HasAnimatedProperties)
                {
                    DetachSlotAnimations(slot);
                }
            }
        }
        if (!_magRegionExpanded)
        {
            _magRegionExpanded = true;
            SyncMagnificationHitRegion();
        }
        if (!_magTimer.IsEnabled)
        {
            if (!_magClock.IsRunning)
            {
                _magClock.Restart();
            }
            _magLastTicks = _magClock.ElapsedTicks;
            _hoverFrames?.Begin("hover-magnify", useOwnRendering: false);
            _magTimer.Start();
        }
    }

    private void UpdateMagnificationTargets(double cursorAxis, double maxScale)
    {
        var edge = CurrentEdge;
        var vertical = DockEdgeRules.IsVertical(edge);
        var radiusDip = DockLayoutMetrics.ItemCell(_effectiveIconSize) * DockMagnificationRules.InfluenceRadiusCells;
        var motion = Motion;
        for (var i = 0; i < _magSlots.Count; i++)
        {
            var slot = _magSlots[i];
            if (slot.Scale.HasAnimatedProperties || slot.Translate.HasAnimatedProperties)
            {
                DetachSlotAnimations(slot);
            }
            var offset = VisualTreeHelper.GetOffset(slot.Button);
            var centerAxis = vertical
                ? offset.Y + (slot.Button.ActualHeight * 0.5d)
                : offset.X + (slot.Button.ActualWidth * 0.5d);
            var targetScale = DockMagnificationRules.ComputeScale(cursorAxis - centerAxis, radiusDip, maxScale);
            var (liftX, liftY) = DockEdgeRules.ComputeLiftVectorDip(edge, targetScale, _effectiveIconSize, motion);
            slot.TargetScale = targetScale;
            slot.TargetLiftX = liftX;
            slot.TargetLiftY = liftY;
        }
    }

    private void OnDockSurfaceMouseLeave(object sender, MouseEventArgs e)
    {
        if (_state.HoverMagnification)
        {
            StopMagnification(animateReturn: true);
        }
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closing || !_state.HoverMagnification)
        {
            return;
        }

        if (e.PropertyName is not (nameof(SystemParameters.ClientAreaAnimation) or null or ""))
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () =>
            {
                if (_closing || !_state.HoverMagnification)
                {
                    return;
                }

                var desiredOverflow = DockMagnificationRules.RequiredTopOverflowDip(
                    _state.HoverMagnification,
                    _state.HoverMagnificationScale,
                    _effectiveIconSize,
                    Motion,
                    SystemParameters.ClientAreaAnimation);
                if (Math.Abs(desiredOverflow - _topOverflowDip) > 0.01d)
                {
                    ApplyState();
                }
            });
    }

    private void StepMagnification()
    {
        if (_closing || _magSlots.Count == 0)
        {
            _hoverFrames?.Finish(completed: false);
            _magTimer.Stop();
            return;
        }

        if (_magPendingTarget is { } pending)
        {
            _magPendingTarget = null;
            UpdateMagnificationTargets(pending.CursorAxis, pending.MaxScale);
        }

        var nowTicks = _magClock.ElapsedTicks;
        var dt = Math.Clamp((nowTicks - _magLastTicks) / (double)System.Diagnostics.Stopwatch.Frequency, 0.001d, 0.05d);
        _magLastTicks = nowTicks;

        var rate = DockMagnificationRules.SmoothingRatePerSecond(Motion);
        var alpha = double.IsPositiveInfinity(rate) ? 1d : 1d - Math.Exp(-rate * dt);
        var anyPending = false;

        for (var i = 0; i < _magSlots.Count; i++)
        {
            var slot = _magSlots[i];
            var nextScale = slot.CurrentScale + ((slot.TargetScale - slot.CurrentScale) * alpha);
            var nextLiftX = slot.CurrentLiftX + ((slot.TargetLiftX - slot.CurrentLiftX) * alpha);
            var nextLiftY = slot.CurrentLiftY + ((slot.TargetLiftY - slot.CurrentLiftY) * alpha);
            if (Math.Abs(slot.TargetScale - nextScale) < 0.0015d
                && Math.Abs(slot.TargetLiftX - nextLiftX) < 0.05d
                && Math.Abs(slot.TargetLiftY - nextLiftY) < 0.05d)
            {
                nextScale = slot.TargetScale;
                nextLiftX = slot.TargetLiftX;
                nextLiftY = slot.TargetLiftY;
            }
            else
            {
                anyPending = true;
            }

            if (nextScale != slot.CurrentScale)
            {
                slot.CurrentScale = nextScale;
                slot.Scale.ScaleX = nextScale;
                slot.Scale.ScaleY = nextScale;
            }
            if (nextLiftX != slot.CurrentLiftX)
            {
                slot.CurrentLiftX = nextLiftX;
                slot.Translate.X = nextLiftX;
            }
            if (nextLiftY != slot.CurrentLiftY)
            {
                slot.CurrentLiftY = nextLiftY;
                slot.Translate.Y = nextLiftY;
            }
        }

        _hoverFrames?.Observe(_magTimer.LastFrame);
        if (!anyPending)
        {
            _hoverFrames?.Finish(completed: true);
            _magTimer.Stop();
        }
    }

    private void StopMagnification(bool animateReturn)
    {
        // Always stop the animation timer immediately when leaving or hiding the dock.
        _hoverFrames?.Finish(completed: false);
        _magTimer.Stop();
        _magPendingTarget = null;
        var generation = ++_magReturnGeneration;
        if (_magSlots.Count == 0)
        {
            _magReturnAnimating = false;
            _magRegionExpanded = false;
            SyncMagnificationHitRegion();
            return;
        }

        var returnDuration = animateReturn && SystemParameters.ClientAreaAnimation
            ? DockMagnificationRules.ReturnDuration(Motion)
            : TimeSpan.Zero;
        if (returnDuration > TimeSpan.Zero)
        {
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            ease.Freeze();
            var scaleBack = new DoubleAnimation(1d, new Duration(returnDuration)) { EasingFunction = ease };
            var liftBack = new DoubleAnimation(0d, new Duration(returnDuration)) { EasingFunction = ease };
            scaleBack.Freeze();
            liftBack.Freeze();

            var startedAny = false;
            ScaleTransform? completionHost = null;
            for (var i = 0; i < _magSlots.Count; i++)
            {
                var slot = _magSlots[i];
                var wasMagnified = Math.Abs(slot.CurrentScale - 1d) > 0.001d
                    || Math.Abs(slot.CurrentLiftX) > 0.05d
                    || Math.Abs(slot.CurrentLiftY) > 0.05d;
                slot.CurrentScale = 1d;
                slot.TargetScale = 1d;
                slot.CurrentLiftX = 0d;
                slot.TargetLiftX = 0d;
                slot.CurrentLiftY = 0d;
                slot.TargetLiftY = 0d;
                if (wasMagnified)
                {
                    if (completionHost is null)
                    {
                        completionHost = slot.Scale;
                        _hoverFrames?.Begin("hover-return", useOwnRendering: true);
                        var completionScaleBack = new DoubleAnimation(1d, new Duration(returnDuration))
                        {
                            EasingFunction = ease
                        };
                        completionScaleBack.Completed += (_, _) =>
                        {
                            if (_closing || _magReturnGeneration != generation)
                            {
                                return;
                            }
                            _hoverFrames?.Finish(completed: true);
                            _magReturnAnimating = false;
                            _magRegionExpanded = false;
                            ResetMagnificationSlotsImmediate();
                            SyncMagnificationHitRegion();
                        };
                        slot.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, completionScaleBack);
                        slot.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleBack);
                    }
                    else
                    {
                        slot.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleBack);
                        slot.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleBack);
                    }
                    slot.Translate.BeginAnimation(TranslateTransform.XProperty, liftBack);
                    slot.Translate.BeginAnimation(TranslateTransform.YProperty, liftBack);
                    startedAny = true;
                }
            }
            _magReturnAnimating = startedAny;
            if (!startedAny)
            {
                _magRegionExpanded = false;
                SyncMagnificationHitRegion();
            }
            return;
        }

        _magReturnAnimating = false;
        _magRegionExpanded = false;
        ResetMagnificationSlotsImmediate();
        SyncMagnificationHitRegion();
    }

    private double ReadHoverScale()
    {
        var largest = 1d;
        foreach (var slot in _magSlots)
        {
            largest = Math.Max(largest, slot.Scale.ScaleX);
        }
        return largest;
    }

    private static void DetachSlotAnimations(MagnificationSlot slot)
    {
        var currentScale = slot.Scale.ScaleX;
        var currentLiftX = slot.Translate.X;
        var currentLiftY = slot.Translate.Y;
        slot.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        slot.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        slot.Translate.BeginAnimation(TranslateTransform.XProperty, null);
        slot.Translate.BeginAnimation(TranslateTransform.YProperty, null);
        if (!double.IsFinite(currentScale) || currentScale <= 0d)
        {
            currentScale = 1d;
        }
        if (!double.IsFinite(currentLiftX))
        {
            currentLiftX = 0d;
        }
        if (!double.IsFinite(currentLiftY))
        {
            currentLiftY = 0d;
        }

        slot.CurrentScale = currentScale;
        slot.Scale.ScaleX = currentScale;
        slot.Scale.ScaleY = currentScale;
        slot.CurrentLiftX = currentLiftX;
        slot.Translate.X = currentLiftX;
        slot.CurrentLiftY = currentLiftY;
        slot.Translate.Y = currentLiftY;
    }

    private void ResetMagnificationSlotsImmediate()
    {
        for (var i = 0; i < _magSlots.Count; i++)
        {
            var slot = _magSlots[i];
            slot.CurrentScale = 1d;
            slot.TargetScale = 1d;
            slot.CurrentLiftX = 0d;
            slot.TargetLiftX = 0d;
            slot.CurrentLiftY = 0d;
            slot.TargetLiftY = 0d;
            slot.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            slot.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            slot.Translate.BeginAnimation(TranslateTransform.XProperty, null);
            slot.Translate.BeginAnimation(TranslateTransform.YProperty, null);
            slot.Scale.ScaleX = 1d;
            slot.Scale.ScaleY = 1d;
            slot.Translate.X = 0d;
            slot.Translate.Y = 0d;
        }
    }

    private void SyncMagnificationHitRegion()
    {
        if (_closing || _handle == 0 || !DockNativeMethods.GetWindowRect(_handle, out var native))
        {
            return;
        }

        var edge = CurrentEdge;
        var widthPx = native.Right - native.Left;
        var heightPx = native.Bottom - native.Top;
        var overflowPx = 0;
        if (_topOverflowDip > 0d && PresentationSource.FromVisual(this) is { CompositionTarget: { } target })
        {
            var dpiX = target.TransformToDevice.M11;
            var dpiY = target.TransformToDevice.M22;
            if (DockSurface.IsLoaded && DockSurface.ActualWidth > 0d && DockSurface.ActualHeight > 0d)
            {
                var surfaceOrigin = DockSurface.TransformToAncestor(this).Transform(new Point(0d, 0d));
                overflowPx = edge switch
                {
                    DockEdge.Left => Math.Max(0, (int)Math.Floor((ActualWidth - (surfaceOrigin.X + DockSurface.ActualWidth)) * dpiX)),
                    DockEdge.Right => Math.Max(0, (int)Math.Floor(surfaceOrigin.X * dpiX)),
                    _ => Math.Max(0, (int)Math.Floor(surfaceOrigin.Y * dpiY))
                };
            }
            if (overflowPx <= 0)
            {
                var axisDpi = DockEdgeRules.IsVertical(edge) ? dpiX : dpiY;
                overflowPx = Math.Max(0, (int)Math.Round(_topOverflowDip * axisDpi));
            }
        }

        var desired = DockMagnificationRules.ComputeHitRegionRect(
            widthPx,
            heightPx,
            edge,
            overflowPx,
            _magRegionExpanded,
            _morph.ActiveKind != DockMorphKind.None);
        if (_hasAppliedHitRegion && _appliedHitRegion == desired)
        {
            return;
        }

        NativeMethods.ApplyWindowRegion(_handle, desired);
        _appliedHitRegion = desired;
        _hasAppliedHitRegion = true;
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

        var fromX = scale.ScaleX;
        var fromY = scale.ScaleY;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = target;
        scale.ScaleY = target;
        if (Math.Abs(fromX - target) < 0.0001d && Math.Abs(fromY - target) < 0.0001d)
        {
            return;
        }

        var duration = new Duration(DesktopStyleRules.HoverZoomDuration(Motion));
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        var animationX = new DoubleAnimation(fromX, target, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        var animationY = new DoubleAnimation(fromY, target, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        animationX.Freeze();
        animationY.Freeze();
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animationX);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animationY);
    }

    private async Task QuitItemFromMenuAsync(DockItem item, string name)
    {
        var result = await WindowCommandService.QuitAsync(item, name, _items);
        Report(result);
        if (!_closing)
        {
            RefreshWindows();
        }
    }

    private void Report(WindowCommandResult result)
    {
        if (!result.Succeeded)
        {
            CommandFailed?.Invoke(result.Message);
        }
    }

    private async Task ReportCommandResultAsync(WindowCommandResult result, string? displayName)
    {
        if (result.Succeeded)
        {
            return;
        }

        var message = result.Message;
        if (result.OpenSystemTrayRecommended
            && OpenTrayOverflowRequested is { } openTray
            && await openTray())
        {
            message = TrayAppRevealRules.FormatMultiInstanceTrayOpenedHint(
                string.IsNullOrWhiteSpace(displayName) ? "该应用" : displayName);
        }

        CommandFailed?.Invoke(message);
    }

    private void OpenItemMenu(DockItem item, string name, UIElement? anchor = null)
    {
        var menu = anchor is null
            ? CreateMenu()
            : CreateMenu(anchor, InwardMenuPlacement(CurrentEdge));
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
            if (DockQuitRules.CanOfferQuit(item, _items))
            {
                menu.AddItem("结束应用", () => _ = QuitItemFromMenuAsync(item, name));
            }
        }
        else if (DockQuitRules.CanOfferQuit(item, _items))
        {
            menu.AddItem("结束后台应用", () => _ = QuitItemFromMenuAsync(item, name));
        }

        var isPathPin = item.Pinned is { } pathPin && DockPinRules.IsPathPin(pathPin);
        var executablePath = item.LaunchTarget?.ExecutablePath
            ?? item.Pinned?.TargetPath
            ?? item.Group?.Windows
                .Select(window => window.ExecutablePath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (executablePath is not null
            && !DockPinRules.IsSystemPlace(executablePath)
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
            menu.AddItem("发送到文件收纳盒…", () => OpenSendToFileBoxMenu(mappingPath, anchor));
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
            CommandFailed?.Invoke("请先新建文件收纳盒。");
            return;
        }

        var menu = anchor is null ? CreateMenu() : CreateMenu(anchor, InwardMenuPlacement(CurrentEdge));
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
                menu.AddItem("添加到 Dock…", PickAppToPin);
                menu.AddItem("Dock 设置…", () => SettingsRequested?.Invoke());
                if (ScreenSelection is null)
                {
                    menu.AddItem("关闭冰蓝 Dock", () => HideRequested?.Invoke());
                }
                else if (ScreenSelection.IsPersistentIdentity)
                {
                    menu.AddItem("关闭此屏的 Dock", () => HideRequested?.Invoke());
                }
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
        var darkSurface = _lastAdaptiveDarkInk is { } adaptiveDarkInk
            ? !adaptiveDarkInk
            : TryFindResource("DockSurfaceBrush") is SolidColorBrush brush
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
            if (dragged.IsPinned && dragged.IsInRunningSection)
            {
                data.SetData(RunningPinDragFormat, true);
            }
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
        ClearPinDragIndicator();
        _dragHover.Clear();

        if (e.Data.GetDataPresent(PinDragFormat) || e.Data.GetDataPresent(RunningDragFormat))
        {
            e.Effects = (e.AllowedEffects & DragDropEffects.Move) != 0 && CanPlacePinDrag(e.Data, null)
                ? DragDropEffects.Move
                : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) || DockShortcuts.HasShellItemDrop(e.Data))
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
            var canMove = (e.AllowedEffects & DragDropEffects.Move) != 0 && CanPlacePinDrag(e.Data, parts.Button.Item);
            e.Effects = canMove ? DragDropEffects.Move : DragDropEffects.None;
            if (canMove && parts.Button.Item is DockItem { IsInRunningSection: false })
            {
                var pos = e.GetPosition(parts.Button);
                var afterHalf = IsVertical
                    ? pos.Y >= parts.Button.ActualHeight * 0.5d
                    : pos.X >= parts.Button.ActualWidth * 0.5d;
                SetPinDragIndicator(parts, afterHalf);
            }
            else
            {
                ClearPinDragIndicator();
            }
            e.Handled = true;
            return;
        }

        ClearPinDragIndicator();

        if (parts.Button.Item is not DockItem current
            || e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] { Length: > 0 } files)
        {
            OnSurfaceDragOver(parts.Button, e);
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

        if (_hoveredDragParts?.Button.Item is DockItem item)
        {
            TryHoverRaise(item);
        }
    }

    private void TryHoverRaise(DockItem item)
    {
        if (_dragHover.Hover(item.Key, item.Group is not null, DateTimeOffset.UtcNow)
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

    private void SetPinDragIndicator(DockButtonParts parts, bool afterHalf)
    {
        if (ReferenceEquals(_pinDragIndicatorParts, parts) && _pinDragIndicatorAfter == afterHalf)
        {
            return;
        }

        ClearPinDragIndicator();
        _pinDragIndicatorParts = parts;
        _pinDragIndicatorAfter = afterHalf;
        // Total edge thickness stays 2px (same as default 1+1) so the indicator never shifts layout.
        parts.Button.BorderThickness = IsVertical
            ? (afterHalf ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0))
            : (afterHalf ? new Thickness(0, 0, 2, 0) : new Thickness(2, 0, 0, 0));
        parts.Button.SetResourceReference(Button.BorderBrushProperty, "DockActiveDotBrush");
    }

    private void ClearPinDragIndicator()
    {
        if (_pinDragIndicatorParts is null)
        {
            return;
        }

        _pinDragIndicatorParts.Button.ClearValue(Button.BorderThicknessProperty);
        _pinDragIndicatorParts.Button.ClearValue(Button.BorderBrushProperty);
        _pinDragIndicatorParts = null;
        _pinDragIndicatorAfter = false;
    }

    private void EndExternalDrag()
    {
        _externalDragActive = false;
        _dragHoverTimer.Stop();
        _dragHover.Clear();
        ClearDragTargetVisual();
        ClearPinDragIndicator();
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

    private async void OnItemDrop(DockButtonParts parts, DockItem target, DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] { Length: > 0 } files)
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

        var pos = e.GetPosition(parts.Button);
        var pointerAxis = IsVertical ? pos.Y : pos.X;
        var itemLength = IsVertical ? parts.Button.ActualHeight : parts.Button.ActualWidth;
        EndExternalDrag();
        DropPin(e, target, pointerAxis, itemLength);
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

    private void DropPin(DragEventArgs e, DockItem target, double pointerAxis, double itemLength)
    {
        if (!CanPlacePinDrag(e.Data, target))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.Data.GetData(RunningDragFormat) is string runningKey)
        {
            e.Handled = true;
            var index = target is { IsInRunningSection: false, Pinned: { } at }
                ? DockEdgeRules.ComputePinInsertIndex(
                    DockPinRules.IndexOf(_state, DockPinRules.IdentityKey(at)),
                    pointerAxis,
                    itemLength,
                    _state.PinnedApps.Count)
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
        var sourceIndex = DockPinRules.IndexOf(_state, key);
        var targetIndex = target is { IsInRunningSection: false, Pinned: { } pinned }
            ? DockEdgeRules.ComputePinMoveIndex(
                sourceIndex,
                DockPinRules.IndexOf(_state, DockPinRules.IdentityKey(pinned)),
                pointerAxis,
                itemLength,
                _state.PinnedApps.Count)
            : _state.PinnedApps.Count - 1;
        if (DockPinRules.Move(_state, key, targetIndex))
        {
            PinsChanged();
        }
    }

    private void OnDropOnSurface(object sender, DragEventArgs e)
    {
        EndExternalDrag();
        if (_closing || e.Handled)
        {
            return;
        }
        if (!CanPlacePinDrag(e.Data, null))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            e.Handled = true;
            _ = PinFilesAsync(files);
            return;
        }

        if (e.Data is System.Runtime.InteropServices.ComTypes.IDataObject shellData
            && DockShortcuts.HasShellItemDrop(e.Data))
        {
            e.Handled = true;
            e.Effects = DragDropEffects.None;
            if ((e.AllowedEffects & DragDropEffects.Link) != 0)
            {
                var locations = DockShortcuts.ReadSystemPlacePins(shellData);
                if (locations.Count > 0)
                {
                    e.Effects = DragDropEffects.Link;
                    PinApps(locations);
                }
            }
            return;
        }

        if (e.Data.GetData(RunningDragFormat) is string runningKey)
        {
            e.Handled = true;
            PinRunning(runningKey, _state.PinnedApps.Count);
            return;
        }

        if (e.Data.GetData(PinDragFormat) is string key
            && DockPinRules.Move(_state, key, _state.PinnedApps.Count - 1))
        {
            PinsChanged();
        }
    }

    private static bool CanPlacePinDrag(System.Windows.IDataObject data, DockItem? target) =>
        !data.GetDataPresent(RunningPinDragFormat) || target is { IsInRunningSection: false };

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
        if (string.Equals(previous, iconFile, StringComparison.Ordinal))
        {
            return;
        }
        pinned.IconFile = iconFile;
        iconStore.Delete(previous);
        PinsChanged();
    }

    private void PinsChanged()
    {
        if (SharedItemsChanged is { } refreshShared)
        {
            refreshShared();
        }
        else
        {
            RefreshSharedItems();
        }
        DockStateChanged?.Invoke();
    }

    private double ContentLengthDip()
    {
        return DockLayoutMetrics.ContentLengthForItems(
                   _items.Count + LeadingButtonCount + (_dockOverflowing ? 1 : 0),
                   _effectiveIconSize)
               + (DividerCount(_items) * DockLayoutMetrics.DividerDip);
    }

    private (bool Start, bool Launcher, bool Desktop, bool TaskView, bool QuickSettings) VisibleCommands =>
        (_state.ShowStartMenuButton, LauncherEnabled && _state.ShowLauncherButton && LauncherRequested is not null,
            _state.ShowDesktopButton, _state.ShowTaskViewButton, _state.ShowQuickSettingsButton);

    private int LeadingButtonCount
    {
        get
        {
            var (start, launcher, desktop, taskView, quickSettings) = VisibleCommands;
            return (start ? 1 : 0) + (launcher ? 1 : 0) + (desktop ? 1 : 0) + (taskView ? 1 : 0)
                + (quickSettings ? 1 : 0);
        }
    }

    private int DividerCount(IReadOnlyList<DockItem> items) =>
        (LeadingButtonCount > 0 && items.Count > 0 ? 1 : 0)
        + (HasRunningDivider(items) ? 1 : 0);

    private static bool HasRunningDivider(IReadOnlyList<DockItem> items) =>
        items.Count > 1 && !items[0].IsInRunningSection && items[^1].IsInRunningSection;

    private void UpdateEffectiveIconSize(MonitorSnapshot monitor, IReadOnlyList<DockItem> items)
    {
        var edge = CurrentEdge;
        var nativeSafe = TaskbarEdgeGuard.AvoidNativeTaskbar(monitor.WorkingArea, monitor.Bounds);
        var safeArea = DockEdgeRules.IsVertical(edge)
            ? DockEdgeRules.ComputeSafeVerticalBounds(
                monitor.Bounds,
                monitor.WorkingArea,
                nativeSafe,
                ResolveTopBarReservedBottomPx?.Invoke(monitor))
            : nativeSafe;
        var availableDip = DockEdgeRules.AvailableExtentPixels(safeArea, edge)
            * 96d / monitor.Dpi * DockLayoutMetrics.MaxExtentFraction;
        var cellCount = items.Count + LeadingButtonCount;
        var dividersDip = DividerCount(items) * DockLayoutMetrics.DividerDip;
        var preferred = _state.AutoIconSize
            ? DockLayoutMetrics.FitIconSize(cellCount, dividersDip, availableDip, _state.IconSize)
            : _state.IconSize;
        // Even at the minimum icon size not everything fits: keep the overflow button and
        // scroll along the dock axis instead of hiding apps for good.
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

        using var measurement = UiDiagnostics.Measure("Dock 前台判定");

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

        var edge = CurrentEdge;
        var bounds = _appBar.Bounds;
        // Native menus own this interaction, except a collapsed auto-hidden taskbar or a
        // taskbar / taskbar-anchored popup on a different edge that does not touch the dock.
        var shouldYield = ShouldYieldToCurrentSystemSurface(monitor.Bounds, edge, bounds);
        if (YieldToSystemMenu(shouldYield)) return;
        var foreground = ForegroundWindowRect(out var foregroundBounds, out var foregroundWindow);
        var hasBacking = TransientForegroundResolver.TryResolveBackingWindow(foregroundWindow, _handle, out var backing);
        var fullScreen = foreground && DockVacateRules.CoversMonitor(foregroundBounds, monitor.Bounds);
        var foregroundZoomed = foreground && foregroundWindow != 0 && DockNativeMethods.IsZoomed(foregroundWindow);
        // A maximized window on the dock's monitor both vacates the reservation and,
        // below, hides the dock at once — the row collapses while Windows re-fills
        // the maximized window over the freed strip, as one motion.
        var maximizedOnDockMonitor = foregroundZoomed
            && DockNativeMethods.MonitorFromWindow(
                foregroundWindow,
                DockNativeMethods.MonitorDefaultToNearest) == monitor.MonitorHandle;
        var backingMaximizedOnDockMonitor = hasBacking
            && backing.IsZoomed
            && backing.MonitorHandle == monitor.MonitorHandle;
        var backingOverlapsDock = hasBacking && backing.Bounds.Intersects(bounds);
        // A maximized window filling the monitor (for example while the taskbar auto-hides),
        // an ordinary resizable desktop window with standard chrome, or a tool/overlay window
        // is still working space rather than a true full-screen app.
        var foregroundOwnProcess = false;
        var hasStandardChrome = false;
        var isToolOrOverlay = false;
        var isTopmost = false;
        if (foreground && foregroundWindow != 0)
        {
            DockNativeMethods.GetWindowThreadProcessId(foregroundWindow, out var foregroundPid);
            foregroundOwnProcess = foregroundPid == (uint)Environment.ProcessId;
            if (!foregroundOwnProcess)
            {
                var exStyle = NativeMethods.GetExStyleOf(foregroundWindow);
                isToolOrOverlay = (exStyle & (NativeMethods.WsExToolWindow | NativeMethods.WsExTransparent)) != 0;
                isTopmost = (exStyle & NativeMethods.WsExTopmost) != 0;
                if (fullScreen && !foregroundZoomed)
                {
                    var style = (long)NativeMethods.GetWindowLongPtr(foregroundWindow, NativeMethods.GwlStyle);
                    var className = new StringBuilder(64);
                    DockNativeMethods.GetClassNameW(foregroundWindow, className, className.Capacity);
                    var isApplicationFrame = string.Equals(
                        className.ToString(),
                        "ApplicationFrameWindow",
                        StringComparison.Ordinal);
                    hasStandardChrome = !isApplicationFrame
                        && (style & WsCaption) == WsCaption
                        && (style & NativeMethods.WsThickFrame) != 0;
                }
            }
        }
        var directTrueFullScreen = DockVacateRules.IsTrueFullScreen(
            fullScreen && !foregroundOwnProcess,
            foregroundZoomed,
            hasStandardChrome,
            isToolOrOverlay,
            isTopmost);
        var backingFullScreen = !directTrueFullScreen
            && !foregroundOwnProcess
            && foregroundWindow != 0
            && FullScreenWatcher.IsTrueFullScreenContext(foregroundWindow, monitor.MonitorHandle);
        var trueFullScreen = DockVacateRules.IsEffectiveFullScreen(directTrueFullScreen, backingFullScreen);
        _trueFullScreenForeground = trueFullScreen;
        var effectiveMaximizedOnDockMonitor = maximizedOnDockMonitor || backingMaximizedOnDockMonitor;
        var checkedMonitorForMaximized = !effectiveMaximizedOnDockMonitor
            && _state.ReleaseForBackgroundMaximizedWindow
            && DockVacateRules.CanReleaseForMaximized(CurrentVisibilityMode, _state.ReleaseWhenMaximized);
        var monitorOverlaps = false;
        var monitorMaximized = false;
        if (checkedMonitorForMaximized)
        {
            TransientForegroundResolver.TryFindCoveringWindowOnMonitor(
                monitor.MonitorHandle, bounds, _handle, out monitorOverlaps, out monitorMaximized, out _);
        }
        var shouldVacate = DockVacateRules.ShouldReleaseForMaximized(
            CurrentVisibilityMode,
            _state.ReleaseWhenMaximized,
            effectiveMaximizedOnDockMonitor,
            _state.ReleaseForBackgroundMaximizedWindow,
            monitorMaximized);
        if (shouldVacate != _vacatedForMaximized)
        {
            _vacatedForMaximized = shouldVacate;
            ApplyState();
        }

        if (!UsesSmartHide)
        {
            var reservedYield = DockVacateRules.ShouldReservedDockYield(
                trueFullScreen,
                fullScreen,
                foregroundOwnProcess,
                isToolOrOverlay,
                foreground ? foregroundBounds : default,
                bounds,
                monitor.Bounds,
                edge);
            if (reservedYield)
            {
                StopMagnification(animateReturn: false);
            }
            _appBar.SetFullScreenDetected(reservedYield);
            return;
        }

        if (CurrentVisibilityMode == DockVisibilityMode.ReserveWorkArea
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
        // Only a bar occupying the dock's own edge suppresses edge reveal; a bottom
        // Windows taskbar never blocks a left or right dock from revealing.
        var edgeFree = MonitorCatalog.TryGetWorkingArea(monitor.MonitorHandle) is not { } liveWorkArea
            || DockRevealRules.IsEdgeFree(liveWorkArea, monitor.Bounds, edge);
        var edgeReveal = DockRevealRules.EdgeReveal(_state.RevealMode, edgeFree);
        var handleAllowed = DockRevealRules.UsesHandle(_state.RevealMode);
        if (handleAllowed != _handleAllowed)
        {
            _handleAllowed = handleAllowed;
            SyncHandle();
        }

        var now = DateTimeOffset.UtcNow;
        var handleRect = _handleWindow is null ? default : _handleWindow.CurrentRect;
        var pointerOverHandle = _handleWindow is { IsVisible: true, IsDragActive: false }
            && cursor.X >= handleRect.Left && cursor.X < handleRect.Right
            && cursor.Y >= handleRect.Top && cursor.Y < handleRect.Bottom;
        _pointerOverHandle = pointerOverHandle;
        var pointerOverStrip = IsVisible
            && cursor.X >= bounds.Left && cursor.X < bounds.Right
            && cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom;
        _handleRevealUntil = DockAutoHideState.UpdateHandleRevealDeadline(
            _handleRevealUntil,
            now,
            revealedFromHandle: pointerOverHandle,
            pointerOverStrip: pointerOverStrip,
            clearRequested: trueFullScreen || (_handleWindow?.IsDragActive ?? false));
        var directOverlaps = foreground && foregroundBounds.Intersects(bounds);
        var (effectiveOverlaps, effectiveOverlapMaximized) = DockVacateRules.ResolveEffectiveSmartHideOverlap(
            directOverlaps,
            maximizedOnDockMonitor,
            backingOverlapsDock,
            backingMaximizedOnDockMonitor);
        if (!checkedMonitorForMaximized && !_state.AutoHideAlways && !(effectiveOverlaps && effectiveOverlapMaximized))
        {
            TransientForegroundResolver.TryFindCoveringWindowOnMonitor(
                monitor.MonitorHandle, bounds, _handle, out monitorOverlaps, out monitorMaximized, out _);
        }
        effectiveOverlaps |= monitorOverlaps;
        effectiveOverlapMaximized |= monitorMaximized;
        var pointerOverDock = pointerOverStrip || pointerOverHandle;
        var input = new DockAutoHideInput(
            // This input is only built while smart-hiding, where a maximized window on
            // this monitor is guaranteed to take over the strip the moment the
            // reservation releases — count it as overlap before its animated resize
            // has covered the pixels, or the collapse would lag the maximize click.
            WindowOverlapsDock: DockAutoHideState.TreatAsOverlapping(
                _state.AutoHideAlways,
                effectiveOverlaps || effectiveOverlapMaximized),
            ForegroundIsFullScreen: trueFullScreen,
            PointerInRevealZone: edgeReveal != DockEdgeReveal.None
                && DockRevealRules.IsInRevealZone(
                    cursor.X,
                    cursor.Y,
                    // Over an occupied edge only the last pixels count: treat the dock as
                    // flush with the screen edge so the zone shrinks to the thin strip.
                    edgeReveal == DockEdgeReveal.EdgeStrip
                        ? DockRevealRules.FlushEdgeBounds(bounds, monitor.Bounds, edge)
                        : bounds,
                    monitor.Bounds,
                    revealDepth,
                    edge),
            PointerOverDock: pointerOverDock,
            InteractionActive: _menuOpen || _dragging || _externalDragActive
                || now < _attentionRevealUntil || now < _handleRevealUntil,
            OverlapIsMaximizedWindow: effectiveOverlapMaximized);
        if (_trueFullScreenForeground && _morph.IsCollapsing)
        {
            // Nothing may animate over a truly full-screen app: land the pending
            // collapse immediately instead of letting the flight finish.
            CompleteSmartHideImmediately();
        }

        if (_autoHide.Update(input, now))
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
                BeginSmartHide();
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
                if (MorphEnabled && !_morph.IsCollapsing)
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

    private static bool ShouldYieldToCurrentSystemSurface(
        PixelRect monitorBounds,
        DockEdge dockEdge,
        PixelRect dockBounds)
    {
        if (!ShellSurfaceNativeMethods.TryGetForegroundSystemMenu(out var surfaceKind, out var systemMenuBounds))
        {
            return false;
        }

        var nativeTaskbarEdge = surfaceKind is SystemSurfaceKind.Taskbar or SystemSurfaceKind.TaskbarPopup
            ? TaskbarEdgeGuard.InferNativeTaskbarEdge(
                monitorBounds,
                surfaceKind == SystemSurfaceKind.Taskbar ? systemMenuBounds : null)
            : null;
        var isTaskbarCollapsed = surfaceKind == SystemSurfaceKind.Taskbar
            && TaskbarEdgeGuard.IsAutoHideTaskbarCollapsed(
                systemMenuBounds,
                monitorBounds,
                nativeTaskbarEdge ?? dockEdge);
        return DockVacateRules.ShouldYieldToSystemSurface(
            surfaceKind,
            dockEdge,
            nativeTaskbarEdge,
            systemMenuBounds.HasArea && systemMenuBounds.Intersects(dockBounds),
            isTaskbarCollapsed);
    }

    private void CheckYieldRelease()
    {
        if (_closing || !_suppressedBySystemMenu)
        {
            _yieldReleaseTimer.Stop();
            return;
        }
        if (_appBar?.Monitor is { } monitor
            && ShouldYieldToCurrentSystemSurface(monitor.Bounds, CurrentEdge, _appBar.Bounds))
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
    private bool TaskbarCoversDock() =>
        _appBar is { Monitor: { } monitor } appBar
        && TaskbarEdgeGuard.IsVisibleTaskbarOverlapping(monitor, appBar.Bounds);

    private void ShowDock()
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
            _appBar?.SetFloatingOverride(null);
            var placement = _appBar?.Bounds ?? default;
            _morph.BeginExpand(MorphCentre(placement), Motion);
        }
        if (!IsVisible)
        {
            _appBar?.SetFloatingOverride(null);
            if (MorphEnabled && !_morph.IsCollapsing)
            {
                var placement = _appBar?.Bounds ?? default;
                // Reappear in place at the centred dock strip with a fade and a slight
                // grow from the row's resting edge.
                _morph.BeginReappear(BottomEdgeCentre(placement), Motion);
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
        StopMagnification(animateReturn: false);
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
        if (!_handleAllowed)
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
        _handleRevealUntil = DateTimeOffset.MinValue;
        _pointerOverHandle = false;
        _appBar?.SetFloatingOverride(null);
        HideFromApp();
        SyncHandle();
    }

    private void OnCollapseMorphCompleted()
    {
        if (_closing)
        {
            return;
        }

        // The row has fully disappeared into the handle: hide the window, restore the
        // strip placement on it while hidden, and let the handle bounce once to land it.
        // Restoring first would reset the surface and move the window while still shown.
        HideFromApp();
        _handleRevealUntil = DateTimeOffset.MinValue;
        _pointerOverHandle = false;
        _appBar?.SetFloatingOverride(null);
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

    // The anchor a reappear/yield grows from: the middle of the screen-facing edge so
    // the dock emerges inward from its resting edge (upward, rightward, or leftward).
    private Point BottomEdgeCentre(PixelRect placement)
    {
        var scale = _appBar?.Monitor is { } monitor ? monitor.Dpi / 96d : 1d;
        var (x, y) = DockEdgeRules.StripAnchorPointDip(placement, CurrentEdge, scale);
        return new Point(x, y);
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
            && (((_hiddenBySmartHide || _pointerOverHandle) && _handleAllowed) || _handleWindow.IsDragActive));
    }

    private void CreateHandleWindow()
    {
        _handleWindow = new DockHandleWindow();
        _handleWindow.SetFrostedGlass(_state.FrostedGlass);
        _handleWindow.DragStarted += () =>
        {
            _handleRevealUntil = DateTimeOffset.MinValue;
            if (_morph.IsCollapsing)
            {
                // Dragging the handle mid-collapse: finish instantly — a row still
                // flying into a handle the user is moving would fight the drag.
                CompleteSmartHideImmediately();
            }
        };
        _handleWindow.Clicked += () =>
        {
            if (_closing || _trueFullScreenForeground || _suppressedBySystemMenu)
            {
                return;
            }

            _handleRevealUntil = DateTimeOffset.UtcNow + DockAutoHideState.HandleRevealGrace;
            UpdateForForeground();
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
            var left = (rect.Left - monitor.Bounds.Left) / scale;
            var top = (rect.Top - monitor.Bounds.Top) / scale;
            if (ScreenSelection is { } selection)
            {
                var preferences = GetOrCreateScreenPreferences?.Invoke()
                    ?? selection.Preferences ?? new ScreenSurfacePreferences();
                preferences.DockHiddenHandleLeftDip = left;
                preferences.DockHiddenHandleTopDip = top;
                ScreenSelection = selection with { Preferences = preferences };
            }
            else
            {
                _state.HiddenHandleLeftDip = left;
                _state.HiddenHandleTopDip = top;
            }
            DockStateChanged?.Invoke();
        };
        _handleWindow.FileDragEntered += () =>
        {
            if (!_closing)
            {
                UpdateForForeground();
            }
        };
        _handleWindow.ExternalDrop += OnDropOnSurface;
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
        var savedLeft = ScreenSelection?.Preferences?.DockHiddenHandleLeftDip ?? _state.HiddenHandleLeftDip;
        var savedTop = ScreenSelection?.Preferences?.DockHiddenHandleTopDip ?? _state.HiddenHandleTopDip;
        if (savedLeft is { } left && savedTop is { } top)
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
                edgeGapPixels,
                CurrentEdge);
        }

        if (!_handleWindow.IsDragActive)
        {
            _handleWindow.MoveTo(handleRect);
        }
    }

    private bool ForegroundWindowRect(out PixelRect bounds, out nint window)
    {
        bounds = default;
        var currentForeground = DockNativeMethods.GetForegroundWindow();
        window = ResolvePanelForeground?.Invoke(currentForeground) ?? currentForeground;
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
                .Append(item.IsInRunningSection ? 'S' : '-')
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
                .Append(item.IsInRunningSection ? 'S' : '-')
                .Append(item.IsRunning ? 'R' : '-')
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
