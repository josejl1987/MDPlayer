using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Fmp.Application.Contracts;

namespace Fmp.Cli;

internal sealed record VisualizationBackendResolution(
    FileInfo Input,
    IReadOnlyList<string> SearchPaths,
    string? FmpComPath,
    PlaybackEnvironment Environment,
    IPlaybackBackend Backend,
    PlaybackProbeResult Probe);

internal sealed class VisualizationBackendResolutionException : Exception
{
    public int ExitCode { get; }

    public VisualizationBackendResolutionException(string message, int exitCode)
        : base(message)
    {
        ExitCode = exitCode;
    }
}

internal static class VisualizationBackendResolver
{
    public static VisualizationBackendResolution Resolve(
        VisualizationRequest request,
        RenderRuntimeOptions runtime)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runtime);

        if (string.IsNullOrWhiteSpace(request.InputPath))
            throw new VisualizationBackendResolutionException("no input file specified", 2);

        var input = new FileInfo(request.InputPath);
        if (!input.Exists)
            throw new VisualizationBackendResolutionException(
                $"input not found: {input.FullName}", 3);

        IReadOnlyList<string> searchPaths;
        string fmpCom;
        try
        {
            searchPaths = BuildSearchPaths(input, runtime);
            fmpCom = PlaybackBackendRegistry.ResolveFmpCom(runtime.FmpCom, searchPaths);
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            throw new VisualizationBackendResolutionException(
                $"invalid backend path: {ex.Message}", 2);
        }

        bool requiresFmp = string.Equals(runtime.Backend, "fmp", StringComparison.Ordinal)
            || (string.Equals(runtime.Backend, "auto", StringComparison.Ordinal)
                && FmpFormat.IsSupportedExtension(input.Extension));
        if (requiresFmp && fmpCom == null)
            throw new VisualizationBackendResolutionException("FMP.COM not found", 4);

        var environment = new PlaybackEnvironment(searchPaths, true, request.Playback.SampleRate);
        PlaybackBackendRegistry registry = PlaybackBackendRegistry.CreateDefault(environment, fmpCom);
        if (!registry.TrySelect(input, environment, runtime.Backend,
                out IPlaybackBackend backend, out PlaybackProbeResult probe))
        {
            string details = probe.Warnings.Count == 0
                ? "no playback backend accepted the input"
                : string.Join("; ", probe.Warnings);
            throw new VisualizationBackendResolutionException(
                $"no playback backend accepted {input.Name}: {details}", 3);
        }

        if (!probe.Visualizable)
            throw new VisualizationBackendResolutionException(
                "MDPlayer can play this track, but none of its active devices expose supported note data",
                10);

        return new VisualizationBackendResolution(input, searchPaths, fmpCom, environment, backend, probe);
    }

    internal static IReadOnlyList<string> BuildSearchPaths(
        FileInfo input,
        IReadOnlyList<string> searchPaths,
        string assetsDir,
        string fmpCom)
        => BuildSearchPathsCore(input, searchPaths, assetsDir, fmpCom);

    internal static IReadOnlyList<string> BuildSearchPaths(
        FileInfo input,
        RenderRuntimeOptions runtime)
        => BuildSearchPathsCore(input, runtime.SearchPaths, runtime.AssetsDir, runtime.FmpCom);

    private static IReadOnlyList<string> BuildSearchPathsCore(
        FileInfo input,
        IReadOnlyList<string> searchPaths,
        string assetsDir,
        string fmpCom)
    {
        ArgumentNullException.ThrowIfNull(input);

        var paths = new List<string>();
        paths.AddRange(searchPaths);
        if (!string.IsNullOrWhiteSpace(assetsDir))
            paths.Add(assetsDir);
        if (!string.IsNullOrWhiteSpace(fmpCom))
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(fmpCom));
            if (!string.IsNullOrWhiteSpace(directory))
                paths.Add(directory);
        }

        // FMP.COM is bundled next to the CLI executable (see MDPlayer.Fmp.Cli.csproj);
        // make it resolvable without --fmp-com / --assets-dir.
        string appDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(appDir) && !paths.Contains(appDir, StringComparer.Ordinal))
            paths.Add(appDir);
        paths.Add(input.DirectoryName ?? ".");

        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(comparer)
            .ToArray();
    }
}
