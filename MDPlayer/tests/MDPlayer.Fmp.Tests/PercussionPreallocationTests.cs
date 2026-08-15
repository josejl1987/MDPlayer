using Fmp.Core.Midi;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Xunit;
using NoteOn = Melanchall.DryWetMidi.Core.NoteOnEvent;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Patch 6 — deterministic unknown-percussion preallocation (spec P1-13):
/// unknown rhythm identities receive MIDI notes BEFORE event generation, in
/// canonical identity order, from the preferred pool 60-81 then the overflow
/// pool (every other note 27-127 minus the reserved GM set). Assignment is
/// independent of timeline event ordering; each assignment is surfaced as
/// UNMAPPED_PERCUSSION conductor text; the allocator throws only when BOTH
/// pools are exhausted. The semantic Map() fallback no longer exists.
/// </summary>
public sealed class PercussionPreallocationTests
{
    private const int Sr = 44_100;
    private const int Ppq = 960;

    private static RhythmEvent UnknownRhythm(string identity, long sample) =>
        new(identity, identity, sample, 1.0f, 0f);

    private static VisualizationTimeline Timeline(params RhythmEvent[] rhythm) => new()
    {
        StartSample = 0,
        EndSample = 8_000_000,
        SampleRate = Sr,
        Rhythm = rhythm,
    };

    private static byte[] Export(MusicalMidiExportOptions options, params RhythmEvent[] rhythm)
    {
        var build = MusicalTimeMapBuilder.Build(Timeline(rhythm), new MusicalTimeMapOptions { FixedBpm = 120 });
        var exporter = new MusicalMidiExporter(build.Map, Ppq, options) { Diagnostics = build.Diagnostics };
        return exporter.Export(Timeline(rhythm)).Bytes;
    }

    private static byte[] Export(params RhythmEvent[] rhythm) =>
        Export(new MusicalMidiExportOptions { EmitPitchBend = false }, rhythm);

    /// <summary>All percussion note-ons in tick order (tick order == the order
    /// hits appear in the file, so per-identity notes are identifiable when the
    /// hits sit at distinct samples).</summary>
    private static int[] Notes(byte[] bytes) =>
        MidiRoundTrip.TrackChunks(bytes).Skip(1)
            .SelectMany(c => c.Events.OfType<NoteOn>())
            .Select(e => (int)((SevenBitNumber)e.NoteNumber))
            .ToArray();

    /// <summary>All percussion note-ons in absolute-tick order, regardless of the
    /// file's track order — so per-identity notes are identifiable when the hits
    /// sit at distinct samples.</summary>
    private static int[] NotesInTickOrder(byte[] bytes)
    {
        var result = new List<(long Tick, int Note)>();
        for (int track = 1; track < MidiRoundTrip.TrackChunks(bytes).Count; track++)
        {
            foreach ((long tick, MidiEvent evt) in MidiRoundTrip.TimedEvents(bytes, track))
            {
                if (evt is NoteOn noteOn)
                    result.Add((tick, (int)((SevenBitNumber)noteOn.NoteNumber)));
            }
        }
        return result.OrderBy(x => x.Tick).Select(x => x.Note).ToArray();
    }

    private static string[] ConductorTexts(byte[] bytes) =>
        MidiRoundTrip.TrackChunks(bytes)[0].Events
            .OfType<TextEvent>()
            .Select(t => t.Text)
            .ToArray();

    [Fact]
    public void UnknownIdentities_AllocatedInCanonicalIdentityOrder_NotEventOrder()
    {
        // "aaa" sorts before "zzz" but the timeline presents zzz FIRST. The
        // preallocation must still hand 60 (first preferred note) to aaa and 61
        // to zzz, so per-identity notes never depend on event ordering. Notes
        // are listed in tick order: the first hit (sample 1000) comes first.
        Assert.Equal(new[] { 61, 60 },
            NotesInTickOrder(Export(UnknownRhythm("zzz", 1_000), UnknownRhythm("aaa", 2_000))));
        Assert.Equal(new[] { 60, 61 },
            NotesInTickOrder(Export(UnknownRhythm("aaa", 1_000), UnknownRhythm("zzz", 2_000))));
    }

    [Fact]
    public void UnknownIdentities_SameInputTwice_ProducesIdenticalBytes()
    {
        byte[] first = Export(UnknownRhythm("bbb", 1_000), UnknownRhythm("aaa", 2_000));
        byte[] second = Export(UnknownRhythm("bbb", 1_000), UnknownRhythm("aaa", 2_000));
        Assert.Equal(first, second);
    }

    [Fact]
    public void OverflowPool_UsedAfterPreferredPool_ExcludingReservedGmNotes()
    {
        // 25 distinct identities: 22 fill the preferred pool 60-81, then the
        // overflow pool starts at 27 and takes every other note (27, 29, 31).
        // None of the reserved GM notes the semantic mapper emits
        // ({36,37,38,42,45,48,49,50,57}) may appear.
        RhythmEvent[] rhythm = Enumerable.Range(0, 25)
            .Select(i => UnknownRhythm($"id-{i:00}", 1_000 + i * 100))
            .ToArray();
        int[] notes = Notes(Export(rhythm)).OrderBy(n => n).ToArray();
        Assert.Equal(
            new[] { 27, 29, 31 }
                .Concat(Enumerable.Range(60, 22))
                .ToArray(),
            notes);
        Assert.DoesNotContain(notes, n => n is 36 or 37 or 38 or 42 or 45 or 48 or 49 or 50 or 57);
    }

    [Fact]
    public void UnmappedPercussion_EmitsConductorTextWithNoteAndSource()
    {
        byte[] bytes = Export(UnknownRhythm("drums", 1_000));
        Assert.Equal(new[] { 60 }, Notes(bytes).OrderBy(n => n).ToArray());
        Assert.Contains(ConductorTexts(bytes),
            t => t == "UNMAPPED_PERCUSSION note=60 source=drums");
    }

    [Fact]
    public void UnmappedPercussion_MetadataSuppressedWhenConductorMetadataDisabled()
    {
        byte[] bytes = Export(
            new MusicalMidiExportOptions { EmitPitchBend = false, EmitConductorMetadata = false },
            UnknownRhythm("drums", 1_000));
        Assert.Equal(new[] { 60 }, Notes(bytes).OrderBy(n => n).ToArray());
        Assert.DoesNotContain(ConductorTexts(bytes),
            t => t.StartsWith("UNMAPPED_PERCUSSION", StringComparison.Ordinal));
    }

    [Fact]
    public void Exhaustion_ThrowsOnlyWhenPreferredAndOverflowPoolsExhausted()
    {
        // The pool holds 69 notes (60-81 + every other note 27-127 minus
        // reserved); 70 distinct unknown identities must throw loudly rather
        // than silently reuse a note.
        RhythmEvent[] rhythm = Enumerable.Range(0, 70)
            .Select(i => UnknownRhythm($"ex-{i:00}", 1_000 + i * 100))
            .ToArray();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Export(rhythm));
        Assert.Contains("exhaustion", ex.Message);
    }
}
