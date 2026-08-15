#nullable enable

using Fmp.Core.Visualization;

namespace Fmp.Core.Timing;

/// <summary>
/// Builds the unified percussion evidence stream (spec §3/§4/§6, D3) from a
/// timeline. Called EXACTLY once per time map by <see cref="MusicalTimeMapBuilder"/>;
/// the same collection is consumed by tempo inference, structural grid selection
/// and the MIDI exporter. Sources, in dedup priority order:
/// NativeRhythm (RhythmEvent, authoritative) &gt; AggregateHit (always evidence)
/// &gt; ClassifiedNote (FM note judged percussive). Events are deduplicated only
/// when they refer to the same physical attack (Domain + VoiceId + sample);
/// simultaneous kick and snare at one sample remain separate events.
/// </summary>
internal static class PercussionEvidenceBuilder
{
    private readonly record struct AttackKey(SourceDomainKey? Domain, string VoiceId, long SamplePosition);

    /// <summary>
    /// Builds the evidence list. The result is deterministically ordered by
    /// (SamplePosition, Domain, VoiceId, EvidenceKind); no HashSet order
    /// dependence leaks into output paths.
    /// </summary>
    public static IReadOnlyList<PercussiveOnset> Build(VisualizationTimeline timeline)
    {
        if (timeline is null)
            return Array.Empty<PercussiveOnset>();

        var byAttack = new Dictionary<AttackKey, PercussiveOnset>();

        // Native rhythm events are authoritative: every RhythmEvent appears
        // with its shared-vocabulary role and its own strength.
        if (timeline.Rhythm is not null)
        {
            foreach (RhythmEvent rhythm in timeline.Rhythm)
            {
                var onset = new PercussiveOnset(
                    rhythm.SamplePosition,
                    rhythm.Domain,
                    rhythm.ChannelId,
                    RhythmRoleClassifier.Classify(rhythm),
                    rhythm.Strength,
                    PercussionEvidenceKind.NativeRhythm,
                    Confidence: 1.0);
                byAttack.TryAdd(new AttackKey(onset.Domain, onset.VoiceId, onset.SamplePosition), onset);
            }
        }

        // Aggregate hits are always physical hits (Role Unknown; the shared
        // classifier has no authoritative vocabulary for aggregate labels).
        if (timeline.AggregateHits is not null)
        {
            foreach (AggregateHitEvent hit in timeline.AggregateHits)
            {
                var onset = new PercussiveOnset(
                    hit.SamplePosition,
                    Domain: null,
                    hit.VoiceId,
                    RhythmRole.Unknown,
                    hit.Strength,
                    PercussionEvidenceKind.AggregateHit,
                    Confidence: 1.0);
                byAttack.TryAdd(new AttackKey(onset.Domain, onset.VoiceId, onset.SamplePosition), onset);
            }
        }

        // FM notes enter the stream only when classified percussive.
        if (timeline.Notes is not null)
        {
            foreach (NoteEvent note in timeline.Notes)
            {
                if (note.Mode is not (VisualizationNoteMode.Fm or VisualizationNoteMode.Fm3Operator))
                    continue;

                InstrumentDefinition? instrument = FindInstrument(timeline, note.InstrumentId);
                PercussionClassification classification =
                    FmPercussionClassifier.Classify(note, instrument, timeline.SampleRate);
                if (!classification.IsPercussive)
                    continue;

                var onset = new PercussiveOnset(
                    note.StartSample,
                    note.Domain,
                    note.ChannelId,
                    classification.Role,
                    classification.Confidence,
                    PercussionEvidenceKind.ClassifiedNote,
                    classification.Confidence);
                byAttack.TryAdd(new AttackKey(onset.Domain, onset.VoiceId, onset.SamplePosition), onset);
            }
        }

        return byAttack.Values
            .OrderBy(onset => onset.SamplePosition)
            .ThenBy(onset => onset.Domain?.ToString() ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(onset => onset.VoiceId, StringComparer.Ordinal)
            .ThenBy(onset => onset.EvidenceKind)
            .ToList();
    }

    private static InstrumentDefinition? FindInstrument(VisualizationTimeline timeline, string instrumentId)
    {
        if (timeline.Instruments is null || string.IsNullOrEmpty(instrumentId))
            return null;
        foreach (InstrumentDefinition instrument in timeline.Instruments)
        {
            if (instrument.Id == instrumentId)
                return instrument;
        }
        return null;
    }
}