using Fmp.Application.Contracts;
using Fmp.Application.Validation;
using Xunit;

namespace Fmp.Application.Tests;

public class ToolRequirementTests
{
    private static VisualizationRequest Request() => TestRequests.Valid();

    [Fact]
    public void FinalVideo_RequiresFfmpeg()
    {
        IReadOnlyList<ToolRequirement> requirements = ToolRequirementResolver.Resolve(Request());
        Assert.Contains(requirements, requirement => requirement.Role == ToolRoles.Ffmpeg
            && requirement.Kind == ToolRequirementKind.Required);
    }

    [Fact]
    public void StemsOnly_DoesNotRequireFfmpeg()
    {
        IReadOnlyList<ToolRequirement> requirements = ToolRequirementResolver.Resolve(Request() with { StemsOnly = true });
        Assert.DoesNotContain(requirements, requirement => requirement.Role == ToolRoles.Ffmpeg);
    }

    [Theory]
    [InlineData(VisualizationLayout.Scopes)]
    [InlineData(VisualizationLayout.Hybrid)]
    [InlineData(VisualizationLayout.Diagnostic)]
    public void ScopeLayouts_RequireCorrscope(VisualizationLayout layout)
    {
        IReadOnlyList<ToolRequirement> requirements = ToolRequirementResolver.Resolve(Request() with { Layout = layout });
        Assert.Contains(requirements, requirement => requirement.Role == ToolRoles.Corrscope
            && requirement.Kind == ToolRequirementKind.Required);
    }

    [Fact]
    public void AutoLayout_WithoutResolvedLayout_CorrscopeIsOptional()
    {
        IReadOnlyList<ToolRequirement> requirements = ToolRequirementResolver.Resolve(Request());
        Assert.Contains(requirements, requirement => requirement.Role == ToolRoles.Corrscope
            && requirement.Kind == ToolRequirementKind.Optional);
    }

    [Fact]
    public void UnifiedRoll_DoesNotRequireCorrscope()
    {
        IReadOnlyList<ToolRequirement> requirements = ToolRequirementResolver.Resolve(
            Request() with { Layout = VisualizationLayout.UnifiedRoll }, resolvedLayout: "unified");
        Assert.DoesNotContain(requirements, requirement => requirement.Role == ToolRoles.Corrscope);
    }

    [Fact]
    public void AutoResolvedToUnified_DoesNotRequireCorrscope()
    {
        IReadOnlyList<ToolRequirement> requirements = ToolRequirementResolver.Resolve(
            Request(), resolvedLayout: "unified");
        Assert.DoesNotContain(requirements, requirement => requirement.Role == ToolRoles.Corrscope);
    }

    [Fact]
    public void AnalysisEnabled_RequiresPythonEnvironment()
    {
        IReadOnlyList<ToolRequirement> requirements = ToolRequirementResolver.Resolve(Request() with { AnalysisEnabled = true });
        Assert.Contains(requirements, requirement => requirement.Role == ToolRoles.AnalysisPython
            && requirement.Kind == ToolRequirementKind.Required);
    }

    [Fact]
    public void AnalysisDisabled_DoesNotRequirePythonEnvironment()
    {
        IReadOnlyList<ToolRequirement> requirements = ToolRequirementResolver.Resolve(Request());
        Assert.DoesNotContain(requirements, requirement => requirement.Role == ToolRoles.AnalysisPython);
    }

    [Fact]
    public void ExplicitNvenc_RequiresNvencRole()
    {
        IReadOnlyList<ToolRequirement> requirements = ToolRequirementResolver.Resolve(Request() with { Encoder = VideoEncoder.Nvenc });
        Assert.Contains(requirements, requirement => requirement.Role == ToolRoles.Nvenc
            && requirement.Kind == ToolRequirementKind.Required);
    }

    [Fact]
    public void AllAvailable_ChecksOnlyRequiredRoles()
    {
        IReadOnlyList<ToolRequirement> requirements = ToolRequirementResolver.Resolve(Request() with { Layout = VisualizationLayout.UnifiedRoll });
        var statuses = new Dictionary<string, ToolStatus>
        {
            [ToolRoles.Ffmpeg] = new() { Role = ToolRoles.Ffmpeg, IsRequired = true, IsAvailable = true },
        };
        Assert.True(ToolRequirementResolver.AllAvailable(requirements, statuses));

        statuses[ToolRoles.Ffmpeg] = statuses[ToolRoles.Ffmpeg] with { IsAvailable = false };
        Assert.False(ToolRequirementResolver.AllAvailable(requirements, statuses));
    }
}
