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

    private static void AssertPitch(NoteEvent note, double frequencyHz, double midiNote)
    {
        Assert.Equal(frequencyHz, note.InitialFrequencyHz, precision: 6);
        Assert.Equal(midiNote, note.InitialMidiNote, precision: 6);
    }

    [Fact]
    public void TonePeriod_LatchNibbleAndContinuationSixBitsAssembleAllTenBits()
    {
        var (decoder, timeline) = NewDecoder();
        decoder.Process(Write(0, 0x8F)); // period low nibble = 0xF
        decoder.Process(Write(0, 0x7F)); // only 0x3F is high period data
        decoder.Process(Write(0, 0x90)); // volume 0 (audible)
        decoder.Complete(1_000);

        NoteEvent note = Assert.Single(timeline.Build(1_000).Notes);
        // Hand calculation: period = 0x3FF = 1023,
        // frequency = 3,579,545 / (32 * 1023) = 109.345827224 Hz,
        // MIDI = 69 + 12*log2(frequency / 440) = 44.896735711.
        AssertPitch(note, 109.345827224, 44.896735711);
    }

    [Fact]
    public void TonePeriod_ContinuationMasksToSixBits()
    {
        var (decoder, timeline) = NewDecoder();
        decoder.Process(Write(0, 0x8F)); // period low nibble = 0xF
        decoder.Process(Write(0, 0x40)); // bit 6 must not enter the period
        decoder.Process(Write(0, 0x90)); // volume 0 (audible)
        decoder.Complete(1_000);

        NoteEvent note = Assert.Single(timeline.Build(1_000).Notes);
        // Hand calculation: period = 0x00F = 15,
        // frequency = 3,579,545 / (32 * 15) = 7457.385416667 Hz,
        // MIDI = 69 + 12*log2(frequency / 440) = 117.997133721.
        AssertPitch(note, 7457.385416667, 117.997133721);
    }

    [Fact]
    public void TonePeriod_SuccessivePeriodsReplaceHighBitsWithoutStaleState()
    {
        var (decoder, timeline) = NewDecoder();
        decoder.Process(Write(0, 0x85)); // initial period = 0x2A5
        decoder.Process(Write(0, 0x2A));
        decoder.Process(Write(0, 0x90)); // start channel 0
        decoder.Process(Write(100, 0x85)); // same low nibble, no intermediate change
        decoder.Process(Write(100, 0x15)); // new period = 0x155
        decoder.Complete(1_000);

        NoteEvent note = Assert.Single(timeline.Build(1_000).Notes);
        AssertPitch(note, 165.230105244, 52.043676585);
        PitchChange change = Assert.Single(note.Pitch);
        // Hand calculation for period 0x155 = 341:
        // frequency = 3,579,545 / (32 * 341) = 328.037481672 Hz,
        // MIDI = 69 + 12*log2(frequency / 440) = 63.916285720.
        Assert.Equal(328.037481672, change.FrequencyHz, precision: 6);
        Assert.Equal(63.916285720, change.MidiNote, precision: 6);
    }

    [Fact]
    public void ToneVolumeWrites_PreserveHeldPeriod()
    {
        var (decoder, timeline) = NewDecoder();
        decoder.Process(Write(0, 0x85)); // period = 0x2A5
        decoder.Process(Write(0, 0x2A));
        decoder.Process(Write(0, 0x90)); // start channel 0
        decoder.Process(Write(100, 0x95)); // volume 5; period must remain 0x2A5
        decoder.Process(Write(100, 0x3F)); // continuation is ignored while volume latched
        decoder.Complete(1_000);

        NoteEvent note = Assert.Single(timeline.Build(1_000).Notes);
        AssertPitch(note, 165.230105244, 52.043676585);
        Assert.Empty(note.Pitch);
    }

    [Fact]
    public void TonePeriodChange_WhileGateOpenAddsPitchChangeWithoutRetrigger()
    {
        var (decoder, timeline) = NewDecoder();
        decoder.Process(Write(0, 0x80)); // period = 0x100
        decoder.Process(Write(0, 0x10));
        decoder.Process(Write(0, 0x90)); // open gate
        decoder.Process(Write(200, 0x80)); // re-latch tone channel after volume latch
        decoder.Process(Write(200, 0x20)); // period = 0x200 while gate remains open
        decoder.Complete(1_000);

        NoteEvent note = Assert.Single(timeline.Build(1_000).Notes);
        AssertPitch(note, 436.956176758, 68.879820868);
        Assert.False(note.IsRetrigger);
        PitchChange change = Assert.Single(note.Pitch);
        Assert.Equal(200, change.SamplePosition);
        Assert.Equal(218.478088379, change.FrequencyHz, precision: 6);
        Assert.Equal(56.879820868, change.MidiNote, precision: 6);
    }

    [Fact]
    public void TonePeriodZero_ProducesNoPitchedNote()
    {
        var (decoder, timeline) = NewDecoder();
        decoder.Process(Write(0, 0x80)); // explicit zero period
        decoder.Process(Write(0, 0x00));
        decoder.Process(Write(0, 0x90)); // audible volume cannot make zero pitched
        decoder.Complete(1_000);

        Assert.Empty(timeline.Build(1_000).Notes);
    }
}
