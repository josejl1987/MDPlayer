using System.Buffers.Binary;
using Fmp.Cli;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class XgmPlaybackBackendTests
{
    [Fact]
    public void Capture_DecodesYm2612AndPsgFrameCommands()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-xgm-{Guid.NewGuid():N}.xgm");
        string wav = path + ".wav";
        try
        {
            File.WriteAllBytes(path, CreateXgm(
                0x20, 0xA0, 0x35,
                0x20, 0xA4, 0x21,
                0x40, 0xF0,
                0x10, 0x90,
                0x10, 0x81,
                0x00,
                0x40, 0x00,
                0x00,
                0x7F));

            var sink = new TimelineDecoderEventSink(44_100);
            using IPlaybackCaptureSession session = new XgmPlaybackBackend().Open(
                new FileInfo(path),
                new PlaybackOptions(LoopCount: 1, FadeSeconds: 0, TailSeconds: 0, OutputAudioPath: wav),
                sink);
            session.Run();
            VisualizationTimeline timeline = sink.Complete(session.SamplePosition, "test");

            Assert.True(session.IsComplete);
            Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Ym2612);
            Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Sn76489);
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
    public void Parse_ExposesEmbeddedPcmOperationWithoutTreatingItAsANote()
    {
        XgmDocument document = XgmDocument.Parse(CreateXgmWithSample(
            0x20, 0x2B, 0x80,
            0x50, 0x01,
            0x00,
            0x7F));

        Assert.Contains(
            document.Frames.SelectMany(frame => frame.Operations),
            operation => operation is XgmPcmOperation { Channel: 0, SampleId: 1 });
        Assert.Equal(256, document.GetSample(1).Length);
    }

    [Fact]
    public void Capture_EmitsStableEmbeddedSampleAssetEvents()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-xgm-asset-{Guid.NewGuid():N}.xgm");
        try
        {
            File.WriteAllBytes(path, CreateXgmWithSample(
                0x50, 0x01,
                0x00,
                0x7F));
            var sink = new AssetRecordingSink();
            using IPlaybackCaptureSession session = new XgmPlaybackBackend().Open(
                new FileInfo(path),
                new PlaybackOptions(LoopCount: 1, FadeSeconds: 0, TailSeconds: 0),
                sink);
            session.Run();

            TimedSampleAssetEvent asset = Assert.Single(sink.Assets);
            Assert.Equal("xgm:sample:1", asset.AssetId);
            Assert.Equal(AssetKind.Pcm, asset.Kind);
            Assert.Equal(256, asset.SizeBytes);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static byte[] CreateXgm(params byte[] music)
    {
        byte[] data = new byte[0x108 + music.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x00, 4), 0x204D4758);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x100, 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x104, 4), (uint)music.Length);
        music.CopyTo(data, 0x108);
        return data;
    }

    private static byte[] CreateXgmWithSample(params byte[] music)
    {
        byte[] data = new byte[0x108 + 0x100 + 4 + music.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x00, 4), 0x204D4758);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x06, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x100, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x04, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x06, 2), 1);
        int musicHeader = 0x104 + 0x100;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(musicHeader, 4), (uint)music.Length);
        for (int index = 0; index < 0x100; index++)
            data[0x104 + index] = (byte)(index - 128);
        music.CopyTo(data, musicHeader + 4);
        return data;
    }

    private sealed class AssetRecordingSink : IPlaybackEventSink
    {
        public List<TimedSampleAssetEvent> Assets { get; } = [];

        public void OnDevice(in DeviceDescriptor device) { }
        public void OnChipWrite(in TimedChipWrite write) { }
        public void OnMidi(in TimedMidiMessage message) { }
        public void OnSampleAsset(in TimedSampleAssetEvent asset) => Assets.Add(asset);
        public void OnLoopBoundary(in TimedLoopBoundary loop) { }
    }
}
