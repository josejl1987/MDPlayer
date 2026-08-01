using Fmp.Core.Visualization;

namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Precomputed, deterministic pitch-range camera (§11). Expands immediately when
/// a note would leave the range; contracts after a 0.75 s hold (§11.3). Short
/// ornaments are not zoomed for (§11.3). Boundaries prefer C (§11.4). The
/// final range is critically-damped between keyframes (§11.5). All state is
/// baked at construction — GetRange/IsClipped are pure lookups.
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

    private readonly FrameRange[] _frames;
    private readonly long _timelineStartSample;
    private readonly long _timelineEndSample;
    private readonly int _totalFrames;

    private sealed record FrameRange(long FromSample, int MinMidi, int MaxMidi, bool IsClipped, bool IsEmpty);

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
        bool allowExtendedSpan = false)
    {
        _timelineStartSample = timelineStartSample;
        _timelineEndSample = timelineEndSample;
        // Keep ordinary pitch lanes musically readable: 16–18 semitones is
        // the preferred window, while MinSpan/MaxSpan remain hard bounds.
        int preferredSpan = Math.Clamp((int)Math.Round(laneHeight / 7.0), 16, 18);
        int maxSpan = allowExtendedSpan ? Fm3MaxSpan : MaxSpan;

        double totalSeconds = Math.Max(0, (timelineEndSample - timelineStartSample) / (double)sampleRate);
        double fps = fpsDenominator > 0 ? (double)fpsNumerator / fpsDenominator : 60.0;
        _totalFrames = Math.Max(1, (int)Math.Ceiling(totalSeconds * fps));

        _frames = new FrameRange[_totalFrames];
        RawTarget[] targets = new RawTarget[_totalFrames];
        for (int frame = 0; frame < _totalFrames; frame++)
        {
            long currentSample = FrameToSample(frame, timelineStartSample, timelineEndSample);
            targets[frame] = PrecomputeTarget(
                mainNotes, currentSample, sampleRate, pastSeconds, futureSeconds, preferredSpan);
        }

        ApplyDelayedContraction(targets, sampleRate);

        for (int i = 0; i < targets.Length; i++)
        {
            ref RawTarget t = ref targets[i];
            if (t.IsEmpty) continue;
            (int low, int high) = AlignToOctave(t.MinMidi, t.MaxMidi, preferredSpan, maxSpan);
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
        return f.IsEmpty ? (48, 72) : (f.MinMidi, f.MaxMidi);
    }

    /// <summary>True when an ornament was deliberately excluded (§11.3), for edge indicators.</summary>
    public bool IsClipped(long currentSample)
        => _frames[SampleToFrame(currentSample)].IsClipped;

    private long FrameToSample(int frame, long startSample, long endSample)
    {
        if (frame <= 0) return startSample;
        if (frame >= _totalFrames - 1) return endSample;
        return startSample + (long)((frame * (double)(endSample - startSample)) / (_totalFrames - 1));
    }

    private int SampleToFrame(long currentSample)
    {
        if (currentSample <= _timelineStartSample) return 0;
        if (currentSample >= _timelineEndSample) return _totalFrames - 1;
        int lo = 0, hi = _totalFrames - 1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo + 1) / 2;
            if (_frames[mid].FromSample <= currentSample) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    private struct RawTarget(long fromSample)
    {
        public long FromSample = fromSample;
        public int MinMidi, MaxMidi;
        public bool IsEmpty = true, IsClipped;
    }

    private readonly record struct NoteExtent(int Min, int Max, long Duration);

    /// <summary>Returns the excluded edge value if the sole edge contributor is a short ornament saving &lt; 2 st.</summary>
    private static bool TryExcludeOrnament(int edge, int without, bool onlyOrnament, int sentinel, out int result)
    {
        result = without;
        if (!onlyOrnament || without == sentinel) return false;
        int savings = Math.Abs(edge - without);
        return savings > 0 && savings < OrnamentClipThresholdSemitones;
    }

    private static RawTarget PrecomputeTarget(
        PreparedNote[] notes, long currentSample, int sampleRate,
        double pastSeconds, double futureSeconds, int preferredSpan)
    {
        long windowStart = currentSample - (long)(pastSeconds * sampleRate);
        long windowEnd = currentSample + (long)(futureSeconds * sampleRate);
        long lookaheadEnd = windowEnd + (long)(AdditionalLookaheadSeconds * sampleRate);
        long ornamentSamples = (long)(OrnamentThresholdSeconds * sampleRate);

        int noteCount = 0;
        Span<NoteExtent> extents = notes.Length <= 128
            ? stackalloc NoteExtent[notes.Length]
            : new NoteExtent[notes.Length];

        foreach (PreparedNote note in notes)
        {
            if (note.InitialMidiNote < 0) continue;
            // §11.2: FM3 operator pitches included; only pure-noise SSG excluded.
            if (note.Mode is VisualizationNoteMode.SsgNoise or VisualizationNoteMode.SsgEnvelopeNoise)
                continue;
            if (note.EndSample <= windowStart || note.StartSample > lookaheadEnd)
                continue;

            int anchorMidi = (int)Math.Round(note.InitialMidiNote);
            int noteMin = anchorMidi, noteMax = anchorMidi;
            foreach (PreparedPitchPoint pc in note.Pitch)
            {
                if (pc.MidiNote < 0 || pc.SamplePosition > lookaheadEnd) continue;
                // Keep the actual prepared contour in the camera extent. The
                // range solver applies the hard 24/30-semitone limit; clipping
                // an intentional ornament is then surfaced by IsClipped so
                // the renderer can draw an edge indicator.
                int pitchMidi = Math.Clamp((int)Math.Round(pc.MidiNote), 0, 127);
                if (pitchMidi < noteMin) noteMin = pitchMidi;
                if (pitchMidi > noteMax) noteMax = pitchMidi;
            }
            extents[noteCount++] = new NoteExtent(noteMin, noteMax, note.EndSample - note.StartSample);
        }

        if (noteCount == 0)
            return new RawTarget(currentSample);

        int fullMin = int.MaxValue, fullMax = int.MinValue;
        for (int i = 0; i < noteCount; i++)
        {
            if (extents[i].Min < fullMin) fullMin = extents[i].Min;
            if (extents[i].Max > fullMax) fullMax = extents[i].Max;
        }

        // §11.3 ornament rule: when the full span exceeds preferred, a short
        // ornament (< 80 ms) at an edge contributing < 2 st is excluded.
        int minMidi = fullMin, maxMidi = fullMax;
        bool clipped = false;
        if (fullMax - fullMin > preferredSpan)
        {
            int highWithout = int.MinValue, lowWithout = int.MaxValue;
            bool highOnlyOrnament = true, lowOnlyOrnament = true;
            for (int i = 0; i < noteCount; i++)
            {
                bool isOrnament = extents[i].Duration < ornamentSamples;
                if (extents[i].Max < fullMax) highWithout = Math.Max(highWithout, extents[i].Max);
                else if (!isOrnament) highOnlyOrnament = false;
                if (extents[i].Min > fullMin) lowWithout = Math.Min(lowWithout, extents[i].Min);
                else if (!isOrnament) lowOnlyOrnament = false;
            }
            if (TryExcludeOrnament(fullMax, highWithout, highOnlyOrnament, int.MinValue, out int newMax))
            { maxMidi = newMax; clipped = true; }
            if (TryExcludeOrnament(lowWithout, fullMin, lowOnlyOrnament, int.MaxValue, out int newMin))
            { minMidi = newMin; clipped = true; }
        }

        return new RawTarget(currentSample) { MinMidi = minMidi, MaxMidi = maxMidi, IsEmpty = false, IsClipped = clipped };
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

            int targetMin = t.MinMidi, targetMax = t.MaxMidi;
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

    /// <summary>§11.4: prefer boundaries near C when it keeps notes well-centered.</summary>
    private static (int Low, int High) AlignToOctave(int minMidi, int maxMidi, int preferredSpan, int maxSpan)
    {
        int span = Math.Clamp(Math.Max(preferredSpan, maxMidi - minMidi), MinSpan, maxSpan);
        int range = maxMidi - minMidi;

        // The range must contain all important pitches whenever it fits under
        // the hard maximum. C-aligned low or high boundaries are preferred
        // only when they move the otherwise centered window by at most three
        // semitones.
        int validLow = Math.Min(minMidi, maxMidi - span);
        int validHigh = Math.Max(minMidi, maxMidi - span);
        int idealLow = range <= span
            ? minMidi - (span - range) / 2
            : maxMidi - span;
        idealLow = Math.Clamp(idealLow, validLow, validHigh);

        int best = idealLow;
        int bestDistance = 4;
        for (int candidate = validLow - 12; candidate <= validHigh + 12; candidate++)
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

    /// <summary>§11.5: critically-damped interpolation. Expansion snaps; contraction eases.</summary>
    private void ApplyDampedInterpolation(RawTarget[] targets, int sampleRate)
    {
        if (targets.Length <= 1) return;
        double durationSeconds = Math.Max(1, _timelineEndSample - _timelineStartSample) / (double)sampleRate;
        double frameSeconds = durationSeconds / _totalFrames;
        double alpha = 1.0 - Math.Exp(-frameSeconds / DampingTimeConstantSeconds);

        int curMin = targets[0].MinMidi, curMax = targets[0].MaxMidi;
        for (int i = 1; i < targets.Length; i++)
        {
            ref RawTarget t = ref targets[i];
            if (t.IsEmpty) { curMin = 0; curMax = 0; continue; }

            int targetMin = t.MinMidi, targetMax = t.MaxMidi;
            // Expand immediately (never hide a note); contract with damping.
            if (targetMin < curMin) curMin = targetMin;
            else if (targetMin > curMin)
                curMin = (int)Math.Round(curMin + (targetMin - curMin) * alpha);
            if (targetMax > curMax) curMax = targetMax;
            else if (targetMax < curMax)
                curMax = (int)Math.Round(curMax + (targetMax - curMax) * alpha);

            if (curMax - curMin < MinSpan)
            {
                int center = (curMin + curMax) / 2;
                curMin = center - MinSpan / 2;
                curMax = curMin + MinSpan;
            }
            t.MinMidi = curMin; t.MaxMidi = curMax;
        }
    }
}
