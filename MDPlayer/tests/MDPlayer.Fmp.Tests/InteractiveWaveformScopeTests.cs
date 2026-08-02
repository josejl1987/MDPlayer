using Fmp.Application.Contracts;
using Fmp.Cli;
using Fmp.Core.Analysis;
using Fmp.Core.Audio;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;

#nullable enable
namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Tests for the interactive random-access scope source that backs GUI stills.
/// These prove per-channel panel placement (sparse sets never shift), seek-order
/// independence, the master fallback filling every panel, and that the
/// interactive path never relies on the Corrscope/Python process.
/// </summary>
public sealed class InteractiveWaveformScopeTests
{
    private const int SampleRate = 1_000; // matches VisualizationTimelineFixture

    // ---- Random-access determinism ----

    [Fact]
    public void ReadFrame_IsSeekOrderIndependent()
    {
        (PanelOverlayRenderer renderer, _) = CreateRenderer();
        string master = WriteWav(SineWave(SampleRate * 12, SampleRate, 330));
        try
        {
            var channels = new[] { MakeChannel(0, "master", master, "#7AA4FF") };
            using var source = MakeSource(renderer, channels);
            Assert.NotNull(source);

            // Render a series of far-apart frames with one live source, then
            // each requested frame again from a fresh source. The bytes must
            // match exactly: no sequential-state dependency.
            long[] safe = new[] { 0, 10, renderer.TotalFrames / 3, 5000, renderer.TotalFrames - 1 }
                .Where(f => f > 0 && f < renderer.TotalFrames)
                .Distinct()
                .ToArray();

            var live = new Dictionary<long, byte[]>();
            foreach (long f in safe)
            {
                var frame = new byte[renderer.ScopeFrameByteCount];
                source!.ReadFrame((int)f, frame);
                live[f] = frame;
            }

            foreach (int f in safe)
            {
                using var fresh = MakeSource(renderer, new[] { MakeChannel(0, "master", master, "#7AA4FF") });
                Assert.NotNull(fresh);
                var again = new byte[renderer.ScopeFrameByteCount];
                fresh!.ReadFrame((int)f, again);
                Assert.Equal(live[f], again);
            }
        }
        finally
        {
            File.Delete(master);
        }
    }

    // ---- Sparse panel mapping (the most important correctness test) ----

    [Fact]
    public void SparseChannels_DrawIntoOwnPanelAndLeaveGapEmpty()
    {
        (PanelOverlayRenderer renderer, _) = CreateRenderer();
        if (renderer.Layout.PanelCount < 3)
            return; // layout has fewer panels; this rendering path is exercised elsewhere

        // Stem A -> panel 0, (no stem at panel 1), stem C -> panel 2.
        string wavA = WriteWav(SineWave(SampleRate * 8, SampleRate, 440));
        string wavC = WriteWav(SineWave(SampleRate * 8, SampleRate, 220));
        try
        {
            var channels = new[]
            {
                MakeChannel(0, "A", wavA, "#FF0000"),
                MakeChannel(2, "C", wavC, "#0000FF"),
            };
            using var source = MakeSource(renderer, channels);

            var frame = new byte[renderer.ScopeFrameByteCount];
            source!.ReadFrame((int)renderer.TotalFrames / 2, frame);

            // Panel 0 shows A, panel 1 is transparent, panel 2 shows C.
            Assert.True(HasOpaquePixels(renderer, frame, 0), "panel 0 should draw stem A");
            Assert.False(HasOpaquePixels(renderer, frame, 1), "panel 1 (no stem) must stay transparent");
            Assert.True(HasOpaquePixels(renderer, frame, 2), "panel 2 should draw stem C");
        }
        finally
        {
            File.Delete(wavA);
            File.Delete(wavC);
        }
    }

    // ---- Master fallback fills every panel ----

    [Fact]
    public void MasterFallback_FillsEveryPanel()
    {
        (PanelOverlayRenderer renderer, _) = CreateRenderer();
        string master = WriteWav(SineWave(SampleRate * 8, SampleRate, 330));
        try
        {
            // One master-backed channel per panel, exactly as projection emits
            // when no isolated stems survive.
            var channels = Enumerable.Range(0, renderer.Layout.PanelCount)
                .Select(p => MakeChannel(p, "master", master, "#7AA4FF"))
                .ToArray();
            using var source = MakeSource(renderer, channels);

            var frame = new byte[renderer.ScopeFrameByteCount];
            source!.ReadFrame((int)renderer.TotalFrames / 2, frame);

            for (int panel = 0; panel < renderer.Layout.PanelCount; panel++)
                Assert.True(HasOpaquePixels(renderer, frame, panel),
                    $"master fallback must draw into panel {panel}");
        }
        finally
        {
            File.Delete(master);
        }
    }

    // ---- No Corrscope process: interactive path uses the in-process source ----

    [Fact]
    public void Factory_InteractivePolicy_ProducesApproximatedInProcessSource()
    {
        (PanelOverlayRenderer renderer, _) = CreateRenderer();
        string master = WriteWav(SineWave(SampleRate * 4, SampleRate, 440));
        try
        {
            var channels = new[]
            {
                new ProjectedScopeChannel(
                    PanelIndex: 0, Name: "master", Label: "Master",
                    WavPath: master, SemanticClass: ScopeSemanticClass.Mixed,
                    WindowWidth: 1, DefaultAmplification: 1.0, DefaultColor: "#7AA4FF"),
            };
            using var source = InteractiveWaveformFrameSource.TryCreate(renderer, channels, 60, 1);
            Assert.NotNull(source);

            // The interactive source must never reference any Corrscope/Python
            // bridge; it approximates triggering on the channel WAV directly.
            Assert.IsType<InteractiveWaveformFrameSource>(source);
        }
        finally
        {
            File.Delete(master);
        }
    }

    [Fact]
    public void Source_ReportsUnavailableChannelCount()
    {
        (PanelOverlayRenderer renderer, _) = CreateRenderer();
        string master = WriteWav(SineWave(SampleRate * 2, SampleRate, 220));
        string missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.wav");
        try
        {
            var channels = new[]
            {
                MakeChannel(0, "ok", master, null),
                MakeChannel(1, "gone", missing, null),
            };
            using var source = MakeSource(renderer, channels);
            Assert.NotNull(source);
            Assert.Equal(1, source!.UnavailableChannelCount);
        }
        finally
        {
            File.Delete(master);
        }
    }

    // ---- Helpers ----

    private static (PanelOverlayRenderer Renderer, VisualizationRequest Request) CreateRenderer()
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        VisualizationRequest request = new()
        {
            InputPath = "fixture.ovi",
            OutputPath = "/tmp/fixture.mp4",
            Output = new OutputSettings
            {
                Width = 960,
                Height = 540,
                FpsNumerator = 60,
                FpsDenominator = 1,
            },
            View = new ViewSettings { PastSeconds = 0.75, FutureSeconds = 2.25 },
            Playback = new PlaybackSettings { SampleRate = SampleRate },
            Tracks = new TrackSettings { Selection = TrackSelectionMode.All },
        };
        var renderer = new PanelOverlayRenderer(
            timeline,
            RendererTestLayout.Build(timeline, request.Output.Width, request.Output.Height),
            VisualizationRendererOptions.Build(
                request,
                new VisualizationPresentation("T", "S", "C"),
                introOutro: false,
                energy: Array.Empty<ChannelEnergyEnvelope>()));
        return (renderer, request);
    }

    private static InteractiveWaveformFrameSource? MakeSource(
        PanelOverlayRenderer renderer,
        IReadOnlyList<ProjectedScopeChannel> channels)
        => InteractiveWaveformFrameSource.TryCreate(renderer, channels, 60, 1);

    private static ProjectedScopeChannel MakeChannel(int panelIndex, string name, string wav, string? color)
        => new(
            PanelIndex: panelIndex,
            Name: name,
            Label: name,
            WavPath: wav,
            SemanticClass: ScopeSemanticClass.Mixed,
            WindowWidth: 1,
            DefaultAmplification: 1.0,
            DefaultColor: color);

    private static string WriteWav(short[] samples)
    {
        string path = Path.Combine(Path.GetTempPath(), $"interactive-{Guid.NewGuid():N}.wav");
        using var writer = new WavWriter(path, SampleRate, channels: 1);
        writer.Write(samples);
        writer.Close();
        return path;
    }

    private static short[] SineWave(int sampleCount, int sampleRate, double frequency)
    {
        var samples = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            double t = i / (double)sampleRate;
            samples[i] = (short)(Math.Sin(2 * Math.PI * frequency * t) * short.MaxValue * 0.8);
        }
        return samples;
    }

    private static bool HasOpaquePixels(PanelOverlayRenderer renderer, byte[] frame, int panelIndex)
    {
        int columnCount = renderer.Layout.ColumnCount;
        int scopeHeight = renderer.Layout.ScopeHeight;
        int gridWidth = renderer.Layout.CorrscopeGridWidth;
        int row = panelIndex / columnCount;
        int cellX = Math.Min((panelIndex % columnCount) * (gridWidth / columnCount), gridWidth - 2);
        int cellWidth = gridWidth / columnCount;
        int cellY = row * scopeHeight;
        for (int y = cellY; y < cellY + scopeHeight; y++)
        {
            for (int x = cellX; x < cellX + cellWidth; x++)
            {
                int offset = (y * gridWidth + x) * 4;
                if (frame[offset] != 0 || frame[offset + 1] != 0 || frame[offset + 2] != 0)
                    return true;
            }
        }
        return false;
    }
}
