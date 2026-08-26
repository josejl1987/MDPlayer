using Fmp.Core.Midi;
using Fmp.Core.Visualization;

namespace Fmp.Application.Export;

/// <summary>
/// Shared in-process entry point for source-faithful raw MIDI transcription.
/// The transport is fixed (120 BPM, PPQ 960); no musical timing inference is
/// applied.
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
            byte[] bytes;
            MidiTranscriptionResult? coreResult = null;
            MidiTranscriptionResult Transcribe() => new MidiTranscriber().Transcribe(timeline);
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
                    new { Request = request, Mode = "raw-source-time" });
                string phase = "raw-midi-transcription";
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
                Report = BuildReport(timeline, coreResult.Diagnostics, performance),
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
        MidiTranscriptionDiagnostics diagnostics,
        ExportPerformanceSummary? performance)
    {
        var lines = new List<string>
        {
            $"midi-mode: raw-fidelity; transport: 120 BPM; ppq: {MidiTranscriber.DefaultPpq}",
            $"song: {timeline.StartSample}-{timeline.EndSample} samples @ {timeline.SampleRate} Hz",
            $"source-notes: {diagnostics.SourceNoteCount}; native-rhythm: {diagnostics.NativeRhythmHitCount}; "
                + $"sample-playback: {diagnostics.SamplePlaybackCount}; "
                + $"same-tick-attacks: {diagnostics.SameTickAttackCollisions}; one-tick-notes: {diagnostics.OneTickNotes}",
            "musical-grid: not inferred; phase: not applicable",
        };
        if (performance is not null)
            lines.AddRange(performance.ToHumanReadable());
        lines.AddRange(diagnostics.BendRangeDiagnostics
            .Select(warning => "pitch-diagnostic: " + warning));
        return lines;
    }
}