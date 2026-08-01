using System.Text.Json;
using Fmp.Application.Export;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;

namespace Fmp.Cli;

/// <summary>Data and parsing for the visualization command.</summary>
internal sealed class VisualizeOptions : RenderSettings
{
    public string Input { get; set; }
    public string OutputDir { get; set; }
    public string VideoPath { get; set; }
    public string CorrscopePath { get; set; }
    public string FfmpegPath { get; set; }
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 60;
    public int FpsDenominator { get; set; } = 1;
    public int ExternalToolTimeoutMinutes { get; set; } = 60;
    public string Title { get; set; }
    public string Subtitle { get; set; }
    public string Credits { get; set; }
    public string FontPath { get; set; }
    public bool StemsOnly { get; set; }
    public bool Overwrite { get; set; }
    public bool Quiet { get; set; }
    public bool Json { get; set; }
    public string CorrscopeVideoTemplate { get; set; }
    public VisualizationPreset Preset { get; set; } = VisualizationPreset.Balanced;
    public bool FinalQuality { get; set; }
    public VideoEncoder Encoder { get; set; } = VideoEncoder.Auto;
    public EffectsMode Effects { get; set; } = EffectsMode.Minimal;
    public NoteColorMode NoteColor { get; set; } = NoteColorMode.Instrument;
    public VisualizationLayoutMode LayoutMode { get; set; } = VisualizationLayoutMode.Auto;
    public string Backend { get; set; } = "auto";
    public string ScopeMode { get; set; } = "auto";
    public bool SpcStems { get; set; }

    /// <summary>
    /// SPC diagnostic option (§25.3): Estimate (default) runs the BRR root
    /// estimator; Relative skips it so instruments keep only relative S-DSP
    /// pitch. Ignored by non-SPC backends; flows through PlaybackOptions.
    /// </summary>
    public SpcPitchMode SpcPitchMode { get; set; } = SpcPitchMode.Estimate;
    public bool BackendExplicit { get; set; }
    public bool ScopeModeExplicit { get; set; }
    public bool SpcStemsExplicit { get; set; }
    public bool SpcPitchExplicit { get; set; }
    public bool TimeoutExplicit { get; set; }
    public double PastSeconds { get; set; } = 0.75;
    public double FutureSeconds { get; set; } = 2.25;
    public double RollZoom { get; set; } = 1.0;
    public int? ScopeHeight { get; set; }
    public int? TimelineHeight { get; set; }
    public double? ScopeRatio { get; set; }
    public VisualizationChannelFilter Channels { get; set; } = VisualizationChannelFilter.Active;
    public VisualizationScopePosition ScopePosition { get; set; } = VisualizationScopePosition.Bottom;
    public VisualizationGroupBy GroupBy { get; set; } = VisualizationGroupBy.None;
    public VisualizationTimeGrid TimeGrid { get; set; } = VisualizationTimeGrid.Automatic;
    public VisualizationTimeScale TimeScale { get; set; } = VisualizationTimeScale.Balanced;
    public bool PrintLayout { get; set; }
    public string LayoutJson { get; set; }
    public bool Analysis { get; set; }
    public string AnalysisPython { get; set; }
    public string AnalysisOutput { get; set; }
    public string AnalysisCache { get; set; }
    public string LayoutTemplatePath { get; set; }
    public string PalettePath { get; set; }
    public VisualizationPalette Palette { get; set; } = VisualizationPalette.Default;
    public string PreviewHtmlPath { get; set; }
    public string DiagnosticPagesPath { get; set; }
    public VisualizationRendererMode Renderer { get; set; } = VisualizationRendererMode.Auto;
    public int MotionBlurSamples { get; set; } = 1;
    public AnalysisDetail AnalysisDetail { get; set; } = AnalysisDetail.Standard;
    public bool AnalysisForce { get; set; }
    public int AnalysisTimeoutMinutes { get; set; } = 10;
    public string AnalysisOverlay { get; set; } = "minimal";

    // ---- Request-driven selection (--request-json / --include-track / --exclude-track) ----

    /// <summary>Track ids explicitly kept by the request (--include-track).</summary>
    public List<string> IncludeTracks { get; } = new();

    /// <summary>Track ids explicitly dropped by the request (--exclude-track).</summary>
    public List<string> ExcludeTracks { get; } = new();

    /// <summary>Path of the request JSON file that seeded this options snapshot.</summary>
    public string RequestJsonPath { get; set; }

    /// <summary>
    /// Structured progress mode. "jsonl" emits one camelCase JSON event per
    /// line on stdout; null keeps the classic human-only progress output.
    /// </summary>
    public string ProgressMode { get; set; }

    // Explicitness tracking for the preset-defaulted fields. The CLI parser
    // sets these when the corresponding option appears on the command line;
    // ApplyRequest marks every field it seeds so the request values survive
    // finalization. Kept internal: the public surface of the options model is
    // unchanged.
    internal bool WidthExplicit { get; set; }
    internal bool HeightExplicit { get; set; }
    internal bool FpsExplicit { get; set; }
    internal bool LayoutExplicit { get; set; }
    internal bool EffectsExplicit { get; set; }
    internal bool ChannelsExplicit { get; set; }
    internal bool AnalysisOverlayExplicit { get; set; }
    internal bool PastExplicit { get; set; }
    internal bool FutureExplicit { get; set; }

    /// <summary>
    /// Seeds every field of this options snapshot from an authoritative
    /// <see cref="Fmp.Application.Contracts.VisualizationRequest"/>. Used by
    /// the --request-json parse path so plan/preview/visualize run the exact
    /// resolved request. Callers then re-apply explicit CLI overrides.
    /// </summary>
    internal void ApplyRequest(Fmp.Application.Contracts.VisualizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Input = request.InputPath;
        OutputDir = Path.GetDirectoryName(request.OutputPath) ?? ".";
        VideoPath = request.OutputPath;

        Preset = MapPreset(request.Preset);
        LayoutMode = MapLayout(request.Layout);
        Width = request.Width;
        Height = request.Height;
        Fps = request.FpsNumerator;
        FpsDenominator = request.FpsDenominator;
        PastSeconds = request.PastSeconds;
        FutureSeconds = request.FutureSeconds;
        RollZoom = request.RollZoom;
        ScopeRatio = request.ScopeRatio;
        ScopePosition = MapScopePosition(request.ScopePosition);
        Channels = request.ChannelSelection == Fmp.Application.Contracts.ChannelSelectionMode.Custom
            ? VisualizationChannelFilter.All
            : MapChannelFilter(request.ChannelSelection);
        IncludeTracks.Clear();
        IncludeTracks.AddRange(request.IncludedTrackIds);
        ExcludeTracks.Clear();
        ExcludeTracks.AddRange(request.ExcludedTrackIds);
        GroupBy = MapGroupBy(request.Grouping);
        TimeGrid = MapTimeGrid(request.TimeGrid);
        Effects = MapEffects(request.Effects);
        NoteColor = MapNoteColor(request.NoteColor);
        Title = request.Title;
        Subtitle = request.Subtitle;
        Credits = request.Credits;
        FontPath = request.FontPath;

        Loops = request.LoopCount;
        Fade = request.FadeSeconds;
        Tail = request.TailSeconds;
        MaxDuration = request.MaximumDurationSeconds ?? 300.0;
        Timeout = request.TimeoutSeconds;
        SampleRate = request.SampleRate;
        SsgGainDb = request.SsgGainDb;
        SpcPitchMode = MapSpcPitch(request.SpcPitch);
        Encoder = MapEncoder(request.Encoder);
        Backend = MapBackend(request.Backend);
        ScopeMode = MapScopeMode(request.ScopeMode);
        FinalQuality = request.FinalQuality;
        StemsOnly = request.StemsOnly;
        Overwrite = request.Overwrite;

        CorrscopePath = request.Tools.CorrscopePath;
        FfmpegPath = request.Tools.FfmpegPath;
        AnalysisPython = request.Tools.AnalysisPython;
        AnalysisCache = request.Tools.AnalysisCache;
        AnalysisOutput = request.Tools.AnalysisOutput;
        AnalysisForce = request.Tools.AnalysisForce;
        AnalysisDetail = MapAnalysisDetail(request.AnalysisDetail);
        AnalysisTimeoutMinutes = request.Tools.AnalysisTimeoutMinutes ?? 10;
        AnalysisOverlay = MapAnalysisOverlay(request.AnalysisOverlay);
        Analysis = request.AnalysisEnabled;
        ExternalToolTimeoutMinutes = request.Tools.ToolTimeoutMinutes ?? 60;
        CorrscopeVideoTemplate = request.Tools.CorrscopeVideoTemplate;
        FmpCom = request.Tools.FmpComPath;
        AssetsDir = request.Tools.AssetsDir;
        SearchPaths.Clear();
        SearchPaths.AddRange(request.Tools.SearchPaths);

        // Every request-seeded field counts as explicitly configured so
        // preset defaulting and backend applicability treat it like a CLI
        // option. Flags whose default value means "not requested" stay false
        // (e.g. ScopeMode Auto, SpcPitch Estimate) so generic backends are not
        // spuriously rejected.
        WidthExplicit = true;
        HeightExplicit = true;
        FpsExplicit = true;
        LayoutExplicit = true;
        EffectsExplicit = true;
        ChannelsExplicit = true;
        AnalysisOverlayExplicit = true;
        PastExplicit = true;
        FutureExplicit = true;
        FmpComExplicit = !string.IsNullOrEmpty(request.Tools.FmpComPath);
        SsgGainExplicit = request.SsgGainDb != 0;
        TimeoutExplicit = request.TimeoutSeconds.HasValue;
        SpcPitchExplicit = request.SpcPitch != Fmp.Application.Contracts.SpcPitchMode.Estimate;
        ScopeModeExplicit = request.ScopeMode != Fmp.Application.Contracts.ScopeMode.Auto;
        BackendExplicit = true;
    }

    private static VisualizationPreset MapPreset(Fmp.Application.Contracts.VisualizationPreset preset) => preset switch
    {
        Fmp.Application.Contracts.VisualizationPreset.Preview => VisualizationPreset.Preview,
        Fmp.Application.Contracts.VisualizationPreset.Final => VisualizationPreset.Final,
        Fmp.Application.Contracts.VisualizationPreset.Diagnostic => VisualizationPreset.Diagnostic,
        _ => VisualizationPreset.Balanced,
    };

    private static VisualizationLayoutMode MapLayout(Fmp.Application.Contracts.VisualizationLayout layout) => layout switch
    {
        Fmp.Application.Contracts.VisualizationLayout.UnifiedRoll => VisualizationLayoutMode.UnifiedRoll,
        Fmp.Application.Contracts.VisualizationLayout.SplitRoll => VisualizationLayoutMode.SplitRoll,
        Fmp.Application.Contracts.VisualizationLayout.Scopes => VisualizationLayoutMode.Scope,
        Fmp.Application.Contracts.VisualizationLayout.Hybrid => VisualizationLayoutMode.Hybrid,
        Fmp.Application.Contracts.VisualizationLayout.Diagnostic => VisualizationLayoutMode.Diagnostic,
        Fmp.Application.Contracts.VisualizationLayout.LegacyDiagnostic => VisualizationLayoutMode.DiagnosticV2,
        Fmp.Application.Contracts.VisualizationLayout.Performance => VisualizationLayoutMode.Performance,
        Fmp.Application.Contracts.VisualizationLayout.ScopeStage => VisualizationLayoutMode.ScopeStage,
        _ => VisualizationLayoutMode.Auto,
    };

    private static VisualizationChannelFilter MapChannelFilter(
        Fmp.Application.Contracts.ChannelSelectionMode mode) => mode switch
    {
        Fmp.Application.Contracts.ChannelSelectionMode.Audible => VisualizationChannelFilter.Audible,
        Fmp.Application.Contracts.ChannelSelectionMode.Semantic => VisualizationChannelFilter.Semantic,
        Fmp.Application.Contracts.ChannelSelectionMode.All => VisualizationChannelFilter.All,
        Fmp.Application.Contracts.ChannelSelectionMode.Custom => VisualizationChannelFilter.All,
        _ => VisualizationChannelFilter.Active,
    };

    private static VisualizationScopePosition MapScopePosition(
        Fmp.Application.Contracts.ScopePosition position) => position switch
    {
        Fmp.Application.Contracts.ScopePosition.Top => VisualizationScopePosition.Top,
        Fmp.Application.Contracts.ScopePosition.Left => VisualizationScopePosition.Left,
        Fmp.Application.Contracts.ScopePosition.Right => VisualizationScopePosition.Right,
        _ => VisualizationScopePosition.Bottom,
    };

    private static VisualizationGroupBy MapGroupBy(Fmp.Application.Contracts.TrackGroupingMode grouping) => grouping switch
    {
        Fmp.Application.Contracts.TrackGroupingMode.Device => VisualizationGroupBy.Device,
        Fmp.Application.Contracts.TrackGroupingMode.Family => VisualizationGroupBy.Family,
        _ => VisualizationGroupBy.None,
    };

    private static VisualizationTimeGrid MapTimeGrid(Fmp.Application.Contracts.TimeGridMode grid) => grid switch
    {
        Fmp.Application.Contracts.TimeGridMode.None => VisualizationTimeGrid.None,
        Fmp.Application.Contracts.TimeGridMode.Authoritative => VisualizationTimeGrid.Authoritative,
        Fmp.Application.Contracts.TimeGridMode.Analytical => VisualizationTimeGrid.Analytical,
        _ => VisualizationTimeGrid.Automatic,
    };

    private static EffectsMode MapEffects(Fmp.Application.Contracts.VisualizationEffects effects) => effects switch
    {
        Fmp.Application.Contracts.VisualizationEffects.None => EffectsMode.None,
        Fmp.Application.Contracts.VisualizationEffects.Diagnostic => EffectsMode.Diagnostic,
        Fmp.Application.Contracts.VisualizationEffects.Cinematic => EffectsMode.Cinematic,
        // The request model exposes four effect presets; CLI-only "all" maps to
        // the closest request-representable value.
        _ => EffectsMode.Minimal,
    };

    private static NoteColorMode MapNoteColor(Fmp.Application.Contracts.NoteColorMode mode) => mode switch
    {
        Fmp.Application.Contracts.NoteColorMode.Channel => NoteColorMode.Channel,
        Fmp.Application.Contracts.NoteColorMode.PitchClass => NoteColorMode.Pitch,
        _ => NoteColorMode.Instrument,
    };

    private static SpcPitchMode MapSpcPitch(Fmp.Application.Contracts.SpcPitchMode mode) => mode switch
    {
        Fmp.Application.Contracts.SpcPitchMode.Relative => SpcPitchMode.Relative,
        _ => SpcPitchMode.Estimate,
    };

    private static VideoEncoder MapEncoder(Fmp.Application.Contracts.VideoEncoder encoder) => encoder switch
    {
        Fmp.Application.Contracts.VideoEncoder.LibX264 => VideoEncoder.LibX264,
        Fmp.Application.Contracts.VideoEncoder.Nvenc => VideoEncoder.Nvenc,
        _ => VideoEncoder.Auto,
    };

    private static string MapBackend(Fmp.Application.Contracts.BackendPreference backend) => backend switch
    {
        Fmp.Application.Contracts.BackendPreference.Fmp => "fmp",
        Fmp.Application.Contracts.BackendPreference.Mdplayer => "mdplayer",
        _ => "auto",
    };

    private static string MapScopeMode(Fmp.Application.Contracts.ScopeMode mode) => mode switch
    {
        Fmp.Application.Contracts.ScopeMode.Master => "master",
        Fmp.Application.Contracts.ScopeMode.Device => "device",
        Fmp.Application.Contracts.ScopeMode.Channel => "channel",
        Fmp.Application.Contracts.ScopeMode.Off => "off",
        _ => "auto",
    };

    private static AnalysisDetail MapAnalysisDetail(Fmp.Application.Contracts.AnalysisDetail detail) => detail switch
    {
        Fmp.Application.Contracts.AnalysisDetail.Minimal => AnalysisDetail.Minimal,
        Fmp.Application.Contracts.AnalysisDetail.Full => AnalysisDetail.Full,
        _ => AnalysisDetail.Standard,
    };

    private static string MapAnalysisOverlay(Fmp.Application.Contracts.AnalysisOverlayMode mode) => mode switch
    {
        Fmp.Application.Contracts.AnalysisOverlayMode.None => "none",
        Fmp.Application.Contracts.AnalysisOverlayMode.Standard => "standard",
        Fmp.Application.Contracts.AnalysisOverlayMode.Full => "full",
        _ => "minimal",
    };
}

internal static class VisualizeOptionsParser
{
    public static VisualizeOptions Parse(string[] args)
    {
        try { return ParseCore(args); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return null;
        }
    }

    internal static VisualizeOptions ParseStrict(string[] args)
        => ParseCore(args);

    /// <summary>
    /// Parses a request-seeded options snapshot: <c>--request-json PATH</c>
    /// followed by optional explicit CLI overrides. Used by the plan and
    /// preview commands.
    /// </summary>
    internal static VisualizeOptions ParseForRequest(string requestJsonPath, string[] overrideArgs)
    {
        var args = new string[(overrideArgs?.Length ?? 0) + 2];
        args[0] = "--request-json";
        args[1] = requestJsonPath;
        if (overrideArgs != null)
            Array.Copy(overrideArgs, 0, args, 2, overrideArgs.Length);
        return ParseCore(args);
    }

    private static VisualizeOptions ParseCore(string[] args)
    {
        int requestIndex = FindRequestJsonIndex(args);
        if (requestIndex < 0)
        {
            var options = new VisualizeOptions();
            ParseArgsInto(options, args);
            return Finalize(options);
        }

        return ParseWithRequest(args, requestIndex);
    }

    private static VisualizeOptions ParseWithRequest(string[] args, int requestIndex)
    {
        // Deterministic rule: the first --request-json must be the first
        // token; anything before it (option or positional) is ambiguous.
        if (requestIndex > 0)
            throw new ArgumentException("ambiguous: options before --request-json are not allowed");

        int occurrences = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (IsRequestJsonToken(args[i]))
                occurrences++;
        }
        if (occurrences > 1)
            throw new ArgumentException("duplicate --request-json option");

        string token = args[requestIndex];
        string path;
        int overrideStart;
        if (token.StartsWith("--request-json=", StringComparison.Ordinal))
        {
            path = token["--request-json=".Length..];
            overrideStart = requestIndex + 1;
        }
        else
        {
            if (requestIndex + 1 >= args.Length)
                throw new ArgumentException("missing value for --request-json");
            path = args[requestIndex + 1];
            overrideStart = requestIndex + 2;
        }

        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("missing value for --request-json");

        Fmp.Application.Contracts.VisualizationRequest request;
        try
        {
            request = VisualizationRequestSerializer.ReadFromFile(path);
        }
        catch (VisualizationRequestException ex)
        {
            throw new ArgumentException(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"reading request file — {ex.Message}");
        }

        var options = new VisualizeOptions { RequestJsonPath = path };
        options.ApplyRequest(request);

        string[] overrideArgs = args.Skip(overrideStart).ToArray();
        if (overrideArgs.Length > 0)
        {
            // Re-apply only the explicitly-set CLI overrides on top of the
            // request-seeded values. The single-pass parser only mutates
            // fields whose options actually appear, so this is equivalent to
            // "copy the fields whose flag is explicit".
            ParseArgsInto(options, overrideArgs);

            // --include-track/--exclude-track are additive; drop duplicates
            // when a CLI override repeats a request-selected track id.
            var include = options.IncludeTracks.Distinct(StringComparer.Ordinal).ToArray();
            options.IncludeTracks.Clear();
            options.IncludeTracks.AddRange(include);
            var exclude = options.ExcludeTracks.Distinct(StringComparer.Ordinal).ToArray();
            options.ExcludeTracks.Clear();
            options.ExcludeTracks.AddRange(exclude);
        }

        return Finalize(options);
    }

    private static bool IsRequestJsonToken(string token)
        => token == "--request-json" || token.StartsWith("--request-json=", StringComparison.Ordinal);

    private static int FindRequestJsonIndex(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (IsRequestJsonToken(args[i]))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Applies the shared finalization: preset/final-quality resolution,
    /// preset defaults for non-explicit fields, then ValidateCommon and the
    /// numeric sanity checks. Byte-identical to the pre-refactor behavior when
    /// --request-json is absent.
    /// </summary>
    internal static VisualizeOptions Finalize(VisualizeOptions options)
    {
        if (options.FinalQuality)
            options.Preset = VisualizationPreset.Final;
        if (options.Preset == VisualizationPreset.Final)
            options.FinalQuality = true;

        VisualizationPresetValues preset = VisualizationOptionParsing.PresetValues(options.Preset);
        if (!options.WidthExplicit) options.Width = preset.Width;
        if (!options.HeightExplicit) options.Height = preset.Height;
        if (!options.FpsExplicit) options.Fps = preset.Fps;
        if (!options.LayoutExplicit) options.LayoutMode = preset.Layout;
        if (!options.EffectsExplicit) options.Effects = preset.Effects;
        if (!options.ChannelsExplicit) options.Channels = preset.Channels;
        if (!options.AnalysisOverlayExplicit) options.AnalysisOverlay = preset.AnalysisOverlay;
        if (!options.PastExplicit && !options.FutureExplicit)
            (options.PastSeconds, options.FutureSeconds) = VisualizationOptionParsing.TimeScaleValues(options.TimeScale);

        ApplyLayoutTemplate(options);
        if (!string.IsNullOrWhiteSpace(options.PalettePath))
        {
            try
            {
                options.Palette = VisualizationPalette.ParseJson(
                    File.ReadAllText(options.PalettePath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or JsonException or ArgumentException or InvalidOperationException)
            {
                throw new ArgumentException(
                    $"could not load palette '{options.PalettePath}': {ex.Message}", ex);
            }
        }

        options.ValidateCommon();

        if (options.SampleRate <= 0 || options.Loops <= 0
            || options.Fade < 0 || options.Tail < 0
            || options.MaxDuration <= 0 || options.Timeout is <= 0
            || options.Width < 480 || options.Height < 270
            || options.Fps <= 0 || options.FpsDenominator <= 0
            || !double.IsFinite(options.PastSeconds) || options.PastSeconds <= 0
            || !double.IsFinite(options.FutureSeconds) || options.FutureSeconds <= 0
            || !double.IsFinite(options.RollZoom) || options.RollZoom <= 0
            || options.ScopeHeight is <= 0 || options.TimelineHeight is <= 0
            || options.ScopeRatio is < 0 or > 0.8
            || options.PastSeconds is < 0.1 or > 15
            || options.FutureSeconds is < 0.1 or > 15
            || options.PastSeconds + options.FutureSeconds is < 0.5 or > 20
            || options.ExternalToolTimeoutMinutes <= 0
            || options.AnalysisTimeoutMinutes <= 0)
            throw new ArgumentException("invalid visualization numeric option");

        if (options.MotionBlurSamples is < 1 or > 8)
            throw new ArgumentException("motion blur samples must be between 1 and 8");

        return options;
    }

    private static void ParseArgsInto(VisualizeOptions options, string[] args)
    {
        var reader = new ArgumentReader(args);

        while (reader.HasMore)
        {
            if (reader.TryReadOption(out string name, out string value))
            {
                if (RenderOptionsParser.TryParse(ref reader, name, options))
                    continue;

                switch (name)
                {
                    case "-o":
                    case "--output": options.OutputDir = reader.RequireValue(name); break;
                    case "--video": options.VideoPath = reader.RequireValue(name); break;
                    case "--corrscope": options.CorrscopePath = reader.RequireValue(name); break;
                    case "--ffmpeg": options.FfmpegPath = reader.RequireValue(name); break;
                    case "--title": options.Title = reader.RequireValue(name); break;
                    case "--subtitle": options.Subtitle = reader.RequireValue(name); break;
                    case "--credits": options.Credits = reader.RequireValue(name); break;
                    case "--font": options.FontPath = reader.RequireValue(name); break;
                    case "--width": options.Width = reader.ReadInt(name); options.WidthExplicit = true; break;
                    case "--height": options.Height = reader.ReadInt(name); options.HeightExplicit = true; break;
                    case "--fps": options.Fps = reader.ReadInt(name); options.FpsExplicit = true; break;
                    case "--fps-denominator": options.FpsDenominator = reader.ReadInt(name); break;
                    case "--tool-timeout-minutes": options.ExternalToolTimeoutMinutes = reader.ReadInt(name); break;
                    case "--duration": options.MaxDuration = reader.ReadDouble(name); break;
                    case "--timeout":
                        options.Timeout = reader.ReadDouble(name);
                        options.TimeoutExplicit = true;
                        break;
                    case "--preset": options.Preset = VisualizationOptionParsing.ParsePreset(reader.RequireValue(name)); break;
                    case "--past-seconds": options.PastSeconds = reader.ReadDouble(name); options.PastExplicit = true; break;
                    case "--future-seconds": options.FutureSeconds = reader.ReadDouble(name); options.FutureExplicit = true; break;
                    case "--time-window":
                        (options.PastSeconds, options.FutureSeconds) = VisualizationOptionParsing.ParseTimeWindow(reader.RequireValue(name));
                        options.PastExplicit = options.FutureExplicit = true;
                        break;
                    case "--time-scale":
                        options.TimeScale = VisualizationOptionParsing.ParseTimeScale(reader.RequireValue(name));
                        break;
                    case "--roll-zoom": options.RollZoom = reader.ReadDouble(name); break;
                    case "--scope-height": options.ScopeHeight = reader.ReadInt(name); break;
                    case "--timeline-height": options.TimelineHeight = reader.ReadInt(name); break;
                    case "--scope-ratio": options.ScopeRatio = reader.ReadDouble(name); break;
                    case "--scope-position": options.ScopePosition = VisualizationOptionParsing.ParseScopePosition(reader.RequireValue(name)); break;
                    case "--group-by": options.GroupBy = VisualizationOptionParsing.ParseGroupBy(reader.RequireValue(name)); break;
                    case "--channels": options.Channels = ParseChannelsOption(reader.RequireValue(name)); options.ChannelsExplicit = true; break;
                    case "--include-track": options.IncludeTracks.Add(reader.RequireValue(name)); break;
                    case "--exclude-track": options.ExcludeTracks.Add(reader.RequireValue(name)); break;
                    case "--time-grid": options.TimeGrid = VisualizationOptionParsing.ParseTimeGrid(reader.RequireValue(name)); break;
                    case "--stems-only" when value == null: options.StemsOnly = true; break;
                    case "--spc-stems" when value == null:
                        options.SpcStems = true;
                        options.SpcStemsExplicit = true;
                        break;
                    case "--spc-pitch":
                        options.SpcPitchMode = ParseSpcPitch(reader.RequireValue(name));
                        options.SpcPitchExplicit = true;
                        break;
                    case "--backend":
                        options.Backend = ParseBackend(reader.RequireValue(name));
                        options.BackendExplicit = true;
                        break;
                    case "--scopes":
                        options.ScopeMode = ParseScopeMode(reader.RequireValue(name));
                        options.ScopeModeExplicit = true;
                        break;
                    case "--overwrite" when value == null: options.Overwrite = true; break;
                    case "--quiet" when value == null: options.Quiet = true; break;
                    case "--json" when value == null: options.Json = true; break;
                    case "--corrscope-video-template": options.CorrscopeVideoTemplate = reader.RequireValue(name); break;
                    case "--final-quality" when value == null: options.FinalQuality = true; break;
                    case "--print-layout" when value == null: options.PrintLayout = true; break;
                    case "--layout-json": options.LayoutJson = reader.RequireValue(name); break;
                    case "--layout-template": options.LayoutTemplatePath = reader.RequireValue(name); break;
                    case "--palette": options.PalettePath = reader.RequireValue(name); break;
                    case "--preview-html": options.PreviewHtmlPath = reader.RequireValue(name); break;
                    case "--diagnostic-pages": options.DiagnosticPagesPath = reader.RequireValue(name); break;
                    case "--renderer": options.Renderer = ParseRenderer(reader.RequireValue(name)); break;
                    case "--motion-blur-samples": options.MotionBlurSamples = reader.ReadInt(name); break;
                    case "--encoder": options.Encoder = ParseEncoder(reader.RequireValue(name)); break;
                    case "--effects": options.Effects = ParseEffects(reader.RequireValue(name)); options.EffectsExplicit = true; break;
                    case "--note-color": options.NoteColor = ParseNoteColor(reader.RequireValue(name)); break;
                    case "--layout": options.LayoutMode = ParseLayout(reader.RequireValue(name)); options.LayoutExplicit = true; break;
                    case "--analysis" when value == null: options.Analysis = true; break;
                    case "--analysis-python": options.AnalysisPython = reader.RequireValue(name); break;
                    case "--analysis-output": options.AnalysisOutput = reader.RequireValue(name); break;
                    case "--analysis-cache": options.AnalysisCache = reader.RequireValue(name); break;
                    case "--analysis-detail": options.AnalysisDetail = ParseAnalysisDetail(reader.RequireValue(name)); break;
                    case "--analysis-force" when value == null: options.AnalysisForce = true; break;
                    case "--analysis-timeout-minutes": options.AnalysisTimeoutMinutes = reader.ReadInt(name); break;
                    case "--analysis-overlay": options.AnalysisOverlay = ParseAnalysisOverlay(reader.RequireValue(name)); options.AnalysisOverlayExplicit = true; break;
                    case "--progress": options.ProgressMode = ParseProgressMode(reader.RequireValue(name)); break;
                    default: throw new ArgumentException($"unknown option '{name}'");
                }
            }
            else
            {
                string positional = reader.Next();
                if (positional == "--") continue;
                if (options.Input != null)
                    throw new ArgumentException($"unexpected argument '{positional}'");
                options.Input = positional;
            }
        }
    }

    /// <summary>
    /// Channel filter parsing. "custom" is the request-driven channel mode and
    /// maps to the all-inclusive filter; the topology-level include/exclude
    /// track lists carry the actual selection.
    /// </summary>
    private static VisualizationChannelFilter ParseChannelsOption(string raw)
    {
        if (string.Equals(raw?.Trim(), "custom", StringComparison.OrdinalIgnoreCase))
            return VisualizationChannelFilter.All;
        return VisualizationOptionParsing.ParseChannels(raw);
    }

    private static string ParseProgressMode(string raw)
    {
        string mode = raw?.Trim().ToLowerInvariant();
        return mode switch
        {
            "jsonl" => "jsonl",
            _ => throw new ArgumentException($"unknown progress mode '{raw}' (expected jsonl)"),
        };
    }

    private static string ParseBackend(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "auto" => "auto",
        "fmp" => "fmp",
        "mdplayer" => "mdplayer",
        _ => throw new ArgumentException($"unknown backend '{raw}' (expected auto, fmp, or mdplayer)"),
    };

    private static string ParseScopeMode(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "auto" => "auto",
        "master" => "master",
        "device" => "device",
        "channel" => "channel",
        "off" => "off",
        _ => throw new ArgumentException(
            $"unknown scope mode '{raw}' (expected auto, master, device, channel, or off)"),
    };

    private static SpcPitchMode ParseSpcPitch(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "estimate" => SpcPitchMode.Estimate,
        "relative" => SpcPitchMode.Relative,
        _ => throw new ArgumentException(
            $"unknown spc pitch mode '{raw}' (expected estimate or relative)"),
    };

    private static VideoEncoder ParseEncoder(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "auto" => VideoEncoder.Auto,
        "libx264" or "x264" or "software" or "cpu" => VideoEncoder.LibX264,
        "nvenc" or "h264_nvenc" or "gpu" or "hardware" => VideoEncoder.Nvenc,
        _ => throw new ArgumentException($"unknown encoder '{raw}' (expected libx264 or nvenc)"),
    };

    private static EffectsMode ParseEffects(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "minimal" => EffectsMode.Minimal,
        "diagnostic" => EffectsMode.Diagnostic,
        "cinematic" => EffectsMode.Cinematic,
        "all" => EffectsMode.All,
        "none" => EffectsMode.None,
        _ => throw new ArgumentException($"unknown effects mode '{raw}' (expected minimal, diagnostic, cinematic, all, or none)"),
    };

    private static NoteColorMode ParseNoteColor(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "instrument" => NoteColorMode.Instrument,
        "pitch" => NoteColorMode.Pitch,
        "channel" => NoteColorMode.Channel,
        _ => throw new ArgumentException($"unknown note-color mode '{raw}' (expected instrument, pitch, or channel)"),
    };

    private static VisualizationLayoutMode ParseLayout(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "auto" => VisualizationLayoutMode.Auto,
        "performance" => VisualizationLayoutMode.Performance,
        "scope-stage" or "scopestage" => VisualizationLayoutMode.ScopeStage,
        "diagnostic" => VisualizationLayoutMode.Diagnostic,
        "diagnostic-v2" => VisualizationLayoutMode.DiagnosticV2,
        "focus" => VisualizationLayoutMode.Focus,
        "scope" => VisualizationLayoutMode.Scope,
        "split-roll" or "split" => VisualizationLayoutMode.SplitRoll,
        "unified-roll" or "unified" => VisualizationLayoutMode.UnifiedRoll,
        "hybrid" => VisualizationLayoutMode.Hybrid,
        _ => throw new ArgumentException($"unknown layout '{raw}' (expected auto, performance, scope-stage, diagnostic, or a legacy name: unified, split, scope, hybrid, diagnostic-v2, focus)"),
    };

    private static VisualizationRendererMode ParseRenderer(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "auto" => VisualizationRendererMode.Auto,
        "cpu" or "software" => VisualizationRendererMode.Cpu,
        "gpu" or "hardware" => VisualizationRendererMode.Gpu,
        _ => throw new ArgumentException("unknown renderer (expected auto, cpu, or gpu)"),
    };

    private static void ApplyLayoutTemplate(VisualizeOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.LayoutTemplatePath))
            return;

        VisualizationLayoutTemplate template;
        try
        {
            template = VisualizationLayoutTemplate.ParseJson(
                File.ReadAllText(options.LayoutTemplatePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or ArgumentException or InvalidOperationException)
        {
            throw new ArgumentException(
                $"could not load layout template '{options.LayoutTemplatePath}': {ex.Message}", ex);
        }

        if (!string.IsNullOrWhiteSpace(template.Layout))
            options.LayoutMode = ParseLayout(template.Layout);
        if (!string.IsNullOrWhiteSpace(template.Channels))
            options.Channels = VisualizationOptionParsing.ParseChannels(template.Channels);
        if (!string.IsNullOrWhiteSpace(template.GroupBy))
            options.GroupBy = VisualizationOptionParsing.ParseGroupBy(template.GroupBy);
        if (!string.IsNullOrWhiteSpace(template.ScopePosition))
            options.ScopePosition = VisualizationOptionParsing.ParseScopePosition(template.ScopePosition);
        if (template.ScopeRatio is double scopeRatio)
            options.ScopeRatio = scopeRatio;
        if (template.PastSeconds is double past)
            options.PastSeconds = past;
        if (template.FutureSeconds is double future)
            options.FutureSeconds = future;
        if (template.RollZoom is double zoom)
            options.RollZoom = zoom;
    }

    private static AnalysisDetail ParseAnalysisDetail(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "minimal" => AnalysisDetail.Minimal,
        "standard" => AnalysisDetail.Standard,
        "full" => AnalysisDetail.Full,
        _ => throw new ArgumentException($"unknown analysis detail '{raw}' (expected minimal, standard, or full)"),
    };

    private static string ParseAnalysisOverlay(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "none" or "minimal" or "standard" or "full" => raw.Trim().ToLowerInvariant(),
        _ => throw new ArgumentException($"unknown analysis overlay '{raw}' (expected none, minimal, or standard; full is also accepted)"),
    };
}
