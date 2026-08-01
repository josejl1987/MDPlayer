using Fmp.Core.Decoding.SnesDsp;
using Fmp.Core.Visualization;

namespace Fmp.Core.Playback.Spc;

/// <summary>
/// Completed SPC capture session. All rendering happens synchronously in
/// <see cref="SpcPlaybackBackend.Open"/> (deterministic, no background thread),
/// so <see cref="Run"/> simply marks the session complete. The master PCM is the
/// unmodified S-DSP stereo result at 32,000 Hz; the linear fade was applied by
/// the backend.
/// </summary>
internal sealed class SpcPlaybackSession : IPlaybackCaptureSession
{
    private readonly CaptureTimingInfo _timing;
    private readonly IReadOnlyList<DeviceDescriptor> _devices;
    private readonly long _totalFrames;
    private bool _complete;

    public SpcPlaybackSession(
        int sampleRate,
        long totalFrames,
        IReadOnlyList<DeviceDescriptor> devices,
        IReadOnlyList<SpcInstrumentDefinition> instruments = null,
        IReadOnlyList<SpcSampleEntry> samples = null)
    {
        _timing = new CaptureTimingInfo(sampleRate, SpcNativeSession.DefaultBlockFrames).Validate();
        _devices = devices;
        _totalFrames = totalFrames;
        Instruments = instruments ?? Array.Empty<SpcInstrumentDefinition>();
        Samples = samples ?? Array.Empty<SpcSampleEntry>();
    }

    /// <summary>
    /// PR 10: instruments resolved from the snapshot DSP voices at open time.
    /// In <see cref="SpcPitchMode.Relative"/> mode every instrument carries
    /// PitchAccuracy "relative" and a null EstimatedRootHz (BRR root estimation
    /// is skipped); in Estimate mode the PR 9 estimator runs.
    /// </summary>
    public IReadOnlyList<SpcInstrumentDefinition> Instruments { get; }

    /// <summary>PR 10: per-sample records (samples.json schema, §26.1).</summary>
    public IReadOnlyList<SpcSampleEntry> Samples { get; }

    public CaptureTimingInfo Timing => _timing;
    public IReadOnlyList<DeviceDescriptor> Devices => _devices;
    public long SamplePosition => _complete ? _totalFrames : 0;
    public bool IsComplete => _complete;

    public void Run(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _complete = true;
    }

    public void Stop()
    {
    }

    public void Dispose()
    {
    }
}
