using Fmp.Core.IO;
using Fmp.Core.Rendering;

namespace Fmp.Cli;

/// <summary>
/// A validated track: the input file, its loaded bytes, and the runtime
/// assets and file system resolved from runtime settings.
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
/// the track bytes.
/// </summary>
internal static class TrackPreparation
{
    public static PreparedTrack Prepare(
        string inputPath,
        string fmpCom,
        string assetsDir,
        IReadOnlyList<string> searchPaths)
    {
        var input = new FileInfo(inputPath);
        if (!input.Exists)
            throw new TrackPreparationException($"input not found: {input.FullName}", 3);

        string extension = input.Extension.ToLowerInvariant();
        if (!FmpFormat.IsSupportedExtension(extension))
            throw new TrackPreparationException(
                $"unsupported format: {extension} ({FmpFormat.ExpectedDescription})", 3);

        string resolvedFmpCom = ToolResolver.ResolveFile(fmpCom, assetsDir, "FMP.COM");
        if (string.IsNullOrEmpty(resolvedFmpCom) || !File.Exists(resolvedFmpCom))
            throw new TrackPreparationException("FMP.COM not found", 4);

        var resolvedSearchPaths = new List<string>();
        resolvedSearchPaths.AddRange(searchPaths);
        if (!string.IsNullOrEmpty(assetsDir))
            resolvedSearchPaths.Add(assetsDir);
        resolvedSearchPaths.Add(input.DirectoryName ?? ".");

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
            new FmpRuntimeAssets(resolvedFmpCom),
            new FmpFileSystem(resolvedSearchPaths.Distinct()));
    }

}
