using Fmp.Application.Contracts;
using Fmp.Application.Export;

namespace Fmp.Cli;

/// <summary>
/// Runtime-only options for the canonical <c>render</c> command (spec §18).
/// Tool paths are process-level settings and are never serialized into the
/// request (schema-1 rule: ToolPaths is runtime-only).
/// </summary>
internal sealed record RenderRuntimeOptions
{
    public string? FmpCom { get; init; }
    public string? AssetsDir { get; init; }
    public IReadOnlyList<string> SearchPaths { get; init; } = Array.Empty<string>();
    public string? CorrscopePath { get; init; }
    public string? FfmpegPath { get; init; }
    public string? AnalysisPython { get; init; }
    public bool Quiet { get; init; }
    public bool Json { get; init; }
    public string? ProgressMode { get; init; }
    public string Backend { get; init; } = "auto";
    public int ToolTimeoutMinutes { get; init; } = 60;
}

/// <summary>
/// Parses the canonical <c>render</c> command (spec §18) into a
/// <see cref="VisualizationRequest"/> plus runtime-only options. Every option
/// the Application formatter emits must be accepted here with identical
/// semantics (the CLI parser MUST round-trip every emitted option; parity
/// tests enforce this).
/// </summary>
internal static class RenderCommandParser
{
    internal static RenderInvocation ParseInvocationCore(string[] args)
    {
        var parsed = ParseCore(args);
        return new RenderInvocation(parsed.Request, parsed.Runtime, parsed.CaptureDirectory, parsed.CaptureKey);
    }

    public static RenderInvocation? ParseInvocation(string[] args)
    {
        try
        {
            return ParseInvocationCore(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parses the canonical command. <paramref name="requestJsonPath"/> is the
    /// optional <c>--request-json</c> seed; when present, the request is loaded
    /// from that file first and CLI options override individual fields.
    /// Returns null (after printing an error) on invalid arguments.
    /// </summary>
    public static (VisualizationRequest Request, RenderRuntimeOptions Runtime, string? RequestJsonPath, string? CaptureDirectory, string? CaptureKey)? Parse(
        string[] args)
    {
        try
        {
            return ParseCore(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return null;
        }
    }

    internal static (VisualizationRequest Request, RenderRuntimeOptions Runtime, string? RequestJsonPath, string? CaptureDirectory, string? CaptureKey) ParseCore(
        string[] args)
    {
        string? input = null;
        string? requestJsonPath = FindRequestJson(args);
        VisualizationRequest? seeded = requestJsonPath is null ? null : ReadSeed(requestJsonPath);

        // Builder state. Defaults come from the canonical schema defaults so an
        // omitted option keeps its request default (same contract as the GUI).
        CompositionKind composition = seeded?.Composition ?? CompositionKind.Diagnostic;
        OutputSettings output = seeded?.Output ?? new();
        TrackSettings tracks = seeded?.Tracks ?? new();
        // CLI track lists override the seeded request: the first
        // --include-track / --exclude-track on the command line *replaces* the
        // seeded list instead of appending to it. A bare include/exclude also
        // implies Custom selection so the lists are actually applied (they are
        // ignored under Active/All), unless the user explicitly chose a mode.
        bool includeTrackSpecified = false;
        bool excludeTrackSpecified = false;
        bool tracksSelectionSpecified = false;
        ViewSettings view = seeded?.View ?? new();
        StyleSettings style = seeded?.Style ?? new();
        PresentationSettings presentation = seeded?.Presentation ?? new();
        PlaybackSettings playback = seeded?.Playback ?? new();
        input = seeded?.InputPath;

        string? fmpCom = null;
        string? assetsDir = null;
        string? corrscope = null;
        string? ffmpeg = null;
        string? analysisPython = null;
        var searchPaths = new List<string>();
        string backend = "auto";
        bool quiet = false;
        bool json = false;
        string? progressMode = null;
        int toolTimeoutMinutes = 60;
        string? captureDirectory = null;
        string? captureKey = null;

        var reader = new ArgumentReader(args);

        while (reader.HasMore)
        {
            if (reader.TryReadOption(out string name, out string value))
            {
                switch (name)
                {
                    // ---- request seed ----
                    case "--request-json":
                        reader.RequireValue(name);
                        break;

                    // ---- identity / output ----
                    // --output carries the final video path; it is read back
                    // from args during path resolution so a --request-json seed
                    // without --output keeps the seeded output path.
                    case "-o":
                    case "--output":
                        reader.RequireValue(name);
                        break;

                    // ---- composition ----
                    case "--composition":
                        composition = ParseComposition(reader.RequireValue(name));
                        break;

                    // ---- output settings ----
                    case "--quality":
                        output = output with { Quality = ParseQuality(reader.RequireValue(name)) };
                        break;
                    case "--width":
                        output = output with { Width = reader.ReadInt(name) };
                        break;
                    case "--height":
                        output = output with { Height = reader.ReadInt(name) };
                        break;
                    case "--fps":
                        output = output with { FpsNumerator = reader.ReadInt(name) };
                        break;
                    case "--fps-denominator":
                        output = output with { FpsDenominator = reader.ReadInt(name) };
                        break;
                    case "--encoder":
                        output = output with { Encoder = ParseEncoder(reader.RequireValue(name)) };
                        break;
                    case "--overwrite" when value == null:
                        output = output with { Overwrite = true };
                        break;

                    // ---- track selection ----
                    case "--tracks":
                        tracks = tracks with { Selection = ParseTrackSelection(reader.RequireValue(name)) };
                        tracksSelectionSpecified = true;
                        break;
                    // The first --include-track/--exclude-track on the command
                    // line *replaces* the list seeded from --request-json; later
                    // occurrences append. Layering over a seeded request would
                    // otherwise silently retain the JSON's custom subset.
                    case "--include-track":
                        tracks = tracks with
                        {
                            IncludedIds = includeTrackSpecified
                                ? tracks.IncludedIds.Append(reader.RequireValue(name)).ToArray()
                                : [reader.RequireValue(name)],
                            Selection = tracksSelectionSpecified
                                ? tracks.Selection
                                : TrackSelectionMode.Custom,
                        };
                        includeTrackSpecified = true;
                        break;
                    case "--exclude-track":
                        tracks = tracks with
                        {
                            ExcludedIds = excludeTrackSpecified
                                ? tracks.ExcludedIds.Append(reader.RequireValue(name)).ToArray()
                                : [reader.RequireValue(name)],
                            Selection = tracksSelectionSpecified
                                ? tracks.Selection
                                : TrackSelectionMode.Custom,
                        };
                        excludeTrackSpecified = true;
                        break;
                    case "--include-inactive" when value == null:
                        tracks = tracks with { IncludeInactiveDiagnosticTracks = true };
                        break;

                    // ---- view settings ----
                    case "--past":
                        view = view with { PastSeconds = reader.ReadDouble(name) };
                        break;
                    case "--future":
                        view = view with { FutureSeconds = reader.ReadDouble(name) };
                        break;
                    case "--time-grid":
                        view = view with { TimeGrid = ParseTimeGrid(reader.RequireValue(name)) };
                        break;
                    case "--structure":
                        view = view with { Structure = ParseStructure(reader.RequireValue(name)) };
                        break;

                    // ---- style settings ----
                    case "--effects":
                        style = style with { Effects = ParseEffects(reader.RequireValue(name)) };
                        break;
                    case "--note-color":
                        style = style with { NoteColor = ParseNoteColor(reader.RequireValue(name)) };
                        break;
                    case "--palette":
                        style = style with { Palette = ParsePalette(reader.RequireValue(name)) };
                        break;

                    // ---- presentation ----
                    // In FullyResolved mode the formatter emits these options
                    // bare (no value) for null/empty fields; OptionalValue
                    // yields null then and the field stays null.
                    case "--title":
                        presentation = presentation with { Title = NullIfEmpty(reader.OptionalValue(name)) };
                        break;
                    case "--subtitle":
                        presentation = presentation with { Subtitle = NullIfEmpty(reader.OptionalValue(name)) };
                        break;
                    case "--credits":
                        presentation = presentation with { Credits = NullIfEmpty(reader.OptionalValue(name)) };
                        break;
                    case "--font":
                        presentation = presentation with { FontPath = NullIfEmpty(reader.OptionalValue(name)) };
                        break;

                    // ---- playback settings ----
                    case "--loops":
                        playback = playback with { LoopCount = reader.ReadInt(name) };
                        break;
                    case "--fade":
                        playback = playback with { FadeSeconds = reader.ReadDouble(name) };
                        break;
                    case "--tail":
                        playback = playback with { TailSeconds = reader.ReadDouble(name) };
                        break;
                    case "--max-duration":
                        playback = playback with { MaximumDurationSeconds = reader.ReadDouble(name) };
                        break;
                    case "--sample-rate":
                        playback = playback with { SampleRate = reader.ReadInt(name) };
                        break;
                    case "--ssg-gain-db":
                        playback = playback with { SsgGainDb = reader.ReadDouble(name) };
                        break;
                    case "--spc-pitch":
                        playback = playback with { SpcPitch = ParseSpcPitch(reader.RequireValue(name)) };
                        break;

                    // ---- runtime tool options ----
                    case "--fmp-com": fmpCom = reader.RequireValue(name); break;
                    case "--assets-dir": assetsDir = reader.RequireValue(name); break;
                    case "--corrscope": corrscope = reader.RequireValue(name); break;
                    case "--ffmpeg": ffmpeg = reader.RequireValue(name); break;
                    case "--analysis-python": analysisPython = reader.RequireValue(name); break;
                    case "-I":
                    case "--search-path": searchPaths.Add(reader.RequireValue(name)); break;
                    case "--backend": backend = reader.RequireValue(name); break;
                    case "--tool-timeout-minutes": toolTimeoutMinutes = reader.ReadInt(name); break;
                    case "--quiet" when value == null: quiet = true; break;
                    case "--json" when value == null: json = true; break;
                    case "--progress":
                        progressMode = ParseProgressMode(reader.RequireValue(name));
                        break;

                    // ---- internal capture reuse (runtime-only) ----
                    case "--capture-dir":
                        if (captureDirectory != null)
                            throw new ArgumentException("--capture-dir may be specified only once");
                        captureDirectory = reader.RequireValue(name);
                        break;
                    case "--capture-key":
                        if (captureKey != null)
                            throw new ArgumentException("--capture-key may be specified only once");
                        captureKey = reader.RequireValue(name);
                        break;

                    default:
                        throw new ArgumentException($"unknown option '{name}'");
                }
            }
            else
            {
                string positional = reader.Next();
                if (positional == "--")
                    continue;
                if (input != null && !string.Equals(input, seeded?.InputPath, StringComparison.Ordinal))
                    throw new ArgumentException($"unexpected argument '{positional}'");
                input = positional;
            }
        }

        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("no input file specified");

        // Resolve the output path (canonical: --output is the final video).
        string outputPath = ResolveOutputPath(args, seeded, input);
        string resolvedInput = ResolveInputPath(args, seeded, input);

        VisualizationRequest request = (seeded ?? NewDefaultRequest(resolvedInput, outputPath)) with
        {
            InputPath = resolvedInput,
            OutputPath = outputPath,
            Composition = composition,
            Output = output,
            Tracks = tracks,
            View = view,
            Style = style,
            Presentation = presentation,
            Playback = playback,
        };

        var runtime = new RenderRuntimeOptions
        {
            FmpCom = fmpCom,
            AssetsDir = assetsDir,
            CorrscopePath = corrscope,
            FfmpegPath = ffmpeg,
            AnalysisPython = analysisPython,
            SearchPaths = searchPaths,
            Backend = backend,
            Quiet = quiet,
            Json = json,
            ProgressMode = progressMode,
            ToolTimeoutMinutes = toolTimeoutMinutes,
        };

        EnsureCaptureOptionsConsistent(captureDirectory, captureKey);

        return (request, runtime, requestJsonPath, captureDirectory, captureKey);
    }

    /// <summary>
    /// Capture reuse requires both a validated capture directory and an
    /// expected capture identity. Supplying one without the other is an
    /// argument error (exit code 2).
    /// </summary>
    private static void EnsureCaptureOptionsConsistent(string? captureDirectory, string? captureKey)
    {
        if (captureDirectory == null && captureKey != null)
            throw new ArgumentException("--capture-key requires --capture-dir");
        if (captureDirectory != null && captureKey == null)
            throw new ArgumentException("--capture-dir requires --capture-key");
    }

    // ------------------------------------------------------------------
    // Output / input resolution
    // ------------------------------------------------------------------

    private static string? FindRequestJson(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--request-json")
                return args[i + 1];
        return args.FirstOrDefault(a => a.StartsWith("--request-json=", StringComparison.Ordinal))?
            ["--request-json=".Length..];
    }

    private static VisualizationRequest ReadSeed(string path)
    {
        try { return VisualizationRequestSerializer.ReadFromFile(path); }
        catch (VisualizationRequestException ex) { throw new ArgumentException(ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new ArgumentException($"reading request file — {ex.Message}"); }
    }

    private static string ResolveOutputPath(string[] args, VisualizationRequest? seeded, string? input)
    {
        string? fromArgs = ReadOutputOverride(args);
        if (fromArgs != null)
            return fromArgs;
        if (seeded?.OutputPath != null)
            return seeded.OutputPath;
        return input != null
            ? Path.Combine(Path.GetDirectoryName(input) ?? ".",
                Path.GetFileNameWithoutExtension(input) + ".visualization" + Path.DirectorySeparatorChar + "visualization.mp4")
            : "visualization.mp4";
    }

    private static string ResolveInputPath(string[] args, VisualizationRequest? seeded, string? input)
    {
        if (input != null)
            return input;
        if (seeded?.InputPath != null)
            return seeded.InputPath;
        throw new ArgumentException("no input file specified");
    }

    private static string? ReadOutputOverride(string[] args, string option = "--output")
    {
        foreach (string candidate in new[] { "-o", "--output" })
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == candidate)
                    return args[i + 1];
            }
            foreach (string arg in args)
            {
                if (arg.StartsWith(candidate + "=", StringComparison.Ordinal))
                    return arg[(candidate.Length + 1)..];
            }
        }
        return null;
    }

    private static VisualizationRequest NewDefaultRequest(string input, string outputPath) => new()
    {
        InputPath = input,
        OutputPath = outputPath,
    };

    // ------------------------------------------------------------------
    // Enum parsers (mirror the formatter's canonical CLI names)
    // ------------------------------------------------------------------

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    internal static CompositionKind ParseComposition(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "diagnostic" => CompositionKind.Diagnostic,
        _ => throw new ArgumentException($"unknown composition '{raw}'"),
    };

    private static SpcPitchInterpretation ParseSpcPitch(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "estimate" => SpcPitchInterpretation.Estimate,
        "relative" => SpcPitchInterpretation.Relative,
        _ => throw new ArgumentException($"unknown SPC pitch interpretation '{raw}'"),
    };

    internal static RenderQuality ParseQuality(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "draft" => RenderQuality.Draft,
        "standard" => RenderQuality.Standard,
        "final" => RenderQuality.Final,
        _ => throw new ArgumentException($"unknown quality '{raw}' (expected draft, standard, or final)"),
    };

    internal static TrackSelectionMode ParseTrackSelection(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "active" => TrackSelectionMode.Active,
        "all" => TrackSelectionMode.All,
        "custom" => TrackSelectionMode.Custom,
        _ => throw new ArgumentException($"unknown track selection '{raw}' (expected active, all, or custom)"),
    };

    internal static TimeGridMode ParseTimeGrid(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "none" => TimeGridMode.None,
        "automatic" => TimeGridMode.Automatic,
        "authoritative" => TimeGridMode.Authoritative,
        "analytical" => TimeGridMode.Analytical,
        _ => throw new ArgumentException(
            $"unknown time grid '{raw}' (expected none, automatic, authoritative, or analytical)"),
    };

    internal static StructureOverlayMode ParseStructure(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "off" => StructureOverlayMode.Off,
        "automatic" => StructureOverlayMode.Automatic,
        _ => throw new ArgumentException($"unknown structure overlay '{raw}' (expected off or automatic)"),
    };

    internal static VisualEffects ParseEffects(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "off" => VisualEffects.Off,
        "subtle" => VisualEffects.Subtle,
        "cinematic" => VisualEffects.Cinematic,
        _ => throw new ArgumentException($"unknown effects '{raw}' (expected off, subtle, or cinematic)"),
    };

    internal static NoteColorMode ParseNoteColor(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "instrument" => NoteColorMode.Instrument,
        "channel" => NoteColorMode.Channel,
        "pitch" => NoteColorMode.PitchClass,
        _ => throw new ArgumentException($"unknown note color '{raw}' (expected instrument, channel, or pitch)"),
    };

    internal static PaletteKind ParsePalette(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "default" => PaletteKind.Default,
        "accessible" => PaletteKind.Accessible,
        "monochrome" => PaletteKind.Monochrome,
        _ => throw new ArgumentException($"unknown palette '{raw}' (expected default, accessible, or monochrome)"),
    };

    internal static VideoEncoder ParseEncoder(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "auto" => VideoEncoder.Auto,
        "x264" or "libx264" => VideoEncoder.LibX264,
        "nvenc" => VideoEncoder.Nvenc,
        _ => throw new ArgumentException($"unknown encoder '{raw}' (expected auto, x264, or nvenc)"),
    };

    internal static string ParseProgressMode(string raw)
    {
        string mode = raw?.Trim().ToLowerInvariant();
        return mode switch
        {
            "human" or null => "human",
            "jsonl" => "jsonl",
            _ => throw new ArgumentException($"unknown progress mode '{raw}' (expected human or jsonl)"),
        };
    }
}
