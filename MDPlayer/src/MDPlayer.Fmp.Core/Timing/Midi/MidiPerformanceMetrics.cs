#nullable enable

using System.Diagnostics;
using Fmp.Core.Timing;

namespace Fmp.Core.Midi;

/// <summary>
/// Opt-in MIDI export work receipt. The normal exporter leaves this null so
/// counters and timestamps do not sit on the event hot path. When enabled, the
/// receipt is deliberately a flat value model: it can be serialized by the
/// application boundary without retaining source objects or diagnostic text.
/// </summary>
internal sealed class MidiPerformanceMetrics
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _normalizeTicks;
    private long _domainAnalysisTicks;
    private long _pitchAnalysisTicks;
    private long _tempoGridTicks;
    private long _eventGenerationTicks;
    private long _eventNormalizationTicks;
    private long _trackConstructionTicks;
    private long _serializationTicks;
    private long _fileWriteTicks;

    public long SourceEvents { get; set; }
    public long SourceEventsProcessed { get; set; }
    public long SourceEventsSkipped { get; set; }
    public long GeneratedMidiEvents { get; set; }
    public long PitchCalculations { get; set; }
    public long PitchCalculationsAvoided { get; set; }
    public long BendEventsEmitted { get; set; }
    public long BendEventsSuppressed { get; set; }
    public long TimelineScans { get; set; }
    public long TimelineSorts { get; set; }
    public long TemporaryCollections { get; set; }
    public long AllocatedBytes { get; set; }
    public long PeakWorkingSetBytes { get; set; }
    public long TempoOnsets { get; set; }
    public long TempoUniqueSamples { get; set; }
    public long TempoScoreForPhaseCalls { get; set; }
    public long TempoSubdivisionFitEvals { get; set; }
    public long TempoScorePhasesPruned { get; set; }
    public long TempoOnsetEvaluationsAvoided { get; set; }

    public void SetTempoInferenceCounters(TempoInferenceCounters counters)
    {
        TempoOnsets = counters.OnsetCount;
        TempoUniqueSamples = counters.UniqueSampleCount;
        TempoScoreForPhaseCalls = counters.ScoreForPhaseCalls;
        TempoSubdivisionFitEvals = counters.SubdivisionFitEvals;
        TempoScorePhasesPruned = counters.ScorePhasesPruned;
        TempoOnsetEvaluationsAvoided = counters.OnsetEvaluationsAvoided;
    }

    public long StartStage(MidiPerformanceStage stage) => Stopwatch.GetTimestamp();

    public void StopStage(MidiPerformanceStage stage, long start)
    {
        long elapsed = Stopwatch.GetTimestamp() - start;
        switch (stage)
        {
            case MidiPerformanceStage.Normalize: _normalizeTicks += elapsed; break;
            case MidiPerformanceStage.DomainAnalysis: _domainAnalysisTicks += elapsed; break;
            case MidiPerformanceStage.PitchAnalysis: _pitchAnalysisTicks += elapsed; break;
            case MidiPerformanceStage.TempoGridAnalysis: _tempoGridTicks += elapsed; break;
            case MidiPerformanceStage.EventGeneration: _eventGenerationTicks += elapsed; break;
            case MidiPerformanceStage.EventNormalization: _eventNormalizationTicks += elapsed; break;
            case MidiPerformanceStage.TrackConstruction: _trackConstructionTicks += elapsed; break;
            case MidiPerformanceStage.SmfSerialization: _serializationTicks += elapsed; break;
            case MidiPerformanceStage.FileWrite: _fileWriteTicks += elapsed; break;
        }
    }

    public MidiPerformanceSnapshot Snapshot()
    {
        long totalTicks = Stopwatch.GetTimestamp() - _started;
        return new MidiPerformanceSnapshot(
            new MidiPerformanceStages(
                Seconds(_normalizeTicks),
                Seconds(_domainAnalysisTicks),
                Seconds(_pitchAnalysisTicks),
                Seconds(_tempoGridTicks),
                Seconds(_eventGenerationTicks),
                Seconds(_eventNormalizationTicks),
                Seconds(_trackConstructionTicks),
                Seconds(_serializationTicks),
                Seconds(_fileWriteTicks),
                Seconds(totalTicks)),
            SourceEvents,
            SourceEventsProcessed,
            SourceEventsSkipped,
            GeneratedMidiEvents,
            PitchCalculations,
            PitchCalculationsAvoided,
            BendEventsEmitted,
            BendEventsSuppressed,
            TimelineScans,
            TimelineSorts,
            TemporaryCollections,
            AllocatedBytes,
            PeakWorkingSetBytes,
            TempoOnsets,
            TempoUniqueSamples,
            TempoScoreForPhaseCalls,
            TempoSubdivisionFitEvals,
            TempoScorePhasesPruned,
            TempoOnsetEvaluationsAvoided);
    }

    private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;
}

internal enum MidiPerformanceStage
{
    Normalize,
    DomainAnalysis,
    PitchAnalysis,
    TempoGridAnalysis,
    EventGeneration,
    EventNormalization,
    TrackConstruction,
    SmfSerialization,
    FileWrite,
}

public sealed record MidiPerformanceStages(
    double NormalizeSeconds,
    double DomainAnalysisSeconds,
    double PitchAnalysisSeconds,
    double TempoGridAnalysisSeconds,
    double EventGenerationSeconds,
    double EventNormalizationSeconds,
    double TrackConstructionSeconds,
    double SmfSerializationSeconds,
    double FileWriteSeconds,
    double TotalSeconds)
{
    /// <summary>Capture is owned by the caller and is zero for core-only exports.</summary>
    public double CaptureSeconds { get; init; }
}

public sealed record MidiPerformanceSnapshot(
    MidiPerformanceStages Stages,
    long SourceEvents,
    long SourceEventsProcessed,
    long SourceEventsSkipped,
    long GeneratedMidiEvents,
    long PitchCalculations,
    long PitchCalculationsAvoided,
    long BendEventsEmitted,
    long BendEventsSuppressed,
    long TimelineScans,
    long TimelineSorts,
    long TemporaryCollections,
    long AllocatedBytes,
    long PeakWorkingSetBytes,
    long TempoOnsets,
    long TempoUniqueSamples,
    long TempoScoreForPhaseCalls,
    long TempoSubdivisionFitEvals,
    long TempoScorePhasesPruned,
    long TempoOnsetEvaluationsAvoided)
{
    public double AllocatedBytesPerSourceEvent =>
        SourceEventsProcessed > 0 ? AllocatedBytes / (double)SourceEventsProcessed : 0;

    /// <summary>
    /// Adds timings owned by the caller around the core exporter, such as tempo
    /// map construction, without putting those clocks on the event hot path.
    /// </summary>
    public MidiPerformanceSnapshot WithOuterStageTimings(
        double? captureSeconds = null,
        double? tempoGridSeconds = null,
        double? fileWriteSeconds = null)
    {
        double outerSeconds = 0;
        if (captureSeconds is double capture)
            outerSeconds += capture;
        if (tempoGridSeconds is double tempo)
            outerSeconds += tempo;
        if (fileWriteSeconds is double fileWrite)
            outerSeconds += fileWrite;

        return this with
        {
            Stages = Stages with
            {
                CaptureSeconds = captureSeconds ?? Stages.CaptureSeconds,
                TempoGridAnalysisSeconds = tempoGridSeconds ?? Stages.TempoGridAnalysisSeconds,
                FileWriteSeconds = fileWriteSeconds ?? Stages.FileWriteSeconds,
                TotalSeconds = Stages.TotalSeconds + outerSeconds,
            },
        };
    }
}
