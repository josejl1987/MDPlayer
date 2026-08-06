using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fmp.Application.Contracts;
using Fmp.Application.Inspection;
using Fmp.Application.Export;
using Fmp.Core.Audio;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;

namespace Fmp.Cli;

/// <summary>
/// A published, reusable preview capture bundle: the session's timeline, master
/// WAV, scope metadata and successful stems recorded in a single
/// <c>capture-manifest.json</c> plus the referenced artifacts, all under one
/// bundle root. Load/validation is used by the render CLI to build final render
/// assets from an existing capture without replaying capture or regenerating
/// stems.
/// </summary>
internal static class VisualizationCaptureBundle
{
    public const string ManifestFileName = "capture-manifest.json";
    public const int SchemaVersion = 1;

    public static string ManifestPath(string bundleRoot)
        => Path.Combine(bundleRoot, ManifestFileName);

    /// <summary>
    /// Creates a determinist capture key for a <see cref="TimelineCaptureKey"/>
    /// by hashing a canonical invariant string (all fields in declaration order)
    /// with SHA-256. No general hashing service is introduced.
    /// </summary>
    public static string ComputeCaptureKey(TimelineCaptureKey key)
    {
        // TimelineCaptureKey is a record struct: its string ordering matches
        // field declaration order in ToString(). Build one canonical string from
        // the declared fields explicitly to stay robust.
        var sb = new StringBuilder();
        sb.Append(key.InputPath).Append('\n');
        sb.Append(key.InputLength.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(key.InputLastWriteUtcTicks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(key.Backend).Append('\n');
        sb.Append(key.LoopCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(key.FadeSeconds.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(key.TailSeconds.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(key.MaximumDurationSeconds?.ToString(
            "R",
            CultureInfo.InvariantCulture) ?? "<null>").Append('\n');
        sb.Append(key.SampleRate.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(key.SsgGainDb.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(((int)key.SpcPitch).ToString(CultureInfo.InvariantCulture)).Append('\n');

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async Task WriteManifestAsync(
        string bundleRoot,
        string captureKey,
        VisualizationRequest request,
        FileInfo input,
        string backendId,
        string timelinePath,
        string masterAudioPath,
        string scopeMetadataPath,
        IReadOnlyList<ScopeRenderer.StemResult> successfulStems,
        int sampleRate,
        long masterSamples,
        bool scopeEnabled,
        bool hasIsolatedStems,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        long length = input.Exists ? input.Length : -1;
        long lastWriteTicks = input.Exists ? input.LastWriteTimeUtc.Ticks : 0;

        var manifest = new CaptureManifest
        {
            SchemaVersion = SchemaVersion,
            CaptureKey = captureKey,
            Input = new CaptureInput
            {
                Path = Path.GetFullPath(request.InputPath),
                Length = length,
                LastWriteUtcTicks = lastWriteTicks,
            },
            BackendId = backendId,
            TimelinePath = RelativeTo(bundleRoot, timelinePath),
            MasterAudioPath = RelativeTo(bundleRoot, masterAudioPath),
            ScopeMetadataPath = RelativeTo(bundleRoot, scopeMetadataPath),
            Stems = successfulStems
                .Select(stem => new CaptureStem
                {
                    Name = stem.Name,
                    Label = stem.Label,
                    PresentationTrackId = stem.PresentationTrackId,
                    SemanticClass = stem.SemanticClass.ToString(),
                    StableOrder = stem.StableOrder,
                    WindowWidth = stem.WindowWidth,
                    DefaultAmplification = stem.DefaultAmplification,
                    DefaultColor = stem.DefaultColor,
                    WavPath = RelativeTo(bundleRoot, stem.WavPath),
                    RenderedSamples = stem.RenderedSamples,
                    Channels = stem.Channels,
                }).ToList(),
            SampleRate = sampleRate,
            MasterSamples = masterSamples,
            ScopeEnabled = scopeEnabled,
            HasIsolatedStems = hasIsolatedStems,
        };

        await WriteJsonAtomicAsync(bundleRoot, ManifestFileName, manifest, cancellationToken);
    }

    private static async Task WriteJsonAtomicAsync<T>(
        string bundleRoot,
        string fileName,
        T value,
        CancellationToken cancellationToken)
    {
        string final = Path.Combine(bundleRoot, fileName);
        string partial = final + ".partial";
        string json = JsonSerializer.Serialize(
            value,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            });
        await File.WriteAllTextAsync(partial, json, cancellationToken);
        File.Move(partial, final, overwrite: true);
    }

    /// <summary>Returns a bundle-relative path, throwing if the artifact lies outside the root.</summary>
    public static string RelativeTo(string bundleRoot, string absolutePath)
    {
        string fullRoot = Path.GetFullPath(bundleRoot);
        string fullPath = Path.GetFullPath(absolutePath);
        string relative = Path.GetRelativePath(fullRoot, fullPath);

        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith("../", StringComparison.Ordinal)
            || relative.StartsWith("..\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"artifact '{absolutePath}' lies outside the bundle root '{bundleRoot}'.");
        }

        return relative;
    }

    /// <summary>Validates containment for an already-relative path from a manifest.</summary>
    public static bool IsInsideBundle(string bundleRoot, string relativePath)
    {
        string fullRoot = Path.GetFullPath(bundleRoot);
        string fullCandidate = Path.GetFullPath(Path.Combine(bundleRoot, relativePath));
        if (string.Equals(fullCandidate, fullRoot, StringComparison.Ordinal))
            return false; // an artifact equal to the root is not a valid file artifact

        string relative = Path.GetRelativePath(fullRoot, fullCandidate);
        if (Path.IsPathRooted(relative))
            return false;
        if (relative == "..")
            return false;
        if (relative.StartsWith("../", StringComparison.Ordinal))
            return false;
        if (relative.StartsWith("..\\", StringComparison.Ordinal))
            return false;
        return true;
    }

    /// <summary>Resolves a bundle-relative manifest path to an absolute path inside the root.</summary>
    public static string ResolveInside(string bundleRoot, string relativePath)
    {
        if (!IsInsideBundle(bundleRoot, relativePath))
        {
            throw new VisualizationExecutionException(
                $"capture bundle artifact escapes the bundle root: '{relativePath}'",
                2, "INVALID_REQUEST");
        }
        return Path.GetFullPath(Path.Combine(bundleRoot, relativePath));
    }

    /// <summary>
    /// Loads and validates a published capture bundle for an explicitly supplied
    /// <c>--capture-dir</c>/<c>--capture-key</c> pair and reconstructs the
    /// <see cref="PreparedCapture"/> for final render without replaying semantic
    /// capture or regenerating stems. Any invalid or incomplete bundle fails
    /// hard; the caller must never fall back to a fresh capture.
    /// </summary>
    public static async Task<PreparedCapture> LoadPreparedCaptureAsync(
        string bundleRoot,
        string expectedKey,
        VisualizationRequest request,
        string backendId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Directory.Exists(bundleRoot))
        {
            throw new VisualizationExecutionException(
                $"capture bundle directory does not exist: '{bundleRoot}'",
                2, "INVALID_REQUEST");
        }

        string manifestFile = ManifestPath(bundleRoot);
        if (!File.Exists(manifestFile))
        {
            throw new VisualizationExecutionException(
                $"capture bundle is missing '{ManifestFileName}': '{bundleRoot}'",
                2, "INVALID_REQUEST");
        }

        string json = await File.ReadAllTextAsync(manifestFile, cancellationToken);
        CaptureManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CaptureManifest>(
                json,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                ?? throw new JsonException("manifest is empty");
        }
        catch (JsonException ex)
        {
            throw new VisualizationExecutionException(
                $"capture bundle manifest is not valid JSON: {ex.Message}",
                2, "INVALID_REQUEST");
        }

        // Structural validation must hold before any field is trusted.
        ValidateManifestStructure(manifest);

        if (manifest.SchemaVersion != SchemaVersion)
        {
            throw new VisualizationExecutionException(
                $"capture bundle schema version {manifest.SchemaVersion} is unsupported",
                2, "INVALID_REQUEST");
        }
        if (!string.Equals(manifest.CaptureKey, expectedKey, StringComparison.Ordinal))
        {
            throw new VisualizationExecutionException(
                "capture bundle key does not match --capture-key",
                2, "INVALID_REQUEST");
        }

        // Input identity must match the request exactly.
        string requestInput = Path.GetFullPath(request.InputPath);
        if (!string.Equals(
                Path.GetFullPath(manifest.Input.Path),
                requestInput,
                StringComparison.Ordinal))
        {
            throw new VisualizationExecutionException(
                "capture bundle was captured for a different input path",
                2, "INVALID_REQUEST");
        }
        FileInfo input = new(requestInput);
        long length = input.Exists ? input.Length : -1;
        long lastWriteTicks = input.Exists ? input.LastWriteTimeUtc.Ticks : 0;
        if (manifest.Input.Length != length || manifest.Input.LastWriteUtcTicks != lastWriteTicks)
        {
            throw new VisualizationExecutionException(
                "capture bundle input identity no longer matches the request input",
                2, "INVALID_REQUEST");
        }

        // Backend identity must match the resolved backend.
        if (!string.Equals(manifest.BackendId, backendId, StringComparison.Ordinal))
        {
            throw new VisualizationExecutionException(
                $"capture bundle backend '{manifest.BackendId}' does not match resolved backend '{backendId}'",
                2, "INPUT_UNSUPPORTED");
        }

        // Validate containment and existence of every artifact.
        string bundleRootFull = Path.GetFullPath(bundleRoot);
        ValidateRelativePath(bundleRootFull, manifest.TimelinePath);
        ValidateRelativePath(bundleRootFull, manifest.MasterAudioPath);
        ValidateRelativePath(bundleRootFull, manifest.ScopeMetadataPath);
        foreach (CaptureStem stem in manifest.Stems)
            ValidateRelativePath(bundleRootFull, stem.WavPath);

        RequireArtifact(bundleRootFull, manifest.TimelinePath, "timeline");
        RequireArtifact(bundleRootFull, manifest.MasterAudioPath, "master audio");
        // Only FMP parallel-synthesis captures write a scope `metadata.json`;
        // VGM/register captures render scopes straight from the published
        // isolated stems and never produce that artifact. The metadata file is a
        // presence guard, not used to reconstruct scopes, so it must only be
        // required for the backend that actually writes it.
        if (manifest.ScopeEnabled && string.Equals(manifest.BackendId, "fmp", StringComparison.Ordinal))
            RequireArtifact(bundleRootFull, manifest.ScopeMetadataPath, "scope metadata");
        foreach (CaptureStem stem in manifest.Stems)
            RequireArtifact(bundleRootFull, stem.WavPath, "stem WAV");

        VisualizationTimeline timeline =
            VisualizationJsonWriter.Read(Path.Combine(bundleRootFull, manifest.TimelinePath));

        var scopeResult = new ScopeRenderer.ScopeResult
        {
            Success = true,
            InputPath = request.InputPath,
            OutputDir = bundleRootFull,
            SampleRate = manifest.SampleRate,
            MasterSamples = manifest.MasterSamples,
            CompletionReason = "loaded-from-bundle",
            Stems = manifest.Stems
                .Select(stem => new ScopeRenderer.StemResult
                {
                    Name = stem.Name,
                    Label = stem.Label,
                    PresentationTrackId = stem.PresentationTrackId,
                    SemanticClass = ParseSemanticClass(stem.SemanticClass),
                    StableOrder = stem.StableOrder,
                    WindowWidth = stem.WindowWidth,
                    DefaultAmplification = stem.DefaultAmplification,
                    DefaultColor = stem.DefaultColor,
                    WavPath = Path.Combine(bundleRootFull, stem.WavPath),
                    RenderedSamples = stem.RenderedSamples,
                    Channels = stem.Channels,
                    // Only successful stems are ever published to the bundle, so
                    // every reconstructed stem is audio-bearing.
                    Success = true,
                }).ToList(),
        };

        StemPlan plan = ScopePlanner.Plan(backendId, timeline.Devices, timeline.Voices, "auto");
        var artifacts = new VisualizationScopeArtifacts(
            plan,
            scopeResult,
            manifest.ScopeEnabled,
            manifest.HasIsolatedStems);

        return new PreparedCapture(
            timeline,
            artifacts,
            backendId,
            Path.Combine(bundleRootFull, manifest.MasterAudioPath));
    }

    private static void ValidateRelativePath(string bundleRoot, string relativePath)
    {
        if (!IsInsideBundle(bundleRoot, relativePath))
        {
            throw new VisualizationExecutionException(
                $"capture bundle artifact escapes the bundle root: '{relativePath}'",
                2, "INVALID_REQUEST");
        }
    }

    private static void RequireArtifact(string bundleRoot, string relativePath, string kind)
    {
        string full = Path.GetFullPath(Path.Combine(bundleRoot, relativePath));
        if (!File.Exists(full))
        {
            throw new VisualizationExecutionException(
                $"capture bundle is missing {kind} artifact: '{relativePath}'",
                2, "INVALID_REQUEST");
        }
    }

    private static ScopeSemanticClass ParseSemanticClass(string value)
    {
        if (!Enum.TryParse(
                value,
                ignoreCase: false,
                out ScopeSemanticClass semanticClass)
            || !Enum.IsDefined(semanticClass))
        {
            throw new VisualizationExecutionException(
                $"capture bundle stem has unknown semantic class: '{value}'",
                2, "INVALID_REQUEST");
        }
        return semanticClass;
    }

    /// <summary>
    /// Validates the structural invariants of a capture manifest before any
    /// field is trusted. Invalid structure always fails with exit code 2 and
    /// must never let a <see cref="NullReferenceException"/> escape.
    /// </summary>
    private static void ValidateManifestStructure(CaptureManifest manifest)
    {
        if (manifest is null)
            throw new VisualizationExecutionException(
                "capture bundle manifest is empty", 2, "INVALID_REQUEST");
        if (manifest.Stems is null)
            throw new VisualizationExecutionException(
                "capture bundle manifest is missing its stem collection",
                2, "INVALID_REQUEST");
        if (string.IsNullOrWhiteSpace(manifest.CaptureKey))
            throw new VisualizationExecutionException(
                "capture bundle manifest is missing its capture key", 2, "INVALID_REQUEST");
        if (string.IsNullOrWhiteSpace(manifest.BackendId))
            throw new VisualizationExecutionException(
                "capture bundle manifest is missing its backend id", 2, "INVALID_REQUEST");
        if (string.IsNullOrWhiteSpace(manifest.TimelinePath))
            throw new VisualizationExecutionException(
                "capture bundle manifest is missing its timeline path", 2, "INVALID_REQUEST");
        if (string.IsNullOrWhiteSpace(manifest.MasterAudioPath))
            throw new VisualizationExecutionException(
                "capture bundle manifest is missing its master audio path", 2, "INVALID_REQUEST");
        if (manifest.SampleRate <= 0)
            throw new VisualizationExecutionException(
                "capture bundle manifest has a non-positive sample rate", 2, "INVALID_REQUEST");
        if (manifest.MasterSamples <= 0)
            throw new VisualizationExecutionException(
                "capture bundle manifest has non-positive master samples", 2, "INVALID_REQUEST");

        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (CaptureStem stem in manifest.Stems)
        {
            if (string.IsNullOrWhiteSpace(stem.Name))
                throw new VisualizationExecutionException(
                    "capture bundle manifest contains a stem with an empty name", 2, "INVALID_REQUEST");
            if (string.IsNullOrWhiteSpace(stem.WavPath))
                throw new VisualizationExecutionException(
                    "capture bundle manifest contains a stem with an empty wav path", 2, "INVALID_REQUEST");
            if (stem.RenderedSamples <= 0)
                throw new VisualizationExecutionException(
                    $"capture bundle stem '{stem.Name}' has non-positive rendered samples", 2, "INVALID_REQUEST");
            if (stem.Channels <= 0)
                throw new VisualizationExecutionException(
                    $"capture bundle stem '{stem.Name}' has non-positive channels", 2, "INVALID_REQUEST");
            if (!seenNames.Add(stem.Name))
                throw new VisualizationExecutionException(
                    $"capture bundle manifest contains duplicate stem name: '{stem.Name}'",
                    2, "INVALID_REQUEST");
            if (!seenPaths.Add(stem.WavPath))
                throw new VisualizationExecutionException(
                    $"capture bundle manifest contains duplicate stem path: '{stem.WavPath}'",
                    2, "INVALID_REQUEST");
        }
    }
}

/// <summary>Describes a successful stem within a capture bundle.</summary>
internal sealed class CaptureStem
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string PresentationTrackId { get; set; } = "";
    public string SemanticClass { get; set; } = "Mixed";
    public int StableOrder { get; set; } = int.MaxValue;
    public int WindowWidth { get; set; } = 1;
    public double DefaultAmplification { get; set; } = 1.0;
    public string DefaultColor { get; set; } = "";
    public string WavPath { get; set; } = "";
    public long RenderedSamples { get; set; }
    public int Channels { get; set; }
}

internal sealed class CaptureInput
{
    public string Path { get; set; } = "";
    public long Length { get; set; }
    public long LastWriteUtcTicks { get; set; }
}

/// <summary>Root of a published capture bundle manifest.</summary>
internal sealed class CaptureManifest
{
    public int SchemaVersion { get; set; }
    public string CaptureKey { get; set; } = "";
    public CaptureInput Input { get; set; } = new();
    public string BackendId { get; set; } = "";
    public string TimelinePath { get; set; } = "";
    public string MasterAudioPath { get; set; } = "";
    public string ScopeMetadataPath { get; set; } = "";
    public List<CaptureStem> Stems { get; set; } = new();
    public int SampleRate { get; set; }
    public long MasterSamples { get; set; }
    public bool ScopeEnabled { get; set; }
    public bool HasIsolatedStems { get; set; }
}