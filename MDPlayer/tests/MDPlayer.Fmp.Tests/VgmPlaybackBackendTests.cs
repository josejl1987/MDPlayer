using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Fmp.Cli;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class VgmPlaybackBackendTests
{
    [Fact]
    public void Parse_DiscoversYm2612AndSn76489AndPreservesSampleTiming()
    {
        VgmDocument document = VgmDocument.Parse(CreateVgm(
            0x52, 0xA0, 0x35,
            0x52, 0xA4, 0x21,
            0x52, 0x28, 0xF0,
            0x50, 0x80,
            0x61, 0x10, 0x00,
            0x52, 0x28, 0x00,
            0x50, 0x9F,
            0x66));

        Assert.Contains(document.Devices, device => device.Id.Type == ChipType.Ym2612);
        Assert.Contains(document.Devices, device => device.Id.Type == ChipType.Sn76489);
        Assert.Equal(6, document.Writes.Count);
        Assert.Equal(16, document.EndSample);
        Assert.Equal(0, document.Writes[0].SourceSample);
        Assert.Equal(16, document.Writes[^1].SourceSample);
    }

    [Fact]
    public void Parse_DecodesYm2612DacStreamFromTypeZeroDataBlock()
    {
        VgmDocument document = VgmDocument.Parse(CreateVgmWithDacData(
            [0x12, 0xA5, 0x7F],
            0x80,
            0x82,
            0x8F,
            0x66));

        Assert.Equal(3, document.Writes.Count);
        Assert.All(document.Writes, write =>
        {
            Assert.Equal(ChipType.Ym2612, write.Device.Type);
            Assert.Equal(0, write.Port);
            Assert.Equal(0x2A, write.Address);
        });
        Assert.Equal([0x12, 0xA5, 0x7F], document.Writes.Select(write => write.Data));
        Assert.Equal([0L, 0L, 2L], document.Writes.Select(write => write.SourceSample));
    }

    [Fact]
    public void Parse_SeeksWithinYm2612DacDataBlock()
    {
        VgmDocument document = VgmDocument.Parse(CreateVgmWithDacData(
            [0x12, 0xA5, 0x7F],
            0xE0, 0x02, 0x00, 0x00, 0x00,
            0x80,
            0xE0, 0x00, 0x00, 0x00, 0x00,
            0x80,
            0x66));

        Assert.Equal([0x7F, 0x12], document.Writes.Select(write => write.Data));
    }

    [Fact]
    public void Parse_DecodesOkim6258FlagsFromHeader()
    {
        VgmDocument document = VgmDocument.Parse(CreateVgmWithOkim6258(
            flags: 0b1000_0011,
            0xB7, 0x04, 0x32,
            0x66));

        Assert.Contains(document.Devices, device => device.Id.Type == ChipType.Okim6258);
        Assert.Equal(0b1000_0011, document.Okim6258Flags);
    }

    [Fact]
    public void Parse_DefaultsOkim6258FlagsToZeroWhenChipAbsentOrAddressUnavailable()
    {
        // No OKIM6258 clock -> no flags
        VgmDocument withoutChip = VgmDocument.Parse(CreateVgm(
            0x52, 0xA0, 0x35,
            0x66));
        Assert.Equal(0, withoutChip.Okim6258Flags);

        // Clock and flags bytes present but dataStart (0x40) does not cover
        // offset 0x94 -> flags default to 0 rather than throwing. The command
        // walk is bounded by the EofOffset so the trailing header bytes at
        // 0x90/0x94 are never interpreted as commands.
        byte[] truncated = new byte[0x95];
        BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(0x00, 4), 0x206D6756);
        BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(0x04, 4), 0x3D); // eof = 0x41
        BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(0x08, 4), 0x0000_0150);
        BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(0x0C, 4), 3_579_545);
        truncated[0x40] = 0x66; // end command at dataStart
        BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(0x90, 4), 4_000_000);
        truncated[0x94] = 0b1000_0011;
        VgmDocument addressedEarly = VgmDocument.Parse(truncated);
        Assert.Equal(0, addressedEarly.Okim6258Flags);
    }

    [Fact]
    public void Capture_ForwardsOkim6258FlagsToRenderer()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-okim-{Guid.NewGuid():N}.vgm");
        string wav = path + ".wav";
        try
        {
            File.WriteAllBytes(path, CreateVgmWithOkim6258(
                flags: 0b1000_0011,
                0xB7, 0x04, 0x32,
                0x66));

            var backend = new VgmPlaybackBackend();
            var sink = new TimelineDecoderEventSink(44_100);
            using IPlaybackCaptureSession session = backend.Open(
                new FileInfo(path),
                new PlaybackOptions(LoopCount: 1, OutputAudioPath: wav, SampleRate: 44_100),
                sink);

            session.Run();
            VisualizationTimeline timeline = sink.Complete(session.SamplePosition, "test");

            Assert.True(session.IsComplete);
            Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Okim6258);
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
    public void Capture_ProducesMixedDeviceTimelineAndMasterWav()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-{Guid.NewGuid():N}.vgm");
        string wav = path + ".wav";
        try
        {
            File.WriteAllBytes(path, CreateVgm(
                0x52, 0xA0, 0x35,
                0x52, 0xA4, 0x21,
                0x52, 0x28, 0xF0,
                0x50, 0x80,
                0x50, 0x01,
                0x50, 0x90,
                0x61, 0x10, 0x00,
                0x52, 0x28, 0x00,
                0x50, 0x9F,
                0x66));

            var backend = new VgmPlaybackBackend();
            var sink = new TimelineDecoderEventSink(44_100);
            using IPlaybackCaptureSession session = backend.Open(
                new FileInfo(path),
                new PlaybackOptions(LoopCount: 1, OutputAudioPath: wav, SampleRate: 44_100),
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
    public void Capture_DecodesYm2151RegisterStream()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-opm-{Guid.NewGuid():N}.vgm");
        string wav = path + ".wav";
        try
        {
            File.WriteAllBytes(path, CreateVgmWithYm2151(
                0x54, 0x28, 0x4C,
                0x54, 0x30, 0x00,
                0x54, 0x08, 0x78,
                0x61, 0x10, 0x00,
                0x54, 0x08, 0x00,
                0x66));

            var sink = new TimelineDecoderEventSink(44_100);
            using IPlaybackCaptureSession session = new VgmPlaybackBackend().Open(
                new FileInfo(path),
                new PlaybackOptions(LoopCount: 1, FadeSeconds: 0, TailSeconds: 0, OutputAudioPath: wav),
                sink);
            session.Run();
            VisualizationTimeline timeline = sink.Complete(session.SamplePosition, "test");

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
    public void Capture_DecodesAy8910RegisterStream()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-ay-{Guid.NewGuid():N}.vgm");
        string wav = path + ".wav";
        try
        {
            File.WriteAllBytes(path, CreateVgm(
                0xA0, 0x00, 0x20,
                0xA0, 0x01, 0x00,
                0xA0, 0x07, 0x00,
                0xA0, 0x08, 0x0F,
                0x61, 0x10, 0x00,
                0xA0, 0x08, 0x00,
                0x66));

            var sink = new TimelineDecoderEventSink(44_100);
            using IPlaybackCaptureSession session = new VgmPlaybackBackend().Open(
                new FileInfo(path),
                new PlaybackOptions(LoopCount: 1, FadeSeconds: 0, TailSeconds: 0, OutputAudioPath: wav),
                sink);
            session.Run();
            VisualizationTimeline timeline = sink.Complete(session.SamplePosition, "test");

            Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Ay8910);
            Assert.Contains(timeline.Notes, note => note.ChannelId == "ay8910.0.psg.1");
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
    public void Capture_DecodesYm2203RegisterStream()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-ym2203-{Guid.NewGuid():N}.vgm");
        string wav = path + ".wav";
        try
        {
            File.WriteAllBytes(path, CreateVgm(
                0x55, 0xA0, 0x20,
                0x55, 0xA4, 0x11,
                0x55, 0x28, 0xF0,
                0x61, 0x10, 0x00,
                0x55, 0x28, 0x00,
                0x66));

            var sink = new TimelineDecoderEventSink(44_100);
            using IPlaybackCaptureSession session = new VgmPlaybackBackend().Open(
                new FileInfo(path),
                new PlaybackOptions(LoopCount: 1, FadeSeconds: 0, TailSeconds: 0, OutputAudioPath: wav),
                sink);
            session.Run();
            VisualizationTimeline timeline = sink.Complete(session.SamplePosition, "test");

            Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Ym2203);
            Assert.Contains(timeline.Notes, note => note.ChannelId == "ym2203.0.fm.1");
            Assert.Contains(timeline.Voices, voice => voice.Id.ToString() == "ym2203.0.ssg.1");
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
    public void Capture_DecodesYm2413RegisterStream()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-ym2413-{Guid.NewGuid():N}.vgm");
        string wav = path + ".wav";
        try
        {
            File.WriteAllBytes(path, CreateVgm(
                0x51, 0x10, 0xAC,
                0x51, 0x20, 0x11,
                0x61, 0x10, 0x00,
                0x51, 0x20, 0x01,
                0x66));

            var sink = new TimelineDecoderEventSink(44_100);
            using IPlaybackCaptureSession session = new VgmPlaybackBackend().Open(
                new FileInfo(path),
                new PlaybackOptions(LoopCount: 1, FadeSeconds: 0, TailSeconds: 0, OutputAudioPath: wav),
                sink);
            session.Run();
            VisualizationTimeline timeline = sink.Complete(session.SamplePosition, "test");

            Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Ym2413);
            Assert.Contains(timeline.Notes, note => note.ChannelId == "ym2413.0.fm.1");
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
    public void Capture_DecodesYm3812OplRegisterStream()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-ym3812-{Guid.NewGuid():N}.vgm");
        string wav = path + ".wav";
        try
        {
            File.WriteAllBytes(path, CreateVgm(
                0x5A, 0xA0, 0x35,
                0x5A, 0xB0, 0x31,
                0x61, 0x10, 0x00,
                0x5A, 0xB0, 0x11,
                0x66));

            var sink = new TimelineDecoderEventSink(44_100);
            using IPlaybackCaptureSession session = new VgmPlaybackBackend().Open(
                new FileInfo(path),
                new PlaybackOptions(LoopCount: 1, FadeSeconds: 0, TailSeconds: 0, OutputAudioPath: wav),
                sink);
            session.Run();
            VisualizationTimeline timeline = sink.Complete(session.SamplePosition, "test");

            Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Ym3812);
            Assert.Contains(timeline.Notes, note => note.ChannelId == "ym3812.0.fm.1");
            Assert.Contains(timeline.Voices, voice => voice.Id.ToString() == "ym3812.0.rhythm.bd");
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
    public void Capture_DecodesY8950OplRegisterStream()
    {
        VisualizationTimeline timeline = CaptureVgm(
            "y8950",
            0x5C, 0xA0, 0x35,
            0x5C, 0xB0, 0x31,
            0x61, 0x10, 0x00,
            0x5C, 0xB0, 0x11,
            0x66);

        Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Y8950);
        Assert.Contains(timeline.Notes, note => note.ChannelId == "y8950.0.fm.1");
    }

    [Fact]
    public void Capture_DecodesYm2608RegisterStream()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-ym2608-{Guid.NewGuid():N}.vgm");
        string wav = path + ".wav";
        try
        {
            File.WriteAllBytes(path, CreateVgm(
                0x56, 0xA0, 0x35,
                0x56, 0xA4, 0x21,
                0x56, 0x28, 0xF0,
                0x61, 0x10, 0x00,
                0x56, 0x28, 0x00,
                0x66));

            var sink = new TimelineDecoderEventSink(44_100);
            using IPlaybackCaptureSession session = new VgmPlaybackBackend().Open(
                new FileInfo(path),
                new PlaybackOptions(LoopCount: 1, FadeSeconds: 0, TailSeconds: 0, OutputAudioPath: wav),
                sink);
            session.Run();
            VisualizationTimeline timeline = sink.Complete(session.SamplePosition, "test");

            Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Ym2608);
            Assert.Contains(timeline.Notes, note => note.ChannelId == "ym2608.0.fm.1");
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
    public void Capture_DecodesYm2608Rhythm()
    {
        VisualizationTimeline timeline = CaptureVgm(
            "ym2608-rhythm",

            // BD key-on
            0x56, 0x10, 0x01,

            // Wait
            0x61, 0x10, 0x00,

            // BD dump/key-off
            0x56, 0x10, 0x81,

            0x66);

        Assert.Contains(
            timeline.Voices,
            voice => voice.Id.ToString() == "ym2608.0.rhythm"
                && voice.Presentation == VoicePresentationKind.Percussion);

        Assert.Contains(
            timeline.Rhythm,
            hit => VisualizationTimelineCompatibility.RhythmBelongsToVoice(
                timeline,
                hit,
                "ym2608.0.rhythm"));

        VisualizationTopology topology = VisualizationTopologyBuilder.Build(
            timeline,
            VisualizationChannelFilter.Active);

        Assert.Contains(
            topology.Panels,
            panel => panel.Id == "ym2608.0.rhythm"
                && panel.Kind == PreparedPanelKind.Rhythm);
    }

    [Fact]
    public void VgmScopeRenderer_RendersYm2608ChannelStems()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-scope-ym2608-{Guid.NewGuid():N}.vgm");
        string output = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-scope-output-{Guid.NewGuid():N}");
        string master = Path.Combine(output, "master.wav");
        try
        {
            File.WriteAllBytes(path, CreateVgm(
                0x56, 0xA0, 0x35,
                0x56, 0xA4, 0x21,
                0x56, 0x28, 0xF0,
                0x61, 0x20, 0x00,
                0x56, 0x28, 0x00,
                0x66));

            ScopeRenderer.ScopeResult result = VgmScopeRenderer.Render(
                path,
                output,
                master,
                sampleRate: 44_100,
                loopCount: 1,
                fadeSeconds: 0,
                tailSeconds: 0.01,
                maxDurationSeconds: 1);

            Assert.True(result.Success, result.LastError);
            string[] channelStems = result.Stems
                .Where(stem => stem.Name != "master")
                .Select(stem => stem.Name)
                .ToArray();
        Assert.True(channelStems.Length >= 11);
            Assert.Contains("ym2608-fm1", channelStems);
            Assert.Contains("ym2608-ssg1", channelStems);
            Assert.Contains("ym2608-rhythm", channelStems);
            Assert.All(result.Stems.Where(stem => stem.Name != "master"), stem =>
            {
                Assert.True(stem.Success, stem.Error);
                Assert.True(File.Exists(stem.WavPath), stem.WavPath);
            });
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void VgmScopeRenderer_RendersYm2151AndOkim6295ChannelStems()
    {
        // A register stream driving a YM2151 (OPM) FM voice on channel 1 plus
        // an OKIM6295 sample command. Both chips previously caused every scope
        // panel to fall back to the master mix; they must now emit isolated
        // per-channel stems (8× YM2151 + 1× OKIM).
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-scope-ym2151-{Guid.NewGuid():N}.vgm");
        string output = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-scope-output-{Guid.NewGuid():N}");
        string master = Path.Combine(output, "master.wav");
        try
        {
            File.WriteAllBytes(path, CreateVgmWithYm2151(
                // YM2151 ch1: key code + key fraction + key on (like OPM).
                0x54, 0x29, 0x4C,
                0x54, 0x31, 0x00,
                0x54, 0x08, 0x18,
                0x61, 0x10, 0x00,
                0x54, 0x08, 0x00,
                // OKIM6295 sample command (0xB8: instance flag|addr + data).
                0xB8, 0x00, 0x00,
                0x66));

            ScopeRenderer.ScopeResult result = VgmScopeRenderer.Render(
                path,
                output,
                master,
                sampleRate: 44_100,
                loopCount: 1,
                fadeSeconds: 0,
                tailSeconds: 0.01,
                maxDurationSeconds: 1);

            Assert.True(result.Success, result.LastError);
            string[] channelStems = result.Stems
                .Where(stem => stem.Name != "master")
                .Select(stem => stem.Name)
                .ToArray();
            Assert.True(channelStems.Length >= 9);
            Assert.Contains("ym2151-fm1", channelStems);
            Assert.Contains("ym2151-fm8", channelStems);
            Assert.Contains("okim6295-sample", channelStems);
            Assert.All(result.Stems.Where(stem => stem.Name != "master"), stem =>
            {
                Assert.True(stem.Success, stem.Error);
                Assert.True(File.Exists(stem.WavPath), stem.WavPath);
            });
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void VgmScopeRenderer_RendersOkim6258AdpcmChannelStem()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-scope-okim6258-{Guid.NewGuid():N}.vgm");
        string output = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-scope-output-{Guid.NewGuid():N}");
        string master = Path.Combine(output, "master.wav");
        try
        {
            var commands = new List<byte>
            {
                0xB7, 0x00, 0x02, // control: start ADPCM playback
            };
            for (int index = 0; index < 256; index++)
            {
                commands.Add(0xB7);
                commands.Add(0x01); // OKIM6258 data port
                commands.Add((byte)(0x11 + (index & 0x0F)));
            }
            commands.AddRange([0x61, 0x88, 0x13]); // 5000 samples
            commands.AddRange([0xB7, 0x00, 0x00]); // control: stop
            commands.Add(0x66);
            File.WriteAllBytes(path, CreateVgm(commands.ToArray()));

            ScopeRenderer.ScopeResult result = VgmScopeRenderer.Render(
                path,
                output,
                master,
                sampleRate: 44_100,
                loopCount: 1,
                fadeSeconds: 0,
                tailSeconds: 0,
                maxDurationSeconds: 1);

            Assert.True(result.Success, result.LastError);
            ScopeRenderer.StemResult stem = Assert.Single(
                result.Stems.Where(stem => stem.Name == "okim6258-sample"));
            Assert.True(stem.Success, stem.Error);
            Assert.True(File.Exists(stem.WavPath), stem.WavPath);
            Assert.Contains(File.ReadAllBytes(stem.WavPath).Skip(44), sample => sample != 0);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void VgmScopeRenderer_RendersYm2203AndYm2610ChannelStems()
    {
        // Drive a YM2203 FM note on channel 0 and a YM2610 FM note on channel 1
        // in one stream. Both chips used to make every scope panel fall back to
        // the master mix; they must now emit isolated per-channel FM+SSG stems
        // (YM2203: fm1..3 + ssg1..3, YM2610: fm1..4 + ssg1..3), and a note
        // written to one channel must be audible there while the sibling stays
        // silent.
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-scope-opn-{Guid.NewGuid():N}.vgm");
        string output = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-scope-opn-out-{Guid.NewGuid():N}");
        string master = Path.Combine(output, "master.wav");
        try
        {
            File.WriteAllBytes(path, CreateVgm(
                // YM2203 (0x55) FM ch0 with a complete OPN operator patch so the
                // emulator actually rings: DT/MUL, TL, RS/AR, SL/RR, key on ch0.
                0x55, 0x30, 0x31,
                0x55, 0x38, 0x03,
                0x55, 0x40, 0x03,
                0x55, 0x48, 0x03,
                0x55, 0x50, 0x00,
                0x55, 0x58, 0x10,
                0x55, 0x70, 0x1F,
                0x55, 0x78, 0x1F,
                0x55, 0xB0, 0x0F,
                0x55, 0xA0, 0x6A,
                0x55, 0xA4, 0x20,
                0x55, 0x28, 0x30,
                0x61, 0xFF, 0x7F,
                0x55, 0x28, 0x00,
                // YM2610 (0x58) FM ch1 so the device is present and its stems
                // are emitted (not a master fallback).
                0x58, 0x28, 0xF1,
                0x61, 0xFF, 0x7F,
                0x58, 0x28, 0x00,
                0x66));

            ScopeRenderer.ScopeResult result = VgmScopeRenderer.Render(
                path, output, master,
                sampleRate: 44_100, loopCount: 1, fadeSeconds: 0, tailSeconds: 0, maxDurationSeconds: 3);

            Assert.True(result.Success, result.LastError);
            var byName = result.Stems.ToDictionary(stem => stem.Name);
            Assert.Contains("ym2203-fm1", byName.Keys);
            Assert.Contains("ym2203-fm3", byName.Keys);
            Assert.Contains("ym2203-ssg1", byName.Keys);
            Assert.Contains("ym2203-ssg3", byName.Keys);
            // YM2610 siblings must also be emitted (channel stems, no fallback).
            Assert.Contains("ym2610-fm1", byName.Keys);
            Assert.Contains("ym2610-fm4", byName.Keys);
            Assert.Contains("ym2610-ssg1", byName.Keys);
            Assert.Contains("ym2610-ssg3", byName.Keys);

            // A sustained channel-0 note must be audible in fm1 and absent in fm2.
            double fm1 = StemRms(byName["ym2203-fm1"]);
            double fm2 = StemRms(byName["ym2203-fm2"]);
            Assert.True(fm1 > -60, $"ym2203-fm1 should be audible but was {fm1:F1}dB");
            Assert.True(fm2 < -90, $"ym2203-fm2 should be silent but was {fm2:F1}dB");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    private static double StemRms(ScopeRenderer.StemResult stem)
    {
        if (!File.Exists(stem.WavPath)) return -200;
        byte[] bytes = File.ReadAllBytes(stem.WavPath);
        int off = FindDataOffset(bytes);
        int count = (bytes.Length - off) / 2;
        if (count <= 0) return -200;
        long sum = 0; int samples = 0, step = Math.Max(1, count / 200_000);
        for (int i = 0; i < count; i += step)
        {
            short v = (short)(bytes[off + i * 2] | (bytes[off + i * 2 + 1] << 8));
            sum += (long)v * v; samples++;
        }
        double rms = Math.Sqrt((double)sum / samples);
        return 20 * Math.Log10((rms + 1e-9) / 32768);
    }

    private static int FindDataOffset(byte[] b)
    {
        for (int i = 0; i < b.Length - 4; i++)
            if (b[i] == (byte)'d' && b[i + 1] == 'a' && b[i + 2] == 't' && b[i + 3] == 'a')
                return i + 8;
        return 44;
    }

    [Fact]
    public void Capture_DecodesYm2610RegisterStream()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-ym2610-{Guid.NewGuid():N}.vgm");
        string wav = path + ".wav";
        try
        {
            File.WriteAllBytes(path, CreateVgm(
                0x58, 0xA0, 0x35,
                0x58, 0xA4, 0x21,
                0x58, 0x28, 0xF0,
                0x61, 0x10, 0x00,
                0x58, 0x28, 0x00,
                0x66));

            var sink = new TimelineDecoderEventSink(44_100);
            using IPlaybackCaptureSession session = new VgmPlaybackBackend().Open(
                new FileInfo(path),
                new PlaybackOptions(LoopCount: 1, FadeSeconds: 0, TailSeconds: 0, OutputAudioPath: wav),
                sink);
            session.Run();
            VisualizationTimeline timeline = sink.Complete(session.SamplePosition, "test");

            Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Ym2610);
            Assert.Contains(timeline.Notes, note => note.ChannelId == "ym2610.0.fm.1");
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
    public void Capture_DecodesDmgNesHuc6280AndK051649RegisterStreams()
    {
        VisualizationTimeline timeline = CaptureVgm(
            "native-psg",
            0xB3, 0x15, 0x11,
            0xB3, 0x16, 0x80,
            0xB3, 0x02, 0xF0,
            0xB3, 0x03, 0x20,
            0xB3, 0x04, 0x00,
            0x61, 0x20, 0x00,
            0xB3, 0x02, 0x00,
            0xB4, 0x15, 0x01,
            0xB4, 0x00, 0x0F,
            0xB4, 0x02, 0x35,
            0xB4, 0x03, 0x00,
            0x61, 0x20, 0x00,
            0xB4, 0x00, 0x00,
            0xB9, 0x00, 0x00,
            0xB9, 0x02, 0x20,
            0xB9, 0x03, 0x00,
            0xB9, 0x04, 0x9F,
            0xB9, 0x05, 0xFF,
            0x61, 0x20, 0x00,
            0xB9, 0x04, 0x00,
            0xD2, 0x01, 0x00, 0x20,
            0xD2, 0x01, 0x01, 0x01,
            0xD2, 0x02, 0x00, 0x0F,
            0xD2, 0x03, 0x00, 0x01,
            0x61, 0x20, 0x00,
            0xD2, 0x02, 0x00, 0x00,
            0x66);

        Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Dmg);
        Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.NesApu);
        Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Huc6280);
        Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.K051649);
        Assert.Contains(timeline.Notes, note => note.ChannelId == "dmg.0.pulse.1");
        Assert.Contains(timeline.Notes, note => note.ChannelId == "nesapu.0.pulse.1");
        Assert.Contains(timeline.Notes, note => note.ChannelId == "huc6280.0.wavetable.1");
        Assert.Contains(timeline.Notes, note => note.ChannelId == "k051649.0.wavetable.1");
    }

    [Fact]
    public void Capture_DecodesYmz280bPcmRegisterStream()
    {
        VisualizationTimeline timeline = CaptureVgm(
            "ymz280b",
            0x5D, 0x00, 0x00, 0x2D,
            0x5D, 0x00, 0x01, 0x80,
            0x5D, 0x00, 0x02, 0x0C,
            0x5D, 0x00, 0x03, 0x08,
            0x61, 0x20, 0x00,
            0x5D, 0x00, 0x01, 0x00,
            0x66);

        Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Ymz280b);
        Assert.Contains(timeline.Voices, voice => voice.Id.ToString() == "ymz280b.0.pcm.1");
        Assert.Contains(timeline.Notes, note => note.ChannelId == "ymz280b.0.pcm.1");
    }

    [Fact]
    public void Capture_DecodesYmf278bFmAndPcmRegisterStreams()
    {
        VisualizationTimeline timeline = CaptureVgm(
            "ymf278b",
            0xD0, 0x00, 0xA0, 0x35,
            0xD0, 0x00, 0xB0, 0x20,
            0x61, 0x20, 0x00,
            0xD0, 0x00, 0xB0, 0x00,
            0xD0, 0x02, 0x20, 0x6E,
            0xD0, 0x02, 0x38, 0x40,
            0xD0, 0x02, 0x50, 0x00,
            0xD0, 0x02, 0x68, 0x88,
            0x61, 0x20, 0x00,
            0xD0, 0x02, 0x68, 0x08,
            0x66);

        Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Ymf278b);
        Assert.Contains(timeline.Notes, note => note.ChannelId == "ymf278b.0.fm.1");
        Assert.Contains(timeline.Notes, note => note.ChannelId == "ymf278b.0.pcm.1");
    }

    [Fact]
    public void Capture_DecodesOkiActivityAndMasterAudio()
    {
        VisualizationTimeline timeline = CaptureVgm(
            "oki",
            0xB7, 0x00, 0x02,
            0x61, 0x20, 0x00,
            0xB7, 0x00, 0x00,
            0xB8, 0x00, 0x80,
            0xB8, 0x00, 0x10,
            0x61, 0x20, 0x00,
            0x66);

        Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Okim6258);
        Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Okim6295);
        Assert.Contains(timeline.Voices, voice => voice.Id.ToString() == "okim6258.0.pcm.1");
        Assert.Contains(timeline.Voices, voice => voice.Id.ToString() == "okim6295.0.pcm.1");
        Assert.Contains(timeline.Rhythm, rhythm => rhythm.ChannelId == "okim6258.0.pcm.1");
        Assert.Contains(timeline.Rhythm, rhythm => rhythm.ChannelId == "okim6295.0.pcm.1");
    }

    [Fact]
    public void Capture_DecodesMultiPcmRegisterStream()
    {
        VisualizationTimeline timeline = CaptureVgm(
            "multipcm",
            0xB5, 0x02, 0x80,
            0xB5, 0x03, 0x40,
            0xB5, 0x05, 0x00,
            0xB5, 0x04, 0x80,
            0x61, 0x20, 0x00,
            0xB5, 0x04, 0x00,
            0x66);

        Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.MultiPcm);
        Assert.Contains(timeline.Voices, voice => voice.Id.ToString() == "multipcm.0.pcm.1");
        Assert.Contains(timeline.Notes, note => note.ChannelId == "multipcm.0.pcm.1");
    }

    [Fact]
    public void Capture_DecodesVgmPcmKeyboardFamilies()
    {
        VisualizationTimeline timeline = CaptureVgm(
            "pcm-families",
            // SEGAPCM channel 1: pitch multiplier followed by key-on/key-off.
            0xC0, 0x07, 0x00, 0x80,
            0xC0, 0x06, 0x00, 0x00,
            // C140 channel 1: frequency and key-on bit.
            0xD4, 0x00, 0x02, 0x20,
            0xD4, 0x00, 0x03, 0x40,
            0xD4, 0x00, 0x05, 0x80,
            // C352 channel 1: 16-bit frequency and control word.
            0xE1, 0x00, 0x02, 0x01, 0x00,
            0xE1, 0x00, 0x03, 0x40, 0x00,
            // K054539 channel 1: 24-bit frequency and global key mask.
            0xD3, 0x00, 0x00, 0x00,
            0xD3, 0x00, 0x01, 0x20,
            0xD3, 0x00, 0x02, 0x00,
            0xD3, 0x02, 0x2C, 0x01,
            // GA20 channel 1: frequency and trigger register.
            0xBF, 0x04, 0x80,
            0xBF, 0x06, 0x01,
            0x61, 0x20, 0x00,
            0xC0, 0x06, 0x00, 0x01,
            0xD4, 0x00, 0x05, 0x00,
            0xD3, 0x02, 0x2C, 0x00,
            0x66);

        Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.SegaPcm);
        Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.C140);
        Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.C352);
        Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.K054539);
        Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Ga20);
        Assert.Contains(timeline.Notes, note => note.ChannelId == "segapcm.0.pcm.1");
        Assert.Contains(timeline.Notes, note => note.ChannelId == "c140.0.pcm.1");
        Assert.Contains(timeline.Notes, note => note.ChannelId == "c352.0.pcm.1");
        Assert.Contains(timeline.Notes, note => note.ChannelId == "k054539.0.pcm.1");
        Assert.Contains(timeline.Notes, note => note.ChannelId == "ga20.0.pcm.1");
    }

    [Fact]
    public void Capture_DecodesRf5c68AndRf5c164SelectedChannelRegisters()
    {
        VisualizationTimeline timeline = CaptureVgm(
            "rf5c",
            // RF5C68 channel 1: select/enable bank, envelope and step.
            0xB0, 0x00, 0x20,
            0xB0, 0x02, 0x00,
            0xB0, 0x03, 0x20,
            0xB0, 0x08, 0x80,
            // RF5C164 uses the same selected-channel register seam.
            0xB1, 0x00, 0x20,
            0xB1, 0x02, 0x00,
            0xB1, 0x03, 0x20,
            0xB1, 0x08, 0x80,
            0x61, 0x20, 0x00,
            0xB0, 0x08, 0x00,
            0xB1, 0x08, 0x00,
            0x66);

        Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Rf5c68);
        Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Rf5c164);
        Assert.Contains(timeline.Notes, note => note.ChannelId == "rf5c68.0.pcm.1");
        Assert.Contains(timeline.Notes, note => note.ChannelId == "rf5c164.0.pcm.1");
    }

    [Fact]
    public void Capture_EmitsEmbeddedVgmPcmAssetEvent()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-asset-{Guid.NewGuid():N}.vgm");
        string wav = path + ".wav";
        try
        {
            File.WriteAllBytes(path, CreateVgmWithSampleAsset(
                0x80,
                0x04, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x10, 0x20, 0x30, 0x40,
                0xC0, 0x07, 0x00, 0x80,
                0x61, 0x10, 0x00,
                0xC0, 0x06, 0x00, 0x00,
                0x66));

            var sink = new AssetRecordingSink();
            using IPlaybackCaptureSession session = new VgmPlaybackBackend().Open(
                new FileInfo(path),
                new PlaybackOptions(LoopCount: 1, FadeSeconds: 0, TailSeconds: 0, OutputAudioPath: wav),
                sink);
            session.Run();

            VgmDocument document = VgmDocument.Parse(File.ReadAllBytes(path));
            VgmSampleAsset asset = Assert.Single(document.Assets);
            Assert.Equal(ChipType.SegaPcm, asset.Device.Type);
            Assert.Equal((uint)4, asset.RomSize);
            Assert.Equal([0x10, 0x20, 0x30, 0x40], asset.Data);

            TimedSampleAssetEvent emitted = Assert.Single(sink.Assets);
            Assert.Equal("vgm:segapcm.0:00000000:00000004", emitted.AssetId);
            Assert.Equal(AssetKind.Pcm, emitted.Kind);
            Assert.Equal(4, emitted.SizeBytes);
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
    public void Capture_WithoutAudioStillEmitsEmbeddedVgmPcmAssetEvent()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-asset-no-audio-{Guid.NewGuid():N}.vgm");
        try
        {
            File.WriteAllBytes(path, CreateVgmWithSampleAsset(
                0x80,
                0x04, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x10, 0x20, 0x30, 0x40,
                0x66));

            var sink = new AssetRecordingSink();
            using IPlaybackCaptureSession session = new VgmPlaybackBackend().Open(
                new FileInfo(path),
                new PlaybackOptions(LoopCount: 1, FadeSeconds: 0, TailSeconds: 0),
                sink);
            session.Run();

            TimedSampleAssetEvent emitted = Assert.Single(sink.Assets);
            Assert.Equal("vgm:segapcm.0:00000000:00000004", emitted.AssetId);
            Assert.Equal(AssetKind.Pcm, emitted.Kind);
            Assert.Equal(4, emitted.SizeBytes);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0x81, (int)ChipType.Ym2608, (int)AssetKind.Adpcm)]
    [InlineData(0x82, (int)ChipType.Ym2610, (int)AssetKind.AdpcmA)]
    [InlineData(0x83, (int)ChipType.Ym2610, (int)AssetKind.AdpcmB)]
    public void Parse_MapsVgmAdpcmDataBlocksToTheirOwningDevice(
        byte blockType,
        int expectedType,
        int expectedKind)
    {
        VgmDocument document = VgmDocument.Parse(CreateVgmWithSampleAsset(
            blockType,
            0x04, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x10, 0x20, 0x30, 0x40,
            0x66));

        VgmSampleAsset asset = Assert.Single(document.Assets);
        Assert.Equal((ChipType)expectedType, asset.Device.Type);
        Assert.Equal((AssetKind)expectedKind, asset.Kind);
    }

    [Fact]
    public void Capture_LoadsYm2608AdpcmDataBeforeRegisterPlayback()
    {
        VisualizationTimeline timeline = CaptureVgmWithSampleAsset(
            "ym2608-adpcm",
            0x81,
            0x56, 0xA0, 0x35,
            0x56, 0xA4, 0x21,
            0x56, 0x28, 0xF0,
            0x61, 0x10, 0x00,
            0x56, 0x28, 0x00,
            0x66);

        Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Ym2608);
        Assert.Contains(timeline.Notes, note => note.ChannelId == "ym2608.0.fm.1");
    }

    [Fact]
    public void Capture_LoadsYm2610AdpcmDataBeforeRegisterPlayback()
    {
        VisualizationTimeline timeline = CaptureVgmWithSampleAsset(
            "ym2610-adpcm",
            0x82,
            0x58, 0xA0, 0x35,
            0x58, 0xA4, 0x21,
            0x58, 0x28, 0xF0,
            0x61, 0x10, 0x00,
            0x58, 0x28, 0x00,
            0x66);

        Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Ym2610);
        Assert.Contains(timeline.Notes, note => note.ChannelId == "ym2610.0.fm.1");
    }

    [Fact]
    public void Capture_HonorsTailAndMaximumDurationOptions()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-options-{Guid.NewGuid():N}.vgm");
        try
        {
            File.WriteAllBytes(path, CreateVgm(
                0x52, 0xA0, 0x35,
                0x61, 0x10, 0x00,
                0x66));

            var sink = new TimelineDecoderEventSink(44_100);
            using IPlaybackCaptureSession session = new VgmPlaybackBackend().Open(
                new FileInfo(path),
                new PlaybackOptions(
                    LoopCount: 1,
                    FadeSeconds: 0,
                    TailSeconds: 0.001,
                    MaxDurationSeconds: 0.0012),
                sink);
            session.Run();

            Assert.Equal(53, session.SamplePosition);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact(Skip = "Legacy visualize integration fixture; canonical render coverage is in CanonicalRenderRequestTests.")]
    public void CliVisualize_ProducesTimelineAudioAndMasterScopeVideo()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-cli-{Guid.NewGuid():N}.vgm");
        string output = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-output-{Guid.NewGuid():N}");
        try
        {
            var commands = new List<byte>
            {
                0x52, 0xA0, 0x35,
                0x52, 0xA4, 0x21,
                0x52, 0x28, 0xF0,
                0x50, 0x80,
                0x50, 0x01,
            };
            commands.AddRange(Enumerable.Repeat((byte)0x62, 20));
            commands.AddRange([0x52, 0x28, 0x00, 0x50, 0x9F, 0x66]);
            File.WriteAllBytes(path, CreateVgm(commands.ToArray()));

                int exitCode = VisualizationRenderCommand.Handle(
            [
                path,
                "--output", output,
                "--width", "480",
                "--height", "360",
                "--fps", "10",
                "--loops", "1",
                "--fade", "0",
                "--tail", "0",
                "--max-duration", "10",
                "--scopes", "master",
                "--overwrite",
                "--quiet",
                "--ffmpeg", "/usr/bin/ffmpeg",
            ]);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(output, "timeline.json")));
            Assert.True(new FileInfo(Path.Combine(output, "audio", "master.wav")).Length > 44);
            string video = Path.Combine(output, "visualization.mp4");
            Assert.True(new FileInfo(video).Length > 0);

            using JsonDocument probe = ProbeVideo(video);
            JsonElement streams = probe.RootElement.GetProperty("streams");
            Assert.Equal(2, streams.GetArrayLength());
            JsonElement videoStream = Assert.Single(streams.EnumerateArray()
                .Where(stream => stream.GetProperty("codec_type").GetString() == "video"));
            Assert.Equal(480, videoStream.GetProperty("width").GetInt32());
            Assert.Equal(360, videoStream.GetProperty("height").GetInt32());
            Assert.Equal("10/1", videoStream.GetProperty("r_frame_rate").GetString());
            Assert.Single(streams.EnumerateArray()
                .Where(stream => stream.GetProperty("codec_type").GetString() == "audio"));

            string[] mp4s = Directory.GetFiles(output, "*.mp4", SearchOption.AllDirectories);
            Assert.Equal(new[] { video }, mp4s);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [SkippableFact]
    public void CanonicalRender_UsesUnifiedRunnerForGeneratedVgm()
    {
        Skip.If(
            Environment.GetEnvironmentVariable("MDPLAYER_HEAVY_TESTS") != "1",
            "Set MDPLAYER_HEAVY_TESTS=1 to run external canonical render tests.");
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-unified-{Guid.NewGuid():N}.vgm");
        string output = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-unified-{Guid.NewGuid():N}.mp4");
        try
        {
            File.WriteAllBytes(path, CreateVgm(
                0x52, 0xA0, 0x35,
                0x52, 0xA4, 0x21,
                0x52, 0x28, 0xF0,
                0x61, 0x10, 0x00,
                0x52, 0x28, 0x00,
                0x66));

            int exitCode = VisualizationRenderCommand.Handle(
            [
                path,
                "--output", output,
                "--width", "480",
                "--height", "360",
                "--fps", "10",
                "--loops", "1",
                "--fade", "0",
                "--tail", "0",
                "--max-duration", "1",
                "--overwrite",
                "--quiet",
                "--ffmpeg", "/usr/bin/ffmpeg",
                // No --corrscope override: the unified runner provisions and uses
                // a working Corrscope (existing PATH/pipx install or the managed
                // project-local venv).
            ]);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(output));
            Assert.True(File.Exists(Path.Combine(
                Path.GetDirectoryName(output)!, "timeline.json")));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(output)) File.Delete(output);
            string outputDir = Path.Combine(
                Path.GetDirectoryName(output)!,
                Path.GetFileNameWithoutExtension(output));
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [SkippableFact]
    public void CanonicalRender_InvalidExplicitCorrscope_FailsWithHardRequirement()
    {
        Skip.If(
            Environment.GetEnvironmentVariable("MDPLAYER_HEAVY_TESTS") != "1",
            "Set MDPLAYER_HEAVY_TESTS=1 to run external canonical render tests.");
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-corrscope-{Guid.NewGuid():N}.vgm");
        string output = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-corrscope-{Guid.NewGuid():N}.mp4");
        try
        {
            File.WriteAllBytes(path, CreateVgm(
                0x52, 0xA0, 0x35,
                0x52, 0xA4, 0x21,
                0x52, 0x28, 0xF0,
                0x61, 0x10, 0x00,
                0x52, 0x28, 0x00,
                0x66));

            // Corrscope is now a hard requirement for scoped renders: an explicit
            // but invalid --corrscope must fail the render rather than silently
            // emit empty scope regions.
            int exitCode = VisualizationRenderCommand.Handle(
            [
                path,
                "--output", output,
                "--width", "480",
                "--height", "360",
                "--fps", "10",
                "--loops", "1",
                "--fade", "0",
                "--tail", "0",
                "--max-duration", "1",
                "--overwrite",
                "--quiet",
                "--ffmpeg", "/usr/bin/ffmpeg",
                "--corrscope", "/nonexistent/corrscope",
            ]);

            Assert.NotEqual(0, exitCode);
            Assert.False(File.Exists(output));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(output)) File.Delete(output);
            string outputDir = Path.Combine(
                Path.GetDirectoryName(output)!,
                Path.GetFileNameWithoutExtension(output));
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    private static JsonDocument ProbeVideo(string videoPath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/ffprobe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (string argument in new[]
        {
            "-v", "error",
            "-show_entries", "stream=codec_type,width,height,r_frame_rate",
            "-of", "json",
            videoPath,
        })
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return JsonDocument.Parse(stdout);
    }

    private static byte[] CreateVgm(params byte[] commands)
    {
        byte[] data = new byte[0x40 + commands.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x00, 4), 0x206D6756);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x08, 4), 0x0000_0150);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x0C, 4), 3_579_545);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x2C, 4), 7_670_454);
        commands.CopyTo(data, 0x40);
        return data;
    }

    private static byte[] CreateVgmWithYm2151(params byte[] commands)
    {
        byte[] data = CreateVgm(commands);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x2C, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x30, 4), 3_579_545);
        return data;
    }

    private static byte[] CreateVgmWithOkim6258(byte flags, params byte[] commands)
    {
        // Data offset points past the fixed header so dataStart (0x114) covers
        // the OKIM6258 clock at 0x90 and flags byte at 0x94.
        const int dataStart = 0x114;
        const uint dataOffset = dataStart - 0x34; // 0xE0
        byte[] data = new byte[dataStart + commands.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x00, 4), 0x206D6756);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x08, 4), 0x0000_0150);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x0C, 4), 3_579_545);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x34, 4), dataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x90, 4), 4_000_000);
        data[0x94] = flags;
        commands.CopyTo(data, dataStart);
        return data;
    }

    private static byte[] CreateVgmWithSampleAsset(byte blockType, params byte[] commands)
    {
        const int blockLength = 12;
        byte[] data = new byte[0x40 + 6 + blockLength + commands.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x00, 4), 0x206D6756);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x08, 4), 0x0000_0150);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x0C, 4), 3_579_545);
        int cursor = 0x40;
        data[cursor++] = 0x67;
        data[cursor++] = 0x66;
        data[cursor++] = blockType;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(cursor, 4), blockLength);
        cursor += 4;
        commands.AsSpan(0, blockLength).CopyTo(data.AsSpan(cursor));
        cursor += blockLength;
        commands.AsSpan(blockLength).CopyTo(data.AsSpan(cursor));
        return data;
    }

    private static byte[] CreateVgmWithDacData(byte[] dacData, params byte[] commands)
    {
        byte[] data = new byte[0x40 + 7 + dacData.Length + commands.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x00, 4), 0x206D6756);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x08, 4), 0x0000_0150);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x0C, 4), 3_579_545);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x2C, 4), 7_670_454);
        int cursor = 0x40;
        data[cursor++] = 0x67;
        data[cursor++] = 0x66;
        data[cursor++] = 0x00;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(cursor, 4), (uint)dacData.Length);
        cursor += 4;
        dacData.CopyTo(data, cursor);
        cursor += dacData.Length;
        commands.CopyTo(data, cursor);
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

    private static VisualizationTimeline CaptureVgm(string name, params byte[] commands)
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-{name}-{Guid.NewGuid():N}.vgm");
        string wav = path + ".wav";
        try
        {
            File.WriteAllBytes(path, CreateVgm(commands));
            var sink = new TimelineDecoderEventSink(44_100);
            using IPlaybackCaptureSession session = new VgmPlaybackBackend().Open(
                new FileInfo(path),
                new PlaybackOptions(LoopCount: 1, FadeSeconds: 0, TailSeconds: 0, OutputAudioPath: wav),
                sink);
            session.Run();
            VisualizationTimeline timeline = sink.Complete(session.SamplePosition, "test");
            Assert.True(new FileInfo(wav).Length > 44);
            return timeline;
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(wav)) File.Delete(wav);
            if (File.Exists(wav + ".tmp")) File.Delete(wav + ".tmp");
        }
    }

    private static VisualizationTimeline CaptureVgmWithSampleAsset(
        string name,
        byte blockType,
        params byte[] commands)
    {
        string path = Path.Combine(Path.GetTempPath(), $"mdplayer-vgm-{name}-{Guid.NewGuid():N}.vgm");
        string wav = path + ".wav";
        try
        {
            byte[] blockAndCommands =
            [
                0x04, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00,
                0x10, 0x20, 0x30, 0x40,
                ..commands,
            ];
            File.WriteAllBytes(path, CreateVgmWithSampleAsset(
                blockType,
                blockAndCommands));
            var sink = new TimelineDecoderEventSink(44_100);
            using IPlaybackCaptureSession session = new VgmPlaybackBackend().Open(
                new FileInfo(path),
                new PlaybackOptions(LoopCount: 1, FadeSeconds: 0, TailSeconds: 0, OutputAudioPath: wav),
                sink);
            session.Run();
            return sink.Complete(session.SamplePosition, "test");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(wav)) File.Delete(wav);
            if (File.Exists(wav + ".tmp")) File.Delete(wav + ".tmp");
        }
    }
}
