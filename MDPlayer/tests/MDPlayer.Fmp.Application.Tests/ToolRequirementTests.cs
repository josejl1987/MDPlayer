using Fmp.Application.Contracts;
using Fmp.Application.Validation;
using Xunit;

namespace Fmp.Application.Tests;

public class ToolRequirementTests
{
    private static VisualizationRequest Request() => TestRequests.Valid();

    [Fact]
    public void FinalVideo_AlwaysRequiresFfmpeg()
    {
        IReadOnlyList<ToolRequirement> requirements = ToolRequirementResolver.Resolve(Request());
        Assert.Contains(requirements, requirement => requirement.Role == ToolRoles.Ffmpeg
            && requirement.Kind == ToolRequirementKind.Required);
    }

    [Theory]
    [InlineData(CompositionKind.ScopeStage)]
    [InlineData(CompositionKind.Diagnostic)]
    public void ScopeCompositions_RequireCorrscope(CompositionKind composition)
    {
        IReadOnlyList<ToolRequirement> requirements = ToolRequirementResolver.Resolve(Request() with { Composition = composition });
        Assert.Contains(requirements, requirement => requirement.Role == ToolRoles.Corrscope
            && requirement.Kind == ToolRequirementKind.Required);
    }

    [Fact]
    public void Performance_WithoutSignalStrip_CorrscopeIsOptional()
    {
        IReadOnlyList<ToolRequirement> requirements = ToolRequirementResolver.Resolve(Request());
        Assert.Contains(requirements, requirement => requirement.Role == ToolRoles.Corrscope
            && requirement.Kind == ToolRequirementKind.Optional);
    }

    [Fact]
    public void Performance_WithSignalStrip_RequiresCorrscope()
    {
        VisualizationRequest request = Request() with
        {
            View = new ViewSettings { PerformanceSignalStrip = true },
        };
        IReadOnlyList<ToolRequirement> requirements = ToolRequirementResolver.Resolve(request);
        Assert.Contains(requirements, requirement => requirement.Role == ToolRoles.Corrscope
            && requirement.Kind == ToolRequirementKind.Required);
    }

    [Fact]
    public void ExplicitNvenc_RequiresNvencRole()
    {
        IReadOnlyList<ToolRequirement> requirements = ToolRequirementResolver.Resolve(
            Request() with { Output = new OutputSettings { Encoder = VideoEncoder.Nvenc } });
        Assert.Contains(requirements, requirement => requirement.Role == ToolRoles.Nvenc
            && requirement.Kind == ToolRequirementKind.Required);
    }

    [Fact]
    public void AutoEncoder_DoesNotRequireNvencRole()
    {
        IReadOnlyList<ToolRequirement> requirements = ToolRequirementResolver.Resolve(Request());
        Assert.DoesNotContain(requirements, requirement => requirement.Role == ToolRoles.Nvenc);
    }

    [Fact]
    public void AllAvailable_ChecksOnlyRequiredRoles()
    {
        IReadOnlyList<ToolRequirement> requirements = ToolRequirementResolver.Resolve(Request());
        var statuses = new Dictionary<string, ToolStatus>
        {
            [ToolRoles.Ffmpeg] = new() { Role = ToolRoles.Ffmpeg, IsRequired = true, IsAvailable = true },
        };
        // Default Performance request requires only FFmpeg.
        Assert.True(ToolRequirementResolver.AllAvailable(requirements, statuses));

        statuses[ToolRoles.Ffmpeg] = statuses[ToolRoles.Ffmpeg] with { IsAvailable = false };
        Assert.False(ToolRequirementResolver.AllAvailable(requirements, statuses));
    }
}
