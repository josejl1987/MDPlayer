using Fmp.Core.Midi;

namespace Fmp.Application.Export;

public enum MidiExportTimingMode
{
    RawSourceTime,
    MusicalTimeMap,
}

/// <summary>Options for source-faithful raw MIDI transcription.</summary>
public sealed class MidiExportRequest
{
    public static readonly MidiExportRequest Default = new();

    /// <summary>Ticks per quarter note. The raw transport is always 120 BPM.</summary>
    public int Ppq { get; init; } = MidiTranscriber.DefaultPpq;

    /// <summary>Chooses raw source transport or the inferred/validated musical time map.</summary>
    public MidiExportTimingMode TimingMode { get; init; } = MidiExportTimingMode.RawSourceTime;

    /// <summary>Optional fixed musical tempo override in beats per minute.</summary>
    public double? FixedBpm { get; init; }

    /// <summary>Optional meter override expressed as <c>numerator/denominator</c>.</summary>
    public string? Meter { get; init; }

    /// <summary>Optional signed source-sample beat-phase override.</summary>
    public long? BeatOffsetSamples { get; init; }

    /// <summary>Reject unresolved musical timing instead of emitting an inferred fallback.</summary>
    public bool StrictTiming { get; init; }

    /// <summary>Opt-in allocation/wall-clock receipt for the transcription stage.</summary>
    public bool EnablePerformanceReceipts { get; init; }

    /// <summary>Stable fixture/input label written to machine-readable receipts.</summary>
    public string PerformanceFixture { get; init; } = "application-midi-export";
}

/// <summary>The outcome of one raw MIDI transcription.</summary>
public sealed class MidiExportResult
{
    public required bool Succeeded { get; init; }
    public string? Error { get; init; }

    /// <summary>The MIDI file bytes (Format 1 SMF), when successful.</summary>
    public byte[]? Bytes { get; init; }

    /// <summary>Concise source-fidelity diagnostics.</summary>
    public IReadOnlyList<string> Report { get; init; } = Array.Empty<string>();

    public ExportPerformanceSummary? Performance { get; init; }

    /// <summary>Planner-owned tracks retained for benchmark/test inspection.</summary>
    internal IReadOnlyList<MidiTrack>? Tracks { get; init; }

    public bool Trustworthy => Succeeded && Bytes is { Length: > 0 };
}
