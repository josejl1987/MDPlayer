namespace Fmp.Core.Audio;

/// <summary>
/// Routes one FMP emulator register stream to multiple inner chip sinks in parallel.
/// Each inner sink is typically wrapped with <see cref="MaskedChipSink"/>
/// so that a single emulation pass can produce all per-channel stems.
/// </summary>
internal sealed class BroadcastChipSink : IFmpChipSink
{
    private readonly IReadOnlyList<(IFmpChipSink Sink, ChannelGroup Channels)> _sinks;

    public BroadcastChipSink(IReadOnlyList<(IFmpChipSink Sink, ChannelGroup Channels)> sinks)
    {
        _sinks = sinks ?? throw new ArgumentNullException(nameof(sinks));
    }

    public void WriteYm2608(int chipId, int port, int address, int value, long samplePosition)
    {
        foreach (var (sink, _) in _sinks)
            sink.WriteYm2608(chipId, port, address, value, samplePosition);
    }

    public void WritePpz8(int port, int address, int value, long samplePosition)
    {
        foreach (var (sink, _) in _sinks)
            sink.WritePpz8(port, address, value, samplePosition);
    }

    public void LoadPpz8Bank(int bank, int mode, ReadOnlyMemory<byte>[] samples, long samplePosition)
    {
        foreach (var (sink, _) in _sinks)
            sink.LoadPpz8Bank(bank, mode, samples, samplePosition);
    }
}
