using Fmp.Core.Audio;

namespace Fmp.Core.Visualization;

/// <summary>
/// Bridges the existing FMP-specific audio sink to the normalized playback
/// event seam. The source driver remains unchanged while future backends can
/// send the same events without depending on FMP types.
/// </summary>
internal sealed class FmpPlaybackEventSinkAdapter : IFmpChipSink
{
    private readonly IPlaybackEventSink _events;
    private readonly IFmpChipSink _downstream;
    private bool _devicesAnnounced;

    public FmpPlaybackEventSinkAdapter(IPlaybackEventSink events, IFmpChipSink downstream = null)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _downstream = downstream;
    }

    public void WriteYm2608(int chipId, int port, int address, int value, long samplePosition)
    {
        AnnounceDevices(chipId);
        var write = new TimedChipWrite(
            samplePosition,
            new DeviceId(ChipType.Ym2608, chipId),
            port,
            address,
            value);
        _events.OnChipWrite(write);
        _downstream?.WriteYm2608(chipId, port, address, value, samplePosition);
    }

    public void LoadPpz8Bank(int bank, int mode, ReadOnlyMemory<byte>[] samples, long samplePosition)
    {
        AnnounceDevices(0);
        long size = samples?.Sum(sample => (long)sample.Length) ?? 0;
        _events.OnSampleAsset(new TimedSampleAssetEvent(
            samplePosition,
            new DeviceId(ChipType.Ppz8, 0),
            $"ppz8:{bank}:{mode}",
            AssetKind.SampleBank,
            size));
        _downstream?.LoadPpz8Bank(bank, mode, samples, samplePosition);
    }

    public void WritePpz8(int port, int address, int value, long samplePosition)
    {
        AnnounceDevices(0);
        var write = new TimedChipWrite(
            samplePosition,
            new DeviceId(ChipType.Ppz8, 0),
            port,
            address,
            value);
        _events.OnChipWrite(write);
        _downstream?.WritePpz8(port, address, value, samplePosition);
    }

    private void AnnounceDevices(int ymInstance)
    {
        if (_devicesAnnounced)
            return;
        _devicesAnnounced = true;
        _events.OnDevice(VisualizationDeviceCatalog.Ym2608(ymInstance));
        _events.OnDevice(VisualizationDeviceCatalog.Ppz8(ymInstance));
    }
}
