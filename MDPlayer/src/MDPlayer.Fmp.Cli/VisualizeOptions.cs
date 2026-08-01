using Fmp.Core.Visualization.Rendering;

namespace Fmp.Cli;

/// <summary>Data and parsing for the FMP visualization command.</summary>
internal sealed class VisualizeOptions : RenderSettings
{
    public string Input { get; set; }
    public string OutputDir { get; set; }
    public string VideoPath { get; set; }
    public string CorrscopePath { get; set; }
    public string FfmpegPath { get; set; }
    public int Width { get; set; } = 1440;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 30;
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
    public bool FinalQuality { get; set; }
    public VideoEncoder Encoder { get; set; } = VideoEncoder.Auto;
    public EffectsMode Effects { get; set; } = EffectsMode.All;
    public NoteColorMode NoteColor { get; set; } = NoteColorMode.Instrument;
    public VisualizationLayoutMode LayoutMode { get; set; } = VisualizationLayoutMode.Diagnostic;
    public bool Analysis { get; set; }
    public string AnalysisPython { get; set; }
    public string AnalysisOutput { get; set; }
    public string AnalysisCache { get; set; }
    public AnalysisDetail AnalysisDetail { get; set; } = AnalysisDetail.Standard;
    public bool AnalysisForce { get; set; }
    public int AnalysisTimeoutMinutes { get; set; } = 10;
    public string AnalysisOverlay { get; set; } = "minimal";
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

    private static VisualizeOptions ParseCore(string[] args)
    {
        var options = new VisualizeOptions();
        var reader = new ArgumentReader(args);
        bool widthExplicit = false, heightExplicit = false, fpsExplicit = false;

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
                    case "--width": options.Width = reader.ReadInt(name); widthExplicit = true; break;
                    case "--height": options.Height = reader.ReadInt(name); heightExplicit = true; break;
                    case "--fps": options.Fps = reader.ReadInt(name); fpsExplicit = true; break;
                    case "--fps-denominator": options.FpsDenominator = reader.ReadInt(name); break;
                    case "--tool-timeout-minutes": options.ExternalToolTimeoutMinutes = reader.ReadInt(name); break;
                    case "--duration": options.MaxDuration = reader.ReadDouble(name); break;
                    case "--timeout": options.Timeout = reader.ReadDouble(name); break;
                    case "--stems-only" when value == null: options.StemsOnly = true; break;
                    case "--overwrite" when value == null: options.Overwrite = true; break;
                    case "--quiet" when value == null: options.Quiet = true; break;
                    case "--json" when value == null: options.Json = true; break;
                    case "--corrscope-video-template": options.CorrscopeVideoTemplate = reader.RequireValue(name); break;
                    case "--final-quality" when value == null: options.FinalQuality = true; break;
                    case "--encoder": options.Encoder = ParseEncoder(reader.RequireValue(name)); break;
                    case "--effects": options.Effects = ParseEffects(reader.RequireValue(name)); break;
                    case "--note-color": options.NoteColor = ParseNoteColor(reader.RequireValue(name)); break;
                    case "--layout": options.LayoutMode = ParseLayout(reader.RequireValue(name)); break;
                    case "--analysis" when value == null: options.Analysis = true; break;
                    case "--analysis-python": options.AnalysisPython = reader.RequireValue(name); break;
                    case "--analysis-output": options.AnalysisOutput = reader.RequireValue(name); break;
                    case "--analysis-cache": options.AnalysisCache = reader.RequireValue(name); break;
                    case "--analysis-detail": options.AnalysisDetail = ParseAnalysisDetail(reader.RequireValue(name)); break;
                    case "--analysis-force" when value == null: options.AnalysisForce = true; break;
                    case "--analysis-timeout-minutes": options.AnalysisTimeoutMinutes = reader.ReadInt(name); break;
                    case "--analysis-overlay": options.AnalysisOverlay = ParseAnalysisOverlay(reader.RequireValue(name)); break;
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

        if (options.FinalQuality)
        {
            if (!widthExplicit) options.Width = 1920;
            if (!heightExplicit) options.Height = 1080;
            if (!fpsExplicit) options.Fps = 60;
        }

        if (options.SampleRate <= 0 || options.Loops <= 0
            || options.Fade < 0 || options.Tail < 0
            || options.MaxDuration <= 0 || options.Timeout is <= 0
            || options.Width < 480 || options.Height < 270
            || options.Fps <= 0 || options.FpsDenominator <= 0
            || options.ExternalToolTimeoutMinutes <= 0
            || options.AnalysisTimeoutMinutes <= 0)
            throw new ArgumentException("invalid visualization numeric option");

        return options;
    }

    private static VideoEncoder ParseEncoder(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "auto" => VideoEncoder.Auto,
        "libx264" or "x264" or "software" or "cpu" => VideoEncoder.LibX264,
        "nvenc" or "h264_nvenc" or "gpu" or "hardware" => VideoEncoder.Nvenc,
        _ => throw new ArgumentException($"unknown encoder '{raw}' (expected libx264 or nvenc)"),
    };

    private static EffectsMode ParseEffects(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "all" => EffectsMode.All,
        "none" => EffectsMode.None,
        _ => throw new ArgumentException($"unknown effects mode '{raw}' (expected all or none)"),
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
        "diagnostic" => VisualizationLayoutMode.Diagnostic,
        "focus" => VisualizationLayoutMode.Focus,
        _ => throw new ArgumentException($"unknown layout '{raw}' (expected diagnostic or focus)"),
    };

    private static AnalysisDetail ParseAnalysisDetail(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "minimal" => AnalysisDetail.Minimal,
        "standard" => AnalysisDetail.Standard,
        "full" => AnalysisDetail.Full,
        _ => throw new ArgumentException($"unknown analysis detail '{raw}' (expected minimal, standard, or full)"),
    };

    private static string ParseAnalysisOverlay(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "none" or "minimal" or "standard" => raw.Trim().ToLowerInvariant(),
        _ => throw new ArgumentException($"unknown analysis overlay '{raw}' (expected none, minimal, or standard)"),
    };
}
