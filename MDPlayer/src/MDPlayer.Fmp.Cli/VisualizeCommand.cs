using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;

namespace Fmp.Cli;

public static class VisualizeCommand
{
    public static int Handle(string[] args)
    {
        VisualizeOptions options = ParseArgs(args);
        if (options == null)
            return 2;
        if (string.IsNullOrEmpty(options.Input))
        {
            Console.Error.WriteLine("error: no input file specified");
            return 2;
        }
        if (options.LayoutMode == VisualizationLayoutMode.Focus)
        {
            Console.Error.WriteLine("warning: --layout focus is deprecated; using --layout auto --channels active");
            options.LayoutMode = VisualizationLayoutMode.Auto;
            options.Channels = VisualizationChannelFilter.Active;
        }

        VisualizationBackendResolution resolution;
        try
        {
            resolution = VisualizationBackendResolver.Resolve(options);
        }
        catch (VisualizationBackendResolutionException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ex.ExitCode;
        }

        return string.Equals(resolution.Backend.Id, "fmp", StringComparison.Ordinal)
            ? VisualizationRunner.Run(options)
            : VgmVisualizeCommand.Handle(options, resolution);
    }

    internal static VisualizeOptions ParseArgs(string[] args)
        => VisualizeOptionsParser.Parse(args);

    // Compatibility entry points keep existing tests and callers source-stable;
    // the implementations live in the concrete runner/support files.
    internal static VisualizationPresentation ResolvePresentation(VisualizeOptions options, FileInfo input)
        => VisualizationSupport.ResolvePresentation(options, input);

    internal static VisualizationTimeline AlignTimelineToAudio(
        VisualizationTimeline timeline, long audioEndSample)
        => VisualizationSupport.AlignTimelineToAudio(timeline, audioEndSample);

    internal static IProgress<ScopeProgress> CreateProgressReporter(bool quiet)
        => VisualizationSupport.CreateProgressReporter(quiet);

    internal static void WriteSummary(
        VisualizeOptions options,
        string timelinePath,
        string videoPath,
        VisualizationPipeline.Result capture,
        ScopeRenderer.ScopeResult scope,
        double captureSeconds = 0,
        double stemRenderSeconds = 0,
        double energySeconds = 0,
        double preparationSeconds = 0,
        double compositionSeconds = 0,
        double totalSeconds = 0,
        Fmp.Core.Visualization.Rendering.SinglePassComposer.ComposeMetrics composeMetrics = null)
        => VisualizationSupport.WriteSummary(options, timelinePath, videoPath, capture, scope,
            captureSeconds, stemRenderSeconds, energySeconds, preparationSeconds,
            compositionSeconds, totalSeconds, composeMetrics);
}
