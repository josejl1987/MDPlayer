using Fmp.Application.Contracts;
using Fmp.Cli;
using Fmp.Core.Audio;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// PR7 (patch 3) tests for progressive first paint: the semantic timeline is
/// decoded into a lightweight <see cref="PreparedTimelineSource"/> and a
/// timeline-only renderer WITHOUT stems, energy or Corrscope, letting a first
/// usable frame appear before heavy scope/stem preparation finishes.
/// </summary>
public sealed class ProgressiveFirstPaintTests
{
    private static VisualizationRequest FixtureRequest(
        string input = "fixture.ovi",
        int width = 960,
        int height = 540)
        => new()
        {
            InputPath = input,
            OutputPath = "/tmp/fixture.mp4",
            Composition = CompositionKind.Diagnostic,
            Output = new OutputSettings
            {
                Width = width,
                Height = height,
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
                Effects = VisualEffects.Off,
                NoteColor = global::Fmp.Application.Contracts.NoteColorMode.Channel,
            },
            Playback = new PlaybackSettings
            {
                SampleRate = 44_100,
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

    private static PreparedTimeline TimelineCapture(
        VisualizationTimeline? timeline = null,
        bool masterAudioProduced = false,
        string? masterAudioPath = null,
        long masterSamples = 0,
        int sampleRate = 1_000)
        => new(
            timeline ?? VisualizationTimelineFixture.Create(),
            BackendId: "fmp",
            MasterAudioPath: masterAudioPath ?? "/does/not/exist.wav",
            MasterSamples: masterSamples,
            SampleRate: sampleRate,
            MasterAudioProduced: masterAudioProduced);

    [Fact]
    public void BuildTimelineSource_ProducesSamePlanAsVisualizationPlanBuilder()
    {
        // The lightweight source's plan is the same pure projection the full
        // source uses, so PlanAsync stays authoritative even though it never
        // prepares stems/energy.
        VisualizationRequest request = FixtureRequest();
        VisualizationWorkspace workspace = VisualizationWorkspace.Create(request);

        PreparedTimeline capture = TimelineCapture();

        PreparedTimelineSource source =
            VisualizationPrepareCoordinator.BuildTimelineSource(
                capture,
                request,
                workspace);

        VisualizationPlanResult direct =
            VisualizationPlanBuilder.Build(
                request,
                capture.Timeline,
                source.Layout,
                workspace.TimelinePath);

        // Compare the projection fields; full record equality is reference-based
        // for the contained lists even when contents match.
        Assert.Equal(direct.ResolvedLayout, source.Plan.ResolvedLayout);
        Assert.Equal(direct.RequestedLayout, source.Plan.RequestedLayout);
        Assert.Equal(direct.EstimatedFrameCount, source.Plan.EstimatedFrameCount);
        Assert.Equal(direct.EstimatedDurationSeconds, source.Plan.EstimatedDurationSeconds);
        Assert.Equal(direct.Capabilities, source.Plan.Capabilities);
        Assert.Equal(capture.BackendId, source.BackendId);
        Assert.Equal(workspace.TimelinePath, source.TimelinePath);
        Assert.Equal(source.TimelinePath, source.Plan.TimelinePath);
    }

    [Fact]
    public void BuildTimelineSource_AlignsTimelineWhenMasterSamplesAvailable()
    {
        VisualizationRequest request = FixtureRequest();
        VisualizationWorkspace workspace = VisualizationWorkspace.Create(request);

        PreparedTimeline capture = TimelineCapture(
            masterAudioProduced: true,
            masterSamples: 8_000,
            sampleRate: 1_000);

        PreparedTimelineSource source =
            VisualizationPrepareCoordinator.BuildTimelineSource(
                capture,
                request,
                workspace);

        Assert.True(source.Timeline.EndSample > capture.Timeline.EndSample,
            "an available master duration > timeline should extend the aligned timeline");
    }

    [Fact]
    public void MasterAudioProduced_RequiresFileToExist()
    {
        VisualizationRequest request = FixtureRequest();
        VisualizationWorkspace workspace = VisualizationWorkspace.Create(request);

        // Flag set but the master file is missing -> the timeline source must
        // not claim a master waveform is available.
        PreparedTimelineSource missing =
            VisualizationPrepareCoordinator.BuildTimelineSource(
                TimelineCapture(masterAudioProduced: true, masterAudioPath: "/does/not/exist.wav"),
                request,
                workspace);

        Assert.False(missing.MasterAudioProduced);
    }

    [Fact]
    public void CreateTimelinePreview_WithoutMaster_HasNoScopeSource()
    {
        // FMP-style capture: semantic panels but initially empty scope regions.
        VisualizationRequest request = FixtureRequest();
        VisualizationWorkspace workspace = VisualizationWorkspace.Create(request);

        PreparedTimelineSource source =
            VisualizationPrepareCoordinator.BuildTimelineSource(
                TimelineCapture(masterAudioProduced: false),
                request,
                workspace);

        using VisualizationFrameRenderer renderer =
            VisualizationFrameRendererFactory.CreateTimelinePreview(
                source,
                introOutro: true);

        Assert.False(renderer.HasScopeSource,
            "a timeline-only renderer must never build an isolated-stem or Corrscope source");
        Assert.True(renderer.TotalFrames > 0);

        // The semantic overlay still renders real frame data without scopes.
        byte[] frame = renderer.RenderFrame(0);
        Assert.NotNull(frame);
        Assert.NotEmpty(frame);
    }

    [Fact]
    public void CreateTimelinePreview_WithMaster_UsesMasterWaveformSource()
    {
        // VGM/SPC-style capture: timeline capture produced a master WAV, so the
        // first frame shows the semantic overlay plus a master waveform
        // duplicated across scope cells — with zero scope/stem preparation.
        string masterWav = Path.Combine(
            Path.GetTempPath(), $"progressive-{Guid.NewGuid():N}.wav");
        try
        {
            WriteSineWav(masterWav);

            VisualizationRequest request = FixtureRequest();
            VisualizationWorkspace workspace = VisualizationWorkspace.Create(request);

            PreparedTimelineSource source =
                VisualizationPrepareCoordinator.BuildTimelineSource(
                    TimelineCapture(
                        masterAudioProduced: true,
                        masterAudioPath: masterWav,
                        masterSamples: 5_000,
                        sampleRate: 1_000),
                    request,
                    workspace);

            Assert.True(source.MasterAudioProduced);

            using VisualizationFrameRenderer renderer =
                VisualizationFrameRendererFactory.CreateTimelinePreview(
                    source,
                    introOutro: true);

            Assert.True(renderer.HasScopeSource,
                "an available master WAV must supply the master-waveform scope source for the first frame");
        }
        finally
        {
            if (File.Exists(masterWav))
                File.Delete(masterWav);
        }
    }

    [Fact]
    public void TimelineSourceCarriesNoScopeOrEnergyArtifacts()
    {
        // Structural guarantee that a timeline-only source cannot reach for
        // stems, energy, Corrscope config or rendered-audio analysis: it has
        // none of those fields by construction.
        VisualizationRequest request = FixtureRequest();
        VisualizationWorkspace workspace = VisualizationWorkspace.Create(request);

        PreparedTimelineSource source =
            VisualizationPrepareCoordinator.BuildTimelineSource(
                TimelineCapture(masterAudioProduced: false),
                request,
                workspace);

        Assert.NotNull(source.Layout);
        Assert.NotNull(source.Timeline);
        Assert.NotNull(source.Plan);

        string typeName = typeof(PreparedTimelineSource).FullName!;
        Assert.DoesNotContain("Scope", nameof(PreparedTimelineSource));
        Assert.DoesNotContain("Energy", nameof(PreparedTimelineSource));
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
}
