#nullable enable

using System.Diagnostics;

namespace Fmp.Core.Visualization.Rendering;

/// <summary>Opt-in renderer work counters accumulated per renderer instance.</summary>
internal sealed class RenderPerformanceMetrics
{
    public RenderPerformanceMetrics(bool enabled, string backendName = "Cpu")
    {
        Enabled = enabled;
        BackendName = backendName;
    }

    public bool Enabled { get; }
    public string BackendName { get; }
    public long Frames { get; set; }
    public long FullRedraws { get; set; }
    public long PartialRedraws { get; set; }
    public long UnchangedFrames { get; set; }
    public long RenderedPixels { get; set; }
    public long AvoidedPixels { get; set; }
    public long SurfaceCopies { get; set; }
    public long FullFrameCopies { get; set; }
    public long ScopeCopies { get; set; }
    public long CopiedBytes { get; set; }
    public long SourceCursorAdvances { get; set; }
    public long PianoRollCursorAdvances { get; set; }
    public long VisibleNotesVisited { get; set; }
    public long RenderTicks { get; set; }
    public long DynamicTicks { get; set; }
    public long FrameStateTicks { get; set; }
    public long CompositingTicks { get; set; }
    public long LayoutTicks { get; set; }
    public long StaticLayerTicks { get; set; }
    public long TextTicks { get; set; }
    public long PianoRollTicks { get; set; }
    public long PitchGridTicks { get; set; }
    public long PitchBandTicks { get; set; }
    public long GridLineTicks { get; set; }
    public long HeaderTicks { get; set; }
    public long BorderTicks { get; set; }
    public long OtherPanelTicks { get; set; }
    public long PresentationTicks { get; set; }
    public long RibbonTicks { get; set; }
    public long RibbonDecorationTicks { get; set; }
    public long RibbonColumnsEvaluated { get; set; }
    public long RibbonPixelsBlended { get; set; }
    public long PitchSegmentsVisited { get; set; }
    public long WaveformTicks { get; set; }
    public long GpuDrawTicks { get; set; }
    public long GpuFlushSyncTicks { get; set; }
    public long GpuReadbackTicks { get; set; }
    public long ScopeUploadTicks { get; set; }
    public long GpuFrameNanos { get; set; }
    public long AllocatedBytes { get; private set; }

    /// <summary>
    /// Starts a new measurement window without rebuilding the renderer or its
    /// sequential state. Benchmark warm-up frames therefore do not contaminate
    /// the reported frame counts, pixels, copies, or allocations.
    /// </summary>
    public void Reset()
    {
        Frames = 0;
        FullRedraws = 0;
        PartialRedraws = 0;
        UnchangedFrames = 0;
        RenderedPixels = 0;
        AvoidedPixels = 0;
        SurfaceCopies = 0;
        FullFrameCopies = 0;
        ScopeCopies = 0;
        CopiedBytes = 0;
        SourceCursorAdvances = 0;
        PianoRollCursorAdvances = 0;
        VisibleNotesVisited = 0;
        RenderTicks = 0;
        DynamicTicks = 0;
        FrameStateTicks = 0;
        CompositingTicks = 0;
        LayoutTicks = 0;
        StaticLayerTicks = 0;
        TextTicks = 0;
        PianoRollTicks = 0;
        PitchGridTicks = 0;
        PitchBandTicks = 0;
        GridLineTicks = 0;
        RibbonTicks = 0;
        RibbonDecorationTicks = 0;
        RibbonColumnsEvaluated = 0;
        RibbonPixelsBlended = 0;
        PitchSegmentsVisited = 0;
        WaveformTicks = 0;
        GpuDrawTicks = 0;
        GpuFlushSyncTicks = 0;
        GpuReadbackTicks = 0;
        ScopeUploadTicks = 0;
        GpuFrameNanos = 0;
        AllocatedBytes = 0;
    }

    public void FinishFrame(long allocatedBefore)
    {
        AllocatedBytes += Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    }

    public RenderPerformanceSnapshot Snapshot(int frameWidth, int frameHeight)
        => new(Seconds(RenderTicks), Seconds(DynamicTicks), Seconds(FrameStateTicks),
            Seconds(CompositingTicks), Seconds(LayoutTicks), Seconds(StaticLayerTicks), Seconds(TextTicks),
            Seconds(PianoRollTicks), Seconds(PitchGridTicks), Seconds(PitchBandTicks),
            Seconds(GridLineTicks), Seconds(RibbonTicks),
            Seconds(RibbonDecorationTicks), Seconds(WaveformTicks),
            Frames, FullRedraws, PartialRedraws, UnchangedFrames,
            RenderedPixels, AvoidedPixels, SurfaceCopies, FullFrameCopies,
            ScopeCopies, CopiedBytes, SourceCursorAdvances, PianoRollCursorAdvances,
            VisibleNotesVisited, RibbonColumnsEvaluated, RibbonPixelsBlended, PitchSegmentsVisited,
            checked((long)frameWidth * frameHeight),
            AllocatedBytes, Process.GetCurrentProcess().PeakWorkingSet64,
            BackendName,
            Seconds(GpuDrawTicks), Seconds(GpuFlushSyncTicks),
            Seconds(GpuReadbackTicks), Seconds(ScopeUploadTicks),
            GpuFrameNanos / 1_000_000_000.0);

    private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;
}

internal sealed record RenderPerformanceSnapshot(
    double RenderSeconds,
    double DynamicSeconds,
    double FrameStateSeconds,
    double CompositingSeconds,
    double LayoutSeconds,
    double StaticLayerSeconds,
    double TextSeconds,
    double PianoRollSeconds,
    double PitchGridSeconds,
    double PitchBandSeconds,
    double GridLineSeconds,
    double RibbonSeconds,
    double RibbonDecorationSeconds,
    double WaveformSeconds,
    long Frames,
    long FullRedraws,
    long PartialRedraws,
    long UnchangedFrames,
    long RenderedPixels,
    long AvoidedPixels,
    long SurfaceCopies,
    long FullFrameCopies,
    long ScopeCopies,
    long CopiedBytes,
    long SourceCursorAdvances,
    long PianoRollCursorAdvances,
    long VisibleNotesVisited,
    long RibbonColumnsEvaluated,
    long RibbonPixelsBlended,
    long PitchSegmentsVisited,
    long FramePixels,
    long AllocatedBytes = 0,
    long PeakWorkingSetBytes = 0,
    string BackendName = "Cpu",
    double GpuDrawSeconds = 0,
    double GpuFlushSyncSeconds = 0,
    double GpuReadbackSeconds = 0,
    double ScopeUploadSeconds = 0,
    double GpuFrameSeconds = 0)
{
    public double AllocatedBytesPerFrame =>
        Frames > 0 ? AllocatedBytes / (double)Frames : 0;

    public double FullFrameCopiesPerFrame =>
        Frames > 0 ? FullFrameCopies / (double)Frames : 0;
}
