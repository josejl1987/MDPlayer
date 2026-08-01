namespace Fmp.Cli;

internal sealed record VisualizationWorkspace(
    string OutputDir,
    string TimelinePath,
    string ScopeDir,
    string VideoPath)
{
    public static VisualizationWorkspace Create(VisualizeOptions options, FileInfo input)
    {
        string outputDir = options.OutputDir;
        if (string.IsNullOrWhiteSpace(outputDir))
            outputDir = Path.Combine(
                input.DirectoryName ?? ".",
                Path.GetFileNameWithoutExtension(input.Name) + ".visualization");

        string timelinePath = Path.Combine(outputDir, "timeline.json");
        string scopeDir = Path.Combine(outputDir, "scope");
        string videoPath = options.VideoPath ?? Path.Combine(outputDir, "visualization.mp4");
        return new VisualizationWorkspace(outputDir, timelinePath, scopeDir, videoPath);
    }

    public bool HasConflict(bool includeVideo)
        => File.Exists(TimelinePath) || (includeVideo && File.Exists(VideoPath));

    public void EnsureDirectories() => Directory.CreateDirectory(ScopeDir);
}
