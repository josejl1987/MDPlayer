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

    /// <summary>Human display name: "FM 007" for FM; "Sample {source:00}" for SNES
    /// DSP sample identities; the canonical string otherwise.</summary>
    public string DisplayName =>
        Family == IdentityFamily.Fm ? $"FM {DedupNumber:000}"
        : Family == IdentityFamily.Pcm && Canonical is not null
            && Canonical.StartsWith("spc:src", StringComparison.Ordinal)
            ? $"Sample {DedupNumber:00}"
            : Canonical;

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
        // SNES DSP sample identity: the decoders emit the short form
        // "spc:src{source}" (SnesDspTimelineDecoder) and the FULL canonical
        // "spc:src{source}:{shortHash}:{adsr1}{adsr2}{gain}[n]"
        // (SpcInstrumentBuilder). Accept both; the suffix (hash/ADSR/gain) is
        // captured VERBATIM in Canonical — the dedup equality key — never
        // re-derived. DedupNumber carries the source number for display.
        if (canonical.StartsWith("spc:src", StringComparison.Ordinal)
            && TryParseSpcSource(canonical, out int sourceNumber))
        {
            identity = new InstrumentIdentity(IdentityFamily.Pcm, sourceNumber, canonical);
            return true;
        }
        // PSG voice-type tokens (SN76489): tone/noise are display-only semantics —
        // PSG has no timbre identity (spec 30) — but they must parse so the
        // exporter never collapses them to a placeholder.
        if (canonical is "sn76489:tone" or "sn76489:noise")
        {
            identity = new InstrumentIdentity(IdentityFamily.Ssg, 0, canonical);
            return true;
        }
        return false;
    }

    /// <summary>Parses the source number of an "spc:srcN[:hex...][n]" identity:
    /// ^spc:src(\d+)(?::[0-9a-fA-F]+)*n?$. The trailing suffix (short hash,
    /// ADSR/gain, optional noise 'n') is validated as colon-hex groups but NOT
    /// interpreted — Canonical keeps it verbatim.</summary>
    private static bool TryParseSpcSource(string canonical, out int sourceNumber)
    {
        sourceNumber = 0;
        const string prefix = "spc:src";
        ReadOnlySpan<char> rest = canonical.AsSpan(prefix.Length);
        int digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
            digits++;
        if (digits == 0 || !int.TryParse(rest[..digits], out sourceNumber))
            return false;
        rest = rest[digits..];
        if (rest.Length == 0)
            return true; // short form: spc:srcN
        if (rest[^1] == 'n')
            rest = rest[..^1]; // optional noise suffix
        while (rest.Length > 0)
        {
            if (rest[0] != ':')
                return false;
            rest = rest[1..];
            int hex = 0;
            while (hex < rest.Length && IsHexDigit(rest[hex]))
                hex++;
            if (hex == 0)
                return false;
            rest = rest[hex..];
        }
        return true;
    }

    private static bool IsHexDigit(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}

/// <summary>
/// Key of one exported MIDI track: a source chip channel paired with the canonical
/// normalized instrument playing through it. Two notes differ track only when this
/// tuple differs; all <c>(Chip, SourceChannel, *)</c> tracks share one MIDI channel.
/// </summary>
internal readonly record struct MidiTrackKey(
    DeviceId Device,
    VoiceKind VoiceFamily,
    int SourceChannel,
    InstrumentIdentity Instrument)
{
    // Compatibility constructor for existing plan/unit fixtures. New exporters
    // must use the full device + voice-family identity above.
    public MidiTrackKey(ChipType chip, int sourceChannel, InstrumentIdentity instrument)
        : this(new DeviceId(chip, 0), VoiceKind.Pcm, sourceChannel, instrument) { }

    public ChipType Chip => Device.Type;
}
