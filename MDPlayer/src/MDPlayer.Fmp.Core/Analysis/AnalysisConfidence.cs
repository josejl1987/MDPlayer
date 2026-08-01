namespace Fmp.Core.Analysis;

internal enum AnalysisOverlayMode
{
    None,
    Minimal,
    Standard,
}

internal static class AnalysisDisplayPolicy
{
    public const double KeyTentativeMinimum = 0.55;
    public const double KeyStrongMinimum = 0.70;

    public static string Certainty(double score)
    {
        if (!double.IsFinite(score) || score < KeyTentativeMinimum)
            return "withheld";
        if (score >= KeyStrongMinimum)
            return "strong";
        return "tentative";
    }

    public static bool DisplayKey(AnalysisConfidence confidence, AnalysisOverlayMode mode)
    {
        if (mode == AnalysisOverlayMode.None || confidence is null || !double.IsFinite(confidence.Score))
            return false;
        if (confidence.Certainty == "strong")
            return confidence.Score >= KeyStrongMinimum;
        return mode == AnalysisOverlayMode.Standard
            && confidence.Certainty == "tentative"
            && confidence.Score >= KeyTentativeMinimum;
    }

    // A1 release gate: these categories remain withheld until their corpus
    // precision gates pass. Keeping the decision here prevents renderer,
    // summary, and cache consumers from growing independent thresholds.
    public static bool DisplayHarmony(AnalysisConfidence confidence, AnalysisOverlayMode mode)
        => false;

    public static bool DisplayRoman(
        AnalysisConfidence key,
        AnalysisConfidence chord,
        AnalysisConfidence roman,
        AnalysisOverlayMode mode)
        => false;

    public static bool DisplayMotif(AnalysisConfidence confidence, AnalysisOverlayMode mode)
        => false;

    public static bool DisplayRelationship(AnalysisConfidence confidence, AnalysisOverlayMode mode)
        => false;
}

// Compatibility bridge for the renderer partials that predate the centralized
// display-policy rename. All thresholds remain owned by AnalysisDisplayPolicy.
internal static class AnalysisConfidencePolicy
{
    public const double KeyStrongMinimum = AnalysisDisplayPolicy.KeyStrongMinimum;
}
