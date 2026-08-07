#nullable enable

namespace Fmp.Core.Timing;

/// <summary>
/// The outcome of a beat-grid fit, exposed so the exporter can report — never
/// hide — how confident it is and what it had to infer. Phase and tempo carry
/// separate confidence because "BPM known" does not imply "grid known".
/// </summary>
internal sealed class TimingDiagnostics
{
    public TimingDiagnostics()
    {
        Warnings = new List<string>();
    }

    public int AnchorCount { get; set; }

    public int RejectedAnchorCount { get; set; }

    public int SegmentCount { get; set; }

    /// <summary>Maximum absolute residual of a retained anchor, in quarters.</summary>
    public double MaxResidualQuarters { get; set; }

    /// <summary>RMS residual of retained anchors, in quarters.</summary>
    public double RmsResidualQuarters { get; set; }

    /// <summary>RMS residual of retained anchors, in samples.</summary>
    public double RmsResidualSamples { get; set; }

    public TimingSource TempoSource { get; set; }

    public TimingSource PhaseSource { get; set; }

    /// <summary>True when tempo had to be estimated (not driver-validated and not user override).</summary>
    public bool TempoInferred => TempoSource is TimingSource.SymbolicInference or TimingSource.AudioInference;

    /// <summary>True when the phase (sample-zero quarter position) had to be estimated.</summary>
    public bool PhaseInferred => PhaseSource is TimingSource.SymbolicInference or TimingSource.AudioInference;

    /// <summary>True when phase is unknown and there was no override to settle it.</summary>
    public bool PhaseUnknown { get; set; }

    /// <summary>Absolute quarter position of sample zero as fitted.</summary>
    public double? SampleZeroQuarter { get; set; }

    public List<string> Warnings { get; }

    /// <summary>True when the fit should not be relied on for grid alignment.</summary>
    public bool IsTrustworthy =>
        !PhaseUnknown
        && !TempoInferred
        && !PhaseInferred
        && RmsResidualQuarters < 0.25;
}
