using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fmp.Application.Contracts;
using Fmp.Application.Export;
using Fmp.Application.Inspection;

namespace Fmp.Application.Preview;

/// <summary>
/// Creates process-based preview sessions that drive the mdplayer-render CLI
/// (<c>plan</c> and <c>preview</c> subcommands) as child processes. The input
/// is inspected in-process first so the GUI always receives probe-level
/// capabilities and issues, even when capture requires external runtimes.
/// </summary>
public sealed class CliPreviewSessionFactory : IVisualizationPreviewSessionFactory
{
    private const string RenderExecutableName = "mdplayer-render";

    public async Task<IVisualizationPreviewSession> OpenAsync(string inputPath, CancellationToken cancellationToken)
    {
        VisualizationInputInfo input = await VisualizationInputInspector.InspectAsync(inputPath, cancellationToken);

        string? exePath = ResolveRenderExecutable();
        if (exePath == null)
        {
            throw new InvalidOperationException(
                $"The '{RenderExecutableName}' executable was not found. Set the MDPLAYER_RENDER_PATH " +
                "environment variable, place it next to this application, or add it to PATH.");
        }

        string sessionId = ShortHash(input.FullPath);
        string workspace = Path.Combine(
            Path.GetTempPath(), "MDPlayer", "Visualizer", "sessions", sessionId);
        Directory.CreateDirectory(workspace);

        return new CliPreviewSession(exePath, workspace, input);
    }

    /// <summary>MDPLAYER_RENDER_PATH → AppContext.BaseDirectory → PATH.</summary>
    private static string? ResolveRenderExecutable()
    {
        string? explicitPath = Environment.GetEnvironmentVariable("MDPLAYER_RENDER_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return Path.GetFullPath(explicitPath);

        string name = OperatingSystem.IsWindows()
            ? RenderExecutableName + ".exe"
            : RenderExecutableName;

        string bundled = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(bundled))
            return bundled;

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                    return candidate;
                if (!OperatingSystem.IsWindows())
                {
                    string exe = candidate + ".exe";
                    if (File.Exists(exe))
                        return exe;
                }
            }
            catch
            {
                // Unreadable PATH entry; keep searching.
            }
        }

        return null;
    }

    private static string ShortHash(string value)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}

/// <summary>
/// Process-based preview session. Each call serializes the current request to
/// <c>request.json</c> in the session workspace and invokes mdplayer-render
/// with <c>plan</c> or <c>preview</c>. The session workspace is retained for
/// diagnostics (timeline.json and rendered preview frames).
/// </summary>
public sealed class CliPreviewSession : IVisualizationPreviewSession
{
    private readonly string _executablePath;
    private readonly string _workspace;
    private readonly string _requestJsonPath;
    private readonly string _timelinePath;
    private int _frameRevision;
    private int _motionRevision;
    private VisualizationSessionCapabilities _capabilities;

    public CliPreviewSession(string executablePath, string workspace, VisualizationInputInfo input)
    {
        _executablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Input = input ?? throw new ArgumentNullException(nameof(input));

        _requestJsonPath = Path.Combine(workspace, "request.json");
        _timelinePath = Path.Combine(workspace, "timeline.json");

        _capabilities = new VisualizationSessionCapabilities
        {
            SemanticCapture = input.SupportsSemanticCapture,
            ScopeCapture = input.SupportsScopeCapture,
            AnalysisAvailable = input.SupportsAnalysis,
            HasCapturedTimeline = false,
            Issues = input.Issues,
        };
    }

    public VisualizationInputInfo Input { get; }

    public VisualizationSessionCapabilities Capabilities => _capabilities;

    public async Task<VisualizationPlanResult> PlanAsync(
        VisualizationRequest request,
        CancellationToken cancellationToken)
    {
        VisualizationRequestSerializer.WriteToFile(request, _requestJsonPath);

        var args = new List<string>
        {
            "plan",
            "--request-json", _requestJsonPath,
            "--json",
        };
        AddTimelineArgs(args);

        ProcessOutput output = await RunCliAsync(args, cancellationToken);
        if (output.ExitCode != 0)
            throw new InvalidOperationException(
                $"mdplayer-render plan failed (exit {output.ExitCode}): {TrimError(output.StandardError)}");

        VisualizationPlanResult? plan;
        try
        {
            plan = JsonSerializer.Deserialize<VisualizationPlanResult>(output.StandardOutput, RequestJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"mdplayer-render plan returned malformed JSON: {ex.Message}", ex);
        }
        if (plan == null)
            throw new InvalidOperationException("mdplayer-render plan returned no result.");

        bool timelineAvailable = !string.IsNullOrEmpty(plan.TimelinePath) || File.Exists(_timelinePath);
        if (timelineAvailable)
            _capabilities = _capabilities with { HasCapturedTimeline = true };

        return plan;
    }

    public async Task<PreviewFrameResult> RenderFrameAsync(
        VisualizationRequest request,
        PreviewFrameRequest preview,
        CancellationToken cancellationToken)
    {
        VisualizationRequestSerializer.WriteToFile(request, _requestJsonPath);

        int width = preview.Width ?? request.Output.Width;
        int height = preview.Height ?? request.Output.Height;
        int revision = ++_frameRevision;
        string pngPath = Path.Combine(_workspace, $"frame-{revision}.png");

        var args = new List<string>
        {
            "preview",
            "--request-json", _requestJsonPath,
            "--time", preview.TimeSeconds.ToString("R", CultureInfo.InvariantCulture),
            "--width", width.ToString(CultureInfo.InvariantCulture),
            "--height", height.ToString(CultureInfo.InvariantCulture),
            "--output", pngPath,
            "--json",
        };
        if (preview.Fidelity == PreviewFidelity.Layout)
        {
            args.Add("--fidelity");
            args.Add("layout");
        }
        AddTimelineArgs(args);

        ProcessOutput output = await RunCliAsync(args, cancellationToken);
        if (output.ExitCode != 0)
            throw new InvalidOperationException(
                $"mdplayer-render preview failed (exit {output.ExitCode}): {TrimError(output.StandardError)}");

        if (!File.Exists(pngPath))
            throw new InvalidOperationException(
                $"mdplayer-render preview did not produce '{pngPath}'.");

        byte[] pngBytes;
        try
        {
            pngBytes = await File.ReadAllBytesAsync(pngPath, cancellationToken);
        }
        finally
        {
            try { File.Delete(pngPath); } catch { }
        }

        PreviewFrameJson? json = ParseOptional<PreviewFrameJson>(output.StandardOutput);
        return new PreviewFrameResult
        {
            Fidelity = preview.Fidelity,
            TimeSeconds = preview.TimeSeconds,
            Width = width,
            Height = height,
            PngBytes = pngBytes,
            HasApproximations = json?.HasApproximations ?? false,
            ApproximationNotes = (IReadOnlyList<string>?)json?.ApproximationNotes ?? Array.Empty<string>(),
            Warning = json?.Warning == null ? null : ToValidationIssue(json.Warning),
        };
    }

    public async Task<MotionPreviewResult> RenderMotionAsync(
        VisualizationRequest request,
        MotionPreviewRequest preview,
        IProgress<PreviewProgress>? progress,
        CancellationToken cancellationToken)
    {
        VisualizationRequestSerializer.WriteToFile(request, _requestJsonPath);

        int revision = ++_motionRevision;
        string outputDir = Path.Combine(_workspace, $"motion-{revision}");

        var args = new List<string>
        {
            "preview",
            "--request-json", _requestJsonPath,
            "--motion",
            "--start", preview.StartSeconds.ToString("R", CultureInfo.InvariantCulture),
            "--duration", preview.DurationSeconds.ToString("R", CultureInfo.InvariantCulture),
            "--fps", preview.Fps.ToString(CultureInfo.InvariantCulture),
            "--max-width", preview.MaxWidth.ToString(CultureInfo.InvariantCulture),
            "--max-height", preview.MaxHeight.ToString(CultureInfo.InvariantCulture),
            "--output-dir", outputDir,
            "--json",
        };
        AddTimelineArgs(args);

        progress?.Report(new PreviewProgress("rendering frames", 0, "Rendering motion preview"));

        ProcessOutput output = await RunCliAsync(args, cancellationToken);
        if (output.ExitCode != 0)
            throw new InvalidOperationException(
                $"mdplayer-render motion preview failed (exit {output.ExitCode}): {TrimError(output.StandardError)}");

        MotionManifestJson? manifest = ParseOptional<MotionManifestJson>(output.StandardOutput);
        int frameCount = manifest?.FrameCount ?? 0;
        progress?.Report(new PreviewProgress("rendering frames", 1.0, $"Rendered {frameCount} frames"));

        return new MotionPreviewResult
        {
            FrameCount = frameCount,
            Fps = manifest?.Fps ?? preview.Fps,
            Width = manifest?.Width ?? preview.MaxWidth,
            Height = manifest?.Height ?? preview.MaxHeight,
            FramesDirectory = outputDir,
            FramePaths = (IReadOnlyList<string>?)manifest?.Frames ?? Array.Empty<string>(),
            HasApproximations = manifest?.HasApproximations ?? false,
            ApproximationNotes = (IReadOnlyList<string>?)manifest?.ApproximationNotes ?? Array.Empty<string>(),
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- Helpers ----

    private void AddTimelineArgs(List<string> args)
    {
        // Write the freshly captured timeline back to the session workspace so
        // it can be inspected, but never *seed* a cached timeline into new
        // plan/preview subprocesses:
        //
        //  1. The cached timeline is keyed only by the input path hash, so
        //     playback changes (loop count, fade/tail, sample rate, SSG gain,
        //     SPC pitch mode, backend) would silently reuse a stale timeline.
        //  2. A seeded timeline skips semantic capture, so the fresh temporary
        //     workspace never receives a master WAV — backends whose scope
        //     strategy depends on the captured master then fail with "master
        //     WAV was not produced by playback capture".
        //
        // Each plan/preview therefore re-captures. The durable optimization is
        // a capture bundle (timeline + master + stems) keyed by the full
        // CaptureKey, not a bare timeline file.
        args.Add("--timeline-out");
        args.Add(_timelinePath);
    }

    private async Task<ProcessOutput> RunCliAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Failed to start '{_executablePath}'.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to start '{_executablePath}': {ex.Message}", ex);
        }

        using var killOnCancel = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        });

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);
        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        await process.WaitForExitAsync(ct);

        return new ProcessOutput(process.ExitCode, stdout, stderr);
    }

    private static T? ParseOptional<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, RequestJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ValidationIssue ToValidationIssue(PreviewWarningJson warning) => new()
    {
        Code = string.IsNullOrWhiteSpace(warning.Code) ? ValidationCodes.PreviewCancelled : warning.Code,
        Severity = Enum.TryParse(warning.Severity, ignoreCase: true, out ValidationSeverity severity)
            ? severity
            : ValidationSeverity.Warning,
        Message = string.IsNullOrWhiteSpace(warning.Message) ? "Preview warning" : warning.Message,
        SettingPath = warning.SettingPath,
        Detail = warning.Detail,
        SuggestedAction = warning.SuggestedAction,
    };

    private static string TrimError(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "no error output";
        return string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())).Trim();
    }

    private sealed record ProcessOutput(int ExitCode, string StandardOutput, string StandardError);

    /// <summary>Permissive parse target for the <c>preview</c> JSON metadata.</summary>
    private sealed class PreviewFrameJson
    {
        public bool HasApproximations { get; set; }
        public List<string>? ApproximationNotes { get; set; }
        public PreviewWarningJson? Warning { get; set; }
    }

    private sealed class PreviewWarningJson
    {
        public string? Code { get; set; }
        public string? Severity { get; set; }
        public string? Message { get; set; }
        public string? SettingPath { get; set; }
        public string? Detail { get; set; }
        public string? SuggestedAction { get; set; }
    }

    /// <summary>Manifest returned by the <c>--motion</c> preview on stdout.</summary>
    private sealed class MotionManifestJson
    {
        public int FrameCount { get; set; }
        public int Fps { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public List<string>? Frames { get; set; }
        public bool HasApproximations { get; set; }
        public List<string>? ApproximationNotes { get; set; }
    }
}
