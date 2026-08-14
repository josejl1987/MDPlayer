using Fmp.Core.Midi;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Xunit;
using NoteOn = Melanchall.DryWetMidi.Core.NoteOnEvent;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// End-to-end YM2608 rhythm → General MIDI export: semantic percussion pitch
/// selection, per-hit velocity from Strength, and CC10 pan emitted before the
/// first hit and whenever the value changes — verified through the DryWetMIDI
/// round trip.
/// </summary>
public sealed class YM2608RhythmMidiExportTests
{
    private const int Sr = 44_100;
    private const int Ppq = 960;

    private static RhythmEvent Rhythm(string voice, long sample, float strength, float pan)
    {
        int index = voice switch
        {
            "bd" => 0,
            "sd" => 1,
            "top" => 2,
            "hh" => 3,
            "tom" => 4,
            "rim" => 5,
            _ => throw new ArgumentOutOfRangeException(nameof(voice)),
        };
        return new RhythmEvent(
            voice, $"ym2608.0.rhythm.{voice}", sample, strength, pan,
            InstrumentId: $"rhythm:{voice}")
        {
            Domain = new SourceDomainKey(
                new DeviceId(ChipType.Ym2608, 0), VoiceKind.Rhythm, index),
        };
    }

    private static VisualizationTimeline Timeline(params RhythmEvent[] rhythm) => new()
    {
        StartSample = 0,
        EndSample = 8_000_000,
        SampleRate = Sr,
        Rhythm = rhythm,
    };

    private static byte[] Export(params RhythmEvent[] rhythm)
    {
        var build = MusicalTimeMapBuilder.Build(Timeline(rhythm), new MusicalTimeMapOptions
        {
            FixedBpm = 120,
        });
        var exporter = new MusicalMidiExporter(build.Map, Ppq,
            new MusicalMidiExportOptions { EmitPitchBend = false })
        {
            Diagnostics = build.Diagnostics,
        };
        return exporter.Export(Timeline(rhythm)).Bytes;
    }

    [Fact]
    public void SemanticIdentities_EmitExpectedGmNotes()
    {
        byte[] bytes = Export(
            Rhythm("bd", 1000, 1.0f, 0f),
            Rhythm("sd", 3000, 1.0f, 0f),
            Rhythm("rim", 5000, 1.0f, 0f),
            Rhythm("hh", 7000, 1.0f, 0f));

        int[] notes = AllNotes(bytes);
        Assert.Contains(36, notes); // bd  → Bass Drum 1
        Assert.Contains(38, notes); // sd  → Acoustic Snare
        Assert.Contains(37, notes); // rim → Side Stick
        Assert.Contains(42, notes); // hh  → Closed Hi-Hat
        // The semantic set must not leak the allocator's default first note (which
        // would also be 36) as anything beyond bd — e.g. hh must never become 37.
        Assert.Equal(new[] { 36, 37, 38, 42 }, notes.OrderBy(n => n).ToArray());
    }
    [Fact]
    public void Hh_OnlyNote42_NeverAllocatorNote()
    {
        byte[] bytes = Export(
            Rhythm("hh", 1000, 1.0f, 0f),
            Rhythm("hh", 2000, 1.0f, 0f),
            Rhythm("hh", 3000, 0.5f, -1f));

        int[] notes = AllNotes(bytes);
        Assert.All(notes, n => Assert.Equal(42, n));
    }

    [Theory]
    [InlineData(-1.0f, 50)]
    [InlineData(0.0f, 48)]
    [InlineData(1.0f, 45)]
    public void Tom_PanSelectsExpectedPitch(float pan, int expected)
    {
        int[] notes = AllNotes(Export(Rhythm("tom", 1000, 1.0f, pan)));
        Assert.Equal(new[] { expected }, notes.OrderBy(n => n).ToArray());
    }

    [Theory]
    [InlineData(-1.0f, 49)]
    [InlineData(0.0f, 49)]
    [InlineData(1.0f, 57)]
    public void Top_PanSelectsExpectedPitch(float pan, int expected)
    {
        int[] notes = AllNotes(Export(Rhythm("top", 1000, 1.0f, pan)));
        Assert.Equal(new[] { expected }, notes.OrderBy(n => n).ToArray());
    }

    [Fact]
    public void Velocity_ReflectsStrength_PerHit()
    {
        byte[] bytes = Export(
            Rhythm("bd", 1000, 1.0f, 0f),
            Rhythm("bd", 2000, 0.5f, 0f),
            Rhythm("bd", 3000, 0.25f, 0f));

        int[] velocities = AllVelocities(bytes);
        Assert.Equal(3, velocities.Length);
        Assert.Equal(127, velocities[0]);
        Assert.Equal(100, velocities[1]);
        Assert.Equal(80, velocities[2]);
        Assert.True(velocities[1] > velocities[2]);
        Assert.True(velocities[2] >= 50);
    }

    [Fact]
    public void CC10Pan_EmittedBeforeFirstHit_AndOnChange_PerTrack()
    {
        byte[] bytes = Export(
            Rhythm("bd", 1000, 1.0f, -1f),
            Rhythm("bd", 2000, 1.0f, 0f),
            Rhythm("bd", 3000, 1.0f, 1f));

        var chunks = MidiRoundTrip.TrackChunks(bytes).Skip(1)
            .Where(c => c.Events.OfType<NoteOn>().Any())
            .ToList();
        var bdChunk = Assert.Single(chunks);
        var cc = bdChunk.Events.OfType<ControlChangeEvent>().ToList();
        Assert.Equal(3, cc.Count);
        Assert.All(cc, e => Assert.Equal((SevenBitNumber)10, e.ControlNumber));
        Assert.Equal((SevenBitNumber)0, cc[0].ControlValue);
        Assert.Equal((SevenBitNumber)64, cc[1].ControlValue);
        Assert.Equal((SevenBitNumber)127, cc[2].ControlValue);

        for (int index = 0; index < bdChunk.Events.Count; index++)
        {
            if (bdChunk.Events[index] is not ControlChangeEvent ccEvent)
                continue;
            var noteOn = Assert.IsType<NoteOn>(bdChunk.Events[index + 1]);
            Assert.Equal((SevenBitNumber)10, ccEvent.ControlNumber);
            Assert.Equal(0L, noteOn.DeltaTime);
        }
    }

    [Fact]
    public void UnknownIdentity_UnlessSampled_UsesReservedAllocatorRange()
    {
        var unknown = new RhythmEvent("drums", "drums", 1000, 1.0f, 0.5f);
        var build = MusicalTimeMapBuilder.Build(
            Timeline(unknown), new MusicalTimeMapOptions { FixedBpm = 120 });
        var exporter = new MusicalMidiExporter(build.Map, Ppq,
            new MusicalMidiExportOptions { EmitPitchBend = false })
        { Diagnostics = build.Diagnostics };
        int[] notes = AllNotes(exporter.Export(Timeline(unknown)).Bytes);
        Assert.Equal(new[] { 60 }, notes.OrderBy(n => n).ToArray());
    }

    private static int[] AllNotes(byte[] bytes) =>
        MidiRoundTrip.TrackChunks(bytes).Skip(1)
            .SelectMany(c => c.Events.OfType<NoteOn>())
            .Select(e => (int)((SevenBitNumber)e.NoteNumber))
            .ToArray();

    private static int[] AllVelocities(byte[] bytes) =>
        MidiRoundTrip.TrackChunks(bytes).Skip(1)
            .SelectMany(c => c.Events.OfType<NoteOn>())
            .Select(e => (int)((SevenBitNumber)e.Velocity))
            .ToArray();
}