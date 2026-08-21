using Fmp.Core.Visualization;

namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Precomputed, deterministic pitch-range camera (§11). The per-frame target
/// expands as soon as lookahead reveals a note and contracts after a 0.75 s
/// hold (§11.3). Short ornaments are not zoomed for (§11.3). Boundaries prefer
/// C, but only inside the interval that keeps every note visible (§11.4). The
/// baked range is critically damped in both directions (§11.5): every target
/// anticipates its notes by at least <see cref="AdditionalLookaheadSeconds"/>
/// of lookahead while damping converges in ~0.2 s, so no sounding note is ever
/// hidden. The configured spans are readability targets; important content
/// may expand them. All state is baked at construction — GetRange/IsClipped
/// are pure lookups.
///
/// The per-frame targets are produced by a single chronological sweep over the
/// monotonic frame clock (event-driven note/point streams over bounded MIDI
/// value multisets), so each note enters, exits, becomes active, and ends
/// exactly once, and each pitch point is applied exactly once:
/// O((N+Q)·log(N+Q)) plus O(1) bounded aggregate upkeep per frame — no
/// per-frame rescan of the notes or pitch points.
/// </summary>
internal sealed class PitchCamera
{
    private const int MinSpan = 12;
    private const int MaxSpan = 24;
    private const int Fm3MaxSpan = 30;
    private const double ContractionHoldSeconds = 0.75;
    private const double OrnamentThresholdSeconds = 0.080;
    private const double AdditionalLookaheadSeconds = 0.50;
    private const double OrnamentClipThresholdSemitones = 2.0;
    private const double DampingTimeConstantSeconds = 0.045;
    private const int DefaultMinMidi = 48;
    private const int DefaultMaxMidi = 72;

    /// <summary>
    /// Maximum value-domain width for the dense multiset path. Prepared MIDI
    /// anchors are non-negative and pitch points clamp to [0, 127], so the
    /// realistic domain is a few hundred values wide at most; only a corrupted
    /// anchor widens it beyond this, where the key-table fallback takes over.
    /// </summary>
    private const int MaxDenseDomain = 65536;

    private readonly FrameRange[] _frames;
    private readonly long _timelineStartSample;
    private readonly long _timelineEndSample;
    private readonly int _totalFrames;
    private readonly int _sampleRate;
    private readonly int _fpsNumerator;
    private readonly int _fpsDenominator;

    /// <summary>Deterministic build-work counters (complexity regression tests).</summary>
    internal sealed record CameraBuildStats(
        long FrameSweepSteps,
        long NoteEnterEvents,
        long NoteLeaveEvents,
        long ActiveStartEvents,
        long ActiveEndEvents,
        long PitchPointUpdates);

    internal CameraBuildStats? LastBuildStats { get; private set; }

    private sealed record FrameRange(long FromSample, double MinMidi, double MaxMidi, bool IsClipped, bool IsEmpty);

    public PitchCamera(
        PreparedNote[] mainNotes,
        int laneHeight,
        int sampleRate,
        double pastSeconds,
        double futureSeconds,
        long timelineStartSample,
        long timelineEndSample,
        int fpsNumerator = 60,
        int fpsDenominator = 1,
        bool allowExtendedSpan = false,
        double rollZoom = 1.0)
    {
        if (!double.IsFinite(rollZoom) || rollZoom <= 0)
            throw new ArgumentOutOfRangeException(nameof(rollZoom));
        _timelineStartSample = timelineStartSample;
        _timelineEndSample = timelineEndSample;
        _sampleRate = sampleRate;
        _fpsNumerator = fpsNumerator;
        _fpsDenominator = fpsDenominator;
        // Keep ordinary pitch lanes musically readable: 16–18 semitones is
        // the preferred window. Minimum and maximum spans are soft targets;
        // important content is allowed to expand the viewport.
        int preferredSpan = Math.Clamp(
            (int)Math.Round(laneHeight / (7.0 * rollZoom)),
            rollZoom > 1.0 ? MinSpan : 16,
            18);
        int maxSpan = allowExtendedSpan ? Fm3MaxSpan : MaxSpan;

        // The camera must count frames and map frame→sample exactly like
        // PanelOverlayRenderer's playhead (FrameSampleClock), otherwise its
        // baked anticipation windows drift against note contact — most
        // visibly at fractional frame rates such as 60000/1001.
        _totalFrames = Math.Max(1, checked((int)FrameSampleClock.FrameCount(
            Math.Max(0, timelineEndSample - timelineStartSample),
            sampleRate,
            fpsNumerator,
            fpsDenominator)));

        _frames = new FrameRange[_totalFrames];
        RawTarget[] targets = new RawTarget[_totalFrames];
        BuildTargets(mainNotes, targets, sampleRate, pastSeconds, futureSeconds, preferredSpan);

        ApplyDelayedContraction(targets, sampleRate);

        for (int i = 0; i < targets.Length; i++)
        {
            ref RawTarget t = ref targets[i];
            if (t.IsEmpty) continue;
            (int low, int high) = AlignToOctave(
                (int)Math.Round(t.MinMidi),
                (int)Math.Round(t.MaxMidi),
                preferredSpan,
                maxSpan);
            t.MinMidi = low; t.MaxMidi = high;
        }

        ApplyDampedInterpolation(targets, sampleRate);

        for (int i = 0; i < _totalFrames; i++)
        {
            RawTarget t = targets[i];
            _frames[i] = t.IsEmpty
                ? new FrameRange(t.FromSample, 0, 0, false, true)
                : new FrameRange(t.FromSample, t.MinMidi, t.MaxMidi, t.IsClipped, false);
        }
    }

    /// <summary>Deterministic pitch range [minMidi, maxMidi) for the given sample.</summary>
    public (int MinMidi, int MaxMidi) GetRange(long currentSample)
    {
        var f = _frames[SampleToFrame(currentSample)];
        return f.IsEmpty
            ? (DefaultMinMidi, DefaultMaxMidi)
            : ((int)Math.Round(f.MinMidi), (int)Math.Round(f.MaxMidi));
    }

    /// <summary>
    /// Returns the continuous camera range used by the renderer. The integer
    /// accessor remains for callers that need stable MIDI boundaries.
    /// </summary>
    public (double MinMidi, double MaxMidi) GetPreciseRange(long currentSample)
    {
        var f = _frames[SampleToFrame(currentSample)];
        return f.IsEmpty ? (DefaultMinMidi, DefaultMaxMidi) : (f.MinMidi, f.MaxMidi);
    }

    public PitchViewport GetViewport(long currentSample)
    {
        (double min, double max) = GetPreciseRange(currentSample);
        return new PitchViewport(min, max);
    }

    /// <summary>True when an ornament was deliberately excluded (§11.3), for edge indicators.</summary>
    public bool IsClipped(long currentSample)
        => _frames[SampleToFrame(currentSample)].IsClipped;

    internal int SampleToFrame(long currentSample)
    {
        if (currentSample <= _timelineStartSample) return 0;
        if (currentSample >= _timelineEndSample) return _totalFrames - 1;
        // Raw inverse of FrameSampleClock.SampleAtFrame: the exact decimal
        // divide lands within one frame of the answer (decimal rounding ties),
        // so the bounded correction walk against the baked (non-decreasing)
        // boundaries makes it exact.
        long relative = currentSample - _timelineStartSample;
        decimal guess = (decimal)relative * _fpsNumerator
            / ((decimal)_sampleRate * _fpsDenominator);
        int g = (int)Math.Clamp(
            (long)decimal.Round(guess, 0, MidpointRounding.AwayFromZero),
            0L,
            (long)_totalFrames - 1L);
        while (g > 0 && _frames[g].FromSample > currentSample) g--;
        while (g < _totalFrames - 1 && _frames[g + 1].FromSample <= currentSample) g++;
        return g;
    }

    private struct RawTarget(long fromSample)
    {
        public long FromSample = fromSample;
        public double MinMidi, MaxMidi;
        public double? ActiveMinMidi, ActiveMaxMidi;
        public bool IsEmpty = true, IsClipped;
    }

    /// <summary>Returns the excluded edge value if the sole edge contributor is a short ornament saving &lt; 2 st.</summary>
    private static bool TryExcludeOrnament(int edge, int without, bool onlyOrnament, int sentinel, out int result)
    {
        result = without;
        if (!onlyOrnament || without == sentinel) return false;
        int savings = Math.Abs(edge - without);
        return savings > 0 && savings < OrnamentClipThresholdSemitones;
    }

    private readonly record struct PointEvent(long Sample, int Note, int Value);

    /// <summary>
    /// Single chronological sweep producing the per-frame raw targets. Each
    /// eligible note enters (lookahead reveal), exits (window past its end),
    /// starts (playhead at its start) and ends (playhead at its end) exactly
    /// once, and each pitch point is applied exactly once — the multisets
    /// (<see cref="ValueCounts"/>) are updated incrementally. Min/Max queries
    /// walk from a cached pointer, bounded by the (small) MIDI value domain
    /// per frame.
    /// </summary>
    private void BuildTargets(
        PreparedNote[] notes,
        RawTarget[] targets,
        int sampleRate,
        double pastSeconds,
        double futureSeconds,
        int preferredSpan)
    {
        long ornamentSamples = (long)(OrnamentThresholdSeconds * sampleRate);
        long sweepSteps = 0;
        int noteCount = notes.Length;

        // Eligible notes: InitialMidiNote >= 0 and not pure-noise SSG.
        // §11.2: FM3 operator pitches are included.
        int[] eligible = new int[noteCount];
        int eligibleCount = 0;
        for (int i = 0; i < noteCount; i++)
        {
            PreparedNote note = notes[i];
            if (note.InitialMidiNote < 0) continue;
            if (note.Mode is VisualizationNoteMode.SsgNoise or VisualizationNoteMode.SsgEnvelopeNoise)
                continue;
            eligible[eligibleCount++] = i;
        }

        PreparedNote[] notesEligible = new PreparedNote[eligibleCount];
        int[] curMin = new int[eligibleCount];
        int[] curMax = new int[eligibleCount];
        bool[] isOrnament = new bool[eligibleCount];
        bool[] inWindow = new bool[eligibleCount];
        bool[] isActive = new bool[eligibleCount];
        int minDomain = 0;
        int maxDomain = 127;
        for (int e = 0; e < eligibleCount; e++)
        {
            PreparedNote note = notes[eligible[e]];
            notesEligible[e] = note;
            int anchor = (int)Math.Round(note.InitialMidiNote);
            curMin[e] = anchor;
            curMax[e] = anchor;
            isOrnament[e] = note.EndSample - note.StartSample < ornamentSamples;
            if (anchor < minDomain) minDomain = anchor;
            if (anchor > maxDomain) maxDomain = anchor;
        }

        // Pitch points of eligible notes (MidiNote >= 0; NaN passes, as in the
        // legacy comparison), in global SamplePosition order: each is applied
        // exactly once as the lookahead passes it, even if the owning note has
        // not entered the in-window set yet.
        int pointCount = 0;
        for (int e = 0; e < eligibleCount; e++)
        {
            PreparedPitchPoint[] notePoints = notesEligible[e].Pitch;
            for (int p = 0; p < notePoints.Length; p++)
                if (!(notePoints[p].MidiNote < 0)) pointCount++;
        }
        PointEvent[] pointEvents = new PointEvent[pointCount];
        int pointPos = 0;
        for (int e = 0; e < eligibleCount; e++)
        {
            PreparedPitchPoint[] notePoints = notesEligible[e].Pitch;
            for (int p = 0; p < notePoints.Length; p++)
            {
                double midi = notePoints[p].MidiNote;
                if (midi < 0) continue;
                pointEvents[pointPos++] = new PointEvent(
                    notePoints[p].SamplePosition,
                    e,
                    Math.Clamp((int)Math.Round(midi), 0, 127));
            }
        }
        Array.Sort(pointEvents, static (PointEvent a, PointEvent b) => a.Sample.CompareTo(b.Sample));

        int[] byStart = new int[eligibleCount];
        int[] byEnd = new int[eligibleCount];
        for (int e = 0; e < eligibleCount; e++)
        {
            byStart[e] = e;
            byEnd[e] = e;
        }
        Array.Sort(byStart, (int a, int b) => notesEligible[a].StartSample.CompareTo(notesEligible[b].StartSample));
        Array.Sort(byEnd, (int a, int b) => notesEligible[a].EndSample.CompareTo(notesEligible[b].EndSample));

        // Bounded MIDI value multisets. The dense path is an offset-indexed
        // count array over [minDomain, maxDomain]; a corrupted anchor wider
        // than MaxDenseDomain falls back to a key table over exactly the
        // values that can be inserted.
        int[] sharedKeys = null;
        if ((long)maxDomain - (long)minDomain > MaxDenseDomain)
        {
            int[] candidates = new int[2 * eligibleCount + pointCount];
            int candidateCount = 0;
            for (int e = 0; e < eligibleCount; e++) candidates[candidateCount++] = curMin[e] > curMax[e] ? curMax[e] : curMin[e];
            for (int e = 0; e < eligibleCount; e++) candidates[candidateCount++] = curMax[e];
            for (int p = 0; p < pointCount; p++) candidates[candidateCount++] = pointEvents[p].Value;
            Array.Sort(candidates, 0, candidateCount);
            int distinct = 0;
            for (int p = 0; p < candidateCount; p++)
                if (p == 0 || candidates[p] != candidates[p - 1])
                    candidates[distinct++] = candidates[p];
            sharedKeys = candidates.AsSpan(0, distinct).ToArray();
        }
        var windowMin = ValueCounts.Create(minDomain, maxDomain, sharedKeys);
        var windowMax = ValueCounts.Create(minDomain, maxDomain, sharedKeys);
        var windowMinOrnament = ValueCounts.Create(minDomain, maxDomain, sharedKeys);
        var windowMaxOrnament = ValueCounts.Create(minDomain, maxDomain, sharedKeys);
        var activeMin = ValueCounts.Create(minDomain, maxDomain, sharedKeys);
        var activeMax = ValueCounts.Create(minDomain, maxDomain, sharedKeys);

        int entryCursor = 0;
        int exitCursor = 0;
        int activeEndCursor = 0;
        int pointCursor = 0;
        int[] deferredActive = new int[eligibleCount];
        int deferredCount = 0;
        int deferredCursor = 0;
        long noteEnters = 0, noteLeaves = 0, activeStarts = 0, activeEnds = 0, pitchPointUpdates = 0;

        for (int frame = 0; frame < targets.Length; frame++)
        {
            sweepSteps++;
            long currentSample = Math.Min(
                _timelineEndSample,
                _timelineStartSample + FrameSampleClock.SampleAtFrame(
                    frame, _sampleRate, _fpsNumerator, _fpsDenominator));
            long windowStart = currentSample - (long)(pastSeconds * sampleRate);
            long lookaheadEnd = currentSample + (long)(futureSeconds * sampleRate)
                + (long)(AdditionalLookaheadSeconds * sampleRate);

            // 1. Pitch points revealed by the lookahead update the owning
            // note's range and the multiset it currently belongs to.
            while (pointCursor < pointCount && pointEvents[pointCursor].Sample <= lookaheadEnd)
            {
                PointEvent pe = pointEvents[pointCursor++];
                pitchPointUpdates++;
                int e = pe.Note;
                int oldMin = curMin[e];
                int oldMax = curMax[e];
                int newMin = Math.Min(oldMin, pe.Value);
                int newMax = Math.Max(oldMax, pe.Value);
                if (newMin == oldMin && newMax == oldMax) continue;
                if (inWindow[e])
                {
                    windowMin.Remove(oldMin);
                    windowMax.Remove(oldMax);
                    windowMin.Add(newMin);
                    windowMax.Add(newMax);
                    if (isOrnament[e])
                    {
                        windowMinOrnament.Remove(oldMin);
                        windowMaxOrnament.Remove(oldMax);
                        windowMinOrnament.Add(newMin);
                        windowMaxOrnament.Add(newMax);
                    }
                }
                if (isActive[e])
                {
                    activeMin.Remove(oldMin);
                    activeMax.Remove(oldMax);
                    activeMin.Add(newMin);
                    activeMax.Add(newMax);
                }
                curMin[e] = newMin;
                curMax[e] = newMax;
            }

            // 2. Note exit: the window has moved past the note's end.
            while (exitCursor < eligibleCount && notesEligible[byEnd[exitCursor]].EndSample <= windowStart)
            {
                int e = byEnd[exitCursor++];
                if (!inWindow[e]) continue;
                inWindow[e] = false;
                noteLeaves++;
                windowMin.Remove(curMin[e]);
                windowMax.Remove(curMax[e]);
                if (isOrnament[e])
                {
                    windowMinOrnament.Remove(curMin[e]);
                    windowMaxOrnament.Remove(curMax[e]);
                }
                if (isActive[e])
                {
                    isActive[e] = false;
                    activeEnds++;
                    activeMin.Remove(curMin[e]);
                    activeMax.Remove(curMax[e]);
                }
            }

            // 3. Note entry: the lookahead has revealed the note.
            while (entryCursor < eligibleCount && notesEligible[byStart[entryCursor]].StartSample <= lookaheadEnd)
            {
                int e = byStart[entryCursor++];
                if (notesEligible[e].EndSample <= windowStart) continue; // past note: never in window
                inWindow[e] = true;
                noteEnters++;
                windowMin.Add(curMin[e]);
                windowMax.Add(curMax[e]);
                if (isOrnament[e])
                {
                    windowMinOrnament.Add(curMin[e]);
                    windowMaxOrnament.Add(curMax[e]);
                }
                if (currentSample >= notesEligible[e].StartSample && currentSample < notesEligible[e].EndSample)
                {
                    isActive[e] = true;
                    activeStarts++;
                    activeMin.Add(curMin[e]);
                    activeMax.Add(curMax[e]);
                }
                else
                    deferredActive[deferredCount++] = e;
            }

            // 4. Deferred active: the playhead has just passed a note's start
            // while the note is still in the window.
            while (deferredCursor < deferredCount
                && notesEligible[deferredActive[deferredCursor]].StartSample <= currentSample)
            {
                int e = deferredActive[deferredCursor++];
                if (!inWindow[e]) continue;
                if (currentSample >= notesEligible[e].EndSample) continue;
                isActive[e] = true;
                activeStarts++;
                activeMin.Add(curMin[e]);
                activeMax.Add(curMax[e]);
            }

            // 5. Active end: the playhead has just passed the note's end.
            while (activeEndCursor < eligibleCount
                && notesEligible[byEnd[activeEndCursor]].EndSample <= currentSample)
            {
                int e = byEnd[activeEndCursor++];
                if (!isActive[e]) continue;
                isActive[e] = false;
                activeEnds++;
                activeMin.Remove(curMin[e]);
                activeMax.Remove(curMax[e]);
            }

            // 6. Emit the frame's raw target from the aggregates.
            if (windowMin.Total == 0)
            {
                targets[frame] = new RawTarget(currentSample);
                continue;
            }
            int fullMin = windowMin.Min();
            int fullMax = windowMax.Max();
            int minMidi = fullMin;
            int maxMidi = fullMax;
            bool clipped = false;
            if (fullMax - fullMin > preferredSpan)
            {
                int highWithout = windowMax.MaxBelow(fullMax);
                int lowWithout = windowMin.MinAbove(fullMin);
                bool highOnlyOrnament = windowMaxOrnament.CountAt(fullMax) == windowMax.CountAt(fullMax);
                bool lowOnlyOrnament = windowMinOrnament.CountAt(fullMin) == windowMin.CountAt(fullMin);
                if (TryExcludeOrnament(fullMax, highWithout, highOnlyOrnament, int.MinValue, out int newMax))
                { maxMidi = newMax; clipped = true; }
                if (TryExcludeOrnament(lowWithout, fullMin, lowOnlyOrnament, int.MaxValue, out int newMin))
                { minMidi = newMin; clipped = true; }
            }
            targets[frame] = new RawTarget(currentSample)
            {
                MinMidi = minMidi,
                MaxMidi = maxMidi,
                ActiveMinMidi = activeMin.Total > 0 ? (double)activeMin.Min() : null,
                ActiveMaxMidi = activeMax.Total > 0 ? (double)activeMax.Max() : null,
                IsEmpty = false,
                IsClipped = clipped,
            };
        }

        LastBuildStats = new CameraBuildStats(
            sweepSteps, noteEnters, noteLeaves, activeStarts, activeEnds, pitchPointUpdates);
    }

    /// <summary>§11.3: expand immediately; contract only after a 0.75 s hold.</summary>
    private static void ApplyDelayedContraction(RawTarget[] targets, int sampleRate)
    {
        long holdSamples = (long)(ContractionHoldSeconds * sampleRate);
        int? heldMin = null, heldMax = null;
        long lastExpansionSample = -1;

        for (int i = 0; i < targets.Length; i++)
        {
            ref RawTarget t = ref targets[i];
            if (t.IsEmpty)
            {
                heldMin = null; heldMax = null; lastExpansionSample = -1;
                continue;
            }

            int targetMin = (int)Math.Round(t.MinMidi), targetMax = (int)Math.Round(t.MaxMidi);
            if (!heldMin.HasValue)
            {
                heldMin = targetMin; heldMax = targetMax;
                lastExpansionSample = t.FromSample;
            }
            else
            {
                bool expands = targetMin < heldMin.Value || targetMax > heldMax.Value;
                bool withinHold = t.FromSample - lastExpansionSample < holdSamples;
                if (expands || withinHold)
                {
                    // Expand immediately; during hold only widen (never narrow).
                    heldMin = Math.Min(heldMin.Value, targetMin);
                    heldMax = Math.Max(heldMax.Value, targetMax);
                }
                else
                {
                    heldMin = targetMin; heldMax = targetMax;
                    lastExpansionSample = t.FromSample;
                }
                if (expands) lastExpansionSample = t.FromSample;
            }
            t.MinMidi = heldMin.Value; t.MaxMidi = heldMax.Value;
        }
    }

    /// <summary>
    /// §11.4: prefer boundaries near C, but only inside the containment
    /// interval — alignment never pushes a note out of the half-open window.
    /// </summary>
    private static (int Low, int High) AlignToOctave(int minMidi, int maxMidi, int preferredSpan, int maxSpan)
    {
        // A half-open [low, high) window needs range+1 rows to contain both
        // endpoints; sizing from range alone dropped the top boundary pitch.
        int required = maxMidi - minMidi + 1;
        // The preferred/max spans are readability targets, not permission to
        // hide an important sounding pitch. A bend or simultaneous note that
        // exceeds the preferred range expands the viewport for this target;
        // contraction can restore the readable span after the hold interval.
        int span = Math.Max(preferredSpan, required);

        if (required > span)
        {
            // Content cannot fit under the hard maximum: anchor to the top so
            // the extreme boundary pitch itself stays visible.
            return (maxMidi - span + 1, maxMidi + 1);
        }

        // Any low inside [lowMin, lowMax] keeps every note visible. The
        // centered position is the default, with the odd spare semitone going
        // below the content so the lowest voice never hugs the bottom edge.
        // A C-aligned boundary replaces it only within three semitones.
        int lowMin = maxMidi - span + 1;
        int lowMax = minMidi;
        int idealLow = Math.Clamp(minMidi - (span - required + 1) / 2, lowMin, lowMax);

        int best = idealLow;
        int bestDistance = 4;
        for (int candidate = lowMin; candidate <= lowMax; candidate++)
        {
            bool lowIsC = Mod(candidate, 12) == 0;
            bool highIsC = Mod(candidate + span, 12) == 0;
            if (!lowIsC && !highIsC)
                continue;
            int distance = Math.Abs(candidate - idealLow);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }
        return (best, best + span);
    }

    private static int Mod(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    /// <summary>
    /// §11.5: analytical critically-damped interpolation in both directions.
    /// The camera keeps continuous position and velocity state; integer MIDI
    /// values are produced only by the compatibility accessor above.
    /// </summary>
    private void ApplyDampedInterpolation(RawTarget[] targets, int sampleRate)
    {
        if (targets.Length <= 1) return;
        double durationSeconds = Math.Max(1, _timelineEndSample - _timelineStartSample) / (double)sampleRate;
        double frameSeconds = durationSeconds / Math.Max(1, _totalFrames - 1);
        double angularFrequency = 1.0 / DampingTimeConstantSeconds;

        double curMin = targets[0].IsEmpty ? DefaultMinMidi : targets[0].MinMidi;
        double curMax = targets[0].IsEmpty ? DefaultMaxMidi : targets[0].MaxMidi;
        double minVelocity = 0;
        double maxVelocity = 0;
        for (int i = 1; i < targets.Length; i++)
        {
            ref RawTarget t = ref targets[i];
            double targetMin = t.IsEmpty ? DefaultMinMidi : t.MinMidi;
            double targetMax = t.IsEmpty ? DefaultMaxMidi : t.MaxMidi;

            StepCriticallyDamped(
                ref curMin,
                ref minVelocity,
                targetMin,
                frameSeconds,
                angularFrequency);
            StepCriticallyDamped(
                ref curMax,
                ref maxVelocity,
                targetMax,
                frameSeconds,
                angularFrequency);

            // Lookahead expansion remains damped for visual continuity. Once
            // a note is actually sounding, the active-content guard prevents
            // the camera from clipping its pitch even if damping has not yet
            // reached the wider future target.
            if (t.ActiveMinMidi is double activeMin
                && t.ActiveMaxMidi is double activeMax)
            {
                if (curMin > activeMin)
                    curMin = activeMin;
                if (curMax < activeMax)
                    curMax = activeMax;
            }

            if (curMax - curMin < MinSpan)
            {
                double center = (curMin + curMax) / 2;
                curMin = center - MinSpan / 2;
                curMax = curMin + MinSpan;
            }
            t.MinMidi = curMin; t.MaxMidi = curMax;
        }
    }

    private static void StepCriticallyDamped(
        ref double position,
        ref double velocity,
        double target,
        double deltaSeconds,
        double angularFrequency)
    {
        double offset = position - target;
        double decay = Math.Exp(-angularFrequency * deltaSeconds);
        double nextOffset = (offset + (velocity + angularFrequency * offset) * deltaSeconds) * decay;
        double nextVelocity = (velocity - angularFrequency
            * (velocity + angularFrequency * offset) * deltaSeconds) * decay;
        position = target + nextOffset;
        velocity = nextVelocity;
    }

    /// <summary>
    /// Bounded multiset of MIDI values with cached Min/Max pointers. The
    /// dense path is an offset-indexed count array over [minDomain,
    /// maxDomain]; a pathological domain (corrupted anchor wider than
    /// <see cref="MaxDenseDomain"/>) falls back to a dictionary-backed key
    /// table over exactly the values that can be inserted. Min/Max queries
    /// walk from the cached pointer — amortized O(1) (the pointer only
    /// oscillates near the answer), worst case one full domain walk per
    /// query; the domain is a few hundred values wide in practice.
    /// </summary>
    private sealed class ValueCounts
    {
        private readonly bool _dense;
        private readonly int _offset;
        private readonly int[] _counts;
        private readonly int[] _keys;
        private readonly int[] _keyCounts;
        private readonly Dictionary<int, int> _keyIndex;
        private int _total;
        private int _minPtr;
        private int _maxPtr;
        private bool _minValid;
        private bool _maxValid;

        private ValueCounts(int minDomain, int maxDomain)
        {
            _dense = true;
            _offset = minDomain;
            _counts = new int[(int)((long)maxDomain - minDomain) + 1];
        }

        private ValueCounts(int[] keys)
        {
            _keys = keys;
            _keyCounts = new int[keys.Length];
            _keyIndex = new Dictionary<int, int>(keys.Length);
            for (int i = 0; i < keys.Length; i++)
                _keyIndex[keys[i]] = i;
        }

        public static ValueCounts Create(int minDomain, int maxDomain, int[] sharedKeys)
            => sharedKeys is null
                ? new ValueCounts(minDomain, maxDomain)
                : new ValueCounts(sharedKeys);

        /// <summary>Total number of elements (with multiplicity).</summary>
        public int Total => _total;

        public void Add(int value)
        {
            int idx = _dense ? value - _offset : _keyIndex[value];
            if (_dense) _counts[idx]++;
            else _keyCounts[idx]++;
            // Only move pointers toward the new element. An invalid pointer is
            // left in place: its count hit zero, so everything on that side is
            // known empty and Min()/Max() must re-walk from here anyway.
            // Re-seating an invalid pointer here would hide surviving elements
            // between the stale position and the new one.
            if (idx < _minPtr) _minPtr = idx;
            if (idx > _maxPtr) _maxPtr = idx;
            _total++;
        }

        public void Remove(int value)
        {
            int idx = _dense ? value - _offset : _keyIndex[value];
            if (_dense) _counts[idx]--;
            else _keyCounts[idx]--;
            if (idx == _minPtr) _minValid = false;
            if (idx == _maxPtr) _maxValid = false;
            _total--;
            if (_total == 0)
            {
                _minValid = false;
                _maxValid = false;
            }
        }

        /// <summary>Count of exactly <paramref name="value"/> in the multiset.</summary>
        public int CountAt(int value)
            => _dense ? _counts[value - _offset] : _keyCounts[_keyIndex[value]];

        /// <summary>Smallest value with a positive count. Assumes <see cref="Total"/> &gt; 0.</summary>
        public int Min()
        {
            if (!_minValid)
            {
                if (_dense)
                {
                    for (int i = _minPtr; i < _counts.Length; i++)
                    {
                        if (_counts[i] > 0) { _minPtr = i; _minValid = true; break; }
                    }
                }
                else
                {
                    for (int i = _minPtr; i < _keys.Length; i++)
                    {
                        if (_keyCounts[i] > 0) { _minPtr = i; _minValid = true; break; }
                    }
                }
            }
            return _dense ? _minPtr + _offset : _keys[_minPtr];
        }

        /// <summary>Largest value with a positive count. Assumes <see cref="Total"/> &gt; 0.</summary>
        public int Max()
        {
            if (!_maxValid)
            {
                if (_dense)
                {
                    for (int i = _maxPtr; i >= 0; i--)
                    {
                        if (_counts[i] > 0) { _maxPtr = i; _maxValid = true; break; }
                    }
                }
                else
                {
                    for (int i = _maxPtr; i >= 0; i--)
                    {
                        if (_keyCounts[i] > 0) { _maxPtr = i; _maxValid = true; break; }
                    }
                }
            }
            return _dense ? _maxPtr + _offset : _keys[_maxPtr];
        }

        /// <summary>
        /// Smallest value strictly greater than <paramref name="lower"/> with
        /// a positive count, or <see cref="int.MaxValue"/> when none.
        /// </summary>
        public int MinAbove(int lower)
        {
            if (_dense)
            {
                if (lower == int.MaxValue) return int.MaxValue;
                int denseIdx = lower + 1 - _offset;
                if (denseIdx < 0) return Min();
                for (int i = denseIdx; i < _counts.Length; i++)
                {
                    if (_counts[i] > 0) return i + _offset;
                }
                return int.MaxValue;
            }
            int idx = UpperBound(_keys, lower);
            for (int i = idx; i < _keys.Length; i++)
            {
                if (_keyCounts[i] > 0) return _keys[i];
            }
            return int.MaxValue;
        }

        /// <summary>
        /// Largest value strictly less than <paramref name="upper"/> with a
        /// positive count, or <see cref="int.MinValue"/> when none.
        /// </summary>
        public int MaxBelow(int upper)
        {
            if (_dense)
            {
                if (upper == int.MinValue) return int.MinValue;
                int denseIdx = upper - 1 - _offset;
                if (denseIdx >= _counts.Length) return Max();
                for (int i = denseIdx; i >= 0; i--)
                {
                    if (_counts[i] > 0) return i + _offset;
                }
                return int.MinValue;
            }
            int idx = LowerBound(_keys, upper) - 1;
            for (int i = idx; i >= 0; i--)
            {
                if (_keyCounts[i] > 0) return _keys[i];
            }
            return int.MinValue;
        }

        private static int UpperBound(int[] keys, int value)
        {
            int lo = 0, hi = keys.Length;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (keys[mid] <= value) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        private static int LowerBound(int[] keys, int value)
        {
            int lo = 0, hi = keys.Length;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (keys[mid] < value) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }
    }
}
