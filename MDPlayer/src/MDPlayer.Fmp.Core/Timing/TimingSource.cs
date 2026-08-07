#nullable enable

namespace Fmp.Core.Timing;

/// <summary>
/// Identifies the origin of musical-time evidence for a tempo segment or phase
/// estimate. Higher in the enum does not imply higher confidence; priority is a
/// <em>policy</em> decision enforced by <see cref="MusicalTimeMapBuilder"/>.
/// </summary>
internal enum TimingSource
{
    /// <summary>Driver beat anchors: known sample positions with quarter positions. Strongest.</summary>
    DriverBeatAnchors,

    /// <summary>A validated BPM emitted by the driver (no beat anchors). Provides tempo, not phase.</summary>
    DriverValidatedTempo,

    /// <summary>Captured sequencer/driver clock. Not yet wired to a producer.</summary>
    SequencerClock,

    /// <summary>Explicit user-provided BPM / beat offset / meter.</summary>
    UserOverride,

    /// <summary>Tempo/phase inferred from symbolic onsets (note-ons, rhythm hits).</summary>
    SymbolicInference,

    /// <summary>Tempo inferred from rendered audio. Reserved; never used by the current writer.</summary>
    AudioInference,
}
