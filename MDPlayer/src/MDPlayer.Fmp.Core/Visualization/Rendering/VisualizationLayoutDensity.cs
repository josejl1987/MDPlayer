namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// The presentation density a resolved layout may render in, chosen by
/// <see cref="VisualizationLayoutResolver"/> from the resolved panel geometry.
/// Density degrades (rather than throwing) when the canvas cannot support the
/// preferred scopes, pitch labels or detailed headers, and stays fixed for the
/// lifetime of one resolved geometry so the overlay, Corrscope and diagnostics
/// all agree on what is actually rendered.
/// </summary>
internal enum VisualizationLayoutDensity
{
    /// <summary>Scopes, roll, pitch labels, detailed headers and instrument text.</summary>
    Full,

    /// <summary>Scopes and roll only; no pitch labels, detailed headers or instrument text.</summary>
    Compact,

    /// <summary>No scope region; at most roll (or aggregate activity when even a roll cannot fit).</summary>
    Minimal,
}

/// <summary>
/// The concrete optional visual regions enabled for a resolved layout. Callers
/// (overlay renderer, Corrscope compositor, diagnostics) must read these
/// capabilities instead of inferring them from pixel dimensions.
/// </summary>
internal sealed record VisualizationLayoutCapabilities(
    bool ShowScopes,
    bool ShowRoll,
    bool ShowPitchLabels,
    bool ShowDetailedHeaders,
    bool ShowInstrumentText);
