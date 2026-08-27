using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Identity-resolution tests for the two-stage (exact canonical hash, then
/// fuzzy normalized cross-correlation) DAC sample identity layer.
/// </summary>
public sealed class DacIdentityResolverTests
{
    private static DacHitEvent Hit(
        string contentHash,
        DacHitClass classification,
        long? sourceOffset = null,
        long startSample = 0)
        => new(
            "ym2612.0.pcm.dac",
            startSample,
            startSample + 1000,
            "dac:0",
            contentHash,
            0,
            1000,
            classification,
            DacHitIdentityKind.Inferred,
            0.9f,
            0.5f,
            SourceOffset: sourceOffset);

    private static IReadOnlyList<string> Resolve(params (DacHitEvent Hit, byte[] Slice)[] candidates)
        => DacIdentityResolver.Resolve(
            candidates
                .Select(pair => new DacIdentityResolver.Candidate(pair.Hit, pair.Slice))
                .ToArray());

    private static byte[] KickWaveform(float amplitude = 1f, int length = 128)
    {
        // A decaying kick-like waveform centered on unsigned 0x80.
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            double decay = Math.Exp(-3.0 * i / length);
            double sample = 0x80 + amplitude * 110 * decay * Math.Sin(i * 0.25);
            bytes[i] = (byte)Math.Clamp((int)Math.Round(sample), 0, 255);
        }
        return bytes;
    }

    private static byte[] ScaleAmplitude(byte[] source, float scale)
    {
        var result = new byte[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            double centered = source[i] - 0x80;
            result[i] = (byte)Math.Clamp((int)Math.Round(0x80 + centered * scale), 0, 255);
        }
        return result;
    }

    private static byte[] ShiftBy(byte[] source, int shift)
    {
        // Pads/trims leading near-silence so the audible body lands a few
        // samples later, simulating boundary jitter between hits.
        var result = new byte[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            int sourceIndex = i - shift;
            result[i] = sourceIndex >= 0 && sourceIndex < source.Length
                ? source[sourceIndex]
                : (byte)0x80;
        }
        return result;
    }

    [Fact]
    public void AmplitudeVariantsOfSameSample_MergeToOneIdentity()
    {
        byte[] loud = KickWaveform(amplitude: 1f);
        byte[] soft = ScaleAmplitude(loud, 0.4f);

        IReadOnlyList<string> ids = Resolve(
            (Hit("loud-slice", DacHitClass.Kick), loud),
            (Hit("soft-slice", DacHitClass.Kick), soft));

        Assert.Equal(1, ids.Distinct().Count());
        Assert.Equal("dacid:0", ids[0]);
        Assert.Equal("dacid:0", ids[1]);
    }

    [Fact]
    public void BoundaryJitter_MergesViaFuzzyMatch()
    {
        // The jittered copy has a different exact canonical hash (leading
        // silence differs), but its audible body matches at a few samples'
        // displacement, so fuzzy resolution merges it.
        byte[] a = KickWaveform(amplitude: 1f);
        byte[] jittered = ShiftBy(a, 3);

        IReadOnlyList<string> ids = Resolve(
            (Hit("a", DacHitClass.Kick), a),
            (Hit("jittered", DacHitClass.Kick), jittered));

        Assert.Equal(1, ids.Distinct().Count());
    }

    [Fact]
    public void DistinctWaveforms_StaySeparate()
    {
        byte[] kick = KickWaveform(amplitude: 1f);
        // A snare-like noise burst is waveform-distinct from the kick.
        var snare = new byte[kick.Length];
        var random = new Random(42);
        for (int i = 0; i < snare.Length; i++)
        {
            double envelope = Math.Exp(-2.0 * i / snare.Length);
            snare[i] = (byte)Math.Clamp(
                (int)Math.Round(0x80 + (random.NextDouble() * 2 - 1) * 120 * envelope), 0, 255);
        }

        IReadOnlyList<string> ids = Resolve(
            (Hit("kick", DacHitClass.Kick), kick),
            (Hit("snare", DacHitClass.Snare), snare));

        Assert.Equal(2, ids.Distinct().Count());
        Assert.NotEqual(ids[0], ids[1]);
    }

    [Fact]
    public void SameSampleAcrossDifferentSourceOffsets_StillMerges()
    {
        // Strong waveform match wins even when the source region differs:
        // addressing is evidence, not absolute identity.
        byte[] a = KickWaveform(amplitude: 1f);
        byte[] b = ScaleAmplitude(a, 0.7f);

        IReadOnlyList<string> ids = Resolve(
            (Hit("a", DacHitClass.Kick, sourceOffset: 0x0000), a),
            (Hit("b", DacHitClass.Kick, sourceOffset: 0x337C), b));

        Assert.Equal(1, ids.Distinct().Count());
    }

    [Fact]
    public void ResolutionIsDeterministicInFirstUseOrder()
    {
        byte[] kick = KickWaveform(amplitude: 1f);
        byte[] snare = new byte[kick.Length];
        var random = new Random(7);
        for (int i = 0; i < snare.Length; i++)
        {
            double envelope = Math.Exp(-2.0 * i / snare.Length);
            snare[i] = (byte)Math.Clamp(
                (int)Math.Round(0x80 + (random.NextDouble() * 2 - 1) * 120 * envelope), 0, 255);
        }

        var candidates = new[]
        {
            (Hit("kick", DacHitClass.Kick), kick),
            (Hit("snare", DacHitClass.Snare), snare),
            (Hit("kick-soft", DacHitClass.Kick), ScaleAmplitude(kick, 0.5f)),
        };

        IReadOnlyList<string> first = Resolve(candidates);
        IReadOnlyList<string> second = Resolve(candidates);

        Assert.Equal(first, second);
        Assert.Equal("dacid:0", first[0]); // kick is first use
        Assert.Equal("dacid:1", first[1]); // snare is second use
        Assert.Equal("dacid:0", first[2]); // kick-soft reuses the kick identity
    }
}