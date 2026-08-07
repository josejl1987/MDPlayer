using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Phase-3 catalog, deduplication, and note-mapping tests operating on the
/// tracker output (spec §30). These assert identity/catalog semantics that the
/// timeline emitter and MIDI exporter depend on in later phases.
/// </summary>
public sealed class DacSampleCatalogTests
{
    private const int Src = 7;
    private static readonly DacSampleFormat Unsigned = DacSampleFormat.DefaultYm2612;
    private static readonly DacSampleFormat Signed = new(
        DacEncoding.Pcm, BitsPerSample: 8, DacSignedness.Signed, ChannelCount: 1);

    [Fact]
    public void SamePayloadRetriggered_BuildsOneAssetTwoEvents()
    {
        byte[] a = [0x10, 0x20, 0x30];
        DacPipeline p = Run(ops =>
        {
            Play(ops, 10, a);
            Play(ops, 40, a);
        });

        DacSampleAsset asset = Assert.Single(p.Result.Assets);
        Assert.Equal(2, asset.TriggerCount);
        Assert.Equal(2, p.Tracker.PlaybackEvents.Count);

        DacPlaybackEvent e0 = p.Tracker.PlaybackEvents[0];
        DacPlaybackEvent e1 = p.Tracker.PlaybackEvents[1];
        Assert.NotEqual(e0.InstanceId, e1.InstanceId);
        Assert.Equal(asset.AssetId, e0.AssetId);
        Assert.Equal(asset.AssetId, e1.AssetId);
        Assert.Equal(e0.SampleId, e1.SampleId);
    }

    [Fact]
    public void SamePayloadAtDifferentRates_OneAssetTwoEventsSameNote()
    {
        byte[] a = [0x10, 0x20, 0x30];
        DacPipeline p = Run(ops =>
        {
            Play(ops, 10, a, rate: 8_000);
            Play(ops, 40, a, rate: 16_000);
        });

        Assert.Single(p.Result.Assets);
        Assert.Equal(2, p.Tracker.PlaybackEvents.Count);
        DacPlaybackEvent e0 = p.Tracker.PlaybackEvents[0];
        DacPlaybackEvent e1 = p.Tracker.PlaybackEvents[1];
        Assert.Equal(e0.AssetId, e1.AssetId);
        Assert.NotEqual(e0.InitialRateHz, e1.InitialRateHz);
        Assert.Equal(e0.SampleId, e1.SampleId);
        DacSampleAsset asset = p.Result.Assets[0];
        Assert.Equal(asset.DisplayNote, asset.DisplayNote);
    }

    [Fact]
    public void DifferentPayloads_TwoAssetsDistinctNotes()
    {
        DacPipeline p = Run(ops =>
        {
            Play(ops, 10, [0x10, 0x20]);
            Play(ops, 40, [0xAA, 0xBB]);
        });

        Assert.Equal(2, p.Result.Assets.Count);
        Assert.NotEqual(p.Result.Assets[0].AssetId, p.Result.Assets[1].AssetId);
        Assert.Equal(p.Result.Assets[0].DisplayBank, p.Result.Assets[1].DisplayBank);
        Assert.NotEqual(p.Result.Assets[0].DisplayNote, p.Result.Assets[1].DisplayNote);
    }

    [Fact]
    public void SourceOffsetIndependence_OneAssetTwoSources()
    {
        byte[] a = [1, 2, 3, 4];
        var tracker = new DacPlaybackTracker();
        tracker.DefineSource(Src, a, Unsigned);

        var ops = new List<DacOperation>();
        ops.Add(new DacOperation.DacPlaybackStarted(10, Src, Position: 0, null, null));
        ConsumeBytes(ops, 10, startOffset: 0, a);
        ops.Add(new DacOperation.DacPlaybackStopped(20, DacStopReason.ExplicitStop));
        ops.Add(new DacOperation.DacPlaybackStarted(40, Src, Position: 2, null, null));
        ConsumeBytes(ops, 40, startOffset: 2, a);
        ops.Add(new DacOperation.DacPlaybackStopped(50, DacStopReason.ExplicitStop));
        tracker.Complete(60);

        Feed(tracker, ops);
        DacCatalogResult result = new DacSampleCatalog().Build(tracker.Candidates);
        tracker.ApplyAssetIds(result);

        DacSampleAsset asset = Assert.Single(result.Assets);
        Assert.Equal(2, asset.Sources.Count);
        Assert.Equal(2, tracker.PlaybackEvents.Count);
        Assert.All(tracker.PlaybackEvents, e => Assert.Equal(asset.AssetId, e.AssetId));
    }

    [Fact]
    public void FormatSensitiveIdentity_SameBytesDifferentFormat_TwoAssets()
    {
        var tracker = new DacPlaybackTracker();
        tracker.DefineSource(0, new byte[] { 1, 2, 3 }, Unsigned);
        tracker.DefineSource(1, new byte[] { 1, 2, 3 }, Signed);

        var ops = new List<DacOperation>();
        ops.Add(new DacOperation.DacPlaybackStarted(10, 0, Position: 0, null, null));
        ConsumeBytes(ops, 10, startOffset: 0, new byte[] { 1, 2, 3 }, sourceId: 0);
        ops.Add(new DacOperation.DacPlaybackStopped(20, DacStopReason.ExplicitStop));
        ops.Add(new DacOperation.DacPlaybackStarted(40, 1, Position: 0, null, null));
        ConsumeBytes(ops, 40, startOffset: 0, new byte[] { 1, 2, 3 }, sourceId: 1);
        ops.Add(new DacOperation.DacPlaybackStopped(50, DacStopReason.ExplicitStop));
        tracker.Complete(60);
        Feed(tracker, ops);

        DacCatalogResult result = new DacSampleCatalog().Build(tracker.Candidates);
        Assert.Equal(2, result.Assets.Count);
        Assert.NotEqual(result.Assets[0].Format.StableKey, result.Assets[1].Format.StableKey);
    }

    [Fact]
    public void MoreThan128Samples_MapToBanksWithoutCollision()
    {
        var ops = new List<DacOperation>();
        for (int i = 0; i < 130; i++)
        {
            byte[] payload = [unchecked((byte)(i & 0xFF)), 0x42];
            Play(ops, 10 + i * 20L, payload);
        }

        DacPipeline p = Run(ops);
        Assert.Equal(130, p.Result.Assets.Count);

        var mapper = new DacNoteMapper();
        Assert.Equal(0, p.Result.Assets[0].DisplayBank);
        Assert.Equal(0, p.Result.Assets[0].DisplayNote);
        Assert.Equal(0, p.Result.Assets[127].DisplayBank);
        Assert.Equal(127, p.Result.Assets[127].DisplayNote);
        Assert.Equal(1, p.Result.Assets[128].DisplayBank);
        Assert.Equal(0, p.Result.Assets[128].DisplayNote);
        Assert.Equal(1, p.Result.Assets[129].DisplayBank);
        Assert.Equal(1, p.Result.Assets[129].DisplayNote);

        // No two assets share the same (bank, note) destination.
        Assert.Equal(130, p.Result.Assets
            .Select(a => (a.DisplayBank, a.DisplayNote))
            .Distinct()
            .Count());
    }

    [Fact]
    public void Determinism_RepeatedAnalysisProducesIdenticalAssets()
    {
        var mk = new List<DacOperation>();
        for (int i = 0; i < 20; i++)
        {
            byte[] payload = [(byte)(i & 0xFF), 0x77, 0x13];
            Play(mk, i * 20L, payload);
        }

        DacCatalogResult r1 = new DacSampleCatalog().Build(FeedThrough(mk).Candidates);
        DacCatalogResult r2 = new DacSampleCatalog().Build(FeedThrough(mk).Candidates);

        Assert.Equal(r1.Assets.Count, r2.Assets.Count);
        for (int i = 0; i < r1.Assets.Count; i++)
        {
            Assert.Equal(r1.Assets[i].AssetId, r2.Assets[i].AssetId);
            Assert.Equal(r1.Assets[i].StableName, r2.Assets[i].StableName);
            Assert.Equal(r1.Assets[i].ContentHash.Hex, r2.Assets[i].ContentHash.Hex);
            Assert.Equal(r1.Assets[i].DisplayBank, r2.Assets[i].DisplayBank);
            Assert.Equal(r1.Assets[i].DisplayNote, r2.Assets[i].DisplayNote);
        }
    }

    [Fact]
    public void EmptyPlayback_NoAssets()
    {
        DacPipeline p = Run(ops =>
        {
            ops.Add(new DacOperation.DacPlaybackStarted(10, Src, 0, null, null));
            ops.Add(new DacOperation.DacPlaybackStopped(20, DacStopReason.ExplicitStop));
        });

        Assert.Empty(p.Result.Assets);
        Assert.Empty(p.Tracker.PlaybackEvents);
    }

    [Fact]
    public void NoteMapper_ValidatesRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DacNoteMapper(noteBase: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DacNoteMapper(notesPerBank: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DacNoteMapper(noteBase: 100, notesPerBank: 40));

        var mapper = new DacNoteMapper(noteBase: 0, notesPerBank: 128);
        Assert.Equal((0, 0), (mapper.Map(0).Bank, mapper.Map(0).Note));
    }

    [Fact]
    public void InvalidRate_KeepsIdentityValid()
    {
        DacPipeline p = Run(ops =>
        {
            ops.Add(new DacOperation.DacPlaybackStarted(10, Src, 0, null, 0.0));
            ops.Add(new DacOperation.DacByteConsumed(10, Src, 0, 0x10));
            ops.Add(new DacOperation.DacPlaybackStopped(20, DacStopReason.ExplicitStop));
        });

        DacSampleAsset asset = Assert.Single(p.Result.Assets);
        DacPlaybackEvent evt = Assert.Single(p.Tracker.PlaybackEvents);
        Assert.Equal(asset.AssetId, evt.AssetId);
        Assert.Contains(p.Tracker.Diagnostics, d => d.Code == "invalid-rate");
    }

    // ---- helpers ----

    private static DacPipeline Run(IEnumerable<DacOperation> ops)
    {
        var tracker = new DacPlaybackTracker();
        Feed(tracker, ops);
        tracker.Complete(10_000);
        DacCatalogResult result = new DacSampleCatalog().Build(tracker.Candidates);
        tracker.ApplyAssetIds(result);
        return new DacPipeline(tracker, result);
    }

    private static DacPipeline Run(Action<List<DacOperation>> build)
    {
        var ops = new List<DacOperation>();
        build(ops);
        return Run(ops);
    }

    private static DacPlaybackTracker FeedThrough(IEnumerable<DacOperation> ops)
    {
        var tracker = new DacPlaybackTracker();
        Feed(tracker, ops);
        return tracker;
    }

    private static void Feed(DacPlaybackTracker tracker, IEnumerable<DacOperation> ops)
    {
        foreach (DacOperation op in ops)
            tracker.Add(op);
    }

    private static void Play(List<DacOperation> ops, long start, byte[] payload, double? rate = null)
    {
        ops.Add(new DacOperation.DacPlaybackStarted(start, Src, 0, null, rate));
        for (int i = 0; i < payload.Length; i++)
            ops.Add(new DacOperation.DacByteConsumed(start + i, Src, i, payload[i]));
        ops.Add(new DacOperation.DacPlaybackStopped(start + 10, DacStopReason.ExplicitStop));
    }

    private static void ConsumeBytes(List<DacOperation> ops, long start, int startOffset, byte[] data, int sourceId = Src)
    {
        for (int i = 0; i < data.Length; i++)
            ops.Add(new DacOperation.DacByteConsumed(start + i, sourceId, startOffset + i, data[i]));
    }

    private sealed record DacPipeline(DacPlaybackTracker Tracker, DacCatalogResult Result);
}