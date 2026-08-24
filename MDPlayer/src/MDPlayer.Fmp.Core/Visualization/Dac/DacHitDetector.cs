namespace Fmp.Core.Visualization;

/// <summary>
/// Deterministic, bounded hit detector for raw 8-bit YM2612 DAC payloads.
/// Labels are explicitly inferred: the source stream does not carry kick,
/// snare, or tom metadata.
/// </summary>
internal static class DacHitDetector
{
    private const double MinimumRms = 0.035;

    public static IReadOnlyList<DacHitEvent> Detect(
        ReadOnlySpan<byte> payload,
        long timelineStart,
        long timelineEnd,
        string sampleId,
        double? rateHz,
        int timelineSampleRate)
    {
        if (payload.Length < 64
            || timelineEnd <= timelineStart
            || string.IsNullOrWhiteSpace(sampleId)
            || timelineSampleRate <= 0)
        {
            return Array.Empty<DacHitEvent>();
        }

        int frameSize = Math.Clamp(payload.Length / 512, 32, 256);
        int hop = Math.Max(8, frameSize / 2);
        var frames = BuildFrames(payload, frameSize, hop);
        if (frames.Count < 3)
            return Array.Empty<DacHitEvent>();

        double median = Percentile(frames.Select(value => value.Rms), 0.50);
        double p90 = Percentile(frames.Select(value => value.Rms), 0.90);
        double onsetThreshold = Math.Max(0.018, (p90 - median) * 0.30);
        double floor = Math.Max(MinimumRms, median * 1.15);
        double estimatedRate = rateHz is > 0 and double explicitRate
            ? explicitRate
            : payload.Length / Math.Max(1e-6,
                (timelineEnd - timelineStart) / (double)timelineSampleRate);
        int minimumGapFrames = Math.Max(2,
            (int)Math.Ceiling(estimatedRate * 0.055 / hop));

        var peaks = new List<int>();
        for (int index = 1; index < frames.Count - 1; index++)
        {
            Frame current = frames[index];
            double previousRms = frames[index - 1].Rms;
            bool crossedFloor = previousRms < floor && current.Rms >= floor;
            bool roseSharply = current.Rms - previousRms >= onsetThreshold;
            if (current.Rms < floor || (!crossedFloor && !roseSharply))
            {
                continue;
            }

            if (peaks.Count > 0 && index - peaks[^1] < minimumGapFrames)
            {
                if (current.Rms > frames[peaks[^1]].Rms)
                    peaks[^1] = index;
                continue;
            }
            peaks.Add(index);
        }

        var result = new List<DacHitEvent>(peaks.Count);
        for (int peakIndex = 0; peakIndex < peaks.Count; peakIndex++)
        {
            int peak = peaks[peakIndex];
            double releaseFloor = Math.Max(floor * 0.65, frames[peak].Rms * 0.18);
            int first = peak;
            while (first > 0 && frames[first - 1].Rms >= releaseFloor)
                first--;
            int last = peak;
            int maximumFrames = Math.Max(4, (int)Math.Ceiling(minimumGapFrames * 3.5));
            while (last + 1 < frames.Count
                && last - peak < maximumFrames
                && frames[last + 1].Rms >= releaseFloor)
            {
                last++;
            }

            int sourceStart = frames[first].Start;
            int sourceEnd = Math.Max(sourceStart + 1, frames[last].End);
            sourceEnd = Math.Min(payload.Length, sourceEnd);
            Feature feature = Measure(payload[sourceStart..sourceEnd]);
            (DacHitClass classification, float confidence) = Classify(feature);
            long startSample = timelineStart + (long)Math.Round(
                (timelineEnd - timelineStart) * (sourceStart / (double)payload.Length));
            long endSample = timelineStart + (long)Math.Round(
                (timelineEnd - timelineStart) * (sourceEnd / (double)payload.Length));
            endSample = Math.Min(timelineEnd, Math.Max(startSample + 1, endSample));

            result.Add(new DacHitEvent(
                "ym2612.0.pcm.dac",
                startSample,
                endSample,
                sampleId,
                sourceStart,
                sourceEnd,
                classification,
                DacHitIdentityKind.Inferred,
                confidence,
                (float)Math.Clamp(feature.Peak, 0, 1)));
        }

        return MergeOverlappingHits(result);
    }

    private static List<Frame> BuildFrames(ReadOnlySpan<byte> payload, int frameSize, int hop)
    {
        var frames = new List<Frame>((payload.Length + hop - 1) / hop);
        for (int start = 0; start < payload.Length; start += hop)
        {
            int end = Math.Min(payload.Length, start + frameSize);
            if (end - start < 8)
                break;
            Feature feature = Measure(payload[start..end]);
            frames.Add(new Frame(start, end, feature.Rms));
        }
        return frames;
    }

    private static Feature Measure(ReadOnlySpan<byte> values)
    {
        double sumSquares = 0;
        double peak = 0;
        double previous = 0;
        double differenceSquares = 0;
        int zeroCrossings = 0;
        double mean = 0;
        for (int index = 0; index < values.Length; index++)
            mean += (values[index] - 128) / 128.0;
        mean /= Math.Max(1, values.Length);

        double last = 0;
        for (int index = 0; index < values.Length; index++)
        {
            double value = (values[index] - 128) / 128.0 - mean;
            double absolute = Math.Abs(value);
            sumSquares += value * value;
            peak = Math.Max(peak, absolute);
            double difference = value - previous;
            differenceSquares += difference * difference;
            if (index > 0 && Math.Sign(value) != Math.Sign(last))
                zeroCrossings++;
            previous = value;
            last = value;
        }

        double count = Math.Max(1, values.Length);
        double rms = Math.Sqrt(sumSquares / count);
        double differenceRms = Math.Sqrt(differenceSquares / count);
        double zeroCrossingRate = zeroCrossings / count;
        double highRatio = differenceRms / Math.Max(0.001, rms + differenceRms);
        return new Feature(rms, peak, zeroCrossingRate, highRatio);
    }

    private static (DacHitClass Classification, float Confidence) Classify(Feature feature)
    {
        if (feature.Rms < MinimumRms)
            return (DacHitClass.Unknown, 0);

        if (feature.ZeroCrossingRate < 0.075 && feature.HighRatio < 0.52)
        {
            float confidence = (float)Math.Clamp(
                0.55 + (0.075 - feature.ZeroCrossingRate) * 2.0,
                0.50,
                0.95);
            return (DacHitClass.Kick, confidence);
        }

        if (feature.ZeroCrossingRate < 0.17 && feature.HighRatio < 0.68)
        {
            float confidence = (float)Math.Clamp(
                0.50 + (0.17 - feature.ZeroCrossingRate) * 1.5,
                0.45,
                0.90);
            return (DacHitClass.Tom, confidence);
        }

        float snareConfidence = (float)Math.Clamp(
            0.50 + feature.ZeroCrossingRate * 1.2 + feature.HighRatio * 0.25,
            0.45,
            0.92);
        return (DacHitClass.Snare, snareConfidence);
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0)
            return 0;
        double position = Math.Clamp(percentile, 0, 1) * (sorted.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(sorted.Length - 1, lower + 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static IReadOnlyList<DacHitEvent> MergeOverlappingHits(List<DacHitEvent> hits)
    {
        if (hits.Count < 2)
            return hits;

        hits.Sort((left, right) =>
        {
            int comparison = left.StartSample.CompareTo(right.StartSample);
            return comparison != 0
                ? comparison
                : left.SourceStartOffset.CompareTo(right.SourceStartOffset);
        });

        var merged = new List<DacHitEvent>(hits.Count);
        foreach (DacHitEvent candidate in hits)
        {
            if (merged.Count == 0)
            {
                merged.Add(candidate);
                continue;
            }

            DacHitEvent previous = merged[^1];
            if (candidate.StartSample >= previous.EndSample)
            {
                merged.Add(candidate);
                continue;
            }

            DacHitEvent winner = candidate.Confidence > previous.Confidence
                || (candidate.Confidence == previous.Confidence && candidate.PeakLevel > previous.PeakLevel)
                ? candidate
                : previous;
            merged[^1] = winner with
            {
                StartSample = Math.Min(previous.StartSample, candidate.StartSample),
                EndSample = Math.Max(previous.EndSample, candidate.EndSample),
                SourceStartOffset = Math.Min(previous.SourceStartOffset, candidate.SourceStartOffset),
                SourceEndOffset = Math.Max(previous.SourceEndOffset, candidate.SourceEndOffset),
            };
        }

        return merged;
    }

    private readonly record struct Frame(int Start, int End, double Rms);

    private readonly record struct Feature(
        double Rms,
        double Peak,
        double ZeroCrossingRate,
        double HighRatio);
}
