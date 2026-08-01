namespace Fmp.Core.Visualization.Composition.Contracts;

/// <summary>
/// The three public publishing compositions (greenfield reset §2). No legacy
/// aliases exist; the request always carries an explicit composition and the
/// CLI never silently substitutes another one.
/// </summary>
internal enum CompositionKind
{
    /// <summary>Default audience-facing composition: a shared pitch roll.</summary>
    Performance,

    /// <summary>Waveform-focused composition: a large scope mosaic.</summary>
    ScopeStage,

    /// <summary>Technical inspection output: a semantic channel grid.</summary>
    Diagnostic,
}
