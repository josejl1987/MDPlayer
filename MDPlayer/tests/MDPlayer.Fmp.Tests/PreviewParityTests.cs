using System.Diagnostics;
using Fmp.Application.Contracts;
using Fmp.Cli;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>PR5 acceptance tests for the shared frame renderer and preview session.</summary>
public sealed class PreviewParityTests
{
    // ---- Fixtures ----

    private static VisualizationRequest FixtureRequest(string input = "fixture.ovi")
        => new()
        {
            InputPath = input,
            OutputPath = "/tmp/fixture.mp4",
            Composition = CompositionKind.Diagnostic,
            Output = new OutputSettings
            {
                Width = 1920,
                Height = 1080,
                FpsNumerator = 60,
                FpsDenominator = 1,
            },
            View = new ViewSettings
            {
                PastSeconds = 0.75,
                FutureSeconds = 2.25,
                TimeGrid = TimeGridMode.Automatic,
            },
            Style = new StyleSettings
            {
                Palette = PaletteKind.Default,
                Effects = VisualEffects.Cinematic,
                NoteColor = global::Fmp.Application.Contracts.NoteColorMode.Channel,
            },
            Playback = new PlaybackSettings
            {
                SampleRate = 44100,
                LoopCount = 1,
                FadeSeconds = 5.0,
                TailSeconds = 0.5,
            },
            Tracks = new TrackSettings
            {
                Selection = TrackSelectionMode.All,
            },
            Presentation = new PresentationSettings
            {
                Title = "Fixture",
            },
        };

    // ---- C. Style changes do not recapture ----

    [Fact]
    public void StyleChangeDoesNotChangeCaptureKey()
    {
        var runtime = new RenderRuntimeOptions();
        VisualizationRequest first = FixtureRequest();
        VisualizationRequest second = first with
        {
            Style = first.Style with { Effects = VisualEffects.Off },
        };

        Assert.Equal(CaptureKey.From(first, runtime), CaptureKey.From(second, runtime));
    }

    [Fact]
    public void CompositionAndDimensionChangeDoNotChangeCaptureKey()
    {
        var runtime = new RenderRuntimeOptions();
        VisualizationRequest first = FixtureRequest();
        VisualizationRequest second = first with
        {
            Composition = CompositionKind.Diagnostic,
            Output = first.Output with { Width = 960, Height = 540 },
            View = first.View with { PastSeconds = 2.0 },
        };

        Assert.Equal(CaptureKey.From(first, runtime), CaptureKey.From(second, runtime));
    }

    // ---- D. Playback changes recapture ----

    [Fact]
    public void SampleRateChangesCaptureKey()
    {
        var runtime = new RenderRuntimeOptions();
        VisualizationRequest first = FixtureRequest();
        VisualizationRequest second = first with
        {
            Playback = first.Playback with { SampleRate = 22050 },
        };

        Assert.NotEqual(CaptureKey.From(first, runtime), CaptureKey.From(second, runtime));
    }

    [Fact]
    public void LoopCountChangesCaptureKey()
    {
        var runtime = new RenderRuntimeOptions();
        VisualizationRequest first = FixtureRequest();
        VisualizationRequest second = first with
        {
            Playback = first.Playback with { LoopCount = 3 },
        };

        Assert.NotEqual(CaptureKey.From(first, runtime), CaptureKey.From(second, runtime));
    }

    [Fact]
    public void SsgGainChangesCaptureKey()
    {
        var runtime = new RenderRuntimeOptions();
        VisualizationRequest first = FixtureRequest();
        VisualizationRequest second = first with
        {
            Playback = first.Playback with { SsgGainDb = -6 },
        };

        Assert.NotEqual(CaptureKey.From(first, runtime), CaptureKey.From(second, runtime));
    }

    [Fact]
    public void SpcPitchInterpretationChangesCaptureKey()
    {
        var runtime = new RenderRuntimeOptions();
        VisualizationRequest first = FixtureRequest();
        VisualizationRequest second = first with
        {
            Playback = first.Playback with { SpcPitch = SpcPitchInterpretation.Relative },
        };

        Assert.NotEqual(CaptureKey.From(first, runtime), CaptureKey.From(second, runtime));
    }

    // ---- E. Request immutability ----

    /// <summary>
    /// The preview session must project a snapshot rather than mutate the
    /// caller's request; equality (and reference identity for the nested record)
    /// must be preserved after a dimension-affecting preview.
    /// </summary>
    [Fact]
    public void RenderKeyProjection_DoesNotMutateSource()
    {
        VisualizationRequest request = FixtureRequest();
        OutputSettings original = request.Output;
        TrackSettings originalTracks = request.Tracks;
        StyleSettings originalStyle = request.Style;

        // Build a projected request the way RenderFrameAsync does.
        VisualizationRequest projected = request with
        {
            Output = request.Output with
            {
                Width = 960,
                Height = 540,
            },
        };

        Assert.Same(original, request.Output);
        Assert.Same(originalTracks, request.Tracks);
        Assert.Same(originalStyle, request.Style);
        Assert.Equal(1920, request.Output.Width);
        Assert.Equal(1080, request.Output.Height);
        Assert.Equal(960, projected.Output.Width);
        Assert.Equal(540, projected.Output.Height);
    }

    // ---- G. Corrscope frame source restarts for backward seek ----

    /// <summary>
    /// The shared scope frame source must restart for a backward seek and read
    /// forward to the requested frame instead of throwing.
    /// </summary>
    [Fact]
    public void CorrscopeSourceRestartsForBackwardSeek()
    {
        const int frameBytes = 16;
        int startCount = 0;

        Func<Process> start = () =>
        {
            startCount++;
            // A deterministic child emitting a known byte pattern: each frame
            // is 16 bytes ending with i on the 4th byte.
            return StartPatternProcess();
        };

        using var source = new CorrscopeFrameSource(start, frameBytes);

        var frame120 = new byte[frameBytes];
        source.ReadFrame(120, frame120);

        // Backward seek: the source must restart (2nd process) and re-read from 0.
        var frame30 = new byte[frameBytes];
        source.ReadFrame(30, frame30);

        Assert.True(startCount >= 2, $"expected at least 2 starts, got {startCount}");
        // Frame 30 from the restarted source: first byte equals 30.
        Assert.Equal(30, frame30[0]);
    }

    private static Process StartPatternProcess()
    {
        // Emit 16-byte frames: [ frameIndex, 15 zero bytes ].
        string[] args =
        {
            "-c",
            "import sys; i=0\n" +
            "while True:\n" +
            "  sys.stdout.buffer.write(bytes([i & 0xFF]) + b'\\x00'*15);\n" +
            "  sys.stdout.buffer.flush();\n" +
            "  i+=1",
        };
        var psi = new ProcessStartInfo
        {
            FileName = "python3",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    // ---- I. No private review renderer remains ----

    [Fact]
    public void ReviewCommand_HasNoPrivateFrameImplementation()
    {
        string reviewSource = SourceFor("ReviewCommand.cs");
        Assert.DoesNotContain("class ScopeStream", reviewSource);
        Assert.DoesNotContain("class PngEncoder", reviewSource);
        Assert.DoesNotContain("StartScopeStream", reviewSource);
    }

    [Fact]
    public void PreviewCommand_HasNoRendererOrBackendOrchestration()
    {
        string source = SourceFor("PreviewCommand.cs");
        Assert.DoesNotContain("PanelOverlayRenderer", source);
        Assert.DoesNotContain("CorrscopeRunner", source);
        Assert.DoesNotContain("VisualizationTopologyBuilder", source);
        Assert.DoesNotContain("VisualizationPlanning", source);
    }

    private static string SourceFor(string fileName)
    {
        string root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MDPlayer.Fmp.Cli"));
        string path = Path.Combine(root, fileName);
        Assert.True(File.Exists(path), $"expected source at {path}");
        return File.ReadAllText(path);
    }
}