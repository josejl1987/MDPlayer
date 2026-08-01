using Fmp.Core.Nise98;

namespace Fmp.Core.Audio;

/// <summary>
/// Output seam for chip register writes from the FMP emulator.
/// The renderer implements this to drive YM2608/PPZ8 synthesis.
/// </summary>
internal interface IFmpChipSink
{
    void WriteYm2608(int chipId, int port, int address, int value, long samplePosition);
    void LoadPpz8Bank(int bank, int mode, ReadOnlyMemory<byte>[] samples, long samplePosition);
    void WritePpz8(int port, int address, int value, long samplePosition);
}
