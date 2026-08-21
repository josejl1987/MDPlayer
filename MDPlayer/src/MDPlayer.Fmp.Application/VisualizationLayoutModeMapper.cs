using Fmp.Application.Contracts;
using Fmp.Core.Visualization.Rendering;

namespace Fmp.Cli;

internal static class VisualizationLayoutModeMapper
{
    public static VisualizationLayoutMode FromComposition(
        CompositionKind composition) => composition switch
    {
        CompositionKind.Diagnostic => VisualizationLayoutMode.Diagnostic,
        CompositionKind.Performance => VisualizationLayoutMode.Performance,
        _ => throw new ArgumentOutOfRangeException(nameof(composition)),
    };
}
