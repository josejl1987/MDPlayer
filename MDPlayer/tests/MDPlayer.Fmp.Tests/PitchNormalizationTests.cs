using Fmp.Core.Midi;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Melanchall.DryWetMidi.Core;
using Xunit;
using NoteEvent = Fmp.Core.Visualization.NoteEvent;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Synthetic deterministic tests for the pitch-normalization stage (SC-2, SC-8,
/// FR-2/FR-8). The stage is a pure function of the timeline, so every test
/// constructs its own residual/noise distribution and asserts exact behavior —
/// no corpus files, no playback backends, no golden bytes.
/// </summary>
public sealed class PitchNormalizationTests
{
    private const int Sr = 44_100;
    private const int Ppq = 960;

    private static readonly SourceDomainKey DefaultKey = new(
        new DeviceId(ChipType.Ym2608, 0), VoiceKind.Fm, 0);

    private static NoteEvent Note(long start, long end, double initial, params PitchChange[] pitch)
        => new("ym2608.0.fm.1", start, end, 440, initial,
            "ym2608:aaaaaaaa11111111", VisualizationNoteMode.Fm, false, pitch);

    private static VisualizationTimeline Timeline(params NoteEvent[] notes) => new()
    {
        StartSample = 0,
        EndSample = 8_000_000,
        SampleRate = Sr,
        Notes = notes,
    };

    /// <summary>Runs the stage with a fixed single-domain key selector and no warning
    /// sink. A fixed key means ALL notes share one domain — fine for single-domain
    /// fixtures; multi-domain tests pass their own keyFor.</summary>
    private static PitchNormalizationModel Normalize(VisualizationTimeline timeline) =>
        PitchNormalizationStage.Normalize(timeline, PitchNormalizationMode.Fidelity,
            PitchNormalizationThresholds.Default, _ => DefaultKey, null);

    // ---- Pass 1: exact-event dedup on a pitch grid (FR-2 / SC-2) ----------------

    [Fact]
    public void Pass1_SameCellDuplicates_OneRetained_FirstTickKept()
    {
        // 60.01 and 60.0102 land in the same 0.5c grid cell as each other (not the
        // initial's): the second exact duplicate is dropped, the FIRST occurrence's
        // sample is kept, and a change in a different cell survives. All changes sit
        // above the P2 deadband so P2 cannot re-swallow the survivors.
        var model = Normalize(Timeline(Note(0, 4000, 60.0,
                new PitchChange(1000, 0, 60.01),
                new PitchChange(2000, 0, 60.0102),
                new PitchChange(3000, 0, 60.02))));

        NormalizedNoteView view = model.Views.Values.Single();
        Assert.NotNull(view.Changes);
        Assert.Equal(2, view.Changes!.Count);
        Assert.Equal(1000, view.Changes[0].SamplePosition); // first occurrence kept
        Assert.Equal(60.01, view.Changes[0].MidiNote);      // raw value, not quantized
        Assert.Equal(3000, view.Changes[1].SamplePosition);
    }

    [Fact]
    public void Pass1_InitialAnchor_IsStateZero_ReproducingChangeDropped()
    {
        // A change whose quantized pitch equals the initial anchor (state #0) is
        // noise — dropped. The anchor itself is never in the change list.
        var model = Normalize(Timeline(Note(0, 4000, 60.0,
                new PitchChange(1000, 0, 60.0001),
                new PitchChange(2000, 0, 60.01))));

        NormalizedNoteView view = model.Views.Values.Single();
        Assert.Single(view.Changes!);
        Assert.Equal(2000, view.Changes![0].SamplePosition);
        Assert.Equal(60.01, view.Changes[0].MidiNote);
    }

    [Fact]
    public void Pass1_FnumResolutionStep_NotDeduped_DeadbandOwnsIt()
    {
        // A single FNUM step (~0.87c) is NOT an exact duplicate on the 0.5c grid —
        // P1 must not dedup it in FNUM units; the deadband pass owns it later.
        var model = Normalize(Timeline(Note(0, 4000, 60.0,
                new PitchChange(1000, 0, 60.0 + 0.87 / 100.0))));

        NormalizedNoteView view = model.Views.Values.Single();
        Assert.Single(view.Changes!);
    }

    [Fact]
    public void Pass1_SameSampleCollapse_FinalWins()
    {
        // Same-sample writes collapse to the final one BEFORE dedup, mirroring
        // BuildPitchAnchors — the stage never flips a final write to an earlier one.
        var model = Normalize(Timeline(Note(0, 4000, 60.0,
                new PitchChange(1000, 0, 60.0),
                new PitchChange(1000, 0, 60.5),
                new PitchChange(2000, 0, 60.6))));

        NormalizedNoteView view = model.Views.Values.Single();
        Assert.Equal(2, view.Changes!.Count);
        Assert.Equal(60.5, view.Changes[0].MidiNote);
        Assert.Equal(60.6, view.Changes[1].MidiNote);
    }

    [Fact]
    public void Pass1_CountNeverGrows()
    {
        var raw = new PitchChange[50];
        for (int i = 0; i < raw.Length; i++)
            raw[i] = new PitchChange(1000 + i * 10, 0, 60.0 + (i % 5) * 0.0004);
        var model = Normalize(Timeline(Note(0, 4000, 60.0, raw)));

        NormalizedNoteView view = model.Views.Values.Single();
        Assert.True(view.Changes!.Count <= raw.Length,
            "the normalization pipeline must be remove-only (count never grows)");
    }

    // ---- Pass 2: deadband + hysteresis (FR-3 / SC-3) ----------------------------

    private static List<PitchNormalizationStage.PitchSample> Cents(double initial, params double[] cents)
    {
        var list = new List<PitchNormalizationStage.PitchSample>(cents.Length);
        for (int i = 0; i < cents.Length; i++)
            list.Add(new PitchNormalizationStage.PitchSample(1000 + i * 10, initial + cents[i] / 100.0));
        return list;
    }

    [Fact]
    public void Pass2_Isolation_ThrashAroundEnter_SettlesToOneStableState()
    {
        // Design thrash case: 0.74/0.76c alternates around Enter (0.75). A single
        // threshold would toggle forever; hysteresis re-centers on the first retained
        // value so the alternation stops after exactly two retained changes.
        var input = Cents(60.0, 0.74, 0.76, 0.74, 0.76, 0.74, 0.76, 0.74, 0.76, 0.74, 0.76);

        var retained = PitchNormalizationStage.Pass2DeadbandHysteresis(
            60.0, input, enterCents: 0.75, exitCents: 0.5);

        Assert.Equal(2, retained.Count); // the pair that settles; no per-sample toggle
        Assert.Equal(0.76, (retained[0].MidiNote - 60.0) * 100.0, 6);
        Assert.Equal(0.74, (retained[1].MidiNote - 60.0) * 100.0, 6);
    }

    [Fact]
    public void Pass2_Pipeline_ThrashSurvivingPass1_OneStableState()
    {
        // Pipeline-level thrash: 0.74c/1.24c alternation survives P1 (distinct grid
        // cells) and must settle through P2's hysteresis — never oscillate.
        var changes = new PitchChange[40];
        for (int i = 0; i < changes.Length; i++)
            changes[i] = new PitchChange(1000 + i * 10, 0, 60.0 + (i % 2 == 0 ? 0.74 : 1.24) / 100.0);
        var model = Normalize(Timeline(Note(0, 4000, 60.0, changes)));

        NormalizedNoteView view = model.Views.Values.Single();
        Assert.Equal(2, view.Changes!.Count);
    }

    [Fact]
    public void Pass2_Isolation_EnterBoundary_InclusiveDrop()
    {
        // dev ≤ Enter drops (inclusive); dev > Enter retains. Epsilon-bracketed so
        // the test never depends on float rounding at the exact boundary (the
        // bracket must sit above the pass's own 1e-9 float guard).
        var below = PitchNormalizationStage.Pass2DeadbandHysteresis(
            60.0, Cents(60.0, 0.75 - 1e-6), enterCents: 0.75, exitCents: 0.5);
        Assert.Empty(below);

        var above = PitchNormalizationStage.Pass2DeadbandHysteresis(
            60.0, Cents(60.0, 0.75 + 1e-6), enterCents: 0.75, exitCents: 0.5);
        Assert.Single(above);
    }

    [Fact]
    public void Pass2_Isolation_ReSuppressionRequiresSettlingWithinExit()
    {
        // After a retained jump (2.0c) the run stays in Pass until a value lands
        // within Exit (0.5c) — that value re-centers and re-suppression begins.
        var input = Cents(60.0, 2.0, 2.3, 2.0, 2.3, 2.0);

        var retained = PitchNormalizationStage.Pass2DeadbandHysteresis(
            60.0, input, enterCents: 0.75, exitCents: 0.5);

        Assert.Equal(2, retained.Count); // 2.0c retained (jump), 2.3c re-centers
        Assert.Equal(2.0, (retained[0].MidiNote - 60.0) * 100.0, 6);
        Assert.Equal(2.3, (retained[1].MidiNote - 60.0) * 100.0, 6);
    }

    [Fact]
    public void Pass2_FnumVibrato_PreservedInFidelity()
    {
        // A true ±0.87c vibrato (FNUM noise floor) sits ABOVE Enter: every swing is
        // retained — normalization must never swallow real vibrato (FR-3 / D5).
        // Only the leading write that reproduces the initial anchor is dropped.
        var changes = new PitchChange[20];
        for (int i = 0; i < changes.Length; i++)
            changes[i] = new PitchChange(1000 + i * 10, 0, 60.0 + (i % 2 == 0 ? 0.0 : 0.87) / 100.0);
        var model = Normalize(Timeline(Note(0, 4000, 60.0, changes)));

        NormalizedNoteView view = model.Views.Values.Single();
        Assert.Equal(changes.Length - 1, view.Changes!.Count);
        for (int i = 0; i < view.Changes.Count; i++)
            Assert.Equal(i % 2 == 0 ? 0.87 : 0.0, (view.Changes[i].MidiNote - 60.0) * 100.0, 6);
    }

    [Fact]
    public void Pass2_MonotoneDrift_MaxSuppressedDriftWithinEnter()
    {
        // A 0.2c monotone ramp: the machine retains only settled steps, and every
        // dropped change is within Enter (0.75c) of the last retained center — the
        // documented worst-case suppressed drift contract (D5).
        var changes = new PitchChange[20];
        for (int i = 0; i < changes.Length; i++)
            changes[i] = new PitchChange(1000 + i * 10, 0, 60.0 + (0.2 * (i + 1)) / 100.0);
        var model = Normalize(Timeline(Note(0, 4000, 60.0, changes)));

        NormalizedNoteView view = model.Views.Values.Single();
        Assert.True(view.Changes!.Count < changes.Length, "compression must remove drift steps");
        foreach (NormalizedPitchChange c in view.Changes)
            Assert.True((c.MidiNote - 60.0) * 100.0 <= 4.0,
                "retained steps must stay within the drift budget of the ramp");
    }

    // ---- Pass 3: tuning-center detection + bias subtraction (FR-4 / SC-4/SC-5) --

    /// <summary>Ten stable notes in one domain, residuals clustered around
    /// <paramref name="biasCents"/> on ≥ 4 distinct MIDI note numbers. All notes
    /// share the physical voice channel; <paramref name="instrumentId"/> selects
    /// the (parseable or placeholder) instrument identity and
    /// <paramref name="startOffset"/> shifts the whole segment in sample time.</summary>
    private static NoteEvent[] StableTunedNotes(double biasCents, int count = 10,
        string instrumentId = "ym2608:aaaaaaaa11111111", long startOffset = 0)
    {
        double[] bases = { 60, 62, 64, 65, 67, 69, 71, 72, 74, 76 };
        var notes = new NoteEvent[count];
        for (int i = 0; i < count; i++)
        {
            double midi = bases[i % bases.Length] + biasCents / 100.0;
            notes[i] = Note(instrumentId, startOffset + i * 100_000L,
                startOffset + i * 100_000L + 50_000, midi);
        }
        return notes;
    }

    [Fact]
    public void Detector_AcceptPlus21cCluster_ExactBiasAndSubtraction()
    {
        var model = Normalize(Timeline(StableTunedNotes(21.0)));

        DomainPitchStats stats = model.Domains[DefaultKey];
        Assert.True(stats.Accepted);
        Assert.Equal(21.0, stats.TuningCents!.Value, 6);
        Assert.Equal(1.0, stats.BaselineConfidence!.Value, 6);

        // P4: every note's initial is normalized by the bias (60.21 − 0.21 = 60).
        NormalizedNoteView view = model.Views.Values.First(v => v.Source.StartSample == 0);
        Assert.Equal(60.0, view.InitialMidiNote, 9);
    }

    [Fact]
    public void Detector_SubtractsBiasFromChangesToo()
    {
        var timeline = Timeline(new[] { Note(0, 200_000, 60.21,
                new PitchChange(100_000, 0, 60.71))
            }.Concat(StableTunedNotes(21.0).Skip(1)).ToArray());
        var model = Normalize(timeline);

        NormalizedNoteView view = model.Views.Values.Single(v => v.Source.StartSample == 0);
        Assert.Equal(60.0, view.InitialMidiNote, 9);
        Assert.Single(view.Changes!);
        Assert.Equal(60.5, view.Changes![0].MidiNote, 9); // 60.71 − 0.21
    }

    [Fact]
    public void Detector_RejectPortamentoOnly_NoBiasAndWarning()
    {
        // A glide 50c away from the initial exceeds StableDeviationMaxCents: every
        // note is expressive, no stable region exists, no center is accepted (SC-5).
        var notes = new NoteEvent[10];
        for (int i = 0; i < notes.Length; i++)
            notes[i] = Note(i * 100_000L, i * 100_000L + 50_000, 60.0,
                new PitchChange(i * 100_000L + 10_000, 0, 60.5));
        var warnings = new List<string>();
        var model = PitchNormalizationStage.Normalize(Timeline(notes),
            PitchNormalizationMode.Fidelity, PitchNormalizationThresholds.Default, _ => DefaultKey, warnings);

        DomainPitchStats stats = model.Domains[DefaultKey];
        Assert.False(stats.Accepted);
        Assert.Null(stats.TuningCents);
        Assert.Equal(60.0, model.Views.Values.First().InitialMidiNote); // raw, no subtraction
        Assert.Contains(warnings, w => w.Contains("rejected"));
    }

    [Fact]
    public void Detector_RejectFewNotes_SyntheticFixtureIsNoOp()
    {
        // A 2–3 note synthetic fixture fails the persistence floor: bias stays 0 and
        // every view is raw — the structural synthetic no-op (SC-8, D8).
        var model = Normalize(Timeline(
            Note(0, 50_000, 60.21),
            Note(100_000, 150_000, 62.21),
            Note(200_000, 250_000, 64.21)));

        DomainPitchStats stats = model.Domains[DefaultKey];
        Assert.False(stats.Accepted);
        Assert.Equal(60.21, model.Views.Values.First().InitialMidiNote);
    }

    [Fact]
    public void Detector_RejectLowCoverage()
    {
        // Half the attacks at +21c, half at +60c: the largest cluster covers only
        // 50% of stable attacks — below the 60% coverage floor.
        var notes = StableTunedNotes(21.0, count: 5)
            .Concat(StableTunedNotes(60.0, count: 5).Select(n => n with { StartSample = n.StartSample + 10_000_000, EndSample = n.EndSample + 10_000_000 }))
            .ToArray();
        var model = Normalize(Timeline(notes));

        Assert.False(model.Domains[DefaultKey].Accepted);
        Assert.Null(model.Domains[DefaultKey].TuningCents);
    }

    [Fact]
    public void Detector_RejectNonPersistentRecentAttacks()
    {
        // 60% coverage at +21c overall, but the LAST ten attacks sit at +60c:
        // persistence fails even though coverage passes.
        var notes = StableTunedNotes(21.0, count: 12)
            .Concat(StableTunedNotes(60.0, count: 10)
                .Select(n => n with { StartSample = n.StartSample + 20_000_000, EndSample = n.EndSample + 20_000_000 }))
            .ToArray();
        var model = Normalize(Timeline(notes));

        Assert.False(model.Domains[DefaultKey].Accepted);
    }

    [Fact]
    public void Detector_MadGate_RejectsTightThresholds()
    {
        // Residuals split 18c/22c: MAD is exactly 2.0 — accepted at the default
        // 2.0c cap, rejected when the (configurable) cap is tightened to 1.0c.
        double[] bases = { 60, 62, 64, 65, 61, 63, 66, 67 };
        var notes = new NoteEvent[8];
        for (int i = 0; i < notes.Length; i++)
            notes[i] = Note(i * 100_000L, i * 100_000L + 50_000, bases[i] + (i < 4 ? 0.18 : 0.22));
        var tight = new PitchNormalizationThresholds { MadMaxCents = 1.0 };
        var model = PitchNormalizationStage.Normalize(Timeline(notes), PitchNormalizationMode.Fidelity, tight, _ => DefaultKey, null);

        Assert.False(model.Domains[DefaultKey].Accepted);

        var loose = PitchNormalizationStage.Normalize(Timeline(notes),
            PitchNormalizationMode.Fidelity, PitchNormalizationThresholds.Default, _ => DefaultKey, null);
        Assert.True(loose.Domains[DefaultKey].Accepted);
    }

    [Fact]
    public void Detector_SnesDspCap_RejectsLargeBias_AcceptsSmall()
    {
        var snesKey = new SourceDomainKey(new DeviceId(ChipType.SnesDsp, 0), VoiceKind.Pcm, 0);
        var large = PitchNormalizationStage.Normalize(Timeline(StableTunedNotes(30.0)),
            PitchNormalizationMode.Fidelity, PitchNormalizationThresholds.Default, _ => snesKey, null);
        Assert.False(large.Domains[snesKey].Accepted);

        var small = PitchNormalizationStage.Normalize(Timeline(StableTunedNotes(10.0)),
            PitchNormalizationMode.Fidelity, PitchNormalizationThresholds.Default, _ => snesKey, null);
        Assert.True(small.Domains[snesKey].Accepted);
        Assert.Equal(10.0, small.Domains[snesKey].TuningCents!.Value, 6);
    }

    [Fact]
    public void Detector_NoiseFloorBias_NotActedOn_Silently()
    {
        // First-run calibration (FR-6): a sub-deadband tuning center (here +0.1c) is
        // "in tune" — no bias applied, NO warning (high confidence, not low), and the
        // measured mode still reaches the report.
        var warnings = new List<string>();
        var model = PitchNormalizationStage.Normalize(Timeline(StableTunedNotes(0.1)),
            PitchNormalizationMode.Fidelity, PitchNormalizationThresholds.Default, _ => DefaultKey, warnings);

        DomainPitchStats stats = model.Domains[DefaultKey];
        Assert.False(stats.Accepted);
        Assert.Null(stats.TuningCents);
        Assert.Equal(0.1, stats.ResidualModeCents!.Value, 6); // measured, reported
        Assert.Empty(warnings);
        Assert.Equal(60.001, model.Views.Values.First().InitialMidiNote, 9); // raw — no subtraction
    }

    [Fact]
    public void Pass3_ConsecutiveIdentical_DropsEqualStates()
    {
        var input = new[]
        {
            new PitchNormalizationStage.PitchSample(1000, 61.0),
            new PitchNormalizationStage.PitchSample(2000, 61.0),
            new PitchNormalizationStage.PitchSample(3000, 62.0),
            new PitchNormalizationStage.PitchSample(4000, 62.0),
            new PitchNormalizationStage.PitchSample(5000, 63.0),
        };

        var retained = PitchNormalizationStage.Pass3ConsecutiveIdentical(input);

        Assert.Equal(3, retained.Count);
        Assert.Equal(new[] { 61.0, 62.0, 63.0 }, retained.Select(s => s.MidiNote));
    }

    // ---- Synthetic no-op (SC-8): clean fixtures are byte-identical --------------

    [Fact]
    public void Normalize_CleanSyntheticNote_IsIdentity()
    {
        // A clean synthetic note — integer initial pitch, no register noise —
        // passes through unchanged: same initial, empty change list.
        var model = Normalize(Timeline(Note(0, 4000, 60.0)));

        NormalizedNoteView view = model.Views.Values.Single();
        Assert.Equal(60.0, view.InitialMidiNote);
        Assert.NotNull(view.Changes);
        Assert.Empty(view.Changes!);
    }

    [Fact]
    public void Normalize_SemitoneScaleChanges_AllPreserved()
    {
        // Deliberate musical bends (50c+) are far above the dedup grid: untouched.
        var model = Normalize(Timeline(Note(0, 8000, 60.0,
                new PitchChange(1000, 0, 60.5),
                new PitchChange(2000, 0, 61.0),
                new PitchChange(3000, 0, 62.0))));

        NormalizedNoteView view = model.Views.Values.Single();
        Assert.Equal(3, view.Changes!.Count);
        Assert.Equal(new[] { 60.5, 61.0, 62.0 }, view.Changes.Select(c => c.MidiNote));
    }

    [Fact]
    public void Export_Determinism_TwoRunsByteIdentical()
    {
        var timeline = Timeline(
            Note(0, 50_000, 60.0,
                new PitchChange(10_000, 0, 60.001),
                new PitchChange(20_000, 0, 60.3),
                new PitchChange(30_000, 0, 61.0)),
            Note(5_000, 60_000, 62.2));

        byte[] first = Export(timeline);
        byte[] second = Export(timeline);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Export_SubCentRegisterNoise_CollapsesToNoBendInfrastructure()
    {
        // A note whose ONLY pitch movement is sub-grid register noise (both changes
        // land in the initial anchor's 0.5c cell) normalizes to an integer pitch
        // with no changes: the exporter emits a single round note and no bend
        // infrastructure (SC-8-style no-op on the bend family).
        var timeline = Timeline(Note(0, 50_000, 60.0,
            new PitchChange(10_000, 0, 60.0001),
            new PitchChange(20_000, 0, 60.0002)));

        var parsed = Parse(Export(timeline));

        Assert.Empty(parsed.Bends);
        Assert.Single(parsed.NoteOns);
        Assert.Equal(60, parsed.NoteOns[0].Note);
    }

    private static byte[] Export(VisualizationTimeline timeline)
    {
        var build = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions
        {
            FixedBpm = 120,
            Meter = new Meter(4, 4),
            DetectTempoChanges = true,
        });
        var exporter = new MusicalMidiExporter(build.Map, Ppq,
            new MusicalMidiExportOptions { EmitPitchBend = true })
        {
            Diagnostics = build.Diagnostics,
        };
        return exporter.Export(timeline).Bytes;
    }

    private static ParsedMidi Parse(byte[] bytes) => Parser.Parse(bytes);

    // ---- Modes + IR (FR-5 / SC-6/SC-7/SC-8) -------------------------------------

    private static MusicalMidiExportResult ExportResult(VisualizationTimeline timeline,
        MusicalMidiExportOptions? options = null)
    {
        var build = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions
        {
            FixedBpm = 120,
            Meter = new Meter(4, 4),
            DetectTempoChanges = true,
        });
        var exporter = new MusicalMidiExporter(build.Map, Ppq,
            options ?? new MusicalMidiExportOptions { EmitPitchBend = true })
        {
            Diagnostics = build.Diagnostics,
        };
        return exporter.Export(timeline);
    }

    [Fact]
    public void Fidelity_AcceptedBias_RpnTuningRoundTrip()
    {
        // SC-6: a tuned domain exports RPN 0x0002 + CC38 at tick 0 with a null-RPN
        // unselect; bends carry only expressive deviation; played pitch = source.
        var result = ExportResult(Timeline(StableTunedNotes(21.0)));
        MidiSemanticDecoder.Result decoded = MidiSemanticDecoder.Decode(result.Bytes);

        var tuned = decoded.State.Single(kv => kv.Value.HasTuning);
        Assert.Equal(0x26B8, decoded.State[tuned.Key].FineTuningValue); // +21c → 0x2000 + 1720
        Assert.Equal(21.0, decoded.State[tuned.Key].FineTuningCents, 1);

        // Null-RPN unselect after the data entry (CC101/CC100 = 127).
        var ccs = decoded.Events[tuned.Key].Where(e => e.Event is ControlChangeEvent).ToList();
        Assert.Contains(ccs, e => e.Tick == 0 && ((ControlChangeEvent)e.Event).ControlNumber == 100
            && ((ControlChangeEvent)e.Event).ControlValue == 2); // RPN 0x0002 select
        Assert.Contains(ccs, e => e.Tick == 0 && ((ControlChangeEvent)e.Event).ControlNumber == 100
            && ((ControlChangeEvent)e.Event).ControlValue == 127); // null RPN unselect
        Assert.Single(ccs.Where(e => e.Tick == 0 && ((ControlChangeEvent)e.Event).ControlNumber == 101
            && ((ControlChangeEvent)e.Event).ControlValue == 0)); // exactly ONE tuning RPN select

        // Notes play at equal-temperament 60 + fine tuning = source pitch 60.21.
        var on = decoded.Events[tuned.Key].Select(e => e.Event).OfType<NoteOnEvent>().First();
        Assert.Equal(60, on.NoteNumber);
        Assert.Equal(60.21, MidiSemanticDecoder.EffectivePitch(on.NoteNumber, 0,
            decoded.State[tuned.Key].BendRange, (int)Math.Round(decoded.State[tuned.Key].FineTuningCents)), 6);
    }

    [Fact]
    public void Fidelity_NoBendTrack_StillEmitsTuning()
    {
        // A tuned domain whose normalized notes are all integer emits NO bends — but
        // fidelity must still restore the bias via tuning or the notes would play at
        // equal temperament instead of source pitch (FR-5 played pitch = source).
        var result = ExportResult(Timeline(StableTunedNotes(21.0)));
        MidiSemanticDecoder.Result decoded = MidiSemanticDecoder.Decode(result.Bytes);

        Assert.Equal(1, decoded.State.Values.Count(s => s.HasTuning));
        Assert.Empty(decoded.Events.Values.SelectMany(e => e).Where(e => e.Event is PitchBendEvent));
    }

    [Fact]
    public void Fidelity_LargeSourceBias_FoldsToFineOnly_RoundTrips()
    {
        // A TRUE source bias beyond ±100c (here +240c = +2.4 st) is folded by the
        // detector into the current semitone cell: every residual is +40c, so the
        // accepted tuning is +40c — always inside the RPN fine range (±100c). The
        // coarse RPN 0x0001 path can therefore never engage from the stage, and
        // the fine-only restoration reproduces the source pitch exactly (played =
        // source, FR-5). This is the regression proof for the DomainBiasCapCents
        // removal (verify warning 2).
        var result = ExportResult(Timeline(StableTunedNotes(240.0)));
        MidiSemanticDecoder.Result decoded = MidiSemanticDecoder.Decode(result.Bytes);

        // The fold is visible in the report: accepted tuning +40c, not +240c.
        DomainPitchStats d = Assert.Single(result.PitchDiagnostics.Domains);
        Assert.True(d.Accepted);
        Assert.Equal(40.0, d.TuningCents!.Value, 6);

        // ONE domain → ONE active tuning state, fine-only (no coarse semitones).
        var tuned = decoded.State.Single(kv => kv.Value.HasTuning);
        Assert.Equal(40.0, decoded.State[tuned.Key].FineTuningCents, 1);
        Assert.Equal(0, decoded.State[tuned.Key].CoarseTuningSemitones);

        // Fine RPN 0x0002 + CC38 at tick 0, null-RPN unselect, exactly one tuning
        // RPN select — and NO coarse RPN 0x0001 (CC100 == 1) anywhere.
        var ccs = decoded.Events[tuned.Key].Where(e => e.Event is ControlChangeEvent).ToList();
        Assert.Contains(ccs, e => e.Tick == 0 && ((ControlChangeEvent)e.Event).ControlNumber == 101
            && ((ControlChangeEvent)e.Event).ControlValue == 0); // RPN select MSB
        Assert.Contains(ccs, e => e.Tick == 0 && ((ControlChangeEvent)e.Event).ControlNumber == 100
            && ((ControlChangeEvent)e.Event).ControlValue == 2); // RPN 0x0002 (fine)
        Assert.Contains(ccs, e => e.Tick == 0 && ((ControlChangeEvent)e.Event).ControlNumber == 100
            && ((ControlChangeEvent)e.Event).ControlValue == 127); // null RPN unselect
        Assert.DoesNotContain(ccs, e => ((ControlChangeEvent)e.Event).ControlNumber == 100
            && ((ControlChangeEvent)e.Event).ControlValue == 1); // no coarse select

        // Played pitch = source pitch: note 62 + 40c = 62.4 (the +240c source bias).
        var on = decoded.Events[tuned.Key].Select(e => e.Event).OfType<NoteOnEvent>().First();
        Assert.Equal(62, on.NoteNumber);
        Assert.Equal(62.4, MidiSemanticDecoder.EffectivePitch(on.NoteNumber, 0,
            decoded.State[tuned.Key].BendRange, (int)Math.Round(decoded.State[tuned.Key].FineTuningCents)), 6);
    }

    [Fact]
    public void DawFriendly_SnapToEt_NoTuningEvents_NoBiasBends()
    {
        // SC-7: DAW-friendly snaps a +21c bias to equal temperament: zero tuning
        // events, zero bias bends, notes at the ET note number.
        var result = ExportResult(Timeline(StableTunedNotes(21.0)),
            new MusicalMidiExportOptions { EmitPitchBend = true, PitchNormalizationMode = PitchNormalizationMode.DawFriendly });
        MidiSemanticDecoder.Result decoded = MidiSemanticDecoder.Decode(result.Bytes);

        Assert.Empty(decoded.State.Values.Where(s => s.HasTuning));
        Assert.Empty(decoded.Events.Values.SelectMany(e => e).Where(e => e.Event is PitchBendEvent));
        var ons = decoded.Events.Values.SelectMany(e => e).Select(e => e.Event).OfType<NoteOnEvent>()
            .Select(on => (int)on.NoteNumber).OrderBy(n => n).ToList();
        Assert.Equal(new[] { 60, 62, 64, 65, 67, 69, 71, 72, 74, 76 }, ons);
    }

    [Fact]
    public void OffMode_CleanFixture_ByteIdenticalToFidelity()
    {
        // SC-8: a clean synthetic fixture (no register noise) exports byte-identical
        // bytes whether normalization is Fidelity or Off — the detector structurally
        // rejects 2–3 note fixtures (bias 0) and the passes are no-ops on clean data.
        var timeline = Timeline(
            Note(0, 50_000, 60.0),
            Note(100_000, 150_000, 62.2),
            Note(200_000, 250_000, 64.0));

        byte[] fidelity = ExportResult(timeline).Bytes;
        byte[] off = ExportResult(timeline, new MusicalMidiExportOptions
        {
            EmitPitchBend = true,
            PitchNormalizationMode = PitchNormalizationMode.Off,
        }).Bytes;

        Assert.Equal(off, fidelity);
    }

    [Fact]
    public void OffMode_NoTuning_NoDiagnosticsDomains()
    {
        var result = ExportResult(Timeline(StableTunedNotes(21.0)), new MusicalMidiExportOptions
        {
            EmitPitchBend = true,
            PitchNormalizationMode = PitchNormalizationMode.Off,
        });
        MidiSemanticDecoder.Result decoded = MidiSemanticDecoder.Decode(result.Bytes);

        Assert.Empty(decoded.State.Values.Where(s => s.HasTuning));
        Assert.Empty(result.PitchDiagnostics.Domains);
    }

    [Fact]
    public void Report_AllNineMandatedFields_PerDomain()
    {
        // SC-9: every per-domain report entry carries the nine mandated fields.
        var result = ExportResult(Timeline(StableTunedNotes(21.0)));

        DomainPitchStats d = Assert.Single(result.PitchDiagnostics.Domains);
        Assert.Equal(10, d.Attacks);
        Assert.Equal(0, d.RetriggerAttacks);
        Assert.Equal(0, d.RawPitchSamples);
        Assert.Equal(21.0, d.ResidualModeCents!.Value, 6);
        Assert.Equal(0.0, d.StableResidualMadCents!.Value, 6);
        Assert.Equal(1.0, d.BaselineConfidence!.Value, 6);
        Assert.Equal(0, d.RawBendTransitions);
        Assert.Equal(0, d.AfterDedup);
        Assert.Equal(0, d.AfterDeadband);
        Assert.Equal(0, d.ExpressiveTransitions);
        Assert.Equal(21.0, d.TuningCents!.Value, 6);
        Assert.True(d.Accepted);
    }

    [Fact]
    public void Fidelity_TwoTunedDomains_OneTuningEventPerEndpoint()
    {
        // D13b: ONE domain → ONE active tuning state; two tuned domains each get
        // exactly one tuning event on their own endpoint (invariant enforced).
        var notes = StableTunedNotes(21.0)
            .Concat(StableTunedNotes(21.0).Select(n => n with
            {
                ChannelId = "ym2608.0.fm.2",
                StartSample = n.StartSample + 5_000_000,
                EndSample = n.EndSample + 5_000_000,
            }))
            .ToArray();
        var result = ExportResult(Timeline(notes));
        MidiSemanticDecoder.Result decoded = MidiSemanticDecoder.Decode(result.Bytes);

        Assert.Equal(2, decoded.State.Values.Count(s => s.HasTuning));
    }

    // ---- Phase 4 (D1): physical-voice pitch domains + endpoint consensus -------

    /// <summary>Two parseable FM instrument identities on the SAME physical voice —
    /// distinct canonicals, one IdentityFamily.Fm — so the PhysicalVoice track
    /// layout collapses them onto one track/endpoint while the pitch domain stays
    /// ONE SourceDomainKey (device + voice family + source channel; instrument id
    /// is never part of the key, D1).</summary>
    private static readonly (string A, string B) PhysicalVoiceInstruments =
        ("ym2608:0:fm:1", "ym2608:0:fm:2");

    /// <summary>The same physical voice playing a SECOND instrument afterwards:
    /// identical channel id, distinct parseable instrument id, shifted sample
    /// window so the two segments never overlap in time.</summary>
    private static NoteEvent[] ShiftToInstrumentB(NoteEvent[] notes, string instrumentB) =>
        notes.Select(n => n with
        {
            InstrumentId = instrumentB,
            StartSample = n.StartSample + 5_000_000,
            EndSample = n.EndSample + 5_000_000,
        }).ToArray();

    /// <summary>Note on the shared physical voice channel with a specific
    /// (parseable or placeholder) instrument identity.</summary>
    private static NoteEvent Note(string instrumentId, long start, long end, double initial,
        params PitchChange[] pitch)
        => new("ym2608.0.fm.1", start, end, 440, initial, instrumentId,
            VisualizationNoteMode.Fm, false, pitch);

    [Fact]
    public void PhysicalVoice_TuningBias_IsRestored()
    {
        // Spec §1 physical-voice round-trip: a voice carrying ONE instrument with
        // a +21c source bias exports its tuning RESTORED on the endpoint — the
        // played pitch equals the source pitch (FR-5), never equal temperament.
        var result = ExportResult(Timeline(StableTunedNotes(21.0, instrumentId: PhysicalVoiceInstruments.A)));
        MidiSemanticDecoder.Result decoded = MidiSemanticDecoder.Decode(result.Bytes);

        var tuned = decoded.State.Single(kv => kv.Value.HasTuning);
        Assert.Equal(21.0, decoded.State[tuned.Key].FineTuningCents, 1);
        var on = decoded.Events[tuned.Key].Select(e => e.Event).OfType<NoteOnEvent>().First();
        Assert.Equal(60, on.NoteNumber);
        Assert.Equal(60.21, MidiSemanticDecoder.EffectivePitch(on.NoteNumber, 0,
            decoded.State[tuned.Key].BendRange, (int)Math.Round(decoded.State[tuned.Key].FineTuningCents)), 2);
    }

    [Fact]
    public void PhysicalVoice_MultipleInstrumentIds_OnePitchDomain()
    {
        // Two sequential instruments on ONE physical voice share ONE SourceDomainKey
        // (device + voice family + source channel): exactly one detected pitch
        // domain and one tuning state on the endpoint, never per-instrument
        // domains (spec §1 / D1).
        var notes = StableTunedNotes(21.0, instrumentId: PhysicalVoiceInstruments.A)
            .Concat(ShiftToInstrumentB(StableTunedNotes(21.0), PhysicalVoiceInstruments.B))
            .ToArray();
        var result = ExportResult(Timeline(notes));
        MidiSemanticDecoder.Result decoded = MidiSemanticDecoder.Decode(result.Bytes);

        DomainPitchStats domain = Assert.Single(result.PitchDiagnostics.Domains);
        Assert.True(domain.Accepted);
        Assert.Equal(21.0, domain.TuningCents!.Value, 6);
        Assert.Equal(1, decoded.State.Values.Count(s => s.HasTuning));
    }

    [Fact]
    public void PhysicalVoice_InstrumentChange_DoesNotLoseTuning()
    {
        // The instrument change mid-song must NOT lose the physical voice's
        // tuning: notes AFTER the change still play at source pitch — the one
        // endpoint tuning state survives the second instrument's segment.
        var notes = StableTunedNotes(21.0, instrumentId: PhysicalVoiceInstruments.A)
            .Concat(ShiftToInstrumentB(StableTunedNotes(21.0), PhysicalVoiceInstruments.B))
            .ToArray();
        var result = ExportResult(Timeline(notes));
        MidiSemanticDecoder.Result decoded = MidiSemanticDecoder.Decode(result.Bytes);

        var tuned = decoded.State.Single(kv => kv.Value.HasTuning);
        // The second segment starts at sample 5_000_000 → tick ~217_687; its
        // first attack (base note 60, +21c) must decode at exactly the source
        // pitch with the tuning still in force.
        var postChangeOns = decoded.Events[tuned.Key]
            .Where(e => e.Tick >= 200_000 && e.Event is NoteOnEvent on && on.Velocity != 0)
            .Select(e => (NoteOnEvent)e.Event)
            .ToList();
        Assert.NotEmpty(postChangeOns);
        Assert.Equal(60.21, MidiSemanticDecoder.EffectivePitch(postChangeOns[0].NoteNumber, 0,
            decoded.State[tuned.Key].BendRange, (int)Math.Round(decoded.State[tuned.Key].FineTuningCents)), 2);
    }

    [Fact]
    public void Fidelity_TuningRoundTrip_UsesExportDomainKey()
    {
        // End-to-end (spec §1): the export pitch key IS the SourceDomainKey — the
        // device/voice-family/source-channel triple parsed from the real channel
        // identity (never a hash, never an instrument). Tuning, bends and the RPN
        // range round-trip through the exported bytes and the independent oracle
        // reproduces the source pitch within 0.01 st at EVERY NoteOn AND NoteOff
        // (endpoint pitch consensus), with the bend-range cap [1, 127] honored.
        var expressive = Note(PhysicalVoiceInstruments.A, 0, 50_000, 60.21,
            new PitchChange(25_000, 0, 60.71));
        // The expressive note REPLACES the first plain note (same sample window)
        // — the physical voice stays monophonic, so the endpoint walk pairs one
        // NoteOn/NoteOff per source note in export order.
        var notes = new[] { expressive }
            .Concat(StableTunedNotes(21.0, instrumentId: PhysicalVoiceInstruments.A).Skip(1))
            .ToArray();
        var result = ExportResult(Timeline(notes));
        MidiSemanticDecoder.Result decoded = MidiSemanticDecoder.Decode(result.Bytes);

        // The pitch model is keyed by the SOURCE domain — the exact key that
        // drives detection, bend-range calculation, tuning emission and the
        // diagnostics (D1 "exported pitch key = SourceDomainKey").
        DomainPitchStats domain = Assert.Single(result.PitchDiagnostics.Domains);
        Assert.Equal(new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Fm, 0), domain.Key);

        // One tuning state for the one domain, within the [1, 127] range cap.
        var tuned = decoded.State.Single(kv => kv.Value.HasTuning);
        Assert.Equal(21.0, tuned.Value.FineTuningCents, 1);
        Assert.InRange(tuned.Value.BendRange, 1, 127);

        AssertEndpointPitchConsensus(tuned.Key, decoded, notes, fineTuningCents: 21.0);
    }

    [Fact]
    public void ConflictingTuningCenters_RejectsNormalization()
    {
        // Spec §1 "Conflicting tuning centers": two sequential instruments on one
        // physical voice with conflicting bias centers (+21c then −35c) and NO
        // single valid domain center — the largest residual cluster covers only
        // 50% (< 60% floor), so tuning-center normalization is REJECTED. Bias 0 →
        // nothing subtracted → nothing to restore → pitch preserved through bends;
        // NO track-local tuning is emitted.
        var notes = StableTunedNotes(21.0, instrumentId: PhysicalVoiceInstruments.A)
            .Concat(ShiftToInstrumentB(StableTunedNotes(-35.0), PhysicalVoiceInstruments.B))
            .ToArray();
        var result = ExportResult(Timeline(notes));
        MidiSemanticDecoder.Result decoded = MidiSemanticDecoder.Decode(result.Bytes);

        DomainPitchStats domain = Assert.Single(result.PitchDiagnostics.Domains);
        Assert.False(domain.Accepted);
        Assert.Null(domain.TuningCents);
        Assert.Empty(decoded.State.Values.Where(s => s.HasTuning));
        // The rejection is the DETECTOR's (no single valid center), not the
        // endpoint-consensus guard's — one domain, one voice.
        Assert.DoesNotContain(result.Diagnostics.Warnings,
            w => w.Contains("conflicting tuning centers on one MIDI endpoint"));
        Assert.Contains(result.Diagnostics.Warnings, w => w.Contains("rejected"));

        // Pitch preserved through bends: every NoteOn and NoteOff of both
        // instruments decodes within the oracle bound with NO tuning.
        AssertEndpointPitchConsensus(decoded.State.Keys.Single(), decoded, notes, fineTuningCents: 0.0);
    }

    /// <summary>Walks one endpoint's decoded events in exported order maintaining
    /// the live bend state, and asserts ENDPOINT PITCH CONSENSUS (spec §1): at
    /// every NoteOn the decoded effective pitch equals the source note's initial
    /// pitch, and at every NoteOff it equals the source note's end pitch (the
    /// last mid-note pitch change, else the initial) — within the oracle bound of
    /// 0.01 semitone. The bend range and fine tuning are resolved once (both are
    /// set at tick 0 and never change on the endpoint).</summary>
    private static void AssertEndpointPitchConsensus(
        (int Port, int Channel) endpoint,
        MidiSemanticDecoder.Result decoded,
        IReadOnlyList<NoteEvent> sourceNotes,
        double fineTuningCents)
    {
        IReadOnlyList<MidiSemanticDecoder.TimedEvent> events = decoded.Events[endpoint];
        int bendRange = decoded.State[endpoint].BendRange;
        int activeBend = 0;
        var byStart = sourceNotes.OrderBy(n => n.StartSample).ToArray();
        int current = -1;
        foreach (MidiSemanticDecoder.TimedEvent timed in events)
        {
            switch (timed.Event)
            {
                case PitchBendEvent bend:
                    activeBend = bend.PitchValue - 8192;
                    break;
                case NoteOnEvent on when on.Velocity != 0:
                    current++;
                    AssertEffectiveAtBoundary(on.NoteNumber, activeBend, bendRange, fineTuningCents,
                        byStart[current].InitialMidiNote, "NoteOn");
                    break;
                case NoteOnEvent off when off.Velocity == 0:
                    AssertEffectiveAtBoundary(off.NoteNumber, activeBend, bendRange, fineTuningCents,
                        EndPitch(byStart[current]), "NoteOff");
                    break;
                case NoteOffEvent off:
                    AssertEffectiveAtBoundary(off.NoteNumber, activeBend, bendRange, fineTuningCents,
                        EndPitch(byStart[current]), "NoteOff");
                    break;
            }
        }
        Assert.Equal(sourceNotes.Count, current + 1);
    }

    /// <summary>The source pitch in effect at a note's end: the last pitch change
    /// inside [StartSample, EndSample), else the initial pitch.</summary>
    private static double EndPitch(NoteEvent note)
    {
        if (note.Pitch is { Count: > 0 })
        {
            for (int i = note.Pitch.Count - 1; i >= 0; i--)
            {
                PitchChange change = note.Pitch[i];
                if (change.SamplePosition >= note.StartSample && change.SamplePosition < note.EndSample)
                    return change.MidiNote;
            }
        }
        return note.InitialMidiNote;
    }

    private static void AssertEffectiveAtBoundary(int noteNumber, int activeBend, int bendRange,
        double fineTuningCents, double expected, string boundary)
    {
        double actual = MidiSemanticDecoder.EffectivePitch(noteNumber, activeBend, bendRange,
            (int)Math.Round(fineTuningCents));
        Assert.True(Math.Abs(actual - expected) <= 0.01,
            $"{boundary}: decoded effective pitch {actual:0.###} != source pitch {expected:0.###} " +
            $"(note {noteNumber}, bend {activeBend}, range {bendRange})");
    }

    [Fact]
    public void CoarseTuning_ExactRpnSequence_RoundTrips()
    {
        // The coarse RPN 0x0001 emission path (BuildTuning, D10) — unreachable from
        // the stage (the detector's mod-100 residual fold keeps |TuningCents| ≤ 50c)
        // but a first-class IR/writer capability. This test pins the exact byte
        // sequence: RPN 0x0001 select (CC101=0, CC100=1), 14-bit data entry
        // (0x2000 + 2 st → CC6 MSB 0x40, CC38 LSB 0x02), null-RPN unselect — all at
        // tick 0 and ordered before any pitch bend (Rank 2 < Rank 3).
        var track = new MidiTrack
        {
            Name = "t",
            Endpoint = new MidiEndpoint(0, 0),
        };
        track.Events.Add(new MidiTuningEvent(0, 0, 0, CoarseSemitones: 2, FineCents: 0));
        track.Events.Add(new MidiPitchBendEvent(0, 0, 0, 512));
        track.Events.Add(new MidiNoteEvent(0, 0, 0, 60, 90, true));

        var bytes = new global::Fmp.Core.Midi.MidiFileWriter(Ppq).Write(Array.Empty<MidiEventBase>(), new[] { track });
        MidiSemanticDecoder.Result decoded = MidiSemanticDecoder.Decode(bytes);

        var endpoint = decoded.State.Single(kv => kv.Value.HasTuning).Key;
        Assert.Equal(2, decoded.State[endpoint].CoarseTuningSemitones);
        Assert.Equal(0x2000, decoded.State[endpoint].FineTuningValue); // no fine RPN emitted

        var events = decoded.Events[endpoint];
        var ccs = events.Where(e => e.Event is ControlChangeEvent).ToList();
        Assert.Equal(6, ccs.Count);
        // Exact sequence: RPN 0x0001 select → data entry MSB/LSB → null RPN unselect.
        Assert.Equal(new[] { 101, 100, 6, 38, 101, 100 },
            ccs.Select(e => (int)((ControlChangeEvent)e.Event).ControlNumber));
        Assert.Equal(new[] { 0, 1, 0x40, 0x02, 127, 127 },
            ccs.Select(e => (int)((ControlChangeEvent)e.Event).ControlValue));
        Assert.All(ccs, e => Assert.Equal(0, e.Tick)); // whole setup lands at tick 0

        // Tuning precedes the pitch bend at the same tick (deterministic Rank order).
        int firstBendIndex = events.FindIndex(e => e.Event is PitchBendEvent);
        Assert.True(firstBendIndex >= 6, "the tuning setup must sort before any pitch bend");

        // Oracle round-trip: note 60 + coarse 2 st + fine 0 = 62.
        var on = events.Select(e => e.Event).OfType<NoteOnEvent>().First();
        Assert.Equal(60, on.NoteNumber);
        Assert.Equal(62.0, MidiSemanticDecoder.EffectivePitch(on.NoteNumber, 0,
            decoded.State[endpoint].BendRange, 0, decoded.State[endpoint].CoarseTuningSemitones), 9);
    }
}
