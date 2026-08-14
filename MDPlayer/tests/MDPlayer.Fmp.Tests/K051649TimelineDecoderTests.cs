using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Unit tests for the K051649 (SCC) decoder's frequency-pair model (INV4) and
/// pitch gate (INV5): the 12-bit frequency register is written as two byte-port
/// writes, and the intermediate value between them must never become a note
/// boundary — the pair reconciles ONCE, at the first write of the pair, with the
/// final pitch. Disabled/ultrasonic periods never produce notes.
/// </summary>
public sealed class K051649TimelineDecoderTests
{
    private const long ClockHz = 3_579_545;

    private static (K051649TimelineDecoder Decoder, TimelineBuilder Builder) NewDecoder()
    {
        var timeline = new TimelineBuilder(44_100);
        var decoder = new K051649TimelineDecoder();
        decoder.Initialize(VisualizationDeviceCatalog.K051649(0, ClockHz), timeline);
        return (decoder, timeline);
    }

    private static TimedChipWrite W(long sample, int address, int data) =>
        new(sample, new DeviceId(ChipType.K051649, 0), 0, address, data);

    private static void Select(K051649TimelineDecoder decoder, long sample, int register) =>
        decoder.Process(W(sample, 0, register));

    /// <summary>Writes a 12-bit frequency as the canonical low-then-high pair.</summary>
    private static void WriteFrequencyPair(K051649TimelineDecoder decoder, int channel, int frequency, long sample)
    {
        Select(decoder, sample, channel * 2);      // low-byte register
        decoder.Process(W(sample, 3, frequency & 0xFF));
        Select(decoder, sample, channel * 2 + 1);  // high-byte register
        decoder.Process(W(sample, 3, (frequency >> 8) & 0x0F));
    }

    private static void KeyOn(K051649TimelineDecoder decoder, int channel, long sample, int volume = 0x0F)
    {
        decoder.Process(W(sample, 7, 0x1F));     // key mask: all channels on
        Select(decoder, sample, channel & 0x07); // volume register for this channel
        decoder.Process(W(sample, 5, volume));
    }

    [Fact]
    public void FrequencyPair_SettlesAtFirstWrite_NoIntermediateBoundary()
    {
        // INV4: the low byte (sample 100) and high byte (sample 200) form ONE
        // 12-bit register value (0x240). The note must start at the pair's FIRST
        // write with the FINAL pitch — the intermediate state (0x040 between the
        // two bytes) is a chip-glitch state and must never create a note boundary.
        var (decoder, timeline) = NewDecoder();
        KeyOn(decoder, 1, 0);
        Select(decoder, 100, 0x02);            // ch 1, low byte
        decoder.Process(W(100, 3, 0x40));
        Select(decoder, 200, 0x03);            // ch 1, high byte
        decoder.Process(W(200, 3, 0x02));
        decoder.Complete(10_000);

        NoteEvent note = Assert.Single(timeline.Build(10_000).Notes);
        Assert.Equal(100, note.StartSample);   // reconciled at the pair start
        Assert.Equal(10_000, note.EndSample);  // no boundary at 200
        Assert.InRange(note.InitialMidiNote, 76.0, 79.0);
    }

    [Fact]
    public void StandaloneLowByte_SettlesAtOwnWrite()
    {
        // A low byte never followed by its high byte is a STANDALONE frequency
        // update; a later volume write proves the driver moved on and settles it
        // at its own sample.
        var (decoder, timeline) = NewDecoder();
        KeyOn(decoder, 1, 0);
        Select(decoder, 100, 0x02);
        decoder.Process(W(100, 3, 0x40));      // low byte → pending at 100
        decoder.Process(W(300, 5, 0x0F));      // volume write → settles the pair at 100
        decoder.Complete(10_000);

        NoteEvent note = Assert.Single(timeline.Build(10_000).Notes);
        Assert.Equal(100, note.StartSample);
    }

    [Fact]
    public void UltrasonicPeriod_ProducesNoNote()
    {
        // INV5: period 1 at 3.58 MHz decodes to ~447 kHz — ultrasonic. With the
        // key on and volume up it must still never become a MIDI note.
        var (decoder, timeline) = NewDecoder();
        KeyOn(decoder, 0, 0);
        Select(decoder, 100, 0x00);            // ch 0, low byte
        decoder.Process(W(100, 3, 0x01));
        Select(decoder, 200, 0x01);            // ch 0, high byte
        decoder.Process(W(200, 3, 0x00));      // freq 0x001
        decoder.Complete(10_000);

        Assert.Empty(timeline.Build(10_000).Notes);
    }

    [Fact]
    public void TonePeriodDisabled_ProducesNoNote()
    {
        // INV5: period 0 is the tone-disabled oscillator state — no note.
        var (decoder, timeline) = NewDecoder();
        KeyOn(decoder, 0, 0);
        Select(decoder, 100, 0x00);
        decoder.Process(W(100, 3, 0x00));
        Select(decoder, 200, 0x01);
        decoder.Process(W(200, 3, 0x00));      // freq 0x000
        decoder.Complete(10_000);

        Assert.Empty(timeline.Build(10_000).Notes);
    }

    [Fact]
    public void KeyMaskOff_ProducesNoNote()
    {
        var (decoder, timeline) = NewDecoder();
        decoder.Process(W(0, 7, 0x00));        // key mask: all off
        decoder.Process(W(0, 5, 0x0F));
        WriteFrequencyPair(decoder, 0, 0x240, 0);
        decoder.Complete(10_000);

        Assert.Empty(timeline.Build(10_000).Notes);
    }

    [Fact]
    public void FrequencyChangeWhileOpen_StablePlateauStartsNewNote()
    {
        var (decoder, timeline) = NewDecoder();
        KeyOn(decoder, 1, 0);
        WriteFrequencyPair(decoder, 1, 0x240, 0);
        WriteFrequencyPair(decoder, 1, 0x300, 500);
        WriteFrequencyPair(decoder, 1, 0x380, 1_000);
        decoder.Complete(10_000);

        NoteEvent[] notes = timeline.Build(10_000).Notes.ToArray();
        Assert.Equal(2, notes.Length);
        Assert.Equal(0, notes[0].StartSample);
        Assert.Equal(1_000, notes[0].EndSample);
        Assert.Equal(1, notes[0].Pitch.Count);
        Assert.Equal(500, notes[0].Pitch[0].SamplePosition);
        Assert.Equal(1_000, notes[1].StartSample);
        Assert.Equal(10_000, notes[1].EndSample);
    }

    [Fact]
    public void StableSemitonePlateau_SplitsSustainedGateIntoNotes()
    {
        var (decoder, timeline) = NewDecoder();
        KeyOn(decoder, 1, 0);
        WriteFrequencyPair(decoder, 1, 0x240, 0);
        WriteFrequencyPair(decoder, 1, 0x300, 100);
        WriteFrequencyPair(decoder, 1, 0x300, 200);
        WriteFrequencyPair(decoder, 1, 0x300, 700);
        decoder.Complete(10_000);

        NoteEvent[] notes = timeline.Build(10_000).Notes.ToArray();
        Assert.Equal(2, notes.Length);
        Assert.Equal(0, notes[0].StartSample);
        Assert.Equal(100, notes[0].EndSample);
        Assert.Equal(100, notes[1].StartSample);
        Assert.Equal(10_000, notes[1].EndSample);
    }

    [Fact]
    public void RapidIntermediateSemitone_DoesNotSplitNote()
    {
        var (decoder, timeline) = NewDecoder();
        KeyOn(decoder, 1, 0);
        WriteFrequencyPair(decoder, 1, 0x240, 0);
        WriteFrequencyPair(decoder, 1, 0x300, 100);
        WriteFrequencyPair(decoder, 1, 0x240, 200);
        decoder.Complete(10_000);

        NoteEvent note = Assert.Single(timeline.Build(10_000).Notes);
        Assert.Equal(10_000, note.EndSample);
    }
}
