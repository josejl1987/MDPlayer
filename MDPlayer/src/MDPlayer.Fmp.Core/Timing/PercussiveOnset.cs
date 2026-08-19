#nullable enable

using Fmp.Core.Visualization;

namespace Fmp.Core.Timing;

/// <summary>
/// How a <see cref="PercussiveOnset"/> entered the unified percussion evidence
/// stream. Ordered by evidence priority: native rhythm events are authoritative,
/// aggregate hits are always a physical hit, and classified FM notes are the
/// least certain source.
/// </summary>
internal enum PercussionEvidenceKind
{
    NativeRhythm,
    AggregateHit,
    ClassifiedNote,
}

/// <summary>
/// One percussive attack in the unified percussion evidence stream (spec §3/§4,
/// D3). Timing stages consume the SAME collection of these onsets, built exactly
/// once by <see cref="PercussionEvidenceBuilder"/>.
/// <see cref="RhythmRole.Unknown"/> is a valid role: an onset may be
/// confidently percussive without a kick/snare/hat identity.
/// </summary>
internal readonly record struct PercussiveOnset(
    long SamplePosition,
    SourceDomainKey? Domain,
    string VoiceId,
    RhythmRole Role,
    double Strength,
    PercussionEvidenceKind EvidenceKind,
    double Confidence);