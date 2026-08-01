using System.Text.Json;
using Fmp.Core.Decoding.SnesDsp;
using Fmp.Core.Playback.Spc;
using Xunit;

namespace Fmp.Core.Tests.Decoding.SnesDsp;

public class BrrSampleReaderTests
{
    private const int BlockSize = BrrSampleReader.BlockSize;

    private static byte[] MakeRam() => new byte[0x10000];

    private static void WriteBlock(byte[] ram, int address, bool end, bool loop)
    {
        ram[address] = (byte)((end ? 0x01 : 0) | (loop ? 0x02 : 0));
        for (int i = 1; i < BlockSize; i++) ram[address + i] = (byte)(i * 0x11);
    }

    private static BrrSample ReadLongChain()
    {
        byte[] ram = MakeRam();
        for (int i = 0; i < BrrSampleReader.MaxBlocks + 100; i++)
            WriteBlock(ram, i * BlockSize, end: false, loop: false);
        return BrrSampleReader.Read(ram, 0, 0);
    }

    [Fact]
    public void OneShot_EndFlag_Terminates()
    {
        byte[] ram = MakeRam();
        WriteBlock(ram, 0x0100, end: false, loop: false);
        WriteBlock(ram, 0x0109, end: true, loop: false);
        BrrSample sample = BrrSampleReader.Read(ram, 0x0100, 0x0109);
        Assert.True(sample.Valid);
        Assert.False(sample.Loops);
        Assert.Equal(2 * BlockSize, sample.EncodedBlocks.Length);
        Assert.Equal(64, sample.Hash.Length);
    }

    [Fact]
    public void Looping_LoopFlag_RecordsLoops()
    {
        byte[] ram = MakeRam();
        WriteBlock(ram, 0x0100, end: false, loop: false);
        WriteBlock(ram, 0x0109, end: true, loop: true);
        BrrSample sample = BrrSampleReader.Read(ram, 0x0100, 0x0109);
        Assert.True(sample.Valid);
        Assert.True(sample.Loops);
        Assert.Equal(2 * BlockSize, sample.EncodedBlocks.Length);
    }

    [Fact]
    public void RamWrap_Handled()
    {
        byte[] ram = MakeRam();
        for (int i = 0; i < BlockSize; i++)
            ram[(0xFFFE + i) & 0xFFFF] = (byte)(i == 0 ? 0x01 : i * 0x11);
        BrrSample sample = BrrSampleReader.Read(ram, 0xFFFE, 0x0109);
        Assert.True(sample.Valid);
        Assert.Equal(BlockSize, sample.EncodedBlocks.Length);
        Assert.Equal(0x01, sample.EncodedBlocks[0]);
        Assert.Equal(ram[0xFFFF], sample.EncodedBlocks[1]);
        Assert.Equal(ram[0x0000], sample.EncodedBlocks[2]);
    }

    [Fact]
    public void InvalidLoopAddress_TerminatesSafely()
    {
        byte[] ram = MakeRam();
        WriteBlock(ram, 0x0100, end: false, loop: false);
        WriteBlock(ram, 0x0109, end: true, loop: true);
        BrrSample sample = BrrSampleReader.Read(ram, 0x0100, 0xFFFF);
        Assert.True(sample.Loops); // loop address is never followed
    }

    [Fact]
    public void LongChainWithoutEndFlag_StopsBounded()
    {
        BrrSample sample = ReadLongChain();
        Assert.False(sample.Valid);
        Assert.Equal(BrrSampleReader.MaxBlocks, sample.EncodedBlocks.Length / BlockSize);
        Assert.True(sample.EncodedBlocks.Length <= BrrSampleReader.MaxBytes);
    }

    [Fact]
    public void RepeatedBlockCycle_Stops()
    {
        byte[] ram = MakeRam();
        WriteBlock(ram, 0x0100, end: false, loop: false);
        WriteBlock(ram, 0x0109, end: true, loop: true);
        BrrSample sample = BrrSampleReader.Read(ram, 0x0100, 0x0100);
        Assert.True(sample.Valid);
        Assert.True(sample.Loops);
        Assert.Equal(2 * BlockSize, sample.EncodedBlocks.Length);
    }

    [Fact]
    public void SameBytesAtDifferentAddresses_HaveSameHash()
    {
        byte[] ram = MakeRam();
        WriteBlock(ram, 0x0100, end: false, loop: false);
        WriteBlock(ram, 0x0109, end: true, loop: true);
        WriteBlock(ram, 0x4000, end: false, loop: false);
        WriteBlock(ram, 0x4009, end: true, loop: true);
        BrrSample a = BrrSampleReader.Read(ram, 0x0100, 0x0109);
        BrrSample b = BrrSampleReader.Read(ram, 0x4000, 0x4009);
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(a.ShortHash, b.ShortHash);
    }

    [Fact]
    public void MalformedChains_NeverThrow()
    {
        Assert.False(BrrSampleReader.Read(ReadOnlySpan<byte>.Empty, 0, 0).Valid);
        var shortRam = new byte[16];
        shortRam[0] = 0x01;
        Assert.False(BrrSampleReader.Read(shortRam, 0x000A, 0).Valid);
    }

    [Fact]
    public void Builder_SameSampleDifferentAdsr_DifferentIdsSameHash()
    {
        var builder = new SpcInstrumentBuilder();
        SpcInstrumentDefinition a = builder.Resolve(BuildSnapshot(adsr1: 0x01), 0);
        SpcInstrumentDefinition b = builder.Resolve(BuildSnapshot(adsr1: 0x81), 0);
        Assert.Equal(a.SampleHash, b.SampleHash);
        Assert.NotEqual(a.Id, b.Id);
        Assert.StartsWith("spc:src3:", a.Id);
        Assert.StartsWith("SRC 3 · ", a.DisplayName);
        Assert.EndsWith(a.SampleShortHash.ToUpperInvariant(), a.DisplayName);
    }

    [Fact]
    public void Builder_SameKey_ReusesInstrument()
    {
        SpcSnapshot snapshot = BuildSnapshot();
        var builder = new SpcInstrumentBuilder();
        Assert.Same(builder.Resolve(snapshot, 0), builder.Resolve(snapshot, 0));
        Assert.Single(builder.Instruments);
    }

    [Fact]
    public void Builder_NoiseFlag_ChangesInstrument()
    {
        var builder = new SpcInstrumentBuilder();
        SpcInstrumentDefinition quiet = builder.Resolve(BuildSnapshot(noise: false), 0);
        SpcInstrumentDefinition noisy = builder.Resolve(BuildSnapshot(noise: true), 0);
        Assert.Equal(quiet.SampleHash, noisy.SampleHash);
        Assert.NotEqual(quiet.Id, noisy.Id);
    }

    [Fact]
    public void SamplesJsonWriter_SerializesSchema_OrderedByHash()
    {
        var builder = new SpcInstrumentBuilder();
        builder.Resolve(BuildSnapshot(voice: 0, srcn: 3), 0);
        builder.Resolve(BuildSnapshot(voice: 1, srcn: 7, adsr1: 0xF1), 1); // different envelope, same sample
        using JsonDocument doc = JsonDocument.Parse(SpcSamplesJsonWriter.Serialize(builder.Samples));
        JsonElement entry = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal(64, entry.GetProperty("hash").GetString().Length);
        Assert.Equal(new[] { 3, 7 }, entry.GetProperty("sourceNumbers").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.Equal(0x0100, entry.GetProperty("startAddress").GetInt32());
        Assert.Equal(0x0109, entry.GetProperty("loopAddress").GetInt32());
        Assert.True(entry.GetProperty("loops").GetBoolean());
        Assert.Equal(JsonValueKind.String, entry.GetProperty("encodedBytes").ValueKind);
        Assert.Equal("relative", entry.GetProperty("pitchAccuracy").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("estimatedRootHz").ValueKind);
    }

    private static SpcSnapshot BuildSnapshot(
        int voice = 0, byte srcn = 3, byte adsr1 = 0x01, byte adsr2 = 0x02,
        byte gain = 0xE0, bool noise = false)
    {
        byte[] ram = MakeRam();
        WriteBlock(ram, 0x0100, end: false, loop: false);
        WriteBlock(ram, 0x0109, end: true, loop: true);
        int dir = 0x0200; // DIR register 0x02 -> directory at 0x0200
        ram[dir + 4 * srcn + 0] = 0x00; // start 0x0100
        ram[dir + 4 * srcn + 1] = 0x01;
        ram[dir + 4 * srcn + 2] = 0x09; // loop 0x0109
        ram[dir + 4 * srcn + 3] = 0x01;
        var dsp = new byte[0x80];
        dsp[0x5D] = 0x02; // DIR
        dsp[0x3D] = (byte)(noise ? 1 << voice : 0); // NON
        dsp[0x10 * voice + 0x04] = srcn;
        dsp[0x10 * voice + 0x05] = adsr1;
        dsp[0x10 * voice + 0x06] = adsr2;
        dsp[0x10 * voice + 0x07] = gain;
        return new SpcSnapshot(
            new SpcMetadata(),
            new SpcCpuRegisters(0, 0, 0, 0, 0, 0),
            ram,
            dsp,
            Array.Empty<string>());
    }
}
