using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;

namespace Fmp.Cli;

public static class VisualizeCommand
{
    public static int Handle(string[] args)
    {
        if (ContainsGenericInput(args))
            return VgmVisualizeCommand.Handle(args);

        VisualizeOptions options = ParseArgs(args);
        if (options == null)
            return 2;
        if (string.IsNullOrEmpty(options.Input))
        {
            Console.Error.WriteLine("error: no input file specified");
            return 2;
        }
        return VisualizationRunner.Run(options);
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

    private static bool ContainsGenericInput(string[] args)
    {
        if (args == null)
            return false;
        var reader = new ArgumentReader(args);
        while (reader.HasMore)
        {
            if (reader.TryReadOption(out string name, out string value))
            {
                if (value == null && OptionConsumesValue(name))
                {
                    try { reader.RequireValue(name); }
                    catch (ArgumentException) { return false; }
                }
                continue;
            }

            string token = reader.Next();
            if (token == "--")
                continue;
            string extension = Path.GetExtension(token).ToLowerInvariant();
            return extension is not (".ovi" or ".opi" or ".ozi" or ".mpi" or ".mvi" or ".mzi");
        }
        return false;
    }

    private static bool OptionConsumesValue(string name) => name switch
    {
        "-o" or "--output" or "--video" or "--fmp-com" or "--assets-dir"
            or "-I" or "--search-path" or "--sample-rate" or "--loops"
            or "--fade" or "--tail" or "--max-duration" or "--ssg-gain-db"
            or "--corrscope" or "--ffmpeg" or "--title" or "--subtitle"
            or "--credits" or "--font" or "--width" or "--height" or "--fps"
            or "--fps-denominator" or "--tool-timeout-minutes" or "--duration"
            or "--timeout" or "--encoder" or "--corrscope-video-template"
            or "--backend" or "--scopes" or "--effects" or "--note-color"
            or "--layout"
            or "--analysis-python" or "--analysis-output" or "--analysis-cache"
            or "--analysis-detail" or "--analysis-timeout-minutes" or "--analysis-overlay" => true,
        _ => false,
    };
}
