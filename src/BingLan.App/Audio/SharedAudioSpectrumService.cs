using BingLan.Core.Audio;

namespace BingLan.App.Audio;

/// <summary>Visible spectrum surfaces share one loopback stream, FFT and frame clock.</summary>
internal static class SharedAudioSpectrumService
{
    internal const int BarCount = 48;
    private static readonly object Gate = new();
    private static readonly List<Action> Subscribers = [];
    private static readonly float[] Levels = new float[BarCount];
    private static Action[] _notifications = [];
    private static WasapiLoopbackCapture? _capture;
    private static Task _previousStop = Task.CompletedTask;
    private static long _generation;

    internal static IDisposable Subscribe(Action frameAvailable)
    {
        lock (Gate)
        {
            Subscribers.Add(frameAvailable);
            _notifications = Subscribers.ToArray();
            if (_capture is null)
            {
                Array.Clear(Levels);
                var generation = ++_generation;
                var capture = new WasapiLoopbackCapture(new SpectrumAnalyzer(BarCount),
                    levels => PublishFrame(generation, levels));
                _capture = capture;
                var previousStop = _previousStop;
                // A quick hide/show waits off the UI thread for the old stream to release its device.
                _ = Task.Run(async () =>
                {
                    await previousStop.ConfigureAwait(false);
                    capture.Start();
                });
            }
        }
        return new Subscription(frameAvailable);
    }

    internal static void CopyLevels(Span<float> destination)
    {
        lock (Gate)
        {
            Levels.CopyTo(destination);
        }
    }

    private static void PublishFrame(long generation, float[] levels)
    {
        Action[] notifications;
        lock (Gate)
        {
            if (_capture is null || generation != _generation)
            {
                return;
            }
            levels.CopyTo(Levels, 0);
            notifications = _notifications;
        }
        foreach (var notify in notifications)
        {
            notify();
        }
    }

    private sealed class Subscription(Action frameAvailable) : IDisposable
    {
        private Action? _frameAvailable = frameAvailable;

        public void Dispose()
        {
            var callback = Interlocked.Exchange(ref _frameAvailable, null);
            if (callback is null)
            {
                return;
            }
            lock (Gate)
            {
                Subscribers.Remove(callback);
                _notifications = Subscribers.ToArray();
                if (Subscribers.Count == 0 && _capture is not null)
                {
                    _capture.Dispose();
                    _previousStop = _capture.Completion;
                    _capture = null;
                    Array.Clear(Levels);
                }
            }
        }
    }
}
