using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Fmp.Core.Rendering;

/// <summary>
/// Canonical capture hash (Workstream B). Produces a stable SHA-256 over the
/// ordered, semantic content of an <see cref="FmpExecutionCapture"/> using an
/// explicit little-endian byte encoding — never JSON property order, never
/// platform-native struct layout, never <see cref="System.Runtime.Serialization.Formatters.Binary.BinaryFormatter"/>.
///
/// The hash intentionally excludes: absolute filesystem paths, object
/// addresses, runtime type names, debug strings, process IDs and wall-clock
/// timestamps. Bank content is included only via its stable SHA-256 (the bank
/// bytes themselves are not re-serialized here; the bank snapshot hash already
/// captures them).
///
/// Two captures with identical semantic content on any platform yield the
/// identical hash. It is the authoritative cross-session / cross-platform
/// comparison key for capture determinism (Workstream C).
/// </summary>
internal static class CaptureHasher
{
    /// <summary>Bumped whenever the canonical byte encoding changes.</summary>
    public const uint CaptureHashVersion = 2;

    private static void WriteU8(Span<byte> dst, ref int off, byte value) => dst[off++] = value;

    private static void WriteU32(Span<byte> dst, ref int off, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(off, 4), value);
        off += 4;
    }

    private static void WriteU64(Span<byte> dst, ref int off, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(off, 8), value);
        off += 8;
    }

    private static void WriteBytes(Span<byte> dst, ref int off, ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(off, 4), value.Length);
        off += 4;
        value.CopyTo(dst.Slice(off, value.Length));
        off += value.Length;
    }

    private static void WriteString(Span<byte> dst, ref int off, string value)
        => WriteBytes(dst, ref off, Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// Computes the canonical SHA-256 of the capture. The hash covers the
    /// capture version, output rate (capture scheduling is rate-dependent), the
    /// fully-ordered event stream, bank IDs + hashes in capture order, and the
    /// termination metadata. Returns uppercase hex SHA-256.
    /// </summary>
    public static string Sha256(FmpExecutionCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);

        // Upper-bound size estimate: header + events (with worst-case PPZ8
        // payload) + banks + termination strings.
        checked
        {
            int size = 64
                + capture.Events.Count * 32
                + capture.Ppz8Banks.Count * 80
                + (capture.TerminationReason ?? "").Length * 3;
            return Compute(capture, Math.Max(size, 64));
        }
    }

    private static string Compute(FmpExecutionCapture capture, int estimatedSize)
    {
        var buffer = new byte[estimatedSize];
        int off = 0;

        // Capture version (uint), output rate-present flag + rate.
        WriteU32(buffer, ref off, CaptureHashVersion);
        WriteU8(buffer, ref off, 1); // output rate is always present
        WriteU32(buffer, ref off, (uint)capture.OutputSampleRate);

        // Fully ordered event stream: type (0=OPNA, 1=PPZ8), master clock, seq, payload.
        foreach (var e in capture.Events)
        {
            switch (e)
            {
                case CapturedOpnaWrite opna:
                    WriteU8(buffer, ref off, 0);
                    WriteU64(buffer, ref off, opna.OpnaMasterClock);
                    WriteU64(buffer, ref off, opna.Sequence);
                    WriteU8(buffer, ref off, opna.Port);
                    WriteU8(buffer, ref off, opna.Address);
                    WriteU8(buffer, ref off, opna.Data);
                    break;
                case CapturedPpz8Command ppz8:
                    WriteU8(buffer, ref off, 1);
                    WriteU64(buffer, ref off, ppz8.OpnaMasterClock);
                    WriteU64(buffer, ref off, ppz8.Sequence);
                    WriteU32(buffer, ref off, (uint)ppz8.Port);
                    WriteU32(buffer, ref off, (uint)ppz8.Address);
                    WriteU32(buffer, ref off, (uint)ppz8.Data);
                    WriteU32(buffer, ref off, (uint)ppz8.BankId);
                    break;
                default:
                    throw new InvalidOperationException($"unsupported captured event type: {e.GetType().Name}");
            }
        }

        // Bank IDs + hashes in capture order (bank bytes are covered by hashes).
        WriteU32(buffer, ref off, (uint)capture.Ppz8Banks.Count);
        foreach (var bank in capture.Ppz8Banks)
        {
            WriteU32(buffer, ref off, (uint)bank.BankId);
            WriteBytes(buffer, ref off, Encoding.ASCII.GetBytes(bank.Sha256));
        }

        // Termination metadata.
        WriteU64(buffer, ref off, capture.FinalOpnaMasterClock);
        WriteU64(buffer, ref off, (ulong)capture.FinalOutputFrame);
        WriteU64(buffer, ref off, (ulong)capture.FadeStartOutputFrame);
        WriteU64(buffer, ref off, (ulong)capture.FadeEndOutputFrame);
        WriteU64(buffer, ref off, (ulong)capture.TailEndOutputFrame);
        WriteU32(buffer, ref off, (uint)capture.LoopCount);
        WriteString(buffer, ref off, capture.TerminationReason ?? "");

        return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, off))).ToLowerInvariant();
    }
}
