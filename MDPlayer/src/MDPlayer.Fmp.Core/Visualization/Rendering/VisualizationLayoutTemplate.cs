using System.Text.Json;

namespace Fmp.Core.Visualization.Rendering;

internal enum VisualizationRendererMode
{
    Auto,
    Cpu,
    Gpu,
}

/// <summary>Portable JSON composition template for repeatable publishing.</summary>
internal sealed record VisualizationLayoutTemplate(
    string Layout,
    string Channels,
    string GroupBy,
    string ScopePosition,
    double? ScopeRatio,
    double? PastSeconds,
    double? FutureSeconds,
    double? RollZoom)
{
    public static VisualizationLayoutTemplate ParseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("layout template JSON is empty", nameof(json));
        return JsonSerializer.Deserialize<VisualizationLayoutTemplate>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new ArgumentException("layout template JSON is empty");
    }
}
