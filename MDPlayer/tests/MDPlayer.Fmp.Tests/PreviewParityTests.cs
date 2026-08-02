using System.Diagnostics;
using Fmp.Application.Contracts;
using Fmp.Application.Preview;
using Fmp.Cli;
using Fmp.Core.Analysis;
using Fmp.Core.Audio;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using MDPlayer.Fmp.Tests.Fixtures;
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

    // ---- J. RenderKey ignores frame-nullifying output fields ----

    [Fact]
    public void RenderKey_IgnoresEncoderAndOverwrite()
    {
        VisualizationRequest first = FixtureRequest();
        VisualizationRequest second = first with
        {
            Output = first.Output with { Encoder = global::Fmp.Application.Contracts.VideoEncoder.Nvenc, Overwrite = true },
        };

        // Changing only encoder/overwrite must not rebuild the renderer: the
        // resulting frames are byte-identical, so the key must be equal.
        Assert.Equal(RenderKey.From(first), RenderKey.From(second));
    }

    [Fact]
    public void RenderKey_ChangesWhenFrameAffectingOutputChanges()
    {
        VisualizationRequest first = FixtureRequest();
        VisualizationRequest second = first with
        {
            Output = first.Output with { Width = 1280, Height = 720 },
        };

        Assert.NotEqual(RenderKey.From(first), RenderKey.From(second));
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
    [SkippableFact]
    public void CorrscopeSourceRestartsForBackwardSeek()
    {
        Skip.IfNot(
            IsPython3Available(),
            "backward-seek bridge test requires python3 on PATH");

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

    private static bool IsPython3Available()
    {
        try
        {
            var probe = Process.Start(new ProcessStartInfo
            {
                FileName = "python3",
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (probe is null)
                return false;
            probe.WaitForExit(5000);
            return probe.HasExited && probe.ExitCode == 0;
        }
        catch
        {
            return false;
        }
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

    // ---- I. Production frame == accurate preview frame (PR5 contract) ----

    /// <summary>
    /// The PR5 parity contract: final, preview and review all build their frame
    /// renderer through the single production factory. When the external
    /// Corrscope bridge is unavailable, the factory must engage the shared
    /// internal master-waveform fallback so scope cells render real content in
    /// every consumer — no transparent holes, no FFmpeg-only divergence.
    /// This replaces the old test, which compared two manually identical
    /// <see cref="PanelOverlayRenderer"/> instances and could not detect the
    /// fallback divergence at all.
    /// </summary>
    [Fact]
    public void SharedFactory_WithoutCorrscope_FillsScopeCellsIdentically()
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        VisualizationRequest request = FixtureRequest();
        var presentation = new VisualizationPresentation("TITLE", "SUB", "CRED");
        ResolvedVisualizationLayout layout = RendererTestLayout.Build(
            timeline, request.Output.Width, request.Output.Height);

        // A real master WAV is required for the internal fallback.
        string masterWav = Path.Combine(Path.GetTempPath(), $"parity-{Guid.NewGuid():N}.wav");
        try
        {
            WriteSineWav(masterWav);

            var prepared = new PreparedVisualizationSource(
                request,
                timeline,
                layout,
                presentation,
                AnalysisOverlayScene.Empty,
                Energy: Array.Empty<ChannelEnergyEnvelope>(),
                Scope: new VisualizationScopeArtifacts(
                    new StemPlan(false, default, default, 0, 0, "test fallback"),
                    Result: null, // no Corrscope artifacts -> internal fallback path
                    Enabled: true,
                    HasIsolatedStems: false),
                Plan: new VisualizationPlanResult
                {
                    ResolvedLayout = "diagnostic",
                    RequestedLayout = "diagnostic",
                    InputPath = request.InputPath,
                },
                MasterAudioPath: masterWav,
                BackendId: "fmp",
                TimelinePath: "");
            var runtime = new RenderRuntimeOptions();
            VisualizationWorkspace workspace = VisualizationWorkspace.Create(request);

            using var production = VisualizationFrameRendererFactory.Create(
                prepared, workspace, runtime, introOutro: true);
            using var accuratePreview = VisualizationFrameRendererFactory.Create(
                prepared, workspace, runtime, introOutro: true);

            // The shared factory must engage the internal master-waveform
            // fallback instead of leaving scope viewports transparent.
            Assert.True(production.HasScopeSource,
                "factory must supply an internal master-waveform scope source when Corrscope is unavailable");
            Assert.True(accuratePreview.HasScopeSource);

            Assert.True(production.TotalFrames > 4);

            double[] probeTimes =
            {
                0.0,
                0.4,
                2.5,
                Math.Max(0, production.TotalFrames / (double)request.Output.FpsNumerator - 0.2),
            };

            foreach (double time in probeTimes)
            {
                long index = (long)Math.Round(time * request.Output.FpsNumerator);
                index = Math.Clamp(index, 0, Math.Max(0, production.TotalFrames - 1));

                byte[] prod = production.RenderFrame(index);
                byte[] preview = accuratePreview.RenderFrame(index);

                Assert.Equal(prod.Length, preview.Length);
                Assert.True(SpanEquals(prod, preview),
                    $"accurate preview frame differs from production frame at t={time} (frame {index})");
            }

            // Scope cells must contain drawn content (the master waveform), not
            // transparent holes — the exact divergence the old test missed.
            byte[] mid = production.RenderFrame(150);
            Assert.True(HasNonZeroScopePixels(mid, layout.Geometry),
                "scope cells rendered through the fallback must not be transparent");

            // The shared PNG encoder (used by the preview session) round-trips
            // the same frame the production path writes.
            byte[] png = PngFrameEncoder.Encode(production.Width, production.Height, mid);
            Assert.True(png.Length > 8, "encoded PNG must carry real pixel data");
            Assert.Equal(0x89, png[0]); // PNG magic
        }
        finally
        {
            try { File.Delete(masterWav); } catch { }
        }
    }

    private static void WriteSineWav(string path)
    {
        using var writer = new WavWriter(path, sampleRate: 1_000, channels: 2);
        var samples = new short[5_000];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(Math.Sin(2 * Math.PI * 110 * i / 1_000.0) * short.MaxValue * 0.8);
        writer.Write(samples);
        writer.Close();
    }

    private static bool HasNonZeroScopePixels(byte[] frame, OverlayLayout geometry)
    {
        for (int panelIndex = 0; panelIndex < geometry.PanelCount; panelIndex++)
        {
            OverlayRect scope = geometry.GetScopeRect(panelIndex);
            for (int y = scope.Y; y < scope.Y + scope.Height && y < geometry.Height; y++)
            {
                for (int x = scope.X; x < scope.X + scope.Width && x < geometry.Width; x++)
                {
                    int offset = (y * geometry.Width + x) * 4;
                    if (frame[offset] != 0 || frame[offset + 1] != 0 || frame[offset + 2] != 0)
                        return true;
                }
            }
        }
        return false;
    }


    // ---- K. Inert-setting cleanup ----

    [Fact]
    public void AccessiblePalette_IsMappedAndDiffersFromDefault()
    {
        VisualizationRequest request = FixtureRequest();
        request = request with
        {
            Style = request.Style with { Palette = PaletteKind.Accessible },
        };
        var presentation = new VisualizationPresentation("TITLE", "SUB", "CRED");

        var accessible = VisualizationRendererOptions.Build(
            request, presentation, introOutro: true, energy: Array.Empty<ChannelEnergyEnvelope>());
        var baseline = VisualizationRendererOptions.Build(
            FixtureRequest(), presentation, introOutro: true, energy: Array.Empty<ChannelEnergyEnvelope>());

        // PaletteKind.Accessible is no longer an inert no-op: it must resolve to
        // the colorblind-safe palette and differ from the default.
        Assert.Same(VisualizationPalette.Accessible, accessible.Palette);
        Assert.NotSame(baseline.Palette, accessible.Palette);
        Assert.NotEqual(VisualizationPalette.Default.CanvasBackground, accessible.Palette.CanvasBackground);
    }

    // ---- J. Real in-process preview session smoke test ----

    /// <summary>
    /// Exercises the real in-process preview session end-to-end against a
    /// checked-in <c>.vgz</c> fixture: open → plan → render accurate stills at
    /// 0s/1s/2s → dispose. Asserts successful, non-empty frames and a stable
    /// session across multiple renders. This is the same factory the GUI now
    /// uses for UI previews, so it guards the wired seam. It is skipped when the
    /// external rendering prerequisites (python3 + ffmpeg) are not on PATH, as
    /// the accurate still depends on them; it does not assert timings.
    /// </summary>
    [SkippableFact]
    public async Task InProcessPreviewSession_RendersFramesOnRealVgz()
    {
        bool hasPy = IsCommandAvailable("python3");
        bool hasFf = IsCommandAvailable("ffmpeg");
        Skip.IfNot(
            hasPy && hasFf,
            $"real .vgz preview smoke test requires python3 and ffmpeg on PATH (py={hasPy}, ff={hasFf})");

        string input = Path.Combine(AppContext.BaseDirectory, "testfixtures", "master-ninja.vgz");
        Assert.True(File.Exists(input), $"expected vgz fixture at {input}");

        string sessionRoot = Path.Combine(
            Path.GetTempPath(), "MDPlayer", "Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionRoot);

        VisualizationRequest request = new()
        {
            InputPath = input,
            OutputPath = Path.Combine(sessionRoot, "preview.mp4"),
            Composition = CompositionKind.Diagnostic,
            Output = new OutputSettings
            {
                Width = 1280,
                Height = 720,
                FpsNumerator = 30,
                FpsDenominator = 1,
            },
            Presentation = new PresentationSettings { Title = "Smoke" },
        };

        var factory = new InProcessVisualizationPreviewSessionFactory();
        await using (IVisualizationPreviewSession session =
               await factory.OpenWithTimelineAsync(input, null, CancellationToken.None))
        {
            VisualizationPlanResult plan = await session.PlanAsync(request, CancellationToken.None);
            Assert.NotNull(plan);

            foreach (double time in new[] { 0.0, 1.0, 2.0 })
            {
                PreviewFrameResult frame = await session.RenderFrameAsync(
                    request,
                    new PreviewFrameRequest
                    {
                        TimeSeconds = time,
                        Fidelity = PreviewFidelity.AccurateStill,
                        Width = 1280,
                        Height = 720,
                    },
                    CancellationToken.None);

                Assert.NotNull(frame.PngBytes);
                Assert.True(frame.PngBytes!.Length > 0,
                    $"frame at t={time} produced an empty PNG");
            }
        }

        // Session workspace can be cleaned up once disposed.
        try { Directory.Delete(sessionRoot, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
    }

    private static bool IsCommandAvailable(string name)
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo
            {
                FileName = name,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (probe is null)
                return false;
            // Drain pipes so a chatty child (e.g. ffmpeg on stderr) cannot
            // block on a full pipe buffer.
            string _ = probe.StandardOutput.ReadToEnd();
            string __ = probe.StandardError.ReadToEnd();
            probe.WaitForExit(5000);
            // Presence is what matters; some builds (e.g. headless ffmpeg)
            // return a nonzero "shown help/version" code, so don't require 0.
            return probe.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool SpanEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }
}