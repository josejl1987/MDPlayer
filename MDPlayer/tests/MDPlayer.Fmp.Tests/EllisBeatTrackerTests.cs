using Fmp.Core.Timing;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class EllisBeatTrackerTests
{
    [Fact]
    public void TempoInduction_RetainsHalfDoubleFamilyInsteadOfApplyingARepair()
    {
        const int sampleRate = 48_000;
        const double bpm = 112;
        long beat = (long)Math.Round(sampleRate * 60.0 / bpm);
        var percussion = Enumerable.Range(0, 64)
            .Select(index => (Sample: index * beat, Strength: index % 4 == 0 ? 1.5 : 0.8))
            .ToArray();
        var melody = Enumerable.Range(0, 128)
            .Select(index => (Sample: (long)Math.Round(index * beat / 2.0), Strength: 0.5))
            .ToArray();

        EllisBeatTrackingResult result = Assert.IsType<EllisBeatTrackingResult>(EllisBeatTracker.Track(
            new[]
            {
                new BeatFeatureStream("percussion", percussion, 1.5),
                new BeatFeatureStream("melody", melody, 0.8),
            },
            sampleRate,
            0,
            percussion[^1].Sample + beat));

        Assert.Contains(result.Candidates, candidate => Math.Abs(candidate.Bpm - 112) < 0.01);
        Assert.Contains(result.Candidates, candidate => Math.Abs(candidate.Bpm - 56) < 0.01);
        Assert.Equal(2, result.Selected.ActiveStreams);
        Assert.True(result.Selected.AgreeingStreams >= 2);
        Assert.True(result.Alternative is not null);
        Assert.True(Math.Abs(result.Selected.Bpm / result.Alternative!.Bpm - 2.0) < 0.01
            || Math.Abs(result.Selected.Bpm / result.Alternative.Bpm - 0.5) < 0.01);
    }

    [Fact]
    public void BeatPath_IsGloballyScoredAndKeepsSourceSamplesUnchanged()
    {
        const int sampleRate = 48_000;
        long beat = sampleRate / 2;
        long[] source = Enumerable.Range(0, 32)
            .Select(index => index * beat)
            .ToArray();

        EllisBeatTrackingResult result = Assert.IsType<EllisBeatTrackingResult>(EllisBeatTracker.Track(
            new[]
            {
                new BeatFeatureStream(
                    "kick",
                    source.Select(sample => (sample, 1.0)).ToArray(),
                    1.0),
            },
            sampleRate,
            0,
            source[^1] + beat));

        EllisBeatCandidate quarterCandidate = result.Candidates.Single(
            candidate => Math.Abs(candidate.Bpm - 120) < 0.01);
        Assert.True(quarterCandidate.PathScore > 0.2);
        Assert.InRange(result.Selected.Bpm, 40, 240);
        Assert.True(result.Selected.Score > 0.2);
        Assert.True(result.Selected.BeatSamples.Zip(result.Selected.BeatSamples.Skip(1),
            (left, right) => right >= left).All(value => value));
        Assert.Equal(0, source[0]);
        Assert.Equal(31 * beat, source[^1]);
    }

    [Fact]
    public void NonMetricalSecondCandidate_DoesNotBecomeTempoAmbiguity()
    {
        const int sampleRate = 48_000;
        long beat = sampleRate / 2;
        EllisBeatTrackingResult result = Assert.IsType<EllisBeatTrackingResult>(EllisBeatTracker.Track(
            new[]
            {
                new BeatFeatureStream(
                    "pulse",
                    Enumerable.Range(0, 64)
                        .Select(index => (Sample: index * beat, Strength: 1.0))
                        .ToArray(),
                    1.0),
            },
            sampleRate,
            0,
            64 * beat));

        Assert.All(
            result.Candidates.Skip(1).Where(candidate =>
                Math.Abs(candidate.Bpm / result.Selected.Bpm - 0.5) >= 0.01
                && Math.Abs(candidate.Bpm / result.Selected.Bpm - 2.0) >= 0.01),
            candidate => Assert.NotEqual(candidate.Bpm, result.Alternative?.Bpm));
    }
}
