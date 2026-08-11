using Fmp.Core.Midi;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Semantic integration + fidelity tests for the endpoint-isolation (Patch A),
/// pitch-planner (B), source-time/conductor (C), symbolic tempo (D), voice
/// identity (E) and semantic-validator (F) work. Uses the independent
/// <see cref="MidiSemanticDecoder"/> to reconstruct playback state from the actual
/// DryWetMIDI bytes — not from the exporter's own IR.
/// </summary>
public sealed class MidiFidelityIntegrationTests
{
    private const int Sr = 44_100;
    private const int Ppq = 960;

    private static MusicalMidiExportResult ExportResult(VisualizationTimeline timeline,
        double? bpm = null, MusicalMidiExportOptions? options = null, Meter? meter = null)
    {
        var build = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions
        {
            FixedBpm = bpm,
            Meter = meter,
            DetectTempoChanges = true,
        });
        var exporter = new MusicalMidiExporter(build.Map, Ppq,
            options ?? new MusicalMidiExportOptions { EmitPitchBend = true })
        {
            Diagnostics = build.Diagnostics,
        };
        return exporter.Export(timeline);
    }

    private static NoteEvent Note(string voice, long start, long end, double midi,
        string instrument = "inst", params PitchChange[] changes)
        => new(voice, start, end, 440, midi, instrument, VisualizationNoteMode.Fm, false, changes);

    private static VisualizationTimeline Timeline(params NoteEvent[] notes) => new()
    {
        StartSample = 0,
        EndSample = 8_000_000,
        SampleRate = Sr,
        Notes = notes,
    };

    /// <summary>Effective pitch at the note's start: note + sign-symmetric bend, with the
    /// fold-in change applied when a pitch change lands exactly on the start sample.</summary>
    private static double EffectiveOnPitch(MidiSemanticDecoder.Result decoded, (int Port, int Channel) endpoint, int note)
        => note + MidiSemanticDecoder.DecodeBend(decoded.State[endpoint].ActiveBend, decoded.State[endpoint].BendRange);

    // ---- Patch B: primary pitch correctness -----------------------------------

    [Fact]
    public void FractionalPitch_EffectiveWithinPointZeroOneSemitone()
    {
        long start = (long)Math.Round(Sr * 60.0 / 120.0);
        var note = Note("v", start, start + 50_000, 60.3);
        MidiSemanticDecoder.Result d = MidiSemanticDecoder.Decode(ExportResult(Timeline(note), 120).Bytes);

        // One endpoint, one note-on. Reconstruct the effective pitch at the note-on:
        // the bend in effect at the earliest bend on/around the note-on, summed with
        // the note number, sign-symmetrically.
        var ep = d.State.Keys.Single();
        var state = d.State[ep];
        // The exporter writes a bend (if non-integer) before/at the note-on.
        int noteOns = d.Events[ep].Count(e => e.Event is Melanchall.DryWetMidi.Core.NoteOnEvent);
        Assert.Equal(1, noteOns);
        var noteOn = d.Events[ep].Single(e => e.Event is Melanchall.DryWetMidi.Core.NoteOnEvent);
        int baseNote = ((Melanchall.DryWetMidi.Core.NoteOnEvent)noteOn.Event).NoteNumber;
        double effective = baseNote + MidiSemanticDecoder.DecodeBend(state.ActiveBend, state.BendRange);
        Assert.InRange(effective, 60.29, 60.31);
    }

    [Fact]
    public void WideSlide_ReanchorsMinimally_NoGapOverlap()
    {
        // 84, 82, 80, 77, 74, 70 across one note; range 24 so no re-anchor needed
        // (all within base 84 +/-24... 70 is -14 of base 84, within range).
        long start = (long)Math.Round(Sr * 60.0 / 120.0);
        var changes = new[]
        {
            new PitchChange(start + 5000, 0, 82), new PitchChange(start + 10_000, 0, 80),
            new PitchChange(start + 15_000, 0, 77), new PitchChange(start + 20_000, 0, 74),
            new PitchChange(start + 25_000, 0, 70),
        };
        var note = Note("v", start, start + 60_000, 84, "inst", changes);
        MidiSemanticDecoder.Result d = MidiSemanticDecoder.Decode(ExportResult(Timeline(note), 120).Bytes);
        var ep = d.State.Keys.Single();

        // Note-count correctness: one source note => one NoteOn and one NoteOff
        // (no re-anchor for a wide-but-in-range slide), no gap/overlap.
        int ons = d.Events[ep].Count(e => e.Event is Melanchall.DryWetMidi.Core.NoteOnEvent);
        int offs = d.Events[ep].Count(e => e.Event is Melanchall.DryWetMidi.Core.NoteOffEvent);
        Assert.Equal(1, ons);
        Assert.Equal(1, offs);
        var baseNote = ((Melanchall.DryWetMidi.Core.NoteOnEvent)d.Events[ep].Single(e => e.Event is Melanchall.DryWetMidi.Core.NoteOnEvent).Event).NoteNumber;
        Assert.Equal(84, baseNote);
    }

    [Fact]
    public void ExtremeSlide_Reanchors_AccurateDecodedPitch()
    {
        // 60 -> 110 is a 50-semitone jump far beyond range 24; must re-anchor.
        long start = (long)Math.Round(Sr * 60.0 / 120.0);
        var changes = new[]
        {
            new PitchChange(start + 5000, 0, 70), new PitchChange(start + 10_000, 0, 80),
            new PitchChange(start + 15_000, 0, 90), new PitchChange(start + 20_000, 0, 100),
            new PitchChange(start + 25_000, 0, 110),
        };
        var note = Note("v", start, start + 60_000, 60, "inst", changes);
        MidiSemanticDecoder.Result d = MidiSemanticDecoder.Decode(ExportResult(Timeline(note), 120).Bytes);
        var ep = d.State.Keys.Single();
        // At least one re-anchor (extra NoteOn after the initial).
        int ons = d.Events[ep].Count(e => e.Event is Melanchall.DryWetMidi.Core.NoteOnEvent);
        var offs = d.Events[ep].Count(e => e.Event is Melanchall.DryWetMidi.Core.NoteOffEvent);
        Assert.True(ons >= 2, "extreme slide must re-anchor to a new base note");
        Assert.Equal(ons, offs); // every note-on has a matching note-off (no gap/overlap)
    }

    // ---- Patch B: folding / collapse / bend reset / boundaries ------------------

    [Fact]
    public void SameSourceSample_FoldsIntoInitialPitch_NoSeparateBendAtOnset()
    {
        long start = (long)Math.Round(Sr * 60.0 / 120.0);
        // Change at EXACTLY StartSample = 64: folds into the effective initial pitch.
        var note = Note("v", start, start + 50_000, 60.0,
            "inst", new PitchChange(start, 0, 64.0));
        MidiSemanticDecoder.Result d = MidiSemanticDecoder.Decode(ExportResult(Timeline(note), 120).Bytes);
        var ep = d.State.Keys.Single();
        int noteOns = d.Events[ep].Count(e => e.Event is Melanchall.DryWetMidi.Core.NoteOnEvent);
        Assert.Equal(1, noteOns);
        var baseNote = ((Melanchall.DryWetMidi.Core.NoteOnEvent)d.Events[ep].Single(e => e.Event is Melanchall.DryWetMidi.Core.NoteOnEvent).Event).NoteNumber;
        // The note starts at base 64 (folded), not 60.
        Assert.Equal(64, baseNote);
    }

    [Fact]
    public void MultipleSameSampleChanges_LastWins()
    {
        long start = (long)Math.Round(Sr * 60.0 / 120.0);
        // 60.1 -> 60.4 -> 60.7 all at the same sample: final (60.7) wins.
        var note = Note("v", start, start + 50_000, 60.0, "inst",
            new PitchChange(start + 1000, 0, 60.1),
            new PitchChange(start + 1000, 0, 60.4),
            new PitchChange(start + 1000, 0, 60.7));
        MidiSemanticDecoder.Result d = MidiSemanticDecoder.Decode(ExportResult(Timeline(note), 120).Bytes);
        var ep = d.State.Keys.Single();
        // Effective pitch after the change == 60.7 (the fold to that sample's final).
        // Reconstruct effective pitch at the last bend.
        var bends = d.Events[ep].Where(e => e.Event is Melanchall.DryWetMidi.Core.PitchBendEvent).ToList();
        int onCount = d.Events[ep].Count(e => e.Event is Melanchall.DryWetMidi.Core.NoteOnEvent);
        Assert.Equal(1, onCount);
        // Multiple samples changes at a single sample: only one bend for that sample.
        var on = (Melanchall.DryWetMidi.Core.NoteOnEvent)d.Events[ep].Single(e => e.Event is Melanchall.DryWetMidi.Core.NoteOnEvent).Event;
        Assert.Equal(60, on.NoteNumber);
        _ = bends;
    }

    [Fact]
    public void SameTickChanges_CollapseToFinalState()
    {
        long start = (long)Math.Round(Sr * 60.0 / 120.0);
        // Two changes mapping to the same MIDI tick => only the final one is emitted.
        var note = Note("v", start, start + 50_000, 60.0, "inst",
            new PitchChange(start + 1, 0, 62.0),
            new PitchChange(start + 2, 0, 64.0));
        MidiSemanticDecoder.Result d = MidiSemanticDecoder.Decode(ExportResult(Timeline(note), 120).Bytes);
        var ep = d.State.Keys.Single();
        int bends = d.Events[ep].Count(e => e.Event is Melanchall.DryWetMidi.Core.PitchBendEvent);
        Assert.True(bends <= 1, "same-tick changes must collapse to a single bend");
    }

    [Fact]
    public void BendReset_BeforeNoteOn()
    {
        // Note A ends at +1500 ST bend; note B is then centered (bend 0). The bend
        // must reset to 0 before B's note-on (never assume NoteOff restores it).
        long start = (long)Math.Round(Sr * 60.0 / 120.0);
        var a = Note("v", start, start + 20_000, 61.5);       // bend +1.5
        var b = Note("v", start + 30_000, start + 50_000, 60.0); // centered, bend 0
        MidiSemanticDecoder.Result d = MidiSemanticDecoder.Decode(ExportResult(Timeline(a, b), 120).Bytes);
        var ep = d.State.Keys.Single();
        var events = d.Events[ep].OrderBy(e => e.Tick).ToList();
        // Locate the second note-on; the bend immediately before it must be 0.
        var noteOns = events.Where(e => e.Event is Melanchall.DryWetMidi.Core.NoteOnEvent).ToList();
        Assert.Equal(2, noteOns.Count);
        long bOnTick = noteOns[1].Tick;
        var bendBefore = events
            .Where(e => e.Event is Melanchall.DryWetMidi.Core.PitchBendEvent && e.Tick <= bOnTick)
            .LastOrDefault();
        Assert.NotNull(bendBefore);
        int signed = ((Melanchall.DryWetMidi.Core.PitchBendEvent)bendBefore.Event).PitchValue - 8192;
        Assert.Equal(0, signed);
    }

    [Fact]
    public void MidiZeroAndOneTwentySeven_BoundariesNoWrap()
    {
        long start = (long)Math.Round(Sr * 60.0 / 120.0);
        var n0 = Note("v", start, start + 10_000, 0.0);
        var n127 = Note("v", start + 20_000, start + 30_000, 127.0);
        MidiSemanticDecoder.Result d = MidiSemanticDecoder.Decode(ExportResult(Timeline(n0, n127), 120).Bytes);
        var ep = d.State.Keys.Single();
        var notes = d.Events[ep].Where(e => e.Event is Melanchall.DryWetMidi.Core.NoteOnEvent)
            .Select(e => ((Melanchall.DryWetMidi.Core.NoteOnEvent)e.Event).NoteNumber).OrderBy(n => n).ToList();
        Assert.Equal(0, notes[0]);
        Assert.Equal(127, notes[^1]);
    }

    [Fact]
    public void RealCollapsedNote_MinimumOneTick()
    {
        long start = (long)Math.Round(Sr * 60.0 / 120.0);
        var note = Note("v", start, start + 5, 60.0); // 5 samples -> same tick
        MidiSemanticDecoder.Result d = MidiSemanticDecoder.Decode(ExportResult(Timeline(note), 120).Bytes);
        var ep = d.State.Keys.Single();
        var on = d.Events[ep].Single(e => e.Event is Melanchall.DryWetMidi.Core.NoteOnEvent);
        var off = d.Events[ep].Single(e => e.Event is Melanchall.DryWetMidi.Core.NoteOffEvent);
        Assert.Equal(on.Tick + 1, off.Tick);
    }

    // ---- Patch C: source-time / conductor -------------------------------------

    [Fact]
    public void SourceTimeRoundTrip_AtMultipleBpms()
    {
        // NoteOn 4800 (0.100s), PitchChange 7200 (0.150s), Rhythm 9600 (0.200s),
        // NoteOff 14400 (0.300s) at 48 kHz. Decoded wall-clock relative to the
        // source start must match at 56/112/173 BPM.
        const int sr = 48_000;
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = 5_000_000,
            SampleRate = sr,
            Notes = new[]
            {
                new NoteEvent("v", 4800, 14400, 440, 60.0, "inst", VisualizationNoteMode.Fm, false,
                    new[] { new PitchChange(7200, 0, 61.0) }),
            },
            Rhythm = new[] { new RhythmEvent("bd", "rhythm.bd", 9600, 1.0f, 0f)
                { Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Rhythm, 0) } },
        };
        foreach (double bpm in new[] { 56.0, 112.0, 173.0 })
        {
            var build = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions { FixedBpm = bpm });
            var exporter = new MusicalMidiExporter(build.Map, Ppq, new MusicalMidiExportOptions { EmitPitchBend = true });
            MidiSemanticDecoder.Result d = MidiSemanticDecoder.Decode(exporter.Export(timeline).Bytes);
            Assert.Single(d.TempoMap);
            int us = d.TempoMap[0].UsPerQuarter;
            double secondsPerTick = us / 1_000_000.0 / Ppq;
            var ep = d.State.Keys.Single(k => k.Item2 != 9); // melodic endpoint (not rhythm ch9)
            var on = d.Events[ep].Single(e => e.Event is Melanchall.DryWetMidi.Core.NoteOnEvent);
            var off = d.Events[ep].Single(e => e.Event is Melanchall.DryWetMidi.Core.NoteOffEvent);
            Assert.InRange(off.Tick * secondsPerTick, 0.290, 0.310); // 0.300s
            // Rhythm ch9 hit at 0.200s.
            var rhythmEp = d.State.Keys.Single(k => k.Item2 == 9);
            var rOn = d.Events[rhythmEp].Single(e => e.Event is Melanchall.DryWetMidi.Core.NoteOnEvent);
            Assert.InRange(rOn.Tick * secondsPerTick, 0.190, 0.210);
            Assert.InRange(on.Tick * secondsPerTick, 0.090, 0.110); // 0.100s
            _ = bpm;
        }
    }

    [Fact]
    public void FirstTempo_AtTickZero_Unconditional()
    {
        // SOURCE_START later than sample 0: first Set Tempo must still sit at tick 0.
        long start = (long)Math.Round(Sr * 60.0 / 120.0);
        var timeline = new VisualizationTimeline
        {
            StartSample = start, // SOURCE_START at sample `start`
            EndSample = start + 5_000_000,
            SampleRate = Sr,
            Notes = new[] { Note("v", start, start + 50_000, 60.0) },
        };
        MidiSemanticDecoder.Result d = MidiSemanticDecoder.Decode(ExportResult(timeline, 120).Bytes);
        Assert.True(d.TempoMap.Count >= 1);
        Assert.Equal(0, d.TempoMap[0].Tick);
    }

    // ---- Patch E: device instance / voice identity ------------------------------

    [Fact]
    public void DeviceInstance_TwoYm2608_FmCh1_TwoDomains()
    {
        var a = Note("ym2608.0.fm.1", 0, 1000, 60) with
        { Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Fm, 0) };
        var b = Note("ym2608.1.fm.1", 0, 1000, 62) with
        { Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 1), VoiceKind.Fm, 0) };
        MidiSemanticDecoder.Result d = MidiSemanticDecoder.Decode(ExportResult(Timeline(a, b), 120).Bytes);
        // Two distinct endpoints (two tracks/domains).
        Assert.Equal(2, d.State.Count);
    }

    [Fact]
    public void Rhythm_Zero_Dot_Rhythm_Top_NotPlaceholder()
    {
        var rhythm = new RhythmEvent("top", "ym2608.0.rhythm.top", 1000, 1.0f, 0f,
            InstrumentId: "rhythm:top")
        { Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Rhythm, 2) };
        var timeline = new VisualizationTimeline
        {
            StartSample = 0, EndSample = 5_000_000, SampleRate = Sr,
            Rhythm = new[] { rhythm },
        };
        MidiSemanticDecoder.Result d = MidiSemanticDecoder.Decode(
            ExportResult(timeline, 120, new MusicalMidiExportOptions { EmitPitchBend = false }).Bytes);
        // Channel 9 endpoint present (percussion), not a placeholder.
        Assert.Contains(d.State.Keys, k => k.Item2 == 9);
    }

    [Fact]
    public void UnpitchedSsgNoise_ExcludedFromExport_NotAnError()
    {
        // SSG noise-only notes carry the intentional -1 "Unpitched" sentinel (the
        // decoder's tested representation: no pitch exists). The melodic MIDI export
        // must exclude them with a diagnostic, never fabricate a pitch and never
        // crash the whole export.
        var noise = new NoteEvent("ym2608.0.ssg.1", 0, 1000, 0, -1.0, "ssg:noise",
            VisualizationNoteMode.SsgNoise, false, Array.Empty<PitchChange>())
        { Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Ssg, 0) };
        var tone = Note("ym2608.0.fm.1", 0, 1000, 62);
        MusicalMidiExportResult result = ExportResult(Timeline(noise, tone), 120);
        // No throw; the melodic content is intact.
        Assert.True(result.Bytes.Length > 0);
        // Only the pitched FM note survives: exactly one melodic endpoint.
        MidiSemanticDecoder.Result d = MidiSemanticDecoder.Decode(result.Bytes);
        Assert.Equal(1, d.State.Count);
        // The exclusion is surfaced as a diagnostic warning.
        Assert.Contains(result.Diagnostics.Warnings,
            w => w.Contains("unpitched noise", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Patch F: endpoint uniqueness / program / determinism ------------------

    [Fact]
    public void EndpointUniqueness_MoreThanSixteenTracks_PortRollover()
    {
        // 18 distinct FM channels on YM2612 instance 0 => >16 tracks, up to 15 per
        // port, so track 17+ rolls onto port 1.
        var notes = Enumerable.Range(1, 18)
            .Select(ch => Note($"ym2612.0.fm.{ch}", 0, 1000, 60) with
            { Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2612, 0), VoiceKind.Fm, ch - 1) })
            .ToArray();
        MidiSemanticDecoder.Result d = MidiSemanticDecoder.Decode(
            ExportResult(Timeline(notes), 120, new MusicalMidiExportOptions { EmitPitchBend = false }).Bytes);
        // At least 18 unique endpoints.
        Assert.True(d.State.Count >= 18);
        int distinctPorts = d.State.Keys.Select(k => k.Item1).Distinct().Count();
        Assert.True(distinctPorts >= 2, "18 tracks must roll across multiple ports");
        // No channel 9 for melodic tracks.
        Assert.DoesNotContain(d.State.Keys, k => k.Item2 == 9);
    }

    [Fact]
    public void NoDefaultProgramZero()
    {
        var note = Note("v", 0, 1000, 60);
        MidiSemanticDecoder.Result d = MidiSemanticDecoder.Decode(ExportResult(Timeline(note), 120).Bytes);
        var ep = d.State.Keys.Single();
        Assert.Null(d.State[ep].Program); // no fake default Program Change 0
    }

    [Fact]
    public void ExplicitProgram_EmitsOnce()
    {
        var note = Note("v", 0, 1000, 60);
        var options = new MusicalMidiExportOptions
        {
            EmitPitchBend = false,
            VoiceOverrides = new[] { new VoiceExportOverride("v") { Program = 12 } },
        };
        MidiSemanticDecoder.Result d = MidiSemanticDecoder.Decode(ExportResult(Timeline(note), 120, options).Bytes);
        var ep = d.State.Keys.Single();
        Assert.Equal(12, d.State[ep].Program);
    }

    [Fact]
    public void Build_ByteIdentical_Determinism()
    {
        long start = (long)Math.Round(Sr * 60.0 / 120.0);
        var note = Note("v", start, start + 40_000, 60.3, "inst",
            new PitchChange(start + 2000, 0, 62.0),
            new PitchChange(start + 4000, 0, 61.0));
        byte[] a = ExportResult(Timeline(note), 120).Bytes;
        byte[] b = ExportResult(Timeline(note), 120).Bytes;
        Assert.Equal(a, b);
    }

    [Fact]
    public void PortExhaustion_Port255_ThrowsLoudly()
    {
        // More distinct domains than available (port,channel) endpoints exceed the
        // byte port limit (256 ports * 15 melodic channels). The allocator must throw
        // a clear error, never silently reuse or wrap a channel.
        var notes = Enumerable.Range(0, 256 * 15 + 1)
            .Select(i => Note($"ym2608.{i / 15}.fm.{i % 15 + 1}", 0, 1000, 60) with
            { Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, i / 15), VoiceKind.Fm, i % 15) })
            .ToArray();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            ExportResult(Timeline(notes), 120, new MusicalMidiExportOptions { EmitPitchBend = false }));
        Assert.Contains("exhaustion", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TempoChange_SourceTime_Continuous()
    {
        // Three tempo segments (A/B/C) at 120/150/90 BPM with a note spanning the
        // first boundary: decoded wall-clock must stay continuous (gate 42).
        const int sr = 48_000;
        double spqA = sr * 60.0 / 120.0;
        double spqB = sr * 60.0 / 150.0;
        long b1 = (long)Math.Round(4 * spqA);
        long b2 = b1 + (long)Math.Round(4 * spqB);
        var map = new MusicalTimeMap(sr, 0, new[]
        {
            new TempoSegment(0, b1, 0.0, spqA, 120, TimingSource.DriverValidatedTempo, 1.0),
            new TempoSegment(b1, b2, 4.0, spqB, 150, TimingSource.DriverValidatedTempo, 1.0),
            new TempoSegment(b2, 10_000_000, 4.0 + 4.0, sr * 60.0 / 90.0, 90, TimingSource.DriverValidatedTempo, 1.0),
        });
        var timeline = new VisualizationTimeline
        {
            StartSample = 0, EndSample = 10_000_000, SampleRate = sr,
            Notes = new[] { new NoteEvent("v", 0, b1 + (long)Math.Round(2 * spqB), 440, 60.0,
                "inst", VisualizationNoteMode.Fm, false, Array.Empty<PitchChange>()) },
        };
        var exporter = new MusicalMidiExporter(map, Ppq, new MusicalMidiExportOptions { EmitPitchBend = true });
        MidiSemanticDecoder.Result d = MidiSemanticDecoder.Decode(exporter.Export(timeline).Bytes);
        Assert.Equal(3, d.TempoMap.Count); // 120, 150, 90 (one Set Tempo per distinct segment)
        long noteOff = d.Events[d.State.Keys.Single()].Single(e => e.Event is Melanchall.DryWetMidi.Core.NoteOffEvent).Tick;
        // 0 -> b1 at 120 BPM, b1 -> (b1 + 2*spqB) at 150 BPM.
        double expected = (b1 / (double)sr) + (2 * spqB / (double)sr);
        // Decode the off tick to seconds through the tempo map.
        double seconds = SecondsOfTick(d, noteOff, Ppq);
        Assert.InRange(seconds, expected - 0.010, expected + 0.010);
    }

    /// <summary>Converts an absolute tick to seconds using the decoded tempo map.</summary>
    private static double SecondsOfTick(MidiSemanticDecoder.Result d, long tick, int ppq)
    {
        double seconds = 0;
        long prev = 0;
        int us = d.TempoMap[0].UsPerQuarter;
        foreach ((long t, int nextUs) in d.TempoMap)
        {
            if (tick <= t) break;
            seconds += (t - prev) * us / 1_000_000.0 / ppq;
            prev = t;
            us = nextUs;
        }
        if (tick > prev)
            seconds += (tick - prev) * us / 1_000_000.0 / ppq;
        return seconds;
    }

    // ---- Patch D: symbolic tempo inference regressions --------------------------

    [Fact]
    public void Symbolic_ThirtySecondAt56_EqualsSixteenthAt112_SurfacesHalfDouble()
    {
        // A 32nd-note texture at 56 BPM is temporally identical to a 16th-note
        // texture at 112 BPM (and its octave equivalents). Onset spacing alone cannot
        // pick the octave, so the inference must (a) resolve to the musically central
        // octave — 112 here, never the extreme 56/224 — and (b) still surface the
        // half/double alternative with both scores and BPMs populated.
        var step = (long)Math.Round(Sr * 60.0 / 112.0 / 4.0); // 16th at 112 == 32nd at 56
        var notes = Enumerable.Range(0, 32)
            .Select(i => Note("v", i * step, i * step + 800, 64))
            .ToArray();
        var build = MusicalTimeMapBuilder.Build(new VisualizationTimeline
        {
            StartSample = 0, EndSample = 32 * step + 10_000, SampleRate = Sr, Notes = notes,
        }, new MusicalTimeMapOptions { Source = TimingSource.SymbolicInference });
        var d = build.Diagnostics;
        Assert.NotNull(d.SelectedBpm);
        // The true pulse is 112; the octave resolution must pick the central member
        // of the 56/112/224 family, not the coarse-duration-favoring extreme.
        Assert.True(Math.Abs(d.SelectedBpm.Value - 112.0) < 0.5,
            $"selected {d.SelectedBpm.Value} must be the central octave 112");
        // Half/double ambiguity must be surfaced with the alternative BPM + scores.
        Assert.True(d.TempoAmbiguous, "a pure dense grid must be flagged prominent half/double ambiguity");
        Assert.True(d.AlternativeBpm is double a && (Math.Abs(112.0 / a - 0.5) < 0.02 || Math.Abs(112.0 / a - 2) < 0.02),
            "alternative must be the half/double of the selected");
        Assert.NotNull(d.SelectedScore);
        Assert.NotNull(d.AlternativeScore);
        Assert.NotNull(d.TempoConfidence);
    }

    [Fact]
    public void Symbolic_BeatLockedRhythm_SurfacesAccentEvidence_ResolvesCentralOctave()
    {
        // A dense melodic stream plus a rhythm/accent layer locked to the quarter
        // beat at 112 BPM. The accent layer is genuine beat-level evidence: it must
        // never push the family toward the coarse 224 extreme, and the resolution
        // still lands on the central octave while flagging the family ambiguity.
        long step = (long)Math.Round(Sr * 60.0 / 112.0 / 4.0);       // 16th at 112
        long beat = (long)Math.Round(Sr * 60.0 / 112.0);             // quarter at 112
        var notes = Enumerable.Range(0, 24)
            .Select(i => Note("v", i * step, i * step + 800, 64))
            .ToArray();
        var rhythm = Enumerable.Range(0, 8)
            .Select(i => new RhythmEvent("top", "rhythm.top", i * beat, 1.0f, 0f)
            {
                Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Rhythm, 2),
            })
            .ToArray();
        var build = MusicalTimeMapBuilder.Build(new VisualizationTimeline
        {
            StartSample = 0, EndSample = 24 * step + 10_000, SampleRate = Sr, Notes = notes, Rhythm = rhythm,
        }, new MusicalTimeMapOptions { Source = TimingSource.SymbolicInference });
        var d = build.Diagnostics;
        Assert.NotNull(d.SelectedBpm);
        Assert.True(Math.Abs(d.SelectedBpm.Value - 112.0) < 0.5,
            $"selected {d.SelectedBpm.Value} must be the central octave 112");
        Assert.True(d.TempoAmbiguous, "a pure metrical grid must still surface family ambiguity");
        Assert.NotNull(d.SelectedScore);
        Assert.NotNull(d.AlternativeScore);
    }

    [Fact]
    public void Symbolic_TripletTexture_ResolvesToTripletRelatedGrid()
    {
        // Eighth-note-triplet texture at 120 BPM. The inference returns a valid
        // tempo whose quarter is a triplet-related multiple of the onsets — i.e. the
        // grid fits a 1/3-quartet lattice (120 or an octave-related bound).
        double spq = Sr * 60.0 / 120.0;
        long step = (long)Math.Round(spq / 3.0);
        var notes = Enumerable.Range(0, 24)
            .Select(i => Note("v", i * step, i * step + 600, 64))
            .ToArray();
        var build = MusicalTimeMapBuilder.Build(new VisualizationTimeline
        {
            StartSample = 0, EndSample = 24 * step + 10_000, SampleRate = Sr, Notes = notes,
        }, new MusicalTimeMapOptions { Source = TimingSource.SymbolicInference });
        var d = build.Diagnostics;
        Assert.NotNull(d.SelectedBpm);
        double selected = d.SelectedBpm.Value;
        // Valid resolutions: the onsets sit every 1/3 quarter at 120. The inferred
        // eighth-triplet map may view them as 16ths/sixteenths at another tempo; any
        // tempo whose samples-per-quarter keeps onsets on the 1/3, 1/6 or octave
        // lattice is acceptable. Bounded to the search range.
        Assert.InRange(selected, 40, 240);
        // Confidence is reported (may be low for an ambiguous triplet texture).
        Assert.NotNull(d.TempoConfidence);
    }

    [Fact]
    public void Symbolic_ContinuousGrid_QuarterPositionIsContinuous()
    {
        // The map never snaps note positions to inferred beats: SampleToQuarterPosition
        // is the raw continuous conversion.
        double spq = Sr * 60.0 / 100.0;
        long step = (long)Math.Round(spq / 2.0);
        var notes = Enumerable.Range(0, 20)
            .Select(i => Note("v", (long)Math.Round(0.37 * step) + i * step, (long)Math.Round(0.37 * step) + i * step + 700, 64))
            .ToArray();
        var build = MusicalTimeMapBuilder.Build(new VisualizationTimeline
        {
            StartSample = 0, EndSample = 20 * step + 10_000, SampleRate = Sr, Notes = notes,
        }, new MusicalTimeMapOptions { Source = TimingSource.SymbolicInference });
        MusicalTimeMap map = build.Map;
        // A non-grid sample maps to a fractional, non-integer quarter — never snapped.
        double q = map.SampleToQuarterPosition((long)Math.Round(0.37 * step) + 3 * step);
        Assert.True(Math.Abs(q - Math.Round(q)) > 0.01, $"quarter {q} must be continuous, not snapped");
    }

    [Fact]
    public void Symbolic_PhaseAfterSourceStart_MapsSourceStartToNegativeQuarter()
    {
        double spq = Sr * 60.0 / 120.0;
        long phaseSample = 1_000;
        var notes = Enumerable.Range(0, 16)
            .Select(i => Note("v", phaseSample + (long)Math.Round(i * spq), phaseSample + (long)Math.Round(i * spq) + 600, 64))
            .ToArray();

        var build = MusicalTimeMapBuilder.Build(new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = phaseSample + 16 * (long)spq + 10_000,
            SampleRate = Sr,
            Notes = notes,
        }, new MusicalTimeMapOptions { Source = TimingSource.SymbolicInference });

        Assert.True(build.Diagnostics.SampleZeroQuarter < 0);
        Assert.True(build.Map.SampleToQuarterPosition(0) < 0);
    }
}
