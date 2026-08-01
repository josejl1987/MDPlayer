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
        VgmDocument document = VgmDocument.Parse(VgmInput.Read(inputPath));
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
                    ref rendered,
                    target,
                    fadeStart,
                    baseEnd);
                var normalized = new TimedChipWrite(
                    target,
                    source.Device,
                    source.Port,
                    source.Address,
                    source.Data);
                foreach (VgmAudioRenderer renderer in renderers)
                    renderer.Write(normalized);
            }

            RenderUntil(renderers, writers, ref rendered, end, fadeStart, baseEnd);
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
        if (document.Devices.Count(device => device.Id.Type == ChipType.Huc6280) == 1)
        {
            for (int channel = 0; channel < 6; channel++)
                specs.Add(new StemSpec(
                    $"huc6280-wave{channel + 1}",
                    $"HuC6280 Wave {channel + 1}",
                    ChipType.Huc6280,
                    channel));
        }

        if (document.Devices.Count(device => device.Id.Type == ChipType.Ym2612) == 1)
        {
            for (int channel = 0; channel < 6; channel++)
                specs.Add(new StemSpec(
                    $"ym2612-fm{channel + 1}",
                    $"YM2612 FM {channel + 1}",
                    ChipType.Ym2612,
                    channel));
        }

        if (document.Devices.Count(device => device.Id.Type == ChipType.Sn76489) == 1)
        {
            for (int channel = 0; channel < 3; channel++)
                specs.Add(new StemSpec(
                    $"sn76489-tone{channel + 1}",
                    $"SN76489 Tone {channel + 1}",
                    ChipType.Sn76489,
                    channel));
            specs.Add(new StemSpec(
                "sn76489-noise",
                "SN76489 Noise",
                ChipType.Sn76489,
                3));
        }

        return specs.ToArray();
    }

    private readonly record struct StemSpec(
        string Name,
        string Label,
        ChipType FilterType,
        int Channel);

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
                yield return write with { SourceSample = write.SourceSample + offset };
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
        ref long rendered,
        long target,
        long fadeStart,
        long baseEnd)
    {
        if (target < rendered)
            throw new InvalidOperationException("VGM stems are not monotonic after loop expansion.");

        short[][] mono = renderers
            .Select(_ => new short[1024])
            .ToArray();
        while (rendered < target)
        {
            int count = (int)Math.Min(1024, target - rendered);
            for (int channel = 0; channel < renderers.Length; channel++)
            {
                short[] pcm = renderers[channel].Render(count);
                for (int sample = 0; sample < count; sample++)
                {
                    long absolute = rendered + sample;
                    double gain = absolute >= baseEnd
                        ? 0
                        : absolute <= fadeStart || fadeStart >= baseEnd
                            ? 1
                            : (baseEnd - absolute) / (double)(baseEnd - fadeStart);
                    int mixed = (pcm[sample * 2] + pcm[sample * 2 + 1]) / 2;
                    mono[channel][sample] = (short)Math.Clamp(mixed * gain, short.MinValue, short.MaxValue);
                }
                writers[channel].Write(mono[channel].AsSpan(0, count));
            }
            rendered += count;
        }
    }
}
