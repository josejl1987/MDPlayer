using Fmp.Application.Contracts;

namespace Fmp.Application.Presets;

/// <summary>Resolved values of a named render-quality profile.</summary>
public sealed record RenderQualityDefinition
{
    public required RenderQuality Quality { get; init; }
    public required int DefaultWidth { get; init; }
    public required int DefaultHeight { get; init; }
    public required int DefaultFpsNumerator { get; init; }
    public required int DefaultFpsDenominator { get; init; }
    public required string Description { get; init; }

    public string DisplayName => Quality.ToString();
}

/// <summary>
/// Built-in render-quality catalog. Quality selects resolution/fps/encoding,
/// never the composition — there is no "Diagnostic" quality preset.
/// </summary>
public static class RenderQualityCatalog
{
    public static IReadOnlyList<RenderQualityDefinition> All { get; } = new[]
    {
        new RenderQualityDefinition
        {
            Quality = RenderQuality.Draft,
            DefaultWidth = 1280,
            DefaultHeight = 720,
            DefaultFpsNumerator = 30,
            DefaultFpsDenominator = 1,
            Description = "Fast export: 720p30 with reduced preview cost.",
        },
        new RenderQualityDefinition
        {
            Quality = RenderQuality.Standard,
            DefaultWidth = 1920,
            DefaultHeight = 1080,
            DefaultFpsNumerator = 60,
            DefaultFpsDenominator = 1,
            Description = "Normal export: 1080p60 with normal antialiasing.",
        },
        new RenderQualityDefinition
        {
            Quality = RenderQuality.Final,
            DefaultWidth = 1920,
            DefaultHeight = 1080,
            DefaultFpsNumerator = 60,
            DefaultFpsDenominator = 1,
            Description = "Highest quality: 1080p60 (or explicit higher resolution) with slower, higher-quality encoding.",
        },
    };

    public static RenderQualityDefinition Get(RenderQuality quality)
        => All.First(definition => definition.Quality == quality);

    /// <summary>Applies the quality profile's defaults to a fresh request snapshot.</summary>
    public static VisualizationRequest ApplyDefaults(VisualizationRequest request, RenderQuality quality)
    {
        RenderQualityDefinition definition = Get(quality);
        return request with
        {
            Output = request.Output with
            {
                Quality = quality,
                Width = definition.DefaultWidth,
                Height = definition.DefaultHeight,
                FpsNumerator = definition.DefaultFpsNumerator,
                FpsDenominator = definition.DefaultFpsDenominator,
            },
        };
    }
}