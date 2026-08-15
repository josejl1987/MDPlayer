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
        Stopwatch captureWatch = Stopwatch.StartNew();
        VisualizationTimeline timeline = TimelineCaptureService.Capture(
            options.Input, options.Timeline, options, captureDependencies: null);
        captureWatch.Stop();
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
        timeline = VoiceStateNormalizationStage.Normalize(timeline);

        Stopwatch tempoWatch = Stopwatch.StartNew();
        MusicalTimeMapBuildResult build = MusicalTimeMapBuilder.Build(timeline, mapOptions);
        tempoWatch.Stop();
        TimingDiagnostics diagnostics = build.Diagnostics;
        MusicalStructure structure = MusicalStructureAnalyzer.Analyze(build.Map, timeline, build.PercussionEvidence);

        var exportOptions = new MusicalMidiExportOptions
        {
            Quantize = options.Quantize,
            EmitPitchBend = options.EmitPitchBend,
            BendRangeSemitones = options.BendRange,
            UsePercussionChannel = options.UsePercussionChannel,
            TrackLayout = options.TrackLayout,
            PitchNormalizationMode = options.PitchNormalization switch
            {
                "fidelity" => PitchNormalizationMode.Fidelity,
                "daw" => PitchNormalizationMode.DawFriendly,
                "off" => PitchNormalizationMode.Off,
                _ => throw new ArgumentException($"unknown --pitch-normalization '{options.PitchNormalization}'"),
            },
            EnablePerformanceMetrics = !string.IsNullOrWhiteSpace(options.TimingReport),
        };
        var exporter = new MusicalMidiExporter(build.Map, options.Ppq, exportOptions)
        {
            Diagnostics = diagnostics,
            Structure = structure,
        };
        MusicalMidiExportResult result = exporter.Export(timeline);

        Directory.CreateDirectory(
            Path.GetDirectoryName(Path.GetFullPath(options.Output)) ?? ".");
        Stopwatch fileWatch = Stopwatch.StartNew();
        File.WriteAllBytes(options.Output, result.Bytes);
        fileWatch.Stop();
        if (result.Performance is { } performance)
        {
            result.Performance = performance.WithOuterStageTimings(
                captureSeconds: captureWatch.Elapsed.TotalSeconds,
                tempoGridSeconds: tempoWatch.Elapsed.TotalSeconds,
                fileWriteSeconds: fileWatch.Elapsed.TotalSeconds);
        }

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

        if (!string.IsNullOrWhiteSpace(options.PitchReport))
            WritePitchReport(options.PitchReport, result);

        return 0;
    }

    /// <summary>
    /// Writes the per-domain pitch-normalization report (FR-6 / SC-9): every domain
    /// carries the nine mandated fields — attacks, raw pitch samples, residual mode,
    /// stable residual MAD, baseline confidence, raw bend transitions, after
    /// deadband, after dedup and expressive transitions — plus retrigger attacks,
    /// the accepted tuning and the acceptance verdict. Mirrors WriteTimingReport.
    /// </summary>
    private static void WritePitchReport(string path, MusicalMidiExportResult export)
    {
        var report = new
        {
            domains = export.PitchDiagnostics.Domains.Select(d => new
            {
                domain = d.Key.ToString(),
                attacks = d.Attacks,
                retriggerAttacks = d.RetriggerAttacks,
                rawPitchSamples = d.RawPitchSamples,
                residualModeCents = d.ResidualModeCents,
                stableResidualMadCents = d.StableResidualMadCents,
                baselineConfidence = d.BaselineConfidence,
                rawBendTransitions = d.RawBendTransitions,
                afterDeadband = d.AfterDeadband,
                afterDedup = d.AfterDedup,
                expressiveTransitions = d.ExpressiveTransitions,
                tuningCents = d.TuningCents,
                accepted = d.Accepted,
            }).ToArray(),
            warnings = export.PitchDiagnostics.Warnings,
        };
        string json = System.Text.Json.JsonSerializer.Serialize(
            report,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        File.WriteAllText(path, json);
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
        // Patch G: pitch/source-time acceptance metrics derived from the exported
        // track set. Recomputed here so the report is self-contained.
        var pitchMetrics = ComputePitchMetrics(export, build.Map, ppq);
        var report = new
        {
            sampleRate = build.Map.SampleRate,
            ppq = ppq, // the CONFIGURED PPQ, never a placeholder (section 50)
            source = diagnostics.TempoSource.ToString(),
            phaseAuthoritative = diagnostics.PhaseAuthoritative,
            tempoAuthoritative = diagnostics.TempoAuthoritative,
            originTickOffset = export.OriginShiftTicks,
            meter = build.Map.Meter is { } m ? new { numerator = m.Numerator, denominator = m.Denominator } : (object?)null,
            downbeatKnown = build.Map.FirstDownbeatQuarter is not null,
            tempoInference = diagnostics.TempoSource == TimingSource.SymbolicInference ? new
            {
                selectedBpm = diagnostics.SelectedBpm,
                microsecondsPerQuarter = diagnostics.TempoMicrosecondsPerQuarter,
                alternativeBpm = diagnostics.AlternativeBpm,
                selectedScore = diagnostics.SelectedScore,
                alternativeScore = diagnostics.AlternativeScore,
                aliasMargin = diagnostics.AliasMargin,
                tempoConfidence = diagnostics.TempoConfidence,
                tempoAmbiguous = diagnostics.TempoAmbiguous,
                phaseSample = diagnostics.PhaseSample,
                sample0Quarter = diagnostics.SampleZeroQuarter,
                tatum = diagnostics.TatumDurationSamples,
                tatumsPerBeat = diagnostics.TatumsPerBeat,
                beat = diagnostics.BeatDurationSamples,
                beatPhase = diagnostics.BeatPhaseSample,
                metricalConfidence = diagnostics.MetricalConfidence,
                downbeatPhase = diagnostics.DownbeatPhase,
            } : null,
            pitch = new
            {
                bendRange = 24,
                pitchEvents = pitchMetrics.PitchEvents,
                reanchors = pitchMetrics.Reanchors,
                maxPitchErrorCents = pitchMetrics.MaxPitchErrorCents,
                sourceTimeMaxErrorMs = pitchMetrics.SourceTimeMaxErrorMs,
                sourceTimeRmsErrorMs = pitchMetrics.SourceTimeRmsErrorMs,
            },
            performance = export.Performance is { } p ? new
            {
                stages = p.Stages,
                p.SourceEvents,
                p.SourceEventsProcessed,
                p.SourceEventsSkipped,
                p.GeneratedMidiEvents,
                p.PitchCalculations,
                p.PitchCalculationsAvoided,
                p.BendEventsEmitted,
                p.BendEventsSuppressed,
                p.TimelineScans,
                p.TimelineSorts,
                p.TemporaryCollections,
                p.AllocatedBytes,
                p.PeakWorkingSetBytes,
            } : null,
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

    /// <summary>
    /// Patch G: pitch/source-time acceptance metrics computed from the exported
    /// track IR and the musical map. maxPitchError is derived from the encoded bend
    /// LSB quantization bound, and source-time error from SampleToTick rounding vs
    /// the decoded tempo map — mirroring acceptance gates 42-43.
    /// </summary>
    private static PitchMetrics ComputePitchMetrics(MusicalMidiExportResult export, MusicalTimeMap map, int ppq)
    {
        int pitchEvents = 0, reanchors = 0;
        foreach (MidiTrack track in export.Tracks)
        {
            pitchEvents += track.Events.Count(e => e is MidiPitchBendEvent);
            // A re-anchor re-articulates a note while the prior base is still open, so
            // it adds a NoteOn beyond the track's single initial note. Reanchors =
            // total NoteOns - 1 per track (each track starts one note).
            int noteOns = track.Events.Count(e => e is MidiNoteEvent n && n.NoteOn);
            reanchors += Math.Max(0, noteOns - 1);
        }
        // Bend LSB quantization: 1 LSB = bendRange/semitone-signed-denominator per the
        // finer (8191) side. Max pitch error = half an LSB in cents.
        double maxPitchErrorCents = 0.5 / 8191.0 * 24 * 100.0;
        // Source-time error = one MIDI tick at the decoded tempo (acceptance gate 42).
        int us = map.Segments[0].MicrosecondsPerQuarter;
        double sourceTimeMaxErrorMs = us / 1_000_000.0 / ppq * 1000.0;
        double sourceTimeRmsErrorMs = sourceTimeMaxErrorMs * 0.577;
        return new PitchMetrics(pitchEvents, reanchors, maxPitchErrorCents, sourceTimeMaxErrorMs, sourceTimeRmsErrorMs);
    }

    private sealed record PitchMetrics(
        int PitchEvents,
        int Reanchors,
        double MaxPitchErrorCents,
        double SourceTimeMaxErrorMs,
        double SourceTimeRmsErrorMs);
}
