using Fmp.Core.Audio;
using Fmp.Core.Visualization;

namespace Fmp.Core.Rendering;

/// <summary>
/// Produces real per-voice stems for generic VGM files whose device exposes a
/// selected-channel register bus. Supported Mega Drive stems are the six
/// YM2612 FM voices plus the three SN76489 tones and its noise voice.
/// </summary>
internal static class VgmScopeRenderer
{
    public static ScopeRenderer.ScopeResult Render(
        string inputPath,
        string outputDir,
        string masterAudioPath,
        int sampleRate,
        int loopCount,
        double fadeSeconds,
        double tailSeconds,
        double? maxDurationSeconds)
    {
        VgmDocument document = VgmDocumentCache.Load(inputPath);
        StemSpec[] specs = BuildSpecs(document);
        if (specs.Length == 0)
            return null;

        Directory.CreateDirectory(outputDir);
        string audioDir = Path.Combine(Path.GetDirectoryName(masterAudioPath) ?? outputDir);
        Directory.CreateDirectory(audioDir);

        int channelCount = specs.Length;
        var renderers = new VgmAudioRenderer[channelCount];
        var writers = new WavWriter[channelCount];
        for (int channel = 0; channel < channelCount; channel++)
        {
            StemSpec spec = specs[channel];
            renderers[channel] = new VgmAudioRenderer(
                document.Devices,
                sampleRate,
                document.Assets,
                channelFilterType: spec.FilterType,
                channelFilter: spec.Channel);
            foreach (VgmSampleAsset asset in document.Assets)
                renderers[channel].LoadAsset(asset);
            writers[channel] = new WavWriter(
                Path.Combine(audioDir, spec.Name + ".wav"),
                sampleRate,
                channels: 1);
        }
        var pcmBuffers = new short[channelCount][];
        var monoBuffers = new short[channelCount][];
        for (int channel = 0; channel < channelCount; channel++)
        {
            pcmBuffers[channel] = new short[2048];
            monoBuffers[channel] = new short[1024];
        }
        var fadeGains = new double[1024];

        var result = new ScopeRenderer.ScopeResult
        {
            Success = false,
            InputPath = inputPath,
            OutputDir = outputDir,
            SampleRate = sampleRate,
        };
        result.Stems.Add(new ScopeRenderer.StemResult
        {
            Name = "master",
            Label = "Master",
            SemanticClass = ScopeSemanticClass.Mixed,
            StableOrder = 0,
            DefaultColor = "#7aa4ff",
            WavPath = masterAudioPath,
            Channels = 2,
            Success = File.Exists(masterAudioPath),
        });

        long rendered = 0;
        try
        {
            long expandedEnd = ScaleSample(ExpandedEndSample(document, loopCount), sampleRate);
            long maxSamples = maxDurationSeconds.HasValue
                ? Math.Max(1, (long)Math.Round(maxDurationSeconds.Value * sampleRate))
                : long.MaxValue;
            long baseEnd = Math.Min(expandedEnd, maxSamples);
            long tail = Math.Max(0, (long)Math.Round(tailSeconds * sampleRate));
            long end = Math.Min(maxSamples, checked(baseEnd + tail));
            long fadeSamples = Math.Max(0, (long)Math.Round(fadeSeconds * sampleRate));
            long fadeStart = Math.Max(0, baseEnd - fadeSamples);

            foreach (VgmRegisterWrite source in ExpandWrites(document, loopCount))
            {
                long target = ScaleSample(source.SourceSample, sampleRate);
                if (target >= baseEnd)
                    break;

                RenderUntil(
                    renderers,
                    writers,
                    pcmBuffers,
                    monoBuffers,
                    ref rendered,
                    target,
                    fadeStart,
                    baseEnd,
                    fadeGains);
                var normalized = new TimedChipWrite(
                    target,
                    source.Device,
                    source.Port,
                    source.Address,
                    source.Data);
                foreach (VgmAudioRenderer renderer in renderers)
                    renderer.Write(normalized);
            }

            RenderUntil(
                renderers, writers, pcmBuffers, monoBuffers, ref rendered,
                end, fadeStart, baseEnd, fadeGains);
            result.MasterSamples = rendered;
            result.Success = true;
            result.CompletionReason = "completed";
            for (int channel = 0; channel < channelCount; channel++)
            {
                StemSpec spec = specs[channel];
                writers[channel].Close();
                result.Stems.Add(new ScopeRenderer.StemResult
                {
                    Name = spec.Name,
                    Label = spec.Label,
                    SemanticClass = spec.SemanticClass,
                    PresentationTrackId = spec.PresentationTrackId,
                    StableOrder = spec.StableOrder,
                    WindowWidth = spec.WindowWidth,
                    DefaultAmplification = spec.DefaultAmplification,
                    DefaultColor = spec.DefaultColor,
                    WavPath = Path.Combine(audioDir, spec.Name + ".wav"),
                    RenderedSamples = rendered,
                    Channels = 1,
                    Success = true,
                });
            }
            result.Stems[0].RenderedSamples = rendered;
            return result;
        }
        catch (Exception ex)
        {
            result.LastError = $"stem render error: {ex.GetType().Name}: {ex.Message}";
            return result;
        }
        finally
        {
            foreach (WavWriter writer in writers)
                writer?.Dispose();
            foreach (VgmAudioRenderer renderer in renderers)
                renderer?.Dispose();
        }
    }

    private static StemSpec[] BuildSpecs(VgmDocument document)
    {
        var specs = new List<StemSpec>();
        string[] fmColors = ["#ff665c", "#ffb44c", "#f2df5b", "#44cc44", "#44aaff", "#aa44ff"];
        string[] pulseColors = ["#62b8ff", "#3399ee", "#62b8ff"];
        if (document.Devices.Count(device => device.Id.Type == ChipType.Huc6280) == 1)
        {
            int instance = FirstInstance(document, ChipType.Huc6280);
            for (int channel = 0; channel < 6; channel++)
                specs.Add(new StemSpec(
                    $"huc6280-wave{channel + 1}",
                    $"HuC6280 Wave {channel + 1}",
                    ChipType.Huc6280,
                    channel,
                    ScopeSemanticClass.PulseStable,
                    10 + channel,
                    1,
                    1.0,
                    fmColors[channel],
                    $"huc6280.{instance}.wavetable.{channel + 1}"));
        }

        if (document.Devices.Count(device => device.Id.Type == ChipType.Ym2608) == 1)
        {
            int instance = FirstInstance(document, ChipType.Ym2608);
            for (int channel = 0; channel < 6; channel++)
                specs.Add(new StemSpec(
                    $"ym2608-fm{channel + 1}",
                    $"YM2608 FM {channel + 1}",
                    ChipType.Ym2608,
                    channel,
                    ScopeSemanticClass.FmEvolving,
                    10 + channel,
                    1,
                    1.0,
                    fmColors[channel],
                    $"ym2608.{instance}.fm.{channel + 1}"));

            for (int channel = 0; channel < 3; channel++)
                specs.Add(new StemSpec(
                    $"ym2608-ssg{channel + 1}",
                    $"YM2608 SSG {channel + 1}",
                    ChipType.Ym2608,
                    6 + channel,
                    ScopeSemanticClass.PulseStable,
                    20 + channel,
                    1,
                    0.7,
                    pulseColors[channel],
                    $"ym2608.{instance}.ssg.{channel + 1}"));

            specs.Add(new StemSpec(
                "ym2608-rhythm",
                "YM2608 Rhythm",
                ChipType.Ym2608,
                9,
                ScopeSemanticClass.Percussive,
                30,
                2,
                0.75,
                "#db72ff",
                $"ym2608.{instance}.rhythm"));
            specs.Add(new StemSpec(
                "ym2608-adpcm",
                "YM2608 ADPCM-B",
                ChipType.Ym2608,
                10,
                ScopeSemanticClass.Pcm,
                31,
                2,
                1.0,
                "#66cc66",
                $"ym2608.{instance}.adpcm-b"));
        }

        if (document.Devices.Count(device => device.Id.Type == ChipType.Ym2612) == 1)
        {
            int instance = FirstInstance(document, ChipType.Ym2612);
            for (int channel = 0; channel < 6; channel++)
                specs.Add(new StemSpec(
                    $"ym2612-fm{channel + 1}",
                    $"YM2612 FM {channel + 1}",
                    ChipType.Ym2612,
                    channel,
                    ScopeSemanticClass.FmEvolving,
                    20 + channel,
                    1,
                    1.0,
                    fmColors[channel],
                    $"ym2612.{instance}.fm.{channel + 1}"));

            // YM2612's shared channel-6 DAC carries the PCM/sample stream. It
            // is a separate stem (channel 6) that maps to the timeline's
            // `pcm.dac` panel so sampled audio shows in corrscope independently
            // of FM6 synthesis.
            specs.Add(new StemSpec(
                "ym2612-pcm",
                "YM2612 PCM (DAC)",
                ChipType.Ym2612,
                6,
                ScopeSemanticClass.Pcm,
                26,
                2,
                1.0,
                "#7fd0ff",
                $"ym2612.{instance}.pcm.dac"));
        }

        if (document.Devices.Count(device => device.Id.Type == ChipType.Ym2151) == 1)
        {
            // OPM: 8 FM voices.
            string[] opmColors = ["#ff665c", "#ffb44c", "#f2df5b", "#44cc44", "#44aaff", "#aa44ff", "#ff9fe0", "#7fd0ff"];
            int instance = FirstInstance(document, ChipType.Ym2151);
            for (int channel = 0; channel < 8; channel++)
                specs.Add(new StemSpec(
                    $"ym2151-fm{channel + 1}",
                    $"YM2151 FM {channel + 1}",
                    ChipType.Ym2151,
                    channel,
                    ScopeSemanticClass.FmEvolving,
                    20 + channel,
                    1,
                    1.0,
                    opmColors[channel],
                    $"ym2151.{instance}.fm.{channel + 1}"));
        }

        if (document.Devices.Count(device => device.Id.Type == ChipType.Okim6295) == 1)
        {
            // OKIM6295 is a single sample/ADPCM voice (four attenuator steps on
            // one output); emit one mono stem that carries all of its writes.
            int instance = FirstInstance(document, ChipType.Okim6295);
            specs.Add(new StemSpec(
                $"okim6295-sample",
                "OKIM6295 Sample",
                ChipType.Okim6295,
                0,
                ScopeSemanticClass.Pcm,
                40,
                2,
                1.0,
                "#66b866",
                $"okim6295.{instance}.pcm.1"));
        }

        if (document.Devices.Count(device => device.Id.Type == ChipType.Sn76489) == 1)
        {
            int instance = FirstInstance(document, ChipType.Sn76489);
            for (int channel = 0; channel < 3; channel++)
                specs.Add(new StemSpec(
                    $"sn76489-tone{channel + 1}",
                    $"SN76489 Tone {channel + 1}",
                    ChipType.Sn76489,
                    channel,
                    ScopeSemanticClass.PulseStable,
                    30 + channel,
                    1,
                    0.7,
                    pulseColors[channel],
                    $"sn76489.{instance}.psg.{channel + 1}"));
            specs.Add(new StemSpec(
                "sn76489-noise",
                "SN76489 Noise",
                ChipType.Sn76489,
                3,
                ScopeSemanticClass.Noise,
                33,
                1,
                0.7,
                "#3399ee",
                $"sn76489.{instance}.noise.1"));
        }

        AddFmChip(specs, document, ChipType.Ym2203, "ym2203", "YM2203", fmCount: 3, ssgCount: 3);
        AddFmChip(specs, document, ChipType.Ym2610, "ym2610", "YM2610", fmCount: 4, ssgCount: 3);
        AddOplChip(specs, document, ChipType.Ym2413, "ym2413", "YM2413", 9);
        AddOplChip(specs, document, ChipType.Ym3526, "ym3526", "YM3526", 9);
        AddOplChip(specs, document, ChipType.Ym3812, "ym3812", "YM3812", 9);
        AddOplChip(specs, document, ChipType.Y8950, "y8950", "Y8950", 9);
        AddOplChip(specs, document, ChipType.Ymf262, "ymf262", "YMF262", 18);
        AddPsgChip(specs, document, ChipType.Ay8910, "ay8910", "AY8910", 3, hasNoise: true);
        AddPsgChip(specs, document, ChipType.NesApu, "nes", "NES APU", 5, hasNoise: false);
        AddPsgChip(specs, document, ChipType.Dmg, "dmg", "GB DMG", 4, hasNoise: false);
        AddPsgChip(specs, document, ChipType.K051649, "k051649", "K051649", 6, hasNoise: false);

        return specs.ToArray();
    }

    private static void AddFmChip(List<StemSpec> specs, VgmDocument document, ChipType type, string slug, string label, int fmCount, int ssgCount)
    {
        string[] pulseColors = ["#62b8ff", "#3399ee", "#62b8ff"];
        if (document.Devices.Count(device => device.Id.Type == type) != 1)
            return;
        int instance = FirstInstance(document, type);
        string[] colors = ["#ff665c", "#ffb44c", "#f2df5b", "#44cc44"];
        for (int channel = 0; channel < fmCount; channel++)
            specs.Add(new StemSpec(
                $"{slug}-fm{channel + 1}",
                $"{label} FM {channel + 1}",
                type,
                channel,
                ScopeSemanticClass.FmEvolving,
                40 + channel,
                1,
                1.0,
                colors[channel % colors.Length],
                $"{slug}.{instance}.fm.{channel + 1}"));
        for (int channel = 0; channel < ssgCount; channel++)
            specs.Add(new StemSpec(
                $"{slug}-ssg{channel + 1}",
                $"{label} SSG {channel + 1}",
                type,
                fmCount + channel,
                ScopeSemanticClass.PulseStable,
                50 + channel,
                1,
                0.7,
                pulseColors[channel % pulseColors.Length],
                $"{slug}.{instance}.ssg.{channel + 1}"));
    }

    private static void AddOplChip(List<StemSpec> specs, VgmDocument document, ChipType type, string slug, string label, int channels)
    {
        if (document.Devices.Count(device => device.Id.Type == type) != 1)
            return;
        int instance = FirstInstance(document, type);
        string[] colors =
        [
            "#ff665c", "#ffb44c", "#f2df5b", "#44cc44", "#44aaff", "#aa44ff",
            "#ff9fe0", "#7fd0ff", "#ffd08a", "#8aff8a", "#ff8a8a", "#8ad0ff",
            "#ffb3e0", "#b3ffb3", "#ffe08a", "#aaccff", "#ff9fd0", "#88cc88",
        ];
        for (int channel = 0; channel < channels; channel++)
            specs.Add(new StemSpec(
                $"{slug}-fm{channel + 1}",
                $"{label} FM {channel + 1}",
                type,
                channel,
                ScopeSemanticClass.FmEvolving,
                60 + channel,
                1,
                1.0,
                colors[channel % colors.Length],
                $"{slug}.{instance}.fm.{channel + 1}"));
    }

    private static void AddPsgChip(List<StemSpec> specs, VgmDocument document, ChipType type, string slug, string label, int channels, bool hasNoise)
    {
        string[] pulseColors = ["#62b8ff", "#3399ee", "#62b8ff"];
        if (document.Devices.Count(device => device.Id.Type == type) != 1)
            return;
        int instance = FirstInstance(document, type);
        for (int channel = 0; channel < channels; channel++)
        {
            bool noise = hasNoise && channel == channels - 1;
            specs.Add(new StemSpec(
                noise ? $"{slug}-noise" : $"{slug}-ch{channel + 1}",
                noise ? $"{label} Noise" : $"{label} Ch {channel + 1}",
                type,
                channel,
                noise ? ScopeSemanticClass.Noise : ScopeSemanticClass.PulseStable,
                70 + channel,
                1,
                noise ? 0.7 : 1.0,
                noise ? "#3399ee" : pulseColors[channel % pulseColors.Length],
                $"{slug}.{instance}.ch.{channel + 1}"));
        }
    }

    private static int FirstInstance(VgmDocument document, ChipType type)
    {
        foreach (DeviceDescriptor device in document.Devices)
        {
            if (device.Id.Type == type)
                return device.Id.Instance;
        }
        return 0;
    }

    private readonly record struct StemSpec(
        string Name,
        string Label,
        ChipType FilterType,
        int Channel,
        ScopeSemanticClass SemanticClass,
        int StableOrder,
        int WindowWidth,
        double DefaultAmplification,
        string DefaultColor,
        string PresentationTrackId);

    private static IEnumerable<VgmRegisterWrite> ExpandWrites(
        VgmDocument document,
        int loopCount)
    {
        long? loop = document.LoopSample;
        long body = loop.HasValue ? document.EndSample - loop.Value : 0;
        int count = loop.HasValue ? loopCount : 1;
        for (int pass = 0; pass < count; pass++)
        {
            long offset = pass == 0 || !loop.HasValue ? 0 : body * pass;
            foreach (VgmRegisterWrite write in document.Writes)
            {
                if (pass > 0 && write.SourceSample < loop.Value)
                    continue;
                yield return offset == 0
                    ? write
                    : write with { SourceSample = write.SourceSample + offset };
            }
        }
    }

    private static long ExpandedEndSample(VgmDocument document, int loopCount)
    {
        if (!document.LoopSample.HasValue)
            return document.EndSample;
        long body = document.EndSample - document.LoopSample.Value;
        return document.EndSample + body * (loopCount - 1);
    }

    private static long ScaleSample(long sourceSample, int sampleRate) =>
        checked((long)Math.Round(sourceSample * sampleRate / 44_100.0, MidpointRounding.AwayFromZero));

    private static void RenderUntil(
        VgmAudioRenderer[] renderers,
        WavWriter[] writers,
        short[][] pcmBuffers,
        short[][] monoBuffers,
        ref long rendered,
        long target,
        long fadeStart,
        long baseEnd,
        double[] fadeGains)
    {
        if (target < rendered)
            throw new InvalidOperationException("VGM stems are not monotonic after loop expansion.");

        while (rendered < target)
        {
            int count = (int)Math.Min(1024, target - rendered);
            long blockStart = rendered;
            for (int sample = 0; sample < count; sample++)
            {
                long absolute = blockStart + sample;
                fadeGains[sample] = absolute >= baseEnd
                    ? 0
                    : absolute <= fadeStart || fadeStart >= baseEnd
                        ? 1
                        : (baseEnd - absolute) / (double)(baseEnd - fadeStart);
            }
            if (renderers.Length == 1)
            {
                RenderChannel(0, count, fadeGains, renderers, writers, pcmBuffers, monoBuffers);
            }
            else
            {
                Parallel.For(0, renderers.Length, channel =>
                    RenderChannel(channel, count, fadeGains, renderers, writers, pcmBuffers, monoBuffers));
            }
            rendered += count;
        }
    }

    private static void RenderChannel(
        int channel,
        int count,
        double[] fadeGains,
        VgmAudioRenderer[] renderers,
        WavWriter[] writers,
        short[][] pcmBuffers,
        short[][] monoBuffers)
    {
        Span<short> pcm = pcmBuffers[channel].AsSpan(0, count * 2);
        renderers[channel].Render(count, pcm);
        Span<short> mono = monoBuffers[channel].AsSpan(0, count);
        for (int sample = 0; sample < count; sample++)
        {
            int mixed = (pcm[sample * 2] + pcm[sample * 2 + 1]) / 2;
            mono[sample] = (short)Math.Clamp(mixed * fadeGains[sample], short.MinValue, short.MaxValue);
        }
        writers[channel].Write(mono);
    }
}
