using System.Text.Json;

namespace Fmp.Core.Decoding.SnesDsp;

/// <summary>Writes samples.json (§26.1): one entry per unique sample hash, ordered by hash.
/// encodedBytes is base64; estimatedRootHz is nullable.</summary>
internal static class SpcSamplesJsonWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Serialize(IEnumerable<SpcSampleEntry> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        SpcSampleEntry[] ordered = samples
            .OrderBy(s => s.Hash, StringComparer.Ordinal)
            .Select(s => s with { SourceNumbers = s.SourceNumbers.OrderBy(n => n).ToList() })
            .ToArray();
        return JsonSerializer.Serialize(ordered, Options);
    }

    public static void Write(string path, IEnumerable<SpcSampleEntry> samples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.WriteAllText(path, Serialize(samples));
    }
}
