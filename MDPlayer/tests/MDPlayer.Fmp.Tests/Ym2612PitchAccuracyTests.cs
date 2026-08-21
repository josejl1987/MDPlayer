using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class Ym2612PitchAccuracyTests
{
    [Fact]
    public void CombinedCsmAndThreeSlotBits_EnableOperatorPitchLanes()
    {
        DeviceDescriptor device = VisualizationDeviceCatalog.Ym2612();
        var timeline = new TimelineBuilder(44_100);
        var decoder = new Ym2612TimelineDecoder();
        decoder.Initialize(device, timeline);

        Write(decoder, device, 0, 0xA2, 0x35, 0);
        Write(decoder, device, 0, 0xA6, 0x21, 0);
        Write(decoder, device, 0, 0x27, 0xC0, 10);
        Write(decoder, device, 0, 0x28, 0x42, 20);
        Write(decoder, device, 0, 0x28, 0x02, 100);
        decoder.Complete(120);

        NoteEvent note = Assert.Single(timeline.Build(120).Notes);
        Assert.Equal("ym2612.0.fm3.op.3", note.ChannelId);
    }

    [Fact]
    public void CsmBitAlone_LeavesChannelThreeInOrdinaryMode()
    {
        DeviceDescriptor device = VisualizationDeviceCatalog.Ym2612();
        var timeline = new TimelineBuilder(44_100);
        var decoder = new Ym2612TimelineDecoder();
        decoder.Initialize(device, timeline);

        Write(decoder, device, 0, 0xA2, 0x35, 0);
        Write(decoder, device, 0, 0xA6, 0x21, 0);
        Write(decoder, device, 0, 0x27, 0x80, 10);
        Write(decoder, device, 0, 0x28, 0x42, 20);
        Write(decoder, device, 0, 0x28, 0x02, 100);
        decoder.Complete(120);

        NoteEvent note = Assert.Single(timeline.Build(120).Notes);
        Assert.Equal("ym2612.0.fm.3", note.ChannelId);
    }

    [Fact]
    public void FmFrequencyChangeWhileKeyHeld_AddsPitchChangeWithoutRetrigger()
    {
        DeviceDescriptor device = VisualizationDeviceCatalog.Ym2612();
        var timeline = new TimelineBuilder(44_100);
        var decoder = new Ym2612TimelineDecoder();
        decoder.Initialize(device, timeline);

        Write(decoder, device, 0, 0xA0, 0x35, 0);
        Write(decoder, device, 0, 0xA4, 0x21, 0);
        Write(decoder, device, 0, 0x28, 0xF0, 100);
        Write(decoder, device, 0, 0xA0, 0x80, 500);
        Write(decoder, device, 0, 0xA4, 0x21, 500);
        Write(decoder, device, 0, 0x28, 0x00, 900);
        decoder.Complete(1_000);

        NoteEvent note = Assert.Single(timeline.Build(1_000).Notes);
        PitchChange change = Assert.Single(note.Pitch);
        Assert.Equal(500, change.SamplePosition);
        Assert.NotEqual(note.InitialMidiNote, change.MidiNote);
    }


    private static void Write(
        Ym2612TimelineDecoder decoder,
        DeviceDescriptor device,
        int port,
        int address,
        int data,
        long sample) =>
        decoder.Process(new TimedChipWrite(sample, device.Id, port, address, data));
}
