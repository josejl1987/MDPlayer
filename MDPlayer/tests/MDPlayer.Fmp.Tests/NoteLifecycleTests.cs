using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// PR 3: note lifecycle (§8.5, §8.6) and contact rail (§10.2), pixel-exact.
public sealed class NoteLifecycleTests
{
    private const int SampleRate = 1000;
    private const string Fm1 = "ym2608.0.fm.1";
    private const string InstrumentA = "ym2608:aaaaaa1111111111";
    private const string InstrumentB = "ym2608:bbbbbb2222222222";

    private static NoteEvent Note(long start, long end, double midi, bool retrigger = false, string instrument = InstrumentA)
        => new(Fm1, start, end, 440.0, midi, instrument, VisualizationNoteMode.Fm, retrigger, Array.Empty<PitchChange>());

    private static VisualizationTimeline Timeline(params NoteEvent[] notes)
        => new()
        {
            SampleRate = SampleRate,
            EndSample = 100_000,
            Instruments =
            [
                new InstrumentDefinition(InstrumentA, "fm", 4, 3, 0, 2, Array.Empty<FmOperatorDefinition>()),
                new InstrumentDefinition(InstrumentB, "fm", 2, 3, 0, 2, Array.Empty<FmOperatorDefinition>()),
            ],
            Notes = notes,
        };

    private static PanelOverlayRenderer Renderer(VisualizationTimeline timeline)
        => new(timeline, RendererTestLayout.Build(timeline), new PanelOverlayRenderer.Options
        {
            FpsNumerator = 20,
            FpsDenominator = 1,
        });

    private static OverlayRect Lane(PanelOverlayRenderer renderer) => renderer.Layout.GetPitchedLaneRect(0, false);

    private static int X(PanelOverlayRenderer renderer, long frameIndex, long sample)
    {
        long currentSample = OverlayLayout.FrameToSample(frameIndex, SampleRate, 20, 1);
        return (int)Math.Round(renderer.Layout.SampleToX(sample, currentSample, SampleRate, Lane(renderer)));
    }

    [Fact]
    public void OnsetCap_IsBrighterThanBody_AndEnlargedWithinFirst110ms()
    {
        // Note is 100 ms old at frame 32 → enlarged cap.
        var renderer = Renderer(Timeline(Note(1500, 4500, 60)));
        byte[] frame = renderer.RenderFrame(32); // sample 1600, window 850..3850
        OverlayRect lane = Lane(renderer);

        // PR 4 active flash (§9.1): at 100 ms old the note is within the
        // 120 ms flash window, so the body fill is lightened beyond the
        // steady +0.14 active step (cubic ease-out decay).
        OverlayColor baseFill = InstrumentColorResolver.ResolveInstrumentFill(InstrumentA);
        OverlayColor capFill = baseFill.Lighten(0.45);

        int capX = X(renderer, 32, 1500); // 85
        Assert.True(ContainsColorInColumn(frame, renderer.Width, capX + 1, lane.Y, lane.Bottom, capFill),
            $"No enlarged onset cap pixels at column {capX + 1}.");

        // Enlarged cap extends above the ribbon body (cap top < body top).
        // Use a body column well beyond the ripple radius (playhead + max ring)
        // so the expanding ring does not overwrite the body fill being checked.
        int bodyX = capX + 20;
        // Include the accent border when measuring the outer cap extent; the
        // border is intentionally brighter than the cap fill.
        // The diagnostic scope border occupies the lane's first row; exclude
        // that chrome so this assertion measures ribbon geometry only.
        int capMinY = ScanRibbonMinY(frame, renderer.Width, capX + 1, lane.Y + 1, lane.Bottom);
        int bodyMinY = ScanRibbonMinY(frame, renderer.Width, bodyX, lane.Y + 1, lane.Bottom);
        Assert.True(bodyMinY >= 0, "No ribbon body pixels found beside the cap.");
        Assert.True(capMinY < bodyMinY,
            $"Enlarged cap does not extend above the body: capMinY={capMinY}, bodyMinY={bodyMinY}.");
    }

    [Fact]
    public void RetriggerCap_ShowsAccentBarThenBlock()
    {
        // Retriggered note is 100 ms old at frame 94.
        var renderer = Renderer(Timeline(
            Note(1500, 4500, 60),
            Note(4600, 5500, 64, retrigger: true, instrument: InstrumentB)));
        byte[] frame = renderer.RenderFrame(94); // sample 4700, window 3950..5750
        OverlayRect lane = Lane(renderer);

        OverlayColor accent = InstrumentColorResolver.ResolveChannelAccent(Fm1, 0);
        OverlayColor capFillB = InstrumentColorResolver.ResolveInstrumentFill(InstrumentB).Lighten(0.45);

        int capX = X(renderer, 94, 4600); // 85
        Assert.True(ContainsColorInColumn(frame, renderer.Width, capX, lane.Y, lane.Bottom, accent),
            $"Retrigger '│' accent bar missing at column {capX}.");
        // Block's own border covers capX+1; bright interior starts capX+2.
        Assert.True(ContainsColorInColumn(frame, renderer.Width, capX + 2, lane.Y, lane.Bottom, capFillB),
            $"Retrigger cap block missing at column {capX + 2}.");
    }

    [Fact]
    public void HardKeyOff_ShowsFlatEndCap_WithoutTaper()
    {
        // B starts one frame after A ends → A is a hard key-off.
        var renderer = Renderer(Timeline(
            Note(1000, 3000, 60),
            Note(3050, 6000, 64, instrument: InstrumentB)));
        byte[] frame = renderer.RenderFrame(50); // sample 2500, window 1750..4750
        OverlayRect lane = Lane(renderer);

        OverlayColor capFill = InstrumentColorResolver.ResolveInstrumentFill(InstrumentA).Lighten(0.45);

        int endX = X(renderer, 50, 3000);
        Assert.True(ContainsColorInColumn(frame, renderer.Width, endX, lane.Y, lane.Bottom, capFill),
            $"Hard key-off end cap missing at column {endX}.");
        // No taper: a body column just before the cap is still full opacity.
        // endX-2 is always interior (round(rightX) may be the blended edge).
        Assert.True(HasRibbonPixel(frame, renderer.Width, endX - 2, lane.Y, lane.Bottom),
            "Hard key-off ribbon tapers before its end cap.");
    }

    [Fact]
    public void NormalRelease_TapersFinal60ms_NoEndCap()
    {
        // Single note, no successor → normal release (last 60 ms taper).
        var renderer = Renderer(Timeline(Note(1000, 4500, 60)));
        byte[] frame = renderer.RenderFrame(80); // sample 4000, window 3250..4750
        OverlayRect lane = Lane(renderer);

        OverlayColor capFill = InstrumentColorResolver.ResolveInstrumentFill(InstrumentA).Lighten(0.45);

        int bodyX = X(renderer, 80, 4055);
        int endX = X(renderer, 80, 4500);

        Assert.True(HasRibbonPixel(frame, renderer.Width, bodyX, lane.Y, lane.Bottom),
            "Normal-release body should be full opacity before the taper window.");

        // Last 60 ms must not contain full-opacity fill; no end cap either.
        for (int x = endX - 2; x < endX; x++)
            Assert.True(HasRibbonPixel(frame, renderer.Width, x, lane.Y, lane.Bottom),
                $"Ribbon disappeared before the release edge at column {x}.");
        Assert.False(ContainsColorInColumn(frame, renderer.Width, endX, lane.Y, lane.Bottom, capFill),
            "Normal release must not draw an end cap.");
    }

    [Fact]
    public void TimelineClipping_ShowsHalfVisibleEndMarkerAtPanelEdge()
    {
        // Note continues past the window → half-visible marker at the edge.
        var renderer = Renderer(Timeline(Note(1000, 20_000, 60)));
        byte[] frame = renderer.RenderFrame(50); // sample 2500, window 1750..4750
        OverlayRect lane = Lane(renderer);

        OverlayColor capFill = InstrumentColorResolver.ResolveInstrumentFill(InstrumentA).Lighten(0.45);

        int edgeX = lane.Right - 1;
        int fillRow = ScanRibbonMinY(frame, renderer.Width, edgeX - 1, lane.Y, lane.Bottom);
        Assert.True(fillRow >= 0, "Clipped ribbon should be drawn up to the panel edge.");

        ReadPixel(frame, renderer.Width, edgeX, fillRow, out byte r, out byte g, out byte b, out _);
        Assert.False(PixelEquals(frame, renderer.Width, edgeX, fillRow, capFill),
            "Clip marker must be half-visible, not a full-opacity end cap.");
        Assert.True(r + g + b > 150, "Clip marker should remain visible at the edge.");
    }

    [Fact]
    public void ContactTick_ExtendsFromPlayheadIntoActiveRibbon()
    {
        var renderer = Renderer(Timeline(
            Note(1500, 4500, 60),
            Note(4600, 5500, 64, instrument: InstrumentB)));
        byte[] frame = renderer.RenderFrame(40); // sample 2000 — note A active
        OverlayRect lane = Lane(renderer);

        OverlayColor accent = InstrumentColorResolver.ResolveChannelAccent(Fm1, 0);
        OverlayColor markerColor = accent.Lighten(0.5);
        int playheadX = renderer.Layout.GetPlayheadX(0);

        int markerMinY = -1, markerMaxY = -1;
        for (int y = lane.Y; y < lane.Bottom; y++)
        {
            bool rowHasMarker = false;
            for (int dx = -2; dx <= 2; dx++)
            {
                if (PixelEquals(frame, renderer.Width, playheadX + dx, y, markerColor))
                {
                    rowHasMarker = true;
                    break;
                }
            }
            if (rowHasMarker)
            {
                if (markerMinY < 0)
                    markerMinY = y;
                markerMaxY = y;
            }
        }
        Assert.True(markerMinY >= 0, "No active pitch marker found at the playhead.");
        int markerY = (markerMinY + markerMaxY) / 2;
        for (int x = playheadX + 3; x <= playheadX + 6; x++)
            Assert.True(PixelEquals(frame, renderer.Width, x, markerY, accent),
                $"Contact tick missing at ({x}, {markerY}).");
    }

    [Fact]
    public void ReleaseStyle_IsHardWhenSuccessorStartsWithinOneFrame()
    {
        var layout = new OverlayLayout(960, 540, 0.75, 2.25);
        var scene = OverlaySceneBuilder.Build(
            Timeline(Note(1000, 3000, 60), Note(3050, 5000, 64)), layout, samplesPerFrame: 50);

        Assert.Equal(NoteReleaseStyle.HardKeyOff, scene.Panels[0].MainNotes[0].ReleaseStyle);
        Assert.Equal(NoteReleaseStyle.Normal, scene.Panels[0].MainNotes[1].ReleaseStyle);
    }

    [Fact]
    public void ReleaseStyle_NormalWhenGapExceedsOneFrame()
    {
        var layout = new OverlayLayout(960, 540, 0.75, 2.25);
        var scene = OverlaySceneBuilder.Build(
            Timeline(Note(1000, 3000, 60), Note(3100, 5000, 64)), layout, samplesPerFrame: 50);

        Assert.Equal(NoteReleaseStyle.Normal, scene.Panels[0].MainNotes[0].ReleaseStyle);
    }

    private static bool ContainsColorInColumn(byte[] frame, int width, int x, int top, int bottom, OverlayColor color)
    {
        for (int y = top; y < bottom; y++)
            if (PixelEquals(frame, width, x, y, color))
                return true;
        return false;
    }

    private static bool HasRibbonPixel(byte[] frame, int width, int x, int top, int bottom)
        => ScanRibbonMinY(frame, width, x, top, bottom) >= 0;

    private static int ScanRibbonMinY(byte[] frame, int width, int x, int top, int bottom)
    {
        for (int y = top; y < bottom; y++)
        {
            int offset = (y * width + x) * 4;
            int a = frame[offset + 3];
            if (a == 0)
                continue;
            // Skia stores premultiplied pixels; un-premultiply so the
            // brightness gate measures the visible straight tint.
            int UnPremul(int c) => a == 255 ? c : Math.Min(255, (c * 255 + a / 2) / a);
            if (UnPremul(frame[offset]) + UnPremul(frame[offset + 1]) + UnPremul(frame[offset + 2]) > 150)
                return y;
        }
        return -1;
    }

    private static bool PixelEquals(byte[] frame, int width, int x, int y, OverlayColor color)
    {
        ReadPixel(frame, width, x, y, out byte r, out byte g, out byte b, out byte a);
        return r == color.R && g == color.G && b == color.B && a == color.A;
    }

    private static void ReadPixel(byte[] frame, int width, int x, int y, out byte r, out byte g, out byte b, out byte a)
    {
        int offset = (y * width + x) * 4;
        r = frame[offset];
        g = frame[offset + 1];
        b = frame[offset + 2];
        a = frame[offset + 3];
    }
}
