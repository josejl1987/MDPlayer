using Fmp.Cli;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class MdxPlaybackBackendTests
{
    [Fact]
    public void ProbeAndCapture_DecodeNativeMdxVoiceAndNoteCommands()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-mdx-{Guid.NewGuid():N}.mdx");
        string wav = path + ".wav";
        try
        {
            File.WriteAllBytes(path, CreateMdx());
            var backend = new MdxPlaybackBackend();
            PlaybackProbeResult probe = backend.Probe(
                new FileInfo(path),
                new PlaybackEnvironment([Path.GetDirectoryName(path)!]));

            Assert.True(probe.Supported);
            Assert.True(probe.Visualizable);
            Assert.True(probe.Portable);
            Assert.Equal(PlaybackAvailability.Available, probe.Availability);

            var sink = new TimelineDecoderEventSink(44_100);
            using IPlaybackCaptureSession session = backend.Open(
                new FileInfo(path),
                new PlaybackOptions(LoopCount: 1, FadeSeconds: 0, TailSeconds: 0, OutputAudioPath: wav),
                sink);
            session.Run();
            VisualizationTimeline timeline = sink.Complete(session.SamplePosition, "test");

            Assert.True(session.IsComplete);
            Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Ym2151);
            Assert.Contains(timeline.Notes, note => note.ChannelId == "ym2151.0.fm.1");
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
    public void CliVisualize_DispatchesMdxThroughGenericBackend()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-mdx-cli-{Guid.NewGuid():N}.mdx");
        string output = Path.Combine(Path.GetTempPath(), $"mdplayer-mdx-output-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(path, CreateMdx());
            int exitCode = VisualizeCommand.Handle(
            [
                path,
                "--output", output,
                "--loops", "1",
                "--fade", "0",
                "--tail", "0",
                "--stems-only",
                "--overwrite",
                "--quiet",
            ]);

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

    [Fact]
    public void ProbeAndCapture_ExpandsDeterministicRepeatCommands()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-mdx-repeat-{Guid.NewGuid():N}.mdx");
        try
        {
            File.WriteAllBytes(path, CreateMdx(
                0xFD, 0x01,
                0xF6, 0x02, 0x00,
                0x90, 0x00,
                0xF5, 0xFF, 0xFB,
                0xF1, 0x00));

            var backend = new MdxPlaybackBackend();
            PlaybackProbeResult probe = backend.Probe(
                new FileInfo(path),
                new PlaybackEnvironment([Path.GetDirectoryName(path)!]));

            Assert.True(probe.Supported);
            var sink = new TimelineDecoderEventSink(44_100);
            using IPlaybackCaptureSession session = backend.Open(
                new FileInfo(path),
                new PlaybackOptions(LoopCount: 1, FadeSeconds: 0, TailSeconds: 0),
                sink);
            session.Run();
            VisualizationTimeline timeline = sink.Complete(session.SamplePosition, "test");

            Assert.Equal(2, timeline.Notes.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Capture_ExpandsFinitePerformanceLoopUsingLoopCount()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-mdx-performance-loop-{Guid.NewGuid():N}.mdx");
        try
        {
            File.WriteAllBytes(path, CreateMdx(
                0xFD, 0x01,
                0x90, 0x00,
                0xF1, 0xFF, 0xFB));

            var sink = new TimelineDecoderEventSink(44_100);
            using IPlaybackCaptureSession session = new MdxPlaybackBackend().Open(
                new FileInfo(path),
                new PlaybackOptions(LoopCount: 2, FadeSeconds: 0, TailSeconds: 0),
                sink);
            session.Run();
            VisualizationTimeline timeline = sink.Complete(session.SamplePosition, "test");

            Assert.Equal(2, timeline.Notes.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static byte[] CreateMdx() => CreateMdx(
        0xFD, 0x01, 0x90, 0x10, 0xF1, 0x00, 0x00);

    private static byte[] CreateMdx(params byte[] track)
    {
        byte[] titleAndPdx =
        [
            (byte)'T', (byte)'e', (byte)'s', (byte)'t',
            0x0D, 0x0A, 0x1A, 0x00,
        ];
        const int offsetStart = 8;
        const int voiceOffset = 0x30;
        const int trackOffset = 0x14;
        const int voiceLength = 27;
        byte[] data = new byte[offsetStart + voiceOffset + Math.Max(voiceLength, track.Length)];
        titleAndPdx.CopyTo(data, 0);

        WriteWord(data, offsetStart + 0, voiceOffset);
        WriteWord(data, offsetStart + 2, trackOffset);
        for (int index = 2; index < 10; index++)
            WriteWord(data, offsetStart + index * 2, 0);
        track.CopyTo(data, offsetStart + trackOffset);

        int voiceStart = offsetStart + voiceOffset;
        data[voiceStart] = 0x01;
        data[voiceStart + 1] = 0xC0;
        data[voiceStart + 2] = 0x0F;
        for (int operatorIndex = 0; operatorIndex < 4; operatorIndex++)
        {
            data[voiceStart + 3 + operatorIndex] = 0x01;
            data[voiceStart + 7 + operatorIndex] = 0x20;
            data[voiceStart + 11 + operatorIndex] = 0x1F;
            data[voiceStart + 15 + operatorIndex] = 0x00;
            data[voiceStart + 19 + operatorIndex] = 0x00;
            data[voiceStart + 23 + operatorIndex] = 0x0F;
        }
        return data;
    }

    private static void WriteWord(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }
}
