using System.Diagnostics;
using Fmp.Core.Midi;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;

namespace Fmp.Cli;

internal static class MidiCommand
{
    public static int Handle(string[] args)
    {
        MidiOptions options;
        try
        {
            options = MidiOptionsParser.Parse(args);
            options.ValidateCommon();
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }

        try
        {
            return Run(options);
        }
        catch (TrackPreparationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ex.ExitCode;
        }
        catch (MusicalTimingException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 4;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: MIDI export failed — {ex.Message}");
            return 7;
        }
    }

    private static int Run(MidiOptions options)
    {
        TextWriter output = options.OutputWriter ?? Console.Out;

        // Capture (or reuse) the timeline.
        Stopwatch captureWatch = Stopwatch.StartNew();
        VisualizationTimeline timeline = TimelineCaptureService.Capture(
            options.Input, options.Timeline, options, captureDependencies: null);
        captureWatch.Stop();
        output.WriteLine($"timeline: {timeline.Notes.Count} notes, {timeline.Beats.Length} beats, " +
            $"{timeline.Timing.Length} timing events, sample rate {timeline.SampleRate}");
        if (!string.IsNullOrWhiteSpace(options.TimelineOut))
        {
            string timelineOut = Path.GetFullPath(options.TimelineOut);
            Directory.CreateDirectory(Path.GetDirectoryName(timelineOut) ?? ".");
            VisualizationJsonWriter.Write(timelineOut, timeline);
            output.WriteLine($"timeline written: {timelineOut}");
        }

        MusicalTimeMapBuildResult? timing = options.MusicalGrid
            ? MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions
            {
                FixedBpm = options.FixedBpm,
                Meter = options.Meter,
                BeatOffsetSamples = options.BeatOffsetSamples,
                StrictTiming = options.StrictTiming,
                EnableStructuralGridSelection = false,
                EnableLegacyHierarchyInference = false,
            })
            : null;
        if (options.Channels && timing is not null)
            throw new InvalidOperationException(
                "--channels cannot be combined with --musical-grid or musical timing overrides.");
        MidiTranscriptionResult result = timing is null
            ? new MidiTranscriber(options.Ppq).Transcribe(timeline)
            : new MidiTranscriber(options.Ppq, timing.Map).Transcribe(timeline);

        if (options.Channels)
            return WriteChannels(options, timeline, result);

        Directory.CreateDirectory(
            Path.GetDirectoryName(Path.GetFullPath(options.Output)) ?? ".");
        Stopwatch fileWatch = Stopwatch.StartNew();
        File.WriteAllBytes(options.Output, result.Bytes);
        fileWatch.Stop();

        output.WriteLine($"wrote {System.IO.Path.GetFullPath(options.Output)} ({result.Bytes.Length} bytes)");
        output.WriteLine(timing is null
            ? $"midi mode: raw-fidelity; transport: 120 BPM; ppq: {options.Ppq}"
            : $"midi mode: musical-time-map; tempo: {timing.Map.Segments[0].BeatsPerMinute:0.###} BPM; "
                + $"tempo-resolved: {timing.Diagnostics.GridSelection?.TempoResolved ?? !timing.Diagnostics.TempoAmbiguous}; "
                + $"meter: {timing.Map.Meter?.ToString() ?? "unresolved"}; "
                + $"meter-resolved: {timing.Diagnostics.GridSelection?.MeterResolved ?? timing.Map.Meter is not null}; "
                + $"downbeat-resolved: {timing.Map.FirstDownbeatQuarter is not null}; ppq: {options.Ppq}");
        if (timing is not null)
            output.WriteLine($"source-quarter-at-start: {timing.Map.Segments[0].QuarterPositionAtStart:R}");
        output.WriteLine($"source: {timeline.StartSample}-{timeline.EndSample} samples @ {timeline.SampleRate} Hz");
        output.WriteLine($"events: notes={result.Diagnostics.SourceNoteCount}; " +
            $"native-rhythm={result.Diagnostics.NativeRhythmHitCount}; " +
            $"same-tick-attacks={result.Diagnostics.SameTickAttackCollisions}; " +
            $"one-tick-notes={result.Diagnostics.OneTickNotes}");

        if (!string.IsNullOrWhiteSpace(options.TimingReport))
            WriteRawTimingReport(options.TimingReport, timeline, result, options.Ppq,
                captureWatch.Elapsed.TotalSeconds, fileWatch.Elapsed.TotalSeconds);

        if (!string.IsNullOrWhiteSpace(options.PitchReport))
            WriteRawPitchReport(options.PitchReport, result);

        return 0;
    }

    private static int WriteChannels(
        MidiOptions options,
        VisualizationTimeline timeline,
        MidiTranscriptionResult ignored)
    {
        TextWriter output = options.OutputWriter ?? Console.Out;
        MidiTrailChannelsResult result = MidiTrailChannelExporter.Export(timeline, options.Ppq);

        string outputDir = Path.GetFullPath(options.Output);
        Directory.CreateDirectory(outputDir);

        foreach (MidiTrailChannelExport channel in result.Channels)
        {
            string safe = SanitizeFileSegment(channel.SourceVoiceId);
            string path = Path.Combine(outputDir, safe + ".mid");
            File.WriteAllBytes(path, channel.Bytes);
            output.WriteLine($"channel {channel.SourceVoiceId,-24} -> {path} ({channel.Bytes.Length} bytes; end tick {channel.EndTick})");
        }

        output.WriteLine($"wrote {result.Channels.Count} per-channel .mid files to {outputDir}");
        output.WriteLine($"transport parity: fixed 120 BPM; ppq: {options.Ppq}; common end tick: {result.TotalEndTick}");
        output.WriteLine($"source: {timeline.StartSample}-{timeline.EndSample} samples @ {timeline.SampleRate} Hz");
        output.WriteLine($"events: notes={result.Channels.Count}; sampling from single transcript");
        return 0;
    }

    private static string SanitizeFileSegment(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '-');
        // Keep domain:/channel: prefixes readable (e.g. domain-FM1).
        return value.Replace(':', '-').Trim('-');
    }

    private static void WriteRawPitchReport(string path, MidiTranscriptionResult export)
    {
        var report = new
        {
            mode = "raw-fidelity",
            tuningNormalization = false,
            tracks = export.Tracks.Select(track => new
            {
                track.Name,
                port = track.Endpoint.Port,
                channel = track.Endpoint.Channel + 1,
                bendRangeSemitones = track.Events.OfType<MidiBendRangeEvent>()
                    .Select(e => (int?)e.Semitones).SingleOrDefault(),
                pitchBends = track.Events.Count(e => e is MidiPitchBendEvent),
                noteOns = track.Events.Count(e => e is MidiNoteEvent n && n.NoteOn),
            }).ToArray(),
        };
        string json = System.Text.Json.JsonSerializer.Serialize(
            report,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        File.WriteAllText(path, json);
    }

    private static void WriteRawTimingReport(
        string path,
        VisualizationTimeline timeline,
        MidiTranscriptionResult export,
        int ppq,
        double captureSeconds,
        double fileWriteSeconds)
    {
        var report = new
        {
            mode = "raw-fidelity",
            sampleRate = timeline.SampleRate,
            ppq,
            transportBpm = 120,
            transportMicrosecondsPerQuarter = MidiTranscriber.TransportMicrosecondsPerQuarter,
            startSample = timeline.StartSample,
            endSample = timeline.EndSample,
            sourceNotes = export.Diagnostics.SourceNoteCount,
            nativeRhythmHits = export.Diagnostics.NativeRhythmHitCount,
            sameTickAttackCollisions = export.Diagnostics.SameTickAttackCollisions,
            oneTickNotes = export.Diagnostics.OneTickNotes,
            musicalGridInferred = false,
            captureSeconds,
            fileWriteSeconds,
        };
        string json = System.Text.Json.JsonSerializer.Serialize(
            report,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        File.WriteAllText(path, json);
    }
}
