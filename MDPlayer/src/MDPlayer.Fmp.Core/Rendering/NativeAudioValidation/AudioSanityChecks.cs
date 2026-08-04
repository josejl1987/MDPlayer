namespace Fmp.Core.Rendering.NativeAudioValidation;

/// <summary>
/// Offline audio-sanity metrics computation (Workstream I). Broad defect
/// detection only: not-silent, not-accidentally-mono, int16-bounded, limited
/// clipping, small DC, first-last-event positioning. Returns metrics for the
/// caller to report/assert with fixture-appropriate thresholds; no universal
/// RMS threshold is imposed here.
/// </summary>
internal static class AudioSanityChecks
{
    public const int MinSample = short.MinValue;
    public const int MaxSample = short.MaxValue;

    /// <summary>
    /// Computes metrics over interleaved little-endian signed-16 PCM
    /// (<c>frames * 2</c> samples). Returns false when the input is empty.
    /// </summary>
    public static AudioSanityMetrics Compute(ReadOnlySpan<byte> interleavedPcm)
    {
        var m = new AudioSanityMetrics();
        int frames = interleavedPcm.Length / 4;
        m.FrameCount = frames;
        if (frames == 0)
        {
            m.PeakLeft = m.PeakRight = m.RmsLeft = m.RmsRight = m.DcMeanLeft = m.DcMeanRight = 0;
            m.ClippedSampleCount = 0;
            m.ZeroSamplePercentage = 100;
            m.LongestZeroRun = 0;
            m.FirstNonZeroFrame = m.LastNonZeroFrame = -1;
            return m;
        }

        long sumL = 0, sumR = 0;
        long sumSqL = 0, sumSqR = 0;
        long absPeakL = 0, absPeakR = 0;
        long clipped = 0;
        long firstNonZero = long.MaxValue;
        long lastNonZero = -1;
        long longestZeroRun = 0, currentZeroRun = 0;
        long zeroSamples = 0;

        var span = interleavedPcm;
        for (int i = 0; i < frames; i++)
        {
            int l = span[i * 4] | (span[i * 4 + 1] << 8);            // little-endian short
            int r = span[i * 4 + 2] | (span[i * 4 + 3] << 8);
            if (l >= 0x8000) l = l - 0x10000;                        // sign-extend
            if (r >= 0x8000) r = r - 0x10000;

            if (l == MinSample || l == MaxSample) clipped++;
            if (r == MinSample || r == MaxSample) clipped++;

            if (l == 0) zeroSamples++;
            if (r == 0) zeroSamples++;

            sumL += l; sumR += r;
            sumSqL += (long)l * l; sumSqR += (long)r * r;
            if (Math.Abs((long)l) > absPeakL) absPeakL = Math.Abs((long)l);
            if (Math.Abs((long)r) > absPeakR) absPeakR = Math.Abs((long)r);

            bool frameZero = l == 0 && r == 0;
            if (!frameZero)
            {
                if (firstNonZero == long.MaxValue) firstNonZero = i;
                lastNonZero = i;
                currentZeroRun = 0;
            }
            else
            {
                currentZeroRun++;
                if (currentZeroRun > longestZeroRun) longestZeroRun = currentZeroRun;
            }
        }

        long totalSamples = (long)frames * 2;

        m.PeakLeft = absPeakL / 32768.0;
        m.PeakRight = absPeakR / 32768.0;
        m.RmsLeft = Math.Sqrt((double)sumSqL / frames) / 32768.0;
        m.RmsRight = Math.Sqrt((double)sumSqR / frames) / 32768.0;
        m.DcMeanLeft = (double)sumL / frames / 32768.0;
        m.DcMeanRight = (double)sumR / frames / 32768.0;
        m.ClippedSampleCount = clipped;
        m.ZeroSamplePercentage = 100.0 * zeroSamples / totalSamples;
        m.LongestZeroRun = longestZeroRun;
        m.FirstNonZeroFrame = firstNonZero == long.MaxValue ? -1 : firstNonZero;
        m.LastNonZeroFrame = lastNonZero;
        return m;
    }
}
