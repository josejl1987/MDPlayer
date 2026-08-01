using Fmp.Application.Contracts;

namespace Fmp.Application.Presets;

/// <summary>Resolved values of a named preset (spec §24.1).</summary>
public sealed record VisualizationPresetDefinition
{
    public required string Name { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int FpsNumerator { get; init; }
    public required VisualizationLayout Layout { get; init; }
    public required VisualizationEffects Effects { get; init; }
    public required AnalysisOverlayMode AnalysisOverlay { get; init; }
    public required ChannelSelectionMode ChannelSelection { get; init; }

    public string DisplayName => Name;
}

/// <summary>
/// Built-in preset catalog. These values are authoritative for BOTH the CLI and
/// the GUI and MUST match the CLI's legacy preset resolution exactly (see
/// Fmp.Core.Visualization.Rendering.VisualizationOptionParsing.PresetValues):
///   Preview    960×540  30 fps  Auto  Minimal   overlay none     Active
///   Final     1920×1080 60 fps  Auto  Cinematic overlay minimal  Active
///   Diagnostic 1920×1080 60 fps  Diagnostic Diagnostic overlay standard All
///   Balanced  1280×720  60 fps  Auto  Minimal   overlay minimal  Active
/// </summary>
public static class VisualizationPresetCatalog
{
    public static IReadOnlyList<VisualizationPresetDefinition> All { get; } = new[]
    {
        new VisualizationPresetDefinition
        {
            Name = nameof(VisualizationPreset.Preview),
            Width = 960, Height = 540, FpsNumerator = 30,
            Layout = VisualizationLayout.Auto,
            Effects = VisualizationEffects.Minimal,
            AnalysisOverlay = AnalysisOverlayMode.None,
            ChannelSelection = ChannelSelectionMode.Active,
        },
        new VisualizationPresetDefinition
        {
            Name = nameof(VisualizationPreset.Balanced),
            Width = 1280, Height = 720, FpsNumerator = 60,
            Layout = VisualizationLayout.Auto,
            Effects = VisualizationEffects.Minimal,
            AnalysisOverlay = AnalysisOverlayMode.Minimal,
            ChannelSelection = ChannelSelectionMode.Active,
        },
        new VisualizationPresetDefinition
        {
            Name = nameof(VisualizationPreset.Final),
            Width = 1920, Height = 1080, FpsNumerator = 60,
            Layout = VisualizationLayout.Auto,
            Effects = VisualizationEffects.Cinematic,
            AnalysisOverlay = AnalysisOverlayMode.Minimal,
            ChannelSelection = ChannelSelectionMode.Active,
        },
        new VisualizationPresetDefinition
        {
            Name = nameof(VisualizationPreset.Diagnostic),
            Width = 1920, Height = 1080, FpsNumerator = 60,
            Layout = VisualizationLayout.Diagnostic,
            Effects = VisualizationEffects.Diagnostic,
            AnalysisOverlay = AnalysisOverlayMode.Standard,
            ChannelSelection = ChannelSelectionMode.All,
        },
    };

    public static VisualizationPresetDefinition Get(VisualizationPreset preset)
        => All.First(definition => definition.Name == preset.ToString());

    /// <summary>
    /// True when the request's visual configuration equals a named preset
    /// (used to show "Custom · based on X" vs a clean preset name).
    /// </summary>
    public static bool Matches(VisualizationRequest request, VisualizationPreset preset)
    {
        VisualizationPresetDefinition definition = Get(preset);
        return request.Width == definition.Width
            && request.Height == definition.Height
            && request.FpsNumerator == definition.FpsNumerator
            && request.Layout == definition.Layout
            && request.Effects == definition.Effects
            && request.AnalysisOverlay == definition.AnalysisOverlay
            && request.ChannelSelection == definition.ChannelSelection;
    }

    /// <summary>
    /// The named preset the request currently matches, or null when the
    /// request is custom (some field deviates from every built-in preset).
    /// </summary>
    public static VisualizationPreset? DetectPreset(VisualizationRequest request)
    {
        foreach (VisualizationPresetDefinition definition in All)
        {
            if (Matches(request, Enum.Parse<VisualizationPreset>(definition.Name)))
                return Enum.Parse<VisualizationPreset>(definition.Name);
        }
        return null;
    }

    /// <summary>
    /// Applies the named preset's values to a fresh request snapshot, keeping
    /// input/output paths, playback settings, metadata and tool overrides.
    /// </summary>
    public static VisualizationRequest Apply(VisualizationRequest request, VisualizationPreset preset)
    {
        VisualizationPresetDefinition definition = Get(preset);
        return request with
        {
            Preset = preset,
            Width = definition.Width,
            Height = definition.Height,
            FpsNumerator = definition.FpsNumerator,
            Layout = definition.Layout,
            Effects = definition.Effects,
            AnalysisOverlay = definition.AnalysisOverlay,
            ChannelSelection = definition.ChannelSelection,
        };
    }
}
