using Fmp.Application.Contracts;
using Fmp.Application.Validation;
using Xunit;

namespace Fmp.Application.Tests;

public class ValidationTests
{
    private static VisualizationRequest ValidRequest() => TestRequests.Valid();

    [Fact]
    public void ValidRequest_HasNoErrorIssues()
    {
        IReadOnlyList<ValidationIssue> issues = VisualizationRequestValidator.Validate(ValidRequest());
        Assert.Empty(issues.Where(issue => issue.Severity == ValidationSeverity.Error));
    }

    [Theory]
    [InlineData(ValidationCodes.InputNotFound, "InputPath")]
    [InlineData(ValidationCodes.InvalidRequest, "OutputPath")]
    public void MissingPaths_ProduceStableCodes(string code, string setting)
    {
        VisualizationRequest request = ValidRequest();
        if (setting == "InputPath")
        {
            request = request with
            {
                InputPath = Path.Combine(Path.GetTempPath(), "does-not-exist.vgz"),
            };
        }
        else
        {
            request = request with { OutputPath = "" };
        }
        Assert.Contains(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Code == code && issue.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void OutputOverlappingInput_IsError()
    {
        string path = Path.Combine(Path.GetTempPath(), "same-file.vgz");
        VisualizationRequest request = ValidRequest() with { OutputPath = path, InputPath = path };
        Assert.Contains(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Code == ValidationCodes.OutputOverlapsInput);
    }

    [Theory]
    [InlineData(479, 720, "Output.Width")]
    [InlineData(1280, 269, "Output.Height")]
    public void TooSmallResolution_IsError(int width, int height, string setting)
    {
        VisualizationRequest request = ValidRequest() with
        {
            Output = new OutputSettings { Width = width, Height = height },
        };
        Assert.Contains(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Code == ValidationCodes.InvalidRequest && issue.SettingPath == setting);
    }

    [Fact]
    public void NonPositiveFpsNumerator_IsError()
    {
        VisualizationRequest request = ValidRequest() with
        {
            Output = new OutputSettings { FpsNumerator = 0 },
        };
        Assert.Contains(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Code == ValidationCodes.InvalidRequest && issue.SettingPath == "Output.FpsNumerator");
    }

    [Fact]
    public void NonPositiveFpsDenominator_IsError()
    {
        VisualizationRequest request = ValidRequest() with
        {
            Output = new OutputSettings { FpsDenominator = 0 },
        };
        Assert.Contains(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Code == ValidationCodes.InvalidRequest && issue.SettingPath == "Output.FpsDenominator");
    }

    [Fact]
    public void OutOfRangePastSeconds_IsError()
    {
        VisualizationRequest request = ValidRequest() with
        {
            View = new ViewSettings { PastSeconds = 20 },
        };
        Assert.Contains(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Code == ValidationCodes.InvalidRequest && issue.SettingPath == "View.PastSeconds");
    }

    [Fact]
    public void OutOfRangeFutureSeconds_IsError()
    {
        VisualizationRequest request = ValidRequest() with
        {
            View = new ViewSettings { FutureSeconds = 16 },
        };
        Assert.Contains(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Code == ValidationCodes.InvalidRequest && issue.SettingPath == "View.FutureSeconds");
    }

    [Fact]
    public void ExcessiveTimeWindow_IsError()
    {
        VisualizationRequest request = ValidRequest() with
        {
            View = new ViewSettings { PastSeconds = 12, FutureSeconds = 12 },
        };
        Assert.Contains(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Code == ValidationCodes.InvalidRequest && issue.SettingPath == "View.PastSeconds");
    }

    [Fact]
    public void NonPositiveLoopCount_IsError()
    {
        VisualizationRequest request = ValidRequest() with
        {
            Playback = new PlaybackSettings { LoopCount = 0 },
        };
        Assert.Contains(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Code == ValidationCodes.InvalidRequest && issue.SettingPath == "Playback.LoopCount");
    }

    [Fact]
    public void NegativeFadeSeconds_IsError()
    {
        VisualizationRequest request = ValidRequest() with
        {
            Playback = new PlaybackSettings { FadeSeconds = -1 },
        };
        Assert.Contains(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Code == ValidationCodes.InvalidRequest && issue.SettingPath == "Playback.FadeSeconds");
    }

    [Fact]
    public void NegativeTailSeconds_IsError()
    {
        VisualizationRequest request = ValidRequest() with
        {
            Playback = new PlaybackSettings { TailSeconds = -0.1 },
        };
        Assert.Contains(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Code == ValidationCodes.InvalidRequest && issue.SettingPath == "Playback.TailSeconds");
    }

    [Fact]
    public void NonPositiveMaximumDuration_IsError()
    {
        VisualizationRequest request = ValidRequest() with
        {
            Playback = new PlaybackSettings { MaximumDurationSeconds = 0 },
        };
        Assert.Contains(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Code == ValidationCodes.InvalidRequest && issue.SettingPath == "Playback.MaximumDurationSeconds");
    }

    [Fact]
    public void NonPositiveSampleRate_IsError()
    {
        VisualizationRequest request = ValidRequest() with
        {
            Playback = new PlaybackSettings { SampleRate = 0 },
        };
        Assert.Contains(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Code == ValidationCodes.InvalidRequest && issue.SettingPath == "Playback.SampleRate");
    }

    [Fact]
    public void NewerSchema_IsErrorAndStops()
    {
        VisualizationRequest request = ValidRequest() with { SchemaVersion = 99 };
        IReadOnlyList<ValidationIssue> issues = VisualizationRequestValidator.Validate(request);
        Assert.Contains(issues, issue => issue.Code == ValidationCodes.UnsupportedSchema);
        // Unsupported schema short-circuits: only the schema issue is returned.
        Assert.Single(issues);
    }

    [Fact]
    public void CustomSelectionWithoutTracks_IsError()
    {
        VisualizationRequest request = ValidRequest() with
        {
            Tracks = new TrackSettings { Selection = TrackSelectionMode.Custom },
        };
        Assert.Contains(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Code == ValidationCodes.InvalidRequest
                && issue.Severity == ValidationSeverity.Error
                && issue.SettingPath == "Tracks.Selection");
    }

    [Fact]
    public void CustomSelectionWithTracks_Passes()
    {
        VisualizationRequest request = ValidRequest() with
        {
            Tracks = new TrackSettings
            {
                Selection = TrackSelectionMode.Custom,
                IncludedIds = new[] { "ym2608.0.fm.1" },
            },
        };
        Assert.DoesNotContain(
            VisualizationRequestValidator.Validate(request),
            issue => issue.SettingPath == "Tracks.Selection" && issue.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void OutputPathOutsideInputDirectory_Warns()
    {
        // Output in a *different* directory than the input and not the default
        // "<input>.visualization" dir → confirmation warning (untrusted projects).
        VisualizationRequest request = ValidRequest() with
        {
            OutputPath = Path.Combine(Path.GetTempPath(), "unrelated-dir", "out.mp4"),
        };
        Assert.Contains(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Code == ValidationCodes.OutputOverlapsInput
                && issue.Severity == ValidationSeverity.Warning
                && issue.SettingPath == nameof(VisualizationRequest.OutputPath));
    }

    [Fact]
    public void OutputPathNextToInput_NoLocationWarning()
    {
        VisualizationRequest request = ValidRequest() with
        {
            OutputPath = Path.Combine(
                Path.GetDirectoryName(ValidRequest().InputPath)!,
                "out.mp4"),
        };
        Assert.DoesNotContain(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Code == ValidationCodes.OutputOverlapsInput
                && issue.Severity == ValidationSeverity.Warning);
    }
}
