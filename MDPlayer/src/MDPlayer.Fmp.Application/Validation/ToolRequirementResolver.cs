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

        // Scope Stage always needs scope content → Corrscope. Performance needs
        // Corrscope only when the optional signal strip is enabled. Diagnostic
        // may include compact scopes when scope sources exist.
        bool needsScopes = request.Composition switch
        {
            CompositionKind.ScopeStage => true,
            CompositionKind.Diagnostic => true,
            _ => request.View.PerformanceSignalStrip,
        };
        if (needsScopes)
        {
            requirements.Add(new ToolRequirement
            {
                Role = ToolRoles.Corrscope,
                Kind = ToolRequirementKind.Required,
                Feature = "scope content",
                Reason = "This composition renders synchronized scope frames through Corrscope.",
            });
        }
        else if (request.Composition == CompositionKind.Performance)
        {
            requirements.Add(new ToolRequirement
            {
                Role = ToolRoles.Corrscope,
                Kind = ToolRequirementKind.Optional,
                Feature = "signal strip",
                Reason = "Scopes are an optional enhancement for Performance; Corrscope is needed only when the signal strip is enabled.",
            });
        }

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