using System.Buffers.Binary;
using Fmp.Cli;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class MidiPlaybackBackendTests
{
    [Fact]
    public void Capture_PreservesPolyphonySustainAndPitchBend()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-midi-{Guid.NewGuid():N}.mid");
        string wav = path + ".wav";
        try
        {
            File.WriteAllBytes(path, CreateMidi());
            var backend = new MidiPlaybackBackend();
            var sink = new TimelineDecoderEventSink(44_100);
            using IPlaybackCaptureSession session = backend.Open(
                new FileInfo(path),
                new PlaybackOptions(LoopCount: 1, FadeSeconds: 0, TailSeconds: 0, OutputAudioPath: wav),
                sink);

            session.Run();
            VisualizationTimeline timeline = sink.Complete(session.SamplePosition, "test");

            Assert.True(session.IsComplete);
            Assert.Single(timeline.Devices);
            Assert.Equal(16, timeline.Voices.Count);
            Assert.Equal(2, timeline.Notes.Count);
            Assert.All(timeline.Notes, note => Assert.Equal("midi.0.channel.1", note.ChannelId));
            Assert.Contains(timeline.Notes, note => note.Pitch.Count == 1);
            Assert.True(timeline.Notes.Max(note => note.EndSample) > timeline.Notes.Min(note => note.StartSample));
            Assert.Contains("notes", timeline.Capabilities);
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
    public void Probe_ReportsMidiAsPortableWhenItHasNotes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-midi-probe-{Guid.NewGuid():N}.mid");
        try
        {
            File.WriteAllBytes(path, CreateMidi());
            PlaybackProbeResult result = new MidiPlaybackBackend().Probe(
                new FileInfo(path),
                new PlaybackEnvironment([Path.GetDirectoryName(path)!]));

            Assert.True(result.Supported);
            Assert.True(result.Visualizable);
            Assert.True(result.Portable);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact(Skip = "Legacy visualize integration fixture; canonical render coverage is in CanonicalRenderRequestTests.")]
    public void CliVisualize_DispatchesMidiThroughGenericBackend()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-midi-cli-{Guid.NewGuid():N}.mid");
        string output = Path.Combine(Path.GetTempPath(), $"mdplayer-midi-output-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(path, CreateMidi());
            var errors = new StringWriter();
            TextWriter originalError = Console.Error;
            int exitCode;
            try
            {
                Console.SetError(errors);
                exitCode = VisualizationRenderCommand.Handle(
                [
                    path,
                    "--output", output,
                    "--loops", "1",
                    "--fade", "0",
                    "--tail", "0",
                    "--overwrite",
                    "--quiet",
                ]);
            }
            finally
            {
                Console.SetError(originalError);
            }

            Assert.True(exitCode == 0, errors.ToString());
            Assert.True(File.Exists(Path.Combine(output, "timeline.json")));
            Assert.True(new FileInfo(Path.Combine(output, "audio", "master.wav")).Length > 44);
            Assert.False(File.Exists(Path.Combine(output, "visualization.mp4")));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    private static byte[] CreateMidi()
    {
        byte[] track =
        [
            0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20,
            0x00, 0xC0, 0x00,
            0x00, 0x90, 0x3C, 0x64,
            0x81, 0x70, 0x90, 0x40, 0x50,
            0x00, 0xE0, 0x00, 0x50,
            0x78, 0xB0, 0x40, 0x7F,
            0x78, 0x80, 0x3C, 0x40,
            0x78, 0x80, 0x40, 0x40,
            0x78, 0xB0, 0x40, 0x00,
            0x00, 0xFF, 0x2F, 0x00,
        ];
        byte[] midi = new byte[14 + 8 + track.Length];
        midi[0] = (byte)'M'; midi[1] = (byte)'T'; midi[2] = (byte)'h'; midi[3] = (byte)'d';
        BinaryPrimitives.WriteUInt32BigEndian(midi.AsSpan(4, 4), 6);
        BinaryPrimitives.WriteUInt16BigEndian(midi.AsSpan(8, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(midi.AsSpan(10, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(midi.AsSpan(12, 2), 480);
        midi[14] = (byte)'M'; midi[15] = (byte)'T'; midi[16] = (byte)'r'; midi[17] = (byte)'k';
        BinaryPrimitives.WriteUInt32BigEndian(midi.AsSpan(18, 4), (uint)track.Length);
        track.CopyTo(midi, 22);
        return midi;
    }
}
