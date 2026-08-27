namespace Fmp.Core.Visualization;

/// <summary>
/// A canonical, amplitude-normalized view of a hit waveform used for identity
/// resolution. Canonicalization removes what does not define a sample:
/// leading/trailing near-silence, the unsigned 0x80 center, DC offset, and
/// playback amplitude. The original unnormalized peak is preserved separately
/// so velocity can still differ between amplitude variants of one sample.
/// </summary>
internal static class DacCanonicalWaveform
{
    /// <summary>|v - 0x80| at or below this is treated as silence for trimming.</summary>
    public const int SilenceTolerance = 2;

    /// <summary>Never trim more than this fraction of the slice from one side.</summary>
    public const double MaxTrimFraction = 0.25;

    private const int QuantizationLevels = 256;

    /// <summary>
    /// Canonical result for one hit slice.
    /// </summary>
    internal sealed record Canonical(
        DacHash256 Hash,
        int ContentLength,
        float OriginalPeak,
        float[] Normalized);

    public static Canonical Build(ReadOnlySpan<byte> slice)
    {
        if (slice.Length == 0)
            return Empty();

        int first = 0;
        int last = slice.Length - 1;
        int maxTrim = Math.Max(0, (int)(slice.Length * MaxTrimFraction));

        while (first < slice.Length - 1
            && first < maxTrim
            && Math.Abs(slice[first] - 0x80) <= SilenceTolerance)
        {
            first++;
        }
        while (last > first
            && slice.Length - 1 - last < maxTrim
            && Math.Abs(slice[last] - 0x80) <= SilenceTolerance)
        {
            last--;
        }

        int length = last - first + 1;
        if (length <= 0)
            return Empty();

        float originalPeak = 0f;
        double sum = 0;
        for (int i = first; i <= last; i++)
        {
            float signed = slice[i] - 0x80f;
            originalPeak = Math.Max(originalPeak, Math.Abs(signed));
            sum += signed;
        }
        if (originalPeak <= 1e-3f)
            return Empty();

        double dc = sum / length;
        float[] normalized = new float[length];
        for (int i = 0; i < length; i++)
        {
            float centered = (slice[first + i] - 0x80f) - (float)dc;
            normalized[i] = centered / originalPeak;
        }

        // Quantize normalized samples back to bytes for a stable canonical
        // hash. Amplitude variants normalize to the same peak, so they produce
        // the same canonical bytes and collide exactly.
        byte[] canonicalBytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            int quantized = (int)Math.Round((normalized[i] + 1f) * 0.5f * (QuantizationLevels - 1));
            canonicalBytes[i] = (byte)Math.Clamp(quantized, 0, QuantizationLevels - 1);
        }

        return new Canonical(
            DacHash256.Of(canonicalBytes),
            length,
            originalPeak,
            normalized);
    }

    private static Canonical Empty()
        => new(DacHash256.Of(ReadOnlySpan<byte>.Empty), 0, 0f, Array.Empty<float>());
}