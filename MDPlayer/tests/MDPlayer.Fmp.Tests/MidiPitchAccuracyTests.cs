using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class MidiPitchAccuracyTests
{
    [Fact]
    public void PitchBendSensitivityRpn_ControlsTheFull14BitBend()
    {
        var state = new MidiChannelPitchState();
        SelectRpn(state, 0, 0);
        state.ApplyControlChange(6, 12);
        state.ApplyControlChange(38, 50);

        Assert.Equal(12.5, state.BendRangeSemitones, 10);

        state.ApplyPitchBend(0x7F, 0x7F);
        Assert.Equal(12.5 * 8191.0 / 8192.0, state.BendSemitones, 10);

        state.ApplyPitchBend(0, 0);
        Assert.Equal(-12.5, state.BendSemitones, 10);
    }

    [Fact]
    public void FineAndCoarseTuning_AreIncludedInChannelPitch()
    {
        var state = new MidiChannelPitchState();

        SelectRpn(state, 0, 2);
        state.ApplyControlChange(6, 65); // +1 semitone coarse

        SelectRpn(state, 0, 1);
        state.ApplyControlChange(6, 0x7F);
        state.ApplyControlChange(38, 0x7F); // almost +1 semitone fine

        Assert.Equal(1 + 8191.0 / 8192.0, state.PitchOffsetSemitones, 10);
    }

    [Fact]
    public void NullRpn_DisablesDataEntryUntilAnotherParameterIsSelected()
    {
        var state = new MidiChannelPitchState();
        SelectRpn(state, 0, 0);
        state.ApplyControlChange(6, 12);

        SelectRpn(state, 0x7F, 0x7F);
        state.ApplyControlChange(6, 24);
        state.ApplyControlChange(38, 50);

        Assert.Equal(12, state.BendRangeSemitones, 10);
    }

    [Fact]
    public void NrpnSelection_PreventsDataEntryFromChangingPitchRpn()
    {
        var state = new MidiChannelPitchState();
        SelectRpn(state, 0, 0);
        state.ApplyControlChange(6, 12);

        state.ApplyControlChange(99, 1);
        state.ApplyControlChange(98, 2);
        state.ApplyControlChange(6, 24);

        Assert.Equal(12, state.BendRangeSemitones, 10);
    }

    [Fact]
    public void ResetAllControllers_CentersPitchWheelWithoutResettingTuning()
    {
        var state = new MidiChannelPitchState();
        SelectRpn(state, 0, 2);
        state.ApplyControlChange(6, 65);
        state.ApplyPitchBend(0, 0);

        Assert.True(state.ApplyControlChange(121, 0));
        Assert.Equal(1, state.PitchOffsetSemitones, 10);
        Assert.Equal(0x2000, state.PitchBendValue);
    }

    [Fact]
    public void TimelineUsesRpnRangeForActiveNotes()
    {
        const int sampleRate = 48_000;
        DeviceDescriptor device = VisualizationDeviceCatalog.Midi();
        var timeline = new TimelineBuilder(sampleRate);
        var decoder = new MidiTimelineDecoder();
        decoder.Initialize(device, timeline);

        Send(decoder, device, 0, MidiMessageType.ControlChange, 101, 0);
        Send(decoder, device, 1, MidiMessageType.ControlChange, 100, 0);
        Send(decoder, device, 2, MidiMessageType.ControlChange, 6, 12);
        Send(decoder, device, 10, MidiMessageType.NoteOn, 60, 100);
        Send(decoder, device, 20, MidiMessageType.PitchBend, 0x7F, 0x7F);
        Send(decoder, device, 100, MidiMessageType.NoteOff, 60, 0);
        decoder.Complete(120);

        NoteEvent note = Assert.Single(timeline.Build(120).Notes);
        PitchChange bend = Assert.Single(note.Pitch);
        Assert.Equal(20, bend.SamplePosition);
        Assert.Equal(60 + 12 * 8191.0 / 8192.0, bend.MidiNote, 10);
    }

    [Fact]
    public void ChangingBendRangeWhileWheelIsDeflected_RetunesActiveNote()
    {
        DeviceDescriptor device = VisualizationDeviceCatalog.Midi();
        var timeline = new TimelineBuilder(48_000);
        var decoder = new MidiTimelineDecoder();
        decoder.Initialize(device, timeline);

        Send(decoder, device, 0, MidiMessageType.NoteOn, 60, 100);
        Send(decoder, device, 10, MidiMessageType.PitchBend, 0x7F, 0x7F);
        Send(decoder, device, 20, MidiMessageType.ControlChange, 101, 0);
        Send(decoder, device, 21, MidiMessageType.ControlChange, 100, 0);
        Send(decoder, device, 22, MidiMessageType.ControlChange, 6, 12);
        Send(decoder, device, 100, MidiMessageType.NoteOff, 60, 0);
        decoder.Complete(120);

        PitchChange[] pitch = Assert.Single(timeline.Build(120).Notes).Pitch.ToArray();
        Assert.Equal(2, pitch.Length);
        Assert.Equal(60 + 2 * 8191.0 / 8192.0, pitch[0].MidiNote, 10);
        Assert.Equal(60 + 12 * 8191.0 / 8192.0, pitch[1].MidiNote, 10);
    }

    private static void SelectRpn(MidiChannelPitchState state, int msb, int lsb)
    {
        state.ApplyControlChange(101, msb);
        state.ApplyControlChange(100, lsb);
    }

    private static void Send(
        MidiTimelineDecoder decoder,
        DeviceDescriptor device,
        long sample,
        MidiMessageType type,
        int data1,
        int data2)
    {
        decoder.Process(new TimedMidiMessage(
            sample,
            device.Id,
            0,
            0,
            type,
            data1,
            data2));
    }
}
