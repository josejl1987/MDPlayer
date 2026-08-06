using Fmp.Core.PlaybackAssets.Opn;

namespace Fmp.Core.PlaybackAssets.Furnace;

/// <summary>Outcome of one export; directories are always <c>fm</c>.</summary>
internal sealed class FurnaceExportResult
{
    public const string FmDirectoryName = "fm";

    public required string TargetDirectory { get; init; }
    public required string ManifestFile { get; init; }

    /// <summary>Number of .tfi files written successfully.</summary>
    public int ExpandedCount { get; init; }

    /// <summary>Per-file failures (target file path -> error).</summary>
    public IReadOnlyDictionary<string, string> Failures { get; init; }
        = new Dictionary<string, string>();

    public bool Failed => Failures.Count != 0;
}

/// <summary>
/// Writes a completed asset snapshot as deterministic .tfi files plus a
/// manifest, using the project's safe atomic-write convention. Filesystem
/// failures are collected per file and surfaced to the caller; they never
/// throw for an individual asset and never corrupt an existing file.
/// </summary>
internal static class FurnaceAssetExporter
{
    public static FurnaceExportResult Export(
        PlaybackAssetSnapshot snapshot,
        string targetDirectory)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        string fmDir = Path.Combine(targetDirectory, FurnaceExportResult.FmDirectoryName);
        Directory.CreateDirectory(fmDir);

        var failures = new Dictionary<string, string>();

        foreach (CapturedFmInstrument instr in snapshot.Instruments)
        {
            string fileName = $"fm_{instr.Ordinal:000}_{instr.Sha256Short}.tfi";
            string finalPath = Path.Combine(fmDir, fileName);
            try
            {
                WriteAtomic(finalPath, instr.TfiBytes);
            }
            catch (Exception ex)
            {
                // Preserve other successfully exported assets; report this one.
                failures[finalPath] = ex.Message;
            }
        }

        string manifestPath = Path.Combine(targetDirectory, "manifest.json");
        string manifestJson = FurnaceAssetManifestWriter.Write(
            FurnaceExportResult.FmDirectoryName, snapshot.Instruments);
        try
        {
            WriteAtomic(manifestPath, ManifestBytes(manifestJson));
        }
        catch (Exception ex)
        {
            failures[manifestPath] = ex.Message;
        }

        return new FurnaceExportResult
        {
            TargetDirectory = targetDirectory,
            ManifestFile = manifestPath,
            ExpandedCount = snapshot.Instruments.Count,
            Failures = failures,
        };
    }

    /// <summary>
    /// Writes exactly the given bytes via a temporary file in the same
    /// directory, verifies the byte count, then renames it into place. Cleans
    /// the temporary file on any failure and never overwrites a partially
    /// written final file.
    /// </summary>
    private static void WriteAtomic(string finalPath, byte[] content)
    {
        string directory = Path.GetDirectoryName(finalPath);
        Directory.CreateDirectory(directory);

        string tempPath = Path.Combine(
            directory, $".tmp-{Path.GetFileName(finalPath)}-{Guid.NewGuid():N}");

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content, 0, content.Length);
            }

            long written = new FileInfo(tempPath).Length;
            if (written != content.Length)
            {
                throw new IOException(
                    $"Expected {content.Length} bytes but wrote {written} to {tempPath}.");
            }

            File.Move(tempPath, finalPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static byte[] ManifestBytes(string json) =>
        System.Text.Encoding.UTF8.GetBytes(json);
}
