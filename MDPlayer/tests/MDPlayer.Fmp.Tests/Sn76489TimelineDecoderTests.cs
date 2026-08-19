using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class Sn76489TimelineDecoderTests
{
    private const long ClockHz = 3_579_545;

    private static (Sn76489TimelineDecoder Decoder, TimelineBuilder Builder) NewDecoder()
    {
        var timeline = new TimelineBuilder(44_100);
        var decoder = new Sn76489TimelineDecoder();
        decoder.Initialize(VisualizationDeviceCatalog.Sn76489(0, ClockHz), timeline);
        return (decoder, timeline);
    }

    private static TimedChipWrite Write(long sample, int data) =>
        new(sample, new DeviceId(ChipType.Sn76489, 0), 0, 0, data);

    private static double MidiFromPeriod(int period)
    {
        double frequency = ClockHz / (32.0 * period);
        return 69.0 + 12.0 * Math.Log2(frequency / 440.0);
    }

    [Fact]
    public void TonePeriod_CombinesFourLatchBitsAndSixDataBits()
    {
        var (decoder, timeline) = NewDecoder();
        decoder.Process(Write(0, 0x83)); // channel 0, tone latch, low nibble = 3
        decoder.Process(Write(0, 0x12)); // high six bits = 0x12 => period 0x123
        decoder.Process(Write(0, 0x90)); // channel 0 volume = 0 (audible)
        decoder.Complete(1_000);

        NoteEvent note = Assert.Single(timeline.Build(1_000).Notes);
        Assert.Equal(MidiFromPeriod(0x123), note.InitialMidiNote, precision: 5);
    }

    [Fact]
    public void TonePeriod_DataWriteReplacesPreviousHighBitsWithoutStaleState()
    {
        var (decoder, timeline) = NewDecoder();
        decoder.Process(Write(0, 0x8B)); // initial low nibble = 0xB
        decoder.Process(Write(0, 0x3A)); // initial period = 0x3AB
        decoder.Process(Write(0, 0x90)); // start channel 0
        decoder.Process(Write(100, 0x82)); // new low nibble = 2
        decoder.Process(Write(100, 0x05)); // new period = 0x052
        decoder.Complete(1_000);

        NoteEvent note = Assert.Single(timeline.Build(1_000).Notes);
        PitchChange change = Assert.Single(note.Pitch);
        Assert.Equal(MidiFromPeriod(0x052), change.MidiNote, precision: 5);
    }
}
