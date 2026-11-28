using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using BingLan.App.Audio;
using BingLan.Core.Models;

namespace BingLan.App.Windows;

/// <summary>A noninteractive, local playback spectrum. Audio is never stored or uploaded.</summary>
public sealed class AudioSpectrumView : FrameworkElement
{
    private const double BaselineThickness = 1.25;
    private const double CurveThickness = 1.75;

    public static readonly DependencyProperty SpectrumBrushProperty = DependencyProperty.Register(
        nameof(SpectrumBrush), typeof(Brush), typeof(AudioSpectrumView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarWidthProperty = DependencyProperty.Register(
        nameof(BarWidth), typeof(double), typeof(AudioSpectrumView),
        new FrameworkPropertyMetadata(DesktopExperienceRules.DefaultSpectrumBarWidth,
            FrameworkPropertyMetadataOptions.AffectsRender, null,
            (_, value) => DesktopExperienceRules.CoerceSpectrumBarWidth((double)value)));

    public static readonly DependencyProperty SpectrumStyleProperty = DependencyProperty.Register(
        nameof(SpectrumStyle), typeof(AudioSpectrumStyle), typeof(AudioSpectrumView),
        new FrameworkPropertyMetadata(AudioSpectrumStyle.Bars, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly float[] _levels = new float[SharedAudioSpectrumService.BarCount];
    private readonly Action _renderFrame;
    private IDisposable? _subscription;
    private int _framePending;
    private bool _suspended;
    private Pen? _curvePen;

    public AudioSpectrumView()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        _renderFrame = RenderFrame;
        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => StopListening();
        IsVisibleChanged += (_, _) => UpdateSubscription();
    }

    public Brush? SpectrumBrush
    {
        get => (Brush?)GetValue(SpectrumBrushProperty);
        set => SetValue(SpectrumBrushProperty, value);
    }

    public double BarWidth
    {
        get => (double)GetValue(BarWidthProperty);
        set => SetValue(BarWidthProperty, value);
    }

    public AudioSpectrumStyle SpectrumStyle
    {
        get => (AudioSpectrumStyle)GetValue(SpectrumStyleProperty);
        set => SetValue(SpectrumStyleProperty, value);
    }

    public void SetSuspended(bool suspended)
    {
        if (_suspended == suspended)
        {
            return;
        }
        _suspended = suspended;
        UpdateSubscription();
        InvalidateVisual();
    }

    private void UpdateSubscription()
    {
        if (IsLoaded && IsVisible && !_suspended && !Dispatcher.HasShutdownStarted)
        {
            _subscription ??= SharedAudioSpectrumService.Subscribe(RequestFrame);
        }
        else
        {
            StopListening();
        }
    }

    private void StopListening()
    {
        _subscription?.Dispose();
        _subscription = null;
        Array.Clear(_levels);
        InvalidateVisual();
    }

    private void RequestFrame()
    {
        if (!Dispatcher.HasShutdownStarted && Interlocked.Exchange(ref _framePending, 1) == 0)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, _renderFrame);
        }
    }

    private void RenderFrame()
    {
        Interlocked.Exchange(ref _framePending, 0);
        if (_subscription is null)
        {
            return;
        }
        SharedAudioSpectrumService.CopyLevels(_levels);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 360 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 100 : availableSize.Height);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (_suspended || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }
        var brush = SpectrumBrush ?? (Brush)GetValue(TextElement.ForegroundProperty);
        if (SpectrumStyle == AudioSpectrumStyle.Curve)
        {
            DrawCurve(drawingContext, brush);
            return;
        }

        var mirrored = SpectrumStyle == AudioSpectrumStyle.Mirror;
        var thickness = Math.Min(BaselineThickness, ActualHeight);
        var lineWidth = mirrored ? ActualWidth / 3 : ActualWidth;
        var lineY = mirrored ? (ActualHeight - thickness) / 2 : ActualHeight - thickness;
        drawingContext.PushOpacity(0.7);
        drawingContext.DrawRoundedRectangle(brush, null,
            new Rect((ActualWidth - lineWidth) / 2, lineY, lineWidth, thickness), thickness / 2, thickness / 2);
        drawingContext.Pop();

        var step = ActualWidth / _levels.Length;
        var barWidth = step * BarWidth;
        var radius = Math.Min(2.5, barWidth * 0.5);
        for (var column = 0; column < _levels.Length; column++)
        {
            var height = DisplayLevel(column) * ActualHeight;
            if (height <= thickness) continue;
            var top = mirrored ? (ActualHeight - height) / 2 : ActualHeight - height;
            drawingContext.DrawRoundedRectangle(brush, null,
                new Rect(column * step + (step - barWidth) * 0.5, top, barWidth, height),
                radius, radius);
        }
    }

    private double DisplayLevel(int column)
    {
        // Consecutive low-to-high bands alternate left and right of the center.
        var center = (_levels.Length - 1) / 2;
        var band = column <= center ? (center - column) * 2 : (column - center) * 2 - 1;
        var level = _levels[band];
        return 0.4 * level + 0.6 * level * level * (3 - 2 * level);
    }

    private void DrawCurve(DrawingContext drawingContext, Brush brush)
    {
        var peak = 0f;
        foreach (var level in _levels) peak = Math.Max(peak, level);
        // Fade the last trace out as the analyzer settles instead of leaving a bright flat line.
        var opacity = Math.Min(1, peak * ActualHeight / 6);
        if (opacity < 1d / 255) return;

        if (_curvePen is null || !ReferenceEquals(_curvePen.Brush, brush))
        {
            _curvePen = new Pen(brush, CurveThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (brush.IsFrozen) _curvePen.Freeze();
        }

        var inset = Math.Min(CurveThickness / 2, ActualHeight / 2);
        var amplitude = ActualHeight - 2 * inset;
        var step = ActualWidth / _levels.Length;
        Point BandPoint(int column) => new((column + 0.5) * step, ActualHeight - inset - amplitude * DisplayLevel(column));

        var path = new StreamGeometry();
        using (var stream = path.Open())
        {
            var previous = BandPoint(0);
            stream.BeginFigure(previous, false, false);
            for (var column = 1; column < _levels.Length; column++)
            {
                var current = BandPoint(column);
                var halfway = new Point((previous.X + current.X) / 2, (previous.Y + current.Y) / 2);
                stream.QuadraticBezierTo(previous, halfway, true, true);
                previous = current;
            }
            stream.LineTo(previous, true, true);
        }
        path.Freeze();
        drawingContext.PushOpacity(opacity);
        drawingContext.DrawGeometry(null, _curvePen, path);
        drawingContext.Pop();
    }
}
