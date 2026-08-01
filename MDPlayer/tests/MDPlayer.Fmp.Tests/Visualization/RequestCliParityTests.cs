using Fmp.Application.Contracts;
using Fmp.Application.Export;
using Fmp.Cli;
using Xunit;

// Aliases to disambiguate the new public contract enums from the unchanged
// Core renderer enums (Core is intentionally frozen during the reset).
using CoreLayout = global::Fmp.Core.Visualization.Rendering.VisualizationLayoutMode;
using CorePreset = global::Fmp.Core.Visualization.Rendering.VisualizationPreset;
using CoreChannels = global::Fmp.Core.Visualization.Rendering.VisualizationChannelFilter;
using CoreTimeGrid = global::Fmp.Core.Visualization.Rendering.VisualizationTimeGrid;
using CoreEffects = global::Fmp.Core.Visualization.Rendering.EffectsMode;
using CoreNoteColor = global::Fmp.Core.Visualization.Rendering.NoteColorMode;
using CoreEncoder = global::Fmp.Core.Visualization.Rendering.VideoEncoder;

namespace MDPlayer.Fmp.Tests.Visualization;

/// <summary>
/// CLI parity tests for the final greenfield schema (v1), spec §33.2: the
/// request → request-json → canonical command → CLI-parse path must resolve to
/// the same options as loading the request-json directly.
///
/// The Application formatter now emits the canonical <c>render</c> command with
/// the new option set, but the CLI render pipeline has not been switched yet,
/// so full command parity is not yet possible. These tests therefore pin
/// (a) the request-json → CLI mapping, (b) the formatter output against the
/// schema, and (c) the round-trip of every emitted option the current CLI
/// parser already accepts with matching semantics.
/// </summary>
public class RequestCliParityTests
{
    public static TheoryData<string, VisualizationRequest> Requests => new()
    {
        { "balanced-default", Balanced() },
        { "final-with-metadata", FinalWithMetadata() },
        { "preview-custom-window", PreviewCustomWindow() },
        { "diagnostic-all", DiagnosticAll() },
        { "custom-channels", CustomChannels() },
        { "fractional-fps-and-playback", FractionalFpsAndPlayback() },
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public void RequestJson_SeedsTheCliOptionsForEveryRequestField(string name, VisualizationRequest request)
    {
        string jsonPath = Path.Combine(Path.GetTempPath(), $"parity-{Guid.NewGuid():N}.json");
        try
        {
            VisualizationRequestSerializer.WriteToFile(request, jsonPath);
            VisualizeOptions options = VisualizeOptionsParser.ParseForRequest(jsonPath, Array.Empty<string>());

            // Identity fields (the request value must flow through untouched).
            Assert.Equal(request.InputPath, options.Input);
            Assert.Equal(FullPath(request.OutputPath), FullPath(options.VideoPath));
            Assert.Equal(FullPath(Path.GetDirectoryName(request.OutputPath) ?? "."), FullPath(options.OutputDir));
            Assert.Equal(request.Output.Width, options.Width);
            Assert.Equal(request.Output.Height, options.Height);
            Assert.Equal(request.Output.FpsNumerator, options.Fps);
            Assert.Equal(request.Output.FpsDenominator, options.FpsDenominator);
            Assert.Equal(request.View.PastSeconds, options.PastSeconds);
            Assert.Equal(request.View.FutureSeconds, options.FutureSeconds);
            Assert.Equal(request.Tracks.IncludedIds, options.IncludeTracks);
            Assert.Equal(request.Tracks.ExcludedIds, options.ExcludeTracks);
            Assert.Equal(request.Playback.LoopCount, options.Loops);
            Assert.Equal(request.Playback.FadeSeconds, options.Fade);
            Assert.Equal(request.Playback.TailSeconds, options.Tail);
            Assert.Equal(request.Playback.MaximumDurationSeconds ?? 300.0, options.MaxDuration);
            Assert.Equal(request.Playback.SampleRate, options.SampleRate);
            Assert.Equal(request.Presentation.Title, options.Title);
            Assert.Equal(request.Presentation.Subtitle, options.Subtitle);
            Assert.Equal(request.Presentation.Credits, options.Credits);
            Assert.Equal(request.Presentation.FontPath, options.FontPath);
            Assert.Equal(request.Output.Overwrite, options.Overwrite);
            Assert.Equal(request.Output.Quality == RenderQuality.Final, options.FinalQuality);

            // Enum mapping onto the unchanged Core-backed CLI model.
            Assert.Equal(ExpectedLayout(request.Composition), options.LayoutMode);
            Assert.Equal(ExpectedPreset(request.Output.Quality), options.Preset);
            Assert.Equal(ExpectedChannels(request.Tracks.Selection), options.Channels);
            Assert.Equal(ExpectedTimeGrid(request.View.TimeGrid), options.TimeGrid);
            Assert.Equal(ExpectedEffects(request.Style.Effects), options.Effects);
            Assert.Equal(ExpectedNoteColor(request.Style.NoteColor), options.NoteColor);
            Assert.Equal(ExpectedEncoder(request.Output.Encoder), options.Encoder);
        }
        finally
        {
            File.Delete(jsonPath);
        }
    }

    /// <summary>
    /// The formatter emits the canonical <c>render</c> command. The CLI render
    /// pipeline does not parse the new options yet, so only the emitted options
    /// whose CLI semantics already match (through the shared render options of
    /// the request-json visualize parser) are fed back through the parser and
    /// must resolve identically to the request-json path. Options the current
    /// CLI does not accept (--composition, --quality, --tracks, --past/--future,
    /// --structure, --signal-strip, --palette, --include-inactive) are excluded
    /// here; <see cref="Formatter_EmittedRenderCommand_MatchesTheRequestSchema"/>
    /// pins them until the render pipeline switches.
    /// </summary>
    [Theory]
    [MemberData(nameof(Requests))]
    public void Formatter_RenderOptionsTheCliParsesToday_RoundTripThroughTheCliParser(string name, VisualizationRequest request)
    {
        string jsonPath = Path.Combine(Path.GetTempPath(), $"parity-{Guid.NewGuid():N}.json");
        try
        {
            VisualizationRequestSerializer.WriteToFile(request, jsonPath);
            VisualizeOptions optionsFromRequest = VisualizeOptionsParser.ParseForRequest(jsonPath, Array.Empty<string>());

            CanonicalCommand command = new VisualizationCommandFormatter().Format(request, CommandDisplayMode.FullyResolved);
            string[] roundTrippable = RoundTrippableSubset(command.Arguments);
            VisualizeOptions optionsFromCommand = VisualizeOptionsParser.ParseStrict(roundTrippable);

            Assert.Equal(optionsFromRequest.Input, optionsFromCommand.Input);
            Assert.Equal(optionsFromRequest.Width, optionsFromCommand.Width);
            Assert.Equal(optionsFromRequest.Height, optionsFromCommand.Height);
            Assert.Equal(optionsFromRequest.Fps, optionsFromCommand.Fps);
            Assert.Equal(optionsFromRequest.FpsDenominator, optionsFromCommand.FpsDenominator);
            Assert.Equal(optionsFromRequest.TimeGrid, optionsFromCommand.TimeGrid);
            Assert.Equal(optionsFromRequest.NoteColor, optionsFromCommand.NoteColor);
            Assert.Equal(optionsFromRequest.Encoder, optionsFromCommand.Encoder);
            Assert.Equal(optionsFromRequest.Title, optionsFromCommand.Title);
            Assert.Equal(optionsFromRequest.Subtitle, optionsFromCommand.Subtitle);
            Assert.Equal(optionsFromRequest.Credits, optionsFromCommand.Credits);
            Assert.Equal(optionsFromRequest.FontPath, optionsFromCommand.FontPath);
            Assert.Equal(optionsFromRequest.IncludeTracks, optionsFromCommand.IncludeTracks);
            Assert.Equal(optionsFromRequest.ExcludeTracks, optionsFromCommand.ExcludeTracks);
            Assert.Equal(optionsFromRequest.Loops, optionsFromCommand.Loops);
            Assert.Equal(optionsFromRequest.Fade, optionsFromCommand.Fade);
            Assert.Equal(optionsFromRequest.Tail, optionsFromCommand.Tail);
            Assert.Equal(optionsFromRequest.MaxDuration, optionsFromCommand.MaxDuration);
            Assert.Equal(optionsFromRequest.SampleRate, optionsFromCommand.SampleRate);
            Assert.Equal(optionsFromRequest.Overwrite, optionsFromCommand.Overwrite);

            // The render command's --output carries the final video path (the
            // model's VideoPath); the visualize parser stores it as OutputDir
            // because the render/visualize option split is not unified yet.
            Assert.Equal(FullPath(optionsFromRequest.VideoPath), FullPath(optionsFromCommand.OutputDir));
        }
        finally
        {
            File.Delete(jsonPath);
        }
    }

    /// <summary>
    /// Pins the exact canonical <c>render</c> command the Application formatter
    /// emits for the final schema. Until the CLI render pipeline accepts these
    /// options, this is the contract the pipeline must implement; when it does,
    /// the options can move into the round-trip test above.
    /// </summary>
    [Theory]
    [MemberData(nameof(Requests))]
    public void Formatter_EmittedRenderCommand_MatchesTheRequestSchema(string name, VisualizationRequest request)
    {
        CanonicalCommand command = new VisualizationCommandFormatter().Format(request, CommandDisplayMode.FullyResolved);

        Assert.Equal("mdplayer-render", command.Executable);
        Assert.Equal("render", command.Arguments[0]);
        Assert.Equal(request.InputPath, command.Arguments[1]);
        string[] args = command.Arguments.Skip(2).ToArray();

        AssertOption(args, "--output", request.OutputPath);
        AssertOption(args, "--composition", CliComposition(request.Composition));
        AssertOption(args, "--quality", CliQuality(request.Output.Quality));
        AssertOption(args, "--width", request.Output.Width.ToString());
        AssertOption(args, "--height", request.Output.Height.ToString());
        AssertOption(args, "--fps", request.Output.FpsNumerator.ToString());
        AssertOption(args, "--fps-denominator", request.Output.FpsDenominator.ToString());
        AssertOption(args, "--past", Format(request.View.PastSeconds));
        AssertOption(args, "--future", Format(request.View.FutureSeconds));
        AssertOption(args, "--time-grid", CliTimeGrid(request.View.TimeGrid));
        AssertOption(args, "--structure", CliStructure(request.View.Structure));
        AssertOption(args, "--effects", CliEffects(request.Style.Effects));
        AssertOption(args, "--note-color", CliNoteColor(request.Style.NoteColor));
        AssertOption(args, "--palette", CliPalette(request.Style.Palette));
        AssertOption(args, "--loops", request.Playback.LoopCount.ToString());
        AssertOption(args, "--fade", Format(request.Playback.FadeSeconds));
        AssertOption(args, "--tail", Format(request.Playback.TailSeconds));
        AssertOption(args, "--max-duration", Format(request.Playback.MaximumDurationSeconds ?? 300.0));
        AssertOption(args, "--sample-rate", request.Playback.SampleRate.ToString());
        AssertOption(args, "--encoder", CliEncoder(request.Output.Encoder));
        AssertFlag(args, "--overwrite", request.Output.Overwrite);
        AssertFlag(args, "--signal-strip", request.View.PerformanceSignalStrip);
        AssertFlag(args, "--include-inactive", request.Tracks.IncludeInactiveDiagnosticTracks);
        if (request.Presentation.Title is not null) AssertOption(args, "--title", request.Presentation.Title);
        if (request.Presentation.Subtitle is not null) AssertOption(args, "--subtitle", request.Presentation.Subtitle);
        if (request.Presentation.Credits is not null) AssertOption(args, "--credits", request.Presentation.Credits);
        if (request.Presentation.FontPath is not null) AssertOption(args, "--font", request.Presentation.FontPath);

        if (request.Tracks.Selection == TrackSelectionMode.Custom)
        {
            AssertOption(args, "--tracks", "custom");
            AssertSequence(args, "--include-track", request.Tracks.IncludedIds);
            AssertSequence(args, "--exclude-track", request.Tracks.ExcludedIds);
        }
        else
        {
            AssertOption(args, "--tracks", request.Tracks.Selection.ToString().ToLowerInvariant());
        }
    }

    [Fact]
    public void RequestJsonPath_AndOverridesAfterIt_OverrideRequestFields()
    {
        VisualizationRequest request = Balanced();
        string jsonPath = Path.Combine(Path.GetTempPath(), $"parity-{Guid.NewGuid():N}.json");
        try
        {
            VisualizationRequestSerializer.WriteToFile(request, jsonPath);
            VisualizeOptions options = VisualizeOptionsParser.ParseForRequest(
                jsonPath, new[] { "--width", "640", "--height", "360", "--layout", "scope-stage" });

            Assert.Equal(640, options.Width);
            Assert.Equal(360, options.Height);
            Assert.Equal(CoreLayout.ScopeStage, options.LayoutMode);
            // Non-overridden fields still come from the request.
            Assert.Equal(request.InputPath, options.Input);
        }
        finally
        {
            File.Delete(jsonPath);
        }
    }

    // ------------------------------------------------------------------
    // Round-trip subset helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Options the formatter emits that the current CLI parser (the request-json
    /// visualize parser, which owns the shared render options) already handles
    /// with matching semantics. Everything else is dropped so the subset can be
    /// parsed today.
    ///
    /// --effects is intentionally excluded: the new schema uses
    /// off|subtle|cinematic while the legacy CLI accepts minimal|diagnostic|
    /// all|none, so it does not round-trip until the render pipeline switches.
    /// </summary>
    private static readonly string[] RoundTrippableValueOptions =
    [
        "--output", "--width", "--height", "--fps", "--fps-denominator",
        "--include-track", "--exclude-track", "--time-grid",
        "--note-color", "--title", "--subtitle", "--credits", "--font",
        "--loops", "--fade", "--tail", "--max-duration", "--sample-rate",
        "--encoder",
    ];

    private static string[] RoundTrippableSubset(IReadOnlyList<string> arguments)
    {
        // arguments = [ "render", INPUT, --option, value, ... ]
        var result = new List<string> { arguments[1] };
        for (int i = 2; i < arguments.Count; i++)
        {
            string option = arguments[i];
            if (RoundTrippableValueOptions.Contains(option))
            {
                if (i + 1 < arguments.Count && !arguments[i + 1].StartsWith("--"))
                {
                    result.Add(option);
                    result.Add(arguments[++i]);
                }
                // Empty value (e.g. a null presentation field) is skipped.
            }
            else if (option == "--overwrite")
            {
                result.Add(option);
            }
            else if (i + 1 < arguments.Count && !arguments[i + 1].StartsWith("--"))
            {
                i++; // skip a value-carrying option the current CLI cannot parse
            }
        }
        return result.ToArray();
    }

    // ------------------------------------------------------------------
    // Emitted-command inspection helpers
    // ------------------------------------------------------------------

    private static string? ValueOf(string[] args, string option)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == option)
                return args[i + 1];
        }
        return null;
    }

    private static void AssertOption(string[] args, string option, string expected)
        => Assert.Equal(expected, ValueOf(args, option));

    private static void AssertFlag(string[] args, string option, bool expected)
        => Assert.Equal(expected, args.Contains(option));

    private static void AssertSequence(string[] args, string option, IReadOnlyList<string> expected)
    {
        var actual = new List<string>();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == option)
                actual.Add(args[i + 1]);
        }
        Assert.Equal(expected, actual);
    }

    private static string Format(double value)
        => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static string? FullPath(string? path)
        => string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path);

    // ------------------------------------------------------------------
    // Canonical CLI names (contract pins; mirror the formatter's output)
    // ------------------------------------------------------------------

    private static string CliComposition(CompositionKind composition) => composition switch
    {
        CompositionKind.ScopeStage => "scope-stage",
        CompositionKind.Diagnostic => "diagnostic",
        _ => "performance",
    };

    private static string CliQuality(RenderQuality quality) => quality.ToString().ToLowerInvariant();

    private static string CliTimeGrid(TimeGridMode grid) => grid.ToString().ToLowerInvariant();

    private static string CliStructure(StructureOverlayMode mode) => mode.ToString().ToLowerInvariant();

    private static string CliEffects(VisualEffects effects) => effects.ToString().ToLowerInvariant();

    private static string CliNoteColor(NoteColorMode mode) => mode switch
    {
        NoteColorMode.Channel => "channel",
        NoteColorMode.PitchClass => "pitch",
        _ => "instrument",
    };

    private static string CliPalette(PaletteKind palette) => palette.ToString().ToLowerInvariant();

    private static string CliEncoder(VideoEncoder encoder) => encoder switch
    {
        VideoEncoder.LibX264 => "x264",
        VideoEncoder.Nvenc => "nvenc",
        _ => "auto",
    };

    // ------------------------------------------------------------------
    // Request → Core-backed CLI model mapping (mirrors ApplyRequest)
    // ------------------------------------------------------------------

    private static CoreLayout ExpectedLayout(CompositionKind composition) => composition switch
    {
        CompositionKind.ScopeStage => CoreLayout.ScopeStage,
        CompositionKind.Diagnostic => CoreLayout.Diagnostic,
        _ => CoreLayout.Performance,
    };

    private static CorePreset ExpectedPreset(RenderQuality quality) => quality switch
    {
        RenderQuality.Draft => CorePreset.Preview,
        RenderQuality.Final => CorePreset.Final,
        _ => CorePreset.Balanced,
    };

    private static CoreChannels ExpectedChannels(TrackSelectionMode mode) => mode switch
    {
        TrackSelectionMode.All => CoreChannels.All,
        TrackSelectionMode.Custom => CoreChannels.All,
        _ => CoreChannels.Active,
    };

    private static CoreTimeGrid ExpectedTimeGrid(TimeGridMode grid) => grid switch
    {
        TimeGridMode.None => CoreTimeGrid.None,
        TimeGridMode.Authoritative => CoreTimeGrid.Authoritative,
        TimeGridMode.Analytical => CoreTimeGrid.Analytical,
        _ => CoreTimeGrid.Automatic,
    };

    private static CoreEffects ExpectedEffects(VisualEffects effects) => effects switch
    {
        VisualEffects.Off => CoreEffects.None,
        VisualEffects.Cinematic => CoreEffects.Cinematic,
        _ => CoreEffects.Minimal,
    };

    private static CoreNoteColor ExpectedNoteColor(NoteColorMode mode) => mode switch
    {
        NoteColorMode.Channel => CoreNoteColor.Channel,
        NoteColorMode.PitchClass => CoreNoteColor.Pitch,
        _ => CoreNoteColor.Instrument,
    };

    private static CoreEncoder ExpectedEncoder(VideoEncoder encoder) => encoder switch
    {
        VideoEncoder.LibX264 => CoreEncoder.LibX264,
        VideoEncoder.Nvenc => CoreEncoder.Nvenc,
        _ => CoreEncoder.Auto,
    };

    // ---- Fixtures (final schema v1) ----

    private static VisualizationRequest Balanced()
    {
        string input = "/tmp/parity/song.vgz";
        return new VisualizationRequest
        {
            InputPath = input,
            OutputPath = "/tmp/parity/song.visualization/visualization.mp4",
        };
    }

    private static VisualizationRequest FinalWithMetadata()
    {
        VisualizationRequest request = Balanced();
        return request with
        {
            Output = request.Output with { Quality = RenderQuality.Final },
            Presentation = new PresentationSettings
            {
                Title = "Final Boss",
                Subtitle = "Act 2",
                Credits = "Composed by Test",
                FontPath = "/tmp/fonts/noto.ttf",
            },
        };
    }

    private static VisualizationRequest PreviewCustomWindow()
    {
        VisualizationRequest request = Balanced();
        return request with
        {
            Output = request.Output with
            {
                Quality = RenderQuality.Draft,
                Width = 960,
                Height = 540,
                FpsNumerator = 30,
            },
            View = request.View with { PastSeconds = 0.4, FutureSeconds = 1.6, PerformanceSignalStrip = true },
        };
    }

    private static VisualizationRequest DiagnosticAll()
    {
        VisualizationRequest request = Balanced();
        return request with
        {
            Composition = CompositionKind.Diagnostic,
            Tracks = request.Tracks with
            {
                Selection = TrackSelectionMode.All,
                IncludeInactiveDiagnosticTracks = true,
            },
            View = request.View with { Structure = StructureOverlayMode.Off },
            Style = request.Style with { Effects = VisualEffects.Off },
        };
    }

    private static VisualizationRequest CustomChannels()
    {
        VisualizationRequest request = Balanced();
        return request with
        {
            Tracks = new TrackSettings
            {
                Selection = TrackSelectionMode.Custom,
                IncludedIds = new[] { "ym2608.0.fm.1", "ym2608.0.fm.2" },
                ExcludedIds = new[] { "ym2608.0.rhythm.1" },
            },
        };
    }

    private static VisualizationRequest FractionalFpsAndPlayback()
    {
        VisualizationRequest request = Balanced();
        return request with
        {
            Output = request.Output with
            {
                FpsNumerator = 60000,
                FpsDenominator = 1001,
                Encoder = VideoEncoder.Nvenc,
                Overwrite = true,
            },
            View = request.View with { TimeGrid = TimeGridMode.Analytical },
            Playback = new PlaybackSettings
            {
                LoopCount = 4,
                FadeSeconds = 3.0,
                TailSeconds = 1.0,
                MaximumDurationSeconds = 120,
                SampleRate = 44100,
            },
        };
    }
}
