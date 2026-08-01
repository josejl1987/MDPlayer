using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Fmp.Application.Contracts;
using Fmp.Application.Inspection;
using Fmp.Application.Preview;
using Fmp.Application.Review;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Fmp.Core.Rendering;
using Fmp.Core.Rendering.Corrscope;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Fmp.Cli;

/// <summary>Generates the real-file visual review gallery.</summary>
public static class ReviewCommand
{
    public static int Handle(string[] args)
    {
        try
        {
            ReviewCliOptions options = Parse(args ?? Array.Empty<string>());
            string corpus = ResolveCorpus(options.Corpus);
            var generator = new ReviewGeneratorOptions
            {
                ManifestPath = Path.GetFullPath(options.Manifest),
                CorpusPath = corpus,
                OutputPath = Path.GetFullPath(options.Output),
                KeepExisting = options.KeepExisting,
                AllowMissingChips = options.AllowMissingChips,
                Filter = new ReviewFilter
                {
                    FileSubstring = options.File,
                    Chip = options.Chip,
                    Composition = options.Composition,
                    MomentName = options.Moment,
                    ResolutionName = options.Resolution,
                },
                PreviewSessions = new InProcessReviewPreviewSessionFactory(),
            };

            ReviewResult result = new ReviewGenerator().GenerateAsync(generator).GetAwaiter().GetResult();
            Console.WriteLine($"Review output: {result.OutputPath}");
            Console.WriteLine($"Files: {result.Files}; moments: {result.Moments}; cases: {result.Cases}");
            Console.WriteLine($"Rendered: {result.Rendered}; unavailable: {result.Unavailable}; failed: {result.Failed}");
            if (result.MissingChips.Count > 0)
                Console.WriteLine($"Missing chips: {string.Join(", ", result.MissingChips)}");
            return result.ExitCode;
        }
        catch (ReviewManifestException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: review failed — {ex.Message}");
            return 1;
        }
    }

    private static ReviewCliOptions Parse(string[] args)
    {
        var options = new ReviewCliOptions();
        var reader = new ArgumentReader(args);
        while (reader.HasMore)
        {
            if (!reader.TryReadOption(out string name, out string value))
                throw new ArgumentException($"unexpected argument '{reader.Next()}'");
            switch (name)
            {
                case "--manifest": options.Manifest = reader.RequireValue(name); break;
                case "--corpus": options.Corpus = reader.RequireValue(name); break;
                case "--output": options.Output = reader.RequireValue(name); break;
                case "--file": options.File = reader.RequireValue(name); break;
                case "--chip": options.Chip = reader.RequireValue(name); break;
                case "--moment": options.Moment = reader.RequireValue(name); break;
                case "--composition": options.Composition = ParseComposition(reader.RequireValue(name)); break;
                case "--resolution": options.Resolution = ParseResolution(reader.RequireValue(name)); break;
                case "--keep-existing" when value == null: options.KeepExisting = true; break;
                case "--allow-missing-chips" when value == null: options.AllowMissingChips = true; break;
                default: throw new ArgumentException($"unknown option '{name}'");
            }
        }
        if (string.IsNullOrWhiteSpace(options.Manifest))
            throw new ArgumentException("--manifest PATH is required");
        if (string.IsNullOrWhiteSpace(options.Output))
            throw new ArgumentException("--output PATH is required");
        return options;
    }

    private static string ResolveCorpus(string explicitCorpus)
    {
        if (!string.IsNullOrWhiteSpace(explicitCorpus))
            return explicitCorpus;
        string environment = Environment.GetEnvironmentVariable("MDPLAYER_REVIEW_CORPUS");
        return !string.IsNullOrWhiteSpace(environment)
            ? environment
            : Path.Combine(Environment.CurrentDirectory, "visual-review", "corpus");
    }

    private static CompositionKind ParseComposition(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "diagnostic" => CompositionKind.Diagnostic,
        _ => throw new ArgumentException("unknown composition (expected diagnostic)"),
    };

    private static string ParseResolution(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "720p" => "720p",
        "1080p" => "1080p",
        _ => throw new ArgumentException("unknown resolution (expected 720p or 1080p)"),
    };

    private sealed class ReviewCliOptions
    {
        public string Manifest;
        public string Corpus;
        public string Output;
        public string File;
        public string Chip;
        public string Moment;
        public CompositionKind? Composition;
        public string Resolution;
        public bool KeepExisting;
        public bool AllowMissingChips;
    }

    private sealed class InProcessReviewPreviewSessionFactory : IVisualizationPreviewSessionFactory
    {
        public async Task<IVisualizationPreviewSession> OpenAsync(string inputPath, CancellationToken cancellationToken)
        {
            VisualizationInputInfo input = await VisualizationInputInspector.InspectAsync(inputPath, cancellationToken);
            return new InProcessReviewPreviewSession(input);
        }
    }

    private sealed class InProcessReviewPreviewSession : IVisualizationPreviewSession
    {
        private readonly string _sessionRoot = Path.Combine(
            Path.GetTempPath(), "MDPlayer", "VisualReview", Guid.NewGuid().ToString("N"));
        private readonly string _timelinePath;
        private readonly string _masterAudioPath;
        private readonly string _scopeRoot;
        private readonly Dictionary<string, ScopeStream> _scopeStreams = new(StringComparer.Ordinal);
        private bool _hasTimeline;

        public InProcessReviewPreviewSession(VisualizationInputInfo input)
        {
            _timelinePath = Path.Combine(_sessionRoot, "timeline.json");
            _masterAudioPath = Path.Combine(_sessionRoot, "master.wav");
            _scopeRoot = Path.Combine(_sessionRoot, "scope");
            Input = input ?? throw new ArgumentNullException(nameof(input));
            Capabilities = new VisualizationSessionCapabilities
            {
                SemanticCapture = input.SupportsSemanticCapture,
                ScopeCapture = input.SupportsScopeCapture,
                AnalysisAvailable = input.SupportsAnalysis,
                Issues = input.Issues,
            };
        }

        public VisualizationInputInfo Input { get; }
        public VisualizationSessionCapabilities Capabilities { get; private set; }

        public Task<VisualizationPlanResult> PlanAsync(
            VisualizationRequest request,
            CancellationToken cancellationToken)
        {
            VisualizationPlanning.PlanOutput output = Prepare(request, cancellationToken);
            return Task.FromResult(output.Plan);
        }

        public Task<PreviewFrameResult> RenderFrameAsync(
            VisualizationRequest request,
            PreviewFrameRequest preview,
            CancellationToken cancellationToken)
        {
            VisualizationPlanning.PlanOutput output = Prepare(request, cancellationToken);
            VisualizationRequest previewRequest = request with
            {
                Output = request.Output with
                {
                    Width = preview.Width ?? request.Output.Width,
                    Height = preview.Height ?? request.Output.Height,
                },
            };
            VisualizationPresentation presentation = VisualizationSupport.ResolvePresentation(
                request, new FileInfo(request.InputPath));
            using PanelOverlayRenderer renderer = VisualizationPlanning.BuildPanelRenderer(
                output.Timeline,
                output.Layout,
                VisualizationPlanning.CreatePreviewRendererOptions(previewRequest, presentation));
            if (renderer.TotalFrames <= 0)
                throw new InvalidOperationException("timeline contains no renderable frames");

            long frameIndex = (long)Math.Round(
                preview.TimeSeconds * previewRequest.Output.FpsNumerator /
                (double)previewRequest.Output.FpsDenominator);
            frameIndex = Math.Clamp(frameIndex, 0, renderer.TotalFrames - 1);
            byte[] frame = renderer.RenderFrame(frameIndex);
            bool hasApproximations = false;
            string[] approximationNotes = Array.Empty<string>();
            if (output.Layout.Geometry.HasScopes)
            {
                byte[] scopeGrid = GetScopeGrid(
                    output, previewRequest, request, renderer, preview.TimeSeconds, cancellationToken);
                renderer.RenderCompositeFrame(frameIndex, scopeGrid, frame);
            }
            ValidationIssue warning = output.Plan.ValidationIssues
                .FirstOrDefault(issue => issue.Severity == ValidationSeverity.Warning);
            return Task.FromResult(new PreviewFrameResult
            {
                Fidelity = preview.Fidelity,
                TimeSeconds = preview.TimeSeconds,
                Width = renderer.Width,
                Height = renderer.Height,
                PngBytes = PngEncoder.Encode(renderer.Width, renderer.Height, frame),
                HasApproximations = hasApproximations,
                ApproximationNotes = approximationNotes,
                Warning = warning,
            });
        }

        public Task<MotionPreviewResult> RenderMotionAsync(
            VisualizationRequest request,
            MotionPreviewRequest preview,
            IProgress<PreviewProgress> progress,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("motion review is not part of the still generator");

        public ValueTask DisposeAsync()
        {
            foreach (ScopeStream stream in _scopeStreams.Values)
                stream.Dispose();
            try
            {
                if (Directory.Exists(_sessionRoot))
                    Directory.Delete(_sessionRoot, recursive: true);
            }
            catch
            {
                // The temporary review timeline is diagnostic-only.
            }
            return ValueTask.CompletedTask;
        }

        private VisualizationPlanning.PlanOutput Prepare(
            VisualizationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VisualizationPlanning.PlanOutput output = VisualizationPlanning.Prepare(
                request,
                new RenderRuntimeOptions(),
                _hasTimeline && File.Exists(_timelinePath) ? _timelinePath : null,
                _timelinePath);
            _hasTimeline = true;
            Capabilities = Capabilities with { HasCapturedTimeline = true };
            return output;
        }

        private byte[] GetScopeGrid(
            VisualizationPlanning.PlanOutput output,
            VisualizationRequest previewRequest,
            VisualizationRequest request,
            PanelOverlayRenderer renderer,
            double timeSeconds,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(output.MasterAudioPath)
                || !File.Exists(output.MasterAudioPath))
            {
                throw new InvalidOperationException(
                    "Scope Stage unavailable: no master WAV was produced by playback capture.");
            }

            string key = $"{request.Composition}-{renderer.Width}x{renderer.Height}-{renderer.ScopeFrameByteCount}";
            if (!_scopeStreams.TryGetValue(key, out ScopeStream stream))
            {
                stream = StartScopeStream(output, previewRequest, request, renderer, key);
                _scopeStreams.Add(key, stream);
            }

            int frameIndex = Math.Max(0, (int)Math.Round(
                timeSeconds * previewRequest.Output.FpsNumerator /
                (double)previewRequest.Output.FpsDenominator));
            return stream.ReadFrame(frameIndex, cancellationToken);
        }

        private ScopeStream StartScopeStream(
            VisualizationPlanning.PlanOutput output,
            VisualizationRequest previewRequest,
            VisualizationRequest request,
            PanelOverlayRenderer renderer,
            string key)
        {
            if (!output.Layout.Geometry.HasScopes)
                throw new InvalidOperationException("Scope Stage unavailable: layout has no scope region.");

            string outputDirectory = Path.Combine(_scopeRoot, key);
            VisualizationRequest scopeRequest = previewRequest with
            {
                Output = previewRequest.Output with
                {
                    Width = renderer.Width,
                    Height = renderer.Height,
                },
            };
            RenderRuntimeOptions runtime = new();
            VisualizationWorkspace workspace = VisualizationWorkspace.Create(
                scopeRequest, new FileInfo(Input.FullPath));
            workspace.EnsureDirectories();
            File.Copy(output.MasterAudioPath, workspace.MasterAudioPath, overwrite: true);

            VisualizationBackendResolution backend = VisualizationBackendResolver.Resolve(scopeRequest, runtime);
            GenericScopeArtifacts artifacts = VisualizationScopeCoordinator.Render(
                backend.Backend.Id,
                backend.Input,
                workspace,
                scopeRequest,
                runtime,
                output.Timeline.Devices,
                output.Timeline.Voices,
                Math.Max(1, output.Timeline.EndSample - output.Timeline.StartSample));
            if (!artifacts.Enabled || artifacts.Result is null || !artifacts.Result.Success)
            {
                throw new InvalidOperationException(
                    $"Scope Stage unavailable: {artifacts.Plan.Reason}");
            }

            CorrscopeConfigWriter.Write(
                workspace.CorrscopeConfigPath,
                workspace.ScopeDir,
                artifacts.Result,
                audioDir: "../audio",
                overrides: new CorrscopeOverrides
                {
                    Fps = scopeRequest.Output.FpsNumerator,
                    RenderWidth = output.Layout.Geometry.CorrscopeGridWidth,
                    RenderHeight = output.Layout.Geometry.CorrscopeGridHeight,
                    LayoutNCols = output.Layout.Geometry.ColumnCount,
                    IncludeMasterAsChannel = !artifacts.HasIsolatedStems,
                    IncludeSilentChannels = scopeRequest.Tracks.Selection == TrackSelectionMode.All,
                    HideLabels = false,
                    ResDivisor = 1.0,
                    Antialiasing = true,
                });

            var runner = new CorrscopeRunner(
                runtime.ToolTimeoutMinutes, runtime.CorrscopePath);
            if (!runner.IsAvailable)
                throw new InvalidOperationException(
                    "Scope Stage unavailable: Corrscope is not installed or cannot be resolved.");
            string bridgePath = Path.Combine(AppContext.BaseDirectory, "corrscope-frames.py");
            if (!File.Exists(bridgePath))
                throw new InvalidOperationException(
                    $"Scope Stage unavailable: bridge script not found: {bridgePath}");
            string pythonPath = CorrscopeRunner.ResolvePythonPath(runner.CorrPath);
            if (string.IsNullOrWhiteSpace(pythonPath))
                throw new InvalidOperationException(
                    "Scope Stage unavailable: no Python interpreter can import Corrscope.");
            Process process = runner.StartRawFrames(
                pythonPath, bridgePath, workspace.CorrscopeConfigPath);
            return new ScopeStream(process, renderer.ScopeFrameByteCount);
        }

        private sealed class ScopeStream : IDisposable
        {
            private readonly Process _process;
            private readonly Stream _output;
            private readonly Task<string> _error;
            private readonly int _frameBytes;
            private readonly Dictionary<int, byte[]> _frames = new();
            private int _nextFrame;

            public ScopeStream(Process process, int frameBytes)
            {
                _process = process;
                _output = process.StandardOutput.BaseStream;
                _error = process.StandardError.ReadToEndAsync();
                _frameBytes = frameBytes;
            }

            public byte[] ReadFrame(int frameIndex, CancellationToken cancellationToken)
            {
                if (_frames.TryGetValue(frameIndex, out byte[] cached))
                    return cached;
                if (frameIndex < _nextFrame)
                {
                    throw new InvalidOperationException(
                        "Scope Stage unavailable: configured moments are out of chronological order.");
                }
                while (_nextFrame <= frameIndex)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] frame = new byte[_frameBytes];
                    int offset = 0;
                    while (offset < frame.Length)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int read = _output.Read(frame, offset, frame.Length - offset);
                        if (read <= 0)
                        {
                            _process.WaitForExit();
                            string error = _error.GetAwaiter().GetResult();
                            throw new InvalidOperationException(
                                $"Scope Stage unavailable: Corrscope exited with {_process.ExitCode}. {error.Trim()}");
                        }
                        offset += read;
                    }
                    int current = _nextFrame++;
                    if (current == frameIndex)
                        _frames[current] = frame;
                }
                return _frames[frameIndex];
            }

            public void Dispose()
            {
                try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
                try { _process.Dispose(); } catch { }
            }
        }
    }

    private static class PngEncoder
    {
        public static byte[] Encode(int width, int height, byte[] rgba)
        {
            using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height);
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    Span<SixLabors.ImageSharp.PixelFormats.Rgba32> row = accessor.GetRowSpan(y);
                    int offset = y * width * 4;
                    for (int x = 0; x < row.Length; x++)
                        row[x] = new SixLabors.ImageSharp.PixelFormats.Rgba32(
                            rgba[offset + x * 4], rgba[offset + x * 4 + 1],
                            rgba[offset + x * 4 + 2], rgba[offset + x * 4 + 3]);
                }
            });
            using var stream = new MemoryStream();
            image.SaveAsPng(stream);
            return stream.ToArray();
        }
    }
}
