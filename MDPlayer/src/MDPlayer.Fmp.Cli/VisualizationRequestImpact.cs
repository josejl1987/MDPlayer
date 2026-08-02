using Fmp.Application.Contracts;
using Fmp.Application.Preview;

namespace Fmp.Cli;

/// <summary>
/// Classifies how a change from one request snapshot to the next affects the
/// staged preview work, so the caller can run the cheapest valid operation.
/// Only the highest affected stage is returned, not a flags combination.
/// </summary>
internal static class VisualizationRequestImpactClassifier
{
    private static readonly FileInfo MissingFileSentinel = new("/__missing__");

    /// <summary>
    /// Compares two request snapshots and yields the highest replayed stage
    /// implied by the difference. The stages are ordered so that a change to an
    /// earlier stage also invalidates every derived later stage.
    /// </summary>
    public static PreviewRequestImpact Classify(
        VisualizationRequest previous,
        VisualizationRequest next,
        RenderRuntimeOptions runtime)
    {
        if (ReferenceEquals(previous, next))
            return PreviewRequestImpact.None;

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
            return PreviewRequestImpact.TimelineCapture;
        }

        ScopeAssetKey previousScope = ScopeAssetKey.From(previous, previousTimeline);
        ScopeAssetKey nextScope = ScopeAssetKey.From(next, nextTimeline);

        if (!previousScope.Equals(nextScope))
        {
            return PreviewRequestImpact.Plan;
        }

        PlanKey previousPlan = PlanKey.From(previous, previousScope);
        PlanKey nextPlan = PlanKey.From(next, nextScope);

        if (previousPlan != nextPlan)
        {
            return PreviewRequestImpact.Plan;
        }

        FrameStyleKey previousFrame = FrameStyleKey.From(previous, previousPlan);
        FrameStyleKey nextFrame = FrameStyleKey.From(next, nextPlan);

        if (previousFrame != nextFrame)
        {
            return PreviewRequestImpact.Frame;
        }

        // Only output path, encoder or overwrite differ.
        if (HasExportOnlyChange(previous, next))
            return PreviewRequestImpact.ExportOnly;

        return PreviewRequestImpact.None;
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
