using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class DacHitDetectorTests
{
    [Fact]
    public void DetectsBoundedInferredHitsAndSeparatesSyntheticDrumShapes()
    {
        byte[] payload = new byte[16_384];
        Array.Fill(payload, (byte)128);
        AddSine(payload, 500, 3_000, 0.03, 0.72);
        AddNoise(payload, 5_000, 7_500, 0.72, 17);
        AddSine(payload, 9_500, 12_000, 0.30, 0.72);

        IReadOnlyList<DacHitEvent> hits = DacHitDetector.Detect(
            payload,
            timelineStart: 0,
            timelineEnd: 48_000,
            "dac:0",
            rateHz: null,
            timelineSampleRate: 48_000);

        Assert.NotEmpty(hits);
        Assert.All(hits, hit =>
        {
            Assert.Equal("ym2612.0.pcm.dac", hit.VoiceId);
            Assert.Equal(DacHitIdentityKind.Inferred, hit.IdentityKind);
            Assert.InRange(hit.StartSample, 0, 47_999);
            Assert.InRange(hit.EndSample, 1, 48_000);
            Assert.InRange(hit.SourceStartOffset, 0, payload.Length - 1);
            Assert.InRange(hit.SourceEndOffset, hit.SourceStartOffset + 1, payload.Length);
            Assert.InRange(hit.Confidence, 0, 1);
            Assert.InRange(hit.PeakLevel, 0, 1);
        });
        Assert.Contains(hits, hit => hit.Classification == DacHitClass.Kick);
        Assert.Contains(hits, hit => hit.Classification == DacHitClass.Snare);
        Assert.Contains(hits, hit => hit.Classification == DacHitClass.Tom);
    }

    private static void AddSine(byte[] payload, int start, int end, double phaseStep, double amplitude)
    {
        for (int index = start; index < end; index++)
        {
            double envelope = Math.Min(1, (index - start) / 128.0)
                * Math.Min(1, (end - index) / 512.0);
            payload[index] = ToByte(Math.Sin((index - start) * phaseStep) * amplitude * envelope);
        }
    }

    private static void AddNoise(byte[] payload, int start, int end, double amplitude, int seed)
    {
        var random = new Random(seed);
        for (int index = start; index < end; index++)
        {
            double envelope = Math.Min(1, (index - start) / 64.0)
                * Math.Min(1, (end - index) / 256.0);
            payload[index] = ToByte((random.NextDouble() * 2 - 1) * amplitude * envelope);
        }
    }

    private static byte ToByte(double value)
        => (byte)Math.Clamp((int)Math.Round(128 + value * 127), 0, 255);
}
