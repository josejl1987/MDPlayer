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
    /// Capture-stage key: canonical input path + length + last-write time +
    /// capture-affecting options. A cache hit MUST never bypass schema or
    /// decoder-version validation (callers re-validate first).
    /// </summary>
    public static string CaptureKey(
        string inputPath,
        long inputLength,
        DateTime lastWriteUtc,
        VisualizationRequest request)
    {
        var builder = new StringBuilder();
        builder.Append("capture|v1|");
        builder.Append(Path.GetFullPath(inputPath));
        builder.Append('|').Append(inputLength);
        builder.Append('|').Append(lastWriteUtc.Ticks);
        builder.Append('|').Append(request.Playback.LoopCount);
        builder.Append('|').Append(request.Playback.FadeSeconds.ToString("R"));
        builder.Append('|').Append(request.Playback.TailSeconds.ToString("R"));
        builder.Append('|').Append(request.Playback.MaximumDurationSeconds?.ToString("R") ?? "-");
        builder.Append('|').Append(request.Playback.SampleRate);
        return Sha1Hex(builder.ToString());
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
