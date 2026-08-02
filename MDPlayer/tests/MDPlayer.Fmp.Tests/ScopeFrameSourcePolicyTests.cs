using Fmp.Application.Contracts;
using Fmp.Cli;
using Fmp.Core.Analysis;
using Fmp.Core.Audio;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;

#nullable enable
namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Verifies the renderer factory selects the correct scope source family for
/// the two <see cref="ScopeFrameSourcePolicy"/> modes: the interactive policy
/// must produce the fully in-process random-access channel source (never
/// Corrscope/Python), while production preserves the master-waveform fallback
/// used when no scope bridge is present.
/// </summary>
public sealed class ScopeFrameSourcePolicyTests
{
    [Fact]
    public void Create_InteractivePolicy_UsesApproximatedInProcessSource()
    {
        (VisualizationTimeline timeline, VisualizationRequest request, ResolvedVisualizationLayout layout,
            string masterWav, ProjectedScopeChannel[] channels) = BuildPrepared();
        try
        {
            var prepared = MakePrepared(timeline, request, layout, masterWav, channels);
            using VisualizationFrameRenderer renderer =
                VisualizationFrameRendererFactory.Create(
                    prepared,
                    VisualizationWorkspace.Create(request),
                    new RenderRuntimeOptions(),
                    introOutro: false,
                    ScopeFrameSourcePolicy.Interactive);

            // No Corrscope / Python / FFmpeg is ever probed on this path; the
            // source is the in-process random-access reader, flagged as
            // approximating production triggering.
            Assert.True(renderer.HasScopeSource);
            Assert.True(renderer.UsesApproximatedScopeSource);
            Assert.Equal(0, renderer.InteractiveScopeUnavailableChannelCount);
        }
        finally
        {
            File.Delete(masterWav);
        }
    }

    [Fact]
    public void Create_ProductionPolicy_WithoutBridge_FallsBackToMasterWaveform()
    {
        (VisualizationTimeline timeline, VisualizationRequest request, ResolvedVisualizationLayout layout,
            string masterWav, ProjectedScopeChannel[] channels) = BuildPrepared();
        try
        {
            var prepared = MakePrepared(timeline, request, layout, masterWav, channels);
            using VisualizationFrameRenderer renderer =
                VisualizationFrameRendererFactory.Create(
                    prepared,
                    VisualizationWorkspace.Create(request),
                    new RenderRuntimeOptions(),
                    introOutro: false,
                    ScopeFrameSourcePolicy.Production);

            // No Corrscope runner/config in this environment: production falls
            // back to the shared internal master-waveform source, which is NOT
            // approximated.
            Assert.True(renderer.HasScopeSource);
            Assert.False(renderer.UsesApproximatedScopeSource);
            Assert.Equal(0, renderer.InteractiveScopeUnavailableChannelCount);

            // Render a frame to prove the fallback draws actual scope content.
            byte[] frame = renderer.RenderFrame(renderer.TotalFrames / 2);
            Assert.NotNull(frame);
            Assert.NotEmpty(frame);
        }
        finally
        {
            File.Delete(masterWav);
        }
    }

    private static VisualizationRequest FixtureRequest()
        => new()
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
            Playback = new PlaybackSettings { SampleRate = 1000 },
            Tracks = new TrackSettings { Selection = TrackSelectionMode.All },
        };

    private static void WriteSineWav(string path)
    {
        using var writer = new WavWriter(path, 1000, channels: 1);
        var samples = new short[1000 * 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(Math.Sin(2 * Math.PI * 330 * i / 1000) * short.MaxValue * 0.8);
        writer.Write(samples);
        writer.Close();
    }

    private static PreparedVisualizationSource MakePrepared(
        VisualizationTimeline timeline,
        VisualizationRequest request,
        ResolvedVisualizationLayout layout,
        string masterWav,
        ProjectedScopeChannel[] channels)
        => new(
            request,
            timeline,
            layout,
            new VisualizationPresentation("T", "S", "C"),
            AnalysisOverlayScene.Empty,
            Energy: Array.Empty<ChannelEnergyEnvelope>(),
            Scope: new VisualizationScopeArtifacts(
                new StemPlan(false, default, default, 0, 0, "test"),
                Result: null,
                Enabled: true,
                HasIsolatedStems: true),
            ScopeChannels: channels,
            Plan: new VisualizationPlanResult
            {
                ResolvedLayout = "diagnostic",
                RequestedLayout = "diagnostic",
                InputPath = request.InputPath,
            },
            MasterAudioPath: masterWav,
            BackendId: "test",
            TimelinePath: "");

    private static (VisualizationTimeline Timeline, VisualizationRequest Request, ResolvedVisualizationLayout Layout, string MasterWav, ProjectedScopeChannel[] Channels) BuildPrepared()
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        VisualizationRequest request = FixtureRequest();
        ResolvedVisualizationLayout layout = RendererTestLayout.Build(
            timeline, request.Output.Width, request.Output.Height);

        string masterWav = Path.Combine(Path.GetTempPath(), $"policy-{Guid.NewGuid():N}.wav");
        WriteSineWav(masterWav);

        var channels = Enumerable.Range(0, layout.Topology.Panels.Count)
            .Select(p => new ProjectedScopeChannel(
                PanelIndex: p, Name: "master", Label: "Master",
                WavPath: masterWav, SemanticClass: ScopeSemanticClass.Mixed,
                WindowWidth: 1, DefaultAmplification: 1.0, DefaultColor: "#7AA4FF"))
            .ToArray();

        return (timeline, request, layout, masterWav, channels);
    }
}
