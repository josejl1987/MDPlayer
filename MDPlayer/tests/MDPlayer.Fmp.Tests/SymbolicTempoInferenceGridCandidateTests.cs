#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Fmp.Core.Timing;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Contract tests for <see cref="SymbolicTempoInference.BuildGridCandidates"/>:
/// the PROVISIONAL hierarchy meter is a prior, never a filter — the full default
/// meter set (4/4, 3/4, 6/8) is always evaluated unless the user overrides the
/// meter explicitly; and inferred downbeats are SOURCE-ALIGNED — at or before the
/// source start, one per beat phase, never shifted or snapped to an arbitrary grid
/// (explicit downbeats generate exactly one phase, the sample itself).
/// </summary>
public sealed class SymbolicTempoInferenceGridCandidateTests
{
    private const int Sr = 44_100;

    private static readonly TempoCandidate Search120 = new(
        Bpm: 120,
        PhaseSample: 0,
        Score: 1.0,
        Ambiguity: TempoAmbiguity.None);

    private static IReadOnlyList<MusicalGridCandidate> Build(
        Meter? explicitMeter = null,
        Meter? provisionalMeter = null,
        long? explicitDownbeatSample = null) =>
        SymbolicTempoInference.BuildGridCandidates(
            new[] { Search120 },
            fallbackBpm: 120,
            requiredBpm: 120,
            requiredPhaseSample: 0,
            requiredScore: 1.0,
            sampleRate: Sr,
            startSample: 0,
            explicitMeter: explicitMeter,
            provisionalMeter: provisionalMeter,
            explicitDownbeatSample: explicitDownbeatSample);

    [Fact]
    public void ProvisionalMeter_NeverRestrictsCandidateMeters()
    {
        // A provisional 3/4 hierarchy reading must not prune 4/4 or 6/8: the
        // whole default meter set is evaluated at the winning BPM family.
        IReadOnlyList<MusicalGridCandidate> candidates = Build(provisionalMeter: new Meter(3, 4));

        Assert.Contains(candidates, candidate =>
            Math.Abs(candidate.Bpm - 120) < 1e-9 && candidate.Meter == new Meter(4, 4));
        Assert.Contains(candidates, candidate =>
            Math.Abs(candidate.Bpm - 120) < 1e-9 && candidate.Meter == new Meter(3, 4));
        Assert.Contains(candidates, candidate =>
            Math.Abs(candidate.Bpm - 120) < 1e-9 && candidate.Meter == new Meter(6, 8));
    }

    [Fact]
    public void ProvisionalMeter_AddsSmallPriorToMatchingMeterOnly()
    {
        IReadOnlyList<MusicalGridCandidate> candidates = Build(provisionalMeter: new Meter(3, 4));

        // Same family score and same phase: the provisional meter's bonus is
        // exactly the small prior constant, applied only to matching candidates.
        MusicalGridCandidate match = candidates.Single(candidate =>
            Math.Abs(candidate.Bpm - 120) < 1e-9
            && candidate.Meter == new Meter(3, 4)
            && candidate.FirstDownbeatQuarter == 0.0);
        MusicalGridCandidate other = candidates.Single(candidate =>
            Math.Abs(candidate.Bpm - 120) < 1e-9
            && candidate.Meter == new Meter(4, 4)
            && candidate.FirstDownbeatQuarter == 0.0);

        Assert.Equal(0.02, match.Score - other.Score, precision: 9);
        // The bonus is far below any structural-evidence signal (must stay small).
        Assert.True(match.Score - other.Score < 0.05);
    }

    [Fact]
    public void ExplicitMeter_StillHardRestricts()
    {
        // User override keeps its hard-restrict semantics: only 3/4 is evaluated.
        IReadOnlyList<MusicalGridCandidate> candidates = Build(
            explicitMeter: new Meter(3, 4),
            provisionalMeter: new Meter(4, 4));

        Assert.NotEmpty(candidates);
        Assert.All(candidates, candidate => Assert.Equal(new Meter(3, 4), candidate.Meter));
    }

    [Fact]
    public void InferredDownbeats_AreSourceAligned_OnePerBeatPhase()
    {
        IReadOnlyList<MusicalGridCandidate> candidates = Build();

        // quarterAtSourceStart is 0 for every candidate; each downbeat must lie
        // in (quarterAtStart - QuartersPerBar, quarterAtStart] — at or before the
        // source start, never more than one bar earlier, never snapped elsewhere.
        foreach (MusicalGridCandidate candidate in candidates)
        {
            Assert.NotNull(candidate.FirstDownbeatQuarter);
            double downbeat = candidate.FirstDownbeatQuarter!.Value;
            double quarterAtStart = candidate.QuarterAtSourceStart;
            double quartersPerBar = candidate.Meter.QuartersPerBar;
            Assert.True(downbeat <= quarterAtStart + 1e-9);
            Assert.True(downbeat > quarterAtStart - quartersPerBar - 1e-9);
        }

        // 4/4 at 120 BPM: exactly the four beat phases, at whole-beat offsets.
        double[] downbeats44 = candidates
            .Where(candidate => Math.Abs(candidate.Bpm - 120) < 1e-9
                && candidate.Meter == new Meter(4, 4))
            .Select(candidate => candidate.FirstDownbeatQuarter!.Value)
            .OrderBy(value => value)
            .ToArray();
        Assert.Equal(new[] { -3.0, -2.0, -1.0, 0.0 }, downbeats44);

        // 3/4 at 120 BPM: exactly the three beat phases.
        double[] downbeats34 = candidates
            .Where(candidate => Math.Abs(candidate.Bpm - 120) < 1e-9
                && candidate.Meter == new Meter(3, 4))
            .Select(candidate => candidate.FirstDownbeatQuarter!.Value)
            .OrderBy(value => value)
            .ToArray();
        Assert.Equal(new[] { -2.0, -1.0, 0.0 }, downbeats34);
    }

    [Fact]
    public void ExplicitDownbeat_GeneratesExactlyThatPhase_NeverShifted()
    {
        // Downbeat at sample 4*Spq (= quarter 4 at 120 BPM): every (bpm, meter)
        // family must carry EXACTLY that quarter, never shifted or snapped.
        long explicitSample = (long)Math.Round(4 * Sr * 60.0 / 120.0);
        IReadOnlyList<MusicalGridCandidate> candidates = Build(explicitDownbeatSample: explicitSample);

        foreach (var group in candidates
            .GroupBy(candidate => (Bpm: Math.Round(candidate.Bpm, 6), candidate.Meter)))
        {
            Assert.Single(group);
            // The exact sample converts under THAT candidate's tempo: no shifting
            // or snapping — the downbeat quarter is precisely sample / spq(bpm).
            double expected = explicitSample * group.Key.Bpm / (Sr * 60.0);
            Assert.Equal(expected, group.Single().FirstDownbeatQuarter!.Value, precision: 9);
        }
    }
}
