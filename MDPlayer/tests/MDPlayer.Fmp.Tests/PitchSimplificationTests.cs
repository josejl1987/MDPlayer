using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Preparation-time compression for zero-order-hold pitch state. Compression
/// may omit sub-pixel changes, but must not turn state changes into ramps.
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
    public void SignificantRegisterSteps_ArePreserved()
    {
        // Two-cent register steps exceed the 1080p sub-pixel tolerance.
        var pitch = new PitchChange[100];
        for (int i = 0; i < pitch.Length; i++)
            pitch[i] = new PitchChange(1000 + i * 100, 0, 60.0 + i * 0.02);

        var prepared = BuildPitchedNote(Note(500, 12_000, 60.0, pitch));
        Assert.Equal(pitch.Length, prepared.Pitch.Length);
        Assert.Equal(1000, prepared.Pitch[0].SamplePosition);
        Assert.Equal(60.0, prepared.Pitch[0].MidiNote);
        Assert.Equal(10_900, prepared.Pitch[^1].SamplePosition);
    }

    [Fact]
    public void SubPixelSubSemitone_IsCollapsedWithinTolerance()
    {
        var prepared = BuildPitchedNote(Note(500, 4000, 60.0,
            new PitchChange(1000, 0, 60.0),
            new PitchChange(2000, 0, 60.001),
            new PitchChange(3000, 0, 60.0)));

        Assert.True(prepared.Pitch.Length <= 2);
        Assert.Equal(60.0, prepared.Pitch[0].MidiNote, 6);
    }

    [Fact]
    public void StepAwareCompression_StaysWithinVisualErrorBound()
    {
        // Small monotonic state changes may be coalesced, but zero-order-hold
        // evaluation at every original write must remain within 0.15 pixels.
        var pitch = new PitchChange[200];
        for (int i = 0; i < pitch.Length; i++)
            pitch[i] = new PitchChange(1000 + i * 40, 0, 60.0 + i * 0.003);

        var prepared = BuildPitchedNote(Note(500, 20_000, 60.0, pitch));
        Assert.True(prepared.Pitch.Length < pitch.Length,
            "Step-aware compression removed nothing from sub-pixel changes.");

        const double laneHeight = 130; // 1080p main lane (header raised to 28px)
        const double minSpan = 12;     // PitchCamera minimum span
        double pixelsPerSemitone = laneHeight / minSpan;
        double maxErrorPixels = 0;
        foreach (PitchChange point in pitch)
        {
            double original = point.MidiNote;
            double simplified = PitchContour.PitchAtSample(
                prepared, point.SamplePosition, samplesPerFrame: 50);
            maxErrorPixels = Math.Max(maxErrorPixels, Math.Abs(original - simplified) * pixelsPerSemitone);
        }

        Assert.True(maxErrorPixels <= 0.15 + 1e-6,
            $"Simplification error {maxErrorPixels:F4} px exceeds the 0.15 px bound.");
    }

    [Fact]
    public void SameSampleDuplicates_KeepTheLastState()
    {
        var prepared = BuildPitchedNote(Note(500, 4000, 60.0,
            new PitchChange(1000, 0, 60.25),
            new PitchChange(1000, 0, 61.5),
            new PitchChange(2000, 0, 62.0)));

        Assert.Equal(2, prepared.Pitch.Length);
        Assert.Equal(61.5, prepared.Pitch[0].MidiNote);
    }
}
