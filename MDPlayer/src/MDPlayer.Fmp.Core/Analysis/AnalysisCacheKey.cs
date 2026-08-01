using System.Security.Cryptography;
using System.Text;

namespace Fmp.Core.Analysis;

internal static class AnalysisCacheKey
{
    public static string ComputeInputHash(AnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(AnalysisJson.CanonicalizeWithoutId(input))))
            .ToLowerInvariant();
    }

    public static string Compute(
        AnalysisInput input,
        string normalizerVersion,
        string workerVersion,
        string music21Version,
        string partituraVersion,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(input);
        string payload = AnalysisJson.Serialize(new
        {
            InputCanonicalJson = AnalysisJson.CanonicalizeWithoutId(input),
            NormalizerVersion = normalizerVersion ?? "",
            InputSchemaVersion = input.SchemaVersion,
            Detail = detail ?? "standard",
            WorkerVersion = workerVersion ?? "",
            Music21Version = music21Version ?? "",
            PartituraVersion = partituraVersion ?? "",
        }, indented: false);
        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}

internal sealed class AnalysisCacheMetadata
{
    public int SchemaVersion { get; init; } = 1;
    public string InputHash { get; init; } = "";
    public string NormalizerVersion { get; init; } = "";
    public string WorkerVersion { get; init; } = "";
    public string RequiredMusic21Version { get; init; } = "";
    public string AnalysisDetail { get; init; } = "";
    public string OutputHash { get; init; } = "";
}
