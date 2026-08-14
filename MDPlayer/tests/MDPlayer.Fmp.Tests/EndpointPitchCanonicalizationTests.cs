using Fmp.Core.Midi;
using Fmp.Core.Playback.Spc;
using Fmp.Core.Rendering;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Patch 1: endpoint-level pitch-bend canonicalization (FR-1..FR-8, SC-2..SC-7,
/// SC-13, SC-15, SC-18, SC-19). Covers the same-tick multi-bend collapse,
/// consecutive-identical suppression, the hard invariant, round-trip coverage on
/// parsed DryWetMIDI bytes, the mandatory Smash-Up-pattern structural regression,
/// determinism and the 120 Smash Up real-file acceptance.
/// </summary>
public sealed class EndpointPitchCanonicalizationTests
{
    private const int Sr = 44_100;
    private const int Ppq = 960;

    // ---- helpers ---------------------------------------------------------------

    /// <summary>An exporter over an empty timeline — enough to run the pure
    /// canonicalization pass on directly-constructed IR tracks.</summary>
    private static MusicalMidiExporter NewExporter()
    {
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = 8_000_000,
            SampleRate = Sr,
            Notes = Array.Empty<NoteEvent>(),
        };
        var build = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions
        {
            FixedBpm = 120,
            DetectTempoChanges = true,
        });
        return new MusicalMidiExporter(build.Map, Ppq,
            new MusicalMidiExportOptions { EmitPitchBend = true });
    }

    private static MidiTrack Track(string name, params MidiEventBase[] events)
    {
        var track = new MidiTrack
        {
            Name = name,
            Endpoint = new MidiEndpoint(0, 0),
        };
        foreach (MidiEventBase evt in events)
            track.Events.Add(evt);
        return track;
    }

    private static MidiNoteEvent NoteOff(long tick, int sourceOrder) =>
        new(tick, 1, 0, 60, 100, NoteOn: false) { SourceOrder = sourceOrder };

    private static MidiNoteEvent NoteOn(long tick, int note, int sourceOrder) =>
        new(tick, 1, 0, note, 100, NoteOn: true) { SourceOrder = sourceOrder };

    private static MidiPitchBendEvent Bend(long tick, int bend, int sourceOrder) =>
        new(tick, 1, 0, bend) { SourceOrder = sourceOrder };

    private static IReadOnlyList<MidiPitchBendEvent> Bends(MidiTrack track) =>
        track.Events.OfType<MidiPitchBendEvent>().OrderBy(b => b.Tick).ThenBy(b => b.SourceOrder).ToList();

    /// <summary>Reconstructs the serialized event order within one tick from the
    /// canonicalized IR: NoteOff (Rank 0) &lt; PitchBend (Rank 3) &lt; NoteOn (Rank 4).</summary>
    private static void AssertNoteOffBendNoteOnOrder(IEnumerable<MidiEventBase> events)
    {
        foreach (IGrouping<long, MidiEventBase> tickGroup in events.GroupBy(e => e.Tick))
        {
            int lastRank = -1;
            foreach (MidiEventBase evt in tickGroup.OrderBy(MidiEventOrder.Rank).ThenBy(e => e.SourceOrder))
            {
                int rank = MidiEventOrder.Rank(evt);
                Assert.True(rank >= lastRank,
                    $"event order violated at tick {evt.Tick}: rank {rank} after rank {lastRank}");
                lastRank = rank;
            }
        }
    }

    /// <summary>Per-endpoint state machine (spec 52): active bend after tick T equals
    /// the final retained PitchBend at T, else carries forward.</summary>
    private static void AssertSemanticBendState(IEnumerable<MidiTrack> tracks)
    {
        foreach (MidiTrack track in tracks)
        {
            int active = 0;
            foreach (IGrouping<long, MidiEventBase> tickGroup in track.Events
                         .GroupBy(e => e.Tick)
                         .OrderBy(g => g.Key))
            {
                int? bendAtTick = tickGroup.OfType<MidiPitchBendEvent>().LastOrDefault()?.Bend;
                // With the same-tick invariant, at most one bend per tick.
                Assert.True(tickGroup.Count(e => e is MidiPitchBendEvent) <= 1,
                    $"more than one bend at tick {tickGroup.Key}");
                if (bendAtTick is int b)
                    active = b;
                Assert.Equal(active, bendAtTick ?? active);
            }
        }
    }

    // ---- SC-2..SC-6: same-tick collapse semantics (direct IR) -------------------

    [Fact]
    public void SameTickOutgoingAndIncoming_KeepIncoming()
    {
        // Real-file pattern (Smash Up tick 4085): NoteOff A; Bend A-final; Bend
        // B-initial; NoteOn B — the incoming note's bend must win.
        var track = Track("mel",
            NoteOff(200, 9),
            Bend(200, -188, 10),
            Bend(200, +160, 11),
            NoteOn(200, 51, 12));

        NewExporter().CanonicalizeEndpointPitchState(new[] { track });

        var bends = Bends(track);
        Assert.Single(bends);
        Assert.Equal(200, bends[0].Tick);
        Assert.Equal(160, bends[0].Bend);
        AssertNoteOffBendNoteOnOrder(track.Events);
        AssertSemanticBendState(new[] { track });
    }

    [Fact]
    public void SameTickResetToZero_KeepReset()
    {
        // Outgoing final +2000, incoming initial 0 at the boundary tick: the reset
        // to zero is REQUIRED (NoteOff does not reset pitch bend) — never omitted.
        var track = Track("mel",
            NoteOff(200, 9),
            Bend(200, +2000, 10),
            Bend(200, 0, 11),
            NoteOn(200, 60, 12));

        NewExporter().CanonicalizeEndpointPitchState(new[] { track });

        var bends = Bends(track);
        Assert.Single(bends);
        Assert.Equal(200, bends[0].Tick);
        Assert.Equal(0, bends[0].Bend);
        AssertNoteOffBendNoteOnOrder(track.Events);
    }

    [Fact]
    public void ConsecutiveIdentical_RemoveDuplicate()
    {
        // Identical bend values on consecutive ticks with no bend-range (RPN)
        // change between: the later duplicate is dropped.
        var track = Track("mel",
            Bend(100, +683, 1),
            Bend(200, +683, 2));

        NewExporter().CanonicalizeEndpointPitchState(new[] { track });

        var bends = Bends(track);
        Assert.Single(bends);
        Assert.Equal(100, bends[0].Tick);
        Assert.Equal(683, bends[0].Bend);
    }

    [Fact]
    public void ConsecutiveIdentical_AfterBendRangeChange_Retained()
    {
        // A bend-range (RPN sensitivity) event between the two identical bends
        // changes what the encoded value means — both must be retained (FR-4).
        var track = Track("mel",
            Bend(100, +683, 1),
            new MidiBendRangeEvent(150, 1, 0, 24) { SourceOrder = 2 },
            Bend(200, +683, 3));

        NewExporter().CanonicalizeEndpointPitchState(new[] { track });

        var bends = Bends(track);
        Assert.Equal(2, bends.Count);
        Assert.Equal(new[] { 100L, 200L }, bends.Select(b => b.Tick));
    }

    [Fact]
    public void DifferentTicks_PreserveBoth()
    {
        // Dense pitch changes on consecutive distinct ticks are valid state
        // transitions — never merged (FR-3).
        var track = Track("mel",
            Bend(100, +1, 1),
            Bend(101, -2, 2),
            Bend(102, +3, 3));

        NewExporter().CanonicalizeEndpointPitchState(new[] { track });

        var bends = Bends(track);
        Assert.Equal(3, bends.Count);
        Assert.Equal(new[] { 100L, 101L, 102L }, bends.Select(b => b.Tick));
    }

    [Fact]
    public void ReanchorAndNoteBoundary_OneBendOnly()
    {
        // Re-anchor bend and new-note bend on the same tick: exactly one bend
        // remains, with the incoming value (real pattern: tick 96233 Bend -1051 /
        // Bend -27 → keep -27).
        var track = Track("mel",
            NoteOff(300, 9),
            Bend(300, -1051, 10),
            Bend(300, -27, 11),
            NoteOn(300, 52, 12));

        NewExporter().CanonicalizeEndpointPitchState(new[] { track });

        var bends = Bends(track);
        Assert.Single(bends);
        Assert.Equal(300, bends[0].Tick);
        Assert.Equal(-27, bends[0].Bend);
        AssertNoteOffBendNoteOnOrder(track.Events);
        AssertSemanticBendState(new[] { track });
    }

    [Fact]
    public void SameTick_ThreeBends_GreatestSourceOrderWins()
    {
        // N > 2 bends on one tick: only the greatest SourceOrder survives.
        var track = Track("mel",
            Bend(400, 10, 1),
            Bend(400, 20, 2),
            Bend(400, 30, 3));

        NewExporter().CanonicalizeEndpointPitchState(new[] { track });

        var bends = Bends(track);
        Assert.Single(bends);
        Assert.Equal(30, bends[0].Bend);
    }

    // ---- SC-15: hard invariant -------------------------------------------------

    [Fact]
    public void InvariantViolationThrows_WithAllFiveFields()
    {
        var track = Track("lead",
            Bend(100, +1, 1),
            Bend(100, +2, 2));

        var ex = Assert.Throws<InvalidOperationException>(
            () => MusicalMidiExporter.ValidateNoDuplicateEndpointTickBends(new[] { track }));

        Assert.Contains("(0, 0)", ex.Message);              // endpoint port, channel
        Assert.Contains("tick 100", ex.Message);            // tick
        Assert.Contains("1, 2", ex.Message);                // bend values
        Assert.Contains("'lead'", ex.Message);              // track name
        Assert.Contains("1, 2", ex.Message);                // SourceOrder values
    }

    [Fact]
    public void Invariant_NoViolation_NoThrow()
    {
        var track = Track("mel",
            Bend(100, +1, 1),
            Bend(200, +2, 2));
        MusicalMidiExporter.ValidateNoDuplicateEndpointTickBends(new[] { track });
    }

    // ---- Semantic state machine over a multi-tick sequence (spec 52) ------------

    [Fact]
    public void SemanticStateMachine_StateCarriesForwardAndTracksFinalBendPerTick()
    {
        var track = Track("mel",
            Bend(100, +5, 1),
            Bend(200, +10, 2),
            Bend(200, +20, 3),   // duplicate at tick 200 → +20 wins
            Bend(300, +20, 4));  // consecutive-identical with +20 → suppressed

        NewExporter().CanonicalizeEndpointPitchState(new[] { track });
        AssertSemanticBendState(new[] { track });

        var bends = Bends(track);
        Assert.Equal(2, bends.Count);
        Assert.Equal(new[] { 100L, 200L }, bends.Select(b => b.Tick));
        Assert.Equal(new[] { 5, 20 }, bends.Select(b => b.Bend));
    }

    // ---- SC-18: determinism -----------------------------------------------------

    [Fact]
    public void Determinism_InsertionOrderIndependent_CollapseBySourceOrder()
    {
        MidiEventBase[] events =
        {
            NoteOff(200, 9),
            Bend(200, -188, 10),
            Bend(200, +160, 11),
            NoteOn(200, 51, 12),
        };
        var trackA = Track("mel", events);
        var trackB = Track("mel", events.Reverse().ToArray()); // same IR, different insertion order

        var exporter = NewExporter();
        exporter.CanonicalizeEndpointPitchState(new[] { trackA });
        exporter.CanonicalizeEndpointPitchState(new[] { trackB });

        Assert.Equal(Bends(trackA).Select(b => b.Bend), Bends(trackB).Select(b => b.Bend));
        Assert.Equal(160, Bends(trackB).Single().Bend);

        // Byte-identical output through the writer regardless of insertion order.
        var conductor = new List<MidiEventBase>();
        byte[] bytesA = new global::Fmp.Core.Midi.MidiFileWriter(Ppq).Write(conductor, new[] { trackA });
        byte[] bytesB = new global::Fmp.Core.Midi.MidiFileWriter(Ppq).Write(conductor, new[] { trackB });
        Assert.Equal(bytesA, bytesB);
    }

    // ---- SC-13: mandatory Smash-Up-pattern synthetic regression -----------------

    /// <summary>
    /// Reproduces the exact structural issue from 120 Smash Up without the real
    /// file: an old note's pitch changes very late; the late source sample rounds
    /// to the same MIDI tick as the NoteOff; the next note starts at that same tick
    /// and requires a DIFFERENT initial bend. Pre-canonicalization this yields two
    /// bends at the handoff tick; the final plan must contain exactly one.
    /// </summary>
    [Fact]
    public void SmashUpPattern_LatePitchChangeHandsOffSameTick_OneBendOnly()
    {
        var build = MusicalTimeMapBuilder.Build(Timeline(), new MusicalTimeMapOptions
        {
            FixedBpm = 120,
            DetectTempoChanges = true,
        });
        long end = 22_050 + 20_000;
        // Largest sample before the end that still maps to the end's tick.
        long late = end - 1;
        while (late > end - 300 && build.Map.SampleToTick(late, Ppq) == build.Map.SampleToTick(end, Ppq))
            late--;
        late++;
        Assert.True(late < end, "expected a late sample mapping to the end tick");

        var timeline = Timeline(
            Note("ym2612.0.fm.1", 22_050, end, 60.0, "fm:3",
                new PitchChange(late, 0, 62.0)),            // very late change → end tick
            Note("ym2612.0.fm.1", end, end + 15_000, 61.5, "fm:3")); // next note, different initial bend

        var exporter = new MusicalMidiExporter(build.Map, Ppq,
            new MusicalMidiExportOptions { EmitPitchBend = true })
        {
            Diagnostics = build.Diagnostics,
        };
        MusicalMidiExportResult result = exporter.Export(timeline);
        byte[] bytes = result.Bytes;

        MidiSemanticDecoder.Result decoded = MidiSemanticDecoder.Decode(bytes);
        long handoffTick = build.Map.SampleToTick(end, Ppq) + result.OriginShiftTicks;

        var bends = new List<(long Tick, int Bend)>();
        foreach (var ep in decoded.Events.Keys)
        {
            foreach (var timed in decoded.Events[ep])
            {
                if (timed.Event is Melanchall.DryWetMidi.Core.PitchBendEvent pb)
                    bends.Add((timed.Tick, pb.PitchValue - 8192));
            }
        }

        // Per (endpoint, tick): at most one bend anywhere.
        Assert.Equal(bends.Count, bends.GroupBy(b => b.Tick).Count());
        // At the handoff tick: exactly one bend, the incoming note's value (-171
        // for 61.5 against base 62 within range 24).
        var handoffBends = bends.Where(b => b.Tick == handoffTick).ToList();
        Assert.Single(handoffBends);
        Assert.Equal(-171, handoffBends[0].Bend);

        // Event order at the handoff tick: NoteOff before PitchBend before NoteOn.
        var handoffEvents = decoded.Events.Values
            .SelectMany(e => e)
            .Where(e => e.Tick == handoffTick)
            .OrderBy(e => e.Event is Melanchall.DryWetMidi.Core.NoteOffEvent ? 0 : e.Event is Melanchall.DryWetMidi.Core.PitchBendEvent ? 1 : 2)
            .ToList();
        Assert.IsType<Melanchall.DryWetMidi.Core.NoteOffEvent>(handoffEvents[0].Event);
        Assert.IsType<Melanchall.DryWetMidi.Core.PitchBendEvent>(handoffEvents[1].Event);
        Assert.IsType<Melanchall.DryWetMidi.Core.NoteOnEvent>(handoffEvents[2].Event);
    }

    // ---- SC-7: round-trip on parsed bytes ---------------------------------------

    [Fact]
    public void MidiRoundTrip_NoEndpointHasMultipleBendsAtSameTick()
    {
        byte[] bytes = Export(Timeline(
            Note("ym2612.0.fm.1", 22_050, 42_050, 60.0, "fm:3",
                new PitchChange(41_950, 0, 62.0)),
            Note("ym2612.0.fm.1", 42_050, 60_000, 61.5, "fm:3"),
            Note("ym2612.0.fm.1", 60_000, 80_000, 64.4, "fm:3",
                new PitchChange(79_900, 0, 66.0)))).Bytes;

        MidiSemanticDecoder.Result decoded = MidiSemanticDecoder.Decode(bytes);
        var bendCounts = new Dictionary<(int Port, int Channel, long Tick), int>();
        foreach (var ep in decoded.Events)
        {
            foreach (var timed in ep.Value)
            {
                if (timed.Event is Melanchall.DryWetMidi.Core.PitchBendEvent)
                {
                    var key = (timed.Port, timed.Channel, timed.Tick);
                    bendCounts[key] = bendCounts.GetValueOrDefault(key) + 1;
                }
            }
        }

        Assert.NotEmpty(bendCounts);
        Assert.All(bendCounts, kv => Assert.True(kv.Value <= 1,
            $"endpoint ({kv.Key.Item1}, {kv.Key.Item2}) has {kv.Value} bends at tick {kv.Key.Item3}"));
    }

    // ---- SC-19: performance (one sort + one grouped pass per track) -------------

    [Fact]
    public void Performance_OneSortAndOneGroupedPassPerTrack()
    {
        var exporter = NewExporter();
        var tracks = new List<MidiTrack>();
        for (int t = 0; t < 4; t++)
        {
            var track = new MidiTrack
            {
                Name = $"track{t}",
                Endpoint = new MidiEndpoint((byte)t, 0),
            };
            int so = 0;
            for (long tick = 0; tick < 500; tick++)
            {
                track.Events.Add(Bend(tick, (int)(tick % 7) - 3, so++));
                if (tick % 3 == 0)
                    track.Events.Add(Bend(tick, (int)(tick % 11) - 5, so++)); // same-tick duplicate
            }
            tracks.Add(track);
        }

        exporter.CanonicalizeEndpointPitchState(tracks);

        Assert.Equal(tracks.Count, exporter.CanonicalizeSortCount);
        Assert.Equal(tracks.Count, exporter.CanonicalizePassCount);
    }

    // ---- T-7: 120 Smash Up real-file acceptance (FR-8) --------------------------

    [Fact]
    public void SmashUp_RealFile_CanonicalizationRemovesOnlyBends()
    {
        string input = Path.Combine(AppContext.BaseDirectory, "testfixtures", "smash-up.spc");
        Assert.True(File.Exists(input), $"SPC fixture not provisioned: {input}");

        VisualizationTimeline timeline = CaptureSmashUp(input);
        Assert.True(timeline.Notes.Count > 0, "SPC capture produced no notes");

        const int ppq = 960;
        var build = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions
        {
            Meter = new Meter(4, 4),
            DetectTempoChanges = true,
        });

        var exporter = new MusicalMidiExporter(build.Map, ppq,
            new MusicalMidiExportOptions { EmitPitchBend = true })
        {
            Diagnostics = build.Diagnostics,
        };

        // Pre-canonicalization output (test-only skip flag) — quantifies the pass.
        var beforeExporter = new MusicalMidiExporter(build.Map, ppq,
            new MusicalMidiExportOptions { EmitPitchBend = true })
        {
            Diagnostics = build.Diagnostics,
            SkipEndpointCanonicalization = true,
        };
        MusicalMidiExportResult beforeResult = beforeExporter.Export(timeline);
        MusicalMidiExportResult afterResult = exporter.Export(timeline);

        MidiSemanticDecoder.Result before = MidiSemanticDecoder.Decode(beforeResult.Bytes);
        MidiSemanticDecoder.Result after = MidiSemanticDecoder.Decode(afterResult.Bytes);

        (int ons, int offs, int bends) Counts(MidiSemanticDecoder.Result d)
        {
            int on = 0, off = 0, bend = 0;
            foreach (var ep in d.Events)
                foreach (var timed in ep.Value)
                {
                    if (timed.Event is Melanchall.DryWetMidi.Core.NoteOnEvent) on++;
                    else if (timed.Event is Melanchall.DryWetMidi.Core.NoteOffEvent) off++;
                    else if (timed.Event is Melanchall.DryWetMidi.Core.PitchBendEvent) bend++;
                }
            return (on, off, bend);
        }

        (int beforeOn, int beforeOff, int beforeBends) = Counts(before);
        (int afterOn, int afterOff, int afterBends) = Counts(after);
        int duplicateTicksBefore = DuplicateBendTickCount(before);
        int duplicateTicksAfter = DuplicateBendTickCount(after);

        // FR-8: canonicalization changes ONLY the bend stream.
        Assert.Equal(beforeOn, afterOn);
        Assert.Equal(beforeOff, afterOff);
        Assert.Equal(
            beforeResult.Tracks.Select(t => t.Endpoint).OrderBy(e => e.Port).ThenBy(e => e.Channel),
            afterResult.Tracks.Select(t => t.Endpoint).OrderBy(e => e.Port).ThenBy(e => e.Channel));
        Assert.True(afterBends < beforeBends, "canonicalization must remove bends");
        Assert.Equal(0, duplicateTicksAfter);
        Assert.True(duplicateTicksBefore > 0, "expected pre-canonicalization duplicate bends");
        Assert.True(beforeBends - afterBends >= duplicateTicksBefore,
            "every removed bend must be accounted for by same-tick duplicates");

        // Report the acceptance numbers (deterministic for this fixed corpus file).
        Console.WriteLine($"[SmashUp] bends before={beforeBends} after={afterBends} " +
            $"duplicateTicksBefore={duplicateTicksBefore} duplicateTicksAfter={duplicateTicksAfter} " +
            $"noteOns={afterOn} noteOffs={afterOff}");
        Assert.True(duplicateTicksBefore >= 3_000,
            $"expected ~3,216 duplicate bend ticks, found {duplicateTicksBefore}");

        // Structural invariants on the canonicalized output.
        // Zero/one-tick note audit: the pre-existing baseline contains EXACTLY ONE
        // re-anchor artifact — endpoint (0,7), note 33, on@49769 off@49770 — a
        // re-anchor landing one tick before the note's end (no source note is
        // shorter than two ticks; the artifact is NOT introduced by canonicalization,
        // which never touches note events — verified by the identical violation set
        // on the pre-canonicalization output below). Fixing the re-anchor itself is
        // explicitly out of scope (spec 36), so the assertion pins the current
        // single occurrence: zero-tick notes are forbidden outright, and one-tick
        // notes may not exceed the documented baseline artifact. (The position was
        // 49746 under the flat fixed-24 bend range; per-domain auto-expansion of the
        // range legitimately shifted the re-anchor pattern — the count stays one.)
        string[] knownOneTickArtifact = { "one-tick note 33 on@49769 off@49770" };
        string[] beforeViolations = NoteDurationViolations(before);
        string[] afterViolations = NoteDurationViolations(after);
        Assert.Equal(beforeViolations, afterViolations); // canonicalization is note-neutral
        Assert.Equal(0, afterViolations.Count(v => v.StartsWith("zero-tick")));
        string[] oneTick = afterViolations.Where(v => v.StartsWith("one-tick")).ToArray();
        Assert.Equal(knownOneTickArtifact, oneTick);
        AssertEveryBendEndpointHas24Rpn(after);
        Assert.Equal(0, DuplicateBendTickCount(after));

        // Same source-relative playback duration before/after canonicalization.
        Assert.Equal(TotalPlaybackTicks(before), TotalPlaybackTicks(after));
    }

    private static int DuplicateBendTickCount(MidiSemanticDecoder.Result d)
    {
        var counts = new Dictionary<(int Port, int Channel, long Tick), int>();
        foreach (var ep in d.Events)
            foreach (var timed in ep.Value)
                if (timed.Event is Melanchall.DryWetMidi.Core.PitchBendEvent)
                {
                    var key = (timed.Port, timed.Channel, timed.Tick);
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
        return counts.Count(kv => kv.Value > 1);
    }

    private static string[] NoteDurationViolations(MidiSemanticDecoder.Result d)
    {
        var violations = new List<string>();
        foreach (var ep in d.Events)
        {
            // Pair each note-off with the pending note-on of the same number and
            // collect zero-tick (off == on) and one-tick (off == on + 1) notes.
            // Re-anchor NoteOff/NoteOn pairs at one tick are legitimate (the old
            // note started earlier); the pairing check above correctly ignores them.
            var pending = new Dictionary<int, long>();
            foreach (var timed in d.Events[ep.Key]
                         .OrderBy(e => e.Tick)
                         .ThenBy(e => e.Event is Melanchall.DryWetMidi.Core.NoteOffEvent ? 0 : 1))
            {
                if (timed.Event is Melanchall.DryWetMidi.Core.NoteOnEvent on && on.Velocity != 0)
                {
                    pending[on.NoteNumber] = timed.Tick;
                }
                else if (timed.Event is Melanchall.DryWetMidi.Core.NoteOffEvent off
                         && pending.TryGetValue(off.NoteNumber, out long onTick))
                {
                    if (timed.Tick <= onTick)
                        violations.Add($"zero-tick note {off.NoteNumber} on@{onTick} off@{timed.Tick}");
                    else if (timed.Tick - onTick == 1)
                        violations.Add($"one-tick note {off.NoteNumber} on@{onTick} off@{timed.Tick}");
                    pending.Remove(off.NoteNumber);
                }
            }
        }
        return violations.ToArray();
    }

    private static void AssertEveryBendEndpointHas24Rpn(MidiSemanticDecoder.Result d)
    {
        foreach (var ep in d.Events)
        {
            bool hasBend = d.Events[ep.Key].Any(e => e.Event is Melanchall.DryWetMidi.Core.PitchBendEvent);
            if (hasBend)
                Assert.True(d.State[ep.Key].BendRange >= 24,
                    $"endpoint {ep.Key} bend-using track must carry an RPN range >= 24");
        }
    }

    private static long TotalPlaybackTicks(MidiSemanticDecoder.Result d) =>
        d.Events.Values.SelectMany(e => e).Select(e => e.Tick).DefaultIfEmpty(0).Max();

    private static VisualizationTimeline CaptureSmashUp(string input)
    {
        string wav = Path.Combine(Path.GetTempPath(), $"mdplayer-smashup-{Guid.NewGuid():N}.wav");
        var sink = new TimelineDecoderEventSink(44_100);
        try
        {
            using IPlaybackCaptureSession session = new SpcPlaybackBackend().Open(
                new FileInfo(input),
                new PlaybackOptions(
                    LoopCount: 1,
                    FadeSeconds: 0,
                    TailSeconds: 0,
                    OutputAudioPath: wav,
                    SampleRate: 44_100),
                sink);
            session.Run();
            return sink.Complete(session.SamplePosition, "test");
        }
        finally
        {
            if (File.Exists(wav)) File.Delete(wav);
        }
    }

    // ---- shared synthetic helpers ----------------------------------------------

    private static VisualizationTimeline Timeline(params NoteEvent[] notes) => new()
    {
        StartSample = 0,
        EndSample = 8_000_000,
        SampleRate = Sr,
        Notes = notes,
    };

    private static NoteEvent Note(string voice, long start, long end, double midi,
        string instrument = "inst", params PitchChange[] changes)
        => new(voice, start, end, 440, midi, instrument, VisualizationNoteMode.Fm, false, changes);

    private static MusicalMidiExportResult Export(VisualizationTimeline timeline, double? bpm = 120)
    {
        var build = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions
        {
            FixedBpm = bpm,
            DetectTempoChanges = true,
        });
        var exporter = new MusicalMidiExporter(build.Map, Ppq,
            new MusicalMidiExportOptions { EmitPitchBend = true })
        {
            Diagnostics = build.Diagnostics,
        };
        return exporter.Export(timeline);
    }
}
