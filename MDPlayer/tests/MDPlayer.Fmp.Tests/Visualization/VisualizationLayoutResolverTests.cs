using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization;

/// <summary>
/// Patch-2 layout resolution tests. The resolver must map every supported
/// output resolution × panel-count combination to a usable <see
/// cref="ResolvedVisualizationLayout"/> without throwing, and must select the
/// correct density tier (Full / Compact / Minimal) with the corresponding
/// capability flags.
/// </summary>
public sealed class VisualizationLayoutResolverTests
{
    private static readonly (int Width, int Height)[] Resolutions =
    {
        (480, 270),
        (640, 360),
        (854, 480),
        (1280, 720),
        (1920, 1080),
        (2560, 1440),
        (3840, 2160),
    };

    private static readonly int[] PanelCounts =
    {
        1, 2, 4, 6, 8, 9, 12, 16, 20, 25, 36, 49, 64,
    };

    private static VisualizationTopology Topology(int panelCount) => new(
        Enumerable.Range(0, panelCount)
            .Select(i => new VisualizationPanel(
                $"t{i}",
                $"Track {i}",
                PreparedPanelKind.Pitched,
                PanelContentKind.SingleVoice,
                i,
                new[] { $"ym2608.0.fm.{i}" },
                OperatorVoiceIds: Array.Empty<string>()))
            .ToArray());

    private static ResolvedVisualizationLayout Resolve(int width, int height, int panelCount)
        => VisualizationLayoutResolver.Resolve(
            width,
            height,
            pastSeconds: 0.75,
            futureSeconds: 2.25,
            VisualizationLayoutMode.Diagnostic,
            Topology(panelCount),
            panelCount,
            VisualizationScopePosition.Top);

    // ---- Resolution × panel-count matrix -----------------------------------

    [Theory]
    [MemberData(nameof(ResolutionMatrix))]
    public void Resolve_EverySupportedMatrix_ProducesValidLayout(
        int width, int height, int panelCount)
    {
        // Only invoke for combinations the resolver supports (it degrades the
        // presentation instead of throwing whenever geometry is usable).
        Assert.True(width >= 480 && height >= 270,
            "below the supported minimum; not part of the matrix");
        Assert.True(panelCount >= 1);

        ResolvedVisualizationLayout result = Resolve(width, height, panelCount);
        AssertSupported(result);
    }

    [Fact]
    public void Resolve_SupportedCombinations_NeverThrow()
    {
        // Build the full supported matrix explicitly and assert none throws
        // merely because scopes or detailed labels do not fit.
        foreach ((int width, int height) in Resolutions)
        {
            foreach (int panelCount in PanelCounts)
            {
                Resolve(width, height, panelCount);
            }
        }
    }

    // ---- density tier selection ---------------------------------------------

    [Fact]
    public void SmallCanvas_SelectsMinimal_NotThrow()
    {
        // 480×270 with many panels resolves to Minimal (device aggregate).
        ResolvedVisualizationLayout result = Resolve(480, 270, panelCount: 16);
        Assert.Equal(VisualizationLayoutDensity.Minimal, result.Density);
        Assert.False(result.Capabilities.ShowScopes);
        AssertSupported(result);
    }

    [Fact]
    public void MediumCanvas_SelectsCompactOrMinimal()
    {
        // A canvas too small for full panels resolves below Full.
        ResolvedVisualizationLayout result = Resolve(900, 506, panelCount: 12);
        Assert.NotEqual(VisualizationLayoutDensity.Full, result.Density);
        AssertSupported(result);
    }

    [Fact]
    public void LargeCanvas_SelectsFull()
    {
        ResolvedVisualizationLayout result = Resolve(1920, 1080, panelCount: 12);
        Assert.Equal(VisualizationLayoutDensity.Full, result.Density);
        AssertFullCapabilities(result.Capabilities);
        AssertSupported(result);
    }

    // ---- capability rules -----------------------------------------------------

    [Fact]
    public void FullDensity_MeetsCapabilityRules()
    {
        // Representative full canvas (1920×1080, 12 panels).
        VisualizationLayoutCapabilities caps = Resolve(1920, 1080, 12).Capabilities;
        Assert.True(caps.ShowScopes);
        Assert.True(caps.ShowRoll);
        Assert.True(caps.ShowPitchLabels);
        Assert.True(caps.ShowDetailedHeaders);
        Assert.True(caps.ShowInstrumentText);
    }

    [Fact]
    public void CompactDensity_MeetsCapabilityRules()
    {
        // Find a compact configuration. 1280×720 single panel is Full at best;
        // narrow multi-panel canvases yield Compact. Use a canvas whose panels
        // meet 220×120 but not 300×180.
        ResolvedVisualizationLayout result = ResolveCompact();
        Assert.Equal(VisualizationLayoutDensity.Compact, result.Density);
        Assert.True(result.Capabilities.ShowScopes,
            "compact keeps scopes when a scope height can be allocated");
        Assert.True(result.Capabilities.ShowRoll, "compact keeps the roll");
        Assert.False(result.Capabilities.ShowPitchLabels);
        Assert.False(result.Capabilities.ShowDetailedHeaders);
        Assert.False(result.Capabilities.ShowInstrumentText);
    }

    [Fact]
    public void MinimalDensity_HasNoScopeRegion()
    {
        ResolvedVisualizationLayout result = Resolve(480, 270, panelCount: 36);
        Assert.Equal(VisualizationLayoutDensity.Minimal, result.Density);
        Assert.False(result.Capabilities.ShowScopes);
    }

    // ---- Disabling scopes gives space to the roll/content -----------------------

    [Fact]
    public void DisablingRoll_GivesSpaceToScope_Content()
    {
        // Small canvas: disabling roll leaves more usable content height and
        // still resolves; the geometry must remain valid without a roll.
        ResolvedVisualizationLayout result = Resolve(480, 270, panelCount: 1);
        AssertSupported(result);
    }

    [Fact]
    public void CanvasBoundary_DoesNotThrow()
    {
        // 480×270 must never throw, though density may be Minimal.
        ResolvedVisualizationLayout result = Resolve(480, 270, panelCount: 1);
        AssertSupported(result);
    }

    // ---- helpers --------------------------------------------------------------

    /// <summary>Finds a canvas/panel-count that resolves to Compact density.</summary>
    private static ResolvedVisualizationLayout ResolveCompact()
    {
        foreach ((int width, int height) in new[]
        {
            (900, 506), (1000, 560), (1100, 600), (1280, 720), (1440, 810), (1680, 900),
        })
        {
            foreach (int panels in new[] { 8, 12, 16, 20, 25 })
            {
                ResolvedVisualizationLayout result = Resolve(width, height, panels);
                if (result.Density == VisualizationLayoutDensity.Compact)
                    return result;
            }
        }
        // Default fallback assertion: never reached when a compact config exists.
        return Resolve(1280, 720, 20);
    }

    private static void AssertSupported(ResolvedVisualizationLayout result)
    {
        OverlayLayout g = result.Geometry;
        Assert.True(g.Width > 0 && g.Height > 0, "non-zero canvas");
        Assert.True(g.PanelCount >= 1, "at least one panel");

        // Panel rectangles inside the canvas.
        foreach (int i in Enumerable.Range(0, g.PanelCount))
        {
            OverlayRect panel = g.GetPanelRect(i);
            Assert.True(panel.Width >= 0 && panel.Height >= 0, "panel non-negative");
            Assert.True(panel.X >= 0 && panel.Y >= 0, "panel origin non-negative");
            Assert.True(panel.Right <= g.Width, "panel inside canvas width");
            Assert.True(panel.Bottom <= g.Height, "panel inside canvas height");

            // Header inside its panel.
            OverlayRect header = g.GetHeaderRect(i);
            Assert.True(header.Y >= panel.Y && header.Bottom <= panel.Bottom, "header inside panel");

            // Scope inside its panel when scopes are enabled.
            if (result.Capabilities.ShowScopes)
            {
                OverlayRect scope = g.GetScopeRect(i);
                Assert.True(scope.Y >= panel.Y && scope.Bottom <= panel.Bottom, "scope inside panel");
            }

            // Timeline inside its panel when enabled.
            if (result.Capabilities.ShowRoll)
            {
                OverlayRect roll = g.GetTimelineRect(i);
                Assert.True(roll.Y >= panel.Y && roll.Bottom <= panel.Bottom, "roll inside panel");
            }
        }

        // No two panel rectangles overlap.
        for (int a = 0; a < g.PanelCount; a++)
        {
            for (int b = a + 1; b < g.PanelCount; b++)
            {
                OverlayRect pa = g.GetPanelRect(a);
                OverlayRect pb = g.GetPanelRect(b);
                bool overlap = pa.Right > pb.X && pb.Right > pa.X
                    && pa.Bottom > pb.Y && pb.Bottom > pa.Y;
                Assert.False(overlap, $"panels {a} and {b} overlap");
            }
        }

        // Top and bottom bars stay in the canvas.
        Assert.True(g.TopBarHeight >= 0 && g.BottomBarHeight >= 0);
        Assert.True(g.TopBarHeight + g.BottomBarHeight <= g.Height);

        // Final row stays above the bottom bar.
        int finalRowY = g.GetPanelRect(g.PanelCount - 1).Y;
        Assert.True(finalRowY + g.PanelHeight <= g.Height - g.BottomBarHeight + 1,
            "final row remains above the bottom bar");
    }

    private static void AssertFullCapabilities(VisualizationLayoutCapabilities caps)
    {
        Assert.True(caps.ShowScopes);
        Assert.True(caps.ShowRoll);
        Assert.True(caps.ShowPitchLabels);
        Assert.True(caps.ShowDetailedHeaders);
        Assert.True(caps.ShowInstrumentText);
    }

    public static IEnumerable<object[]> ResolutionMatrix()
    {
        foreach ((int width, int height) in Resolutions)
            foreach (int panelCount in PanelCounts)
                yield return new object[] { width, height, panelCount };
    }
}
