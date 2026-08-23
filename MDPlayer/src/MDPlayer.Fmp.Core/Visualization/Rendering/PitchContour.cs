namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Deterministic zero-order-hold evaluation of a prepared pitch contour.
/// Chip-register writes and MIDI messages change state at their timestamp;
/// they do not describe a ramp leading up to the next write. The initial pitch
/// is held before the first point and each point is held until the following
/// point. No state, allocations, or invented intermediate pitch values.
/// </summary>
internal static class PitchContour
{
    /// <summary>
    /// Returns the pitch state in force at <paramref name="sample"/>.
    /// <paramref name="samplesPerFrame"/> remains in the signature for source
    /// compatibility; pitch semantics are independent of output frame rate.
    /// </summary>
    public static double PitchAtSample(PreparedNote note, long sample, double samplesPerFrame)
        => PitchAtSample(note.InitialMidiNote, note.Pitch, sample);

    internal static double PitchAtSample(
        double initialMidiNote,
        PreparedPitchPoint[] pitch,
        long sample)
    {
        if (pitch.Length == 0)
            return initialMidiNote;

        // Upper-bound search: first point strictly after the sample.
        int low = 0;
        int high = pitch.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (pitch[middle].SamplePosition <= sample)
                low = middle + 1;
            else
                high = middle;
        }

        return low == 0 ? initialMidiNote : pitch[low - 1].MidiNote;
    }

    /// <summary>
    /// Monotonic companion to <see cref="PitchAtSample"/>. The caller keeps the
    /// index of the last point at or before the previous sample.
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

        while (segmentIndex + 1 < pitch.Length
            && pitch[segmentIndex + 1].SamplePosition <= sample)
            segmentIndex++;

        return segmentIndex < 0
            ? note.InitialMidiNote
            : pitch[Math.Min(segmentIndex, pitch.Length - 1)].MidiNote;
    }

    /// <summary>
    /// Linear interpolation between pitch points. Holds <see cref="PreparedNote.InitialMidiNote"/>
    /// before the first point, then linearly interpolates between successive points.
    /// This smooths vibrato and bends into continuous lines instead of ZOH stair-steps,
    /// while still honoring the sampled pitch values exactly at their timestamps.
    /// </summary>
    public static double PitchAtSampleInterpolated(PreparedNote note, long sample, double samplesPerFrame)
        => PitchAtSampleInterpolated(note.InitialMidiNote, note.Pitch, sample);

    internal static double PitchAtSampleInterpolated(
        double initialMidiNote,
        PreparedPitchPoint[] pitch,
        long sample)
    {
        if (pitch.Length == 0)
            return initialMidiNote;
        int low = 0;
        int high = pitch.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (pitch[middle].SamplePosition <= sample)
                low = middle + 1;
            else
                high = middle;
        }

        if (low == 0)
            return initialMidiNote;
        if (low >= pitch.Length)
            return pitch[pitch.Length - 1].MidiNote;

        PreparedPitchPoint prev = pitch[low - 1];
        PreparedPitchPoint next = pitch[low];
        if (next.SamplePosition == prev.SamplePosition)
            return next.MidiNote;
        double t = (sample - prev.SamplePosition) / (double)(next.SamplePosition - prev.SamplePosition);
        t = Math.Clamp(t, 0, 1);
        return prev.MidiNote + t * (next.MidiNote - prev.MidiNote);
    }

    public static double PitchAtSampleInterpolatedMonotonic(
        PreparedNote note,
        long sample,
        double samplesPerFrame,
        ref int segmentIndex)
    {
        PreparedPitchPoint[] pitch = note.Pitch;
        if (pitch.Length == 0)
            return note.InitialMidiNote;
        while (segmentIndex + 1 < pitch.Length
            && pitch[segmentIndex + 1].SamplePosition <= sample)
            segmentIndex++;
        if (segmentIndex < 0)
            return note.InitialMidiNote;
        if (segmentIndex + 1 >= pitch.Length)
            return pitch[pitch.Length - 1].MidiNote;
        PreparedPitchPoint prev = pitch[segmentIndex];
        PreparedPitchPoint next = pitch[segmentIndex + 1];
        if (sample < prev.SamplePosition)
            return note.InitialMidiNote;
        if (next.SamplePosition == prev.SamplePosition)
            return next.MidiNote;
        double t = (sample - prev.SamplePosition) / (double)(next.SamplePosition - prev.SamplePosition);
        t = Math.Clamp(t, 0, 1);
        return prev.MidiNote + t * (next.MidiNote - prev.MidiNote);
    }
}
