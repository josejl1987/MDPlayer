using Fmp.Application.Contracts;

namespace Fmp.Application.Validation;

/// <summary>
/// Feature-dependent tool requirement calculation (spec §19.2). A pure
/// function of the request — previews that do not use a feature never require
/// its tools. Layouts that may resolve to scope content mark Corrscope as
/// required when the request explicitly asks for scopes; for Auto the planner
/// resolves the concrete layout first and re-evaluates.
/// </summary>
public static class ToolRequirementResolver
{
    /// <summary>
    /// Computes tool requirements. <paramref name="resolvedLayout"/> is the
    /// concrete layout (from the planner) when known; null defers scope
    /// decisions to "auto" semantics.
    /// </summary>
    public static IReadOnlyList<ToolRequirement> Resolve(VisualizationRequest request, string? resolvedLayout = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requirements = new List<ToolRequirement>();

        // Final video encoding always requires FFmpeg. A semantic-only still
        // preview or stems-only job does not.
        if (!request.StemsOnly)
        {
            requirements.Add(new ToolRequirement
            {
                Role = ToolRoles.Ffmpeg,
                Kind = ToolRequirementKind.Required,
                Feature = "final video",
                Reason = "FFmpeg encodes the composed raw frames into the output video.",
            });
        }

        // Scope content requires Corrscope (external scope renderer).
        bool needsScopes = request.Layout switch
        {
            VisualizationLayout.Scopes
                or VisualizationLayout.Hybrid
                or VisualizationLayout.Diagnostic
                or VisualizationLayout.LegacyDiagnostic => true,
            VisualizationLayout.Auto => resolvedLayout is "scopes" or "hybrid" or "diagnostic" or "diagnostic-v2",
            _ => false,
        };
        if (needsScopes)
        {
            requirements.Add(new ToolRequirement
            {
                Role = ToolRoles.Corrscope,
                Kind = ToolRequirementKind.Required,
                Feature = "scope wall",
                Reason = "This layout renders synchronized scope stems through Corrscope.",
            });
        }
        else if (request.Layout == VisualizationLayout.Auto && resolvedLayout is null)
        {
            requirements.Add(new ToolRequirement
            {
                Role = ToolRoles.Corrscope,
                Kind = ToolRequirementKind.Optional,
                Feature = "scope wall",
                Reason = "Auto layout may resolve to a scope composition; Corrscope is needed only then.",
            });
        }

        if (request.AnalysisEnabled)
        {
            requirements.Add(new ToolRequirement
            {
                Role = ToolRoles.AnalysisPython,
                Kind = ToolRequirementKind.Required,
                Feature = "symbolic analysis",
                Reason = "Analysis runs in a Python worker environment.",
            });
        }

        if (request.Encoder == VideoEncoder.Nvenc)
        {
            requirements.Add(new ToolRequirement
            {
                Role = ToolRoles.Nvenc,
                Kind = ToolRequirementKind.Required,
                Feature = "hardware encoding",
                Reason = "Explicit NVENC requires an FFmpeg build exposing h264_nvenc.",
            });
        }

        if (!string.IsNullOrEmpty(request.Tools.FmpComPath)
            || !string.IsNullOrEmpty(request.Tools.AssetsDir))
        {
            requirements.Add(new ToolRequirement
            {
                Role = ToolRoles.FmpCom,
                Kind = ToolRequirementKind.Required,
                Feature = "FMP capture",
                Reason = "FMP-family capture requires the FMP.COM emulator runtime asset.",
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
