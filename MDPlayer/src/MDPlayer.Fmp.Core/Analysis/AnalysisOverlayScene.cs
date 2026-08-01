using Fmp.Core.Visualization;

namespace Fmp.Core.Analysis;

internal enum AnalysisMarkerKind
{
    Section,
    Phrase,
    Motif,
}

internal sealed record AnalysisSectionMarker(long Sample, string Label);

internal sealed record AnalysisHarmonyMarker(long StartSample, long EndSample, string Label, double Confidence = 1.0);

internal sealed record AnalysisProgressMarker(long Sample, AnalysisMarkerKind Kind);

internal sealed record AnalysisMotifMarker(long StartSample, long EndSample, string MotifId);

internal sealed record AnalysisRelationshipMarker(string Label);

/// <summary>
/// Renderer-neutral, precomputed presentation data derived from analysis.json.
/// A1 exposes only a policy-approved global key. Other marker arrays remain
/// empty until their independent release gates pass.
/// </summary>
internal sealed class AnalysisOverlayScene
{
    public static AnalysisOverlayScene Empty { get; } = new();

    public string KeyLabel { get; init; } = "";
    public AnalysisSectionMarker[] Sections { get; init; } = [];
    public AnalysisHarmonyMarker[] Harmony { get; init; } = [];
    public AnalysisProgressMarker[] PhraseMarkers { get; init; } = [];
    public AnalysisMotifMarker[] MotifMarkers { get; init; } = [];
    public AnalysisRelationshipMarker[] Relationships { get; init; } = [];
}

internal static class AnalysisOverlaySceneBuilder
{
    public static AnalysisOverlayScene Build(
        AnalysisOutput output,
        VisualizationTimeline timeline,
        AnalysisOverlayMode mode)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (output is null || mode == AnalysisOverlayMode.None)
            return AnalysisOverlayScene.Empty;

        KeyInterpretation key = output.Global?.Key;
        KeyCandidate primary = key?.Primary;
        if (primary is null || !AnalysisDisplayPolicy.DisplayKey(key.Confidence, mode)
            || string.IsNullOrWhiteSpace(primary.Tonic)
            || string.IsNullOrWhiteSpace(primary.Mode))
            return AnalysisOverlayScene.Empty;

        return new AnalysisOverlayScene
        {
            KeyLabel = $"KEY {primary.Tonic} {primary.Mode}",
        };
    }

    // Kept as a source-compatible bridge for analysis-only callers while the
    // public CLI migrates from the old bool flag to explicit overlay modes.
    public static AnalysisOverlayScene Build(
        AnalysisOutput output,
        VisualizationTimeline timeline,
        bool allowTentative)
        => Build(
            output,
            timeline,
            allowTentative ? AnalysisOverlayMode.Standard : AnalysisOverlayMode.Minimal);
}
