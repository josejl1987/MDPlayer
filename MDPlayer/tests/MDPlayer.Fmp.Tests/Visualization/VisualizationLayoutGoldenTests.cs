using System.Security.Cryptography;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization;

public sealed class VisualizationLayoutGoldenTests
{
    private static readonly (VisualizationLayoutMode Mode, string Hash)[] Goldens =
    [
        (VisualizationLayoutMode.UnifiedRoll, "10CE127116981A43"),
        (VisualizationLayoutMode.SplitRoll, "FD4130AB2FBD0832"),
        (VisualizationLayoutMode.Scope, "CF8009CFEA696B4F"),
        (VisualizationLayoutMode.Hybrid, "0D89EEFD60096825"),
        (VisualizationLayoutMode.Diagnostic, "4DDD185242FC6351"),
        (VisualizationLayoutMode.DiagnosticV2, "BD45C6223CEDAE57"),
    ];

    [Theory]
    [InlineData("UnifiedRoll")]
    [InlineData("SplitRoll")]
    [InlineData("Scope")]
    [InlineData("Hybrid")]
    [InlineData("Diagnostic")]
    [InlineData("DiagnosticV2")]
    public void LayoutGolden_IsDeterministic(string modeName)
    {
        VisualizationLayoutMode mode = Enum.Parse<VisualizationLayoutMode>(modeName);
        var renderer = new PanelOverlayRenderer(
            VisualizationTimelineFixture.Create(),
            new PanelOverlayRenderer.Options
            {
                Width = 960,
                Height = 540,
                FpsNumerator = 30,
                FpsDenominator = 1,
                LayoutMode = mode,
                Channels = VisualizationChannelFilter.All,
                Effects = EffectsMode.Minimal,
            });

        byte[] scope = new byte[renderer.ScopeFrameByteCount];
        for (int index = 0; index < scope.Length; index++)
            scope[index] = (byte)((index * 31 + (int)mode * 17) % 251);

        byte[] frame = new byte[renderer.FrameByteCount];
        renderer.RenderCompositeFrame(45, scope, frame);
        string actual = Convert.ToHexString(SHA256.HashData(frame))[..16];
        string expected = Goldens.Single(golden => golden.Mode == mode).Hash;
        Assert.Equal(expected, actual);
    }
}
