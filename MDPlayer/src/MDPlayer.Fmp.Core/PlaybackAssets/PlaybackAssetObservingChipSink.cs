using Fmp.Core.Audio;
using Fmp.Core.Visualization;

namespace Fmp.Core.PlaybackAssets;

/// <summary>
/// Decorator that can place one <see cref="IPlaybackAssetCollector"/> on the
/// ordered YM2608 register write stream of an <see cref="IFmpChipSink"/>
/// (the FMP/MDSound render path) immediately before each write reaches the
/// underlying synthesizer. Instruments are captured as part of the same
/// ordered stream; every write is forwarded unchanged.
///
/// The collector is attached per render (see <see cref="Collector"/>), so a
/// single instance stays permanently in the FMP write pipeline and simply
/// forwards untouched while no collector is attached — zero behavioral change
/// for the common, non-dumping case.
/// </summary>
internal sealed class PlaybackAssetObservingChipSink : IFmpChipSink
{
    private readonly IFmpChipSink _inner;

    public PlaybackAssetObservingChipSink(IFmpChipSink inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>The collector to feed, or null to forward writes untouched.</summary>
    public IPlaybackAssetCollector Collector { get; set; }

    public void WriteYm2608(int chipId, int port, int address, int value, long samplePosition)
    {
        ObserveSafely(chipId, port, address, value, samplePosition);
        _inner.WriteYm2608(chipId, port, address, value, samplePosition);
    }

    public void LoadPpz8Bank(int bank, int mode, ReadOnlyMemory<byte>[] samples, long samplePosition)
        => _inner.LoadPpz8Bank(bank, mode, samples, samplePosition);

    public void WritePpz8(int port, int address, int value, long samplePosition)
        => _inner.WritePpz8(port, address, value, samplePosition);

    private void ObserveSafely(int chipId, int port, int address, int value, long samplePosition)
    {
        var collector = Collector;
        if (collector == null)
            return;

        // An export/collection bug must never corrupt playback: failures are
        // swallowed here rather than propagated into the synthesis write path.
        try
        {
            collector.Observe(new ChipWriteEvent
            {
                ChipType = ChipType.Ym2608,
                ChipIndex = chipId,
                Port = port,
                Address = address,
                Data = (byte)value,
                PlaybackSample = samplePosition,
                WriteIndex = null,
            });
        }
        catch (Exception)
        {
            // Diagnostics wiring is out of scope; do not disturb playback.
        }
    }
}
