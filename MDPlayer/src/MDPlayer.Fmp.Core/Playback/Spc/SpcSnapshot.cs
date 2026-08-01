using System.Text;

namespace Fmp.Core.Playback.Spc;

/// <summary>SPC700 snapshot: signature, CPU regs, 64 KiB RAM, 128 DSP regs, ID666 metadata.</summary>
internal sealed record SpcMetadata
{
    public const string Signature = "SNES-SPC700 Sound File Data v0.30";
    public const int SignatureLength = 33;
    public const int HeaderSize = 0x100;
    public const int RamSize = 0x1_0000;
    public const int DspRegisterSize = 0x80;
    public const int MinimumFileSize = HeaderSize + RamSize + DspRegisterSize;

    public bool HasId666 { get; init; }
    public string SongTitle { get; init; } = "";
    public string GameTitle { get; init; } = "";
    public string Dumper { get; init; } = "";
    public string Comment { get; init; } = "";
    public string Artist { get; init; } = "";
    public int? PlayLengthSeconds { get; init; }
    public int? FadeLengthMilliseconds { get; init; }
    public string Emulator { get; init; } = "";
}

internal sealed record SpcCpuRegisters(byte A, byte X, byte Y, byte Psw, ushort Sp, ushort Pc)
{
    public static SpcCpuRegisters Parse(ReadOnlySpan<byte> h)
    {
        if (h.Length < SpcMetadata.HeaderSize)
            throw new SpcFormatException("SPC header is truncated");
        return new SpcCpuRegisters(h[0x27], h[0x28], h[0x29], h[0x2A],
            (ushort)(h[0x2D] | (h[0x2C] << 8)), (ushort)(h[0x26] | (h[0x25] << 8)));
    }
}

internal sealed class SpcSnapshot
{
    public SpcSnapshot(SpcMetadata metadata, SpcCpuRegisters cpu, byte[] ram, byte[] dspRegisters, IReadOnlyList<string> warnings)
    {
        Metadata = metadata; Cpu = cpu; Ram = ram; DspRegisters = dspRegisters; Warnings = warnings;
    }

    public SpcMetadata Metadata { get; }
    public SpcCpuRegisters Cpu { get; }
    public byte[] Ram { get; }
    public byte[] DspRegisters { get; }
    public IReadOnlyList<string> Warnings { get; }

    public static SpcSnapshot Parse(ReadOnlyMemory<byte> input)
    {
        ReadOnlySpan<byte> data = input.Span;
        if (data.Length < SpcMetadata.MinimumFileSize)
            throw new SpcFormatException($"SPC file is too small ({data.Length} bytes; minimum {SpcMetadata.MinimumFileSize})");

        Span<char> sig = stackalloc char[SpcMetadata.SignatureLength];
        for (int i = 0; i < SpcMetadata.SignatureLength; i++)
            sig[i] = (char)data[i];
        if (!sig.SequenceEqual(SpcMetadata.Signature))
            throw new SpcFormatException("SPC signature mismatch");

        var cpu = SpcCpuRegisters.Parse(data[..SpcMetadata.HeaderSize]);
        byte[] ram = data.Slice(SpcMetadata.HeaderSize, SpcMetadata.RamSize).ToArray();
        byte[] dsp = data.Slice(SpcMetadata.HeaderSize + SpcMetadata.RamSize, SpcMetadata.DspRegisterSize).ToArray();
        var warnings = new List<string>();
        return new SpcSnapshot(SpcMetadataParser.Parse(data, warnings), cpu, ram, dsp, warnings);
    }
}

internal sealed class SpcFormatException : Exception
{
    public SpcFormatException(string message) : base(message) { }
}
