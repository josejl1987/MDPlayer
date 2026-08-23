using Fmp.Core.Midi;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class MidiChannelProgramTests
{
    [Fact]
    public void Seal_UsesOneDeterministicSameTickOrder()
    {
        var program = new MidiChannelProgram("voice-a", midiChannel: 3, bendRange: 12);
        var noteOn = new MidiNoteEvent(100, 0, 3, 60, 96, NoteOn: true);
        var controller = new MidiControlChangeEvent(100, 0, 3, 64, 127);
        var bend = new MidiPitchBendEvent(100, 0, 3, 512);
        var bank = new MidiBankEvent(100, 0, 3, 4);
        var noteOff = new MidiNoteEvent(100, 0, 3, 60, 96, NoteOn: false);
        var range = new MidiBendRangeEvent(0, 0, 3, 12);

        program.Append(noteOn);
        program.Append(controller);
        program.Append(bend);
        program.Append(bank);
        program.Append(noteOff);
        program.Append(range);

        program.Seal();

        Assert.Collection(
            program.OrderedEvents,
            evt => Assert.Same(range, evt),
            evt => Assert.Same(noteOff, evt),
            evt => Assert.Same(bank, evt),
            evt => Assert.Same(bend, evt),
            evt => Assert.Same(noteOn, evt),
            evt => Assert.Same(controller, evt));
    }

    [Fact]
    public void Validate_RejectsSecondBendRangeInitialization()
    {
        var program = new MidiChannelProgram("voice-a", midiChannel: 0, bendRange: 12);
        program.Append(new MidiBendRangeEvent(0, 0, 0, 12));
        program.Append(new MidiBendRangeEvent(0, 0, 0, 12));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(program.Seal);

        Assert.Contains("initializes bend range more than once", error.Message);
    }

    [Fact]
    public void Validate_RejectsEventOwnedByAnotherChannel()
    {
        var program = new MidiChannelProgram("voice-a", midiChannel: 3, bendRange: 0);
        program.Append(new MidiNoteEvent(0, 0, 2, 60, 96, NoteOn: true));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(program.Seal);

        Assert.Contains("owns channel 3", error.Message);
    }

    [Fact]
    public void Validate_RejectsPitchBendWithoutRangeInitialization()
    {
        var program = new MidiChannelProgram("voice-a", midiChannel: 0, bendRange: 12);
        program.Append(new MidiPitchBendEvent(0, 0, 0, 1));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(program.Seal);

        Assert.Contains("without exactly one range initialization", error.Message);
    }
}
