using Fmp.Core.Midi;
using Fmp.Core.Visualization;

namespace Fmp.Application.Export;

/// <summary>
/// Shared in-process entry point for raw source-faithful MIDI transcription.
/// It preserves source-relative timing and pitch through <see cref="MidiTranscriber"/>
/// and deliberately performs no BPM, meter, downbeat or structure inference.
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
                    new { Request = request, Mode = "raw-fidelity" });
                bytes = recorder.Measure("raw-midi-transcription",
                    (timeline.Notes ?? Array.Empty<NoteEvent>()).Count,
                    () =>
                    {
                        coreResult = new MidiTranscriber(request.Ppq)
                            .Transcribe(timeline);
                        return coreResult.Bytes;
                    });
                performance = recorder.Complete();
            }
            else
            {
                coreResult = new MidiTranscriber(request.Ppq)
                    .Transcribe(timeline);
                bytes = coreResult.Bytes;
            }

            return new MidiExportResult
            {
                Succeeded = true,
                Bytes = bytes,
                Report = BuildRawReport(timeline, request.Ppq, coreResult.Diagnostics, performance),
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

    private static IReadOnlyList<string> BuildRawReport(
        VisualizationTimeline timeline,
        int ppq,
        MidiTranscriptionDiagnostics diagnostics,
        ExportPerformanceSummary? performance)
    {
        var lines = new List<string>
        {
            $"midi-mode: raw-fidelity; transport: 120 BPM; ppq: {ppq}",
            $"song: {timeline.StartSample}-{timeline.EndSample} samples @ {timeline.SampleRate} Hz",
            "musical-grid: not inferred; phase: not applicable",
            $"source-notes: {diagnostics.SourceNoteCount}; native-rhythm: {diagnostics.NativeRhythmHitCount}; "
                + $"same-tick-attacks: {diagnostics.SameTickAttackCollisions}; one-tick-notes: {diagnostics.OneTickNotes}",
        };
        if (performance is not null)
            lines.AddRange(performance.ToHumanReadable());
        return lines;
    }

    private static void ValidateRequest(MidiExportRequest request)
    {
        if (request.Ppq <= 0 || request.Ppq > 32767)
            throw new ArgumentException($"PPQ must be positive and at most 32767 (MIDI-valid division), got {request.Ppq}");
    }
}