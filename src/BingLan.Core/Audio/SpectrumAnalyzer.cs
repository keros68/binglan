namespace BingLan.Core.Audio;

/// <summary>
/// Pure-logic FFT and logarithmic bar spectrum analyzer.
/// Reuses buffers on the audio/frame path; sample appends may run concurrently with one frame consumer.
/// </summary>
public sealed class SpectrumAnalyzer
{
    public const int DefaultFftSize = 2048;
    public const int MinBarCount = 16;
    public const int MaxBarCount = 96;

    private readonly int _fftSize;
    private readonly int _fftBits;
    private readonly int _barCount;
    private float[][] _channelBuffers;
    private float[][] _channelFrames;
    private readonly float[] _window;
    private readonly float[] _real;
    private readonly float[] _imag;
    private readonly float[] _magnitudes;
    private readonly float[] _rawBands;
    private readonly float[] _smoothedBands;
    private readonly int[] _binStart;
    private readonly int[] _binEnd;
    private readonly float[] _tiltGain;
    private readonly int[] _bitReverse;
    private readonly float[] _cosTable;
    private readonly float[] _sinTable;

    private readonly object _sampleLock = new();
    private int _writeIndex;
    private int _sampleRate;
    private int _lastConfiguredSampleRate;
    private long _samplesWritten;
    private long _lastProcessedSamples;
    private double _silenceSeconds;
    private float _autoPeak = 0.08f;

    public SpectrumAnalyzer(int barCount = 48, int fftSize = DefaultFftSize, int initialSampleRate = 48000)
    {
        if ((fftSize & (fftSize - 1)) != 0 || fftSize < 256 || fftSize > 65536)
        {
            throw new ArgumentOutOfRangeException(nameof(fftSize), "FFT size must be a power of two from 256 to 65536.");
        }

        _fftSize = fftSize;
        _fftBits = BitOperationsLog2(fftSize);
        _barCount = Math.Clamp(barCount, MinBarCount, MaxBarCount);
        _sampleRate = Math.Clamp(initialSampleRate, 8000, 192000);

        _channelBuffers = [new float[_fftSize]];
        _channelFrames = [new float[_fftSize]];
        _window = new float[_fftSize];
        _real = new float[_fftSize];
        _imag = new float[_fftSize];
        _magnitudes = new float[_fftSize / 2];
        _rawBands = new float[_barCount];
        _smoothedBands = new float[_barCount];
        _binStart = new int[_barCount];
        _binEnd = new int[_barCount];
        _tiltGain = new float[_barCount];
        _bitReverse = new int[_fftSize];
        _cosTable = new float[_fftSize / 2];
        _sinTable = new float[_fftSize / 2];

        for (var i = 0; i < _fftSize; i++)
        {
            // Hann window reduces spectral leakage across bars.
            _window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (_fftSize - 1)));
            _bitReverse[i] = ReverseBits(i, _fftBits);
        }

        for (var i = 0; i < _fftSize / 2; i++)
        {
            var angle = -2f * MathF.PI * i / _fftSize;
            _cosTable[i] = MathF.Cos(angle);
            _sinTable[i] = MathF.Sin(angle);
        }

        RebuildFrequencyBins(_sampleRate);
    }

    public int BarCount => _barCount;
    public int FftSize => _fftSize;
    public bool IsSilent => _silenceSeconds >= SilenceThresholdSeconds && MaxLevel < 0.004f;
    public float MaxLevel { get; private set; }
    public double SilenceThresholdSeconds { get; set; } = 1.5;

    /// <summary>
    /// Appends mono samples in [-1, 1] from the audio capture thread.
    /// </summary>
    public void PushMonoSamples(ReadOnlySpan<float> samples, int sampleRate) =>
        PushInterleavedSamples(samples, 1, sampleRate);

    /// <summary>Appends signed interleaved samples, preserving each channel's spectrum.</summary>
    public void PushInterleavedSamples(ReadOnlySpan<float> samples, int channels, int sampleRate)
    {
        if (channels is < 1 or > 32 || samples.Length % channels != 0)
        {
            throw new ArgumentException("Samples must contain complete frames of 1 to 32 channels.", nameof(channels));
        }
        if (samples.IsEmpty)
        {
            return;
        }

        lock (_sampleLock)
        {
            UpdateSampleRate(sampleRate);
            UpdateChannels(channels);

            var write = _writeIndex;
            var mask = _fftSize - 1;
            for (var i = 0; i < samples.Length; i += channels)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    var sample = samples[i + channel];
                    _channelBuffers[channel][write] = float.IsFinite(sample) ? Math.Clamp(sample, -1f, 1f) : 0f;
                }
                write = (write + 1) & mask;
            }

            _writeIndex = write;
            _samplesWritten += samples.Length / channels;
        }
    }

    /// <summary>
    /// Appends a block of silence (used when WASAPI sets AUDCLNT_BUFFERFLAGS_SILENT).
    /// </summary>
    public void PushSilence(int frameCount, int sampleRate, int channels = 1)
    {
        if (channels is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }
        if (frameCount <= 0)
        {
            return;
        }

        lock (_sampleLock)
        {
            UpdateSampleRate(sampleRate);
            UpdateChannels(channels);

            var count = Math.Min(frameCount, _fftSize);
            var write = _writeIndex;
            var mask = _fftSize - 1;
            for (var i = 0; i < count; i++)
            {
                foreach (var ring in _channelBuffers)
                {
                    ring[write] = 0f;
                }
                write = (write + 1) & mask;
            }

            _writeIndex = write;
            _samplesWritten += frameCount;
        }
    }

    /// <summary>
    /// Advances one visual frame by <paramref name="deltaSeconds"/> and writes normalized [0, 1]
    /// bar heights into <paramref name="destination"/>. Returns true if the visual output changed
    /// and should be redrawn; returns false once fully settled in silence.
    /// </summary>
    public bool ComputeFrame(double deltaSeconds, Span<float> destination)
    {
        if (destination.Length < _barCount)
        {
            throw new ArgumentException($"Destination length must be at least {_barCount}.", nameof(destination));
        }

        var dt = (float)Math.Clamp(double.IsFinite(deltaSeconds) ? deltaSeconds : 1.0 / 30.0, 1.0 / 240.0, 0.25);
        bool hasNewAudio;
        int sampleRate;
        float[][] channelFrames;
        var peakAbs = 0f;

        lock (_sampleLock)
        {
            hasNewAudio = _samplesWritten != _lastProcessedSamples;
            _lastProcessedSamples = _samplesWritten;
            sampleRate = _sampleRate;
            channelFrames = _channelFrames;

            if (hasNewAudio)
            {
                var start = _writeIndex;
                var mask = _fftSize - 1;
                for (var channel = 0; channel < _channelBuffers.Length; channel++)
                {
                    for (var i = 0; i < _fftSize; i++)
                    {
                        var sample = _channelBuffers[channel][(start + i) & mask];
                        peakAbs = MathF.Max(peakAbs, MathF.Abs(sample));
                        channelFrames[channel][i] = sample * _window[i];
                    }
                }
            }
        }

        if (sampleRate != _lastConfiguredSampleRate)
        {
            RebuildFrequencyBins(sampleRate);
        }

        var isAudioActive = hasNewAudio && peakAbs > 1e-5f;
        if (isAudioActive)
        {
            _silenceSeconds = 0;
            Array.Clear(_magnitudes);
            // Average spectral power, so phase differences cannot cancel audible channels.
            // Work is O(channels * FFT size * log FFT size), bounded to 32 device channels.
            foreach (var frame in channelFrames)
            {
                frame.CopyTo(_real, 0);
                Array.Clear(_imag);
                ExecuteFftInPlace();
            }
            for (var i = 0; i < _magnitudes.Length; i++)
            {
                _magnitudes[i] = MathF.Sqrt(_magnitudes[i] / channelFrames.Length);
            }
            MapToLogBands();
        }
        else
        {
            _silenceSeconds += dt;
            Array.Clear(_rawBands);
        }

        // Fast rise, smooth gravity-like fall (frame-rate independent exponential smoothing).
        var attackAlpha = 1f - MathF.Exp(-dt * 28f);
        var decayAlpha = 1f - MathF.Exp(-dt * 8.5f);
        var maxLevel = 0f;
        var anyNonZeroBefore = MaxLevel > 0.0005f;

        for (var i = 0; i < _barCount; i++)
        {
            var target = _rawBands[i];
            var current = _smoothedBands[i];
            var alpha = target > current ? attackAlpha : decayAlpha;
            var next = current + (target - current) * alpha;
            if (next < 0.0015f)
            {
                next = 0f;
            }

            _smoothedBands[i] = next;
            if (next > maxLevel)
            {
                maxLevel = next;
            }
        }

        // Gentle 3-tap spatial smoothing keeps neighboring bars visually cohesive.
        for (var i = 0; i < _barCount; i++)
        {
            var left = i > 0 ? _smoothedBands[i - 1] : _smoothedBands[i];
            var center = _smoothedBands[i];
            var right = i + 1 < _barCount ? _smoothedBands[i + 1] : _smoothedBands[i];
            var blended = Math.Clamp(left * 0.14f + center * 0.72f + right * 0.14f, 0f, 1f);
            destination[i] = blended;
        }

        MaxLevel = maxLevel;
        return isAudioActive || maxLevel > 0.0005f || anyNonZeroBefore;
    }

    private void UpdateSampleRate(int sampleRate)
    {
        if (sampleRate is < 8000 or > 192000 || sampleRate == _sampleRate)
        {
            return;
        }

        _sampleRate = sampleRate;
        foreach (var ring in _channelBuffers)
        {
            Array.Clear(ring);
        }
        _writeIndex = 0;
    }

    private void UpdateChannels(int channels)
    {
        if (_channelBuffers.Length == channels)
        {
            return;
        }
        _channelBuffers = Enumerable.Range(0, channels).Select(_ => new float[_fftSize]).ToArray();
        _channelFrames = Enumerable.Range(0, channels).Select(_ => new float[_fftSize]).ToArray();
        _writeIndex = 0;
    }

    private void RebuildFrequencyBins(int sampleRate)
    {
        _lastConfiguredSampleRate = sampleRate;
        var nyquist = sampleRate * 0.5f;
        var minFreq = 35f;
        var maxFreq = MathF.Min(14500f, nyquist * 0.92f);
        var logMin = MathF.Log(minFreq);
        var logMax = MathF.Log(maxFreq);
        var maxBin = _fftSize / 2 - 1;

        var previousEnd = 1;
        for (var i = 0; i < _barCount; i++)
        {
            var t0 = (float)i / _barCount;
            var t1 = (float)(i + 1) / _barCount;
            var f0 = MathF.Exp(logMin + (logMax - logMin) * t0);
            var f1 = MathF.Exp(logMin + (logMax - logMin) * t1);

            var start = Math.Clamp((int)MathF.Floor(f0 * _fftSize / sampleRate), 1, maxBin);
            var end = Math.Clamp((int)MathF.Ceiling(f1 * _fftSize / sampleRate), start, maxBin);
            if (start < previousEnd && i > 0)
            {
                start = Math.Min(previousEnd, maxBin);
                end = Math.Max(start, end);
            }

            _binStart[i] = start;
            _binEnd[i] = end;
            previousEnd = Math.Min(end, maxBin);

            // Subtle high-frequency tilt so trebles stay lively alongside bass.
            var normPos = (float)i / Math.Max(1, _barCount - 1);
            _tiltGain[i] = 0.85f + 0.75f * MathF.Pow(normPos, 0.85f);
        }
    }

    private void ExecuteFftInPlace()
    {
        var n = _fftSize;
        for (var i = 0; i < n; i++)
        {
            var j = _bitReverse[i];
            if (j > i)
            {
                (_real[i], _real[j]) = (_real[j], _real[i]);
                (_imag[i], _imag[j]) = (_imag[j], _imag[i]);
            }
        }

        for (var size = 2; size <= n; size <<= 1)
        {
            var half = size >> 1;
            var tableStep = n / size;
            for (var start = 0; start < n; start += size)
            {
                var tableIndex = 0;
                for (var k = start; k < start + half; k++)
                {
                    var wr = _cosTable[tableIndex];
                    var wi = _sinTable[tableIndex];
                    tableIndex += tableStep;

                    var kr = _real[k + half];
                    var ki = _imag[k + half];
                    var tr = wr * kr - wi * ki;
                    var ti = wr * ki + wi * kr;

                    _real[k + half] = _real[k] - tr;
                    _imag[k + half] = _imag[k] - ti;
                    _real[k] += tr;
                    _imag[k] += ti;
                }
            }
        }

        var scale = 2.0f / n;
        var halfN = n / 2;
        for (var i = 0; i < halfN; i++)
        {
            var r = _real[i];
            var im = _imag[i];
            _magnitudes[i] += (r * r + im * im) * scale * scale;
        }
    }

    private void MapToLogBands()
    {
        var framePeak = 0f;
        for (var i = 0; i < _barCount; i++)
        {
            var start = _binStart[i];
            var end = _binEnd[i];
            var sumSq = 0f;
            var peak = 0f;
            var count = end - start + 1;

            for (var b = start; b <= end; b++)
            {
                var m = _magnitudes[b];
                sumSq += m * m;
                if (m > peak)
                {
                    peak = m;
                }
            }

            var rms = MathF.Sqrt(sumSq / count);
            var energy = (rms * 0.65f + peak * 0.35f) * _tiltGain[i];
            if (energy > framePeak)
            {
                framePeak = energy;
            }

            _rawBands[i] = energy;
        }

        // Slow automatic gain ceiling so both quiet background music and loud tracks look balanced.
        var targetPeak = Math.Clamp(framePeak, 0.04f, 0.85f);
        _autoPeak = targetPeak > _autoPeak
            ? _autoPeak + (targetPeak - _autoPeak) * 0.18f
            : _autoPeak + (targetPeak - _autoPeak) * 0.015f;

        const float minDb = -52f;
        const float maxDb = 0f;
        for (var i = 0; i < _barCount; i++)
        {
            var normalized = _rawBands[i] / MathF.Max(_autoPeak, 0.02f);
            var db = 20f * MathF.Log10(normalized + 1e-6f);
            var height = Math.Clamp((db - minDb) / (maxDb - minDb), 0f, 1f);
            // Gentle curve keeps noise floor clean while emphasizing musical dynamics.
            _rawBands[i] = MathF.Pow(height, 1.35f);
        }
    }

    private static int BitOperationsLog2(int value)
    {
        var bits = 0;
        while ((value >>= 1) != 0)
        {
            bits++;
        }

        return bits;
    }

    private static int ReverseBits(int value, int bitCount)
    {
        var result = 0;
        for (var i = 0; i < bitCount; i++)
        {
            result = (result << 1) | (value & 1);
            value >>= 1;
        }

        return result;
    }
}
