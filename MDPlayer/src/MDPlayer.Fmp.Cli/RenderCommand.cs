using System.Text.Json;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;

namespace Fmp.Cli;

public static class RenderCommand
{
    public static int Handle(string[] args)
    {
        var opts = ParseArgs(args);
        if (opts == null) return 2;
        if (opts.Input == null) { Console.Error.WriteLine("error: no input file specified"); return 2; }

        // The FMP renderer has a separate preparation path.  Let the generic
        // backend registry handle formats such as SPC before falling back to
        // that FMP-only path.
        if (TryRenderGeneric(opts, out int genericExitCode))
            return genericExitCode;

        PreparedTrack track;
        try { track = TrackPreparation.Prepare(opts.Input, opts.FmpCom, opts.AssetsDir, opts.SearchPaths); }
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
            PrintJson(new { success = outcome.Success, exitCode, output = outputPath, samples = outcome.RenderedSamples, stopReason = outcome.StopReason, assetDumpError = outcome.AssetDumpError });
        }
        else if (outcome.Success)
        {
            if (!string.IsNullOrEmpty(outcome.AssetDumpError))
                Console.Error.WriteLine($"warning: {outcome.AssetDumpError}");
            if (!opts.Quiet)
                Console.Error.WriteLine($"done: {outputPath} ({TimeSpan.FromSeconds((double)outcome.RenderedSamples / opts.SampleRate):g}) reason={outcome.StopReason}");
        }
        else
        {
            Console.Error.WriteLine($"error: {outcome.LastError}");
        }

        return exitCode;
    }

    private static bool TryRenderGeneric(RenderOptions opts, out int exitCode)
    {
        exitCode = 0;
        var input = new FileInfo(opts.Input);
        if (!input.Exists)
            return false;
        if (!string.Equals(input.Extension, ".spc", StringComparison.OrdinalIgnoreCase))
            return false;

        var searchPaths = new List<string>(opts.SearchPaths);
        if (!string.IsNullOrWhiteSpace(opts.AssetsDir))
            searchPaths.Add(opts.AssetsDir);
        searchPaths.Add(input.DirectoryName ?? ".");

        var environment = new PlaybackEnvironment(searchPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            OfflineOnly: true,
            SampleRate: opts.SampleRate);
        var registry = PlaybackBackendRegistry.CreateDefault(environment);
        if (!registry.TrySelect(input, environment, "mdplayer",
                out IPlaybackBackend backend, out PlaybackProbeResult probe)
            || backend.Id != "spc")
        {
            return false;
        }

        string outputPath = opts.Output ?? Path.Combine(
            input.DirectoryName ?? ".", Path.GetFileNameWithoutExtension(input.Name) + ".wav");
        if (File.Exists(outputPath) && !opts.Overwrite)
        {
            Console.Error.WriteLine($"error: output exists: {outputPath} (use --overwrite)");
            exitCode = 9;
            return true;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
            if (!opts.Quiet)
                Console.Error.WriteLine($"rendering: {input.Name} ({backend.Id})");

            using IPlaybackCaptureSession session = backend.Open(
                input,
                new PlaybackOptions(
                    opts.Loops,
                    opts.Fade,
                    opts.Tail,
                    // SPC resolves its own metadata/default duration when no
                    // explicit --duration was supplied.
                    opts.Duration,
                    outputPath,
                    opts.SampleRate),
                NullPlaybackEventSink.Instance);
            session.Run();

            string metadataPath = opts.MetadataPath ?? Path.ChangeExtension(outputPath, ".metadata.json");
            var metadata = new
            {
                sourceFile = input.Name,
                sourceFormat = probe.Format.ToLowerInvariant(),
                backend = backend.Id,
                sampleRate = session.Timing.SampleRate,
                renderedSamples = session.SamplePosition,
                renderedDuration = TimeSpan.FromSeconds(
                    (double)session.SamplePosition / session.Timing.SampleRate).ToString(@"hh\:mm\:ss\.fff"),
                warnings = probe.Warnings,
            };
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, JsonOptions));

            if (opts.Json)
            {
                PrintJson(new
                {
                    success = true,
                    exitCode = 0,
                    output = outputPath,
                    samples = session.SamplePosition,
                    sampleRate = session.Timing.SampleRate,
                    stopReason = "completed",
                });
            }
            else if (!opts.Quiet)
            {
                Console.Error.WriteLine($"done: {outputPath} ({TimeSpan.FromSeconds(
                    (double)session.SamplePosition / session.Timing.SampleRate):g})");
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: generic render failed: {ex.Message}");
            exitCode = 7;
            return true;
        }
    }

    private sealed class NullPlaybackEventSink : IPlaybackEventSink
    {
        public static readonly NullPlaybackEventSink Instance = new();

        public void OnDevice(in DeviceDescriptor device) { }
        public void OnChipWrite(in TimedChipWrite write) { }
        public void OnMidi(in TimedMidiMessage message) { }
        public void OnSampleAsset(in TimedSampleAssetEvent asset) { }
        public void OnLoopBoundary(in TimedLoopBoundary loop) { }
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
            opts.ValidateCommon();
            return opts;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return null;
        }
    }

    private class RenderOptions : BatchRenderSettings
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
