using System.Diagnostics;
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
        timeline = VoiceStateNormalizationStage.Normalize(timeline);

        // GUI parity: the Application path enforces the same option semantics the
        // CLI does, so the two entry points cannot drift (§11, plan IC-07).
        try
        {
            ValidateRequest(request);
        }
        catch (ArgumentException ex)
        {
            return Failed($"Invalid timing input: {ex.Message}");
        }

        MusicalTimeMapBuildResult build;
        TempoInferenceCounters tempoCounters = default;
        Stopwatch? tempoWatch = request.EnablePerformanceMetrics ? Stopwatch.StartNew() : null;
        try
        {
            build = request.EnablePerformanceMetrics
                ? MusicalTimeMapBuilder.Build(timeline, ToMapOptions(request), out tempoCounters)
                : MusicalTimeMapBuilder.Build(timeline, ToMapOptions(request));
            tempoWatch?.Stop();
        }
        catch (MusicalTimingException ex)
        {
            return Failed($"Cannot establish timing: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return Failed($"Invalid timing input: {ex.Message}");
        }
        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(build.Map, timeline);

        try
        {
            var exporter = new MusicalMidiExporter(build.Map, request.Ppq, new MusicalMidiExportOptions
            {
                Quantize = request.Quantize,
                EmitPitchBend = request.EmitPitchBend,
                BendRangeSemitones = request.BendRangeSemitones,
                UsePercussionChannel = request.UsePercussionChannel,
                Velocity = request.Velocity,
                TrackLayout = request.TrackLayout,
                EmitMarkers = request.EmitMarkers,
                EmitConductorMetadata = request.EmitConductorMetadata,
                VoiceOverrides = ToVoiceOverrides(request.VoiceOptions),
                EnablePerformanceMetrics = request.EnablePerformanceMetrics,
                TempoInferenceCounters = tempoCounters,
            })
            {
                Diagnostics = build.Diagnostics,
                Structure = structure,
            };
            byte[] bytes;
            MusicalMidiExportResult? coreResult = null;
            ExportPerformanceSummary? performance = null;
            if (request.EnablePerformanceReceipts)
            {
                var recorder = new ExportPerformanceRecorder(
                    request.PerformanceFixture,
                    new
                    {
                        Source = timeline.Source,
                        SampleRate = timeline.SampleRate,
                        StartSample = timeline.StartSample,
                        EndSample = timeline.EndSample,
                        NoteCount = (timeline.Notes ?? Array.Empty<NoteEvent>()).Count,
                    },
                    new { Request = request });
                bytes = recorder.Measure("midi-planning-and-serialization",
                    (timeline.Notes ?? Array.Empty<NoteEvent>()).Count,
                    () =>
                    {
                        coreResult = exporter.Export(timeline);
                        return coreResult.Bytes;
                    });
                performance = recorder.Complete();
            }
            else
            {
                coreResult = exporter.Export(timeline);
                bytes = coreResult.Bytes;
            }
            return new MidiExportResult
            {
                Succeeded = true,
                Bytes = bytes,
                SegmentCount = build.Map.Segments.Count,
                TempoSource = build.Diagnostics.TempoSource.ToString(),
                PhaseSource = build.Diagnostics.PhaseSource.ToString(),
                PhaseUnknown = build.Diagnostics.PhaseUnknown,
                Report = BuildReport(build, request.Ppq, performance),
                Performance = performance,
                PerformanceMetrics = coreResult?.Performance is { } corePerformance
                    ? corePerformance.WithOuterStageTimings(
                        tempoGridSeconds: tempoWatch?.Elapsed.TotalSeconds ?? 0)
                    : null,
                Tracks = coreResult?.Tracks,
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
            VisualizationTimeline timeline = NormalizeSerializedTimeline(timelinePath);
            return Export(timeline, request);
        }
        catch (MusicalTimingException ex)
        {
            return Failed($"Cannot establish timing in '{timelinePath}': {ex.Message}");
        }
        catch (Exception ex)
        {
            return Failed($"Could not read timeline '{timelinePath}': {ex.Message}");
        }
    }

    /// <summary>
    /// Routes a serialized timeline through the SAME producer-clock normalization
    /// boundary the CLI serialized path uses (<see cref="Fmp.Core.Visualization.TimelineBuilder.Merge"/>
    /// → <see cref="Fmp.Core.Visualization.ProducerClockNormalization"/>), so a
    /// serialized consumer is never outside the single-boundary invariant (spec
    /// §4.1). The Application/GUI path has no separate playback clock, so the
    /// producer's own declared rate is the destination: normalization is a
    /// pass-through when that rate is present and explicit, and an ambiguous or
    /// missing source rate is rejected with an actionable
    /// <see cref="MusicalTimingException"/> — not a generic JSON/argument error.
    /// The serialized [StartSample, EndSample] range is preserved on the rebuilt
    /// timeline (the map origin is kept), while every event sample still passes
    /// through the boundary.
    /// </summary>
    private static VisualizationTimeline NormalizeSerializedTimeline(string timelinePath)
    {
        VisualizationTimeline loaded = VisualizationJsonWriter.Read(timelinePath);

        // Destination clock = the timeline's own declared rate. Validate it through
        // the boundary up front so an ambiguous/missing source rate raises an
        // actionable MusicalTimingException before TimelineBuilder is constructed
        // (its ctor would otherwise reject a nonpositive rate with a generic
        // ArgumentOutOfRangeException).
        int destinationRate = loaded.SampleRate;
        ProducerClockNormalization.ConvertSamplePosition(
            "serialized-timeline",
            sourceSample: 0,
            loaded.SampleRate,
            destinationRate);

        var builder = new TimelineBuilder(destinationRate);
        builder.Merge(loaded); // applies the boundary to every timed event family.

        long startSample = ProducerClockNormalization.ConvertSamplePosition(
            "serialized-timeline",
            loaded.StartSample,
            loaded.SampleRate,
            destinationRate);
        long endSample = ProducerClockNormalization.ConvertSamplePosition(
            "serialized-timeline",
            loaded.EndSample,
            loaded.SampleRate,
            destinationRate);
        return builder.Build(endSample, loaded.StopReason, loaded.Source, startSample);
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

    private static void ValidateRequest(MidiExportRequest request)
    {
        if (request.Ppq <= 0 || request.Ppq > 32767)
            throw new ArgumentException($"PPQ must be positive and at most 32767 (MIDI-valid division), got {request.Ppq}");
        if (request.Bpm is double bpm)
        {
            if (!double.IsFinite(bpm))
                throw new ArgumentException("BPM must be a finite number");
            if (bpm <= 0)
                throw new ArgumentException("BPM must be positive");
        }
        if (request.TempoSource == MidiTempoSource.Fixed && request.Bpm is null)
            throw new ArgumentException("tempo-source Fixed requires Bpm");
        if (request.FirstDownbeatSample is not null && string.IsNullOrWhiteSpace(request.Meter))
            throw new ArgumentException("FirstDownbeatSample requires a meter (e.g. \"4/4\")");
    }

    private static MusicalTimeMapOptions ToMapOptions(MidiExportRequest request) => new()
    {
        FixedBpm = request.Bpm,
        BeatOffsetSamples = request.BeatOffsetSamples,
        Meter = Meter.TryParse(request.Meter),
        FirstDownbeatSample = request.FirstDownbeatSample,
        Source = ResolveSource(request.TempoSource),
        DetectTempoChanges = true,
        StrictTiming = request.StrictTiming,
    };

    private static TimingSource? ResolveSource(MidiTempoSource source) => source switch
    {
        MidiTempoSource.Driver => TimingSource.DriverBeatAnchors,
        MidiTempoSource.Symbolic => TimingSource.SymbolicInference,
        MidiTempoSource.Fixed => TimingSource.UserOverride,
        _ => null,
    };

    private static IReadOnlyList<string> BuildReport(MusicalTimeMapBuildResult build, int ppq,
        ExportPerformanceSummary? performance = null)
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
        if (performance is not null)
            lines.AddRange(performance.ToHumanReadable());
        return lines;
    }
}
