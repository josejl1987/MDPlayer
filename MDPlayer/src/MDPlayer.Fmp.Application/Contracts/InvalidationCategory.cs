namespace Fmp.Application.Contracts;

/// <summary>
/// A request's invalidation category: which cached preparation stages a change
/// invalidates (spec §17.3).
/// </summary>
public enum InvalidationCategory
{
    PresentationOnly,
    LayoutAffecting,
    AnalysisAffecting,
    CaptureAffecting,
    ExportOnly,
}

/// <summary>
/// Computes the invalidation category for a request property path. The GUI
/// uses this to decide what must be re-planned/rebuilt after an edit.
/// </summary>
public static class InvalidationCategorizer
{
    public static InvalidationCategory Categorize(string propertyPath)
    {
        ArgumentNullException.ThrowIfNull(propertyPath);
        return propertyPath switch
        {
            nameof(VisualizationRequest.Presentation)
            or nameof(VisualizationRequest.Style)
            or nameof(PresentationSettings.Title)
            or nameof(PresentationSettings.Subtitle)
            or nameof(PresentationSettings.Credits)
            or nameof(PresentationSettings.FontPath)
            or nameof(StyleSettings.Effects)
            or nameof(StyleSettings.NoteColor)
            or nameof(StyleSettings.Palette)
            => InvalidationCategory.PresentationOnly,

            nameof(VisualizationRequest.Composition)
            or nameof(VisualizationRequest.Output)
            or nameof(VisualizationRequest.View)
            or nameof(TrackSettings.Selection)
            or nameof(TrackSettings.IncludedIds)
            or nameof(TrackSettings.ExcludedIds)
            or nameof(ViewSettings.PastSeconds)
            or nameof(ViewSettings.FutureSeconds)
            or nameof(ViewSettings.TimeGrid)
            or nameof(ViewSettings.Structure)
            or nameof(ViewSettings.PerformanceSignalStrip)
            or nameof(OutputSettings.Width)
            or nameof(OutputSettings.Height)
            or nameof(OutputSettings.FpsNumerator)
            or nameof(OutputSettings.FpsDenominator)
            => InvalidationCategory.LayoutAffecting,

            nameof(VisualizationRequest.Playback)
            or nameof(PlaybackSettings.LoopCount)
            or nameof(PlaybackSettings.FadeSeconds)
            or nameof(PlaybackSettings.TailSeconds)
            or nameof(PlaybackSettings.MaximumDurationSeconds)
            or nameof(PlaybackSettings.SampleRate)
            => InvalidationCategory.CaptureAffecting,

            nameof(VisualizationRequest.OutputPath)
            or nameof(OutputSettings.Encoder)
            or nameof(OutputSettings.Overwrite)
            => InvalidationCategory.ExportOnly,

            _ => InvalidationCategory.LayoutAffecting,
        };
    }
}
