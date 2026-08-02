using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fmp.Application.Contracts;

namespace Fmp.Application.Preview;

/// <summary>
/// Deterministic cache-key model (spec §18.2). Keys are produced from the
/// request + input identity + preview parameters. The process-based preview
/// session uses these to name session-workspace artifacts; an in-process
/// implementation would use the same keys for a real preview cache.
/// </summary>
public static class PreviewCacheKey
{
    /// <summary>
    /// Bumped manually whenever capture semantics change without a
    /// request-schema change, so persisted artifacts from an older capture
    /// implementation are never reused for a newer one.
    /// </summary>
    internal const int CaptureImplementationVersion = 1;

    /// <summary>Version of the on-disk capture bundle layout and manifest schema.</summary>
    internal const int CaptureBundleSchemaVersion = 1;

    /// <summary>Directory-safe prefix for capture keys produced by this implementation.</summary>
    internal const string CaptureKeyPrefix = "capture-v2-";

    /// <summary>
    /// Complete capture fingerprint: canonical full input path, input identity,
    /// version identity, every playback/capture-affecting setting, and the
    /// capture-affecting track settings. A bundle validated against this key is
    /// guaranteed to reflect the exact playback + capture settings that produced
    /// it; visual-only settings (composition, dimensions, fps, encoder, quality,
    /// timing window, effects, palette, note color, presentation text) are
    /// deliberately excluded so changing them never invalidates a capture.
    /// </summary>
    public static string CaptureKey(
        string inputPath,
        long inputLength,
        DateTime lastWriteUtc,
        VisualizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var builder = new StringBuilder();
        builder.Append("capture|v2|");
        builder.Append(Path.GetFullPath(inputPath));
        builder.Append('|').Append(inputLength);
        builder.Append('|').Append(lastWriteUtc.Ticks);
        builder.Append('|').Append(CaptureBundleSchemaVersion);
        builder.Append('|').Append(request.SchemaVersion);
        builder.Append('|').Append(CaptureImplementationVersion);

        PlaybackSettings playback = request.Playback;
        builder.Append('|').Append(playback.LoopCount);
        builder.Append('|').Append(playback.FadeSeconds.ToString("R", CultureInfo.InvariantCulture));
        builder.Append('|').Append(playback.TailSeconds.ToString("R", CultureInfo.InvariantCulture));
        builder.Append('|').Append(playback.MaximumDurationSeconds?.ToString("R", CultureInfo.InvariantCulture) ?? "-");
        builder.Append('|').Append(playback.SampleRate);
        builder.Append('|').Append(playback.SsgGainDb.ToString("R", CultureInfo.InvariantCulture));
        builder.Append('|').Append(playback.SpcPitch);

        TrackSettings tracks = request.Tracks;
        builder.Append('|').Append(tracks.Selection);
        builder.Append('|').Append(JoinOrdinal(tracks.IncludedIds));
        builder.Append('|').Append(JoinOrdinal(tracks.ExcludedIds));
        builder.Append('|').Append(tracks.IncludeInactiveDiagnosticTracks ? 1 : 0);

        string hash = Sha256Hex(builder.ToString());
        return CaptureKeyPrefix + hash;
    }

    /// <summary>Frame-stage key: request identity + time + dimensions + fidelity.</summary>
    public static string FrameKey(
        VisualizationRequest request,
        double timeSeconds,
        int width,
        int height,
        PreviewFidelity fidelity)
    {
        var builder = new StringBuilder();
        builder.Append("frame|v1|");
        builder.Append(RequestHash(request));
        builder.Append('|').Append(timeSeconds.ToString("R"));
        builder.Append('|').Append(width).Append('x').Append(height);
        builder.Append('|').Append(fidelity);
        return Sha1Hex(builder.ToString());
    }

    /// <summary>Stable content hash of the canonical request JSON.</summary>
    public static string RequestHash(VisualizationRequest request)
        => Sha1Hex(JsonSerializer.Serialize(request, RequestJson.Options));

    /// <summary>Joins an ID collection by ordinal, sorted so reordering the
    /// collection does not change the capture key.</summary>
    private static string JoinOrdinal(IReadOnlyList<string> values)
    {
        if (values is null || values.Count == 0)
            return "-";
        string[] sorted = values
            .Where(static v => !string.IsNullOrEmpty(v))
            .OrderBy(static v => v, StringComparer.Ordinal)
            .ToArray();
        return string.Join(",", sorted);
    }

    private static string Sha256Hex(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Sha1Hex(string value)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// Shared JSON serializer options for request/project payloads. Enums as
/// strings, camelCase property names, indented for readability.
/// </summary>
public static class RequestJson
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };
}
