using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using BingLan.App.Interop;
using BingLan.App.Services;
using BingLan.Core.Dock;
using Microsoft.Win32;
using Border = System.Windows.Controls.Border;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Window = System.Windows.Window;

namespace BingLan.App.Dock;

/// <summary>Shared glass lifetime, theme and animation binding for the Dock row and handle.</summary>
internal sealed class DockGlassSurface : IDisposable
{
    private static readonly DependencyProperty ControllerProperty = DependencyProperty.RegisterAttached(
        "Controller", typeof(DockGlassSurface), typeof(DockGlassSurface));
    private static readonly DependencyProperty FillProperty = DependencyProperty.RegisterAttached(
        "Fill", typeof(Brush), typeof(DockGlassSurface), new PropertyMetadata(null, BrushChanged));
    private static readonly DependencyProperty StrokeProperty = DependencyProperty.RegisterAttached(
        "Stroke", typeof(Brush), typeof(DockGlassSurface), new PropertyMetadata(null, BrushChanged));
    private readonly Window _window;
    private readonly Border _surface;
    private readonly string _fillKey;
    private readonly string _strokeKey;
    private HwndSource? _source;
    private DockCompositionBackdrop? _backdrop;
    private bool _failed;
    private bool _syncing;
    private bool _disposed;
    private bool _tracking;
    private bool _usingGlassFill;
    private (Rect Rect, double RadiusX, double RadiusY, double Opacity, Color Fill, Color Stroke)? _lastVisual;
    private bool _transparency = ReadTransparency();

    internal DockGlassSurface(Window window, Border surface, string fillKey, string strokeKey)
    {
        _window = window;
        _surface = surface;
        _fillKey = fillKey;
        _strokeKey = strokeKey;
        surface.SetValue(ControllerProperty, this);
        surface.SetResourceReference(FillProperty, fillKey);
        surface.SetResourceReference(StrokeProperty, strokeKey);
        window.Loaded += Loaded;
        window.IsVisibleChanged += VisibilityChanged;
        window.LocationChanged += PlacementChanged;
        window.SizeChanged += SizeChanged;
        window.Closed += Closed;
        surface.LayoutUpdated += LayoutUpdated;
        AccessibilityThemeManager.HighContrastChanged += ThemeChanged;
    }

    internal bool IsActive => _backdrop is not null;
    internal nint BackgroundHandle => _backdrop?.Handle ?? 0;
    internal PixelRect? BackgroundBounds => _backdrop?.Bounds;

    internal void TrackAnimation(bool active)
    {
        if (_disposed) return;
        if (active != _tracking)
        {
            _tracking = active;
            if (active) CompositionTarget.Rendering += Rendering;
            else CompositionTarget.Rendering -= Rendering;
        }
        Sync();
    }

    private static void BrushChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        (sender.GetValue(ControllerProperty) as DockGlassSurface)?.Sync();
    private void Loaded(object sender, RoutedEventArgs e)
    {
        if (_source is null)
        {
            _source = PresentationSource.FromVisual(_window) as HwndSource;
            _source?.AddHook(WindowProc);
        }
        Sync(true);
    }
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Sync(true);
    private void PlacementChanged(object? sender, EventArgs e) => Sync(true);
    private void SizeChanged(object sender, SizeChangedEventArgs e) => Sync(true);
    private void LayoutUpdated(object? sender, EventArgs e) => Sync();
    private void Rendering(object? sender, EventArgs e) => Sync();
    private void ThemeChanged(object? sender, EventArgs e) => Sync();
    private void Closed(object? sender, EventArgs e) => Dispose();

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is NativeMethods.WmSettingChange or NativeMethods.WmThemeChanged)
        {
            _transparency = ReadTransparency();
            Sync();
        }
        else if (message == NativeMethods.WmWindowPosChanged) Sync(true);
        return 0;
    }

    private void Sync(bool reorder = false)
    {
        if (_disposed || _syncing) return;
        UiDiagnostics.Hit(UiActivity.GlassSync);
        _syncing = true;
        try
        {
            var fill = _surface.GetValue(FillProperty) as SolidColorBrush;
            var stroke = _surface.GetValue(StrokeProperty) as SolidColorBrush;
            if (_failed || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
                || !_transparency || AccessibilityThemeManager.IsHighContrastEnabled || SystemParameters.HighContrast
                || fill is null || fill.Color.A == 0 || !_window.IsVisible)
            {
                Release();
                return;
            }
            if (!_surface.IsLoaded || _surface.ActualWidth <= 0 || _surface.ActualHeight <= 0) return;
            var handle = new WindowInteropHelper(_window).Handle;
            if (handle == 0 || !DockNativeMethods.GetWindowRect(handle, out var native)) return;
            var bounds = new PixelRect(native.Left, native.Top, native.Right, native.Bottom);
            if (!bounds.HasArea) return;
            var dpi = PresentationSource.FromVisual(_window)!.CompositionTarget.TransformToDevice;
            var rect = _surface.TransformToAncestor(_window).TransformBounds(new Rect(_surface.RenderSize));
            var radius = _surface.CornerRadius.TopLeft;
            var radiusX = radius * rect.Width / _surface.ActualWidth * dpi.M11;
            var radiusY = radius * rect.Height / _surface.ActualHeight * dpi.M22;
            rect.Transform(dpi);
            _backdrop ??= new DockCompositionBackdrop();
            var visual = (Rect: rect, RadiusX: radiusX, RadiusY: radiusY,
                Opacity: _surface.Opacity * _window.Opacity, Fill: fill.Color, Stroke: stroke?.Color ?? Colors.Transparent);
            if (_lastVisual != visual)
            {
                _backdrop.Update(visual.Rect, visual.RadiusX, visual.RadiusY, visual.Opacity, visual.Fill, visual.Stroke);
                _lastVisual = visual;
            }
            // Alpha 1 retains native layered-window hit testing over the whole surface.
            if (_surface.Background is not SolidColorBrush { Color.A: 1 })
            {
                _surface.Background = new SolidColorBrush(Color.FromArgb(1, fill.Color.R, fill.Color.G, fill.Color.B));
                _surface.BorderBrush = Brushes.Transparent;
                _usingGlassFill = true;
            }
            _backdrop.Position(handle, bounds, reorder);
        }
        catch (Exception error)
        {
            _failed = true;
            Debug.WriteLine($"Dock glass unavailable: {error}");
            Release();
        }
        finally { _syncing = false; }
    }

    private void Release()
    {
        _backdrop?.Dispose();
        _backdrop = null;
        _lastVisual = null;
        if (_usingGlassFill)
        {
            _usingGlassFill = false;
            _surface.SetResourceReference(Border.BackgroundProperty, _fillKey);
            _surface.SetResourceReference(Border.BorderBrushProperty, _strokeKey);
        }
    }

    private static bool ReadTransparency()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int value || value != 0;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_tracking) CompositionTarget.Rendering -= Rendering;
        _source?.RemoveHook(WindowProc);
        _window.Loaded -= Loaded;
        _window.IsVisibleChanged -= VisibilityChanged;
        _window.LocationChanged -= PlacementChanged;
        _window.SizeChanged -= SizeChanged;
        _window.Closed -= Closed;
        _surface.LayoutUpdated -= LayoutUpdated;
        AccessibilityThemeManager.HighContrastChanged -= ThemeChanged;
        _surface.ClearValue(ControllerProperty);
        Release();
    }
}
