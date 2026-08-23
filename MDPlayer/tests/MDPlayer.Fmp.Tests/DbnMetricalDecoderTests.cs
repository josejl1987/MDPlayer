using Fmp.Core.Timing;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class DbnMetricalDecoderTests
{
    private const int SampleRate = 48_000;
    private const long Quarter = 24_000;
    private const long Eighth = Quarter / 2;

    [Fact]
    public void Viterbi_ResolvesFourFourFromIndependentAccentAndSurfaceStreams()
    {
        DbnMetricalResult result = Decode(
            Enumerable.Range(0, 256)
                .Select(index => (Sample: index * Eighth, Strength: 0.35))
                .ToArray(),
            Enumerable.Range(0, 32)
                .Select(index => (Sample: index * Quarter, Strength: index % 4 == 0 ? 2.0 : 1.2))
                .ToArray(),
            endSample: 32 * Quarter,
            structuralEvidence: true);

        Assert.True(result.MeterResolved,
            $"meter={result.Selected.Meter}, score={result.Selected.Score}, " +
            $"meterMargin={result.Selected.MeterMargin}, downbeatMargin={result.Selected.DownbeatMargin}");
        Assert.True(result.DownbeatResolved);
        Assert.True(result.Selected.Meter == new Meter(4, 4),
            string.Join(", ", result.Candidates.Select(candidate =>
                $"{candidate.Meter}:{candidate.Score:0.###}")));
        Assert.Equal(0, result.Selected.DownbeatSample);
    }

    [Fact]
    public void Viterbi_RecognizesCompoundSixEightGrouping()
    {
        DbnMetricalResult result = Decode(
            Enumerable.Range(0, 192)
                .Select(index => (Sample: index * Eighth, Strength: 0.30))
                .ToArray(),
            Enumerable.Range(0, 32)
                .Where(index => index % 6 is 0 or 3)
                .Select(index => (Sample: index * Eighth, Strength: 2.0))
                .ToArray(),
            endSample: 32 * Eighth,
            structuralEvidence: true);

        Assert.True(result.MeterResolved,
            $"meter={result.Selected.Meter}, score={result.Selected.Score}, " +
            $"meterMargin={result.Selected.MeterMargin}, downbeatMargin={result.Selected.DownbeatMargin}");
        Assert.Equal(new Meter(6, 8), result.Selected.Meter);
    }

    [Fact]
    public void CloseEvidenceMarginsRemainUnresolved()
    {
        DbnMetricalResult result = Decode(
            Enumerable.Range(0, 64)
                .Select(index => (Sample: index * Eighth, Strength: 0.3))
                .ToArray(),
            Array.Empty<(long Sample, double Strength)>(),
            endSample: 32 * Quarter);

        Assert.False(result.MeterResolved);
        Assert.False(result.DownbeatResolved);
    }

    [Fact]
    public void JointViterbi_AllowsTempoFamilyChangeWithATransitionPenalty()
    {
        const long firstSegmentEnd = 8 * Quarter;
        const long secondBeat = 16_000; // 180 BPM
        var onsets = Enumerable.Range(0, 8)
            .Select(index => (Sample: index * Quarter, Strength: 1.5))
            .Concat(Enumerable.Range(0, 16)
                .Select(index => (Sample: firstSegmentEnd + index * secondBeat, Strength: 1.5)))
            .ToArray();

        DbnMetricalResult? result = DbnMetricalDecoder.Decode(
            new[]
            {
                new DbnTempoHypothesis(120, 0, 0.20),
                new DbnTempoHypothesis(180, 0, 1.00),
            },
            new[]
            {
                new BeatFeatureStream("percussion", onsets, 1.5),
                new BeatFeatureStream("accent", onsets, 1.5),
            },
            SampleRate,
            0,
            firstSegmentEnd + 16 * secondBeat,
            new[] { 0L });

        Assert.NotNull(result);
        Assert.Equal(180, result!.Selected.Tempo.Bpm);
        Assert.True(result.TempoSwitchCount >= 1,
            $"selected={result.Selected.Tempo.Bpm} BPM; " +
            $"candidates={string.Join(", ", result.Candidates.Select(c => c.Tempo.Bpm))}");
    }

    private static DbnMetricalResult Decode(
        IReadOnlyList<(long Sample, double Strength)> surface,
        IReadOnlyList<(long Sample, double Strength)> accents,
        long endSample,
        bool structuralEvidence = false)
    {
        DbnMetricalResult? result = DbnMetricalDecoder.Decode(
            new[] { new DbnTempoHypothesis(120, 0, 0.8) },
            new[]
            {
                new BeatFeatureStream("percussion", surface, 1.4),
                new BeatFeatureStream("accent", accents, 1.5),
            },
            SampleRate,
            0,
            endSample,
            structuralEvidence ? new[] { 0L } : Array.Empty<long>());
        return Assert.IsType<DbnMetricalResult>(result);
    }
}
