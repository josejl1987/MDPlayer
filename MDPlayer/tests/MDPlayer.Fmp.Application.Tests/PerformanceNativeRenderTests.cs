using Fmp.Application.Contracts;
using Fmp.Application.Tests;
using Fmp.Cli;
using Fmp.Core.Audio;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace Fmp.Application.Tests;

/// <summary>
/// Contract for the native Performance composition (formerly MidiTrail): the
/// renderer gate is gone, the rendered master WAV is the authoritative render
/// duration, and the timeline-only preview path aligns to that duration
/// exactly like final export.
/// </summary>
public sealed class PerformanceNativeRenderTests : IDisposable
{
    private const int Rate = 48_000;
    private readonly string _root;

    public PerformanceNativeRenderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"performance-native-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ---- Master WAV authority -------------------------------------------------

    [Fact]
    public void TryResolveMasterWavSamples_ReturnsActualPcmCount()
    {
        string wav = WriteMasterWav("master.wav", Rate, sampleCount: 240_000);
        Assert.Equal(240_000, VisualizationPrepareCoordinator.TryResolveMasterWavSamples(wav, Rate));
    }

    [Fact]
    public void TryResolveMasterWavSamples_RescalesWhenHeaderRateDiffers()
    {
        string wav = WriteMasterWav("resampled.wav", 44_100, sampleCount: 44_100);
        Assert.Equal(48_000, VisualizationPrepareCoordinator.TryResolveMasterWavSamples(wav, Rate));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryResolveMasterWavSamples_NullPathYieldsFallback(string? path)
    {
        Assert.Null(VisualizationPrepareCoordinator.TryResolveMasterWavSamples(path, Rate));
    }

    [Fact]
    public void TryResolveMasterWavSamples_MissingOrGarbageFileYieldsFallback()
    {
        string missing = Path.Combine(_root, "missing.wav");
        string garbage = Path.Combine(_root, "garbage.wav");
        File.WriteAllBytes(garbage, new byte[] { 1, 2, 3, 4 });

        Assert.Null(VisualizationPrepareCoordinator.TryResolveMasterWavSamples(missing, Rate));
        Assert.Null(VisualizationPrepareCoordinator.TryResolveMasterWavSamples(garbage, Rate));
    }

    // ---- Renderer gate removal ------------------------------------------------

    [Fact]
    public void TimelinePreview_ConstructsForPerformance_AndRendersFirstAndLastFrame()
    {
        long wavSamples = 96_000; // 2 s @ 48 kHz
        string master = WriteMasterWav("preview-master.wav", Rate, (int)wavSamples);

        VisualizationRequest request = TestRequests.Valid(
            inputPath: Path.Combine(_root, "input.vgz"),
            outputPath: Path.Combine(_root, "out", "video.mp4"));
        request = request with { Composition = CompositionKind.Performance };

        VisualizationTimeline timeline = MinimalTimeline(endSample: wavSamples);
        VisualizationWorkspace workspace = WorkspaceFor(request, master);
        PreparedTimelineSource source = BuildSource(request, timeline, workspace);

        // Before patch 1 this threw MIDITRAIL_UNAVAILABLE.
        using VisualizationFrameRenderer renderer =
            VisualizationFrameRendererFactory.CreateTimelinePreview(source, introOutro: true);

        // The audio-authoritative frame count: 2 s @ 60 fps = 120 frames.
        Assert.Equal(120, renderer.TotalFrames);
        Assert.NotEmpty(renderer.RenderFrame(0));
        Assert.NotEmpty(renderer.RenderFrame(renderer.TotalFrames - 1));
    }

    [Fact]
    public void TimelinePreview_AlignsEndSampleToMasterWavDuration()
    {
        long wavSamples = 72_000; // 1.5 s — longer than the event span below
        string master = WriteMasterWav("align-master.wav", Rate, (int)wavSamples);

        VisualizationRequest request = TestRequests.Valid(
            inputPath: Path.Combine(_root, "input.vgz"),
            outputPath: Path.Combine(_root, "out", "video.mp4"));
        request = request with { Composition = CompositionKind.Performance };

        // Event span ends at 0.5 s; the WAV plays for 1.5 s. The aligned
        // timeline must end where the AUDIO ends, not where events end.
        VisualizationTimeline timeline = MinimalTimeline();
        Assert.Equal(24_000, timeline.EndSample);

        VisualizationWorkspace workspace = WorkspaceFor(request, master);
        PreparedPlanContext plan = VisualizationPrepareCoordinator.BuildPlanContext(
            timeline, request, workspace);
        var capture = new PreparedTimeline(
            timeline, BackendId: "vgm", MasterAudioPath: master,
            MasterSamples: wavSamples, SampleRate: Rate, MasterAudioProduced: true);

        PreparedTimelineSource source =
            VisualizationPrepareCoordinator.BuildTimelineSourceFrom(
                capture, request, workspace, plan);

        Assert.Equal(wavSamples, source.Timeline.EndSample - source.Timeline.StartSample);
    }

    // ---- Fixtures --------------------------------------------------------------

    private static VisualizationTimeline MinimalTimeline(long endSample = 24_000)
    {
        var voice = new VoiceDescriptor(
            new VoiceId(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Fm, 0),
            "FM1",
            VoicePresentationKind.Pitched,
            Order: 0,
            IsPercussion: false,
            IsNoise: false,
            SupportsPitch: true);
        string voiceId = voice.Id.ToString();
        return new VisualizationTimeline
        {
            SampleRate = Rate,
            StartSample = 0,
            EndSample = endSample,
            Devices = [new DeviceDescriptor(
                voice.Id.Device, voice.Id.Device.ToString(), 1, DeviceCapabilities.Notes)],
            Voices = [voice],
            Notes =
            [
                new NoteEvent(
                    voiceId, 4_800, 12_000, 440.0, 69.0,
                    "instrument", VisualizationNoteMode.Fm, IsRetrigger: false, []),
            ],
            Rhythm = [],
        };
    }

    private VisualizationWorkspace WorkspaceFor(VisualizationRequest request, string masterPath)
    {
        var workspace = new VisualizationWorkspace(
            OutputDir: _root,
            TimelinePath: Path.Combine(_root, "timeline.json"),
            AudioDir: _root,
            MasterAudioPath: masterPath,
            ScopeDir: Path.Combine(_root, "scope"),
            ScopeMetadataPath: Path.Combine(_root, "scope", "metadata.json"),
            CorrscopeConfigPath: Path.Combine(_root, "scope", "corrscope-grid.yaml"),
            VideoPath: request.OutputPath);
        VisualizationJsonWriter.Write(workspace.TimelinePath, MinimalTimeline());
        return workspace;
    }

    private static PreparedTimelineSource BuildSource(
        VisualizationRequest request,
        VisualizationTimeline timeline,
        VisualizationWorkspace workspace)
    {
        PreparedPlanContext plan = VisualizationPrepareCoordinator.BuildPlanContext(
            timeline, request, workspace);
        return new PreparedTimelineSource(
            Request: request,
            Timeline: timeline,
            Layout: plan.Layout,
            Presentation: VisualizationPresentationSupport.ResolvePresentation(
                request, new FileInfo(request.InputPath)),
            Plan: plan.Plan,
            MasterAudioPath: workspace.MasterAudioPath,
            MasterAudioProduced: true,
            BackendId: "vgm",
            TimelinePath: workspace.TimelinePath);
    }

    /// <summary>Writes a real PCM16 stereo WAV via the production writer.</summary>
    private string WriteMasterWav(string name, int sampleRate, int sampleCount)
    {
        string path = Path.Combine(_root, name);
        var writer = new WavWriter(path, sampleRate, channels: 2, bitsPerSample: 16);
        short[] chunk = new short[2 * 1_000]; // stereo interleaved
        for (int written = 0; written < sampleCount; written += 1_000)
        {
            int remaining = Math.Min(1_000, sampleCount - written);
            if (remaining < 1_000)
                Array.Clear(chunk, 0, chunk.Length);
            writer.Write(chunk.AsSpan(0, 2 * remaining));
        }
        // Dispose() discards the temp file by design; only Close() finalizes
        // the RIFF header and publishes the final path.
        writer.Close();
        return path;
    }
}
