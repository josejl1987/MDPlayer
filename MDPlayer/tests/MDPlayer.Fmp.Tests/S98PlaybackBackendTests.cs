using System.Buffers.Binary;
using System.IO.Compression;
using Fmp.Cli;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class S98PlaybackBackendTests
{
    [Fact]
    public void Parse_MapsDeviceSlotsAndTimerSyncs()
    {
        S98Document document = S98Document.Parse(CreateS98(
            0x00, 0xA0, 0x35,
            0x00, 0xA4, 0x21,
            0x00, 0x28, 0xF0,
            0x02, 0x00, 0x80,
            0x02, 0x00, 0x01,
            0x02, 0x00, 0x90,
            0xFF,
            0x00, 0x28, 0x00,
            0xFD));

        Assert.Equal(2, document.Devices.Count);
        Assert.Equal(ChipType.Ym2612, document.Devices[0].Id.Type);
        Assert.Equal(ChipType.Sn76489, document.Devices[1].Id.Type);
        Assert.Equal(7, document.Writes.Count);
        Assert.Equal(1, document.EndSync);
        Assert.Equal(441, document.ToSamples(document.EndSync, 44_100));
    }

    [Fact]
    public void Parse_AcceptsLegacyV0AndV2Headers()
    {
        S98Document v0 = S98Document.Parse(CreateLegacyS98(
            (byte)'0',
            0x00, 0xA0, 0x35,
            0xFF,
            0xFD));
        Assert.Single(v0.Devices);
        Assert.Equal(ChipType.Ym2608, v0.Devices[0].Id.Type);

        S98Document v2 = S98Document.Parse(CreateLegacyS98(
            (byte)'2',
            0x00, 0xA0, 0x35,
            0xFF,
            0xFD,
            devices: [(4, 7_987_200u)]));
        Assert.Single(v2.Devices);
        Assert.Equal(ChipType.Ym2608, v2.Devices[0].Id.Type);
        Assert.Single(v2.Writes);
    }

    [Fact]
    public void Parse_InflatesCompressedDumpData()
    {
        S98Document document = S98Document.Parse(CreateCompressedS98(
            0x00, 0xA0, 0x35,
            0xFF,
            0xFD));

        Assert.Single(document.Writes);
        Assert.Equal(ChipType.Ym2612, document.Writes[0].Device.Type);
    }

    [Fact]
    public void Capture_ProducesTimelineAndMasterWav()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-s98-{Guid.NewGuid():N}.s98");
        string wav = path + ".wav";
        try
        {
            File.WriteAllBytes(path, CreateS98(
                0x00, 0xA0, 0x35,
                0x00, 0xA4, 0x21,
                0x00, 0x28, 0xF0,
                0x02, 0x00, 0x80,
                0x02, 0x00, 0x01,
                0x02, 0x00, 0x90,
                0xFF, 0xFF,
                0x00, 0x28, 0x00,
                0xFD));

            var sink = new TimelineDecoderEventSink(44_100);
            using IPlaybackCaptureSession session = new S98PlaybackBackend().Open(
                new FileInfo(path),
                new PlaybackOptions(LoopCount: 1, FadeSeconds: 0, TailSeconds: 0, OutputAudioPath: wav),
                sink);
            session.Run();
            VisualizationTimeline timeline = sink.Complete(session.SamplePosition, "test");

            Assert.True(session.IsComplete);
            Assert.Contains(timeline.Notes, note => note.ChannelId == "ym2612.0.fm.1");
            Assert.Contains(timeline.Notes, note => note.ChannelId == "sn76489.0.psg.1");
            Assert.True(new FileInfo(wav).Length > 44);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(wav)) File.Delete(wav);
            if (File.Exists(wav + ".tmp")) File.Delete(wav + ".tmp");
        }
    }

    [Fact]
    public void Capture_DecodesYm3812OplDevice()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-s98-ym3812-{Guid.NewGuid():N}.s98");
        string wav = path + ".wav";
        try
        {
            File.WriteAllBytes(path, CreateS98WithDevices(
                [(8, 3_579_545u)],
                0x00, 0xA0, 0x35,
                0x00, 0xB0, 0x31,
                0xFF,
                0x00, 0xB0, 0x11,
                0xFD));

            var sink = new TimelineDecoderEventSink(44_100);
            using IPlaybackCaptureSession session = new S98PlaybackBackend().Open(
                new FileInfo(path),
                new PlaybackOptions(LoopCount: 1, FadeSeconds: 0, TailSeconds: 0, OutputAudioPath: wav),
                sink);
            session.Run();
            VisualizationTimeline timeline = sink.Complete(session.SamplePosition, "test");

            Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Ym3812);
            Assert.Contains(timeline.Notes, note => note.ChannelId == "ym3812.0.fm.1");
            Assert.True(new FileInfo(wav).Length > 44);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(wav)) File.Delete(wav);
            if (File.Exists(wav + ".tmp")) File.Delete(wav + ".tmp");
        }
    }

    [Fact]
    public void Probe_AllowsSupportedDeviceWhenAnotherDeclaredDeviceIsUnsupported()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-s98-mixed-{Guid.NewGuid():N}.s98");
        try
        {
            File.WriteAllBytes(path, CreateS98WithDevices(
                [(3, 7_670_454u), (999, 1u)],
                0x00, 0xA0, 0x35,
                0x00, 0xA4, 0x21,
                0x00, 0x28, 0xF0,
                0xFF,
                0x00, 0x28, 0x00,
                0xFD));

            PlaybackProbeResult probe = new S98PlaybackBackend().Probe(
                new FileInfo(path),
                new PlaybackEnvironment([Path.GetDirectoryName(path)!]));

            Assert.True(probe.Supported);
            Assert.True(probe.Visualizable);
            Assert.Contains(probe.Warnings, warning => warning.Contains("unknown.0", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CliVisualize_DispatchesS98ThroughBackendRegistry()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-s98-cli-{Guid.NewGuid():N}.s98");
        string output = Path.Combine(Path.GetTempPath(), $"mdplayer-s98-output-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(path, CreateS98(
                0x00, 0xA0, 0x35,
                0x00, 0xA4, 0x21,
                0x00, 0x28, 0xF0,
                0xFF,
                0x00, 0x28, 0x00,
                0xFD));

            int exitCode = VisualizeCommand.Handle(
            [path, "--output", output, "--loops", "1", "--fade", "0", "--tail", "0", "--stems-only", "--overwrite", "--quiet"]);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(output, "timeline.json")));
            Assert.True(new FileInfo(Path.Combine(output, "audio", "master.wav")).Length > 44);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    private static byte[] CreateS98(params byte[] commands)
        => CreateS98WithDevices([(3, 7_670_454u), (16, 3_579_545u)], commands);

    private static byte[] CreateLegacyS98(
        byte version,
        byte unused0,
        byte unused1,
        byte unused2,
        byte unused3,
        byte unused4,
        (int Type, uint Clock)[] devices = null)
    {
        devices ??= [];
        int dataOffset = version == (byte)'2'
            ? 0x20 + (devices.Length + 1) * 0x10
            : 0x20;
        byte[] commands = [unused0, unused1, unused2, unused3, unused4];
        byte[] data = new byte[dataOffset + commands.Length];
        data[0] = (byte)'S';
        data[1] = (byte)'9';
        data[2] = (byte)'8';
        data[3] = version;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x04, 4), 10);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x08, 4), 1000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x14, 4), (uint)dataOffset);
        if (version == (byte)'2')
        {
            for (int index = 0; index < devices.Length; index++)
            {
                int offset = 0x20 + index * 0x10;
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), (uint)devices[index].Type);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 4, 4), devices[index].Clock);
            }
        }
        commands.CopyTo(data, dataOffset);
        return data;
    }

    private static byte[] CreateCompressedS98(params byte[] commands)
    {
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var deflater = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
                deflater.Write(commands);
            compressed = output.ToArray();
        }

        const int dataOffset = 0x30;
        byte[] data = new byte[dataOffset + compressed.Length];
        data[0] = (byte)'S';
        data[1] = (byte)'9';
        data[2] = (byte)'8';
        data[3] = (byte)'2';
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x04, 4), 10);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x08, 4), 1000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x14, 4), dataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x1C, 4), dataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x20, 4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x24, 4), 7_670_454);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x0C, 4), (uint)compressed.Length);
        compressed.CopyTo(data, dataOffset);
        return data;
    }

    private static byte[] CreateS98WithDevices(
        (int Type, uint Clock)[] devices,
        params byte[] commands)
    {
        int dataOffset = 0x20 + devices.Length * 0x10;
        byte[] data = new byte[dataOffset + commands.Length];
        data[0] = (byte)'S';
        data[1] = (byte)'9';
        data[2] = (byte)'8';
        data[3] = (byte)'3';
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x04, 4), 10);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x08, 4), 1000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x14, 4), (uint)dataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x1C, 4), (uint)devices.Length);
        for (int index = 0; index < devices.Length; index++)
        {
            int offset = 0x20 + index * 0x10;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), (uint)devices[index].Type);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 4, 4), devices[index].Clock);
        }
        commands.CopyTo(data, dataOffset);
        return data;
    }
}
