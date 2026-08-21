using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization.Rendering;

/// <summary>
/// Deterministic complexity, allocation, and frame-pipeline regression guards
/// for the renderer performance overhaul. No wall-clock scaling assertions:
/// complexity is measured with PitchCamera's build-work counters, allocation
/// with GC allocation counters, and pipeline overlap with controlled fake
/// stage delays.
/// </summary>
public sealed class RendererPerfRegressionTests
{
    private const int SampleRate = 44100;
    private const int Fps = 60;

    private static PreparedNote[] ConstantDensityNotes(int noteCount, long timelineEnd)
    {
        // Constant musical density: notes evenly spread over the timeline with
        // a fixed duration, so 1x/2x/4x durations scale E and F together.
        var notes = new PreparedNote[noteCount];
        const long duration = 4410; // 100 ms
        for (int i = 0; i < noteCount; i++)
        {
            long start = (long)i * timelineEnd / noteCount;
            notes[i] = MakeNote(start, Math.Min(timelineEnd + duration, start + duration), 40 + (i % 48));
        }
        return notes;
    }

    private static PreparedNote MakeNote(long start, long end, int midi) => new()
    {
        StartSample = start,
        EndSample = end,
        InitialMidiNote = midi,
        Mode = VisualizationNoteMode.Fm,
        InstrumentId = "inst:1",
        IsRetrigger = false,
        Fill = new OverlayColor(200, 200, 200),
        ActiveFill = new OverlayColor(255, 255, 255),
        Accent = new OverlayColor(220, 220, 220),
        CapFill = new OverlayColor(240, 240, 240),
        Pitch = new[] { new PreparedPitchPoint((start + end) / 2, midi + 5) },
    };

    private static PitchCamera BuildCamera(PreparedNote[] notes, long timelineEnd)
        => new(notes, laneHeight: 168, sampleRate: SampleRate, pastSeconds: 0.75,
            futureSeconds: 2.25, timelineStartSample: 0, timelineEndSample: timelineEnd,
            Fps, 1);

    // ------------------------------------------------------------------
    // B. Complexity regression (deterministic counters, not wall clock).
    // ------------------------------------------------------------------

    [Fact]
    public void CameraBuildWork_ScalesLinearly_WhenDurationAndDensityScaleTogether()
    {
        long baseEnd = SampleRate * 10; // 10 s @ 60 fps => ~600 frames
        var stats1 = BuildCamera(ConstantDensityNotes(300, baseEnd), baseEnd).LastBuildStats!;

        long end2 = baseEnd * 2;
        var stats2 = BuildCamera(ConstantDensityNotes(600, end2), end2).LastBuildStats!;

        long end4 = baseEnd * 4;
        var stats4 = BuildCamera(ConstantDensityNotes(1200, end4), end4).LastBuildStats!;

        // Linear input => each counter roughly doubles then quadruples from 1x.
        // A quadratic camera would show ~2x at "2x" and ~16x at "4x" on the
        // event counters. Allow generous slack (3x / 8x) for constant factors.
        AssertScaling(stats1.NoteEnterEvents * 3, stats2.NoteEnterEvents);
        AssertScaling(stats1.NoteEnterEvents * 8, stats4.NoteEnterEvents);
        AssertScaling(stats1.NoteLeaveEvents * 3 + 64, stats2.NoteLeaveEvents);
        AssertScaling(stats1.NoteLeaveEvents * 8 + 128, stats4.NoteLeaveEvents);
        AssertScaling(stats1.PitchPointUpdates * 3, stats2.PitchPointUpdates);
        AssertScaling(stats1.PitchPointUpdates * 8, stats4.PitchPointUpdates);
        AssertScaling(stats1.ActiveStartEvents * 3 + 64, stats2.ActiveStartEvents);
        AssertScaling(stats1.ActiveStartEvents * 8 + 256, stats4.ActiveStartEvents);
        AssertScaling(stats1.ActiveEndEvents * 3 + 64, stats2.ActiveEndEvents);
        AssertScaling(stats1.ActiveEndEvents * 8 + 256, stats4.ActiveEndEvents);

        // Frame sweep steps are exactly the frame count (one step per frame).
        Assert.Equal(
            FrameSampleClock.FrameCount(baseEnd, SampleRate, Fps, 1),
            stats1.FrameSweepSteps);
        Assert.Equal(
            FrameSampleClock.FrameCount(end4, SampleRate, Fps, 1),
            stats4.FrameSweepSteps);
    }

    private static void AssertScaling(double allowedMax, double actual)
        => Assert.True(actual <= allowedMax,
            $"work grew super-linearly: allowed {allowedMax}, actual {actual}");

    // ------------------------------------------------------------------
    // C. Allocation regression (>128-note camera must not allocate per-frame
    // note arrays; allocations scale with E at fixed F).
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1000)]
    [InlineData(4000)]
    public void CameraConstruction_AllocationsScaleWithNoteCount_NotFramesTimesNotes(int noteCount)
    {
        long timelineEnd = SampleRate * 20; // fixed F across both densities
        var notes = ConstantDensityNotes(noteCount, timelineEnd);

        // Warm-up construction to populate JIT/type state, then measure.
        _ = BuildCamera(ConstantDensityNotes(noteCount, timelineEnd), timelineEnd);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        PitchCamera camera = BuildCamera(notes, timelineEnd);
        long after = GC.GetAllocatedBytesForCurrentThread();

        long totalFrames = FrameSampleClock.FrameCount(timelineEnd, SampleRate, Fps, 1);
        long framesTimesNotes = totalFrames * (long)notes.Length;
        long allocated = after - before;

        Assert.NotNull(camera.LastBuildStats);
        // The forbidden shape would allocate ~16 bytes * F * E (a NoteExtent[]
        // per frame). Even at the small scale that dwarfs every legitimate
        // O(F + E + Q) structure by orders of magnitude.
        Assert.True(allocated < framesTimesNotes / 4,
            $"allocated {allocated} bytes; F*E reference is {framesTimesNotes} bytes — per-frame note allocation appears present.");
    }

    // ------------------------------------------------------------------
    // D. Sequential vs random-access frame equivalence (identical RGBA).
    // ------------------------------------------------------------------

    [Fact]
    public void SequentialRender_MatchesRandomAccessRender_ByteForByte()
    {
        var renderer = ScopeCadenceHelper.CreateRenderer(scopeFps: null, outputFps: 60);
        long total = renderer.TotalFrames;
        Assert.True(total > 12, $"fixture too short: {total} frames");

        // Frames chosen to hit start, onsets, bends, dense sections, near end.
        var probes = new long[] { 0, 1, 2, 5, 7, 9, 11, total - 2, total - 1 };

        var sequential = new byte[probes.Length][];
        var session = renderer.CreateSequentialSession();
        try
        {
            var buffer = new byte[renderer.FrameByteCount];
            session.Initialize(buffer);
            for (long f = 0; f < total; f++)
            {
                session.RenderNext(f, buffer);
                int probeIdx = Array.IndexOf(probes, f);
                if (probeIdx >= 0)
                    sequential[probeIdx] = buffer.ToArray();
            }
        }
        finally
        {
        }

        try
        {
            foreach (long probe in probes)
            {
                byte[] randomAccess = renderer.RenderFrame(probe);
                int probeIdx = Array.IndexOf(probes, probe);
                Assert.Equal(sequential[probeIdx], randomAccess);
            }
        }
        finally
        {
            renderer.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // E. Pipeline overlap, failure, cancellation, saturation.
    // ------------------------------------------------------------------

    private static SinglePassComposer.ComposeMetrics RunPipeline(
        int capacity,
        long totalFrames,
        Func<SinglePassComposer.FrameSlot, long, bool> fill,
        Action<SinglePassComposer.FrameSlot, long, SinglePassComposer.PipelineMetrics> render,
        Func<SinglePassComposer.FrameSlot, SinglePassComposer.PipelineMetrics, bool> write,
        CancellationToken token = default)
        => SinglePassComposer.RunFramePipeline(
            capacity, gridFrameBytes: 16, outFrameBytes: 32, totalFrames,
            fill, render, write,
            initializeSession: static _ => { },
            includeQueueWaitInCorrscopeMetrics: false,
            token, abortProducer: null);

    [Fact]
    public void Pipeline_OverlapsRenderWithWrite_FasterThanSerializedSum()
    {
        const long frames = 12;
        const int delayMs = 15;
        long serializedEstimate = 2 * frames * delayMs; // ms

        var stopwatch = Stopwatch.StartNew();
        SinglePassComposer.ComposeMetrics metrics = RunPipeline(
            3, frames,
            fill: static (_, _) => true,
            render: static (_, _, _) => Thread.Sleep(delayMs),
            write: static (_, _) => { Thread.Sleep(delayMs); return true; });
        stopwatch.Stop();

        double serializedMs = serializedEstimate;
        double overlappedMs = stopwatch.Elapsed.TotalMilliseconds;
        Assert.True(overlappedMs < serializedMs * 0.85,
            $"no overlap evidence: {overlappedMs:F0} ms vs serialized {serializedMs:F0} ms");
        Assert.Equal(frames, metrics.FrameCount);
    }

    [Fact]
    public void Pipeline_PreservesRootRendererException_AndDoesNotDeadlock()
    {
        var boom = new InvalidOperationException("renderer exploded");
        var error = Assert.Throws<InvalidOperationException>(() => RunPipeline(
            2, 100,
            fill: static (_, _) => true,
            render: (_, index, _) =>
            {
                if (index == 3)
                    throw boom;
            },
            write: static (_, _) => true));
        Assert.Same(boom, error);
    }

    [Fact]
    public void Pipeline_PreservesWriterException_AndDoesNotDeadlock()
    {
        var boom = new IOException("pipe vanished");
        var error = Assert.Throws<IOException>(() => RunPipeline(
            2, 100,
            fill: static (_, _) => true,
            render: static (_, _, _) => { },
            write: (_, _) => throw boom));
        Assert.Same(boom, error);
    }

    [Fact]
    public void Pipeline_BrokenPipe_StopsCleanly_AtWriteBoundary()
    {
        const long stopAfter = 5;
        long written = 0;
        var metrics = RunPipeline(
            2, 100,
            fill: static (_, _) => true,
            render: static (_, _, _) => { },
            write: (_, _) => ++written <= stopAfter);
        Assert.Equal(stopAfter, metrics.FrameCount);
    }

    [Fact]
    public async Task Pipeline_Cancellation_UnblocksAllStages()
    {
        using var cts = new CancellationTokenSource();
        var cancelled = Assert.ThrowsAny<OperationCanceledException>(() => RunPipeline(
            2, 10_000,
            fill: static (_, _) => true,
            render: static (_, _, _) => Thread.Sleep(1),
            write: (_, _) =>
            {
                cts.Cancel();
                return true;
            },
            token: cts.Token));
        Assert.IsType<OperationCanceledException>(cancelled);
        await Task.CompletedTask;
    }

    [Fact]
    public void Pipeline_QueueSaturation_BoundedDepth_AndCompletes()
    {
        const int capacity = 2;
        var metrics = RunPipeline(
            capacity, 50,
            fill: static (_, _) => true,
            render: static (_, _, _) => Thread.Sleep(1),
            write: static (_, _) => { Thread.Sleep(2); return true; });
        Assert.True(metrics.MaxQueueDepth <= capacity,
            $"queue depth {metrics.MaxQueueDepth} exceeded capacity {capacity}");
        Assert.Equal(50, metrics.FrameCount);
    }
}

/// <summary>Scope source whose frames depend on the frame index (bends move).</summary>
internal sealed class ScriptedScopeSource : IScopeFrameSource
{
    private readonly int _gridBytes;
    public int Reads;

    public ScriptedScopeSource(int gridBytes) => _gridBytes = gridBytes;

    public bool FramesAreOpaque => false;

    public void ReadFrame(int frameIndex, Span<byte> destination)
    {
        Reads++;
        destination.Clear();
        if (destination.Length >= 8 && frameIndex < 256)
        {
            destination[0] = (byte)frameIndex;
            destination[4] = (byte)(frameIndex >> 8);
        }
    }

    public void Dispose() { }
}

internal static class ScopeCadenceHelper
{
    /// <summary>Mirrors ScopeCadenceMappingTests.CreateRenderer with a scripted scope source.</summary>
    internal static VisualizationFrameRenderer CreateRenderer(double? scopeFps, int outputFps)
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        var overlay = new PanelOverlayRenderer(
            timeline,
            RendererTestLayout.Build(timeline),
            new PanelOverlayRenderer.Options { FpsNumerator = outputFps, FpsDenominator = 1 });
        var source = new ScriptedScopeSource(overlay.ScopeFrameByteCount);
        return new VisualizationFrameRenderer(overlay, source, scopeFps);
    }
}
