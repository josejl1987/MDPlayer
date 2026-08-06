using System.IO.Compression;
using Fmp.Core.IO;
using Fmp.Core.PlaybackAssets.Opn;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;

namespace Fmp.Core.PlaybackAssets.Furnace;

/// <summary>Result of a Furnace FM-asset dump operation.</summary>
public sealed class FurnaceAssetDumpResult
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
/// Public entry point for exporting the FM instruments of an OPN song as
/// Furnace-compatible .tfi files plus a manifest. Dispatch is content-based
/// rather than extension-based:
///   * VGM/VGZ (and other VGM streams) are parsed directly; their ordered
///     OPN-family register writes are fed to the asset collector and exported.
///   * FMP-family tracks (.OVI/.OPI/.OZI/.MPI/.MVI/.MZI) are rendered through
///     the FMP engine (default MDSound YM2608 path) while a collector observes
///     the ordered write stream.
/// This is the same pipeline the Fmp.Cli drives via <c>--dump-furnace-assets</c>.
/// </summary>
public static class FurnaceAssetDumpService
{
    /// <summary>
    /// Collects every distinct FM instrument from <paramref name="trackPath"/>
    /// and exports it to <paramref name="targetDirectory"/>. The target
    /// directory is created on demand when <c>null</c>. Never writes the
    /// master audio; only firmware assets.
    /// </summary>
    public static FurnaceAssetDumpResult Dump(
        string trackPath,
        string? targetDirectory = null,
        int sampleRate = 44100,
        string? fmpComPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackPath);

        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            return new FurnaceAssetDumpResult
            {
                RenderSucceeded = false,
                RenderError = "no target directory was provided for the Furnace asset dump.",
            };
        }

        byte[] trackData;
        try
        {
            trackData = File.ReadAllBytes(trackPath);
        }
        catch (Exception ex)
        {
            return new FurnaceAssetDumpResult
            {
                RenderSucceeded = false,
                RenderError = $"could not read '{trackPath}': {ex.Message}",
            };
        }

        // Content-based dispatch: try VGM first (works for .vgm, .vgz and raw
        // VGM streams); fall back to the FMP-engine render for FMP-family files.
        if (TryCollectVgm(trackData, out string? vgmError, out PlaybackAssetSnapshot? vgmSnapshot))
        {
            if (vgmSnapshot is null)
            {
                return new FurnaceAssetDumpResult
                {
                    RenderSucceeded = false,
                    RenderError = vgmError,
                };
            }
            return Export(vgmSnapshot, targetDirectory);
        }

        if (!IsFmpFamilyExtension(trackPath))
        {
            return new FurnaceAssetDumpResult
            {
                RenderSucceeded = false,
                RenderError =
                    $"'{Path.GetFileName(trackPath)}' is not a supported FM-music format: "
                    + "expected a VGM/VGZ or an FMP-family track "
                    + "(.OVI/.OPI/.OZI/.MPI/.MVI/.MZI).",
            };
        }

        string resolvedFmpCom = ResolveFmpCom(fmpComPath);
        if (resolvedFmpCom is null)
        {
            return new FurnaceAssetDumpResult
            {
                RenderSucceeded = false,
                RenderError = "FMP.COM was not found; the Furnace asset dump requires the FMP runtime.",
            };
        }

        var assets = new FmpRuntimeAssets(resolvedFmpCom);
        var renderer = new FmpRenderer(assets, fileSystem: null, sampleRate, ssgGainDb: 0);

        // Use the platform's default OPNA backend (MDSound); the asset
        // collector observes the same ordered write stream on either backend.
        var options = new FmpRenderer.Options
        {
            AssetDumpDirectory = targetDirectory,
            // Asset collection only needs one pass; fade/tail are audio
            // aesthetics that would only slow the dump.
            LoopCount = 1,
            FadeSeconds = 0,
            TailSeconds = 0,
        };

        string trackFile = Path.GetFileName(trackPath);
        FmpRenderer.Result result = renderer.RenderToWav(
            trackData, trackFile, outputWavPath: null, options);

        return new FurnaceAssetDumpResult
        {
            RenderSucceeded = result.Success,
            RenderError = result.Success ? null : result.LastError,
            ExportWarning = string.IsNullOrEmpty(result.AssetDumpError) ? null : result.AssetDumpError,
            ExportedCount = CountExportedAssets(targetDirectory),
        };
    }

    /// <summary>
    /// Attempts to collect FM instruments directly from a VGM/VGZ byte stream
    /// without audio rendering: the parsed VGM carries the authoritative,
    /// ordered list of register writes, which is exactly what the collector
    /// needs. Returns true when the input parses as a VGM document.
    /// </summary>
    private static bool TryCollectVgm(
        byte[] trackData,
        out string? error,
        out PlaybackAssetSnapshot? snapshot)
    {
        error = null;
        snapshot = null;

        VgmDocument document;
        try
        {
            document = VgmDocument.Parse(MaybeDecompressVgz(trackData));
        }
        catch (Exception ex) when (ex is IOException or VgmPlaybackException)
        {
            return false; // not a VGM stream; let the caller fall through
        }

        var collector = new PlaybackAssetCollector();
        foreach (VgmRegisterWrite write in document.Writes)
        {
            // Only OPN-family FM register streams are meaningful to the TFI pipe.
            if (!OpnChipCapabilities.TryGetCapabilities(write.Device.Type, out _))
                continue;

            collector.Observe(new ChipWriteEvent
            {
                ChipType = write.Device.Type,
                ChipIndex = write.Device.Instance,
                Port = write.Port,
                Address = write.Address,
                Data = (byte)write.Data,
                PlaybackSample = write.SourceSample,
                WriteIndex = null,
            });
        }

        snapshot = collector.Complete();
        return true;
    }

    private static FurnaceAssetDumpResult Export(
        PlaybackAssetSnapshot snapshot,
        string targetDirectory)
    {
        try
        {
            var export = FurnaceAssetExporter.Export(snapshot, targetDirectory);
            return new FurnaceAssetDumpResult
            {
                RenderSucceeded = true,
                RenderError = null,
                ExportWarning = export.Failed
                    ? "export failed for some assets: " + string.Join(", ", export.Failures.Keys)
                    : null,
                ExportedCount = export.ExpandedCount,
            };
        }
        catch (Exception ex)
        {
            return new FurnaceAssetDumpResult
            {
                RenderSucceeded = true,
                RenderError = null,
                ExportWarning = $"asset dump export failed: {ex.GetType().Name}: {ex.Message}",
                ExportedCount = 0,
            };
        }
    }

    private static int CountExportedAssets(string targetDirectory)
    {
        string fmDir = Path.Combine(targetDirectory, FurnaceExportResult.FmDirectoryName);
        try
        {
            if (!Directory.Exists(fmDir))
                return 0;
            return Directory.GetFiles(fmDir, "*.tfi").Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Resolves FMP.COM from an explicit path, the executable directory,
    /// or the current directory (matching how the CLI ships it next to the
    /// product).</summary>
    private static string? ResolveFmpCom(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return Path.GetFullPath(explicitPath);

        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "FMP.COM"),
            Path.Combine(Directory.GetCurrentDirectory(), "FMP.COM"),
        };
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static readonly HashSet<string> FmpFamilyExtensions = new(
        new[] { ".OVI", ".OPI", ".OZI", ".MPI", ".MVI", ".MZI" },
        StringComparer.OrdinalIgnoreCase);

    private static bool IsFmpFamilyExtension(string trackPath) =>
        FmpFamilyExtensions.Contains(Path.GetExtension(trackPath));

    /// <summary>Decompresses a .vgz stream (gzip magic 1f 8b) down to the raw
    /// VGM bytes; a plain .vgm is returned unchanged. Mirrors VgmInput.Read.</summary>
    private static byte[] MaybeDecompressVgz(byte[] data)
    {
        if (data.Length < 2 || data[0] != 0x1F || data[1] != 0x8B)
            return data;

        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
