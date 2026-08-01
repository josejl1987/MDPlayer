using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Fmp.Core.Visualization;

/// <summary>
/// Deterministic, bounded asset preparation shared by decoders. It knows only
/// normalized generic samples; chip encodings are decoded before this layer.
/// </summary>
internal static class VisualizationAssetBuilder
{
    public const int DefaultWaveformPreviewLength = 64;
    public const int MaximumWaveformPreviewLength = 128;
    public const int DefaultSamplePreviewBins = 128;
    public const int MaximumSamplePreviewBins = 256;

    public static WaveformDefinition CreateWaveform(
        string family,
        IReadOnlyList<float> normalizedSamples,
        string displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        ArgumentNullException.ThrowIfNull(normalizedSamples);
        if (normalizedSamples.Count < 2)
            throw new ArgumentException("A waveform requires at least two samples.", nameof(normalizedSamples));

        float[] normalized = Normalize(normalizedSamples);
        float[] preview = CreateWaveformPreview(normalized);
        string id = "wave:" + HashFloats(normalized);
        return new WaveformDefinition(id, family, normalized.Length, preview, displayName);
    }

    public static WaveformDefinition CreateIntegerWaveform(
        string family,
        IReadOnlyList<int> samples,
        int nativeMinimum,
        int nativeMaximum,
        string displayName = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (nativeMaximum <= nativeMinimum)
            throw new ArgumentOutOfRangeException(nameof(nativeMaximum));
        var normalized = new float[samples.Count];
        double centre = (nativeMinimum + nativeMaximum) / 2.0;
        double halfRange = (nativeMaximum - nativeMinimum) / 2.0;
        for (int index = 0; index < samples.Count; index++)
            normalized[index] = (float)Math.Clamp((samples[index] - centre) / halfRange, -1, 1);
        return CreateWaveform(family, normalized, displayName);
    }

    public static SampleDefinition CreateSample(
        string family,
        IReadOnlyList<float> normalizedSamples,
        int? nativeSampleRate = null,
        int? loopStart = null,
        int? loopEnd = null,
        SampleLoopMode loopMode = SampleLoopMode.None,
        string displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        ArgumentNullException.ThrowIfNull(normalizedSamples);
        if (normalizedSamples.Count == 0)
            throw new ArgumentException("A sample preview requires source samples.", nameof(normalizedSamples));

        float[] normalized = Normalize(normalizedSamples);
        WaveformEnvelopePoint[] preview = CreateEnvelope(normalized, DefaultSamplePreviewBins);
        return new SampleDefinition(
            "sample:" + HashFloats(normalized),
            family,
            normalized.Length,
            nativeSampleRate,
            loopStart,
            loopEnd,
            loopMode,
            preview,
            displayName);
    }

    public static SampleDefinition CreateSyntheticSample(
        string id,
        string family,
        int sourceLengthSamples,
        int? loopStart = null,
        int? loopEnd = null,
        SampleLoopMode loopMode = SampleLoopMode.Unknown,
        string displayName = null,
        AssetIdentityKind identityKind = AssetIdentityKind.BankAndSlot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (sourceLengthSamples < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceLengthSamples));
        return new SampleDefinition(
            id,
            family,
            sourceLengthSamples,
            null,
            loopStart,
            loopEnd,
            loopMode,
            [],
            displayName)
        {
            IdentityKind = identityKind,
        };
    }

    public static float[] CreateWaveformPreview(IReadOnlyList<float> normalizedSamples)
    {
        int count = normalizedSamples.Count <= DefaultWaveformPreviewLength
            ? normalizedSamples.Count
            : Math.Min(MaximumWaveformPreviewLength, DefaultWaveformPreviewLength);
        return Resample(normalizedSamples, count);
    }

    public static WaveformEnvelopePoint[] CreateEnvelope(
        IReadOnlyList<float> normalizedSamples,
        int requestedBins = DefaultSamplePreviewBins)
    {
        int bins = Math.Clamp(requestedBins, 1, MaximumSamplePreviewBins);
        bins = Math.Min(bins, Math.Max(1, normalizedSamples.Count));
        var result = new WaveformEnvelopePoint[bins];
        for (int bin = 0; bin < bins; bin++)
        {
            int start = bin * normalizedSamples.Count / bins;
            int end = Math.Max(start + 1, (bin + 1) * normalizedSamples.Count / bins);
            float minimum = 1;
            float maximum = -1;
            for (int index = start; index < end && index < normalizedSamples.Count; index++)
            {
                float value = Math.Clamp(normalizedSamples[index], -1, 1);
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
            }
            result[bin] = new WaveformEnvelopePoint(minimum, maximum);
        }
        return result;
    }

    private static float[] Normalize(IReadOnlyList<float> samples)
    {
        var normalized = new float[samples.Count];
        for (int index = 0; index < samples.Count; index++)
        {
            float value = samples[index];
            if (!float.IsFinite(value))
                throw new ArgumentException("Asset samples must be finite.", nameof(samples));
            normalized[index] = Math.Clamp(value, -1, 1);
        }
        return normalized;
    }

    private static float[] Resample(IReadOnlyList<float> source, int count)
    {
        var result = new float[count];
        if (source.Count == count)
        {
            for (int index = 0; index < count; index++)
                result[index] = source[index];
            return result;
        }

        for (int index = 0; index < count; index++)
        {
            double position = index * (source.Count - 1.0) / Math.Max(1, count - 1);
            int left = (int)Math.Floor(position);
            int right = Math.Min(source.Count - 1, left + 1);
            double fraction = position - left;
            result[index] = (float)(source[left] + (source[right] - source[left]) * fraction);
        }
        return result;
    }

    private static string HashFloats(IReadOnlyList<float> samples)
    {
        byte[] bytes = new byte[checked(samples.Count * sizeof(float))];
        for (int index = 0; index < samples.Count; index++)
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(index * sizeof(float)),
                BitConverter.SingleToInt32Bits(samples[index]));
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
