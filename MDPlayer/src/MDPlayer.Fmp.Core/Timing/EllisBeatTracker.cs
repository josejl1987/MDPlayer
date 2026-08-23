#nullable enable

namespace Fmp.Core.Timing;

/// <summary>A weighted symbolic onset stream used by the offline beat tracker.</summary>
internal sealed record BeatFeatureStream(
    string Name,
    IReadOnlyList<(long Sample, double Strength)> Onsets,
    double Weight);

internal sealed record EllisBeatCandidate(
    double Bpm,
    long PhaseSample,
    double Score,
    double PathScore,
    IReadOnlyList<long> BeatSamples);

internal sealed record EllisBeatTrackingResult(
    EllisBeatCandidate Selected,
    EllisBeatCandidate? Alternative,
    IReadOnlyList<EllisBeatCandidate> Candidates);

/// <summary>Cached symbolic onset envelope and its normalized autocorrelation.</summary>
internal sealed record OnsetEnvelope(
    string Name,
    double[] Correlation,
    int HopSamples,
    double Weight);

/// <summary>
/// A small symbolic adaptation of Ellis's offline beat tracker.
///
/// Tempo induction uses several independent onset streams and retains local
/// autocorrelation peaks plus their half/double metrical relatives.  For each
/// resulting hypothesis, beat positions are selected by a dynamic program whose
/// transition cost is the log interval-ratio penalty used by the Ellis tracker.
/// The tracker only returns a musical hypothesis; it never rewrites source
/// samples.
/// </summary>
internal static class EllisBeatTracker
{
    private const double MinBpm = 40.0;
    private const double MaxBpm = 240.0;
    private const double LogIntervalSigma = 0.16;
    private const int MaxTempoSeedsPerStream = 5;
    private const int MaxCandidates = 12;

    internal static EllisBeatTrackingResult? Track(
        IReadOnlyList<BeatFeatureStream> streams,
        int sampleRate,
        long startSample,
        long endSample)
    {
        ArgumentNullException.ThrowIfNull(streams);
        if (sampleRate <= 0 || endSample <= startSample)
            return null;

        BeatFeatureStream[] active = streams
            .Where(stream => stream.Onsets is not null && stream.Onsets.Count >= 2)
            .Select(stream => new BeatFeatureStream(
                stream.Name,
                stream.Onsets.OrderBy(onset => onset.Sample).ToArray(),
                Math.Max(0.01, stream.Weight)))
            .ToArray();
        if (active.Length == 0)
            return null;

        OnsetEnvelope[] envelopes = active
            .Select(stream => BuildOnsetEnvelope(stream, sampleRate, startSample, endSample))
            .ToArray();
        double[] seeds = InduceTempoSeeds(envelopes, sampleRate);
        if (seeds.Length == 0)
            return null;

        var candidateBpms = new HashSet<int>();
        foreach (double seed in seeds)
        {
            foreach (double ratio in new[] { 0.5, 1.0, 2.0 })
            {
                double bpm = seed * ratio;
                if (bpm >= MinBpm && bpm <= MaxBpm)
                    candidateBpms.Add((int)Math.Round(bpm * 2.0));
            }
        }

        var candidates = new List<EllisBeatCandidate>();
        foreach (int bpmBin in candidateBpms)
        {
            double bpm = bpmBin / 2.0;
            double tempoScore = AutocorrelationAgreement(envelopes, bpm, sampleRate);
            if (tempoScore <= 0)
                continue;

            EllisBeatCandidate? bestPhase = null;
            foreach (long phase in PhaseCandidates(active, bpm, sampleRate, startSample))
            {
                BeatPath path = DecodeBeatPath(active, bpm, phase, sampleRate, startSample, endSample);
                double score = 0.65 * tempoScore + 0.35 * path.Score;
                if (bestPhase is null || score > bestPhase.Score)
                {
                    bestPhase = new EllisBeatCandidate(
                        bpm, phase, score, path.Score, path.Samples);
                }
            }
            if (bestPhase is not null)
                candidates.Add(bestPhase);
        }

        if (candidates.Count == 0)
            return null;

        candidates = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Bpm)
            .Take(MaxCandidates)
            .ToList();
        EllisBeatCandidate selected = candidates[0];
        // Only a metrical-family competitor belongs in the ambiguity channel.
        // An unrelated lower-scoring tempo remains in Candidates for DBN
        // evaluation, but must not make a musically resolved tempo appear
        // ambiguous merely because the search retained several hypotheses.
        EllisBeatCandidate? alternative = candidates
            .Where(candidate => IsHalfDouble(candidate.Bpm, selected.Bpm))
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault();

        return new EllisBeatTrackingResult(selected, alternative, candidates);
    }

    private static OnsetEnvelope BuildOnsetEnvelope(
        BeatFeatureStream stream,
        int sampleRate,
        long startSample,
        long endSample)
    {
        int hopSamples = Math.Max(1, sampleRate / 100);
        int frameCount = (int)Math.Clamp(
            Math.Ceiling((endSample - startSample) / (double)hopSamples) + 1,
            2,
            250_000);
        var values = new double[frameCount];
        foreach ((long sample, double strength) in stream.Onsets)
        {
            int frame = (int)Math.Clamp(
                Math.Round((sample - startSample) / (double)hopSamples,
                    MidpointRounding.AwayFromZero),
                0,
                frameCount - 1);
            values[frame] = Math.Min(4.0, values[frame] + strength);
        }

        double mean = values.Average();
        var centered = new double[values.Length];
        double totalEnergy = 0;
        for (int index = 0; index < values.Length; index++)
        {
            centered[index] = values[index] - mean;
            totalEnergy += centered[index] * centered[index];
        }

        int maximumLag = Math.Min(
            values.Length - 1,
            (int)Math.Ceiling(sampleRate * 60.0 / MinBpm / hopSamples));
        var correlation = new double[maximumLag + 1];
        correlation[0] = 1.0;
        if (totalEnergy <= 1e-12)
            return new OnsetEnvelope(stream.Name, correlation, hopSamples, stream.Weight);

        for (int lag = 1; lag <= maximumLag; lag++)
        {
            double covariance = 0;
            double leftEnergy = 0;
            double rightEnergy = 0;
            for (int index = 0; index < centered.Length - lag; index++)
            {
                double left = centered[index];
                double right = centered[index + lag];
                covariance += left * right;
                leftEnergy += left * left;
                rightEnergy += right * right;
            }
            double normalized = leftEnergy > 1e-12 && rightEnergy > 1e-12
                ? covariance / Math.Sqrt(leftEnergy * rightEnergy)
                : 0;
            correlation[lag] = Math.Clamp((normalized + 1.0) / 2.0, 0, 1);
        }
        return new OnsetEnvelope(stream.Name, correlation, hopSamples, stream.Weight);
    }

    private static double[] InduceTempoSeeds(
        IReadOnlyList<OnsetEnvelope> streams,
        int sampleRate)
    {
        var histogram = new Dictionary<int, double>();
        foreach (OnsetEnvelope stream in streams)
        {
            Dictionary<int, double> local = BuildAutocorrelationHistogram(stream, sampleRate);
            foreach (int bin in LocalPeakBins(local).OrderByDescending(bin => local[bin]).Take(MaxTempoSeedsPerStream))
                histogram[bin] = histogram.GetValueOrDefault(bin) + local[bin] * stream.Weight;
        }

        return histogram
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Take(MaxTempoSeedsPerStream)
            .Select(pair => pair.Key / 2.0)
            .ToArray();
    }

    private static Dictionary<int, double> BuildAutocorrelationHistogram(
        OnsetEnvelope stream,
        int sampleRate)
    {
        var histogram = new Dictionary<int, double>();
        int minimumLag = Math.Max(1, (int)Math.Floor(
            sampleRate * 60.0 / MaxBpm / stream.HopSamples));
        int maximumLag = Math.Min(
            stream.Correlation.Length - 1,
            (int)Math.Ceiling(sampleRate * 60.0 / MinBpm / stream.HopSamples));
        for (int lag = minimumLag; lag <= maximumLag; lag++)
        {
            double score = stream.Correlation[lag];
            if (score <= 0.5 + 1e-9)
                continue;
            double bpm = 60.0 * sampleRate / (lag * (double)stream.HopSamples);
            int bin = (int)Math.Round(bpm * 2.0);
            histogram[bin] = Math.Max(histogram.GetValueOrDefault(bin), score);
        }
        return histogram;
    }

    private static IEnumerable<int> LocalPeakBins(IReadOnlyDictionary<int, double> histogram)
    {
        foreach (int bin in histogram.Keys)
        {
            double value = histogram[bin];
            double left = histogram.GetValueOrDefault(bin - 1);
            double right = histogram.GetValueOrDefault(bin + 1);
            if (value >= left && value >= right)
                yield return bin;
        }
    }

    private static double AutocorrelationAgreement(
        IReadOnlyList<OnsetEnvelope> streams,
        double bpm,
        int sampleRate)
    {
        double weightedScore = 0;
        double totalWeight = 0;
        int agreeingStreams = 0;
        foreach (OnsetEnvelope stream in streams)
        {
            double score = StreamPeriodicity(stream, bpm, sampleRate);
            weightedScore += stream.Weight * score;
            totalWeight += stream.Weight;
            if (score >= 0.55)
                agreeingStreams++;
        }
        if (totalWeight <= 0)
            return 0;
        double agreement = agreeingStreams / (double)streams.Count;
        return 0.70 * weightedScore / totalWeight + 0.30 * agreement;
    }

    private static double StreamPeriodicity(OnsetEnvelope stream, double bpm, int sampleRate)
    {
        int center = (int)Math.Round(sampleRate * 60.0 / bpm / stream.HopSamples);
        if (center <= 0 || center >= stream.Correlation.Length)
            return 0.5;
        int first = Math.Max(1, center - 2);
        int last = Math.Min(stream.Correlation.Length - 1, center + 2);
        double best = 0.5;
        for (int lag = first; lag <= last; lag++)
            best = Math.Max(best, stream.Correlation[lag]);
        return best;
    }

    private static IEnumerable<long> PhaseCandidates(
        IReadOnlyList<BeatFeatureStream> streams,
        double bpm,
        int sampleRate,
        long startSample)
    {
        double samplesPerBeat = sampleRate * 60.0 / bpm;
        long phaseStep = Math.Max(1, (long)Math.Round(samplesPerBeat / 16.0));
        var bins = new Dictionary<long, double>();
        foreach (BeatFeatureStream stream in streams)
        {
            foreach ((long sample, double strength) in stream.Onsets)
            {
                double relative = (sample - startSample) / samplesPerBeat;
                double residue = relative - Math.Floor(relative);
                long bin = (long)Math.Round(residue * samplesPerBeat / phaseStep) * phaseStep;
                bins[bin] = bins.GetValueOrDefault(bin) + stream.Weight * strength;
            }
        }

        yield return startSample;
        foreach (long bin in bins.OrderByDescending(pair => pair.Value).Take(8).Select(pair => pair.Key))
        {
            long phase = startSample + bin;
            while (phase > startSample + samplesPerBeat)
                phase -= (long)Math.Round(samplesPerBeat);
            yield return phase;
        }
    }

    private static BeatPath DecodeBeatPath(
        IReadOnlyList<BeatFeatureStream> streams,
        double bpm,
        long phase,
        int sampleRate,
        long startSample,
        long endSample)
    {
        double samplesPerBeat = sampleRate * 60.0 / bpm;
        long step = Math.Max(1, (long)Math.Round(samplesPerBeat / 8.0));
        long firstBeat = (long)Math.Floor((startSample - phase) / samplesPerBeat) - 1;
        int count = Math.Max(2, (int)Math.Ceiling((endSample - (phase + firstBeat * samplesPerBeat)) / samplesPerBeat) + 1);
        const int offsetCount = 5;
        double[,] scores = new double[count, offsetCount];
        int[,] predecessors = new int[count, offsetCount];
        for (int beat = 0; beat < count; beat++)
        {
            for (int offset = 0; offset < offsetCount; offset++)
            {
                long sample = (long)Math.Round(phase + (firstBeat + beat) * samplesPerBeat)
                    + (offset - 2) * step;
                double observation = ObservationAt(streams, sample, samplesPerBeat);
                scores[beat, offset] = observation;
                predecessors[beat, offset] = -1;
                if (beat == 0)
                    continue;
                double best = double.NegativeInfinity;
                for (int previous = 0; previous < offsetCount; previous++)
                {
                    long previousSample = (long)Math.Round(phase + (firstBeat + beat - 1) * samplesPerBeat)
                        + (previous - 2) * step;
                    double interval = sample - previousSample;
                    double logRatio = Math.Log(Math.Max(1, interval) / samplesPerBeat);
                    double transition = -logRatio * logRatio / (2 * LogIntervalSigma * LogIntervalSigma);
                    double value = scores[beat - 1, previous] + transition;
                    if (value > best)
                    {
                        best = value;
                        predecessors[beat, offset] = previous;
                    }
                }
                scores[beat, offset] += best;
            }
        }

        int finalOffset = 0;
        for (int offset = 1; offset < offsetCount; offset++)
        {
            if (scores[count - 1, offset] > scores[count - 1, finalOffset])
                finalOffset = offset;
        }

        var samples = new long[count];
        int current = finalOffset;
        double observationTotal = 0;
        int observationCount = 0;
        for (int beat = count - 1; beat >= 0; beat--)
        {
            long sample = (long)Math.Round(phase + (firstBeat + beat) * samplesPerBeat)
                + (current - 2) * step;
            samples[beat] = sample;
            if (sample >= startSample && sample <= endSample)
            {
                observationTotal += ObservationAt(streams, sample, samplesPerBeat);
                observationCount++;
            }
            current = beat > 0 ? predecessors[beat, current] : current;
        }
        double averageObservation = observationCount > 0 ? observationTotal / observationCount : 0;
        return new BeatPath(Math.Clamp(averageObservation, 0, 1), samples);
    }

    private static double ObservationAt(
        IReadOnlyList<BeatFeatureStream> streams,
        long sample,
        double samplesPerBeat)
    {
        double total = 0;
        double weight = 0;
        double tolerance = Math.Max(1, samplesPerBeat * 0.18);
        foreach (BeatFeatureStream stream in streams)
        {
            double nearest = 0;
            int first = LowerBound(stream.Onsets, sample - tolerance);
            for (int index = first;
                 index < stream.Onsets.Count && stream.Onsets[index].Sample <= sample + tolerance;
                 index++)
            {
                (long onset, double strength) = stream.Onsets[index];
                long distance = Math.Abs(onset - sample);
                nearest = Math.Max(nearest, strength * Math.Exp(-distance * distance / (2 * tolerance * tolerance)));
            }
            total += stream.Weight * nearest;
            weight += stream.Weight;
        }
        return weight > 0 ? total / (total + weight) : 0;
    }

    private static int LowerBound(
        IReadOnlyList<(long Sample, double Strength)> onsets,
        double sample)
    {
        int low = 0;
        int high = onsets.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (onsets[middle].Sample < sample)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private static bool IsHalfDouble(double left, double right)
    {
        double ratio = left / right;
        return Math.Abs(ratio - 0.5) < 0.01 || Math.Abs(ratio - 2.0) < 0.01;
    }

    private readonly record struct BeatPath(double Score, IReadOnlyList<long> Samples);
}
