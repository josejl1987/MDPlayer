using Fmp.Core.Midi;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using DryMarkerEvent = Melanchall.DryWetMidi.Core.MarkerEvent;
using DryMidiFile = Melanchall.DryWetMidi.Core.MidiFile;
using DryMidiFileFormat = Melanchall.DryWetMidi.Core.MidiFileFormat;
using DryNoteOnEvent = Melanchall.DryWetMidi.Core.NoteOnEvent;
using DryPitchBendEvent = Melanchall.DryWetMidi.Core.PitchBendEvent;
using DryWritingSettings = Melanchall.DryWetMidi.Core.WritingSettings;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Phase-5 tests for the identity-first MIDI sample-trigger export (spec §18),
/// the deduplicated sample asset/WAV + manifest export (spec §29), and the
/// DAC timing separation (spec §37/§38): the exporter receives an already-
/// established <see cref="MusicalTimeMap"/> (no independent sample→tick math,
/// no local BPM default), applies the shared non-negative origin, and keeps
/// DAC sample identity fully independent of trigger→tick (§67, §68).
/// </summary>
public sealed class DacMidiExporterTests
{
    private const int Src = 3;
    private const int Sr = 44_100;
    private const int Ppq = 480;

    [Fact]
    public void BuildEvents_EmitsNoteOnNoteOffPerTrigger()
    {
        DacAnalysisReport report = MakeReport(ops =>
        {
            Play(ops, 100, [0x10, 0x20]);   // asset 0
            Play(ops, 200, [0xAA, 0xBB]);   // asset 1
        });

        IReadOnlyList<DacMidiEvent> events = ExportEvents(report);

        var ons = events.Where(e => e.NoteOn).ToArray();
        var offs = events.Where(e => e.NoteOn == false && e.Text is null).ToArray();
        Assert.Equal(2, ons.Length);
        Assert.Equal(2, offs.Length);

        // asset 0 -> note 0, asset 1 -> note 1
        Assert.Equal(0, ons[0].Note);
        Assert.Equal(1, ons[1].Note);
        // note-on ticks increasing
        Assert.True(ons[1].Tick > ons[0].Tick);
    }

    [Fact]
    public void BuildEvents_SameTimestampRetrigger_OrdersNoteOffBeforeNoteOn()
    {
        DacAnalysisReport report = MakeReport(ops =>
        {
            byte[] a = [0x10, 0x20, 0x30];
            Play(ops, 100, a);              // ends at 110
            Play(ops, 110, a);              // retrigger at 110
        });

        IReadOnlyList<DacMidiEvent> events = ExportEvents(report);

        // The retrigger note-on and the prior note-off share the same tick (110
        // mapped to ticks). The note-off must be sorted before that note-on.
        int secondOnIndex = -1;
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i].NoteOn)
            {
                secondOnIndex = i;
                break;
            }
        }
        Assert.True(secondOnIndex > 0);
        Assert.False(events[secondOnIndex - 1].NoteOn);
        Assert.Equal(events[secondOnIndex - 1].Tick, events[secondOnIndex].Tick);
    }

    [Fact]
    public void Write_ProducesValidSmfHeader()
    {
        DacAnalysisReport report = MakeReport(ops =>
        {
            Play(ops, 0, [0x10, 0x20]);
            Play(ops, 100, [0xAA, 0xBB]);
        });

        byte[] midi = new DacMidiExporter(MakeMap()).Write(report);

        // "MThd" + length 6, format 1, at least one track marker "MTrk".
        Assert.Equal(0x4D, midi[0]); // M
        Assert.Equal(0x54, midi[1]); // T
        Assert.Equal(0x68, midi[2]); // h
        Assert.Equal(0x64, midi[3]); // d
        Assert.Equal(0x00, midi[8]); // format high byte
        Assert.Equal(0x01, midi[9]); // format low byte (format 1)
        Assert.True(midi[11] >= 1); // ntrks low byte (>= 1 track)
        Assert.Contains("MTrk", System.Text.Encoding.ASCII.GetString(midi));

        // Reasonable length given a header, meta track, and a data track.
        Assert.True(midi.Length > 14 + 20);
    }

    [Fact]
    public void DryWetMidi_IndependentSemanticRoundTrip_CoversMelodicRhythmMarkerPitchAndDac()
    {
        long sample = 1_000;
        var melodic = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = 5_000_000,
            SampleRate = Sr,
            Notes = new[]
            {
                new NoteEvent("melodic", sample, sample + 20_000, 440, 60, "inst",
                    VisualizationNoteMode.Fm, false,
                    new[] { new PitchChange(sample + 5_000, 466.16, 61.0) }),
            },
            Rhythm = new[] { new RhythmEvent("drums", "drums", sample, 1.0f, 0.5f) },
            LoopMarkers = new[] { new LoopMarker(sample, LoopMarkerKind.Start, 0) },
        };
        byte[] melodicBytes = new MusicalMidiExporter(MakeMap(), Ppq,
            new MusicalMidiExportOptions { EmitPitchBend = true }).Export(melodic).Bytes;
        byte[] dacBytes = new DacMidiExporter(MakeMap(), Ppq).Write(
            MakeReport(ops => Play(ops, sample, [0x10, 0x20])));

        Assert.Contains(MidiRoundTrip.TrackChunks(melodicBytes).SelectMany(c => c.Events),
            e => e is DryNoteOnEvent);
        Assert.Contains(MidiRoundTrip.TrackChunks(melodicBytes).SelectMany(c => c.Events),
            e => e is DryPitchBendEvent or DryMarkerEvent);
        Assert.Contains(MidiRoundTrip.TrackChunks(dacBytes).SelectMany(c => c.Events),
            e => e is DryNoteOnEvent);

        Assert.Equal(SemanticEvents(melodicBytes), SemanticEvents(ReWrite(melodicBytes)));
        Assert.Equal(SemanticEvents(dacBytes), SemanticEvents(ReWrite(dacBytes)));
    }

    [Fact]
    public void BuildEvents_RateMetadataIsEmittedWhenRatePresent()
    {
        DacAnalysisReport report = MakeReport(ops =>
        {
            Play(ops, 10, [0x10], rate: 8000);
        });

        IReadOnlyList<DacMidiEvent> events = ExportEvents(report);
        Assert.Contains(events, e =>
            e.Text != null && e.Text.StartsWith(DacMidiEvent.RateMetaTextPrefix + "8000"));
    }

    /// <summary>P1 — a rate-metadata text event must serialize to a WELL-FORMED SMF
    /// data track: exactly ONE VLQ delta precedes each FF 01 text meta, with no
    /// spurious second delta byte consumed as a status byte. The whole track must
    /// parse as a valid event stream to its End of Track.</summary>
    [Fact]
    public void Write_RateMetadata_ProducesValidDacTrackWithSingleDeltaPerMeta()
    {
        DacAnalysisReport report = MakeReport(ops =>
        {
            Play(ops, 10, [0x10], rate: 8000);
            Play(ops, 1000, [0xAA, 0xBB], rate: 11_025);
        });

        byte[] midi = new DacMidiExporter(MakeMap(), Ppq).Write(report);
        List<(long Delta, string Kind)> events = ParseDacTrackEvents(midi);

        // At least one rate meta text event and one note event survived intact.
        var rateTexts = events.Where(e => e.Kind.StartsWith("text:"));
        Assert.Contains(rateTexts, e => e.Kind.EndsWith("DACRATE:8000"));
        Assert.Contains(events, e => e.Kind == "note-on" || e.Kind == "note-off");

        // Every meta-text event must be preceded by exactly one delta (via the
        // parser reading delta+status+meta) — the parse itself proves no stray or
        // missing delta byte; otherwise ParseDacTrackEvents would record a bogus
        // channel-status event for the consumed 0x00 or throw.
        Assert.True(events.Count >= 4, "note on/off + rate metas must all be parsed");
        // The P1 double-delta bug consumed a zero byte as a status byte -> a
        // spurious "chan0"/"chanN" event. Fixed code must never emit one.
        Assert.DoesNotContain(events, e => e.Kind.StartsWith("chan"));
    }

    /// <summary>P2 — a Set Tempo whose serialized VLQ delta exceeds the representable
    /// MIDI range (0x0FFFFFFF) must be REJECTED, not emitted as a 5+ byte over-length
    /// quantity. The DAC writer mirrors the core writer's bound (§44).</summary>
    [Fact]
    public void Write_TempoDeltaBeyondVlqRange_IsRejected()
    {
        // A single-segment 120 BPM map runs to a huge sample count; its Set Tempo
        // sits at tick 0, but we need a SECOND tempo whose delta from tick 0 exceeds
        // 0x0FFFFFFF. Build a two-segment map whose boundary lands past that tick.
        long boundarySamples = 13_000_000_000L;
        double spq = Sr * 60.0 / 120.0;
        double boundaryQuarter = boundarySamples / spq;
        var map = new MusicalTimeMap(
            Sr,
            0,
            new[]
            {
                new TempoSegment(0, boundarySamples, 0.0, spq, 120, TimingSource.UserOverride, 1.0),
                new TempoSegment(boundarySamples, boundarySamples + 1, boundaryQuarter, spq, 90, TimingSource.UserOverride, 1.0),
            },
            meter: null);

        DacAnalysisReport report = MakeReport(ops => Play(ops, 0, [0x10, 0x20]));

        // The second tempo's delta (its own tick − 0) exceeds 0x0FFFFFFF, so the
        // writer must throw instead of serializing an over-long VLQ.
        Assert.Throws<ArgumentOutOfRangeException>(() => new DacMidiExporter(map, Ppq).Write(report));
    }

    /// <summary>§67 — melodic + DAC + rhythm at the same sample map to the same tick.</summary>
    [Fact]
    public void BuildEvents_SharedSample_MapsToSameTickAsMelodicAndRhythm()
    {
        DacAnalysisReport report = MakeReport(ops =>
        {
            Play(ops, 1000, [0x10, 0x20]);
        });

        IReadOnlyList<DacMidiEvent> events = ExportEvents(report);
        var dacOn = events.First(e => e.NoteOn);
        long sharedSample = 1000;

        // Build a timeline with a melodic note and a rhythm trigger at the SAME
        // source sample, export through the shared musical exporter (same map +
        // origin), and read back the note-on tick. All three must align.
        var map = MakeMap();
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = 5_000_000,
            SampleRate = Sr,
            Notes = new[]
            {
                new NoteEvent(
                    "melodic", sharedSample, sharedSample + 2000, 440, 69, "inst",
                    VisualizationNoteMode.Fm, false, Array.Empty<PitchChange>()),
            },
            Rhythm = new[]
            {
                new RhythmEvent("drums", "drums", sharedSample, 1.0f, 0.5f),
            },
        };
        var exporter = new MusicalMidiExporter(map, Ppq);
        var result = exporter.Export(timeline);
        using var ms = new MemoryStream(result.Bytes);
        using var br = new BinaryReader(ms);
        var noteTicks = ParseNoteOnTicks(br);

        // The melodic note-on and the rhythm note-on both land on the same tick as
        // the DAC trigger at the identical source sample.
        Assert.Contains(dacOn.Tick, noteTicks);
        Assert.True(noteTicks.All(t => t == dacOn.Tick), "melodic/rhythm ticks must match the DAC tick");
    }

    /// <summary>§68 — two distinct sample-ID→note maps → same ticks, same tempo track.</summary>
    [Fact]
    public void Write_TwoNoteMappings_SameTicksAndTempoTrack()
    {
        // Same timed DAC sequence under two different note bases. Only the
        // identity→note mapping differs; ticks and tempo must be identical.
        DacAnalysisReport report = MakeReport(ops =>
        {
            Play(ops, 100, [0x10, 0x20]);
            Play(ops, 500, [0xAA, 0xBB]);
        });

        var mapA = MakeMap();
        var mapB = MakeMap();

        // Different note bases change only the identity→note mapping.
        byte[] midiA = new DacMidiExporter(mapA, Ppq, noteBase: 0).Write(report);
        byte[] midiB = new DacMidiExporter(mapB, Ppq, noteBase: 30).Write(report);

        // Note numbers may differ (identity independent), ticks + tempo identical.
        var parsedA = ParseTicksAndTempo(midiA);
        var parsedB = ParseTicksAndTempo(midiB);

        Assert.Equal(parsedA.Ticks, parsedB.Ticks);
        Assert.Equal(parsedA.Tempi, parsedB.Tempi);
    }

    /// <summary>§19 — a multi-segment tempo map serializes a Set Tempo at EVERY
    /// segment boundary on the DAC conductor, not just the first segment.</summary>
    [Fact]
    public void Write_MultiSegmentTempoMap_EmitsTempoAtEveryBoundary()
    {
        var map = MakeMultiSegmentMap(firstDownbeatQuarter: null);
        DacAnalysisReport report = MakeReport(ops => Play(ops, 1000, [0x10, 0x20]));

        byte[] midi = new DacMidiExporter(map, Ppq).Write(report); // format 1, conductor track 0
        List<(long Tick, int Us)> tempos = ParseConductorTempos(midi);

        // One Set Tempo per distinct segment: segment 0 at its start (tick 0) and
        // segment 1 at the later boundary's sample-derived tick.
        Assert.Equal(2, tempos.Count);
        Assert.NotEqual(tempos[0].Us, tempos[1].Us);
        Assert.Equal(0, tempos[0].Tick);
        Assert.True(tempos[1].Tick > tempos[0].Tick, "the 2nd tempo must sit at the later boundary");
    }

    [Fact]
    public void Write_MultiSegmentTempoMap_PreservesSourceWallClockForSerializedDacNotes()
    {
        const double firstBpm = 123.45;
        const double secondBpm = 87.5;
        const long boundary = 200_000;
        double firstSpq = Sr * 60.0 / firstBpm;
        double secondSpq = Sr * 60.0 / secondBpm;
        var map = new MusicalTimeMap(
            Sr,
            0,
            new[]
            {
                new TempoSegment(0, boundary, 0.0, firstSpq, firstBpm, TimingSource.UserOverride, 1.0),
                new TempoSegment(boundary, 5_000_000, boundary / firstSpq, secondSpq, secondBpm,
                    TimingSource.UserOverride, 1.0),
            });
        long before = boundary - 100;
        long after = boundary + 100;
        DacAnalysisReport report = MakeReport(ops =>
        {
            Play(ops, before, [0x10, 0x20], duration: 100);
            Play(ops, after, [0xAA, 0xBB], duration: 100);
        });

        byte[] midi = new DacMidiExporter(map, Ppq).Write(report);
        List<(long Tick, bool NoteOn)> notes = ParseDacNoteEvents(midi);
        List<(long Tick, int Us)> tempos = ParseConductorTempos(midi);

        Assert.Equal(new[]
        {
            (int)Math.Round(60_000_000.0 / firstBpm),
            (int)Math.Round(60_000_000.0 / secondBpm),
        }, tempos.Select(t => t.Us).ToArray());
        Assert.Equal(4, notes.Count);
        Assert.Equal(new[] { true, false, true, false }, notes.Select(n => n.NoteOn).ToArray());

        double tickSeconds = tempos.Max(t => t.Us) / 1_000_000.0 / Ppq;
        var expectedSamples = new[] { before, boundary, after, after + 100 };
        foreach (var pair in expectedSamples.Zip(notes))
            Assert.InRange(Math.Abs(TickToSeconds(pair.Second.Tick, tempos) - (double)pair.First / Sr),
                0, tickSeconds);
    }

    /// <summary>§67 + §19 — first-downbeat-before-map-start AND a multi-segment map:
    /// DAC, melodic and rhythm triggers at the same sample map to the SAME tick
    /// (shared non-negative origin incl. the downbeat) and the DAC conductor carries
    /// the full tempo sequence at matching ticks.</summary>
    [Fact]
    public void Write_DownbeatBeforeMapStart_MultiSegment_SharedOriginAndFullTempoSequence()
    {
        var map = MakeMultiSegmentMap(firstDownbeatQuarter: -4.0);
        long sharedSample = 1000;
        DacAnalysisReport report = MakeReport(ops => Play(ops, sharedSample, [0x10, 0x20]));

        // DAC: same exporter + map → same origin as the melodic exporter.
        IReadOnlyList<DacMidiEvent> events = new DacMidiExporter(map, Ppq).BuildEvents(report);
        var dacOn = events.First(e => e.NoteOn);

        // Melodic + rhythm at the SAME source sample through the shared map.
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = 5_000_000,
            SampleRate = Sr,
            Notes = new[]
            {
                new NoteEvent(
                    "melodic", sharedSample, sharedSample + 2000, 440, 69, "inst",
                    VisualizationNoteMode.Fm, false, Array.Empty<PitchChange>()),
            },
            Rhythm = new[]
            {
                new RhythmEvent("drums", "drums", sharedSample, 1.0f, 0.5f),
            },
        };
        var exporter = new MusicalMidiExporter(map, Ppq); // EmitMarkers default true
        var result = exporter.Export(timeline);
        using var ms = new MemoryStream(result.Bytes);
        using var br = new BinaryReader(ms);
        List<long> noteTicks = ParseNoteOnTicks(br);

        // First-downbeat before map start shifts the origin: the shared sample does
        // NOT land on tick 0 — it lands on the same 1920+ tick for all three.
        Assert.True(dacOn.Tick > 0, "downbeat-before-map-start must push the shared tick past 0");
        Assert.Contains(dacOn.Tick, noteTicks);
        Assert.True(noteTicks.All(t => t == dacOn.Tick),
            "melodic/rhythm ticks must match the DAC tick on the shared origin");

        // The DAC conductor has the FULL tempo sequence at the same sample-derived
        // ticks the melodic conductor used (0? NO — offset by the downbeat origin).
        byte[] dacMidi = new DacMidiExporter(map, Ppq).Write(report);
        List<(long Tick, int Us)> dacTempos = ParseConductorTempos(dacMidi);
        Assert.Equal(2, dacTempos.Count);
        Assert.True(dacTempos[0].Tick > 0, "the first Set Tempo is shifted by the downbeat origin");
        Assert.True(dacTempos[1].Tick > dacTempos[0].Tick);
    }

    /// <summary>§67 + §21 (cycle 2 fix) — with EmitMarkers=false, a first-downbeat
    /// BEFORE the map start must NOT be folded into the DAC origin (mirroring the
    /// melodic exporter's gate), so a shared sample maps to the SAME tick for DAC,
    /// melodic and rhythm — no origin divergence from the marker-disabled melodic
    /// export, and no leading default-tempo ticks at the DAW.</summary>
    [Fact]
    public void BuildEvents_MarkersDisabled_DownbeatBeforeMapStart_SharedTickAcrossDacMelodicRhythm()
    {
        var map = MakeMultiSegmentMap(firstDownbeatQuarter: -4.0);
        long sharedSample = 1000;
        DacAnalysisReport report = MakeReport(ops => Play(ops, sharedSample, [0x10, 0x20]));

        // DAC, markers disabled: the downbeat is NOT folded, so the shared sample maps
        // to the melodic/rhythm tick (not a downbeat-shifted DAC-only tick).
        IReadOnlyList<DacMidiEvent> events = new DacMidiExporter(map, Ppq, emitMarkers: false).BuildEvents(report);
        var dacOn = events.First(e => e.NoteOn);

        // Melodic + rhythm at the SAME source sample through the shared map, markers
        // disabled on the melodic side too.
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = 5_000_000,
            SampleRate = Sr,
            Notes = new[]
            {
                new NoteEvent(
                    "melodic", sharedSample, sharedSample + 2000, 440, 69, "inst",
                    VisualizationNoteMode.Fm, false, Array.Empty<PitchChange>()),
            },
            Rhythm = new[]
            {
                new RhythmEvent("drums", "drums", sharedSample, 1.0f, 0.5f),
            },
        };
        var exporter = new MusicalMidiExporter(map, Ppq,
            new MusicalMidiExportOptions { EmitPitchBend = true, EmitMarkers = false });
        var result = exporter.Export(timeline);
        using var ms = new MemoryStream(result.Bytes);
        using var br = new BinaryReader(ms);
        List<long> noteTicks = ParseNoteOnTicks(br);

        // Markers disabled → the origin is NOT shifted by the excluded downbeat, so
        // the shared sample maps to a NON-downbeat tick (the melodic conductor's
        // first Set Tempo lands on tick 0) — and it is the SAME tick for all three.
        Assert.Contains(dacOn.Tick, noteTicks);
        Assert.True(noteTicks.All(t => t == dacOn.Tick),
            "melodic/rhythm ticks must match the DAC tick with markers disabled");
    }

    /// <summary>§37 — a negative source trigger yields a nonnegative tick after the shared origin.</summary>
    [Fact]
    public void BuildEvents_NegativeSourceQuarter_YieldsNonnegativeTick()
    {
        // A map whose musical origin is negative at sample 0 (a pickup): the DAC
        // exporter must apply the same non-negative origin so no trigger emits a
        // negative tick — it must include the conductor origin, not leave a gap.
        var map = MakeNegativeOriginMap();
        IReadOnlyList<DacMidiEvent> events = new DacMidiExporter(map, Ppq).BuildEvents(
            MakeReport(ops => Play(ops, 0, [0x10, 0x20])));

        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.True(e.Tick >= 0, $"tick {e.Tick} must be non-negative"));
        // The first emitted event lands on tick 0 (origin aligned), not a hidden gap.
        Assert.Contains(events, e => e.Tick == 0);
    }

    // ---- helpers ----

    private static IReadOnlyList<DacMidiEvent> ExportEvents(DacAnalysisReport report) =>
        new DacMidiExporter(MakeMap(), Ppq).BuildEvents(report);

    private static byte[] ReWrite(byte[] bytes)
    {
        DryMidiFile file = MidiRoundTrip.Read(bytes);
        using var stream = new MemoryStream();
        file.Write(stream, DryMidiFileFormat.MultiTrack, new DryWritingSettings());
        return stream.ToArray();
    }

    private static IReadOnlyList<string> SemanticEvents(byte[] bytes) =>
        MidiRoundTrip.TrackChunks(bytes)
            .SelectMany((_, track) => MidiRoundTrip.TimedEvents(bytes, track)
                .Select(item => $"{track}:{item.Tick}:{item.Event.GetType().Name}:{item.Event}"))
            .ToArray();

    private static DacAnalysisReport MakeReport(Action<List<DacOperation>> build)
    {
        var ops = new List<DacOperation>();
        build(ops);
        var tracker = new DacPlaybackTracker();
        foreach (DacOperation op in ops)
            tracker.Add(op);
        tracker.Complete(50_000);
        return DacAnalysisReport.From(tracker);
    }

    private static void Play(List<DacOperation> ops, long start, byte[] payload, double? rate = null,
        long duration = 10)
    {
        ops.Add(new DacOperation.DacPlaybackStarted(start, Src, 0, null, rate));
        for (int i = 0; i < payload.Length; i++)
            ops.Add(new DacOperation.DacByteConsumed(start + i, Src, i, payload[i]));
        ops.Add(new DacOperation.DacPlaybackStopped(start + duration, DacStopReason.ExplicitStop));
    }

    /// <summary>120 BPM map from sample 0, quarter 0.</summary>
    private static MusicalTimeMap MakeMap()
    {
        double spq = Sr * 60.0 / 120.0;
        return new MusicalTimeMap(
            Sr,
            0,
            new[]
            {
                new TempoSegment(0, 5_000_000, 0, spq, 120, TimingSource.UserOverride, 1.0),
            },
            meter: null);
    }

    /// <summary>A map whose quarter position at sample 0 is negative (pickup ≈ 2 beats).</summary>
    private static MusicalTimeMap MakeNegativeOriginMap()
    {
        double spq = Sr * 60.0 / 120.0;
        // Quarter position at sample 0 == -2.0 (a two-quarter pickup before the bar).
        return new MusicalTimeMap(
            Sr,
            0,
            new[]
            {
                new TempoSegment(0, 5_000_000, -2.0, spq, 120, TimingSource.UserOverride, 1.0),
            },
            meter: null);
    }

    /// <summary>
    /// A two-segment tempo map: 120 BPM from sample 0 to 200000, then 90 BPM from
    /// 200000 onward, quarter-continuous. FirstDownbeatQuarter may place a downbeat
    /// before the map start (quarter -4) when requested.
    /// </summary>
    private static MusicalTimeMap MakeMultiSegmentMap(double? firstDownbeatQuarter)
    {
        double spq120 = Sr * 60.0 / 120.0;
        double spq90 = Sr * 60.0 / 90.0;
        long boundary = 200_000;
        // Segment 1 (90 BPM) begins exactly where segment 0 (120 BPM) ends, so the
        // quarter position continues without a discontinuity.
        double boundaryQuarter = 0.0 + (boundary - 0) / spq120;
        return new MusicalTimeMap(
            Sr,
            0,
            new[]
            {
                new TempoSegment(0, boundary, 0.0, spq120, 120, TimingSource.UserOverride, 1.0),
                new TempoSegment(boundary, 5_000_000, boundaryQuarter, spq90, 90, TimingSource.UserOverride, 1.0),
            },
            meter: null,
            firstDownbeatQuarter: firstDownbeatQuarter);
    }

    /// <summary>
    /// Parses the (absolute tick, µs/qn) Set Tempo events on the conductor track —
    /// track 0 of this format-1 stream — in serialized order. The conductor track is
    /// the first MTrk chunk.
    /// </summary>
    /// <summary>
    /// Parses every event (delta, kind) on the DAC data track — the LAST MTrk
    /// chunk(s) of this format-1 stream — and throws if the track is malformed
    /// (an invalid status byte, an unterminated meta, or missing EOT). This is the
    /// P1 guard: the old double-delta bug emitted a stray 0x00 where a status byte
    /// is required, which would fail here.
    /// </summary>
    private static List<(long Delta, string Kind)> ParseDacTrackEvents(byte[] midi)
    {
        var found = new List<(long, string)>();
        using var ms = new MemoryStream(midi);
        using var br = new BinaryReader(ms);
        br.ReadBytes(4);               // MThd
        ReadInt32BE(br);               // MThd len
        br.ReadInt16();                // format
        int ntrks = ReadInt16BE(br);
        br.ReadInt16();                // division
        int trackIndex = 0;
        for (int t = 0; t < ntrks; t++)
        {
            br.ReadBytes(4);           // MTrk
            int len = ReadInt32BE(br);
            long trackEnd = ms.Position + len;
            while (ms.Position < trackEnd)
            {
                long delta = ReadVlv(br);
                if (ms.Position >= trackEnd)
                    Assert.Fail("delta consumed past track end: malformed track");
                byte status = br.ReadByte();
                if (status == 0xFF)
                {
                    byte type = br.ReadByte();
                    long metaLen = ReadVlv(br);
                    var payload = br.ReadBytes((int)metaLen);
                    // End of Track must be the final event in the chunk.
                    if (type == 0x2F)
                        Assert.True(ms.Position == trackEnd, $"trailing data after EOT (pos {ms.Position}, end {trackEnd})");
                    else if (type == 0x01)
                        found.Add((delta, "text:" + System.Text.Encoding.ASCII.GetString(payload)));
                    continue;
                }
                if ((status & 0xF0) == 0xF0)
                {
                    Assert.Fail("unexpected sysex in DAC data track");
                }
                int dataBytes = (status & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;
                byte[] data = new byte[dataBytes];
                for (int i = 0; i < dataBytes; i++)
                {
                    if (ms.Position >= trackEnd)
                        Assert.Fail("data byte past track end: malformed track");
                    data[i] = br.ReadByte();
                }
                string kind = (status & 0xF0) switch
                {
                    0x90 when data[1] != 0 => "note-on",
                    0x90 => "note-off00",
                    0x80 => "note-off",
                    _ => $"chan{(status & 0x0F)}",
                };
                found.Add((delta, kind));
            }
            trackIndex++;
        }
        return found;
    }

    private static List<(long Tick, int Us)> ParseConductorTempos(byte[] midi)
    {
        var tempos = new List<(long, int)>();
        using var ms = new MemoryStream(midi);
        using var br = new BinaryReader(ms);
        br.ReadBytes(4);               // MThd
        ReadInt32BE(br);               // MThd len
        br.ReadInt16();                // format
        int ntrks = ReadInt16BE(br);
        br.ReadInt16();                // division
        for (int t = 0; t < ntrks; t++)
        {
            br.ReadBytes(4);           // MTrk
            int len = ReadInt32BE(br);
            long trackEnd = ms.Position + len;
            long abs = 0;
            while (ms.Position < trackEnd)
            {
                abs += ReadVlv(br);
                byte status = br.ReadByte();
                if ((status & 0xFF) == 0xFF)
                {
                    byte type = br.ReadByte();
                    long metaLen = ReadVlv(br);
                    var payload = br.ReadBytes((int)metaLen);
                    if (type == 0x51 && t == 0) // Set Tempo on the conductor
                        tempos.Add((abs, (payload[0] << 16) | (payload[1] << 8) | payload[2]));
                    continue;
                }
                if ((status & 0xF0) == 0xF0)
                {
                    long sysexLen = ReadVlv(br);
                    for (long i = 0; i < sysexLen; i++)
                        br.ReadByte();
                    continue;
                }
                int dataBytes = (status & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;
                for (int i = 0; i < dataBytes; i++)
                    br.ReadByte();
            }
        }
        return tempos;
    }

    private static List<(long Tick, bool NoteOn)> ParseDacNoteEvents(byte[] midi)
    {
        var notes = new List<(long, bool)>();
        using var ms = new MemoryStream(midi);
        using var br = new BinaryReader(ms);
        br.ReadBytes(4); ReadInt32BE(br); br.ReadInt16();
        int ntrks = ReadInt16BE(br); br.ReadInt16();
        for (int track = 0; track < ntrks; track++)
        {
            br.ReadBytes(4);
            int length = ReadInt32BE(br);
            long end = ms.Position + length;
            long tick = 0;
            while (ms.Position < end)
            {
                tick += ReadVlv(br);
                byte status = br.ReadByte();
                if (status == 0xFF)
                {
                    br.ReadByte();
                    long size = ReadVlv(br);
                    br.ReadBytes((int)size);
                    continue;
                }
                if ((status & 0xF0) == 0xF0)
                {
                    long size = ReadVlv(br);
                    br.ReadBytes((int)size);
                    continue;
                }
                br.ReadByte();
                byte velocity = br.ReadByte();
                if (track > 0 && (status & 0xF0) is 0x80 or 0x90)
                    notes.Add((tick, (status & 0xF0) == 0x90 && velocity != 0));
            }
        }
        return notes;
    }

    private static double TickToSeconds(long tick, IReadOnlyList<(long Tick, int Us)> tempos)
    {
        double seconds = 0;
        long previousTick = tempos[0].Tick;
        int us = tempos[0].Us;
        foreach ((long tempoTick, int tempoUs) in tempos.Skip(1))
        {
            if (tick <= tempoTick)
                break;
            seconds += (tempoTick - previousTick) * us / 1_000_000.0 / Ppq;
            previousTick = tempoTick;
            us = tempoUs;
        }
        if (tick > previousTick)
            seconds += (tick - previousTick) * us / 1_000_000.0 / Ppq;
        return seconds;
    }

    /// <summary>Parses the absolute tick of every note-on (0x90 vel&gt;0) in a format-1 stream.</summary>
    private static List<long> ParseNoteOnTicks(BinaryReader br)
    {
        br.ReadBytes(4); ReadInt32BE(br); br.ReadInt16();
        int ntrks = ReadInt16BE(br); br.ReadInt16();
        var ticks = new List<long>();
        for (int t = 0; t < ntrks; t++)
        {
            br.ReadBytes(4);
            int len = ReadInt32BE(br);
            long trackEnd = br.BaseStream.Position + len;
            long abs = 0;
            while (br.BaseStream.Position < trackEnd)
            {
                long delta = ReadVlv(br);
                abs += delta;
                byte status = br.ReadByte();
                if (status == 0xFF)
                {
                    br.ReadByte();
                    long metaLen = ReadVlv(br);
                    for (long i = 0; i < metaLen; i++)
                        br.ReadByte();
                }
                else if ((status & 0xF0) == 0xF0)
                {
                    long sysexLen = ReadVlv(br);
                    for (long i = 0; i < sysexLen; i++)
                        br.ReadByte();
                }
                else
                {
                    int d0 = br.ReadByte();
                    int d1 = (status & 0xF0) is 0xC0 or 0xD0 ? -1 : br.ReadByte();
                    if ((status & 0xF0) == 0x90 && d1 != 0)
                        ticks.Add(abs);
                }
            }
        }
        return ticks;
    }

    private static (List<long> Ticks, List<long> Tempi) ParseTicksAndTempo(byte[] midi)
    {
        // Walk each MTrk, decode the VLQ delta stream and collect absolute ticks,
        // and the tempo bytes on the conductor (first) track.
        var ticks = new List<long>();
        var tempi = new List<long>();
        using var ms = new MemoryStream(midi);
        using var br = new BinaryReader(ms);
        br.ReadBytes(4);            // MThd
        ReadInt32BE(br);            // MThd len
        br.ReadInt16();             // format
        int ntrks = ReadInt16BE(br);
        br.ReadInt16();             // division
        for (int t = 0; t < ntrks; t++)
        {
            br.ReadBytes(4);        // MTrk
            int len = ReadInt32BE(br);
            long trackEnd = ms.Position + len;
            long abs = 0;
            while (ms.Position < trackEnd)
            {
                long delta = ReadVlv(br);
                abs += delta;
                byte status = br.ReadByte();
                if ((status & 0xFF) == 0xFF)
                {
                    byte type = br.ReadByte();
                    long metaLen = ReadVlv(br);
                    var payload = br.ReadBytes((int)metaLen);
                    if (type == 0x51) // Set Tempo
                        tempi.Add((payload[0] << 16) | (payload[1] << 8) | payload[2]);
                    continue;
                }
                switch (status & 0xF0)
                {
                    case 0x80:
                    case 0x90:
                        ticks.Add(abs);
                        br.ReadByte();
                        br.ReadByte();
                        break;
                    default:
                        br.ReadByte();
                        br.ReadByte();
                        break;
                }
            }
        }
        return (ticks, tempi);
    }

    private static long ReadVlv(BinaryReader br)
    {
        long value = 0;
        byte b;
        do
        {
            b = br.ReadByte();
            value = (value << 7) | (uint)(b & 0x7F);
        } while ((b & 0x80) != 0);
        return value;
    }

    private static int ReadInt16BE(BinaryReader br) => (br.ReadByte() << 8) | br.ReadByte();

    private static int ReadInt32BE(BinaryReader br) =>
        (br.ReadByte() << 24) | (br.ReadByte() << 16) | (br.ReadByte() << 8) | br.ReadByte();
}
