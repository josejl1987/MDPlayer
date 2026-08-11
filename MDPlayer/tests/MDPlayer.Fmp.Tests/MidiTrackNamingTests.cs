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

        // Each track gets a UNIQUE MIDI endpoint (Patch A: one track → one endpoint),
        // so the two instrument tracks use distinct channels — never a shared one.
        var chunks = MidiRoundTrip.TrackChunks(bytes).Skip(1).ToList();
        var ch0 = chunks[0].Events.OfType<DryNote>().First().Channel;
        var ch1 = chunks[1].Events.OfType<DryNote>().First().Channel;
        Assert.NotEqual(ch0, ch1);
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
    public void IndependentDomains_GetUniqueEndpoints_AcrossPorts()
    {
        // 16 distinct FM domains each get a UNIQUE (port, channel) endpoint. Melodic
        // channels 0-8 & 10-15 give 15 per port, so the 16th rolls onto port 1
        // (Patch A: port>255 exhausts, but never channel wrapping/reuse).
        var notes = Enumerable.Range(0, 16)
            .Select(instance => Note($"ym2608.{instance}.fm.1", "fm:1", 0, 1000)
                with { Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, instance), VoiceKind.Fm, 0) })
            .ToArray();

        var result = Export(notes);
        var endpoints = MidiRoundTrip.Read(result.Bytes).GetTrackChunks()
            .Skip(1)
            .Select(c => (port: c.Events.OfType<PortPrefixEvent>().FirstOrDefault()?.Port ?? 0,
                          channel: c.Events.OfType<DryNote>().First().Channel))
            .ToArray();
        Assert.Equal(16, endpoints.Length);
        // All endpoints distinct — no wrapping or reuse.
        Assert.Equal(16, endpoints.Distinct().Count());
        // The 16th rolled onto a second port.
        Assert.Equal(2, endpoints.Select(e => e.port).Distinct().Count());
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
        // Each instrument track gets a UNIQUE endpoint (Patch A), so the two tracks
        // use different channels.
        Assert.Equal(2, chunks.Select(c => c.Events.OfType<DryNote>().Single().Channel).Distinct().Count());
        // Only the track that actually emitted a bend gets the fixed RPN setup (7).
        var ranges = result.Tracks.SelectMany(t => t.Events).OfType<MidiBendRangeEvent>().ToList();
        Assert.Single(ranges); // the fm:1 note bends; the fm:2 note is constant-pitch
        Assert.Equal(7, ranges[0].Semitones);
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
        // A single rhythm identity on the chip gets the clean semantic name, not the
        // raw source-voice "rhythm:top" canonical.
        Assert.Equal("YM2608 Rhythm", rhythmName);
        var rhythmChunk = Assert.Single(MidiRoundTrip.TrackChunks(rhythmResult.Bytes).Skip(1));
        Assert.Equal(9, rhythmChunk.Events.OfType<DryNote>().Single().Channel);
    }

    [Fact]
    public void MultipleRhythmVoices_SameChip_DisambiguatedByVoiceName()
    {
        // Two distinct rhythm identities on one YM2608 get the semantic chip name
        // disambiguated by their short voice name (never duplicate "YM2608 Rhythm").
        var timeline = new VisualizationTimeline
        {
            StartSample = 0, EndSample = 5000, SampleRate = 44100,
            Rhythm = new[]
            {
                new RhythmEvent("bd", "ym2608.0.rhythm.bd", 0, 1, 0, InstrumentId: "rhythm:bd")
                { Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Rhythm, 0) },
                new RhythmEvent("top", "ym2608.0.rhythm.top", 0, 1, 0, InstrumentId: "rhythm:top")
                { Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Rhythm, 2) },
            },
        };
        var result = ExportTimeline(timeline, new MusicalMidiExportOptions { EmitPitchBend = false });
        string[] names = TrackNames(result).Where(n => n != "Conductor").OrderBy(n => n).ToArray();
        Assert.Equal(2, names.Length);
        Assert.Equal("YM2608 Rhythm - bd", names[0]);
        Assert.Equal("YM2608 Rhythm - top", names[1]);
    }

    [Fact]
    public void WavetableTrack_Huc6280_GetsSemanticChipName_NotPlaceholder()
    {
        // A HuC6280 wavetable note must resolve to a proper instrument track named
        // from the chip + display name ("HUC6280 CH1 - WAVE 1"), never collapse to
        // the raw channelId placeholder.
        var note = Note("huc6280.0.wavetable.1", "huc6280:wave:1", 0, 1000) with
        { Domain = new SourceDomainKey(new DeviceId(ChipType.Huc6280, 0), VoiceKind.Wavetable, 0) };
        var result = Export(note);
        string[] names = TrackNames(result).Where(n => n != "Conductor").ToArray();
        Assert.Single(names);
        Assert.Equal("HUC6280 CH1 - WAVE 1", names[0]);
    }

    [Fact]
    public void DmgPulseAndWaveChannels_GetDistinctFamilyNames()
    {
        // The DMG decoder now disambiguates pulse vs wave instrument IDs, so the
        // two pulse channels and the wave channel resolve to distinct family names.
        var pulse = Note("dmg.0.pulse.1", "dmg:pulse:1", 0, 1000) with
        { Domain = new SourceDomainKey(new DeviceId(ChipType.Dmg, 0), VoiceKind.Pulse, 0) };
        var wave = Note("dmg.0.wavetable.1", "dmg:wave:3", 0, 1000) with
        { Domain = new SourceDomainKey(new DeviceId(ChipType.Dmg, 0), VoiceKind.Wavetable, 2) };
        var result = Export(pulse, wave);
        string[] names = TrackNames(result).Where(n => n != "Conductor").OrderBy(n => n).ToArray();
        Assert.Equal(2, names.Length);
        Assert.Equal("DMG CH1 - PULSE", names[0]);
        Assert.Equal("DMG CH3 - WAVE 3", names[1]);
    }

    [Fact]
    public void SpcSampleVoice_GetsPcmTrackName_NotPlaceholder()
    {
        // An SPC sample voice ("spc:src3") resolves to a PCM-family track named
        // from the chip + channel, never a raw channelId placeholder.
        var note = Note("snesdsp.0.pcmvoice.1", "spc:src3", 0, 1000) with
        { Domain = new SourceDomainKey(new DeviceId(ChipType.SnesDsp, 0), VoiceKind.PcmVoice, 0) };
        var result = Export(note);
        string[] names = TrackNames(result).Where(n => n != "Conductor").ToArray();
        Assert.Single(names);
        Assert.Equal("SNESDSP CH1 - PCM", names[0]);
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
