namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Metadata shown in the dedicated top and bottom bands of the visualization.
/// All fields default to empty so the renderer can operate without any
/// presentation metadata.
/// </summary>
internal sealed record VisualizationPresentation(string Title, string Subtitle, string Credits)
{
    public static VisualizationPresentation Empty { get; } = new(string.Empty, string.Empty, string.Empty);
}
