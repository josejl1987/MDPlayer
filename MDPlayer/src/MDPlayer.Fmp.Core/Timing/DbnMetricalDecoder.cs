#nullable enable

namespace Fmp.Core.Timing;

internal sealed record DbnTempoHypothesis(
    double Bpm,
    long PhaseSample,
    double PriorScore);

internal sealed record DbnTempoRun(
    long StartSample,
    long EndSample,
    DbnTempoHypothesis Tempo);

internal sealed record DbnMetricalCandidate(
    DbnTempoHypothesis Tempo,
    Meter Meter,
    int DownbeatPhase,
    long DownbeatSample,
    double Score,
    double MeterMargin,
    double DownbeatMargin,
    double DownbeatMarking);

internal sealed record DbnMetricalResult(
    DbnMetricalCandidate Selected,
    DbnMetricalCandidate? Alternative,
    bool MeterResolved,
    bool DownbeatResolved,
    double MeterConfidence,
    double DownbeatConfidence,
    IReadOnlyList<DbnMetricalCandidate> Candidates,
    int TempoSwitchCount,
    IReadOnlyList<DbnTempoRun> TempoPath);

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

    // These are the explicit DBN parameters: transitions are independent of the
    // song, while the observations are conditional on the hidden beat state.
    // Meter priors are intentionally neutral; 4/4 must win by evidence.
    private static readonly DbnModel Model = new(
        MeterSwitchPenalty: 6.0,
        TempoSwitchPenalty: 8.0,
        TempoRatioPenalty: 1.5,
        ResolveMargin: 0.002,
        DownbeatResolveMargin: 0.01,
        DownbeatMarkingFloor: 0.35,
        SurfaceDownbeatProbability: 0.82,
        SurfaceCompoundProbability: 0.58,
        SurfaceSecondaryProbability: 0.40,
        SurfaceOffbeatProbability: 0.20,
        AccentDownbeatProbability: 0.92,
        AccentCompoundProbability: 0.68,
        // Secondary must be > 0.5 so an accented secondary (e.g. a snare
        // backbeat on beats 2/4 of 4/4) is REWARDED over an unaccented one.
        // With 0.46, BernoulliLikelihood made an accented secondary a
        // penalty (0.245 < 0.283), so the model preferred readings that
        // concentrated every accent on the downbeat — i.e. the fastest
        // octave of a dense grid (240 > 120 for the same physical pattern).
        AccentSecondaryProbability: 0.72,
        AccentOffbeatProbability: 0.22,
        BoundaryDownbeatProbability: 0.95,
        BoundaryOtherProbability: 0.08,
        BoundaryPhaseBonus: 0.10);

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

        DbnTempoHypothesis[] validTempos = tempos
            .Where(tempo => tempo.Bpm > 0 && double.IsFinite(tempo.Bpm))
            .ToArray();
        if (validTempos.Length == 0)
            return null;

        // SCRATCH-DEBUG
        Console.WriteLine($"[DBN-SCRATCH] tempos={string.Join(",", validTempos.Select(t => $"{t.Bpm:0.###}@prior{t.PriorScore:0.###}"))}");
        Console.WriteLine($"[DBN-SCRATCH] streams={string.Join(",", streams.Select(s => $"{s.Name}:{s.Onsets?.Count ?? 0}"))}");
        Console.WriteLine($"[DBN-SCRATCH] samples={startSample}..{endSample} boundaries={string.Join(",", structuralBoundaries)}");

        JointViterbiResult? joint = RunJointViterbi(
            validTempos, streams, sampleRate, startSample, endSample, structuralBoundaries);
        if (joint is null)
            return null;

        var candidates = new List<DbnMetricalCandidate>(validTempos.Length);
        // SCRATCH-DEBUG: phase landscapes per tempo for the chosen meter.
        var scratchPhaseLandscapes = new Dictionary<int, double[]>();
        for (int tempoIndex = 0; tempoIndex < validTempos.Length; tempoIndex++)
        {
            DbnTempoHypothesis tempo = validTempos[tempoIndex];
            Dictionary<Meter, double> meterEvidence = SupportedMeters.ToDictionary(
                meter => meter,
                // PATTERN-PRIMARY: the accent pattern is the meter's real
                // identity — 6/8 vs 2/4 vs 3/4 are the same surface density on
                // different accent grids, and the joint TempoMeterScores (built
                // on surface density) alone flips 3/4 below 2/4. Weight the
                // bar-periodic accent pattern above the joint meter score.
                meter => 0.45 * joint.TempoMeterScores[tempoIndex].GetValueOrDefault(meter)
                    + 0.55 * MeterPatternScore(
                        meter, tempo, streams, sampleRate, startSample));
            // Pick the (meter, phase) pair that maximizes the metrical terms of
            // the full score together. Choosing the meter first and the phase
            // second lets a near-tied meter choice (2/4 vs 4/4 at 120 BPM on a
            // backbeat grid) land the downbeat frames on unaccented positions
            // and sink a hypothesis that is metrically correct.
            Meter meter = SupportedMeters[0];
            int phase = 0;
            double downbeatScore = 0;
            double downbeatMarking = 0;
            double[]? phaseScores = null;
            double bestMetrical = double.NegativeInfinity;
            foreach (Meter candidateMeter in SupportedMeters)
            {
                double[] candidatePhases = PhaseScores(
                    candidateMeter, tempo, streams, sampleRate,
                    startSample, endSample, structuralBoundaries,
                    out double marking);
                int bestPhase = Array.IndexOf(candidatePhases, candidatePhases.Max());
                double metrical = 0.25 * candidatePhases[bestPhase]
                    + 0.20 * meterEvidence[candidateMeter];
                if (metrical > bestMetrical)
                {
                    bestMetrical = metrical;
                    meter = candidateMeter;
                    phase = bestPhase;
                    downbeatScore = candidatePhases[bestPhase];
                    downbeatMarking = marking;
                    phaseScores = candidatePhases;
                }
            }
            scratchPhaseLandscapes[tempoIndex] = phaseScores!;
            // The margin must compare the winner to the STRONGEST genuinely
            // distinct downbeat, not to differently-quantized views of the same
            // downbeat. PhaseScores returns units*substeps fine sub-step phases,
            // and a bar-periodic accent is reachable from several adjacent
            // sub-steps through the observation tolerance (a phase a few thousand
            // samples off a kick still gets full accent activation after
            // clamping), so a literal winner-vs-strongest over every sub-step is
            // dominated by near-duplicate offsets and is ~0 even for a
            // well-determined downbeat. Phases within the observation tolerance of
            // the winner's downbeat are the SAME downbeat and are excluded; the
            // margin is the lead over the best phase that is a real alternative.
            double downbeatMargin = WinnerLeadOverDistinctPhase(
                phaseScores!, phase, sampleRate, tempo.Bpm);
            double meterPatternScore = meterEvidence[meter];
            double competingMeter = meterEvidence
                .Where(pair => pair.Key != meter)
                .Select(pair => pair.Value)
                .DefaultIfEmpty(0)
                .Max();
            double meterMargin = meterEvidence[meter] - competingMeter;
            // The joint Viterbi path explains the complete tempo/meter/beat
            // state sequence; the observation terms keep a bar-length accent
            // pattern from being washed out by dense surface onsets.
            double step = sampleRate * 60.0 / tempo.Bpm / 2.0;
            long downbeat = FirstDownbeatAtOrAfter(
                tempo.PhaseSample, startSample, tempo.Bpm, meter, phase, sampleRate);
            // TIGHT-tolerance boundary-alignment term in the full score: a
            // hypothesis whose NATURAL grid downbeat lands within a small
            // fraction of a step of a structural boundary (loop point) is
            // rewarded. Measured on the pre-snap downbeat so a coarse grid
            // that merely drifts near a boundary does not collect the bonus —
            // the old per-frame 0.40-step boundary likelihood biased half-tempo
            // readings, which have fewer downbeat frames per boundary period.
            double boundaryAlignment = BoundaryAlignment(
                downbeat, structuralBoundaries, step * 0.25) ? 1.0 : 0.0;
            double score = 0.55 * joint.TempoScores[tempoIndex]
                + 0.25 * downbeatScore
                + 0.20 * meterPatternScore
                + 0.05 * boundaryAlignment;
            // A hypothesis whose grid cannot account for a large fraction of
            // the surface onsets (they fall beyond the observation tolerance of
            // every grid node) is under-evidenced: the per-node normalization
            // rewards a coarser grid that explains its few nodes cleanly while
            // silently ignoring the rest. On the 90 BPM 6/8 control, 60/2-4's
            // 8th-note grid (22050) misses every third hi-hat (14700: 7350 >
            // 0.32*step = 7056) and won the raw evidence while ignoring 40% of
            // the percussion; the 90/6-8 grid resolves all of it.
            score *= SurfaceCoverage(tempo, streams, sampleRate, startSample, endSample);
            // The phase search is sub-step quantized; the reading is not more
            // precise than the observation tolerance. When a structural
            // boundary (loop point) coincides with the downbeat within that
            // tolerance, the boundary is the more confident, discrete position —
            // it is real musical structure, not a quantized grid point.
            downbeat = SnapDownbeatToBoundary(downbeat, structuralBoundaries, step * 0.40);
            candidates.Add(new DbnMetricalCandidate(
                tempo, meter, phase, downbeat, score, meterMargin, downbeatMargin, downbeatMarking));
        }

        candidates = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Tempo.Bpm)
            .ThenBy(candidate => candidate.Meter.Numerator)
            .ThenBy(candidate => candidate.DownbeatPhase)
            .ToList();
        // SCRATCH-DEBUG
        Console.WriteLine("[DBN-SCRATCH] candidates:");
        foreach (DbnMetricalCandidate c in candidates)
            Console.WriteLine(
                $"[DBN-SCRATCH]   {c.Tempo.Bpm:0.###}BPM {c.Meter} phase={c.DownbeatPhase} " +
                $"score={c.Score:0.####} meterMargin={c.MeterMargin:0.####} downbeatMargin={c.DownbeatMargin:0.####} " +
                $"coverage={SurfaceCoverage(c.Tempo, streams, sampleRate, startSample, endSample):0.###}");
        foreach (DbnMetricalCandidate c in candidates.Take(2))
        {
            double q = sampleRate * 60.0 / c.Tempo.Bpm;
            double step = q / 2.0;
            double fine = step / 12.0;
            long first = (long)Math.Floor((startSample - c.Tempo.PhaseSample) / fine) - 2;
            BeatFeatureStream? perc = streams.FirstOrDefault(s => s.Name == "percussion");
            BeatFeatureStream? accent = streams.FirstOrDefault(s => s.Name == "accent");
            Console.WriteLine($"[DBN-SCRATCH]   {c.Tempo.Bpm:0.###} percOnlyPhaseLandscape (top 6, avg max strength at db frames):");
            double[] percLand = new double[96];
            var rhythmic = new List<BeatFeatureStream>();
            if (perc is { Onsets.Count: > 0 }) rhythmic.Add(perc);
            if (accent is { Onsets.Count: > 0 }) rhythmic.Add(accent);
            if (rhythmic.Count == 0)
            {
                BeatFeatureStream? bass = streams.FirstOrDefault(s => s.Name == "bass");
                if (bass is { Onsets.Count: > 0 }) rhythmic.Add(bass);
            }
            for (int phase = 0; phase < 96; phase++)
            {
                double total = 0;
                int count = 0;
                for (int frame = 0; frame < 5000; frame++)
                {
                    int beatInBar = (int)PositiveModulo(phase + frame, 96);
                    if (beatInBar != 0)
                        continue;
                    long sample = SampleAt(c.Tempo.PhaseSample, first + frame, fine);
                    if (sample < startSample || sample >= endSample)
                        continue;
                    count++;
                    double best = 0;
                    foreach (BeatFeatureStream ps in rhythmic)
                    {
                        double tol = step * 0.32;
                        int lo = LowerBound(ps.Onsets, sample - (long)tol);
                        for (int i = lo; i < ps.Onsets.Count && ps.Onsets[i].Sample <= sample + tol; i++)
                        {
                            double dist = Math.Abs(ps.Onsets[i].Sample - sample);
                            if (dist <= tol)
                                best = Math.Max(best, ps.Onsets[i].Strength);
                        }
                    }
                    total += best;
                }
                percLand[phase] = count > 0 ? total / count : 0;
            }
            int[] topP = Enumerable.Range(0, 96).OrderByDescending(i => percLand[i]).Take(6).ToArray();
            Console.WriteLine($"[DBN-SCRATCH]     streams=[{string.Join(",", rhythmic.Select(s => s.Name))}] " + string.Join(" ", topP.Select(i => $"{i}:{percLand[i]:0.####}")));
        }
        Console.WriteLine($"[DBN-SCRATCH] jointTempoScores={string.Join(",", joint.TempoScores.Select(s => $"{s:0.###}"))}");
        foreach (DbnMetricalCandidate c in candidates.Take(3))
        {
            int ti = Array.IndexOf(validTempos, c.Tempo);
            Console.WriteLine($"[DBN-SCRATCH]   {c.Tempo.Bpm:0.###} joint={joint.TempoScores[ti]:0.###} meterEv=" +
                string.Join(";", SupportedMeters.Select(m =>
                    $"{m}={(0.45 * joint.TempoMeterScores[ti].GetValueOrDefault(m) + 0.55 * MeterPatternScore(m, c.Tempo, streams, sampleRate, startSample)):0.####}")));
        }
        foreach (DbnMetricalCandidate c in candidates.Take(3))
        {
            if (!scratchPhaseLandscapes.TryGetValue(Array.IndexOf(validTempos, c.Tempo), out double[]? landscape))
                continue;
            int[] top = Enumerable.Range(0, landscape.Length)
                .OrderByDescending(i => landscape[i])
                .Take(8)
                .ToArray();
            Console.WriteLine($"[DBN-SCRATCH]   {c.Tempo.Bpm:0.###} phaseLandscapeTop=" +
                string.Join(" ", top.Select(i => $"{i}:{landscape[i]:0.####}")));
        }
        DbnMetricalCandidate selected = candidates.First();
        // A higher-scored candidate whose OWN margins are degenerate — several
        // phases within the observation tolerance explain the same bar-periodic
        // accent, so no distinct downbeat exists (margin ~0) — must not block a
        // resolved alternative. A slow-tempo grid on a dense event stream often
        // out-scores a musically correct grid precisely because every event lands
        // near some beat, yet its downbeat margin collapses. Prefer the
        // highest-scoring candidate that actually resolves; fall back to the
        // max-score candidate only when nothing resolves.
        DbnMetricalCandidate resolved = candidates
            .Where(candidate => candidate.DownbeatMarking >= Model.DownbeatMarkingFloor
                && candidate.Score >= 0.12
                && candidate.MeterMargin >= Model.ResolveMargin
                && candidate.DownbeatMargin >= Model.DownbeatResolveMargin)
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault();
        if (resolved is not null)
            selected = resolved;
        // The joint pass owns the tempo PATH (it may legitimately change tempo
        // mid-track); the full metrical score owns the tempo SELECTION so a
        // hypothesis whose meter/downbeat reading is incoherent cannot win on
        // the joint likelihood alone.
        DbnMetricalCandidate? alternative = candidates
            .Where(candidate => candidate != selected)
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault();
        bool meterResolved = selected.DownbeatMarking >= Model.DownbeatMarkingFloor
            && selected.Score >= 0.12
            && selected.MeterMargin >= Model.ResolveMargin
            && selected.DownbeatMargin >= Model.DownbeatResolveMargin;
        // The downbeat is where the rhythmic marking puts it. A structural
        // boundary (loop point) refines it via SnapDownbeatToBoundary but is
        // not required: real captured music carries no loop markers, and a
        // marked downbeat is the downbeat evidence.
        bool downbeatResolved = meterResolved;
        double meterConfidence = Math.Clamp(
            0.60 * selected.Score + 0.40 * Math.Min(1, selected.MeterMargin * 8), 0, 1);
        double downbeatConfidence = Math.Clamp(
            0.60 * selected.Score + 0.40 * Math.Min(1, selected.DownbeatMargin * 8), 0, 1);
        // SCRATCH-DEBUG
        Console.WriteLine(
            $"[DBN-SCRATCH] result: sel={selected.Tempo.Bpm:0.###}BPM {selected.Meter} " +
            $"score={selected.Score:0.####} meterMargin={selected.MeterMargin:0.####} " +
            $"downbeatMargin={selected.DownbeatMargin:0.####} downbeatMarking={selected.DownbeatMarking:0.####} " +
            $"meterResolved={meterResolved} downbeatResolved={downbeatResolved} " +
            $"alt={(alternative is null ? "null" : $"{alternative.Tempo.Bpm:0.###}")}");
        return new DbnMetricalResult(
            selected,
            alternative,
            meterResolved,
            downbeatResolved,
            meterConfidence,
            downbeatConfidence,
            candidates,
            joint.TempoSwitchCount,
            joint.TempoPath);
    }

    private static JointViterbiResult? RunJointViterbi(
        IReadOnlyList<DbnTempoHypothesis> tempos,
        IReadOnlyList<BeatFeatureStream> streams,
        int sampleRate,
        long startSample,
        long endSample,
        IReadOnlyList<long> structuralBoundaries)
    {
        var rawNodes = new List<GridNode>();
        var steps = new double[tempos.Count];
        for (int tempoIndex = 0; tempoIndex < tempos.Count; tempoIndex++)
        {
            DbnTempoHypothesis tempo = tempos[tempoIndex];
            double step = sampleRate * 60.0 / tempo.Bpm / 2.0;
            if (step < 1 || !double.IsFinite(step))
                continue;

            steps[tempoIndex] = step;
            long firstFrame = FirstFrameFor(tempo, sampleRate, startSample);
            long lastFrame = (long)Math.Ceiling(
                (endSample - tempo.PhaseSample) / step) + 2;
            int frameCount = (int)Math.Clamp(lastFrame - firstFrame + 1, 2, 250_000);
            for (int frame = 0; frame < frameCount; frame++)
            {
                rawNodes.Add(new GridNode(
                    tempoIndex,
                    SampleAt(tempo.PhaseSample, firstFrame + frame, step),
                    step));
            }
        }

        if (rawNodes.Count == 0)
            return null;

        GridNode[] nodes = rawNodes
            .OrderBy(node => node.Sample)
            .ThenBy(node => node.TempoIndex)
            .ToArray();
        var nodesByTempo = Enumerable.Range(0, tempos.Count)
            .Select(_ => new List<int>())
            .ToArray();
        for (int nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
            nodesByTempo[nodes[nodeIndex].TempoIndex].Add(nodeIndex);

        State[] states = SupportedMeters.SelectMany(StatesFor).ToArray();
        int stateCount = states.Length;
        double[,] scores = new double[nodes.Length, stateCount];
        int[,] previousNodes = new int[nodes.Length, stateCount];
        int[,] previousStates = new int[nodes.Length, stateCount];
        for (int nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
        {
            for (int stateIndex = 0; stateIndex < stateCount; stateIndex++)
            {
                scores[nodeIndex, stateIndex] = double.NegativeInfinity;
                previousNodes[nodeIndex, stateIndex] = -1;
                previousStates[nodeIndex, stateIndex] = -1;
            }

            GridNode currentNode = nodes[nodeIndex];
            for (int currentStateIndex = 0;
                 currentStateIndex < stateCount;
                 currentStateIndex++)
            {
                State currentState = states[currentStateIndex];
                double best = IsInitialNode(currentNode, startSample)
                    ? InitialScore(tempos[currentNode.TempoIndex])
                    : double.NegativeInfinity;
                int bestPreviousNode = -1;
                int bestPreviousState = -1;

                for (int previousTempoIndex = 0;
                     previousTempoIndex < tempos.Count;
                     previousTempoIndex++)
                {
                    if (nodesByTempo[previousTempoIndex].Count == 0)
                        continue;
                    double tolerance = Math.Min(
                        currentNode.Step, steps[previousTempoIndex]) * 0.45;
                    int previousNodeIndex = FindPreviousNode(
                        nodes, nodesByTempo[previousTempoIndex],
                        currentNode.Sample - currentNode.Step,
                        currentNode.Sample, tolerance);
                    if (previousNodeIndex < 0)
                        continue;

                    for (int previousStateIndex = 0;
                         previousStateIndex < stateCount;
                         previousStateIndex++)
                    {
                        double previousScore = scores[previousNodeIndex, previousStateIndex];
                        if (double.IsNegativeInfinity(previousScore))
                            continue;
                        double transition = TransitionScore(
                            states[previousStateIndex], currentState,
                            nodes[previousNodeIndex].TempoIndex,
                            currentNode.TempoIndex, tempos);
                        if (double.IsNegativeInfinity(transition))
                            continue;
                        double value = previousScore + transition;
                        if (value > best)
                        {
                            best = value;
                            bestPreviousNode = previousNodeIndex;
                            bestPreviousState = previousStateIndex;
                        }
                    }
                }

                if (!double.IsNegativeInfinity(best))
                {
                    double logObs = LogObservation(
                        ObservationAt(
                            currentState, currentNode.Sample, streams,
                            currentNode.Step, structuralBoundaries));
                    scores[nodeIndex, currentStateIndex] = best + logObs;
                    previousNodes[nodeIndex, currentStateIndex] = bestPreviousNode;
                    previousStates[nodeIndex, currentStateIndex] = bestPreviousState;
                }
            }
        }

        var tempoScores = new double[tempos.Count];
        var tempoMeterScores = new IReadOnlyDictionary<Meter, double>[tempos.Count];
        int selectedTempoIndex = -1;
        int selectedNodeIndex = -1;
        int selectedStateIndex = -1;
        double selectedRawScore = double.NegativeInfinity;
        for (int tempoIndex = 0; tempoIndex < tempos.Count; tempoIndex++)
        {
            List<int> tempoNodes = nodesByTempo[tempoIndex];
            if (tempoNodes.Count == 0)
            {
                tempoScores[tempoIndex] = 0;
                tempoMeterScores[tempoIndex] = new Dictionary<Meter, double>();
                continue;
            }

            int finalNodeIndex = tempoNodes
                .OrderBy(nodeIndex => Math.Abs(nodes[nodeIndex].Sample - endSample))
                .First();
            // Score each tempo hypothesis on ITS OWN grid. The joint grid lets
            // the global optimum hop between tempo grids, so a "best path ending
            // at tempo X" was typically 21 nodes of the sparse 40 BPM grid plus a
            // short tail on X — every tempo then scored almost identically, and
            // the fastest octave won on the tail's grid density. A constrained
            // same-tempo Viterbi measures each hypothesis on the grid it claims.
            (double tempoRawScore, int pathLength, Dictionary<Meter, double> meterScores) =
                RunConstrainedTempoViterbi(
                    tempoIndex, tempoNodes, nodes, states, stateCount,
                    tempos, streams, startSample, structuralBoundaries);
            double normalized = NormalizeScore(tempoRawScore, Math.Max(1, pathLength));
            tempoScores[tempoIndex] = 0.65 * normalized
                + 0.35 * Math.Clamp(tempos[tempoIndex].PriorScore, 0, 1);
            tempoMeterScores[tempoIndex] = meterScores;
            if (tempoScores[tempoIndex] > selectedRawScore)
            {
                selectedRawScore = tempoScores[tempoIndex];
                selectedTempoIndex = tempoIndex;
                selectedNodeIndex = finalNodeIndex;
                selectedStateIndex = Enumerable.Range(0, stateCount)
                    .OrderByDescending(index => scores[finalNodeIndex, index])
                    .First();
            }
        }

        if (selectedTempoIndex < 0 || selectedNodeIndex < 0 || selectedStateIndex < 0)
            return null;

        var reversePath = new List<GridNode>();
        int node = selectedNodeIndex;
        int state = selectedStateIndex;
        while (node >= 0 && state >= 0)
        {
            reversePath.Add(nodes[node]);
            int previousNode = previousNodes[node, state];
            if (previousNode < 0)
                break;
            int previousState = previousStates[node, state];
            node = previousNode;
            state = previousState;
        }
        reversePath.Reverse();

        var tempoPath = new List<DbnTempoRun>();
        if (reversePath.Count > 0)
        {
            int currentTempoIndex = reversePath[0].TempoIndex;
            long currentStart = startSample;
            for (int pathIndex = 1; pathIndex < reversePath.Count; pathIndex++)
            {
                GridNode pathNode = reversePath[pathIndex];
                if (pathNode.TempoIndex == currentTempoIndex)
                    continue;

                long boundary = Math.Clamp(pathNode.Sample, startSample, endSample);
                if (boundary > currentStart)
                {
                    tempoPath.Add(new DbnTempoRun(
                        currentStart,
                        boundary,
                        tempos[currentTempoIndex]));
                }
                currentTempoIndex = pathNode.TempoIndex;
                currentStart = boundary;
            }
            if (endSample > currentStart)
            {
                tempoPath.Add(new DbnTempoRun(
                    currentStart,
                    endSample,
                    tempos[currentTempoIndex]));
            }
        }

        return new JointViterbiResult(
            selectedTempoIndex,
            Math.Max(0, tempoPath.Count - 1),
            tempoScores,
            tempoMeterScores,
            tempoPath);
    }

    private static bool IsInitialNode(GridNode node, long startSample) =>
        node.Sample >= startSample - node.Step * 2.5
        && node.Sample <= startSample + node.Step;

    /// <summary>
    /// Forward Viterbi restricted to ONE tempo's grid, used to score each tempo
    /// hypothesis on the grid it claims (see the scoring loop). Transitions may
    /// only move within this tempo's own nodes, so the result is the hypothesis'
    /// own fit — not a foreign-grid path that merely ends on this tempo.
    /// </summary>
    private static (double RawScore, int PathLength, Dictionary<Meter, double> MeterScores)
        RunConstrainedTempoViterbi(
            int tempoIndex,
            IReadOnlyList<int> tempoNodes,
            GridNode[] nodes,
            State[] states,
            int stateCount,
            IReadOnlyList<DbnTempoHypothesis> tempos,
            IReadOnlyList<BeatFeatureStream> streams,
            long startSample,
            IReadOnlyList<long> structuralBoundaries)
    {
        int count = tempoNodes.Count;
        var localByGlobal = new Dictionary<int, int>(count);
        for (int local = 0; local < count; local++)
            localByGlobal[tempoNodes[local]] = local;

        var scores = new double[count, stateCount];
        var previousNodes = new int[count, stateCount];
        var previousStates = new int[count, stateCount];
        for (int local = 0; local < count; local++)
        {
            GridNode currentNode = nodes[tempoNodes[local]];
            for (int stateIndex = 0; stateIndex < stateCount; stateIndex++)
            {
                State currentState = states[stateIndex];
                double best = IsInitialNode(currentNode, startSample)
                    ? InitialScore(tempos[tempoIndex])
                    : double.NegativeInfinity;
                int bestPreviousNode = -1;
                int bestPreviousState = -1;

                double tolerance = currentNode.Step * 0.45;
                int previousGlobal = FindPreviousNode(
                    nodes, tempoNodes, currentNode.Sample - currentNode.Step,
                    currentNode.Sample, tolerance);
                if (previousGlobal >= 0
                    && localByGlobal.TryGetValue(previousGlobal, out int previousLocal))
                {
                    for (int previousStateIndex = 0;
                         previousStateIndex < stateCount;
                         previousStateIndex++)
                    {
                        double previousScore = scores[previousLocal, previousStateIndex];
                        if (double.IsNegativeInfinity(previousScore))
                            continue;
                        double transition = TransitionScore(
                            states[previousStateIndex], currentState,
                            tempoIndex, tempoIndex, tempos);
                        if (double.IsNegativeInfinity(transition))
                            continue;
                        double value = previousScore + transition;
                        if (value > best)
                        {
                            best = value;
                            bestPreviousNode = previousLocal;
                            bestPreviousState = previousStateIndex;
                        }
                    }
                }

                if (!double.IsNegativeInfinity(best))
                {
                    scores[local, stateIndex] = best + LogObservation(
                        ObservationAt(
                            currentState, currentNode.Sample, streams,
                            currentNode.Step, structuralBoundaries));
                    previousNodes[local, stateIndex] = bestPreviousNode;
                    previousStates[local, stateIndex] = bestPreviousState;
                }
                else
                {
                    scores[local, stateIndex] = double.NegativeInfinity;
                    previousNodes[local, stateIndex] = -1;
                    previousStates[local, stateIndex] = -1;
                }
            }
        }

        // tempoNodes are in sample order, so the final node is the last one.
        int finalLocal = count - 1;
        double rawScore = double.NegativeInfinity;
        int bestFinalState = 0;
        var meterScores = new Dictionary<Meter, double>();
        foreach (Meter meter in SupportedMeters)
        {
            double meterRawScore = double.NegativeInfinity;
            for (int stateIndex = 0; stateIndex < stateCount; stateIndex++)
            {
                if (states[stateIndex].Meter != meter)
                    continue;
                double value = scores[finalLocal, stateIndex];
                if (value > meterRawScore)
                    meterRawScore = value;
                if (value > rawScore)
                {
                    rawScore = value;
                    bestFinalState = stateIndex;
                }
            }
            meterScores[meter] = NormalizeScore(meterRawScore, count);
        }

        int pathLength = 1;
        int pathLocal = finalLocal;
        int pathState = bestFinalState;
        while (previousNodes[pathLocal, pathState] >= 0)
        {
            int previousLocal = previousNodes[pathLocal, pathState];
            int previousState = previousStates[pathLocal, pathState];
            pathLocal = previousLocal;
            pathState = previousState;
            pathLength++;
        }
        return (rawScore, pathLength, meterScores);
    }

    /// <summary>
    /// The winner's lead over the STRONGEST competing phase — not the weakest.
    /// max(winner - every competitor) measured the gap to the weakest phase
    /// (0.70 for [0.80,0.79,0.10]) and exaggerated confidence; the honest margin
    /// is winner - second-best (0.01 there). This is the canonical formula and
    /// feeds DownbeatConfidence.
    /// </summary>
    internal static double DownbeatMarginFor(IReadOnlyList<double> phaseScores, int phase)
    {
        double competingPhase = phaseScores
            .Where((_, index) => index != phase)
            .DefaultIfEmpty(phaseScores[phase])
            .Max();
        return phaseScores[phase] - competingPhase;
    }

    /// <summary>
    /// Winner's lead over the strongest GENUINELY DISTINCT downbeat. The phase
    /// grid over-samples: a bar-periodic accent is reachable from several
    /// adjacent sub-step phases through the observation tolerance (a phase a few
    /// thousand samples from a kick still scores full accent activation after
    /// clamping). Those near-duplicates are the same downbeat quantized at
    /// different offsets, not competitors; counting them collapses the honest
    /// margin to ~0 even when the downbeat is well determined. Phases whose
    /// downbeat is within the observation tolerance of the winner's downbeat are
    /// excluded; the margin is the lead over the best genuinely alternative phase.
    /// </summary>
    private static double WinnerLeadOverDistinctPhase(
        IReadOnlyList<double> phaseScores,
        int phase,
        int sampleRate,
        double bpm)
    {
        double fineStep = sampleRate * 60.0 / bpm / 2.0 / (phaseScores.Count > 0 ? phaseScores.Count : 1);
        // The phase array covers one bar (units * substeps fine steps). A
        // distinct downbeat is separated from the winner by more than the
        // observation tolerance, measured around the bar (circular).
        int cycle = phaseScores.Count;
        double tolerance = sampleRate * 60.0 / bpm / 2.0 * 0.32;
        double winnerScore = phaseScores[phase];
        double bestCompetitor = 0;
        for (int index = 0; index < cycle; index++)
        {
            if (index == phase)
                continue;
            double score = phaseScores[index];
            if (score <= bestCompetitor)
                continue;
            int forward = ((index - phase) % cycle + cycle) % cycle;
            int circular = Math.Min(forward, cycle - forward);
            if (circular * fineStep <= tolerance)
                continue; // same downbeat, not a real competitor
            bestCompetitor = score;
        }
        return winnerScore - bestCompetitor;
    }

    private static double InitialScore(
        DbnTempoHypothesis tempo)
    {
        double prior = Math.Clamp(tempo.PriorScore, 0.001, 1.0);
        // The first observation is added by the same update as every later
        // observation. This keeps the Viterbi likelihood from double-counting
        // the initial node. Tempo priors are weak and meter/phase priors neutral.
        return 0.05 * Math.Log(prior);
    }

    private static int FindPreviousNode(
        IReadOnlyList<GridNode> nodes,
        IReadOnlyList<int> nodesByTempo,
        double targetSample,
        long currentSample,
        double tolerance)
    {
        int low = 0;
        int high = nodesByTempo.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (nodes[nodesByTempo[middle]].Sample < targetSample)
                low = middle + 1;
            else
                high = middle;
        }

        int bestNode = -1;
        double bestDistance = double.PositiveInfinity;
        for (int offset = -2; offset <= 1; offset++)
        {
            int position = low + offset;
            if (position < 0 || position >= nodesByTempo.Count)
                continue;
            int nodeIndex = nodesByTempo[position];
            long sample = nodes[nodeIndex].Sample;
            if (sample >= currentSample)
                continue;
            double distance = Math.Abs(sample - targetSample);
            if (distance <= tolerance && distance < bestDistance)
            {
                bestNode = nodeIndex;
                bestDistance = distance;
            }
        }
        return bestNode;
    }

    private static double NormalizeScore(double rawScore, int frameCount) =>
        double.IsNegativeInfinity(rawScore)
            ? 0
            : Math.Clamp(Math.Exp(rawScore / Math.Max(1, frameCount)), 0, 1);

    private static double[] PhaseScores(
        Meter meter,
        DbnTempoHypothesis tempo,
        IReadOnlyList<BeatFeatureStream> streams,
        int sampleRate,
        long startSample,
        long endSample,
        IReadOnlyList<long> structuralBoundaries,
        out double bestMarking)
    {
        int units = Units(meter);
        // The Ellis phase sample is arbitrary (any sample, not necessarily on
        // the beat grid), so the downbeat search needs sub-step resolution;
        // integer step shifts cannot recover a half-step phase offset. 12
        // substeps per step reach any offset within the observation tolerance.
        const int substeps = 12;
        double[] scores = new double[units * substeps];
        double[] markings = new double[units * substeps];
        double quarterSamples = sampleRate * 60.0 / tempo.Bpm;
        double stepSamples = quarterSamples / 2.0;
        double fineStep = stepSamples / substeps;
        long firstFrame = (long)Math.Floor((startSample - tempo.PhaseSample) / fineStep) - 2;
        long lastFrame = (long)Math.Ceiling((endSample - tempo.PhaseSample) / fineStep) + 2;
        int count = (int)Math.Clamp(lastFrame - firstFrame + 1, 1, 250_000);
        int cycle = units * substeps;
        // The downbeat-marking evidence is the RHYTHMIC stream (drums, or bass
        // when the track has no drums), not the dense melodic surface: melody
        // saturates the per-frame observation at every phase and flattens the
        // phase landscape. Sparse, bar-periodic marking is what a real downbeat
        // looks like.
        IReadOnlyList<BeatFeatureStream> rhythmic = RhythmicStreams(streams);
        for (int phase = 0; phase < scores.Length; phase++)
        {
            double total = 0;
            double markingTotal = 0;
            int downbeatCount = 0;
            bool boundaryAligned = false;
            for (int frame = 0; frame < count; frame++)
            {
                int beatInBar = (int)PositiveModulo(phase + frame, cycle);
                if (beatInBar != 0)
                    continue;
                long sample = SampleAt(tempo.PhaseSample, firstFrame + frame, fineStep);
                // Only downbeat frames INSIDE the observed span carry evidence.
                // The ±2-frame padding (and the frame just past the last onset)
                // are window artifacts with nothing to observe: they score the
                // floor clamp (~0.026) and drag the average down. A hypothesis
                // whose grid happens to place a downbeat frame just past
                // endSample was penalized by exactly one such frame (90's
                // 9-frame average at 0.612 lost to 89.5's 8-frame 0.643 on the
                // 90 BPM control), so out-of-span frames must not count.
                if (sample < startSample || sample >= endSample)
                    continue;
                downbeatCount++;
                double obs = RhythmicObservationAt(
                    new State(meter, beatInBar), sample, streams, stepSamples);
                total += obs;
                double marking = 0;
                foreach (BeatFeatureStream stream in rhythmic)
                    marking = Math.Max(marking, Nearest(stream.Onsets, sample, stepSamples * 0.32));
                markingTotal += marking;
                if (BoundaryAlignment(sample, structuralBoundaries, stepSamples * 0.25))
                    boundaryAligned = true;
            }
            // Integer division count/cycle truncated the true downbeat-frame
            // count and inflated scores past 1.0 (40 BPM measured 1.087),
            // letting a coarse grid win the phase term on arithmetic, not
            // evidence. Normalize by the exact number of downbeat frames
            // actually sampled.
            scores[phase] = downbeatCount > 0 ? total / downbeatCount : 0;
            markings[phase] = downbeatCount > 0 ? markingTotal / downbeatCount : 0;
            // A phase whose downbeat coincides with a structural boundary (loop
            // point) within the tight 0.25-step tolerance is the more confident
            // reading: the boundary is real musical structure, not a quantized
            // grid point. Applied once per phase AFTER the average, so it is
            // independent of how many downbeat frames the grid carries — the
            // old per-frame boundary likelihood biased sparse grids, which have
            // fewer downbeat frames per boundary period.
            if (downbeatCount > 0 && boundaryAligned)
                scores[phase] = Math.Min(1.0, scores[phase] + Model.BoundaryPhaseBonus);
        }
        bestMarking = markings.Max();
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
            double logLikelihood = 0;
            double total = 0;
            var downbeatStrengths = new List<double>();
            foreach ((long sample, double strength) in accent.Onsets)
            {
                long frame = (long)Math.Round((sample - tempo.PhaseSample) / step,
                    MidpointRounding.AwayFromZero);
                int beat = (int)PositiveModulo(frame - phase, units);
                if (beat == 0)
                    downbeatStrengths.Add(strength);
                // Raw clamped strength, matching the observation model in
                // ObservationAt: a kick (strength 1.0) must read as a near-certain
                // accent, not as the ~0.5 probability that strength/(1+strength)
                // produced — the flattened mapping made every expectation ~0.5
                // and destroyed downbeat/secondary discrimination.
                // Strength is NOT pre-clamped to [0,1]: BernoulliLikelihood
                // saturates the observed-hit mapping, so a kick (1.5-2.0) reads
                // as stronger accent evidence than a snare (1.0). Without this,
                // a reading that moves the snare onto the downbeat and the kick
                // onto a secondary beat scores exactly like the correct reading
                // (kick on the downbeat) — the evidence strengths are
                // combinatorially symmetric under the bar-line shift.
                double activation = strength;
                double expected = ExpectedAccentProbability(meter, beat);
                logLikelihood += strength
                    * Math.Log(BernoulliLikelihood(activation, expected));
                total += strength;
            }
            if (total > 0)
            {
                // A correct reading puts the strongest accents (kicks) on every
                // downbeat; a double-tempo illusion shifts kick/snare/offbeat
                // through the downbeat slot, so its downbeat strengths vary.
                double consistency = DownbeatConsistency(downbeatStrengths);
                best = Math.Max(best, Math.Exp(logLikelihood / total) * consistency);
            }
        }
        _ = startSample;
        return Math.Clamp(best, 0, 1);
    }

    /// <summary>
    /// Downbeat accents must be consistently the strongest accents: the
    /// coefficient-of-variation penalty scores 1.0 for identical strengths
    /// down to 0 at high variance. Fewer than two downbeat onsets carry no
    /// consistency evidence.
    /// </summary>
    private static double DownbeatConsistency(IReadOnlyList<double> strengths)
    {
        if (strengths.Count < 2)
            return 1.0;
        double mean = 0;
        foreach (double strength in strengths)
            mean += strength;
        mean /= strengths.Count;
        if (mean <= 0)
            return 1.0;
        double variance = 0;
        foreach (double strength in strengths)
        {
            double delta = strength - mean;
            variance += delta * delta;
        }
        variance /= strengths.Count;
        return Math.Clamp(1.0 - Math.Sqrt(variance) / mean, 0, 1);
    }

    private static double ExpectedAccentProbability(Meter meter, int beatInBar)
    {
        bool compoundBeat = meter.Denominator == 8 && beatInBar == 3;
        return beatInBar == 0
            ? Model.AccentDownbeatProbability
            : compoundBeat
                ? Model.AccentCompoundProbability
                : beatInBar % 2 == 0
                    ? Model.AccentSecondaryProbability
                    : Model.AccentOffbeatProbability;
    }

    /// <summary>
    /// Fraction of surface onsets the tempo's 8th-note grid can observe at the
    /// model tolerance. A reading that cannot see an onset cannot account for
    /// it; discounting by coverage keeps a sparse grid from winning on the
    /// few nodes it happens to explain while the subdivision pulse passes
    /// through it unseen.
    /// </summary>
    private static double SurfaceCoverage(
        DbnTempoHypothesis tempo,
        IReadOnlyList<BeatFeatureStream> streams,
        int sampleRate,
        long startSample,
        long endSample)
    {
        BeatFeatureStream? surface = streams.FirstOrDefault(stream => stream.Name == "percussion")
            ?? streams.FirstOrDefault(stream => stream.Name == "accent");
        if (surface is null || surface.Onsets.Count == 0)
            return 1.0;
        double step = sampleRate * 60.0 / tempo.Bpm / 2.0;
        double tolerance = step * 0.32;
        int total = 0;
        int seen = 0;
        foreach ((long sample, double _) in surface.Onsets)
        {
            if (sample < startSample || sample >= endSample)
                continue;
            total++;
            long frame = (long)Math.Round((sample - tempo.PhaseSample) / step,
                MidpointRounding.AwayFromZero);
            long node = SampleAt(tempo.PhaseSample, frame, step);
            double dist = Math.Abs(node - sample);
            if (dist <= tolerance)
                seen++;
        }
        return total > 0 ? (double)seen / total : 1.0;
    }

    private static double ObservationAt(
        State state,
        long sample,
        IReadOnlyList<BeatFeatureStream> streams,
        double stepSamples,
        IReadOnlyList<long> structuralBoundaries)
    {
        double signal = 0;
        double signalWeight = 0;
        double accent = 0;
        foreach (BeatFeatureStream stream in streams)
        {
            double nearest = Nearest(stream.Onsets, sample, stepSamples * 0.32);
            signal += stream.Weight * nearest;
            signalWeight += stream.Weight;
            // Accent evidence is the strong downbeat-marking stream (kick/snare)
            // ONLY. The percussion stream also carries hi-hat and other
            // subdivisions; folding them into the accent term makes a hi-hat on
            // every sixteenth look like accent evidence everywhere, which
            // flattens the downbeat/secondary contrast and lets a faster
            // octave win the joint path on raw grid density.
            if (stream.Name == "accent")
                accent = Math.Max(accent, nearest);
        }
        signal = signalWeight > 0 ? Math.Clamp(signal / signalWeight, 0, 1) : 0;
        bool compoundBeat = state.Meter == new Meter(6, 8) && state.BeatInBar == 3;
        double surfaceProbability = state.BeatInBar switch
        {
            0 => Model.SurfaceDownbeatProbability,
            _ when compoundBeat => Model.SurfaceCompoundProbability,
            _ when state.BeatInBar % 2 == 0 => Model.SurfaceSecondaryProbability,
            _ => Model.SurfaceOffbeatProbability,
        };
        double accentProbability = ExpectedAccentProbability(state.Meter, state.BeatInBar);
        // Structural boundaries (loop points) are sparse, user-supplied markers.
        // They must not enter the per-frame likelihood: a Bernoulli conditioned
        // on the beat state made "downbeat without a boundary" a ~0.095 penalty,
        // and a slower grid has fewer downbeat frames per boundary period, so
        // half-tempo readings escaped the penalty while the correct tempo was
        // punished — the joint slow-tempo bias. PhaseScores also maximized
        // boundary coincidences, giving denser-compatible coarse grids a
        // further edge. Excluding the boundary term leaves tempo/meter
        // comparison to the accent and surface evidence, which correctly favor
        // the reviewed grid.
        return Math.Clamp(
            BernoulliLikelihood(signal, surfaceProbability)
                * BernoulliLikelihood(accent, accentProbability),
            0.001,
            0.999);
    }

    /// <summary>
    /// Downbeat-phase observation over the RHYTHMIC streams only (drums, or
    /// bass when the track has no drums). The dense melodic surface saturates
    /// the all-stream weighted mean at every phase and flattens the phase
    /// landscape, so the phase search uses the sparse bar-periodic marking that
    /// a real downbeat produces. A track with no accent stream carries no
    /// accent information and the accent term is neutral; with an empty accent
    /// stream the Bernoulli read "no accent here" at every frame and penalized
    /// downbeat states (~0.12) while rewarding offbeat states (~0.75).
    /// </summary>
    private static double RhythmicObservationAt(
        State state,
        long sample,
        IReadOnlyList<BeatFeatureStream> streams,
        double stepSamples)
    {
        IReadOnlyList<BeatFeatureStream> rhythmic = RhythmicStreams(streams);
        double signal = 0;
        double signalWeight = 0;
        double accent = 0;
        foreach (BeatFeatureStream stream in rhythmic)
        {
            double nearest = Nearest(stream.Onsets, sample, stepSamples * 0.32);
            signal += stream.Weight * nearest;
            signalWeight += stream.Weight;
            if (stream.Name == "accent")
                accent = Math.Max(accent, nearest);
        }
        signal = signalWeight > 0 ? Math.Clamp(signal / signalWeight, 0, 1) : 0;
        bool compoundBeat = state.Meter == new Meter(6, 8) && state.BeatInBar == 3;
        double surfaceProbability = state.BeatInBar switch
        {
            0 => Model.SurfaceDownbeatProbability,
            _ when compoundBeat => Model.SurfaceCompoundProbability,
            _ when state.BeatInBar % 2 == 0 => Model.SurfaceSecondaryProbability,
            _ => Model.SurfaceOffbeatProbability,
        };
        bool hasAccentEvidence = streams.Any(
            stream => stream.Name == "accent" && stream.Onsets.Count > 0);
        return Math.Clamp(
            BernoulliLikelihood(signal, surfaceProbability)
                * (hasAccentEvidence
                    ? BernoulliLikelihood(accent, ExpectedAccentProbability(state.Meter, state.BeatInBar))
                    : 1.0),
            0.001,
            0.999);
    }

    /// <summary>
    /// The streams that carry downbeat-marking evidence, in priority order:
    /// drums (accent, then percussion), falling back to bass only when the
    /// track has no drum stream at all. Streams with no onsets are ignored.
    /// </summary>
    private static IReadOnlyList<BeatFeatureStream> RhythmicStreams(
        IReadOnlyList<BeatFeatureStream> streams)
    {
        var rhythmic = new List<BeatFeatureStream>(2);
        foreach (string name in new[] { "percussion", "accent" })
        {
            BeatFeatureStream? stream = streams.FirstOrDefault(s => s.Name == name);
            if (stream is { Onsets.Count: > 0 })
                rhythmic.Add(stream);
        }
        if (rhythmic.Count == 0)
        {
            BeatFeatureStream? bass = streams.FirstOrDefault(s => s.Name == "bass");
            if (bass is { Onsets.Count: > 0 })
                rhythmic.Add(bass);
        }
        return rhythmic;
    }

    private static double BernoulliLikelihood(double activation, double expectedHitProbability)
    {
        // Saturate the affine mapping on the SUM, not on activation first:
        // an onset stronger than nominal (kick 1.5-2.0 vs snare 1.0) must read
        // as MORE certain accent evidence, otherwise strength ordering is lost
        // and "snare on the downbeat / kick on a secondary beat" scores exactly
        // like the correct reading. For activation <= 1.0 this is unchanged.
        double observedHitProbability = Math.Clamp(0.05 + 0.90 * activation, 0, 1);
        return expectedHitProbability * observedHitProbability
            + (1.0 - expectedHitProbability) * (1.0 - observedHitProbability);
    }

    private static bool BoundaryAlignment(
        long downbeat,
        IReadOnlyList<long> structuralBoundaries,
        double tolerance) =>
        structuralBoundaries.Any(boundary => Math.Abs(boundary - downbeat) <= tolerance);

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

    private static double TransitionScore(
        State prior,
        State current,
        int priorTempoIndex,
        int currentTempoIndex,
        IReadOnlyList<DbnTempoHypothesis> tempos)
    {
        bool tempoChanged = priorTempoIndex != currentTempoIndex;
        double tempoPenalty = 0;
        if (tempoChanged)
        {
            double ratio = tempos[currentTempoIndex].Bpm / tempos[priorTempoIndex].Bpm;
            tempoPenalty = -Model.TempoSwitchPenalty
                - Model.TempoRatioPenalty * Math.Abs(Math.Log(Math.Max(1e-9, ratio)));
        }

        if (prior.Meter == current.Meter)
        {
            int expected = (int)PositiveModulo(prior.BeatInBar + 1, Units(prior.Meter));
            return expected == current.BeatInBar
                ? tempoPenalty
                : double.NegativeInfinity;
        }

        bool priorAtBarEnd = prior.BeatInBar == Units(prior.Meter) - 1;
        return priorAtBarEnd && current.BeatInBar == 0
            ? -Model.MeterSwitchPenalty + tempoPenalty
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

    private static long FirstFrameFor(
        DbnTempoHypothesis tempo,
        int sampleRate,
        long startSample)
    {
        double step = sampleRate * 60.0 / tempo.Bpm / 2.0;
        return (long)Math.Floor((startSample - tempo.PhaseSample) / step) - 2;
    }

    private static long FirstDownbeatAtOrAfter(
        long phaseSample,
        long startSample,
        double bpm,
        Meter meter,
        int beatInBar,
        int sampleRate)
    {
        double step = sampleRate * 60.0 / bpm / 2.0;
        int units = Units(meter);
        // PhaseScores searches at substep resolution; recover the same grid
        // here so the reported downbeat sample matches the scored alignment.
        const int substeps = 12;
        double fineStep = step / substeps;
        int cycle = units * substeps;
        // The downbeat frames are those with frame ≡ -beatInBar (mod cycle);
        // find the first such frame at or after the frame containing startSample.
        long firstFrame = (long)Math.Floor((startSample - phaseSample) / fineStep);
        long frame = firstFrame + PositiveModulo(-beatInBar - firstFrame, cycle);
        return SampleAt(phaseSample, frame, fineStep);
    }

    private static long SnapDownbeatToBoundary(
        long downbeat,
        IReadOnlyList<long> structuralBoundaries,
        double tolerance)
    {
        long best = downbeat;
        double bestDistance = double.PositiveInfinity;
        foreach (long boundary in structuralBoundaries)
        {
            double distance = Math.Abs(boundary - downbeat);
            if (distance <= tolerance && distance < bestDistance)
            {
                bestDistance = distance;
                best = boundary;
            }
        }
        return best;
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

    private sealed record DbnModel(
        double MeterSwitchPenalty,
        double TempoSwitchPenalty,
        double TempoRatioPenalty,
        double ResolveMargin,
        double DownbeatResolveMargin,
        double DownbeatMarkingFloor,
        double SurfaceDownbeatProbability,
        double SurfaceCompoundProbability,
        double SurfaceSecondaryProbability,
        double SurfaceOffbeatProbability,
        double AccentDownbeatProbability,
        double AccentCompoundProbability,
        double AccentSecondaryProbability,
        double AccentOffbeatProbability,
        double BoundaryDownbeatProbability,
        double BoundaryOtherProbability,
        double BoundaryPhaseBonus);

    private readonly record struct State(Meter Meter, int BeatInBar);

    private readonly record struct GridNode(
        int TempoIndex,
        long Sample,
        double Step);

    private sealed record JointViterbiResult(
        int SelectedTempoIndex,
        int TempoSwitchCount,
        IReadOnlyList<double> TempoScores,
        IReadOnlyList<IReadOnlyDictionary<Meter, double>> TempoMeterScores,
        IReadOnlyList<DbnTempoRun> TempoPath);
}
