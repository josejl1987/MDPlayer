using Fmp.Application.Contracts;

namespace Fmp.Application.Validation;

/// <summary>
/// Feature-dependent tool requirement calculation for schema 2. A pure function
/// of the request — previews that do not use a feature never require its tools.
/// </summary>
public static class ToolRequirementResolver
{
    public static IReadOnlyList<ToolRequirement> Resolve(VisualizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requirements = new List<ToolRequirement>();

        // Final video encoding always requires FFmpeg.
        requirements.Add(new ToolRequirement
        {
            Role = ToolRoles.Ffmpeg,
            Kind = ToolRequirementKind.Required,
            Feature = "final video",
            Reason = "FFmpeg encodes the composed raw frames into the output video.",
        });

        // Diagnostic is the only composition and always renders synchronized
        // scope content → Corrscope is required.
        requirements.Add(new ToolRequirement
        {
            Role = ToolRoles.Corrscope,
            Kind = ToolRequirementKind.Required,
            Feature = "scope content",
            Reason = "The diagnostic composition renders synchronized scope frames through Corrscope.",
        });

        if (request.Output.Encoder == VideoEncoder.Nvenc)
        {
            requirements.Add(new ToolRequirement
            {
                Role = ToolRoles.Nvenc,
                Kind = ToolRequirementKind.Required,
                Feature = "hardware encoding",
                Reason = "Explicit NVENC requires an FFmpeg build exposing h264_nvenc.",
            });
        }

        return requirements;
    }

    /// <summary>True when every required tool is currently available.</summary>
    public static bool AllAvailable(
        IReadOnlyList<ToolRequirement> requirements,
        IReadOnlyDictionary<string, ToolStatus> statuses)
    {
        return requirements
            .Where(requirement => requirement.Kind == ToolRequirementKind.Required)
            .All(requirement => statuses.TryGetValue(requirement.Role, out ToolStatus? status)
                && status.IsAvailable);
    }
}