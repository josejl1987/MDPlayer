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
    private static double ExpectedFrequency(int frequencyRegister) =>
        ClockHz / (32.0 * (frequencyRegister + 1));

    private static double ExpectedMidiNote(int frequencyRegister)
    {
        double frequency = ExpectedFrequency(frequencyRegister);
        return 69.0 + 12.0 * Math.Log2(frequency / 440.0);
    }

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
        // The low byte (sample 100) and high byte (sample 200) form ONE
        // 12-bit register value. The note starts at the pair's first write
        // with the final physical pitch.
        var (decoder, timeline) = NewDecoder();
        KeyOn(decoder, 1, 0);
        Select(decoder, 100, 0x02);
        decoder.Process(W(100, 3, 0x40));
        Select(decoder, 200, 0x03);
        decoder.Process(W(200, 3, 0x02));
        decoder.Complete(10_000);

        NoteEvent note = Assert.Single(timeline.Build(10_000).Notes);
        Assert.Equal(100, note.StartSample);
        Assert.Equal(10_000, note.EndSample);
        Assert.Equal(ExpectedFrequency(0x240), note.InitialFrequencyHz, precision: 10);
        Assert.Equal(ExpectedMidiNote(0x240), note.InitialMidiNote, precision: 10);
    }

    [Fact]
    public void StandaloneLowByte_SettlesAtOwnWrite()
    {
        // A low byte never followed by its high byte is a standalone update;
        // a later volume write proves the driver moved on and settles it at
        // the low-byte write's sample.
        var (decoder, timeline) = NewDecoder();
        KeyOn(decoder, 1, 0);
        Select(decoder, 100, 0x02);
        decoder.Process(W(100, 3, 0x40));
        decoder.Process(W(300, 5, 0x0F));
        decoder.Complete(10_000);

        NoteEvent note = Assert.Single(timeline.Build(10_000).Notes);
        Assert.Equal(100, note.StartSample);
        Assert.Equal(ExpectedFrequency(0x040), note.InitialFrequencyHz, precision: 10);
    }

    [Fact]
    public void FrequencyChangeWhileOpen_AddsPitchWithoutSplittingNote()
    {
        var (decoder, timeline) = NewDecoder();
        KeyOn(decoder, 1, 0);
        WriteFrequencyPair(decoder, 1, 0x240, 0);
        WriteFrequencyPair(decoder, 1, 0x300, 1_000);
        decoder.Process(W(5_000, 7, 0x00));
        decoder.Complete(10_000);

        NoteEvent note = Assert.Single(timeline.Build(10_000).Notes);
        Assert.Equal(0, note.StartSample);
        Assert.Equal(5_000, note.EndSample);
        Assert.Equal(ExpectedFrequency(0x240), note.InitialFrequencyHz, precision: 10);
        Assert.Equal(ExpectedMidiNote(0x240), note.InitialMidiNote, precision: 10);
        PitchChange change = Assert.Single(note.Pitch);
        Assert.Equal(1_000, change.SamplePosition);
        Assert.Equal(ExpectedFrequency(0x300), change.FrequencyHz, precision: 10);
        Assert.Equal(ExpectedMidiNote(0x300), change.MidiNote, precision: 10);
    }

    [Fact]
    public void MultipleFrequencyChangesWhileOpen_StayInOneNote()
    {
        var (decoder, timeline) = NewDecoder();
        KeyOn(decoder, 1, 0);
        WriteFrequencyPair(decoder, 1, 0x240, 0);
        WriteFrequencyPair(decoder, 1, 0x300, 1_000);
        WriteFrequencyPair(decoder, 1, 0x180, 2_000);
        decoder.Process(W(5_000, 7, 0x00));
        decoder.Complete(10_000);

        NoteEvent note = Assert.Single(timeline.Build(10_000).Notes);
        Assert.Equal(2, note.Pitch.Count);
        Assert.Equal(1_000, note.Pitch[0].SamplePosition);
        Assert.Equal(ExpectedFrequency(0x300), note.Pitch[0].FrequencyHz, precision: 10);
        Assert.Equal(2_000, note.Pitch[1].SamplePosition);
        Assert.Equal(ExpectedFrequency(0x180), note.Pitch[1].FrequencyHz, precision: 10);
    }

    [Fact]
    public void KeyOff_ClosesOpenNote()
    {
        var (decoder, timeline) = NewDecoder();
        KeyOn(decoder, 1, 0);
        WriteFrequencyPair(decoder, 1, 0x240, 0);
        decoder.Process(W(2_000, 7, 0x00));
        decoder.Complete(10_000);

        NoteEvent note = Assert.Single(timeline.Build(10_000).Notes);
        Assert.Equal(0, note.StartSample);
        Assert.Equal(2_000, note.EndSample);
    }

    [Fact]
    public void KeyOnAfterOff_StartsAnotherAttack()
    {
        var (decoder, timeline) = NewDecoder();
        KeyOn(decoder, 1, 0);
        WriteFrequencyPair(decoder, 1, 0x240, 0);
        decoder.Process(W(2_000, 7, 0x00));
        KeyOn(decoder, 1, 3_000);
        decoder.Complete(5_000);

        NoteEvent[] notes = timeline.Build(5_000).Notes.ToArray();
        Assert.Equal(2, notes.Length);
        Assert.Equal(0, notes[0].StartSample);
        Assert.Equal(2_000, notes[0].EndSample);
        Assert.Equal(3_000, notes[1].StartSample);
        Assert.Equal(5_000, notes[1].EndSample);
    }

    [Fact]
    public void Retrigger_ClosesAndReopensAtSameSample()
    {
        var (decoder, timeline) = NewDecoder();
        KeyOn(decoder, 1, 0);
        WriteFrequencyPair(decoder, 1, 0x240, 0);
        decoder.Process(W(2_000, 7, 0x00));
        KeyOn(decoder, 1, 2_000);
        decoder.Complete(5_000);

        NoteEvent[] notes = timeline.Build(5_000).Notes.ToArray();
        Assert.Equal(2, notes.Length);
        Assert.Equal(2_000, notes[0].EndSample);
        Assert.Equal(2_000, notes[1].StartSample);
    }

    [Fact]
    public void DisabledPeriod_ProducesNoNote()
    {
        var (decoder, timeline) = NewDecoder();
        KeyOn(decoder, 0, 0);
        WriteFrequencyPair(decoder, 0, 0x000, 100);
        decoder.Complete(10_000);

        Assert.Empty(timeline.Build(10_000).Notes);
    }

    [Fact]
    public void VeryLowPeriod_ProducesNoNote()
    {
        var (decoder, timeline) = NewDecoder();
        KeyOn(decoder, 0, 0);
        WriteFrequencyPair(decoder, 0, 0x001, 100);
        decoder.Complete(10_000);

        Assert.Empty(timeline.Build(10_000).Notes);
    }

    [Fact]
    public void KeyMaskOff_ProducesNoNote()
    {
        var (decoder, timeline) = NewDecoder();
        decoder.Process(W(0, 7, 0x00));
        decoder.Process(W(0, 5, 0x0F));
        WriteFrequencyPair(decoder, 0, 0x240, 0);
        decoder.Complete(10_000);

        Assert.Empty(timeline.Build(10_000).Notes);
    }
}
