namespace Fmp.Cli;

public class Program
{
    const int EXIT_SUCCESS = 0;
    const int EXIT_INVALID_ARGS = 2;
    const int EXIT_UNSUPPORTED_FORMAT = 3;
    const int EXIT_MISSING_RUNTIME_ASSET = 4;
    const int EXIT_EMULATION_FAILURE = 7;

    public static int Main(string[] args)
    {
        // Register CodePages for CP932 support
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        string command = args[0].ToLowerInvariant();

        try
        {
            switch (command)
            {
                case "render":
                    return VisualizationRenderCommand.Handle(args.Skip(1).ToArray());
                case "batch":
                    return BatchCommand.Handle(args.Skip(1).ToArray());
                case "inspect":
                    return InspectCommand.Handle(args.Skip(1).ToArray());
                case "visualize":
                    Console.Error.WriteLine("warning: 'visualize' is deprecated; use 'render'");
                    return VisualizationRenderCommand.Handle(args.Skip(1).ToArray());
                case "analyze":
                    return AnalyzeCommand.Handle(args.Skip(1).ToArray());
                case "plan":
                    return PlanCommand.Handle(args.Skip(1).ToArray());
                case "preview":
                    return PreviewCommand.Handle(args.Skip(1).ToArray());
                case "review":
                    return ReviewCommand.Handle(args.Skip(1).ToArray());
                case "midi":
                    return MidiCommand.Handle(args.Skip(1).ToArray());
                default:
                    Console.Error.WriteLine($"error: unknown command '{command}'");
                    PrintUsage();
                    return EXIT_INVALID_ARGS;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return EXIT_EMULATION_FAILURE;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  mdplayer-render render <input> [options]");
        Console.WriteLine("  mdplayer-render inspect <input> [options]");
        Console.WriteLine("  mdplayer-render plan <input> [--request-json PATH] [options]");
        Console.WriteLine("  mdplayer-render preview <input> [--request-json PATH] [options]");
        Console.WriteLine("  mdplayer-render review --manifest PATH --output DIR [options]");
        Console.WriteLine("  mdplayer-render analyze <input> [options]");
        Console.WriteLine("  mdplayer-render midi <input> --output PATH [options]");
        Console.WriteLine();
        Console.WriteLine("Render options:");
        Console.WriteLine("  -o, --output PATH          Output video path");
        Console.WriteLine("  --composition diagnostic       Composition (default: diagnostic)");
        Console.WriteLine("  --quality draft|standard|final  Render quality profile (default: standard)");
        Console.WriteLine("  --width PIXELS             Video width (default: 1920)");
        Console.WriteLine("  --height PIXELS            Video height (default: 1080)");
        Console.WriteLine("  --fps RATE                 Video frame rate (default: 60)");
        Console.WriteLine("  --tracks active|all|custom  Track selection (default: active)");
        Console.WriteLine("  --include-track ID         Keep only the listed tracks (repeatable)");
        Console.WriteLine("  --exclude-track ID         Drop the listed tracks (repeatable)");
        Console.WriteLine("  --include-inactive         Include inactive tracks in Diagnostic");
        Console.WriteLine("  --past SECONDS             Past time window (default: 0.8)");
        Console.WriteLine("  --future SECONDS           Future time window (default: 3.2)");
        Console.WriteLine("  --time-grid none|automatic|authoritative|analytical");
        Console.WriteLine("  --structure off|automatic  Structural analysis overlay");
        Console.WriteLine("  --effects off|subtle|cinematic  Visual effect preset (default: subtle)");
        Console.WriteLine("  --note-color instrument|channel|pitch");
        Console.WriteLine("  --palette default|accessible|monochrome");
        Console.WriteLine("  --scope-fps FPS             Scope render cadence (default: min(output, 30))");
        Console.WriteLine("  --scope-opacity 0.05..1.0   Waveform layer opacity over the panel body (default: 1.0)");
        Console.WriteLine("  --title TEXT --subtitle TEXT --credits TEXT --font PATH");
        Console.WriteLine("  --loops COUNT --fade SECONDS --tail SECONDS --max-duration SECONDS");
        Console.WriteLine("  --sample-rate HZ           Capture sample rate (default: 48000)");
        Console.WriteLine("--opna-backend <mdsound|native-audio>");
        Console.WriteLine("    Select the YM2608 audio backend for FMP rendering.");
        Console.WriteLine("    Default: mdsound.");
        Console.WriteLine("  --dump-furnace-assets DIR  Export FM instruments as Furnace .tfi (FMP/OVI and VGM/VGZ)");
        Console.WriteLine("  --encoder auto|x264|nvenc  Encoder (default: auto)");
        Console.WriteLine("  --overwrite                Overwrite existing output");
        Console.WriteLine("  --progress human|jsonl     Progress output mode");
        Console.WriteLine("  --quiet                    Suppress progress output");
        Console.WriteLine();
        Console.WriteLine("Runtime tool options (not saved in projects):");
        Console.WriteLine("  --fmp-com PATH             Path to FMP.COM");
        Console.WriteLine("  --assets-dir DIR           Assets directory");
        Console.WriteLine("  -I, --search-path PATH     Supplemental file search paths");
        Console.WriteLine("  --corrscope PATH           Corrscope executable path");
        Console.WriteLine("  --ffmpeg PATH              FFmpeg executable path");
        Console.WriteLine("  --analysis-python PATH     Python interpreter for symbolic analysis");
        Console.WriteLine("  --tool-timeout-minutes N   External tool timeout (default: 60)");
        Console.WriteLine();
        Console.WriteLine("Internal capture reuse (runtime-only; not saved in projects):");
        Console.WriteLine("  --capture-dir DIR          Reuse a validated prepared capture");
        Console.WriteLine("  --capture-key KEY          Expected capture identity");
        Console.WriteLine();
        Console.WriteLine("Plan options:");
        Console.WriteLine("  --request-json PATH        Request JSON file");
        Console.WriteLine("  --timeline PATH            Reuse an existing visualization timeline");
        Console.WriteLine("  --timeline-out PATH        Write the captured/reused timeline to PATH");
        Console.WriteLine("  --json                     Machine-readable plan JSON on stdout");
        Console.WriteLine();
        Console.WriteLine("Preview options:");
        Console.WriteLine("  --request-json PATH        Request JSON file (required)");
        Console.WriteLine("  --time SECONDS             Still frame time (default: 0)");
        Console.WriteLine("  --output PATH              Still output PNG path");
        Console.WriteLine("  --width N --height N       Preview size overrides (cap 1920)");
        Console.WriteLine($"  {Fmp.Cli.PreviewCommand.FidelityHelpLine}  Still fidelity (default: accurate)");
        Console.WriteLine("  --timeline PATH            Reuse an existing visualization timeline");
        Console.WriteLine("  --timeline-out PATH        Write the captured/reused timeline to PATH");
        Console.WriteLine("  --json                     Machine-readable preview metadata on stdout");
        Console.WriteLine("  --motion                   Render a motion sequence instead of a still");
        Console.WriteLine("  --start SECONDS --duration SECONDS --fps N  Motion range (defaults 0 / 3 / 15)");
        Console.WriteLine("  --max-width N --max-height N               Motion size cap (defaults 960 / 540)");
        Console.WriteLine("  --output-dir DIR           Motion frame output directory");
        Console.WriteLine();
        Console.WriteLine("Review options:");
        Console.WriteLine("  --manifest PATH            Review manifest JSON");
        Console.WriteLine("  --corpus DIR               Real-file corpus (or MDPLAYER_REVIEW_CORPUS)");
        Console.WriteLine("  --output DIR               Review artifact directory");
        Console.WriteLine("  --file TEXT --chip NAME --moment NAME");
        Console.WriteLine("  --composition diagnostic");
        Console.WriteLine("  --resolution 720p|1080p   --keep-existing");
        Console.WriteLine("  --allow-missing-chips");
        Console.WriteLine();
        Console.WriteLine("Analyze options:");
        Console.WriteLine("  --timeline PATH            Reuse an existing visualization timeline");
        Console.WriteLine("  --analysis-python PATH     Python interpreter (or MDPLAYER_ANALYSIS_PYTHON)");
        Console.WriteLine("  --analysis-output DIR      Analysis output directory");
        Console.WriteLine("  --analysis-cache PATH     Shared analysis cache path");
        Console.WriteLine("  --analysis-detail minimal|standard|full");
        Console.WriteLine("  --analysis-force          Ignore an existing cache result");
        Console.WriteLine("  --analysis-timeout-minutes N  Worker timeout (default: 10)");
        Console.WriteLine();
        Console.WriteLine("MIDI options:");
        Console.WriteLine("  -o, --output PATH         Output .mid file (required)");
        Console.WriteLine("  --timeline PATH           Reuse an existing visualization timeline");
        Console.WriteLine("  --ppq N                   Ticks per quarter note (default: 960)");
        Console.WriteLine("  --tempo-source auto|driver|symbolic|fixed");
        Console.WriteLine("  --bpm NUMBER             Fixed tempo override");
        Console.WriteLine("  --beat-offset-samples N  Phase override (beat grid vs sample zero)");
        Console.WriteLine("  --meter NUM/DEN          Time signature, e.g. 4/4");
        Console.WriteLine("  --first-downbeat-sample N  Sample of the first bar start");
        Console.WriteLine("  --quantize off|1/8|1/16|1/32  Grid snapping (default: off)");
        Console.WriteLine("  --timing-report PATH     Write a JSON timing-confidence report");
        Console.WriteLine("  --strict-timing          Fail on ambiguous tempo / unknown phase");
        Console.WriteLine("  --no-pitch-bend          Disable pitch-bend export");
        Console.WriteLine("  --bend-range SEMITONES   Pitch-bend depth via RPN (default: 2)");
        Console.WriteLine("  --no-percussion-channel  Do not force channel 9 for drums");
        Console.WriteLine("  --track-layout physical|instrument  Track grouping (default: physical)");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  mdplayer-render analyze track.ovi --fmp-com /path/FMP.COM");
        Console.WriteLine("  mdplayer-render analyze --timeline track.visualization/timeline.json");
        Console.WriteLine("  mdplayer-render render track.ovi");
    }
}
