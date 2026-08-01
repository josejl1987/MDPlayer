using Fmp.Application.Contracts;

namespace Fmp.Application.Export;

/// <summary>Display modes for the canonical command.</summary>
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
/// Canonical command formatter for the final schema 2 contract. It emits the
/// `render` command with stable option order. The CLI parser MUST round-trip
/// every emitted option (parity tests enforce this).
/// </summary>
public sealed class VisualizationCommandFormatter : IVisualizationCommandFormatter
{
    public const string ExecutableName = "mdplayer-render";

    public CanonicalCommand Format(VisualizationRequest request, CommandDisplayMode mode = CommandDisplayMode.Compact)
    {
        ArgumentNullException.ThrowIfNull(request);
        var arguments = new List<string> { "render", request.InputPath };

        // Composition.
        AddIf(arguments, "--composition", CompositionName(request.Composition), mode,
            request.Composition != CompositionKind.Performance);

        // Output.
        AddIf(arguments, "--output", request.OutputPath, mode, true);

        // Quality profile and resolution/fps.
        OutputSettings output = request.Output;
        AddIf(arguments, "--quality", QualityName(output.Quality), mode, output.Quality != RenderQuality.Standard);
        AddIf(arguments, "--width", output.Width.ToString(), mode, output.Width != 1920);
        AddIf(arguments, "--height", output.Height.ToString(), mode, output.Height != 1080);
        AddIf(arguments, "--fps", output.FpsNumerator.ToString(), mode, output.FpsNumerator != 60);
        AddIf(arguments, "--fps-denominator", output.FpsDenominator.ToString(), mode, output.FpsDenominator != 1);

        // Track selection.
        TrackSettings tracks = request.Tracks;
        if (tracks.Selection == TrackSelectionMode.Custom)
        {
            arguments.Add("--tracks");
            arguments.Add("custom");
            foreach (string trackId in tracks.IncludedIds)
                arguments.AddRange(new[] { "--include-track", trackId });
            foreach (string trackId in tracks.ExcludedIds)
                arguments.AddRange(new[] { "--exclude-track", trackId });
        }
        else
        {
            AddIf(arguments, "--tracks", tracks.Selection.ToString().ToLowerInvariant(), mode,
                tracks.Selection != TrackSelectionMode.Active);
        }
        if (tracks.IncludeInactiveDiagnosticTracks)
            arguments.Add("--include-inactive");

        // View settings.
        ViewSettings view = request.View;
        AddIf(arguments, "--past", Format(view.PastSeconds), mode, view.PastSeconds != 0.8);
        AddIf(arguments, "--future", Format(view.FutureSeconds), mode, view.FutureSeconds != 3.2);
        AddIf(arguments, "--time-grid", TimeGridName(view.TimeGrid), mode, view.TimeGrid != TimeGridMode.Automatic);
        AddIf(arguments, "--structure", StructureName(view.Structure), mode, view.Structure != StructureOverlayMode.Automatic);
        if (view.PerformanceSignalStrip)
            arguments.Add("--signal-strip");

        // Style.
        StyleSettings style = request.Style;
        AddIf(arguments, "--effects", EffectsName(style.Effects), mode, style.Effects != VisualEffects.Subtle);
        AddIf(arguments, "--note-color", NoteColorName(style.NoteColor), mode, style.NoteColor != NoteColorMode.Instrument);
        AddIf(arguments, "--palette", PaletteName(style.Palette), mode, style.Palette != PaletteKind.Default);

        // Presentation.
        PresentationSettings presentation = request.Presentation;
        AddIf(arguments, "--title", presentation.Title, mode, !string.IsNullOrEmpty(presentation.Title));
        AddIf(arguments, "--subtitle", presentation.Subtitle, mode, !string.IsNullOrEmpty(presentation.Subtitle));
        AddIf(arguments, "--credits", presentation.Credits, mode, !string.IsNullOrEmpty(presentation.Credits));
        AddIf(arguments, "--font", presentation.FontPath, mode, !string.IsNullOrEmpty(presentation.FontPath));

        // Playback.
        PlaybackSettings playback = request.Playback;
        AddIf(arguments, "--loops", playback.LoopCount.ToString(), mode, playback.LoopCount != 2);
        AddIf(arguments, "--fade", Format(playback.FadeSeconds), mode, playback.FadeSeconds != 5.0);
        AddIf(arguments, "--tail", Format(playback.TailSeconds), mode, playback.TailSeconds != 0.5);
        AddIf(arguments, "--max-duration", Format(playback.MaximumDurationSeconds), mode, playback.MaximumDurationSeconds != 300.0);
        AddIf(arguments, "--sample-rate", playback.SampleRate.ToString(), mode, playback.SampleRate != 48_000);

        // Encoder and overwrite.
        AddIf(arguments, "--encoder", EncoderName(output.Encoder), mode, output.Encoder != VideoEncoder.Auto);
        if (output.Overwrite)
            arguments.Add("--overwrite");

        return new CanonicalCommand
        {
            Executable = ExecutableName,
            Arguments = arguments,
            DisplayText = BuildDisplayText(arguments),
        };
    }

    internal static string CompositionName(CompositionKind composition) => composition switch
    {
        CompositionKind.ScopeStage => "scope-stage",
        CompositionKind.Diagnostic => "diagnostic",
        _ => "performance",
    };

    internal static string QualityName(RenderQuality quality) => quality.ToString().ToLowerInvariant();

    internal static string TimeGridName(TimeGridMode grid) => grid.ToString().ToLowerInvariant();

    internal static string StructureName(StructureOverlayMode mode) => mode.ToString().ToLowerInvariant();

    internal static string EffectsName(VisualEffects effects) => effects.ToString().ToLowerInvariant();

    internal static string NoteColorName(NoteColorMode mode) => mode switch
    {
        NoteColorMode.Channel => "channel",
        NoteColorMode.PitchClass => "pitch",
        _ => "instrument",
    };

    internal static string PaletteName(PaletteKind palette) => palette.ToString().ToLowerInvariant();

    internal static string EncoderName(VideoEncoder encoder) => encoder switch
    {
        VideoEncoder.LibX264 => "x264",
        VideoEncoder.Nvenc => "nvenc",
        _ => "auto",
    };

    private static string Format(double? value) => value is null ? "" : value.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static void AddIf(List<string> arguments, string option, string? value, CommandDisplayMode mode, bool condition)
    {
        if (condition || mode == CommandDisplayMode.FullyResolved)
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