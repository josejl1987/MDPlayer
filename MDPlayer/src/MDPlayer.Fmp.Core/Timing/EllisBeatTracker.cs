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
    IReadOnlyList<long> BeatSamples,
    int AgreeingStreams,
    int ActiveStreams);

internal sealed record EllisBeatTrackingResult(
    EllisBeatCandidate Selected,
    EllisBeatCandidate? Alternative,
    IReadOnlyList<EllisBeatCandidate> Candidates);

/// <summary>Cached symbolic onset envelope and its normalized autocorrelation.</summary>
internal sealed record OnsetEnvelope(
    string Name,
    double[] Values,
    double[] Correlation,
    int HopSamples,
    double Weight);

internal sealed record EllisReferenceBeatPath(
    IReadOnlyList<int> Frames,
    double[] LocalScore,
    double CumulativeScore);

internal readonly record struct TempoAgreement(
    double Score,
    int AgreeingStreams,
    int ActiveStreams);

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
    private const int MaxTempoSeedsPerStream = 5;
    private const int MaxCandidates = 12;
    // A retained family member is an ambiguity only when it has independent
    // support and remains within the calibrated competition band. Candidates
    // outside this band stay available to DBN diagnostics but do not suppress
    // a resolved tempo merely by existing.
    private const double CompetitiveTempoScoreRatio = 0.90;

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
            TempoAgreement agreement = AutocorrelationAgreement(envelopes, bpm, sampleRate);
            if (agreement.Score <= 0)
                continue;

            BeatPath path = DecodeBeatPath(
                envelopes, bpm, sampleRate, startSample, endSample);
            double score = 0.65 * agreement.Score + 0.35 * path.Score;
            long phase = path.Samples.Count > 0 ? path.Samples[0] : startSample;
            candidates.Add(new EllisBeatCandidate(
                bpm, phase, score, path.Score, path.Samples,
                agreement.AgreeingStreams, agreement.ActiveStreams));
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
            .Where(candidate => candidate.AgreeingStreams >= 2)
            .Where(candidate => candidate.Score >= selected.Score * CompetitiveTempoScoreRatio)
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
            return new OnsetEnvelope(stream.Name, values, correlation, hopSamples, stream.Weight);

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
        return new OnsetEnvelope(stream.Name, values, correlation, hopSamples, stream.Weight);
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

    private static TempoAgreement AutocorrelationAgreement(
        IReadOnlyList<OnsetEnvelope> streams,
        double bpm,
        int sampleRate)
    {
        double weightedScore = 0;
        double totalWeight = 0;
        int agreeingStreams = 0;
        int activeStreams = 0;
        foreach (OnsetEnvelope stream in streams)
        {
            double score = StreamPeriodicity(stream, bpm, sampleRate);
            weightedScore += stream.Weight * score;
            totalWeight += stream.Weight;
            activeStreams++;
            if (score >= 0.55)
                agreeingStreams++;
        }
        if (totalWeight <= 0)
            return new TempoAgreement(0, agreeingStreams, activeStreams);
        double agreement = agreeingStreams / (double)streams.Count;
        return new TempoAgreement(
            0.70 * weightedScore / totalWeight + 0.30 * agreement,
            agreeingStreams,
            activeStreams);
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

    private static BeatPath DecodeBeatPath(
        IReadOnlyList<OnsetEnvelope> envelopes,
        double bpm,
        int sampleRate,
        long startSample,
        long endSample)
    {
        if (envelopes.Count == 0)
            return new BeatPath(0, Array.Empty<long>());

        EllisReferenceBeatPath[] paths = envelopes
            .Select(envelope => TrackFixedTempo(
                envelope.Values, bpm, sampleRate, envelope.HopSamples, trim: false))
            .ToArray();
        double totalWeight = envelopes.Sum(envelope => envelope.Weight);
        double score = totalWeight > 0
            ? paths.Zip(envelopes, (path, envelope) => path.Frames.Count == 0
                    ? 0
                    : envelope.Weight * PathStrength(path.LocalScore, path.Frames))
                .Sum() / totalWeight
            : 0;
        int bestIndex = Enumerable.Range(0, paths.Length)
            .OrderByDescending(index => paths[index].CumulativeScore * envelopes[index].Weight)
            .First();
        long[] samples = paths[bestIndex].Frames
            .Select(frame => startSample + (long)frame * envelopes[bestIndex].HopSamples)
            .Where(sample => sample >= startSample && sample <= endSample)
            .ToArray();
        return new BeatPath(Math.Clamp(score / (1.0 + score), 0, 1), samples);
    }

    /// <summary>
    /// Runs the frame-indexed Ellis dynamic program for a fixed tempo. This is
    /// intentionally exposed to the test assembly so persisted librosa fixtures
    /// can compare the beat frame sequence, not just the selected BPM.
    /// </summary>
    internal static EllisReferenceBeatPath TrackFixedTempo(
        IReadOnlyList<double> onsetEnvelope,
        double bpm,
        int sampleRate,
        int hopSamples,
        bool trim = false)
    {
        ArgumentNullException.ThrowIfNull(onsetEnvelope);
        if (sampleRate <= 0 || hopSamples <= 0 || bpm <= 0 || onsetEnvelope.Count < 2)
            throw new ArgumentOutOfRangeException();

        int framesPerBeat = Math.Max(1, (int)Math.Round(
            sampleRate / (double)hopSamples * 60.0 / bpm,
            MidpointRounding.ToEven));
        double[] localScore = BuildLocalScore(onsetEnvelope, framesPerBeat);
        (int[] backlink, double[] cumulative) = RunEllisDynamicProgram(
            localScore, framesPerBeat, tightness: 100.0);
        int tail = LastBeat(cumulative);
        var frames = new List<int>();
        for (int frame = tail; frame >= 0; frame = backlink[frame])
        {
            frames.Add(frame);
            if (backlink[frame] < 0)
                break;
        }
        frames.Reverse();
        if (trim)
            TrimWeakEdges(frames, localScore);
        return new EllisReferenceBeatPath(frames, localScore, cumulative[tail]);
    }

    private static double[] BuildLocalScore(
        IReadOnlyList<double> onsetEnvelope,
        int framesPerBeat)
    {
        double mean = onsetEnvelope.Average();
        double variance = onsetEnvelope.Count > 1
            ? onsetEnvelope.Sum(value => (value - mean) * (value - mean))
                / (onsetEnvelope.Count - 1)
            : 0;
        double scale = Math.Sqrt(variance);
        var normalized = onsetEnvelope
            .Select(value => scale > 1e-12 ? value / scale : value)
            .ToArray();
        var local = new double[normalized.Length];
        for (int frame = 0; frame < normalized.Length; frame++)
        {
            for (int offset = -framesPerBeat; offset <= framesPerBeat; offset++)
            {
                int source = frame - offset;
                if (source < 0 || source >= normalized.Length)
                    continue;
                double window = Math.Exp(-0.5 * Math.Pow(
                    offset * 32.0 / framesPerBeat, 2));
                local[frame] += window * normalized[source];
            }
        }
        return local;
    }

    private static (int[] Backlink, double[] Cumulative) RunEllisDynamicProgram(
        double[] localScore,
        int framesPerBeat,
        double tightness)
    {
        var backlink = Enumerable.Repeat(-1, localScore.Length).ToArray();
        var cumulative = new double[localScore.Length];
        // Match librosa 0.11: retain first-beat mode while local score stays
        // below the initial threshold, including leading silence and weak
        // pickup frames. The first meaningful frame keeps its predecessor;
        // only the absence of a predecessor makes it a path root.
        double threshold = 0.01 * localScore.Max();
        bool firstBeat = true;
        for (int frame = 0; frame < localScore.Length; frame++)
        {
            double bestScore = double.NegativeInfinity;
            int beatLocation = -1;
            int first = frame - (int)Math.Round(framesPerBeat / 2.0);
            int lastExclusive = frame - 2 * framesPerBeat - 1;
            for (int location = first; location > lastExclusive; location--)
            {
                if (location < 0)
                    break;
                double interval = frame - location;
                double score = cumulative[location]
                    - tightness * Math.Pow(Math.Log(interval) - Math.Log(framesPerBeat), 2);
                if (score > bestScore)
                {
                    bestScore = score;
                    beatLocation = location;
                }
            }

            cumulative[frame] = beatLocation >= 0
                ? localScore[frame] + bestScore
                : localScore[frame];
            if (firstBeat && localScore[frame] < threshold)
            {
                backlink[frame] = -1;
            }
            else
            {
                backlink[frame] = beatLocation;
                firstBeat = false;
            }
        }
        return (backlink, cumulative);
    }

    private static int LastBeat(IReadOnlyList<double> cumulative)
    {
        var maxima = new List<int>();
        for (int index = 0; index < cumulative.Count; index++)
        {
            bool left = index == 0 || cumulative[index] >= cumulative[index - 1];
            bool right = index == cumulative.Count - 1
                || cumulative[index] >= cumulative[index + 1];
            if (left && right)
                maxima.Add(index);
        }
        if (maxima.Count == 0)
        {
            int best = 0;
            for (int index = 1; index < cumulative.Count; index++)
                if (cumulative[index] > cumulative[best])
                    best = index;
            return best;
        }
        double[] values = maxima.Select(index => cumulative[index]).OrderBy(value => value).ToArray();
        double threshold = 0.5 * values[values.Length / 2];
        for (int index = maxima.Count - 1; index >= 0; index--)
            if (cumulative[maxima[index]] >= threshold)
                return maxima[index];
        return maxima[^1];
    }

    private static void TrimWeakEdges(List<int> frames, IReadOnlyList<double> localScore)
    {
        if (frames.Count == 0)
            return;
        double[] beatScores = frames.Select(frame => localScore[frame]).ToArray();
        double rms = Math.Sqrt(beatScores.Select(value => value * value).Average());
        double threshold = 0.5 * rms;
        while (frames.Count > 0 && localScore[frames[0]] <= threshold)
            frames.RemoveAt(0);
        while (frames.Count > 0 && localScore[frames[^1]] <= threshold)
            frames.RemoveAt(frames.Count - 1);
    }

    private static double PathStrength(IReadOnlyList<double> localScore, IReadOnlyList<int> frames)
    {
        if (frames.Count == 0)
            return 0;
        return frames.Average(frame => Math.Max(0, localScore[frame]));
    }

    private static bool IsHalfDouble(double left, double right)
    {
        double ratio = left / right;
        return Math.Abs(ratio - 0.5) < 0.01 || Math.Abs(ratio - 2.0) < 0.01;
    }

    private readonly record struct BeatPath(double Score, IReadOnlyList<long> Samples);
}
