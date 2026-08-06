using Fmp.Application.Contracts;

namespace Fmp.Cli;

internal record VisualizationWorkspace(
    string OutputDir,
    string TimelinePath,
    string AudioDir,
    string MasterAudioPath,
    string ScopeDir,
    string ScopeMetadataPath,
    string CorrscopeConfigPath,
    string VideoPath)
{
    public static VisualizationWorkspace Create(VisualizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        FileInfo input = new(request.InputPath);
        ArgumentNullException.ThrowIfNull(input);

        string outputDir = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))
            ?? input.DirectoryName
            ?? ".";
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
        string videoPath = Path.GetFullPath(request.OutputPath);
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

/// <summary>
/// A scratch workspace rooted under the system temporary directory. It is
/// deleted when disposed; only artifacts explicitly written outside the
/// workspace (e.g. an explicit <c>--timeline-out</c> path) survive the
/// owning command. Used by planning paths so that merely requesting a plan
/// can never create or replace files inside the real output directory.
/// </summary>
internal sealed record TemporaryVisualizationWorkspace : VisualizationWorkspace, IDisposable
{
    private readonly string _root;

    private TemporaryVisualizationWorkspace(
        string root,
        string outputDir,
        string timelinePath,
        string audioDir,
        string masterAudioPath,
        string scopeDir,
        string scopeMetadataPath,
        string corrscopeConfigPath,
        string videoPath)
        : base(
            outputDir,
            timelinePath,
            audioDir,
            masterAudioPath,
            scopeDir,
            scopeMetadataPath,
            corrscopeConfigPath,
            videoPath)
    {
        _root = root;
    }

    public static TemporaryVisualizationWorkspace Create(string label)
    {
        string root = Path.Combine(Path.GetTempPath(), $"fmp-render-{label}-{Guid.NewGuid():N}");
        string timelinePath = Path.Combine(root, "timeline.json");
        string audioDir = Path.Combine(root, "audio");
        string scopeDir = Path.Combine(root, "scope");
        return new TemporaryVisualizationWorkspace(
            root,
            root,
            timelinePath,
            audioDir,
            Path.Combine(audioDir, "master.wav"),
            scopeDir,
            Path.Combine(scopeDir, "metadata.json"),
            Path.Combine(scopeDir, "corrscope-grid.yaml"),
            Path.Combine(root, "output.mp4"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of a scratch directory; a leftover temp
            // directory is harmless.
        }
    }
}
