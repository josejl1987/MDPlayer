namespace Fmp.Core.Visualization;

/// <summary>Instrument families understood by the normalized-identity pipeline.</summary>
internal enum IdentityFamily
{
    Fm,
    Ssg,
    Midi,
    Rhythm,
    Pcm,
}

/// <summary>
/// Canonical normalized instrument identity. Two identities are VALUE-EQUAL when
/// their <see cref="Canonical"/> strings match — regardless of the dedup number or
/// the chip/instance/channel that produced them — so "FM 007" denotes the same
/// normalized patch everywhere. <see cref="DedupNumber"/> is the global stable
/// number assigned by <see cref="DeterministicIdentityTable"/>.
/// </summary>
internal readonly record struct InstrumentIdentity(
    IdentityFamily Family,
    int DedupNumber,
    string Canonical)
{
    public static readonly InstrumentIdentity Empty = new(IdentityFamily.Pcm, 0, "");

    /// <summary>Human display name: "FM 007" for FM; the canonical string otherwise.</summary>
    public string DisplayName =>
        Family == IdentityFamily.Fm ? $"FM {DedupNumber:000}" : Canonical;

    public bool IsEmpty => string.IsNullOrEmpty(Canonical);

    public bool Equals(InstrumentIdentity other) =>
        string.Equals(Canonical, other.Canonical, StringComparison.Ordinal);

    public override int GetHashCode() =>
        Canonical is null ? 0 : StringComparer.Ordinal.GetHashCode(Canonical);

    /// <summary>
    /// Parses a canonical identity string (the <see cref="NoteEvent.InstrumentId"/> /
    /// rhythm <c>InstrumentId</c> values emitted by the decoders) back into a typed
    /// identity. Returns false for placeholder/unresolved tokens, which is the
    /// exporter's signal to collapse a source channel to a single per-channel track.
    /// </summary>
    public static bool TryParse(string canonical, out InstrumentIdentity identity)
    {
        identity = Empty;
        if (string.IsNullOrWhiteSpace(canonical))
            return false;

        if (canonical.StartsWith("fm:", StringComparison.Ordinal)
            && int.TryParse(canonical.AsSpan(3), out int fmNumber))
        {
            identity = new InstrumentIdentity(IdentityFamily.Fm, fmNumber, canonical);
            return true;
        }
        if (canonical.StartsWith("ssg:", StringComparison.Ordinal))
        {
            identity = new InstrumentIdentity(IdentityFamily.Ssg, 0, canonical);
            return true;
        }
        if (canonical.StartsWith("rhythm:", StringComparison.Ordinal))
        {
            identity = new InstrumentIdentity(IdentityFamily.Rhythm, 0, canonical);
            return true;
        }
        if (canonical.StartsWith("dac:", StringComparison.Ordinal))
        {
            identity = new InstrumentIdentity(IdentityFamily.Pcm, 0, canonical);
            return true;
        }
        if (canonical.StartsWith("pcm:", StringComparison.Ordinal))
        {
            identity = new InstrumentIdentity(IdentityFamily.Pcm, 0, canonical);
            return true;
        }
        return false;
    }
}

/// <summary>
/// Key of one exported MIDI track: a source chip channel paired with the canonical
/// normalized instrument playing through it. Two notes differ track only when this
/// tuple differs; all <c>(Chip, SourceChannel, *)</c> tracks share one MIDI channel.
/// </summary>
internal readonly record struct MidiTrackKey(
    ChipType Chip,
    int SourceChannel,
    InstrumentIdentity Instrument);