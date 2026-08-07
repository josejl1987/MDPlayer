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
            return options.StrictTiming ? 5 : 4;
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
        VisualizationTimeline timeline = TimelineCaptureService.Capture(
            options.Input, options.Timeline, options, captureDependencies: null);
        output.WriteLine($"timeline: {timeline.Notes.Count} notes, {timeline.Beats.Length} beats, " +
            $"{timeline.Timing.Length} timing events, sample rate {timeline.SampleRate}");

        // Resolve the forced timing source (auto => strongest available).
        TimingSource? forced = options.TempoSource switch
        {
            MidiTempoSource.Driver => TimingSource.DriverBeatAnchors,
            MidiTempoSource.Symbolic => TimingSource.SymbolicInference,
            MidiTempoSource.Fixed => TimingSource.UserOverride,
            _ => null,
        };
        if (forced == TimingSource.UserOverride && options.Bpm is null)
            throw new ArgumentException("--tempo-source fixed requires --bpm");

        var mapOptions = new MusicalTimeMapOptions
        {
            FixedBpm = options.Bpm,
            BeatOffsetSamples = options.BeatOffsetSamples,
            Meter = options.Meter,
            FirstDownbeatSample = options.FirstDownbeatSample,
            Source = forced,
            StrictTiming = options.StrictTiming,
            DetectTempoChanges = true,
        };

        MusicalTimeMapBuildResult build = MusicalTimeMapBuilder.Build(timeline, mapOptions);
        TimingDiagnostics diagnostics = build.Diagnostics;

        var exportOptions = new MusicalMidiExportOptions
        {
            Quantize = options.Quantize,
            EmitPitchBend = options.EmitPitchBend,
            BendRangeSemitones = options.BendRange,
            UsePercussionChannel = options.UsePercussionChannel,
        };
        var exporter = new MusicalMidiExporter(build.Map, options.Ppq, exportOptions)
        {
            Diagnostics = diagnostics,
        };
        MusicalMidiExportResult result = exporter.Export(timeline);

        Directory.CreateDirectory(
            Path.GetDirectoryName(Path.GetFullPath(options.Output)) ?? ".");
        File.WriteAllBytes(options.Output, result.Bytes);

        output.WriteLine($"wrote {System.IO.Path.GetFullPath(options.Output)} ({result.Bytes.Length} bytes)");
        output.WriteLine($"tempo: {diagnostics.TempoSource}, {build.Map.Segments.Count} segment(s); " +
            $"phase: {diagnostics.PhaseSource}, {build.Map.SampleToQuarterPosition(build.Map.StartSample):0.###} quarter(s) at sample 0");
        foreach (string warning in diagnostics.Warnings)
            output.WriteLine($"warning: {warning}");

        if (!string.IsNullOrWhiteSpace(options.TimingReport))
            WriteTimingReport(options.TimingReport, timeline, build);

        return 0;
    }

    private static void WriteTimingReport(string path, VisualizationTimeline timeline, MusicalTimeMapBuildResult build)
    {
        var report = new
        {
            sampleRate = build.Map.SampleRate,
            ppq = 0,
            segments = build.Map.Segments.Select(s => new
            {
                s.StartSample,
                s.EndSample,
                s.QuarterPositionAtStart,
                s.SamplesPerQuarter,
                bpm = s.BeatsPerMinute,
                source = s.Source.ToString(),
                s.Confidence,
            }).ToArray(),
            meter = build.Map.Meter?.ToString(),
            firstDownbeatQuarter = build.Map.FirstDownbeatQuarter,
            startSample = build.Map.StartSample,
            endSample = build.Map.EndSample,
            diagnostics = new
            {
                build.Diagnostics.AnchorCount,
                build.Diagnostics.RejectedAnchorCount,
                build.Diagnostics.SegmentCount,
                build.Diagnostics.MaxResidualQuarters,
                build.Diagnostics.RmsResidualQuarters,
                tempoSource = build.Diagnostics.TempoSource.ToString(),
                phaseSource = build.Diagnostics.PhaseSource.ToString(),
                build.Diagnostics.PhaseUnknown,
                sampleZeroQuarter = build.Diagnostics.SampleZeroQuarter,
                build.Diagnostics.IsTrustworthy,
                warnings = build.Diagnostics.Warnings,
                timelineAnnotations = new
                {
                    notes = timeline.Notes.Count,
                    beats = timeline.Beats.Length,
                    timing = timeline.Timing.Length,
                    rhythm = timeline.Rhythm.Count,
                },
            },
        };
        string json = System.Text.Json.JsonSerializer.Serialize(
            report,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        File.WriteAllText(path, json);
    }
}
