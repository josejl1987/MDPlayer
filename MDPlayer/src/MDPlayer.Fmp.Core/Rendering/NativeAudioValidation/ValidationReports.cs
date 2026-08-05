using System.Text.Json.Serialization;

namespace Fmp.Core.Rendering.NativeAudioValidation;

/// <summary>
/// Backend-independent capture summary (Workstream B) and the basis for the
/// canonical capture SHA-256 (<see cref="CaptureHasher"/>). Produced by the
/// internal validation harness only, never during ordinary production
/// rendering.
/// </summary>
internal sealed class CaptureValidationReport
{
    public required string FixtureId { get; init; }
    public required int OutputSampleRate { get; init; }

    public required int EventCount { get; init; }
    public required int OpnaWriteCount { get; init; }
    public required int Ppz8CommandCount { get; init; }
    public required int Ppz8BankCount { get; init; }

    public required ulong FinalOpnaMasterClock { get; init; }
    public required long FinalOutputFrame { get; init; }
    public required long FadeStartOutputFrame { get; init; }
    public required long FadeEndOutputFrame { get; init; }
    public required long TailEndOutputFrame { get; init; }

    public required int LoopCount { get; init; }
    public required string TerminationReason { get; init; }

    /// <summary>Canonical SHA-256 over the capture (<see cref="CaptureHasher"/>).</summary>
    public required string CaptureSha256 { get; init; }
}

/// <summary>
/// Per-replay validation report (Workstream E). Captures the zero-native-read
/// guarantee (status/IRQ counts must be 0), the driver replay event counts and
/// the produced PCM with its audio-sanity metrics. Produced by the internal
/// validation harness only.
/// </summary>
internal sealed class ReplayValidationReport
{
    public required string FixtureId { get; init; }
    public required int OutputRate { get; init; }
    public required string CaptureHash { get; init; }
    public required uint NativeAbiVersion { get; init; }
    public required int NativeOutputLatency { get; init; }

    public required int ReplayedOpnaEventCount { get; init; }
    public required int ReplayedPpz8EventCount { get; init; }

    /// <summary>Must be 0 for a compliant replay.</summary>
    public required int NativeStatusReadCount { get; init; }
    /// <summary>Must be 0 for a compliant replay.</summary>
    public required int NativeIrqReadCount { get; init; }

    public required long OutputFrameCount { get; init; }

    public required string PcmSha256 { get; init; }

    public required double PeakLeft { get; init; }
    public required double PeakRight { get; init; }
    public required double RmsLeft { get; init; }
    public required double RmsRight { get; init; }
    public required double DcMeanLeft { get; init; }
    public required double DcMeanRight { get; init; }
    public required long ClippedSampleCount { get; init; }
    public required long FirstNonZeroFrame { get; init; }
    public required long LastNonZeroFrame { get; init; }

    public required double RenderDurationSeconds { get; init; }
}

/// <summary>
/// Per-fixture validation result combining the capture and replay summaries
/// (Workstream "Validation output"). Serialized to JSON (authoritative) and
/// summarized in Markdown (human-readable). Machine-specific benchmark values
/// are reported, never asserted as golden expectations.
/// </summary>
internal sealed class FixtureValidationResult
{
    public required string FixtureId { get; init; }
    public required string CaptureHash { get; init; }
    public required int EventCount { get; init; }
    public required IReadOnlyList<string> BankHashes { get; init; }
    public required int OutputRate { get; init; }
    public required long FrameCount { get; init; }
    public required string PcmHash { get; init; }
    public required AudioSanityMetrics AudioSanity { get; init; }
    public required double CaptureDurationSeconds { get; init; }
    public required double ReplayDurationSeconds { get; init; }
    public required double TotalDurationSeconds { get; init; }
    public required long AllocatedBytes { get; init; }
    public required string Result { get; init; }
}

/// <summary>
/// Offline audio-sanity metrics (Workstream I). Computed by
/// <see cref="AudioSanityChecks"/>; reported, not thresholded globally.
/// </summary>
internal sealed class AudioSanityMetrics
{
    [JsonIgnore] public long FrameCount { get; set; }
    public double PeakLeft { get; set; }
    public double PeakRight { get; set; }
    public double RmsLeft { get; set; }
    public double RmsRight { get; set; }
    public double DcMeanLeft { get; set; }
    public double DcMeanRight { get; set; }
    public long ClippedSampleCount { get; set; }
    public double ZeroSamplePercentage { get; set; }
    public long LongestZeroRun { get; set; }
    public long FirstNonZeroFrame { get; set; }
    public long LastNonZeroFrame { get; set; }
}
