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
    {
        PreparedPitchPoint[] pitch = note.Pitch;
        if (pitch.Length == 0)
            return note.InitialMidiNote;

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

        return low == 0 ? note.InitialMidiNote : pitch[low - 1].MidiNote;
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
}
