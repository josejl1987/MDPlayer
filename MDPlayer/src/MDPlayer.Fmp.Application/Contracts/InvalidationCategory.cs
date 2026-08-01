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
            nameof(VisualizationRequest.Title)
            or nameof(VisualizationRequest.Subtitle)
            or nameof(VisualizationRequest.Credits)
            or nameof(VisualizationRequest.FontPath)
            or nameof(VisualizationRequest.Effects)
            or nameof(VisualizationRequest.NoteColor)
            => InvalidationCategory.PresentationOnly,

            nameof(VisualizationRequest.Layout)
            or nameof(VisualizationRequest.Width)
            or nameof(VisualizationRequest.Height)
            or nameof(VisualizationRequest.FpsNumerator)
            or nameof(VisualizationRequest.FpsDenominator)
            or nameof(VisualizationRequest.ChannelSelection)
            or nameof(VisualizationRequest.IncludedTrackIds)
            or nameof(VisualizationRequest.ExcludedTrackIds)
            or nameof(VisualizationRequest.PastSeconds)
            or nameof(VisualizationRequest.FutureSeconds)
            or nameof(VisualizationRequest.ScopeRatio)
            or nameof(VisualizationRequest.ScopePosition)
            or nameof(VisualizationRequest.Grouping)
            or nameof(VisualizationRequest.TimeGrid)
            or nameof(VisualizationRequest.RollZoom)
            => InvalidationCategory.LayoutAffecting,

            nameof(VisualizationRequest.AnalysisEnabled)
            or nameof(VisualizationRequest.AnalysisDetail)
            or nameof(VisualizationRequest.AnalysisOverlay)
            or nameof(VisualizationRequest.Tools.AnalysisForce)
            => InvalidationCategory.AnalysisAffecting,

            nameof(VisualizationRequest.LoopCount)
            or nameof(VisualizationRequest.FadeSeconds)
            or nameof(VisualizationRequest.TailSeconds)
            or nameof(VisualizationRequest.MaximumDurationSeconds)
            or nameof(VisualizationRequest.SampleRate)
            or nameof(VisualizationRequest.TimeoutSeconds)
            or nameof(VisualizationRequest.Backend)
            => InvalidationCategory.CaptureAffecting,

            nameof(VisualizationRequest.OutputPath)
            or nameof(VisualizationRequest.Encoder)
            or nameof(VisualizationRequest.Overwrite)
            or nameof(VisualizationRequest.FinalQuality)
            or nameof(VisualizationRequest.StemsOnly)
            => InvalidationCategory.ExportOnly,

            _ => InvalidationCategory.LayoutAffecting,
        };
    }
}
