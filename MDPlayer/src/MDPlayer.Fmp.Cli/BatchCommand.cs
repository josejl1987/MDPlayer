using System.Text.Json;
using Fmp.Core.IO;

namespace Fmp.Cli;

public static class BatchCommand
{
    /// <summary>One result record per input track, shared by console and JSON output.</summary>
    internal sealed record BatchResult(
        string Input,
        string Output,
        bool Success,
        string Error)
    {
        /// <summary>True when the track was skipped because the output already exists.</summary>
        public bool Skipped { get; init; }
    }

    public static int Handle(string[] args)
    {
        var opts = ParseArgs(args);
        if (opts == null) return 2;
        if (opts.InputDir == null) { Console.Error.WriteLine("error: no input directory specified"); return 2; }

        var inputDir = new DirectoryInfo(opts.InputDir);
        if (!inputDir.Exists) { Console.Error.WriteLine($"error: directory not found: {inputDir.FullName}"); return 3; }

        // Collect files
        var files = new List<FileInfo>();
        var searchOption = opts.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        foreach (var pattern in opts.Patterns)
        {
            files.AddRange(inputDir.GetFiles(pattern, searchOption));
        }

        // Source directory order, de-duplicated
        files = files
            .GroupBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        { Console.Error.WriteLine("error: no matching files found"); return 3; }

        // Prepare output directory
        string outputDir = opts.OutputDir ?? Path.Combine(inputDir.FullName, "output");
        Directory.CreateDirectory(outputDir);

        // Include the input directory in the track search paths (shared TrackPreparation
        // adds the per-file directory and the assets directory itself).
        opts.SearchPaths.Add(inputDir.FullName);

        // Resolve FMP.COM once to fail fast before processing any file.
        string fmpComPath = ToolResolver.ResolveFile(opts.FmpCom, opts.AssetsDir, "FMP.COM");
        if (fmpComPath == null || !File.Exists(fmpComPath))
        { Console.Error.WriteLine("error: FMP.COM not found"); return 4; }

        // Process files
        var results = new List<BatchResult>();
        int succeeded = 0, failed = 0, skipped = 0;

        foreach (var input in files)
        {
            // Output path uses the source basename — deterministic and stable.
            string wavFile = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(input.Name) + ".wav");

            // Check skip/overwrite
            if (File.Exists(wavFile))
            {
                if (opts.SkipExisting)
                {
                    results.Add(new BatchResult(input.Name, Path.GetFileName(wavFile), Success: true, Error: null)
                    {
                        Skipped = true,
                    });
                    skipped++;
                    if (!opts.Json && !opts.Quiet)
                        Console.Error.WriteLine($"skip: {input.Name}");
                    continue;
                }
                if (!opts.Overwrite)
                {
                    results.Add(new BatchResult(
                        input.Name,
                        Path.GetFileName(wavFile),
                        Success: false,
                        Error: "output exists (use --overwrite or --skip-existing)"));
                    failed++;
                    if (!opts.Json && !opts.Quiet)
                        Console.Error.WriteLine($"error: {input.Name} — output exists (use --overwrite or --skip-existing)");
                    continue;
                }
            }

            if (!opts.Quiet)
                Console.Error.WriteLine($"render: {input.Name}");

            PreparedTrack track;
            try { track = TrackPreparation.Prepare(input.FullName, opts); }
            catch (TrackPreparationException ex)
            {
                results.Add(new BatchResult(input.Name, null, Success: false, Error: ex.Message));
                failed++;
                if (!opts.Json && !opts.Quiet)
                    Console.Error.WriteLine($"  fail: {ex.Message}");
                continue;
            }

            var outcome = new TrackRenderer().Render(track, wavFile, opts);
            if (outcome.Success)
            {
                results.Add(new BatchResult(input.Name, Path.GetFileName(wavFile), Success: true, Error: null));
                succeeded++;
                if (!opts.Quiet)
                    Console.Error.WriteLine($"  ok: {wavFile}");
            }
            else
            {
                results.Add(new BatchResult(input.Name, null, Success: false, Error: outcome.LastError));
                failed++;
                if (!opts.Quiet)
                    Console.Error.WriteLine($"  fail: {outcome.LastError}");
            }
        }

        if (opts.Json)
        {
            var summary = new { total = files.Count, succeeded, failed, skipped, tracks = results };
            Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));
        }
        else if (!opts.Quiet)
        {
            Console.Error.WriteLine($"\nresults: {succeeded} ok, {failed} failed, {skipped} skipped / {files.Count} total");
        }

        return failed > 0 ? 7 : 0;
    }

    private static BatchOptions ParseArgs(string[] args)
    {
        var opts = new BatchOptions();
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
                        case "--output-dir": opts.OutputDir = reader.RequireValue(name); break;
                        case "--recursive" when value == null: opts.Recursive = true; break;
                        case "--glob":
                        case "--include": opts.Patterns.Clear(); opts.Patterns.Add(reader.RequireValue(name)); break;
                        case "--skip-existing" when value == null: opts.SkipExisting = true; break;
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
                    if (opts.InputDir != null)
                    {
                        Console.Error.WriteLine($"error: unexpected argument {positional}");
                        return null;
                    }
                    opts.InputDir = positional;
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

    private class BatchOptions : RenderSettings
    {
        public string InputDir { get; set; }
        public string OutputDir { get; set; }
        public bool Recursive { get; set; }
        public List<string> Patterns { get; set; } = new()
        {
            "*.ovi", "*.OVI",
            "*.opi", "*.OPI",
            "*.ozi", "*.OZI",
            "*.mpi", "*.MPI",
            "*.mvi", "*.MVI",
            "*.mzi", "*.MZI",
        };
        public bool SkipExisting { get; set; }
        public bool Overwrite { get; set; }
        public bool Quiet { get; set; }
        public bool Json { get; set; }
    }
}
