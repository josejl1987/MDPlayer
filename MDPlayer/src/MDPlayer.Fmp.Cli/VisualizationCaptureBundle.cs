using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fmp.Application.Contracts;
using Fmp.Application.Inspection;
using Fmp.Application.Export;
using Fmp.Core.Rendering;

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
        sb.Append(key.InputLength).Append('\n');
        sb.Append(key.InputLastWriteUtcTicks).Append('\n');
        sb.Append(key.Backend).Append('\n');
        sb.Append(key.LoopCount).Append('\n');
        sb.Append(key.FadeSeconds).Append('\n');
        sb.Append(key.TailSeconds).Append('\n');
        sb.Append(key.MaximumDurationSeconds).Append('\n');
        sb.Append(key.SampleRate).Append('\n');
        sb.Append(key.SsgGainDb).Append('\n');
        sb.Append(key.SpcPitch).Append('\n');

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
        string combined = Path.GetFullPath(Path.Combine(bundleRoot, relativePath));
        return combined.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
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