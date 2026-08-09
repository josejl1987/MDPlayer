using Fmp.Core.Midi;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Melanchall.DryWetMidi.Core;
using Xunit;
using VisualizationNoteEvent = Fmp.Core.Visualization.NoteEvent;
using DryNote = Melanchall.DryWetMidi.Core.NoteOnEvent;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Track-grouping regression tests: derivation of MIDI track names from
/// chip + source channel + instrument, and the stable per-source-channel
/// instrument-track allocation (an instrument reappearing never yields a new
/// per-contiguous-interval track).
/// </summary>
public sealed class MidiTrackNamingTests
{
    private const int Ppq = 480;

    private static InstrumentIdentity Fm(int n) => new(IdentityFamily.Fm, n, $"fm:{n}");

    private static VisualizationNoteEvent Note(string channelId, string instrument, long start, long end)
        => new(channelId, start, end, 440, 60, instrument, VisualizationNoteMode.Fm, false, Array.Empty<PitchChange>());

    private static MusicalMidiExportResult Export(params VisualizationNoteEvent[] notes)
    {
        var map = new MusicalTimeMap(
            44100, 0,
            new[] { new TempoSegment(0, 5_000_000, 0, 22050, 120, TimingSource.UserOverride, 1.0) },
            meter: null);
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = 5_000_000,
            SampleRate = 44100,
            Notes = notes,
        };
        var exporter = new MusicalMidiExporter(map, Ppq, new MusicalMidiExportOptions { EmitPitchBend = false });
        return exporter.Export(timeline);
    }

    private static string[] TrackNames(MusicalMidiExportResult result)
    {
        var chunks = MidiRoundTrip.TrackChunks(result.Bytes);
        return chunks.Select(c => c.Events.OfType<SequenceTrackNameEvent>().FirstOrDefault()?.Text ?? "")
            .ToArray();
    }

    [Fact]
    public void SameInstrument_TwoHardwareChannels_TwoDistinctChannelAwareNames()
    {
        // YM2612 CH1 + FM003 AND YM2612 CH3 + FM003.
        var result = Export(
            Note("ym2612.0.fm.1", "fm:3", 0, 1000),   // source channel 1 (0-based 0)
            Note("ym2612.0.fm.3", "fm:3", 0, 1000));  // source channel 3 (0-based 2)

        string[] names = TrackNames(result).Where(n => n != "Conductor").ToArray();
        Assert.Equal(2, names.Length);
        // Each name is chip + source channel + instrument display name, distinct.
        Assert.Equal("YM2612 CH1 - FM 003", names[0]);
        Assert.Equal("YM2612 CH3 - FM 003", names[1]);
        Assert.NotEqual(names[0], names[1]);
    }

    [Fact]
    public void SameSourceChannel_TwoInstruments_TwoTracksSameMidiChannelDifferentNames()
    {
        // YM2612 CH2 + FM003 AND YM2612 CH2 + FM007.
        var result = Export(
            Note("ym2612.0.fm.2", "fm:3", 0, 1000),   // source channel 2 (0-based 1)
            Note("ym2612.0.fm.2", "fm:7", 0, 1000));

        byte[] bytes = result.Bytes;
        string[] names = TrackNames(result).Where(n => n != "Conductor").ToArray();
        Assert.Equal(2, names.Length);
        Assert.Equal("YM2612 CH2 - FM 003", names[0]);
        Assert.Equal("YM2612 CH2 - FM 007", names[1]);

        // Same MIDI channel for both (channel tracks source channel).
        var chunks = MidiRoundTrip.TrackChunks(bytes).Skip(1).ToList();
        var ch0 = chunks[0].Events.OfType<DryNote>().First().Channel;
        var ch1 = chunks[1].Events.OfType<DryNote>().First().Channel;
        Assert.Equal(ch0, ch1);
    }

    [Fact]
    public void StableInstrumentTrack_ReappearingInstrument_NoNewTrack()
    {
        // CH2 instrument A, CH2 instrument B, CH2 instrument A => exactly 2 tracks.
        var result = Export(
            Note("ym2612.0.fm.2", "fm:5", 0, 1000),     // A (source channel 2)
            Note("ym2612.0.fm.2", "fm:9", 2000, 3000),  // B
            Note("ym2612.0.fm.2", "fm:5", 4000, 5000)); // A again

        string[] names = TrackNames(result).Where(n => n != "Conductor").ToArray();
        Assert.Equal(2, names.Length);
        Assert.Equal("YM2612 CH2 - FM 005", names[0]);
        Assert.Equal("YM2612 CH2 - FM 009", names[1]);

        // All A notes land on the same A track.
        var chunks = MidiRoundTrip.TrackChunks(result.Bytes).Skip(1).ToList();
        var aChunk = chunks[0]; // first track is FM 005 (sorted by canonical)
        var aOnTicks = aChunk.Events.OfType<DryNote>()
            .Select(ev => aChunk.Events.TakeWhile(e => !ReferenceEquals(e, ev)).Sum(e => e.DeltaTime))
            .ToList();
        Assert.Equal(2, aOnTicks.Count); // both A notes present on one track
    }

    [Fact]
    public void PlaceholderInstrument_KeepsChannelIdName()
    {
        var result = Export(
            Note("ym2612.0.fm.2", "unresolved-token", 0, 1000));
        string[] names = TrackNames(result).Where(n => n != "Conductor").ToArray();
        // Placeholder keeps the source channelId based name.
        Assert.Single(names);
    }
}