
namespace Fmp.Core.Audio;

/// <summary>
/// No-op <see cref="IFmpChipSink"/> implementation used for trace-only runs
/// where no audio synthesis is required.
/// </summary>
internal class NullFmpChipSink : IFmpChipSink
{
    public void WriteYm2608(int chipId, int port, int address, int value, long samplePosition)
    {
    }

    public void LoadPpz8Bank(int bank, int mode, ReadOnlyMemory<byte>[] samples, long samplePosition)
    {
    }

    public void WritePpz8(int port, int address, int value, long samplePosition)
    {
    }
}
