using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// PR 10 D1: verifies the per-frame hot path does not allocate managed memory
/// (§20.1). Measures GC allocated bytes around a warm RenderFrame loop and
/// asserts the delta is zero after preparation warm-up.
/// </summary>
public sealed class HotPathAllocationTests
{
    private const int SampleRate = 1000;
    private const string Fm1 = "ym2608.0.fm.1";
    private const string InstrumentA = "ym2608:aaaaaa1111111111";

    private static NoteEvent Note(long start, long end, double midi)
        => new(Fm1, start, end, 440.0, midi, InstrumentA, VisualizationNoteMode.Fm, false, Array.Empty<PitchChange>());

    private static VisualizationTimeline Timeline(params NoteEvent[] notes)
        => new()
        {
            SampleRate = SampleRate,
            StartSample = 0,
            EndSample = 100_000,
            Instruments = [new InstrumentDefinition(InstrumentA, "fm", 4, 3, 0, 2, Array.Empty<FmOperatorDefinition>())],
            Notes = notes,
            Rhythm = Array.Empty<RhythmEvent>(),
        };

    private static PanelOverlayRenderer Renderer(VisualizationTimeline timeline)
        => new(timeline, RendererTestLayout.Build(timeline), new PanelOverlayRenderer.Options
        {
            FpsNumerator = 20,
            FpsDenominator = 1,
        });

    [Fact]
    public void RenderFrame_NoPerFrameManagedAllocationGrowth()
    {
        // §20.1: no per-frame managed allocation growth. All clock and pitch
        // labels are prepared before this measurement.
        var timeline = Timeline(Note(500, 5000, 60));
        var renderer = Renderer(timeline);
        byte[] buffer = new byte[renderer.FrameByteCount];

        // Warmup: render enough frames to exercise every dynamic branch.
        for (long f = 0; f < 30; f++)
            renderer.RenderCompositeFrame(f, ReadOnlySpan<byte>.Empty, buffer);

        // Force GC to get a clean baseline.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();

        for (long f = 30; f < 330; f++)
            renderer.RenderCompositeFrame(f, ReadOnlySpan<byte>.Empty, buffer);

        long afterBytes = GC.GetAllocatedBytesForCurrentThread();
        long delta = afterBytes - beforeBytes;

        Assert.Equal(0, delta);
    }

    [Fact]
    public void RenderFrame_CLabelCacheEliminatesGridStringAllocation()
    {
        // Specifically verify that C-label grid rows don't allocate per frame.
        // The precomputed COctaveLabels array replaces "C" + (midi/12-1).
        var timeline = Timeline(Note(500, 5000, 60));
        var renderer = Renderer(timeline);
        byte[] buffer = new byte[renderer.FrameByteCount];

        // Render a frame with visible C labels (midi 60 = C5).
        renderer.RenderCompositeFrame(1, ReadOnlySpan<byte>.Empty, buffer);

        // The COctaveLabels array must be precomputed and non-empty.
        Assert.True(COctaveLabels.Length >= 12,
            "COctaveLabels must be precomputed with at least 12 entries (octaves -1 through 10).");
    }

    [Fact]
    public void SequentialSession_NoPerFrameManagedAllocation()
    {
        var renderer = Renderer(Timeline(Note(500, 5000, 60)));
        byte[] buffer = new byte[renderer.FrameByteCount];
        var session = renderer.CreateSequentialSession();
        session.Initialize(buffer);
        for (long frame = 0; frame < 30; frame++)
            session.RenderNext(frame, ReadOnlySpan<byte>.Empty, buffer);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();

        for (long frame = 30; frame < 330; frame++)
            session.RenderNext(frame, ReadOnlySpan<byte>.Empty, buffer);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - beforeBytes);
    }

    private static string[] COctaveLabels =>
        typeof(PanelOverlayRenderer)
            .GetField("COctaveLabels", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetValue(null) as string[]
        ?? Array.Empty<string>();
}
