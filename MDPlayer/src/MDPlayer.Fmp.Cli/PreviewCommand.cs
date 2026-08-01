using System.Text.Json;
using System.Text.Json.Serialization;
using Fmp.Application.Contracts;
using Fmp.Application.Export;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
#nullable enable

namespace Fmp.Cli;

/// <summary>
/// `mdplayer-render preview` — renders a still PNG or a short motion sequence
/// from a request JSON. The musical overlay matches the published composition;
/// scope walls and analysis overlays are approximated (noted in the output).
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
        VisualizeOptions options = VisualizeOptionsParser.ParseForRequest(settings.RequestJsonPath, Array.Empty<string>());

        // Still/motion dimension overrides cap at the preview maximum.
        if (settings.Width.HasValue)
            options.Width = Math.Min(settings.Width.Value, MaxPreviewDimension);
        if (settings.Height.HasValue)
            options.Height = Math.Min(settings.Height.Value, MaxPreviewDimension);

        VisualizationPlanning.PlanOutput output = VisualizationPlanning.Prepare(
            options, request, settings.TimelinePath, settings.TimelineOutPath);

        var presentation = VisualizationSupport.ResolvePresentation(options, new FileInfo(options.Input));
        (bool hasApproximations, string[] approximationNotes) = ComputeApproximationNotes(
            output.Layout.Geometry);

        if (settings.Motion)
            return RunMotion(settings, options, output, presentation, hasApproximations, approximationNotes);
        return RunStill(settings, options, output, presentation, hasApproximations, approximationNotes);
    }

    private static int RunStill(
        PreviewSettings settings,
        VisualizeOptions options,
        VisualizationPlanning.PlanOutput output,
        Fmp.Core.Visualization.Rendering.VisualizationPresentation presentation,
        bool hasApproximations,
        string[] approximationNotes)
    {
        PanelOverlayRenderer renderer = VisualizationPlanning.BuildPanelRenderer(
            output.Timeline,
            output.Layout,
            VisualizationPlanning.CreatePreviewRendererOptions(options, presentation));

        if (renderer.TotalFrames <= 0)
            throw new InvalidOperationException("timeline contains no renderable frames");

        if (settings.Fidelity == "layout")
        {
            byte[] frame = new byte[renderer.FrameByteCount];
            using (var stream = new MemoryStream(frame, writable: true))
                renderer.WriteStaticFrame(stream);
            WritePng(renderer.Width, renderer.Height, frame, settings.Output);
        }
        else
        {
            long frameIndex = (long)Math.Round(
                settings.TimeSeconds * options.Fps / (double)options.FpsDenominator);
            frameIndex = Math.Clamp(frameIndex, 0, renderer.TotalFrames - 1);
            byte[] frame = renderer.RenderFrame(frameIndex);
            WritePng(renderer.Width, renderer.Height, frame, settings.Output);
        }

        ValidationIssue warning = output.Plan.ValidationIssues
            .FirstOrDefault(issue => issue.Severity == ValidationSeverity.Warning);
        if (settings.Json)
        {
            var json = new
            {
                schemaVersion = 1,
                timeSeconds = settings.TimeSeconds,
                width = renderer.Width,
                height = renderer.Height,
                fidelity = settings.Fidelity,
                hasApproximations,
                approximationNotes,
                warning = warning == null ? null : new
                {
                    code = warning.Code,
                    severity = warning.Severity.ToString(),
                    message = warning.Message,
                    settingPath = warning.SettingPath,
                    detail = warning.Detail,
                    suggestedAction = warning.SuggestedAction,
                },
            };
            Console.WriteLine(JsonSerializer.Serialize(json, JsonOptions));
        }
        return 0;
    }

    private static int RunMotion(
        PreviewSettings settings,
        VisualizeOptions options,
        VisualizationPlanning.PlanOutput output,
        Fmp.Core.Visualization.Rendering.VisualizationPresentation presentation,
        bool hasApproximations,
        string[] approximationNotes)
    {
        int frameCount = (int)Math.Ceiling(settings.DurationSeconds * settings.Fps);
        if (frameCount <= 0)
            throw new InvalidOperationException("motion preview produced no frames");

        // Scale the request dimensions to fit the preview max box, preserving
        // aspect ratio.
        double scale = Math.Min(
            settings.MaxWidth / (double)options.Width,
            settings.MaxHeight / (double)options.Height);
        int motionWidth = Math.Max(1, (int)Math.Round(options.Width * scale));
        int motionHeight = Math.Max(1, (int)Math.Round(options.Height * scale));

        PanelOverlayRenderer renderer = VisualizationPlanning.BuildPanelRenderer(
            output.Timeline,
            output.Layout,
            VisualizationPlanning.CreatePreviewRendererOptions(options, presentation));

        if (renderer.TotalFrames <= 0)
            throw new InvalidOperationException("timeline contains no renderable frames");

        Directory.CreateDirectory(settings.OutputDir);
        var frames = new List<string>(frameCount);
        for (int i = 0; i < frameCount; i++)
        {
            double timeSeconds = settings.StartSeconds + i / (double)settings.Fps;
            long frameIndex = (long)Math.Round(
                timeSeconds * options.Fps / (double)options.FpsDenominator);
            frameIndex = Math.Clamp(frameIndex, 0, renderer.TotalFrames - 1);
            byte[] frame = renderer.RenderFrame(frameIndex);
            string fileName = $"frame-{i:D4}.png";
            WriteScaledPng(
                renderer.Width,
                renderer.Height,
                motionWidth,
                motionHeight,
                frame,
                Path.Combine(settings.OutputDir, fileName));
            frames.Add(fileName);
        }

        var manifest = new
        {
            frameCount,
            fps = settings.Fps,
            width = motionWidth,
            height = motionHeight,
            frames,
            hasApproximations,
            approximationNotes,
        };
        File.WriteAllText(
            Path.Combine(settings.OutputDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions));

        if (settings.Json)
        {
            var json = new
            {
                schemaVersion = 1,
                frameCount,
                fps = settings.Fps,
                width = motionWidth,
                height = motionHeight,
                outputDir = settings.OutputDir,
                frames,
                hasApproximations,
                approximationNotes,
            };
            Console.WriteLine(JsonSerializer.Serialize(json, JsonOptions));
        }
        return 0;
    }

    private static (bool HasApproximations, string[] Notes) ComputeApproximationNotes(
        OverlayLayout layout)
    {
        var notes = new List<string>();
        bool hasScopes = layout.HasScopes;
        if (hasScopes)
            notes.Add("Scope wall omitted in preview");
        return (notes.Count > 0, notes.ToArray());
    }

    private static void WritePng(int width, int height, byte[] rgba, string path)
    {
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        using Image<Rgba32> image = CreateImage(width, height, rgba);
        image.SaveAsPng(path);
    }

    private static void WriteScaledPng(
        int sourceWidth,
        int sourceHeight,
        int width,
        int height,
        byte[] rgba,
        string path)
    {
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        using Image<Rgba32> image = CreateImage(sourceWidth, sourceHeight, rgba);
        if (sourceWidth != width || sourceHeight != height)
            image.Mutate(context => context.Resize(width, height));
        image.SaveAsPng(path);
    }

    private static Image<Rgba32> CreateImage(int width, int height, byte[] rgba)
    {
        var image = new Image<Rgba32>(width, height);
        // Load the RGBA frame buffer into the image. (CopyPixelDataTo would
        // copy the empty image into the buffer; the row accessor is the
        // supported ImageSharp 3.x write path.)
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                int rowOffset = y * width * 4;
                for (int x = 0; x < row.Length; x++)
                {
                    int offset = rowOffset + x * 4;
                    row[x] = new Rgba32(
                        rgba[offset],
                        rgba[offset + 1],
                        rgba[offset + 2],
                        rgba[offset + 3]);
                }
            }
        });
        return image;
    }

    private static string ParseFidelity(string raw)
    {
        return raw?.Trim().ToLowerInvariant() switch
        {
            "layout" => "layout",
            "accurate" => "accurate",
            _ => throw new ArgumentException($"unknown fidelity '{raw}' (expected layout or accurate)"),
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
