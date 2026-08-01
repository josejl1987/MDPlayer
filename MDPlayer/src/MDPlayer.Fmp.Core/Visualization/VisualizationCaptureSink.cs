using Fmp.Core.Audio;

namespace Fmp.Core.Visualization;

/// <summary>
/// Narrow chip-write seam for visualization capture. A downstream sink may be
/// supplied later to capture events during an audio/stem render without changing
/// the decoder or the panel renderer.
/// </summary>
internal sealed class VisualizationCaptureSink : IFmpChipSink
{
    private readonly Ym2608TimelineDecoder _decoder;
    private readonly IFmpChipSink _downstream;

    public VisualizationCaptureSink(Ym2608TimelineDecoder decoder, IFmpChipSink downstream = null)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _downstream = downstream;
    }

    public void WriteYm2608(int chipId, int port, int address, int value, long samplePosition)
    {
        _downstream?.WriteYm2608(chipId, port, address, value, samplePosition);
        _decoder.ApplyYm2608(chipId, port, address, value, samplePosition);
    }

    public void LoadPpz8Bank(int bank, int mode, ReadOnlyMemory<byte>[] samples, long samplePosition)
    {
        _downstream?.LoadPpz8Bank(bank, mode, samples, samplePosition);
    }

    public void WritePpz8(int port, int address, int value, long samplePosition)
    {
        _downstream?.WritePpz8(port, address, value, samplePosition);
    }
}
