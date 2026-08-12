using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class Huc6280TimelineDecoderTests
{
    /// <summary>A4-ish period (0x100) at the 3 579 545 Hz clock divider 32.</summary>
    private const uint Period = 0x100;

    private static (Huc6280TimelineDecoder Decoder, TimelineBuilder Builder) NewDecoder()
    {
        var timeline = new TimelineBuilder(44_100);
        var decoder = new Huc6280TimelineDecoder();
        decoder.Initialize(VisualizationDeviceCatalog.Huc6280(), timeline);
        return (decoder, timeline);
    }

    private static void Select(Huc6280TimelineDecoder decoder, long sample, int channel) =>
        decoder.Process(new TimedChipWrite(sample, new DeviceId(ChipType.Huc6280, 0), 0, 0, channel & 7));

    private static void WriteFrequency(Huc6280TimelineDecoder decoder, long sample, uint period = Period)
    {
        decoder.Process(new TimedChipWrite(sample, new DeviceId(ChipType.Huc6280, 0), 0, 2, (int)(period & 0xFF)));
        decoder.Process(new TimedChipWrite(sample, new DeviceId(ChipType.Huc6280, 0), 0, 3, (int)((period >> 8) & 0x0F)));
    }

    private static void WriteReg(in TimedChipWrite write, Huc6280TimelineDecoder decoder) =>
        decoder.Process(write);

    private static TimedChipWrite W(long sample, int address, int data) =>
        new(sample, new DeviceId(ChipType.Huc6280, 0), 0, address, data);

    [Fact]
    public void Tone_WithoutOnBit_ProducesNoNote()
    {
        var (decoder, timeline) = NewDecoder();
        Select(decoder, 0, 0);
        WriteFrequency(decoder, 0);
        decoder.Process(W(0, 5, 0x11));   // LAL=1, RAL=1
        decoder.Process(W(0, 4, 0x1F));   // AL but ON bit clear
        decoder.Complete(10_000);

        Assert.Empty(timeline.Build(10_000).Notes);
    }

    [Fact]
    public void Tone_WithOnBit_CreatesSsgToneNote()
    {
        var (decoder, timeline) = NewDecoder();
        Select(decoder, 0, 0);
        WriteFrequency(decoder, 0);
        decoder.Process(W(0, 5, 0x11));
        decoder.Process(W(0, 4, 0x9F));   // ON=1, AL=0x1F

        decoder.Process(W(5_000, 4, 0x1F)); // ON cleared
        decoder.Complete(10_000);

        NoteEvent note = Assert.Single(timeline.Build(10_000).Notes);
        Assert.Equal("huc6280.0.wavetable.1", note.ChannelId);
        Assert.Equal(VisualizationNoteMode.SsgTone, note.Mode);
        Assert.InRange(note.InitialMidiNote, 68.0, 70.0);
    }

    [Fact]
    public void Tone_WhileDdaEnabled_ProducesNoNote()
    {
        var (decoder, timeline) = NewDecoder();
        Select(decoder, 0, 0);
        WriteFrequency(decoder, 0);
        decoder.Process(W(0, 5, 0x11));
        decoder.Process(W(0, 4, 0xDF));   // ON=1, DDA=1, AL
        decoder.Complete(10_000);

        Assert.Empty(timeline.Build(10_000).Notes);
    }

    [Fact]
    public void KeyOff_ClosesOpenNote()
    {
        var (decoder, timeline) = NewDecoder();
        Select(decoder, 0, 0);
        WriteFrequency(decoder, 0);
        decoder.Process(W(0, 5, 0x11));
        decoder.Process(W(0, 4, 0x9F));   // ON=1
        decoder.Process(W(0, 4, 0x1F));   // same freq, volume, ON cleared
        decoder.Complete(10_000);

        Assert.Empty(timeline.Build(10_000).Notes);
    }

    [Fact]
    public void NoiseOn_ChannelFour_CreatesSsgNoiseUnpitchedNote()
    {
        var (decoder, timeline) = NewDecoder();
        Select(decoder, 0, 4);
        WriteFrequency(decoder, 0);
        decoder.Process(W(0, 5, 0x11));
        decoder.Process(W(0, 4, 0x9F));   // ON=1, AL
        decoder.Process(W(0, 7, 0x80));   // noise enable (NE)
        decoder.Complete(10_000);

        NoteEvent note = Assert.Single(timeline.Build(10_000).Notes);
        Assert.Equal("huc6280.0.wavetable.5", note.ChannelId);
        Assert.Equal(VisualizationNoteMode.SsgNoise, note.Mode);
        Assert.Equal(-1, note.InitialMidiNote);
        Assert.Equal(0, note.InitialFrequencyHz);
    }

    [Fact]
    public void NoiseEnableThenDisable_RestartsAsTone()
    {
        var (decoder, timeline) = NewDecoder();
        Select(decoder, 0, 4);
        WriteFrequency(decoder, 0);
        decoder.Process(W(0, 5, 0x11));
        decoder.Process(W(0, 4, 0x9F));
        decoder.Process(W(0, 7, 0x80));       // noise on
        decoder.Process(W(2_000, 7, 0x00));   // noise off
        decoder.Complete(10_000);

        NoteEvent[] notes = timeline.Build(10_000).Notes
            .Where(note => note.ChannelId == "huc6280.0.wavetable.5")
            .OrderBy(note => note.StartSample)
            .ToArray();

        Assert.Equal(2, notes.Length);
        Assert.Equal(VisualizationNoteMode.SsgNoise, notes[0].Mode);
        Assert.Equal(VisualizationNoteMode.SsgTone, notes[1].Mode);
        Assert.Equal(0, notes[0].StartSample);
        Assert.Equal(2_000, notes[0].EndSample);
        Assert.Equal(2_000, notes[1].StartSample);
    }

    [Fact]
    public void NoiseWriteOnLowChannel_IsIgnored()
    {
        var (decoder, timeline) = NewDecoder();
        Select(decoder, 0, 0);
        WriteFrequency(decoder, 0);
        decoder.Process(W(0, 5, 0x11));
        decoder.Process(W(0, 4, 0x9F));
        decoder.Process(W(0, 7, 0x80));   // noise write on channel 0: no effect
        decoder.Complete(10_000);

        NoteEvent note = Assert.Single(timeline.Build(10_000).Notes);
        Assert.Equal(VisualizationNoteMode.SsgTone, note.Mode);
    }

    [Fact]
    public void FrequencyChange_WhileOn_AddsPitchWithoutSplittingNote()
    {
        var (decoder, timeline) = NewDecoder();
        Select(decoder, 0, 0);
        WriteFrequency(decoder, 0, 0x100);
        decoder.Process(W(0, 5, 0x11));
        decoder.Process(W(0, 4, 0x9F));       // ON, note opens at 0x100
        WriteFrequency(decoder, 2_000, 0x80);  // smaller period -> higher pitch
        decoder.Process(W(5_000, 4, 0x1F));
        decoder.Complete(10_000);

        NoteEvent[] notes = timeline.Build(10_000).Notes
            .Where(note => note.ChannelId == "huc6280.0.wavetable.1")
            .ToArray();

        var note = Assert.Single(notes);
        Assert.False(note.IsRetrigger);
        Assert.InRange(note.InitialMidiNote, 68.0, 70.0); // period 0x100
        Assert.NotEmpty(note.Pitch);
        // The final pitch point reflects the settled period 0x80.
        Assert.InRange(note.Pitch[^1].MidiNote, 80.0, 82.0);
        Assert.Equal(2_000, note.Pitch[^1].SamplePosition);
    }
}