using Fmp.Core.Midi;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;

namespace Fmp.Application.Export;

/// <summary>
/// Exports a captured <see cref="VisualizationTimeline"/> as a Standard MIDI
/// File (Format 1) entirely in-process by routing every event through the
/// canonical <see cref="MusicalTimeMap"/>. This is the shared, architecture-safe
/// entry point for the GUI (and any future in-process consumer) — it never shells
/// to the CLI and never lets the GUI depend on Core internals directly.
///
/// Timing confidence is preserved, not hidden: a tempo override without a beat
/// phase is reported as phase-unknown rather than silently aligned to a guessed
/// downbeat.
/// </summary>
public sealed class MidiExportService
{
    /// <summary>Exports MIDI bytes from an already-captured timeline.</summary>
    internal MidiExportResult Export(VisualizationTimeline timeline, MidiExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(request);

        MusicalTimeMapBuildResult build;
        try
        {
            build = MusicalTimeMapBuilder.Build(timeline, ToMapOptions(request));
        }
        catch (MusicalTimingException ex)
        {
            return Failed($"Cannot establish timing: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return Failed($"Invalid timing input: {ex.Message}");
        }

        try
        {
            var exporter = new MusicalMidiExporter(build.Map, request.Ppq, new MusicalMidiExportOptions
            {
                Quantize = request.Quantize,
                EmitPitchBend = request.EmitPitchBend,
                BendRangeSemitones = request.BendRangeSemitones,
                UsePercussionChannel = request.UsePercussionChannel,
                Velocity = request.Velocity,
                EmitMarkers = request.EmitMarkers,
                EmitConductorMetadata = request.EmitConductorMetadata,
                VoiceOverrides = ToVoiceOverrides(request.VoiceOptions),
            })
            {
                Diagnostics = build.Diagnostics,
            };
            byte[] bytes = exporter.Export(timeline).Bytes;
            return new MidiExportResult
            {
                Succeeded = true,
                Bytes = bytes,
                SegmentCount = build.Map.Segments.Count,
                TempoSource = build.Diagnostics.TempoSource.ToString(),
                PhaseSource = build.Diagnostics.PhaseSource.ToString(),
                PhaseUnknown = build.Diagnostics.PhaseUnknown,
                Report = BuildReport(build, request.Ppq),
            };
        }
        catch (Exception ex)
        {
            return Failed($"MIDI export failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Exports MIDI from a persisted timeline JSON file (e.g. the lease's
    /// <c>timeline.json</c>) so a caller that holds a lease path never needs to
    /// load <see cref="VisualizationTimeline"/> itself.
    /// </summary>
    public MidiExportResult ExportFromTimelinePath(string timelinePath, MidiExportRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timelinePath);
        if (!File.Exists(timelinePath))
            return Failed($"Timeline file not found: {timelinePath}");
        try
        {
            VisualizationTimeline timeline = VisualizationJsonWriter.Read(timelinePath);
            return Export(timeline, request);
        }
        catch (Exception ex)
        {
            return Failed($"Could not read timeline '{timelinePath}': {ex.Message}");
        }
    }

    private static MidiExportResult Failed(string error) =>
        new() { Succeeded = false, Error = error };

    /// <summary>Maps the public DTO list to the Core exporter's override type.</summary>
    private static IReadOnlyList<VoiceExportOverride> ToVoiceOverrides(IReadOnlyList<MidiVoiceOption> options)
    {
        if (options is null || options.Count == 0)
            return Array.Empty<VoiceExportOverride>();
        return options.Select(v => new VoiceExportOverride(v.ChannelId)
        {
            Include = v.Include,
            Program = v.Program,
            Channel = v.Channel,
            Velocity = v.Velocity,
            TransposeSemitones = v.TransposeSemitones,
        }).ToArray();
    }

    /// <summary>
    /// Lists the voices present in a timeline so callers can build per-voice
    /// export options before exporting. Returns a graceful error result when the
    /// timeline can't be read.
    /// </summary>
    public MidiVoiceProbe ProbeVoices(string timelinePath)
    {
        try
        {
            VisualizationTimeline timeline = VisualizationJsonWriter.Read(timelinePath);
            return new MidiVoiceProbe
            {
                Succeeded = true,
                Voices = DiscoverVoices(timeline),
            };
        }
        catch (Exception ex)
        {
            return new MidiVoiceProbe
            {
                Succeeded = false,
                Error = $"Could not read timeline '{timelinePath}': {ex.Message}",
            };
        }
    }

    private static IReadOnlyList<MidiVoiceDescriptor> DiscoverVoices(VisualizationTimeline timeline)
    {
        var voices = new Dictionary<string, MidiVoiceDescriptor>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (VoiceDescriptor voice in timeline.Voices ?? Array.Empty<VoiceDescriptor>())
        {
            if (voice is null) continue;
            names[voice.Id.ToString()] = voice.Label ?? voice.Id.ToString();
        }
        foreach (NoteEvent note in timeline.Notes ?? Array.Empty<NoteEvent>())
        {
            if (note is null || string.IsNullOrEmpty(note.ChannelId)) continue;
            voices[note.ChannelId] = new MidiVoiceDescriptor
            {
                ChannelId = note.ChannelId,
                Label = names.TryGetValue(note.ChannelId, out string? n) ? n : note.ChannelId,
            };
        }
        foreach (RhythmEvent rhythm in timeline.Rhythm ?? Array.Empty<RhythmEvent>())
        {
            if (rhythm is null || string.IsNullOrEmpty(rhythm.ChannelId)) continue;
            string name = names.TryGetValue(rhythm.ChannelId, out string? rn) ? rn : rhythm.ChannelId;
            voices[rhythm.ChannelId] = new MidiVoiceDescriptor
            {
                ChannelId = rhythm.ChannelId,
                Label = name,
                IsPercussion = true,
            };
        }
        return voices.Values
            .OrderBy(v => v.ChannelId, StringComparer.Ordinal)
            .ToList();
    }

    private static MusicalTimeMapOptions ToMapOptions(MidiExportRequest request) => new()
    {
        FixedBpm = request.Bpm,
        BeatOffsetSamples = request.BeatOffsetSamples,
        Meter = Meter.TryParse(request.Meter),
        FirstDownbeatSample = request.FirstDownbeatSample,
        Source = ResolveSource(request.TempoSource),
        DetectTempoChanges = true,
        StrictTiming = false, // GUI reports signals rather than throwing.
    };

    private static TimingSource? ResolveSource(MidiTempoSource source) => source switch
    {
        MidiTempoSource.Driver => TimingSource.DriverBeatAnchors,
        MidiTempoSource.Symbolic => TimingSource.SymbolicInference,
        MidiTempoSource.Fixed => TimingSource.UserOverride,
        _ => null,
    };

    private static IReadOnlyList<string> BuildReport(MusicalTimeMapBuildResult build, int ppq)
    {
        var lines = new List<string>();
        var map = build.Map;
        var d = build.Diagnostics;
        lines.Add($"tempo-source: {d.TempoSource}, segments: {map.Segments.Count}, ppq: {ppq}");
        lines.Add($"phase-source: {d.PhaseSource}" +
            (d.PhaseUnknown ? " (phase unknown)" : $""));
        lines.Add($"song: {map.StartSample}–{map.EndSample} samples @ {map.SampleRate} Hz");
        if (map.Meter != null)
            lines.Add($"meter: {map.Meter}");
        lines.AddRange(d.Warnings);
        return lines;
    }
}
