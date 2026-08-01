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
                    return RenderCommand.Handle(args.Skip(1).ToArray());
                case "batch":
                    return BatchCommand.Handle(args.Skip(1).ToArray());
                case "inspect":
                    return InspectCommand.Handle(args.Skip(1).ToArray());
                case "visualize":
                    return VisualizeCommand.Handle(args.Skip(1).ToArray());
                case "analyze":
                    return AnalyzeCommand.Handle(args.Skip(1).ToArray());
                case "plan":
                    return PlanCommand.Handle(args.Skip(1).ToArray());
                case "preview":
                    return PreviewCommand.Handle(args.Skip(1).ToArray());
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
        Console.WriteLine("  mdplayer-render render <input.ovi|opi|ozi|mpi|mvi|mzi|spc> [options]");
        Console.WriteLine("  mdplayer-render visualize <input.ovi|input.vgm|input.xgm|input.s98|input.mdx|input.mid|input.spc> [options]");
        Console.WriteLine("  mdplayer-render inspect <input.ovi|input.vgm|input.xgm|input.s98|input.mdx|input.mid|input.spc> [--visualization]");
        Console.WriteLine("  mdplayer-render analyze <input.ovi> [options]");
        Console.WriteLine("  mdplayer-render plan --request-json PATH [options]");
        Console.WriteLine("  mdplayer-render preview --request-json PATH [--time T] [--motion] [options]");
        Console.WriteLine();
        Console.WriteLine("Render options:");
        Console.WriteLine("  -o, --output PATH          Output WAV path");
        Console.WriteLine("  --fmp-com PATH             Path to FMP.COM");
        Console.WriteLine("  --assets-dir DIR           Assets directory");
        Console.WriteLine("  -I, --search-path PATH     Supplemental file search paths");
        Console.WriteLine("  --sample-rate HZ           Sample rate (default: 44100)");
        Console.WriteLine("  --loops COUNT              Loop count (default: 2)");
        Console.WriteLine("  --fade SECONDS             Fade duration (default: 5)");
        Console.WriteLine("  --tail SECONDS             Tail duration (default: 0.5)");
        Console.WriteLine("  --duration SECONDS         Explicit render duration");
        Console.WriteLine("  --max-duration SECONDS     Safety max duration (default: 300)");
        Console.WriteLine("  --metadata PATH            Path for metadata JSON");
        Console.WriteLine("  --ssg-gain-db DB           Adjust YM2608 SSG/PSG audio level (default: 0)");
        Console.WriteLine("  --overwrite                Overwrite existing output");
        Console.WriteLine("  --quiet                    Suppress progress output");
        Console.WriteLine("  --json                     Machine-readable JSON output");
        Console.WriteLine();
        Console.WriteLine("Visualize options:");
        Console.WriteLine("  -o, --output DIR           Output directory (default: <input>.visualization/)");
        Console.WriteLine("  --fmp-com PATH             Path to FMP.COM");
        Console.WriteLine("  --assets-dir DIR           Assets directory");
        Console.WriteLine("  -I, --search-path PATH     Supplemental file search paths");
        Console.WriteLine("  --sample-rate HZ           Sample rate (default: 44100)");
        Console.WriteLine("  --loops COUNT              Loop count (default: 2)");
        Console.WriteLine("  --fade SECONDS             Post-loop fade duration (default: 5)");
        Console.WriteLine("  --tail SECONDS             Post-fade tail duration (default: 0.5)");
        Console.WriteLine("  --duration SECONDS         Capture exactly this maximum duration");
        Console.WriteLine("  --max-duration SECONDS     Song-end safety cap (default: 300)");
        Console.WriteLine("  --ssg-gain-db DB           Adjust YM2608 SSG/PSG audio level (default: 0)");
        Console.WriteLine("  --stems-only               Render stems + corrscope YAML, skip video composition");
        Console.WriteLine("  --spc-stems                Also export SPC per-voice/echo stems next to master.wav");
        Console.WriteLine("  --video PATH               Final MP4 path (default: <output>/visualization.mp4)");
        Console.WriteLine("  --width PIXELS             Video width (default: 1280 balanced)");
        Console.WriteLine("  --height PIXELS            Video height (default: 720 balanced)");
        Console.WriteLine("  --fps RATE                 Video frame rate (default: 60 balanced)");
        Console.WriteLine("  --final-quality            Use 1080p60, veryfast/crf18, Corrscope AA on");
        Console.WriteLine("  --preset preview|balanced|final|diagnostic  Publishing preset (default: balanced)");
        Console.WriteLine("  --encoder auto|libx264|nvenc Encoder (default: auto: NVENC when available, otherwise libx264)");
        Console.WriteLine("  --backend auto|fmp|mdplayer  Playback backend preference (default: auto)");
        Console.WriteLine("  --effects none|minimal|diagnostic|cinematic  Dynamic effect preset (default: minimal)");
        Console.WriteLine("  --layout auto|unified|split|scope|hybrid|diagnostic|diagnostic-v2");
        Console.WriteLine("  --channels active|audible|semantic|all|custom  Track filtering (default: active; custom keeps --include-track/--exclude-track)");
        Console.WriteLine("  --include-track ID         Keep only the listed tracks (repeatable; master panels always kept)");
        Console.WriteLine("  --exclude-track ID         Drop the listed tracks (repeatable)");
        Console.WriteLine("  --past-seconds N --future-seconds N --time-window P:F --time-scale dense|balanced|wide");
        Console.WriteLine("  --scope-ratio N --scope-position bottom|top|left|right --group-by none|device|family");
        Console.WriteLine("  --time-grid none|automatic|authoritative|analytical");
        Console.WriteLine("  --note-color instrument|channel|pitch");
        Console.WriteLine("  --print-layout            Print the prepared layout plan");
        Console.WriteLine("  --layout-json PATH        Export the prepared layout plan as JSON");
        Console.WriteLine("  --layout-template PATH    Apply a reusable JSON composition template");
        Console.WriteLine("  --palette PATH            Apply a reusable JSON palette");
        Console.WriteLine("  --preview-html PATH       Write an interactive HTML canvas preview");
        Console.WriteLine("  --diagnostic-pages DIR    Write paginated SVG diagnostic sheets");
        Console.WriteLine("  --renderer auto|cpu|gpu   Select software or OpenCL semantic rendering");
        Console.WriteLine("  --motion-blur-samples N   Deterministic temporal samples (1-8)");
        Console.WriteLine("  --spc-pitch estimate|relative  SPC diagnostic: estimate BRR root vs relative pitch only (default: estimate)");
        Console.WriteLine("  --font PATH                TrueType/OpenType font with CJK coverage");
        Console.WriteLine("  --corrscope PATH           Corrscope executable path");
        Console.WriteLine("  --ffmpeg PATH              FFmpeg executable path");
        Console.WriteLine("  --scopes auto|channel|device|master|off   Generic backend scope mode");
        Console.WriteLine("  --tool-timeout-minutes N   Corrscope/FFmpeg timeout (default: 60)");
        Console.WriteLine("  --overwrite                Overwrite timeline/video outputs");
        Console.WriteLine("  --quiet                    Suppress progress output");
        Console.WriteLine("  --json                     Machine-readable command summary");
        Console.WriteLine("  --analysis                 Run optional symbolic analysis once per job");
        Console.WriteLine("  --analysis-python PATH     Python interpreter for symbolic analysis");
        Console.WriteLine("  --analysis-detail minimal|standard|full");
        Console.WriteLine("  --analysis-force          Ignore an existing analysis cache");
        Console.WriteLine("  --analysis-timeout-minutes N  Analysis worker timeout (default: 10)");
        Console.WriteLine("  --analysis-overlay none|minimal|standard|full");
        Console.WriteLine("  --request-json PATH        Seed every option from a request JSON file (must be the first option)");
        Console.WriteLine("  --progress jsonl           Emit structured JSON-lines progress on stdout");
        Console.WriteLine();
        Console.WriteLine("Plan options:");
        Console.WriteLine("  --request-json PATH        Request JSON file (required)");
        Console.WriteLine("  --timeline PATH            Reuse an existing visualization timeline");
        Console.WriteLine("  --timeline-out PATH        Write the captured/reused timeline to PATH");
        Console.WriteLine("  --json                     Machine-readable plan JSON on stdout");
        Console.WriteLine();
        Console.WriteLine("Preview options:");
        Console.WriteLine("  --request-json PATH        Request JSON file (required)");
        Console.WriteLine("  --time SECONDS             Still frame time (default: 0)");
        Console.WriteLine("  --output PATH              Still output PNG path");
        Console.WriteLine("  --width N --height N       Preview size overrides (cap 1920)");
        Console.WriteLine("  --fidelity layout|accurate Still fidelity (default: accurate)");
        Console.WriteLine("  --timeline PATH            Reuse an existing visualization timeline");
        Console.WriteLine("  --timeline-out PATH        Write the captured/reused timeline to PATH");
        Console.WriteLine("  --json                     Machine-readable preview metadata on stdout");
        Console.WriteLine("  --motion                   Render a motion sequence instead of a still");
        Console.WriteLine("  --start SECONDS --duration SECONDS --fps N  Motion range (defaults 0 / 3 / 15)");
        Console.WriteLine("  --max-width N --max-height N               Motion size cap (defaults 960 / 540)");
        Console.WriteLine("  --output-dir DIR           Motion frame output directory");
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
        Console.WriteLine("Examples:");
        Console.WriteLine("  mdplayer-render analyze track.ovi --fmp-com /path/FMP.COM");
        Console.WriteLine("  mdplayer-render analyze --timeline track.visualization/timeline.json");
        Console.WriteLine("  mdplayer-render visualize track.ovi");
        Console.WriteLine("  mdplayer-render visualize track.vgm --stems-only");
    }
}
