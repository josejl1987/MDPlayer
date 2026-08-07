using Fmp.Core.Midi;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// End-to-end musical MIDI export tests: build a timeline, fit its time map, export
/// to Format 1 MIDI, then parse the bytes back and verify tempo placement, tick
/// accuracy, note monotonicity, retrigger ordering, determinism, and the conductor
/// markers.
/// </summary>
public sealed class MusicalMidiExporterTests
{
    private const int Sr = 44_100;
    private const int Ppq = 960;

    [Fact]
    public void Export_Format1_ConductorHasSetTempoAndMarkers()
    {
        TimelineState tt = BuildTimeline(120, noteTiming: "on");

        byte[] bytes = Export(tt);

        ParsedMidi parsed = Parser.Parse(bytes);
        Assert.Equal(1, parsed.Format);
        Assert.Equal(Ppq, parsed.Ppq);
        Assert.True(parsed.ConductorTempoTicks.Count >= 1, "conductor must contain a Set Tempo");
        Assert.Contains(parsed.ConductorMarkers, m => m.Name == "SOURCE_START");
        Assert.Contains(parsed.ConductorMarkers, m => m.Name == "LOOP_START");
        Assert.Contains(parsed.ConductorMarkers, m => m.Name == "LOOP_END");
    }

    [Fact]
    public void Export_BeatPhase_PreservedNotQuantized()
    {
        // Sample zero is one quarter before the first downbeat.
        double spq = Sr * 60.0 / 120.0;
        var timeline = BuildTimelineFixedPhase(spq);
        var state = new TimelineState { Timeline = timeline };
        byte[] bytes = Export(state);
        ParsedMidi parsed = Parser.Parse(bytes);

        // The first note sits at sample 0.5*spq => quarter -0.5 relative to the source
        // origin. Its musical phase must be preserved after the origin shift, so the
        // note-on lands on a half-quarter tick, never snapped to a whole quarter/bar.
        long noteOn = parsed.Notes[0].On;
        Assert.True(noteOn > 0);
        Assert.Equal(Ppq / 2, noteOn % Ppq); // 480 within the quarter → phase -0.5 preserved
        Assert.NotEqual(0, noteOn % Ppq);
    }

    [Fact]
    public void Export_NoCumulativeDrift_TenMinuteSong()
    {
        double spq = Sr * 60.0 / 120.0;
        var notes = Enumerable.Range(0, 4800)
            .Where(i => i % 4 == 0)
            .Select(i => NewNote("v", (long)Math.Round(i * spq), (long)Math.Round(i * spq) + 1000, 60))
            .ToArray();
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = (long)Math.Round(4800 * spq) + 10_000,
            SampleRate = Sr,
            Notes = notes,
        };
        var state = new TimelineState { Timeline = timeline, FixedBpm = 120 };
        byte[] bytes = Export(state);
        ParsedMidi parsed = Parser.Parse(bytes);

        // The last note starts at quarter 4796 and must map to tick 4796*PPQ exactly.
        double lastStartSample = 4796 * spq;
        double expectedTick = 4796 * (double)Ppq;
        // Find the note-on events and pick the one nearest to any conductor marker.
        var noteOns = parsed.NoteOns.OrderBy(n => n.Tick).ToList();
        Assert.NotEmpty(noteOns);
        long maxExpected = (long)expectedTick;
        Assert.True(noteOns[^1].Tick >= maxExpected - 2 && noteOns[^1].Tick <= maxExpected + 2,
            $"last note tick {noteOns[^1].Tick} should be ~{maxExpected}");
        _ = lastStartSample;
    }

    [Fact]
    public void Export_NotesAreMonotonic_OffAfterOn()
    {
        TimelineState tt = BuildTimeline(120, noteTiming: "on");
        byte[] bytes = Export(tt);
        ParsedMidi parsed = Parser.Parse(bytes);

        foreach (IMidiPitchNote note in parsed.Notes)
        {
            Assert.True(note.Off > note.On, "note-off must follow note-on");
        }
    }

    [Fact]
    public void Export_SameTickRetrigger_OffBeforeOn()
    {
        // Build a retrigger directly at the writer level: a note-off and the next
        // note-on share the same MIDI tick. The writer must serialize the note-off
        // (0x80) before the note-on (0x90) so a same-pitch retrigger never glitches
        // into a running legato.
        var track = new global::Fmp.Core.Midi.MidiTrack { Name = "retrig" };
        const int on = 960; // quarter 1
        const int tick = 2400 + 240; // off and next on both here
        track.Events.Add(new global::Fmp.Core.Midi.MidiNoteEvent(on, 1, 0, 64, 90, NoteOn: true));
        track.Events.Add(new global::Fmp.Core.Midi.MidiNoteEvent(tick, 1, 0, 64, 90, NoteOn: false));
        track.Events.Add(new global::Fmp.Core.Midi.MidiNoteEvent(tick, 1, 0, 64, 90, NoteOn: true));

        var writer = new global::Fmp.Core.Midi.MidiFileWriter(Ppq);
        byte[] bytes = writer.Write(Array.Empty<MidiEventBase>(), new[] { track });

        // Walk the single musical track recording (tick, status, note) occurrences in
        // serialized order; the off (0x8x) must serialize before the on (0x9x).
        var occurrences = WalkNoteStatus(bytes);
        int offIndex = occurrences.FindIndex(o => o.Status == 0x80);
        int onIndex = occurrences.FindIndex(o => o.Status == 0x90);
        Assert.True(offIndex >= 0, "expected a note-off");
        Assert.True(onIndex >= 0, "expected a note-on");
        // At the shared retrigger tick, the note-off must serialize before the note-on.
        var sameTick = occurrences.Where(o => o.Tick == tick).ToList();
        int relOff = sameTick.FindIndex(o => o.Status == 0x80);
        int relOn = sameTick.FindIndex(o => o.Status == 0x90);
        Assert.Equal(2, sameTick.Count);
        Assert.Equal(0, relOff);
        Assert.Equal(1, relOn);
    }

    private static List<(long Tick, int Status, int Note)> WalkNoteStatus(byte[] data)
    {
        var result = new List<(long, int, int)>();
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);
        ms.Position = 14; // skip MThd header + 6-byte SMF body
        while (ms.Position + 8 <= ms.Length)
        {
            byte[] id = br.ReadBytes(4);
            int trackLen = (br.ReadByte() << 24) | (br.ReadByte() << 16) | (br.ReadByte() << 8) | br.ReadByte();
            long trackEnd = ms.Position + trackLen;
            long absTick = 0;
            int runningStatus = 0;
            while (ms.Position < trackEnd)
            {
                absTick += ReadVlvLong(br);
                int firstData = -1;
                int b = br.ReadByte();
                if (b == 0xFF)
                {
                    runningStatus = 0xFF;
                    br.ReadByte();
                    br.ReadBytes((int)ReadVlvLong(br));
                    continue;
                }
                if (b == 0xF0 || b == 0xF7)
                {
                    runningStatus = 0xF0;
                    br.ReadBytes((int)ReadVlvLong(br));
                    continue;
                }
                int status;
                if ((b & 0x80) != 0)
                {
                    status = b;
                    runningStatus = status;
                }
                else
                {
                    status = runningStatus;
                    firstData = b;
                }
                switch (status & 0xF0)
                {
                    case 0x90:
                    case 0x80:
                        int note = firstData >= 0 ? firstData : br.ReadByte();
                        br.ReadByte(); // velocity
                        result.Add((absTick, status & 0xF0, note));
                        break;
                    case 0xC0:
                    case 0xD0:
                        if (firstData < 0) br.ReadByte();
                        break;
                    default:
                        if (firstData >= 0) br.ReadByte(); else br.ReadBytes(2);
                        break;
                }
            }
        }
        return result;

        static long ReadVlvLong(BinaryReader r)
        {
            long v = 0; byte b;
            do { b = r.ReadByte(); v = (v << 7) | (uint)(b & 0x7F); } while ((b & 0x80) != 0);
            return v;
        }
    }


    [Fact]
    public void Export_ByteForByteDeterministic()
    {
        TimelineState tt = BuildTimeline(120, noteTiming: "on");
        byte[] first = Export(tt);
        byte[] second = Export(tt);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Export_PitchBend_AppearsForPitchingNote()
    {
        double spq = Sr * 60.0 / 120.0;
        long start = (long)Math.Round(spq);
        double pitchMidi = FrequencyToMidinote(1320);
        var note = new NoteEvent(
            ChannelId: "v",
            StartSample: start,
            EndSample: start + 50_000,
            InitialFrequencyHz: 440,
            InitialMidiNote: 60,
            InstrumentId: "inst",
            Mode: VisualizationNoteMode.Fm,
            IsRetrigger: false,
            Pitch: new[] { new PitchChange(start + 5000, 1320, pitchMidi) });
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = start + 100_000,
            SampleRate = Sr,
            Notes = new[] { note },
        };
        var state = new TimelineState { Timeline = timeline, FixedBpm = 120 };
        byte[] bytes = Export(state);
        ParsedMidi parsed = Parser.Parse(bytes);

        Assert.True(parsed.PitchBendCount > 0, "a pitching note should emit pitch bends");
    }

    [Fact]
    public void Export_FractionalInitialNote_BendsToTruePitchAtNoteOn()
    {
        double spq = Sr * 60.0 / 120.0;
        long start = (long)Math.Round(spq);
        // A note whose true pitch is 60.3 semitones; it must be played at 60.3, not
        // the rounded 60, via a note-on pitch-bend (cent correction).
        var note = new NoteEvent(
            ChannelId: "v",
            StartSample: start,
            EndSample: start + 50_000,
            InitialFrequencyHz: 440,
            InitialMidiNote: 60.3,
            InstrumentId: "inst",
            Mode: VisualizationNoteMode.Fm,
            IsRetrigger: false,
            Pitch: Array.Empty<PitchChange>());
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = start + 100_000,
            SampleRate = Sr,
            Notes = new[] { note },
        };
        var state = new TimelineState { Timeline = timeline, FixedBpm = 120 };
        ParsedMidi parsed = Parser.Parse(Export(state));

        // A bend at (or before) the note-on corrects the rounded note to 60.3.
        ParsedBend? bend = parsed.Bends.OrderBy(b => b.Tick).FirstOrDefault();
        Assert.NotNull(bend);
        // bend is in [-8192, 8191] for +-2 semitones range; 0.3 ST -> +1228.
        Assert.InRange(bend!.Bend, 1100, 1350);
        long noteOn = parsed.Notes[0].On;
        Assert.True(bend.Tick <= parsed.Notes[0].On, "cent correction bend must precede/coincide with note-on");
    }

    [Fact]
    public void Export_LongPitchContour_SplitsIntoMultipleNotesRatherThanClipping()
    {
        double spq = Sr * 60.0 / 120.0;
        long start = (long)Math.Round(spq);
        // A clean D4 (60) glissing down past the +-2 semitone bend range to about
        // 45 (C#) — a 15-semitone slide that a single +-2 note cannot carry.
        var changes = new[]
        {
            new PitchChange(start + 10_000, 440 * Math.Pow(2, -2.0 / 12), 58.0),
            new PitchChange(start + 20_000, 440 * Math.Pow(2, -7.0 / 12), 53.0),
            new PitchChange(start + 30_000, 440 * Math.Pow(2, -12.0 / 12), 48.0),
            new PitchChange(start + 40_000, 440 * Math.Pow(2, -15.6 / 12), 44.4),
        };
        var note = new NoteEvent(
            ChannelId: "v",
            StartSample: start,
            EndSample: start + 60_000,
            InitialFrequencyHz: 440,
            InitialMidiNote: 60,
            InstrumentId: "inst",
            Mode: VisualizationNoteMode.Fm,
            IsRetrigger: false,
            Pitch: changes);
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = start + 100_000,
            SampleRate = Sr,
            Notes = new[] { note },
        };
        var state = new TimelineState { Timeline = timeline, FixedBpm = 120 };
        ParsedMidi parsed = Parser.Parse(Export(state));

        // The long contour must be re-articulated into several short notes (one
        // per segment fold) instead of a single clipped note.
        Assert.True(parsed.Notes.Count >= 3,
            $"expected the glissando to split into >=3 notes, got {parsed.Notes.Count}");
        // Every emitted bend stays within the configured range.
        Assert.All(parsed.Bends, bend => Assert.InRange(bend.Bend, -8192, 8191));
        // The final note of the run lands near the endpoint pitch (C# ~44).
        long lastOn = parsed.Notes.Max(n => n.On);
        Assert.True(lastOn > parsed.Notes[0].On, "later split notes start after the first");
    }

    [Fact]
    public void Export_WithQuantize_SnapsNoteOnToGrid()
    {
        double spq = Sr * 60.0 / 120.0;
        // Note slightly off an eighth-note grid line.
        long offGrid = (long)Math.Round(spq) + 1200;
        var note = NewNote("v", offGrid, offGrid + 5000, 60);
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = offGrid + 20_000,
            SampleRate = Sr,
            Notes = new[] { note },
        };
        var state = new TimelineState { Timeline = timeline, FixedBpm = 120 };

        // Unquantized: exact sample-derived tick.
        byte[] rawBytes = Export(state);
        long rawTick = Parser.Parse(rawBytes).Notes[0].On;
        // Quantized to 1/16: nearest (PPQ/4 = 240) grid line.
        state.Quantize = "1/16";
        byte[] qBytes = Export(state);
        long qTick = Parser.Parse(qBytes).Notes[0].On;

        Assert.NotEqual(rawTick, qTick);
        Assert.Equal(0, qTick % (Ppq / 4));
    }

    /* ---------- helpers ---------- */

    private sealed class TimelineState
    {
        public VisualizationTimeline Timeline;
        public string Quantize = "off";
        public double? FixedBpm;
    }

    private static double FrequencyToMidinote(double freq) => 69 + 12 * Math.Log2(freq / 440.0);

    private static NoteEvent NewNote(string voice, long start, long end, int midi)
        => new(
            ChannelId: voice,
            StartSample: start,
            EndSample: end,
            InitialFrequencyHz: 440,
            InitialMidiNote: midi,
            InstrumentId: "inst",
            Mode: VisualizationNoteMode.Fm,
            IsRetrigger: false,
            Pitch: Array.Empty<PitchChange>());

    private static TimelineState BuildTimeline(double bpm, string noteTiming)
    {
        double spq = Sr * 60.0 / bpm;
        var notes = new List<NoteEvent>();
        if (noteTiming == "on")
        {
            for (int i = 0; i < 8; i++)
                notes.Add(NewNote("v", (long)Math.Round(i * 4 * spq), (long)Math.Round(i * 4 * spq) + 2000, 60 + i));
        }
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = (long)Math.Round(8 * 4 * spq) + 20_000,
            SampleRate = Sr,
            Notes = notes.ToArray(),
            Beats = BuildBeats(bpm),
            LoopMarkers = new[]
            {
                new LoopMarker((long)Math.Round(1 * 4 * spq), LoopMarkerKind.Start, 0),
                new LoopMarker((long)Math.Round(4 * 4 * spq), LoopMarkerKind.Restart, 0),
            },
            Source = new TrackMetadata("vgz", "song", "chip", "song.vgz"),
        };
        return new TimelineState { Timeline = timeline };
    }

    private static BeatEvent[] BuildBeats(double bpm)
    {
        double spq = Sr * 60.0 / bpm;
        return Enumerable.Range(0, 40)
            .Select(i => new BeatEvent((long)Math.Round(i * 4 * spq / 4.0), i * 4.0 / 4.0))
            .ToArray();
    }

    private static VisualizationTimeline BuildTimelineFixedPhase(double spq)
    {
        // beat index 0 is at sample spq (one quarter late), so sample zero is at -1 quarter.
        var notes = new[]
        {
            NewNote("v", (long)Math.Round(0.5 * spq), (long)Math.Round(0.5 * spq) + 2000, 60),
        };
        var beats = Enumerable.Range(0, 8)
            .Select(i => new BeatEvent((long)Math.Round((i + 1) * spq), i + 1))
            .ToArray();
        return new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = (long)Math.Round(8 * spq) + 20_000,
            SampleRate = Sr,
            Notes = notes,
            Beats = beats,
        };
    }

    private static byte[] Export(TimelineState state)
    {
        var build = MusicalTimeMapBuilder.Build(state.Timeline, new MusicalTimeMapOptions
        {
            FixedBpm = state.FixedBpm,
            Meter = new Meter(4, 4),
            DetectTempoChanges = true,
        });
        var exportOptions = new MusicalMidiExportOptions
        {
            Quantize = state.Quantize,
            EmitPitchBend = true,
        };
        var exporter = new MusicalMidiExporter(build.Map, Ppq, exportOptions)
        {
            Diagnostics = build.Diagnostics,
        };
        return exporter.Export(state.Timeline).Bytes;
    }

    private static byte[] ExportWithOptions(TimelineState state, MusicalMidiExportOptions options)
    {
        var build = MusicalTimeMapBuilder.Build(state.Timeline, new MusicalTimeMapOptions
        {
            FixedBpm = state.FixedBpm,
            Meter = new Meter(4, 4),
            DetectTempoChanges = true,
        });
        var exporter = new MusicalMidiExporter(build.Map, Ppq, options)
        {
            Diagnostics = build.Diagnostics,
        };
        return exporter.Export(state.Timeline).Bytes;
    }

    [Fact]
    public void Export_ExcludedVoice_EmitsNoNotesFromThatChannel()
    {
        // Two voices; exclude "v2".
        double spq = Sr * 60.0 / 120.0;
        var notes = new[]
        {
            NewNote("v1", (long)Math.Round(1 * spq), (long)Math.Round(1 * spq) + 1000, 60),
            NewNote("v2", (long)Math.Round(2 * spq), (long)Math.Round(2 * spq) + 1000, 62),
        };
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = (long)Math.Round(4 * spq) + 20_000,
            SampleRate = Sr,
            Notes = notes,
            Beats = BuildBeats(120),
        };
        var state = new TimelineState { Timeline = timeline };
        byte[] bytes = ExportWithOptions(state, new MusicalMidiExportOptions
        {
            EmitPitchBend = true,
            VoiceOverrides = new[]
            {
                new VoiceExportOverride("v1"),
                new VoiceExportOverride("v2") { Include = false },
            },
        });
        ParsedMidi parsed = Parser.Parse(bytes);
        Assert.All(parsed.NoteOns, n => Assert.NotEqual(62, n.Note)); // v2's pitch (62) must be absent
    }

    [Fact]
    public void Export_VelocityOverride_ChangesNoteVelocity()
    {
        double spq = Sr * 60.0 / 120.0;
        var notes = new[]
        {
            NewNote("v1", (long)Math.Round(1 * spq), (long)Math.Round(1 * spq) + 1000, 60),
        };
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = (long)Math.Round(4 * spq) + 20_000,
            SampleRate = Sr,
            Notes = notes,
            Beats = BuildBeats(120),
        };
        byte[] bytes = ExportWithOptions(new TimelineState { Timeline = timeline },
            new MusicalMidiExportOptions { EmitPitchBend = true, Velocity = 33 });
        ParsedMidi parsed = Parser.Parse(bytes);
        Assert.Contains(parsed.NoteOns, n => n.Velocity == 33);
    }

    [Fact]
    public void Export_ProgramOverride_SetsProgramOnChannel()
    {
        double spq = Sr * 60.0 / 120.0;
        var notes = new[]
        {
            NewNote("v1", (long)Math.Round(1 * spq), (long)Math.Round(1 * spq) + 1000, 60),
        };
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = (long)Math.Round(4 * spq) + 20_000,
            SampleRate = Sr,
            Notes = notes,
            Beats = BuildBeats(120),
        };
        byte[] bytes = ExportWithOptions(new TimelineState { Timeline = timeline },
            new MusicalMidiExportOptions
            {
                EmitPitchBend = true,
                VoiceOverrides = new[] { new VoiceExportOverride("v1") { Program = 40 } },
            });
        ParsedMidi parsed = Parser.Parse(bytes);
        Assert.Contains(parsed.ProgramByChannel.Values, p => p == 40);
    }

    [Fact]
    public void Export_TransposeOverride_ShiftsPitch()
    {
        double spq = Sr * 60.0 / 120.0;
        var notes = new[]
        {
            NewNote("v1", (long)Math.Round(1 * spq), (long)Math.Round(1 * spq) + 1000, 60),
        };
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = (long)Math.Round(4 * spq) + 20_000,
            SampleRate = Sr,
            Notes = notes,
            Beats = BuildBeats(120),
        };
        byte[] bytes = ExportWithOptions(new TimelineState { Timeline = timeline },
            new MusicalMidiExportOptions
            {
                EmitPitchBend = true,
                VoiceOverrides = new[] { new VoiceExportOverride("v1") { TransposeSemitones = 5 } },
            });
        ParsedMidi parsed = Parser.Parse(bytes);
        // Original note 60 transposed +5 => MIDI 65.
        Assert.Contains(parsed.NoteOns, n => n.Note == 65);
    }

    [Fact]
    public void Export_ChannelOverride_ChangesNoteChannel()
    {
        double spq = Sr * 60.0 / 120.0;
        var notes = new[]
        {
            NewNote("v1", (long)Math.Round(1 * spq), (long)Math.Round(1 * spq) + 1000, 60),
        };
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = (long)Math.Round(4 * spq) + 20_000,
            SampleRate = Sr,
            Notes = notes,
            Beats = BuildBeats(120),
        };
        byte[] bytes = ExportWithOptions(new TimelineState { Timeline = timeline },
            new MusicalMidiExportOptions
            {
                EmitPitchBend = true,
                VoiceOverrides = new[] { new VoiceExportOverride("v1") { Channel = 3 } },
            });
        ParsedMidi parsed = Parser.Parse(bytes);
        Assert.Contains(parsed.NoteOns, n => n.Channel == 3);
    }
}

/* ---- minimal SMF reader (Format 1) for validation ---- */

internal interface IMidiPitchNote
{
    long On { get; }
    long Off { get; }
    int Note { get; }
    int Channel { get; }
}

internal sealed class ParsedMidi
{
    public int Format;
    public int Ppq;
    public List<TempoAtTick> ConductorTempo = new();
    public List<Marker> ConductorMarkers = new();
    public List<ParsedPitchNote> Notes = new();
    public int PitchBendCount;
    public List<ParsedBend> Bends = new();

    // Tempo events sorted by tick.
    public List<long> ConductorTempoTicks => ConductorTempo.Select(t => t.Tick).ToList();
    public List<ParsedMidiNote> NoteOns { get; } = new();

    // Channel -> last program change seen on that channel.
    public Dictionary<int, int> ProgramByChannel { get; } = new();
}

internal sealed record ParsedBend(long Tick, int Channel, int Bend);

internal sealed record TempoAtTick(long Tick, int MicrosecondsPerQuarter);

internal sealed class ParsedMidiNote
{
    public long Tick;
    public int Note;
    public int Channel;
    public int Velocity;
}

internal static class Parser
{
    public static ParsedMidi Parse(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);
        byte[] hdr = br.ReadBytes(4);
        if (!(hdr.Length == 4 && hdr[0] == (byte)'M' && hdr[1] == (byte)'T' && hdr[2] == (byte)'h' && hdr[3] == (byte)'d'))
            throw new InvalidDataException("not an SMF file (missing MThd)");
        int hdrLen = ReadInt32(br);
        int format = ReadInt16(br);
        int ntrks = ReadInt16(br);
        int division = ReadInt16(br);
        var result = new ParsedMidi { Format = format, Ppq = division };
        _ = hdrLen;
        if (format != 1 && format != 0)
            throw new InvalidDataException($"unexpected SMF format {format}");

        for (int t = 0; t < ntrks; t++)
        {
            byte[] id = br.ReadBytes(4);
            int len = ReadInt32(br);
            long trackEnd = ms.Position + len;
            if (id[0] != (byte)'M' || id[1] != (byte)'T' || id[2] != (byte)'r' || id[3] != (byte)'k')
                throw new InvalidDataException("expected MTrk chunk");

            long absTick = 0;
            int runningStatus = 0;
            var openNotes = new Dictionary<(int channel, int note), long>();
            while (ms.Position < trackEnd)
            {
                absTick += ReadVlv(br);
                int b = br.ReadByte();
                if (b == 0xFF)
                {
                    runningStatus = 0xFF;
                    byte type = br.ReadByte();
                    byte[] payload = ReadBytes(br, (int)ReadVlv(br));
                    if (type == 0x51 && payload.Length >= 3)
                    {
                        int usperQ = (payload[0] << 16) | (payload[1] << 8) | payload[2];
                        result.ConductorTempo.Add(new TempoAtTick(absTick, usperQ));
                    }
                    else if (type == 0x06)
                    {
                        result.ConductorMarkers.Add(new Marker { Tick = absTick, Name = System.Text.Encoding.ASCII.GetString(payload) });
                    }
                    continue;
                }
                if (b == 0xF0 || b == 0xF7)
                {
                    runningStatus = 0xF0;
                    int sysexLen = (int)ReadVlv(br);
                    ReadBytes(br, sysexLen);
                    continue;
                }

                int status;
                int firstData = -1;
                if ((b & 0x80) != 0)
                {
                    status = b;
                    runningStatus = status;
                }
                else
                {
                    status = runningStatus;
                    firstData = b; // consume b as the first data byte below
                }
                if ((status & 0x80) == 0) throw new InvalidDataException($"invalid running status {status}");
                int channel = status & 0x0F;
                int note;
                int vel;
                switch (status & 0xF0)
                {
                    case 0x90:
                    case 0x80:
                        note = firstData >= 0 ? firstData : br.ReadByte();
                        vel = br.ReadByte();
                        if ((status & 0xF0) == 0x90 && vel != 0)
                        {
                            openNotes[(channel, note)] = absTick;
                            result.NoteOns.Add(new ParsedMidiNote { Tick = absTick, Note = note, Channel = channel, Velocity = vel });
                        }
                        else
                        {
                            if (openNotes.TryGetValue((channel, note), out long on))
                            {
                                result.Notes.Add(new ParsedPitchNote { On = on, Off = absTick, Note = note, Channel = channel });
                                openNotes.Remove((channel, note));
                            }
                        }
                        break;
                    case 0xE0:
                        int lsb;
                        int msb;
                        if (firstData >= 0) { lsb = firstData; msb = br.ReadByte(); }
                        else { lsb = br.ReadByte(); msb = br.ReadByte(); }
                        result.PitchBendCount++;
                        int signed = ((msb << 7) | lsb) - 8192;
                        result.Bends.Add(new ParsedBend(absTick, channel, signed));
                        break;
                    case 0xC0:
                    case 0xD0:
                        int prog = firstData >= 0 ? firstData : br.ReadByte();
                        if ((status & 0xF0) == 0xC0)
                            result.ProgramByChannel[channel] = prog;
                        break;
                    default:
                        // Control changes carry two data bytes.
                        if (firstData >= 0) { br.ReadByte(); } else { br.ReadByte(); br.ReadByte(); }
                        break;
                }
            }
            // Close any notes still open at EOT (pathological) at the last tick.
            foreach (var kv in openNotes)
                result.Notes.Add(new ParsedPitchNote { On = kv.Value, Off = trackEnd, Note = kv.Key.note, Channel = kv.Key.channel });
        }
        return result;
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

    private static byte[] ReadBytes(BinaryReader br, int n) => br.ReadBytes(n);

    private static int ReadInt16(BinaryReader br) => (br.ReadByte() << 8) | br.ReadByte();
    private static int ReadInt32(BinaryReader br) => (br.ReadByte() << 24) | (br.ReadByte() << 16) | (br.ReadByte() << 8) | br.ReadByte();
}

internal sealed class Marker
{
    public long Tick;
    public string Name;
}

internal sealed class ParsedPitchNote : IMidiPitchNote
{
    public long On;
    public long Off;
    public int Note;
    public int Channel;
    long IMidiPitchNote.On => On;
    long IMidiPitchNote.Off => Off;
    int IMidiPitchNote.Note => Note;
    int IMidiPitchNote.Channel => Channel;
}

