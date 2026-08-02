using Fmp.Application.Contracts;
using Fmp.Cli;
using Xunit;

namespace MDPlayer.Fmp.Tests.Preview;

/// <summary>
/// Request-difference classification precedence. A change to an earlier staged
/// key must classify as that stage (and everything derived), an export-only
/// change must classify as <see cref="VisualizationRequestImpact.ExportOnly"/>,
/// and an identical request must classify as <see cref="VisualizationRequestImpact.None"/>.
/// </summary>
public sealed class VisualizationRequestImpactTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputPath;

    public VisualizationRequestImpactTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MDPlayerImpactTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _inputPath = Path.Combine(_tempDir, "input.vgz");
        File.WriteAllText(_inputPath, "fixture-input");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private VisualizationRequest BaseRequest() => new()
    {
        InputPath = _inputPath,
        OutputPath = Path.Combine(_tempDir, "out.mp4"),
        Composition = CompositionKind.Diagnostic,
        Output = new OutputSettings
        {
            Width = 1920,
            Height = 1080,
            FpsNumerator = 60,
            FpsDenominator = 1,
            Quality = RenderQuality.Standard,
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
            NoteColor = NoteColorMode.Channel,
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

    private static VisualizationRequestImpact Classify(
        VisualizationRequest a,
        VisualizationRequest b)
        => VisualizationRequestImpactClassifier.Classify(a, b, new RenderRuntimeOptions());

    [Fact]
    public void IdenticalRequests_AreNone()
    {
        VisualizationRequest request = BaseRequest();
        Assert.Equal(VisualizationRequestImpact.None, Classify(request, request));
    }

    [Fact]
    public void SampleRateChange_ReplaysTimelineCapture()
    {
        VisualizationRequest a = BaseRequest();
        VisualizationRequest b = a with { Playback = a.Playback with { SampleRate = 22050 } };

        VisualizationRequestImpact impact = Classify(a, b);
        Assert.True(impact.Has(VisualizationRequestImpact.TimelineCapture));
        Assert.True(impact.Has(VisualizationRequestImpact.ScopeAssets));
        Assert.True(impact.Has(VisualizationRequestImpact.Plan));
        Assert.True(impact.Has(VisualizationRequestImpact.Frame));
    }

    [Fact]
    public void SsgGainChange_IsTimelineCapture()
    {
        VisualizationRequest a = BaseRequest();
        VisualizationRequest b = a with { Playback = a.Playback with { SsgGainDb = -9 } };
        Assert.True(Classify(a, b).Has(VisualizationRequestImpact.TimelineCapture));
    }

    [Fact]
    public void TrackSelectionChange_RegeneratesScopeAssetsOnly()
    {
        VisualizationRequest a = BaseRequest();
        VisualizationRequest b = a with { Tracks = a.Tracks with { Selection = TrackSelectionMode.Active } };

        VisualizationRequestImpact impact = Classify(a, b);
        Assert.False(impact.Has(VisualizationRequestImpact.TimelineCapture));
        Assert.True(impact.Has(VisualizationRequestImpact.ScopeAssets));
        Assert.True(impact.Has(VisualizationRequestImpact.Plan));
        Assert.True(impact.Has(VisualizationRequestImpact.Frame));
    }

    [Fact]
    public void IncludedIdsOrderChange_HasNoImpact()
    {
        VisualizationRequest a = BaseRequest() with
        {
            Tracks = BaseRequest().Tracks with { IncludedIds = ["b", "a"] },
        };
        VisualizationRequest b = BaseRequest() with
        {
            Tracks = BaseRequest().Tracks with { IncludedIds = ["a", "b"] },
        };
        Assert.Equal(VisualizationRequestImpact.None, Classify(a, b));
    }

    [Fact]
    public void DimensionChange_RebuildsPlan()
    {
        VisualizationRequest a = BaseRequest();
        VisualizationRequest b = a with { Output = a.Output with { Width = 1280, Height = 720 } };

        VisualizationRequestImpact impact = Classify(a, b);
        Assert.False(impact.Has(VisualizationRequestImpact.TimelineCapture));
        Assert.False(impact.Has(VisualizationRequestImpact.ScopeAssets));
        Assert.True(impact.Has(VisualizationRequestImpact.Plan));
        Assert.True(impact.Has(VisualizationRequestImpact.Frame));
    }

    [Fact]
    public void PastFutureWindowChange_RebuildsPlan()
    {
        VisualizationRequest a = BaseRequest();
        VisualizationRequest b = a with { View = a.View with { PastSeconds = 3.0 } };
        Assert.True(Classify(a, b).Has(VisualizationRequestImpact.Plan));
    }

    [Fact]
    public void TimeGridChange_RebuildsPlan()
    {
        VisualizationRequest a = BaseRequest();
        VisualizationRequest b = a with { View = a.View with { TimeGrid = TimeGridMode.Authoritative } };
        Assert.True(Classify(a, b).Has(VisualizationRequestImpact.Plan));
    }

    [Fact]
    public void TitleChange_IsFrameOnly()
    {
        VisualizationRequest a = BaseRequest();
        VisualizationRequest b = a with { Presentation = a.Presentation with { Title = "New" } };

        VisualizationRequestImpact impact = Classify(a, b);
        Assert.Equal(VisualizationRequestImpact.Frame, impact);
    }

    [Fact]
    public void PaletteChange_IsFrameOnly()
    {
        VisualizationRequest a = BaseRequest();
        VisualizationRequest b = a with { Style = a.Style with { Palette = PaletteKind.Monochrome } };
        Assert.Equal(VisualizationRequestImpact.Frame, Classify(a, b));
    }

    [Fact]
    public void OutputPathChange_IsExportOnly()
    {
        VisualizationRequest a = BaseRequest() with { OutputPath = "/a/out.mp4" };
        VisualizationRequest b = a with { OutputPath = "/b/out.mp4" };
        Assert.Equal(VisualizationRequestImpact.ExportOnly, Classify(a, b));
    }

    [Fact]
    public void EncoderAndOverwriteChange_IsExportOnly()
    {
        VisualizationRequest a = BaseRequest();
        VisualizationRequest b = a with { Output = a.Output with { Encoder = VideoEncoder.Nvenc, Overwrite = true } };
        Assert.Equal(VisualizationRequestImpact.ExportOnly, Classify(a, b));
    }
}
