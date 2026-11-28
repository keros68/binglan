using BingLan.Core.Audio;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.Themes;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BingLan.SmokeTests;

internal static class AudioSpectrumTests
{
    internal static void Run()
    {
        CheckBarWidthSettings();
        CheckStyleSettings();
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

    private static void CheckBarWidthSettings()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        var state = LocalStateStore.CreateDefault();
        var spectrum = state.DesktopExperience.GetComponent(DesktopComponentKind.AudioVisualizer);
        Check(spectrum.SpectrumBarWidth == 0.4d, "频谱默认柱宽应为频段间距的 40%");
        foreach (var (input, expected) in new[]
                 { (-1d, 0.25d), (2d, 0.8d), (double.NaN, 0.4d), (double.PositiveInfinity, 0.4d) })
        {
            spectrum.SpectrumBarWidth = input;
            Check(spectrum.SpectrumBarWidth == expected, "频谱柱宽应限制范围并处理非有限数");
        }
        spectrum.SpectrumBarWidth = 0.63d;
        spectrum.IsVisible = true;
        const string screenKey = "DISPLAY#SPECTRUM#SECONDARY";
        var profile = ScreenProfileAccessor.CopyPrimaryProfileToScreen(
            state, screenKey, new PixelRect(1920, 0, 3840, 1080), 96).Profile;
        var secondarySpectrum = profile.Experience.GetComponent(DesktopComponentKind.AudioVisualizer);
        Check(secondarySpectrum.SpectrumBarWidth == 0.63d, "复制主屏设置应保留频谱柱宽");
        secondarySpectrum.SpectrumBarWidth = 0.27d;
        Check(spectrum.SpectrumBarWidth == 0.63d, "副屏频谱柱宽应独立调整");

        var directory = Path.Combine(Path.GetTempPath(), $"BingLanSpectrum-{Guid.NewGuid():N}");
        try
        {
            var store = new LocalStateStore(directory);
            store.Save(state);
            var reloaded = store.Load();
            Check(reloaded.DesktopExperience.GetComponent(DesktopComponentKind.AudioVisualizer).SpectrumBarWidth == 0.63d
                && reloaded.MultiScreen!.Screens[screenKey].Experience
                    .GetComponent(DesktopComponentKind.AudioVisualizer).SpectrumBarWidth == 0.27d,
                "主副屏自定义频谱柱宽应保存重载");

            var json = JsonNode.Parse(File.ReadAllText(store.StatePath))!;
            json["SchemaVersion"] = 69;
            foreach (var experience in new[] { json["DesktopExperience"]!, json["MultiScreen"]!["Screens"]![screenKey]!["Experience"]! })
            {
                foreach (var component in experience["Components"]!.AsArray())
                {
                    component!.AsObject().Remove("SpectrumBarWidth");
                }
            }
            File.WriteAllText(store.StatePath, json.ToJsonString());
            var migrated = store.Load();
            Check(migrated.SchemaVersion == AppState.CurrentSchemaVersion
                && migrated.DesktopExperience.GetComponent(DesktopComponentKind.AudioVisualizer).SpectrumBarWidth == 0.4d
                && migrated.MultiScreen!.Screens[screenKey].Experience
                    .GetComponent(DesktopComponentKind.AudioVisualizer).SpectrumBarWidth == 0.4d,
                "缺少柱宽的 v69 配置应为主副屏恢复 40% 默认值");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        var area = new ThemeArea(0, 0, 1920, 1080);
        var version = new Version(0, 1, 0);
        var theme = ThemeRules.Export(state, "频谱测试", area, version);
        using var archive = new MemoryStream();
        ThemeArchive.Write(archive, theme);
        archive.Position = 0;
        var read = ThemeArchive.Read(archive, version);
        Check(read.Succeeded, "频谱主题应可导出重读");
        var imported = DesktopExperienceRules.CreateDefault();
        ThemeRules.ApplyVisuals(read.Package!, imported, area);
        Check(imported.GetComponent(DesktopComponentKind.AudioVisualizer).SpectrumBarWidth == 0.63d,
            "频谱柱宽应随主题导入导出");
        var token = theme.Tokens.Components.Single(component => component.Kind == DesktopComponentKind.AudioVisualizer);
        token.SpectrumBarWidth = 9d;
        ThemeRules.Normalize(theme);
        Check(token.SpectrumBarWidth == 0.8d, "主题中的柱宽应限制在有效范围");
        var legacyToken = JsonSerializer.Deserialize<ThemeComponentTokens>("{}")!;
        Check(legacyToken.SpectrumBarWidth == 0.4d, "旧主题缺少柱宽应使用 40% 默认值");
    }

    private static void CheckStyleSettings()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        var state = LocalStateStore.CreateDefault();
        var spectrum = state.DesktopExperience.GetComponent(DesktopComponentKind.AudioVisualizer);
        Check(spectrum.SpectrumStyle == AudioSpectrumStyle.Bars, "频谱默认样式应为细线音柱");
        foreach (var style in Enum.GetValues<AudioSpectrumStyle>())
        {
            spectrum.SpectrumStyle = style;
            Check(spectrum.SpectrumStyle == style, "三种频谱样式应可选择");
        }
        foreach (var value in new[] { -1, 3, int.MaxValue })
        {
            spectrum.SpectrumStyle = (AudioSpectrumStyle)value;
            Check(spectrum.SpectrumStyle == AudioSpectrumStyle.Bars, "非法频谱样式应恢复细线音柱");
        }
        spectrum.SpectrumStyle = AudioSpectrumStyle.Mirror;
        spectrum.SpectrumBarWidth = 0.63d;
        const string screenKey = "DISPLAY#SPECTRUM#STYLE";
        var secondary = ScreenProfileAccessor.CopyPrimaryProfileToScreen(
            state, screenKey, new PixelRect(1920, 0, 3840, 1080), 96).Profile.Experience
            .GetComponent(DesktopComponentKind.AudioVisualizer);
        Check(secondary.SpectrumStyle == AudioSpectrumStyle.Mirror, "复制主屏设置应保留频谱样式");
        secondary.SpectrumStyle = AudioSpectrumStyle.Curve;
        Check(spectrum.SpectrumStyle == AudioSpectrumStyle.Mirror && secondary.SpectrumBarWidth == 0.63d,
            "副屏应可独立切换曲线并保留音柱柱宽");

        var directory = Path.Combine(Path.GetTempPath(), $"BingLanSpectrumStyle-{Guid.NewGuid():N}");
        try
        {
            var store = new LocalStateStore(directory);
            store.Save(state);
            var savedJson = File.ReadAllText(store.StatePath);
            var reloaded = store.Load();
            Check(reloaded.DesktopExperience.GetComponent(DesktopComponentKind.AudioVisualizer).SpectrumStyle
                    == AudioSpectrumStyle.Mirror
                && reloaded.MultiScreen!.Screens[screenKey].Experience
                    .GetComponent(DesktopComponentKind.AudioVisualizer).SpectrumStyle == AudioSpectrumStyle.Curve,
                "主副屏频谱样式应分别保存重载");

            foreach (var schema in new[] { 69, AppState.CurrentSchemaVersion })
            {
                var json = JsonNode.Parse(savedJson)!;
                json["SchemaVersion"] = schema;
                foreach (var experience in new[] { json["DesktopExperience"]!, json["MultiScreen"]!["Screens"]![screenKey]!["Experience"]! })
                {
                    foreach (var component in experience["Components"]!.AsArray())
                    {
                        component!.AsObject().Remove("SpectrumStyle");
                    }
                }
                File.WriteAllText(store.StatePath, json.ToJsonString());
                var legacy = store.Load();
                Check(legacy.SchemaVersion == AppState.CurrentSchemaVersion
                    && legacy.DesktopExperience.GetComponent(DesktopComponentKind.AudioVisualizer).SpectrumStyle
                        == AudioSpectrumStyle.Bars
                    && legacy.MultiScreen!.Screens[screenKey].Experience
                        .GetComponent(DesktopComponentKind.AudioVisualizer).SpectrumStyle == AudioSpectrumStyle.Bars
                    && legacy.DesktopExperience.GetComponent(DesktopComponentKind.AudioVisualizer).SpectrumBarWidth == 0.63d,
                    "旧配置缺少样式应为主副屏恢复细线音柱并保留柱宽");
            }

            var invalidJson = JsonNode.Parse(savedJson)!;
            foreach (var experience in new[] { invalidJson["DesktopExperience"]!, invalidJson["MultiScreen"]!["Screens"]![screenKey]!["Experience"]! })
            {
                foreach (var component in experience["Components"]!.AsArray())
                {
                    component!["SpectrumStyle"] = 99;
                }
            }
            File.WriteAllText(store.StatePath, invalidJson.ToJsonString());
            var normalized = store.Load();
            Check(normalized.DesktopExperience.GetComponent(DesktopComponentKind.AudioVisualizer).SpectrumStyle
                    == AudioSpectrumStyle.Bars
                && normalized.MultiScreen!.Screens[screenKey].Experience
                    .GetComponent(DesktopComponentKind.AudioVisualizer).SpectrumStyle == AudioSpectrumStyle.Bars
                && normalized.DesktopExperience.GetComponent(DesktopComponentKind.AudioVisualizer).SpectrumBarWidth == 0.63d,
                "非法样式配置应恢复默认样式且保留其他设置");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        var area = new ThemeArea(0, 0, 1920, 1080);
        var version = new Version(0, 1, 0);
        foreach (var style in Enum.GetValues<AudioSpectrumStyle>())
        {
            spectrum.SpectrumStyle = style;
            var theme = ThemeRules.Export(state, "频谱样式测试", area, version);
            using var archive = new MemoryStream();
            ThemeArchive.Write(archive, theme);
            archive.Position = 0;
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true))
            using (var reader = new StreamReader(zip.GetEntry("tokens.json")!.Open()))
            {
                var tokens = JsonNode.Parse(reader.ReadToEnd())!;
                Check(tokens["Components"]!.AsArray().Single(component => component!["Kind"]!.GetValue<string>() == "AudioVisualizer")!
                        ["SpectrumStyle"]!.GetValue<string>() == style.ToString(),
                    "主题中的频谱样式应放在组件视觉令牌中并使用枚举名称");
            }
            archive.Position = 0;
            var read = ThemeArchive.Read(archive, version);
            Check(read.Succeeded, "三种频谱样式的主题应可重读");
            var imported = DesktopExperienceRules.CreateDefault();
            ThemeRules.ApplyVisuals(read.Package!, imported, area);
            Check(imported.GetComponent(DesktopComponentKind.AudioVisualizer).SpectrumStyle == style,
                "频谱样式应随主题导入导出");
            var token = theme.Tokens.Components.Single(component => component.Kind == DesktopComponentKind.AudioVisualizer);
            token.SpectrumStyle = (AudioSpectrumStyle)99;
            ThemeRules.Normalize(theme);
            Check(token.SpectrumStyle == AudioSpectrumStyle.Bars, "主题非法频谱样式应恢复默认值");
        }
        Check(JsonSerializer.Deserialize<ThemeComponentTokens>("{}")!.SpectrumStyle == AudioSpectrumStyle.Bars,
            "旧主题缺少样式应使用细线音柱");
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
