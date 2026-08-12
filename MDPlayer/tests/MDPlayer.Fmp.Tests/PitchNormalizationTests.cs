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

    // ---- Pass 1: exact-event dedup on a pitch grid (FR-2 / SC-2) ----------------

    [Fact]
    public void Pass1_SameCellDuplicates_OneRetained_FirstTickKept()
    {
        // 60.001 and 60.0009 land in the same 0.5c grid cell as each other (not the
        // initial's): the second exact duplicate is dropped, the FIRST occurrence's
        // sample is kept, and a change in a different cell survives.
        var model = PitchNormalizationStage.Normalize(
            Timeline(Note(0, 4000, 60.0,
                new PitchChange(1000, 0, 60.001),
                new PitchChange(2000, 0, 60.0009),
                new PitchChange(3000, 0, 60.003))),
            PitchNormalizationThresholds.Default);

        NormalizedNoteView view = model.Views.Values.Single();
        Assert.NotNull(view.Changes);
        Assert.Equal(2, view.Changes!.Count);
        Assert.Equal(1000, view.Changes[0].SamplePosition); // first occurrence kept
        Assert.Equal(60.001, view.Changes[0].MidiNote);     // raw value, not quantized
        Assert.Equal(3000, view.Changes[1].SamplePosition);
    }

    [Fact]
    public void Pass1_InitialAnchor_IsStateZero_ReproducingChangeDropped()
    {
        // A change whose quantized pitch equals the initial anchor (state #0) is
        // noise — dropped. The anchor itself is never in the change list.
        var model = PitchNormalizationStage.Normalize(
            Timeline(Note(0, 4000, 60.0,
                new PitchChange(1000, 0, 60.0001),
                new PitchChange(2000, 0, 60.01))),
            PitchNormalizationThresholds.Default);

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
        var model = PitchNormalizationStage.Normalize(
            Timeline(Note(0, 4000, 60.0,
                new PitchChange(1000, 0, 60.0 + 0.87 / 1200.0))),
            PitchNormalizationThresholds.Default);

        NormalizedNoteView view = model.Views.Values.Single();
        Assert.Single(view.Changes!);
    }

    [Fact]
    public void Pass1_SameSampleCollapse_FinalWins()
    {
        // Same-sample writes collapse to the final one BEFORE dedup, mirroring
        // BuildPitchAnchors — the stage never flips a final write to an earlier one.
        var model = PitchNormalizationStage.Normalize(
            Timeline(Note(0, 4000, 60.0,
                new PitchChange(1000, 0, 60.0),
                new PitchChange(1000, 0, 60.5),
                new PitchChange(2000, 0, 60.6))),
            PitchNormalizationThresholds.Default);

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
        var model = PitchNormalizationStage.Normalize(
            Timeline(Note(0, 4000, 60.0, raw)),
            PitchNormalizationThresholds.Default);

        NormalizedNoteView view = model.Views.Values.Single();
        Assert.True(view.Changes!.Count <= raw.Length,
            "the normalization pipeline must be remove-only (count never grows)");
    }

    // ---- Synthetic no-op (SC-8): clean fixtures are byte-identical --------------

    [Fact]
    public void Normalize_CleanSyntheticNote_IsIdentity()
    {
        // A clean synthetic note — integer initial pitch, no register noise —
        // passes through unchanged: same initial, empty change list.
        var model = PitchNormalizationStage.Normalize(
            Timeline(Note(0, 4000, 60.0)),
            PitchNormalizationThresholds.Default);

        NormalizedNoteView view = model.Views.Values.Single();
        Assert.Equal(60.0, view.InitialMidiNote);
        Assert.NotNull(view.Changes);
        Assert.Empty(view.Changes!);
    }

    [Fact]
    public void Normalize_SemitoneScaleChanges_AllPreserved()
    {
        // Deliberate musical bends (50c+) are far above the dedup grid: untouched.
        var model = PitchNormalizationStage.Normalize(
            Timeline(Note(0, 8000, 60.0,
                new PitchChange(1000, 0, 60.5),
                new PitchChange(2000, 0, 61.0),
                new PitchChange(3000, 0, 62.0))),
            PitchNormalizationThresholds.Default);

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
