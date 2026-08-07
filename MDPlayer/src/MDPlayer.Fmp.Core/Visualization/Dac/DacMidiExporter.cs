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
/// Serializes a <see cref="DacAnalysisReport"/> into a deterministic
/// standard MIDI file (format 1, single track group) that treats each DAC
/// playback event as a sample trigger (spec §18). Playback rate is carried as
/// metadata; the note sequence is identity-first with correct retrigger
/// ordering (old note-off before new note-on).
/// </summary>
internal sealed class DacMidiExporter
{
    private readonly int _ppqn;
    private readonly int _bpm;
    private readonly int _sampleRate;
    private readonly int _noteBase;
    private readonly int _notesPerBank;

    public DacMidiExporter(
        int sampleRate,
        int ppqn = 480,
        int bpm = 120,
        int noteBase = 0,
        int notesPerBank = 128)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (ppqn <= 0)
            throw new ArgumentOutOfRangeException(nameof(ppqn));
        if (bpm <= 0)
            throw new ArgumentOutOfRangeException(nameof(bpm));
        _sampleRate = sampleRate;
        _ppqn = ppqn;
        _bpm = bpm;
        _noteBase = noteBase;
        _notesPerBank = notesPerBank;
    }

    /// <summary>Nominal ticks corresponding to one timeline sample position.</summary>
    private double TicksPerSample => (double)_ppqn * _bpm / 60.0 / _sampleRate;

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

            long startTick = Ticks(evt.StartSample);
            long endTick = Ticks(Math.Max(evt.StartSample, evt.EndSample));

            events.Add(new DacMidiEvent(
                startTick, track, channel, NoteOn: true, asset.DisplayNote,
                Velocity: DefaultVelocity, Text: null));
            events.Add(new DacMidiEvent(
                endTick, track, channel, NoteOn: false, asset.DisplayNote,
                Velocity: 0, Text: null));

            if (evt.InitialRateHz is double rate && rate > 0)
            {
                events.Add(new DacMidiEvent(
                    startTick, track, channel, NoteOn: false, asset.DisplayNote,
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
            trackNames: TrackNames(report.Assets.Count));
    }

    private static int TrackCount(int assetCount)
        => assetCount <= 0 ? 1 : (assetCount - 1) / 16 + 1;

    private static string[] TrackNames(int assetCount)
    {
        int tracks = Math.Max(1, TrackCount(assetCount));
        if (tracks == 1)
            return ["YM2612 DAC Samples"];
        return Enumerable.Range(1, tracks)
            .Select(i => $"YM2612 DAC Samples {i}")
            .ToArray();
    }

    private long Ticks(long sample) => (long)Math.Round(sample * TicksPerSample, MidpointRounding.AwayFromZero);

    public const int DefaultVelocity = 100;
}