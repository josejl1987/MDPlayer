namespace Fmp.Cli;

internal sealed record VisualizationWorkspace(
    string OutputDir,
    string TimelinePath,
    string AudioDir,
    string MasterAudioPath,
    string ScopeDir,
    string ScopeMetadataPath,
    string CorrscopeConfigPath,
    string VideoPath)
{
    public static VisualizationWorkspace Create(VisualizeOptions options, FileInfo input)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(input);

        string outputDir = options.OutputDir;
        if (string.IsNullOrWhiteSpace(outputDir))
            outputDir = Path.Combine(
                input.DirectoryName ?? ".",
                Path.GetFileNameWithoutExtension(input.Name) + ".visualization");

        string timelinePath = Path.Combine(outputDir, "timeline.json");
        string audioDir = Path.Combine(outputDir, "audio");
        string masterAudioPath = Path.Combine(audioDir, "master.wav");
        string scopeDir = Path.Combine(outputDir, "scope");
        string scopeMetadataPath = Path.Combine(scopeDir, "metadata.json");
        string corrscopeConfigPath = Path.Combine(scopeDir, "corrscope-grid.yaml");
        string videoPath = options.VideoPath ?? Path.Combine(outputDir, "visualization.mp4");
        return new VisualizationWorkspace(
            outputDir,
            timelinePath,
            audioDir,
            masterAudioPath,
            scopeDir,
            scopeMetadataPath,
            corrscopeConfigPath,
            videoPath);
    }

    public bool HasConflict(bool includeVideo)
        => FirstConflict(includeVideo) != null;

    public string FirstConflict(bool includeVideo)
    {
        foreach (string path in EnumerateArtifacts(includeVideo))
        {
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    public IEnumerable<string> EnumerateArtifacts(bool includeVideo)
    {
        yield return TimelinePath;
        yield return MasterAudioPath;
        yield return ScopeMetadataPath;
        yield return CorrscopeConfigPath;
        if (includeVideo)
            yield return VideoPath;
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(OutputDir);
        Directory.CreateDirectory(AudioDir);
        Directory.CreateDirectory(ScopeDir);
    }
}
