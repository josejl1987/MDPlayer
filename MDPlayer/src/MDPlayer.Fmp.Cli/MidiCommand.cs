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
        // Report the resolved timing source explicitly - for auto this is the source
        // that was actually selected, so the user can see the choice (section 48).
        string mode = options.TempoSource == MidiTempoSource.Auto ? "auto-selected" : "forced";
        output.WriteLine($"timing source: {diagnostics.TempoSource} ({mode}); " +
            $"{build.Map.Segments.Count} segment(s)");
        output.WriteLine($"tempo: {diagnostics.TempoSource}, phase: {diagnostics.PhaseSource}, " +
            $"{build.Map.SampleToQuarterPosition(build.Map.StartSample):0.###} quarter(s) at sample 0 " +
            $"[{(diagnostics.PhaseUnknown ? "not beat-aligned; phase unknown" : diagnostics.PhaseAuthoritative ? "beat-aligned" : "not beat-aligned; phase inferred")}]");
        foreach (string warning in diagnostics.Warnings)
            output.WriteLine($"warning: {warning}");

        if (!string.IsNullOrWhiteSpace(options.TimingReport))
            WriteTimingReport(options.TimingReport, timeline, build, result, options.Ppq);

        return 0;
    }

    private static void WriteTimingReport(
        string path,
        VisualizationTimeline timeline,
        MusicalTimeMapBuildResult build,
        MusicalMidiExportResult export,
        int ppq)
    {
        TimingDiagnostics diagnostics = build.Diagnostics;
        int inputAnchors = Math.Max(diagnostics.AnchorCount, diagnostics.RawAnchorCount);
        int accepted = Math.Max(0, inputAnchors - diagnostics.RejectedAnchorCount);
        var report = new
        {
            sampleRate = build.Map.SampleRate,
            ppq = ppq, // the CONFIGURED PPQ, never a placeholder (section 50)
            source = diagnostics.TempoSource.ToString(),
            phaseAuthoritative = diagnostics.PhaseAuthoritative,
            tempoAuthoritative = diagnostics.TempoAuthoritative,
            originTickOffset = (long)Math.Round(export.OriginOffsetQuarters * ppq),
            meter = build.Map.Meter is { } m ? new { numerator = m.Numerator, denominator = m.Denominator } : (object?)null,
            downbeatKnown = build.Map.FirstDownbeatQuarter is not null,
            anchors = new
            {
                input = inputAnchors,
                accepted = accepted,
                rejected = diagnostics.RejectedAnchorCount,
                rmsResidualSamples = diagnostics.RmsResidualSamples,
                maxResidualSamples = diagnostics.MaxResidualSamples,
                rejectedDetails = diagnostics.RejectedAnchors.Select(r => new
                {
                    sample = r.Sample,
                    beatPosition = r.QuarterPosition,
                    residual = r.ResidualQuarters,
                    reason = r.Reason,
                }).ToArray(),
            },
            segments = build.Map.Segments.Select(s => new
            {
                startSample = s.StartSample,
                endSample = s.EndSample,
                quarterAtStart = s.QuarterPositionAtStart,
                bpm = s.BeatsPerMinute,
                source = s.Source.ToString(),
                confidence = s.Confidence,
            }).ToArray(),
            warnings = diagnostics.Warnings,
        };
        string json = System.Text.Json.JsonSerializer.Serialize(
            report,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        File.WriteAllText(path, json);
    }
}
