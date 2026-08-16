using Fmp.Core.Midi;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using NoteOnEvent = Melanchall.DryWetMidi.Core.NoteOnEvent;
using NoteOffEvent = Melanchall.DryWetMidi.Core.NoteOffEvent;
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
        Assert.Contains(parsed.ConductorMarkers, m => m.Name == "SOURCE_LOOP_ENTRY");
        Assert.Contains(parsed.ConductorMarkers, m => m.Name == "SOURCE_LOOP_RESTART");
    }

    [Fact]
    public void Export_Markers_RestartBoundary_EmitsRawEntryAndRestartMarkers()
    {
        // Source entry and restart are raw capture positions. A restart is not a
        // colocated structural end/start pair.
        TimelineState tt = BuildTimeline(120, noteTiming: "on");

        ParsedMidi parsed = Parser.Parse(Export(tt));

        Marker entry = Assert.Single(
            parsed.ConductorMarkers.Where(m => m.Name == "SOURCE_LOOP_ENTRY"));
        Marker restart = Assert.Single(
            parsed.ConductorMarkers.Where(m => m.Name == "SOURCE_LOOP_RESTART"));
        Assert.NotEqual(entry.Tick, restart.Tick);
        Assert.DoesNotContain(parsed.ConductorMarkers, m => m.Name is "LOOP_START" or "LOOP_END");
        Marker? renderEnd = parsed.ConductorMarkers.FirstOrDefault(m => m.Name == "RENDER_END");
        Assert.NotNull(renderEnd);
        Assert.All(parsed.ConductorMarkers, m =>
            Assert.True(m.Tick <= renderEnd!.Tick, $"{m.Name} must not outlast RENDER_END"));
    }

    [Fact]
    public void Export_SourceSamplesKeepWallClockTimeAcrossMusicalBpms()
    {
        long start = Sr / 2;
        long end = start + Sr / 2;
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = Sr * 2,
            SampleRate = Sr,
            Notes = new[] { NewNote("v", start, end, 60) },
        };

        var exports = new[] { 56.0, 112.0, 173.0 }
            .Select(bpm => Parser.Parse(Export(new TimelineState
            {
                Timeline = timeline,
                FixedBpm = bpm,
            })))
            .ToArray();

        Assert.Equal(3, exports.Select(parsed => parsed.Notes[0].On).Distinct().Count());
        foreach (ParsedMidi parsed in exports)
        {
            TempoAtTick tempo = Assert.Single(parsed.ConductorTempo);
            double onSeconds = parsed.Notes[0].On * tempo.MicrosecondsPerQuarter / (Ppq * 1_000_000.0);
            double offSeconds = parsed.Notes[0].Off * tempo.MicrosecondsPerQuarter / (Ppq * 1_000_000.0);
            Assert.Equal((double)start / Sr, onSeconds, 5);
            Assert.Equal((double)end / Sr, offSeconds, 5);
        }
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
        var track = new global::Fmp.Core.Midi.MidiTrack { Name = "retrig", Endpoint = new global::Fmp.Core.Midi.MidiEndpoint(0, 0) };
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

    [Theory]
    [InlineData(-1.0)]
    [InlineData(128.0)]
    [InlineData(double.NaN)]
    public void Export_OutOfRangeInitialPitch_Suppressed_NoNoteOn(double pitch)
    {
        // INV5: a source pitch outside [0, 127] is meaningless MIDI garbage (the
        // AY period-2 ultrasonic init states the user classified as "audible MIDI
        // garbage"). The note is SUPPRESSED — never clamped, never wrapped — so no
        // melodic NoteOn may decode at all.
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = 100_000,
            SampleRate = Sr,
            Notes = new[] { NewNote("invalid", 1_000, 10_000, 60) with { InitialMidiNote = pitch } },
        };

        byte[] bytes = Export(new TimelineState { Timeline = timeline });
        ParsedMidi parsed = Parser.Parse(bytes);
        Assert.Empty(parsed.NoteOns);
    }

    [Fact]
    public void Export_BoundaryPitch_InRange_EncodesBoundaryBaseWithResidualBend()
    {
        // Encoder capability kept (INV5 boundary net): a REPRESENTABLE pitch near
        // the top of the domain (126.9) is encoded with the base pinned at the
        // nearest legal note (127) and the residual carried by the bend — the
        // decoded pitch still equals the source pitch and nothing clamps.
        const double pitch = 126.9;
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = 100_000,
            SampleRate = Sr,
            Notes = new[] { NewNote("invalid", 1_000, 10_000, 60) with { InitialMidiNote = pitch } },
        };

        byte[] bytes = Export(new TimelineState { Timeline = timeline });
        ParsedMidi parsed = Parser.Parse(bytes);
        int on = Assert.Single(parsed.NoteOns).Note;
        Assert.Equal(127, on);
        // The residual is carried as a bend (boundary base + excursion), never
        // dropped: without it the decoded pitch would clamp to the boundary key.
        ParsedBend initialBend = Assert.Single(parsed.Bends);
        Assert.InRange(initialBend.Bend, -8192, 8191);
        Assert.NotEqual(0, initialBend.Bend);
        Assert.All(parsed.Notes, n => Assert.True(n.Off > n.On, "no zero/negative-length artifacts"));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(128.0)]
    public void Export_SustainedOutOfRangePitchChange_Suppressed_NoNoteOn(double pitch)
    {
        // INV5: a pitch change to an out-of-domain pitch (60 -> -1 / 60 -> 128)
        // sustained for half the note would decode outside [0, 127]. The whole
        // note is suppressed — no clamping, no wrapping, nothing emitted.
        var note = NewNote("invalid", 1_000, 10_000, 60) with
        {
            Pitch = new[] { new PitchChange(5_000, 440, pitch) },
        };
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = 100_000,
            SampleRate = Sr,
            Notes = new[] { note },
        };

        byte[] bytes = Export(new TimelineState { Timeline = timeline });
        ParsedMidi parsed = Parser.Parse(bytes);
        Assert.Empty(parsed.NoteOns);
    }

    [Fact]
    public void Export_BoundarySlide_InRange_EncodesWithoutWrapping()
    {
        // The re-anchor machinery still works for LEGAL boundary-adjacent pitches:
        // a slide 60 -> 126.9 must not throw, must not wrap, and must land on a
        // boundary base (127) carrying the residual as an in-range bend.
        var note = NewNote("invalid", 1_000, 10_000, 60) with
        {
            Pitch = new[] { new PitchChange(5_000, 440, 126.9) },
        };
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = 100_000,
            SampleRate = Sr,
            Notes = new[] { note },
        };

        byte[] bytes = Export(new TimelineState { Timeline = timeline });
        ParsedMidi parsed = Parser.Parse(bytes);
        Assert.All(parsed.Bends, b => Assert.InRange(b.Bend, -8192, 8191));
        Assert.All(parsed.NoteOns, n => Assert.InRange(n.Note, 0, 127));
        Assert.Contains(parsed.NoteOns, n => n.Note == 127);
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
        // Default fixed bend range is 24 semitones; 0.3 ST -> 0.3/24*8191 ≈ +102.
        Assert.InRange(bend!.Bend, 90, 115);
        long noteOn = parsed.Notes[0].On;
        Assert.True(bend.Tick <= parsed.Notes[0].On, "cent correction bend must precede/coincide with note-on");
    }

    [Fact]
    public void Export_LongPitchContour_UsesOneNoteWithWideBendRange()
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

        // One source key-on is one MIDI note; the contour is represented by bends
        // and a domain-wide range rather than local note splitting.
        Assert.Single(parsed.Notes);
        Assert.True(parsed.Notes[0].Off > parsed.Notes[0].On);
        Assert.NotEmpty(parsed.Bends);
        // Every emitted bend stays within the selected representable range.
        Assert.All(parsed.Bends, bend => Assert.InRange(bend.Bend, -8192, 8191));
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

    [Fact]
    public void Export_NegativePickup_PreservedAfterGlobalShift()
    {
        // §54: first note at quarter -0.5, first downbeat at quarter 0. Sample zero is
        // one quarter BEFORE the first downbeat (beat 0 at sample spq => sample 0 is
        // quarter -1). After the global origin shift every tick must be nonnegative and
        // the pickup must stay 0.5 quarter before the downbeat, never snapped to it.
        double spq = Sr * 60.0 / 120.0;
        var notes = new[] { NewNote("v", (long)Math.Round(0.5 * spq), (long)Math.Round(0.5 * spq) + 2000, 60) };
        var beats = Enumerable.Range(0, 8)
            .Select(i => new BeatEvent((long)Math.Round((i + 1) * spq), i))
            .ToArray();
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = (long)Math.Round(8 * spq) + 20_000,
            SampleRate = Sr,
            Notes = notes,
            Beats = beats,
        };
        var state = new TimelineState { Timeline = timeline };

        var build = MusicalTimeMapBuilder.Build(state.Timeline, new MusicalTimeMapOptions
        {
            Meter = new Meter(4, 4),
            FirstDownbeatSample = (long)Math.Round(spq),
            DetectTempoChanges = true,
        });
        var exporter = new MusicalMidiExporter(build.Map, Ppq, new MusicalMidiExportOptions { EmitPitchBend = true })
        {
            Diagnostics = build.Diagnostics,
        };
        ParsedMidi parsed = Parser.Parse(exporter.Export(state.Timeline).Bytes);

        // Every exported note-on/off and bend tick is nonnegative (§21).
        Assert.All(parsed.Notes, n => Assert.True(n.On >= 0 && n.Off >= 0, "note ticks must be nonnegative"));
        Assert.All(parsed.Bends, b => Assert.True(b.Tick >= 0, "bend ticks must be nonnegative"));

        // The pickup sits exactly 0.5 quarter (480 ticks) before the FIRST_DOWNBEAT.
        long noteOn = parsed.Notes[0].On;
        Marker? downbeat = parsed.ConductorMarkers.FirstOrDefault(m => m.Name == "FIRST_DOWNBEAT");
        Assert.NotNull(downbeat);
        Assert.Equal(Ppq / 2, downbeat!.Tick - noteOn);
        // Half-quarter phase preserved — never snapped to a whole quarter/bar.
        Assert.Equal(Ppq / 2, noteOn % Ppq);
        Assert.NotEqual(0, noteOn % Ppq);
    }

    [Fact]
    public void Export_NoteCrossingTempoChange_IndependentEndpointTicks()
    {
        // §60: note starts quarter 15.5, ends quarter 16.5, tempo change at quarter 16.
        // Both endpoint ticks derive independently from their source samples. A
        // "startTick + converted duration" implementation would convert the second half
        // at the wrong tempo and land on 15744 instead of 15840.
        double spqA = Sr * 60.0 / 120.0;
        double spqB = Sr * 60.0 / 150.0;
        long boundary = (long)Math.Round(16 * spqA);
        var beats = new List<BeatEvent>();
        for (int i = 0; i < 16; i++)
            beats.Add(new BeatEvent((long)Math.Round(i * spqA), i));
        for (int i = 0; i < 16; i++)
            beats.Add(new BeatEvent(boundary + (long)Math.Round(i * spqB), 16 + i));
        var note = new NoteEvent(
            ChannelId: "v",
            StartSample: (long)Math.Round(15.5 * spqA),
            EndSample: boundary + (long)Math.Round(0.5 * spqB),
            InitialFrequencyHz: 440,
            InitialMidiNote: 60,
            InstrumentId: "inst",
            Mode: VisualizationNoteMode.Fm,
            IsRetrigger: false,
            Pitch: Array.Empty<PitchChange>());
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = boundary + (long)Math.Round(16 * spqB) + 10_000,
            SampleRate = Sr,
            Notes = new[] { note },
            Beats = beats.ToArray(),
            Timing = new[] { new DriverTimingEvent(boundary, 0, 150.0) },
        };
        var state = new TimelineState { Timeline = timeline };
        ParsedMidi parsed = Parser.Parse(Export(state));

        ParsedPitchNote n = parsed.Notes[0];
        Assert.Equal((long)Math.Round(15.5 * Ppq), n.On); // 14880
        Assert.Equal((long)Math.Round(16.5 * Ppq), n.Off); // 15840
        Assert.True(n.Off > n.On);
    }

    [Fact]
    public void Export_PitchAtTempoBoundary_CorrectTickAndNoDiscontinuity()
    {
        // §61: a pitch change whose sample exactly equals the tempo-segment boundary
        // must land on the correct absolute tick with no one-tick discontinuity.
        double spqA = Sr * 60.0 / 120.0;
        double spqB = Sr * 60.0 / 150.0;
        long boundary = (long)Math.Round(16 * spqA);
        var beats = new List<BeatEvent>();
        for (int i = 0; i < 16; i++)
            beats.Add(new BeatEvent((long)Math.Round(i * spqA), i));
        for (int i = 0; i < 16; i++)
            beats.Add(new BeatEvent(boundary + (long)Math.Round(i * spqB), 16 + i));
        long start = (long)Math.Round(15 * spqA);
        var change = new PitchChange(boundary, 440 * Math.Pow(2, 1.0 / 12), 61.0);
        var note = new NoteEvent(
            "v", start, boundary + (long)Math.Round(2 * spqB), 440, 60,
            "inst", VisualizationNoteMode.Fm, false, new[] { change });
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = boundary + (long)Math.Round(4 * spqB) + 10_000,
            SampleRate = Sr,
            Notes = new[] { note },
            Beats = beats.ToArray(),
            Timing = new[] { new DriverTimingEvent(boundary, 0, 150.0) },
        };
        var state = new TimelineState { Timeline = timeline };
        ParsedMidi parsed = Parser.Parse(Export(state));

        // The boundary bend maps to exactly quarter 16 => tick 16*PPQ (no offset).
        Assert.Contains(parsed.Bends, b => b.Tick == 16 * Ppq);
        // The note starts exactly on its base note (bend 0) at quarter 15, so no
        // note-start bend is emitted — only the boundary bend exists (no discontinuity).
        Assert.Single(parsed.Bends);
        Assert.Equal(16 * Ppq, parsed.Bends[0].Tick);
    }

    [Fact]
    public void Export_PositiveDurationRoundsToSameTick_MinimumDuration()
    {
        // §29/§62: EndSample > StartSample but both map to one integer MIDI tick =>
        // endTick forced to startTick + 1 (a per-note minimum, never a global
        // quantization of surrounding events).
        double spq = Sr * 60.0 / 120.0; // one tick ≈ 22050/960 ≈ 22.97 samples
        long start = (long)Math.Round(spq); // quarter 1 => tick 960
        var note = NewNote("v", start, start + 5, 60); // 5 samples later => still tick 960
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = start + 20_000,
            SampleRate = Sr,
            Notes = new[] { note },
            Beats = new[] { new BeatEvent(0, 0.0), new BeatEvent((long)Math.Round(spq), 1.0) },
        };
        var state = new TimelineState { Timeline = timeline };
        ParsedMidi parsed = Parser.Parse(Export(state));

        Assert.Single(parsed.Notes);
        Assert.Equal(960, parsed.Notes[0].On);
        Assert.Equal(parsed.Notes[0].On + 1, parsed.Notes[0].Off);
    }

    [Fact]
    public void Export_SameVoiceRetrigger_SameTick_NoteOffBeforeNoteOn()
    {
        // §63: same voice/pitch, old note ends tick 960, new begins tick 960 => the
        // encoded order is Note-Off then Note-On (explicit, never insertion/hash
        // dependent). Exact origin 0 because beat 0 is at sample 0.
        double spq = Sr * 60.0 / 120.0;
        var notes = new[]
        {
            NewNote("v", 0, (long)Math.Round(spq), 60), // quarter 0→1, off at 960
            NewNote("v", (long)Math.Round(spq), (long)Math.Round(2 * spq), 60) with { IsRetrigger = true }, // on at 960 (real retrigger: INV3 boundary)
        };
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = (long)Math.Round(3 * spq) + 10_000,
            SampleRate = Sr,
            Notes = notes,
            Beats = new[] { new BeatEvent(0, 0.0), new BeatEvent((long)Math.Round(spq), 1.0) },
        };
        ParsedMidi parsed = Parser.Parse(Export(new TimelineState { Timeline = timeline }));

        // Exactly one note-on sits at tick 960 (the old note's off is a 0x80 there).
        Assert.Equal(1, parsed.NoteOns.Count(n => n.Tick == 960));

        // Walk raw statuses on the single musical track; note-off (0x80) precedes
        // note-on (0x90) at tick 960.
        byte[] bytes = Export(new TimelineState { Timeline = timeline });
        var occurrences = WalkNoteStatus(bytes).Where(o => o.Tick == 960).ToList();
        Assert.Equal(2, occurrences.Count);
        Assert.Equal(0x80, occurrences[0].Status);
        Assert.Equal(0x90, occurrences[1].Status);
    }

    [Fact]
    public void TwoSourceAttacks_SameMidiTick_BothSurvive()
    {
        // Spec §2: two source attacks at the SAME sample round to one MIDI tick.
        // Legacy behavior suppressed the second via (track, tick) dedup; both
        // NoteOns must now survive and the receipt must report the collision as
        // informational with zero dropped attacks.
        var notes = new[]
        {
            NewNote("v", 0, 1000, 60),
            NewNote("v", 0, 2000, 62),
        };
        MidiSemanticDecoder.Result decoded = Decode(notes);

        Assert.Equal(2, CountNoteOns(decoded, 0));
        Melanchall.DryWetMidi.Common.SevenBitNumber attack60 = (Melanchall.DryWetMidi.Common.SevenBitNumber)60;
        Melanchall.DryWetMidi.Common.SevenBitNumber attack62 = (Melanchall.DryWetMidi.Common.SevenBitNumber)62;
        Assert.Contains(decoded.Events.Values.SelectMany(e => e), e
            => e.Event is NoteOnEvent { NoteNumber: var note60 } && note60 == attack60);
        Assert.Contains(decoded.Events.Values.SelectMany(e => e), e
            => e.Event is NoteOnEvent { NoteNumber: var note62 } && note62 == attack62);
    }

    [Fact]
    public void Retrigger_SameTick_IsNotDeduplicated()
    {
        // Spec §2: a monophonic retrigger at one tick must decode as previous
        // NoteOff → new NoteOn, and the attack must NOT be dropped.
        double spq = Sr * 60.0 / 120.0;
        var notes = new[]
        {
            NewNote("v", 0, (long)Math.Round(spq), 60),
            NewNote("v", (long)Math.Round(spq), (long)Math.Round(spq) + 1000, 60) with { IsRetrigger = true },
        };
        MidiSemanticDecoder.Result decoded = Decode(notes);

        long retriggerTick = decoded.Events.Values.SelectMany(e => e)
            .First(e => e.Event is NoteOffEvent).Tick;
        var atTick = decoded.Events.Values.SelectMany(e => e)
            .Where(e => e.Tick == retriggerTick).ToList();
        Assert.Equal(2, atTick.Count);
        Assert.IsType<NoteOffEvent>(atTick[0].Event);
        Assert.IsType<NoteOnEvent>(atTick[1].Event);
        // The retrigger attack arrives as its own NoteOn — never suppressed.
        Assert.Equal(2, CountAllNoteOns(decoded));
    }

    [Fact]
    public void InstrumentChange_SameTick_IsNotDeduplicated()
    {
        // Spec §2: two sequential instruments reusing one physical voice, second
        // attack on the same tick as the first — both source NoteOns survive.
        var notes = new[]
        {
            NewNote("v", 0, 1000, 60) with { InstrumentId = "inst-a" },
            NewNote("v", 0, 2000, 62) with { InstrumentId = "inst-b" },
        };
        MidiSemanticDecoder.Result decoded = Decode(notes);

        Assert.Equal(2, CountNoteOns(decoded, 0));
    }

    [Fact]
    public void DistinctSourceSamples_RoundingToSameTick_ArePreserved()
    {
        // Spec §2: two attacks at DISTINCT source samples that still round to the
        // same MIDI tick (10 samples < half tick at 120 BPM / 960 PPQ) — both must
        // survive; sample identity is never collapsed into one MIDI NoteOn.
        var notes = new[]
        {
            NewNote("v", 0, 1000, 60),
            NewNote("v", 10, 2000, 62),
        };
        MidiSemanticDecoder.Result decoded = Decode(notes);

        Assert.Equal(2, CountNoteOns(decoded, 0));
    }

    [Fact]
    public void AttackCounters_DroppedSourceAttacks_AlwaysZero()
    {
        // Spec §2/§11 invariant: droppedSourceAttacks == 0 in normal export and all
        // counters are populated (receipt aliases sourceNotes / initialMidiNoteOns /
        // sameTickAttackCollisions / droppedSourceAttacks).
        double spq = Sr * 60.0 / 120.0;
        var notes = new[]
        {
            NewNote("v", 0, 1000, 60),
            NewNote("v", 0, 2000, 62),
            NewNote("v2", (long)Math.Round(spq), (long)Math.Round(spq) + 1000, 64),
        };
        SourceAttackCounters counters = ExportResult(notes).AttackCounters;

        Assert.Equal(3, counters.SourceNoteCount);
        Assert.Equal(3, counters.InitialNoteOnCount);
        Assert.Equal(1, counters.SameTickAttackCollisions);
        Assert.Equal(0, counters.DroppedSourceAttacks);
    }

    [Fact]
    public void Export_UnknownMeter_OmitsTimeSignature()
    {
        // §64: excellent beat/tempo anchors but no bar information => correct beat
        // alignment and NO fabricated 4/4 Time Signature.
        double spq = Sr * 60.0 / 120.0;
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = (long)Math.Round(8 * spq) + 20_000,
            SampleRate = Sr,
            Notes = new[] { NewNote("v", (long)Math.Round(spq), (long)Math.Round(spq) + 2000, 60) },
            Beats = Enumerable.Range(0, 40).Select(i => new BeatEvent((long)Math.Round(i * spq), i)).ToArray(),
        };
        var state = new TimelineState { Timeline = timeline };
        ParsedMidi parsed = Parser.Parse(ExportNoMeter(state));
        Assert.Empty(parsed.TimeSignatures);
    }

    [Fact]
    public void Export_KnownMeter_EmitsTimeSignatureWithBarPhase()
    {
        // §65: authoritative 4/4 + firstDownbeatSample => valid Time Signature with the
        // correct bar phase after the origin shift; note timing is NOT changed to force
        // a convenient bar 1.
        double spq = Sr * 60.0 / 120.0;
        var beats = Enumerable.Range(0, 8)
            .Select(i => new BeatEvent((long)Math.Round((i + 1) * spq), i))
            .ToArray();
        var note = NewNote("v", (long)Math.Round(0.5 * spq), (long)Math.Round(0.5 * spq) + 2000, 60);
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = (long)Math.Round(8 * spq) + 20_000,
            SampleRate = Sr,
            Notes = new[] { note },
            Beats = beats,
        };
        var state = new TimelineState { Timeline = timeline };
        var build = MusicalTimeMapBuilder.Build(state.Timeline, new MusicalTimeMapOptions
        {
            Meter = new Meter(4, 4),
            FirstDownbeatSample = (long)Math.Round(spq),
            DetectTempoChanges = true,
        });
        var exporter = new MusicalMidiExporter(build.Map, Ppq, new MusicalMidiExportOptions { EmitPitchBend = true })
        {
            Diagnostics = build.Diagnostics,
        };
        ParsedMidi parsed = Parser.Parse(exporter.Export(state.Timeline).Bytes);

        Assert.Single(parsed.TimeSignatures);
        Assert.Equal(4, parsed.TimeSignatures[0].Numerator);
        Assert.Equal(4, parsed.TimeSignatures[0].Denominator);

        // Bar phase: sample0 = quarter -1 (pickup), so after the bar-aligned origin the
        // pickup must stay half a quarter before the downbeat — not snapped to bar 1.
        long noteOn = parsed.Notes[0].On;
        Marker? downbeat = parsed.ConductorMarkers.FirstOrDefault(m => m.Name == "FIRST_DOWNBEAT");
        Assert.NotNull(downbeat);
        Assert.Equal(Ppq / 2, downbeat!.Tick - noteOn);
        Assert.True(noteOn > 0);
    }

    [Fact]
    public void Export_LoopMarker_OffBeat_PreservesExactTick()
    {
        // §66/§40: loop markers map through the same map + origin and are never
        // quantized — an off-beat marker keeps its exact recovered musical position.
        double spq = Sr * 60.0 / 120.0;
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = (long)Math.Round(16 * spq) + 20_000,
            SampleRate = Sr,
            Notes = new[] { NewNote("v", (long)Math.Round(4 * spq), (long)Math.Round(4 * spq) + 2000, 60) },
            Beats = Enumerable.Range(0, 40).Select(i => new BeatEvent((long)Math.Round(i * spq), i)).ToArray(),
            LoopMarkers = new[]
            {
                new LoopMarker((long)Math.Round(4.5 * spq), LoopMarkerKind.Start, 0),   // quarter 4.5 off-beat
                new LoopMarker((long)Math.Round(8 * spq), LoopMarkerKind.Restart, 0),   // quarter 8 on-beat
            },
        };
        var state = new TimelineState { Timeline = timeline };
        ParsedMidi parsed = Parser.Parse(Export(state));

        Marker? start = parsed.ConductorMarkers.FirstOrDefault(m => m.Name == "SOURCE_LOOP_ENTRY");
        Marker? end = parsed.ConductorMarkers.FirstOrDefault(m => m.Name == "SOURCE_LOOP_RESTART");
        Assert.NotNull(start);
        Assert.NotNull(end);
        Assert.Equal((long)(8 * Ppq), end!.Tick);     // 7680 — on-beat
        Assert.Equal(Ppq / 2, start!.Tick % Ppq);
    }

    [Fact]
    public void Export_MarkerAndPitchEarlierThanFirstNote_AllTicksNonnegative()
    {
        // §21 regression: the global origin offset must cover EVERY event family —
        // loop markers and pitch changes included — not just notes/rhythm/first-sample.
        // A loop marker at sample 0 and a pitch change before the first note sit in the
        // negative pickup phase (sample 0 = quarter -1 when the first downbeat is at
        // sample spq). After export every note, bend, and marker tick must be
        // nonnegative — no family may clamp a negative delta.
        double spq = Sr * 60.0 / 120.0;
        long noteStart = (long)Math.Round(2 * spq);          // quarter 2
        long pitchAt = (long)Math.Round(0.75 * spq);         // quarter -0.25, BEFORE the note
        long S = (long)Math.Round(0.5 * spq);                // loop marker at quarter -0.5
        var note = new NoteEvent(
            ChannelId: "v",
            StartSample: noteStart,
            EndSample: noteStart + (long)Math.Round(1 * spq),
            InitialFrequencyHz: 440,
            InitialMidiNote: 60,
            InstrumentId: "inst",
            Mode: VisualizationNoteMode.Fm,
            IsRetrigger: false,
            Pitch: new[] { new PitchChange(pitchAt, 440 * Math.Pow(2, 1.0 / 12), 61.0) });
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = (long)Math.Round(8 * spq) + 20_000,
            SampleRate = Sr,
            Notes = new[] { note },
            Beats = Enumerable.Range(0, 8)
                .Select(i => new BeatEvent((long)Math.Round((i + 1) * spq), i))
                .ToArray(),
            LoopMarkers = new[] { new LoopMarker(S, LoopMarkerKind.Start, 0) },
        };
        var state = new TimelineState { Timeline = timeline };
        var build = MusicalTimeMapBuilder.Build(state.Timeline, new MusicalTimeMapOptions
        {
            Meter = new Meter(4, 4),
            FirstDownbeatSample = (long)Math.Round(spq),
            DetectTempoChanges = true,
        });
        var exporter = new MusicalMidiExporter(build.Map, Ppq, new MusicalMidiExportOptions { EmitPitchBend = true })
        {
            Diagnostics = build.Diagnostics,
        };
        ParsedMidi parsed = Parser.Parse(exporter.Export(state.Timeline).Bytes);

        // The earliest exported family is the loop marker at quarter -0.5; with a
        // bar-conserving origin it must map to a nonnegative tick and cap the shift so
        // no family lands negative.
        Assert.Contains(parsed.ConductorMarkers, m => m.Name == "SOURCE_LOOP_ENTRY");
        Assert.All(parsed.ConductorMarkers, m => Assert.True(m.Tick >= 0, $"marker {m.Name} tick must be nonnegative"));
        // No pitch bend (or note) may clamp a negative delta.
        Assert.Contains(parsed.ConductorMarkers, m => m.Name == "SOURCE_LOOP_ENTRY");
        Assert.All(parsed.Bends, b => Assert.True(b.Tick >= 0, "bend ticks must be nonnegative"));
        // A pitch change BEFORE the note start is ignored (Patch B: fold only from
        // the note's own start onward); the note itself is the event that must land
        // on a nonnegative tick, keeping every family within the shifted origin.
        Assert.True(parsed.Notes[0].On >= 0);
    }

    [Fact]
    public void Export_MarkersDisabled_DownbeatBeforeMapStart_DoesNotShiftOrigin()
    {
        // P2 (cycle 2): ComputeOriginOffset must include the first-downbeat/secondary
        // marker elements ONLY when those events are actually emitted (EmitMarkers).
        // Here a first-downbeat marker sits four quarters BEFORE the map start (sample
        // zero = quarter 0) and a loop marker precedes the first note. With markers
        // disabled they are NOT emitted, so they must not push the origin back — the
        // first Set Tempo must land on tick 0, leaving no leading ticks at the DAW's
        // default tempo.
        var map = new MusicalTimeMap(44100, 0, new[]
        {
            new TempoSegment(0, 100_000, 0.0, 22050, 120, TimingSource.DriverBeatAnchors, 1.0),
        }, meter: null, firstDownbeatQuarter: -4.0);
        var timeline = new VisualizationTimeline
        {
            SampleRate = 44100,
            StartSample = 0,
            EndSample = 200_000,
            Notes = new[] { NewNote("v", (long)Math.Round(5000.0), (long)Math.Round(5000.0) + 4000, 60) },
            LoopMarkers = new[] { new LoopMarker(2000, LoopMarkerKind.Start, 0) }, // before the first note
        };
        var exporter = new MusicalMidiExporter(map, Ppq, new MusicalMidiExportOptions { EmitPitchBend = true, EmitMarkers = false });
        ParsedMidi parsed = Parser.Parse(exporter.Export(timeline).Bytes);

        // No markers emitted.
        Assert.Empty(parsed.ConductorMarkers);
        // The origin is unchanged by the excluded downbeat/marker: the first Set
        // Tempo lands exactly on tick 0 (no leading default-tempo ticks).
        Assert.NotEmpty(parsed.ConductorTempo);
        Assert.Equal(0, parsed.ConductorTempo[0].Tick);
    }

    [Fact]
    public void Export_MarkersEnabled_DownbeatBeforeMapStart_OriginStillCoversIt()
    {
        // Mirror of the P2 case: with EmitMarkers=true the first-downbeat marker IS
        // emitted, so the origin must still cover it — the downbeat (quarter -4) maps
        // back to tick 0 rather than a negative tick, and the conductor is shifted by
        // exactly the downbeat's four quarters.
        var map = new MusicalTimeMap(44100, 0, new[]
        {
            new TempoSegment(0, 100_000, 0.0, 22050, 120, TimingSource.DriverBeatAnchors, 1.0),
        }, meter: null, firstDownbeatQuarter: -4.0);
        var timeline = new VisualizationTimeline
        {
            SampleRate = 44100,
            StartSample = 0,
            EndSample = 200_000,
            Notes = new[] { NewNote("v", (long)Math.Round(5000.0), (long)Math.Round(5000.0) + 4000, 60) },
            LoopMarkers = new[] { new LoopMarker(2000, LoopMarkerKind.Start, 0) },
        };
        var exporter = new MusicalMidiExporter(map, Ppq, new MusicalMidiExportOptions { EmitPitchBend = true, EmitMarkers = true });
        ParsedMidi parsed = Parser.Parse(exporter.Export(timeline).Bytes);

        Marker? downbeat = parsed.ConductorMarkers.FirstOrDefault(m => m.Name == "FIRST_DOWNBEAT");
        Assert.NotNull(downbeat);
        Assert.Equal(0, downbeat!.Tick); // -4 quarters → tick 0 via minimal integer shift, never negative
        // The first Set Tempo is a setup event that stays at logical tick 0
        // (Patch C.2), so there is no leading default-tempo span; the SOURCE_START
        // marker carries the origin shift.
        Assert.NotEmpty(parsed.ConductorTempo);
        Assert.Equal(0, parsed.ConductorTempo[0].Tick);
        Marker? sourceStart = parsed.ConductorMarkers.FirstOrDefault(m => m.Name == "SOURCE_START");
        Assert.NotNull(sourceStart);
        Assert.Equal(4 * Ppq, sourceStart!.Tick);
    }

    [Fact]
    public void Export_PitchBendDisabled_EarlyPitchChangeDoesNotShiftOrigin()
    {
        // P2 (final, definitive): ComputeOriginOffset must derive the global origin
        // ONLY from families that will actually be emitted. Here EmitPitchBend=false,
        // so no bend is ever serialized; a pitch change sitting before the map's
        // start sample must therefore not be folded into the origin. Pre-fix it was —
        // pushing the pitch's sample through SampleToQuarterPosition of a sample
        // before the first segment (which threw), and in any case dragging the origin
        // away from the first real event. With bends disabled the first Set Tempo must
        // land exactly on tick 0.
        var map = new MusicalTimeMap(44100, 0, new[]
        {
            new TempoSegment(0, 100_000, 0.0, 22050, 120, TimingSource.DriverBeatAnchors, 1.0),
        }, meter: null, firstDownbeatQuarter: null);
        var note = new NoteEvent(
            ChannelId: "v",
            StartSample: 0,
            EndSample: 5000,
            InitialFrequencyHz: 440,
            InitialMidiNote: 60,
            InstrumentId: "inst",
            Mode: VisualizationNoteMode.Fm,
            IsRetrigger: false,
            Pitch: new[] { new PitchChange(-1000, 440 * Math.Pow(2, 1.0 / 12), 61.0) }); // before map start
        var timeline = new VisualizationTimeline
        {
            SampleRate = 44100,
            StartSample = 0,
            EndSample = 200_000,
            Notes = new[] { note },
        };
        var exporter = new MusicalMidiExporter(map, Ppq, new MusicalMidiExportOptions { EmitPitchBend = false });
        ParsedMidi parsed = Parser.Parse(exporter.Export(timeline).Bytes);

        // No bends are emitted and the origin is untouched: the first Set Tempo lands
        // exactly on tick 0 (no leading default-tempo ticks).
        Assert.Empty(parsed.Bends);
        Assert.NotEmpty(parsed.ConductorTempo);
        Assert.Equal(0, parsed.ConductorTempo[0].Tick);
    }

    [Fact]
    public void Export_SkippedNote_EarlyPitchChangeDoesNotShiftOrigin()
    {
        // P2 (final, definitive): a note Export drops (EndSample <= StartSample) emits
        // no note and no bend, so neither its start sample nor its pitch changes may
        // contribute to the global origin. Pre-fix the skipped note's early pitch was
        // still scanned and dragged the origin (a sample before the map's first segment
        // even threw through SampleToQuarterPosition). With the note skipped the first
        // Set Tempo must land exactly on tick 0.
        var map = new MusicalTimeMap(44100, 0, new[]
        {
            new TempoSegment(0, 100_000, 0.0, 22050, 120, TimingSource.DriverBeatAnchors, 1.0),
        }, meter: null, firstDownbeatQuarter: null);
        var note = new NoteEvent(
            ChannelId: "v",
            StartSample: 0,
            EndSample: 0, // EndSample == StartSample => skipped by Export
            InitialFrequencyHz: 440,
            InitialMidiNote: 60,
            InstrumentId: "inst",
            Mode: VisualizationNoteMode.Fm,
            IsRetrigger: false,
            Pitch: new[] { new PitchChange(-1000, 440 * Math.Pow(2, 1.0 / 12), 61.0) }); // before map start
        var timeline = new VisualizationTimeline
        {
            SampleRate = 44100,
            StartSample = 0,
            EndSample = 200_000,
            Notes = new[] { note },
        };
        var exporter = new MusicalMidiExporter(map, Ppq, new MusicalMidiExportOptions { EmitPitchBend = true });
        ParsedMidi parsed = Parser.Parse(exporter.Export(timeline).Bytes);

        // The skipped note emits nothing; the origin is untouched and the first Set
        // Tempo lands exactly on tick 0.
        Assert.Empty(parsed.Notes);
        Assert.Empty(parsed.Bends);
        Assert.NotEmpty(parsed.ConductorTempo);
        Assert.Equal(0, parsed.ConductorTempo[0].Tick);
    }

    [Fact]
    public void Export_ExcludedRhythm_EarlyTriggerDoesNotShiftOrigin()
    {
        // P2 (final, definitive): ComputeOriginOffset must derive the global origin
        // ONLY from rhythm triggers that will actually be emitted. Export/BuildTracks
        // drop a rhythm channel whose VoiceExportOverride.Include=false (no track, no
        // events), so its triggers must not contribute to the origin either. Pre-fix
        // the excluded channel's early trigger was still min()ed and dragged the origin
        // forward into the emitted note/conductor (a sample before the map's first
        // segment even yields a negative quarter), delaying the first real event/tempo
        // off tick 0. With the channel excluded the first Set Tempo must land exactly
        // on tick 0.
        var map = new MusicalTimeMap(44100, 10000, new[]
        {
            new TempoSegment(10000, 100_000, 0.0, 50000, 120, TimingSource.DriverBeatAnchors, 1.0),
        }, meter: null, firstDownbeatQuarter: null);
        var timeline = new VisualizationTimeline
        {
            SampleRate = 44100,
            StartSample = 0,
            EndSample = 100_000,
            // Emitted melodic note, later in the map.
            Notes = new[] { NewNote("v", 20000, 24000, 60) },
            // Excluded rhythm channel whose trigger precedes every emitted event.
            Rhythm = new[] { new RhythmEvent("excluded", "excl", 0, 1.0f, 0f) },
        };
        var exporter = new MusicalMidiExporter(map, Ppq, new MusicalMidiExportOptions
        {
            EmitPitchBend = true,
            VoiceOverrides = new[] { new VoiceExportOverride("excl") { Include = false } },
        });
        ParsedMidi parsed = Parser.Parse(exporter.Export(timeline).Bytes);

        // The excluded rhythm emits no percussion; the origin is untouched and the first
        // Set Tempo lands exactly on tick 0 (no leading default-tempo ticks).
        Assert.Empty(parsed.NoteOns.Where(n => n.Note == 36));
        Assert.NotEmpty(parsed.ConductorTempo);
        Assert.Equal(0, parsed.ConductorTempo[0].Tick);
    }

    [Fact]
    public void Export_SameSample_MelodicRhythmAndMapShareTick()
    {
        // §67 (melodic + rhythm legs; DAC leg owned by WP05): a melodic NoteEvent and a
        // rhythm trigger at the same sample S must map through the SAME MusicalTimeMap +
        // origin to the same musical tick — no independent samples-per-tick math. The
        // shared map's SampleToTick is asserted equal, which is the invariant a DAC
        // trigger routed through the same map must also satisfy (§37).
        double spq = Sr * 60.0 / 120.0;
        long S = (long)Math.Round(8 * spq);
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = (long)Math.Round(16 * spq) + 20_000,
            SampleRate = Sr,
            Notes = new[] { NewNote("v", S, S + 4000, 60) },
            Rhythm = new[] { new RhythmEvent("bd", "rhythm.bd", S, 1.0f, 0f) },
            Beats = Enumerable.Range(0, 40).Select(i => new BeatEvent((long)Math.Round(i * spq), i)).ToArray(),
        };
        var state = new TimelineState { Timeline = timeline };
        var build = MusicalTimeMapBuilder.Build(state.Timeline, new MusicalTimeMapOptions
        {
            Meter = new Meter(4, 4),
            DetectTempoChanges = true,
        });
        var exporter = new MusicalMidiExporter(build.Map, Ppq, new MusicalMidiExportOptions { EmitPitchBend = true })
        {
            Diagnostics = build.Diagnostics,
        };
        ParsedMidi parsed = Parser.Parse(exporter.Export(state.Timeline).Bytes);

        ParsedPitchNote melodic = parsed.Notes.First(n => n.Channel == 1);
        long noteOn = melodic.On;
        Assert.Equal(8 * Ppq, noteOn); // melodic note at quarter 8 via shared map

        // Rhythm trigger at the same sample => same tick (shared map, no per-family math).
        var rhythmOn = parsed.NoteOns.FirstOrDefault(n => n.Note == 36); // first rhythm voice
        Assert.NotNull(rhythmOn);
        Assert.Equal(noteOn, rhythmOn!.Tick);

        // The shared map yields the same tick for any event routed at sample S (§37).
        Assert.Equal(noteOn, build.Map.SampleToTick(S, Ppq));
    }

    [Fact]
    public void Export_AdjacentSegments_SameMicrosecondsPerQuarter_SingleTempoEvent()
    {
        // §19: adjacent tempo segments whose emitted µs/qn value (the MIDI integer, not
        // raw BPM) is identical emit ONE Set Tempo event.
        var map = new MusicalTimeMap(44100, 0, new[]
        {
            new TempoSegment(0, 1000, 0.0, 22050, 120, TimingSource.DriverBeatAnchors, 1.0),
            new TempoSegment(1000, 2000, 1000.0 / 22050, 22050, 120, TimingSource.DriverBeatAnchors, 1.0),
        }, meter: new Meter(4, 4));
        var timeline = new VisualizationTimeline
        {
            SampleRate = 44100,
            StartSample = 0,
            EndSample = 3000,
            Notes = new[] { NewNote("v", 0, 500, 60) },
        };
        var exporter = new MusicalMidiExporter(map, Ppq, new MusicalMidiExportOptions { EmitPitchBend = true });
        ParsedMidi parsed = Parser.Parse(exporter.Export(timeline).Bytes);

        Assert.Single(parsed.ConductorTempo);
        Assert.Equal(500_000, parsed.ConductorTempo[0].MicrosecondsPerQuarter);
    }

    [Fact]
    public void Export_BendRangeRpn_EmitsFixedRangeOnEachMelodicTrack()
    {
        // Fixed bounded bend range (Patch B): RPN setup is emitted once per melodic
        // track that emits bends, at the CONFIGURED range — never auto-expanded to a
        // per-domain maximum.
        double spq = Sr * 60.0 / 120.0;
        var notes = new[]
        {
            new NoteEvent("v", (long)Math.Round(spq), (long)Math.Round(spq) + 20_000, 440, 60.3,
                "inst", VisualizationNoteMode.Fm, false, Array.Empty<PitchChange>()),
            new NoteEvent("v", (long)Math.Round(2 * spq), (long)Math.Round(2 * spq) + 20_000, 440, 60.3,
                "inst2", VisualizationNoteMode.Fm, false,
                new[] { new PitchChange((long)Math.Round(2 * spq) + 5_000, 440 * Math.Pow(2, 10.0 / 12), 70.0) }),
        };
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = (long)Math.Round(6 * spq) + 20_000,
            SampleRate = Sr,
            Notes = notes,
            Beats = BuildBeats(120),
        };
        var build = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions { Meter = new Meter(4, 4), DetectTempoChanges = true });
        var exporter = new MusicalMidiExporter(build.Map, Ppq, new MusicalMidiExportOptions { EmitPitchBend = true, BendRangeSemitones = 24 })
        {
            Diagnostics = build.Diagnostics,
        };
        MusicalMidiExportResult result = exporter.Export(timeline);

        var rangeEvents = result.Tracks.SelectMany(t => t.Events).OfType<MidiBendRangeEvent>().ToList();
        // Both notes share one placeholder track (same channel, non-canonical
        // instrument tokens collapse to a single per-channel track), so exactly one
        // RPN setup at the FIXED configured range (24) is emitted.
        Assert.Single(rangeEvents);
        Assert.Equal(24, rangeEvents[0].Semitones);
    }

    [Fact]
    public void Export_BendOffset_UsesDomainRangeWithoutWrapping()
    {
        // A 20-semitone jump selects a domain-wide range rather than re-articulating
        // the source note. Every bend remains representable and the one MIDI note
        // preserves the source key-on/key-off boundary.
        double spq = Sr * 60.0 / 120.0;
        long start = (long)Math.Round(spq);
        var changes = new[] { new PitchChange(start + 5000, 440 * Math.Pow(2, 20.0 / 12), 80.0) };
        var note = new NoteEvent("v", start, start + 60_000, 440, 60,
            "inst", VisualizationNoteMode.Fm, false, changes);
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = start + 100_000,
            SampleRate = Sr,
            Notes = new[] { note },
        };
        var state = new TimelineState { Timeline = timeline };
        ParsedMidi parsed = Parser.Parse(Export(state));

        Assert.All(parsed.Bends, b => Assert.InRange(b.Bend, -8192, 8191));
        Assert.Single(parsed.Notes);
        Assert.Equal(60, parsed.NoteOns.Single().Note);
    }

    /* ---------- helpers ---------- */

    private sealed class TimelineState
    {
        public VisualizationTimeline Timeline;
        public string Quantize = "off";
        public double? FixedBpm;
    }

    // ── Phase 5: GM drum remap gate (spec §8; D6, D7) ────────────────────────────

    private static FmOperatorDefinition GateOp(int ar, int sl, int sr, int rr, int tl = 30) =>
        new(ar, sr, sr, rr, sl, tl, 0, 1, 0, AmplitudeModulation: false, SsgEnvelope: 0);

    private static InstrumentDefinition GateInstrument(string id, int algorithm, params FmOperatorDefinition[] ops) =>
        new(id, "Fm", algorithm, Feedback: 0, Ams: null, Fms: null, ops);

    /// <summary>Fast-attack decaying transient: percussive, role from the shared
    /// vocabulary, confidence computed by <see cref="FmPercussionClassifier"/>.</summary>
    private static NoteEvent GateNote(string channelId, string instrumentId, int midi) =>
        new(
            ChannelId: channelId,
            StartSample: 4400,
            EndSample: 4400 + 1323, // 30 ms gate at 44.1 kHz → short
            InitialFrequencyHz: 261.6,
            InitialMidiNote: midi,
            InstrumentId: instrumentId,
            Mode: VisualizationNoteMode.Fm,
            IsRetrigger: true,
            Pitch: Array.Empty<PitchChange>());

    /// <summary>Exports a timeline carrying FM notes + their instrument table and
    /// returns the bytes plus the unified percussion evidence the map built for
    /// them (the exporter consumed that SAME collection — D3 relay, never a
    /// builder re-invocation).</summary>
    private static byte[] ExportWithDrumEvidence(
        NoteEvent[] notes, InstrumentDefinition[] instruments, out IReadOnlyList<PercussiveOnset> evidence)
    {
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = notes.Max(n => n.EndSample) + 10_000,
            SampleRate = Sr,
            Notes = notes,
            Instruments = instruments,
            Beats = BuildBeats(120),
        };
        var build = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions
        {
            FixedBpm = 120,
            Meter = new Meter(4, 4),
            DetectTempoChanges = true,
        });
        evidence = build.PercussionEvidence;
        var exporter = new MusicalMidiExporter(build.Map, Ppq, new MusicalMidiExportOptions
        {
            EmitPitchBend = true,
            PercussionEvidence = build.PercussionEvidence,
        })
        {
            Diagnostics = build.Diagnostics,
        };
        return exporter.Export(timeline).Bytes;
    }

    [Fact]
    public void Export_KnownRoleAtOrAboveThreshold_RemapsToGmDrumChannel9()
    {
        // Fixture from Phase 1: a kick-vocabulary fast-attack decaying retrigger
        // classifies Bd at confidence >= 0.80 — the ONLY path into the gate.
        InstrumentDefinition kick = GateInstrument("kick", 7,
            GateOp(31, 0, 31, 31), GateOp(31, 0, 31, 31), GateOp(31, 0, 31, 31), GateOp(31, 0, 31, 31));
        NoteEvent note = GateNote("ym2608.0.fm.1", "kick", midi: 60);
        byte[] bytes = ExportWithDrumEvidence(new[] { note }, new[] { kick }, out IReadOnlyList<PercussiveOnset> evidence);

        // "Live classification at export time" (evidence-driven, not channel names):
        // the unified collection holds the classified onset...
        PercussiveOnset onset = Assert.Single(evidence);
        Assert.Equal(PercussionEvidenceKind.ClassifiedNote, onset.EvidenceKind);
        Assert.Equal(RhythmRole.Bd, onset.Role);
        Assert.True(onset.Confidence >= GeneralMidiDrumMapper.RequiredDrumRoleConfidence);

        // ...and the exported bytes carry ONE GM drum hit on channel 9 (0-based =
        // 1-based channel 10), never the melodic pitch 60.
        ParsedMidi parsed = Parser.Parse(bytes);
        Assert.Contains(parsed.NoteOns, n => n.Note == 36 && n.Channel == 9);
        Assert.DoesNotContain(parsed.NoteOns, n => n.Note == 60);
    }

    [Fact]
    public void Export_UnknownRole_StaysMelodic_AndStillFeedsTimingEvidence()
    {
        // Confidently percussive but role = Unknown (0.92, the classifier's cap):
        // the gate MUST NOT remap (no invented roles, no fabricated GM tom), the
        // note keeps its melodic pitch and track, and it still participates as
        // percussion evidence in timing.
        InstrumentDefinition transient = GateInstrument("transient_4", 7,
            GateOp(31, 0, 31, 31), GateOp(31, 0, 31, 31), GateOp(31, 0, 31, 31), GateOp(31, 0, 31, 31));
        NoteEvent note = GateNote("ym2608.0.fm.4", "transient_4", midi: 62);
        byte[] bytes = ExportWithDrumEvidence(new[] { note }, new[] { transient }, out IReadOnlyList<PercussiveOnset> evidence);

        PercussiveOnset onset = Assert.Single(evidence);
        Assert.Equal(PercussionEvidenceKind.ClassifiedNote, onset.EvidenceKind);
        Assert.Equal(RhythmRole.Unknown, onset.Role);
        Assert.True(onset.Confidence >= 0.80, $"unknown role must not be gated on confidence under the cap: {onset.Confidence}");

        ParsedMidi parsed = Parser.Parse(bytes);
        Assert.Contains(parsed.NoteOns, n => n.Note == 62 && n.Channel != 9);
        Assert.DoesNotContain(parsed.NoteOns, n => n.Channel == 9);
    }

    [Fact]
    public void Export_BelowThreshold_StaysMelodic_AndStillFeedsTimingEvidence()
    {
        // No instrument envelope → the conservative fallback classifies the
        // repeated short attack percussive at only 0.70. Role is Unknown anyway,
        // but the test pins the BELOW-THRESHOLD branch of the gate (spec §8:
        // IsPercussive && Role != Unknown && Confidence >= 0.80 required).
        NoteEvent note = GateNote("ym2608.0.fm.7", "missing", midi: 63);
        byte[] bytes = ExportWithDrumEvidence(new[] { note }, Array.Empty<InstrumentDefinition>(), out IReadOnlyList<PercussiveOnset> evidence);

        PercussiveOnset onset = Assert.Single(evidence);
        Assert.Equal(PercussionEvidenceKind.ClassifiedNote, onset.EvidenceKind);
        Assert.Equal(0.70, onset.Confidence, precision: 6);

        ParsedMidi parsed = Parser.Parse(bytes);
        Assert.Contains(parsed.NoteOns, n => n.Note == 63 && n.Channel != 9);
        Assert.DoesNotContain(parsed.NoteOns, n => n.Channel == 9);
    }

    [Fact]
    public void Export_DrumRemap_IsDeterministicAcrossRuns()
    {
        InstrumentDefinition kick = GateInstrument("kick", 7,
            GateOp(31, 0, 31, 31), GateOp(31, 0, 31, 31), GateOp(31, 0, 31, 31), GateOp(31, 0, 31, 31));
        NoteEvent note = GateNote("ym2608.0.fm.1", "kick", midi: 60);

        byte[] first = ExportWithDrumEvidence(new[] { note }, new[] { kick }, out IReadOnlyList<PercussiveOnset> firstEvidence);
        byte[] second = ExportWithDrumEvidence(new[] { note }, new[] { kick }, out IReadOnlyList<PercussiveOnset> secondEvidence);
        // "Deterministic and stable across runs": byte-identical output and
        // byte-identical evidence — no ordering, lookup or allocation instability.
        Assert.Equal(first, second);
        Assert.Equal(Assert.Single(firstEvidence), Assert.Single(secondEvidence));
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

    /// <summary>Exports the given notes on a fixed 120 BPM 4/4 timeline and returns
    /// the full result receipt (attack counters included).</summary>
    private static MusicalMidiExportResult ExportResult(NoteEvent[] notes)
    {
        double spq = Sr * 60.0 / 120.0;
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = notes.Max(n => n.EndSample) + 10_000,
            SampleRate = Sr,
            Notes = notes,
            Beats = new[] { new BeatEvent(0, 0.0), new BeatEvent((long)Math.Round(spq), 1.0) },
        };
        var build = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions
        {
            FixedBpm = 120,
            Meter = new Meter(4, 4),
            DetectTempoChanges = true,
        });
        var exporter = new MusicalMidiExporter(build.Map, Ppq, new MusicalMidiExportOptions
        {
            EmitPitchBend = true,
        })
        {
            Diagnostics = build.Diagnostics,
        };
        return exporter.Export(timeline);
    }

    private static MidiSemanticDecoder.Result Decode(NoteEvent[] notes)
        => MidiSemanticDecoder.Decode(ExportResult(notes).Bytes);

    private static int CountNoteOns(MidiSemanticDecoder.Result decoded, long tick)
        => decoded.Events.Values.SelectMany(e => e)
            .Count(e => e.Tick == tick && e.Event is NoteOnEvent);

    private static int CountAllNoteOns(MidiSemanticDecoder.Result decoded)
        => decoded.Events.Values.SelectMany(e => e).Count(e => e.Event is NoteOnEvent);

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
        VisualizationTimeline timeline = VoiceStateNormalizationStage.Normalize(state.Timeline);
        var build = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions
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
        return exporter.Export(timeline).Bytes;
    }

    private static byte[] ExportWithOptions(TimelineState state, MusicalMidiExportOptions options)
    {
        VisualizationTimeline timeline = VoiceStateNormalizationStage.Normalize(state.Timeline);
        var build = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions
        {
            FixedBpm = state.FixedBpm,
            Meter = new Meter(4, 4),
            DetectTempoChanges = true,
        });
        var exporter = new MusicalMidiExporter(build.Map, Ppq, options)
        {
            Diagnostics = build.Diagnostics,
        };
        return exporter.Export(timeline).Bytes;
    }

    /// <summary>Exports with NO meter so the unknown-meter (§64) path is exercised.</summary>
    private static byte[] ExportNoMeter(TimelineState state)
    {
        var build = MusicalTimeMapBuilder.Build(state.Timeline, new MusicalTimeMapOptions
        {
            FixedBpm = state.FixedBpm,
            Meter = null,
            DetectTempoChanges = true,
        });
        var exporter = new MusicalMidiExporter(build.Map, Ppq, new MusicalMidiExportOptions
        {
            EmitPitchBend = true,
        })
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

    public List<ParsedTimeSignature> TimeSignatures { get; } = new();

    // Tempo events sorted by tick.
    public List<long> ConductorTempoTicks => ConductorTempo.Select(t => t.Tick).ToList();
    public List<ParsedMidiNote> NoteOns { get; } = new();

    // Channel -> last program change seen on that channel.
    public Dictionary<int, int> ProgramByChannel { get; } = new();
}

internal sealed record ParsedBend(long Tick, int Channel, int Bend);

internal sealed record ParsedTimeSignature(int Numerator, int Denominator);

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
                    else if (type == 0x58 && payload.Length >= 2)
                    {
                        result.TimeSignatures.Add(new ParsedTimeSignature(payload[0], 1 << payload[1]));
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
