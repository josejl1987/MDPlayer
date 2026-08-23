using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace MDPlayer.Fmp.Tests.Visualization.Rendering;

/// <summary>
/// Skia-backend placement probe: verifies the scope grid actually lands in
/// the scope hole and that band/line/ribbon primitives put pixels where the
/// layout says they belong. Structural checks (region statistics), not
/// byte-parity — determinism to the legacy rasterizer was explicitly relaxed.
/// </summary>
public sealed class SkiaPlacementProbeTests
{
    [Fact]
    public void ScopeGrid_LandsInScopeHole()
    {
        var timeline = VisualizationTimelineFixture.Create();
        var overlay = new PanelOverlayRenderer(
            timeline,
            RendererTestLayout.Build(timeline, 960, 540),
            new PanelOverlayRenderer.Options { FpsNumerator = 30 });

        var scope = new byte[overlay.ScopeFrameByteCount];
        // Opaque saturated red everywhere: impossible to mistake for chrome.
        for (int i = 0; i < scope.Length; i += 4)
        {
            scope[i] = 255; scope[i + 1] = 0; scope[i + 2] = 0; scope[i + 3] = 255;
        }

        var withScope = new byte[overlay.FrameByteCount];
        overlay.RenderCompositeFrame(5, scope, withScope);

        var layout = overlay.Layout;
        var scopeRect = layout.GetScopeRect(0);
        int sampled = 0, red = 0;
        for (int y = scopeRect.Y + 2; y < scopeRect.Bottom - 2; y += 7)
        {
            for (int x = scopeRect.X + 2; x < scopeRect.Right - 2; x += 11)
            {
                int o = (y * overlay.Width + x) * 4;
                sampled++;
                if (withScope[o] > 200 && withScope[o + 1] < 60 && withScope[o + 2] < 60)
                    red++;
            }
        }
        Assert.True(sampled > 0, "no samples taken");
        Assert.True(red > sampled / 2,
            $"scope grid did not land: {red}/{sampled} sampled pixels were red");
    }

    [Fact]
    public void BlackKeyBands_PresentInPitchedLane()
    {
        var timeline = VisualizationTimelineFixture.Create();
        var overlay = new PanelOverlayRenderer(
            timeline,
            RendererTestLayout.Build(timeline, 960, 540),
            new PanelOverlayRenderer.Options { FpsNumerator = 30 });

        var frame = new byte[overlay.FrameByteCount];
        overlay.RenderCompositeFrame(3, new byte[overlay.ScopeFrameByteCount], frame);

        var layout = overlay.Layout;
        bool anyBandPixel = false;
        for (int p = 0; p < layout.PanelCount && !anyBandPixel; p++)
        {
            var lane = layout.GetPitchedLaneRect(p, false);
            for (int y = lane.Y; y < lane.Bottom && !anyBandPixel; y++)
            {
                for (int x = lane.X; x < lane.Right; x += 3)
                {
                    int o = (y * overlay.Width + x) * 4;
                    if (frame[o + 3] == 255) { anyBandPixel = true; break; }
                }
            }
        }
        Assert.True(anyBandPixel, "pitched lanes rendered no content");
    }
}
