namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Renderer composition. The publishing surface exposes three intentional
/// compositions — Performance (unified roll), ScopeStage (scope wall) and
/// Diagnostic (full channel grid). The remaining values are legacy modes kept
/// for CLI/GUI back-compatibility; they resolve to the same rendering paths
/// as the three canonical compositions.
/// </summary>
internal enum VisualizationLayoutMode
{
    Diagnostic,
    Focus,
    Scope,
    SplitRoll,
    UnifiedRoll,
    Hybrid,
    Auto,
    DiagnosticV2,
    /// <summary>Canonical publishing composition: one dominant unified roll,
    /// compact unpitched lanes, and an optional compact scope strip.</summary>
    Performance,
    /// <summary>Canonical publishing composition: a large scope mosaic with a
    /// compact synchronized activity strip below the scopes.</summary>
    ScopeStage,
}
