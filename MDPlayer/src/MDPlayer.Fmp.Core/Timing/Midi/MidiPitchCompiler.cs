namespace Fmp.Core.Midi;

/// <summary>Exact, receiver-compatible pitch math for the MIDI compiler.</summary>
internal static class MidiPitchCompiler
{
    /// <summary>
    /// The smallest pitch-bend range (in semitones) that can encode every curve
    /// point as a bend relative to its base note. Zero means the voice is
    /// pitch-flat and needs no RPN setup. Ranges over 127 are unrepresentable
    /// and rejected: the source is reported, not rescued.
    /// </summary>
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

    /// <summary>Encodes the receiver-visible unsigned 14-bit Pitch Bend value.</summary>
    public static int EncodeUnsigned14(double deltaSemitones, int bendRange) =>
        EncodeSignedBend(deltaSemitones, bendRange) + 8192;

    /// <summary>Decodes the receiver-visible unsigned 14-bit Pitch Bend value.</summary>
    public static int DecodeUnsigned14(int unsignedValue)
    {
        if (unsignedValue is < 0 or > 16383)
            throw new ArgumentOutOfRangeException(nameof(unsignedValue));
        return unsignedValue - 8192;
    }

    /// <summary>Reconstructs the source-relative pitch from an unsigned bend.</summary>
    public static double DecodePitch(int baseNote, int unsignedValue, int bendRange)
    {
        if (baseNote is < 0 or > 127)
            throw new ArgumentOutOfRangeException(nameof(baseNote));
        if (bendRange is < 0 or > 127)
            throw new ArgumentOutOfRangeException(nameof(bendRange));
        int signed = DecodeUnsigned14(unsignedValue);
        double denominator = signed < 0 ? 8192.0 : 8191.0;
        return baseNote + signed / denominator * bendRange;
    }
}