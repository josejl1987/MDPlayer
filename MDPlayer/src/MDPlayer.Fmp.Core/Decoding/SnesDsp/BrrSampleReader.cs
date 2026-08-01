using System.Security.Cryptography;

namespace Fmp.Core.Decoding.SnesDsp;

/// <summary>Result of walking a BRR sample chain. <see cref="Hash"/> is a stable SHA-256
/// (lowercase hex) over the raw encoded block bytes in chain order, independent of RAM location.</summary>
internal sealed record BrrSample(
    ushort StartAddress,
    ushort LoopAddress,
    byte[] EncodedBlocks,
    bool Loops,
    bool Valid,
    string Hash)
{
    public string ShortHash => Hash.Length >= 8 ? Hash[..8] : Hash;
}

/// <summary>Iterative, bounds-checked walker over a BRR sample chain in SPC RAM (spec §14.2).
/// Stops on the end flag, on a repeated block address (cycle) or on the safety limits; never
/// follows the loop address, never recurses, never throws (malformed data yields Valid=false).</summary>
internal static class BrrSampleReader
{
    public const int BlockSize = 9;
    public const int MaxBlocks = 4096;
    public const int MaxBytes = 65536;

    private const byte EndFlag = 0x01;
    private const byte LoopFlag = 0x02;
    public static BrrSample Read(ReadOnlySpan<byte> ram, ushort startAddress, ushort loopAddress)
    {
        if (ram.Length == 0)
            return Invalid(startAddress, loopAddress);

        var blocks = new List<byte[]>(Math.Min(MaxBlocks, 64));
        var seen = new HashSet<ushort>();
        ushort address = startAddress;
        int blockCount = 0;
        int bytesRead = 0;
        byte lastHeader = 0;

        while (true)
        {
            if (blockCount >= MaxBlocks || bytesRead + BlockSize > MaxBytes || !seen.Add(address))
                break; // max block count, max bytes traversed, or cycle

            var block = new byte[BlockSize];
            if (!TryReadBlock(ram, address, block))
                break; // block would be truncated/out of range

            blocks.Add(block);
            blockCount++;
            bytesRead += BlockSize;
            lastHeader = block[0];

            if ((lastHeader & EndFlag) != 0)
            {
                byte[] encoded = Concat(blocks);
                bool loops = (lastHeader & LoopFlag) != 0;
                return new BrrSample(startAddress, loopAddress, encoded, loops, true, Sha256Hex(encoded));
            }

            address = (ushort)(address + BlockSize);
        }

        // Abnormal termination (limits, cycle, truncated RAM): return the partial chain with a deterministic hash.
        byte[] partial = Concat(blocks);
        return new BrrSample(startAddress, loopAddress, partial, false, false, Sha256Hex(partial));
    }

    internal static string Sha256Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static bool TryReadBlock(ReadOnlySpan<byte> ram, ushort address, byte[] block)
    {
        for (int i = 0; i < BlockSize; i++)
        {
            ushort p = (ushort)(address + i); // 16-bit wrap: bytes past 0xFFFF continue at 0x0000
            if (p >= ram.Length)
                return false;
            block[i] = ram[p];
        }
        return true;
    }

    private static BrrSample Invalid(ushort startAddress, ushort loopAddress) =>
        new(startAddress, loopAddress, Array.Empty<byte>(), false, false, Sha256Hex(ReadOnlySpan<byte>.Empty));

    private static byte[] Concat(List<byte[]> blocks) => blocks.SelectMany(b => b).ToArray();
}
