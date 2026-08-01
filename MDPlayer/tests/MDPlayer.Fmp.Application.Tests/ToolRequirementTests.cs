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

    [Fact]
    public void Diagnostic_RequiresCorrscope()
    {
        IReadOnlyList<ToolRequirement> requirements = ToolRequirementResolver.Resolve(Request());
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
            [ToolRoles.Corrscope] = new() { Role = ToolRoles.Corrscope, IsRequired = true, IsAvailable = true },
        };
        // Default Diagnostic request requires FFmpeg and Corrscope; both are
        // available, so the request is renderable until one of them drops.
        Assert.True(ToolRequirementResolver.AllAvailable(requirements, statuses));

        statuses[ToolRoles.Ffmpeg] = statuses[ToolRoles.Ffmpeg] with { IsAvailable = false };
        Assert.False(ToolRequirementResolver.AllAvailable(requirements, statuses));
    }
}
