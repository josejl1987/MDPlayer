using Fmp.Application.Contracts;

namespace Fmp.Cli;

/// <summary>
/// Classifies how a change from one request snapshot to the next affects the
/// staged preview work, so the caller can run the cheapest valid operation.
/// </summary>
[Flags]
internal enum VisualizationRequestImpact
{
    None = 0,
    ExportOnly = 1,
    Frame = 2,
    Plan = 4,
    ScopeAssets = 8,
    TimelineCapture = 16,
}

internal static class VisualizationRequestImpactExtensions
{
    public static bool Has(this VisualizationRequestImpact impact, VisualizationRequestImpact flag)
        => (impact & flag) == flag;
}

internal static class VisualizationRequestImpactClassifier
{
    private static readonly FileInfo MissingFileSentinel = new("/__missing__");

    /// <summary>
    /// Compares two request snapshots and yields the highest replayed stage
    /// implied by the difference. The stages are ordered so that a change to an
    /// earlier stage also invalidates every derived later stage.
    /// </summary>
    public static VisualizationRequestImpact Classify(
        VisualizationRequest previous,
        VisualizationRequest next,
        RenderRuntimeOptions runtime)
    {
        if (ReferenceEquals(previous, next))
            return VisualizationRequestImpact.None;

        // Snapshot the input file identity so the key comparison is cheap and
        // deterministic; a missing input is represented via the sentinel so the
        // comparison still yields a change signal when appropriate.
        FileInfo previousFile = DescribeInput(previous.InputPath);
        FileInfo nextFile = DescribeInput(next.InputPath);

        TimelineCaptureKey previousTimeline =
            TimelineCaptureKey.From(previous, previousFile, runtime);
        TimelineCaptureKey nextTimeline =
            TimelineCaptureKey.From(next, nextFile, runtime);

        if (previousTimeline != nextTimeline)
        {
            return VisualizationRequestImpact.TimelineCapture
                | VisualizationRequestImpact.ScopeAssets
                | VisualizationRequestImpact.Plan
                | VisualizationRequestImpact.Frame;
        }

        ScopeAssetKey previousScope = ScopeAssetKey.From(previous, previousTimeline);
        ScopeAssetKey nextScope = ScopeAssetKey.From(next, nextTimeline);

        if (!previousScope.Equals(nextScope))
        {
            return VisualizationRequestImpact.ScopeAssets
                | VisualizationRequestImpact.Plan
                | VisualizationRequestImpact.Frame;
        }

        PlanKey previousPlan = PlanKey.From(previous, previousScope);
        PlanKey nextPlan = PlanKey.From(next, nextScope);

        if (previousPlan != nextPlan)
        {
            return VisualizationRequestImpact.Plan
                | VisualizationRequestImpact.Frame;
        }

        FrameStyleKey previousFrame = FrameStyleKey.From(previous, previousPlan);
        FrameStyleKey nextFrame = FrameStyleKey.From(next, nextPlan);

        if (previousFrame != nextFrame)
        {
            return VisualizationRequestImpact.Frame;
        }

        // Only output path, encoder or overwrite differ.
        if (HasExportOnlyChange(previous, next))
            return VisualizationRequestImpact.ExportOnly;

        return VisualizationRequestImpact.None;
    }

    private static FileInfo DescribeInput(string inputPath)
    {
        try
        {
            var fi = new FileInfo(Path.GetFullPath(inputPath));
            return fi.Exists ? fi : MissingFileSentinel;
        }
        catch
        {
            return MissingFileSentinel;
        }
    }

    private static bool HasExportOnlyChange(
        VisualizationRequest previous,
        VisualizationRequest next)
    {
        return !string.Equals(
                previous.OutputPath, next.OutputPath, StringComparison.Ordinal)
            || previous.Output.Encoder != next.Output.Encoder
            || previous.Output.Overwrite != next.Output.Overwrite;
    }
}
