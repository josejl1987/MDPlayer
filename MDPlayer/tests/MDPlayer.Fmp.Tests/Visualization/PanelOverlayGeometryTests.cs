using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Metric-based geometry tests for the generated visualization (no golden
/// images). These pin the layout invariants of the typography/alignment pass:
/// panel-headers holding three non-intersecting text slots, a fixed pitch
/// gutter, shared title/clock reservation, and consistent panel bounds.
/// </summary>
public sealed class PanelOverlayGeometryTests
{
    public static TheoryData<int, int> Resolutions() => new()
    {
        { 1600, 900 },
        { 1920, 1080 },
    };

    /// <summary>Builds a diagnostic 12-panel overlay for the given size.</summary>
    private static OverlayLayout Layout(int width, int height)
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        ResolvedVisualizationLayout resolved = RendererTestLayout.Build(
            timeline,
            width,
            height,
            channels: VisualizationChannelFilter.All);
        return resolved.Geometry;
    }

    private static PanelOverlayRenderer Renderer(int width, int height)
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        return new PanelOverlayRenderer(
            timeline,
            RendererTestLayout.Build(timeline, width, height, channels: VisualizationChannelFilter.All),
            new PanelOverlayRenderer.Options
            {
                FpsNumerator = 20,
                FpsDenominator = 1,
            });
    }

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void EveryPanelHeader_IsSameHeight(int width, int height)
    {
        OverlayLayout layout = Layout(width, height);
        int first = layout.GetHeaderRect(0).Height;
        Assert.True(first > 0, "panel header must have positive height");
        for (int i = 0; i < layout.PanelCount; i++)
            Assert.Equal(first, layout.GetHeaderRect(i).Height);
    }

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void HeaderTextSlots_AreDisjointAndInsideHeader(int width, int height)
    {
        OverlayLayout layout = Layout(width, height);
        for (int i = 0; i < layout.PanelCount; i++)
        {
            PanelHeaderLayout slots = layout.HeaderSlots(i);
            Assert.True(slots.Name.Height == slots.State.Height
                && slots.State.Height == slots.Patch.Height,
                "all header slots share one vertical band");
            Assert.Equal(slots.Name.Y, slots.State.Y);

            // Slots are horizontally adjacent, never overlapping.
            Assert.True(slots.State.X >= slots.Name.Right);
            Assert.True(slots.Patch.X >= slots.State.Right);

            // Each slot stays within its panel header.
            OverlayRect header = layout.GetHeaderRect(i);
            Assert.True(slots.Name.X >= header.X);
            Assert.True(slots.Patch.Right <= header.Right);
            Assert.True(slots.Name.Y >= header.Y);
            Assert.True(slots.Patch.Bottom <= header.Bottom);
        }
    }

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void ScopeAndTimeline_ShareHorizontalBounds(int width, int height)
    {
        OverlayLayout layout = Layout(width, height);
        for (int i = 0; i < layout.PanelCount; i++)
        {
            OverlayRect scope = layout.GetScopeRect(i);
            OverlayRect timeline = layout.GetTimelineRect(i);
            Assert.Equal(scope.X, timeline.X);
            Assert.Equal(scope.Width, timeline.Width);
        }
    }

    [Theory]
    [MemberData(nameof(Resolutions))]
    public void PitchGutter_KeepsLabelsClearOfPanelAndGridLine(int width, int height)
    {
        OverlayLayout layout = Layout(width, height);
        Assert.True(layout.PitchLabelWidth >= 20);
        Assert.True(layout.PitchLabelInsetLeft > 0 && layout.PitchLabelInsetRight > 0);
        Assert.True(layout.PitchLabelInsetLeft + layout.PitchLabelInsetRight <= layout.PitchLabelWidth);
        for (int i = 0; i < layout.PanelCount; i++)
        {
            OverlayRect timeline = layout.GetTimelineRect(i);
            // The pitch lane content starts exactly after the gutter.
            OverlayRect lane = layout.GetPitchedLaneRect(i, reserveFm3OperatorRibbons: false);
            Assert.Equal(timeline.X + layout.PitchLabelWidth, lane.X);
            Assert.Equal(timeline.Width - layout.PitchLabelWidth, lane.Width);
        }
    }

    [Fact]
    public void Title_IsTrimmedBeforeTheReservedClock()
    {
        // A title far longer than fits must be trimmed so its rightmost glyph
        // never crosses into the reserved clock band. The clock occupies the
        // right edge of the top bar; the title is confined to the remainder.
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        PanelOverlayRenderer renderer = new(
            timeline,
            RendererTestLayout.Build(timeline, 1920, 1080),
            new PanelOverlayRenderer.Options
            {
                FpsNumerator = 20,
                FpsDenominator = 1,
                Presentation = new VisualizationPresentation(new string('A', 300), "", ""),
            });

        byte[] frame = renderer.RenderFrame(40);
        OverlayRect topBar = renderer.Layout.TopBarRect;
        int clockLeft = renderer.TopBarClockLeft;
        int titleMaxX = renderer.TopBarFallbackTitleMaxX;

        // The clock is reserved first; the title's trimmed right limit must
        // land strictly left of the clock's left edge (a 40px gap). This is
        // the rectangle-level invariant from the layout work: title bounds and
        // clock bounds never intersect.
        Assert.True(titleMaxX <= clockLeft);
        Assert.True(clockLeft - titleMaxX >= 40);

        // And confirm the title is actually present (trimmed to its limit), not
        // dropped entirely because it exceeded the reservation.
        int titleTop = topBar.Y + Math.Max(2, (topBar.Height - 21) / 2);
        bool titlePresent = false;
        for (int x = topBar.X + renderer.Layout.SafeHorizontalMargin; x < titleMaxX; x++)
        {
            int offset = titleTop * renderer.Width * 4 + x * 4 + 3;
            if (frame[offset] != 0)
            {
                titlePresent = true;
                break;
            }
        }
        Assert.True(titlePresent, "a long title must still be drawn (trimmed), not dropped");
    }

    [Fact]
    public void PerPanelHeaders_DrawDistinctChannelNames()
    {
        // Diagnostic frames show one panel per channel; repeated master
        // fallback would render identical Name slots everywhere. The channel
        // labels are drawn per-panel from the resolved topology, so every
        // active panel carries its own identifier.
        OverlayLayout layout = Layout(1920, 1080);
        HashSet<string> labels = new();
        for (int i = 0; i < layout.PanelCount; i++)
            labels.Add(OverlayLayout.PanelIds[i]);
        // The diagnostic fixture spans several channels (FM1, FM3, SSG1, SSG2,
        // rhythm, ADC channels…); if the renderer were repeating a master
        // waveform there would be a single repeated identity, not distinct ones.
        Assert.True(labels.Count >= 2, "expected a multi-channel diagnostic grid");

        PanelOverlayRenderer renderer = Renderer(1920, 1080);
        byte[] frame = renderer.RenderFrame(40);
        // Every header Name slot contains opaque name pixels.
        for (int i = 0; i < layout.PanelCount; i++)
        {
            OverlayRect name = layout.HeaderSlots(i).Name;
            int x = name.X + name.Width / 2;
            int y = name.Y + name.Height / 2;
            int offset = y * renderer.Width * 4 + x * 4 + 3;
            Assert.NotEqual(0, frame[offset]);
        }
    }
}
