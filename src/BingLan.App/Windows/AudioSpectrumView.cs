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
    public static readonly DependencyProperty SpectrumBrushProperty = DependencyProperty.Register(
        nameof(SpectrumBrush), typeof(Brush), typeof(AudioSpectrumView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarWidthProperty = DependencyProperty.Register(
        nameof(BarWidth), typeof(double), typeof(AudioSpectrumView),
        new FrameworkPropertyMetadata(DesktopExperienceRules.DefaultSpectrumBarWidth,
            FrameworkPropertyMetadataOptions.AffectsRender, null,
            (_, value) => DesktopExperienceRules.CoerceSpectrumBarWidth((double)value)));

    private readonly float[] _levels = new float[SharedAudioSpectrumService.BarCount];
    private readonly Action _renderFrame;
    private IDisposable? _subscription;
    private int _framePending;
    private bool _suspended;

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
        var step = ActualWidth / _levels.Length;
        var barWidth = step * BarWidth;
        var radius = Math.Min(2.5, barWidth * 0.5);
        var restingHeight = Math.Min(2d, ActualHeight);
        var center = (_levels.Length - 1) / 2;
        for (var i = 0; i < _levels.Length; i++)
        {
            var level = _levels[i];
            // Keep quiet overtones visible while emphasizing differences in the midrange.
            var displayLevel = 0.4 * level + 0.6 * level * level * (3 - 2 * level);
            var height = Math.Max(restingHeight, displayLevel * ActualHeight);
            // Place successive low-to-high bands alternately on either side of the center.
            var column = i % 2 == 0 ? center - i / 2 : center + (i + 1) / 2;
            drawingContext.DrawRoundedRectangle(brush, null,
                new Rect(column * step + (step - barWidth) * 0.5, ActualHeight - height, barWidth, height),
                radius, radius);
        }
    }
}
