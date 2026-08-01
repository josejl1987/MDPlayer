using Fmp.Core.IO;

namespace Fmp.Cli;

/// <summary>
/// A validated track: the input file, its loaded bytes, and the runtime
/// assets and file system resolved from the render settings.
/// </summary>
internal sealed record PreparedTrack(
    FileInfo Input,
    byte[] Data,
    FmpRuntimeAssets Assets,
    FmpFileSystem FileSystem);

/// <summary>
/// Thrown by <see cref="TrackPreparation.Prepare"/> when a track cannot be
/// prepared. <see cref="ExitCode"/> mirrors the CLI's existing per-stage
/// failure codes: 3 = input/format, 4 = FMP.COM missing, 7 = I/O failure.
/// </summary>
internal sealed class TrackPreparationException : Exception
{
    public int ExitCode { get; }

    public TrackPreparationException(string message, int exitCode) : base(message)
    {
        ExitCode = exitCode;
    }
}

/// <summary>
/// Validates an FMP-family track input, resolves FMP.COM and the search paths, and loads
/// the track bytes. Shared by the render, batch and visualize commands.
/// </summary>
internal static class TrackPreparation
{
    public static PreparedTrack Prepare(string inputPath, RenderSettings settings)
    {
        var input = new FileInfo(inputPath);
        if (!input.Exists)
            throw new TrackPreparationException($"input not found: {input.FullName}", 3);

        string extension = input.Extension.ToLowerInvariant();
        if (extension is not (".ovi" or ".opi" or ".ozi" or ".mpi" or ".mvi" or ".mzi"))
            throw new TrackPreparationException(
                $"unsupported format: {extension} (expected .ovi, .opi, .ozi, .mpi, .mvi, or .mzi)", 3);

        string fmpCom = ToolResolver.ResolveFile(settings.FmpCom, settings.AssetsDir, "FMP.COM");
        if (string.IsNullOrEmpty(fmpCom) || !File.Exists(fmpCom))
            throw new TrackPreparationException("FMP.COM not found", 4);

        var searchPaths = new List<string>();
        searchPaths.AddRange(settings.SearchPaths);
        if (!string.IsNullOrEmpty(settings.AssetsDir))
            searchPaths.Add(settings.AssetsDir);
        searchPaths.Add(input.DirectoryName ?? ".");

        byte[] data;
        try
        {
            data = File.ReadAllBytes(input.FullName);
        }
        catch (Exception ex)
        {
            throw new TrackPreparationException($"reading input — {ex.Message}", 7);
        }

        return new PreparedTrack(
            input,
            data,
            new FmpRuntimeAssets(fmpCom),
            new FmpFileSystem(searchPaths.Distinct()));
    }
}
