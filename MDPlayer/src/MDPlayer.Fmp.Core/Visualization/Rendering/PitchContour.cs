namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Deterministic evaluation of a prepared note's pitch contour (Visualization
/// 2.0 §8). Between adjacent pitch points the pitch is linearly interpolated;
/// clearly discontinuous transitions — a fast, large change on a retriggered
/// note (§8.3) — are preserved as steps. Before the first pitch point the
/// contour bends from the note's initial pitch; after the last point it holds
/// the final pitch to the note end. No state, no allocations, no randomness:
/// the same inputs always produce the same output.
/// </summary>
internal static class PitchContour
{
    /// <summary>
    /// Returns the actual MIDI pitch at <paramref name="sample"/> for
    /// <paramref name="note"/>, linearly interpolating the prepared pitch
    /// points. <paramref name="samplesPerFrame"/> is the output frame duration
    /// in samples, used by the step-change rule.
    /// </summary>
    public static double PitchAtSample(PreparedNote note, long sample, double samplesPerFrame)
    {
        PreparedPitchPoint[] pitch = note.Pitch;
        if (pitch.Length == 0)
            return note.InitialMidiNote;

        if (sample <= note.StartSample)
            return pitch[0].SamplePosition <= note.StartSample
                ? pitch[0].MidiNote
                : note.InitialMidiNote;

        if (sample >= pitch[^1].SamplePosition)
            return pitch[^1].MidiNote;

        // Rightmost point at or before the sample (binary search into the
        // pre-sorted array — permitted on the hot path).
        int low = 0;
        int high = pitch.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (pitch[middle].SamplePosition < sample)
                low = middle + 1;
            else
                high = middle;
        }

        int index = low - 1;
        if (index < 0)
        {
            // Before the first point: bend from the note's initial pitch.
            long firstSample = pitch[0].SamplePosition;
            if (firstSample <= note.StartSample)
                return pitch[0].MidiNote;
            if (IsStep(note.IsRetrigger, firstSample - note.StartSample,
                pitch[0].MidiNote - note.InitialMidiNote, samplesPerFrame))
                return note.InitialMidiNote;
            double fraction = (sample - note.StartSample) / (double)(firstSample - note.StartSample);
            return note.InitialMidiNote
                + (pitch[0].MidiNote - note.InitialMidiNote) * Math.Clamp(fraction, 0, 1);
        }

        long interval = pitch[index + 1].SamplePosition - pitch[index].SamplePosition;
        if (interval <= 0)
            return pitch[index].MidiNote;

        if (IsStep(note.IsRetrigger, interval,
            pitch[index + 1].MidiNote - pitch[index].MidiNote, samplesPerFrame))
            return pitch[index].MidiNote;

        double t = (sample - pitch[index].SamplePosition) / (double)interval;
        return pitch[index].MidiNote + (pitch[index + 1].MidiNote - pitch[index].MidiNote) * t;
    }

    /// <summary>
    /// Monotonic rasterizer companion to <see cref="PitchAtSample"/>. The
    /// caller keeps <paramref name="segmentIndex"/> between increasing sample
    /// positions, turning the per-column lookup into one forward cursor.
    /// </summary>
    public static double PitchAtSampleMonotonic(
        PreparedNote note,
        long sample,
        double samplesPerFrame,
        ref int segmentIndex)
    {
        PreparedPitchPoint[] pitch = note.Pitch;
        if (pitch.Length == 0)
            return note.InitialMidiNote;
        if (sample <= note.StartSample)
        {
            segmentIndex = -1;
            return pitch[0].SamplePosition <= note.StartSample
                ? pitch[0].MidiNote
                : note.InitialMidiNote;
        }

        while (segmentIndex + 1 < pitch.Length
            && pitch[segmentIndex + 1].SamplePosition <= sample)
            segmentIndex++;

        if (segmentIndex < 0)
        {
            long firstSample = pitch[0].SamplePosition;
            if (firstSample <= note.StartSample)
                return pitch[0].MidiNote;
            if (IsStep(note.IsRetrigger, firstSample - note.StartSample,
                pitch[0].MidiNote - note.InitialMidiNote, samplesPerFrame))
                return note.InitialMidiNote;
            double fraction = (sample - note.StartSample) / (double)(firstSample - note.StartSample);
            return note.InitialMidiNote
                + (pitch[0].MidiNote - note.InitialMidiNote) * Math.Clamp(fraction, 0, 1);
        }

        if (segmentIndex >= pitch.Length - 1)
            return pitch[^1].MidiNote;

        PreparedPitchPoint from = pitch[segmentIndex];
        PreparedPitchPoint to = pitch[segmentIndex + 1];
        long interval = to.SamplePosition - from.SamplePosition;
        if (interval <= 0 || IsStep(note.IsRetrigger, interval, to.MidiNote - from.MidiNote, samplesPerFrame))
            return from.MidiNote;
        double t = (sample - from.SamplePosition) / (double)interval;
        return from.MidiNote + (to.MidiNote - from.MidiNote) * t;
    }

    /// <summary>
    /// A transition is a preserved step when the note is a retrigger, the
    /// change happens within one output frame, and the jump is at least
    /// 0.75 semitones (§8.3). Everything else interpolates continuously.
    /// </summary>
    private static bool IsStep(bool isRetrigger, long intervalSamples, double pitchChange, double samplesPerFrame)
        => isRetrigger
            && intervalSamples <= samplesPerFrame
            && Math.Abs(pitchChange) >= 0.75;
}
