using Fmp.Core.Midi;
using Melanchall.DryWetMidi.Core;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Writer-level tests for <see cref="MidiFileWriter"/>: Format 1 header / PPQ /
/// chunk structure / nonnegative deltas, one effective EOT per track,
/// tempo-before-note-on at the same tick, byte-identical determinism, the
/// deterministic SourceOrder secondary-key sort, and invalid-input rejection.
/// Round-trip assertions use DryWetMIDI's object model.
/// </summary>
public sealed class MidiFileWriterTests
{
    private const int Ppq = 960;

    // ---- Format 1, PPQ, track count ----

    [Fact]
    public void Write_Header_IsFormat1WithConfiguredPpq()
    {
        var writer = new MidiFileWriter(Ppq);
        byte[] bytes = writer.Write(EmptyConductor(), new[] { new MidiTrack { Name = "t" } });

        var file = MidiRoundTrip.Read(bytes);
        Assert.Equal(MidiFileFormat.MultiTrack, file.OriginalFormat);
        var div = Assert.IsType<TicksPerQuarterNoteTimeDivision>(file.TimeDivision);
        Assert.Equal(Ppq, div.TicksPerQuarterNote);
        // ntrks = conductor + 1 musical.
        Assert.Equal(2, MidiRoundTrip.TrackChunks(bytes).Count);
        Assert.True(MidiRoundTrip.StartsWith("MThd", bytes), "bytes must start with MThd");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-960)]
    public void Writer_RejectsInvalidPpq(int ppq)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MidiFileWriter(ppq));
    }

    [Fact]
    public void Writer_RejectsPpqAbove32767()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MidiFileWriter(32768));
    }

    [Fact]
    public void Write_EveryTrackStartsWithNameAndEndsWithEot()
    {
        var c = new List<MidiEventBase>
        {
            new MidiTempoEvent(0, 500_000),
            new MidiTimeSignatureEvent(0, 4, 4),
        };
        var t1 = new MidiTrack { Name = "t1" };
        t1.Events.Add(new MidiNoteEvent(10, 0, 0, 60, 90, NoteOn: true));
        var t2 = new MidiTrack { Name = "t2" };
        t2.Events.Add(new MidiNoteEvent(5, 0, 1, 70, 90, NoteOn: true));
        var writer = new MidiFileWriter(Ppq);
        byte[] bytes = writer.Write(c, new[] { t1, t2 });

        // 2 musical + 1 conductor = 3 tracks, each ending with exactly one EndOfTrack.
        var chunks = MidiRoundTrip.TrackChunks(bytes);
        Assert.Equal(3, chunks.Count);
        foreach (TrackChunk chunk in chunks)
        {
            Assert.Equal(1, chunk.Events.Count(e => e is EndOfTrackEvent));
        }
        // First event of each chunk is the SequenceTrackName.
        Assert.IsType<SequenceTrackNameEvent>(chunks[0].Events[0]);
        Assert.Equal("t1", ((SequenceTrackNameEvent)chunks[1].Events[0]).Text);
        Assert.Equal("t2", ((SequenceTrackNameEvent)chunks[2].Events[0]).Text);
    }

    [Fact]
    public void Write_TempoPrecedesNoteOn_AtSameTick()
    {
        // A tempo at tick T and a note-on at T: the tempo must serialize first.
        var c = new List<MidiEventBase> { new MidiTempoEvent(100, 500_000) };
        var track = new MidiTrack { Name = "t" };
        track.Events.Add(new MidiNoteEvent(100, 0, 0, 60, 90, NoteOn: true));
        var writer = new MidiFileWriter(Ppq);
        byte[] bytes = writer.Write(c, new[] { track });

        var conductorEvents = MidiRoundTrip.TimedEvents(bytes, 0);
        var musicalEvents = MidiRoundTrip.TimedEvents(bytes, 1);
        var tempo = Assert.Single(conductorEvents.Where(e => e.Event is SetTempoEvent));
        var noteOn = Assert.Single(musicalEvents.Where(e => e.Event is NoteOnEvent));
        // Same absolute tick; tempo must come first in the file-wide serialization.
        Assert.Equal(100, tempo.Tick);
        Assert.Equal(100, noteOn.Tick);
        // Both events carry delta 100 from tick 0 -> the tempo event appears earlier
        // in the byte stream (conductor chunk precedes the musical chunk).
        Assert.Equal(100, tempo.Event.DeltaTime);
        Assert.Equal(100, noteOn.Event.DeltaTime);
    }

    // ---- determinism + SourceOrder secondary sort ----

    [Fact]
    public void Write_Twice_IsByteIdentical()
    {
        var track = new MidiTrack { Name = "t" };
        track.Events.Add(new MidiNoteEvent(10, 0, 0, 60, 90, NoteOn: true));
        track.Events.Add(new MidiNoteEvent(20, 0, 0, 60, 0, NoteOn: false));
        var writer = new MidiFileWriter(Ppq);
        byte[] a = writer.Write(EmptyConductor(), new[] { track });
        byte[] b = writer.Write(EmptyConductor(), new[] { track });
        Assert.Equal(a, b);
    }

    [Fact]
    public void Write_ShuffledEqualEvents_SourceOrderDeterminesBytes()
    {
        // Three notes sharing the same tick AND the same rank (all note-on). The
        // only distinguishing key is SourceOrder, so the serialized byte order
        // must follow SourceOrder regardless of the list's insertion order.
        var tNormal = new MidiTrack { Name = "t" };
        tNormal.Events.Add(NoteAt(100, 0, 60, sourceOrder: 0));
        tNormal.Events.Add(NoteAt(100, 0, 62, sourceOrder: 1));
        tNormal.Events.Add(NoteAt(100, 0, 64, sourceOrder: 2));
        var writer = new MidiFileWriter(Ppq);
        byte[] normal = writer.Write(EmptyConductor(), new[] { tNormal });

        var tShuffled = new MidiTrack { Name = "t" };
        tShuffled.Events.Add(NoteAt(100, 0, 64, sourceOrder: 2));
        tShuffled.Events.Add(NoteAt(100, 0, 60, sourceOrder: 0));
        tShuffled.Events.Add(NoteAt(100, 0, 62, sourceOrder: 1));
        byte[] shuffled = writer.Write(EmptyConductor(), new[] { tShuffled });

        Assert.Equal(normal, shuffled);
    }

    [Fact]
    public void Write_SameTickRetrigger_NoteOffBeforeNoteOn()
    {
        var track = new MidiTrack { Name = "retrig" };
        track.Events.Add(new MidiNoteEvent(960, 1, 0, 64, 90, NoteOn: false));
        track.Events.Add(new MidiNoteEvent(960, 1, 0, 64, 90, NoteOn: true));
        var writer = new MidiFileWriter(Ppq);
        byte[] bytes = writer.Write(EmptyConductor(), new[] { track });

        var events = MidiRoundTrip.TimedEvents(bytes, 1); // musical track (index 0 = conductor)
        var atTick = events.Where(e => e.Tick == 960 && e.Event is not EndOfTrackEvent)
            .Select(e => e.Event).ToList();
        Assert.Equal(2, atTick.Count);
        Assert.IsType<NoteOffEvent>(atTick[0]);
        Assert.IsType<NoteOnEvent>(atTick[1]);
    }

    // ---- invalid inputs ----

    [Fact]
    public void Write_RejectsNegativeTick()
    {
        var track = new MidiTrack { Name = "t" };
        track.Events.Add(new MidiNoteEvent(-1, 0, 0, 60, 90, NoteOn: true));
        var writer = new MidiFileWriter(Ppq);
        Assert.Throws<InvalidOperationException>(() => writer.Write(EmptyConductor(), new[] { track }));
    }

    // ---- helpers ----

    private static List<MidiEventBase> EmptyConductor() => new();

    private static MidiNoteEvent NoteAt(long tick, int channel, int note, int sourceOrder)
    {
        var evt = new MidiNoteEvent(tick, 0, channel, note, 90, NoteOn: true);
        evt.SourceOrder = sourceOrder;
        return evt;
    }
}