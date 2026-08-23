using System.Diagnostics;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

#nullable enable
namespace MDPlayer.Fmp.Tests;

/// <summary>
/// TEMPORARY throughput probe: measures ms/frame for the sequential-session
/// (video) path and the random-access composite path at 1920x1080, plus
/// managed allocation growth per frame. Removed after comparison.
/// </summary>
public sealed class ScratchSpeedProbe
{
    private readonly ITestOutputHelper _out;
    public ScratchSpeedProbe(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(1920, 1080)]
    public void Measure(int w, int h)
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        var renderer = new PanelOverlayRenderer(
            timeline,
            RendererTestLayout.Build(timeline, w, h),
            new PanelOverlayRenderer.Options { FpsNumerator = 60, FpsDenominator = 1 });
        byte[] dst = new byte[renderer.FrameByteCount];
        byte[] grid = new byte[renderer.ScopeFrameByteCount];
        long total = renderer.TotalFrames;
        _out.WriteLine($"PROBE slice: {w}x{h} frameBytes={renderer.FrameByteCount:N0} totalFrames={total} panels={renderer.Layout.PanelCount}");

        const int N = 1500;
        var session = renderer.CreateSequentialSession();
        session.Initialize(dst);
        for (int i = 0; i < 30; i++) session.RenderNext(i % total, grid, dst);

        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < N; i++) session.RenderNext(i % total, grid, dst);
        sw.Stop();
        long alloc = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
        _out.WriteLine($"PROBE sequential(RenderNext): {sw.Elapsed.TotalMilliseconds / N:F3} ms/frame  alloc={alloc / (double)N:F0} B/frame");

        for (int i = 0; i < 30; i++) renderer.RenderCompositeFrame(i % total, grid, dst);
        beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        sw.Restart();
        for (int i = 0; i < N; i++) renderer.RenderCompositeFrame(i % total, grid, dst);
        sw.Stop();
        alloc = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
        _out.WriteLine($"PROBE composite(RenderCompositeFrame): {sw.Elapsed.TotalMilliseconds / N:F3} ms/frame  alloc={alloc / (double)N:F0} B/frame");

        renderer.Dispose();
    }

    [Fact]
    public void OverviewGapDiagnostic()
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        VisualizationTopology topology = VisualizationTopologyBuilder.Build(
            timeline, VisualizationChannelFilter.All, VisualizationGroupBy.None);
        ResolvedVisualizationLayout resolved = VisualizationLayoutResolver.Resolve(
            960, 300, 0.75, 2.25, VisualizationLayoutMode.Diagnostic, topology,
            topology.Panels.Count, VisualizationScopePosition.Top);
        using var renderer = new PanelOverlayRenderer(timeline, resolved);
        var lay = renderer.Layout;
        _out.WriteLine($"variant={resolved.Variant} panels={lay.PanelCount}");
        _out.WriteLine($"TopBar={lay.TopBarRect} BottomBar={lay.BottomBarRect}");
        for (int p = 0; p < lay.PanelCount; p++)
            _out.WriteLine($"p{p} hdr={lay.GetHeaderRect(p)} tl={lay.GetTimelineRect(p)} scope={lay.GetScopeRect(p)}");

        byte[] baseline = renderer.RenderFrame(0);
        byte[] scopeGrid = new byte[renderer.ScopeFrameByteCount];
        Array.Fill(scopeGrid, (byte)200);
        byte[] composed = new byte[renderer.FrameByteCount];
        renderer.RenderCompositeFrame(0, scopeGrid, composed);
        // Force a full re-establish, then composed again: isolates my change.
        renderer.RenderDynamicFrame(0, new byte[renderer.FrameByteCount]);
        byte[] composedFull = new byte[renderer.FrameByteCount];
        renderer.RenderCompositeFrame(0, scopeGrid, composedFull);
        {
            int first = -1, count = 0;
            for (int i = 0; i < composed.Length; i++)
            {
                if (composed[i] != composedFull[i])
                {
                    if (first < 0) first = i;
                    count++;
                }
            }
            _out.WriteLine($"incremental vs full composed: firstDiffByte={first} diffBytes={count}");
            if (first >= 0)
            {
                int y = first / 4 / renderer.Width;
                int x = first / 4 % renderer.Width;
                _out.WriteLine($"  at ({x},{y}) inc=({composed[first]},{composed[first+1]},{composed[first+2]},{composed[first+3]}) full=({composedFull[first]},{composedFull[first+1]},{composedFull[first+2]},{composedFull[first+3]})");
            }
        }

        int shown = 0;
        for (int offset = 0; offset < composed.Length && shown < 12; offset += 4)
        {
            if (composed[offset] == baseline[offset]
                && composed[offset + 1] == baseline[offset + 1]
                && composed[offset + 2] == baseline[offset + 2]
                && composed[offset + 3] == baseline[offset + 3])
                continue;
            int pixel = offset / 4;
            int x = pixel % renderer.Width;
            int y = pixel / renderer.Width;
            bool inScope = false;
            for (int p = 0; p < lay.PanelCount; p++)
            {
                OverlayRect s = lay.GetScopeRect(p);
                if (x >= s.X && x < s.Right && y >= s.Y && y < s.Bottom) { inScope = true; break; }
            }
            if (!inScope)
            {
                _out.WriteLine($"DIFF at ({x},{y}) RGBA b=({baseline[offset]},{baseline[offset+1]},{baseline[offset+2]},{baseline[offset+3]}) c=({composed[offset]},{composed[offset+1]},{composed[offset+2]},{composed[offset+3]})");
                shown++;
            }
        }
        if (shown == 0)
            _out.WriteLine("no diffs outside scope rects");
        // Full-width diff map of the two rows around the mark.
        for (int y = 193; y <= 195; y++)
        {
            var row = new System.Text.StringBuilder();
            for (int x = 0; x < renderer.Width; x++)
            {
                int off = (y * renderer.Width + x) * 4;
                bool same = baseline[off] == composed[off] && baseline[off + 1] == composed[off + 1]
                    && baseline[off + 2] == composed[off + 2] && baseline[off + 3] == composed[off + 3];
                row.Append(same ? '.' : '#');
            }
            _out.WriteLine($"row y={y}: {row}");
        }
        // Geometry for row-2 panels + playhead X positions.
        for (int p = 8; p < lay.PanelCount; p++)
            _out.WriteLine($"p{p} hdr={lay.GetHeaderRect(p)} tl={lay.GetTimelineRect(p)} scope={lay.GetScopeRect(p)} panel={lay.GetPanelRect(p)} playheadX={lay.GetPlayheadX(p)}");
    }

    [Fact]
    public void AllocationProbe()
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        var renderer = new PanelOverlayRenderer(
            timeline,
            RendererTestLayout.Build(timeline, 1920, 1080),
            new PanelOverlayRenderer.Options { FpsNumerator = 60, FpsDenominator = 1 });
        byte[] buffer = new byte[renderer.FrameByteCount];
        long total = renderer.TotalFrames;
        var session = renderer.CreateSequentialSession();
        session.Initialize(buffer);
        for (long frame = 0; frame < 30; frame++)
            session.RenderNext(frame % total, ReadOnlySpan<byte>.Empty, buffer);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (long frame = 30; frame < 330; frame++)
            session.RenderNext(frame % total, ReadOnlySpan<byte>.Empty, buffer);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        _out.WriteLine($"ALLOC delta={delta} perFrame={delta / 300.0:F2}");
        renderer.Dispose();
    }

    [Fact]
    public void AllocationProbeSmall()
    {
        const int sampleRate = 1000;
        var timeline = new VisualizationTimeline
        {
            SampleRate = sampleRate,
            StartSample = 0,
            EndSample = 100_000,
            Instruments =
            [
                new InstrumentDefinition("ym2608:aaaaaa1111111111", "fm", 4, 3, 0, 2,
                    Array.Empty<FmOperatorDefinition>()),
            ],
            Notes =
            [
                new NoteEvent("ym2608.0.fm.1", 500, 5000, 440.0, 60,
                    "ym2608:aaaaaa1111111111", VisualizationNoteMode.Fm, false,
                    Array.Empty<PitchChange>()),
            ],
            Rhythm = Array.Empty<RhythmEvent>(),
        };
        var renderer = new PanelOverlayRenderer(timeline, RendererTestLayout.Build(timeline),
            new PanelOverlayRenderer.Options { FpsNumerator = 20, FpsDenominator = 1 });
        byte[] buffer = new byte[renderer.FrameByteCount];
        for (long f = 0; f < 30; f++)
            renderer.RenderCompositeFrame(f, ReadOnlySpan<byte>.Empty, buffer);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (long f = 30; f < 330; f++)
            renderer.RenderCompositeFrame(f, ReadOnlySpan<byte>.Empty, buffer);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        _out.WriteLine($"SMALL-ALLOC delta={delta} perFrame={delta / 300.0:F2}");
        renderer.Dispose();
    }

    [Fact]
    public void RestoreIsolation()
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        var renderer = new PanelOverlayRenderer(
            timeline,
            RendererTestLayout.Build(timeline, 1920, 1080),
            new PanelOverlayRenderer.Options { FpsNumerator = 60, FpsDenominator = 1 });
        var lay = renderer.Layout;
        _out.WriteLine($"TopBar={lay.TopBarRect} BottomBar={lay.BottomBarRect}");
        _out.WriteLine($"hdr0={lay.GetHeaderRect(0)} tl0={lay.GetTimelineRect(0)} hdr1={lay.GetHeaderRect(1)}");

        byte[] a = renderer.RenderFrame(40);
        // Force a full re-establish via RenderDynamicFrame (clears canvas + flag).
        renderer.RenderDynamicFrame(0, new byte[renderer.FrameByteCount]);
        byte[] b = renderer.RenderFrame(40);
        // Incremental from b's persistent canvas: render a neighbor, then 40 again.
        renderer.RenderFrame(39);
        byte[] c = renderer.RenderFrame(40);

        bool InRects(int x, int y, bool body)
        {
            if (lay.TopBarRect.Contains(x, y) || lay.BottomBarRect.Contains(x, y)) return true;
            for (int p = 0; p < renderer.Layout.PanelCount; p++)
            {
                if (lay.GetHeaderRect(p).Contains(x, y)) return true;
                if (lay.GetTimelineRect(p).Contains(x, y)) return true;
                if (body && lay.GetScopeRect(p).Contains(x, y)) return true;
            }
            return false;
        }

        void Report(string label, byte[] a0, byte[] b0, bool body)
        {
            int first = -1, count = 0;
            for (int i = 0; i < a0.Length; i++)
            {
                if (a0[i] != b0[i])
                {
                    if (first < 0) first = i;
                    count++;
                }
            }
            _out.WriteLine($"{label}: firstDiffByte={first} diffBytes={count}");
            if (first >= 0)
            {
                int y = first / 4 / 1920;
                int x = first / 4 % 1920;
                _out.WriteLine($"  at ({x},{y}) channel={first % 4} a={a0[first]} b={b0[first]} inRects={InRects(x, y, body)}");
            }
        }

        Report("a vs b (full re-establish)", a, b, true);
        Report("a vs c (incremental)", a, c, true);
        renderer.Dispose();
    }
}