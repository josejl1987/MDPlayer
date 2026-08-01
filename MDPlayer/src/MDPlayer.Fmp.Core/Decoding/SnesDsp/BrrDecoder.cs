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
            int range = Math.Min(header >> 4, 12); // 13-15 are reserved; clamp like the reference
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

    // One 4-bit sub-sample: sign-extend the nibble, apply the range shift, then the filter.
    private static short DecodeSample(int nibble, int range, int filter, ref int old1, ref int old2)
    {
        int s = (nibble < 8 ? nibble : nibble - 16) << 12 >> range;
        switch (filter)
        {
            case 1:
                s += (old1 >> 1) + (old1 >> 4) - (old2 >> 1);
                break;
            case 2:
                s += old1 + (old1 >> 4) - (old2 >> 1) - (old2 >> 1);
                break;
            case 3:
                s += (old1 >> 1) + (old1 >> 4) + (old2 >> 1) - (old2 >> 4);
                break;
        }
        old2 = old1;
        old1 = Clamp16(s);
        return (short)old1;
    }

    private static int Clamp16(int value) =>
        value > short.MaxValue ? short.MaxValue : value < short.MinValue ? short.MinValue : value;
}
