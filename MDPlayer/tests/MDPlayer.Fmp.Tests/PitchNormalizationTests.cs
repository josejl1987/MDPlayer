using Fmp.Core.Midi;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Xunit;

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

    private static readonly MidiTrackKey DefaultKey = new(
        new DeviceId(ChipType.Ym2608, 0), VoiceKind.Fm, 0, InstrumentIdentity.Empty);

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
        PitchNormalizationStage.Normalize(timeline, PitchNormalizationThresholds.Default,
            _ => DefaultKey, null);

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
    /// <paramref name="biasCents"/> on ≥ 4 distinct MIDI note numbers.</summary>
    private static NoteEvent[] StableTunedNotes(double biasCents, int count = 10)
    {
        double[] bases = { 60, 62, 64, 65, 67, 69, 71, 72, 74, 76 };
        var notes = new NoteEvent[count];
        for (int i = 0; i < count; i++)
        {
            double midi = bases[i % bases.Length] + biasCents / 100.0;
            notes[i] = Note(i * 100_000L, i * 100_000L + 50_000, midi);
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
            PitchNormalizationThresholds.Default, _ => DefaultKey, warnings);

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
        var model = PitchNormalizationStage.Normalize(Timeline(notes), tight, _ => DefaultKey, null);

        Assert.False(model.Domains[DefaultKey].Accepted);

        var loose = PitchNormalizationStage.Normalize(Timeline(notes),
            PitchNormalizationThresholds.Default, _ => DefaultKey, null);
        Assert.True(loose.Domains[DefaultKey].Accepted);
    }

    [Fact]
    public void Detector_SnesDspCap_RejectsLargeBias_AcceptsSmall()
    {
        var snesKey = new MidiTrackKey(new DeviceId(ChipType.SnesDsp, 0), VoiceKind.Pcm, 0, InstrumentIdentity.Empty);
        var large = PitchNormalizationStage.Normalize(Timeline(StableTunedNotes(30.0)),
            PitchNormalizationThresholds.Default, _ => snesKey, null);
        Assert.False(large.Domains[snesKey].Accepted);

        var small = PitchNormalizationStage.Normalize(Timeline(StableTunedNotes(10.0)),
            PitchNormalizationThresholds.Default, _ => snesKey, null);
        Assert.True(small.Domains[snesKey].Accepted);
        Assert.Equal(10.0, small.Domains[snesKey].TuningCents!.Value, 6);
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
}
