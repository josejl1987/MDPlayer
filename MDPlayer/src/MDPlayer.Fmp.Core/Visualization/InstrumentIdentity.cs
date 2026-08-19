namespace Fmp.Core.Visualization;

/// <summary>Instrument families understood by the normalized-identity pipeline.</summary>
internal enum IdentityFamily
{
    Fm,
    Ssg,
    Midi,
    Rhythm,
    Pcm,
    Wavetable,
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

    /// <summary>
    /// Human display name. The normalized FM form renders as "FM 007"; SNES DSP
    /// sample voices as "Sample {source:00}"; wavetable as "WAVE 3"; chip-positional
    /// FM/MIDI/PCM labels collapse to their family ("FM", "MIDI", "PCM") since the
    /// track name already carries the chip and channel. Everything else keeps the
    /// canonical string.
    /// </summary>
    public string DisplayName => Family switch
    {
        IdentityFamily.Fm => FmDisplayName,
        IdentityFamily.Wavetable => WavetableDisplayName,
        IdentityFamily.Pcm => PcmDisplayName,
        IdentityFamily.Midi => "MIDI",
        IdentityFamily.Ssg => SsgDisplayName,
        _ => Canonical,
    };

    /// <summary>Human name for an SSG/PSG identity. The normalized <c>ssg:…</c> forms
    /// keep the canonical string; Game Boy pulse channels render as "PULSE" (the
    /// track name already carries the channel number).</summary>
    private string SsgDisplayName
    {
        get
        {
            if (Canonical is null)
                return "";
            if (Canonical.StartsWith("dmg:pulse:", StringComparison.Ordinal)
                || Canonical.StartsWith("dmg:", StringComparison.Ordinal))
                return "PULSE";
            return Canonical;
        }
    }

    /// <summary>Human name for an FM identity: "FM 007" for the normalized
    /// <c>fm:&lt;n&gt;</c> patch form; "FM" for chip-positional forms such as
    /// <c>ym2203:0:fm:1</c> (whose canonical has no global patch number).</summary>
    private string FmDisplayName
    {
        get
        {
            if (Canonical is not null
                && Canonical.StartsWith("fm:", StringComparison.Ordinal)
                && int.TryParse(Canonical.AsSpan(3), out _))
                return $"FM {DedupNumber:000}";
            return "FM";
        }
    }

    /// <summary>Human name for a wavetable identity: "WAVE 3" from "huc6280:wave:3".</summary>
    private string WavetableDisplayName
    {
        get
        {
            int separator = Canonical is null ? -1 : Canonical.LastIndexOf(':');
            return separator >= 0 && separator < Canonical.Length - 1
                ? "WAVE " + Canonical[(separator + 1)..]
                : "WAVE";
        }
    }

    /// <summary>Human name for a PCM identity: "Sample {source:00}" for SNES DSP
    /// sample voices (spc:srcN); "PCM" for chip-positional forms whose canonical
    /// has no global sample number.</summary>
    private string PcmDisplayName
    {
        get
        {
            if (Canonical is not null
                && Canonical.StartsWith("spc:src", StringComparison.Ordinal))
                return $"Sample {DedupNumber:00}";
            return "PCM";
        }
    }

    public bool IsEmpty => string.IsNullOrEmpty(Canonical);

    public bool Equals(InstrumentIdentity other) =>
        string.Equals(Canonical, other.Canonical, StringComparison.Ordinal);

    public override int GetHashCode() =>
        Canonical is null ? 0 : StringComparer.Ordinal.GetHashCode(Canonical);

    /// <summary>
    /// Parses a canonical identity string (the <see cref="NoteEvent.InstrumentId"/> /
    /// rhythm <c>InstrumentId</c> values emitted by the decoders) back into a typed
    /// identity. Returns false for placeholder/unresolved tokens, allowing callers
    /// to collapse a source channel to a single per-channel track.
    /// </summary>
    public static bool TryParse(string canonical, out InstrumentIdentity identity)
    {
        identity = Empty;
        if (string.IsNullOrWhiteSpace(canonical))
            return false;

        // Normalized identity forms: family-prefixed canonicals.
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
        // Chip-prefixed decoder forms: "<chip>[:<instance>]:<family-token>:<suffix>"
        // (e.g. ym2203:0:fm:1, ymz280b:pcm:1, huc6280:wave:1, ay8910:0:tone:1). The
        // family token carries the semantic family; the positional canonical keeps
        // distinct channels distinct tracks while giving them a real identity.
        if (canonical.Contains(":fm:", StringComparison.Ordinal))
        {
            identity = new InstrumentIdentity(IdentityFamily.Fm, 0, canonical);
            return true;
        }
        if (canonical.Contains(":pcm:", StringComparison.Ordinal))
        {
            identity = new InstrumentIdentity(IdentityFamily.Pcm, 0, canonical);
            return true;
        }
        if (canonical.Contains(":wave:", StringComparison.Ordinal))
        {
            identity = new InstrumentIdentity(IdentityFamily.Wavetable, 0, canonical);
            return true;
        }
        if (canonical.Contains(":tone:", StringComparison.Ordinal))
        {
            identity = new InstrumentIdentity(IdentityFamily.Ssg, 0, canonical);
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
        // PSG has no timbre identity (spec 30) — but they must parse so MIDI track
        // naming never collapses them to a placeholder.
        if (canonical is "sn76489:tone" or "sn76489:noise")
        {
            identity = new InstrumentIdentity(IdentityFamily.Ssg, 0, canonical);
            return true;
        }

        // Special decoder forms without a family token.
        // YM2151 is a pure FM chip: "ym2151:<instance>:<channel>".
        if (canonical.StartsWith("ym2151:", StringComparison.Ordinal))
        {
            identity = new InstrumentIdentity(IdentityFamily.Fm, 0, canonical);
            return true;
        }
        // Game Boy: "dmg:pulse:<channel>" / "dmg:wave:<channel>" (the DMG decoder
        // must disambiguate pulse vs wave channels), plus the legacy bare
        // "dmg:<n>" form where channel 3 is the wave channel and 1-2 are pulse.
        if (canonical.StartsWith("dmg:pulse:", StringComparison.Ordinal))
        {
            identity = new InstrumentIdentity(IdentityFamily.Ssg, 0, canonical);
            return true;
        }
        if (canonical.StartsWith("dmg:", StringComparison.Ordinal)
            && int.TryParse(canonical.AsSpan(4), out int dmgChannel)
            && dmgChannel is >= 1 and <= 3)
        {
            identity = new InstrumentIdentity(
                dmgChannel == 3 ? IdentityFamily.Wavetable : IdentityFamily.Ssg, 0, canonical);
            return true;
        }
        // MIDI passthrough: "midi:channel-<n>:program-<p>".
        if (canonical.StartsWith("midi:channel-", StringComparison.Ordinal))
        {
            identity = new InstrumentIdentity(IdentityFamily.Midi, 0, canonical);
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
    // Compatibility constructor for existing plan/unit fixtures. New callers
    // must use the full device + voice-family identity above.
    public MidiTrackKey(ChipType chip, int sourceChannel, InstrumentIdentity instrument)
        : this(new DeviceId(chip, 0), VoiceKind.Pcm, sourceChannel, instrument) { }

    public ChipType Chip => Device.Type;
}
