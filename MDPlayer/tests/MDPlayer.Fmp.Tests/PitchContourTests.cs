using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Register and MIDI pitch points are timestamped state changes. The renderer
/// must never invent a ramp before the write occurs.
/// </summary>
public sealed class PitchContourTests
{
    private const double SamplesPerFrame = 50; // deliberately irrelevant to pitch state

    private static PreparedNote Note(
        long start,
        long end,
        double initial,
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
    public void HoldsPreviousValue_UntilTimestampedPitchWrite()
    {
        var note = Note(500, 3200, 60, (1700, 61), (2300, 62));
        Assert.Equal(60, PitchContour.PitchAtSample(note, 500, SamplesPerFrame));
        Assert.Equal(60, PitchContour.PitchAtSample(note, 1699, SamplesPerFrame));
        Assert.Equal(61, PitchContour.PitchAtSample(note, 1700, SamplesPerFrame));
        Assert.Equal(61, PitchContour.PitchAtSample(note, 2299, SamplesPerFrame));
        Assert.Equal(62, PitchContour.PitchAtSample(note, 2300, SamplesPerFrame));
        Assert.Equal(62, PitchContour.PitchAtSample(note, 3000, SamplesPerFrame));
    }

    [Fact]
    public void FractionalPitchValues_ArePreservedWithoutQuantization()
    {
        var note = Note(0, 4000, 60, (1000, 60.42), (2000, 61.17));
        Assert.Equal(60.42, PitchContour.PitchAtSample(note, 1000, SamplesPerFrame));
        Assert.Equal(60.42, PitchContour.PitchAtSample(note, 1500, SamplesPerFrame));
        Assert.Equal(61.17, PitchContour.PitchAtSample(note, 2000, SamplesPerFrame));
    }

    [Fact]
    public void MonotonicCursor_MatchesBinarySearchAtEveryBoundary()
    {
        var note = Note(100, 1000, 60, (250, 60.25), (500, 61), (750, 59.75));
        int cursor = -1;
        for (long sample = 100; sample < 1000; sample++)
        {
            double expected = PitchContour.PitchAtSample(note, sample, SamplesPerFrame);
            double actual = PitchContour.PitchAtSampleMonotonic(
                note,
                sample,
                SamplesPerFrame,
                ref cursor);
            Assert.Equal(expected, actual);
        }
    }
}
