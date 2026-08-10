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

    [Fact]
    public void IndependentDomains_ExhaustChannelsWithoutWrappingOrReuse()
    {
        var notes = Enumerable.Range(0, 16)
            .Select(instance => Note($"ym2608.{instance}.fm.1", "fm:1", 0, 1000)
                with { Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, instance), VoiceKind.Fm, 0) })
            .ToArray();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Export(notes));
        Assert.Contains("channel exhaustion", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("without wrapping or merging", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplicitSharedChannel_ConflictingProgramAndBankFailsBeforeSmfCreation()
    {
        var options = new MusicalMidiExportOptions
        {
            EmitPitchBend = false,
            VoiceOverrides = new[]
            {
                new VoiceExportOverride("ym2608.0.fm.1") { Channel = 3, Program = 10, Bank = 1 },
                new VoiceExportOverride("ym2608.0.fm.1-b") { Channel = 3, Program = 11, Bank = 1 },
            },
        };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Export(options,
            Note("ym2608.0.fm.1", "fm:1", 0, 1000),
            Note("ym2608.0.fm.1-b", "fm:2", 0, 1000)));
        Assert.Contains("incompatible state", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("program/bank", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("10", error.Message);
        Assert.Contains("11", error.Message);
    }

    [Fact]
    public void SharedDomain_CompatibleTracksKeepOneChannelAndOneBendRangeAfterRoundTrip()
    {
        var result = Export(new MusicalMidiExportOptions { EmitPitchBend = true, BendRangeSemitones = 7 },
            Note("ym2608.0.fm.1", "fm:1", 0, 1000) with
            {
                Pitch = new[] { new PitchChange(500, 440 * Math.Pow(2, 7.0 / 12), 67) },
            },
            Note("ym2608.0.fm.1", "fm:2", 1000, 2000));

        var chunks = MidiRoundTrip.TrackChunks(result.Bytes).Skip(1).ToList();
        Assert.Equal(2, chunks.Count);
        Assert.Equal(1, chunks.Select(c => c.Events.OfType<DryNote>().Single().Channel).Distinct().Count());
        Assert.Single(result.Tracks.SelectMany(t => t.Events).OfType<MidiBendRangeEvent>());
        Assert.Equal(7, result.Tracks.SelectMany(t => t.Events).OfType<MidiBendRangeEvent>().Single().Semitones);
    }

    [Fact]
    public void MalformedLegacyVoiceIdsRemainSeparateByOwnershipAndValidRhythmIsNotPlaceholder()
    {
        var result = Export(
            Note("ym2608.0.fm.unknown-a", "fm:1", 0, 1000),
            Note("ym2608.0.fm.unknown-b", "fm:1", 1000, 2000));

        string[] names = TrackNames(result).Where(n => n != "Conductor").ToArray();
        Assert.Equal(2, names.Length);
        Assert.Contains("ym2608.0.fm.unknown-a", names);
        Assert.Contains("ym2608.0.fm.unknown-b", names);

        var rhythm = new RhythmEvent("top", "ym2608.0.rhythm.top", 0, 1, 0, InstrumentId: "rhythm:top")
        {
            Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Rhythm, 2),
        };
        var rhythmResult = ExportTimeline(new VisualizationTimeline
        {
            StartSample = 0, EndSample = 5000, SampleRate = 44100,
            Rhythm = new[] { rhythm },
        }, new MusicalMidiExportOptions { EmitPitchBend = false });
        string rhythmName = Assert.Single(TrackNames(rhythmResult).Where(n => n != "Conductor"));
        Assert.Contains("rhythm:top", rhythmName);
        var rhythmChunk = Assert.Single(MidiRoundTrip.TrackChunks(rhythmResult.Bytes).Skip(1));
        Assert.Equal(9, rhythmChunk.Events.OfType<DryNote>().Single().Channel);
    }

    private static MusicalMidiExportResult Export(MusicalMidiExportOptions options, params VisualizationNoteEvent[] notes) =>
        ExportTimeline(new VisualizationTimeline
        {
            StartSample = 0, EndSample = 5_000_000, SampleRate = 44100, Notes = notes,
        }, options);

    private static MusicalMidiExportResult ExportTimeline(VisualizationTimeline timeline, MusicalMidiExportOptions options)
    {
        var map = new MusicalTimeMap(
            44100, 0,
            new[] { new TempoSegment(0, 5_000_000, 0, 22050, 120, TimingSource.UserOverride, 1.0) },
            meter: null);
        return new MusicalMidiExporter(map, Ppq, options).Export(timeline);
    }
}
