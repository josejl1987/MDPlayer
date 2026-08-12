#nullable enable

namespace Fmp.Core.Midi;

/// <summary>
/// Per-domain pitch-normalization report (FR-6 / D12), surfaced on
/// <see cref="MusicalMidiExportResult"/> and projected to JSON by the CLI's
/// --pitch-report flag. All cents values are TRUE cents. Every field is measured —
/// never fabricated: attacks/retriggers and the pipeline counts come from the
/// stage, residual mode / MAD / confidence from the tuning-center detector, and
/// warnings carry low-confidence domains verbatim.
/// </summary>
internal sealed class PitchNormalizationDiagnostics
{
    public List<DomainPitchStats> Domains { get; } = new();

    public List<string> Warnings { get; } = new();
}
