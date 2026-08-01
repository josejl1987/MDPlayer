namespace Fmp.Core.Visualization.Rendering;

internal enum VisualizationPreset
{
    Preview,
    Balanced,
    Final,
    Diagnostic,
}

internal enum VisualizationChannelFilter
{
    Active,
    Audible,
    Semantic,
    All,
}

internal enum VisualizationScopePosition
{
    Bottom,
    Top,
    Left,
    Right,
}

internal enum VisualizationGroupBy
{
    None,
    Device,
    Family,
}

internal enum VisualizationTimeGrid
{
    None,
    Automatic,
    Authoritative,
    Analytical,
}

internal enum VisualizationTimeScale
{
    Dense,
    Balanced,
    Wide,
}

internal readonly record struct VisualizationPresetValues(
    int Width,
    int Height,
    int Fps,
    VisualizationLayoutMode Layout,
    EffectsMode Effects,
    string AnalysisOverlay,
    VisualizationChannelFilter Channels);

internal static class VisualizationOptionParsing
{
    public static VisualizationPreset ParsePreset(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "preview" => VisualizationPreset.Preview,
        "balanced" => VisualizationPreset.Balanced,
        "final" => VisualizationPreset.Final,
        "diagnostic" => VisualizationPreset.Diagnostic,
        _ => throw new ArgumentException(
            $"unknown preset '{raw}' (expected preview, balanced, final, or diagnostic)"),
    };

    public static VisualizationPresetValues PresetValues(VisualizationPreset preset) => preset switch
    {
        VisualizationPreset.Preview => new(960, 540, 30, VisualizationLayoutMode.Diagnostic,
            EffectsMode.Minimal, "none", VisualizationChannelFilter.Active),
        VisualizationPreset.Final => new(1920, 1080, 60, VisualizationLayoutMode.Diagnostic,
            EffectsMode.Cinematic, "minimal", VisualizationChannelFilter.Active),
        VisualizationPreset.Diagnostic => new(1920, 1080, 60, VisualizationLayoutMode.Diagnostic,
            EffectsMode.Diagnostic, "standard", VisualizationChannelFilter.All),
        _ => new(1280, 720, 60, VisualizationLayoutMode.Diagnostic,
            EffectsMode.Minimal, "minimal", VisualizationChannelFilter.Active),
    };

    public static VisualizationChannelFilter ParseChannels(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "active" => VisualizationChannelFilter.Active,
        "audible" => VisualizationChannelFilter.Audible,
        "semantic" => VisualizationChannelFilter.Semantic,
        "all" => VisualizationChannelFilter.All,
        _ => throw new ArgumentException(
            $"unknown channel filter '{raw}' (expected active, audible, semantic, or all)"),
    };

    public static VisualizationScopePosition ParseScopePosition(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "bottom" => VisualizationScopePosition.Bottom,
        "top" => VisualizationScopePosition.Top,
        "left" => VisualizationScopePosition.Left,
        "right" => VisualizationScopePosition.Right,
        _ => throw new ArgumentException(
            $"unknown scope position '{raw}' (expected bottom, top, left, or right)"),
    };

    public static VisualizationGroupBy ParseGroupBy(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "none" => VisualizationGroupBy.None,
        "device" => VisualizationGroupBy.Device,
        "family" => VisualizationGroupBy.Family,
        _ => throw new ArgumentException(
            $"unknown grouping '{raw}' (expected none, device, or family)"),
    };

    public static VisualizationTimeGrid ParseTimeGrid(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "none" => VisualizationTimeGrid.None,
        "automatic" => VisualizationTimeGrid.Automatic,
        "authoritative" => VisualizationTimeGrid.Authoritative,
        "analytical" => VisualizationTimeGrid.Analytical,
        _ => throw new ArgumentException(
            $"unknown time grid '{raw}' (expected none, automatic, authoritative, or analytical)"),
    };

    public static VisualizationTimeScale ParseTimeScale(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "dense" => VisualizationTimeScale.Dense,
        "balanced" => VisualizationTimeScale.Balanced,
        "wide" => VisualizationTimeScale.Wide,
        _ => throw new ArgumentException(
            $"unknown time scale '{raw}' (expected dense, balanced, or wide)"),
    };

    public static (double Past, double Future) TimeScaleValues(VisualizationTimeScale scale) => scale switch
    {
        VisualizationTimeScale.Dense => (0.40, 1.60),
        VisualizationTimeScale.Wide => (1.50, 4.50),
        _ => (0.75, 2.25),
    };

    public static (double Past, double Future) ParseTimeWindow(string raw)
    {
        string[] parts = raw?.Split(':', StringSplitOptions.TrimEntries);
        if (parts is not [var past, var future]
            || !double.TryParse(past, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double pastSeconds)
            || !double.TryParse(future, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double futureSeconds))
            throw new ArgumentException("--time-window must use the form <past:future>");
        return (pastSeconds, futureSeconds);
    }
}

internal static class VisualizationContentAvailability
{
    public static bool HasRenderableContent(VisualizationTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        return timeline.Notes.Any(note => VisualizationActivity.IsMeaningfulNote(timeline, note))
            || timeline.Rhythm.Any(value => value.SamplePosition >= timeline.StartSample
                && value.SamplePosition <= timeline.EndSample)
            || timeline.SamplePlayback.Any(value => value.EndSample > value.StartSample)
            || timeline.NoiseStates.Any(value => value.EndSample > value.StartSample)
            || timeline.AggregateHits.Length > 0
            || timeline.WaveformChanges.Length > 0
            || timeline.Devices.Any(device => device.ScopeSupport != ScopeSupport.None)
            || VisualizationTimelineCompatibility.HasLegacyRenderableContent(timeline);
    }
}

internal static class VisualizationLayoutNames
{
    public static string ToCliName(VisualizationLayoutMode mode) => mode switch
    {
        VisualizationLayoutMode.Diagnostic => "diagnostic",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
