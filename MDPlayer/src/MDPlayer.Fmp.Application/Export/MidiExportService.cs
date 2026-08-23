using Fmp.Core.Midi;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;

namespace Fmp.Application.Export;

/// <summary>
/// Shared in-process entry point for source-faithful MIDI transcription. Raw mode
/// remains available for exact source transport; musical mode supplies the same
/// compiler with a <see cref="MusicalTimeMap"/> and serialized tempo/meter state.
/// </summary>
public sealed class MidiExportService
{
    /// <summary>Exports MIDI bytes from an already-captured timeline.</summary>
    internal MidiExportResult Export(VisualizationTimeline timeline, MidiExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            ValidateRequest(request);
        }
        catch (ArgumentException ex)
        {
            return Failed($"Invalid MIDI export input: {ex.Message}");
        }

        try
        {
            byte[] bytes;
            MidiTranscriptionResult? coreResult = null;
            MusicalTimeMapBuildResult? timing = request.TimingMode == MidiExportTimingMode.MusicalTimeMap
                ? MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions
                {
                    FixedBpm = request.FixedBpm,
                    Meter = ParseMeter(request.Meter),
                    BeatOffsetSamples = request.BeatOffsetSamples,
                    StrictTiming = request.StrictTiming,
                    EnableStructuralGridSelection = false,
                    EnableLegacyHierarchyInference = false,
                })
                : null;
            MidiTranscriptionResult Transcribe() => timing is null
                ? new MidiTranscriber(request.Ppq).Transcribe(timeline)
                : new MidiTranscriber(request.Ppq, timing.Map).Transcribe(timeline);
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
                    new { Request = request, Mode = request.TimingMode.ToString() });
                string phase = request.TimingMode == MidiExportTimingMode.RawSourceTime
                    ? "raw-midi-transcription"
                    : "musical-midi-transcription";
                bytes = recorder.Measure(phase,
                    (timeline.Notes ?? Array.Empty<NoteEvent>()).Count,
                    () =>
                    {
                        coreResult = Transcribe();
                        return coreResult.Bytes;
                    });
                performance = recorder.Complete();
            }
            else
            {
                coreResult = Transcribe();
                bytes = coreResult.Bytes;
            }

            return new MidiExportResult
            {
                Succeeded = true,
                Bytes = bytes,
                Report = BuildReport(timeline, request, coreResult.Diagnostics, timing, performance),
                Performance = performance,
                Tracks = coreResult.Tracks,
            };
        }
        catch (Exception ex)
        {
            return Failed($"MIDI export failed: {ex.Message}");
        }
    }

    /// <summary>Exports a persisted source timeline without rewriting its events.</summary>
    public MidiExportResult ExportFromTimelinePath(string timelinePath, MidiExportRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timelinePath);
        if (!File.Exists(timelinePath))
            return Failed($"Timeline file not found: {timelinePath}");
        try
        {
            return Export(VisualizationJsonWriter.Read(timelinePath), request);
        }
        catch (Exception ex)
        {
            return Failed($"Could not read timeline '{timelinePath}': {ex.Message}");
        }
    }

    private static MidiExportResult Failed(string error) =>
        new() { Succeeded = false, Error = error };

    private static IReadOnlyList<string> BuildReport(
        VisualizationTimeline timeline,
        MidiExportRequest request,
        MidiTranscriptionDiagnostics diagnostics,
        MusicalTimeMapBuildResult? timing,
        ExportPerformanceSummary? performance)
    {
        int ppq = request.Ppq;
        var lines = new List<string>
        {
            timing is null
                ? $"midi-mode: raw-fidelity; transport: 120 BPM; ppq: {ppq}"
                : $"midi-mode: musical-time-map; ppq: {ppq}",
            $"song: {timeline.StartSample}-{timeline.EndSample} samples @ {timeline.SampleRate} Hz",
            $"source-notes: {diagnostics.SourceNoteCount}; native-rhythm: {diagnostics.NativeRhythmHitCount}; "
                + $"sample-playback: {diagnostics.SamplePlaybackCount}; "
                + $"same-tick-attacks: {diagnostics.SameTickAttackCollisions}; one-tick-notes: {diagnostics.OneTickNotes}",
        };
        if (timing is null)
            lines.Add("musical-grid: not inferred; phase: not applicable");
        else
        {
            TimingDiagnostics d = timing.Diagnostics;
            lines.Add($"tempo: {timing.Map.Segments[0].BeatsPerMinute:0.###} BPM; "
                + $"resolved: {d.GridSelection?.TempoResolved ?? !d.TempoAmbiguous}");
            lines.Add($"meter: {timing.Map.Meter?.ToString() ?? "unresolved"}; "
                + $"resolved: {d.GridSelection?.MeterResolved ?? timing.Map.Meter is not null}");
            lines.Add($"downbeat: {(timing.Map.FirstDownbeatQuarter is null ? "unresolved" : "resolved")}; "
                + "source-events: unchanged");
        }
        if (performance is not null)
            lines.AddRange(performance.ToHumanReadable());
        lines.AddRange(diagnostics.BendRangeDiagnostics
            .Select(warning => "pitch-diagnostic: " + warning));
        return lines;
    }

    private static void ValidateRequest(MidiExportRequest request)
    {
        if (request.Ppq <= 0 || request.Ppq > 32767)
            throw new ArgumentException($"PPQ must be positive and at most 32767 (MIDI-valid division), got {request.Ppq}");
        if (!Enum.IsDefined(request.TimingMode))
            throw new ArgumentException($"Unknown MIDI timing mode: {request.TimingMode}");
        if (request.FixedBpm is double bpm && (!double.IsFinite(bpm) || bpm <= 0))
            throw new ArgumentException("Fixed BPM must be finite and positive.");
        _ = ParseMeter(request.Meter);
        if (request.TimingMode == MidiExportTimingMode.RawSourceTime
            && (request.FixedBpm is not null || request.Meter is not null
                || request.BeatOffsetSamples is not null || request.StrictTiming))
            throw new ArgumentException("Musical timing overrides require MusicalTimeMap timing mode.");
    }

    private static Meter? ParseMeter(string? value)
    {
        if (value is null)
            return null;
        Meter? meter = Meter.TryParse(value);
        if (meter is null || (meter.Denominator & (meter.Denominator - 1)) != 0)
            throw new ArgumentException($"Invalid meter '{value}'. Use numerator/denominator with a power-of-two denominator.");
        return meter;
    }
}
