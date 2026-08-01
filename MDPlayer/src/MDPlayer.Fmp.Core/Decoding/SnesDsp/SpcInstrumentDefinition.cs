using Fmp.Core.Visualization;

namespace Fmp.Core.Decoding.SnesDsp;

/// <summary>An SPC instrument resolved from a DSP voice at key-on. Derives from the generic
/// <see cref="InstrumentDefinition"/> (Kind "spc") so it flows through TimelineBuilder and
/// VisualizationJsonWriter. IDs are stable: 'spc:src23:a1b2c3d4'.</summary>
internal sealed record SpcInstrumentDefinition(
    string Id,
    string DisplayName,
    string SampleHash,
    int SourceNumber,
    ushort StartAddress,
    ushort LoopAddress,
    bool Loops,
    byte Adsr1,
    byte Adsr2,
    byte Gain,
    double? EstimatedRootHz,
    double RootConfidence,
    string PitchAccuracy)
    : InstrumentDefinition(Id, "spc", null, null, null, null, null)
{
    public string SampleShortHash => SampleHash is { Length: >= 8 } ? SampleHash[..8] : SampleHash;

    /// <summary>Default display name: 'SRC 23 · A1B2C3D4' (source number + short sample hash).</summary>
    public static string DefaultDisplayName(int sourceNumber, string sampleHash) =>
        $"SRC {sourceNumber} · {(sampleHash is { Length: >= 8 } ? sampleHash[..8] : sampleHash).ToUpperInvariant()}";
}
