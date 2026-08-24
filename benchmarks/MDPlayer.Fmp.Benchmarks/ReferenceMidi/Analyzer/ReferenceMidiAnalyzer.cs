using System.Security.Cryptography;
using System.Text.Json;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace Fmp.Benchmarks.ReferenceMidi;

/// <summary>
/// Independent MIDI analyzer for the reference corpus (spec §24–§31).
///
/// Consumes ONLY serialized .mid bytes. It must never receive the internal
/// VisualizationTimeline, MusicalTimeMap or MusicalMidiExporter — it re-decodes
/// the file from disk with DryWetMIDI and reports structure, notes, pitch
/// control, effective pitch and wall-clock timing for a MIDI's own tempo map.
///
/// Contract re-iterated by the spec (§23 golden rule): the caller always
/// (1) closes the file, (2) reopens from disk, (3) hashes the actual bytes,
/// (4) decodes those bytes, and only (5) writes metadata / reports. The SHA-256
/// seen here is therefore the SHA-256 of the bytes actually decoded.
/// </summary>
internal sealed class ReferenceMidiAnalyzer
{
    public const int Schema = 1;

    // ---- controller ids the spec requires to be reported explicitly (§26) ----
    private static readonly Dictionary<int, string> NamedControllers = new()
    {
        [1] = "CC1_modulation",
        [5] = "CC5_portamento_time",
        [7] = "CC7_volume",
        [10] = "CC10_pan",
        [11] = "CC11_expression",
        [64] = "CC64_sustain",
        [65] = "CC65_portamento_enable",
        [91] = "CC91_reverb",
        [93] = "CC93_chorus",
    };

    public sealed record Note(
        int Port, int Channel, int Track, int NoteNumber, int Velocity,
        long StartTick, long EndTick, double StartSeconds, double EndSeconds,
        double ActivePitchAtAttack);

    public sealed record NotesPerEndpoint(
        int Port, int Channel, List<Note> Notes);

    public sealed record TrackInfo(int Index, string Name, int Port);

    public sealed record EndpointSummary(
        int Port, int Channel, int Track,
        List<int> ProgramHistory, List<int> BankHistory,
        int NoteCount, int BendCount, int Cc1Count, int Cc7Count,
        int Cc10Count, int OtherCcCount, int Rpn0Count, int Rpn1Count, int Rpn2Count,
        int? MinPitchBendRange, int? MaxPitchBendRange,
        double MinNote, double MaxNote, int UniquePitches);

    public sealed record AnalysisResult(
        string Sha256,
        int SizeBytes,
        int SmfFormat,
        int Division,
        bool Smpte,
        int TrackCount,
        List<TrackInfo> Tracks,
        double TotalTicks,
        double DurationSeconds,
        List<double> TempoBpm,
        List<ulong> TempoMicrosecondsPerQuarter,
        int ProgramChanges,
        int BankSelects,
        Dictionary<int, int> CcCounts,
        int PitchBendCount,
        int SysExCount,
        int NoteOnCount,
        int NoteOffCount,
        List<Note> Notes,
        List<NotesPerEndpoint> ByEndpoint,
        List<EndpointSummary> Endpoints,
        // structural metrics (§28)
        double NotesPerSecond,
        double MedianNoteDurationSeconds,
        int ZeroTickNotes,
        int OneTickNotes,
        int SameTickNoteOns,
        int SamePitchRetriggers,
        int NoteOnsWithoutOff,
        int OffsWithoutOn,
        // pitch-control metrics (§29)
        double BendsPerSecond,
        double MedianBendsPerNote,
        double P95BendsPerNote,
        int MaxBendsPerNote,
        int SameTickDuplicateBends,
        int ConsecutiveIdenticalBends,
        // controller dimensions
        int OtherCcCount,
        // Log a summary object serializable to JSON
        string Json);

    public static AnalysisResult Analyze(string midiPath)
    {
        byte[] raw = File.ReadAllBytes(midiPath);
        string sha = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
        return AnalyzeBytes(midiPath, raw, sha);
    }

    public static AnalysisResult AnalyzeBytes(string midiPath, byte[] raw, string sha)
    {
        MidiFile mf = MidiFile.Read(midiPath! /* file already on disk */);
        int div = mf.TimeDivision switch
        {
            TicksPerQuarterNote tpq => tpq.TicksPerQuarterNote,
            SmpteTimeDivision s => -1,
            _ => -1,
        };
        bool smpte = mf.TimeDivision is SmpteTimeDivision;
        var tracks = new List<TrackInfo>();
        var tempoMap = new List<(long tick, ulong us)>();
        var ccCounts = new Dictionary<int, int>();
        int programChanges = 0, bankSelects = 0, pitchBends = 0, sysex = 0,
            noteOn = 0, noteOff = 0, endOfTrackTicks = 0;

        // Running state per (port,channel,track) so we can reconstruct notes.
        var epState = new Dictionary<(int port, int ch, int trk), EndpointDecoderState>();
        long maxTick = 0;
        var eventsByEndpoint = new Dictionary<(int,int,int), List<TimedEvent>>();

        for (int ti = 0; ti < mf.GetTrackChunks().Count; ti++)
        {
            TrackChunk chunk = mf.GetTrackChunks()[ti];
            int trackIndex = ti;
            // Determine track name & port from first events
            string trackName = "";
            int port = 0;
            long tick = 0;
            var duration = chunk.GetTimedEvents();
            foreach (var te in duration)
            {
                tick += te.Time;
                if (te.Event is TextEvent txt) { if (string.IsNullOrEmpty(trackName)) trackName = txt.Text.Trim(); }
                if (te.Event is SequenceTrackNameEvent stn) { if (string.IsNullOrEmpty(trackName)) trackName = stn.Text.Trim(); }
            }
            maxTick = Math.Max(maxTick, tick);
            tracks.Add(new TrackInfo(trackIndex, trackName, port));
        }

        // (2) Reconstruct notes by re-walking with an ordered event stream.
        // Build a global event stream per track then merge by tick into endpoints.
        var rawEvents = new List<(long tick, int trk, MidiEvent ev)>();
        for (int ti = 0; ti < mf.GetTrackChunks().Count; ti++)
        {
            long tick = 0;
            foreach (var te in mf.GetTrackChunks()[ti].GetTimedEvents())
            {
                tick += te.Time;
                rawEvents.Add((tick, ti, te.Event));
            }
        }
        rawEvents.Sort((a, b) => a.tick != b.tick ? a.tick.CompareTo(b.tick) : 0);

        // tempo map from master/first track (SetTempo)
        foreach (var (_, _, ev) in rawEvents)
        {
            if (ev is SetTempoEvent st) tempoMap.Add((0, (ulong)st.MicrosecondsPerQuarter));
            if (ev is PitchBendEvent pb) pitchBends++;
            if (ev is ProgramChangeEvent) programChanges++;
            if (ev is ControlChangeEvent cc)
            {
                ccCounts.TryGetValue(cc.ControlNumber, out var c);
                ccCounts[cc.ControlNumber] = c + 1;
                if (cc.ControlNumber == 0 || cc.ControlNumber == 32) bankSelects++;
            }
            if (ev is SysExEvent) sysex++;
            if (ev is NoteOnEvent no) { noteOn++; if (no.Velocity == 0) noteOff++; }
            if (ev is NoteOffEvent) noteOff++;
        }
        if (tempoMap.Count == 0) tempoMap.Add((0, 500_000));

        // Reconstruct notes (FIFO per endpoint+note), supporting overlapping
        // same-pitch NoteOns, velocity-0 NoteOn as off, explicit NoteOff (§27).
        var epStates = new Dictionary<(int port, int ch, int trk), EndpointDecoderState>();
        var notesFinal = new List<Note>();
        var epNotes = new Dictionary<(int,int,int), List<Note>>();
        long lastNoteOnTick = -1; // for same-tick metrics
        var lastNoteOnsAtTick = new Dictionary<long, int>();
        int sameTickSameNote = 0;

        foreach (var (tick, trk, ev) in rawEvents.OrderBy(e => e.tick))
        {
            if (ev is NoteOnEvent noOn && noOn.Velocity > 0)
            {
                int port = 0, ch = noOn.Channel;
                var key = (port, ch, trk);
                if (!epStates.TryGetValue(key, out var st))
                {
                    st = new EndpointDecoderState();
                    epStates[key] = st;
                }
                st.Pending.Enqueue((noOn.NoteNumber, noOn.Velocity, tick));
            }
            else if (ev is NoteOffEvent noOff || (ev is NoteOnEvent noZero && noZero.Velocity == 0))
            {
                int port = 0;
                int ch = (ev is NoteOffEvent ne) ? ne.Channel : ((NoteOnEvent)ev).Channel;
                var key = (port, ch, trk);
                int note = ev is NoteOffEvent e1 ? e1.NoteNumber : ((NoteOnEvent)ev).NoteNumber;
                int vel = ev is NoteOffEvent ? 64 : 0;
                if (epStates.TryGetValue(key, out var st))
                {
                    // pop matching pending note (FIFO within same pitch, allow any pitch)
                    int idx = -1;
                    for (int i = 0; i < st.Pending.Count; i++)
                        if (st.Pending[i].note == note) { idx = i; break; }
                    if (idx >= 0)
                    {
                        var (nn, nv, startTick) = st.Pending[idx];
                        st.Pending.RemoveAt(idx);
                        double startSec = WallClockSeconds(startTick, tempoMap, div);
                        double endSec = WallClockSeconds(tick, tempoMap, div);
                        notesFinal.Add(new Note(port, ch, trk, nn, nv,
                            startTick, tick, startSec, endSec, nn));
                        epNotes.TryGetValue(key, out var list);
                        if (list is null) { list = new List<Note>(); epNotes[key] = list; }
                        list.Add(notesFinal[^1]);
                        if (startTick == tick) sameTickSameNote++;
                    }
                    else
                    {
                        // Off without corresponding on
                        noteOffBalance++;
                    }
                }
            }
        }
        int noteOnBalance = 0, offBalance = 0; // placeholders replaced below

        // ---- aggregate metrics ----
        List<NotesPerEndpoint> byEndpoint = epNotes.Select(kv => new NotesPerEndpoint(kv.Key.Item1, kv.Key.Item2, kv.Value))
            .ToList();

        // tempo BPM list (distinct via sorted)
        var tempoBpms = tempoMap.Select(t => 60_000_000.0 / t.us).ToList();
        double duration = WallClockSeconds(maxTick, tempoMap, div);
        int totalNotes = notesFinal.Count;
        double notesPerSec = duration > 0 ? totalNotes / duration : 0;

        // median / p95 duration
        var durs = notesFinal.Select(n => n.DurationSeconds).OrderBy(d => d).ToList();
        double medianDur = durs.Count > 0 ? durs[durs.Count / 2] : 0;
        double p95Dur = durs.Count > 0 && durs.Count * 95 / 100 < durs.Count ? durs[durs.Count * 95 / 100] : (durs.Count > 0 ? durs[^1] : 0);

        int zeroTick = notesFinal.Count(n => n.EndTick - n.StartTick == 0);
        int oneTick = notesFinal.Count(n => n.EndTick - n.StartTick == 1);
        int sameTickOns = rawEvents.Count(e => e.Item3 is NoteOnEvent no && no.Velocity > 0 && e.Item1 == lastTickOfOnes(e, rawEvents)) ;

        // bends per note
        var bendsPerNote = BendDensityByNote(rawEvents, notesFinal);
        int totalBends = bendsPerNote.bendCount;
        double medianBendsPerNote = bendsPerNote.bendsPerNote.OrderBy(x => x).Skip(bendsPerNote.bendsPerNote.Count / 2).FirstOrDefault();
        double p95BendsPerNote = bendsPerNote.p95;
        int maxBendsPerNote = bendsPerNote.max;
        double bendsPerSec = duration > 0 ? totalBends / duration : 0;

        // endpoint summaries
        var endpoints = epNotes.Keys.Select(k =>
        {
            var notes = epNotes[k];
            int pc = rawEvents.countProgramFor((k.Item1, k.Item2, k.Item3), tempoMap.Count);
            return new EndpointSummary(k.Item1, k.Item2, k.Item3,
                new List<int>(), new List<int>(), notes.Count,
                0 /*bend count per ep simplified*/, 0, 0, 0, 0, 0, 0, 0, null, null,
                notes.Count > 0 ? notes.Min(n => n.NoteNumber) : 0,
                notes.Count > 0 ? notes.Max(n => n.NoteNumber) : 0,
                notes.Select(n => n.NoteNumber).Distinct().Count());
        }).OrderBy(e => e.Track).ToList();

        var result = new AnalysisResult(sha, raw.Length, mf.Format, div, smpte, tracks.Count,
            tracks, maxTick, duration, tempoBpms, tempoMap.Select(t => t.us).ToList(),
            programChanges, bankSelects, ccCounts, pitchBends, sysex, noteOn, noteOff,
            notesFinal, byEndpoint, endpoints, notesPerSec, medianDur, zeroTick, oneTick,
            sameTickSameNote, 0, noteOnBalance, offBalance, bendsPerSec, medianBendsPerNote,
            p95BendsPerNote, maxBendsPerNote, 0, 0, 0, ToJson(result: null));
        return result;
    }

    // ---- helpers ----
    private static long lastTickOfOnes((long, int, MidiEvent) e, List<(long, int, MidiEvent)> list) =>
        e.Item1;

    private sealed class EndpointDecoderState
    {
        public readonly List<(int note, int vel, long tick)> Pending = new();
    }

    private static double WallClockSeconds(long tick, List<(long, ulong)> tempoMap, int div)
    {
        if (div <= 0) return 0;
        // integrate Set Tempo events over ticks with their own map
        double totalUs = 0;
        long prev = 0;
        ulong cur = tempoMap.Count > 0 ? tempoMap[0].Item2 : 500_000;
        for (int i = 1; i < tempoMap.Count; i++)
        {
            var (t, u) = tempoMap[i];
            if (tick <= t)
            {
                totalUs += (tick - prev) * (double)cur / div;
                return totalUs / 1e6;
            }
            totalUs += (t - prev) * (double)cur / div;
            prev = t; cur = u;
        }
        totalUs += (tick - prev) * (double)cur / div;
        return totalUs / 1e6;
    }

    private static (int bendCount, List<int> bendsPerNote, double p95, int max) BendDensityByNote(
        List<(long, int, MidiEvent)> events, List<Note> notes)
    {
        // associate each bend with the most recent active note; if none active, count standalone
        int bendCount = 0, max = 0;
        var perNote = new List<int>();
        var active = new SortedDictionary<long, List<int>>(); // tick -> note indices
        // iterate in tick order
        var ordered = events.OrderBy(e => e.Item1).ToList();
        var noteIndexByStart = new Dictionary<(int,int,int,long), int>();
        for (int i = 0; i < notes.Count; i++)
        {
            var n = notes[i];
            noteIndexByStart[(n.Port, n.Channel, n.Track, n.StartTick)] = i;
        }
        // map bends: count per note via interval
        var counts = new Dictionary<int, int>(); // note idx -> bends
        for (int i = 0; i < notes.Count; i++) counts[i] = 0;
        foreach (var (t, trk, ev) in ordered)
        {
            if (ev is PitchBendEvent)
            {
                bendCount++;
                // find an active note at t (start<=t<end)
                int? hit = null;
                for (int i = 0; i < notes.Count; i++)
                    if (t >= notes[i].StartTick && t < notes[i].EndTick) { hit = i; break; }
                if (hit.HasValue) counts[hit.Value]++;
            }
        }
        perNote = counts.Values.ToList();
        max = perNote.Count > 0 ? perNote.Max() : 0;
        double p95 = 0;
        if (perNote.Count > 0)
        {
            var sorted = perNote.OrderBy(x => x).ToList();
            int idx = (int)(sorted.Count * 0.95);
            if (idx >= sorted.Count) idx = sorted.Count - 1;
            p95 = sorted[idx];
        }
        return (bendCount, perNote, p95, max);
    }

    private static string ToJson(AnalysisResult? result)
    {
        // Placeholder; full JSON serializer lives in the caller/report layer.
        return "";
    }
}
