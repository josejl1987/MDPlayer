using Fmp.Core.Midi;
using Melanchall.DryWetMidi.Core;

namespace MDPlayer.Fmp.Tests.Support;

/// <summary>
/// Corpus validator (FR-13 / request 40): test/dev tooling that inspects a
/// generated .mid and reports 14 structural fields per file. NOT a general MIDI
/// analysis application — it exists to make the 9-file regression corpus auditable
/// in one compact table.
/// </summary>
internal static class CorpusMidiValidator
{
    internal sealed record CorpusReport(
        string File,
        double? TempoBpm,
        int TrackCount,
        int UniqueEndpointCount,
        int BendTrackCount,
        int RpnCoveredBendTracks,
        int PitchBendCount,
        int DuplicateEndpointTickBendCount,
        int ProgramCount,
        int ZeroTickNotes,
        int OneTickNotes,
        int LoopMarkerCount,
        string Meter,
        long? SourceStartTick);

    public static CorpusReport Analyze(string file, byte[] midiBytes)
    {
        MidiSemanticDecoder.Result decoded = MidiSemanticDecoder.Decode(midiBytes);
        IReadOnlyList<TrackChunk> chunks = MidiRoundTrip.TrackChunks(midiBytes);

        // Tempo from the decoded tempo map (first segment).
        double? tempoBpm = decoded.TempoMap.Count > 0
            ? 60_000_000.0 / decoded.TempoMap[0].UsPerQuarter
            : null;

        int trackCount = chunks.Count - 1; // conductor is chunk 0
        var endpoints = new HashSet<(int Port, int Channel)>();
        foreach (var ep in decoded.Events)
            endpoints.Add(ep.Key);
        int uniqueEndpointCount = endpoints.Count;

        int bendTrackCount = decoded.Events.Count(ep =>
            ep.Value.Any(e => e.Event is PitchBendEvent));
        int rpnCoveredBendTracks = decoded.Events.Count(ep =>
            ep.Value.Any(e => e.Event is PitchBendEvent)
            && decoded.State[ep.Key].BendRange == 24);

        int pitchBendCount = decoded.Events.Values
            .SelectMany(e => e).Count(e => e.Event is PitchBendEvent);

        int duplicateEndpointTickBendCount = 0;
        var bendCounts = new Dictionary<(int Port, int Channel, long Tick), int>();
        foreach (var ep in decoded.Events)
            foreach (var timed in ep.Value)
                if (timed.Event is PitchBendEvent)
                {
                    var key = (timed.Port, timed.Channel, timed.Tick);
                    bendCounts[key] = bendCounts.GetValueOrDefault(key) + 1;
                }
        duplicateEndpointTickBendCount = bendCounts.Count(kv => kv.Value > 1);

        int programCount = decoded.Events.Values
            .SelectMany(e => e).Count(e => e.Event is ProgramChangeEvent);

        // Zero/one-tick notes via note-on/note-off pairing per endpoint.
        int zeroTickNotes = 0, oneTickNotes = 0;
        foreach (var ep in decoded.Events)
        {
            var pending = new Dictionary<int, long>();
            foreach (var timed in decoded.Events[ep.Key]
                         .OrderBy(e => e.Tick)
                         .ThenBy(e => e.Event is NoteOffEvent ? 0 : 1))
            {
                if (timed.Event is NoteOnEvent on && on.Velocity != 0)
                    pending[on.NoteNumber] = timed.Tick;
                else if (timed.Event is NoteOffEvent off
                         && pending.TryGetValue(off.NoteNumber, out long onTick))
                {
                    if (timed.Tick <= onTick) zeroTickNotes++;
                    else if (timed.Tick - onTick == 1) oneTickNotes++;
                    pending.Remove(off.NoteNumber);
                }
            }
        }

        int loopMarkerCount = chunks.SelectMany(c => c.Events)
            .Count(e => e is MarkerEvent m
                && (m.Text == "LOOP_START" || m.Text == "LOOP_END" || m.Text == "LOOP_MARK"));

        string meter = "unknown";
        foreach (MidiEvent evt in chunks[0].Events)
        {
            if (evt is TimeSignatureEvent ts)
            {
                meter = $"{ts.Numerator}/{Math.Pow(2, ts.Denominator):0}";
                break;
            }
        }

        long? sourceStartTick = null;
        TrackChunk sourceStartChunk = chunks.FirstOrDefault(c =>
            c.Events.Any(e => e is MarkerEvent m && m.Text == "SOURCE_START"));
        if (sourceStartChunk is not null)
        {
            long tick = 0;
            foreach (MidiEvent evt in sourceStartChunk.Events)
            {
                tick += evt.DeltaTime;
                if (evt is MarkerEvent m && m.Text == "SOURCE_START")
                {
                    sourceStartTick = tick;
                    break;
                }
            }
        }

        return new CorpusReport(
            File: file,
            TempoBpm: tempoBpm,
            TrackCount: trackCount,
            UniqueEndpointCount: uniqueEndpointCount,
            BendTrackCount: bendTrackCount,
            RpnCoveredBendTracks: rpnCoveredBendTracks,
            PitchBendCount: pitchBendCount,
            DuplicateEndpointTickBendCount: duplicateEndpointTickBendCount,
            ProgramCount: programCount,
            ZeroTickNotes: zeroTickNotes,
            OneTickNotes: oneTickNotes,
            LoopMarkerCount: loopMarkerCount,
            Meter: meter,
            SourceStartTick: sourceStartTick);
    }

    /// <summary>Prints the 14-field report for one file (FR-13).</summary>
    public static void Print(string file, byte[] midiBytes)
    {
        CorpusReport r = Analyze(file, midiBytes);
        Console.WriteLine(string.Join(" | ",
            $"file={Path.GetFileName(r.File)}",
            $"tempo={r.TempoBpm?.ToString("0.##") ?? "none"}",
            $"tracks={r.TrackCount}",
            $"endpoints={r.UniqueEndpointCount}",
            $"bend-tracks={r.BendTrackCount}",
            $"rpn-covered={r.RpnCoveredBendTracks}",
            $"bends={r.PitchBendCount}",
            $"dup-bends={r.DuplicateEndpointTickBendCount}",
            $"programs={r.ProgramCount}",
            $"zero-tick={r.ZeroTickNotes}",
            $"one-tick={r.OneTickNotes}",
            $"loops={r.LoopMarkerCount}",
            $"meter={r.Meter}",
            $"source-start={r.SourceStartTick?.ToString() ?? "none"}"));
    }
}
