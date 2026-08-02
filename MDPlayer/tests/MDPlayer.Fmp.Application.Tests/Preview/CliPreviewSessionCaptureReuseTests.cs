using System.Text.Json;
using Fmp.Application.Contracts;
using Fmp.Application.Preview;
using Xunit;

namespace Fmp.Application.Tests;

/// <summary>
/// Capture-reuse tests for <see cref="CliPreviewSession"/>. A fake
/// <see cref="IPreviewCliRunner"/> records every CLI invocation and emulates
/// the renderer by writing the capture artifact (timeline.json + master.wav)
/// into the target <c>--capture-dir</c> on the first capture and re-using it
/// afterwards. This lets us assert exactly when a capture occurs without the
/// real renderer.
/// </summary>
public sealed class CliPreviewSessionCaptureReuseTests
{
    private static readonly byte[] ValidPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private sealed class FakeRunner : IPreviewCliRunner
    {
        public int CaptureCount { get; private set; }
        public List<string> Commands { get; } = new();
        public List<string?> CaptureKeys { get; } = new();
        public List<string?> RenderCaptureDirectories { get; } = new();

        public Task<ProcessOutput> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            Commands.Add(string.Join(' ', arguments));
            string command = arguments[0];

            // Extract --capture-dir and --capture-key.
            string? captureDir = null;
            string? captureKey = null;
            for (int i = 0; i < arguments.Count - 1; i++)
            {
                if (arguments[i] == "--capture-dir") captureDir = arguments[i + 1];
                if (arguments[i] == "--capture-key") captureKey = arguments[i + 1];
            }

            // Record which capture directory an export-style render was pointed
            // at, so the integration test can assert export reuse.
            if (command == "render")
                RenderCaptureDirectories.Add(captureDir);

            string? dir = captureDir ?? throw new InvalidOperationException("no --capture-dir");
            string timelinePath = Path.Combine(dir, "timeline.json");
            if (!File.Exists(timelinePath))
            {
                // Fresh capture: write the capture artifact.
                Directory.CreateDirectory(dir);
                File.WriteAllText(timelinePath, "{}");
                File.WriteAllBytes(Path.Combine(dir, "master.wav"), new byte[] { 1, 2, 3, 4 });
                CaptureCount++;
                CaptureKeys.Add(captureKey);
            }

            if (command == "plan")
            {
                string json = JsonSerializer.Serialize(new VisualizationPlanResult
                {
                    ResolvedLayout = "diagnostic",
                    RequestedLayout = "diagnostic",
                    InputPath = captureDir!,
                }, RequestJson.Options);
                return Task.FromResult(new ProcessOutput(0, json, string.Empty));
            }

            if (command == "preview")
            {
                string? output = ExtractAfter(arguments, "--output");
                if (!string.IsNullOrEmpty(output))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    File.WriteAllBytes(output, ValidPng);
                }
                return Task.FromResult(new ProcessOutput(0, string.Empty, string.Empty));
            }

            return Task.FromResult(new ProcessOutput(0, string.Empty, string.Empty));
        }

        private static string? ExtractAfter(IReadOnlyList<string> args, string flag)
        {
            for (int i = 0; i < args.Count - 1; i++)
                if (args[i] == flag)
                    return args[i + 1];
            return null;
        }
    }

    private static VisualizationInputInfo InputInfo(string path) => new()
    {
        FullPath = path,
        DisplayName = Path.GetFileName(path),
        Format = "VGZ",
        EstimatedDuration = TimeSpan.FromMinutes(3),
        Tracks = Array.Empty<TrackCapabilityInfo>(),
    };

    /// <summary>Builds a session whose input is a real on-disk file, so the
    /// capture key's input identity is stable for the session lifetime.</summary>
    private (CliPreviewSession Session, FakeRunner Runner, string Workspace) Build()
    {
        string input = Path.Combine(
            Path.GetTempPath(), "mdplayer-cli-session-" + Guid.NewGuid().ToString("N") + ".vgz");
        File.WriteAllBytes(input, new byte[] { 0x56, 0x67, 0x6d });

        string workspace = Path.Combine(
            Path.GetTempPath(), "mdplayer-cli-session-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        var runner = new FakeRunner();
        var session = new CliPreviewSession(
            "fake-mdplayer-render",
            workspace,
            InputInfo(input),
            runner);
        return (session, runner, workspace);
    }

    private static VisualizationRequest Request(string inputPath) => new()
    {
        InputPath = inputPath,
        OutputPath = Path.Combine(Path.GetTempPath(), "out.mp4"),
    };

    private static PreviewFrameRequest FrameRequest(double time) => new()
    {
        TimeSeconds = time,
        Width = 960,
        Height = 540,
        Fidelity = PreviewFidelity.TimelineStill,
    };

    [Fact]
    public async Task PlanPlusTwentyFrames_CausesOneCapture()
    {
        var (session, runner, _) = Build();
        VisualizationRequest request = Request(session.Input.FullPath);

        await session.PlanAsync(request, CancellationToken.None);
        for (int i = 0; i < 20; i++)
            await session.RenderFrameAsync(request, FrameRequest(i), CancellationToken.None);

        Assert.Equal(1, runner.CaptureCount);
    }

    [Fact]
    public async Task VisualOnlySettings_CauseNoAdditionalCaptures()
    {
        var (session, runner, _) = Build();
        VisualizationRequest request = Request(session.Input.FullPath);

        await session.PlanAsync(request, CancellationToken.None);
        await session.RenderFrameAsync(request, FrameRequest(5), CancellationToken.None);
        Assert.Equal(1, runner.CaptureCount);

        // Palette change: visual-only.
        var palette = request with { Style = request.Style with { Palette = PaletteKind.Accessible } };
        await session.RenderFrameAsync(palette, FrameRequest(5), CancellationToken.None);

        // Title change: visual-only.
        var title = palette with { Presentation = palette.Presentation with { Title = "New Title" } };
        await session.RenderFrameAsync(title, FrameRequest(5), CancellationToken.None);

        // Resolution change: visual-only.
        var res = title with { Output = title.Output with { Width = 1280, Height = 720 } };
        await session.RenderFrameAsync(res, FrameRequest(5), CancellationToken.None);

        // Frame time change: not capture-affecting.
        await session.RenderFrameAsync(res, FrameRequest(10), CancellationToken.None);

        Assert.Equal(1, runner.CaptureCount);
    }

    [Fact]
    public async Task CaptureAffectingSettings_CauseExactlyOneAdditionalCaptureEach()
    {
        var (session, runner, _) = Build();
        VisualizationRequest request = Request(session.Input.FullPath);

        await session.PlanAsync(request, CancellationToken.None);
        Assert.Equal(1, runner.CaptureCount);

        // Sample rate change -> one new capture.
        var newRate = request with { Playback = request.Playback with { SampleRate = 44_100 } };
        await session.RenderFrameAsync(newRate, FrameRequest(0), CancellationToken.None);
        Assert.Equal(2, runner.CaptureCount);

        // SSG gain change -> one new capture.
        var newGain = newRate with { Playback = newRate.Playback with { SsgGainDb = 6.0 } };
        await session.RenderFrameAsync(newGain, FrameRequest(0), CancellationToken.None);
        Assert.Equal(3, runner.CaptureCount);

        // SPC pitch change -> one new capture.
        var newPitch = newGain with { Playback = newGain.Playback with { SpcPitch = SpcPitchInterpretation.Relative } };
        await session.RenderFrameAsync(newPitch, FrameRequest(0), CancellationToken.None);
        Assert.Equal(4, runner.CaptureCount);
    }

    [Fact]
    public async Task TwoConcurrentInitialFrameRequests_CauseOneCapture()
    {
        var (session, runner, _) = Build();
        VisualizationRequest request = Request(session.Input.FullPath);

        Task a = session.RenderFrameAsync(request, FrameRequest(0), CancellationToken.None);
        Task b = session.RenderFrameAsync(request, FrameRequest(1), CancellationToken.None);
        await Task.WhenAll(a, b);

        Assert.Equal(1, runner.CaptureCount);
    }

    [Fact]
    public async Task CancellingInitialCapture_LeavesNoCommittedBundle()
    {
        var (session, runner, workspace) = Build();
        VisualizationRequest request = Request(session.Input.FullPath);

        using var cts = new CancellationTokenSource();
        // Cancelled before the capture gate is entered, so nothing commits.
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.RenderFrameAsync(request, FrameRequest(0), cts.Token));

        Assert.Equal(0, runner.CaptureCount);

        // No committed final bundle directory remains.
        string capturesRoot = Path.Combine(workspace, "captures");
        string[] finalDirs = Directory.Exists(capturesRoot)
            ? Directory.GetDirectories(capturesRoot)
                .Where(d => !Path.GetFileName(d).EndsWith(".tmp", StringComparison.Ordinal))
                .ToArray()
            : Array.Empty<string>();
        Assert.Empty(finalDirs);
    }

    // ── Patch 4: full preview workflow with export reuse ───────────────

    private static Task ExportRenderAsync(
        FakeRunner runner,
        string? captureDirectory,
        string captureKey,
        VisualizationRequest request)
    {
        var args = new List<string>
        {
            "render",
            "--request-json", Path.Combine(Path.GetTempPath(), "export-request.json"),
            "--capture-key", captureKey,
        };
        if (!string.IsNullOrWhiteSpace(captureDirectory))
        {
            args.Add("--capture-dir");
            args.Add(captureDirectory);
        }
        return runner.RunAsync(args, CancellationToken.None);
    }

    /// <summary>The exact Patch 4 §2 sequence: one capture across plan, many
    /// frames and visual-only settings, reused by a simulated export; then a
    /// sample-rate change produces exactly one additional capture with a new
    /// key/directory reused by the next export.</summary>
    [Fact]
    public async Task FullWorkflow_ReusesOneCapture_ThenOneMoreOnSampleRateChange()
    {
        var (session, runner, _) = Build();
        var traces = new List<CaptureTrace>();
        session.CaptureTraceOccurred += traces.Add;

        VisualizationRequest request = Request(session.Input.FullPath);

        // 1. Open input / plan.
        await session.PlanAsync(request, CancellationToken.None);

        // 2-5. Frames at 0, 10, 20 seconds.
        await session.RenderFrameAsync(request, FrameRequest(0), CancellationToken.None);
        await session.RenderFrameAsync(request, FrameRequest(10), CancellationToken.None);
        VisualizationRequest at20 = request;
        await session.RenderFrameAsync(at20, FrameRequest(20), CancellationToken.None);

        // 6. Palette change (visual-only) → frame at 20s.
        VisualizationRequest palette = at20 with
        {
            Style = at20.Style with { Palette = PaletteKind.Accessible },
        };
        await session.RenderFrameAsync(palette, FrameRequest(20), CancellationToken.None);

        // 7. Title change (visual-only) → frame at 20s.
        VisualizationRequest titled = palette with
        {
            Presentation = palette.Presentation with { Title = "Workflow title" },
        };
        await session.RenderFrameAsync(titled, FrameRequest(20), CancellationToken.None);

        // 8. Resolution change (visual-only) → frame at 20s.
        VisualizationRequest resized = titled with
        {
            Output = titled.Output with { Width = 1280, Height = 720 },
        };
        await session.RenderFrameAsync(resized, FrameRequest(20), CancellationToken.None);

        // 9. Exactly one capture so far; all decisions share one capture key.
        Assert.Equal(1, runner.CaptureCount);
        string firstKey = traces.Single(t => t.Kind == CaptureTraceKind.Started).CaptureKey;
        Assert.True(traces.Where(t => t.Reused).All(t => t.CaptureKey == firstKey),
            "all preview reuses use the same capture key");

        // 10. Export render reuses the session's active capture directory.
        Assert.False(string.IsNullOrWhiteSpace(session.ActiveCaptureDirectory));
        await ExportRenderAsync(runner, session.ActiveCaptureDirectory, firstKey, resized);
        Assert.Equal(1, runner.CaptureCount);
        Assert.All(runner.RenderCaptureDirectories,
            d => Assert.Equal(session.ActiveCaptureDirectory, d));

        // 11. Change sample rate → exactly one additional capture.
        VisualizationRequest newRate = resized with
        {
            Playback = resized.Playback with { SampleRate = 44_100 },
        };
        await session.RenderFrameAsync(newRate, FrameRequest(20), CancellationToken.None);
        Assert.Equal(2, runner.CaptureCount);

        string secondKey = traces.Where(t => t.Kind == CaptureTraceKind.Started)
            .Select(t => t.CaptureKey).Distinct().ElementAt(1);
        Assert.NotEqual(firstKey, secondKey);
        Assert.False(string.IsNullOrWhiteSpace(session.ActiveCaptureDirectory));

        // 12. Next export uses the new capture directory/key.
        await ExportRenderAsync(runner, session.ActiveCaptureDirectory, secondKey, newRate);
        Assert.Equal(2, runner.CaptureCount);
        Assert.Equal(session.ActiveCaptureDirectory, runner.RenderCaptureDirectories.Last());

        session.CaptureTraceOccurred -= traces.Add;
    }

    [Fact]
    public async Task CaptureTrace_ReportsStartedReusedCompletedWithKeys()
    {
        var (session, runner, _) = Build();
        var traces = new List<CaptureTrace>();
        session.CaptureTraceOccurred += traces.Add;

        VisualizationRequest request = Request(session.Input.FullPath);
        await session.PlanAsync(request, CancellationToken.None);
        await session.RenderFrameAsync(request, FrameRequest(10), CancellationToken.None);

        // First capture: Started then Completed, same key, not reused.
        Assert.Contains(traces, t => t.Kind == CaptureTraceKind.Started && !t.Reused);
        Assert.Contains(traces, t => t.Kind == CaptureTraceKind.Completed && !t.Reused);
        string startedKey = traces.Single(t => t.Kind == CaptureTraceKind.Started).CaptureKey;
        string completedKey = traces.Single(t => t.Kind == CaptureTraceKind.Completed).CaptureKey;
        Assert.Equal(startedKey, completedKey);
        Assert.False(string.IsNullOrWhiteSpace(startedKey));
        Assert.Contains("capture-v2-", startedKey);

        // The second preview reused the same bundle: at least one Reused trace
        // with the same key and non-empty capture directory.
        Assert.Contains(traces,
            t => t.Kind == CaptureTraceKind.Reused
                && t.Reused
                && t.CaptureKey == startedKey
                && !string.IsNullOrWhiteSpace(t.CaptureDirectory));

        session.CaptureTraceOccurred -= traces.Add;
    }
}
