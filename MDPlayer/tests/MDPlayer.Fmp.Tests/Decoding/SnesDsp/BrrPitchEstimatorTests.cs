using Fmp.Core.Decoding.SnesDsp;
using Xunit;

namespace Fmp.Core.Tests.Decoding.SnesDsp;

/// <summary>PR 9 BRR root estimation: decoder filter math, YIN/autocorrelation pipeline,
/// confidence thresholds and determinism (§15; fixtures per §30.6).</summary>
public class BrrPitchEstimatorTests
{
    private const int SampleRate = BrrDecoder.SampleRateHz;

    [Fact]
    public void BrrSineLoop_EstimateWithinCents_HighConfidence()
    {
        const double freq = 400.0; // period = 80 samples exactly
        byte[] blocks = TestBrrEncoder.EncodeSineBlocks(TestBrrEncoder.GenerateSine(freq, SampleRate, 20000, 288));
        var sample = new BrrSample(0x1000, (ushort)(0x1000 + 3 * BrrSampleReader.BlockSize), blocks, true, true, "h");
        PitchEstimate est = BrrPitchEstimator.Estimate(sample);
        Assert.True(est.Confidence >= 0.85, $"conf {est.Confidence:F3}");
        Assert.Equal(PitchEstimateReason.Periodic, est.Reason);
        Assert.InRange(Math.Abs(1200.0 * Math.Log2(est.FrequencyHz / freq)), 0, 50);
        Assert.Equal("estimated", BrrPitchEstimator.AccuracyString(est));
    }
    [Fact]
    public void NoiseSample_AperiodicOrLowConfidence()
    {
        PitchEstimate est = BrrPitchEstimator.EstimateFromPcm(TestBrrEncoder.GenerateNoise(4096, 12345), SampleRate);
        Assert.True(est.Reason is PitchEstimateReason.Aperiodic or PitchEstimateReason.LowConfidence);
        Assert.True(est.Confidence < 0.65);
    }
    [Fact]
    public void ShortOneShot_TooShort_Relative()
    {
        PitchEstimate est = BrrPitchEstimator.EstimateFromPcm(TestBrrEncoder.GenerateSine(440, SampleRate, 20000, 64), SampleRate);
        Assert.Equal(PitchEstimateReason.TooShort, est.Reason);
        Assert.Equal(0.0, est.FrequencyHz);
        Assert.Equal("relative", BrrPitchEstimator.AccuracyString(est));
        Assert.Equal(PitchEstimateReason.TooShort, BrrPitchEstimator.EstimateFromPcm(Array.Empty<short>(), SampleRate).Reason);
    }
    [Fact]
    public void RepeatedEstimates_Identical()
    {
        short[] sine = TestBrrEncoder.GenerateSine(330, SampleRate, 20000, 2048);
        Assert.Equal(BrrPitchEstimator.EstimateFromPcm(sine, SampleRate), BrrPitchEstimator.EstimateFromPcm(sine, SampleRate));
    }
    [Fact]
    public void PcmSine_EstimateWithinCents_HighConfidence()
    {
        const double freq = 220.0;
        PitchEstimate est = BrrPitchEstimator.EstimateFromPcm(TestBrrEncoder.GenerateSine(freq, SampleRate, 20000, 2048), SampleRate);
        Assert.True(est.Confidence >= 0.85, $"conf {est.Confidence:F3}");
        Assert.InRange(Math.Abs(1200.0 * Math.Log2(est.FrequencyHz / freq)), 0, 50);
    }
    [Fact]
    public void Decode_Filter0_RangeShift_ExactValues()
    {
        var blocks = new byte[BrrSampleReader.BlockSize];
        blocks[0] = 0x01; // end flag, range 0, filter 0
        for (int i = 1; i < blocks.Length; i++)
            blocks[i] = 0x77;
        short[] pcm = BrrDecoder.Decode(new BrrSample(0x1000, 0x1000, blocks, false, true, "h"));
        Assert.All(pcm, s => Assert.Equal(28672, s));
    }
    [Fact]
    public void Decode_Filter1_PredictsExactly()
    {
        var blocks = new byte[BrrSampleReader.BlockSize];
        blocks[0] = 0x04; // range 0, filter 1
        blocks[1] = 0x10; // first nibble 1, the rest 0
        short[] pcm = BrrDecoder.Decode(new BrrSample(0x1000, 0x1000, blocks, false, true, "h"));
        Assert.Equal(4096, pcm[0]);
        Assert.Equal(2304, pcm[1]);
        Assert.Equal(-752, pcm[2]);
        Assert.Equal(-1575, pcm[3]);
    }
    [Fact]
    public void Builder_EstimateRoot_CachesAndNeverLabelsExact()
    {
        var builder = new SpcInstrumentBuilder(enablePitchEstimation: true);
        byte[] blocks = TestBrrEncoder.EncodeSineBlocks(TestBrrEncoder.GenerateSine(400, SampleRate, 20000, 288));
        var sample = new BrrSample(0x1000, (ushort)(0x1000 + 3 * BrrSampleReader.BlockSize), blocks, true, true, "s");
        (double? hz, double conf, string acc) = builder.EstimateRoot(sample);
        Assert.Equal(builder.EstimateRoot(sample).Hz, hz); // cached -> identical
        Assert.True(hz.HasValue && conf >= 0.85);
        Assert.Equal("estimated", acc);
        Assert.NotEqual("exact", acc);
        (double? nz, _, string nacc) = builder.EstimateRoot(NoiseSample());
        Assert.Null(nz);
        Assert.Equal("unpitched", nacc);
    }

    private static BrrSample NoiseSample() =>
        new(0x1000, 0x1000, TestBrrEncoder.EncodeSineBlocks(TestBrrEncoder.GenerateNoise(4096, 99)), true, true, "n");
}

/// <summary>Tiny deterministic BRR encoder for tests: filter 0, fixed range, end+loop flags.</summary>
internal static class TestBrrEncoder
{
    public static short[] GenerateSine(double frequency, int sampleRate, double amplitude, int length)
    {
        var pcm = new short[length];
        for (int i = 0; i < length; i++)
            pcm[i] = (short)Math.Round(amplitude * Math.Sin(2.0 * Math.PI * frequency * i / sampleRate));
        return pcm;
    }

    public static short[] GenerateNoise(int length, int seed)
    {
        var r = new Random(seed);
        var pcm = new short[length];
        for (int i = 0; i < length; i++) pcm[i] = (short)(r.Next(0, 65535) - 32767);
        return pcm;
    }

    public static byte[] EncodeSineBlocks(short[] pcm)
    {
        int blocks = (pcm.Length + BrrDecoder.SamplesPerBlock - 1) / BrrDecoder.SamplesPerBlock;
        int maxAbs = 1;
        foreach (short s in pcm)
            maxAbs = Math.Max(maxAbs, Math.Abs(s));
        int range = 0;
        for (int r = 0; r <= 12; r++)
            if ((7 << (12 - r)) >= maxAbs) range = r;
        int step = 1 << (12 - range);
        var output = new byte[blocks * BrrSampleReader.BlockSize];
        for (int b = 0; b < blocks; b++)
        {
            output[b * BrrSampleReader.BlockSize] = (byte)((range << 4) | (b == blocks - 1 ? 3 : 0));
            for (int i = 0; i < BrrDecoder.SamplesPerBlock; i++)
            {
                int idx = b * BrrDecoder.SamplesPerBlock + i;
                int s = idx < pcm.Length ? pcm[idx] : 0;
                int nib = Math.Clamp((int)Math.Round(s / (double)step), -8, 7) & 0x0F;
                int slot = b * BrrSampleReader.BlockSize + 1 + i / 2;
                if ((i & 1) == 0) output[slot] |= (byte)(nib << 4);
                else output[slot] |= (byte)nib;
            }
        }
        return output;
    }
}
