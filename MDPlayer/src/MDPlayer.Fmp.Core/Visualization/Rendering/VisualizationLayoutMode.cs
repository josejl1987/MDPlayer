namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Renderer composition. The publishing surface currently exposes a single
/// composition — Diagnostic (full channel grid). <see cref="Auto"/> resolves
/// to Diagnostic and exists as the default entry point; future layouts can be
/// added back as additional enum values without touching the dispatch surface.
/// </summary>
internal enum VisualizationLayoutMode
{
    Diagnostic,
    Auto,
}
