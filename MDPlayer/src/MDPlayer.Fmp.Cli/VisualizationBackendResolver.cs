using Fmp.Core.Rendering;
using Fmp.Core.Visualization;

namespace Fmp.Cli;

internal sealed record VisualizationBackendResolution(
    FileInfo Input,
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
    public static VisualizationBackendResolution Resolve(VisualizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Input))
            throw new VisualizationBackendResolutionException("no input file specified", 2);

        var input = new FileInfo(options.Input);
        if (!input.Exists)
            throw new VisualizationBackendResolutionException(
                $"input not found: {input.FullName}", 3);

        IReadOnlyList<string> searchPaths;
        string fmpCom;
        try
        {
            searchPaths = BuildSearchPaths(input, options);
            fmpCom = PlaybackBackendRegistry.ResolveFmpCom(options.FmpCom, searchPaths);
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            throw new VisualizationBackendResolutionException(
                $"invalid backend path: {ex.Message}", 2);
        }

        if (options.FmpComExplicit && fmpCom == null)
        {
            throw new VisualizationBackendResolutionException(
                $"FMP.COM not found: {options.FmpCom}", 4);
        }

        bool requiresFmp = string.Equals(options.Backend, "fmp", StringComparison.Ordinal)
            || (string.Equals(options.Backend, "auto", StringComparison.Ordinal)
                && FmpFormat.IsSupportedExtension(input.Extension));
        if (requiresFmp && fmpCom == null)
            throw new VisualizationBackendResolutionException("FMP.COM not found", 4);

        var environment = new PlaybackEnvironment(searchPaths, true, options.SampleRate);
        PlaybackBackendRegistry registry = PlaybackBackendRegistry.CreateDefault(
            environment,
            fmpCom);
        if (!registry.TrySelect(
                input,
                environment,
                options.Backend,
                out IPlaybackBackend backend,
                out PlaybackProbeResult probe))
        {
            string details = probe.Warnings.Count == 0
                ? "no playback backend accepted the input"
                : string.Join("; ", probe.Warnings);
            throw new VisualizationBackendResolutionException(
                $"no playback backend accepted {input.Name}: {details}", 3);
        }

        if (!probe.Visualizable)
        {
            throw new VisualizationBackendResolutionException(
                "MDPlayer can play this track, but none of its active devices expose supported note data",
                10);
        }

        VisualizationOptionApplicability.Validate(options, backend.Id);
        if (string.Equals(backend.Id, "fmp", StringComparison.Ordinal) && fmpCom != null)
            options.FmpCom = fmpCom;
        return new VisualizationBackendResolution(input, environment, backend, probe);
    }

    internal static IReadOnlyList<string> BuildSearchPaths(
        FileInfo input,
        VisualizeOptions options)
        => BuildSearchPathsCore(input, options.SearchPaths, options.AssetsDir, options.FmpCom);

    internal static IReadOnlyList<string> BuildSearchPaths(
        FileInfo input,
        RenderSettings settings)
        => BuildSearchPathsCore(input, settings.SearchPaths, settings.AssetsDir, settings.FmpCom);

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

internal static class VisualizationOptionApplicability
{
    public static void Validate(VisualizeOptions options, string backendId)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(backendId))
            throw new ArgumentException("backend id is required", nameof(backendId));

        if (!string.Equals(backendId, "spc", StringComparison.Ordinal)
            && (options.SpcStemsExplicit || options.SpcPitchExplicit))
        {
            throw Unsupported("--spc-stems and --spc-pitch", backendId);
        }

        if (!string.Equals(backendId, "fmp", StringComparison.Ordinal))
        {
            if (options.FmpComExplicit)
                throw Unsupported("--fmp-com", backendId);
            if (options.SsgGainExplicit)
                throw Unsupported("--ssg-gain-db", backendId);
            if (options.TimeoutExplicit)
                throw Unsupported("--timeout", backendId);
            if (!string.IsNullOrWhiteSpace(options.CorrscopeVideoTemplate))
                throw Unsupported("--corrscope-video-template", backendId);
        }
        else if (options.ScopeModeExplicit)
        {
            throw new VisualizationBackendResolutionException(
                "--scopes is not supported by the legacy FMP visualization path",
                2);
        }
    }

    private static VisualizationBackendResolutionException Unsupported(
        string option,
        string backendId) =>
        new($"{option} does not apply to backend '{backendId}'", 2);
}
