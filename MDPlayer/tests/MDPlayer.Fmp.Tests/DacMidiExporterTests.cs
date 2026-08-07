using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Phase-5 tests for the identity-first MIDI sample-trigger export (spec §18)
/// and the deduplicated sample asset/WAV + manifest export (spec §29).
/// </summary>
public sealed class DacMidiExporterTests
{
    private const int Src = 3;

    [Fact]
    public void BuildEvents_EmitsNoteOnNoteOffPerTrigger()
    {
        DacAnalysisReport report = MakeReport(ops =>
        {
            Play(ops, 100, [0x10, 0x20]);   // asset 0
            Play(ops, 200, [0xAA, 0xBB]);   // asset 1
        });

        IReadOnlyList<DacMidiEvent> events = new DacMidiExporter(44_100).BuildEvents(report);

        var ons = events.Where(e => e.NoteOn).ToArray();
        var offs = events.Where(e => e.NoteOn == false && e.Text is null).ToArray();
        Assert.Equal(2, ons.Length);
        Assert.Equal(2, offs.Length);

        // asset 0 -> note 0, asset 1 -> note 1
        Assert.Equal(0, ons[0].Note);
        Assert.Equal(1, ons[1].Note);
        // note-on ticks increasing
        Assert.True(ons[1].Tick > ons[0].Tick);
    }

    [Fact]
    public void BuildEvents_SameTimestampRetrigger_OrdersNoteOffBeforeNoteOn()
    {
        DacAnalysisReport report = MakeReport(ops =>
        {
            byte[] a = [0x10, 0x20, 0x30];
            Play(ops, 100, a);              // ends at 110
            Play(ops, 110, a);              // retrigger at 110
        });

        IReadOnlyList<DacMidiEvent> events = new DacMidiExporter(44_100).BuildEvents(report);

        // The retrigger note-on and the prior note-off share the same tick (110
        // mapped to ticks). The note-off must be sorted before that note-on.
        int secondOnIndex = -1;
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i].NoteOn)
            {
                secondOnIndex = i;
                break;
            }
        }
        Assert.True(secondOnIndex > 0);
        Assert.False(events[secondOnIndex - 1].NoteOn);
        Assert.Equal(events[secondOnIndex - 1].Tick, events[secondOnIndex].Tick);
    }

    [Fact]
    public void Write_ProducesValidSmfHeader()
    {
        DacAnalysisReport report = MakeReport(ops =>
        {
            Play(ops, 0, [0x10, 0x20]);
            Play(ops, 100, [0xAA, 0xBB]);
        });

        byte[] midi = new DacMidiExporter(44_100).Write(report);

        // "MThd" + length 6, format 1, at least one track marker "MTrk".
        Assert.Equal(0x4D, midi[0]); // M
        Assert.Equal(0x54, midi[1]); // T
        Assert.Equal(0x68, midi[2]); // h
        Assert.Equal(0x64, midi[3]); // d
        Assert.Equal(0x00, midi[8]); // format high byte
        Assert.Equal(0x01, midi[9]); // format low byte (format 1)
        Assert.True(midi[11] >= 1); // ntrks low byte (>= 1 track)
        Assert.Contains("MTrk", System.Text.Encoding.ASCII.GetString(midi));

        // Reasonable length given a header, meta track, and a data track.
        Assert.True(midi.Length > 14 + 20);
    }

    [Fact]
    public void BuildEvents_RateMetadataIsEmittedWhenRatePresent()
    {
        DacAnalysisReport report = MakeReport(ops =>
        {
            Play(ops, 10, [0x10], rate: 8000);
        });

        IReadOnlyList<DacMidiEvent> events = new DacMidiExporter(44_100).BuildEvents(report);
        Assert.Contains(events, e =>
            e.Text != null && e.Text.StartsWith(DacMidiEvent.RateMetaTextPrefix + "8000"));
    }

    // ---- helpers ----

    private static DacAnalysisReport MakeReport(Action<List<DacOperation>> build)
    {
        var ops = new List<DacOperation>();
        build(ops);
        var tracker = new DacPlaybackTracker();
        foreach (DacOperation op in ops)
            tracker.Add(op);
        tracker.Complete(50_000);
        return DacAnalysisReport.From(tracker);
    }

    private static void Play(List<DacOperation> ops, long start, byte[] payload, double? rate = null)
    {
        ops.Add(new DacOperation.DacPlaybackStarted(start, Src, 0, null, rate));
        for (int i = 0; i < payload.Length; i++)
            ops.Add(new DacOperation.DacByteConsumed(start + i, Src, i, payload[i]));
        ops.Add(new DacOperation.DacPlaybackStopped(start + 10, DacStopReason.ExplicitStop));
    }
}