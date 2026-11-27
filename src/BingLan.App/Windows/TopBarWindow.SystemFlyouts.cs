using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.App.TopBar;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.TopBar;
using Microsoft.Win32;

namespace BingLan.App.Windows;

/// <summary>
/// Independent macOS-style system control flyouts for Volume, Battery and Network,
/// anchored directly below their TopBar buttons so users never get jolted by the
/// bottom-right Windows taskbar Quick Settings panel.
/// </summary>
public partial class TopBarWindow
{
    private const string GlyphSpeaker = "\uE767";
    private const string GlyphHeadphone = "\uE7F6";
    private const string GlyphBrightness = "\uE706";
    private const string GlyphLock = "\uE72E";
    private const string GlyphRefresh = "\uE72C";

    private Popup? _volumeFlyout;
    private Popup? _batteryFlyout;
    private Popup? _networkFlyout;
    private int _volumeFlyoutGeneration;
    private int _batteryFlyoutGeneration;
    private int _networkFlyoutGeneration;
    private int _networkActionGeneration;
    private int _networkBusyCount;
    private int _masterVolumeWriteVersion;
    private int _masterMuteWriteVersion;
    private int _brightnessWriteVersion;
    private int _sessionVolumeWriteVersion;
    private int _wheelVolumeWriteVersion;

    internal Popup? VolumeFlyout => _volumeFlyout;
    internal Popup? BatteryFlyout => _batteryFlyout;
    internal Popup? NetworkFlyout => _networkFlyout;

    private bool HasOpenSystemFlyout =>
        _volumeFlyout?.IsOpen == true
        || _batteryFlyout?.IsOpen == true
        || _networkFlyout?.IsOpen == true;

    private bool HasAnySystemFlyoutRef =>
        _volumeFlyout is not null
        || _batteryFlyout is not null
        || _networkFlyout is not null;

    private bool IsSystemFlyoutBusy => _networkBusyCount > 0;

    private bool HasAnyOpenFlyout =>
        HasOpenSystemFlyout
        || _todoFlyout?.IsOpen == true
        || _clockFlyout?.IsOpen == true
        || _inputMethodFlyout?.IsOpen == true;

    private void CloseAllHoverToolTips()
    {
        foreach (var element in _modules.Values)
        {
            if (element.ToolTip is ToolTip tip && tip.IsOpen)
            {
                tip.IsOpen = false;
            }
        }
        if (_searchButton?.ToolTip is ToolTip searchTip && searchTip.IsOpen)
        {
            searchTip.IsOpen = false;
        }
    }

    private void CloseSystemFlyouts()
    {
        CloseVolumeFlyout();
        CloseBatteryFlyout();
        CloseNetworkFlyout();
    }

    private void CloseVolumeFlyout()
    {
        _volumeFlyoutGeneration++;
        _volumeFlyout?.SetCurrentValue(Popup.IsOpenProperty, false);
        _volumeFlyout = null;
        UpdateForegroundTimerInterval();
    }

    private void CloseBatteryFlyout()
    {
        _batteryFlyoutGeneration++;
        _batteryFlyout?.SetCurrentValue(Popup.IsOpenProperty, false);
        _batteryFlyout = null;
        UpdateForegroundTimerInterval();
    }

    private void CloseNetworkFlyout()
    {
        _networkFlyoutGeneration++;
        _networkActionGeneration++;
        _networkFlyout?.SetCurrentValue(Popup.IsOpenProperty, false);
        _networkFlyout = null;
        UpdateForegroundTimerInterval();
    }

    private void TryCloseSystemFlyoutsOnOutsideClick(DockNativeMethods.NativePoint cursor)
    {
        TryCloseOnOutsideClick(_volumeFlyout, cursor);
        TryCloseOnOutsideClick(_batteryFlyout, cursor);
        TryCloseOnOutsideClick(_networkFlyout, cursor);
    }

    private TopBarModuleKind? ResolveSystemFlyoutOwner(Popup flyout) =>
        flyout == _volumeFlyout ? TopBarModuleKind.Volume
        : flyout == _batteryFlyout ? TopBarModuleKind.Battery
        : flyout == _networkFlyout ? TopBarModuleKind.Network
        : null;

    private void AttachVolumeQuickWheel(Button button)
    {
        button.PreviewMouseWheel += (_, args) =>
        {
            if (_facts?.Volume is not { } current)
            {
                return;
            }
            args.Handled = true;
            var nextPercent = TopBarControlRules.AdjustVolumeByWheelDelta(current.Percent, args.Delta);
            var nextMuted = nextPercent == 0 && current.Muted;
            ApplyImmediateVolume(nextPercent, nextMuted);
            var writeId = ++_wheelVolumeWriteVersion;
            _ = _environment.SetMasterVolumeAsync(nextPercent).ContinueWith(_ =>
            {
                if (!_closing && writeId == _wheelVolumeWriteVersion)
                {
                    RefreshSystemFacts();
                }
            }, TaskScheduler.Default);
        };
        button.PreviewMouseDown += (_, args) =>
        {
            if (args.ChangedButton != MouseButton.Middle || _facts?.Volume is not { } current)
            {
                return;
            }
            args.Handled = true;
            var nextMuted = !current.Muted;
            ApplyImmediateVolume(current.Percent, nextMuted);
            _ = _environment.SetMasterMuteAsync(nextMuted).ContinueWith(_ =>
            {
                if (!_closing)
                {
                    RefreshSystemFacts();
                }
            }, TaskScheduler.Default);
        };
    }

    private void ApplyImmediateVolume(int percent, bool muted)
    {
        var clamped = TopBarControlRules.ClampPercent(percent);
        if (_facts is { } current)
        {
            _facts = current with { Volume = (clamped, muted) };
            RenderSystemModules();
        }
    }

    private sealed record SystemFlyoutLook(
        TopBarFlyoutPalette Palette,
        Color GlassTint,
        Color FallbackSurface,
        Color BorderColor,
        string AccentHex,
        bool AllowBackdropBlur,
        bool UseTextShadow);

    private TopBarSearchPalette ResolveTopBarPopupPalette() =>
        TopBarSearchLookRules.Resolve(
            _style,
            launcherAppearance: null,
            topBar: _state,
            useClearGlass: _environment.LauncherAppearance.UseClearGlass);

    private SystemFlyoutLook ResolveSystemFlyoutLook(bool highContrast)
    {
        var accentHex = WidgetAppearanceRules.CoerceColorOrDefault(_style.AccentColor, DesktopStyleRules.DefaultAccentColor);
        var unified = ResolveTopBarPopupPalette();
        var allowBlur = !highContrast && unified.AllowBackdropBlur;
        var glassTint = ParseColorOrDefault(unified.GlassTint, Color.FromArgb(0xB8, 0xEC, 0xF6, 0xFF));
        var fallbackSurface = ParseColorOrDefault(unified.FallbackSurface, Color.FromArgb(0xE8, 0xEC, 0xF6, 0xFF));
        var borderColor = ParseColorOrDefault(unified.Border, Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF));
        var activeBadgeBackground = unified.UseDarkInk ? "#82B9E1FA" : "#44FFFFFF";
        var flyoutPalette = new TopBarFlyoutPalette(
            UseDarkInk: unified.UseDarkInk,
            Background: unified.FallbackSurface,
            Border: unified.Border,
            Foreground: unified.Foreground,
            Divider: unified.Divider,
            BadgeBackground: unified.BadgeBackground,
            ActiveBadgeBackground: activeBadgeBackground,
            ActiveRowBackground: unified.ActiveRowBackground,
            HoverRowBackground: unified.HoverRowBackground);
        return new SystemFlyoutLook(
            flyoutPalette,
            glassTint,
            fallbackSurface,
            borderColor,
            accentHex,
            allowBlur,
            UseTextShadow: !highContrast && unified.UseTextShadow);
    }

    private static void ApplyFlyoutContentShadow(Border card, SystemFlyoutLook look)
    {
        if (!look.UseTextShadow)
        {
            return;
        }
        void Apply()
        {
            if (card.Child is UIElement content && content.Effect is null)
            {
                var shadow = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    Opacity = 0.65d,
                    BlurRadius = 8d,
                    ShadowDepth = 1d,
                    Direction = 270d
                };
                shadow.Freeze();
                content.Effect = shadow;
            }
        }
        Apply();
        card.Loaded += (_, _) => Apply();
    }

    private static Color ParseColorOrDefault(string hex, Color fallback)
    {
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color c)
            {
                return c;
            }
        }
        catch
        {
        }
        return fallback;
    }

    private static string FormatArgb(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private sealed class PopupGlassBackdrop : IDisposable
    {
        private readonly Popup _popup;
        private readonly Border _card;
        private readonly SystemFlyoutLook _look;
        private HwndSource? _hookedSource;
        private DockCompositionBackdrop? _backdrop;
        private bool _disposed;
        private bool _failed;
        private bool _usingGlassFill;

        internal PopupGlassBackdrop(Popup popup, Border card, SystemFlyoutLook look)
        {
            _popup = popup;
            _card = card;
            _look = look;
            _popup.Opened += OnTrigger;
            _card.Loaded += OnTrigger;
            _card.SizeChanged += OnTrigger;
            _popup.Closed += OnClosed;
        }

        private void OnTrigger(object? sender, EventArgs e)
        {
            Sync(reorder: true);
            _card.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Sync(reorder: true));
        }

        private nint OnPopupWindowMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
        {
            if (msg is NativeMethods.WmWindowPosChanged or DockNativeMethods.WmDpiChanged)
            {
                Sync(reorder: msg == DockNativeMethods.WmDpiChanged);
            }
            return 0;
        }

        private void AttachHook(HwndSource source)
        {
            if (ReferenceEquals(_hookedSource, source))
            {
                return;
            }
            DetachHook();
            _hookedSource = source;
            _hookedSource.AddHook(OnPopupWindowMessage);
        }

        private void DetachHook()
        {
            if (_hookedSource is not null)
            {
                _hookedSource.RemoveHook(OnPopupWindowMessage);
                _hookedSource = null;
            }
        }

        private void OnClosed(object? sender, EventArgs e) => Dispose();

        internal void Sync(bool reorder = false)
        {
            if (_disposed || _failed || !_popup.IsOpen || !_look.AllowBackdropBlur)
            {
                return;
            }

            if (!DockGlassRules.ShouldUseCompositionBackdrop(
                    OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000),
                    frostedGlassEnabled: true,
                    transparencyEnabled: ReadTransparencyEnabled(),
                    batterySaverActive: ReadBatterySaverActive(),
                    highContrastEnabled: AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast,
                    surfaceAlpha: _look.GlassTint.A))
            {
                ReleaseBackdrop();
                return;
            }

            if (!_card.IsLoaded || _card.ActualWidth <= 0 || _card.ActualHeight <= 0)
            {
                return;
            }

            if (PresentationSource.FromVisual(_card) is not HwndSource source || source.Handle == 0 || source.CompositionTarget is null)
            {
                return;
            }

            AttachHook(source);

            if (!DockNativeMethods.GetWindowRect(source.Handle, out var native))
            {
                return;
            }

            var bounds = new PixelRect(native.Left, native.Top, native.Right, native.Bottom);
            if (!bounds.HasArea)
            {
                return;
            }

            try
            {
                var dpi = source.CompositionTarget.TransformToDevice;
                var rect = source.RootVisual is Visual root
                    ? _card.TransformToAncestor(root).TransformBounds(new Rect(_card.RenderSize))
                    : new Rect(_card.RenderSize);
                var radius = _card.CornerRadius.TopLeft;
                var radiusX = radius * dpi.M11;
                var radiusY = radius * dpi.M22;
                rect.Transform(dpi);

                var (backdropBounds, leftInsetPx, topInsetPx) = DockMagnificationRules.ComputeBackdropSurfaceBounds(
                    bounds,
                    (int)Math.Floor(rect.X),
                    (int)Math.Floor(rect.Y),
                    (int)Math.Ceiling(rect.Width),
                    (int)Math.Ceiling(rect.Height),
                    false);
                var visualRect = leftInsetPx > 0 || topInsetPx > 0
                    ? new Rect(rect.X - leftInsetPx, rect.Y - topInsetPx, rect.Width, rect.Height)
                    : rect;

                _backdrop ??= new DockCompositionBackdrop();
                _backdrop.Update(
                    visualRect,
                    radiusX,
                    radiusY,
                    opacity: 1.0,
                    fill: _look.GlassTint,
                    border: _look.BorderColor,
                    tintAlpha: _look.GlassTint.A);

                // Alpha 1 retains layered-window hit testing across the full card while Composition renders the blur behind it.
                if (_card.Background is not SolidColorBrush { Color.A: 1 })
                {
                    var sentinel = new SolidColorBrush(Color.FromArgb(1, _look.GlassTint.R, _look.GlassTint.G, _look.GlassTint.B));
                    sentinel.Freeze();
                    _card.Background = sentinel;
                    _card.BorderBrush = Brushes.Transparent;
                    _usingGlassFill = true;
                }

                _backdrop.Position(source.Handle, backdropBounds, reorder);
            }
            catch
            {
                _failed = true;
                ReleaseBackdrop();
            }
        }

        private void ReleaseBackdrop()
        {
            _backdrop?.Dispose();
            _backdrop = null;
            if (_usingGlassFill)
            {
                _usingGlassFill = false;
                var bg = new SolidColorBrush(_look.FallbackSurface);
                bg.Freeze();
                var border = new SolidColorBrush(_look.BorderColor);
                border.Freeze();
                _card.Background = bg;
                _card.BorderBrush = border;
            }
        }

        private static bool ReadTransparencyEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("EnableTransparency") is not int value || value != 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool ReadBatterySaverActive()
        {
            try
            {
                var status = default(TopBarNativeMethods.SystemPowerStatus);
                return TopBarNativeMethods.GetSystemPowerStatus(ref status)
                    && (status.SystemStatusFlag & 1) != 0;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _popup.Opened -= OnTrigger;
            _card.Loaded -= OnTrigger;
            _card.SizeChanged -= OnTrigger;
            _popup.Closed -= OnClosed;
            DetachHook();
            ReleaseBackdrop();
        }
    }

    private Popup CreateSystemFlyoutPopup(
        Button anchor,
        Border card,
        SystemFlyoutLook look,
        double preferredWidth,
        Action<Popup> onClosed)
    {
        var onRight = ReferenceEquals(anchor.Parent, RightModules);
        var hostPanel = anchor.Parent as Panel ?? RightModules;
        var horizontalOffset = onRight
            ? -Math.Max(0, preferredWidth - Math.Max(anchor.ActualWidth, 32))
            : -6;
        var flyout = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = horizontalOffset,
            VerticalOffset = 4,
            StaysOpen = true,
            AllowsTransparency = true,
            Child = card
        };
        PopupGlassBackdrop? glass = look.AllowBackdropBlur ? new PopupGlassBackdrop(flyout, card, look) : null;
        flyout.Closed += (_, _) =>
        {
            glass?.Dispose();
            glass = null;
            onClosed(flyout);
            if (flyout.Parent is Panel parent)
            {
                parent.Children.Remove(flyout);
            }
            UpdateForegroundTimerInterval();
        };
        hostPanel.Children.Add(flyout);
        return flyout;
    }

    private Border CreateSystemFlyoutCard(bool highContrast, SystemFlyoutLook look, double width)
    {
        var card = new Border
        {
            Width = width,
            Background = highContrast ? SystemColors.WindowBrush : FrozenBrush(look.Palette.Background),
            BorderBrush = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(look.Palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 12, 12, 10),
            SnapsToDevicePixels = true
        };
        ApplyFlyoutContentShadow(card, look);
        return card;
    }

    // ---------------------------------------------------------------- 音量控制浮层

    private async void ToggleVolumeFlyout(Button button)
    {
        if (_volumeFlyout?.IsOpen == true)
        {
            CloseVolumeFlyout();
            return;
        }

        CloseTodoFlyout(animate: false);
        CloseClockFlyout();
        CloseInputMethodFlyout();
        CloseBatteryFlyout();
        CloseNetworkFlyout();
        CloseAllHoverToolTips();

        var highContrast = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast;
        var look = ResolveSystemFlyoutLook(highContrast);
        var card = CreateSystemFlyoutCard(highContrast, look, width: 292);
        var generation = ++_volumeFlyoutGeneration;

        var task = _environment.ReadAudioSnapshotAsync();
        var completedSync = task.IsCompletedSuccessfully;
        card.Child = completedSync
            ? BuildVolumeFlyoutContent(task.Result, card, highContrast, look, generation)
            : BuildFlyoutLoadingContent("声音", highContrast, look.Palette);

        var flyout = CreateSystemFlyoutPopup(button, card, look, 292, closed =>
        {
            if (ReferenceEquals(_volumeFlyout, closed))
            {
                _volumeFlyout = null;
            }
        });
        _volumeFlyout = flyout;
        flyout.IsOpen = true;
        OnFlyoutOpened();

        if (completedSync)
        {
            return;
        }

        try
        {
            var snapshot = await task;
            if (_closing || generation != _volumeFlyoutGeneration || !ReferenceEquals(_volumeFlyout, flyout))
            {
                return;
            }
            card.Child = BuildVolumeFlyoutContent(snapshot, card, highContrast, look, generation);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StartupLog.WriteCrash("TopBarVolumeFlyout", ex);
            if (!_closing && generation == _volumeFlyoutGeneration && ReferenceEquals(_volumeFlyout, flyout))
            {
                card.Child = BuildVolumeFlyoutContent(TopBarAudioSnapshot.Unavailable, card, highContrast, look, generation);
            }
        }
    }

    private FrameworkElement BuildVolumeFlyoutContent(
        TopBarAudioSnapshot snapshot,
        Border card,
        bool highContrast,
        SystemFlyoutLook look,
        int generation)
    {
        var flyoutPalette = look.Palette;
        var inkBrush = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(flyoutPalette.Foreground);
        var accentHex = look.AccentHex;
        var mutedOpacity = highContrast ? 1.0 : 0.72;
        var root = new StackPanel();

        // Header
        var header = new DockPanel { Margin = new Thickness(4, 0, 4, 8) };
        var percentText = new TextBlock
        {
            Text = !snapshot.Available ? "不可用" : snapshot.MasterMuted ? "已静音" : $"{snapshot.MasterPercent}%",
            Foreground = inkBrush,
            Opacity = mutedOpacity,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(percentText, System.Windows.Controls.Dock.Right);
        header.Children.Add(percentText);
        header.Children.Add(new TextBlock
        {
            Text = "声音",
            Foreground = inkBrush,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        });
        root.Children.Add(header);

        if (!snapshot.Available)
        {
            root.Children.Add(new TextBlock
            {
                Text = "未检测到可用的音频输出设备",
                Foreground = inkBrush,
                Opacity = mutedOpacity,
                FontSize = 12,
                Margin = new Thickness(4, 2, 4, 8)
            });
        }
        else
        {
            var isMuted = snapshot.MasterMuted;
            var currentPercent = snapshot.MasterPercent;

            var masterRow = new Grid { Margin = new Thickness(2, 0, 2, 8) };
            masterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            masterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var muteGlyph = new TextBlock
            {
                Text = VolumeGlyphFor(currentPercent, isMuted),
                FontFamily = StatusIconFont,
                FontSize = 14,
                Foreground = inkBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var muteButton = CreateFlyoutIconButton(
                highContrast,
                flyoutPalette,
                muteGlyph,
                size: 30,
                automationName: isMuted ? "取消静音" : "静音");
            muteButton.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(muteButton, 0);
            masterRow.Children.Add(muteButton);

            var slider = CreateFlyoutSlider(
                currentPercent,
                highContrast,
                flyoutPalette,
                accentHex,
                automationName: "主音量");
            Grid.SetColumn(slider, 1);
            masterRow.Children.Add(slider);

            var syncing = false;
            muteButton.Click += async (_, _) =>
            {
                var targetMuted = !isMuted;
                isMuted = targetMuted;
                muteGlyph.Text = VolumeGlyphFor(currentPercent, isMuted);
                percentText.Text = isMuted ? "已静音" : $"{currentPercent}%";
                AutomationProperties.SetName(muteButton, isMuted ? "取消静音" : "静音");
                ApplyImmediateVolume(currentPercent, isMuted);
                var writeId = ++_masterMuteWriteVersion;
                var applied = await _environment.SetMasterMuteAsync(targetMuted);
                if (_closing) return;
                if (writeId != _masterMuteWriteVersion) return;
                RefreshSystemFacts();
                if (!applied && generation == _volumeFlyoutGeneration && _volumeFlyout?.IsOpen == true)
                {
                    var refreshed = await _environment.ReadAudioSnapshotAsync();
                    if (!_closing && writeId == _masterMuteWriteVersion && generation == _volumeFlyoutGeneration && _volumeFlyout?.IsOpen == true)
                    {
                        card.Child = BuildVolumeFlyoutContent(refreshed, card, highContrast, look, generation);
                    }
                }
            };
            slider.ValueChanged += (_, args) =>
            {
                if (syncing) return;
                var next = TopBarControlRules.ClampPercent((int)Math.Round(args.NewValue));
                if (next == currentPercent && !isMuted) return;
                currentPercent = next;
                if (next > 0 && isMuted)
                {
                    isMuted = false;
                    AutomationProperties.SetName(muteButton, "静音");
                }
                muteGlyph.Text = VolumeGlyphFor(currentPercent, isMuted);
                percentText.Text = isMuted ? "已静音" : $"{currentPercent}%";
                ApplyImmediateVolume(currentPercent, isMuted);
                var writeId = ++_masterVolumeWriteVersion;
                _ = _environment.SetMasterVolumeAsync(currentPercent).ContinueWith(_ =>
                {
                    if (!_closing && writeId == _masterVolumeWriteVersion)
                    {
                        RefreshSystemFacts();
                    }
                }, TaskScheduler.Default);
            };
            root.Children.Add(masterRow);

            // Output devices
            if (snapshot.Devices.Count > 0)
            {
                root.Children.Add(CreateFlyoutDivider(highContrast, flyoutPalette));
                root.Children.Add(CreateFlyoutSectionLabel("输出设备", inkBrush, mutedOpacity));

                var deviceList = new StackPanel();
                foreach (var device in snapshot.Devices)
                {
                    var captured = device;
                    var rowGrid = new Grid();
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });

                    var rowInk = highContrast && device.IsDefault ? SystemColors.HighlightTextBrush : inkBrush;
                    var icon = new TextBlock
                    {
                        Text = LooksLikeHeadphone(device.Name) ? GlyphHeadphone : GlyphSpeaker,
                        FontFamily = StatusIconFont,
                        FontSize = 13,
                        Foreground = rowInk,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(icon, 0);
                    rowGrid.Children.Add(icon);

                    var label = new TextBlock
                    {
                        Text = device.Name,
                        Foreground = rowInk,
                        FontSize = 12,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(4, 0, 6, 0)
                    };
                    Grid.SetColumn(label, 1);
                    rowGrid.Children.Add(label);

                    if (device.IsDefault)
                    {
                        var check = new TextBlock
                        {
                            Text = "\uE73E",
                            FontFamily = StatusIconFont,
                            FontSize = 11,
                            Foreground = rowInk,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        Grid.SetColumn(check, 2);
                        rowGrid.Children.Add(check);
                    }

                    var deviceButton = CreateInputMethodActionButton(
                        highContrast,
                        isActive: device.IsDefault,
                        flyoutPalette,
                        rowGrid,
                        automationName: $"切换输出设备 {device.Name}");
                    deviceButton.Click += async (_, _) =>
                    {
                        if (captured.IsDefault) return;
                        var switched = await _environment.SetDefaultOutputDeviceAsync(captured.Id);
                        if (!switched || _closing || generation != _volumeFlyoutGeneration) return;
                        RefreshSystemFacts();
                        var refreshed = await _environment.ReadAudioSnapshotAsync();
                        if (!_closing && generation == _volumeFlyoutGeneration && _volumeFlyout?.IsOpen == true)
                        {
                            card.Child = BuildVolumeFlyoutContent(refreshed, card, highContrast, look, generation);
                        }
                    };
                    deviceList.Children.Add(deviceButton);
                }

                if (snapshot.Devices.Count > 4)
                {
                    root.Children.Add(CreateFlyoutScrollViewer(deviceList, maxHeight: 148, inkBrush, highContrast));
                }
                else
                {
                    root.Children.Add(deviceList);
                }
            }

            // Per-app audio sessions
            if (snapshot.Sessions.Count > 0)
            {
                root.Children.Add(CreateFlyoutDivider(highContrast, flyoutPalette));
                root.Children.Add(CreateFlyoutSectionLabel("应用音量", inkBrush, mutedOpacity));

                var sessionPanel = new StackPanel { Margin = new Thickness(0, 0, 8, 2) };
                foreach (var session in snapshot.Sessions)
                {
                    sessionPanel.Children.Add(CreateAudioSessionRow(
                        snapshot.DefaultDeviceId, session, highContrast, flyoutPalette, inkBrush, accentHex, mutedOpacity));
                }

                root.Children.Add(CreateFlyoutScrollViewer(sessionPanel, maxHeight: 176, inkBrush, highContrast));
            }
        }

        root.Children.Add(CreateFlyoutDivider(highContrast, flyoutPalette));
        root.Children.Add(CreateFlyoutFooterButton(
            "声音设置…",
            "打开 Windows 声音设置",
            highContrast,
            flyoutPalette,
            inkBrush,
            () =>
            {
                CloseVolumeFlyout();
                _environment.OpenSystemSettings("ms-settings:sound");
            }));
        return root;
    }

    private FrameworkElement CreateAudioSessionRow(
        string? deviceId,
        TopBarAudioSession session,
        bool highContrast,
        TopBarFlyoutPalette flyoutPalette,
        Brush inkBrush,
        string accentHex,
        double mutedOpacity)
    {
        var pid = session.ProcessId;
        var sessionPercent = session.VolumePercent;
        var sessionMuted = session.IsMuted;

        var container = new StackPanel { Margin = new Thickness(4, 3, 4, 5) };
        var topRow = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        var pctLabel = new TextBlock
        {
            Text = sessionMuted ? "静音" : $"{sessionPercent}%",
            Foreground = inkBrush,
            Opacity = mutedOpacity,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(pctLabel, System.Windows.Controls.Dock.Right);
        topRow.Children.Add(pctLabel);
        topRow.Children.Add(new TextBlock
        {
            Text = session.DisplayName,
            Foreground = inkBrush,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        container.Children.Add(topRow);

        var controlRow = new Grid();
        controlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var glyph = new TextBlock
        {
            Text = VolumeGlyphFor(sessionPercent, sessionMuted),
            FontFamily = StatusIconFont,
            FontSize = 12,
            Foreground = inkBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var muteBtn = CreateFlyoutIconButton(
            highContrast,
            flyoutPalette,
            glyph,
            size: 24,
            automationName: $"{session.DisplayName} 静音切换");
        muteBtn.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(muteBtn, 0);
        controlRow.Children.Add(muteBtn);

        var slider = CreateFlyoutSlider(
            sessionPercent,
            highContrast,
            flyoutPalette,
            accentHex,
            automationName: $"{session.DisplayName} 音量");
        Grid.SetColumn(slider, 1);
        controlRow.Children.Add(slider);

        muteBtn.Click += (_, _) =>
        {
            sessionMuted = !sessionMuted;
            glyph.Text = VolumeGlyphFor(sessionPercent, sessionMuted);
            pctLabel.Text = sessionMuted ? "静音" : $"{sessionPercent}%";
            _ = _environment.SetAudioSessionMuteAsync(deviceId, pid, sessionMuted);
        };
        slider.ValueChanged += (_, args) =>
        {
            var next = TopBarControlRules.ClampPercent((int)Math.Round(args.NewValue));
            if (next == sessionPercent && !sessionMuted) return;
            sessionPercent = next;
            if (next > 0 && sessionMuted) sessionMuted = false;
            glyph.Text = VolumeGlyphFor(sessionPercent, sessionMuted);
            pctLabel.Text = sessionMuted ? "静音" : $"{sessionPercent}%";
            _ = ++_sessionVolumeWriteVersion;
            _ = _environment.SetAudioSessionVolumeAsync(deviceId, pid, sessionPercent);
        };

        container.Children.Add(controlRow);
        return container;
    }

    // ---------------------------------------------------------------- 电量与电源浮层

    private async void ToggleBatteryFlyout(Button button)
    {
        if (_batteryFlyout?.IsOpen == true)
        {
            CloseBatteryFlyout();
            return;
        }

        CloseTodoFlyout(animate: false);
        CloseClockFlyout();
        CloseInputMethodFlyout();
        CloseVolumeFlyout();
        CloseNetworkFlyout();
        CloseAllHoverToolTips();

        var highContrast = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast;
        var look = ResolveSystemFlyoutLook(highContrast);
        var card = CreateSystemFlyoutCard(highContrast, look, width: 286);
        var generation = ++_batteryFlyoutGeneration;

        var task = _environment.ReadBatterySnapshotAsync();
        var completedSync = task.IsCompletedSuccessfully;
        card.Child = completedSync
            ? BuildBatteryFlyoutContent(task.Result, card, highContrast, look, generation)
            : BuildFlyoutLoadingContent("电池与电源", highContrast, look.Palette);

        var flyout = CreateSystemFlyoutPopup(button, card, look, 286, closed =>
        {
            if (ReferenceEquals(_batteryFlyout, closed))
            {
                _batteryFlyout = null;
            }
        });
        _batteryFlyout = flyout;
        flyout.IsOpen = true;
        OnFlyoutOpened();

        if (completedSync)
        {
            return;
        }

        try
        {
            var snapshot = await task;
            if (_closing || generation != _batteryFlyoutGeneration || !ReferenceEquals(_batteryFlyout, flyout))
            {
                return;
            }
            card.Child = BuildBatteryFlyoutContent(snapshot, card, highContrast, look, generation);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StartupLog.WriteCrash("TopBarBatteryFlyout", ex);
            if (!_closing && generation == _batteryFlyoutGeneration && ReferenceEquals(_batteryFlyout, flyout))
            {
                var fallback = new TopBarBatterySnapshot(-1, TopBarBatteryStatus.Unknown, false, null, false, TopBarPowerMode.Unknown, false, 0);
                card.Child = BuildBatteryFlyoutContent(fallback, card, highContrast, look, generation);
            }
        }
    }

    private FrameworkElement BuildBatteryFlyoutContent(
        TopBarBatterySnapshot snapshot,
        Border card,
        bool highContrast,
        SystemFlyoutLook look,
        int generation)
    {
        var flyoutPalette = look.Palette;
        var inkBrush = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(flyoutPalette.Foreground);
        var accentHex = look.AccentHex;
        var mutedOpacity = highContrast ? 1.0 : 0.72;
        var hasBattery = snapshot.Percent >= 0;
        var root = new StackPanel();

        var header = new DockPanel { Margin = new Thickness(4, 0, 4, 8) };
        var statusBadge = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 2, 6, 2),
            Background = highContrast ? SystemColors.ControlBrush : FrozenBrush(flyoutPalette.BadgeBackground),
            Child = new TextBlock
            {
                Text = TopBarControlRules.FormatBatteryHeadline(snapshot.Status, snapshot.Percent, snapshot.IsPowerSaver),
                Foreground = highContrast ? SystemColors.ControlTextBrush : inkBrush,
                FontSize = 11,
                FontWeight = FontWeights.Medium
            }
        };
        DockPanel.SetDock(statusBadge, System.Windows.Controls.Dock.Right);
        header.Children.Add(statusBadge);
        header.Children.Add(new TextBlock
        {
            Text = hasBattery ? "电池" : "电源",
            Foreground = inkBrush,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        });
        root.Children.Add(header);

        // Hero battery summary
        if (hasBattery)
        {
            var hero = new StackPanel { Margin = new Thickness(4, 0, 4, 10) };
            var percentRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var glyph = new TextBlock
            {
                Text = BatteryGlyph(snapshot.Percent, snapshot.Status == TopBarBatteryStatus.Charging),
                FontFamily = StatusIconFont,
                FontSize = 20,
                Foreground = inkBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            DockPanel.SetDock(glyph, System.Windows.Controls.Dock.Left);
            percentRow.Children.Add(glyph);
            percentRow.Children.Add(new TextBlock
            {
                Text = $"{snapshot.Percent}%",
                Foreground = inkBrush,
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            hero.Children.Add(percentRow);

            var fillColor = snapshot.Status == TopBarBatteryStatus.Charging
                ? Color.FromRgb(0x34, 0xC7, 0x59)
                : snapshot.Percent <= 20
                    ? Color.FromRgb(0xFF, 0x9F, 0x0A)
                    : ParseColorOrDefault(accentHex, Color.FromRgb(0x1E, 0xA9, 0xFF));
            var barTrack = new Border
            {
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = highContrast ? SystemColors.ControlBrush : FrozenBrush(flyoutPalette.BadgeBackground),
                ClipToBounds = true,
                Margin = new Thickness(0, 0, 0, 6)
            };
            var barGrid = new Grid();
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(snapshot.Percent, 2), GridUnitType.Star) });
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(100 - snapshot.Percent, 0), GridUnitType.Star) });
            var fill = new Border
            {
                CornerRadius = new CornerRadius(3),
                Background = highContrast ? SystemColors.HighlightBrush : new SolidColorBrush(fillColor)
            };
            Grid.SetColumn(fill, 0);
            barGrid.Children.Add(fill);
            barTrack.Child = barGrid;
            hero.Children.Add(barTrack);

            hero.Children.Add(new TextBlock
            {
                Text = TopBarControlRules.FormatBatteryRemaining(
                    snapshot.Status,
                    snapshot.Percent,
                    snapshot.RemainingSeconds,
                    snapshot.IsPowerSaver),
                Foreground = inkBrush,
                Opacity = mutedOpacity,
                FontSize = 12
            });
            root.Children.Add(hero);
        }
        else
        {
            root.Children.Add(new TextBlock
            {
                Text = TopBarControlRules.FormatBatteryRemaining(
                    snapshot.Status,
                    snapshot.Percent,
                    snapshot.RemainingSeconds,
                    snapshot.IsPowerSaver),
                Foreground = inkBrush,
                Opacity = mutedOpacity,
                FontSize = 12,
                Margin = new Thickness(4, 0, 4, 8)
            });
        }

        // Power mode segmented selector
        if (snapshot.PowerModeSupported)
        {
            root.Children.Add(CreateFlyoutDivider(highContrast, flyoutPalette));
            root.Children.Add(CreateFlyoutSectionLabel("电源模式", inkBrush, mutedOpacity));

            var segmentBorder = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = highContrast ? SystemColors.ControlBrush : FrozenBrush(flyoutPalette.BadgeBackground),
                Padding = new Thickness(3),
                Margin = new Thickness(2, 2, 2, 6)
            };
            var segmentGrid = new Grid();
            segmentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            segmentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            segmentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            TopBarPowerMode[] modes =
            [
                TopBarPowerMode.BestPowerEfficiency,
                TopBarPowerMode.Balanced,
                TopBarPowerMode.BestPerformance
            ];
            for (var i = 0; i < modes.Length; i++)
            {
                var mode = modes[i];
                var active = snapshot.PowerMode == mode;
                var label = TopBarControlRules.PowerModeLabel(mode);
                var pill = CreateInputMethodActionButton(
                    highContrast,
                    isActive: active,
                    flyoutPalette,
                    new TextBlock
                    {
                        Text = label,
                        Foreground = highContrast && active ? SystemColors.HighlightTextBrush : inkBrush,
                        FontSize = 12,
                        FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    automationName: $"电源模式 {label}");
                pill.MinHeight = 28;
                pill.Margin = new Thickness(1, 0, 1, 0);
                pill.Click += async (_, _) =>
                {
                    if (active) return;
                    var applied = await _environment.SetPowerModeAsync(mode);
                    if (!applied || _closing || generation != _batteryFlyoutGeneration) return;
                    var refreshed = await _environment.ReadBatterySnapshotAsync();
                    if (!_closing && generation == _batteryFlyoutGeneration && _batteryFlyout?.IsOpen == true)
                    {
                        card.Child = BuildBatteryFlyoutContent(refreshed, card, highContrast, look, generation);
                    }
                };
                Grid.SetColumn(pill, i);
                segmentGrid.Children.Add(pill);
            }

            segmentBorder.Child = segmentGrid;
            root.Children.Add(segmentBorder);
        }

        // Built-in laptop display brightness
        if (snapshot.BrightnessSupported)
        {
            root.Children.Add(CreateFlyoutDivider(highContrast, flyoutPalette));
            var brightHeader = new DockPanel { Margin = new Thickness(4, 2, 4, 4) };
            var brightPct = new TextBlock
            {
                Text = $"{snapshot.BrightnessPercent}%",
                Foreground = inkBrush,
                Opacity = mutedOpacity,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(brightPct, System.Windows.Controls.Dock.Right);
            brightHeader.Children.Add(brightPct);
            brightHeader.Children.Add(new TextBlock
            {
                Text = "屏幕亮度",
                Foreground = inkBrush,
                Opacity = mutedOpacity,
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            });
            root.Children.Add(brightHeader);

            var brightRow = new Grid { Margin = new Thickness(4, 0, 4, 6) };
            brightRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            brightRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var sun = new TextBlock
            {
                Text = GlyphBrightness,
                FontFamily = StatusIconFont,
                FontSize = 14,
                Foreground = inkBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(sun, 0);
            brightRow.Children.Add(sun);

            var currentBright = snapshot.BrightnessPercent;
            var brightSlider = CreateFlyoutSlider(
                currentBright,
                highContrast,
                flyoutPalette,
                accentHex,
                automationName: "屏幕亮度");
            brightSlider.ValueChanged += (_, args) =>
            {
                var next = TopBarControlRules.ClampPercent((int)Math.Round(args.NewValue));
                if (next == currentBright) return;
                currentBright = next;
                brightPct.Text = $"{currentBright}%";
                _ = ++_brightnessWriteVersion;
                _ = _environment.SetBuiltInBrightnessAsync(currentBright);
            };
            Grid.SetColumn(brightSlider, 1);
            brightRow.Children.Add(brightSlider);
            root.Children.Add(brightRow);
        }

        root.Children.Add(CreateFlyoutDivider(highContrast, flyoutPalette));
        root.Children.Add(CreateFlyoutFooterButton(
            "电源与电池设置…",
            "打开 Windows 电源与电池设置",
            highContrast,
            flyoutPalette,
            inkBrush,
            () =>
            {
                CloseBatteryFlyout();
                _environment.OpenSystemSettings("ms-settings:powersleep");
            }));
        return root;
    }

    // ---------------------------------------------------------------- 网络与 Wi-Fi 浮层

    private async void ToggleNetworkFlyout(Button button)
    {
        if (_networkFlyout?.IsOpen == true)
        {
            CloseNetworkFlyout();
            return;
        }

        CloseTodoFlyout(animate: false);
        CloseClockFlyout();
        CloseInputMethodFlyout();
        CloseVolumeFlyout();
        CloseBatteryFlyout();
        CloseAllHoverToolTips();

        var highContrast = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast;
        var look = ResolveSystemFlyoutLook(highContrast);
        var card = CreateSystemFlyoutCard(highContrast, look, width: 300);
        var generation = ++_networkFlyoutGeneration;

        var task = _environment.ReadNetworkSnapshotAsync();
        var completedSync = task.IsCompletedSuccessfully;
        card.Child = completedSync
            ? BuildNetworkFlyoutContent(task.Result, card, highContrast, look.Palette, generation, statusNotice: null)
            : BuildFlyoutLoadingContent("网络", highContrast, look.Palette);

        var flyout = CreateSystemFlyoutPopup(button, card, look, 300, closed =>
        {
            if (ReferenceEquals(_networkFlyout, closed))
            {
                _networkFlyout = null;
            }
        });
        _networkFlyout = flyout;
        flyout.IsOpen = true;
        OnFlyoutOpened();

        if (completedSync)
        {
            return;
        }

        try
        {
            var snapshot = await task;
            if (_closing || generation != _networkFlyoutGeneration || !ReferenceEquals(_networkFlyout, flyout))
            {
                return;
            }
            card.Child = BuildNetworkFlyoutContent(snapshot, card, highContrast, look.Palette, generation, statusNotice: null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StartupLog.WriteCrash("TopBarNetworkFlyout", ex);
            if (!_closing && generation == _networkFlyoutGeneration && ReferenceEquals(_networkFlyout, flyout))
            {
                card.Child = BuildNetworkFlyoutContent(TopBarNetworkSnapshot.Disconnected, card, highContrast, look.Palette, generation, statusNotice: null);
            }
        }
    }

    private FrameworkElement BuildNetworkFlyoutContent(
        TopBarNetworkSnapshot snapshot,
        Border card,
        bool highContrast,
        TopBarFlyoutPalette flyoutPalette,
        int generation,
        string? statusNotice)
    {
        var inkBrush = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(flyoutPalette.Foreground);
        var mutedOpacity = highContrast ? 1.0 : 0.68;
        var root = new StackPanel();

        // Header
        var header = new DockPanel { Margin = new Thickness(4, 0, 2, 8) };
        if (snapshot.HasWifiInterface && snapshot.InterfaceGuid != Guid.Empty)
        {
            var refreshGlyph = new TextBlock
            {
                Text = GlyphRefresh,
                FontFamily = StatusIconFont,
                FontSize = 12,
                Foreground = inkBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var refreshBtn = CreateFlyoutIconButton(
                highContrast,
                flyoutPalette,
                refreshGlyph,
                size: 26,
                automationName: "刷新附近 WLAN 列表");
            refreshBtn.Click += async (_, _) =>
            {
                var actionGen = ++_networkActionGeneration;
                _networkBusyCount++;
                try
                {
                    var scanStarted = await _environment.TriggerWifiScanAsync(snapshot.InterfaceGuid);
                    if (_closing || generation != _networkFlyoutGeneration || actionGen != _networkActionGeneration) return;
                    if (!scanStarted)
                    {
                        card.Child = BuildNetworkFlyoutContent(snapshot, card, highContrast, flyoutPalette, generation, "扫描请求未成功，请稍后重试");
                        return;
                    }
                    card.Child = BuildNetworkFlyoutContent(snapshot, card, highContrast, flyoutPalette, generation, "正在扫描附近网络…");
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        await Task.Delay(1200);
                        if (_closing || generation != _networkFlyoutGeneration || actionGen != _networkActionGeneration || _networkFlyout?.IsOpen != true) return;
                        var refreshed = await _environment.ReadNetworkSnapshotAsync();
                        if (_closing || generation != _networkFlyoutGeneration || actionGen != _networkActionGeneration || _networkFlyout?.IsOpen != true) return;
                        card.Child = BuildNetworkFlyoutContent(refreshed, card, highContrast, flyoutPalette, generation, attempt < 2 ? "正在更新网络列表…" : null);
                    }
                }
                finally
                {
                    _networkBusyCount = Math.Max(0, _networkBusyCount - 1);
                }
            };
            DockPanel.SetDock(refreshBtn, System.Windows.Controls.Dock.Right);
            header.Children.Add(refreshBtn);
        }
        header.Children.Add(new TextBlock
        {
            Text = "网络",
            Foreground = inkBrush,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        });
        root.Children.Add(header);

        // Active connection card
        var connCard = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = highContrast ? SystemColors.ControlBrush : FrozenBrush(flyoutPalette.BadgeBackground),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(2, 0, 2, 8)
        };
        var connGrid = new Grid();
        connGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        connGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        connGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var connGlyph = new TextBlock
        {
            Text = !snapshot.IsConnected
                ? GlyphWifiOff
                : !snapshot.IsWireless
                    ? GlyphEthernet
                    : WifiGlyphForQuality(snapshot.SignalQuality),
            FontFamily = StatusIconFont,
            FontSize = 15,
            Foreground = highContrast ? SystemColors.ControlTextBrush : inkBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(connGlyph, 0);
        connGrid.Children.Add(connGlyph);

        var connInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        connInfo.Children.Add(new TextBlock
        {
            Text = snapshot.ConnectionTitle,
            Foreground = highContrast ? SystemColors.ControlTextBrush : inkBrush,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var detailParts = new List<string>();
        if (snapshot.IsConnected && snapshot.IsWireless && snapshot.SignalQuality is { } quality)
        {
            detailParts.Add($"信号 {quality}%");
        }
        if (TopBarControlRules.FormatLinkSpeed(snapshot.LinkSpeedBitsPerSecond) is { } speed)
        {
            detailParts.Add(speed);
        }
        if (!string.IsNullOrWhiteSpace(snapshot.Ipv4Address))
        {
            detailParts.Add(snapshot.Ipv4Address);
        }
        connInfo.Children.Add(new TextBlock
        {
            Text = !snapshot.IsConnected
                ? "当前没有活跃网络连接"
                : detailParts.Count > 0 ? string.Join(" · ", detailParts) : "已连接",
            Foreground = highContrast ? SystemColors.ControlTextBrush : inkBrush,
            Opacity = mutedOpacity,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(connInfo, 1);
        connGrid.Children.Add(connInfo);

        if (snapshot.IsConnected && snapshot.IsWireless && snapshot.InterfaceGuid != Guid.Empty)
        {
            var disconnectBtn = CreateInputMethodActionButton(
                highContrast,
                isActive: false,
                flyoutPalette,
                new TextBlock
                {
                    Text = "断开",
                    Foreground = inkBrush,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                automationName: "断开当前 WLAN 连接");
            disconnectBtn.MinHeight = 26;
            disconnectBtn.Margin = new Thickness(6, 0, 0, 0);
            disconnectBtn.Click += async (_, _) =>
            {
                var actionGen = ++_networkActionGeneration;
                _networkBusyCount++;
                try
                {
                    var ok = await _environment.DisconnectWifiAsync(snapshot.InterfaceGuid);
                    if (_closing || generation != _networkFlyoutGeneration || actionGen != _networkActionGeneration) return;
                    if (!ok)
                    {
                        card.Child = BuildNetworkFlyoutContent(snapshot, card, highContrast, flyoutPalette, generation, "断开请求未成功");
                        return;
                    }
                    card.Child = BuildNetworkFlyoutContent(snapshot, card, highContrast, flyoutPalette, generation, "正在断开连接…");
                    for (var attempt = 0; attempt < 4; attempt++)
                    {
                        await Task.Delay(500);
                        if (_closing || generation != _networkFlyoutGeneration || actionGen != _networkActionGeneration || _networkFlyout?.IsOpen != true) return;
                        RefreshSystemFacts();
                        var refreshed = await _environment.ReadNetworkSnapshotAsync();
                        if (_closing || generation != _networkFlyoutGeneration || actionGen != _networkActionGeneration || _networkFlyout?.IsOpen != true) return;
                        var done = !refreshed.IsConnected || !refreshed.IsWireless;
                        card.Child = BuildNetworkFlyoutContent(refreshed, card, highContrast, flyoutPalette, generation, done || attempt == 3 ? null : "正在断开连接…");
                        if (done) break;
                    }
                }
                finally
                {
                    _networkBusyCount = Math.Max(0, _networkBusyCount - 1);
                }
            };
            Grid.SetColumn(disconnectBtn, 2);
            connGrid.Children.Add(disconnectBtn);
        }

        connCard.Child = connGrid;
        root.Children.Add(connCard);

        if (!string.IsNullOrWhiteSpace(statusNotice))
        {
            root.Children.Add(new TextBlock
            {
                Text = statusNotice,
                Foreground = inkBrush,
                Opacity = mutedOpacity,
                FontSize = 11,
                Margin = new Thickness(6, 0, 6, 6)
            });
        }

        // Available Wi-Fi networks
        if (snapshot.HasWifiInterface)
        {
            root.Children.Add(CreateFlyoutSectionLabel("WLAN 网络", inkBrush, mutedOpacity));
            if (snapshot.WifiListAccessDenied)
            {
                var noticeBox = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Background = highContrast ? SystemColors.ControlBrush : FrozenBrush(flyoutPalette.BadgeBackground),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(2, 2, 2, 4)
                };
                var noticeStack = new StackPanel();
                noticeStack.Children.Add(new TextBlock
                {
                    Text = "系统未开启桌面应用位置权限，无法列出附近 WLAN。",
                    Foreground = highContrast ? SystemColors.ControlTextBrush : inkBrush,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 6)
                });
                var locBtn = CreateInputMethodActionButton(
                    highContrast,
                    isActive: true,
                    flyoutPalette,
                    new TextBlock
                    {
                        Text = "打开位置权限设置…",
                        Foreground = highContrast ? SystemColors.HighlightTextBrush : inkBrush,
                        FontSize = 12,
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    automationName: "打开 Windows 位置权限设置");
                locBtn.MinHeight = 28;
                locBtn.Click += (_, _) =>
                {
                    CloseNetworkFlyout();
                    _environment.OpenSystemSettings("ms-settings:privacy-location");
                };
                noticeStack.Children.Add(locBtn);
                noticeBox.Child = noticeStack;
                root.Children.Add(noticeBox);
            }
            else if (snapshot.AvailableNetworks.Count == 0)
            {
                root.Children.Add(new TextBlock
                {
                    Text = "附近暂无可用 WLAN 网络",
                    Foreground = inkBrush,
                    Opacity = mutedOpacity,
                    FontSize = 12,
                    Margin = new Thickness(6, 2, 6, 6)
                });
            }
            else
            {
                var wifiList = new StackPanel();
                foreach (var net in snapshot.AvailableNetworks)
                {
                    var captured = net;
                    var rowInk = highContrast && net.IsConnected ? SystemColors.HighlightTextBrush : inkBrush;
                    var rowGrid = new Grid();
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var sigIcon = new TextBlock
                    {
                        Text = WifiGlyphForQuality(net.SignalQuality),
                        FontFamily = StatusIconFont,
                        FontSize = 13,
                        Foreground = rowInk,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(sigIcon, 0);
                    rowGrid.Children.Add(sigIcon);

                    var ssidText = new TextBlock
                    {
                        Text = net.Ssid,
                        Foreground = rowInk,
                        FontSize = 12,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(4, 0, 6, 0)
                    };
                    Grid.SetColumn(ssidText, 1);
                    rowGrid.Children.Add(ssidText);

                    var trailing = new StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Horizontal,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    if (net.IsSecured && !net.IsConnected)
                    {
                        trailing.Children.Add(new TextBlock
                        {
                            Text = GlyphLock,
                            FontFamily = StatusIconFont,
                            FontSize = 10,
                            Foreground = rowInk,
                            Opacity = mutedOpacity,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 4, 0)
                        });
                    }
                    var tagText = net.IsConnected ? "已连接" : net.IsSaved ? "已保存" : "需在设置连接";
                    trailing.Children.Add(new TextBlock
                    {
                        Text = tagText,
                        Foreground = rowInk,
                        Opacity = net.IsConnected ? 1.0 : mutedOpacity,
                        FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    Grid.SetColumn(trailing, 2);
                    rowGrid.Children.Add(trailing);

                    var netBtn = CreateInputMethodActionButton(
                        highContrast,
                        isActive: net.IsConnected,
                        flyoutPalette,
                        rowGrid,
                        automationName: net.IsConnected
                            ? $"WLAN {net.Ssid} 已连接"
                            : net.IsSaved ? $"连接到已保存 WLAN {net.Ssid}" : $"在系统设置中连接 {net.Ssid}");
                    netBtn.Click += async (_, _) =>
                    {
                        if (captured.IsConnected) return;
                        if (captured.IsSaved && !string.IsNullOrWhiteSpace(captured.ProfileName) && snapshot.InterfaceGuid != Guid.Empty)
                        {
                            var actionGen = ++_networkActionGeneration;
                            _networkBusyCount++;
                            try
                            {
                                card.Child = BuildNetworkFlyoutContent(
                                    snapshot, card, highContrast, flyoutPalette, generation, $"正在连接 {captured.Ssid}…");
                                var ok = await _environment.ConnectSavedWifiAsync(snapshot.InterfaceGuid, captured.ProfileName!);
                                if (_closing || generation != _networkFlyoutGeneration || actionGen != _networkActionGeneration) return;
                                if (!ok)
                                {
                                    card.Child = BuildNetworkFlyoutContent(
                                        snapshot, card, highContrast, flyoutPalette, generation, $"连接 {captured.Ssid} 未成功，可尝试在系统设置中连接");
                                    return;
                                }
                                for (var attempt = 0; attempt < 4; attempt++)
                                {
                                    await Task.Delay(750);
                                    if (_closing || generation != _networkFlyoutGeneration || actionGen != _networkActionGeneration || _networkFlyout?.IsOpen != true) return;
                                    RefreshSystemFacts();
                                    var refreshed = await _environment.ReadNetworkSnapshotAsync();
                                    if (_closing || generation != _networkFlyoutGeneration || actionGen != _networkActionGeneration || _networkFlyout?.IsOpen != true) return;
                                    var connectedTarget = string.Equals(refreshed.CurrentSsid, captured.Ssid, StringComparison.Ordinal);
                                    card.Child = BuildNetworkFlyoutContent(
                                        refreshed, card, highContrast, flyoutPalette, generation,
                                        connectedTarget || attempt == 3 ? null : $"正在连接 {captured.Ssid}…");
                                    if (connectedTarget) break;
                                }
                            }
                            finally
                            {
                                _networkBusyCount = Math.Max(0, _networkBusyCount - 1);
                            }
                        }
                        else
                        {
                            CloseNetworkFlyout();
                            _environment.OpenSystemSettings("ms-settings:network-wifi");
                        }
                    };
                    wifiList.Children.Add(netBtn);
                }

                root.Children.Add(CreateFlyoutScrollViewer(wifiList, maxHeight: 204, inkBrush, highContrast));
            }
        }

        root.Children.Add(CreateFlyoutDivider(highContrast, flyoutPalette));
        root.Children.Add(CreateFlyoutFooterButton(
            "网络和 Wi-Fi 设置…",
            "打开 Windows 网络和 Wi-Fi 设置",
            highContrast,
            flyoutPalette,
            inkBrush,
            () =>
            {
                CloseNetworkFlyout();
                _environment.OpenSystemSettings("ms-settings:network-wifi");
            }));
        return root;
    }

    // ---------------------------------------------------------------- 共享浮层控件辅助

    private ScrollViewer CreateFlyoutScrollViewer(UIElement content, double maxHeight, Brush inkBrush, bool highContrast)
    {
        var scroller = new ScrollViewer
        {
            MaxHeight = maxHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = content
        };
        if (!highContrast && TryFindResource("CardScrollViewerStyle") is Style cardScrollStyle)
        {
            scroller.Style = cardScrollStyle;
        }
        scroller.Resources["PrimaryTextBrush"] = inkBrush;
        return scroller;
    }

    private static FrameworkElement BuildFlyoutLoadingContent(
        string title,
        bool highContrast,
        TopBarFlyoutPalette flyoutPalette)
    {
        var inkBrush = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(flyoutPalette.Foreground);
        var panel = new StackPanel { Margin = new Thickness(4, 0, 4, 4) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = inkBrush,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "正在读取系统状态…",
            Foreground = inkBrush,
            Opacity = highContrast ? 1.0 : 0.68,
            FontSize = 12
        });
        return panel;
    }

    private static Border CreateFlyoutDivider(bool highContrast, TopBarFlyoutPalette flyoutPalette) => new()
    {
        Height = 1,
        Margin = new Thickness(4, 6, 4, 6),
        Background = highContrast ? SystemColors.WindowTextBrush : FrozenBrush(flyoutPalette.Divider)
    };

    private static TextBlock CreateFlyoutSectionLabel(string text, Brush inkBrush, double opacity) => new()
    {
        Text = text,
        Foreground = inkBrush,
        Opacity = opacity,
        FontSize = 11,
        FontWeight = FontWeights.Medium,
        Margin = new Thickness(6, 2, 6, 4)
    };

    private static Button CreateFlyoutFooterButton(
        string text,
        string automationName,
        bool highContrast,
        TopBarFlyoutPalette flyoutPalette,
        Brush inkBrush,
        Action onClick)
    {
        var button = CreateInputMethodActionButton(
            highContrast,
            isActive: false,
            flyoutPalette,
            new TextBlock
            {
                Text = text,
                Foreground = inkBrush,
                Opacity = highContrast ? 1.0 : 0.86,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            },
            automationName);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static Button CreateFlyoutIconButton(
        bool highContrast,
        TopBarFlyoutPalette flyoutPalette,
        UIElement content,
        double size,
        string automationName)
    {
        var hoverBg = highContrast ? SystemColors.HighlightBrush : FrozenBrush(flyoutPalette.HoverRowBackground);
        var shellFactory = new FrameworkElementFactory(typeof(Border));
        shellFactory.Name = "Shell";
        shellFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        shellFactory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        shellFactory.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        shellFactory.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = shellFactory };
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg, "Shell"));
        template.Triggers.Add(hoverTrigger);

        var button = new Button
        {
            Template = template,
            Width = size,
            Height = size,
            Cursor = Cursors.Hand,
            Content = content
        };
        AutomationProperties.SetName(button, automationName);
        return button;
    }

    private static Slider CreateFlyoutSlider(
        int initialValue,
        bool highContrast,
        TopBarFlyoutPalette flyoutPalette,
        string accentHex,
        string automationName)
    {
        var trackBrushExpr = highContrast
            ? "{DynamicResource {x:Static SystemColors.ControlBrushKey}}"
            : flyoutPalette.UseDarkInk ? "#2E122C44" : "#34FFFFFF";
        var accentBrushExpr = highContrast
            ? "{DynamicResource {x:Static SystemColors.HighlightBrushKey}}"
            : accentHex;
        var thumbFillExpr = highContrast
            ? "{DynamicResource {x:Static SystemColors.WindowBrushKey}}"
            : "#FFFFFF";
        var thumbStrokeExpr = highContrast
            ? "{DynamicResource {x:Static SystemColors.WindowTextBrushKey}}"
            : accentHex;
        var xaml = $$"""
<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 TargetType="Slider">
  <Grid VerticalAlignment="Center">
    <Border Height="4" CornerRadius="2" Background="{{trackBrushExpr}}" VerticalAlignment="Center"/>
    <Track x:Name="PART_Track">
      <Track.DecreaseRepeatButton>
        <RepeatButton Command="Slider.DecreaseLarge">
          <RepeatButton.Template>
            <ControlTemplate TargetType="RepeatButton">
              <Border Height="4" CornerRadius="2" Background="{{accentBrushExpr}}" VerticalAlignment="Center"/>
            </ControlTemplate>
          </RepeatButton.Template>
        </RepeatButton>
      </Track.DecreaseRepeatButton>
      <Track.IncreaseRepeatButton>
        <RepeatButton Command="Slider.IncreaseLarge">
          <RepeatButton.Template>
            <ControlTemplate TargetType="RepeatButton">
              <Border Height="4" Background="Transparent" VerticalAlignment="Center"/>
            </ControlTemplate>
          </RepeatButton.Template>
        </RepeatButton>
      </Track.IncreaseRepeatButton>
      <Track.Thumb>
        <Thumb Width="14" Height="14">
          <Thumb.Template>
            <ControlTemplate TargetType="Thumb">
              <Ellipse Fill="{{thumbFillExpr}}" Stroke="{{thumbStrokeExpr}}" StrokeThickness="1.5"/>
            </ControlTemplate>
          </Thumb.Template>
        </Thumb>
      </Track.Thumb>
    </Track>
  </Grid>
</ControlTemplate>
""";
        var sliderTemplate = (ControlTemplate)XamlReader.Parse(xaml);

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = TopBarControlRules.ClampPercent(initialValue),
            SmallChange = 2,
            LargeChange = 10,
            IsMoveToPointEnabled = true,
            IsSnapToTickEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
            Height = 22,
            Cursor = Cursors.Hand,
            Template = sliderTemplate
        };
        slider.PreviewMouseWheel += (_, args) =>
        {
            args.Handled = true;
            slider.Value = TopBarControlRules.AdjustVolumeByWheelDelta((int)Math.Round(slider.Value), args.Delta);
        };
        AutomationProperties.SetName(slider, automationName);
        return slider;
    }

    private static string VolumeGlyphFor(int percent, bool muted) =>
        muted ? GlyphVolumeMuted : percent switch
        {
            <= 0 => GlyphVolume0,
            <= 33 => GlyphVolume1,
            <= 66 => GlyphVolume2,
            _ => GlyphVolume3
        };

    private static string WifiGlyphForQuality(int? quality) =>
        TopBarModuleRules.WifiSignalBars(quality) switch
        {
            1 => GlyphWifi1,
            2 => GlyphWifi2,
            3 => GlyphWifi3,
            _ => GlyphWifi
        };

    private static bool LooksLikeHeadphone(string name) =>
        name.Contains("耳机", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Headphone", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Headset", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Earbud", StringComparison.OrdinalIgnoreCase)
        || name.Contains("AirPods", StringComparison.OrdinalIgnoreCase);
}
