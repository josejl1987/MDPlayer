using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// PR 2: continuous pitch ribbons — linear pitch interpolation and the
/// §8.3 step-preservation rule, evaluated through the deterministic
/// <see cref="PitchContour"/> helper used by the renderer's hot path.
/// </summary>
public sealed class PitchContourTests
{
    private const double SamplesPerFrame = 50; // 1000 Hz / 20 fps

    private static PreparedNote Note(
        long start,
        long end,
        double initial,
        bool retrigger = false,
        params (long Sample, double Midi)[] pitch)
    {
        var points = new PreparedPitchPoint[pitch.Length];
        for (int i = 0; i < pitch.Length; i++)
            points[i] = new PreparedPitchPoint(pitch[i].Sample, pitch[i].Midi);
        return new PreparedNote
        {
            StartSample = start,
            EndSample = end,
            InitialMidiNote = initial,
            Mode = VisualizationNoteMode.Fm,
            InstrumentId = "inst:1",
            IsRetrigger = retrigger,
            Fill = new OverlayColor(200, 200, 200),
            ActiveFill = new OverlayColor(255, 255, 255),
            Accent = new OverlayColor(220, 220, 220),
            CapFill = new OverlayColor(240, 240, 240),
            Pitch = points,
        };
    }

    [Fact]
    public void FlatNote_IsConstant()
    {
        var note = Note(0, 4000, 60);
        Assert.Equal(60, PitchContour.PitchAtSample(note, 1000, SamplesPerFrame));
        Assert.Equal(60, PitchContour.PitchAtSample(note, 3999, SamplesPerFrame));
    }

    [Fact]
    public void InterpolatesLinearly_BetweenPitchPoints()
    {
        var note = Note(500, 3200, 60, false, (1700, 61), (2300, 62));
        Assert.Equal(60, PitchContour.PitchAtSample(note, 500, SamplesPerFrame));
        Assert.Equal(60.5, PitchContour.PitchAtSample(note, 1100, SamplesPerFrame)); // bend from initial
        Assert.Equal(61, PitchContour.PitchAtSample(note, 1700, SamplesPerFrame));
        Assert.Equal(61.5, PitchContour.PitchAtSample(note, 2000, SamplesPerFrame));
        Assert.Equal(62, PitchContour.PitchAtSample(note, 2300, SamplesPerFrame));
        Assert.Equal(62, PitchContour.PitchAtSample(note, 3000, SamplesPerFrame)); // holds to the end
    }

    [Fact]
    public void ContinuousValues_AreNotQuantizedToSemitones()
    {
        var note = Note(0, 4000, 60, false, (1000, 60.42), (2000, 61.17));
        Assert.Equal(60.42, PitchContour.PitchAtSample(note, 1000, SamplesPerFrame));
        double halfway = PitchContour.PitchAtSample(note, 1500, SamplesPerFrame);
        Assert.InRange(halfway, 60.42, 61.17);
        Assert.NotEqual(Math.Round(halfway), halfway);
    }

    [Fact]
    public void RetriggerStep_IsPreservedAsStep()
    {
        // Fast (< 1 frame), large (>= 0.75 semitones), marked retrigger → hold.
        var note = Note(1000, 3000, 60, retrigger: true, (1010, 62));
        Assert.Equal(60, PitchContour.PitchAtSample(note, 1005, SamplesPerFrame));
        Assert.Equal(62, PitchContour.PitchAtSample(note, 1010, SamplesPerFrame));
    }

    [Fact]
    public void SameStep_WithoutRetrigger_IsInterpolated()
    {
        var note = Note(1000, 3000, 60, retrigger: false, (1010, 62));
        Assert.Equal(61, PitchContour.PitchAtSample(note, 1005, SamplesPerFrame));
    }

    [Fact]
    public void SlowRetriggerStep_IsInterpolated()
    {
        // Interval > 1 output frame → continuous even for a retrigger.
        var note = Note(1000, 3000, 60, retrigger: true, (1600, 62));
        Assert.Equal(61, PitchContour.PitchAtSample(note, 1300, SamplesPerFrame));
    }
}
