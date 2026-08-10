using Fmp.Core.Midi;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// DryWetMIDI round-trip semantic tests for <see cref="MidiFileWriter"/>: notes,
/// program, tempo, meter, pitch bend, RPN, markers, same-tick ordering, tempo
/// segments, determinism, and invalid-value rejection. All assertions use the
/// parsed DryWetMIDI object model, not raw byte walking.
/// </summary>
public sealed class MidiFileWriterRoundTripTests
{
    private const int Ppq = 960;

    // ---- Notes ----

    [Fact]
    public void Notes_RoundTrip_PreserveTickChannelNoteVelocity()
    {
        var track = new MidiTrack { Name = "t", Endpoint = new MidiEndpoint(0, 0) };
        track.Events.Add(new MidiNoteEvent(480, 0, 3, 60, 95, NoteOn: true));
        track.Events.Add(new MidiNoteEvent(720, 0, 3, 60, 0, NoteOn: false));
        byte[] bytes = Write(track);

        var chunk = LastMusicalChunk(bytes);
        var on = chunk.Events.OfType<NoteOnEvent>().Single();
        var off = chunk.Events.OfType<NoteOffEvent>().Single();
        Assert.Equal(480, AbsTick(chunk, on));
        Assert.Equal(720, AbsTick(chunk, off));
        Assert.Equal((FourBitNumber)3, on.Channel);
        Assert.Equal((SevenBitNumber)60, on.NoteNumber);
        Assert.Equal((SevenBitNumber)95, on.Velocity);
    }

    [Fact]
    public void PortPrefix_RoundTrip_PreservesTrackEndpointPort()
    {
        var track = new MidiTrack { Name = "port-7", Endpoint = new MidiEndpoint(7, 3) };
        byte[] bytes = Write(track);

        var chunk = LastMusicalChunk(bytes);
        var prefix = chunk.Events.OfType<PortPrefixEvent>().Single();
        Assert.Equal((byte)7, prefix.Port);
    }

    // ---- Program ----

    [Fact]
    public void Program_RoundTrip_PreservesNumberChannelTick()
    {
        var track = new MidiTrack { Name = "t", Endpoint = new MidiEndpoint(0, 0) };
        track.Events.Add(new MidiProgramEvent(960, 0, 2, 40));
        byte[] bytes = Write(track);

        var chunk = LastMusicalChunk(bytes);
        var prog = chunk.Events.OfType<ProgramChangeEvent>().Single();
        Assert.Equal(960, AbsTick(chunk, prog));
        Assert.Equal((SevenBitNumber)40, prog.ProgramNumber);
        Assert.Equal((FourBitNumber)2, prog.Channel);
    }

    // ---- Tempo ----

    [Fact]
    public void Tempo_RoundTrip_PreservesMicrosecondsAndTick()
    {
        var conductor = new List<MidiEventBase> { new MidiTempoEvent(240, 500_000) };
        byte[] bytes = Write(new MidiTrack { Name = "t", Endpoint = new MidiEndpoint(0, 0) }, conductor);

        var chunk = MidiRoundTrip.TrackChunks(bytes)[0];
        var tempo = chunk.Events.OfType<SetTempoEvent>().Single();
        Assert.Equal(240, AbsTick(chunk, tempo));
        Assert.Equal(500_000, tempo.MicrosecondsPerQuarterNote);
    }

    // ---- Meter ----

    [Fact]
    public void Meter_RoundTrip_PreservesNumeratorDenominatorTick()
    {
        var conductor = new List<MidiEventBase> { new MidiTimeSignatureEvent(0, 3, 8) };
        byte[] bytes = Write(new MidiTrack { Name = "t", Endpoint = new MidiEndpoint(0, 0) }, conductor);

        var chunk = MidiRoundTrip.TrackChunks(bytes)[0];
        var ts = chunk.Events.OfType<TimeSignatureEvent>().Single();
        Assert.Equal((byte)3, ts.Numerator);
        Assert.Equal((byte)8, ts.Denominator);
    }

    // ---- Pitch bend ----

    [Theory]
    [InlineData(-8192, 0)]
    [InlineData(0, 8192)]
    [InlineData(8191, 16383)]
    public void PitchBend_RoundTrip_MapsSignedTo14Bit(int signed, ushort expectedUnsigned)
    {
        var track = new MidiTrack { Name = "t", Endpoint = new MidiEndpoint(0, 0) };
        track.Events.Add(new MidiPitchBendEvent(0, 0, 0, signed));
        byte[] bytes = Write(track);

        var chunk = LastMusicalChunk(bytes);
        var bend = chunk.Events.OfType<PitchBendEvent>().Single();
        Assert.Equal(expectedUnsigned, bend.PitchValue);
    }

    [Theory]
    [InlineData(-8193)]
    [InlineData(8192)]
    public void PitchBend_OutOfRange_Throws(int signed)
    {
        var track = new MidiTrack { Name = "t", Endpoint = new MidiEndpoint(0, 0) };
        track.Events.Add(new MidiPitchBendEvent(0, 0, 0, signed));
        var writer = new MidiFileWriter(Ppq);
        Assert.Throws<InvalidOperationException>(() => writer.Write(Array.Empty<MidiEventBase>(), new[] { track }));
    }

    // ---- RPN ----

    [Fact]
    public void BendRange_RoundTrip_EmitsExpectedCcSequenceInOrder()
    {
        var track = new MidiTrack { Name = "t", Endpoint = new MidiEndpoint(0, 0) };
        // MidiBendRangeEvent(source, track, channel, semitones)
        track.Events.Add(new MidiBendRangeEvent(0, 0, 0, 2));
        byte[] bytes = Write(track);

        var chunk = LastMusicalChunk(bytes);
        var ccs = chunk.Events.OfType<ControlChangeEvent>().ToList();
        Assert.Equal(6, ccs.Count);
        int[] expectedControl = { 101, 100, 6, 101, 100, 6 };
        int[] expectedValue = { 0, 0, 2, 127, 127, 0 };
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal((SevenBitNumber)expectedControl[i], ccs[i].ControlNumber);
            Assert.Equal((SevenBitNumber)expectedValue[i], ccs[i].ControlValue);
            Assert.Equal((FourBitNumber)0, ccs[i].Channel);
        }
        // First generated CC carries the source delta; the rest are delta 0.
        Assert.Equal(0, ccs[0].DeltaTime);
        for (int i = 1; i < 6; i++)
            Assert.Equal(0, ccs[i].DeltaTime);
    }

    // ---- Markers ----

    [Fact]
    public void Marker_RoundTrip_PreservesNameAndTick()
    {
        var conductor = new List<MidiEventBase> { new MidiMarkerEvent(1234, "LOOP_START") };
        byte[] bytes = Write(new MidiTrack { Name = "t", Endpoint = new MidiEndpoint(0, 0) }, conductor);

        var chunk = MidiRoundTrip.TrackChunks(bytes)[0];
        var marker = chunk.Events.OfType<MarkerEvent>().Single();
        Assert.Equal(1234, AbsTick(chunk, marker));
        Assert.Equal("LOOP_START", marker.Text);
    }

    // ---- Conductor track name ----

    [Fact]
    public void Conductor_TrackName_IsConductor()
    {
        byte[] bytes = Write(new MidiTrack { Name = "t", Endpoint = new MidiEndpoint(0, 0) });
        var chunk = MidiRoundTrip.TrackChunks(bytes)[0];
        var name = chunk.Events.OfType<SequenceTrackNameEvent>().Single();
        Assert.Equal("Conductor", name.Text);
    }

    // ---- Same-tick ordering (MANDATORY) ----

    [Fact]
    public void SameTick_NoteOffPrecedesNoteOn_AtSharedTick()
    {
        var track = new MidiTrack { Name = "t", Endpoint = new MidiEndpoint(0, 0) };
        track.Events.Add(new MidiNoteEvent(960, 0, 0, 60, 90, NoteOn: false));
        track.Events.Add(new MidiNoteEvent(960, 0, 0, 60, 90, NoteOn: true));
        byte[] bytes = Write(track);

        var chunk = LastMusicalChunk(bytes);
        var atTick = chunk.Events.Where(e => e is NoteOffEvent or NoteOnEvent).ToList();
        Assert.IsType<NoteOffEvent>(atTick[0]);
        Assert.IsType<NoteOnEvent>(atTick[1]);
    }

    [Fact]
    public void SameTick_Priority_NoteOffBeforeBendBeforeNoteOn()
    {
        var track = new MidiTrack { Name = "t", Endpoint = new MidiEndpoint(0, 0) };
        // ranks: NoteOff(0) < Bend(3) < NoteOn(4).
        track.Events.Add(new MidiNoteEvent(0, 0, 0, 60, 90, NoteOn: true));   // rank 4
        track.Events.Add(new MidiPitchBendEvent(0, 0, 0, 0));                 // rank 3
        track.Events.Add(new MidiNoteEvent(0, 0, 0, 60, 90, NoteOn: false));  // rank 0
        byte[] bytes = Write(track);

        var chunk = LastMusicalChunk(bytes);
        var seq = chunk.Events.Where(e => e is NoteOffEvent or NoteOnEvent or PitchBendEvent).ToList();
        Assert.IsType<NoteOffEvent>(seq[0]);
        Assert.IsType<PitchBendEvent>(seq[1]);
        Assert.IsType<NoteOnEvent>(seq[2]);
    }

    // ---- Determinism ----

    [Fact]
    public void Determinism_SameTimelineTwice_ByteIdentical()
    {
        var track = new MidiTrack { Name = "t", Endpoint = new MidiEndpoint(0, 0) };
        track.Events.Add(new MidiNoteEvent(0, 0, 0, 60, 90, NoteOn: true));
        track.Events.Add(new MidiNoteEvent(960, 0, 0, 60, 0, NoteOn: false));
        var writer = new MidiFileWriter(Ppq);
        byte[] a = writer.Write(Array.Empty<MidiEventBase>(), new[] { track });
        byte[] b = writer.Write(Array.Empty<MidiEventBase>(), new[] { track });
        Assert.Equal(a, b);
    }

    // ---- Tempo segments / tempo changes ----

    [Fact]
    public void TempoChanges_TwoSegments_SetTempoAtExpectedTicks()
    {
        var conductor = new List<MidiEventBase>
        {
            new MidiTempoEvent(0, 500_000),
            new MidiTempoEvent(1920, 400_000),
        };
        // Note ticks are derived from the map calculation upstream (unchanged here).
        var track = new MidiTrack { Name = "t", Endpoint = new MidiEndpoint(0, 0) };
        track.Events.Add(new MidiNoteEvent(960, 0, 0, 60, 90, NoteOn: true));
        byte[] bytes = Write(track, conductor);

        var chunk = MidiRoundTrip.TrackChunks(bytes)[0];
        var tempos = chunk.Events.OfType<SetTempoEvent>().ToList();
        Assert.Equal(2, tempos.Count);
        Assert.Equal(500_000, tempos[0].MicrosecondsPerQuarterNote);
        Assert.Equal(400_000, tempos[1].MicrosecondsPerQuarterNote);
        Assert.Equal(0, AbsTick(chunk, tempos[0]));
        Assert.Equal(1920, AbsTick(chunk, tempos[1]));
        // The note tick is unchanged by the timing authority (map).
        var noteChunk = LastMusicalChunk(bytes);
        Assert.Equal(960, AbsTick(noteChunk, noteChunk.Events.OfType<NoteOnEvent>().Single()));
    }

    // ---- Pickup / global origin: nonnegative ticks + preserved distances ----

    [Fact]
    public void Pickup_NoNegativeTicks_RelativeDistancesPreserved()
    {
        var track = new MidiTrack { Name = "t", Endpoint = new MidiEndpoint(0, 0) };
        track.Events.Add(new MidiPitchBendEvent(10, 0, 0, 500));   // earliest
        track.Events.Add(new MidiNoteEvent(100, 0, 0, 60, 90, NoteOn: true));
        track.Events.Add(new MidiNoteEvent(300, 0, 0, 60, 0, NoteOn: false));
        byte[] bytes = Write(track);

        var chunk = LastMusicalChunk(bytes);
        foreach (var (tick, evt) in MidiRoundTrip.TimedEvents(bytes, 1))
        {
            Assert.True(tick >= 0, "all ticks must be non-negative");
        }
        // Distances preserved: off - on = 200, bend - on = -90.
        var on = chunk.Events.OfType<NoteOnEvent>().Single();
        var off = chunk.Events.OfType<NoteOffEvent>().Single();
        var bend = chunk.Events.OfType<PitchBendEvent>().Single();
        Assert.Equal(200, AbsTick(chunk, off) - AbsTick(chunk, on));
        Assert.Equal(90, AbsTick(chunk, on) - AbsTick(chunk, bend));
    }

    // ---- Invalid values ----

    [Theory]
    [InlineData(0)]
    [InlineData(0x1000000)]
    public void Tempo_InvalidValue_Throws(int us)
    {
        var conductor = new List<MidiEventBase> { new MidiTempoEvent(0, us) };
        var writer = new MidiFileWriter(Ppq);
        Assert.Throws<InvalidOperationException>(() =>
            writer.Write(conductor, new[] { new MidiTrack { Name = "t", Endpoint = new MidiEndpoint(0, 0) } }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0xFFFFFF)]
    public void Tempo_BoundaryValues_Accepted(int us)
    {
        var conductor = new List<MidiEventBase> { new MidiTempoEvent(0, us) };
        byte[] bytes = Write(new MidiTrack { Name = "t", Endpoint = new MidiEndpoint(0, 0) }, conductor);
        var chunk = MidiRoundTrip.TrackChunks(bytes)[0];
        Assert.Equal(us, chunk.Events.OfType<SetTempoEvent>().Single().MicrosecondsPerQuarterNote);
    }

    [Fact]
    public void Meter_NonPowerOfTwoDenominator_Throws()
    {
        var conductor = new List<MidiEventBase> { new MidiTimeSignatureEvent(0, 3, 5) };
        var writer = new MidiFileWriter(Ppq);
        Assert.Throws<InvalidOperationException>(() =>
            writer.Write(conductor, new[] { new MidiTrack { Name = "t", Endpoint = new MidiEndpoint(0, 0) } }));
    }

    // ---- helpers ----

    private static byte[] Write(MidiTrack track, List<MidiEventBase>? conductor = null)
    {
        var writer = new MidiFileWriter(Ppq);
        return writer.Write(conductor ?? new List<MidiEventBase>(), new[] { track });
    }

    private static TrackChunk LastMusicalChunk(byte[] bytes) =>
        MidiRoundTrip.TrackChunks(bytes).Last();

    private static long AbsTick(TrackChunk chunk, MidiEvent target)
    {
        long tick = 0;
        foreach (MidiEvent evt in chunk.Events)
        {
            tick += evt.DeltaTime;
            if (ReferenceEquals(evt, target))
                return tick;
        }
        throw new ArgumentException("event not found in chunk", nameof(target));
    }
}
