using Fmp.Application.Contracts;
using Xunit;

namespace Fmp.Application.Tests;

public class InvalidationCategorizerTests
{
    [Theory]
    [InlineData(nameof(VisualizationRequest.Presentation), InvalidationCategory.PresentationOnly)]
    [InlineData(nameof(PresentationSettings.Title), InvalidationCategory.PresentationOnly)]
    [InlineData(nameof(PresentationSettings.Subtitle), InvalidationCategory.PresentationOnly)]
    [InlineData(nameof(PresentationSettings.Credits), InvalidationCategory.PresentationOnly)]
    [InlineData(nameof(PresentationSettings.FontPath), InvalidationCategory.PresentationOnly)]
    [InlineData(nameof(VisualizationRequest.Style), InvalidationCategory.PresentationOnly)]
    [InlineData(nameof(StyleSettings.Effects), InvalidationCategory.PresentationOnly)]
    [InlineData(nameof(StyleSettings.NoteColor), InvalidationCategory.PresentationOnly)]
    [InlineData(nameof(StyleSettings.Palette), InvalidationCategory.PresentationOnly)]

    [InlineData(nameof(VisualizationRequest.Composition), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(VisualizationRequest.Output), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(OutputSettings.Width), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(OutputSettings.Height), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(OutputSettings.FpsNumerator), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(OutputSettings.FpsDenominator), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(TrackSettings.Selection), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(TrackSettings.IncludedIds), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(TrackSettings.ExcludedIds), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(VisualizationRequest.View), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(ViewSettings.PastSeconds), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(ViewSettings.FutureSeconds), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(ViewSettings.TimeGrid), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(ViewSettings.Structure), InvalidationCategory.LayoutAffecting)]
    [InlineData(nameof(ViewSettings.PerformanceSignalStrip), InvalidationCategory.LayoutAffecting)]

    [InlineData(nameof(VisualizationRequest.Playback), InvalidationCategory.CaptureAffecting)]
    [InlineData(nameof(PlaybackSettings.LoopCount), InvalidationCategory.CaptureAffecting)]
    [InlineData(nameof(PlaybackSettings.FadeSeconds), InvalidationCategory.CaptureAffecting)]
    [InlineData(nameof(PlaybackSettings.TailSeconds), InvalidationCategory.CaptureAffecting)]
    [InlineData(nameof(PlaybackSettings.MaximumDurationSeconds), InvalidationCategory.CaptureAffecting)]
    [InlineData(nameof(PlaybackSettings.SampleRate), InvalidationCategory.CaptureAffecting)]

    [InlineData(nameof(VisualizationRequest.OutputPath), InvalidationCategory.ExportOnly)]
    [InlineData(nameof(OutputSettings.Encoder), InvalidationCategory.ExportOnly)]
    [InlineData(nameof(OutputSettings.Overwrite), InvalidationCategory.ExportOnly)]
    public void Categorize_MapsPropertyToCategory(string property, InvalidationCategory expected)
        => Assert.Equal(expected, InvalidationCategorizer.Categorize(property));

    [Fact]
    public void Categorize_UnknownProperty_DefaultsToLayoutAffecting()
        => Assert.Equal(
            InvalidationCategory.LayoutAffecting,
            InvalidationCategorizer.Categorize("SomeFutureProperty"));
}
