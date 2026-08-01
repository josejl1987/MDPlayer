using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// PR 2: preparation-time pitch-point simplification (§8.4) — deterministic
/// bounded-error simplification that preserves extrema, significant steps,
/// and the first/last points, all within 0.15 vertical pixels at 1080p.
/// </summary>
public sealed class PitchSimplificationTests
{
    private const int SampleRate = 1000;

    private static NoteEvent Note(long start, long end, double initial, params PitchChange[] pitch)
        => new(
            "ym2608.0.fm.1", start, end, 261.63, initial,
            "ym2608:aaaaaa1111111111", VisualizationNoteMode.Fm, false, pitch);

    private static VisualizationTimeline Timeline(params NoteEvent[] notes)
        => new()
        {
            SampleRate = SampleRate,
            StartSample = 0,
            EndSample = 100_000,
            Instruments =
            [
                new InstrumentDefinition("ym2608:aaaaaa1111111111", "fm", 4, 3, 0, 2, Array.Empty<FmOperatorDefinition>()),
            ],
            Notes = notes,
            Rhythm = Array.Empty<RhythmEvent>(),
        };

    private static PreparedNote BuildPitchedNote(NoteEvent note)
    {
        var layout = new OverlayLayout(1920, 1080, 0.75, 2.25);
        var scene = OverlaySceneBuilder.Build(Timeline(note), layout);
        return scene.Panels[0].MainNotes[0];
    }

    [Fact]
    public void CollinearDenseSteps_CollapseToEndpoints()
    {
        // A 100-point ramp in 2-cent steps: perfectly collinear, so RDP keeps
        // only the endpoints (no point is an extremum or a > 0.1 semitone step).
        var pitch = new PitchChange[100];
        for (int i = 0; i < pitch.Length; i++)
            pitch[i] = new PitchChange(1000 + i * 100, 0, 60.0 + i * 0.02);

        var prepared = BuildPitchedNote(Note(500, 12_000, 60.0, pitch));
        Assert.True(prepared.Pitch.Length <= 2,
            $"Expected ~2 points after simplification, got {prepared.Pitch.Length}.");
        Assert.Equal(1000, prepared.Pitch[0].SamplePosition);
        Assert.Equal(60.0, prepared.Pitch[0].MidiNote);
        Assert.Equal(10_900, prepared.Pitch[^1].SamplePosition);
    }

    [Fact]
    public void VibratoExtrema_ArePreserved()
    {
        // Alternating valleys (60.0) and peaks (60.4): every point is either a
        // local extremum or adjacent to a > 0.1 semitone change → all kept.
        var pitch = new PitchChange[21];
        for (int i = 0; i < pitch.Length; i++)
            pitch[i] = new PitchChange(1000 + i * 200, 0, i % 2 == 0 ? 60.0 : 60.4);

        var prepared = BuildPitchedNote(Note(500, 12_000, 60.0, pitch));
        Assert.Equal(pitch.Length, prepared.Pitch.Length);
        for (int i = 0; i < pitch.Length; i++)
            Assert.Equal(pitch[i].MidiNote, prepared.Pitch[i].MidiNote);
    }

    [Fact]
    public void SimplifiedContour_StaysWithinVisualErrorBound()
    {
        // A deterministic pseudo-vibrato with small steps. The simplified
        // contour must deviate from the original by at most 0.15 px at 1080p
        // (0.15 px × 12-semitone minimum camera span / 138 px lane height).
        var pitch = new PitchChange[200];
        double phase = 0;
        for (int i = 0; i < pitch.Length; i++)
        {
            phase += 0.11;
            pitch[i] = new PitchChange(1000 + i * 40, 0, 60.0 + 0.5 * Math.Sin(phase) + (i % 7) * 0.003);
        }

        var prepared = BuildPitchedNote(Note(500, 20_000, 60.0, pitch));
        Assert.True(prepared.Pitch.Length < pitch.Length,
            "Simplification removed nothing from a dense bend contour.");

        const double laneHeight = 138; // 1080p main lane
        const double minSpan = 12;     // PitchCamera minimum span
        double pixelsPerSemitone = laneHeight / minSpan;
        double maxErrorPixels = 0;
        for (int i = 0; i < pitch.Length; i++)
        {
            double original = pitch[i].MidiNote;
            double simplified = PitchContour.PitchAtSample(prepared, pitch[i].SamplePosition, samplesPerFrame: 50);
            maxErrorPixels = Math.Max(maxErrorPixels, Math.Abs(original - simplified) * pixelsPerSemitone);
        }

        Assert.True(maxErrorPixels <= 0.15 + 1e-9,
            $"Simplification error {maxErrorPixels:F4} px exceeds the 0.15 px bound.");
    }

    [Fact]
    public void FirstAndLastPoints_AreAlwaysPreserved()
    {
        var pitch = new PitchChange[50];
        for (int i = 0; i < pitch.Length; i++)
            pitch[i] = new PitchChange(1000 + i * 100, 0, 60.0 + (i % 3) * 0.01);

        var prepared = BuildPitchedNote(Note(500, 12_000, 60.0, pitch));
        Assert.True(prepared.Pitch.Length >= 2);
        Assert.Equal(pitch[0].SamplePosition, prepared.Pitch[0].SamplePosition);
        Assert.Equal(pitch[0].MidiNote, prepared.Pitch[0].MidiNote);
        Assert.Equal(pitch[^1].SamplePosition, prepared.Pitch[^1].SamplePosition);
        Assert.Equal(pitch[^1].MidiNote, prepared.Pitch[^1].MidiNote);
    }

    [Fact]
    public void UnpitchedPoints_AreFilteredOut()
    {
        var note = Note(500, 4000, 60.0,
            new PitchChange(1000, 0, -1),
            new PitchChange(1500, 0, 61.0));

        var prepared = BuildPitchedNote(note);
        Assert.Single(prepared.Pitch);
        Assert.Equal(1500, prepared.Pitch[0].SamplePosition);
    }
}
