using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Phase-2 lifecycle unit tests for the DAC playback tracker and extractor.
/// Synthetic operation sequences are valid for isolating the playback state
/// machine (DAC specification §30 / §31). Asset deduplication and identity
/// assignment happen in a later phase and are out of scope here.
/// </summary>
public sealed class DacPlaybackTrackerTests
{
    private const int Src = 7;
    private static readonly DacSampleFormat Format = DacSampleFormat.DefaultYm2612;

    [Fact]
    public void SamePayloadRetriggered_ProducesTwoEventsWithSharedCandidate()
    {
        byte[] a = [0x10, 0x20, 0x30];
        var tracker = new DacPlaybackTracker();

        tracker.Add(new DacOperation.DacPlaybackStarted(10, Src, 0, null, null));
        foreach ((byte value, int i) in a.Select((value, i) => (value, i)))
            tracker.Add(new DacOperation.DacByteConsumed(10 + i, Src, i, value));
        tracker.Add(new DacOperation.DacPlaybackStopped(20, DacStopReason.ExplicitStop));

        tracker.Add(new DacOperation.DacPlaybackStarted(40, Src, 0, null, null));
        foreach ((byte value, int i) in a.Select((value, i) => (value, i)))
            tracker.Add(new DacOperation.DacByteConsumed(40 + i, Src, i, value));
        tracker.Add(new DacOperation.DacPlaybackStopped(50, DacStopReason.ExplicitStop));

        tracker.Complete(60);

        Assert.Equal(2, tracker.PlaybackEvents.Count);
        Assert.Equal(2, tracker.Candidates.Count);
        Assert.NotEqual(tracker.PlaybackEvents[0].InstanceId, tracker.PlaybackEvents[1].InstanceId);
        Assert.True(tracker.PlaybackEvents[0].SampleId == tracker.PlaybackEvents[1].SampleId); // both null in this phase
        Assert.Equal(a, tracker.Candidates[0].Payload.ToArray());
        Assert.Equal(a, tracker.Candidates[1].Payload.ToArray());
        Assert.Equal(Format.StableKey, tracker.Candidates[0].Format.StableKey);
    }

    [Fact]
    public void SamePayloadDifferentRates_ProducesDistinctInstancesWithDifferentRates()
    {
        byte[] a = [0x10, 0x20, 0x30];
        var tracker = new DacPlaybackTracker();

        Play(tracker, 10, a, rate: 8_000);
        Play(tracker, 40, a, rate: 16_000);
        tracker.Complete(60);

        Assert.Equal(2, tracker.PlaybackEvents.Count);
        Assert.Equal(8000, tracker.PlaybackEvents[0].InitialRateHz);
        Assert.Equal(16000, tracker.PlaybackEvents[1].InitialRateHz);
        Assert.Equal(a, tracker.Candidates[0].Payload.ToArray());
        Assert.Equal(a, tracker.Candidates[1].Payload.ToArray());
    }

    [Fact]
    public void DifferentPayloads_ProduceDistinctCandidates()
    {
        var tracker = new DacPlaybackTracker();
        Play(tracker, 10, [0x10, 0x20]);
        Play(tracker, 40, [0xAA, 0xBB]);
        tracker.Complete(60);

        Assert.Equal(2, tracker.Candidates.Count);
        Assert.NotEqual(
            tracker.Candidates[0].Payload.ToArray(),
            tracker.Candidates[1].Payload.ToArray());
    }

    [Fact]
    public void SourceOffsetDoesNotAffectExtractedPayload()
    {
        // Identical bytes played from two different source offsets must produce
        // identical candidate payloads (spec 30.4).
        byte[] a = [1, 2, 3, 4];
        var tracker = new DacPlaybackTracker();
        tracker.DefineSource(Src, a, Format);

        tracker.Add(new DacOperation.DacPlaybackStarted(10, Src, Position: 0, null, null));
        ConsumeRange(tracker, 10, startOffset: 0, a);
        tracker.Add(new DacOperation.DacPlaybackStopped(20, DacStopReason.ExplicitStop));

        tracker.Add(new DacOperation.DacPlaybackStarted(40, Src, Position: 2, null, null));
        ConsumeRange(tracker, 40, startOffset: 2, a);
        tracker.Add(new DacOperation.DacPlaybackStopped(50, DacStopReason.ExplicitStop));

        tracker.Complete(60);

        Assert.Equal(2, tracker.Candidates.Count);
        Assert.Equal(
            tracker.Candidates[0].Payload.ToArray(),
            tracker.Candidates[1].Payload.ToArray());
    }

    [Fact]
    public void RetriggerInterruption_ClosesOldEventAtNewStart()
    {
        var tracker = new DacPlaybackTracker();
        // Start A, consume two of three bytes, then restart A.
        tracker.Add(new DacOperation.DacPlaybackStarted(10, Src, 0, null, null));
        tracker.Add(new DacOperation.DacByteConsumed(10, Src, 0, 0x10));
        tracker.Add(new DacOperation.DacByteConsumed(11, Src, 1, 0x20));
        tracker.Add(new DacOperation.DacPlaybackStarted(12, Src, 0, null, null)); // retrigger
        tracker.Add(new DacOperation.DacByteConsumed(12, Src, 0, 0x10));
        tracker.Add(new DacOperation.DacByteConsumed(13, Src, 1, 0x20));
        tracker.Add(new DacOperation.DacByteConsumed(14, Src, 2, 0x30));
        tracker.Add(new DacOperation.DacPlaybackStopped(20, DacStopReason.ExplicitStop));
        tracker.Complete(60);

        DacPlaybackEvent first = tracker.PlaybackEvents[0];
        DacPlaybackEvent second = tracker.PlaybackEvents[1];
        Assert.Equal(DacStopReason.Retriggered, first.StopReason);
        Assert.Equal(12, first.EndSample);
        Assert.Equal(12, second.StartSample);
        Assert.Equal(2, first.BytesConsumed);
        Assert.Equal(3, second.BytesConsumed);
    }

    [Fact]
    public void NaturalCompletion_ClosesAtDeclaredLength()
    {
        byte[] a = [1, 2, 3];
        var tracker = new DacPlaybackTracker();
        tracker.DefineSource(Src, a, Format);

        tracker.Add(new DacOperation.DacPlaybackStarted(10, Src, 0, DeclaredLength: a.Length, null));
        ConsumeRange(tracker, 10, startOffset: 0, a);
        tracker.Complete(60);

        DacPlaybackEvent evt = Assert.Single(tracker.PlaybackEvents);
        Assert.Equal(DacStopReason.NaturalEnd, evt.StopReason);
        Assert.Equal(a.Length, evt.BytesConsumed);
        Assert.False(evt.WasTruncated);
    }

    [Fact]
    public void ExplicitStop_ClosesAtStopTimestamp()
    {
        var tracker = new DacPlaybackTracker();
        Play(tracker, 10, [1, 2, 3]);
        tracker.Complete(60);

        Assert.Single(tracker.PlaybackEvents, evt =>
            evt.StopReason == DacStopReason.ExplicitStop && evt.EndSample == 20);
    }

    [Fact]
    public void EndOfStream_ClosesActivePlaybackWithEndOfStreamReason()
    {
        var tracker = new DacPlaybackTracker();
        tracker.Add(new DacOperation.DacPlaybackStarted(10, Src, 0, null, null));
        tracker.Add(new DacOperation.DacByteConsumed(10, Src, 0, 0x10));
        tracker.Add(new DacOperation.DacByteConsumed(11, Src, 1, 0x20));
        tracker.Complete(60);

        DacPlaybackEvent evt = Assert.Single(tracker.PlaybackEvents);
        Assert.Equal(DacStopReason.EndOfStream, evt.StopReason);
        Assert.Equal(60, evt.EndSample);
    }

    [Fact]
    public void MidEventRateChange_KeepsOneEventAndRecordsRatePoint()
    {
        var tracker = new DacPlaybackTracker();
        tracker.Add(new DacOperation.DacPlaybackStarted(10, Src, 0, null, 8_000));
        tracker.Add(new DacOperation.DacByteConsumed(10, Src, 0, 0x10));
        tracker.Add(new DacOperation.DacByteConsumed(11, Src, 1, 0x20));
        tracker.Add(new DacOperation.DacRateChanged(100, 11_025));
        tracker.Add(new DacOperation.DacByteConsumed(101, Src, 2, 0x30));
        tracker.Add(new DacOperation.DacPlaybackStopped(120, DacStopReason.ExplicitStop));
        tracker.Complete(130);

        DacPlaybackEvent evt = Assert.Single(tracker.PlaybackEvents);
        Assert.Equal(8000, evt.InitialRateHz);
        DacRatePoint point = Assert.Single(evt.RatePoints);
        Assert.Equal(100, point.Timestamp);
        Assert.Equal(11_025, point.RateHz);
    }

    [Fact]
    public void EmptyPlayback_ProducesNoEventsOrCandidates()
    {
        var tracker = new DacPlaybackTracker();
        tracker.Add(new DacOperation.DacPlaybackStarted(10, Src, 0, null, null));
        tracker.Add(new DacOperation.DacPlaybackStopped(20, DacStopReason.ExplicitStop));
        tracker.Complete(60);

        Assert.Empty(tracker.PlaybackEvents);
        Assert.Empty(tracker.Candidates);
    }

    [Fact]
    public void TruncatedDeclaredSource_PreservesPartialPayloadAndWarns()
    {
        byte[] a = [1, 2, 3];
        var tracker = new DacPlaybackTracker();
        tracker.DefineSource(Src, a, Format);

        tracker.Add(new DacOperation.DacPlaybackStarted(10, Src, 0, DeclaredLength: 10, null));
        ConsumeRange(tracker, 10, startOffset: 0, a);
        tracker.Complete(60);

        DacPlaybackEvent evt = Assert.Single(tracker.PlaybackEvents);
        Assert.True(evt.WasTruncated);
        Assert.Equal(DacStopReason.EndOfStream, evt.StopReason);
        Assert.Equal(a, tracker.Candidates[0].Payload.ToArray());
        Assert.Contains(tracker.Diagnostics, d => d.Code == "truncated-input");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void InvalidRate_EmitsDiagnosticAndKeepsIdentity(double rate)
    {
        var tracker = new DacPlaybackTracker();
        tracker.Add(new DacOperation.DacPlaybackStarted(10, Src, 0, null, rate));
        tracker.Add(new DacOperation.DacByteConsumed(10, Src, 0, 0x10));
        tracker.Add(new DacOperation.DacPlaybackStopped(20, DacStopReason.ExplicitStop));
        tracker.Complete(60);

        DacPlaybackEvent evt = Assert.Single(tracker.PlaybackEvents);
        Assert.Null(evt.InitialRateHz);
        Assert.Contains(tracker.Diagnostics, d => d.Code == "invalid-rate");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonFiniteRate_EmitsDiagnosticAndIgnoresValue(double rate)
    {
        var tracker = new DacPlaybackTracker();
        tracker.Add(new DacOperation.DacPlaybackStarted(10, Src, 0, null, rate));
        tracker.Add(new DacOperation.DacByteConsumed(10, Src, 0, 0x10));
        tracker.Add(new DacOperation.DacPlaybackStopped(20, DacStopReason.ExplicitStop));
        tracker.Complete(60);

        DacPlaybackEvent evt = Assert.Single(tracker.PlaybackEvents);
        Assert.Null(evt.InitialRateHz);
        Assert.Contains(tracker.Diagnostics, d => d.Code == "invalid-rate");
    }

    [Fact]
    public void SourceDiscontinuity_SplitsSessionIntoTwoEvents()
    {
        var tracker = new DacPlaybackTracker();
        tracker.Add(new DacOperation.DacPlaybackStarted(10, Src, 0, null, null));
        tracker.Add(new DacOperation.DacByteConsumed(10, Src, 0, 0x10));
        // Non-contiguous byte (gap in source position).
        tracker.Add(new DacOperation.DacByteConsumed(50, Src, 5, 0x20));
        tracker.Add(new DacOperation.DacPlaybackStopped(60, DacStopReason.ExplicitStop));
        tracker.Complete(70);

        Assert.Equal(2, tracker.PlaybackEvents.Count);
        Assert.Equal(DacStopReason.SourceDiscontinuity, tracker.PlaybackEvents[0].StopReason);
        Assert.Equal(DacStopReason.ExplicitStop, tracker.PlaybackEvents[1].StopReason);
    }

    [Fact]
    public void NoDacActivity_ProducesNothing()
    {
        var tracker = new DacPlaybackTracker();
        tracker.Complete(100);
        Assert.Empty(tracker.PlaybackEvents);
        Assert.Empty(tracker.Candidates);
        Assert.Empty(tracker.Diagnostics);
    }

    [Fact]
    public void Normalizer_EmitsStartAndBytesOnDacWrite_ThenStopOnDisable()
    {
        var normalizer = new Ym2612DacNormalizer();
        var ops = new List<DacOperation>();

        normalizer.Process(0, 0, 0x2B, 0x80, ops);            // enable
        normalizer.Process(0, 0, 0x2A, 0x10, ops);            // byte 0
        normalizer.Process(8, 0, 0x2A, 0x20, ops);            // byte 1
        normalizer.Process(16, 0, 0x2B, 0x00, ops);           // disable
        normalizer.Complete(24, ops);

        Assert.Equal(4, ops.Count);

        var start = Assert.IsType<DacOperation.DacPlaybackStarted>(ops[0]);
        Assert.Equal(0, start.Timestamp);
        Assert.Equal(0, start.Position);

        var b0 = Assert.IsType<DacOperation.DacByteConsumed>(ops[1]);
        Assert.Equal(0, b0.Position);
        Assert.Equal(0x10, b0.Value);

        var b1 = Assert.IsType<DacOperation.DacByteConsumed>(ops[2]);
        Assert.Equal(1, b1.Position);
        Assert.Equal(0x20, b1.Value);

        var stop = Assert.IsType<DacOperation.DacPlaybackStopped>(ops[3]);
        Assert.Equal(DacStopReason.ExplicitStop, stop.Reason);
    }

    [Fact]
    public void Normalizer_EmitsStartAfterIdleRetrigger()
    {
        var normalizer = new Ym2612DacNormalizer();
        var ops = new List<DacOperation>();

        // enable -> bytes -> disable -> enable -> bytes (a fresh trigger)
        normalizer.Process(0, 0, 0x2B, 0x80, ops);
        normalizer.Process(0, 0, 0x2A, 0x10, ops);
        normalizer.Process(8, 0, 0x2A, 0x20, ops);
        normalizer.Process(16, 0, 0x2B, 0x00, ops);
        normalizer.Process(100, 0, 0x2B, 0x80, ops);
        normalizer.Process(100, 0, 0x2A, 0x10, ops);

        Assert.Equal(2, ops.Count(op => op is DacOperation.DacPlaybackStarted));
        Assert.Equal(3, ops.Count(op => op is DacOperation.DacByteConsumed));
        Assert.Equal(1, ops.Count(op => op is DacOperation.DacPlaybackStopped));
    }

    [Fact]
    public void NormalizerToTracker_SplitsTwoBurstsIntoTwoEventsWithSamePayload()
    {
        var normalizer = new Ym2612DacNormalizer();
        var ops = new List<DacOperation>();

        void Feed(long ts, int port, int addr, int value) => normalizer.Process(ts, port, addr, value, ops);

        Feed(0, 0, 0x2B, 0x80);
        Feed(0, 0, 0x2A, 0x12);
        Feed(8, 0, 0x2A, 0x34);
        Feed(16, 0, 0x2A, 0x56);
        Feed(24, 0, 0x2B, 0x00);
        Feed(100, 0, 0x2B, 0x80);
        Feed(100, 0, 0x2A, 0x12);
        Feed(108, 0, 0x2A, 0x34);
        Feed(116, 0, 0x2A, 0x56);
        Feed(124, 0, 0x2B, 0x00);
        normalizer.Complete(130, ops);

        var tracker = new DacPlaybackTracker();
        foreach (DacOperation op in ops)
            tracker.Add(op);
        tracker.Complete(130);

        Assert.Equal(2, tracker.PlaybackEvents.Count);
        Assert.All(tracker.PlaybackEvents, evt => Assert.Equal(DacStopReason.ExplicitStop, evt.StopReason));
        Assert.Equal(
            new byte[] { 0x12, 0x34, 0x56 },
            tracker.Candidates[0].Payload.ToArray());
        Assert.Equal(
            tracker.Candidates[0].Payload.ToArray(),
            tracker.Candidates[1].Payload.ToArray());
    }

    private static void Play(DacPlaybackTracker tracker, long start, byte[] payload, double? rate = null)
    {
        tracker.Add(new DacOperation.DacPlaybackStarted(start, Src, 0, null, rate));
        for (int i = 0; i < payload.Length; i++)
            tracker.Add(new DacOperation.DacByteConsumed(start + i, Src, i, payload[i]));
        tracker.Add(new DacOperation.DacPlaybackStopped(start + 10, DacStopReason.ExplicitStop));
    }

    private static void ConsumeRange(
        DacPlaybackTracker tracker,
        long start,
        int startOffset,
        byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            int position = startOffset + i;
            tracker.Add(new DacOperation.DacByteConsumed(start + i, Src, position, data[i]));
        }
    }
}
