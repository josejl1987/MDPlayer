using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using MDPlayer.Fmp.Tests;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace MDPlayer.Fmp.Tests.Visualization.Rendering;

public sealed class ScratchRibbonDiag
{
    private readonly ITestOutputHelper _out;
    public ScratchRibbonDiag(ITestOutputHelper output) => _out = output;

    private static bool Bright(byte[] f, int w, int x, int y)
    {
        int o = (y * w + x) * 4;
        int max = Math.Max(f[o], Math.Max(f[o + 1], f[o + 2]));
        int min = Math.Min(f[o], Math.Min(f[o + 1], f[o + 2]));
        int sum = f[o] + f[o + 1] + f[o + 2];
        return f[o + 3] > 0 && sum > 150 && max - min > 25;
    }

    [Fact]
    public void Di()
    {
        var timeline = VisualizationTimelineFixture.Create();
        var renderer = new PanelOverlayRenderer(
            timeline,
            RendererTestLayout.Build(timeline),
            new PanelOverlayRenderer.Options { FpsNumerator = 20, FpsDenominator = 1 });
        byte[] frame = renderer.RenderFrame(40);
        OverlayRect lane = renderer.Layout.GetPitchedLaneRect(0, false);
        _out.WriteLine($"lane={lane}");

        int playheadX = renderer.Layout.GetPlayheadX(0);
        // Per-column: max RGB sum, count of bright pixels.
        for (int x = lane.X; x < lane.X + (int)(0.65 * lane.Width) + 3; x++)
        {
            int maxSum = 0, bright = 0, midSum = 0;
            int maxY = -1;
            for (int y = lane.Y; y < lane.Bottom; y++)
            {
                int o = (y * renderer.Width + x) * 4;
                int sum = frame[o] + frame[o + 1] + frame[o + 2];
                if (sum > maxSum) { maxSum = sum; maxY = y; }
                if (Bright(frame, renderer.Width, x, y)) bright++;
                if (y == lane.Y + lane.Height / 2) midSum = sum;
            }
            string mark = Math.Abs(x - playheadX) <= 2 ? " [playhead]" : "";
            if (x == 65 || x == 90)
            {
                // Dump the row profile at gap and live columns.
                _out.WriteLine($"== column {x} profile ==");
                for (int y = lane.Y; y < lane.Bottom; y += 2)
                {
                    int o = (y * renderer.Width + x) * 4;
                    _out.WriteLine($"  y={y} rgba={frame[o]},{frame[o+1]},{frame[o+2]},{frame[o+3]} sum={frame[o]+frame[o+1]+frame[o+2]}");
                }
            }
            _out.WriteLine($"x={x} maxSum={maxSum} atY={maxY} bright={bright} midSum={midSum}{mark}");
        }
    }
}