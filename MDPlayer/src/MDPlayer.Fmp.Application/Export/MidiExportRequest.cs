namespace Fmp.Application.Export;

/// <summary>Who supplies the tempo used by a GUI MIDI export. Mirrors the CLI.</summary>
public enum MidiTempoSource
{
    /// <summary>Use the strongest available evidence (driver beats &gt; validated &gt; symbolic).</summary>
    Auto,
    Driver,
    Symbolic,
    Fixed,
}

/// <summary>
/// Options for a GUI MIDI export. All fields are optional; sensible defaults
/// resolve to "use the strongest available evidence". Tempo is NEVER derived
/// from meter, and a fixed BPM alone does not establish a phase.
/// </summary>
public sealed class MidiExportRequest
{
    public static readonly MidiExportRequest Default = new();

    /// <summary>Ticks per quarter note (default 960).</summary>
    public int Ppq { get; init; } = 960;

    public MidiTempoSource TempoSource { get; init; } = MidiTempoSource.Auto;

    /// <summary>Fixed tempo override (BPM).</summary>
    public double? Bpm { get; init; }

    /// <summary>Phase override: beat offset in samples at sample zero.</summary>
    public long? BeatOffsetSamples { get; init; }

    /// <summary>Time signature, e.g. "4/4".</summary>
    public string? Meter { get; init; }

    /// <summary>Sample of the first bar start (downbeat).</summary>
    public long? FirstDownbeatSample { get; init; }

    /// <summary>off | 1/8 | 1/16 | 1/32 grid snapping for note ons.</summary>
    public string Quantize { get; init; } = "off";

    /// <summary>Emit continuous pitch-bend for intra-note pitch movement.</summary>
    public bool EmitPitchBend { get; init; } = true;

    /// <summary>Semitones of the pitch-bend range written as an RPN (default 24). Not
    /// auto-expanded: an offset beyond it re-anchors or fails.</summary>
    public int BendRangeSemitones { get; init; } = 24;

    /// <summary>When true, an unresolved timing ambiguity (no beat phase, conflicting/
    /// anchors, pathological residual) fails the export instead of proceeding with an
    /// inferred/unaligned grid — mirroring the CLI --strict-timing. Default false: the
    /// GUI reports the signal on the result rather than throwing.</summary>
    public bool StrictTiming { get; init; }

    /// <summary>Place percussive voices on MIDI channel 9 (GM percussion).</summary>
    public bool UsePercussionChannel { get; init; } = true;

    /// <summary>Metadata to stamp on the conductor track.</summary>
    public string? Title { get; init; }

    /// <summary>Source format label (e.g. "vgz").</summary>
    public string? SourceFormat { get; init; }

    /// <summary>Default note velocity (1–127) for voices without a per-voice override.</summary>
    public int Velocity { get; init; } = 90;

    /// <summary>Emit loop/section markers on the conductor track.</summary>
    public bool EmitMarkers { get; init; } = true;

    /// <summary>Emit conductor track name / source metadata / timing-confidence text.</summary>
    public bool EmitConductorMetadata { get; init; } = true;

    /// <summary>Per-voice transforms. A missing/no-entry voice keeps default export behavior.</summary>
    public IReadOnlyList<MidiVoiceOption> VoiceOptions { get; init; } = Array.Empty<MidiVoiceOption>();
}

/// <summary>Per-voice MIDI export transform (mirrors the Core exporter override).</summary>
public sealed class MidiVoiceOption
{
    public MidiVoiceOption(string channelId)
    {
        ChannelId = channelId;
        Include = true;
        Program = null;
        Channel = null;
        Velocity = null;
        TransposeSemitones = 0;
    }

    /// <summary>The timeline's <c>ChannelId</c> this option applies to.</summary>
    public string ChannelId { get; }

    /// <summary>False to drop this voice (no track, no notes).</summary>
    public bool Include { get; set; } = true;

    /// <summary>GM program (0–127) forced on this voice's track; null = no override.</summary>
    public int? Program { get; set; }

    /// <summary>MIDI channel (0–15) forced on this voice's track; null = default allocation.</summary>
    public int? Channel { get; set; }

    /// <summary>Note velocity (1–127); null = option default.</summary>
    public int? Velocity { get; set; }

    /// <summary>Semitones to transpose this voice; 0 = none.</summary>
    public int TransposeSemitones { get; set; }
}

/// <summary>A voice discovered in a timeline, for building per-voice export options.</summary>
public sealed class MidiVoiceDescriptor
{
    public string ChannelId { get; init; } = "";
    public string Label { get; init; } = "";
    public bool IsPercussion { get; init; }
}

/// <summary>The discovered voices of a timeline (for the GUI's per-voice panel).</summary>
public sealed class MidiVoiceProbe
{
    public required bool Succeeded { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<MidiVoiceDescriptor> Voices { get; init; } = Array.Empty<MidiVoiceDescriptor>();
}

/// <summary>The outcome of a GUI MIDI export plus a concise human-readable report.</summary>
public sealed class MidiExportResult
{
    public required bool Succeeded { get; init; }

    public string? Error { get; init; }

    /// <summary>The MIDI file bytes (Format 1 SMF), when successful.</summary>
    public byte[]? Bytes { get; init; }

    /// <summary>Report lines suitable for a status notice (tempo/phase segments).</summary>
    public IReadOnlyList<string> Report { get; init; } = Array.Empty<string>();

    public int SegmentCount { get; init; }
    public string? TempoSource { get; init; }
    public string? PhaseSource { get; init; }
    public bool PhaseUnknown { get; init; }

    /// <summary>True when the beat grid (phase) could not be established.</summary>
    public bool Trustworthy => !PhaseUnknown && SegmentCount > 0;
}
