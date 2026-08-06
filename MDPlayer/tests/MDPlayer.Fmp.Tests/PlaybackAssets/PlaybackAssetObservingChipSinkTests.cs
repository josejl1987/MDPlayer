using Fmp.Core.Audio;
using Fmp.Core.PlaybackAssets;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests.PlaybackAssets;

/// <summary>
/// Verifies the §16 pipeline on the FMP/MDSound render backend: the
/// <see cref="PlaybackAssetObservingChipSink"/> forwards every YM2608 write
/// unchanged to the synthesizer while the collector captures instruments at
/// key-on, and forwards untouched when no collector is attached.
/// </summary>
public class PlaybackAssetObservingChipSinkTests
{
    [Fact]
    public void ForwardsWritesUnchanged_AndCapturesAtKeyOn()
    {
        var inner = new RecordingFmpChipSink();
        var collector = new PlaybackAssetCollector();
        var sink = new PlaybackAssetObservingChipSink(inner) { Collector = collector };

        sink.WriteYm2608(0, 0, 0xB0, 0x35, 100);
        sink.WriteYm2608(0, 0, 0x30, 0x71, 200);
        sink.WriteYm2608(0, 0, 0x40, 0x0B, 300);
        sink.WriteYm2608(0, 0, 0x28, 0xF0, 400); // key-on ch0

        // Every write forwarded unchanged to the inner sink, in order.
        Assert.Collection(
            inner.Ym2608Writes,
            w => Assert.Equal((0, 0, 0xB0, 0x35, 100L), w),
            w => Assert.Equal((0, 0, 0x30, 0x71, 200L), w),
            w => Assert.Equal((0, 0, 0x40, 0x0B, 300L), w),
            w => Assert.Equal((0, 0, 0x28, 0xF0, 400L), w));

        // Collector captured exactly one instrument (the key-on).
        var snapshot = collector.Complete();
        Assert.Single(snapshot.Instruments);
        Assert.Equal(5, snapshot.Instruments[0].TfiBytes[0]); // algorithm
        Assert.Equal(6, snapshot.Instruments[0].TfiBytes[1]); // feedback

        // Playback sample metadata surfaced on the observed write.
        Assert.Equal(400L, snapshot.Instruments[0].FirstSeenPlaybackSample);
    }

    [Fact]
    public void ForwardsUntouched_WhenNoCollectorAttached()
    {
        var inner = new RecordingFmpChipSink();
        var sink = new PlaybackAssetObservingChipSink(inner); // Collector null

        sink.WriteYm2608(0, 0, 0x28, 0xF0, 100);

        Assert.Single(inner.Ym2608Writes);
    }

    [Fact]
    public void CollectorFailure_DoesNotDisruptSynthesisWrites()
    {
        var inner = new RecordingFmpChipSink();
        var sink = new PlaybackAssetObservingChipSink(inner)
        {
            Collector = new ThrowingCollector(),
        };

        sink.WriteYm2608(0, 0, 0x28, 0xF0, 100);

        // The write still reached the synthesizer despite the collector failing.
        Assert.Single(inner.Ym2608Writes);
    }

    private sealed class RecordingFmpChipSink : IFmpChipSink
    {
        public List<(int Chip, int Port, int Address, int Byte, long Sample)> Ym2608Writes { get; } = new();

        public void WriteYm2608(int chipId, int port, int address, int value, long samplePosition)
            => Ym2608Writes.Add((chipId, port, address, value, samplePosition));
        public void LoadPpz8Bank(int bank, int mode, ReadOnlyMemory<byte>[] samples, long samplePosition) { }
        public void WritePpz8(int port, int address, int value, long samplePosition) { }
    }

    private sealed class ThrowingCollector : IPlaybackAssetCollector
    {
        public void Observe(in ChipWriteEvent write) => throw new InvalidOperationException("boom");
        public PlaybackAssetSnapshot Complete() => throw new InvalidOperationException("boom");
    }
}
