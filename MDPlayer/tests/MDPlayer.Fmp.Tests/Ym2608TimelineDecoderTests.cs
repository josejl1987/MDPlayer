using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class Ym2608TimelineDecoderTests
{
    [Fact]
    public void TimerBWritesAreCapturedWithoutInventingTempo()
    {
        var decoder = new Ym2608TimelineDecoder();
        decoder.ApplyYm2608(0, 0, 0x26, 0x42, 123);

        VisualizationTimeline timeline = decoder.Complete(200, 44_100, "test");
        DriverTimingEvent timing = Assert.Single(timeline.Timing);
        Assert.Equal(123, timing.SamplePosition);
        Assert.Equal(0x42, timing.TimerBValue);
        Assert.Null(timing.ValidatedBpm);
    }

    [Fact]
    public void FmKeyOnAndKeyOff_CreateOneEndExclusiveNote()
    {
        var decoder = new Ym2608TimelineDecoder();
        WriteFmPitch(decoder, 0, 100, 0x135, 4);
        decoder.ApplyYm2608(0, 0, 0x28, 0xF0, 200);
        decoder.ApplyYm2608(0, 0, 0x28, 0x00, 1000);

        var timeline = decoder.Complete(1200, 44_100, "test");
        var note = Assert.Single(timeline.Notes.Where(note => note.ChannelId == "ym2608.0.fm.1"));

        Assert.Equal(200, note.StartSample);
        Assert.Equal(1000, note.EndSample);
        Assert.Equal(VisualizationNoteMode.Fm, note.Mode);
        Assert.False(note.IsRetrigger);
        Assert.InRange(note.InitialMidiNote, 59.9, 60.1);
    }

    [Fact]
    public void FmPitchWrite_AddsPitchPointWithoutSplittingNote()
    {
        var decoder = new Ym2608TimelineDecoder();
        WriteFmPitch(decoder, 0, 100, 0x135, 4);
        decoder.ApplyYm2608(0, 0, 0x28, 0xF0, 200);
        WriteFmPitch(decoder, 0, 500, 0x180, 4);
        decoder.ApplyYm2608(0, 0, 0x28, 0x00, 900);

        var timeline = decoder.Complete(1000, 44_100, "test");
        var note = Assert.Single(timeline.Notes.Where(note => note.ChannelId == "ym2608.0.fm.1"));

        var pitch = Assert.Single(note.Pitch);
        Assert.Equal(500, pitch.SamplePosition);
        Assert.NotEqual(note.InitialMidiNote, pitch.MidiNote);
    }

    [Fact]
    public void FmRetrigger_ClosesPreviousBeforeOpeningNext()
    {
        var decoder = new Ym2608TimelineDecoder();
        WriteFmPitch(decoder, 0, 0, 0x135, 4);
        decoder.ApplyYm2608(0, 0, 0x28, 0xF0, 100);
        decoder.ApplyYm2608(0, 0, 0x28, 0xF0, 500);
        decoder.ApplyYm2608(0, 0, 0x28, 0x00, 900);

        var notes = decoder.Complete(1000, 44_100, "test").Notes
            .Where(note => note.ChannelId == "ym2608.0.fm.1")
            .OrderBy(note => note.StartSample)
            .ToArray();

        Assert.Equal(2, notes.Length);
        Assert.Equal(100, notes[0].StartSample);
        Assert.Equal(500, notes[0].EndSample);
        Assert.Equal(500, notes[1].StartSample);
        Assert.Equal(900, notes[1].EndSample);
        Assert.True(notes[1].IsRetrigger);
    }

    [Fact]
    public void FmPatchId_IsSnapshottedAtKeyOn()
    {
        var decoder = new Ym2608TimelineDecoder();
        WriteFmPitch(decoder, 0, 0, 0x135, 4);
        decoder.ApplyYm2608(0, 0, 0x30, 0x01, 10);
        decoder.ApplyYm2608(0, 0, 0x28, 0xF0, 100);
        decoder.ApplyYm2608(0, 0, 0x30, 0x07, 300);
        decoder.ApplyYm2608(0, 0, 0x28, 0x00, 500);
        decoder.ApplyYm2608(0, 0, 0x28, 0xF0, 600);
        decoder.ApplyYm2608(0, 0, 0x28, 0x00, 800);

        var notes = decoder.Complete(900, 44_100, "test").Notes
            .Where(note => note.ChannelId == "ym2608.0.fm.1")
            .OrderBy(note => note.StartSample)
            .ToArray();

        Assert.Equal(2, notes.Length);
        Assert.NotEqual(notes[0].InstrumentId, notes[1].InstrumentId);
    }

    [Fact]
    public void PanChange_DoesNotChangePatchId()
    {
        var decoder = new Ym2608TimelineDecoder();
        WriteFmPitch(decoder, 0, 0, 0x135, 4);
        decoder.ApplyYm2608(0, 0, 0xB4, 0x00, 10);
        decoder.ApplyYm2608(0, 0, 0x28, 0xF0, 100);
        decoder.ApplyYm2608(0, 0, 0x28, 0x00, 300);
        decoder.ApplyYm2608(0, 0, 0xB4, 0xC0, 400);
        decoder.ApplyYm2608(0, 0, 0x28, 0xF0, 500);
        decoder.ApplyYm2608(0, 0, 0x28, 0x00, 700);

        var notes = decoder.Complete(800, 44_100, "test").Notes
            .Where(note => note.ChannelId == "ym2608.0.fm.1")
            .OrderBy(note => note.StartSample)
            .ToArray();

        Assert.Equal(notes[0].InstrumentId, notes[1].InstrumentId);
    }

    [Fact]
    public void AmsAndFmsChangePatchIdentity()
    {
        var decoder = new Ym2608TimelineDecoder();
        WriteFmPitch(decoder, 0, 0, 0x135, 4);
        decoder.ApplyYm2608(0, 0, 0xB4, 0x00, 10);
        decoder.ApplyYm2608(0, 0, 0x28, 0xF0, 100);
        decoder.ApplyYm2608(0, 0, 0x28, 0x00, 300);
        decoder.ApplyYm2608(0, 0, 0xB4, 0x31, 400);
        decoder.ApplyYm2608(0, 0, 0x28, 0xF0, 500);
        decoder.ApplyYm2608(0, 0, 0x28, 0x00, 700);

        var notes = decoder.Complete(800, 44_100, "test").Notes
            .Where(note => note.ChannelId == "ym2608.0.fm.1")
            .OrderBy(note => note.StartSample)
            .ToArray();

        Assert.NotEqual(notes[0].InstrumentId, notes[1].InstrumentId);
    }

    [Fact]
    public void SsgTone_CreatesPitchedNoteAndPitchCurve()
    {
        var decoder = new Ym2608TimelineDecoder();
        decoder.ApplyYm2608(0, 0, 0x07, 0x3E, 10);
        decoder.ApplyYm2608(0, 0, 0x00, 0x58, 20);
        decoder.ApplyYm2608(0, 0, 0x01, 0x01, 20);
        decoder.ApplyYm2608(0, 0, 0x08, 0x0F, 30);
        decoder.ApplyYm2608(0, 0, 0x00, 0x20, 100);
        decoder.ApplyYm2608(0, 0, 0x08, 0x00, 300);

        var note = Assert.Single(decoder.Complete(400, 44_100, "test").Notes);
        Assert.Equal(VisualizationNoteMode.SsgTone, note.Mode);
        Assert.True(note.InitialMidiNote > 0);
        Assert.Single(note.Pitch);
        Assert.Equal("ssg:tone", note.InstrumentId);
    }

    [Fact]
    public void SsgNoiseOnly_CreatesUnpitchedNoiseNote()
    {
        var decoder = new Ym2608TimelineDecoder();
        decoder.ApplyYm2608(0, 0, 0x07, 0x37, 10);
        decoder.ApplyYm2608(0, 0, 0x08, 0x0F, 20);
        decoder.ApplyYm2608(0, 0, 0x08, 0x00, 300);

        var note = Assert.Single(decoder.Complete(400, 44_100, "test").Notes);
        Assert.Equal(VisualizationNoteMode.SsgNoise, note.Mode);
        Assert.Equal(-1.0, note.InitialMidiNote);
        Assert.Empty(note.Pitch);
        Assert.Equal("ssg:noise", note.InstrumentId);
    }

    [Fact]
    public void RhythmRegister10_ProducesCorrectVoiceTriggers()
    {
        var decoder = new Ym2608TimelineDecoder();
        decoder.ApplyYm2608(0, 0, 0x11, 0x00, 10);
        decoder.ApplyYm2608(0, 0, 0x18, 0xC0, 20);
        decoder.ApplyYm2608(0, 0, 0x19, 0xC0, 20);
        decoder.ApplyYm2608(0, 0, 0x10, 0x03, 100);

        var events = decoder.Complete(200, 44_100, "test").Rhythm;
        Assert.Equal(2, events.Count);
        Assert.Contains(events, evt => evt.Voice == "bd");
        Assert.Contains(events, evt => evt.Voice == "sd");
    }

    [Fact]
    public void RhythmLevelRegisters_DoNotTriggerEvents()
    {
        var decoder = new Ym2608TimelineDecoder();
        for (int address = 0x18; address <= 0x1D; address++)
            decoder.ApplyYm2608(0, 0, address, 0xC0, address);

        Assert.Empty(decoder.Complete(100, 44_100, "test").Rhythm);
    }

    [Fact]
    public void RhythmDumpCommand_DoesNotTriggerEvents()
    {
        var decoder = new Ym2608TimelineDecoder();
        decoder.ApplyYm2608(0, 0, 0x10, 0x81, 100);
        Assert.Empty(decoder.Complete(200, 44_100, "test").Rhythm);
    }

    [Fact]
    public void Complete_ClosesEveryOpenNoteAtTimelineEnd()
    {
        var decoder = new Ym2608TimelineDecoder();
        WriteFmPitch(decoder, 0, 0, 0x135, 4);
        decoder.ApplyYm2608(0, 0, 0x28, 0xF0, 100);

        var note = Assert.Single(decoder.Complete(1000, 44_100, "test").Notes);
        Assert.Equal(1000, note.EndSample);
    }

    [Fact]
    public void OrdinaryChannelMainNotesNeverOverlap()
    {
        var decoder = new Ym2608TimelineDecoder();
        WriteFmPitch(decoder, 0, 0, 0x135, 4);
        decoder.ApplyYm2608(0, 0, 0x28, 0xF0, 100);
        decoder.ApplyYm2608(0, 0, 0x28, 0xF0, 200);
        decoder.ApplyYm2608(0, 0, 0x28, 0xF0, 300);
        decoder.ApplyYm2608(0, 0, 0x28, 0x00, 400);

        var notes = decoder.Complete(500, 44_100, "test").Notes
            .Where(note => note.ChannelId == "ym2608.0.fm.1")
            .OrderBy(note => note.StartSample)
            .ToArray();

        for (int index = 1; index < notes.Length; index++)
            Assert.True(notes[index - 1].EndSample <= notes[index].StartSample);
    }

    [Fact]
    public void Fm3SpecialMode_UsesOperatorChannelsNotMainFm3()
    {
        var decoder = new Ym2608TimelineDecoder();
        WriteFmPitch(decoder, 2, 0, 0x135, 4);
        decoder.ApplyYm2608(0, 0, 0x27, 0x40, 50);
        decoder.ApplyYm2608(0, 0, 0x28, 0x42, 100);
        decoder.ApplyYm2608(0, 0, 0x28, 0x02, 300);

        var notes = decoder.Complete(400, 44_100, "test").Notes;
        Assert.DoesNotContain(notes, note => note.ChannelId == "ym2608.0.fm.3");
        Assert.Contains(notes, note => note.ChannelId == "ym2608.0.fm3.op.3");
    }

    private static void WriteFmPitch(
        Ym2608TimelineDecoder decoder,
        int channel,
        long sample,
        int fNumber,
        int block)
    {
        int port = channel >= 3 ? 1 : 0;
        int localChannel = channel % 3;
        decoder.ApplyYm2608(0, port, 0xA0 + localChannel, fNumber & 0xFF, sample);
        decoder.ApplyYm2608(0, port, 0xA4 + localChannel,
            ((block & 0x07) << 3) | ((fNumber >> 8) & 0x07), sample);
    }
}
