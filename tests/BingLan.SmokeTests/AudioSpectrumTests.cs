using BingLan.Core.Audio;

namespace BingLan.SmokeTests;

internal static class AudioSpectrumTests
{
    internal static void Run()
    {
        var bass = AnalyzeTone(120, 48000);
        var treble = AnalyzeTone(4000, 48000);
        var treble44 = AnalyzeTone(4000, 44100);
        if (bass.PeakIndex is < 3 or > 16 || treble.PeakIndex is < 30 or > 43
            || treble.PeakIndex <= bass.PeakIndex + 15
            || Math.Abs(treble44.PeakIndex - treble.PeakIndex) > 2)
        {
            throw new InvalidOperationException("FFT peaks must follow frequency across playback sample rates.");
        }

        var analyzer = new SpectrumAnalyzer();
        var samples = Tone(1000, 48000, analyzer.FftSize);
        var levels = new float[analyzer.BarCount];
        for (var i = 0; i < 10; i++)
        {
            analyzer.PushMonoSamples(samples, 48000);
            analyzer.ComputeFrame(1d / 30, levels);
        }
        if (analyzer.MaxLevel < 0.2f)
        {
            throw new InvalidOperationException("Audible input must raise spectrum bars.");
        }
        for (var i = 0; i < 90; i++)
        {
            analyzer.ComputeFrame(1d / 30, levels);
        }
        if (!analyzer.IsSilent || analyzer.ComputeFrame(1d / 30, levels) || levels.Any(level => level != 0))
        {
            throw new InvalidOperationException("No new loopback packets must settle to zero and stop redraws.");
        }

        analyzer.PushMonoSamples(samples, 48000);
        analyzer.PushSilence(analyzer.FftSize, 48000);
        analyzer.ComputeFrame(1d / 30, levels);
        if (levels.Any(level => level != 0))
        {
            throw new InvalidOperationException("WASAPI silent packets must clear old audio.");
        }

        analyzer.PushMonoSamples(samples, 48000);
        analyzer.PushMonoSamples(new float[64], 44100);
        analyzer.ComputeFrame(1d / 30, levels);
        if (levels.Any(level => level != 0))
        {
            throw new InvalidOperationException("A changed device sample rate must discard old samples.");
        }

        analyzer.PushMonoSamples([float.NaN, float.PositiveInfinity, float.NegativeInfinity, 2f, -2f], 44100);
        analyzer.ComputeFrame(double.NaN, levels);
        if (levels.Any(level => !float.IsFinite(level) || level is < 0 or > 1))
        {
            throw new InvalidOperationException("Device samples must produce finite normalized bar heights.");
        }

        var tone = Tone(1000, 48000, SpectrumAnalyzer.DefaultFftSize);
        var mono = AnalyzeChannels(tone);
        var samePhase = AnalyzeChannels(tone, tone);
        var oppositePhase = AnalyzeChannels(tone, tone.Select(sample => -sample).ToArray());
        if (oppositePhase.Max() < 0.2f || mono.Where((level, index) =>
                Math.Abs(level - oppositePhase[index]) > 0.0001f
                || Math.Abs(level - samePhase[index]) > 0.0001f).Any())
        {
            throw new InvalidOperationException("Stereo channel polarity must not change a tone's spectrum or mono response.");
        }

        var mixed = AnalyzeChannels(Tone(120, 48000, SpectrumAnalyzer.DefaultFftSize),
            Tone(4000, 48000, SpectrumAnalyzer.DefaultFftSize));
        if (mixed[bass.PeakIndex] < 0.2f || mixed[treble.PeakIndex] < 0.2f)
        {
            throw new InvalidOperationException("Distinct frequencies in separate stereo channels must both remain visible.");
        }
    }

    private static float[] AnalyzeChannels(params float[][] channels)
    {
        var analyzer = new SpectrumAnalyzer();
        var samples = new float[analyzer.FftSize * channels.Length];
        for (var frame = 0; frame < analyzer.FftSize; frame++)
        {
            for (var channel = 0; channel < channels.Length; channel++)
            {
                samples[frame * channels.Length + channel] = channels[channel][frame];
            }
        }
        var levels = new float[analyzer.BarCount];
        for (var frame = 0; frame < 12; frame++)
        {
            analyzer.PushInterleavedSamples(samples, channels.Length, 48000);
            analyzer.ComputeFrame(1d / 30, levels);
        }
        return levels;
    }

    private static (int PeakIndex, float Height) AnalyzeTone(double frequency, int sampleRate)
    {
        var analyzer = new SpectrumAnalyzer();
        var levels = new float[analyzer.BarCount];
        var samples = Tone(frequency, sampleRate, analyzer.FftSize);
        for (var frame = 0; frame < 12; frame++)
        {
            analyzer.PushMonoSamples(samples, sampleRate);
            analyzer.ComputeFrame(1d / 30, levels);
        }
        var max = levels.Max();
        if (max < 0.2f)
        {
            throw new InvalidOperationException("A test tone must produce visible bars.");
        }
        return (Array.IndexOf(levels, max), max);
    }

    private static float[] Tone(double frequency, int sampleRate, int length) => Enumerable.Range(0, length)
        .Select(index => (float)(0.25 * Math.Sin(2 * Math.PI * frequency * index / sampleRate))).ToArray();
}
