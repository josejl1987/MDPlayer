using Fmp.Application.Contracts;
using Fmp.Cli;
using Xunit;

namespace MDPlayer.Fmp.Tests.Preview;

/// <summary>
/// Isolation of the four staged preview keys. A change to an earlier stage must
/// change that stage's key, while a change to a later or unrelated stage must
/// not disturb it. ID ordering for track selection must not change a key.
/// </summary>
public sealed class VisualizationPreviewKeyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputPath;

    public VisualizationPreviewKeyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MDPlayerKeyTests", Guid.NewGuid().ToString("N"));
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

    // ---- TimelineCaptureKey changes ----

    [Theory]
    [InlineData("loop-count")]
    [InlineData("fade")]
    [InlineData("tail")]
    [InlineData("maximum-duration")]
    [InlineData("sample-rate")]
    [InlineData("ssg-gain")]
    [InlineData("spc-pitch")]
    public void TimelineCaptureKey_Changes(string change)
    {
        VisualizationRequest request = BaseRequest();
        VisualizationRequest other = change switch
        {
            "loop-count" => request with { Playback = request.Playback with { LoopCount = 3 } },
            "fade" => request with { Playback = request.Playback with { FadeSeconds = 2.0 } },
            "tail" => request with { Playback = request.Playback with { TailSeconds = 1.0 } },
            "maximum-duration" => request with { Playback = request.Playback with { MaximumDurationSeconds = 60 } },
            "sample-rate" => request with { Playback = request.Playback with { SampleRate = 22050 } },
            "ssg-gain" => request with { Playback = request.Playback with { SsgGainDb = -6 } },
            "spc-pitch" => request with { Playback = request.Playback with { SpcPitch = SpcPitchInterpretation.Relative } },
            _ => request,
        };

        Assert.NotEqual(TimelineOf(request), TimelineOf(other));
    }

    [Fact]
    public void TimelineCaptureKey_InputLengthChange_ChangesKey()
    {
        TimelineCaptureKey before = TimelineOf(BaseRequest());
        File.WriteAllText(_inputPath, "a-different-length");
        TimelineCaptureKey after = TimelineOf(BaseRequest());
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void TimelineCaptureKey_InputLastWriteChange_ChangesKey()
    {
        TimelineCaptureKey before = TimelineOf(BaseRequest());
        File.SetLastWriteTimeUtc(_inputPath, DateTime.UtcNow.AddSeconds(5));
        TimelineCaptureKey after = TimelineOf(BaseRequest());
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void TimelineCaptureKey_IgnoresStyleTitleAndDimensions()
    {
        VisualizationRequest request = BaseRequest();
        VisualizationRequest other = request with
        {
            Style = request.Style with { Palette = PaletteKind.Monochrome },
            Presentation = request.Presentation with { Title = "Changed" },
            Output = request.Output with { Width = 960, Height = 540 },
        };

        Assert.Equal(TimelineOf(request), TimelineOf(other));
    }

    // ---- ScopeAssetKey changes ----

    [Theory]
    [InlineData("selection")]
    [InlineData("included")]
    [InlineData("excluded")]
    [InlineData("include-inactive")]
    public void ScopeAssetKey_ChangesButTimelineDoesNot(string change)
    {
        VisualizationRequest request = BaseRequest();
        VisualizationRequest other = change switch
        {
            "selection" => request with { Tracks = request.Tracks with { Selection = TrackSelectionMode.Active } },
            "included" => request with { Tracks = request.Tracks with { IncludedIds = ["ch1", "ch2"] } },
            "excluded" => request with { Tracks = request.Tracks with { ExcludedIds = ["ch3"] } },
            "include-inactive" => request with { Tracks = request.Tracks with { IncludeInactiveDiagnosticTracks = true } },
            _ => request,
        };

        Assert.Equal(TimelineOf(request), TimelineOf(other));
        Assert.NotEqual(ScopeAssetKey.From(request, TimelineOf(request)), ScopeAssetKey.From(other, TimelineOf(other)));
    }

    [Fact]
    public void ScopeAssetKey_IdOrderingDoesNotChangeTheKey()
    {
        VisualizationRequest a = BaseRequest() with
        {
            Tracks = BaseRequest().Tracks with { IncludedIds = ["b", "a", "c"] },
        };
        VisualizationRequest b = BaseRequest() with
        {
            Tracks = BaseRequest().Tracks with { IncludedIds = ["a", "c", "b"] },
        };

        var timeline = TimelineOf(a);
        Assert.Equal(ScopeAssetKey.From(a, timeline), ScopeAssetKey.From(b, timeline));
    }

    [Fact]
    public void ScopeAssetKey_IgnoresTitleAndStyle()
    {
        VisualizationRequest request = BaseRequest();
        VisualizationRequest other = request with
        {
            Style = request.Style with { Palette = PaletteKind.Monochrome },
            Presentation = request.Presentation with { Title = "Changed" },
        };

        Assert.Equal(ScopeAssetKey.From(request, TimelineOf(request)), ScopeAssetKey.From(other, TimelineOf(other)));
    }

    // ---- PlanKey changes ----

    [Theory]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("fps-num")]
    [InlineData("fps-denom")]
    [InlineData("past-seconds")]
    [InlineData("future-seconds")]
    [InlineData("time-grid")]
    [InlineData("structure-mode")]
    public void PlanKey_ChangesButNotTimelineOrScope(string change)
    {
        VisualizationRequest request = BaseRequest();
        VisualizationRequest other = change switch
        {
            "width" => request with { Output = request.Output with { Width = 1280 } },
            "height" => request with { Output = request.Output with { Height = 720 } },
            "fps-num" => request with { Output = request.Output with { FpsNumerator = 30 } },
            "fps-denom" => request with { Output = request.Output with { FpsDenominator = 2 } },
            "past-seconds" => request with { View = request.View with { PastSeconds = 2.0 } },
            "future-seconds" => request with { View = request.View with { FutureSeconds = 4.0 } },
            "time-grid" => request with { View = request.View with { TimeGrid = TimeGridMode.Authoritative } },
            "structure-mode" => request with { View = request.View with { Structure = StructureOverlayMode.Off } },
            _ => request,
        };

        var timeline = TimelineOf(request);
        Assert.Equal(timeline, TimelineOf(other));
        Assert.Equal(ScopeAssetKey.From(request, timeline), ScopeAssetKey.From(other, timeline));
        Assert.NotEqual(PlanKey.From(request, ScopeAssetKey.From(request, timeline)), PlanKey.From(other, ScopeAssetKey.From(other, timeline)));
    }

    [Fact]
    public void PlanKey_IgnoresStyleAndTitle()
    {
        VisualizationRequest request = BaseRequest();
        VisualizationRequest other = request with
        {
            Style = request.Style with { Palette = PaletteKind.Monochrome },
            Presentation = request.Presentation with { Title = "Changed" },
        };

        Assert.Equal(
            PlanKey.From(request, ScopeAssetKey.From(request, TimelineOf(request))),
            PlanKey.From(other, ScopeAssetKey.From(other, TimelineOf(other))));
    }

    // ---- FrameStyleKey changes ----

    [Theory]
    [InlineData("quality")]
    [InlineData("effects")]
    [InlineData("note-color")]
    [InlineData("palette")]
    [InlineData("title")]
    [InlineData("subtitle")]
    [InlineData("credits")]
    [InlineData("font")]
    public void FrameStyleKey_ChangesButNotEarlierKeys(string change)
    {
        VisualizationRequest request = BaseRequest();
        VisualizationRequest other = change switch
        {
            "quality" => request with { Output = request.Output with { Quality = RenderQuality.Draft } },
            "effects" => request with { Style = request.Style with { Effects = VisualEffects.Off } },
            "note-color" => request with { Style = request.Style with { NoteColor = NoteColorMode.PitchClass } },
            "palette" => request with { Style = request.Style with { Palette = PaletteKind.Monochrome } },
            "title" => request with { Presentation = request.Presentation with { Title = "New Title" } },
            "subtitle" => request with { Presentation = request.Presentation with { Subtitle = "A subtitle" } },
            "credits" => request with { Presentation = request.Presentation with { Credits = "Credits" } },
            "font" => request with { Presentation = request.Presentation with { FontPath = "/fonts/x.ttf" } },
            _ => request,
        };

        var timeline = TimelineOf(request);
        var scope = ScopeAssetKey.From(request, timeline);
        var plan = PlanKey.From(request, scope);

        Assert.Equal(timeline, TimelineOf(other));
        Assert.Equal(scope, ScopeAssetKey.From(other, timeline));
        Assert.Equal(plan, PlanKey.From(other, ScopeAssetKey.From(other, timeline)));
        Assert.NotEqual(FrameStyleKey.From(request, plan), FrameStyleKey.From(other, PlanKey.From(other, ScopeAssetKey.From(other, timeline))));
    }

    [Fact]
    public void FrameStyleKey_IgnoresOutputPathEncoderAndOverwrite()
    {
        VisualizationRequest request = BaseRequest() with { OutputPath = "/a/out.mp4" };
        VisualizationRequest other = request with
        {
            Output = request.Output with { Encoder = VideoEncoder.Nvenc, Overwrite = true },
        };

        var timeline = TimelineOf(request);
        var scope = ScopeAssetKey.From(request, timeline);
        var plan = PlanKey.From(request, scope);

        Assert.Equal(
            FrameStyleKey.From(request, plan),
            FrameStyleKey.From(other, PlanKey.From(other, ScopeAssetKey.From(other, timeline))));
    }
}
