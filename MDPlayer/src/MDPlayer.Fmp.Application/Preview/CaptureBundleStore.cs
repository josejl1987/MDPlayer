using System.Text.Json;

namespace Fmp.Application.Preview;

/// <summary>
/// The one session-local store responsible for durable capture bundles under
/// a single session workspace. It never shares bundles across unrelated GUI
/// sessions and implements no eviction. Bundles are captured into a temporary
/// directory and atomically renamed into <c>{session}/captures/&lt;key&gt;/</c>
/// only once all required artifacts validate.
///
/// Directory layout:
/// <code>
/// {session}/captures/
///   capture-v2-&lt;hash&gt;/
///     manifest.json
///     timeline.json
///     master.wav      (optional, backends/compositions that produce one)
///     stems/          (optional)
///   .capture-v2-&lt;hash&gt;-&lt;guid&gt;.tmp/
/// </code>
/// </summary>
internal sealed class CaptureBundleStore
{
    private const string CapturesDirectoryName = "captures";
    private const string ManifestFileName = "manifest.json";

    private readonly string _sessionDirectory;

    public CaptureBundleStore(string sessionDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        _sessionDirectory = Path.GetFullPath(sessionDirectory);
    }

    /// <summary>The canonical final directory for a capture key (not yet created).</summary>
    public string GetBundleDirectory(string captureKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureKey);
        return Path.Combine(_sessionDirectory, CapturesDirectoryName, captureKey);
    }

    /// <summary>
    /// Opens an existing final bundle when it validates against the supplied
    /// input identity; otherwise returns a fresh temporary directory. Never
    /// writes into a final directory. Requires the new bundle to be completed
    /// via <see cref="Commit"/> before it becomes visible.
    /// </summary>
    public CaptureBundle? TryOpenValid(
        string captureKey,
        string inputPath,
        long inputLength,
        DateTime inputLastWriteUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureKey);

        string finalDir = GetBundleDirectory(captureKey);
        if (!Directory.Exists(finalDir))
            return null;

        CaptureBundle? bundle = TryOpen(finalDir, captureKey, inputPath, inputLength, inputLastWriteUtc);
        if (bundle is null)
        {
            // A final directory without a valid manifest (or with mismatched
            // identity) is invalid; delete it rather than treating a partial or
            // stale bundle as usable.
            try { Directory.Delete(finalDir, recursive: true); } catch { }
            return null;
        }

        return bundle;
    }

    /// <summary>
    /// Creates the temporary capture directory a producer writes artifacts into
    /// (never into the final directory). The resulting bundle is committed via
    /// <see cref="Commit"/> (which atomically renames it into place) or cleaned
    /// up via <see cref="DeleteIncomplete"/>.
    /// </summary>
    public CaptureBundle CreatePaths(string captureKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureKey);

        string tempDir = Path.Combine(
            _sessionDirectory,
            CapturesDirectoryName,
            $".{captureKey}-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(tempDir);

        return new CaptureBundle
        {
            Key = captureKey,
            DirectoryPath = tempDir,
            ManifestPath = Path.Combine(tempDir, ManifestFileName),
            TimelinePath = Path.Combine(tempDir, "timeline.json"),
        };
    }

    /// <summary>
    /// Commits a validated temporary bundle to its final directory. The
    /// manifest must already exist and validate. If a final bundle already
    /// exists and validates, the temporary copy is discarded in its favour.
    /// </summary>
    public CaptureBundle Commit(
        CaptureBundle temporaryBundle,
        CaptureBundleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(temporaryBundle);
        ArgumentNullException.ThrowIfNull(manifest);

        string finalDir = GetBundleDirectory(manifest.CaptureKey);
        string tempDir = temporaryBundle.DirectoryPath;

        if (Directory.Exists(finalDir))
        {
            // Existing bundle wins: discard the temporary copy.
            DeleteIncomplete(tempDir);
            return TryOpen(finalDir, manifest.CaptureKey, manifest.InputPath,
                    manifest.InputLength,
                    new DateTime(manifest.InputLastWriteUtcTicks, DateTimeKind.Utc))!
                ?? throw new InvalidOperationException(
                    "An existing final capture bundle failed validation during commit.");
        }

        // Ensure required artifacts validate before moving into place.
        ValidateRequired(tempDir, manifest);

        // The manifest doubles here as the final manifest written at the end of
        // a temporary bundle; writing it is the caller's responsibility. On a
        // fresh temp bundle we persist it to the temp dir "last".
        string finalManifest = Path.Combine(tempDir, ManifestFileName);
        VisualizationManifestWriter.Write(finalManifest, manifest);

        Directory.CreateDirectory(Path.GetDirectoryName(finalDir)!);
        Directory.Move(tempDir, finalDir);

        return TryOpen(finalDir, manifest.CaptureKey, manifest.InputPath,
                manifest.InputLength,
                new DateTime(manifest.InputLastWriteUtcTicks, DateTimeKind.Utc))!
            ?? throw new InvalidOperationException(
                "Committed capture bundle failed re-validation.");
    }

    /// <summary>Deletes an incomplete or failed temporary capture directory if it exists.</summary>
    public void DeleteIncomplete(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            return;
        if (!Directory.Exists(directoryPath))
            return;
        // Guard against deleting an arbitrary path: only touch directories
        // beneath the captures root so a bug can never eat unrelated data.
        string capturesRoot = Path.Combine(_sessionDirectory, CapturesDirectoryName);
        string full = Path.GetFullPath(directoryPath);
        if (!full.StartsWith(capturesRoot, StringComparison.Ordinal))
            return;
        try { Directory.Delete(full, recursive: true); } catch { }
    }

    // ---- Validation -------------------------------------------------------

    private CaptureBundle? TryOpen(
        string finalDir,
        string captureKey,
        string inputPath,
        long inputLength,
        DateTime inputLastWriteUtc)
    {
        string manifestPath = Path.Combine(finalDir, ManifestFileName);
        if (!File.Exists(manifestPath))
            return null;

        CaptureBundleManifest? manifest;
        try
        {
            manifest = VisualizationManifestWriter.Read(manifestPath);
        }
        catch (Exception)
        {
            return null;
        }

        if (manifest is null)
            return null;
        if (manifest.SchemaVersion != PreviewCacheKey.CaptureBundleSchemaVersion)
            return null;
        if (!string.Equals(manifest.CaptureKey, captureKey, StringComparison.Ordinal))
            return null;
        if (!string.Equals(
                Path.GetFullPath(manifest.InputPath),
                Path.GetFullPath(inputPath),
                StringComparison.Ordinal))
            return null;
        if (manifest.InputLength != inputLength)
            return null;
        if (manifest.InputLastWriteUtcTicks != inputLastWriteUtc.Ticks)
            return null;

        string timelinePath = Path.Combine(finalDir, manifest.TimelineFileName);
        if (!File.Exists(timelinePath))
            return null;

        return new CaptureBundle
        {
            Key = captureKey,
            DirectoryPath = finalDir,
            ManifestPath = manifestPath,
            TimelinePath = timelinePath,
            MasterWavePath = string.IsNullOrWhiteSpace(manifest.MasterWaveFileName)
                ? null
                : Path.Combine(finalDir, manifest.MasterWaveFileName),
            StemsDirectoryPath = string.IsNullOrWhiteSpace(manifest.StemsDirectoryName)
                ? null
                : Path.Combine(finalDir, manifest.StemsDirectoryName),
        };
    }

    private static void ValidateRequired(
        string tempDir,
        CaptureBundleManifest manifest)
    {
        string timelinePath = Path.Combine(tempDir, manifest.TimelineFileName);
        if (!File.Exists(timelinePath))
            throw new InvalidOperationException(
                "Capture bundle is missing its required timeline artifact.");

        string manifestPath = Path.Combine(tempDir, ManifestFileName);
        if (File.Exists(manifestPath)
            && VisualizationManifestWriter.Read(manifestPath) is null)
        {
            throw new InvalidOperationException(
                "Capture bundle has an unreadable manifest.");
        }
    }

    /// <summary>Pairs manifest read/write so both directions use the same schema contract.</summary>
    private static class VisualizationManifestWriter
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

        public static void Write(string path, CaptureBundleManifest manifest)
        {
            string json = JsonSerializer.Serialize(manifest, Json);
            File.WriteAllText(path, json);
        }

        public static CaptureBundleManifest? Read(string path)
            => JsonSerializer.Deserialize<CaptureBundleManifest>(File.ReadAllText(path), Json);
    }
}
