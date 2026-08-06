namespace Fmp.Application.PlaybackAssets;

/// <summary>
/// Result of a Furnace FM-asset dump, in the application layer so the GUI can
/// surface it without referencing the core assembly directly (architecture:
/// the GUI reaches Core only through Application).
/// </summary>
public sealed class FurnaceExportResult
{
    /// <summary>True if the underlying render completed.</summary>
    public bool RenderSucceeded { get; init; }

    /// <summary>Fatal render error, or null on success.</summary>
    public string? RenderError { get; init; }

    /// <summary>Non-fatal asset-export warning, or null.</summary>
    public string? ExportWarning { get; init; }

    /// <summary>Number of distinct .tfi instruments exported.</summary>
    public int ExportedCount { get; init; }
}

/// <summary>
/// Application-level facade over the core Furnace FM-asset exporter. The GUI
/// depends on this (not the core service) so the GUI keeps its dependency
/// direction Core → Application → GUI.
/// </summary>
public static class FurnaceExportService
{
    public static FurnaceExportResult Dump(
        string trackPath,
        string? targetDirectory = null,
        int sampleRate = 44100,
        string? fmpComPath = null)
    {
        global::Fmp.Core.PlaybackAssets.Furnace.FurnaceAssetDumpResult core =
            global::Fmp.Core.PlaybackAssets.Furnace.FurnaceAssetDumpService.Dump(
                trackPath, targetDirectory, sampleRate, fmpComPath);
        return new FurnaceExportResult
        {
            RenderSucceeded = core.RenderSucceeded,
            RenderError = core.RenderError,
            ExportWarning = core.ExportWarning,
            ExportedCount = core.ExportedCount,
        };
    }
}
