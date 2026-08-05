namespace Fmp.Core.Rendering;

/// <summary>
/// Optional event tap on the existing FMP execution path. When installed,
/// capture events are recorded at the exact operation boundary where Nise98
/// performs them, timestamped with the authoritative absolute YM2608
/// master-clock position accumulated by <see cref="FmpRuntime"/> at the control
/// tick rate. The default is null (capture disabled): the ordinary MDSound
/// path then performs no event allocation whatsoever.
/// </summary>
internal interface IFmpExecutionCaptureSink
{
    /// <summary>
    /// Captures one YM2608 register write. <paramref name="port"/> is the
    /// logical YM2608 port (0 or 1), already normalized at the boundary.
    /// </summary>
    void CaptureOpnaWrite(ulong opnaMasterClock, byte port, byte address, byte data);

    /// <summary>Captures one PPZ8 command at its authoritative master-clock position.</summary>
    void CapturePpz8Command(ulong opnaMasterClock, in Ppz8Command command);

    /// <summary>
    /// Captures one PPZ8 bank's immutable content. Returns the capture-local
    /// bank ID (deduplicated by content hash) referenced by later commands.
    /// <paramref name="logicalName"/> is the bank's resolved file name;
    /// <paramref name="dataChannels"/> are the per-channel immutable bytes.
    /// </summary>
    int CapturePpz8Bank(string logicalName, int bankIndex, int mode, ReadOnlySpan<byte[]> dataChannels);
}
