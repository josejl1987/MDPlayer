namespace Fmp.Core.Decoding.SnesDsp;

/// <summary>BRR (4-bit ADPCM) block decoder implementing the four S-DSP filters (§15.1).
/// One 16-bit PCM sample per BRR sub-sample at the S-DSP internal clock (32,000 Hz at
/// pitch 0x1000). Deterministic, never throws; filter formulas follow Snes_Spc Spc_Dsp.</summary>
internal static class BrrDecoder
{
    /// <summary>S-DSP nominal sample clock: BRR plays at this rate when pitch == 0x1000.</summary>
    public const int SampleRateHz = 32000;
    public const int SamplesPerBlock = 16;

    /// <summary>Decode every encoded block into 16-bit PCM at the S-DSP internal clock.</summary>
    public static short[] Decode(BrrSample sample)
    {
        if (sample == null || sample.EncodedBlocks == null || sample.EncodedBlocks.Length == 0)
            return Array.Empty<short>();
        int blockCount = sample.EncodedBlocks.Length / BrrSampleReader.BlockSize;
        var output = new short[blockCount * SamplesPerBlock];
        int old1 = 0, old2 = 0, outIndex = 0;
        for (int b = 0; b < blockCount; b++)
        {
            int offset = b * BrrSampleReader.BlockSize;
            byte header = sample.EncodedBlocks[offset];
            int range = header >> 4;
            int filter = (header >> 2) & 0x03;
            for (int i = 1; i < BrrSampleReader.BlockSize; i++)
            {
                byte packed = sample.EncodedBlocks[offset + i];
                output[outIndex++] = DecodeSample(packed >> 4, range, filter, ref old1, ref old2);
                output[outIndex++] = DecodeSample(packed & 0x0F, range, filter, ref old1, ref old2);
            }
        }
        return output;
    }

    /// <summary>Decode the stable loop region (samples from the loop block onward, §15.2
    /// step 4). The full chain is decoded first so filter history is continuous, then sliced
    /// at the loop block; falls back to the full decode when the loop address is missing.</summary>
    public static short[] DecodeLoopRegion(BrrSample sample)
    {
        short[] full = Decode(sample);
        int loopBlock = LoopBlockIndex(sample);
        if (loopBlock <= 0 || loopBlock * SamplesPerBlock >= full.Length)
            return full;
        int start = loopBlock * SamplesPerBlock;
        var region = new short[full.Length - start];
        Array.Copy(full, start, region, 0, region.Length);
        return region;
    }

    /// <summary>Block index of the loop address in the chain, or -1 when missing/unaligned/out of range.</summary>
    internal static int LoopBlockIndex(BrrSample sample)
    {
        if (sample == null || !sample.Loops)
            return -1;
        long delta = (long)sample.LoopAddress - sample.StartAddress;
        if (delta < 0 || delta % BrrSampleReader.BlockSize != 0)
            return -1;
        long block = delta / BrrSampleReader.BlockSize;
        return block >= sample.EncodedBlocks.Length / BrrSampleReader.BlockSize ? -1 : (int)block;
    }

    private static short DecodeSample(int nibble, int range, int filter, ref int old1, ref int old2)
    {
        // Sign-extend the nibble and apply the range shift exactly like the
        // upstream S-DSP reference (range values 13..15 are intentionally not clamped).
        int rightShift = range == 0 ? 13 : range <= 12 ? 12 : 16;
        int leftShift = range == 0 ? 0 : range <= 12 ? range - 1 : 11;
        int s = ((nibble < 8 ? nibble : nibble - 16) << 12) >> rightShift;
        s <<= leftShift;

        int p1 = old1;
        int p2 = old2 >> 1;
        switch (filter)
        {
            case 1:
                s += (p1 >> 1) + ((-p1) >> 5);
                break;
            case 2:
                s += p1 - p2 + (p2 >> 4) + ((p1 * -3) >> 6);
                break;
            case 3:
                s += p1 - p2 + ((p1 * -13) >> 7) + ((p2 * 3) >> 4);
                break;
        }

        old2 = old1;
        int clamped = Clamp16(s);
        // Spc_Dsp keeps decoded history at *2 scale; expose the unscaled 16-bit sample.
        old1 = clamped * 2;
        return (short)clamped;
    }

    private static int Clamp16(int value) =>
        value > short.MaxValue ? short.MaxValue : value < short.MinValue ? short.MinValue : value;
}
