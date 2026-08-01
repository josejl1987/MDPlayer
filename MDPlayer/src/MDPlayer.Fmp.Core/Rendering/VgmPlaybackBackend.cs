using System.IO.Compression;
using Fmp.Core.Audio;
using Fmp.Core.Audio.Mdsound;
using Fmp.Core.Visualization;

namespace Fmp.Core.Rendering;

internal sealed record VgmRegisterWrite(
    long SourceSample,
    DeviceId Device,
    int Port,
    int Address,
    int Data);

internal sealed record VgmSampleAsset(
    DeviceId Device,
    uint RomSize,
    uint StartAddress,
    byte[] Data,
    AssetKind Kind);

internal sealed class VgmDocument
{
    private VgmDocument(
        IReadOnlyList<DeviceDescriptor> devices,
        IReadOnlyList<VgmRegisterWrite> writes,
        long endSample,
        long? loopSample,
        IReadOnlyList<VgmSampleAsset> assets,
        IReadOnlyList<string> warnings)
    {
        Devices = devices;
        Writes = writes;
        EndSample = endSample;
        LoopSample = loopSample;
        Assets = assets;
        Warnings = warnings;
    }

    public IReadOnlyList<DeviceDescriptor> Devices { get; }
    public IReadOnlyList<VgmRegisterWrite> Writes { get; }
    public long EndSample { get; }
    public long? LoopSample { get; }
    public IReadOnlyList<VgmSampleAsset> Assets { get; }
    public IReadOnlyList<string> Warnings { get; }

    public static VgmDocument Parse(ReadOnlyMemory<byte> input)
    {
        ReadOnlySpan<byte> data = input.Span;
        if (data.Length < 0x40 || Read32(data, 0) != 0x206D6756)
            throw new VgmPlaybackException("input is not a valid VGM stream");

        uint version = Read32(data, 0x08);
        long eof = Read32(data, 0x04) == 0
            ? data.Length
            : Math.Min(data.Length, 4L + Read32(data, 0x04));
        int dataStart = version >= 0x0000_0150 && Read32(data, 0x34) != 0
            ? checked((int)(0x34 + Read32(data, 0x34)))
            : 0x40;
        if (dataStart < 0x40 || dataStart >= eof)
            throw new VgmPlaybackException("VGM data offset is outside the file");

        var devices = new Dictionary<DeviceId, DeviceDescriptor>();
        AddClockDevices(data, dataStart, devices);
        var writes = new List<VgmRegisterWrite>();
        var assets = new List<VgmSampleAsset>();
        var warnings = new List<string>();
        long sourceSample = 0;
        long? loopSample = null;
        long loopAddress = Read32(data, 0x1C) == 0 ? -1 : 0x1C + Read32(data, 0x1C);
        int cursor = dataStart;
        byte[] ym2612DacData = null;
        int ym2612DacCursor = 0;
        bool warnedAboutMissingDacData = false;
        bool ended = false;

        while (cursor < eof)
        {
            if (cursor == loopAddress)
                loopSample = sourceSample;

            byte command = data[cursor++];
            switch (command)
            {
                case 0x4F:
                    Skip(data, ref cursor, 1, eof, command);
                    break;
                case 0x50:
                    EnsureDevice(devices, ChipType.Sn76489, 0, 3_579_545);
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(ChipType.Sn76489, 0),
                        0,
                        0,
                        data[cursor++]));
                    break;
                case 0x52:
                case 0x53:
                    EnsureDevice(devices, ChipType.Ym2612, 0, 7_670_454);
                    Require(data, cursor, 2, eof, command);
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(ChipType.Ym2612, 0),
                        command == 0x52 ? 0 : 1,
                        data[cursor],
                        data[cursor + 1]));
                    cursor += 2;
                    break;
                case 0x54:
                    EnsureDevice(devices, ChipType.Ym2151, 0, 3_579_545);
                    Require(data, cursor, 2, eof, command);
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(ChipType.Ym2151, 0),
                        0,
                        data[cursor],
                        data[cursor + 1]));
                    cursor += 2;
                    break;
                case 0x5C:
                    EnsureDevice(devices, ChipType.Y8950, 0, 3_579_545);
                    Require(data, cursor, 2, eof, command);
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(ChipType.Y8950, 0),
                        0,
                        data[cursor],
                        data[cursor + 1]));
                    cursor += 2;
                    break;
                case 0x5D:
                case 0xAD:
                    Require(data, cursor, 3, eof, command);
                    int ymz280bInstance = (data[cursor] & 0x80) != 0 ? 1 : 0;
                    EnsureDevice(devices, ChipType.Ymz280b, ymz280bInstance, 16_934_400);
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(ChipType.Ymz280b, ymz280bInstance),
                        0,
                        data[cursor + 1],
                        data[cursor + 2]));
                    cursor += 3;
                    break;
                case 0x55:
                    EnsureDevice(devices, ChipType.Ym2203, 0, 3_579_545);
                    Require(data, cursor, 2, eof, command);
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(ChipType.Ym2203, 0),
                        0,
                        data[cursor],
                        data[cursor + 1]));
                    cursor += 2;
                    break;
                case 0x56:
                case 0x57:
                    EnsureDevice(devices, ChipType.Ym2608, 0, 7_987_200);
                    Require(data, cursor, 2, eof, command);
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(ChipType.Ym2608, 0),
                        command == 0x56 ? 0 : 1,
                        data[cursor],
                        data[cursor + 1]));
                    cursor += 2;
                    break;
                case 0x58:
                case 0x59:
                    EnsureDevice(devices, ChipType.Ym2610, 0, 8_000_000);
                    Require(data, cursor, 2, eof, command);
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(ChipType.Ym2610, 0),
                        command == 0x58 ? 0 : 1,
                        data[cursor],
                        data[cursor + 1]));
                    cursor += 2;
                    break;
                case 0x5A:
                    EnsureDevice(devices, ChipType.Ym3812, 0, 3_579_545);
                    Require(data, cursor, 2, eof, command);
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(ChipType.Ym3812, 0),
                        0,
                        data[cursor],
                        data[cursor + 1]));
                    cursor += 2;
                    break;
                case 0x5B:
                    EnsureDevice(devices, ChipType.Ym3526, 0, 3_579_545);
                    Require(data, cursor, 2, eof, command);
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(ChipType.Ym3526, 0),
                        0,
                        data[cursor],
                        data[cursor + 1]));
                    cursor += 2;
                    break;
                case 0x5E:
                case 0x5F:
                    EnsureDevice(devices, ChipType.Ymf262, 0, 14_318_180);
                    Require(data, cursor, 2, eof, command);
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(ChipType.Ymf262, 0),
                        command == 0x5E ? 0 : 1,
                        data[cursor],
                        data[cursor + 1]));
                    cursor += 2;
                    break;
                case 0x51:
                    EnsureDevice(devices, ChipType.Ym2413, 0, 3_579_545);
                    Require(data, cursor, 2, eof, command);
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(ChipType.Ym2413, 0),
                        0,
                        data[cursor],
                        data[cursor + 1]));
                    cursor += 2;
                    break;
                case 0xA0:
                    EnsureDevice(devices, ChipType.Ay8910, 0, 1_789_773);
                    Require(data, cursor, 2, eof, command);
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(ChipType.Ay8910, 0),
                        0,
                        data[cursor],
                        data[cursor + 1]));
                    cursor += 2;
                    break;
                case 0xB3:
                case 0xB4:
                case 0xB9:
                    Require(data, cursor, 2, eof, command);
                    ChipType registerType = command switch
                    {
                        0xB3 => ChipType.Dmg,
                        0xB4 => ChipType.NesApu,
                        _ => ChipType.Huc6280,
                    };
                    int registerInstance = (data[cursor] & 0x80) != 0 ? 1 : 0;
                    EnsureDevice(devices, registerType, registerInstance, registerType switch
                    {
                        ChipType.Dmg => 4_194_304,
                        ChipType.NesApu => 1_789_773,
                        _ => 3_579_545,
                    });
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(registerType, registerInstance),
                        0,
                        data[cursor] & 0x7F,
                        data[cursor + 1]));
                    cursor += 2;
                    break;
                case 0xB7:
                    Require(data, cursor, 2, eof, command);
                    EnsureDevice(devices, ChipType.Okim6258, 0, 4_000_000);
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(ChipType.Okim6258, 0),
                        0,
                        data[cursor] & 0x7F,
                        data[cursor + 1]));
                    cursor += 2;
                    break;
                case 0xB8:
                    Require(data, cursor, 2, eof, command);
                    int okim6295Instance = (data[cursor] & 0x80) != 0 ? 1 : 0;
                    EnsureDevice(devices, ChipType.Okim6295, okim6295Instance, 4_000_000);
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(ChipType.Okim6295, okim6295Instance),
                        0,
                        data[cursor] & 0x7F,
                        data[cursor + 1]));
                    cursor += 2;
                    break;
                case 0xB5:
                    Require(data, cursor, 2, eof, command);
                    int multiPcmInstance = (data[cursor] & 0x80) != 0 ? 1 : 0;
                    EnsureDevice(devices, ChipType.MultiPcm, multiPcmInstance, 8_000_000);
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(ChipType.MultiPcm, multiPcmInstance),
                        0,
                        data[cursor] & 0x7F,
                        data[cursor + 1]));
                    cursor += 2;
                    break;
                case 0xB0:
                case 0xB1:
                case 0xBF:
                    Require(data, cursor, 2, eof, command);
                    ChipType pcmRegisterType = command switch
                    {
                        0xB0 => ChipType.Rf5c68,
                        0xB1 => ChipType.Rf5c164,
                        _ => ChipType.Ga20,
                    };
                    int pcmRegisterInstance = (data[cursor] & 0x80) != 0 ? 1 : 0;
                    EnsureDevice(devices, pcmRegisterType, pcmRegisterInstance, pcmRegisterType switch
                    {
                        ChipType.Rf5c68 or ChipType.Rf5c164 => 12_500_000,
                        _ => 8_000_000,
                    });
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(pcmRegisterType, pcmRegisterInstance),
                        0,
                        data[cursor] & 0x7F,
                        data[cursor + 1]));
                    cursor += 2;
                    break;
                case 0xC0:
                    Require(data, cursor, 3, eof, command);
                    EnsureDevice(devices, ChipType.SegaPcm, 0, 4_000_000);
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(ChipType.SegaPcm, 0),
                        0,
                        data[cursor] | (data[cursor + 1] << 8),
                        data[cursor + 2]));
                    cursor += 3;
                    break;
                case 0xD2:
                    Require(data, cursor, 3, eof, command);
                    int k051649Instance = (data[cursor] & 0x80) != 0 ? 1 : 0;
                    EnsureDevice(devices, ChipType.K051649, k051649Instance, 3_579_545);
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(ChipType.K051649, k051649Instance),
                        0,
                        (data[cursor] & 0x7F) << 1,
                        data[cursor + 1]));
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(ChipType.K051649, k051649Instance),
                        0,
                        ((data[cursor] & 0x7F) << 1) | 1,
                        data[cursor + 2]));
                    cursor += 3;
                    break;
                case 0xD0:
                    Require(data, cursor, 3, eof, command);
                    int ymf278bInstance = (data[cursor] & 0x80) != 0 ? 1 : 0;
                    EnsureDevice(devices, ChipType.Ymf278b, ymf278bInstance, 33_868_800);
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(ChipType.Ymf278b, ymf278bInstance),
                        data[cursor] & 0x7F,
                        data[cursor + 1],
                        data[cursor + 2]));
                    cursor += 3;
                    break;
                case 0xD3:
                case 0xD4:
                    Require(data, cursor, 3, eof, command);
                    ChipType pcmWideType = command == 0xD3 ? ChipType.K054539 : ChipType.C140;
                    int pcmWideInstance = (data[cursor] & 0x80) != 0 ? 1 : 0;
                    EnsureDevice(devices, pcmWideType, pcmWideInstance,
                        pcmWideType == ChipType.K054539 ? 18_000_000 : 21_390);
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(pcmWideType, pcmWideInstance),
                        0,
                        ((data[cursor] & 0x7F) << 8) | data[cursor + 1],
                        data[cursor + 2]));
                    cursor += 3;
                    break;
                case 0xE1:
                    Require(data, cursor, 4, eof, command);
                    int c352Instance = (data[cursor] & 0x80) != 0 ? 1 : 0;
                    EnsureDevice(devices, ChipType.C352, c352Instance, 24_192_000);
                    writes.Add(new VgmRegisterWrite(
                        sourceSample,
                        new DeviceId(ChipType.C352, c352Instance),
                        0,
                        ((data[cursor] & 0x7F) << 8) | data[cursor + 1],
                        (data[cursor + 2] << 8) | data[cursor + 3]));
                    cursor += 4;
                    break;
                case 0x61:
                    Require(data, cursor, 2, eof, command);
                    sourceSample += data[cursor] | (data[cursor + 1] << 8);
                    cursor += 2;
                    break;
                case 0x62:
                    sourceSample += 735;
                    break;
                case 0x63:
                    sourceSample += 882;
                    break;
                case 0x66:
                    ended = true;
                    cursor = (int)eof;
                    break;
                case >= 0x70 and <= 0x7F:
                    sourceSample += (command & 0x0F) + 1;
                    break;
                case >= 0x80 and <= 0x8F:
                    EnsureDevice(devices, ChipType.Ym2612, 0, 7_670_454);
                    if (ym2612DacData is not null && ym2612DacCursor < ym2612DacData.Length)
                    {
                        writes.Add(new VgmRegisterWrite(
                            sourceSample,
                            new DeviceId(ChipType.Ym2612, 0),
                            0,
                            0x2A,
                            ym2612DacData[ym2612DacCursor++]));
                    }
                    else if (!warnedAboutMissingDacData)
                    {
                        warnings.Add("YM2612 DAC stream command has no available type 0 data block");
                        warnedAboutMissingDacData = true;
                    }
                    sourceSample += command & 0x0F;
                    break;
                case 0x67:
                    Require(data, cursor, 6, eof, command);
                    if (data[cursor] != 0x66)
                        throw new VgmPlaybackException("VGM data block is missing its 0x66 marker");
                    uint encodedBlockLength = Read32(data, cursor + 2);
                    int assetInstance = (encodedBlockLength & 0x8000_0000) != 0 ? 1 : 0;
                    uint blockLength = encodedBlockLength & 0x7FFF_FFFF;
                    if (data[cursor + 1] == 0x00)
                    {
                        if (blockLength > int.MaxValue
                            || cursor + 6L + blockLength > data.Length)
                            throw new VgmPlaybackException("YM2612 DAC data block exceeds the file");
                        ym2612DacData = data.Slice(cursor + 6, (int)blockLength).ToArray();
                        ym2612DacCursor = 0;
                    }
                    if (TryReadSampleAsset(
                        data,
                        cursor,
                        data[cursor + 1],
                        blockLength,
                        assetInstance,
                        out VgmSampleAsset asset))
                        assets.Add(asset);
                    cursor = checked(cursor + 6 + (int)blockLength);
                    if (cursor > eof)
                        throw new VgmPlaybackException("VGM data block exceeds the file");
                    break;
                case 0x68:
                    Skip(data, ref cursor, 11, eof, command);
                    break;
                case 0xE0:
                    Require(data, cursor, 4, eof, command);
                    uint dacOffset = Read32(data, cursor);
                    ym2612DacCursor = dacOffset > int.MaxValue
                        ? int.MaxValue
                        : (int)dacOffset;
                    cursor += 4;
                    break;
                case 0x90:
                    Skip(data, ref cursor, 4, eof, command);
                    break;
                case 0x91:
                case 0x92:
                    Skip(data, ref cursor, 5, eof, command);
                    break;
                case 0x93:
                    Skip(data, ref cursor, 10, eof, command);
                    break;
                case 0x94:
                    Skip(data, ref cursor, 1, eof, command);
                    break;
                case 0x95:
                    Skip(data, ref cursor, 2, eof, command);
                    break;
                default:
                    if (!TrySkipKnownCommand(data, ref cursor, command, eof))
                    {
                        warnings.Add($"VGM command 0x{command:X2} is not decoded; playback stops at sample {sourceSample}");
                        cursor = (int)eof;
                    }
                    break;
            }

            if (ended)
                break;
        }

        if (!ended)
            warnings.Add("VGM stream reached EOF without an explicit end command");
        if (sourceSample <= 0 && writes.Count > 0)
            sourceSample = 1;

        long? normalizedLoop = loopSample.HasValue
            && loopSample.Value >= 0
            && loopSample.Value < sourceSample
            ? loopSample.Value
            : null;

        return new VgmDocument(
            devices.Values.OrderBy(device => device.Id.ToString(), StringComparer.Ordinal).ToArray(),
            writes,
            sourceSample,
            normalizedLoop,
            assets,
            warnings);
    }

    private static bool TryReadSampleAsset(
        ReadOnlySpan<byte> data,
        int blockCursor,
        byte blockType,
        uint blockLength,
        int instance,
        out VgmSampleAsset asset)
    {
        asset = null;
        if (blockLength == 0)
            return false;

        ChipType type;
        uint romSize;
        uint startAddress;
        int payloadOffset;
        uint payloadLength;
        AssetKind kind = AssetKind.Pcm;
        if (blockType >= 0x80 && blockType <= 0x9F && blockLength >= 8)
        {
            romSize = Read32(data, blockCursor + 6);
            startAddress = Read32(data, blockCursor + 10);
            payloadOffset = blockCursor + 14;
            payloadLength = blockLength - 8;
            type = blockType switch
            {
                0x80 => ChipType.SegaPcm,
                0x81 => ChipType.Ym2608,
                0x82 or 0x83 => ChipType.Ym2610,
                0x84 or 0x87 => ChipType.Ymf278b,
                0x86 => ChipType.Ymz280b,
                0x88 => ChipType.Y8950,
                0x89 => ChipType.MultiPcm,
                0x8B => ChipType.Okim6295,
                0x8C => ChipType.K054539,
                0x8D => ChipType.C140,
                0x92 => ChipType.C352,
                0x93 => ChipType.Ga20,
                _ => ChipType.Unknown,
            };
            kind = blockType switch
            {
                0x81 => AssetKind.Adpcm,
                0x82 => AssetKind.AdpcmA,
                0x83 => AssetKind.AdpcmB,
                _ => AssetKind.Pcm,
            };
        }
        else if ((blockType is 0xC0 or 0xC1 or 0xC2) && blockLength >= 2)
        {
            romSize = 0;
            startAddress = (uint)(data[blockCursor + 6] | (data[blockCursor + 7] << 8));
            payloadOffset = blockCursor + 8;
            payloadLength = blockLength - 2;
            type = blockType switch
            {
                0xC0 => ChipType.Rf5c68,
                0xC1 => ChipType.Rf5c164,
                _ => ChipType.NesApu,
            };
            kind = AssetKind.SampleBank;
        }
        else
        {
            return false;
        }

        if (type == ChipType.Unknown
            || payloadLength > int.MaxValue
            || payloadOffset < 0
            || payloadOffset + (int)payloadLength > data.Length)
            return false;

        asset = new VgmSampleAsset(
            new DeviceId(type, instance),
            romSize,
            startAddress,
            data.Slice(payloadOffset, (int)payloadLength).ToArray(),
            kind);
        return true;
    }

    private static void AddClockDevices(
        ReadOnlySpan<byte> data,
        int dataStart,
        Dictionary<DeviceId, DeviceDescriptor> devices)
    {
        uint snClock = ReadHeader32(data, dataStart, 0x0C);
        uint opllClock = ReadHeader32(data, dataStart, 0x10);
        uint ym2203Clock = ReadHeader32(data, dataStart, 0x44);
        uint ym2608Clock = ReadHeader32(data, dataStart, 0x48);
        uint ym2610Clock = ReadHeader32(data, dataStart, 0x4C);
        uint ym3812Clock = ReadHeader32(data, dataStart, 0x50);
        uint ym3526Clock = ReadHeader32(data, dataStart, 0x54);
        uint y8950Clock = ReadHeader32(data, dataStart, 0x58);
        uint ymf262Clock = ReadHeader32(data, dataStart, 0x5C);
        uint ymf278bClock = ReadHeader32(data, dataStart, 0x60);
        uint ymClock = ReadHeader32(data, dataStart, 0x2C);
        uint opmClock = ReadHeader32(data, dataStart, 0x30);
        uint dmgClock = ReadHeader32(data, dataStart, 0x80);
        uint nesClock = ReadHeader32(data, dataStart, 0x84);
        uint k051649Clock = ReadHeader32(data, dataStart, 0x9C);
        uint huc6280Clock = ReadHeader32(data, dataStart, 0xA4);
        uint ymz280bClock = ReadHeader32(data, dataStart, 0x68);
        uint okim6258Clock = ReadHeader32(data, dataStart, 0x90);
        uint okim6295Clock = ReadHeader32(data, dataStart, 0x98);
        uint multiPcmClock = ReadHeader32(data, dataStart, 0x88);
        uint segaPcmClock = ReadHeader32(data, dataStart, 0x38);
        uint rf5c68Clock = ReadHeader32(data, dataStart, 0x40);
        uint rf5c164Clock = ReadHeader32(data, dataStart, 0x6C);
        uint k054539Clock = ReadHeader32(data, dataStart, 0xA0);
        uint c140Clock = ReadHeader32(data, dataStart, 0xA8);
        uint c352Clock = ReadHeader32(data, dataStart, 0xDC);
        uint ga20Clock = ReadHeader32(data, dataStart, 0xE0);
        AddClock(devices, ChipType.Sn76489, snClock, 3_579_545, "SN76489");
        AddClock(devices, ChipType.Ym2413, opllClock, 3_579_545, "YM2413");
        AddClock(devices, ChipType.Ym2203, ym2203Clock, 3_579_545, "YM2203");
        AddClock(devices, ChipType.Ym2608, ym2608Clock, 7_987_200, "YM2608");
        AddClock(devices, ChipType.Ym2610, ym2610Clock, 8_000_000, "YM2610");
        AddClock(devices, ChipType.Ym3812, ym3812Clock, 3_579_545, "YM3812");
        AddClock(devices, ChipType.Ym3526, ym3526Clock, 3_579_545, "YM3526");
        AddClock(devices, ChipType.Y8950, y8950Clock, 3_579_545, "Y8950");
        AddClock(devices, ChipType.Ymf262, ymf262Clock, 14_318_180, "YMF262");
        AddClock(devices, ChipType.Ymf278b, ymf278bClock, 33_868_800, "YMF278B");
        AddClock(devices, ChipType.Ym2612, ymClock, 7_670_454, "YM2612");
        AddClock(devices, ChipType.Ym2151, opmClock, 3_579_545, "YM2151");
        AddClock(devices, ChipType.Dmg, dmgClock, 4_194_304, "DMG");
        AddClock(devices, ChipType.NesApu, nesClock, 1_789_773, "NES APU");
        AddClock(devices, ChipType.K051649, k051649Clock, 3_579_545, "K051649");
        AddClock(devices, ChipType.Huc6280, huc6280Clock, 3_579_545, "HuC6280");
        AddClock(devices, ChipType.Ymz280b, ymz280bClock, 16_934_400, "YMZ280B");
        AddClock(devices, ChipType.Okim6258, okim6258Clock, 4_000_000, "OKIM6258");
        AddClock(devices, ChipType.Okim6295, okim6295Clock, 4_000_000, "OKIM6295");
        AddClock(devices, ChipType.MultiPcm, multiPcmClock, 8_000_000, "MultiPCM");
        AddClock(devices, ChipType.SegaPcm, segaPcmClock, 4_000_000, "SEGAPCM");
        AddClock(devices, ChipType.Rf5c68, rf5c68Clock, 12_500_000, "RF5C68");
        AddClock(devices, ChipType.Rf5c164, rf5c164Clock, 12_500_000, "RF5C164");
        AddClock(devices, ChipType.K054539, k054539Clock, 18_000_000, "K054539");
        AddClock(devices, ChipType.C140, c140Clock, 21_390, "C140");
        AddClock(devices, ChipType.C352, c352Clock, 24_192_000, "C352");
        AddClock(devices, ChipType.Ga20, ga20Clock, 8_000_000, "GA20");
    }

    private static void AddClock(
        Dictionary<DeviceId, DeviceDescriptor> devices,
        ChipType type,
        uint encodedClock,
        long fallback,
        string name)
    {
        uint clock = encodedClock & 0x3FFF_FFFF;
        if (clock == 0)
            return;
        int count = (encodedClock & 0x4000_0000) != 0 ? 2 : 1;
        for (int instance = 0; instance < count; instance++)
            EnsureDevice(devices, type, instance, clock == 0 ? fallback : clock);
    }

    private static void EnsureDevice(
        Dictionary<DeviceId, DeviceDescriptor> devices,
        ChipType type,
        int instance,
        long clock)
    {
        DeviceId id = new(type, instance);
        if (devices.ContainsKey(id))
            return;
        devices[id] = type switch
        {
            ChipType.Ym2203 => VisualizationDeviceCatalog.Ym2203(instance, clock),
            ChipType.Ym2608 => VisualizationDeviceCatalog.Ym2608(instance) with { ClockHz = clock },
            ChipType.Ym2610 => VisualizationDeviceCatalog.Ym2610(instance, clock),
            ChipType.Ym2413 => VisualizationDeviceCatalog.Ym2413(instance, clock),
            ChipType.Ym3526 => VisualizationDeviceCatalog.Ym3526(instance, clock),
            ChipType.Y8950 => VisualizationDeviceCatalog.Y8950(instance, clock),
            ChipType.Ym3812 => VisualizationDeviceCatalog.Ym3812(instance, clock),
            ChipType.Ymf262 => VisualizationDeviceCatalog.Ymf262(instance, clock),
            ChipType.Ymf278b => VisualizationDeviceCatalog.Ymf278b(instance, clock),
            ChipType.Ym2612 => VisualizationDeviceCatalog.Ym2612(instance, clock),
            ChipType.Ym2151 => VisualizationDeviceCatalog.Ym2151(instance, clock),
            ChipType.Sn76489 => VisualizationDeviceCatalog.Sn76489(instance, clock),
            ChipType.Ay8910 => VisualizationDeviceCatalog.Ay8910(instance, clock),
            ChipType.Dmg => VisualizationDeviceCatalog.Dmg(instance, clock),
            ChipType.NesApu => VisualizationDeviceCatalog.NesApu(instance, clock),
            ChipType.Huc6280 => VisualizationDeviceCatalog.Huc6280(instance, clock),
            ChipType.K051649 => VisualizationDeviceCatalog.K051649(instance, clock),
            ChipType.Ymz280b => VisualizationDeviceCatalog.Ymz280b(instance, clock),
            ChipType.Okim6258 => VisualizationDeviceCatalog.Okim6258(instance, clock),
            ChipType.Okim6295 => VisualizationDeviceCatalog.Okim6295(instance, clock),
            ChipType.MultiPcm => VisualizationDeviceCatalog.MultiPcm(instance, clock),
            ChipType.SegaPcm => VisualizationDeviceCatalog.SegaPcm(instance, clock),
            ChipType.Rf5c68 => VisualizationDeviceCatalog.Rf5c68(instance, clock),
            ChipType.Rf5c164 => VisualizationDeviceCatalog.Rf5c164(instance, clock),
            ChipType.C140 => VisualizationDeviceCatalog.C140(instance, clock),
            ChipType.C352 => VisualizationDeviceCatalog.C352(instance, clock),
            ChipType.K054539 => VisualizationDeviceCatalog.K054539(instance, clock),
            ChipType.Ga20 => VisualizationDeviceCatalog.Ga20(instance, clock),
            _ => new DeviceDescriptor(id, id.ToString(), clock, DeviceCapabilities.None),
        };
    }

    private static bool TrySkipKnownCommand(ReadOnlySpan<byte> data, ref int cursor, byte command, long eof)
    {
        int count = command switch
        {
            >= 0x30 and <= 0x3F => 1,
            >= 0x40 and <= 0x4E => 2,
            >= 0x51 and <= 0x5F => 2,
            >= 0xA0 and <= 0xAF => 2,
            >= 0xB0 and <= 0xBF => 2,
            >= 0xC0 and <= 0xCF => 3,
            >= 0xD0 and <= 0xDF => 3,
            0x60 or 0x64 or 0x65 => 1,
            0xE0 => 4,
            _ => -1,
        };
        if (count < 0)
            return false;
        Skip(data, ref cursor, count, eof, command);
        return true;
    }

    private static void Require(ReadOnlySpan<byte> data, int cursor, int count, long eof, byte command)
    {
        if (cursor < 0 || cursor + count > eof || cursor + count > data.Length)
            throw new VgmPlaybackException($"VGM command 0x{command:X2} is truncated");
    }

    private static void Skip(ReadOnlySpan<byte> data, ref int cursor, int count, long eof, byte command)
    {
        Require(data, cursor, count, eof, command);
        cursor += count;
    }

    private static uint Read32(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length)
            return 0;
        return (uint)(data[offset]
            | (data[offset + 1] << 8)
            | (data[offset + 2] << 16)
            | (data[offset + 3] << 24));
    }

    private static uint ReadHeader32(ReadOnlySpan<byte> data, int dataStart, int offset) =>
        offset + 4 <= dataStart ? Read32(data, offset) : 0;
}

internal sealed class VgmPlaybackBackend : IPlaybackBackend
{
    public string Id => "vgm";

    public PlaybackProbeResult Probe(FileInfo input, PlaybackEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.Exists)
            return new PlaybackProbeResult(false, "vgm", [], [], [$"input not found: {input.FullName}"]);

        try
        {
            VgmDocument document = VgmDocument.Parse(VgmInput.Read(input.FullName));
            ChipTimelineDecoderRegistry decoderRegistry = ChipTimelineDecoderRegistry.CreateDefault();
            bool visualizable = document.Devices.Any(device =>
                decoderRegistry.HasDecoder(device.Id.Type));
            return new PlaybackProbeResult(
                true,
                "vgm",
                [],
                [],
                document.Warnings)
            {
                Visualizable = visualizable,
                Portable = true,
                Availability = PlaybackAvailability.Available,
            };
        }
        catch (Exception ex) when (ex is IOException or VgmPlaybackException)
        {
            return new PlaybackProbeResult(false, "vgm", [], [], [ex.Message]);
        }
    }

    public IPlaybackCaptureSession Open(
        FileInfo input,
        PlaybackOptions options,
        IPlaybackEventSink eventSink)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventSink);
        VgmDocument document = VgmDocument.Parse(VgmInput.Read(input.FullName));
        return new VgmCaptureSession(document, options, eventSink);
    }
}

internal sealed class VgmCaptureSession : IPlaybackCaptureSession
{
    private readonly VgmDocument _document;
    private readonly PlaybackOptions _options;
    private readonly IPlaybackEventSink _events;
    private bool _stopped;

    public VgmCaptureSession(VgmDocument document, PlaybackOptions options, IPlaybackEventSink events)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        if (_options.LoopCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.LoopCount));
        Timing = new CaptureTimingInfo(_options.SampleRate, 1).Validate();
    }

    public CaptureTimingInfo Timing { get; }
    public IReadOnlyList<DeviceDescriptor> Devices => _document.Devices;
    public long SamplePosition { get; private set; }
    public bool IsComplete { get; private set; }

    public void Run(CancellationToken cancellationToken = default)
    {
        if (IsComplete)
            throw new InvalidOperationException("The VGM capture session has already completed.");

        foreach (DeviceDescriptor device in _document.Devices)
            _events.OnDevice(device);
        if (_events is TimelineDecoderEventSink timelineSink)
        {
            foreach (string warning in _document.Warnings)
                timelineSink.ReportWarning(warning);

            foreach (DeviceDescriptor device in _document.Devices)
            {
                if (device.Id.Type == ChipType.Ga20
                    && !_document.Assets.Any(asset => asset.Device == device.Id))
                {
                    timelineSink.ReportWarning(
                        $"{device.Id}: master PCM renderer skipped because no GA20 sample asset block was present");
                }
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_options.OutputAudioPath ?? "capture.wav")) ?? ".");
        using var audio = new VgmAudioRenderer(_document.Devices, Timing.SampleRate, _document.Assets);
        foreach (VgmSampleAsset asset in _document.Assets)
        {
            _events.OnSampleAsset(new TimedSampleAssetEvent(
                0,
                asset.Device,
                $"vgm:{asset.Device}:{asset.StartAddress:X8}:{asset.Data.Length:X8}",
                asset.Kind,
                asset.Data.Length));
            audio.LoadAsset(asset);
        }
        WavWriter writer = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(_options.OutputAudioPath))
                writer = new WavWriter(_options.OutputAudioPath, Timing.SampleRate, 2);

            long expandedEnd = ScaleSample(ExpandedEndSample());
            long maxSamples = _options.MaxDurationSeconds.HasValue
                ? Math.Max(1, (long)Math.Round(_options.MaxDurationSeconds.Value * Timing.SampleRate))
                : long.MaxValue;
            long baseEnd = Math.Min(expandedEnd, maxSamples);
            long tail = Math.Max(0, (long)Math.Round(_options.TailSeconds * Timing.SampleRate));
            long end = Math.Min(maxSamples, checked(baseEnd + tail));
            long fade = Math.Max(0, (long)Math.Round(_options.FadeSeconds * Timing.SampleRate));
            long fadeStart = Math.Max(0, baseEnd - fade);
            long rendered = 0;
            foreach (VgmRegisterWrite write in ExpandWrites())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_stopped)
                    break;

                long target = ScaleSample(write.SourceSample);
                if (target >= baseEnd)
                    break;
                RenderUntil(audio, writer, ref rendered, target, fadeStart, baseEnd);
                var normalized = new TimedChipWrite(
                    target,
                    write.Device,
                    write.Port,
                    write.Address,
                    write.Data);
                _events.OnChipWrite(normalized);
                audio.Write(normalized);
                SamplePosition = target;
            }

            RenderUntil(audio, writer, ref rendered, end, fadeStart, baseEnd);
            SamplePosition = rendered;
            IsComplete = true;
        }
        finally
        {
            writer?.Close();
        }
    }

    public void Stop() => _stopped = true;

    public void Dispose() => Stop();

    private IEnumerable<VgmRegisterWrite> ExpandWrites()
    {
        long? loop = _document.LoopSample;
        long body = loop.HasValue ? _document.EndSample - loop.Value : 0;
        int count = loop.HasValue ? _options.LoopCount : 1;

        for (int pass = 0; pass < count; pass++)
        {
            long offset = pass == 0 || !loop.HasValue ? 0 : body * pass;
            if (pass > 0)
            {
                long boundary = _document.EndSample + body * (pass - 1);
                _events.OnLoopBoundary(new TimedLoopBoundary(ScaleSample(boundary), pass));
            }

            foreach (VgmRegisterWrite write in _document.Writes)
            {
                if (pass > 0 && write.SourceSample < loop.Value)
                    continue;
                yield return write with { SourceSample = write.SourceSample + offset };
            }
        }
    }

    private long ExpandedEndSample()
    {
        if (!_document.LoopSample.HasValue)
            return _document.EndSample;
        long body = _document.EndSample - _document.LoopSample.Value;
        return _document.EndSample + body * (_options.LoopCount - 1);
    }

    private long ScaleSample(long sourceSample) =>
        checked((long)Math.Round(sourceSample * Timing.SampleRate / 44_100.0, MidpointRounding.AwayFromZero));

    private static void RenderUntil(
        VgmAudioRenderer audio,
        WavWriter writer,
        ref long rendered,
        long target,
        long fadeStart,
        long baseEnd)
    {
        if (target < rendered)
            throw new InvalidOperationException("VGM events are not monotonic after loop expansion.");
        int remaining;
        while (rendered < target)
        {
            remaining = (int)Math.Min(1024, target - rendered);
            short[] pcm = audio.Render(remaining);
            ApplyFade(pcm, rendered, fadeStart, baseEnd);
            writer?.Write(pcm);
            rendered += remaining;
        }
    }

    private static void ApplyFade(short[] pcm, long startSample, long fadeStart, long baseEnd)
    {
        for (int index = 0; index < pcm.Length / 2; index++)
        {
            long absolute = startSample + index;
            double gain = absolute >= baseEnd
                ? 0
                : absolute <= fadeStart || fadeStart >= baseEnd
                    ? 1
                    : (baseEnd - absolute) / (double)(baseEnd - fadeStart);
            pcm[index * 2] = (short)Math.Clamp(pcm[index * 2] * gain, short.MinValue, short.MaxValue);
            pcm[index * 2 + 1] = (short)Math.Clamp(pcm[index * 2 + 1] * gain, short.MinValue, short.MaxValue);
        }
    }
}

internal sealed class VgmAudioRenderer : IDisposable
{
    private readonly MDSound.MDSound _mds;
    private readonly Dictionary<int, MDSound.ym2612> _ym2612 = [];
    private readonly Dictionary<int, MDSound.ym2151> _ym2151 = [];
    private readonly Dictionary<int, MDSound.sn76489> _sn76489 = [];
    private readonly Dictionary<int, MDSound.ay8910> _ay8910 = [];
    private readonly Dictionary<int, MDSound.ym2203> _ym2203 = [];
    private readonly Dictionary<int, MDSound.ym2610> _ym2610 = [];
    private readonly Dictionary<int, byte[]> _ym2610AdpcmA = [];
    private readonly Dictionary<int, byte[]> _ym2610AdpcmB = [];
    private readonly Dictionary<int, MDSound.emu2413> _ym2413 = [];
    private readonly Dictionary<int, MDSound.ym3526> _ym3526 = [];
    private readonly Dictionary<int, MDSound.ym3812> _ym3812 = [];
    private readonly Dictionary<int, MDSound.y8950> _y8950 = [];
    private readonly Dictionary<int, MDSound.ymf262> _ymf262 = [];
    private readonly Dictionary<int, MDSound.ymf278b> _ymf278b = [];
    private readonly Dictionary<int, MDSound.ymz280b> _ymz280b = [];
    private readonly Dictionary<int, MDSound.segapcm> _segaPcm = [];
    private readonly Dictionary<int, MDSound.rf5c68> _rf5c68 = [];
    private readonly Dictionary<int, MDSound.scd_pcm> _rf5c164 = [];
    private readonly Dictionary<int, MDSound.c140> _c140 = [];
    private readonly Dictionary<int, MDSound.c352> _c352 = [];
    private readonly Dictionary<int, MDSound.K054539> _k054539 = [];
    private readonly Dictionary<int, MDSound.iremga20> _ga20 = [];
    private readonly Dictionary<int, MDSound.okim6258> _okim6258 = [];
    private readonly Dictionary<int, MDSound.okim6295> _okim6295 = [];
    private readonly Dictionary<int, MDSound.multipcm> _multiPcm = [];
    private readonly Dictionary<int, MDSound.gb> _dmg = [];
    private readonly Dictionary<int, MDSound.nes_intf> _nes = [];
    private readonly Dictionary<int, MDSound.Ootake_PSG> _huc6280 = [];
    private readonly Dictionary<int, MDSound.K051649> _k051649 = [];
    private readonly MdsoundFmpChipSink _opna;
    private readonly short[] _buffer = new short[2048];
    private readonly int _sampleRate;
    private readonly ChipType? _channelFilterType;
    private readonly int _channelFilter;
    private int _hucSelectedChannel;
    private int _sn76489LatchedChannel;

    public VgmAudioRenderer(
        IReadOnlyList<DeviceDescriptor> devices,
        int sampleRate,
        IReadOnlyList<VgmSampleAsset> assets = null,
        ChipType? channelFilterType = null,
        int channelFilter = -1)
    {
        _sampleRate = sampleRate;
        _channelFilterType = channelFilterType;
        _channelFilter = channelFilter;
        if (channelFilterType.HasValue && channelFilter < 0)
            throw new ArgumentOutOfRangeException(nameof(channelFilter));
        assets ??= [];
        var chips = new List<MDSound.MDSound.Chip>();
        foreach (DeviceDescriptor device in devices)
        {
            // GA20 requires an external PCM ROM before MDSound can safely
            // initialize its renderer. The timeline remains visualizable and
            // master capture continues for the other devices until the VGM
            // data-block asset path is wired into this backend.
            if (device.Id.Type == ChipType.Ga20
                && !assets.Any(asset => asset.Device == device.Id))
                continue;

            if (device.Id.Type == ChipType.Ym2612)
            {
                var instrument = new MDSound.ym2612();
                _ym2612[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.YM2612,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                });
            }
            else if (device.Id.Type == ChipType.Ym2151)
            {
                var instrument = new MDSound.ym2151();
                _ym2151[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.YM2151,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                });
            }
            else if (device.Id.Type == ChipType.Sn76489)
            {
                var instrument = new MDSound.sn76489();
                _sn76489[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.SN76489,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                });
            }
            else if (device.Id.Type == ChipType.Ay8910)
            {
                var instrument = new MDSound.ay8910();
                _ay8910[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.AY8910,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                });
            }
            else if (device.Id.Type == ChipType.Ym2203)
            {
                var instrument = new MDSound.ym2203();
                _ym2203[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.YM2203,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                });
            }
            else if (device.Id.Type == ChipType.Ym2610)
            {
                var instrument = new MDSound.ym2610();
                _ym2610[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.YM2610,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                });
            }
            else if (device.Id.Type == ChipType.Ym2413)
            {
                var instrument = new MDSound.emu2413();
                _ym2413[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.YM2413,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                });
            }
            else if (device.Id.Type == ChipType.Ym3526)
            {
                var instrument = new MDSound.ym3526();
                _ym3526[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.YM3526,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                });
            }
            else if (device.Id.Type == ChipType.Ym3812)
            {
                var instrument = new MDSound.ym3812();
                _ym3812[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.YM3812,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                });
            }
            else if (device.Id.Type == ChipType.Y8950)
            {
                var instrument = new MDSound.y8950();
                _y8950[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.Y8950,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                });
            }
            else if (device.Id.Type == ChipType.Ymf262)
            {
                var instrument = new MDSound.ymf262();
                _ymf262[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.YMF262,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                });
            }
            else if (device.Id.Type == ChipType.Ymz280b)
            {
                var instrument = new MDSound.ymz280b();
                _ymz280b[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.YMZ280B,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                });
            }
            else if (device.Id.Type == ChipType.Ymf278b)
            {
                var instrument = new MDSound.ymf278b();
                _ymf278b[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.YMF278B,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                    Option = new object[] { AppContext.BaseDirectory },
                });
            }
            else if (device.Id.Type == ChipType.SegaPcm)
            {
                var instrument = new MDSound.segapcm();
                _segaPcm[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.SEGAPCM,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                    Option = new object[] { 0 },
                });
            }
            else if (device.Id.Type == ChipType.Rf5c68)
            {
                var instrument = new MDSound.rf5c68();
                _rf5c68[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.RF5C68,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                });
            }
            else if (device.Id.Type == ChipType.Rf5c164)
            {
                var instrument = new MDSound.scd_pcm();
                _rf5c164[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.RF5C164,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                });
            }
            else if (device.Id.Type == ChipType.C140)
            {
                var instrument = new MDSound.c140();
                _c140[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.C140,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                    Option = new object[] { 2 },
                });
            }
            else if (device.Id.Type == ChipType.C352)
            {
                var instrument = new MDSound.c352();
                _c352[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.C352,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                    Option = new object[] { (byte)0 },
                });
            }
            else if (device.Id.Type == ChipType.K054539)
            {
                var instrument = new MDSound.K054539();
                _k054539[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.K054539,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                });
            }
            else if (device.Id.Type == ChipType.Ga20)
            {
                var instrument = new MDSound.iremga20();
                _ga20[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.GA20,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                });
            }
            else if (device.Id.Type == ChipType.Okim6258)
            {
                var instrument = new MDSound.okim6258();
                _okim6258[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.OKIM6258,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                    Option = new object[] { 0 },
                });
            }
            else if (device.Id.Type == ChipType.Okim6295)
            {
                var instrument = new MDSound.okim6295();
                _okim6295[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.OKIM6295,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                });
            }
            else if (device.Id.Type == ChipType.MultiPcm)
            {
                var instrument = new MDSound.multipcm();
                _multiPcm[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.MultiPCM,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                });
            }
            else if (device.Id.Type == ChipType.Dmg)
            {
                var instrument = new MDSound.gb();
                _dmg[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.DMG,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                });
            }
            else if (device.Id.Type == ChipType.NesApu)
            {
                var instrument = new MDSound.nes_intf();
                _nes[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.Nes,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                });
            }
            else if (device.Id.Type == ChipType.Huc6280)
            {
                var instrument = new MDSound.Ootake_PSG();
                _huc6280[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.HuC6280,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                });
            }
            else if (device.Id.Type == ChipType.K051649)
            {
                var instrument = new MDSound.K051649();
                _k051649[device.Id.Instance] = instrument;
                chips.Add(new MDSound.MDSound.Chip
                {
                    ID = (byte)device.Id.Instance,
                    type = MDSound.MDSound.enmInstrumentType.K051649,
                    Instrument = instrument,
                    Update = instrument.Update,
                    Start = instrument.Start,
                    Stop = instrument.Stop,
                    Reset = instrument.Reset,
                    SamplingRate = (uint)sampleRate,
                    Clock = (uint)Math.Max(1, device.ClockHz),
                    Volume = 0,
                });
            }
        }
        if (devices.Any(device => device.Id.Type == ChipType.Ym2608))
        {
            if (devices.Count(device => device.Id.Type == ChipType.Ym2608) > 1)
                throw new VgmPlaybackException("multiple YM2608 devices are not supported by the portable VGM audio path");
            _opna = new MdsoundFmpChipSink(sampleRate);
            _opna.Start();
        }
        _mds = new MDSound.MDSound((uint)sampleRate, 1024, chips.ToArray());
    }

    public void Write(in TimedChipWrite write)
    {
        if (_channelFilterType.HasValue)
        {
            if (write.Device.Type != _channelFilterType.Value)
                return;
            switch (_channelFilterType.Value)
            {
                case ChipType.Huc6280:
                    WriteFilteredHuc6280(write);
                    break;
                case ChipType.Ym2612:
                    WriteFilteredYm2612(write);
                    break;
                case ChipType.Sn76489:
                    WriteFilteredSn76489(write);
                    break;
            }
            return;
        }

        switch (write.Device.Type)
        {
            case ChipType.Ym2608:
                _opna?.WriteYm2608(write.Device.Instance, write.Port, write.Address, write.Data, write.SamplePosition);
                break;
            case ChipType.Ym2612:
                _mds.WriteYM2612((byte)write.Device.Instance, (byte)write.Port, (byte)write.Address, (byte)write.Data);
                break;
            case ChipType.Ym2151:
                _mds.WriteYM2151((byte)write.Device.Instance, (byte)write.Address, (byte)write.Data);
                break;
            case ChipType.Sn76489:
                _mds.WriteSN76489((byte)write.Device.Instance, (byte)write.Data);
                break;
            case ChipType.Ay8910:
                _mds.WriteAY8910((byte)write.Device.Instance, (byte)write.Address, (byte)write.Data);
                break;
            case ChipType.Ym2203:
                _mds.WriteYM2203((byte)write.Device.Instance, (byte)write.Address, (byte)write.Data);
                break;
            case ChipType.Ym2610:
                _mds.WriteYM2610((byte)write.Device.Instance, (byte)write.Port, (byte)write.Address, (byte)write.Data);
                break;
            case ChipType.Ym2413:
                _mds.WriteYM2413emu((byte)write.Device.Instance, (byte)write.Address, (byte)write.Data);
                break;
            case ChipType.Ym3526:
                _mds.WriteYM3526((byte)write.Device.Instance, (byte)write.Address, (byte)write.Data);
                break;
            case ChipType.Ym3812:
                _mds.WriteYM3812((byte)write.Device.Instance, (byte)write.Address, (byte)write.Data);
                break;
            case ChipType.Y8950:
                _mds.WriteY8950((byte)write.Device.Instance, (byte)write.Address, (byte)write.Data);
                break;
            case ChipType.Ymf262:
                _mds.WriteYMF262((byte)write.Device.Instance, (byte)write.Port, (byte)write.Address, (byte)write.Data);
                break;
            case ChipType.Ymz280b:
                _mds.WriteYMZ280B((byte)write.Device.Instance, (byte)write.Address, (byte)write.Data);
                break;
            case ChipType.Ymf278b:
                _mds.WriteYMF278B((byte)write.Device.Instance, (byte)write.Port, (byte)write.Address, (byte)write.Data);
                break;
            case ChipType.SegaPcm:
                _mds.WriteSEGAPCM((byte)write.Device.Instance, write.Address, (byte)write.Data);
                break;
            case ChipType.Rf5c68:
                _mds.WriteRF5C68((byte)write.Device.Instance, (byte)write.Address, (byte)write.Data);
                break;
            case ChipType.Rf5c164:
                _mds.WriteRF5C164((byte)write.Device.Instance, (byte)write.Address, (byte)write.Data);
                break;
            case ChipType.C140:
                _mds.WriteC140((byte)write.Device.Instance, (uint)write.Address, (byte)write.Data);
                break;
            case ChipType.C352:
                _mds.WriteC352((byte)write.Device.Instance, (uint)write.Address, (uint)write.Data);
                break;
            case ChipType.K054539:
                _mds.WriteK054539((byte)write.Device.Instance, write.Address, (byte)write.Data);
                break;
            case ChipType.Ga20:
                _mds.WriteGA20((byte)write.Device.Instance, (byte)write.Address, (byte)write.Data);
                break;
            case ChipType.Okim6258:
                _mds.WriteOKIM6258((byte)write.Device.Instance, (byte)write.Port, (byte)write.Data);
                break;
            case ChipType.Okim6295:
                _mds.WriteOKIM6295((byte)write.Device.Instance, (byte)write.Port, (byte)write.Data);
                break;
            case ChipType.MultiPcm:
                _mds.WriteMultiPCM((byte)write.Device.Instance, (byte)write.Address, (byte)write.Data);
                break;
            case ChipType.Dmg:
                _mds.WriteDMG((byte)write.Device.Instance, (byte)write.Address, (byte)write.Data);
                break;
            case ChipType.NesApu:
                _mds.WriteNES((byte)write.Device.Instance, (byte)write.Address, (byte)write.Data);
                break;
            case ChipType.Huc6280:
                _mds.WriteHuC6280((byte)write.Device.Instance, (byte)write.Address, (byte)write.Data);
                break;
            case ChipType.K051649:
                _mds.WriteK051649((byte)write.Device.Instance, write.Address, (byte)write.Data);
                break;
        }
    }

    private void WriteFilteredHuc6280(in TimedChipWrite write)
    {
        int selected = Math.Clamp(write.Data & 0x07, 0, 5);
        if (write.Address == 0)
        {
            _hucSelectedChannel = selected;
            if (selected == _channelFilter)
                _mds.WriteHuC6280((byte)write.Device.Instance, 0, (byte)_channelFilter);
            return;
        }

        // Register 1 is global balance. Select the target voice explicitly
        // because writes to other voices are intentionally not forwarded.
        if (write.Address == 1)
        {
            _mds.WriteHuC6280((byte)write.Device.Instance, 0, (byte)_channelFilter);
            _mds.WriteHuC6280((byte)write.Device.Instance, (byte)write.Address, (byte)write.Data);
            return;
        }

        if (_hucSelectedChannel == _channelFilter)
            _mds.WriteHuC6280((byte)write.Device.Instance, (byte)write.Address, (byte)write.Data);
    }

    private void WriteFilteredYm2612(in TimedChipWrite write)
    {
        // YM2612 DAC samples replace channel 6 on the Mega Drive. Keep them
        // in the FM6/DAC stem, but do not leak the DAC stream into FM1-FM5.
        if (write.Address == 0x2A)
        {
            if (_channelFilter == 5)
                _mds.WriteYM2612((byte)write.Device.Instance, (byte)write.Port,
                    (byte)write.Address, (byte)write.Data);
            return;
        }

        if (write.Address == 0x2B)
        {
            if (_channelFilter == 5)
                _mds.WriteYM2612((byte)write.Device.Instance, (byte)write.Port,
                    (byte)write.Address, (byte)write.Data);
            return;
        }

        int channel = Ym2612Channel(write.Port, write.Address, write.Data);
        if (channel < 0 || channel == _channelFilter || IsYm2612Global(write.Address))
            _mds.WriteYM2612((byte)write.Device.Instance, (byte)write.Port,
                (byte)write.Address, (byte)write.Data);
    }

    private void WriteFilteredSn76489(in TimedChipWrite write)
    {
        int data = write.Data & 0xFF;
        if ((data & 0x80) != 0)
            _sn76489LatchedChannel = (data >> 5) & 0x03;

        if (_sn76489LatchedChannel == _channelFilter)
            _mds.WriteSN76489((byte)write.Device.Instance, (byte)data);
    }

    private static int Ym2612Channel(int port, int address, int data)
    {
        if (address == 0x28)
        {
            int local = data & 0x03;
            int selectedPort = (data >> 2) & 0x01;
            return local == 3 ? -1 : selectedPort * 3 + local;
        }
        if (address is >= 0xA0 and <= 0xA2)
            return port * 3 + address - 0xA0;
        if (address is >= 0xA4 and <= 0xA6)
            return port * 3 + address - 0xA4;
        if (address is >= 0xB0 and <= 0xB2)
            return port * 3 + address - 0xB0;
        if (address is >= 0x30 and <= 0x9F)
        {
            int local = address & 0x03;
            return local <= 2 ? port * 3 + local : -1;
        }
        if (port == 0 && address is >= 0xA8 and <= 0xAE)
            return 2;
        return -1;
    }

    private static bool IsYm2612Global(int address) =>
        address is 0x22 or 0x24 or 0x25 or 0x26 or 0x27;

    public void LoadAsset(VgmSampleAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        uint length = checked((uint)asset.Data.Length);
        byte chip = (byte)asset.Device.Instance;
        switch (asset.Device.Type)
        {
            case ChipType.SegaPcm:
                _mds.WriteSEGAPCMPCMData(chip, asset.RomSize, asset.StartAddress, length, asset.Data, 0);
                break;
            case ChipType.Rf5c68:
                _mds.WriteRF5C68PCMData(chip, asset.StartAddress, length, asset.Data, 0);
                break;
            case ChipType.Rf5c164:
                _mds.WriteRF5C164PCMData(chip, asset.StartAddress, length, asset.Data, 0);
                break;
            case ChipType.Ym2608:
                _opna?.LoadYm2608AdpcmData(asset.StartAddress, asset.Data);
                break;
            case ChipType.Ym2610:
                LoadYm2610Adpcm(asset);
                break;
            case ChipType.Ymf278b:
                if (asset.Kind == AssetKind.SampleBank)
                    _mds.WriteYMF278BPCMRAMData(chip, asset.RomSize, asset.StartAddress, length, asset.Data, 0);
                else
                    _mds.WriteYMF278BPCMData(chip, asset.RomSize, asset.StartAddress, length, asset.Data, 0);
                break;
            case ChipType.Ymz280b:
                _mds.WriteYMZ280BPCMData(chip, asset.RomSize, asset.StartAddress, length, asset.Data, 0);
                break;
            case ChipType.Y8950:
                _mds.WriteY8950PCMData(chip, asset.RomSize, asset.StartAddress, length, asset.Data, 0);
                break;
            case ChipType.MultiPcm:
                _mds.WriteMultiPCMPCMData(chip, asset.RomSize, asset.StartAddress, length, asset.Data, 0);
                break;
            case ChipType.Okim6295:
                _mds.WriteOKIM6295PCMData(chip, asset.RomSize, asset.StartAddress, length, asset.Data, 0);
                break;
            case ChipType.K054539:
                _mds.WriteK054539PCMData(chip, asset.RomSize, asset.StartAddress, length, asset.Data, 0);
                break;
            case ChipType.C140:
                _mds.WriteC140PCMData(chip, asset.RomSize, asset.StartAddress, length, asset.Data, 0);
                break;
            case ChipType.C352:
                _mds.WriteC352PCMData(chip, asset.RomSize, asset.StartAddress, length, asset.Data, 0);
                break;
            case ChipType.Ga20:
                _mds.WriteGA20PCMData(chip, asset.RomSize, asset.StartAddress, length, asset.Data, 0);
                break;
        }
    }

    private void LoadYm2610Adpcm(VgmSampleAsset asset)
    {
        if (!_ym2610.ContainsKey(asset.Device.Instance))
            return;

        Dictionary<int, byte[]> banks = asset.Kind == AssetKind.AdpcmA
            ? _ym2610AdpcmA
            : _ym2610AdpcmB;
        int requiredLength = checked((int)Math.Max(
            asset.RomSize,
            checked(asset.StartAddress + (uint)asset.Data.Length)));
        if (!banks.TryGetValue(asset.Device.Instance, out byte[] bank)
            || bank.Length != requiredLength)
        {
            bank = new byte[requiredLength];
            banks[asset.Device.Instance] = bank;
        }

        asset.Data.CopyTo(bank.AsSpan(checked((int)asset.StartAddress)));
        if (asset.Kind == AssetKind.AdpcmA)
            _mds.WriteYM2610_SetAdpcmA((byte)asset.Device.Instance, bank);
        else
            _mds.WriteYM2610_SetAdpcmB((byte)asset.Device.Instance, bank);
    }

    public short[] Render(int samples)
    {
        if (samples <= 0)
            return Array.Empty<short>();
        int count = checked(samples * 2);
        short[] output = count <= _buffer.Length ? _buffer : new short[count];
        _mds.Update(output, 0, count, null);
        if (_opna != null)
        {
            int frameCount = count / 2;
            int[][] opna = [new int[frameCount], new int[frameCount]];
            _opna.Render(opna, frameCount);
            for (int index = 0; index < frameCount; index++)
            {
                output[index * 2] = (short)Math.Clamp(output[index * 2] + opna[0][index], short.MinValue, short.MaxValue);
                output[index * 2 + 1] = (short)Math.Clamp(output[index * 2 + 1] + opna[1][index], short.MinValue, short.MaxValue);
            }
        }
        return output.AsSpan(0, count).ToArray();
    }

    public void Dispose()
    {
        foreach (KeyValuePair<int, MDSound.ym2612> entry in _ym2612)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.ym2151> entry in _ym2151)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.sn76489> entry in _sn76489)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.ay8910> entry in _ay8910)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.ym2203> entry in _ym2203)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.ym2610> entry in _ym2610)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.emu2413> entry in _ym2413)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.ym3526> entry in _ym3526)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.ym3812> entry in _ym3812)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.y8950> entry in _y8950)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.ymf262> entry in _ymf262)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.ymz280b> entry in _ymz280b)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.ymf278b> entry in _ymf278b)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.segapcm> entry in _segaPcm)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.rf5c68> entry in _rf5c68)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.scd_pcm> entry in _rf5c164)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.c140> entry in _c140)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.c352> entry in _c352)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.K054539> entry in _k054539)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.iremga20> entry in _ga20)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.okim6258> entry in _okim6258)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.okim6295> entry in _okim6295)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.multipcm> entry in _multiPcm)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.gb> entry in _dmg)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.nes_intf> entry in _nes)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.Ootake_PSG> entry in _huc6280)
            entry.Value.Stop((byte)entry.Key);
        foreach (KeyValuePair<int, MDSound.K051649> entry in _k051649)
            entry.Value.Stop((byte)entry.Key);
        _opna?.Dispose();
    }
}

internal static class VgmInput
{
    public static byte[] Read(string path)
    {
        using FileStream input = File.OpenRead(path);
        if (input.Length < 2)
            return ReadAll(input);
        int first = input.ReadByte();
        int second = input.ReadByte();
        input.Position = 0;
        if (first != 0x1F || second != 0x8B)
            return ReadAll(input);

        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] ReadAll(Stream input)
    {
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}

internal sealed class VgmPlaybackException : Exception
{
    public VgmPlaybackException(string message) : base(message) { }
}
