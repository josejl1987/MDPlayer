using Fmp.Application.Contracts;
using Fmp.Application.Presets;
using Xunit;

namespace Fmp.Application.Tests;

/// <summary>
/// Render-quality catalog tests (replaces the deleted VisualizationPresetCatalog
/// tests). Quality selects resolution/fps — never the composition.
/// </summary>
public class RenderQualityCatalogTests
{
    [Theory]
    [InlineData(RenderQuality.Draft, 1280, 720, 30, 1)]
    [InlineData(RenderQuality.Standard, 1920, 1080, 60, 1)]
    [InlineData(RenderQuality.Final, 1920, 1080, 60, 1)]
    public void Get_ResolvesSpecValues(RenderQuality quality, int width, int height, int fpsNumerator, int fpsDenominator)
    {
        RenderQualityDefinition definition = RenderQualityCatalog.Get(quality);
        Assert.Equal(quality, definition.Quality);
        Assert.Equal(width, definition.DefaultWidth);
        Assert.Equal(height, definition.DefaultHeight);
        Assert.Equal(fpsNumerator, definition.DefaultFpsNumerator);
        Assert.Equal(fpsDenominator, definition.DefaultFpsDenominator);
        Assert.False(string.IsNullOrEmpty(definition.Description));
        Assert.Equal(quality.ToString(), definition.DisplayName);
    }

    [Fact]
    public void All_ContainsExactlyTheThreeQualities()
    {
        Assert.Equal(
            new[] { RenderQuality.Draft, RenderQuality.Standard, RenderQuality.Final },
            RenderQualityCatalog.All.Select(definition => definition.Quality));
        // There is no "Diagnostic" render quality — quality never selects composition.
        Assert.DoesNotContain(
            RenderQualityCatalog.All,
            definition => definition.Quality.ToString().Contains("Diagnostic", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplyDefaults_Draft_SetsOutputFields_AndKeepsPaths()
    {
        VisualizationRequest request = TestRequests.Valid();
        VisualizationRequest applied = RenderQualityCatalog.ApplyDefaults(request, RenderQuality.Draft);

        Assert.Equal(RenderQuality.Draft, applied.Output.Quality);
        Assert.Equal(1280, applied.Output.Width);
        Assert.Equal(720, applied.Output.Height);
        Assert.Equal(30, applied.Output.FpsNumerator);
        Assert.Equal(1, applied.Output.FpsDenominator);
        // Paths survive.
        Assert.Equal(request.InputPath, applied.InputPath);
        Assert.Equal(request.OutputPath, applied.OutputPath);
    }

    [Fact]
    public void ApplyDefaults_StandardAndFinal_ShareFullHdProfile()
    {
        VisualizationRequest standard = RenderQualityCatalog.ApplyDefaults(TestRequests.Valid(), RenderQuality.Standard);
        VisualizationRequest final = RenderQualityCatalog.ApplyDefaults(TestRequests.Valid(), RenderQuality.Final);

        foreach (VisualizationRequest applied in new[] { standard, final })
        {
            Assert.Equal(1920, applied.Output.Width);
            Assert.Equal(1080, applied.Output.Height);
            Assert.Equal(60, applied.Output.FpsNumerator);
            Assert.Equal(1, applied.Output.FpsDenominator);
        }
        Assert.Equal(RenderQuality.Standard, standard.Output.Quality);
        Assert.Equal(RenderQuality.Final, final.Output.Quality);
    }
}
