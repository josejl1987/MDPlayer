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
        VisualizationPreset.Preview => new(960, 540, 30, VisualizationLayoutMode.Auto,
            EffectsMode.Minimal, "none", VisualizationChannelFilter.Active),
        VisualizationPreset.Final => new(1920, 1080, 60, VisualizationLayoutMode.Auto,
            EffectsMode.Cinematic, "minimal", VisualizationChannelFilter.Active),
        VisualizationPreset.Diagnostic => new(1920, 1080, 60, VisualizationLayoutMode.Diagnostic,
            EffectsMode.Diagnostic, "standard", VisualizationChannelFilter.All),
        _ => new(1280, 720, 60, VisualizationLayoutMode.Auto,
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

internal static class VisualizationLayoutModeResolver
{
    public static VisualizationLayoutMode Resolve(
        VisualizationTimeline timeline,
        VisualizationLayoutMode requested)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (requested is (VisualizationLayoutMode.UnifiedRoll
            or VisualizationLayoutMode.Hybrid)
            && timeline.Voices.Any(voice => voice.SupportsPitch
                && !voice.IsNoise
                && !voice.IsPercussion
                && !IsAbsolutePitchSystem(voice.PitchSystem)))
        {
            // An explicit shared composition still cannot invent an absolute
            // coordinate for a relative-only voice. Split is the actionable
            // safe fallback and is reported as the selected layout in the
            // prepared plan.
            return VisualizationLayoutMode.SplitRoll;
        }

        if (requested != VisualizationLayoutMode.Auto)
            return requested;

        HashSet<string> noteChannels = timeline.Notes
            .Where(note => VisualizationActivity.IsMeaningfulNote(timeline, note))
            .Select(note => note.ChannelId)
            .ToHashSet(StringComparer.Ordinal);
        int pitched = timeline.Voices.Count(voice => voice.SupportsPitch
            && !voice.IsNoise
            && !voice.IsPercussion
            && noteChannels.Contains(voice.Id.ToString()));
        int activeVoiceCount = timeline.Voices.Count(voice => noteChannels.Contains(voice.Id.ToString()));
        int nonPitched = Math.Max(0, activeVoiceCount - pitched);
        double nonPitchedRatio = activeVoiceCount == 0
            ? 0
            : nonPitched / (double)activeVoiceCount;
        int semantic = noteChannels.Count
            + timeline.Rhythm.Where(value => value.SamplePosition >= timeline.StartSample
                && value.SamplePosition <= timeline.EndSample)
                .Count()
            + timeline.SamplePlayback.Where(value => value.EndSample > value.StartSample).Count()
            + timeline.NoiseStates.Where(value => value.EndSample > value.StartSample).Count()
            + timeline.AggregateHits.Length
            + (VisualizationTimelineCompatibility.HasLegacyRenderableContent(timeline) ? 1 : 0);
        bool hasScope = timeline.WaveformChanges.Length > 0
            || timeline.Devices.Any(device => device.ScopeSupport != ScopeSupport.None);

        bool pitchModelsCompatible = timeline.Voices
            .Where(voice => voice.SupportsPitch
                && !voice.IsNoise
                && !voice.IsPercussion
                && noteChannels.Contains(voice.Id.ToString()))
            .Select(voice => voice.PitchSystem)
            .All(IsAbsolutePitchSystem);

        if (pitched >= 2 && nonPitchedRatio <= 0.35 && pitchModelsCompatible)
            return VisualizationLayoutMode.UnifiedRoll;

        // Relative-only and otherwise incompatible active pitch models must
        // keep independent cameras. They are never allowed into a shared
        // absolute roll, even when waveform stems are also available.
        if (pitched >= 2 && !pitchModelsCompatible)
            return VisualizationLayoutMode.SplitRoll;
        // A prepared capture with both semantic events and usable waveform
        // stems gets one shared hybrid composition. This keeps the decision
        // stable for the whole video and avoids silently dropping available
        // scope content from an audience-facing render.
        if (semantic > 0 && hasScope)
            return VisualizationLayoutMode.Hybrid;
        if (pitched > 0)
            return VisualizationLayoutMode.SplitRoll;
        if (hasScope || semantic > 0)
            return VisualizationLayoutMode.Scope;
        return VisualizationLayoutMode.Diagnostic;
    }

    private static bool IsAbsolutePitchSystem(PitchCoordinateSystem system)
        => system is PitchCoordinateSystem.AbsoluteMidi
            or PitchCoordinateSystem.AbsoluteSemitone
            or PitchCoordinateSystem.FrequencyHz;
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
        VisualizationLayoutMode.Focus => "auto",
        VisualizationLayoutMode.UnifiedRoll => "unified",
        VisualizationLayoutMode.SplitRoll => "split",
        VisualizationLayoutMode.DiagnosticV2 => "diagnostic-v2",
        _ => mode.ToString().ToLowerInvariant(),
    };
}
