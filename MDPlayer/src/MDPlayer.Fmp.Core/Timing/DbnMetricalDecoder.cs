#nullable enable

namespace Fmp.Core.Timing;

internal sealed record DbnTempoHypothesis(
    double Bpm,
    long PhaseSample,
    double PriorScore);

internal sealed record DbnMetricalCandidate(
    DbnTempoHypothesis Tempo,
    Meter Meter,
    int DownbeatPhase,
    long DownbeatSample,
    double Score,
    double MeterMargin,
    double DownbeatMargin);

internal sealed record DbnMetricalResult(
    DbnMetricalCandidate Selected,
    DbnMetricalCandidate? Alternative,
    bool MeterResolved,
    bool DownbeatResolved,
    double MeterConfidence,
    double DownbeatConfidence,
    IReadOnlyList<DbnMetricalCandidate> Candidates);

/// <summary>
/// Compact symbolic DBN/Viterbi decoder for meter and downbeat.
///
/// The hidden state is (tempo hypothesis, meter, beat-in-bar). Observations are
/// independent onset/accent/structural-boundary evidence. Same-meter transitions
/// advance exactly one eighth-note state; meter changes are only allowed at a bar
/// boundary and pay a large penalty. A close meter or phase margin is reported as
/// unresolved instead of being converted into a confident guess.
/// </summary>
internal static class DbnMetricalDecoder
{
    private static readonly Meter[] SupportedMeters =
    {
        new(2, 4), new(3, 4), new(4, 4), new(6, 8),
    };

    private const double MeterSwitchPenalty = 6.0;
    // Symbolic grids are quantized and often have near-tied bar hypotheses. The
    // margin is deliberately small, but meter still requires a clearly preferred
    // downbeat phase so a bare periodic stream remains unresolved.
    private const double ResolveMargin = 0.002;
    private const double DownbeatEvidenceMargin = 0.05;

    internal static DbnMetricalResult? Decode(
        IReadOnlyList<DbnTempoHypothesis> tempos,
        IReadOnlyList<BeatFeatureStream> streams,
        int sampleRate,
        long startSample,
        long endSample,
        IReadOnlyList<long> structuralBoundaries)
    {
        ArgumentNullException.ThrowIfNull(tempos);
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(structuralBoundaries);
        if (sampleRate <= 0 || endSample <= startSample || tempos.Count == 0)
            return null;

        var candidates = new List<DbnMetricalCandidate>();
        foreach (DbnTempoHypothesis tempo in tempos)
        {
            if (tempo.Bpm <= 0 || !double.IsFinite(tempo.Bpm))
                continue;
            ViterbiPath path = RunViterbi(
                tempo, fixedMeter: null, streams, sampleRate, startSample, endSample,
                structuralBoundaries);
            if (path.Frames.Count == 0)
                continue;

            Dictionary<Meter, double> meterEvidence = SupportedMeters.ToDictionary(
                meter => meter,
                meter => 0.25 * path.MeterScores.GetValueOrDefault(meter)
                    + 0.45 * MeterPatternScore(
                        meter, tempo, streams, sampleRate, startSample)
                    + 0.30 * MeterPrior(meter));
            Meter meter = meterEvidence
                .OrderByDescending(pair => pair.Value)
                .ThenByDescending(pair => pair.Key.QuartersPerBar)
                .First().Key;
            double[] phaseScores = PhaseScores(
                meter, tempo, streams, sampleRate, startSample, endSample, structuralBoundaries);
            int phase = Array.IndexOf(phaseScores, phaseScores.Max());
            double downbeatScore = phaseScores[phase];
            double downbeatMargin = phaseScores
                .Where((_, index) => index != phase)
                .DefaultIfEmpty(0)
                .Max(value => downbeatScore - value);
            double meterPatternScore = meterEvidence[meter];
            double competingMeter = meterEvidence
                .Where(pair => pair.Key != meter)
                .Select(pair => pair.Value)
                .DefaultIfEmpty(0)
                .Max();
            double meterMargin = meterEvidence[meter] - competingMeter;
            // The Viterbi path explains the full state sequence; the
            // downbeat observation term keeps a bar-length accent pattern
            // from being washed out by dense surface onsets.
            double score = 0.25 * path.Score
                + 0.25 * downbeatScore
                + 0.45 * meterPatternScore
                + 0.05 * Math.Clamp(tempo.PriorScore, 0, 1);
            long downbeat = DownbeatAtOrBefore(
                tempo.PhaseSample, path.FirstFrame, startSample, tempo.Bpm, meter, phase, sampleRate);
            candidates.Add(new DbnMetricalCandidate(
                tempo, meter, phase, downbeat, score, meterMargin, downbeatMargin));
        }

        if (candidates.Count == 0)
            return null;
        candidates = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Tempo.Bpm)
            .ThenBy(candidate => candidate.Meter.Numerator)
            .ThenBy(candidate => candidate.DownbeatPhase)
            .ToList();
        DbnMetricalCandidate selected = candidates[0];
        DbnMetricalCandidate? alternative = candidates.Skip(1).FirstOrDefault();
        bool meterResolved = selected.Score >= 0.12
            && selected.MeterMargin >= ResolveMargin
            && selected.DownbeatMargin >= DownbeatEvidenceMargin;
        bool downbeatResolved = meterResolved
            && structuralBoundaries.Count > 0
            && selected.DownbeatMargin >= ResolveMargin;
        double meterConfidence = Math.Clamp(
            0.60 * selected.Score + 0.40 * Math.Min(1, selected.MeterMargin * 8), 0, 1);
        double downbeatConfidence = Math.Clamp(
            0.60 * selected.Score + 0.40 * Math.Min(1, selected.DownbeatMargin * 8), 0, 1);
        return new DbnMetricalResult(
            selected,
            alternative,
            meterResolved,
            downbeatResolved,
            meterConfidence,
            downbeatConfidence,
            candidates);
    }

    private static ViterbiPath RunViterbi(
        DbnTempoHypothesis tempo,
        Meter? fixedMeter,
        IReadOnlyList<BeatFeatureStream> streams,
        int sampleRate,
        long startSample,
        long endSample,
        IReadOnlyList<long> structuralBoundaries)
    {
        double quarterSamples = sampleRate * 60.0 / tempo.Bpm;
        double stepSamples = quarterSamples / 2.0;
        if (stepSamples < 1)
            return ViterbiPath.Empty;
        long firstFrame = (long)Math.Floor((startSample - tempo.PhaseSample) / stepSamples) - 2;
        long lastFrame = (long)Math.Ceiling((endSample - tempo.PhaseSample) / stepSamples) + 2;
        int frameCount = (int)Math.Clamp(lastFrame - firstFrame + 1, 2, 250_000);
        State[] states = fixedMeter is Meter fixedValue
            ? StatesFor(fixedValue)
            : SupportedMeters.SelectMany(StatesFor).ToArray();
        double[,] scores = new double[frameCount, states.Length];
        int[,] previous = new int[frameCount, states.Length];
        for (int stateIndex = 0; stateIndex < states.Length; stateIndex++)
        {
            State state = states[stateIndex];
            long sample = SampleAt(tempo.PhaseSample, firstFrame, stepSamples);
            scores[0, stateIndex] = LogObservation(
                ObservationAt(state, sample, streams, stepSamples, structuralBoundaries));
            previous[0, stateIndex] = -1;
        }

        for (int frame = 1; frame < frameCount; frame++)
        {
            long sample = SampleAt(tempo.PhaseSample, firstFrame + frame, stepSamples);
            for (int currentIndex = 0; currentIndex < states.Length; currentIndex++)
            {
                State current = states[currentIndex];
                double best = double.NegativeInfinity;
                int bestPrevious = -1;
                for (int previousIndex = 0; previousIndex < states.Length; previousIndex++)
                {
                    State prior = states[previousIndex];
                    double transition = TransitionScore(prior, current);
                    if (double.IsNegativeInfinity(transition))
                        continue;
                    double value = scores[frame - 1, previousIndex] + transition;
                    if (value > best)
                    {
                        best = value;
                        bestPrevious = previousIndex;
                    }
                }
                scores[frame, currentIndex] = best + LogObservation(
                    ObservationAt(current, sample, streams, stepSamples, structuralBoundaries));
                previous[frame, currentIndex] = bestPrevious;
            }
        }

        int finalState = 0;
        for (int stateIndex = 1; stateIndex < states.Length; stateIndex++)
        {
            if (scores[frameCount - 1, stateIndex] > scores[frameCount - 1, finalState])
                finalState = stateIndex;
        }

        State[] path = new State[frameCount];
        int currentState = finalState;
        for (int frame = frameCount - 1; frame >= 0; frame--)
        {
            path[frame] = states[currentState];
            currentState = frame > 0 ? previous[frame, currentState] : currentState;
            if (currentState < 0)
                currentState = 0;
        }

        var meterScores = new Dictionary<Meter, double>();
        foreach (Meter meter in SupportedMeters)
        {
            double best = double.NegativeInfinity;
            for (int stateIndex = 0; stateIndex < states.Length; stateIndex++)
            {
                if (states[stateIndex].Meter == meter)
                    best = Math.Max(best, scores[frameCount - 1, stateIndex]);
            }
            meterScores[meter] = Math.Clamp(Math.Exp(best / frameCount), 0, 1);
        }

        Meter selectedMeter = path
            .GroupBy(value => value.Meter)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.Numerator)
            .First().Key;
        int firstBeatInBar = path.First(value => value.Meter == selectedMeter).BeatInBar;
        double pathScore = Math.Clamp(
            Math.Exp(scores[frameCount - 1, finalState] / frameCount), 0, 1);
        return new ViterbiPath(
            selectedMeter,
            firstBeatInBar,
            firstFrame,
            pathScore,
            meterScores,
            path);
    }

    private static double[] PhaseScores(
        Meter meter,
        DbnTempoHypothesis tempo,
        IReadOnlyList<BeatFeatureStream> streams,
        int sampleRate,
        long startSample,
        long endSample,
        IReadOnlyList<long> structuralBoundaries)
    {
        int units = Units(meter);
        double[] scores = new double[units];
        double quarterSamples = sampleRate * 60.0 / tempo.Bpm;
        double stepSamples = quarterSamples / 2.0;
        long firstFrame = (long)Math.Floor((startSample - tempo.PhaseSample) / stepSamples) - 2;
        long lastFrame = (long)Math.Ceiling((endSample - tempo.PhaseSample) / stepSamples) + 2;
        int count = (int)Math.Clamp(lastFrame - firstFrame + 1, 1, 250_000);
        for (int phase = 0; phase < units; phase++)
        {
            double total = 0;
            for (int frame = 0; frame < count; frame++)
            {
                int beatInBar = (int)PositiveModulo(phase + frame, units);
                if (beatInBar != 0)
                    continue;
                long sample = SampleAt(tempo.PhaseSample, firstFrame + frame, stepSamples);
                total += ObservationAt(
                    new State(meter, beatInBar), sample, streams, stepSamples, structuralBoundaries);
            }
            scores[phase] = count > 0 ? total / Math.Max(1, count / units) : 0;
        }
        return scores;
    }

    private static double MeterPatternScore(
        Meter meter,
        DbnTempoHypothesis tempo,
        IReadOnlyList<BeatFeatureStream> streams,
        int sampleRate,
        long startSample)
    {
        BeatFeatureStream? accent = streams.FirstOrDefault(stream => stream.Name == "accent")
            ?? streams.FirstOrDefault(stream => stream.Name == "percussion");
        if (accent is null || accent.Onsets.Count == 0)
            return 0.5;

        double step = sampleRate * 60.0 / tempo.Bpm / 2.0;
        int units = Units(meter);
        double best = 0;
        for (int phase = 0; phase < units; phase++)
        {
            double weighted = 0;
            double total = 0;
            foreach ((long sample, double strength) in accent.Onsets)
            {
                long frame = (long)Math.Round((sample - tempo.PhaseSample) / step,
                    MidpointRounding.AwayFromZero);
                int beat = (int)PositiveModulo(frame - phase, units);
                double expected = beat == 0
                    ? 1.0
                    : meter.Denominator == 8 && beat == 3
                        ? 0.62
                        : beat % 2 == 0 ? 0.32 : 0.08;
                weighted += strength * expected;
                total += strength;
            }
            if (total > 0)
                best = Math.Max(best, weighted / total);
        }
        _ = startSample;
        return Math.Clamp(best, 0, 1);
    }

    private static double MeterPrior(Meter meter) => meter switch
    {
        { Numerator: 4, Denominator: 4 } => 1.0,
        { Numerator: 3, Denominator: 4 } => 0.82,
        { Numerator: 6, Denominator: 8 } => 0.78,
        _ => 0.0,
    };

    private static double ObservationAt(
        State state,
        long sample,
        IReadOnlyList<BeatFeatureStream> streams,
        double stepSamples,
        IReadOnlyList<long> structuralBoundaries)
    {
        double signal = 0;
        double accent = 0;
        foreach (BeatFeatureStream stream in streams)
        {
            double nearest = Nearest(stream.Onsets, sample, stepSamples * 0.32);
            signal += stream.Weight * nearest;
            if (stream.Name is "percussion" or "accent")
                accent = Math.Max(accent, nearest);
        }
        double boundary = structuralBoundaries.Any(value =>
            Math.Abs(value - sample) <= stepSamples * 0.40) ? 1.0 : 0;
        signal = signal / (signal + 2.0);
        bool compoundBeat = state.Meter == new Meter(6, 8) && state.BeatInBar == 3;
        double result = state.BeatInBar switch
        {
            0 => 0.72 * signal + 0.48 * accent + 0.15 * boundary,
            _ when compoundBeat => 0.48 * signal + 0.24 * accent,
            _ when state.BeatInBar % 2 == 0 => 0.30 * signal + 0.08 * accent,
            _ => 0.05 * signal,
        };
        return Math.Clamp(result, 0.001, 0.999);
    }

    private static double Nearest(
        IReadOnlyList<(long Sample, double Strength)> onsets,
        long sample,
        double tolerance)
    {
        double best = 0;
        int first = LowerBound(onsets, sample - tolerance);
        for (int index = first;
             index < onsets.Count && onsets[index].Sample <= sample + tolerance;
             index++)
        {
            (long onset, double strength) = onsets[index];
            long distance = Math.Abs(onset - sample);
            best = Math.Max(best, strength * Math.Exp(-distance * distance / (2 * tolerance * tolerance)));
        }
        return best;
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

    private static double LogObservation(double observation) => Math.Log(observation);

    private static double TransitionScore(State prior, State current)
    {
        if (prior.Meter == current.Meter)
        {
            int expected = (int)PositiveModulo(prior.BeatInBar + 1, Units(prior.Meter));
            return expected == current.BeatInBar ? 0 : double.NegativeInfinity;
        }
        bool priorAtBarEnd = prior.BeatInBar == Units(prior.Meter) - 1;
        return priorAtBarEnd && current.BeatInBar == 0
            ? -MeterSwitchPenalty
            : double.NegativeInfinity;
    }

    private static State[] StatesFor(Meter meter) =>
        Enumerable.Range(0, Units(meter))
            .Select(beat => new State(meter, beat))
            .ToArray();

    private static int Units(Meter meter) =>
        meter.Denominator == 8 ? meter.Numerator : meter.Numerator * 2;

    private static long SampleAt(long phase, long frame, double step) =>
        (long)Math.Round(phase + frame * step, MidpointRounding.AwayFromZero);

    private static long DownbeatAtOrBefore(
        long phaseSample,
        long firstFrame,
        long startSample,
        double bpm,
        Meter meter,
        int beatInBar,
        int sampleRate)
    {
        double step = sampleRate * 60.0 / bpm / 2.0;
        int units = Units(meter);
        long downbeat = phaseSample
            + (long)Math.Round(
                (firstFrame + PositiveModulo(-beatInBar, units)) * step,
                MidpointRounding.AwayFromZero);
        long bar = Math.Max(1, (long)Math.Round(units * step, MidpointRounding.AwayFromZero));
        while (downbeat > startSample)
            downbeat -= bar;
        return downbeat;
    }

    private static int PositiveModulo(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static long PositiveModulo(long value, int modulus)
    {
        long result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private readonly record struct State(Meter Meter, int BeatInBar);

    private sealed record ViterbiPath(
        Meter Meter,
        int InitialBeatInBar,
        long FirstFrame,
        double Score,
        IReadOnlyDictionary<Meter, double> MeterScores,
        IReadOnlyList<State> Frames)
    {
        public static ViterbiPath Empty { get; } = new(
            new Meter(4, 4), 0, 0, 0,
            new Dictionary<Meter, double>(),
            Array.Empty<State>());
    }
}
