using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Phase 1 unit tests for the unified percussion evidence builder (spec §3/§4/§6,
/// acceptance items 5/6/8). Every native RhythmEvent must appear with its
/// classified role, AggregateHitEvents are always evidence, percussive FM notes
/// enter as ClassifiedNote, dedup happens only per physical attack with priority
/// NativeRhythm &gt; AggregateHit &gt; ClassifiedNote, and simultaneous kick+snare at
/// one sample position survive as separate onsets.
/// </summary>
public sealed class PercussionEvidenceBuilderTests
{
    private const int SampleRate = 44_100;

    private static SourceDomainKey FmDomain(int index) =>
        new(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Fm, index);

    private static RhythmEvent Kick(long sample) =>
        new("ym2608.0.rhythm.bd", "ym2608.0.rhythm.bd", sample, 1.0f, 0f,
            InstrumentId: "rhythm:bd") { Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Rhythm, 0) };

    private static RhythmEvent Snare(long sample) =>
        new("ym2608.0.rhythm.sd", "ym2608.0.rhythm.sd", sample, 0.9f, 0f,
            InstrumentId: "rhythm:sd") { Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Rhythm, 1) };

    private static FmOperatorDefinition Op(int ar, int sl, int sr, int rr) =>
        new(ar, sr, sr, rr, sl, 30, 0, 1, 0, AmplitudeModulation: false, SsgEnvelope: 0);

    private static InstrumentDefinition SnappyInstrument(string id) =>
        new(id, "Fm", Algorithm: 7, Feedback: 0, Ams: null, Fms: null,
            new[] { Op(31, 0, 31, 31), Op(31, 0, 31, 31), Op(31, 0, 31, 31), Op(31, 0, 31, 31) });

    private static NoteEvent FmNote(string channelId, string instrumentId, long start, long end,
        bool isRetrigger, SourceDomainKey domain, double midi = 60) =>
        new(channelId, start, end, 261.6, midi, instrumentId, VisualizationNoteMode.Fm,
            isRetrigger, Array.Empty<PitchChange>()) { Domain = domain };

    [Fact]
    public void EveryNativeRhythmEvent_AppearsInEvidence()
    {
        var timeline = new VisualizationTimeline
        {
            SampleRate = SampleRate,
            Rhythm =
            [
                Kick(1000),
                Snare(1000 + 600),
                new RhythmEvent("ym2608.0.rhythm.hh", "ym2608.0.rhythm.hh", 1000 + 1200, 0.7f, 0f,
                    InstrumentId: "rhythm:hh")
                {
                    Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Rhythm, 2),
                },
            ],
        };

        IReadOnlyList<PercussiveOnset> evidence = PercussionEvidenceBuilder.Build(timeline);

        Assert.Equal(3, evidence.Count);
        Assert.All(evidence, onset => Assert.Equal(PercussionEvidenceKind.NativeRhythm, onset.EvidenceKind));
        Assert.Equal(new[] { 1000L, 1600L, 2200L }, evidence.Select(o => o.SamplePosition));
        Assert.Equal(RhythmRole.Bd, evidence[0].Role);
        Assert.Equal(RhythmRole.Sd, evidence[1].Role);
        Assert.Equal(RhythmRole.Hh, evidence[2].Role);
        Assert.Equal(1.0f, evidence[0].Strength);
        Assert.Equal(1.0, evidence[0].Confidence);
    }

    [Fact]
    public void SimultaneousKickAndSnare_AtSameSample_BothSurvive()
    {
        var timeline = new VisualizationTimeline
        {
            SampleRate = SampleRate,
            Rhythm = [Kick(1000), Snare(1000)],
        };

        IReadOnlyList<PercussiveOnset> evidence = PercussionEvidenceBuilder.Build(timeline);

        Assert.Equal(2, evidence.Count);
        Assert.All(evidence, onset => Assert.Equal(1000L, onset.SamplePosition));
        Assert.Contains(evidence, o => o.Role == RhythmRole.Bd);
        Assert.Contains(evidence, o => o.Role == RhythmRole.Sd);
    }

    [Fact]
    public void AggregateHit_IsAlwaysEvidence_WithUnknownRole()
    {
        var timeline = new VisualizationTimeline
        {
            SampleRate = SampleRate,
            AggregateHits =
            [
                new AggregateHitEvent("agg.1", "sub", "impact", 5000, 1.0f, 0f, "asset.1"),
            ],
        };

        IReadOnlyList<PercussiveOnset> evidence = PercussionEvidenceBuilder.Build(timeline);

        PercussiveOnset onset = Assert.Single(evidence);
        Assert.Equal(PercussionEvidenceKind.AggregateHit, onset.EvidenceKind);
        Assert.Equal(RhythmRole.Unknown, onset.Role);
        Assert.Equal(5000L, onset.SamplePosition);
        Assert.Equal(1.0, onset.Confidence);
    }

    [Fact]
    public void PercussiveFmNote_EntersAsClassifiedNote()
    {
        var timeline = new VisualizationTimeline
        {
            SampleRate = SampleRate,
            Instruments = [SnappyInstrument("kick")],
            Notes =
            [
                FmNote("ym2608.0.fm.3", "kick", 7000, 7000 + 1323, isRetrigger: true, FmDomain(3)),
            ],
        };

        IReadOnlyList<PercussiveOnset> evidence = PercussionEvidenceBuilder.Build(timeline);

        PercussiveOnset onset = Assert.Single(evidence);
        Assert.Equal(PercussionEvidenceKind.ClassifiedNote, onset.EvidenceKind);
        Assert.Equal(RhythmRole.Bd, onset.Role);
        Assert.Equal(7000L, onset.SamplePosition);
        Assert.True(onset.Confidence >= 0.80, $"expected high confidence, got {onset.Confidence}");
    }

    [Fact]
    public void UnknownRoleOnset_IsValidEvidence_NotCoerced()
    {
        var timeline = new VisualizationTimeline
        {
            SampleRate = SampleRate,
            Instruments = [SnappyInstrument("transient")],
            Notes =
            [
                FmNote("ym2608.0.fm.4", "transient", 9000, 9000 + 1323, isRetrigger: true, FmDomain(4)),
            ],
        };

        IReadOnlyList<PercussiveOnset> evidence = PercussionEvidenceBuilder.Build(timeline);

        PercussiveOnset onset = Assert.Single(evidence);
        Assert.Equal(PercussionEvidenceKind.ClassifiedNote, onset.EvidenceKind);
        Assert.Equal(RhythmRole.Unknown, onset.Role);
        Assert.True(onset.Confidence > 0.8 && onset.Confidence <= 0.92,
            $"expected unknown-role confidence cap, got {onset.Confidence}");
    }

    [Fact]
    public void NonPercussiveFmNote_DoesNotEnterEvidence()
    {
        InstrumentDefinition sustained = new("bass", "Fm", Algorithm: 0, Feedback: 0, Ams: null, Fms: null,
            new[] { Op(10, 12, 8, 12), Op(10, 12, 8, 12), Op(10, 12, 8, 12), Op(10, 12, 8, 12) });
        var timeline = new VisualizationTimeline
        {
            SampleRate = SampleRate,
            Instruments = [sustained],
            Notes =
            [
                FmNote("ym2608.0.fm.0", "bass", 0, 44_100, isRetrigger: false, FmDomain(0), midi: 24),
            ],
        };

        Assert.Empty(PercussionEvidenceBuilder.Build(timeline));
    }

    [Fact]
    public void NativeRhythm_WinsDedupPriority_OverClassifiedNote()
    {
        // Same physical attack: one native rhythm event and one percussive FM
        // note sharing Domain + voice + sample position.
        SourceDomainKey domain = FmDomain(0);
        var timeline = new VisualizationTimeline
        {
            SampleRate = SampleRate,
            Instruments = [SnappyInstrument("kick")],
            Rhythm =
            [
                new RhythmEvent("ym2608.0.fm.0", "ym2608.0.fm.0", 12000, 0.5f, 0f)
                {
                    Domain = domain,
                },
            ],
            Notes =
            [
                FmNote("ym2608.0.fm.0", "kick", 12000, 12000 + 1323, isRetrigger: true, domain),
            ],
        };

        IReadOnlyList<PercussiveOnset> evidence = PercussionEvidenceBuilder.Build(timeline);

        PercussiveOnset onset = Assert.Single(evidence);
        Assert.Equal(PercussionEvidenceKind.NativeRhythm, onset.EvidenceKind);
        Assert.Equal(0.5, onset.Strength);
        Assert.Equal(12000L, onset.SamplePosition);
    }

    [Fact]
    public void ResultIsDeterministicallyOrdered()
    {
        var timeline = new VisualizationTimeline
        {
            SampleRate = SampleRate,
            Rhythm = [Snare(500), Kick(1000), Snare(1000), Kick(500)],
        };

        IReadOnlyList<PercussiveOnset> evidence = PercussionEvidenceBuilder.Build(timeline);

        Assert.Equal(new[] { 500L, 500L, 1000L, 1000L }, evidence.Select(o => o.SamplePosition));
        Assert.Equal(new[] { RhythmRole.Bd, RhythmRole.Sd, RhythmRole.Bd, RhythmRole.Sd },
            evidence.Select(o => o.Role));
    }
}