namespace Fmp.Core.Rendering;

/// <summary>
/// Progress snapshot reported by the scope renderer for an individual stem.
/// This contract is shared between the renderer (Core) and the CLI progress reporter.
/// </summary>
internal readonly record struct ScopeProgress
{
    /// <summary>Stem name, e.g. "ym2608-fm1".</summary>
    public string StemName { get; init; }

    /// <summary>Samples rendered so far for this stem.</summary>
    public long RenderedSamples { get; init; }

    /// <summary>Wall-clock time elapsed since this stem started rendering.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>True when the stem has finished rendering.</summary>
    public bool Completed { get; init; }
}
