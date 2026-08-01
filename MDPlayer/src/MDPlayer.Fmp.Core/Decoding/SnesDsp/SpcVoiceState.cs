using Fmp.Core.Visualization;

namespace Fmp.Core.Decoding.SnesDsp;

/// <summary>
/// Mutable per-voice note model (§12.1). At most one note is active per S-DSP
/// voice at any time; a KEY_ON retrigger closes the previous note at the same
/// sample and opens a new one with <see cref="IsRetrigger"/> set.
/// </summary>
internal sealed class SpcActiveNote
{
    public bool Active { get; set; } = true;
    public bool Releasing { get; set; }

    /// <summary>True when the voice was keyed on with the S-DSP sample-position carry flag.</summary>
    public bool InitialCarryIn { get; set; }

    public long StartSample { get; set; }

    /// <summary>Sample where the release phase began, when RELEASE_START was observed.</summary>
    public long? ReleaseStartSample { get; set; }

    public int SourceNumber { get; set; }
    public string InstrumentId { get; set; } = "";

    /// <summary>Relative pitch at note start, in semitones above the sample's natural rate (§13.3).</summary>
    public double InitialRelativeSemitones { get; set; }

    public bool IsRetrigger { get; set; }

    /// <summary>Monotonic pitch-change points recorded after the note started.</summary>
    public List<PitchChange> Pitch { get; } = [];
}

/// <summary>
/// Immutable diagnostic snapshot of one voice's state.
/// </summary>
internal sealed record SpcVoiceState(
    int Voice,
    bool Active,
    bool Releasing,
    long? ReleaseStartSample,
    int SourceNumber,
    string InstrumentId,
    double InitialRelativeSemitones,
    int PitchPointCount);
