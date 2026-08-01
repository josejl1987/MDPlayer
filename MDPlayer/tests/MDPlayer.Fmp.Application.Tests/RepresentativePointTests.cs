using Fmp.Application.Contracts;
using Fmp.Application.Preview;
using Fmp.Core.Visualization;
using Xunit;

namespace Fmp.Application.Tests;

/// <summary>Minimal timeline builder (Core internals are visible to this test assembly).</summary>
internal static class TimelineFixture
{
    public static VisualizationTimeline Create()
    {
        const int sampleRate = 1_000;
        return new VisualizationTimeline
        {
            SampleRate = sampleRate,
            StartSample = 0,
            EndSample = 20_000,
            Instruments = Array.Empty<InstrumentDefinition>(),
            Notes =
            [
                new NoteEvent("ym2608.0.fm.1", 2_000, 4_000, 261.63, 60, "ym2608:aaaa", VisualizationNoteMode.Fm, false, Array.Empty<PitchChange>()),
                new NoteEvent("ym2608.0.fm.1", 3_000, 5_000, 293.66, 62, "ym2608:bbbb", VisualizationNoteMode.Fm, false, Array.Empty<PitchChange>()),
                new NoteEvent("ym2608.0.fm.2", 10_000, 12_000, 130.81, 48, "ym2608:cccc", VisualizationNoteMode.Fm, false, Array.Empty<PitchChange>()),
            ],
            Rhythm =
            [
                new RhythmEvent("ym2608.0.rhythm.1", "ym2608.0.rhythm.1", 1_500, 0.8f, 0f),
            ],
            SamplePlayback =
            [
                new SamplePlaybackEvent("ym2608.0.adpcm.1", 6_000, 9_000, "sample-1", null, 1.0, 1.0f, 0f, false, false),
            ],
        };
    }
}

public class RepresentativePointTests
{
    [Fact]
    public void Compute_CoversAllCategories()
    {
        IReadOnlyList<RepresentativePoint> points =
            RepresentativePointAnalyzer.Compute(TimelineFixture.Create(), introSeconds: 0.75);

        Assert.NotEmpty(points);

        Assert.Contains(points, point => point.Kind == "intro-end" && Math.Abs(point.TimeSeconds - 0.75) < 0.001);
        // First event is the rhythm at sample 1500 → 1.5 s.
        Assert.Contains(points, point => point.Kind == "first-event" && Math.Abs(point.TimeSeconds - 1.5) < 0.001);
        Assert.Contains(points, point => point.Kind == "densest");
        Assert.Contains(points, point => point.Kind == "widest-pitch");
        Assert.Contains(points, point => point.Kind == "first-percussion" && Math.Abs(point.TimeSeconds - 1.5) < 0.001);
        Assert.Contains(points, point => point.Kind == "first-sample" && Math.Abs(point.TimeSeconds - 6.0) < 0.001);
        Assert.Contains(points, point => point.Kind == "middle" && Math.Abs(point.TimeSeconds - 10.0) < 0.001);
        Assert.Contains(points, point => point.Kind == "near-outro" && Math.Abs(point.TimeSeconds - 17.0) < 0.001);
    }

    [Fact]
    public void Compute_TimesAreWithinTrackRange()
    {
        IReadOnlyList<RepresentativePoint> points =
            RepresentativePointAnalyzer.Compute(TimelineFixture.Create(), introSeconds: 0.75);

        foreach (RepresentativePoint point in points)
        {
            Assert.InRange(point.TimeSeconds, 0, 20.0);
            Assert.False(string.IsNullOrEmpty(point.Kind));
            Assert.False(string.IsNullOrEmpty(point.Label));
        }
    }

    [Fact]
    public void Compute_EmptyTimeline_OnlyIntroPoint()
    {
        var empty = new VisualizationTimeline
        {
            SampleRate = 1_000,
            StartSample = 0,
            EndSample = 0,
            Notes = Array.Empty<NoteEvent>(),
            Rhythm = Array.Empty<RhythmEvent>(),
        };
        IReadOnlyList<RepresentativePoint> points = RepresentativePointAnalyzer.Compute(empty, introSeconds: 0.75);
        Assert.All(points, point => Assert.Contains(point.Kind, new[] { "intro-end" }));
    }
}
