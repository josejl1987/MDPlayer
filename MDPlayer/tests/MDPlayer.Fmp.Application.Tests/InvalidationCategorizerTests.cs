using Fmp.Application.Contracts;
using Xunit;

namespace Fmp.Application.Tests;

public class InvalidationCategorizerTests
{
    [Theory]
    [InlineData(nameof(VisualizationRequest.Title), InvalidationCategory.PresentationOnly)]
    [InlineData(nameof(VisualizationRequest.Subtitle), InvalidationCategory.PresentationOnly)]
    [InlineData(nameof(VisualizationRequest.Credits), InvalidationCategory.PresentationOnly)]
    [InlineData(nameof(VisualizationRequest.FontPath), InvalidationCategory.PresentationOnly)]
    [InlineData(nameof(VisualizationRequest.Effects), InvalidationCategory.PresentationOnly)]
    [InlineData(nameof(VisualizationRequest.NoteColor), InvalidationCategory.PresentationOnly)]
    [InlineData(nameof(VisualizationRequest.Layout), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(VisualizationRequest.Width), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(VisualizationRequest.Height), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(VisualizationRequest.ChannelSelection), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(VisualizationRequest.IncludedTrackIds), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(VisualizationRequest.PastSeconds), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(VisualizationRequest.ScopeRatio), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(VisualizationRequest.Grouping), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(VisualizationRequest.TimeGrid), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(VisualizationRequest.AnalysisEnabled), InvalidationCategory.AnalysisAffecting)]
    [InlineData(nameof(VisualizationRequest.AnalysisDetail), InvalidationCategory.AnalysisAffecting)]
    [InlineData(nameof(VisualizationRequest.AnalysisOverlay), InvalidationCategory.AnalysisAffecting)]
    [InlineData(nameof(VisualizationRequest.LoopCount), InvalidationCategory.CaptureAffecting)]
    [InlineData(nameof(VisualizationRequest.FadeSeconds), InvalidationCategory.CaptureAffecting)]
    [InlineData(nameof(VisualizationRequest.TailSeconds), InvalidationCategory.CaptureAffecting)]
    [InlineData(nameof(VisualizationRequest.MaximumDurationSeconds), InvalidationCategory.CaptureAffecting)]
    [InlineData(nameof(VisualizationRequest.SampleRate), InvalidationCategory.CaptureAffecting)]
    [InlineData(nameof(VisualizationRequest.OutputPath), InvalidationCategory.ExportOnly)]
    [InlineData(nameof(VisualizationRequest.Encoder), InvalidationCategory.ExportOnly)]
    [InlineData(nameof(VisualizationRequest.Overwrite), InvalidationCategory.ExportOnly)]
    public void Categorize_MapsPropertyToCategory(string property, InvalidationCategory expected)
        => Assert.Equal(expected, InvalidationCategorizer.Categorize(property));

    [Fact]
    public void Categorize_UnknownProperty_DefaultsToLayoutAffecting()
        => Assert.Equal(
            InvalidationCategory.LayoutAffecting,
            InvalidationCategorizer.Categorize("SomeFutureProperty"));
}
