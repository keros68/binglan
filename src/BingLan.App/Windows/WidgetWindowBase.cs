using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.Core.Models;
using BingLan.Core.Services;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using FontStyle = System.Windows.FontStyle;
using MenuItem = System.Windows.Controls.MenuItem;
using Point = System.Windows.Point;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using Separator = System.Windows.Controls.Separator;
using SystemColors = System.Windows.SystemColors;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace BingLan.App.Windows;

/// <summary>Which edges of a card a resize drag moves.</summary>
[Flags]
public enum ResizeEdges
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8
}

public class WidgetWindowBase : Window
{
    /// <summary>The gap snapped cards keep between each other; part of the desktop style.</summary>
    internal static double CardSpacingDip { get; set; } = DesktopStyleRules.DefaultCardSpacing;

    private MoveSession? _moveSession;
    private DragState? _drag;

    private const double SnapThresholdDip = 8d;
    private const double SnapReachDip = 64d;
    private const int DragStartPixels = 3;
    private const double ResizeBandDip = 8d;
    private const double DefaultHeaderCornerRadius = 9.2d;
    private const double DefaultItemCornerRadius = 10d;

    private static readonly DependencyPropertyKey SurfaceCornerRadiusPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(SurfaceCornerRadius),
            typeof(CornerRadius),
            typeof(WidgetWindowBase),
            new FrameworkPropertyMetadata(
                new CornerRadius(WidgetAppearanceRules.DefaultCornerRadius)));

    public static readonly DependencyProperty SurfaceCornerRadiusProperty =
        SurfaceCornerRadiusPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey HeaderCornerRadiusPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HeaderCornerRadius),
            typeof(CornerRadius),
            typeof(WidgetWindowBase),
            new FrameworkPropertyMetadata(
                new CornerRadius(DefaultHeaderCornerRadius)));

    public static readonly DependencyProperty HeaderCornerRadiusProperty =
        HeaderCornerRadiusPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey ItemCornerRadiusPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(ItemCornerRadius),
            typeof(CornerRadius),
            typeof(WidgetWindowBase),
            new FrameworkPropertyMetadata(
                new CornerRadius(DefaultItemCornerRadius)));

    public static readonly DependencyProperty ItemCornerRadiusProperty =
        ItemCornerRadiusPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey WidgetBackgroundBrushPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(WidgetBackgroundBrush),
            typeof(Brush),
            typeof(WidgetWindowBase),
            new FrameworkPropertyMetadata(Brushes.Transparent));

    public static readonly DependencyProperty WidgetBackgroundBrushProperty =
        WidgetBackgroundBrushPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey WidgetHeaderBrushPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(WidgetHeaderBrush),
            typeof(Brush),
            typeof(WidgetWindowBase),
            new FrameworkPropertyMetadata(Brushes.Transparent));

    public static readonly DependencyProperty WidgetHeaderBrushProperty =
        WidgetHeaderBrushPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey WidgetTextBrushPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(WidgetTextBrush),
            typeof(Brush),
            typeof(WidgetWindowBase),
            new FrameworkPropertyMetadata(Brushes.Black));

    public static readonly DependencyProperty WidgetTextBrushProperty =
        WidgetTextBrushPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey WidgetSecondaryTextBrushPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(WidgetSecondaryTextBrush),
            typeof(Brush),
            typeof(WidgetWindowBase),
            new FrameworkPropertyMetadata(Brushes.Gray));

    public static readonly DependencyProperty WidgetSecondaryTextBrushProperty =
        WidgetSecondaryTextBrushPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey WidgetTitleFontFamilyPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(WidgetTitleFontFamily),
            typeof(FontFamily),
            typeof(WidgetWindowBase),
            new FrameworkPropertyMetadata(
                new FontFamily(WidgetAppearanceRules.DefaultTitleFontFamily)));

    public static readonly DependencyProperty WidgetTitleFontFamilyProperty =
        WidgetTitleFontFamilyPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey WidgetTitleFontWeightPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(WidgetTitleFontWeight),
            typeof(FontWeight),
            typeof(WidgetWindowBase),
            new FrameworkPropertyMetadata(
                WidgetAppearanceRules.DefaultTitleFontBold
                    ? FontWeights.Bold
                    : FontWeights.Normal));

    public static readonly DependencyProperty WidgetTitleFontWeightProperty =
        WidgetTitleFontWeightPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey WidgetTitleFontStylePropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(WidgetTitleFontStyle),
            typeof(FontStyle),
            typeof(WidgetWindowBase),
            new FrameworkPropertyMetadata(
                WidgetAppearanceRules.DefaultTitleFontItalic
                    ? FontStyles.Italic
                    : FontStyles.Normal));

    public static readonly DependencyProperty WidgetTitleFontStyleProperty =
        WidgetTitleFontStylePropertyKey.DependencyProperty;

    public static readonly DependencyProperty WidgetCornerRadiusProperty =
        DependencyProperty.Register(
            nameof(WidgetCornerRadius),
            typeof(double),
            typeof(WidgetWindowBase),
            new FrameworkPropertyMetadata(
                WidgetAppearanceRules.DefaultCornerRadius,
                OnWidgetCornerRadiusChanged,
                CoerceWidgetCornerRadius));

    private static readonly DependencyPropertyKey IsTitleEditingPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsTitleEditing),
            typeof(bool),
            typeof(WidgetWindowBase),
            new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsTitleEditingProperty =
        IsTitleEditingPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey IsWidgetSelectedPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsWidgetSelected),
            typeof(bool),
            typeof(WidgetWindowBase),
            new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsWidgetSelectedProperty =
        IsWidgetSelectedPropertyKey.DependencyProperty;

    private HwndSource? _source;
    private WindowPlacement? _placement;
    private bool _isUpdatingNativeWindowShape;
    private bool _isApplyingTaskbarSafeBounds;
    private bool _titleEditorHooked;
    private string? _titleBeforeEdit;
    private bool _isClosing;
    private bool _isChangingVisibilityByApp;

    public WidgetWindowBase()
    {
        AccessibilityThemeManager.EnsureInitialized();
        AccessibilityThemeManager.HighContrastChanged += AccessibilityThemeManager_HighContrastChanged;
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = false;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        SourceInitialized += OnSourceInitialized;
        LocationChanged += (_, _) => PlacementChanged();
        SizeChanged += (_, _) =>
        {
            PlacementChanged();
            RefreshNativeWindowShape();
        };
        DpiChanged += (_, _) =>
        {
            EnsureTaskbarSafeBounds();
            RefreshNativeWindowShape();
        };
        Loaded += (_, _) =>
        {
            RefreshBackdrop();
            // Moving a transparent window redraws it. With software rendering the card's
            // drawn image is kept, so a drag only copies it (drag steps ~18 -> ~14 ms in the
            // UI test, about what hardware rendering takes); text looks the same.
            if (Content is UIElement content)
            {
                content.CacheMode = new System.Windows.Media.BitmapCache { SnapsToDevicePixels = true };
            }
        };
        ContentRendered += (_, _) =>
        {
            EnsureTaskbarSafeBounds();
            RefreshBackdrop();
            SendToBack();
        };
        // Cards belong to the desktop: once another window is used they drop behind every
        // ordinary window again instead of staying on top of it.
        Deactivated += (_, _) =>
        {
            if (_drag is null)
            {
                SendToBack();
            }
        };
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            ApplySelection(true);
            Selected?.Invoke(this);
            var point = e.GetPosition(this);
            if (e.ClickCount == 2 && TryBeginTitleEditAt(point))
            {
                e.Handled = true;
                return;
            }
            if (e.ClickCount == 1 && CanStartDragAt(point) && DockNativeMethods.GetCursorPos(out var cursor)
                && BeginDrag(cursor.X, cursor.Y, ResizeEdgesAt(point)))
            {
                CaptureMouse();
                e.Handled = true;
            }
        };
        PreviewMouseMove += (_, e) =>
        {
            if (_drag is not null && e.LeftButton == MouseButtonState.Pressed
                && DockNativeMethods.GetCursorPos(out var cursor))
            {
                DragTo(cursor.X, cursor.Y);
                return;
            }

            // Show a resize cursor over an edge; elsewhere the controls' own cursors apply.
            var point = e.GetPosition(this);
            Cursor = CanStartDragAt(point) ? ResizeCursor(ResizeEdgesAt(point)) : null;
        };
        MouseLeave += (_, _) =>
        {
            if (_drag is null)
            {
                Cursor = null;
            }
        };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_drag is not null)
            {
                EndDrag();
                ReleaseMouseCapture();
                e.Handled = true;
            }
        };
        LostMouseCapture += (_, _) => EndDrag();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F2 && !IsTitleEditing && TitleEditorBox is not null)
            {
                BeginTitleEdit();
                e.Handled = true;
            }
        };
        // Moving keyboard focus into a card selects it, like a click, so its settings
        // entry is reachable without a mouse; that selection lasts until focus leaves. A
        // card selected by a click is deselected once the mouse leaves it.
        IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if (e.NewValue is true && !IsWidgetSelected)
            {
                ApplySelection(true);
                Selected?.Invoke(this);
                // Set after the host has deselected the other cards, which resets it.
                _selectedByFocus = true;
            }
            else if (e.NewValue is false && !IsMouseOver)
            {
                ApplySelection(false);
            }
        };
        MouseLeave += (_, _) =>
        {
            if (!_selectedByFocus && _drag is null && !IsTitleEditing)
            {
                ApplySelection(false);
            }
        };
        Closing += (_, e) =>
        {
            e.Cancel = !CanClose;
            if (!e.Cancel)
            {
                _isClosing = true;
            }
        };
        ContextMenu = BuildContextMenu();
    }

    public event Action<WidgetWindowBase>? WidgetChanged;
    public event Action<WidgetWindowBase>? Selected;
    public event Action? OrganizeDesktopRequested;
    public event Action<WidgetWindowBase>? OpenAppSettingsRequested;
    public event Action<WidgetWindowBase>? DeleteRequested;
    public event Action? ExitRequested;

    public bool IsWidgetLocked { get; private set; }

    public bool IsTitleEditing => (bool)GetValue(IsTitleEditingProperty);

    public bool IsWidgetSelected => (bool)GetValue(IsWidgetSelectedProperty);

    public bool CanClose { get; set; }

    public double WidgetCornerRadius
    {
        get => (double)GetValue(WidgetCornerRadiusProperty);
        set => SetValue(WidgetCornerRadiusProperty, value);
    }

    public CornerRadius SurfaceCornerRadius =>
        (CornerRadius)GetValue(SurfaceCornerRadiusProperty);

    public CornerRadius HeaderCornerRadius =>
        (CornerRadius)GetValue(HeaderCornerRadiusProperty);

    public CornerRadius ItemCornerRadius =>
        (CornerRadius)GetValue(ItemCornerRadiusProperty);

    public Brush WidgetBackgroundBrush =>
        (Brush)GetValue(WidgetBackgroundBrushProperty);

    public Brush WidgetHeaderBrush =>
        (Brush)GetValue(WidgetHeaderBrushProperty);

    public Brush WidgetTextBrush =>
        (Brush)GetValue(WidgetTextBrushProperty);

    public Brush WidgetSecondaryTextBrush =>
        (Brush)GetValue(WidgetSecondaryTextBrushProperty);

    public FontFamily WidgetTitleFontFamily =>
        (FontFamily)GetValue(WidgetTitleFontFamilyProperty);

    public FontWeight WidgetTitleFontWeight =>
        (FontWeight)GetValue(WidgetTitleFontWeightProperty);

    public FontStyle WidgetTitleFontStyle =>
        (FontStyle)GetValue(WidgetTitleFontStyleProperty);

    public WidgetAppearanceState WidgetAppearance { get; private set; } = new();

    public WidgetBackdropResult BackdropResult { get; private set; } =
        new(WidgetBackdropMode.SolidFallback, "窗口尚未初始化");

    public string? NativeStyleError { get; private set; }

    public string? NativeShapeError { get; private set; }

    public void ApplyPlacement(WindowPlacement placement, bool isLocked)
    {
        // 先拍下目标值。设置 Left/Top 会同步触发宿主的状态捕获，宿主与窗口
        // 共用同一个 placement 对象时，不能让中途捕获覆盖尚未应用的宽高。
        var left = placement.Left;
        var top = placement.Top;
        var width = Math.Max(MinWidth, placement.Width);
        var height = Math.Max(MinHeight, placement.Height);
        _placement = placement;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        IsWidgetLocked = isLocked;
        OnLockChanged(isLocked);
        EnsureTaskbarSafeBounds();
    }

    private bool _selectedByFocus;

    public void ApplySelection(bool isSelected)
    {
        _selectedByFocus = false;
        SetValue(IsWidgetSelectedPropertyKey, isSelected);
    }

    public void ApplyWidgetLock(bool isLocked)
    {
        IsWidgetLocked = isLocked;
        OnLockChanged(isLocked);
        if (IsLoaded)
        {
            NotifyStateChanged();
        }
    }

    public void CopyPlacementTo(WindowPlacement placement)
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }
        placement.Left = Left;
        placement.Top = Top;
        placement.Width = Width;
        placement.Height = Height;
        WindowScreenRecovery.RememberLayout(this, placement);
    }

    /// <summary>Puts the card back where it last sat on the arrangement of monitors now in use.</summary>
    internal bool RestoreDisplayLayout() =>
        _placement is not null && WindowScreenRecovery.RestoreLayout(this, _placement);

    public void RefreshBackdrop()
    {
        if (_source is null || _source.IsDisposed)
        {
            return;
        }

        try
        {
            BackdropResult = WidgetBackdropController.Apply(this, _source);
        }
        catch (Exception exception)
        {
            var color = Color.FromArgb(0, 0, 0, 0);
            _source.CompositionTarget.BackgroundColor = color;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Background = brush;
            BackdropResult = new WidgetBackdropResult(
                WidgetBackdropMode.SolidFallback,
                $"材质异常，已回退纯色：{exception.GetType().Name}");
        }
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        if (!AllowsTransparency)
        {
            throw new InvalidOperationException(
                "可调抗锯齿圆角必须使用 WPF 逐像素透明窗口。");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        AccessibilityThemeManager.HighContrastChanged -= AccessibilityThemeManager_HighContrastChanged;
        if (_source is not null && !_source.IsDisposed)
        {
            _source.RemoveHook(WindowProc);
        }
        _source = null;
        base.OnClosed(e);
    }

    protected void NotifyStateChanged() => WidgetChanged?.Invoke(this);

    private void NotifyAppearanceChanged()
    {
        if (IsLoaded)
        {
            NotifyStateChanged();
        }
    }

    private void RefreshAppearanceBrushes()
    {
        if (AccessibilityThemeManager.IsHighContrastEnabled)
        {
            SetValue(WidgetBackgroundBrushPropertyKey, SystemColors.WindowBrush);
            SetValue(WidgetHeaderBrushPropertyKey, SystemColors.ControlBrush);
            SetValue(WidgetTextBrushPropertyKey, SystemColors.WindowTextBrush);
            SetValue(WidgetSecondaryTextBrushPropertyKey, SystemColors.WindowTextBrush);
            return;
        }

        SetValue(
            WidgetBackgroundBrushPropertyKey,
            CreateAppearanceBrush(
                WidgetAppearance.BackgroundColor,
                WidgetAppearance.BackgroundOpacity));
        SetValue(
            WidgetHeaderBrushPropertyKey,
            CreateAppearanceBrush(
                WidgetAppearance.HeaderColor,
                WidgetAppearance.HeaderOpacity));
        SetValue(
            WidgetTextBrushPropertyKey,
            CreateAppearanceBrush(WidgetAppearance.TextColor, 1d));
        SetValue(
            WidgetSecondaryTextBrushPropertyKey,
            CreateAppearanceBrush(WidgetAppearance.TextColor, 0.68d));
    }

    private void AccessibilityThemeManager_HighContrastChanged(object? sender, EventArgs e)
    {
        RefreshAppearanceBrushes();
        RefreshBackdrop();
    }

    private void RefreshTitleTypography()
    {
        var family = InstalledFontCatalog.Resolve(WidgetAppearance.TitleFontFamily);
        SetValue(WidgetTitleFontFamilyPropertyKey, InstalledFontCatalog.Create(family));
        SetValue(
            WidgetTitleFontWeightPropertyKey,
            WidgetAppearance.TitleFontBold
                ? FontWeights.Bold
                : InstalledFontCatalog.WeightOf(family, FontWeights.Normal));
        SetValue(
            WidgetTitleFontStylePropertyKey,
            WidgetAppearance.TitleFontItalic ? FontStyles.Italic : FontStyles.Normal);
    }

    private static Brush CreateAppearanceBrush(string value, double opacity)
    {
        var color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(value)!;
        color.A = (byte)Math.Round(Math.Clamp(opacity, 0d, 1d) * byte.MaxValue);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>The card's title box, for cards that have a title.</summary>
    protected virtual System.Windows.Controls.TextBox? TitleEditorBox => null;

    /// <summary>Writes the text in the title box to the card's state.</summary>
    protected virtual void CommitTitleEdit()
    {
    }

    /// <summary>
    /// Makes the title editable in place. The title is otherwise part of the drag area,
    /// so editing starts on a double-click or F2 and ends on Enter, Esc or leaving the box.
    /// </summary>
    public void BeginTitleEdit()
    {
        if (TitleEditorBox is not { } editor || IsTitleEditing)
        {
            return;
        }

        if (!_titleEditorHooked)
        {
            _titleEditorHooked = true;
            editor.LostKeyboardFocus += (_, _) => EndTitleEdit(commit: true);
            editor.PreviewKeyDown += (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Escape)
                {
                    EndTitleEdit(commit: e.Key == Key.Enter);
                    e.Handled = true;
                }
            };
        }

        _titleBeforeEdit = editor.Text;
        SetValue(IsTitleEditingPropertyKey, true);
        Activate();
        editor.Focus();
        editor.SelectAll();
    }

    /// <summary>Ends title editing, keeping the typed title or putting the previous one back.</summary>
    public void EndTitleEdit(bool commit)
    {
        if (TitleEditorBox is not { } editor || !IsTitleEditing)
        {
            return;
        }

        SetValue(IsTitleEditingPropertyKey, false);
        if (!commit && _titleBeforeEdit is not null)
        {
            editor.Text = _titleBeforeEdit;
        }
        CommitTitleEdit();
        if (editor.IsKeyboardFocused)
        {
            Keyboard.ClearFocus();
        }
    }

    /// <summary>Starts title editing when the point is on the title; used for a double-click.</summary>
    internal bool TryBeginTitleEditAt(Point point)
    {
        if (IsTitleEditing ||
            TitleEditorBox is not { IsVisible: true } editor ||
            !new Rect(editor.TranslatePoint(new Point(), this), editor.RenderSize).Contains(point))
        {
            return false;
        }

        BeginTitleEdit();
        return true;
    }

    /// <summary>
    /// Whether a press at this point moves the card: the card is unlocked and the point is
    /// on its surface but not on a control, so typing, ticking and buttons keep working.
    /// </summary>
    public bool CanStartDragAt(Point point) =>
        !IsWidgetLocked && IsInsideRoundedSurface(point) && !IsInteractive(point);

    /// <summary>
    /// The edges a press at this point resizes: a band of 8 DIP along each edge, and both
    /// edges at a corner. Elsewhere a press moves the card.
    /// </summary>
    public ResizeEdges ResizeEdgesAt(Point point)
    {
        var edges = ResizeEdges.None;
        if (point.X <= ResizeBandDip)
        {
            edges |= ResizeEdges.Left;
        }
        else if (point.X >= ActualWidth - ResizeBandDip)
        {
            edges |= ResizeEdges.Right;
        }
        if (point.Y <= ResizeBandDip)
        {
            edges |= ResizeEdges.Top;
        }
        else if (point.Y >= ActualHeight - ResizeBandDip)
        {
            edges |= ResizeEdges.Bottom;
        }
        return edges;
    }

    private static System.Windows.Input.Cursor? ResizeCursor(ResizeEdges edges) => edges switch
    {
        ResizeEdges.Left or ResizeEdges.Right => System.Windows.Input.Cursors.SizeWE,
        ResizeEdges.Top or ResizeEdges.Bottom => System.Windows.Input.Cursors.SizeNS,
        ResizeEdges.Left | ResizeEdges.Top or ResizeEdges.Right | ResizeEdges.Bottom =>
            System.Windows.Input.Cursors.SizeNWSE,
        ResizeEdges.Right | ResizeEdges.Top or ResizeEdges.Left | ResizeEdges.Bottom =>
            System.Windows.Input.Cursors.SizeNESW,
        _ => null
    };

    protected void Settings_Click(object sender, RoutedEventArgs e) => RequestAppSettings();

    protected void ApplyAppearance(WidgetAppearanceState appearance)
    {
        WidgetAppearance = appearance;
        WidgetAppearance.BackgroundColor = WidgetAppearance.BackgroundColor;
        WidgetAppearance.BackgroundOpacity = WidgetAppearance.BackgroundOpacity;
        WidgetAppearance.HeaderColor = WidgetAppearance.HeaderColor;
        WidgetAppearance.HeaderOpacity = WidgetAppearance.HeaderOpacity;
        WidgetAppearance.TextColor = WidgetAppearance.TextColor;
        WidgetAppearance.TitleFontFamily = InstalledFontCatalog.Resolve(
            WidgetAppearance.TitleFontFamily);
        RefreshAppearanceBrushes();
        RefreshTitleTypography();
    }

    public void SetWidgetBackgroundColor(string value)
    {
        WidgetAppearance.BackgroundColor = value;
        RefreshAppearanceBrushes();
        NotifyAppearanceChanged();
    }

    public void SetWidgetHeaderColor(string value)
    {
        WidgetAppearance.HeaderColor = value;
        RefreshAppearanceBrushes();
        NotifyAppearanceChanged();
    }

    public void SetWidgetBackgroundOpacity(double value)
    {
        WidgetAppearance.BackgroundOpacity = value;
        RefreshAppearanceBrushes();
        NotifyAppearanceChanged();
    }

    public void SetWidgetHeaderOpacity(double value)
    {
        WidgetAppearance.HeaderOpacity = value;
        RefreshAppearanceBrushes();
        NotifyAppearanceChanged();
    }

    public void SetWidgetTextColor(string value)
    {
        WidgetAppearance.TextColor = value;
        RefreshAppearanceBrushes();
        NotifyAppearanceChanged();
    }

    public void SetWidgetTitleFontFamily(string value)
    {
        WidgetAppearance.TitleFontFamily = InstalledFontCatalog.Resolve(value);
        RefreshTitleTypography();
        NotifyAppearanceChanged();
    }

    public void SetWidgetTitleFontBold(bool value)
    {
        WidgetAppearance.TitleFontBold = value;
        RefreshTitleTypography();
        NotifyAppearanceChanged();
    }

    public void SetWidgetTitleFontItalic(bool value)
    {
        WidgetAppearance.TitleFontItalic = value;
        RefreshTitleTypography();
        NotifyAppearanceChanged();
    }

    public void ResetWidgetAppearance()
    {
        WidgetAppearance.BackgroundColor = WidgetAppearanceRules.DefaultBackgroundColor;
        WidgetAppearance.BackgroundOpacity = WidgetAppearanceRules.DefaultBackgroundOpacity;
        WidgetAppearance.HeaderColor = WidgetAppearanceRules.DefaultHeaderColor;
        WidgetAppearance.HeaderOpacity = WidgetAppearanceRules.DefaultHeaderOpacity;
        WidgetAppearance.TextColor = WidgetAppearanceRules.DefaultTextColor;
        WidgetAppearance.TitleFontFamily = InstalledFontCatalog.Resolve(
            WidgetAppearanceRules.DefaultTitleFontFamily);
        WidgetAppearance.TitleFontBold = WidgetAppearanceRules.DefaultTitleFontBold;
        WidgetAppearance.TitleFontItalic = WidgetAppearanceRules.DefaultTitleFontItalic;
        WidgetCornerRadius = WidgetAppearanceRules.DefaultCornerRadius;
        RefreshAppearanceBrushes();
        RefreshTitleTypography();
        NotifyAppearanceChanged();
    }

    protected void RequestDesktopOrganization() => OrganizeDesktopRequested?.Invoke();

    protected void RequestAppSettings() => OpenAppSettingsRequested?.Invoke(this);

    protected virtual void OnLockChanged(bool locked)
    {
    }

    private static object CoerceWidgetCornerRadius(
        DependencyObject dependencyObject,
        object baseValue) =>
        WidgetAppearanceRules.CoerceCornerRadius((double)baseValue);

    private static void OnWidgetCornerRadiusChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not WidgetWindowBase window)
        {
            return;
        }

        var radius = (double)args.NewValue;
        window.SetValue(SurfaceCornerRadiusPropertyKey, new CornerRadius(radius));
        window.SetValue(
            HeaderCornerRadiusPropertyKey,
            ScaleInteriorCornerRadius(radius, DefaultHeaderCornerRadius));
        window.SetValue(
            ItemCornerRadiusPropertyKey,
            ScaleInteriorCornerRadius(radius, DefaultItemCornerRadius));
        window.RefreshNativeWindowShape();
        if (window.IsLoaded)
        {
            window.NotifyStateChanged();
        }
    }

    private static CornerRadius ScaleInteriorCornerRadius(
        double outerRadius,
        double defaultInteriorRadius) =>
        new(outerRadius * defaultInteriorRadius /
            WidgetAppearanceRules.DefaultCornerRadius);

    private void PlacementChanged()
    {
        if (IsLoaded)
        {
            WidgetChanged?.Invoke(this);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (!AllowsTransparency)
        {
            throw new InvalidOperationException(
                "可调抗锯齿圆角必须使用 WPF 逐像素透明窗口。");
        }

        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowProc);

        try
        {
            ApplyNativeWindowStyles(handle, ShowInTaskbar);
            NativeStyleError = null;
        }
        catch (Win32Exception exception)
        {
            NativeStyleError = exception.Message;
        }

        RefreshNativeWindowShape();
        RefreshBackdrop();
    }

    private void RefreshNativeWindowShape()
    {
        if (_source is null || _source.IsDisposed || _isUpdatingNativeWindowShape)
        {
            return;
        }

        _isUpdatingNativeWindowShape = true;
        try
        {
            string? error = null;
            var cornerPreference = NativeMethods.DwmWindowCornerDoNotRound;
            var cornerResult = NativeMethods.DwmSetWindowAttribute(
                _source.Handle,
                NativeMethods.DwmwaWindowCornerPreference,
                ref cornerPreference,
                Marshal.SizeOf<int>());
            if (cornerResult < 0)
            {
                error = $"关闭 DWM 系统圆角失败：0x{cornerResult:X8}";
            }

            var ncRenderingPolicy = NativeMethods.DwmNcRenderingDisabled;
            var ncRenderingResult = NativeMethods.DwmSetWindowAttribute(
                _source.Handle,
                NativeMethods.DwmwaNcRenderingPolicy,
                ref ncRenderingPolicy,
                Marshal.SizeOf<int>());
            if (ncRenderingResult < 0)
            {
                error = error is null
                    ? $"关闭 DWM 非客户区绘制失败：0x{ncRenderingResult:X8}"
                    : $"{error}；关闭 DWM 非客户区绘制失败：0x{ncRenderingResult:X8}";
            }

            UpdateLayeredClip();
            NativeShapeError = error;
        }
        finally
        {
            _isUpdatingNativeWindowShape = false;
        }
    }

    /// <summary>
    /// Applies the desktop-wide card shadow. Each card is its own window clipped to its
    /// rounded shape, so the shadow gives the text and icons inside the card depth rather
    /// than drawing outside the card edge.
    /// </summary>
    internal void ApplyStyle(DesktopStyleState style)
    {
        if (Content is not UIElement content)
        {
            return;
        }

        content.Effect = style.CardShadow switch
        {
            CardShadow.Soft => new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 6,
                ShadowDepth = 1,
                Opacity = 0.28,
                Color = Colors.Black
            },
            CardShadow.Strong => new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 10,
                ShadowDepth = 2,
                Opacity = 0.5,
                Color = Colors.Black
            },
            _ => null
        };
    }

    private void UpdateLayeredClip()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var radius = Math.Min(
            WidgetCornerRadius,
            Math.Min(width, height) / 2d);
        Clip = radius <= 0d
            ? null
            : new RectangleGeometry(new Rect(0d, 0d, width, height), radius, radius);
    }

    private static void ApplyNativeWindowStyles(nint handle, bool showInTaskbar)
    {
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlStyle).ToInt64();
        style |= NativeMethods.WsThickFrame;
        // Cards are never maximised; without the maximise box Windows does not snap a
        // card to half the screen when it is dropped at a screen edge.
        style &= ~NativeMethods.WsMaximizeBox;
        SetWindowLongChecked(handle, NativeMethods.GwlStyle, style);

        var exStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        exStyle |= NativeMethods.WsExAcceptFiles;
        if (showInTaskbar)
        {
            exStyle |= NativeMethods.WsExAppWindow;
            exStyle &= ~NativeMethods.WsExToolWindow;
        }
        else
        {
            exStyle |= NativeMethods.WsExToolWindow;
            exStyle &= ~NativeMethods.WsExAppWindow;
        }
        exStyle &= ~(NativeMethods.WsExNoActivate |
                     NativeMethods.WsExTopmost |
                     NativeMethods.WsExTransparent);
        SetWindowLongChecked(handle, NativeMethods.GwlExStyle, exStyle);

        if (!NativeMethods.SetWindowPos(
                handle,
                0,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoMove |
                NativeMethods.SwpNoSize |
                NativeMethods.SwpNoZOrder |
                NativeMethods.SwpNoActivate |
                NativeMethods.SwpFrameChanged))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static void SetWindowLongChecked(nint handle, int index, long value)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = NativeMethods.SetWindowLongPtr(handle, index, (nint)value);
        var error = Marshal.GetLastPInvokeError();
        if (previous == 0 && error != 0)
        {
            throw new Win32Exception(error);
        }
    }

    private nint WindowProc(
        nint window,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message == NativeMethods.WmNcCalcSize && wParam != 0)
        {
            handled = true;
            return 0;
        }

        if (message == NativeMethods.WmNcHitTest)
        {
            return HandleHitTest(lParam, ref handled);
        }

        if (message == NativeMethods.WmSettingChange && wParam == NativeMethods.SpiSetWorkArea)
        {
            BingLan.App.Services.WindowScreenRecovery.KeepOutOfReservedEdges(this);
        }

        if (message is NativeMethods.WmDwmCompositionChanged
            or NativeMethods.WmThemeChanged
            or NativeMethods.WmSettingChange)
        {
            EnsureTaskbarSafeBounds();
            RefreshNativeWindowShape();
            RefreshBackdrop();
        }

        if (!_isClosing && !_isChangingVisibilityByApp)
        {
            if (message == NativeMethods.WmSize && wParam == NativeMethods.SizeMinimized)
            {
                // The shell's "minimise all" minimised the card; it is desktop furniture
                // and puts itself back.
                RecoverFromShell(minimized: true);
            }
            else if (message == NativeMethods.WmShowWindow && wParam == 0)
            {
                // The shell's "show desktop" hid the card; it shows itself again.
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

    /// <summary>
    /// Puts the card back after the shell hid or minimised it, for example through
    /// "show desktop" or "minimise all" on a Windows build that does not skip tool
    /// windows. Runs after the current message so the shell's change completes first;
    /// hides the app itself asked for are marked with <see cref="HideFromApp"/> and
    /// never countered. The state is read from the native window because WPF does not
    /// notice a visibility change made by another process.
    /// </summary>
    private void RecoverFromShell(bool minimized)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Send,
            new Action(() =>
            {
                if (_isClosing || _source is not { IsDisposed: false } source)
                {
                    return;
                }
                var handle = source.Handle;
                if (minimized ? !NativeMethods.IsIconic(handle)
                    : NativeMethods.IsWindowVisible(handle))
                {
                    return;
                }

                // SW_SHOWNOACTIVATE shows (and un-minimises) without stealing the
                // foreground; the card then goes back under every ordinary window.
                NativeMethods.ShowWindow(handle, NativeMethods.SwShowNoActivate);
                SendToBack();
            }));
    }

    /// <summary>
    /// Hides the card for the app's own reason — the tray toggle or a component's
    /// visibility setting — so the shell-hide recovery must leave it hidden.
    /// </summary>
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

    private void EnsureTaskbarSafeBounds()
    {
        if (_source is null ||
            _source.IsDisposed ||
            _isApplyingTaskbarSafeBounds ||
            WindowState != WindowState.Normal)
        {
            return;
        }

        _isApplyingTaskbarSafeBounds = true;
        try
        {
            TaskbarEdgeGuard.EnsureWindowBounds(_source.Handle);
        }
        finally
        {
            _isApplyingTaskbarSafeBounds = false;
        }
    }

    // Nothing but the dragged card moves during a drag, so the monitors, the other cards
    // and the taskbar edges are read once when it starts instead of on every mouse step.
    private void BeginMoveSession()
    {
        _moveSession = CreateMoveSession();
        TaskbarEdgeGuard.BeginMove();
    }

    private MoveSession CreateMoveSession()
    {
        var others = new List<BingLan.Core.Dock.PixelRect>();
        foreach (var other in System.Windows.Application.Current.Windows.OfType<WidgetWindowBase>())
        {
            if (!ReferenceEquals(other, this)
                && other.IsVisible
                && other._source is { } source
                && DockNativeMethods.GetWindowRect(source.Handle, out var bounds))
            {
                others.Add(new BingLan.Core.Dock.PixelRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
            }
        }
        return new MoveSession(MonitorCatalog.GetAll(), others);
    }

    private sealed record MoveSession(
        IReadOnlyList<MonitorSnapshot> Monitors,
        IReadOnlyList<BingLan.Core.Dock.PixelRect> Others);

    private sealed class DragState(int cursorX, int cursorY, BingLan.Core.Dock.PixelRect start, ResizeEdges edges)
    {
        internal int CursorX { get; } = cursorX;
        internal int CursorY { get; } = cursorY;
        internal BingLan.Core.Dock.PixelRect Start { get; } = start;
        internal ResizeEdges Edges { get; } = edges;
        internal bool Moving { get; set; }
    }

    /// <summary>Puts the card behind all ordinary windows without activating it.</summary>
    internal void SendToBack()
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

    /// <summary>The card's window handle; 0 before the window exists or after it closed.</summary>
    internal nint WindowHandle => _source is { IsDisposed: false } source ? source.Handle : 0;

    /// <summary>
    /// Lifts the card into the topmost band at the given z-order slot — under the
    /// taskbar — while the shell shows the desktop: the raised desktop surface would
    /// otherwise cover the card behind the wallpaper. <see cref="SendToBack"/> puts it
    /// back once ordinary windows return.
    /// </summary>
    internal void RaiseAboveDesktop(nint insertAfter)
    {
        if (_source is { IsDisposed: false } source)
        {
            NativeMethods.SetWindowPos(
                source.Handle,
                insertAfter,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize |
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        }
    }

    /// <summary>
    /// Starts following the pointer from a press at this screen position, in pixels: the
    /// card moves, or with edges given, those edges follow the pointer and the card resizes.
    /// </summary>
    internal bool BeginDrag(int cursorX, int cursorY, ResizeEdges edges = ResizeEdges.None)
    {
        if (IsWidgetLocked || _source is null || _source.IsDisposed ||
            !DockNativeMethods.GetWindowRect(_source.Handle, out var bounds))
        {
            return false;
        }

        _drag = new DragState(
            cursorX,
            cursorY,
            new BingLan.Core.Dock.PixelRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
            edges);
        return true;
    }

    /// <summary>
    /// Moves the card with the pointer, snapped to nearby cards and the work area and kept
    /// off an auto-hidden taskbar edge. Small jitters before the drag starts are ignored.
    /// </summary>
    internal void DragTo(int cursorX, int cursorY)
    {
        if (_drag is not { } drag || _source is null || _source.IsDisposed)
        {
            return;
        }

        var dx = cursorX - drag.CursorX;
        var dy = cursorY - drag.CursorY;
        if (!drag.Moving)
        {
            if (Math.Abs(dx) < DragStartPixels && Math.Abs(dy) < DragStartPixels)
            {
                return;
            }
            drag.Moving = true;
            if (drag.Edges == ResizeEdges.None)
            {
                BeginMoveSession();
            }
        }

        if (drag.Edges != ResizeEdges.None)
        {
            ResizeTo(drag, dx, dy);
            return;
        }

        var proposed = new BingLan.Core.Dock.PixelRect(
            drag.Start.Left + dx,
            drag.Start.Top + dy,
            drag.Start.Right + dx,
            drag.Start.Bottom + dy);
        var snapped = Snap(proposed);
        var target = new TaskbarEdgeGuard.PixelRect
        {
            Left = snapped.Left,
            Top = snapped.Top,
            Right = snapped.Right,
            Bottom = snapped.Bottom
        };
        TaskbarEdgeGuard.ConstrainRect(ref target);
        NativeMethods.SetWindowPos(
            _source.Handle,
            0,
            target.Left,
            target.Top,
            0,
            0,
            NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    // Moves the dragged edges with the pointer, never below the card's minimum size; the
    // opposite edges stay where they are.
    private void ResizeTo(DragState drag, int dx, int dy)
    {
        var scale = VisualTreeHelper.GetDpi(this);
        var minimumWidth = (int)Math.Ceiling(MinWidth * scale.DpiScaleX);
        var minimumHeight = (int)Math.Ceiling(MinHeight * scale.DpiScaleY);
        var start = drag.Start;
        var left = start.Left;
        var top = start.Top;
        var right = start.Right;
        var bottom = start.Bottom;
        if (drag.Edges.HasFlag(ResizeEdges.Left))
        {
            left = Math.Min(start.Left + dx, right - minimumWidth);
        }
        if (drag.Edges.HasFlag(ResizeEdges.Right))
        {
            right = Math.Max(start.Right + dx, left + minimumWidth);
        }
        if (drag.Edges.HasFlag(ResizeEdges.Top))
        {
            top = Math.Min(start.Top + dy, bottom - minimumHeight);
        }
        if (drag.Edges.HasFlag(ResizeEdges.Bottom))
        {
            bottom = Math.Max(start.Bottom + dy, top + minimumHeight);
        }
        NativeMethods.SetWindowPos(
            _source!.Handle,
            0,
            left,
            top,
            right - left,
            bottom - top,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    internal void EndDrag()
    {
        if (_drag is not { } drag)
        {
            return;
        }

        _drag = null;
        Cursor = null;
        if (drag.Moving)
        {
            _moveSession = null;
            TaskbarEdgeGuard.EndMove();
            SnapGuideOverlay.HideGuides();
            EnsureTaskbarSafeBounds();
        }
    }

    // Snaps a proposed position to nearby cards and the work area and shows the matching
    // guides. Holding Alt moves freely.
    private BingLan.Core.Dock.PixelRect Snap(BingLan.Core.Dock.PixelRect moving)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            SnapGuideOverlay.HideGuides();
            return moving;
        }

        _moveSession ??= CreateMoveSession();
        var session = _moveSession;
        var centerX = (moving.Left + moving.Right) / 2;
        var centerY = (moving.Top + moving.Bottom) / 2;
        var monitor = session.Monitors.FirstOrDefault(candidate =>
                centerX >= candidate.Bounds.Left && centerX < candidate.Bounds.Right
                && centerY >= candidate.Bounds.Top && centerY < candidate.Bounds.Bottom)
            ?? session.Monitors.FirstOrDefault(candidate => candidate.IsPrimary);
        if (monitor is null)
        {
            return moving;
        }

        var scale = monitor.Dpi / 96d;
        var result = SnapRules.Snap(
            moving,
            session.Others,
            monitor.WorkingArea,
            threshold: (int)Math.Round(SnapThresholdDip * scale),
            gap: (int)Math.Round(CardSpacingDip * scale),
            reach: (int)Math.Round(SnapReachDip * scale));
        SnapGuideOverlay.ShowGuides(monitor, result.Guides);
        return result.Bounds;
    }

    private nint HandleHitTest(nint lParam, ref bool handled)
    {
        handled = true;
        var point = ClientPoint(lParam);
        if (!IsInsideRoundedSurface(point))
        {
            return NativeMethods.HtTransparent;
        }

        // The card moves itself while dragged (see BeginDrag), so it follows the pointer
        // even when Windows is set to drag only a window outline; no edge resizes.
        return NativeMethods.HtClient;
    }

    private Point ClientPoint(nint lParam)
    {
        var packed = lParam.ToInt64();
        return PointFromScreen(new Point(
            unchecked((short)(packed & 0xFFFF)),
            unchecked((short)((packed >> 16) & 0xFFFF))));
    }

    private bool IsInsideRoundedSurface(Point point)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (point.X < 0d || point.Y < 0d || point.X > width || point.Y > height)
        {
            return false;
        }

        var radius = Math.Min(WidgetCornerRadius, Math.Min(width, height) / 2d);
        if (radius <= 0d ||
            (point.X >= radius && point.X <= width - radius) ||
            (point.Y >= radius && point.Y <= height - radius))
        {
            return true;
        }

        var centerX = point.X < radius ? radius : width - radius;
        var centerY = point.Y < radius ? radius : height - radius;
        var deltaX = point.X - centerX;
        var deltaY = point.Y - centerY;
        return deltaX * deltaX + deltaY * deltaY <= radius * radius;
    }

    private bool IsInteractive(Point point)
    {
        DependencyObject? current = InputHitTest(point) as DependencyObject;
        while (current is not null)
        {
            if (current is ButtonBase
                or TextBoxBase
                or PasswordBox
                or Selector
                or RangeBase
                or ScrollBar
                or Thumb
                or FrameworkElement { Tag: "Interactive" })
            {
                return true;
            }

            current = current is Visual or Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        var appSettings = new MenuItem { Header = "在冰蓝桌面中设置…" };
        appSettings.Click += (_, _) => RequestAppSettings();
        var delete = new MenuItem { Header = "删除此组件" };
        delete.Click += (_, _) => DeleteRequested?.Invoke(this);
        var exit = new MenuItem { Header = "退出冰蓝桌面" };
        exit.Click += (_, _) => ExitRequested?.Invoke();
        // A locked card can no longer be dragged or resized; its content still works.
        var lockPosition = new MenuItem { Header = "锁定位置和大小", IsCheckable = true };
        lockPosition.Click += (_, _) => ApplyWidgetLock(lockPosition.IsChecked);
        menu.Opened += (_, _) => lockPosition.IsChecked = IsWidgetLocked;

        menu.Items.Add(appSettings);
        menu.Items.Add(lockPosition);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        menu.Items.Add(exit);
        return menu;
    }
}
