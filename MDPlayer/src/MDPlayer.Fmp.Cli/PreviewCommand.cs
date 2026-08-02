using System.Text.Json;
using System.Text.Json.Serialization;
using Fmp.Application.Contracts;
using Fmp.Application.Export;
using Fmp.Application.Preview;
#nullable enable

namespace Fmp.Cli;

/// <summary>
/// `mdplayer-render preview` — renders a still PNG or a short motion sequence
/// from a request JSON through the shared in-process preview session. The
/// accurate fidelity uses exactly the same frame renderer as final video
/// composition and visual review, so preview, review and final all agree.
/// </summary>
public static class PreviewCommand
{
    private const int MaxPreviewDimension = 1920;

    public static int Handle(string[] args)
    {
        string requestJsonPath = null;
        string timelinePath = null;
        string timelineOutPath = null;
        string output = null;
        string outputDir = null;
        double time = 0;
        int? width = null;
        int? height = null;
        string fidelity = "accurate";
        bool json = false;
        bool motion = false;
        double start = 0;
        double duration = 3;
        int fps = 15;
        int maxWidth = 960;
        int maxHeight = 540;

        var reader = new ArgumentReader(args ?? Array.Empty<string>());
        try
        {
            while (reader.HasMore)
            {
                if (reader.TryReadOption(out string name, out string value))
                {
                    switch (name)
                    {
                        case "--request-json": requestJsonPath = reader.RequireValue(name); break;
                        case "--timeline": timelinePath = reader.RequireValue(name); break;
                        case "--timeline-out": timelineOutPath = reader.RequireValue(name); break;
                        case "--time": time = reader.ReadDouble(name); break;
                        case "--output": output = reader.RequireValue(name); break;
                        case "--width": width = reader.ReadInt(name); break;
                        case "--height": height = reader.ReadInt(name); break;
                        case "--fidelity": fidelity = ParseFidelity(reader.RequireValue(name)); break;
                        case "--motion" when value == null: motion = true; break;
                        case "--start": start = reader.ReadDouble(name); break;
                        case "--duration": duration = reader.ReadDouble(name); break;
                        case "--fps": fps = reader.ReadInt(name); break;
                        case "--max-width": maxWidth = reader.ReadInt(name); break;
                        case "--max-height": maxHeight = reader.ReadInt(name); break;
                        case "--output-dir": outputDir = reader.RequireValue(name); break;
                        case "--json" when value == null: json = true; break;
                        default: throw new ArgumentException($"unknown option '{name}'");
                    }
                }
                else
                {
                    string positional = reader.Next();
                    if (positional == "--") continue;
                    throw new ArgumentException($"unexpected argument '{positional}'");
                }
            }

            if (string.IsNullOrWhiteSpace(requestJsonPath))
                throw new ArgumentException("--request-json PATH is required");

            if (motion)
            {
                if (string.IsNullOrWhiteSpace(outputDir))
                    throw new ArgumentException("--output-dir DIR is required for --motion previews");
                if (duration <= 0 || fps <= 0 || maxWidth <= 0 || maxHeight <= 0)
                    throw new ArgumentException("--duration, --fps, --max-width and --max-height must be positive");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(output))
                    throw new ArgumentException("--output PNG is required for still previews");
                if (width is <= 0 || height is <= 0)
                    throw new ArgumentException("--width and --height must be positive");
            }

            return Run(new PreviewSettings
            {
                RequestJsonPath = requestJsonPath,
                TimelinePath = timelinePath,
                TimelineOutPath = timelineOutPath,
                Output = output,
                OutputDir = outputDir,
                TimeSeconds = time,
                Width = width,
                Height = height,
                Fidelity = fidelity,
                Json = json,
                Motion = motion,
                StartSeconds = start,
                DurationSeconds = duration,
                Fps = fps,
                MaxWidth = maxWidth,
                MaxHeight = maxHeight,
            });
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (VisualizationRequestException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (TrackPreparationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ex.ExitCode;
        }
        catch (VisualizationBackendResolutionException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ex.ExitCode;
        }
        catch (VisualizationExecutionException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: preview failed — {ex.Message}");
            return 7;
        }
    }

    private sealed record PreviewSettings
    {
        public required string RequestJsonPath { get; init; }
        public string? TimelinePath { get; init; }
        public string? TimelineOutPath { get; init; }
        public string? Output { get; init; }
        public string? OutputDir { get; init; }
        public double TimeSeconds { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }
        public string Fidelity { get; init; } = "accurate";
        public bool Json { get; init; }
        public bool Motion { get; init; }
        public double StartSeconds { get; init; }
        public double DurationSeconds { get; init; } = 3.0;
        public int Fps { get; init; } = 15;
        public int MaxWidth { get; init; } = 960;
        public int MaxHeight { get; init; } = 540;
    }

    private static int Run(PreviewSettings settings)
    {
        VisualizationRequest request = VisualizationRequestSerializer.ReadFromFile(settings.RequestJsonPath);
        var runtime = new RenderRuntimeOptions();
        return RunAsync(settings, request, runtime).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(
        PreviewSettings settings,
        VisualizationRequest request,
        RenderRuntimeOptions runtime)
    {
        await using IVisualizationPreviewSession session =
            await new InProcessVisualizationPreviewSessionFactory(runtime).OpenWithTimelineAsync(
                request.InputPath, settings.TimelinePath, CancellationToken.None,
                timelineOutPath: settings.TimelineOutPath);

        if (settings.Motion)
        {
            MotionPreviewResult result = await session.RenderMotionAsync(
                request,
                BuildMotionRequest(settings),
                progress: null,
                CancellationToken.None);

            WriteMotionResult(settings, result);
        }
        else
        {
            PreviewFrameResult result = await session.RenderFrameAsync(
                request,
                BuildFrameRequest(settings),
                CancellationToken.None);

            File.WriteAllBytes(settings.Output!, result.PngBytes);
            WriteStillResult(settings, result);
        }

        return 0;
    }

    private static PreviewFrameRequest BuildFrameRequest(PreviewSettings settings)
    {
        PreviewFidelity fidelity = settings.Fidelity switch
        {
            "layout" => PreviewFidelity.Layout,
            "interactive" => PreviewFidelity.InteractiveStill,
            _ => PreviewFidelity.AccurateStill,
        };
        return new PreviewFrameRequest
        {
            TimeSeconds = settings.TimeSeconds,
            Fidelity = fidelity,
            Width = settings.Width.HasValue
                ? Math.Min(settings.Width.Value, MaxPreviewDimension)
                : null,
            Height = settings.Height.HasValue
                ? Math.Min(settings.Height.Value, MaxPreviewDimension)
                : null,
        };
    }

    private static MotionPreviewRequest BuildMotionRequest(PreviewSettings settings)
        => new()
        {
            StartSeconds = settings.StartSeconds,
            DurationSeconds = settings.DurationSeconds,
            Fps = settings.Fps,
            MaxWidth = settings.MaxWidth,
            MaxHeight = settings.MaxHeight,
        };

    private static void WriteStillResult(PreviewSettings settings, PreviewFrameResult result)
    {
        if (!settings.Json)
            return;
        var json = new
        {
            schemaVersion = 1,
            timeSeconds = result.TimeSeconds,
            width = result.Width,
            height = result.Height,
            fidelity = result.Fidelity.ToString(),
            hasApproximations = result.HasApproximations,
            approximationNotes = result.ApproximationNotes,
            warning = result.Warning == null ? null : new
            {
                code = result.Warning.Code,
                severity = result.Warning.Severity.ToString(),
                message = result.Warning.Message,
                settingPath = result.Warning.SettingPath,
                detail = result.Warning.Detail,
                suggestedAction = result.Warning.SuggestedAction,
            },
        };
        Console.WriteLine(JsonSerializer.Serialize(json, JsonOptions));
    }

    private static void WriteMotionResult(PreviewSettings settings, MotionPreviewResult result)
    {
        Directory.CreateDirectory(settings.OutputDir!);
        var frames = new List<string>(result.FrameCount);

        // The session returns frame paths in its temp workspace; copy them to
        // the requested output directory.
        for (int i = 0; i < result.FramePaths.Count; i++)
        {
            string fileName = $"frame-{i:D4}.png";
            string source = result.FramePaths[i];
            string dest = Path.Combine(settings.OutputDir!, fileName);
            if (File.Exists(source))
                File.Copy(source, dest, overwrite: true);
            frames.Add(fileName);
        }

        var manifest = new
        {
            frameCount = result.FrameCount,
            fps = result.Fps,
            width = result.Width,
            height = result.Height,
            frames,
            hasApproximations = result.HasApproximations,
            approximationNotes = result.ApproximationNotes,
        };
        File.WriteAllText(
            Path.Combine(settings.OutputDir!, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions));

        if (settings.Json)
        {
            var json = new
            {
                schemaVersion = 1,
                frameCount = result.FrameCount,
                fps = result.Fps,
                width = result.Width,
                height = result.Height,
                outputDir = settings.OutputDir,
                frames,
                hasApproximations = result.HasApproximations,
                approximationNotes = result.ApproximationNotes,
            };
            Console.WriteLine(JsonSerializer.Serialize(json, JsonOptions));
        }
    }

    private static string ParseFidelity(string raw)
    {
        return raw?.Trim().ToLowerInvariant() switch
        {
            "layout" => "layout",
            "interactive" => "interactive",
            "accurate" => "accurate",
            _ => throw new ArgumentException(
                $"unknown fidelity '{raw}' (expected layout, interactive, or accurate)"),
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
