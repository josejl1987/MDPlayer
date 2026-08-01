using Fmp.Application.Contracts;

namespace Fmp.Application.Export;

/// <summary>Display modes for the canonical command (spec §20.3).</summary>
public enum CommandDisplayMode
{
    /// <summary>Omit options equal to CLI defaults (short, readable).</summary>
    Compact,

    /// <summary>Emit every resolved setting required for reproducibility.</summary>
    FullyResolved,
}

/// <summary>Formats a request as the canonical, runnable CLI command.</summary>
public interface IVisualizationCommandFormatter
{
    CanonicalCommand Format(VisualizationRequest request, CommandDisplayMode mode = CommandDisplayMode.Compact);
}

/// <summary>
/// Canonical command formatter. Option order is stable (spec §20.2):
/// command → input → output → preset/layout → resolution/fps → channels →
/// time → scopes → style → analysis → metadata → playback → encoder/tools →
/// overwrite/diagnostics. The CLI parser MUST round-trip every emitted option
/// (parity tests enforce this).
/// </summary>
public sealed class VisualizationCommandFormatter : IVisualizationCommandFormatter
{
    public const string ExecutableName = "mdplayer-render";

    private static readonly TimeWindow Defaults = new(0.75, 2.25);

    public CanonicalCommand Format(VisualizationRequest request, CommandDisplayMode mode = CommandDisplayMode.Compact)
    {
        ArgumentNullException.ThrowIfNull(request);
        var arguments = new List<string> { "visualize", request.InputPath };
        var compact = mode == CommandDisplayMode.Compact;

        // 3. Output: -o <dir> --video <path>. Compact omits -o when it equals
        // the CLI's default "<input>.visualization" directory.
        string? outputDir = OutputDirectory(request);
        string defaultDir = DefaultOutputDirectory(request.InputPath);
        if (compact && outputDir != null
            && string.Equals(outputDir, defaultDir, StringComparison.Ordinal))
        {
            // Omit -o; still emit --video unless it equals the default name.
            string defaultVideo = Path.Combine(defaultDir, "visualization.mp4");
            if (!string.Equals(request.OutputPath, defaultVideo, StringComparison.Ordinal))
                arguments.AddRange(new[] { "--video", request.OutputPath });
        }
        else
        {
            arguments.AddRange(new[] { "-o", outputDir ?? defaultDir, "--video", request.OutputPath });
        }

        // 4. Preset and layout.
        AddIf(arguments, "--preset", PresetName(request.Preset), compact, request.Preset != VisualizationPreset.Balanced);
        AddIf(arguments, "--layout", LayoutName(request.Layout), compact, request.Layout != VisualizationLayout.Auto);

        // 5. Resolution and frame rate.
        AddIf(arguments, "--width", request.Width.ToString(), compact, request.Width != 1280);
        AddIf(arguments, "--height", request.Height.ToString(), compact, request.Height != 720);
        AddIf(arguments, "--fps", request.FpsNumerator.ToString(), compact, request.FpsNumerator != 60);
        AddIf(arguments, "--fps-denominator", request.FpsDenominator.ToString(), compact, request.FpsDenominator != 1);

        // 6. Channel selection.
        if (request.ChannelSelection == ChannelSelectionMode.Custom)
        {
            arguments.Add("--channels");
            arguments.Add("all");
            foreach (string trackId in request.IncludedTrackIds)
                arguments.AddRange(new[] { "--include-track", trackId });
            foreach (string trackId in request.ExcludedTrackIds)
                arguments.AddRange(new[] { "--exclude-track", trackId });
        }
        else
        {
            AddIf(arguments, "--channels", ChannelName(request.ChannelSelection), compact,
                request.ChannelSelection != ChannelSelectionMode.Active);
        }

        // 7. Time settings.
        AddIf(arguments, "--past-seconds", Format(request.PastSeconds), compact, request.PastSeconds != Defaults.Past);
        AddIf(arguments, "--future-seconds", Format(request.FutureSeconds), compact, request.FutureSeconds != Defaults.Future);

        // 8. Scope settings.
        AddIf(arguments, "--scope-ratio", Format(request.ScopeRatio), compact, request.ScopeRatio is not null);
        AddIf(arguments, "--scope-position", ScopePositionName(request.ScopePosition), compact, request.ScopePosition != ScopePosition.Bottom);
        AddIf(arguments, "--group-by", GroupByName(request.Grouping), compact, request.Grouping != TrackGroupingMode.None);
        AddIf(arguments, "--time-grid", TimeGridName(request.TimeGrid), compact, request.TimeGrid != TimeGridMode.Automatic);
        AddIf(arguments, "--roll-zoom", Format(request.RollZoom), compact, request.RollZoom != 1.0);

        // 9. Style.
        AddIf(arguments, "--effects", EffectsName(request.Effects), compact, request.Effects != VisualizationEffects.Minimal);
        AddIf(arguments, "--note-color", NoteColorName(request.NoteColor), compact, request.NoteColor != NoteColorMode.Instrument);
        AddIf(arguments, "--font", request.FontPath, compact, !string.IsNullOrEmpty(request.FontPath));

        // 10. Analysis.
        if (request.AnalysisEnabled)
            arguments.Add("--analysis");
        AddIf(arguments, "--analysis-detail", AnalysisDetailName(request.AnalysisDetail), compact, request.AnalysisDetail != AnalysisDetail.Standard);
        AddIf(arguments, "--analysis-overlay", AnalysisOverlayName(request.AnalysisOverlay), compact, request.AnalysisOverlay != AnalysisOverlayMode.Minimal);
        AddIf(arguments, "--analysis-python", request.Tools.AnalysisPython, compact, !string.IsNullOrEmpty(request.Tools.AnalysisPython));
        AddIf(arguments, "--analysis-cache", request.Tools.AnalysisCache, compact, !string.IsNullOrEmpty(request.Tools.AnalysisCache));
        if (request.Tools.AnalysisForce)
            arguments.Add("--analysis-force");
        AddIf(arguments, "--analysis-timeout-minutes", request.Tools.AnalysisTimeoutMinutes?.ToString(), compact, request.Tools.AnalysisTimeoutMinutes is not null);

        // 11. Metadata.
        AddIf(arguments, "--title", request.Title, compact, !string.IsNullOrEmpty(request.Title));
        AddIf(arguments, "--subtitle", request.Subtitle, compact, !string.IsNullOrEmpty(request.Subtitle));
        AddIf(arguments, "--credits", request.Credits, compact, !string.IsNullOrEmpty(request.Credits));

        // 12. Playback.
        AddIf(arguments, "--loops", request.LoopCount.ToString(), compact, request.LoopCount != 2);
        AddIf(arguments, "--fade", Format(request.FadeSeconds), compact, request.FadeSeconds != 5.0);
        AddIf(arguments, "--tail", Format(request.TailSeconds), compact, request.TailSeconds != 0.5);
        AddIf(arguments, "--max-duration", Format(request.MaximumDurationSeconds), compact, request.MaximumDurationSeconds != 300.0);
        AddIf(arguments, "--timeout", Format(request.TimeoutSeconds), compact, request.TimeoutSeconds is not null);
        AddIf(arguments, "--sample-rate", request.SampleRate.ToString(), compact, request.SampleRate != 44_100);
        AddIf(arguments, "--ssg-gain-db", Format(request.SsgGainDb), compact, request.SsgGainDb != 0);
        AddIf(arguments, "--spc-pitch", SpcPitchName(request.SpcPitch), compact, request.SpcPitch != SpcPitchMode.Estimate);
        AddIf(arguments, "--backend", BackendName(request.Backend), compact, request.Backend != BackendPreference.Auto);
        AddIf(arguments, "--scopes", ScopeModeName(request.ScopeMode), compact, request.ScopeMode != ScopeMode.Auto);

        // 13. Encoder and tools.
        AddIf(arguments, "--encoder", EncoderName(request.Encoder), compact, request.Encoder != VideoEncoder.Auto);
        AddIf(arguments, "--corrscope", request.Tools.CorrscopePath, compact, !string.IsNullOrEmpty(request.Tools.CorrscopePath));
        AddIf(arguments, "--ffmpeg", request.Tools.FfmpegPath, compact, !string.IsNullOrEmpty(request.Tools.FfmpegPath));
        if (request.FinalQuality || request.Preset == VisualizationPreset.Final)
            arguments.Add("--final-quality");
        if (request.StemsOnly)
            arguments.Add("--stems-only");
        AddIf(arguments, "--tool-timeout-minutes", request.Tools.ToolTimeoutMinutes?.ToString(), compact, request.Tools.ToolTimeoutMinutes is not null);

        // 14. Overwrite and diagnostics.
        if (request.Overwrite)
            arguments.Add("--overwrite");

        return new CanonicalCommand
        {
            Executable = ExecutableName,
            Arguments = arguments,
            DisplayText = BuildDisplayText(arguments),
        };
    }

    private sealed record TimeWindow(double Past, double Future);

    private static string? OutputDirectory(VisualizationRequest request)
    {
        try
        {
            string? directory = Path.GetDirectoryName(request.OutputPath);
            return string.IsNullOrEmpty(directory) ? null : Path.GetFullPath(directory);
        }
        catch
        {
            return null;
        }
    }

    private static string DefaultOutputDirectory(string inputPath)
    {
        string? inputDir = Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? ".";
        return Path.Combine(inputDir, Path.GetFileNameWithoutExtension(inputPath) + ".visualization");
    }

    internal static string LayoutName(VisualizationLayout layout) => layout switch
    {
        VisualizationLayout.UnifiedRoll => "unified",
        VisualizationLayout.SplitRoll => "split",
        VisualizationLayout.Scopes => "scope",
        VisualizationLayout.Hybrid => "hybrid",
        VisualizationLayout.Diagnostic => "diagnostic",
        VisualizationLayout.LegacyDiagnostic => "diagnostic-v2",
        _ => "auto",
    };

    internal static string PresetName(VisualizationPreset preset) => preset.ToString().ToLowerInvariant();

    internal static string ChannelName(ChannelSelectionMode mode) => mode switch
    {
        ChannelSelectionMode.Audible => "audible",
        ChannelSelectionMode.Semantic => "semantic",
        ChannelSelectionMode.All => "all",
        _ => "active",
    };

    internal static string ScopePositionName(ScopePosition position) => position.ToString().ToLowerInvariant();

    internal static string GroupByName(TrackGroupingMode grouping) => grouping.ToString().ToLowerInvariant();

    internal static string TimeGridName(TimeGridMode grid) => grid.ToString().ToLowerInvariant();

    internal static string EffectsName(VisualizationEffects effects) => effects.ToString().ToLowerInvariant();

    internal static string NoteColorName(NoteColorMode mode) => mode switch
    {
        NoteColorMode.Channel => "channel",
        NoteColorMode.PitchClass => "pitch",
        _ => "instrument",
    };

    internal static string AnalysisDetailName(AnalysisDetail detail) => detail.ToString().ToLowerInvariant();

    internal static string AnalysisOverlayName(AnalysisOverlayMode mode) => mode.ToString().ToLowerInvariant();

    internal static string SpcPitchName(SpcPitchMode mode) => mode.ToString().ToLowerInvariant();

    internal static string BackendName(BackendPreference backend) => backend.ToString().ToLowerInvariant();

    internal static string ScopeModeName(ScopeMode mode) => mode.ToString().ToLowerInvariant();

    internal static string EncoderName(VideoEncoder encoder) => encoder switch
    {
        VideoEncoder.LibX264 => "libx264",
        VideoEncoder.Nvenc => "nvenc",
        _ => "auto",
    };

    private static string Format(double? value) => value is null ? "" : value.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static void AddIf(List<string> arguments, string option, string? value, bool compact, bool condition)
    {
        if (condition || (!compact && !string.IsNullOrEmpty(value)))
        {
            arguments.Add(option);
            if (!string.IsNullOrEmpty(value))
                arguments.Add(value);
        }
    }

    private static string BuildDisplayText(IReadOnlyList<string> arguments)
        => string.Join(' ', arguments.Select(QuoteForDisplay));

    private static string QuoteForDisplay(string argument)
    {
        if (argument.Length == 0 || argument.IndexOfAny(new[] { ' ', '\t', '"', '\'' }) < 0)
            return argument;
        return "\"" + argument.Replace("\"", "\\\"") + "\"";
    }
}
