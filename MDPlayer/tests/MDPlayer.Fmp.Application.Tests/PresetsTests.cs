using Fmp.Application.Contracts;
using Fmp.Application.Presets;
using Xunit;

namespace Fmp.Application.Tests;

public class PresetsTests
{
    [Theory]
    [InlineData(VisualizationPreset.Preview, 960, 540, 30, VisualizationLayout.Auto, VisualizationEffects.Minimal, AnalysisOverlayMode.None, ChannelSelectionMode.Active)]
    [InlineData(VisualizationPreset.Balanced, 1280, 720, 60, VisualizationLayout.Auto, VisualizationEffects.Minimal, AnalysisOverlayMode.Minimal, ChannelSelectionMode.Active)]
    [InlineData(VisualizationPreset.Final, 1920, 1080, 60, VisualizationLayout.Auto, VisualizationEffects.Cinematic, AnalysisOverlayMode.Minimal, ChannelSelectionMode.Active)]
    [InlineData(VisualizationPreset.Diagnostic, 1920, 1080, 60, VisualizationLayout.Diagnostic, VisualizationEffects.Diagnostic, AnalysisOverlayMode.Standard, ChannelSelectionMode.All)]
    public void Preset_ResolvesSpecValues(
        VisualizationPreset preset, int width, int height, int fps,
        VisualizationLayout layout, VisualizationEffects effects,
        AnalysisOverlayMode overlay, ChannelSelectionMode channels)
    {
        VisualizationPresetDefinition definition = VisualizationPresetCatalog.Get(preset);
        Assert.Equal(width, definition.Width);
        Assert.Equal(height, definition.Height);
        Assert.Equal(fps, definition.FpsNumerator);
        Assert.Equal(layout, definition.Layout);
        Assert.Equal(effects, definition.Effects);
        Assert.Equal(overlay, definition.AnalysisOverlay);
        Assert.Equal(channels, definition.ChannelSelection);
    }

    [Fact]
    public void Apply_SetsPresetFields_AndKeepsPathsAndTools()
    {
        VisualizationRequest request = TestRequests.Valid();
        VisualizationRequest applied = VisualizationPresetCatalog.Apply(request, VisualizationPreset.Preview);

        Assert.Equal(960, applied.Width);
        Assert.Equal(540, applied.Height);
        Assert.Equal(30, applied.FpsNumerator);
        Assert.Equal(VisualizationLayout.Auto, applied.Layout);
        Assert.Equal(VisualizationEffects.Minimal, applied.Effects);
        Assert.Equal(VisualizationPreset.Preview, applied.Preset);
        // Paths and playback settings survive.
        Assert.Equal(request.InputPath, applied.InputPath);
        Assert.Equal(request.OutputPath, applied.OutputPath);
        Assert.Equal(request.LoopCount, applied.LoopCount);
        Assert.Equal(request.Tools, applied.Tools);
    }

    [Fact]
    public void DetectPreset_MatchesExactRequest_AndNullWhenCustom()
    {
        // The default request equals the Balanced preset exactly.
        Assert.Equal(VisualizationPreset.Balanced, VisualizationPresetCatalog.DetectPreset(TestRequests.Valid()));

        VisualizationRequest custom = TestRequests.Valid() with { Effects = VisualizationEffects.Cinematic };
        Assert.Null(VisualizationPresetCatalog.DetectPreset(custom));
    }

    [Fact]
    public void DetectPreset_MatchesEveryPresetValue()
    {
        foreach (VisualizationPreset preset in Enum.GetValues<VisualizationPreset>())
        {
            VisualizationRequest request = VisualizationPresetCatalog.Apply(TestRequests.Valid(), preset);
            Assert.Equal(preset, VisualizationPresetCatalog.DetectPreset(request));
        }
    }
}
