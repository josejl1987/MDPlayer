using Fmp.Core.Metadata;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// PR 7: FM and FM3 presentation (§13.2 instrument-change overlay, §13.3
/// operator bars, §13.4 FM3 operator reduced opacity). All assertions are
/// pixel-exact or visibility-based, with deterministic timing.
/// </summary>
public sealed class FmPresentationTests
{
    private const int SampleRate = 1000;
    private const string Fm1 = "ym2608.0.fm.1";
    private const string InstrumentA = "ym2608:aaaaaa1111111111";
    private const string InstrumentB = "ym2608:bbbbbb2222222222";

    private static NoteEvent Note(long start, long end, double midi, string instrument = InstrumentA)
        => new(Fm1, start, end, 440.0, midi, instrument, VisualizationNoteMode.Fm, false, Array.Empty<PitchChange>());

    private static VisualizationTimeline Timeline(params NoteEvent[] notes)
        => new()
        {
            SampleRate = SampleRate,
            StartSample = 0,
            EndSample = 100_000,
            Instruments =
            [
                FmInstrument(InstrumentA, 4),
                FmInstrument(InstrumentB, 2),
            ],
            Notes = notes,
            Rhythm = Array.Empty<RhythmEvent>(),
        };

    private static InstrumentDefinition FmInstrument(string id, int algorithm)
    {
        var operators = new FmOperatorDefinition[]
        {
            new(31, 10, 5, 4, 8, 20, 1, 2, 0, true, 0),
            new(20, 8, 4, 3, 12, 30, 1, 2, 0, false, 0),
            new(15, 6, 3, 2, 16, 40, 1, 1, 0, false, 0),
            new(10, 4, 2, 1, 24, 50, 1, 1, 0, true, 0),
        };
        return new InstrumentDefinition(id, "fm", algorithm, 3, 0, 2, operators);
    }

    private static PanelOverlayRenderer Renderer(VisualizationTimeline timeline)
        => new(timeline, RendererTestLayout.Build(timeline), new PanelOverlayRenderer.Options
        {
            FpsNumerator = 20,
            FpsDenominator = 1,
        });

    private static OverlayRect Header(PanelOverlayRenderer renderer)
        => renderer.Layout.GetHeaderRect(0);

    private static int FrameForSample(long sample)
        => OverlayLayout.FrameToSample(0, SampleRate, 20, 1) == 0
            ? (int)(sample * 20 / SampleRate)
            : (int)Math.Round((double)sample * 20 / SampleRate);

    // ---- §13.2 instrument-change overlay ----

    [Fact]
    public void InstrumentChange_OverlayAppearsWithin800Ms()
    {
        // Note A starts at 500; note B (different instrument) starts at 5000.
        // At frame for sample 5100 (100 ms after change), the overlay should show.
        var renderer = Renderer(Timeline(
            Note(500, 5000, 60, InstrumentA),
            Note(5000, 9000, 64, InstrumentB)));
        byte[] frame = renderer.RenderFrame(FrameForSample(5100));
        OverlayRect header = Header(renderer);

        // The overlay text "ALG 2" should appear somewhere in the header.
        // Since we can't easily check text rendering pixel-by-pixel, verify
        // that the header has more bright pixels than a non-change frame
        // (the operator bars add brightness).
        byte[] frameNormal = renderer.RenderFrame(FrameForSample(3000));
        int overlayBrightness = CountBrightPixels(frame, renderer.Width, header);
        int normalBrightness = CountBrightPixels(frameNormal, renderer.Width, header);
        Assert.True(overlayBrightness > normalBrightness,
            $"Instrument-change overlay should add brightness: overlay={overlayBrightness}, normal={normalBrightness}.");
    }

    [Fact]
    public void InstrumentChange_OverlayDisappearsAfter800Ms()
    {
        // Note B starts at 5000; at sample 5900 (900 ms later), overlay is gone.
        var renderer = Renderer(Timeline(
            Note(500, 5000, 60, InstrumentA),
            Note(5000, 9000, 64, InstrumentB)));
        byte[] frameAfter = renderer.RenderFrame(FrameForSample(5900));
        OverlayRect header = Header(renderer);

        // After 800 ms, the header should look like a normal (non-change) frame.
        var rendererNormal = Renderer(Timeline(Note(500, 9000, 60, InstrumentA)));
        byte[] frameNormal = rendererNormal.RenderFrame(FrameForSample(5900));
        int afterBrightness = CountBrightPixels(frameAfter, renderer.Width, header);
        int normalBrightness = CountBrightPixels(frameNormal, renderer.Width, header);
        Assert.True(afterBrightness <= normalBrightness + 5,
            $"Overlay should disappear after 800 ms: after={afterBrightness}, normal={normalBrightness}.");
    }

    [Fact]
    public void InstrumentChange_NoOverlayWhenInstrumentUnchanged()
    {
        // Two notes with the same instrument — no change overlay.
        var renderer = Renderer(Timeline(
            Note(500, 5000, 60, InstrumentA),
            Note(5000, 9000, 64, InstrumentA)));
        byte[] frame = renderer.RenderFrame(FrameForSample(5100));
        OverlayRect header = Header(renderer);

        // Should look like a normal frame (no operator bars).
        var rendererSingle = Renderer(Timeline(Note(500, 9000, 60, InstrumentA)));
        byte[] frameSingle = rendererSingle.RenderFrame(FrameForSample(5100));
        int withRetrofit = CountBrightPixels(frame, renderer.Width, header);
        int single = CountBrightPixels(frameSingle, renderer.Width, header);
        Assert.True(withRetrofit <= single + 5,
            $"No overlay when instrument unchanged: retrofit={withRetrofit}, single={single}.");
    }

    // ---- §13.2 overlay does not cover scope ----

    [Fact]
    public void InstrumentChange_OverlayStaysInHeader()
    {
        var renderer = Renderer(Timeline(
            Note(500, 5000, 60, InstrumentA),
            Note(5000, 9000, 64, InstrumentB)));
        byte[] frameOverlay = renderer.RenderFrame(FrameForSample(5100));
        OverlayRect scope = renderer.Layout.GetScopeRect(0);

        // Compare the scope's top row brightness against a frame with no
        // instrument change — the overlay should not add extra bright pixels
        // beyond what the normal header border already produces.
        var rendererNormal = Renderer(Timeline(Note(500, 9000, 60, InstrumentA)));
        byte[] frameNormal = rendererNormal.RenderFrame(FrameForSample(5100));

        int scopeTopY = scope.Y;
        int overlayBright = CountBrightInRow(frameOverlay, renderer.Width, scopeTopY, scope.X, scope.Right);
        int normalBright = CountBrightInRow(frameNormal, renderer.Width, scopeTopY, scope.X, scope.Right);
        Assert.True(overlayBright <= normalBright + 2,
            $"Instrument-change overlay must not cover scope: overlay={overlayBright}, normal={normalBright} bright pixels in scope top row.");
    }

    // ---- §13.4 FM3 operator reduced opacity ----

    [Fact]
    public void Fm3OperatorRibbons_AreDimmerThanMainRibbon()
    {
        // Create a timeline with FM3 main + operator notes.
        var timeline = new VisualizationTimeline
        {
            SampleRate = SampleRate,
            StartSample = 0,
            EndSample = 100_000,
            Instruments = [FmInstrument(InstrumentA, 4)],
            Notes =
            [
                new NoteEvent("ym2608.0.fm.3", 1200, 3800, 392.0, 67, InstrumentA, VisualizationNoteMode.Fm, false, Array.Empty<PitchChange>()),
                new NoteEvent("ym2608.0.fm3.op.1", 1300, 3000, 196.0, 55, InstrumentA, VisualizationNoteMode.Fm3Operator, false, Array.Empty<PitchChange>()),
            ],
            Rhythm = Array.Empty<RhythmEvent>(),
        };
        var renderer = Renderer(timeline);
        byte[] frame = renderer.RenderFrame(FrameForSample(2000));

        OverlayRect mainLane = renderer.Layout.GetPitchedLaneRect(2, true);
        OverlayRect opRibbons = renderer.Layout.GetFm3OperatorRect(2);

        int mainBrightness = MaxBrightnessInRect(frame, renderer.Width, mainLane);
        int opBrightness = MaxBrightnessInRect(frame, renderer.Width, opRibbons);

        Assert.True(mainBrightness > opBrightness,
            $"FM3 main ribbon should be brighter than operator ribbons: main={mainBrightness}, op={opBrightness}.");
    }

    // ---- preparation-time detection ----

    [Fact]
    public void PreparedNote_HasInstrumentChangeFlag()
    {
        var layout = new OverlayLayout(960, 540, 0.75, 2.25);
        var timeline = Timeline(
            Note(500, 5000, 60, InstrumentA),
            Note(5000, 9000, 64, InstrumentB));
        var scene = OverlaySceneBuilder.Build(timeline, layout, samplesPerFrame: 50);

        var fm1Notes = scene.Panels[0].MainNotes;
        Assert.Equal(2, fm1Notes.Length);
        Assert.False(fm1Notes[0].HasInstrumentChange, "First note should not have a change.");
        Assert.True(fm1Notes[1].HasInstrumentChange, "Second note should have a change.");
        Assert.Equal(5000, fm1Notes[1].InstrumentChangeSample);
    }

    [Fact]
    public void PreparedNote_NoChangeFlagWhenInstrumentStable()
    {
        var layout = new OverlayLayout(960, 540, 0.75, 2.25);
        var timeline = Timeline(
            Note(500, 5000, 60, InstrumentA),
            Note(5000, 9000, 64, InstrumentA));
        var scene = OverlaySceneBuilder.Build(timeline, layout, samplesPerFrame: 50);

        foreach (var note in scene.Panels[0].MainNotes)
            Assert.False(note.HasInstrumentChange, "No change when instrument is stable.");
    }

    // ---- helpers ----

    private static int CountBrightPixels(byte[] frame, int width, OverlayRect rect)
    {
        int count = 0;
        for (int y = rect.Y; y < rect.Bottom; y++)
            for (int x = rect.X; x < rect.Right; x++)
            {
                int offset = (y * width + x) * 4;
                int brightness = frame[offset] + frame[offset + 1] + frame[offset + 2];
                if (brightness > 300)
                    count++;
            }
        return count;
    }

    private static int MaxBrightnessInRect(byte[] frame, int width, OverlayRect rect)
    {
        int max = 0;
        for (int y = rect.Y; y < rect.Bottom; y++)
            for (int x = rect.X; x < rect.Right; x++)
            {
                int offset = (y * width + x) * 4;
                if (frame[offset + 3] == 0)
                    continue;
                int brightness = frame[offset] + frame[offset + 1] + frame[offset + 2];
                if (brightness > max)
                    max = brightness;
            }
        return max;
    }

    private static int CountBrightInRow(byte[] frame, int width, int y, int xStart, int xEnd)
    {
        int count = 0;
        for (int x = xStart; x < xEnd; x++)
        {
            int offset = (y * width + x) * 4;
            int brightness = frame[offset] + frame[offset + 1] + frame[offset + 2];
            if (brightness > 300)
                count++;
        }
        return count;
    }
}
