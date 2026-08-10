using Fmp.Core.Timing;

namespace Fmp.Core.Visualization;

/// <summary>
/// A single timed MIDI event in the DAC sample-trigger export stream. The note
/// carries the sample identity (via the asset's display note/channel), never a
/// pitch estimate.
/// </summary>
internal sealed record DacMidiEvent(
    long Tick,
    int Track,
    int Channel,
    bool NoteOn,
    int Note,
    int Velocity,
    string? Text)
{
    /// <summary>Dedicated SysEx family used to ship playback-rate metadata.</summary>
    public const string RateMetaTextPrefix = "DACRATE:";
}

/// <summary>
/// A Set Tempo (FF 51) event on the DAC conductor track: the tempo value at the
/// tick where a <see cref="MusicalTimeMap"/> tempo segment begins. One is emitted
/// per distinct tempo segment so multi-segment maps change tempo at every boundary
/// (mirrors the melodic conductor, §19).
/// </summary>
internal sealed record DacTempoEvent(long Tick, int MicrosecondsPerQuarter);

/// <summary>
/// Serializes a <see cref="DacAnalysisReport"/> into a deterministic
/// standard MIDI file (format 1, single track group) that treats each DAC
/// playback event as a sample trigger (spec §18). Playback rate is carried as
/// metadata; the note sequence is identity-first with correct retrigger
/// ordering (old note-off before new note-on).
///
/// Timing separation (spec §37/§38): this exporter owns NO timer. It receives an
/// already-established <see cref="MusicalTimeMap"/> and applies exactly the same
/// global non-negative origin policy as the melodic exporter, so a DAC trigger
/// sample maps to the same musical tick as any identical melodic/rhythm trigger
/// (§67). It never derives ticks from sample rate, a fixed BPM, or a local BPM
/// default. DAC sample identity (raw PCM → canonical ID → MIDI note) is carried
/// by the catalog assets and is fully independent of trigger→tick (§68).
/// </summary>
internal sealed class DacMidiExporter
{
    private readonly MusicalTimeMap _map;
    private readonly int _ppqn;
    private readonly long _originShiftTicks;
    // Identity policy only (§38): note base shifts the sample-ID→note label but
    // never the trigger→tick conversion.
    private readonly int _noteBase;
    // Mirrors the melodic exporter's EmitMarkers gate (§21): the first-downbeat
    // quarter is folded into the origin ONLY when markers are emitted, so DAC and
    // melodic/rhythm origins stay identical whether markers are on or off (§67).
    private readonly bool _emitMarkers;

    public DacMidiExporter(MusicalTimeMap map, int ppqn = 480, int noteBase = 0, bool emitMarkers = true)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));
        if (ppqn <= 0)
            throw new ArgumentOutOfRangeException(nameof(ppqn));
        if (noteBase is < 0 or > 127)
            throw new ArgumentOutOfRangeException(nameof(noteBase));
        _ppqn = ppqn;
        _noteBase = noteBase;
        _emitMarkers = emitMarkers;
        _originShiftTicks = ComputeOriginShiftTicks();
    }

    /// <summary>Identity offset applied to the asset's display note (never timing).</summary>
    private int NoteFor(DacSampleAsset asset) => (_noteBase + asset.DisplayNote) & 0x7F;

    /// <summary>
    /// Produces the ordered MIDI trigger event stream for a report. Events are
    /// emitted oldest-to-newest; same-timestamp retriggers order the prior
    /// note-off before the new note-on (spec §18.3).
    /// </summary>
    public IReadOnlyList<DacMidiEvent> BuildEvents(DacAnalysisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        int trackCount = TrackCount(report.Assets.Count);
        var events = new List<DacMidiEvent>();

        foreach (DacPlaybackEvent evt in report.PlaybackEvents)
        {
            int assetId = evt.AssetId ?? -1;
            if (assetId < 0 || assetId >= report.Assets.Count)
                continue;
            DacSampleAsset asset = report.Assets[assetId];

            // Disambiguate destination: bank selects channel, groups of 16
            // banks select additional tracks (spec §17 / §18).
            int track = asset.DisplayBank / 16;
            int channel = asset.DisplayBank % 16;

            long startTick = MapTick(evt.StartSample);
            long endTick = MapTick(Math.Max(evt.StartSample, evt.EndSample));

            events.Add(new DacMidiEvent(
                startTick, track, channel, NoteOn: true, NoteFor(asset),
                Velocity: DefaultVelocity, Text: null));
            events.Add(new DacMidiEvent(
                endTick, track, channel, NoteOn: false, NoteFor(asset),
                Velocity: 0, Text: null));

            if (evt.InitialRateHz is double rate && rate > 0)
            {
                events.Add(new DacMidiEvent(
                    startTick, track, channel, NoteOn: false, NoteFor(asset),
                    Velocity: 0, Text: $"{DacMidiEvent.RateMetaTextPrefix}{rate:0.###}"));
            }
        }

        // Stable ordering: track, tick, then note-off before note-on so a same
        // note retriggered at the same tick is closed before reopened.
        return events
            .OrderBy(e => e.Track)
            .ThenBy(e => e.Tick)
            .ThenBy(e => e.NoteOn ? 1 : 0) // note-off (0) sorts before note-on (1)
            .ThenBy(e => e.Note)
            .ToArray();
    }

    /// <summary>Serializes the report to a standard MIDI file (format 1).</summary>
    public byte[] Write(DacAnalysisReport report)
    {
        IReadOnlyList<DacMidiEvent> events = BuildEvents(report);
        return MidiFileWriter.Write(
            _ppqn,
            events,
            trackNames: TrackNames(report.Assets.Count),
            tempoEvents: BuildTempoEvents());
    }

    /// <summary>
    /// Serializes a Set Tempo event for every tempo segment at its sample-derived
    /// tick, deduping adjacent segments whose emitted µs/qn value is identical
    /// (mirror of the melodic conductor, §19). BuildEvents maps triggers through
    /// each segment's changing quarter position, so the conductor must switch tempo
    /// at every later boundary or DAC playback tempo would diverge from the map.
    /// </summary>
    private IReadOnlyList<DacTempoEvent> BuildTempoEvents()
    {
        var tempoEvents = new List<DacTempoEvent>();
        int? lastUsPerQuarter = null;
        foreach (TempoSegment segment in _map.Segments)
        {
            int us = segment.MicrosecondsPerQuarter;
            if (us == lastUsPerQuarter)
                continue;
            lastUsPerQuarter = us;
            long tick = MapTick(segment.StartSample);
            tempoEvents.Add(new DacTempoEvent(tick, us));
        }
        return tempoEvents;
    }

    private static int TrackCount(int assetCount)
        => assetCount <= 0 ? 1 : (assetCount - 1) / 16 + 1;

    private static string[] TrackNames(int assetCount)
    {
        // One track per DAC source channel, named deterministically from the
        // canonical dac: identity (unified with the melodic exporter's scheme, R8).
        int tracks = Math.Max(1, TrackCount(assetCount));
        if (tracks == 1)
            return ["YM2612 DAC"];
        return Enumerable.Range(1, tracks)
            .Select(i => $"YM2612 DAC dac:{i:000}")
            .ToArray();
    }

    /// <summary>
    /// Maps a DAC trigger sample to an absolute MIDI tick through the shared
    /// <see cref="MusicalTimeMap"/> plus the same single global origin shift the
    /// melodic exporter applies (spec §21, §37). No sample-rate/BPM math here —
    /// this is the only path from a DAC trigger sample to a tick, so it can never
    /// diverge from the musical grid.
    /// </summary>
    private long MapTick(long sample) =>
        _map.SampleToTick(sample, _ppqn) + _originShiftTicks;

    /// <summary>
    /// Computes the single global non-negative integer tick origin (spec §21) using
    /// the same policy as the melodic exporter: the map's FirstSample tick is the
    /// lower bound (every DAC trigger sits at or after it) and, mirroring the
    /// melodic exporter's EmitMarkers gate, the first downbeat is folded in only
    /// when markers are emitted. The shift is the minimal whole tick that makes
    /// every emitted trigger nonnegative — never aligned to quarter/bar.
    /// </summary>
    private long ComputeOriginShiftTicks()
    {
        long minTick = _map.SampleToTick(_map.FirstSample, _ppqn);
        if (_emitMarkers && _map.FirstDownbeatQuarter is double downbeat)
            minTick = Math.Min(minTick, _map.QuarterPositionToTick(downbeat, _ppqn));
        return Math.Max(0, -minTick);
    }

    public const int DefaultVelocity = 100;
}
