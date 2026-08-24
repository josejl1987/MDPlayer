namespace Fmp.Core.Visualization;

internal static class DacWaveformPreviewBuilder
{
    public const int MaximumBuckets = 256;

    public static WaveformEnvelopePoint[] Build(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
            return [];

        int bucketCount = Math.Min(MaximumBuckets, payload.Length);
        var preview = new WaveformEnvelopePoint[bucketCount];
        for (int bucket = 0; bucket < bucketCount; bucket++)
        {
            int start = bucket * payload.Length / bucketCount;
            int end = Math.Max(start + 1, (bucket + 1) * payload.Length / bucketCount);
            float minimum = 1f;
            float maximum = -1f;
            for (int index = start; index < end && index < payload.Length; index++)
            {
                float amplitude = (payload[index] - 128) / 128f;
                amplitude = Math.Clamp(amplitude, -1f, 1f);
                minimum = Math.Min(minimum, amplitude);
                maximum = Math.Max(maximum, amplitude);
            }
            preview[bucket] = new WaveformEnvelopePoint(minimum, maximum);
        }
        return preview;
    }
}
