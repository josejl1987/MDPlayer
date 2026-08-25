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
    double DownbeatMargin);

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
        DownbeatEvidenceMargin: 0.05,
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
        BoundaryOtherProbability: 0.08);

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

        JointViterbiResult? joint = RunJointViterbi(
            validTempos, streams, sampleRate, startSample, endSample, structuralBoundaries);
        if (joint is null)
            return null;

        var candidates = new List<DbnMetricalCandidate>(validTempos.Length);
        for (int tempoIndex = 0; tempoIndex < validTempos.Length; tempoIndex++)
        {
            DbnTempoHypothesis tempo = validTempos[tempoIndex];
            Dictionary<Meter, double> meterEvidence = SupportedMeters.ToDictionary(
                meter => meter,
                meter => 0.55 * joint.TempoMeterScores[tempoIndex].GetValueOrDefault(meter)
                    + 0.45 * MeterPatternScore(
                        meter, tempo, streams, sampleRate, startSample));
            // Pick the (meter, phase) pair that maximizes the metrical terms of
            // the full score together. Choosing the meter first and the phase
            // second lets a near-tied meter choice (2/4 vs 4/4 at 120 BPM on a
            // backbeat grid) land the downbeat frames on unaccented positions
            // and sink a hypothesis that is metrically correct.
            Meter meter = SupportedMeters[0];
            int phase = 0;
            double downbeatScore = 0;
            double[]? phaseScores = null;
            double bestMetrical = double.NegativeInfinity;
            foreach (Meter candidateMeter in SupportedMeters)
            {
                double[] candidatePhases = PhaseScores(
                    candidateMeter, tempo, streams, sampleRate,
                    startSample, endSample, structuralBoundaries);
                int bestPhase = Array.IndexOf(candidatePhases, candidatePhases.Max());
                double metrical = 0.25 * candidatePhases[bestPhase]
                    + 0.20 * meterEvidence[candidateMeter];
                if (metrical > bestMetrical)
                {
                    bestMetrical = metrical;
                    meter = candidateMeter;
                    phase = bestPhase;
                    downbeatScore = candidatePhases[bestPhase];
                    phaseScores = candidatePhases;
                }
            }
            double downbeatMargin = phaseScores!
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
            // The joint Viterbi path explains the complete tempo/meter/beat
            // state sequence; the observation terms keep a bar-length accent
            // pattern from being washed out by dense surface onsets.
            double score = 0.55 * joint.TempoScores[tempoIndex]
                + 0.25 * downbeatScore
                + 0.20 * meterPatternScore;
            if (Environment.GetEnvironmentVariable("DBG_DBN") == "1")
            {
                Console.WriteLine($"DBG_DBN tempo={tempo.Bpm:0.###} joint={joint.TempoScores[tempoIndex]:0.###} downbeat={downbeatScore:0.###} meterPat={meterPatternScore:0.###} meter={meter} phase={phase} full={score:0.###} meterEv={string.Join(";", meterEvidence.Select(kv => $"{kv.Key}={kv.Value:0.###}"))}");
            }
            long downbeat = DownbeatAtOrBefore(
                tempo.PhaseSample,
                startSample, tempo.Bpm, meter, phase, sampleRate);
            candidates.Add(new DbnMetricalCandidate(
                tempo, meter, phase, downbeat, score, meterMargin, downbeatMargin));
        }

        candidates = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Tempo.Bpm)
            .ThenBy(candidate => candidate.Meter.Numerator)
            .ThenBy(candidate => candidate.DownbeatPhase)
            .ToList();
        DbnMetricalCandidate selected = candidates.First();
        // The joint pass owns the tempo PATH (it may legitimately change tempo
        // mid-track); the full metrical score owns the tempo SELECTION so a
        // hypothesis whose meter/downbeat reading is incoherent cannot win on
        // the joint likelihood alone.
        DbnMetricalCandidate? alternative = candidates
            .Where(candidate => candidate != selected)
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault();
        bool meterResolved = HasAccentEvidence(streams)
            && selected.Score >= 0.12
            && selected.MeterMargin >= Model.ResolveMargin
            && selected.DownbeatMargin >= Model.DownbeatEvidenceMargin;
        bool downbeatEvidence = HasAccentEvidence(streams)
            && HasStructuralBoundaryEvidence(
                selected, streams, sampleRate, startSample, structuralBoundaries);
        bool downbeatResolved = meterResolved
            && downbeatEvidence
            && selected.DownbeatMargin >= Model.ResolveMargin;
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
                    if (Environment.GetEnvironmentVariable("DBG_DBN") == "1"
                        && currentNode.Sample < 44100 * 2
                        && (currentNode.TempoIndex is 0 or 4
                            || Math.Abs(tempos[currentNode.TempoIndex].Bpm - 120) < 0.01))
                    {
                        Console.WriteLine($"DBG_DBN_OBS tempoIdx={currentNode.TempoIndex} bpm={tempos[currentNode.TempoIndex].Bpm:0.###} sample={currentNode.Sample} state={currentState.Meter}/{currentState.BeatInBar} logObs={logObs:0.###}");
                    }
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
            if (Environment.GetEnvironmentVariable("DBG_DBN") == "1")
            {
                Console.WriteLine($"DBG_DBN tempo={tempos[tempoIndex].Bpm:0.###} rawNorm={normalized:0.###} prior={tempos[tempoIndex].PriorScore:0.###} joint={tempoScores[tempoIndex]:0.###} pathLen={pathLength} jointMeterEv={string.Join(";", meterScores.Select(kv => $"{kv.Key}={kv.Value:0.###}"))}");
            }
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
        if (Environment.GetEnvironmentVariable("DBG_DBN") == "1")
        {
            Console.WriteLine($"DBG_DBN_PATH selTempoIdx={selectedTempoIndex} bpm={tempos[selectedTempoIndex].Bpm:0.###} pathNodes={reversePath.Count} rawScore={selectedRawScore:0.###} avgLog={selectedRawScore / Math.Max(1, reversePath.Count):0.###} meterMix={string.Join(";", reversePath.GroupBy(n => n.TempoIndex).Select(g => $"{tempos[g.Key].Bpm:0.###}={g.Count()}"))}");
        }

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
        IReadOnlyList<long> structuralBoundaries)
    {
        int units = Units(meter);
        // The Ellis phase sample is arbitrary (any sample, not necessarily on
        // the beat grid), so the downbeat search needs sub-step resolution;
        // integer step shifts cannot recover a half-step phase offset. 12
        // substeps per step reach any offset within the observation tolerance.
        const int substeps = 12;
        double[] scores = new double[units * substeps];
        double quarterSamples = sampleRate * 60.0 / tempo.Bpm;
        double stepSamples = quarterSamples / 2.0;
        double fineStep = stepSamples / substeps;
        long firstFrame = (long)Math.Floor((startSample - tempo.PhaseSample) / fineStep) - 2;
        long lastFrame = (long)Math.Ceiling((endSample - tempo.PhaseSample) / fineStep) + 2;
        int count = (int)Math.Clamp(lastFrame - firstFrame + 1, 1, 250_000);
        int cycle = units * substeps;
        for (int phase = 0; phase < scores.Length; phase++)
        {
            double total = 0;
            int downbeatCount = 0;
            for (int frame = 0; frame < count; frame++)
            {
                int beatInBar = (int)PositiveModulo(phase + frame, cycle);
                if (beatInBar != 0)
                    continue;
                downbeatCount++;
                long sample = SampleAt(tempo.PhaseSample, firstFrame + frame, fineStep);
                double obs = ObservationAt(
                    new State(meter, beatInBar), sample, streams, stepSamples, structuralBoundaries);
                total += obs;
                if (Environment.GetEnvironmentVariable("DBG_DBN") == "1"
                    && Math.Abs(tempo.Bpm - 120) < 0.01 && meter.Numerator == 4 && meter.Denominator == 4 && phase == 94)
                {
                    Console.WriteLine($"DBG_DBN_PS bpm={tempo.Bpm:0.###} phase={phase} sample={sample} obs={obs:0.###} ps={tempo.PhaseSample}");
                }
            }
            scores[phase] = count > 0 ? total / Math.Max(1, count / cycle) : 0;
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
            double logLikelihood = 0;
            double total = 0;
            foreach ((long sample, double strength) in accent.Onsets)
            {
                long frame = (long)Math.Round((sample - tempo.PhaseSample) / step,
                    MidpointRounding.AwayFromZero);
                int beat = (int)PositiveModulo(frame - phase, units);
                // Raw clamped strength, matching the observation model in
                // ObservationAt: a kick (strength 1.0) must read as a near-certain
                // accent, not as the ~0.5 probability that strength/(1+strength)
                // produced — the flattened mapping made every expectation ~0.5
                // and destroyed downbeat/secondary discrimination.
                double activation = Math.Clamp(strength, 0, 1);
                double expected = ExpectedAccentProbability(meter, beat);
                logLikelihood += strength
                    * Math.Log(BernoulliLikelihood(activation, expected));
                total += strength;
            }
            if (total > 0)
                best = Math.Max(best, Math.Exp(logLikelihood / total));
        }
        _ = startSample;
        return Math.Clamp(best, 0, 1);
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
        double boundary = structuralBoundaries.Any(value =>
            Math.Abs(value - sample) <= stepSamples * 0.40) ? 1.0 : 0;
        bool compoundBeat = state.Meter == new Meter(6, 8) && state.BeatInBar == 3;
        double surfaceProbability = state.BeatInBar switch
        {
            0 => Model.SurfaceDownbeatProbability,
            _ when compoundBeat => Model.SurfaceCompoundProbability,
            _ when state.BeatInBar % 2 == 0 => Model.SurfaceSecondaryProbability,
            _ => Model.SurfaceOffbeatProbability,
        };
        double accentProbability = ExpectedAccentProbability(state.Meter, state.BeatInBar);
        double boundaryProbability = state.BeatInBar == 0
            ? Model.BoundaryDownbeatProbability
            : Model.BoundaryOtherProbability;
        return Math.Clamp(
            BernoulliLikelihood(signal, surfaceProbability)
                * BernoulliLikelihood(accent, accentProbability)
                * BernoulliLikelihood(boundary, boundaryProbability),
            0.001,
            0.999);
    }

    private static double BernoulliLikelihood(double activation, double expectedHitProbability)
    {
        double observedHitProbability = 0.05 + 0.90 * Math.Clamp(activation, 0, 1);
        return expectedHitProbability * observedHitProbability
            + (1.0 - expectedHitProbability) * (1.0 - observedHitProbability);
    }

    private static bool HasAccentEvidence(IReadOnlyList<BeatFeatureStream> streams) =>
        streams.Any(stream => stream.Name == "accent" && stream.Onsets.Count >= 2);

    private static bool HasStructuralBoundaryEvidence(
        DbnMetricalCandidate selected,
        IReadOnlyList<BeatFeatureStream> streams,
        int sampleRate,
        long startSample,
        IReadOnlyList<long> structuralBoundaries)
    {
        if (structuralBoundaries.Count == 0)
            return false;

        double step = sampleRate * 60.0 / selected.Tempo.Bpm / 2.0;
        long downbeat = DownbeatAtOrBefore(
            selected.Tempo.PhaseSample,
            startSample,
            selected.Tempo.Bpm,
            selected.Meter,
            selected.DownbeatPhase,
            sampleRate);
        bool boundary = structuralBoundaries.Any(sample =>
            Math.Abs(sample - downbeat) <= step * 0.40);
        bool accent = streams
            .Where(stream => stream.Name == "accent")
            .SelectMany(stream => stream.Onsets)
            .Any(onset => Math.Abs(onset.Sample - downbeat) <= step * 0.40);
        return boundary && accent;
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

    private static long DownbeatAtOrBefore(
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
        long firstFrame = (long)Math.Floor((startSample - phaseSample) / fineStep) - 2;
        long downbeat = phaseSample
            + (long)Math.Round(
                (firstFrame + PositiveModulo(-beatInBar, units * substeps)) * fineStep,
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

    private sealed record DbnModel(
        double MeterSwitchPenalty,
        double TempoSwitchPenalty,
        double TempoRatioPenalty,
        double ResolveMargin,
        double DownbeatEvidenceMargin,
        double SurfaceDownbeatProbability,
        double SurfaceCompoundProbability,
        double SurfaceSecondaryProbability,
        double SurfaceOffbeatProbability,
        double AccentDownbeatProbability,
        double AccentCompoundProbability,
        double AccentSecondaryProbability,
        double AccentOffbeatProbability,
        double BoundaryDownbeatProbability,
        double BoundaryOtherProbability);

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
