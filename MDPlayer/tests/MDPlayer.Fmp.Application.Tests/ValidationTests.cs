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
    [InlineData(479, 720, "Width")]
    [InlineData(1280, 269, "Height")]
    public void TooSmallResolution_IsError(int width, int height, string setting)
    {
        VisualizationRequest request = ValidRequest() with { Width = width, Height = height };
        Assert.Contains(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Code == ValidationCodes.InvalidRequest && issue.SettingPath == setting);
    }

    [Fact]
    public void NonPositiveFps_IsError()
    {
        VisualizationRequest request = ValidRequest() with { FpsNumerator = 0 };
        Assert.Contains(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Code == ValidationCodes.InvalidRequest && issue.SettingPath == "FpsNumerator");
    }

    [Fact]
    public void OutOfRangePastSeconds_IsError()
    {
        VisualizationRequest request = ValidRequest() with { PastSeconds = 20 };
        Assert.Contains(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Code == ValidationCodes.InvalidRequest && issue.SettingPath == "PastSeconds");
    }

    [Fact]
    public void ExcessiveScopeRatio_IsError()
    {
        VisualizationRequest request = ValidRequest() with { ScopeRatio = 0.9 };
        Assert.Contains(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Code == ValidationCodes.InvalidRequest && issue.SettingPath == "ScopeRatio");
    }

    [Fact]
    public void NonPositiveLoopCount_IsError()
    {
        VisualizationRequest request = ValidRequest() with { LoopCount = 0 };
        Assert.Contains(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Code == ValidationCodes.InvalidRequest && issue.SettingPath == "LoopCount");
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
    public void CustomSelectionWithoutTracks_IsWarning()
    {
        VisualizationRequest request = ValidRequest() with { ChannelSelection = ChannelSelectionMode.Custom };
        Assert.Contains(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Severity == ValidationSeverity.Warning
                && issue.SettingPath == nameof(VisualizationRequest.ChannelSelection));
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

    [Fact]
    public void OverlayWithoutAnalysis_IsWarning()
    {
        VisualizationRequest request = ValidRequest() with { AnalysisOverlay = AnalysisOverlayMode.Standard };
        Assert.Contains(
            VisualizationRequestValidator.Validate(request),
            issue => issue.Code == ValidationCodes.AnalysisFailed
                && issue.Severity == ValidationSeverity.Warning);
    }
}
