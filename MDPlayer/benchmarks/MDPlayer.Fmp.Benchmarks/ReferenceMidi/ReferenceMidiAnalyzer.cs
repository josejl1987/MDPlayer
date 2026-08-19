using System.Security.Cryptography;
using System.Text.Json;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace Fmp.Benchmarks;

/// <summary>
/// Independent reference MIDI analyzer. Decodes a serialized .mid file using
/// ONLY DryWetMIDI 8.0.3 plus the raw bytes (SHA-256), with no dependency on
/// VisualizationTimeline / MusicalTimeMap / MusicalMidiExporter internals.
/// Note reconstruction is a pure FIFO per-(port,channel,note) matcher that
/// treats velocity-0 NoteOn as a NoteOff and supports overlapping same-pitch
/// attacks. Tempo integration uses the file's OWN Set Tempo events.
/// </summary>
internal static class ReferenceMidiAnalyzer
{
    // ---- Nested result records (System.Text.Json serializable) ----

    public sealed record FileInfoOut(string Sha256, long SizeBytes, int SmfFormat, int Division, bool Smpte, int TrackCount);
    public sealed record TrackInfo(int Index, string? Name, int Port);
    public sealed record DurationOut(long TotalTicks, double DurationSeconds);
    public sealed record TempoEntry(int UsPerQuarter, double Bpm);
    public sealed record CountsOut(
        int ProgramChanges, int BankSelects, int PitchBendCount, int SysExCount,
        int NoteOnCount, int NoteOffCount,
        IReadOnlyDictionary<int, int> CcCounts, int OtherCcCount);

    public sealed record NoteOut(
        int Port, int Channel, int Track, int Note, int Velocity,
        long StartTick, long EndTick, double StartSeconds, double EndSeconds,
        double ActivePitchAtAttack, double PitchAtEnd);

    public sealed record EndpointOut(int Port, int Channel, IReadOnlyList<ReferenceMidiNoteInfo> Notes)
    {
        // serialized via its own record below; kept as a thin container
    }

    /// <summary>The per-note detail shipped inside byEndpoint entries.</summary>
    public sealed record ReferenceMidiNoteInfo(
        int Note, int Velocity, long StartTick, long EndTick,
        double StartSeconds, double EndSeconds, double ActivePitchAtAttack);

    public sealed record StructuralOut(
        double NotesPerSecond, double MedianNoteDurationSeconds,
        double P05DurationSeconds, double P50DurationSeconds, double P95DurationSeconds,
        int ZeroTickNotes, int OneTickNotes, int SameTickNoteOns, int SamePitchRetriggers,
        int NoteOnsWithoutOff, int OffsWithoutOn,
        int? PitchRangeMin, int? PitchRangeMax,
        int UniquePitches, IReadOnlyDictionary<int, int> PitchClassHistogram);

    public sealed record PitchControlOut(
        double BendsPerSecond, IReadOnlyList<int> BendsPerNote,
        double MedianBendsPerNote, double P95BendsPerNote, double MaxBendsPerNote,
        int SameTickDuplicateBends, int ConsecutiveIdenticalBends,
        IReadOnlyList<double> BendRangesUsed, IReadOnlyDictionary<string, int> RangeChangesPerChannel);

    public sealed record AnalyzerResult(
        FileInfoOut File, IReadOnlyList<TrackInfo> Tracks,
        DurationOut Duration, IReadOnlyList<TempoEntry> Tempo,
        CountsOut Counts, IReadOnlyList<NoteOut> Notes,
        IReadOnlyList<EndpointOut> ByEndpoint,
        StructuralOut Structural, PitchControlOut PitchControl);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string ToJson(AnalyzerResult result)
        => JsonSerializer.Serialize(result, JsonOptions);

    /// <summary>Lightweight tempo map (ppq + Set Tempo curve) shared with the comparator.</summary>
    internal sealed record TempoMap(int Ppq, IReadOnlyList<(long Tick, int UsPerQuarter)> Curve);

    internal static TempoMap ReadTempoMap(string midiPath)
    {
        MidiFile f = MidiFile.Read(midiPath);
        int ppq = f.TimeDivision is TicksPerQuarterNoteTimeDivision t ? t.TicksPerQuarterNote : -1;
        var raw = new List<(long Tick, int UsPerQuarter)>();
        foreach (TrackChunk c in f.GetTrackChunks())
        {
            foreach (TimedEvent ev in c.GetTimedEvents())
            {
                if (ev.Event is SetTempoEvent te)
                    raw.Add((ev.Time, (int)te.MicrosecondsPerQuarterNote));
            }
        }
        raw.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        var curve = new List<(long Tick, int UsPerQuarter)>();
        foreach (var (tick, us) in raw)
        {
            if (curve.Count > 0 && curve[^1].Tick == tick)
                curve[^1] = (tick, us);
            else
                curve.Add((tick, us));
        }
        return new TempoMap(ppq, curve);
    }

    /// <summary>Seconds occupied by a single tick at the given absolute tick, from the local tempo.</summary>
    internal static double TickDurationSeconds(TempoMap map, long tick)
    {
        if (map.Ppq <= 0) return 0.0;
        int us = 500_000;
        foreach (var (t, u) in map.Curve)
        {
            if (tick >= t) us = u; else break;
        }
        return us / (1_000_000.0 * map.Ppq);
    }

    /// <summary>Analyzes a serialized .mid file and returns the structural report.</summary>
    public static AnalyzerResult Analyze(string midiPath)
    {
        byte[] bytes = File.ReadAllBytes(midiPath);
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        MidiFile midiFile = MidiFile.Read(midiPath);
        int smfFormat = ReadHeaderFormat(bytes);

        TrackChunk[] chunks = midiFile.GetTrackChunks().ToArray();

        bool smpte = midiFile.TimeDivision is SmpteTimeDivision;
        int division = midiFile.TimeDivision switch
        {
            TicksPerQuarterNoteTimeDivision t => t.TicksPerQuarterNote,
            _ => -1,
        };

        // ---- Tempo map from the file's own Set Tempo events ----
        var tempoEvents = new List<(long tick, int usPerQuarter)>();
        long lastTick = 0;
        foreach (TrackChunk chunk in chunks)
        {
            foreach (TimedEvent ev in chunk.GetTimedEvents())
            {
                if (ev.Event is SetTempoEvent tempo)
                    tempoEvents.Add((ev.Time, (int)tempo.MicrosecondsPerQuarterNote));
                if (ev.Time > lastTick) lastTick = ev.Time;
            }
        }
        tempoEvents.Sort((a, b) => a.tick.CompareTo(b.tick));
        long totalTicks = lastTick;

        int ppq = division > 0 ? division : 1;
        // built tempo curve: list of (startTick, usPerQuarter), merged duplicates at same tick
        var curve = new List<(long tick, int usPerQuarter)>();
        foreach (var (tick, us) in tempoEvents)
        {
            if (curve.Count > 0 && curve[^1].tick == tick)
                curve[^1] = (tick, us); // last one at same tick wins
            else
                curve.Add((tick, us));
        }
        double durationSeconds = smpte ? 0.0 : IntegrateSeconds(totalTicks, ppq, curve);
        var tempoList = curve.Select(c => new TempoEntry(c.usPerQuarter, Bpm(c.usPerQuarter))).ToList();

        double TickToSeconds(long tick)
        {
            if (smpte) return 0.0;
            double seconds = 0.0;
            long prev = 0;
            int us = 500_000; // initial 120 BPM default
            foreach (var (t, u) in curve)
            {
                long segmentTicks = t - prev;
                if (segmentTicks > 0)
                    seconds += segmentTicks * us / (1_000_000.0 * ppq);
                us = u;
                prev = t;
                if (tick <= t)
                {
                    if (tick < t)
                    {
                        // roll back the overshoot
                        seconds -= (t - tick) * us / (1_000_000.0 * ppq);
                    }
                    break;
                }
            }
            if (tick > prev)
                seconds += (tick - prev) * us / (1_000_000.0 * ppq);
            return seconds;
        }

        // ---- Flatten timed events across tracks, stable by time ----
        var evts = new List<(long time, MidiEvent midiEvent, int track)>();
        for (int ti = 0; ti < chunks.Length; ti++)
        {
            foreach (TimedEvent ev in chunks[ti].GetTimedEvents())
                evts.Add((ev.Time, ev.Event, ti));
        }
        evts.Sort((a, b) =>
        {
            int c = a.time.CompareTo(b.time);
            return c != 0 ? c : a.track.CompareTo(b.track);
        });

        // ---- Track names ----
        var trackNames = new List<string?>();
        for (int ti = 0; ti < chunks.Length; ti++)
        {
            string? name = null;
            foreach (TimedEvent ev in chunks[ti].GetTimedEvents())
            {
                if (ev.Event is SequenceTrackNameEvent seq && name is null)
                    name = seq.Text;
            }
            trackNames.Add(name);
        }
        var tracks = chunks.Select((c, i) => new TrackInfo(i, trackNames[i], PortOf(i))).ToList();

        // ---- Note reconstruction (FIFO per port/channel/note) ----
        string key(int port, int channel, int note) => $"{port}:{channel}:{note}";
        var open = new Dictionary<string, Queue<AttackRec>>();
        var notes = new List<NoteOut>();

        // bend tracking
        var activeBend = new Dictionary<string, int>();   // endpoint key -> PitchValue (0..16383)
        var activeRange = new Dictionary<string, double>(); // endpoint key -> bend range semitones
        // RPN state for bend-range changes (controller 101/100/6)
        var rpn = new Dictionary<string, int[]>();         // endpoint key -> [msb, lsb]

        int noteOnCount = 0;
        int noteOffCount = 0;
        int programChanges = 0;
        int bankSelects = 0;
        int pitchBendCount = 0;
        int sysExCount = 0;
        var ccCounts = new Dictionary<int, int>();
        int otherCcCount = 0;
        int sameTickNoteOns = 0;
        int samePitchRetriggers = 0;
        int noteOnsWithoutOff = 0;
        int offsWithoutOn = 0;
        var pitchClassHistogram = new Dictionary<int, int>();
        var pitchSet = new HashSet<int>();

        var allBends = new List<(string endpoint, long tick, int value, int track, int channel)>();
        var rangeChanges = new Dictionary<string, int>(); // channel -> count of bend-range RPN changes

        for (int i = 0; i < evts.Count; i++)
        {
            var (time, midiEvent, track) = evts[i];
            int port = PortOf(track);

            switch (midiEvent)
            {
                case NoteOnEvent on when on.Velocity > 0:
                {
                    noteOnCount++;
                    int channel = on.Channel;
                    int note = on.NoteNumber;
                    string ek = EndpointKey(port, channel);
                    string k = key(port, channel, note);
                    if (!open.TryGetValue(k, out var q)) { q = new Queue<AttackRec>(); open[k] = q; }
                    bool wasActive = q.Count > 0;
                    double range = activeRange.TryGetValue(ek, out double r) ? r : 24.0;
                    double bend = BendOffset(activeBend.TryGetValue(ek, out int pv) ? pv : 8192, range);
                    q.Enqueue(new AttackRec(time, on.Velocity, note, track, range, bend));
                    pitchClassHistogram.TryGetValue((note % 12 + 12) % 12, out int pc);
                    pitchClassHistogram[(note % 12 + 12) % 12] = pc + 1;
                    pitchSet.Add(note);
                    if (wasActive) samePitchRetriggers++;
                    break;
                }
                case NoteOnEvent on: // velocity == 0 => off
                case NoteOffEvent off:
                {
                    noteOffCount++;
                    int channel = midiEvent is NoteOnEvent zon ? zon.Channel : ((NoteOffEvent)midiEvent).Channel;
                    int note = midiEvent is NoteOnEvent ? ((NoteOnEvent)midiEvent).NoteNumber : ((NoteOffEvent)midiEvent).NoteNumber;
                    string k = key(port, channel, note);
                    if (open.TryGetValue(k, out var q) && q.Count > 0)
                    {
                        AttackRec attack = q.Dequeue();
                        string ek = EndpointKey(port, channel);
                        double range = activeRange.TryGetValue(ek, out double rr) ? rr : 24.0;
                        double endBend = BendOffset(activeBend.TryGetValue(ek, out int epv) ? epv : 8192, range);
                        var noteOut = new NoteOut(
                            port, channel, track, note, attack.Velocity,
                            attack.StartTick, time, TickToSeconds(attack.StartTick), TickToSeconds(time),
                            note + attack.BendOffsetAtAttack, note + endBend);
                        notes.Add(noteOut);
                    }
                    else
                    {
                        offsWithoutOn++;
                    }
                    break;
                }
                case PitchBendEvent bend:
                {
                    pitchBendCount++;
                    int channel = bend.Channel;
                    string ek = EndpointKey(port, channel);
                    activeBend[ek] = bend.PitchValue;
                    allBends.Add((ek, time, bend.PitchValue, track, channel));
                    break;
                }
                case ProgramChangeEvent pc:
                    programChanges++;
                    break;
                case ControlChangeEvent cc:
                {
                    int ctrl = cc.ControlNumber;
                    if (ctrl is 0 or 32) { bankSelects++; }
                    else if (ccCounts.ContainsKey(ctrl)) ccCounts[ctrl]++;
                    else ccCounts[ctrl] = 1;
                    // RPN/NRPN bend-range handling (RPN 0,0 = pitch bend range, channel)
                    int channel = cc.Channel;
                    string ek = EndpointKey(port, channel);
                    if (ctrl == 101 || ctrl == 100)
                    {
                        if (!rpn.TryGetValue(ek, out var rr)) { rr = new int[2]; rpn[ek] = rr; }
                        rr[ctrl == 101 ? 0 : 1] = cc.ControlValue;
                    }
                    else if (ctrl == 6)
                    {
                        if (rpn.TryGetValue(ek, out var rr) && rr[0] == 0 && rr[1] == 0)
                        {
                            double newRange = cc.ControlValue;
                            activeRange[ek] = newRange > 0 ? newRange : 24.0;
                            rangeChanges.TryGetValue(channel.ToString(), out int rc);
                            rangeChanges[channel.ToString()] = rc + 1;
                        }
                    }
                    else
                    {
                        otherCcCount++;
                    }
                    break;
                }
                case SysExEvent:
                    sysExCount++;
                    break;
            }

            // same-tick reusable detection: any two note-ons at identical (endpoint,tick,note) -> sameTick
        }

        // ---- Same-tick NoteOns: count of (endpoint,note) groups with >1 distinct same-tick attacks ----
        var onGroups = new Dictionary<string, int>();
        foreach (var (time, midiEvent, track) in evts)
        {
            if (midiEvent is NoteOnEvent on && on.Velocity > 0)
            {
                string gk = key(PortOf(track), on.Channel, on.NoteNumber) + "@" + time;
                onGroups.TryGetValue(gk, out int c);
                onGroups[gk] = c + 1;
            }
        }
        sameTickNoteOns = onGroups.Values.Where(c => c > 1).Sum(c => c - 1);

        // ---- Close unclosed notes at file end ----
        foreach (var kv in open)
        {
            foreach (AttackRec attack in kv.Value)
            {
                string[] parts = kv.Key.Split(':');
                int port = int.Parse(parts[0]);
                int channel = int.Parse(parts[1]);
                int note = int.Parse(parts[2]);
                string ek = EndpointKey(port, channel);
                double range = activeRange.TryGetValue(ek, out double rr) ? rr : 24.0;
                double endBend = BendOffset(activeBend.TryGetValue(ek, out int epv) ? epv : 8192, range);
                notes.Add(new NoteOut(port, channel, attack.Track, note, attack.Velocity,
                    attack.StartTick, totalTicks, TickToSeconds(attack.StartTick), TickToSeconds(totalTicks),
                    note + attack.BendOffsetAtAttack, note + endBend));
                noteOnsWithoutOff++;
            }
        }

        // ---- Structural ----
        var durationsSeconds = notes.Select(n => n.EndSeconds - n.StartSeconds).OrderBy(d => d).ToList();
        double medianDur = Percentile(durationsSeconds, 0.50);
        int zeroTick = notes.Count(n => n.EndTick == n.StartTick);
        int oneTick = notes.Count(n => n.EndTick == n.StartTick + 1);
        int? pitchMin = notes.Count == 0 ? (int?)null : notes.Min(n => n.Note);
        int? pitchMax = notes.Count == 0 ? (int?)null : notes.Max(n => n.Note);
        int uniquePitches = pitchSet.Count;
        var structural = new StructuralOut(
            NotesPerSecond: SafeDivide(notes.Count, durationSeconds),
            MedianNoteDurationSeconds: medianDur,
            P05DurationSeconds: Percentile(durationsSeconds, 0.05),
            P50DurationSeconds: Percentile(durationsSeconds, 0.50),
            P95DurationSeconds: Percentile(durationsSeconds, 0.95),
            ZeroTickNotes: zeroTick,
            OneTickNotes: oneTick,
            SameTickNoteOns: sameTickNoteOns,
            SamePitchRetriggers: samePitchRetriggers,
            NoteOnsWithoutOff: noteOnsWithoutOff,
            OffsWithoutOn: offsWithoutOn,
            PitchRangeMin: pitchMin,
            PitchRangeMax: pitchMax,
            UniquePitches: uniquePitches,
            PitchClassHistogram: pitchClassHistogram);

        // ---- Pitch control ----
        var bendsPerNote = new List<int>();
        foreach (var nr in notesSorted(notes))
        {
            string ek = EndpointKey(nr.Port, nr.Channel);
            int count = allBends.Count(b =>
                b.endpoint == ek && b.tick >= nr.StartTick && b.tick <= nr.EndTick);
            bendsPerNote.Add(count);
        }
        var sortedBpn = bendsPerNote.OrderBy(x => x).ToList();
        // bends per second
        double bendsPerSecond = SafeDivide(allBends.Count, durationSeconds);
        // same tick duplicate bends per endpoint
        int sameTickDuplicateBends = 0;
        foreach (var grp in allBends.GroupBy(b => b.endpoint + "@" + b.tick))
            if (grp.Count() > 1) sameTickDuplicateBends += grp.Count() - 1;
        // consecutive identical bends per endpoint (time-ordered)
        int consecutiveIdenticalBends = 0;
        foreach (var grp in allBends.GroupBy(b => b.endpoint))
        {
            List<(long tick, int value)> ordered = grp.OrderBy(b => b.tick).Select(b => (b.tick, b.value)).ToList();
            for (int j = 1; j < ordered.Count; j++)
                if (ordered[j].value == ordered[j - 1].value && IsSameBendNeighbor(ordered[j - 1].tick, ordered[j].tick))
                    consecutiveIdenticalBends++;
        }

        var pitchControl = new PitchControlOut(
            BendsPerSecond: bendsPerSecond,
            BendsPerNote: bendsPerNote,
            MedianBendsPerNote: MedianOfInts(sortedBpn),
            P95BendsPerNote: PercentileOfInts(sortedBpn, 0.95),
            MaxBendsPerNote: sortedBpn.Count > 0 ? sortedBpn[^1] : 0,
            SameTickDuplicateBends: sameTickDuplicateBends,
            ConsecutiveIdenticalBends: consecutiveIdenticalBends,
            BendRangesUsed: BendRangesUsed(activeRange, activeBend),
            RangeChangesPerChannel: rangeChanges);

        // ---- byEndpoint ----
        var byEndpoint = BuildByEndpoint(notes);

        var result = new AnalyzerResult(
            new FileInfoOut(sha, bytes.LongLength, smfFormat, division, smpte, chunks.Length),
            tracks,
            new DurationOut(totalTicks, durationSeconds),
            tempoList,
            new CountsOut(programChanges, bankSelects, pitchBendCount, sysExCount,
                noteOnCount, noteOffCount, ccCounts, otherCcCount),
            notes.OrderBy(n => (n.StartTick, n.Port, n.Channel, n.Track, n.Note)).ToList(),
            byEndpoint,
            structural,
            pitchControl);

        return result;
    }

    // ================= helpers =================

    private sealed record AttackRec(long StartTick, int Velocity, int Note, int Track, double Range, double BendOffsetAtAttack);

    private const double DefaultBendRangeSemitones = 24.0;

    /// <summary>Port derived from track index (standard MIDI has a single port stream per track).</summary>
    private static int PortOf(int track) => track;

    private static string EndpointKey(int port, int channel) => $"{port}:{channel}";

    /// <summary>Sign-symmetric bend decode: +/-Full range across the 0..16383 window centered at 8192.</summary>
    private static double BendOffset(int pitchValue, double rangeSemitones)
        => ((pitchValue - 8192) / 8192.0) * rangeSemitones;

    private static double Bpm(int usPerQuarter) => usPerQuarter > 0 ? 60_000_000.0 / usPerQuarter : 0.0;

    private static double IntegrateSeconds(long targetTick, int ppq, List<(long tick, int us)> curve)
    {
        if (ppq <= 0) return 0.0;
        double seconds = 0.0;
        long prev = 0;
        int us = 500_000;
        foreach (var (t, u) in curve)
        {
            long seg = t - prev;
            if (seg > 0) seconds += seg * us / (1_000_000.0 * ppq);
            us = u;
            prev = t;
            if (targetTick <= t)
            {
                if (targetTick < t)
                    seconds -= (t - targetTick) * us / (1_000_000.0 * ppq);
                return seconds;
            }
        }
        if (targetTick > prev)
            seconds += (targetTick - prev) * us / (1_000_000.0 * ppq);
        return seconds;
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0.0;
        int exact = (int)Math.Floor(p * (sorted.Count - 1));
        exact = Math.Clamp(exact, 0, sorted.Count - 1);
        return sorted[exact];
    }

    private static double MedianOfInts(List<int> sorted)
        => sorted.Count == 0 ? 0.0 : sorted[(sorted.Count - 1) / 2];

    private static double PercentileOfInts(List<int> sorted, double p)
    {
        if (sorted.Count == 0) return 0.0;
        int idx = Math.Clamp((int)Math.Floor(p * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[idx];
    }

    private static double SafeDivide(double numerator, double denominator)
        => denominator > 1e-9 ? numerator / denominator : 0.0;

    private static bool IsSameBendNeighbor(long a, long b) => true; // time-adjacent regardless of tick

    private static IReadOnlyList<double> BendRangesUsed(
        IReadOnlyDictionary<string, double> activeRange,
        IReadOnlyDictionary<string, int> activeBend)
    {
        var set = new HashSet<double>();
        // add the default range always plus any explicit set
        foreach (var range in activeRange.Values)
            set.Add(range);
        if (set.Count == 0) set.Add(DefaultBendRangeSemitones);
        return set.OrderBy(x => x).ToList();
    }

    private static IReadOnlyList<EndpointOut> BuildByEndpoint(IReadOnlyList<NoteOut> notes)
    {
        var groups = notes
            .GroupBy(n => (n.Port, n.Channel))
            .OrderBy(g => g.Key.Port).ThenBy(g => g.Key.Channel)
            .ToList();
        var result = new List<EndpointOut>();
        foreach (var g in groups)
        {
            var infos = g
                .OrderBy(n => n.StartTick)
                .Select(n => new ReferenceMidiNoteInfo(n.Note, n.Velocity, n.StartTick, n.EndTick,
                    n.StartSeconds, n.EndSeconds, n.ActivePitchAtAttack))
                .ToList();
            result.Add(new EndpointOut(g.Key.Port, g.Key.Channel, infos));
        }
        return result;
    }

    /// <summary>Sorted note projection used for bend-per-note counting.</summary>
    private static IEnumerable<(int Port, int Channel, long StartTick, long EndTick)> notesSorted(IReadOnlyList<NoteOut> notes)
        => notes.Select(n => (n.Port, n.Channel, n.StartTick, n.EndTick));

    private static int ReadHeaderFormat(byte[] bytes)
    {
        // SMF header: "MThd" <len> <format:2> <ntrk:2> <division:2> at byte offset 8
        if (bytes.Length >= 10 && bytes[0] == (byte)'M' && bytes[1] == (byte)'T'
            && bytes[2] == (byte)'h' && bytes[3] == (byte)'d')
            return (bytes[8] << 8) | bytes[9];
        return -1;
    }
}
