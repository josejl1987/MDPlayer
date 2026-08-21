using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class Ym2612DacTimelineTests
{
    [Fact]
    public void Decoder_RepresentsDacBurstAsSamplePlaybackOnDacVoice()
    {
        VisualizationTimeline timeline = Decode(
            Write(0, 0x2B, 0x80),
            Write(0, 0x2A, 0x10),
            Write(8, 0x2A, 0x20),
            Write(16, 0x2A, 0x30),
            Write(24, 0x2B, 0x00));

        SamplePlaybackEvent dac = Assert.Single(timeline.SamplePlayback);
        Assert.Equal("ym2612.0.pcm.dac", dac.VoiceId);
        Assert.Equal(0, dac.StartSample);
        Assert.Equal(24, dac.EndSample);
        Assert.NotNull(dac.SourceAttackId);
        Assert.NotNull(dac.SampleId);

        SampleDefinition sample = Assert.Single(timeline.Samples);
        Assert.Equal(dac.SampleId, sample.Id);
        Assert.Equal("DAC S000", sample.DisplayName);

        // No pitched PCM note remains: the placeholder is removed.
        Assert.DoesNotContain(timeline.Notes, note => note.Mode == VisualizationNoteMode.Pcm);
        Assert.Contains(timeline.Voices, voice =>
            voice.Id.ToString() == "ym2612.0.pcm.dac" && voice.DisplayName == "DAC");
        Assert.Contains(timeline.Devices, device =>
            device.Id.Type == ChipType.Ym2612
            && device.Capabilities.HasFlag(DeviceCapabilities.SampleIdentity));
    }

    [Fact]
    public void Decoder_DeduplicatesIdenticalDacPayloadsByContent()
    {
        VisualizationTimeline timeline = Decode(
            Write(0, 0x2B, 0x80),
            Write(0, 0x2A, 0x12),
            Write(8, 0x2A, 0x34),
            Write(16, 0x2A, 0x56),
            Write(24, 0x2B, 0x00),
            Write(100, 0x2B, 0x80),
            Write(100, 0x2A, 0x12),
            Write(108, 0x2A, 0x34),
            Write(116, 0x2A, 0x56),
            Write(124, 0x2B, 0x00));

        Assert.Equal(2, timeline.SamplePlayback.Length);
        Assert.Equal(timeline.SamplePlayback[0].SampleId, timeline.SamplePlayback[1].SampleId);
        Assert.True(timeline.SamplePlayback[1].Retrigger);
        Assert.Single(timeline.Samples);
    }

    [Fact]
    public void Decoder_SplitsEnabledDacStreamAcrossLongWriteGap()
    {
        VisualizationTimeline timeline = Decode(
            Write(0, 0x2B, 0x80),
            Write(0, 0x2A, 0x10),
            Write(8, 0x2A, 0x20),
            Write(200, 0x2A, 0x30),
            Write(208, 0x2A, 0x40),
            Write(216, 0x2B, 0x00));

        SamplePlaybackEvent[] dac = timeline.SamplePlayback;
        Assert.Equal(2, dac.Length);
        // First burst's event is closed when the next trigger begins (the
        // observed timeline is authoritative, not the burst's natural duration).
        Assert.Equal((0L, 200L), (dac[0].StartSample, dac[0].EndSample));
        Assert.Equal((200L, 216L), (dac[1].StartSample, dac[1].EndSample));
    }

    [Fact]
    public void Decoder_StopsFm6WhenDacModeIsEnabled()
    {
        VisualizationTimeline timeline = Decode(
            Write(0, 0xA2, 0x35, port: 1),
            Write(0, 0xA6, 0x21, port: 1),
            Write(0, 0x28, 0xF6),
            Write(10, 0x2B, 0x80),
            Write(10, 0x2A, 0x80),
            Write(20, 0x2B, 0x00));

        NoteEvent fm6 = Assert.Single(timeline.Notes.Where(note => note.ChannelId == "ym2612.0.fm.6"));
        Assert.Equal(10, fm6.EndSample);
        Assert.Single(timeline.SamplePlayback);
    }

    private static VisualizationTimeline Decode(params TimedChipWrite[] writes)
    {
        var builder = new TimelineBuilder(44_100);
        var decoder = new Ym2612TimelineDecoder();
        DeviceDescriptor device = VisualizationDeviceCatalog.Ym2612();
        decoder.Initialize(device, builder);
        foreach (TimedChipWrite write in writes)
            decoder.Process(write);
        long endSample = Math.Max(1, writes.Length == 0 ? 1 : writes.Max(write => write.SamplePosition) + 16);
        decoder.Complete(endSample);
        return builder.Build(endSample, "test");
    }

    private static TimedChipWrite Write(long sample, int address, int data, int port = 0) => new(
        sample,
        new DeviceId(ChipType.Ym2612, 0),
        port,
        address,
        data);
}