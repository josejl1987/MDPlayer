using Fmp.Core.Visualization;

namespace Fmp.Core.Analysis;

internal static class FmpSymbolicNormalizer
{
    internal const string Version = "1";

    public static AnalysisInput Normalize(VisualizationTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.SampleRate <= 0)
            throw new ArgumentException("Timeline sample rate must be positive.", nameof(timeline));
        if (timeline.EndSample < timeline.StartSample)
            throw new ArgumentException("Timeline end precedes its start.", nameof(timeline));

        var voiceById = (timeline.Voices ?? Array.Empty<VoiceDescriptor>())
            .Where(voice => voice is not null)
            .GroupBy(voice => voice.Id.ToString(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var notesByChannel = (timeline.Notes ?? Array.Empty<NoteEvent>())
            .Where(note => note is not null)
            .Where(note => note.EndSample > note.StartSample)
            .OrderBy(note => note.StartSample)
            .ThenBy(note => note.ChannelId, StringComparer.Ordinal)
            .GroupBy(note => note.ChannelId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var externalNotesByChannel = BuildExternalNotes(timeline, voiceById);
        var externalKinds = externalNotesByChannel
            .ToDictionary(pair => pair.Key, pair => pair.Value.Kind, StringComparer.Ordinal);

        var channelIds = voiceById.Keys
            .Concat(notesByChannel.Keys)
            .Concat(externalNotesByChannel.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var channels = new List<AnalysisChannel>(channelIds.Length);
        foreach (string channelId in channelIds)
        {
            voiceById.TryGetValue(channelId, out VoiceDescriptor voice);
            notesByChannel.TryGetValue(channelId, out NoteEvent[] sourceNotes);
            sourceNotes ??= Array.Empty<NoteEvent>();
            string kind = externalKinds.TryGetValue(channelId, out string externalKind)
                ? externalKind
                : ChannelKind(voice, sourceNotes);
            double weight = voice?.Kind == VoiceKind.Fm3Operator
                || sourceNotes.Any(note => note.Mode == VisualizationNoteMode.Fm3Operator)
                ? 0.35 : 1.0;
            if (kind is "rhythm" or "ssg-noise")
                weight = 0;

            var notes = new List<AnalysisNote>();
            for (int index = 0; index < sourceNotes.Length; index++)
            {
                NoteEvent source = sourceNotes[index];
                bool noise = IsNoise(voice, source);
                bool pitched = !noise && source.InitialMidiNote >= 0
                    && double.IsFinite(source.InitialMidiNote)
                    && voice?.Kind is not VoiceKind.Rhythm and not VoiceKind.Noise;
                StructuralPitchResult pitch = pitched
                    ? StructuralPitchExtractor.Extract(source, timeline.SampleRate)
                    : new StructuralPitchResult(-1, Array.Empty<StructuralPitchRegion>(), false, false, -1);
                notes.Add(new AnalysisNote
                {
                    Id = $"{channelId}:{source.StartSample}:{index}",
                    StartSample = source.StartSample,
                    EndSample = source.EndSample,
                    StructuralMidiPitch = Math.Round(pitch.PrincipalPitch, 4),
                    PitchClass = pitch.PitchClass,
                    Octave = pitch.PitchClass >= 0 ? (int)Math.Floor(pitch.PrincipalPitch / 12) - 1 : -1,
                    InstrumentId = source.InstrumentId ?? "",
                    IsRetrigger = source.IsRetrigger,
                    IsPitched = pitched,
                    IsNoise = noise,
                    Microtonal = pitch.Microtonal,
                    Gliding = pitch.Gliding,
                    PitchRegions = pitch.Regions
                        .Select(region => new AnalysisPitchRegion(
                            region.StartSample,
                            region.EndSample,
                            Math.Round(region.MidiPitch, 4),
                            Math.Round(region.Stability, 6)))
                        .ToArray(),
                });
            }

            if (externalNotesByChannel.TryGetValue(channelId, out ExternalAnalysisNotes external))
                notes.AddRange(external.Notes);

            channels.Add(new AnalysisChannel
            {
                Id = channelId,
                Kind = kind,
                AnalysisWeight = weight,
                Notes = notes
                    .OrderBy(note => note.StartSample)
                    .ThenBy(note => note.EndSample)
                    .ThenBy(note => note.Id, StringComparer.Ordinal)
                    .ToArray(),
            });
        }

        BeatEvent[] beats = ValidatedBeats(timeline);
        var input = new AnalysisInput
        {
            SampleRate = timeline.SampleRate,
            StartSample = timeline.StartSample,
            EndSample = timeline.EndSample,
            Track = new AnalysisTrack
            {
                Title = timeline.Source?.Title ?? "",
                SourceFormat = timeline.Source?.SourceFormat ?? "FMP",
                SourcePathHint = SafePathHint(timeline.Source?.SourceFile),
            },
            Timing = new AnalysisTiming
            {
                TimingMode = beats.Length >= 2 ? "beats" : "seconds",
                TempoEvents = (timeline.Timing ?? Array.Empty<DriverTimingEvent>())
                    .Where(value => value.ValidatedBpm is > 0 && double.IsFinite(value.ValidatedBpm.Value))
                    .Select(value => new TempoEvent(value.SamplePosition, value.ValidatedBpm.Value, 1.0))
                    .OrderBy(value => value.Sample)
                    .ToArray(),
                Beats = beats
                    .Select(value => new BeatPosition(value.SamplePosition, value.BeatIndex))
                    .ToArray(),
                Loops = (timeline.LoopMarkers ?? Array.Empty<LoopMarker>())
                    .OrderBy(marker => marker.SamplePosition)
                    .ThenBy(marker => marker.Iteration)
                    .Select(marker => new AnalysisLoop(
                        marker.SamplePosition,
                        marker.Kind.ToString().ToLowerInvariant(),
                        marker.Iteration))
                    .ToArray(),
            },
            Channels = channels,
            ArpeggioEvidence = FindArpeggioEvidence(channels, timeline.SampleRate),
        };
        input.AnalysisId = "sha256:" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(AnalysisJson.CanonicalizeWithoutId(input))))
            .ToLowerInvariant();
        return input;
    }

    private static BeatEvent[] ValidatedBeats(VisualizationTimeline timeline)
    {
        BeatEvent[] values = (timeline.Beats ?? Array.Empty<BeatEvent>())
            .Where(value => value is not null
                && value.SamplePosition >= timeline.StartSample
                && value.SamplePosition <= timeline.EndSample
                && double.IsFinite(value.BeatIndex))
            .OrderBy(value => value.SamplePosition)
            .ThenBy(value => value.BeatIndex)
            .ToArray();
        if (values.Length < 2)
            return Array.Empty<BeatEvent>();
        for (int index = 1; index < values.Length; index++)
        {
            if (values[index].SamplePosition <= values[index - 1].SamplePosition
                || values[index].BeatIndex <= values[index - 1].BeatIndex)
                return Array.Empty<BeatEvent>();
        }
        DriverTimingEvent[] tempos = (timeline.Timing ?? Array.Empty<DriverTimingEvent>())
            .Where(value => value.ValidatedBpm is > 0 && double.IsFinite(value.ValidatedBpm.Value))
            .OrderBy(value => value.SamplePosition)
            .ToArray();
        foreach (var pair in values.Zip(values.Skip(1)))
        {
            DriverTimingEvent tempo = tempos.LastOrDefault(value => value.SamplePosition <= pair.First.SamplePosition);
            if (tempo?.ValidatedBpm is not > 0)
                continue;
            double expectedSamples = timeline.SampleRate * 60.0
                / tempo.ValidatedBpm.Value * (pair.Second.BeatIndex - pair.First.BeatIndex);
            double error = Math.Abs(pair.Second.SamplePosition - pair.First.SamplePosition - expectedSamples)
                / Math.Max(1.0, expectedSamples);
            if (error > .35)
                return Array.Empty<BeatEvent>();
        }
        return values;
    }

    private sealed record ExternalAnalysisNotes(string Kind, IReadOnlyList<AnalysisNote> Notes);

    private static Dictionary<string, ExternalAnalysisNotes> BuildExternalNotes(
        VisualizationTimeline timeline,
        IReadOnlyDictionary<string, VoiceDescriptor> voices)
    {
        var result = new Dictionary<string, ExternalAnalysisNotes>(StringComparer.Ordinal);

        string ppz8Channel = voices.Values
            .Where(voice => voice.Kind == VoiceKind.Pcm
                && voice.Id.Device.Type == ChipType.Ppz8
                && string.Equals(voice.Id.Name, "ppz8", StringComparison.Ordinal))
            .Select(voice => voice.Id.ToString())
            .FirstOrDefault() ?? "ppz8.0";
        var ppz8Notes = (timeline.Ppz8 ?? Array.Empty<Ppz8Event>())
            .Select((value, index) => ExternalNote(
                $"{ppz8Channel}:{value.StartSample}:sample:{index}",
                value.StartSample,
                value.EndSample,
                value.MidiNote,
                false))
            .Where(note => note is not null)
            .Cast<AnalysisNote>()
            .ToArray();
        if (ppz8Notes.Length > 0)
            result[ppz8Channel] = new ExternalAnalysisNotes("ppz8", ppz8Notes);

        string adpcmChannel = voices.Values
            .Where(voice => voice.Kind == VoiceKind.Adpcm
                && string.Equals(voice.Id.Name, "adpcm-b", StringComparison.Ordinal))
            .Select(voice => voice.Id.ToString())
            .FirstOrDefault() ?? "adpcm-b";
        var adpcmNotes = (timeline.AdpcmB ?? Array.Empty<AdpcmBEvent>())
            .Select((value, index) => ExternalNote(
                $"{adpcmChannel}:{value.StartSample}:sample:{index}",
                value.StartSample,
                value.EndSample,
                value.FrequencyHz is > 0 ? 69.0 + 12.0 * Math.Log2(value.FrequencyHz.Value / 440.0) : null,
                false))
            .Where(note => note is not null)
            .Cast<AnalysisNote>()
            .ToArray();
        if (adpcmNotes.Length > 0)
            result[adpcmChannel] = new ExternalAnalysisNotes("adpcm", adpcmNotes);

        foreach (IGrouping<string, RhythmEvent> group in (timeline.Rhythm ?? Array.Empty<RhythmEvent>())
            .GroupBy(value => value.ChannelId, StringComparer.Ordinal))
        {
            AnalysisNote[] rhythmNotes = group
                .OrderBy(value => value.SamplePosition)
                .Select((value, index) => new AnalysisNote
                {
                    Id = $"{group.Key}:{value.SamplePosition}:rhythm:{index}",
                    StartSample = value.SamplePosition,
                    EndSample = Math.Min(timeline.EndSample, value.SamplePosition + 1),
                    StructuralMidiPitch = -1,
                    PitchClass = -1,
                    Octave = -1,
                    IsPitched = false,
                    IsNoise = true,
                })
                .Where(note => note.EndSample > note.StartSample)
                .ToArray();
            if (rhythmNotes.Length > 0)
                result[group.Key] = new ExternalAnalysisNotes("rhythm", rhythmNotes);
        }

        return result;
    }

    private static AnalysisNote ExternalNote(
        string id,
        long startSample,
        long endSample,
        double? midiPitch,
        bool noise)
    {
        bool pitched = midiPitch is >= 0 and <= 127 && double.IsFinite(midiPitch.Value);
        double value = pitched ? midiPitch.Value : -1;
        double nearest = pitched ? Math.Round(value, MidpointRounding.AwayFromZero) : -1;
        bool microtonal = pitched && Math.Abs(value - nearest) * 100 > StructuralPitchExtractor.VibratoToleranceCents;
        int pitchClass = pitched && !microtonal ? ((int)nearest % 12 + 12) % 12 : -1;
        return new AnalysisNote
        {
            Id = id,
            StartSample = startSample,
            EndSample = endSample,
            StructuralMidiPitch = value,
            PitchClass = pitchClass,
            Octave = pitchClass >= 0 ? (int)Math.Floor(value / 12) - 1 : -1,
            IsPitched = pitched,
            IsNoise = noise,
            Microtonal = microtonal,
        };
    }

    private static string ChannelKind(VoiceDescriptor voice, IReadOnlyList<NoteEvent> notes)
    {
        if (voice is not null)
        {
            if (voice.IsPercussion || voice.Kind == VoiceKind.Rhythm)
                return "rhythm";
            if (voice.IsNoise || voice.Kind == VoiceKind.Noise)
                return "ssg-noise";
            return voice.Kind switch
            {
                VoiceKind.Fm => "fm",
                VoiceKind.Fm3Operator => "fm3-operator",
                VoiceKind.Ssg or VoiceKind.Psg or VoiceKind.Pulse => "ssg-tone",
                VoiceKind.Adpcm => "adpcm",
                VoiceKind.Pcm => "ppz8",
                VoiceKind.MidiChannel => "unknown",
                _ => "unknown",
            };
        }
        if (notes.Any(note => note.Mode == VisualizationNoteMode.Fm3Operator))
            return "fm3-operator";
        if (notes.Any(note => note.Mode is VisualizationNoteMode.SsgNoise
            or VisualizationNoteMode.SsgEnvelopeNoise))
            return "ssg-noise";
        if (notes.Any(note => note.Mode is VisualizationNoteMode.SsgTone
            or VisualizationNoteMode.SsgToneNoise
            or VisualizationNoteMode.SsgEnvelopeTone
            or VisualizationNoteMode.SsgEnvelopeToneNoise))
            return "ssg-tone";
        if (notes.Any(note => note.Mode == VisualizationNoteMode.Fm))
            return "fm";
        if (notes.Any(note => note.Mode == VisualizationNoteMode.Pcm))
            return "ppz8";
        return "unknown";
    }

    private static bool IsNoise(VoiceDescriptor voice, NoteEvent note)
        => voice?.IsNoise == true || voice?.Kind == VoiceKind.Noise
            || note.Mode is VisualizationNoteMode.SsgNoise
                or VisualizationNoteMode.SsgEnvelopeNoise;

    private static string SafePathHint(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return "";
        string normalized = source.Replace('\\', '/');
        return Path.GetFileName(normalized);
    }

    private static IReadOnlyList<ArpeggioEvidence> FindArpeggioEvidence(
        IReadOnlyList<AnalysisChannel> channels,
        int sampleRate)
    {
        var result = new List<ArpeggioEvidence>();
        long maxGap = Math.Max(1, (long)Math.Round(sampleRate * 0.10));
        foreach (AnalysisChannel channel in channels)
        {
            AnalysisNote[] notes = channel.Notes
                .Where(note => note.IsPitched && note.PitchClass >= 0)
                .OrderBy(note => note.StartSample)
                .ToArray();
            for (int start = 0; start + 2 < notes.Length; start++)
            {
                int end = start;
                while (end + 1 < notes.Length
                    && notes[end + 1].StartSample - notes[end].StartSample <= maxGap)
                    end++;
                if (end - start + 1 < 3)
                    continue;
                int[] classes = notes[start..(end + 1)].Select(note => note.PitchClass).Distinct().OrderBy(x => x).ToArray();
                if (classes.Length < 3)
                    continue;
                int[] sequence = notes[start..(end + 1)].Select(note => note.PitchClass).ToArray();
                int cycleLength = FindCycleLength(sequence);
                if (cycleLength < 2)
                    continue;
                long[] gaps = Enumerable.Range(start, end - start)
                    .Select(index => notes[index + 1].StartSample - notes[index].StartSample)
                    .Where(gap => gap > 0)
                    .ToArray();
                if (gaps.Length == 0)
                    continue;
                long period = gaps.Take(cycleLength).Sum();
                double expectedGap = period / (double)cycleLength;
                double regularity = Math.Clamp(
                    1 - gaps.Select(gap => Math.Abs(gap - expectedGap)).Average()
                        / Math.Max(1, expectedGap),
                    0,
                    1);
                if (regularity < 0.70)
                    continue;
                result.Add(new ArpeggioEvidence
                {
                    ChannelId = channel.Id,
                    StartSample = notes[start].StartSample,
                    EndSample = notes[end].EndSample,
                    PitchClasses = classes,
                    PeriodSamples = period,
                    Regularity = Math.Round(regularity, 6),
                    Weight = Math.Round(Math.Min(1, regularity * channel.AnalysisWeight), 6),
                });
                start = end;
            }
        }
        return result.OrderBy(value => value.StartSample).ThenBy(value => value.ChannelId, StringComparer.Ordinal).ToArray();
    }

    private static int FindCycleLength(IReadOnlyList<int> sequence)
    {
        int maximum = Math.Min(6, sequence.Count / 2);
        for (int period = 2; period <= maximum; period++)
        {
            int comparisons = sequence.Count - period;
            int matches = Enumerable.Range(period, comparisons)
                .Count(index => sequence[index] == sequence[index - period]);
            if (comparisons > 0 && matches / (double)comparisons >= 0.65)
                return period;
        }
        return 0;
    }
}
