using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using BingLan.App.Audio;

namespace BingLan.App.Windows;

/// <summary>A noninteractive, local playback spectrum. Audio is never stored or uploaded.</summary>
public sealed class AudioSpectrumView : FrameworkElement
{
    public static readonly DependencyProperty SpectrumBrushProperty = DependencyProperty.Register(
        nameof(SpectrumBrush), typeof(Brush), typeof(AudioSpectrumView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

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

    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        UpdateSubscription();
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
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }
        var brush = SpectrumBrush ?? (Brush)GetValue(TextElement.ForegroundProperty);
        var step = ActualWidth / _levels.Length;
        var barWidth = step * 0.68;
        var radius = Math.Min(2.5, barWidth * 0.5);
        for (var i = 0; i < _levels.Length; i++)
        {
            var height = _levels[i] * ActualHeight;
            if (height < 0.5)
            {
                continue;
            }
            drawingContext.DrawRoundedRectangle(brush, null,
                new Rect(i * step + (step - barWidth) * 0.5, ActualHeight - height, barWidth, height),
                radius, radius);
        }
    }
}
