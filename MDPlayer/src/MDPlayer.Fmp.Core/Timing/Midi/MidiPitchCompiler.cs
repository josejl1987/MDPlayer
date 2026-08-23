namespace Fmp.Core.Midi;

/// <summary>Exact, receiver-compatible pitch math for the MIDI compiler.</summary>
internal static class MidiPitchCompiler
{
    public static int SelectMinimaxBaseNote(IReadOnlyList<SourcePitchPoint> curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (curve.Count == 0)
            throw new InvalidOperationException("A source pitch curve cannot be empty.");

        double minimum = double.PositiveInfinity;
        double maximum = double.NegativeInfinity;
        foreach (SourcePitchPoint point in curve)
        {
            if (!double.IsFinite(point.MidiNote))
                throw new InvalidOperationException("Source pitch is not finite.");
            minimum = Math.Min(minimum, point.MidiNote);
            maximum = Math.Max(maximum, point.MidiNote);
        }

        double midpoint = (minimum + maximum) / 2.0;
        int lower = ClampMidiNote((long)Math.Floor(midpoint));
        int upper = ClampMidiNote((long)Math.Ceiling(midpoint));
        double lowerError = Excursion(minimum, maximum, lower);
        double upperError = Excursion(minimum, maximum, upper);

        // Equal minimax errors are resolved toward the lower note so the result
        // remains deterministic at half-semitone boundaries.
        return lowerError <= upperError ? lower : upper;
    }

    public static int RequiredBendRange(
        IEnumerable<(IReadOnlyList<SourcePitchPoint> Curve, int BaseNote)> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        double maximum = 0;
        foreach ((IReadOnlyList<SourcePitchPoint> curve, int baseNote) in notes)
        {
            if (baseNote is < 0 or > 127)
                throw new InvalidOperationException("Base MIDI note is outside [0, 127].");
            foreach (SourcePitchPoint point in curve)
            {
                if (!double.IsFinite(point.MidiNote))
                    throw new InvalidOperationException("Source pitch is not finite.");
                maximum = Math.Max(maximum, Math.Abs(point.MidiNote - baseNote));
            }
        }

        if (maximum <= 0)
            return 0;
        if (maximum > 127.0 + 1e-9)
            throw new InvalidOperationException(
                $"Required pitch-bend excursion {maximum:0.###} exceeds MIDI's 127-semitone RPN limit.");
        return checked((int)Math.Ceiling(maximum));
    }

    /// <summary>
    /// Encodes a signed internal bend value. MIDI's positive side has 8191
    /// steps while its negative side has 8192; the writer adds the 8192 center.
    /// </summary>
    public static int EncodeSignedBend(double deltaSemitones, int bendRange)
    {
        if (!double.IsFinite(deltaSemitones))
            throw new InvalidOperationException("Pitch bend delta is not finite.");
        if (bendRange == 0)
        {
            if (Math.Abs(deltaSemitones) > 1e-9)
                throw new InvalidOperationException("A zero-range domain cannot encode a pitch bend.");
            return 0;
        }
        if (bendRange is < 0 or > 127 || Math.Abs(deltaSemitones) > bendRange + 1e-9)
            throw new InvalidOperationException(
                $"Pitch offset {deltaSemitones:0.###} is outside +/-{bendRange} semitones.");

        double normalized = deltaSemitones / bendRange;
        double scaled = normalized < 0 ? normalized * 8192.0 : normalized * 8191.0;
        return Math.Clamp(
            (int)Math.Round(scaled, MidpointRounding.AwayFromZero),
            -8192,
            8191);
    }

    private static double Excursion(double minimum, double maximum, int baseNote) =>
        Math.Max(Math.Abs(minimum - baseNote), Math.Abs(maximum - baseNote));

    private static int ClampMidiNote(long value) => (int)Math.Clamp(value, 0L, 127L);
}
