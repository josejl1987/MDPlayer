namespace Fmp.Core.Visualization;

/// <summary>
/// Translates the low-level YM2612 DAC register activity into the normalized
/// <see cref="DacOperation"/> boundary consumed by <see cref="DacPlaybackTracker"/>.
///
/// The YM2612 exposes no DAC trigger command or declared length: playback is
/// gated by the DAC enable latch (register 0x2B bit 7) and single-byte samples
/// written to register 0x2A. A session begins on the first sample written after
/// the DAC is enabled (or after idle) and ends when the DAC is disabled or the
/// stream ends. Source positions advance by one per written byte.
/// </summary>
internal sealed class Ym2612DacNormalizer
{
    /// <summary>Source id used for the implicit YM2612 DAC byte stream.</summary>
    public const int DacSourceId = 0;

    private readonly int _gapThresholdSamples;
    private bool _dacEnabled;
    private bool _playing;
    private bool _explicitSession;
    private long _cursor;
    private long _lastTimestamp = -1;

    public bool ExplicitSession => _explicitSession;

    public Ym2612DacNormalizer(int gapThresholdSamples = DefaultGapThresholdSamples)
    {
        _gapThresholdSamples = gapThresholdSamples > 0 ? gapThresholdSamples : DefaultGapThresholdSamples;
    }

    /// <summary>
    /// Feeds one chip write to the normalizer, emitting the corresponding
    /// normalized DAC operations into <paramref name="ops"/>.
    /// </summary>
    public void Process(long samplePosition, int port, int address, int value, List<DacOperation> ops, long? sourceOffset = null)
    {
        if (port != 0)
            return;

        if (address == 0x2B)
        {
            bool enabled = (value & 0x80) != 0;
            if (enabled == _dacEnabled)
                return;

            if (!enabled && _playing)
            {
                // DAC latched off: close the active session.
                ops.Add(new DacOperation.DacPlaybackStopped(samplePosition, DacStopReason.ExplicitStop));
                _playing = false;
            }
            _dacEnabled = enabled;
            _lastTimestamp = samplePosition;
            return;
        }

        if (address == 0x2A)
        {
            if (!_dacEnabled)
                return;

            if (_explicitSession)
            {
                if (!_playing)
                {
                    ops.Add(new DacOperation.DacPlaybackStarted(
                        samplePosition,
                        DacSourceId,
                        Position: 0,
                        DeclaredLength: null,
                        RateHz: null));
                    _playing = true;
                    _cursor = 0;
                }

                ops.Add(new DacOperation.DacByteConsumed(
                    samplePosition,
                    DacSourceId,
                    Position: _cursor,
                    Value: (byte)value,
                    SourceOffset: sourceOffset));
                _cursor++;
                _lastTimestamp = samplePosition;
                return;
            }

            // A long time gap between samples with no intervening write is a
            // separate burst (many tracks leave the DAC enabled across hits).
            if (_playing && _lastTimestamp >= 0 && samplePosition - _lastTimestamp > _gapThresholdSamples)
            {
                ops.Add(new DacOperation.DacPlaybackStopped(samplePosition, DacStopReason.SourceDiscontinuity));
                _playing = false;
            }

            if (!_playing)
            {
                // First byte of a session: declare the start.
                ops.Add(new DacOperation.DacPlaybackStarted(
                    samplePosition,
                    DacSourceId,
                    Position: _cursor,
                    DeclaredLength: null,
                    RateHz: null));
                _playing = true;
            }

            ops.Add(new DacOperation.DacByteConsumed(
                samplePosition,
                DacSourceId,
                Position: _cursor,
                Value: (byte)value,
                SourceOffset: sourceOffset));
            _cursor++;
            _lastTimestamp = samplePosition;
            return;
        }

        _lastTimestamp = samplePosition;
    }

    /// <summary>
    /// Starts an authoritative VGM DAC stream session. The marker is allowed
    /// to arrive while the actual YM2612 DAC latch is off; in that case no
    /// tracker operation is synthesized until a real 0x2B enable and 0x2A
    /// write occur.
    /// </summary>
    public void BeginExplicitSession(
        long samplePosition,
        byte streamId,
        double? rateHz,
        List<DacOperation> ops)
    {
        ArgumentNullException.ThrowIfNull(ops);
        if (_playing)
            ops.Add(new DacOperation.DacPlaybackStopped(samplePosition, DacStopReason.Retriggered));

        _playing = false;
        _explicitSession = true;
        _cursor = 0;
        _lastTimestamp = samplePosition;
        if (!_dacEnabled)
            return;

        ops.Add(new DacOperation.DacPlaybackStarted(
            samplePosition,
            DacSourceId,
            Position: 0,
            DeclaredLength: null,
            RateHz: rateHz));
        _playing = true;
    }

    public void EndExplicitSession(
        long samplePosition,
        DacStopReason reason,
        List<DacOperation> ops)
    {
        ArgumentNullException.ThrowIfNull(ops);
        if (_playing)
            ops.Add(new DacOperation.DacPlaybackStopped(samplePosition, reason));
        _playing = false;
        _explicitSession = false;
        _lastTimestamp = samplePosition;
    }

    public void ChangeExplicitRate(
        long samplePosition,
        double rateHz,
        List<DacOperation> ops)
    {
        ArgumentNullException.ThrowIfNull(ops);
        if (_explicitSession && _playing)
            ops.Add(new DacOperation.DacRateChanged(samplePosition, rateHz));
    }

    /// <summary>
    /// Closes any active playback at the end of the stream. Returns the
    /// terminating operation (or nothing) so the caller appends it last.
    /// </summary>
    public void Complete(long endSample, List<DacOperation> ops)
    {
        if (_playing)
        {
            _playing = false;
            _explicitSession = false;
            _dacEnabled = false;
            ops.Add(new DacOperation.DacPlaybackStopped(endSample, DacStopReason.EndOfStream));
        }
    }

    /// <summary>Default sample-gap threshold used to split DAC bursts.</summary>
    public const int DefaultGapThresholdSamples = 64;
}
