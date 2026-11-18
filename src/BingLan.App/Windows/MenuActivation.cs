using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BingLan.App.Interop;
using BingLan.Core.Dock;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using SystemColors = System.Windows.SystemColors;

namespace BingLan.App.Windows;

/// <summary>
/// A context menu hosted in a <c>StaysOpen</c> non-activating <see cref="Popup"/> for
/// surfaces that never activate themselves (the dock and the top bar). Standard WPF
/// <see cref="System.Windows.Controls.ContextMenu"/> relies on <c>MenuBase</c> mouse
/// capture and foreground activation, which collapses immediately on touchscreen laptops
/// (<c>WispLogic</c> promoted right-button release) or when foreground focus bounces.
/// </summary>
internal sealed class NonActivatingPopupMenu
{
    private static readonly TimeSpan OutsideWatchInterval = TimeSpan.FromMilliseconds(35);

    private readonly Popup _popup;
    private readonly Border _card;
    private readonly StackPanel _itemsPanel;
    private readonly List<Border> _itemRows = [];
    private readonly MenuDismissState _dismissState = new();
    private readonly DispatcherTimer _watchTimer;
    private readonly Brush _textBrush;
    private readonly Brush _hoverBrush;
    private readonly Brush _separatorBrush;
    private Border? _pressedRow;
    private bool _watchingInput;
    private bool _lastWasSeparator = true;
    private int _itemCount;

    internal NonActivatingPopupMenu(
        UIElement placementTarget,
        PlacementMode placement = PlacementMode.Mouse,
        bool darkSurface = false)
    {
        var highContrast = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast;
        Brush surfaceBrush;
        Brush borderBrush;
        if (highContrast)
        {
            surfaceBrush = SystemColors.MenuBrush;
            borderBrush = SystemColors.WindowFrameBrush;
            _textBrush = SystemColors.MenuTextBrush;
            _hoverBrush = SystemColors.HighlightBrush;
            _separatorBrush = SystemColors.WindowFrameBrush;
        }
        else if (darkSurface)
        {
            surfaceBrush = FrozenBrush(Color.FromRgb(0x20, 0x2C, 0x3B));
            borderBrush = FrozenBrush(Color.FromRgb(0x53, 0x64, 0x77));
            _textBrush = FrozenBrush(Color.FromRgb(0xF0, 0xF5, 0xFC));
            _hoverBrush = FrozenBrush(Color.FromArgb(0x46, 0x7A, 0xB4, 0xED));
            _separatorBrush = FrozenBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            surfaceBrush = FrozenBrush(Color.FromRgb(0xF3, 0xF7, 0xFC));
            borderBrush = FrozenBrush(Color.FromRgb(0xC6, 0xD5, 0xE5));
            _textBrush = FrozenBrush(Color.FromRgb(0x20, 0x36, 0x4D));
            _hoverBrush = FrozenBrush(Color.FromArgb(0x38, 0x55, 0x9E, 0xF3));
            _separatorBrush = FrozenBrush(Color.FromArgb(0x22, 0x1E, 0x2B, 0x3C));
        }

        _itemsPanel = new StackPanel { Orientation = Orientation.Vertical };
        var scroller = new ScrollViewer
        {
            MaxHeight = 360,
            Focusable = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _itemsPanel
        };
        _card = new Border
        {
            Background = surfaceBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            MinWidth = 184,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Child = scroller
        };
        if (System.Windows.Application.Current?.TryFindResource("UiFont") is System.Windows.Media.FontFamily uiFont)
        {
            System.Windows.Documents.TextElement.SetFontFamily(_card, uiFont);
        }
        _card.ContextMenuOpening += (_, args) => args.Handled = true;
        _card.MouseRightButtonDown += (_, args) => args.Handled = true;
        _card.MouseRightButtonUp += (_, args) => args.Handled = true;

        _popup = new Popup
        {
            PlacementTarget = placementTarget,
            Placement = placement,
            StaysOpen = true,
            AllowsTransparency = true,
            PopupAnimation = SystemParameters.MenuPopupAnimation,
            Child = _card
        };
        _watchTimer = new DispatcherTimer(
            OutsideWatchInterval,
            DispatcherPriority.Input,
            (_, _) => OnWatchTick(),
            placementTarget.Dispatcher);
        _watchTimer.Stop();

        _popup.Opened += OnPopupOpened;
        _popup.Closed += OnPopupClosed;
    }

    internal event EventHandler? Opened;

    internal event EventHandler? Closed;

    internal bool HasItems => _itemCount > 0;

    internal UIElement PlacementTarget
    {
        get => _popup.PlacementTarget;
        set => _popup.PlacementTarget = value;
    }

    internal PlacementMode Placement
    {
        get => _popup.Placement;
        set => _popup.Placement = value;
    }

    internal bool IsOpen
    {
        get => _popup.IsOpen;
        set
        {
            if (value && _itemCount == 0)
            {
                return;
            }
            _popup.IsOpen = value;
        }
    }

    internal nint PopupHandle =>
        _popup.IsOpen && PresentationSource.FromVisual(_card) is HwndSource source
            ? source.Handle
            : 0;

    internal void AddItem(string header, Action action, ImageSource? icon = null)
    {
        var label = new TextBlock
        {
            Text = header,
            Foreground = _textBrush,
            FontSize = 13,
            MaxWidth = 280,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };

        UIElement rowContent;
        if (icon is not null)
        {
            var image = new Image
            {
                Source = icon,
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var dock = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(image, System.Windows.Controls.Dock.Left);
            dock.Children.Add(image);
            dock.Children.Add(label);
            rowContent = dock;
        }
        else
        {
            rowContent = label;
        }

        var highContrast = AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast;
        var row = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(12, 6, 12, 6),
            MinHeight = 32,
            Margin = new Thickness(0, 1, 0, 1),
            Cursor = Cursors.Hand,
            SnapsToDevicePixels = true,
            Child = rowContent
        };
        AutomationProperties.SetName(row, header);

        void SetHighlighted(bool highlighted)
        {
            row.Background = highlighted ? _hoverBrush : Brushes.Transparent;
            if (highContrast)
            {
                label.Foreground = highlighted ? SystemColors.HighlightTextBrush : _textBrush;
            }
        }

        row.MouseEnter += (_, _) =>
        {
            foreach (var other in _itemRows)
            {
                if (!ReferenceEquals(other, row))
                {
                    other.Background = Brushes.Transparent;
                }
            }
            SetHighlighted(true);
        };
        row.MouseLeave += (_, _) => SetHighlighted(false);
        row.MouseLeftButtonDown += (_, args) =>
        {
            _pressedRow = row;
            SetHighlighted(true);
            args.Handled = true;
        };
        row.MouseLeftButtonUp += (_, args) =>
        {
            var matchesPress = _pressedRow is null || ReferenceEquals(_pressedRow, row);
            _pressedRow = null;
            args.Handled = true;
            if (!matchesPress)
            {
                return;
            }
            _popup.IsOpen = false;
            _popup.Dispatcher.BeginInvoke(action, DispatcherPriority.Input);
        };

        _itemRows.Add(row);
        _itemsPanel.Children.Add(row);
        _itemCount++;
        _lastWasSeparator = false;
    }

    internal void AddSeparator()
    {
        if (_itemCount == 0 || _lastWasSeparator)
        {
            return;
        }

        _itemsPanel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(8, 4, 8, 4),
            Background = _separatorBrush,
            SnapsToDevicePixels = true
        });
        _lastWasSeparator = true;
    }

    private void OnPopupOpened(object? sender, EventArgs e)
    {
        if (_lastWasSeparator && _itemsPanel.Children.Count > 0)
        {
            _itemsPanel.Children.RemoveAt(_itemsPanel.Children.Count - 1);
            _lastWasSeparator = false;
        }

        EnsurePopupNonActivating();
        StopWatching();

        var mouseHeld = ShellSurfaceNativeMethods.IsAnyMouseButtonPressed();
        var escapeHeld = TopBarNativeMethods.IsKeyCurrentlyDown(TopBarNativeMethods.EscapeKey);
        if (!mouseHeld)
        {
            ConsumeMouseLatches();
        }
        if (!escapeHeld)
        {
            TopBarNativeMethods.ConsumeMouseButtonDown(TopBarNativeMethods.EscapeKey);
        }
        _dismissState.Start(DockNativeMethods.GetForegroundWindow(), mouseHeld, escapeHeld);

        InputManager.Current.PreProcessInput += OnPreProcessInput;
        _watchingInput = true;
        _watchTimer.Start();
        Opened?.Invoke(this, EventArgs.Empty);
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        StopWatching();
        _pressedRow = null;
        foreach (var row in _itemRows)
        {
            row.Background = Brushes.Transparent;
        }
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void StopWatching()
    {
        _watchTimer.Stop();
        if (_watchingInput)
        {
            InputManager.Current.PreProcessInput -= OnPreProcessInput;
            _watchingInput = false;
        }
    }

    private void EnsurePopupNonActivating()
    {
        _popup.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!_popup.IsOpen
                || PresentationSource.FromVisual(_card) is not HwndSource source
                || source.Handle == 0)
            {
                return;
            }
            var style = (long)NativeMethods.GetWindowLongPtr(source.Handle, NativeMethods.GwlExStyle);
            if ((style & DockNativeMethods.WsExNoActivate) == 0)
            {
                NativeMethods.SetWindowLongPtr(
                    source.Handle,
                    NativeMethods.GwlExStyle,
                    (nint)(style | DockNativeMethods.WsExNoActivate));
            }
        });
    }

    private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (!_popup.IsOpen)
        {
            return;
        }

        if (e.StagingItem.Input is MouseButtonEventArgs { ButtonState: MouseButtonState.Pressed } mouseArgs)
        {
            var popupSource = PresentationSource.FromVisual(_card);
            if (popupSource is not null && ReferenceEquals(mouseArgs.MouseDevice.ActiveSource, popupSource))
            {
                return;
            }
            if (!IsCursorInsidePopup())
            {
                _popup.IsOpen = false;
            }
        }
    }

    private void OnWatchTick()
    {
        if (!_popup.IsOpen)
        {
            StopWatching();
            return;
        }

        var anyMouseHeld = ShellSurfaceNativeMethods.IsAnyMouseButtonPressed();
        var anyMouseLatch = _dismissState.IsMouseArmed
            && (TopBarNativeMethods.IsMouseButtonDown(TopBarNativeMethods.LeftMouseButton)
                || TopBarNativeMethods.IsMouseButtonDown(TopBarNativeMethods.RightMouseButton)
                || TopBarNativeMethods.IsMouseButtonDown(TopBarNativeMethods.MiddleMouseButton));
        var escapeHeld = TopBarNativeMethods.IsKeyCurrentlyDown(TopBarNativeMethods.EscapeKey);
        var escapeLatch = _dismissState.IsEscapeArmed
            && TopBarNativeMethods.IsMouseButtonDown(TopBarNativeMethods.EscapeKey);
        var pointerInside = IsCursorInsidePopup();
        var foreground = DockNativeMethods.GetForegroundWindow();
        var systemMenu = ShellSurfaceNativeMethods.IsSystemMenuForeground();

        if (_dismissState.ShouldCloseOnPoll(
                anyMouseHeld,
                anyMouseLatch,
                escapeHeld,
                escapeLatch,
                pointerInside,
                foreground,
                PopupHandle,
                systemMenu,
                out var newlyArmedMouse,
                out var newlyArmedEscape))
        {
            _popup.IsOpen = false;
            return;
        }

        if (newlyArmedMouse)
        {
            ConsumeMouseLatches();
        }
        if (newlyArmedEscape)
        {
            TopBarNativeMethods.ConsumeMouseButtonDown(TopBarNativeMethods.EscapeKey);
        }
    }

    private bool IsCursorInsidePopup()
    {
        if (PopupHandle is var handle && handle != 0
            && DockNativeMethods.GetCursorPos(out var cursor)
            && DockNativeMethods.GetWindowRect(handle, out var rect))
        {
            return cursor.X >= rect.Left && cursor.X < rect.Right
                && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
        }
        return false;
    }

    private static void ConsumeMouseLatches()
    {
        TopBarNativeMethods.ConsumeMouseButtonDown(TopBarNativeMethods.LeftMouseButton);
        TopBarNativeMethods.ConsumeMouseButtonDown(TopBarNativeMethods.RightMouseButton);
        TopBarNativeMethods.ConsumeMouseButtonDown(TopBarNativeMethods.MiddleMouseButton);
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
