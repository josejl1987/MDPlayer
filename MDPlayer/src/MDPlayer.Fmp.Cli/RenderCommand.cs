using System.Text.Json;

namespace Fmp.Cli;

public static class RenderCommand
{
    public static int Handle(string[] args)
    {
        var opts = ParseArgs(args);
        if (opts == null) return 2;
        if (opts.Input == null) { Console.Error.WriteLine("error: no input file specified"); return 2; }

        PreparedTrack track;
        try { track = TrackPreparation.Prepare(opts.Input, opts); }
        catch (TrackPreparationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ex.ExitCode;
        }

        // Output path defaults to the source basename.
        string outputPath = opts.Output ?? Path.Combine(
            track.Input.DirectoryName ?? ".", Path.GetFileNameWithoutExtension(track.Input.Name) + ".wav");

        if (File.Exists(outputPath) && !opts.Overwrite)
        { Console.Error.WriteLine($"error: output exists: {outputPath} (use --overwrite)"); return 9; }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

        // Resolve trace path if requested
        if (opts.TracePath == null && opts.AutoTrace)
        {
            string baseDir = Path.GetDirectoryName(outputPath) ?? ".";
            string baseName = Path.GetFileNameWithoutExtension(outputPath);
            opts.TracePath = Path.Combine(baseDir, baseName + ".register-trace.jsonl");
        }

        if (!opts.Quiet) Console.Error.WriteLine($"rendering: {track.Input.Name}");
        var outcome = new TrackRenderer().Render(track, outputPath, opts);

        // Write metadata.json
        if (outcome.Success)
        {
            string metaPath = opts.MetadataPath ?? Path.ChangeExtension(outputPath, ".metadata.json");
            File.WriteAllText(metaPath, outcome.Metadata.ToJson());
        }

        int exitCode = outcome.Success ? 0 : 7;

        if (opts.Json)
        {
            PrintJson(new { success = outcome.Success, exitCode, output = outputPath, samples = outcome.RenderedSamples, stopReason = outcome.StopReason });
        }
        else if (outcome.Success)
        {
            if (!opts.Quiet)
                Console.Error.WriteLine($"done: {outputPath} ({TimeSpan.FromSeconds((double)outcome.RenderedSamples / opts.SampleRate):g}) reason={outcome.StopReason}");
        }
        else
        {
            Console.Error.WriteLine($"error: {outcome.LastError}");
        }

        return exitCode;
    }

    private static RenderOptions ParseArgs(string[] args)
    {
        var opts = new RenderOptions();
        var reader = new ArgumentReader(args);
        try
        {
            while (reader.HasMore)
            {
                if (reader.TryReadOption(out string name, out string value))
                {
                    if (RenderOptionsParser.TryParse(ref reader, name, opts))
                        continue;

                    switch (name)
                    {
                        case "-o":
                        case "--output": opts.Output = reader.RequireValue(name); break;
                        case "--duration": opts.Duration = reader.ReadDouble(name); break;
                        case "--timeout": opts.Timeout = reader.ReadDouble(name); break;
                        case "--trace": opts.TracePath = reader.RequireValue(name); break;
                        case "--auto-trace" when value == null: opts.AutoTrace = true; break;
                        case "--metadata": opts.MetadataPath = reader.RequireValue(name); break;
                        case "--overwrite" when value == null: opts.Overwrite = true; break;
                        case "--quiet" when value == null: opts.Quiet = true; break;
                        case "--json" when value == null: opts.Json = true; break;
                        default:
                            Console.Error.WriteLine($"error: unknown option {name}");
                            return null;
                    }
                }
                else
                {
                    string positional = reader.Next();
                    if (positional == "--") continue;
                    if (opts.Input != null)
                    {
                        Console.Error.WriteLine($"error: unexpected argument '{positional}'");
                        return null;
                    }
                    opts.Input = positional;
                }
            }
            return opts;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return null;
        }
    }

    private class RenderOptions : RenderSettings
    {
        public string Input { get; set; }
        public string Output { get; set; }
        public bool AutoTrace { get; set; }
        public string MetadataPath { get; set; }
        public bool Overwrite { get; set; }
        public bool Quiet { get; set; }
        public bool Json { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static void PrintJson(object obj) =>
        Console.WriteLine(JsonSerializer.Serialize(obj, JsonOptions));
}
