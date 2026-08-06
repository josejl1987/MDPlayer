using Fmp.Core.PlaybackAssets;
using Fmp.Core.Visualization;
using MDPlayer.Fmp.Tests.Clocked;
using Xunit;

namespace MDPlayer.Fmp.Tests.PlaybackAssets;

/// <summary>
/// Verifies the §16 pipeline: the single authoritative observation point sits
/// between the ordered chip-write stream and the emulator. The observing
/// device must forward every write unchanged to the underlying emulator while
/// the collector captures instruments at key-on.
/// </summary>
public class PlaybackAssetObservingDeviceTests
{
    [Fact]
    public void ForwardsWrites_AndCapturesAtKeyOn()
    {
        var inner = new RecordingOpnaDevice();
        var collector = new PlaybackAssetCollector();
        using var device = new PlaybackAssetObservingDevice(
            inner, collector, ChipType.Ym2608, chipIndex: 0);

        // Emulate the complete write order a driver would produce.
        device.WriteRegister(0, bank: 0, address: 0xB0, value: 0x35); // alg 5, fb 6
        device.WriteRegister(0, bank: 0, address: 0x30, value: 0x71); // Op1 DT7 MT1
        device.WriteRegister(0, bank: 0, address: 0x40, value: 0x0B); // Op1 TL
        device.WriteRegister(0, bank: 0, address: 0x28, value: 0xF0); // key-on ch0

        // Every write forwarded unchanged, in order.
        Assert.Equal(4, inner.Writes.Count);
        Assert.Equal((0ul, (byte)0, (byte)0xB0, (byte)0x35), inner.Writes[0]);
        Assert.Equal((0ul, (byte)0, (byte)0x28, (byte)0xF0), inner.Writes[3]);

        // Collector captured exactly one instrument (the key-on).
        var snapshot = collector.Complete();
        Assert.Single(snapshot.Instruments);
        Assert.Equal(5, snapshot.Instruments[0].TfiBytes[0]); // algorithm
        Assert.Equal(6, snapshot.Instruments[0].TfiBytes[1]); // feedback
    }

    [Fact]
    public void CollectorFailure_DoesNotDisruptEmulatorWrites()
    {
        var inner = new RecordingOpnaDevice();

        // A throwing collector exercises the "an export bug must never corrupt
        // playback" guarantee.
        var failing = new ThrowingCollector();
        using var device = new PlaybackAssetObservingDevice(
            inner, failing, ChipType.Ym2608, chipIndex: 0);

        device.WriteRegister(0, bank: 0, address: 0x28, value: 0xF0);

        // The write still reached the emulator despite the collector failing.
        Assert.Single(inner.Writes);
        Assert.Equal((0ul, (byte)0, (byte)0x28, (byte)0xF0), inner.Writes[0]);
    }

    private sealed class ThrowingCollector : IPlaybackAssetCollector
    {
        public void Observe(in ChipWriteEvent write) => throw new InvalidOperationException("boom");
        public PlaybackAssetSnapshot Complete() => throw new InvalidOperationException("boom");
    }
}
