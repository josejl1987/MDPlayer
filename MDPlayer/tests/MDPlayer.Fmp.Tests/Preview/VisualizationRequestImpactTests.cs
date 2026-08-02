using Fmp.Application.Contracts;
using Fmp.Application.Preview;
using Fmp.Cli;
using Xunit;

namespace MDPlayer.Fmp.Tests.Preview;

/// <summary>
/// Request-difference classification precedence. A change to an earlier staged
/// key must classify as that single highest stage (not a flag combination), an
/// export-only change as <see cref="PreviewRequestImpact.ExportOnly"/>, and an
/// identical request as <see cref="PreviewRequestImpact.None"/>.
/// </summary>
public sealed class PreviewRequestImpactTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputPath;

    public PreviewRequestImpactTests()
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

    private TimelineCaptureKey TimelineOf(VisualizationRequest request)
    {
        var runtime = new RenderRuntimeOptions();
        return TimelineCaptureKey.From(request, new FileInfo(_inputPath), runtime);
    }

    private static PreviewRequestImpact Classify(
        VisualizationRequest a,
        VisualizationRequest b)
        => VisualizationRequestImpactClassifier.Classify(a, b, new RenderRuntimeOptions());

    [Fact]
    public void IdenticalRequests_AreNone()
    {
        VisualizationRequest request = BaseRequest();
        Assert.Equal(PreviewRequestImpact.None, Classify(request, request));
    }

    [Fact]
    public void SampleRateChange_ReplaysTimelineCapture()
    {
        VisualizationRequest a = BaseRequest();
        VisualizationRequest b = a with { Playback = a.Playback with { SampleRate = 22050 } };
        Assert.Equal(PreviewRequestImpact.TimelineCapture, Classify(a, b));
    }

    [Fact]
    public void SsgGainChange_IsTimelineCapture()
    {
        VisualizationRequest a = BaseRequest();
        VisualizationRequest b = a with { Playback = a.Playback with { SsgGainDb = -9 } };
        Assert.Equal(PreviewRequestImpact.TimelineCapture, Classify(a, b));
    }

    [Fact]
    public void TrackSelectionChange_IsPlanNotTimelineCapture()
    {
        VisualizationRequest a = BaseRequest();
        VisualizationRequest b = a with { Tracks = a.Tracks with { Selection = TrackSelectionMode.Active } };

        PreviewRequestImpact impact = Classify(a, b);
        Assert.NotEqual(PreviewRequestImpact.TimelineCapture, impact);
        Assert.Equal(PreviewRequestImpact.Plan, impact);
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
        Assert.Equal(PreviewRequestImpact.None, Classify(a, b));
    }

    [Fact]
    public void DimensionChange_RebuildsPlan()
    {
        VisualizationRequest a = BaseRequest();
        VisualizationRequest b = a with { Output = a.Output with { Width = 1280, Height = 720 } };
        Assert.Equal(PreviewRequestImpact.Plan, Classify(a, b));
    }

    [Fact]
    public void PastFutureWindowChange_RebuildsPlan()
    {
        VisualizationRequest a = BaseRequest();
        VisualizationRequest b = a with { View = a.View with { PastSeconds = 3.0 } };
        Assert.Equal(PreviewRequestImpact.Plan, Classify(a, b));
    }

    [Fact]
    public void TimeGridChange_RebuildsPlan()
    {
        VisualizationRequest a = BaseRequest();
        VisualizationRequest b = a with { View = a.View with { TimeGrid = TimeGridMode.Authoritative } };
        Assert.Equal(PreviewRequestImpact.Plan, Classify(a, b));
    }

    [Fact]
    public void TitleChange_IsFrameOnly()
    {
        VisualizationRequest a = BaseRequest();
        VisualizationRequest b = a with { Presentation = a.Presentation with { Title = "New" } };
        Assert.Equal(PreviewRequestImpact.Frame, Classify(a, b));
    }

    [Fact]
    public void PaletteChange_IsFrameOnly()
    {
        VisualizationRequest a = BaseRequest();
        VisualizationRequest b = a with { Style = a.Style with { Palette = PaletteKind.Monochrome } };
        Assert.Equal(PreviewRequestImpact.Frame, Classify(a, b));
    }

    [Fact]
    public void OutputPathChange_IsExportOnly()
    {
        VisualizationRequest a = BaseRequest() with { OutputPath = "/a/out.mp4" };
        VisualizationRequest b = a with { OutputPath = "/b/out.mp4" };
        Assert.Equal(PreviewRequestImpact.ExportOnly, Classify(a, b));
    }

    [Fact]
    public void EncoderAndOverwriteChange_IsExportOnly()
    {
        VisualizationRequest a = BaseRequest();
        VisualizationRequest b = a with { Output = a.Output with { Encoder = VideoEncoder.Nvenc, Overwrite = true } };
        Assert.Equal(PreviewRequestImpact.ExportOnly, Classify(a, b));
    }

    // ---- Classification/key corrections for this commit ----

    [Fact]
    public void DuplicateIncludedIds_NormalizeToSameKey()
    {
        VisualizationRequest a = BaseRequest() with
        {
            Tracks = BaseRequest().Tracks with { IncludedIds = ["b", "a", "b"] },
        };
        VisualizationRequest b = BaseRequest() with
        {
            Tracks = BaseRequest().Tracks with { IncludedIds = ["a", "b"] },
        };
        Assert.Equal(PreviewRequestImpact.None, Classify(a, b));
    }

    [Fact]
    public void IncludedExcludedIds_AffectHashEqualityConsistently()
    {
        var timeline = TimelineOf(BaseRequest());

        var a = BaseRequest() with
        {
            Tracks = BaseRequest().Tracks with { IncludedIds = ["a", "b"], ExcludedIds = ["c"] },
        };
        var b = BaseRequest() with
        {
            Tracks = BaseRequest().Tracks with { IncludedIds = ["b", "a"], ExcludedIds = ["c"] },
        };
        var c = BaseRequest() with
        {
            Tracks = BaseRequest().Tracks with { IncludedIds = ["a", "b"], ExcludedIds = ["d"] },
        };

        var ka = ScopeAssetKey.From(a, timeline);
        var kb = ScopeAssetKey.From(b, timeline);
        var kc = ScopeAssetKey.From(c, timeline);

        Assert.True(ka.Equals(kb));
        Assert.Equal(ka.GetHashCode(), kb.GetHashCode());
        Assert.False(ka.Equals(kc));
    }

    [Fact]
    public void MissingFile_CanBeClassifiedWithoutThrowing()
    {
        string missing = Path.Combine(_tempDir, "does-not-exist.vgz");
        VisualizationRequest a = BaseRequest() with
        {
            InputPath = missing,
            Playback = BaseRequest().Playback with { SampleRate = 44100 },
        };
        VisualizationRequest b = BaseRequest() with
        {
            InputPath = missing,
            Playback = BaseRequest().Playback with { SampleRate = 22050 },
        };

        Assert.Equal(PreviewRequestImpact.TimelineCapture, Classify(a, b));
    }
}