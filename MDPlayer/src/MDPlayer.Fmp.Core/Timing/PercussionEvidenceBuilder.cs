#nullable enable

using Fmp.Core.Visualization;

namespace Fmp.Core.Timing;

/// <summary>
/// Builds the unified percussion evidence stream (spec §3/§4/§6, D3) from a
/// timeline. Called EXACTLY once per time map by <see cref="MusicalTimeMapBuilder"/>;
/// the same collection is consumed by tempo inference and structural grid
/// selection. Sources, in cross-kind dedup priority order:
/// NativeRhythm (RhythmEvent, authoritative) &gt; AggregateHit (always evidence)
/// &gt; ClassifiedNote (FM note judged percussive). Kind-priority dedup applies
/// only ACROSS kinds — a non-native event claiming the same physical attack
/// slot (Domain + VoiceId + sample) as a native event is dropped; every native
/// RhythmEvent always appears (§5), so simultaneous kick and snare at one
/// sample remain separate events even when they share a rhythm voice.
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

        // Native rhythm events are authoritative (spec §5): EVERY RhythmEvent
        // appears with its shared-vocabulary role and its own strength — two
        // distinct RhythmEvents that share a physical slot (e.g. kick + snare on
        // one rhythm voice at one sample) are two attacks and both survive.
        // Kind-priority dedup (NativeRhythm > AggregateHit > ClassifiedNote)
        // therefore applies only ACROSS kinds: a non-native event claiming the
        // same attack slot as a native event is dropped.
        var nativeKeys = new HashSet<AttackKey>();
        var result = new List<PercussiveOnset>();

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
                nativeKeys.Add(new AttackKey(onset.Domain, onset.VoiceId, onset.SamplePosition));
                result.Add(onset);
            }
        }

        // Aggregate hits are always physical hits (Role Unknown; the shared
        // classifier has no authoritative vocabulary for aggregate labels).
        if (timeline.AggregateHits is not null)
        {
            var aggregateKeys = new HashSet<AttackKey>();
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
                var key = new AttackKey(onset.Domain, onset.VoiceId, onset.SamplePosition);
                if (nativeKeys.Contains(key) || !aggregateKeys.Add(key))
                    continue;
                result.Add(onset);
            }
        }

        // FM notes enter the stream only when classified percussive.
        if (timeline.Notes is not null)
        {
            var classifiedKeys = new HashSet<AttackKey>();
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
                var key = new AttackKey(onset.Domain, onset.VoiceId, onset.SamplePosition);
                if (nativeKeys.Contains(key) || !classifiedKeys.Add(key))
                    continue;
                result.Add(onset);
            }
        }

        return result
            .OrderBy(onset => onset.SamplePosition)
            .ThenBy(onset => onset.Domain?.ToString() ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(onset => onset.VoiceId, StringComparer.Ordinal)
            .ThenBy(onset => onset.EvidenceKind)
            .ToList();
    }

    // SCRATCH-DEBUG
    public static void DumpRoles(VisualizationTimeline timeline)
    {
        if (timeline?.Rhythm is null)
            return;
        var byRole = timeline.Rhythm
            .Take(3000)
            .GroupBy(r => RhythmRoleClassifier.Classify(r))
            .Select(g => $"{g.Key}={g.Count()}")
            .ToArray();
        Console.WriteLine($"[DBN-SCRATCH] rhythmRoles={string.Join(",", byRole)}");
        foreach (RhythmEvent r in timeline.Rhythm.Take(5))
            Console.WriteLine($"[DBN-SCRATCH]   rhythm voice='{r.Voice}' ch='{r.ChannelId}' inst='{r.InstrumentId}' parent='{r.ParentVoiceId}' domain={r.Domain} strength={r.Strength:0.###}");
        foreach (PercussiveOnset o in Build(timeline).Take(10))
            Console.WriteLine($"[DBN-SCRATCH]   onset role={o.Role} voice={o.VoiceId} strength={o.Strength:0.###} kind={o.EvidenceKind}");
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