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
/// Structured diagnostic for a capture lifecycle event: a capture was started
/// anew (<see cref="CaptureTraceKind.Started"/>), an existing valid bundle was
/// reused (<see cref="CaptureTraceKind.Reused"/>), or a new capture completed
/// (<see cref="CaptureTraceKind.Completed"/>). Carries only the capture key and
/// bundle directory — never the full request snapshot. Tests and users use this
/// to tell whether timeline navigation or style changes caused recapture.
/// </summary>
internal enum CaptureTraceKind
{
    Started,
    Reused,
    Completed,
}

/// <summary>
/// A single capture lifecycle event with the associated capture key and bundle
/// directory. <see cref="Reused"/> is a convenience for the reuse decision.
/// </summary>
internal sealed record CaptureTrace(
    string CaptureKey,
    string? CaptureDirectory,
    bool Reused,
    CaptureTraceKind Kind,
    DateTime OccurredUtc);

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
    private readonly IPreviewCliRunner _runner;
    private readonly CaptureBundleStore _captureStore;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private CaptureBundle? _activeCapture;
    private string? _activeCaptureKey;
    private int _frameRevision;
    private int _motionRevision;
    private VisualizationSessionCapabilities _capabilities;

    public CliPreviewSession(string executablePath, string workspace, VisualizationInputInfo input)
        : this(executablePath, workspace, input, new ProcessCliRunner(executablePath))
    {
    }

    /// <summary>Test constructor injecting a fake CLI runner.</summary>
    internal CliPreviewSession(
        string executablePath,
        string workspace,
        VisualizationInputInfo input,
        IPreviewCliRunner runner)
    {
        _executablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Input = input ?? throw new ArgumentNullException(nameof(input));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));

        _requestJsonPath = Path.Combine(workspace, "request.json");
        _captureStore = new CaptureBundleStore(workspace);

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

    /// <summary>
    /// Absolute directory of the active reusable capture bundle, or null when
    /// no capture has been produced yet. Export can hand this to the render CLI
    /// via <c>--capture-dir</c> to reuse the same timeline instead of
    /// re-capturing.
    /// </summary>
    public string? ActiveCaptureDirectory => _activeCapture?.DirectoryPath;

    /// <summary>
    /// Lightweight diagnostic channel so callers/tests can tell whether a
    /// preview/plan/export call reused an existing capture or started a new
    /// one. Carries only the capture key/directory and reuse flag — never the
    /// full request JSON.
    /// </summary>
    internal event Action<CaptureTrace>? CaptureTraceOccurred;

    private void EmitCapture(string captureKey, string? captureDirectory, CaptureTraceKind kind)
        => CaptureTraceOccurred?.Invoke(
            new CaptureTrace(
                captureKey,
                captureDirectory,
                kind == CaptureTraceKind.Reused,
                kind,
                DateTime.UtcNow));

    public async Task<VisualizationPlanResult> PlanAsync(
        VisualizationRequest request,
        CancellationToken cancellationToken)
    {
        VisualizationRequestSerializer.WriteToFile(request, _requestJsonPath);

        CaptureBundle bundle = await EnsureCaptureBundleAsync(request, cancellationToken);

        var args = new List<string>
        {
            "plan",
            "--request-json", _requestJsonPath,
            "--json",
        };
        AddCaptureArgs(args, bundle);

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

        if (_activeCapture is { HasTimeline: true })
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

        CaptureBundle bundle = await EnsureCaptureBundleAsync(request, cancellationToken);

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
        AddCaptureArgs(args, bundle);

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
            ScopeKind = preview.Fidelity switch
            {
                PreviewFidelity.AccurateStill => PreviewScopeKind.Corrscope,
                PreviewFidelity.InteractiveStill => PreviewScopeKind.PerChannel,
                _ => PreviewScopeKind.None,
            },
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

        CaptureBundle bundle = await EnsureCaptureBundleAsync(request, cancellationToken);

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
        AddCaptureArgs(args, bundle);

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

    // ---- Helpers ----

    private void AddCaptureArgs(List<string> args, CaptureBundle bundle)
    {
        // Reuse a durable capture bundle instead of re-capturing per frame. The
        // capture key already encodes every playback/capture-affecting setting,
        // so a committed bundle is guaranteed to match the current request.
        args.Add("--capture-dir");
        args.Add(bundle.DirectoryPath);
        args.Add("--capture-key");
        args.Add(bundle.Key);
    }

    /// <summary>
    /// Ensures a valid capture bundle exists for the request, reusing a
    /// committed one whenever possible and performing exactly one new capture
    /// otherwise. The capture gate prevents two concurrent preview requests
    /// from starting duplicate captures.
    /// </summary>
    private async Task<CaptureBundle> EnsureCaptureBundleAsync(
        VisualizationRequest request,
        CancellationToken cancellationToken)
    {
        FileInfo input = new(request.InputPath);
        string captureKey = PreviewCacheKey.CaptureKey(
            input.FullName,
            input.Exists ? input.Length : 0,
            input.Exists ? input.LastWriteTimeUtc : default,
            request);

        // Fast path: the active key matches and the bundle still validates.
        if (_activeCaptureKey == captureKey
            && _activeCapture is { } active
            && _captureStore.TryOpenValid(
                captureKey,
                input.FullName,
                input.Exists ? input.Length : 0,
                input.Exists ? input.LastWriteTimeUtc : default) is { })
        {
            EmitCapture(captureKey, active.DirectoryPath, CaptureTraceKind.Reused);
            return active;
        }

        await _captureGate.WaitAsync(cancellationToken);
        try
        {
            // Re-check after acquiring the gate: a concurrent request may have
            // just committed a matching bundle.
            if (_activeCaptureKey == captureKey && _activeCapture is { } recheck)
            {
                CaptureBundle? existing = _captureStore.TryOpenValid(
                    captureKey,
                    input.FullName,
                    input.Exists ? input.Length : 0,
                    input.Exists ? input.LastWriteTimeUtc : default);
                if (existing is { })
                {
                    _activeCapture = existing;
                    EmitCapture(captureKey, existing.DirectoryPath, CaptureTraceKind.Reused);
                    return existing;
                }
            }

            // Ask the store for a previously committed valid bundle.
            CaptureBundle? valid = _captureStore.TryOpenValid(
                captureKey,
                input.FullName,
                input.Exists ? input.Length : 0,
                input.Exists ? input.LastWriteTimeUtc : default);
            if (valid is { })
            {
                _activeCapture = valid;
                _activeCaptureKey = captureKey;
                _capabilities = _capabilities with { HasCapturedTimeline = true };
                EmitCapture(captureKey, valid.DirectoryPath, CaptureTraceKind.Reused);
                return valid;
            }

            // No reusable bundle: create a temporary capture directory, drive a
            // single CLI call to capture into it, then commit.
            CaptureBundle temporary = _captureStore.CreatePaths(captureKey);
            EmitCapture(captureKey, temporary.DirectoryPath, CaptureTraceKind.Started);
            try
            {
                VisualizationRequestSerializer.WriteToFile(request, _requestJsonPath);
                var args = new List<string>
                {
                    "plan",
                    "--request-json", _requestJsonPath,
                    "--json",
                };
                // Direct the CLI to write its capture into the temporary bundle
                // directory (which is not yet a valid bundle, so it captures).
                AddCaptureArgs(args, temporary);

                ProcessOutput output = await RunCliAsync(args, cancellationToken);
                if (output.ExitCode != 0)
                    throw new InvalidOperationException(
                        $"mdplayer-render capture failed (exit {output.ExitCode}): {TrimError(output.StandardError)}");

                // The CLI wrote timeline.json (and master.wav for backends that
                // produce one); the store validates and commits atomically.
                CaptureBundle committed = _captureStore.Commit(
                    temporary,
                    CreateManifest(captureKey, input, temporary));
                _activeCapture = committed;
                _activeCaptureKey = captureKey;
                _capabilities = _capabilities with { HasCapturedTimeline = true };
                EmitCapture(captureKey, committed.DirectoryPath, CaptureTraceKind.Completed);
                return committed;
            }
            catch (Exception)
            {
                // Never leave a partial temporary bundle behind.
                _captureStore.DeleteIncomplete(temporary.DirectoryPath);
                throw;
            }
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private static CaptureBundleManifest CreateManifest(
        string captureKey,
        FileInfo input,
        CaptureBundle temporary)
    {
        return new CaptureBundleManifest
        {
            CaptureKey = captureKey,
            InputPath = input.FullName,
            InputLength = input.Exists ? input.Length : 0,
            InputLastWriteUtcTicks = input.Exists ? input.LastWriteTimeUtc.Ticks : 0,
            CreatedUtc = DateTime.UtcNow,
            TimelineFileName = "timeline.json",
            MasterWaveFileName = File.Exists(Path.Combine(temporary.DirectoryPath, "master.wav"))
                ? "master.wav"
                : null,
            StemsDirectoryName = Directory.Exists(Path.Combine(temporary.DirectoryPath, "stems"))
                ? "stems"
                : null,
        };
    }

    private async Task<ProcessOutput> RunCliAsync(IReadOnlyList<string> args, CancellationToken ct)
        => await _runner.RunAsync(args, ct);

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

    /// <summary>Per-method capture gate disposal via <see cref="System.IDisposable"/>.</summary>
    public ValueTask DisposeAsync()
    {
        _captureGate.Dispose();
        return ValueTask.CompletedTask;
    }

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

/// <summary>Captured result of one CLI subprocess invocation.</summary>
internal sealed record ProcessOutput(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Narrow seam over the CLI subprocess so capture-reuse behaviour can be tested
/// without the real renderer. The default implementation spawns
/// <c>mdplayer-render</c>; tests inject a fake that records arguments and
/// emulates artifact creation.
/// </summary>
internal interface IPreviewCliRunner
{
    Task<ProcessOutput> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

/// <summary>Default process-based runner for <see cref="IPreviewCliRunner"/>.</summary>
internal sealed class ProcessCliRunner : IPreviewCliRunner
{
    private readonly string _executablePath;

    public ProcessCliRunner(string executablePath)
        => _executablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));

    public async Task<ProcessOutput> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in arguments)
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
}
